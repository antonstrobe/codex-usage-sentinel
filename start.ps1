param([switch]$Show)
$ErrorActionPreference = 'Stop'
$serverModePath = Join-Path $PSScriptRoot 'server-mode.json'
if (Test-Path -LiteralPath $serverModePath -PathType Leaf) {
    $serverMode = Get-Content -LiteralPath $serverModePath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($serverMode.enabled -eq $true) {
        Write-Output 'Monitoring runs on the server. Open your Telegram bot and send /menu.'
        return
    }
}
$exePath = Join-Path $PSScriptRoot 'dist\CodexUsageSentinel.exe'
if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) {
    throw 'Local EXE is missing. Run build.ps1 after reviewing the sources. If antivirus removed it, investigate the detection before rebuilding.'
}
# Launch only the reviewed local build. Never rebuild or change Windows startup here.
$mode = if ($Show) { '--show' } else { '--tray' }
$appProcess = Start-Process -FilePath $exePath -WorkingDirectory (Split-Path -Parent $exePath) -ArgumentList $mode -WindowStyle Hidden -PassThru
Write-Output ('Launch requested. PID: ' + $appProcess.Id + '. Check the tray icon; a second launch uses the existing instance.')
