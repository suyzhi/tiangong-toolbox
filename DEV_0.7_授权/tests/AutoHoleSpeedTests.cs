using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using TianGongCadSuite;
using A=SolidEdgeAssembly;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;
using F=SolidEdgeFramework;
using S=SolidEdgeFrameworkSupport;

// 0.7.10 打孔速度真机端到端：走「自动打孔」窗口的真实代码路径（AcceptPick / DrillForTest），量 20 个孔要多久。
//   A 板 200×150×10 上 20 个参考孔（孔特征），B 板实心在 A 上方 z+30；点 A 顶面认孔 → 点 B 底面 → 打孔，计时。
//   模式（第二个参数，逗号分隔，默认 link,nolink,mixed）：
//     link   保持关联（默认勾着，天工原生关联 + 多孔合一）20×Φ6.6
//     nolink 不保持关联 20×Φ6.6
//     mixed  保持关联 12×Φ6.6 + 8×Φ5（两种规格 = 两个多孔孔特征）
//   每个模式核对：打孔数、没有失败、B 里孔数、多孔孔特征状态、切除体积；并打出插件日志里的 AutoHoleTiming 分段。
//     bars4  保持关联，20 个孔落在 4 个零件（"型材"）上，各 5 个：每个零件各进出一次原位编辑
// 两种跑法：进程外 tools/hole-feature-probe/DrillSpeedE2E.exe（连 CAD 跑，每次 COM 调用都跨进程，偏慢）；
//          进程内 PanelTests.exe --autohole-speed（经开发构建的 Diagnostics.StartTestsMode 在 CAD 里跑 = 插件真实的速度）。
public static class DrillSpeed {
    static F.Application app;
    static int fail = 0;
    static string where = "进程外";
    static object M = Type.Missing;
    const string Tpl = @"C:\Program Files\NDS\TianGong 2025\Template\ISO Metric\";
    const double LimitSeconds = 5.0;
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
    static int HoleCount(P.Model m){
        int n = m.Holes.Count;
        for (int i = 1; i <= m.UserDefinedPatterns.Count; i++) n += m.UserDefinedPatterns.Item(i).NumberInputFeatures;
        return n;
    }
    static string Multis(P.Model m){
        var l = new List<string>();
        for (int i = 1; i <= m.UserDefinedPatterns.Count; i++) { var u = m.UserDefinedPatterns.Item(i); object d; var st = u.GetStatusEx(out d); l.Add(u.Name + "[" + u.NumberInputFeatures + "]" + (st == P.FeatureStatusConstants.igFeatureOK ? "" : "{" + st + "}")); }
        return string.Join(" | ", l.ToArray());
    }
    static int Copies(P.PartDocument p){ try { return p.Constructions.InterpartConstructions.Count; } catch { return -1; } }
    static string Tree(P.PartDocument p){
        var l = new List<string>();
        try { var f = p.DesignEdgebarFeatures; for (int i = 1; i <= f.Count; i++) { try { l.Add((string)((dynamic)f.Item(i)).Name); } catch { l.Add("?"); } } } catch { }
        return string.Join(" → ", l.ToArray());
    }

    // 20 个孔位：5 列 × 4 行；mixed 时后 8 个是 Φ5
    static List<double[]> Grid(){
        var l = new List<double[]>();
        for (int j = 0; j < 4; j++) for (int i = 0; i < 5; i++) l.Add(new[]{ 0.02 + 0.04 * i, 0.03 + 0.03 * j });
        return l;
    }

