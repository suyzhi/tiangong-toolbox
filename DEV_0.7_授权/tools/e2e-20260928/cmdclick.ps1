param(
    [Parameter(Mandatory=$true)][int]$Dx,
    [Parameter(Mandatory=$true)][int]$Dy,
    [string]$Tag = 'cmd',
    [string]$ExpectTitle = ''
)
$ErrorActionPreference = 'Continue'
. (Join-Path (Split-Path $PSScriptRoot -Parent) 'mouse-e2e\lib.ps1')
$d = Get-Desk -Name $script:DeskName
$main = Get-CadMain -TimeoutSec 30 -Desk $d.Handle
if ($main -eq $null) { Write-Output 'NOCAD'; exit 1 }
# 关掉旧面板/残留窗口
foreach ($w in @(Get-DeskWindows -Desk $d.Handle -VisibleOnly | Where-Object { $_.Pid -eq $main.Pid -and $_.H -ne $main.H -and $_.Wd -gt 120 -and $_.Ht -gt 60 })) {
    if ($w.C -eq '#32770') { continue }
    Close-StrayWindow -Hwnd $w.H | Out-Null
    Write-Output ('CLOSED hwnd=' + $w.H)
    Start-Sleep -Milliseconds 800
}
# 插件标签页
Invoke-Act -Tag ($Tag + '-tab') -Actions (@(@{ t='clickat'; x=($main.X+770); y=($main.Y+45) }, @{ t='sleep'; ms=2500 }) | ConvertTo-Json -Compress) | Out-Null
# 点命令
Invoke-Act -Tag ($Tag + '-cmd') -Actions (@(@{ t='clickat'; x=($main.X+$Dx); y=($main.Y+$Dy) }, @{ t='sleep'; ms=3500 }) | ConvertTo-Json -Compress) | Out-Null
Start-Sleep -Seconds 1
Write-Output '--- windows ---'
Show-Windows
