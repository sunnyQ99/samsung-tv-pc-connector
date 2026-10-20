# TV-only audio module

These scripts check audio devices, perform a controlled driver install, back up the Sunshine configuration, and restore the three default output roles.

They never start Sunshine, never elevate themselves, never contact the TV, and never mute the Windows master volume.

All entry points need **64-bit PowerShell** (PowerShell 7 and Windows PowerShell 5.1 both load them). On normal execution stdout is a single JSON object. Callers must check `status`: exit code 0 alone does not mean success. Deploy the scripts together with `CastAudio.Common.ps1`, `CastAudioInterop.cs` and `SteamRootDevice.cs`.

## Call order used by the backend

The backend serializes start, stop and install operations behind one lock. Every session gets a new snapshot path; the defaults captured at the first install are never reused as the restore target for later sessions.

1. **Stop this app's Sunshine.** Confirm every `sunshine` process has exited; if another instance is still running, abort the configuration change.
2. **Check the device.** Run `Enable-TvOnlyAudio.ps1 -CheckOnly`. Only `ready` returns a usable `endpointId`.
3. **Elevate if needed.**
   - On `needsElevation`, the backend starts the same script through UAC, *without* `-CheckOnly`, and passes a unique `-ResultPath`. The script never shows UAC itself.
   - The backend uses a hidden window, waits for the process to exit, then reads the result file.
   - A cancelled UAC prompt, a missing file, or any `status` other than `ready` means TV-only audio is not available.
4. **Save the defaults.** Run `Save-CastAudioDefaults.ps1 -SnapshotPath <unique per-session JSON>`; it must return `saved`. It refuses to overwrite an existing snapshot.
5. **Configure Sunshine.** Run `Set-CastAudioMode.ps1 -Mode TvOnly -EndpointId <from ready> -ReceiptPath <receipt JSON> [-ConfigPath <sunshine.conf>]`. Accept `configured`, or `alreadyConfigured` when it matches the current configuration.
6. **Start Sunshine.**
   - In Moonlight, *Play audio on host* is off (`localAudioPlayMode=0`), and TV audio stays on.
   - When a real audio session starts, Sunshine switches the default playback device to the virtual speaker.
