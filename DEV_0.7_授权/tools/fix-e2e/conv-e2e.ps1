# tools/fix-e2e/conv-e2e.ps1 —— 私有桌面上用真实鼠标消息完成一次"扫描 -> 开始转换 -> 等结果"。
# 关键：控件 HWND 必须从 helper 的 enumchild 日志里解析。
#   helper 的行格式：  HWND=1311186    BUTTON.app.0.34f5582_r8_ad1 Rect=522,501 110x30 Vis=True En=True '开始转换'
param(
    [string]$OutDir = 'H:\桌面\YF25.07\621增值焊接工位\产品\0928最新产品数模\天工E2E输出',
    [int]$WaitSec = 300
)
$ErrorActionPreference = 'Continue'
$Root = 'C:\Users\admin\Documents\ChatGPT\天工CAD型材内嵌玻璃板插件\DEV_0.7_授权'
. (Join-Path $Root 'tools\mouse-e2e\lib.ps1')
$Desk = 'TGConv070'
Get-Desk -Name $Desk | Out-Null

foreach ($p in @(Get-Process TianGongConverter -ErrorAction SilentlyContinue)) { try { $p.Kill() } catch {} }
Start-Sleep -Seconds 2

$exe = Join-Path $Root 'build\TianGongConverter.exe'
$inp = 'H:\桌面\YF25.07\621增值焊接工位\产品\0928最新产品数模'
$convPid = Start-OnDesk -DeskName $Desk -CommandLine ('"' + $exe + '" --input "' + $inp + '"') -WorkDir (Split-Path $exe)
Write-Output ("converter pid = " + $convPid)

$w = $null
for ($i = 0; $i -lt 40; $i++) {
    $w = Get-TgWindow -Desk (Get-Desk -Name $Desk).Handle -TitleLike '*批量格式转换*'
    if ($w -ne $null) { break }
    Start-Sleep -Seconds 2
}
if ($w -eq $null) { Write-Output 'NO WINDOW'; exit 1 }
Write-Output ("hwnd=" + $w.H)

$kids = @(Invoke-DeskJob -DeskName $Desk -Tag 'enum' -Ops @(@{ t = 'enumchild'; hwnd = [int64]$w.H }))
function Find-Ctl([string]$needle) {
    foreach ($k in $kids) {
        if ($k -notmatch "'" -or $k -notmatch "HWND=(\d+)") { continue }
        if ($k.EndsWith("'" + $needle + "'")) { return [int64]$Matches[1] }
    }
    return [int64]0
}
$hStart = Find-Ctl '开始转换'
$hScan  = Find-Ctl '扫描文件'
$hOutEd = 0
foreach ($k in $kids) { if ($k -match "EDIT" -and $k -match "HWND=(\d+)" -and $k -match "Rect=5\d\d,3\d\d") { $hOutEd = [int64]$Matches[1] } }
Write-Output ("hStart=" + $hStart + " hScan=" + $hScan + " hOutEd=" + $hOutEd)
if ($hStart -eq 0 -or $hScan -eq 0) { Write-Output 'CONTROL NOT FOUND'; $kids | ForEach-Object { Write-Output ('  ' + $_) }; exit 1 }

if ($hOutEd -ne 0) {
    Invoke-DeskJob -DeskName $Desk -Tag 'setout' -Ops @(@{ t = 'settext'; hwnd = $hOutEd; v = $OutDir }) | Out-Null
    Start-Sleep -Milliseconds 600
    Write-Output ("输出目录 -> " + $OutDir)
}
Invoke-DeskJob -DeskName $Desk -Tag 'scan' -Ops @(@{ t = 'sendclick'; hwnd = $hScan }, @{ t = 'sleep'; ms = 3500 }) | Write-Output
Save-TgShot -Hwnd $w.H -Path (Join-Path $Root 'artifacts\mouse-e2e\conv-05-scanned.png') | Out-Null
Start-Sleep -Seconds 8
Invoke-DeskJob -DeskName $Desk -Tag 'start' -Ops @(@{ t = 'sendclick'; hwnd = $hStart }) | Write-Output

$workRoot = $null
for ($i = 0; $i -lt [int]($WaitSec / 4); $i++) {
    Start-Sleep -Seconds 4
    $d = Get-ChildItem "$env:LOCALAPPDATA\Temp\TianGongConverter" -Directory -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($d -ne $null) { $workRoot = $d.FullName }
    $st = ''
    if ($workRoot) { foreach ($f in @(Get-ChildItem $workRoot -Filter 'status-*.txt' -ErrorAction SilentlyContinue)) { $st += (Get-Content $f.FullName -Raw) } }
    Write-Output ("t+" + (($i+1)*4) + "s status=" + ($st -replace "[\r\n]+",' || '))
    if ($st -match 'EXIT') { break }
}
Write-Output "--- status files ---"
if ($workRoot) {
    foreach ($f in @(Get-ChildItem $workRoot -Filter 'status-*.txt' -ErrorAction SilentlyContinue)) {
        Write-Output ("=== " + $f.Name + " (" + $f.Length + " bytes) ===")
        Get-Content $f.FullName -Raw
    }
}
Save-TgShot -Hwnd $w.H -Path (Join-Path $Root 'artifacts\mouse-e2e\conv-06-result.png') | Out-Null
Write-Output "--- 输出目录 ---"
Get-ChildItem $OutDir -Recurse -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName + "  " + $_.Length }
