<#
.SYNOPSIS
    Compiles and packages the public, shippable release of Zenith Macros
    without developer privileges (clean Free tier default, no admin bypass).
#>

param(
    [string]$Version = "2.4.1",
    [switch]$SelfContained = $false
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path "$PSScriptRoot\.."
Set-Location $root

Write-Host "================================================================" -ForegroundColor Green
Write-Host "  Packaging Zenith Macros -- PUBLIC SHIPPING RELEASE v$Version  " -ForegroundColor Green
Write-Host "================================================================" -ForegroundColor Green

$distRoot = "$root\dist"
$packageDirName = if ($SelfContained) { "ZenithMacros-v$Version-Standalone" } else { "ZenithMacros-v$Version" }
$targetDir = "$distRoot\$packageDirName"

if (Test-Path $targetDir) {
    Write-Host "Cleaning previous output in $targetDir..." -ForegroundColor Gray
    Remove-Item -Recurse -Force $targetDir
}
New-Item -ItemType Directory -Force -Path $targetDir | Out-Null

# 1. Publish with DeveloperMode=false
$proj = "$root\src\MacroSuite.App\MacroSuite.App.csproj"
$selfContainedArg = if ($SelfContained) { "--self-contained:true" } else { "--self-contained:false" }

Write-Host "`n[1/4] Publishing application via dotnet publish (DeveloperMode=false)..." -ForegroundColor Cyan
dotnet publish $proj -c Release -r win-x64 $selfContainedArg -p:DeveloperMode=false -o $targetDir

if ($LASTEXITCODE -ne 0) {
    Write-Error "Publish failed with exit code $LASTEXITCODE."
    exit $LASTEXITCODE
}

# 2. Add Launcher Batch File
Write-Host "[2/4] Generating distribution launcher..." -ForegroundColor Cyan
$launcherContent = @"
@echo off
title Zenith Macros
cd /d "%~dp0"
start "" "MacroSuite.App.exe"
"@
Set-Content -Path "$targetDir\Launch-ZenithMacros.bat" -Value $launcherContent -Encoding ASCII

# 3. Add User README
Write-Host "[3/4] Generating user documentation..." -ForegroundColor Cyan
$readmeContent = @"
================================================================================
  ZENITH MACROS — AUTOMATION SUITE (Version $Version)
================================================================================

Welcome to Zenith Macros!

GETTING STARTED
---------------
1. Run 'MacroSuite.App.exe' (or double-click 'Launch-ZenithMacros.bat').
2. Click '+ New Script' to create an automation sequence or click '● REC' (F2)
   to record live keystrokes and mouse movements.
3. Use the Playback controls (Speed multiplier, Repeat count) to replay macros.

KEYBOARD NAVIGATION
-------------------
  ↑ (Up)     : Select previous macro in the list
  ↓ (Down)   : Select next macro in the list
  Enter      : Play currently selected macro
  Space      : Play / Pause playback
  Delete     : Delete selected macro
  Esc        : Emergency Panic Stop / Cancel playback or recording

LICENSING & TIERS
-----------------
Zenith Macros includes a permanent Free Community tier out of the box,
granting unlimited basic input recording, playback, and editing.

To unlock advanced capabilities (OpenCV image recognition, color detection,
multi-modal AI Vision, autonomous safety gating, and plugins), navigate to
the 'Licensing' tab in the top navigation bar and enter your Pro or
Enterprise license key.

REQUIREMENTS
------------
- Windows 10 (version 19041+) or Windows 11 (64-bit)
- .NET 8.0 Desktop Runtime (x64)
  Download if needed: https://dotnet.microsoft.com/download/dotnet/8.0/runtime

SUPPORT & COMMUNITY
-------------------
Documentation: https://zenithmacros.dev/docs
Report Issues: support@zenithmacros.dev

(c) 2026 Zenith Automation Suite. All rights reserved.
"@
Set-Content -Path "$targetDir\README.txt" -Value $readmeContent -Encoding UTF8

# 4. Create ZIP Archive for Distribution
Write-Host "[4/4] Creating distribution ZIP archive..." -ForegroundColor Cyan
Start-Sleep -Seconds 1
$zipFile = "$distRoot\$packageDirName-win-x64.zip"
if (Test-Path $zipFile) {
    Remove-Item -Force $zipFile
}
Add-Type -AssemblyName "System.IO.Compression.FileSystem"
[System.IO.Compression.ZipFile]::CreateFromDirectory($targetDir, $zipFile, [System.IO.Compression.CompressionLevel]::Optimal, $false)

$zipSizeMb = [math]::Round((Get-Item $zipFile).Length / 1MB, 2)

Write-Host "`n================================================================" -ForegroundColor Green
Write-Host "  SHIPPING PACKAGE READY FOR DISTRIBUTION!                      " -ForegroundColor Green
Write-Host "================================================================" -ForegroundColor Green
Write-Host "  Folder:  $targetDir" -ForegroundColor Gray
Write-Host "  ZIP:     $zipFile ($zipSizeMb MB)" -ForegroundColor Yellow
Write-Host "  Profile: Public Release (Free tier default, no admin bypass)" -ForegroundColor White
