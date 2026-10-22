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

// 0.7.11：先在"来源零件"上打孔（改了被零件间复制的那张面）→ 打孔零件里的复制过期 → 再往打孔零件上打孔，还能不能打。
// 这是用户现场的顺序：一个装配里一个零件接一个零件地配孔，前面打过的零件正好是后面零件里零件间复制的来源。**只在副本上跑**（不保存）。
// 用法: LinkSourceEditProbe.exe <输出目录> <装配副本路径> <打孔零件名关键字>
class LinkSourceEditProbe {
    static F.Application app;
    static void L(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    static void Pump(int n){ for (int i = 0; i < n; i++) { try { app.DoIdle(); } catch { } try { Application.DoEvents(); } catch { } Thread.Sleep(60); } }
    static string Hr(Exception e){ return "0x" + e.HResult.ToString("X8") + " " + e.Message; }
    static T Try<T>(Func<T> f, T d){ try { return f(); } catch { return d; } }
    static string panelLog = Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA"), @"TianGongCadSuite\panel.log");

    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        TianGongCadSuite.Licensing.LicenseTestHooks.GateOverride = 0;
        SrcFilter.Register();
        string asmPath = args[1], want = args[2];
        try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); } catch (Exception e) { L("连不上 CAD：" + e.Message); return 2; }
        try { app.DisplayAlerts = false; } catch { }
        A.AssemblyDocument asm = null;
        try {
            asm = (A.AssemblyDocument)app.Documents.Open(asmPath);
            Pump(20);
            L("打开 " + Path.GetFileName(asmPath));
            Func<AssemblyPart> T = () => AssemblyPart.List(asm, new List<string>()).First(p => Try(() => p.Part.FullName, "").IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0);
            var target = T();
            Copies(target.Part, "开始");
            // 每个零件间复制的来源面：AsmSource 是装配引用 → 当成"用户点的面"去打孔
            var sources = new List<object>();
            var all = target.Part.Constructions.InterpartConstructions;
            for (int i = 1; i <= all.Count; i++) {
                var c = (P.InterpartConstruction)all.Item(i);
                object src = null; try { src = c.AsmSource; } catch (Exception e) { L("   " + Try(() => c.Name, "?") + " 读不到 AsmSource " + Hr(e)); }
                if (src != null) sources.Add(src);
                L("   " + Try(() => c.Name, "?") + " 来源 " + (src == null ? "-" : src.GetType().Name));
            }
            int k = 0;
            foreach (var src in sources) {
                k++;
                try {
                    var tf = AutoHoleReader.ReadTarget(src);
                    V3 local; if (!Centroid(tf.Face, out local)) { L("   来源 " + k + "：取不到面上的点"); continue; }
                    var r = AutoHoleWriter.Drill(tf, new HoleSpec { Kind = HoleKind.Through, HoleDiameter = 3.3 }, new[]{ tf.Placement.Point(local) });
                    Pump(4);
                    L("   来源 " + k + "（" + tf.PartName + " " + tf.Label + "）上打孔：" + r.Created + "/" + r.Requested + (r.Failures.Count > 0 ? " " + string.Join("；", r.Failures) : ""));
                } catch (Exception e) { L("   来源 " + k + " 打孔异常 " + Hr(e)); }
                if (k >= 4) break;
            }
            target = T();
            Copies(target.Part, "来源改过以后");
            long mark = new FileInfo(panelLog).Length;
            Drill(asm, target, "来源改过·不更新·装配上下文", false, 0);
            Drill(asm, T(), "来源改过·不更新·原位", true, 1);
            Tail(mark);
            try { asm.UpdateAll(); L("已 UpdateAll"); } catch (Exception e) { L("UpdateAll 失败 " + Hr(e)); }
            Pump(10);
            target = T();
            Copies(target.Part, "UpdateAll 后");
            Drill(asm, target, "UpdateAll 后·装配上下文", false, 2);
        } catch (Exception e) {
            L("PROBE 异常：" + e.GetType().Name + " " + Hr(e) + "\r\n" + e.StackTrace);
        } finally { try { if (asm != null) asm.Close(false); } catch { } Pump(4); }
        L("PROBE DONE");
        return 0;
    }

    static void Tail(long from){
        try {
            using (var fs = new FileStream(panelLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) {
                fs.Seek(from, SeekOrigin.Begin);
                foreach (var x in new StreamReader(fs, System.Text.Encoding.UTF8).ReadToEnd().Split('\n')) if (x.Trim().Length > 0 && !x.StartsWith("   在 ")) L("   日志 " + x.TrimEnd('\r'));
            }
        } catch { }
    }

    static void Copies(P.PartDocument part, string when){
        try {
            var all = part.Constructions.InterpartConstructions;
            var s = new List<string>();
            for (int i = 1; i <= all.Count; i++) {
                var c = (P.InterpartConstruction)all.Item(i);
                object d = null; string st;
                try { st = c.GetStatusEx(out d).ToString().Replace("igFeature", ""); } catch (Exception e) { st = "(" + Hr(e) + ")"; }
                s.Add(Try(() => c.Name, "?") + "=" + st + (d == null ? "" : "[" + d + "]"));
            }
            L("   [" + when + "] " + string.Join("，", s.ToArray()));
        } catch (Exception e) { L("   [" + when + "] 读不出 " + Hr(e)); }
    }

    static bool Centroid(G.Face f, out V3 c){
        c = new V3();
        int n = 0; Array pts = new double[0]; object nrm, tex;
        f.GetFacetData(0.0005, out n, ref pts, out nrm, out tex);
        double best = 0;
        for (int t = 0; t + 8 < pts.Length; t += 9) {
            var p0 = P3(pts, t); var p1 = P3(pts, t + 3); var p2 = P3(pts, t + 6);
            double a = (p1 - p0).Cross(p2 - p0).Length;
            if (a > best) { best = a; c = (p0 + p1 + p2) * (1.0 / 3.0); }
        }
        return best > 0;
    }
    static V3 P3(Array a, int i){ return new V3(Convert.ToDouble(a.GetValue(i)), Convert.ToDouble(a.GetValue(i + 1)), Convert.ToDouble(a.GetValue(i + 2))); }
    static int HoleCount(P.PartDocument part){ return Try(() => ((P.Model)part.Models.Item(1)).Holes.Count, -1); }

    static void Drill(A.AssemblyDocument asm, AssemblyPart ap, string label, bool inPlace, int shift){
        L(""); L("   [" + label + "]");
        NativeHoleLink.InPlace s = null;
        try {
            G.Face best = null; double area = 0;
            foreach (G.Face f in (G.Faces)((G.Body)((P.Model)ap.Part.Models.Item(1)).Body).get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll)) {
                if (!(f.Geometry is G.Plane)) continue; double a = Try(() => f.Area, 0); if (a > area) { area = a; best = f; }
            }
            V3 local; Centroid(best, out local);
            var target = AutoHoleReader.ReadTarget(ap.Reference(best));
            V3 nrm = target.Placement.InverseNormal(target.Plane.Normal).Unit();
            V3 u = Math.Abs(nrm.X) < 0.9 ? new V3(1, 0, 0) : new V3(0, 1, 0);
            u = (u - nrm * u.Dot(nrm)).Unit();
            var centre = target.Placement.Point(local + u * (0.009 * shift));
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
        } finally { if (s != null) { try { NativeHoleLink.Leave(app, s); } catch { } Pump(4); } }
    }
}

[ComImport, Guid("00000016-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface ISrcMessageFilter {
    [PreserveSig] int HandleInComingCall(int type, IntPtr task, int ticks, IntPtr info);
    [PreserveSig] int RetryRejectedCall(IntPtr task, int ticks, int reject);
    [PreserveSig] int MessagePending(IntPtr task, int ticks, int pending);
}
sealed class SrcFilter : ISrcMessageFilter {
    [DllImport("ole32.dll")] static extern int CoRegisterMessageFilter(ISrcMessageFilter f, out ISrcMessageFilter old);
    static SrcFilter keep;
    public static void Register(){ keep = new SrcFilter(); ISrcMessageFilter old; CoRegisterMessageFilter(keep, out old); }
    public int HandleInComingCall(int a, IntPtr b, int c, IntPtr d){ return 0; }
    public int RetryRejectedCall(IntPtr a, int ticks, int reject){ return ticks < 120000 ? 200 : -1; }
    public int MessagePending(IntPtr a, int b, int c){ return 2; }
}

