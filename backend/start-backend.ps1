$ErrorActionPreference = 'Stop'
$backendRoot = Split-Path $PSScriptRoot -Parent
$backendSocket = [Net.Sockets.TcpClient]::new()
try {
    $backendSocket.Connect('127.0.0.1', 18765)
    if ($backendSocket.Connected) { exit 0 }
} catch { } finally { $backendSocket.Dispose() }
$backendPython = (& py -3.14 -c 'import sys; print(sys.executable)').Trim()
if (-not (Test-Path -LiteralPath $backendPython)) { throw 'Python 3.14 is required.' }
$backendLogs = Join-Path $backendRoot 'diagnostics\remote-view'
New-Item -ItemType Directory -Force -Path $backendLogs | Out-Null
$backendProcess = Start-Process -FilePath $backendPython -ArgumentList @('-u', ('"' + (Join-Path $PSScriptRoot 'server.py') + '"')) -WorkingDirectory $backendRoot -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $backendLogs 'backend-stdout.log') -RedirectStandardError (Join-Path $backendLogs 'backend-stderr.log')
$backendProcess.Id | Set-Content -LiteralPath (Join-Path $backendLogs 'backend.pid')
