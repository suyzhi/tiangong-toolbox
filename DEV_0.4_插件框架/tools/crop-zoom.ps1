Add-Type -AssemblyName System.Drawing
$dir = 'C:\Users\admin\Documents\ChatGPT\天工CAD型材内嵌玻璃板插件\DEV_0.4_插件框架\artifacts\visual'
function CropZoom($src,$dst,$x,$y,$w,$h,$zoom){
  $img = [System.Drawing.Image]::FromFile($src)
  $rw = [Math]::Min($w, $img.Width - $x); $rh = [Math]::Min($h, $img.Height - $y)
  $ow = [int]($rw * $zoom); $oh = [int]($rh * $zoom)
  $bmp = New-Object System.Drawing.Bitmap($ow, $oh)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
  $g.DrawImage($img, (New-Object System.Drawing.Rectangle(0,0,$ow,$oh)), (New-Object System.Drawing.Rectangle($x,$y,$rw,$rh)), [System.Drawing.GraphicsUnit]::Pixel)
  $g.Dispose(); $bmp.Save($dst, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose(); $img.Dispose()
  Write-Output ('CROP ' + $dst)
}
CropZoom (Join-Path $dir '05-五种孔剖面.png') (Join-Path $dir 'z-05.png') 380 300 1050 520 1.6
CropZoom (Join-Path $dir '04-剖面.png') (Join-Path $dir 'z-04.png') 380 300 1050 520 1.6
CropZoom (Join-Path $dir '02-打孔后.png') (Join-Path $dir 'z-02.png') 380 200 1050 620 1.5
Get-ChildItem $dir -Filter 'z-*.png' | Select-Object Name,Length | Format-Table -AutoSize | Out-String -Width 80