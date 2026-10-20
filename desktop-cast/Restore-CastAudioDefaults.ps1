[CmdletBinding()]
param([Parameter(Mandatory)][string]$SnapshotPath, [switch]$CheckOnly)
try {
    . (Join-Path $PSScriptRoot 'CastAudio.Common.ps1')
    $snapshot = Get-Content -LiteralPath $SnapshotPath -Raw | ConvertFrom-Json
    if ($snapshot.schemaVersion -ne 1 -or $snapshot.kind -notin @('castAudioDefaults','castAudioDriverInstall')) { throw 'Unsupported audio snapshot.' }
    Assert-CastAudioRoleSnapshot $snapshot.defaults
    if ($CheckOnly) {
        Write-CastAudioJson ([ordered]@{status='checkOnly'; savedDefaults=@($snapshot.defaults); currentDefaults=@([TVCitation.CastAudio.Audio]::GetDefaults())})
    } else {
        Assert-CastAudioSunshineStopped
        $results = @(Restore-CastAudioRoleSnapshot $snapshot.defaults)
        $status = if (@($results | Where-Object HResult -lt 0).Count) { 'restoreIncomplete' } else { 'restored' }
        Write-CastAudioJson ([ordered]@{status=$status; roles=$results; defaults=@([TVCitation.CastAudio.Audio]::GetDefaults())})
    }
} catch { [ordered]@{status='error'; message=$_.Exception.Message} | ConvertTo-Json -Compress }
