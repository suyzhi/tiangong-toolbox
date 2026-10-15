// tools/hole-feature-probe/HoleMultiProbe.cs —— 「一个孔特征放多个孔」第二轮 + 现场编组失败的几种猜测（2026-10-14 写，待真机跑）。
//
// 第一轮（HoleFeatureProbe T1/T2）的结论是"做不到"：一个轮廓放 N 个 Holes2d，End 之后只剩 1 个；
// 每个孔各建一个草图集再 Hole.SetProfiles(3, …) → igFeatureFailed。下面这些路当时**没试过**：
//   M1  N 个轮廓放在**同一个草图集**（ProfileSets.Add() 只调一次）里，再 SetProfiles ——
//       Solid Edge 的多轮廓特征要求轮廓同属一个 ProfileSet，第一轮每个轮廓各建了一个草图集，可能就是失败原因；
//   M1b 同一个草图集里放 N 个轮廓，只拿第一个去 AddThroughAll，看 CAD 会不会把同集的轮廓一起用上；
//   M2  Holes.AddSync(N, 轮廓数组) —— 接口本身就收多个轮廓（同步建模孔；研究报告说和顺序建模混用有风险，这里量一下）；
//   M3  种子孔 + UserDefinedPatterns.AddByProfiles（用户定义阵列：一个孔特征 + 一个阵列特征，改种子孔的孔型，阵列跟着变）；
//   M4  种子孔 + Patterns.AddByRectangular（1×N 矩形阵列：批量排孔的"沿线等距"正好是这个）；
//   E1  已建好的孔改孔型：hole.HoleData = 新 HoleData（通孔 → 圆柱沉孔），Recompute 后看切除体积 ——
//       行得通的话，"打错孔了直接改一组孔的孔型"就不用删了重打；
//   E2  阵列的种子孔改孔型，阵列出来的孔跟不跟。
// 现场编组失败（AddFeatureGroupsBySet：10-13 E_INVALIDARG、10-14「系统无法找到指定的资源」）的猜测：
//   G1  孔中间夹着一个没用掉的轮廓（建孔失败留下的）→ BySet 报什么；挪到一起（Reorder）后能不能编；ByRange 行不行；
//   G2  孔中间夹着一个基准面（整轮重试重建的）→ 同上；
//   G3  同一批里两种规格交错（A B A B）→ 分规格编组报什么，Reorder 后能不能编。
//
// 每一项都在新建的文档上做，结果写进输出目录；收尾关掉自己建的文档。
// 运行：tools\run-hole-feature-probe.ps1 -Exe build\HoleMultiProbe.exe -OutDir artifacts\hole-multi-probe
//      （本机开着 CAD 就拒绝运行；跑完只结束自己起的那个 CAD）
// 编译：见同目录 README.md（只依赖接口库）。
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using F=SolidEdgeFramework;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;
using S=SolidEdgeFrameworkSupport;

