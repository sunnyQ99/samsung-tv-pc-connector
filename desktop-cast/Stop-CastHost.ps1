$ErrorActionPreference = 'Stop'
$castRoot = Split-Path $PSScriptRoot -Parent
$castExe = [IO.Path]::GetFullPath((Join-Path $castRoot 'tools\cast\sunshine\Sunshine\sunshine.exe'))
$castConfig = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'sunshine.conf'))
$castOwned = @(Get-CimInstance Win32_Process -Filter "Name='sunshine.exe'" |
    Where-Object { $_.ExecutablePath -eq $castExe -and $_.CommandLine.Contains($castConfig) })
foreach ($castProcess in $castOwned) {
    # The owned standalone process has no service, display mutation, or global NVIDIA override.
    Stop-Process -Id $castProcess.ProcessId -ErrorAction Stop
}
[pscustomobject]@{ Status = 'stopped'; Count = $castOwned.Count }
