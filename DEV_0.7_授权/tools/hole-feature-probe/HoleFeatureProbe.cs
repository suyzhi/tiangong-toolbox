using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using A=SolidEdgeAssembly;
using F=SolidEdgeFramework;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;
using S=SolidEdgeFrameworkSupport;

// 孔特征事实探针（2026-10-13）。为"自动打孔 0.7.3"量四件事：
//   T1 一个轮廓放 N 个 Holes2d 能不能一次切出 N 个孔（旧结论是"只切第一个"，要复核）
//   T2 不行的话：N 个轮廓 + Hole.SetProfiles 合进一个特征；再不行就 FeatureGroups 编组
//   T3 Hole.Name 能不能改（特征树里显示的名字）
//   T4 螺纹深度：ThreadDepthMethod / ThreadDepth 各种组合回读；以及现有配方默认给了多深
//   T5 已建好的孔特征，改 Hole2d 圆心能不能让孔跟着走（孔位同步的基础）
//   T6 孔特征上挂 AttributeSets，保存再打开还在不在（记"这个孔跟哪个参考孔走"）
//   T7 装配里跨零件 IncludeEdge（真正的关联）能不能做
// 只在新建文档上操作，产物落在输出目录；不碰用户文件。
class HoleFeatureProbe {
    static object M = Type.Missing;
    static string Out = ".";
    static F.Application app;
    const string Tpl = @"C:\Program Files\NDS\TianGong 2025\Template\ISO Metric\";

