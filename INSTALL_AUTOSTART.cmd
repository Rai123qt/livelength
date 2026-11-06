@echo off
setlocal EnableExtensions
cd /d "%~dp0"
set "SOURCE=%~dp0OUT\net10.0-windows"
set "DEST=%APPDATA%\Autodesk\ApplicationPlugins\LiveLengthManager2027.bundle"
if not exist "%SOURCE%\LiveLengthManager.dll" (
    echo ERROR: DLL not found. Please run BUILD.cmd first.
    pause
    exit /b 1
)
if not exist "%DEST%\Contents" mkdir "%DEST%\Contents"
if errorlevel 1 goto :fail
copy /Y "%~dp0LiveLengthManager2027.bundle\PackageContents.xml" "%DEST%\PackageContents.xml" >nul
if errorlevel 1 goto :fail
copy /Y "%SOURCE%\LiveLengthManager.dll" "%DEST%\Contents\LiveLengthManager.dll" >nul
if errorlevel 1 goto :fail
if exist "%SOURCE%\LiveLengthManager.deps.json" copy /Y "%SOURCE%\LiveLengthManager.deps.json" "%DEST%\Contents\" >nul
if exist "%SOURCE%\LiveLengthManager.runtimeconfig.json" copy /Y "%SOURCE%\LiveLengthManager.runtimeconfig.json" "%DEST%\Contents\" >nul
echo Installed AutoCAD 2027 bundle: "%DEST%"
echo Restart AutoCAD 2027 and run LLMGR.
echo The 2026 bundle (LiveLengthManager.bundle) remains untouched.
pause
exit /b 0
:fail
echo ERROR: Installation failed. Close AutoCAD, check permissions, retry.
pause
exit /b 1
