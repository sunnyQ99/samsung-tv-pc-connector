# QN90C native video sender

This is the continuous transport form of the verified CLEAR/UNMUTE capture path. It has delivered the complete Samsung Home UI and moving app UI at 1920×1080, about 30 access units per second. It does not claim 120 fps and never bypasses protected-content decisions.

Normally the backend deploys and runs it (`backend/native_video.py`). Local builds and self-tests do not load Samsung libraries or connect to the TV.

## Build and files

```
dotnet build tools/tv-native-stream/TvNativeStream.csproj -c Release -o tools/tv-native-stream/out
dotnet --roll-forward Major tools/tv-native-stream/out/TvNativeStream.dll --layout-selftest
dotnet --roll-forward Major tools/tv-native-stream/out/TvNativeStream.dll --protocol-selftest
```

Upload all three files from `out/`: `TvNativeStream.dll`, `TvNativeStream.deps.json`, and `TvNativeStream.runtimeconfig.json`. Build targets net6.0/AnyCPU for the verified TV's Samsung ARM32 .NET 6.0.9 launcher; do not use the PC `--roll-forward` option on the TV.

## Owned runtime and launch

Keep the root-agent session alive for the whole capture. Replace `PID` with that root agent's live PID; the sender pins its `/proc/PID/stat` start time to reject PID reuse.

Runtime directory must be `/run/samsung-tv-root/agent-PID/tv-native-stream`. Upload the three files there. Write mode-0600 `session.json` there, with a freshly generated one-use 32-byte random token encoded as exactly 64 lowercase hex characters:

```json
{"protocolVersion":2,"port":55001,"token":"REPLACE_WITH_64_LOWERCASE_HEX","parentPid":1234,"maxSessionSeconds":60}
```

Only port 49152–65535 is allowed; receiver address is fixed in code to `192.168.1.75`. Lifetime is 10–14400 seconds, including setup. For first acceptance use 60 seconds; four hours is a limit supported by the configuration, not an endurance claim.

Execute the following as a direct root-agent command body, replacing PID. This device rejects unsigned shell script files, so do not upload a shell script and invoke it:

```sh
/usr/bin/dotnet /run/samsung-tv-root/agent-PID/tv-native-stream/TvNativeStream.dll --session /run/samsung-tv-root/agent-PID/tv-native-stream/session.json > /run/samsung-tv-root/agent-PID/tv-native-stream/sender.log 2>&1 &
```

The launcher should record `$!` in the owned directory and verify that exact process exits. Do not put the token on the command line or print the session file. Remove previous stop/cleanup receipts before a new session, only after the old PID is gone.

## Wire contracts TVCN2 and legacy TVCN1

1. PC listens before launching the helper; accept only the expected TV IP. TV connects to the fixed PC address within three seconds.
2. TV sends exactly 71 ASCII bytes: `TVCN2 ` (session protocolVersion 2), the 64-character token, and LF. PC caps the unauthenticated header, compares the expected one-use token in constant time, and replies exactly `OK` plus LF within three seconds.
3. After the acknowledgement, each original Annex-B access unit is preceded by 16 bytes: ASCII `TVF2`, big-endian uint32 payload length (1–1048576), uint32 sequence (starts at zero), and uint32 flags (bit zero indicates an actual type-5 IDR NAL; all other bits zero). This preserves encoder event boundaries for direct WebCodecs playback. No capture timestamps are invented. Legacy session protocolVersion 1 still sends `TVCN1 ` and a raw unframed Annex-B stream; the current production receiver requires version 2.
4. PC→TV sends single ASCII `H` heartbeat bytes every two seconds, or `S` to stop. EOF, unknown input, or ten seconds without a heartbeat stops capture. There is no reconnect inside the sender.

The sender passes original CLEAR access units through immediately with TCP_NODELAY. It does not batch frames or synthesize SPS/PPS. A one-MiB reusable managed buffer is an AU size bound, not a queue. Each complete AU has a two-second total send deadline. The receiver should use one persistent decoder/remuxer and share its output with viewers.

## Native order and protection

The source and helpers preserve the validated native sequence: verify four original library hashes; original display GObject/TEE init and availability; verify minimap disabled; original SM GObject, CLEAR=2/CLONE=0/resolution readback and duplicate-symbol bindings; SM device init; exact video30/capability/buffer metadata guards; native clone setup; bitrate/format/parameters; STREAMON; event subscription; availability; original capture UNMUTE; availability; wait/dequeue; availability on **every AU**; original TEE CLEAR GET_DATA; send. Native mapped ES memory is never dereferenced.

This uses the native capture encoder's mute switch, not the TV speaker volume. No system library, global configuration, private logging namespace, mixer plane or DRM policy is changed. Any nonzero availability result stops capture; protected content is never exported through an alternative path.

## Stop and cleanup

Normal cleanup is STREAMOFF → capture MUTE=1 → SYNC=0 → SM destroy/unref → TEE finalize/display unref. Receiver disconnect, `S`, SIGTERM/SIGINT, stop file, runtime removal, parent death/PID reuse and session expiry all enter cleanup. Five seconds without an encoded AU fails the session.

The capture process takes a nonblocking `flock` on `/run/samsung-tv-root/tv-native-stream.lock` before touching native capture state and holds it until process exit. Never delete this lock file while senders may exist. This serializes this sender's instances; the operator must still avoid other unrelated capture tools.

A watchdog tracks progress, not time since capture start: a main-thread operation stalled four seconds requests stop, and six seconds invokes `_exit(124)`. Each cleanup operation also updates progress. This is an emergency bound, not proof that a blocked kernel D-state process can be killed. The caller must confirm the recorded PID is gone before another attempt.

At normal return, `cleanup.json` records `pid`, `rc`, reason, AU/byte counts and `cleanupCallsCompleted`. It is supplementary; it never replaces PID-gone verification. No receipt, a cleanup failure, or a surviving process forbids an automatic restart. A partial native initialization reports cleanup as unverified and relies on process exit for process-owned resources.

Managed progress JSON is emitted once per five seconds and at startup/cleanup. There are no per-frame diagnostic files or managed per-frame logs. The original Samsung libraries retain their own system logging behavior. Token and session contents are not logged.

## Validation boundary

`--layout-selftest` and `--protocol-selftest` run without native libraries. The 30 fps limit is a hardware result: requesting `frame-rate` 6000 still produced 30 fps (every PTS delta was 1500 at 45 kHz). Long-duration endurance and recovery after a full TV power cycle remain unverified.
