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

// 一键出图探针 3（2026-11-03）。探针 2 量出：建尺寸 / 气泡时传的 x,y 是**图纸坐标**（传视图坐标会挂到图纸左下角）。这里全部用 ViewToSheet 换过的坐标再量一遍：
//   P1 坐标尺寸横竖两串 + 文字挪位（折线）；P2 两线距离 + SetOrientation + TrackDistance；P3 半径的几种办法；P4 孔径引线式；
//   P5 刻字气泡挂到轴测图；P6 存盘；P7 主视图转 90° 再折；P8 装配明细表 + 自动气泡
// 用法：DrawProbe3.exe <输出目录> <模板.dft> <带孔块.par> <圆角板.par> <装配.asm>
class DrawProbe3 {
    static F.Application app;
    static void L(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    static T Try<T>(Func<T> f, T d){ try { var v = f(); return v == null ? d : v; } catch { return d; } }
    static string E(Exception e){ return e.GetType().Name + " 0x" + e.HResult.ToString("X8") + " " + e.Message; }
    static string Mm(double m){ return (m * 1000).ToString("0.###"); }
    static void Idle(){ for (int k = 0; k < 10; k++) { try { app.DoIdle(); } catch { } Thread.Sleep(20); } }
    static void T(string what, Action a){ try { a(); } catch (Exception e) { L("  " + what + " 失败 " + E(e)); } }
    static string R(object o){ try { double a, b, c, d; ((dynamic)o).Range(out a, out b, out c, out d); return "(" + Mm(a) + "," + Mm(b) + ")-(" + Mm(c) + "," + Mm(d) + ")"; } catch (Exception e) { return "range? " + e.Message; } }
    static string Dm(S.Dimension d){ return Try(() => d.DimensionType.ToString().Replace("igDimType", ""), "?") + " 值=" + Try(() => Mm(d.Value), "?") + " 范围=" + R(d) + " 前缀=" + Try(() => d.PrefixDisplayedText, ""); }
    static double[] LP(D.DVLine2d l){ double a, b, c, d; l.GetStartPoint(out a, out b); l.GetEndPoint(out c, out d); return new[]{ a, b, c, d }; }
    static double[] S2(D.DrawingView v, double x, double y){ double a, b; v.ViewToSheet(x, y, out a, out b); return new[]{ a, b }; }
    static string outDir;

    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = Encoding.UTF8;
        outDir = args[0]; string tpl = args[1];
        app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application");
        try { app.DisplayAlerts = false; } catch { }
        try { Block(tpl, args[2]); } catch (Exception e) { L("FATAL block " + E(e)); }
        try { Plate(tpl, args[3]); } catch (Exception e) { L("FATAL plate " + E(e)); }
        try { Asm(tpl, args[4]); } catch (Exception e) { L("FATAL asm " + E(e)); }
        L("RESULT probe3 done");
        return 0;
    }
    static void Save(D.DraftDocument dft, string name){
        try { dft.SaveAs(Path.Combine(outDir, name + ".dft")); L("  P6 SaveAs OK"); } catch (Exception e) { L("  P6 SaveAs 失败 " + E(e)); }
        try { object old = null; app.GetGlobalParameter(F.ApplicationGlobalConstants.seApplicationGlobalDraftSaveAsPDFSheetOptions, ref old); app.SetGlobalParameter(F.ApplicationGlobalConstants.seApplicationGlobalDraftSaveAsPDFSheetOptions, 1); dft.SaveCopyAs(Path.Combine(outDir, name + ".pdf")); app.SetGlobalParameter(F.ApplicationGlobalConstants.seApplicationGlobalDraftSaveAsPDFSheetOptions, old); } catch (Exception e) { L("  pdf 失败 " + E(e)); }
        try { dft.Close(false); } catch { }
    }

