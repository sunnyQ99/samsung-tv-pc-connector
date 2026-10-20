# TV pointer test page

A self-contained Tizen web app (`TVCPtrT001`, app `TVCPtrT001.Probe`) used to test mouse input and measure click-to-picture latency on the TV.

**Contents and permissions.**

- Static red/green/blue/white buttons, a moving cyan block and an increasing UI counter, for checking unprotected screen capture.
- The package has no network permission, access origin, service, on-boot flag, root or external resources, and its CSP blocks network connections.
- `config.xml` sets `pointing-device-support="enable"`. Per [Samsung's mouse guide](https://developer.samsung.com/smarttv/develop/guides/user-interaction/mouse.html), 2021+ models default to off, so an app without it never receives mouse events.
- The Tizen web package ID must be exactly 10 alphanumeric characters ([config.xml reference](https://github.com/Samsung/tizen-docs/blob/master/docs/application/web/reference/config-xml.md)); the build script checks this. A 9-character ID was rejected at install (`failed[118,-4]`).

## Build

```powershell
py -3.14 tools/tv-pointer-probe/build.py
```

Every build gets a new UUID folder under `%LOCALAPPDATA%\TVCITATION\Signing\pointer-builds\`. It is signed with your own author certificate and the SDK legacy distributor accepted by this TV. The build temporarily switches the local signing profile to legacy and always restores it in `finally`. It never deploys.

## Use

Install with your normal deployment flow, then start it with `app_launcher -u 5001 -s TVCPtrT001.Probe` (or use the `was_debug` flow to get a CDP port). Read the whitelisted diagnostics over CDP:

```javascript
JSON.stringify({
  state: window.tvcProbeState,
  viewport: {width: innerWidth, height: innerHeight},
  events: window.tvcInputEvents.slice(-100)
})
```

**What is recorded.**

- `window.tvcInputEvents` keeps only `isTrusted=true` pointer, mouse, click and wheel events plus a few control keys (arrows, OK, Back). It never records typed characters or page data.
- At most 100 entries are kept, and moves are merged per type every 50 ms. Each entry has a relative time, coordinates, buttons and an allow-listed target ID.
- Nothing is saved to files or sent anywhere.

**Interpreting the results.**

- `isTrusted` only means the browser treated an event as trusted; correlate it with the known injection time, event type and coordinates.
- `uiTicks` counts `requestAnimationFrame` updates. It is not the capture frame rate or the panel refresh rate.

`check.cjs` tests these locally: rejection of untrusted events and character keys, the 100-entry cap, move merging, the animated state and pagehide cleanup.
