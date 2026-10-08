@echo off
rem Launcher for tools\pack.ps1. The machine PowerShell policy is Restricted, so a
rem bare .ps1 silently does nothing; only this .cmd wrapper is reliable.
setlocal
pushd "%~dp0.."
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0pack.ps1" %*
set EXITCODE=%ERRORLEVEL%
popd
endlocal & exit /b %EXITCODE%
