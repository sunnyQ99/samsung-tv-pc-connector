[CmdletBinding()]
param(
    [ValidateSet('TvOnly','Restore')][string]$Mode = 'TvOnly',
    [string]$ConfigPath = (Join-Path $PSScriptRoot 'sunshine.conf'),
    [Parameter(Mandatory)][string]$ReceiptPath,
    [string]$EndpointId,
    [switch]$CheckOnly
)
try {
    . (Join-Path $PSScriptRoot 'CastAudio.Common.ps1')
    Assert-CastAudioSunshineStopped
    $config = [IO.Path]::GetFullPath($ConfigPath)
    $receiptFile = [IO.Path]::GetFullPath($ReceiptPath)
    if (-not [IO.File]::Exists($config)) { throw 'configMissing: Sunshine configuration was not found.' }
    if ([string]::Equals($config,$receiptFile,[StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid receipt path.' }
    $currentHash = (Get-FileHash -LiteralPath $config -Algorithm SHA256).Hash
    if ($Mode -eq 'Restore') {
        $receipt = [IO.File]::ReadAllText($receiptFile) | ConvertFrom-Json
        if ($receipt.schemaVersion -ne 1 -or $receipt.kind -ne 'castAudioConfiguration' -or -not [string]::Equals($config,$receipt.configPath,[StringComparison]::OrdinalIgnoreCase)) { throw 'invalidReceipt: Configuration receipt does not match.' }
        if ($currentHash -eq $receipt.originalSha256) {
            $result = @{status='alreadyRestored'; configPath=$config; receiptPath=$receiptFile}
        } else {
            if ($currentHash -ne $receipt.appliedSha256) { throw 'configChanged: Configuration changed since this audio mode was applied; refusing to overwrite newer changes.' }
            $backup = [IO.Path]::GetFullPath([string]$receipt.backupPath)
            if ((Get-FileHash -LiteralPath $backup -Algorithm SHA256).Hash -ne $receipt.originalSha256) { throw 'backupChanged: Original configuration backup hash does not match.' }
            if ($CheckOnly) { $result = @{status='wouldRestore'; configPath=$config; receiptPath=$receiptFile} }
            else {
                Assert-CastAudioSunshineStopped
                if ((Get-FileHash -LiteralPath $config -Algorithm SHA256).Hash -ne $currentHash) { throw 'configChanged: Configuration changed before restore.' }
                Set-CastAudioFileBytes $config ([IO.File]::ReadAllBytes($backup))
                $receipt.status='restored'
                Save-CastAudioJsonFile $receiptFile $receipt
                $result = @{status='restored'; configPath=$config; receiptPath=$receiptFile}
            }
        }
    } else {
        $matches = @(Get-CastAudioSteamEndpoints | Where-Object Id -eq $EndpointId)
        if ($matches.Count -ne 1) { throw 'endpointMissing: EndpointId must identify one active Steam Streaming Speakers render endpoint.' }
        if ([IO.File]::Exists($receiptFile)) {
            $existing = [IO.File]::ReadAllText($receiptFile) | ConvertFrom-Json
            if ($existing.schemaVersion -eq 1 -and $existing.kind -eq 'castAudioConfiguration' -and [string]::Equals($config,$existing.configPath,[StringComparison]::OrdinalIgnoreCase) -and $existing.endpointId -eq $EndpointId -and $existing.appliedSha256 -eq $currentHash) {
                $result = @{status='alreadyConfigured'; endpointId=$EndpointId; configPath=$config; receiptPath=$receiptFile}
            } else { throw 'receiptExists: Use a new receipt path for a new configuration change.' }
        } else {
            $original = [IO.File]::ReadAllBytes($config)
            $text = [IO.File]::ReadAllText($config)
            $updated = ConvertTo-CastAudioConfiguration $text $EndpointId
            if ($CheckOnly) { $result=@{status='wouldConfigure'; endpointId=$EndpointId; configPath=$config; virtual_sink=$EndpointId; install_steam_audio_drivers='disabled'} }
            else {
                $backup = $receiptFile + '.original.conf'
                if ([IO.File]::Exists($backup)) { throw 'backupExists: Use a new receipt path.' }
                [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($receiptFile)) | Out-Null
                # Persist recovery information before modifying the configuration.
                $stream = [IO.File]::Open($backup,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
                try { $stream.Write($original,0,$original.Length) } finally { $stream.Dispose() }
                $bytes = [Text.UTF8Encoding]::new($false).GetBytes($updated)
                $sha = [Security.Cryptography.SHA256]::Create()
                try { $appliedHash = [BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-','') } finally { $sha.Dispose() }
                $receipt = [ordered]@{schemaVersion=1; kind='castAudioConfiguration'; status='pending'; createdUtc=[DateTime]::UtcNow.ToString('o'); configPath=$config; backupPath=$backup; originalSha256=$currentHash; appliedSha256=$appliedHash; endpointId=$EndpointId}
                Save-CastAudioJsonFile $receiptFile $receipt
                Assert-CastAudioSunshineStopped
                if ((Get-FileHash -LiteralPath $config -Algorithm SHA256).Hash -ne $currentHash) { throw 'configChanged: Configuration changed before update.' }
                Set-CastAudioFileBytes $config $bytes
                $receipt.status='configured'; Save-CastAudioJsonFile $receiptFile $receipt
                $result=@{status='configured'; endpointId=$EndpointId; configPath=$config; receiptPath=$receiptFile}
            }
        }
    }
} catch { $result=@{status='error'; message=$_.Exception.Message} }
$result | ConvertTo-Json -Depth 12 -Compress
