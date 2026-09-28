// tools/ChamferProbe.cs —— 孔口倒角的真实几何探针（一次性证据程序，留在库里备查）。
//
// 为什么需要它：预览要画"孔口倒角 0.5×45°"这个形状，但 Solid Edge 的
//   HoleData.SetStartChamfer(Index, Setback, Angle)
// 里 Setback 到底是"轴向深度"还是"径向宽度"、Angle 是从轴线量还是从端面量，
// 文档没说清。45° 时四种解释的结果完全一样，任何非 45° 角度就会画错。
//
// 做法：10mm 厚板上打一串 Φ6 通孔，每个孔的倒角参数不同，逐个测量切除体积。
//   切除体积 - 纯圆柱体积 = 倒角环体积，再和 4 种候选几何模型对表，谁对得上就是谁。
//   4 种候选（r=孔半径, S=Setback, A=Angle）：
//     A: 轴向深 S，径向长 S·tanA      B: 轴向深 S，径向长 S/tanA
//     C: 径向长 S，轴向深 S·tanA      D: 径向长 S，轴向深 S/tanA
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

static class ChamferProbe {
    const string CadHome = @"C:\Program Files\NDS\TianGong 2025";
    static string Template { get { return Path.Combine(CadHome, "Template", "ISO Metric", "iso metric part.par"); } }
    const double T = 0.010;          // 板厚 10mm
    const double HoleDia = 6.0;      // Φ6 通孔
    static string N(double v){ return v.ToString("0.###", CultureInfo.InvariantCulture); }
    static double D(Array a,int i){ return Convert.ToDouble(a.GetValue(i)); }
    static double Vol(P.Model m){ return ((G.Body)m.Body).Volume; }

    [STAThread]
    static int Main(string[] args){
        string outDir = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
        Directory.CreateDirectory(outDir);
        using (new OleFilter()) {
            try { return Run(outDir); }
            catch (Exception e) { Console.WriteLine("CHAMFER PROBE FATAL " + e); return 1; }
        }
    }

