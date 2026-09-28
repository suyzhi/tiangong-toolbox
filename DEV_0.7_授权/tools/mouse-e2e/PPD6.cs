using System;
using System.Runtime.InteropServices;
using TianGongCadSuite;
using A=SolidEdgeAssembly;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;
using F=SolidEdgeFramework;

class PPD6 {
    static void L(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    static string Full(P.PartDocument p){ try { return p.FullName; } catch { return "?"; } }
    static string SafeName(object d){ try { dynamic x = d; return Convert.ToString(x.Name); } catch { return "?"; } }

    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        F.Application app;
        try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); }
        catch (Exception e) { L("连不上 CAD：" + e.Message); return 2; }
        var activePart = app.ActiveDocument as P.PartDocument;
        if (activePart == null){ L("不在原位编辑"); return 3; }
        A.AssemblyDocument asm = null;
        for (int i = 1; i <= app.Documents.Count && asm == null; i++) asm = app.Documents.Item(i) as A.AssemblyDocument;
        A.Occurrence refOcc = null, tgtOcc = null; G.Edge refEdge = null;
        foreach (A.Occurrence occ in asm.Occurrences){
            var pd = occ.OccurrenceDocument as P.PartDocument;
            if (pd == null || pd.Models.Count < 1) continue;
            bool isActive = string.Equals(Full(pd), Full(activePart), StringComparison.OrdinalIgnoreCase);
            var mdl = (P.Model)pd.Models.Item(1);
            G.Edge circle = null;
            foreach (G.Edge e in (G.Edges)((G.Body)mdl.Body).get_Edges(G.FeatureTopologyQueryTypeConstants.igQueryAll))
                if (e.Geometry is G.Circle) { circle = e; break; }
            if (isActive) tgtOcc = occ;
            if (circle != null && refOcc == null && !isActive){ refOcc = occ; refEdge = circle; }
        }
        var tgtDoc = (P.PartDocument)tgtOcc.OccurrenceDocument;
        var tgtModel = (P.Model)tgtDoc.Models.Item(1);
        G.Face face = null; double bz = double.MinValue;
        foreach (G.Face f in (G.Faces)((G.Body)tgtModel.Body).get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll)){
            var pl = f.Geometry as G.Plane; if (pl == null) continue;
            Array p = new double[3], nn = new double[3];
            try { pl.GetPlaneData(ref p, ref nn); } catch { continue; }
            if (Math.Abs(Convert.ToDouble(nn.GetValue(2))) < 0.9) continue;
            double z = Convert.ToDouble(p.GetValue(2));
            if (z > bz){ bz = z; face = f; }
        }
        var target = AutoHoleReader.ReadTarget(asm.CreateReference(tgtOcc, face));
        var refHole = AutoHoleReader.ReadReference(asm.CreateReference(refOcc, refEdge));
        var centre = AutoHoleReader.Intersect(refHole, target);

        L("A. 装配系：面点 (" + (target.Plane.Point.X*1000).ToString("0.###") + "," + (target.Plane.Point.Y*1000).ToString("0.###") + "," + (target.Plane.Point.Z*1000).ToString("0.###") + ")  孔心 (" + (centre.X*1000).ToString("0.###") + "," + (centre.Y*1000).ToString("0.###") + "," + (centre.Z*1000).ToString("0.###") + ")");
        V3 lp = target.Placement.InversePoint(target.Plane.Point);
        V3 lc = target.Placement.InversePoint(centre);
        L("B. 用选中的 Placement 换算到局部：面点 (" + (lp.X*1000).ToString("0.###") + "," + (lp.Y*1000).ToString("0.###") + "," + (lp.Z*1000).ToString("0.###") + ")  孔心 (" + (lc.X*1000).ToString("0.###") + "," + (lc.Y*1000).ToString("0.###") + "," + (lc.Z*1000).ToString("0.###") + ")");

        // 用实例矩阵自己算一遍（Occurrence.GetMatrix → 局部->装配）
        try {
            Array m = null; tgtOcc.GetMatrix(ref m);
            double[] mm = new double[16];
            for (int i = 0; i < 16; i++) mm[i] = Convert.ToDouble(m.GetValue(i));
            double x = centre.X, y = centre.Y, z = centre.Z;
            double ax = mm[0]*x + mm[1]*y + mm[2]*z + mm[3];
            double ay = mm[4]*x + mm[5]*y + mm[6]*z + mm[7];
            double az = mm[8]*x + mm[9]*y + mm[10]*z + mm[11];
            L("C. 实例矩阵最后一列(平移) = (" + (mm[3]*1000).ToString("0.###") + "," + (mm[7]*1000).ToString("0.###") + "," + (mm[11]*1000).ToString("0.###") + ")");
            L("   用实例矩阵正算（把装配点当局部点会造成）: (" + (ax*1000).ToString("0.#") + "," + (ay*1000).ToString("0.#") + "," + (az*1000).ToString("0.#") + ")");
        } catch (Exception e) { L("C. 读实例矩阵失败：" + e.Message); }

        // 零件实体包围盒（用 Range 读）
        try {
            var body = (G.Body)tgtModel.Body;
            Array rng = null;
            try { rng = (Array)body.GetType().InvokeMember("Range", System.Reflection.BindingFlags.GetProperty, null, body, null); } catch {}
            if (rng != null){
                string s = "";
                for (int i = 0; i < rng.Length; i++) s += (Convert.ToDouble(rng.GetValue(i))*1000).ToString("0.#") + " ";
                L("D. 目标零件实体 Range(局部, mm) = " + s);
            } else L("D. 读不到 Range");
        } catch (Exception e) { L("D. 包围盒失败：" + e.Message); }
        L("PPD6 done");
        return 0;
    }
}
