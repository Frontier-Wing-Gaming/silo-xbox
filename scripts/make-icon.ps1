# Draws the Silolux app icon: a rounded teal-to-indigo square with a light
# ring and a play triangle. Deliberately unlike Silo's three-bar mark.
param([string]$Out, [int]$Size = 1024)
Add-Type -AssemblyName System.Drawing
$bmp = New-Object System.Drawing.Bitmap $Size, $Size
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.Clear([System.Drawing.Color]::Transparent)

$r = [int]($Size * 0.22)
$path = New-Object System.Drawing.Drawing2D.GraphicsPath
$path.AddArc(0, 0, 2 * $r, 2 * $r, 180, 90)
$path.AddArc($Size - 2 * $r, 0, 2 * $r, 2 * $r, 270, 90)
$path.AddArc($Size - 2 * $r, $Size - 2 * $r, 2 * $r, 2 * $r, 0, 90)
$path.AddArc(0, $Size - 2 * $r, 2 * $r, 2 * $r, 90, 90)
$path.CloseFigure()
$grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
  (New-Object System.Drawing.Point 0, 0), (New-Object System.Drawing.Point $Size, $Size),
  [System.Drawing.Color]::FromArgb(255, 20, 184, 166), [System.Drawing.Color]::FromArgb(255, 79, 70, 229))
$g.FillPath($grad, $path)

$white = [System.Drawing.Color]::FromArgb(245, 255, 255, 255)
$ringW = [single]($Size * 0.06)
$pen = New-Object System.Drawing.Pen $white, $ringW
$m = $Size * 0.2
$g.DrawEllipse($pen, [single]$m, [single]$m, [single]($Size - 2 * $m), [single]($Size - 2 * $m))

$c = $Size / 2.0
$t = $Size * 0.17
$pts = @(
  (New-Object System.Drawing.PointF ([single]($c - $t * 0.7)), ([single]($c - $t))),
  (New-Object System.Drawing.PointF ([single]($c - $t * 0.7)), ([single]($c + $t))),
  (New-Object System.Drawing.PointF ([single]($c + $t * 1.05)), ([single]$c))
)
$g.FillPolygon((New-Object System.Drawing.SolidBrush $white), [System.Drawing.PointF[]]$pts)
$g.Dispose()
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
