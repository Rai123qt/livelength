$ErrorActionPreference = 'Stop'
$package = Join-Path $PSScriptRoot 'LiveLengthManager2027.bundle'
$acadPath = & (Join-Path $PSScriptRoot 'FIND_AUTOCAD_2027.ps1')
if (-not $acadPath) {
    Write-Host 'ERROR: AutoCAD 2027 was not found on this computer.'
    exit 1
}
if (Get-Process -Name acad -ErrorAction SilentlyContinue) {
    Write-Host 'ERROR: Close AutoCAD before installation, then run this installer again.'
    exit 1
}
$files = @('PackageContents.xml', 'Contents\LiveLengthManager.Loader.dll', 'Contents\app\LiveLengthManager.dll')
foreach ($relative in $files) {
    if (-not (Test-Path -LiteralPath (Join-Path $package $relative) -PathType Leaf)) {
        Write-Host "ERROR: The installation package is incomplete: $relative"
        exit 1
    }
}
$plugins = Join-Path $env:APPDATA 'Autodesk\ApplicationPlugins'
$destination = Join-Path $plugins 'LiveLengthManager2027.bundle'
try {
    New-Item -ItemType Directory -Path (Join-Path $destination 'Contents\app') -Force | Out-Null
    # Versions before 3.11 loaded Contents\LiveLengthManager.dll directly; the loader replaces it.
    foreach ($old in @('Contents\LiveLengthManager.dll', 'Contents\LiveLengthManager.deps.json')) {
        $path = Join-Path $destination $old
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
    }
    foreach ($relative in $files) {
        $target = Join-Path $destination $relative
        Copy-Item -LiteralPath (Join-Path $package $relative) -Destination $target -Force
        $sourceHash = (Get-FileHash -LiteralPath (Join-Path $package $relative) -Algorithm SHA256).Hash
        $installedHash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
        if ($sourceHash -ne $installedHash) { throw "Verification failed: $relative" }
    }
    Write-Host "AutoCAD found: $acadPath"
    Write-Host "Plugin installed: $destination"
    Write-Host 'Open AutoCAD 2027 and enter LLMGR. Later versions update from inside the palette.'
    exit 0
} catch {
    Write-Host "ERROR: $($_.Exception.Message)"
    exit 1
}