    static void Say(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    static double D(Array a, int i){ return Convert.ToDouble(a.GetValue(i)); }
    static string Mm(double m){ return (m * 1000.0).ToString("0.###"); }

    static int Main(string[] args){
        Out = args.Length > 0 ? args[0] : ".";
        Directory.CreateDirectory(Out);
        app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application");
        Say("CAD " + app.Version);
        try { app.DisplayAlerts = false; } catch { }
        Run("T1 单轮廓多孔", T1);
        Run("T2 多轮廓 SetProfiles / FeatureGroups", T2);
        Run("T3 特征改名", T3);
        Run("T4 螺纹深度", T4);
        Run("T5 改孔心", T5);
        Run("T6 属性集持久化", T6);
        Run("T7 跨零件关联", T7);
        Say("PROBE DONE");
        return 0;
    }

    static void Run(string name, Action a){
        Say("");
        Say("==== " + name);
        try { a(); } catch (Exception e) { Say("FATAL " + name + " : " + e.GetType().Name + " 0x" + e.HResult.ToString("X8") + " " + e.Message); }
    }

    // ---------------- 夹具 ----------------
    sealed class Plate { public P.PartDocument Part; public P.Model Model; public double Top; }

    static Plate NewPlate(double w, double h, double t){
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
        var pl = new Plate { Part = part, Model = model };
        double zmin, zmax; ZRange(pl, out zmin, out zmax);
        pl.Top = zmax;
        Say("   板 " + Mm(w) + "x" + Mm(h) + "x" + Mm(t) + " z∈[" + Mm(zmin) + "," + Mm(zmax) + "] 体积 " + (Vol(pl) * 1e9).ToString("0.#") + " mm³");
        return pl;
    }
    static void ZRange(Plate p, out double zmin, out double zmax){
        zmin = double.MaxValue; zmax = double.MinValue;
        foreach (G.Face f in Faces(p)) {
            var pl = f.Geometry as G.Plane; if (pl == null) continue;
            Array a = new double[3], n = new double[3]; pl.GetPlaneData(ref a, ref n);
            if (Math.Abs(Math.Abs(D(n,2)) - 1) > 1e-6) continue;
            zmin = Math.Min(zmin, D(a,2)); zmax = Math.Max(zmax, D(a,2));
        }
    }
    static IEnumerable<G.Face> Faces(Plate p){
        foreach (G.Face f in (G.Faces)((G.Body)p.Model.Body).get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll)) yield return f;
    }
    static double Vol(Plate p){ return ((G.Body)p.Model.Body).Volume; }
    static G.Face TopFace(Plate p){
        foreach (G.Face f in Faces(p)) {
            var pl = f.Geometry as G.Plane; if (pl == null) continue;
            Array a = new double[3], n = new double[3]; pl.GetPlaneData(ref a, ref n);
            if (Math.Abs(Math.Abs(D(n,2)) - 1) < 1e-6 && Math.Abs(D(a,2) - p.Top) < 1e-9) return f;
        }
        return null;
    }
    static P.RefPlane FindXY(P.PartDocument part){
        foreach (P.RefPlane c in part.RefPlanes) {
            Array n = new double[3], p = new double[3], u = new double[3];
            c.GetNormal(ref n); c.GetRootPoint(ref p); c.GetReferenceDirection(ref u);
            if (Math.Abs(D(n,2)-1) < 1e-8 && Math.Abs(D(p,0)) < 1e-8 && Math.Abs(D(p,1)) < 1e-8 && Math.Abs(D(u,0)-1) < 1e-8) return c;
        }
        return null;
    }
    static P.RefPlane TopPlane(Plate p){
        return p.Part.RefPlanes.AddParallelByDistance(TopFace(p), 0.0, P.ReferenceElementConstants.igNormalSide, M, M, M, M);
    }
    static P.Profile NewProfile(Plate p, P.RefPlane plane, IList<double[]> xy){
        var prof = p.Part.ProfileSets.Add().Profiles.Add(plane);
        foreach (var c in xy) {
            double x2, y2; prof.Convert3DCoordinate(c[0], c[1], p.Top, out x2, out y2);
            prof.Holes2d.Add(x2, y2);
        }
        int end = prof.End(P.ProfileValidationType.igProfileClosed);
        if (end != 0) Say("   注意：轮廓 End 返回 " + end);
        return prof;
    }
    static P.HoleData Regular(P.PartDocument part, double diaM){
        return part.HoleDataCollection.AddEx(P.FeaturePropertyConstants.igRegularHole,
            "ISO Metric", M, M, M, diaM,
            0.0, 0.0, 0.0, 0.0, 0.0,
            P.FeaturePropertyConstants.igNone, M, M, M, M, M, P.FeaturePropertyConstants.igVBottomDimToFlat, M,
            M, M, M, M, M, true,
            M, M, M, M, M, M, M, M, M, M, M, M);
    }
    static P.Hole Through(Plate p, P.Profile prof, P.HoleData data){
        foreach (var side in new[]{ P.FeaturePropertyConstants.igLeft, P.FeaturePropertyConstants.igRight }) {
            P.Hole h = null;
            try { h = p.Model.Holes.AddThroughAll(prof, side, data); } catch (Exception e) { Say("   AddThroughAll(" + side + ") 异常 0x" + e.HResult.ToString("X8") + " " + e.Message); continue; }
            if (h == null) continue;
            object desc; var st = h.GetStatusEx(out desc);
            if ((int)st == (int)P.FeatureStatusConstants.igFeatureOK) return h;
            Say("   AddThroughAll(" + side + ") 状态 " + st);
            try { h.Delete(); } catch { }
        }
        return null;
    }
    static P.Hole Finite(Plate p, P.Profile prof, P.HoleData data, double depthM){
        foreach (var side in new[]{ P.FeaturePropertyConstants.igLeft, P.FeaturePropertyConstants.igRight }) {
            P.Hole h = null;
            try { h = p.Model.Holes.AddFinite(prof, side, depthM, data); } catch (Exception e) { Say("   AddFinite(" + side + ") 异常 0x" + e.HResult.ToString("X8") + " " + e.Message); continue; }
            if (h == null) continue;
            object desc; var st = h.GetStatusEx(out desc);
            if ((int)st == (int)P.FeatureStatusConstants.igFeatureOK) return h;
            Say("   AddFinite(" + side + ") 状态 " + st);
            try { h.Delete(); } catch { }
        }
        return null;
    }
    // 本体上所有"在顶面上"的圆边圆心（xy，mm），用来数孔、看孔在哪
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
    static void Close(Plate p, string save){
        try { if (save != null) p.Part.SaveAs(Path.Combine(Out, save)); } catch (Exception e) { Say("   保存失败 " + e.Message); }
        try { p.Part.Close(false); } catch { }
    }
    static double Cyl(double diaM, double hM){ return Math.PI * diaM * diaM / 4 * hM; }
    static void Names(string tag, object feature){
        try {
            dynamic f = feature;
            string n = ""; string eb = ""; string dn = ""; string sn = "";
            try { n = f.Name; } catch { }
            try { eb = f.EdgebarName; } catch { }
            try { dn = f.DisplayName; } catch { }
            try { sn = f.SystemName; } catch { }
            Say("   " + tag + " Name=「" + n + "」 EdgebarName=「" + eb + "」 DisplayName=「" + dn + "」 SystemName=「" + sn + "」");
        } catch (Exception e) { Say("   " + tag + " 读名字异常 " + e.Message); }
    }

