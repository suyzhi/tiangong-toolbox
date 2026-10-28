using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using D=SolidEdgeDraft;
using F=SolidEdgeFramework;

// 只读：打开一张工程图，列出每张图纸的视图：名字、关联的模型文件、文件在不在、是不是最新。用法: DftList.exe <输出目录> <工程图>
class DftList {
    static void L(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    static T Try<T>(Func<T> f, T d){ try { return f(); } catch { return d; } }
    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        F.Application app;
        try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); } catch (Exception e) { L("连不上 CAD：" + e.Message); return 2; }
        try { app.DisplayAlerts = false; } catch { }
        D.DraftDocument dft = null;
        try {
            dft = (D.DraftDocument)app.Documents.Open(args[1]);
            for (int i = 0; i < 40; i++) { try { app.DoIdle(); } catch { } Application.DoEvents(); Thread.Sleep(50); }
            int views = 0, linked = 0, missing = 0, stale = 0;
            foreach (D.Sheet sh in dft.Sheets) {
                foreach (D.DrawingView v in sh.DrawingViews) {
                    views++;
                    string file = "";
                    try { var link = v.ModelLink; if (link != null) file = Try(() => (string)((dynamic)link).FileName, ""); } catch { }
                    if (file.Length > 0) { linked++; if (!File.Exists(file)) missing++; }
                    bool up = Try(() => v.IsUpToDate, true); if (!up) stale++;
                    if (views <= 12) L("  " + sh.Name + " / " + Try(() => v.Name, "?") + "　→ " + (file.Length > 0 ? file : "（没有模型链接）") + (file.Length > 0 && !File.Exists(file) ? "（文件不在）" : "") + (up ? "" : "（过期）"));
                }
            }
            int links = Try(() => dft.ModelLinks.Count, -1);
            L("RESULT 图纸 " + Try(() => dft.Sheets.Count, -1) + "，视图 " + views + "，有模型链接的 " + linked + "，链接文件不在 " + missing + "，过期 " + stale + "，ModelLinks " + links);
        } catch (Exception e) { L("异常：" + e.Message); return 1; }
        finally { try { if (dft != null) dft.Close(false); } catch { } }
        return 0;
    }
}
