using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using TianGongCadSuite;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;
using F=SolidEdgeFramework;

// 判别实验：把 .par 单独当**顶层零件**打开，用产品代码打一个 Φ6 通孔。
// 用法: PartDrill.exe <par1> [par2 ...]
class PartDrill {
    static void L(string s){ Console.WriteLine(s); Console.Out.Flush(); }

    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        F.Application app;
        try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); }
        catch (Exception e) { L("FATAL 连不上 CAD：" + e.Message); return 2; }
        try { app.Visible = true; app.ScreenUpdating = true; } catch { }

        int pass = 0, fail = 0;
        foreach (string path in args){
            L("---- " + path);
            P.PartDocument doc = null;
            try { doc = (P.PartDocument)app.Documents.Open(path); }
            catch (Exception e) { L("FAIL 打开失败：" + e.Message); fail++; continue; }
            if (doc == null) { L("FAIL 打开返回 null"); fail++; continue; }
            var model = (P.Model)doc.Models.Item(1);
            G.Face face = null; double bestZ = double.MinValue; double[] fpt = new double[3], fnv = new double[3];
            foreach (G.Face f in (G.Faces)((G.Body)model.Body).get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll)){
                var pl = f.Geometry as G.Plane; if (pl == null) continue;
                Array p = new double[3], n = new double[3];
                try { pl.GetPlaneData(ref p, ref n); } catch { continue; }
                double nz = Math.Abs(Convert.ToDouble(n.GetValue(2)));
                double z = Convert.ToDouble(p.GetValue(2));
                if (nz < 0.9) continue;
                if (z > bestZ) { bestZ = z; face = f; fpt = new double[]{ Convert.ToDouble(p.GetValue(0)), Convert.ToDouble(p.GetValue(1)), z }; fnv = new double[]{ Convert.ToDouble(n.GetValue(0)), Convert.ToDouble(n.GetValue(1)), Convert.ToDouble(n.GetValue(2)) }; }
            }
            if (face == null) { L("FAIL 没有水平面"); fail++; continue; }
            L("  顶面平面点 (" + (fpt[0]*1000).ToString("0.#") + "," + (fpt[1]*1000).ToString("0.#") + "," + (fpt[2]*1000).ToString("0.#") + ") mm");
            var target = new TargetFace {
                Plane = new PlaneInput(V3.From(fpt), V3.From(fnv).Unit(), "顶面"),
                Face = face, Part = doc, Placement = Transform.Identity, Label = "顶面", PartName = doc.Name
            };
            var spec = new HoleSpec { Kind = HoleKind.Through, HoleDiameter = 6, Depth = 0 };
            var centre = new V3(fpt[0] + 0.03, fpt[1] + 0.03, fpt[2]);
            double v0 = 0, v1 = 0;
            try { v0 = ((G.Body)model.Body).Volume; } catch { }
            try {
                var res = AutoHoleWriter.Drill(target, spec, new V3[]{ centre });
                try { v1 = ((G.Body)model.Body).Volume; } catch { }
                if (res.Created == 1 && res.Failures.Count == 0){
                    pass++; L("PASS 打孔成功：" + res.Method + "，切除 " + ((v0 - v1) * 1e9).ToString("0.#") + " mm³");
                } else {
                    fail++; L("FAIL 打孔失败：成功 " + res.Created + "，失败 " + string.Join("；", res.Failures.ToArray()));
                }
            } catch (Exception e) { fail++; L("FAIL 抛异常：" + e.GetType().Name + " " + e.Message); }
            try { doc.Close(false); } catch { }
        }
        L("PART-DRILL pass=" + pass + " fail=" + fail);
        return fail == 0 ? 0 : 1;
    }
}
