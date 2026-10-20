using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TvcPolicy;

internal sealed class VconfPolicy : IPolicy, IDisposable
{
    internal const string Key = "memory/window_system/input/force_enable_cursor";
    internal const string Executable = "/usr/bin/buxton2ctl";
    internal const string ExecutableHash = "2d04299c5962511169f6822b80287fb7bca6403b8cf052da623028936a8b6c27";
    Process? child;
    string[] childArgs = Array.Empty<string>();
    string childStart = "";
    Task<string>? stdout, stderr;
    Stopwatch? cleanup;
    string lastCleanupError = "";
    const int CommandMs = 650, KillWaitMs = 300, CleanupMs = 3500;

    public bool Get(Func<bool>? cancel = null)
    {
        string result = Run(new[] { "get", "memory", Key }, cancel);
        return ParseBoolean(result);
    }
    public void Set(bool value, Func<bool>? cancel = null) => Run(Arguments(value), cancel);
    internal static string[] Arguments(bool value) => new[] { "set-bool", "memory", Key, value ? "1" : "0" };
    internal void VerifyDaemon()
    {
        using var file = File.OpenRead(Executable);
        byte[] header = new byte[20];
        if (file.Read(header, 0, 20) != 20 || header[0] != 127 || header[1] != 'E' || header[2] != 'L' || header[3] != 'F' ||
            header[4] != 1 || header[5] != 1 || BitConverter.ToUInt16(header, 16) != 3 || BitConverter.ToUInt16(header, 18) != 40)
            throw new IOException("Expected original buxton2ctl ELF32 little-endian ARM PIE");
        file.Position = 0; using var sha = SHA256.Create();
        if (!Convert.ToHexString(sha.ComputeHash(file)).Equals(ExecutableHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Original buxton2ctl executable SHA256 mismatch");
        if (Run(new[] { "check" }, null).Trim() != "Buxton service is available")
            throw new IOException("Normal Buxton daemon unavailable; direct mode is forbidden");
    }
    internal static bool ParseBoolean(string text)
    {
        string line = text.Trim();
        if (line == "[memory] " + Key + " = Boolean: True") return true;
        if (line == "[memory] " + Key + " = Boolean: False") return false;
        throw new IOException("Fixed key readback did not match one exact Boolean result");
    }
    string Run(string[] args, Func<bool>? cancel)
    {
        if (child != null && !FinishOrStopChild()) throw new CommandFailure("Prior owned buxton2ctl has not exited", lastCleanupError);
        if (cancel?.Invoke() == true) throw new OperationCanceledException("Command cancelled before process creation");
        int budget = cleanup == null ? CommandMs : Math.Min(CommandMs, Math.Max(0, CleanupMs - (int)cleanup.ElapsedMilliseconds));
        if (budget <= 0) throw new TimeoutException("Bounded restore window exhausted");
        var psi = new ProcessStartInfo(Executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (string arg in args) psi.ArgumentList.Add(arg);
        var process = new Process { StartInfo = psi };
        if (!process.Start()) { process.Dispose(); throw new IOException("Cannot start fixed buxton2ctl"); }
        child = process; childArgs = args; childStart = "";
        stdout = Drain(process.StandardOutput); stderr = Drain(process.StandardError);
        var clock = Stopwatch.StartNew();
        Exception? originalError = null; string? output = null;
        try
        {
            if (!process.HasExited)
            {
                try { CheckChildIdentity(); }
                catch when (process.HasExited) { } // A short fixed get may exit during /proc reads.
            }
            while (!process.WaitForExit(10))
            {
                if (cancel?.Invoke() == true) throw new OperationCanceledException("In-flight fixed command cancelled");
                if (clock.ElapsedMilliseconds >= budget) throw new TimeoutException("Fixed buxton2ctl command timed out");
            }
            if (!Task.WaitAll(new Task[] { stdout, stderr }, 150)) throw new IOException("Fixed command output did not finish");
            output = stdout.GetAwaiter().GetResult();
            string error = stderr.GetAwaiter().GetResult();
            if (process.ExitCode != 0) throw new IOException("Fixed buxton2ctl returned exit code " + process.ExitCode);
            if (!string.IsNullOrWhiteSpace(error)) throw new IOException("Fixed buxton2ctl reported an error");
        }
        catch (Exception error) { originalError = error; }
        // Even timeout/cancel may have already committed True. Reap first; Lease restores next.
        // Preserve the original exception instead of replacing it with a finally/identity failure.
        bool stopped = FinishOrStopChild();
        if (originalError != null || !stopped)
            throw new CommandFailure(originalError?.Message ?? "", stopped ? "" : lastCleanupError);
        return output!;
    }
    public bool Quiesce()
    {
        cleanup ??= Stopwatch.StartNew();
        return FinishOrStopChild();
    }
    bool FinishOrStopChild()
    {
        lastCleanupError = "";
        if (child == null) return true;
        try
        {
            if (!child.HasExited)
            {
                CheckChildIdentity();
                child.Kill(entireProcessTree: false);
                if (!child.WaitForExit(KillWaitMs)) { lastCleanupError = "Verified owned buxton2ctl did not exit after termination"; return false; }
            }
            if (!child.HasExited) { lastCleanupError = "Owned buxton2ctl exit not confirmed"; return false; }
            child.Dispose(); child = null; stdout = stderr = null; childStart = "";
            return true;
        }
        catch (Exception error)
        {
            lastCleanupError = error.Message;
            try { if (child?.HasExited == true) { child.Dispose(); child = null; lastCleanupError = ""; return true; } } catch { }
            return false;
        }
    }
    void CheckChildIdentity()
    {
        if (child == null) throw new InvalidOperationException("Missing owned child");
        string proc = "/proc/" + child.Id.ToString(CultureInfo.InvariantCulture);
        var identity = Guard.ProcessIdentity(proc);
        if (identity.Parent != Environment.ProcessId || identity.State is "Z" or "X") throw new IOException("Owned child parent/liveness identity mismatch");
        if (childStart.Length == 0) childStart = identity.Start;
        if (identity.Start != childStart || Guard.Link(proc + "/exe") != Executable) throw new IOException("Owned child executable/start identity mismatch");
        byte[] expected = Encoding.UTF8.GetBytes(string.Join('\0', new[] { Executable }.Concat(childArgs)) + '\0');
        if (!File.ReadAllBytes(proc + "/cmdline").SequenceEqual(expected)) throw new IOException("Owned child fixed argv identity mismatch");
    }
    static async Task<string> Drain(StreamReader reader)
    {
        var result = new StringBuilder(); char[] block = new char[512]; int count;
        while ((count = await reader.ReadAsync(block, 0, block.Length).ConfigureAwait(false)) > 0)
        {
            if (result.Length + count > 8192) throw new IOException("Fixed command output exceeded limit");
            result.Append(block, 0, count);
        }
        return result.ToString();
    }
    public void Dispose() => FinishOrStopChild();
}

internal sealed class CommandFailure : IOException
{
    internal string OriginalError { get; }
    internal string CleanupError { get; }
    internal CommandFailure(string original, string cleanup)
        : base("commandError=" + original + (cleanup.Length == 0 ? "" : "; cleanupError=" + cleanup))
    { OriginalError = original; CleanupError = cleanup; }
}
