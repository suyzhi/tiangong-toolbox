# tools/fix-e2e/probemap2.ps1 —— 稀疏网格实测（第二版：y 范围按实测文字行 76/108/140 的屏幕偏移 +20 重排）。
$ErrorActionPreference='Continue'
$Root='C:\Users\admin\Documents\ChatGPT\天工CAD型材内嵌玻璃板插件\DEV_0.7_授权'
. (Join-Path $Root 'tools\mouse-e2e\lib.ps1')
Add-Type -AssemblyName System.Drawing
$Desk='TGWork070'
$d=Get-Desk -Name $Desk
$main=Get-TgWindow -Desk $d.Handle -ClassLike 'EngineFrame*'
if($main -eq $null){ Write-Output 'NO CAD'; exit 1 }
Write-Output ("origin=" + $main.X + "," + $main.Y + " size=" + $main.Wd + "x" + $main.Ht)
function Strays { @(Get-DeskWindows -Desk $d.Handle -VisibleOnly | Where-Object { $_.Pid -eq $main.Pid -and $_.H -ne $main.H -and $_.Wd -gt 120 -and $_.Ht -gt 60 }) }
function CloseAll { foreach($w in (Strays)){ Close-StrayWindow -Hwnd $w.H | Out-Null }; Start-Sleep -Milliseconds 450 }
function Sig {
  $p=Join-Path $Root 'artifacts\fix-e2e\sig.png'
  Save-TgShot -Hwnd $main.H -Path $p | Out-Null
  $img=[System.Drawing.Image]::FromFile($p)
  $tot=0
  for($y=95;$y -lt 150;$y++){ for($x=20;$x -lt 520;$x+=3){ $c=$img.GetPixel($x,$y); $v=[int](($c.R+$c.G+$c.B)/3); if($v -lt 140){ $tot++ } } }
  $img.Dispose(); return $tot
}
CloseAll
Invoke-DeskJob -DeskName $Desk -Tag 'tab' -Ops @(@{ t='clickat'; x=($main.X+770); y=($main.Y+45) }, @{ t='sleep'; ms=2200 }) | Out-Null
Write-Output ("插件标签页签名 sig=" + (Sig))
$map=@{}
foreach($ry in @(66,78,90,102,114,126,138,150)){
  $line = "screenY=$ry :"
  foreach($rx in @(20,60,100,140,180,220,260,300,340,380,420,460)){
    CloseAll
    Invoke-DeskJob -DeskName $Desk -Tag 'p' -Ops @(@{ t='clickat'; x=($main.X+$rx+20); y=($main.Y+$ry+20) }, @{ t='sleep'; ms=1300 }) | Out-Null
    $s=Strays
    if($s.Count -gt 0){ $t=$s[0].T; $line += ("[" + $rx + "]=" + $t + "  "); $map[("$rx,$ry")]=$t }
  }
  Write-Output $line
}
CloseAll
Write-Output ('sig after=' + (Sig))
Write-Output '=== MAP ==='
foreach($k in ($map.Keys | Sort-Object)){ Write-Output ("  " + $k + " -> " + $map[$k]) }
$map | ConvertTo-Json | Set-Content (Join-Path $Root 'artifacts\fix-e2e\probemap2.json') -Encoding UTF8
Write-Output 'saved'