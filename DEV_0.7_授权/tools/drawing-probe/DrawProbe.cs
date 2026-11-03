using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using D=SolidEdgeDraft;
using F=SolidEdgeFramework;
using S=SolidEdgeFrameworkSupport;

// 一键出图的接口事实探针（2026-11-03）：在零件副本上用公司 A3 模板新建工程图，
// 量：视图怎么放、视图里的二维几何能读到什么、各类尺寸（长度 / 两线距离 / 直径 / 半径 / 坐标尺寸）建不建得出来、
// 前缀能不能写、引线文字、中心线；最后存 dft + pdf 给人看。
// 用法：DrawProbe.exe <输出目录> <模板.dft> <零件1> [零件2 ...]
class DrawProbe {
    static F.Application app;
    static void L(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    static T Try<T>(Func<T> f, T d){ try { return f(); } catch { return d; } }
    static string E(Exception e){ return e.GetType().Name + " 0x" + e.HResult.ToString("X8") + " " + e.Message; }
    static string Mm(double m){ return (m * 1000).ToString("0.###"); }
    static void Idle(){ for (int k = 0; k < 10; k++) { try { app.DoIdle(); } catch { } Thread.Sleep(20); } }

    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = Encoding.UTF8;
        string outDir = args[0], tpl = args[1];
        app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application");
        try { app.DisplayAlerts = false; } catch { }
        L("CAD " + app.Version);
        for (int i = 2; i < args.Length; i++) {
            try { One(outDir, tpl, args[i]); } catch (Exception e) { L("FATAL " + Path.GetFileName(args[i]) + " " + E(e)); }
        }
        L("RESULT probe done");
        return 0;
    }

