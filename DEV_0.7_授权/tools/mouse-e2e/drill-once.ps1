# tools/mouse-e2e/drill-once.ps1 —— 在「自动打孔」面板上：重选 -> 点孔边 -> 点打孔面 -> 点开始打孔，全程真鼠标。
param(
    [Parameter(Mandatory=$true)][int[]]$HoleXY,
    [Parameter(Mandatory=$true)][int[]]$FaceXY,
    [string]$Tag = "drill"
)
$ErrorActionPreference = 'Continue'
. (Join-Path $PSScriptRoot 'lib.ps1')

function Find-Ctl($log, $kind, $text) {
    foreach ($line in $log) {
        if ($line -notmatch $kind) { continue }
        if ($text -and $line -notmatch [regex]::Escape($text)) { continue }
        if ($line -match "HWND=(\d+).*Rect=(\d+),(\d+) (\d+)x(\d+)") {
            return @{ H = [int64]$Matches[1]; X = [int]$Matches[2]; Y = [int]$Matches[3]; W = [int]$Matches[4]; Ht = [int]$Matches[5] }
        }
    }
    return $null
}
$aw = (Get-DeskWindows -Desk (Get-Desk -Name $script:DeskName).Handle -VisibleOnly) | Where-Object { $_.T -eq "自动打孔" } | Select-Object -First 1
if ($aw -eq $null) { Write-Output "自动打孔窗口不在"; exit 1 }
$log = Invoke-DeskJob -DeskName $script:DeskName -Tag "$Tag-enum" -Ops @(@{ t = "enumchild"; hwnd = [int64]$aw.H }) -TimeoutSec 90
$reset = Find-Ctl $log "BUTTON" "全部重选"
$start = Find-Ctl $log "BUTTON" "开始打孔"
if (-not $reset -or -not $start) { Write-Output "找不到按钮"; exit 1 }
$rx = $reset.X + [int]($reset.W / 2); $ry = $reset.Y + [int]($reset.Ht / 2)
$sx = $start.X + [int]($start.W / 2); $sy = $start.Y + [int]($start.Ht / 2)
Write-Output ("重选=(" + $rx + "," + $ry + ")  开始打孔=(" + $sx + "," + $sy + ")")
$ops = @(
    @{ t = "clickat"; x = $rx; y = $ry }, @{ t = "sleep"; ms = 800 },
    @{ t = "clickat"; x = $HoleXY[0]; y = $HoleXY[1] }, @{ t = "sleep"; ms = 1200 },
    @{ t = "clickat"; x = $FaceXY[0]; y = $FaceXY[1] }, @{ t = "sleep"; ms = 1200 }
)
$log2 = Invoke-DeskJob -DeskName $script:DeskName -Tag "$Tag-pick" -Ops $ops -TimeoutSec 180
$log3 = Invoke-DeskJob -DeskName $script:DeskName -Tag "$Tag-state" -Ops @(@{ t = "enumchild"; hwnd = [int64]$aw.H }) -TimeoutSec 90
$log3 | Where-Object { $_ -match "个孔|个面|将打|打孔面|参考孔" } | ForEach-Object { Write-Output ("  面板: " + $_.Trim()) }
$log4 = Invoke-DeskJob -DeskName $script:DeskName -Tag "$Tag-go" -Ops @(@{ t = "clickat"; x = $sx; y = $sy }, @{ t = "sleep"; ms = 4000 }) -TimeoutSec 180
Start-Sleep -Seconds 3
$res = (Get-DeskWindows -Desk (Get-Desk -Name $script:DeskName).Handle -VisibleOnly) | Where-Object { $_.T -eq "自动打孔完成" } | Select-Object -First 1
if ($res) {
    $log5 = Invoke-DeskJob -DeskName $script:DeskName -Tag "$Tag-res" -Ops @(@{ t = "enumchild"; hwnd = [int64]$res.H }) -TimeoutSec 60
    $log5 | Where-Object { $_ -match "Static.*\S" } | ForEach-Object { Write-Output ("  结果: " + $_.Trim()) }
    Save-Shot -Name ($Tag + "-result") -Hwnd $res.H | Out-Null
} else { Write-Output "  没有结果框" }
