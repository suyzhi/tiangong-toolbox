using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using F=SolidEdgeFramework;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;
using S=SolidEdgeFrameworkSupport;

// 探针：给「全参数显式」的建孔配方逐项定值（二分法）。
//
// 背景：HoleDataCollection 的可选参数留 Missing 时，CAD 会灌入"上次用过的孔参数"
// （实测灌进来 沉头 Φ13.71×0.8 / 90° / 深 50.8mm），所以沉孔、锥形沉孔、通孔也要走
// 「几何相关参数一个都不留」的配方。但"全显式"不是随便给——给错值 AddEx 直接 E_INVALIDARG。
//
// 用法：HoleDefaultProbe.exe <输出目录> [matrix|full]
//   matrix（默认）：以已知可用的最小配方为基线，逐项把某个参数改成显式值，看哪一项会炸。
//   full：用 full 模式下确认可用的配方，把四种孔型 × 上/下表面都打一遍。
class HoleDefaultProbe {
    static object M = Type.Missing;
    const string CadHome = @"C:\Program Files\NDS\TianGong 2025";
    static string Template { get { return Path.Combine(CadHome, "Template", "ISO Metric", "iso metric part.par"); } }
    static string OutDir;
    const double TM = 0.010;
    const double HALF = TM / 2.0;

