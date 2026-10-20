using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

// QN90C-only readback of the real WM's processed pointer position. No writes or ptrace.
// Offsets were disassembled and independently verified with two live movements;
// see diagnostics/remote-view/pointer-barrier/read-check-result.json.
internal sealed class WmPointerPosition : IDisposable
{
    const string ExpectedExe = "/usr/bin/enlightenment";
    const string ExpectedHash = "35cea56d50de74863fe5501fdb231ae70752295d365276c974cd21562b0df870";
    const uint GlobalAddress = 0x19d26c, CallbackAddress = 0xafc80, CoordinatesOffset = 0x68;
    const uint FocusedClientAddress = 0x19d2c4, CursorDataOffset = 0x2441c;
    const string CursorModule = "/usr/lib/enlightenment/modules/e-mod-tizen-cursormgr-tv/linux-gnueabi-armv7l-ver-autocannoli-0.20/module.so";
    const string CursorModuleHash = "7bc7b79fac86e2bd85d64153181d2190456b73c2523e6931022b709d124e5ab1";
    internal const int MaximumWaitMilliseconds = 150;
    int fd = -1;
    string proc = "", startTime = "";
    uint wmDataAddress, cursorDataAddress;
    readonly byte[] pointerBytes = new byte[4], coordinateBytes = new byte[8], linkBytes = new byte[4096];
    readonly byte[] stateBytes = new byte[4];

