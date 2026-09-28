param(
    [Parameter(Mandatory=$true)][int64]$Hwnd,
    [string]$ButtonText = '确定',
    [string]$Tag = 'dismiss'
)
$ErrorActionPreference = 'Continue'
. (Join-Path (Split-Path $PSScriptRoot -Parent) 'mouse-e2e\lib.ps1')
# 1) 现场枚举（helper 跑在私有桌面上，句柄才有效）
$log = Invoke-DeskJob -DeskName $script:DeskName -Tag ($Tag + '-enum') -Ops @(@{ t = 'enumchild'; hwnd = $Hwnd }) -TimeoutSec 90
$log | Set-Content -LiteralPath (Join-Path $script:Out ($Tag + '-enum.txt')) -Encoding UTF8
$btn = $null
foreach ($l in $log) {
  Write-Output $l
  if ($l -match 'BUTTON' -and $l -match [regex]::Escape($ButtonText) -and $l -match 'HWND=(\d+).*Rect=(-?\d+),(-?\d+) (\d+)x(\d+)') {
    $btn = @{ H = [int64]$Matches[1]; X = [int]$Matches[2]; Y = [int]$Matches[3]; W = [int]$Matches[4]; Ht = [int]$Matches[5] }
  }
}
if ($btn -eq $null) { Write-Output ('没找到按钮 ' + $ButtonText); exit 1 }
Write-Output ('按钮 hwnd=' + $btn.H + ' rect=' + $btn.X + ',' + $btn.Y + ' ' + $btn.W + 'x' + $btn.Ht)
# 2) 真鼠标点它的中心
$cx = $btn.X + [int]($btn.W / 2); $cy = $btn.Y + [int]($btn.Ht / 2)
$log2 = Invoke-DeskJob -DeskName $script:DeskName -Tag $Tag -Ops @(
    @{ t = 'fg'; hwnd = $Hwnd },
    @{ t = 'clickat'; x = $cx; y = $cy },
    @{ t = 'sleep'; ms = 1500 }
) -TimeoutSec 120
$log2 | ForEach-Object { Write-Output $_ }
if ((@($log2 | Where-Object { $_ -match 'clickat' })).Count -gt 0) {
  Write-Output ('--- clickat 行: ' + ((@($log2 | Where-Object { $_ -match 'clickat' }))[0]))
}
Start-Sleep -Milliseconds 800
Write-Output '--- 现在的窗口 ---'
Show-Windows
