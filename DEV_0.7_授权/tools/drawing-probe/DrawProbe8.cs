using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using D=SolidEdgeDraft;
using F=SolidEdgeFramework;

// 只读探针 8：视图坐标 → 图纸坐标对不对得上。每个视图：图元外框（视图坐标）四个角 ViewToSheet 以后的位置、视图 Range、比例、旋转角。
// 查 OK310-02-010 主视图：竖直坐标串画成了图下面一串量 x 的、孔引线甩到别处。
// 用法：DrawProbe8.exe <输出目录> <工程图.dft>
class DrawProbe8 {
    static string Mm(double v){ return Math.Round(v * 1000, 2).ToString(); }
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
                double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
                foreach (D.DVLine2d l in v.DVLines2d) { double a, b, c, e; l.GetStartPoint(out a, out b); l.GetEndPoint(out c, out e); x0 = Math.Min(x0, Math.Min(a, c)); x1 = Math.Max(x1, Math.Max(a, c)); y0 = Math.Min(y0, Math.Min(b, e)); y1 = Math.Max(y1, Math.Max(b, e)); }
                double ra, rb, rc, rd; v.Range(out ra, out rb, out rc, out rd);
                string rot = "?"; try { rot = ((dynamic)v).RotationAngle.ToString(); } catch (Exception e) { rot = "err " + e.Message; }
                string org = "?"; try { double ox, oy; v.GetOrigin(out ox, out oy); org = Mm(ox) + "," + Mm(oy); } catch (Exception e) { org = "err " + e.Message; }
                Console.WriteLine("== " + v.Name + " " + o + " 视线(" + dx + "," + dy + "," + dz + ") x(" + ux + "," + uy + "," + uz + ") 比例 " + v.ScaleFactor + " 旋转 " + rot + " 原点 " + org);
                Console.WriteLine("  图元外框(视图) " + Mm(x0) + "," + Mm(y0) + " - " + Mm(x1) + "," + Mm(y1) + "   Range(图纸) " + Mm(ra) + "," + Mm(rb) + " - " + Mm(rc) + "," + Mm(rd));
                foreach (var p in new[] { new[] { x0, y0 }, new[] { x1, y0 }, new[] { x1, y1 }, new[] { x0, y1 } }) {
                    double sx, sy; v.ViewToSheet(p[0], p[1], out sx, out sy);
                    Console.WriteLine("  (" + Mm(p[0]) + "," + Mm(p[1]) + ") → 图纸 (" + Mm(sx) + "," + Mm(sy) + ")");
                }
            }
        }
        try { d.Close(false); } catch { }
        Console.WriteLine("RESULT probe8 done");
        return 0;
    }
}
