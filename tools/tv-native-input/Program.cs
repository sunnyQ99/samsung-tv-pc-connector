using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static class Program
{
    const string Host = "192.168.1.75";
    static readonly Stopwatch Clock = Stopwatch.StartNew();
    static readonly UTF8Encoding Utf8 = new(false, true);
    static volatile bool StopRequested, Finished;
    static long LastProgress, StopAt;
    static string Reason = "stopped";
    static int Main(string[] args)
    {
        if (args.SequenceEqual(new[] { "--selftest" })) return SelfTest();
        if (args.SequenceEqual(new[] { "--policy-selftest" })) return TvcPolicy.PolicyTests.Run();
        if (args.Length == 2 && args[0] == "--policy-session") return TvcPolicy.PolicyCompanion.Run(args[1]);
        if (!OperatingSystem.IsLinux() || IntPtr.Size != 4 || RuntimeInformation.ProcessArchitecture != Architecture.Arm)
        { Log(new { ok = false, error = "Linux ARM32 QN90C only; no native action performed" }); return 64; }
        if (args.Length != 2 || args[0] != "--session") return 64;
        using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; RequestStop("SIGTERM"); });
        using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, c => { c.Cancel = true; RequestStop("SIGINT"); });
        new Thread(() => {
            while (!Finished)
            {
                Thread.Sleep(250);
                long now = Clock.ElapsedMilliseconds;
                if (now - Interlocked.Read(ref LastProgress) > 12000 || (StopRequested && now - Interlocked.Read(ref StopAt) > 11000))
                { Log(new { stage = "deadline", cleanupComplete = false, reason = Reason }); _exit(124); }
            }
        }) { IsBackground = true, Name = "native-input-watchdog" }.Start();
        TcpClient? client = null; NetworkStream? stream = null;
        TvcPolicy.PolicyHandle? policy = null;
        int code = 1;
        try
        {
            Session session = ReadSession(args[1]);
            string parentStart = ParentStart(session.ParentPid);
            File.Delete(args[1]); // A validated owned one-use session file; no reusable credential is retained.
            client = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
            if (!client.ConnectAsync(Host, session.Port).Wait(3000)) throw new TimeoutException("PC callback connection timed out");
            stream = client.GetStream(); stream.ReadTimeout = 3000; stream.WriteTimeout = 1000;
            Send(stream, new { type = "hello", protocol = 1, token = session.Token, pid = Environment.ProcessId, startTime = ParentStart(Environment.ProcessId),
                width = 1920, height = 1080, capabilities = new[] { "move", "left-down", "left-up" } });
            using (var ack = JsonDocument.Parse(ReadLineBlocking(stream)))
                Need(ack.RootElement.GetProperty("type").GetString() == "hello" && ack.RootElement.GetProperty("ok").GetBoolean(), "PC refused session");
            NativeMouse.Logger = value => { Pulse(); Log(value); };
            NativeMouse.Initialize(); Pulse();
            if (StopRequested) throw new OperationCanceledException("Input stopped before policy authorization");
            policy = new TvcPolicy.PolicyHandle(args[1], session.ParentPid, parentStart);
            policy.Start(Pulse); Pulse();
            Send(stream, new { type = "ready", ok = true, width = 1920, height = 1080, pointerAvailable = true, buttons = 0, policy = policy.Ready });
            long lastMessage = Clock.ElapsedMilliseconds, lastParentCheck = 0, lastId = 0;
            var line = new List<byte>(1024); byte[] buffer = new byte[4096];
            while (!StopRequested)
            {
                Pulse(); policy.EnsureAlive(); NativeMouse.Pump(); long now = Clock.ElapsedMilliseconds;
                if (now - lastMessage >= 10000) { RequestStop("heartbeat-timeout"); break; }
                if (now - lastParentCheck >= 500)
                {
                    lastParentCheck = now;
                    if (ParentStart(session.ParentPid) != parentStart) { RequestStop("parent-exited"); break; }
                }
                if (!client.Client.Poll(20000, SelectMode.SelectRead)) continue;
                if (client.Available == 0) { RequestStop("EOF"); break; }
                int n = stream.Read(buffer, 0, Math.Min(buffer.Length, client.Available));
                if (n == 0) { RequestStop("EOF"); break; }
                for (int i = 0; i < n && !StopRequested; i++)
                {
                    if (buffer[i] != 10)
                    { if (line.Count >= 4096) throw new InvalidDataException("Command line too large"); line.Add(buffer[i]); continue; }
                    string text = Utf8.GetString(line.ToArray()); line.Clear();
                    Command command;
                    try { command = ParseCommand(text, lastId, NativeMouse.IsDown); }
                    catch (Exception ex) { throw new InvalidDataException("Invalid ordered input: " + ex.Message); }
                    lastId = command.Id; lastMessage = Clock.ElapsedMilliseconds;
                    if (command.Action != "stop") policy.RenewFromValidPcCommand();
                    if (command.Action == "ping")
                    { Send(stream, new { id = command.Id, ok = true, result = 0, buttons = NativeMouse.IsDown ? 1 : 0 }); continue; }
                    if (command.Action == "stop")
                    { Send(stream, new { id = command.Id, ok = true, result = 0, stopping = true, buttons = NativeMouse.IsDown ? 1 : 0 }); RequestStop("requested"); break; }
                    if (command.Action == "wheel")
                    { Send(stream, new { id = command.Id, ok = false, result = -2, error = "Wheel is not verified; unsupported", buttons = NativeMouse.IsDown ? 1 : 0 }); continue; }
                    try
                    {
                        NativeMouse.Execute(command.Action, command.X, command.Y);
                        Send(stream, new { id = command.Id, ok = true, result = 0, buttons = NativeMouse.IsDown ? 1 : 0 });
                    }
                    catch (Exception ex)
                    {
                        Send(stream, new { id = command.Id, ok = false, result = -1, error = ex.Message, buttons = NativeMouse.IsDown ? 1 : 0 });
                        RequestStop("native-input-error"); throw;
                    }
                }
            }
            code = 0;
        }
        catch (Exception ex)
        {
            Log(new { type = "error", ok = false, stage = NativeMouse.CurrentStage, error = ex.Message });
            if (stream != null) TrySend(stream, new { type = "error", ok = false, error = ex.Message });
        }
        finally
        {
            RequestStop(Reason); Pulse();
            var errors = new List<string>(NativeMouse.Cleanup());
            var policyStop = policy?.ReleaseAndWait(Pulse);
            if (policyStop != null && !policyStop.cleanupComplete) errors.Add("Cursor policy recovery pending: " + policyStop.error);
            var closed = new { type = "closed", cleanupComplete = errors.Count == 0, reason = Reason, errors, policy = policyStop };
            Log(closed); if (stream != null) TrySend(stream, closed);
            client?.Dispose(); Finished = true;
            if (errors.Count != 0) code = 2;
        }
        return code;
    }
    internal sealed record Session(int Port, string Token, int ParentPid);
    internal sealed record Command(long Id, string Action, uint X = 0, uint Y = 0);
    static Session ReadSession(string path)
    {
        var m = Regex.Match(path, @"^/run/samsung-tv-root/agent-([1-9][0-9]*)/native-input-[a-f0-9]{12}/session\.json$");
        Need(m.Success, "Session must be in the owned native-input directory");
        Need(new FileInfo(path).Length <= 4096, "Session file too large");
        using var j = JsonDocument.Parse(File.ReadAllText(path)); var r = j.RootElement;
        Need(r.GetProperty("host").GetString() == Host, "Unexpected PC callback host");
        int port = r.GetProperty("port").GetInt32(), pid = r.GetProperty("parentPid").GetInt32();
        string token = r.GetProperty("token").GetString() ?? "";
        Need(port >= 49152 && port <= 65535 && pid > 1 && pid.ToString() == m.Groups[1].Value, "Invalid callback/parent");
        Need(Regex.IsMatch(token, "^[a-f0-9]{64}$"), "Invalid one-use session credential");
        Need(r.GetProperty("width").GetInt32() == 1920 && r.GetProperty("height").GetInt32() == 1080, "Unexpected coordinate bounds");
        return new(port, token, pid);
    }
    internal static Command ParseCommand(string text, long previous, bool down)
    {
        using var j = JsonDocument.Parse(text); var r = j.RootElement;
        long id = r.GetProperty("id").GetInt64(); string action = r.GetProperty("action").GetString() ?? "";
        Need(id > previous && id <= 9007199254740991L, "ID must be increasing positive integer");
        if (action is "ping" or "stop") return new(id, action);
        Need(action is "move" or "down" or "up" or "wheel", "Unsupported action");
        uint x = r.GetProperty("x").GetUInt32(), y = r.GetProperty("y").GetUInt32();
        Need(x < 1920 && y < 1080, "Coordinate outside 1920x1080");
        int buttons = r.GetProperty("buttons").GetInt32();
        Need(buttons is 0 or 1, "Only left button is verified");
        if (action == "move") Need(buttons == (down ? 1 : 0), "Move button state mismatch");
        if (action == "down") Need(buttons == 1 && !down, "Duplicate or invalid down");
        if (action == "up") Need(buttons == 0, "Invalid up state");
        return new(id, action, x, y);
    }
    static string ParentStart(int pid)
    {
        string text = File.ReadAllText("/proc/" + pid + "/stat");
        return text[(text.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries)[19];
    }
    static string ReadLineBlocking(NetworkStream stream)
    {
        var bytes = new List<byte>(); int b;
        while ((b = stream.ReadByte()) != 10)
        { if (b < 0) throw new EndOfStreamException(); if (bytes.Count >= 4096) throw new InvalidDataException("Handshake line too large"); bytes.Add((byte)b); }
        return Utf8.GetString(bytes.ToArray());
    }
    static void Send(NetworkStream stream, object value)
    { byte[] b = Utf8.GetBytes(JsonSerializer.Serialize(value) + "\n"); stream.Write(b, 0, b.Length); }
    static void TrySend(NetworkStream stream, object value) { try { Send(stream, value); } catch { } }
    static void Pulse() => Interlocked.Exchange(ref LastProgress, Clock.ElapsedMilliseconds);
    static void RequestStop(string reason) { if (StopRequested) return; Reason = reason; Interlocked.Exchange(ref StopAt, Clock.ElapsedMilliseconds); StopRequested = true; }
    static void Need(bool ok, string error) { if (!ok) throw new InvalidOperationException(error); }
    static void Log(object value) { Console.WriteLine(JsonSerializer.Serialize(value)); Console.Out.Flush(); }
    static int SelfTest()
    {
        int passed = 0;
        Command C(long id, string action, int x, int y, int buttons, long previous, bool down) => ParseCommand(JsonSerializer.Serialize(new {id,action,x,y,buttons}), previous, down);
        Need(C(1,"move",640,360,0,0,false).X==640,"move"); passed++;
        Need(C(2,"down",1280,720,1,1,false).Action=="down","down"); passed++;
        Need(C(3,"move",1919,1079,1,2,true).Y==1079,"drag"); passed++;
        Need(C(4,"up",0,0,0,3,true).Action=="up","up"); passed++;
        foreach (Action bad in new Action[] {
            ()=>C(1,"move",0,0,0,1,false), ()=>C(2,"move",1920,0,0,1,false), ()=>C(2,"move",0,-1,0,1,false),
            ()=>C(2,"move",0,0,0,1,true), ()=>C(2,"down",0,0,1,1,true), ()=>C(2,"up",0,0,1,1,true), ()=>C(2,"move",0,0,2,1,false),
            ()=>C(2,"shell",0,0,0,1,false) })
        { bool rejected=false; try {bad();} catch {rejected=true;} Need(rejected,"negative input test"); passed++; }
        Need(ParseCommand("{\"id\":9,\"action\":\"ping\"}",8,false).Action=="ping","heartbeat"); passed++;
        Need(C(10,"wheel",0,0,0,9,false).Action=="wheel","explicit unsupported wheel response"); passed++;
        Log(new { ok=true,tests=passed,nativeCalls=0 }); return 0;
    }
    [DllImport("libc.so.6", CallingConvention=CallingConvention.Cdecl)] static extern void _exit(int code);
}
