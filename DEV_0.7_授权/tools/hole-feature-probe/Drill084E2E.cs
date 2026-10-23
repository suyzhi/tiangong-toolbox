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

// 0.8.4 自动打孔真机端到端（走「自动打孔」窗口的真实路径；夹具新建，多实体零件用副本，都不存原件）：
//   场景一（新建夹具，天工原生关联）：
//     ① A 板 6 个孔 → B 板：孔位链接 + 两个多孔孔特征编成一个「关联孔 …」组；打完打孔面列表清空
//     ② 挪 A → 同步孔位：孔跟过去，同步结果报出孔位链接个数
//     ③ 不全部重选，直接点 D 板顶面 + B 板底面打第二批：另起一个链接组，孔位链接紧挨着它的孔
//     ④ 撤销第二批：组、链接、孔都没了，第一批不动
//     ⑤ 改孔型改第一批里的一个多孔特征：只改它（另一个规格不跟着变），链接组还在
//     ⑥ 在特征树里删第一批的链接组：孔、链接一起没了，零件不再带链接
//   场景二（多实体零件副本 + 新建定位板）：两个不同实体上、面 ID 相同的两张面一次打
//     ⑦ 两张面都进列表（0.8.3 第二张会顶掉第一张）→ 打孔：两个实体各打上 1 个孔
//     ⑧ 零件的链接属于别的装配时：结果明确说没挂上天工关联、原因、已改用插件关联；挪定位板 → 同步孔位 → 孔挪回同轴
//     ⑨ 撤销
// 用法: Drill084E2E.exe <输出目录> [多实体零件副本]
class Drill084E2E {
    static F.Application app;
    static int fail = 0;
    static object M = Type.Missing;
    const string Tpl = @"C:\Program Files\NDS\TianGong 2025\Template\ISO Metric\";
    static void L(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    static void Check(bool ok, string label){ L((ok ? "PASS: " : "FAIL: ") + label); if (!ok) fail++; }
    static void Pump(int n){ for (int i = 0; i < n; i++) { try { app.DoIdle(); } catch { } try { Application.DoEvents(); } catch { } Thread.Sleep(60); } }
    static string N(double v){ return v.ToString("0.###", CultureInfo.InvariantCulture); }
    static double D(Array a,int i){ return Convert.ToDouble(a.GetValue(i)); }
    static string Hr(Exception e){ return e.GetType().Name + " 0x" + e.HResult.ToString("X8") + " " + e.Message; }
    static string R(AutoHoleForm f){ return (f.LastResult ?? "").Replace("\r\n", " ⏎ "); }
    static void Retry(Action a, string what){
        Exception last = null;
        for (int i = 0; i < 8; i++) { try { a(); return; } catch (Exception e) { last = e; L("   " + what + " 被拒（0x" + e.HResult.ToString("X8") + "），重试"); Pump(8); } }
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
    static bool HasCircle(P.Model m, double z, double x, double y){
        foreach (G.Edge e in (G.Edges)((G.Body)m.Body).get_Edges(G.FeatureTopologyQueryTypeConstants.igQueryAll)) {
            var c = e.Geometry as G.Circle; if (c == null) continue;
            Array cc = new double[3], ax = new double[3]; double r; c.GetCircleData(ref cc, ref ax, out r);
            if (Math.Abs(D(cc,2) - z) < 1e-7 && Math.Abs(D(cc,0) - x) < 1e-6 && Math.Abs(D(cc,1) - y) < 1e-6) return true;
        }
        return false;
    }
    static int HoleCount(P.Model m){
        int n = m.Holes.Count;
        for (int i = 1; i <= m.UserDefinedPatterns.Count; i++) n += m.UserDefinedPatterns.Item(i).NumberInputFeatures;
        return n;
    }
    static int Copies(P.PartDocument p){ try { return p.Constructions.InterpartConstructions.Count; } catch { return -1; } }
    static bool Links(P.PartDocument p){ bool h = false; try { p.HasInterpartLinks(ref h); } catch { } return h; }
    static string Tree(P.PartDocument p){
        var l = new List<string>();
        try { var f = p.DesignEdgebarFeatures; for (int i = 1; i <= f.Count; i++) { try { l.Add((string)((dynamic)f.Item(i)).Name); } catch { l.Add("?"); } } } catch { }
        return string.Join(" → ", l.ToArray());
    }
    static string Udps(P.Model m){
        var l = new List<string>();
        for (int i = 1; i <= m.UserDefinedPatterns.Count; i++) { var u = m.UserDefinedPatterns.Item(i); object d; l.Add(u.Name + "[" + u.NumberInputFeatures + "]" + (u.GetStatusEx(out d) == P.FeatureStatusConstants.igFeatureOK ? "" : "{坏}")); }
        return string.Join(" | ", l.ToArray());
    }
    static List<P.FeatureGroup> GroupList(P.Model m){ var l = new List<P.FeatureGroup>(); for (int i = 1; i <= m.FeatureGroups.Count; i++) l.Add(m.FeatureGroups.Item(i)); return l; }
    static string Groups(P.Model m){
        var l = new List<string>();
        foreach (var g in GroupList(m)) {
            var mem = new List<string>();
            for (int k = 1; k <= g.Count; k++) { try { object o = g.Item(k); mem.Add(o is P.InterpartConstruction ? "复制" : o is P.UserDefinedPattern ? "多孔" : o is P.Hole ? "孔" : "?"); } catch { mem.Add("?"); } }
            l.Add(g.Name + "[" + string.Join(",", mem.ToArray()) + "]");
        }
        return string.Join(" | ", l.ToArray());
    }
    static G.Edge CircleEdgeAt(P.Model m, double x, double y, double z){
        G.Edge best = null; double bestR = double.MaxValue;
        foreach (G.Edge e in (G.Edges)((G.Body)m.Body).get_Edges(G.FeatureTopologyQueryTypeConstants.igQueryAll)) {
            var c = e.Geometry as G.Circle; if (c == null) continue;
            Array cc = new double[3], ax = new double[3]; double r; c.GetCircleData(ref cc, ref ax, out r);
            if (Math.Abs(D(cc,0) - x) < 1e-6 && Math.Abs(D(cc,1) - y) < 1e-6 && Math.Abs(D(cc,2) - z) < 1e-7 && r < bestR) { best = e; bestR = r; }
        }
        return best;
    }
    static readonly double[][] Clear = { new[]{0.02,0.02}, new[]{0.08,0.02}, new[]{0.08,0.08}, new[]{0.02,0.08} };
    static readonly double[][] Tap = { new[]{0.05,0.03}, new[]{0.05,0.07} };

    static P.PartDocument NewPart(){
        var p = (P.PartDocument)app.Documents.Add("SolidEdge.PartDocument", Tpl + "iso metric part.par");
        p.ModelingMode = P.ModelingModeConstants.seModelingModeOrdered;
        return p;
    }
    static string MakeA(string path){
        var a = NewPart();
        var ma = Plate(a, 0.1, 0.1, 0.01);
        var top = a.RefPlanes.AddParallelByDistance(FaceAtZ(ma, 0.01), 0.0, P.ReferenceElementConstants.igNormalSide, M, M, M, M);
        foreach (var c in Clear) Hole(a, ma, top, c[0], c[1], 0.01, 0.0066);
        foreach (var c in Tap) Hole(a, ma, top, c[0], c[1], 0.01, 0.005);
        a.SaveAs(path); a.Close(false);
        return path;
    }
    static string MakeD(string path){
        var d = NewPart();
        var md = Plate(d, 0.1, 0.1, 0.01);
        var top = d.RefPlanes.AddParallelByDistance(FaceAtZ(md, 0.01), 0.0, P.ReferenceElementConstants.igNormalSide, M, M, M, M);
        Hole(d, md, top, 0.035, 0.05, 0.01, 0.0066); Hole(d, md, top, 0.065, 0.05, 0.01, 0.0066);
        d.SaveAs(path); d.Close(false);
        return path;
    }
    static string MakePlate(string path){ var b = NewPart(); Plate(b, 0.1, 0.1, 0.01); b.SaveAs(path); b.Close(false); return path; }
    // 定位板：20×20×5，中心一个 Φ6.6 通孔（孔轴 = 板的 z）
    static string MakePin(string path){
        var p = NewPart();
        var m = Plate(p, 0.02, 0.02, 0.005);
        var top = p.RefPlanes.AddParallelByDistance(FaceAtZ(m, 0.005), 0.0, P.ReferenceElementConstants.igNormalSide, M, M, M, M);
        Hole(p, m, top, 0.01, 0.01, 0.005, 0.0066);
        p.SaveAs(path); p.Close(false);
        return path;
    }

    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        TianGongCadSuite.Licensing.LicenseTestHooks.GateOverride = 0;
        E2EFilter84.Register();
        string outDir = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(outDir);
        try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); }
        catch (Exception e) { L("连不上 CAD：" + e.Message); return 2; }
        try { app.DisplayAlerts = false; } catch { }
        L("已连上 CAD " + app.Version + "　插件 DLL " + typeof(AutoHoleForm).Assembly.GetName().Version);
        try { Scene1(outDir); } catch (Exception e) { L("场景一异常：" + Hr(e) + "\r\n" + e.StackTrace); fail++; }
        if (args.Length > 1) { try { Scene2(outDir, args[1]); } catch (Exception e) { L("场景二异常：" + Hr(e) + "\r\n" + e.StackTrace); fail++; } }
        L("");
        L(fail == 0 ? "DRILL-084 E2E OK" : ("DRILL-084 E2E FAILED " + fail));
        return fail == 0 ? 0 : 1;
    }

    static void Scene1(string outDir){
        L("======== 场景一：天工原生关联 + 孔位链接组");
        string stamp = DateTime.Now.ToString("HHmmss");
        string pa = MakeA(Path.Combine(outDir, "EA_" + stamp + ".par")), pb = MakePlate(Path.Combine(outDir, "EB_" + stamp + ".par")), pd = MakeD(Path.Combine(outDir, "ED_" + stamp + ".par"));
        A.AssemblyDocument a0 = null;
        Retry(delegate { a0 = (A.AssemblyDocument)app.Documents.Add("SolidEdge.AssemblyDocument", Tpl + "iso metric assembly.asm"); }, "新建装配");
        Pump(20);
        A.Occurrence occA = null, occB = null, occD = null;
        Retry(delegate { occA = a0.Occurrences.AddByFilename(pa); occB = a0.Occurrences.AddByFilename(pb); occD = a0.Occurrences.AddByFilename(pd); }, "插入");
        Array mB = new double[]{ 1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0.03,1 }, mD = new double[]{ 1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0.06,1 };
        Retry(delegate { occB.PutMatrix(ref mB, true); occD.PutMatrix(ref mD, true); }, "放置");
        Retry(delegate { a0.SaveAs(Path.Combine(outDir, "E1_" + stamp + ".asm")); }, "保存");
        Pump(20);
        var partA = (P.PartDocument)occA.OccurrenceDocument; var partB = (P.PartDocument)occB.OccurrenceDocument; var partD = (P.PartDocument)occD.OccurrenceDocument;
        var mA = (P.Model)partA.Models.Item(1); var mBm = (P.Model)partB.Models.Item(1); var mDm = (P.Model)partD.Models.Item(1);
        AutoHoleForm form = null;
        try {
            form = new AutoHoleForm(app, a0); form.SuppressDialogs = true; form.Show(); Pump(8);
            form.KeepLink = true;
            L("① A 顶面认孔 → B 底面 → 打孔");
            form.AcceptPick(a0.CreateReference(occA, FaceAtZ(mA, 0.01))); Pump(4);
            form.ScanFaceOnPick = false; form.AcceptPick(a0.CreateReference(occB, FaceAtZ(mBm, 0.0))); Pump(4); form.ScanFaceOnPick = true;
            Check(form.UiSummary().IndexOf("将打 6 个孔") >= 0, "预览：将打 6 个孔");
            form.DrillForTest(); Pump(10);
            L("   RESULT: " + R(form));
            Check(form.LastResult.IndexOf("共打孔 6 个") >= 0 && form.LastResult.IndexOf("天工原生关联 6 个孔") >= 0, "6 个孔、都挂上天工原生关联");
            L("   B 特征树：" + Tree(partB));
            L("   B 组：" + Groups(mBm));
            var gs = GroupList(mBm);
            Check(gs.Count == 1 && gs[0].Name.StartsWith("关联孔 ") && Groups(mBm).Contains("[复制,多孔,多孔]"), "孔位链接 + 两个多孔孔特征编成一个「关联孔 …」组");
            Check(gs.Count == 1 && HoleGrouping.IsLinkGroup(gs[0]), "组上挂着孔位链接组的标记");
            Check(form.UiSummary().IndexOf("打孔面 0 个") >= 0, "打完打孔面列表清空（不会把用过的面再打一遍）");
            Check(ReferenceEquals(app.ActiveDocument, a0), "回到顶层装配");

            L("② 挪 A (+5,-3) → 同步孔位");
            Array mA2 = new double[]{ 1,0,0,0, 0,1,0,0, 0,0,1,0, 0.005,-0.003,0,1 };
            Retry(delegate { occA.PutMatrix(ref mA2, true); }, "挪 A"); Pump(8);
            form.SyncForTest(); Pump(10);
            L("   同步：" + R(form) + "　状态：" + form.UiSummary().Substring(form.UiSummary().IndexOf("状态=")));
            bool moved = Clear.All(c => HasCircle(mBm, 0.0, c[0] + 0.005, c[1] - 0.003)) && Tap.All(c => HasCircle(mBm, 0.0, c[0] + 0.005, c[1] - 0.003));
            Check(moved, "编了组以后 6 个孔照样跟到 (+5,-3)");
            Check((form.LastResult ?? "").IndexOf("1 个孔位链接") >= 0, "同步结果报出孔位链接个数");

            L("③ 第二批：D 顶面 + B 底面（A 挪过，参考孔重选）");
            form.ClearAllForTest();
            form.AcceptPick(a0.CreateReference(occD, FaceAtZ(mDm, 0.01))); Pump(4);
            form.ScanFaceOnPick = false; form.AcceptPick(a0.CreateReference(occB, FaceAtZ(mBm, 0.0))); Pump(4); form.ScanFaceOnPick = true;
            L("   UI: " + form.UiSummary());
            form.DrillForTest(); Pump(10);
            L("   RESULT: " + R(form));
            Check(form.LastResult.IndexOf("共打孔 2 个") >= 0 && form.LastResult.IndexOf("天工原生关联 2 个孔") >= 0 && form.LastResult.IndexOf("打孔面 1 个") >= 0, "第二批：只打新点的一张面，2 个孔挂上关联");
            L("   B 组：" + Groups(mBm));
            L("   B 特征树：" + Tree(partB));
            Check(GroupList(mBm).Count == 2 && Copies(partB) == 2, "第二批另起一个链接组、2 个孔位链接（实 " + GroupList(mBm).Count + " / " + Copies(partB) + "）");

            L("④ 撤销第二批");
            form.UndoForTest(); Pump(8);
            L("   " + R(form) + "　组：" + Groups(mBm));
            Check(GroupList(mBm).Count == 1 && Copies(partB) == 1 && HoleCount(mBm) == 6, "撤销后回到 1 个组、1 个链接、6 个孔（实 " + GroupList(mBm).Count + " / " + Copies(partB) + " / " + HoleCount(mBm) + "）");
            form.Close(); form = null; Pump(4);

            L("⑤ 改孔型：点「×4」里的孔 → M8 沉孔（只改它，另一个规格不动）→ 撤销");
            HoleEditForm ef = null;
            try {
                ef = new HoleEditForm(app, a0); ef.SuppressDialogs = true; ef.Show(); Pump(6);
                ef.AcceptPick(a0.CreateReference(occB, CircleEdgeAt(mBm, 0.025, 0.017, 0.0))); Pump(4);
                L("   预览：" + ef.PreviewText.Replace("\r\n", " ⏎ "));
                string before = Udps(mBm);
                ef.SetSpecForTest(HoleKind.Counterbore, "M8", 0); Pump(2);
                ef.ApplyForTest(); Pump(8);
                L("   改后：" + Udps(mBm) + "　组：" + Groups(mBm));
                Check(Udps(mBm).Contains("M8") && Udps(mBm).Contains("M6 沉孔 Φ6.6/Φ11"), "只改了点中的多孔特征，另一个规格没被整组带着改");
                Check(GroupList(mBm).Count == 1 && Groups(mBm).Contains("[复制,多孔,多孔]"), "链接组还在");
                ef.UndoForTest(); Pump(8);
                Check(Udps(mBm) == before, "撤销修改后回到原样（" + Udps(mBm) + "）");
            } finally { if (ef != null) try { ef.Close(); } catch { } Pump(4); }

            L("⑥ 在特征树里删第一批的链接组");
            var gl = GroupList(mBm);
            if (gl.Count > 0) { try { gl[0].Delete(); Pump(6); } catch (Exception e) { L("   删组异常 " + Hr(e)); } }
            Check(HoleCount(mBm) == 0 && Copies(partB) == 0 && !Links(partB), "删组 = 孔、孔位链接一起删，零件不再带链接（实 孔 " + HoleCount(mBm) + " 复制 " + Copies(partB) + " 链接 " + Links(partB) + "）");
        } finally { if (form != null) try { form.Close(); } catch { } try { a0.Close(false); } catch { } Pump(6); }
    }

    // 实体 m 上能放 Φ6.6 孔的平面（面内两个方向都 ≥ 24mm），按面积从大到小
    sealed class Cand { public int Body; public G.Face Face; public string Id; public double Area; public V3 Centre, Out; }
    static List<Cand> Candidates(P.PartDocument part){
        var list = new List<Cand>();
        for (int m = 1; m <= part.Models.Count; m++) {
            var body = (G.Body)((P.Model)part.Models.Item(m)).Body;
            foreach (G.Face f in (G.Faces)body.get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll)) {
                var pl = f.Geometry as G.Plane; if (pl == null) continue;
                Array p = new double[3], n = new double[3]; pl.GetPlaneData(ref p, ref n);
                var nv = V3.From(n).Unit();
                Array lo = new double[3], hi = new double[3]; f.GetRange(ref lo, ref hi);
                var span = V3.From(hi) - V3.From(lo);
                var dims = new[]{ Math.Abs(span.X), Math.Abs(span.Y), Math.Abs(span.Z) }.OrderByDescending(x => x).ToArray();
                if (dims[1] < 0.024) continue;
                var c = (V3.From(hi) + V3.From(lo)) * 0.5;
                c = c - nv * (c - V3.From(p)).Dot(nv);
                bool rev = false; try { rev = f.IsParamReversed; } catch { }
                list.Add(new Cand { Body = m, Face = f, Id = f.ID.ToString(), Area = f.Area, Centre = c, Out = rev ? nv * -1.0 : nv });
            }
        }
        return list.OrderByDescending(x => x.Area).ToList();
    }

    static Array Frame(V3 origin, V3 z){
        var x = Math.Abs(z.X) < 0.9 ? new V3(1, 0, 0) : new V3(0, 1, 0);
        x = (x - z * x.Dot(z)).Unit(); var y = z.Cross(x).Unit();
        return new double[]{ x.X,x.Y,x.Z,0, y.X,y.Y,y.Z,0, z.X,z.Y,z.Z,0, origin.X,origin.Y,origin.Z,1 };
    }

    static void Scene2(string outDir, string mbPath){
        L(""); L("======== 场景二：多实体零件，两个实体上面 ID 相同的两张面一次打");
        string stamp = DateTime.Now.ToString("HHmmss");
        string pin = MakePin(Path.Combine(outDir, "EP_" + stamp + ".par"));
        A.AssemblyDocument a0 = null;
        Retry(delegate { a0 = (A.AssemblyDocument)app.Documents.Add("SolidEdge.AssemblyDocument", Tpl + "iso metric assembly.asm"); }, "新建装配");
        Pump(20);
        A.Occurrence occM = null;
        Retry(delegate { occM = a0.Occurrences.AddByFilename(mbPath); }, "插入多实体零件");
        Pump(10);
        var part = (P.PartDocument)occM.OccurrenceDocument;
        L("实体数 " + part.Models.Count + "　HasInterpartLinks=" + Links(part));
        string ctx = ""; try { part.GetInContextAssemblyNameForInterpartLinks(ref ctx); } catch { }
        L("链接属于：" + ctx);
        var cands = Candidates(part);
        // 找两个不同实体上 ID 相同的面（0.8.3 的 bug 正是这种）；找不到就取两个不同实体上最大的面
        Cand c1 = null, c2 = null;
        foreach (var a in cands) { foreach (var b in cands) if (b.Body != a.Body && b.Id == a.Id && (b.Centre - a.Centre).Length > 0.03) { c1 = a; c2 = b; break; } if (c1 != null) break; }
        if (c1 == null) { c1 = cands[0]; c2 = cands.First(x => x.Body != c1.Body); L("没找到同 ID 的两张面，取两个实体上最大的面"); }
        L("面 1：实体 " + c1.Body + " ID " + c1.Id + " 中心 " + c1.Centre + "　面 2：实体 " + c2.Body + " ID " + c2.Id + " 中心 " + c2.Centre);
        A.Occurrence p1 = null, p2 = null;
        Retry(delegate { p1 = a0.Occurrences.AddByFilename(pin); p2 = a0.Occurrences.AddByFilename(pin); }, "插入定位板");
        // 定位板孔心 (10,10) 落在面中心外法向 30mm 处
        Func<Cand, Array> place = c => { var fr = Frame(c.Centre, c.Out); var z = c.Out; var x = new V3(D(fr,0), D(fr,1), D(fr,2)); var y = new V3(D(fr,4), D(fr,5), D(fr,6));
                                         var o = c.Centre + z * 0.03 - x * 0.01 - y * 0.01; return Frame(o, z); };
        Array f1 = place(c1), f2 = place(c2);
        Retry(delegate { p1.PutMatrix(ref f1, true); p2.PutMatrix(ref f2, true); }, "放置定位板");
        Retry(delegate { a0.SaveAs(Path.Combine(outDir, "E2_" + stamp + ".asm")); }, "保存装配（只存新建的装配，零件副本不存）");
        Pump(15);
        var pinPart1 = (P.PartDocument)p1.OccurrenceDocument; var pm = (P.Model)pinPart1.Models.Item(1);
        var m1 = (P.Model)part.Models.Item(c1.Body); var m2 = (P.Model)part.Models.Item(c2.Body);
        AutoHoleForm form = null;
        try {
            form = new AutoHoleForm(app, a0); form.SuppressDialogs = true; form.Show(); Pump(8);
            form.KeepLink = true;
            form.AcceptPick(a0.CreateReference(p1, FaceAtZ(pm, 0.005))); Pump(3);
            form.AcceptPick(a0.CreateReference(p2, FaceAtZ((P.Model)((P.PartDocument)p2.OccurrenceDocument).Models.Item(1), 0.005))); Pump(3);
            form.ScanFaceOnPick = false;
            form.AcceptPick(a0.CreateReference(occM, c1.Face)); Pump(3);
            form.AcceptPick(a0.CreateReference(occM, c2.Face)); Pump(3);
            form.ScanFaceOnPick = true;
            string ui = form.UiSummary();
            L("   UI: " + ui);
            Check(ui.IndexOf("打孔面 2 个") >= 0, "⑦ 两个实体上的两张面都在列表里（0.8.3 第二张会顶掉第一张）");
            Check(ui.IndexOf("实体 " + c1.Body) >= 0 && ui.IndexOf("实体 " + c2.Body) >= 0, "列表里写着是哪个实体");
            int h1 = HoleSpotsCount(m1), h2 = HoleSpotsCount(m2);
            form.DrillForTest(); Pump(10);
            L("   RESULT: " + R(form));
            var heads = form.ResultItemsForTest.Where(i => i.Kind == ResultList.Kind.Warn).Select(i => i.Text).ToList();
            foreach (var w in heads) L("   ⚠ " + w);
            bool d1 = HoleAt(m1, c1), d2 = HoleAt(m2, c2);
            Check(d1 && d2, "⑦ 两个实体各打上 1 个孔（实体 " + c1.Body + "：" + d1 + "，实体 " + c2.Body + "：" + d2 + "）");
            bool elsewhere = ctx.Length > 0 && !NativeLinkMath.SamePath(ctx, a0.FullName);
            if (elsewhere) {
                Check(form.LastResult.IndexOf("没挂上天工关联") >= 0 && form.LastResult.IndexOf("属于") >= 0 && form.LastResult.IndexOf("插件关联 2 个孔") >= 0,
                      "⑧ 链接属于别的装配：结果说清楚没挂上天工关联、为什么、已改用插件关联");
                Check(heads.Count > 0, "⑧ 结果列表里有醒目的「!」条目");
            } else Check(form.LastResult.IndexOf("天工原生关联 2 个孔") >= 0, "⑧ 2 个孔都挂上天工原生关联");

            L("⑧ 挪定位板 1（面内 +2mm）→ 同步孔位");
            var fr1 = Frame(c1.Centre, c1.Out); var xv = new V3(D(fr1,0), D(fr1,1), D(fr1,2));
            var moved = new double[16]; for (int i = 0; i < 16; i++) moved[i] = D(f1, i);
            moved[12] += xv.X * 0.002; moved[13] += xv.Y * 0.002; moved[14] += xv.Z * 0.002;
            Array mv = moved;
            Retry(delegate { p1.PutMatrix(ref mv, true); }, "挪定位板"); Pump(8);
            form.SyncForTest(); Pump(10);
            L("   同步：" + R(form));
            var shifted = new Cand { Body = c1.Body, Centre = c1.Centre + xv * 0.002, Out = c1.Out };
            Check(HoleAt(m1, shifted) && !HoleAt(m1, c1), "同步后实体 " + c1.Body + " 上的孔跟到了新位置");
            Check(HoleAt(m2, c2), "没挪的那个孔不动");

            L("⑨ 撤销");
            form.UndoForTest(); Pump(8);
            L("   " + R(form));
            Check(!HoleAt(m1, shifted) && !HoleAt(m2, c2) && HoleSpotsCount(m1) == h1 && HoleSpotsCount(m2) == h2, "撤销后两个实体都回到原样");
        } finally { if (form != null) try { form.Close(); } catch { } try { a0.Close(false); } catch { } Pump(6); }
    }

    static int HoleSpotsCount(P.Model m){ int n = 0; foreach (G.Edge e in (G.Edges)((G.Body)m.Body).get_Edges(G.FeatureTopologyQueryTypeConstants.igQueryAll)) if (e.Geometry is G.Circle) n++; return n; }
    // 面上 c.Centre 处有没有 Φ6.6 左右的孔口圆（零件局部 = 装配，零件在原点）
    static bool HoleAt(P.Model m, Cand c){
        foreach (G.Edge e in (G.Edges)((G.Body)m.Body).get_Edges(G.FeatureTopologyQueryTypeConstants.igQueryAll)) {
            var ci = e.Geometry as G.Circle; if (ci == null) continue;
            Array cc = new double[3], ax = new double[3]; double r; ci.GetCircleData(ref cc, ref ax, out r);
            var d = V3.From(cc) - c.Centre; var radial = d - c.Out * d.Dot(c.Out);
            if (radial.Length < 0.0002 && r * 2000 > 4 && r * 2000 < 7) return true;   // Φ6.6 参考孔按配做规则打成 M6 螺纹孔（Φ4.92）
        }
        return false;
    }
}

[ComImport, Guid("00000016-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IE2E84Filter {
    [PreserveSig] int HandleInComingCall(int type, IntPtr task, int ticks, IntPtr info);
    [PreserveSig] int RetryRejectedCall(IntPtr task, int ticks, int reject);
    [PreserveSig] int MessagePending(IntPtr task, int ticks, int pending);
}
sealed class E2EFilter84 : IE2E84Filter {
    [DllImport("ole32.dll")] static extern int CoRegisterMessageFilter(IE2E84Filter f, out IE2E84Filter old);
    static E2EFilter84 keep;
    public static void Register(){ keep = new E2EFilter84(); IE2E84Filter old; CoRegisterMessageFilter(keep, out old); }
    public int HandleInComingCall(int a, IntPtr b, int c, IntPtr d){ return 0; }
    public int RetryRejectedCall(IntPtr a, int ticks, int reject){ return ticks < 120000 ? 200 : -1; }
    public int MessagePending(IntPtr a, int b, int c){ return 2; }
}
