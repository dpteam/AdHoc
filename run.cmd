@echo off
setlocal
cd /d "%~dp0bin"
if not exist "AdHocServer.exe" (
  echo AdHocServer.exe not found, run build.cmd first.
  exit /b 1
)
AdHocServer.exe %*
endlocal