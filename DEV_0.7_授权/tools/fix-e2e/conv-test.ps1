# tools/fix-e2e/conv-test.ps1 —— 在私有桌面上用"真实鼠标点击"驱动独立版转换器完成一次转换。
# 目的是验证 FormatConvertForm 的真实代码路径（而不是绕过 UI 直接调 worker）。
param(
    [string]$InputPath = 'H:\桌面\YF25.07\621增值焊接工位\产品\0928最新产品数模',
    [string]$OutRoot   = 'H:\桌面\YF25.07\621增值焊接工位\产品\0928最新产品数模',
    [int]$WaitSec = 240
)
$ErrorActionPreference = 'Continue'
$Root = 'C:\Users\admin\Documents\ChatGPT\天工CAD型材内嵌玻璃板插件\DEV_0.7_授权'
. (Join-Path $Root 'tools\mouse-e2e\lib.ps1')     # 含 desk.ps1 + Save-TgShot + Get-TgWindow 等

$Desk = 'TGConv070'
Get-Desk -Name $Desk | Out-Null
Write-Output ("desktop = " + $Desk)

# 清掉可能存在的旧实例（用户桌面上的那个也要收掉，否则同名窗体会干扰回读）
foreach ($p in @(Get-Process TianGongConverter -ErrorAction SilentlyContinue)) { try { $p.Kill(); Write-Output ("killed stray converter pid=" + $p.Id) } catch {} }
Start-Sleep -Seconds 2

$exe = Join-Path $Root 'build\TianGongConverter.exe'
$work = Join-Path $env:TEMP 'conv-e2e-work'
New-Item -ItemType Directory -Force -Path $work | Out-Null
$convPid = Start-OnDesk -DeskName $Desk -CommandLine ('"' + $exe + '" --input "' + $InputPath + '"') -WorkDir $work
Write-Output ("converter pid = " + $convPid)

$w = $null
for ($i = 0; $i -lt 40; $i++) {
    $w = Get-TgWindow -Desk (Get-Desk -Name $Desk).Handle -TitleLike '*批量格式转换*'
    if ($w -ne $null) { break }
    Start-Sleep -Seconds 2
}
if ($w -eq $null) { Write-Output 'CONVERTER WINDOW NOT FOUND'; Show-DeskWindows -Desk (Get-Desk -Name $Desk).Handle; exit 1 }
Write-Output ("hwnd=" + $w.H + " rect=" + $w.X + "," + $w.Y + " " + $w.Wd + "x" + $w.Ht + " title=" + $w.T)
Save-TgShot -Hwnd $w.H -Path (Join-Path $Root 'artifacts\mouse-e2e\conv-01-open.png') | Out-Null

# 枚举控件（私有桌面上必须由桌面内 helper 做，但控件 HWND 是全局的，这里用 helper 的 enumchild）
$kids = Invoke-DeskJob -DeskName $Desk -Tag 'enum' -Ops @(@{ t = 'enumchild'; hwnd = [int64]$w.H })
foreach ($k in $kids) { Write-Output ('  ' + $k) }
