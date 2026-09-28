# tools/fix-e2e/probemap.ps1 —— 稀疏网格实测 + 插件标签页自校正。
$ErrorActionPreference='Continue'
$Root='C:\Users\admin\Documents\ChatGPT\天工CAD型材内嵌玻璃板插件\DEV_0.7_授权'
. (Join-Path $Root 'tools\mouse-e2e\lib.ps1')
Add-Type -AssemblyName System.Drawing
$Desk='TGWork070'
$d=Get-Desk -Name $Desk
$main=Get-TgWindow -Desk $d.Handle -ClassLike 'EngineFrame*'
if($main -eq $null){ Write-Output 'NO CAD'; exit 1 }
Write-Output ("origin=" + $main.X + "," + $main.Y)
function Strays { @(Get-DeskWindows -Desk $d.Handle -VisibleOnly | Where-Object { $_.Pid -eq $main.Pid -and $_.H -ne $main.H -and $_.Wd -gt 120 -and $_.Ht -gt 60 }) }
function CloseAll { foreach($w in (Strays)){ Close-StrayWindow -Hwnd $w.H | Out-Null }; Start-Sleep -Milliseconds 500 }

# 插件标签页签名：插件命令的文字是"纯文字按钮"，图标很窄；用文字行 y=110 的暗像素数当指纹
function RibbonSig {
  $p=Join-Path $Root 'artifacts\fix-e2e\sig.png'
  Save-TgShot -Hwnd $main.H -Path $p | Out-Null
  $img=[System.Drawing.Image]::FromFile($p)
  $dark=0
  for($y=100;$y -lt 125;$y++){ for($x=20;$x -lt 500;$x+=2){ $c=$img.GetPixel($x,$y); $v=[int](($c.R+$c.G+$c.B)/3); if($v -lt 140){ $dark++ } } }
  $img.Dispose()
  return $dark
}
function EnsurePluginTab {
  for($i=0;$i -lt 6;$i++){
    CloseAll
    $before=Sig
    Invoke-DeskJob -DeskName $Desk -Tag 'tab' -Ops @(@{ t='clickat'; x=($main.X+770); y=($main.Y+45) }, @{ t='sleep'; ms=2200 }) | Out-Null
    if((RibbonSig) -gt 150){ Write-Output ("插件标签页已激活 (sig=" + (RibbonSig) + ")"); return $true }
  }
  Write-Output '无法切到插件标签页'
  return $false
}
function Sig { return (RibbonSig) }
if(-not (EnsurePluginTab)){ exit 1 }

$map=@{}
foreach($ry in @(50,62,74,86,98,110,122,134)){
  $line = "ry=$ry :"
  foreach($rx in @(20,40,60,80,100,120,140,160,180,200,220,240,260,280,300)){
    CloseAll
    Invoke-DeskJob -DeskName $Desk -Tag 'p' -Ops @(@{ t='clickat'; x=($main.X+$rx); y=($main.Y+$ry) }, @{ t='sleep'; ms=1200 }) | Out-Null
    $s=Strays
    if($s.Count -gt 0){ $t=$s[0].T; $line += ("[" + $rx + "," + $ry + "]=" + $t + "  "); $map[("$rx,$ry")]=$t }
  }
  Write-Output $line
  if((RibbonSig) -lt 150){ Write-Output ("  (标签页被切走，重新校正 sig=" + (RibbonSig) + ")"); if(-not (EnsurePluginTab)){ break } }
}
CloseAll
Write-Output '=== MAP ==='
foreach($k in ($map.Keys | Sort-Object)){ Write-Output ("  " + $k + " -> " + $map[$k]) }
$map | ConvertTo-Json | Set-Content (Join-Path $Root 'artifacts\fix-e2e\probemap.json') -Encoding UTF8
Write-Output 'saved'