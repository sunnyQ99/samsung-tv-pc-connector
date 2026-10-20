using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

internal static partial class Program
{
    static void Configure(IntPtr instance)
    {
        using var format = new Buffer(204); format.Set(0, 2); Call("get-output-format-before", GetFormat, instance, format);
        if (format.Get(0) != 2) throw new InvalidOperationException("Unexpected output format type.");
        using var bitrate = new Buffer(8); bitrate.Set(0, 0x00a20064); bitrate.Set(4, 2048);
        Call("set-native-bitrate", SetBitrate, instance, bitrate);
        format.Set(4, 1920); format.Set(8, 1080); Call("set-output-format", SetFormat, instance, format);
        using var output = new Buffer(204); output.Set(0, 2); Call("get-output-format-after", GetFormat, instance, output);
        if (output.Get(4) != 1920 || output.Get(8) != 1080) throw new InvalidOperationException("Driver resolution readback mismatch.");
        using var capture = new Buffer(204); capture.Set(0, 1); Call("get-capture-format", GetFormat, instance, capture);
        using var parameter = new Buffer(204); parameter.Set(0, 2); parameter.Set(12, 1); parameter.Set(16, 3000);
        Call("set-native-parameters", SetParam, instance, parameter);
        Emit(new { stage = "configured", width = 1920, height = 1080, nativeFrameRateParameter = 3000, measuredFramesPerSecond = (double?)null });
    }
    static uint Query(IntPtr instance)
    {
        using var b = new Buffer(68); using var plane = new Buffer(120);
        b.Set(4, 1); b.Set(48, 1); b.Set(52, unchecked((uint)plane.Pointer.ToInt32()));
        Call("query-buffer", QueryBuffer, instance, b);
        if (b.Get(52) != unchecked((uint)plane.Pointer.ToInt32()) || plane.Get(12) != 0 || plane.Get(4) != 2097152 || plane.Get(8) != 0x46100000)
            throw new InvalidOperationException("Buffer metadata differs from verified QN90C; no interpretation fallback.");
        Emit(new { stage = "buffer", length = plane.Get(4), offset = $"0x{plane.Get(8):x8}", mappedMemoryRead = false });
        return plane.Get(4);
    }
    static void ValidateEvent(uint offset, uint size, uint length)
    {
        if (size == 0 || size > MaximumAu || offset > MaximumAu || (ulong)offset + size > length)
            throw new InvalidOperationException("Native event range exceeds checked CLEAR buffer bounds.");
    }
    static bool IsAnnexB(ReadOnlySpan<byte> data) => data.Length >= 4 && data[0] == 0 && data[1] == 0 && (data[2] == 1 || (data[2] == 0 && data[3] == 1));
    static void Available(IntPtr display, string label, bool verbose = true)
    {
        MarkSilent("native-availability-" + label);
        int rc = tz_is_display_encoding_avaliable(display);
        if (verbose || rc != 0) Emit(new { stage = "availability", label, rc, protectedContentRejected = rc == -250 });
        if (rc != 0) throw new InvalidOperationException("Native availability refused; preserve the content protection decision.");
    }
    static void Verify(string path, string expected)
    { string actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(); if (actual != expected) throw new InvalidOperationException("Original library hash mismatch: " + path); }
    static IntPtr Factory(string name, string expected)
    {
        IntPtr value = gst_element_factory_make(name, "tvcitation_" + name);
        if (value == IntPtr.Zero) throw new InvalidOperationException("Missing factory " + name);
        try
        {
            IntPtr plugin = gst_plugin_feature_get_plugin(gst_element_get_factory(value));
            if (plugin == IntPtr.Zero) throw new InvalidOperationException("Factory plugin absent.");
            string? actual; try { actual = Marshal.PtrToStringUTF8(gst_plugin_get_filename(plugin)); } finally { gst_object_unref(plugin); }
            int rc = gst_element_get_state(value, out int state, out int pending, 0);
            if (actual != expected || rc != 1 || state != 1 || pending != 0) throw new InvalidOperationException("Unexpected factory/state.");
            return value;
        }
        catch { gst_object_unref(value); throw; }
    }
    static IntPtr OpenNow(string path)
    { IntPtr h = dlopen(path, 2); if (h == IntPtr.Zero) throw new InvalidOperationException("dlopen original: " + Marshal.PtrToStringUTF8(dlerror())); return h; }
    static void CheckBindings(string path, IntPtr handle, Binding[] bindings)
    {
        IntPtr symbol = NativeLibrary.GetExport(handle, bindings[0].Name);
        if (dladdr(symbol, out DlInfo info) == 0 || Marshal.PtrToStringUTF8(info.FileName) != path) throw new InvalidOperationException("Native module provenance mismatch.");
        foreach (var b in bindings)
        {
            IntPtr expected = info.Base + b.SymbolOffset;
            IntPtr actual = Marshal.ReadIntPtr(info.Base + b.GotOffset);
            if (actual != expected) throw new InvalidOperationException($"Duplicate native binding mismatch: {path} {b.Name}; no object ABI crossover allowed.");
        }
        Emit(new { stage = "native-bindings-verified", path, count = bindings.Length });
    }
    static void SetProperty(IntPtr o, string name, string value) => gst_util_set_object_arg(o, name, value);
    delegate int HalCall(IntPtr instance, IntPtr arg);
    static void Call(string stage, HalCall call, IntPtr instance, Buffer b, bool verbose = true)
    { MarkSilent(stage); int rc = call(instance, b.Pointer); if (verbose || rc < 0) Emit(new { stage, rc }); if (rc < 0) throw new InvalidOperationException(stage + " failed rc=" + rc); }
    static void Cleanup(string name, Func<int> operation)
    { try { Mark(name); int rc = operation(); Emit(new { stage = name + "-result", rc }); if (rc != 0) CleanupErrors.Add(name + " rc=" + rc); } catch (Exception e) { CleanupErrors.Add(name + ": " + e.Message); } }
    static void Mark(string value) { MarkSilent(value); Emit(new { stage = "enter", operation = value, elapsedMs = Clock.ElapsedMilliseconds }); }
    static void MarkSilent(string value) { Stage = value; Volatile.Write(ref LastMainProgressMs, Clock.ElapsedMilliseconds); }
    static void Emit(object value) { lock (LogLock) { Console.WriteLine(JsonSerializer.Serialize(value)); Console.Out.Flush(); } }
    sealed class Buffer : IDisposable
    {
        public IntPtr Pointer { get; }
        readonly int Length;
        public Buffer(int size) { Length = size; Pointer = Marshal.AllocHGlobal(size); Clear(); }
        public void Clear() => Marshal.Copy(new byte[Length], 0, Pointer, Length);
        public uint Get(int offset) => unchecked((uint)Marshal.ReadInt32(Pointer, offset));
        public void Set(int offset, uint value) => Marshal.WriteInt32(Pointer, offset, unchecked((int)value));
        public string Ascii(int offset, int count) { byte[] a = new byte[count]; Marshal.Copy(Pointer + offset, a, 0, count); return System.Text.Encoding.ASCII.GetString(a).Split('\0')[0]; }
        public void Dispose() => Marshal.FreeHGlobal(Pointer);
    }
    static int SelfTest()
    {
        using var e = new Buffer(128); e.Set(0, EncoderEvent); e.Set(8, 32); e.Set(12, 0x181d); e.Set(16, 177321001); e.Set(24, 2);
        bool passed = e.Get(8) == 32 && e.Get(12) == 0x181d && e.Get(24) == 2;
        ValidateEvent(e.Get(8), e.Get(12), 2097152);
        int rejected = 0;
        foreach (var (offset, size) in new[] { (0u, 0u), (0u, 1048577u), (1048577u, 1u), (uint.MaxValue, 16u) })
            try { ValidateEvent(offset, size, 2097152); } catch (InvalidOperationException) { rejected++; }
        passed &= rejected == 4 && IsAnnexB(new byte[] { 0, 0, 0, 1, 0x67 }) && !IsAnnexB(new byte[] { 1, 2, 3, 4 });
        Emit(new { selfTest = passed, nativeCalls = false, eventBytes = 128, subscribeBytes = 32, rejectedRanges = rejected, duplicateBindings = SmBindings.Length + DisplayBindings.Length });
        return passed ? 0 : 1;
    }
    readonly record struct Binding(string Name, int GotOffset, int SymbolOffset);
    [StructLayout(LayoutKind.Sequential)] struct DlInfo { public IntPtr FileName, Base, SymbolName, Symbol; }
    [DllImport("libdl.so.2", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr dlopen([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libdl.so.2", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr dlerror();
    [DllImport("libdl.so.2", CallingConvention = CallingConvention.Cdecl)] static extern int dladdr(IntPtr address, out DlInfo info);
    [DllImport("libvconf.so.0", CallingConvention = CallingConvention.Cdecl)] static extern int vconf_get_int([MarshalAs(UnmanagedType.LPUTF8Str)] string key, out int value);
    [DllImport(Gst, CallingConvention = CallingConvention.Cdecl)] static extern void gst_init(IntPtr argc, IntPtr argv);
    [DllImport(Gst, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr gst_element_factory_make([MarshalAs(UnmanagedType.LPUTF8Str)] string factory, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Gst, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr gst_element_get_factory(IntPtr element);
    [DllImport(Gst, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr gst_plugin_feature_get_plugin(IntPtr feature);
    [DllImport(Gst, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr gst_plugin_get_filename(IntPtr plugin);
    [DllImport(Gst, CallingConvention = CallingConvention.Cdecl)] static extern int gst_element_get_state(IntPtr element, out int state, out int pending, ulong timeout);
    [DllImport(Gst, CallingConvention = CallingConvention.Cdecl)] static extern void gst_object_unref(IntPtr value);
    [DllImport(Gst, CallingConvention = CallingConvention.Cdecl)] static extern void gst_util_set_object_arg(IntPtr value, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string argument);
    [DllImport(Display, CallingConvention = CallingConvention.Cdecl)] static extern int tz_diplayencodesrc_init(IntPtr element);
    [DllImport(Display, CallingConvention = CallingConvention.Cdecl)] static extern int tz_is_display_encoding_avaliable(IntPtr element);
    [DllImport(Display, CallingConvention = CallingConvention.Cdecl)] static extern int tz_diplayencodesrc_finalize(IntPtr element);
    [DllImport(Display, CallingConvention = CallingConvention.Cdecl)] static extern int tz_diplayencodesrc_pull_buffer(IntPtr element, uint offset, uint size, IntPtr destination);
    [DllImport(Sm, CallingConvention = CallingConvention.Cdecl)] static extern int gst_smsrcvideoenc_device_init(IntPtr element);
    [DllImport(Sm, CallingConvention = CallingConvention.Cdecl)] static extern int gst_smsrcvideoenc_destory(IntPtr element);
    [DllImport(Sm, CallingConvention = CallingConvention.Cdecl)] static extern int tvvideoenc_set_drm_info(IntPtr element, int switching);
    [DllImport(Sm, CallingConvention = CallingConvention.Cdecl)] static extern int tvvideoenc_set_mute_control(IntPtr element, int mute);
    [DllImport(Sm, CallingConvention = CallingConvention.Cdecl)] static extern int tvvideoenc_set_sync_control(IntPtr element, int sync);
    [DllImport(Facade, EntryPoint = "_ZN13IVideoEncoder11getInstanceEv", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr GetInstance();
    [DllImport(Hal, EntryPoint = "_ZN19VideoEncoderSecImpl17queryCapabilitiesEPv", CallingConvention = CallingConvention.Cdecl)] static extern int QueryCapabilities(IntPtr instance, IntPtr arg);
    [DllImport(Hal, EntryPoint = "_ZN19VideoEncoderSecImpl17queryBufferStatusEPv", CallingConvention = CallingConvention.Cdecl)] static extern int QueryBuffer(IntPtr instance, IntPtr arg);
    [DllImport(Hal, EntryPoint = "_ZN19VideoEncoderSecImpl9getFormatEPv", CallingConvention = CallingConvention.Cdecl)] static extern int GetFormat(IntPtr instance, IntPtr arg);
    [DllImport(Hal, EntryPoint = "_ZN19VideoEncoderSecImpl9setFormatEPv", CallingConvention = CallingConvention.Cdecl)] static extern int SetFormat(IntPtr instance, IntPtr arg);
    [DllImport(Hal, EntryPoint = "_ZN19VideoEncoderSecImpl10setBitrateEPv", CallingConvention = CallingConvention.Cdecl)] static extern int SetBitrate(IntPtr instance, IntPtr arg);
    [DllImport(Hal, EntryPoint = "_ZN19VideoEncoderSecImpl8setParamEPv", CallingConvention = CallingConvention.Cdecl)] static extern int SetParam(IntPtr instance, IntPtr arg);
    [DllImport(Hal, EntryPoint = "_ZN19VideoEncoderSecImpl11startStreamEPi", CallingConvention = CallingConvention.Cdecl)] static extern int StartStream(IntPtr instance, IntPtr arg);
    [DllImport(Hal, EntryPoint = "_ZN19VideoEncoderSecImpl10stopStreamEPv", CallingConvention = CallingConvention.Cdecl)] static extern int StopStream(IntPtr instance, IntPtr arg);
    [DllImport(Hal, EntryPoint = "_ZN19VideoEncoderSecImpl14subscribeEventEPv", CallingConvention = CallingConvention.Cdecl)] static extern int SubscribeEvent(IntPtr instance, IntPtr arg);
    [DllImport(Hal, EntryPoint = "_ZN19VideoEncoderSecImpl12dequeueEventEPv", CallingConvention = CallingConvention.Cdecl)] static extern int DequeueEvent(IntPtr instance, IntPtr arg);
    [DllImport(Hal, EntryPoint = "_ZN19VideoEncoderSecImpl12waitForReadyEPb", CallingConvention = CallingConvention.Cdecl)] static extern int WaitForReady(IntPtr instance, out byte ready);
    [DllImport("libc.so.6", CallingConvention = CallingConvention.Cdecl)] static extern void _exit(int status);
    [DllImport("libc.so.6", CallingConvention = CallingConvention.Cdecl, SetLastError = true)] static extern int flock(int fd, int operation);
}
