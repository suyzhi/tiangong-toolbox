# tools/run-facescan-e2e.ps1 —— 在私有桌面上跑"点带孔的面自动认孔 → 点打孔面 → 打孔"的界面级实测。
# 走的是插件「自动打孔」窗口的真实代码路径（和鼠标点击同一个 AcceptPick），不弹框、结果写日志。
param(
    [string]$Fixture,
    [string]$LibraryPath,
    [string]$OutDir
)
$ErrorActionPreference='Continue'
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'desk-lib.ps1')
if(-not $Fixture){ $Fixture = Join-Path $root 'artifacts\facescan-fixture\ScanFixture.asm' }
if(-not $OutDir){ $OutDir = Join-Path $root 'artifacts\facescan-e2e' }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$exe = Join-Path $root 'build\FaceScanFormE2E.exe'
if(-not (Test-Path $exe)){ Write-Output ('找不到 ' + $exe + '（先按 tools\facescan-probe\README.md 编译）'); exit 1 }
if(-not (Test-Path $Fixture)){ Write-Output ('找不到夹具 ' + $Fixture + '（先跑 tools\run-facescan-fixture.ps1）'); exit 1 }
if($LibraryPath){ & (Join-Path $root 'tools\build.ps1') -OutputDirectory (Split-Path $LibraryPath -Parent) -DevBuild | Out-Null }

Get-Process -Name TianGong -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill() } catch {} }
Start-Sleep -Seconds 4
$d = Get-Desk -Name 'TGWork'
$cadPid = Start-OnDesk -DeskName 'TGWork' -CommandLine '"C:\Program Files\NDS\TianGong 2025\Program\TianGong.exe"' -WorkDir 'C:\Program Files\NDS\TianGong 2025\Program'
Write-Output ('私有桌面 CAD pid=' + $cadPid)
$w = Wait-TgWindow -Desk $d.Handle -ClassLike 'EngineFrame*' -TimeoutSec 300
if($w -eq $null){ Write-Output '私有桌面 CAD 未就绪'; exit 1 }
Start-Sleep -Seconds 25

$log = Join-Path $OutDir 'facescan-e2e.txt'
if(Test-Path $log){ Remove-Item $log -Force }
$run = 'cmd.exe /c ""' + $exe + '" "' + $Fixture + '" "' + $OutDir + '" > "' + $log + '" 2>&1"'
$p = Start-OnDesk -DeskName 'TGWork' -CommandLine $run -WorkDir $OutDir
$deadline = (Get-Date).AddSeconds(900)
while((Get-Date) -lt $deadline){
  Start-Sleep -Seconds 5
  if(-not (Get-Process -Id $p -ErrorAction SilentlyContinue)){ break }
}
Start-Sleep -Seconds 3
if(Test-Path $log){ Get-Content $log -Encoding UTF8 }
Get-Process -Name TianGong -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill() } catch {} }
Start-Sleep -Seconds 4
Write-Output ('剩余 CAD 进程数：' + @(Get-Process -Name TianGong -ErrorAction SilentlyContinue).Count)
