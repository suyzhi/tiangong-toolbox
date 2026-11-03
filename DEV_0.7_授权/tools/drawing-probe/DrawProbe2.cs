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

// 一键出图探针 2（2026-11-03）：尺寸怎么摆。
//   E1 两圆心距离：默认量斜距还是水平？MeasurementAxisEx 各值；E2 TrackDistance；E3 坐标尺寸（原点 + 一串）怎么定方向、文字落在哪；
//   E4 尺寸关键点；E5 直径 + 前后缀 + 文字挪位；E6 圆角半径；E7 模板里现成的气泡（刻字）改挂到轴测图；E8 图纸比例；E9 折叠方向
// 用法：DrawProbe2.exe <输出目录> <模板.dft> <零件(带孔块)> <零件(圆角板)>
class DrawProbe2 {
    static F.Application app;
    static void L(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    static T Try<T>(Func<T> f, T d){ try { var v = f(); return v == null ? d : v; } catch { return d; } }
    static string E(Exception e){ return e.GetType().Name + " 0x" + e.HResult.ToString("X8") + " " + e.Message; }
    static string Mm(double m){ return (m * 1000).ToString("0.###"); }
    static void Idle(){ for (int k = 0; k < 10; k++) { try { app.DoIdle(); } catch { } Thread.Sleep(20); } }
    static void T(string what, Action a){ try { a(); } catch (Exception e) { L("  " + what + " 失败 " + E(e)); } }

    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = Encoding.UTF8;
        string outDir = args[0], tpl = args[1];
        app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application");
        try { app.DisplayAlerts = false; } catch { }
        try { Block(outDir, tpl, args[2]); } catch (Exception e) { L("FATAL block " + E(e)); }
        try { Plate(outDir, tpl, args[3]); } catch (Exception e) { L("FATAL plate " + E(e)); }
        L("RESULT probe2 done");
        return 0;
    }

    static string R(object o){
        try { double a, b, c, d; ((dynamic)o).Range(out a, out b, out c, out d); return "(" + Mm(a) + "," + Mm(b) + ")-(" + Mm(c) + "," + Mm(d) + ")"; } catch (Exception e) { return "range? " + e.Message; }
    }
    static string Dm(S.Dimension d){
        string r = Try(() => d.DimensionType.ToString().Replace("igDimType", ""), "?") + " 值=" + Try(() => Mm(d.Value), "?") + " 轴=" + Try(() => d.MeasurementAxisEx.ToString(), "?") + " track=" + Try(() => Mm(d.TrackDistance), "?") + " 范围=" + R(d);
        try { int n = d.KeyPointCount; var ks = new List<string>(); for (int i = 0; i < n; i++) { double x, y, z; F.KeyPointType kt; int h; d.GetKeyPoint(i, out x, out y, out z, out kt, out h); ks.Add(i + ":(" + Mm(x) + "," + Mm(y) + ")" + kt.ToString().Replace("igKeyPoint", "") + "/" + h); } r += " 关键点 " + string.Join(" ", ks); } catch (Exception e) { r += " 关键点? " + e.Message; }
        return r;
    }

    static D.DraftDocument New(string tpl, out D.Sheet sheet){
        var dft = (D.DraftDocument)app.Documents.Add("SolidEdge.DraftDocument", tpl); Idle();
        sheet = dft.ActiveSheet; return dft;
    }
    static void Save(D.DraftDocument dft, string outDir, string name){
        try { dft.SaveAs(Path.Combine(outDir, name + ".dft")); } catch (Exception e) { L("SaveAs 失败 " + E(e)); }
        try { object old = null; app.GetGlobalParameter(F.ApplicationGlobalConstants.seApplicationGlobalDraftSaveAsPDFSheetOptions, ref old); app.SetGlobalParameter(F.ApplicationGlobalConstants.seApplicationGlobalDraftSaveAsPDFSheetOptions, 1); dft.SaveCopyAs(Path.Combine(outDir, name + ".pdf")); app.SetGlobalParameter(F.ApplicationGlobalConstants.seApplicationGlobalDraftSaveAsPDFSheetOptions, old); } catch (Exception e) { L("pdf 失败 " + E(e)); }
        try { dft.Close(false); } catch { }
    }
    static double[] LP(D.DVLine2d l){ double a, b, c, d; l.GetStartPoint(out a, out b); l.GetEndPoint(out c, out d); return new[]{ a, b, c, d }; }
    static double[] CP(D.DVCircle2d c){ double a, b; c.GetCenterPoint(out a, out b); return new[]{ a, b, c.Radius }; }

