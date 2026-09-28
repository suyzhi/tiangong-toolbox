# tools/mouse-e2e/01-start.ps1 —— 把最新构建注册进 CAD，然后在私有桌面上启动天工 CAD 并打开夹具。
param(
    [string]$LibraryPath,
    [string]$Fixture = 'C:\Users\admin\Documents\ChatGPT\天工CAD型材内嵌玻璃板插件\DEV_0.4_插件框架\artifacts\v6-20260925\autohole-tapped-20260925-105921\TappedFixture.asm'
)
$ErrorActionPreference = 'Continue'
. (Join-Path $PSScriptRoot 'lib.ps1')

if (-not $LibraryPath) {
    $LibraryPath = Join-Path $script:Root 'build\license-test\TianGongCadSuite.dll'
}
if (-not (Test-Path -LiteralPath $LibraryPath)) { throw ("找不到要注册的插件：" + $LibraryPath) }
Write-Output ('注册插件：' + $LibraryPath)
& (Join-Path $script:Root 'tools\install.ps1') -LibraryPath $LibraryPath | Out-String | Write-Output

# 单实例程序：先把残留实例清掉，再在私有桌面上启动。
foreach ($p in @(Get-Process -Name TianGong -ErrorAction SilentlyContinue)) { try { $p.Kill() } catch {} }
Start-Sleep -Seconds 5

$d = Get-Desk -Name $script:DeskName
Write-Output ('desktop handle = ' + $d.Handle)
$cadPid = Start-OnDesk -DeskName $script:DeskName -CommandLine ('"C:\Program Files\NDS\TianGong 2025\Program\TianGong.exe" "' + $Fixture + '"') -WorkDir (Split-Path -Parent $Fixture)
Write-Output ('CAD pid = ' + $cadPid)

$main = Get-CadMain -TimeoutSec 300 -Desk $d.Handle
if ($main -eq $null) { Write-Output "CAD 主窗口未出现"; Show-Windows; exit 1 }
Write-Output ('CAD hwnd=' + $main.H + ' title=' + $main.T + ' rect=' + $main.X + ',' + $main.Y + ' ' + $main.Wd + 'x' + $main.Ht)
Start-Sleep -Seconds 20
Write-Output "--- 私有桌面可见窗口 ---"
Show-Windows
Save-Shot -Name "01-launched" | Out-Null
