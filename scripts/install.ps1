


# StreamerHub installer.
# Builds the app, gets the music helper, adds a Start Menu shortcut.

[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'StreamerHub')
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$repo = Split-Path $PSScriptRoot -Parent
$proj = Join-Path $repo 'StreamerHub\StreamerHub.csproj'

function Write-Step([string]$msg) {
    Write-Host ''
    Write-Host "==> $msg" -ForegroundColor Cyan
}

function Write-Ok([string]$msg) {
    Write-Host "    $msg" -ForegroundColor Green
}

function Fetch([string]$url, [string]$dest) {
    if (Test-Path -LiteralPath $dest) { return }
    $tmp = "$dest.partial"
    Invoke-WebRequest -Uri $url -OutFile $tmp
    Move-Item -LiteralPath $tmp -Destination $dest
}

Write-Step 'Checking your computer'
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host 'One small helper (dotnet) is needed the first time.' -ForegroundColor Red
    Write-Host 'Install it free from https://dotnet.microsoft.com/download, then run this again.'
    exit 1
}
Write-Ok "good to go"

Write-Step "Building the app in $InstallDir"
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null

$configBackup = $null
$installedConfig = Join-Path $InstallDir 'Config.json'
if (Test-Path -LiteralPath $installedConfig) {
    $configBackup = Join-Path $env:TEMP "streamerhub_config_backup_$PID.json"
    Copy-Item -LiteralPath $installedConfig -Destination $configBackup
    Write-Ok 'your settings are being kept, they will be back in a second'
}

& dotnet publish $proj -c Release -o $InstallDir
if ($LASTEXITCODE -ne 0) {
    Write-Host 'The build failed. See the message above and try again.' -ForegroundColor Red
    exit 1
}

if ($configBackup) {
    Copy-Item -LiteralPath $configBackup -Destination $installedConfig -Force
    Write-Ok 'your settings are back in place'
} elseif (-not (Test-Path -LiteralPath $installedConfig)) {
    $repoConfig = Join-Path $repo 'StreamerHub\Config.json'
    if (Test-Path -LiteralPath $repoConfig) {
        Copy-Item -LiteralPath $repoConfig -Destination $installedConfig
        Write-Ok 'default settings created'
    }
}

$toolsDir = Join-Path $InstallDir 'tools'
New-Item -ItemType Directory -Force -Path $toolsDir | Out-Null

Write-Step 'Getting the music helper (yt-dlp)'
$ytd = Join-Path $toolsDir 'yt-dlp.exe'
Fetch 'https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe' $ytd
Write-Ok "ready at $ytd"

Write-Step 'Getting the audio app (mpv)'
$mpvDir = Join-Path $toolsDir 'mpv'
try {
    $rel = Invoke-RestMethod -Uri 'https://api.github.com/repos/shinchiro/mpv-winbuild-cmake/releases/latest' -Headers @{ 'User-Agent' = 'StreamerHub-installer' }
    $asset = $rel.assets | Where-Object { $_.name -like 'mpv-x86_64-*.7z' } | Select-Object -First 1
    if (-not $asset) { throw 'no mpv release build found' }
    $z7 = Join-Path $toolsDir '7zr.exe'
    Fetch 'https://www.7-zip.org/a/7zr.exe' $z7
    $archive = Join-Path $toolsDir $asset.name
    Fetch $asset.browser_download_url $archive
    $staging = Join-Path $toolsDir 'mpv-stage'
    New-Item -ItemType Directory -Force -Path $staging | Out-Null
    & $z7 x $archive "-o$staging" -y | Out-Null
    $inner = Get-ChildItem -Path $staging -Recurse -Filter 'mpv.exe' | Select-Object -First 1
    if (-not $inner) { throw 'mpv.exe not found in the download' }
    New-Item -ItemType Directory -Force -Path $mpvDir | Out-Null
    Copy-Item -LiteralPath $inner.FullName -Destination $mpvDir -Force
    $d3d = Join-Path $inner.Directory.FullName 'd3dcompiler_43.dll'
    if (Test-Path -LiteralPath $d3d) { Copy-Item -LiteralPath $d3d -Destination $mpvDir -Force }
    Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $archive -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $z7 -Force -ErrorAction SilentlyContinue
    Write-Ok "ready at $(Join-Path $mpvDir 'mpv.exe')"
} catch {
    Write-Host "    The audio app download failed: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host '    The app still works, but music sound needs mpv. Run install.bat again to retry.' -ForegroundColor Yellow
}

Write-Step 'Adding the Start Menu shortcut'
$ws = New-Object -ComObject WScript.Shell
$shortcutPath = Join-Path ([Environment]::GetFolderPath('Programs')) 'Streamer Hub.lnk'
$sc = $ws.CreateShortcut($shortcutPath)
$sc.TargetPath = Join-Path $InstallDir 'StreamerHub.exe'
$sc.WorkingDirectory = $InstallDir
$sc.Description = 'StreamerHub: chat + stats + music for your live'
$sc.Save()
Write-Ok "shortcut ready: $shortcutPath"

Write-Host ''
Write-Host 'Done! StreamerHub is installed.' -ForegroundColor Green
Write-Host "Open 'Streamer Hub' from your Start Menu and your dashboard opens in the browser." -ForegroundColor Cyan
Write-Host 'The app sits in the system tray; right-click it and choose Quit to stop it.'
Write-Host 'To point it at your accounts, edit Config.json in the app folder (your settings were kept).'
Write-Host 'If something looks odd, the log file is at logs\app.log next to the app.'
