using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using TianGongCadSuite;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;
using F=SolidEdgeFramework;

// 0.7.13：多实体零件（PartBodies）上走插件的真实打孔接口。**只在副本上跑**（不保存）。
//   多实体零件（现场 YF25.07G01-L1-M30-02-07.par，39 个设计体）：
//     ① 实体 6 一张面一批 3 个同规格孔 → 多孔特征；② 同一批再打一次 → 都报"本来就有孔"、不算失败；
//     ③ 实体 15 一个 2mm 盲孔；④ 实体 12 批量排孔 3 个（种子孔 + 阵列）→ ⑤ 撤销它；⑥ 实体 1 单孔；⑦ 实体 6 上腰孔
//   单实体零件（回归，可选第 2 个路径）：⑧ 一个通孔 + 一批 2 个
// 用法: MultiBodyE2E.exe <输出目录> <多实体零件副本> [单实体零件副本]
class MultiBodyE2E {
    static F.Application app;
    static int fail = 0;
    static void L(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    static void Check(bool ok, string label){ L((ok ? "PASS: " : "FAIL: ") + label); if (!ok) fail++; }
    static void Pump(int n){ for (int i = 0; i < n; i++) { try { app.DoIdle(); } catch { } try { Application.DoEvents(); } catch { } Thread.Sleep(60); } }
    static T Try<T>(Func<T> f, T dflt){ try { return f(); } catch { return dflt; } }
    static int Faces(P.PartDocument part, int m){ return Try(() => ((G.Faces)((G.Body)((P.Model)part.Models.Item(m)).Body).get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll)).Count, -1); }
    static string Res(DrillResult r){
        return "建了 " + r.Created + "/" + r.Requested + "，本来就有 " + r.AlreadyOk + "，多孔特征 " + r.Combined + "，方式 " + r.Method
             + (r.Failures.Count > 0 ? "，失败：" + string.Join("；", r.Failures) : "") + (r.Notes.Count > 0 ? "，说明：" + string.Join("；", r.Notes) : "");
    }

    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        TianGongCadSuite.Licensing.LicenseTestHooks.GateOverride = 0;
        E2EFilter.Register();
        try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); }
        catch (Exception e) { L("连不上 CAD：" + e.Message); return 2; }
        try { app.DisplayAlerts = false; } catch { }
        L("已连上 CAD " + app.Version + "　插件 DLL " + typeof(AutoHoleWriter).Assembly.GetName().Version);
        MultiPart(args[1]);
        if (args.Length > 2) SinglePart(args[2]);
        L(fail == 0 ? "MULTI-BODY E2E OK" : ("MULTI-BODY E2E FAILED " + fail));
        return fail == 0 ? 0 : 1;
    }

    // 实体 m 上面积最大的平面 + 面上几个孔位（最大三角形重心，沿面内一个方向每隔 step 一个）
    static TargetFace Face(P.PartDocument part, int m, int count, double step, out List<V3> centres){
        var body = (G.Body)((P.Model)part.Models.Item(m)).Body;
        G.Face best = null; double area = 0;
        foreach (G.Face f in (G.Faces)body.get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll)) {
            if (!(Try<object>(() => f.Geometry, null) is G.Plane)) continue;
            double a = Try(() => f.Area, 0.0);
            if (a > area) { area = a; best = f; }
        }
        var target = AutoHoleReader.ReadTarget(best);   // 零件文档里直接给面片：PickGeometry 当作单位变换
        Array p = new double[3], nv = new double[3]; ((G.Plane)best.Geometry).GetPlaneData(ref p, ref nv);
        var n = V3.From(nv).Unit();
        var c = Inside(best);
        // 面内方向：面片包围盒最长的那个轴（投影到面内）
        Array lo = new double[3], hi = new double[3]; best.GetRange(ref lo, ref hi);
        var span = V3.From(hi) - V3.From(lo);
        V3 axis = Math.Abs(span.X) >= Math.Abs(span.Y) && Math.Abs(span.X) >= Math.Abs(span.Z) ? new V3(1, 0, 0) : Math.Abs(span.Y) >= Math.Abs(span.Z) ? new V3(0, 1, 0) : new V3(0, 0, 1);
        var u = (axis - n * axis.Dot(n)).Unit();
        centres = new List<V3>();
        for (int k = 0; k < count; k++) centres.Add(c + u * (step * (k - (count - 1) / 2.0)));
        L("   实体 " + m + " 面积 " + (area * 1e6).ToString("0") + "mm²，认出的实体 = " + target.Body);
        return target;
    }

    static void MultiPart(string path){
        L(""); L("== 多实体零件 " + Path.GetFileName(path));
        P.PartDocument part = null;
        try {
            part = (P.PartDocument)app.Documents.Open(path);
            Pump(15);
            int mc = PartBodies.Count(part);
            L("实体数 " + mc);
            Check(mc > 1, "确实是多实体零件");
            var spec = new HoleSpec { Kind = HoleKind.Through, HoleDiameter = 6.6 };

            // ① 实体 6 一批 3 个
            List<V3> cs; var t6 = Face(part, 6, 3, 0.012, out cs);
            Check(t6.Body == 6, "① 面认在实体 6 上（" + t6.Body + "）");
            int f0 = Faces(part, 6);
            var reqs = cs.Select((c, i) => new AutoHoleWriter.HoleRequest { Spec = spec, Centre = c, Source = "孔" + (i + 1) }).ToList();
            var r1 = AutoHoleWriter.DrillRequests(t6, reqs); Pump(4);
            L("   " + Res(r1) + "　实体6 面 " + f0 + " → " + Faces(part, 6));
            Check(r1.Created == 3 && r1.Failures.Count == 0, "① 3 个孔都打上");
            Check(r1.Combined == 1, "① 合成一个多孔孔特征");
            Check(Faces(part, 6) == f0 + 6, "① 实体 6 上多了 3 个孔（面数 +6）");
            Check(Faces(part, 1) == 18, "① 别的实体没被切（实体 1 面数 18）");

            // ② 同一批再打一次
            List<V3> cs2; var t6b = Face(part, 6, 3, 0.012, out cs2);
            var r2 = AutoHoleWriter.DrillRequests(t6b, cs.Select((c, i) => new AutoHoleWriter.HoleRequest { Spec = spec, Centre = c, Source = "孔" + (i + 1) }).ToList()); Pump(4);
            L("   " + Res(r2));
            Check(r2.AlreadyOk == 3 && r2.Failures.Count == 0 && r2.Created == 0, "② 同位置再打：3 个都是本来就有孔，没有失败");

            // ③ 实体 15 盲孔
            List<V3> c15; var t15 = Face(part, 15, 1, 0, out c15);
            int f15 = Faces(part, 15);
            var r3 = AutoHoleWriter.DrillRequests(t15, new[]{ new AutoHoleWriter.HoleRequest { Spec = new HoleSpec { Kind = HoleKind.Through, HoleDiameter = 5, Depth = 2 }, Centre = c15[0], Source = "盲孔" } }); Pump(4);
            L("   " + Res(r3) + "　实体15 面 " + f15 + " → " + Faces(part, 15));
            Check(r3.Created == 1 && Faces(part, 15) > f15, "③ 实体 15 上 2mm 盲孔打上");

            // ④ 实体 12 批量排孔 → ⑤ 撤销
            List<V3> c12; var t12 = Face(part, 12, 3, 0.010, out c12);
            int f12 = Faces(part, 12);
            var r4 = HolePatternWriter.Drill(t12, spec, c12, null); Pump(4);
            L("   " + Res(r4) + "　实体12 面 " + f12 + " → " + Faces(part, 12));
            Check(r4.Created == 3 && r4.Failures.Count == 0 && Faces(part, 12) == f12 + 6, "④ 实体 12 批量排孔 3 个");
            Check(r4.Method.Contains("阵列"), "④ 用的是种子孔 + 阵列（" + r4.Method + "）");
            var u4 = r4.Undo.Run(); Pump(4);
            L("   撤销：" + u4.Summary + "　实体12 面 → " + Faces(part, 12));
            Check(Faces(part, 12) == f12, "⑤ 撤销后实体 12 面数回到 " + f12);

            // ⑥ 实体 1 单孔
            List<V3> c1; var t1 = Face(part, 1, 1, 0, out c1);
            int f1 = Faces(part, 1);
            var r6 = AutoHoleWriter.DrillRequests(t1, new[]{ new AutoHoleWriter.HoleRequest { Spec = spec, Centre = c1[0], Source = "单孔" } }); Pump(4);
            L("   " + Res(r6) + "　实体1 面 " + f1 + " → " + Faces(part, 1));
            Check(r6.Created == 1 && Faces(part, 1) == f1 + 2, "⑥ 实体 1 单孔打上");

            // ⑦ 实体 6 腰孔（拉伸切除）
            List<V3> c6s; var t6s = Face(part, 6, 1, 0, out c6s);
            Array pn = new double[3]; V3 n6 = t6s.Plane.Normal;
            var dir = (Math.Abs(n6.X) < 0.9 ? new V3(1, 0, 0) : new V3(0, 1, 0)); dir = (dir - n6 * dir.Dot(n6)).Unit();
            int fs = Faces(part, 6);
            var r7 = AutoHoleWriter.DrillSlots(t6s, new[]{ c6s[0] + dir * 0.03 }, new[]{ dir }, 12, 5); Pump(4);
            L("   " + Res(r7) + "　实体6 面 " + fs + " → " + Faces(part, 6));
            Check(r7.Created == 1 && Faces(part, 6) > fs, "⑦ 实体 6 腰孔切上");
        } catch (Exception e) {
            L("E2E 异常：" + e.GetType().Name + " 0x" + e.HResult.ToString("X8") + " " + e.Message + "\r\n" + e.StackTrace);
            fail++;
        } finally {
            try { if (part != null) part.Close(false); } catch { }
            Pump(6);
        }
    }

    static void SinglePart(string path){
        L(""); L("== 单实体零件（回归） " + Path.GetFileName(path));
        P.PartDocument part = null;
        try {
            part = (P.PartDocument)app.Documents.Open(path);
            Pump(15);
            L("实体数 " + PartBodies.Count(part));
            var spec = new HoleSpec { Kind = HoleKind.Through, HoleDiameter = 6.6 };
            List<V3> cs; var t = Face(part, 1, 3, 0.012, out cs);
            int f0 = Faces(part, 1);
            var r1 = AutoHoleWriter.DrillRequests(t, new[]{ new AutoHoleWriter.HoleRequest { Spec = spec, Centre = cs[0], Source = "单孔" } }); Pump(4);
            L("   " + Res(r1));
            int f1 = Faces(part, 1);
            Check(r1.Created == 1 && f1 > f0, "⑧ 单实体零件单孔打上（面 " + f0 + " → " + f1 + "，孔壁可能是 1 张或 2 张面）");
            List<V3> cs2; var t2 = Face(part, 1, 3, 0.012, out cs2);
            var r2 = AutoHoleWriter.DrillRequests(t2, cs.Skip(1).Select((c, i) => new AutoHoleWriter.HoleRequest { Spec = spec, Centre = c, Source = "孔" + (i + 2) }).ToList()); Pump(4);
            L("   " + Res(r2) + "　面 " + f0 + " → " + Faces(part, 1));
            Check(r2.Created == 2 && r2.Combined == 1 && Faces(part, 1) >= f1 + 2, "⑧ 单实体零件一批 2 个合成多孔特征");
        } catch (Exception e) {
            L("E2E 异常：" + e.GetType().Name + " 0x" + e.HResult.ToString("X8") + " " + e.Message + "\r\n" + e.StackTrace);
            fail++;
        } finally {
            try { if (part != null) part.Close(false); } catch { }
            Pump(6);
        }
    }

    static V3 Inside(G.Face face){
        int n = 0; Array pts = new double[0]; object nrm = null, tex = null;
        face.GetFacetData(0.0005, out n, ref pts, out nrm, out tex);
        double best = 0; V3 c = new V3();
        for (int t = 0; t + 8 < pts.Length; t += 9) {
            var p0 = P3(pts, t); var p1 = P3(pts, t + 3); var p2 = P3(pts, t + 6);
            double a = (p1 - p0).Cross(p2 - p0).Length;
            if (a > best) { best = a; c = (p0 + p1 + p2) * (1.0 / 3.0); }
        }
        return c;
    }
    static V3 P3(Array a, int i){ return new V3(Convert.ToDouble(a.GetValue(i)), Convert.ToDouble(a.GetValue(i + 1)), Convert.ToDouble(a.GetValue(i + 2))); }
}

[ComImport, Guid("00000016-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IE2EMessageFilter {
    [PreserveSig] int HandleInComingCall(int type, IntPtr task, int ticks, IntPtr info);
    [PreserveSig] int RetryRejectedCall(IntPtr task, int ticks, int reject);
    [PreserveSig] int MessagePending(IntPtr task, int ticks, int pending);
}
sealed class E2EFilter : IE2EMessageFilter {
    [DllImport("ole32.dll")] static extern int CoRegisterMessageFilter(IE2EMessageFilter f, out IE2EMessageFilter old);
    static E2EFilter keep;
    public static void Register(){ keep = new E2EFilter(); IE2EMessageFilter old; CoRegisterMessageFilter(keep, out old); }
    public int HandleInComingCall(int a, IntPtr b, int c, IntPtr d){ return 0; }
    public int RetryRejectedCall(IntPtr a, int ticks, int reject){ return ticks < 120000 ? 200 : -1; }
    public int MessagePending(IntPtr a, int b, int c){ return 2; }
}
