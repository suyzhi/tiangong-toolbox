using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using TianGongCadSuite;
using A=SolidEdgeAssembly;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;
using F=SolidEdgeFramework;

// 验证"孔心落在已有孔上导致失败"：同一个局部目标，孔心分别用 (50,50,10) 和 (30,30,10)
class PPD8 {
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
        A.Occurrence refOcc = null; G.Edge refEdge = null;
        foreach (A.Occurrence occ in asm.Occurrences){
            var pd = occ.OccurrenceDocument as P.PartDocument;
            if (pd == null || pd.Models.Count < 1) continue;
            bool isActive = string.Equals(Full(pd), Full(activePart), StringComparison.OrdinalIgnoreCase);
            if (isActive) continue;
            var mdl = (P.Model)pd.Models.Item(1);
            foreach (G.Edge e in (G.Edges)((G.Body)mdl.Body).get_Edges(G.FeatureTopologyQueryTypeConstants.igQueryAll))
                if (e.Geometry is G.Circle) { refOcc = occ; refEdge = e; break; }
            if (refOcc != null) break;
        }
        var refHole = AutoHoleReader.ReadReference(asm.CreateReference(refOcc, refEdge));
        var spec = HoleMatcher.Match(refHole.DiameterMm).Target;
        L("参考孔 Φ" + refHole.DiameterMm.ToString("0.##") + " -> " + spec.Summary);

        var actModel = (P.Model)activePart.Models.Item(1);
        // 取活动零件的顶面（局部）
        G.Face top = null; double tz = double.MinValue; double[] tp = new double[3], tn = new double[3];
        foreach (G.Face f in (G.Faces)((G.Body)actModel.Body).get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll)){
            var pl = f.Geometry as G.Plane; if (pl == null) continue;
            Array p = new double[3], nn = new double[3];
            try { pl.GetPlaneData(ref p, ref nn); } catch { continue; }
            if (Math.Abs(Convert.ToDouble(nn.GetValue(2))) < 0.9) continue;
            double z = Convert.ToDouble(p.GetValue(2));
            if (z > tz){ tz = z; top = f; tp = new double[]{ Convert.ToDouble(p.GetValue(0)), Convert.ToDouble(p.GetValue(1)), z }; tn = new double[]{ Convert.ToDouble(nn.GetValue(0)), Convert.ToDouble(nn.GetValue(1)), Convert.ToDouble(nn.GetValue(2)) }; }
        }
        L("活动零件顶面局部点 (" + (tp[0]*1000).ToString("0.#") + "," + (tp[1]*1000).ToString("0.#") + "," + (tp[2]*1000).ToString("0.#") + ")");
        var t = new TargetFace { Plane = new PlaneInput(V3.From(tp), V3.From(tn).Unit(), "顶面"), Face = top, Part = activePart, Placement = Transform.Identity, Label = "顶面", PartName = SafeName(activePart) };

        foreach (double[] c in new double[][]{ new double[]{0.05,0.05,0.010}, new double[]{0.03,0.03,0.010}, new double[]{0.07,0.07,0.010} }){
            var req = new List<AutoHoleWriter.HoleRequest>();
            req.Add(new AutoHoleWriter.HoleRequest { Spec = spec, Centre = new V3(c[0], c[1], c[2]), Source = "孔心(" + (c[0]*1000).ToString("0") + "," + (c[1]*1000).ToString("0") + ")" });
            var r = AutoHoleWriter.DrillRequests(t, req);
            L("孔心 (" + (c[0]*1000).ToString("0") + "," + (c[1]*1000).ToString("0") + ") => " + (r.Created == 1 && r.Failures.Count == 0 ? ("PASS " + r.Method) : ("FAIL " + string.Join("；", r.Failures.ToArray()))));
        }
        L("PPD8 done");
        return 0;
    }
}
