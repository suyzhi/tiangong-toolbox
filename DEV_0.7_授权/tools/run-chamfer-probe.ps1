# tools/run-chamfer-probe.ps1 —— 在私有桌面上跑"孔口倒角几何"探针，不打扰用户桌面。
$ErrorActionPreference='Continue'
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'desk-lib.ps1')
$out = Join-Path $root 'artifacts\chamfer-probe'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$log = Join-Path $out 'chamfer-probe.log.txt'
if(Test-Path $log){ Remove-Item $log -Force }

# 先用 build.ps1 编好的插件 DLL，再单独编探针 exe
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$bin = Join-Path $root 'build\chamfer-probe'
New-Item -ItemType Directory -Force -Path $bin | Out-Null
Copy-Item (Join-Path $root 'build\Interop.TG.dll') $bin -Force
Copy-Item (Join-Path $root 'build\TianGongCadSuite.dll') $bin -Force
& $csc /nologo /target:exe /platform:x64 /out:"$bin\ChamferProbe.exe" /reference:"$bin\Interop.TG.dll" /reference:"$bin\TianGongCadSuite.dll" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll (Join-Path $PSScriptRoot 'ChamferProbe.cs')
if($LASTEXITCODE -ne 0){ Write-Output '探针编译失败'; exit 1 }
Write-Output '探针编译完成'

$existing = @(Get-Process -Name TianGong -ErrorAction SilentlyContinue)
Write-Output ('启动前本机 CAD 进程数（用户桌面，保持不动）：' + $existing.Count)
if($existing.Count -gt 0){ Write-Output '已有 CAD 在跑 -> 为免误连用户的实例，本次不跑探针'; exit 2 }

$d = Get-Desk -Name 'TGWork'
$cmd = '"C:\Program Files\NDS\TianGong 2025\Program\TianGong.exe"'
$cadPid = Start-OnDesk -DeskName 'TGWork' -CommandLine $cmd -WorkDir 'C:\Program Files\NDS\TianGong 2025\Program'
Write-Output ('私有桌面 CAD pid=' + $cadPid)
$w = Wait-TgWindow -Desk $d.Handle -ClassLike 'EngineFrame*' -TimeoutSec 300
if($w -eq $null){ Write-Output '私有桌面 CAD 未就绪'; exit 1 }
Write-Output ('CAD 窗口就绪 hwnd=' + $w.H)
Start-Sleep -Seconds 20

$run = 'cmd.exe /c ""' + (Join-Path $bin 'ChamferProbe.exe') + '" "' + $out + '" > "' + $log + '" 2>&1"'
$p = Start-OnDesk -DeskName 'TGWork' -CommandLine $run -WorkDir $out
Write-Output ('探针进程 pid=' + $p)
$deadline = (Get-Date).AddSeconds(1200)
while((Get-Date) -lt $deadline){
  Start-Sleep -Seconds 5
  $done = (Test-Path $log) -and ((Get-Content $log -Raw -ErrorAction SilentlyContinue) -match 'PROBE DONE|PROBE FATAL')
  $dead = -not (Get-Process -Id $p -ErrorAction SilentlyContinue)
  if($done -or $dead){ break }
}
Write-Output '--- 探针输出 ---'
if(Test-Path $log){ Get-Content $log -Encoding UTF8 }
Write-Output '--- 清理私有桌面的 CAD ---'
Get-Process -Name TianGong -ErrorAction SilentlyContinue | Where-Object { $_.Id -eq $cadPid } | ForEach-Object { try { $_.Kill() } catch {} }
Write-Output ('剩余 CAD 进程数：' + @(Get-Process -Name TianGong -ErrorAction SilentlyContinue).Count)
