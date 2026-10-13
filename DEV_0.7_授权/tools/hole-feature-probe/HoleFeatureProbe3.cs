using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using F=SolidEdgeFramework;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;
using S=SolidEdgeFrameworkSupport;

// 孔特征事实探针 · 第三轮（2026-10-13）
//   E1 一个轮廓 4 个 Holes2d，换不同的 End 校验标志（第二轮发现：End(igProfileClosed) 把 4 个收成 1 个）
//   E2 E1 里能成的那种：挪其中一个孔心、删/加孔心，孔跟不跟
//   E3 螺纹深度的线框截图（看装饰螺纹到底多长）
class HoleFeatureProbe3 {
    static object M = Type.Missing;
    static string Out = ".";
    static F.Application app;
    const string Tpl = @"C:\Program Files\NDS\TianGong 2025\Template\ISO Metric\";
    static void Say(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    static double D(Array a, int i){ return Convert.ToDouble(a.GetValue(i)); }
    static string Mm(double m){ return (m * 1000.0).ToString("0.###"); }

    static int Main(string[] args){
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Out = args.Length > 0 ? args[0] : ".";
        Directory.CreateDirectory(Out);
        app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application");
        Say("CAD " + app.Version);
        try { app.DisplayAlerts = false; } catch { }
        Run("E1 End 标志", E1);
        Run("E2 多孔特征改孔心/孔径", E2);
        Run("E3 螺纹线框截图", E3);
        Say("PROBE DONE");
        return 0;
    }
    static void Run(string name, Action a){
        Say(""); Say("==== " + name);
        try { a(); } catch (Exception e) { Say("FATAL " + name + " : 0x" + e.HResult.ToString("X8") + " " + e.Message); }
    }

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
        prof.End(P.ProfileValidationType.igProfileClosed);
        Array arr = new object[]{ prof };
        var model = part.Models.AddFiniteExtrudedProtrusion(1, ref arr, P.FeaturePropertyConstants.igRight, t);
        return new Plate { Part = part, Model = model, Top = t };
    }
    static P.RefPlane FindXY(P.PartDocument part){
        foreach (P.RefPlane c in part.RefPlanes) {
            Array n = new double[3], p = new double[3], u = new double[3];
            c.GetNormal(ref n); c.GetRootPoint(ref p); c.GetReferenceDirection(ref u);
            if (Math.Abs(D(n,2)-1) < 1e-8 && Math.Abs(D(p,0)) < 1e-8 && Math.Abs(D(p,1)) < 1e-8 && Math.Abs(D(u,0)-1) < 1e-8) return c;
        }
        return null;
    }
    static G.Face TopFace(Plate p){
        foreach (G.Face f in (G.Faces)((G.Body)p.Model.Body).get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll)) {
            var pl = f.Geometry as G.Plane; if (pl == null) continue;
            Array a = new double[3], n = new double[3]; pl.GetPlaneData(ref a, ref n);
            if (Math.Abs(Math.Abs(D(n,2)) - 1) < 1e-6 && Math.Abs(D(a,2) - p.Top) < 1e-9) return f;
        }
        return null;
    }
    static P.RefPlane TopPlane(Plate p){ return p.Part.RefPlanes.AddParallelByDistance(TopFace(p), 0.0, P.ReferenceElementConstants.igNormalSide, M, M, M, M); }
    static double Vol(Plate p){ return ((G.Body)p.Model.Body).Volume; }
    static double Cyl(double d, double h){ return Math.PI * d * d / 4 * h; }
    static List<string> HolesOnTop(Plate p){
        var res = new List<string>();
        foreach (G.Edge e in (G.Edges)((G.Body)p.Model.Body).get_Edges(G.FeatureTopologyQueryTypeConstants.igQueryAll)) {
            var c = e.Geometry as G.Circle; if (c == null) continue;
            Array cc = new double[3], ax = new double[3]; double r; c.GetCircleData(ref cc, ref ax, out r);
            if (Math.Abs(D(cc,2) - p.Top) > 1e-7) continue;
            res.Add("(" + Mm(D(cc,0)) + "," + Mm(D(cc,1)) + ")Φ" + Mm(2*r));
        }
        res.Sort(); return res;
    }
    static P.HoleData Tapped(P.PartDocument part, object tdm, object td){
        var d = part.HoleDataCollection.AddEx(P.FeaturePropertyConstants.igRegularHole,
            "ISO Metric", M, "M6", M, 0.004917,
            0.0, 0.0, 0.0, 0.0, 0.0,
            P.FeaturePropertyConstants.igTappedHole, M, M, 0.004917, tdm, td, P.FeaturePropertyConstants.igVBottomDimToFlat, M,
            M, M, M, M, M, true,
            M, M, M, M, M, M, M, M, M, M, M, M);
        try { d.ThreadExternalDiameter = 0.006; d.ThreadDescription = "M6"; } catch { }
        return d;
    }
    static P.HoleData Regular(P.PartDocument part, double dia){
        return part.HoleDataCollection.AddEx(P.FeaturePropertyConstants.igRegularHole,
            "ISO Metric", M, M, M, dia, 0.0, 0.0, 0.0, 0.0, 0.0,
            P.FeaturePropertyConstants.igNone, M, M, M, M, M, P.FeaturePropertyConstants.igVBottomDimToFlat, M,
            M, M, M, M, M, true, M, M, M, M, M, M, M, M, M, M, M, M);
    }
    static P.Hole Make(Plate p, P.Profile prof, P.HoleData data, double depth){
        foreach (var side in new[]{ P.FeaturePropertyConstants.igLeft, P.FeaturePropertyConstants.igRight }) {
            P.Hole h = null;
            try { h = depth > 0 ? p.Model.Holes.AddFinite(prof, side, depth, data) : p.Model.Holes.AddThroughAll(prof, side, data); }
            catch (Exception e) { Say("   建孔(" + side + ") 异常 0x" + e.HResult.ToString("X8") + " " + e.Message); continue; }
            if (h == null) continue;
            object desc; var st = h.GetStatusEx(out desc);
            if ((int)st == (int)P.FeatureStatusConstants.igFeatureOK) return h;
            Say("   建孔(" + side + ") 状态 " + st);
            try { h.Delete(); } catch { }
        }
        return null;
    }
    static void Close(Plate p, string save){
        try { if (save != null) p.Part.SaveAs(Path.Combine(Out, save)); } catch (Exception e) { Say("   保存失败 " + e.Message); }
        try { p.Part.Close(false); } catch { }
    }

    static readonly double[][] Pts = { new[]{0.02,0.02}, new[]{0.08,0.02}, new[]{0.08,0.08}, new[]{0.02,0.08} };

    static P.Profile MultiProfile(Plate p, P.RefPlane plane, double[][] pts, int flags, bool callEnd){
        var prof = p.Part.ProfileSets.Add().Profiles.Add(plane);
        foreach (var c in pts) { double x2, y2; prof.Convert3DCoordinate(c[0], c[1], p.Top, out x2, out y2); prof.Holes2d.Add(x2, y2); }
        if (callEnd) {
            try { int e = prof.End((P.ProfileValidationType)flags); Say("   End(" + flags + ")=" + e + " 之后 Holes2d.Count=" + prof.Holes2d.Count); }
            catch (Exception e) { Say("   End(" + flags + ") 异常 0x" + e.HResult.ToString("X8") + " " + e.Message + " Count=" + prof.Holes2d.Count); }
        } else Say("   不调 End，Holes2d.Count=" + prof.Holes2d.Count);
        return prof;
    }

    static void E1(){
        var cases = new object[][] {
            new object[]{ "End(0)", 0, true },
            new object[]{ "AllowPointsAsProfiles", 524288, true },
            new object[]{ "AllowNested", 8192, true },
            new object[]{ "Closed|AllowNested", 1 | 8192, true },
            new object[]{ "Closed|AllowPoints", 1 | 524288, true },
            new object[]{ "NoSelfIntersect", 8, true },
            new object[]{ "不调End", 0, false },
        };
        foreach (var c in cases) {
            string tag = (string)c[0];
            Plate p = null;
            try {
                p = NewPlate(0.1, 0.1, 0.02); double v0 = Vol(p);
                var prof = MultiProfile(p, TopPlane(p), Pts, (int)c[1], (bool)c[2]);
                var h = Make(p, prof, Regular(p.Part, 0.006), 0);
                double cut = v0 - Vol(p), want = 4 * Cyl(0.006, 0.02);
                Say("RESULT E1 " + tag + "：" + (h == null ? "建不出来" : ("切除比 " + (cut/want).ToString("0.000") + " 特征数 " + p.Model.Holes.Count + " 顶面圆 " + string.Join(" ", HolesOnTop(p).ToArray()))));
            } catch (Exception e) { Say("RESULT E1 " + tag + " 异常 0x" + e.HResult.ToString("X8") + " " + e.Message); }
            finally { if (p != null) Close(p, null); }
        }
        foreach (var blind in new[]{ false, true }) {
            Plate p = null;
            try {
                p = NewPlate(0.1, 0.1, 0.02); double v0 = Vol(p);
                var prof = MultiProfile(p, TopPlane(p), Pts, 0, true);
                var h = Make(p, prof, Tapped(p.Part, P.FeaturePropertyConstants.igFinite, 0.012), blind ? 0.016 : 0);
                double cut = v0 - Vol(p), want = 4 * Cyl(0.004917, blind ? 0.016 : 0.02);
                Say("RESULT E1 螺纹" + (blind ? "盲16" : "贯通") + " End(0)：" + (h == null ? "建不出来" : ("切除比 " + (cut/want).ToString("0.000") + " 名 " + h.Name)));
            } catch (Exception e) { Say("RESULT E1 螺纹 异常 " + e.Message); }
            finally { if (p != null) Close(p, blind ? "E1-tapped-blind.par" : "E1-tapped.par"); }
        }
    }

    static void E2(){
        var p = NewPlate(0.1, 0.1, 0.02);
        var prof = MultiProfile(p, TopPlane(p), Pts, 0, true);
        var h = Make(p, prof, Regular(p.Part, 0.006), 0);
        if (h == null) { Say("RESULT E2 建不出来"); Close(p, null); return; }
        Say("   改前 " + string.Join(" ", HolesOnTop(p).ToArray()));
        var fp = (P.Profile)h.Profile;
        Say("   特征轮廓 Holes2d.Count=" + fp.Holes2d.Count);
        try {
            var h2 = fp.Holes2d.Item(2);
            double tx, ty; fp.Convert3DCoordinate(0.07, 0.03, p.Top, out tx, out ty);
            h2.SetCenterPoint(tx, ty);
            Say("   第 2 个孔心 -> (70,30)");
        } catch (Exception e) { Say("   SetCenterPoint 异常 " + e.Message); }
        try { Say("   End(0)=" + fp.End((P.ProfileValidationType)0) + " Count=" + fp.Holes2d.Count); } catch (Exception e) { Say("   End 异常 " + e.Message); }
        try { p.Model.Recompute(); } catch (Exception e) { Say("   Recompute 异常 " + e.Message); }
        object desc; Say("   状态 " + h.GetStatusEx(out desc));
        Say("RESULT E2 挪孔心后 " + string.Join(" ", HolesOnTop(p).ToArray()) + "（期望 (20,20)(70,30)(80,80)(20,80)）");
        for (int i = 1; i <= fp.Holes2d.Count; i++) {
            double x2, y2, x, y, z; fp.Holes2d.Item(i).GetCenterPoint(out x2, out y2); fp.Convert2DCoordinate(x2, y2, out x, out y, out z);
            Say("   Hole2d#" + i + " 2D(" + Mm(x2) + "," + Mm(y2) + ") -> 3D(" + Mm(x) + "," + Mm(y) + "," + Mm(z) + ")");
        }
        try { fp.Holes2d.Item(4).Delete(); fp.End((P.ProfileValidationType)0); p.Model.Recompute(); Say("RESULT E2 删第 4 个孔心后 " + string.Join(" ", HolesOnTop(p).ToArray())); }
        catch (Exception e) { Say("RESULT E2 删孔心异常 " + e.Message); }
        try { double tx, ty; fp.Convert3DCoordinate(0.05, 0.05, p.Top, out tx, out ty); fp.Holes2d.Add(tx, ty); fp.End((P.ProfileValidationType)0); p.Model.Recompute(); Say("RESULT E2 加孔心 (50,50) 后 " + string.Join(" ", HolesOnTop(p).ToArray()) + " 状态 " + h.GetStatusEx(out desc)); }
        catch (Exception e) { Say("RESULT E2 加孔心异常 " + e.Message); }
        try { h.Name = "通孔 Φ6 ×4"; Say("   改名后 " + h.Name); } catch (Exception e) { Say("   改名异常 " + e.Message); }
        Close(p, "E2-multi-move.par");
    }

    static void E3(){
        Shot("贯通-默认2mm", 0, M, M, "E3-a-default");
        Shot("贯通-igThroughAll", 0, P.FeaturePropertyConstants.igThroughAll, M, "E3-b-throughall");
        Shot("贯通-igFinite15", 0, P.FeaturePropertyConstants.igFinite, 0.015, "E3-c-finite15");
        Shot("盲16-igThroughAll", 0.016, P.FeaturePropertyConstants.igThroughAll, M, "E3-d-blind-throughall");
    }
    static void Shot(string tag, double depth, object tdm, object td, string shot){
        var p = NewPlate(0.03, 0.03, 0.02);
        var prof = p.Part.ProfileSets.Add().Profiles.Add(TopPlane(p));
        double x2, y2; prof.Convert3DCoordinate(0.015, 0.015, p.Top, out x2, out y2);
        prof.Holes2d.Add(x2, y2); prof.End(P.ProfileValidationType.igProfileClosed);
        var h = Make(p, prof, Tapped(p.Part, tdm, td), depth);
        if (h == null) { Say("RESULT E3 " + tag + " 建不出来"); Close(p, null); return; }
        var d = (P.HoleData)h.HoleData;
        Say("RESULT E3 " + tag + "：TDM=" + d.ThreadDepthMethod + " TD=" + Mm(d.ThreadDepth));
        try {
            dynamic view = ((dynamic)app.ActiveWindow).View;
            try { view.ApplyNamedView("前视图"); } catch (Exception e) { Say("   前视图 " + e.Message); try { view.ApplyNamedView("Front"); } catch { } }
            try { view.RenderModeType = F.SeRenderModeType.seRenderModeWireframe; } catch (Exception e) { Say("   线框 " + e.Message); try { view.SetRenderMode(1); } catch { } }
            view.Fit();
            System.Threading.Thread.Sleep(1500);
            Snap((IntPtr)(int)app.hWnd, Path.Combine(Out, shot + ".png"));
        } catch (Exception e) { Say("   截图异常 " + e.Message); }
        Close(p, shot + ".par");
    }
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    static void Snap(IntPtr hwnd, string path){
        RECT r; GetWindowRect(hwnd, out r);
        int w = Math.Max(1, r.R - r.L), hh = Math.Max(1, r.B - r.T);
        using (var bmp = new Bitmap(w, hh)) {
            using (var g = Graphics.FromImage(bmp)) { var dc = g.GetHdc(); PrintWindow(hwnd, dc, 2); g.ReleaseHdc(dc); }
            bmp.Save(path, ImageFormat.Png);
        }
        Say("   截图 " + path);
    }
}
