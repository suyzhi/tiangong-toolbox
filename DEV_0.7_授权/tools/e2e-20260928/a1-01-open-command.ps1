# tools/e2e-20260928/a1-01-open-command.ps1 —— 2026-09-28 实测：确保"插件"标签页 -> 点一个功能区命令 -> 看弹什么窗口。
$ErrorActionPreference='Continue'
. (Join-Path (Split-Path $PSScriptRoot -Parent) 'mouse-e2e\lib.ps1')
$d = Get-Desk -Name $script:DeskName
$main = Get-CadMain -TimeoutSec 30 -Desk $d.Handle
if ($main -eq $null) { Write-Output 'NO CAD'; exit 1 }
Write-Output ('CAD hwnd=' + $main.H + ' rect=' + $main.X + ',' + $main.Y + ' ' + $main.Wd + 'x' + $main.Ht)
function Strays { @(Get-DeskWindows -Desk $d.Handle -VisibleOnly | Where-Object { $_.Pid -eq $main.Pid -and $_.H -ne $main.H -and $_.Wd -gt 120 -and $_.Ht -gt 60 }) }

# 1) 切到"插件"标签页（标定值：main + (770,45)）
Invoke-Act -Tag 'a1tab' -Actions (@(@{ t='clickat'; x=($main.X+770); y=($main.Y+45) }, @{ t='sleep'; ms=2500 }) | ConvertTo-Json -Compress) | Out-Null
Save-Shot -Name 'a1-01-plugin-tab' | Out-Null
Write-Output '--- 切页后窗口 ---'; Show-Windows

# 2) 点"配孔检查"（标定值 main+(260,96)）
Invoke-Act -Tag 'a1cmd8' -Actions (@(@{ t='clickat'; x=($main.X+260); y=($main.Y+96) }, @{ t='sleep'; ms=3000 }) | ConvertTo-Json -Compress) | Out-Null
Write-Output '--- 点命令后窗口 ---'; Show-Windows
foreach ($w in (Strays)) {
  $n = 'a1-02-' + ($w.T -replace '[^\w\u4e00-\u9fa5]+','_')
  Save-TgShot -Hwnd $w.H -Path (Join-Path $script:Out ($n + '.png')) | Out-Null
  Write-Output ('  截图 ' + $n + '.png  ' + $w.Wd + 'x' + $w.Ht)
}
Save-Shot -Name 'a1-03-after-command' | Out-Null
Write-Output 'DONE'
