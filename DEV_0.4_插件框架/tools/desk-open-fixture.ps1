$ErrorActionPreference='Continue'
. 'C:\Users\admin\Documents\ChatGPT\天工CAD型材内嵌玻璃板插件\DEV_0.4_插件框架\tools\desk-lib.ps1'
$work = 'C:\Users\admin\Documents\ChatGPT\天工CAD型材内嵌玻璃板插件\DEV_0.4_插件框架\artifacts\deskwork'
$shotDir = Join-Path $work 'shots2'
New-Item -ItemType Directory -Force -Path $shotDir | Out-Null

# 先关掉旧实例（停在欢迎页的那个）
foreach($p in @(Get-Process -Name TianGong -ErrorAction SilentlyContinue)){ try { $p.Kill() } catch {} }
Start-Sleep -Seconds 4

$d = Get-Desk -Name 'TGWork'
$fixture = 'C:\Users\admin\Documents\ChatGPT\天工CAD型材内嵌玻璃板插件\DEV_0.4_插件框架\artifacts\v6-20260925\autohole-tapped-20260925-105921\TappedFixture.asm'
$exe = 'C:\Program Files\NDS\TianGong 2025\Program\TianGong.exe'
$procId = Start-OnDesk -DeskName 'TGWork' -CommandLine ('"' + $exe + '" "' + $fixture + '"') -WorkDir (Split-Path -Parent $fixture)
Write-Output ('CAD PID ' + $procId)

$main = Wait-TgWindow -Desk $d.Handle -ClassLike 'EngineFrame*' -TimeoutSec 240
if($main -eq $null){ Write-Output 'MAIN WINDOW NOT FOUND'; exit 1 }
Write-Output ('MAIN hwnd=' + $main.H + ' title=' + $main.T)

# 等文档真正打开：标题里应出现夹具名
$deadline = (Get-Date).AddSeconds(180)
while((Get-Date) -lt $deadline){
  Start-Sleep -Seconds 5
  $w = Get-TgWindow -Desk $d.Handle -ClassLike 'EngineFrame*'
  if($w -ne $null -and $w.T -notmatch '天工 CAD 2025 标准版$'){ Write-Output ('TITLE ' + $w.T); break }
  Write-Output ('waiting... title=' + ($w.T))
}
[void][TgDeskNative]::MoveWindow($main.H, 40, 30, 1520, 920, $true)
Start-Sleep -Seconds 6
$main = Get-TgWindow -Desk $d.Handle -ClassLike 'EngineFrame*'
Write-Output '--- 私有桌面所有可见窗口 ---'
foreach($w in (Get-DeskWindows -Desk $d.Handle -VisibleOnly)){ Write-Output ('HWND={0,-12} PID={1,-8} Rect={2},{3} {4}x{5} cls={6} title={7}' -f $w.H,$w.Pid,$w.X,$w.Y,$w.Wd,$w.Ht,$w.C,$w.T) }
Write-Output ('shot = ' + (Save-TgShot -Hwnd $main.H -Path (Join-Path $shotDir 'asm-opened.png') -Flags 2))
Get-ChildItem $shotDir | Select-Object Name,Length | Format-Table -AutoSize | Out-String -Width 100
