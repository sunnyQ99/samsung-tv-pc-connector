using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TvcPolicy;

internal sealed record PolicySession(int version, int inputPid, string inputStartTime, int rootPid, string rootStartTime, string instance);
internal sealed record PolicyOwner(int pid, string startTime, string instance, int inputPid, string inputStartTime, int rootPid, string rootStartTime, string[] argv, string mode = "pending");
internal sealed record PolicyReady(int pid, string startTime, string instance, string mode, bool policyReady);
internal sealed record PolicyResult(int pid, string startTime, string instance, int inputPid, string inputStartTime, int rootPid, string rootStartTime,
    string[] argv,
    string mode, bool cleanupComplete, bool neverEnabled, bool restoreRequired, bool? restored, bool? initialValue, bool? finalValue,
    int exitCode, string reason, string error, string commandError, string cleanupError);
internal sealed record PolicyHeartbeat(int version, string instance, long sequence);
internal sealed record PolicyRelease(int version, string instance);

internal static class PolicyFiles
{
    internal static readonly string[] Names = { "policy-session.json", "policy-owner.json", "policy-heartbeat.json", "policy-ready.json", "policy-release.json", "policy-result.json" };
    internal static string Folder(string path)
    {
        var match = Regex.Match(path, @"^/run/samsung-tv-root/agent-([1-9][0-9]*)/native-input-[a-f0-9]{12}/policy-session\.json$");
        Need(match.Success && Path.GetFullPath(typeof(Program).Assembly.Location) == Path.GetDirectoryName(path) + "/NativeInput.dll", "Policy must use own NativeInput staging assembly/path");
        return Path.GetDirectoryName(path)!;
    }
    internal static T Read<T>(string folder, string name)
    {
        Need(Names.Contains(name), "Unsupported fixed policy file");
        string path = Path.Combine(folder, name);
        Need(new FileInfo(path).Length <= 8192, "Policy file exceeded bound");
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path)) ?? throw new IOException("Invalid policy JSON");
    }
    internal static void Write(string folder, string name, object value)
    {
        Need(Names.Contains(name), "Unsupported fixed policy file");
        string path = Path.Combine(folder, name), temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value), new UTF8Encoding(false));
        Need(chmod(temporary, 0x180) == 0, "Cannot restrict owned policy file");
        File.Move(temporary, path, true);
    }
    internal static string Start(int pid)
    {
        var identity = Guard.ProcessIdentity("/proc/" + pid.ToString(CultureInfo.InvariantCulture));
        Need(identity.State is not ("Z" or "X"), "Owned process is no longer alive"); return identity.Start;
    }
    internal static string[] InputArgv(string folder) => new[] { "/usr/bin/dotnet", folder + "/NativeInput.dll", "--session", folder + "/session.json" };
    internal static string[] PolicyArgv(string folder) => new[] { "/usr/bin/dotnet", folder + "/NativeInput.dll", "--policy-session", folder + "/policy-session.json" };
    internal static bool Alive(int pid, string start, string[]? argv = null)
    {
        try
        {
            if (Start(pid) != start) return false;
            string proc = "/proc/" + pid;
            if (!File.ReadLines(proc + "/status").Any(line => line.StartsWith("Uid:") && line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Skip(1).All(v => v == "0"))) return false;
            return argv == null || File.ReadAllBytes(proc + "/cmdline").SequenceEqual(Encoding.UTF8.GetBytes(string.Join('\0', argv) + '\0'));
        }
        catch { return false; }
    }
    internal static void ValidateSession(PolicySession session, string folder)
    {
        Need(session.version == 1 && session.inputPid > 1 && session.rootPid > 1 && session.inputPid != session.rootPid &&
            Regex.IsMatch(session.instance, "^[a-f0-9]{32}$") && Regex.IsMatch(session.inputStartTime, "^[0-9]+$") && Regex.IsMatch(session.rootStartTime, "^[0-9]+$"), "Invalid policy identity configuration");
        Need(folder.StartsWith("/run/samsung-tv-root/agent-" + session.rootPid + "/", StringComparison.Ordinal), "Root PID does not match staging");
        Need(Alive(session.inputPid, session.inputStartTime, InputArgv(folder)) && Alive(session.rootPid, session.rootStartTime), "Input/root policy identity is not alive");
    }
    internal static bool Same(PolicyOwner owner, PolicySession session, string folder, int pid) =>
        owner.pid == pid && owner.inputPid == session.inputPid && owner.inputStartTime == session.inputStartTime &&
        owner.rootPid == session.rootPid && owner.rootStartTime == session.rootStartTime && owner.instance == session.instance &&
        owner.argv.SequenceEqual(PolicyArgv(folder));
    internal static void Need(bool value, string message) { if (!value) throw new IOException(message); }
    [DllImport("libc.so.6", SetLastError = true)] static extern int chmod([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int mode);
}
