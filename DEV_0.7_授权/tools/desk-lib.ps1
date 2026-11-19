# tools/desk-lib.ps1 —— 私有桌面 + 天工CAD 的最小工具集。
# 笔记：COM 激活（New-Object -ComObject）会把 CAD 启动到"默认桌面"，无法隔离用户；
# 必须用 Start-OnDesk 直接启动 TianGong.exe，COM 客户端再从 ROT 连上去。
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$extDesk = 'C:\Users\admin\.dsh\skills\windows-app-gui-automation\scripts\desk.ps1'
if (Test-Path $extDesk) {
    . $extDesk
} else {
    if (-not ('TgPrivateDesk' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public class TgPrivateDesk {
    public const uint GENERIC_ALL = 0x10000000;
    [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    public static extern IntPtr CreateDesktopW(string lpszDesktop, IntPtr lpszDevice, IntPtr pDevmode, uint dwFlags, uint dwDesiredAccess, IntPtr lpsa);
    [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    public static extern IntPtr OpenDesktopW(string lpszDesktop, uint dwFlags, bool fInherit, uint dwDesiredAccess);
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll", SetLastError=true)]
    public static extern bool EnumDesktopWindows(IntPtr hDesktop, EnumWindowsProc lpfn, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
    public struct STARTUPINFO {
        public int cb; public string lpReserved; public string lpDesktop; public string lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    public static extern bool CreateProcessW(string app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);

    public static IntPtr EnsureDesktop(string name) {
        IntPtr h = OpenDesktopW(name, 0, false, GENERIC_ALL);
        if (h == IntPtr.Zero) h = CreateDesktopW(name, IntPtr.Zero, IntPtr.Zero, 0, GENERIC_ALL, IntPtr.Zero);
        if (h == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return h;
    }
    public static int StartOnDesktop(string deskName, string cmdLine, string workDir) {
        var si = new STARTUPINFO(); si.cb = Marshal.SizeOf(typeof(STARTUPINFO)); si.lpDesktop = deskName;
        var pi = new PROCESS_INFORMATION();
        var sb = new StringBuilder(cmdLine);
        if (!CreateProcessW(null, sb, IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero, workDir, ref si, out pi))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
        return pi.dwProcessId;
    }
    public class WinInfo { public IntPtr H; public string C; public string T; public int Wd; public int Ht; }
    public static List<WinInfo> ListWindows(IntPtr desk, bool visibleOnly) {
        var list = new List<WinInfo>();
        EnumDesktopWindows(desk, (h, lp) => {
            if (visibleOnly && !IsWindowVisible(h)) return true;
            RECT r; if (!GetWindowRect(h, out r)) return true;
            var c = new StringBuilder(256); GetClassNameW(h, c, 256);
            var t = new StringBuilder(512); GetWindowTextW(h, t, 512);
            list.Add(new WinInfo { H = h, C = c.ToString(), T = t.ToString(), Wd = r.R - r.L, Ht = r.B - r.T });
            return true;
        }, IntPtr.Zero);
        return list;
    }
}
'@ -Language CSharp
    }
    function Get-Desk {
        param([Parameter(Mandatory=$true)][string]$Name)
        $h = [TgPrivateDesk]::EnsureDesktop($Name)
        [PSCustomObject]@{ Name = $Name; Handle = $h }
    }
    function Start-OnDesk {
        param([Parameter(Mandatory=$true)][string]$DeskName, [Parameter(Mandatory=$true)][string]$CommandLine, [string]$WorkDir = $null)
        [TgPrivateDesk]::StartOnDesktop($DeskName, $CommandLine, $WorkDir)
    }
    function Get-DeskWindows {
        param([Parameter(Mandatory=$true)][IntPtr]$Desk, [switch]$VisibleOnly)
        [TgPrivateDesk]::ListWindows($Desk, [bool]$VisibleOnly)
    }
}

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
