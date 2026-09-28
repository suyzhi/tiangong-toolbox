using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using TianGongCadSuite;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;
using F=SolidEdgeFramework;

// 判定实验：对**当前活动文档**（= 鼠标双击进入原位编辑后的那个零件）打一个 Φ6 通孔。
// 用法: InPlaceDrill.exe
class InPlaceDrill {
    static void L(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    static F.Application app;
    static void Pump(int n){ for (int i = 0; i < n; i++){ try { app.DoIdle(); } catch {} Thread.Sleep(250); } }

    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); }
        catch (Exception e) { L("FATAL 连不上 CAD：" + e.Message); return 2; }
        try { app.Visible = true; app.ScreenUpdating = true; } catch { }
        Pump(6);

        object active = null; try { active = app.ActiveDocument; } catch (Exception e) { L("读活动文档失败：" + e.Message); }
        P.PartDocument part = active as P.PartDocument;
        if (part == null){
            string nm = "?"; try { dynamic d = active; nm = Convert.ToString(d.Name); } catch {}
            L("活动文档不是零件（" + nm + "）。请先双击零件进入原位编辑，再跑这个驱动。");
            return 3;
        }
        string pn = ""; try { pn = part.Name; } catch {}
        bool ro = false; try { ro = part.ReadOnly; } catch {}
        L("活动零件文档 " + pn + "  ReadOnly=" + ro);

        var model = (P.Model)part.Models.Item(1);
        G.Face face = null; double bestZ = double.MinValue; double[] fpt = new double[3], fnv = new double[3];
        foreach (G.Face f in (G.Faces)((G.Body)model.Body).get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll)){
            var pl = f.Geometry as G.Plane; if (pl == null) continue;
            Array p = new double[3], n = new double[3];
            try { pl.GetPlaneData(ref p, ref n); } catch { continue; }
            if (Math.Abs(Convert.ToDouble(n.GetValue(2))) < 0.9) continue;
            double z = Convert.ToDouble(p.GetValue(2));
            if (z > bestZ) { bestZ = z; face = f; fpt = new double[]{ Convert.ToDouble(p.GetValue(0)), Convert.ToDouble(p.GetValue(1)), z }; fnv = new double[]{ Convert.ToDouble(n.GetValue(0)), Convert.ToDouble(n.GetValue(1)), Convert.ToDouble(n.GetValue(2)) }; }
        }
        if (face == null){ L("找不到水平面"); return 4; }
        L("打孔面平面点 (" + (fpt[0]*1000).ToString("0.#") + "," + (fpt[1]*1000).ToString("0.#") + "," + (fpt[2]*1000).ToString("0.#") + ") mm");

        var target = new TargetFace {
            Plane = new PlaneInput(V3.From(fpt), V3.From(fnv).Unit(), "顶面"),
            Face = face, Part = part, Placement = Transform.Identity, Label = "顶面", PartName = pn
        };
        var spec = new HoleSpec { Kind = HoleKind.Through, HoleDiameter = 6, Depth = 0 };
        var centre = new V3(fpt[0] + 0.03, fpt[1] + 0.03, fpt[2]);
        double v0 = 0, v1 = 0; try { v0 = ((G.Body)model.Body).Volume; } catch {}
        try {
            var res = AutoHoleWriter.Drill(target, spec, new V3[]{ centre });
            try { v1 = ((G.Body)model.Body).Volume; } catch {}
            if (res.Created == 1 && res.Failures.Count == 0){ L("PASS 原位编辑下打孔成功：" + res.Method + "，切除 " + ((v0-v1)*1e9).ToString("0.#") + " mm³"); return 0; }
            L("FAIL 打孔失败：成功 " + res.Created + "，失败 " + string.Join("；", res.Failures.ToArray()));
        } catch (Exception e) { L("FAIL 异常：" + e.GetType().Name + " " + e.Message); }
        L("INPLACE-DRILL 失败");
        return 1;
    }
}
