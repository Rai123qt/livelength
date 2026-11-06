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
foreach ($relative in @('PackageContents.xml', 'Contents\LiveLengthManager.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $package $relative) -PathType Leaf)) {
        Write-Host "ERROR: The installation package is incomplete: $relative"
        exit 1
    }
}
$plugins = Join-Path $env:APPDATA 'Autodesk\ApplicationPlugins'
$destination = Join-Path $plugins 'LiveLengthManager2027.bundle'
try {
    New-Item -ItemType Directory -Path (Join-Path $destination 'Contents') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $package 'PackageContents.xml') -Destination $destination -Force
    Copy-Item -LiteralPath (Join-Path $package 'Contents\LiveLengthManager.dll') -Destination (Join-Path $destination 'Contents') -Force
    $deps = Join-Path $package 'Contents\LiveLengthManager.deps.json'
    if (Test-Path -LiteralPath $deps) { Copy-Item -LiteralPath $deps -Destination (Join-Path $destination 'Contents') -Force }
    $sourceHash = (Get-FileHash -LiteralPath (Join-Path $package 'Contents\LiveLengthManager.dll') -Algorithm SHA256).Hash
    $installedHash = (Get-FileHash -LiteralPath (Join-Path $destination 'Contents\LiveLengthManager.dll') -Algorithm SHA256).Hash
    if ($sourceHash -ne $installedHash) { throw 'DLL verification failed.' }
    Write-Host "AutoCAD found: $acadPath"
    Write-Host "Plugin installed: $destination"
    Write-Host 'Open AutoCAD 2027 and enter LLMGR.'
    exit 0
} catch {
    Write-Host "ERROR: $($_.Exception.Message)"
    exit 1
}
