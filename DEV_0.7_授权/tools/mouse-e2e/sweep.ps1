# tools/mouse-e2e/sweep.ps1 —— 逐个点开插件功能区的命令：先清干净 -> 点 -> 记录新增窗口 -> 按标题栏×关闭。
$ErrorActionPreference = 'Continue'
. (Join-Path $PSScriptRoot 'lib.ps1')

$main = Get-CadMain
if ($main -eq $null) { Write-Output "NO CAD"; exit 1 }

function Get-Stray {
    $d = Get-Desk -Name $script:DeskName
    $out = @()
    foreach ($w in (Get-DeskWindows -Desk $d.Handle -VisibleOnly)) {
        if ($w.Pid -ne $main.Pid) { continue }
        if ($w.T -like "*天工 CAD*") { continue }
        if ($w.C -like "*XTP*" -or $w.C -like "*Internet Explorer*") { continue }
        if ($w.Wd -lt 120 -or $w.Ht -lt 60) { continue }
        $out += $w
    }
    return $out
}

function Close-Stray {
    foreach ($w in (Get-Stray)) {
        Close-StrayWindow -Hwnd $w.H | Out-Null
    }
    Start-Sleep -Milliseconds 600
}

$cmds = @(
    @{ n = '四面生成内嵌板';   x = 58;  y = 72 },
    @{ n = '型材自动填充';     x = 42;  y = 91 },
    @{ n = '批量格式转换';     x = 142; y = 91 },
    @{ n = '生成矩形板';       x = 366; y = 72 },
    @{ n = '多型材自动填充';   x = 366; y = 95 },
    @{ n = '批量排孔';         x = 242; y = 72 },
    @{ n = '配孔检查';         x = 258; y = 91 },
    @{ n = 'Lineup 模型标记';  x = 44;  y = 115 },
    @{ n = '自动打孔';         x = 143; y = 118 },
    @{ n = '导出出图训练数据'; x = 142; y = 72 }
)

Close-Stray
Start-Sleep -Seconds 1
$left = Get-Stray
Write-Output ("起始残留窗口：" + $left.Count)

foreach ($c in $cmds) {
    $before = @(Get-Stray | ForEach-Object { $_.H })
    Invoke-DeskJob -DeskName $script:DeskName -Tag "open" -Ops @(@{ t = "fg"; hwnd = [int64]$main.H }, @{ t = "clickat"; x = $c.x; y = $c.y }) -TimeoutSec 90 | Out-Null
    Start-Sleep -Seconds 3
    $after = Get-Stray
    $new = @($after | Where-Object { $before -notcontains $_.H })
    if ($new.Count -eq 0) {
        Write-Output ("  " + $c.n + " -> （没有新窗口）  [当前残留 " + $after.Count + " 个]")
    }
    foreach ($w in $new) {
        Write-Output ("  " + $c.n + " -> [" + $w.T + "]  " + $w.Wd + "x" + $w.Ht)
        $safe = ($c.n -replace "[\\/:*?<>| ]", "_")
        Save-Shot -Name ("cmd-" + $safe) -Hwnd $w.H | Out-Null
    }
    Start-Sleep -Milliseconds 500
    Close-Stray
    Start-Sleep -Milliseconds 800
}
Write-Output "--- 收尾后残留 ---"
Write-Output ("  " + (Get-Stray).Count + " 个")
