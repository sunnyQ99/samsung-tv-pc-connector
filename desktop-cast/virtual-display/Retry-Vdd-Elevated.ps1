#requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
# Fixed installation entry point. It accepts no command, path, driver or action arguments.
# The caller explicitly starts this script with RunAs; this script never self-elevates.
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$diagnostics=Join-Path $workspace 'diagnostics\cast'
$installer=Join-Path $PSScriptRoot 'Manage-Vdd.ps1'
$receiptPath=Join-Path $env:ProgramData 'TVCITATION\VirtualDisplayDriver\installation.json'
$runId=([DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'))+'-'+[guid]::NewGuid().ToString('N').Substring(0,8)
$runBase=Join-Path $diagnostics ('vdd-install-'+$runId)
$resultPath=$runBase+'.json'
$latestPath=Join-Path $diagnostics 'vdd-install-result.json'
$result=[ordered]@{schema=1;runId=$runId;operation='RetryRemovedVdd';status='starting';ok=$false;
    startedAtUtc=[DateTime]::UtcNow.ToString('o');completedAtUtc=$null;wrapperPid=$PID;childPid=$null;
    exitCode=$null;wrapperExitCode=$null;wrapperLog=($runBase+'.wrapper.log');stdoutLog=($runBase+'.stdout.log');stderrLog=($runBase+'.stderr.log');
    resultPath=$resultPath;receiptPath=$receiptPath;receiptPhase=$null;receiptStage=$null;receiptErrorDetails=$null;nativeInstallCode=$null;
    rebootRequired=$null;error=$null;receiptError=$null;cleanupError=$null}
$wrapperExit=1
$locked=$false
$mutex=$null
function Write-InstallLog([string]$Message) {
    [IO.File]::AppendAllText($result.wrapperLog,([DateTime]::UtcNow.ToString('o')+' '+$Message+[Environment]::NewLine),[Text.UTF8Encoding]::new($false))
}
function Write-InstallResult {
    foreach ($path in @($resultPath,$latestPath)) {
        $temp=$path+'.'+[guid]::NewGuid().ToString('N')+'.tmp'
        [IO.File]::WriteAllText($temp,($result | ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))
        try {
            if (Test-Path -LiteralPath $path) { [IO.File]::Replace($temp,$path,[NullString]::Value) }
            else { [IO.File]::Move($temp,$path) }
        } finally { if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Force } }
    }
}
try {
    New-Item -ItemType Directory -Force -Path $diagnostics | Out-Null
    Write-InstallLog 'Wrapper started; no driver operation has run yet.'
    Write-InstallResult
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
    try { $admin=([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) }
    finally { $identity.Dispose() }
    if (-not $admin) { $wrapperExit=5; throw 'Administrator permission is required; no installer process was started.' }
    $mutex=[Threading.Mutex]::new($false,'Local\TVCITATION-VddInstall')
    try { $locked=$mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $locked=$true }
    if (-not $locked) { $wrapperExit=1618; throw 'Another TVCITATION VDD installation wrapper is running.' }
    if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) { throw 'The fixed Manage-Vdd.ps1 installer is missing.' }
    $shell=Join-Path $PSHOME 'pwsh.exe'
    if (-not (Test-Path -LiteralPath $shell -PathType Leaf)) { throw 'This wrapper requires the current PowerShell 7 executable.' }
    # Start-Process invokes an executable directly. This fixed -File path is quoted once;
    # no -Command, Invoke-Expression, shell concatenation, URL or caller-supplied argument exists.
    $arguments=@('-NoLogo','-NoProfile','-NonInteractive','-File',('"'+$installer+'"'),'-Action','Install','-TrustPublisher','-RetryRemoved')
    $result.status='running'; Write-InstallResult
    $child=Start-Process -FilePath $shell -ArgumentList $arguments -WorkingDirectory $workspace -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput $result.stdoutLog -RedirectStandardError $result.stderrLog
    $result.childPid=$child.Id; Write-InstallResult
    Write-InstallLog ('Started fixed installer process PID '+$child.Id+'.')
    # Wait only for this installer process. No arbitrary timeout kills an in-progress PnP installation.
    $child.WaitForExit()
    $result.exitCode=$child.ExitCode
    Write-InstallLog ('Installer process exited with code '+$child.ExitCode+'.')
    $wrapperExit=[int]$child.ExitCode
    if (Test-Path -LiteralPath $receiptPath) {
        $receipt=Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
        if ($receipt.owner -eq 'TVCITATION' -and $receipt.instanceId -eq 'ROOT\MTTVDD\0000') {
            $result.receiptPhase=$receipt.phase; $result.nativeInstallCode=$receipt.nativeInstallCode
            $result.receiptStage=$receipt.stage; $result.receiptErrorDetails=$receipt.errorDetails
            $result.rebootRequired=$receipt.rebootRequired; $result.receiptError=$receipt.error; $result.cleanupError=$receipt.cleanupError
        }
    }
    $result.ok=($result.exitCode -eq 0 -and $result.receiptPhase -eq 'installed')
    if ($result.ok) { $result.status='succeeded' }
    else {
        $result.status='failed'
        $result.error='Installer failed or did not leave a verified installed receipt. See the fixed stdout/stderr logs.'
        if ($wrapperExit -eq 0) { $wrapperExit=1 }
    }
} catch {
    $result.status='failed'; $result.ok=$false; $result.error=$_.Exception.Message
    try { Write-InstallLog ('Failure: '+$result.error) } catch { }
    if ($wrapperExit -eq 0) { $wrapperExit=1 }
} finally {
    $result.completedAtUtc=[DateTime]::UtcNow.ToString('o'); $result.wrapperExitCode=$wrapperExit
    try { Write-InstallLog ('Final status='+$result.status+'; wrapperExitCode='+$wrapperExit+'.') } catch { }
    try { Write-InstallResult }
    catch { [Console]::Error.WriteLine('Cannot persist VDD result JSON: '+$_.Exception.Message); if ($wrapperExit -eq 0) { $wrapperExit=1 } }
    if ($locked -and $mutex) { $mutex.ReleaseMutex() }
    if ($mutex) { $mutex.Dispose() }
}
# Only fixed result/log locations and non-secret status metadata are emitted.
$result | ConvertTo-Json -Depth 5
exit $wrapperExit
