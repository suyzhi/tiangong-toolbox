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

// 配孔检查 + 批量排孔 真机端到端（2026-10-13）。自己造夹具（新文档，落在输出目录），走两个窗口的真实代码路径。
//
// 配孔检查夹具（单位 mm，装配坐标）：
//   A  板 100×80×10 @z0     孔 H1 Φ6.6 @(20,40)，H2 Φ8.5 @(70,40)
//   B  板  40×80×10 @z10    没孔（盖住 H1 的轴线）              -> 应报「B 漏打孔」，且只报一条
//   C  板  40×80×10 @z20    孔 Φ6.6 @(20,40)（与 H1 同轴）
//   D  板  40×80×10 @z500   没孔（H1 轴线上，但 500mm 外）       -> 不该报
//   G  板 @x50~90 @z300     孔 Φ6.8 @(70,40)（与 H2 同轴，隔 290mm）-> 不该报配错孔
//   E  板  40×80×10 @y100~180 @z0  孔 Φ6.6 @(20,140)
//   PIN 圆柱 Φ6 长 30 @(20,140) @z-10（插在 E 的孔里）      -> 外圆不算孔；也不该报「PIN 漏打孔」
// 批量排孔：Q 板 120×60×10 @x300，沿最长边等分 4 个 M6 螺纹孔 -> 一组「…×4」；同一个面再排 2 个 -> 并成「…×6」。
// 用法: CheckPatternE2E.exe <输出目录>
class CheckPatternE2E {
    static F.Application app;
    static int fail = 0;
    static object M = Type.Missing;
    const string Tpl = @"C:\Program Files\NDS\TianGong 2025\Template\ISO Metric\";
    static void L(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    static void Check(bool ok, string label){ L((ok ? "PASS: " : "FAIL: ") + label); if (!ok) fail++; }
    static void Pump(int n){ for (int i = 0; i < n; i++) { try { app.DoIdle(); } catch { } try { Application.DoEvents(); } catch { } Thread.Sleep(60); } }
    static double D(Array a,int i){ return Convert.ToDouble(a.GetValue(i)); }
    static void Retry(Action a, string what){
        Exception last = null;
        for (int i = 0; i < 8; i++) {
            try { a(); return; }
            catch (Exception e) { last = e; L("   " + what + " 被 CAD 拒绝（0x" + e.HResult.ToString("X8") + "），重试 " + (i + 1)); Pump(8); }
        }
        throw last;
    }

    // ---- 夹具 ----
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
        part.ModelingMode = P.ModelingModeConstants.seModelingModeOrdered;
        return part;
    }
    // 局部 XY 上 [x0,x1]×[y0,y1] 的矩形，向 +Z 拉 t（米）
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
    static P.Model Pin(P.PartDocument part, double cx, double cy, double dia, double len){
        var prof = part.ProfileSets.Add().Profiles.Add(FindXY(part));
        prof.Circles2d.AddByCenterRadius(cx, cy, dia / 2);
        prof.End(P.ProfileValidationType.igProfileClosed);
        Array arr = new object[]{ prof };
        return part.Models.AddFiniteExtrudedProtrusion(1, ref arr, P.FeaturePropertyConstants.igRight, len);
    }
    static G.Face FaceAtZ(P.Model m, double z){
        foreach (G.Face f in (G.Faces)((G.Body)m.Body).get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll)) {
            var pl = f.Geometry as G.Plane; if (pl == null) continue;
            Array p = new double[3], n = new double[3]; pl.GetPlaneData(ref p, ref n);
            if (Math.Abs(Math.Abs(D(n,2)) - 1) < 1e-6 && Math.Abs(D(p,2) - z) < 1e-9) return f;
        }
        return null;
    }
    // 同一张面只能建一次基准面（第二次 AddParallelByDistance 会 E_FAIL，见 AUTO-HOLE.md），所以每个零件建一次、复用
    static readonly Dictionary<P.PartDocument, P.RefPlane> tops = new Dictionary<P.PartDocument, P.RefPlane>();
    static void Hole(P.PartDocument part, P.Model model, double x, double y, double t, double dia){
        P.RefPlane top;
        if (!tops.TryGetValue(part, out top)) { top = part.RefPlanes.AddParallelByDistance(FaceAtZ(model, t), 0.0, P.ReferenceElementConstants.igNormalSide, M, M, M, M); tops[part] = top; }
        var prof = part.ProfileSets.Add().Profiles.Add(top);
        double x2, y2; prof.Convert3DCoordinate(x, y, t, out x2, out y2);
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
    static string Save(P.PartDocument part, string dir, string name){
        string p = Path.Combine(dir, name);
        Retry(delegate { part.SaveAs(p); }, "保存 " + name);
        Retry(delegate { part.Close(false); }, "关闭 " + name);
        return p;
    }
    static A.Occurrence Place(A.AssemblyDocument asm, string path, double x, double y, double z){
        A.Occurrence occ = null;
        Retry(delegate { occ = asm.Occurrences.AddByFilename(path); }, "插入 " + Path.GetFileName(path));
        Pump(6);
        Array m = new double[]{ 1,0,0,0, 0,1,0,0, 0,0,1,0, x,y,z,1 };
        Retry(delegate { occ.PutMatrix(ref m, true); }, "放置 " + Path.GetFileName(path));
        return occ;
    }
    static List<Control> All(Control root){ var l = new List<Control>(); Collect(root, l); return l; }
    static void Collect(Control c, List<Control> all){ foreach (Control k in c.Controls) { all.Add(k); Collect(k, all); } }

    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        TianGongCadSuite.Licensing.LicenseTestHooks.GateOverride = 0;   // 开发构建才有；本机没激活，实测要真写模型
        string outDir = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
        Directory.CreateDirectory(outDir);
        try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); }
        catch (Exception e) { L("连不上 CAD：" + e.Message); return 2; }
        try { app.DisplayAlerts = false; } catch { }
        L("已连上 CAD " + app.Version);
        string st = DateTime.Now.ToString("HHmmss");
        A.AssemblyDocument asm = null;
        try {
            // ---- 夹具零件 ----
            var a = NewPart(); var ma = Plate(a, 0, 0, 0.1, 0.08, 0.01); Hole(a, ma, 0.02, 0.04, 0.01, 0.0066); Hole(a, ma, 0.07, 0.04, 0.01, 0.0085);
            string pa = Save(a, outDir, "CkA_" + st + ".par");
            var b = NewPart(); Plate(b, 0, 0, 0.04, 0.08, 0.01); string pb = Save(b, outDir, "CkB_" + st + ".par");
            var c = NewPart(); var mc = Plate(c, 0, 0, 0.04, 0.08, 0.01); Hole(c, mc, 0.02, 0.04, 0.01, 0.0066); string pc = Save(c, outDir, "CkC_" + st + ".par");
            var d = NewPart(); Plate(d, 0, 0, 0.04, 0.08, 0.01); string pd = Save(d, outDir, "CkD_" + st + ".par");
            var g = NewPart(); var mg = Plate(g, 0.05, 0, 0.09, 0.08, 0.01); Hole(g, mg, 0.07, 0.04, 0.01, 0.0068); string pg = Save(g, outDir, "CkG_" + st + ".par");
            var e = NewPart(); var me = Plate(e, 0, 0.1, 0.04, 0.18, 0.01); Hole(e, me, 0.02, 0.14, 0.01, 0.0066); string pe = Save(e, outDir, "CkE_" + st + ".par");
            var pin = NewPart(); Pin(pin, 0.02, 0.14, 0.006, 0.03); string ppin = Save(pin, outDir, "CkPIN_" + st + ".par");
            var q = NewPart(); Plate(q, 0, 0, 0.12, 0.06, 0.01); string pq = Save(q, outDir, "CkQ_" + st + ".par");
            Retry(delegate { asm = (A.AssemblyDocument)app.Documents.Add("SolidEdge.AssemblyDocument", Tpl + "iso metric assembly.asm"); }, "新建装配");
            Pump(20);
            Place(asm, pa, 0, 0, 0); var occB = Place(asm, pb, 0, 0, 0.01); Place(asm, pc, 0, 0, 0.02); var occD = Place(asm, pd, 0, 0, 0.5);
            Place(asm, pg, 0, 0, 0.3); Place(asm, pe, 0, 0, 0); var occPin = Place(asm, ppin, 0, 0, -0.01); var occQ = Place(asm, pq, 0.3, 0, 0);
            Retry(delegate { asm.SaveAs(Path.Combine(outDir, "CkFixture_" + st + ".asm")); }, "保存装配");
            Pump(20);
            string nameB = occB.Name, nameD = occD.Name, namePin = occPin.Name;
            L("夹具就绪：B=" + nameB + " D=" + nameD + " PIN=" + namePin);

            // ================= 配孔检查 =================
            L(""); L("① 配孔检查");
            AutoHoleCheckForm cf = null;
            try {
                Retry(delegate { cf = new AutoHoleCheckForm(app, asm); }, "打开配孔检查");
                Retry(delegate { cf.Show(); }, "显示");
                Pump(6);
                Retry(delegate { cf.RunCheckForTest(); }, "开始检查");
                Pump(6);
                var issues = cf.IssuesForTest;
                foreach (var i in issues) L("   [" + i.KindName + "] " + i.Message);
                L("   状态：" + cf.StatusForTest + "　结论：" + cf.SummaryText);
                var holes = cf.HolesForTest; var shafts = cf.ShaftsForTest;
                L("   采集到孔 " + holes.Count + " 条：" + string.Join(" | ", holes.Select(h => h.PartName + " Φ" + h.DiameterMm.ToString("0.###", CultureInfo.InvariantCulture)
                      + " 轴向 " + ((h.AxialMax - h.AxialMin) * 1000).ToString("0.#", CultureInfo.InvariantCulture) + "mm").ToArray()));
                L("   轴/销 " + shafts.Count + " 条：" + string.Join(" | ", shafts.Select(h => h.PartName + " Φ" + h.DiameterMm.ToString("0.##", CultureInfo.InvariantCulture)).ToArray()));
                var missing = issues.Where(i => i.Kind == HoleIssueKind.MissingHole).ToList();
                Check(missing.Count == 1, "只报 1 条漏打孔（实 " + missing.Count + "）");
                Check(missing.Count == 1 && missing[0].Message.Contains("穿过了 " + nameB), "漏打孔说的是夹在中间的 B");
                Check(missing.Count == 1 && missing[0].Holes.Select(h => h.PartName).Distinct().Count() == 2, "这条漏打孔合并了 A、C 两边的孔");
                Check(!issues.Any(i => i.Message.Contains(nameD)), "500mm 外的 D 不再被当成漏打孔");
                Check(!issues.Any(i => i.Message.Contains(namePin)), "插着销的孔：不报「销上漏打孔」");
                Check(!issues.Any(i => i.Kind == HoleIssueKind.WrongSpec), "同一直线上隔 290mm 的 Φ8.5 / Φ6.8 不再报配错孔");
                Check(!issues.Any(i => i.Kind == HoleIssueKind.Misaligned), "没有孔偏了");
                Check(!holes.Any(h => h.PartName == namePin), "销的外圆不算孔");
                Check(shafts.Any(h => h.PartName == namePin && Math.Abs(h.DiameterMm - 6) < 0.01), "销记成了轴/销（Φ6）");
                Check(holes.Where(h => h.DiameterMm > 3).All(h => Math.Abs((h.AxialMax - h.AxialMin) - 0.01) < 1e-6), "每个通孔的上下两条边合并成 10mm 的轴向范围");
                Check(issues.Count(i => i.Kind == HoleIssueKind.Unpaired) >= 3, "H2、G、E 的孔是孤孔（默认不显示）");
            } finally { if (cf != null) try { cf.Close(); } catch { } }

            // ================= 批量排孔 =================
            L(""); L("② 批量排孔：Q 板沿最长边等分 4 个 M6 螺纹孔");
            var partQ = (P.PartDocument)occQ.OccurrenceDocument;
            var modelQ = (P.Model)partQ.Models.Item(1);
            double vol0 = ((G.Body)modelQ.Body).Volume;
            AutoHolePatternForm pf = null;
            try {
                Retry(delegate { pf = new AutoHolePatternForm(app, asm); }, "打开批量排孔");
                pf.SuppressDialogs = true;
                pf.AsPattern = false;   // 这里专门测"逐个打孔"那条路（编组、并组、接着编号）；种子孔 + 阵列在 EditPatternE2E
                Retry(delegate { pf.Show(); }, "显示");
                Pump(6);
                var all = All(pf);
                var kind = all.OfType<Segmented>().First(s => s.Items.Contains("螺纹孔"));
                var count = all.OfType<NumericUpDown>().First(n => n.Minimum == 1 && n.Maximum == 720);
                var edge = all.OfType<NumericUpDown>().First(n => n.Minimum == 0 && n.Maximum == 5000);
                object refQ = null;
                Retry(delegate { refQ = asm.CreateReference(occQ, FaceAtZ(modelQ, 0.01)); }, "Q 顶面引用");
                Retry(delegate { pf.AcceptPick(refQ); }, "点 Q 顶面");
                Pump(6);
                kind.SelectedIndex = 1; count.Value = 4; edge.Value = 10;
                Pump(4);
                Retry(delegate { pf.DrillForTest(); }, "开始打孔");
                Pump(8);
                L("   RESULT: " + (pf.LastResult ?? "").Replace("\r\n", " ⏎ "));
                Check((pf.LastResult ?? "").Contains("排孔完成：4 / 4"), "排孔 4 / 4");
                Check((pf.LastResult ?? "").Contains("M6 螺纹孔（螺纹全长） ×4"), "结果写明特征组「M6 螺纹孔（螺纹全长） ×4」");
                var g1 = new List<string>(); foreach (P.FeatureGroup fg in modelQ.FeatureGroups) g1.Add(fg.Name + "[" + fg.Count + "]");
                Check(g1.Contains("M6 螺纹孔（螺纹全长） ×4[4]"), "Q 上一个特征组「…×4」含 4 个孔（实 " + string.Join(" | ", g1.ToArray()) + "）");
                int stdOk = 0, full = 0;
                for (int i = 1; i <= modelQ.Holes.Count; i++) {
                    var hd = (P.HoleData)modelQ.Holes.Item(i).HoleData;
                    if (hd.Standard == "GB Metric" && hd.SubType == "标准螺纹" && hd.Size == "M6") stdOk++;
                    if (hd.ThreadDepthMethod.ToString() == "igThroughAll") full++;
                }
                Check(stdOk == 4 && full == 4, "4 个孔都是天工标准孔（GB Metric/标准螺纹/M6）、螺纹全长（实 " + stdOk + "/" + full + "）");

                L(""); L("③ 同一个面接着再排 2 个（边距 25）");
                object refQ2 = null;
                Retry(delegate { refQ2 = asm.CreateReference(occQ, FaceAtZ(modelQ, 0.01)); }, "Q 顶面引用（第二批）");
                Retry(delegate { pf.AcceptPick(refQ2); }, "再点 Q 顶面");
                Pump(6);
                count.Value = 2; edge.Value = 25;
                Pump(4);
                Retry(delegate { pf.DrillForTest(); }, "第二批打孔");
                Pump(8);
                L("   RESULT: " + (pf.LastResult ?? "").Replace("\r\n", " ⏎ "));
                Check((pf.LastResult ?? "").Contains("排孔完成：2 / 2"), "第二批排孔 2 / 2（再点同一个面不再报错）");
                var g2 = new List<string>(); foreach (P.FeatureGroup fg in modelQ.FeatureGroups) g2.Add(fg.Name + "[" + fg.Count + "]");
                L("   特征组：" + string.Join(" | ", g2.ToArray()));
                Check(g2.Count == 1 && g2.Contains("M6 螺纹孔（螺纹全长） ×6[6]"), "并成一个组「M6 螺纹孔（螺纹全长） ×6」");
                var names = new List<string>(); for (int i = 1; i <= modelQ.Holes.Count; i++) names.Add(modelQ.Holes.Item(i).Name);
                Check(names.Contains("M6 螺纹孔（螺纹全长）-5") && names.Contains("M6 螺纹孔（螺纹全长）-6"), "第二批接着编号 -5、-6");
                double cut = vol0 - ((G.Body)modelQ.Body).Volume, want = 6 * Math.PI * Math.Pow(0.004917 / 2, 2) * 0.01;
                Check(Math.Abs(cut - want) / want < 0.01, "Q 切除体积 = 6 个 M6 螺纹孔（实 " + (cut * 1e9).ToString("0.#") + " / 期 " + (want * 1e9).ToString("0.#") + " mm³）");
            } finally { if (pf != null) try { pf.Close(); } catch { } }
        } catch (Exception ex) {
            L("E2E 异常：" + ex.GetType().Name + ": " + ex.Message + "\r\n" + ex.StackTrace);
            fail++;
        } finally {
            try { if (asm != null) asm.Close(false); } catch { }
            Pump(2);
        }
        L("");
        L(fail == 0 ? "CHECK-PATTERN E2E OK" : ("CHECK-PATTERN E2E FAILED " + fail));
        return fail == 0 ? 0 : 1;
    }
}