    // ---------------- T1 ----------------
    static void T1(){
        var p = NewPlate(0.1, 0.1, 0.02);
        double v0 = Vol(p);
        var plane = TopPlane(p);
        var pts = new List<double[]>{ new[]{0.02,0.02}, new[]{0.08,0.02}, new[]{0.08,0.08}, new[]{0.02,0.08} };
        var prof = NewProfile(p, plane, pts);
        Say("   轮廓 Holes2d.Count=" + prof.Holes2d.Count);
        var h = Through(p, prof, Regular(p.Part, 0.006));
        if (h == null) { Say("RESULT T1 贯通：建不出来"); Close(p, null); return; }
        double cut = v0 - Vol(p), want = 4 * Cyl(0.006, 0.02);
        Say("RESULT T1 贯通 4 孔 1 特征：切除 " + (cut*1e9).ToString("0.#") + " / 理论 " + (want*1e9).ToString("0.#") + " 比 " + (cut/want).ToString("0.000") + "  Holes.Count=" + p.Model.Holes.Count);
        Say("   顶面圆：" + string.Join(" ", HolesOnTop(p).ToArray()));
        int np = 0; Array profs = new object[0];
        try { h.GetProfiles(out np, ref profs); Say("   GetProfiles n=" + np); } catch (Exception e) { Say("   GetProfiles 异常 " + e.Message); }

        // 盲孔版本（另一块板）
        var q = NewPlate(0.1, 0.1, 0.02);
        double q0 = Vol(q);
        var prof2 = NewProfile(q, TopPlane(q), pts);
        var hb = Finite(q, prof2, Regular(q.Part, 0.006), 0.012);
        if (hb == null) Say("RESULT T1 盲孔：建不出来");
        else {
            double c2 = q0 - Vol(q), w2 = 4 * Cyl(0.006, 0.012);
            Say("RESULT T1 盲孔 4 孔 1 特征：切除 " + (c2*1e9).ToString("0.#") + " / 理论 " + (w2*1e9).ToString("0.#") + " 比 " + (c2/w2).ToString("0.000"));
        }
        Close(p, "T1-through.par"); Close(q, "T1-blind.par");
    }

    // ---------------- T2 ----------------
    static void T2(){
        var p = NewPlate(0.1, 0.1, 0.02);
        double v0 = Vol(p);
        var plane = TopPlane(p);
        var pts = new List<double[]>{ new[]{0.02,0.02}, new[]{0.08,0.02}, new[]{0.08,0.08} };
        var profs = new List<P.Profile>();
        foreach (var c in pts) profs.Add(NewProfile(p, plane, new List<double[]>{ c }));
        var data = Regular(p.Part, 0.006);
        var h = Through(p, profs[0], data);
        if (h == null) { Say("RESULT T2 第一个孔就失败"); Close(p, null); return; }
        try {
            Array arr = new object[]{ profs[0], profs[1], profs[2] };
            h.SetProfiles(3, ref arr);
            try { p.Model.Recompute(); } catch (Exception e) { Say("   Recompute 异常 " + e.Message); }
            object desc; var st = h.GetStatusEx(out desc);
            double cut = v0 - Vol(p), want = 3 * Cyl(0.006, 0.02);
            Say("RESULT T2 SetProfiles(3)：状态 " + st + " 切除比 " + (cut/want).ToString("0.000") + " Holes.Count=" + p.Model.Holes.Count);
            Say("   顶面圆：" + string.Join(" ", HolesOnTop(p).ToArray()));
        } catch (Exception e) { Say("RESULT T2 SetProfiles 异常 0x" + e.HResult.ToString("X8") + " " + e.Message); }
        Close(p, "T2-setprofiles.par");

        // FeatureGroups：3 个孔各自成特征，再编成一组
        var q = NewPlate(0.1, 0.1, 0.02);
        var qp = TopPlane(q);
        var holes = new List<object>();
        foreach (var c in pts) {
            var hh = Through(q, NewProfile(q, qp, new List<double[]>{ c }), Regular(q.Part, 0.006));
            if (hh != null) holes.Add(hh);
        }
        try {
            Array arr = holes.ToArray();
            var grp = q.Model.FeatureGroups.AddFeatureGroupsBySet(holes.Count, ref arr);
            Say("RESULT T2 FeatureGroups：" + (grp == null ? "null" : ("建成 Count=" + grp.Count)));
            if (grp != null) {
                Names("编组(改名前)", grp);
                try { grp.Name = "M6螺纹孔×3"; } catch (Exception e) { Say("   编组改名异常 0x" + e.HResult.ToString("X8") + " " + e.Message); }
                Names("编组(改名后)", grp);
            }
        } catch (Exception e) { Say("RESULT T2 FeatureGroups 异常 0x" + e.HResult.ToString("X8") + " " + e.Message); }
        Close(q, "T2-group.par");
    }

