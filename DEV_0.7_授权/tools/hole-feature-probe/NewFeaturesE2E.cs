using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using TianGongCadSuite;
using A=SolidEdgeAssembly;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;
using F=SolidEdgeFramework;
using S=SolidEdgeFrameworkSupport;

// 0.7.5 新功能真机端到端（2026-10-14）。自己造夹具（新文档，落在输出目录），走窗口的真实代码路径。
//   ① 自动打孔（规则「螺栓 + 螺母」）：A 顶面 3 个 Φ6.6 孔 → B 底面；结果列表逐孔带位置、跳转到孔；
//   ② 另一个窗口从 D 再配 1 个同规格孔到 B 同一张面 → 并进 ×4 → 撤销 → B 回到 ×3（组名、成员名原样）→ 第一个窗口撤销 → B 没孔、体积复原；
//   ③ 批量排孔：起端 10 / 末端 30、离方向边 15mm，3 个孔的位置；撤销；
//   ④ 配孔检查：漏打孔的零件在子装配里 → 能报出来，MissingPart 是「子装配/零件」；「去配孔」带入参考孔；
//   ⑤ 型材自动填充：两个互相垂直的框架一起选 → 2 个平面各 1 个框口 → 生成 → 零件属性里有尺寸、材质。
// 用法: NewFeaturesE2E.exe <输出目录>
class NewFeaturesE2E {
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
    static G.Face FaceAtZ(P.Model m, double z){
        foreach (G.Face f in (G.Faces)((G.Body)m.Body).get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll)) {
            var pl = f.Geometry as G.Plane; if (pl == null) continue;
            Array p = new double[3], n = new double[3]; pl.GetPlaneData(ref p, ref n);
            if (Math.Abs(Math.Abs(D(n,2)) - 1) < 1e-6 && Math.Abs(D(p,2) - z) < 1e-9) return f;
        }
        return null;
    }
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
    // 0.7.9 起同规格孔合成「多孔孔特征」（UserDefinedPattern，输入孔在它里面），不再是特征组 —— 两种都列出来
    static List<string> Groups(P.Model m){
        var l = new List<string>();
        foreach (P.FeatureGroup g in m.FeatureGroups) l.Add(g.Name + "[" + g.Count + "]");
        int n = 0; try { n = m.UserDefinedPatterns.Count; } catch { }
        for (int i = 1; i <= n; i++) { try { var u = m.UserDefinedPatterns.Item(i); l.Add(u.Name + "[" + u.NumberInputFeatures + "]"); } catch { } }
        return l;
    }
    // 多孔特征里的输入孔 Holes.Item 会抛 E_POINTER（真机 2026-10-22），读不到的跳过
    static List<string> HoleNames(P.Model m){ var l = new List<string>(); for (int i = 1; i <= m.Holes.Count; i++) { try { l.Add(m.Holes.Item(i).Name); } catch { } } l.Sort(StringComparer.Ordinal); return l; }
    static P.HoleData FirstHoleData(P.Model m){
        for (int i = 1; i <= m.Holes.Count; i++) { try { return (P.HoleData)m.Holes.Item(i).HoleData; } catch { } }
        int n = 0; try { n = m.UserDefinedPatterns.Count; } catch { }
        for (int i = 1; i <= n; i++) {
            try {
                Array feats = new object[0];
                m.UserDefinedPatterns.Item(i).GetInputFeatures(ref feats);
                foreach (object f in feats) { var h = f as P.Hole; if (h != null) return (P.HoleData)h.HoleData; }
            } catch { }
        }
        return null;
    }
    // 零件局部坐标下，某个 z 平面上的圆心（毫米）
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
    static void PickUntilAccepted(AutoHoleForm form, object sel, string what){
        for (int i = 0; i < 6; i++) {
            Retry(delegate { form.AcceptPick(sel); }, what);
            Pump(8);
            string ui = form.UiSummary();
            if (ui.IndexOf("0x80010001") < 0 && ui.IndexOf("拒绝接收呼叫") < 0) return;
            L("   " + what + " 撞上 CAD 忙，重来 " + (i + 1)); Pump(12);
        }
    }

    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        TianGongCadSuite.Licensing.LicenseTestHooks.GateOverride = 0;   // 开发构建才有；实测要真写模型
        string outDir = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
        Directory.CreateDirectory(outDir);
        try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); }
        catch (Exception e) { L("连不上 CAD：" + e.Message); return 2; }
        try { app.DisplayAlerts = false; } catch { }
        L("已连上 CAD " + app.Version + "，插件 DLL " + Product.Version);
        string st = DateTime.Now.ToString("HHmmss");
        // 型材填充会把输出目录记进用户的 autopanel.tsv：测试完恢复原样，别改了用户的设置
        string panelSettings = SettingsFile.PathOf("autopanel.tsv");
        byte[] panelSettingsBefore = File.Exists(panelSettings) ? File.ReadAllBytes(panelSettings) : null;
        var savedRules = HoleRuleBook.Current;
        try {
            Holes(outDir, st);
            Pattern(outDir, st);
            CheckSub(outDir, st);
            Panels(outDir, st);
        } catch (Exception ex) {
            L("E2E 异常：" + ex.GetType().Name + ": " + ex.Message + "\r\n" + ex.StackTrace);
            fail++;
        } finally {
            HoleRuleBook.Current = savedRules;
            try { if (panelSettingsBefore == null) { if (File.Exists(panelSettings)) File.Delete(panelSettings); } else File.WriteAllBytes(panelSettings, panelSettingsBefore); } catch { }
        }
        L("");
        L(fail == 0 ? "NEW-FEATURES E2E OK" : ("NEW-FEATURES E2E FAILED " + fail));
        return fail == 0 ? 0 : 1;
    }

    // ================= ①② 自动打孔 + 撤销 =================
    static void Holes(string outDir, string st){
        L(""); L("① 自动打孔（规则：螺栓 + 螺母）");
        HoleRuleBook.Current = HoleRuleBook.Presets[3];
        var a = NewPart(); var ma = Plate(a, 0, 0, 0.1, 0.06, 0.01);
        Hole(a, ma, 0.02, 0.03, 0.01, 0.0066); Hole(a, ma, 0.05, 0.03, 0.01, 0.0066); Hole(a, ma, 0.08, 0.03, 0.01, 0.0066);
        string pa = Save(a, outDir, "UaA_" + st + ".par");
        var b = NewPart(); Plate(b, 0, 0, 0.1, 0.06, 0.01); string pb = Save(b, outDir, "UaB_" + st + ".par");
        var d = NewPart(); var md = Plate(d, 0, 0, 0.1, 0.06, 0.01); Hole(d, md, 0.035, 0.015, 0.01, 0.0066); string pd = Save(d, outDir, "UaD_" + st + ".par");
        var asm = NewAsm();
        try {
            var occA = Place(asm, pa, At(0, 0, 0)); var occB = Place(asm, pb, At(0, 0, 0.01)); var occD = Place(asm, pd, At(0, 0, -0.02));
            Retry(delegate { asm.SaveAs(Path.Combine(outDir, "UaFixture_" + st + ".asm")); }, "保存装配");
            Pump(20);
            P.Model modelA = null, modelB = null, modelD = null;
            Retry(delegate {
                modelA = (P.Model)((P.PartDocument)occA.OccurrenceDocument).Models.Item(1);
                modelB = (P.Model)((P.PartDocument)occB.OccurrenceDocument).Models.Item(1);
                modelD = (P.Model)((P.PartDocument)occD.OccurrenceDocument).Models.Item(1);
            }, "取实例文档");
            double volB0 = Vol(modelB);

            AutoHoleForm f1 = null, f2 = null;
            try {
                Retry(delegate { f1 = new AutoHoleForm(app, asm); }, "打开自动打孔");
                f1.SuppressDialogs = true; f1.KeepLink = false;
                Retry(delegate { f1.Show(); }, "显示"); Pump(10);
                object refA = null, refB = null;
                Retry(delegate { refA = asm.CreateReference(occA, FaceAtZ(modelA, 0.01)); refB = asm.CreateReference(occB, FaceAtZ(modelB, 0.0)); }, "引用");
                PickUntilAccepted(f1, refA, "点 A 顶面");
                PickUntilAccepted(f1, refB, "点 B 底面");
                L("   UI: " + f1.UiSummary());
                Retry(delegate { f1.DrillForTest(); }, "开始打孔"); Pump(10);
                string res = f1.LastResult ?? "";
                L("   RESULT: " + res.Replace("\r\n", " ⏎ "));
                Check(res.Contains("共打孔 3 个") && !res.Contains("失败"), "打孔 3 个，没有失败");
                var items = f1.ResultItemsForTest;
                L("   结果列表：" + string.Join(" | ", items.Select(i => i.Kind + ":" + i.Text + (i.HasWhere ? "@" : "")).ToArray()));
                Check(items.Count(i => i.Kind == ResultList.Kind.Done && i.HasWhere) == 3, "结果列表 3 条「打好的孔」，都带位置");
                var first = items.First(i => i.Kind == ResultList.Kind.Done);
                Check(Math.Abs(first.Where.Z - 0.01) < 1e-6, "带的位置是装配坐标（孔心在 B 底面 z=10mm）");
                bool zoomed = false;
                Retry(delegate { zoomed = CadView.ZoomToHole(app, first.Where, first.DiameterMm); }, "跳转到孔");
                Check(zoomed, "跳转到孔：视图镜头移动成功");
                var holesB = Circles(modelB, 0.0);
                Check(holesB.Count == 3 && holesB.All(h => Math.Abs(h[2] - 6.6) < 0.01), "螺栓 + 螺母规则：B 上配的是 Φ6.6 过孔（实 " + string.Join(" ", holesB.Select(h => "Φ" + N(h[2])).ToArray()) + "）");
                var hd = FirstHoleData(modelB);
                Check(hd != null && hd.Standard == "GB Metric" && hd.SubType == "常规螺钉间隙" && hd.Size == "M6", "过孔是天工标准孔（GB Metric / 常规螺钉间隙 / M6，实 " + hd.Standard + "/" + hd.SubType + "/" + hd.Size + "）");
                var groups1 = Groups(modelB); var names1 = HoleNames(modelB); double volB1 = Vol(modelB);
                L("   B 特征组：" + string.Join(" | ", groups1.ToArray()) + "　孔：" + string.Join(" | ", names1.ToArray()));
                Check(groups1.Count == 1 && groups1[0].EndsWith("[3]"), "B 上 3 个孔合成一个多孔特征 / 编成一组");
                Check(f1.CanUndo, "可以撤销");

                L(""); L("② 第二个窗口：从 D 再配 1 个到 B 同一张面 → 并组 → 撤销");
                Retry(delegate { f2 = new AutoHoleForm(app, asm); }, "打开第二个自动打孔");
                f2.SuppressDialogs = true; f2.KeepLink = false;
                Retry(delegate { f2.Show(); }, "显示"); Pump(10);
                object refD = null, refB2 = null;
                Retry(delegate { refD = asm.CreateReference(occD, FaceAtZ(modelD, 0.01)); refB2 = asm.CreateReference(occB, FaceAtZ(modelB, 0.0)); }, "引用");
                PickUntilAccepted(f2, refD, "点 D 顶面");
                // B 底面上已经有第一批的孔了：勾着「点平面时自动识别面上的孔」会把它当参考面，要当打孔面就得先勾掉
                All(f2).OfType<CheckBox>().First(cb => cb.Text == "点平面时自动识别面上的孔").Checked = false;
                PickUntilAccepted(f2, refB2, "点 B 底面");
                L("   UI: " + f2.UiSummary());
                Retry(delegate { f2.DrillForTest(); }, "第二批打孔"); Pump(10);
                L("   RESULT: " + (f2.LastResult ?? "").Replace("\r\n", " ⏎ "));
                var groups2 = Groups(modelB);
                L("   B 特征组：" + string.Join(" | ", groups2.ToArray()) + "　孔：" + string.Join(" | ", HoleNames(modelB).ToArray()));
                Check(Circles(modelB, 0.0).Count == 4, "第二批打上：B 底面共 4 个孔（实 " + Circles(modelB, 0.0).Count + "）");
                Retry(delegate { f2.UndoForTest(); }, "撤销第二批"); Pump(10);
                L("   撤销：" + (f2.LastResult ?? "").Replace("\r\n", " ⏎ "));
                var groups3 = Groups(modelB); var names3 = HoleNames(modelB);
                L("   B 特征组：" + string.Join(" | ", groups3.ToArray()) + "　孔：" + string.Join(" | ", names3.ToArray()));
                Check(groups3.SequenceEqual(groups1), "撤销后特征组和第一批一样（组名、成员数）");
                Check(names3.SequenceEqual(names1), "撤销后孔名和第一批一样");
                Check(Math.Abs(Vol(modelB) - volB1) < 1e-12, "撤销后 B 体积回到第一批打完时");
                Check(!f2.CanUndo, "撤销过一次就不能再撤");

                Retry(delegate { f1.UndoForTest(); }, "撤销第一批"); Pump(10);
                L("   撤销：" + (f1.LastResult ?? "").Replace("\r\n", " ⏎ "));
                Check(modelB.Holes.Count == 0 && Groups(modelB).Count == 0, "第一批也撤了：B 上没有孔、没有组 / 多孔特征");
                Check(Math.Abs(Vol(modelB) - volB0) < 1e-12, "B 体积复原");
            } finally {
                if (f2 != null) try { f2.Close(); } catch { }
                if (f1 != null) try { f1.Close(); } catch { }
            }
        } finally { try { asm.Close(false); } catch { } Pump(4); HoleRuleBook.Current = HoleRuleBook.Presets[0]; }
    }

    // ================= ③ 批量排孔 =================
    static void Pattern(string outDir, string st){
        L(""); L("③ 批量排孔：起端 10 / 末端 30，离方向边 15mm，等分 3 个 M6 过孔");
        var q = NewPart(); Plate(q, 0, 0, 0.12, 0.06, 0.01); string pq = Save(q, outDir, "UpQ_" + st + ".par");
        var asm = NewAsm();
        AutoHolePatternForm pf = null;
        try {
            var occQ = Place(asm, pq, At(0, 0, 0));
            Retry(delegate { asm.SaveAs(Path.Combine(outDir, "UpFixture_" + st + ".asm")); }, "保存装配");
            Pump(10);
            P.Model mq = null;
            Retry(delegate { mq = (P.Model)((P.PartDocument)occQ.OccurrenceDocument).Models.Item(1); }, "取 Q");
            double vol0 = Vol(mq);
            Retry(delegate { pf = new AutoHolePatternForm(app, asm); }, "打开批量排孔");
            pf.SuppressDialogs = true;
            Retry(delegate { pf.Show(); }, "显示"); Pump(6);
            var all = All(pf);
            var count = all.OfType<NumericUpDown>().First(n => n.Minimum == 1 && n.Maximum == 720);
            var big = all.OfType<NumericUpDown>().Where(n => n.Minimum == 0 && n.Maximum == 5000).ToList();   // 起端、末端、横向
            var same = all.OfType<CheckBox>().First(cb => cb.Text == "两端相同");
            var lat = all.OfType<Segmented>().First(s => s.Items.Contains("离方向边"));
            object refQ = null;
            Retry(delegate { refQ = asm.CreateReference(occQ, FaceAtZ(mq, 0.01)); }, "Q 顶面引用");
            Retry(delegate { pf.AcceptPick(refQ); }, "点 Q 顶面"); Pump(6);
            count.Value = 3; same.Checked = false; big[0].Value = 10; big[1].Value = 30; lat.SelectedIndex = 1; big[2].Value = 15;
            Pump(4);
            Retry(delegate { pf.DrillForTest(); }, "开始打孔"); Pump(8);
            L("   RESULT: " + (pf.LastResult ?? "").Replace("\r\n", " ⏎ "));
            Check((pf.LastResult ?? "").Contains("排孔完成：3 / 3"), "排孔 3 / 3");
            var c = Circles(mq, 0.01).OrderBy(h => h[0]).ToList();
            L("   顶面孔心：" + string.Join(" ", c.Select(h => "(" + N(h[0]) + "," + N(h[1]) + ")Φ" + N(h[2])).ToArray()));
            var xs = c.Select(h => h[0]).ToArray();
            bool fromLeft = xs.Length == 3 && Math.Abs(xs[0] - 10) < 0.01 && Math.Abs(xs[1] - 50) < 0.01 && Math.Abs(xs[2] - 90) < 0.01;
            bool fromRight = xs.Length == 3 && Math.Abs(xs[0] - 30) < 0.01 && Math.Abs(xs[1] - 70) < 0.01 && Math.Abs(xs[2] - 110) < 0.01;
            Check(fromLeft || fromRight, "两端边距 10 / 30：孔心 x = 10/50/90（或从另一头量 30/70/110）");
            Check(c.Count == 3 && c.All(h => Math.Abs(h[1] - 15) < 0.01) || c.Count == 3 && c.All(h => Math.Abs(h[1] - 45) < 0.01), "离方向边 15mm：孔心 y = 15（方向边在 y=0）或 45（方向边在 y=60）");
            Check(pf.CanUndo, "可以撤销");
            Retry(delegate { pf.UndoForTest(); }, "撤销"); Pump(8);
            Check(mq.Holes.Count == 0 && Math.Abs(Vol(mq) - vol0) < 1e-12, "撤销后 Q 没有孔、体积复原");
        } finally {
            if (pf != null) try { pf.Close(); } catch { }
            try { asm.Close(false); } catch { }
            Pump(4);
        }
    }

    // ================= ④ 配孔检查：漏打孔的零件在子装配里 =================
    static void CheckSub(string outDir, string st){
        L(""); L("④ 配孔检查：A、C 有孔，夹在中间的 B 在子装配里、没孔");
        var a = NewPart(); var ma = Plate(a, 0, 0, 0.06, 0.06, 0.01); Hole(a, ma, 0.02, 0.03, 0.01, 0.0066); string pa = Save(a, outDir, "UcA_" + st + ".par");
        var b = NewPart(); Plate(b, 0, 0, 0.06, 0.06, 0.01); string pb = Save(b, outDir, "UcB_" + st + ".par");
        var c = NewPart(); var mc = Plate(c, 0, 0, 0.06, 0.06, 0.01); Hole(c, mc, 0.02, 0.03, 0.01, 0.0066); string pc = Save(c, outDir, "UcC_" + st + ".par");
        var sub = NewAsm();
        Place(sub, pb, At(0, 0, 0));
        string psub = Path.Combine(outDir, "UcSub_" + st + ".asm");
        Retry(delegate { sub.SaveAs(psub); }, "保存子装配");
        Retry(delegate { sub.Close(false); }, "关闭子装配");
        var asm = NewAsm();
        AutoHoleCheckForm cf = null; AutoHoleForm hf = null;
        try {
            Place(asm, pa, At(0, 0, 0)); Place(asm, psub, At(0, 0, 0.01)); Place(asm, pc, At(0, 0, 0.02));
            Retry(delegate { asm.SaveAs(Path.Combine(outDir, "UcFixture_" + st + ".asm")); }, "保存装配");
            Pump(20);
            Retry(delegate { cf = new AutoHoleCheckForm(app, asm); }, "打开配孔检查");
            Retry(delegate { cf.Show(); }, "显示"); Pump(6);
            Retry(delegate { cf.RunCheckForTest(); }, "开始检查"); Pump(6);
            var issues = cf.IssuesForTest;
            foreach (var i in issues) L("   [" + i.KindName + "] " + i.Message + (i.MissingPart.Length > 0 ? "　MissingPart=" + i.MissingPart : ""));
            L("   状态：" + cf.StatusForTest);
            var miss = issues.Where(i => i.Kind == HoleIssueKind.MissingHole).ToList();
            Check(miss.Count == 1, "报 1 条漏打孔（实 " + miss.Count + "）");
            Check(miss.Count == 1 && miss[0].MissingPart.Contains("/"), "没孔的是子装配里的零件（名字写成「子装配/零件」）");
            Check(cf.StatusForTest.Contains("含子装配里的 1 个零件"), "状态栏说明检查了子装配里的零件");
            if (miss.Count == 1) {
                Retry(delegate { hf = new AutoHoleForm(app, asm); }, "打开自动打孔（去配孔）");
                hf.SuppressDialogs = true;
                Retry(delegate { hf.Show(); }, "显示"); Pump(6);
                hf.PreloadReferences(miss[0].Holes, miss[0].MissingPart);
                Pump(4);
                L("   UI: " + hf.UiSummary());
                Check(hf.UiSummary().Contains("参考孔 1 组"), "去配孔：漏打孔的孔带进自动打孔当参考孔（同轴同径归一组）");
            }
        } finally {
            if (hf != null) try { hf.Close(); } catch { }
            if (cf != null) try { cf.Close(); } catch { }
            try { asm.Close(false); } catch { }
            Pump(4);
        }
    }

    // ================= ⑤ 型材自动填充：两个垂直平面 =================
    static void Panels(string outDir, string st){
        L(""); L("⑤ 型材自动填充：XY 平面一个框 + YZ 平面一个框，一起选");
        // 四根型材（截面 20×20），围成 500×300 的框口；同一个零件文件插两次就是"两根同样的型材"
        var lr = NewPart(); Plate(lr, 0, 0, 0.02, 0.3, 0.02); string plr = Save(lr, outDir, "UfV_" + st + ".par");      // 竖：20×300
        var tb = NewPart(); Plate(tb, 0, 0, 0.54, 0.02, 0.02); string ptb = Save(tb, outDir, "UfH_" + st + ".par");    // 横：540×20
        var asm = NewAsm();
        AutoPanelForm form = null;
        try {
            var occs = new List<A.Occurrence>();
            // 框 1：XY 平面，厚度方向 z ∈ [-10,10]
            occs.Add(Place(asm, plr, At(-0.02, 0, -0.01))); occs.Add(Place(asm, plr, At(0.5, 0, -0.01)));
            occs.Add(Place(asm, ptb, At(-0.02, -0.02, -0.01))); occs.Add(Place(asm, ptb, At(-0.02, 0.3, -0.01)));
            // 框 2：把框 1 绕 Y 转 90° 放到 x=2m（x→z、z→-x），在 YZ 平面里
            Func<double, double, double, double[]> rot = (x, y, z) => new double[]{ 0,0,1,0, 0,1,0,0, -1,0,0,0, x,y,z,1 };
            occs.Add(Place(asm, plr, rot(2.01, 0, 0.02))); occs.Add(Place(asm, plr, rot(2.01, 0, -0.5)));
            // 转过来以后零件的 x 对应装配的 z：横梁要从 z=-0.5 伸到 z=+0.04，才和两根竖梁（z∈[-0.5,-0.48]、[0.02,0.04]）接上
            occs.Add(Place(asm, ptb, rot(2.01, -0.02, -0.5))); occs.Add(Place(asm, ptb, rot(2.01, 0.3, -0.5)));
            Retry(delegate { asm.SaveAs(Path.Combine(outDir, "UfFixture_" + st + ".asm")); }, "保存装配");
            Pump(20);
            int before = asm.Occurrences.Count;
            Retry(delegate { asm.SelectSet.RemoveAll(); foreach (var o in occs) asm.SelectSet.Add(o); }, "选中 8 根型材");
            Pump(4);
            Retry(delegate { form = new AutoPanelForm(app, asm); }, "打开型材自动填充");
            Retry(delegate { form.Show(); }, "显示"); Pump(6);
            typeof(AutoPanelForm).GetMethod("SetParameters", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(form, new object[]{ "6", "1" }); Pump(4);
            L("   状态：" + PanelStatus(form));
            Check(GetInt(form, "OpeningCount") == 2, "识别到 2 个框口（实 " + GetInt(form, "OpeningCount") + "）");
            Check(PanelStatus(form).Contains("2 个平面"), "两个框口在两个平面上");
            var mat = All(form).OfType<ComboBox>().First(cb => cb.DropDownStyle == ComboBoxStyle.DropDown);
            mat.Text = "玻璃";
            Retry(delegate { typeof(AutoPanelForm).GetMethod("Generate", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(form, null); }, "生成");
            Pump(10);
            L("   状态：" + PanelStatus(form));
            Check(PanelStatus(form).StartsWith("已生成 2 块板子"), "生成 2 块板子");
            Check(asm.Occurrences.Count == before + 2, "装配里多了 2 个实例");
            for (int i = before + 1; i <= asm.Occurrences.Count; i++) {
                var occ = asm.Occurrences.Item(i);
                var part = (P.PartDocument)occ.OccurrenceDocument;
                dynamic custom = ((dynamic)part.Properties).Item("Custom");
                string w = "", h = "", t = "", m = "";
                try { w = custom.Item("宽度mm").Value; } catch { }
                try { h = custom.Item("高度mm").Value; } catch { }
                try { t = custom.Item("厚度mm").Value; } catch { }
                try { m = custom.Item("材质").Value; } catch { }
                L("   " + occ.Name + "：宽 " + w + " 高 " + h + " 厚 " + t + " 材质 " + m);
                var dims = new[]{ w, h }.OrderBy(x => x).ToArray();
                Check(dims[0] == "298" && dims[1] == "498" && t == "6" && m == "玻璃", occ.Name + " 的自定义属性：498×298×6、材质 玻璃");
                double x0, y0, z0, x1, y1, z1;
                occ.Range(out x0, out y0, out z0, out x1, out y1, out z1);
                L("   " + occ.Name + " 包围盒 (" + N(x0*1000) + "," + N(y0*1000) + "," + N(z0*1000) + ")-(" + N(x1*1000) + "," + N(y1*1000) + "," + N(z1*1000) + ")");
            }
            Check(form.Visible, "生成完窗口还开着");
        } finally {
            if (form != null) try { form.Close(); } catch { }
            try { asm.Close(false); } catch { }
            Pump(4);
        }
    }
    static string PanelStatus(AutoPanelForm f){ return (string)typeof(AutoPanelForm).GetProperty("StatusText", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(f, null); }
    static int GetInt(object o, string prop){
        return (int)o.GetType().GetProperty(prop, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public).GetValue(o, null);
    }
}
