$ErrorActionPreference='Continue'
. 'C:\Users\admin\Documents\ChatGPT\天工CAD型材内嵌玻璃板插件\DEV_0.4_插件框架\tools\desk-lib.ps1'
$work = 'C:\Users\admin\Documents\ChatGPT\天工CAD型材内嵌玻璃板插件\DEV_0.4_插件框架\artifacts\deskwork'
$out = Join-Path $work 'attach2'
New-Item -ItemType Directory -Force -Path $out | Out-Null
# 先关掉默认桌面上那个 CAD（会打扰用户）
foreach($p in @(Get-Process -Name TianGong -ErrorAction SilentlyContinue)){ try { $p.Kill() } catch {} }
Start-Sleep -Seconds 4

$d = Get-Desk -Name 'TGWork'
$fixture = 'C:\Users\admin\Documents\ChatGPT\天工CAD型材内嵌玻璃板插件\DEV_0.4_插件框架\artifacts\v6-20260925\autohole-tapped-20260925-105921\TappedFixture.asm'
$exe = 'C:\Program Files\NDS\TianGong 2025\Program\TianGong.exe'
$cadPid = Start-OnDesk -DeskName 'TGWork' -CommandLine ('"' + $exe + '" "' + $fixture + '"') -WorkDir (Split-Path -Parent $fixture)
Write-Output ('CAD PID ' + $cadPid)
$main = Wait-TgWindow -Desk $d.Handle -ClassLike 'EngineFrame*' -TitleLike '*asm*' -TimeoutSec 300
if($main -eq $null){ Write-Output 'CAD WINDOW NOT FOUND'; exit 1 }
Write-Output ('CAD hwnd=' + $main.H + ' title=' + $main.T)
Start-Sleep -Seconds 5

# 在同一个私有桌面上启动 powershell 5.1 驱动（它带 Marshal.GetActiveObject）
$ps5 = "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe"
$cmd = '"{0}" -NoProfile -ExecutionPolicy Bypass -File "{1}" -OutDir "{2}"' -f $ps5, 'C:\Users\admin\Documents\ChatGPT\天工CAD型材内嵌玻璃板插件\DEV_0.4_插件框架\tools\desk-attach2.ps1', $out
$driverPid = Start-OnDesk -DeskName 'TGWork' -CommandLine $cmd -WorkDir $out
Write-Output ('driver PID ' + $driverPid)
$deadline = (Get-Date).AddSeconds(120)
while((Get-Date) -lt $deadline){
  Start-Sleep -Seconds 3
  $log = Join-Path $out 'attach2.log.txt'
  if((Test-Path $log) -and ((Get-Content $log -Raw) -match 'DONE')){ break }
}
Get-Content (Join-Path $out 'attach2.log.txt') -Raw -Encoding UTF8
