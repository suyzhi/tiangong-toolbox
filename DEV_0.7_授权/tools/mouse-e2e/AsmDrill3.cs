using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using TianGongCadSuite;
using A=SolidEdgeAssembly;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;
using F=SolidEdgeFramework;

// 判别实验 5：打开装配 -> 把目标实例原位激活 -> 打孔 -> 退出原位激活。
// 用法: AsmDrill3.exe <asm路径>
class AsmDrill3 {
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
        L("目标件 " + tgtOcc.Name + "  规格 " + match.Row.Size);

        Func<string> attempt = () => {
            var req = new List<AutoHoleWriter.HoleRequest>();
            req.Add(new AutoHoleWriter.HoleRequest { Spec = match.Target, Centre = centre, Source = "原位激活后" });
            var r = AutoHoleWriter.DrillRequests(target, req);
            return r.Created == 1 && r.Failures.Count == 0 ? null : string.Join("；", r.Failures.ToArray());
        };

        string e1 = attempt();
        L(e1 == null ? "PASS 未激活就成功" : ("FAIL 未激活：" + e1));
        if (e1 == null) return 0;

        bool on = false;
        try { tgtOcc.Activate = true; on = true; L("已原位激活目标实例，InPlaceActivated=" + asm.InPlaceActivated); }
        catch (Exception e) { L("原位激活失败：" + e.Message); }
        Pump(10);

        string e2 = on ? attempt() : "未激活";
        L(e2 == null ? "PASS 原位激活后打孔成功" : ("FAIL 原位激活后：" + e2));

        if (on) { try { tgtOcc.Activate = false; Pump(6); L("已退出原位激活，InPlaceActivated=" + asm.InPlaceActivated); } catch (Exception e) { L("退出原位激活失败：" + e.Message); } }
        L("ASM-DRILL3 " + (e2 == null ? "FIXED" : "STILL-FAIL"));
        return e2 == null ? 0 : 1;
    }
}
