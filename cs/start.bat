@echo off
REM Build (if needed) and launch the bridge. Double-click this file.
setlocal
cd /d "%~dp0"

if not exist bridge.exe (
  echo bridge.exe not found, building...
  call build.bat || exit /b 1
)

bridge.exe %*
endlocal
