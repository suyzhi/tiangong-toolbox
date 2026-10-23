using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using TianGongCadSuite;
using A=SolidEdgeAssembly;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;
using F=SolidEdgeFramework;
using S=SolidEdgeFrameworkSupport;

// 0.8.3：带零件间链接的零件（用「自动打孔」保持关联打过孔的零件都是）上用「批量排孔」「腰孔」「改孔型」。
//   ① A 板 4×Φ6.6 + 2×Φ5，B 板实心在 A 上方 z+30；自动打孔保持关联 → B 有零件间链接
//   ② 批量排孔（阵列）在 B 顶面排 3 个 M6 螺纹孔；③ 不建阵列再排 2 个；④ 腰孔 2 个；⑤ 改孔型：①打的孔改成通孔
//   每一步都要打成、活动文档回到装配、没有停在原位编辑里。
// 用法: LinkedPatternE2E.exe <输出目录>
class LinkedPatternE2E {
    static F.Application app;
    static int fail = 0;
    static object M = Type.Missing;
    const string Tpl = @"C:\Program Files\NDS\TianGong 2025\Template\ISO Metric\";
    static void L(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    static void Check(bool ok, string label){ L((ok ? "PASS: " : "FAIL: ") + label); if (!ok) fail++; }
    static void Pump(int n){ for (int i = 0; i < n; i++) { try { app.DoIdle(); } catch { } try { Application.DoEvents(); } catch { } Thread.Sleep(60); } }
    static string N(double v){ return v.ToString("0.###", CultureInfo.InvariantCulture); }
    static double D(Array a,int i){ return Convert.ToDouble(a.GetValue(i)); }
    static double Vol(P.Model m){ return ((G.Body)m.Body).Volume; }
    static void Retry(Action a, string what){
        Exception last = null;
        for (int i = 0; i < 8; i++) {
            try { a(); return; }
            catch (Exception e) { last = e; L("   " + what + " 被 CAD 拒绝（0x" + e.HResult.ToString("X8") + "），重试 " + (i + 1)); Pump(8); }
        }
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
    static bool InterpartLinks(P.PartDocument p){ bool has = false; try { p.HasInterpartLinks(ref has); } catch { } return has; }
    // 孔数 = 单孔 + 多孔孔特征（UserDefinedPattern）的输入孔
    static int HoleCount(P.Model m){
        int n = m.Holes.Count;
        for (int i = 1; i <= m.UserDefinedPatterns.Count; i++) n += m.UserDefinedPatterns.Item(i).NumberInputFeatures;
        return n;
    }
    static string Multis(P.Model m){
        var l = new List<string>();
        for (int i = 1; i <= m.UserDefinedPatterns.Count; i++) { var u = m.UserDefinedPatterns.Item(i); object d; l.Add(u.Name + "[" + u.NumberInputFeatures + "]" + (u.GetStatusEx(out d) == P.FeatureStatusConstants.igFeatureOK ? "" : "{" + u.GetStatusEx(out d) + "}")); }
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
    // 某个孔位在面 z 上最大的圆（孔口）直径，mm；没有为 0
    static double DiaAt(P.Model m, double z, double x, double y){
        double best = 0;
        foreach (G.Edge e in (G.Edges)((G.Body)m.Body).get_Edges(G.FeatureTopologyQueryTypeConstants.igQueryAll)) {
            var c = e.Geometry as G.Circle; if (c == null) continue;
            Array cc = new double[3], ax = new double[3]; double r; c.GetCircleData(ref cc, ref ax, out r);
            if (Math.Abs(D(cc,0) - x) < 1e-6 && Math.Abs(D(cc,1) - y) < 1e-6 && Math.Abs(D(cc,2) - z) < 1e-7) best = Math.Max(best, r * 2000);
        }
        return best;
    }
    static int Copies(P.PartDocument p){ try { return p.Constructions.InterpartConstructions.Count; } catch { return -1; } }
    static string CopyNames(P.PartDocument p){
        var l = new List<string>();
        try { foreach (object o in (System.Collections.IEnumerable)p.Constructions.InterpartConstructions) l.Add((string)((dynamic)o).Name); } catch { }
        return string.Join(" | ", l.ToArray());
    }
    static string Tree(P.PartDocument p){
        var l = new List<string>();
        try { var f = p.DesignEdgebarFeatures; for (int i = 1; i <= f.Count; i++) { try { l.Add((string)((dynamic)f.Item(i)).Name); } catch { l.Add("?"); } } } catch { }
        return string.Join(" → ", l.ToArray());
    }

    static readonly double[][] Clear = { new[]{0.02,0.02}, new[]{0.08,0.02}, new[]{0.08,0.08}, new[]{0.02,0.08} };   // Φ6.6
    static readonly double[][] Tap = { new[]{0.05,0.03}, new[]{0.05,0.07} };                                              // Φ5

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

    static List<Control> All(Control root){ var l = new List<Control>(); foreach (Control c in root.Controls) { l.Add(c); l.AddRange(All(c)); } return l; }
    static int PatternHoles(P.Model m){ int n = 0; try { for (int i = 1; i <= m.Patterns.Count; i++) n += Math.Max(0, m.Patterns.Item(i).NumberOfOccurrences - 1); } catch { } return n; }
    static void SetSlot(AutoHolePatternForm pf, bool on){ ((CheckBox)typeof(AutoHolePatternForm).GetField("slotCheck", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(pf)).Checked = on; }
    static string Res(string s){ return (s ?? "").Replace("\r\n", " | "); }

    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        TianGongCadSuite.Licensing.LicenseTestHooks.GateOverride = 0;
        string outDir = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
        Directory.CreateDirectory(outDir);
        try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); }
        catch (Exception e) { L("连不上 CAD：" + e.Message); return 2; }
        try { app.DisplayAlerts = false; } catch { }
        L("已连上 CAD " + app.Version + "　插件 " + typeof(AutoHoleWriter).Assembly.GetName().Version);
        string stamp = DateTime.Now.ToString("HHmmss");
        A.AssemblyDocument asm = null;
        try { Run(outDir, stamp, out asm); }
        catch (Exception e) { L("E2E 异常：" + e.GetType().Name + ": " + e.Message + "\r\n" + e.StackTrace); fail++; }
        finally { try { if (asm != null) asm.Close(true); } catch { } Pump(2); }
        L("");
        L(fail == 0 ? "LINKED-PATTERN E2E OK" : ("LINKED-PATTERN E2E FAILED " + fail));
        return fail == 0 ? 0 : 1;
    }

    static void Run(string outDir, string stamp, out A.AssemblyDocument asm){
        string pa = MakeA(Path.Combine(outDir, "LA_" + stamp + ".par"));
        string pb = MakePlate(Path.Combine(outDir, "LB_" + stamp + ".par"));
        A.AssemblyDocument a0 = null;
        Retry(delegate { a0 = (A.AssemblyDocument)app.Documents.Add("SolidEdge.AssemblyDocument", Tpl + "iso metric assembly.asm"); }, "新建装配");
        asm = a0; Pump(20);
        A.Occurrence occA = null, occB = null;
        Retry(delegate { occA = a0.Occurrences.AddByFilename(pa); }, "插入 A");
        Retry(delegate { occB = a0.Occurrences.AddByFilename(pb); }, "插入 B");
        Array mB = new double[]{ 1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0.03,1 };
        Retry(delegate { occB.PutMatrix(ref mB, true); }, "放置 B");
        Retry(delegate { a0.SaveAs(Path.Combine(outDir, "LinkedPattern_" + stamp + ".asm")); }, "保存装配");
        Pump(20);
        P.PartDocument partA = null, partB = null; P.Model modelA = null, modelB = null;
        Retry(delegate { partA = (P.PartDocument)occA.OccurrenceDocument; partB = (P.PartDocument)occB.OccurrenceDocument; modelA = (P.Model)partA.Models.Item(1); modelB = (P.Model)partB.Models.Item(1); }, "取实例文档");

        L("① 自动打孔（保持关联）：A 顶面 → B 底面");
        AutoHoleForm form = null;
        List<KeyValuePair<P.PartDocument, string>> created = null;
        try {
            object refA = null, refB = null;
            Retry(delegate { refA = a0.CreateReference(occA, FaceAtZ(modelA, 0.01)); refB = a0.CreateReference(occB, FaceAtZ(modelB, 0.0)); }, "引用");
            Retry(delegate { form = new AutoHoleForm(app, a0); }, "打开自动打孔");
            form.SuppressDialogs = true; Retry(delegate { form.Show(); }, "显示"); Pump(8);
            Retry(delegate { form.AcceptPick(refA); }, "点 A"); Pump(4);
            Retry(delegate { form.AcceptPick(refB); }, "点 B"); Pump(4);
            Retry(delegate { form.DrillForTest(); }, "打孔"); Pump(10);
            L("   RESULT: " + Res(form.LastResult));
            Check((form.LastResult ?? "").Contains("共打孔 6 个"), "自动打孔 6 个");
            created = form.LastCreatedForTest;
        } finally { if (form != null) { try { form.Close(); } catch { } } Pump(6); }
        Check(InterpartLinks(partB), "B 有零件间链接（后面几步要在原位编辑里改它）");
        int holes0 = HoleCount(modelB) + PatternHoles(modelB);

        L("② 批量排孔（阵列）：B 顶面 3 个 M6 螺纹孔");
        AutoHolePatternForm pf = null;
        try {
            Retry(delegate { pf = new AutoHolePatternForm(app, a0); }, "打开批量排孔");
            pf.SuppressDialogs = true; Retry(delegate { pf.Show(); }, "显示"); Pump(6);
            var all = All(pf);
            var kind = all.OfType<Segmented>().First(s => s.Items.Contains("螺纹孔"));
            var count = all.OfType<NumericUpDown>().First(n => n.Minimum == 1 && n.Maximum == 720);
            var edge = all.OfType<NumericUpDown>().First(n => n.Minimum == 0 && n.Maximum == 5000);
            object top = null;
            Retry(delegate { top = a0.CreateReference(occB, FaceAtZ(modelB, 0.01)); }, "B 顶面引用");
            Retry(delegate { pf.AcceptPick(top); }, "点 B 顶面"); Pump(6);
            kind.SelectedIndex = 1; count.Value = 3; edge.Value = 12; Pump(4);
            pf.AsPattern = true;
            Retry(delegate { pf.DrillForTest(); }, "排孔"); Pump(8);
            L("   RESULT: " + Res(pf.LastResult));
            Check((pf.LastResult ?? "").Contains("排孔完成：3 / 3"), "阵列排孔 3 / 3");
            Check(ReferenceEquals(app.ActiveDocument, a0), "排完回到装配");
            int h1 = HoleCount(modelB) + PatternHoles(modelB);
            Check(h1 - holes0 == 3, "B 上多了 3 个孔（实 " + (h1 - holes0) + "）");

            L("③ 不建阵列，逐个：B 顶面再排 2 个通孔（边距 40）");
            Retry(delegate { top = a0.CreateReference(occB, FaceAtZ(modelB, 0.01)); }, "B 顶面引用 2");
            Retry(delegate { pf.AcceptPick(top); }, "再点 B 顶面"); Pump(6);
            kind.SelectedIndex = 0; count.Value = 2; edge.Value = 40; pf.AsPattern = false; Pump(4);
            Retry(delegate { pf.DrillForTest(); }, "排孔 2"); Pump(8);
            L("   RESULT: " + Res(pf.LastResult));
            Check((pf.LastResult ?? "").Contains("排孔完成：2 / 2"), "逐个排孔 2 / 2");
            Check(ReferenceEquals(app.ActiveDocument, a0), "排完回到装配");

            L("④ 腰孔：B 顶面 2 个（边距 45）");
            Retry(delegate { top = a0.CreateReference(occB, FaceAtZ(modelB, 0.01)); }, "B 顶面引用 3");
            Retry(delegate { pf.AcceptPick(top); }, "再点 B 顶面"); Pump(6);
            SetSlot(pf, true); count.Value = 2; edge.Value = 45; Pump(4);
            int cut0 = modelB.ExtrudedCutouts.Count;
            Retry(delegate { pf.DrillForTest(); }, "腰孔"); Pump(8);
            L("   RESULT: " + Res(pf.LastResult));
            Check(modelB.ExtrudedCutouts.Count - cut0 == 2, "B 上多了 2 个腰孔（实 " + (modelB.ExtrudedCutouts.Count - cut0) + "）");
            Check(ReferenceEquals(app.ActiveDocument, a0), "切完回到装配");
            SetSlot(pf, false);
        } finally { if (pf != null) { try { pf.Close(); } catch { } } Pump(6); }

        L("⑤ 改孔型：①打的孔整组改成通孔");
        HoleEditForm ef = null;
        try {
            Retry(delegate { ef = new HoleEditForm(app, a0); }, "打开改孔型");
            ef.SuppressDialogs = true; Retry(delegate { ef.Show(); }, "显示"); Pump(6);
            Retry(delegate { ef.Preload(created); }, "带入"); Pump(4);
            Check(ef.TargetCount > 0, "带入了 ①打的孔（" + ef.TargetCount + " 个特征）");
            ef.SetSpecForTest(HoleKind.Through, null, 0);
            Retry(delegate { ef.ApplyForTest(); }, "应用"); Pump(8);
            L("   RESULT: " + Res(ef.LastResult));
            Check((ef.LastResult ?? "").IndexOf("失败") < 0 && (ef.LastResult ?? "").Contains("个孔"), "改孔型没有失败");
            Check(ReferenceEquals(app.ActiveDocument, a0), "改完回到装配");
        } finally { if (ef != null) { try { ef.Close(); } catch { } } Pump(6); }
        L("   B 特征树：" + Tree(partB));
    }
}
