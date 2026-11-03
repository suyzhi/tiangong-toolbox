using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using D=SolidEdgeDraft;
using F=SolidEdgeFramework;
using S=SolidEdgeFrameworkSupport;

// 一键出图探针 7：公差和粗糙度。在 A4 模板上放一个零件俯视图，试：
//   直径尺寸加 H7（公差带）、坐标尺寸加 ±0.05、线性尺寸加上下偏差；
//   形位公差框（不带引线放在孔标注下面 / 带引线挂在圆上 / SetAlignmentShape 跟着尺寸）、基准（挂在边上）、粗糙度（挂在边上）。
// 每一步读回属性和 Range，存 dft + pdf 看样子。用法：DrawProbe7.exe <输出目录> <模板.dft> <零件副本>
class DrawProbe7 {
    static F.Application app;
    static void L(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    static string E(Exception e){ return e.GetType().Name + " 0x" + e.HResult.ToString("X8") + " " + e.Message; }
    static string Mm(double m){ return (m * 1000).ToString("0.###"); }
    static void Idle(){ for (int k = 0; k < 10; k++) { try { app.DoIdle(); } catch { } Thread.Sleep(20); } }
    static string Box(dynamic o){ try { double a, b, c, d; o.Range(out a, out b, out c, out d); return "(" + Mm(a) + "," + Mm(b) + ")-(" + Mm(c) + "," + Mm(d) + ")"; } catch (Exception e) { return "Range " + E(e); } }
    static T Try<T>(Func<T> f, T d){ try { var v = f(); return v == null ? d : v; } catch { return d; } }
    static string Tol(S.Dimension d){
        return "值 " + Mm(Try(() => d.Value, 0.0)) + " 显示 " + Try(() => d.DisplayType.ToString(), "?") + " 上 [" + Try(() => d.PrimaryUpperTolerance, "?") + "] 下 [" + Try(() => d.PrimaryLowerTolerance, "?") + "] 孔带 [" + Try(() => d.HoleClassString, "?") + "] 轴带 [" + Try(() => d.ShaftClassString, "?") + "] 符号 [" + Try(() => d.PrimaryToleranceSymbol, "?") + "] 标准 " + Try(() => d.UseStandardLimitAndFitTolerance, false) + " 框 " + Box(d);
    }
    static void Step(string name, Action a){ try { a(); } catch (Exception e) { L("  " + name + " 失败 " + E(e)); } }
    static object Ref(D.DrawingView v, object m){ return v.GetReferenceToGraphicMember2[m]; }

    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = Encoding.UTF8;
        app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application");
        try { app.DisplayAlerts = false; } catch { }
        var dft = (D.DraftDocument)app.Documents.Add("SolidEdge.DraftDocument", args[1]); Idle();
        var sheet = dft.ActiveSheet;
        var link = dft.ModelLinks.Add(args[2]);
        var v = sheet.DrawingViews.AddPartView(link, D.ViewOrientationConstants.igTopView, 1.0, 0.15, 0.15, D.PartDrawingViewTypeConstants.sePartDesignedView); Idle();
        L("视图 " + Box(v) + " 比例 " + v.ScaleFactor);
        var dims = (S.Dimensions)sheet.Dimensions;
        // 最小的圆（孔）、最长的水平线（底边）、最长的竖直线
        D.DVCircle2d hole = null; foreach (D.DVCircle2d c in v.DVCircles2d) if (hole == null || c.Radius < hole.Radius) hole = c;
        D.DVLine2d bottom = null, side = null; double lb = 0, ls = 0;
        foreach (D.DVLine2d l in v.DVLines2d) {
            double x1, y1, x2, y2; l.GetStartPoint(out x1, out y1); l.GetEndPoint(out x2, out y2);
            double len = Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1));
            if (Math.Abs(y1 - y2) < 1e-7 && (bottom == null || len > lb + 1e-7 || (Math.Abs(len - lb) < 1e-7 && y1 < Y(bottom)))) { bottom = l; lb = len; }
            if (Math.Abs(x1 - x2) < 1e-7 && (side == null || len > ls + 1e-7 || (Math.Abs(len - ls) < 1e-7 && x1 < X(side)))) { side = l; ls = len; }
        }
        L("孔 " + (hole == null ? "无" : "R" + Mm(hole.Radius)) + " 底边 " + Mm(lb) + " 侧边 " + Mm(ls));
        double hx = 0, hy = 0, hsx = 0, hsy = 0; if (hole != null) { hole.GetCenterPoint(out hx, out hy); v.ViewToSheet(hx, hy, out hsx, out hsy); }

        // A. 孔直径 + H7（ClassPlusMinus：人图里 Ø6 H7 读回 +0.012/0、std=true）
        S.Dimension dA = null;
        Step("A 直径", () => { dA = dims.AddRadialDiameter(Ref(v, hole)); L("A 建好 " + Tol(dA)); });
        Step("A1 std=true", () => { dA.UseStandardLimitAndFitTolerance = true; L("A1 " + Tol(dA)); });
        Step("A2 HoleClass=H7", () => { dA.HoleClassString = "H7"; L("A2 " + Tol(dA)); });
        Step("A3 Display=ClassPlusMinus", () => { dA.DisplayType = S.DimDispTypeConstants.igDimDisplayTypeClassPlusMinus; L("A3 " + Tol(dA)); });
        Step("A4 前缀 4×", () => { dA.PrefixString = "4×"; L("A4 " + Tol(dA) + " 显示文字 前[" + Try(() => dA.PrefixDisplayedText, "") + "]"); });
        Step("A5 挪文字", () => { int k = dA.KeyPointCount - 1; dA.SetKeyPoint(k, hsx + 0.03, hsy + 0.02, 0); L("A5 " + Box(dA)); });
        // A'：反过来先设显示类型再设公差带；再试 Classfit（只显示 H7）
        S.Dimension dA2 = null;
        Step("A' 直径2", () => { dA2 = dims.AddRadialDiameter(Ref(v, hole)); dA2.DisplayType = S.DimDispTypeConstants.igDimDisplayTypeClassPlusMinus; dA2.HoleClassString = "H7"; L("A' 先类型后带 " + Tol(dA2)); });
        Step("A' Classfit", () => { dA2.DisplayType = S.DimDispTypeConstants.igDimDisplayTypeClassfit; L("A' Classfit " + Tol(dA2)); });
        Step("A' 挪", () => { int k = dA2.KeyPointCount - 1; dA2.SetKeyPoint(k, hsx + 0.03, hsy - 0.025, 0); });
        // A''：同一个尺寸改轴的公差带 h6（外圆）
        S.Dimension dA3 = null;
        Step("A'' 轴 h6", () => { dA3 = dims.AddRadialDiameter(Ref(v, hole)); dA3.UseStandardLimitAndFitTolerance = true; dA3.ShaftClassString = "h6"; dA3.DisplayType = S.DimDispTypeConstants.igDimDisplayTypeClassPlusMinus; L("A'' " + Tol(dA3)); int k = dA3.KeyPointCount - 1; dA3.SetKeyPoint(k, hsx - 0.03, hsy - 0.025, 0); });

        // B. 坐标尺寸 ±0.05（人图读回 上 "±0.03" 下 "±0.03"、UnitTolerance）
        S.Dimension origin = null, dB = null;
        Step("B 坐标", () => {
            double x1, y1; side.GetStartPoint(out x1, out y1); double sx, sy; v.ViewToSheet(x1, y1, out sx, out sy);
            origin = dims.AddCoordinateOriginEx(Ref(v, side), sx, sy, 0, true, sx, sy + 0.06, 0);
            dB = dims.AddCoordinateEx(origin, Ref(v, hole), hsx, hsy, 0, true, hsx, sy + 0.06, 0);
            L("B 建好 " + Tol(dB));
        });
        Step("B1 Unit ±", () => { dB.DisplayType = S.DimDispTypeConstants.igDimDisplayTypeUnitTolerance; dB.PrimaryUpperTolerance = "0.05"; dB.PrimaryLowerTolerance = "-0.05"; L("B1 " + Tol(dB)); });
        Step("B2 直接写 ±0.05", () => { dB.PrimaryUpperTolerance = "±0.05"; L("B2 " + Tol(dB)); });

        // C. 线性尺寸 上下偏差 +0.021 / 0（Tolerance 类型）
        S.Dimension dC = null;
        Step("C 线性", () => {
            double x1, y1, x2, y2; bottom.GetStartPoint(out x1, out y1); bottom.GetEndPoint(out x2, out y2);
            dC = dims.AddLength(Ref(v, bottom)); L("C 建好 " + Tol(dC));
            dC.DisplayType = S.DimDispTypeConstants.igDimDisplayTypeTolerance; dC.PrimaryUpperTolerance = "+0.021"; dC.PrimaryLowerTolerance = "0"; L("C1 " + Tol(dC));
        });
        Step("C2 UnitTolerance 不对称", () => { dC.DisplayType = S.DimDispTypeConstants.igDimDisplayTypeUnitTolerance; L("C2 " + Tol(dC)); });

        // D. 形位公差框：不带引线，放在 A 那个孔标注下面（人图：%PO%VB%DI0.03，没挂图元，紧贴尺寸）
        var fcfs = (S.FeatureControlFrames)sheet.FeatureControlFrames;
        Step("D 位置度 Add", () => {
            double a, b, c, d; dA.Range(out a, out b, out c, out d);
            var f = fcfs.Add(a, b - 0.004, 0); f.PrimaryFrame = "%PO%VB%DI0.03%VBA";
            L("D " + Box(f) + " 文字 [" + f.PrimaryFrame + "] 顶点 " + f.VertexCount + " 挂上 " + Try(() => f.IsTerminatorAttachedToEntity, false));
        });
        Step("D2 SetAlignmentShape", () => {
            double a, b, c, d; dA2.Range(out a, out b, out c, out d);
            var f = fcfs.Add(a, b - 0.004, 0); f.PrimaryFrame = "%PO%VB%DI0.03";
            f.SetAlignmentShape(dA2, a, b - 0.004);
            object s; double x, y; f.GetAlignmentShape(out s, out x, out y);
            L("D2 " + Box(f) + " 对齐到 " + (s == null ? "空" : Microsoft.VisualBasic.Information.TypeName(s)));
            // 挪尺寸，看框跟不跟
            int k = dA2.KeyPointCount - 1; double kx, ky, kz; F.KeyPointType kt; int hh; dA2.GetKeyPoint(k, out kx, out ky, out kz, out kt, out hh);
            dA2.SetKeyPoint(k, kx + 0.01, ky, 0); Idle(); L("D2 尺寸右挪 10mm 后框 " + Box(f));
        });
        // E. 带引线挂在边上的平行度（AddByTerminator + AddVertex）
        Step("E 平行度 挂底边", () => {
            double x1, y1, x2, y2; bottom.GetStartPoint(out x1, out y1); bottom.GetEndPoint(out x2, out y2);
            double sx, sy; v.ViewToSheet((x1 + x2) / 2 + (x2 - x1) * 0.25, y1, out sx, out sy);
            var f = fcfs.AddByTerminator(Ref(v, bottom), sx, sy, 0, false); f.PrimaryFrame = "%PP%VB0.05%VBA"; f.AddVertex(sx, sy - 0.012, 0); f.AddVertex(sx + 0.008, sy - 0.012, 0);
            L("E " + Box(f) + " 挂上 " + f.IsTerminatorAttachedToEntity + " 顶点 " + f.VertexCount);
        });
        // F. 基准 A 挂在底边上
        var dfs = (S.DatumFrames)sheet.DatumFrames;
        Step("F 基准 A", () => {
            double x1, y1, x2, y2; bottom.GetStartPoint(out x1, out y1); bottom.GetEndPoint(out x2, out y2);
            double sx, sy; v.ViewToSheet((x1 + x2) / 2 - (x2 - x1) * 0.25, y1, out sx, out sy);
            var f = dfs.AddByTerminator(Ref(v, bottom), sx, sy, 0, false); f.Datum = "A"; f.AddVertex(sx, sy - 0.008, 0);
            L("F " + Box(f) + " 挂上 " + f.IsTerminatorAttachedToEntity + " 字母 " + f.Datum + " 顶点 " + f.VertexCount);
        });
        Step("F2 基准 B 不加顶点", () => {
            double x1, y1, x2, y2; side.GetStartPoint(out x1, out y1); side.GetEndPoint(out x2, out y2);
            double sx, sy; v.ViewToSheet(x1, (y1 + y2) / 2, out sx, out sy);
            var f = dfs.AddByTerminator(Ref(v, side), sx, sy, 0, false); f.Datum = "B";
            L("F2 " + Box(f) + " 挂上 " + f.IsTerminatorAttachedToEntity + " 顶点 " + f.VertexCount);
        });
        // G. 粗糙度 Ra3.2 挂在顶边（底边对面）上 / 不加顶点
        var sfs = (S.SurfaceFinishSymbols)sheet.SurfaceFinishSymbols;
        Step("G 粗糙度", () => {
            double x1, y1, x2, y2; bottom.GetStartPoint(out x1, out y1); bottom.GetEndPoint(out x2, out y2);
            double sx, sy; v.ViewToSheet(x1 + (x2 - x1) * 0.1, y1, out sx, out sy);
            var f = sfs.AddByTerminator(Ref(v, bottom), sx, sy, 0, false);
            // 第一次跑：新建的符号直接读 SurfaceFinishSymbol 报 E_INVALIDARG，所以先设再读
            L("G 建好 " + Box(f) + " 顶点 " + f.VertexCount);
            Step("G 设类型", () => f.SurfaceFinishSymbol = S.DimSurfTextureSymTypeConstants.igDimSurfaceFinishMachined);
            Step("G 设 RoughnessValue", () => f.RoughnessValue = "3.2");
            L("G1 值 [" + Try(() => f.RoughnessValue, "?") + "] 最小 [" + Try(() => f.MinimumRoughnessValue, "?") + "] 类型 " + Try(() => f.SurfaceFinishSymbol.ToString(), "?") + " " + Box(f) + " 挂上 " + f.IsTerminatorAttachedToEntity);
        });
        Step("G2 粗糙度 侧边 Minimum=1.6", () => {
            double x1, y1, x2, y2; side.GetStartPoint(out x1, out y1); side.GetEndPoint(out x2, out y2);
            double sx, sy; v.ViewToSheet(x1, y1 + (y2 - y1) * 0.8, out sx, out sy);
            var f = sfs.AddByTerminator(Ref(v, side), sx, sy, 0, false); f.SurfaceFinishSymbol = S.DimSurfTextureSymTypeConstants.igDimSurfaceFinishMachined; f.MinimumRoughnessValue = "1.6";
            L("G2 值 [" + f.RoughnessValue + "] 最小 [" + f.MinimumRoughnessValue + "] " + Box(f) + " 旋转 " + Try(() => f.RotationAngle, -1.0));
        });
        try { dft.SaveAs(Path.Combine(args[0], "probe7.dft")); } catch (Exception e) { L("SaveAs " + E(e)); }
        try { object old = null; app.GetGlobalParameter(F.ApplicationGlobalConstants.seApplicationGlobalDraftSaveAsPDFSheetOptions, ref old); app.SetGlobalParameter(F.ApplicationGlobalConstants.seApplicationGlobalDraftSaveAsPDFSheetOptions, 1); dft.SaveCopyAs(Path.Combine(args[0], "probe7.pdf")); app.SetGlobalParameter(F.ApplicationGlobalConstants.seApplicationGlobalDraftSaveAsPDFSheetOptions, old); } catch (Exception e) { L("pdf " + E(e)); }
        try { dft.Close(false); } catch { }
        L("RESULT probe7 done");
        return 0;
    }
    static double Y(D.DVLine2d l){ double x, y; l.GetStartPoint(out x, out y); return y; }
    static double X(D.DVLine2d l){ double x, y; l.GetStartPoint(out x, out y); return x; }
}
