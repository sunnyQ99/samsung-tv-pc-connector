using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace TvcPolicy;

internal sealed class Guard
{
    internal const string Wm = "/usr/bin/enlightenment";
    internal const string WmHash = "35cea56d50de74863fe5501fdb231ae70752295d365276c974cd21562b0df870";
    internal const string Module = "/usr/lib/enlightenment/modules/e-mod-tizen-cursormgr-tv/linux-gnueabi-armv7l-ver-autocannoli-0.20/module.so";
    internal const string ModuleHash = "7bc7b79fac86e2bd85d64153181d2190456b73c2523e6931022b709d124e5ab1";
    const uint DataOffset = 0x2441c;
    internal int WmPid { get; private set; }
    string identity = "";
    internal void Verify(bool requireRestorable = true)
    {
        var found = new List<int>();
        foreach (string proc in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(proc), out int pid) || pid < 2) continue;
            try { if (Link(proc + "/exe") == Wm && File.ReadAllText(proc + "/comm").Trim() == "enlightenment") found.Add(pid); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        Need(found.Count == 1, "Exactly one original compositor is required");
        int current = found[0]; string dir = "/proc/" + current;
        var state = ProcessIdentity(dir);
        Need(state.State is not ("Z" or "X"), "Compositor must be alive");
        if (WmPid != 0) Need(WmPid == current && identity == state.Start, "Compositor generation changed");
        WmPid = current; identity = state.Start;
        using (var exe = File.OpenRead(dir + "/exe"))
        {
            byte[] header = new byte[20];
            Need(exe.Read(header, 0, 20) == 20 && header[0] == 127 && header[1] == 'E' && header[2] == 'L' && header[3] == 'F' &&
                header[4] == 1 && header[5] == 1 && BitConverter.ToUInt16(header, 16) == 2 && BitConverter.ToUInt16(header, 18) == 40,
                "Original ELF32 little-endian ARM executable required");
            exe.Position = 0; using var sha = SHA256.Create();
            Need(Convert.ToHexString(sha.ComputeHash(exe)).Equals(WmHash, StringComparison.OrdinalIgnoreCase), "Compositor SHA256 mismatch");
        }
        using (var module = File.OpenRead(dir + "/root" + Module))
        using (var sha = SHA256.Create())
            Need(Convert.ToHexString(sha.ComputeHash(module)).Equals(ModuleHash, StringComparison.OrdinalIgnoreCase), "Cursor module SHA256 mismatch");
        var maps = File.ReadLines(dir + "/maps").Select(ParseMap).Where(m => m.Path == Module).ToArray();
        var code = maps.Where(m => m.Permissions.Contains('x')).ToArray();
        Need(maps.Length >= 2 && code.Length == 1 && code[0].Offset == 0 && code[0].Start < uint.MaxValue - DataOffset - 0x98,
            "Exact cursor module mapping required");
        uint data = (uint)code[0].Start + DataOffset;
        Need(maps.Any(m => m.Start <= data && data + 0x98 <= m.End && m.Permissions.StartsWith('r')), "Cursor module state is not readable in original mapping");
        Need(ProcessIdentity(dir).Start == identity, "Compositor identity changed during guard");
        int fd = open(dir + "/mem", 0x80000); Need(fd >= 0, "Read-only compositor memory unavailable");
        try
        {
            Need(!requireRestorable || Read(fd, data + 0x8c)[2] == 0, "defaultProduct is not zero; False could be rejected, so no lease is allowed");
            Need(!requireRestorable || BitConverter.ToInt32(Read(fd, data + 0x54), 0) == 0, "Current module force cursor state must be zero");
        }
        finally { close(fd); }
        Need(ProcessIdentity(dir).Start == identity, "Compositor generation changed after guard");
    }
    static byte[] Read(int fd, uint address)
    {
        byte[] result = new byte[4];
        Need(pread64(fd, result, (UIntPtr)4, address).ToInt64() == 4, "Read-only cursor guard was short"); return result;
    }
    internal static (string State, int Parent, string Start) ProcessIdentity(string proc)
    {
        string text = File.ReadAllText(proc + "/stat"); int end = text.LastIndexOf(')'); Need(end > 0, "Invalid stat");
        var fields = text[(end + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Need(fields.Length > 19 && ulong.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out _), "Invalid process start identity");
        return (fields[0], int.Parse(fields[1], CultureInfo.InvariantCulture), fields[19]);
    }
    internal static string Link(string path)
    {
        byte[] bytes = new byte[4096]; int count = readlink(path, bytes, (UIntPtr)4096).ToInt32();
        if (count < 0 || count >= bytes.Length) throw new IOException("Cannot verify exact executable link");
        return Encoding.UTF8.GetString(bytes, 0, count);
    }
    static Map ParseMap(string line)
    {
        var fields = line.Split(' ', 6, StringSplitOptions.RemoveEmptyEntries); Need(fields.Length >= 5, "Invalid map");
        var span = fields[0].Split('-');
        return new Map(ulong.Parse(span[0], NumberStyles.HexNumber), ulong.Parse(span[1], NumberStyles.HexNumber), fields[1],
            ulong.Parse(fields[2], NumberStyles.HexNumber), fields.Length == 6 ? fields[5].Trim() : "");
    }
    static void Need(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    record Map(ulong Start, ulong End, string Permissions, ulong Offset, string Path);
    [DllImport("libc.so.6", SetLastError = true)] static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc.so.6", SetLastError = true)] static extern IntPtr pread64(int fd, [Out] byte[] buffer, UIntPtr count, long offset);
    [DllImport("libc.so.6", SetLastError = true)] static extern IntPtr readlink([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [Out] byte[] buffer, UIntPtr count);
    [DllImport("libc.so.6")] static extern int close(int fd);
}