    static void Block(string tpl, string part){
        L("==== 带孔块 " + Path.GetFileName(part));
        var dft = (D.DraftDocument)app.Documents.Add("SolidEdge.DraftDocument", tpl); Idle();
        D.Sheet sheet = dft.ActiveSheet;
        var link = dft.ModelLinks.Add(part);
        var top = sheet.DrawingViews.AddPartView(link, D.ViewOrientationConstants.igTopView, 2.0, 0.10, 0.19, D.PartDrawingViewTypeConstants.sePartDesignedView); Idle();
        L("  俯视图 " + R(top));
        var dims = (S.Dimensions)sheet.Dimensions;
        var cs = new List<D.DVCircle2d>(); foreach (D.DVCircle2d c in top.DVCircles2d) cs.Add(c);
        var ls = new List<D.DVLine2d>(); foreach (D.DVLine2d l in top.DVLines2d) ls.Add(l);
        var vert = ls.Where(l => { var p = LP(l); return Math.Abs(p[0] - p[2]) < 1e-7; }).OrderBy(l => LP(l)[0]).ToList();
        var horz = ls.Where(l => { var p = LP(l); return Math.Abs(p[1] - p[3]) < 1e-7; }).OrderBy(l => LP(l)[1]).ToList();
        var small = cs.Where(c => c.Radius < 0.005).OrderBy(c => { double x, y; c.GetCenterPoint(out x, out y); return x; }).ToList();
        double vx0, vy0, vx1, vy1; top.Range(out vx0, out vy0, out vx1, out vy1);
        // P1 横向坐标串：文字线在视图下方 12mm（图纸）
        T("P1 横向", () => {
            var p = LP(vert[0]); var a = S2(top, p[0], (p[1] + p[3]) / 2);
            double yl = vy0 - 0.012;
            var o = dims.AddCoordinateOriginEx(vert[0], a[0], a[1], 0, false, a[0], yl, 0); L("  P1 原点 " + Dm(o));
            int k = 0;
            foreach (var c in small) { double x, y; c.GetCenterPoint(out x, out y); var s = S2(top, x, y); var d = dims.AddCoordinateEx(o, c, s[0], s[1], 0, true, s[0], yl, 0); L("  P1 孔 " + Dm(d));
                if (k++ == 0) T("P1 文字挪 6mm(SetKeyPoint 3/4)", () => {
                    for (int i = 0; i < d.KeyPointCount; i++) { double kx, ky, kz; F.KeyPointType kt; int h; d.GetKeyPoint(i, out kx, out ky, out kz, out kt, out h); L("     kp" + i + " (" + Mm(kx) + "," + Mm(ky) + ") h=" + h); }
                    double tx, ty, tz; F.KeyPointType tk; int th; d.GetKeyPoint(4, out tx, out ty, out tz, out tk, out th);
                    d.SetKeyPoint(4, tx + 0.006, ty, 0); L("  P1 kp4+6 → " + Dm(d) + " Jogged=" + Try(() => d.Jogged, false));
                }); }
            var pr = LP(vert[vert.Count - 1]); var b = S2(top, pr[0], (pr[1] + pr[3]) / 2); var dr = dims.AddCoordinateEx(o, vert[vert.Count - 1], b[0], b[1], 0, false, b[0], yl, 0); L("  P1 右边 " + Dm(dr));
        });
        T("P1 竖向", () => {
            var p = LP(horz[0]); var a = S2(top, (p[0] + p[2]) / 2, p[1]);
            double xl = vx0 - 0.012;
            var o = dims.AddCoordinateOriginEx(horz[0], a[0], a[1], 0, false, xl, a[1], 0); L("  P1 竖原点 " + Dm(o));
            foreach (var c in small.Take(2)) { double x, y; c.GetCenterPoint(out x, out y); var s = S2(top, x, y); var d = dims.AddCoordinateEx(o, c, s[0], s[1], 0, true, xl, s[1], 0); L("  P1 竖孔 " + Dm(d)); }
            var pt = LP(horz[horz.Count - 1]); var b = S2(top, (pt[0] + pt[2]) / 2, pt[1]); var dt = dims.AddCoordinateEx(o, horz[horz.Count - 1], b[0], b[1], 0, false, xl, b[1], 0); L("  P1 上边 " + Dm(dt));
        });
        // P2 两条竖线的距离，放在视图上方
        T("P2 总长", () => {
            var p = LP(vert[0]); var q = LP(vert[vert.Count - 1]);
            var a = S2(top, p[0], Math.Max(p[1], p[3])); var b = S2(top, q[0], Math.Max(q[1], q[3]));
            var d = dims.AddDistanceBetweenObjects(vert[0], a[0], a[1], 0, false, vert[vert.Count - 1], b[0], b[1], 0, false); L("  P2 建 " + Dm(d));
            d.SetOrientation(S.DimensionOrientationConstants.igOrientationHorizontal); L("  P2 水平 " + Dm(d) + " track=" + Mm(d.TrackDistance));
            d.TrackDistance = 0.010; L("  P2 track=10 " + Dm(d));
        });
        // P4 孔径：直径（默认）/ 引线式直径
        T("P4 引线直径", () => { var d = dims.AddRadialDiameter(small[small.Count - 1]); d.PrefixString = "4×"; d.SuffixString = " 贯通"; L("  P4 RadialDiameter " + Dm(d)); });
        T("P4 圆直径", () => { var d = dims.AddCircularDiameter(small[0]); d.PrefixString = "4×"; L("  P4 CircularDiameter " + Dm(d)); });
        // P5 刻字：模板里的气泡挂到轴测图
        var iso = sheet.DrawingViews.AddPartView(link, D.ViewOrientationConstants.igTopFrontRightView, 1.0, 0.33, 0.13, D.PartDrawingViewTypeConstants.sePartDesignedView); Idle();
        T("P5 刻字", () => {
            var bs = (S.Balloons)sheet.Balloons; S.Balloon k = null; for (int i = 1; i <= bs.Count; i++) if (Try(() => bs.Item(i).BalloonText, "").Contains("刻字")) { k = bs.Item(i); break; }
            L("  P5 模板刻字 " + (k == null ? "没有" : Try(() => k.BalloonDisplayedText, "") + " " + R(k)));
            D.DVLine2d l = null; double best = -1; foreach (D.DVLine2d x in iso.DVLines2d) { var pp = LP(x); double len = Math.Sqrt((pp[2] - pp[0]) * (pp[2] - pp[0]) + (pp[3] - pp[1]) * (pp[3] - pp[1])); if (len > best) { best = len; l = x; } }
            var p = LP(l); var s = S2(iso, (p[0] + p[2]) / 2, (p[1] + p[3]) / 2);
            if (k != null) {
                T("P5 SetTerminator(图纸坐标)", () => { k.SetTerminator(l, s[0], s[1], 0, false); L("  P5 挂上=" + k.IsTerminatorAttachedToEntity + " " + R(k)); });
                if (!Try(() => k.IsTerminatorAttachedToEntity, false)) T("P5 TGReattach", () => L("  P5 TGReattach → " + k.TGReattachToDrawingView(iso) + " 挂上=" + k.IsTerminatorAttachedToEntity + " " + R(k)));
            }
            T("P5 新建刻字", () => { var b = bs.AddByTerminator(l, s[0], s[1], 0, false); b.Callout = 1; b.BalloonText = "刻字：%{文件名（无扩展名）|R1}"; L("  P5 新气泡 显示=" + Try(() => b.BalloonDisplayedText, "") + " " + R(b));
                for (int i = 0; i < b.KeyPointCount; i++) { double kx, ky, kz; F.KeyPointType kt; int h; b.GetKeyPoint(i, out kx, out ky, out kz, out kt, out h); L("     kp" + i + " (" + Mm(kx) + "," + Mm(ky) + ") h=" + h); }
                T("P5 挪文字 SetKeyPoint 末点", () => { int n = b.KeyPointCount; double kx, ky, kz; F.KeyPointType kt; int h; b.GetKeyPoint(n - 1, out kx, out ky, out kz, out kt, out h); b.SetKeyPoint(n - 1, 0.25, 0.06, 0); L("  P5 挪后 " + R(b)); });
            });
        });
        // P7 视图转 90° 再折
        T("P7 转 90", () => {
            var v = sheet.DrawingViews.AddPartView(link, D.ViewOrientationConstants.igTopView, 1.0, 0.25, 0.22, D.PartDrawingViewTypeConstants.sePartDesignedView); Idle();
            v.SetRotationAngle(Math.PI / 2); Idle(); v.Update(); Idle();
            double ang; v.GetRotationAngle(out ang); L("  P7 转后 角=" + ang + " " + R(v));
            var dn = sheet.DrawingViews.AddByFold(v, D.FoldTypeConstants.igFoldDown, 0.25, 0.17); Idle();
            double dx, dy, dz, ux, uy, uz; D.ViewOrientationConstants kk; dn.ViewOrientation(out dx, out dy, out dz, out ux, out uy, out uz, out kk); double a2; dn.GetRotationAngle(out a2);
            L("  P7 下折 → " + kk + " 视线=(" + dx + "," + dy + "," + dz + ") x=(" + ux + "," + uy + "," + uz + ") 角=" + a2 + " " + R(dn));
        });
        T("P7 自定义朝向", () => {
            var v = sheet.DrawingViews.AddPartView(link, D.ViewOrientationConstants.igTopView, 1.0, 0.36, 0.22, D.PartDrawingViewTypeConstants.sePartDesignedView); Idle();
            v.SetViewOrientation(0, 0, 1, 0, 1, 0); Idle(); v.Update(); Idle();
            double dx, dy, dz, ux, uy, uz; D.ViewOrientationConstants kk; v.ViewOrientation(out dx, out dy, out dz, out ux, out uy, out uz, out kk);
            L("  P7 SetViewOrientation(视线 0,0,1 x 0,1,0) → " + kk + " 视线=(" + dx + "," + dy + "," + dz + ") x=(" + ux + "," + uy + "," + uz + ") " + R(v));
            var dn = sheet.DrawingViews.AddByFold(v, D.FoldTypeConstants.igFoldRight, 0.40, 0.22); Idle();
            dn.ViewOrientation(out dx, out dy, out dz, out ux, out uy, out uz, out kk); L("  P7 自定义后右折 → " + kk + " 视线=(" + dx + "," + dy + "," + dz + ") x=(" + ux + "," + uy + "," + uz + ")");
        });
        T("P9 样式", () => { var st = dims.Style; L("  P9 尺寸样式 字高=" + Mm(Try(() => (double)((dynamic)st).TextHeight, 0.0)) + " 名=" + Try(() => (string)((dynamic)st).Name, "")); });
        T("P9 视图隐藏线", () => { L("  P9 HiddenEdges? " + Try(() => ((dynamic)top).DisplayHiddenEdges, (object)"?") + " Defaults_ShowHidden? " + Try(() => ((dynamic)top).Defaults_ShowHiddenEdges, (object)"?")); });
        try { double p, m; sheet.SheetSetup.GetScale(out p, out m); L("  图纸比例 " + p + ":" + m); sheet.SheetSetup.SetScale(2, 1); } catch { }
        L("  尺寸数 " + dims.Count);
        Save(dft, "probe3-block");
    }

