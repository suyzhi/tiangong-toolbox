using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using TianGongCadSuite;
using A=SolidEdgeAssembly;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;
using F=SolidEdgeFramework;

// 二分：同一状态下分别用 Drill/DrillRequests × 锚定目标/单位变换目标，找出到底哪个因素决定能写。
class PanelPathDrill5 {
    static void L(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    static string Full(P.PartDocument p){ try { return p.FullName; } catch { return "?"; } }
    static string SafeName(object d){ try { dynamic x = d; return Convert.ToString(x.Name); } catch { return "?"; } }

    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        F.Application app;
        try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); }
        catch (Exception e) { L("连不上 CAD：" + e.Message); return 2; }
        var activePart = app.ActiveDocument as P.PartDocument;
        L("活动文档 " + SafeName(app.ActiveDocument) + " 是零件=" + (activePart != null));
        if (activePart == null) return 3;

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
        var spec = HoleMatcher.Match(refHole.DiameterMm).Target;
        var centreAssembly = AutoHoleReader.Intersect(refHole, target);
        L("参考孔 Φ" + refHole.DiameterMm.ToString("0.##") + " -> " + spec.Summary);
        L("孔心(装配) " + (centreAssembly.X*1000).ToString("0.#") + "," + (centreAssembly.Y*1000).ToString("0.#") + "," + (centreAssembly.Z*1000).ToString("0.#"));

        // A) Drill + 锚定目标（Part=活动文档, Face=活动文档的面, Placement=原样）
        var actModel = (P.Model)activePart.Models.Item(1);
        V3 lp = target.Placement.InversePoint(target.Plane.Point);
        V3 ln = target.Placement.InverseNormal(target.Plane.Normal).Unit();
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
        var anchored = new TargetFace { Plane = target.Plane, Face = found, Part = activePart, Placement = target.Placement, PartName = SafeName(activePart), Label = "平面 " + (found==null?0:found.ID) };
        try {
            var rA = AutoHoleWriter.Drill(anchored, spec, new V3[]{ centreAssembly });
            L(rA.Created == 1 && rA.Failures.Count == 0 ? ("A) Drill + 锚定目标 => PASS：" + rA.Method) : ("A) Drill + 锚定目标 => FAIL：" + string.Join("；", rA.Failures.ToArray())));
        } catch (Exception e) { L("A) Drill + 锚定目标 => 异常 " + e.Message); }

        // B) DrillRequests + 锚定目标
        try {
            var req = new List<AutoHoleWriter.HoleRequest>();
            req.Add(new AutoHoleWriter.HoleRequest { Spec = spec, Centre = centreAssembly, Source = "B" });
            var rB = AutoHoleWriter.DrillRequests(anchored, req);
            L(rB.Created == 1 && rB.Failures.Count == 0 ? ("B) DrillRequests + 锚定目标 => PASS：" + rB.Method) : ("B) DrillRequests + 锚定目标 => FAIL：" + string.Join("；", rB.Failures.ToArray())));
        } catch (Exception e) { L("B) DrillRequests + 锚定目标 => 异常 " + e.Message); }

        // C) Drill + 单位变换目标（InPlaceDrill 的配方，局部坐标）
        G.Face top = null; double tz = double.MinValue; double[] tp = new double[3], tn = new double[3];
        foreach (G.Face f in (G.Faces)((G.Body)actModel.Body).get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll)){
            var pl = f.Geometry as G.Plane; if (pl == null) continue;
            Array p = new double[3], nn = new double[3];
            try { pl.GetPlaneData(ref p, ref nn); } catch { continue; }
            if (Math.Abs(Convert.ToDouble(nn.GetValue(2))) < 0.9) continue;
            double z = Convert.ToDouble(p.GetValue(2));
            if (z > tz){ tz = z; top = f; tp = new double[]{ Convert.ToDouble(p.GetValue(0)), Convert.ToDouble(p.GetValue(1)), z }; tn = new double[]{ Convert.ToDouble(nn.GetValue(0)), Convert.ToDouble(nn.GetValue(1)), Convert.ToDouble(nn.GetValue(2)) }; }
        }
        var identityTarget = new TargetFace { Plane = new PlaneInput(V3.From(tp), V3.From(tn).Unit(), "顶面"), Face = top, Part = activePart, Placement = Transform.Identity, Label = "顶面", PartName = SafeName(activePart) };
        try {
            var rC = AutoHoleWriter.Drill(identityTarget, spec, new V3[]{ new V3(tp[0] + 0.03, tp[1] + 0.03, tp[2]) });
            L(rC.Created == 1 && rC.Failures.Count == 0 ? ("C) Drill + 单位变换 => PASS：" + rC.Method) : ("C) Drill + 单位变换 => FAIL：" + string.Join("；", rC.Failures.ToArray())));
        } catch (Exception e) { L("C) Drill + 单位变换 => 异常 " + e.Message); }

        L("PPD5 done");
        return 0;
    }
}
