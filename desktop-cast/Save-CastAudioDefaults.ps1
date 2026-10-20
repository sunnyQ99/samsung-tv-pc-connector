[CmdletBinding()]
param([Parameter(Mandatory)][string]$SnapshotPath)
try {
    . (Join-Path $PSScriptRoot 'CastAudio.Common.ps1')
    Assert-CastAudioSunshineStopped
    $full = [IO.Path]::GetFullPath($SnapshotPath)
    if (Test-Path -LiteralPath $full) { throw 'snapshotExists: Refusing to overwrite the original default endpoints.' }
    $snapshot = [ordered]@{ schemaVersion=1; kind='castAudioDefaults'; createdUtc=[DateTime]::UtcNow.ToString('o'); defaults=@([TVCitation.CastAudio.Audio]::GetDefaults()) }
    Save-CastAudioJsonFile $full $snapshot
    Write-CastAudioJson ([ordered]@{status='saved'; snapshotPath=$full; defaults=$snapshot.defaults})
} catch { [ordered]@{status='error'; message=$_.Exception.Message} | ConvertTo-Json -Compress }
