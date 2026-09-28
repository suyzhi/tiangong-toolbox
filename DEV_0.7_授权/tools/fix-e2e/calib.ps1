# tools/fix-e2e/calib.ps1 —— 用"点一下就知道打开了哪个窗口"的办法标定功能区命令坐标。
$ErrorActionPreference='Continue'
$Root='C:\Users\admin\Documents\ChatGPT\天工CAD型材内嵌玻璃板插件\DEV_0.7_授权'
. (Join-Path $Root 'tools\mouse-e2e\lib.ps1')
$Desk='TGWork070'
$d=Get-Desk -Name $Desk
$main=Get-TgWindow -Desk $d.Handle -ClassLike 'EngineFrame*'
function Strays { @(Get-DeskWindows -Desk $d.Handle -VisibleOnly | Where-Object { $_.Pid -eq $main.Pid -and $_.H -ne $main.H -and $_.Wd -gt 120 -and $_.Ht -gt 60 }) }
function CloseAll { foreach($w in (Strays)){ Close-StrayWindow -Hwnd $w.H | Out-Null }; Start-Sleep -Milliseconds 600 }
# 保证在"插件"标签页
CloseAll
Invoke-DeskJob -DeskName $Desk -Tag 'tab' -Ops @(@{ t='clickat'; x=($main.X+770); y=($main.Y+45) }, @{ t='sleep'; ms=2000 }) | Out-Null
CloseAll
foreach($cy in @(110,124,138,152,166,180)){
  $line = "y=$cy : "
  foreach($cx in @(70,120,170,220,270,320,370,420)){
    CloseAll
    Invoke-DeskJob -DeskName $Desk -Tag 'c' -Ops @(@{ t='clickat'; x=($main.X+$cx); y=($main.Y+$cy) }, @{ t='sleep'; ms=1600 }) | Out-Null
    $s = Strays
    $name = if($s.Count -gt 0){ $s[0].T } else { '-' }
    $line += ("[{0},{1}]={2}  " -f $cx,$cy,$name)
  }
  Write-Output $line
  CloseAll
}
Write-Output 'done'