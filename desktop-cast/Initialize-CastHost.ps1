$ErrorActionPreference = 'Stop'
$castRoot = Split-Path $PSScriptRoot -Parent
$castExe = Join-Path $castRoot 'tools\cast\sunshine\Sunshine\sunshine.exe'
$castConfig = Join-Path $PSScriptRoot 'sunshine.conf'
$castPrivate = Join-Path $env:LOCALAPPDATA 'TVCITATION\Sunshine'
$castDiagnostics = Join-Path $castRoot 'diagnostics\cast'
if (-not (Test-Path -LiteralPath $castExe)) { throw 'The verified Sunshine binary is missing.' }
New-Item -ItemType Directory -Path $castPrivate, $castDiagnostics -Force | Out-Null

# Keep pairing keys and the DPAPI-protected management credential outside the repository.
$castAcl = [System.Security.AccessControl.DirectorySecurity]::new()
$castAcl.SetAccessRuleProtection($true, $false)
$castInheritance = [System.Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
foreach ($castIdentity in @([System.Security.Principal.WindowsIdentity]::GetCurrent().User, [System.Security.Principal.SecurityIdentifier]::new('S-1-5-18'))) {
    $castRule = [System.Security.AccessControl.FileSystemAccessRule]::new($castIdentity, 'FullControl', $castInheritance, 'None', 'Allow')
    $castAcl.AddAccessRule($castRule)
}
Set-Acl -LiteralPath $castPrivate -AclObject $castAcl

if (-not (Test-Path -LiteralPath $castConfig)) {
    $castApps = (Join-Path $PSScriptRoot 'apps.json').Replace('\', '/')
    $castPrivateForward = $castPrivate.Replace('\', '/')
    $castLog = (Join-Path $castDiagnostics 'sunshine.log').Replace('\', '/')
    @"
# TVCITATION user-scoped host; no display changes or driver installation.
sunshine_name = TVCITATION PC
locale = zh
upnp = disabled
address_family = ipv4
origin_web_ui_allowed = pc
lan_encryption_mode = 1
wan_encryption_mode = 2
port = 47989
encoder = nvenc
adapter_name = NVIDIA GeForce RTX 5070 Ti
stream_audio = enabled
install_steam_audio_drivers = disabled
controller = disabled
gamepad_driver = vigembus
keyboard = enabled
mouse = enabled
dd_configuration_option = disabled
dd_resolution_option = disabled
dd_refresh_rate_option = disabled
dd_hdr_option = disabled
nvenc_opengl_vulkan_on_dxgi = disabled
system_tray = enabled
notify_pre_releases = disabled
file_apps = $castApps
credentials_file = $castPrivateForward/web-credentials.json
file_state = $castPrivateForward/pairing-state.json
pkey = $castPrivateForward/cakey.pem
cert = $castPrivateForward/cacert.pem
log_path = $castLog
"@ | Set-Content -LiteralPath $castConfig -Encoding utf8
}
if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'apps.json'))) {
    @{ env = @{}; apps = @(@{ name = 'Desktop'; 'image-path' = (Join-Path (Split-Path $castExe) 'assets\desktop.png').Replace('\', '/') }) } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'apps.json') -Encoding utf8
}

$castCredentialPath = Join-Path $castPrivate 'web-credential.clixml'
$castCredentialJson = Join-Path $castPrivate 'web-credentials.json'
if (-not (Test-Path -LiteralPath $castCredentialJson)) {
    if (Test-Path -LiteralPath $castCredentialPath) {
        $castCredential = Import-Clixml -LiteralPath $castCredentialPath
    } else {
        $castPassword = [Convert]::ToBase64String([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(36))
        $castCredential = [PSCredential]::new('tvcitation', (ConvertTo-SecureString -String $castPassword -AsPlainText -Force))
        $castCredential | Export-Clixml -LiteralPath $castCredentialPath
        $castPassword = $null
    }
    # Pass secrets only in memory to the official local credentials command; never log them.
    $castStart = [System.Diagnostics.ProcessStartInfo]::new($castExe)
    $castStart.WorkingDirectory = Split-Path $castExe
    $castStart.UseShellExecute = $false
    $castStart.CreateNoWindow = $true
    $castStart.RedirectStandardOutput = $true
    $castStart.RedirectStandardError = $true
    $castStart.ArgumentList.Add($castConfig)
    $castStart.ArgumentList.Add('--creds')
    $castStart.ArgumentList.Add($castCredential.UserName)
    $castStart.ArgumentList.Add($castCredential.GetNetworkCredential().Password)
    $castProcess = [System.Diagnostics.Process]::Start($castStart)
    $castStdout = $castProcess.StandardOutput.ReadToEnd()
    $castStderr = $castProcess.StandardError.ReadToEnd()
    $castProcess.WaitForExit()
    if ($castProcess.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $castCredentialJson)) {
        throw "Sunshine credential initialization failed (exit $($castProcess.ExitCode)); no secret output was printed."
    }
    $castStart.ArgumentList.Clear()
    $castCredential = $null
    $castStdout = $null
    $castStderr = $null
}
[pscustomobject]@{ Config = $castConfig; CredentialFile = $castCredentialPath; PrivateDirectory = $castPrivate; ManagementUrl = 'https://localhost:47990' }
