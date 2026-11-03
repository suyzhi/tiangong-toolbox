using System;
using System.Collections.Generic;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;

namespace TianGongCadSuite {
    // 一键出图：读零件实体，统计选视图要用的特征（见 ModelFacts）。
    // 孔 = 凹的圆柱面（IsParamReversed，判据与配孔检查同一条，真机实测过）；同轴的几段（沉孔、螺纹底孔）只算一个孔。
    // 只认轴线沿 X/Y/Z 的孔和法向沿 X/Y/Z 的平面 —— 斜面、斜孔不参与选主视图（主视图总是正视图）。
    public static class DrawingModelRead {
        const double AxisCos = 0.9998;

        public static ModelFacts Facts(object document, double[] range){
            var f = new ModelFacts();
            for (int i = 0; i < 3; i++) f.Size[i] = Math.Abs(range[i + 3] - range[i]);
            f.SheetMetal = document is P.SheetMetalDocument;
            var offsets = new List<double>[] { new List<double>(), new List<double>(), new List<double>() };
            var holeAxes = new List<double[]>[] { new List<double[]>(), new List<double[]>(), new List<double[]>() };
            foreach (var body in Bodies(document)) {
                G.Faces faces;
                try { faces = (G.Faces)body.get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll); } catch { continue; }
                foreach (G.Face face in faces) {
                    object geom; try { geom = face.Geometry; } catch { continue; }
                    var plane = geom as G.Plane;
                    if (plane != null) {
                        Array p = new double[3], n = new double[3];
                        try { plane.GetPlaneData(ref p, ref n); } catch { continue; }
                        int a = AxisOf(n);
                        if (a < 0) { Chamfer(face, n, f); continue; }
                        double area = 0; try { area = face.Area; } catch { }
                        f.FaceArea[a] += area;
                        offsets[a].Add(Convert.ToDouble(p.GetValue(a)));
                        continue;
                    }
                    var cyl = geom as G.Cylinder;
                    if (cyl != null) {
                        bool hole; try { hole = face.IsParamReversed; } catch { continue; }
                        if (!hole) { EdgeRound(face, cyl, f); continue; }
                        Array bp = new double[3], ax = new double[3]; double r = 0;
                        try { cyl.GetCylinderData(ref bp, ref ax, out r); } catch { continue; }
                        int a = AxisOf(ax); if (a < 0) continue;
                        // 轴线位置（垂直于轴的两个坐标）；同轴的算一个
                        double u = Convert.ToDouble(bp.GetValue((a + 1) % 3)), v = Convert.ToDouble(bp.GetValue((a + 2) % 3));
                        bool seen = false;
                        foreach (var h in holeAxes[a]) if (Math.Abs(h[0] - u) < 1e-5 && Math.Abs(h[1] - v) < 1e-5) { seen = true; break; }
                        if (!seen) holeAxes[a].Add(new[] { u, v });
                    }
                }
            }
            for (int a = 0; a < 3; a++) { f.Holes[a] = holeAxes[a].Count; f.Steps[a] = DrawingPlan.Distinct(offsets[a], 1e-5).Count; }
            return f;
        }

        // 倒角面：法向在两个轴之间 45°、窄（两个方向的宽度都不超过 6mm）。记下边长（C 几）
        static void Chamfer(G.Face face, Array n, ModelFacts f){
            try {
                double[] v = { Math.Abs(Convert.ToDouble(n.GetValue(0))), Math.Abs(Convert.ToDouble(n.GetValue(1))), Math.Abs(Convert.ToDouble(n.GetValue(2))) };
                int big = 0; for (int i = 0; i < 3; i++) if (v[i] > 0.2) big++;
                if (big != 2) return;
                int i1 = -1, i2 = -1; for (int i = 0; i < 3; i++) if (v[i] > 0.2) { if (i1 < 0) i1 = i; else i2 = i; }
                if (Math.Abs(v[i1] - v[i2]) > 0.02) return;   // 不是 45°
                Array a = new double[3], b = new double[3]; face.GetExactRange(ref a, ref b);
                double e1 = Math.Abs(Convert.ToDouble(b.GetValue(i1)) - Convert.ToDouble(a.GetValue(i1))), e2 = Math.Abs(Convert.ToDouble(b.GetValue(i2)) - Convert.ToDouble(a.GetValue(i2)));
                double leg = Math.Min(e1, e2);
                if (leg > 0.006 || leg < 1e-5) return;
                f.Chamfers.Add(Math.Round(leg * 1000, 2));
                f.EdgeTol[i1] = Math.Max(f.EdgeTol[i1], leg); f.EdgeTol[i2] = Math.Max(f.EdgeTol[i2], leg);
            } catch { }
        }

