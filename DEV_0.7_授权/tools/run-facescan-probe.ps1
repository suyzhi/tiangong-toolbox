# tools/run-facescan-probe.ps1 —— 在私有桌面上跑"面扫描/沉孔识别"事实探针，不打扰用户桌面。
param([string]$OutDir)
$ErrorActionPreference='Continue'
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'desk-lib.ps1')
$out = if($OutDir){$OutDir}else{Join-Path $root 'artifacts\facescan-probe'}
New-Item -ItemType Directory -Force -Path $out | Out-Null

$bin = Join-Path $root 'build'
$exe = Join-Path $bin 'FaceScanProbe.exe'
if(-not (Test-Path $exe)){ Write-Output '先按 tools\facescan-probe\README.md 编译探针'; exit 1 }

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

$log = Join-Path $out 'facescan-probe.txt'
if(Test-Path $log){ Remove-Item $log -Force }
$run = 'cmd.exe /c ""' + $exe + '" "' + $out + '" > "' + $log + '" 2>&1"'
$p = Start-OnDesk -DeskName 'TGWork' -CommandLine $run -WorkDir $out
$deadline = (Get-Date).AddSeconds(900)
while((Get-Date) -lt $deadline){
  Start-Sleep -Seconds 5
  if(-not (Get-Process -Id $p -ErrorAction SilentlyContinue)){ break }
}
Start-Sleep -Seconds 3
if(Test-Path $log){ Get-Content $log -Encoding UTF8 }
Write-Output '--- 清理私有桌面的 CAD ---'
Get-Process -Name TianGong -ErrorAction SilentlyContinue | Where-Object { $_.Id -eq $cadPid } | ForEach-Object { try { $_.Kill() } catch {} }
Start-Sleep -Seconds 5
Get-Process -Name TianGong -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill() } catch {} }
Write-Output ('剩余 CAD 进程数：' + @(Get-Process -Name TianGong -ErrorAction SilentlyContinue).Count)
