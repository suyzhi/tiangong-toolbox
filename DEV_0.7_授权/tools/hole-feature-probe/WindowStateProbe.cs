using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using TianGongCadSuite;
using A=SolidEdgeAssembly;
using P=SolidEdgePart;
using F=SolidEdgeFramework;

// 0.7.12：原位编辑以后 app.Windows 里多出来的窗口是什么、怎么认"这个文档在别的窗口里开着"。只读，不保存。
// 用法: WindowStateProbe.exe <输出目录> <T 装配> <X 装配（含 T）> <目标零件文件名关键字>
class WindowStateProbe {
    static F.Application app;
    static void L(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    static void Pump(int n){ for (int i = 0; i < n; i++) { try { app.DoIdle(); } catch { } try { Application.DoEvents(); } catch { } Thread.Sleep(60); } }
    static string N(object d){ try { return (string)((dynamic)d).Name; } catch { return "?"; } }
    static void Dump(string title){
        L("== " + title + "　活动文档 " + N(app.ActiveDocument));
        try { foreach (object d in app.Documents) {
            string ip = "?"; try { ip = ((bool)((dynamic)d).InPlaceActivated).ToString(); } catch { }
            int wc = -1; try { wc = ((dynamic)d).Windows.Count; } catch { }
            L("   文档 " + N(d) + "　InPlaceActivated=" + ip + "　自己的窗口数=" + wc);
        } } catch (Exception e) { L("   Documents 读不出 " + e.Message); }
        try { foreach (object w in app.Windows) {
            dynamic x = w; string doc = "?", vis = "?", typ = "?", st = "?", h = "?";
            try { doc = N(x.Parent); } catch { }
            try { vis = x.Visible.ToString(); } catch { }
            try { typ = x.Type.ToString(); } catch { }
            try { st = x.WindowState.ToString(); } catch { }
            try { h = x.hWnd.ToString(); } catch { }
            L("   窗口 " + x.Caption + "　Parent=" + doc + "　Visible=" + vis + "　Type=" + typ + "　State=" + st + "　hWnd=" + h);
        } } catch (Exception e) { L("   Windows 读不出 " + e.Message); }
    }
    static OccChain Find(A.AssemblyDocument top, string want){
        var res = new List<OccChain>();
        Walk(top, new List<A.Occurrence>(), want, res, 0);
        return res.Count > 0 ? res[0] : null;
    }
    static void Walk(A.AssemblyDocument doc, List<A.Occurrence> path, string want, List<OccChain> res, int depth){
        if (depth > 8 || res.Count > 0) return;
        for (int i = 1; i <= doc.Occurrences.Count; i++) {
            var o = doc.Occurrences.Item(i);
            var p = new List<A.Occurrence>(path); p.Add(o);
            bool sub = false; try { sub = o.Subassembly; } catch { }
            if (sub) { var d = o.OccurrenceDocument as A.AssemblyDocument; if (d != null) Walk(d, p, want, res, depth + 1); }
            else if ((o.OccurrenceFileName ?? "").IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0) { var c = new OccChain(); c.Items.AddRange(p); res.Add(c); return; }
        }
    }
    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        WsFilter.Register();
        try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); } catch (Exception e) { L("连不上 CAD：" + e.Message); return 2; }
        try { app.DisplayAlerts = false; } catch { }
        A.AssemblyDocument t = null, x = null;
        try {
            t = (A.AssemblyDocument)app.Documents.Open(args[1]); Pump(20);
            Dump("只开 T");
            var chain = Find(t, args[3]);
            if (chain == null) { L("找不到目标零件"); return 3; }
            string why;
            for (int round = 1; round <= 2; round++) {
                var s = NativeHoleLink.Enter(app, t, chain, out why);
                L("第 " + round + " 次进原位：" + (s == null ? "失败 " + why : "成功 " + N(s.Part)));
                Dump("第 " + round + " 次原位编辑中");
                if (s != null) NativeHoleLink.Leave(app, s);
                Pump(10);
                Dump("第 " + round + " 次退出原位后");
            }
            x = (A.AssemblyDocument)app.Documents.Open(args[2]); Pump(20);
            Dump("又开了 X（含 T）");
            ((dynamic)t).Activate(); Pump(20);
            Dump("切回 T");
        } catch (Exception e) { L("异常 " + e.Message + "\r\n" + e.StackTrace); }
        finally { try { if (x != null) x.Close(false); } catch { } try { if (t != null) t.Close(false); } catch { } }
        L("WINDOW PROBE DONE");
        return 0;
    }
}
[ComImport, Guid("00000016-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IWsMessageFilter {
    [PreserveSig] int HandleInComingCall(int type, IntPtr task, int ticks, IntPtr info);
    [PreserveSig] int RetryRejectedCall(IntPtr task, int ticks, int reject);
    [PreserveSig] int MessagePending(IntPtr task, int ticks, int pending);
}
sealed class WsFilter : IWsMessageFilter {
    [DllImport("ole32.dll")] static extern int CoRegisterMessageFilter(IWsMessageFilter f, out IWsMessageFilter old);
    static WsFilter keep;
    public static void Register(){ keep = new WsFilter(); IWsMessageFilter old; CoRegisterMessageFilter(keep, out old); }
    public int HandleInComingCall(int a, IntPtr b, int c, IntPtr d){ return 0; }
    public int RetryRejectedCall(IntPtr a, int ticks, int reject){ return ticks < 120000 ? 200 : -1; }
    public int MessagePending(IntPtr a, int b, int c){ return 2; }
}
