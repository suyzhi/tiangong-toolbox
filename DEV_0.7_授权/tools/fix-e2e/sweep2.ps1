# tools/fix-e2e/sweep2.ps1 —— 逐个点开插件功能区的命令。
# 坐标标定：客户区位图与 1500x900 窗口 1:1；位图 -> 屏幕 需 +20（窗口外框），
# 命令按钮文字中心的位图坐标由 fresh-top.png（2x 裁图，源起点 (0,28)）量得。
param([switch]$KeepOpen)
$ErrorActionPreference = 'Continue'
$Root = 'C:\Users\admin\Documents\ChatGPT\天工CAD型材内嵌玻璃板插件\DEV_0.7_授权'
. (Join-Path $Root 'tools\mouse-e2e\lib.ps1')
$Desk = 'TGWork070'
$d = Get-Desk -Name $Desk
$main = Get-TgWindow -Desk $d.Handle -ClassLike 'EngineFrame*'
if ($main -eq $null) { Write-Output 'NO CAD'; exit 1 }
Write-Output ("CAD hwnd=" + $main.H + " origin=" + $main.X + "," + $main.Y)

function Get-Stray {
    $out = @()
    foreach ($w in (Get-DeskWindows -Desk $d.Handle -VisibleOnly)) {
        if ($w.Pid -ne $main.Pid) { continue }
        if ($w.H -eq $main.H) { continue }
        if ($w.Wd -lt 120 -or $w.Ht -lt 60) { continue }
        $out += $w
    }
    return $out
}
function Close-Stray { foreach ($w in (Get-Stray)) { Close-StrayWindow -Hwnd $w.H | Out-Null }; Start-Sleep -Milliseconds 700 }

# 切到"插件"标签页：位图中心 (770,45) + 20 偏移
Invoke-DeskJob -DeskName $Desk -Tag 'tab' -Ops @(@{ t='clickat'; x=($main.X+770); y=($main.Y+45) }, @{ t='sleep'; ms=2500 }) | Out-Null

# 命令按钮文字中心的位图坐标
$cmds = @(
    @{ n='四面生成内嵌板';   bx=97;  by=95  },
    @{ n='型材自动填充';     bx=97;  by=122 },
    @{ n='Lineup 模型标记';  bx=97;  by=150 },
    @{ n='导出出图训练数据'; bx=249; by=95  },
    @{ n='批量格式转换';     bx=239; by=122 },
    @{ n='批量排孔';         bx=344; by=95  },
    @{ n='配孔检查';         bx=354; by=122 },
    @{ n='自动打孔';         bx=239; by=150 },
    @{ n='生成矩形板';       bx=437; by=95  },
    @{ n='多型材自动填充';   bx=445; by=122 }
)

Close-Stray
Write-Output ("起始残留窗口：" + (Get-Stray).Count)
foreach ($c in $cmds) {
    $before = @(Get-Stray | ForEach-Object { $_.H })
    $sx = $main.X + $c.bx + 20; $sy = $main.Y + $c.by + 20
    Invoke-DeskJob -DeskName $Desk -Tag 'open' -Ops @(@{ t='fg'; hwnd=[int64]$main.H }, @{ t='clickat'; x=$sx; y=$sy }, @{ t='sleep'; ms=3000 }) | Out-Null
    $after = @(Get-Stray)
    $new = @($after | Where-Object { $before -notcontains $_.H })
    if ($new.Count -eq 0) {
        Write-Output ("  " + $c.n + " -> （没有新窗口）")
    }
    foreach ($w in $new) {
        Write-Output ("  " + $c.n + " -> [" + $w.T + "] " + $w.Wd + "x" + $w.Ht)
        $safe = ($c.n -replace '[\\/:*?<>| ]', '_')
        Save-TgShot -Hwnd $w.H -Path (Join-Path $Root ('artifacts\fix-e2e\cmd2-' + $safe + '.png')) | Out-Null
    }
    if (-not $KeepOpen) { Close-Stray; Start-Sleep -Milliseconds 700 }
}
Write-Output ("收尾残留：" + (Get-Stray).Count)
