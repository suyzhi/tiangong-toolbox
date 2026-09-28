# tools/fix-e2e/sweep4.ps1 —— 按实测坐标表逐个点开插件命令，截图存档。
$ErrorActionPreference='Continue'
$Root='C:\Users\admin\Documents\ChatGPT\天工CAD型材内嵌玻璃板插件\DEV_0.7_授权'
. (Join-Path $Root 'tools\mouse-e2e\lib.ps1')
$Desk='TGWork070'
$d=Get-Desk -Name $Desk
$main=Get-TgWindow -Desk $d.Handle -ClassLike 'EngineFrame*'
if($main -eq $null){ Write-Output 'NO CAD'; exit 1 }
function Strays { @(Get-DeskWindows -Desk $d.Handle -VisibleOnly | Where-Object { $_.Pid -eq $main.Pid -and $_.H -ne $main.H -and $_.Wd -gt 120 -and $_.Ht -gt 60 }) }
function CloseAll { foreach($w in (Strays)){ Close-StrayWindow -Hwnd $w.H | Out-Null }; Start-Sleep -Milliseconds 600 }
CloseAll
Invoke-DeskJob -DeskName $Desk -Tag 'tab' -Ops @(@{ t='clickat'; x=($main.X+770); y=($main.Y+45) }, @{ t='sleep'; ms=2200 }) | Out-Null

$pts = @(
  @{ n='a'; rx=20;  ry=66 },
  @{ n='b'; rx=20;  ry=78 },
  @{ n='c'; rx=20;  ry=90 },
  @{ n='d'; rx=140; ry=66 },
  @{ n='e'; rx=140; ry=78 },
  @{ n='f'; rx=140; ry=90 },
  @{ n='g'; rx=220; ry=66 },
  @{ n='h'; rx=220; ry=78 }
)
foreach($p in $pts){
  CloseAll
  Invoke-DeskJob -DeskName $Desk -Tag 'c' -Ops @(@{ t='clickat'; x=($main.X+$p.rx+20); y=($main.Y+$p.ry+20) }, @{ t='sleep'; ms=2600 }) | Out-Null
  $s=Strays
  if($s.Count -eq 0){ Write-Output ($p.n + " [" + $p.rx + "," + $p.ry + "] -> （无窗口）"); continue }
  foreach($w in $s){
    Write-Output ($p.n + " [" + $p.rx + "," + $p.ry + "] -> [" + $w.T + "] " + $w.Wd + "x" + $w.Ht + " hwnd=" + $w.H)
    Save-TgShot -Hwnd $w.H -Path (Join-Path $Root ('artifacts\fix-e2e\s4-' + $p.n + '.png')) | Out-Null
  }
}
CloseAll
Write-Output 'done'