class HoleMultiProbe {
    static object M = Type.Missing;
    static string Out = ".";
    static F.Application app;
    const string Tpl = @"C:\Program Files\NDS\TianGong 2025\Template\ISO Metric\";
    static readonly List<double[]> Pts = new List<double[]>{ new[]{0.02,0.03}, new[]{0.05,0.03}, new[]{0.08,0.03} };

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
        if (args.Length > 1 && args[1] == "2") {
            // 第二轮（同日）：第一轮 M3 建出了用户定义阵列但后面 E_FAIL、M4 枚举参数没传对、Reorder 后孔状态是"警告/失败"
            // P1（用户定义阵列）不再跑：AddByProfiles 直接把 CAD 搞崩（RPC_E_SERVERFAULT → RPC 服务器不可用），2026-10-14 实测
            Run("P2 矩形阵列（枚举按类型传）", P2);
            Run("P3 交错的孔 Reorder 后 Recompute", P3);
            Say("PROBE DONE");
            return 0;
        }
        Run("M1 同一草图集多轮廓 + SetProfiles", M1);
        Run("M1b 同一草图集多轮廓，只给第一个", M1b);
        Run("M2 Holes.AddSync 多轮廓", M2);
        Run("M3 种子孔 + 用户定义阵列", M3);
        Run("M4 种子孔 + 矩形阵列 1×3", M4);
        Run("E1 已建孔改孔型（通孔 → 沉孔）", E1);
        Run("G1 孔之间夹着空轮廓", G1);
        Run("G2 孔之间夹着基准面", G2);
        Run("G3 两种规格交错", G3);
        Say("PROBE DONE");
        return 0;
    }
    static void Run(string name, Action a){
        Say(""); Say("==== " + name);
        try { a(); } catch (Exception e) { Say("FATAL " + name + " : " + e.GetType().Name + " " + Hr(e)); }
    }

    // ---------------- 夹具 ----------------
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
        double zmin = double.MaxValue, zmax = double.MinValue;
        foreach (G.Face f in Faces(pl)) {
            var g = f.Geometry as G.Plane; if (g == null) continue;
            Array a = new double[3], n = new double[3]; g.GetPlaneData(ref a, ref n);
            if (Math.Abs(Math.Abs(D(n,2)) - 1) > 1e-6) continue;
            zmin = Math.Min(zmin, D(a,2)); zmax = Math.Max(zmax, D(a,2));
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
    static P.RefPlane TopPlane(Plate p){ return p.Part.RefPlanes.AddParallelByDistance(TopFace(p), 0.0, P.ReferenceElementConstants.igNormalSide, M, M, M, M); }
    // 在给定草图集里加一个轮廓，放一个 Holes2d
    static P.Profile ProfileIn(P.ProfileSet set, Plate p, P.RefPlane plane, double[] c){
        var prof = set.Profiles.Add(plane);
        double x2, y2; prof.Convert3DCoordinate(c[0], c[1], p.Top, out x2, out y2);
        prof.Holes2d.Add(x2, y2);
        int end = prof.End(P.ProfileValidationType.igProfileClosed);
        if (end != 0) Say("   注意：轮廓 End 返回 " + end);
        return prof;
    }
    static P.Profile OwnProfile(Plate p, P.RefPlane plane, double[] c){ return ProfileIn(p.Part.ProfileSets.Add(), p, plane, c); }
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
        int holes = -1, pats = -1, udp = -1;
        try { holes = p.Model.Holes.Count; } catch { }
        try { pats = p.Model.Patterns.Count; } catch { }
        try { udp = p.Model.UserDefinedPatterns.Count; } catch { }
        Say("RESULT " + tag + "：切除比 " + (want > 0 ? (cut / want).ToString("0.000") : "-") + "（切除 " + (cut * 1e9).ToString("0.#") + " / 理论 " + (want * 1e9).ToString("0.#") + " mm³）"
            + " 孔特征 " + holes + " 阵列 " + pats + " 用户阵列 " + udp + " 顶面圆：" + string.Join(" ", HolesOnTop(p).ToArray()));
    }
    static void Status(string tag, object feature){
        try { object d; var st = ((dynamic)feature).GetStatusEx(out d); Say("   " + tag + " 状态 " + st + (d == null ? "" : "（" + d + "）")); }
        catch (Exception e) { Say("   " + tag + " 读状态异常 " + Hr(e)); }
    }
    static void Close(Plate p, string save){
        try { if (save != null) p.Part.SaveAs(Path.Combine(Out, save)); } catch (Exception e) { Say("   保存失败 " + e.Message); }
        try { p.Part.Close(false); } catch { }
    }
    static void Order(string tag, Plate p){
        try {
            dynamic feats = p.Model.Features; int n = feats.Count; var names = new List<string>();
            for (int i = 1; i <= n; i++) { try { dynamic f = feats.Item(i); names.Add(i + ":" + (string)f.Name); } catch { names.Add(i + ":?"); } }
            Say("   " + tag + " 特征表：" + string.Join(" | ", names.ToArray()));
        } catch (Exception e) { Say("   " + tag + " 读特征表异常 " + Hr(e)); }
    }
    static P.FeatureGroup BySet(P.Model m, IList<P.Hole> holes){
        var items = new object[holes.Count]; for (int i = 0; i < holes.Count; i++) items[i] = holes[i];
        Array arr = items;
        return m.FeatureGroups.AddFeatureGroupsBySet(items.Length, ref arr);
    }
    static void TryGroup(string tag, Plate p, IList<P.Hole> holes){
        try { var g = BySet(p.Model, holes); Say("RESULT " + tag + " BySet：" + (g == null ? "null" : "成功 成员 " + g.Count)); if (g != null) { try { g.Ungroup(); } catch { } } }
        catch (Exception e) { Say("RESULT " + tag + " BySet：失败 " + Hr(e)); }
        try { var g = p.Model.FeatureGroups.AddFeatureGroupsByRange(holes[0], holes[holes.Count - 1]); Say("RESULT " + tag + " ByRange：" + (g == null ? "null" : "成功 成员 " + g.Count)); if (g != null) { try { g.Ungroup(); } catch { } } }
        catch (Exception e) { Say("RESULT " + tag + " ByRange：失败 " + Hr(e)); }
    }
    static void Gather(string tag, Plate p, IList<P.Hole> holes){
        try { var last = holes[holes.Count - 1]; for (int i = 0; i < holes.Count - 1; i++) holes[i].Reorder(last, true); Say("   " + tag + " Reorder 完成"); }
        catch (Exception e) { Say("   " + tag + " Reorder 异常 " + Hr(e)); }
        Order(tag + "（挪后）", p);
        foreach (var h in holes) Status(tag + " 挪后孔", h);
    }

    // ---------------- M ----------------
    static void M1(){
        var p = NewPlate(); double v0 = Vol(p); var plane = TopPlane(p);
        var set = p.Part.ProfileSets.Add();
        var profs = new List<P.Profile>(); foreach (var c in Pts) profs.Add(ProfileIn(set, p, plane, c));
        Say("   草图集里轮廓数 " + set.Profiles.Count);
        var h = Through(p, profs[0], Regular(p, 0.006));
        if (h == null) { Say("RESULT M1 第一个孔就失败"); Close(p, null); return; }
        try {
            Array arr = new object[]{ profs[0], profs[1], profs[2] };
            h.SetProfiles(3, ref arr);
            try { p.Model.Recompute(); } catch (Exception e) { Say("   Recompute 异常 " + Hr(e)); }
            Status("M1 孔", h);
            int n = 0; Array got = new object[0]; try { h.GetProfiles(out n, ref got); } catch (Exception e) { Say("   GetProfiles 异常 " + Hr(e)); }
            Say("   GetProfiles 数 " + n);
        } catch (Exception e) { Say("   SetProfiles 异常 " + Hr(e)); }
        Report("M1", p, v0, 3 * Cyl(0.006, p.T));
        Close(p, "M1-set-profiles.par");
    }
    static void M1b(){
        var p = NewPlate(); double v0 = Vol(p); var plane = TopPlane(p);
        var set = p.Part.ProfileSets.Add();
        var profs = new List<P.Profile>(); foreach (var c in Pts) profs.Add(ProfileIn(set, p, plane, c));
        var h = Through(p, profs[0], Regular(p, 0.006));
        if (h != null) Status("M1b 孔", h);
        Report("M1b", p, v0, 3 * Cyl(0.006, p.T));
        Close(p, "M1b-same-set.par");
    }
    static void M2(){
        foreach (bool sameSet in new[]{ true, false }) {
            var p = NewPlate(); double v0 = Vol(p); var plane = TopPlane(p);
            var set = p.Part.ProfileSets.Add();
            var profs = new List<object>(); foreach (var c in Pts) profs.Add(sameSet ? ProfileIn(set, p, plane, c) : OwnProfile(p, plane, c));
            object hole = null;
            foreach (var side in new[]{ P.FeaturePropertyConstants.igLeft, P.FeaturePropertyConstants.igRight }) {
                try {
                    hole = p.Model.Holes.AddSync(profs.Count, profs.ToArray(), side, P.FeaturePropertyConstants.igThroughAll, M, Regular(p, 0.006));
                    Say("   AddSync(" + side + ") 返回 " + (hole == null ? "null" : "孔特征"));
                    if (hole != null) { Status("M2 孔", hole); try { Say("   建模方式 " + ((P.Hole)hole).ModelingModeType); } catch { } break; }
                } catch (Exception e) { Say("   AddSync(" + side + ") 异常 " + Hr(e)); }
            }
            Report("M2 " + (sameSet ? "同一草图集" : "各自草图集"), p, v0, 3 * Cyl(0.006, p.T));
            Close(p, sameSet ? "M2-sync-sameset.par" : "M2-sync-ownsets.par");
        }
    }
    static void M3(){
        foreach (string mark in new[]{ "Points2d", "Holes2d" }) {
            var p = NewPlate(); double v0 = Vol(p); var plane = TopPlane(p);
            var seed = Through(p, OwnProfile(p, plane, Pts[0]), Regular(p, 0.006));
            if (seed == null) { Say("RESULT M3 种子孔失败"); Close(p, null); continue; }
            var prof = p.Part.ProfileSets.Add().Profiles.Add(plane);
            for (int i = 1; i < Pts.Count; i++) {
                double x2, y2; prof.Convert3DCoordinate(Pts[i][0], Pts[i][1], p.Top, out x2, out y2);
                try { if (mark == "Points2d") ((dynamic)prof).Points2d.Add(x2, y2); else prof.Holes2d.Add(x2, y2); }
                catch (Exception e) { Say("   " + mark + ".Add 异常 " + Hr(e)); }
            }
            try { Say("   轮廓 End(0)=" + prof.End((P.ProfileValidationType)0)); } catch (Exception e) { Say("   End 异常 " + Hr(e)); }
            object pat = null;
            try {
                pat = ((dynamic)p.Model.UserDefinedPatterns).AddByProfiles(1, new object[]{ prof }, seed);
                Say("   AddByProfiles 返回 " + (pat == null ? "null" : "阵列特征"));
                if (pat != null) Status("M3 阵列", pat);
            } catch (Exception e) { Say("   AddByProfiles 异常 " + Hr(e)); }
            Report("M3 " + mark, p, v0, 3 * Cyl(0.006, p.T));
            if (pat != null) E2("M3 " + mark, p, v0, seed);
            Close(p, "M3-udp-" + mark + ".par");
        }
    }
    static void M4(){
        var p = NewPlate(); double v0 = Vol(p); var plane = TopPlane(p);
        var seed = Through(p, OwnProfile(p, plane, Pts[0]), Regular(p, 0.006));
        if (seed == null) { Say("RESULT M4 种子孔失败"); Close(p, null); return; }
        object pat = null;
        // AddByRectangular(NumberOfFeatures, FeatureArray, ReferencePlane, XCount, YCount, XSpacing, YSpacing, Angle, PatternMethod, ReferenceIndex)
        // PatternMethod 用 sePatternFixedOffset（固定间距）的数值：先按名字取，取不到就试 0..3
        foreach (int method in new[]{ 2, 0, 1, 3 }) {
            try {
                pat = ((dynamic)p.Model.Patterns).AddByRectangular(1, new object[]{ seed }, plane, 3, 1, 0.03, 0.03, 0.0, method, 1);
                Say("   AddByRectangular(方法 " + method + ") 返回 " + (pat == null ? "null" : "阵列特征"));
                if (pat != null) { Status("M4 阵列", pat); break; }
            } catch (Exception e) { Say("   AddByRectangular(方法 " + method + ") 异常 " + Hr(e)); }
        }
        Report("M4", p, v0, 3 * Cyl(0.006, p.T));
        if (pat != null) E2("M4", p, v0, seed);
        Close(p, "M4-rect.par");
    }
    // 阵列的种子孔改成沉孔，看阵列跟不跟
    static void E2(string tag, Plate p, double v0, P.Hole seed){
        try {
            seed.HoleData = Counterbore(p, 0.0066, 0.011, 0.0068);
            try { p.Model.Recompute(); } catch (Exception e) { Say("   Recompute 异常 " + Hr(e)); }
            Status(tag + " 改后种子孔", seed);
            double one = Cyl(0.0066, p.T) + Cyl(0.011, 0.0068) - Cyl(0.0066, 0.0068);
            Report(tag + " 种子孔改沉孔后（理论按 3 个沉孔）", p, v0, 3 * one);
        } catch (Exception e) { Say("   " + tag + " 改种子孔异常 " + Hr(e)); }
    }

    // ---------------- 第二轮 ----------------
    static void Step(string what, Action a){ try { a(); Say("   " + what + "：成功"); } catch (Exception e) { Say("   " + what + "：异常 " + Hr(e)); } }
    static void SafeReport(string tag, Plate p, double v0, double want){
        try { Report(tag, p, v0, want); } catch (Exception e) { Say("RESULT " + tag + "：读结果异常 " + Hr(e)); }
    }
    static void P1(){
        foreach (string mark in new[]{ "Points2d", "Holes2d" }) {
            var p = NewPlate(); double v0 = Vol(p); var plane = TopPlane(p);
            var seed = Through(p, OwnProfile(p, plane, Pts[0]), Regular(p, 0.006));
            if (seed == null) { Say("RESULT P1 种子孔失败"); Close(p, null); continue; }
            var prof = p.Part.ProfileSets.Add().Profiles.Add(plane);
            for (int i = 1; i < Pts.Count; i++) {
                double x2, y2; prof.Convert3DCoordinate(Pts[i][0], Pts[i][1], p.Top, out x2, out y2);
                if (mark == "Points2d") ((dynamic)prof).Points2d.Add(x2, y2); else prof.Holes2d.Add(x2, y2);
            }
            Step("轮廓 End(0)=" + prof.End((P.ProfileValidationType)0), delegate { });
            object pat = null;
            Step("AddByProfiles", delegate { pat = ((dynamic)p.Model.UserDefinedPatterns).AddByProfiles(1, new object[]{ prof }, seed); });
            if (pat != null) {
                Status("P1 阵列", pat);
                Step("阵列实例数 " + SafeInt(delegate { return (int)((dynamic)pat).GetNumberOfOccurrences(); }), delegate { });
            }
            Step("Recompute", delegate { p.Model.Recompute(); });
            Step("体积 " + SafeD(delegate { return Vol(p) * 1e9; }), delegate { });
            SafeReport("P1 " + mark, p, v0, 3 * Cyl(0.006, p.T));
            if (pat != null) {
                Step("种子孔改沉孔", delegate { seed.HoleData = Counterbore(p, 0.0066, 0.011, 0.0068); });
                Step("Recompute", delegate { p.Model.Recompute(); });
                Status("P1 改后阵列", pat); Status("P1 改后种子孔", seed);
                double one = Cyl(0.0066, p.T) + Cyl(0.011, 0.0068) - Cyl(0.0066, 0.0068);
                SafeReport("P1 " + mark + " 种子改沉孔后（理论 3 个沉孔）", p, v0, 3 * one);
            }
            Close(p, "P1-udp-" + mark + ".par");
        }
    }
    static string SafeInt(Func<int> f){ try { return f().ToString(); } catch (Exception e) { return "异常 " + Hr(e); } }
    static string SafeD(Func<double> f){ try { return f().ToString("0.#"); } catch (Exception e) { return "异常 " + Hr(e); } }
    static void P2(){
        var p = NewPlate(); double v0 = Vol(p); var plane = TopPlane(p);
        var seed = Through(p, OwnProfile(p, plane, Pts[0]), Regular(p, 0.006));
        if (seed == null) { Say("RESULT P2 种子孔失败"); Close(p, null); return; }
        P.Pattern pat = null;
        foreach (SolidEdgeFrameworkSupport.PatternOffsetTypeConstants method in Enum.GetValues(typeof(SolidEdgeFrameworkSupport.PatternOffsetTypeConstants))) {
            try {
                Array feats = new object[]{ seed };
                pat = p.Model.Patterns.AddByRectangular(1, feats, plane, 3, 1, 0.03, 0.03, 0.0, method, 1);
                Say("   AddByRectangular(" + method + ") 返回 " + (pat == null ? "null" : "阵列特征"));
                if (pat != null) { Status("P2 阵列", pat); break; }
            } catch (Exception e) { Say("   AddByRectangular(" + method + ") 异常 " + Hr(e)); }
        }
        SafeReport("P2", p, v0, 3 * Cyl(0.006, p.T));
        if (pat != null) {
            Step("种子孔改沉孔", delegate { seed.HoleData = Counterbore(p, 0.0066, 0.011, 0.0068); });
            Step("Recompute", delegate { p.Model.Recompute(); });
            Status("P2 改后阵列", pat);
            double one = Cyl(0.0066, p.T) + Cyl(0.011, 0.0068) - Cyl(0.0066, 0.0068);
            SafeReport("P2 种子改沉孔后（理论 3 个沉孔）", p, v0, 3 * one);
        }
        Close(p, "P2-rect.par");
    }
    static void P3(){
        var p = NewPlate(); double v0 = Vol(p); var plane = TopPlane(p);
        var a1 = Through(p, OwnProfile(p, plane, Pts[0]), Regular(p, 0.006));
        var b1 = Through(p, OwnProfile(p, plane, Pts[1]), Regular(p, 0.009));
        var a2 = Through(p, OwnProfile(p, plane, Pts[2]), Regular(p, 0.006));
        var A = new List<P.Hole>{ a1, a2 };
        Gather("P3", p, A);
        Step("Recompute", delegate { p.Model.Recompute(); });
        foreach (var h in new[]{ a1, b1, a2 }) Status("P3 Recompute 后", h);
        SafeReport("P3（理论 2×Φ6 + Φ9）", p, v0, 2 * Cyl(0.006, p.T) + Cyl(0.009, p.T));
        TryGroup("P3 挪到一起 + Recompute 后", p, A);
        Close(p, "P3.par");
    }

    // ---------------- E ----------------
    static void E1(){
        var p = NewPlate(); double v0 = Vol(p); var plane = TopPlane(p);
        var h = Through(p, OwnProfile(p, plane, Pts[1]), Regular(p, 0.0066));
        if (h == null) { Say("RESULT E1 建孔失败"); Close(p, null); return; }
        Report("E1 改前（通孔 Φ6.6）", p, v0, Cyl(0.0066, p.T));
        try {
            h.HoleData = Counterbore(p, 0.0066, 0.011, 0.0068);
            try { p.Model.Recompute(); } catch (Exception e) { Say("   Recompute 异常 " + Hr(e)); }
            Status("E1 改后孔", h);
            try { var d = (P.HoleData)h.HoleData; Say("   回读孔型 " + d.HoleType + " 沉孔Φ" + Mm(d.CounterboreDiameter) + " 深" + Mm(d.CounterboreDepth)); } catch (Exception e) { Say("   回读异常 " + Hr(e)); }
            Report("E1 改后（沉孔 Φ6.6/Φ11 深6.8）", p, v0, Cyl(0.0066, p.T) + Cyl(0.011, 0.0068) - Cyl(0.0066, 0.0068));
        } catch (Exception e) { Say("RESULT E1 改孔型异常 " + Hr(e)); }
        Close(p, "E1-edit-type.par");
    }

    // ---------------- G ----------------
    static void G1(){
        var p = NewPlate(); var plane = TopPlane(p);
        var h1 = Through(p, OwnProfile(p, plane, Pts[0]), Regular(p, 0.006));
        var h2 = Through(p, OwnProfile(p, plane, Pts[1]), Regular(p, 0.006));
        var orphan = OwnProfile(p, plane, new[]{ 0.05, 0.05 });   // 没用掉的轮廓（建孔失败时留下的那种）
        var h3 = Through(p, OwnProfile(p, plane, Pts[2]), Regular(p, 0.006));
        Order("G1", p);
        var holes = new List<P.Hole>{ h1, h2, h3 };
        TryGroup("G1 夹着空轮廓", p, holes);
        Gather("G1", p, holes);
        TryGroup("G1 挪到一起后", p, holes);
        try { orphan.Delete(); Say("   删掉空轮廓"); } catch (Exception e) { Say("   删空轮廓异常 " + Hr(e)); }
        TryGroup("G1 删掉空轮廓后", p, holes);
        Close(p, "G1.par");
    }
    static void G2(){
        var p = NewPlate(); var plane = TopPlane(p);
        var h1 = Through(p, OwnProfile(p, plane, Pts[0]), Regular(p, 0.006));
        P.RefPlane extra = null;
        try { extra = p.Part.RefPlanes.AddParallelByDistance(TopFace(p), 0.005, P.ReferenceElementConstants.igNormalSide, M, M, M, M); } catch (Exception e) { Say("   建夹在中间的基准面异常 " + Hr(e)); }
        var h2 = Through(p, OwnProfile(p, plane, Pts[1]), Regular(p, 0.006));
        Order("G2", p);
        var holes = new List<P.Hole>{ h1, h2 };
        TryGroup("G2 夹着基准面", p, holes);
        Gather("G2", p, holes);
        TryGroup("G2 挪到一起后", p, holes);
        Close(p, "G2.par");
    }
    static void G3(){
        var p = NewPlate(); var plane = TopPlane(p);
        var a1 = Through(p, OwnProfile(p, plane, Pts[0]), Regular(p, 0.006));
        var b1 = Through(p, OwnProfile(p, plane, Pts[1]), Regular(p, 0.009));
        var a2 = Through(p, OwnProfile(p, plane, Pts[2]), Regular(p, 0.006));
        var b2 = Through(p, OwnProfile(p, plane, new[]{ 0.05, 0.045 }), Regular(p, 0.009));
        Order("G3", p);
        var A = new List<P.Hole>{ a1, a2 }; var B = new List<P.Hole>{ b1, b2 };
        TryGroup("G3 交错的 A", p, A);
        Gather("G3 A", p, A); Gather("G3 B", p, B);
        TryGroup("G3 挪到一起后 A", p, A);
        TryGroup("G3 挪到一起后 B", p, B);
        Close(p, "G3.par");
    }
}
