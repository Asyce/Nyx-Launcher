@echo off
setlocal
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\start-nyx.ps1"
set "nyxExitCode=%ERRORLEVEL%"
endlocal & exit /b %nyxExitCode%
