using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using D=SolidEdgeDraft;
using F=SolidEdgeFramework;
using S=SolidEdgeFrameworkSupport;

// 只读：列一张工程图里所有尺寸（类型、值、前后缀、下标、显示类型）和气泡文字。用法：DumpDims.exe <输出目录> <dft1> [dft2...]
class DumpDims {
    static T Try<T>(Func<T> f, T d){ try { var v = f(); return v == null ? d : v; } catch { return d; } }
    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = Encoding.UTF8;
        var app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application");
        try { app.DisplayAlerts = false; } catch { }
        for (int i = 1; i < args.Length; i++) {
            var d = (D.DraftDocument)app.Documents.Open(args[i]);
            for (int k = 0; k < 6; k++) { try { app.DoIdle(); } catch { } Thread.Sleep(20); }
            Console.WriteLine("== " + args[i]);
            foreach (D.Sheet sh in d.Sheets) {
                if (sh.SectionType != D.SheetSectionTypeConstants.igWorkingSection) continue;
                foreach (S.Dimension m in (S.Dimensions)sh.Dimensions)
                    if (Try(() => m.DimensionType.ToString(), "") != "igDimTypeCoordinate")
                        Console.WriteLine("  " + Try(() => m.DimensionType.ToString(), "") + " " + Math.Round(Try(() => m.Value, 0.0) * 1000, 2) + " [" + Try(() => m.PrefixString, "") + "|" + Try(() => m.OverrideString, "") + "|" + Try(() => m.SuffixString, "") + "|" + Try(() => m.SubfixString, "") + "] " + Try(() => m.DisplayType.ToString(), ""));
                foreach (S.Balloon b in (S.Balloons)sh.Balloons) Console.WriteLine("  气泡 " + Try(() => b.BalloonDisplayedText, ""));
            }
            try { d.Close(false); } catch { }
        }
        Console.WriteLine("RESULT dump done");
        return 0;
    }
}