    // panel.log 里这次新写的 AutoHoleTiming 行
    static long LogMark(){ try { return new FileInfo(Log.FilePath).Length; } catch { return 0; } }
    static void PrintTiming(long from){
        try {
            using (var fs = new FileStream(Log.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) {
                if (from > fs.Length) from = 0;
                fs.Seek(from, SeekOrigin.Begin);
                var text = new StreamReader(fs, Encoding.UTF8).ReadToEnd();
                foreach (var line in text.Split('\n')) {
                    string t = line.TrimEnd('\r');
                    if (t.IndexOf("AutoHoleTiming") >= 0 || t.IndexOf("HoleCombine") >= 0 || t.IndexOf("AutoHoleSide") >= 0) L("   日志：" + t);
                }
            }
        } catch (Exception e) { L("   读日志失败：" + e.Message); }
    }

    // bars4：参考板 20 个孔落在 4 根"型材"（4 个零件、各 5 个孔）上 —— 每个零件各进出一次原位编辑、各建一个零件间复制
    static void RunBars(string outDir, string stamp){
        L("======== 模式 bars4（4 个零件各 5 孔，保持关联）");
        var grid = Grid();
        string pa = Path.Combine(outDir, "SA_bars_" + stamp + ".par");
        var pbs = new List<string>();
        {
            var a = (P.PartDocument)app.Documents.Add("SolidEdge.PartDocument", Tpl + "iso metric part.par");
            a.ModelingMode = P.ModelingModeConstants.seModelingModeOrdered;
            var ma = Plate(a, 0.2, 0.15, 0.01);
            var topA = a.RefPlanes.AddParallelByDistance(FaceAtZ(ma, 0.01), 0.0, P.ReferenceElementConstants.igNormalSide, M, M, M, M);
            foreach (var c in grid) Hole(a, ma, topA, c[0], c[1], 0.01, 0.0066);
            a.SaveAs(pa); a.Close(false);
            for (int j = 0; j < 4; j++) {
                string pb = Path.Combine(outDir, "SBar" + (j + 1) + "_" + stamp + ".par");
                var b = (P.PartDocument)app.Documents.Add("SolidEdge.PartDocument", Tpl + "iso metric part.par");
                b.ModelingMode = P.ModelingModeConstants.seModelingModeOrdered;
                Plate(b, 0.2, 0.02, 0.01);
                b.SaveAs(pb); b.Close(false);
                pbs.Add(pb);
            }
        }
        A.AssemblyDocument asm = null;
        Retry(delegate { asm = (A.AssemblyDocument)app.Documents.Add("SolidEdge.AssemblyDocument", Tpl + "iso metric assembly.asm"); }, "新建装配");
        Pump(20);
        A.Occurrence occA = null;
        Retry(delegate { occA = asm.Occurrences.AddByFilename(pa); }, "插入 A");
        var occs = new List<A.Occurrence>();
        for (int j = 0; j < 4; j++) {
            A.Occurrence o = null; string pb = pbs[j]; double y = 0.03 + 0.03 * j - 0.01;
            Retry(delegate { o = asm.Occurrences.AddByFilename(pb); }, "插入型材");
            Array m = new double[]{ 1,0,0,0, 0,1,0,0, 0,0,1,0, 0,y,0.03,1 };
            Retry(delegate { o.PutMatrix(ref m, true); }, "放置型材");
            occs.Add(o);
        }
        Retry(delegate { asm.SaveAs(Path.Combine(outDir, "Speed_bars_" + stamp + ".asm")); }, "保存装配");
        Pump(20);
        P.Model modelA = null; var models = new List<P.Model>(); var parts = new List<P.PartDocument>();
        Retry(delegate {
            modelA = (P.Model)((P.PartDocument)occA.OccurrenceDocument).Models.Item(1);
            models.Clear(); parts.Clear();
            foreach (var o in occs) { var pd = (P.PartDocument)o.OccurrenceDocument; parts.Add(pd); models.Add((P.Model)pd.Models.Item(1)); }
        }, "取实例文档");
        object refA = null; var refBs = new List<object>();
        Retry(delegate { refA = asm.CreateReference(occA, FaceAtZ(modelA, 0.01)); refBs.Clear(); for (int j = 0; j < 4; j++) refBs.Add(asm.CreateReference(occs[j], FaceAtZ(models[j], 0.0))); }, "引用");
        AutoHoleForm form = null;
        try {
            Retry(delegate { form = new AutoHoleForm(app, asm); }, "打开自动打孔窗口");
            form.SuppressDialogs = true;
            Retry(delegate { form.Show(); }, "显示窗口");
            Pump(10);
            Retry(delegate { form.AcceptPick(refA); }, "点 A"); Pump(5);
            foreach (var r in refBs) { var rr = r; Retry(delegate { form.AcceptPick(rr); }, "点型材"); Pump(3); }
            L("   UI: " + form.UiSummary());
            Check(form.UiSummary().IndexOf("将打 20 个孔") >= 0, "预览：将打 20 个孔");
            long mark = LogMark();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Retry(delegate { form.DrillForTest(); }, "开始打孔");
            double secs = clock.Elapsed.TotalSeconds;
            Pump(5);
            string res = form.LastResult ?? "";
            L("   用时 " + secs.ToString("0.00", CultureInfo.InvariantCulture) + "s");
            L("   RESULT: " + res.Replace("\r\n", " ⏎ "));
            PrintTiming(mark);
            Check(res.IndexOf("共打孔 20 个") >= 0 && res.IndexOf("失败") < 0, "共打孔 20 个、没有失败项");
            Check(res.IndexOf("天工原生关联 20 个孔") >= 0, "20 个孔都挂上天工原生关联");
            bool each = true;
            for (int j = 0; j < 4; j++) {
                L("   型材 " + (j + 1) + "：" + Multis(models[j]));
                if (HoleCount(models[j]) != 5 || models[j].UserDefinedPatterns.Count != 1 || Multis(models[j]).IndexOf("{") >= 0) each = false;
                for (int i = 0; i < 5; i++) { var c = grid[j * 5 + i]; if (!HasCircle(models[j], 0.0, c[0], c[1] - (0.03 + 0.03 * j - 0.01))) each = false; }
            }
            Check(each, "每根型材 5 个孔、一个多孔孔特征、孔位都对");
            Check(ReferenceEquals(app.ActiveDocument, asm), "打完活动文档回到装配");
            Check(secs <= LimitSeconds, "20 个孔（4 个零件）" + LimitSeconds + "s 内打完（实 " + secs.ToString("0.00", CultureInfo.InvariantCulture) + "s，" + where + "）");
            Retry(delegate { form.UndoForTest(); }, "撤销");
            Pump(5);
            int left = 0, copies = 0; for (int j = 0; j < 4; j++) { left += HoleCount(models[j]); copies += Copies(parts[j]); }
            Check(left == 0 && copies == 0, "撤销后孔和零件间复制都没了（孔 " + left + "，复制 " + copies + "）");
        } finally {
            try { if (form != null) form.Close(); } catch { }
            try { asm.Close(false); } catch { }
            Pump(5);
        }
    }

    static void Run(string outDir, string stamp, string mode){
        if (mode == "bars4") { RunBars(outDir, stamp); return; }
        L("======== 模式 " + mode);
        bool link = mode != "nolink", mixed = mode == "mixed";
        var grid = Grid();
        string pa = Path.Combine(outDir, "SA_" + mode + "_" + stamp + ".par");
        string pb = Path.Combine(outDir, "SB_" + mode + "_" + stamp + ".par");
        {
            var a = (P.PartDocument)app.Documents.Add("SolidEdge.PartDocument", Tpl + "iso metric part.par");
            a.ModelingMode = P.ModelingModeConstants.seModelingModeOrdered;
            var ma = Plate(a, 0.2, 0.15, 0.01);
            var topA = a.RefPlanes.AddParallelByDistance(FaceAtZ(ma, 0.01), 0.0, P.ReferenceElementConstants.igNormalSide, M, M, M, M);
            for (int k = 0; k < grid.Count; k++) Hole(a, ma, topA, grid[k][0], grid[k][1], 0.01, mixed && k >= 12 ? 0.005 : 0.0066);
            a.SaveAs(pa); a.Close(false);
            var b = (P.PartDocument)app.Documents.Add("SolidEdge.PartDocument", Tpl + "iso metric part.par");
            b.ModelingMode = P.ModelingModeConstants.seModelingModeOrdered;
            Plate(b, 0.2, 0.15, 0.01);
            b.SaveAs(pb); b.Close(false);
        }
        A.AssemblyDocument asm = null;
        Retry(delegate { asm = (A.AssemblyDocument)app.Documents.Add("SolidEdge.AssemblyDocument", Tpl + "iso metric assembly.asm"); }, "新建装配");
        Pump(20);
        A.Occurrence occA = null, occB = null;
        Retry(delegate { occA = asm.Occurrences.AddByFilename(pa); }, "插入 A");
        Retry(delegate { occB = asm.Occurrences.AddByFilename(pb); }, "插入 B");
        Array mB = new double[]{ 1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0.03,1 };
        Retry(delegate { occB.PutMatrix(ref mB, true); }, "放置 B");
        Retry(delegate { asm.SaveAs(Path.Combine(outDir, "Speed_" + mode + "_" + stamp + ".asm")); }, "保存装配");
        Pump(20);
        P.PartDocument partB = null; P.Model modelA = null, modelB = null; double volB0 = 0;
        Retry(delegate {
            modelA = (P.Model)((P.PartDocument)occA.OccurrenceDocument).Models.Item(1);
            partB = (P.PartDocument)occB.OccurrenceDocument; modelB = (P.Model)partB.Models.Item(1);
            volB0 = Vol(modelB);
        }, "取实例文档");
        object refA = null, refB = null;
        Retry(delegate { refA = asm.CreateReference(occA, FaceAtZ(modelA, 0.01)); refB = asm.CreateReference(occB, FaceAtZ(modelB, 0.0)); }, "引用");

        AutoHoleForm form = null;
        try {
            Retry(delegate { form = new AutoHoleForm(app, asm); }, "打开自动打孔窗口");
            form.SuppressDialogs = true;
            Retry(delegate { form.Show(); }, "显示窗口");
            Pump(10);
            form.KeepLink = link;
            Retry(delegate { form.AcceptPick(refA); }, "点 A"); Pump(5);
            Retry(delegate { form.AcceptPick(refB); }, "点 B"); Pump(5);
            L("   UI: " + form.UiSummary());
            Check(form.UiSummary().IndexOf("将打 20 个孔") >= 0, "预览：将打 20 个孔");
            long mark = LogMark();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Retry(delegate { form.DrillForTest(); }, "开始打孔");
            double secs = clock.Elapsed.TotalSeconds;
            Pump(5);
            string res = form.LastResult ?? "";
            L("   用时 " + secs.ToString("0.00", CultureInfo.InvariantCulture) + "s");
            L("   RESULT: " + res.Replace("\r\n", " ⏎ "));
            PrintTiming(mark);
            Check(res.IndexOf("共打孔 20 个") >= 0, "共打孔 20 个");
            Check(res.IndexOf("失败") < 0, "没有失败项");
            if (link) Check(res.IndexOf("天工原生关联 20 个孔") >= 0, "20 个孔都挂上天工原生关联");
            Check(ReferenceEquals(app.ActiveDocument, asm), "打完活动文档回到装配");
            bool screen = false; try { screen = app.ScreenUpdating; } catch { }
            Check(screen, "打完屏幕刷新已恢复（ScreenUpdating = True）");
            L("   多孔孔特征：" + Multis(modelB));
            L("   B 特征树：" + Tree(partB));
            Check(HoleCount(modelB) == 20, "B 板 20 个孔（实 " + HoleCount(modelB) + "，单孔 " + modelB.Holes.Count + "）");
            Check(modelB.Holes.Count == 0 && modelB.UserDefinedPatterns.Count == (mixed ? 2 : 1) && Multis(modelB).IndexOf("{") < 0,
                  "每个规格一个多孔孔特征，状态都正常（" + Multis(modelB) + "）");
            if (link) Check(Copies(partB) == 1, "B 里 1 个零件间复制（实 " + Copies(partB) + "）");
            bool all = true;
            foreach (var c in grid) if (!HasCircle(modelB, 0.0, c[0], c[1])) all = false;
            Check(all, "20 个孔位上都有孔（B 底面）");
            if (!mixed) {
                double removed = volB0 - Vol(modelB);
                double expect = 20 * Math.PI * Math.Pow(0.004917 / 2, 2) * 0.010;
                Check(Math.Abs(removed - expect) / expect < 0.02, "切除体积 = 20×M6 螺纹孔（实 " + N(removed * 1e9) + " / 期 " + N(expect * 1e9) + "）");
            }
            Check(secs <= LimitSeconds, "20 个孔 " + LimitSeconds + "s 内打完（实 " + secs.ToString("0.00", CultureInfo.InvariantCulture) + "s，" + where + "）");

            if (link) {
                L("   挪 A (+5,-3)mm → 同步孔位（更新链接）");
                Array mA = new double[]{ 1,0,0,0, 0,1,0,0, 0,0,1,0, 0.005,-0.003,0,1 };
                Retry(delegate { occA.PutMatrix(ref mA, true); }, "挪 A");
                Pump(10);
                Retry(delegate { form.SyncForTest(); }, "同步孔位");
                Pump(10);
                bool moved = true;
                foreach (var c in grid) if (!HasCircle(modelB, 0.0, c[0] + 0.005, c[1] - 0.003)) moved = false;
                Check(moved, "20 个孔都跟到了 (+5,-3)");
                Check(Multis(modelB).IndexOf("{") < 0, "挪动以后多孔孔特征状态仍正常（" + Multis(modelB) + "）");
            }
            L("   撤销本次打孔");
            Retry(delegate { form.UndoForTest(); }, "撤销");
            Pump(5);
            Check(HoleCount(modelB) == 0 && Copies(partB) == 0, "撤销后孔和零件间复制都没了（孔 " + HoleCount(modelB) + "，复制 " + Copies(partB) + "）");
        } finally {
            try { if (form != null) form.Close(); } catch { }
            try { asm.Close(false); } catch { }
            Pump(5);
        }
    }

    // 跑一遍（modes 逗号分隔，空 = 全部），返回失败项数
    public static int Run(F.Application host, string outDir, string modes, bool inProcess){
        app = host; fail = 0; where = inProcess ? "进程内" : "进程外";
        if (string.IsNullOrEmpty(modes)) modes = "link,nolink,mixed,bars4";
        outDir = Path.GetFullPath(outDir);
        Directory.CreateDirectory(outDir);
        try { app.DisplayAlerts = false; } catch { }
        L("CAD " + app.Version + "，插件 " + typeof(AutoHoleForm).Assembly.GetName().Version + "，" + where);
        string stamp = DateTime.Now.ToString("HHmmss");
        foreach (var mode in modes.Split(',')) {
            try { Run(outDir, stamp, mode.Trim()); }
            catch (Exception e) { L("E2E 异常（" + mode + "）：" + e.GetType().Name + ": " + e.Message + "\r\n" + e.StackTrace); fail++; }
            Pump(10);
        }
        L("");
        L(fail == 0 ? "DRILL-SPEED E2E OK" : ("DRILL-SPEED E2E FAILED " + fail));
        return fail;
    }
}
