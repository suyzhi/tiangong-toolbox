# tools/mouse-e2e/activate.ps1 —— 在已经弹出的激活窗口里，用真键盘输入激活码并真鼠标点「激活」。
param([Parameter(Mandatory=$true)][string]$CodeFile, [string]$Tag = "activate")
$ErrorActionPreference = 'Continue'
. (Join-Path $PSScriptRoot 'lib.ps1')

function Get-Ctl($log, $kind, $wantText) {
    foreach ($line in $log) {
        if ($line -notmatch $kind) { continue }
        if ($wantText -and $line -notmatch [regex]::Escape($wantText)) { continue }
        if ($line -match "HWND=(\d+).*Rect=(\d+),(\d+) (\d+)x(\d+)") {
            return @{ H = [int64]$Matches[1]; X = [int]$Matches[2]; Y = [int]$Matches[3]; W = [int]$Matches[4]; Ht = [int]$Matches[5] }
        }
    }
    return $null
}

$d = Get-Desk -Name $script:DeskName
$win = $null
foreach ($c in (Get-DeskWindows -Desk $d.Handle -VisibleOnly)) { if ($c.T -like "*授权激活*") { $win = $c; break } }
if ($win -eq $null) { Write-Output "激活窗口未出现"; Show-Windows; exit 1 }
Write-Output ("激活窗口 hwnd=" + $win.H)

$log = Invoke-DeskJob -DeskName $script:DeskName -Tag ($Tag + "-enum") -Ops @(@{ t = "enumchild"; hwnd = [int64]$win.H }) -TimeoutSec 90
$box = Get-Ctl $log "EDIT" $null
$btn = Get-Ctl $log "BUTTON" "激活"
if ($box -eq $null -or $btn -eq $null) { Write-Output "找不到控件"; $log | ForEach-Object { Write-Output ("  " + $_) }; exit 1 }

$code = (Get-Content -LiteralPath $CodeFile -Raw).Trim()
Write-Output ("输入激活码长度 " + $code.Length + " → 点按钮 (" + ($btn.X + [int]($btn.W / 2)) + "," + ($btn.Y + [int]($btn.Ht / 2)) + ")")
$ops = @(
    @{ t = "fg"; hwnd = [int64]$win.H },
    @{ t = "clickat"; x = ($box.X + 40); y = ($box.Y + 20) },
    @{ t = "type"; hwnd = $box.H; v = $code },
    @{ t = "sleep"; ms = 500 },
    @{ t = "clickat"; x = ($btn.X + [int]($btn.W / 2)); y = ($btn.Y + [int]($btn.Ht / 2)) }
)
$log2 = Invoke-DeskJob -DeskName $script:DeskName -Tag $Tag -Ops $ops -TimeoutSec 180
$log2 | Where-Object { $_ -match "clickat|type" } | ForEach-Object { Write-Output ("  " + $_.Trim().Substring(0, [Math]::Min(160, $_.Trim().Length))) }
Start-Sleep -Seconds 2
Write-Output "--- 现在的窗口 ---"
Show-Windows
