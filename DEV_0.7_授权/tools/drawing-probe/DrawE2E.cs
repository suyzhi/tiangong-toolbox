using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using F=SolidEdgeFramework;
using TianGongCadSuite;

// 一键出图端到端：对清单里每个零件走插件的 DrawingMaker.Part（和功能区命令同一路径），dft + pdf 落在输出目录。
// 用法：DrawE2E.exe <输出目录> <零件1> [零件2 ...]   （零件也可以是一个 .txt 清单，每行一个）
class DrawE2E {
    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = Encoding.UTF8;
        string outDir = args[0];
        var app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application");
        try { app.DisplayAlerts = false; } catch { }
        var files = new System.Collections.Generic.List<string>();
        for (int i = 1; i < args.Length; i++) { if (args[i].EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) files.AddRange(File.ReadAllLines(args[i], Encoding.UTF8)); else files.Add(args[i]); }
        int ok = 0;
        foreach (var f in files) {
            if (f.Trim().Length == 0) continue;
            var t0 = DateTime.Now;
            var opt = new DrawingOptions { OutputDir = outDir, PdfDir = outDir, Overwrite = true, Close = true };
            var o = f.Trim().EndsWith(".asm", StringComparison.OrdinalIgnoreCase) ? DrawingMaker.Assembly(app, f.Trim(), opt) : DrawingMaker.Part(app, f.Trim(), opt);
            if (o.Ok) ok++;
            Console.WriteLine((o.Ok ? "OK  " : "FAIL") + " " + Path.GetFileName(f) + " " + (DateTime.Now - t0).TotalSeconds.ToString("0.0") + "s 图纸=" + o.Sheet + " 比例=" + o.Scale + " 视图=" + o.Views + " 尺寸=" + o.Dimensions + " 失败=" + o.Failed + " 孔=" + o.Holes + "/" + o.HoleCallouts + " 圆角=" + o.Radii + " 完成度=" + o.Covered + "/" + o.Required);
            foreach (var n in o.Notes) Console.WriteLine("     " + n);
        }
        Console.WriteLine("RESULT " + ok + "/" + files.Count);
        return 0;
    }
}
