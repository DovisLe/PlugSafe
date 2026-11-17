# Builds the public release into .\release\ (never includes your settings, drives or logs):
#   PlugSafe.zip                        exe + installer + readme, for installing it the first time
#   PlugSafe.exe, PlugSafe.exe.sha256   attach both to the GitHub release too; the in-app updater uses them
# Usage:  powershell -ExecutionPolicy Bypass -File package.ps1
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$src = Join-Path $root 'src'
$release = Join-Path $root 'release'
$stage = Join-Path $release 'PlugSafe'

if (-not (Test-Path (Join-Path $src 'icon.ico'))) { & (Join-Path $root 'make-icon.ps1') }
Remove-Item $release -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish $src -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none -o $stage
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
Copy-Item (Join-Path $root 'share\*') $stage

$exe = Join-Path $release 'PlugSafe.exe'
Copy-Item (Join-Path $stage 'PlugSafe.exe') $exe
$hash = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path "$exe.sha256" -Value "$hash  PlugSafe.exe" -Encoding ascii -NoNewline
Compress-Archive -Path $stage -DestinationPath (Join-Path $release 'PlugSafe.zip')
Remove-Item $stage -Recurse -Force
Get-ChildItem $release | ForEach-Object { Write-Host ("{0,-22} {1,8:N0} KB" -f $_.Name, ($_.Length / 1KB)) }
