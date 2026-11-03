using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using D=SolidEdgeDraft;
using F=SolidEdgeFramework;

// 一键出图探针 6：剖视图。主视图上画一条剖切线 → CreateView，量：剖视图放在哪、朝向、里面的边是什么类型、能不能挪、标签。
// 用法：DrawProbe6.exe <输出目录> <模板.dft> <带台阶孔的零件>
class DrawProbe6 {
    static F.Application app;
    static void L(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    static string E(Exception e){ return e.GetType().Name + " 0x" + e.HResult.ToString("X8") + " " + e.Message; }
    static string Mm(double m){ return (m * 1000).ToString("0.###"); }
    static void Idle(){ for (int k = 0; k < 10; k++) { try { app.DoIdle(); } catch { } Thread.Sleep(20); } }
    static string R(D.DrawingView v){ double a, b, c, d; v.Range(out a, out b, out c, out d); return "(" + Mm(a) + "," + Mm(b) + ")-(" + Mm(c) + "," + Mm(d) + ")"; }

    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = Encoding.UTF8;
        app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application");
        try { app.DisplayAlerts = false; } catch { }
        var dft = (D.DraftDocument)app.Documents.Add("SolidEdge.DraftDocument", args[1]); Idle();
        var sheet = dft.ActiveSheet;
        var link = dft.ModelLinks.Add(args[2]);
        var v = sheet.DrawingViews.AddPartView(link, D.ViewOrientationConstants.igTopView, 1.0, 0.10, 0.18, D.PartDrawingViewTypeConstants.sePartDesignedView); Idle();
        L("主视图 " + R(v));
        double a, b, c, d; v.Range(out a, out b, out c, out d);
        double cy = (b + d) / 2;
        D.DrawingView sec = null;
        try {
            var cut = v.CuttingPlanes.Add();
            L("CuttingPlanes.Add OK");
            var prof = cut.Profile;
            prof.Lines2d.AddBy2Points(a - 0.005, cy, c + 0.005, cy);
            L("剖切线 OK（图纸坐标）");

            sec = cut.CreateView();
            Idle();
            L("建好时 线 " + sec.DVLines2d.Count + " 最新=" + sec.IsUpToDate);
            try { sec.Update(); Idle(); L("Update 后 线 " + sec.DVLines2d.Count); } catch (Exception e) { L("Update 失败 " + E(e)); }
            try { sec.ForceUpdate(); Idle(); L("ForceUpdate 后 线 " + sec.DVLines2d.Count); } catch (Exception e) { L("ForceUpdate 失败 " + E(e)); }
            try { dft.UpdateAll(true); Idle(); L("UpdateAll 后 线 " + sec.DVLines2d.Count); } catch (Exception e) { L("UpdateAll 失败 " + E(e)); }
            L("CreateView OK 类型 " + sec.DrawingViewType + " " + R(sec) + " 标题 " + sec.CaptionDisplayedTextPrimary);
        } catch (Exception e) { L("剖视失败 " + E(e)); }
        if (sec != null) {
            try { double dx, dy, dz, ux, uy, uz; D.ViewOrientationConstants k; sec.ViewOrientation(out dx, out dy, out dz, out ux, out uy, out uz, out k); L("剖视朝向 " + k + " 视线(" + dx + "," + dy + "," + dz + ") x(" + ux + "," + uy + "," + uz + ")"); } catch (Exception e) { L("朝向 " + E(e)); }
            var types = new System.Collections.Generic.Dictionary<string, int>();
            foreach (D.DVLine2d l in sec.DVLines2d) { string t = l.EdgeType.ToString(); types[t] = (types.ContainsKey(t) ? types[t] : 0) + 1; }
            foreach (var kv in types) L("  剖视线 " + kv.Key + " " + kv.Value);
            L("  剖视 圆 " + sec.DVCircles2d.Count + " 弧 " + sec.DVArcs2d.Count);
            try { double ox, oy; sec.GetOrigin(out ox, out oy); sec.SetOrigin(ox, oy - 0.10); Idle(); L("挪剖视 → " + R(sec)); } catch (Exception e) { L("挪剖视失败 " + E(e)); }
            try { sec.SetOrigin(0.30, 0.18); Idle(); L("挪到右边 → " + R(sec)); } catch (Exception e) { L("挪到右边失败 " + E(e)); }
        }
        try { dft.SaveAs(Path.Combine(args[0], "probe6.dft")); } catch (Exception e) { L("SaveAs " + E(e)); }
        try { object old = null; app.GetGlobalParameter(F.ApplicationGlobalConstants.seApplicationGlobalDraftSaveAsPDFSheetOptions, ref old); app.SetGlobalParameter(F.ApplicationGlobalConstants.seApplicationGlobalDraftSaveAsPDFSheetOptions, 1); dft.SaveCopyAs(Path.Combine(args[0], "probe6.pdf")); app.SetGlobalParameter(F.ApplicationGlobalConstants.seApplicationGlobalDraftSaveAsPDFSheetOptions, old); } catch (Exception e) { L("pdf " + E(e)); }
        try { dft.Close(false); } catch { }
        L("RESULT probe6 done");
        return 0;
    }
}
