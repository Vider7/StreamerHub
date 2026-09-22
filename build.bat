@echo off
cd /d "%~dp0"

echo Building web UI...
pushd web
call npm install --no-audit --no-fund >nul 2>&1
call npm run build
if errorlevel 1 goto :fail
popd

echo.
echo Building server...
dotnet publish StreamerHub -c Release -r win-x64 --self-contained true -o dist
if errorlevel 1 goto :fail
if not exist dist\StreamerHub.exe goto :fail

echo.
echo Built: dist\StreamerHub.exe
if not exist dist\tools\yt-dlp.exe (
  echo Note: yt-dlp.exe is missing from dist\tools. Run install.bat once, or drop
  echo yt-dlp.exe into dist\tools to enable search and playback.
)
if not exist dist\tools\mpv\mpv.exe (
  echo Note: mpv.exe is missing from dist\tools\mpv. Run install.bat once, or drop
  echo an mpv build into dist\tools\mpv so music can play sound.
)
echo Run it and use it from the browser tab that opens.
pause
exit /b 0

:fail
echo Build failed - see messages above.
pause
exit /b 1