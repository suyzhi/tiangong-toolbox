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

// 0.8.5：照"正常人怎么用"走一遍「自动打孔」「批量排孔」窗口（真实代码路径，夹具全是新建的，落在输出目录里）。
//   S1 用户截图：上板 R 两个 Φ6.6 通孔；下块 T（40 厚）底面在同一位置有 Φ5 盲孔，顶面另有一个不相干的 Φ10 孔；T 在装配里用了两次。
//      点 R 顶面认孔 → 点 T 顶面（自动认孔开着）应当成打孔面 → 预览提醒用了两次 → 打孔 2 个、没有"本来就有孔" → 撤销 → 再打
//   S2 L 形打孔面：三个参考孔有一个投在缺角上 → 预览 2 个、打 2 个、没有失败
//   S3 批量排孔在 L 形面上：第 3、4 个孔位落在缺角 → 预览标出、打 2 个、没有失败
//   S4 参考孔是螺纹孔（M6，Φ4.917 内径）→ 配出来的是过孔；从下往上打（打孔面法向朝下）
//   S5 盲孔比板还深（10 厚板上打 15 深）→ 看 CAD 怎么回、插件怎么说
// 用法: User085E2E.exe <输出目录>
class User085E2E {
    static F.Application app;
    static int fail = 0;
    static object M = Type.Missing;
    const string Tpl = @"C:\Program Files\NDS\TianGong 2025\Template\ISO Metric\";
    static void L(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    static void Check(bool ok, string label){ L((ok ? "PASS: " : "FAIL: ") + label); if (!ok) fail++; }
    static void Pump(int n){ for (int i = 0; i < n; i++) { try { app.DoIdle(); } catch { } try { Application.DoEvents(); } catch { } Thread.Sleep(60); } }
    static double D(Array a,int i){ return Convert.ToDouble(a.GetValue(i)); }
    static string Hr(Exception e){ return e.GetType().Name + " 0x" + e.HResult.ToString("X8") + " " + e.Message; }
    static string R(string s){ return (s ?? "").Replace("\r\n", " ⏎ "); }
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
    // 多边形板（xy 平面上的点，米），厚 t
    static P.Model Poly(P.PartDocument part, double[][] pts, double t){
        var prof = part.ProfileSets.Add().Profiles.Add(FindXY(part));
        int n = pts.Length;
        var Ls = new S.Line2d[n];
        for (int i = 0; i < n; i++) Ls[i] = prof.Lines2d.AddBy2Points(pts[i][0], pts[i][1], pts[(i+1)%n][0], pts[(i+1)%n][1]);
        var rel = (S.Relations2d)prof.Relations2d;
        for (int i = 0; i < n; i++) rel.AddKeypoint(Ls[i], (int)SolidEdgeConstants.KeypointIndexConstants.igLineEnd, Ls[(i+1)%n], (int)SolidEdgeConstants.KeypointIndexConstants.igLineStart);
        prof.End(P.ProfileValidationType.igProfileClosed);
        Array arr = new object[]{ prof };
        return part.Models.AddFiniteExtrudedProtrusion(1, ref arr, P.FeaturePropertyConstants.igRight, t);
    }
    static P.Model Plate(P.PartDocument part, double w, double h, double t){ return Poly(part, new[]{ new[]{0.0,0.0}, new[]{w,0.0}, new[]{w,h}, new[]{0.0,h} }, t); }
    static G.Face FaceAtZ(P.Model m, double z){
        foreach (G.Face f in (G.Faces)((G.Body)m.Body).get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll)) {
            var pl = f.Geometry as G.Plane; if (pl == null) continue;
            Array p = new double[3], n = new double[3]; pl.GetPlaneData(ref p, ref n);
            if (Math.Abs(Math.Abs(D(n,2)) - 1) < 1e-6 && Math.Abs(D(p,2) - z) < 1e-9) return f;
        }
        return null;
    }
    static G.Edge CircleEdge(P.Model m, double x, double y, double z){
        foreach (G.Edge e in (G.Edges)((G.Body)m.Body).get_Edges(G.FeatureTopologyQueryTypeConstants.igQueryAll)) {
            var c = e.Geometry as G.Circle; if (c == null) continue;
            Array cc = new double[3], ax = new double[3]; double r; c.GetCircleData(ref cc, ref ax, out r);
            if (Math.Abs(D(cc,0) - x) < 1e-6 && Math.Abs(D(cc,1) - y) < 1e-6 && Math.Abs(D(cc,2) - z) < 1e-6) return e;
        }
        return null;
    }
    static bool HasCircle(P.Model m, double x, double y, double z){ return CircleEdge(m, x, y, z) != null; }
    static double CircleR(P.Model m, double x, double y, double z){
        var e = CircleEdge(m, x, y, z); if (e == null) return 0;
        Array cc = new double[3], ax = new double[3]; double r; ((G.Circle)e.Geometry).GetCircleData(ref cc, ref ax, out r); return r * 2000;
    }
    static AutoHoleWriter.HoleRequest Req(double x, double y, double z, HoleSpec spec, string src){
        return new AutoHoleWriter.HoleRequest { Spec = spec, Centre = new V3(x, y, z), Source = src };
    }
    static HoleSpec Thru(double d){ return new HoleSpec { Kind = HoleKind.Through, HoleDiameter = d }; }
    static void Drill(P.PartDocument part, P.Model m, double z, params AutoHoleWriter.HoleRequest[] reqs){
        var r = AutoHoleWriter.DrillRequests(AutoHoleReader.ReadTarget(FaceAtZ(m, z)), reqs); Pump(4);
        if (r.Created != reqs.Length) throw new InvalidOperationException("夹具打孔 " + r.Created + "/" + reqs.Length + "：" + string.Join("；", r.Failures));
    }
    static P.PartDocument NewPart(){
        var p = (P.PartDocument)app.Documents.Add("SolidEdge.PartDocument", Tpl + "iso metric part.par");
        p.ModelingMode = P.ModelingModeConstants.seModelingModeOrdered;
        Pump(6);
        return p;
    }
    static A.AssemblyDocument NewAsm(string path, out A.Occurrence[] occs, params object[] placed){
        A.AssemblyDocument a = null;
        Retry(delegate { a = (A.AssemblyDocument)app.Documents.Add("SolidEdge.AssemblyDocument", Tpl + "iso metric assembly.asm"); }, "新建装配");
        Pump(20);
        var list = new List<A.Occurrence>();
        for (int i = 0; i + 1 < placed.Length; i += 2) {
            string file = (string)placed[i]; var at = (double[])placed[i + 1];
            A.Occurrence o = null;
            Retry(delegate { o = a.Occurrences.AddByFilename(file); }, "插入");
            Array mx = new double[]{ 1,0,0,0, 0,1,0,0, 0,0,1,0, at[0],at[1],at[2],1 };
            var oo = o; Retry(delegate { oo.PutMatrix(ref mx, true); }, "放置");
            list.Add(o);
        }
        Retry(delegate { a.SaveAs(path); }, "保存装配");
        Pump(20);
        occs = list.ToArray();
        return a;
    }
    static P.Model ModelOf(A.Occurrence o){ return (P.Model)((P.PartDocument)o.OccurrenceDocument).Models.Item(1); }

    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        TianGongCadSuite.Licensing.LicenseTestHooks.GateOverride = 0;
        E2EFilterU.Register();
        string outDir = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(outDir);
        try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); }
        catch (Exception e) { L("连不上 CAD：" + e.Message); return 2; }
        try { app.DisplayAlerts = false; } catch { }
        L("已连上 CAD " + app.Version + "　插件 DLL " + typeof(AutoHoleForm).Assembly.GetName().Version);
        string stamp = DateTime.Now.ToString("HHmmss");
        foreach (var s in new Action<string, string>[]{ S1, S2S3, S4, S5, S6, S7, S8, S9, S10, S11 }) {
            try { s(outDir, stamp); } catch (Exception e) { L("场景异常：" + Hr(e) + "\r\n" + e.StackTrace); fail++; }
            try { while (app.Documents.Count > 0) { ((dynamic)app.Documents.Item(1)).Close(false); Pump(2); } } catch { }
        }
        L("");
        L(fail == 0 ? "USER-085 E2E OK" : ("USER-085 E2E FAILED " + fail));
        return fail == 0 ? 0 : 1;
    }

    static void S1(string outDir, string stamp){
        L("======== S1 用户截图：反面盲孔 + 面上已有别的孔 + 零件用了两次");
        string pr = Path.Combine(outDir, "S1R_" + stamp + ".par"), pt = Path.Combine(outDir, "S1T_" + stamp + ".par");
        var r = NewPart(); var mr = Plate(r, 0.1, 0.06, 0.01);
        Drill(r, mr, 0.01, Req(0.03, 0.03, 0.01, Thru(6.6), "R1"), Req(0.07, 0.03, 0.01, Thru(6.6), "R2"));
        r.SaveAs(pr); r.Close(false);
        var t = NewPart(); var mt = Plate(t, 0.1, 0.06, 0.04);
        Drill(t, mt, 0.0, Req(0.03, 0.03, 0, new HoleSpec { Kind = HoleKind.Through, HoleDiameter = 5, Depth = 8 }, "底盲孔"));
        Drill(t, mt, 0.04, Req(0.05, 0.048, 0.04, Thru(10), "顶上不相干的孔"));
        t.SaveAs(pt); t.Close(false);
        A.Occurrence[] o;
        var a = NewAsm(Path.Combine(outDir, "S1_" + stamp + ".asm"), out o, pr, new[]{ 0.0, 0.0, 0.04 }, pt, new[]{ 0.0, 0.0, 0.0 }, pt, new[]{ 0.2, 0.0, 0.0 });
        AutoHoleForm f = null;
        try {
            f = new AutoHoleForm(app, a); f.SuppressDialogs = true; f.Show(); Pump(8);
            f.KeepLink = true; f.ScanFaceOnPick = true;
            f.AcceptPick(a.CreateReference(o[0], FaceAtZ(ModelOf(o[0]), 0.01))); Pump(4);
            L("   点 R 顶面后：" + f.UiSummary());
            f.ScanFaceOnPick = false;   // 0.9.1：到「打孔面」页再点 T（参考孔页点面一律认孔）
            f.AcceptPick(a.CreateReference(o[1], FaceAtZ(ModelOf(o[1]), 0.04))); Pump(4);
            string ui = f.UiSummary();
            L("   点 T 顶面后：" + ui);
            Check(ui.IndexOf("打孔面 1 个") >= 0, "T 顶面（上面有个不相干的孔）当成了打孔面");
            Check(ui.IndexOf("参考孔 1 组") >= 0 && ui.IndexOf("×2") >= 0, "参考孔还是 R 的 2 个（T 面上的孔没混进来）");
            Check(ui.IndexOf("将打 2 个孔") >= 0, "预览：将打 2 个孔");
            Check(ui.IndexOf("用了 2 次") >= 0, "提醒 T 在装配里用了 2 次");
            f.DrillForTest(); Pump(10);
            L("   RESULT: " + R(f.LastResult));
            var mt1 = ModelOf(o[1]);
            Check(f.LastResult.IndexOf("共打孔 2 个") >= 0 && f.LastResult.IndexOf("本来就有孔") < 0, "打了 2 个，没有「本来就有孔」（截图那个问题）");
            Check(HasCircle(mt1, 0.03, 0.03, 0.04) && HasCircle(mt1, 0.07, 0.03, 0.04), "T 顶面两个孔位上都有孔口");
            f.UndoForTest(); Pump(8);
            L("   撤销：" + R(f.LastResult));
            Check(!HasCircle(ModelOf(o[1]), 0.03, 0.03, 0.04) && HasCircle(ModelOf(o[1]), 0.03, 0.03, 0.0), "撤销后顶面孔没了、底面盲孔还在");
            f.AcceptPick(a.CreateReference(o[1], FaceAtZ(ModelOf(o[1]), 0.04))); Pump(4);
            f.DrillForTest(); Pump(10);
            L("   再打：" + R(f.LastResult));
            Check(f.LastResult.IndexOf("共打孔 2 个") >= 0, "撤销后再点面再打：2 个");
        } finally { if (f != null) try { f.Close(); } catch { } Pump(4); try { a.Close(false); } catch { } Pump(6); }
    }

    // L 形板：x 0..100 × y 0..30，加 x 0..50 × y 30..60（右上角空）
    static readonly double[][] Lshape = { new[]{0.0,0.0}, new[]{0.1,0.0}, new[]{0.1,0.03}, new[]{0.05,0.03}, new[]{0.05,0.06}, new[]{0.0,0.06} };
    static void S2S3(string outDir, string stamp){
        L("======== S2 L 形打孔面 / S3 L 形面上批量排孔");
        string pr = Path.Combine(outDir, "S2R_" + stamp + ".par"), pl = Path.Combine(outDir, "S2L_" + stamp + ".par");
        var r = NewPart(); var mr = Plate(r, 0.1, 0.06, 0.01);
        Drill(r, mr, 0.01, Req(0.02, 0.015, 0.01, Thru(6.6), "a"), Req(0.08, 0.015, 0.01, Thru(6.6), "b"), Req(0.08, 0.045, 0.01, Thru(6.6), "缺角上"));
        r.SaveAs(pr); r.Close(false);
        var l = NewPart(); Poly(l, Lshape, 0.01); l.SaveAs(pl); l.Close(false);
        A.Occurrence[] o;
        var a = NewAsm(Path.Combine(outDir, "S2_" + stamp + ".asm"), out o, pr, new[]{ 0.0, 0.0, 0.03 }, pl, new[]{ 0.0, 0.0, 0.0 });
        AutoHoleForm f = null;
        try {
            f = new AutoHoleForm(app, a); f.SuppressDialogs = true; f.Show(); Pump(8);
            f.KeepLink = false; f.ScanFaceOnPick = true;   // 用户自己的设置可能关着「自动识别」（autohole.tsv），测试里显式打开
            f.AcceptPick(a.CreateReference(o[0], FaceAtZ(ModelOf(o[0]), 0.01))); Pump(4);
            f.ScanFaceOnPick = false; f.AcceptPick(a.CreateReference(o[1], FaceAtZ(ModelOf(o[1]), 0.01))); Pump(4); f.ScanFaceOnPick = true;
            string ui = f.UiSummary();
            L("   预览：" + ui);
            Check(ui.IndexOf("将打 2 个孔") >= 0 && ui.IndexOf("跳过 1") >= 0, "预览：将打 2 个，缺角上那个跳过");
            f.DrillForTest(); Pump(10);
            L("   RESULT: " + R(f.LastResult));
            Check(f.LastResult.IndexOf("共打孔 2 个") >= 0 && f.LastResult.IndexOf("失败") < 0, "打 2 个、没有失败");
            f.Close(); f = null; Pump(4);
        } finally { if (f != null) try { f.Close(); } catch { } Pump(4); }

        AutoHolePatternForm pf = null;
        try {
            pf = new AutoHolePatternForm(app, a); pf.SuppressDialogs = true; pf.Show(); Pump(8);
            pf.AcceptPick(a.CreateReference(o[1], FaceAtZ(ModelOf(o[1]), 0.0))); Pump(4);
            pf.SetLayoutForTest(0, 4, 10, 1, 45); Pump(2);
            L("   排孔预览：" + R(pf.PreviewForTest));
            Check(pf.PreviewForTest.IndexOf("落在面外") >= 0, "排孔预览标出落在缺角上的孔位");
            pf.AsPattern = true;
            pf.DrillForTest(); Pump(10);
            L("   排孔结果：" + R(pf.LastResult));
            Check(pf.LastResult.IndexOf("排孔完成：2 / 2") >= 0 && pf.LastResult.IndexOf("失败原因") < 0, "排孔：缺角上的不打，其余 2 个打上、没有失败");
        } finally { if (pf != null) try { pf.Close(); } catch { } Pump(4); try { a.Close(false); } catch { } Pump(6); }
    }

    static void S4(string outDir, string stamp){
        L("======== S4 参考孔是螺纹孔；从下往上打（打孔面法向朝下）");
        string pb = Path.Combine(outDir, "S4B_" + stamp + ".par"), pc = Path.Combine(outDir, "S4C_" + stamp + ".par");
        var b = NewPart(); var mb = Plate(b, 0.1, 0.06, 0.02);
        var m6 = HoleMatcher.FromRow(HoleMatcher.Find("M6").Value, HoleKind.Tapped);
        m6.Depth = 12; m6.ThreadFull = true;
        Drill(b, mb, 0.02, Req(0.03, 0.03, 0.02, m6, "M6a"), Req(0.07, 0.03, 0.02, m6, "M6b"));
        L("   底座 M6 孔口直径 " + CircleR(mb, 0.03, 0.03, 0.02).ToString("0.###"));
        b.SaveAs(pb); b.Close(false);
        var c = NewPart(); Plate(c, 0.1, 0.06, 0.008); c.SaveAs(pc); c.Close(false);
        A.Occurrence[] o;
        var a = NewAsm(Path.Combine(outDir, "S4_" + stamp + ".asm"), out o, pb, new[]{ 0.0, 0.0, 0.0 }, pc, new[]{ 0.0, 0.0, 0.02 });
        AutoHoleForm f = null;
        try {
            f = new AutoHoleForm(app, a); f.SuppressDialogs = true; f.Show(); Pump(8);
            f.KeepLink = true; f.ScanFaceOnPick = true;
            var mbo = ModelOf(o[0]);
            f.AcceptPick(a.CreateReference(o[0], CircleEdge(mbo, 0.03, 0.03, 0.02))); Pump(4);
            f.AcceptPick(a.CreateReference(o[0], CircleEdge(mbo, 0.07, 0.03, 0.02))); Pump(4);
            L("   点两个 M6 孔口后：" + f.UiSummary());
            f.ScanFaceOnPick = false; f.AcceptPick(a.CreateReference(o[1], FaceAtZ(ModelOf(o[1]), 0.0))); Pump(4); f.ScanFaceOnPick = true;
            string ui = f.UiSummary();
            L("   点盖板底面后：" + ui);
            Check(ui.IndexOf("将打 2 个孔") >= 0, "预览：将打 2 个孔");
            f.DrillForTest(); Pump(10);
            L("   RESULT: " + R(f.LastResult));
            var mc = ModelOf(o[1]);
            double d = CircleR(mc, 0.03, 0.03, 0.0);
            L("   盖板底面孔口 Φ" + d.ToString("0.###"));
            Check(f.LastResult.IndexOf("共打孔 2 个") >= 0, "从下往上打 2 个");
            Check(f.LastResult.IndexOf("M6 沉孔 Φ6.6/Φ11") >= 0 && Math.Abs(d - 11) < 0.05, "M6 螺纹孔配的是 M6 沉孔（配做规则：螺纹底孔配沉孔），孔口 Φ11（实 Φ" + d.ToString("0.###") + "）");
        } finally { if (f != null) try { f.Close(); } catch { } Pump(4); try { a.Close(false); } catch { } Pump(6); }
    }

    // S6：参考孔点两次 = 取消；上板 Φ6.6 + 垫板 Φ7 同轴只算一个；预览认出本来就有的孔；孔口切边提醒；斜参考孔不打；改孔型点平面
    static void S6(string outDir, string stamp){
        L("======== S6 选择 / 预览 / 斜孔 / 改孔型点平面");
        string pa = Path.Combine(outDir, "S6A_" + stamp + ".par"), pp = Path.Combine(outDir, "S6P_" + stamp + ".par"), pt = Path.Combine(outDir, "S6T_" + stamp + ".par"), pk = Path.Combine(outDir, "S6K_" + stamp + ".par");
        var a0 = NewPart(); var ma = Plate(a0, 0.1, 0.06, 0.01);
        Drill(a0, ma, 0.01, Req(0.03, 0.03, 0.01, Thru(6.6), "A1"), Req(0.07, 0.03, 0.01, Thru(6.6), "A2"), Req(0.0965, 0.03, 0.01, Thru(6.6), "靠边"));
        a0.SaveAs(pa); a0.Close(false);
        var p0 = NewPart(); var mp = Plate(p0, 0.1, 0.06, 0.005);
        Drill(p0, mp, 0.005, Req(0.03, 0.03, 0.005, Thru(7), "P1"));
        p0.SaveAs(pp); p0.Close(false);
        var t0 = NewPart(); var mt = Plate(t0, 0.098, 0.06, 0.02);   // 比 A 窄 2mm：A 靠边那个孔配到 T 上孔口切出面外
        Drill(t0, mt, 0.02, Req(0.07, 0.03, 0.02, Thru(6.6), "T 上本来就有"));
        t0.SaveAs(pt); t0.Close(false);
        var k0 = NewPart(); var mk = Plate(k0, 0.04, 0.04, 0.01);
        Drill(k0, mk, 0.01, Req(0.02, 0.02, 0.01, Thru(6.6), "K1"));
        k0.SaveAs(pk); k0.Close(false);
        A.Occurrence[] o;
        var a = NewAsm(Path.Combine(outDir, "S6_" + stamp + ".asm"), out o, pa, new[]{ 0.0, 0.0, 0.025 }, pp, new[]{ 0.0, 0.0, 0.02 }, pt, new[]{ 0.0, 0.0, 0.0 }, pk, new[]{ 0.3, 0.0, 0.0 });
        // K 绕 x 转 5°：它的孔对 T 顶面是斜的
        double c5 = Math.Cos(5 * Math.PI / 180), s5 = Math.Sin(5 * Math.PI / 180);
        Array mk2 = new double[]{ 1,0,0,0, 0,c5,s5,0, 0,-s5,c5,0, 0.03,0.0,0.03,1 };
        Retry(delegate { o[3].PutMatrix(ref mk2, true); }, "转 K");
        AutoHoleForm f = null;
        try {
            f = new AutoHoleForm(app, a); f.SuppressDialogs = true; f.Show(); Pump(8);
            f.KeepLink = false; f.ScanFaceOnPick = true;
            var mao = ModelOf(o[0]);
            var e1 = a.CreateReference(o[0], CircleEdge(mao, 0.03, 0.03, 0.01));
            f.AcceptPick(e1); Pump(2);
            Check(f.UiSummary().IndexOf("参考孔 1 组") >= 0, "点一个孔口：1 个参考孔");
            f.AcceptPick(a.CreateReference(o[0], CircleEdge(mao, 0.03, 0.03, 0.01))); Pump(2);
            L("   再点一次：" + f.UiSummary());
            Check(f.UiSummary().IndexOf("参考孔 0 组") >= 0, "同一个孔再点一次 = 取消");
            f.AcceptPick(a.CreateReference(o[0], FaceAtZ(mao, 0.01))); Pump(4);
            f.ScanFaceOnPick = false; f.AcceptPick(a.CreateReference(o[1], FaceAtZ(ModelOf(o[1]), 0.005))); Pump(4); f.ScanFaceOnPick = true;
            L("   扫 A 顶面 + P 顶面：" + f.UiSummary());
            Check(f.UiSummary().IndexOf("参考孔 1 组") >= 0 && f.UiSummary().IndexOf("×3") >= 0, "P 上和 A 同轴的 Φ7 没再加成第二组（A 的 3 个孔一组）");
            Check(f.UiSummary().IndexOf("打孔面 1 个") >= 0, "P 只有 1 个孔、A 的另外 2 个孔投到它的空位上：P 当打孔面（它漏了孔）");
            f.ScanFaceOnPick = false; f.AcceptPick(a.CreateReference(o[2], FaceAtZ(ModelOf(o[2]), 0.02))); Pump(4); f.ScanFaceOnPick = true;
            string ui = f.UiSummary();
            L("   点 T 顶面：" + ui);
            Check(ui.IndexOf("将打 4 个孔") >= 0 && ui.IndexOf("2 个位置本来就有孔") >= 0, "预览：P、T 各 2 个，2 个位置本来就有孔（P 上的 Φ7、T 上的 Φ6.6）");
            Check(ui.IndexOf("切到面的边上") >= 0, "预览：靠边那个孔口切出面外的提醒");
            f.DrillForTest(); Pump(10);
            L("   RESULT: " + R(f.LastResult));
            Check(f.LastResult.IndexOf("共打孔 4 个") >= 0 && f.LastResult.IndexOf("2 个位置本来就有孔") >= 0, "打 4 个，2 个本来就有");
            f.ClearAllForTest(); Pump(2);
            f.AcceptPick(a.CreateReference(o[3], FaceAtZ(ModelOf(o[3]), 0.01))); Pump(4);
            f.ScanFaceOnPick = false; f.AcceptPick(a.CreateReference(o[2], FaceAtZ(ModelOf(o[2]), 0.02))); Pump(4); f.ScanFaceOnPick = true;
            ui = f.UiSummary();
            L("   斜参考孔 + T 顶面：" + ui);
            Check(ui.IndexOf("不垂直") >= 0, "预览说参考孔和打孔面不垂直");
            Check(ui.IndexOf("可打孔=False") >= 0, "斜孔不让打");
            f.Close(); f = null; Pump(4);
        } finally { if (f != null) try { f.Close(); } catch { } Pump(4); }
        HoleEditForm ef = null;
        try {
            ef = new HoleEditForm(app, a); ef.SuppressDialogs = true; ef.Show(); Pump(6);
            ef.AcceptPick(a.CreateReference(o[2], FaceAtZ(ModelOf(o[2]), 0.02))); Pump(4);
            L("   改孔型点 T 顶面：" + ef.StatusForTest);
            Check(ef.TargetCountForTest >= 2 && ef.StatusForTest.IndexOf("从这张面上加入") >= 0, "改孔型点平面：面上的孔都加进来");
        } finally { if (ef != null) try { ef.Close(); } catch { } Pump(4); try { a.Close(false); } catch { } Pump(6); }
    }

    // S7：单独打开的零件里批量排孔（不建装配），再撤销
    static void S7(string outDir, string stamp){
        L("======== S7 单独打开的零件里批量排孔");
        var p = NewPart(); var m = Plate(p, 0.12, 0.05, 0.01);
        AutoHolePatternForm pf = null;
        try {
            pf = new AutoHolePatternForm(app, p); pf.SuppressDialogs = true; pf.Show(); Pump(6);
            pf.AcceptPick(FaceAtZ(m, 0.01)); Pump(2);
            pf.SetLayoutForTest(0, 5, 10, 0, 0); Pump(2);
            L("   预览：" + R(pf.PreviewForTest));
            pf.DrillForTest(); Pump(8);
            L("   结果：" + R(pf.LastResult));
            Check(pf.LastResult.IndexOf("排孔完成：5 / 5") >= 0, "单独零件里排 5 个孔");
            pf.UndoForTest(); Pump(6);
            L("   撤销：" + R(pf.LastResult));
            Check(pf.LastResult.IndexOf("已撤销 5 个孔") >= 0, "撤销 5 个");
        } finally { if (pf != null) try { pf.Close(); } catch { } Pump(4); try { p.Close(false); } catch { } Pump(4); }
    }

    // S8：进入选择状态用的是 QuickPick 定位（右键 / 停一下能选被挡住的面），天工接受这个模式
    static void S8(string outDir, string stamp){
        L("======== S8 选择状态：QuickPick 定位");
        A.AssemblyDocument a = null; AutoHoleForm f = null;
        try {
            Retry(delegate { a = (A.AssemblyDocument)app.Documents.Add("SolidEdge.AssemblyDocument", Tpl + "iso metric assembly.asm"); }, "新建装配");
            Pump(10);
            f = new AutoHoleForm(app, a); f.SuppressDialogs = true; f.Show(); Pump(4);
            string err = "";
            try { f.StartPicking(); } catch (Exception e) { err = Hr(e); }
            Pump(4);
            L("   StartPicking：" + (err.Length == 0 ? "OK" : err) + "　LocateMode=" + f.LocateModeForTest);
            Check(err.Length == 0 && f.LocateModeForTest == 2, "选择命令起来了，定位模式 = seLocateQuickPick(2)");
            f.StopPickingForTest();
        } finally { if (f != null) try { f.Close(); } catch { } Pump(4); try { if (a != null) a.Close(false); } catch { } Pump(4); }
    }

    // S9：参考件上是腰孔：点它一头的圆弧 → 目标件上配同样的腰孔；再点一次取消
    static void S9(string outDir, string stamp){
        L("======== S9 参考腰孔");
        string pr = Path.Combine(outDir, "S9R_" + stamp + ".par"), pt = Path.Combine(outDir, "S9T_" + stamp + ".par");
        var r = NewPart(); var mr = Plate(r, 0.1, 0.06, 0.01);
        var sr = AutoHoleWriter.DrillSlots(AutoHoleReader.ReadTarget(FaceAtZ(mr, 0.01)), new[]{ new V3(0.05, 0.03, 0.01) }, new[]{ new V3(1, 0, 0) }, 30, 9); Pump(4);
        if (sr.Created != 1) throw new InvalidOperationException("夹具腰孔没切成：" + string.Join("；", sr.Failures));
        r.SaveAs(pr); r.Close(false);
        var t = NewPart(); Plate(t, 0.1, 0.06, 0.01); t.SaveAs(pt); t.Close(false);
        A.Occurrence[] o;
        var a = NewAsm(Path.Combine(outDir, "S9_" + stamp + ".asm"), out o, pr, new[]{ 0.0, 0.0, 0.01 }, pt, new[]{ 0.0, 0.0, 0.0 });
        AutoHoleForm f = null;
        try {
            f = new AutoHoleForm(app, a); f.SuppressDialogs = true; f.Show(); Pump(6);
            f.KeepLink = false; f.ScanFaceOnPick = false;
            var arc = CircleEdge(ModelOf(o[0]), 0.0395, 0.03, 0.01);
            L("   圆弧边：" + (arc != null));
            f.AcceptPick(a.CreateReference(o[0], arc)); Pump(2);
            f.AcceptPick(a.CreateReference(o[1], FaceAtZ(ModelOf(o[1]), 0.01))); Pump(4);
            string ui = f.UiSummary();
            L("   点圆弧 + T 顶面：" + ui);
            Check(ui.IndexOf("腰孔 ×1") >= 0, "预览：将打 腰孔 ×1");
            f.DrillForTest(); Pump(8);
            L("   RESULT: " + R(f.LastResult));
            var mt = ModelOf(o[1]);
            Check(HasCircle(mt, 0.0395, 0.03, 0.01) && HasCircle(mt, 0.0605, 0.03, 0.01), "T 顶面上两头圆弧都在（30×9 腰孔配上了）");
        } finally { if (f != null) try { f.Close(); } catch { } Pump(4); try { a.Close(false); } catch { } Pump(6); }
    }

    // 0.9.1：点面认孔连腰孔一起认（用户「点面认不全」：在他的零件上比对，漏的几乎全是腰孔）
    static void S11(string outDir, string stamp){
        L("======== S11 0.9.1 点面认孔连腰孔一起认");
        string pr = Path.Combine(outDir, "S11R_" + stamp + ".par"), pt = Path.Combine(outDir, "S11T_" + stamp + ".par");
        var r = NewPart(); var mr = Plate(r, 0.12, 0.06, 0.01);
        Drill(r, mr, 0.01, Req(0.02, 0.03, 0.01, Thru(6.6), "R1"), Req(0.10, 0.03, 0.01, Thru(6.6), "R2"));
        var sr = AutoHoleWriter.DrillSlots(AutoHoleReader.ReadTarget(FaceAtZ(mr, 0.01)), new[]{ new V3(0.06, 0.03, 0.01) }, new[]{ new V3(1, 0, 0) }, 30, 9); Pump(4);
        if (sr.Created != 1) throw new InvalidOperationException("夹具腰孔没切成：" + string.Join("；", sr.Failures));
        r.SaveAs(pr); r.Close(false);
        var t = NewPart(); Plate(t, 0.12, 0.06, 0.01); t.SaveAs(pt); t.Close(false);
        A.Occurrence[] o;
        var a = NewAsm(Path.Combine(outDir, "S11_" + stamp + ".asm"), out o, pr, new[]{ 0.0, 0.0, 0.01 }, pt, new[]{ 0.0, 0.0, 0.0 });
        AutoHoleForm f = null;
        try {
            f = new AutoHoleForm(app, a); f.SuppressDialogs = true; f.Show(); Pump(6);
            f.KeepLink = false;
            f.AcceptPick(a.CreateReference(o[0], FaceAtZ(ModelOf(o[0]), 0.01))); Pump(4);
            string ui = f.UiSummary(); L("   参考孔页点 R 顶面：" + ui);
            Check(ui.IndexOf("参考孔 1 组") >= 0 && ui.IndexOf("×2") >= 0 && ui.IndexOf("腰孔 1 条") >= 0, "R 顶面：2 个 Φ6.6 + 1 条 30×9 腰孔都认出来");
            f.AcceptPick(a.CreateReference(o[0], FaceAtZ(ModelOf(o[0]), 0.01))); Pump(4);
            Check(f.UiSummary().IndexOf("腰孔 1 条") >= 0, "同一张面再点一次：腰孔不重复加");
            f.ScanFaceOnPick = false;
            f.AcceptPick(a.CreateReference(o[1], FaceAtZ(ModelOf(o[1]), 0.01))); Pump(4);
            ui = f.UiSummary(); L("   打孔面页点 T 顶面：" + ui);
            Check(ui.IndexOf("将打") >= 0 && ui.IndexOf("腰孔 ×1") >= 0, "预览：2 个孔 + 腰孔 ×1");
            f.DrillForTest(); Pump(8);
            L("   RESULT: " + R(f.LastResult));
            var mt = ModelOf(o[1]);
            Check(HasCircle(mt, 0.02, 0.03, 0.01) && HasCircle(mt, 0.10, 0.03, 0.01), "T 上两个圆孔打上");
            Check(HasCircle(mt, 0.0495, 0.03, 0.01) && HasCircle(mt, 0.0705, 0.03, 0.01), "T 上 30×9 腰孔配上（两头圆弧都在）");
        } finally { if (f != null) try { f.Close(); } catch { } Pump(4); try { a.Close(false); } catch { } Pump(6); }
    }

    static void S5(string outDir, string stamp){
        L("======== S5 盲孔比板还深（10 厚板上打 15 深）");
        var p = NewPart(); var m = Plate(p, 0.1, 0.06, 0.01);
        try {
            var r = AutoHoleWriter.DrillRequests(AutoHoleReader.ReadTarget(FaceAtZ(m, 0.01)), new[]{ Req(0.05, 0.03, 0.01, new HoleSpec { Kind = HoleKind.Through, HoleDiameter = 6.6, Depth = 15 }, "深盲孔") }); Pump(4);
            L("   结果：建了 " + r.Created + "，失败：" + string.Join("；", r.Failures) + "；说明：" + string.Join("；", r.Notes) + "；自检：" + r.Audit);
            bool bottom = HasCircle(m, 0.05, 0.03, 0.0);
            L("   底面有没有孔口：" + bottom);
            Check(r.Created == 1 || r.Failures.Count == 1, "要么打成（穿透）要么说清楚（实 建 " + r.Created + "）");
            Check(r.Failures.All(x => x.IndexOf("深") >= 0 || x.IndexOf("厚") >= 0), "失败原因提到深度/板厚");
            Check(!bottom || r.Notes.Any(x => x.IndexOf("穿出去") >= 0), "打穿了就在结果里说「穿出去了」");
            // 薄板（这块 10 厚）上 M6 沉孔深 6.8：不该报；再换一块 5 厚的：沉头切穿，要报
            var cb = HoleMatcher.FromRow(HoleMatcher.Find("M6").Value, HoleKind.Counterbore);
            var r2 = AutoHoleWriter.DrillRequests(AutoHoleReader.ReadTarget(FaceAtZ(m, 0.01)), new[]{ Req(0.02, 0.03, 0.01, cb, "10 厚上的沉孔") }); Pump(4);
            L("   10 厚上 M6 沉孔：建了 " + r2.Created + "，说明：" + string.Join("；", r2.Notes));
            Check(r2.Created == 1 && !r2.Notes.Any(x => x.IndexOf("切穿") >= 0 || x.IndexOf("还深") >= 0), "10 厚板上的 M6 沉孔不误报");
            var p5 = NewPart(); var m5 = Plate(p5, 0.06, 0.06, 0.005);
            try {
                var r3 = AutoHoleWriter.DrillRequests(AutoHoleReader.ReadTarget(FaceAtZ(m5, 0.005)), new[]{ Req(0.03, 0.03, 0.005, cb, "5 厚上的沉孔") }); Pump(4);
                L("   5 厚上 M6 沉孔：建了 " + r3.Created + "，说明：" + string.Join("；", r3.Notes));
                Check(r3.Notes.Any(x => x.IndexOf("通孔") >= 0 && x.IndexOf("沉孔深") >= 0), "5 厚板上的 M6 沉孔切穿了：结果里说清楚");
            } finally { try { p5.Close(false); } catch { } Pump(2); }
        } finally { try { p.Close(false); } catch { } Pump(4); }
    }

    // 0.9.1 用户要的：点平面的意思按页面定、点了不翻页。
    //   参考孔页上连点两张带孔的面都认孔（第二张面上有已选参考孔投得到的空位，0.9.0 会把它当打孔面并跳到「打孔面」）；
    //   参考孔页上点没孔的面不当打孔面；打孔面页上点带孔的面 = 打孔面（撤掉从它认进来的参考孔），Ctrl 点 = 认孔；
    //   打孔面页上点孔口圆边照样加参考孔、不翻页；预先选好的对象仍按老规则（有空位就当打孔面）
    static void S10(string outDir, string stamp){
        L("======== S10 0.9.1 点平面按页面定、点了不翻页");
        string pr = Path.Combine(outDir, "S10R_" + stamp + ".par"), pt = Path.Combine(outDir, "S10T_" + stamp + ".par");
        var r = NewPart(); var mr = Plate(r, 0.1, 0.06, 0.01);
        Drill(r, mr, 0.01, Req(0.03, 0.03, 0.01, Thru(6.6), "R1"), Req(0.07, 0.03, 0.01, Thru(6.6), "R2"));
        r.SaveAs(pr); r.Close(false);
        var t = NewPart(); var mt = Plate(t, 0.1, 0.06, 0.02);
        Drill(t, mt, 0.02, Req(0.05, 0.048, 0.02, new HoleSpec { Kind = HoleKind.Through, HoleDiameter = 10, Depth = 8 }, "T 顶上已有的盲孔（底面要没孔）"));
        t.SaveAs(pt); t.Close(false);
        A.Occurrence[] o;
        var a = NewAsm(Path.Combine(outDir, "S10_" + stamp + ".asm"), out o, pr, new[]{ 0.0, 0.0, 0.02 }, pt, new[]{ 0.0, 0.0, 0.0 });
        AutoHoleForm f = null;
        try {
            f = new AutoHoleForm(app, a); f.SuppressDialogs = true; f.Show(); Pump(8);
            f.KeepLink = false;
            Check(f.PageForTest == 0, "开窗在参考孔页");
            f.AcceptPick(a.CreateReference(o[0], FaceAtZ(ModelOf(o[0]), 0.01))); Pump(4);
            string ui = f.UiSummary(); L("   参考孔页点 R 顶面：" + ui);
            Check(ui.IndexOf("参考孔 1 组") >= 0 && ui.IndexOf("×2") >= 0 && f.PageForTest == 0, "R 顶面认出 2 个孔，还在参考孔页");
            f.AcceptPick(a.CreateReference(o[1], FaceAtZ(ModelOf(o[1]), 0.02))); Pump(4);
            ui = f.UiSummary(); L("   参考孔页点 T 顶面：" + ui);
            Check(ui.IndexOf("参考孔 2 组") >= 0 && ui.IndexOf("打孔面 0 个") >= 0, "T 顶面（R 的孔投得到它的空位）照样认孔：Φ10 成第 2 组，不当打孔面");
            Check(f.PageForTest == 0, "认完不跳到打孔面页");
            f.AcceptPick(a.CreateReference(o[1], FaceAtZ(ModelOf(o[1]), 0.0))); Pump(4);
            ui = f.UiSummary(); L("   参考孔页点 T 底面（没孔）：" + ui);
            Check(ui.IndexOf("打孔面 0 个") >= 0 && ui.IndexOf("没有认出孔") >= 0 && f.PageForTest == 0, "参考孔页点没孔的面：不当打孔面、说清楚去「打孔面」那一步");
            f.GoPageForTest(1);
            f.AcceptPick(a.CreateReference(o[1], FaceAtZ(ModelOf(o[1]), 0.02))); Pump(4);
            ui = f.UiSummary(); L("   打孔面页点 T 顶面：" + ui);
            Check(ui.IndexOf("打孔面 1 个") >= 0 && ui.IndexOf("参考孔 1 组") >= 0 && f.PageForTest == 1, "打孔面页点带孔的 T 顶面 = 打孔面，从它认进来的 Φ10 撤掉，还在打孔面页");
            Check(ui.IndexOf("将打 2 个孔") >= 0, "预览：将打 2 个孔");
            f.AcceptPick(a.CreateReference(o[1], FaceAtZ(ModelOf(o[1]), 0.02)), true); Pump(4);
            ui = f.UiSummary(); L("   打孔面页 Ctrl 点 T 顶面：" + ui);
            Check(ui.IndexOf("参考孔 2 组") >= 0 && f.PageForTest == 1, "打孔面页 Ctrl 点 = 认面上的孔（Φ10 又回来了）");
            var e10 = CircleEdge(ModelOf(o[1]), 0.05, 0.048, 0.02);
            f.AcceptPick(a.CreateReference(o[1], e10)); Pump(4);
            ui = f.UiSummary(); L("   打孔面页点 Φ10 孔口圆边（再点一次 = 取消）：" + ui);
            Check(ui.IndexOf("参考孔 1 组") >= 0 && f.PageForTest == 1, "打孔面页点孔口圆边照样管参考孔、不翻页");
            f.ClearAllForTest(); Pump(2);
            f.AcceptPreselectedForTest(a.CreateReference(o[0], FaceAtZ(ModelOf(o[0]), 0.01))); Pump(4);
            f.AcceptPreselectedForTest(a.CreateReference(o[1], FaceAtZ(ModelOf(o[1]), 0.02))); Pump(4);
            ui = f.UiSummary(); L("   预先选好 R 顶面 + T 顶面：" + ui);
            Check(ui.IndexOf("参考孔 1 组") >= 0 && ui.IndexOf("打孔面 1 个") >= 0, "预先选好的对象按老规则：T 顶面有空位 = 打孔面");
            f.Close(); f = null; Pump(4);
        } finally { if (f != null) try { f.Close(); } catch { } Pump(4); }
    }
}

[ComImport, Guid("00000016-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IE2EMessageFilterU {
    [PreserveSig] int HandleInComingCall(int type, IntPtr task, int ticks, IntPtr info);
    [PreserveSig] int RetryRejectedCall(IntPtr task, int ticks, int reject);
    [PreserveSig] int MessagePending(IntPtr task, int ticks, int pending);
}
sealed class E2EFilterU : IE2EMessageFilterU {
    [DllImport("ole32.dll")] static extern int CoRegisterMessageFilter(IE2EMessageFilterU f, out IE2EMessageFilterU old);
    static E2EFilterU keep;
    public static void Register(){ keep = new E2EFilterU(); IE2EMessageFilterU old; CoRegisterMessageFilter(keep, out old); }
    public int HandleInComingCall(int a, IntPtr b, int c, IntPtr d){ return 0; }
    public int RetryRejectedCall(IntPtr a, int ticks, int reject){ return ticks < 120000 ? 200 : -1; }
    public int MessagePending(IntPtr a, int b, int c){ return 2; }
}
