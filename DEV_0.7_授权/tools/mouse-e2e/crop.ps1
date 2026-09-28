# tools/mouse-e2e/crop.ps1 —— 裁切放大截图，用来精确量坐标（不要靠肉眼估）。
param([Parameter(Mandatory=$true)][string]$Src,[Parameter(Mandatory=$true)][string]$Dst,[int]$X,[int]$Y,[int]$W,[int]$H,[double]$Zoom = 3)
Add-Type -AssemblyName System.Drawing
$img = [System.Drawing.Image]::FromFile($Src)
$rw = [Math]::Min($W, $img.Width - $X); $rh = [Math]::Min($H, $img.Height - $Y)
$ow = [int]($rw * $Zoom); $oh = [int]($rh * $Zoom)
$bmp = New-Object System.Drawing.Bitmap($ow, $oh)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g.DrawImage($img, (New-Object System.Drawing.Rectangle(0,0,$ow,$oh)), (New-Object System.Drawing.Rectangle($X,$Y,$rw,$rh)), [System.Drawing.GraphicsUnit]::Pixel)
$g.Dispose(); $bmp.Save($Dst, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose(); $img.Dispose()
Write-Output ("CROP " + $Dst + "  crop=(" + $X + "," + $Y + " " + $rw + "x" + $rh + ") zoom=" + $Zoom)
