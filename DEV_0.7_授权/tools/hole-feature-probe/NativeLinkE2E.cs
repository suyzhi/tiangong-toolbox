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

// 0.7.8 天工原生关联真机端到端：走「自动打孔」窗口的真实代码路径（AcceptPick / DrillForTest / SyncForTest / UndoForTest）。
//   场景一（顶层）：A 板 4×Φ6.6 + 2×Φ5（孔特征），B 板实心在 A 上方 z+30
//     ① 点 A 顶面认孔 → ② 点 B 底面 → ③ 打孔：6 个孔都挂上原生关联，B 里一个「孔位链接-…」零件间复制、HasInterpartLinks
//     ④ 挪 A (+5,-3)mm → 「同步孔位」（= 更新链接）→ B 的 6 个孔都跟过去，切除体积不变
//     ⑤ 同一张面再打一批（第二个参考件 D 上的 2 个 Φ6.6）→ 并进已有的组
//     ⑥ 撤销第二批 → 孔和它的零件间复制都没了，第一批不受影响
//   场景二（子装配）：S.asm 里是 A2（同上的孔），T.asm 里是 B2；顶层 = S + T(z+30)
//     ⑦ 点 A2 顶面 → 点 B2 底面 → 打孔：两级原位编辑、原生关联 6 个
//     ⑧ 挪 S (+4,0) → 同步 → B2 的孔跟过去
// 用法: NativeLinkE2E.exe <输出目录> [keep]   （keep：最后不关装配，留给截图看特征树）
class NativeLinkE2E {
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

    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        TianGongCadSuite.Licensing.LicenseTestHooks.GateOverride = 0;
        string outDir = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
        bool keep = args.Length > 1 && args[1] == "keep";
        Directory.CreateDirectory(outDir);
        try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); }
        catch (Exception e) { L("连不上 CAD：" + e.Message); return 2; }
        try { app.DisplayAlerts = false; } catch { }
        L("已连上 CAD " + app.Version);
        string stamp = DateTime.Now.ToString("HHmmss");
        A.AssemblyDocument asm = null, top2 = null;
        try {
            TopLevel(outDir, stamp, out asm);
            if (asm != null && !keep) { try { asm.Close(true); } catch { } asm = null; }
            Pump(10);
            Nested(outDir, stamp, out top2);
        } catch (Exception e) {
            L("E2E 异常：" + e.GetType().Name + ": " + e.Message + "\r\n" + e.StackTrace);
            fail++;
        } finally {
            if (!keep) {
                try { if (asm != null) asm.Close(true); } catch {}
                try { if (top2 != null) top2.Close(true); } catch {}
            }
            Pump(2);
        }
        L("");
        L(fail == 0 ? "NATIVE-LINK E2E OK" : ("NATIVE-LINK E2E FAILED " + fail));
        return fail == 0 ? 0 : 1;
    }

    static void TopLevel(string outDir, string stamp, out A.AssemblyDocument asm){
        L("======== 场景一：顶层装配里的两个零件");
        string pa = MakeA(Path.Combine(outDir, "NA_" + stamp + ".par"));
        string pb = MakePlate(Path.Combine(outDir, "NB_" + stamp + ".par"));
        string pd = Path.Combine(outDir, "ND_" + stamp + ".par");
        {   // D：只有 2 个 Φ6.6，给⑤第二批用（孔心和 A 的孔错开）
            var d = (P.PartDocument)app.Documents.Add("SolidEdge.PartDocument", Tpl + "iso metric part.par");
            d.ModelingMode = P.ModelingModeConstants.seModelingModeOrdered;
            var md = Plate(d, 0.1, 0.1, 0.01);
            var topD = d.RefPlanes.AddParallelByDistance(FaceAtZ(md, 0.01), 0.0, P.ReferenceElementConstants.igNormalSide, M, M, M, M);
            Hole(d, md, topD, 0.035, 0.05, 0.01, 0.0066); Hole(d, md, topD, 0.065, 0.05, 0.01, 0.0066);
            d.SaveAs(pd); d.Close(false);
        }
        A.AssemblyDocument a0 = null;
        Retry(delegate { a0 = (A.AssemblyDocument)app.Documents.Add("SolidEdge.AssemblyDocument", Tpl + "iso metric assembly.asm"); }, "新建装配");
        asm = a0;
        Pump(20);
        A.Occurrence occA = null, occB = null, occD = null;
        Retry(delegate { occA = a0.Occurrences.AddByFilename(pa); }, "插入 A");
        Retry(delegate { occB = a0.Occurrences.AddByFilename(pb); }, "插入 B");
        Retry(delegate { occD = a0.Occurrences.AddByFilename(pd); }, "插入 D");
        Array mB = new double[]{ 1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0.03,1 };
        Array mD = new double[]{ 1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0.06,1 };   // D 在 B 上面
        Retry(delegate { occB.PutMatrix(ref mB, true); occD.PutMatrix(ref mD, true); }, "放置 B/D");
        Retry(delegate { a0.SaveAs(Path.Combine(outDir, "NativeTop_" + stamp + ".asm")); }, "保存装配");
        Pump(20);
        P.PartDocument partA = null, partB = null, partD = null; P.Model modelA = null, modelB = null, modelD = null; double volB0 = 0;
        Retry(delegate {
            partA = (P.PartDocument)occA.OccurrenceDocument; partB = (P.PartDocument)occB.OccurrenceDocument; partD = (P.PartDocument)occD.OccurrenceDocument;
            modelA = (P.Model)partA.Models.Item(1); modelB = (P.Model)partB.Models.Item(1); modelD = (P.Model)partD.Models.Item(1);
            volB0 = Vol(modelB);
        }, "取实例文档");
        object refA = null, refB = null;
        Retry(delegate { refA = a0.CreateReference(occA, FaceAtZ(modelA, 0.01)); refB = a0.CreateReference(occB, FaceAtZ(modelB, 0.0)); }, "引用");

        AutoHoleForm form = null;
        try {
            Retry(delegate { form = new AutoHoleForm(app, a0); }, "打开自动打孔窗口");
            form.SuppressDialogs = true;
            Retry(delegate { form.Show(); }, "显示窗口");
            Pump(10);
            form.KeepLink = true;
            L("① 点 A 顶面"); Retry(delegate { form.AcceptPick(refA); }, "点 A"); Pump(5);
            L("   UI: " + form.UiSummary());
            L("② 点 B 底面"); Retry(delegate { form.AcceptPick(refB); }, "点 B"); Pump(5);
            Check(form.UiSummary().IndexOf("将打 6 个孔") >= 0, "预览：将打 6 个孔");
            L("③ 开始打孔");
            var t0 = DateTime.Now;
            Retry(delegate { form.DrillForTest(); }, "开始打孔");
            Pump(10);
            string res = form.LastResult ?? "";
            L("   用时 " + (DateTime.Now - t0).TotalSeconds.ToString("0.0") + "s");
            L("   RESULT: " + res.Replace("\r\n", " ⏎ "));
            Check(res.IndexOf("共打孔 6 个") >= 0, "共打孔 6 个");
            Check(res.IndexOf("失败") < 0, "没有失败项");
            Check(res.IndexOf("天工原生关联 6 个孔") >= 0, "6 个孔都挂上天工原生关联");
            Check(ReferenceEquals(app.ActiveDocument, a0), "打完已退出原位编辑，活动文档回到装配");
            L("   多孔孔特征：" + Multis(modelB));
            Check(HoleCount(modelB) == 6 && modelB.Holes.Count == 0, "B 板 6 个孔，全在多孔孔特征里（实 " + HoleCount(modelB) + "，单孔 " + modelB.Holes.Count + "）");
            Check(Multis(modelB).Contains("M6 螺纹孔（螺纹全长） ×4[4]") && Multis(modelB).Contains("M6 沉孔 Φ6.6/Φ11 深6.8 ×2[2]") && Multis(modelB).IndexOf("{") < 0, "每个规格合成一个多孔孔特征「…×4」「…×2」，状态都正常");
            Check(InterpartLinks(partB), "B 零件 HasInterpartLinks = True");
            Check(Copies(partB) == 1, "B 里 1 个零件间复制（同一张参考面只复制一次）（实 " + Copies(partB) + "：" + CopyNames(partB) + "）");
            Check(CopyNames(partB).IndexOf("孔位链接-NA_") >= 0, "零件间复制名字「孔位链接-参考零件名」");
            L("   B 特征树：" + Tree(partB));
            var groups = new List<string>();
            foreach (P.FeatureGroup g in modelB.FeatureGroups) groups.Add(g.Name + "[" + g.Count + "]");
            L("   特征组：" + string.Join(" | ", groups.ToArray()));
            Check(groups.Count == 1 && groups[0].StartsWith("关联孔 "), "0.8.4：孔位链接和这张面的多孔孔特征编成一个「关联孔 …」组（实 " + string.Join(" | ", groups.ToArray()) + "）");
            double removed = volB0 - Vol(modelB);
            double expect = 4 * Math.PI * Math.Pow(0.004917/2, 2) * 0.010
                          + 2 * (Math.PI * Math.Pow(0.0055, 2) * 0.0068 + Math.PI * Math.Pow(0.0033, 2) * 0.0032);
            Check(Math.Abs(removed - expect) / expect < 0.02, "B 切除体积 = 4×M6 螺纹孔 + 2×M6 沉孔（实 " + N(removed*1e9) + " / 期 " + N(expect*1e9) + "）");
            int legacy = 0; for (int i = 1; i <= modelB.Holes.Count; i++) if (HoleLinkStore.Read(modelB.Holes.Item(i)) != null) legacy++;
            Check(legacy == 0, "原生关联的孔不再写属性集关联（实 " + legacy + "）");

            L("④ 挪 A (+5,-3)mm → 同步孔位（更新链接）");
            Array mA = new double[]{ 1,0,0,0, 0,1,0,0, 0,0,1,0, 0.005,-0.003,0,1 };
            Retry(delegate { occA.PutMatrix(ref mA, true); }, "挪 A");
            Pump(10);
            Retry(delegate { form.SyncForTest(); }, "同步孔位");
            Pump(10);
            L("   B 底面圆心：" + Circles(modelB, 0.0));
            bool moved = true;
            foreach (var c in Clear) if (!HasCircle(modelB, 0.0, c[0] + 0.005, c[1] - 0.003)) moved = false;
            foreach (var c in Tap) if (!HasCircle(modelB, 0.0, c[0] + 0.005, c[1] - 0.003)) moved = false;
            Check(moved, "B 的 6 个孔都跟到了 (+5,-3)");
            Check(Multis(modelB).IndexOf("{") < 0, "挪动、同步以后多孔孔特征状态仍正常（" + Multis(modelB) + "）");
            Check(!HasCircle(modelB, 0.0, 0.02, 0.02), "旧位置 (20,20) 没有孔了");
            Check(Math.Abs((volB0 - Vol(modelB)) - removed) / removed < 1e-3, "切除体积不变");

            L("⑤ 第二批：点 D 顶面（2×Φ6.6）认孔 → 勾掉「自动识别面上的孔」→ 再点 B 底面当打孔面（和第一批同一张面，才会并组）");
            Retry(delegate { form.ClearAllForTest(); }, "全部重选");
            object refD = null, refB2 = null;
            Retry(delegate { refD = a0.CreateReference(occD, FaceAtZ(modelD, 0.01)); refB2 = a0.CreateReference(occB, FaceAtZ(modelB, 0.0)); }, "引用（第二批）");
            Retry(delegate { form.AcceptPick(refD); }, "点 D"); Pump(5);
            form.ScanFaceOnPick = false;
            Retry(delegate { form.AcceptPick(refB2); }, "点 B 底面"); Pump(5);
            form.ScanFaceOnPick = true;
            L("   UI: " + form.UiSummary());
            Retry(delegate { form.DrillForTest(); }, "第二批打孔");
            Pump(10);
            string res2 = form.LastResult ?? "";
            L("   RESULT: " + res2.Replace("\r\n", " ⏎ "));
            Check(res2.IndexOf("共打孔 2 个") >= 0 && res2.IndexOf("天工原生关联 2 个孔") >= 0, "第二批 2 个孔、都挂上原生关联");
            L("   多孔孔特征：" + Multis(modelB));
            Check(HoleCount(modelB) == 8 && modelB.UserDefinedPatterns.Count == 3, "B 现在 8 个孔、3 个多孔孔特征（实 " + HoleCount(modelB) + " / " + modelB.UserDefinedPatterns.Count + "）");
            Check(HasCircle(modelB, 0.01, 0.035, 0.05) && HasCircle(modelB, 0.01, 0.065, 0.05), "第二批孔在 D 的孔下面 (35,50) (65,50)");
            Check(Copies(partB) == 2, "B 里 2 个零件间复制（A 一个、D 一个）（实 " + Copies(partB) + "）");
            var g2 = new List<string>();
            foreach (P.FeatureGroup g in modelB.FeatureGroups) g2.Add(g.Name + "[" + g.Count + "]");
            L("   特征组：" + string.Join(" | ", g2.ToArray()));
            L("   B 特征树：" + Tree(partB));
            Check(g2.Count == 2 && g2.All(x => x.StartsWith("关联孔 ")), "0.8.4：第二批另起一个「关联孔 …」组（链接和孔一起删）");
            Check(Tree(partB).IndexOf("孔位链接-ND_") > Tree(partB).IndexOf("M6 沉孔"), "第二批的孔位链接紧挨着第二批的孔（排在第一批之后）");

            L("⑥ 撤销第二批");
            Retry(delegate { form.UndoForTest(); }, "撤销");
            Pump(10);
            Check(HoleCount(modelB) == 6 && modelB.UserDefinedPatterns.Count == 2, "撤销后 B 回到 6 个孔、2 个多孔孔特征（实 " + HoleCount(modelB) + " / " + modelB.UserDefinedPatterns.Count + "）");
            Check(Copies(partB) == 1, "第二批的零件间复制也删掉了（实 " + Copies(partB) + "）");
            Check(modelB.FeatureGroups.Count == 1 && Multis(modelB).Contains("M6 螺纹孔（螺纹全长） ×4[4]"), "撤销后只剩第一批的链接组、第一批的「×4」原样（" + Multis(modelB) + "）");

            L("⑦ 改孔型：点「×4」里的一个孔 → 改成 M8 圆柱沉孔 → 撤销修改");
            form.Close(); form = null;
            HoleEditForm ef = null;
            try {
                Retry(delegate { ef = new HoleEditForm(app, a0); }, "打开改孔型");
                ef.SuppressDialogs = true;
                Retry(delegate { ef.Show(); }, "显示"); Pump(6);
                object refEdge = null;
                Retry(delegate { refEdge = a0.CreateReference(occB, CircleEdgeAt(modelB, 0.025, 0.017, 0.0)); }, "孔口圆边引用");
                Retry(delegate { ef.AcceptPick(refEdge); }, "点孔"); Pump(6);
                L("   预览：" + ef.PreviewText.Replace("\r\n", " ⏎ "));
                Check(ef.TargetCount == 1 && ef.PreviewText.IndexOf("多孔孔特征") >= 0, "认出多孔孔特征里的孔（整个特征一起变）");
                ef.SetSpecForTest(HoleKind.Counterbore, "M8", 0); Pump(2);
                var cb = ef.CurrentSpecForTest;
                Retry(delegate { ef.ApplyForTest(); }, "应用"); Pump(8);
                L("   RESULT: " + (ef.LastResult ?? "").Replace("\r\n", " ⏎ "));
                L("   多孔孔特征：" + Multis(modelB));
                bool all8 = true;
                foreach (var c in Clear) if (Math.Abs(DiaAt(modelB, 0.0, c[0] + 0.005, c[1] - 0.003) - cb.CounterboreDiameter) > 0.01) all8 = false;
                Check(all8, "4 个孔口都变成 Φ" + N(cb.CounterboreDiameter) + "（整个多孔特征一起变）");
                Check(Multis(modelB).Contains(HoleNaming.Short(cb) + " ×4[4]"), "多孔孔特征改名「" + HoleNaming.Short(cb) + " ×4」");
                Retry(delegate { ef.UndoForTest(); }, "撤销修改"); Pump(8);
                L("   撤销：" + (ef.LastResult ?? "").Replace("\r\n", " ⏎ ") + "　" + Multis(modelB));
                bool back = true;
                foreach (var c in Clear) if (DiaAt(modelB, 0.0, c[0] + 0.005, c[1] - 0.003) > 5.5) back = false;
                L("   撤销后孔口直径：" + string.Join(" ", Clear.Select(c => N(DiaAt(modelB, 0.0, c[0] + 0.005, c[1] - 0.003))).ToArray()));
                if (!back) {   // 诊断：是不是几何还没重算
                    try { modelB.Recompute(); } catch { } Pump(6);
                    L("   重算后孔口直径：" + string.Join(" ", Clear.Select(c => N(DiaAt(modelB, 0.0, c[0] + 0.005, c[1] - 0.003))).ToArray()));
                }
                Check(back && Multis(modelB).Contains("M6 螺纹孔（螺纹全长） ×4[4]"), "撤销后回到 M6 螺纹孔 ×4");
            } finally { if (ef != null) try { ef.Close(); } catch {} }
        } finally { if (form != null) try { form.Close(); } catch {} }
        try { a0.Save(); } catch { }
    }

    static void Nested(string outDir, string stamp, out A.AssemblyDocument top){
        L(""); L("======== 场景二：参考零件、打孔零件都在子装配里");
        string pa = MakeA(Path.Combine(outDir, "NA2_" + stamp + ".par"));
        string pb = MakePlate(Path.Combine(outDir, "NB2_" + stamp + ".par"));
        string ps = Path.Combine(outDir, "NS_" + stamp + ".asm"), pt = Path.Combine(outDir, "NT_" + stamp + ".asm");
        A.AssemblyDocument s = null, t = null, tp = null;
        Retry(delegate { s = (A.AssemblyDocument)app.Documents.Add("SolidEdge.AssemblyDocument", Tpl + "iso metric assembly.asm"); s.Occurrences.AddByFilename(pa); s.SaveAs(ps); s.Close(false); }, "子装配 S");
        Retry(delegate { t = (A.AssemblyDocument)app.Documents.Add("SolidEdge.AssemblyDocument", Tpl + "iso metric assembly.asm"); t.Occurrences.AddByFilename(pb); t.SaveAs(pt); t.Close(false); }, "子装配 T");
        Retry(delegate { tp = (A.AssemblyDocument)app.Documents.Add("SolidEdge.AssemblyDocument", Tpl + "iso metric assembly.asm"); }, "顶层装配");
        top = tp;
        A.Occurrence occS = null, occT = null;
        Retry(delegate { occS = tp.Occurrences.AddByFilename(ps); occT = tp.Occurrences.AddByFilename(pt); }, "插入 S/T");
        Array mT = new double[]{ 1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0.03,1 };
        Retry(delegate { occT.PutMatrix(ref mT, true); }, "放置 T");
        Retry(delegate { tp.SaveAs(Path.Combine(outDir, "NativeNested_" + stamp + ".asm")); }, "保存");
        Pump(20);
        A.AssemblyDocument sDoc = null, tDoc = null; A.Occurrence inS = null, inT = null; P.PartDocument partA = null, partB = null; P.Model modelA = null, modelB = null;
        Retry(delegate {
            sDoc = (A.AssemblyDocument)occS.OccurrenceDocument; tDoc = (A.AssemblyDocument)occT.OccurrenceDocument;
            inS = (A.Occurrence)sDoc.Occurrences.Item(1); inT = (A.Occurrence)tDoc.Occurrences.Item(1);
            partA = (P.PartDocument)inS.OccurrenceDocument; partB = (P.PartDocument)inT.OccurrenceDocument;
            modelA = (P.Model)partA.Models.Item(1); modelB = (P.Model)partB.Models.Item(1);
        }, "取子装配里的零件");
        object refA = null, refB = null;
        Retry(delegate {
            refA = tp.CreateReference(occS, sDoc.CreateReference(inS, FaceAtZ(modelA, 0.01)));
            refB = tp.CreateReference(occT, tDoc.CreateReference(inT, FaceAtZ(modelB, 0.0)));
        }, "嵌套引用");
        AutoHoleForm form = null;
        try {
            Retry(delegate { form = new AutoHoleForm(app, tp); }, "打开自动打孔窗口");
            form.SuppressDialogs = true;
            Retry(delegate { form.Show(); }, "显示窗口");
            Pump(10);
            Retry(delegate { form.AcceptPick(refA); }, "点 A2"); Pump(5);
            Retry(delegate { form.AcceptPick(refB); }, "点 B2"); Pump(5);
            L("   UI: " + form.UiSummary());
            Retry(delegate { form.DrillForTest(); }, "打孔");
            Pump(10);
            string res = form.LastResult ?? "";
            L("   RESULT: " + res.Replace("\r\n", " ⏎ "));
            Check(res.IndexOf("共打孔 6 个") >= 0, "子装配：共打孔 6 个");
            Check(res.IndexOf("天工原生关联 6 个孔") >= 0, "子装配：6 个孔都挂上原生关联（两级原位编辑）");
            Check(ReferenceEquals(app.ActiveDocument, tp), "打完退回顶层装配");
            Check(InterpartLinks(partB), "B2 HasInterpartLinks = True");
            L("⑧ 挪 S (+4,0)mm → 同步孔位");
            Array mS = new double[]{ 1,0,0,0, 0,1,0,0, 0,0,1,0, 0.004,0,0,1 };
            Retry(delegate { occS.PutMatrix(ref mS, true); }, "挪 S");
            Pump(10);
            Retry(delegate { form.SyncForTest(); }, "同步孔位");
            Pump(10);
            L("   B2 底面圆心：" + Circles(modelB, 0.0));
            bool moved = true;
            foreach (var c in Clear) if (!HasCircle(modelB, 0.0, c[0] + 0.004, c[1])) moved = false;
            foreach (var c in Tap) if (!HasCircle(modelB, 0.0, c[0] + 0.004, c[1])) moved = false;
            Check(moved, "B2 的 6 个孔都跟到了 (+4,0)");
            L("⑨ 撤销（打孔零件在子装配里：FenceHoleE2E 在用户围栏上撤销时卡死过）");
            var t9 = DateTime.Now;
            Retry(delegate { form.UndoForTest(); }, "撤销"); Pump(10);
            L("   撤销用时 " + (DateTime.Now - t9).TotalSeconds.ToString("0.0") + "s：" + (form.LastResult ?? "").Replace("\r\n", " ⏎ "));
            Check(HoleCount(modelB) == 0 && Copies(partB) == 0, "子装配：撤销后 B2 没有孔、没有零件间复制（实 " + HoleCount(modelB) + " / " + Copies(partB) + "）");
            Check(ReferenceEquals(app.ActiveDocument, tp), "子装配：撤销后仍在顶层装配");
        } finally { if (form != null) try { form.Close(); } catch {} }
        try { tp.Save(); } catch { }
    }
}