    [STAThread] static int Main(string[] args){
        OutDir = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(Environment.CurrentDirectory, "artifacts", "hole-default-probe-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        string mode = args.Length > 1 ? args[1] : "matrix";
        Directory.CreateDirectory(OutDir);
        F.Application app = null; bool own = false;
        try {
            try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); Console.WriteLine("INFO attached"); }
            catch { app = (F.Application)Activator.CreateInstance(Type.GetTypeFromProgID("SolidEdge.Application")); own = true; Console.WriteLine("INFO new instance"); }
            Console.WriteLine("INFO version " + app.Version + " mode=" + mode);
            app.Visible = true; app.ScreenUpdating = true;
            if (mode == "full") Full(app);
            else if (mode == "bottom") Bottom(app);
            else Matrix(app);
        } catch (Exception e) { Console.WriteLine("FATAL " + e.GetType().Name + ": " + e.Message); Console.WriteLine(e.StackTrace); }
        finally { try { if (app != null && own) app.Quit(); } catch { } }
        Console.WriteLine("HOLE-DEFAULT-PROBE DONE");
        return 0;
    }

    enum Kind { Through, Tapped, Counterbore, Countersink }

    // ---------- 逐项二分 ----------
    static void Matrix(F.Application app){
        // 基线 = 之前实测可用的最小配方（几何参数显式，其它全 Missing）
        Console.WriteLine("--- 基线：最小配方（已知可用） ---");
        Run(app, "base-through", Kind.Through, true, r => { });

        Console.WriteLine("--- non-tapped 专用参数逐项显式化 ---");
        Run(app, "std-iso",        Kind.Through, true, r => r.Standard = "ISO Metric");
        Run(app, "std-empty",      Kind.Through, true, r => r.Standard = "");
        // taper-0 / tDim-0 / vB-0 三项实测必炸（E_INVALIDARG），见 AUTO-HOLE.md
        Run(app, "tapermethod-45", Kind.Through, true, r => { r.TaperMethod = P.FeaturePropertyConstants.igTaperByAngle; r.Taper = M; r.TaperDimType = M; });
        Run(app, "threaddepthmethod-0", Kind.Through, true, r => { r.ThreadDepthMethod = 0.0; r.ThreadDepth = 0.0; });
        Run(app, "threaddiaopt-0", Kind.Through, true, r => r.ThreadDiameterOption = 0.0);
        Run(app, "tapped-nonzero-taper", Kind.Tapped, true, r => { r.Size = "M6"; r.ThreadDescription = "M6"; r.Taper = M; r.TaperMethod = M; });
        Run(app, "tapdrill-0",     Kind.Through, true, r => r.ThreadTapDrillDiameter = 0.0);
        Run(app, "headclr-0",      Kind.Through, true, r => r.HeadClearance = 0.0);
        Run(app, "chamfer-0",      Kind.Through, true, r => { r.StartChamferOn = 0.0; r.StartChamferSetback = 0.0; r.StartChamferAngle = 0.0; r.NeckChamferOn = 0.0; r.NeckChamferSetback = 0.0; r.NeckChamferAngle = 0.0; r.EndChamferOn = 0.0; r.EndChamferSetback = 0.0; r.EndChamferAngle = 0.0; });

        Console.WriteLine("--- 沉孔：定位 / 螺纹占位字段 ---");
        Run(app, "cb-base",        Kind.Counterbore, true, r => { });
        Run(app, "cb-loc150",      Kind.Counterbore, true, r => r.CounterboreProfileLocationType = P.FeaturePropertyConstants.igCounterboreProfileIsAtBottom);
        Run(app, "cb-loc149",      Kind.Counterbore, true, r => r.CounterboreProfileLocationType = 149.0);
        Run(app, "cb-tapdrill-0",  Kind.Counterbore, true, r => r.ThreadTapDrillDiameter = 0.0);
        Run(app, "cb-headclr-0",   Kind.Counterbore, true, r => r.HeadClearance = 0.0);
        Run(app, "cb-threaddiaopt-0", Kind.Counterbore, true, r => r.ThreadDiameterOption = 0.0);

        Console.WriteLine("--- VBottomDimType （三档同板）---");
        Run(app, "vB-145", Kind.Through, true, r => r.VBottomDimType = P.FeaturePropertyConstants.igVBottomDimToFlat);
        Run(app, "vB-missing", Kind.Through, true, r => r.VBottomDimType = M);
        Run(app, "vB-x", Kind.Through, true, r => r.VBottomDimType = 0.0);
    }

    // 诊断：板厚 10mm 时板体是 z∈[0,10]（模板用 igSymmetric 但没有居中），
    // 所以"下表面"在 z=0。这个组验证：下表面该用哪个基准面偏移 + 哪个 DeepDir 才能建孔。
    static void Bottom(F.Application app){
        Console.WriteLine("--- 下表面建孔：基准面偏移 × 切深方向 ---");
        Var(app, "bot-off-m005", -0.005, true);
        Var(app, "bot-off-p005-near", 0.005, true);
        Var(app, "bot-off-p005-far", 0.005, false);
        Var(app, "bot-off-m005-far", -0.005, false);
    }

    static void Var(F.Application app, string tag, double offset, bool nearFace){
        Console.Write(("=== " + tag + " offset=" + offset + " " + (nearFace ? "近面(下表面)" : "远面(上表面)")).PadRight(46));
        P.PartDocument part = null;
        try {
            P.Model model; part = NewPart(app, "V" + tag.Replace("-", ""), 0.06, 0.06, TM, out model);
            var r = new Recipe();
            var data = part.HoleDataCollection.AddEx(P.FeaturePropertyConstants.igRegularHole,
                "ISO Metric", M, M, M, 0.011, 0, 0, 0, 0, 0,
                P.FeaturePropertyConstants.igNone, M, M, M, M, M,
                P.FeaturePropertyConstants.igVBottomDimToFlat, M, M, M, M, M, M, true,
                M, M, M, M, M, M, M, M, M, M, M, M);
            var plane = part.RefPlanes.AddParallelByDistance(FindXY(part), offset, P.ReferenceElementConstants.igNormalSide, M, M, M, M);
            var prof = part.ProfileSets.Add().Profiles.Add(plane);
            double z = nearFace ? 0.0 : TM;
            double x2, y2; prof.Convert3DCoordinate(0.03, 0.03, z, out x2, out y2);
            prof.Holes2d.Add(x2, y2);
            if (prof.End(P.ProfileValidationType.igProfileClosed) != 0) { Console.WriteLine("轮廓不闭合"); return; }
            P.Hole hole = null; object desc = null;
            foreach (var side in new[]{ P.FeaturePropertyConstants.igLeft, P.FeaturePropertyConstants.igRight }) {
                try { hole = model.Holes.AddThroughAll(prof, side, data); }
                catch (Exception e) { Console.WriteLine("side " + side + " 抛异常 " + e.Message); continue; }
                if (hole == null) { Console.WriteLine("side " + side + " null"); continue; }
                var fs = hole.GetStatusEx(out desc);
                if ((int)fs == (int)P.FeatureStatusConstants.igFeatureOK) { Console.WriteLine("OK side=" + side + " volume=" + F(Vol(model) * 1e9)); break; }
                Console.WriteLine("side=" + side + " status=" + fs);
                try { hole.Delete(); } catch { } hole = null;
            }
        } catch (Exception e) { Console.WriteLine("FATAL " + e.GetType().Name + ": " + e.Message); }
        finally { if (part != null) try { part.Close(false); } catch { } }
    }

    // ---------- 全组合 ----------
    static void Full(F.Application app){
        Console.WriteLine("--- 全组合：四种孔型 × 上/下表面（配方 = matrix 结论）---");
        Case(app, "cb-top", true,  Kind.Counterbore);
        Case(app, "cb-bot", false, Kind.Counterbore);
        Case(app, "cs-top", true,  Kind.Countersink);
        Case(app, "cs-bot", false, Kind.Countersink);
        Case(app, "rg-top", true,  Kind.Through);
        Case(app, "rg-bot", false, Kind.Through);
        Case(app, "tp-top", true,  Kind.Tapped);
        Case(app, "tp-bot", false, Kind.Tapped);
    }

    class Recipe {
        public object Standard = M, SubType = M, Size = M, Fit = M;
        public object ThreadMinor = M, ThreadDepthMethod = M, ThreadDepth = M, ThreadExternal = M, ThreadDescription = M;
        public object TaperMethod = M, Taper = M, TaperDimType = M;
        public object VBottomDimType = P.FeaturePropertyConstants.igVBottomDimToFlat;
        public object CounterboreProfileLocationType = M;
        public object ThreadDiameterOption = M, ThreadTapDrillDiameter = M, HeadClearance = M;
        public object StartChamferOn = M, StartChamferSetback = M, StartChamferAngle = M;
        public object NeckChamferOn = M, NeckChamferSetback = M, NeckChamferAngle = M;
        public object EndChamferOn = M, EndChamferSetback = M, EndChamferAngle = M;
    }

    static void Run(F.Application app, string tag, Kind kind, bool top, Action<Recipe> tweak){
        var r = new Recipe(); tweak(r);
        Drill(app, tag, kind, top, r, true);
    }
    // 最终配方：几何相关的参数全部显式（含"不需要就显式 0"），
    // 只有三类**实测不能显式给值**的留在 Missing：
    //   ① 锥度相关 TaperMethod/Taper/TaperDimType（给 0 会 E_INVALIDARG；留空时 CAD
    //      灌进来的是 igTaperByRatio/0.05/igTaperDimAtBottom，对非锥孔是无副作用的占位值）
    //   ② 螺纹深度法 ThreadDepthMethod（同上）
    //   ③ 那些"给了反而更糟"的：见 AUTO-HOLE.md 的表
    static void Case(F.Application app, string tag, bool top, Kind kind){
        var r = new Recipe();
        r.Standard = "ISO Metric";                 // 空串也行，实测都可用
        r.VBottomDimType = P.FeaturePropertyConstants.igVBottomDimToFlat;
        if (kind == Kind.Tapped) { r.Size = "M6"; r.ThreadMinor = M; r.ThreadExternal = M; r.ThreadDescription = "M6"; }
        if (kind == Kind.Counterbore) r.CounterboreProfileLocationType = P.FeaturePropertyConstants.igCounterboreProfileIsAtTop;
        Drill(app, tag, kind, top, r, false);
    }

    static void Drill(F.Application app, string tag, Kind kind, bool top, Recipe r, bool verbose){
        Console.Write(("=== " + tag + " " + kind + " " + (top ? "上" : "下") + " ===").PadRight(34));
        P.PartDocument part = null;
        try {
            P.Model model; part = NewPart(app, "P" + tag.Replace("-", ""), 0.06, 0.06, TM, out model);
            double v0 = Vol(model);
            double dia, cbd = 0, cbdep = 0, csd = 0, csa = 0, bottom = 0;
            switch (kind) {
                case Kind.Counterbore: dia = 0.011; cbd = 0.018; cbdep = 0.011; break;
                case Kind.Countersink: dia = 0.011; csd = 0.020; csa = 90.0; break;
                case Kind.Tapped: dia = 0.004917; break;
                default: dia = 0.011; break;
            }
            var data = part.HoleDataCollection.AddEx(ToType(kind),
                r.Standard, r.SubType, r.Size, r.Fit, dia,
                cbd, cbdep, csd, csa, bottom,
                P.FeaturePropertyConstants.igNone, r.TaperMethod, r.Taper,
                r.ThreadMinor, r.ThreadDepthMethod, r.ThreadDepth,
                r.VBottomDimType, r.TaperDimType,
                r.CounterboreProfileLocationType, M, M,
                r.ThreadExternal, r.ThreadDescription, true,
                r.ThreadDiameterOption, r.ThreadTapDrillDiameter, r.HeadClearance,
                r.StartChamferOn, r.StartChamferSetback, r.StartChamferAngle,
                r.NeckChamferOn, r.NeckChamferSetback, r.NeckChamferAngle,
                r.EndChamferOn, r.EndChamferSetback, r.EndChamferAngle);
            if (verbose) Console.WriteLine();
            if (verbose) Console.WriteLine("      IN  " + Brief(data));
            var plane = part.RefPlanes.AddParallelByDistance(FindXY(part), top ? HALF : -HALF,
                P.ReferenceElementConstants.igNormalSide, M, M, M, M);
            var prof = part.ProfileSets.Add().Profiles.Add(plane);
            double x2, y2; prof.Convert3DCoordinate(0.03, 0.03, top ? HALF : -HALF, out x2, out y2);
            prof.Holes2d.Add(x2, y2);
            if (prof.End(P.ProfileValidationType.igProfileClosed) != 0) { Console.WriteLine("轮廓不闭合"); return; }
            P.Hole hole = null; object desc = null;
            foreach (var side in new[]{ P.FeaturePropertyConstants.igLeft, P.FeaturePropertyConstants.igRight }) {
                try { hole = model.Holes.AddThroughAll(prof, side, data); }
                catch (Exception e) { Console.WriteLine("side " + side + " 抛异常 " + e.Message); continue; }
                if (hole == null) { Console.WriteLine("side " + side + " null"); continue; }
                var fs = hole.GetStatusEx(out desc);
                if ((int)fs == (int)P.FeatureStatusConstants.igFeatureOK) { Console.WriteLine("OK side=" + side); break; }
                Console.WriteLine("side=" + side + " status=" + fs + " desc=" + desc);
                try { hole.Delete(); } catch { } hole = null;
            }
            if (hole == null) return;
            double removed = (Vol(model) - v0) * 1e9;
            int cones; string cinfo; CountCones((G.Body)model.Body, out cones, out cinfo);
            Console.WriteLine("      GEO removed=" + F(removed) + " mm3 cones=" + cones + (cinfo.Length > 0 ? "  " + cinfo : ""));
            var back = hole.HoleData as P.HoleData;
            Console.WriteLine("      OUT " + (back == null ? "(无 HoleData)" : Brief(back)));
            part.SaveAs(Path.Combine(OutDir, tag + ".par"));
        } catch (Exception e) { Console.WriteLine("FATAL " + e.GetType().Name + ": " + e.Message); }
        finally { if (part != null) try { part.Close(false); } catch { } }
    }

    static P.FeaturePropertyConstants ToType(Kind k){
        switch (k) {
            case Kind.Counterbore: return P.FeaturePropertyConstants.igCounterboreHole;
            case Kind.Countersink: return P.FeaturePropertyConstants.igCountersinkHole;
            default: return P.FeaturePropertyConstants.igRegularHole;
        }
    }

    static string Brief(P.HoleData d){
        if (d == null) return "(null)";
        return "type=" + Safe(() => d.HoleType.ToString())
            + " dia=" + Safe(() => F(d.HoleDiameter * 1000))
            + " cbd=" + Safe(() => F(d.CounterboreDiameter * 1000))
            + " cbdep=" + Safe(() => F(d.CounterboreDepth * 1000))
            + " csd=" + Safe(() => F(d.CountersinkDiameter * 1000))
            + " csa=" + Safe(() => F(d.CountersinkAngle))
            + " bottom=" + Safe(() => F(d.BottomAngle))
            + " vB=" + Safe(() => d.VBottomDimType.ToString())
            + " taperM=" + Safe(() => d.TaperMethod.ToString())
            + " taper=" + Safe(() => F(d.Taper))
            + " tDim=" + Safe(() => d.TaperDimType.ToString())
            + " cbLoc=" + Safe(() => d.CounterboreProfileLocationType.ToString())
            + " treat=" + Safe(() => d.TreatmentType.ToString())
            + " threadDesc=" + Safe(() => d.ThreadDescription);
    }
    static string Safe(Func<string> f){ try { return f(); } catch { return "<err>"; } }
    static string F(double v){ return v.ToString("0.####", CultureInfo.InvariantCulture); }

    static void CountCones(G.Body body, out int cones, out string info){
        cones = 0; info = "";
        var list = new List<string>();
        foreach (G.Face f in (G.Faces)body.get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll)) {
            object geo = null; try { geo = f.Geometry; } catch { }
            var cone = geo as G.Cone; if (cone == null) continue;
            cones++;
            try {
                Array o = new double[3], ax = new double[3]; double r = 0, ang = 0; bool ex = false;
                cone.GetConeData(ref o, ref ax, out r, out ang, out ex);
                list.Add("CONE z=" + F(D(o, 2) * 1000) + "mm r=" + F(r * 1000) + "mm halfAngle=" + F(ang * 180 / Math.PI) + "deg");
            } catch { }
        }
        info = string.Join(" | ", list.ToArray());
    }
    static double Vol(P.Model m){ return ((G.Body)m.Body).Volume; }
    static double D(Array a, int i){ return Convert.ToDouble(a.GetValue(i)); }

    static P.RefPlane FindXY(P.PartDocument part){
        foreach (P.RefPlane c in part.RefPlanes) {
            Array n = new double[3], p = new double[3], u = new double[3];
            c.GetNormal(ref n); c.GetRootPoint(ref p); c.GetReferenceDirection(ref u);
            if (Math.Abs(D(n, 2) - 1) < 1e-8 && Math.Abs(D(p, 0)) < 1e-8 && Math.Abs(D(p, 1)) < 1e-8 && Math.Abs(D(u, 0) - 1) < 1e-8) return c;
        }
        return null;
    }

    static P.PartDocument NewPart(F.Application app, string n, double w, double h, double t, out P.Model model){
        P.PartDocument part = (P.PartDocument)app.Documents.Add("SolidEdge.PartDocument", Template);
        part.ModelingMode = P.ModelingModeConstants.seModelingModeOrdered;
        var profile = part.ProfileSets.Add().Profiles.Add(FindXY(part));
        var L = new S.Line2d[4];
        L[0] = profile.Lines2d.AddBy2Points(0, 0, w, 0); L[1] = profile.Lines2d.AddBy2Points(w, 0, w, h);
        L[2] = profile.Lines2d.AddBy2Points(w, h, 0, h); L[3] = profile.Lines2d.AddBy2Points(0, h, 0, 0);
        var rel = (S.Relations2d)profile.Relations2d;
        for (int i = 0; i < 4; i++) {
            rel.AddKeypoint(L[i], (int)SolidEdgeConstants.KeypointIndexConstants.igLineEnd, L[(i + 1) % 4], (int)SolidEdgeConstants.KeypointIndexConstants.igLineStart);
            if (i % 2 == 0) rel.AddHorizontal(L[i]); else rel.AddVertical(L[i]);
        }
        rel.AddKeypointFix(L[0], (int)SolidEdgeConstants.KeypointIndexConstants.igLineStart);
        var dims = (S.Dimensions)profile.Dimensions; dims.Constraint = true; dims.AddLength(L[0]); dims.AddLength(L[1]);
        if (profile.End(P.ProfileValidationType.igProfileClosed) != 0) throw new InvalidOperationException("base not closed");
        Array arr = new object[]{ profile };
        model = part.Models.AddFiniteExtrudedProtrusion(1, ref arr, P.FeaturePropertyConstants.igSymmetric, t);
        return part;
    }
}
