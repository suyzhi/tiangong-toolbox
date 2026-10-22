using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using TianGongCadSuite;
using A=SolidEdgeAssembly;
using P=SolidEdgePart;
using F=SolidEdgeFramework;

// 0.7.12：怎么进原位编辑才不留下子文档窗口。每种进法：进 → 记窗口 / 天工说的顶层 → 关闭并返回 → 记窗口。不保存。
// 用法: InPlaceDirectProbe.exe <输出目录> <T 装配> <子装配里的零件关键字>
class InPlaceDirectProbe {
    static F.Application app;
    static A.AssemblyDocument top;
    static void L(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    static void Pump(int n){ for (int i = 0; i < n; i++) { try { app.DoIdle(); } catch { } try { Application.DoEvents(); } catch { } Thread.Sleep(60); } }
    static string N(object d){ try { return (string)((dynamic)d).Name; } catch { return "?"; } }
    static string WinList(){ var l = new List<string>(); try { foreach (object w in app.Windows) l.Add((string)((dynamic)w).Caption); } catch { } return l.Count + "［" + string.Join("｜", l.ToArray()) + "］"; }
    static bool WaitActive(Func<bool> ok, int ms){ var until = DateTime.UtcNow.AddMilliseconds(ms); while (DateTime.UtcNow < until) { try { if (ok()) return true; } catch { } Pump(1); } return false; }
    static string TopOf(object d){ try { object t = null, o = null; ((dynamic)d).GetTopDocumentAndSubOccurrenceOfIPADoc(out t, out o); return N(t); } catch (Exception e) { return "?" + e.Message; } }

    static void Try(string tag, Action select, string wantName){
        L(""); L("── " + tag + "　开始前窗口 " + WinList());
        try { app.ActiveSelectSet.RemoveAll(); } catch { }
        try { select(); L("   选中 " + app.ActiveSelectSet.Count + " 个"); } catch (Exception e) { L("   选不中 0x" + e.HResult.ToString("X8") + " " + e.Message); return; }
        try { app.StartCommand((F.SolidEdgeCommandConstants)57872); } catch (Exception e) { L("   命令失败 " + e.Message); return; }
        bool ok = WaitActive(() => N(app.ActiveDocument) == wantName, 10000);
        object a = app.ActiveDocument;
        string ip = "?"; try { ip = ((bool)((dynamic)a).InPlaceActivated).ToString(); } catch { }
        L("   进" + (ok ? "成了" : "不去（10 秒）") + "：活动 " + N(a) + "　InPlace=" + ip + "　天工说顶层 " + TopOf(a) + "　窗口 " + WinList());
        // 退回顶层（一级一级关闭并返回）
        for (int k = 0; k < 4 && !ReferenceEquals(app.ActiveDocument, top); k++) {
            object before = app.ActiveDocument;
            try { app.StartCommand((F.SolidEdgeCommandConstants)40013); } catch (Exception e) { L("   返回失败 " + e.Message); break; }
            WaitActive(() => !ReferenceEquals(app.ActiveDocument, before), 10000);
            L("   关闭并返回 → 活动 " + N(app.ActiveDocument));
        }
        Pump(10);
        L("   退回后窗口 " + WinList());
    }

    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        IdFilter.Register();
        try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); } catch (Exception e) { L("连不上 CAD：" + e.Message); return 2; }
        try { app.DisplayAlerts = false; } catch { }
        try {
            top = (A.AssemblyDocument)app.Documents.Open(args[1]); Pump(20);
            string want = args[2];
            // 子装配里的零件：顶层实例 + 它的 SubOccurrence
            A.Occurrence subAsm = null; A.SubOccurrence leafSub = null; A.Occurrence leafInSub = null; A.Occurrence direct = null;
            for (int i = 1; i <= top.Occurrences.Count && (leafSub == null || direct == null); i++) {
                var o = top.Occurrences.Item(i);
                bool sub = false; try { sub = o.Subassembly; } catch { }
                if (!sub) { if (direct == null) direct = o; continue; }
                if (leafSub != null) continue;
                var subs = o.SubOccurrences;
                for (int j = 1; j <= subs.Count; j++) {
                    var so = (A.SubOccurrence)subs.Item(j);
                    if ((so.SubOccurrenceFileName ?? "").IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0) {
                        subAsm = o; leafSub = so;
                        var d = (A.AssemblyDocument)o.OccurrenceDocument;
                        for (int k = 1; k <= d.Occurrences.Count; k++) if ((d.Occurrences.Item(k).OccurrenceFileName ?? "").IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0) leafInSub = d.Occurrences.Item(k);
                        break;
                    }
                }
            }
            if (leafSub == null) { L("找不到子装配里的 " + want); return 3; }
            string partName = Path.GetFileName(leafSub.SubOccurrenceFileName);
            L("子装配 " + subAsm.Name + " 里的 " + leafSub.Name + "；顶层直接零件 " + (direct == null ? "无" : direct.Name));

            Try("M1 选 SubOccurrence 直接进", () => app.ActiveSelectSet.Add(leafSub), partName);
            Try("M2 选 SubOccurrence.ThisAsOccurrence 直接进", () => app.ActiveSelectSet.Add(leafSub.ThisAsOccurrence), partName);
            Try("M3 选 SubOccurrence.Reference 直接进", () => app.ActiveSelectSet.Add(leafSub.Reference), partName);
            Try("M4 一级一级（现在的做法）第 1 级", () => subAsm.Select(true), Path.GetFileName(subAsm.OccurrenceFileName));
            if (direct != null) Try("M5 顶层直接零件（1 级）", () => direct.Select(true), Path.GetFileName(direct.OccurrenceFileName));
        } catch (Exception e) { L("异常 " + e.Message + "\r\n" + e.StackTrace); }
        finally { try { if (top != null) top.Close(false); } catch { } }
        L("INPLACE DIRECT PROBE DONE");
        return 0;
    }
}
[ComImport, Guid("00000016-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IIdMessageFilter {
    [PreserveSig] int HandleInComingCall(int type, IntPtr task, int ticks, IntPtr info);
    [PreserveSig] int RetryRejectedCall(IntPtr task, int ticks, int reject);
    [PreserveSig] int MessagePending(IntPtr task, int ticks, int pending);
}
sealed class IdFilter : IIdMessageFilter {
    [DllImport("ole32.dll")] static extern int CoRegisterMessageFilter(IIdMessageFilter f, out IIdMessageFilter old);
    static IdFilter keep;
    public static void Register(){ keep = new IdFilter(); IIdMessageFilter old; CoRegisterMessageFilter(keep, out old); }
    public int HandleInComingCall(int a, IntPtr b, int c, IntPtr d){ return 0; }
    public int RetryRejectedCall(IntPtr a, int ticks, int reject){ return ticks < 120000 ? 200 : -1; }
    public int MessagePending(IntPtr a, int b, int c){ return 2; }
}
