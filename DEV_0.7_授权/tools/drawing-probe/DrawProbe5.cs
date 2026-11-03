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

// 一键出图探针 5：螺纹孔文字怎么写（OverrideString 在 e2e 里不显示）、坐标尺寸文字怎么错开（折线）、引线文字 AddVertex。
// 用法：DrawProbe5.exe <输出目录> <模板.dft> <带孔零件>
class DrawProbe5 {
    static F.Application app;
    static void L(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    static T Try<T>(Func<T> f, T d){ try { var v = f(); return v == null ? d : v; } catch { return d; } }
    static string E(Exception e){ return e.GetType().Name + " 0x" + e.HResult.ToString("X8") + " " + e.Message; }
    static string Mm(double m){ return (m * 1000).ToString("0.###"); }
    static void Idle(){ for (int k = 0; k < 10; k++) { try { app.DoIdle(); } catch { } Thread.Sleep(20); } }
    static void T(string what, Action a){ try { a(); } catch (Exception e) { L("  " + what + " 失败 " + E(e)); } }
    static string R(object o){ try { double a, b, c, d; ((dynamic)o).Range(out a, out b, out c, out d); return "(" + Mm(a) + "," + Mm(b) + ")-(" + Mm(c) + "," + Mm(d) + ")"; } catch (Exception e) { return "range? " + e.Message; } }
    static double[] S2(D.DrawingView v, double x, double y){ double a, b; v.ViewToSheet(x, y, out a, out b); return new[]{ a, b }; }
    static object Ref(D.DrawingView v, object m){ return v.GetReferenceToGraphicMember2[m]; }

    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = Encoding.UTF8;
        string outDir = args[0];
        app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application");
        try { app.DisplayAlerts = false; } catch { }
        var dft = (D.DraftDocument)app.Documents.Add("SolidEdge.DraftDocument", args[1]); Idle();
        var sheet = dft.ActiveSheet; var dims = (S.Dimensions)sheet.Dimensions;
        var link = dft.ModelLinks.Add(args[2]);
        var v = sheet.DrawingViews.AddPartView(link, D.ViewOrientationConstants.igTopView, 2.0, 0.12, 0.18, D.PartDrawingViewTypeConstants.sePartDesignedView); Idle();
        var cs = new List<D.DVCircle2d>(); foreach (D.DVCircle2d c in v.DVCircles2d) cs.Add(c);
        var small = cs.Where(c => c.Radius < 0.005).ToList();
        L("圆 " + cs.Count + " 小圆 " + small.Count);
        double vx0, vy0, vx1, vy1; v.Range(out vx0, out vy0, out vx1, out vy1);
        int k = 0;
        Action<string, Action<S.Dimension>> one = (label, set) => {
            if (k >= small.Count) return; var c = small[k++];
            T(label, () => { var d = dims.AddRadialDiameter(Ref(v, c)); set(d); L("  " + label + " → 显示 前缀[" + Try(() => d.PrefixDisplayedText, "") + "] 后缀[" + Try(() => d.SuffixDisplayedText, "") + "] 覆盖[" + Try(() => d.OverrideString, "") + "] 类型 " + Try(() => d.DisplayType.ToString(), "") + " 范围 " + R(d)); });
        };
        one("A 覆盖 M4", d => { d.PrefixString = "4×"; d.OverrideString = "M4"; d.SuffixString = "%DP8"; });
        one("B 前缀 4×M4 + Blank", d => { d.PrefixString = "4×M4"; d.DisplayType = S.DimDispTypeConstants.igDimDisplayTypeBlank; d.SuffixString = "%DP8"; });
        one("C 覆盖 %DIM4", d => { d.OverrideString = "4×M4%DP8"; });
        one("D 前缀含值 + 覆盖空格", d => { d.PrefixString = "4×M4"; d.OverrideString = " "; d.SuffixString = "%DP8"; });
        // 坐标尺寸折线
        D.DVLine2d left = null; double lx = 1e9; foreach (D.DVLine2d l in v.DVLines2d) { double a, b, c2, d2; l.GetStartPoint(out a, out b); l.GetEndPoint(out c2, out d2); if (Math.Abs(a - c2) < 1e-7 && a < lx) { lx = a; left = l; } }
        T("J 坐标折线", () => {
            double a, b, c2, d2; left.GetStartPoint(out a, out b); left.GetEndPoint(out c2, out d2);
            var p = S2(v, a, (b + d2) / 2); double yl = vy0 - 0.012;
            var o = dims.AddCoordinateOriginEx(Ref(v, left), p[0], p[1], 0, false, p[0], yl, 0);
            int i = 0;
            foreach (var c in cs) {
                double x, y; c.GetCenterPoint(out x, out y); var s = S2(v, x, y);
                var d = dims.AddCoordinateEx(o, Ref(v, c), s[0], s[1], 0, true, s[0], yl, 0);
                if (i == 0) {
                    L("  J 默认 Jogged=" + Try(() => d.Jogged, false) + " 范围 " + R(d));
                    T("J Jogged=true", () => { d.Jogged = true; L("  J 设 Jogged → " + d.Jogged + " " + R(d)); });
                    T("J SetJogPoint2", () => { d.SetJogPoint2(s[0] + 0.008, yl - 0.004); L("  J SetJogPoint2 → " + R(d)); double jx, jy; d.GetJogPoint2(out jx, out jy); L("    jog2=(" + Mm(jx) + "," + Mm(jy) + ")"); });
                    T("J SetJogPoint1Distance", () => { d.SetJogPoint1Distance(0.003); d.SetJogPoint3Distance(0.003); L("  J 1/3 距离 → " + R(d)); });
                }
                if (i == 1) T("J SetMultipleJogPoints", () => { Array xo = new double[] { 0.006 }, yo = new double[] { 0.0 }, dd = new double[] { 0.003 }; d.SetMultipleJogPoints(1, 0.003, ref xo, ref yo, ref dd); L("  J 多折点 → Jogged=" + Try(() => d.Jogged, false) + " " + R(d)); });
                if (i == 2) T("J 建时文字偏 8mm", () => { var d3 = dims.AddCoordinateEx(o, Ref(v, c), s[0], s[1], 0, true, s[0] + 0.008, yl, 0); L("  J 建时偏 → Jogged=" + Try(() => d3.Jogged, false) + " " + R(d3)); });
                i++;
            }
        });
        // 引线文字
        T("N AddVertex", () => {
            var c = cs[0]; double x, y; c.GetCenterPoint(out x, out y); var s = S2(v, x + c.Radius, y);
            var b = ((S.Balloons)sheet.Balloons).AddByTerminator(Ref(v, c), s[0], s[1], 0, false); b.Callout = 1; b.BalloonText = "4-C2";
            L("  N 建 " + R(b)); b.AddVertex(s[0] + 0.02, s[1] + 0.015, 0); L("  N AddVertex 后 " + R(b));
        });
        try { dft.SaveAs(Path.Combine(outDir, "probe5.dft")); } catch (Exception e) { L("SaveAs 失败 " + E(e)); }
        try { object old = null; app.GetGlobalParameter(F.ApplicationGlobalConstants.seApplicationGlobalDraftSaveAsPDFSheetOptions, ref old); app.SetGlobalParameter(F.ApplicationGlobalConstants.seApplicationGlobalDraftSaveAsPDFSheetOptions, 1); dft.SaveCopyAs(Path.Combine(outDir, "probe5.pdf")); app.SetGlobalParameter(F.ApplicationGlobalConstants.seApplicationGlobalDraftSaveAsPDFSheetOptions, old); } catch (Exception e) { L("pdf 失败 " + E(e)); }
        try { dft.Close(false); } catch { }
        L("RESULT probe5 done");
        return 0;
    }
}
