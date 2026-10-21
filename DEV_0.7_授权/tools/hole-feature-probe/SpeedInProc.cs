using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using F=SolidEdgeFramework;

// 0.7.10 打孔计时（进程内）：连上 CAD，找到插件（开发构建，带 Diagnostics 测试入口），让它在 CAD 自己的线程里跑
// PanelTests.exe --autohole-speed（= tests/AutoHoleSpeedTests.cs 的 DrillSpeed，和进程外 DrillSpeedE2E 同一套场景），
// 等结果、打印出来。量到的就是用户点「开始打孔」时插件的真实速度。
// 前提：注册的是开发构建（tools/install.ps1 -LibraryPath <开发构建 DLL>），PanelTests.exe 在 DLL 旁边。
// 用法: SpeedInProc.exe <输出目录>     （经 run-cad-queue.ps1 在私有桌面跑）
class SpeedInProc {
    const string AddInGuid = "{8C05165C-65A4-4EF2-A138-508589D82004}";
    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = Encoding.UTF8;
        string outDir = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
        Directory.CreateDirectory(outDir);
        InProcFilter.Register();
        F.Application app;
        try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); }
        catch (Exception e) { Console.WriteLine("连不上 CAD：" + e.Message); return 2; }
        // 插件要等 CAD 打开文档才真正连上（同 tools/native-test.ps1：先建一个空装配）
        object blank = null;
        try { blank = app.Documents.Add("SolidEdge.AssemblyDocument"); } catch (Exception e) { Console.WriteLine("建空装配失败：" + e.Message); }
        dynamic addin = null;
        var until = DateTime.Now.AddSeconds(60);
        while (addin == null && DateTime.Now < until) {
            try { foreach (dynamic a in (dynamic)app.AddIns) { if (string.Equals((string)a.GUID, AddInGuid, StringComparison.OrdinalIgnoreCase)) { addin = a; break; } } } catch { }
            if (addin == null) Thread.Sleep(1000);
        }
        if (addin == null) { Console.WriteLine("找不到插件"); return 2; }
        addin.Connect = true;
        dynamic diag = null;
        for (int i = 0; i < 30 && diag == null; i++) { try { diag = addin.Object; } catch { } if (diag == null) { try { app.DoIdle(); } catch { } Thread.Sleep(1000); } }
        if (diag == null) { Console.WriteLine("插件没有给出 Diagnostics（注册的不是开发构建？）"); return 2; }
        Console.WriteLine("插件 DLL：" + (string)diag.LoadedLibrary);
        diag.StartSpeedTests(outDir);
        string text = "";
        var deadline = DateTime.Now.AddMinutes(12);
        while (DateTime.Now < deadline) {
            Thread.Sleep(2000);
            try { text = (string)diag.Result; } catch { text = ""; }
            if (!string.IsNullOrEmpty(text)) break;
        }
        if (string.IsNullOrEmpty(text)) { Console.WriteLine("进程内计时超时"); return 1; }
        Console.WriteLine(text);
        try { ((dynamic)blank).Close(false); } catch { }
        return text.Contains("EXIT 0") ? 0 : 1;
    }
}

[ComImport, Guid("00000016-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IInProcMessageFilter {
    [PreserveSig] int HandleInComingCall(int type, IntPtr task, int ticks, IntPtr info);
    [PreserveSig] int RetryRejectedCall(IntPtr task, int ticks, int reject);
    [PreserveSig] int MessagePending(IntPtr task, int ticks, int pending);
}
sealed class InProcFilter : IInProcMessageFilter {
    [DllImport("ole32.dll")] static extern int CoRegisterMessageFilter(IInProcMessageFilter f, out IInProcMessageFilter old);
    static InProcFilter keep;
    public static void Register(){ keep = new InProcFilter(); IInProcMessageFilter old; CoRegisterMessageFilter(keep, out old); }
    public int HandleInComingCall(int a, IntPtr b, int c, IntPtr d){ return 0; }
    public int RetryRejectedCall(IntPtr a, int ticks, int reject){ return ticks < 600000 ? 200 : -1; }   // CAD 在跑测试时一直忙：等着
    public int MessagePending(IntPtr a, int b, int c){ return 2; }
}
