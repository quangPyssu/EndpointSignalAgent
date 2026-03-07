@echo off
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0remove-logon-startup.ps1"
if errorlevel 1 (
  echo.
  echo Remove failed.
  pause
  exit /b %errorlevel%
)
echo.
echo Startup removal completed.
pause
