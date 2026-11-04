using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using A = SolidEdgeAssembly;
using F = SolidEdgeFramework;

// 0.11.3 零件预览：总装配预览是空的 / 偏到一角 —— 列出装配里每个顶层实例的可见性、文件在不在、包围盒（米），
// 看是不是有隐藏的、找不到的、离得很远的实例把预览控件「适合」的范围撑大了。
// 用法: AsmRangeProbe.exe <输出目录> <装配1> [装配2 …]      （经 run-cad-queue.ps1 在私有桌面跑）
class AsmRangeProbe {
    static F.Application app;
    static void L(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    static T Try<T>(Func<T> f, T d){ try { return f(); } catch { return d; } }

    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = Encoding.UTF8;
        try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); } catch (Exception e) { L("连不上 CAD：" + e.Message); return 2; }
        try { app.DisplayAlerts = false; } catch { }
        for (int i = 1; i < args.Length; i++) {
            L("==== " + args[i]);
            var asm = (A.AssemblyDocument)app.Documents.Open(args[i]);
            for (int k = 0; k < 20; k++) { try { app.DoIdle(); } catch { } Thread.Sleep(100); }
            var rows = new List<string>();
            double[] lo = { 1e9, 1e9, 1e9 }, hi = { -1e9, -1e9, -1e9 };
            int n = 0, hidden = 0, missing = 0;
            foreach (A.Occurrence o in asm.Occurrences) {
                n++;
                string f = Try(() => o.OccurrenceFileName, "?");
                bool vis = Try(() => o.Visible, true), exists = File.Exists(f);
                bool hasDoc = Try(() => o.OccurrenceDocument != null, false);
                if (!vis) hidden++;
                if (!exists || !hasDoc) missing++;
                string box = "";
                try {
                    Array mn = new double[3], mx = new double[3];
                    o.GetRangeBox(ref mn, ref mx);
                    var a = (double[])mn; var b = (double[])mx;
                    box = string.Format("[{0:0.000},{1:0.000},{2:0.000}]-[{3:0.000},{4:0.000},{5:0.000}]", a[0], a[1], a[2], b[0], b[1], b[2]);
                    if (vis) for (int d = 0; d < 3; d++) { lo[d] = Math.Min(lo[d], a[d]); hi[d] = Math.Max(hi[d], b[d]); }
                } catch (Exception e) { box = "range err " + e.Message; }
                rows.Add((vis ? "  " : "H ") + (exists ? "" : "MISSING ") + (hasDoc ? "" : "NODOC ") + Path.GetFileName(f) + " " + box + (Path.GetDirectoryName(f).Equals(Path.GetDirectoryName(args[i]), StringComparison.OrdinalIgnoreCase) ? "" : "  @" + Path.GetDirectoryName(f)));
            }
            L("occurrences=" + n + " hidden=" + hidden + " missing=" + missing);
            L(string.Format("visible range [{0:0.000},{1:0.000},{2:0.000}]-[{3:0.000},{4:0.000},{5:0.000}]", lo[0], lo[1], lo[2], hi[0], hi[1], hi[2]));
            foreach (var r in rows) L(r);
            try { asm.Close(false); } catch { }
        }
        L("RESULT done");
        return 0;
    }
}
