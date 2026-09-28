# tools/fix-e2e/sweep3.ps1 —— 逐个点开插件功能区的命令（坐标由 meas-3x.png 量得，位图+20 = 屏幕）。
# 量测依据（3x 裁图，源起点 (10,60)）：文字行中心位图 y = 76 / 98 / 124 / 145；
# 各命令文字中心位图 x：四面 47、型材 48、Lineup 62、导出 137、批量格式转换 156、自动打孔 152、
# 批量排孔 199、配孔检查 199、生成矩形板 296、多型材 310。
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

Close-Stray
Invoke-DeskJob -DeskName $Desk -Tag 'tab' -Ops @(@{ t='clickat'; x=($main.X+770); y=($main.Y+45) }, @{ t='sleep'; ms=2200 }) | Out-Null
Close-Stray

$cmds = @(
    @{ n='四面生成内嵌板';   bx=47;  by=76  },
    @{ n='型材自动填充';     bx=48;  by=98  },
    @{ n='Lineup 模型标记';  bx=62;  by=124 },
    @{ n='导出出图训练数据'; bx=137; by=76  },
    @{ n='批量格式转换';     bx=156; by=98  },
    @{ n='自动打孔';         bx=152; by=124 },
    @{ n='批量排孔';         bx=199; by=76  },
    @{ n='配孔检查';         bx=199; by=98  },
    @{ n='生成矩形板';       bx=296; by=76  },
    @{ n='多型材自动填充';   bx=310; by=98  }
)

Write-Output ("起始残留窗口：" + (Get-Stray).Count)
foreach ($c in $cmds) {
    $before = @(Get-Stray | ForEach-Object { $_.H })
    $sx = $main.X + $c.bx + 20; $sy = $main.Y + $c.by + 20
    Invoke-DeskJob -DeskName $Desk -Tag 'open' -Ops @(@{ t='clickat'; x=$sx; y=$sy }, @{ t='sleep'; ms=3000 }) | Out-Null
    $after = @(Get-Stray)
    $new = @($after | Where-Object { $before -notcontains $_.H })
    if ($new.Count -eq 0) { Write-Output ("  " + $c.n + " -> （没有新窗口）") }
    foreach ($w in $new) {
        Write-Output ("  " + $c.n + " -> [" + $w.T + "] " + $w.Wd + "x" + $w.Ht)
        $safe = ($c.n -replace '[\\/:*?<>| ]', '_')
        Save-TgShot -Hwnd $w.H -Path (Join-Path $Root ('artifacts\fix-e2e\cmd3-' + $safe + '.png')) | Out-Null
    }
    if (-not $KeepOpen) { Close-Stray; Start-Sleep -Milliseconds 700 }
}
Write-Output ("收尾残留：" + (Get-Stray).Count)
