$ErrorActionPreference='Continue'
. 'C:\Users\admin\Documents\ChatGPT\天工CAD型材内嵌玻璃板插件\DEV_0.4_插件框架\tools\desk-lib.ps1'
$d = Get-Desk -Name 'TGWork'
$before = @(Get-Process -Name TianGong -ErrorAction SilentlyContinue).Count
Write-Output ('CAD 进程数 before=' + $before)
$cmd = '"C:\Program Files\PowerShell\7\pwsh.exe" -NoProfile -ExecutionPolicy Bypass -File "C:\Users\admin\Documents\ChatGPT\天工CAD型材内嵌玻璃板插件\DEV_0.4_插件框架\tools\desk-attach-test.ps1"'
$procId = Start-OnDesk -DeskName 'TGWork' -CommandLine $cmd -WorkDir 'C:\Users\admin\Documents\ChatGPT\天工CAD型材内嵌玻璃板插件\DEV_0.4_插件框架\artifacts\deskwork'
Write-Output ('driver pid ' + $procId)
$deadline = (Get-Date).AddSeconds(90)
while((Get-Date) -lt $deadline){
  Start-Sleep -Seconds 3
  $log = 'C:\Users\admin\Documents\ChatGPT\天工CAD型材内嵌玻璃板插件\DEV_0.4_插件框架\artifacts\deskwork\attach-test.log.txt'
  if((Test-Path $log) -and ((Get-Content $log -Raw) -match 'DONE')){ break }
}
$after = @(Get-Process -Name TianGong -ErrorAction SilentlyContinue).Count
Write-Output ('CAD 进程数 after=' + $after)
Get-Content 'C:\Users\admin\Documents\ChatGPT\天工CAD型材内嵌玻璃板插件\DEV_0.4_插件框架\artifacts\deskwork\attach-test.log.txt' -Raw -Encoding UTF8
