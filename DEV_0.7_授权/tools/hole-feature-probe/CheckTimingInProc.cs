using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using F = SolidEdgeFramework;

// 第 4 轮：配孔检查进程内计时。让插件（开发构建，Diagnostics.StartCheckTiming）在 CAD 自己的线程里打开装配、
// 按窗口同一条路径跑一遍检查，分阶段（列零件 / 读孔 / 射线表 / 配对）、按零件（最慢的 15 个）报时间。
// 进程外跑（CheckCompare）每个 COM 调用都跨进程，时间会被放大很多倍 —— 用户点「开始检查」时是这里量到的速度。
// 用法: CheckTimingInProc.exe <输出目录> <装配副本> [超时分钟]
class CheckTimingInProc {
    static void L(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        F.Application app;
        try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); }
        catch (Exception e) { L("连不上 CAD：" + e.Message); return 2; }
        try { app.DisplayAlerts = false; } catch { }
        dynamic diag = null;
        for (int i = 0; i < 60 && diag == null; i++) {
            try {
                if (i == 0) app.Documents.Add("SolidEdge.AssemblyDocument");
                foreach (F.AddIn a in app.AddIns) if (string.Equals(a.GUID, "{8C05165C-65A4-4EF2-A138-508589D82004}", StringComparison.OrdinalIgnoreCase)) { a.Connect = true; diag = a.Object; }
            } catch { }
            if (diag == null) Thread.Sleep(1000);
        }
        if (diag == null) { L("插件没有给出 Diagnostics（注册的不是开发构建？）"); return 2; }
        L("LIBRARY " + (string)diag.LoadedLibrary);
        int minutes = args.Length > 2 ? int.Parse(args[2]) : 60;
        var t0 = DateTime.Now;
        diag.StartCheckTiming(Path.GetFullPath(args[1]));
        var end = DateTime.Now.AddMinutes(minutes);
        string r = "";
        while (DateTime.Now < end) {
            Thread.Sleep(2000);
            try { r = (string)diag.Result; } catch { continue; }   // CAD 正忙（检查跑在它的线程上）时调用会被拒，接着等
            if (!string.IsNullOrEmpty(r)) break;
        }
        L(string.IsNullOrEmpty(r) ? "TIMEOUT after " + minutes + " min" : r);
        L("wall " + (DateTime.Now - t0).TotalSeconds.ToString("0") + "s");
        L("TIMING DONE");
        return 0;
    }
}
