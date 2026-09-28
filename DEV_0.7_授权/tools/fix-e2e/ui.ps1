# tools/fix-e2e/ui.ps1 —— 私有桌面/任意桌面通用的 Win32 UI 小工具（每个 pwsh 进程独立，需 dot-source）。
$ErrorActionPreference='Continue'
if (-not ('TgUiApi' -as [type])) {
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public class TgUiApi {
  [DllImport("user32.dll", CharSet=CharSet.Unicode, EntryPoint="SendMessageW")] public static extern IntPtr Send(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode, EntryPoint="SendMessageW")] static extern IntPtr SendStr(IntPtr h, uint m, IntPtr w, StringBuilder l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode, EntryPoint="GetClassNameW")] static extern int GetClassNameX(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr h, EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
  [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
  delegate bool EnumProc(IntPtr h, IntPtr l);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  public static void Click(IntPtr h) { Send(h, 0x00F5, IntPtr.Zero, IntPtr.Zero); }        // BM_CLICK
  public static string GetText(IntPtr h) { var sb = new StringBuilder(65536); SendStr(h, 0x000D, new IntPtr(65536), sb); return sb.ToString(); }
  public static int ListCount(IntPtr h) { return (int)Send(h, 0x018B, IntPtr.Zero, IntPtr.Zero); }  // LB_GETCOUNT
  public static string ListItem(IntPtr h, int i) { var sb = new StringBuilder(1024); SendStr(h, 0x0189, new IntPtr(i), sb); return sb.ToString(); } // LB_GETTEXT
  public static bool Alive(IntPtr h) { return IsWindow(h) && IsWindowVisible(h); }
  public static int[] Rect(IntPtr h) { RECT r; GetWindowRect(h, out r); return new int[] { r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top }; }
  public static List<string> Kids(IntPtr root) {
    var res = new List<string>();
    EnumChildWindows(root, (h,l) => {
      var t = new StringBuilder(512); GetWindowTextW(h, t, 512);
      var c = new StringBuilder(256); GetClassNameX(h, c, 256);
      RECT r; GetWindowRect(h, out r);
      res.Add("hwnd=" + h + " cls=" + c.ToString() + " rect=" + r.Left + "," + r.Top + "," + (r.Right-r.Left) + "x" + (r.Bottom-r.Top) + " txt=[" + t.ToString() + "]");
      return true;
    }, IntPtr.Zero);
    return res;
  }
  public static string Find(IntPtr root, string textSub, string clsSub) {
    string found = "";
    EnumChildWindows(root, (h,l) => {
      if (found.Length > 0) return false;
      var t = new StringBuilder(512); GetWindowTextW(h, t, 512);
      var c = new StringBuilder(256); GetClassNameX(h, c, 256);
      bool okT = (textSub == null || t.ToString().Contains(textSub));
      bool okC = (clsSub == null || c.ToString().Contains(clsSub));
      if (okT && okC) found = h.ToString();
      return true;
    }, IntPtr.Zero);
    return found;
  }
}
'@ -Language CSharp
}
function Find-Ctl { param([IntPtr]$Root, [string]$Text, [string]$Cls, [switch]$AnyMatch)
  # 返回匹配控件句柄（默认要求文本完全相等；-AnyMatch 子串）
  $kids = [TgUiApi]::Kids($Root)
  foreach($k in $kids){
    if($k -match 'cls=(\S+) .*txt=\[(.*)\]$'){
      $c=$Matches[1]; $t=$Matches[2]
      if($Cls -and ($c -notlike ("*"+$Cls+"*"))){continue}
      if($Text -ne $null -and $Text -ne ''){
        if($AnyMatch){ if($t -notlike ("*"+$Text+"*")){continue} } else { if($t -ne $Text){continue} }
      }
      if($k -match 'hwnd=(\d+)'){ return [IntPtr][int64]$Matches[1] }
    }
  }
  return [IntPtr]::Zero
}
