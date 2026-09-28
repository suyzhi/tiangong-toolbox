# tools/mouse-e2e/capall.ps1 —— 把 CAD 进程当前所有可见窗口都截一张，方便回读"有没有新弹窗"。
param([string]$Tag = "cap")
$ErrorActionPreference = 'Continue'
. (Join-Path $PSScriptRoot 'lib.ps1')
$d = Get-Desk -Name $script:DeskName
$main = Get-CadMain
if ($main -eq $null) { Write-Output "NO CAD WINDOW"; exit 1 }
$cadPid = $main.Pid
$i = 0
foreach ($w in (Get-DeskWindows -Desk $d.Handle -VisibleOnly)) {
    if ($w.Pid -ne $cadPid) { continue }
    if ($w.Wd -lt 80 -or $w.Ht -lt 40) { continue }
    $i++
    $name = $Tag + "-w" + $i
    Save-Shot -Name $name -Hwnd $w.H | Out-Null
    Write-Output ("  [" + $i + "] hwnd=" + $w.H + " rect=" + $w.X + "," + $w.Y + " " + $w.Wd + "x" + $w.Ht + " cls=" + $w.C + " title=" + $w.T)
}
