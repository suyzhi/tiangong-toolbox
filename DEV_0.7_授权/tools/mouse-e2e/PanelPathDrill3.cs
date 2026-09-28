using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using TianGongCadSuite;
using A=SolidEdgeAssembly;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;
using F=SolidEdgeFramework;

// 只诊断锚定为什么没命中（不改插件、不重编译）。
class PanelPathDrill3 {
    static void L(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    static string Full(P.PartDocument p){ try { return p.FullName; } catch (Exception e) { return "<读不到:" + e.Message + ">"; } }

    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        F.Application app;
        try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); }
        catch (Exception e) { L("连不上 CAD：" + e.Message); return 2; }
        var activePart = app.ActiveDocument as P.PartDocument;
        L("ActiveDocument 是零件 = " + (activePart != null));
        if (activePart == null) return 3;

        A.AssemblyDocument asm = null;
        int n = app.Documents.Count;
        for (int i = 1; i <= n && asm == null; i++){
            var c = app.Documents.Item(i) as A.AssemblyDocument; if (c == null) continue;
            asm = c;
        }
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
        L("target.Part.FullName = " + Full(tgtDoc));
        L("activePart.FullName   = " + Full(activePart));
        L("两者相等 = " + string.Equals(Full(tgtDoc), Full(activePart), StringComparison.OrdinalIgnoreCase));

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
        V3 lp = target.Placement.InversePoint(target.Plane.Point);
        V3 ln = target.Placement.InverseNormal(target.Plane.Normal).Unit();
        L("目标面局部平面点 (" + (lp.X*1000).ToString("0.###") + "," + (lp.Y*1000).ToString("0.###") + "," + (lp.Z*1000).ToString("0.###") + ") mm  法向(" + ln.X.ToString("0.###") + "," + ln.Y.ToString("0.###") + "," + ln.Z.ToString("0.###") + ")");

        var actModel = (P.Model)activePart.Models.Item(1);
        int count = 0; var best = new List<string>();
        foreach (G.Face f in (G.Faces)((G.Body)actModel.Body).get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll)){
            var pl = f.Geometry as G.Plane; if (pl == null) continue;
            Array p = new double[3], nn = new double[3];
            try { pl.GetPlaneData(ref p, ref nn); } catch { continue; }
            V3 cn = V3.From(nn).Unit();
            double dot = Math.Abs(cn.Dot(ln));
            double dist = Math.Abs((V3.From(p) - lp).Dot(ln));
            count++;
            if (count <= 30 && dot > 0.9) best.Add("    候选面 ID=" + f.ID + " |dot|=" + dot.ToString("0.000") + " 距离=" + (dist*1000).ToString("0.#####") + " mm");
        }
        L("活动零件上的平面面片数（含非水平）≈ " + count);
        foreach (var s in best) L(s);
        return 0;
    }
}
