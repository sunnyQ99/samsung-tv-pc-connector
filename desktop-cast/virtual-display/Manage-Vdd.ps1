[CmdletBinding()]
param(
    [ValidateSet('Check','Install','Remove')][string]$Action='Check',
    [switch]$TrustPublisher,
    [switch]$RetryRemoved
)
. (Join-Path $PSScriptRoot 'VddPackage.ps1')
$vddDirectory=Join-Path $env:ProgramData 'TVCITATION\VirtualDisplayDriver'
$vddReceiptPath=Join-Path $vddDirectory 'installation.json'
$vddRegistry='HKLM:\SOFTWARE\MikeTheTech\VirtualDisplayDriver'
$vddRegistryParent='HKLM:\SOFTWARE\MikeTheTech'
$vddInstance='ROOT\MTTVDD\0000'
Initialize-VddInterop
# Offline rollback must remain possible if the source download or certificate revocation
# server is unavailable. Removal trusts only the protected receipt and fixed local hashes.
$vddPackage=if ($Action -ne 'Remove') { Test-VddPackage } else { $null }
if ($Action -eq 'Check') {
    [pscustomobject]@{package=$vddPackage; ownedReceiptPresent=(Test-Path -LiteralPath $vddReceiptPath);
        targetDirectoryExists=(Test-Path -LiteralPath $vddDirectory); registryExists=(Test-Path -LiteralPath $vddRegistry);
        requiresAdministrator=$true; installsOnlyDisplay=$true; requestsPrimaryDisplayChange=$false} | ConvertTo-Json -Depth 10
    exit 0
}
$vddIdentity=[Security.Principal.WindowsIdentity]::GetCurrent()
try { $vddAdmin=([Security.Principal.WindowsPrincipal]::new($vddIdentity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) }
finally { $vddIdentity.Dispose() }
if (-not $vddAdmin) { throw 'Run this specific script in an administrator PowerShell. It does not elevate itself.' }
function Save-Receipt { Write-VddJson $vddReceiptPath $script:vddReceipt }
function Set-InstallStage([string]$Stage) { $script:vddReceipt.stage=$Stage; Save-Receipt }
function New-VddRegistryConfiguration {
    # SOFTWARE allows CREATE_SUB_KEY but denies the provider's broader READ|WRITE open.
    # Native calls request only necessary rights; null security preserves inherited ACLs.
    if ($vddDirectory -ne [TVCitation.VirtualDisplay.VddRegistry]::ExpectedDirectory) { throw 'VDD registry target directory mismatch.' }
    Set-InstallStage 'createRegistryParent'
    $script:vddReceipt.registryParentCreated=[TVCitation.VirtualDisplay.VddRegistry]::CreateParent(); Save-Receipt
    if (Test-Path -LiteralPath $vddRegistry) { throw 'VDD registry leaf appeared concurrently; no values were overwritten.' }
    Set-InstallStage 'createRegistryLeaf'
    if (-not [TVCitation.VirtualDisplay.VddRegistry]::CreateLeaf()) { throw 'VDD registry leaf appeared concurrently; no values were overwritten.' }
    $script:vddReceipt.registryCreated=$true; Save-Receipt
    Set-InstallStage 'setRegistryPath'
    [TVCitation.VirtualDisplay.VddRegistry]::SetPathOnEmptyLeaf()
}
function Assert-OwnedReceipt($receipt) {
    if ($receipt.schema -ne 1 -or $receipt.owner -ne 'TVCITATION' -or $receipt.instanceId -ne $vddInstance -or
        $receipt.directory -ne $vddDirectory -or $receipt.signerThumbprint -ne $script:VddLeaf) { throw 'VDD ownership receipt does not match this package.' }
}
function Remove-OwnedVdd {
    Assert-OwnedReceipt $script:vddReceipt
    $script:vddReceipt.phase='removing'; Save-Receipt
    $nodes=@([TVCitation.VirtualDisplay.VddRootDevice]::Find())
    if ($nodes.Count -gt 1 -or ($nodes.Count -eq 1 -and $nodes[0].InstanceId -ne $vddInstance)) { throw 'Another MttVDD device exists; cleanup stopped.' }
    if ($nodes.Count -eq 1) {
        if (-not $script:vddReceipt.rootCreated) { throw 'The root device was not created by this transaction.' }
        & "$env:windir\System32\pnputil.exe" /remove-device $vddInstance | Out-Null
        $nativeRemove=$LASTEXITCODE
        if ($nativeRemove -notin @(0,3010)) { throw "Exact VDD device removal failed: $nativeRemove" }
        if (@([TVCitation.VirtualDisplay.VddRootDevice]::Find()).Count -ne 0) { throw 'VDD device still present after removal.' }
    }
    # Remove only a newly staged package named in this receipt and still matching its INF hash.
    # Never force a package removal or touch NVIDIA/Parsec/other display adapters.
    if ($script:vddReceipt.publishedInf -and $script:vddReceipt.publishedInf -notin $script:vddReceipt.previousPublishedInf) {
        $infName=[string]$script:vddReceipt.publishedInf
        if ($infName -notmatch '^oem\d+\.inf$') { throw 'Invalid published INF receipt.' }
        $infPath=Join-Path $env:windir ('INF\'+$infName)
        if (Test-Path -LiteralPath $infPath) {
            if ((Get-FileHash -LiteralPath $infPath -Algorithm SHA256).Hash -ne $script:VddHashes['MttVDD.inf']) { throw 'Published INF has changed; package retained.' }
            & "$env:windir\System32\pnputil.exe" /delete-driver $infName | Out-Null
            if ($LASTEXITCODE -notin @(0,3010)) { throw "VDD package removal failed: $LASTEXITCODE" }
        }
    }
    if ($script:vddReceipt.registryCreated) { [TVCitation.VirtualDisplay.VddRegistry]::RemoveOwnedLeaf() }
    if ($script:vddReceipt.registryParentCreated) { [TVCitation.VirtualDisplay.VddRegistry]::RemoveEmptyOwnedParent() | Out-Null }
    if ($script:vddReceipt.publisherAdded) {
        $leafPath='Cert:\LocalMachine\TrustedPublisher\'+$script:VddLeaf
        if (Test-Path -LiteralPath $leafPath) { Remove-Item -LiteralPath $leafPath }
    }
    $script:vddReceipt.phase='removed'; $script:vddReceipt.removedAtUtc=[DateTime]::UtcNow.ToString('o'); Save-Receipt
    # Retain the tiny protected files and receipt as evidence. No recursive directory deletion.
    $script:vddReceipt
}
if ($Action -eq 'Remove') {
    if (-not (Test-Path -LiteralPath $vddReceiptPath)) { throw 'No owned VDD receipt; no changes made.' }
    $script:vddReceipt=Get-Content -LiteralPath $vddReceiptPath -Raw | ConvertFrom-Json
    Remove-OwnedVdd | ConvertTo-Json -Depth 10
    exit 0
}
if ($vddPackage.existingDevices.Count -ne 0) { throw 'An MttVDD device already exists; refusing duplicate/adoption.' }
$retryState=$null
if ($RetryRemoved) { $retryState=Get-VddRemovedRetryState }
else {
    if (Test-Path -LiteralPath $vddDirectory) { throw 'Target directory already exists; use only the verified -RetryRemoved path after a completed rollback.' }
    if (Test-Path -LiteralPath $vddRegistry) { throw 'An existing VDD registry configuration exists; refusing to overwrite it.' }
}
if (-not $vddPackage.trustedPublisherPresent -and -not $TrustPublisher) { throw 'This signed package requires its pinned leaf certificate in TrustedPublisher. Use -TrustPublisher for that specific change; no root CA is added.' }
$previousPublished=@(Get-ChildItem -LiteralPath (Join-Path $env:windir 'INF') -Filter 'oem*.inf' -File | Where-Object {
    (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash -eq $script:VddHashes['MttVDD.inf']
} | Select-Object -ExpandProperty Name)
$physicalBefore=@(Get-VddDisplaySnapshot)
if ($retryState) {
    $history=Join-Path $vddDirectory 'receipt-history'
    New-Item -ItemType Directory -Path $history -Force | Out-Null
    $priorReceiptArchive=Join-Path $history ('removed-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')+'-'+[guid]::NewGuid().ToString('N')+'.json')
    Copy-Item -LiteralPath $vddReceiptPath -Destination $priorReceiptArchive
    # Only a proved empty leaf belonging to the old removed receipt is allowed here.
    if (Test-Path -LiteralPath $vddRegistry) {
        $key=Get-Item -LiteralPath $vddRegistry
        if ($key.ValueCount -ne 0 -or $key.SubKeyCount -ne 0) { throw 'VDD registry changed during retry preflight.' }
        [TVCitation.VirtualDisplay.VddRegistry]::RemoveEmptyLeaf()
    }
} else { New-Item -ItemType Directory -Path $vddDirectory | Out-Null; $priorReceiptArchive=$null }
$vddAcl=[Security.AccessControl.DirectorySecurity]::new()
$vddAcl.SetAccessRuleProtection($true,$false)
foreach ($vddRule in @(@('S-1-5-18','FullControl'),@('S-1-5-32-544','FullControl'),@('S-1-5-11','ReadAndExecute'))) {
    $vddAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($vddRule[0]),$vddRule[1],'ContainerInherit,ObjectInherit','None','Allow'))
}
Set-Acl -LiteralPath $vddDirectory -AclObject $vddAcl
$script:vddReceipt=[ordered]@{schema=1;owner='TVCITATION';phase='preparing';instanceId=$vddInstance;directory=$vddDirectory;
    createdAtUtc=[DateTime]::UtcNow.ToString('o');removedAtUtc=$null;signerThumbprint=$script:VddLeaf;rootCreated=$false;
    registryCreated=$false;registryParentCreated=$false;publisherAdded=$false;previousPublishedInf=$previousPublished;publishedInf=$null;nativeInstallCode=$null;
    rebootRequired=$false;binding=$null;bindingEvidence=$null;bindingAttempts=0;bindingTransitions=@();error=$null;errorDetails=$null;cleanupError=$null;stage='preparingFiles';previousReceiptArchive=$priorReceiptArchive;displaysBefore=$physicalBefore;displaysAfter=$null}
