# Windows control app

WinForms + WebView2 app (`TVCITATION.Control.exe`, .NET 10). It shows the live TV picture, a remote control, text input and PC → TV casting, and talks only to the local backend at `http://127.0.0.1:18765`.

## Connection, input and audio

- **Startup.** By default the app prepares the local backend (`backend/start-backend.ps1`) and sends `/connect`. With the TV already on and the backend ready, control, video and pointer were ready about 14 s after launch. Each channel reports its own status; startup is not instant.
  - If the backend restarts, the GUI requests the connection again.
  - After you click **Disconnect** it stays disconnected.
  - `--manual-connect` turns off the first automatic connection.
- **Mouse.** Mouse input is forwarded only when all of these hold: `pointerAvailable=true`, *Mouse control* is checked, and the picture has focus.
  - Coordinates map to 1920×1080 with the aspect ratio preserved. Clicks on the letterbox bars are ignored.
  - Supported: move, left down and left up. Right, middle and wheel are disabled.
  - Moves are throttled to one every 32 ms, with a trailing send so the resting position always arrives. At most one move is in flight, and only the latest pending point is kept.
  - Down and up keep their order on a dedicated pointer queue, separate from remote, text and cast requests.
- **Keyboard and text.**
  - Remote keys come from an explicit allow-list.
  - Text input needs a focused text field on the TV.
- **Not every TV app accepts the mouse.** `pointerAvailable` means the input device and the cursor-policy session are ready, not that every app reacts to the mouse. If a position check fails before a click, the click is refused and that input session ends.
- **TV-only audio.** *Play audio on TV only* applies to the next cast; changing it does not interrupt a running cast. `/cast/start` returns HTTP 202, and progress is reported through `castOperation`. The app only shows "casting" once fresh stream evidence confirms `castActive=true`. **Stop casting** ends the stream and restores the default audio devices and the Sunshine configuration; a failed restore asks you to retry.
- **Closing the window.**
  1. The app stops accepting new input.
  2. It gives an accepted press and its release up to 700 ms to finish; a release queued by focus loss counts and is not sent twice.
  3. It cancels ordinary requests and sends `/disconnect` with its own 500 ms budget, so the backend cleans up TV control, the pointer and the TV → PC video.
  4. A running PC → TV cast and the shared backend are left untouched.
  5. If the request fails, the backend heartbeat and native disconnect handling still clean up.
- **Audio recovery after a dropped stream.** If a confirmed TV-only cast drops for about 5 s, the backend tries to restore PC audio. It does not touch anything while an operation is in progress, the state is unknown, or another host instance is running.

## Live video

The preferred path is:

1. The TV's native TVCN2 channel sends complete Annex-B H.264 access units.
2. The app reads them from the authenticated `/video-frames` endpoint.
3. WebCodecs decodes them, preferring software decoding.
4. Each frame is drawn to a canvas immediately.

There is no screenshot polling. The codec string comes from the real SPS. `controlConnected`, `videoConnected`, `nativeFramesAvailable`, `mseVideoAvailable` and the pointer state are reported separately.

- **Replay and resync.** The direct replay ring holds at most 120 frames or 8 MiB, and late joiners start from a keyframe that carries SPS/PPS. A reader that falls out of the ring is disconnected and reconnects. The player validates the TVF2 header, sequence, length and key flag. It resynchronizes when 6 frames wait for decoding or the decoder queue reaches 4, so a backlog cannot build up. Frame rates shown are counted from actual decodes and draws, not taken from the configured 30 fps.
- **MSE fallback.** If WebCodecs is unavailable or the direct path fails, the player falls back to an FFmpeg copy remux → fMP4 → `/video` → Media Source Extensions.
  - FFmpeg is fed from its own bounded queue (30 frames / 4 MiB); congestion disables the MSE path without blocking direct frames.
  - The MSE replay window is 10 s / 16 MiB. The player keeps about 9 s of history and jumps near the live edge when it lags more than 300 ms.
  - These buffers apply only to the fallback, not to the direct canvas path.
- **No audio on this channel.** The TV → PC channel carries video only and never changes Windows or PC → TV audio.
- **Diagnostics.** Set `TVCITATION_PLAYBACK_DIAGNOSTICS=1` to write bounded `videoPerf` metrics (renderer, frame rates, decode latency, queues) to `%LOCALAPPDATA%\TVCITATION\Control\playback-metrics-<pid>.json`. No pixels or input contents are recorded.

## Authentication and resource limits

- **Local API.** The backend address is fixed to `http://127.0.0.1:18765`, and `--backend-url` accepts only loopback HTTP. The GUI reads the per-user token from `%LOCALAPPDATA%\TVCITATION\Control\api-token` and sends it in `X-TVCitation-Token`; it never appears in URLs, page source or logs.
- **WebView2.** WebView2 loads only `/player` and resources from the same backend. External resources, new windows, downloads and permission requests are blocked.
- **Video receiver.** The backend's video receiver binds the PC address on a random port in 49152–65535. It accepts only the TV address and a one-use token, sends a heartbeat every 2 s, and a stop command or disconnect triggers native cleanup on the TV.
  - The TV sender holds an exclusive lock.
  - Before a retry, the backend confirms the old sender (recorded PID, start time, arguments) has exited. It never kills processes by name.
- **Mouse session.** After the mouse session authenticates, a separate cursor-policy companion process starts on the TV. It holds a global lock and checks the identity of the input and root processes.
  - Only valid PC commands renew its 6 s lease.
  - Lease expiry, parent exit or an explicit release restores the original cursor policy.
  - If the original value was `False`, it is restored and read back. If it was `True`, it is left for its external owner.

## Build and checks

```powershell
dotnet build .\tv-control-app\TVCitation.Control.csproj -c Release
dotnet run --project .\tv-control-app\checks\TVCitation.Control.Checks.csproj -c Release -- <output-folder>
dotnet run --project .\tv-control-app\checks\TVCitation.Control.Checks.csproj -c Release -- <output-folder> --playback-only
```

The checks use a temporary browser profile, an off-screen window and a loopback mock, and never start the real backend or touch the TV.

- **Default run:** layout rendering (saved as PNGs in the output folder), coordinate mapping, authentication, reconnect after a backend restart, casting status and WebView2 resource blocking.
- **`--playback-only`:** about 33 s with the default autoplay policy, covering real 1080p decoding, EOF and status-timeout reconnects, and resource release.

Publish a self-contained build with `dotnet publish -c Release -r win-x64 --self-contained -o tv-control-app/publish`. Keep the whole publish folder, as a direct child of `tv-control-app/`. The app also needs the rest of this repository (`backend`, `desktop-cast`, `research/samsung-tv-root`, `tools`) and the PC requirements listed in the [main README](../README.md). It is not a stand-alone installer.
