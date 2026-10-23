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

// 0.7.15：多实体零件上「面扫描找孔」「配孔检查」只读第 1 个实体（0.7.13 交接遗留）。**只在副本上跑**。
//   ① 多实体零件副本：实体 6 上打 2 个 M6 沉孔 + 实体 1 上打 1 个 Φ6.6 通孔，存盘（副本）；
//      点实体 6 那张面扫孔 → 应认出 2 个、配做孔径 6.6（沉孔口 Φ11）；
//   ② 打开含这个零件的装配副本：CollectAssemblyHoles 里这个零件应有实体 6 的 2 个孔 + 实体 1 的 1 个孔；
//      OccurrenceBodies 射线表要能打中实体 6（孔轴线穿过实体 6 的材料）；
//   ③ 另一个零件（可选）：实体数、最大平面上打一个孔，看能不能打上（现场「万向轮范围」打不上）。
// 用法: MultiBodyCheckE2E.exe <输出目录> <多实体零件副本> <含它的装配副本> <副本根目录> [另一个零件副本]
class MultiBodyCheckE2E {
    static F.Application app;
    static int fail = 0;
    static void L(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    static void Check(bool ok, string label){ L((ok ? "PASS: " : "FAIL: ") + label); if (!ok) fail++; }
    static void Pump(int n){ for (int i = 0; i < n; i++) { try { app.DoIdle(); } catch { } try { Application.DoEvents(); } catch { } Thread.Sleep(60); } }
    static T Try<T>(Func<T> f, T dflt){ try { return f(); } catch { return dflt; } }
    static string Res(DrillResult r){
        return "建了 " + r.Created + "/" + r.Requested + "，本来就有 " + r.AlreadyOk + "，方式 " + r.Method
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
        string partPath = args[1], asmPath = args[2], root = Path.GetFullPath(args[3]);
        if (!Path.GetFullPath(partPath).StartsWith(root, StringComparison.OrdinalIgnoreCase) || !Path.GetFullPath(asmPath).StartsWith(root, StringComparison.OrdinalIgnoreCase)) {
            L("FATAL: 零件 / 装配不在副本根目录下，拒绝运行"); return 3;
        }
        List<V3> drilled6;
        if (PartPhase(partPath, out drilled6)) AsmPhase(asmPath, Path.GetFileNameWithoutExtension(partPath), drilled6, root);
        if (args.Length > 4) OtherPart(args[4], root);
        L(fail == 0 ? "MULTI-BODY CHECK E2E OK" : ("MULTI-BODY CHECK E2E FAILED " + fail));
        return fail == 0 ? 0 : 1;
    }

    static G.Face Largest(P.PartDocument part, int m){
        var body = (G.Body)((P.Model)part.Models.Item(m)).Body;
        G.Face best = null; double area = 0;
        foreach (G.Face f in (G.Faces)body.get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll)) {
            if (!(Try<object>(() => f.Geometry, null) is G.Plane)) continue;
            double a = Try(() => f.Area, 0.0);
            if (a > area) { area = a; best = f; }
        }
        return best;
    }
    static List<V3> Spots(G.Face face, int count, double step){
        Array p = new double[3], nv = new double[3]; ((G.Plane)face.Geometry).GetPlaneData(ref p, ref nv);
        var n = V3.From(nv).Unit();
        var c = Inside(face);
        Array lo = new double[3], hi = new double[3]; face.GetRange(ref lo, ref hi);
        var span = V3.From(hi) - V3.From(lo);
        V3 axis = Math.Abs(span.X) >= Math.Abs(span.Y) && Math.Abs(span.X) >= Math.Abs(span.Z) ? new V3(1, 0, 0) : Math.Abs(span.Y) >= Math.Abs(span.Z) ? new V3(0, 1, 0) : new V3(0, 0, 1);
        var u = (axis - n * axis.Dot(n)).Unit();
        var list = new List<V3>();
        for (int k = 0; k < count; k++) list.Add(c + u * (step * (k - (count - 1) / 2.0)));
        return list;
    }

