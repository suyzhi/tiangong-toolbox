using System;
using System.IO;
using System.Runtime.InteropServices;
using TianGongCadSuite;
using F=SolidEdgeFramework;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;
using S=SolidEdgeFrameworkSupport;

// 目的：找出"同一张面打第二批孔"到底怎么才能成功。
// 依次验证四条路：
//   A. 第二次再对同一个 face 调 AddParallelByDistance      —— 复现已知的 E_FAIL
//   B. 直接复用第一次建好的那个基准面对象                  —— 最省事，若可行就用它做缓存
//   C. 用"与目标面平行的默认基准面 + 偏移"新建一个基准面   —— 板类零件通吃
//   D. 看看新建的面到底在不在 part.RefPlanes 里
class PlaneReuseSpike {
    static object M = Type.Missing;
    static P.PartDocument part; static P.Model model;
    static void Say(string s){ Console.WriteLine(s); Console.Out.Flush(); }

    static void Main(string[] args){
        var app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application");
        Say("CAD " + app.Version);
        part = (P.PartDocument)app.Documents.Add("SolidEdge.PartDocument",
            @"C:\Program Files\NDS\TianGong 2025\Template\ISO Metric\iso metric part.par");
        part.ModelingMode = P.ModelingModeConstants.seModelingModeOrdered;
        model = BuildPlate(0.1, 0.1, 0.02);
        Say("板 100x100x20 建好，体积 " + (((G.Body)model.Body).Volume * 1e9).ToString("0.#") + " mm³");
        Say("初始 RefPlanes 数量 " + part.RefPlanes.Count);

        var face1 = PlanarFaceAtZ(0.010);
        Say("A) 第一次 AddParallelByDistance(face,0) ...");
        P.RefPlane plane1 = null;
        try {
            plane1 = part.RefPlanes.AddParallelByDistance(face1, 0.0, P.ReferenceElementConstants.igNormalSide, M, M, M, M);
            Say("   成功，plane1=" + (plane1 == null ? "null" : "ok"));
        } catch (Exception e) { Say("   失败 " + e.Message); return; }
        Say("   之后 RefPlanes 数量 " + part.RefPlanes.Count);
        Say("   在集合里?" + InCollection(plane1));

        Say("B) 用 plane1 打第一个孔 ...");
        string w1 = DrillOne(plane1, 0.03, 0.03, 0.005);
        Say("   " + (w1.Length == 0 ? "成功" : "失败：" + w1));

        Say("C) 复用同一个 plane1 打第二个孔（这正是第二批孔要走的路径）...");
        string w2 = DrillOne(plane1, 0.07, 0.03, 0.005);
        Say("   " + (w2.Length == 0 ? "成功" : "失败：" + w2));

        Say("D) 重新取顶面，再 AddParallelByDistance(face2,0) —— 复现已知约束 ...");
        var face2 = PlanarFaceAtZ(0.010);
        try {
            var plane2 = part.RefPlanes.AddParallelByDistance(face2, 0.0, P.ReferenceElementConstants.igNormalSide, M, M, M, M);
            Say("   竟然成功了 plane2=" + (plane2 == null ? "null" : "ok") + "  RefPlanes=" + part.RefPlanes.Count);
            string w3 = DrillOne(plane2, 0.03, 0.07, 0.005);
            Say("   用 plane2 打孔 " + (w3.Length == 0 ? "成功" : "失败：" + w3));
        } catch (Exception e) { Say("   如预期失败：" + e.Message); }

        Say("E) 用'与目标面平行的 XY 基准面 + 偏移'新建 ...");
        try {
            P.RefPlane xy = null;
            foreach (P.RefPlane rp in part.RefPlanes) {
                Array n = new double[3], p = new double[3];
                rp.GetNormal(ref n); rp.GetRootPoint(ref p);
                if (Math.Abs(Convert.ToDouble(n.GetValue(2)) - 1) < 1e-6 && Math.Abs(Convert.ToDouble(p.GetValue(2))) < 1e-9) { xy = rp; break; }
            }
            if (xy == null) Say("   找不到 XY 基准面");
            else {
                var plane3 = part.RefPlanes.AddParallelByDistance(xy, 0.010, P.ReferenceElementConstants.igNormalSide, M, M, M, M);
                Say("   成功 plane3=" + (plane3 == null ? "null" : "ok") + "  RefPlanes=" + part.RefPlanes.Count + "  在集合里?" + InCollection(plane3));
                string w4 = DrillOne(plane3, 0.07, 0.07, 0.005);
                Say("   用 plane3 打孔 " + (w4.Length == 0 ? "成功" : "失败：" + w4));
            }
        } catch (Exception e) { Say("   失败：" + e.Message); }

        string outp = Path.Combine(args.Length > 0 ? args[0] : ".", "PlaneReuse.par");
        try { part.SaveAs(outp); Say("SAVED " + outp); } catch (Exception e) { Say("保存失败 " + e.Message); }
        try { part.Close(false); } catch { }
        Say("SPIKE DONE");
    }

