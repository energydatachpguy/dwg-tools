# Finds the AutoCAD / Plant 3D install the plugin is built against.
# Prints one line:  OK|<folder>|<year>   NET8|<folder>|<year>   NONE||
# Order: ACAD_DIR environment variable, then "acad_exe" in settings.json,
# then the newest AutoCAD 2020-2024 under Program Files\Autodesk.
# AutoCAD 2025 and newer run on .NET 8, which this plugin does not support yet.
$ErrorActionPreference = 'SilentlyContinue'
$root = Split-Path -Parent $PSScriptRoot

function YearOf([string]$dir) {
    $m = [regex]::Match((Split-Path -Leaf $dir), '20\d\d')
    if ($m.Success) { return [int]$m.Value }
    $v = (Get-Item (Join-Path $dir 'acad.exe')).VersionInfo.ProductMajorPart
    # AutoCAD R23 = 2019/2020, R24 = 2021-2024, R25 = 2025+
    if ($v -ge 25) { return 2025 }
    if ($v -gt 0) { return 2024 }
    return 0
}
function Report([string]$dir) {
    $y = YearOf $dir
    if ($y -ge 2025) { "NET8|$dir|$y" } else { "OK|$dir|$y" }
    exit 0
}

if ($env:ACAD_DIR -and (Test-Path (Join-Path $env:ACAD_DIR 'acdbmgd.dll'))) { Report $env:ACAD_DIR }

$sp = Join-Path $root 'settings.json'
if (Test-Path $sp) {
    $s = Get-Content $sp -Raw | ConvertFrom-Json
    if ($s.acad_exe) {
        $d = Split-Path -Parent $s.acad_exe
        if (Test-Path (Join-Path $d 'acdbmgd.dll')) { Report $d }
    }
}

$found = @(Get-ChildItem 'C:\Program Files\Autodesk' -Directory |
    Where-Object { $_.Name -like 'AutoCAD*' -and (Test-Path (Join-Path $_.FullName 'acdbmgd.dll')) } |
    ForEach-Object { [pscustomobject]@{ Dir = $_.FullName; Year = (YearOf $_.FullName) } })
$ok = @($found | Where-Object { $_.Year -lt 2025 } | Sort-Object Year)
if ($ok.Count) { Report $ok[-1].Dir }
$new = @($found | Sort-Object Year)
if ($new.Count) { Report $new[-1].Dir }
"NONE||"