    internal static WmPointerPosition Open()
    {
        var result = new WmPointerPosition();
        try { result.Initialize(); return result; }
        catch { result.Dispose(); throw; }
    }
    void Initialize()
    {
        Need(OperatingSystem.IsLinux() && IntPtr.Size == 4 && RuntimeInformation.ProcessArchitecture == Architecture.Arm,
            "WM readback requires verified Linux ARM32");
        Need(geteuid() == 0, "WM readback requires root");
        var matches = new List<string>();
        foreach (string candidate in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(candidate), out int pid) || pid <= 1) continue;
            try { if (Link(candidate + "/exe") == ExpectedExe && File.ReadAllText(candidate + "/comm").Trim() == "enlightenment") matches.Add(candidate); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        Need(matches.Count == 1, "Exactly one real enlightenment process is required");
        proc = matches[0]; startTime = ProcessStartTime(File.ReadAllText(proc + "/stat"));
        using (var executable = File.OpenRead(proc + "/exe"))
        using (var sha = SHA256.Create())
            Need(Convert.ToHexString(sha.ComputeHash(executable)).Equals(ExpectedHash, StringComparison.OrdinalIgnoreCase),
                "Running WM executable hash mismatch");
        var maps = File.ReadLines(proc + "/maps").Select(ParseMap).ToArray();
        Need(maps.Any(m => Contains(m, CallbackAddress, 4) && m.Permissions.Contains('x') && m.Path == ExpectedExe), "WM fixed code mapping mismatch");
        Need(maps.Any(m => Contains(m, GlobalAddress, 4) && m.Permissions.StartsWith('r')), "WM global is not readable");
        Need(maps.Any(m => Contains(m, FocusedClientAddress, 4) && m.Permissions.StartsWith('r')), "WM focus is not readable");
        var moduleCode = maps.Where(m => m.Path == CursorModule && m.Permissions.Contains('x')).ToArray();
        Need(moduleCode.Length == 1 && moduleCode[0].Start <= uint.MaxValue - CursorDataOffset - 0x78,
            "Unique original cursor module code mapping required");
        string moduleLine = File.ReadLines(proc + "/maps").Single(line => line.EndsWith(CursorModule) &&
            line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1].Contains('x'));
        Need(moduleLine.Split(' ', StringSplitOptions.RemoveEmptyEntries)[2] == "00000000", "Cursor module load offset mismatch");
        using (var module = File.OpenRead(proc + "/root" + CursorModule))
        using (var sha = SHA256.Create())
            Need(Convert.ToHexString(sha.ComputeHash(module)).Equals(CursorModuleHash, StringComparison.OrdinalIgnoreCase),
                "Cursor module hash mismatch");
        cursorDataAddress = (uint)moduleCode[0].Start + CursorDataOffset;
        Need(maps.Any(m => m.Path == CursorModule && Contains(m, cursorDataAddress, 0x78) && m.Permissions.StartsWith('r')),
            "Original cursor state mapping missing");
        VerifyIdentity();
        fd = open(proc + "/mem", 0x80000); // O_RDONLY (0) | O_CLOEXEC; never open for writing.
        Need(fd >= 0, "Cannot open read-only WM position; errno=" + Marshal.GetLastPInvokeError());
        ReadExact(GlobalAddress, pointerBytes);
        wmDataAddress = BitConverter.ToUInt32(pointerBytes, 0);
        Need(wmDataAddress != 0 && (wmDataAddress & 3) == 0 && wmDataAddress <= uint.MaxValue - CoordinatesOffset - 8, "Invalid WM pointer");
        Need(maps.Any(m => Contains(m, wmDataAddress + CoordinatesOffset, 8) && m.Permissions.StartsWith('r')), "WM position is not readable");
        Read(); VerifyIdentity();
    }
    internal void VerifyIdentity()
    {
        Need(Link(proc + "/exe") == ExpectedExe && ProcessStartTime(File.ReadAllText(proc + "/stat")) == startTime,
            "WM process identity changed; input refused");
    }
    internal double MoveAndWaitFor(uint x, uint y, Action move, Action pump)
    {
        MotionState before = ReadMotionState();
        ValidateMotionState(before, before);
        var clock = Stopwatch.StartNew();
        move();
        double elapsed = PollUntil(x, y, before, ReadMotionState, pump, move, clock);
        VerifyIdentity();
        return elapsed;
    }
    MotionState ReadMotionState()
    {
        uint focus = ReadWord(FocusedClientAddress), client = ReadWord(cursorDataAddress + 0x2c);
        ReadExact(cursorDataAddress + 4, stateBytes); byte available = stateBytes[1];
        ReadExact(cursorDataAddress + 8, stateBytes); byte notHidden = stateBytes[0];
        ReadExact(cursorDataAddress + 0x74, stateBytes); byte pending = stateBytes[0], transition = stateBytes[1];
        Need(available <= 1 && notHidden <= 1 && pending <= 1 && transition <= 1, "Unexpected cursor state values");
        Position position = Read();
        Need(focus == ReadWord(FocusedClientAddress) && client == ReadWord(cursorDataAddress + 0x2c),
            "WM focus changed during pointer read; input refused");
        return new(position, available != 0, notHidden != 0, pending != 0, transition != 0, focus, client);
    }
    uint ReadWord(uint address) { ReadExact(address, stateBytes); return BitConverter.ToUInt32(stateBytes, 0); }
    Position Read()
    {
        Need(fd >= 0, "WM position reader is closed");
        ReadExact(GlobalAddress, pointerBytes);
        Need(BitConverter.ToUInt32(pointerBytes, 0) == wmDataAddress, "WM data pointer changed; input refused");
        ReadExact(wmDataAddress + CoordinatesOffset, coordinateBytes);
        ReadExact(GlobalAddress, pointerBytes);
        Need(BitConverter.ToUInt32(pointerBytes, 0) == wmDataAddress, "WM data pointer changed during read");
        var position = new Position(BitConverter.ToInt32(coordinateBytes, 0), BitConverter.ToInt32(coordinateBytes, 4));
        Need(position.RawX >= 0 && position.RawX <= 1920 * 256 && position.RawY >= 0 && position.RawY <= 1080 * 256,
            "WM pointer position outside verified bounds");
        return position;
    }
    internal static double PollUntil(uint x, uint y, MotionState before, Func<MotionState> read,
        Action pump, Action resendMove, Stopwatch? existingClock = null)
    {
        Need(x < 1920 && y < 1080, "Requested pointer position outside verified bounds");
        ValidateMotionState(before, before);
        var clock = existingClock ?? Stopwatch.StartNew();
        Position? first = null, last = null;
        int readCount = 0;
        bool resendUsed = false, centerObserved = false;
        // Exact module: CA1/pending1/CN0 MOVE queues a center+1, then center warp.
        // Live QN90C full-screen evidence proves (960,540). Do not generalize to
        // other geometries or accept the original target before that queued warp.
        bool awaitCenterWarp = before.PendingWarp && !before.CursorNotHiddenConfig;
        void CheckDeadline(string reason)
        {
            if (clock.ElapsedMilliseconds < MaximumWaitMilliseconds) return;
            string Describe(Position? value) => value is Position p
                ? FormattableString.Invariant($"({p.RawX / 256.0:0.###},{p.RawY / 256.0:0.###};raw={p.RawX},{p.RawY})") : "none";
            throw new InvalidOperationException(FormattableString.Invariant(
                $"{reason}; button action refused; target=({x},{y}); first={Describe(first)}; last={Describe(last)}; elapsedMs={clock.Elapsed.TotalMilliseconds:0.###}; readCount={readCount}; pendingBefore={before.PendingWarp}; centerObserved={centerObserved}; moveResent={resendUsed}"));
        }
        while (true)
        {
            CheckDeadline("WM pointer did not reach requested position within 150 ms");
            MotionState state = read();
            ValidateMotionState(before, state);
            Position actual = state.Position;
            first ??= actual; last = actual; readCount++;
            CheckDeadline("WM pointer read exceeded 150 ms");
            if (awaitCenterWarp && !state.PendingWarp && actual.RawX == 960 * 256 && actual.RawY == 540 * 256)
                centerObserved = true;
            if (awaitCenterWarp && centerObserved && !resendUsed && (x != 960 || y != 540))
            {
                // Only a MOVE is repeated, never a button. Same total deadline.
                resendUsed = true;
                resendMove();
                CheckDeadline("WM pointer retry exceeded 150 ms");
                continue;
            }
            if ((!awaitCenterWarp || centerObserved) && !state.PendingWarp &&
                actual.RawX == x * 256 && actual.RawY == y * 256) return clock.Elapsed.TotalMilliseconds;
            pump();
            Thread.Sleep(2);
        }
    }
    internal static void ValidateMotionState(MotionState before, MotionState actual)
    {
        Need(before.FocusedClient != 0 && before.CursorClient != 0 &&
            actual.FocusedClient == before.FocusedClient && actual.CursorClient == before.CursorClient,
            "WM focus/client changed; button action refused");
        Need(actual.CursorAvailable && !actual.TransitionBlocked, "WM cursor is unavailable or in transition; button action refused");
        Need(actual.CursorNotHiddenConfig == before.CursorNotHiddenConfig, "WM cursor configuration changed; button action refused");
        Need(before.PendingWarp || !actual.PendingWarp, "WM scheduled a new cursor warp; button action refused");
    }
    void ReadExact(uint address, byte[] bytes)
    {
        Need(bytes.Length is 4 or 8, "WM read size must be four or eight bytes");
        Need(pread64(fd, bytes, (UIntPtr)(uint)bytes.Length, address).ToInt64() == bytes.Length,
            "Read-only WM position read failed; errno=" + Marshal.GetLastPInvokeError());
    }
    string Link(string path)
    {
        int count = readlink(path, linkBytes, (UIntPtr)(uint)linkBytes.Length).ToInt32();
        if (count < 0) throw new IOException("Cannot verify WM executable; errno=" + Marshal.GetLastPInvokeError());
        Need(count < linkBytes.Length, "Truncated WM executable path");
        return Encoding.UTF8.GetString(linkBytes, 0, count);
    }
    internal static string ProcessStartTime(string stat)
    {
        int end = stat.LastIndexOf(')'); Need(end > 0, "Invalid WM process stat");
        var fields = stat[(end + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Need(fields.Length > 19 && fields[0] != "Z" && ulong.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out _),
            "WM process is gone or invalid");
        return fields[19];
    }
    internal static Map ParseMap(string line)
    {
        var p = line.Split(' ', 6, StringSplitOptions.RemoveEmptyEntries); Need(p.Length >= 5, "Invalid WM maps entry");
        var range = p[0].Split('-'); Need(range.Length == 2, "Invalid WM maps range");
        return new(ulong.Parse(range[0], NumberStyles.HexNumber), ulong.Parse(range[1], NumberStyles.HexNumber), p[1], p.Length == 6 ? p[5].Trim() : "");
    }
    internal static bool Contains(Map map, uint address, uint length) => map.Start <= address && (ulong)address + length <= map.End;
    public void Dispose() { if (fd >= 0) { int old = fd; fd = -1; close(old); } }
    static void Need(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    internal readonly record struct Position(int RawX, int RawY);
    internal readonly record struct MotionState(Position Position, bool CursorAvailable, bool CursorNotHiddenConfig,
        bool PendingWarp, bool TransitionBlocked, uint FocusedClient, uint CursorClient);
    internal sealed record Map(ulong Start, ulong End, string Permissions, string Path);
    [DllImport("libc.so.6", SetLastError = true)] static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc.so.6", SetLastError = true)] static extern IntPtr pread64(int fd, [Out] byte[] buffer, UIntPtr count, long offset);
    [DllImport("libc.so.6", SetLastError = true)] static extern IntPtr readlink([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [Out] byte[] buffer, UIntPtr count);
    [DllImport("libc.so.6")] static extern int close(int fd);
    [DllImport("libc.so.6")] static extern uint geteuid();
}
