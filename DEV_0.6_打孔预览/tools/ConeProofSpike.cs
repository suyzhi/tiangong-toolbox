using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using TianGongCadSuite;
using F=SolidEdgeFramework;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;
using S=SolidEdgeFrameworkSupport;

// 视觉对照件：同一块板上并排打 4 种孔，然后把板沿 Y 剖开一半，
// 孔的剖面直接暴露出来 —— "螺纹孔到底带不带锥面"肉眼可判。
//   ① x=15  旧写法（修复前插件的做法）：AddEx(igTappedHole,…)
//   ② x=35  新写法（产品代码 AutoHoleWriter.Drill）：普通孔 + 螺纹数据，贯通
//   ③ x=55  新写法：V 型底盲孔 6mm
//   ④ x=75  新写法：贯通 + 孔口倒角 0.5×45°
class ConeProofSpike {
    static object M = Type.Missing;
    const string CadHome = @"C:\Program Files\NDS\TianGong 2025";
    static string Template { get { return Path.Combine(CadHome,"Template","ISO Metric","iso metric part.par"); } }
    static string OutDir;

    [STAThread] static int Main(string[] args){
        OutDir = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(Environment.CurrentDirectory, "artifacts", "cone-proof");
        Directory.CreateDirectory(OutDir);
        F.Application app = null; bool own = false;
        try {
            try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); }
            catch { app = (F.Application)Activator.CreateInstance(Type.GetTypeFromProgID("SolidEdge.Application")); own = true; }
            try { app.Visible = false; } catch { }
            Console.WriteLine("VERSION " + app.Version);
            P.PartDocument part = (P.PartDocument)app.Documents.Add("SolidEdge.PartDocument", Template);
            part.ModelingMode = P.ModelingModeConstants.seModelingModeOrdered;
            P.Model model = BuildPlate(part, 0.1, 0.06, 0.01);
            Console.WriteLine("PLATE OK  volume=" + (Vol(model)*1e9).ToString("0.#"));

            // ① 旧写法：修复前插件就是在这一行上出问题的
            string why1 = DrillOld(part, model, 0.015, 0.03);
            Console.WriteLine("OLD-RECIPE " + (why1.Length == 0 ? "OK" : why1));

            // ②③④ 新写法：直接调用产品代码
            var row = HoleMatcher.Find("M6").Value;
            var through = HoleMatcher.FromRow(row, HoleKind.Tapped);
            string w1, w2, w3;
            var r1 = AutoHoleWriter.Drill(Target(part, model), through, new V3[]{ new V3(0.035, 0.03, 0.005) });
            w1 = r1.Created == 1 ? "" : string.Join("；", r1.Failures.ToArray());
            Console.WriteLine("NEW-THROUGH created=" + r1.Created + " method=" + r1.Method + " audit=" + r1.Audit + " " + w1);

            var blind = through.Clone(); blind.Depth = 6; blind.Bottom = HoleBottom.VBottom;
            var r2 = AutoHoleWriter.Drill(Target(part, model), blind, new V3[]{ new V3(0.055, 0.03, 0.005) });
            w2 = r2.Created == 1 ? "" : string.Join("；", r2.Failures.ToArray());
            Console.WriteLine("NEW-VBOTTOM created=" + r2.Created + " method=" + r2.Method + " " + w2);

            var cham = through.Clone(); cham.Chamfer = true; cham.ChamferSetback = 0.5; cham.ChamferAngle = 45;
            var r3 = AutoHoleWriter.Drill(Target(part, model), cham, new V3[]{ new V3(0.075, 0.03, 0.005) });
            w3 = r3.Created == 1 ? "" : string.Join("；", r3.Failures.ToArray());
            Console.WriteLine("NEW-CHAMFER created=" + r3.Created + " method=" + r3.Method + " " + w3);

            // 剖开：切掉 y < 0.03 的那一半，孔的半剖面朝 -Y
            CutHalf(part, model);
            Console.WriteLine("SECTION OK volume=" + (Vol(model)*1e9).ToString("0.#"));

