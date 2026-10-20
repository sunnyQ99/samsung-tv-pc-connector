# Experimental 4K120 virtual display

**Status: experimental and not validated end to end.** These scripts add a separate 3840×2160 @ 120 Hz *extended* desktop through the open-source [Virtual Display Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver), so Sunshine can capture a native 4K120 source. The install and mode switch have not passed a full on-device acceptance, and 4K120 streaming to the TV has not been verified.

The physical monitor keeps its own mode. The new display is an extended desktop: existing windows do not appear on it automatically. Move the video or browser window there, or open it there. Upscaling a 1080p desktop does not count as a native 4K source.

## Pinned driver package

- **Release.** The official release [25.7.23](https://github.com/VirtualDrivers/Virtual-Display-Driver/releases/tag/25.7.23) (`VirtualDisplayDriver-x86.Driver.Only.zip`). The INF is NTamd64 and the PE machine is 0x8664; the driver version is 11.30.4.434.
- **Hashes.** The archive SHA-256 is pinned in `VddPackage.ps1`: `E24210692B442B39AF763536330CE78B423F19342B7A7792C26DE3944E418B3A`.
- **Signature.** Authenticode for the CAT and DLL must be valid, and the INF and DLL must pass `WinVerifyTrust` catalog-membership checks.
  - The leaf certificate is SignPath Foundation, thumbprint `3CF8CF26D8BA266C3A483AB7D26D4A818E317D76`.
  - The chain is SignPath → GlobalSign GCC R45 CodeSigning CA 2020 → GlobalSign Code Signing Root R45.
  - This is not a Microsoft WHQL signature.
- **`-TrustPublisher`.** It adds only that verified leaf certificate to TrustedPublisher. It never adds a root CA, disables Secure Boot or HVCI, or enables test signing.

## Commands

Read-only check:

```powershell
pwsh -NoProfile -File .\desktop-cast\virtual-display\Manage-Vdd.ps1 -Action Check
```

Install from an elevated PowerShell; the script never prompts for UAC itself:

```powershell
pwsh -NoProfile -File .\desktop-cast\virtual-display\Manage-Vdd.ps1 -Action Install -TrustPublisher
```

`Install-Vdd-Elevated.ps1` is a fixed, argument-free entry point for `Start-Process -Verb RunAs`. It runs only the install command above, with no arbitrary path or command parameters. It writes its logs and result JSON under `diagnostics/cast/`.

**Install rules.**

- Only the fixed instance `ROOT\MTTVDD\0000` in the Display class is used. Any existing MttVDD device, folder or configuration stops the install.
- Files and the configuration go to `%ProgramData%\TVCITATION\VirtualDisplayDriver`, writable only by Administrators and SYSTEM. The driver's documented `VDDPATH` registry value points there.
- Only the display driver is installed, not the bundled virtual audio device.
- Acceptance requires all of these:
  - the node is started with no problem code, with the exact version and provider;
  - the published INF and the single DriverStore copy match the pinned hashes;
  - the installed files pass the signature checks again.
- **Physical displays.** Their identity, primary flag, resolution, refresh rate and position must stay unchanged, or the new device is removed again.

**Switching to 4K120.** Test the target mode first, then apply it:

```powershell
pwsh -NoProfile -File .\desktop-cast\virtual-display\Set-VddExtended4K120.ps1
pwsh -NoProfile -File .\desktop-cast\virtual-display\Set-VddExtended4K120.ps1 -Apply
```

- **Choosing the output.** The helper accepts exactly one non-primary VDD output and picks 3840×2160 / 120 Hz / 32 bpp / progressive from the modes Windows actually enumerates.
- **Apply.** It runs `CDS_TEST` first, then places the virtual display to the right of all physical displays.
  - It writes no persistent display configuration and never changes the primary display.
  - A separate 30 s rollback process restores the previous state unless the new mode reads back correctly and is committed.

Then set Sunshine's `output_name` to the verified VDD output, read from Sunshine's log or `dxgi-info.exe`; do not guess a GUID or display number. Keep `dd_configuration_option`, `dd_resolution_option` and `dd_refresh_rate_option` disabled, and back up the original configuration so it can be restored.

## Removal

1. Stop any stream that uses the virtual display.
2. Restore Sunshine's original `output_name`.
3. From an elevated PowerShell run:

```powershell
pwsh -NoProfile -File .\desktop-cast\virtual-display\Manage-Vdd.ps1 -Action Remove
```

What gets removed:

- Only the `ROOT\MTTVDD\0000` device that the receipt says this tool created, with a matching hardware ID. There are no wildcards and no forced driver deletion.
- Only the oem driver package this install added, if its INF hash still matches; removal stops if the package is still in use.
- The TrustedPublisher leaf certificate and the `VDDPATH` value this install added. Registry keys are deleted only when they are empty and owned by this install.

`Retry-Vdd-Elevated.ps1` re-runs an install after a clean, receipt-verified removal. It applies only when every pre-check passes: same package, same physical displays, no leftover device or unknown registry content.

## Notes

- The registry helper (`VddRegistry.cs`) opens `HKLM\SOFTWARE` only with `KEY_CREATE_SUB_KEY`; PowerShell's `New-Item` asks for broader rights there. Created keys are tracked for exact removal, and ACLs are never changed.
- `SetupVerifyInfFile` may return FALSE with `ERROR_AUTHENTICODE_TRUSTED_PUBLISHER` (0xE0000241) for valid non-WHQL packages; that result is accepted. Any other error, such as 0xE0000242, is rejected.
- WMI `Win32_PnPSignedDriver.IsSigned` can be false for this package even when the direct Authenticode checks pass. That discrepancy is recorded and not used as a pass or fail signal.
- The driver's named-pipe control interface allows broad local access. These scripts never use it to change configuration.
