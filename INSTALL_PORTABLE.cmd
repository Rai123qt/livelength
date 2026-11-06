@echo off
setlocal
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0INSTALL_PORTABLE.ps1"
set "RESULT=%ERRORLEVEL%"
echo.
if "%RESULT%"=="0" (echo Installation complete.) else (echo Installation failed. See the message above.)
pause
exit /b %RESULT%
