@echo off
setlocal
set "SCRIPT=%~dp0Extract-FS2Hercules.ps1"
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%" %*
set "EXITCODE=%ERRORLEVEL%"
if not "%FS2HERCULES_NO_PAUSE%"=="1" pause
exit /b %EXITCODE%
