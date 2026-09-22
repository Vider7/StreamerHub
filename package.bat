@echo off
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\release.ps1" %*
if errorlevel 1 (
  echo.
  echo Packaging failed - see messages above.
  pause
)
endlocal