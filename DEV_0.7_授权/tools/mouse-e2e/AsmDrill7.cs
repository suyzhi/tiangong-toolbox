using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using TianGongCadSuite;
using A=SolidEdgeAssembly;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;
using F=SolidEdgeFramework;

// 判别实验 10：装配里目标实例是 ReadOnly 吗？MakeWritable() 之后能不能打孔？
// 用法: AsmDrill7.exe <asm路径>
class AsmDrill7 {
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
        var tgtDoc = (P.PartDocument)tgtOcc.OccurrenceDocument;
        string tgtPath = ""; try { tgtPath = tgtDoc.FullName; } catch { }
        bool roBefore = false; try { roBefore = tgtDoc.ReadOnly; } catch {}
        L("目标件 " + tgtOcc.Name + "  ReadOnly(前)=" + roBefore);
        bool fileRoBefore = File.Exists(tgtPath) && (File.GetAttributes(tgtPath) & FileAttributes.ReadOnly) != 0;
        L("  文件只读属性(前)=" + fileRoBefore);

        var refHole = AutoHoleReader.ReadReference(asm.CreateReference(refOcc, refEdge));
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

        Func<string> attempt = () => {
            var req = new List<AutoHoleWriter.HoleRequest>();
            req.Add(new AutoHoleWriter.HoleRequest { Spec = match.Target, Centre = centre, Source = "MakeWritable" });
            var r = AutoHoleWriter.DrillRequests(target, req);
            return r.Created == 1 && r.Failures.Count == 0 ? null : string.Join("；", r.Failures.ToArray());
        };

        string e0 = attempt();
        L("打孔（未处理）：" + (e0 ?? "居然成功"));

        // 关键：把实例改成可写
        try { tgtOcc.MakeWritable(); L("已调用 Occurrence.MakeWritable()"); } catch (Exception e) { L("MakeWritable 失败：" + e.Message); }
        Pump(8);
        bool roAfter = false; try { roAfter = tgtDoc.ReadOnly; } catch {}
        bool fileRoAfter = File.Exists(tgtPath) && (File.GetAttributes(tgtPath) & FileAttributes.ReadOnly) != 0;
        L("  ReadOnly(后)=" + roAfter + "  文件只读属性(后)=" + fileRoAfter);

        string e1 = attempt();
        L("打孔（MakeWritable 之后）：" + (e1 ?? "PASS 成功了"));
        L("ASM-DRILL7 " + (e1 == null ? "MAKEWRITABLE-OK" : "STILL-FAIL"));
        return e1 == null ? 0 : 1;
    }
}
