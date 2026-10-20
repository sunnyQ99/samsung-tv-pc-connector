[CmdletBinding()]
param()
try {
    . (Join-Path $PSScriptRoot 'CastAudio.Common.ps1')
    Write-CastAudioJson ([ordered]@{
        status='ok'; timestampUtc=[DateTime]::UtcNow.ToString('o'); administrator=(Test-CastAudioAdministrator)
        sunshineRunning=(@(Get-Process -Name sunshine -ErrorAction SilentlyContinue).Count -gt 0)
        defaults=@([TVCitation.CastAudio.Audio]::GetDefaults()); endpoints=@([TVCitation.CastAudio.Audio]::GetEndpoints())
        steamEndpoints=@(Get-CastAudioSteamEndpoints)
    })
} catch { [ordered]@{status='error'; message=$_.Exception.Message} | ConvertTo-Json -Compress }
