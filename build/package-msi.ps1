<#
.SYNOPSIS
  Builds the MSI installer for Hisui PDF (Windows x64).

.DESCRIPTION
  1. Installs the WiX v4 dotnet tool if not already present.
  2. Runs dotnet publish (win-x64 profile).
  3. Builds the WiX project → bin/installer/HisuiPDF-{Version}-win-x64.msi

.PARAMETER Version
  Version string embedded in the MSI (default: 1.0.0).

.EXAMPLE
  .\build\package-msi.ps1 -Version 1.0.0
#>
param([string]$Version = "1.0.0")

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

# ---- 1. ensure WiX toolset is installed -------------------------------------

Write-Host "Checking WiX toolset..."
$wixInstalled = & dotnet tool list --global 2>$null | Select-String "wix"
if (-not $wixInstalled) {
    Write-Host "  Installing WiX v4..."
    & dotnet tool install --global wix --version 4.0.5
    if ($LASTEXITCODE -ne 0) { throw "Failed to install WiX toolset" }
} else {
    Write-Host "  WiX already installed: $wixInstalled"
}

# ---- 2. publish win-x64 -----------------------------------------------------

Write-Host ""
Write-Host "Publishing win-x64..."
& dotnet publish "$root\src\Hisui.Pdf.App\Hisui.Pdf.App.csproj" /p:PublishProfile=win-x64
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# ---- 3. build MSI -----------------------------------------------------------

Write-Host ""
Write-Host "Building MSI (Version $Version)..."
$outDir = "$root\bin\installer"
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

& dotnet build "$root\src\Hisui.Pdf.Installer.Win\Hisui.Pdf.Installer.Win.wixproj" `
    -c Release `
    -p:Version=$Version

if ($LASTEXITCODE -ne 0) { throw "WiX build failed" }

$msiPath = "$outDir\HisuiPDF-$Version-win-x64.msi"
Write-Host ""
Write-Host "Output: $msiPath"
Write-Host ""
Write-Host "Install silently (admin):"
Write-Host "  msiexec /i `"$msiPath`" /qn"
Write-Host ""
Write-Host "Install interactively:"
Write-Host "  msiexec /i `"$msiPath`""
