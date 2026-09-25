$ErrorActionPreference='Continue'
. 'C:\Users\admin\Documents\ChatGPT\天工CAD型材内嵌玻璃板插件\DEV_0.4_插件框架\tools\desk-lib.ps1'
$work = 'C:\Users\admin\Documents\ChatGPT\天工CAD型材内嵌玻璃板插件\DEV_0.4_插件框架\artifacts\deskwork'
$out  = 'C:\Users\admin\Documents\ChatGPT\天工CAD型材内嵌玻璃板插件\DEV_0.4_插件框架\artifacts\visual'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$log = Join-Path $out 'visual.log.txt'
if(Test-Path $log){ Remove-Item $log -Force }

# 1) 把插件注册到最新的 build（CAD 只会加载注册表指向的那份）
$buildRoot = 'C:\Users\admin\Documents\ChatGPT\天工CAD型材内嵌玻璃板插件\DEV_0.4_插件框架\build'
# 取最新的 holefix-* 构建（写死过一次路径，结果验收跑的是旧 DLL，白跑一轮）
$latest = Get-ChildItem $buildRoot -Directory -Filter 'holefix-*' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
Write-Output ('USE BUILD ' + $latest.FullName)
Copy-Item (Join-Path $latest.FullName 'TianGongCadSuite.dll') 'C:\Users\admin\Documents\ChatGPT\天工CAD型材内嵌玻璃板插件\DEV_0.4_插件框架\build\visual\TianGongCadSuite.dll' -Force
& 'C:\Users\admin\Documents\ChatGPT\天工CAD型材内嵌玻璃板插件\DEV_0.4_插件框架\tools\install.ps1' -LibraryPath (Join-Path $latest.FullName 'TianGongCadSuite.dll') | Out-String

# 2) 重启私有桌面上的 CAD
foreach($p in @(Get-Process -Name TianGong -ErrorAction SilentlyContinue)){ try { $p.Kill() } catch {} }
Start-Sleep -Seconds 4
$d = Get-Desk -Name 'TGWork'
$fixture = 'C:\Users\admin\Documents\ChatGPT\天工CAD型材内嵌玻璃板插件\DEV_0.4_插件框架\artifacts\v6-20260925\autohole-tapped-20260925-105921\TappedFixture.asm'
$cadPid = Start-OnDesk -DeskName 'TGWork' -CommandLine ('"C:\Program Files\NDS\TianGong 2025\Program\TianGong.exe" "' + $fixture + '"') -WorkDir (Split-Path -Parent $fixture)
Write-Output ('CAD PID ' + $cadPid)
$main = Wait-TgWindow -Desk $d.Handle -ClassLike 'EngineFrame*' -TitleLike '*asm*' -TimeoutSec 300
if($main -eq $null){ Write-Output 'CAD 窗口未出现'; exit 1 }
Write-Output ('CAD hwnd=' + $main.H + ' title=' + $main.T)
Start-Sleep -Seconds 8

# 3) 在同一个私有桌面上跑可视化验收
$exe = 'C:\Users\admin\Documents\ChatGPT\天工CAD型材内嵌玻璃板插件\DEV_0.4_插件框架\build\visual\VisualAcceptance.exe'
$cmd = 'cmd.exe /c ""' + $exe + '" "' + $out + '" > "' + $log + '" 2>&1"'
$drv = Start-OnDesk -DeskName 'TGWork' -CommandLine $cmd -WorkDir $out
Write-Output ('driver pid ' + $drv)
$deadline = (Get-Date).AddSeconds(240)
while((Get-Date) -lt $deadline){
  Start-Sleep -Seconds 4
  if((Test-Path $log) -and ((Get-Content $log -Raw) -match 'VISUAL ASSERTIONS')){ break }
}
Write-Output '--- visual log ---'
if(Test-Path $log){ Get-Content $log -Raw -Encoding UTF8 } else { Write-Output '(no log)' }
Write-Output '--- files ---'
Get-ChildItem $out -Filter '*.png' | Select-Object Name,Length | Format-Table -AutoSize | Out-String -Width 120