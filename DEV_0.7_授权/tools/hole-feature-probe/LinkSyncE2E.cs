using System;
using System.Collections.Generic;
using System.Globalization;
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

// 自动打孔 0.7.3 真机端到端（2026-10-13）：编组命名 + 螺纹全长 + 参考孔关联 + 同步孔位。
// 自己造夹具（新文档，落在输出目录），走插件「自动打孔」窗口的真实代码路径（AcceptPick / DrillForTest / SyncForTest）：
//   A 板 100×100×10，上面 4×Φ6.6（M6 过孔）+ 2×Φ5（M6 底孔），都是孔特征；B 板实心，在 A 上方 z+30
//   ① 点 A 顶面自动认孔 → ② 点 B 底面当打孔面 → ③ 打孔
//   ④ 核对：B 上 2 个特征组、名字、螺纹全长、6 个孔都带关联记录、切除体积
//   ⑤ 把 A 在装配里挪 (+5,-3)mm → 检查出 6 个错位 → 同步 → B 的孔全部跟到新位置
//   ⑥ 在 A 零件里把一个孔特征挪 +4mm → 检查出 1 个错位 → 同步 → B 上对应的孔跟过去
// 用法: LinkSyncE2E.exe <输出目录>
class LinkSyncE2E {
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
    static void PickUntilAccepted(AutoHoleForm form, object sel, string what){
        for (int i = 0; i < 6; i++) {
            Retry(delegate { form.AcceptPick(sel); }, what);
            Pump(8);
            string ui = form.UiSummary();
            if (ui.IndexOf("0x80010001") < 0 && ui.IndexOf("拒绝接收呼叫") < 0) return;
            L("   " + what + " 撞上 CAD 忙，重来 " + (i + 1)); Pump(12);
        }
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
    // 某张面（零件局部 z）上所有圆边的圆心（毫米，按 x,y 排序），用来核对孔在哪
    static List<string> CirclesAt(P.Model m, double z){
        var res = new List<string>();
        foreach (G.Edge e in (G.Edges)((G.Body)m.Body).get_Edges(G.FeatureTopologyQueryTypeConstants.igQueryAll)) {
            var c = e.Geometry as G.Circle; if (c == null) continue;
            Array cc = new double[3], ax = new double[3]; double r; c.GetCircleData(ref cc, ref ax, out r);
            if (Math.Abs(D(cc,2) - z) > 1e-7) continue;
            res.Add("(" + N(D(cc,0)*1000) + "," + N(D(cc,1)*1000) + ")");
        }
        res.Sort(StringComparer.Ordinal);
        return res;
    }
    static bool HasCircle(P.Model m, double z, double x, double y){
        foreach (G.Edge e in (G.Edges)((G.Body)m.Body).get_Edges(G.FeatureTopologyQueryTypeConstants.igQueryAll)) {
            var c = e.Geometry as G.Circle; if (c == null) continue;
            Array cc = new double[3], ax = new double[3]; double r; c.GetCircleData(ref cc, ref ax, out r);
            if (Math.Abs(D(cc,2) - z) < 1e-7 && Math.Abs(D(cc,0) - x) < 1e-6 && Math.Abs(D(cc,1) - y) < 1e-6) return true;
        }
        return false;
    }

    static readonly double[][] Clear = { new[]{0.02,0.02}, new[]{0.08,0.02}, new[]{0.08,0.08}, new[]{0.02,0.08} };   // Φ6.6
    static readonly double[][] Tap = { new[]{0.05,0.03}, new[]{0.05,0.07} };                                              // Φ5

    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        TianGongCadSuite.Licensing.LicenseTestHooks.GateOverride = 0;   // 开发构建才有；本机没激活，实测要真写模型
        string outDir = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
        Directory.CreateDirectory(outDir);
        try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); }
        catch (Exception e) { L("连不上 CAD：" + e.Message); return 2; }
        try { app.DisplayAlerts = false; } catch { }
        L("已连上 CAD " + app.Version);
        string stamp = DateTime.Now.ToString("HHmmss");
        string pa = Path.Combine(outDir, "LinkA_" + stamp + ".par"), pb = Path.Combine(outDir, "LinkB_" + stamp + ".par");
        A.AssemblyDocument asm = null;
        try {
            // ---- 夹具 ----
            var a = (P.PartDocument)app.Documents.Add("SolidEdge.PartDocument", Tpl + "iso metric part.par");
            a.ModelingMode = P.ModelingModeConstants.seModelingModeOrdered;
            var ma = Plate(a, 0.1, 0.1, 0.01);
            var topA = a.RefPlanes.AddParallelByDistance(FaceAtZ(ma, 0.01), 0.0, P.ReferenceElementConstants.igNormalSide, M, M, M, M);
            foreach (var c in Clear) Hole(a, ma, topA, c[0], c[1], 0.01, 0.0066);
            foreach (var c in Tap) Hole(a, ma, topA, c[0], c[1], 0.01, 0.005);
            a.SaveAs(pa); a.Close(false);
            var b = (P.PartDocument)app.Documents.Add("SolidEdge.PartDocument", Tpl + "iso metric part.par");
            b.ModelingMode = P.ModelingModeConstants.seModelingModeOrdered;
            Plate(b, 0.1, 0.1, 0.01);
            b.SaveAs(pb); b.Close(false);
            // 新建装配后 CAD 会忙一阵子，紧接着的跨进程调用可能被拒（0x80010001）：每一步都等一等、拒了就重试
            Retry(delegate { asm = (A.AssemblyDocument)app.Documents.Add("SolidEdge.AssemblyDocument", Tpl + "iso metric assembly.asm"); }, "新建装配");
            Pump(20);
            A.Occurrence occA = null, occB = null;
            Retry(delegate { occA = asm.Occurrences.AddByFilename(pa); }, "插入 A");
            Pump(10);
            Retry(delegate { occB = asm.Occurrences.AddByFilename(pb); }, "插入 B");
            Pump(10);
            Array mB = new double[]{ 1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0.03,1 };
            Retry(delegate { occB.PutMatrix(ref mB, true); }, "放置 B");
            Retry(delegate { asm.SaveAs(Path.Combine(outDir, "LinkFixture_" + stamp + ".asm")); }, "保存装配");
            Pump(20);
            L("夹具：A 板 4×Φ6.6 + 2×Φ5（孔特征），B 板实心在 A 上方 z+30");

            P.PartDocument partA = null, partB = null; P.Model modelA = null, modelB = null; double volB0 = 0;
            Retry(delegate {
                partA = (P.PartDocument)occA.OccurrenceDocument; partB = (P.PartDocument)occB.OccurrenceDocument;
                modelA = (P.Model)partA.Models.Item(1); modelB = (P.Model)partB.Models.Item(1);
                volB0 = Vol(modelB);
            }, "取实例文档");
            object refA = null, refB = null;
            Retry(delegate { refA = asm.CreateReference(occA, FaceAtZ(modelA, 0.01)); }, "A 顶面引用");
            Retry(delegate { refB = asm.CreateReference(occB, FaceAtZ(modelB, 0.0)); }, "B 底面引用");

            AutoHoleForm form = null;
            try {
                Retry(delegate { form = new AutoHoleForm(app, asm); }, "打开自动打孔窗口");
                form.SuppressDialogs = true;
                Retry(delegate { form.Show(); }, "显示窗口");
                Pump(10);
                var r0 = form.LinkReportForTest();
                Check(r0 != null && r0.Linked == 0, "打孔前装配里没有关联孔");

                L(""); L("① 点 A 的顶面（带孔的面）");
                PickUntilAccepted(form, refA, "点 A 顶面");
                string ui1 = form.UiSummary();
                L("   UI: " + ui1);
                Check(ui1.IndexOf("参考孔 2 组") >= 0, "认出 2 组参考孔（Φ6.6 / Φ5）");
                Check(ui1.IndexOf("M6 螺纹孔（螺纹全长）") >= 0, "Φ6.6 反推成 M6 螺纹孔，默认螺纹全长");

                L(""); L("② 点 B 的底面（打孔面）");
                PickUntilAccepted(form, refB, "点 B 底面");
                string ui2 = form.UiSummary();
                L("   UI: " + ui2);
                Check(ui2.IndexOf("将打 6 个孔") >= 0, "预览：将打 6 个孔");

                L(""); L("③ 开始打孔");
                Check(form.KeepLink, "默认保持与参考孔关联");
                Retry(delegate { form.DrillForTest(); }, "开始打孔");
                Pump(10);
                string res = form.LastResult ?? "";
                L("   RESULT: " + res.Replace("\r\n", " ⏎ "));
                Check(res.IndexOf("共打孔 6 个") >= 0, "共打孔 6 个");
                Check(res.IndexOf("失败") < 0, "没有失败项");
                Check(res.IndexOf("M6 螺纹孔（螺纹全长） ×4") >= 0, "结果写明特征组「M6 螺纹孔（螺纹全长） ×4」");
                Check(res.IndexOf("M6 沉孔 Φ6.6/Φ11 深6.8 ×2") >= 0, "结果写明特征组「M6 沉孔 Φ6.6/Φ11 深6.8 ×2」（沉孔尺寸取天工孔标准表）");
                Check(res.IndexOf("已关联参考孔 6 个") >= 0, "6 个孔都关联了参考孔");
                Check(res.IndexOf("自检") < 0, "自检没有报异常（孔型/螺纹深度都按要求建出来）");

                // ④ 模型核对
                L(""); L("④ 核对 B 板");
                Check(modelB.Holes.Count == 6, "B 板 6 个孔特征（实 " + modelB.Holes.Count + "）");
                var groupNames = new List<string>();
                foreach (P.FeatureGroup g in modelB.FeatureGroups) groupNames.Add(g.Name + "[" + g.Count + "]");
                L("   特征组：" + string.Join(" | ", groupNames.ToArray()));
                Check(groupNames.Contains("M6 螺纹孔（螺纹全长） ×4[4]"), "特征组「M6 螺纹孔（螺纹全长） ×4」含 4 个孔");
                Check(groupNames.Contains("M6 沉孔 Φ6.6/Φ11 深6.8 ×2[2]"), "特征组「M6 沉孔 Φ6.6/Φ11 深6.8 ×2」含 2 个孔");
                int tappedFull = 0, linked = 0, stdOk = 0; var holeNames = new List<string>(); var stdSeen = new List<string>();
                for (int i = 1; i <= modelB.Holes.Count; i++) {
                    var h = modelB.Holes.Item(i);
                    holeNames.Add(h.Name);
                    var d = (P.HoleData)h.HoleData;
                    string tdm = ""; try { tdm = d.ThreadDepthMethod.ToString(); } catch { }
                    if (h.Name.StartsWith("M6 螺纹孔") && tdm == "igThroughAll") tappedFull++;
                    if (HoleLinkStore.Read(h) != null) linked++;
                    // 天工孔标准表：标准/子类型/大小/配合 要写进去，天工自带的孔命令打开才不显示 *
                    string std = "", sub = "", size = "", fit = "";
                    try { std = d.Standard; sub = d.SubType; size = d.Size; fit = d.Fit; } catch { }
                    string key = std + "/" + sub + "/" + size + "/" + fit;
                    if (!stdSeen.Contains(key)) stdSeen.Add(key);
                    if (h.Name.StartsWith("M6 螺纹孔") && key == "GB Metric/标准螺纹/M6/") stdOk++;
                    if (h.Name.StartsWith("M6 沉孔") && key == "GB Metric/内六角螺钉/M6/普通 (H13)") stdOk++;
                }
                L("   孔标准字段：" + string.Join(" | ", stdSeen.ToArray()));
                Check(stdOk == 6, "6 个孔都按天工孔标准表写了 标准/子类型/大小/配合（实 " + stdOk + "）");
                L("   孔名：" + string.Join(" | ", holeNames.ToArray()));
                Check(holeNames.Contains("M6 螺纹孔（螺纹全长）-1") && holeNames.Contains("M6 螺纹孔（螺纹全长）-4"), "组里的孔按「规格-序号」命名");
                Check(tappedFull == 4, "4 个螺纹孔的螺纹深度方式都是全长 igThroughAll（实 " + tappedFull + "）");
                Check(linked == 6, "6 个孔特征都带关联记录（实 " + linked + "）");
                double removed = volB0 - Vol(modelB);
                double expect = 4 * Math.PI * Math.Pow(0.004917/2, 2) * 0.010
                              + 2 * (Math.PI * Math.Pow(0.0055, 2) * 0.0068 + Math.PI * Math.Pow(0.0033, 2) * 0.0032);
                Check(Math.Abs(removed - expect) / expect < 0.02, "B 板切除体积 = 4×M6 螺纹孔 + 2×M6 沉孔（实 " + N(removed*1e9) + " / 期 " + N(expect*1e9) + " mm³）");
                var r1 = form.LinkReportForTest();
                Check(r1.Linked == 6 && r1.Misplaced == 0 && r1.Broken == 0, "关联检查：6 个关联孔，全部同轴（" + r1.Summary + "）");

                // ⑤ 参考零件 A 在装配里挪 (+5,-3)mm
                L(""); L("⑤ 把 A 在装配里挪 (+5,-3)mm");
                Array mA = new double[]{ 1,0,0,0, 0,1,0,0, 0,0,1,0, 0.005,-0.003,0,1 };
                Retry(delegate { occA.PutMatrix(ref mA, true); }, "挪 A");
                Pump(10);
                var r2 = form.LinkReportForTest();
                L("   " + r2.Summary);
                Check(r2.Misplaced == 6, "挪了以后检查出 6 个错位孔（实 " + r2.Misplaced + "）");
                bool offOk = true; foreach (var it in r2.Items) if (Math.Abs(it.OffsetMm - Math.Sqrt(34)) > 1e-3) offOk = false;
                Check(offOk, "每个孔的错位量都是 √(5²+3²)=5.831mm");
                Retry(delegate { form.SyncForTest(); }, "同步孔位");
                Pump(10);
                L("   SYNC: " + (form.LastResult ?? "").Replace("\r\n", " ⏎ "));
                Check((form.LastResult ?? "").IndexOf("挪回 6 个孔") >= 0, "同步：挪回 6 个孔");
                L("   B 底面圆心：" + string.Join(" ", CirclesAt(modelB, 0.0).ToArray()));
                bool allMoved = true;
                foreach (var c in Clear) if (!HasCircle(modelB, 0.0, c[0] + 0.005, c[1] - 0.003)) allMoved = false;
                foreach (var c in Tap) if (!HasCircle(modelB, 0.0, c[0] + 0.005, c[1] - 0.003)) allMoved = false;
                Check(allMoved, "B 上 6 个孔都跟到了 (+5,-3) 的新位置");
                Check(!HasCircle(modelB, 0.0, 0.02, 0.02), "旧位置 (20,20) 已经没有孔");
                var r3 = form.LinkReportForTest();
                Check(r3.Misplaced == 0 && r3.Linked == 6, "同步后再查：全部同轴（" + r3.Summary + "）");
                Check(modelB.FeatureGroups.Count == 2 && modelB.Holes.Count == 6, "同步没有新建/删除特征（还是 2 组 6 孔）");
                double removed2 = volB0 - Vol(modelB);
                Check(Math.Abs(removed2 - removed) / removed < 1e-3, "同步后切除体积不变（只是挪位置）");

                // ⑥ 参考孔本身在 A 零件里被挪了（孔特征改位置）
                L(""); L("⑥ 在 A 零件里把 (20,20) 那个孔特征挪到 (24,20)");
                P.Hole moveMe = null;
                for (int i = 1; i <= modelA.Holes.Count; i++) {
                    var h = modelA.Holes.Item(i);
                    var prof = (P.Profile)h.Profile; double x2, y2, x, y, z;
                    prof.Holes2d.Item(1).GetCenterPoint(out x2, out y2); prof.Convert2DCoordinate(x2, y2, out x, out y, out z);
                    if (Math.Abs(x - 0.02) < 1e-6 && Math.Abs(y - 0.02) < 1e-6) { moveMe = h; break; }
                }
                Check(moveMe != null, "找到 A 上 (20,20) 的孔特征");
                if (moveMe != null) {
                    var prof = (P.Profile)moveMe.Profile; double tx, ty;
                    prof.Convert3DCoordinate(0.024, 0.02, 0.01, out tx, out ty);
                    prof.Holes2d.Item(1).SetCenterPoint(tx, ty);
                    prof.End(P.ProfileValidationType.igProfileClosed);
                    modelA.Recompute();
                    Pump(10);
                    var r4 = form.LinkReportForTest();
                    L("   " + r4.Summary);
                    Check(r4.Misplaced == 1, "检查出 1 个错位孔（参考孔在零件里被挪了）（实 " + r4.Misplaced + "）");
                    Retry(delegate { form.SyncForTest(); }, "同步孔位");
                    Pump(10);
                    L("   SYNC: " + (form.LastResult ?? "").Replace("\r\n", " ⏎ "));
                    Check(HasCircle(modelB, 0.0, 0.024 + 0.005, 0.02 - 0.003), "B 上对应的孔跟到了 (29,17)");
                    var r5 = form.LinkReportForTest();
                    Check(r5.Misplaced == 0, "再查：全部同轴（" + r5.Summary + "）");
                }
                // ⑦ 同一张面上再打第二批同规格的孔：要并进已有的「M6 螺纹孔」组，而不是再出一个「… (2)」
                L(""); L("⑦ 同一张面再打 2 个 M6 螺纹孔（第二批）");
                object refB2 = null; TargetFace tB = null;
                Retry(delegate { refB2 = asm.CreateReference(occB, FaceAtZ(modelB, 0.0)); tB = AutoHoleReader.ReadTarget(refB2); }, "B 底面引用（第二批）");
                var m6 = HoleMatcher.FromRow(HoleMatcher.Find("M6").Value, HoleKind.Tapped);
                var reqs = new List<AutoHoleWriter.HoleRequest>();
                foreach (var xy in new[]{ new[]{ 0.05, 0.05 }, new[]{ 0.05, 0.09 } })
                    reqs.Add(new AutoHoleWriter.HoleRequest { Spec = m6, Centre = tB.Placement.Point(new V3(xy[0], xy[1], 0)), Source = "第二批" });
                DrillResult r7 = null;
                Retry(delegate { r7 = AutoHoleWriter.DrillRequests(tB, reqs); }, "第二批打孔");
                L("   第二批：打孔 " + r7.Created + "，特征树：" + string.Join(" | ", r7.Features.ToArray()) + (r7.Notes.Count > 0 ? "　说明：" + string.Join("；", r7.Notes.ToArray()) : ""));
                Check(r7.Created == 2, "第二批打了 2 个孔");
                var g7 = new List<string>();
                foreach (P.FeatureGroup g in modelB.FeatureGroups) g7.Add(g.Name + "[" + g.Count + "]");
                L("   特征组：" + string.Join(" | ", g7.ToArray()));
                Check(g7.Count == 2, "还是 2 个特征组（第二批没有另起一组）");
                Check(g7.Contains("M6 螺纹孔（螺纹全长） ×6[6]"), "螺纹孔组并成「M6 螺纹孔（螺纹全长） ×6」");
                var n7 = new List<string>();
                for (int i = 1; i <= modelB.Holes.Count; i++) n7.Add(modelB.Holes.Item(i).Name);
                Check(n7.Contains("M6 螺纹孔（螺纹全长）-5") && n7.Contains("M6 螺纹孔（螺纹全长）-6"), "第二批的孔接着编号 -5、-6");

                L(""); L("UI: " + form.UiSummary());
            } finally { if (form != null) try { form.Close(); } catch {} }
            try { ((dynamic)app.ActiveWindow).View.Fit(); ((dynamic)app.ActiveWindow).View.SaveAsImage(Path.Combine(outDir, "link-sync-e2e.png"), 1400, 1000); } catch {}
        } catch (Exception e) {
            L("E2E 异常：" + e.GetType().Name + ": " + e.Message + "\r\n" + e.StackTrace);
            fail++;
        } finally {
            try { if (asm != null) asm.Close(false); } catch {}
            Pump(2);
        }
        L("");
        L(fail == 0 ? "LINK-SYNC E2E OK" : ("LINK-SYNC E2E FAILED " + fail));
        return fail == 0 ? 0 : 1;
    }
}
