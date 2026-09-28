param(
    [Parameter(Mandatory=$true)][string]$Src,
    [int]$X = 0, [int]$Y = 55, [int]$W = 560, [int]$H = 90,
    [int]$Dark = 150, [int]$GapX = 8, [int]$GapY = 4
)
Add-Type -AssemblyName System.Drawing
$img = [System.Drawing.Image]::FromFile((Resolve-Path -LiteralPath $Src).Path)
$bmp = New-Object System.Drawing.Bitmap($img)
# 1) 投影：统计每个 x 上"暗像素"的个数（文字 = 暗）
$col = @{}
for ($y = $Y; $y -lt ($Y + $H); $y++) {
    for ($x = $X; $x -lt ($X + $W); $x++) {
        $c = $bmp.GetPixel($x, $y); $v = [int](($c.R + $c.G + $c.B) / 3)
        if ($v -lt $Dark) { if ($col.ContainsKey($x)) { $col[$x]++ } else { $col[$x] = 1 } }
    }
}
# 2) 按 x 分段（间隙 > GapX 就断）
$runs = @(); $start = -1; $lastDark = -1
for ($x = $X; $x -lt ($X + $W); $x++) {
    $has = $col.ContainsKey($x)
    if ($has) { if ($start -lt 0) { $start = $x }; $lastDark = $x }
    else { if ($start -ge 0 -and ($x - $lastDark) -gt $GapX) { $runs += ,@($start, $lastDark); $start = -1 } }
}
if ($start -ge 0) { $runs += ,@($start, $lastDark) }
Write-Output ('XRUNS ' + $runs.Count)
foreach ($r in $runs) {
    # 3) 段内按 y 分段 -> 每个文字块
    $row = @{}
    for ($x = $r[0]; $x -le $r[1]; $x++) {
        for ($y = $Y; $y -lt ($Y + $H); $y++) {
            $c = $bmp.GetPixel($x, $y); $v = [int](($c.R + $c.G + $c.B) / 3)
            if ($v -lt $Dark) { if ($row.ContainsKey($y)) { $row[$y]++ } else { $row[$y] = 1 } }
        }
    }
    $ys = @(); $s = -1; $ld = -1
    for ($y = $Y; $y -lt ($Y + $H); $y++) {
        $has = $row.ContainsKey($y)
        if ($has) { if ($s -lt 0) { $s = $y }; $ld = $y }
        else { if ($s -ge 0 -and ($y - $ld) -gt $GapY) { $ys += ,@($s, $ld); $s = -1 } }
    }
    if ($s -ge 0) { $ys += ,@($s, $ld) }
    foreach ($yy in $ys) {
        $cx = [int](($r[0] + $r[1]) / 2); $cy = [int](($yy[0] + $yy[1]) / 2)
        Write-Output ('BOX x=' + $r[0] + '..' + $r[1] + ' y=' + $yy[0] + '..' + $yy[1] + '  center=(' + $cx + ',' + $cy + ')')
    }
}
$bmp.Dispose(); $img.Dispose()
