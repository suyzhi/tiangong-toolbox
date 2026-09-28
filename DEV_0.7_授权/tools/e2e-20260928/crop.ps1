param([Parameter(Mandatory=$true)][string]$Src,[int]$X,[int]$Y,[int]$W,[int]$H,[int]$Zoom=3,[Parameter(Mandatory=$true)][string]$Out)
Add-Type -AssemblyName System.Drawing
$img=[System.Drawing.Image]::FromFile($Src)
$rect=New-Object System.Drawing.Rectangle($X,$Y,$W,$H)
$bmp=New-Object System.Drawing.Bitmap(($W*$Zoom),($H*$Zoom))
$g=[System.Drawing.Graphics]::FromImage($bmp)
$g.InterpolationMode=[System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
$g.PixelOffsetMode=[System.Drawing.Drawing2D.PixelOffsetMode]::Half
$dst=New-Object System.Drawing.Rectangle(0,0,($W*$Zoom),($H*$Zoom))
$g.DrawImage($img,$dst,$rect,[System.Drawing.GraphicsUnit]::Pixel)
$g.Dispose();$bmp.Save($Out,[System.Drawing.Imaging.ImageFormat]::Png);$bmp.Dispose();$img.Dispose()
Write-Output ('cropped ' + $X + ',' + $Y + ' ' + $W + 'x' + $H + ' x' + $Zoom + ' -> ' + $Out)
