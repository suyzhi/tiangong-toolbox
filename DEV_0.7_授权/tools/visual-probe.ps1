param([string]$OutDir)
$ErrorActionPreference='Continue'
Add-Type -AssemblyName System.Drawing
if (-not ('ShotApi' -as [type])) {
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
public class ShotApi {
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr hwnd, int x, int y, int w, int h, bool repaint);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }

    public static string[] Dump() {
        var list = new List<string>();
        EnumWindows((h, l) => {
            uint pid; GetWindowThreadProcessId(h, out pid);
            var t = new StringBuilder(512); GetWindowTextW(h, t, 512);
            var c = new StringBuilder(256); GetClassNameW(h, c, 256);
            RECT r; GetWindowRect(h, out r);
            list.Add(string.Format("{0}|pid={1}|vis={2}|icon={3}|rect={4},{5},{6}x{7}|cls={8}|title={9}",
                h, pid, IsWindowVisible(h), IsIconic(h), r.L, r.T, r.R-r.L, r.B-r.T, c, t));
            return true;
        }, IntPtr.Zero);
        return list.ToArray();
    }
    public static IntPtr FindByClass(string cls) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, l) => {
            var c = new StringBuilder(256); GetClassNameW(h, c, 256);
            if (c.ToString() == cls && IsWindowVisible(h)) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
    public static bool Shot(IntPtr hwnd, string path, uint flags) {
        RECT r; if (!GetWindowRect(hwnd, out r)) return false;
        int w = r.R - r.L, h = r.B - r.T;
        if (w <= 0 || h <= 0 || w > 8000 || h > 8000) return false;
        using (var bmp = new Bitmap(w, h))
        using (var g = Graphics.FromImage(bmp)) {
            IntPtr hdc = g.GetHdc();
            bool ok = PrintWindow(hwnd, hdc, flags);
            g.ReleaseHdc(hdc);
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            return ok;
        }
    }
}
'@ -Language CSharp
}
$dir = $OutDir
New-Item -Path $dir -ItemType Directory -Force | Out-Null
function Log($s){ Write-Output $s; Add-Content -Path (Join-Path $dir 'probe.log.txt') -Value $s -Encoding UTF8 }
Set-Content -Path (Join-Path $dir 'probe.log.txt') -Value ('PROBE START ' + (Get-Date -Format o)) -Encoding UTF8

$app = New-Object -ComObject SolidEdge.Application
$app.Visible = $true
$app.ScreenUpdating = $true
Log ("VERSION " + $app.Version)
$part = $app.Documents.Add('SolidEdge.PartDocument')
$part.ModelingMode = 1
Log ("DOC " + $part.Name)

$rp = $null
foreach($c in $part.RefPlanes){
  $n = New-Object 'double[]' 3; $p = New-Object 'double[]' 3; $u = New-Object 'double[]' 3
  $c.GetNormal([ref]$n); $c.GetRootPoint([ref]$p); $c.GetReferenceDirection([ref]$u)
  if([Math]::Abs($n[2]-1) -lt 1e-8 -and [Math]::Abs($p[0]) -lt 1e-8 -and [Math]::Abs($p[1]) -lt 1e-8 -and [Math]::Abs($u[0]-1) -lt 1e-8){ $rp = $c; break }
}
$prof = $part.ProfileSets.Add().Profiles.Add($rp)
$l0 = $prof.Lines2d.AddBy2Points(0,0,0.1,0); $l1 = $prof.Lines2d.AddBy2Points(0.1,0,0.1,0.1)
$l2 = $prof.Lines2d.AddBy2Points(0.1,0.1,0,0.1); $l3 = $prof.Lines2d.AddBy2Points(0,0.1,0,0)
$rel = $prof.Relations2d
$ls = @($l0,$l1,$l2,$l3)
for($i=0;$i -lt 4;$i++){ [void]$rel.AddKeypoint($ls[$i], 3, $ls[($i+1)%4], 2); if($i % 2 -eq 0){[void]$rel.AddHorizontal($ls[$i])} else {[void]$rel.AddVertical($ls[$i])} }
[void]$rel.AddKeypointFix($l0, 2)
$dims = $prof.Dimensions; $dims.Constraint = $true; [void]$dims.AddLength($l0); [void]$dims.AddLength($l1)
Log ("BASE PROFILE END " + $prof.End(1))
$arr = New-Object object[] 1; $arr[0] = $prof
$model = $part.Models.AddFiniteExtrudedProtrusion(1, [ref]$arr, 3, 0.01)

