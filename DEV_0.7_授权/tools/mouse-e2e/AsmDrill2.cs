using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using TianGongCadSuite;
using A=SolidEdgeAssembly;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;
using F=SolidEdgeFramework;

// 判别实验 4：装配里打孔失败后，试两种补救再打一次：
//   补救 A：把目标零件文档 Activate() 一下（让它在会话里"热"起来）
//   补救 B：把零件文件用 Documents.Open 打开一次，再打
// 用法: AsmDrill2.exe <asm路径>
class AsmDrill2 {
    static void L(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    static F.Application app;

    static void Pump(int n){ for (int i = 0; i < n; i++){ try { app.DoIdle(); } catch { } Thread.Sleep(250); } }

    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); }
        catch (Exception e) { L("FATAL 连不上 CAD：" + e.Message); return 2; }
        try { app.Visible = true; app.ScreenUpdating = true; } catch { }

        var asm = (A.AssemblyDocument)app.Documents.Open(args[0]);
        L("装配 " + asm.FullName);
        Pump(8);

        A.Occurrence refOcc = null, tgtOcc = null; G.Edge refEdge = null;
        foreach (A.Occurrence occ in asm.Occurrences){
            var pd = occ.OccurrenceDocument as P.PartDocument;
            if (pd == null || pd.Models.Count < 1) continue;
            var mdl = (P.Model)pd.Models.Item(1);
            G.Edge circle = null;
            foreach (G.Edge e in (G.Edges)((G.Body)mdl.Body).get_Edges(G.FeatureTopologyQueryTypeConstants.igQueryAll))
                if (e.Geometry is G.Circle) { circle = e; break; }
            if (circle != null && refOcc == null) { refOcc = occ; refEdge = circle; }
            else if (tgtOcc == null) { tgtOcc = occ; }
        }
        var refHole = AutoHoleReader.ReadReference(asm.CreateReference(refOcc, refEdge));
        var tgtDoc = (P.PartDocument)tgtOcc.OccurrenceDocument;
        var tgtModel = (P.Model)tgtDoc.Models.Item(1);
        G.Face face = null; double bestZ = double.MinValue;
        foreach (G.Face f in (G.Faces)((G.Body)tgtModel.Body).get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll)){
            var pl = f.Geometry as G.Plane; if (pl == null) continue;
            Array p = new double[3], n = new double[3];
            try { pl.GetPlaneData(ref p, ref n); } catch { continue; }
            if (Math.Abs(Convert.ToDouble(n.GetValue(2))) < 0.9) continue;
            double z = Convert.ToDouble(p.GetValue(2));
            if (z > bestZ) { bestZ = z; face = f; }
        }
        var target = AutoHoleReader.ReadTarget(asm.CreateReference(tgtOcc, face));
        var match = HoleMatcher.Match(refHole.DiameterMm);
        var centre = AutoHoleReader.Intersect(refHole, target);
        string tgtPath = ""; try { tgtPath = tgtDoc.FullName; } catch { }
        L("目标件 " + tgtOcc.Name + "  " + tgtPath);

        Func<string> attempt = () => {
            var req = new List<AutoHoleWriter.HoleRequest>();
            req.Add(new AutoHoleWriter.HoleRequest { Spec = match.Target, Centre = centre, Source = "夹具" });
            var r = AutoHoleWriter.DrillRequests(target, req);
            return r.Created == 1 && r.Failures.Count == 0 ? null : string.Join("；", r.Failures.ToArray());
        };

        string e1 = attempt();
        L(e1 == null ? "PASS 第一次就打成功" : ("FAIL 第一次：" + e1));
        if (e1 == null) return 0;

        // 补救 A：激活目标零件文档
        try { tgtDoc.Activate(); L("已 Activate 目标零件文档"); } catch (Exception ex) { L("Activate 失败：" + ex.Message); }
        Pump(8);
        string e2 = attempt();
        L(e2 == null ? "PASS 补救A（激活零件文档）后打成功" : ("FAIL 补救A：" + e2));
        if (e2 == null) return 0;

        // 补救 B：把零件文件打开一次
        try { var d = app.Documents.Open(tgtPath); L("已打开零件文件：" + (d != null)); } catch (Exception ex) { L("打开零件失败：" + ex.Message); }
        Pump(8);
        string e3 = attempt();
        L(e3 == null ? "PASS 补救B（打开零件文件）后打成功" : ("FAIL 补救B：" + e3));

        L("ASM-DRILL2 result=" + (e3 == null ? "B-OK" : "ALL-FAIL"));
        return e3 == null ? 0 : 1;
    }
}
