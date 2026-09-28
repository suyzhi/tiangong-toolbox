# tools/mouse-e2e/lib.ps1 —— 真鼠标点击实测用的私有桌面驱动（DEV 0.7 树自己的路径）。
# 关键约束（来自 windows-app-gui-automation 技能）：
#   * 输入桌面（SendInput）在私有桌面上无效，只能用"移动该桌面的光标 + 发消息"。
#   * 所有枚举/截图/点击都必须由跑在该桌面上的 helper 完成。
$ErrorActionPreference = 'Stop'
$script:Root = 'C:\Users\admin\Documents\ChatGPT\天工CAD型材内嵌玻璃板插件\DEV_0.7_授权'
$script:Out  = Join-Path $script:Root 'artifacts\mouse-e2e'
$script:DeskName = 'TGWork070'
New-Item -ItemType Directory -Force -Path $script:Out | Out-Null
. (Join-Path $script:Root 'tools\desk-lib.ps1')

function Get-CadMain {
    param([int]$TimeoutSec = 0, [IntPtr]$Desk = [IntPtr]::Zero)
    if ($Desk -eq [IntPtr]::Zero) { $Desk = (Get-Desk -Name $script:DeskName).Handle }
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    do {
        $w = Get-TgWindow -Desk $Desk -ClassLike 'EngineFrame*'
        if ($w -ne $null) { return $w }
        Start-Sleep -Seconds 3
    } while ((Get-Date) -lt $deadline)
    return $null
}

function Save-Shot {
    param([Parameter(Mandatory=$true)][string]$Name, [IntPtr]$Hwnd = [IntPtr]::Zero)
    if ($Hwnd -eq [IntPtr]::Zero) {
        $w = Get-CadMain
        if ($w -eq $null) { Write-Output "NO CAD WINDOW"; return $null }
        $Hwnd = $w.H
    }
    $path = Join-Path $script:Out ($Name + ".png")
    $ok = Save-TgShot -Hwnd $Hwnd -Path $path -Flags 2
    Write-Output ("SHOT ok=" + $ok + " -> " + $path)
    return $path
}

# 在私有桌面上执行一串操作（JSON 数组），并回读日志。
function Invoke-Act {
    param([Parameter(Mandatory=$true)][string]$Tag, [Parameter(Mandatory=$true)][string]$Actions, [int]$WaitMs = 1200, [switch]$NoRefore)
    $d = Get-Desk -Name $script:DeskName
    $main = Get-CadMain
    if ($main -eq $null) { Write-Output "NO CAD WINDOW"; return $null }
    $ops = @()
    if (-not $NoRefore) { $ops += @{ t = "fg"; hwnd = [int64]$main.H } }
    foreach ($a in ($Actions | ConvertFrom-Json)) { $ops += $a }
    $ops += @{ t = "sleep"; ms = $WaitMs }
    $log = Invoke-DeskJob -DeskName $script:DeskName -Tag $Tag -Ops $ops -TimeoutSec 180
    foreach ($line in $log) { Write-Output ("  " + $line) }
    return $log
}

# 列出私有桌面上当前可见窗口（回读纪律：每步之后都要看一眼有没有新弹窗）。
function Show-Windows {
    $d = Get-Desk -Name $script:DeskName
    foreach ($w in (Get-DeskWindows -Desk $d.Handle -VisibleOnly)) {
        Write-Output ("  HWND={0,-10} PID={1,-7} Rect={2},{3} {4}x{5} cls={6} title={7}" -f $w.H, $w.Pid, $w.X, $w.Y, $w.Wd, $w.Ht, $w.C, $w.T)
    }
}
# 关闭窗口：标题栏的 × 在非客户区，post 客户区消息点不到它；直接发 WM_CLOSE 更可靠（跨桌面可用）。
if (-not ('TgCloseApi' -as [type])) {
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public class TgCloseApi {
    [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    public static bool Close(IntPtr h) { if (!IsWindow(h)) return false; return PostMessageW(h, 0x0010, IntPtr.Zero, IntPtr.Zero); }
}
'@ -Language CSharp
}

function Close-StrayWindow {
    param([Parameter(Mandatory=$true)][Int64]$Hwnd)
    $ok = [TgCloseApi]::Close([IntPtr]$Hwnd)
    Start-Sleep -Milliseconds 900
    return $ok
}

