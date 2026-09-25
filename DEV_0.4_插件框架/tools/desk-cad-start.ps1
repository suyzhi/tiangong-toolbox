$ErrorActionPreference='Stop'
. 'C:\Users\admin\.dsh\skills\windows-app-gui-automation\scripts\desk.ps1'
$FdWorkDir = 'C:\Users\admin\Documents\ChatGPT\天工CAD型材内嵌玻璃板插件\DEV_0.4_插件框架\artifacts\deskwork'
New-Item -ItemType Directory -Force -Path $FdWorkDir | Out-Null
$d = Get-Desk -Name 'TGWork'
Write-Output ('DESK handle=' + $d.Handle)
$exe = 'C:\Program Files\NDS\TianGong 2025\Program\TianGong.exe'
$procId = Start-OnDesk -DeskName 'TGWork' -CommandLine ('"' + $exe + '"') -WorkDir 'C:\Program Files\NDS\TianGong 2025\Program'
Write-Output ('CAD PID ' + $procId)
for($i=0; $i -lt 24; $i++){
  Start-Sleep -Seconds 5
  $ws = Get-DeskWindows -Desk $d.Handle -ProcId $procId -VisibleOnly
  $n = @($ws).Count
  Write-Output ("t=" + ($i*5) + "s visibleWindows=" + $n)
  if($n -gt 0){ break }
}
Write-Output '--- windows on private desktop (all pids) ---'
Show-DeskWindows -Desk $d.Handle -VisibleOnly | Select-Object -First 30
Write-Output '--- shot via helper ---'
$log = Invoke-DeskJob -DeskName 'TGWork' -Tag 'shot1' -Ops @(
  @{ t='shot'; hwnd=0; path=(Join-Path $FdWorkDir 'desk-full.png') }
) -TimeoutSec 60
$log
Get-ChildItem $FdWorkDir -Filter '*.png' | Select-Object Name,Length | Format-Table -AutoSize | Out-String -Width 120
