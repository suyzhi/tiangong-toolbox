# tools/run-form-ab.ps1 —— A/B：未改动的 0.4 基线 vs 0.6 的 --autohole-form，同一个 CAD 会话里各跑一次。
$ErrorActionPreference='Continue'
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'desk-lib.ps1')
$out = Join-Path $root 'artifacts\form-ab'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$newExe = Join-Path $root 'build\PanelTests.exe'
$oldExe = 'C:\Users\admin\Documents\ChatGPT\天工CAD型材内嵌玻璃板插件\DEV_0.4_插件框架\build\ab-original\PanelTests.exe'
foreach($e in @($newExe,$oldExe)){ if(-not (Test-Path $e)){ Write-Output ('缺 ' + $e); exit 1 } }

$existing = @(Get-Process -Name TianGong -ErrorAction SilentlyContinue)
if($existing.Count -gt 0){ Write-Output '已有 CAD 在跑，跳过'; exit 2 }
$d = Get-Desk -Name 'TGWork'
$cmd = '"C:\Program Files\NDS\TianGong 2025\Program\TianGong.exe"'
$cadPid = Start-OnDesk -DeskName 'TGWork' -CommandLine $cmd -WorkDir 'C:\Program Files\NDS\TianGong 2025\Program'
$w = Wait-TgWindow -Desk $d.Handle -ClassLike 'EngineFrame*' -TimeoutSec 300
if($w -eq $null){ Write-Output 'CAD 未就绪'; exit 1 }
Start-Sleep -Seconds 20

foreach($pair in @(@('原始0.4',$oldExe), @('新版0.6',$newExe))){
  $tag = $pair[0]; $exe = $pair[1]
  $log = Join-Path $out ($tag + '.txt')
  if(Test-Path $log){ Remove-Item $log -Force }
  $run = 'cmd.exe /c ""' + $exe + '" --autohole-form "' + $out + '" > "' + $log + '" 2>&1"'
  $p = Start-OnDesk -DeskName 'TGWork' -CommandLine $run -WorkDir $out
  $deadline = (Get-Date).AddSeconds(420)
  while((Get-Date) -lt $deadline){
    Start-Sleep -Seconds 4
    $txt = if(Test-Path $log){ Get-Content $log -Raw -ErrorAction SilentlyContinue } else { '' }
    if($txt -match 'FORM ASSERTIONS|FATAL|Exception'){ break }
    if(-not (Get-Process -Id $p -ErrorAction SilentlyContinue)){ break }
  }
  Write-Output ('===== ' + $tag + ' =====')
  if(Test-Path $log){ Get-Content $log -Encoding UTF8 | Select-String -Pattern 'PASS: |FAIL:|ASSERTIONS|Exception|attached' | ForEach-Object { $_.Line } }
  Start-Sleep -Seconds 5
}
Get-Process -Name TianGong -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill() } catch {} }
Write-Output 'done'
