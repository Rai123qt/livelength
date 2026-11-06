@echo off
setlocal
set "DEST=%APPDATA%\Autodesk\ApplicationPlugins\LiveLengthManager2027.bundle"
if not exist "%DEST%" (
    echo The AutoCAD 2027 bundle is not installed.
    pause
    exit /b 0
)
echo Close AutoCAD 2027 before continuing.
choice /M "Remove LiveLengthManager2027.bundle"
if errorlevel 2 exit /b 0
rmdir /S /Q "%DEST%"
if errorlevel 1 (
    echo ERROR: Could not remove the bundle. Check permissions.
    pause
    exit /b 1
)
if exist "%APPDATA%\LiveLengthManager2027\versions" rmdir /S /Q "%APPDATA%\LiveLengthManager2027\versions"
echo Uninstalled AutoCAD 2027 bundle and downloaded updates. No DWG links were removed.
pause
