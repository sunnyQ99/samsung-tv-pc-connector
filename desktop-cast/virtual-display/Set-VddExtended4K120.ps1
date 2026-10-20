[CmdletBinding()]
param([switch]$Apply)
. (Join-Path $PSScriptRoot 'VddPackage.ps1')
Initialize-VddInterop
$receiptPath=Join-Path $env:ProgramData 'TVCITATION\VirtualDisplayDriver\installation.json'
if (-not (Test-Path -LiteralPath $receiptPath)) { throw 'No owned VDD installation receipt.' }
$receipt=Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
if ($receipt.owner -ne 'TVCITATION' -or $receipt.instanceId -ne 'ROOT\MTTVDD\0000' -or $receipt.phase -ne 'installed') { throw 'No completed owned VDD installation.' }
$binding=Get-VddInstalledBinding $receipt.instanceId
if (-not $binding) { throw 'VDD signed binding is not active.' }
$before=@(Get-VddDisplaySnapshot)
$vdds=@($before | Where-Object { $_.id -in @('ROOT\MttVDD',$receipt.instanceId) })
if ($vdds.Count -ne 1 -or $vdds[0].primary) { throw 'Cannot uniquely identify a non-primary VDD output; no mode changes made.' }
$vdd=$vdds[0]
$physical=@($before | Where-Object { $_.id -notin @('ROOT\MttVDD',$receipt.instanceId) })
if (-not @($physical | Where-Object { $_.attached -and $_.primary }).Count) { throw 'No attached physical primary display.' }
$x=[int](($physical | Where-Object attached | ForEach-Object { $_.mode.x + $_.mode.width } | Measure-Object -Maximum).Maximum)
$y=0
$testResult=[TVCitation.VirtualDisplay.Inventory]::Set4K120($vdd.name,$x,$y,$true)
if ($testResult -ne 0) { throw "Windows rejected VDD 4K120 mode test: $testResult" }
if (-not $Apply) {
    [pscustomobject]@{testedOnly=$true;nativeTestResult=$testResult;output=$vdd.name;instance=$receipt.instanceId;width=3840;height=2160;hz=120;x=$x;y=$y;primary=$false} | ConvertTo-Json
    exit 0
}
$requestDirectory=Join-Path $env:LOCALAPPDATA 'TVCITATION\Control\display-rollback'
New-Item -ItemType Directory -Force -Path $requestDirectory | Out-Null
$requestPath=Join-Path $requestDirectory ([guid]::NewGuid().ToString('N')+'.json')
$original=[pscustomobject]@{name=$vdd.name;attached=$vdd.attached;width=0;height=0;hz=60;x=0;y=0}
if ($vdd.attached) {
    foreach ($field in @('width','height','hz','x','y')) { $original.$field=$vdd.mode.$field }
}
$deadline=[DateTime]::UtcNow.AddSeconds(30)
Write-VddJson $requestPath ([pscustomobject]@{deadlineUtc=$deadline.ToString('o');before=$original})
$pwsh=(Get-Command pwsh -ErrorAction Stop).Source
$watchdogPath=Join-Path $PSScriptRoot 'VddModeWatchdog.ps1'
$watchdog=Start-Process -FilePath $pwsh -ArgumentList @('-NoProfile','-File',('"'+$watchdogPath+'"'),'-RequestPath',('"'+$requestPath+'"')) -WindowStyle Hidden -PassThru
try {
    $readyDeadline=[DateTime]::UtcNow.AddSeconds(5)
    while (-not (Test-Path -LiteralPath ($requestPath+'.ready'))) {
        $watchdog.Refresh()
        if ($watchdog.HasExited -or [DateTime]::UtcNow -gt $readyDeadline) { throw 'VDD rollback watchdog did not become ready.' }
        Start-Sleep -Milliseconds 100
    }
    $rc=[TVCitation.VirtualDisplay.Inventory]::Set4K120($vdd.name,$x,$y,$false)
    if ($rc -ne 0) { throw "VDD apply failed: $rc" }
    $after=@(Get-VddDisplaySnapshot)
    if (-not (Test-VddPhysicalPreserved $physical $after)) { throw 'The physical display changed; rolling back the VDD mode.' }
    $active=@($after | Where-Object { $_.id -in @('ROOT\MttVDD',$receipt.instanceId) })
    if ($active.Count -ne 1 -or -not $active[0].attached -or $active[0].primary -or
        $active[0].mode.width -ne 3840 -or $active[0].mode.height -ne 2160 -or $active[0].mode.hz -ne 120 -or
        $active[0].mode.x -ne $x -or $active[0].mode.y -ne $y) { throw '4K120 extended output readback failed.' }
    if ([DateTime]::UtcNow -gt $deadline.AddSeconds(-5)) { throw 'Too close to rollback deadline to commit the display mode.' }
    [IO.File]::WriteAllText(($requestPath+'.committed'),'verified')
    [pscustomobject]@{applied=$true;output=$active[0];physicalPreserved=$true;rollbackRecord=$requestPath;
        note='Independent extended desktop; source content must be opened or moved onto this display.'} | ConvertTo-Json -Depth 8
} catch {
    # Immediate best-effort rollback, plus the already running 30-second watchdog if this process exits.
    $restore=[TVCitation.VirtualDisplay.Inventory]::RestoreVirtual($original.name,$original.attached,$original.width,$original.height,$original.hz,$original.x,$original.y)
    if ($restore -eq 0) { [IO.File]::WriteAllText(($requestPath+'.committed'),'rolled-back') }
    throw
}
