using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

// QN90C owner-scoped probe using the real, hash-verified Wayland protocol ABI.
// Never patch WM memory, change security policy, or persist a device/service.
internal static class NativeMouse
{
    const string Wl = "/usr/lib/libwayland-client.so.0";
    const string Extension = "/usr/lib/libtizen-extension-client.so.0";
    const string WmHash = "35cea56d50de74863fe5501fdb231ae70752295d365276c974cd21562b0df870";
    const string WlHash = "01dc185d30ff3e9036e7e0e23b4773c8ef9dbb9cf0d50a7360672e34d4c08dab";
    const string ExtensionHash = "7096cc07082e3f95ab1a5aca0998ddb9e23f90bb22c5a1b36e862b81ee574600";
    static readonly Stopwatch Clock = Stopwatch.StartNew();
    static readonly Dispatcher DispatchRoot = Dispatch;
    static readonly Dictionary<string, (uint Name, uint Version)> Globals = new();
    static readonly HashSet<IntPtr> Devices = new();
    static readonly List<uint> Replies = new();
    static readonly List<string> CleanupErrors = new();
    static IntPtr Display, Registry, Seat, Manager, WlHandle, ExtensionHandle;
    static string Stage = "entry", DeviceName = "", CallbackError = "";
    static bool OwnDeviceReady, InitAttempted, ButtonDown;
    static uint X, Y;
    static WmPointerPosition? PositionReader;

