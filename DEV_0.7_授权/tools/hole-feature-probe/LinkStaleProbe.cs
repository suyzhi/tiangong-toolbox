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

// 0.7.11：零件间链接"过期"（来源零件挪了 / 改了、打孔零件里的零件间复制还没更新）时还能不能打孔。**只在副本上跑**（不保存）。
//   ① 打开建链的那个装配，打孔零件里每个零件间复制的状态
//   ② 挪一个来源零件（顶层实例 Move）→ 再看状态 → 不更新链接直接打孔（装配上下文 / 原位）
//   ③ 更新链接（InterpartConstruction.Update / UpdateAll）→ 再打
// 用法: LinkStaleProbe.exe <输出目录> <装配副本路径> <打孔零件名关键字> [挪动毫米]
class LinkStaleProbe {
    static F.Application app;
    static void L(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    static void Pump(int n){ for (int i = 0; i < n; i++) { try { app.DoIdle(); } catch { } try { Application.DoEvents(); } catch { } Thread.Sleep(60); } }
    static string Hr(Exception e){ return "0x" + e.HResult.ToString("X8") + " " + e.Message; }
    static T Try<T>(Func<T> f, T dflt){ try { return f(); } catch { return dflt; } }

    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        TianGongCadSuite.Licensing.LicenseTestHooks.GateOverride = 0;
        StaleFilter.Register();
        string asmPath = args[1], want = args[2];
        double moveMm = args.Length > 3 ? double.Parse(args[3]) : 3;
        try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); }
        catch (Exception e) { L("连不上 CAD：" + e.Message); return 2; }
        try { app.DisplayAlerts = false; } catch { }
        A.AssemblyDocument asm = null;
        try {
            asm = (A.AssemblyDocument)app.Documents.Open(asmPath);
            Pump(20);
            L("打开 " + Path.GetFileName(asmPath));
            var parts = AssemblyPart.List(asm, new List<string>());
            var target = parts.FirstOrDefault(p => Try(() => p.Part.FullName, "").IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0);
            if (target == null) { L("找不到目标"); return 3; }
            string ctx = ""; try { target.Part.GetInContextAssemblyNameForInterpartLinks(ref ctx); } catch { }
            L("目标 " + target.Name + " 建链装配=" + ctx);
            Copies(target.Part, "挪动前");
            // 来源零件：零件间复制名字「孔位链接-<零件名>」；在顶层实例里找
            A.Occurrence src = null;
            var names = new List<string>();
            var all = target.Part.Constructions.InterpartConstructions;
            for (int i = 1; i <= all.Count; i++) { string n = Try(() => ((P.InterpartConstruction)all.Item(i)).Name, ""); if (n.StartsWith("孔位链接-")) names.Add(n.Substring(5)); }
            foreach (A.Occurrence o in asm.Occurrences) {
                string fn = Try(() => Path.GetFileNameWithoutExtension(o.OccurrenceFileName), "");
                if (names.Contains(fn, StringComparer.OrdinalIgnoreCase)) { src = o; break; }
            }
            if (src == null) {
                L("顶层里找不到来源零件（" + string.Join("，", names.ToArray()) + "）；顶层实例：");
                foreach (A.Occurrence o in asm.Occurrences) L("   " + Try(() => o.Name, "?"));
                return 4;
            }
            Array m0 = new double[16]; src.GetMatrix(ref m0);
            L("来源 " + src.Name + " 位置 (" + Mm(m0, 12) + ", " + Mm(m0, 13) + ", " + Mm(m0, 14) + ")mm");

            Drill(asm, target, "挪动前·装配上下文", false);

            try { src.Move(moveMm / 1000.0, 0, 0); } catch (Exception e) { L("Move 失败 " + Hr(e)); }
            Pump(10);
            Array m1 = new double[16]; src.GetMatrix(ref m1);
            L("挪动后位置 (" + Mm(m1, 12) + ", " + Mm(m1, 13) + ", " + Mm(m1, 14) + ")mm");
            parts = AssemblyPart.List(asm, new List<string>());
            target = parts.First(p => Try(() => p.Part.FullName, "").IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0);
            Copies(target.Part, "挪动后");
            Drill(asm, target, "挪动后·不更新·装配上下文", false);
            parts = AssemblyPart.List(asm, new List<string>());
            target = parts.First(p => Try(() => p.Part.FullName, "").IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0);
            Drill(asm, target, "挪动后·不更新·原位", true);

            try { asm.UpdateAll(); L("已 UpdateAll"); } catch (Exception e) { L("UpdateAll 失败 " + Hr(e)); }
            Pump(10);
            parts = AssemblyPart.List(asm, new List<string>());
            target = parts.First(p => Try(() => p.Part.FullName, "").IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0);
            Copies(target.Part, "UpdateAll 后");
            Drill(asm, target, "UpdateAll 后·装配上下文", false);
        } catch (Exception e) {
            L("PROBE 异常：" + e.GetType().Name + " " + Hr(e) + "\r\n" + e.StackTrace);
        } finally {
            try { if (asm != null) asm.Close(false); } catch { }
            Pump(4);
        }
        L("PROBE DONE");
        return 0;
    }
    static string Mm(Array m, int i){ return (Convert.ToDouble(m.GetValue(i)) * 1000).ToString("0.###"); }

    static void Copies(P.PartDocument part, string when){
        try {
            var all = part.Constructions.InterpartConstructions;
            var parts = new List<string>();
            for (int i = 1; i <= all.Count; i++) {
                var c = (P.InterpartConstruction)all.Item(i);
                object d = null; string st;
                try { st = c.GetStatusEx(out d).ToString().Replace("igFeature", ""); } catch (Exception e) { st = "(" + Hr(e) + ")"; }
                parts.Add(Try(() => c.Name, "?") + "=" + st + (d == null ? "" : "[" + d + "]"));
            }
            var model = (P.Model)part.Models.Item(1);
            string ms = ""; try { ms = ((dynamic)model).HealthStatus.ToString(); } catch { ms = "?"; }
            L("   [" + when + "] 零件间复制：" + string.Join("，", parts.ToArray()) + "　模型状态=" + ms);
        } catch (Exception e) { L("   [" + when + "] 读不出 " + Hr(e)); }
    }

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
        int n = 0; Array pts = new double[0]; object nrm, tex;
        best.GetFacetData(0.0005, out n, ref pts, out nrm, out tex);
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

    static int shift = 0;
    static void Drill(A.AssemblyDocument asm, AssemblyPart ap, string label, bool inPlace){
        L(""); L("   [" + label + "]");
        NativeHoleLink.InPlace s = null;
        try {
            V3 local; var face = BigPlane(ap.Part, out local);
            if (face == null) { L("     没有平面"); return; }
            var target = AutoHoleReader.ReadTarget(ap.Reference(face));
            // 每次换个位置（同一个点打过就是"已经有孔"）：沿面内挪 8mm × 次数
            V3 nrm = target.Placement.InverseNormal(target.Plane.Normal).Unit();
            V3 u = Math.Abs(nrm.X) < 0.9 ? new V3(1, 0, 0) : new V3(0, 1, 0);
            u = (u - nrm * u.Dot(nrm)).Unit();
            local = local + u * (0.008 * shift++);
            var centre = target.Placement.Point(local);
            if (inPlace) {
                string why;
                var chain = NativeHoleLink.ChainOf(asm, target.Pick, target.Part, target.Placement, out why);
                if (chain == null) { L("     认不出实例：" + why); return; }
                s = NativeHoleLink.Enter(app, asm, chain, out why);
                if (s == null) { L("     进不了原位：" + why); return; }
                target = AutoHoleWriter.AnchorToActivePart(target);
                Copies(target.Part, "原位里");
            }
            int before = HoleCount(target.Part);
            var r = AutoHoleWriter.Drill(target, new HoleSpec { Kind = HoleKind.Through, HoleDiameter = 6.6 }, new[]{ centre });
            Pump(4);
            L("     建了 " + r.Created + "/" + r.Requested + "，孔 " + before + " → " + HoleCount(target.Part) + (r.Failures.Count > 0 ? "，失败：" + string.Join("；", r.Failures) : ""));
            L("     RESULT " + label + "：" + (r.Created > 0 ? "OK" : "FAIL"));
        } catch (Exception e) {
            L("     异常 " + e.GetType().Name + " " + Hr(e));
            L("     RESULT " + label + "：EXC");
        } finally {
            if (s != null) { try { NativeHoleLink.Leave(app, s); } catch { } Pump(4); }
        }
    }
}

[ComImport, Guid("00000016-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IStaleMessageFilter {
    [PreserveSig] int HandleInComingCall(int type, IntPtr task, int ticks, IntPtr info);
    [PreserveSig] int RetryRejectedCall(IntPtr task, int ticks, int reject);
    [PreserveSig] int MessagePending(IntPtr task, int ticks, int pending);
}
sealed class StaleFilter : IStaleMessageFilter {
    [DllImport("ole32.dll")] static extern int CoRegisterMessageFilter(IStaleMessageFilter f, out IStaleMessageFilter old);
    static StaleFilter keep;
    public static void Register(){ keep = new StaleFilter(); IStaleMessageFilter old; CoRegisterMessageFilter(keep, out old); }
    public int HandleInComingCall(int a, IntPtr b, int c, IntPtr d){ return 0; }
    public int RetryRejectedCall(IntPtr a, int ticks, int reject){ return ticks < 120000 ? 200 : -1; }
    public int MessagePending(IntPtr a, int b, int c){ return 2; }
}

