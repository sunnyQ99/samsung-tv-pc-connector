$ErrorActionPreference = 'Stop'
$castRoot = Split-Path $PSScriptRoot -Parent
$castExe = [IO.Path]::GetFullPath((Join-Path $castRoot 'tools\cast\sunshine\Sunshine\sunshine.exe'))
$castConfig = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'sunshine.conf'))
$castDiagnostics = Join-Path $castRoot 'diagnostics\cast'
$castPrivate = Join-Path $env:LOCALAPPDATA 'TVCITATION\Sunshine'
if (-not (Test-Path -LiteralPath $castConfig) -or -not (Test-Path -LiteralPath (Join-Path $castPrivate 'web-credentials.json'))) {
    & (Join-Path $PSScriptRoot 'Initialize-CastHost.ps1') | Out-Null
}
$castExisting = Get-CimInstance Win32_Process -Filter "Name='sunshine.exe'" |
    Where-Object { $_.ExecutablePath -eq $castExe -and $_.CommandLine.Contains($castConfig) }
if ($castExisting) {
    [pscustomobject]@{ Status = 'already-running'; ProcessId = @($castExisting.ProcessId); ManagementUrl = 'https://localhost:47990' }
    return
}
$castOther = Get-Process -Name sunshine -ErrorAction SilentlyContinue
if ($castOther) { throw 'Another Sunshine instance is running; this script will not change it.' }
$castProcess = Start-Process -FilePath $castExe -ArgumentList ('"' + $castConfig + '"') -WorkingDirectory (Split-Path $castExe) -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $castDiagnostics 'sunshine-stdout.log') -RedirectStandardError (Join-Path $castDiagnostics 'sunshine-stderr.log')
[pscustomobject]@{ ProcessId = $castProcess.Id; StartedUtc = (Get-Date).ToUniversalTime().ToString('o'); Executable = $castExe; Config = $castConfig } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $castPrivate 'owned-process.json') -Encoding utf8
[pscustomobject]@{ Status = 'started'; ProcessId = $castProcess.Id; ManagementUrl = 'https://localhost:47990'; Log = (Join-Path $castDiagnostics 'sunshine.log') }
