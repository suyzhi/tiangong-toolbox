using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using TianGongCadSuite;
using A=SolidEdgeAssembly;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;
using F=SolidEdgeFramework;

// 判别实验 11：顺序改成 —— 先 Occurrence.MakeWritable()，再重新解析零件/面/参考，最后打孔。
// 用法: AsmDrill8.exe <asm路径>
class AsmDrill8 {
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

        // ① 先把两个实例都改成可写（参考件也要读得到边，写孔只写目标件）
        foreach (A.Occurrence occ in new A.Occurrence[]{ tgtOcc }){
            try { occ.MakeWritable(); L("MakeWritable " + occ.Name + " OK"); } catch (Exception e) { L("MakeWritable " + occ.Name + " 失败：" + e.Message); }
        }
        Pump(10);

        // ② MakeWritable 之后重新解析：零件、模型、面、参考孔
        var tgtDoc = (P.PartDocument)tgtOcc.OccurrenceDocument;
        var tgtModel = (P.Model)tgtDoc.Models.Item(1);
        bool ro = false; try { ro = tgtDoc.ReadOnly; } catch {}
        L("目标件 " + tgtOcc.Name + "  ReadOnly(新解析)=" + ro);
        G.Face face = null; double bestZ = double.MinValue;
        foreach (G.Face f in (G.Faces)((G.Body)tgtModel.Body).get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll)){
            var pl = f.Geometry as G.Plane; if (pl == null) continue;
            Array p = new double[3], n = new double[3];
            try { pl.GetPlaneData(ref p, ref n); } catch { continue; }
            if (Math.Abs(Convert.ToDouble(n.GetValue(2))) < 0.9) continue;
            double z = Convert.ToDouble(p.GetValue(2));
            if (z > bestZ) { bestZ = z; face = f; }
        }
        if (face == null) { L("FATAL 找不到水平面"); return 5; }
        var refHole = AutoHoleReader.ReadReference(asm.CreateReference(refOcc, refEdge));
        var target = AutoHoleReader.ReadTarget(asm.CreateReference(tgtOcc, face));
        var match = HoleMatcher.Match(refHole.DiameterMm);
        var centre = AutoHoleReader.Intersect(refHole, target);
        L("规格 " + match.Row.Size + "  面 " + target.Label + "  孔心 z=" + (centre.Z * 1000).ToString("0.#") + "mm");

        var req = new List<AutoHoleWriter.HoleRequest>();
        req.Add(new AutoHoleWriter.HoleRequest { Spec = match.Target, Centre = centre, Source = "先MakeWritable再解析" });
        var r = AutoHoleWriter.DrillRequests(target, req);
        bool ok = r.Created == 1 && r.Failures.Count == 0;
        L(ok ? ("PASS 打孔成功：" + r.Method) : ("FAIL 打孔失败：" + string.Join("；", r.Failures.ToArray())));
        L("ASM-DRILL8 " + (ok ? "ORDER-FIXED-OK" : "STILL-FAIL"));
        return ok ? 0 : 1;
    }
}
