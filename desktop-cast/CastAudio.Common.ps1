Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not [Environment]::Is64BitProcess) { throw 'Use 64-bit PowerShell for the audio module.' }
if (-not ('TVCitation.CastAudio.Audio' -as [type])) {
    Add-Type -Path (Join-Path $PSScriptRoot 'CastAudioInterop.cs')
}
if (-not ('TVCitation.CastAudio.SteamRootDevice' -as [type])) {
    Add-Type -Path (Join-Path $PSScriptRoot 'SteamRootDevice.cs')
}
function Write-CastAudioJson($Value) { $Value | ConvertTo-Json -Depth 12 -Compress }
function Complete-CastAudioResult($Value, [string]$ResultPath) {
    $json = $Value | ConvertTo-Json -Depth 12 -Compress
    if ($ResultPath) {
        $full = [IO.Path]::GetFullPath($ResultPath)
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($full)) | Out-Null
        [IO.File]::WriteAllText($full, $json, [Text.UTF8Encoding]::new($false))
    }
    Write-Output $json
}
function Test-CastAudioAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try { return ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) }
    finally { $identity.Dispose() }
}
function Assert-CastAudioSunshineStopped {
    if (@(Get-Process -Name sunshine -ErrorAction SilentlyContinue).Count -gt 0) { throw 'sunshineRunning: Stop Sunshine before changing audio configuration or restoring default endpoints.' }
}
function Get-CastAudioSteamEndpoints {
    @([TVCitation.CastAudio.Audio]::GetEndpoints() | Where-Object {
        $_.InterfaceName -eq 'Steam Streaming Speakers' -or $_.Name -eq 'Steam Streaming Speakers' -or $_.Name -match '^.+ \(Steam Streaming Speakers\)$'
    })
}
function Get-CastAudioSteamDrivers {
    @(Get-CimInstance Win32_PnPSignedDriver -ErrorAction Stop | Where-Object {
        $_.DeviceID -like 'ROOT\STEAMSTREAMINGSPEAKERS\*' -or $_.DeviceName -eq 'Steam Streaming Speakers'
    } | Select-Object DeviceName, DeviceID, InfName, DriverVersion, IsSigned, Signer, DriverProviderName)
}
function Test-CastAudioSteamBinding($RootDevices, $Drivers, $PublishedPackage, $Endpoints, [string]$EndpointParent) {
    # The caller has already validated the fixed Steam source package's signatures and catalog membership.
    $roots = @($RootDevices); $devices = @($Drivers); $endpoints = @($Endpoints)
    if ($endpoints.Count -eq 0) { return 'endpointMissing' }
    if ($endpoints.Count -gt 1) { return 'ambiguousEndpoint' }
    if ($null -eq $PublishedPackage -or [string]::IsNullOrWhiteSpace($PublishedPackage.PublishedInf)) { return 'driverBindingUnverified' }
    if ($roots.Count -ne 1 -or $roots[0].ConfigManagerResult -ne 0 -or $roots[0].ProblemCode -ne 0 -or ($roots[0].StatusFlags -band 8) -eq 0) { return 'driverBindingUnverified' }
    $root = $roots[0]
    if ($root.HardwareIds -notcontains [TVCitation.CastAudio.SteamRootDevice]::HardwareId -or $EndpointParent -ne $root.InstanceId -or $endpoints[0].State -ne 1) { return 'driverBindingUnverified' }
    $matching = @($devices | Where-Object { $_.DeviceID -eq $root.InstanceId })
    if ($matching.Count -ne 1 -or $matching[0].IsSigned -ne $true -or $matching[0].InfName -ne [IO.Path]::GetFileName($PublishedPackage.PublishedInf)) { return 'driverBindingUnverified' }
    return 'ready'
}
function Get-CastAudioSteamInstallation([string]$Inf) {
    $endpoints = @(Get-CastAudioSteamEndpoints)
    $roots = @([TVCitation.CastAudio.SteamRootDevice]::Find())
    $drivers = @(Get-CastAudioSteamDrivers)
    # The helper matches the installed INF + catalog hashes to the trusted source package.
    $published = [TVCitation.CastAudio.Driver]::GetPublishedDriver($Inf)
    $parent = $null; $evidenceError = $null
    if ($endpoints.Count -eq 1) {
        try { $parent = (Get-PnpDeviceProperty -InstanceId ('SWD\MMDEVAPI\' + $endpoints[0].Id) -KeyName 'DEVPKEY_Device_Parent' -ErrorAction Stop).Data }
        catch { $evidenceError = $_.Exception.Message }
    }
    $status = Test-CastAudioSteamBinding $roots $drivers $published $endpoints $parent
    [ordered]@{status=$status; endpoint=$(if ($status -eq 'ready') {$endpoints[0]} else {$null}); endpoints=$endpoints; rootDevices=$roots; drivers=$drivers; publishedPackage=$published; endpointParent=$parent; evidenceError=$evidenceError}
}
function Save-CastAudioJsonFile([string]$Path, $Value) {
    $full = [IO.Path]::GetFullPath($Path)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($full)) | Out-Null
    $temp = $full + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
    try {
        [IO.File]::WriteAllText($temp, ($Value | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
        if ([IO.File]::Exists($full)) { [IO.File]::Replace($temp, $full, [NullString]::Value) }
        else { [IO.File]::Move($temp, $full) }
    } finally { if ([IO.File]::Exists($temp)) { [IO.File]::Delete($temp) } }
}
function Assert-CastAudioRoleSnapshot($Defaults) {
    $items = @($Defaults)
    if ($items.Count -ne 3 -or (@($items | ForEach-Object { [int]$_.Role } | Sort-Object -Unique) -join ',') -ne '0,1,2') { throw 'Invalid three-role audio snapshot.' }
    foreach ($item in $items) {
        if ($null -ne $item.Id -and [string]$item.Id -notmatch '^\{0\.0\.0\.00000000\}\.\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$') { throw 'Invalid audio snapshot endpoint ID.' }
    }
}
function Restore-CastAudioRoleSnapshot($Defaults) {
    Assert-CastAudioRoleSnapshot $Defaults
    $items = @($Defaults)
    $results = foreach ($item in $items) {
        if ($null -eq $item.Id -or [string]::IsNullOrWhiteSpace([string]$item.Id)) {
            [pscustomobject]@{ Role=[int]$item.Role; Id=$null; Changed=$false; HResult=0; Note='No original endpoint; no change made.' }
        } else { [TVCitation.CastAudio.Audio]::Restore([int]$item.Role, [string]$item.Id) }
    }
    return @($results)
}
function ConvertTo-CastAudioConfiguration([string]$Text, [string]$EndpointId) {
    if ($EndpointId -notmatch '^\{0\.0\.0\.00000000\}\.\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$') { throw 'Invalid render endpoint ID.' }
    $newline = if ($Text.Contains("`r`n")) { "`r`n" } else { "`n" }
    $values = [ordered]@{ virtual_sink=$EndpointId; install_steam_audio_drivers='disabled' }
    foreach ($key in $values.Keys) {
        $pattern = '(?m)^[ \t]*' + [regex]::Escape($key) + '[ \t]*=[^\r\n]*'
        $replacement = $key + ' = ' + $values[$key]
        if ([regex]::IsMatch($Text,$pattern)) { $Text = [regex]::Replace($Text,$pattern,$replacement) }
        else { if ($Text.Length -gt 0 -and -not $Text.EndsWith("`n")) { $Text += $newline }; $Text += $replacement + $newline }
    }
    return $Text
}
function Set-CastAudioFileBytes([string]$Path, [byte[]]$Bytes) {
    $temp = $Path + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
    try { [IO.File]::WriteAllBytes($temp,$Bytes); [IO.File]::Replace($temp,$Path,[NullString]::Value) }
    finally { if ([IO.File]::Exists($temp)) { [IO.File]::Delete($temp) } }
}
