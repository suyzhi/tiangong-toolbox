# tools/mouse-e2e/act.ps1 —— 在私有桌面上对 CAD 做一次"鼠标动作 + 截图 + 窗口回读"。
param(
    [Parameter(Mandatory=$true)][string]$Tag,
    [Parameter(Mandatory=$true)][string]$Actions,
    [int]$WaitMs = 1200,
    [switch]$NoShot,
    [string]$ShotName
)
$ErrorActionPreference = 'Continue'
. (Join-Path $PSScriptRoot 'lib.ps1')

$jobLog = Invoke-Act -Tag $Tag -Actions $Actions -WaitMs $WaitMs
Write-Output "--- 私有桌面 helper 日志 ---"
foreach ($line in $jobLog) { Write-Output ('  ' + $line) }
if (-not $NoShot) {
    $name = if ($ShotName) { $ShotName } else { $Tag }
    Save-Shot -Name $name | Out-Null
}
Write-Output "--- 私有桌面窗口 ---"
Show-Windows
