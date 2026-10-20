[CmdletBinding()]
param(
    [switch]$CheckOnly,
    [string]$ReceiptDirectory = (Join-Path $env:LOCALAPPDATA 'TVCITATION\CastAudio'),
    [string]$ResultPath
)
# The script never elevates itself, starts Sunshine, or downloads a driver.
try {
    . (Join-Path $PSScriptRoot 'CastAudio.Common.ps1')
    $found = @(Get-CastAudioSteamEndpoints)
    if ($found.Count -gt 1) { throw 'ambiguousEndpoint: More than one active Steam Streaming Speakers endpoint was found.' }
        # No caller-selected INF path is accepted. Bind to Steam's installed, signed package.
        $driverDirectory = Join-Path ([Environment]::GetFolderPath('CommonProgramFilesX86')) 'Steam\drivers\Windows10\x64'
        $inf = Join-Path $driverDirectory 'SteamStreamingSpeakers.inf'
        $sys = Join-Path $driverDirectory 'SteamStreamingSpeakers.sys'
        $cat = Join-Path $driverDirectory 'steamstreamingspeakers.cat'
        foreach ($file in @($inf,$sys,$cat)) { if (-not [IO.File]::Exists($file)) { throw "driverPackageMissing: $file" } }
        $sysSignature = Get-AuthenticodeSignature -LiteralPath $sys
        $catSignature = Get-AuthenticodeSignature -LiteralPath $cat
        if ($catSignature.Status -ne 'Valid' -or $catSignature.SignerCertificate.Subject -notmatch 'CN=Microsoft Windows Hardware Compatibility Publisher(,|$)') { throw 'untrustedCatalog: Expected a valid Microsoft hardware catalog signature.' }
        # After installation PowerShell prefers the installed Windows catalog signature over the
        # embedded Valve signature. Both routes must still match this verified catalog below.
        $valveSigner = $sysSignature.SignerCertificate -and $sysSignature.SignerCertificate.Subject -match '(^|,\s*)O=Valve (Corp\.|Corporation)(,|$)'
        $catalogSigner = $sysSignature.SignerCertificate -and $sysSignature.SignerCertificate.Thumbprint -eq $catSignature.SignerCertificate.Thumbprint
        if ($sysSignature.Status -ne 'Valid' -or (-not $valveSigner -and -not $catalogSigner)) { throw 'untrustedDriver: Expected a valid Valve signature or the verified Microsoft hardware catalog signer.' }
        if (-not [TVCitation.CastAudio.Driver]::IsCatalogMember($cat,$inf) -or -not [TVCitation.CastAudio.Driver]::IsCatalogMember($cat,$sys)) { throw 'untrustedPackage: INF or driver does not match the signed catalog.' }
        $infText = [IO.File]::ReadAllText($inf)
        if ($infText -notmatch '(?im)^\[DeviceInstall32\]\s*$' -or $infText -notmatch '(?im)^AddDevice\s*=\s*ROOT\\SteamStreamingSpeakers\\0000' -or $infText -notmatch '(?im)^ClassGUID\s*=\s*\{4d36e96c-e325-11ce-bfc1-08002be10318\}\s*$' -or $infText -notmatch '(?im)^HardwareIds\s*=\s*ROOT\\SteamStreamingSpeakers\s*$') { throw 'unsupportedPackage: Expected Steam media root-device installation section.' }
        $rootDevices = @([TVCitation.CastAudio.SteamRootDevice]::Find())
        if ($rootDevices.Count -gt 1) { throw 'ambiguousRootDevice: More than one Steam Streaming Speakers hardware node exists. No installation was attempted.' }
        $package = [ordered]@{inf=$inf; sha256=(Get-FileHash -LiteralPath $inf -Algorithm SHA256).Hash; catalogSha256=(Get-FileHash -LiteralPath $cat -Algorithm SHA256).Hash; trusted=$true; hardwareId=[TVCitation.CastAudio.SteamRootDevice]::HardwareId; rootDeviceRequired=($rootDevices.Count -eq 0); existingRootDevices=$rootDevices}
        if ($found.Count -eq 1) {
            $verification = Get-CastAudioSteamInstallation $inf
            $result = [ordered]@{status=$verification.status; endpointId=$(if ($null -ne $verification.endpoint) {$verification.endpoint.Id} else {$null}); endpoint=$verification.endpoint; installed=$false; package=$package; verification=$verification}
        } elseif (-not (Test-CastAudioAdministrator)) {
            $result = [ordered]@{status='needsElevation'; endpointId=$null; package=$package; checkOnly=[bool]$CheckOnly; message='Administrator approval is required to install Steam Streaming Speakers. No installation was attempted.'}
        } elseif ($CheckOnly) {
            $result = [ordered]@{status='needsInstallation'; endpointId=$null; package=$package; checkOnly=$true}
        } else {
            Assert-CastAudioSunshineStopped
            $receiptPath = Join-Path ([IO.Path]::GetFullPath($ReceiptDirectory)) ('driver-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfff') + '-' + [guid]::NewGuid().ToString('N') + '.json')
            $beforeDrivers = @(Get-CastAudioSteamDrivers)
            $receipt = [ordered]@{schemaVersion=3; kind='castAudioDriverInstall'; createdUtc=[DateTime]::UtcNow.ToString('o'); status='pending'; package=$package; defaults=@([TVCitation.CastAudio.Audio]::GetDefaults()); beforeDrivers=$beforeDrivers; afterDrivers=@(); newDrivers=@(); beforeRootDevices=$rootDevices; afterRootDevices=@(); rootRegistration=$null; beforePackage=[TVCitation.CastAudio.Driver]::GetPublishedDriver($inf); afterPackage=$null; verification=$null; endpoint=$null; installError=$null; nativeWarning=$null; rebootRequired=$false; restoreResults=@(); error=$null}
            Save-CastAudioJsonFile $receiptPath $receipt
            try {
                # DiInstallDriverW only binds existing devices. Register the fixed Valve hardware ID first.
                # Existing nodes (including disabled/non-present nodes) are reused, never duplicated or force-enabled.
                $receipt.rootRegistration = [TVCitation.CastAudio.SteamRootDevice]::EnsureRegistered()
                $receipt.rebootRequired = $receipt.rootRegistration.RebootRequired
                Save-CastAudioJsonFile $receiptPath $receipt
                if ($receipt.rootRegistration.Error -eq 0) {
                    $reboot = $false
                    $receipt.installError = [TVCitation.CastAudio.Driver]::Install($inf,[ref]$reboot)
                    $receipt.rebootRequired = $receipt.rebootRequired -or $reboot
                }
                # PnP can finish the INF's root-device installation while DiInstallDriver is still
                # enumerating updates. ERROR_NO_MORE_ITEMS (259) then means no better driver was
                # updated in the observed Windows path. It is not success by itself: verify all
                # signed-package, device and endpoint associations, and preserve the native code.
                if ($receipt.rootRegistration.Error -eq 0 -and $receipt.installError -in @(0,259)) {
                    $deadline = [DateTime]::UtcNow.AddSeconds(20)
                    do {
                        $receipt.verification = Get-CastAudioSteamInstallation $inf
                        if ($receipt.verification.status -in @('ready','ambiguousEndpoint')) { break }
                        Start-Sleep -Milliseconds 500
                    } while ([DateTime]::UtcNow -lt $deadline)
                }
                $receipt.afterDrivers = @(Get-CastAudioSteamDrivers)
                $receipt.afterRootDevices = @([TVCitation.CastAudio.SteamRootDevice]::Find())
                $receipt.afterPackage = [TVCitation.CastAudio.Driver]::GetPublishedDriver($inf)
                $beforeKeys = @($beforeDrivers | ForEach-Object { $_.DeviceID + '|' + $_.InfName })
                $receipt.newDrivers = @($receipt.afterDrivers | Where-Object { ($_.DeviceID + '|' + $_.InfName) -notin $beforeKeys })
                if ($receipt.rootRegistration.Error -ne 0) { $receipt.status='deviceRegistrationFailed'; $receipt.error=$receipt.rootRegistration.Stage + ': ' + $receipt.rootRegistration.Message }
                elseif ($receipt.installError -notin @(0,259)) { $receipt.status='installFailed' }
                else {
                    $receipt.status=$receipt.verification.status
                    if ($receipt.status -eq 'ready') {
                        $receipt.endpoint=$receipt.verification.endpoint
                        if ($receipt.installError -eq 259) { $receipt.nativeWarning='DiInstallDriver returned ERROR_NO_MORE_ITEMS (259); the exact signed package, started device and active endpoint were independently verified.' }
                    }
                }
            } catch {
                $receipt.status='error'; $receipt.error=$_.Exception.Message
            } finally {
                # Preserve each Windows role separately; do not collapse communications into multimedia.
                try {
                    Assert-CastAudioSunshineStopped
                    $receipt.restoreResults = @(Restore-CastAudioRoleSnapshot $receipt.defaults)
                    if (@($receipt.restoreResults | Where-Object HResult -lt 0).Count) { $receipt.status='restoreIncomplete' }
                } catch { $receipt.status='restoreIncomplete'; $receipt.error=$_.Exception.Message }
                Save-CastAudioJsonFile $receiptPath $receipt
            }
            $result = [ordered]@{status=$receipt.status; endpointId=$(if ($null -ne $receipt.endpoint) {$receipt.endpoint.Id} else {$null}); installed=($null -ne $receipt.endpoint); driverCallSucceeded=($null -ne $receipt.installError -and $receipt.installError -eq 0); rootRegistration=$receipt.rootRegistration; verification=$receipt.verification; nativeWarning=$receipt.nativeWarning; rebootRequired=$receipt.rebootRequired; installError=$receipt.installError; receiptPath=$receiptPath; restoreResults=$receipt.restoreResults; message=$receipt.error}
        }
} catch {
    $result = [ordered]@{status='error'; message=$_.Exception.Message}
    if (Get-Variable receiptPath -ErrorAction SilentlyContinue) { $result.receiptPath=$receiptPath }
}
if (Get-Command Complete-CastAudioResult -ErrorAction SilentlyContinue) { Complete-CastAudioResult $result $ResultPath }
else {
    $json=$result | ConvertTo-Json -Depth 12 -Compress
    if ($ResultPath) { [IO.File]::WriteAllText([IO.Path]::GetFullPath($ResultPath),$json,[Text.UTF8Encoding]::new($false)) }
    Write-Output $json
}
