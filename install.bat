@echo off
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\install.ps1" %*
if errorlevel 1 (
  echo.
  echo Install failed. See messages above.
  pause
)
endlocal