using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using TianGongCadSuite;
using A=SolidEdgeAssembly;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;
using F=SolidEdgeFramework;

// 复刻面板路径：用 asm.CreateReference(occ, face) 取面（和用户点选一样），再调 DrillRequests。
// 用来在"原位编辑"状态下快速验证 AnchorToActivePart / 写入是否成功。
// 用法: PanelPathDrill.exe <asm路径>
class PanelPathDrill {
    static void L(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    static F.Application app;
    static void Pump(int n){ for (int i = 0; i < n; i++){ try { app.DoIdle(); } catch {} Thread.Sleep(250); } }

    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); }
        catch (Exception e) { L("FATAL 连不上 CAD：" + e.Message); return 2; }
        object act = null; try { act = app.ActiveDocument; } catch {}
        string actName = "?"; try { dynamic d = act; actName = Convert.ToString(d.Name); } catch {}
        L("活动文档 = " + actName);
        var activePart = act as P.PartDocument;
        L("是零件文档吗 = " + (activePart != null));

        var asm = (A.AssemblyDocument)app.Documents.Open(args[0]);
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
        // 目标件优先选"正在编辑的那个零件"
        if (activePart != null){
            string an = ""; try { an = activePart.FullName; } catch {}
            foreach (A.Occurrence occ in asm.Occurrences){
                var pd = occ.OccurrenceDocument as P.PartDocument; if (pd == null) continue;
                string nm = ""; try { nm = pd.FullName; } catch {}
                if (string.Equals(nm, an, StringComparison.OrdinalIgnoreCase)){ tgtOcc = occ; break; }
            }
        }
        L("参照件 " + refOcc.Name + "  目标件 " + tgtOcc.Name);

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
        // 和面板一样：通过"实例选择引用"取面
        var target = AutoHoleReader.ReadTarget(asm.CreateReference(tgtOcc, face));
        L("目标面 " + target.PartName + " " + target.Label + "   Placement=" + (target.Placement == null ? "null" : "有"));

        var refHole = AutoHoleReader.ReadReference(asm.CreateReference(refOcc, refEdge));
        var match = HoleMatcher.Match(refHole.DiameterMm);
        var centre = AutoHoleReader.Intersect(refHole, target);
        L("参考孔 Φ" + refHole.DiameterMm.ToString("0.##") + " -> " + match.Row.Size + "  孔心(" + (centre.X*1000).ToString("0.#") + "," + (centre.Y*1000).ToString("0.#") + "," + (centre.Z*1000).ToString("0.#") + ")");

        var req = new List<AutoHoleWriter.HoleRequest>();
        req.Add(new AutoHoleWriter.HoleRequest { Spec = match.Target, Centre = centre, Source = "面板路径复刻" });
        var r = AutoHoleWriter.DrillRequests(target, req);
        if (r.Created == 1 && r.Failures.Count == 0){ L("PASS 打孔成功：" + r.Method); return 0; }
        L("FAIL 打孔失败：" + string.Join("；", r.Failures.ToArray()));

        // ---- 诊断：锚定是否命中 ----
        var anchored = AutoHoleWriter.AnchorToActivePart(target);
        L("锚定结果：Part 同对象=" + ReferenceEquals(anchored.Part, target.Part) + "  Face 同对象=" + ReferenceEquals(anchored.Face, target.Face));
        L("  target.Part.FullName = " + SafeName(target.Part));
        L("  active.FullName       = " + (activePart == null ? "null" : SafeName(activePart)));

        // ---- 对照 B：完全照 InPlaceDrill 的做法（活动文档的面 + 单位变换） ----
        if (activePart != null && activePart.Models.Count >= 1){
            var am = (P.Model)activePart.Models.Item(1);
            G.Face tf2 = null; double bz = double.MinValue; double[] fp = new double[3], fn = new double[3];
            foreach (G.Face f in (G.Faces)((G.Body)am.Body).get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll)){
                var pl = f.Geometry as G.Plane; if (pl == null) continue;
                Array p = new double[3], n = new double[3];
                try { pl.GetPlaneData(ref p, ref n); } catch { continue; }
                if (Math.Abs(Convert.ToDouble(n.GetValue(2))) < 0.9) continue;
                double z = Convert.ToDouble(p.GetValue(2));
                if (z > bz){ bz = z; tf2 = f; fp = new double[]{ Convert.ToDouble(p.GetValue(0)), Convert.ToDouble(p.GetValue(1)), z }; fn = new double[]{ Convert.ToDouble(n.GetValue(0)), Convert.ToDouble(n.GetValue(1)), Convert.ToDouble(n.GetValue(2)) }; }
            }
            if (tf2 != null){
                var t2 = new TargetFace { Plane = new PlaneInput(V3.From(fp), V3.From(fn).Unit(), "顶面"), Face = tf2, Part = activePart, Placement = Transform.Identity, Label = "顶面", PartName = SafeName(activePart) };
                var req2 = new List<AutoHoleWriter.HoleRequest>();
                req2.Add(new AutoHoleWriter.HoleRequest { Spec = match.Target, Centre = new V3(fp[0] + 0.03, fp[1] + 0.03, fp[2]), Source = "对照B" });
                var r2 = AutoHoleWriter.DrillRequests(t2, req2);
                L(r2.Created == 1 && r2.Failures.Count == 0 ? ("对照B PASS 成功：" + r2.Method) : ("对照B FAIL：" + string.Join("；", r2.Failures.ToArray())));
            }
        }
        return 0;
    }
    static string SafeName(P.PartDocument p){ try { return p.FullName; } catch { return "?"; } }
}