    static void Block(string outDir, string tpl, string part){
        L("==== 带孔块 " + Path.GetFileName(part));
        D.Sheet sheet; var dft = New(tpl, out sheet);
        T("E8 图纸比例", () => { double p, m; sheet.SheetSetup.GetScale(out p, out m); L("  E8 原比例 " + p + ":" + m + " 默认视图比例 " + sheet.SheetSetup.GetDefaultDrawingViewScaleFactor()); });
        // 模板里现成的东西
        T("E7 模板气泡", () => { var bs = (S.Balloons)sheet.Balloons; L("  E7 气泡 " + bs.Count); for (int i = 1; i <= bs.Count; i++) { var b = bs.Item(i); L("    气泡" + i + " callout=" + b.Callout + " text=" + Try(() => b.BalloonText, "") + " 显示=" + Try(() => b.BalloonDisplayedText, "") + " 引线=" + b.Leader + " 挂上=" + Try(() => b.IsTerminatorAttachedToEntity, false) + " 范围=" + R(b)); } });
        T("E7 模板引线", () => { var ls = (S.Leaders)sheet.Leaders; L("  E7 引线 " + ls.Count); });
        T("E7 模板文本", () => { dynamic ts = sheet.TextBoxes; L("  E7 文本框 " + ts.Count); });
        var link = dft.ModelLinks.Add(part);
        var top = sheet.DrawingViews.AddPartView(link, D.ViewOrientationConstants.igTopView, 2.0, 0.12, 0.19, D.PartDrawingViewTypeConstants.sePartDesignedView); Idle();
        L("  俯视图 范围 " + R(top) + " 线 " + top.DVLines2d.Count + " 圆 " + top.DVCircles2d.Count + " 弧 " + top.DVArcs2d.Count);
        T("E9 折叠", () => {
            var down = sheet.DrawingViews.AddByFold(top, D.FoldTypeConstants.igFoldDown, 0.12, 0.09); Idle();
            double dx, dy, dz, ux, uy, uz; D.ViewOrientationConstants k; down.ViewOrientation(out dx, out dy, out dz, out ux, out uy, out uz, out k); L("  E9 俯视图下折 → " + k + " 范围 " + R(down));
            var rt = sheet.DrawingViews.AddByFold(top, D.FoldTypeConstants.igFoldRight, 0.22, 0.19); Idle();
            rt.ViewOrientation(out dx, out dy, out dz, out ux, out uy, out uz, out k); L("  E9 俯视图右折 → " + k + " 范围 " + R(rt));
        });
        T("E8 改图纸比例", () => { sheet.SheetSetup.SetScale(2, 1); double p, m; sheet.SheetSetup.GetScale(out p, out m); L("  E8 改后 " + p + ":" + m); });
        var dims = (S.Dimensions)sheet.Dimensions;
        var cs = new List<D.DVCircle2d>(); foreach (D.DVCircle2d c in top.DVCircles2d) cs.Add(c);
        foreach (var c in cs) { var p = CP(c); L("  圆 (" + Mm(p[0]) + "," + Mm(p[1]) + ") R" + Mm(p[2]) + " " + Try(() => c.EdgeType.ToString(), "")); }
        var ls2 = new List<D.DVLine2d>(); foreach (D.DVLine2d l in top.DVLines2d) ls2.Add(l);
        var vert = ls2.Where(l => { var p = LP(l); return Math.Abs(p[0] - p[2]) < 1e-7; }).OrderBy(l => LP(l)[0]).ToList();
        var horz = ls2.Where(l => { var p = LP(l); return Math.Abs(p[1] - p[3]) < 1e-7; }).OrderBy(l => LP(l)[1]).ToList();
        var small = cs.Where(c => c.Radius < 0.005).OrderBy(c => CP(c)[0]).ThenBy(c => CP(c)[1]).ToList();
        L("  小圆 " + small.Count + " 竖线 " + vert.Count + " 横线 " + horz.Count);
        // E1 两个不同 x、不同 y 的圆心
        if (small.Count >= 2) {
            var a = small[0]; var b = small[small.Count - 1]; var pa = CP(a); var pb = CP(b);
            T("E1 圆心距", () => {
                var d = dims.AddDistanceBetweenObjects(a, pa[0], pa[1], 0, true, b, pb[0], pb[1], 0, true); L("  E1 默认 " + Dm(d));
                foreach (S.DimAxisModeConstants m in new[]{ S.DimAxisModeConstants.igDimAxisModeImplied, S.DimAxisModeConstants.igDimAxisModeExplicit, S.DimAxisModeConstants.igDimAxisModeCoordinate, S.DimAxisModeConstants.igDimAxisModeDefault })
                    T("E1 轴 " + m, () => { d.MeasurementAxisEx = m; L("  E1 设 " + m + " → " + Dm(d)); });
                T("E1 SetOrientation 水平", () => { d.SetOrientation(S.DimensionOrientationConstants.igOrientationHorizontal); L("  E1 水平 → " + Dm(d)); });
                T("E1 SetOrientation 竖直", () => { d.SetOrientation(S.DimensionOrientationConstants.igOrientationVertical); L("  E1 竖直 → " + Dm(d)); });
                T("E2 track", () => { d.TrackDistance = 0.012; L("  E2 track=12 → " + Dm(d)); d.TrackDistance = -0.012; L("  E2 track=-12 → " + Dm(d)); });
            });
            T("E1b 先设 Axis 再建", () => {
                dims.AxisMode = S.DimAxisModeConstants.igDimAxisModeImplied;
                var d = dims.AddDistanceBetweenObjects(a, pa[0], pa[1], 0, true, b, pb[0], pb[1], 0, true); L("  E1b Implied 建 → " + Dm(d));
                dims.AxisMode = S.DimAxisModeConstants.igDimAxisModeDefault;
            });
        }
        // E3 坐标尺寸：横向一串（原点 = 最左竖线），竖向一串（原点 = 最下横线）
        if (vert.Count > 0 && small.Count > 0) T("E3 横向坐标", () => {
            var pv = LP(vert[0]); double ymid = (pv[1] + pv[3]) / 2;
            var o = dims.AddCoordinateOriginEx(vert[0], pv[0], ymid, 0, false, pv[0], ymid + 0.04, 0); L("  E3 原点Ex " + Dm(o));
            foreach (var c in small) { var p = CP(c); var d = dims.AddCoordinateEx(o, c, p[0], p[1], 0, true, p[0], ymid + 0.04, 0); L("  E3 → " + Dm(d)); }
            var pr = LP(vert[vert.Count - 1]); var dr = dims.AddCoordinateEx(o, vert[vert.Count - 1], pr[0], (pr[1] + pr[3]) / 2, 0, false, pr[0], ymid + 0.04, 0); L("  E3 右边 → " + Dm(dr));
        });
        if (horz.Count > 0 && small.Count > 0) T("E3 竖向坐标", () => {
            var ph = LP(horz[0]); double xm = (ph[0] + ph[2]) / 2;
            var o = dims.AddCoordinateOriginEx(horz[0], xm, ph[1], 0, false, xm - 0.04, ph[1], 0); L("  E3 竖原点Ex " + Dm(o));
            foreach (var c in small) { var p = CP(c); var d = dims.AddCoordinateEx(o, c, p[0], p[1], 0, true, xm - 0.04, p[1], 0); L("  E3 竖 → " + Dm(d)); }
        });
        // E5 直径 + 前后缀
        if (small.Count > 0) T("E5 直径", () => {
            var d = dims.AddCircularDiameter(small[0]); L("  E5 默认 " + Dm(d));
            d.PrefixString = small.Count + "×"; d.SuffixString = " 贯通"; L("  E5 前后缀 显示=" + Try(() => d.PrefixDisplayedText, "") + "|" + Try(() => d.SuffixDisplayedText, "") + " " + Dm(d));
            T("E5 SetKeyPoint 文字挪", () => { double x, y, z; F.KeyPointType kt; int h; d.GetKeyPoint(d.KeyPointCount - 1, out x, out y, out z, out kt, out h); d.SetKeyPoint(d.KeyPointCount - 1, x + 0.015, y + 0.015, 0); L("  E5 挪后 " + Dm(d)); });
            T("E5 TextOffsets", () => { double x, y; d.GetTextOffsets(out x, out y); L("  E5 TextOffsets " + Mm(x) + "," + Mm(y)); d.SetTextOffsets(0.01, 0.01); L("  E5 设后 " + Dm(d)); });
            T("E5 Leader 属性", () => { L("  E5 Leader=" + d.Leader + " BreakPosition=" + d.BreakPosition + " Display=" + d.DisplayType); });
            T("E5 孔标注显示类型", () => { var d2 = dims.AddCircularDiameter(small[small.Count - 1]); d2.DisplayType = S.DimDispTypeConstants.igDimDisplayTypeHoleCallout; L("  E5 HoleCallout 显示=" + Try(() => d2.PrefixDisplayedText, "") + " / " + Dm(d2)); });
        });
        // E5b 气泡式孔标注（callout）挂在孔上
        if (small.Count > 0) T("E5b callout 挂孔", () => {
            var p = CP(small[0]); var bs = (S.Balloons)sheet.Balloons;
            var b = bs.AddByTerminator(small[0], p[0] + p[2], p[1], 0, false); b.Callout = 1; b.Leader = true; b.BalloonText = "4×%{HoleCallout}"; L("  E5b 显示=" + Try(() => b.BalloonDisplayedText, "") + " 范围=" + R(b));
            T("E5b 挪自由点", () => { b.SetFreeSpacePoint(p[0] + 0.02, p[1] + 0.02, 0); L("  E5b 挪后 范围=" + R(b)); });
        });
        T("E7 刻字挂轴测", () => {
            var iso = sheet.DrawingViews.AddPartView(link, D.ViewOrientationConstants.igTopFrontRightView, 1.0, 0.31, 0.11, D.PartDrawingViewTypeConstants.sePartDesignedView); Idle();
            var bs = (S.Balloons)sheet.Balloons; S.Balloon k = null; for (int i = 1; i <= bs.Count; i++) if (Try(() => bs.Item(i).BalloonDisplayedText, "").Contains("刻字")) { k = bs.Item(i); break; }
            if (k == null) { L("  E7 没找到刻字气泡"); return; }
            D.DVLine2d l = null; foreach (D.DVLine2d x in iso.DVLines2d) { l = x; break; }
            var p = LP(l); L("  E7 轴测线 (" + Mm(p[0]) + "," + Mm(p[1]) + ")");
            T("E7 SetTerminator(视图坐标)", () => { k.SetTerminator(l, (p[0] + p[2]) / 2, (p[1] + p[3]) / 2, 0, false); L("  E7 挂上=" + k.IsTerminatorAttachedToEntity + " 范围=" + R(k)); });
            T("E7 TGReattach", () => { L("  E7 TGReattachToDrawingView → " + k.TGReattachToDrawingView(iso)); });
        });
        L("  尺寸数 " + dims.Count);
        Save(dft, outDir, "probe2-block");
    }

