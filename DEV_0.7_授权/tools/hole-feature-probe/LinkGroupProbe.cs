using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using TianGongCadSuite;
using A=SolidEdgeAssembly;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;
using F=SolidEdgeFramework;
using S=SolidEdgeFrameworkSupport;

// 0.8.4 探针（只在新建夹具 / 副本上跑）：
//   G 孔位链接（零件间复制）和它的多孔孔特征能不能编进一个特征组；组 Delete 是不是连成员一起删
//      G1 装配上下文里编组  G2 原位编辑里编组  G3 组.Delete()
//   U 多孔孔特征里的输入孔能不能挪（插件「同步孔位」退路要用）：改输入孔轮廓的孔心 + 重算；输入孔上能不能写关联属性
//   B 多实体零件的面 ID 是不是跨实体唯一（「不同设计体上的面无法同时打」怀疑同 ID 被当成同一张面）
// 用法: LinkGroupProbe.exe <输出目录> [多实体零件副本]
class LinkGroupProbe {
    static F.Application app;
    static object M = Type.Missing;
    const string Tpl = @"C:\Program Files\NDS\TianGong 2025\Template\ISO Metric\";
    static void L(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    static void Pump(int n){ for (int i = 0; i < n; i++) { try { app.DoIdle(); } catch { } try { Application.DoEvents(); } catch { } Thread.Sleep(60); } }
    static string N(double v){ return v.ToString("0.###", CultureInfo.InvariantCulture); }
    static double D(Array a,int i){ return Convert.ToDouble(a.GetValue(i)); }
    static string Hr(Exception e){ return e.GetType().Name + " 0x" + e.HResult.ToString("X8") + " " + e.Message; }
    static void Retry(Action a, string what){
        Exception last = null;
        for (int i = 0; i < 8; i++) { try { a(); return; } catch (Exception e) { last = e; Pump(8); } }
        throw last;
    }
    static P.RefPlane FindXY(P.PartDocument part){
        foreach (P.RefPlane c in part.RefPlanes) {
            Array n = new double[3], p = new double[3];
            c.GetNormal(ref n); c.GetRootPoint(ref p);
            if (Math.Abs(D(n,2)-1) < 1e-8 && Math.Abs(D(p,2)) < 1e-8) return c;
        }
        return null;
    }
    static P.Model Plate(P.PartDocument part, double w, double h, double t){
        var prof = part.ProfileSets.Add().Profiles.Add(FindXY(part));
        var Ls = new S.Line2d[4];
        Ls[0] = prof.Lines2d.AddBy2Points(0, 0, w, 0); Ls[1] = prof.Lines2d.AddBy2Points(w, 0, w, h);
        Ls[2] = prof.Lines2d.AddBy2Points(w, h, 0, h); Ls[3] = prof.Lines2d.AddBy2Points(0, h, 0, 0);
        var rel = (S.Relations2d)prof.Relations2d;
        for (int i = 0; i < 4; i++) rel.AddKeypoint(Ls[i], (int)SolidEdgeConstants.KeypointIndexConstants.igLineEnd, Ls[(i+1)%4], (int)SolidEdgeConstants.KeypointIndexConstants.igLineStart);
        prof.End(P.ProfileValidationType.igProfileClosed);
        Array arr = new object[]{ prof };
        return part.Models.AddFiniteExtrudedProtrusion(1, ref arr, P.FeaturePropertyConstants.igRight, t);
    }
    static G.Face FaceAtZ(P.Model m, double z){
        foreach (G.Face f in (G.Faces)((G.Body)m.Body).get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll)) {
            var pl = f.Geometry as G.Plane; if (pl == null) continue;
            Array p = new double[3], n = new double[3]; pl.GetPlaneData(ref p, ref n);
            if (Math.Abs(Math.Abs(D(n,2)) - 1) < 1e-6 && Math.Abs(D(p,2) - z) < 1e-9) return f;
        }
        return null;
    }
    static void Hole(P.PartDocument part, P.Model model, P.RefPlane plane, double x, double y, double z, double dia){
        var prof = part.ProfileSets.Add().Profiles.Add(plane);
        double x2, y2; prof.Convert3DCoordinate(x, y, z, out x2, out y2);
        prof.Holes2d.Add(x2, y2); prof.End(P.ProfileValidationType.igProfileClosed);
        var data = part.HoleDataCollection.AddEx(P.FeaturePropertyConstants.igRegularHole,
            "ISO Metric", M, M, M, dia, 0.0, 0.0, 0.0, 0.0, 0.0,
            P.FeaturePropertyConstants.igNone, M, M, M, M, M, P.FeaturePropertyConstants.igVBottomDimToFlat, M,
            M, M, M, M, M, true, M, M, M, M, M, M, M, M, M, M, M, M);
        foreach (var side in new[]{ P.FeaturePropertyConstants.igLeft, P.FeaturePropertyConstants.igRight }) {
            P.Hole h = null;
            try { h = model.Holes.AddThroughAll(prof, side, data); } catch { continue; }
            object desc; if (h != null && (int)h.GetStatusEx(out desc) == (int)P.FeatureStatusConstants.igFeatureOK) return;
            try { if (h != null) h.Delete(); } catch { }
        }
        throw new InvalidOperationException("夹具打孔失败");
    }
    static string Circles(P.Model m, double z){
        var res = new List<string>();
        foreach (G.Edge e in (G.Edges)((G.Body)m.Body).get_Edges(G.FeatureTopologyQueryTypeConstants.igQueryAll)) {
            var c = e.Geometry as G.Circle; if (c == null) continue;
            Array cc = new double[3], ax = new double[3]; double r; c.GetCircleData(ref cc, ref ax, out r);
            if (Math.Abs(D(cc,2) - z) > 1e-7) continue;
            res.Add("(" + N(D(cc,0)*1000) + "," + N(D(cc,1)*1000) + ")");
        }
        res.Sort(StringComparer.Ordinal);
        return string.Join(" ", res.ToArray());
    }
    static string Tree(P.PartDocument p){
        var l = new List<string>();
        try { var f = p.DesignEdgebarFeatures; for (int i = 1; i <= f.Count; i++) { try { l.Add((string)((dynamic)f.Item(i)).Name); } catch { l.Add("?"); } } } catch (Exception e) { l.Add("读不到:" + e.Message); }
        return string.Join(" → ", l.ToArray());
    }
    static int Copies(P.PartDocument p){ try { return p.Constructions.InterpartConstructions.Count; } catch { return -1; } }
    static bool Links(P.PartDocument p){ bool h = false; try { p.HasInterpartLinks(ref h); } catch { } return h; }
    static string Udps(P.Model m){
        var l = new List<string>();
        for (int i = 1; i <= m.UserDefinedPatterns.Count; i++) { var u = m.UserDefinedPatterns.Item(i); object d; l.Add(u.Name + "[" + u.NumberInputFeatures + "]{" + u.GetStatusEx(out d) + "}"); }
        return string.Join(" | ", l.ToArray());
    }
    static string Groups(P.Model m){
        var l = new List<string>();
        for (int i = 1; i <= m.FeatureGroups.Count; i++) {
            var g = m.FeatureGroups.Item(i);
            var mem = new List<string>();
            for (int k = 1; k <= g.Count; k++) { try { object o = g.Item(k); mem.Add(o is P.InterpartConstruction ? "复制" : o is P.UserDefinedPattern ? "多孔" : o is P.Hole ? "孔" : o.GetType().Name); } catch (Exception e) { mem.Add("?" + e.Message); } }
            l.Add(g.Name + "[" + string.Join(",", mem.ToArray()) + "]");
        }
        return string.Join(" | ", l.ToArray());
    }
    static readonly double[][] Clear = { new[]{0.02,0.02}, new[]{0.08,0.02}, new[]{0.08,0.08}, new[]{0.02,0.08} };
    static readonly double[][] Tap = { new[]{0.05,0.03}, new[]{0.05,0.07} };

