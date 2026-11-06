$ErrorActionPreference = 'SilentlyContinue'

function Test-AcadDirectory([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return $false }
    foreach ($file in @('AcMgd.dll', 'AcDbMgd.dll', 'AcCoreMgd.dll', 'AdWindows.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $Path $file) -PathType Leaf)) { return $false }
    }
    return $true
}

$candidates = [System.Collections.Generic.List[string]]::new()
foreach ($root in @('HKLM:\SOFTWARE\Autodesk\AutoCAD\R26.0', 'HKLM:\SOFTWARE\WOW6432Node\Autodesk\AutoCAD\R26.0')) {
    foreach ($key in @(Get-Item -LiteralPath $root) + @(Get-ChildItem -LiteralPath $root)) {
        if ($null -eq $key) { continue }
        $properties = Get-ItemProperty -LiteralPath $key.PSPath
        foreach ($name in @('AcadLocation', 'InstallLocation', 'Location')) {
            if ($properties.$name) { $candidates.Add([string]$properties.$name) }
        }
    }
}
foreach ($drive in [System.IO.DriveInfo]::GetDrives()) {
    if (-not $drive.IsReady -or $drive.DriveType -ne 'Fixed') { continue }
    foreach ($folder in @('Program Files', 'Program Files (x86)', 'Autodesk')) {
        $candidates.Add((Join-Path $drive.RootDirectory.FullName "$folder\Autodesk\AutoCAD 2027"))
        if ($folder -eq 'Autodesk') {
            $candidates.Add((Join-Path $drive.RootDirectory.FullName 'Autodesk\AutoCAD 2027'))
        }
    }
}
foreach ($path in $candidates) {
    if (Test-AcadDirectory $path) { Write-Output $path; exit 0 }
}
exit 1
