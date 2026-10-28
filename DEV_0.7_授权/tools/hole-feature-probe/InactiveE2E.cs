using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using TianGongCadSuite;
using A=SolidEdgeAssembly;
using F=SolidEdgeFramework;

// 0.10.2：大装配常把零件设成"不激活"（Activate=false，只显示、不加载）。配孔检查读零件时这些零件怎么样：
// 读得到实体吗、会不会被说成"文件打不开"。在副本上把前几个顶层零件改成不激活（不保存），跑一遍 AssemblyPart.List。
// 用法: InactiveE2E.exe <输出目录> <装配副本>
class InactiveE2E {
    static F.Application app;
    static void L(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    static T Try<T>(Func<T> f, T d){ try { return f(); } catch { return d; } }
    static void Pump(int n){ for (int i = 0; i < n; i++) { try { app.DoIdle(); } catch { } Application.DoEvents(); Thread.Sleep(50); } }
    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        TianGongCadSuite.Licensing.LicenseTestHooks.GateOverride = 0;
        InactiveFilter.Register();
        try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); } catch (Exception e) { L("连不上 CAD：" + e.Message); return 2; }
        try { app.DisplayAlerts = false; } catch { }
        A.AssemblyDocument asm = null;
        try {
            asm = (A.AssemblyDocument)app.Documents.Open(args[1]); Pump(20);
            var w0 = new List<string>();
            int before = AssemblyPart.List(asm, w0).Count;
            L("全部激活：零件 " + before + "，提示 " + w0.Count);
            var changed = new List<string>();
            foreach (A.Occurrence o in asm.Occurrences) {
                if (changed.Count >= 5) break;
                if (Try(() => o.Subassembly, true)) continue;
                try { o.Activate = false; changed.Add(o.Name); } catch (Exception e) { L("   设不了不激活 " + Try(() => o.Name, "?") + "：" + e.Message); }
            }
            Pump(20);
            foreach (A.Occurrence o in asm.Occurrences) if (changed.Contains(Try(() => o.Name, ""))) {
                object d = Try<object>(() => o.OccurrenceDocument, null);
                L("   " + o.Name + "：Activate=" + Try(() => o.Activate.ToString(), "?") + " 文档=" + (d == null ? "null" : "有"));
            }
            var w1 = new List<string>();
            var parts = AssemblyPart.List(asm, w1);
            L("不激活 " + changed.Count + " 个以后：零件 " + parts.Count + "，提示 " + w1.Count);
            foreach (var w in w1.Take(10)) L("   提示：" + w);
            L("RESULT before=" + before + " after=" + parts.Count + " warnings=" + w1.Count);
        } catch (Exception e) { L("异常：" + e); return 1; }
        finally { try { if (asm != null) asm.Close(false); } catch { } Pump(4); }
        return 0;
    }
}
[ComImport, Guid("00000016-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IInactiveFilter {
    [PreserveSig] int HandleInComingCall(int type, IntPtr task, int ticks, IntPtr info);
    [PreserveSig] int RetryRejectedCall(IntPtr task, int ticks, int reject);
    [PreserveSig] int MessagePending(IntPtr task, int ticks, int pending);
}
sealed class InactiveFilter : IInactiveFilter {
    [DllImport("ole32.dll")] static extern int CoRegisterMessageFilter(IInactiveFilter f, out IInactiveFilter old);
    static InactiveFilter keep;
    public static void Register(){ keep = new InactiveFilter(); IInactiveFilter old; CoRegisterMessageFilter(keep, out old); }
    public int HandleInComingCall(int a, IntPtr b, int c, IntPtr d){ return 0; }
    public int RetryRejectedCall(IntPtr a, int ticks, int reject){ return ticks < 120000 ? 200 : -1; }
    public int MessagePending(IntPtr a, int b, int c){ return 2; }
}
