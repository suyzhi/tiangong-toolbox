using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using A = SolidEdgeAssembly;
using F = SolidEdgeFramework;

// 0.11.3 零件预览：总装配预览是空的 —— 在明文副本上做变体装配（隐藏一个零件 / 隐藏一个子装配），存盘后用独立程序看预览。
// 用法: AsmVariantProbe.exe <输出目录> <副本文件夹>（里面要有 2号子围栏.asm、SK26.01-OK310-03-001.asm）
class AsmVariantProbe {
    static F.Application app;
    static void L(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    static void Idle(){ for (int k = 0; k < 20; k++) { try { app.DoIdle(); } catch { } Thread.Sleep(100); } }
    static string Head(string f){ var b = new byte[4]; using (var s = File.OpenRead(f)) s.Read(b, 0, 4); return BitConverter.ToString(b); }

    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = Encoding.UTF8;
        string dir = args[1];
        try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); } catch (Exception e) { L("连不上 CAD：" + e.Message); return 2; }
        try { app.DisplayAlerts = false; } catch { }
        if (args.Length > 2 && args[2] == "ext") {
            // 引用别的文件夹里的零件、存盘后把那个零件删掉（焊接台车里 …-02-14.par / -28.par 就是这样找不到的）
            string sub = Path.Combine(dir, "ext"); Directory.CreateDirectory(sub);
            foreach (var v in new[] { "vis", "hid" }) {
                string part = Path.Combine(sub, "ext-" + v + ".par");
                File.Copy(Path.Combine(dir, "填充板_20261015_114424_001.par"), part, true);
                try {
                    var asm = (A.AssemblyDocument)app.Documents.Open(Path.Combine(dir, "2号子围栏.asm")); Idle();
                    var o = asm.Occurrences.AddByFilename(part); Idle();
                    if (v == "hid") o.Visible = false;
                    string dst = Path.Combine(dir, "var-ext" + v + ".asm");
                    asm.SaveAs(dst); Idle(); asm.Close(false); Idle();
                    L(dst + " saved, head=" + Head(dst));
                } catch (Exception e) { L(v + " FAIL " + e.Message); }
            }
            L("RESULT done");
            return 0;
        }
        Variant(dir, "2号子围栏.asm", "var-hidpart.asm", 21);
        Variant(dir, "SK26.01-OK310-03-001.asm", "var-hidsub.asm", 2);
        Variant(dir, "2号子围栏.asm", "var-plain.asm", 0);
        L("RESULT done");
        return 0;
    }
    static void Variant(string dir, string src, string dst, int hide){
        try {
            var asm = (A.AssemblyDocument)app.Documents.Open(Path.Combine(dir, src)); Idle();
            if (hide > 0) { var o = asm.Occurrences.Item(hide); o.Visible = false; L(dst + ": hid " + o.OccurrenceFileName + " visible=" + o.Visible); }
            asm.SaveAs(Path.Combine(dir, dst)); Idle();
            asm.Close(false); Idle();
            L(dst + " saved, head=" + Head(Path.Combine(dir, dst)));
        } catch (Exception e) { L(dst + " FAIL " + e.Message); }
    }
}
