using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using TianGongCadSuite;
using A=SolidEdgeAssembly;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;
using F=SolidEdgeFramework;

// 判别实验 3：由**客户端自己打开**装配，再对实例零件打孔。
// 与 FixtureDrill.cs 的唯一区别：装配不是 CAD 启动时用命令行带进来的。
// 用法: AsmDrill.exe <asm路径>
class AsmDrill {
    static void L(string s){ Console.WriteLine(s); Console.Out.Flush(); }

    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        F.Application app;
        try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); }
        catch (Exception e) { L("FATAL 连不上 CAD：" + e.Message); return 2; }
        try { app.Visible = true; app.ScreenUpdating = true; } catch { }

        A.AssemblyDocument asm = null;
        try { asm = (A.AssemblyDocument)app.Documents.Open(args[0]); }
        catch (Exception e) { L("FATAL 打开装配失败：" + e.Message); return 3; }
        if (asm == null) { L("FATAL 打开装配返回 null"); return 3; }
        L("装配（由客户端打开）" + asm.FullName);
        for (int i = 0; i < 6; i++) { try { app.DoIdle(); } catch { } System.Threading.Thread.Sleep(300); }

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
        if (refOcc == null || tgtOcc == null || refEdge == null) { L("FATAL 找不到参照件/目标件"); return 4; }
        try { L("参照件 " + refOcc.Name + "   目标件 " + tgtOcc.Name); } catch { }

        var refHole = AutoHoleReader.ReadReference(asm.CreateReference(refOcc, refEdge));
        L("参考孔 Φ" + refHole.DiameterMm.ToString("0.###"));
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
        if (face == null) { L("FATAL 目标件没有水平面"); return 5; }
        var target = AutoHoleReader.ReadTarget(asm.CreateReference(tgtOcc, face));
        L("打孔面 " + target.PartName + " " + target.Label);
        var match = HoleMatcher.Match(refHole.DiameterMm);
        var centre = AutoHoleReader.Intersect(refHole, target);
        L("规格 " + match.Row.Size + "  孔心 (" + (centre.X*1000).ToString("0.#") + "," + (centre.Y*1000).ToString("0.#") + "," + (centre.Z*1000).ToString("0.#") + ") mm");

        int pass = 0, fail = 0;
        try{
            var req = new List<AutoHoleWriter.HoleRequest>();
            req.Add(new AutoHoleWriter.HoleRequest { Spec = match.Target, Centre = centre, Source = "客户端打开装配" });
            var r1 = AutoHoleWriter.DrillRequests(target, req);
            if (r1.Created == 1 && r1.Failures.Count == 0){ pass++; L("PASS 打孔成功（DrillRequests）：" + r1.Method); }
            else { fail++; L("FAIL 打孔失败（DrillRequests）：成功 " + r1.Created + "，失败 " + string.Join("；", r1.Failures.ToArray())); }
        }catch(Exception e){ fail++; L("FAIL 抛异常：" + e.GetType().Name + " " + e.Message); }
        L("ASM-DRILL pass=" + pass + " fail=" + fail);
        return fail == 0 ? 0 : 1;
    }
}
