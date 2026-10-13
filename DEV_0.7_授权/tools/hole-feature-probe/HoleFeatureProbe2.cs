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

// 孔特征事实探针 · 第二轮（2026-10-13）
//   R1 一个轮廓 N 个 Holes2d：每 Add 一次看 Count / 返回值（第一轮只看到 Count=1）
//   R2 一个轮廓 N 个 Circles2d + Holes.AddThroughAll / AddFinite：能不能一个特征切 N 个孔、孔型数据还在不在
//   R3 R2 那种特征里挪其中一个圆的圆心 -> 孔跟不跟
//   R4 编组：组里的孔改名、组上挂属性、保存重开
//   R5 螺纹深度：盲孔 + igThroughAll；贯通 + igThroughAll 的 ThreadDepth 回读；截图看装饰螺纹长度
class HoleFeatureProbe2 {
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
        Run("R1 多 Holes2d", R1);
        Run("R2 多 Circles2d 一个特征", R2);
        Run("R3 挪圆心", R3);
        Run("R4 编组改名/属性", R4);
        Run("R5 螺纹深度与截图", R5);
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

    static void R1(){
        var p = NewPlate(0.1, 0.1, 0.02);
        var prof = p.Part.ProfileSets.Add().Profiles.Add(TopPlane(p));
        foreach (var c in Pts) {
            double x2, y2; prof.Convert3DCoordinate(c[0], c[1], p.Top, out x2, out y2);
            try {
                var h2 = prof.Holes2d.Add(x2, y2);
                double cx = 0, cy = 0; try { h2.GetCenterPoint(out cx, out cy); } catch { }
                Say("   Holes2d.Add(" + Mm(x2) + "," + Mm(y2) + ") -> " + (h2 == null ? "null" : ("圆心(" + Mm(cx) + "," + Mm(cy) + ")")) + " Count=" + prof.Holes2d.Count
                    + " Points2d=" + SafeCount(() => prof.Points2d.Count) + " Circles2d=" + prof.Circles2d.Count);
            } catch (Exception e) { Say("   Holes2d.Add 异常 0x" + e.HResult.ToString("X8") + " " + e.Message); }
        }
        Say("RESULT R1 最终 Holes2d.Count=" + prof.Holes2d.Count);
        Close(p, null);
    }
    static string SafeCount(Func<int> f){ try { return f().ToString(); } catch (Exception e) { return "!" + e.HResult.ToString("X8"); } }

    static P.Profile CircleProfile(Plate p, P.RefPlane plane, double dia, double[][] pts){
        var prof = p.Part.ProfileSets.Add().Profiles.Add(plane);
        foreach (var c in pts) {
            double x2, y2; prof.Convert3DCoordinate(c[0], c[1], p.Top, out x2, out y2);
            prof.Circles2d.AddByCenterRadius(x2, y2, dia / 2);
        }
        int end = prof.End(P.ProfileValidationType.igProfileClosed);
        Say("   圆轮廓 Circles2d.Count=" + prof.Circles2d.Count + " End=" + end);
        return prof;
    }

    static void R2(){
        // 贯通普通孔
        var p = NewPlate(0.1, 0.1, 0.02); double v0 = Vol(p);
        var h = Make(p, CircleProfile(p, TopPlane(p), 0.0066, Pts), Regular(p.Part, 0.0066), 0);
        double cut = v0 - Vol(p), want = 4 * Cyl(0.0066, 0.02);
        Say("RESULT R2 圆轮廓·贯通Φ6.6：" + (h == null ? "建不出来" : ("切除比 " + (cut/want).ToString("0.000") + " 特征数 " + p.Model.Holes.Count + " 顶面圆 " + string.Join(" ", HolesOnTop(p).ToArray()))));
        if (h != null) Say("   孔名 " + h.Name);
        Close(p, "R2-through.par");
        // 螺纹孔（M6 内小径 4.917，贯通，螺纹 igFinite 15）
        var q = NewPlate(0.1, 0.1, 0.02); double q0 = Vol(q);
        var hq = Make(q, CircleProfile(q, TopPlane(q), 0.004917, Pts), Tapped(q.Part, P.FeaturePropertyConstants.igFinite, 0.015), 0);
        double c2 = q0 - Vol(q), w2 = 4 * Cyl(0.004917, 0.02);
        Say("RESULT R2 圆轮廓·M6 螺纹贯通：" + (hq == null ? "建不出来" : ("切除比 " + (c2/w2).ToString("0.000") + " 特征数 " + q.Model.Holes.Count)));
        if (hq != null) { var d = (P.HoleData)hq.HoleData; Say("   回读 Treat=" + d.TreatmentType + " TDM=" + d.ThreadDepthMethod + " TD=" + Mm(d.ThreadDepth) + " Desc=" + d.ThreadDescription + " 名 " + hq.Name); }
        Close(q, "R2-tapped.par");
        // 盲孔沉孔
        var r = NewPlate(0.1, 0.1, 0.02); double r0 = Vol(r);
        var cb = r.Part.HoleDataCollection.AddEx(P.FeaturePropertyConstants.igCounterboreHole,
            "ISO Metric", M, M, M, 0.0066, 0.011, 0.0065, 0.0, 0.0, 0.0,
            P.FeaturePropertyConstants.igNone, M, M, M, M, M, P.FeaturePropertyConstants.igVBottomDimToFlat, M,
            P.FeaturePropertyConstants.igCounterboreProfileIsAtTop, M, M, M, M, true, M, M, M, M, M, M, M, M, M, M, M, M);
        var hr = Make(r, CircleProfile(r, TopPlane(r), 0.0066, Pts), cb, 0);
        double c3 = r0 - Vol(r), w3 = 4 * (Cyl(0.011, 0.0065) + Cyl(0.0066, 0.02 - 0.0065));
        Say("RESULT R2 圆轮廓·沉孔贯通：" + (hr == null ? "建不出来" : ("切除比 " + (c3/w3).ToString("0.000") + " 特征数 " + r.Model.Holes.Count)));
        Close(r, "R2-cbore.par");
        // 盲孔 12
        var s = NewPlate(0.1, 0.1, 0.02); double s0 = Vol(s);
        var hs = Make(s, CircleProfile(s, TopPlane(s), 0.006, Pts), Regular(s.Part, 0.006), 0.012);
        double c4 = s0 - Vol(s), w4 = 4 * Cyl(0.006, 0.012);
        Say("RESULT R2 圆轮廓·盲孔 12：" + (hs == null ? "建不出来" : ("切除比 " + (c4/w4).ToString("0.000"))));
        Close(s, null);
    }

