# tools/run-autohole-native.ps1 —— 在私有桌面上跑完自动打孔的全部原生回归，不打扰用户桌面。
$ErrorActionPreference='Continue'
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'desk-lib.ps1')
$out = Join-Path $root 'artifacts\native-regression'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$exe = Join-Path $root 'build\PanelTests.exe'
if(-not (Test-Path $exe)){ Write-Output '先跑 tools\build.ps1'; exit 1 }

$existing = @(Get-Process -Name TianGong -ErrorAction SilentlyContinue)
Write-Output ('启动前本机 CAD 进程数（用户桌面，保持不动）：' + $existing.Count)
if($existing.Count -gt 0){ Write-Output '已有 CAD 在跑 -> 为避免误连用户的实例，本次不跑原生回归'; exit 2 }

$d = Get-Desk -Name 'TGWork'
$cmd = '"C:\Program Files\NDS\TianGong 2025\Program\TianGong.exe"'
$cadPid = Start-OnDesk -DeskName 'TGWork' -CommandLine $cmd -WorkDir 'C:\Program Files\NDS\TianGong 2025\Program'
Write-Output ('私有桌面 CAD pid=' + $cadPid)
$w = Wait-TgWindow -Desk $d.Handle -ClassLike 'EngineFrame*' -TimeoutSec 300
if($w -eq $null){ Write-Output '私有桌面 CAD 未就绪'; exit 1 }
Write-Output ('CAD 窗口就绪 hwnd=' + $w.H)
Start-Sleep -Seconds 20

$modes = @('--autohole-fixes','--autohole-tapped','--autohole-pattern','--autohole','--autohole-form','--autohole-multi')
foreach($m in $modes){
  $log = Join-Path $out ($m.TrimStart('-') + '.txt')
  if(Test-Path $log){ Remove-Item $log -Force }
  $run = 'cmd.exe /c ""' + $exe + '" ' + $m + ' "' + $out + '" > "' + $log + '" 2>&1"'
  $p = Start-OnDesk -DeskName 'TGWork' -CommandLine $run -WorkDir $out
  # 必须等进程真的退出，不能看到日志里出现 "ASSERTIONS" 就往下走 ——
  # 每次 PanelTests 起动时都会先无条件跑一遍纯逻辑断言（也带 "ASSERTIONS" 字样），
  # 按关键字提前跳出会让上一个测试还占着 CAD，下一个就顶上去，结果全是假失败。
  $deadline = (Get-Date).AddSeconds(900)
  while((Get-Date) -lt $deadline){
    Start-Sleep -Seconds 5
    if(-not (Get-Process -Id $p -ErrorAction SilentlyContinue)){ break }
  }
  Start-Sleep -Seconds 3
  $res = if(Test-Path $log){ (Get-Content $log -Encoding UTF8 | Select-String -Pattern 'ASSERTIONS|FAIL:|FATAL|Exception' | ForEach-Object { $_.Line }) -join ' | ' } else { '（无输出）' }
  Write-Output ($m + '  ->  ' + $res)
  Start-Sleep -Seconds 3
}

Write-Output '--- 清理私有桌面的 CAD ---'
Get-Process -Name TianGong -ErrorAction SilentlyContinue | Where-Object { $_.Id -eq $cadPid } | ForEach-Object { try { $_.Kill() } catch {} }
Start-Sleep -Seconds 5
Get-Process -Name TianGong -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill() } catch {} }
Write-Output ('剩余 CAD 进程数：' + @(Get-Process -Name TianGong -ErrorAction SilentlyContinue).Count)
