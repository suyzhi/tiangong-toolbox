using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using A=SolidEdgeAssembly;
using F=SolidEdgeFramework;

// 只读：打开一个装配，列出顶层实例（名字、引用的文件、文件在不在、文档加载了没有）。用法: AsmList.exe <输出目录> <装配>
class AsmList {
    static void L(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    static T Try<T>(Func<T> f, T d){ try { return f(); } catch { return d; } }
    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        F.Application app;
        try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); } catch (Exception e) { L("连不上 CAD：" + e.Message); return 2; }
        try { app.DisplayAlerts = false; } catch { }
        A.AssemblyDocument asm = null;
        try {
            asm = (A.AssemblyDocument)app.Documents.Open(args[1]);
            for (int i = 0; i < 40; i++) { try { app.DoIdle(); } catch { } Application.DoEvents(); Thread.Sleep(50); }
            int n = Try(() => asm.Occurrences.Count, -1), missing = 0, loaded = 0;
            L("实例 " + n);
            foreach (A.Occurrence o in asm.Occurrences) {
                string fn = Try(() => o.OccurrenceFileName, "?");
                bool exists = File.Exists(fn); if (!exists) missing++;
                bool doc = Try<object>(() => o.OccurrenceDocument, null) != null; if (doc) loaded++;
                L("  " + Try(() => o.Name, "?") + "　→ " + fn + (exists ? "" : "（文件不在）") + (doc ? "" : "（没加载）"));
            }
            L("RESULT 实例 " + n + "，文件不在 " + missing + "，已加载 " + loaded);
        } catch (Exception e) { L("异常：" + e.Message); return 1; }
        finally { try { if (asm != null) asm.Close(false); } catch { } }
        return 0;
    }
}
