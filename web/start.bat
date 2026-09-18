@echo off
REM Minimal IM bridge launcher. Double-click this file.
REM It starts bridge.py, which also opens the browser for you.
cd /d "%~dp0"
python bridge.py %*
if errorlevel 1 pause
