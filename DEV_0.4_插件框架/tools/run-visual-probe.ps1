$ErrorActionPreference='Stop'
. 'C:\Users\admin\.dsh\skills\windows-app-gui-automation\scripts\desk.ps1'
$FdWorkDir = 'C:\Users\admin\Documents\ChatGPT\天工CAD型材内嵌玻璃板插件\DEV_0.4_插件框架\artifacts\deskwork'
New-Item -ItemType Directory -Force -Path $FdWorkDir | Out-Null
$d = Get-Desk -Name 'TGWork'
Write-Output ('DESK handle=' + $d.Handle + ' created=' + $d.Created)
$out = Join-Path $FdWorkDir 'visual-probe'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$pwshExe = (Get-Command pwsh).Source
$cmd = '"{0}" -NoProfile -ExecutionPolicy Bypass -File "{1}" -OutDir "{2}"' -f $pwshExe, 'C:\Users\admin\Documents\ChatGPT\天工CAD型材内嵌玻璃板插件\DEV_0.4_插件框架\tools\visual-probe.ps1', $out
Write-Output ('CMD ' + $cmd)
$procId = Start-OnDesk -DeskName 'TGWork' -CommandLine $cmd -WorkDir $out
Write-Output ('PID ' + $procId)
$deadline = (Get-Date).AddMinutes(6)
while((Get-Date) -lt $deadline){
  Start-Sleep -Seconds 5
  $p = Get-Process -Id $procId -ErrorAction SilentlyContinue
  if($p -eq $null){ Write-Output 'PROBE PROCESS EXITED'; break }
}
Write-Output '--- desk windows ---'
Show-DeskWindows -Desk $d.Handle -VisibleOnly | Select-Object -First 25
Write-Output '--- probe log ---'
$log = Join-Path $out 'probe.log.txt'
if(Test-Path $log){ Get-Content $log -Raw -Encoding UTF8 }
Write-Output '--- files ---'
Get-ChildItem $out | Select-Object Name,Length | Format-Table -AutoSize | Out-String -Width 120