    internal static Action<object>? Logger;
    internal static bool IsDown => ButtonDown;
    internal static string CurrentStage => Stage;
    internal static void Initialize()
    {
        if (!OperatingSystem.IsLinux() || IntPtr.Size != 4 || RuntimeInformation.ProcessArchitecture != Architecture.Arm)
            throw new InvalidOperationException("Linux ARM32 QN90C only");
        foreach (string key in new[] { "LD_PRELOAD", "LD_LIBRARY_PATH" })
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key))) throw new InvalidOperationException("Refuse native search override: " + key);
        Verify("/usr/bin/enlightenment", WmHash); Verify(Wl, WlHash); Verify(Extension, ExtensionHash);
        Mark("open-read-only-wm-position"); PositionReader = WmPointerPosition.Open();
        WlHandle = NativeLibrary.Load(Wl); ExtensionHandle = NativeLibrary.Load(Extension);
        var protocol = NativeLibrary.GetExport(ExtensionHandle, "tizen_input_device_manager_interface");
        CheckProtocol(protocol);
        DeviceName = "TVCitation_Pointer_" + Environment.ProcessId;
        Mark("connect"); Display = wl_display_connect("/run/wayland-0");
        Need(Display != IntPtr.Zero, "Wayland display connection failed");
        Registry = Construct(Display, 1, Interface("wl_registry_interface"), 1, IntPtr.Zero);
        AddDispatcher(Registry, 1); Roundtrip("registry");
        Need(Globals.TryGetValue("wl_seat", out var seatGlobal), "wl_seat not advertised");
        Need(Globals.TryGetValue("tizen_input_device_manager", out var managerGlobal) && managerGlobal.Version >= 2, "tizen_input_device_manager v2 required");
        Seat = Bind(seatGlobal.Name, Interface("wl_seat_interface"), 1); AddDispatcher(Seat, 3); Roundtrip("seat");
        Manager = Bind(managerGlobal.Name, protocol, 2); AddDispatcher(Manager, 2); Roundtrip("manager");
        using var name = new Utf8(DeviceName);
        InitAttempted = true; Request(8, "init-owned-mouse", (IntPtr)1, name.Value);
        long until = Clock.ElapsedMilliseconds + 2200;
        while (!OwnDeviceReady && Clock.ElapsedMilliseconds < until) { Thread.Sleep(30); Roundtrip("wait-owned-device"); }
        Need(OwnDeviceReady, "Temporary mouse accepted but matching device_info not received");
    }
    internal static void Execute(string action, uint x, uint y)
    {
        Need(x < 1920 && y < 1080, "Pointer outside measured 1920x1080 bounds");
        Need(action is "move" or "down" or "up", "Unsupported input action");
        X = x; Y = y;
        try
        {
            Need(PositionReader != null, "WM position reader unavailable");
            PositionReader!.VerifyIdentity();
            if (action is "down" or "up")
            {
                Mark("wait-wm-pointer-position");
                PositionReader.MoveAndWaitFor(X, Y,
                    () => Request(5, "absolute-move", (IntPtr)1, (IntPtr)X, (IntPtr)Y, (IntPtr)1), Pump);
                if (action == "down") { ButtonDown = true; Request(5, "left-down", IntPtr.Zero, (IntPtr)X, (IntPtr)Y, (IntPtr)1); }
                else ReleaseLeft("left-up");
            }
            else Request(5, "absolute-move", (IntPtr)1, (IntPtr)X, (IntPtr)Y, (IntPtr)1);
        }
        catch
        {
            // On an UP failure, release on our existing connection even if the
            // readback timed out. Never leave a held button waiting for a move.
            // If that attempt fails, ButtonDown stays true so Cleanup retries it.
            if (action == "up" && ButtonDown)
                Clean(() => ReleaseLeft("failed-up-release"));
            throw;
        }
    }
    static void ReleaseLeft(string stage)
    { Request(5, stage, (IntPtr)2, (IntPtr)X, (IntPtr)Y, (IntPtr)1); ButtonDown = false; }
    internal static void Pump()
    {
        if (Display == IntPtr.Zero) return;
        while (wl_display_prepare_read(Display) != 0)
            Need(wl_display_dispatch_pending(Display) >= 0, "Wayland dispatch failed");
        wl_display_flush(Display);
        var p = new PollFd { Fd = wl_display_get_fd(Display), Events = 1 };
        if (poll(ref p, 1, 0) > 0)
        { Need(wl_display_read_events(Display) >= 0, "Wayland read failed"); Need(wl_display_dispatch_pending(Display) >= 0, "Wayland dispatch failed"); }
        else wl_display_cancel_read(Display);
        Need(string.IsNullOrEmpty(CallbackError), "Wayland callback: " + CallbackError);
    }
    internal static string[] Cleanup()
    {
        if (Manager != IntPtr.Zero && Display != IntPtr.Zero && wl_display_get_error(Display) == 0)
        {
            if (ButtonDown) Clean(() => ReleaseLeft("cleanup-left-up"));
            if (InitAttempted) Clean(() => Request(3, "deinit-owned-mouse", (IntPtr)1));
            Clean(() => { Send(Manager, 9); Roundtrip("destroy-manager"); });
        }
        if (Display != IntPtr.Zero) { wl_display_disconnect(Display); Display = IntPtr.Zero; }
        Clean(() => PositionReader?.Dispose()); PositionReader = null;
        ButtonDown = false;
        return CleanupErrors.ToArray();
    }
    static void CheckProtocol(IntPtr ptr)
    {
        var i = Marshal.PtrToStructure<WlInterface>(ptr);
        Need(Marshal.PtrToStringUTF8(i.Name) == "tizen_input_device_manager" && i.Version == 4 && i.MethodCount == 12 && i.EventCount == 5, "Unexpected input manager protocol layout");
        foreach (var expected in new[] { (3, "deinit_generator", "u"), (5, "generate_pointer", "uuuu"), (8, "init_generator_with_name", "2us"), (9, "destroy", "2") })
        {
            var m = Marshal.PtrToStructure<WlMessage>(i.Methods + expected.Item1 * Marshal.SizeOf<WlMessage>());
            Need(Marshal.PtrToStringUTF8(m.Name) == expected.Item2 && Marshal.PtrToStringUTF8(m.Signature) == expected.Item3, "Protocol mismatch at opcode " + expected.Item1);
        }
    }
    static IntPtr Interface(string name) => NativeLibrary.GetExport(WlHandle, name);
    static IntPtr Bind(uint name, IntPtr iface, uint version)
    {
        var i = Marshal.PtrToStructure<WlInterface>(iface);
        return Construct(Registry, 0, iface, version, (IntPtr)name, i.Name, (IntPtr)version, IntPtr.Zero);
    }
    static IntPtr Construct(IntPtr target, uint opcode, IntPtr iface, uint version, params IntPtr[] args)
    {
        using var a = new Arguments(args);
        var p = wl_proxy_marshal_array_constructor_versioned(target, opcode, a.Value, iface, version);
        Need(p != IntPtr.Zero, "Wayland proxy construction failed"); return p;
    }
    static void Send(IntPtr target, uint opcode, params IntPtr[] args)
    { using var a = new Arguments(args); wl_proxy_marshal_array(target, opcode, a.Value); }
    static void Request(uint opcode, string stage, params IntPtr[] args)
    {
        Need(Replies.Count == 0, "Unexpected unconsumed input manager result");
        Mark(stage); Send(Manager, opcode, args); Roundtrip(stage);
        Need(Replies.Count == 1, "Expected exactly one input manager result for " + stage);
        uint code = Replies[0]; Replies.Clear();
        if (stage is not ("absolute-move" or "left-down" or "left-up")) Emit(new { stage, result = code });
        Need(code == 0, "Input manager rejected " + stage + ": " + code + " (1=permission, 4=resources, 5=parameter/device-not-ready)");
    }
    static void AddDispatcher(IntPtr p, int kind)
    { Need(wl_proxy_add_dispatcher(p, DispatchRoot, (IntPtr)kind, IntPtr.Zero) == 0, "Cannot install Wayland dispatcher"); }
    static int Dispatch(IntPtr implementation, IntPtr target, uint opcode, IntPtr message, IntPtr args)
    {
        try
        {
            IntPtr A(int n) => Marshal.ReadIntPtr(args, n * IntPtr.Size);
            uint U(int n) => unchecked((uint)Marshal.ReadInt32(args, n * IntPtr.Size));
            int kind = implementation.ToInt32();
            if (kind == 1 && opcode == 0)
            { string name = Marshal.PtrToStringUTF8(A(1)) ?? ""; Globals[name] = (U(0), U(2)); }
            else if (kind == 2 && opcode == 0)
            { var device = A(2); if (device != IntPtr.Zero) { Devices.Add(device); AddDispatcher(device, 4); } }
            else if (kind == 2 && opcode == 1) { Devices.Remove(A(2)); }
            else if (kind == 2 && opcode == 2) { Replies.Add(U(0)); }
            else if (kind == 4 && opcode == 0)
            {
                string name = Marshal.PtrToStringUTF8(A(0)) ?? "";
                if (name == DeviceName) { OwnDeviceReady = true; Emit(new { stage = "owned-device-ready", name, clas = U(1) }); }
            }
        }
        catch (Exception ex) { CallbackError = ex.Message; }
        return 0;
    }
    static void Roundtrip(string stage)
    {
        Mark(stage); Need(wl_display_roundtrip(Display) >= 0, "Wayland roundtrip failed: " + wl_display_get_error(Display));
        Need(string.IsNullOrEmpty(CallbackError), "Wayland callback: " + CallbackError);
    }
    static void Clean(Action action) { try { action(); } catch (Exception ex) { CleanupErrors.Add(ex.Message); } }
    static void Mark(string stage) { Stage = stage; }
    static void Need(bool ok, string error) { if (!ok) throw new InvalidOperationException(error); }
    static void Emit(object value) => Logger?.Invoke(value);
    static void Verify(string path, string hash)
    { using var f = File.OpenRead(path); using var sha = SHA256.Create(); Need(Convert.ToHexString(sha.ComputeHash(f)).Equals(hash, StringComparison.OrdinalIgnoreCase), "Native file hash mismatch: " + path); }
    [StructLayout(LayoutKind.Sequential)] struct WlInterface { public IntPtr Name; public int Version; public int MethodCount; public IntPtr Methods; public int EventCount; public IntPtr Events; }
    [StructLayout(LayoutKind.Sequential)] struct WlMessage { public IntPtr Name, Signature, Types; }
    sealed class Arguments : IDisposable
    {
        public IntPtr Value { get; }
        public Arguments(IntPtr[] args) { Value = Marshal.AllocHGlobal(Math.Max(1,args.Length) * IntPtr.Size); for (int i=0;i<Math.Max(1,args.Length);i++) Marshal.WriteIntPtr(Value,i*IntPtr.Size,i<args.Length?args[i]:IntPtr.Zero); }
        public void Dispose() => Marshal.FreeHGlobal(Value);
    }
    sealed class Utf8 : IDisposable { public IntPtr Value { get; } public Utf8(string value) { Value = Marshal.StringToCoTaskMemUTF8(value); } public void Dispose()=>Marshal.FreeCoTaskMem(Value); }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Dispatcher(IntPtr implementation, IntPtr target, uint opcode, IntPtr message, IntPtr args);
    [DllImport(Wl, CallingConvention=CallingConvention.Cdecl)] static extern IntPtr wl_display_connect([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Wl, CallingConvention=CallingConvention.Cdecl)] static extern int wl_display_roundtrip(IntPtr display);
    [DllImport(Wl, CallingConvention=CallingConvention.Cdecl)] static extern int wl_display_get_error(IntPtr display);
    [DllImport(Wl, CallingConvention=CallingConvention.Cdecl)] static extern void wl_display_disconnect(IntPtr display);
    [DllImport(Wl, CallingConvention=CallingConvention.Cdecl)] static extern IntPtr wl_proxy_marshal_array_constructor_versioned(IntPtr proxy,uint opcode,IntPtr args,IntPtr iface,uint version);
    [DllImport(Wl, CallingConvention=CallingConvention.Cdecl)] static extern void wl_proxy_marshal_array(IntPtr proxy,uint opcode,IntPtr args);
    [DllImport(Wl, CallingConvention=CallingConvention.Cdecl)] static extern int wl_proxy_add_dispatcher(IntPtr proxy,Dispatcher dispatcher,IntPtr implementation,IntPtr data);
    [StructLayout(LayoutKind.Sequential)] struct PollFd { public int Fd; public short Events, Revents; }
    [DllImport("libc.so.6", CallingConvention=CallingConvention.Cdecl)] static extern int poll(ref PollFd fds, uint count, int timeout);
    [DllImport(Wl, CallingConvention=CallingConvention.Cdecl)] static extern int wl_display_prepare_read(IntPtr display);
    [DllImport(Wl, CallingConvention=CallingConvention.Cdecl)] static extern int wl_display_dispatch_pending(IntPtr display);
    [DllImport(Wl, CallingConvention=CallingConvention.Cdecl)] static extern int wl_display_flush(IntPtr display);
    [DllImport(Wl, CallingConvention=CallingConvention.Cdecl)] static extern int wl_display_get_fd(IntPtr display);
    [DllImport(Wl, CallingConvention=CallingConvention.Cdecl)] static extern int wl_display_read_events(IntPtr display);
    [DllImport(Wl, CallingConvention=CallingConvention.Cdecl)] static extern void wl_display_cancel_read(IntPtr display);
}
