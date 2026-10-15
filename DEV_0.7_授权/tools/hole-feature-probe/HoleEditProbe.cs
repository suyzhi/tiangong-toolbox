// tools/hole-feature-probe/HoleEditProbe.cs —— 「改孔型」和「种子孔 + 阵列」接进插件之前要量清的事（2026-10-14）。
//
// HoleMultiProbe 的 P2 里，种子孔从 Φ6 通孔换成 Φ6.6/Φ11 沉孔后切除体积少了约 7%：
// 三个孔各少 78.4mm³ = 沉孔下面那段按 Φ6 而不是 Φ6.6 切 —— 换 HoleData 后孔径没跟着变？
//   X1 Φ6 通孔换成 Φ6.6/Φ11 沉孔：回读孔径、切除体积；再直接改新 HoleData 的 HoleDiameter 看能不能补上
//   X2 不换对象，原地改孔的 HoleData.HoleDiameter（Φ6 → Φ6.6）
//   X3 Φ6 通孔换成 Φ9 通孔（变大）；列出孔特征的尺寸（GetDimensions）
//   X4 撤销：把原来那个 HoleData 对象赋回去，体积能不能复原
//   X5 贯通 ↔ 盲孔：改 ExtentType / Depth
//   X6 矩形阵列 RectangleAngle 的单位和方向（30°，看阵列出来的孔心）
//   X7 圆周阵列 AddByCircular（AxisPoint 传 3D 点），看孔心
//   X8 种子孔 + 阵列编成一个特征组；阵列的 NumberOfOccurrences / GetInputFeatures
// 运行：tools\run-hole-feature-probe.ps1 -Exe build\HoleEditProbe.exe -OutDir artifacts\hole-edit-probe
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using F=SolidEdgeFramework;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;
using S=SolidEdgeFrameworkSupport;

class HoleEditProbe {
    static object M = Type.Missing;
    static string Out = ".";
    static F.Application app;
    const string Tpl = @"C:\Program Files\NDS\TianGong 2025\Template\ISO Metric\";

