using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using TianGongCadSuite;
using A=SolidEdgeAssembly;
using F=SolidEdgeFramework;

// 0.10.2：「型材自动填充」在一个没测过的真实围栏上走窗口真实路径（只读，不生成）：
// 把顶层的每个实例都选上（= 用户框选整个围栏）→ 打开窗口（构造时读取当前选择）→ 报读到几根型材、跳过哪些、识别出几个框口、各平面没识别的原因、用时。
// 用法: PanelScanE2E.exe <输出目录> <装配副本>
class PanelScanE2E {
    static F.Application app;
    static void L(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    static void Pump(int n){ for (int i = 0; i < n; i++) { try { app.DoIdle(); } catch { } Application.DoEvents(); Thread.Sleep(60); } }
    static object Get(object o, string name){ return o.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).GetValue(o, null); }
    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        TianGongCadSuite.Licensing.LicenseTestHooks.GateOverride = 0;
        ScanFilter.Register();
        try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); } catch (Exception e) { L("连不上 CAD：" + e.Message); return 2; }
        try { app.DisplayAlerts = false; } catch { }
        A.AssemblyDocument asm = null; AutoPanelForm f = null;
        try {
            asm = (A.AssemblyDocument)app.Documents.Open(args[1]); Pump(20);
            asm.SelectSet.RemoveAll();
            int n = 0; foreach (A.Occurrence o in asm.Occurrences) { try { asm.SelectSet.Add(o); n++; } catch { } }
            L("选上顶层实例 " + n + " 个");
            var t0 = DateTime.Now;
            f = new AutoPanelForm(app, asm);
            double s = (DateTime.Now - t0).TotalSeconds;
            f.Show(); Pump(6);
            L("读取 + 识别用时 " + s.ToString("0.0") + "s");
            L("型材：" + Get(f, "MemberText"));
            L("状态：" + Get(f, "StatusText"));
            var texts = (string[])Get(f, "OpeningTexts");
            L("框口 " + texts.Length + " 个");
            foreach (var t in texts.Take(40)) L("   " + t);
            L("SCAN DONE");
        } catch (Exception e) { L("异常：" + e); return 1; }
        finally { try { if (f != null) f.Close(); } catch { } try { if (asm != null) asm.Close(false); } catch { } Pump(4); }
        return 0;
    }
}
[ComImport, Guid("00000016-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IScanMessageFilter {
    [PreserveSig] int HandleInComingCall(int type, IntPtr task, int ticks, IntPtr info);
    [PreserveSig] int RetryRejectedCall(IntPtr task, int ticks, int reject);
    [PreserveSig] int MessagePending(IntPtr task, int ticks, int pending);
}
sealed class ScanFilter : IScanMessageFilter {
    [DllImport("ole32.dll")] static extern int CoRegisterMessageFilter(IScanMessageFilter f, out IScanMessageFilter old);
    static ScanFilter keep;
    public static void Register(){ keep = new ScanFilter(); IScanMessageFilter old; CoRegisterMessageFilter(keep, out old); }
    public int HandleInComingCall(int a, IntPtr b, int c, IntPtr d){ return 0; }
    public int RetryRejectedCall(IntPtr a, int ticks, int reject){ return ticks < 120000 ? 200 : -1; }
    public int MessagePending(IntPtr a, int b, int c){ return 2; }
}