    static void One(string outDir, string tpl, string part){
        string name = Path.GetFileNameWithoutExtension(part);
        L(""); L("==== " + name);
        var dft = (D.DraftDocument)app.Documents.Add("SolidEdge.DraftDocument", tpl);
        Idle();
        D.Sheet sheet = dft.ActiveSheet;
        L("模板图纸 " + sheet.Name + " " + Mm(sheet.SheetSetup.SheetWidth) + "x" + Mm(sheet.SheetSetup.SheetHeight) + " 背景=" + Try(() => sheet.Background.Name, "?") + " 工作页数=" + dft.Sections.WorkingSection.Sheets.Count);
        L("模板里已有：视图 " + sheet.DrawingViews.Count + " 文本框 " + Try(() => ((dynamic)sheet.TextBoxes).Count, -1) + " 尺寸 " + Try(() => ((S.Dimensions)sheet.Dimensions).Count, -1));
        var link = dft.ModelLinks.Add(part);
        double x0, y0, z0, x1, y1, z1; link.Range3d(out x0, out y0, out z0, out x1, out y1, out z1);
        L("模型范围 mm: X " + Mm(x0) + "~" + Mm(x1) + " Y " + Mm(y0) + "~" + Mm(y1) + " Z " + Mm(z0) + "~" + Mm(z1));
        foreach (var o in new[]{ D.ViewOrientationConstants.igFrontView, D.ViewOrientationConstants.igTopView, D.ViewOrientationConstants.igRightView }) {
            try { double a, b, c, d; link.Range2d(o, out a, out b, out c, out d); L("  Range2d " + o + " : " + Mm(c - a) + " x " + Mm(d - b)); } catch (Exception e) { L("  Range2d " + o + " 失败 " + E(e)); }
        }
        double scale = 1.0;
        D.DrawingView front = null;
        try { front = sheet.DrawingViews.AddPartView(link, D.ViewOrientationConstants.igFrontView, scale, 0.12, 0.17, D.PartDrawingViewTypeConstants.sePartDesignedView); Idle(); L("主视图 OK"); }
        catch (Exception e) { L("AddPartView 失败 " + E(e)); }
        if (front == null) return;
        Dump(front, "主视图");
        D.DrawingView right = null, top = null, iso = null;
        try { double a, b, c, d; front.Range(out a, out b, out c, out d); right = sheet.DrawingViews.AddByFold(front, D.FoldTypeConstants.igFoldRight, c + 0.06, (b + d) / 2); Idle(); L("右视图（折叠）OK"); Dump(right, "右视图"); } catch (Exception e) { L("AddByFold right 失败 " + E(e)); }
        try { double a, b, c, d; front.Range(out a, out b, out c, out d); top = sheet.DrawingViews.AddByFold(front, D.FoldTypeConstants.igFoldUp, (a + c) / 2, d + 0.05); Idle(); L("俯视图（折叠）OK"); Dump(top, "俯视图"); } catch (Exception e) { L("AddByFold up 失败 " + E(e)); }
        try { iso = sheet.DrawingViews.AddPartView(link, D.ViewOrientationConstants.igTopFrontRightView, scale * 0.8, 0.31, 0.11, D.PartDrawingViewTypeConstants.sePartDesignedView); Idle(); L("轴测图 OK"); } catch (Exception e) { L("轴测图 失败 " + E(e)); }

        // ---- 尺寸 ----
        var dims = (S.Dimensions)sheet.Dimensions;
        var lines = new List<D.DVLine2d>(); foreach (D.DVLine2d l in front.DVLines2d) lines.Add(l);
        var circles = new List<D.DVCircle2d>(); foreach (D.DVCircle2d c in front.DVCircles2d) circles.Add(c);
        var arcs = new List<D.DVArc2d>(); foreach (D.DVArc2d a in front.DVArcs2d) arcs.Add(a);
        // 竖线 / 横线（视图坐标）
        Func<D.DVLine2d, double[]> P = l => { double a, b, c, d; l.GetStartPoint(out a, out b); l.GetEndPoint(out c, out d); return new[]{ a, b, c, d }; };
        var vert = lines.Where(l => { var p = P(l); return Math.Abs(p[0] - p[2]) < 1e-7 && Math.Abs(p[1] - p[3]) > 1e-6; }).OrderBy(l => P(l)[0]).ToList();
        var horz = lines.Where(l => { var p = P(l); return Math.Abs(p[1] - p[3]) < 1e-7 && Math.Abs(p[0] - p[2]) > 1e-6; }).OrderBy(l => P(l)[1]).ToList();
        L("竖线 " + vert.Count + " 横线 " + horz.Count + " 圆 " + circles.Count + " 弧 " + arcs.Count);
        if (horz.Count > 0) Try1("AddLength(最下横线)", () => { var d = dims.AddLength(horz[0]); return D1(d); });
        if (vert.Count >= 2) Try1("AddDistanceBetweenObjects(最左竖线, 最右竖线)", () => {
            var a = vert[0]; var b = vert[vert.Count - 1]; var pa = P(a); var pb = P(b);
            double sx, sy, tx, ty; front.ViewToSheet(pa[0], pa[1], out sx, out sy); front.ViewToSheet(pb[0], pb[1], out tx, out ty);
            var d = dims.AddDistanceBetweenObjects(a, sx, sy, 0, false, b, tx, ty, 0, false); return D1(d); });
        if (circles.Count > 0) Try1("AddCircularDiameter(圆1)", () => { var d = dims.AddCircularDiameter(circles[0]); try { d.PrefixString = circles.Count + "×"; } catch (Exception e) { L("   写前缀失败 " + E(e)); } return D1(d) + " 前缀显示=" + Try(() => d.PrefixDisplayedText, "?"); });
        if (circles.Count > 0) Try1("AddRadialDiameter(圆1)", () => D1(dims.AddRadialDiameter(circles[0])));
        if (arcs.Count > 0) Try1("AddRadius(弧1)", () => D1(dims.AddRadius(arcs[0])));
        // 坐标尺寸：原点在最左竖线，再标一个圆心
        if (vert.Count > 0 && circles.Count > 0) {
            S.Dimension origin = null;
            Try1("AddCoordinateOrigin(最左竖线)", () => { var p = P(vert[0]); double sx, sy; front.ViewToSheet(p[0], p[1], out sx, out sy); origin = dims.AddCoordinateOrigin(vert[0], sx, sy, 0, false); return D1(origin); });
            if (origin != null) {
                Try1("AddCoordinate(原点竖线 → 圆1 圆心 keypoint)", () => { var p = P(vert[0]); double cx, cy; circles[0].GetCenterPoint(out cx, out cy); double sx, sy, ux, uy; front.ViewToSheet(p[0], p[1], out sx, out sy); front.ViewToSheet(cx, cy, out ux, out uy); var d = dims.AddCoordinate(vert[0], sx, sy, 0, false, circles[0], ux, uy, 0, true); return D1(d); });
                Try1("AddCoordinateEx(原点尺寸 → 圆1 圆心)", () => { double cx, cy; circles[0].GetCenterPoint(out cx, out cy); double ux, uy; front.ViewToSheet(cx, cy, out ux, out uy); var d = dims.AddCoordinateEx(origin, circles[0], ux, uy, 0, true, ux, uy + 0.02, 0); return D1(d); });
            }
        }
        if (horz.Count > 0 && circles.Count > 0) Try1("AddDistanceBetweenObjects(最下横线, 圆1 圆心)", () => {
            var p = P(horz[0]); double cx, cy; circles[0].GetCenterPoint(out cx, out cy);
            double sx, sy, ux, uy; front.ViewToSheet(p[0], p[1], out sx, out sy); front.ViewToSheet(cx, cy, out ux, out uy);
            var d = dims.AddDistanceBetweenObjects(horz[0], sx, sy, 0, false, circles[0], ux, uy, 0, true); return D1(d); });
        // 引线文字（刻字）
        Try1("Balloons.Add 引线文字", () => { var bs = (S.Balloons)sheet.Balloons; var b = bs.Add(0.25, 0.08, 0); b.Callout = 1; b.BalloonText = "刻字：" + name; b.Leader = true; return "OK text=" + Try(() => b.BalloonDisplayedText, "?"); });
        Try1("TextBoxes.Add 文本", () => { var tb = ((S.TextBoxes)sheet.TextBoxes).Add(0.02, 0.06, 0); tb.Text = "探针文字"; return "OK"; });
        // 中心线 / 中心标记
        Try1("SetAutoGenerateOption_TG + RetrieveCenterLinesCenterMarks", () => { front.SetAutoGenerateOption_TG(true, true, false); ((dynamic)front).RetrieveCenterLinesCenterMarks(true); Idle(); return "CenterLines=" + Try(() => ((dynamic)sheet.CenterLines).Count, -1) + " CenterMarks=" + Try(() => ((dynamic)sheet.CenterMarks).Count, -1); });
        Try1("RetrieveDimensions", () => { int before = dims.Count; front.RetrieveDimensions(true); Idle(); return "dims " + before + " -> " + dims.Count; });
        L("尺寸总数 " + dims.Count);
        foreach (S.Dimension d in dims) L("   " + D1(d));
        string dftOut = Path.Combine(outDir, name + ".dft");
        try { dft.SaveAs(dftOut); L("存 " + dftOut); } catch (Exception e) { L("SaveAs 失败 " + E(e)); }
        try { object old = null; app.GetGlobalParameter(F.ApplicationGlobalConstants.seApplicationGlobalDraftSaveAsPDFSheetOptions, ref old); app.SetGlobalParameter(F.ApplicationGlobalConstants.seApplicationGlobalDraftSaveAsPDFSheetOptions, 1); dft.SaveCopyAs(Path.Combine(outDir, name + ".pdf")); app.SetGlobalParameter(F.ApplicationGlobalConstants.seApplicationGlobalDraftSaveAsPDFSheetOptions, old); L("存 pdf"); } catch (Exception e) { L("pdf 失败 " + E(e)); }
        try { dft.Close(false); } catch { }
    }

