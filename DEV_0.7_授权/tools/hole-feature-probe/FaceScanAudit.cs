using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using TianGongCadSuite;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;
using F=SolidEdgeFramework;

// 0.9.1：「点面认孔有时认不全」—— 逐张平面比对 HoleScanCad.ScanFace 的结果和面上实际的孔口。只读，不存盘（仍只开副本）。
//   实际孔口 = 这张面的边里，旁边那张面是孔壁（圆柱 IsParamReversed=True / 圆锥 / 圆环、轴线与面法向平行）的圆边或圆弧；
//   另外列出内环上不是圆的闭合边（导入件常见的样条孔口）。
//   每个漏认的孔口打印：在外环还是内环、整圆还是圆弧、壁面类型、旁边的壁面偏向哪一侧、与它同轴同径的别的壁面在哪。
//   还统计：与这张面共面且相邻（共一条边）的面 —— 看起来是一张面，实际被拆成几张。
// 用法: FaceScanAudit.exe <输出目录> <副本根目录> <零件1> [零件2 ...]
class FaceScanAudit {
    static F.Application app;
    static void L(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    static void Pump(int n){ for (int i = 0; i < n; i++) { try { app.DoIdle(); } catch { } try { Application.DoEvents(); } catch { } Thread.Sleep(60); } }
    static T Try<T>(Func<T> f, T dflt){ try { return f(); } catch { return dflt; } }
    static string N(double v){ return v.ToString("0.###"); }

    static int faces, facesWithMouths, facesMissing, mouthsTotal, mouthsMissed, splitFaces, slotLoopsTotal, slotsFound;
    static readonly Dictionary<string, int> reasons = new Dictionary<string, int>();
    static void Reason(string r){ int n; reasons.TryGetValue(r, out n); reasons[r] = n + 1; }

    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        TianGongCadSuite.Licensing.LicenseTestHooks.GateOverride = 0;
        AuditFilter.Register();
        try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); }
        catch (Exception e) { L("连不上 CAD：" + e.Message); return 2; }
        try { app.DisplayAlerts = false; } catch { }
        L("已连上 CAD " + app.Version + "　插件 DLL " + typeof(HoleScanCad).Assembly.GetName().Version);
        string root = Path.GetFullPath(args[1]);
        var files = new List<string>();
        for (int i = 2; i < args.Length; i++) {
            if (Directory.Exists(args[i])) files.AddRange(Directory.GetFiles(args[i], "*.par")); else files.Add(args[i]);
        }
        if (args.Length == 2) files.AddRange(Directory.GetFiles(root, "*.par"));
        foreach (var f in files) {
            if (!Path.GetFullPath(f).StartsWith(root, StringComparison.OrdinalIgnoreCase)) { L("跳过（不在副本根目录下）：" + f); continue; }
            try { AuditPart(f); } catch (Exception e) { L("FATAL " + Path.GetFileName(f) + "：" + e); }
        }
        L("");
        L("RESULT 腰形内环 " + slotLoopsTotal + "，认出腰孔 " + slotsFound);
        L("RESULT 平面 " + faces + "，有孔口的面 " + facesWithMouths + "，认不全的面 " + facesMissing + "，孔口 " + mouthsTotal + "，漏认 " + mouthsMissed + "，被拆成几张的共面面 " + splitFaces);
        foreach (var kv in reasons.OrderByDescending(k => k.Value)) L("RESULT 漏认原因 " + kv.Key + " ×" + kv.Value);
        return 0;
    }

    sealed class Mouth { public G.Edge Edge; public V3 C, Ax; public double R; public bool Closed, Outer, ArcLoop; public G.Face Wall; public string WallKind = ""; }

    static void AuditPart(string path){
        L(""); L("== " + Path.GetFileName(path));
        var part = (P.PartDocument)app.Documents.Open(path);
        Pump(10);
        try {
            int mi = 0;
            foreach (P.Model model in part.Models) {
                mi++;
                var body = (G.Body)model.Body;
                var all = new List<G.Face>();
                foreach (G.Face f in (G.Faces)body.get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll)) all.Add(f);
                foreach (var f in all) {
                    if (!(Try<object>(() => f.Geometry, null) is G.Plane)) continue;
                    faces++;
                    AuditFace(part, mi, body, f, all);
                }
            }
        } finally {
            try { part.Close(false); } catch { }
            Pump(4);
        }
    }

    static V3 Normal(G.Face f){
        Array p = new double[3], n = new double[3]; ((G.Plane)f.Geometry).GetPlaneData(ref p, ref n);
        return V3.From(n).Unit();
    }
    static V3 PlanePoint(G.Face f){
        Array p = new double[3], n = new double[3]; ((G.Plane)f.Geometry).GetPlaneData(ref p, ref n);
        return V3.From(p);
    }
    static V3 Outward(G.Face f){ var n = Normal(f); return Try(() => f.IsParamReversed, false) ? n * -1.0 : n; }

    static void AuditFace(P.PartDocument part, int mi, G.Body body, G.Face face, List<G.Face> all){
        var n = Normal(face);
        var mouths = new List<Mouth>();
        var oddInner = new List<string>();
        var coplanarNeighbours = new HashSet<int>();
        var neighbourFaces = new List<G.Face>();
        int slotLoops = 0; var oddLoops = new List<string>();
        foreach (G.Loop loop in (G.Loops)face.Loops) {
            bool outer = Try(() => loop.IsOuterLoop, true);
            bool lines = false; int nArc = 0, nLine = 0;
            foreach (G.Edge e in (G.Edges)loop.Edges) { object g = Try<object>(() => e.Geometry, null); if (!(g is G.Circle)) lines = true; if (g is G.Circle) nArc++; else if (g is G.Line) nLine++; }
            if (!outer && nArc == 2 && nLine == 2 && Try(() => ((G.Edges)loop.Edges).Count, 0) == 4) slotLoops++;
            else if (!outer && nArc > 0 && nLine + nArc > 1) {
                // 带圆弧、又不是标准"两弧两直线"的内环：列出组成，看腰孔到底长什么样
                var parts = new List<string>();
                foreach (G.Edge e in (G.Edges)loop.Edges) {
                    object g = Try<object>(() => e.Geometry, null);
                    var ci = g as G.Circle;
                    if (ci != null) { Array cc = new double[3], ax = new double[3]; double r = 0; ci.GetCircleData(ref cc, ref ax, out r); parts.Add("弧R" + N(r * 1000) + "@" + V3.From(cc) + (Try(() => e.IsClosed, false) ? "整" : "")); }
                    else parts.Add(g == null ? "?" : g.GetType().Name.Replace("Class", "") + "#" + e.ID);
                }
                oddLoops.Add("内环[" + parts.Count + "] " + string.Join(" ", parts));
            }
            foreach (G.Edge e in (G.Edges)loop.Edges) {
                // 相邻面
                int cnt = 0; Array fs = new object[0];
                try { e.GetFaces(out cnt, ref fs); } catch { }
                G.Face other = null;
                for (int i = 0; i < cnt; i++) { var o = fs.GetValue(i) as G.Face; if (o != null && Try(() => o.ID, -1) != face.ID) other = o; }
                if (other != null && Try<object>(() => other.Geometry, null) is G.Plane) {
                    var on = Normal(other);
                    if (Math.Abs(Math.Abs(on.Dot(n)) - 1) < 1e-6 && Math.Abs((PlanePoint(other) - PlanePoint(face)).Dot(n)) < 1e-7) { if (coplanarNeighbours.Add(other.ID)) neighbourFaces.Add(other); }
                }
                var ci = Try<object>(() => e.Geometry, null) as G.Circle;
                if (ci == null) {
                    if (!outer && Try(() => e.IsClosed, false)) oddInner.Add("边#" + e.ID + " " + Try(() => e.Geometry.GetType().Name, "?"));
                    continue;
                }
                Array cc = new double[3], ax = new double[3]; double r = 0;
                ci.GetCircleData(ref cc, ref ax, out r);
                if (Math.Abs(Math.Abs(V3.From(ax).Unit().Dot(n)) - 1) > 1e-4) continue;
                var m = new Mouth { Edge = e, C = V3.From(cc), Ax = V3.From(ax).Unit(), R = r * 1000, Closed = Try(() => e.IsClosed, true), Outer = outer || lines, ArcLoop = !outer && !lines, Wall = other };
                object wg = other == null ? null : Try<object>(() => other.Geometry, null);
                m.WallKind = wg is G.Cylinder ? (Try(() => other.IsParamReversed, false) ? "孔壁圆柱" : "凸台圆柱") : wg is G.Cone ? "圆锥" : wg is G.Torus ? "圆环" : wg is G.Plane ? "平面" : (wg == null ? "?" : wg.GetType().Name);
                if (m.WallKind == "凸台圆柱" || m.WallKind == "平面") continue;
                mouths.Add(m);
            }
        }
        if (coplanarNeighbours.Count > 0) splitFaces++;
        foreach (var ol in oddLoops.Take(6)) L("  实体" + mi + " 面#" + face.ID + " " + ol);
        if (oddLoops.Count > 0) Reason("带圆弧的非标准内环");
        if (slotLoops > 0) {
            int skippedSlots; var sl = HoleScanCad.ScanFaceSlots(AutoHoleReader.ReadTarget(face), out skippedSlots);
            slotLoopsTotal += slotLoops; slotsFound += sl.Count;
            if (sl.Count != slotLoops) { L("  实体" + mi + " 面#" + face.ID + "：腰形内环 " + slotLoops + "，认出腰孔 " + sl.Count + "（判为凸台 " + skippedSlots + "）"); Reason("腰孔没认全"); }
        }
        if (mouths.Count == 0 && oddInner.Count == 0) return;
        facesWithMouths++;
        // 同一个孔口被拆成几段圆弧：按圆心合并
        var uniq = new List<Mouth>();
        foreach (var m in mouths) if (!uniq.Any(u => (u.C - m.C).Length < 1e-6 && Math.Abs(u.R - m.R) < 0.001)) uniq.Add(m);
        // 只算真孔口：内环上、环里全是圆（不是腰孔/缺口）、整圆或几段圆弧拼成整圆
        // 内环里全是圆弧、圆心都在一处（< 0.05mm）也算：导入件把一个孔口拆成几段、圆心/半径有微小误差的
        var looseArcs = mouths.Where(m => !m.Closed && m.ArcLoop).ToList();
        foreach (var m in looseArcs) if (!uniq.Contains(m) && !uniq.Any(u => (u.C - m.C).Length < 5e-5 && Math.Abs(u.R - m.R) < 0.05)) uniq.Add(m);
        uniq = uniq.Where(m => !m.Outer && (m.Closed || m.ArcLoop)).ToList();
        // 相邻共面面（看起来是同一张面）上的孔口
        int neighbourHoles = 0;
        foreach (var nf in neighbourFaces) {
            try {
                foreach (G.Loop loop in (G.Loops)nf.Loops) {
                    if (Try(() => loop.IsOuterLoop, true)) continue;
                    foreach (G.Edge e in (G.Edges)loop.Edges) if (Try<object>(() => e.Geometry, null) is G.Circle && Try(() => e.IsClosed, false)) neighbourHoles++;
                }
            } catch { }
        }
        if (neighbourHoles > 0) { L("  实体" + mi + " 面#" + face.ID + "：相邻共面面 " + neighbourFaces.Count + " 张上还有 " + neighbourHoles + " 个孔口（扫这张面时不会认）"); Reason("相邻共面面上的孔"); }
        if (uniq.Count == 0 && oddInner.Count == 0) return;
        mouthsTotal += uniq.Count;

        HoleScanCad.Invalidate(part);
        var t = AutoHoleReader.ReadTarget(face);
        string how; int skipped;
        var found = HoleScanCad.ScanFace(t, out how, out skipped);
        // ScanFace 结果是装配坐标（这里零件单开，Placement 为单位变换）
        var missed = uniq.Where(m => !found.Any(h => HoleScan.Parallel(h.Axis, m.Ax) && HoleScan.RadialOffsetMm(m.C, m.Ax, h.Center) < 0.05)).ToList();
        if (missed.Count == 0 && oddInner.Count == 0) return;
        facesMissing++;
        mouthsMissed += missed.Count;
        L("  实体" + mi + " 面#" + face.ID + " 法向 " + n + "：孔口 " + uniq.Count + "，扫描认出 " + found.Count + "（跳过 " + skipped + "）" + (how.Length > 0 ? " " + how : "")
          + (coplanarNeighbours.Count > 0 ? "，相邻共面面 " + coplanarNeighbours.Count + " 张" : "") + (oddInner.Count > 0 ? "，内环非圆闭合边 " + string.Join(",", oddInner) : ""));
        if (oddInner.Count > 0) Reason("内环上的孔口不是圆（样条等）");
        foreach (var m in missed) {
            int arcs = mouths.Count(x => (x.C - m.C).Length < 1e-6 && Math.Abs(x.R - m.R) < 0.001);
            string why;
            if (m.Outer) why = "孔口在外环上（孔挨着面的边界 / 面被拆）";
            else if (!m.Closed && arcs < 2) why = "单段圆弧";
            else why = "其他";
            // 旁边壁面的包围盒中心偏向
            string side = "";
            if (m.Wall != null) {
                Array lo = new double[3], hi = new double[3];
                try { m.Wall.GetRange(ref lo, ref hi); var c = (V3.From(lo) + V3.From(hi)) * 0.5; side = "自己的壁偏 " + N((c - m.C).Dot(Outward(face)) * 1000) + "mm"; } catch { }
            }
            // 同轴同径的别的壁面（ScanFace 判凸台时取的是第一张这种面）
            var others = new List<string>();
            foreach (var f in all) {
                object g = Try<object>(() => f.Geometry, null);
                if (!(g is G.Cylinder) && !(g is G.Cone) && !(g is G.Torus)) continue;
                if (f.ID == (m.Wall == null ? -1 : m.Wall.ID)) continue;
                foreach (G.Edge e in (G.Edges)f.Edges) {
                    var ci = Try<object>(() => e.Geometry, null) as G.Circle; if (ci == null) continue;
                    Array cc = new double[3], ax = new double[3]; double r = 0; ci.GetCircleData(ref cc, ref ax, out r);
                    if (Math.Abs(r * 1000 - m.R) > 0.01 || !HoleScan.Parallel(V3.From(ax), m.Ax) || HoleScan.RadialOffsetMm(m.C, m.Ax, V3.From(cc)) > 0.01) continue;
                    Array lo = new double[3], hi = new double[3]; f.GetRange(ref lo, ref hi);
                    var c = (V3.From(lo) + V3.From(hi)) * 0.5;
                    others.Add("面#" + f.ID + "(" + g.GetType().Name.Replace("Class", "") + ",轴向 " + N(HoleScan.AxialOffsetMm(m.C, m.Ax, V3.From(cc))) + "mm,偏 " + N((c - m.C).Dot(Outward(face)) * 1000) + "mm)");
                    break;
                }
            }
            if (why == "其他" && others.Count > 0) why = "同轴同径的别处壁面被当成它的壁（判成凸台）";
            Reason(why);
            L("    漏 Φ" + N(m.R * 2) + " @" + m.C + " 边#" + m.Edge.ID + " " + (m.Outer ? "外环" : "内环") + " " + (m.Closed ? "整圆" : "圆弧×" + arcs) + " 壁=" + m.WallKind + " " + side
              + (others.Count > 0 ? " 同轴同径壁面：" + string.Join(" ", others) : "") + " → " + why);
        }
    }
}

[ComImport, Guid("00000016-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAuditFilter {
    [PreserveSig] int HandleInComingCall(int type, IntPtr task, int ticks, IntPtr info);
    [PreserveSig] int RetryRejectedCall(IntPtr task, int ticks, int reject);
    [PreserveSig] int MessagePending(IntPtr task, int ticks, int pending);
}
sealed class AuditFilter : IAuditFilter {
    [DllImport("ole32.dll")] static extern int CoRegisterMessageFilter(IAuditFilter f, out IAuditFilter old);
    static AuditFilter keep;
    public static void Register(){ keep = new AuditFilter(); IAuditFilter old; CoRegisterMessageFilter(keep, out old); }
    public int HandleInComingCall(int a, IntPtr b, int c, IntPtr d){ return 0; }
    public int RetryRejectedCall(IntPtr a, int ticks, int reject){ return ticks < 120000 ? 200 : -1; }
    public int MessagePending(IntPtr a, int b, int c){ return 2; }
}