    // ---------------- T3 ----------------
    static void T3(){
        var p = NewPlate(0.1, 0.1, 0.02);
        var h = Through(p, NewProfile(p, TopPlane(p), new List<double[]>{ new[]{0.05,0.05} }), Regular(p.Part, 0.0066));
        if (h == null) { Say("RESULT T3 建孔失败"); Close(p, null); return; }
        Names("孔(改名前)", h);
        string want = "M6沉孔 Φ6.6 ×4";
        try { h.Name = want; Say("   Name 赋值未抛异常"); } catch (Exception e) { Say("   Name 赋值异常 0x" + e.HResult.ToString("X8") + " " + e.Message); }
        Names("孔(改名后)", h);
        string path = Path.Combine(Out, "T3-name.par");
        Close(p, "T3-name.par");
        try {
            var re = (P.PartDocument)app.Documents.Open(path);
            var m = (P.Model)re.Models.Item(1);
            Names("孔(重开后)", m.Holes.Item(1));
            re.Close(false);
        } catch (Exception e) { Say("   重开异常 " + e.Message); }
        // 改中文、带 Φ、带 × —— 和上面同一条，单独打一行结论
    }

    // ---------------- T4 ----------------
    static void T4(){
        // 每个变体一块新板（20 厚），打一个 M6 螺纹孔，回读螺纹相关字段
        Variant("现有配方(贯通,ThreadDepthMethod留空)", false, 0, M, M, P.FeaturePropertyConstants.igNone);
        Variant("贯通+igFinite 15mm", false, 0, P.FeaturePropertyConstants.igFinite, 0.015, P.FeaturePropertyConstants.igNone);
        Variant("贯通+igThroughAll", false, 0, P.FeaturePropertyConstants.igThroughAll, M, P.FeaturePropertyConstants.igNone);
        Variant("贯通+igFull", false, 0, P.FeaturePropertyConstants.igFull, M, P.FeaturePropertyConstants.igNone);
        Variant("贯通+Treatment=igTappedHole+igFinite 15", false, 0, P.FeaturePropertyConstants.igFinite, 0.015, P.FeaturePropertyConstants.igTappedHole);
        Variant("盲孔16+留空", true, 0.016, M, M, P.FeaturePropertyConstants.igNone);
        Variant("盲孔16+igFinite 12", true, 0.016, P.FeaturePropertyConstants.igFinite, 0.012, P.FeaturePropertyConstants.igNone);
        Variant("盲孔16+igFull", true, 0.016, P.FeaturePropertyConstants.igFull, M, P.FeaturePropertyConstants.igNone);
        // 建好以后再改 HoleData 属性能不能生效
        PostSet();
    }
    static void Variant(string tag, bool blind, double depthM, object tdm, object td, P.FeaturePropertyConstants treat){
        Plate p = null;
        try {
            p = NewPlate(0.1, 0.1, 0.02);
            var data = p.Part.HoleDataCollection.AddEx(P.FeaturePropertyConstants.igRegularHole,
                "ISO Metric", M, "M6", M, 0.004917,
                0.0, 0.0, 0.0, 0.0, 0.0,
                treat, M, M, 0.004917, tdm, td, P.FeaturePropertyConstants.igVBottomDimToFlat, M,
                M, M, M, M, M, true,
                M, M, M, M, M, M, M, M, M, M, M, M);
            try { data.ThreadExternalDiameter = 0.006; } catch (Exception e) { Say("   设公称径异常 " + e.Message); }
            try { data.ThreadDescription = "M6"; } catch (Exception e) { Say("   设规格异常 " + e.Message); }
            var prof = NewProfile(p, TopPlane(p), new List<double[]>{ new[]{0.05,0.05} });
            var h = blind ? Finite(p, prof, data, depthM) : Through(p, prof, data);
            if (h == null) { Say("RESULT T4 " + tag + "：建不出来"); return; }
            DumpThread("RESULT T4 " + tag, (P.HoleData)h.HoleData);
        } catch (Exception e) { Say("RESULT T4 " + tag + " 异常 0x" + e.HResult.ToString("X8") + " " + e.Message); }
        finally { if (p != null) Close(p, null); }
    }
    static void DumpThread(string tag, P.HoleData d){
        var parts = new List<string>();
        Add(parts, "Treat", () => d.TreatmentType.ToString());
        Add(parts, "TDM", () => d.ThreadDepthMethod.ToString());
        Add(parts, "TD", () => Mm(d.ThreadDepth));
        Add(parts, "Setting", () => d.ThreadSetting.ToString());
        Add(parts, "TGExtent", () => d.TGHoleExtent.ToString());
        Add(parts, "TGDepth", () => Mm(d.TGHoleDepth));
        Add(parts, "Loc", () => d.ThreadLocation.ToString());
        Add(parts, "Inside", () => Mm(d.InsideEffectiveThreadLength));
        Add(parts, "Desc", () => d.ThreadDescription);
        Add(parts, "Nom", () => Mm(d.ThreadNominalDiameter));
        Add(parts, "Ext", () => Mm(d.ThreadExternalDiameter));
        Add(parts, "Minor", () => Mm(d.ThreadMinorDiameter));
        Add(parts, "Pitch", () => Mm(d.TGThreadPitch));
        Add(parts, "Type", () => d.HoleType.ToString());
        Say(tag + "：" + string.Join(" ", parts.ToArray()));
    }
    static void Add(List<string> l, string k, Func<string> f){ try { l.Add(k + "=" + f()); } catch (Exception e) { l.Add(k + "=!" + e.HResult.ToString("X8")); } }

