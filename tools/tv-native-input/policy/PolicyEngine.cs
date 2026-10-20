using System.Diagnostics;

namespace TvcPolicy;

internal interface IPolicy
{
    bool Get(Func<bool>? cancel = null);
    void Set(bool value, Func<bool>? cancel = null);
    bool Quiesce();
}

// Pure single-writer engine; companion owns it, never the input/Wayland process.
internal sealed class PolicyEngine
{
    internal bool InitialValue, RestoreRequired, CleanupComplete;
    internal bool DaemonVerified, InitialRead;
    internal bool? FinalValue, Restored;
    internal string Mode = "pending", Error = "", CommandError = "", CleanupError = "";
    internal int Run(IPolicy policy, Func<bool> cancelled, Action verifyDaemon, Action<bool> verifyGuard,
        Action ready, Action<int>? sleep = null)
    {
        sleep ??= Thread.Sleep;
        void Check() { if (cancelled()) throw new OperationCanceledException("Policy authorization ended"); }
        try
        {
            Check(); verifyDaemon(); DaemonVerified = true; Check(); InitialValue = policy.Get(cancelled); InitialRead = true;
            verifyGuard(!InitialValue); Check();
            if (!InitialValue)
            {
                Mode = "owned-false-to-true";
                RestoreRequired = true; // Before any possibly committed True, including failures.
                policy.Set(true, cancelled); Check();
                if (!policy.Get(cancelled)) throw new IOException("True policy did not read back True");
            }
            else Mode = "preexisting-true"; // Borrowed state: never write True or False.
            Check(); ready();
            while (!cancelled()) sleep(25);
        }
        catch (Exception error) { Record(error); }
        finally
        {
            for (int attempt = 0; attempt < 2 && !CleanupComplete; attempt++)
            {
                try
                {
                    if (!policy.Quiesce()) throw new IOException("Own command exit not confirmed before restoration");
                    if (!DaemonVerified || !InitialRead)
                    { CleanupComplete = !RestoreRequired; break; } // Never execute an unverified command during cleanup.
                    bool current = policy.Get();
                    if (RestoreRequired && current) policy.Set(false);
                    if (!policy.Quiesce()) throw new IOException("Own restore command exit not confirmed");
                    FinalValue = policy.Get(); // Always fresh, including an earlier observed False.
                    Restored = RestoreRequired ? !FinalValue.Value : null;
                    CleanupComplete = !RestoreRequired || Restored == true;
                    if (!CleanupComplete) throw new IOException("Final owned cursor policy is not False");
                }
                catch (Exception error) { Record(error); }
            }
        }
        return CleanupComplete ? 0 : 1;
    }
    void Record(Exception error)
    {
        Error = Error.Length == 0 ? error.Message : Error + "; " + error.Message;
        if (error is CommandFailure failure)
        {
            if (CommandError.Length == 0) CommandError = failure.OriginalError;
            if (CleanupError.Length == 0) CleanupError = failure.CleanupError;
        }
    }
}

// Only a newly observed sequence from the authenticated input loop extends the lease.
internal sealed class RenewalClock
{
    readonly Func<long> now;
    readonly long created;
    long latest, sequence;
    bool expired;
    internal RenewalClock(Func<long>? now = null)
    {
        this.now = now ?? (() => (long)(Stopwatch.GetTimestamp() * (1000.0 / Stopwatch.Frequency)));
        created = latest = this.now();
    }
    internal long Sequence => sequence;
    internal bool Expired => expired || now() - created >= 14400000 ||
        (sequence == 0 ? now() - created >= 3000 : now() - latest >= 6000);
    internal bool Observe(long next)
    {
        if (Expired) { expired = true; return false; }
        if (next < sequence || next < 1 || next > 9007199254740991L) { expired = true; return false; }
        if (next > sequence) { sequence = next; latest = now(); }
        return true;
    }
}
