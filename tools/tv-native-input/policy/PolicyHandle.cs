using System.Diagnostics;
using System.Security.Cryptography;

namespace TvcPolicy;

// Called only from the authenticated input/Wayland loop. No timer renews this lease.
internal sealed class PolicyHandle
{
    readonly string folder;
    readonly PolicySession session;
    Process? child;
    PolicyOwner? owner;
    PolicyReady? ready;
    long sequence;
    bool released;
    internal PolicyHandle(string inputSessionPath, int rootPid, string rootStart)
    {
        folder = Path.GetDirectoryName(inputSessionPath)!;
        PolicyFiles.Folder(folder + "/policy-session.json");
        session = new PolicySession(1, Environment.ProcessId, PolicyFiles.Start(Environment.ProcessId), rootPid, rootStart,
            Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant());
    }
    internal PolicyReady Ready => ready ?? throw new IOException("Cursor policy companion is not ready");
    internal void Start(Action pulse)
    {
        PolicyFiles.ValidateSession(session, folder);
        foreach (string name in PolicyFiles.Names)
            PolicyFiles.Need(!File.Exists(Path.Combine(folder, name)) && !File.Exists(Path.Combine(folder, name + ".tmp")), "Unexpected old policy file in fresh input session");
        PolicyFiles.Write(folder, "policy-session.json", session);
        var psi = new ProcessStartInfo("/usr/bin/dotnet") { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add(folder + "/NativeInput.dll"); psi.ArgumentList.Add("--policy-session"); psi.ArgumentList.Add(folder + "/policy-session.json");
        child = new Process { StartInfo = psi };
        if (!child.Start()) throw new IOException("Cannot start cursor policy companion");
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 2500)
        {
            pulse();
            if (child.HasExited) throw new IOException("Cursor policy companion exited before identity receipt");
            if (File.Exists(Path.Combine(folder, "policy-owner.json")))
            {
                owner = PolicyFiles.Read<PolicyOwner>(folder, "policy-owner.json");
                PolicyFiles.Need(PolicyFiles.Same(owner, session, folder, child.Id) && PolicyFiles.Alive(owner.pid, owner.startTime, owner.argv), "Cursor policy owner identity mismatch");
                break;
            }
            Thread.Sleep(20);
        }
        if (owner == null) throw new TimeoutException("Cursor policy identity receipt timed out; no authorization seed was sent");
        // The constructor/start is reachable only after PC auth and mouse initialization.
        // This single initial seed is not renewed while waiting for ready.
        WriteRenewal();
        while (clock.ElapsedMilliseconds < 5500)
        {
            pulse();
            if (child.HasExited) throw new IOException("Cursor policy companion exited before ready");
            if (File.Exists(Path.Combine(folder, "policy-ready.json")))
            {
                var result = PolicyFiles.Read<PolicyReady>(folder, "policy-ready.json");
                PolicyFiles.Need(result.policyReady && result.pid == owner.pid && result.startTime == owner.startTime && result.instance == session.instance &&
                    result.mode is "owned-false-to-true" or "preexisting-true", "Cursor policy ready receipt mismatch");
                ready = result; EnsureAlive(); return;
            }
            Thread.Sleep(20);
        }
        throw new TimeoutException("Cursor policy readiness timed out");
    }
    internal void EnsureAlive()
    {
        if (released || ready == null || child == null || child.HasExited || File.Exists(Path.Combine(folder, "policy-result.json")))
            throw new IOException("Cursor policy companion is not active");
    }
    internal void RenewFromValidPcCommand()
    {
        EnsureAlive(); WriteRenewal();
    }
    void WriteRenewal()
    {
        PolicyFiles.Need(!released, "Policy release has begun; renewal forbidden");
        PolicyFiles.Write(folder, "policy-heartbeat.json", new PolicyHeartbeat(1, session.instance, checked(++sequence)));
    }
    internal PolicyStop ReleaseAndWait(Action pulse)
    {
        released = true;
        string error = ""; PolicyResult? result = null;
        if (child == null) return new(0, "", session.instance, "pending", true, true, null, "");
        try { PolicyFiles.Write(folder, "policy-release.json", new PolicyRelease(1, session.instance)); }
        catch (Exception ex) { error = ex.Message; }
        bool gone = false;
        var clock = Stopwatch.StartNew();
        try
        {
            while (clock.ElapsedMilliseconds < 7000)
            {
                pulse();
                if (child.WaitForExit(20)) { gone = true; break; }
            }
            // No Kill. A blocked/killed input parent must leave the independent restorer alive.
            if (!gone) throw new TimeoutException("Cursor policy companion is still restoring; retained for backend verification");
            if (owner == null && File.Exists(Path.Combine(folder, "policy-owner.json"))) owner = PolicyFiles.Read<PolicyOwner>(folder, "policy-owner.json");
            if (owner == null) throw new IOException("Cursor policy owner receipt unavailable; backend must confirm no authorized companion");
            PolicyFiles.Need(PolicyFiles.Same(owner, session, folder, child.Id), "Closed policy owner identity mismatch");
            result = PolicyFiles.Read<PolicyResult>(folder, "policy-result.json");
            PolicyFiles.Need(result.pid == owner.pid && result.startTime == owner.startTime && result.instance == session.instance &&
                result.inputPid == session.inputPid && result.inputStartTime == session.inputStartTime &&
                result.rootPid == session.rootPid && result.rootStartTime == session.rootStartTime, "Closed policy result identity mismatch");
            PolicyFiles.Need(ResultVerified(result), "Cursor policy restore/readback was not verified");
        }
        catch (Exception ex) { error = error.Length == 0 ? ex.Message : error + "; " + ex.Message; }
        int pid = child.Id;
        if (gone) child.Dispose();
        return new(pid, owner?.startTime ?? "", session.instance, result?.mode ?? ready?.mode ?? "pending", gone,
            gone && result != null && ResultVerified(result), result, error);
    }
    internal static bool ResultVerified(PolicyResult result) => result.cleanupComplete &&
        (result.neverEnabled && !result.restoreRequired && result.restored == null ||
         result.mode == "owned-false-to-true" && result.restoreRequired && !result.neverEnabled && result.restored == true && result.finalValue == false);
}
internal sealed record PolicyStop(int pid, string startTime, string instance, string mode, bool processGone, bool cleanupComplete, PolicyResult? result, string error);
