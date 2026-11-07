<#
.SYNOPSIS
    Builds the private Zenith Macros Developer Edition with Master Administrator privileges
    and updates your Desktop shortcut.
#>

param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path "$PSScriptRoot\.."
Set-Location $root

Write-Host "================================================================" -ForegroundColor Cyan
Write-Host "  Building Zenith Macros -- DEVELOPER EDITION [Master Admin]    " -ForegroundColor Cyan
Write-Host "================================================================" -ForegroundColor Cyan

# 1. Build with DeveloperMode=true
$proj = "$root\src\MacroSuite.App\MacroSuite.App.csproj"
dotnet build $proj -c $Configuration -p:DeveloperMode=true

if ($LASTEXITCODE -ne 0) {
    Write-Error "Build failed with exit code $LASTEXITCODE."
    exit $LASTEXITCODE
}

$binDir = "$root\src\MacroSuite.App\bin\$Configuration\net8.0-windows10.0.19041.0"
$exePath = "$binDir\MacroSuite.App.exe"

if (-not (Test-Path $exePath)) {
    Write-Error "Executable not found at $exePath"
    exit 1
}

# 2. Update Desktop Shortcut to point to your Developer Build
$desktop = [Environment]::GetFolderPath('Desktop')
$shortcutPath = Join-Path $desktop "Zenith Macros.lnk"

$wsh = New-Object -ComObject WScript.Shell
$shortcut = $wsh.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $exePath
$shortcut.WorkingDirectory = $binDir
$shortcut.Description = "Zenith Macros - Developer Edition"
$shortcut.IconLocation = "$exePath,0"
$shortcut.Save()

Write-Host "`n[SUCCESS] Developer Edition built successfully!" -ForegroundColor Green
Write-Host "  Executable: $exePath" -ForegroundColor Gray
Write-Host "  Shortcut:   $shortcutPath (Linked to Developer Build)" -ForegroundColor Gray
Write-Host "  Privileges: Master Administrator (Unlimited)" -ForegroundColor Yellow
