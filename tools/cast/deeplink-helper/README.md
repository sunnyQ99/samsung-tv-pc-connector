# Moonlight native AppControl launcher

Launches Moonlight on the TV straight into the PC's Sunshine *Desktop* app through Tizen AppControl. It targets the measured Tizen 7 / ARMv7 TV and uses the TV's existing .NET 6 runtime. Building it does not contact the TV; the backend runs it on the TV through the root session when casting starts.

## Files and use

Build with `dotnet build tools/cast/deeplink-helper/MoonlightDeepLink.csproj -c Release -o tools/cast/deeplink-helper/out`, then upload the three files from `out/` together to `/home/owner/share/tmp/sdk_tools/tvcitation-cast/` on the TV (the path `backend/server.py` uses):

- `MoonlightDeepLink.dll`
- `MoonlightDeepLink.runtimeconfig.json`
- `MoonlightDeepLink.deps.json`

Invoke with the TV's .NET launcher from the directory containing these files:

```text
dotnet MoonlightDeepLink.dll <Sunshine-serverUid> <Desktop-numeric-appId>
```

The two values are actual identifiers, not the PC IP address or the text `Desktop`. Read `serverUid` from Sunshine GameStream server-info XML's `uniqueid` element. Read the numeric app ID from the app-list entry whose title is Desktop. The receiver must already have that host paired and stored.

For local inspection only, the final argument `--dry-run` constructs and prints the request without loading any native library:

```powershell
dotnet --roll-forward Major ./out/MoonlightDeepLink.dll validation-host-uid 12345 --dry-run
```

The helper's target app is fixed to `MoonLightS.MoonlightWasm`; its operation is fixed to `http://samsung.com/appcontrol/operation/eden_resume`. It neither installs apps nor changes settings. Native execution submits one asynchronous launch request and pumps GLib's default context for up to 8 seconds to receive its delivery result. An already-running different Sunshine application can cause Moonlight's own switch-app confirmation flow.

Optional `--diagnose` adds only the whitelisted `Uid`, `Gid`, `CapEff`, and `CapPrm` fields from `/proc/self/status`; it does not change the launch path. Both `--diagnose` and `--dry-run` can be supplied in either order.

Exit codes: `0` = dry-run completed, or launch callback returned zero with the correct opaque request context and cleanup succeeded; `1` = native/load/callback/cleanup error; `2` = invalid CLI arguments; `3` = delivery callback was not observed within 8 seconds. JSON distinguishes `requestAccepted`, `callbackReceived`, `requestDelivered`, and `timedOut`. `streamVerified` is always false: use a newly armed `CastMonitor` connection observation and independent TV frame/audio acceptance.

## ABI and payload evidence

Official Samsung/TizenFX source pinned to commit `98de5bc2a3d71dde28f3b61a9fe40f4a5f7e2dff`:

- [Interop.Libraries.cs](https://github.com/Samsung/TizenFX/blob/98de5bc2a3d71dde28f3b61a9fe40f4a5f7e2dff/src/Tizen.Applications.Common/Interop/Interop.Libraries.cs#L22) defines `libcapi-appfw-app-control.so.0`.
- [Interop.AppControl.cs](https://github.com/Samsung/TizenFX/blob/98de5bc2a3d71dde28f3b61a9fe40f4a5f7e2dff/src/Tizen.Applications.Common/Interop/Interop.AppControl.cs#L60) declares create/set-operation/set-app-id, add-extra-data-array, destroy, and send-launch-request. Relevant lines are 60, 72, 96, 120, 129, and 138.
- The same official interop source declares `ResultCallback(IntPtr request, int result, IntPtr userData)` and `SendLaunchRequestAsync(handle, resultCallback, replyCallback, userData)`. The [native API reference](https://docs.stg.tizen.org/application/native/api/iot-headed/latest/group__CAPI__APP__CONTROL__MODULE.html) distinguishes the delivery-result callback from a callee's optional reply. This helper requests the delivery result and passes a null reply callback.
- [GLib's official declaration](https://docs.gtk.org/glib/method.MainContext.iteration.html) is `gboolean g_main_context_iteration(GMainContext*, gboolean)`. The P/Invoke uses 32-bit integers for `gboolean`, a null context, and false `may_block`; a monotonic managed deadline limits the event loop to 8 seconds. Native callback execution itself is outside the managed scheduler's control.

The P/Invoke declarations use C calling convention, pointer-sized handles, 32-bit return codes/counts, and explicit UTF-8 strings. A manually allocated one-element `char**` supplies `PAYLOAD`; both the array and its UTF-8 string remain alive until after handle destruction. The callback is rooted until process exit. Its opaque nonzero `user_data` tag identifies this process's sole request. A callback's handle address is recorded but is not an acceptance condition: the measured firmware returned a different handle address with a successful callback. The helper does not change UID, environment variables, permissions, or application security policy.

The TV's actual library exports `app_control_send_launch_request_async`. Export presence does not by itself establish runtime behavior.

The exact v1.19.0 WGT's `config.xml` declares `eden_resume`. Its `platform/index.js` reads AppControl key `PAYLOAD`, parses that string into an object, then parses the object's `values` string into `{ serverUid, appId }`. App ID must remain numeric because the receiver compares it using strict equality. The helper supplies an actual array value for Web AppControl `data.value[0]`:

```javascript
data = [{
  key: 'PAYLOAD',
  value: [JSON.stringify({ values: JSON.stringify({ serverUid, appId }) })]
}]
```

Receiver source: [v1.19.0 platform/index.js](https://github.com/brightcraft/moonlight-tizen/blob/v1.19.0/wasm/platform/index.js#L4544). The receiver's `handleDeepLink()` requires its `_isSmartHubSupported` probe to succeed. On the developer's TV this probe's cached value was true and the installed JavaScript matched the WGT, so no receiver patch is needed.

## Local validation

- Builds for `net6.0` / AnyCPU with the .NET 10 SDK, with no external NuGet packages.
- `--dry-run` (with or without `--diagnose`, in either order) prints both JSON layers without native access; missing, malformed, unknown and duplicate arguments return exit 2.
- On the TV, the asynchronous launch plus the GLib event loop returned a zero-result callback about 200 ms after the request, followed by a fresh Sunshine connection and 1080p60 stream startup. An earlier fire-and-exit call returned zero without starting a stream, which is why the helper keeps its event loop alive until the callback arrives. This does not prove TV presentation, audio or 4K120.