7. **On Stop casting:**
   - End the stream and this app's Sunshine, then run `Restore-CastAudioDefaults.ps1 -SnapshotPath <this session's snapshot>`.
   - Also run `Set-CastAudioMode.ps1 -Mode Restore -ReceiptPath <receipt> [-ConfigPath ...]` to restore the original configuration.
   - The two restores are independent: one failing never skips the other.

**Closing the GUI does not run the stop/restore steps.** A cast that is still playing keeps going. Restoring outputs during an active cast would make the PC audible again. After a crash the session snapshot is kept, and the backend offers or performs the restore next time; it is never overwritten.

## Entry points

| Script | Parameters | Main statuses |
| --- | --- | --- |
| `Get-CastAudioState.ps1` | none | `ok` with `administrator`, `sunshineRunning`, `defaults`, `endpoints`, `steamEndpoints` |
| `Enable-TvOnlyAudio.ps1` | optional `-CheckOnly`, `-ReceiptDirectory`, `-ResultPath` | `ready`, `needsElevation`, `needsInstallation`, `deviceRegistrationFailed`, `installFailed`, `endpointMissing`, `ambiguousEndpoint`, `driverBindingUnverified`, `restoreIncomplete`, `error` |
| `Save-CastAudioDefaults.ps1` | required `-SnapshotPath` | `saved`, `error` |
| `Restore-CastAudioDefaults.ps1` | required `-SnapshotPath`; optional `-CheckOnly` | `restored`, `restoreIncomplete`, `checkOnly`, `error` |
| `Set-CastAudioMode.ps1` | `-Mode TvOnly\|Restore`; required `-ReceiptPath`; `-EndpointId` for TvOnly; optional `-ConfigPath`, `-CheckOnly` | `configured`, `alreadyConfigured`, `restored`, `alreadyRestored`, `wouldConfigure`, `wouldRestore`, `error` |

`Enable -ResultPath` writes exactly the same JSON as stdout. Give it a fresh absolute path that the current user can read. The default install-receipt folder is `%LOCALAPPDATA%\TVCITATION\CastAudio`. If UAC runs under a different administrator account, pass the original user's receipt folder explicitly so the restore data is not lost.

## Driver install rules

**Source files.**

- The install reads only `%CommonProgramFiles(x86)%\Steam\drivers\Windows10\x64\SteamStreamingSpeakers.inf` and the SYS/CAT files next to it. It accepts no external INF parameter and downloads nothing.
- It requires a valid embedded Valve SYS signature, or a valid signature with the same certificate as the verified Microsoft hardware CAT. INF and SYS must also pass catalog-membership checks against that CAT.
- After Windows installs the CAT, [Get-AuthenticodeSignature prefers the catalog signature](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.security/get-authenticodesignature). A Microsoft signature after install is therefore not treated as a change of origin.

**Device node.**

- A non-administrator without a usable endpoint gets `needsElevation`.
- An administrator first looks for an existing media node with hardware ID `ROOT\SteamStreamingSpeakers`, including disabled and non-present nodes.
- If none exists, it creates the fixed instance `ROOT\SteamStreamingSpeakers\0000` named in the INF through Microsoft SetupAPI, sets the double-NUL-terminated hardware ID and runs `DIF_REGISTERDEVICE`.
- An existing node is reused. Several matching nodes, or a conflict on the fixed instance, stop the install. No duplicate device is created and a disabled device is never force-enabled.

**Driver binding.**

- `DiInstallDriverW` follows. Only native results 0 and 259 are accepted, with up to 20 s of waiting.
- `ready` requires all of these:
  - exactly one active render endpoint;
  - its `DEVPKEY_Device_Parent` pointing at the single Steam root node;
  - the node started with no problem code;
  - the signed PnP driver's INF equal to the published INF whose hashes match the trusted source INF/CAT.
- `CheckOnly` on an existing endpoint verifies the same links; a device name alone is never enough.

**Results and receipts.**

- `driverCallSucceeded=true` only means Windows accepted the driver-install call.
- Result 259 (`ERROR_NO_MORE_ITEMS`) was observed after asynchronous PnP had already installed the package. It is accepted only when every link check above passes, and stays visible as `driverCallSucceeded=false`, `installError=259`, `nativeWarning`.
- A driver that is only staged without a playback device gives `endpointMissing`, not `ready`.
- A required reboot is reported as `rebootRequired=true`; the script never reboots.
- The driver receipt (schemaVersion 3) records:
  - the three default roles before and after;
  - PnP instance and INF inventories, and newly added instances;
  - root-node creation and registration stages and errors;
  - the matching driver-store / published INF, the full `verification`, and the final endpoint;
  - native error codes and warnings, and the default-device restore result.
- The receipt is saved immediately after root registration, before binding the driver.
- The install always tries to restore the three default roles, even if it failed.
- Drivers are never uninstalled automatically. A registered node whose install later failed is kept and recorded for reuse.

## Configuration and defaults

- **Sunshine configuration.** `Set-CastAudioMode` changes only `virtual_sink` and `install_steam_audio_drivers=disabled`. The original configuration bytes and their SHA-256 are kept beside the receipt. A restore happens only while the current configuration still matches what was applied, so later edits are never overwritten. Changes, snapshots and restores all refuse to run while Sunshine is running.
- **Default outputs.** The snapshot covers the Console, Multimedia and Communications roles. A restore validates the full snapshot first; if an original device is gone, it returns `restoreIncomplete` instead of picking another one. Default outputs are set through the same Windows PolicyConfig COM interface that Sunshine uses. Microsoft gives no stability promise for it, so re-check after Windows updates.
- **Limits of TV-only audio.** It relies on applications following the Windows default output. An app bound explicitly to a physical speaker may still play on the PC. Per-app routing and existing Voicemeeter routing are not touched.

References: [Sunshine Windows audio](https://github.com/LizardByte/Sunshine/blob/v2026.914.233613/src/platform/windows/audio.cpp), [DiInstallDriverW](https://learn.microsoft.com/en-us/windows/win32/api/newdev/nf-newdev-diinstalldriverw), [SetupGetInfPublishedNameW](https://learn.microsoft.com/en-us/windows/win32/api/setupapi/nf-setupapi-setupgetinfpublishednamew), [DevCon install](https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/devcon-install), [SetupDiCreateDeviceInfoW](https://learn.microsoft.com/en-us/windows/win32/api/setupapi/nf-setupapi-setupdicreatedeviceinfow), [SetupDiCallClassInstaller](https://learn.microsoft.com/en-us/windows/win32/api/setupapi/nf-setupapi-setupdicallclassinstaller).

## Python coordinator

`backend/cast_audio.py` exports `CastAudioCoordinator`. It defaults to the project paths and `%LOCALAPPDATA%\TVCITATION\Control`, which the backend initializes. Constructing or importing it creates no files and changes no settings.

- **`prepare(tv_only: bool) -> dict`**
  - With no active session, it stops this app's Sunshine, saves the current defaults, requests UAC once if needed, applies the audio configuration and restarts Sunshine.
  - On success it returns `ok=true, status=prepared`; the caller then starts the TV stream, and calls `stop()` to roll back if that fails.
  - With an active session in the same audio mode it returns `alreadyActive`. A different or unknown mode is refused, so the user must stop casting first.
- **`stop() -> dict`**
  - Calls Sunshine's local close API, stops this app's Sunshine, then restores defaults and configuration independently.
  - Calling it again never applies an old snapshot over defaults the user changed since.
- **`status() -> dict`**
  - Returns `tvOnlyAudio`, `audioPolicyKnown`, `needsRecovery`, `operationInProgress`, `audioStage`, `castActive` and more.
  - It does not wait for the operation lock, so it can be polled while a UAC prompt is open.

The main receipt is `audio-session.json` in the restricted folder. Snapshots and configuration backups use unique per-session names.

Waiting for UAC has no forced timeout, so a running elevated install cannot be interrupted by a parallel restore. An HTTP or GUI timeout is not a UAC cancellation: the backend keeps showing the state until the result arrives.

**Stream-loss recovery** (watcher in `server.py`):

- It acts only on a `prepared`, `tvOnlyAudio=true` session whose defaults and configuration are backed up, and only after it saw `streamConnected=true` with the correct host ownership.
- A clear client disconnect, application exit or host loss must last about 5 s. The watcher then re-checks the session, host ownership and client state before calling `stop()`.
- A brief HTTP failure alone is not treated as stream loss. A busy cast task, lock or audio operation resets the debounce. A client that reconnects first cancels the recovery.
- Another Sunshine instance, an unknown state or a failed restore shows "needs restoring"; each session gets one automatic attempt.
- After a backend restart, an old `prepared` record is never used for automatic recovery unless that session's stream is observed again.

The offline checks in `backend/checks_audio_loss.py` cover these paths.