Save-Receipt
try {
    foreach ($name in $script:VddHashes.Keys) { Copy-Item -LiteralPath (Join-Path $vddPackage.source $name) -Destination (Join-Path $vddDirectory $name) }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'vdd_settings.4k120.xml') -Destination (Join-Path $vddDirectory 'vdd_settings.xml')
    Test-VddPackage $vddDirectory | Out-Null
    if (-not $vddPackage.trustedPublisherPresent) {
        Set-InstallStage 'trustPinnedPublisher'
        $script:vddReceipt.publisherAdded=$true; Save-Receipt
        $cert=(Get-AuthenticodeSignature -LiteralPath (Join-Path $vddDirectory 'mttvdd.cat')).SignerCertificate
        $store=[Security.Cryptography.X509Certificates.X509Store]::new('TrustedPublisher','LocalMachine')
        try { $store.Open('ReadWrite'); $store.Add($cert) } finally { $store.Close() }
    }
    New-VddRegistryConfiguration
    Set-InstallStage 'registerRootDevice'
    $registration=[TVCitation.VirtualDisplay.VddRootDevice]::EnsureRegistered()
    $script:vddReceipt.rootCreated=$registration.Created; Save-Receipt
    if (-not $registration.Created -or -not $registration.Registered -or $registration.Error -ne 0) { throw "VDD root registration failed or was not owned: $($registration | ConvertTo-Json -Compress)" }
    [bool]$reboot=$false
    Set-InstallStage 'installDriver'
    $rc=[TVCitation.CastAudio.Driver]::Install((Join-Path $vddDirectory 'MttVDD.inf'),[ref]$reboot)
    $script:vddReceipt.nativeInstallCode=$rc; $script:vddReceipt.rebootRequired=$reboot; Save-Receipt
    # 259 is only accepted when PnP independently finishes installing exactly this signed package.
    if ($rc -notin @(0,259)) { throw "VDD native install failed: $rc" }
    $deadline=[DateTime]::UtcNow.AddSeconds(25)
    $lastBindingState=$null
    do {
        $bindingEvidence=Get-VddBindingEvidence $vddInstance
        $script:vddReceipt.bindingEvidence=$bindingEvidence
        $script:vddReceipt.bindingAttempts++
        $bindingState=($bindingEvidence.failedChecks -join ',')
        if($bindingState -ne $lastBindingState -or $script:vddReceipt.bindingAttempts -eq 1) {
            $script:vddReceipt.bindingTransitions+=@([pscustomobject]@{attempt=$script:vddReceipt.bindingAttempts;timestampUtc=$bindingEvidence.timestampUtc;failedChecks=$bindingEvidence.failedChecks})
            $lastBindingState=$bindingState
        }
        Save-Receipt
        $binding=$bindingEvidence.binding
        if ($binding) { break }
        Start-Sleep -Milliseconds 400
    } while ([DateTime]::UtcNow -lt $deadline)
    if (-not $binding) { throw ('VDD binding validation did not pass within 25 seconds: '+($bindingEvidence.failedChecks -join ', ')+'. See bindingEvidence in the receipt.') }
    $script:vddReceipt.binding=$binding; $script:vddReceipt.publishedInf=$binding.publishedInf
    $script:vddReceipt.displaysAfter=@(Get-VddDisplaySnapshot); Save-Receipt
    if (-not (Test-VddPhysicalPreserved $physicalBefore $script:vddReceipt.displaysAfter)) { throw 'Physical display topology or mode changed after VDD installation; removing only the new VDD.' }
    $script:vddReceipt.phase='installed'; Save-Receipt
    [pscustomobject]@{installed=$true;receipt=$vddReceiptPath;binding=$binding;
        note='Driver installed. Display enumeration, extended topology, physical 1080p320 preservation and Sunshine capture still require validation. No physical mode setter was called.'} | ConvertTo-Json -Depth 10
} catch {
    $script:vddReceipt.error=$_.Exception.Message
    $script:vddReceipt.errorDetails=[ordered]@{stage=$script:vddReceipt.stage;exceptionType=$_.Exception.GetType().FullName;
        fullyQualifiedErrorId=$_.FullyQualifiedErrorId;scriptLine=$_.InvocationInfo.ScriptLineNumber;position=$_.InvocationInfo.PositionMessage;stack=$_.ScriptStackTrace}
    # Capture a staged INF even if device-start validation failed, for precise cleanup.
    $staged=@(Get-CimInstance Win32_PnPSignedDriver | Where-Object DeviceID -eq $vddInstance)
    if ($staged.Count -eq 1 -and $staged[0].InfName -match '^oem\d+\.inf$') { $script:vddReceipt.publishedInf=$staged[0].InfName }
    if (-not $script:vddReceipt.publishedInf) {
        $newInf=@(Get-ChildItem -LiteralPath (Join-Path $env:windir 'INF') -Filter 'oem*.inf' -File | Where-Object {
            $_.Name -notin $previousPublished -and (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash -eq $script:VddHashes['MttVDD.inf']
        })
        if ($newInf.Count -eq 1) { $script:vddReceipt.publishedInf=$newInf[0].Name }
    }
    Save-Receipt
    try { Remove-OwnedVdd | Out-Null } catch { $script:vddReceipt.cleanupError=$_.Exception.Message; Save-Receipt }
    throw "VDD installation failed: $($script:vddReceipt.error). Cleanup: $($script:vddReceipt.cleanupError)"
}
