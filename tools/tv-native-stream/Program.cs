using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

// Original QN90C CLEAR/UNMUTE path, verified separately against moving app UI and Samsung HOME.
internal static partial class Program
{
    const string Display = "/usr/lib/gstreamer-1.0/libgstdisplayencodesrc.so";
    const string Sm = "/usr/lib/gstreamer-1.0/libgstsmsrcvideoenc.so";
    const string Hal = "/usr/lib/libvideo-encoder-impl.so";
    const string Facade = "/usr/lib/libvideo-encoder.so";
    const string Gst = "libgstreamer-1.0.so.0";
    const string MiniKey = "memory/menu/network/screenmirroring/tv2m_minimap_share";
    const string ReceiverAddress = "192.168.1.75";
    const int MaximumAu = 1024 * 1024;
    const uint EncoderEvent = 0x08000001;
    static readonly Stopwatch Clock = Stopwatch.StartNew();
    static readonly List<string> CleanupErrors = new();
    static readonly object LogLock = new();
    static string Stage = "entry";
    static string? StopReason;
    static volatile bool Finished, StopRequested;
    static long LastMainProgressMs, LastHeartbeatMs;
    static Socket? Peer;
    static FileStream? CaptureLock;
    static string? SessionDirectory;

    static int Main(string[] args)
    {
        if (args.SequenceEqual(new[] { "--layout-selftest" })) return SelfTest();
        if (args.SequenceEqual(new[] { "--protocol-selftest" })) return ProtocolSelfTest();
        if (!OperatingSystem.IsLinux() || IntPtr.Size != 4 || RuntimeInformation.ProcessArchitecture != Architecture.Arm)
        { Emit(new { error = "Linux ARM32 only; no native or network action performed." }); return 64; }
        if (args.Length != 2 || args[0] != "--session")
        { Emit(new { usage = "TvNativeStream.dll --session ABSOLUTE_OWNED_SESSION_JSON" }); return 64; }
        IntPtr display = IntPtr.Zero, sm = IntPtr.Zero, instance = IntPtr.Zero;
        bool teeInitialized = false, smInitialized = false, smInitAttempted = false, sourceAttempted = false, streamAttempted = false;
        int result = 1;
        long frames = 0, events = 0, bytes = 0, dropped = 0;
        Stopwatch? streaming = null;
        Session? session = null;
        using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; RequestStop("sigterm"); });
        using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, c => { c.Cancel = true; RequestStop("sigint"); });
        try
        {
            session = ReadSession(args[1]);
            string runtime = $"/run/samsung-tv-root/agent-{session.ParentPid}";
            string directory = runtime + "/tv-native-stream";
            SessionDirectory = directory;
            string parentStartTime = ReadParentStartTime(session.ParentPid);
            foreach (string key in new[] { "LD_PRELOAD", "LD_LIBRARY_PATH", "GST_PLUGIN_PATH", "GST_PLUGIN_SYSTEM_PATH", "GST_PLUGIN_PATH_1_0", "GST_PLUGIN_SYSTEM_PATH_1_0" })
                if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key))) throw new InvalidOperationException("Native search overrides are not allowed.");
            Verify(Display, "79a9b8f652238e426f6377cda76e6cf7abfe83e932479e23ec59e65e0be765ae");
            Verify(Sm, "c91da192a309a09420f4b6193042f540430906896139b23cceea1c0654c63c89");
            Verify(Hal, "6d21a540c7035bdc458c7a974e624894a2586f6aeb47f29cdc980a342e00b77a");
            Verify(Facade, "0e2d738cac4f7b24c1b180b3a104fac11a34ec47356ced26cc058201a6e193cf");
            if (File.Exists(directory + "/stop")) throw new InvalidOperationException("Stop file already exists.");
            Emit(new { stage = "verified", pid = Environment.ProcessId, parentPid = session.ParentPid,
                protocol = "TVCN" + session.ProtocolVersion, receiver = ReceiverAddress, port = session.Port, maxSessionSeconds = session.MaxSessionSeconds,
                directEsMemoryRead = false, perFrameFileOutput = false, protectedContentAllowed = false });
            Volatile.Write(ref LastMainProgressMs, Clock.ElapsedMilliseconds);
            Volatile.Write(ref LastHeartbeatMs, Clock.ElapsedMilliseconds);
            StartWatchdog(session, directory, parentStartTime);
            ConnectAndAuthenticate(session);
            AcquireCaptureLock();
            Mark("gst-init"); gst_init(IntPtr.Zero, IntPtr.Zero);
            ThrowIfStopped();
            Mark("load-original-display"); IntPtr displayHandle = OpenNow(Display);
            display = Factory("displayencodesrc", Display);
            CheckBindings(Display, displayHandle, DisplayBindings);
            Mark("tee-init"); int init = tz_diplayencodesrc_init(display);
            Emit(new { stage = "tee-init-result", rc = init });
            if (init != 0) throw new InvalidOperationException("Native TEE initialization refused.");
            teeInitialized = true;
            Available(display, "before-source");
            ThrowIfStopped();
            Mark("read-minimap"); int miniRc = vconf_get_int(MiniKey, out int mini);
            if (miniRc != 0 || mini != 0) throw new InvalidOperationException("Require verified minimap=0.");
            Mark("load-original-sm"); IntPtr smHandle = OpenNow(Sm);
            sm = Factory("smsrcvideoenc", Sm);
            CheckBindings(Sm, smHandle, SmBindings);
            CheckBindings(Display, displayHandle, DisplayBindings);
            SetProperty(sm, "drm-type", "2"); SetProperty(sm, "enc-type", "0");
            SetProperty(sm, "src-w", "1920"); SetProperty(sm, "src-h", "1080");
            SetProperty(sm, "dst-w", "1920"); SetProperty(sm, "dst-h", "1080");
            SetProperty(sm, "frame-rate", "3000");
            IntPtr priv = Marshal.ReadIntPtr(sm, 0x104);
            if (priv == IntPtr.Zero || Marshal.ReadInt32(sm, 0xdc) != 2 || Marshal.ReadInt32(sm, 0xd8) != 0
                || Marshal.ReadInt32(priv, 0x308) != 1920 || Marshal.ReadInt32(priv, 0x30c) != 1080
                || Marshal.ReadInt32(priv, 0x310) != 1920 || Marshal.ReadInt32(priv, 0x314) != 1080)
                throw new InvalidOperationException("Native CLEAR/CLONE/resolution readback failed.");
            ThrowIfStopped();
            Mark("sm-native-device-init"); smInitAttempted = true;
            int smRc = gst_smsrcvideoenc_device_init(sm);
            Emit(new { stage = "sm-init-result", rc = smRc });
            if (smRc != 1) throw new InvalidOperationException("Native SM init incomplete; no capture.");
            smInitialized = true;
            instance = GetInstance();
            if (instance == IntPtr.Zero) throw new InvalidOperationException("HAL unavailable.");
            int videoFd = Marshal.ReadInt32(instance, 8);
            if (videoFd < 0 || new FileInfo($"/proc/self/fd/{videoFd}").LinkTarget != "/dev/video30")
                throw new InvalidOperationException("Unexpected HAL device.");
            using var caps = new Buffer(104);
            Call("query-capabilities", QueryCapabilities, instance, caps);
            if (caps.Ascii(0, 16) != "sdp-hen" || (caps.Get(84) & 0x04000002) != 0x04000002)
                throw new InvalidOperationException("Unexpected encoder capabilities.");
            uint mappingLength = Query(instance);
            ThrowIfStopped();
            Available(display, "before-config");
            Mark("native-clone-config"); sourceAttempted = true;
            if (tvvideoenc_set_drm_info(sm, 1) != 0) throw new InvalidOperationException("Original clone setup failed.");
            Configure(instance);
            using var streamType = new Buffer(4); streamType.Set(0, 2);
            using var subscription = new Buffer(32); subscription.Set(0, EncoderEvent);
            using var evt = new Buffer(128);
            using var destination = new Buffer(MaximumAu + 16);
            byte[] managed = new byte[MaximumAu];
            byte[] frameHeader = new byte[16];
            ThrowIfStopped();
            Available(display, "before-start"); streamAttempted = true;
            Call("stream-on", StartStream, instance, streamType);
            Call("subscribe-event", SubscribeEvent, instance, subscription);
            Available(display, "before-capture-unmute");
            Mark("native-capture-unmute");
            int unmuteRc = tvvideoenc_set_mute_control(sm, 0);
            if (unmuteRc != 0 || Marshal.ReadInt32(priv, 0x364) != 0) throw new InvalidOperationException("Original capture UNMUTE failed.");
            Available(display, "after-capture-unmute");
            Emit(new { stage = "streaming", width = 1920, height = 1080, codec = "h264", streamFormat = "AnnexB" });
            streaming = Stopwatch.StartNew();
            bool haveIFrame = false;
            long lastFrameAt = 0, lastProgressAt = 0, previousFrames = 0;
            while (!StopRequested)
            {
                MarkSilent("wait-event");
                int waitRc = WaitForReady(instance, out byte ready);
                if (waitRc != 1 || ready > 1) throw new InvalidOperationException("Native wait result invalid.");
                if (streaming.ElapsedMilliseconds - lastFrameAt > 5000)
                    throw new InvalidOperationException("No encoded frames for five seconds.");
                if (ready == 0) continue;
                evt.Clear(); Call("dequeue-event", DequeueEvent, instance, evt, false); events++;
                uint type = evt.Get(0), offset = evt.Get(8), size = evt.Get(12), sliceType = evt.Get(24);
                if (type != EncoderEvent) continue;
                ValidateEvent(offset, size, mappingLength);
                if (!haveIFrame && sliceType != 2) { dropped++; continue; }
                haveIFrame = true;
                ThrowIfStopped();
                Available(display, "before-get-data", false);
                MarkSilent("native-clear-get-data");
                int pullRc = tz_diplayencodesrc_pull_buffer(display, offset, size, destination.Pointer);
                if (pullRc != 0) throw new InvalidOperationException("Original CLEAR GET_DATA refused/failed.");
                ThrowIfStopped();
                Marshal.Copy(destination.Pointer + 16, managed, 0, (int)size);
                if (frames == 0 && !IsAnnexB(managed.AsSpan(0, (int)size)))
                    throw new InvalidOperationException("First CLEAR payload is not Annex-B.");
                MarkSilent("send-access-unit");
                if (session.ProtocolVersion == 2)
                {
                    WriteFrameHeader(frameHeader, managed.AsSpan(0, (int)size), checked((uint)frames));
                    SendBounded(frameHeader, frameHeader.Length, 2000);
                }
                SendBounded(managed, (int)size, 2000);
                frames++; bytes += size; lastFrameAt = streaming.ElapsedMilliseconds;
                if (lastFrameAt - lastProgressAt >= 5000)
                {
                    Emit(new { stage = "progress", frames, bytes, elapsedMs = lastFrameAt,
                        recentAuPerSecond = (frames - previousFrames) * 1000.0 / (lastFrameAt - lastProgressAt) });
                    previousFrames = frames; lastProgressAt = lastFrameAt;
                }
            }
            result = 0;
        }
        catch (OperationCanceledException) when (StopRequested) { result = 0; }
        catch (SocketException) when (StopRequested) { result = 0; }
        catch (ObjectDisposedException) when (StopRequested) { result = 0; }
        catch (Exception error)
        {
            RequestStop("error");
            Emit(new { stage = "error", lastStage = Stage, error = error.Message });
        }
        finally
        {
            RequestStop(StopReason ?? "completed");
            ClosePeer();
            if (streamAttempted && instance != IntPtr.Zero)
                Cleanup("stream-off", () => { using var b = new Buffer(4); b.Set(0, 2); return StopStream(instance, b.Pointer); });
            if (sourceAttempted)
            { Cleanup("native-capture-mute", () => tvvideoenc_set_mute_control(sm, 1)); Cleanup("native-capture-sync-off", () => tvvideoenc_set_sync_control(sm, 0)); }
            if (smInitAttempted && !smInitialized)
                CleanupErrors.Add("SM partial init: native ownership incomplete; process exit reclaims process resources, hardware cleanup unverified.");
            if (smInitialized) Cleanup("sm-native-destroy", () => gst_smsrcvideoenc_destory(sm) == 1 ? 0 : -1);
            if (sm != IntPtr.Zero) Cleanup("sm-unref", () => { gst_object_unref(sm); return 0; });
            if (teeInitialized) Cleanup("tee-finalize", () => tz_diplayencodesrc_finalize(display));
            if (display != IntPtr.Zero) Cleanup("display-unref", () => { gst_object_unref(display); return 0; });
            Finished = true;
        }
        if (CleanupErrors.Count != 0 || StopReason == "native-progress-timeout" || StopReason == "heartbeat-timeout") result = 1;
        var completion = new { stage = "complete", pid = Environment.ProcessId, rc = result, reason = StopReason, frames, events, bytes, dropped,
            streamMs = streaming?.ElapsedMilliseconds ?? 0, processMs = Clock.ElapsedMilliseconds,
            cleanupCallsCompleted = CleanupErrors.Count == 0, processExitStillRequired = true, cleanupErrors = CleanupErrors };
        // This receipt is supplementary: the caller still must observe this PID has exited.
        if (SessionDirectory != null)
            try { File.WriteAllText(SessionDirectory + "/cleanup.json", JsonSerializer.Serialize(completion)); } catch { }
        Emit(completion);
        // Keep the advisory lock until process exit, including the complete cleanup and receipt write.
        GC.KeepAlive(CaptureLock);
        return result;
    }

    sealed class Session
    {
        [JsonPropertyName("protocolVersion")] public int ProtocolVersion { get; set; }
        [JsonPropertyName("port")] public int Port { get; set; }
        [JsonPropertyName("token")] public string Token { get; set; } = "";
        [JsonPropertyName("parentPid")] public int ParentPid { get; set; }
        [JsonPropertyName("maxSessionSeconds")] public int MaxSessionSeconds { get; set; }
    }
    static void ValidateSession(Session s)
    {
        if (s.ProtocolVersion is not (1 or 2) || s.Port < 49152 || s.Port > 65535 || s.ParentPid <= 1
            || s.MaxSessionSeconds < 10 || s.MaxSessionSeconds > 14400
            || s.Token.Length != 64 || s.Token.Any(c => !(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')))
            throw new InvalidOperationException("Session protocol/port/token/parent/lifetime validation failed.");
    }
    static Session ReadSession(string name)
    {
        Session s;
        try
        {
            var file = new FileInfo(name);
            if (!Path.IsPathFullyQualified(name) || file.LinkTarget != null || file.Length <= 0 || file.Length > 4096)
                throw new InvalidOperationException();
            s = JsonSerializer.Deserialize<Session>(File.ReadAllBytes(name)) ?? throw new InvalidOperationException();
            ValidateSession(s);
        }
        catch { throw new InvalidOperationException("Invalid session file; contents withheld."); }
        string directory = $"/run/samsung-tv-root/agent-{s.ParentPid}/tv-native-stream";
        if (Path.GetFullPath(name) != directory + "/session.json" || AppContext.BaseDirectory.TrimEnd('/') != directory
            || !Directory.Exists(directory) || new DirectoryInfo(directory).LinkTarget != null)
            throw new InvalidOperationException("Session and helper must be in the active root agent's owned runtime directory.");
        return s;
    }
    static string ReadParentStartTime(int pid)
    {
        string stat = File.ReadAllText($"/proc/{pid}/stat");
        int end = stat.LastIndexOf(')');
        string[] values = stat[(end + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (end < 0 || values.Length <= 19 || values[0] is "Z" or "X" || !ulong.TryParse(values[19], out _))
            throw new InvalidOperationException("Root agent parent is not live.");
        return values[19]; // Linux proc stat field22; rejects PID reuse.
    }
    static void ConnectAndAuthenticate(Session s)
    {
        Mark("receiver-connect");
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        { NoDelay = true, SendBufferSize = 65536, SendTimeout = 2000, ReceiveTimeout = 1000 };
        Peer = socket;
        var connecting = socket.ConnectAsync(new IPEndPoint(IPAddress.Parse(ReceiverAddress), s.Port));
        if (!connecting.Wait(3000)) throw new InvalidOperationException("Receiver connect deadline exceeded.");
        connecting.GetAwaiter().GetResult();
        Mark("receiver-authentication");
        byte[] header = Encoding.ASCII.GetBytes("TVCN" + s.ProtocolVersion + " " + s.Token + "\n");
        try { SendBounded(header, header.Length, 2000); }
        finally { Array.Clear(header, 0, header.Length); s.Token = ""; }
        byte[] acknowledgment = new byte[3];
        var deadline = Stopwatch.StartNew();
        int received = 0;
        while (received < 3)
        {
            int remaining = 3000 - (int)deadline.ElapsedMilliseconds;
            if (remaining <= 0) throw new InvalidOperationException("Receiver authentication deadline exceeded.");
            socket.ReceiveTimeout = remaining;
            int n = socket.Receive(acknowledgment, received, 3 - received, SocketFlags.None);
            if (n == 0) throw new InvalidOperationException("Receiver closed authentication.");
            received += n;
        }
        if (!acknowledgment.SequenceEqual(new byte[] { (byte)'O', (byte)'K', 10 }))
            throw new InvalidOperationException("Receiver rejected authentication.");
        socket.ReceiveTimeout = 1000;
        Volatile.Write(ref LastHeartbeatMs, Clock.ElapsedMilliseconds);
        new Thread(ReceiveControl) { IsBackground = true, Name = "receiver-heartbeat" }.Start();
        Emit(new { stage = "receiver-authenticated", protocol = "TVCN" + s.ProtocolVersion });
    }
    static void AcquireCaptureLock()
    {
        Mark("capture-exclusive-lock");
        CaptureLock = new FileStream("/run/samsung-tv-root/tv-native-stream.lock", FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.ReadWrite);
        if (flock(CaptureLock.SafeFileHandle.DangerousGetHandle().ToInt32(), 2 | 4) != 0)
            throw new InvalidOperationException("Another native sender still owns capture; no automatic retry.");
    }
    static void SendBounded(byte[] buffer, int count, int milliseconds)
    {
        Socket socket = Peer ?? throw new OperationCanceledException();
        var deadline = Stopwatch.StartNew();
        int offset = 0;
        while (offset < count)
        {
            ThrowIfStopped();
            int remaining = milliseconds - (int)deadline.ElapsedMilliseconds;
            if (remaining <= 0) throw new InvalidOperationException("Receiver backpressure deadline exceeded.");
            socket.SendTimeout = remaining;
            int sent = socket.Send(buffer, offset, count - offset, SocketFlags.None);
            if (sent <= 0) throw new InvalidOperationException("Receiver stopped accepting data.");
            offset += sent;
        }
    }
    static void ReceiveControl()
    {
        byte[] input = new byte[32];
        try
        {
            Socket socket = Peer ?? throw new OperationCanceledException();
            while (!StopRequested)
            {
                int n;
                try { n = socket.Receive(input); }
                catch (SocketException e) when (e.SocketErrorCode is SocketError.TimedOut or SocketError.WouldBlock) { continue; }
                if (n == 0) { RequestStop("receiver-disconnected"); break; }
                for (int i = 0; i < n; i++)
                {
                    if (input[i] == (byte)'H') Volatile.Write(ref LastHeartbeatMs, Clock.ElapsedMilliseconds);
                    else if (input[i] == (byte)'S') { RequestStop("receiver-stop"); break; }
                    else { RequestStop("invalid-control-byte"); break; }
                }
            }
        }
        catch { if (!StopRequested) RequestStop("receiver-disconnected"); }
        finally { ClosePeer(); }
    }
    static void StartWatchdog(Session s, string directory, string parentStartTime)
    {
        int parent = s.ParentPid, lifetime = s.MaxSessionSeconds;
        new Thread(() =>
        {
            long nextParentCheck = 0;
            while (!Finished)
            {
                long now = Clock.ElapsedMilliseconds;
                long stall = now - Volatile.Read(ref LastMainProgressMs);
                if (stall >= 4000) { RequestStop("native-progress-timeout"); ClosePeer(); }
                if (stall >= 6000)
                {
                    // A blocked kernel D-state operation may still outlive _exit. Parent must confirm PID gone.
                    // Never block the watchdog on a diagnostic pipe/console lock.
                    _exit(124);
                }
                if (!StopRequested)
                {
                    if (now >= (long)lifetime * 1000) RequestStop("session-lifetime");
                    else if (now - Volatile.Read(ref LastHeartbeatMs) >= 10000) RequestStop("heartbeat-timeout");
                    else if (now >= nextParentCheck)
                    {
                        nextParentCheck = now + 500;
                        try
                        {
                            if (!Directory.Exists(directory) || File.Exists(directory + "/stop")) RequestStop("runtime-stop");
                            else if (ReadParentStartTime(parent) != parentStartTime) RequestStop("parent-changed");
                        }
                        catch { RequestStop("parent-gone"); }
                    }
                }
                if (StopRequested) ClosePeer();
                Thread.Sleep(100);
            }
        }) { IsBackground = true, Name = "native-stream-watchdog" }.Start();
    }
    static void RequestStop(string reason)
    { Interlocked.CompareExchange(ref StopReason, reason, null); StopRequested = true; }
    static void ThrowIfStopped() { if (StopRequested) throw new OperationCanceledException(); }
    static void ClosePeer()
    {
        Socket? socket = Interlocked.Exchange(ref Peer, null);
        if (socket == null) return;
        try { socket.Shutdown(SocketShutdown.Both); } catch { }
        try { socket.Dispose(); } catch { }
    }
    static bool ContainsIdr(ReadOnlySpan<byte> payload)
    {
        // Scan Annex-B start codes, including both three- and four-byte forms.
        for (int i = 0; i + 3 < payload.Length; i++)
            if (payload[i] == 0 && payload[i + 1] == 0 && payload[i + 2] == 1
                && (payload[i + 3] & 31) == 5) return true;
        return false;
    }
    static void WriteFrameHeader(Span<byte> header, ReadOnlySpan<byte> payload, uint sequence)
    {
        if (header.Length != 16 || payload.Length == 0 || payload.Length > MaximumAu || !IsAnnexB(payload))
            throw new InvalidOperationException("Invalid framed access unit.");
        header[0] = (byte)'T'; header[1] = (byte)'V'; header[2] = (byte)'F'; header[3] = (byte)'2';
        BinaryPrimitives.WriteUInt32BigEndian(header[4..8], checked((uint)payload.Length));
        BinaryPrimitives.WriteUInt32BigEndian(header[8..12], sequence);
        BinaryPrimitives.WriteUInt32BigEndian(header[12..16], ContainsIdr(payload) ? 1u : 0u);
    }
    static int ProtocolSelfTest()
    {
        var s = new Session { ProtocolVersion = 1, Port = 55001, Token = new string('a', 64), ParentPid = 123, MaxSessionSeconds = 60 };
        ValidateSession(s);
        bool pass = Encoding.ASCII.GetByteCount("TVCN1 " + s.Token + "\n") == 71;
        s.ProtocolVersion = 2; ValidateSession(s);
        pass &= Encoding.ASCII.GetByteCount("TVCN2 " + s.Token + "\n") == 71;
        byte[] frameHeader = new byte[16];
        byte[] idr = { 0, 0, 0, 1, 0x67, 0x42, 0x40, 0x28, 0, 0, 1, 0x65, 1 };
        WriteFrameHeader(frameHeader, idr, 0x1020304);
        pass &= Encoding.ASCII.GetString(frameHeader, 0, 4) == "TVF2"
            && BinaryPrimitives.ReadUInt32BigEndian(frameHeader.AsSpan(4, 4)) == idr.Length
            && BinaryPrimitives.ReadUInt32BigEndian(frameHeader.AsSpan(8, 4)) == 0x1020304
            && BinaryPrimitives.ReadUInt32BigEndian(frameHeader.AsSpan(12, 4)) == 1;
        WriteFrameHeader(frameHeader, new byte[] { 0, 0, 1, 0x41, 5 }, 0);
        pass &= BinaryPrimitives.ReadUInt32BigEndian(frameHeader.AsSpan(12, 4)) == 0;
        int rejected = 0;
        foreach (int port in new[] { 0, 80, 49151, 65536 })
        { s.Port = port; try { ValidateSession(s); } catch (InvalidOperationException) { rejected++; } }
        s.Port = 55001; s.Token = new string('A', 64);
        try { ValidateSession(s); } catch (InvalidOperationException) { rejected++; }
        s.Token = new string('a', 64); s.ProtocolVersion = 3;
        try { ValidateSession(s); } catch (InvalidOperationException) { rejected++; }
        pass &= rejected == 6;
        Emit(new { protocolSelfTest = pass, networkCalls = false, nativeCalls = false, headerBytes = 71, frameHeaderBytes = 16, framedAuChecks = pass, rejectedSessions = rejected });
        return pass ? 0 : 1;
    }
}