    static void PostSet(){
        Plate p = null;
        try {
            p = NewPlate(0.1, 0.1, 0.02);
            var data = p.Part.HoleDataCollection.AddEx(P.FeaturePropertyConstants.igRegularHole,
                "ISO Metric", M, "M6", M, 0.004917,
                0.0, 0.0, 0.0, 0.0, 0.0,
                P.FeaturePropertyConstants.igNone, M, M, 0.004917, M, M, P.FeaturePropertyConstants.igVBottomDimToFlat, M,
                M, M, M, M, M, true,
                M, M, M, M, M, M, M, M, M, M, M, M);
            try { data.ThreadExternalDiameter = 0.006; data.ThreadDescription = "M6"; } catch { }
            try { data.ThreadDepthMethod = P.FeaturePropertyConstants.igFinite; } catch (Exception e) { Say("   预设 TDM 异常 " + e.Message); }
            try { data.ThreadDepth = 0.014; } catch (Exception e) { Say("   预设 TD 异常 " + e.Message); }
            var h = Through(p, NewProfile(p, TopPlane(p), new List<double[]>{ new[]{0.05,0.05} }), data);
            if (h == null) { Say("RESULT T4 建前改属性：建不出来"); return; }
            DumpThread("RESULT T4 建前改属性(igFinite 14)", (P.HoleData)h.HoleData);
            var hd = (P.HoleData)h.HoleData;
            try { hd.ThreadDepth = 0.018; Say("   建后改 TD 未抛异常"); } catch (Exception e) { Say("   建后改 TD 异常 " + e.Message); }
            try { p.Model.Recompute(); } catch { }
            DumpThread("RESULT T4 建后改属性(18)", (P.HoleData)h.HoleData);
        } catch (Exception e) { Say("RESULT T4 PostSet 异常 0x" + e.HResult.ToString("X8") + " " + e.Message); }
        finally { if (p != null) Close(p, "T4-postset.par"); }
    }