            string path = Path.Combine(OutDir, "ConeProof.par");
            part.SaveAs(path);
            Console.WriteLine("SAVED " + path);
            try { part.Close(false); } catch { }
        } catch (Exception e) {
            Console.WriteLine("FATAL " + e.GetType().Name + ": " + e.Message);
            Console.WriteLine(e.StackTrace);
        } finally {
            try { if (app != null && own) app.Quit(); } catch { }
        }
        Console.WriteLine("CONE-PROOF DONE");
        return 0;
    }

    static double Vol(P.Model m){ return ((G.Body)m.Body).Volume; }

    // 每次打孔前重新取一次面：打完孔以后缓存的面对象会失效
    static TargetFace Target(P.PartDocument part, P.Model model){
        var face = PlanarFaceAtZ(model, 0.005);
        if (face == null) throw new InvalidOperationException("找不到顶面");
        return new TargetFace {
            Plane = new PlaneInput(new V3(0, 0, 0.005), new V3(0, 0, 1), "顶面"),
            Face = face, Part = part, Placement = Transform.Identity, Label = "顶面", PartName = "ConeProof"
        };
    }

    static G.Face PlanarFaceAtZ(P.Model model, double z){
        var body = (G.Body)model.Body;
        foreach (G.Face f in (G.Faces)body.get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll)){
            var pl = f.Geometry as G.Plane;
            if (pl == null) continue;
            Array p = new double[3], n = new double[3];
            pl.GetPlaneData(ref p, ref n);
            if (Math.Abs(D(n,2)) < 1e-6) continue;
            if (Math.Abs(D(p,2) - z) < 1e-6) return f;
        }
        return null;
    }
    static double D(Array a, int i){ return Convert.ToDouble(a.GetValue(i)); }

    // 修复前插件的写法（AutoHoleCad.BuildHoleData 的旧版本）：直接用 igTappedHole + 全部缺省参数
    static string DrillOld(P.PartDocument part, P.Model model, double x, double y){
        try {
            var xy = FindXY(part);
            var plane = part.RefPlanes.AddParallelByDistance(xy, 0.005, P.ReferenceElementConstants.igNormalSide, M, M, M, M);
            var prof = part.ProfileSets.Add().Profiles.Add(plane);
            double x2, y2; prof.Convert3DCoordinate(x, y, 0.005, out x2, out y2);
            prof.Holes2d.Add(x2, y2);
            if (prof.End(P.ProfileValidationType.igProfileClosed) != 0) return "轮廓不闭合";
            P.HoleData hd = null;
            foreach (var std in new[] { "ISO Metric" }) {
                try {
                    // 37 个参数：(HoleType, Standard, SubType, Size, Fit) + 32 个缺省值
                    hd = part.HoleDataCollection.AddEx(P.FeaturePropertyConstants.igTappedHole, std, M, "M6", M,
                        M, M, M, M, M, M, M, M, M, M, M, M, M, M, M, M, M, M, M, M, M, M, M, M, M, M, M, M, M, M, M, M);
                    if (hd != null) break;
                } catch { }
            }
            if (hd == null) return "AddEx 失败";
            object desc = null;
            foreach (var side in new[]{ P.FeaturePropertyConstants.igLeft, P.FeaturePropertyConstants.igRight }) {
                P.Hole h = null;
                try { h = model.Holes.AddThroughAll(prof, side, hd); } catch (Exception e) { return "打孔异常 " + e.Message; }
                if (h == null) continue;
                if ((int)h.GetStatusEx(out desc) == (int)P.FeatureStatusConstants.igFeatureOK) {
                    Console.WriteLine("OLD-RECIPE holeType=" + hd.HoleType + " dia=" + (hd.HoleDiameter*1000).ToString("0.###")
                        + " cbd=" + (hd.CounterboreDiameter*1000).ToString("0.###") + " csd=" + (hd.CountersinkDiameter*1000).ToString("0.###")
                        + " csa=" + hd.CountersinkAngle.ToString("0.#"));
                    return "";
                }
                try { h.Delete(); } catch { }
            }
            return "两方向都失败";
        } catch (Exception e) { return "异常 " + e.Message; }
    }

    // 在顶面上画一个大矩形，向下通切掉 y < 0.03 的半块
    static void CutHalf(P.PartDocument part, P.Model model){
        var xy = FindXY(part);
        var plane = part.RefPlanes.AddParallelByDistance(xy, 0.005, P.ReferenceElementConstants.igNormalSide, M, M, M, M);
        var prof = part.ProfileSets.Add().Profiles.Add(plane);
        var l = new S.Line2d[4];
        l[0] = prof.Lines2d.AddBy2Points(-0.02, -0.02, 0.12, -0.02);
        l[1] = prof.Lines2d.AddBy2Points(0.12, -0.02, 0.12, 0.03);
        l[2] = prof.Lines2d.AddBy2Points(0.12, 0.03, -0.02, 0.03);
        l[3] = prof.Lines2d.AddBy2Points(-0.02, 0.03, -0.02, -0.02);
        var rel = (S.Relations2d)prof.Relations2d;
        for (int i = 0; i < 4; i++) rel.AddKeypoint(l[i], (int)SolidEdgeConstants.KeypointIndexConstants.igLineEnd, l[(i+1)%4], (int)SolidEdgeConstants.KeypointIndexConstants.igLineStart);
        int st = prof.End(P.ProfileValidationType.igProfileClosed);
        Console.WriteLine("SECTION profile end=" + st);
        if (st != 0) throw new InvalidOperationException("剖切轮廓不闭合");
        object desc = null;
        foreach (var side in new[]{ P.FeaturePropertyConstants.igLeft, P.FeaturePropertyConstants.igRight }) {
            P.ExtrudedCutout cut = null;
            try { cut = model.ExtrudedCutouts.AddThroughAll(prof, P.FeaturePropertyConstants.igRight, side); }
            catch (Exception e) { Console.WriteLine("cut side " + side + " threw " + e.Message); continue; }
            if (cut == null) continue;
            if ((int)cut.GetStatusEx(out desc) == (int)P.FeatureStatusConstants.igFeatureOK) { Console.WriteLine("cut OK side=" + side); return; }
            try { cut.Delete(); } catch { }
        }
        throw new InvalidOperationException("剖切失败");
    }

    static P.RefPlane FindXY(P.PartDocument part){
        foreach (P.RefPlane c in part.RefPlanes) {
            Array n = new double[3], p = new double[3], u = new double[3];
            c.GetNormal(ref n); c.GetRootPoint(ref p); c.GetReferenceDirection(ref u);
            if (Math.Abs(D(n,2)-1) < 1e-8 && Math.Abs(D(p,0)) < 1e-8 && Math.Abs(D(p,1)) < 1e-8 && Math.Abs(D(u,0)-1) < 1e-8) return c;
        }
        return null;
    }

    static P.Model BuildPlate(P.PartDocument part, double w, double h, double t){
        var prof = part.ProfileSets.Add().Profiles.Add(FindXY(part));
        var L = new S.Line2d[4];
        L[0] = prof.Lines2d.AddBy2Points(0, 0, w, 0); L[1] = prof.Lines2d.AddBy2Points(w, 0, w, h);
        L[2] = prof.Lines2d.AddBy2Points(w, h, 0, h); L[3] = prof.Lines2d.AddBy2Points(0, h, 0, 0);
        var rel = (S.Relations2d)prof.Relations2d;
        for (int i = 0; i < 4; i++) { rel.AddKeypoint(L[i], (int)SolidEdgeConstants.KeypointIndexConstants.igLineEnd, L[(i+1)%4], (int)SolidEdgeConstants.KeypointIndexConstants.igLineStart); if (i%2==0) rel.AddHorizontal(L[i]); else rel.AddVertical(L[i]); }
        rel.AddKeypointFix(L[0], (int)SolidEdgeConstants.KeypointIndexConstants.igLineStart);
        if (prof.End(P.ProfileValidationType.igProfileClosed) != 0) throw new InvalidOperationException("base not closed");
        Array arr = new object[]{ prof };
        return part.Models.AddFiniteExtrudedProtrusion(1, ref arr, P.FeaturePropertyConstants.igSymmetric, t);
    }
}
