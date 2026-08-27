@echo off
rem ---------------------------------------------------------------------------
rem  Double-click this to get WinSnipper running from nothing.
rem
rem  Builds it if there's no build, turns on autostart, installs the keep-alive
rem  task that brings it back if it ever dies, drops a desktop shortcut, and
rem  starts it. Safe to run again any time -- it only does what's missing.
rem ---------------------------------------------------------------------------
setlocal
set "SCRIPT=%~dp0tools\winsnipper.ps1"

where pwsh >nul 2>&1
if %errorlevel%==0 (
    pwsh -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%" setup
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%" setup
)

echo.
pause