    static void Say(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    static double D(Array a, int i){ return Convert.ToDouble(a.GetValue(i)); }
    static string Mm(double m){ return (m * 1000.0).ToString("0.###"); }
    static string Hr(Exception e){ return "0x" + e.HResult.ToString("X8") + " " + e.Message; }

    static int Main(string[] args){
        Out = args.Length > 0 ? args[0] : ".";
        Directory.CreateDirectory(Out);
        app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application");
        Say("CAD " + app.Version);
        try { app.DisplayAlerts = false; } catch { }
        bool circular = args.Length > 1 && args[1] == "circ";
        // 第一次 COM 调用常撞上 RPC_E_SERVERCALL_RETRYLATER（CAD 刚起来还在忙），先等一下
        for (int i = 0; i < 20; i++) { try { app.DoIdle(); var n = app.Documents.Count; break; } catch { System.Threading.Thread.Sleep(1000); } }
        if (args.Length > 1 && args[1] == "ext") {
            Run("X1 换 HoleData（Φ6 → Φ6.6/Φ11 沉孔）", X1);
            Run("X5b 贯通 → 盲孔（看 ExtentSide，失败就翻面）", X5b);
            Run("X9 盲孔 → 贯通 / 换盲孔深度", X9);
            Run("X7 圆周阵列", X7);
            Say("PROBE DONE");
            return 0;
        }
        if (!circular) {
            Run("X1 换 HoleData（Φ6 → Φ6.6/Φ11 沉孔）", X1);
            Run("X2 原地改 HoleDiameter", X2);
            Run("X3 换 HoleData（Φ6 → Φ9）+ 尺寸", X3);
            Run("X4 赋回原来的 HoleData", X4);
            Run("X5 贯通 ↔ 盲孔", X5);
            Run("X6 矩形阵列 30°", X6);
            Run("X8 种子孔 + 阵列编组", X8);
        } else {
            Run("X7 圆周阵列", X7);
        }
        Say("PROBE DONE");
        return 0;
    }
    static void Run(string name, Action a){
        Say(""); Say("==== " + name);
        try { a(); } catch (Exception e) { Say("FATAL " + name + " : " + e.GetType().Name + " " + Hr(e)); }
    }

    // ---------------- 夹具（和 HoleMultiProbe 同一套：100×60×20 的板，顶面 z=20） ----------------
    sealed class Plate { public P.PartDocument Part; public P.Model Model; public double Top; public double T; }
    static Plate NewPlate(){
        double w = 0.1, h = 0.06, t = 0.02;
        var part = (P.PartDocument)app.Documents.Add("SolidEdge.PartDocument", Tpl + "iso metric part.par");
        part.ModelingMode = P.ModelingModeConstants.seModelingModeOrdered;
        var prof = part.ProfileSets.Add().Profiles.Add(FindXY(part));
        var L = new S.Line2d[4];
        L[0] = prof.Lines2d.AddBy2Points(0, 0, w, 0); L[1] = prof.Lines2d.AddBy2Points(w, 0, w, h);
        L[2] = prof.Lines2d.AddBy2Points(w, h, 0, h); L[3] = prof.Lines2d.AddBy2Points(0, h, 0, 0);
        var rel = (S.Relations2d)prof.Relations2d;
        for (int i = 0; i < 4; i++) rel.AddKeypoint(L[i], (int)SolidEdgeConstants.KeypointIndexConstants.igLineEnd, L[(i+1)%4], (int)SolidEdgeConstants.KeypointIndexConstants.igLineStart);
        if (prof.End(P.ProfileValidationType.igProfileClosed) != 0) throw new InvalidOperationException("底板不闭合");
        Array arr = new object[]{ prof };
        var model = part.Models.AddFiniteExtrudedProtrusion(1, ref arr, P.FeaturePropertyConstants.igRight, t);
        var pl = new Plate { Part = part, Model = model, T = t };
        double zmax = double.MinValue;
        foreach (G.Face f in Faces(pl)) {
            var g = f.Geometry as G.Plane; if (g == null) continue;
            Array a = new double[3], n = new double[3]; g.GetPlaneData(ref a, ref n);
            if (Math.Abs(Math.Abs(D(n,2)) - 1) > 1e-6) continue;
            zmax = Math.Max(zmax, D(a,2));
        }
        pl.Top = zmax;
        return pl;
    }
    static IEnumerable<G.Face> Faces(Plate p){
        foreach (G.Face f in (G.Faces)((G.Body)p.Model.Body).get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll)) yield return f;
    }
    static double Vol(Plate p){ return ((G.Body)p.Model.Body).Volume; }
    static G.Face TopFace(Plate p){
        foreach (G.Face f in Faces(p)) {
            var g = f.Geometry as G.Plane; if (g == null) continue;
            Array a = new double[3], n = new double[3]; g.GetPlaneData(ref a, ref n);
            if (Math.Abs(Math.Abs(D(n,2)) - 1) < 1e-6 && Math.Abs(D(a,2) - p.Top) < 1e-9) return f;
        }
        return null;
    }
    static P.RefPlane FindXY(P.PartDocument part){
        foreach (P.RefPlane c in part.RefPlanes) {
            Array n = new double[3], q = new double[3], u = new double[3];
            c.GetNormal(ref n); c.GetRootPoint(ref q); c.GetReferenceDirection(ref u);
            if (Math.Abs(D(n,2)-1) < 1e-8 && Math.Abs(D(q,0)) < 1e-8 && Math.Abs(D(q,1)) < 1e-8 && Math.Abs(D(u,0)-1) < 1e-8) return c;
        }
        return null;
    }
    static P.RefPlane TopPlane(Plate p){
        var pl = p.Part.RefPlanes.AddParallelByDistance(TopFace(p), 0.0, P.ReferenceElementConstants.igNormalSide, M, M, M, M);
        try {
            Array n = new double[3], u = new double[3]; pl.GetNormal(ref n); pl.GetReferenceDirection(ref u);
            Say("   顶面基准面 法向(" + D(n,0) + "," + D(n,1) + "," + D(n,2) + ") 参考方向(" + D(u,0) + "," + D(u,1) + "," + D(u,2) + ")");
        } catch (Exception e) { Say("   读基准面方向异常 " + Hr(e)); }
        return pl;
    }
    static P.Profile OwnProfile(Plate p, P.RefPlane plane, double x, double y){
        var prof = p.Part.ProfileSets.Add().Profiles.Add(plane);
        double x2, y2; prof.Convert3DCoordinate(x, y, p.Top, out x2, out y2);
        prof.Holes2d.Add(x2, y2);
        int end = prof.End(P.ProfileValidationType.igProfileClosed);
        if (end != 0) Say("   注意：轮廓 End 返回 " + end);
        return prof;
    }
    static P.HoleData Regular(Plate p, double diaM){
        return p.Part.HoleDataCollection.AddEx(P.FeaturePropertyConstants.igRegularHole, "ISO Metric", M, M, M, diaM,
            0.0, 0.0, 0.0, 0.0, 0.0, P.FeaturePropertyConstants.igNone, M, M, M, M, M, P.FeaturePropertyConstants.igVBottomDimToFlat, M,
            M, M, M, M, M, true, M, M, M, M, M, M, M, M, M, M, M, M);
    }
    static P.HoleData Counterbore(Plate p, double diaM, double cbDiaM, double cbDepM){
        return p.Part.HoleDataCollection.AddEx(P.FeaturePropertyConstants.igCounterboreHole, "ISO Metric", M, M, M, diaM,
            cbDiaM, cbDepM, 0.0, 0.0, 0.0, P.FeaturePropertyConstants.igNone, M, M, M, M, M, P.FeaturePropertyConstants.igVBottomDimToFlat, M,
            P.FeaturePropertyConstants.igCounterboreProfileIsAtTop, M, M, M, M, true, M, M, M, M, M, M, M, M, M, M, M, M);
    }
    static P.Hole Through(Plate p, P.Profile prof, P.HoleData data){
        foreach (var side in new[]{ P.FeaturePropertyConstants.igLeft, P.FeaturePropertyConstants.igRight }) {
            P.Hole h = null;
            try { h = p.Model.Holes.AddThroughAll(prof, side, data); } catch (Exception e) { Say("   AddThroughAll(" + side + ") 异常 " + Hr(e)); continue; }
            if (h == null) continue;
            object desc; var st = h.GetStatusEx(out desc);
            if ((int)st == (int)P.FeatureStatusConstants.igFeatureOK) return h;
            Say("   AddThroughAll(" + side + ") 状态 " + st);
            try { h.Delete(); } catch { }
        }
        return null;
    }
    static double Cyl(double diaM, double hM){ return Math.PI * diaM * diaM / 4 * hM; }
    static double CbVol(Plate p, double dia, double cbd, double cbdep){ return Cyl(dia, p.T) + Cyl(cbd, cbdep) - Cyl(dia, cbdep); }
    static List<string> HolesOnTop(Plate p){
        var res = new List<string>();
        foreach (G.Edge e in (G.Edges)((G.Body)p.Model.Body).get_Edges(G.FeatureTopologyQueryTypeConstants.igQueryAll)) {
            var c = e.Geometry as G.Circle; if (c == null) continue;
            Array cc = new double[3], ax = new double[3]; double r;
            c.GetCircleData(ref cc, ref ax, out r);
            if (Math.Abs(D(cc,2) - p.Top) > 1e-7) continue;
            res.Add("(" + Mm(D(cc,0)) + "," + Mm(D(cc,1)) + ")Φ" + Mm(2*r));
        }
        res.Sort();
        return res;
    }
    static void Report(string tag, Plate p, double v0, double want){
        double cut = v0 - Vol(p);
        Say("RESULT " + tag + "：切除比 " + (want > 0 ? (cut / want).ToString("0.000") : "-") + "（切除 " + (cut * 1e9).ToString("0.#") + " / 理论 " + (want * 1e9).ToString("0.#") + " mm³）"
            + " 顶面圆：" + string.Join(" ", HolesOnTop(p).ToArray()));
    }
    static void Status(string tag, object feature){
        try { object d; var st = ((dynamic)feature).GetStatusEx(out d); Say("   " + tag + " 状态 " + ((int)st == (int)P.FeatureStatusConstants.igFeatureOK ? "OK" : st.ToString()) + (d == null ? "" : "（" + d + "）")); }
        catch (Exception e) { Say("   " + tag + " 读状态异常 " + Hr(e)); }
    }
    static void Step(string what, Action a){ try { a(); Say("   " + what + "：成功"); } catch (Exception e) { Say("   " + what + "：异常 " + Hr(e)); } }
    static void Recompute(Plate p){ try { p.Model.Recompute(); } catch (Exception e) { Say("   Recompute 异常 " + Hr(e)); } }
    static void ReadData(string tag, P.Hole h){
        try {
            var d = (P.HoleData)h.HoleData;
            Say("   " + tag + " 回读：孔型 " + d.HoleType + " 孔径Φ" + Mm(d.HoleDiameter) + " 沉孔Φ" + Mm(d.CounterboreDiameter) + " 深" + Mm(d.CounterboreDepth));
        } catch (Exception e) { Say("   " + tag + " 回读异常 " + Hr(e)); }
        try { Say("   " + tag + " 范围 " + h.ExtentType + " 深 " + SafeDepth(h)); } catch (Exception e) { Say("   " + tag + " 读范围异常 " + Hr(e)); }
    }
    static string SafeDepth(P.Hole h){ try { return Mm(h.Depth); } catch (Exception e) { return "异常 " + Hr(e); } }
    static void Dims(string tag, P.Hole h){
        try {
            int n = 0; Array arr = new object[0];
            ((dynamic)h).GetDimensions(out n, ref arr);
            Say("   " + tag + " 尺寸数 " + n);
            for (int i = 0; i < n; i++) {
                try { dynamic dm = arr.GetValue(i); Say("     尺寸 " + i + "：值 " + Mm((double)dm.Value) + " 类型 " + dm.DimensionType); }
                catch (Exception e) { Say("     尺寸 " + i + " 读异常 " + Hr(e)); }
            }
        } catch (Exception e) { Say("   " + tag + " GetDimensions 异常 " + Hr(e)); }
    }
    static void Close(Plate p, string save){
        try { if (save != null) p.Part.SaveAs(Path.Combine(Out, save)); } catch (Exception e) { Say("   保存失败 " + e.Message); }
        try { p.Part.Close(false); } catch { }
    }