    // ---------------- T5 ----------------
    static void T5(){
        var p = NewPlate(0.1, 0.1, 0.02);
        var plane = TopPlane(p);
        var prof = NewProfile(p, plane, new List<double[]>{ new[]{0.02,0.02}, new[]{0.08,0.02} });
        var h = Through(p, prof, Regular(p.Part, 0.006));
        if (h == null) { Say("RESULT T5 建孔失败"); Close(p, null); return; }
        Say("   改前顶面圆：" + string.Join(" ", HolesOnTop(p).ToArray()));
        P.Profile fp = null;
        try { fp = (P.Profile)h.Profile; } catch (Exception e) { Say("   h.Profile 异常 " + e.Message); }
        if (fp == null) {
            try { int n = 0; Array a = new object[0]; h.GetProfiles(out n, ref a); if (n > 0) fp = (P.Profile)a.GetValue(0); } catch (Exception e) { Say("   GetProfiles 异常 " + e.Message); }
        }
        if (fp == null) { Say("RESULT T5 取不到特征轮廓"); Close(p, null); return; }
        Say("   特征轮廓 Holes2d.Count=" + fp.Holes2d.Count);
        // 第一个孔挪 +10mm X（用 SetCenterPoint，坐标走轮廓 2D）
        try {
            var h2 = fp.Holes2d.Item(1);
            double cx, cy; h2.GetCenterPoint(out cx, out cy);
            double tx, ty; fp.Convert3DCoordinate(0.03, 0.03, p.Top, out tx, out ty);
            h2.SetCenterPoint(tx, ty);
            Say("   SetCenterPoint (" + Mm(cx) + "," + Mm(cy) + ") -> (" + Mm(tx) + "," + Mm(ty) + ") 未抛异常");
        } catch (Exception e) { Say("   SetCenterPoint 异常 0x" + e.HResult.ToString("X8") + " " + e.Message); }
        try { int e2 = fp.End(P.ProfileValidationType.igProfileClosed); Say("   再次 End=" + e2); } catch (Exception e) { Say("   再次 End 异常 " + e.Message); }
        try { p.Model.Recompute(); Say("   Recompute 未抛异常"); } catch (Exception e) { Say("   Recompute 异常 " + e.Message); }
        object desc; Say("   状态 " + h.GetStatusEx(out desc));
        Say("RESULT T5 改后顶面圆：" + string.Join(" ", HolesOnTop(p).ToArray()) + "（期望 (30,30) 和 (80,20)）");
        Close(p, "T5-move.par");
    }

    // ---------------- T6 ----------------
    static void T6(){
        var p = NewPlate(0.1, 0.1, 0.02);
        var h = Through(p, NewProfile(p, TopPlane(p), new List<double[]>{ new[]{0.05,0.05} }), Regular(p.Part, 0.006));
        if (h == null) { Say("RESULT T6 建孔失败"); Close(p, null); return; }
        string payload = "v1|ref=PlateA.par:1|c=0.05,0.05,0|a=0,0,1|d=6.6";
        try {
            var sets = (F.AttributeSets)h.AttributeSets;
            var set = sets.Add("TGAutoHole");
            var at = set.Add("Link", F.AttributeTypeConstants.seStringUnicode);
            at.Value = payload;
            Say("   写入属性集 OK");
        } catch (Exception e) { Say("   写属性集异常 0x" + e.HResult.ToString("X8") + " " + e.Message); }
        string path = Path.Combine(Out, "T6-attr.par");
        Close(p, "T6-attr.par");
        try {
            var re = (P.PartDocument)app.Documents.Open(path);
            var hh = ((P.Model)re.Models.Item(1)).Holes.Item(1);
            bool present = hh.get_IsAttributeSetPresent("TGAutoHole");
            string got = "";
            if (present) got = (string)((F.AttributeSet)((F.AttributeSets)hh.AttributeSets).Item("TGAutoHole")).Item("Link").Value;
            Say("RESULT T6 重开后 present=" + present + " 值" + (got == payload ? "一致" : ("不一致「" + got + "」")));
            // 零件文档上也试一下（存整个零件的链接表时用）
            try {
                var ds = (F.AttributeSets)((dynamic)re).AttributeSets;
                var s2 = ds.Add("TGAutoHoleDoc");
                s2.Add("Links", F.AttributeTypeConstants.seStringUnicode).Value = "doc-level";
                Say("   零件文档级属性集写入 OK");
            } catch (Exception e) { Say("   零件文档级属性集异常 " + e.Message); }
            re.Close(false);
        } catch (Exception e) { Say("RESULT T6 重开异常 " + e.Message); }
    }

