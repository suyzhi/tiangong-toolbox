using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using TianGongCadSuite;
using A=SolidEdgeAssembly;
using F=SolidEdgeFramework;

// 0.7.15：整套配孔检查（和窗口同一条路径：CollectAssemblyHoles → OccurrenceBodies → HoleCheck.Run），只读不改。
// 用旧 / 新两个开发构建各编一份，在同一个装配副本上跑，比较多实体零件读全以后多出来的报告是真问题还是误报。
// 用法: CheckCompare.exe <输出目录> <装配副本>
class CheckCompare {
    static void L(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        TianGongCadSuite.Licensing.LicenseTestHooks.GateOverride = 0;
        CmpFilter.Register();
        F.Application app;
        try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); }
        catch (Exception e) { L("连不上 CAD：" + e.Message); return 2; }
        try { app.DisplayAlerts = false; } catch { }
        L("插件 DLL " + typeof(AutoHoleWriter).Assembly.GetName().Version + "　" + typeof(AutoHoleWriter).Assembly.Location);
        A.AssemblyDocument asm = null;
        try {
            asm = (A.AssemblyDocument)app.Documents.Open(Path.GetFullPath(args[1]));
            for (int i = 0; i < 30; i++) { try { app.DoIdle(); } catch { } System.Threading.Thread.Sleep(60); }
            var t0 = DateTime.Now;
            List<string> warnings; List<HoleRecord> shafts; List<AssemblyPart> parts;
            var holes = AutoHoleWriter.CollectAssemblyHoles(asm, out warnings, out shafts, out parts);
            var bodies = AutoHoleWriter.OccurrenceBodies.Build(parts);
            var names = parts.Select(p => p.Name).Distinct().ToList();
            var issues = HoleCheck.Run(holes, null, true, names, bodies, shafts);
            try { typeof(AutoHoleCheckForm).GetMethod("MarkFailedFeatures").Invoke(null, new object[]{ issues, parts }); } catch { }   // 0.10.1 起窗口里会标"孔特征出错"，旧构建没有这个方法
            L("用时 " + (DateTime.Now - t0).TotalSeconds.ToString("0.0") + "s　零件 " + parts.Count + "　孔 " + holes.Count + "　凸台 " + shafts.Count + "　警告 " + warnings.Count);
            foreach (var g in issues.GroupBy(i => i.KindName)) L("  " + g.Key + "：" + g.Count());
            foreach (var it in issues.Where(i => i.Kind != HoleIssueKind.Unpaired).OrderBy(i => i.Message))
                L("  ISSUE " + it.KindName + "｜" + it.Message + (it.MissingPart.Length > 0 ? "｜缺：" + it.MissingPart : "")
                  + "｜" + string.Join(",", it.Holes.Select(h => h.PartName + " Φ" + h.DiameterMm.ToString("0.##"))));
            L("CHECK DONE");
        } catch (Exception e) { L("异常：" + e); return 1; }
        finally { try { if (asm != null) asm.Close(false); } catch { } }
        return 0;
    }
}

[ComImport, Guid("00000016-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface ICmpMessageFilter {
    [PreserveSig] int HandleInComingCall(int type, IntPtr task, int ticks, IntPtr info);
    [PreserveSig] int RetryRejectedCall(IntPtr task, int ticks, int reject);
    [PreserveSig] int MessagePending(IntPtr task, int ticks, int pending);
}
sealed class CmpFilter : ICmpMessageFilter {
    [DllImport("ole32.dll")] static extern int CoRegisterMessageFilter(ICmpMessageFilter f, out ICmpMessageFilter old);
    static CmpFilter keep;
    public static void Register(){ keep = new CmpFilter(); ICmpMessageFilter old; CoRegisterMessageFilter(keep, out old); }
    public int HandleInComingCall(int a, IntPtr b, int c, IntPtr d){ return 0; }
    public int RetryRejectedCall(IntPtr a, int ticks, int reject){ return ticks < 120000 ? 200 : -1; }
    public int MessagePending(IntPtr a, int b, int c){ return 2; }
}
