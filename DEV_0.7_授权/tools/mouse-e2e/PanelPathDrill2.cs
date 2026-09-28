using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using TianGongCadSuite;
using A=SolidEdgeAssembly;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;
using F=SolidEdgeFramework;

// 复刻面板路径（**不打开装配**，因此不会踢掉原位编辑）：
// 用 asm.CreateReference(occ, face) 取面，再调 DrillRequests（里面会 AnchorToActivePart）。
// 用法: PanelPathDrill2.exe
class PanelPathDrill2 {
    static void L(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    static F.Application app;
    static void Pump(int n){ for (int i = 0; i < n; i++){ try { app.DoIdle(); } catch {} Thread.Sleep(250); } }
    static string Full(P.PartDocument p){ try { return p.FullName; } catch { return "?"; } }

    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); }
        catch (Exception e) { L("FATAL 连不上 CAD：" + e.Message); return 2; }
        var activePart = app.ActiveDocument as P.PartDocument;
        string actName = "?"; try { dynamic d = app.ActiveDocument; actName = Convert.ToString(d.Name); } catch {}
        L("活动文档 = " + actName + "   是零件=" + (activePart != null));
        if (activePart == null){ L("当前不在原位编辑状态，请先双击零件"); return 3; }

        // 在打开的文档里找"包含这个零件的装配"
        A.AssemblyDocument asm = null;
        int n = app.Documents.Count;
        for (int i = 1; i <= n && asm == null; i++){
            var candidate = app.Documents.Item(i) as A.AssemblyDocument;
            if (candidate == null) continue;
            try {
                foreach (A.Occurrence occ in candidate.Occurrences){
                    var pd = occ.OccurrenceDocument as P.PartDocument;
                    if (pd != null && string.Equals(Full(pd), Full(activePart), StringComparison.OrdinalIgnoreCase)){ asm = candidate; break; }
                }
            } catch (Exception e) { L("遍历实例失败：" + e.Message); }
        }
        if (asm == null){ L("没找到包含当前零件的装配"); return 4; }
        L("所属装配 = " + asm.Name);

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
        if (tgtOcc == null || refOcc == null){ L("找不到参照件或目标件"); return 5; }
        L("参照件 " + refOcc.Name + "  目标件(正在编辑) " + tgtOcc.Name);

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
        var match = HoleMatcher.Match(refHole.DiameterMm);
        var centre = AutoHoleReader.Intersect(refHole, target);
        L("目标面 " + target.PartName + " " + target.Label + "  参考孔 Φ" + refHole.DiameterMm.ToString("0.##") + " -> " + match.Row.Size);

        var anchored = AutoHoleWriter.AnchorToActivePart(target);
        L("锚定：Part 换了=" + (!ReferenceEquals(anchored.Part, target.Part)) + "  Face 换了=" + (!ReferenceEquals(anchored.Face, target.Face)));

        var req = new List<AutoHoleWriter.HoleRequest>();
        req.Add(new AutoHoleWriter.HoleRequest { Spec = match.Target, Centre = centre, Source = "面板路径复刻2" });
        var r = AutoHoleWriter.DrillRequests(target, req);
        L(r.Created == 1 && r.Failures.Count == 0 ? ("PASS 打孔成功：" + r.Method) : ("FAIL 打孔失败：" + string.Join("；", r.Failures.ToArray())));
        L("PANELPATH2 done");
        return 0;
    }
}