    // ---------------- X ----------------
    static void X1(){
        var p = NewPlate(); double v0 = Vol(p); var plane = TopPlane(p);
        var h = Through(p, OwnProfile(p, plane, 0.05, 0.03), Regular(p, 0.006));
        Report("X1 原始 Φ6", p, v0, Cyl(0.006, p.T));
        Dims("X1 原始", h);
        Step("换成 Φ6.6/Φ11 沉孔", delegate { h.HoleData = Counterbore(p, 0.0066, 0.011, 0.0068); });
        Recompute(p); Status("X1 换后", h); ReadData("X1 换后", h);
        Report("X1 换后（理论按 Φ6.6）", p, v0, CbVol(p, 0.0066, 0.011, 0.0068));
        Say("   （按 Φ6 算的理论值 " + (CbVol(p, 0.006, 0.011, 0.0068) * 1e9).ToString("0.#") + "）");
        Dims("X1 换后", h);
        Step("再直接改 HoleDiameter = 6.6", delegate { ((P.HoleData)h.HoleData).HoleDiameter = 0.0066; });
        Recompute(p); Status("X1 改孔径后", h); ReadData("X1 改孔径后", h);
        Report("X1 改孔径后（理论按 Φ6.6）", p, v0, CbVol(p, 0.0066, 0.011, 0.0068));
        Close(p, "X1.par");
    }
    static void X2(){
        var p = NewPlate(); double v0 = Vol(p); var plane = TopPlane(p);
        var h = Through(p, OwnProfile(p, plane, 0.05, 0.03), Regular(p, 0.006));
        Step("原地改 HoleDiameter = 6.6", delegate { ((P.HoleData)h.HoleData).HoleDiameter = 0.0066; });
        Recompute(p); Status("X2", h); ReadData("X2", h);
        Report("X2（理论 Φ6.6）", p, v0, Cyl(0.0066, p.T));
        Step("原地改 孔型=沉孔 Φ11 深6.8", delegate {
            var d = (P.HoleData)h.HoleData;
            d.HoleType = P.FeaturePropertyConstants.igCounterboreHole; d.CounterboreDiameter = 0.011; d.CounterboreDepth = 0.0068;
        });
        Recompute(p); Status("X2 原地改沉孔", h); ReadData("X2 原地改沉孔", h);
        Report("X2 原地改沉孔（理论 Φ6.6/Φ11）", p, v0, CbVol(p, 0.0066, 0.011, 0.0068));
        Close(p, "X2.par");
    }
    static void X3(){
        var p = NewPlate(); double v0 = Vol(p); var plane = TopPlane(p);
        var h = Through(p, OwnProfile(p, plane, 0.05, 0.03), Regular(p, 0.006));
        Step("换成 Φ9 通孔", delegate { h.HoleData = Regular(p, 0.009); });
        Recompute(p); Status("X3", h); ReadData("X3", h);
        Report("X3（理论 Φ9）", p, v0, Cyl(0.009, p.T));
        Dims("X3", h);
        Close(p, "X3.par");
    }
    static void X4(){
        var p = NewPlate(); double v0 = Vol(p); var plane = TopPlane(p);
        var h = Through(p, OwnProfile(p, plane, 0.05, 0.03), Regular(p, 0.0066));
        object old = h.HoleData;
        double before = Vol(p);
        Step("换成沉孔", delegate { h.HoleData = Counterbore(p, 0.0066, 0.011, 0.0068); });
        Recompute(p);
        Report("X4 换成沉孔", p, v0, CbVol(p, 0.0066, 0.011, 0.0068));
        Step("赋回原来的 HoleData 对象", delegate { h.HoleData = old; });
        Recompute(p); Status("X4 赋回后", h); ReadData("X4 赋回后", h);
        Say("RESULT X4 赋回后体积差 " + ((Vol(p) - before) * 1e9).ToString("0.###") + " mm³（0 = 完全复原）");
        Close(p, "X4.par");
    }
    static void X5(){
        var p = NewPlate(); double v0 = Vol(p); var plane = TopPlane(p);
        var h = Through(p, OwnProfile(p, plane, 0.05, 0.03), Regular(p, 0.0066));
        ReadData("X5 原始", h);
        Step("ExtentType = igFinite", delegate { h.ExtentType = P.FeaturePropertyConstants.igFinite; });
        Step("Depth = 10", delegate { h.Depth = 0.010; });
        Recompute(p); Status("X5 改盲孔", h); ReadData("X5 改盲孔", h);
        Report("X5 改盲孔 深10（理论）", p, v0, Cyl(0.0066, 0.010));
        Step("ExtentType = igThroughAll", delegate { h.ExtentType = P.FeaturePropertyConstants.igThroughAll; });
        Recompute(p); Status("X5 改回贯通", h); ReadData("X5 改回贯通", h);
        Report("X5 改回贯通（理论）", p, v0, Cyl(0.0066, p.T));
        Close(p, "X5.par");
    }
    static string Side(P.Hole h){ try { return h.ExtentSide.ToString(); } catch (Exception e) { return "异常 " + Hr(e); } }
    static bool Ok(P.Hole h){ try { object d; return (int)h.GetStatusEx(out d) == (int)P.FeatureStatusConstants.igFeatureOK; } catch { return false; } }
    static void X5b(){
        var p = NewPlate(); double v0 = Vol(p); var plane = TopPlane(p);
        var h = Through(p, OwnProfile(p, plane, 0.05, 0.03), Regular(p, 0.0066));
        Say("   原始 ExtentSide " + Side(h));
        Step("Depth = 10（先设深度）", delegate { h.Depth = 0.010; });
        Step("ExtentType = igFinite", delegate { h.ExtentType = P.FeaturePropertyConstants.igFinite; });
        Recompute(p); Status("X5b 改盲孔", h); Say("   ExtentSide " + Side(h));
        Report("X5b 改盲孔 深10", p, v0, Cyl(0.0066, 0.010));
        if (!Ok(h)) {
            var other = h.ExtentSide == P.FeaturePropertyConstants.igLeft ? P.FeaturePropertyConstants.igRight : P.FeaturePropertyConstants.igLeft;
            Step("ExtentSide = " + other, delegate { h.ExtentSide = other; });
            Recompute(p); Status("X5b 翻面后", h);
            Report("X5b 翻面后 深10", p, v0, Cyl(0.0066, 0.010));
        }
        Step("Depth = 15", delegate { h.Depth = 0.015; });
        Recompute(p); Status("X5b 改深15", h);
        Report("X5b 深15", p, v0, Cyl(0.0066, 0.015));
        Close(p, "X5b.par");
    }
    static void X9(){
        var p = NewPlate(); double v0 = Vol(p); var plane = TopPlane(p);
        var prof = OwnProfile(p, plane, 0.05, 0.03);
        P.Hole h = null;
        foreach (var side in new[]{ P.FeaturePropertyConstants.igLeft, P.FeaturePropertyConstants.igRight }) {
            try { h = p.Model.Holes.AddFinite(prof, side, 0.010, Regular(p, 0.0066)); } catch (Exception e) { Say("   AddFinite(" + side + ") 异常 " + Hr(e)); continue; }
            if (h != null && Ok(h)) { Say("   AddFinite 用的面 " + side); break; }
            try { if (h != null) h.Delete(); } catch { } h = null;
        }
        if (h == null) { Say("RESULT X9 建盲孔失败"); Close(p, null); return; }
        Say("   ExtentSide " + Side(h) + " 深 " + SafeDepth(h));
        Report("X9 盲孔 深10", p, v0, Cyl(0.0066, 0.010));
        Step("ExtentType = igThroughAll", delegate { h.ExtentType = P.FeaturePropertyConstants.igThroughAll; });
        Recompute(p); Status("X9 改贯通", h);
        Report("X9 改贯通", p, v0, Cyl(0.0066, p.T));
        Step("ExtentType = igFinite（回盲孔）", delegate { h.ExtentType = P.FeaturePropertyConstants.igFinite; });
        Step("Depth = 12", delegate { h.Depth = 0.012; });
        Recompute(p); Status("X9 回盲孔 12", h);
        Report("X9 回盲孔 深12", p, v0, Cyl(0.0066, 0.012));
        Close(p, "X9.par");
    }
    static P.Pattern Rect(Plate p, P.RefPlane plane, P.Hole seed, int n, double spacing, double angle){
        P.Pattern pat = null;
        Step("AddByRectangular(" + n + "×1, 间距 " + Mm(spacing) + ", 角 " + angle + ")", delegate {
            Array feats = new object[]{ seed };
            pat = p.Model.Patterns.AddByRectangular(1, feats, plane, n, 1, spacing, spacing, angle, S.PatternOffsetTypeConstants.sePatternFixedOffset, 1);
        });
        if (pat != null) Status("阵列", pat);
        return pat;
    }
    static void X6(){
        var p = NewPlate(); double v0 = Vol(p); var plane = TopPlane(p);
        var seed = Through(p, OwnProfile(p, plane, 0.02, 0.015), Regular(p, 0.006));
        double a = Math.PI / 6;
        var pat = Rect(p, plane, seed, 3, 0.02, a);
        Say("   期望（弧度、逆时针、从基准面参考方向量）：(20,15) (37.321,25) (54.641,35)");
        Report("X6 30° 弧度", p, v0, 3 * Cyl(0.006, p.T));
        if (pat != null) {
            Step("NumberOfOccurrences=" + SafeInt(delegate { return pat.NumberOfOccurrences; }), delegate { });
            Step("SetRectangularPatternData(4×1, 15mm, 0°)", delegate { pat.SetRectangularPatternData(S.PatternOffsetTypeConstants.sePatternFixedOffset, 4, 1, 0.015, 0.015, 0.0); });
            Recompute(p); Status("改阵列参数后", pat);
            Report("X6 改成 4×15mm 0°", p, v0, 4 * Cyl(0.006, p.T));
            Step("种子孔换沉孔 + 改孔径", delegate { seed.HoleData = Counterbore(p, 0.0066, 0.011, 0.0068); ((P.HoleData)seed.HoleData).HoleDiameter = 0.0066; });
            Recompute(p); Status("种子改后阵列", pat);
            Report("X6 种子改沉孔（理论 4 个 Φ6.6/Φ11）", p, v0, 4 * CbVol(p, 0.0066, 0.011, 0.0068));
            Step("删阵列", delegate { pat.Delete(); });
            Report("X6 删阵列后（理论 1 个沉孔）", p, v0, CbVol(p, 0.0066, 0.011, 0.0068));
        }
        Close(p, "X6.par");
    }
    static string SafeInt(Func<int> f){ try { return f().ToString(); } catch (Exception e) { return "异常 " + Hr(e); } }
    static void X7(){
        var p = NewPlate(); double v0 = Vol(p); var plane = TopPlane(p);
        var seed = Through(p, OwnProfile(p, plane, 0.065, 0.03), Regular(p, 0.006));   // 绕 (50,30) 半径 15
        P.Pattern pat = null;
        foreach (bool arc in new[]{ false, true }) {
            Step("AddByCircular(4 个, 90°, 轴点 3D, 弧阵=" + arc + ")", delegate {
                Array feats = new object[]{ seed };
                Array axis = new double[]{ 0.05, 0.03, p.Top };
                pat = p.Model.Patterns.AddByCircular(1, feats, plane, 4, Math.PI / 2, axis, S.PatternOffsetTypeConstants.sePatternFixedOffset, P.PatternCurveAnchorSideConstants.sePatternCurveLeftSide, arc);
            });
            if (pat != null) { Status("圆周阵列", pat); break; }
        }
        Say("   期望：(65,30) (50,45) (35,30) (50,15)");
        Report("X7 圆周 4 个", p, v0, 4 * Cyl(0.006, p.T));
        if (pat != null) {
            Step("GetCircularPatternData", delegate {
                S.PatternOffsetTypeConstants m; int n; double ang;
                pat.GetCircularPatternData(out m, out n, out ang);
                Say("     方法 " + m + " 个数 " + n + " 角 " + ang);
            });
            Step("GetAxisPosition", delegate { Array ax = new double[3]; pat.GetAxisPosition(ref ax); Say("     轴点 " + D(ax,0) + "," + D(ax,1) + "," + D(ax,2)); });
            Step("SetCircularPatternData(6 个, 60°)", delegate { pat.SetCircularPatternData(S.PatternOffsetTypeConstants.sePatternFixedOffset, 6, Math.PI / 3); });
            Recompute(p); Status("改后", pat);
            Report("X7 改成 6 个", p, v0, 6 * Cyl(0.006, p.T));
        }
        Close(p, "X7.par");
    }
    static void X8(){
        var p = NewPlate(); double v0 = Vol(p); var plane = TopPlane(p);
        var seed = Through(p, OwnProfile(p, plane, 0.02, 0.03), Regular(p, 0.006));
        var pat = Rect(p, plane, seed, 3, 0.03, 0.0);
        if (pat == null) { Close(p, null); return; }
        Step("NumberOfOccurrences=" + SafeInt(delegate { return pat.NumberOfOccurrences; }), delegate { });
        Step("GetInputFeatures", delegate {
            Array arr = new object[0]; pat.GetInputFeatures(ref arr);
            Say("     输入特征数 " + arr.Length + (arr.Length > 0 && arr.GetValue(0) is P.Hole ? "，第一个是孔：" + ((P.Hole)arr.GetValue(0)).Name : ""));
        });
        Step("阵列 Name / SystemName = " + Safe(delegate { return pat.Name + " / " + pat.SystemName; }), delegate { });
        Step("阵列改名", delegate { pat.Name = "M6 通孔 阵列"; });
        Step("阵列挂属性", delegate { var set = ((F.AttributeSets)pat.AttributeSets).Add("TGAutoHoleSpec"); ((F.Attribute)set.Add("Key", F.AttributeTypeConstants.seStringUnicode)).Value = "k"; });
        Step("阵列读属性 " + Safe(delegate { return pat.get_IsAttributeSetPresent("TGAutoHoleSpec").ToString(); }), delegate { });
        P.FeatureGroup g = null;
        Step("BySet[种子, 阵列]", delegate {
            Array arr = new object[]{ seed, pat };
            g = p.Model.FeatureGroups.AddFeatureGroupsBySet(2, ref arr);
            Say("     成员 " + g.Count);
        });
        if (g != null) {
            Step("组成员类型", delegate { for (int i = 1; i <= g.Count; i++) { object m = g.Item(i); Say("     " + i + "：" + (m is P.Hole ? "孔" : m is P.Pattern ? "阵列" : "其它")); } });
            Step("组改名", delegate { g.Name = "M6 通孔 ×3"; });
        }
        // 撤销顺序：散组 → 删阵列 → 删种子孔
        Step("散组", delegate { if (g != null) g.Ungroup(); });
        Step("删阵列", delegate { pat.Delete(); });
        Step("删种子孔", delegate { seed.Delete(); });
        Report("X8 全删后（理论 0）", p, v0, 0);
        Close(p, "X8.par");
    }
    static string Safe(Func<string> f){ try { return f(); } catch (Exception e) { return "异常 " + Hr(e); } }
}