    static string MakeA(string path){
        var a = (P.PartDocument)app.Documents.Add("SolidEdge.PartDocument", Tpl + "iso metric part.par");
        a.ModelingMode = P.ModelingModeConstants.seModelingModeOrdered;
        var ma = Plate(a, 0.1, 0.1, 0.01);
        var topA = a.RefPlanes.AddParallelByDistance(FaceAtZ(ma, 0.01), 0.0, P.ReferenceElementConstants.igNormalSide, M, M, M, M);
        foreach (var c in Clear) Hole(a, ma, topA, c[0], c[1], 0.01, 0.0066);
        foreach (var c in Tap) Hole(a, ma, topA, c[0], c[1], 0.01, 0.005);
        a.SaveAs(path); a.Close(false);
        return path;
    }
    static string MakePlate(string path){
        var b = (P.PartDocument)app.Documents.Add("SolidEdge.PartDocument", Tpl + "iso metric part.par");
        b.ModelingMode = P.ModelingModeConstants.seModelingModeOrdered;
        Plate(b, 0.1, 0.1, 0.01);
        b.SaveAs(path); b.Close(false);
        return path;
    }

    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        TianGongCadSuite.Licensing.LicenseTestHooks.GateOverride = 0;
        PFilter.Register();
        string outDir = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(outDir);
        try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); }
        catch (Exception e) { L("连不上 CAD：" + e.Message); return 2; }
        try { app.DisplayAlerts = false; } catch { }
        L("已连上 CAD " + app.Version + "　插件 DLL " + typeof(AutoHoleForm).Assembly.GetName().Version);
        try { Synthetic(outDir); } catch (Exception e) { L("夹具部分异常：" + Hr(e) + "\r\n" + e.StackTrace); }
        if (args.Length > 1) { try { FaceIds(args[1]); } catch (Exception e) { L("面 ID 部分异常：" + Hr(e)); } }
        L("PROBE DONE");
        return 0;
    }

    static void Synthetic(string outDir){
        string stamp = DateTime.Now.ToString("HHmmss");
        string pa = MakeA(Path.Combine(outDir, "GA_" + stamp + ".par"));
        string pb = MakePlate(Path.Combine(outDir, "GB_" + stamp + ".par"));
        string pc = MakePlate(Path.Combine(outDir, "GC_" + stamp + ".par"));
        A.AssemblyDocument a0 = null;
        Retry(delegate { a0 = (A.AssemblyDocument)app.Documents.Add("SolidEdge.AssemblyDocument", Tpl + "iso metric assembly.asm"); }, "新建装配");
        Pump(20);
        A.Occurrence occA = null, occB = null, occC = null;
        Retry(delegate { occA = a0.Occurrences.AddByFilename(pa); }, "A");
        Retry(delegate { occB = a0.Occurrences.AddByFilename(pb); }, "B");
        Retry(delegate { occC = a0.Occurrences.AddByFilename(pc); }, "C");
        Array mB = new double[]{ 1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0.03,1 };
        Array mC = new double[]{ 1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0.06,1 };
        Retry(delegate { occB.PutMatrix(ref mB, true); occC.PutMatrix(ref mC, true); }, "放置");
        Retry(delegate { a0.SaveAs(Path.Combine(outDir, "GTop_" + stamp + ".asm")); }, "保存");
        Pump(20);
        P.PartDocument partA = (P.PartDocument)occA.OccurrenceDocument, partB = (P.PartDocument)occB.OccurrenceDocument, partC = (P.PartDocument)occC.OccurrenceDocument;
        P.Model mA = (P.Model)partA.Models.Item(1), mBm = (P.Model)partB.Models.Item(1), mCm = (P.Model)partC.Models.Item(1);

        // ---- 打孔：B 保持关联、C 不关联 ----
        foreach (var keep in new[]{ true, false }) {
            var tgtOcc = keep ? occB : occC; var tgtModel = keep ? mBm : mCm; double z = keep ? 0.0 : 0.0;
            object refA = a0.CreateReference(occA, FaceAtZ(mA, 0.01)), refT = a0.CreateReference(tgtOcc, FaceAtZ(tgtModel, z));
            var form = new AutoHoleForm(app, a0); form.SuppressDialogs = true; form.Show(); Pump(8);
            form.KeepLink = keep;
            form.AcceptPick(refA); Pump(4);
            form.ScanFaceOnPick = false; form.AcceptPick(refT); Pump(4);
            form.DrillForTest(); Pump(10);
            L((keep ? "B（保持关联）" : "C（不关联）") + " 打孔：" + (form.LastResult ?? "").Replace("\r\n", " ⏎ "));
            form.Close(); Pump(4);
        }
        L("B 特征树：" + Tree(partB));
        L("B 多孔：" + Udps(mBm) + "　复制 " + Copies(partB) + "　组：" + Groups(mBm));

        // ---- G1 装配上下文里编组：复制 + 全部多孔特征 ----
        var members = new List<object>();
        for (int i = 1; i <= partB.Constructions.InterpartConstructions.Count; i++) members.Add(partB.Constructions.InterpartConstructions.Item(i));
        for (int i = 1; i <= mBm.UserDefinedPatterns.Count; i++) members.Add(mBm.UserDefinedPatterns.Item(i));
        P.FeatureGroup g = TryGroup(mBm, members, "G1 装配上下文");
        if (g == null) {
            string why;
            var chain = NativeHoleLink.ChainOf(a0, null, partB, PlacementOf(occB), out why);
            L("G2 进原位：链 " + (chain == null ? "认不出 " + why : chain.Key));
            var s = chain == null ? null : NativeHoleLink.Enter(app, a0, chain, out why);
            if (s == null) L("   进不了原位：" + why);
            else {
                var pb2 = s.Part; var mb2 = (P.Model)pb2.Models.Item(1);
                var mem2 = new List<object>();
                for (int i = 1; i <= pb2.Constructions.InterpartConstructions.Count; i++) mem2.Add(pb2.Constructions.InterpartConstructions.Item(i));
                for (int i = 1; i <= mb2.UserDefinedPatterns.Count; i++) mem2.Add(mb2.UserDefinedPatterns.Item(i));
                g = TryGroup(mb2, mem2, "G2 原位编辑里");
                if (g != null) {
                    L("   组成员：" + Groups(mb2));
                    try { g.Name = "孔位链接组测试"; L("   改组名 OK"); } catch (Exception e) { L("   改组名失败 " + Hr(e)); }
                    L("   原位里特征树：" + Tree(pb2));
                }
                NativeHoleLink.Leave(app, s);
                Pump(6);
            }
        } else L("   组成员：" + Groups(mBm));
        L("编组后 B 特征树：" + Tree(partB));
        L("编组后 B 组：" + Groups(mBm) + "　多孔 " + Udps(mBm));

        // ---- G4 挪参考零件 + UpdateAll：编了组以后关联还跟不跟 ----
        try {
            Array mA2 = new double[]{ 1,0,0,0, 0,1,0,0, 0,0,1,0, 0.004,0,0,1 };
            occA.PutMatrix(ref mA2, true); Pump(6);
            a0.UpdateAll(); Pump(10);
            L("G4 挪 A +4mm、更新链接后 B 底面圆：" + Circles(mBm, 0.0) + "　多孔 " + Udps(mBm));
        } catch (Exception e) { L("G4 异常 " + Hr(e)); }

        // ---- G3 组.Delete()：装配上下文里 ----
        if (g != null) {
            P.FeatureGroup gg = null;
            try { gg = mBm.FeatureGroups.Count > 0 ? mBm.FeatureGroups.Item(1) : null; } catch { }
            if (gg == null) L("G3 装配上下文里找不到组");
            else {
                try { gg.Delete(); Pump(6); L("G3 装配上下文 组.Delete() OK"); } catch (Exception e) { L("G3 装配上下文 组.Delete() 失败 " + Hr(e)); }
                L("   删组后：多孔 " + Udps(mBm) + "　单孔 " + mBm.Holes.Count + "　复制 " + Copies(partB) + "　HasInterpartLinks=" + Links(partB) + "　组 " + Groups(mBm));
                L("   B 底面圆：" + Circles(mBm, 0.0));
                L("   B 特征树：" + Tree(partB));
            }
        }

        // ---- U 多孔孔特征里的输入孔：写关联属性、挪孔心 ----
        try {
            L("C 多孔：" + Udps(mCm) + "　底面圆：" + Circles(mCm, 0.0));
            var u = mCm.UserDefinedPatterns.Item(1);
            Array arr = new object[0]; u.GetInputFeatures(ref arr);
            var inputs = arr.Cast<object>().OfType<P.Hole>().ToList();
            L("U 输入孔 " + inputs.Count + " 个，第 1 个 " + inputs[0].Name + " 在 Model.Holes 里=" + Enumerable.Range(1, mCm.Holes.Count).Any(i => mCm.Holes.Item(i).SystemName == inputs[0].SystemName));
            try {
                HoleLinkStore.Write(inputs[1], new HoleLink { RefOcc = "x.par:1", RefDoc = "x.par", RefCenter = new V3(1, 2, 3), RefAxis = new V3(0, 0, 1), TgtCenter = new V3(4, 5, 6) });
                var back = HoleLinkStore.Read(inputs[1]);
                L("U1 输入孔写关联属性：回读 " + (back == null ? "null" : back.RefOcc));
            } catch (Exception e) { L("U1 写属性失败 " + Hr(e)); }
            var prof = (P.Profile)inputs[1].Profile;
            double x2, y2, x, y, zz; prof.Holes2d.Item(1).GetCenterPoint(out x2, out y2); prof.Convert2DCoordinate(x2, y2, out x, out y, out zz);
            L("U2 第 2 个输入孔孔心 (" + N(x*1000) + "," + N(y*1000) + "," + N(zz*1000) + ")，轮廓 Holes2d " + prof.Holes2d.Count);
            double nx, ny; prof.Convert3DCoordinate(x + 0.003, y, zz, out nx, out ny);
            prof.Holes2d.Item(1).SetCenterPoint(nx, ny);
            int end = prof.End(P.ProfileValidationType.igProfileClosed);
            mCm.Recompute(); Pump(6);
            object d; L("U2 挪 +3mm（End=" + end + "）重算后：多孔 " + Udps(mCm) + "　输入孔状态 " + inputs[1].GetStatusEx(out d) + "　底面圆：" + Circles(mCm, 0.0));
            // U3 种子孔（第 1 个输入孔）也挪一下
            var prof0 = (P.Profile)inputs[0].Profile;
            prof0.Holes2d.Item(1).GetCenterPoint(out x2, out y2); prof0.Convert2DCoordinate(x2, y2, out x, out y, out zz);
            prof0.Convert3DCoordinate(x, y + 0.002, zz, out nx, out ny);
            prof0.Holes2d.Item(1).SetCenterPoint(nx, ny); prof0.End(P.ProfileValidationType.igProfileClosed);
            mCm.Recompute(); Pump(6);
            L("U3 种子孔挪 +2mm(y) 重算后：多孔 " + Udps(mCm) + "　底面圆：" + Circles(mCm, 0.0));
        } catch (Exception e) { L("U 异常 " + Hr(e) + "\r\n" + e.StackTrace); }
        try { a0.Close(false); } catch { }
        Pump(6);
    }

    static Transform PlacementOf(A.Occurrence occ){ Array m = new double[16]; occ.GetMatrix(ref m); return new Transform(m); }

    static P.FeatureGroup TryGroup(P.Model model, List<object> members, string label){
        try {
            Array arr = members.ToArray();
            var g = model.FeatureGroups.AddFeatureGroupsBySet(members.Count, ref arr);
            L(label + " BySet：" + (g == null ? "null" : "成员 " + g.Count + "/" + members.Count));
            if (g != null && g.Count == members.Count) return g;
            if (g != null) { try { g.Ungroup(); } catch { } }
        } catch (Exception e) { L(label + " BySet 失败 " + Hr(e)); }
        try {
            var g = model.FeatureGroups.AddFeatureGroupsByRange(members[0], members[members.Count - 1]);
            L(label + " ByRange：" + (g == null ? "null" : "成员 " + g.Count + "/" + members.Count));
            if (g != null && g.Count == members.Count) return g;
            if (g != null) { try { g.Ungroup(); } catch { } }
        } catch (Exception e) { L(label + " ByRange 失败 " + Hr(e)); }
        return null;
    }

    static void FaceIds(string path){
        L(""); L("== B 多实体零件面 ID：" + Path.GetFileName(path));
        var part = (P.PartDocument)app.Documents.Open(path);
        Pump(15);
        var where = new Dictionary<int, List<int>>();
        int n = part.Models.Count, faces = 0;
        for (int m = 1; m <= n; m++) {
            var body = (G.Body)((P.Model)part.Models.Item(m)).Body;
            foreach (G.Face f in (G.Faces)body.get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll)) {
                faces++;
                int id = f.ID; List<int> l;
                if (!where.TryGetValue(id, out l)) { l = new List<int>(); where[id] = l; }
                if (!l.Contains(m)) l.Add(m);
            }
        }
        var dup = where.Where(kv => kv.Value.Count > 1).ToList();
        L("实体 " + n + "，面 " + faces + "，不同 ID " + where.Count + "，跨实体重复的 ID " + dup.Count);
        foreach (var kv in dup.Take(10)) L("   ID " + kv.Key + " 出现在实体 " + string.Join(",", kv.Value.Select(x => x.ToString()).ToArray()));
        try { part.Close(false); } catch { }
    }
}

[ComImport, Guid("00000016-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IPMessageFilter {
    [PreserveSig] int HandleInComingCall(int type, IntPtr task, int ticks, IntPtr info);
    [PreserveSig] int RetryRejectedCall(IntPtr task, int ticks, int reject);
    [PreserveSig] int MessagePending(IntPtr task, int ticks, int pending);
}
sealed class PFilter : IPMessageFilter {
    [DllImport("ole32.dll")] static extern int CoRegisterMessageFilter(IPMessageFilter f, out IPMessageFilter old);
    static PFilter keep;
    public static void Register(){ keep = new PFilter(); IPMessageFilter old; CoRegisterMessageFilter(keep, out old); }
    public int HandleInComingCall(int a, IntPtr b, int c, IntPtr d){ return 0; }
    public int RetryRejectedCall(IntPtr a, int ticks, int reject){ return ticks < 120000 ? 200 : -1; }
    public int MessagePending(IntPtr a, int b, int c){ return 2; }
}