    static void R3(){
        var p = NewPlate(0.1, 0.1, 0.02);
        var h = Make(p, CircleProfile(p, TopPlane(p), 0.006, Pts), Regular(p.Part, 0.006), 0);
        if (h == null) { Say("RESULT R3 建孔失败"); Close(p, null); return; }
        var prof = (P.Profile)h.Profile;
        Say("   改前 " + string.Join(" ", HolesOnTop(p).ToArray()));
        // 第 1 个圆挪到 (30,30)
        var c = prof.Circles2d.Item(1);
        double cx, cy; c.GetCenterPoint(out cx, out cy);
        double tx, ty; prof.Convert3DCoordinate(0.03, 0.03, p.Top, out tx, out ty);
        try { c.SetCenterPoint(tx, ty); Say("   Circle2d.SetCenterPoint OK"); } catch (Exception e) { Say("   SetCenterPoint 异常 " + e.Message); try { c.Move(cx, cy, tx, ty); Say("   Move OK"); } catch (Exception e2) { Say("   Move 异常 " + e2.Message); } }
        try { Say("   End=" + prof.End(P.ProfileValidationType.igProfileClosed)); } catch (Exception e) { Say("   End 异常 " + e.Message); }
        try { p.Model.Recompute(); } catch (Exception e) { Say("   Recompute 异常 " + e.Message); }
        object desc; Say("   状态 " + h.GetStatusEx(out desc));
        Say("RESULT R3 改后 " + string.Join(" ", HolesOnTop(p).ToArray()) + "（期望第一个到 (30,30)，其余不动）");
        // 改孔径（四个一起）：HoleData.HoleDiameter + 圆半径
        Close(p, "R3-move.par");
    }

