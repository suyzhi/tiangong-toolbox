using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using TianGongCadSuite;
using A=SolidEdgeAssembly;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;
using F=SolidEdgeFramework;

// 判别实验 6：干净会话里验证"打孔前先把目标零件文件打开一次"这个补救是否成立。
// 用法: AsmDrill4.exe <asm路径>
class AsmDrill4 {
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
        L("目标件 " + tgtOcc.Name + " -> " + tgtPath);

        Func<string> attempt = () => {
            var req = new List<AutoHoleWriter.HoleRequest>();
            req.Add(new AutoHoleWriter.HoleRequest { Spec = match.Target, Centre = centre, Source = "尝试" });
            var r = AutoHoleWriter.DrillRequests(target, req);
            return r.Created == 1 && r.Failures.Count == 0 ? null : string.Join("；", r.Failures.ToArray());
        };

        string e1 = attempt();
        L(e1 == null ? "PASS 直接打成功（不需要补救）" : ("FAIL 直接打：" + e1));
        if (e1 == null){ L("ASM-DRILL4 NO-FIX-NEEDED"); return 0; }

        // 补救：把目标零件文件当文档打开一次
        try {
            object d = app.Documents.Open(tgtPath);
            string dn = "null";
            try { var doc = d as P.PartDocument; dn = (doc == null) ? "返回的不是 PartDocument" : doc.Name; }
            catch (Exception ex) { dn = "读名字失败：" + ex.Message; }
            L("已打开零件文件：" + dn);
        } catch (Exception ex) { L("打开零件文件失败：" + ex.Message); }
        Pump(10);
        string e2 = attempt();
        L(e2 == null ? "PASS 打开零件文件后打成功" : ("FAIL 打开零件文件后：" + e2));
        L("ASM-DRILL4 " + (e2 == null ? "WORKAROUND-OK" : "STILL-FAIL"));
        return e2 == null ? 0 : 1;
    }
}