$M = [Type]::Missing
foreach($xy in @(@(0.03,0.03), @(0.07,0.03), @(0.03,0.07))){
  $plane = $part.RefPlanes.AddParallelByDistance($rp, 0.005, 7, $M, $M, $M, $M)
  $hp = $part.ProfileSets.Add().Profiles.Add($plane)
  $x2 = 0.0; $y2 = 0.0
  $hp.Convert3DCoordinate($xy[0], $xy[1], 0.005, [ref]$x2, [ref]$y2)
  [void]$hp.Holes2d.Add($x2, $y2)
  [void]$hp.End(1)
  $hd = $part.HoleDataCollection.Add(33, 0.004917, 0.0, 0.0, 0.0, 0.0, 0.0, 44, $M, $M, 0.004917, $M, $M, 145, $M, $M, $M, $M, 0.006, 'M6', $true)
  foreach($side in @(1,2)){
    try { $h = $model.Holes.AddThroughAll($hp, $side, $hd) } catch { continue }
    if($h -ne $null){ $desc=$null; $s=$h.GetStatusEx([ref]$desc); if([int]$s -eq 1216476310){ Log ("HOLE OK at " + $xy[0] + "," + $xy[1]); break } else { try{$h.Delete()}catch{} } }
  }
}
# 第四个孔：V 底盲孔，用来看钻尖锥面
$plane = $part.RefPlanes.AddParallelByDistance($rp, 0.005, 7, $M, $M, $M, $M)
$hp = $part.ProfileSets.Add().Profiles.Add($plane)
$hp.Convert3DCoordinate(0.07, 0.07, 0.005, [ref]$x2, [ref]$y2)
[void]$hp.Holes2d.Add($x2, $y2); [void]$hp.End(1)
$hd2 = $part.HoleDataCollection.Add(33, 0.004917, 0.0, 0.0, 0.0, 0.0, 118.0, 44, $M, $M, 0.004917, $M, $M, 146, $M, $M, $M, $M, 0.006, 'M6', $true)
foreach($side in @(1,2)){ try { $h = $model.Holes.AddFinite($hp, $side, 0.006, $hd2) } catch { continue }
  if($h -ne $null){ $desc=$null; $s=$h.GetStatusEx([ref]$desc); if([int]$s -eq 1216476310){ Log 'V-BOTTOM HOLE OK'; break } else { try{$h.Delete()}catch{} } } }

# 视图：等轴测
try { $app.DoIdle() } catch {}
try { $w = $app.ActiveWindow; $w.View.Fit() } catch { Log ('FIT FAILED ' + $_.Exception.Message) }
try { $w.View.SetIsometric() } catch { Log ('ISO FAILED ' + $_.Exception.Message) }
try { $w.View.Fit() } catch {}
try { $app.DoIdle() } catch {}
Start-Sleep -Milliseconds 1500
try { $app.DoIdle() } catch {}

try { $w.View.SaveAsImage((Join-Path $dir 'a-saveasimage.png'), 1200, 900); Log 'SAVEASIMAGE ok' } catch { Log ('SAVEASIMAGE FAILED ' + $_.Exception.Message) }

Log '--- WINDOWS ---'
foreach($line in [ShotApi]::Dump()){ Log $line }

$hwnd = [IntPtr]$app.ActiveWindowHWND
Log ("ActiveWindowHWND " + $hwnd)
if($hwnd -ne [IntPtr]::Zero){
  [void][ShotApi]::ShowWindow($hwnd, 3)     # SW_MAXIMIZE
  [void][ShotApi]::SetForegroundWindow($hwnd)
  Start-Sleep -Milliseconds 800
  try { $app.DoIdle() } catch {}
  Log ("PRINTWINDOW 0 -> " + [ShotApi]::Shot($hwnd, (Join-Path $dir 'b-printwindow0.png'), 0))
  Log ("PRINTWINDOW 2 -> " + [ShotApi]::Shot($hwnd, (Join-Path $dir 'c-printwindow2.png'), 2))
}
Log 'PROBE DONE'
$part.SaveAs((Join-Path $dir 'ConeDemo.par'))
try { $part.Close($false) } catch {}
try { $app.Quit() } catch {}
