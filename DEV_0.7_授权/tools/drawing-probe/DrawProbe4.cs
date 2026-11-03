using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using D=SolidEdgeDraft;
using F=SolidEdgeFramework;

// 一键出图探针 4：SetViewOrientation 两个向量到底是什么意思（e2e-2 里 015 的主视图转了 90°）。
// 零件外形已知（015：X 79 / Y 275 / Z 160），每种朝向建一个视图，读视图坐标里图元外框的宽高 → 反推屏幕 x、y 对应哪个模型轴。
// 用法：DrawProbe4.exe <输出目录> <模板.dft> <零件>
class DrawProbe4 {
    static F.Application app;
    static void L(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    static void Idle(){ for (int k = 0; k < 10; k++) { try { app.DoIdle(); } catch { } Thread.Sleep(20); } }
    static string Box(D.DrawingView v){
        double x0 = 1e9, y0 = 1e9, x1 = -1e9, y1 = -1e9;
        foreach (D.DVLine2d l in v.DVLines2d) { double a, b, c, d; l.GetStartPoint(out a, out b); l.GetEndPoint(out c, out d); x0 = Math.Min(x0, Math.Min(a, c)); x1 = Math.Max(x1, Math.Max(a, c)); y0 = Math.Min(y0, Math.Min(b, d)); y1 = Math.Max(y1, Math.Max(b, d)); }
        double dx, dy, dz, ux, uy, uz; D.ViewOrientationConstants k; v.ViewOrientation(out dx, out dy, out dz, out ux, out uy, out uz, out k);
        double ang = 0; try { v.GetRotationAngle(out ang); } catch { }
        // 一个已知点：模型原点在视图里落在哪 —— 用视图坐标原点 ViewToSheet 没意义，改读圆心最左的圆
        return "宽 " + ((x1 - x0) * 1000).ToString("0.#") + " 高 " + ((y1 - y0) * 1000).ToString("0.#") + "  报告 " + k + " 视线(" + R(dx) + "," + R(dy) + "," + R(dz) + ") x(" + R(ux) + "," + R(uy) + "," + R(uz) + ") 转角 " + R(ang) + " 圆 " + v.DVCircles2d.Count;
    }
    static string R(double v){ return Math.Round(v, 3).ToString(); }

    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = Encoding.UTF8;
        app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application");
        try { app.DisplayAlerts = false; } catch { }
        var dft = (D.DraftDocument)app.Documents.Add("SolidEdge.DraftDocument", args[1]); Idle();
        var sheet = dft.ActiveSheet;
        var link = dft.ModelLinks.Add(args[2]);
        double x0, y0, z0, x1, y1, z1; link.Range3d(out x0, out y0, out z0, out x1, out y1, out z1);
        L("模型 X " + R((x1 - x0) * 1000) + " Y " + R((y1 - y0) * 1000) + " Z " + R((z1 - z0) * 1000));
        foreach (var o in new[] { D.ViewOrientationConstants.igFrontView, D.ViewOrientationConstants.igTopView, D.ViewOrientationConstants.igRightView, D.ViewOrientationConstants.igLeftView, D.ViewOrientationConstants.igBackView, D.ViewOrientationConstants.igBottomView }) {
            try { var v = sheet.DrawingViews.AddPartView(link, o, 0.2, 0.2, 0.15, D.PartDrawingViewTypeConstants.sePartDesignedView); Idle(); L("标准 " + o + "：" + Box(v)); } catch (Exception e) { L("标准 " + o + " 失败 " + e.Message); }
        }
        var tests = new[] {
            new double[] { 1, 0, 0, 0, 1, 0 }, new double[] { 1, 0, 0, 0, 0, 1 }, new double[] { 0, 0, 1, 0, 1, 0 }, new double[] { 0, 0, 1, 1, 0, 0 },
            new double[] { 0, -1, 0, 1, 0, 0 }, new double[] { 0, -1, 0, 0, 0, 1 }, new double[] { -1, 0, 0, 0, 1, 0 }, new double[] { 0, 1, 0, 0, 0, 1 } };
        foreach (var t in tests) {
            try {
                var v = sheet.DrawingViews.AddPartView(link, D.ViewOrientationConstants.igTopView, 0.2, 0.2, 0.15, D.PartDrawingViewTypeConstants.sePartDesignedView); Idle();
                v.SetViewOrientation(t[0], t[1], t[2], t[3], t[4], t[5]); Idle(); try { v.Update(); } catch { } Idle();
                L("Set 视线(" + t[0] + "," + t[1] + "," + t[2] + ") x(" + t[3] + "," + t[4] + "," + t[5] + ")：" + Box(v));
                if (t[0] == 1 && t[4] == 1) {
                    var dn = sheet.DrawingViews.AddByFold(v, D.FoldTypeConstants.igFoldDown, 0.2, 0.08); Idle(); L("   下折：" + Box(dn));
                    var rt = sheet.DrawingViews.AddByFold(v, D.FoldTypeConstants.igFoldRight, 0.33, 0.15); Idle(); L("   右折：" + Box(rt));
                    
                }
            } catch (Exception e) { L("Set 失败 " + e.Message); }
        }
        // 旋转角
        try { var v = sheet.DrawingViews.AddPartView(link, D.ViewOrientationConstants.igRightView, 0.2, 0.2, 0.15, D.PartDrawingViewTypeConstants.sePartDesignedView); Idle(); v.SetRotationAngle(Math.PI / 2); Idle(); try { v.Update(); } catch { } Idle(); L("右视 + 转90：" + Box(v)); } catch (Exception e) { L("转角失败 " + e.Message); }
        try { dft.Close(false); } catch { }
        L("RESULT probe4 done");
        return 0;
    }
}
