using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using TianGongCadSuite;
using A=SolidEdgeAssembly;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;
using F=SolidEdgeFramework;

// 判别实验 7：在装配里把目标实例**原位激活**（CAD 官方口径），等它真正进入原位编辑后再打孔。
// 用法: AsmDrill5.exe <asm路径>
class AsmDrill5 {
    static void L(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    static F.Application app;

    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); }
        catch (Exception e) { L("FATAL 连不上 CAD：" + e.Message); return 2; }
        try { app.Visible = true; app.ScreenUpdating = true; } catch { }

        var asm = (A.AssemblyDocument)app.Documents.Open(args[0]);
        L("装配 " + asm.FullName);
        for (int i = 0; i < 8; i++){ try { app.DoIdle(); } catch {} Thread.Sleep(300); }

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
        L("目标件 " + tgtOcc.Name);

        Func<string> attempt = () => {
            var req = new List<AutoHoleWriter.HoleRequest>();
            req.Add(new AutoHoleWriter.HoleRequest { Spec = match.Target, Centre = centre, Source = "原位编辑" });
            var r = AutoHoleWriter.DrillRequests(target, req);
            return r.Created == 1 && r.Failures.Count == 0 ? null : string.Join("；", r.Failures.ToArray());
        };

        L("直接打（现状）：" + (attempt() ?? "成功"));
        L("InPlaceActivated(前)=" + asm.InPlaceActivated);

        // 原位激活目标实例
        try { tgtOcc.Activate = true; } catch (Exception e) { L("设 Activate=true 失败：" + e.Message); }
        bool on = false;
        for (int i = 0; i < 20; i++){
            try { app.DoIdle(); } catch {}
            Thread.Sleep(400);
            try { on = asm.InPlaceActivated; } catch {}
            if (on) { L("第 " + (i + 1) + " 次轮询：InPlaceActivated=True"); break; }
        }
        if (!on) L("轮询 8 秒后仍是 InPlaceActivated=False");

        string r2 = attempt();
        L("原位编辑中打孔：" + (r2 ?? "PASS 成功"));

        if (on) {
            try { tgtOcc.Activate = false; } catch (Exception e) { L("设 Activate=false 失败：" + e.Message); }
            for (int i = 0; i < 12; i++){ try { app.DoIdle(); } catch {} Thread.Sleep(400); try { if (!asm.InPlaceActivated) break; } catch {} }
            try { L("退出后 InPlaceActivated=" + asm.InPlaceActivated); } catch {}
        }
        L("ASM-DRILL5 " + (r2 == null ? "IN-PLACE-OK" : "STILL-FAIL"));
        return r2 == null ? 0 : 1;
    }
}
