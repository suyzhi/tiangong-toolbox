# tools/desk-lib.ps1 —— 私有桌面 + 天工CAD 的最小工具集。
# 笔记：COM 激活（New-Object -ComObject）会把 CAD 启动到"默认桌面"，无法隔离用户；
# 必须用 Start-OnDesk 直接启动 TianGong.exe，COM 客户端再从 ROT 连上去。
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
. 'C:\Users\admin\.dsh\skills\windows-app-gui-automation\scripts\desk.ps1'

if (-not ('TgDeskNative' -as [type])) {
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public class TgDeskNative {
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int ht, bool repaint);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
    public static int[] Rect(IntPtr h) { RECT r; if (!GetWindowRect(h, out r)) return null; return new int[]{ r.L, r.T, r.R - r.L, r.B - r.T }; }
    public static string Title(IntPtr h) { var t = new StringBuilder(512); GetWindowTextW(h, t, 512); return t.ToString(); }
    public static string Cls(IntPtr h) { var c = new StringBuilder(256); GetClassNameW(h, c, 256); return c.ToString(); }
}
'@ -Language CSharp
}

# 截图（PowerShell 侧做位图，C# 只放 P/Invoke —— Add-Type 的默认引用集里没有 System.Drawing）
function Save-TgShot {
    param([Parameter(Mandatory=$true)][IntPtr]$Hwnd, [Parameter(Mandatory=$true)][string]$Path, [int]$Flags = 2)
    $r = [TgDeskNative]::Rect($Hwnd)
    if ($r -eq $null) { return $false }
    $w = $r[2]; $h = $r[3]
    if ($w -le 0 -or $h -le 0 -or $w -gt 8000 -or $h -gt 8000) { return $false }
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    $ok = [TgDeskNative]::PrintWindow($Hwnd, $hdc, $Flags)
    $g.ReleaseHdc($hdc); $g.Dispose()
    $dir = Split-Path -Parent $Path
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    $bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return $ok
}

function Get-TgWindow {
    param([Parameter(Mandatory=$true)][IntPtr]$Desk, [string]$ClassLike = '', [string]$TitleLike = '')
    $ws = Get-DeskWindows -Desk $Desk -VisibleOnly
    foreach ($w in $ws) {
        if ($w.Wd -le 0 -or $w.Ht -le 0) { continue }
        if ($ClassLike -and $w.C -notlike $ClassLike) { continue }
        if ($TitleLike -and $w.T -notlike $TitleLike) { continue }
        return $w
    }
    return $null
}

function Wait-TgWindow {
    param([Parameter(Mandatory=$true)][IntPtr]$Desk, [string]$ClassLike = '', [string]$TitleLike = '', [int]$TimeoutSec = 180)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        $w = Get-TgWindow -Desk $Desk -ClassLike $ClassLike -TitleLike $TitleLike
        if ($w -ne $null) { return $w }
        Start-Sleep -Seconds 3
    }
    return $null
}
