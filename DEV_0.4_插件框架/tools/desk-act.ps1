param(
  [Parameter(Mandatory=$true)][string]$Tag,
  [Parameter(Mandatory=$true)][string]$Actions,     # JSON 数组，交给私有桌面的 helper 执行
  [int]$WaitMs = 1500,
  [switch]$NoShot
)
$ErrorActionPreference = 'Continue'
. 'C:\Users\admin\Documents\ChatGPT\天工CAD型材内嵌玻璃板插件\DEV_0.4_插件框架\tools\desk-lib.ps1'
$work = 'C:\Users\admin\Documents\ChatGPT\天工CAD型材内嵌玻璃板插件\DEV_0.4_插件框架\artifacts\deskwork'
$shotDir = Join-Path $work 'shots'
New-Item -ItemType Directory -Force -Path $shotDir | Out-Null
$d = Get-Desk -Name 'TGWork'
$main = Get-TgWindow -Desk $d.Handle -ClassLike 'EngineFrame*'
if ($main -eq $null) { Write-Output 'NO CAD WINDOW'; exit 1 }
Write-Output ('MAIN hwnd=' + $main.H + ' rect=' + $main.X + ',' + $main.Y + ' ' + $main.Wd + 'x' + $main.Ht)

$ops = @()
$ops += @{ t='fg'; hwnd=[int64]$main.H }
foreach ($a in ($Actions | ConvertFrom-Json)) { $ops += $a }
$ops += @{ t='sleep'; ms=$WaitMs }

$log = Invoke-DeskJob -DeskName 'TGWork' -Tag $Tag -Ops $ops -TimeoutSec 120
$log | ForEach-Object { Write-Output ('  ' + $_) }

if (-not $NoShot) {
  $main = Get-TgWindow -Desk $d.Handle -ClassLike 'EngineFrame*'
  $path = Join-Path $shotDir ($Tag + '.png')
  $ok = Save-TgShot -Hwnd $main.H -Path $path -Flags 2
  Write-Output ('SHOT ' + $ok + ' -> ' + $path)
  Write-Output '--- 私有桌面可见窗口 ---'
  foreach ($w in (Get-DeskWindows -Desk $d.Handle -VisibleOnly)) {
    Write-Output ('  HWND={0,-12} PID={1,-8} Rect={2},{3} {4}x{5} cls={6} title={7}' -f $w.H,$w.Pid,$w.X,$w.Y,$w.Wd,$w.Ht,$w.C,$w.T)
  }
}
