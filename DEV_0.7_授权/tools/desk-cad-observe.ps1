$ErrorActionPreference='Continue'
. 'C:\Users\admin\Documents\ChatGPT\天工CAD型材内嵌玻璃板插件\DEV_0.4_插件框架\tools\desk-lib.ps1'
$work = 'C:\Users\admin\Documents\ChatGPT\天工CAD型材内嵌玻璃板插件\DEV_0.4_插件框架\artifacts\deskwork'
$shotDir = Join-Path $work 'shots1'
New-Item -ItemType Directory -Force -Path $shotDir | Out-Null
$d = Get-Desk -Name 'TGWork'
Write-Output '--- 私有桌面可见窗口 ---'
foreach($w in (Get-DeskWindows -Desk $d.Handle -VisibleOnly)){
  Write-Output ('HWND={0,-12} PID={1,-8} Rect={2},{3} {4}x{5} cls={6} title={7}' -f $w.H,$w.Pid,$w.X,$w.Y,$w.Wd,$w.Ht,$w.C,$w.T)
}
$main = Get-TgWindow -Desk $d.Handle -ClassLike 'EngineFrame*'
if($main -eq $null){ Write-Output 'EngineFrame NOT FOUND'; exit 1 }
[void][TgDeskNative]::MoveWindow($main.H, 60, 40, 1500, 900, $true)
Start-Sleep -Milliseconds 800
$main = Get-TgWindow -Desk $d.Handle -ClassLike 'EngineFrame*'
Write-Output ('MAIN hwnd=' + $main.H + ' rect=' + ($main.X) + ',' + ($main.Y) + ' ' + ($main.Wd) + 'x' + ($main.Ht))
$p1 = Join-Path $shotDir 'cad-main-pw2.png'
$p0 = Join-Path $shotDir 'cad-main-pw0.png'
Write-Output ('printwindow flags2 = ' + (Save-TgShot -Hwnd $main.H -Path $p1 -Flags 2))
Write-Output ('printwindow flags0 = ' + (Save-TgShot -Hwnd $main.H -Path $p0 -Flags 0))
Get-ChildItem $shotDir | Select-Object Name,Length | Format-Table -AutoSize | Out-String -Width 100
