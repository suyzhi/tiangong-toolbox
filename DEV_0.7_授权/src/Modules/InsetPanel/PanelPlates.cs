using System;
using System.Collections.Generic;
using System.Linq;

namespace TianGongCadSuite {
    // 型材自动填充：把"板子"（以前生成的填充板、手画的玻璃板）和"型材"分开，并判断一个框口里是不是已经有板子。
    //
    // 为什么要分：跨子装配填充时，最省事的选法是把几个子围栏整个选上。子围栏里除了型材，还有已经生成的
    // 4mm 填充板、整面的玻璃板。它们以前会被当成型材参与识别 —— 板子和型材在同一层，
    // 「板厚超出型材共同深度范围」，整个平面一个框口都识别不出来（2026-10-15 在 SK26.01-OK310-03-001 上实测）。
    // 现在板子单独拿出来：不参与识别，只用来把"已经装了板"的框口标出来、默认不勾。
    public static class PanelPlates {
        const double ThinMax = 0.03;        // 板厚上限 30mm
        const double WideMin = 0.15;        // 板宽下限 150mm（扁钢、窄压条不算板）
        const double ThinRatio = 0.15;      // 厚 / 宽
        const double DepthTol = 0.03;       // 板子中面离框口中面 30mm 以内才算"装在这个框口里"
        const double OverlapMin = 0.5;      // 板子盖住框口面积一半以上

        struct P2 {
            public double X, Y;
            public P2(double x, double y){ X = x; Y = y; }
        }
        // 薄板：在垂直于最长直边 axis 的二维截面上求最小外接矩形厚度与宽度（2D 凸包旋转边法向扫描）。
        // 不能按世界 X/Y/Z 轴直接投影：斜角/非正交围栏上旋转 ≥2°~45° 的竖立薄板在世界轴上跨度会暴涨，
        // 误判成直型材塞进 Members 导致整面框口识别崩溃。
        public static bool IsPlate(V3 axis, IList<V3> points){
            if (points == null || points.Count < 2) return false;
            V3 a;
            try { a = axis.Unit(); } catch (ArgumentException) { return false; }
            var world = new[]{ new V3(1, 0, 0), new V3(0, 1, 0), new V3(0, 0, 1) };
            V3 e = world.OrderBy(w => Math.Abs(w.Dot(a))).First();
            V3 b = (e - a * e.Dot(a)).Unit(), c = a.Cross(b).Unit();
            var pts = new List<P2>(points.Count);
            for (int i = 0; i < points.Count; i++) pts.Add(new P2(points[i].Dot(b), points[i].Dot(c)));
            var hull = ConvexHull2D(pts);
            if (hull.Count < 2) return false;
            double bestThin = double.PositiveInfinity, bestWide = 0;
            if (hull.Count == 2) {
                double dx = hull[1].X - hull[0].X, dy = hull[1].Y - hull[0].Y;
                bestThin = 0;
                bestWide = Math.Sqrt(dx * dx + dy * dy);
            } else {
                int m = hull.Count;
                for (int i = 0; i < m; i++) {
                    P2 p0 = hull[i], p1 = hull[(i + 1) % m];
                    double dx = p1.X - p0.X, dy = p1.Y - p0.Y;
                    double len = Math.Sqrt(dx * dx + dy * dy);
                    if (len < 1e-12) continue;
                    double ux = dx / len, uy = dy / len;
                    double vx = -uy, vy = ux;
                    double minU = double.PositiveInfinity, maxU = double.NegativeInfinity;
                    double minV = double.PositiveInfinity, maxV = double.NegativeInfinity;
                    for (int k = 0; k < m; k++) {
                        double u = hull[k].X * ux + hull[k].Y * uy;
                        double v = hull[k].X * vx + hull[k].Y * vy;
                        if (u < minU) minU = u; if (u > maxU) maxU = u;
                        if (v < minV) minV = v; if (v > maxV) maxV = v;
                    }
                    double eu = maxU - minU, ev = maxV - minV;
                    double thin = Math.Min(eu, ev), wide = Math.Max(eu, ev);
                    if (thin < bestThin - 1e-12 || (Math.Abs(thin - bestThin) <= 1e-12 && wide > bestWide)) {
                        bestThin = thin;
                        bestWide = wide;
                    }
                }
            }
            if (double.IsInfinity(bestThin)) return false;
            return bestThin <= ThinMax && bestWide >= WideMin && bestThin <= ThinRatio * bestWide;
        }
        static double Cross2(P2 o, P2 a, P2 b){ return (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X); }
        static List<P2> ConvexHull2D(List<P2> pts){
            pts.Sort(delegate(P2 p, P2 q){
                int cx = p.X.CompareTo(q.X);
                return cx != 0 ? cx : p.Y.CompareTo(q.Y);
            });
            var uniq = new List<P2>(pts.Count);
            for (int i = 0; i < pts.Count; i++) {
                if (uniq.Count == 0 || Math.Abs(pts[i].X - uniq[uniq.Count - 1].X) > 1e-12 || Math.Abs(pts[i].Y - uniq[uniq.Count - 1].Y) > 1e-12)
                    uniq.Add(pts[i]);
            }
            if (uniq.Count <= 2) return uniq;
            var lower = new List<P2>();
            for (int i = 0; i < uniq.Count; i++) {
                while (lower.Count >= 2 && Cross2(lower[lower.Count - 2], lower[lower.Count - 1], uniq[i]) <= 1e-15) lower.RemoveAt(lower.Count - 1);
                lower.Add(uniq[i]);
            }
            var upper = new List<P2>();
            for (int i = uniq.Count - 1; i >= 0; i--) {
                while (upper.Count >= 2 && Cross2(upper[upper.Count - 2], upper[upper.Count - 1], uniq[i]) <= 1e-15) upper.RemoveAt(upper.Count - 1);
                upper.Add(uniq[i]);
            }
            lower.RemoveAt(lower.Count - 1);
            upper.RemoveAt(upper.Count - 1);
            lower.AddRange(upper);
            return lower;
        }