    static void Plate(string outDir, string tpl, string part){
        L("==== 圆角板 " + Path.GetFileName(part));
        D.Sheet sheet; var dft = New(tpl, out sheet);
        var link = dft.ModelLinks.Add(part);
        var top = sheet.DrawingViews.AddPartView(link, D.ViewOrientationConstants.igTopView, 4.0, 0.12, 0.17, D.PartDrawingViewTypeConstants.sePartDesignedView); Idle();
        L("  俯视图 范围 " + R(top) + " 线 " + top.DVLines2d.Count + " 圆 " + top.DVCircles2d.Count + " 弧 " + top.DVArcs2d.Count);
        var dims = (S.Dimensions)sheet.Dimensions;
        int i = 0;
        foreach (D.DVArc2d a in top.DVArcs2d) {
            i++; var aa = a;
            T("E6 弧" + i, () => { double cx, cy; aa.GetCenterPoint(out cx, out cy); L("  E6 弧 心(" + Mm(cx) + "," + Mm(cy) + ") R" + Mm(aa.Radius) + " " + aa.EdgeType + " 扫角=" + Try(() => aa.SweepAngle, 0.0)); var d = dims.AddRadius(aa); L("  E6 AddRadius → " + Dm(d)); });
            if (i == 1) T("E6 前缀", () => { var d = dims.Item(dims.Count); d.PrefixString = "4-"; L("  E6 前缀后 " + Try(() => d.PrefixDisplayedText, "")); });
        }
        T("E6 圆弧 dynamic", () => { foreach (D.DVArc2d a in top.DVArcs2d) { object d = ((dynamic)dims).AddRadius(a); L("  E6 dynamic → " + Dm((S.Dimension)d)); break; } });
        Save(dft, outDir, "probe2-plate");
    }
}