    static void R4(){
        var p = NewPlate(0.1, 0.1, 0.02);
        var plane = TopPlane(p);
        var holes = new List<object>();
        int k = 0;
        foreach (var c in Pts) {
            var prof = p.Part.ProfileSets.Add().Profiles.Add(plane);
            double x2, y2; prof.Convert3DCoordinate(c[0], c[1], p.Top, out x2, out y2);
            prof.Holes2d.Add(x2, y2); prof.End(P.ProfileValidationType.igProfileClosed);
            var h = Make(p, prof, Tapped(p.Part, P.FeaturePropertyConstants.igFinite, 0.012), 0);
            if (h == null) continue;
            k++; try { h.Name = "M6螺纹孔-" + k; } catch (Exception e) { Say("   孔改名异常 " + e.Message); }
            try { var set = ((F.AttributeSets)h.AttributeSets).Add("TGAutoHole"); set.Add("Link", F.AttributeTypeConstants.seStringUnicode).Value = "hole" + k; } catch (Exception e) { Say("   孔属性异常 " + e.Message); }
            holes.Add(h);
        }
        Array arr = holes.ToArray();
        P.FeatureGroup g = null;
        try { g = p.Model.FeatureGroups.AddFeatureGroupsBySet(holes.Count, ref arr); } catch (Exception e) { Say("   编组异常 " + e.Message); }
        if (g == null) { Say("RESULT R4 编组失败"); Close(p, null); return; }
        try { g.Name = "M6螺纹孔 ×4（螺纹深12）"; } catch (Exception e) { Say("   组改名异常 " + e.Message); }
        try { var set = ((F.AttributeSets)g.AttributeSets).Add("TGAutoHoleGroup"); set.Add("Spec", F.AttributeTypeConstants.seStringUnicode).Value = "M6|tapped"; } catch (Exception e) { Say("   组属性异常 0x" + e.HResult.ToString("X8") + " " + e.Message); }
        string path = Path.Combine(Out, "R4-group.par");
        Close(p, "R4-group.par");
        var re = (P.PartDocument)app.Documents.Open(path);
        var m = (P.Model)re.Models.Item(1);
        Say("   重开 FeatureGroups.Count=" + m.FeatureGroups.Count);
        foreach (P.FeatureGroup fg in m.FeatureGroups) {
            Say("   组 Name=" + fg.Name + " Edgebar=" + fg.EdgebarName + " Count=" + fg.Count + " 属性=" + SafeBool(() => fg.get_IsAttributeSetPresent("TGAutoHoleGroup")));
            for (int i = 1; i <= fg.Count; i++) {
                object it = fg.Item(i);
                var hh = it as P.Hole;
                Say("     成员 " + i + " " + (hh == null ? it.GetType().Name : (hh.Name + " 属性=" + SafeBool(() => hh.get_IsAttributeSetPresent("TGAutoHole")))));
            }
        }
        // 往已有组里追加一个孔：没有 Add 方法 —— 先散组再按新集合重编
        try {
            var fg = m.FeatureGroups.Item(1);
            var members = new List<object>();
            for (int i = 1; i <= fg.Count; i++) members.Add(fg.Item(i));
            string name = fg.Name;
            fg.Ungroup();
            var top = TopFace(new Plate { Part = re, Model = m, Top = 0.02 });
            var pl2 = re.RefPlanes.AddParallelByDistance(top, 0.0, P.ReferenceElementConstants.igNormalSide, M, M, M, M);
            var prof = re.ProfileSets.Add().Profiles.Add(pl2);
            double x2, y2; prof.Convert3DCoordinate(0.05, 0.05, 0.02, out x2, out y2);
            prof.Holes2d.Add(x2, y2); prof.End(P.ProfileValidationType.igProfileClosed);
            var extra = Make(new Plate { Part = re, Model = m, Top = 0.02 }, prof, Tapped(re, P.FeaturePropertyConstants.igFinite, 0.012), 0);
            members.Add(extra);
            Array a2 = members.ToArray();
            var g2 = m.FeatureGroups.AddFeatureGroupsBySet(members.Count, ref a2);
            g2.Name = name.Replace("×4", "×5");
            Say("RESULT R4 散组重编 OK：" + g2.Name + " Count=" + g2.Count + " 组数=" + m.FeatureGroups.Count);
        } catch (Exception e) { Say("RESULT R4 散组重编异常 0x" + e.HResult.ToString("X8") + " " + e.Message); }
        re.Close(false);
    }
    static string SafeBool(Func<bool> f){ try { return f().ToString(); } catch (Exception e) { return "!" + e.HResult.ToString("X8"); } }

    static void R5(){
        Thread("贯通-默认(2mm)", 0, M, M, "R5-a-default");
        Thread("贯通-igFinite15", 0, P.FeaturePropertyConstants.igFinite, 0.015, "R5-b-finite15");
        Thread("贯通-igThroughAll", 0, P.FeaturePropertyConstants.igThroughAll, M, "R5-c-throughall");
        Thread("盲16-igThroughAll", 0.016, P.FeaturePropertyConstants.igThroughAll, M, "R5-d-blind-throughall");
        Thread("盲16-igFinite16", 0.016, P.FeaturePropertyConstants.igFinite, 0.016, "R5-e-blind-finite16");
    }
    static void Thread(string tag, double depth, object tdm, object td, string shot){
        var p = NewPlate(0.06, 0.06, 0.02);
        // 孔放在边上：剖开看（板宽 60，孔心 x=30 y=0 —— 半个孔露在外面能直接看到螺纹段）
        var prof = p.Part.ProfileSets.Add().Profiles.Add(TopPlane(p));
        double x2, y2; prof.Convert3DCoordinate(0.03, 0.03, p.Top, out x2, out y2);
        prof.Holes2d.Add(x2, y2); prof.End(P.ProfileValidationType.igProfileClosed);
        var h = Make(p, prof, Tapped(p.Part, tdm, td), depth);
        if (h == null) { Say("RESULT R5 " + tag + " 建不出来"); Close(p, null); return; }
        var d = (P.HoleData)h.HoleData;
        Say("RESULT R5 " + tag + "：TDM=" + d.ThreadDepthMethod + " TD=" + Mm(d.ThreadDepth) + " Ext=" + d.TGHoleExtent + " Depth=" + Mm(d.TGHoleDepth));
        try {
            dynamic win = app.ActiveWindow;
            dynamic view = win.View;
            try { view.ApplyNamedView("等轴测视图"); } catch { try { view.ApplyNamedView("Iso"); } catch { } }
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
