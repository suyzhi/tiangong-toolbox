# tools/run-hole-feature-probe.ps1 —— 在私有桌面上跑"孔特征事实探针"（多孔合一 / 改名 / 螺纹深度 / 改孔心 / 属性集 / 跨零件）。
# 用法：tools\run-hole-feature-probe.ps1 [-Exe build\HoleFeatureProbe.exe] [-OutDir ...]
# 和其它探针一样：本机已有 CAD 在跑就不跑（避免从 ROT 误连到用户的实例）；
# 收尾只结束**自己启动的那个** CAD 进程，绝不动别的。
param([string]$Exe, [string]$OutDir, [string[]]$ExeArgs)
$ErrorActionPreference='Continue'
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'desk-lib.ps1')
$out = if($OutDir){$OutDir}else{Join-Path $root 'artifacts\hole-feature-probe'}
New-Item -ItemType Directory -Force -Path $out | Out-Null
$exePath = if($Exe){ (Resolve-Path $Exe).Path } else { Join-Path $root 'build\HoleFeatureProbe.exe' }
if(-not (Test-Path $exePath)){ Write-Output ('找不到 ' + $exePath); exit 1 }

$existing = @(Get-Process -Name TianGong -ErrorAction SilentlyContinue)
Write-Output ('启动前本机 CAD 进程数（用户桌面，保持不动）：' + $existing.Count)
if($existing.Count -gt 0){ Write-Output '已有 CAD 在跑 -> 为避免误连用户的实例，本次不跑'; exit 2 }

$d = Get-Desk -Name 'TGWork'
$cmd = '"C:\Program Files\NDS\TianGong 2025\Program\TianGong.exe"'
$cadPid = Start-OnDesk -DeskName 'TGWork' -CommandLine $cmd -WorkDir 'C:\Program Files\NDS\TianGong 2025\Program'
Write-Output ('私有桌面 CAD pid=' + $cadPid)
$w = Wait-TgWindow -Desk $d.Handle -ClassLike 'EngineFrame*' -TimeoutSec 300
if($w -eq $null){ Write-Output '私有桌面 CAD 未就绪'; exit 1 }
Write-Output ('CAD 窗口就绪 hwnd=' + $w.H)
Start-Sleep -Seconds 25

$name = [IO.Path]::GetFileNameWithoutExtension($exePath)
$log = Join-Path $out ($name + '.txt')
if(Test-Path $log){ Remove-Item $log -Force }
$extra = if($ExeArgs){ ' ' + ($ExeArgs -join ' ') } else { '' }
$run = 'cmd.exe /c ""' + $exePath + '" "' + $out + '"' + $extra + ' > "' + $log + '" 2>&1"'
$p = Start-OnDesk -DeskName 'TGWork' -CommandLine $run -WorkDir $out
$deadline = (Get-Date).AddSeconds(1200)
while((Get-Date) -lt $deadline){
  Start-Sleep -Seconds 5
  if(-not (Get-Process -Id $p -ErrorAction SilentlyContinue)){ break }
}
Start-Sleep -Seconds 3
if(Test-Path $log){ Get-Content $log -Encoding UTF8 }
Write-Output '--- 清理：只结束本脚本启动的 CAD ---'
Get-Process -Id $cadPid -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill() } catch {} }
Start-Sleep -Seconds 3
Write-Output ('剩余 CAD 进程数：' + @(Get-Process -Name TianGong -ErrorAction SilentlyContinue).Count)
