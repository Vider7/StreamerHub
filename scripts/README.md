# scripts

- `install.ps1` (run via `install.bat`): builds the app, installs it to
  `%LOCALAPPDATA%\StreamerHub`, fetches `yt-dlp` + `mpv` into `tools/`, adds
  the Start Menu shortcut. Existing `Config.json` is preserved.
- `release.ps1` (run via `package.bat`): builds the web UI, publishes the
  self-contained server, bundles the music helpers, and writes
  `release/StreamerHub-win-x64.zip` + `release/version.json` (update feed).
- `build.bat` (repo root): dev build into `dist/` for local testing.
