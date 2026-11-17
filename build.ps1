# Builds PlugSafe into .\app\PlugSafe.exe for your own use (needs the .NET 10 SDK; the app needs the .NET 10 Desktop Runtime).
# Usage:  powershell -ExecutionPolicy Bypass -File build.ps1 [-Force]
param([switch]$Force)
$ErrorActionPreference = 'Stop'
$src = Join-Path $PSScriptRoot 'src'
$out = Join-Path $PSScriptRoot 'app'

if (-not (Test-Path (Join-Path $src 'icon.ico'))) { & (Join-Path $PSScriptRoot 'make-icon.ps1') }

# Don't pull the rug out from under a running backup (the app marks one with PlugSafe\<PC>\.in-progress).
$busy = Get-PSDrive -PSProvider FileSystem | ForEach-Object {
    Get-ChildItem (Join-Path $_.Root 'PlugSafe\*\.in-progress') -Force -ErrorAction SilentlyContinue
}
if ($busy -and -not $Force) {
    throw "A backup looks like it's running ($($busy[0].FullName)). Wait for it to finish, or use -Force."
}

# The exe can't be overwritten while it's running.
$running = Get-Process PlugSafe -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$out\*" }
if ($running) {
    Write-Host 'Closing the running PlugSafe...'
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 500
}

dotnet publish $src -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none -o $out
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
Write-Host "Built $(Join-Path $out 'PlugSafe.exe')"