    static int Run(string outDir){
        F.Application app;
        try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); Console.WriteLine("attached to running CAD"); }
        catch { Console.WriteLine("CHAMFER PROBE FATAL 连不上正在运行的天工CAD"); return 1; }
        app.Visible = true; app.ScreenUpdating = true;

        P.PartDocument part = null;
        object original = null;
        try { original = app.ActiveDocument; } catch { }
        try {
            part = BuildPlate(app, 0.200, 0.060, T);
            var model = (P.Model)part.Models.Item(1);
            double v0 = Vol(model);
            double cyl = Math.PI * Math.Pow(HoleDia / 2000.0, 2) * T;   // Φ6 通 10mm 的圆柱体积

            double[] set = { 0.5, 2.0, 2.0, 2.0, 1.0, 3.0 };
            double[] ang = { 45.0, 45.0, 60.0, 30.0, 90.0, 60.0 };
            int made = 0;
            Console.WriteLine("=== 孔口倒角几何探针：Φ" + N(HoleDia) + " 通孔，板厚 " + N(T*1000) + "mm，圆柱体积 " + N(cyl*1e9) + " mm³ ===");
            Console.WriteLine("Setback  Angle | 实测切除 mm³ | 倒角环 mm³ | A(轴深S,径S·tanA) B(轴深S,径S/tanA) C(径S,轴深S·tanA) D(径S,轴深S/tanA)");
            for (int i = 0; i < set.Length; i++) {
                double s = set[i], a = ang[i];
                double x = 0.015 + 0.030 * i, y = 0.030;
                var spec = new HoleSpec { Kind = HoleKind.Through, HoleDiameter = HoleDia, Depth = 0,
                                          Chamfer = true, ChamferSetback = s, ChamferAngle = a };
                var face = PlanarFaceAtZ(model, -T / 2);
                if (face == null) { Console.WriteLine("找不到下表面，跳过"); continue; }
                var target = AutoHoleReader.ReadTarget(face);
                double before = Vol(model);
                var res = AutoHoleWriter.Drill(target, spec, new V3[]{ new V3(x, y, -T / 2) });
                if (res.Created != 1) { Console.WriteLine("S=" + N(s) + " A=" + N(a) + " 打孔失败：" + string.Join("；", res.Failures.ToArray())); continue; }
                model = (P.Model)part.Models.Item(1);      // 特征增加后重新取模型
                double removed = before - Vol(model);
                double ring = removed - cyl;

                double r = HoleDia / 2000.0;
                double[] model4 = {
                    Ring(r, s, r + s * Math.Tan(a * Math.PI / 180.0)),
                    Ring(r, s, r + s / Math.Tan(a * Math.PI / 180.0)),
                    Ring(r, s * Math.Tan(a * Math.PI / 180.0), r + s),
                    Ring(r, s / Math.Tan(a * Math.PI / 180.0), r + s)
                };
                Console.WriteLine(N(s) + "  " + N(a) + "° | " + N(removed*1e9) + " | " + N(ring*1e9)
                    + " | A=" + N(model4[0]*1e9) + " B=" + N(model4[1]*1e9) + " C=" + N(model4[2]*1e9) + " D=" + N(model4[3]*1e9)
                    + " | 锥面数=" + Cones(model));
                made++;
            }
            Console.WriteLine("CHAMFER PROBE DONE 打孔 " + made + " 个");
            part.SaveAs(Path.Combine(outDir, "ChamferProbe.par"));
            return made > 0 ? 0 : 1;
        } finally {
            if (part != null) try { part.Close(false); } catch { }
            try { ((dynamic)original).Activate(); } catch { }
        }
    }

    // 倒角环体积：从半径 r0 扩到 R、轴向深 d 的圆台，减掉同深度同半径 r0 的圆柱。
    static double Ring(double r0, double d, double R){
        if (d <= 0 || R <= r0) return 0;
        return Math.PI * d / 3.0 * (R*R + R*r0 + r0*r0) - Math.PI * r0*r0 * d;
    }

    static int Cones(P.Model m){
        int n = 0;
        foreach (G.Face f in (G.Faces)((G.Body)m.Body).get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll))
            if (f.Geometry is G.Cone) n++;
        return n;
    }

    static G.Face PlanarFaceAtZ(P.Model m, double z){
        foreach (G.Face f in (G.Faces)((G.Body)m.Body).get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll)){
            var pl = f.Geometry as G.Plane; if (pl == null) continue;
            Array p=new double[3], n=new double[3]; pl.GetPlaneData(ref p, ref n);
            if (Math.Abs(Math.Abs(D(n,2)) - 1) > 1e-6) continue;
            if (Math.Abs(D(p,2)-z) < 1e-6) return f;
        }
        return null;
    }

    static P.PartDocument BuildPlate(F.Application app, double w, double h, double t){
        var part = (P.PartDocument)app.Documents.Add("SolidEdge.PartDocument", Template);
        part.ModelingMode = P.ModelingModeConstants.seModelingModeOrdered;
        P.RefPlane plane = FindXY(part);
        if (plane == null) throw new InvalidOperationException("模板缺少标准 XY 基准面。");
        var profile = part.ProfileSets.Add().Profiles.Add(plane);
        var L = new S.Line2d[4];
        L[0]=profile.Lines2d.AddBy2Points(0,0,w,0); L[1]=profile.Lines2d.AddBy2Points(w,0,w,h);
        L[2]=profile.Lines2d.AddBy2Points(w,h,0,h); L[3]=profile.Lines2d.AddBy2Points(0,h,0,0);
        var rel = (S.Relations2d)profile.Relations2d;
        for (int i=0;i<4;i++){ rel.AddKeypoint(L[i],(int)SolidEdgeConstants.KeypointIndexConstants.igLineEnd,L[(i+1)%4],(int)SolidEdgeConstants.KeypointIndexConstants.igLineStart); if(i%2==0) rel.AddHorizontal(L[i]); else rel.AddVertical(L[i]); }
        rel.AddKeypointFix(L[0],(int)SolidEdgeConstants.KeypointIndexConstants.igLineStart);
        if (profile.End(P.ProfileValidationType.igProfileClosed) != 0) throw new InvalidOperationException("矩形轮廓未闭合。");
        Array arr = new object[]{profile};
        part.Models.AddFiniteExtrudedProtrusion(1, ref arr, P.FeaturePropertyConstants.igSymmetric, t);
        return part;
    }

    static P.RefPlane FindXY(P.PartDocument part){
        foreach (P.RefPlane c in part.RefPlanes){
            Array n = new double[3], p = new double[3], u = new double[3];
            c.GetNormal(ref n); c.GetRootPoint(ref p); c.GetReferenceDirection(ref u);
            if (Math.Abs(D(n,2)-1)<1e-8 && Math.Abs(D(p,0))<1e-8 && Math.Abs(D(p,1))<1e-8 && Math.Abs(D(u,0)-1)<1e-8) return c;
        }
        return null;
    }
}
