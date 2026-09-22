[CmdletBinding()]
param(
    [string]$ConfigFile = 'StreamerHub\Config.json'
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = Split-Path $PSScriptRoot -Parent
$stage = Join-Path $root 'release\stage'
$out = Join-Path $root 'release'
$zip = Join-Path $out 'StreamerHub-win-x64.zip'

Write-Host '==> building web UI (1/6)'
Push-Location (Join-Path $root 'web')
& npm install --no-audit --no-fund | Out-Null
& npm run build
if ($LASTEXITCODE -ne 0) { Pop-Location; throw 'web build failed' }
Pop-Location

Write-Host '==> publishing server, self-contained win-x64 (2/6)'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage | Out-Null
& dotnet publish (Join-Path $root 'StreamerHub\StreamerHub.csproj') -c Release -r win-x64 --self-contained true -o $stage
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }
if (-not (Test-Path (Join-Path $stage 'StreamerHub.exe'))) { throw 'StreamerHub.exe missing after publish' }

Write-Host '==> cleaning stage: no logs, no debug symbols (3/6)'
Remove-Item (Join-Path $stage 'logs') -Recurse -Force -ErrorAction SilentlyContinue
Get-ChildItem $stage -Filter '*.pdb' -Recurse | Remove-Item -Force -ErrorAction SilentlyContinue

Write-Host '==> bundling music helpers: mpv + yt-dlp (4/6)'
if (Test-Path (Join-Path $root 'dist\tools')) {
    Copy-Item (Join-Path $root 'dist\tools') $stage -Recurse -Force
} else {
    Write-Host '   WARNING: dist\tools not found - the zip will have no music. Run install.bat once first.' -ForegroundColor Yellow
}

Write-Host '==> shipping a fresh default config + version (5/6)'
$tmpl = Join-Path $root $ConfigFile
if (Test-Path $tmpl) { Copy-Item $tmpl (Join-Path $stage 'Config.json') -Force }
$verPath = Join-Path $root 'StreamerHub\version.txt'
$version = if (Test-Path $verPath) { (Get-Content $verPath).Trim() } else { '1.5.0' }

Write-Host '==> zipping'
New-Item -ItemType Directory -Path $out -Force | Out-Null
if (Test-Path $zip) { Remove-Item $zip -Force }
tar.exe -a -cf $zip -C $stage .
if ($LASTEXITCODE -ne 0) { throw 'zip failed' }
Remove-Item $stage -Recurse -Force

$sha = (Get-FileHash -Algorithm SHA256 -LiteralPath $zip).Hash.ToLowerInvariant()
$manifest = Join-Path $root 'version.json'
$feed = @{
    version = $version
    url     = "https://github.com/Vider7/StreamerHub/releases/download/v$version/StreamerHub-win-x64.zip"
    sha256  = $sha
} | ConvertTo-Json
[System.IO.File]::WriteAllText($manifest, $feed, [System.Text.UTF8Encoding]::new($false))

$mb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host ''
Write-Host "Done: $zip ($mb MB)" -ForegroundColor Green
Write-Host "Version: v$version"
Write-Host "SHA256:  $sha"
Write-Host "Feed:    $manifest"
Write-Host ''
Write-Host 'To ship this release (GitHub):' -ForegroundColor Cyan
Write-Host '  1. Create a GitHub Release tagged v$version (use your shell to expand):'
Write-Host "     gh release create v$version $zip --title v$version"
Write-Host '     (or web UI: Releases > Draft a new release, attach the zip).'
Write-Host '  2. Commit the updated version.json to main (it now points at the release asset).'
Write-Host 'Their app checks the feed on startup and every few hours, and shows an "update" button.'
Write-Host 'The zip is SHA-256 verified before anything is replaced - they never read an unverified file.'