@echo off
REM Build with the C# compiler that ships with Windows -- no SDK needed.
REM Note: that compiler only supports C# 5.
setlocal
cd /d "%~dp0"

set CSC64=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set CSC32=C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe

if exist "%CSC64%" (set CSC=%CSC64%) else (set CSC=%CSC32%)

if not exist "%CSC%" (
  echo [X] Cannot find csc.exe. Is .NET Framework 4.x installed?
  exit /b 1
)

echo Using: %CSC%
"%CSC%" /nologo /target:exe /platform:anycpu /out:bridge.exe ^
  /r:System.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll ^
  bridge.cs wxdb.cs

if errorlevel 1 (
  echo.
  echo [X] Build failed.
  exit /b 1
)

echo.
echo [OK] bridge.exe built.
endlocal
