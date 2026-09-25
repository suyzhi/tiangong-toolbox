param(
  [string]$Tag = 'live',
  [string]$Actions = '[]',
  [switch]$StartCad,
  [string]$Fixture = ''
)
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName System.Drawing
$work = 'C:\Users\admin\Documents\ChatGPT\天工CAD型材内嵌玻璃板插件\DEV_0.4_插件框架\artifacts\live'
New-Item -ItemType Directory -Force -Path $work | Out-Null

if (-not ('LiveApi' -as [type])) {
Add-Type -TypeDefinition @'
using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
public class LiveApi {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern IntPtr SetActiveWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int ht, bool repaint);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }

    // 强制把窗口弄到前台：AttachThreadInput 借前台线程的输入队列，这是绕过
    // "只有前台进程才能改前台窗口"限制的标准做法。
    public static bool ForceForeground(IntPtr h) {
        for (int i = 0; i < 3; i++) {
            IntPtr fg = GetForegroundWindow();
            if (fg == h) return true;
            uint fgThread = GetWindowThreadProcessId(fg, out uint _);
            uint myThread = GetCurrentThreadId();
            bool attached = false;
            try {
                if (fgThread != myThread) attached = AttachThreadInput(fgThread, myThread, true);
                ShowWindow(h, 9);          // SW_RESTORE
                BringWindowToTop(h);
                SetForegroundWindow(h);
                SetActiveWindow(h);
            } finally {
                if (attached) AttachThreadInput(fgThread, myThread, false);
            }
            Thread.Sleep(350);
            if (GetForegroundWindow() == h) return true;
        }
        return GetForegroundWindow() == h;
    }
    public static void Click(int x, int y) {
        SetCursorPos(x, y);
        Thread.Sleep(130);
        mouse_event(0x0002, 0, 0, 0, IntPtr.Zero);
        Thread.Sleep(70);
        mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);
        Thread.Sleep(200);
    }
    public static int[] Cursor() { POINT p; GetCursorPos(out p); return new int[]{ p.X, p.Y }; }
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
    public static void Key(byte vk) {
        keybd_event(vk, 0, 0, IntPtr.Zero); Thread.Sleep(50);
        keybd_event(vk, 0, 2, IntPtr.Zero); Thread.Sleep(120);
    }
    public static IntPtr FindWindow(string cls, string titlePart) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, l) => {
            var c = new StringBuilder(256); GetClassNameW(h, c, 256);
            if (cls.Length > 0 && !c.ToString().StartsWith(cls)) return true;
            var t = new StringBuilder(512); GetWindowTextW(h, t, 512);
            if (titlePart.Length > 0 && t.ToString().IndexOf(titlePart, StringComparison.OrdinalIgnoreCase) < 0) return true;
            if (!IsWindowVisible(h)) return true;
            found = h; return false;
        }, IntPtr.Zero);
        return found;
    }
    public static string Title(IntPtr h) { var t = new StringBuilder(512); GetWindowTextW(h, t, 512); return t.ToString(); }
    public static uint PidOf(IntPtr h) { uint pid; GetWindowThreadProcessId(h, out pid); return pid; }
    public static string Cls(IntPtr h) { var c = new StringBuilder(256); GetClassNameW(h, c, 256); return c.ToString(); }
    public static int[] Rect(IntPtr h) { RECT r; if (!GetWindowRect(h, out r)) return null; return new int[]{ r.L, r.T, r.R-r.L, r.B-r.T }; }
    public static IntPtr FindByTitleExact(string title) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, l) => {
            if (!IsWindowVisible(h)) return true;
            var t = new StringBuilder(512); GetWindowTextW(h, t, 512);
            if (t.ToString() == title) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
'@ -Language CSharp
}

function Shot-Rect([string]$Path, [int]$X, [int]$Y, [int]$W, [int]$H) {
    $bmp = New-Object System.Drawing.Bitmap($W, $H)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($X, $Y, 0, 0, (New-Object System.Drawing.Size($W, $H)))
    $g.Dispose(); $bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
}

if ($StartCad) {
    foreach ($p in @(Get-Process -Name TianGong -ErrorAction SilentlyContinue)) { try { $p.Kill() } catch { } }
    Start-Sleep -Seconds 3
    $arg = if ($Fixture) { '"' + $Fixture + '"' } else { '' }
    Start-Process -FilePath 'C:\Program Files\NDS\TianGong 2025\Program\TianGong.exe' -ArgumentList $arg
    $cad = [IntPtr]::Zero
    $deadline = (Get-Date).AddSeconds(180)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 3
        $cad = [LiveApi]::FindWindow('EngineFrame', '')
        if ($cad -ne [IntPtr]::Zero -and [LiveApi]::Title($cad) -match '\.(asm|par|dft)') { break }
    }
    if ($cad -eq [IntPtr]::Zero) { Write-Output 'CAD 窗口未出现'; exit 1 }
}

$cad = [LiveApi]::FindWindow('EngineFrame', '')
if ($cad -eq [IntPtr]::Zero) { Write-Output '未找到 CAD 主窗口'; exit 1 }
[void][LiveApi]::MoveWindow($cad, 20, 20, 1500, 900, $true)
$ok = [LiveApi]::ForceForeground($cad)
Write-Output ('置前台=' + $ok + ' 前台窗口=' + [LiveApi]::Title([LiveApi]::GetForegroundWindow()))
$r = [LiveApi]::Rect($cad)
Write-Output ('CAD rect=' + ($r -join ',') + ' title=' + [LiveApi]::Title($cad))
if (-not $ok) { Write-Output '警告：CAD 不在前台，真实点击会被跳过（防止误点其它窗口）' }

$origin = [LiveApi]::Cursor()
$i = 0
foreach ($a in ($Actions | ConvertFrom-Json)) {
    $i++
    switch ($a.t) {
        'click' {
            $fg = [LiveApi]::GetForegroundWindow()
            if ($fg -ne $cad -and [LiveApi]::PidOf($fg) -ne [LiveApi]::PidOf($cad)) {
                Write-Output ("  [{0}] 跳过点击：前台不是 CAD（是 {1}）" -f $i, [LiveApi]::Title($fg)); break
            }
            [LiveApi]::Click([int]$a.x, [int]$a.y)
            Write-Output ("  [{0}] click {1},{2}" -f $i, $a.x, $a.y)
        }
        'key' {
            $fg2 = [LiveApi]::GetForegroundWindow()
            if ($fg2 -ne $cad -and [LiveApi]::PidOf($fg2) -ne [LiveApi]::PidOf($cad)) {
                Write-Output ("  [{0}] 跳过按键：前台不是 CAD（是 {1}）" -f $i, [LiveApi]::Title($fg2)); break
            }
            [LiveApi]::Key([byte][int]$a.vk)
            Write-Output ("  [{0}] key vk={1}" -f $i, $a.vk)
        }
        'sleep' { Start-Sleep -Milliseconds ([int]$a.ms); Write-Output ("  [{0}] sleep {1}" -f $i, $a.ms) }
        'shot' {
            if (-not $a.path) { $a.path = (Join-Path $work ($Tag + '-' + $i + '.png')) }
            $rr = [LiveApi]::Rect($cad)
            Shot-Rect -Path $a.path -X $rr[0] -Y $rr[1] -W $rr[2] -H $rr[3]
            Write-Output ("  [{0}] shot -> {1}" -f $i, $a.path)
        }
    }
}
[LiveApi]::SetCursorPos($origin[0], $origin[1]) | Out-Null
Write-Output 'DONE'