    static bool PartPhase(string path, out List<V3> drilled6){
        drilled6 = new List<V3>();
        L(""); L("== ① 多实体零件 " + Path.GetFileName(path));
        P.PartDocument part = null;
        try {
            part = (P.PartDocument)app.Documents.Open(path);
            Pump(15);
            int mc = PartBodies.Count(part);
            L("实体数 " + mc);
            if (mc < 6) { Check(false, "要至少 6 个实体"); return false; }
            var cb = new HoleSpec { Kind = HoleKind.Counterbore, ThreadSize = "M6", HoleDiameter = 6.6, CounterboreDiameter = 11, CounterboreDepth = 2 };
            var f6 = Largest(part, 6);
            var t6 = AutoHoleReader.ReadTarget(f6);
            drilled6 = Spots(f6, 2, 0.014);
            var r6 = AutoHoleWriter.DrillRequests(t6, drilled6.Select((c, i) => new AutoHoleWriter.HoleRequest { Spec = cb, Centre = c, Source = "沉孔" + (i + 1) }).ToList()); Pump(4);
            L("   实体6：" + Res(r6));
            Check(r6.Created == 2, "实体 6 上 2 个沉孔打上");
            var f1 = Largest(part, 1);
            var t1 = AutoHoleReader.ReadTarget(f1);
            var c1 = Spots(f1, 1, 0);
            var r1 = AutoHoleWriter.DrillRequests(t1, new[]{ new AutoHoleWriter.HoleRequest { Spec = new HoleSpec { Kind = HoleKind.Through, HoleDiameter = 6.6 }, Centre = c1[0], Source = "通孔" } }); Pump(4);
            L("   实体1：" + Res(r1));
            Check(r1.Created == 1, "实体 1 上 1 个通孔打上");

            HoleScanCad.Invalidate(part);
            // 点实体 6 那张面扫孔（打完孔面片可能换了，重新找最大平面）
            var t6b = AutoHoleReader.ReadTarget(Largest(part, 6));
            string how; int skipped;
            var found = HoleScanCad.ScanFace(t6b, out how, out skipped);
            L("   扫实体 6 的面：" + found.Count + " 个孔，跳过 " + skipped + " " + how + "　" + string.Join("；", found.Select(h => "Φ" + h.DiameterMm.ToString("0.##") + " 孔口Φ" + h.MouthDiameterMm.ToString("0.##") + " " + h.MouthKind)));
            Check(found.Count == 2, "面扫描认出实体 6 上的 2 个孔");
            Check(found.Count > 0 && found.All(h => Math.Abs(h.DiameterMm - 6.6) < 0.05), "配做孔径按沉孔下面的 Φ6.6（不是孔口 Φ11）");
            // 点实体 6 上的孔口圆边（Φ11）认孔
            G.Edge mouth = null;
            foreach (G.Edge e in (G.Edges)((G.Body)((P.Model)part.Models.Item(6)).Body).get_Edges(G.FeatureTopologyQueryTypeConstants.igQueryAll)) {
                var ci = Try<object>(() => e.Geometry, null) as G.Circle; if (ci == null) continue;
                Array cc = new double[3], ax = new double[3]; double r = 0; ci.GetCircleData(ref cc, ref ax, out r);
                if (Math.Abs(r * 2000 - 11) < 0.05) { mouth = e; break; }
            }
            if (mouth != null) {
                var h = HoleScanCad.ScanEdge(mouth);
                L("   点 Φ11 孔口圆边：Φ" + h.DiameterMm.ToString("0.##") + " " + h.MouthKind);
                Check(Math.Abs(h.DiameterMm - 6.6) < 0.05, "点沉孔口圆边认出配做 Φ6.6");
            } else Check(false, "实体 6 上找不到 Φ11 圆边");
            part.Save(); Pump(6);
            L("   副本已存盘：" + part.FullName);
            return true;
        } catch (Exception e) {
            L("E2E 异常：" + e.GetType().Name + " 0x" + e.HResult.ToString("X8") + " " + e.Message + "\r\n" + e.StackTrace);
            fail++; return false;
        } finally {
            try { if (part != null) part.Close(false); } catch { }
            Pump(6);
        }
    }