    static void Try1(string what, Func<string> f){ try { L("  " + what + " -> " + f()); } catch (Exception e) { L("  " + what + " 失败 " + E(e)); } }
    static string D1(S.Dimension d){
        string r = Try(() => d.DimensionType.ToString(), "?") + " 值=" + Try(() => Mm(d.Value), "?");
        try { double a, b, c, e; d.Range(out a, out b, out c, out e); r += " 范围=(" + Mm(a) + "," + Mm(b) + ")-(" + Mm(c) + "," + Mm(e) + ")"; } catch { }
        r += " track=" + Try(() => Mm(d.TrackDistance), "?");
        return r;
    }
    static void Dump(D.DrawingView v, string tag){
        double a, b, c, d; v.Range(out a, out b, out c, out d);
        L(tag + "：比例 " + v.ScaleFactor + " 图纸范围 (" + Mm(a) + "," + Mm(b) + ")-(" + Mm(c) + "," + Mm(d) + ") 线 " + v.DVLines2d.Count + " 圆 " + v.DVCircles2d.Count + " 弧 " + v.DVArcs2d.Count + " 椭圆 " + v.DVEllipses2d.Count + " 样条 " + v.DVBSplineCurves2d.Count);
        try { double dx, dy, dz, ux, uy, uz; D.ViewOrientationConstants k; v.ViewOrientation(out dx, out dy, out dz, out ux, out uy, out uz, out k); L("   朝向 view=(" + dx + "," + dy + "," + dz + ") x=(" + ux + "," + uy + "," + uz + ") " + k); } catch (Exception e) { L("   朝向失败 " + E(e)); }
        try { double p, q; v.ModelToView(0, 0, 0, out p, out q); double r, s; v.ModelToView(0.01, 0, 0, out r, out s); L("   ModelToView 原点→(" + Mm(p) + "," + Mm(q) + ") X+10mm→(" + Mm(r) + "," + Mm(s) + ")"); } catch (Exception e) { L("   ModelToView 失败 " + E(e)); }
        try { double p, q; v.ViewToSheet(0, 0, out p, out q); L("   ViewToSheet(0,0)=(" + Mm(p) + "," + Mm(q) + ")"); } catch (Exception e) { L("   ViewToSheet 失败 " + E(e)); }
        int n = 0;
        var types = new Dictionary<string,int>();
        foreach (D.DVLine2d l in v.DVLines2d) { string t = Try(() => l.EdgeType.ToString(), "?"); types[t] = (types.ContainsKey(t) ? types[t] : 0) + 1; if (n++ < 6) { double p, q, r, s; l.GetStartPoint(out p, out q); l.GetEndPoint(out r, out s); L("   线 (" + Mm(p) + "," + Mm(q) + ")-(" + Mm(r) + "," + Mm(s) + ") " + t); } }
        L("   线类型 " + string.Join(" ", types.Select(kv => kv.Key + "=" + kv.Value)));
        n = 0; foreach (D.DVCircle2d c2 in v.DVCircles2d) { if (n++ >= 6) break; double p, q; c2.GetCenterPoint(out p, out q); L("   圆 心(" + Mm(p) + "," + Mm(q) + ") R" + Mm(c2.Radius) + " " + Try(() => c2.EdgeType.ToString(), "?")); }
        // 一条线 → 三维边：引用键 / 面ID
        foreach (D.DVLine2d l in v.DVLines2d) { try { int id; bool isEdge; v.GetEdgeOrFaceID(l, out id, out isEdge); L("   GetEdgeOrFaceID 第1条线 → id=" + id + " isEdge=" + isEdge); } catch (Exception e) { L("   GetEdgeOrFaceID 失败 " + E(e)); } break; }
        foreach (D.DVLine2d l in v.DVLines2d) { try { var mm = l.ModelMember; L("   ModelMember=" + (mm == null ? "null" : Try(() => mm.FileName, "?"))); } catch (Exception e) { L("   ModelMember 失败 " + E(e)); } break; }
    }
}
