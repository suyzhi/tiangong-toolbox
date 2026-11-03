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

// 一键出图探针 7c：H7 怎么才显示出来。探针 7 里 ClassPlusMinus + HoleClassString=H7 + 标准=true 设上了，但图上只有 Ø6.6、上下偏差读不出。
// 先把人图里一个 H7 孔标注的全部属性列出来，再在零件图上用 4 种设法各建一个，列同样的属性，存 pdf 看。
// 用法：DrawProbe7c.exe <输出目录> <人图副本.dft> <模板.dft> <零件副本>
class DrawProbe7c {
    static F.Application app;
    static void L(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    static string E(Exception e){ return e.GetType().Name + " 0x" + e.HResult.ToString("X8") + " " + e.Message; }
    static void Idle(){ for (int k = 0; k < 10; k++) { try { app.DoIdle(); } catch { } Thread.Sleep(20); } }
    static void Step(string name, Action a){ try { a(); } catch (Exception e) { L("  " + name + " 失败 " + E(e)); } }
    static readonly string[] Props = { "DisplayType", "HoleClassString", "ShaftClassString", "PrimaryToleranceSymbol", "PrimaryUpperTolerance", "PrimaryLowerTolerance", "UseStandardLimitAndFitTolerance",
        "ToleranceHeightRatio", "HoleShaftToleranceSeparatorType", "InhibitZeroToleranceDisplay", "PrefixString", "SuffixString", "SubfixString", "SuperfixString", "OverrideString", "Value", "Style" };
    static string Dump(S.Dimension d){
        var sb = new StringBuilder();
        foreach (var p in Props) {
            string v;
            try { object o = typeof(S.Dimension).GetProperty(p).GetValue(d, null); v = o == null ? "null" : (p == "Style" ? Microsoft.VisualBasic.Information.TypeName(o) : Convert.ToString(o)); } catch (Exception e) { v = "ERR " + (e.InnerException ?? e).Message.Split('\n')[0]; }
            sb.Append(p).Append("=[").Append(v).Append("] ");
        }
        try { double a, b, c, e; d.Range(out a, out b, out c, out e); sb.Append("框 " + Math.Round((c - a) * 1000, 2) + "×" + Math.Round((e - b) * 1000, 2)); } catch { }
        return sb.ToString();
    }
    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = Encoding.UTF8;
        app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application");
        try { app.DisplayAlerts = false; } catch { }
        // 1. 人图里的 H7
        var h = (D.DraftDocument)app.Documents.Open(args[1]); Idle();
        foreach (D.Sheet sh in h.Sheets) {
            if (sh.SectionType != D.SheetSectionTypeConstants.igWorkingSection) continue;
            foreach (S.Dimension d in (S.Dimensions)sh.Dimensions) {
                string tt = ""; try { tt = d.DisplayType.ToString(); } catch { }
                if (tt.Contains("Class") || tt.Contains("Tol")) L("人图 " + Dump(d));
            }
        }
        try { h.Close(false); } catch { }
        // 2. 四种设法
        var dft = (D.DraftDocument)app.Documents.Add("SolidEdge.DraftDocument", args[2]); Idle();
        var sheet = dft.ActiveSheet; var link = dft.ModelLinks.Add(args[3]);
        var v = sheet.DrawingViews.AddPartView(link, D.ViewOrientationConstants.igTopView, 1.0, 0.15, 0.15, D.PartDrawingViewTypeConstants.sePartDesignedView); Idle();
        D.DVCircle2d hole = null; foreach (D.DVCircle2d c in v.DVCircles2d) if (hole == null || c.Radius < hole.Radius) hole = c;
        double hx, hy, sx, sy; hole.GetCenterPoint(out hx, out hy); v.ViewToSheet(hx, hy, out sx, out sy);
        var dims = (S.Dimensions)sheet.Dimensions;
        Func<int, S.Dimension> mk = i => { var d = dims.AddRadialDiameter(v.GetReferenceToGraphicMember2[hole]); int k = d.KeyPointCount - 1; d.SetKeyPoint(k, sx - 0.09 + i * 0.045, sy + 0.035, 0); return d; };
        Step("V1 标准+H7+符号", () => { var d = mk(0); d.UseStandardLimitAndFitTolerance = true; d.HoleClassString = "H7"; d.PrimaryToleranceSymbol = "H7"; d.DisplayType = S.DimDispTypeConstants.igDimDisplayTypeClassPlusMinus; Idle(); L("V1 " + Dump(d)); });
        Step("V2 ClassPlusMinus+数值", () => { var d = mk(1); d.DisplayType = S.DimDispTypeConstants.igDimDisplayTypeClassPlusMinus; d.HoleClassString = "H7"; d.PrimaryToleranceSymbol = "H7"; d.PrimaryUpperTolerance = "+0.015"; d.PrimaryLowerTolerance = "0"; Idle(); L("V2 " + Dump(d)); });
        Step("V3 HoleShaftWithTol", () => { var d = mk(2); d.UseStandardLimitAndFitTolerance = true; d.HoleClassString = "H7"; d.DisplayType = S.DimDispTypeConstants.igDimDisplayTypeHoleShaftWithTol; Idle(); L("V3 " + Dump(d)); });
        Step("V4 Tolerance+后缀", () => { var d = mk(3); d.DisplayType = S.DimDispTypeConstants.igDimDisplayTypeTolerance; d.PrimaryUpperTolerance = "+0.015"; d.PrimaryLowerTolerance = "0"; d.SuffixString = "H7"; Idle(); L("V4 " + Dump(d)); });
        Step("V5 Classfit+标准", () => { var d = mk(4); d.UseStandardLimitAndFitTolerance = true; d.HoleClassString = "H7"; d.PrimaryToleranceSymbol = "H7"; d.DisplayType = S.DimDispTypeConstants.igDimDisplayTypeClassfit; Idle(); L("V5 " + Dump(d)); });
        try { dft.SaveAs(Path.Combine(args[0], "probe7c.dft")); } catch (Exception e) { L("SaveAs " + E(e)); }
        try { object old = null; app.GetGlobalParameter(F.ApplicationGlobalConstants.seApplicationGlobalDraftSaveAsPDFSheetOptions, ref old); app.SetGlobalParameter(F.ApplicationGlobalConstants.seApplicationGlobalDraftSaveAsPDFSheetOptions, 1); dft.SaveCopyAs(Path.Combine(args[0], "probe7c.pdf")); app.SetGlobalParameter(F.ApplicationGlobalConstants.seApplicationGlobalDraftSaveAsPDFSheetOptions, old); } catch (Exception e) { L("pdf " + E(e)); }
        try { dft.Close(false); } catch { }
        L("RESULT probe7c done");
        return 0;
    }
}
