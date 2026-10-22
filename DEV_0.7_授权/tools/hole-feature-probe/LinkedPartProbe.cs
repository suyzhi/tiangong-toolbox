using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using TianGongCadSuite;
using A=SolidEdgeAssembly;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;
using F=SolidEdgeFramework;

// 0.7.11：「带链接的零件打不上孔，断开链接才能打」现场复现。**只在副本上跑**（不保存）。
//   ① 列出装配里每个零件的链接状态：HasInterpartLinks、InterpartLinks（来源 / 冻结）、建链接时的装配、零件间复制的状态、失败的特征
//   ② 对指定零件（默认名字含 M30-02-2）和一个没有链接的零件各打一个 Φ6.6 通孔：
//      A 装配上下文直接打（插件不保持关联时的路径）；B 原位编辑里打（保持关联时的路径）；C 冻结链接后再打；D 解冻后再打
// 用法: LinkedPartProbe.exe <输出目录> <装配副本路径> [零件名关键字]
class LinkedPartProbe {
    static F.Application app;
    static void L(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    static void Pump(int n){ for (int i = 0; i < n; i++) { try { app.DoIdle(); } catch { } try { Application.DoEvents(); } catch { } Thread.Sleep(60); } }
    static string Hr(Exception e){ return "0x" + e.HResult.ToString("X8") + " " + e.Message; }
    static T Try<T>(Func<T> f, T dflt){ try { return f(); } catch { return dflt; } }

    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        TianGongCadSuite.Licensing.LicenseTestHooks.GateOverride = 0;
        ProbeFilter2.Register();
        string asmPath = args[1];
        string want = args.Length > 2 ? args[2] : "M30-02-2.par";
        try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); }
        catch (Exception e) { L("连不上 CAD：" + e.Message); return 2; }
        try { app.DisplayAlerts = false; } catch { }
        L("已连上 CAD " + app.Version);
        A.AssemblyDocument asm = null;
        try {
            var t0 = DateTime.Now;
            asm = (A.AssemblyDocument)app.Documents.Open(asmPath);
            Pump(20);
            L("打开 " + Path.GetFileName(asmPath) + " 用时 " + (DateTime.Now - t0).TotalSeconds.ToString("0.0") + "s");
            var warn = new List<string>();
            var parts = AssemblyPart.List(asm, warn);
            L("零件实例 " + parts.Count + "，警告 " + warn.Count);
            foreach (var w in warn.Take(10)) L("  警告 " + w);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AssemblyPart target = null, control = null;
            L(""); L("① 链接状态");
            foreach (var ap in parts) {
                string full = Try(() => ap.Part.FullName, "?");
                if (!seen.Add(full)) continue;
                bool has = false; try { ap.Part.HasInterpartLinks(ref has); } catch { }
                string ctx = ""; try { ap.Part.GetInContextAssemblyNameForInterpartLinks(ref ctx); } catch (Exception e) { ctx = "(" + Hr(e) + ")"; }
                int ipc = Try(() => ap.Part.Constructions.InterpartConstructions.Count, -1);
                int links = Try(() => ((F.InterpartLinks)ap.Part.InterpartLinks).Count, -1);
                bool inCopy = full.IndexOf(Path.GetDirectoryName(asmPath), StringComparison.OrdinalIgnoreCase) >= 0;
                string line = (has ? "[链接] " : "       ") + ap.Name + " | " + Path.GetFileName(full) + (inCopy ? "" : "  ★不在副本目录：" + full)
                    + " | HasInterpartLinks=" + has + " 链接数=" + links + " 零件间复制=" + ipc + (ctx.Length > 0 ? " 建链装配=" + ctx : "")
                    + " ReadOnly=" + Try(() => ap.Part.ReadOnly.ToString(), "?");
                L(line);
                if (has) Detail(ap.Part);
                if (target == null && full.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0) target = ap;
                if (control == null && !has && !ap.Nested) control = ap;
            }
            L("");
            if (target == null) { L("找不到含「" + want + "」的零件"); return 3; }
            L("② 目标零件 " + target.Name + (target.Nested ? "（子装配里）" : ""));
            Detail(target.Part);
            Features(target.Part);
            if (control != null) { L(""); L("── 对照：没有链接的零件 " + control.Name); RunModes(asm, control, false); }
            L(""); L("── 目标：" + target.Name);
            RunModes(asm, target, true);
        } catch (Exception e) {
            L("PROBE 异常：" + e.GetType().Name + " " + Hr(e) + "\r\n" + e.StackTrace);
        } finally {
            try { if (asm != null) asm.Close(false); } catch { }
            Pump(4);
        }
        L("PROBE DONE");
        return 0;
    }

    static void Detail(P.PartDocument part){
        try {
            var links = (F.InterpartLinks)part.InterpartLinks;
            for (int i = 1; i <= links.Count && i <= 12; i++) {
                var lk = (F.InterpartLink)links.Item(i);
                string a = "", b = ""; bool frozen = false;
                try { lk.GetInfo(out a, out b); } catch (Exception e) { a = "(" + Hr(e) + ")"; }
                try { lk.IsFrozen(out frozen); } catch { }
                L("      链接 " + i + "：" + a + " | " + b + " 冻结=" + frozen);
            }
        } catch (Exception e) { L("      读链接失败 " + Hr(e)); }
        try {
            var all = part.Constructions.InterpartConstructions;
            for (int i = 1; i <= all.Count && i <= 12; i++) {
                var c = (P.InterpartConstruction)all.Item(i);
                object d = null; string st = "?";
                try { st = c.GetStatusEx(out d).ToString(); } catch (Exception e) { st = "(" + Hr(e) + ")"; }
                L("      零件间复制 " + i + "：" + Try(() => c.Name, "?") + " 状态=" + st + " 断开=" + Try(() => c.IsBroken.ToString(), "?") + (d == null ? "" : " 说明=" + d));
            }
        } catch (Exception e) { L("      读零件间复制失败 " + Hr(e)); }
    }

    static void Features(P.PartDocument part){
        try {
            var tree = (P.EdgebarFeatures)((dynamic)part).DesignEdgebarFeatures;
            int bad = 0;
            for (int i = 1; i <= tree.Count; i++) {
                object f = tree.Item(i); string nm = Try(() => (string)((dynamic)f).Name, "?"), st = "";
                try { object d; st = ((dynamic)f).GetStatusEx(out d).ToString(); } catch { try { st = ((dynamic)f).Status.ToString(); } catch { st = "-"; } }
                bool ok = st == "-" || st.IndexOf("OK", StringComparison.OrdinalIgnoreCase) >= 0 || st == "1216476310";
                if (!ok) bad++;
                if (!ok || i <= 3 || i > tree.Count - 6) L("      特征 " + i + "/" + tree.Count + " " + nm + " 状态=" + st);
            }
            L("      特征树 " + tree.Count + " 项，状态不正常 " + bad + " 项");
        } catch (Exception e) { L("      读特征树失败 " + Hr(e)); }
    }

    // 最大那张平面上、面积最大那个三角形的重心（一定在面上）
    static G.Face BigPlane(P.PartDocument part, out V3 centre){
        centre = new V3();
        G.Face best = null; double area = 0;
        var body = (G.Body)((P.Model)part.Models.Item(1)).Body;
        foreach (G.Face f in (G.Faces)body.get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll)) {
            if (!(f.Geometry is G.Plane)) continue;
            double a = Try(() => f.Area, 0);
            if (a > area) { area = a; best = f; }
        }
        if (best == null) return null;
        int n = 0; Array pts = new double[0];
        object nrm = null, tex = null; best.GetFacetData(0.0005, out n, ref pts, out nrm, out tex);
        double bestTri = 0;
        for (int t = 0; t + 8 < pts.Length; t += 9) {
            var p0 = P3(pts, t); var p1 = P3(pts, t + 3); var p2 = P3(pts, t + 6);
            double ta = (p1 - p0).Cross(p2 - p0).Length;
            if (ta > bestTri) { bestTri = ta; centre = (p0 + p1 + p2) * (1.0 / 3.0); }
        }
        return best;
    }
    static V3 P3(Array a, int i){ return new V3(Convert.ToDouble(a.GetValue(i)), Convert.ToDouble(a.GetValue(i + 1)), Convert.ToDouble(a.GetValue(i + 2))); }

    static int HoleCount(P.PartDocument part){ return Try(() => ((P.Model)part.Models.Item(1)).Holes.Count, -1); }

    static void RunModes(A.AssemblyDocument asm, AssemblyPart ap, bool freezeTest){
        Mode("A 装配上下文", asm, ap, false);
        Mode("B 原位编辑", asm, ap, true);
        if (!freezeTest) return;
        bool has = false; try { ap.Part.HasInterpartLinks(ref has); } catch { }
        if (!has) return;
        try { ap.Part.FreezeAllInterpartLinks(); L("   已 FreezeAllInterpartLinks"); } catch (Exception e) { L("   Freeze 失败 " + Hr(e)); }
        Pump(6);
        Mode("C 冻结后·装配上下文", asm, ap, false);
        Mode("C 冻结后·原位编辑", asm, ap, true);
        try { ap.Part.ThawAllInterpartLinks(); L("   已 ThawAllInterpartLinks"); } catch (Exception e) { L("   Thaw 失败 " + Hr(e)); }
        Pump(6);
        Mode("D 解冻后·装配上下文", asm, ap, false);
    }

    static void Mode(string label, A.AssemblyDocument asm, AssemblyPart ap, bool inPlace){
        L(""); L("   [" + label + "]");
        NativeHoleLink.InPlace s = null;
        try {
            V3 local; var face = BigPlane(ap.Part, out local);
            if (face == null) { L("     没有平面"); return; }
            var target = AutoHoleReader.ReadTarget(ap.Reference(face));
            var centre = target.Placement.Point(local);
            if (inPlace) {
                string why;
                var chain = NativeHoleLink.ChainOf(asm, target.Pick, target.Part, target.Placement, out why);
                if (chain == null) { L("     认不出实例：" + why); return; }
                var t0 = DateTime.Now;
                s = NativeHoleLink.Enter(app, asm, chain, out why);
                if (s == null) { L("     进不了原位：" + why); return; }
                L("     进原位 " + (DateTime.Now - t0).TotalMilliseconds.ToString("0") + "ms，活动文档 " + Try(() => ((dynamic)app.ActiveDocument).Name, "?")
                  + " 同一对象=" + ReferenceEquals(s.Part, ap.Part) + " InPlaceActivated=" + Try(() => s.Part.InPlaceActivated.ToString(), "?"));
                target = AutoHoleWriter.AnchorToActivePart(target);
            }
            int before = HoleCount(target.Part);
            var spec = new HoleSpec { Kind = HoleKind.Through, HoleDiameter = 6.6 };
            var r = AutoHoleWriter.Drill(target, spec, new[]{ centre });
            Pump(4);
            L("     建了 " + r.Created + "/" + r.Requested + "，孔 " + before + " → " + HoleCount(target.Part) + (r.Failures.Count > 0 ? "，失败：" + string.Join("；", r.Failures) : ""));
            L("     RESULT " + label + "：" + (r.Created > 0 ? "OK" : "FAIL"));
        } catch (Exception e) {
            L("     异常 " + e.GetType().Name + " " + Hr(e) + "\r\n" + e.StackTrace);
            L("     RESULT " + label + "：EXC");
        } finally {
            if (s != null) { try { NativeHoleLink.Leave(app, s); } catch { } Pump(4); }
        }
    }
}

[ComImport, Guid("00000016-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IProbeMessageFilter2 {
    [PreserveSig] int HandleInComingCall(int type, IntPtr task, int ticks, IntPtr info);
    [PreserveSig] int RetryRejectedCall(IntPtr task, int ticks, int reject);
    [PreserveSig] int MessagePending(IntPtr task, int ticks, int pending);
}
sealed class ProbeFilter2 : IProbeMessageFilter2 {
    [DllImport("ole32.dll")] static extern int CoRegisterMessageFilter(IProbeMessageFilter2 f, out IProbeMessageFilter2 old);
    static ProbeFilter2 keep;
    public static void Register(){ keep = new ProbeFilter2(); IProbeMessageFilter2 old; CoRegisterMessageFilter(keep, out old); }
    public int HandleInComingCall(int a, IntPtr b, int c, IntPtr d){ return 0; }
    public int RetryRejectedCall(IntPtr a, int ticks, int reject){ return ticks < 120000 ? 200 : -1; }
    public int MessagePending(IntPtr a, int b, int c){ return 2; }
}
