# Renders the Microsoft Store listing art from the Silolux icon:
#   poster-1440x2160.png   9:16 poster art (required for the Xbox Store)
#   boxart-2160x2160.png   1:1 box art
#   tile-300.png, tile-150.png, tile-71.png   Store display images
# Usage: powershell -File scripts/make-store-art.ps1 [-Out assets/store]
# Commands assume the repository root is the cwd.
param([string]$Out = "assets/store")
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force $Out | Out-Null
$Out = (Resolve-Path $Out).Path
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("silolux-art-" + [guid]::NewGuid())
New-Item -ItemType Directory $tmp | Out-Null

function Icon([int]$size) {
  $file = Join-Path $tmp "icon-$size.png"
  & (Join-Path $PSScriptRoot "make-icon.ps1") -Out $file -Size $size
  return [System.Drawing.Image]::FromFile($file)
}

function Canvas([int]$w, [int]$h) {
  $bmp = New-Object System.Drawing.Bitmap $w, $h
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
  $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
  $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
  # The app's background, lifting toward indigo at the bottom.
  # The gradient runs a little past both edges: GDI+ wraps a brush that ends exactly at
  # the border, which leaves a light seam along the top and left.
  $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    (New-Object System.Drawing.Point 0, -2), (New-Object System.Drawing.Point 0, ($h + 2)),
    [System.Drawing.Color]::FromArgb(255, 13, 14, 18), [System.Drawing.Color]::FromArgb(255, 30, 27, 75))
  $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
  $g.FillRectangle($bg, -1, -1, $w + 2, $h + 2)
  # A soft teal glow behind where the icon sits.
  $glow = New-Object System.Drawing.Drawing2D.GraphicsPath
  $gr = [int]($w * 0.75)
  $glow.AddEllipse([int](($w - $gr) / 2), [int]($h * 0.42 - $gr / 2), $gr, $gr)
  $pgb = New-Object System.Drawing.Drawing2D.PathGradientBrush($glow)
  $pgb.CenterColor = [System.Drawing.Color]::FromArgb(70, 20, 184, 166)
  $pgb.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 20, 184, 166))
  $g.FillPath($pgb, $glow)
  return @($bmp, $g)
}

function Title($g, [int]$w, [single]$y, [single]$px) {
  $font = New-Object System.Drawing.Font("Segoe UI Semibold", $px, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
  $fmt = New-Object System.Drawing.StringFormat
  $fmt.Alignment = [System.Drawing.StringAlignment]::Center
  $brush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 245, 246, 250))
  $g.DrawString("Silolux", $font, $brush, (New-Object System.Drawing.RectangleF 0, $y, $w, ($px * 1.6)), $fmt)
}

function Save($bmp, $g, [string]$name) {
  $g.Dispose()
  $bmp.Save((Join-Path $Out $name), [System.Drawing.Imaging.ImageFormat]::Png)
  $bmp.Dispose()
  Write-Host "  $name"
}

try {
  # 9:16 poster: icon in the upper half, name below.
  $bmp, $g = Canvas 1440 2160
  $icon = Icon 820
  $g.DrawImage($icon, [int]((1440 - 820) / 2), 540, 820, 820)
  Title $g 1440 1440 210
  $icon.Dispose()
  Save $bmp $g "poster-1440x2160.png"

  # 1:1 box art.
  $bmp, $g = Canvas 2160 2160
  $icon = Icon 1080
  $g.DrawImage($icon, [int]((2160 - 1080) / 2), 420, 1080, 1080)
  Title $g 2160 1560 260
  $icon.Dispose()
  Save $bmp $g "boxart-2160x2160.png"

  # Store display images: the icon itself, drawn at each size.
  foreach ($s in 300, 150, 71) {
    $icon = Icon $s
    $icon.Save((Join-Path $Out "tile-$s.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $icon.Dispose()
    Write-Host "  tile-$s.png"
  }
} finally {
  Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}
Write-Host "Store art written to $Out"
