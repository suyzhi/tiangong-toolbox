using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using TianGongCadSuite;
using A=SolidEdgeAssembly;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;
using F=SolidEdgeFramework;

// 判别实验 9：干净会话里先打开目标零件文件（让它成为"文档"），再对装配里的实例打孔。
// 用法: AsmDrill6.exe <asm路径>
class AsmDrill6 {
    static void L(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    static F.Application app;
    static void Pump(int n){ for (int i = 0; i < n; i++){ try { app.DoIdle(); } catch {} Thread.Sleep(250); } }

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
        var tgtDoc0 = (P.PartDocument)tgtOcc.OccurrenceDocument;
        string tgtPath = ""; try { tgtPath = tgtDoc0.FullName; } catch { }
        L("目标件 " + tgtOcc.Name + " -> " + tgtPath);

        // 先把零件文件打开成"文档"（此时它没有未保存更改，不应弹框）
        try {
            object d = app.Documents.Open(tgtPath);
            string dn = "?";
            try { var pd2 = d as P.PartDocument; dn = pd2 == null ? "非 PartDocument" : pd2.Name; } catch {}
            L("已打开零件文档：" + dn);
        } catch (Exception ex) { L("打开零件文档失败：" + ex.Message); }
        Pump(10);

        // 重新取一遍面/引用（打开文档后对象可能变了）
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
        var refHole = AutoHoleReader.ReadReference(asm.CreateReference(refOcc, refEdge));
        var target = AutoHoleReader.ReadTarget(asm.CreateReference(tgtOcc, face));
        var match = HoleMatcher.Match(refHole.DiameterMm);
        var centre = AutoHoleReader.Intersect(refHole, target);

        var req = new List<AutoHoleWriter.HoleRequest>();
        req.Add(new AutoHoleWriter.HoleRequest { Spec = match.Target, Centre = centre, Source = "先打开零件文档" });
        var r = AutoHoleWriter.DrillRequests(target, req);
        bool ok = r.Created == 1 && r.Failures.Count == 0;
        L(ok ? ("PASS 打孔成功：" + r.Method) : ("FAIL 打孔失败：" + string.Join("；", r.Failures.ToArray())));
        L("ASM-DRILL6 " + (ok ? "WARMUP-OK" : "STILL-FAIL"));
        return ok ? 0 : 1;
    }
}