        // 外棱倒圆（凸的四分之一圆柱面，半径 ≤ 6mm）：和倒角一样，它在视图里留下一条离外形 r 的切线，不该当台阶标
        static void EdgeRound(G.Face face, G.Cylinder cyl, ModelFacts f){
            try {
                Array bp = new double[3], ax = new double[3]; double r; cyl.GetCylinderData(ref bp, ref ax, out r);
                if (r > 0.006 || r < 1e-5) return;
                int a = AxisOf(ax); if (a < 0) return;
                Array lo = new double[3], hi = new double[3]; face.GetExactRange(ref lo, ref hi);
                for (int i = 0; i < 3; i++) { if (i == a) continue; if (Math.Abs(Convert.ToDouble(hi.GetValue(i)) - Convert.ToDouble(lo.GetValue(i))) > r * 1.05) return; }
                f.Rounds.Add(Math.Round(r * 1000, 2));
                for (int i = 0; i < 3; i++) if (i != a) f.EdgeTol[i] = Math.Max(f.EdgeTol[i], r);
            } catch { }
        }

        // ---- 孔（给孔标注用） ----
        // 凹圆柱面按轴线归成孔：同一根轴线上半径最小的那段是孔本身，更大的是沉孔；
        // 一端连着同轴锥面（钻尖）或者一块和孔差不多大的端面（平底）= 盲孔，深度 = 孔那段圆柱的长度。
        // 孔特征（含插件「多孔合一」的输入孔）上能读到规格的，按轮廓里孔心的位置挂上去：螺纹规格、螺纹深度、沉孔尺寸都用它的。
        public static List<ModelHole> Holes(object document){
            var cyls = new List<ModelHole>();
            foreach (var body in Bodies(document)) {
                G.Faces faces;
                try { faces = (G.Faces)body.get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll); } catch { continue; }
                foreach (G.Face face in faces) {
                    G.Cylinder cyl = null; try { cyl = face.Geometry as G.Cylinder; } catch { }
                    if (cyl == null) continue;
                    bool hole; try { hole = face.IsParamReversed; } catch { continue; }
                    if (!hole) continue;
                    Array bp = new double[3], ax = new double[3]; double r = 0;
                    try { cyl.GetCylinderData(ref bp, ref ax, out r); } catch { continue; }
                    V3 axis; try { axis = V3.From(ax).Unit(); } catch { continue; }
                    double lo, hi; if (!Extent(face, V3.From(bp), axis, out lo, out hi)) continue;
                    var seg = new ModelHole { Point = V3.From(bp), Axis = axis, Radius = r, Lo = lo, Hi = hi };
                    seg.BlindLo = ClosedEnd(face, axis, r, V3.From(bp) + axis * lo); seg.BlindHi = ClosedEnd(face, axis, r, V3.From(bp) + axis * hi);
                    cyls.Add(seg);
                }
            }
            var holes = ModelHole.Merge(cyls);
            try { AttachSpecs(document, holes); } catch (Exception e) { Log.Write("DrawingHoleSpecs", e); }
            return holes;
        }

        // 圆柱面沿轴线的范围（相对 Point 的参数）：用面上的圆边圆心
        static bool Extent(G.Face face, V3 p, V3 axis, out double lo, out double hi){
            lo = double.MaxValue; hi = double.MinValue;
            try {
                foreach (G.Edge e in (G.Edges)face.Edges) {
                    var c = e.Geometry as G.Circle; if (c == null) continue;
                    Array cp = new double[3]; c.GetCenterPoint(ref cp);
                    double t = (V3.From(cp) - p).Dot(axis);
                    lo = Math.Min(lo, t); hi = Math.Max(hi, t);
                }
            } catch { }
            if (lo > hi) {
                try { Array a = new double[3], b = new double[3]; face.GetExactRange(ref a, ref b); double t1 = (V3.From(a) - p).Dot(axis), t2 = (V3.From(b) - p).Dot(axis); lo = Math.Min(t1, t2); hi = Math.Max(t1, t2); } catch { return false; }
            }
            return hi - lo > 1e-7;
        }

        // 这一端是不是封口：挨着的面里有同轴锥面（钻尖），或者是垂直于轴、面积不超过孔截面 1.3 倍的平面（平底）
        static bool ClosedEnd(G.Face face, V3 axis, double r, V3 at){
            try {
                foreach (G.Edge e in (G.Edges)face.Edges) {
                    var c = e.Geometry as G.Circle; if (c == null) continue;
                    Array cp = new double[3]; c.GetCenterPoint(ref cp);
                    if ((V3.From(cp) - at).Length > 1e-5) continue;
                    int n; Array fs = new object[0]; e.GetFaces(out n, ref fs);
                    foreach (object o in fs) {
                        var f = o as G.Face; if (f == null || f.ID == face.ID) continue;
                        object g = f.Geometry;
                        var cone = g as G.Cone;
                        if (cone != null) {
                            Array p = new double[3], a = new double[3]; double rr, ang; bool exp; cone.GetConeData(ref p, ref a, out rr, out ang, out exp);
                            if (Math.Abs(Math.Abs(V3.From(a).Unit().Dot(axis)) - 1) < 1e-4 && f.IsParamReversed) return true;
                        }
                        var pl = g as G.Plane;
                        if (pl != null) {
                            Array p = new double[3], nn = new double[3]; pl.GetPlaneData(ref p, ref nn);
                            // 平底 = 一块圆盘：整块面都在孔的圆里（外接框不超过直径）。台阶环面（沉孔底）外圈比孔大，不算封口
                            if (Math.Abs(Math.Abs(V3.From(nn).Unit().Dot(axis)) - 1) < 1e-4 && DiskWithin(f, at, axis, r)) return true;
                        }
                    }
                }
            } catch { }
            return false;
        }

        static bool DiskWithin(G.Face f, V3 center, V3 axis, double r){
            try {
                Array a = new double[3], b = new double[3]; f.GetExactRange(ref a, ref b);
                var lo = V3.From(a); var hi = V3.From(b);
                // 轴对齐的外框：垂直于轴的两个方向上半宽不超过 r（留 1% 余量）
                for (int i = 0; i < 3; i++) {
                    double ax = Math.Abs(i == 0 ? axis.X : i == 1 ? axis.Y : axis.Z); if (ax > 0.9) continue;
                    double c = i == 0 ? center.X : i == 1 ? center.Y : center.Z, l = i == 0 ? lo.X : i == 1 ? lo.Y : lo.Z, h = i == 0 ? hi.X : i == 1 ? hi.Y : hi.Z;
                    if (c - l > r * 1.01 + 1e-6 || h - c > r * 1.01 + 1e-6) return false;
                }
                return true;
            } catch { return false; }
        }

        // 孔特征的规格挂到几何孔上：按孔心落在孔轴线上（0.05mm）配
        static void AttachSpecs(object document, List<ModelHole> holes){
            if (holes.Count == 0) return;
            dynamic models = ((dynamic)document).Models;
            int nm = (int)models.Count;
            for (int i = 1; i <= nm; i++) {
                P.Model model = models.Item(i);
                var list = new List<P.Hole>();
                try { foreach (P.Hole h in model.Holes) list.Add(h); } catch { }
                try { foreach (P.UserDefinedPattern u in model.UserDefinedPatterns) list.AddRange(HoleCombine.Inputs(u)); } catch { }
                foreach (var h in list) {
                    HoleSpec spec; bool through;
                    try { spec = HoleEditCad.ReadSpec(h, out through); } catch { continue; }
                    int np; Array profs = new object[0];
                    try { h.GetProfiles(out np, ref profs); } catch { continue; }
                    foreach (object po in profs) {
                        var prof = po as P.Profile; if (prof == null) continue;
                        int nh = 0; try { nh = prof.Holes2d.Count; } catch { }
                        for (int k = 1; k <= nh; k++) {
                            try {
                                double x2, y2, x, y, z; prof.Holes2d.Item(k).GetCenterPoint(out x2, out y2); prof.Convert2DCoordinate(x2, y2, out x, out y, out z);
                                var c = new V3(x, y, z);
                                foreach (var mh in holes) if (mh.Spec == null && mh.OnAxis(c, 5e-5)) mh.Spec = spec;
                            } catch { }
                        }
                    }
                }
            }
        }

        static int AxisOf(Array n){
            for (int a = 0; a < 3; a++) if (Math.Abs(Convert.ToDouble(n.GetValue(a))) >= AxisCos) return a;
            return -1;
        }

        static IEnumerable<G.Body> Bodies(object document){
            var list = new List<G.Body>();
            try {
                dynamic models = ((dynamic)document).Models;
                int n = (int)models.Count;
                for (int i = 1; i <= n; i++) { try { var b = (G.Body)models.Item(i).Body; if (b != null) list.Add(b); } catch { } }
            } catch (Exception e) { Log.Write("DrawingModelRead.Bodies", e); }
            return list;
        }
    }
}
