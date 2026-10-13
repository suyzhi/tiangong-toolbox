using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using F=SolidEdgeFramework;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;
using S=SolidEdgeFrameworkSupport;

// 孔标准探针（2026-10-13）：为什么自动打孔打出来的孔，用天工自带的孔命令打开时规格显示 "*"。
// 一块 200×100×20 的板，上面两排孔：
//   上排 = 现有配方（"ISO Metric"，子类型/大小/配合留空，尺寸用插件自己的表）
//   下排 = 新配方（按天工孔标准表 GB Metric 显式给 标准/子类型/大小/配合，尺寸取表里那一行）
// 每个孔一个特征，名字写明是哪一种；回读 HoleData 的 Standard/SubType/Size/Fit 打出来，零件存盘供鼠标实测打开对比。
class HoleStdProbe {
    static object M = Type.Missing;
    static F.Application app;
    const string Tpl = @"C:\Program Files\NDS\TianGong 2025\Template\ISO Metric\";
    static void Say(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    static double D(Array a, int i){ return Convert.ToDouble(a.GetValue(i)); }
    const double T = 0.02;

    static int Main(string[] args){
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        string outDir = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
        Directory.CreateDirectory(outDir);
        app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application");
        try { app.DisplayAlerts = false; } catch { }
        Say("CAD " + app.Version);
        var part = (P.PartDocument)app.Documents.Add("SolidEdge.PartDocument", Tpl + "iso metric part.par");
        part.ModelingMode = P.ModelingModeConstants.seModelingModeOrdered;
        var model = Plate(part, 0.2, 0.1, T);
        var top = part.RefPlanes.AddParallelByDistance(TopFace(model), 0.0, P.ReferenceElementConstants.igNormalSide, M, M, M, M);
        var none = P.FeaturePropertyConstants.igNone; var vd = P.FeaturePropertyConstants.igVBottomDimToFlat;
        var cbTop = P.FeaturePropertyConstants.igCounterboreProfileIsAtTop;

        // ---- 上排：现有配方 ----
        Make(part, model, top, 0.025, 0.07, "旧-通孔Φ6.6", Ex(part, P.FeaturePropertyConstants.igRegularHole, "ISO Metric", M, M, M, 0.0066, 0.0, 0.0, 0.0, 0.0, none, M, M, M, cbTop));
        Make(part, model, top, 0.075, 0.07, "旧-螺纹M6", Tap(Ex(part, P.FeaturePropertyConstants.igRegularHole, "ISO Metric", M, "M6", M, 0.004917, 0.0, 0.0, 0.0, 0.0, P.FeaturePropertyConstants.igTappedHole, 0.004917, P.FeaturePropertyConstants.igThroughAll, M, cbTop), "M6"));
        Make(part, model, top, 0.125, 0.07, "旧-沉孔M6", Ex(part, P.FeaturePropertyConstants.igCounterboreHole, "ISO Metric", M, M, M, 0.0066, 0.011, 0.0065, 0.0, 0.0, none, M, M, M, cbTop));
        Make(part, model, top, 0.175, 0.07, "旧-锥沉M6", Ex(part, P.FeaturePropertyConstants.igCountersinkHole, "ISO Metric", M, M, M, 0.0066, 0.0, 0.0, 0.010, 90.0, none, M, M, M, cbTop));

        // ---- 下排：按天工 GB Metric 表 ----
        Make(part, model, top, 0.025, 0.03, "新-通孔Φ6.6", Ex(part, P.FeaturePropertyConstants.igRegularHole, "GB Metric", "钻头大小", "6.6", "精确", 0.0066, 0.0, 0.0, 0.0, 0.0, none, M, M, M, cbTop));
        Make(part, model, top, 0.075, 0.03, "新-螺纹M6", Tap(Ex(part, P.FeaturePropertyConstants.igRegularHole, "GB Metric", "标准螺纹", "M6", M, 0.004917, 0.0, 0.0, 0.0, 0.0, P.FeaturePropertyConstants.igTappedHole, 0.004917, P.FeaturePropertyConstants.igThroughAll, M, cbTop), "M6"));
        Make(part, model, top, 0.125, 0.03, "新-沉孔M6", Ex(part, P.FeaturePropertyConstants.igCounterboreHole, "GB Metric", "内六角螺钉", "M6", "普通 (H13)", 0.0066, 0.011, 0.0068, 0.0, 0.0, none, M, M, M, cbTop));
        Make(part, model, top, 0.175, 0.03, "新-锥沉M6", Ex(part, P.FeaturePropertyConstants.igCountersinkHole, "GB Metric", "机用", "M6", "普通 (H13)", 0.0066, 0.0, 0.0, 0.0126, 90.0, none, M, M, M, cbTop));

        string path = Path.Combine(outDir, "HoleStd.par");
        part.SaveAs(path);
        Say("SAVED " + path);
        // 重开再读一遍：看 CAD 存进文件的标准字段
        part.Close(false);
        var re = (P.PartDocument)app.Documents.Open(path);
        var m2 = (P.Model)re.Models.Item(1);
        for (int i = 1; i <= m2.Holes.Count; i++) Dump("重开 ", m2.Holes.Item(i));
        Say("PROBE DONE");
        return 0;
    }

    static P.HoleData Ex(P.PartDocument part, P.FeaturePropertyConstants type, object std, object sub, object size, object fit, double dia,
                         double cbd, double cbdep, double csd, double csa, P.FeaturePropertyConstants treat, object minor, object tdm, object td,
                         P.FeaturePropertyConstants cbLoc){
        return part.HoleDataCollection.AddEx(type, std, sub, size, fit, dia,
            cbd, cbdep, csd, csa, 0.0,
            treat, M, M, minor, tdm, td, P.FeaturePropertyConstants.igVBottomDimToFlat, M,
            type == P.FeaturePropertyConstants.igCounterboreHole ? (object)cbLoc : M, M, M, M, M, true,
            M, M, M, M, M, M, M, M, M, M, M, M);
    }
    static P.HoleData Tap(P.HoleData d, string size){
        try { d.ThreadExternalDiameter = 0.006; } catch (Exception e) { Say("   公称径 " + e.Message); }
        try { d.ThreadDescription = size; } catch (Exception e) { Say("   规格 " + e.Message); }
        return d;
    }

    static void Make(P.PartDocument part, P.Model model, P.RefPlane plane, double x, double y, string name, P.HoleData data){
        var prof = part.ProfileSets.Add().Profiles.Add(plane);
        double x2, y2; prof.Convert3DCoordinate(x, y, T, out x2, out y2);
        prof.Holes2d.Add(x2, y2); prof.End(P.ProfileValidationType.igProfileClosed);
        P.Hole h = null;
        foreach (var side in new[]{ P.FeaturePropertyConstants.igLeft, P.FeaturePropertyConstants.igRight }) {
            try { h = model.Holes.AddThroughAll(prof, side, data); } catch (Exception e) { Say("   " + name + " 建孔异常 " + e.Message); h = null; continue; }
            object desc;
            if (h != null && (int)h.GetStatusEx(out desc) == (int)P.FeatureStatusConstants.igFeatureOK) break;
            try { if (h != null) h.Delete(); } catch { }
            h = null;
        }
        if (h == null) { Say("RESULT " + name + " 建不出来"); return; }
        try { h.Name = name; } catch { }
        Dump("RESULT ", h);
    }

    static void Dump(string tag, P.Hole h){
        var d = (P.HoleData)h.HoleData;
        var l = new List<string>();
        Add(l, "Std", () => d.Standard); Add(l, "Sub", () => d.SubType); Add(l, "Size", () => d.Size); Add(l, "Fit", () => d.Fit);
        Add(l, "Type", () => d.HoleType.ToString()); Add(l, "Dia", () => (d.HoleDiameter * 1000).ToString("0.###"));
        Add(l, "Cb", () => (d.CounterboreDiameter * 1000).ToString("0.###") + "/" + (d.CounterboreDepth * 1000).ToString("0.###"));
        Add(l, "Cs", () => (d.CountersinkDiameter * 1000).ToString("0.###") + "/" + d.CountersinkAngle.ToString("0.#"));
        Add(l, "Treat", () => d.TreatmentType.ToString()); Add(l, "Desc", () => d.ThreadDescription);
        Add(l, "Units", () => d.Units.ToString());
        Say(tag + h.Name + "：" + string.Join(" ", l.ToArray()));
    }
    static void Add(List<string> l, string k, Func<string> f){ try { l.Add(k + "=「" + f() + "」"); } catch (Exception e) { l.Add(k + "=!" + e.HResult.ToString("X8")); } }

    static P.RefPlane FindXY(P.PartDocument part){
        foreach (P.RefPlane c in part.RefPlanes) {
            Array n = new double[3], p = new double[3], u = new double[3];
            c.GetNormal(ref n); c.GetRootPoint(ref p); c.GetReferenceDirection(ref u);
            if (Math.Abs(D(n,2)-1) < 1e-8 && Math.Abs(D(p,0)) < 1e-8 && Math.Abs(D(p,1)) < 1e-8 && Math.Abs(D(u,0)-1) < 1e-8) return c;
        }
        return null;
    }
    static P.Model Plate(P.PartDocument part, double w, double h, double t){
        var prof = part.ProfileSets.Add().Profiles.Add(FindXY(part));
        var L = new S.Line2d[4];
        L[0] = prof.Lines2d.AddBy2Points(0, 0, w, 0); L[1] = prof.Lines2d.AddBy2Points(w, 0, w, h);
        L[2] = prof.Lines2d.AddBy2Points(w, h, 0, h); L[3] = prof.Lines2d.AddBy2Points(0, h, 0, 0);
        var rel = (S.Relations2d)prof.Relations2d;
        for (int i = 0; i < 4; i++) rel.AddKeypoint(L[i], (int)SolidEdgeConstants.KeypointIndexConstants.igLineEnd, L[(i+1)%4], (int)SolidEdgeConstants.KeypointIndexConstants.igLineStart);
        prof.End(P.ProfileValidationType.igProfileClosed);
        Array arr = new object[]{ prof };
        return part.Models.AddFiniteExtrudedProtrusion(1, ref arr, P.FeaturePropertyConstants.igRight, t);
    }
    static G.Face TopFace(P.Model m){
        foreach (G.Face f in (G.Faces)((G.Body)m.Body).get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll)) {
            var pl = f.Geometry as G.Plane; if (pl == null) continue;
            Array a = new double[3], n = new double[3]; pl.GetPlaneData(ref a, ref n);
            if (Math.Abs(Math.Abs(D(n,2)) - 1) < 1e-6 && Math.Abs(D(a,2) - T) < 1e-9) return f;
        }
        return null;
    }
}
