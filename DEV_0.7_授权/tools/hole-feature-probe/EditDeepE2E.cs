using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using TianGongCadSuite;
using A=SolidEdgeAssembly;
using F=SolidEdgeFramework;

// 0.10.2：「改孔型」改**深层子装配里、带零件间链接**的零件上已有的孔（现场焊接台车：002 板在第 3 层子装配里，39 个零件间复制）。
// 打孔在这种零件上只能进原位编辑才打得上（0.7.11）；改孔型从来没在这种零件上实测过。**只在副本上跑**（不保存）。
//   在顶层装配里按配孔检查的孔记录点 002 板上的一个孔 → 改成 M6 圆柱沉孔 → 应用 → 看改成没有 / 报了什么 → 撤销修改。
// 用法: EditDeepE2E.exe <输出目录> <装配副本> <零件名关键字> [孔径 mm，默认任意]
class EditDeepE2E {
    static F.Application app;
    static int fail;
    static void L(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    static void Check(bool ok, string what){ L((ok ? "PASS: " : "FAIL: ") + what); if (!ok) fail++; }
    static void Pump(int n){ for (int i = 0; i < n; i++) { try { app.DoIdle(); } catch { } Application.DoEvents(); Thread.Sleep(60); } }

    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        TianGongCadSuite.Licensing.LicenseTestHooks.GateOverride = 0;
        EditFilter.Register();
        string asmPath = args[1], want = args[2];
        double dia = args.Length > 3 ? double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 0;
        try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); } catch (Exception e) { L("连不上 CAD：" + e.Message); return 2; }
        try { app.DisplayAlerts = false; } catch { }
        A.AssemblyDocument asm = null;
        HoleEditForm ef = null;
        try {
            asm = (A.AssemblyDocument)app.Documents.Open(asmPath); Pump(20);
            List<string> warn; List<HoleRecord> shafts; List<AssemblyPart> parts;
            var holes = AutoHoleWriter.CollectAssemblyHoles(asm, out warn, out shafts, out parts);
            var mine = holes.Where(h => h.PartName.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0 && (dia <= 0 || Math.Abs(h.DiameterMm - dia) < 0.05)).ToList();
            L("目标零件上的孔记录 " + mine.Count + " 个（" + string.Join("、", mine.Select(h => "Φ" + h.DiameterMm.ToString("0.##")).Distinct().Take(8).ToArray()) + "）");
            if (mine.Count == 0) { L("没有可点的孔"); return 3; }
            var ap = parts.First(p => p.Name == mine[0].PartName);
            bool has = false; try { ap.Part.HasInterpartLinks(ref has); } catch { }
            L("目标 " + ap.Name + "　HasInterpartLinks=" + has);
            ef = new HoleEditForm(app, asm); ef.SuppressDialogs = true; ef.Show(); Pump(6);
            HoleRecord picked = null;
            foreach (var h in mine) {
                var sel = h.Selection; if (sel == null) continue;
                try { ef.AcceptPick(sel); } catch (Exception e) { L("   点孔异常 " + e.Message); }
                Pump(6);
                if (ef.TargetCount > 0) { picked = h; break; }
                L("   这个孔认不出孔特征：" + ef.StatusForTest);
            }
            Check(picked != null, "点到一个能改的孔特征（" + ef.StatusForTest + "）");
            if (picked == null) return 1;
            L("   点的是 Φ" + picked.DiameterMm.ToString("0.##") + "　预览：" + ef.PreviewText.Replace("\r\n", " ⏎ "));
            ef.WholeGroup = false;
            ef.SetSpecForTest(HoleKind.Counterbore, "M6", 0); Pump(2);
            object startDoc = app.ActiveDocument;
            var t0 = DateTime.Now;
            ef.ApplyForTest(); Pump(10);
            L("   应用 " + (DateTime.Now - t0).TotalSeconds.ToString("0.0") + "s：" + (ef.LastResult ?? "").Replace("\r\n", " ⏎ "));
            L("   状态：" + ef.StatusForTest);
            Check((ef.LastResult ?? "").Contains("：1 个孔") || (ef.LastResult ?? "").IndexOf("个孔", StringComparison.Ordinal) > 0 && !(ef.LastResult ?? "").Contains("失败"), "改孔型改成了、没有失败项");
            Check(ReferenceEquals(app.ActiveDocument, startDoc), "改完回到顶层装配（没停在原位编辑里）");
            Check(ef.CanUndo, "可以撤销修改");
            t0 = DateTime.Now;
            ef.UndoForTest(); Pump(10);
            L("   撤销 " + (DateTime.Now - t0).TotalSeconds.ToString("0.0") + "s：" + (ef.LastResult ?? "").Replace("\r\n", " ⏎ "));
            Check(!(ef.LastResult ?? "").Contains("中断") && !(ef.LastResult ?? "").Contains("没能"), "撤销修改没有出错");
        } catch (Exception e) { L("E2E 异常：" + e); fail++; }
        finally {
            try { if (ef != null) ef.Close(); } catch { }
            try { if (asm != null) asm.Close(false); } catch { }
            Pump(4);
        }
        L(fail == 0 ? "EDIT-DEEP E2E OK" : "EDIT-DEEP E2E FAILED " + fail);
        return fail == 0 ? 0 : 1;
    }
}

[ComImport, Guid("00000016-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IEditMessageFilter {
    [PreserveSig] int HandleInComingCall(int type, IntPtr task, int ticks, IntPtr info);
    [PreserveSig] int RetryRejectedCall(IntPtr task, int ticks, int reject);
    [PreserveSig] int MessagePending(IntPtr task, int ticks, int pending);
}
sealed class EditFilter : IEditMessageFilter {
    [DllImport("ole32.dll")] static extern int CoRegisterMessageFilter(IEditMessageFilter f, out IEditMessageFilter old);
    static EditFilter keep;
    public static void Register(){ keep = new EditFilter(); IEditMessageFilter old; CoRegisterMessageFilter(keep, out old); }
    public int HandleInComingCall(int a, IntPtr b, int c, IntPtr d){ return 0; }
    public int RetryRejectedCall(IntPtr a, int ticks, int reject){ return ticks < 120000 ? 200 : -1; }
    public int MessagePending(IntPtr a, int b, int c){ return 2; }
}
