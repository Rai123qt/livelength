@echo off
setlocal EnableExtensions
cd /d "%~dp0"
set "SOURCE=%~dp0OUT\net10.0-windows"
set "DEST=%APPDATA%\Autodesk\ApplicationPlugins\LiveLengthManager2027.bundle"
if not exist "%~dp0OUT\loader\LiveLengthManager.Loader.dll" (
    echo ERROR: Loader DLL not found. Please run BUILD.cmd first.
    pause
    exit /b 1
)
if not exist "%SOURCE%\LiveLengthManager.dll" (
    echo ERROR: DLL not found. Please run BUILD.cmd first.
    pause
    exit /b 1
)
if not exist "%DEST%\Contents\app" mkdir "%DEST%\Contents\app"
if errorlevel 1 goto :fail
copy /Y "%~dp0LiveLengthManager2027.bundle\PackageContents.xml" "%DEST%\PackageContents.xml" >nul
if errorlevel 1 goto :fail
copy /Y "%~dp0OUT\loader\LiveLengthManager.Loader.dll" "%DEST%\Contents\LiveLengthManager.Loader.dll" >nul
if errorlevel 1 goto :fail
copy /Y "%SOURCE%\LiveLengthManager.dll" "%DEST%\Contents\app\LiveLengthManager.dll" >nul
if errorlevel 1 goto :fail
rem Pre-3.11 bundles loaded Contents\LiveLengthManager.dll directly; the loader replaces it.
if exist "%DEST%\Contents\LiveLengthManager.dll" del /F /Q "%DEST%\Contents\LiveLengthManager.dll"
if exist "%DEST%\Contents\LiveLengthManager.deps.json" del /F /Q "%DEST%\Contents\LiveLengthManager.deps.json"
if exist "%APPDATA%\LiveLengthManager2027\versions\bad.txt" del /F /Q "%APPDATA%\LiveLengthManager2027\versions\bad.txt"
if exist "%APPDATA%\LiveLengthManager2027\versions\loading.txt" del /F /Q "%APPDATA%\LiveLengthManager2027\versions\loading.txt"
echo Installed AutoCAD 2027 bundle: "%DEST%"
echo Restart AutoCAD 2027 and run LLMGR.
echo The 2026 bundle (LiveLengthManager.bundle) remains untouched.
pause
exit /b 0
:fail
echo ERROR: Installation failed. Close AutoCAD, check permissions, retry.
pause
exit /b 1
