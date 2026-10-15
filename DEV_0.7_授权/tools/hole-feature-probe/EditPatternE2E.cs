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

// 0.7.6 真机端到端（2026-10-14）：「种子孔 + 阵列」和「改孔型」。自己造夹具（新文档，落在输出目录），走窗口的真实代码路径。
//   ① 批量排孔（阵列）：Q 板等分 4 个 M6 螺纹孔 → 1 个孔特征 + 1 个矩形阵列、组「…×4」、孔位、切除体积；
//   ② 同一面再排 2 个 → 并进同一组「…×6」；撤销第二批 → 回到「…×4」；
//   ③ 改孔型：点阵列里第 3 个孔的孔口圆边 → 认出种子孔 + 阵列 + 组 → 改成 M8 圆柱沉孔 → 4 个沉孔、组名跟着改；
//      贯通改盲孔被拒绝；撤销修改 → 回到 M6 螺纹孔、组名原样、体积复原；
//   ④ 逐个打的 3 个 M6 过孔（不建阵列）→「改孔型…」带进来 → 整组改成 M8 螺纹孔 → 组名、孔名、螺纹回读；
//   ⑤ 圆周均布（阵列）：用一个圆盘零件的边定分度圆，6 个 Φ6.6 → 1 个孔特征 + 1 个圆周阵列，孔位在 R20 上。
// 用法: EditPatternE2E.exe <输出目录>
class EditPatternE2E {
    static F.Application app;
    static int fail = 0;
    static object M = Type.Missing;
    const string Tpl = @"C:\Program Files\NDS\TianGong 2025\Template\ISO Metric\";
    static void L(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    static void Check(bool ok, string label){ L((ok ? "PASS: " : "FAIL: ") + label); if (!ok) fail++; }
    static void Pump(int n){ for (int i = 0; i < n; i++) { try { app.DoIdle(); } catch { } try { Application.DoEvents(); } catch { } Thread.Sleep(60); } }
    static double D(Array a,int i){ return Convert.ToDouble(a.GetValue(i)); }
    static string N(double v){ return v.ToString("0.###", CultureInfo.InvariantCulture); }
    static void Retry(Action a, string what){
        Exception last = null;
        for (int i = 0; i < 8; i++) {
            try { a(); return; }
            catch (Exception e) { last = e; L("   " + what + " 被 CAD 拒绝（0x" + e.HResult.ToString("X8") + "），重试 " + (i + 1)); Pump(8); }
        }
        throw last;
    }

    // ---- 夹具（和 NewFeaturesE2E 同一套） ----
    static P.RefPlane FindXY(P.PartDocument part){
        foreach (P.RefPlane c in part.RefPlanes) {
            Array n = new double[3], p = new double[3], u = new double[3];
            c.GetNormal(ref n); c.GetRootPoint(ref p); c.GetReferenceDirection(ref u);
            if (Math.Abs(D(n,2)-1) < 1e-8 && Math.Abs(D(p,0)) < 1e-8 && Math.Abs(D(p,1)) < 1e-8 && Math.Abs(D(u,0)-1) < 1e-8) return c;
        }
        return null;
    }
    static P.PartDocument NewPart(){
        P.PartDocument part = null;
        Retry(delegate { part = (P.PartDocument)app.Documents.Add("SolidEdge.PartDocument", Tpl + "iso metric part.par"); }, "新建零件");
        Retry(delegate { part.ModelingMode = P.ModelingModeConstants.seModelingModeOrdered; }, "顺序建模");
        return part;
    }
    static P.Model Plate(P.PartDocument part, double x0, double y0, double x1, double y1, double t){
        var prof = part.ProfileSets.Add().Profiles.Add(FindXY(part));
        var Ls = new S.Line2d[4];
        Ls[0] = prof.Lines2d.AddBy2Points(x0, y0, x1, y0); Ls[1] = prof.Lines2d.AddBy2Points(x1, y0, x1, y1);
        Ls[2] = prof.Lines2d.AddBy2Points(x1, y1, x0, y1); Ls[3] = prof.Lines2d.AddBy2Points(x0, y1, x0, y0);
        var rel = (S.Relations2d)prof.Relations2d;
        for (int i = 0; i < 4; i++) rel.AddKeypoint(Ls[i], (int)SolidEdgeConstants.KeypointIndexConstants.igLineEnd, Ls[(i+1)%4], (int)SolidEdgeConstants.KeypointIndexConstants.igLineStart);
        prof.End(P.ProfileValidationType.igProfileClosed);
        Array arr = new object[]{ prof };
        return part.Models.AddFiniteExtrudedProtrusion(1, ref arr, P.FeaturePropertyConstants.igRight, t);
    }
    static P.Model Disc(P.PartDocument part, double cx, double cy, double r, double t){
        var prof = part.ProfileSets.Add().Profiles.Add(FindXY(part));
        prof.Circles2d.AddByCenterRadius(cx, cy, r);
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
    static G.Edge CircleEdgeAt(P.Model m, double x, double y, double z){
        G.Edge best = null; double bestR = double.MaxValue;
        foreach (G.Edge e in (G.Edges)((G.Body)m.Body).get_Edges(G.FeatureTopologyQueryTypeConstants.igQueryAll)) {
            var c = e.Geometry as G.Circle; if (c == null) continue;
            Array cc = new double[3], ax = new double[3]; double r;
            c.GetCircleData(ref cc, ref ax, out r);
            if (Math.Abs(D(cc,0) - x) < 1e-6 && Math.Abs(D(cc,1) - y) < 1e-6 && Math.Abs(D(cc,2) - z) < 1e-7 && r < bestR) { best = e; bestR = r; }
        }
        return best;
    }
    static string Save(P.PartDocument part, string dir, string name){
        string p = Path.Combine(dir, name);
        Retry(delegate { part.SaveAs(p); }, "保存 " + name);
        Retry(delegate { part.Close(false); }, "关闭 " + name);
        return p;
    }
    static A.Occurrence Place(A.AssemblyDocument asm, string path, double[] m){
        A.Occurrence occ = null;
        Retry(delegate { occ = asm.Occurrences.AddByFilename(path); }, "插入 " + Path.GetFileName(path));
        Pump(6);
        Array mm = m;
        Retry(delegate { occ.PutMatrix(ref mm, true); }, "放置 " + Path.GetFileName(path));
        return occ;
    }
    static double[] At(double x, double y, double z){ return new double[]{ 1,0,0,0, 0,1,0,0, 0,0,1,0, x,y,z,1 }; }
    static A.AssemblyDocument NewAsm(){
        A.AssemblyDocument asm = null;
        Retry(delegate { asm = (A.AssemblyDocument)app.Documents.Add("SolidEdge.AssemblyDocument", Tpl + "iso metric assembly.asm"); }, "新建装配");
        Pump(20);
        return asm;
    }
    static List<Control> All(Control root){ var l = new List<Control>(); Collect(root, l); return l; }
    static void Collect(Control c, List<Control> all){ foreach (Control k in c.Controls) { all.Add(k); Collect(k, all); } }
    static double Vol(P.Model m){ return ((G.Body)m.Body).Volume; }
    static List<string> Groups(P.Model m){ var l = new List<string>(); foreach (P.FeatureGroup g in m.FeatureGroups) l.Add(g.Name + "[" + g.Count + "]"); return l; }
    static List<string> FeatureNames(P.Model m){
        var l = new List<string>();
        for (int i = 1; i <= m.Holes.Count; i++) l.Add(m.Holes.Item(i).Name);
        for (int i = 1; i <= m.Patterns.Count; i++) l.Add(m.Patterns.Item(i).Name);
        l.Sort(StringComparer.Ordinal); return l;
    }
    static List<double[]> Circles(P.Model m, double z){
        var r = new List<double[]>();
        foreach (G.Edge e in (G.Edges)((G.Body)m.Body).get_Edges(G.FeatureTopologyQueryTypeConstants.igQueryAll)) {
            var c = e.Geometry as G.Circle; if (c == null) continue;
            Array cc = new double[3], ax = new double[3]; double rad;
            c.GetCircleData(ref cc, ref ax, out rad);
            if (Math.Abs(D(cc,2) - z) > 1e-7) continue;
            r.Add(new[]{ Math.Round(D(cc,0) * 1000, 3), Math.Round(D(cc,1) * 1000, 3), Math.Round(rad * 2000, 3) });
        }
        return r;
    }
    static string Dump(P.Model m){
        try {
            var h = m.Holes.Item(1); var d = (P.HoleData)h.HoleData;
            return h.SystemName + " " + d.HoleType + " Φ" + N(d.HoleDiameter * 1000) + " 大小=" + d.Size + " 螺纹=" + d.ThreadDescription + " 处理=" + d.TreatmentType + " 数据名=" + d.Name;
        } catch (Exception e) { return "读不出：" + e.Message; }
    }
    static string Show(List<double[]> c){ return string.Join(" ", c.Select(h => "(" + N(h[0]) + "," + N(h[1]) + ")Φ" + N(h[2])).ToArray()); }
    static double Cyl(double diaMm, double hMm){ return Math.PI * diaMm * diaMm / 4 * hMm * 1e-9; }
    static bool Rel(double got, double want){ return Math.Abs(got - want) / want < 0.005; }
    static string Mm3(double v){ return (v * 1e9).ToString("0.#"); }

    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        TianGongCadSuite.Licensing.LicenseTestHooks.GateOverride = 0;
        string outDir = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
        Directory.CreateDirectory(outDir);
        try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); }
        catch (Exception e) { L("连不上 CAD：" + e.Message); return 2; }
        try { app.DisplayAlerts = false; } catch { }
        L("已连上 CAD " + app.Version + "，插件 DLL " + Product.Version);
        string st = DateTime.Now.ToString("HHmmss");
        try {
            LinearAndEdit(outDir, st);
            Individual(outDir, st);
            Circular(outDir, st);
        } catch (Exception ex) {
            L("E2E 异常：" + ex.GetType().Name + ": " + ex.Message + "\r\n" + ex.StackTrace);
            fail++;
        }
        L("");
        L(fail == 0 ? "EDIT-PATTERN E2E OK" : ("EDIT-PATTERN E2E FAILED " + fail));
        return fail == 0 ? 0 : 1;
    }

    static AutoHolePatternForm OpenPattern(A.AssemblyDocument asm){
        AutoHolePatternForm pf = null;
        Retry(delegate { pf = new AutoHolePatternForm(app, asm); }, "打开批量排孔");
        pf.SuppressDialogs = true;
        Retry(delegate { pf.Show(); }, "显示"); Pump(6);
        return pf;
    }

    // ================= ①②③ 阵列 + 改孔型 =================
    static void LinearAndEdit(string outDir, string st){
        L(""); L("① 批量排孔（阵列）：Q 板等分 4 个 M6 螺纹孔，边距 10");
        var q = NewPart(); Plate(q, 0, 0, 0.12, 0.06, 0.01); string pq = Save(q, outDir, "EpQ_" + st + ".par");
        var asm = NewAsm();
        AutoHolePatternForm pf = null; HoleEditForm ef = null;
        try {
            var occQ = Place(asm, pq, At(0, 0, 0));
            Retry(delegate { asm.SaveAs(Path.Combine(outDir, "EpFixture_" + st + ".asm")); }, "保存装配");
            Pump(10);
            P.Model mq = null;
            Retry(delegate { mq = (P.Model)((P.PartDocument)occQ.OccurrenceDocument).Models.Item(1); }, "取 Q");
            double vol0 = Vol(mq);
            pf = OpenPattern(asm);
            Check(pf.AsPattern, "批量排孔默认建成阵列");
            var all = All(pf);
            var kind = all.OfType<Segmented>().First(s => s.Items.Contains("螺纹孔"));
            var count = all.OfType<NumericUpDown>().First(n => n.Minimum == 1 && n.Maximum == 720);
            var edge = all.OfType<NumericUpDown>().First(n => n.Minimum == 0 && n.Maximum == 5000);
            object refQ = null;
            Retry(delegate { refQ = asm.CreateReference(occQ, FaceAtZ(mq, 0.01)); }, "Q 顶面引用");
            Retry(delegate { pf.AcceptPick(refQ); }, "点 Q 顶面"); Pump(6);
            kind.SelectedIndex = 1; count.Value = 4; edge.Value = 10; Pump(4);
            Retry(delegate { pf.DrillForTest(); }, "开始打孔"); Pump(8);
            L("   RESULT: " + (pf.LastResult ?? "").Replace("\r\n", " ⏎ "));
            Check((pf.LastResult ?? "").Contains("排孔完成：4 / 4"), "排孔 4 / 4");
            Check((pf.LastResult ?? "").Contains("矩形阵列"), "生成方式写明「种子孔 + 矩形阵列」");
            Check(mq.Holes.Count == 1 && mq.Patterns.Count == 1, "Q 上 1 个孔特征 + 1 个阵列（实 " + mq.Holes.Count + " / " + mq.Patterns.Count + "）");
            var g1 = Groups(mq);
            L("   特征组：" + string.Join(" | ", g1.ToArray()) + "　特征：" + string.Join(" | ", FeatureNames(mq).ToArray()));
            Check(g1.Count == 1 && g1[0] == "M6 螺纹孔（螺纹全长） ×4[2]", "组「M6 螺纹孔（螺纹全长） ×4」= 种子孔 + 阵列");
            var c1 = Circles(mq, 0.01).OrderBy(h => h[0]).ToList();
            L("   顶面孔心：" + Show(c1));
            double[] want1 = { 10, 43.333, 76.667, 110 };
            Check(c1.Count == 4 && Enumerable.Range(0, 4).All(i => Math.Abs(c1[i][0] - want1[i]) < 0.01 && Math.Abs(c1[i][1] - 30) < 0.01 && Math.Abs(c1[i][2] - 4.917) < 0.01),
                  "4 个孔在 x = 10/43.333/76.667/110、y = 30，Φ4.917");
            double one = Cyl(4.917, 10);
            Check(Rel(vol0 - Vol(mq), 4 * one), "切除体积 = 4 个 M6 螺纹底孔（实 " + Mm3(vol0 - Vol(mq)) + " / 期 " + Mm3(4 * one) + " mm³）");
            var hd = (P.HoleData)mq.Holes.Item(1).HoleData;
            Check(hd.Standard == "GB Metric" && hd.SubType == "标准螺纹" && hd.Size == "M6" && hd.ThreadDepthMethod.ToString() == "igThroughAll", "种子孔是天工标准螺纹孔、螺纹全长");

            L(""); L("② 同一个面再排 2 个（边距 25）→ 并组 → 撤销");
            object refQ2 = null;
            Retry(delegate { refQ2 = asm.CreateReference(occQ, FaceAtZ(mq, 0.01)); }, "Q 顶面引用（第二批）");
            Retry(delegate { pf.AcceptPick(refQ2); }, "再点 Q 顶面"); Pump(6);
            count.Value = 2; edge.Value = 25; Pump(4);
            Retry(delegate { pf.DrillForTest(); }, "第二批"); Pump(8);
            L("   RESULT: " + (pf.LastResult ?? "").Replace("\r\n", " ⏎ "));
            var g2 = Groups(mq);
            L("   特征组：" + string.Join(" | ", g2.ToArray()) + "　特征：" + string.Join(" | ", FeatureNames(mq).ToArray()));
            Check(g2.Count == 1 && g2[0] == "M6 螺纹孔（螺纹全长） ×6[4]", "两批阵列并成一组「…×6」（4 个成员：2 个种子孔 + 2 个阵列）");
            Check(Rel(vol0 - Vol(mq), 6 * one), "切除体积 = 6 个孔");
            Retry(delegate { pf.UndoForTest(); }, "撤销第二批"); Pump(8);
            L("   撤销：" + (pf.LastResult ?? "").Replace("\r\n", " ⏎ "));
            Check(Groups(mq).SequenceEqual(g1) && mq.Holes.Count == 1 && mq.Patterns.Count == 1, "撤销后回到第一批：组「…×4」、1 孔 1 阵列");
            Check(Rel(vol0 - Vol(mq), 4 * one), "撤销后体积 = 4 个孔");
            var names1 = FeatureNames(mq);

            L(""); L("③ 改孔型：点阵列里第 3 个孔 → 整组改成 M8 圆柱沉孔 → 撤销");
            Retry(delegate { ef = new HoleEditForm(app, asm); }, "打开改孔型");
            ef.SuppressDialogs = true;
            Retry(delegate { ef.Show(); }, "显示"); Pump(6);
            object refEdge = null;
            Retry(delegate { refEdge = asm.CreateReference(occQ, CircleEdgeAt(mq, 0.076667, 0.03, 0.01) ?? CircleEdgeAt(mq, 0.0766667, 0.03, 0.01)); }, "第 3 个孔的圆边引用");
            Retry(delegate { ef.AcceptPick(refEdge); }, "点第 3 个孔"); Pump(6);
            L("   预览：" + ef.PreviewText.Replace("\r\n", " ⏎ "));
            Check(ef.TargetCount == 1, "认出 1 个要改的孔（阵列里的孔 → 种子孔）");
            var cur = ef.CurrentSpecForTest;
            Check(cur.Kind == HoleKind.Tapped && cur.ThreadSize == "M6" && cur.Through, "界面按现在的规格填好：M6 螺纹孔、贯通（实 " + cur.Summary + "）");
            L("   改前种子孔：" + Dump(mq));
            ef.SetSpecForTest(HoleKind.Tapped, "M6", 8); Pump(2);
            Check(!ef.CanApply && ef.PreviewText.Contains("贯通孔"), "贯通孔改盲孔：拒绝，并说明原因");
            ef.SetSpecForTest(HoleKind.Counterbore, "M8", 0); Pump(2);
            var cb = ef.CurrentSpecForTest;
            L("   改成：" + cb.Summary);
            Check(ef.CanApply, "M8 圆柱沉孔可以应用");
            Retry(delegate { ef.ApplyForTest(); }, "应用"); Pump(8);
            L("   RESULT: " + (ef.LastResult ?? "").Replace("\r\n", " ⏎ "));
            var g3 = Groups(mq);
            L("   特征组：" + string.Join(" | ", g3.ToArray()) + "　特征：" + string.Join(" | ", FeatureNames(mq).ToArray()));
            string cbShort = HoleNaming.Short(cb);
            Check(g3.Count == 1 && g3[0] == cbShort + " ×4[2]", "组名跟着改成「" + cbShort + " ×4」");
            var c3 = Circles(mq, 0.01);
            L("   顶面孔口：" + Show(c3));
            Check(c3.Count == 4 && c3.All(h => Math.Abs(h[2] - cb.CounterboreDiameter) < 0.01), "4 个孔口都变成 Φ" + N(cb.CounterboreDiameter) + "（阵列跟着种子孔变）");
            double oneCb = Cyl(cb.HoleDiameter, 10) + Cyl(cb.CounterboreDiameter, cb.CounterboreDepth) - Cyl(cb.HoleDiameter, cb.CounterboreDepth);
            Check(Rel(vol0 - Vol(mq), 4 * oneCb), "切除体积 = 4 个沉孔（实 " + Mm3(vol0 - Vol(mq)) + " / 期 " + Mm3(4 * oneCb) + " mm³，孔径也换了）");
            var hd3 = (P.HoleData)mq.Holes.Item(1).HoleData;
            Check(hd3.HoleType.ToString() == "igCounterboreHole" && hd3.Size == "M8", "种子孔回读 igCounterboreHole / M8");
            Check(ef.CanUndo, "可以撤销修改");
            Retry(delegate { ef.UndoForTest(); }, "撤销修改"); Pump(8);
            L("   撤销：" + (ef.LastResult ?? "").Replace("\r\n", " ⏎ "));
            Check(Groups(mq).SequenceEqual(g1), "撤销后组名回到「M6 螺纹孔（螺纹全长） ×4」");
            Check(FeatureNames(mq).SequenceEqual(names1), "撤销后孔名、阵列名原样");
            Check(Rel(vol0 - Vol(mq), 4 * one), "撤销后体积回到 4 个 M6 螺纹孔");
            L("   撤销后种子孔：" + Dump(mq) + "　切除 " + Mm3(vol0 - Vol(mq)));
            var hd4 = (P.HoleData)mq.Holes.Item(1).HoleData;
            Check(hd4.Size == "M6" && hd4.ThreadDescription.Contains("M6"), "撤销后种子孔回读 M6 螺纹");
        } finally {
            if (ef != null) try { ef.Close(); } catch { }
            if (pf != null) try { pf.Close(); } catch { }
            try { asm.Close(false); } catch { }
            Pump(4);
        }
    }

    // ================= ④ 逐个打的孔，从批量排孔「改孔型…」带进来 =================
    static void Individual(string outDir, string st){
        L(""); L("④ 逐个打 3 个 M6 过孔 → 「改孔型…」带进来 → 整组改成 M8 螺纹孔");
        var r = NewPart(); Plate(r, 0, 0, 0.12, 0.06, 0.01); string pr = Save(r, outDir, "EpR_" + st + ".par");
        var asm = NewAsm();
        AutoHolePatternForm pf = null; HoleEditForm ef = null;
        try {
            var occ = Place(asm, pr, At(0, 0, 0));
            Retry(delegate { asm.SaveAs(Path.Combine(outDir, "EpRFixture_" + st + ".asm")); }, "保存装配");
            Pump(10);
            P.Model m = null;
            Retry(delegate { m = (P.Model)((P.PartDocument)occ.OccurrenceDocument).Models.Item(1); }, "取 R");
            double vol0 = Vol(m);
            pf = OpenPattern(asm);
            pf.AsPattern = false;
            var all = All(pf);
            var count = all.OfType<NumericUpDown>().First(n => n.Minimum == 1 && n.Maximum == 720);
            object refR = null;
            Retry(delegate { refR = asm.CreateReference(occ, FaceAtZ(m, 0.01)); }, "R 顶面引用");
            Retry(delegate { pf.AcceptPick(refR); }, "点 R 顶面"); Pump(6);
            count.Value = 3; Pump(4);
            Retry(delegate { pf.DrillForTest(); }, "开始打孔"); Pump(8);
            L("   RESULT: " + (pf.LastResult ?? "").Replace("\r\n", " ⏎ "));
            Check(m.Holes.Count == 3 && m.Patterns.Count == 0, "逐个打：3 个孔特征、没有阵列");
            var created = pf.LastCreatedForTest;
            Check(created != null && created.Count == 3, "「改孔型…」能带过去刚打的 3 个孔");
            Retry(delegate { ef = new HoleEditForm(app, asm); }, "打开改孔型");
            ef.SuppressDialogs = true;
            Retry(delegate { ef.Show(); }, "显示"); Pump(4);
            Retry(delegate { ef.Preload(created); }, "带入"); Pump(4);
            Check(ef.TargetCount == 3, "带进来 3 个孔");
            ef.SetSpecForTest(HoleKind.Tapped, "M8", 0); Pump(2);
            Retry(delegate { ef.ApplyForTest(); }, "应用"); Pump(8);
            L("   RESULT: " + (ef.LastResult ?? "").Replace("\r\n", " ⏎ "));
            var g = Groups(m);
            var names = FeatureNames(m);
            L("   特征组：" + string.Join(" | ", g.ToArray()) + "　孔：" + string.Join(" | ", names.ToArray()));
            Check(g.Count == 1 && g[0] == "M8 螺纹孔（螺纹全长） ×3[3]", "组名改成「M8 螺纹孔（螺纹全长） ×3」");
            Check(names.SequenceEqual(new[]{ "M8 螺纹孔（螺纹全长）-1", "M8 螺纹孔（螺纹全长）-2", "M8 螺纹孔（螺纹全长）-3" }), "孔名改成 M8 …-1/-2/-3");
            int ok = 0;
            for (int i = 1; i <= m.Holes.Count; i++) { var hd = (P.HoleData)m.Holes.Item(i).HoleData; if (hd.ThreadDescription.Contains("M8") && hd.Size == "M8") ok++; }
            Check(ok == 3, "3 个孔都回读 M8 螺纹（实 " + ok + "）");
            Check(Rel(vol0 - Vol(m), 3 * Cyl(6.647, 10)), "切除体积 = 3 个 M8 螺纹底孔 Φ6.647（实 " + Mm3(vol0 - Vol(m)) + " / 期 " + Mm3(3 * Cyl(6.647, 10)) + " mm³）");
            // 螺纹孔换螺纹孔、孔径变小（M8 Φ6.647 → M5 Φ4.134）：CAD 会按旧孔径查螺纹表，插件要分两步换
            ef.SetSpecForTest(HoleKind.Tapped, "M5", 0); Pump(2);
            Retry(delegate { ef.ApplyForTest(); }, "再改成 M5"); Pump(8);
            L("   RESULT: " + (ef.LastResult ?? "").Replace("\r\n", " ⏎ "));
            int ok5 = 0;
            for (int i = 1; i <= m.Holes.Count; i++) { var hd = (P.HoleData)m.Holes.Item(i).HoleData; if (hd.ThreadDescription.Contains("M5") && hd.Size == "M5") ok5++; }
            L("   " + Dump(m));
            Check(ok5 == 3, "再改成 M5：3 个孔都回读 M5 螺纹（实 " + ok5 + "）");
            Check(Rel(vol0 - Vol(m), 3 * Cyl(4.134, 10)), "切除体积 = 3 个 M5 螺纹底孔 Φ4.134（实 " + Mm3(vol0 - Vol(m)) + " / 期 " + Mm3(3 * Cyl(4.134, 10)) + " mm³）");
            // 换过 HoleData 的孔 SystemName 会变：批量排孔窗口的「撤销本次打孔」仍要删得掉
            Retry(delegate { pf.UndoForTest(); }, "撤销本次打孔"); Pump(8);
            L("   撤销本次打孔：" + (pf.LastResult ?? "").Replace("\r\n", " ⏎ "));
            Check(m.Holes.Count == 0 && m.FeatureGroups.Count == 0 && Math.Abs(Vol(m) - vol0) < 1e-12, "改过孔型再「撤销本次打孔」：3 个孔都删掉、体积复原");
        } finally {
            if (ef != null) try { ef.Close(); } catch { }
            if (pf != null) try { pf.Close(); } catch { }
            try { asm.Close(false); } catch { }
            Pump(4);
        }
    }

    // ================= ⑤ 圆周均布（阵列） =================
    static void Circular(string outDir, string st){
        L(""); L("⑤ 圆周均布（阵列）：圆盘边定分度圆 R20，6 个 M6 过孔");
        var c = NewPart(); Plate(c, 0, 0, 0.1, 0.1, 0.01); string pc = Save(c, outDir, "EpC_" + st + ".par");
        var d = NewPart(); Disc(d, 0, 0, 0.02, 0.005); string pd = Save(d, outDir, "EpDisc_" + st + ".par");
        var asm = NewAsm();
        AutoHolePatternForm pf = null;
        try {
            var occC = Place(asm, pc, At(0, 0, 0));
            var occD = Place(asm, pd, At(0.05, 0.05, 0.01));
            Retry(delegate { asm.SaveAs(Path.Combine(outDir, "EpCFixture_" + st + ".asm")); }, "保存装配");
            Pump(10);
            P.Model mc = null, md = null;
            Retry(delegate { mc = (P.Model)((P.PartDocument)occC.OccurrenceDocument).Models.Item(1); md = (P.Model)((P.PartDocument)occD.OccurrenceDocument).Models.Item(1); }, "取实例");
            double vol0 = Vol(mc);
            pf = OpenPattern(asm);
            var all = All(pf);
            var mode = all.OfType<Segmented>().First(s => s.Items.Contains("圆周均布"));
            var count = all.OfType<NumericUpDown>().First(n => n.Minimum == 1 && n.Maximum == 720);
            mode.SelectedIndex = 2; count.Value = 6; Pump(4);
            object refTop = null, refCircle = null;
            Retry(delegate { refTop = asm.CreateReference(occC, FaceAtZ(mc, 0.01)); }, "C 顶面引用");
            Retry(delegate { pf.AcceptPick(refTop); }, "点 C 顶面"); Pump(6);
            Retry(delegate { refCircle = asm.CreateReference(occD, CircleEdgeAt(md, 0, 0, 0)); }, "圆盘底边引用");
            Retry(delegate { pf.AcceptPick(refCircle); }, "点圆盘底边"); Pump(6);
            Retry(delegate { pf.DrillForTest(); }, "开始打孔"); Pump(8);
            L("   RESULT: " + (pf.LastResult ?? "").Replace("\r\n", " ⏎ "));
            Check((pf.LastResult ?? "").Contains("排孔完成：6 / 6") && (pf.LastResult ?? "").Contains("圆周阵列"), "排孔 6 / 6，种子孔 + 圆周阵列");
            Check(mc.Holes.Count == 1 && mc.Patterns.Count == 1, "1 个孔特征 + 1 个阵列（实 " + mc.Holes.Count + " / " + mc.Patterns.Count + "）");
            var cs = Circles(mc, 0.01).Where(h => h[2] < 10).ToList();
            L("   顶面孔心：" + Show(cs));
            Check(cs.Count == 6 && cs.All(h => Math.Abs(Math.Sqrt((h[0] - 50) * (h[0] - 50) + (h[1] - 50) * (h[1] - 50)) - 20) < 0.01), "6 个孔都在 (50,50) R20 上");
            var ang = cs.Select(h => Math.Atan2(h[1] - 50, h[0] - 50) * 180 / Math.PI).Select(a => (a + 360) % 360).OrderBy(a => a).ToList();
            Check(Enumerable.Range(1, 5).All(i => Math.Abs(ang[i] - ang[i - 1] - 60) < 0.05), "间隔 60°（实 " + string.Join(" ", ang.Select(a => N(a)).ToArray()) + "）");
            double one = Cyl(6.6, 10);
            Check(Rel(vol0 - Vol(mc), 6 * one), "切除体积 = 6 个 Φ6.6（实 " + Mm3(vol0 - Vol(mc)) + " / 期 " + Mm3(6 * one) + " mm³）");
            Retry(delegate { pf.UndoForTest(); }, "撤销"); Pump(8);
            Check(mc.Holes.Count == 0 && mc.Patterns.Count == 0 && Math.Abs(Vol(mc) - vol0) < 1e-12, "撤销：孔和阵列都删掉、体积复原");
        } finally {
            if (pf != null) try { pf.Close(); } catch { }
            try { asm.Close(false); } catch { }
            Pump(4);
        }
    }
}
