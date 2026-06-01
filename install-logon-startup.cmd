@echo off
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install-logon-startup.ps1"
if errorlevel 1 (
  echo.
  echo Setup failed.
  pause
  exit /b %errorlevel%
)
echo.
echo Startup setup completed.
pause
