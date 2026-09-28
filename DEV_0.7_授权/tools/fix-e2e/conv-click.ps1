$ErrorActionPreference='Continue'
$Root = 'C:\Users\admin\Documents\ChatGPT\天工CAD型材内嵌玻璃板插件\DEV_0.7_授权'
. (Join-Path $Root 'tools\mouse-e2e\lib.ps1')
$Desk = 'TGConv070'
$w = Get-TgWindow -Desk (Get-Desk -Name $Desk).Handle -TitleLike '*批量格式转换*'
if ($w -eq $null) { 'NO WINDOW'; exit 1 }
Write-Output ("hwnd=" + $w.H + " rect=" + $w.X + "," + $w.Y)

# 1) 点击"自动（推荐：单进程）"取消勾选 -> 并行进程=2（与用户截图一致）
$ops = @(
  @{ t='fg'; hwnd=[int64]$w.H },
  @{ t='clickat'; x=746; y=455 },
  @{ t='sleep'; ms=800 },
  @{ t='shot'; hwnd=[int64]$w.H; path=(Join-Path $Root 'artifacts\mouse-e2e\conv-02-unautofirst.png') }
)
Invoke-DeskJob -DeskName $Desk -Tag 't1' -Ops $ops | Out-Null

# 2) 真实鼠标点击"扫描文件" (522..956 是窗口客户区起点，屏幕坐标 = rect + 客户区坐标；这里直接用屏幕坐标)
$ops2 = @(
  @{ t='clickat'; x=896; y=516 },   # 扫描文件 中心: 846+50=896, 501+15+... 用枚举 Rect 推算
  @{ t='sleep'; ms=2500 },
  @{ t='shot'; hwnd=[int64]$w.H; path=(Join-Path $Root 'artifacts\mouse-e2e\conv-03-scanned.png') }
)
Invoke-DeskJob -DeskName $Desk -Tag 't2' -Ops $ops2 | Write-Output

# 3) 真实鼠标点击"开始转换"
$ops3 = @(
  @{ t='clickat'; x=577; y=516 },
  @{ t='sleep'; ms=1500 },
  @{ t='shot'; hwnd=[int64]$w.H; path=(Join-Path $Root 'artifacts\mouse-e2e\conv-04-started.png') }
)
Invoke-DeskJob -DeskName $Desk -Tag 't3' -Ops $ops3 | Write-Output
Write-Output "--- 私有桌面窗口 ---"
Show-Windows