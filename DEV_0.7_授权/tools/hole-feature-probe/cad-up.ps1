# 在私有桌面 TGWork 上起一个 CAD（不收尾），再跑一次探针 exe 的某个模式（例如 U = 建板留着开）
param([string]$Mode = 'U', [string]$Tag = 'ui')
$root = 'C:\Users\admin\Documents\ChatGPT\天工CAD型材内嵌玻璃板插件\DEV_0.7_授权'
. (Join-Path $root 'tools\desk-lib.ps1')
$existing = @(Get-Process -Name TianGong -ErrorAction SilentlyContinue)
$d = Get-Desk -Name 'TGWork'
if ($existing.Count -eq 0) {
  $cadPid = Start-OnDesk -DeskName 'TGWork' -CommandLine '"C:\Program Files\NDS\TianGong 2025\Program\TianGong.exe"' -WorkDir 'C:\Program Files\NDS\TianGong 2025\Program'
  "CAD pid=$cadPid"
  $w = Wait-TgWindow -Desk $d.Handle -ClassLike 'EngineFrame*' -TimeoutSec 300
  if ($w -eq $null) { 'CAD 未就绪'; exit 1 }
  Start-Sleep -Seconds 25
} else {
  # 只复用"我在私有桌面上起的那个"：主窗口必须在 TGWork 桌面上；否则可能是用户自己的 CAD，绝不连
  $mine = Get-TgWindow -Desk $d.Handle -ClassLike 'EngineFrame*'
  if ($mine -eq $null -or $existing.Count -gt 1) { '本机有用户的 CAD 在跑 -> 不跑（避免从 ROT 连到用户的实例）'; exit 2 }
  'CAD 已在私有桌面上跑 pid=' + $existing[0].Id
}
$out = Join-Path $root ('artifacts\asm-link-probe-' + $Tag)
New-Item -ItemType Directory -Force -Path $out | Out-Null
$log = Join-Path $out 'AsmDrivenHoleProbe.txt'
if (Test-Path $log) { Remove-Item $log -Force }
$exe = Join-Path $root 'build\AsmDrivenHoleProbe.exe'
$run = 'cmd.exe /c ""' + $exe + '" "' + $out + '" ' + $Mode + ' > "' + $log + '" 2>&1"'
$p = Start-OnDesk -DeskName 'TGWork' -CommandLine $run -WorkDir $out
$deadline = (Get-Date).AddSeconds(900)
while ((Get-Date) -lt $deadline) { Start-Sleep -Seconds 2; if (-not (Get-Process -Id $p -ErrorAction SilentlyContinue)) { break } }
Get-Content $log -Encoding Default
