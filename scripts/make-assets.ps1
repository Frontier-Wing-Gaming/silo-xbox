# Renders the UWP tile, logo and splash images from one square source icon.
# Usage: powershell -File scripts/make-assets.ps1 -Source <icon.png>
# Commands assume the repository root is the cwd.
param(
  [Parameter(Mandatory = $true)][string]$Source,
  [string]$Out = "Silolux/Assets"
)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
$icon = [System.Drawing.Image]::FromFile((Resolve-Path $Source))
$bg = [System.Drawing.Color]::FromArgb(255, 13, 14, 18)
New-Item -ItemType Directory -Force $Out | Out-Null

function Render([string]$name, [int]$w, [int]$h, [double]$scale, [bool]$transparent) {
  $bmp = New-Object System.Drawing.Bitmap $w, $h
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
  $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
  if ($transparent) { $g.Clear([System.Drawing.Color]::Transparent) } else { $g.Clear($bg) }
  $side = [int]([Math]::Min($w, $h) * $scale)
  $g.DrawImage($icon, [int](($w - $side) / 2), [int](($h - $side) / 2), $side, $side)
  $g.Dispose()
  $bmp.Save((Join-Path $Out $name), [System.Drawing.Imaging.ImageFormat]::Png)
  $bmp.Dispose()
}

Render "Square44x44Logo.png" 44 44 1.0 $true
Render "Square44x44Logo.targetsize-24_altform-unplated.png" 24 24 1.0 $true
Render "Square150x150Logo.png" 150 150 0.6 $false
Render "Wide310x150Logo.png" 310 150 0.6 $false
Render "LargeTile.png" 310 310 0.5 $false
Render "StoreLogo.png" 50 50 1.0 $true
Render "SplashScreen.png" 620 300 0.45 $false
Render "AppIcon.png" 256 256 1.0 $true
$icon.Dispose()
Write-Host "Assets written to $Out"
