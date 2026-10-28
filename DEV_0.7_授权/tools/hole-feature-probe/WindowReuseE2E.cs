using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using A = SolidEdgeAssembly;
using F = SolidEdgeFramework;

// 0.10.3 窗口管理真机：用 Application.StartCommand 像点功能区按钮一样点插件命令，看插件开着哪些窗口。
//   · 配孔检查查完以后再点「配孔检查」：窗口还是那一个、结果留着（状态栏说"窗口一直开着"）；
//   · 配孔检查开着时点「自动打孔」「改孔型」：检查窗口不被关掉（伴随窗口），打孔 / 改孔型照常换；
//   · 「批量格式转换」点两次只有一个转换窗口；「型材自动填充」点两次只有一个窗口。
// 用法: WindowReuseE2E.exe <输出目录>（开发构建要注册成插件；夹具是一个新建的空装配，存在输出目录里）
class WindowReuseE2E {
    static F.Application app; static dynamic diag; static int fail;
    static void L(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    static void Check(bool ok, string what){ L((ok ? "PASS: " : "FAIL: ") + what); if (!ok) fail++; }
    static void Pump(int seconds){ var end = DateTime.Now.AddSeconds(seconds); while (DateTime.Now < end) Thread.Sleep(200); }
    static void Retry(Action a, string what){
        for (int i = 0; ; i++) {
            try { a(); return; }
            catch (COMException e) { if (i >= 30) throw; if (e.ErrorCode != unchecked((int)0x80010001) && e.ErrorCode != unchecked((int)0x8001010A)) throw; Thread.Sleep(500); }
        }
    }
    static string Forms(){ string s = null; Retry(delegate { s = (string)diag.OpenFormsReport(); }, "OpenFormsReport"); return s ?? ""; }
    static int Count(string forms, string type){ return forms.Split('\n').Count(l => l.StartsWith(type + "|", StringComparison.Ordinal)); }
    static void Command(int id){
        int native = 0; Retry(delegate { native = (int)diag.NativeCommandId(id); }, "NativeCommandId");
        Retry(delegate { app.StartCommand((F.SolidEdgeCommandConstants)native); }, "StartCommand " + id);
        Pump(4);
    }
    static string Wait(Action start, int seconds){
        Retry(start, "start");
        var end = DateTime.Now.AddSeconds(seconds); string r = "";
        while (DateTime.Now < end) { Thread.Sleep(500); try { r = (string)diag.Result; } catch { continue; } if (!string.IsNullOrEmpty(r)) return r; }
        return "TIMEOUT";
    }
    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        string outDir = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
        Directory.CreateDirectory(outDir);
        try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); }
        catch (Exception e) { L("连不上 CAD：" + e.Message); return 2; }
        try { app.DisplayAlerts = false; } catch { }
        try {
            A.AssemblyDocument asm = null;
            Retry(delegate { asm = (A.AssemblyDocument)app.Documents.Add("SolidEdge.AssemblyDocument"); }, "新建装配");
            string file = Path.Combine(outDir, "WinFixture_" + DateTime.Now.ToString("HHmmss") + ".asm");
            Retry(delegate { asm.SaveAs(file); }, "保存装配");
            foreach (F.AddIn a in app.AddIns) if (string.Equals(a.GUID, "{8C05165C-65A4-4EF2-A138-508589D82004}", StringComparison.OrdinalIgnoreCase)) { a.Connect = true; diag = a.Object; }
            if (diag == null) { L("FAIL: 插件没加载"); return 1; }
            L("LIBRARY " + (string)diag.LoadedLibrary);

            Command(8);
            string f1 = Forms(); L(f1);
            Check(Count(f1, "AutoHoleCheckForm") == 1, "点「配孔检查」开出检查窗口");
            L(Wait(delegate { diag.StartCheckRun(); }, 60));
            Command(8);
            string f2 = Forms(); L(f2);
            Check(Count(f2, "AutoHoleCheckForm") == 1 && f2.Contains("窗口一直开着"), "查完再点「配孔检查」：还是那一个窗口，结果留着");

            Command(6);
            string f3 = Forms(); L(f3);
            Check(Count(f3, "AutoHoleForm") == 1 && Count(f3, "AutoHoleCheckForm") == 1, "检查窗口开着时点「自动打孔」：打孔窗口开出来，检查窗口还在");
            Command(9);
            string f4 = Forms(); L(f4);
            Check(Count(f4, "HoleEditForm") == 1 && Count(f4, "AutoHoleForm") == 0 && Count(f4, "AutoHoleCheckForm") == 1, "再点「改孔型」：打孔窗口（没选东西）换成改孔型，检查窗口还在");

            Command(2); Command(2);
            string f5 = Forms(); L(f5);
            Check(Count(f5, "AutoPanelForm") == 1 && Count(f5, "HoleEditForm") == 0 && Count(f5, "AutoHoleCheckForm") == 1, "「型材自动填充」点两次：只有一个填充窗口，改孔型换掉，检查窗口还在");

            Command(5); Command(5);
            string f6 = Forms(); L(f6);
            Check(Count(f6, "FormatConvertForm") == 1, "「批量格式转换」点两次只有一个转换窗口");

            L(Wait(delegate { diag.StartCloseForms(); }, 30));
            try { asm.Close(false); } catch { }
        } catch (Exception e) { L("E2E 异常：" + e); fail++; }
        L(fail == 0 ? "WINDOW-REUSE E2E OK" : "WINDOW-REUSE E2E FAILED " + fail);
        return fail == 0 ? 0 : 1;
    }
}
