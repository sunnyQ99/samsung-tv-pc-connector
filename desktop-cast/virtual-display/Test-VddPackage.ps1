[CmdletBinding()]
param([string]$OutputPath)
. (Join-Path $PSScriptRoot 'VddPackage.ps1')
$result = Test-VddPackage
if ($OutputPath) { Write-VddJson ([IO.Path]::GetFullPath($OutputPath)) $result }
$result | ConvertTo-Json -Depth 8
