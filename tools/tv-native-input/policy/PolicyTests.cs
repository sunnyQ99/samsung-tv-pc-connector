using System.Text.Json;

namespace TvcPolicy;

// No platform calls, files, processes, network, or real policy commands.
internal static class PolicyTests
{
    static int checks;
    static void Need(bool value, string name)
    {
        checks++;
        if (!value) throw new InvalidOperationException("Policy selftest " + checks + ": " + name);
    }
    internal static int Run()
    {
        RenewalTests(); CommandContractTests(); EngineTests(); ResultTests();
        Console.WriteLine(JsonSerializer.Serialize(new { ok = true, policyTests = checks, nativeCalls = 0,
            policyCommands = 0, networkCalls = 0, childProcesses = 0 }));
        return 0;
    }
    static void RenewalTests()
    {
        long time = 0; var clock = new RenewalClock(() => time);
        Need(clock.Sequence == 0 && !clock.Expired, "initially unseeded");
        time = 2999; Need(!clock.Expired && clock.Observe(1), "seed before three-second deadline");
        time = 5000; Need(clock.Observe(1) && clock.Sequence == 1, "old sequence accepted without renewal");
        time = 8998; Need(!clock.Expired, "one millisecond before lease expiry");
        time = 8999; Need(clock.Expired && !clock.Observe(2), "late new sequence cannot resurrect");
        time = 0; Need(!clock.Observe(3), "expired state is sticky after observe");

        time = 0; clock = new RenewalClock(() => time);
        time = 3000; Need(clock.Expired && !clock.Observe(1), "unseeded timeout cannot authorize");
        time = 0; clock = new RenewalClock(() => time); Need(clock.Observe(1), "first seed");
        time = 5999; Need(clock.Observe(2), "new sequence renews before expiry");
        time = 11998; Need(!clock.Expired, "renewed deadline");
        time = 11999; Need(clock.Expired, "renewed exact deadline");

        foreach (long invalid in new[] { 0L, -1L, 9007199254740992L })
        {
            time = 0; clock = new RenewalClock(() => time);
            Need(!clock.Observe(invalid), "invalid sequence rejected");
            Need(!clock.Observe(1), "invalid sequence cannot restart lease");
        }
        time = 0; clock = new RenewalClock(() => time);
        Need(clock.Observe(5) && !clock.Observe(4), "decreasing sequence cancels");
        Need(!clock.Observe(6), "decreasing sequence is terminal");
        time = 0; clock = new RenewalClock(() => time); Need(clock.Observe(1), "duration cap seed");
        for (int i = 1; i < 3600; i++) { time = i * 4000L; if (!clock.Observe(i + 1)) throw new InvalidOperationException("valid monotonic renew failed"); }
        time = 14400000; Need(clock.Expired && !clock.Observe(4000), "four-hour absolute cap");
    }
    static void CommandContractTests()
    {
        Need(VconfPolicy.Executable == "/usr/bin/buxton2ctl", "fixed ELF only");
        Need(VconfPolicy.ExecutableHash == "2d04299c5962511169f6822b80287fb7bca6403b8cf052da623028936a8b6c27", "exact measured ELF hash");
        Need(VconfPolicy.Arguments(true).SequenceEqual(new[] { "set-bool", "memory", VconfPolicy.Key, "1" }), "exact True argv");
        Need(VconfPolicy.Arguments(false).SequenceEqual(new[] { "set-bool", "memory", VconfPolicy.Key, "0" }), "exact False argv");
        Need(VconfPolicy.ParseBoolean("[memory] " + VconfPolicy.Key + " = Boolean: True\n"), "measured True syntax");
        Need(!VconfPolicy.ParseBoolean("[memory] " + VconfPolicy.Key + " = Boolean: False\n"), "measured False syntax");
        foreach (string wrong in new[] { "True", "False", VconfPolicy.Key + ", value = False (Boolean)",
            "[memory] " + VconfPolicy.Key + " = Integer: 0", "[memory] other = Boolean: False",
            "[memory] " + VconfPolicy.Key + " = Boolean: False\nextra" })
        {
            bool rejected = false; try { VconfPolicy.ParseBoolean(wrong); } catch (IOException) { rejected = true; }
            Need(rejected, "strict fixed key Boolean parse");
        }
        var command = new CommandFailure("original", "cleanup");
        Need(command.OriginalError == "original" && command.CleanupError == "cleanup" && command.Message.Contains("original") && command.Message.Contains("cleanup"), "original and cleanup errors retained");
        Need(PolicyFiles.Names.Length == 6 && PolicyFiles.Names.Distinct().Count() == 6, "bounded six owned files");
        string folder = "/run/samsung-tv-root/agent-123/native-input-123456abcdef";
        Need(PolicyFiles.PolicyArgv(folder).SequenceEqual(new[] { "/usr/bin/dotnet", folder + "/NativeInput.dll", "--policy-session", folder + "/policy-session.json" }), "fixed companion argv");
        Need(PolicyFiles.InputArgv(folder).SequenceEqual(new[] { "/usr/bin/dotnet", folder + "/NativeInput.dll", "--session", folder + "/session.json" }), "fixed parent argv");
        var session = new PolicySession(1, 40, "400", 20, "200", new string('a', 32));
        var owner = new PolicyOwner(60, "600", session.instance, 40, "400", 20, "200", PolicyFiles.PolicyArgv(folder));
        Need(PolicyFiles.Same(owner, session, folder, 60), "full receipt identity");
        Need(!PolicyFiles.Same(owner with { inputStartTime = "401" }, session, folder, 60), "reused input PID rejected");
        Need(!PolicyFiles.Same(owner with { rootStartTime = "201" }, session, folder, 60), "reused root PID rejected");
        Need(!PolicyFiles.Same(owner with { instance = new string('b', 32) }, session, folder, 60), "stale instance rejected");
        Need(!PolicyFiles.Same(owner with { argv = PolicyFiles.InputArgv(folder) }, session, folder, 60), "wrong role argv rejected");
        Need(!PolicyFiles.Same(owner, session, folder, 61), "other companion PID rejected");
    }
    static void EngineTests()
    {
        foreach (string mode in new[] { "normal", "cancel-after-commit", "enable-error-after-commit", "delayed-commit", "already-false",
            "restore-error-after-commit", "unquiesced", "readback-error", "borrowed", "borrowed-external-false", "guard-failure", "daemon-failure", "cancel-before-start" })
        {
            long now = 0; bool stop = mode == "cancel-before-start"; int daemon = 0, ready = 0; bool? guardRestorable = null;
            var engine = new PolicyEngine(); var fake = new FakePolicy(mode);
            fake.OnEnable = () => { Need(engine.RestoreRequired, mode + " marks restore before enable"); if (mode == "cancel-after-commit") stop = true; };
            int rc = engine.Run(fake, () => stop || now >= 100,
                () => { daemon++; if (mode == "daemon-failure") throw new CommandFailure("wrong executable hash", ""); },
                restorable => { guardRestorable = restorable; if (mode == "guard-failure") throw new IOException("wrong module"); },
                () => { ready++; if (mode == "borrowed-external-false") fake.Value = false; },
                ms => now += ms);
            bool borrowed = mode.StartsWith("borrowed", StringComparison.Ordinal);
            bool never = borrowed || mode is "guard-failure" or "daemon-failure" or "cancel-before-start";
            Need(fake.Calls.Count(c => c == "set:true") == (never ? 0 : 1), mode + " at most one enable");
            Need(engine.RestoreRequired == !never, mode + " owns only attempted change");
            if (mode is "daemon-failure" or "cancel-before-start")
            {
                Need(!engine.DaemonVerified && !engine.InitialRead, mode + " no verified daemon/baseline");
                Need(fake.Calls.All(c => c == "quiesce"), mode + " no unverified cleanup commands");
                Need(engine.CleanupComplete && engine.FinalValue == null && engine.Restored == null, mode + " reports no write without claiming False");
                Need(ready == 0 && daemon == (mode == "daemon-failure" ? 1 : 0), mode + " no ready");
                continue;
            }
            Need(guardRestorable == !borrowed, mode + " restoration guard only required for owned False baseline");
            if (mode == "unquiesced")
            {
                Need(rc != 0 && !engine.CleanupComplete && engine.Restored != true, "uncertain setter cannot claim restore");
                Need(!fake.Calls.Contains("set:false"), "do not race an unquiesced enable"); continue;
            }
            Need(rc == 0 && engine.CleanupComplete, mode + " cleanup verified");
            Need(fake.Calls.Last() == "get", mode + " fresh final read");
            Need(fake.Calls.Count(c => c == "set:false") <= 1, mode + " do not repeat already committed False");
            if (fake.Calls.Contains("set:false"))
                Need(fake.Calls.IndexOf("quiesce") < fake.Calls.IndexOf("set:false"), mode + " reap/stop before restoring");
            if (borrowed)
            {
                Need(engine.Mode == "preexisting-true" && engine.InitialValue && engine.Restored == null, mode + " borrowed remains unowned");
                Need(!fake.Calls.Any(c => c.StartsWith("set:")), mode + " no writes at all");
                Need(engine.FinalValue == (mode == "borrowed"), mode + " preserve observed external change");
            }
            else if (mode == "guard-failure") Need(!engine.RestoreRequired && ready == 0 && !fake.Value, "invalid guard never enables");
            else Need(engine.Restored == true && engine.FinalValue == false && !fake.Value, mode + " owned state returned False");
            if (mode == "delayed-commit") Need(fake.Calls.IndexOf("quiesce-commit") < fake.Calls.IndexOf("set:false"), "late enable completed before restore");
        }
    }
    static void ResultTests()
    {
        var valid = new PolicyResult(60, "600", new string('a', 32), 40, "400", 20, "200", PolicyFiles.PolicyArgv("/run/samsung-tv-root/agent-20/native-input-123456abcdef"),
            "owned-false-to-true", true, false, true, true, false, false, 0, "released", "", "", "");
        Need(PolicyHandle.ResultVerified(valid), "owned verified result");
        Need(!PolicyHandle.ResultVerified(valid with { finalValue = true }), "owned still True rejected");
        Need(!PolicyHandle.ResultVerified(valid with { restored = false }), "failed restore rejected");
        Need(!PolicyHandle.ResultVerified(valid with { cleanupComplete = false }), "incomplete cleanup rejected");
        Need(!PolicyHandle.ResultVerified(valid with { neverEnabled = true }), "contradictory owned receipt rejected");
        var borrowed = valid with { mode = "preexisting-true", neverEnabled = true, restoreRequired = false, restored = null, initialValue = true, finalValue = true };
        Need(PolicyHandle.ResultVerified(borrowed), "preexisting True kept");
        Need(PolicyHandle.ResultVerified(borrowed with { finalValue = false }), "external False preserved without overriding");
        var pending = borrowed with { mode = "pending", initialValue = null, finalValue = null };
        Need(PolicyHandle.ResultVerified(pending), "never-enabled failure may have unknown final value");
        Need(!PolicyHandle.ResultVerified(pending with { restoreRequired = true }), "pending ownership contradiction rejected");
        Need(!PolicyHandle.ResultVerified(pending with { restored = true }), "pending cannot pretend restoration");
    }
    sealed class FakePolicy : IPolicy
    {
        readonly string mode; bool delayed, failedRead, failedRestore;
        internal readonly List<string> Calls = new();
        internal bool Value;
        internal Action OnEnable = () => { };
        internal FakePolicy(string mode) { this.mode = mode; Value = mode.StartsWith("borrowed", StringComparison.Ordinal); }
        public bool Get(Func<bool>? cancel = null)
        {
            Calls.Add("get");
            if (mode == "readback-error" && Value && !failedRead) { failedRead = true; throw new IOException("uncertain readback"); }
            return Value;
        }
        public void Set(bool value, Func<bool>? cancel = null)
        {
            Calls.Add("set:" + value.ToString().ToLowerInvariant());
            if (value)
            {
                OnEnable();
                if (mode == "delayed-commit") { delayed = true; throw new TimeoutException("enable still running"); }
                Value = true;
                if (mode == "already-false") Value = false;
                if (mode is "enable-error-after-commit" or "unquiesced") throw new CommandFailure("uncertain committed True", "");
            }
            else
            {
                Value = false;
                if (mode == "restore-error-after-commit" && !failedRestore) { failedRestore = true; throw new CommandFailure("uncertain committed False", ""); }
            }
        }
        public bool Quiesce()
        {
            Calls.Add("quiesce");
            if (delayed) { Value = true; delayed = false; Calls.Add("quiesce-commit"); }
            return mode != "unquiesced";
        }
    }
}
