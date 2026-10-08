# Author: Scott Sheen (with ChatGPT)
# Created: 2026-10-08
# Purpose: Launch the repository's published application.
$exe = Join-Path $PSScriptRoot 'artifacts\PowerMonitor\PowerMonitor.exe'
if (-not (Test-Path -LiteralPath $exe)) {
    throw 'Publish the application first; see README.md.'
}
Start-Process -FilePath $exe -WindowStyle Hidden
