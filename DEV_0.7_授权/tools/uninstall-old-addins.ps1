param(
    # 只清理这两条"更早版本"的注册记录；现行版本（{8C05165C…}）和 NDS 其它加载项一律不碰。
    [string]$AutoUninstall = 'no'
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

# 这两个 GUID 是本插件更早的两代（TianGongPanel 0.1.2 / TianGongPanelAuto 0.3.0）：
#   * 按钮与本插件同名（生成矩形板 / 多型材自动填充），用户很容易点错；
#   * 旧代码里有同一个"按文件名插入、命中装配目录同名旧零件"的坑（已在新版修掉）。
# 因此建议卸载掉它们：功能在新版里都有。
$targets = @(
    @{ Guid = '{98BF0FA8-7D65-4A13-9926-6A45836FD6D2}'; Name = '矩形板 (DEV 0.1.2)' },
    @{ Guid = '{B3B28C54-11D0-4488-B226-AD62429CEED2}'; Name = '矩形板 (DEV 0.3.0)' }
)
$addInsRoot = 'HKCU:\Software\NDS\TianGong\Version 225\AddIns'

foreach ($t in $targets) {
    $nds = Join-Path $addInsRoot $t.Guid
    $cls = "HKCU:\Software\Classes\CLSID\" + $t.Guid
    $progId = if ($t.Name -like '*0.1*') { 'HKCU:\Software\Classes\TianGongPanel.PanelAddIn' } else { 'HKCU:\Software\Classes\TianGongPanelAuto.PanelAddIn' }
    $hasNds = Test-Path $nds
    $hasCls = Test-Path $cls
    if (-not $hasNds -and -not $hasCls) { Write-Output ($t.Name + '：本来就没注册，跳过'); continue }
    if ($AutoUninstall -ne 'yes') {
        Write-Output ($t.Name + '：' + $t.Guid + '  ' + $(if ($hasNds) { '（NDS 加载项记录存在）' } else { '' }) + $(if ($hasCls) { '（COM 注册存在）' } else { '' }) + '  → 干跑，未删除')
        continue
    }
    if ($hasNds) { Remove-Item -Path $nds -Recurse -Force }
    if ($hasCls) { Remove-Item -Path $cls -Recurse -Force }
    if (Test-Path $progId) { Remove-Item -Path $progId -Recurse -Force }
    Write-Output ($t.Name + '：已卸载（下次启动 CAD 生效）')
}
Write-Output ''
Write-Output '用法：先干跑看清单 → 确认后加 -AutoUninstall yes 真删。'
Write-Output '注意：卸载后要完全重启天工 CAD；现行版本 {8C05165C-65A4-4EF2-A138-508589D82004} 不受影响。'
