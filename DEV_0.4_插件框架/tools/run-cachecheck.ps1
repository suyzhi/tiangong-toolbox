$ErrorActionPreference='Continue'
. 'C:\Users\admin\Documents\ChatGPT\天工CAD型材内嵌玻璃板插件\DEV_0.4_插件框架\tools\desk-lib.ps1'
$root = 'C:\Users\admin\Documents\ChatGPT\天工CAD型材内嵌玻璃板插件\DEV_0.4_插件框架'
$out = Join-Path $root 'artifacts\cachecheck'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$log = Join-Path $out 'cachecheck.log.txt'
if(Test-Path $log){ Remove-Item $log -Force }

# 私有桌面上启动一个独立 CAD（用户的 CAD 不受影响）
$d = Get-Desk -Name 'TGWork'
$existing = Get-Process -Name TianGong -ErrorAction SilentlyContinue
Write-Output ('用户桌面的 CAD 进程数（保持不动）：' + @($existing).Count)
$cmd = '"C:\Program Files\NDS\TianGong 2025\Program\TianGong.exe"'
$cadPid = Start-OnDesk -DeskName 'TGWork' -CommandLine $cmd -WorkDir 'C:\Program Files\NDS\TianGong 2025\Program'
Write-Output ('私有桌面 CAD pid=' + $cadPid)
$w = Wait-TgWindow -Desk $d.Handle -ClassLike 'EngineFrame*' -TimeoutSec 240
if($w -eq $null){ Write-Output '私有桌面 CAD 未就绪'; exit 1 }
Write-Output ('私有桌面 CAD 窗口 ' + $w.H + ' ' + $w.T)
Start-Sleep -Seconds 10

# 在同一个私有桌面上跑含"缓存失效"断言的回归测试
$run = 'cmd.exe /c ""' + (Join-Path $root 'build\fix2\PanelTests.exe') + '" --autohole-tapped "' + $out + '" > "' + $log + '" 2>&1"'
$p = Start-OnDesk -DeskName 'TGWork' -CommandLine $run -WorkDir $out
Write-Output ('测试进程 pid=' + $p)
$deadline = (Get-Date).AddSeconds(300)
while((Get-Date) -lt $deadline){
  Start-Sleep -Seconds 5
  if((Test-Path $log) -and ((Get-Content $log -Raw) -match 'TAPPED ASSERTIONS|FATAL')){ break }
}
Write-Output '--- 测试输出（只看关键行）---'
if(Test-Path $log){
  Get-Content $log -Encoding UTF8 | Select-String -Pattern 'PASS: |FAIL|FATAL|ASSERTIONS|缓存|重新加载' | ForEach-Object { $_.Line }
}
Write-Output '--- 清理私有桌面的 CAD ---'
Get-Process -Name TianGong -ErrorAction SilentlyContinue | Where-Object { $_.Id -eq $cadPid } | ForEach-Object { try { $_.Kill() } catch {} }
Write-Output ('剩余 CAD：' + @(Get-Process -Name TianGong -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne 42896 }).Count + ' (用户那个 42896 不动)')