        // 这块板是否已经装在框口里：和框口同向（板厚方向 = 框口法向）、中面靠近、盖住框口一半以上面积
        public static bool Covers(PanelSpec spec, IList<V3> plate){
            if (spec == null || plate == null || plate.Count == 0 || spec.Width <= 0 || spec.Height <= 0) return false;
            V3 o = spec.Origin;
            double n0 = plate.Min(p => (p - o).Dot(spec.N)), n1 = plate.Max(p => (p - o).Dot(spec.N));
            if (n1 - n0 > ThinMax || Math.Abs((n0 + n1) / 2) > DepthTol) return false;
            double u0 = plate.Min(p => (p - o).Dot(spec.U)), u1 = plate.Max(p => (p - o).Dot(spec.U));
            double v0 = plate.Min(p => (p - o).Dot(spec.V)), v1 = plate.Max(p => (p - o).Dot(spec.V));
            double du = Math.Min(u1, spec.Width) - Math.Max(u0, 0), dv = Math.Min(v1, spec.Height) - Math.Max(v0, 0);
            if (du <= 0 || dv <= 0) return false;
            return du * dv >= OverlapMin * spec.Width * spec.Height;
        }

        // 型材是否贴着这个框口（包围盒碰到框口外扩 Border 的矩形）—— 界面上标「跨 N 个子装配」用，只是个提示
        public static bool Borders(PanelSpec spec, IList<V3> member){
            const double Border = 0.03;
            if (spec == null || member == null || member.Count == 0) return false;
            V3 o = spec.Origin;
            double u0 = member.Min(p => (p - o).Dot(spec.U)), u1 = member.Max(p => (p - o).Dot(spec.U));
            double v0 = member.Min(p => (p - o).Dot(spec.V)), v1 = member.Max(p => (p - o).Dot(spec.V));
            return u1 > -Border && u0 < spec.Width + Border && v1 > -Border && v0 < spec.Height + Border;
        }

        static double Extent(IList<V3> points, V3 dir){ return points.Max(p => p.Dot(dir)) - points.Min(p => p.Dot(dir)); }
    }
}
