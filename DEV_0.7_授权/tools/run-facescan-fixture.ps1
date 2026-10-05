# tools/run-facescan-fixture.ps1 —— 在私有桌面上造"面扫描"鼠标实测用的夹具装配。
param([string]$OutDir)
$ErrorActionPreference='Continue'
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'desk-lib.ps1')
$out = if($OutDir){$OutDir}else{Join-Path $root 'artifacts\facescan-fixture'}
New-Item -ItemType Directory -Force -Path $out | Out-Null
$exe = Join-Path $root 'build\MakeScanFixture.exe'
if(-not (Test-Path $exe)){ Write-Output '先编译 build\MakeScanFixture.exe（见 tools\facescan-probe\README.md）'; exit 1 }

Get-Process -Name TianGong -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill() } catch {} }
Start-Sleep -Seconds 4
$d = Get-Desk -Name 'TGWork'
$cadPid = Start-OnDesk -DeskName 'TGWork' -CommandLine '"C:\Program Files\NDS\TianGong 2025\Program\TianGong.exe"' -WorkDir 'C:\Program Files\NDS\TianGong 2025\Program'
Write-Output ('私有桌面 CAD pid=' + $cadPid)
$w = Wait-TgWindow -Desk $d.Handle -ClassLike 'EngineFrame*' -TimeoutSec 300
if($w -eq $null){ Write-Output '私有桌面 CAD 未就绪'; exit 1 }
Start-Sleep -Seconds 25

$log = Join-Path $out 'make-fixture.txt'
if(Test-Path $log){ Remove-Item $log -Force }
$run = 'cmd.exe /c ""' + $exe + '" "' + $out + '" > "' + $log + '" 2>&1"'
$p = Start-OnDesk -DeskName 'TGWork' -CommandLine $run -WorkDir $out
$deadline = (Get-Date).AddSeconds(600)
while((Get-Date) -lt $deadline){
  Start-Sleep -Seconds 5
  if(-not (Get-Process -Id $p -ErrorAction SilentlyContinue)){ break }
}
Start-Sleep -Seconds 3
if(Test-Path $log){ Get-Content $log -Encoding UTF8 }
Get-Process -Name TianGong -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill() } catch {} }
Start-Sleep -Seconds 4
Write-Output ('剩余 CAD 进程数：' + @(Get-Process -Name TianGong -ErrorAction SilentlyContinue).Count)
Get-ChildItem $out | ForEach-Object { Write-Output ('  ' + $_.Name + '  ' + $_.Length) }
