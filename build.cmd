@echo off
setlocal enabledelayedexpansion
rem ============================================================
rem  DPT.Agent AdHoc Server - build script (no NuGet, no MSBuild)
rem  Targets .NET Framework 4.0 so it still runs on Windows XP SP3.
rem ============================================================

set "ROOT=%~dp0"
set "SRC=%ROOT%src\AdHocServer"
set "OUT=%ROOT%bin"

if not exist "%OUT%" mkdir "%OUT%"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo [ERROR] csc.exe from .NET Framework 4.x was not found.
  echo         Install .NET Framework 4.0 or newer and try again.
  exit /b 1
)

echo [1/4] collecting sources...
if exist "%OUT%\sources.rsp" del "%OUT%\sources.rsp"
for /r "%SRC%" %%f in (*.cs) do echo "%%f">>"%OUT%\sources.rsp"

echo [2/4] compiling with %CSC%
"%CSC%" /nologo /target:exe /platform:anycpu /langversion:4 /optimize+ /debug:pdbonly ^
  /warn:4 /define:TRACE ^
  /out:"%OUT%\AdHocServer.exe" ^
  /reference:System.dll /reference:System.Core.dll /reference:System.Xml.dll ^
  @"%OUT%\sources.rsp"

if errorlevel 1 (
  echo.
  echo [ERROR] build failed.
  exit /b 1
)

echo [3/4] copying web assets...
if exist "%OUT%\public" rmdir /s /q "%OUT%\public"
xcopy "%ROOT%public" "%OUT%\public\" /E /I /Y /Q >nul
xcopy "%ROOT%config.example.ini" "%OUT%\" /Y /Q >nul
if not exist "%OUT%\data" mkdir "%OUT%\data"
if not exist "%OUT%\logs" mkdir "%OUT%\logs"
if exist "%ROOT%data\games.json" xcopy "%ROOT%data\games.json" "%OUT%\data\" /D /Y >nul

echo [4/4] done.
echo.
echo   exe   : %OUT%\AdHocServer.exe
echo   run   : run.cmd
echo   public: http://localhost:44501/   admin: http://localhost:44502/  (bin\config.ini)
echo.
endlocal