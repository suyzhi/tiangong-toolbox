using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using TianGongCadSuite;
using A=SolidEdgeAssembly;
using G=SolidEdgeGeometry;
using F=SolidEdgeFramework;

// 0.10.2 诊断：配孔检查报"漏打孔"时，孔的轴线在对方零件上到底碰到了哪些面（类型、包围盒、平面的精确交点、交点在不在面上）。
// 用法: RayDebug.exe <输出目录> <装配> <孔所在零件实例名> <X> <Y> <Z>（mm，取报告里 @ 的坐标） <对方零件实例名>
class RayDebug {
    static void L(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    static T Try<T>(Func<T> f, T d){ try { return f(); } catch { return d; } }
    static string P(V3 v){ return "(" + (v.X * 1000).ToString("0.#") + "," + (v.Y * 1000).ToString("0.#") + "," + (v.Z * 1000).ToString("0.#") + ")"; }
    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        TianGongCadSuite.Licensing.LicenseTestHooks.GateOverride = 0;
        RayFilter.Register();
        F.Application app;
        try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); } catch (Exception e) { L("连不上 CAD：" + e.Message); return 2; }
        try { app.DisplayAlerts = false; } catch { }
        A.AssemblyDocument asm = null;
        try {
            asm = (A.AssemblyDocument)app.Documents.Open(Path.GetFullPath(args[1]));
            for (int i = 0; i < 30; i++) { try { app.DoIdle(); } catch { } System.Threading.Thread.Sleep(60); }
            string holePart = args[2], target = args[6];
            var at = new V3(double.Parse(args[3]) / 1000, double.Parse(args[4]) / 1000, double.Parse(args[5]) / 1000);
            List<string> warnings; List<HoleRecord> shafts; List<AssemblyPart> parts;
            var holes = AutoHoleWriter.CollectAssemblyHoles(asm, out warnings, out shafts, out parts);
            var h = holes.Where(x => x.PartName.EndsWith(holePart)).OrderBy(x => (x.Center - at).Length).FirstOrDefault();
            if (h == null) { L("找不到孔"); return 1; }
            L("孔 " + h.PartName + " Φ" + h.DiameterMm.ToString("0.##") + " 孔心" + P(h.Center) + " 轴" + h.Axis + " 轴向范围 " + (h.AxialMin * 1000).ToString("0.#") + "~" + (h.AxialMax * 1000).ToString("0.#") + "mm（离给的坐标 " + ((h.Center - at).Length * 1000).ToString("0.#") + "mm）");
            var ap = parts.FirstOrDefault(p => p.Name.EndsWith(target));
            if (ap == null) { L("找不到对方零件 " + target); return 1; }
            var place = ap.Placement;
            var o = place.InversePoint(h.Center); var d = place.InverseNormal(h.Axis).Unit();
            L("对方 " + ap.Name + "，实体 " + ap.Bodies.Count);
            foreach (var body in ap.Bodies) {
                object faces = Try<object>(() => body.get_FacesByRay(o.X, o.Y, o.Z, d.X, d.Y, d.Z), null);
                var coll = faces as System.Collections.IEnumerable; if (coll == null) { L("  没有命中面"); continue; }
                foreach (object x in coll) {
                    var f = x as G.Face; if (f == null) continue;
                    object g = Try<object>(() => f.Geometry, null);
                    Array lo = new double[3], hi = new double[3]; Try(() => { f.GetRange(ref lo, ref hi); return 0; }, 0);
                    var blo = V3.From(lo); var bhi = V3.From(hi);
                    // 包围盒在轴线上的投影范围（相对孔心，mm）
                    var box = new Box3(blo, bhi); double t0, t1; bool clip = box.Clip(o, d, out t0, out t1);
                    string s = "  面#" + Try(() => f.ID, -1) + " " + (g == null ? "?" : g.GetType().Name.Replace("Class", "")) + " 盒" + P(blo) + "~" + P(bhi) + (clip ? " 盒在轴上 " + (t0 * 1000).ToString("0.#") + "~" + (t1 * 1000).ToString("0.#") + "mm" : " 盒不在轴上");
                    var pl = g as G.Plane;
                    if (pl != null) {
                        Array pp = new double[3], nn = new double[3]; pl.GetPlaneData(ref pp, ref nn);
                        var n = V3.From(nn).Unit(); double den = d.Dot(n);
                        if (Math.Abs(den) > 1e-9) {
                            double t = (V3.From(pp) - o).Dot(n) / den; var q = o + d * t;
                            s += " 平面交点 t=" + (t * 1000).ToString("0.##") + "mm";
                            // 交点在不在面上：参数化后问 IsParamOnFace（有这个接口的话）
                            try {
                                Array pt = new double[]{ q.X, q.Y, q.Z }, guess = new double[0], dev = new double[1], par = new double[2], flags = new int[1];
                                f.GetParamAtPoint(1, ref pt, ref guess, ref dev, ref par, ref flags);
                                Array onFace = new int[1];
                                try { f.GetParamOnFace(1, ref par, ref onFace); s += " 在面上=" + onFace.GetValue(0); }
                                catch (Exception e2) { s += " GetParamOnFace 失败（" + e2.Message + "）"; }
                            } catch (Exception e) { s += " 取不到参数（" + e.Message + "）"; }
                        } else s += " 平面和轴线平行";
                    }
                    L(s);
                }
            }
            L("DONE");
        } catch (Exception e) { L("异常：" + e); return 1; }
        finally { try { if (asm != null) asm.Close(false); } catch { } }
        return 0;
    }
}
[ComImport, Guid("00000016-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IRayFilter {
    [PreserveSig] int HandleInComingCall(int type, IntPtr task, int ticks, IntPtr info);
    [PreserveSig] int RetryRejectedCall(IntPtr task, int ticks, int reject);
    [PreserveSig] int MessagePending(IntPtr task, int ticks, int pending);
}
sealed class RayFilter : IRayFilter {
    [DllImport("ole32.dll")] static extern int CoRegisterMessageFilter(IRayFilter f, out IRayFilter old);
    static RayFilter keep;
    public static void Register(){ keep = new RayFilter(); IRayFilter old; CoRegisterMessageFilter(keep, out old); }
    public int HandleInComingCall(int a, IntPtr b, int c, IntPtr d){ return 0; }
    public int RetryRejectedCall(IntPtr a, int ticks, int reject){ return ticks < 120000 ? 200 : -1; }
    public int MessagePending(IntPtr a, int b, int c){ return 2; }
}