    static void AsmPhase(string asmPath, string partStem, List<V3> drilled6, string root){
        L(""); L("== ② 装配 " + Path.GetFileName(asmPath));
        A.AssemblyDocument asm = null;
        try {
            asm = (A.AssemblyDocument)app.Documents.Open(asmPath);
            Pump(30);
            List<string> warnings; List<HoleRecord> shafts; List<AssemblyPart> parts;
            var holes = AutoHoleWriter.CollectAssemblyHoles(asm, out warnings, out shafts, out parts);
            L("   零件实例 " + parts.Count + "，孔 " + holes.Count + "，读取警告 " + warnings.Count);
            foreach (var w in warnings.Take(10)) L("     警告：" + w);
            foreach (var ap in parts) {
                int n = PartBodies.Count(ap.Part);
                if (n > 1) L("   多实体：" + ap.Name + "　实体 " + n + "　" + Try(() => ap.Part.FullName, "?"));
            }
            var mine = parts.Where(p => Try(() => Path.GetFileNameWithoutExtension(p.Part.FullName), "") == partStem).ToList();
            if (mine.Count == 0) { Check(false, "装配里没有 " + partStem); return; }
            var ap0 = mine[0];
            string full = Try(() => ap0.Part.FullName, "");
            Check(full.StartsWith(root, StringComparison.OrdinalIgnoreCase), "装配里的零件就是副本（" + full + "）");
            var its = holes.Where(h => h.PartName == ap0.Name).ToList();
            L("   " + ap0.Name + " 的孔：" + its.Count + "　" + string.Join("；", its.Select(h => "Φ" + h.DiameterMm.ToString("0.##"))));
            int d66 = its.Count(h => Math.Abs(h.DiameterMm - 6.6) < 0.05);
            Check(d66 >= 3, "配孔检查读到这个零件的 3 个 Φ6.6（实体 6 两个 + 实体 1 一个），实际 " + d66);
            // 射线：沿实体 6 孔的轴线打，应命中这个零件（装配坐标）
            var index = AutoHoleWriter.OccurrenceBodies.Build(parts);
            var place = ap0.Placement;
            // 两个沉孔中间（材料里）沿面法线的一条线：应该穿过实体 6 的材料
            var f6 = Largest(ap0.Part, 6);
            Array pp = new double[3], nv = new double[3]; ((G.Plane)f6.Geometry).GetPlaneData(ref pp, ref nv);
            var nn = V3.From(nv).Unit();
            var mid = (drilled6[0] + drilled6[1]) * 0.5;
            var probe = new HoleRecord { Center = place.Point(mid), Axis = place.Vector(nn).Unit(), DiameterMm = 6.6, PartName = "探针" };
            Check(index.Hits(probe, ap0.Name), "射线表：实体 6 两孔之间沿法线的线穿过这个零件");
        } catch (Exception e) {
            L("E2E 异常：" + e.GetType().Name + " 0x" + e.HResult.ToString("X8") + " " + e.Message + "\r\n" + e.StackTrace);
            fail++;
        } finally {
            try { if (asm != null) asm.Close(false); } catch { }
            Pump(6);
        }
    }

    static void OtherPart(string path, string root){
        L(""); L("== ③ " + Path.GetFileName(path));
        if (!Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase)) { L("不在副本目录，跳过"); return; }
        P.PartDocument part = null;
        try {
            part = (P.PartDocument)app.Documents.Open(path);
            Pump(15);
            int mc = PartBodies.Count(part);
            L("   实体数 " + mc + "　孔特征 " + Try(() => part.Models.Item(1).Holes.Count, -1));
            try {
                var feats = part.DesignEdgebarFeatures;
                var names = new List<string>();
                for (int i = 1; i <= feats.Count; i++) names.Add(Try(() => (string)((dynamic)feats.Item(i)).Name, "?"));
                L("   特征树：" + string.Join(" / ", names));
            } catch (Exception e) { L("   特征树读不到：" + e.Message); }
            for (int m = 1; m <= Math.Min(mc, 3); m++) {
                var f = Largest(part, m);
                if (f == null) { L("   实体 " + m + " 没有平面"); continue; }
                var t = AutoHoleReader.ReadTarget(f);
                var c = Spots(f, 1, 0);
                var r = AutoHoleWriter.DrillRequests(t, new[]{ new AutoHoleWriter.HoleRequest { Spec = new HoleSpec { Kind = HoleKind.Through, HoleDiameter = 6.6 }, Centre = c[0], Source = "通孔" } }); Pump(4);
                L("   实体 " + m + " 面积 " + (Try(() => f.Area, 0.0) * 1e6).ToString("0") + "mm²：" + Res(r));
                Check(r.Created == 1, "③ 实体 " + m + " 最大平面打上一个通孔");
            }
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
