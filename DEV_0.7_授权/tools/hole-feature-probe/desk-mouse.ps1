# 在私有桌面 TGWork 上执行一串鼠标/键盘动作，然后截主窗口。坐标用"截图坐标"（= 屏幕坐标 + 8），这里自动减 8。
param([Parameter(Mandatory=$true)][string]$Tag, [string]$Actions = '[]', [int]$WaitMs = 1200, [string]$Com = '')
$ErrorActionPreference = 'Continue'
$sp = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'artifacts\cad-queue'; New-Item -ItemType Directory -Force -Path $sp | Out-Null
. 'C:\Users\admin\Documents\ChatGPT\天工CAD型材内嵌玻璃板插件\DEV_0.7_授权\tools\desk-lib.ps1'
$d = Get-Desk -Name 'TGWork'
$main = Get-TgWindow -Desk $d.Handle -ClassLike 'EngineFrame*'
if ($main -eq $null) { 'NO CAD WINDOW'; exit 1 }
if ($Com) {
  $app = [Runtime.InteropServices.Marshal]::GetActiveObject('SolidEdge.Application')
  if ($Com -match '^cmd:(\d+)$') { $app.StartCommand([int]$Matches[1]); "StartCommand $($Matches[1])" }
}
$ops = @(@{ t='fg'; hwnd=[int64]$main.H })
foreach ($a in ($Actions | ConvertFrom-Json)) {
  if ($a.x -ne $null) { $a.x = [int]$a.x - 8 }
  if ($a.y -ne $null) { $a.y = [int]$a.y - 8 }
  $ops += $a
}
$ops += @{ t='sleep'; ms=$WaitMs }
$log = Invoke-DeskJob -DeskName 'TGWork' -Tag $Tag -Ops $ops -TimeoutSec 120 -WorkDir (Join-Path $sp 'deskjobs')
$log | Select-Object -Last 8 | ForEach-Object { '  ' + $_ }
$main = Get-TgWindow -Desk $d.Handle -ClassLike 'EngineFrame*'
Save-TgShot -Hwnd $main.H -Path (Join-Path $sp ($Tag + '.png')) | Out-Null
foreach ($w in (Get-DeskWindows -Desk $d.Handle -VisibleOnly)) { if ($w.Wd -gt 30 -and $w.H -ne $main.H) { '  win ' + $w.H + ' ' + $w.C + ' "' + $w.T + '" ' + $w.X + ',' + $w.Y + ' ' + $w.Wd + 'x' + $w.Ht } }
