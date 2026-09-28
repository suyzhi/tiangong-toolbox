using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using TianGongCadSuite;
using A=SolidEdgeAssembly;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;
using F=SolidEdgeFramework;

// 逐咒语实验：在"打开磁盘装配"这个上下文里，依次尝试各种让实例可写的手段，每试一个就打一次孔。
// 用法: AsmDrill9.exe <asm路径>
class AsmDrill9 {
    static void L(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    static object M = Type.Missing;
    static F.Application app;
    static A.AssemblyDocument asm;
    static A.Occurrence refOcc, tgtOcc;
    static G.Edge refEdge;
    static void Pump(int n){ for (int i = 0; i < n; i++){ try { app.DoIdle(); } catch {} Thread.Sleep(250); } }

    // 每次尝试前重新解析（模式切换后旧对象可能失效）
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

        asm = (A.AssemblyDocument)app.Documents.Open(args[0]);
        L("装配 " + asm.FullName);
        Pump(8);

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

        var tgtDoc0 = (P.PartDocument)tgtOcc.OccurrenceDocument;
        int winCount = -1; try { winCount = tgtDoc0.Windows.Count; } catch (Exception e) { L("读零件窗口数失败：" + e.Message); }
        L("零件文档窗口数=" + winCount);

        L(TryDrill("① 现状"));

        // 咒语 A：ActivateAll
        try { asm.ActivateAll(); L("已 asm.ActivateAll()"); } catch (Exception e) { L("ActivateAll 失败：" + e.Message); }
        Pump(10);
        L(TryDrill("② ActivateAll 之后"));

        // 咒语 B：EditAssembly
        try { asm.EditAssembly(); L("已 asm.EditAssembly()"); } catch (Exception e) { L("EditAssembly 失败：" + e.Message); }
        Pump(10);
        L(TryDrill("③ EditAssembly 之后"));

        // 咒语 C：激活零件文档的窗口（如果有）
        try {
            var pd = (P.PartDocument)tgtOcc.OccurrenceDocument;
            if (pd.Windows.Count > 0){ dynamic w = pd.Windows.Item(1); w.Activate(); L("已激活零件窗口"); }
            else L("零件没有窗口，跳过");
        } catch (Exception e) { L("激活零件窗口失败：" + e.Message); }
        Pump(10);
        L(TryDrill("④ 激活零件窗口之后"));

        L("ASM-DRILL9 结束");
        return 0;
    }
}
