param(
    [string]$Tag = 'b1',
    [int]$CheckWaitMs = 15000,
    [switch]$NoRibbonClick
)
# 注意：跨进程控制台的输出一律只打 ASCII（中文会被父进程按 GBK 解码，父进程匹配中文会静默失败）。
# 中文结果写进 <Tag>-panel.txt（UTF-8），由调用方用 -Encoding UTF8 读。
$ErrorActionPreference = 'Continue'
. (Join-Path (Split-Path $PSScriptRoot -Parent) 'mouse-e2e\lib.ps1')
$pwshExe = 'C:\Program Files\PowerShell\7\pwsh.exe'
$deskrun = 'C:\temp\tg-test\deskrun.ps1'
$dumpScript = 'C:\temp\tg-test\dumpcheck.ps1'
$d = Get-Desk -Name $script:DeskName
$main = Get-CadMain -TimeoutSec 30 -Desk $d.Handle
if ($main -eq $null) { Write-Output 'NOCAD'; exit 1 }
function Strays { @(Get-DeskWindows -Desk $d.Handle -VisibleOnly | Where-Object { $_.Pid -eq $main.Pid -and $_.H -ne $main.H -and $_.Wd -gt 120 -and $_.Ht -gt 60 }) }
function PanelWindows { @(Strays | Where-Object { $_.T -like '*配孔检查*' }) }
function Ctl($log, $kind, $text) {
    foreach ($line in $log) {
        if ($line -notmatch $kind) { continue }
        if ($text -and $line -notmatch [regex]::Escape($text)) { continue }
        if ($line -match 'HWND=(\d+).*Rect=(-?\d+),(-?\d+) (\d+)x(\d+)') {
            return @{ H = [int64]$Matches[1]; X = [int]$Matches[2]; Y = [int]$Matches[3]; W = [int]$Matches[4]; Ht = [int]$Matches[5] }
        }
    }
    return $null
}
# 1) 关掉 CAD 的模态框（真鼠标点它的按钮）
for ($k = 0; $k -lt 4; $k++) {
    $dlg = (Strays) | Where-Object { $_.C -eq '#32770' } | Select-Object -First 1
    if ($dlg -eq $null) { break }
    $l0 = Invoke-DeskJob -DeskName $script:DeskName -Tag ($Tag + '-dlg') -Ops @(@{ t = 'enumchild'; hwnd = [int64]$dlg.H }) -TimeoutSec 60
    $b = Ctl $l0 'Button' $null
    if ($b -eq $null) { Close-StrayWindow -Hwnd $dlg.H | Out-Null; Start-Sleep -Milliseconds 700; continue }
    $dx = $b.X + [int]($b.W / 2); $dy = $b.Y + [int]($b.Ht / 2)
    Write-Output ('MODAL hwnd=' + $dlg.H + ' click=(' + $dx + ',' + $dy + ')')
    Invoke-DeskJob -DeskName $script:DeskName -Tag ($Tag + '-dlgclick') -Ops @(@{ t = 'fg'; hwnd = [int64]$dlg.H }, @{ t = 'clickat'; x = $dx; y = $dy }, @{ t = 'sleep'; ms = 1200 }) -TimeoutSec 120 | Out-Null
}
# 2) 关掉旧面板（否则插件会把旧面板重新显示，读到的是上一次的结果 —— 踩过）
for ($k = 0; $k -lt 6; $k++) {
    $p = PanelWindows | Select-Object -First 1
    if ($p -eq $null) { break }
    Close-StrayWindow -Hwnd $p.H | Out-Null
    Write-Output ('CLOSED_PANEL hwnd=' + $p.H)
    Start-Sleep -Milliseconds 900
}
if ((PanelWindows).Count -gt 0) { Write-Output 'OLDPANEL_STILL_OPEN'; exit 2 }
# 3) 点功能区命令开面板（最多重试 3 次）
$form = $null
for ($attempt = 1; $attempt -le 3 -and $form -eq $null; $attempt++) {
    if (-not $NoRibbonClick) {
        Invoke-Act -Tag ($Tag + '-tab' + $attempt) -Actions (@(@{ t='clickat'; x=($main.X+770); y=($main.Y+45) }, @{ t='sleep'; ms=2500 }) | ConvertTo-Json -Compress) | Out-Null
        Invoke-Act -Tag ($Tag + '-cmd' + $attempt) -Actions (@(@{ t='clickat'; x=($main.X+260); y=($main.Y+96) }, @{ t='sleep'; ms=3000 }) | ConvertTo-Json -Compress) | Out-Null
    }
    for ($i = 0; $i -lt 10; $i++) {
        $form = PanelWindows | Select-Object -First 1
        if ($form -ne $null) { break }
        Start-Sleep -Seconds 1
    }
    if ($form -eq $null) { Write-Output ('RETRY_OPEN attempt=' + $attempt) }
}
if ($form -eq $null) { Write-Output 'NOPANEL'; Show-Windows; exit 1 }
Write-Output ('PANEL hwnd=' + $form.H)
# 4) 点「开始检查」
$log = Invoke-DeskJob -DeskName $script:DeskName -Tag ($Tag + '-enum1') -Ops @(@{ t = 'enumchild'; hwnd = [int64]$form.H }) -TimeoutSec 90
$log | Set-Content -LiteralPath (Join-Path $script:Out ($Tag + '-enum1.txt')) -Encoding UTF8
$btn = Ctl $log 'BUTTON' '开始检查'
if ($btn -eq $null) { $btn = Ctl $log 'BUTTON' '重新检查' }
if ($btn -eq $null) { Write-Output 'NOBUTTON'; exit 1 }
$bx = $btn.X + [int]($btn.W / 2); $by = $btn.Y + [int]($btn.Ht / 2)
Write-Output ('RUNBTN hwnd=' + $btn.H + ' at=(' + $bx + ',' + $by + ')')
$log2 = Invoke-DeskJob -DeskName $script:DeskName -Tag ($Tag + '-go') -Ops @(
    @{ t = 'fg'; hwnd = [int64]$form.H },
    @{ t = 'clickat'; x = $bx; y = $by },
    @{ t = 'sleep'; ms = $CheckWaitMs }
) -TimeoutSec 300
$log2 | Where-Object { $_ -match 'clickat' } | ForEach-Object { Write-Output ('  ' + $_.Trim()) }
Start-Sleep -Seconds 2
# 5) 截图 + 由桌面内进程回读面板（中文字都进文件）
$shotFile = Join-Path $script:Out ($Tag + '-result.png')
Invoke-DeskJob -DeskName $script:DeskName -Tag ($Tag + '-shot') -Ops @(@{ t = 'shot'; hwnd = [int64]$form.H; path = $shotFile }) -TimeoutSec 90 | Out-Null
$dumpFile = Join-Path $script:Out ($Tag + '-panel.txt')
& $pwshExe -NoProfile -ExecutionPolicy Bypass -File $deskrun -Script $dumpScript -OutFile $dumpFile 2>&1 | Out-Null
Write-Output ('DUMP ' + $dumpFile)
Write-Output ('SHOT ' + $shotFile)
