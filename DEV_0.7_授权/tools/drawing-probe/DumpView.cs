using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using D=SolidEdgeDraft;
using F=SolidEdgeFramework;

// 只读：打开一张工程图，列出每个视图里所有线 / 圆 / 弧（视图坐标 mm、边类型）。查"某条边为什么没被标"用。
// 用法：DumpView.exe <输出目录> <工程图.dft>
class DumpView {
    static string Mm(double v){ return Math.Round(v * 1000, 3).ToString(); }
    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = Encoding.UTF8;
        var app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application");
        try { app.DisplayAlerts = false; } catch { }
        var d = (D.DraftDocument)app.Documents.Open(args[1]);
        for (int k = 0; k < 6; k++) { try { app.DoIdle(); } catch { } Thread.Sleep(20); }
        foreach (D.Sheet sh in d.Sheets) {
            if (sh.SectionType != D.SheetSectionTypeConstants.igWorkingSection) continue;
            foreach (D.DrawingView v in sh.DrawingViews) {
                double dx, dy, dz, ux, uy, uz; D.ViewOrientationConstants o; v.ViewOrientation(out dx, out dy, out dz, out ux, out uy, out uz, out o);
                Console.WriteLine("== " + v.Name + " " + o + " 线 " + v.DVLines2d.Count);
                foreach (D.DVLine2d l in v.DVLines2d) { double a, b, c, e; l.GetStartPoint(out a, out b); l.GetEndPoint(out c, out e); Console.WriteLine("  L (" + Mm(a) + "," + Mm(b) + ")-(" + Mm(c) + "," + Mm(e) + ") " + l.EdgeType); }
                foreach (D.DVArc2d l in v.DVArcs2d) { double a, b; l.GetCenterPoint(out a, out b); Console.WriteLine("  A (" + Mm(a) + "," + Mm(b) + ") R" + Mm(l.Radius) + " sweep " + Math.Round(l.SweepAngle, 3) + " " + l.EdgeType); }
                foreach (D.DVCircle2d l in v.DVCircles2d) { double a, b; l.GetCenterPoint(out a, out b); Console.WriteLine("  C (" + Mm(a) + "," + Mm(b) + ") R" + Mm(l.Radius) + " " + l.EdgeType); }
            }
        }
        try { d.Close(false); } catch { }
        Console.WriteLine("RESULT dump done");
        return 0;
    }
}
