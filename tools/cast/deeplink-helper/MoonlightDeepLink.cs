using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;

internal static class MoonlightDeepLink
{
    private const string DefaultTargetAppId = "MoonLightS.MoonlightWasm";
    private const string Operation = "http://samsung.com/appcontrol/operation/eden_resume";
    private const string Library = "libcapi-appfw-app-control.so.0";
    private const int DeliveryTimeoutMs = 8000;

    // Keep the native callback rooted until process exit, including timeout cleanup.
    private static ResultCallback? diagnosticCallback;

    private static int Main(string[] args)
    {
        bool dryRun = false;
        bool diagnose = false;
        string targetAppId = DefaultTargetAppId;
        bool targetSpecified = false;
        bool optionsValid = args.Length >= 2 && args.Length <= 6;
        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] == "--dry-run" && !dryRun) dryRun = true;
            else if (args[i] == "--diagnose" && !diagnose) diagnose = true;
            else if (args[i] == "--target" && !targetSpecified && i + 1 < args.Length)
            {
                targetSpecified = true;
                targetAppId = args[++i];
                optionsValid &= targetAppId == DefaultTargetAppId || targetAppId == "TVCScreen1.MoonlightWasm";
            }
            else optionsValid = false;
        }
        if (!optionsValid ||
            string.IsNullOrWhiteSpace(args[0]) || args[0].Length > 512 ||
            Array.Exists(args[0].ToCharArray(), char.IsControl) ||
            !int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out int appId))
        {
            Console.WriteLine(JsonSerializer.Serialize(new {
                error = "Usage: MoonlightDeepLink.dll <Sunshine-serverUid> <numeric-appId> [--target MoonLightS.MoonlightWasm|TVCScreen1.MoonlightWasm] [--diagnose] [--dry-run]"
            }));
            return 2;
        }

        string serverUid = args[0];
        string values = JsonSerializer.Serialize(new { serverUid, appId });
        string payload = JsonSerializer.Serialize(new { values });
        if (dryRun)
        {
            Console.WriteLine(JsonSerializer.Serialize(new {
                dryRun = true, diagnose, timeoutMs = DeliveryTimeoutMs, sendMode = "asyncWithGlibLoop",
                targetAppId, operation = Operation,
                serverUid, appId, data = new[] { new { key = "PAYLOAD", value = new[] { payload } } }
            }));
            return 0;
        }

        IntPtr handle = IntPtr.Zero;
        IntPtr payloadUtf8 = IntPtr.Zero;
        IntPtr payloadArray = IntPtr.Zero;
        bool requestAccepted = false;
        int callbackReceived = 0;
        int callbackResult = 0;
        bool callbackHandleMatches = false;
        bool callbackContextMatches = false;
        bool timedOut = false;
        long callbackWaitMs = 0;
        var identity = new Dictionary<string, string>();
        string? error = null;
        var stages = new List<object>();

        void Check(string stage, int rc)
        {
            stages.Add(new { stage, rc });
            if (rc != 0)
                throw new InvalidOperationException(stage + " failed with rc=" + rc.ToString(CultureInfo.InvariantCulture));
        }

        try
        {
            if (diagnose)
            {
                // Whitelisted fields only; no environment, app data, or credentials.
                foreach (string line in File.ReadLines("/proc/self/status"))
                {
                    int colon = line.IndexOf(':');
                    if (colon <= 0) continue;
                    string name = line.Substring(0, colon);
                    if (name == "Uid" || name == "Gid" || name == "CapEff" || name == "CapPrm")
                        identity[name] = line.Substring(colon + 1).Trim();
                }
            }
            // Resolve/check GLib before submitting a request. FALSE means no wait.
            g_main_context_iteration(IntPtr.Zero, 0);
            Check("create", app_control_create(out handle));
            if (handle == IntPtr.Zero)
                throw new InvalidOperationException("app_control_create returned a null handle");
            Check("set_app_id", app_control_set_app_id(handle, targetAppId));
            Check("set_operation", app_control_set_operation(handle, Operation));

            // A one-element native array maps directly to Web AppControl data.value[0].
            // Retain caller-owned UTF-8 value and char** until the handle is destroyed.
            payloadUtf8 = Marshal.StringToCoTaskMemUTF8(payload);
            payloadArray = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(payloadArray, payloadUtf8);
            Check("add_extra_data_array", app_control_add_extra_data_array(handle, "PAYLOAD", payloadArray, 1));

            {
                // One request per short-lived process. An opaque nonzero tag associates
                // this callback without assuming this firmware reuses the native handle.
                IntPtr requestContext = new IntPtr(0x54435643);
                diagnosticCallback = (request, result, userData) =>
                {
                    // No exceptions or collections are used across the native boundary.
                    if (Volatile.Read(ref callbackReceived) != 0) return;
                    callbackResult = result;
                    callbackHandleMatches = request == handle;
                    callbackContextMatches = userData == requestContext;
                    Volatile.Write(ref callbackReceived, 1);
                };
                var wait = Stopwatch.StartNew();
                Check("send_launch_request_async", app_control_send_launch_request_async(
                    handle, diagnosticCallback, IntPtr.Zero, requestContext));
                requestAccepted = true;
                while (Volatile.Read(ref callbackReceived) == 0 && wait.ElapsedMilliseconds < DeliveryTimeoutMs)
                {
                    // Pump the GLib global-default loop without an unbounded native wait.
                    g_main_context_iteration(IntPtr.Zero, 0);
                    if (Volatile.Read(ref callbackReceived) == 0)
                    {
                        int remaining = DeliveryTimeoutMs - (int)wait.ElapsedMilliseconds;
                        if (remaining > 0) Thread.Sleep(Math.Min(10, remaining));
                    }
                }
                callbackWaitMs = wait.ElapsedMilliseconds;
                timedOut = Volatile.Read(ref callbackReceived) == 0;
                if (!timedOut)
                {
                    stages.Add(new { stage = "launch_result_callback", rc = callbackResult,
                        handleMatches = callbackHandleMatches, contextMatches = callbackContextMatches });
                    if (!callbackContextMatches)
                        error = "Launch result callback did not match the request context";
                    else if (callbackResult != 0)
                        error = "Launch result callback failed with rc=" + callbackResult.ToString(CultureInfo.InvariantCulture);
                }
            }
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message;
        }
        finally
        {
            if (handle != IntPtr.Zero)
            {
                try
                {
                    int rc = app_control_destroy(handle);
                    stages.Add(new { stage = "destroy", rc });
                    if (rc != 0 && error == null)
                        error = "app_control_destroy failed with rc=" + rc.ToString(CultureInfo.InvariantCulture);
                }
                catch (Exception ex)
                {
                    error ??= ex.GetType().Name + ": " + ex.Message;
                }
            }
            if (payloadArray != IntPtr.Zero) Marshal.FreeHGlobal(payloadArray);
            if (payloadUtf8 != IntPtr.Zero) Marshal.FreeCoTaskMem(payloadUtf8);
            GC.KeepAlive(diagnosticCallback);
        }

        bool received = Volatile.Read(ref callbackReceived) != 0;
        bool requestDelivered = received && callbackContextMatches && callbackResult == 0;
        string deliveryStatus = requestDelivered ? "deliveredToApplication"
            : received ? "deliveryFailed" : requestAccepted ? "deliveryUnknown" : "notSubmitted";
        Console.WriteLine(JsonSerializer.Serialize(new {
            dryRun = false, diagnose, targetAppId, operation = Operation,
            serverUid, appId, requestAccepted, requestDelivered, callbackReceived = received,
            callbackResult = received ? (int?)callbackResult : null,
            callbackHandleMatches = received ? (bool?)callbackHandleMatches : null,
            callbackContextMatches = received ? (bool?)callbackContextMatches : null,
            deliveryStatus, timedOut, callbackWaitMs, timeoutMs = DeliveryTimeoutMs, sendMode = "asyncWithGlibLoop",
            streamVerified = false, identity, stages, error
        }));
        if (error != null || !requestAccepted) return 1;
        if (timedOut) return 3;
        return requestDelivered ? 0 : 1;
    }

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int app_control_create(out IntPtr handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int app_control_set_app_id(IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string appId);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int app_control_set_operation(IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string operation);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int app_control_add_extra_data_array(IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string key, IntPtr values, int length);

    // Official TizenFX ResultCallback: void(IntPtr request, int result, IntPtr userData).
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ResultCallback(IntPtr request, int result, IntPtr userData);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int app_control_send_launch_request_async(IntPtr handle, ResultCallback resultCallback,
        IntPtr replyCallback, IntPtr userData);

    // GLib gboolean is an int, not a one-byte C++ bool. NULL selects the default context.
    [DllImport("libglib-2.0.so.0", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int g_main_context_iteration(IntPtr context, int mayBlock);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int app_control_destroy(IntPtr handle);
}
