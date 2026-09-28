param([string]$OnlyTitle = '')
$ErrorActionPreference = 'Continue'
. (Join-Path (Split-Path $PSScriptRoot -Parent) 'mouse-e2e\lib.ps1')
$d = Get-Desk -Name $script:DeskName
$main = Get-CadMain -TimeoutSec 20 -Desk $d.Handle
if ($main -eq $null) { Write-Output 'NO CAD'; exit 1 }
$strays = @(Get-DeskWindows -Desk $d.Handle -VisibleOnly | Where-Object { $_.Pid -eq $main.Pid -and $_.H -ne $main.H -and $_.Wd -gt 120 -and $_.Ht -gt 60 })
foreach ($w in $strays) {
  if ($OnlyTitle -and $w.T -notlike ('*' + $OnlyTitle + '*')) { continue }
  Close-StrayWindow -Hwnd $w.H | Out-Null
  Write-Output ('closed: ' + $w.T)
}
Start-Sleep -Milliseconds 800
Write-Output '--- windows ---'
Show-Windows
