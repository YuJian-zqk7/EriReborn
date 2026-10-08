@echo off
rem A launcher, because this machine's PowerShell execution policy is Restricted and
rem refuses to run any .ps1 file directly. A .cmd file is not subject to that policy,
rem and the flag below relaxes it for the child process only.
rem
rem Use this instead of calling the .ps1 directly, or the script simply does nothing
rem and reports nothing - which is how a silent no-op gets mistaken for a passing run.
setlocal
where pwsh >nul 2>nul
if %errorlevel%==0 (
  pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0regression.ps1" %*
) else (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0regression.ps1" %*
)
exit /b %errorlevel%
