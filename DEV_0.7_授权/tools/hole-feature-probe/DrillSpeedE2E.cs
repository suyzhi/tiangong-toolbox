using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using TianGongCadSuite;
using A=SolidEdgeAssembly;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;
using F=SolidEdgeFramework;
using S=SolidEdgeFrameworkSupport;

// 进程外跑 tests/AutoHoleSpeedTests.cs（DrillSpeed）。编译时两个文件一起给 csc。
// 用法: DrillSpeedE2E.exe <输出目录> [模式，逗号分隔：link,nolink,mixed,bars4]
class DrillSpeedE2E {
    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = Encoding.UTF8;
        TianGongCadSuite.Licensing.LicenseTestHooks.GateOverride = 0;
        SpeedFilter.Register();
        F.Application app;
        try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); }
        catch (Exception e) { Console.WriteLine("连不上 CAD：" + e.Message); return 2; }
        return DrillSpeed.Run(app, args.Length > 0 ? args[0] : ".", args.Length > 1 ? args[1] : "", false) == 0 ? 0 : 1;
    }
}

// 进程外调 CAD：CAD 忙（RPC_E_CALL_REJECTED / RETRYLATER）时由 COM 自动等，最多 120 秒（同 FenceHoleE2E）
[ComImport, Guid("00000016-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface ISpeedMessageFilter {
    [PreserveSig] int HandleInComingCall(int type, IntPtr task, int ticks, IntPtr info);
    [PreserveSig] int RetryRejectedCall(IntPtr task, int ticks, int reject);
    [PreserveSig] int MessagePending(IntPtr task, int ticks, int pending);
}
sealed class SpeedFilter : ISpeedMessageFilter {
    [DllImport("ole32.dll")] static extern int CoRegisterMessageFilter(ISpeedMessageFilter f, out ISpeedMessageFilter old);
    static SpeedFilter keep;
    public static void Register(){ keep = new SpeedFilter(); ISpeedMessageFilter old; CoRegisterMessageFilter(keep, out old); }
    public int HandleInComingCall(int a, IntPtr b, int c, IntPtr d){ return 0; }
    public int RetryRejectedCall(IntPtr a, int ticks, int reject){ return ticks < 120000 ? 50 : -1; }
    public int MessagePending(IntPtr a, int b, int c){ return 2; }
}
