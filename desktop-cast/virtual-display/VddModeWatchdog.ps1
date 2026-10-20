[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RequestPath)
$ErrorActionPreference='Stop'
Add-Type -Path @((Join-Path $PSScriptRoot 'VddRootDevice.cs'),(Join-Path $PSScriptRoot 'VddDisplayInventory.cs'))
$request=Get-Content -LiteralPath $RequestPath -Raw | ConvertFrom-Json
$deadline=if ($request.deadlineUtc -is [DateTime]) { $request.deadlineUtc.ToUniversalTime() }
else { [DateTime]::Parse([string]$request.deadlineUtc,[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime() }
[IO.File]::WriteAllText(($RequestPath+'.ready'),'ready')
while ([DateTime]::UtcNow -lt $deadline) {
    if (Test-Path -LiteralPath ($RequestPath+'.committed')) { exit 0 }
    Start-Sleep -Milliseconds 200
}
if (Test-Path -LiteralPath ($RequestPath+'.committed')) { exit 0 }
try {
    $before=$request.before
    $rc=[TVCitation.VirtualDisplay.Inventory]::RestoreVirtual($before.name,$before.attached,
        $before.width,$before.height,$before.hz,$before.x,$before.y)
    [IO.File]::WriteAllText(($RequestPath+'.rollback'),"nativeResult=$rc")
} catch {
    [IO.File]::WriteAllText(($RequestPath+'.rollback'),$_.Exception.Message)
}
