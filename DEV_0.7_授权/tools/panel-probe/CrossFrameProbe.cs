using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using TianGongCadSuite;
using A=SolidEdgeAssembly;
using P=SolidEdgePart;
using F=SolidEdgeFramework;

// 跨子装配型材填充的排查探针（2026-10-15）：只读，不改模型。
// 围栏是四个子装配环起来的，某一面的立柱属于相邻子装配 —— 看现有识别在这种装配上到底卡在哪。
// 用法：CrossFrameProbe.exe <输出目录> <装配路径> [厚度mm=5] [间隙mm=0]
static class CrossFrameProbe {
    static F.Application app;
    static void L(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    static string N(double v){ return v.ToString("0.###", CultureInfo.InvariantCulture); }
    static string Name(object o){ return o is A.Occurrence ? ((A.Occurrence)o).Name : ((A.SubOccurrence)o).Name; }
    static bool IsAsm(object o){ return o is A.Occurrence ? ((A.Occurrence)o).Subassembly : ((A.SubOccurrence)o).Subassembly; }
    static string File(object o){
        try { object d = o is A.Occurrence ? (object)((A.Occurrence)o).OccurrenceDocument : (object)((A.SubOccurrence)o).SubOccurrenceDocument;
              return d == null ? "" : Path.GetFileName((string)((dynamic)d).FullName); } catch { return "?"; }
    }
    static IEnumerable<object> Children(object o){
        var list = new List<object>();
        if (o is A.Occurrence) foreach (A.SubOccurrence c in ((A.Occurrence)o).SubOccurrences) list.Add(c);
        else foreach (A.SubOccurrence c in ((A.SubOccurrence)o).SubOccurrences) list.Add(c);
        return list;
    }
    static void Leaves(object o, List<object> into){ if (IsAsm(o)) foreach (var c in Children(o)) Leaves(c, into); else into.Add(o); }
    static string Box(FrameMember m){
        return "X " + N(m.Points.Min(p => p.X) * 1000) + ".." + N(m.Points.Max(p => p.X) * 1000)
            + "  Y " + N(m.Points.Min(p => p.Y) * 1000) + ".." + N(m.Points.Max(p => p.Y) * 1000)
            + "  Z " + N(m.Points.Min(p => p.Z) * 1000) + ".." + N(m.Points.Max(p => p.Z) * 1000);
    }
    static void TryDetect(string label, List<FrameMember> members, double t, double g){
        L("=== " + label + "：" + members.Count + " 根 ===");
        var notes = new List<string>();
        try {
            var found = FrameGrouping.Detect(members, t, g, notes);
            foreach (var f in found) {
                var s = f.Opening.Solve(t, g);
                var c = s.Corner(0, 0) + s.U * (s.Width / 2) + s.V * (s.Height / 2);
                L("  框口 " + f.PlaneLabel + "  " + N(s.Width * 1000) + " × " + N(s.Height * 1000) + "  中心 (" + N(c.X * 1000) + ", " + N(c.Y * 1000) + ", " + N(c.Z * 1000) + ")");
            }
        } catch (Exception e) { L("  识别失败：" + e.Message.Replace("\n", " | ")); }
        foreach (var n in notes) L("  说明：" + n);
    }

    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (args.Length < 2) { L("用法: CrossFrameProbe.exe <输出目录> <装配路径> [厚度mm] [间隙mm]"); return 1; }
        double t = (args.Length > 2 ? double.Parse(args[2], CultureInfo.InvariantCulture) : 5) / 1000;
        double g = (args.Length > 3 ? double.Parse(args[3], CultureInfo.InvariantCulture) : 0) / 1000;
        for (int i = 0; i < 60 && app == null; i++) { try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); } catch { Thread.Sleep(1000); } }
        if (app == null) { L("FATAL 连不上 CAD"); return 2; }
        try { app.Visible = true; app.DisplayAlerts = false; } catch { }
        A.AssemblyDocument asm = null;
        for (int i = 0; i < 8 && asm == null; i++) { try { asm = (A.AssemblyDocument)app.Documents.Open(Path.GetFullPath(args[1])); } catch (Exception e) { L("打开重试 " + e.Message); Thread.Sleep(3000); } }
        for (int i = 0; i < 20; i++) { try { app.DoIdle(); } catch { } Thread.Sleep(300); }
        L("装配 " + asm.FullName + "  顶层实例 " + asm.Occurrences.Count);

        var tops = asm.Occurrences.Cast<A.Occurrence>().ToList();
        var perTop = new Dictionary<string, List<FrameMember>>();
        foreach (var top in tops) {
            var leaves = new List<object>(); Leaves(top, leaves);
            L("-- " + top.Name + "  " + File(top) + "  子装配=" + top.Subassembly + "  叶子 " + leaves.Count);
            var members = new List<FrameMember>();
            foreach (var leaf in leaves) {
                try {
                    // FrameReader.Read 要至少 4 根，单根读时借它的私有逻辑：一次读一根 + 3 根凑数不现实，所以读失败时只报原因
                    var one = FrameReaderOne(asm, leaf);
                    members.Add(one);
                    L("     " + Name(leaf) + "  [" + File(leaf) + "]  轴 " + one.Axis + "  " + Box(one));
                } catch (Exception e) { L("     " + Name(leaf) + "  [" + File(leaf) + "]  读不了：" + e.Message); }
            }
            perTop[top.Name] = members;
        }
        foreach (var kv in perTop) if (kv.Value.Count >= 4) TryDetect("单独 " + kv.Key, kv.Value, t, g);
        var all = perTop.Values.SelectMany(x => x).ToList();
        TryDetect("全部型材（不含填充板）", all.Where(m => !m.Name.StartsWith("填充板") && !m.Name.StartsWith("玻璃板")).ToList(), t, g);
        TryDetect("全部（含已有板子）", all, t, g);
        return 0;
    }

    // 读一根（型材或板子都返回；读不了的抛出原因）
    static FrameMember FrameReaderOne(A.AssemblyDocument asm, object leaf){
        var r = FrameReader.ReadAll(asm, new[]{ leaf });
        if (r.Members.Count + r.Plates.Count == 0) throw new ArgumentException("不是直型材（详见 panel.log 的 FrameReaderSkip）");
        return r.Members.Count > 0 ? r.Members[0] : r.Plates[0];
    }
}
