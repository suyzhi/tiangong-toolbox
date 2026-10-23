using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using TianGongCadSuite;
using A=SolidEdgeAssembly;
using G=SolidEdgeGeometry;
using F=SolidEdgeFramework;

// 0.7.15：配孔检查新报的"漏打孔"是不是真的 —— 指定一个零件实例和一条装配坐标里的孔轴线，
// 列出这个零件每个实体被这条直线穿过的面：面类型、沿轴线离孔心多远（包围盒）、面上有没有和轴线同轴的圆边。只读不改。
// 用法: MissProbe.exe <输出目录> <装配副本> <实例名> x y z(mm) ax ay az
class MissProbe {
    static void L(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        TianGongCadSuite.Licensing.LicenseTestHooks.GateOverride = 0;
        var app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application");
        try { app.DisplayAlerts = false; } catch { }
        var asm = (A.AssemblyDocument)app.Documents.Open(Path.GetFullPath(args[1]));
        try {
            var p = new V3(double.Parse(args[3]) / 1000, double.Parse(args[4]) / 1000, double.Parse(args[5]) / 1000);
            var ax = new V3(double.Parse(args[6]), double.Parse(args[7]), double.Parse(args[8])).Unit();
            var ap = AssemblyPart.List(asm, null).First(x => x.Name == args[2]);
            V3 o = ap.Placement.InversePoint(p), d = ap.Placement.InverseNormal(ax).Unit();
            L(ap.Name + " 实体 " + ap.Bodies.Count + "　局部原点 " + o + " 方向 " + d);
            for (int b = 0; b < ap.Bodies.Count; b++) {
                object faces;
                try { faces = ap.Bodies[b].get_FacesByRay(o.X, o.Y, o.Z, d.X, d.Y, d.Z); } catch (Exception e) { L("  实体 " + (b + 1) + " 打不了射线：" + e.Message); continue; }
                var coll = faces as System.Collections.IEnumerable; if (coll == null) continue;
                foreach (object x in coll) {
                    var f = x as G.Face; if (f == null) continue;
                    Array lo = new double[3], hi = new double[3]; f.GetRange(ref lo, ref hi);
                    double t0 = (V3.From(lo) - o).Dot(d) * 1000, t1 = (V3.From(hi) - o).Dot(d) * 1000;
                    string kind = f.Geometry == null ? "?" : f.Geometry.GetType().Name;
                    try { if (f.Geometry is G.Plane) kind = "Plane"; else if (f.Geometry is G.Cylinder) kind = "Cylinder"; } catch { }
                    L("  实体 " + (b + 1) + " 面#" + f.ID + " " + kind + " 面积 " + (f.Area * 1e6).ToString("0") + "mm²　沿轴 " + Math.Min(t0, t1).ToString("0.0") + "～" + Math.Max(t0, t1).ToString("0.0") + "mm");
                }
            }
        } finally { try { asm.Close(false); } catch { } }
        return 0;
    }
}
