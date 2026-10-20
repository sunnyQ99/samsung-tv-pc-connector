$ErrorActionPreference = 'Stop'
$script:VddLeaf = '3CF8CF26D8BA266C3A483AB7D26D4A818E317D76'
$script:VddHashes = [ordered]@{
    'MttVDD.inf' = '550D211FE481E74DFE3F9D724ED78BE48B3A9113405965D683D9373E8D672F5D'
    'MttVDD.dll' = 'C9CA837F57A98FBD43BC416A7F535A95843626E7759EAF85CF0CD7CE334DBB05'
    'mttvdd.cat' = '08A0093FC9B2E32B287A6F8A77CA4DE0A31830D29FC33D2B13A918DC859468F6'
}
function Initialize-VddInterop {
    if (-not ('TVCitation.VirtualDisplay.Inventory' -as [type])) {
        if ('TVCitation.VirtualDisplay.VddRootDevice' -as [type]) { throw 'Use a fresh PowerShell process to load both VDD native helpers together.' }
        Add-Type -Path @((Join-Path $PSScriptRoot 'VddRootDevice.cs'),(Join-Path $PSScriptRoot 'VddDisplayInventory.cs'))
    }
    if (-not ('TVCitation.VirtualDisplay.VddRegistry' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'VddRegistry.cs') }
    if (-not ('TVCitation.VirtualDisplay.VddSignature' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'VddSignature.cs') }
    # Loading declarations has no audio/device side effects. Only catalog verification and
    # the standard DiInstallDriver wrapper are reused; no audio methods are called.
    if (-not ('TVCitation.CastAudio.Driver' -as [type])) { Add-Type -Path (Join-Path (Split-Path $PSScriptRoot) 'CastAudioInterop.cs') }
}
function Get-VddSourcePath {
    [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../tools/cast/virtual-display-driver/driver-25.7.23/VirtualDisplayDriver'))
}
function Test-VddPackage([string]$Path = (Get-VddSourcePath)) {
    Initialize-VddInterop
    $files = foreach ($entry in $script:VddHashes.GetEnumerator()) {
        $full = Join-Path $Path $entry.Key
        $hash = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash
        if ($hash -ne $entry.Value) { throw "Pinned VDD file differs: $($entry.Key)" }
        [pscustomobject]@{ name=$entry.Key; sha256=$hash }
    }
    $cat = Join-Path $Path 'mttvdd.cat'
    $catSig = Get-AuthenticodeSignature -LiteralPath $cat
    $dllSig = Get-AuthenticodeSignature -LiteralPath (Join-Path $Path 'MttVDD.dll')
    foreach ($sig in @($catSig,$dllSig)) {
        if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Thumbprint -ne $script:VddLeaf) { throw 'VDD signature or pinned publisher mismatch.' }
    }
    $chain = [Security.Cryptography.X509Certificates.X509Chain]::new()
    try {
        $chain.ChainPolicy.RevocationMode = 'Online'
        $chain.ChainPolicy.UrlRetrievalTimeout = [TimeSpan]::FromSeconds(10)
        if (-not $chain.Build($catSig.SignerCertificate)) { throw 'VDD publisher chain validation failed.' }
        $chainInfo = @($chain.ChainElements | ForEach-Object { [pscustomobject]@{subject=$_.Certificate.Subject;thumbprint=$_.Certificate.Thumbprint} })
    } finally { $chain.Dispose() }
    foreach ($name in @('MttVDD.inf','MttVDD.dll')) {
        if (-not [TVCitation.CastAudio.Driver]::IsCatalogMember($cat,(Join-Path $Path $name))) { throw "VDD file is not a verified catalog member: $name" }
    }
    [pscustomobject]@{ valid=$true; source=$Path; release='25.7.23'; driverVersion='11.30.4.434'; architecture='AMD64';
        files=@($files); signerThumbprint=$script:VddLeaf; chain=$chainInfo;
        trustedPublisherPresent=(Test-Path ('Cert:\LocalMachine\TrustedPublisher\'+$script:VddLeaf));
        existingDevices=@([TVCitation.VirtualDisplay.VddRootDevice]::Find()) }
}
function Write-VddJson([string]$Path, $Value) {
    $temp = "$Path.$([guid]::NewGuid().ToString('N')).tmp"
    [IO.File]::WriteAllText($temp,($Value | ConvertTo-Json -Depth 12),[Text.UTF8Encoding]::new($false))
    try {
        if (Test-Path -LiteralPath $Path) { [IO.File]::Replace($temp,$Path,[NullString]::Value) }
        else { [IO.File]::Move($temp,$Path) }
    } finally { if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Force } }
}
function Get-VddBindingNodes { [TVCitation.VirtualDisplay.VddRootDevice]::Find() }
function Get-VddHashEvidence([string]$Path,[string]$ExpectedHash) {
    $evidence=[ordered]@{path=$Path;exists=$false;expectedSha256=$ExpectedHash;sha256=$null;matches=$false;error=$null}
    try {
        $evidence.exists=Test-Path -LiteralPath $Path -PathType Leaf
        if($evidence.exists) {
            $evidence.sha256=(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
            $evidence.matches=($evidence.sha256 -eq $ExpectedHash)
        }
    } catch { $evidence.error=$_.Exception.Message }
    [pscustomobject]$evidence
}
function Get-VddInfSignature([string]$Path) { [TVCitation.VirtualDisplay.VddSignature]::VerifyInf($Path) }
function Get-VddInstalledSignatureEvidence([string]$StorePath) {
    # This policy is scoped to the pinned UMDF/IddCx package, not an exception for
    # arbitrary kernel drivers. Win32_PnPSignedDriver is retained as inventory data.
    $errors=[Collections.Generic.List[string]]::new()
    $result=[ordered]@{policy='PinnedUMDF-SetupAPI-Authenticode';valid=$false;package=$null;setupApi=$null;setupCatalog=$null;errors=@()}
    try {
        # Reverify the installed bytes, publisher fingerprint, trusted chain/revocation,
        # and INF + DLL WinVerifyTrust catalog membership, not only the source download.
        $result.package=Test-VddPackage $StorePath
        if(-not $result.package.valid) { $errors.Add('installedPackageInvalid') }
        if(-not $result.package.trustedPublisherPresent) { $errors.Add('pinnedTrustedPublisherAbsent') }
    } catch { $errors.Add('installedPackageVerification: '+$_.Exception.Message) }
    try {
        $result.setupApi=Get-VddInfSignature (Join-Path $StorePath 'MttVDD.inf')
        if(-not $result.setupApi.TrustedSignature) { $errors.Add('setupApiRejected: '+$result.setupApi.ErrorHex) }
        if([string]::IsNullOrWhiteSpace($result.setupApi.DigitalSigner)) { $errors.Add('setupApiSignerMissing') }
        if([string]::IsNullOrWhiteSpace($result.setupApi.CatalogPath)) { $errors.Add('setupApiCatalogMissing') }
        else {
            # SetupAPI may return the store CAT or its published CatRoot copy. Require
            # exact catalog bytes, rather than assuming a particular catalog location.
            $result.setupCatalog=Get-VddHashEvidence $result.setupApi.CatalogPath $script:VddHashes['mttvdd.cat']
            if(-not $result.setupCatalog.matches) { $errors.Add('setupApiCatalogIdentity') }
        }
    } catch { $errors.Add('setupApiVerification: '+$_.Exception.Message) }
    $result.errors=$errors.ToArray()
    $result.valid=($errors.Count -eq 0)
    [pscustomobject]$result
}
function Get-VddBindingEvidence([string]$InstanceId) {
    $clock=[Diagnostics.Stopwatch]::StartNew()
    $failures=[Collections.Generic.List[string]]::new()
    $evidence=[ordered]@{schema=1;timestampUtc=[DateTime]::UtcNow.ToString('o');instanceId=$InstanceId;
        ready=$false;binding=$null;failedChecks=@();nodeCount=0;nodes=@();nodeReadError=$null;
        cimCount=0;cim=@();cimReadError=$null;relatedCim=@();isSignedValue=$null;isSignedType=$null;isSignedAccepted=$false;
        publishedInf=$null;storeRoot=(Join-Path $env:windir 'System32\DriverStore\FileRepository');storeCandidates=@();storeReadError=$null;matchingStoreCount=0;
        directSignature=$null;directSignatureError=$null;cimSignatureDiscrepancy=$false;warnings=@();elapsedMilliseconds=0}
    try { $evidence.nodes=@(Get-VddBindingNodes | Where-Object InstanceId -eq $InstanceId) }
    catch { $evidence.nodeReadError=$_.Exception.Message; $failures.Add('nodeReadError') }
    $evidence.nodeCount=$evidence.nodes.Count
    if($evidence.nodeCount -ne 1) { $failures.Add('nodeCount') }
    else {
        $node=$evidence.nodes[0]
        if($node.ConfigManagerResult -ne 0) { $failures.Add('configManagerResult') }
        if($node.ProblemCode -ne 0) { $failures.Add('problemCode') }
        if(-not ($node.StatusFlags -band 8)) { $failures.Add('nodeNotStarted') }
    }
    try {
        $inventory=@(Get-CimInstance Win32_PnPSignedDriver)
        $evidence.cim=@($inventory | Where-Object DeviceID -eq $InstanceId | Select-Object DeviceID,DeviceName,IsSigned,DriverVersion,DriverProviderName,InfName,Signer)
        $evidence.relatedCim=@($inventory | Where-Object {
            $_.DeviceID -ne $InstanceId -and ($_.DriverProviderName -eq 'MikeTheTech' -or $_.DeviceName -match 'MttVDD|Virtual Display' -or $_.DeviceID -like 'ROOT\MTTVDD\*')
        } | Select-Object DeviceID,DeviceName,IsSigned,DriverVersion,DriverProviderName,InfName,Signer)
    } catch { $evidence.cimReadError=$_.Exception.Message; $failures.Add('cimReadError') }
    $evidence.cimCount=$evidence.cim.Count
    if($evidence.cimCount -ne 1) { $failures.Add('cimCount') }
    else {
        $driver=$evidence.cim[0]
        $evidence.isSignedValue=$driver.IsSigned
        if($null -ne $driver.IsSigned) { $evidence.isSignedType=$driver.IsSigned.GetType().FullName }
        # CIM declares Boolean. Parse a serialized Boolean explicitly if one is encountered;
        # PowerShell's truthiness would incorrectly accept the nonempty string "False".
        if($driver.IsSigned -is [bool]) { $evidence.isSignedAccepted=$driver.IsSigned }
        elseif($driver.IsSigned -is [string]) {
            [bool]$parsedSigned=$false
            $evidence.isSignedAccepted=([bool]::TryParse($driver.IsSigned,[ref]$parsedSigned) -and $parsedSigned)
        }
        # Do not turn FALSE into a success. Direct package verification below is mandatory
        # even when CIM says TRUE, and records this discrepancy if those checks pass.
        if($driver.DriverVersion -ne '11.30.4.434') { $failures.Add('driverVersion') }
        if($driver.DriverProviderName -ne 'MikeTheTech') { $failures.Add('driverProvider') }
        if($driver.InfName -notmatch '^oem\d+\.inf$') { $failures.Add('publishedInfName') }
        else {
            $evidence.publishedInf=Get-VddHashEvidence (Join-Path $env:windir ('INF\'+$driver.InfName)) $script:VddHashes['MttVDD.inf']
            if(-not $evidence.publishedInf.matches) { $failures.Add('publishedInfHash') }
        }
    }
    try {
        $evidence.storeCandidates=@(Get-ChildItem -LiteralPath $evidence.storeRoot -Directory -Filter 'mttvdd.inf_*' | ForEach-Object {
            $candidate=$_.FullName
            $files=@(foreach($entry in $script:VddHashes.GetEnumerator()) {Get-VddHashEvidence (Join-Path $candidate $entry.Key) $entry.Value})
            [pscustomobject]@{path=$candidate;matches=(@($files | Where-Object {-not $_.matches}).Count -eq 0);files=$files}
        })
    } catch { $evidence.storeReadError=$_.Exception.Message; $failures.Add('storeReadError') }
    $evidence.matchingStoreCount=@($evidence.storeCandidates | Where-Object matches).Count
    if($evidence.matchingStoreCount -ne 1) { $failures.Add('matchingStoreCount') }
    else {
        try {
            $evidence.directSignature=Get-VddInstalledSignatureEvidence (($evidence.storeCandidates | Where-Object matches).path)
            if(-not $evidence.directSignature.valid) { $failures.Add('directInstalledSignature') }
            elseif(-not $evidence.isSignedAccepted) {
                $evidence.cimSignatureDiscrepancy=$true
                $evidence.warnings=@('CIM does not report a signature. The pinned UMDF package independently passed SetupAPI trusted-signature, Authenticode chain and catalog-member verification; CIM fields remain unchanged.')
            }
        } catch { $evidence.directSignatureError=$_.Exception.Message; $failures.Add('directInstalledSignature') }
    }
    $evidence.failedChecks=$failures.ToArray()
    $evidence.ready=($failures.Count -eq 0)
    if($evidence.ready) {
        $evidence.binding=[pscustomobject]@{instanceId=$node.InstanceId;publishedInf=$driver.InfName;driverVersion=$driver.DriverVersion;
            signed=$true;signaturePolicy=$evidence.directSignature.policy;signer=$evidence.directSignature.setupApi.DigitalSigner;
            cimIsSigned=$driver.IsSigned;cimSigner=$driver.Signer;cimSignatureDiscrepancy=$evidence.cimSignatureDiscrepancy;
            problemCode=$node.ProblemCode;storePath=($evidence.storeCandidates | Where-Object matches).path}
    }
    $evidence.elapsedMilliseconds=$clock.ElapsedMilliseconds
    [pscustomobject]$evidence
}
function Get-VddInstalledBinding([string]$InstanceId) {
    (Get-VddBindingEvidence $InstanceId).binding
}
function Get-VddDisplaySnapshot {
    Initialize-VddInterop
    foreach ($device in [TVCitation.VirtualDisplay.Inventory]::Devices($null)) {
        $mode=$null
        if ($device.StateFlags -band 1) {
            $current=[TVCitation.VirtualDisplay.Inventory]::Current($device.DeviceName)
            $mode=[pscustomobject]@{width=$current.dmPelsWidth;height=$current.dmPelsHeight;hz=$current.dmDisplayFrequency;x=$current.dmPositionX;y=$current.dmPositionY}
        }
        [pscustomobject]@{name=$device.DeviceName;id=$device.DeviceID;description=$device.DeviceString;attached=[bool]($device.StateFlags -band 1);
            primary=[bool]($device.StateFlags -band 4);mode=$mode;monitors=@([TVCitation.VirtualDisplay.Inventory]::Devices($device.DeviceName) | Select-Object DeviceID,DeviceString)}
    }
}
function Test-VddPhysicalPreserved($Before,$After) {
    foreach ($original in @($Before | Where-Object attached)) {
        $same=@($After | Where-Object id -eq $original.id | Where-Object {
            (($_.monitors.DeviceID | Sort-Object) -join '|') -eq (($original.monitors.DeviceID | Sort-Object) -join '|')
        })
        if ($same.Count -ne 1 -or -not $same[0].attached -or $same[0].primary -ne $original.primary) { return $false }
        foreach ($field in @('width','height','hz','x','y')) { if ($same[0].mode.$field -ne $original.mode.$field) { return $false } }
    }
    return $true
}
function Assert-VddRemovedRetryReceipt($Receipt,[string]$ExpectedDirectory,$Nodes,[bool]$RegistryEmptyOrAbsent,[bool]$PublisherPresent,$PublishedInf,$CurrentDisplays) {
    if ($Receipt.schema -ne 1 -or $Receipt.owner -ne 'TVCITATION' -or $Receipt.phase -ne 'removed' -or
        $Receipt.instanceId -ne 'ROOT\MTTVDD\0000' -or $Receipt.directory -ne $ExpectedDirectory -or
        $Receipt.signerThumbprint -ne $script:VddLeaf -or $Receipt.cleanupError -or -not $Receipt.removedAtUtc) { throw 'The prior receipt is not a completed owned rollback.' }
    if (@($Nodes).Count -ne 0) { throw 'An MttVDD device exists; removed-receipt retry is forbidden.' }
    if (-not $RegistryEmptyOrAbsent) { throw 'VDD registry contains values/subkeys; retry cannot adopt or overwrite it.' }
    if ($Receipt.publisherAdded -and $PublisherPresent) { throw 'The prior added publisher certificate remains; rollback state is inconsistent.' }
    if ((@($Receipt.previousPublishedInf | Sort-Object) -join '|') -ne (@($PublishedInf | Sort-Object) -join '|')) { throw 'Published VDD package baseline differs from the prior receipt.' }
    if (-not (Test-VddPhysicalPreserved $Receipt.displaysBefore $CurrentDisplays)) { throw 'Physical display state differs from the prior receipt.' }
}
function Get-VddRemovedRetryState {
    $directory=Join-Path $env:ProgramData 'TVCITATION\VirtualDisplayDriver'
    $receiptPath=Join-Path $directory 'installation.json'
    if (-not (Test-Path -LiteralPath $receiptPath -PathType Leaf)) { throw 'No removed VDD receipt exists.' }
    $receipt=Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
    $package=Test-VddPackage $directory
    $allowed=@('installation.json','MttVDD.inf','MttVDD.dll','mttvdd.cat','vdd_settings.xml','receipt-history')
    $children=@(Get-ChildItem -LiteralPath $directory -Force)
    if (@($children | Where-Object { $_.Name -notin $allowed -or ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -or ($_.PSIsContainer -and $_.Name -ne 'receipt-history') }).Count) { throw 'Owned directory contains unexpected files, directories, or reparse points.' }
    if ((Get-Item -LiteralPath $directory).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Owned directory is a reparse point.' }
    $acl=Get-Acl -LiteralPath $directory
    if (-not $acl.AreAccessRulesProtected) { throw 'Owned directory permissions are no longer protected.' }
    $writeMask=[Security.AccessControl.FileSystemRights]::Write -bor [Security.AccessControl.FileSystemRights]::Delete -bor
        [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor [Security.AccessControl.FileSystemRights]::ChangePermissions -bor [Security.AccessControl.FileSystemRights]::TakeOwnership
    foreach ($rule in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -eq 'Allow' -and ($rule.FileSystemRights -band $writeMask) -and
            $rule.IdentityReference.Value -notin @('S-1-5-18','S-1-5-32-544')) { throw 'Owned directory permits writes outside SYSTEM/Administrators.' }
    }
    if ((Get-FileHash -LiteralPath (Join-Path $directory 'vdd_settings.xml') -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'vdd_settings.4k120.xml') -Algorithm SHA256).Hash) { throw 'Prepared VDD configuration changed after rollback.' }
    $registry='HKLM:\SOFTWARE\MikeTheTech\VirtualDisplayDriver'
    $empty=$true
    if (Test-Path -LiteralPath $registry) {
        $key=Get-Item -LiteralPath $registry
        $empty=($receipt.registryCreated -and $key.ValueCount -eq 0 -and $key.SubKeyCount -eq 0)
    }
    $published=@(Get-ChildItem -LiteralPath (Join-Path $env:windir 'INF') -Filter 'oem*.inf' -File | Where-Object {
        (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash -eq $script:VddHashes['MttVDD.inf']
    } | Select-Object -ExpandProperty Name)
    Assert-VddRemovedRetryReceipt $receipt $directory $package.existingDevices $empty $package.trustedPublisherPresent $published @(Get-VddDisplaySnapshot)
    [pscustomobject]@{ready=$true;directory=$directory;receiptPath=$receiptPath;previousReceipt=$receipt;publishedInf=$published;registryEmptyOrAbsent=$empty}
}
