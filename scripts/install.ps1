


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

function Fetch([string]$url, [string]$dest, [string]$sha256) {
    if (Test-Path -LiteralPath $dest) { return }
    $tmp = "$dest.partial"
    Invoke-WebRequest -Uri $url -OutFile $tmp
    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $tmp).Hash.ToLowerInvariant()
    if ($actual -ne $sha256.ToLowerInvariant()) {
        Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
        throw "hash mismatch for $(Split-Path $dest -Leaf): got $actual. Upstream likely released a new build - re-pin the hashes in this script (see below)."
    }
    Move-Item -LiteralPath $tmp -Destination $dest
}

# Pinned helper binaries (trust-on-first-use, hashed 2026-09-29). Every fresh
# download is SHA-256 verified; a mismatch fails closed. When upstream ships a
# new build, download it yourself, hash it with
#   (Get-FileHash -Algorithm SHA256 <file>).Hash
# and update the pins here. Existing installs skip downloads entirely.
$YtdlpUrl = 'https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe'
$YtdlpSha = '66674953FE251B89F4D08C5F0E35E0728679BD67AB3D7D05C0562AF101DD3E7A'
$ZrUrl = 'https://www.7-zip.org/a/7zr.exe'
$ZrSha = 'AD4C82FADCBDF93C03B4FC440F300509C7D60C5C2F4D183E35D9D70D6957037D'
$MpvUrl = 'https://github.com/shinchiro/mpv-winbuild-cmake/releases/download/20260928/mpv-x86_64-20260928-git-e470f8986e.7z'
$MpvName = 'mpv-x86_64-20260928-git-e470f8986e.7z'
$MpvSha = '6491BA670F836553FDD0D965C6E99ED8AA4053C1C79A5E4E30A1E127DA64714A'

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
Fetch $YtdlpUrl $ytd $YtdlpSha
Write-Ok "ready at $ytd"

Write-Step 'Getting the audio app (mpv)'
$mpvDir = Join-Path $toolsDir 'mpv'
try {
    $z7 = Join-Path $toolsDir '7zr.exe'
    Fetch $ZrUrl $z7 $ZrSha
    $archive = Join-Path $toolsDir $MpvName
    Fetch $MpvUrl $archive $MpvSha
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
