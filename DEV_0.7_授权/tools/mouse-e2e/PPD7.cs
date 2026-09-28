using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using TianGongCadSuite;
using A=SolidEdgeAssembly;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;
using F=SolidEdgeFramework;

// D) DrillRequests + "局部坐标 + 单位变换" 的目标（等价于 C，但走面板那条路）
// E) Drill + "局部坐标 + 单位变换" 的目标，孔心用真实孔心（50,50,10）
class PPD7 {
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
        var picked = AutoHoleReader.ReadTarget(asm.CreateReference(tgtOcc, face));
        var refHole = AutoHoleReader.ReadReference(asm.CreateReference(refOcc, refEdge));
        var spec = HoleMatcher.Match(refHole.DiameterMm).Target;
        var centreAssembly = AutoHoleReader.Intersect(refHole, picked);

        // 把选中目标转换成"局部坐标 + 单位变换"的目标：面从活动文档里按局部平面找
        V3 lp = picked.Placement.InversePoint(picked.Plane.Point);
        V3 ln = picked.Placement.InverseNormal(picked.Plane.Normal).Unit();
        V3 lc = picked.Placement.InversePoint(centreAssembly);
        var actModel = (P.Model)activePart.Models.Item(1);
        G.Face found = null; double best = double.MaxValue;
        foreach (G.Face f in (G.Faces)((G.Body)actModel.Body).get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll)){
            var pl = f.Geometry as G.Plane; if (pl == null) continue;
            Array p = new double[3], nn = new double[3];
            try { pl.GetPlaneData(ref p, ref nn); } catch { continue; }
            V3 cn = V3.From(nn).Unit();
            if (Math.Abs(cn.Dot(ln)) < 0.999) continue;
            double d = Math.Abs((V3.From(p) - lp).Dot(ln));
            if (d < best){ best = d; found = f; }
        }
        L("局部坐标：面点 (" + (lp.X*1000).ToString("0.###") + "," + (lp.Y*1000).ToString("0.###") + "," + (lp.Z*1000).ToString("0.###") + ")  孔心 (" + (lc.X*1000).ToString("0.###") + "," + (lc.Y*1000).ToString("0.###") + "," + (lc.Z*1000).ToString("0.###") + ")  匹配面=" + (found==null?"null":found.ID.ToString()));
        if (found == null) return 4;
        var localTarget = new TargetFace { Plane = new PlaneInput(lp, ln, "打孔面"), Face = found, Part = activePart, Placement = Transform.Identity, Label = "平面 " + found.ID, PartName = SafeName(activePart) };

        // D) 面板路径（DrillRequests）+ 局部目标
        try {
            var req = new List<AutoHoleWriter.HoleRequest>();
            req.Add(new AutoHoleWriter.HoleRequest { Spec = spec, Centre = lc, Source = "D" });
            var rD = AutoHoleWriter.DrillRequests(localTarget, req);
            L(rD.Created == 1 && rD.Failures.Count == 0 ? ("D) DrillRequests + 局部目标 => PASS：" + rD.Method) : ("D) DrillRequests + 局部目标 => FAIL：" + string.Join("；", rD.Failures.ToArray())));
        } catch (Exception e) { L("D) 异常 " + e.Message); }
        L("PPD7 done");
        return 0;
    }
}