    static bool InCollection(P.RefPlane target){
        try {
            foreach (P.RefPlane rp in part.RefPlanes) if (rp == target) return true;
        } catch { }
        return false;
    }

    static string DrillOne(P.RefPlane plane, double x, double y, double dia){
        try {
            var prof = part.ProfileSets.Add().Profiles.Add(plane);
            double x2, y2; prof.Convert3DCoordinate(x, y, 0.010, out x2, out y2);
            prof.Holes2d.Add(x2, y2);
            if (prof.End(P.ProfileValidationType.igProfileClosed) != 0) return "轮廓不闭合";
            var hd = part.HoleDataCollection.Add(P.FeaturePropertyConstants.igRegularHole, dia,
                0.0, 0.0, 0.0, 0.0, 0.0, P.FeaturePropertyConstants.igNone, M, M, M, M, M,
                P.FeaturePropertyConstants.igVBottomDimToFlat, M, M, M, M, M, M, true);
            object desc = null;
            foreach (var side in new[]{ P.FeaturePropertyConstants.igLeft, P.FeaturePropertyConstants.igRight }) {
                P.Hole h = null;
                try { h = model.Holes.AddThroughAll(prof, side, hd); } catch (Exception e) { return "异常 " + e.Message; }
                if (h == null) continue;
                if ((int)h.GetStatusEx(out desc) == (int)P.FeatureStatusConstants.igFeatureOK) return "";
                try { h.Delete(); } catch { }
            }
            return "两方向都失败";
        } catch (Exception e) { return "异常 " + e.Message; }
    }

    static G.Face PlanarFaceAtZ(double z){
        foreach (G.Face f in (G.Faces)((G.Body)model.Body).get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll)) {
            var pl = f.Geometry as G.Plane; if (pl == null) continue;
            Array p = new double[3], n = new double[3];
            pl.GetPlaneData(ref p, ref n);
            if (Math.Abs(D(n,2)) < 1e-6) continue;
            if (Math.Abs(D(p,2) - z) < 1e-6) return f;
        }
        return null;
    }
    static double D(Array a, int i){ return Convert.ToDouble(a.GetValue(i)); }
    static P.RefPlane FindXY(){
        foreach (P.RefPlane c in part.RefPlanes) {
            Array n = new double[3], p = new double[3], u = new double[3];
            c.GetNormal(ref n); c.GetRootPoint(ref p); c.GetReferenceDirection(ref u);
            if (Math.Abs(D(n,2)-1) < 1e-8 && Math.Abs(D(p,0)) < 1e-8 && Math.Abs(D(p,1)) < 1e-8 && Math.Abs(D(u,0)-1) < 1e-8) return c;
        }
        return null;
    }
    static P.Model BuildPlate(double w, double h, double t){
        var prof = part.ProfileSets.Add().Profiles.Add(FindXY());
        var L = new S.Line2d[4];
        L[0] = prof.Lines2d.AddBy2Points(0, 0, w, 0); L[1] = prof.Lines2d.AddBy2Points(w, 0, w, h);
        L[2] = prof.Lines2d.AddBy2Points(w, h, 0, h); L[3] = prof.Lines2d.AddBy2Points(0, h, 0, 0);
        var rel = (S.Relations2d)prof.Relations2d;
        for (int i = 0; i < 4; i++) { rel.AddKeypoint(L[i], (int)SolidEdgeConstants.KeypointIndexConstants.igLineEnd, L[(i+1)%4], (int)SolidEdgeConstants.KeypointIndexConstants.igLineStart); if (i%2==0) rel.AddHorizontal(L[i]); else rel.AddVertical(L[i]); }
        rel.AddKeypointFix(L[0], (int)SolidEdgeConstants.KeypointIndexConstants.igLineStart);
        if (prof.End(P.ProfileValidationType.igProfileClosed) != 0) throw new InvalidOperationException("底板不闭合");
        Array arr = new object[]{ prof };
        return part.Models.AddFiniteExtrudedProtrusion(1, ref arr, P.FeaturePropertyConstants.igSymmetric, t);
    }
}
