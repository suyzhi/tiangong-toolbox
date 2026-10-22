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

// 0.7.12：原位编辑留下的子文档窗口能不能关、关了改动丢不丢、会不会弹框；子文档自己开着窗口时原位编辑会怎样、关掉窗口能不能恢复。不保存。
// 用法: WindowCloseProbe.exe <输出目录> <T 装配> <目标零件文件名关键字>
class WindowCloseProbe {
    static F.Application app;
    static void L(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    static void Pump(int n){ for (int i = 0; i < n; i++) { try { app.DoIdle(); } catch { } try { Application.DoEvents(); } catch { } Thread.Sleep(60); } }
    static string N(object d){ try { return (string)((dynamic)d).Name; } catch { return "?"; } }
    static List<object> Wins(){ var l = new List<object>(); try { foreach (object w in app.Windows) l.Add(w); } catch { } return l; }
    static string Cap(object w){ try { return (string)((dynamic)w).Caption; } catch { return "?"; } }
    static int H(object w){ try { return (int)((dynamic)w).hWnd; } catch { return 0; } }
    static string WinList(){ var l = new List<string>(); foreach (var w in Wins()) l.Add(Cap(w)); return l.Count + "［" + string.Join("｜", l.ToArray()) + "］"; }
    static OccChain Find(A.AssemblyDocument top, string want){
        var res = new List<OccChain>(); Walk(top, new List<A.Occurrence>(), want, res, 0); return res.Count > 0 ? res[0] : null;
    }
    static void Walk(A.AssemblyDocument doc, List<A.Occurrence> path, string want, List<OccChain> res, int depth){
        if (depth > 8 || res.Count > 0) return;
        for (int i = 1; i <= doc.Occurrences.Count; i++) {
            var o = doc.Occurrences.Item(i); var p = new List<A.Occurrence>(path); p.Add(o);
            bool sub = false; try { sub = o.Subassembly; } catch { }
            if (sub) { var d = o.OccurrenceDocument as A.AssemblyDocument; if (d != null) Walk(d, p, want, res, depth + 1); }
            else if ((o.OccurrenceFileName ?? "").IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0) { var c = new OccChain(); c.Items.AddRange(p); res.Add(c); return; }
        }
    }
    static int Planes(P.PartDocument p){ try { return p.RefPlanes.Count; } catch { return -1; } }
    static string TopOf(P.PartDocument p){ try { object t = null, o = null; p.GetTopDocumentAndSubOccurrenceOfIPADoc(out t, out o); return N(t); } catch (Exception e) { return "?" + e.Message; } }

    // 进原位 → 加一个基准面（零件变脏）→ 退出 → 关掉新多出来的窗口
    static void Round(A.AssemblyDocument t, OccChain chain, P.PartDocument part, string tag, bool close){
        var before = new HashSet<int>(); foreach (var w in Wins()) before.Add(H(w));
        string why; var t0 = DateTime.Now;
        var s = NativeHoleLink.Enter(app, t, chain, out why);
        L(tag + " 进原位：" + (s == null ? "失败 " + why : "成功，天工说顶层是 " + TopOf(s.Part)) + "　" + (DateTime.Now - t0).TotalSeconds.ToString("0.0") + "s　窗口 " + WinList());
        if (s == null) return;
        int p0 = Planes(s.Part);
        try { s.Part.RefPlanes.AddParallelByDistance(s.Part.RefPlanes.Item(1), 0.01 + p0 * 0.001, P.ReferenceElementConstants.igNormalSide); L(tag + " 加基准面 " + p0 + " → " + Planes(s.Part)); }
        catch (Exception e) { L(tag + " 加基准面失败 0x" + e.HResult.ToString("X8") + " " + e.Message); }
        NativeHoleLink.Leave(app, s); Pump(10);
        L(tag + " 退出后：活动 " + N(app.ActiveDocument) + "　窗口 " + WinList() + "　零件脏=" + Dirty(part));
        if (!close) return;
        foreach (var w in Wins()) {
            if (before.Contains(H(w))) continue;
            var c0 = DateTime.Now; string cap = Cap(w);
            try { ((dynamic)w).Close(); L(tag + " 关窗口「" + cap + "」用时 " + (DateTime.Now - c0).TotalSeconds.ToString("0.0") + "s"); }
            catch (Exception e) { L(tag + " 关窗口「" + cap + "」失败 0x" + e.HResult.ToString("X8") + " " + e.Message); }
        }
        Pump(10);
        L(tag + " 关完：活动 " + N(app.ActiveDocument) + "　窗口 " + WinList() + "　零件基准面 " + Planes(part) + "　零件脏=" + Dirty(part));
    }
    static string Dirty(P.PartDocument p){ try { return p.Dirty.ToString(); } catch (Exception e) { return "?" + e.Message; } }

    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        WcFilter.Register();
        try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); } catch (Exception e) { L("连不上 CAD：" + e.Message); return 2; }
        try { app.DisplayAlerts = false; } catch { }
        A.AssemblyDocument t = null;
        try {
            t = (A.AssemblyDocument)app.Documents.Open(args[1]); Pump(20);
            var chain = Find(t, args[2]);
            if (chain == null) { L("找不到目标零件"); return 3; }
            var part = (P.PartDocument)chain.Leaf.OccurrenceDocument;
            L("目标 " + N(part) + "　实例链 " + chain.Items.Count + " 级　基准面 " + Planes(part) + "　窗口 " + WinList());
            Round(t, chain, part, "R1", true);
            // 关掉窗口后从 T 重新取零件：R1 加的基准面还在不在（在 = 只是旧的文档对象作废；不在 = 改动丢了）
            chain = Find(t, args[2]);
            part = (P.PartDocument)chain.Leaf.OccurrenceDocument;
            L("R1 后重新取零件：基准面 " + Planes(part) + "（R1 前 3、R1 加了 1）　零件脏=" + Dirty(part) + "　窗口 " + WinList());
            Round(t, chain, part, "R2", true);
            chain = Find(t, args[2]); part = (P.PartDocument)chain.Leaf.OccurrenceDocument;
            L("R2 后重新取零件：基准面 " + Planes(part) + "　零件脏=" + Dirty(part));
            chain = Find(t, args[2]); part = (P.PartDocument)chain.Leaf.OccurrenceDocument;
            Round(t, chain, part, "R3（不关窗口）", false);
            chain = Find(t, args[2]); part = (P.PartDocument)chain.Leaf.OccurrenceDocument;
            Round(t, chain, part, "R4（上一轮的窗口还在）", true);
            chain = Find(t, args[2]); part = (P.PartDocument)chain.Leaf.OccurrenceDocument;
            // 零件自己开一个窗口（像用户双击打开零件那样），再在装配里原位编辑它
            ((dynamic)t).Activate(); Pump(10);
            var own = app.Documents.Open(part.FullName); Pump(20);
            L("R5 零件自己开了窗口：活动 " + N(app.ActiveDocument) + "　窗口 " + WinList());
            ((dynamic)t).Activate(); Pump(20);
            Round(t, chain, part, "R5（零件有自己的窗口）", false);
            // 关掉零件自己的窗口，再来一轮，看能不能恢复
            foreach (var w in Wins()) { if (Cap(w) == N(part)) { try { ((dynamic)w).Close(); L("R6 关掉零件自己的窗口"); } catch (Exception e) { L("R6 关零件窗口失败 " + e.Message); } } }
            Pump(10);
            chain = Find(t, args[2]); part = (P.PartDocument)chain.Leaf.OccurrenceDocument;
            Round(t, chain, part, "R6（关掉零件窗口后）", true);
        } catch (Exception e) { L("异常 " + e.Message + "\r\n" + e.StackTrace); }
        finally { try { if (t != null) t.Close(false); } catch { } }
        L("WINDOW CLOSE PROBE DONE");
        return 0;
    }
}
[ComImport, Guid("00000016-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IWcMessageFilter {
    [PreserveSig] int HandleInComingCall(int type, IntPtr task, int ticks, IntPtr info);
    [PreserveSig] int RetryRejectedCall(IntPtr task, int ticks, int reject);
    [PreserveSig] int MessagePending(IntPtr task, int ticks, int pending);
}
sealed class WcFilter : IWcMessageFilter {
    [DllImport("ole32.dll")] static extern int CoRegisterMessageFilter(IWcMessageFilter f, out IWcMessageFilter old);
    static WcFilter keep;
    public static void Register(){ keep = new WcFilter(); IWcMessageFilter old; CoRegisterMessageFilter(keep, out old); }
    public int HandleInComingCall(int a, IntPtr b, int c, IntPtr d){ return 0; }
    public int RetryRejectedCall(IntPtr a, int ticks, int reject){ return ticks < 120000 ? 200 : -1; }
    public int MessagePending(IntPtr a, int b, int c){ return 2; }
}
