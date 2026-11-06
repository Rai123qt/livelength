@echo off
setlocal EnableExtensions
cd /d "%~dp0"
set "ACAD_DIR=%~1"
if not defined ACAD_DIR if defined ACAD_2027_DIR set "ACAD_DIR=%ACAD_2027_DIR%"
if not defined ACAD_DIR for /f "usebackq delims=" %%I in (`powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0FIND_AUTOCAD_2027.ps1"`) do if not defined ACAD_DIR set "ACAD_DIR=%%I"
if not defined ACAD_DIR (
    echo ERROR: AutoCAD 2027 installation was not found.
    goto :fail
)
echo LiveLength Manager 2027 - build and install
for %%D in (AcMgd.dll AcDbMgd.dll AcCoreMgd.dll AdWindows.dll) do (
    if not exist "%ACAD_DIR%\%%D" (
        echo ERROR: Missing AutoCAD 2027 library: "%ACAD_DIR%\%%D"
        goto :fail
    )
)
set "DOTNET_EXE=dotnet"
if exist "%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe" set "DOTNET_EXE=%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe"
"%DOTNET_EXE%" --list-sdks | findstr /R /C:"^10\." >nul
if errorlevel 1 (
    echo ERROR: .NET SDK 10 is required. Install it from https://dotnet.microsoft.com/download/dotnet/10.0
    goto :fail
)
"%DOTNET_EXE%" build "%~dp0LiveLengthManager\LiveLengthManager.csproj" -c Release -f net10.0-windows "-p:AcadDir=%ACAD_DIR%" -o "%~dp0OUT\net10.0-windows"
if errorlevel 1 goto :fail
"%DOTNET_EXE%" build "%~dp0LiveLengthManager.Loader\LiveLengthManager.Loader.csproj" -c Release -f net10.0-windows "-p:AcadDir=%ACAD_DIR%" -o "%~dp0OUT\loader"
if errorlevel 1 goto :fail
set "SOURCE=%~dp0OUT\net10.0-windows"
set "DEST=%APPDATA%\Autodesk\ApplicationPlugins\LiveLengthManager2027.bundle"
if not exist "%SOURCE%\LiveLengthManager.dll" goto :fail
tasklist /FI "IMAGENAME eq acad.exe" 2>nul | findstr /I /C:"acad.exe" >nul
if not errorlevel 1 (
    echo ERROR: Close AutoCAD before installing, then run this file again.
    goto :fail
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
echo SUCCESS: Installed to "%DEST%"
echo Restart AutoCAD 2027 and enter LLMGR.
pause
exit /b 0
:fail
echo Build or installation could not complete. See the error above.
pause
exit /b 1
