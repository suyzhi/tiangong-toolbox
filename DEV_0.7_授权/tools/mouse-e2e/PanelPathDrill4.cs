using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using TianGongCadSuite;
using A=SolidEdgeAssembly;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;
using F=SolidEdgeFramework;

// 决定性实验：手工锚定（Part=活动文档、Face=活动文档上的同面）+ 对比两个 app 的 ActiveDocument。
class PanelPathDrill4 {
    static void L(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    static string Full(P.PartDocument p){ try { return p.FullName; } catch { return "?"; } }

    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        F.Application app;
        try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); }
        catch (Exception e) { L("连不上 CAD：" + e.Message); return 2; }
        var activePart = app.ActiveDocument as P.PartDocument;
        L("app.ActiveDocument = " + SafeName(app.ActiveDocument) + "  是零件=" + (activePart != null));
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
        // 对比：从 target.Part.Application 拿到的 ActiveDocument 是什么
        try {
            var viaPart = tgtDoc.Application.ActiveDocument;
            L("target.Part.Application.ActiveDocument = " + SafeName(viaPart));
        } catch (Exception e) { L("target.Part.Application 取 ActiveDocument 失败：" + e.Message); }

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
        var match = HoleMatcher.Match(refHole.DiameterMm);
        var centre = AutoHoleReader.Intersect(refHole, target);

        // 手工锚定：Part 换成活动文档；Face 换成活动文档模型上的同一张面（按局部平面匹配）；Placement/Plane 不变
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
        L("手工锚定匹配到面：" + (found == null ? "null" : ("ID=" + found.ID)) + "  距离=" + (best*1000).ToString("0.#####") + " mm");
        if (found == null) return 4;

        var anchored = new TargetFace {
            Plane = target.Plane, Face = found, Part = activePart, Placement = target.Placement,
            PartName = SafeName(activePart), Label = "平面 " + found.ID
        };
        var req = new List<AutoHoleWriter.HoleRequest>();
        req.Add(new AutoHoleWriter.HoleRequest { Spec = match.Target, Centre = centre, Source = "手工锚定" });
        var r = AutoHoleWriter.DrillRequests(anchored, req);
        L(r.Created == 1 && r.Failures.Count == 0 ? ("PASS 手工锚定后打孔成功：" + r.Method) : ("FAIL 手工锚定后仍失败：" + string.Join("；", r.Failures.ToArray())));
        L("PPD4 done");
        return 0;
    }
    static string SafeName(object document){ try { dynamic d = document; return Convert.ToString(d.Name); } catch { return "?"; } }
}
