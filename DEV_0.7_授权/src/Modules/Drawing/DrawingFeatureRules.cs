using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace TianGongCadSuite {
    // 车间特色特征识别纯逻辑：
    //   1. 圆周均布孔（PCD 分度圆）：≥3 孔共圆且按等角/对称圆周分布，去除坐标串里的小数 XY，改标分度圆 ØPCD + 均布；
    //   2. 腰孔（长圆孔）：成对同半径半圆弧 + 两条平行切线，坐标串留中心点，另标槽宽×总长；
    //   3. 斜面角度：非 45° 小倒角的长斜线（或对称锥尖双斜线），配对相邻参考边标角度尺寸。
    // 不依赖 CAD / WinForms，全部由纯逻辑测试覆盖。

    public sealed class PcdPattern {
        public double Cx, Cy;          // 分度圆圆心（视图坐标，米）
        public double Radius;          // 分度圆半径（视图坐标，米）
        public double DiameterMm { get { return Math.Round(Radius * 2000.0, 2); } }
        public bool FullEqualSpacing;  // 是否整圆等角均布（如 6×60° / 8×45°）
        public double StepAngleDeg;    // 相邻孔夹角（度）
        public double StartAngleDeg;   // 起始孔相对 +X 轴夹角（度，[0, 360)）
        public bool NeedsStartAngle;   // 起始角偏离 0°/90°/半步角时需要补标定位角度
        public DwgEdge CenterCircle;   // 同心外圆 / 内孔（若有，供原生 BoltHoleCircle 绑定圆心）
        public readonly List<DwgEdge> Holes = new List<DwgEdge>();
    }

    public sealed class SlotFeature {
        public double Cx, Cy;          // 腰孔中心（视图坐标，米）
        public double WidthMm;         // 槽宽 = 2R（毫米）
        public double CenterDistMm;    // 两半圆弧圆心距（毫米）
        public double TotalLengthMm;   // 总长 = CenterDist + Width（毫米）
        public double AngleDeg;        // 槽长轴与水平夹角（[0, 180)）
        public DwgEdge Arc1, Arc2;
        public DwgEdge Line1, Line2;
    }

    public sealed class SlotGroup {
        public readonly List<SlotFeature> Slots = new List<SlotFeature>();
        public SlotFeature Leader;
        public double WidthMm, TotalLengthMm, CenterDistMm;
        public int Count { get { return Slots.Count; } }
        public string CalloutText(bool through, double depthMm){
            string pre = Count > 1 ? Count + "×" : "";
            string w = Math.Round(WidthMm, 2).ToString("0.##", CultureInfo.InvariantCulture);
            string l = Math.Round(TotalLengthMm, 2).ToString("0.##", CultureInfo.InvariantCulture);
            string tail = through ? " 贯通" : (depthMm > 0.05 ? "%DP" + Math.Round(depthMm, 2).ToString("0.##", CultureInfo.InvariantCulture) : "");
            return pre + "腰孔 " + w + "×" + l + tail;
        }
    }

    public sealed class AngleFeature {
        public DwgEdge Edge1;
        public DwgEdge Edge2;          // 相邻基准边，或对称锥面的另一条斜线
        public double AngleDeg;        // 实测角度（度）
        public bool IsIncludedCone;    // 是否两条对称斜线的夹角（如定位销锥尖 67.4°）
        public double VertexX, VertexY;// 交点（视图坐标）
    }

    public static class DrawingFeatureRules {
        const double Tol = 5e-5; // 0.05mm

        // ---- 1. 圆周均布孔（PCD）识别 ----
        // 避免误杀矩形板四角安装孔：
        //   · N >= 5 或 N == 3：只要共圆等角一律按 PCD 标；
        //   · N == 4：仅当视图里存在与 (Cx,Cy) 同心的大圆（如圆形法兰/轴承座外圆），或者 4 孔呈正交菱形（0°/90°/180°/270°）而非轴对齐矩形时才按 PCD 标。
        public static PcdPattern TryDetectPcd(HoleGroup group, IList<DwgEdge> allEdges){
            if (group == null || group.Circles == null || group.Circles.Count < 3) return null;
            var cs = group.Circles;
            int n = cs.Count;

            // 收集候选圆心：1) 同视图内半径大于孔组的大圆圆心；2) 孔组质心；3) 前 3 个不共线孔的外心
            var candidates = new List<KeyValuePair<double[], DwgEdge>>();
            if (allEdges != null) {
                var bigCircles = allEdges.Where(e => e.Kind == DwgKind.Circle && e.R > group.DiameterMm * 0.0005 + 0.002)
                    .OrderByDescending(e => e.R).ToList();
                foreach (var bc in bigCircles) {
                    if (!candidates.Any(c => Math.Abs(c.Key[0] - bc.Cx) < Tol && Math.Abs(c.Key[1] - bc.Cy) < Tol))
                        candidates.Add(new KeyValuePair<double[], DwgEdge>(new[] { bc.Cx, bc.Cy }, bc));
                }
            }
            double avgX = cs.Average(c => c.Cx), avgY = cs.Average(c => c.Cy);
            candidates.Add(new KeyValuePair<double[], DwgEdge>(new[] { avgX, avgY }, null));
            double ccx, ccy;
            if (Circumcenter(cs[0].Cx, cs[0].Cy, cs[1].Cx, cs[1].Cy, cs[2].Cx, cs[2].Cy, out ccx, out ccy)) {
                candidates.Add(new KeyValuePair<double[], DwgEdge>(new[] { ccx, ccy }, null));
            }

            foreach (var cand in candidates) {
                double cx = cand.Key[0], cy = cand.Key[1];
                double rSum = 0;
                var rs = new double[n];
                for (int i = 0; i < n; i++) {
                    rs[i] = Math.Sqrt((cs[i].Cx - cx) * (cs[i].Cx - cx) + (cs[i].Cy - cy) * (cs[i].Cy - cy));
                    rSum += rs[i];
                }
                double pcdR = rSum / n;
                if (pcdR < 0.004) continue; // 分度圆直径至少 ≥ 8mm
                bool onCircle = true;
                for (int i = 0; i < n; i++) {
                    if (Math.Abs(rs[i] - pcdR) > Math.Max(Tol, pcdR * 0.002)) { onCircle = false; break; }
                }
                if (!onCircle) continue;

                // 计算各孔极角并排序
                var angs = new List<double>();
                for (int i = 0; i < n; i++) {
                    double a = Math.Atan2(cs[i].Cy - cy, cs[i].Cx - cx) * 180.0 / Math.PI;
                    if (a < 0) a += 360.0;
                    angs.Add(a);
                }
                angs.Sort();

                double fullStep = 360.0 / n;
                bool fullEq = true;
                for (int i = 0; i < n; i++) {
                    double next = (i + 1 < n) ? angs[i + 1] : angs[0] + 360.0;
                    if (Math.Abs((next - angs[i]) - fullStep) > 0.6) { fullEq = false; break; }
                }
                if (!fullEq) continue;

                // 针对 N == 4 严格排除矩形板四角孔（四角在 45°/135°/225°/315° 且无同心圆外轮廓的板件仍用 XY 坐标更清晰）
                if (n == 4) {
                    bool axisAlignedDiamond = Math.Abs(angs[0]) < 1.0 || Math.Abs(angs[0] - 90.0) < 1.0;
                    bool hasConcentricOuter = HasConcentricCircularOutline(cx, cy, pcdR, allEdges);
                    if (!hasConcentricOuter && !axisAlignedDiamond) continue;
                }

                // 判断起始角是否落在常规正交或对称半步角上（0° / 90° / fullStep/2）
                double a0 = angs[0];
                double rem90 = a0 % 90.0;
                bool onAxis = rem90 < 0.6 || Math.Abs(rem90 - 90.0) < 0.6;
                bool onHalfStep = Math.Abs(a0 - fullStep / 2.0) < 0.6 || Math.Abs((a0 % fullStep) - fullStep / 2.0) < 0.6;

                var p = new PcdPattern {
                    Cx = cx, Cy = cy, Radius = pcdR,
                    FullEqualSpacing = true,
                    StepAngleDeg = Math.Round(fullStep, 2),
                    StartAngleDeg = Math.Round(a0, 2),
                    NeedsStartAngle = !(onAxis || onHalfStep),
                    CenterCircle = cand.Value
                };
                p.Holes.AddRange(cs);
                return p;
            }
            return null;
        }

        // 检查 (cx,cy) 处是否存在比 pcdR 更大的同心圆或同心大圆弧（圆形法兰 / 轴肩特征）
        static bool HasConcentricCircularOutline(double cx, double cy, double pcdR, IList<DwgEdge> edges){
            if (edges == null) return false;
            foreach (var e in edges) {
                if (e.Kind == DwgKind.Line) continue;
                if (Math.Abs(e.Cx - cx) > Tol * 2 || Math.Abs(e.Cy - cy) > Tol * 2) continue;
                if (e.Kind == DwgKind.Circle && e.R > pcdR + 0.001) return true;
                if (e.Kind == DwgKind.Arc && Math.Abs(e.Sweep) >= Math.PI - 0.1 && e.R > pcdR + 0.001) return true;
            }
            return false;
        }

        static bool Circumcenter(double x1, double y1, double x2, double y2, double x3, double y3, out double cx, out double cy){
            cx = 0; cy = 0;
            double d = 2.0 * (x1 * (y2 - y3) + x2 * (y3 - y1) + x3 * (y1 - y2));
            if (Math.Abs(d) < 1e-12) return false;
            double u1 = x1 * x1 + y1 * y1, u2 = x2 * x2 + y2 * y2, u3 = x3 * x3 + y3 * y3;
            cx = (u1 * (y2 - y3) + u2 * (y3 - y1) + u3 * (y1 - y2)) / d;
            cy = (u1 * (x3 - x2) + u2 * (x1 - x3) + u3 * (x2 - x1)) / d;
            return true;
        }

        // ---- 2. 腰孔（长圆孔）识别与分组 ----
        public static List<SlotGroup> FindSlots(IList<DwgEdge> edges){
            var result = new List<SlotGroup>();
            if (edges == null || edges.Count == 0) return result;
            var semiArcs = edges.Where(e => e.Kind == DwgKind.Arc
                && Math.Abs(Math.Abs(e.Sweep) - Math.PI) < 0.08
                && e.R >= 0.0008 && e.R <= 0.060).ToList();
            if (semiArcs.Count < 2) return result;

            var lines = edges.Where(e => e.Kind == DwgKind.Line).ToList();
            var used = new bool[semiArcs.Count];
            var rawSlots = new List<SlotFeature>();

            for (int i = 0; i < semiArcs.Count; i++) {
                if (used[i]) continue;
                var a = semiArcs[i];
                for (int j = i + 1; j < semiArcs.Count; j++) {
                    if (used[j]) continue;
                    var b = semiArcs[j];
                    if (Math.Abs(a.R - b.R) > Tol) continue;
                    double dx = b.Cx - a.Cx, dy = b.Cy - a.Cy;
                    double dist = Math.Sqrt(dx * dx + dy * dy);
                    if (dist < 0.0005 || dist > 0.300) continue;
                    double ux = dx / dist, uy = dy / dist;
                    double nx = -uy, ny = ux; // 垂直于槽轴方向
                    double midX = (a.Cx + b.Cx) / 2.0, midY = (a.Cy + b.Cy) / 2.0;

                    // 查找两条平行切线（距离槽轴各为 ±R，长度接近 dist）
                    DwgEdge lPlus = null, lMinus = null;
                    foreach (var l in lines) {
                        if (Math.Abs(l.Length - dist) > Math.Max(Tol * 4, dist * 0.03)) continue;
                        double ldx = l.X2 - l.X1, ldy = l.Y2 - l.Y1;
                        double ll = Math.Sqrt(ldx * ldx + ldy * ldy);
                        if (ll < 1e-7) continue;
                        double dot = Math.Abs((ldx / ll) * ux + (ldy / ll) * uy);
                        if (dot < 0.999) continue;
                        double lmx = (l.X1 + l.X2) / 2.0 - midX, lmy = (l.Y1 + l.Y2) / 2.0 - midY;
                        double along = Math.Abs(lmx * ux + lmy * uy);
                        double perp = lmx * nx + lmy * ny;
                        if (along > Tol * 4) continue;
                        if (Math.Abs(perp - a.R) <= Tol * 3) lPlus = l;
                        else if (Math.Abs(perp + a.R) <= Tol * 3) lMinus = l;
                    }
                    if (lPlus == null || lMinus == null) continue;
                    // 验证两端半圆弧的端点是否确实开口朝向对方（防止把两个背靠背 U 槽误合成腰孔）
                    var ma = DrawingPlan.ArcMidpoint(a);
                    var mb = DrawingPlan.ArcMidpoint(b);
                    double da = (ma[0] - a.Cx) * ux + (ma[1] - a.Cy) * uy;
                    double db = (mb[0] - b.Cx) * ux + (mb[1] - b.Cy) * uy;
                    if (da > 0 || db < 0) continue; // a 的弧顶应朝 -u，b 的弧顶应朝 +u

                    used[i] = true; used[j] = true;
                    double ang = Math.Atan2(uy, ux) * 180.0 / Math.PI;
                    if (ang < 0) ang += 180.0;
                    if (ang >= 180.0) ang -= 180.0;
                    rawSlots.Add(new SlotFeature {
                        Cx = midX, Cy = midY,
                        WidthMm = Math.Round(a.R * 2000.0, 2),
                        CenterDistMm = Math.Round(dist * 1000.0, 2),
                        TotalLengthMm = Math.Round((dist + 2.0 * a.R) * 1000.0, 2),
                        AngleDeg = Math.Round(ang, 1),
                        Arc1 = a, Arc2 = b, Line1 = lPlus, Line2 = lMinus
                    });
                    break;
                }
            }

            // 按 (WidthMm, TotalLengthMm) 归组
            foreach (var s in rawSlots) {
                var grp = result.FirstOrDefault(g =>
                    Math.Abs(g.WidthMm - s.WidthMm) < 0.05 && Math.Abs(g.TotalLengthMm - s.TotalLengthMm) < 0.05);
                if (grp == null) {
                    grp = new SlotGroup { WidthMm = s.WidthMm, TotalLengthMm = s.TotalLengthMm, CenterDistMm = s.CenterDistMm, Leader = s };
                    result.Add(grp);
                }
                grp.Slots.Add(s);
            }
            foreach (var g in result) {
                g.Leader = g.Slots.OrderByDescending(s => s.Cx + s.Cy).First();
            }
            return result;
        }

        // ---- 3. 斜面 / 锥面角度识别 ----
        // 找出视图里非水平、非竖直、且不是 ≤6mm 45° 倒角的长斜线（长度 ≥ 4mm）：
        //   · 若存在两条长度近似相等、关于水平或竖直轴镜像对称且相交/延长相交的斜线（典型为定位销锥尖 67.4°、V 型块 90°/120°），
        //     合为一条对称夹角；
        //   · 否则配对共享端点的相邻水平/竖直边，标出斜面与基准边的锐角夹角（避开 45° 小倒角）。
        public static List<AngleFeature> FindAngles(IList<DwgEdge> edges){
            var result = new List<AngleFeature>();
            if (edges == null || edges.Count < 2) return result;
            var slants = new List<DwgEdge>();
            foreach (var e in edges) {
                if (e.Kind != DwgKind.Line || e.IsHorizontal || e.IsVertical) continue;
                double adx = Math.Abs(e.X2 - e.X1), ady = Math.Abs(e.Y2 - e.Y1);
                // 排除 C0.1 ~ C6 的 45° 倒角（已由倒角标注处理）
                if (Math.Abs(adx - ady) < 1e-5 && adx <= 0.006 + 1e-9) continue;
                if (e.Length < 0.004) continue;
                // 去重重合投影斜线
                double loX = Math.Min(e.X1, e.X2), hiX = Math.Max(e.X1, e.X2);
                double loY = Math.Min(e.Y1, e.Y2), hiY = Math.Max(e.Y1, e.Y2);
                if (slants.Any(o => Math.Abs(Math.Min(o.X1, o.X2) - loX) < Tol && Math.Abs(Math.Max(o.X1, o.X2) - hiX) < Tol
                    && Math.Abs(Math.Min(o.Y1, o.Y2) - loY) < Tol && Math.Abs(Math.Max(o.Y1, o.Y2) - hiY) < Tol)) continue;
                slants.Add(e);
            }
            if (slants.Count == 0) return result;

            var used = new bool[slants.Count];
            // 优先匹配对称双斜线（如锥尖夹角 67.4°、V 槽 90°）
            for (int i = 0; i < slants.Count; i++) {
                if (used[i]) continue;
                var a = slants[i];
                for (int j = i + 1; j < slants.Count; j++) {
                    if (used[j]) continue;
                    var b = slants[j];
                    if (Math.Abs(a.Length - b.Length) > Math.Max(0.0005, a.Length * 0.05)) continue;
                    double angA = LineSlopeDeg(a), angB = LineSlopeDeg(b);
                    // 关于水平轴或竖直轴镜像对称：angA + angB ≈ 180°
                    if (Math.Abs((angA + angB) - 180.0) > 1.0) continue;
                    double ix, iy;
                    if (!IntersectLines(a, b, out ix, out iy)) continue;
                    // 交点应落在两线段附近（允许锥尖有小圆角/平头截断，距离不超过两倍线长）
                    double da = DistToSegment(ix, iy, a), db = DistToSegment(ix, iy, b);
                    if (da > a.Length * 1.5 || db > b.Length * 1.5) continue;
                    double included = AngleBetweenDirs(a, b, ix, iy);
                    if (included < 10.0 || included > 165.0) continue;
                    used[i] = true; used[j] = true;
                    result.Add(new AngleFeature { Edge1 = a, Edge2 = b, AngleDeg = Math.Round(included, 1), IsIncludedCone = true, VertexX = ix, VertexY = iy });
                    break;
                }
            }

            // 剩余单斜线：找共享端点的最长相邻正交边标夹角（相同角度+平行斜线只标代表一条）
            var ortho = edges.Where(e => e.Kind == DwgKind.Line && (e.IsHorizontal || e.IsVertical) && e.Length >= 0.003).ToList();
            var seenAngles = new List<double>();
            for (int i = 0; i < slants.Count; i++) {
                if (used[i]) continue;
                var s = slants[i];
                DwgEdge bestRef = null;
                double vx = 0, vy = 0;
                foreach (var o in ortho) {
                    double tx, ty;
                    if (!SharedEndpoint(s, o, out tx, out ty)) continue;
                    if (bestRef == null || o.Length > bestRef.Length) { bestRef = o; vx = tx; vy = ty; }
                }
                if (bestRef == null) continue;
                double deg = AngleBetweenDirs(s, bestRef, vx, vy);
                if (deg < 5.0 || deg > 175.0 || Math.Abs(deg - 90.0) < 0.5) continue;
                double acute = deg > 90.0 ? 180.0 - deg : deg;
                double rounded = Math.Round(acute, 1);
                if (seenAngles.Any(x => Math.Abs(x - rounded) < 0.2)) continue;
                seenAngles.Add(rounded);
                result.Add(new AngleFeature { Edge1 = s, Edge2 = bestRef, AngleDeg = rounded, IsIncludedCone = false, VertexX = vx, VertexY = vy });
                if (result.Count >= 4) break; // 一张视图最多标 4 处角度防挤乱
            }
            return result;
        }

        static double LineSlopeDeg(DwgEdge l){
            double a = Math.Atan2(l.Y2 - l.Y1, l.X2 - l.X1) * 180.0 / Math.PI;
            while (a < 0) a += 180.0;
            while (a >= 180.0) a -= 180.0;
            return a;
        }

        static bool SharedEndpoint(DwgEdge a, DwgEdge b, out double x, out double y){
            double[,] pa = { { a.X1, a.Y1 }, { a.X2, a.Y2 } };
            double[,] pb = { { b.X1, b.Y1 }, { b.X2, b.Y2 } };
            for (int i = 0; i < 2; i++) {
                for (int j = 0; j < 2; j++) {
                    if (Math.Abs(pa[i, 0] - pb[j, 0]) <= Tol && Math.Abs(pa[i, 1] - pb[j, 1]) <= Tol) {
                        x = pa[i, 0]; y = pa[i, 1]; return true;
                    }
                }
            }
            x = 0; y = 0; return false;
        }

        static bool IntersectLines(DwgEdge a, DwgEdge b, out double ix, out double iy){
            ix = 0; iy = 0;
            double x1 = a.X1, y1 = a.Y1, x2 = a.X2, y2 = a.Y2;
            double x3 = b.X1, y3 = b.Y1, x4 = b.X2, y4 = b.Y2;
            double den = (x1 - x2) * (y3 - y4) - (y1 - y2) * (x3 - x4);
            if (Math.Abs(den) < 1e-12) return false;
            double n1 = x1 * y2 - y1 * x2, n2 = x3 * y4 - y3 * x4;
            ix = (n1 * (x3 - x4) - (x1 - x2) * n2) / den;
            iy = (n1 * (y3 - y4) - (y1 - y2) * n2) / den;
            return true;
        }

        static double DistToSegment(double px, double py, DwgEdge e){
            double dx = e.X2 - e.X1, dy = e.Y2 - e.Y1;
            double l2 = dx * dx + dy * dy;
            if (l2 < 1e-12) return Math.Sqrt((px - e.X1) * (px - e.X1) + (py - e.Y1) * (py - e.Y1));
            double t = Math.Max(0, Math.Min(1, ((px - e.X1) * dx + (py - e.Y1) * dy) / l2));
            double qx = e.X1 + t * dx, qy = e.Y1 + t * dy;
            return Math.Sqrt((px - qx) * (px - qx) + (py - qy) * (py - qy));
        }

        static double AngleBetweenDirs(DwgEdge a, DwgEdge b, double vx, double vy){
            // 从交点 (vx, vy) 指向各自远离交点的一端
            double dax = ((a.X1 - vx) * (a.X1 - vx) + (a.Y1 - vy) * (a.Y1 - vy)) >= ((a.X2 - vx) * (a.X2 - vx) + (a.Y2 - vy) * (a.Y2 - vy))
                ? (a.X1 - vx) : (a.X2 - vx);
            double day = ((a.X1 - vx) * (a.X1 - vx) + (a.Y1 - vy) * (a.Y1 - vy)) >= ((a.X2 - vx) * (a.X2 - vx) + (a.Y2 - vy) * (a.Y2 - vy))
                ? (a.Y1 - vy) : (a.Y2 - vy);
            double dbx = ((b.X1 - vx) * (b.X1 - vx) + (b.Y1 - vy) * (b.Y1 - vy)) >= ((b.X2 - vx) * (b.X2 - vx) + (b.Y2 - vy) * (b.Y2 - vy))
                ? (b.X1 - vx) : (b.X2 - vx);
            double dby = ((b.X1 - vx) * (b.X1 - vx) + (b.Y1 - vy) * (b.Y1 - vy)) >= ((b.X2 - vx) * (b.X2 - vx) + (b.Y2 - vy) * (b.Y2 - vy))
                ? (b.Y1 - vy) : (b.Y2 - vy);
            double la = Math.Sqrt(dax * dax + day * day), lb = Math.Sqrt(dbx * dbx + dby * dby);
            if (la < 1e-9 || lb < 1e-9) return 0;
            double cos = Math.Max(-1.0, Math.Min(1.0, (dax * dbx + day * dby) / (la * lb)));
            return Math.Acos(cos) * 180.0 / Math.PI;
        }
    }
}
