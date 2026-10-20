# Samsung TV PC Connector

Watch and control a Samsung smart TV from a Windows PC. The TV's live picture runs inside a desktop window: move the mouse and click to use TV apps, press remote keys, type into TV text fields. The same app also casts the PC desktop to the TV, with optional TV-only audio.

> **Status: personal research project, one verified TV.** Built and tested against a single Samsung QN90C (2023, Tizen 7, ARMv7). It needs **root access to the TV** through [samsung-tv-root](https://github.com/chris-ritsen/samsung-tv-root). The TV-side helpers refuse to run unless the Samsung system libraries match the SHA-256 hashes of that firmware. Not affiliated with or endorsed by Samsung. Rooting your TV is at your own risk.

## Features

- **Live TV → PC picture.** The TV's own display-clone hardware encoder produces 1080p H.264 at about 30 fps. The PC decodes it with WebCodecs and draws it straight to a canvas: no screenshot polling, no re-encoding.
- **Mouse control inside the picture.** Moves and left click/drag are sent through a temporary virtual Wayland pointer on the TV. Coordinates are mapped aspect-correctly to 1920×1080, and clicks on the letterbox are ignored. A pressed button is always released on focus loss, window close, disconnect or heartbeat timeout.
- **Remote keys and text.** Arrows, OK, Home, Back, Play/Pause, volume and mute, as on-screen buttons or keyboard shortcuts. Text can be sent to the focused TV input field.
- **PC → TV casting.** Sunshine on the PC streams to Moonlight on the TV and is started from the app. Optional *Play audio on TV only* routes PC audio through Steam Streaming Speakers and restores your default audio devices afterwards.
- **Auto-connect.** When the TV is on, opening the app connects the control, video and pointer channels automatically and shows each one's status.

## Measured on the developer's setup

| What | Result | Scope |
| --- | --- | --- |
| Picture frame rate | 900 frames drawn in 30.0 s (≈30 fps), no drops | Actual EXE canvas draws |
| PC-side video path | p50 2.2 ms, p95 3.0 ms | Frame received by the backend → canvas draw (offline harness) |
| Mouse release → TV app event | p50 36 ms, p95 47 ms | Local API → trusted DOM click on the TV |
| Click → changed picture | p50 295 ms, p95 343 ms | Local API → first changed canvas frame; dominated by TV-side capture and encoding |
| App → backend HTTP request | ≈2 ms | Previously ≈2 s per request: `localhost` tried IPv6 first |

These numbers come from one PC and one TV. They are not the full time until light reaches your eyes: Windows composition and the panel scan-out are not included.

## How it works

```
Windows PC                                                Samsung TV (rooted, Tizen 7)
──────────                                                ────────────────────────────
TVCITATION.Control.exe  (WinForms + WebView2)
 ├─ player.js  WebCodecs → canvas   ◄── H.264 access units ── tv-native-stream   (display-clone encoder)
 └─ mouse / keys ─► backend  (Python, 127.0.0.1:18765)
                     ├─ pointer RPC ─────────────────────────► tv-native-input    (temporary Wayland pointer)
                     ├─ root agent session (samsung-tv-root, SDB)
                     └─ remote keys / text ──────────────────► TV remote API      (wss://TV:8002)
Sunshine (PC) ──────────────────────────────────────────────► Moonlight (TV)      (PC → TV casting)
```

The backend starts the two TV helpers through a root-agent session. Each helper gets a one-use session file with a random token. Each helper connects back to a fixed PC address and is stopped and cleaned up when the session ends. Details: [tv-control-app/README.md](tv-control-app/README.md).

## Repository layout

| Path | Contents |
| --- | --- |
| [`tv-control-app/`](tv-control-app/) | Windows app (.NET 10 WinForms + WebView2) and its offline UI/player checks |
| [`backend/`](backend/) | Local Python service: TV connection, video relay, mouse queue, casting/audio coordination, offline checks |
| [`tools/tv-native-stream/`](tools/tv-native-stream/) | TV-side H.264 screen sender (.NET 6, ARM32) |
| [`tools/tv-native-input/`](tools/tv-native-input/) | TV-side virtual mouse helper and cursor-policy companion (.NET 6, ARM32) |
| [`tools/pair-tv.py`](tools/pair-tv.py) | One-time pairing with the TV remote API; pins the TV certificate |
| [`tools/tv-boot-app/`](tools/tv-boot-app/) | Optional Tizen web app with a background health service (boot persistence test) |
| [`tools/tv-pointer-probe/`](tools/tv-pointer-probe/) | Tizen test page for pointer and latency measurements |
| [`tools/cast/deeplink-helper/`](tools/cast/deeplink-helper/) | TV-side helper that launches Moonlight straight into the PC desktop |
| [`desktop-cast/`](desktop-cast/) | Sunshine host scripts, TV-only audio module, experimental 4K120 virtual display |
| [`desktop-control/`](desktop-control/) | Older stand-alone remote app (no root needed) |
| [`research/samsung-tv-root`](https://github.com/chris-ritsen/samsung-tv-root) | Submodule: root agent and SDB client used by the backend |
| [`patches/`](patches/) | Local changes applied on top of the samsung-tv-root submodule |

## Requirements

**PC:** Windows 11 x64, [.NET 10 SDK](https://dotnet.microsoft.com/download), [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/), Python 3.14 with the `py` launcher, PowerShell 7, FFmpeg on `PATH`, and Git for Windows.

**TV:** Samsung QN90C on Tizen 7 (ARMv7) with its built-in .NET 6 runtime (`/usr/bin/dotnet`). Developer Mode must be on so SDB can connect, and the TV must be rooted with samsung-tv-root's `archive-root`.

**Third-party files you download yourself (not in this repo):**

| File | Put it at |
| --- | --- |
| Tizen SDB 4.2.36 for Windows (from the [Tizen SDK](https://developer.tizen.org/development/tizen-studio/download)) | `tools/sdb/data/tools/sdb.exe` |
| `websocket-client` 1.9.2 | `tools/python/` (see setup) |
| [Sunshine](https://github.com/LizardByte/Sunshine) portable build, only for casting | `tools/cast/sunshine/Sunshine/sunshine.exe` |
| [Moonlight for Tizen](https://github.com/brightcraft/moonlight-tizen), installed on the TV, only for casting | the TV |

## Setup

1. **Clone with the submodule and apply the local patch**
   ```powershell
   git clone --recurse-submodules https://github.com/sunnyQ99/samsung-tv-pc-connector.git
   cd samsung-tv-pc-connector
   git -C research/samsung-tv-root apply ../../patches/samsung-tv-root-local.patch
   py -3.14 -m pip install --target tools/python websocket-client==1.9.2
   ```
2. **Set your addresses.** The TV address `192.168.1.5` and PC address `192.168.1.75` are fixed constants. Search and replace them in `backend/server.py`, `backend/native_video.py`, `backend/native_input.py`, `tools/tv-native-stream/Program.cs`, `tools/tv-native-input/Program.cs` and `tools/pair-tv.py`. They are also in the optional `tools/tv-boot-app/*` and `desktop-control/*`. Give both devices fixed DHCP leases.
3. **Pair the remote API once.** Run `py -3.14 tools/pair-tv.py --remote-only` and accept the prompt on the TV. This stores the pairing token with Windows DPAPI in `%LOCALAPPDATA%\TVCITATION\samsung-token.dpapi`. It also records the TV's TLS certificate fingerprint in `diagnostics/tv-remote-result.json`; the backend pins that fingerprint.
4. **Root the TV** by following samsung-tv-root (Developer Mode, SDB, `archive-root`). Then build its two payloads where the backend expects them:
   ```powershell
   foreach ($p in 'SamsungTvArchiveRoot','SamsungTvRootAgent') {
     dotnet build "research/samsung-tv-root/payloads/common/$p.csproj" -c Release -f net6.0 -o diagnostics/root-analysis/self-built-net6/ready-net6
   }
   ```
5. **Build the TV helpers** (net6.0, AnyCPU). The backend uploads `out/` and checks the input helper against `out/manifest.json`:
   ```powershell
   dotnet build tools/tv-native-stream/TvNativeStream.csproj -c Release -o tools/tv-native-stream/out
   dotnet build tools/tv-native-input/NativeInput.csproj -c Release -o tools/tv-native-input/out
   $o = 'tools/tv-native-input/out'; $m = [ordered]@{}
   foreach ($f in 'NativeInput.dll','NativeInput.deps.json','NativeInput.runtimeconfig.json') { $m[$f] = (Get-FileHash "$o/$f" -Algorithm SHA256).Hash.ToLower() }
   $m | ConvertTo-Json | Set-Content "$o/manifest.json" -Encoding utf8
   ```
6. **Build and start the app**
   ```powershell
   dotnet publish tv-control-app/TVCitation.Control.csproj -c Release -r win-x64 --self-contained -o tv-control-app/publish
   ./tv-control-app/publish/TVCITATION.Control.exe
   ```
   The publish folder must stay a direct child of `tv-control-app/`: the app looks up to two parent folders for `backend/start-backend.ps1`. Turn the TV on, and the app starts the backend and connects by itself.
7. **Optional casting:** see [desktop-cast/README.md](desktop-cast/README.md).

## Offline checks (no TV needed)

```powershell
dotnet run --project tv-control-app/checks/TVCitation.Control.Checks.csproj -c Release -- $env:TEMP\tvc-ui-check
foreach ($c in 'audio_loss','native_input','native_input_policy','reconnect','sdb_health') { py -3.14 "backend/checks_$c.py" }
```

The UI check uses its own loopback mock server, an off-screen window and a temporary browser profile. The backend checks use fakes for the TV, SDB and PowerShell.

## Safety design

- **Firmware pinning.** The TV helpers verify the SHA-256 of every Samsung library they bind to, and stop on any mismatch instead of guessing an ABI.
- **Protected content.** DRM-protected content is never exported: the encoder's availability check runs on every frame, and a refusal stops capture.
- **Sessions and API access.**
  - Each TV helper session uses a one-use token, fixed peer addresses, a heartbeat, and parent-process checks.
  - The local API listens only on loopback and needs a per-user token.
  - WebView2 may load only the local player page.
- **Button release.** A held mouse button is released on focus loss, close, disconnect or timeout. The TV cursor setting that the helper enables is restored by an independent companion process, even if the main helper dies.
- **No persistent TV changes.** Nothing on the TV's system partition is modified; the helpers run from `/run/samsung-tv-root/…`.

## Known limitations

- One verified TV model and firmware. Other Samsung models need their own library hashes, offsets and testing.
- Fixed IP addresses, and Windows only.
- Mouse support is move plus left button only: no right/middle button or wheel. Some TV apps ignore mouse input; use the remote keys there.
- The capture runs at 30 fps; a 60 fps request still produced 30 fps on this TV.
- Not validated: 4K120 PC → TV casting, recovery after a full TV power cycle, and long-term endurance.

## Credits

[samsung-tv-root](https://github.com/chris-ritsen/samsung-tv-root) (Unlicense) provides the root agent and SDB client. [Sunshine](https://github.com/LizardByte/Sunshine) and [Moonlight for Tizen](https://github.com/brightcraft/moonlight-tizen) handle PC → TV casting. TV-only audio uses Valve's Steam Streaming Speakers driver. The Tizen SDK tools are Samsung's.

## License

[MIT](LICENSE)
