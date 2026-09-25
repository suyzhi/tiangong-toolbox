using System;
using System.IO;
using System.Runtime.InteropServices;
using TianGongCadSuite;
using A=SolidEdgeAssembly;
using F=SolidEdgeFramework;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;

// 诊断：为什么"同一个面打第二批孔"时找不到可复用的共面基准面。
class DiagnosePlane {
    static void Main(string[] args){
        string dir = args.Length > 0 ? args[0] : "artifacts/visual";
        var app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application");
        Console.WriteLine("CAD " + app.Version);
        var asm = app.ActiveDocument as A.AssemblyDocument;
        Console.WriteLine("DOC " + (asm == null ? "不是装配" : asm.Name));
        if (asm == null) return;
        P.PartDocument part = null; A.Occurrence occ = null;
        foreach (A.Occurrence o in asm.Occurrences) {
            var d = o.OccurrenceDocument as P.PartDocument;
            Console.WriteLine("  OCC " + o.Name + " -> " + (d == null ? "?" : d.Name));
            if (d != null && d.Name.StartsWith("VisB")) { part = d; occ = o; }
        }
        if (part == null) { Console.WriteLine("找不到 VisB"); return; }
        var model = (P.Model)part.Models.Item(1);
        Console.WriteLine("VisB 体积 " + (((G.Body)model.Body).Volume * 1e9).ToString("0.#") + " mm³，孔特征 " + model.Holes.Count + " 个");

        Console.WriteLine("--- 零件里所有基准面 ---");
        foreach (P.RefPlane rp in part.RefPlanes) {
            Array n = new double[3], p = new double[3];
            rp.GetNormal(ref n); rp.GetRootPoint(ref p);
            Console.WriteLine("  RefPlane root=(" + Mm(p,0) + ", " + Mm(p,1) + ", " + Mm(p,2) + ") normal=("
                + Num(n,0) + ", " + Num(n,1) + ", " + Num(n,2) + ")");
        }

        // 取顶面（局部 z=+10mm），复刻 ReadTarget 的路径
        G.Face faceTop = null;
        foreach (G.Face f in (G.Faces)((G.Body)model.Body).get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll)) {
            var pl = f.Geometry as G.Plane; if (pl == null) continue;
            Array p = new double[3], n = new double[3];
            pl.GetPlaneData(ref p, ref n);
            if (Math.Abs(Convert.ToDouble(n.GetValue(2))) < 1e-6) continue;
            if (Math.Abs(Convert.ToDouble(p.GetValue(2)) - 0.010) < 1e-6) { faceTop = f; break; }
        }
        if (faceTop == null) { Console.WriteLine("找不到顶面"); return; }
        var target = AutoHoleReader.ReadTarget(asm.CreateReference(occ, faceTop));
        Console.WriteLine("--- target ---");
        Console.WriteLine("  PartName=" + target.PartName + " Label=" + target.Label);
        Console.WriteLine("  Plane.Point=(" + target.Plane.Point + ")  Plane.Normal=(" + target.Plane.Normal + ")");
        var fpt = target.Placement.InversePoint(target.Plane.Point);
        var fnv = target.Placement.InverseNormal(target.Plane.Normal).Unit();
        Console.WriteLine("  局部点=(" + fpt + ")  局部法向=(" + fnv + ")  Placement.Rigid=" + target.Placement.Rigid);

        Console.WriteLine("--- 共面判定（逐条） ---");
        foreach (P.RefPlane rp in part.RefPlanes) {
            Array n = new double[3], p = new double[3];
            rp.GetNormal(ref n); rp.GetRootPoint(ref p);
            var rnv = V3.From(n).Unit();
            double dot = Math.Abs(rnv.Dot(fnv));
            var rr = V3.From(p);
            double dist = (rr - fpt).Dot(fnv);
            Console.WriteLine("  rp root=(" + Mm(p,0) + "," + Mm(p,1) + "," + Mm(p,2) + ") |dot|=" + dot.ToString("F9")
                + " 距离=" + (dist * 1000).ToString("F9") + "mm  平行? " + (Math.Abs(dot - 1) <= 1e-6)
                + " 共面? " + (Math.Abs(dist) < 1e-7));
        }
        Console.WriteLine("--- 面片几何直读（对照） ---");
        try {
            var pl = faceTop.Geometry as G.Plane;
            Array p = new double[3], n = new double[3];
            pl.GetPlaneData(ref p, ref n);
            Console.WriteLine("  face root=(" + Mm(p,0) + "," + Mm(p,1) + "," + Mm(p,2) + ") normal=(" + Num(n,0) + "," + Num(n,1) + "," + Num(n,2) + ")");
        } catch (Exception e) { Console.WriteLine("  直读失败（面已失效）：" + e.Message); }
        Console.WriteLine("DIAG DONE");
    }
    static string Mm(Array a, int i){ return (Convert.ToDouble(a.GetValue(i)) * 1000).ToString("F6"); }
    static string Num(Array a, int i){ return Convert.ToDouble(a.GetValue(i)).ToString("F6"); }
}
