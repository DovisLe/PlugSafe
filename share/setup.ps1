# PlugSafe installer: makes sure the .NET 10 Desktop Runtime is installed, copies the app to your user folder,
# adds a Start menu shortcut and (recommended) starts it with Windows so it notices your backup drive.
# Running it again updates PlugSafe and keeps your settings.
param([switch]$Uninstall)

$dest = Join-Path $env:LOCALAPPDATA 'Programs\PlugSafe'
$exe = Join-Path $dest 'PlugSafe.exe'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'PlugSafe.lnk'

function Step($text) { Write-Host "`n> $text" -ForegroundColor Cyan }

# Stops only the copy running from this exact file.
function Stop-App {
    Get-Process PlugSafe -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force
    Start-Sleep -Milliseconds 500
}

function Ask($question) { (Read-Host "`n$question (Y/n)") -notmatch '^[nN]' }

if ($Uninstall) {
    Step 'Removing PlugSafe'
    if (Test-Path $exe) { Start-Process $exe -ArgumentList '--startup', 'off' -Wait }
    Stop-App
    Remove-Item $dest -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $shortcut -Force -ErrorAction SilentlyContinue
    Write-Host "`nPlugSafe is removed. Your files, and the backups on your USB drives, were not touched." -ForegroundColor Green
    return
}

$source = Join-Path $PSScriptRoot 'PlugSafe.exe'
if (-not (Test-Path $source)) {
    Write-Host 'PlugSafe.exe is missing. Unzip the whole folder first, then run "Install PlugSafe" from inside it.' -ForegroundColor Red
    return
}

Step 'Checking the .NET 10 Desktop Runtime'
if (Test-Path "$env:ProgramFiles\dotnet\shared\Microsoft.WindowsDesktop.App\10.*") {
    Write-Host 'Already installed.'
} elseif (Get-Command winget -ErrorAction SilentlyContinue) {
    Write-Host 'Installing it (Windows may ask for permission)...'
    winget install --id Microsoft.DotNet.DesktopRuntime.10 -e --accept-source-agreements --accept-package-agreements --disable-interactivity
} else {
    Write-Host 'Please install ".NET 10 Desktop Runtime (x64)" from https://dotnet.microsoft.com/download/dotnet/10.0 and run this again.' -ForegroundColor Red
    return
}

Step 'Copying PlugSafe'
Stop-App   # updating an older copy
New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item $source $exe -Force
Unblock-File $exe   # drop the "downloaded from the internet" mark so Windows doesn't warn on every start

Step 'Adding a Start menu shortcut'
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($shortcut)
$link.TargetPath = $exe
$link.WorkingDirectory = $dest
$link.Description = 'PlugSafe - automatic USB backups'
$link.Save()

if (Ask 'PlugSafe has to be running to notice your backup drive. Start it automatically when Windows starts?') {
    Start-Process $exe -ArgumentList '--startup', 'on' -Wait
    Write-Host 'It will start with Windows (hidden in the tray). Change this any time from its tray menu.'
}

$updates = Ask 'Check GitHub for PlugSafe updates once a week? Only the version number is fetched; nothing about you or your files is sent.'
Start-Process $exe -ArgumentList '--updates', $(if ($updates) { 'on' } else { 'off' }) -Wait

Start-Process $exe
Write-Host "`nDone! In PlugSafe: plug in your USB drive, click 'Add drive', then choose the files to back up." -ForegroundColor Green
