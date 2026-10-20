# PC → TV casting host

The PC side of casting is [Sunshine](https://github.com/LizardByte/Sunshine), running as a separate process under the current Windows user. The TV side is [Moonlight for Tizen](https://github.com/brightcraft/moonlight-tizen).

On the developer's PC (RTX 5070 Ti), these were verified: host startup, NVENC initialization, the local admin API, TV pairing and 1080p60 desktop streaming. TV-only audio was also confirmed by ear, along with restoring all three default audio roles afterwards. 4K120 has not been validated.

Normally the Windows app starts and stops casting for you. The scripts below are for setup and manual control.

## Setup

1. Download the Sunshine portable build and extract it to `tools/cast/sunshine/Sunshine/` so that `sunshine.exe` is at `tools/cast/sunshine/Sunshine/sunshine.exe`.
2. Run `desktop-cast/Initialize-CastHost.ps1` once in PowerShell 7. It generates `sunshine.conf` and `apps.json` next to the scripts; both are machine-specific and not tracked. It also creates the private state folder `%LOCALAPPDATA%\TVCITATION\Sunshine`.
3. Install Moonlight on the TV and pair it with the host (see below).
4. Build [`tools/cast/deeplink-helper`](../tools/cast/deeplink-helper/README.md) and copy it to the TV, so the app can open Moonlight straight into the desktop. Then set `MOONLIGHT_SERVER_UID` (Sunshine `serverinfo` `<uniqueid>`) and `MOONLIGHT_DESKTOP_APP_ID` (the numeric id of the Desktop app) at the top of `backend/server.py`.

## Use

```powershell
pwsh -NoProfile -File .\desktop-cast\Start-CastHost.ps1
pwsh -NoProfile -File .\desktop-cast\Stop-CastHost.ps1
```

- **Start** is idempotent: if this app's host is already running, it returns its PID. If another Sunshine process is running, it refuses to start a competing one.
- **Stop** only matches the binary in this folder together with this configuration file.

The admin UI is at `https://localhost:47990`. The host advertises itself to the TV as **TVCITATION PC** with a single **Desktop** app. Nothing is set to start at boot.

`Invoke-CastHostApi.ps1` wraps certificate-pinned login to the local API, so passwords never appear on the command line:

```powershell
& '.\desktop-cast\Invoke-CastHostApi.ps1' -Path '/api/apps'
& '.\desktop-cast\Invoke-CastHostApi.ps1' -Path '/api/pin'
```

To pair:

1. Start pairing from Moonlight on the TV.
2. Read `/api/pin` to get the pending record.
3. Submit its `pairing_id` (32 hex characters in this Sunshine version), the 4-digit PIN shown on the TV, and a name:

```powershell
& '.\desktop-cast\Invoke-CastHostApi.ps1' -Method POST -Path '/api/pin' -Body @{
    pairing_id = '<pending record id>'
    pin = '<4-digit PIN shown on the TV>'
    name = 'Samsung TV'
}
```

## Configuration and credentials

- `sunshine.conf` and `apps.json` contain no commands that launch external programs.
- `%LOCALAPPDATA%\TVCITATION\Sunshine` holds pairing state, certificates and credentials. Its ACL allows only the current user and SYSTEM.
- `web-credential.clixml` is protected with the current user's DPAPI key; `Import-Clixml` returns a PSCredential only for that account. `web-credentials.json` is Sunshine's own salted hash, not a plain password.
- UPnP, gamepad input, automatic audio-driver install and automatic display-mode changes are disabled.
- The admin API accepts localhost only. Without credentials it answers HTTP 401 locally and HTTP 403 on the LAN address.
- Sunshine is not installed as a service. TV-only audio uses Valve's signed Steam Streaming Speakers driver; see [AUDIO.md](AUDIO.md). Sunshine changes only its own NVIDIA application profile, not global NVIDIA settings.
- On the developer's PC, NVENC initialized H.264, HEVC and AV1, and the 10-bit HEVC/AV1 probes passed. An encoder probe at 1080p60 says nothing about sustained 4K120. For a native 4K source, see the experimental [virtual-display](virtual-display/README.md) scripts.

References: [Sunshine configuration](https://docs.lizardbyte.dev/projects/sunshine/latest/md_docs_2configuration.html), [confighttp.cpp](https://github.com/LizardByte/Sunshine/blob/v2026.914.233613/src/confighttp.cpp), [NVIDIA profile handling](https://github.com/LizardByte/Sunshine/blob/v2026.914.233613/src/platform/windows/nvprefs/driver_settings.cpp).