    // ---------------- T7 ----------------
    // 装配：A 板（带一个 Φ10 孔）在下，B 板（实心）在上 z+30。B 上建轮廓，
    // IncludeEdge(A 的孔口圆边) —— 看能不能建立跨零件关联、以及挪 A 后 B 的孔跟不跟。
    static void T7(){
        var a = NewPlate(0.1, 0.1, 0.01);
        var ah = Through(a, NewProfile(a, TopPlane(a), new List<double[]>{ new[]{0.03,0.04} }), Regular(a.Part, 0.010));
        Say("   A 板孔 " + (ah == null ? "失败" : "OK"));
        string pa = Path.Combine(Out, "T7-A.par"), pb = Path.Combine(Out, "T7-B.par"), pasm = Path.Combine(Out, "T7.asm");
        Close(a, "T7-A.par");
        var b = NewPlate(0.1, 0.1, 0.01);
        Close(b, "T7-B.par");
        var asm = (A.AssemblyDocument)app.Documents.Add("SolidEdge.AssemblyDocument", Tpl + "iso metric assembly.asm");
        var occA = asm.Occurrences.AddByFilename(pa);
        var occB = asm.Occurrences.AddByFilename(pb);
        Array mtx = new double[]{ 1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0.03,1 };
        try { occB.PutMatrix(ref mtx, true); } catch (Exception e) { Say("   PutMatrix 异常 " + e.Message); }
        try { asm.SaveAs(pasm); } catch (Exception e) { Say("   装配保存异常 " + e.Message); }
        var partA = (P.PartDocument)occA.OccurrenceDocument;
        var partB = (P.PartDocument)occB.OccurrenceDocument;
        // A 的孔口圆边（A 顶面上的圆）
        G.Edge circle = null;
        foreach (G.Edge e in (G.Edges)((G.Body)((P.Model)partA.Models.Item(1)).Body).get_Edges(G.FeatureTopologyQueryTypeConstants.igQueryAll)) {
            var c = e.Geometry as G.Circle; if (c == null) continue;
            Array cc = new double[3], ax = new double[3]; double r; c.GetCircleData(ref cc, ref ax, out r);
            if (D(cc,2) > 0.005) { circle = e; break; }
        }
        Say("   A 孔口圆边 " + (circle == null ? "没找到" : "找到"));
        object refA = asm.CreateReference(occA, circle);
        // B 的底面（z=0 局部）
        var bModel = (P.Model)partB.Models.Item(1);
        G.Face bottom = null;
        foreach (G.Face f in (G.Faces)((G.Body)bModel.Body).get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll)) {
            var pl = f.Geometry as G.Plane; if (pl == null) continue;
            Array p0 = new double[3], n0 = new double[3]; pl.GetPlaneData(ref p0, ref n0);
            if (Math.Abs(Math.Abs(D(n0,2)) - 1) < 1e-6 && Math.Abs(D(p0,2)) < 1e-9) { bottom = f; break; }
        }
        P.RefPlane bp = null;
        try { bp = partB.RefPlanes.AddParallelByDistance(bottom, 0.0, P.ReferenceElementConstants.igNormalSide, M, M, M, M); } catch (Exception e) { Say("   B 基准面异常 " + e.Message); }
        if (bp == null) { Say("RESULT T7 B 基准面失败"); return; }
        var prof = partB.ProfileSets.Add().Profiles.Add(bp);
        foreach (var what in new object[]{ refA, circle }) {
            string label = what == refA ? "装配引用" : "零件边对象";
            try {
                object inc = null;
                prof.IncludeEdge(what, out inc);
                Say("RESULT T7 IncludeEdge(" + label + ") 成功，返回 " + (inc == null ? "null" : Marshal.IsComObject(inc) ? "COM 对象" : inc.GetType().Name)
                    + "  Circles2d=" + prof.Circles2d.Count + " Arcs2d=" + prof.Arcs2d.Count);
                break;
            } catch (Exception e) { Say("RESULT T7 IncludeEdge(" + label + ") 异常 0x" + e.HResult.ToString("X8") + " " + e.Message); }
        }
        try {
            object pe = prof.ProjectEdge(refA);
            Say("RESULT T7 ProjectEdge(装配引用) 成功 " + (pe == null ? "null" : "对象"));
        } catch (Exception e) { Say("RESULT T7 ProjectEdge(装配引用) 异常 0x" + e.HResult.ToString("X8") + " " + e.Message); }
        try { bool has = false; partB.HasInterpartLinks(ref has); Say("   B.HasInterpartLinks=" + has); } catch (Exception e) { Say("   HasInterpartLinks 异常 " + e.Message); }
        try { asm.Save(); } catch { }
        try { asm.Close(false); } catch { }
    }
}
