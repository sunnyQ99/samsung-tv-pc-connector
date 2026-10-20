using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TvcPolicy;

internal static class PolicyCompanion
{
    static volatile bool stopping;
    internal static int Run(string sessionPath)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.Arm || IntPtr.Size != 4 || geteuid() != 0) return 64;
        int heldLock = -1; string folder = ""; PolicySession? session = null; PolicyOwner? owner = null;
        var engine = new PolicyEngine(); string reason = "startup"; int code = 1;
        try
        {
            PolicyFiles.Need(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LD_PRELOAD")) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LD_LIBRARY_PATH")), "Loader overrides forbidden");
            folder = PolicyFiles.Folder(sessionPath);
            session = PolicyFiles.Read<PolicySession>(folder, "policy-session.json"); PolicyFiles.ValidateSession(session, folder);
            PolicyFiles.Need(Guard.ProcessIdentity("/proc/" + Environment.ProcessId).Parent == session.inputPid, "Companion must be a direct child of its verified input process");
            PolicyFiles.Need(setsid() >= 0, "Cannot isolate companion session from input process group");
            owner = new PolicyOwner(Environment.ProcessId, PolicyFiles.Start(Environment.ProcessId), session.instance,
                session.inputPid, session.inputStartTime, session.rootPid, session.rootStartTime, PolicyFiles.PolicyArgv(folder));
            // Identity receipt MUST precede any possibility of changing policy.
            PolicyFiles.Write(folder, "policy-owner.json", owner);
            heldLock = open("/run/samsung-tv-root/cursor-policy-lease.lock", 2 | 0x40 | 0x80000 | 0x20000, 0x180);
            PolicyFiles.Need(heldLock >= 0 && flock(heldLock, 2 | 4) == 0, "Another cursor policy lease owns the global lock");
            using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; stopping = true; });
            using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, context => { context.Cancel = true; stopping = true; });
            var renewal = new RenewalClock();
            bool Cancelled()
            {
                if (stopping) { reason = "signal"; return true; }
                if (!PolicyFiles.Alive(session.inputPid, session.inputStartTime, PolicyFiles.InputArgv(folder)) || !PolicyFiles.Alive(session.rootPid, session.rootStartTime))
                { reason = "parent-identity-ended"; return true; }
                try
                {
                    if (File.Exists(Path.Combine(folder, "policy-release.json")))
                    {
                        var release = PolicyFiles.Read<PolicyRelease>(folder, "policy-release.json");
                        PolicyFiles.Need(release.version == 1 && release.instance == session.instance, "Invalid policy release identity");
                        reason = "released"; return true;
                    }
                    if (File.Exists(Path.Combine(folder, "policy-heartbeat.json")))
                    {
                        var heartbeat = PolicyFiles.Read<PolicyHeartbeat>(folder, "policy-heartbeat.json");
                        PolicyFiles.Need(heartbeat.version == 1 && heartbeat.instance == session.instance, "Invalid policy heartbeat identity");
                        if (!renewal.Observe(heartbeat.sequence)) { reason = "heartbeat-expired-or-invalid"; return true; }
                    }
                    if (renewal.Expired) { reason = "heartbeat-expired"; return true; }
                    return false;
                }
                catch { reason = "policy-control-file-invalid"; return true; }
            }
            // No seed, no mutation. Only the authenticated input loop can authorize/renew.
            while (renewal.Sequence == 0 && !Cancelled()) Thread.Sleep(20);
            if (renewal.Sequence == 0 || Cancelled()) throw new OperationCanceledException("No live authenticated input authorization");
            using var policy = new VconfPolicy(); var guard = new Guard();
            code = engine.Run(policy, Cancelled, policy.VerifyDaemon, guard.Verify,
                () => PolicyFiles.Write(folder, "policy-ready.json", new PolicyReady(owner.pid, owner.startTime, owner.instance, engine.Mode, true)));
        }
        catch (Exception error)
        {
            engine.Error = engine.Error.Length == 0 ? error.Message : engine.Error + "; " + error.Message;
            // Before engine started, no True is reachable. Never call an unverified CLI here.
            if (!engine.RestoreRequired) engine.CleanupComplete = true;
        }
        finally
        {
            if (session != null && owner != null)
            {
                var result = new PolicyResult(owner.pid, owner.startTime, owner.instance, session.inputPid, session.inputStartTime, session.rootPid, session.rootStartTime, owner.argv,
                    engine.Mode, engine.CleanupComplete, !engine.RestoreRequired, engine.RestoreRequired, engine.Restored,
                    engine.InitialRead ? engine.InitialValue : null, engine.FinalValue, code, reason, engine.Error, engine.CommandError, engine.CleanupError);
                try { PolicyFiles.Write(folder, "policy-result.json", result); } catch { }
            }
            if (heldLock >= 0) close(heldLock);
        }
        return code;
    }
    [DllImport("libc.so.6")] static extern uint geteuid();
    [DllImport("libc.so.6")] static extern int setsid();
    [DllImport("libc.so.6", SetLastError = true)] static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, int mode);
    [DllImport("libc.so.6", SetLastError = true)] static extern int flock(int fd, int operation);
    [DllImport("libc.so.6")] static extern int close(int fd);
}
