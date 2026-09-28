using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using TianGongCadSuite;
using A=SolidEdgeAssembly;
using F=SolidEdgeFramework;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;

// 对照实验驱动：对**已经打开的老图纸**（TappedFixture.asm）用产品代码打孔，
// 先走面板那条路（DrillRequests），再走验收那条路（Drill），同一个孔心、同一条面。
// 目的：分清失败的根因是"零件/图纸"还是"调用路径"。
class FixtureDrill {
    static void L(string s){ Console.WriteLine(s); Console.Out.Flush(); }

    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        F.Application app;
        try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); }
        catch (Exception e) { L("FATAL 连接 CAD 失败：" + e.Message); return 2; }
        try { app.Visible = true; app.ScreenUpdating = true; } catch { }

        A.AssemblyDocument asm = null;
        for (int i = 1; i <= app.Documents.Count; i++){
            var d = app.Documents.Item(i) as A.AssemblyDocument;
            if (d == null) continue;
            string fn = null; try { fn = d.FullName; } catch { }
            if (fn != null && fn.IndexOf("TappedFixture", StringComparison.OrdinalIgnoreCase) >= 0) { asm = d; break; }
        }
        if (asm == null) { L("FATAL 没找到 TappedFixture.asm"); return 3; }
        L("装配 " + asm.FullName);

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
        if (refOcc == null || tgtOcc == null || refEdge == null) { L("FATAL 装配里找不到'带圆孔件 + 另一个件'"); return 4; }
        try { L("参照件 " + refOcc.Name + "   目标件 " + tgtOcc.Name); } catch { }

        var refHole = AutoHoleReader.ReadReference(asm.CreateReference(refOcc, refEdge));
        L("参考孔 Φ" + refHole.DiameterMm.ToString("0.###"));

        var tgtDoc = (P.PartDocument)tgtOcc.OccurrenceDocument;
        var tgtModel = (P.Model)tgtDoc.Models.Item(1);
        G.Face face = null; double faceZ = 0, bestZ = double.MinValue;
        foreach (G.Face f in (G.Faces)((G.Body)tgtModel.Body).get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll)){
            var pl = f.Geometry as G.Plane; if (pl == null) continue;
            Array pt = new double[3], nv = new double[3];
            try { pl.GetPlaneData(ref pt, ref nv); } catch { continue; }
            double nz = Math.Abs(Convert.ToDouble(nv.GetValue(2)));
            double z = Convert.ToDouble(pt.GetValue(2));
            if (nz < 0.9) continue;
            if (z > bestZ) { bestZ = z; face = f; faceZ = z; }
        }
        if (face == null) { L("FATAL 目标件上找不到水平面"); return 5; }
        var target = AutoHoleReader.ReadTarget(asm.CreateReference(tgtOcc, face));
        L("打孔面 " + target.PartName + " " + target.Label + "（平面 z=" + (faceZ * 1000).ToString("0.##") + "mm）");

        var match = HoleMatcher.Match(refHole.DiameterMm);
        L("规格 " + match.Row.Size + " → " + match.Target.Summary);
        var centre = AutoHoleReader.Intersect(refHole, target);
        L("孔心 " + (centre.X * 1000).ToString("0.##") + "," + (centre.Y * 1000).ToString("0.##") + "," + (centre.Z * 1000).ToString("0.##") + " mm");

        int pass = 0, fail = 0;
        // ---- 路线 1：面板按钮走的那条（DrillRequests）----
        try{
            var req = new List<AutoHoleWriter.HoleRequest>();
            req.Add(new AutoHoleWriter.HoleRequest { Spec = match.Target, Centre = centre, Source = "面板路线" });
            var r1 = AutoHoleWriter.DrillRequests(target, req);
            if (r1.Created == 1 && r1.Failures.Count == 0){ pass++; L("PASS 面板路线（DrillRequests）打孔成功：" + r1.Method); }
            else { fail++; L("FAIL 面板路线（DrillRequests）：成功 " + r1.Created + "，失败 " + string.Join("；", r1.Failures.ToArray())); }
        }catch(Exception e){ fail++; L("FAIL 面板路线抛异常：" + e.GetType().Name + " " + e.Message); }

        // ---- 路线 2：验收程序走的那条（Drill，带"丢缓存重建再试"）----
        var centre2 = new V3(centre.X, centre.Y, centre.Z);
        try{
            var r2 = AutoHoleWriter.Drill(target, match.Target, new V3[]{ centre2 });
            if (r2.Created == 1 && r2.Failures.Count == 0){ pass++; L("PASS 验收路线（Drill）打孔成功：" + r2.Method); }
            else { fail++; L("FAIL 验收路线（Drill）：成功 " + r2.Created + "，失败 " + string.Join("；", r2.Failures.ToArray())); }
        }catch(Exception e){ fail++; L("FAIL 验收路线抛异常：" + e.GetType().Name + " " + e.Message); }

        L("FIXTURE-DRILL pass=" + pass + " fail=" + fail);
        return fail == 0 ? 0 : 1;
    }
}
