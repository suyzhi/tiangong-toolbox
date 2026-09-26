$ErrorActionPreference='Continue'
. 'C:\Users\admin\Documents\ChatGPT\天工CAD型材内嵌玻璃板插件\DEV_0.4_插件框架\tools\desk-lib.ps1'
$out = 'C:\Users\admin\Documents\ChatGPT\天工CAD型材内嵌玻璃板插件\DEV_0.4_插件框架\artifacts\visual'
$log = Join-Path $out 'diagnose.log.txt'
if(Test-Path $log){ Remove-Item $log -Force }
$d = Get-Desk -Name 'TGWork'
$main = Get-TgWindow -Desk $d.Handle -ClassLike 'EngineFrame*'
if($main -eq $null){ Write-Output 'CAD 窗口没找到' } else { Write-Output ('CAD hwnd=' + $main.H + ' ' + $main.T) }
$cmd = 'cmd.exe /c ""C:\Users\admin\Documents\ChatGPT\天工CAD型材内嵌玻璃板插件\DEV_0.4_插件框架\build\visual\DiagnosePlane.exe" "' + $out + '" > "' + $log + '" 2>&1"'
$p = Start-OnDesk -DeskName 'TGWork' -CommandLine $cmd -WorkDir $out
Write-Output ('diag pid ' + $p)
$deadline = (Get-Date).AddSeconds(120)
while((Get-Date) -lt $deadline){
  Start-Sleep -Seconds 3
  if((Test-Path $log) -and ((Get-Content $log -Raw) -match 'DIAG DONE')){ break }
}
Get-Content $log -Raw -Encoding UTF8