    static void Plate(string tpl, string part){
        L("==== 圆角板 " + Path.GetFileName(part));
        var dft = (D.DraftDocument)app.Documents.Add("SolidEdge.DraftDocument", tpl); Idle();
        D.Sheet sheet = dft.ActiveSheet;
        var link = dft.ModelLinks.Add(part);
        var top = sheet.DrawingViews.AddPartView(link, D.ViewOrientationConstants.igTopView, 4.0, 0.12, 0.17, D.PartDrawingViewTypeConstants.sePartDesignedView); Idle();
        var dims = (S.Dimensions)sheet.Dimensions;
        D.DVArc2d arc = null; foreach (D.DVArc2d a in top.DVArcs2d) { arc = a; break; }
        double cx, cy, sx, sy, ex, ey; arc.GetCenterPoint(out cx, out cy); arc.GetStartPoint(out sx, out sy); arc.GetEndPoint(out ex, out ey);
        double mid = arc.StartAngle + arc.SweepAngle / 2; double mx = cx + arc.Radius * Math.Cos(mid), my = cy + arc.Radius * Math.Sin(mid);
        var ms = S2(top, mx, my);
        T("P3 AddRadius", () => L("  P3 AddRadius → " + Dm(dims.AddRadius(arc))));
        T("P3 AddRadialDiameter(弧)", () => L("  P3 AddRadialDiameter(弧) → " + Dm(dims.AddRadialDiameter(arc))));
        T("P3 AddCircularDiameter(弧)", () => L("  P3 AddCircularDiameter(弧) → " + Dm(dims.AddCircularDiameter(arc))));
        T("P3 Reference", () => { F.Reference rf; top.GetReferenceToGraphicMember(arc, out rf); L("  P3 引用 " + (rf == null ? "null" : "有")); L("  P3 AddRadius(引用) → " + Dm(dims.AddRadius(rf))); });
        T("P3 DimInitData", () => { var init = dims.DimInitData; L("  P3 DimInitData " + (init == null ? "null" : init.GetType().Name)); });
        T("P3 圆角引线文字", () => { var bs = (S.Balloons)sheet.Balloons; var b = bs.AddByTerminator(arc, ms[0], ms[1], 0, false); b.Callout = 1; b.BalloonText = "4-R5"; L("  P3 引线 R → " + Try(() => b.BalloonDisplayedText, "") + " " + R(b)); });
        T("P3 角度", () => { D.DVLine2d l1 = null; foreach (D.DVLine2d l in top.DVLines2d) { l1 = l; break; } L("  P3 AddLength 线 → " + Dm(dims.AddLength(l1))); });
        Save(dft, "probe3-plate");
    }

