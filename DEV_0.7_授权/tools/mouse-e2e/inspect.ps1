# tools/mouse-e2e/inspect.ps1 —— 截图某个顶层窗口 + 枚举其子控件（由私有桌面内的 helper 完成）。
param([string]$TitleLike = "*", [string]$Tag = "inspect", [switch]$NoEnum)
$ErrorActionPreference = 'Continue'
. (Join-Path $PSScriptRoot 'lib.ps1')
$d = Get-Desk -Name $script:DeskName
$w = $null
foreach ($c in (Get-DeskWindows -Desk $d.Handle -VisibleOnly)) {
    if ($c.T -like $TitleLike -and $c.Wd -gt 0) { $w = $c; break }
}
if ($w -eq $null) { Write-Output ("窗口未找到: " + $TitleLike); Show-Windows; exit 1 }
Write-Output ("TARGET hwnd=" + $w.H + " rect=" + $w.X + "," + $w.Y + " " + $w.Wd + "x" + $w.Ht + " title=" + $w.T)
Save-Shot -Name $Tag -Hwnd $w.H | Out-Null
if (-not $NoEnum) {
    $log = Invoke-DeskJob -DeskName $script:DeskName -Tag ($Tag + "-enum") -Ops @(@{ t = "enumchild"; hwnd = [int64]$w.H }) -TimeoutSec 90
    Write-Output "--- 子控件 ---"
    foreach ($line in $log) { Write-Output ("  " + $line) }
}
