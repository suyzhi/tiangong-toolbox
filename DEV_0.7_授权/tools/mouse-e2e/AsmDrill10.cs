using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using TianGongCadSuite;
using A=SolidEdgeAssembly;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;
using F=SolidEdgeFramework;

// 逐咒语实验 2：先等 CAD 真正就绪（连续 3 次轻调用成功），再逐条试"让实例可写"的手段，
// 每一条都重试到成功或明确失败为止，然后打一次孔。
// 用法: AsmDrill10.exe <asm路径>
class AsmDrill10 {
    static void L(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    static F.Application app;
    static A.AssemblyDocument asm;
    static A.Occurrence refOcc, tgtOcc;
    static G.Edge refEdge;
    static void Pump(int n){ for (int i = 0; i < n; i++){ try { app.DoIdle(); } catch {} Thread.Sleep(250); } }

    static void WaitReady(){
        int ok = 0;
        for (int i = 0; i < 120 && ok < 3; i++){
            try { int n = app.Documents.Count; try { app.DoIdle(); } catch {} ok++; }
            catch { ok = 0; }
            Thread.Sleep(400);
        }
        L("WaitReady 完成（连续成功 " + ok + " 次）");
    }

    // 反复调用某个动作直到不抛异常（最多 tries 次）
    static bool Retry(string label, Action a){
        for (int i = 1; i <= 10; i++){
            try { a(); L("  " + label + " 第 " + i + " 次成功"); return true; }
            catch (Exception e){
                if (i == 10){ L("  " + label + " 10 次都失败：" + e.Message); return false; }
                try { app.DoIdle(); } catch {}
                Thread.Sleep(500);
            }
        }
        return false;
    }

    static string TryDrill(string label){
        try {
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
            if (face == null) return label + "：找不到打孔面";
            var refHole = AutoHoleReader.ReadReference(asm.CreateReference(refOcc, refEdge));
            var target = AutoHoleReader.ReadTarget(asm.CreateReference(tgtOcc, face));
            var match = HoleMatcher.Match(refHole.DiameterMm);
            var centre = AutoHoleReader.Intersect(refHole, target);
            var req = new List<AutoHoleWriter.HoleRequest>();
            req.Add(new AutoHoleWriter.HoleRequest { Spec = match.Target, Centre = centre, Source = label });
            var r = AutoHoleWriter.DrillRequests(target, req);
            if (r.Created == 1 && r.Failures.Count == 0) return label + "：✅ 成功（" + r.Method + "）";
            return label + "：❌ " + string.Join("；", r.Failures.ToArray());
        } catch (Exception e) { return label + "：❌ 异常 " + e.GetType().Name + " " + e.Message; }
    }

    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); }
        catch (Exception e) { L("FATAL 连不上 CAD：" + e.Message); return 2; }
        try { app.Visible = true; app.ScreenUpdating = true; } catch { }
        WaitReady();

        asm = (A.AssemblyDocument)app.Documents.Open(args[0]);
        L("装配 " + asm.FullName);
        WaitReady();
        Pump(6);

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
        L("参照件 " + refOcc.Name + "  目标件 " + tgtOcc.Name);

        L(TryDrill("① 现状（CAD 就绪后）"));

        bool okA = Retry("asm.ActivateAll()", () => asm.ActivateAll());
        Pump(12);
        L(TryDrill("② ActivateAll 之后"));

        if (!okA) L("（ActivateAll 没成功，② 的结果仅供参考）");
        L("asm.InPlaceActivated=" + SafeInPlace());

        L("ASM-DRILL10 结束");
        return 0;
    }
    static string SafeInPlace(){ try { return asm.InPlaceActivated.ToString(); } catch (Exception e) { return "读不到(" + e.Message + ")"; } }
}