    static void Asm(string tpl, string asm){
        L("==== 装配 " + Path.GetFileName(asm));
        var dft = (D.DraftDocument)app.Documents.Add("SolidEdge.DraftDocument", tpl); Idle();
        D.Sheet sheet = dft.ActiveSheet;
        var link = dft.ModelLinks.Add(asm);
        double x0, y0, z0, x1, y1, z1; link.Range3d(out x0, out y0, out z0, out x1, out y1, out z1); L("  范围 " + Mm(x1 - x0) + "×" + Mm(y1 - y0) + "×" + Mm(z1 - z0));
        D.DrawingView v = null;
        T("P8 装配视图", () => { v = sheet.DrawingViews.AddAssemblyView(link, D.ViewOrientationConstants.igTopFrontRightView, 0.5, 0.15, 0.17, D.AssemblyDrawingViewTypeConstants.seAssemblyDesignedView); Idle(); L("  P8 视图 " + R(v)); });
        if (v == null) { Save(dft, "probe3-asm"); return; }
        foreach (string cfg in new[] { "SK-BOM", "" }) {
            string c = cfg;
            T("P8 明细表 " + c, () => {
                var pl = dft.PartsLists.Add(v, c, 1, 1); Idle();
                L("  P8 明细表(" + c + ") 行=" + Try(() => pl.Rows.Count, -1) + " 列=" + Try(() => pl.Columns.Count, -1) + " 气泡=" + Try(() => ((S.Balloons)sheet.Balloons).Count, -1) + " 范围=" + R(pl));
                try { var h = new List<string>(); foreach (D.TableColumn col in pl.Columns) h.Add(Try(() => col.HeaderRowValue, "?")); L("  P8 表头 " + string.Join("|", h)); } catch (Exception e) { L("  P8 表头读不到 " + E(e)); }
                try { for (int r = 1; r <= Math.Min(3, pl.Rows.Count); r++) { var cells = new List<string>(); for (int k = 1; k <= pl.Columns.Count; k++) cells.Add(Try(() => (string)((dynamic)pl).Cell(r, k).value, "")); L("  P8 行" + r + " " + string.Join("|", cells)); } } catch (Exception e) { L("  P8 单元格读不到 " + E(e)); }
            });
            if (dft.PartsLists.Count > 0) break;
        }
        Save(dft, "probe3-asm");
    }
}
