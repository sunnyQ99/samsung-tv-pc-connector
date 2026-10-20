# TV mouse input helper (QN90C)

A temporary Wayland input helper for the verified QN90C firmware. It runs as root on the TV (Linux ARM32, .NET 6) and creates its own virtual pointer through the `tizen_input_device_manager` protocol. Each session also starts a separate companion process that temporarily enables the normal cursor policy and restores it afterwards.

It never patches window-manager memory or driver binaries, and never installs services or boot settings. On Windows you can run `--selftest` and `--policy-selftest` offline. A real run always goes through the one-use session created by `backend/native_input.py`.

## Behavior

- **Coordinates.** They are measured 1920×1080 logical coordinates; do not double them.
- **Actions.** Supported: move, left down and left up; a PC `click` sends down then up. Right, middle and wheel are unverified and return "unsupported".
- **Apps.** The target app must enable Samsung's `pointing-device-support`, and some TV apps ignore the mouse. A native ACK means the window manager accepted the input, not that the app redrew.
- **Position barrier before buttons.** Before a down or up, the helper moves the pointer and reads the window manager's pointer state back, read-only (`O_RDONLY` reads of verified 4/8-byte fields; no writes, no ptrace).
  - The button is sent only once the pointer is at the target.
  - If a pending cursor warp re-centres the pointer to (960,540), the same move is re-sent once; buttons are never repeated.
  - Focus and client identity, cursor availability and the transition state are checked throughout.
  - The first move and the retry share a 150 ms budget. If the pointer has not arrived, the click is refused rather than sent to the wrong place.
- **Release on failure.** If an up fails, the helper still tries to release the button, then cleans up.

## Cursor-policy companion

The companion starts only after the PC has authenticated the session and the native mouse is initialized. It is the same DLL, run with `--policy-session`.

- **Lock and identity.** It holds a global `flock` and manages `memory/window_system/input/force_enable_cursor` through the normal, hash-verified Buxton interface. Before writing True, it saves the PID, start time, full argv and the input/root identities. The main input helper verifies them before writing the first authorization heartbeat.
- **Lease.** Only valid PC commands seen by the input main loop renew the lease; there is no separate renewal timer. The companion enforces a 6 s lease on a monotonic clock and checks the input/root identities, explicit release and signals. It still restores the policy on its own if the input thread blocks, exits or is killed by its watchdog.
- **Restore.** A restore waits up to about 3.5 s and reads the value back. If the original value was False, it writes False back. If it was True, nothing is written, so an external owner can change it later.
- **Normal shutdown.** The helper releases the temporary mouse first, then asks the companion to restore and waits up to 7 s. The backend keeps verifying for up to 11 s.
- **Cleanup.** `policy-owner.json` and `policy-result.json` keep the full identity and restore evidence. The backend deletes the fixed policy files only after it confirms the original processes have exited. Missing receipts, identity changes or a failed RPC keep a "retired" record, which the next root connection re-checks.

## Interfaces

- **PC side:** `NativeInputController(root)`, `await ensure_connected(root_connection)`, `status()`, `await pointer(action, x, y, button='left', delta=0)`, `await stop()`.
- **TV launch:** `/usr/bin/dotnet <owned-dir>/NativeInput.dll --session <owned-dir>/session.json`.
- **Session file.**
  - Location: `/run/samsung-tv-root/agent-<parent-pid>/native-input-<12hex>/session.json`.
  - Contents: the fixed PC address, a random TCP port in 49152–65535, a random 64-hex token, `parentPid`, `width=1920`, `height=1080`.
  - It is written mode 600 into a mode-700 folder and deleted by the helper right after validation.
- **Handshake.**
  1. The TV connects back to the PC with `TCP_NODELAY` on both ends.
  2. The TV sends `hello` with `protocol=1`, the token, its PID/start time and the screen size.
  3. The PC checks the fixed TV address, the one-use token and the launch PID, and answers `hello/ok`.
  4. When the device and the companion are both ready, the TV sends `ready` with `policyReady=true`.
- **Commands.**
  - JSON lines, each with an increasing positive `id` and an `action` of ping/move/down/up/stop; input actions also carry x/y/buttons (0 or 1).
  - Every ACK echoes the `id` with `ok=true`, `result=0` and `buttons`.
  - One native thread owns Wayland and pumps it continuously; each connection creates exactly one temporary device.
  - Only adjacent unsent moves are merged. down/up/click keep their queue boundaries, so a click never waits behind a pile of old moves.
- **Stop conditions.**
  - The PC sends a ping every 2 s.
  - The helper stops and releases the left button and its temporary device after 10 s without a valid command, or on EOF, SIGTERM, parent exit or a PID start-time change.
  - A watchdog bounds blocked native calls. A forced exit is reported as `cleanupComplete=false`.
- **Status.** `lastAckMilliseconds`, `maxAckMilliseconds`, `meanAckMilliseconds` and `ackSampleCount` cover at most 64 in-memory samples. They measure the round trip from PC send to native ACK, not end-to-end visual latency.

## Build and offline checks

```powershell
dotnet build tools/tv-native-input/NativeInput.csproj -c Release -o tools/tv-native-input/out --nologo
dotnet --roll-forward Major tools/tv-native-input/out/NativeInput.dll --selftest
dotnet --roll-forward Major tools/tv-native-input/out/NativeInput.dll --policy-selftest
py -3.14 backend/checks_native_input.py
py -3.14 backend/checks_native_input_policy.py
```

After each build, write `out/manifest.json` with the SHA-256 of the three output files (see the main README). The PC checks every file against it before uploading. On the TV, the helper verifies the fixed SHA-256 of the window manager, Wayland client and Tizen extension client, plus the protocol table layout, before loading any native API. A firmware change makes it fail instead of guessing the ABI.
