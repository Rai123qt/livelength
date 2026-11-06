@echo off
setlocal EnableExtensions
cd /d "%~dp0"
rem Optional parameter: BUILD.cmd "D:\Custom\AutoCAD 2027"
set "ACAD_DIR=%~1"
if not defined ACAD_DIR if defined ACAD_2027_DIR set "ACAD_DIR=%ACAD_2027_DIR%"
if not defined ACAD_DIR for /f "usebackq delims=" %%I in (`powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0FIND_AUTOCAD_2027.ps1"`) do if not defined ACAD_DIR set "ACAD_DIR=%%I"
if not defined ACAD_DIR (
    echo ERROR: AutoCAD 2027 installation was not found.
    echo Pass its directory as an argument: BUILD.cmd "D:\Path\AutoCAD 2027"
    pause
    exit /b 1
)
echo [LiveLength] Target: AutoCAD 2027 / .NET 10 / x64
for %%D in (AcMgd.dll AcDbMgd.dll AcCoreMgd.dll AdWindows.dll) do (
    if not exist "%ACAD_DIR%\%%D" (
        echo ERROR: Missing "%ACAD_DIR%\%%D"
        echo Install AutoCAD 2027 or pass its installation directory to BUILD.cmd.
        echo Example: BUILD.cmd "D:\Autodesk\AutoCAD 2027"
        pause
        exit /b 1
    )
)
set "DOTNET_EXE=dotnet"
if exist "%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe" set "DOTNET_EXE=%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe"
"%DOTNET_EXE%" --list-sdks | findstr /R /C:"^10\." >nul
if errorlevel 1 (
    echo ERROR: .NET 10 SDK not detected. Download it from https://dotnet.microsoft.com/download/dotnet/10.0
    pause
    exit /b 1
)
echo Installed .NET SDKs:
"%DOTNET_EXE%" --list-sdks
set "OUTPUT=%~dp0OUT\net10.0-windows"
"%DOTNET_EXE%" build "%~dp0LiveLengthManager\LiveLengthManager.csproj" -c Release -f net10.0-windows "-p:AcadDir=%ACAD_DIR%" -o "%OUTPUT%"
if errorlevel 1 (
    echo ERROR: Compilation failed. Keep the full error output to troubleshoot.
    pause
    exit /b 1
)
if not exist "%OUTPUT%\LiveLengthManager.dll" (
    echo ERROR: Build returned but output DLL was not found.
    pause
    exit /b 1
)
echo.
echo BUILD OK: "%OUTPUT%\LiveLengthManager.dll"
echo Next: close AutoCAD and run INSTALL_AUTOSTART.cmd.
pause
exit /b 0
