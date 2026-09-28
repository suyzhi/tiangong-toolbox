$ErrorActionPreference='Continue'
. 'C:\Users\admin\Documents\ChatGPT\天工CAD型材内嵌玻璃板插件\DEV_0.4_插件框架\tools\desk-lib.ps1'
$out = 'C:\Users\admin\Documents\ChatGPT\天工CAD型材内嵌玻璃板插件\DEV_0.4_插件框架\artifacts\visual'
$log = Join-Path $out 'planereuse.log.txt'
if(Test-Path $log){ Remove-Item $log -Force }
$d = Get-Desk -Name 'TGWork'
$cmd = 'cmd.exe /c ""C:\Users\admin\Documents\ChatGPT\天工CAD型材内嵌玻璃板插件\DEV_0.4_插件框架\build\visual\PlaneReuseSpike.exe" "' + $out + '" > "' + $log + '" 2>&1"'
$p = Start-OnDesk -DeskName 'TGWork' -CommandLine $cmd -WorkDir $out
Write-Output ('spike pid ' + $p)
$deadline = (Get-Date).AddSeconds(150)
while((Get-Date) -lt $deadline){
  Start-Sleep -Seconds 3
  if((Test-Path $log) -and ((Get-Content $log -Raw) -match 'SPIKE DONE')){ break }
}
Get-Content $log -Raw -Encoding UTF8
