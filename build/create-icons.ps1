<#
.SYNOPSIS
  Generates icon assets for all installer platforms.

.DESCRIPTION
  Without -SourcePng: creates simple placeholder PNGs (blue background + "PDF" text).
  With -SourcePng: resizes your high-resolution source image to all required sizes.

  Required sizes:
    MSIX   : Square44x44, Square150x150, Wide310x150, StoreLogo (50x50), SplashScreen (620x300)
    Linux  : HisuiPDF.png 256x256
    macOS  : AppIcon.icns (must be created manually with iconutil on macOS, see note below)

.PARAMETER SourcePng
  Optional path to a high-resolution source PNG (512x512 or larger recommended).

.EXAMPLE
  # Generate placeholders
  .\build\create-icons.ps1

  # Resize from your own icon
  .\build\create-icons.ps1 -SourcePng path\to\icon-1024.png
#>
param([string]$SourcePng = "")

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$assetsDir = Join-Path $root "src\Hisui.Pdf.App\Assets"

function New-PngDir([string]$Path) {
    $dir = Split-Path $Path -Parent
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
}

function New-PlaceholderPng([string]$Path, [int]$W, [int]$H, [string]$Label = "PDF") {
    New-PngDir $Path
    $bmp = New-Object System.Drawing.Bitmap($W, $H)
    $g   = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit

    $bg = [System.Drawing.Color]::FromArgb(255, 31, 97, 153)
    $g.Clear($bg)

    $fontSize = [Math]::Max(7, [int]($H * 0.20))
    $font  = New-Object System.Drawing.Font("Segoe UI", $fontSize, [System.Drawing.FontStyle]::Bold)
    $brush = [System.Drawing.Brushes]::White
    $sf    = New-Object System.Drawing.StringFormat
    $sf.Alignment     = [System.Drawing.StringAlignment]::Center
    $sf.LineAlignment = [System.Drawing.StringAlignment]::Center
    $rect  = New-Object System.Drawing.RectangleF(0, 0, $W, $H)
    $g.DrawString($Label, $font, $brush, $rect, $sf)

    $font.Dispose(); $g.Dispose()
    $bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "  [placeholder] $Path"
}

function Resize-Png([string]$Src, [string]$Dst, [int]$W, [int]$H) {
    New-PngDir $Dst
    $s = New-Object System.Drawing.Bitmap($Src)
    $d = New-Object System.Drawing.Bitmap($W, $H)
    $g = [System.Drawing.Graphics]::FromImage($d)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.DrawImage($s, 0, 0, $W, $H)
    $g.Dispose(); $s.Dispose()
    $d.Save($Dst, [System.Drawing.Imaging.ImageFormat]::Png)
    $d.Dispose()
    Write-Host "  [resized] $Dst"
}

# Targets
$icons = @(
    @{ Rel = "appx\Square44x44Logo.png";     W = 44;  H = 44;  L = "PDF"       }
    @{ Rel = "appx\Square150x150Logo.png";   W = 150; H = 150; L = "Hisui PDF" }
    @{ Rel = "appx\Wide310x150Logo.png";     W = 310; H = 150; L = "Hisui PDF" }
    @{ Rel = "appx\StoreLogo.png";           W = 50;  H = 50;  L = "PDF"       }
    @{ Rel = "appx\SplashScreen.png";        W = 620; H = 300; L = "Hisui PDF" }
    @{ Rel = "linux\HisuiPDF.png";           W = 256; H = 256; L = "Hisui PDF" }
)

if ($SourcePng -and (Test-Path $SourcePng)) {
    Write-Host "Resizing from: $SourcePng"
    foreach ($i in $icons) {
        Resize-Png $SourcePng (Join-Path $assetsDir $i.Rel) $i.W $i.H
    }
} else {
    if ($SourcePng) { Write-Warning "Source PNG not found: $SourcePng — generating placeholders." }
    else            { Write-Host    "No source PNG supplied — generating placeholders." }
    foreach ($i in $icons) {
        New-PlaceholderPng (Join-Path $assetsDir $i.Rel) $i.W $i.H $i.L
    }
}

Write-Host ""
Write-Host "macOS .icns: must be created on macOS from a 1024x1024 PNG:"
Write-Host "  mkdir AppIcon.iconset"
Write-Host "  sips -z 512 512 icon.png --out AppIcon.iconset/icon_512x512.png"
Write-Host "  # (add other sizes as needed)"
Write-Host "  iconutil -c icns AppIcon.iconset"
Write-Host "  cp AppIcon.icns src/Hisui.Pdf.App/Assets/osx/AppIcon.icns"
Write-Host ""
Write-Host "Replace placeholders with real branding before release."
Write-Host "Usage: .\build\create-icons.ps1 -SourcePng path\to\icon-1024.png"
