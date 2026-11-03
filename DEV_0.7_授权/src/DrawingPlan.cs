using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace TianGongCadSuite {
    // 一键出图（0.11.0，命令 13）的纯逻辑：视图里的二维图元 → 要标哪些尺寸、文字摆在哪。不碰 CAD，tests/pure 能跑。
    //
    // 公司图纸的规矩（2026-11-03 普查 734 张人画的图：坐标尺寸 1179 个、线性 500 个、直径 160、半径 133、倒角 68）：
    //   * 零件图以「坐标尺寸」为主：一个基准边记 0，其它每条边 / 每个孔心标到基准的距离（看 YF25.07G01-L1-M30-02-015、SK26.01-OK310-02-004）；
    //   * 孔：同规格的孔只标一个，前缀「4×」，后面写贯通 / 深度（天工孔标注自己生成，见 DrawingDims）；
    //   * 圆角：同半径的只标一个「4-R5」；
    //   * 坐标尺寸挤在一起时文字错开、拉折线（人画的 91 / 94 就是这样）。
    // 坐标单位：视图坐标（米，未乘比例）。纸面距离 = 视图距离 × 比例。

    public enum DwgKind { Line, Circle, Arc }

    // 视图里的一个二维图元（读自 DVLines2d / DVCircles2d / DVArcs2d）。
    public sealed class DwgEdge {
        public DwgKind Kind;
        public double X1, Y1, X2, Y2;      // 直线端点 / 圆弧起止点
        public double Cx, Cy, R;           // 圆、圆弧
        public double Sweep;               // 圆弧扫角（弧度），整圆 2π
        public object Tag;                 // 原始 CAD 对象（DVLine2d 等）
        public bool IsVertical { get { return Kind == DwgKind.Line && Math.Abs(X1 - X2) < DrawingPlan.Eps && Math.Abs(Y1 - Y2) > DrawingPlan.Eps; } }
        public bool IsHorizontal { get { return Kind == DwgKind.Line && Math.Abs(Y1 - Y2) < DrawingPlan.Eps && Math.Abs(X1 - X2) > DrawingPlan.Eps; } }
        public double Length { get { return Kind == DwgKind.Line ? Math.Sqrt((X2 - X1) * (X2 - X1) + (Y2 - Y1) * (Y2 - Y1)) : R * Math.Abs(Sweep); } }
        public static DwgEdge Line(double x1, double y1, double x2, double y2){ return new DwgEdge { Kind = DwgKind.Line, X1 = x1, Y1 = y1, X2 = x2, Y2 = y2 }; }
        public static DwgEdge Circle(double cx, double cy, double r){ return new DwgEdge { Kind = DwgKind.Circle, Cx = cx, Cy = cy, R = r, Sweep = 2 * Math.PI, X1 = cx + r, Y1 = cy, X2 = cx + r, Y2 = cy }; }
        public static DwgEdge Arc(double cx, double cy, double r, double sweep, double x1, double y1, double x2, double y2){ return new DwgEdge { Kind = DwgKind.Arc, Cx = cx, Cy = cy, R = r, Sweep = sweep, X1 = x1, Y1 = y1, X2 = x2, Y2 = y2 }; }
        public override string ToString(){
            Func<double, string> f = v => (v * 1000).ToString("0.##", CultureInfo.InvariantCulture);
            if (Kind == DwgKind.Line) return "线(" + f(X1) + "," + f(Y1) + ")-(" + f(X2) + "," + f(Y2) + ")";
            return (Kind == DwgKind.Circle ? "圆" : "弧") + "(" + f(Cx) + "," + f(Cy) + ")R" + f(R);
        }
    }

    // 坐标尺寸的一个落点：挂在哪个图元上、点在哪（视图坐标）、量的是哪个坐标值。
    public sealed class DwgTarget {
        public DwgEdge Edge;
        public bool KeyPoint;          // true = 关键点（圆心 / 圆弧象限点），false = 线上一点
        public bool Quadrant;          // 关键点是圆弧最外的象限点，不是圆心
        public double X, Y;            // 选取点
        public double Coord;           // 沿尺寸方向的坐标
        public double TextAt;          // 文字摆放位置（沿尺寸方向，排开后的）
        public override string ToString(){ return (Coord * 1000).ToString("0.###", CultureInfo.InvariantCulture) + "@" + Edge; }
    }

    // 一串坐标尺寸：Horizontal = 量 x（文字竖排，摆在视图上方或下方）；否则量 y（摆在左边或右边）。
    public sealed class OrdinateRun {
        public bool Horizontal;
        public DwgTarget Origin;
        public readonly List<DwgTarget> Targets = new List<DwgTarget>();   // 不含原点
        public double Line;              // 文字所在的那条线（Horizontal 时是 y，否则是 x），视图坐标
        public bool LowSide;             // true = 下方 / 左边
        public int Count { get { return Targets.Count + 1; } }
    }

    // 同规格孔的标注：挂在 Leader 那个圆上，Count 个。
    public sealed class HoleGroup {
        public readonly List<DwgEdge> Circles = new List<DwgEdge>();
        public DwgEdge Leader;
        public double DiameterMm;
        public int Count { get { return Circles.Count; } }
    }

    public sealed class RadiusGroup {
        public readonly List<DwgEdge> Arcs = new List<DwgEdge>();
        public DwgEdge Leader;
        public double RadiusMm;
        public int Count { get { return Arcs.Count; } }
    }

    public static class DrawingPlan {
        public const double Eps = 1e-7;
        // 两个坐标差小于它算同一个（米）。模型里 0.01mm 以内的错位不值得单独标一个尺寸
        public const double SameCoord = 1e-5;
        // 纸面上坐标尺寸文字之间至少隔多远（米）：公司模板尺寸字高 3.5，再留 1mm
        public const double TextPitch = 0.0045;

        // ---- 坐标值去重 ----
        public static List<double> Distinct(IEnumerable<double> values, double tol){
            var r = new List<double>();
            foreach (double v in values.OrderBy(x => x)) if (r.Count == 0 || v - r[r.Count - 1] > tol) r.Add(v);
            return r;
        }

        // 视图里所有图元的外框（视图坐标）。圆 / 弧按整圆算（保守）
        public static double[] Box(IEnumerable<DwgEdge> edges){
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
            foreach (var e in edges) {
                if (e.Kind == DwgKind.Line) { x0 = Math.Min(x0, Math.Min(e.X1, e.X2)); x1 = Math.Max(x1, Math.Max(e.X1, e.X2)); y0 = Math.Min(y0, Math.Min(e.Y1, e.Y2)); y1 = Math.Max(y1, Math.Max(e.Y1, e.Y2)); }
                else if (e.Kind == DwgKind.Circle) { x0 = Math.Min(x0, e.Cx - e.R); x1 = Math.Max(x1, e.Cx + e.R); y0 = Math.Min(y0, e.Cy - e.R); y1 = Math.Max(y1, e.Cy + e.R); }
                else { foreach (var p in ArcExtremes(e)) { x0 = Math.Min(x0, p[0]); x1 = Math.Max(x1, p[0]); y0 = Math.Min(y0, p[1]); y1 = Math.Max(y1, p[1]); } }
            }
            if (x0 > x1) return new double[] { 0, 0, 0, 0 };
            return new[] { x0, y0, x1, y1 };
        }
        // 圆弧上会撑大外框的点：两个端点 + 落在扫过范围里的四个象限点
        static IEnumerable<double[]> ArcExtremes(DwgEdge a){
            yield return new[] { a.X1, a.Y1 }; yield return new[] { a.X2, a.Y2 };
            double s = Math.Atan2(a.Y1 - a.Cy, a.X1 - a.Cx), sweep = a.Sweep;
            for (int q = 0; q < 4; q++) {
                double ang = q * Math.PI / 2, d = ang - s;
                if (sweep >= 0) { while (d < 0) d += 2 * Math.PI; if (d <= sweep + 1e-9) yield return new[] { a.Cx + a.R * Math.Cos(ang), a.Cy + a.R * Math.Sin(ang) }; }
                else { while (d > 0) d -= 2 * Math.PI; if (d >= sweep - 1e-9) yield return new[] { a.Cx + a.R * Math.Cos(ang), a.Cy + a.R * Math.Sin(ang) }; }
            }
        }

        // ---- 坐标尺寸要标的落点 ----
        // horizontal=true：量 x —— 竖线、圆心、整半圆以上的弧心（腰孔两头）都是落点；
        // 同一个 x 有好几个图元时挑一个：圆心优先（孔位是加工最要的），其次最长的线。
        // 圆角（扫角不到半圆的弧）不当落点：它的位置由两条直边定，人画的图也不标圆角心。
        public static List<DwgTarget> Targets(IList<DwgEdge> edges, bool horizontal){
            var raw = new List<DwgTarget>();
            foreach (var e in edges) {
                if (e.Kind == DwgKind.Line) {
                    if (horizontal ? e.IsVertical : e.IsHorizontal) raw.Add(new DwgTarget { Edge = e, KeyPoint = false, X = (e.X1 + e.X2) / 2, Y = (e.Y1 + e.Y2) / 2, Coord = horizontal ? e.X1 : e.Y1 });
                } else if (e.Kind == DwgKind.Circle || Math.Abs(e.Sweep) >= Math.PI - 1e-6) {
                    raw.Add(new DwgTarget { Edge = e, KeyPoint = true, X = e.Cx, Y = e.Cy, Coord = horizontal ? e.Cx : e.Cy });
                }
            }
            // 外形的最外端是圆弧（销子的球头、圆头板）时那里没有直线，总长就标不出来：补一个落在圆弧最外点上的落点
            var box = Box(edges);
            double lo = horizontal ? box[0] : box[1], hi = horizontal ? box[2] : box[3];
            foreach (double ext in new[] { lo, hi }) {
                if (raw.Any(t => Math.Abs(t.Coord - ext) <= SameCoord)) continue;
                foreach (var e in edges) {
                    if (e.Kind == DwgKind.Line) continue;
                    double c = horizontal ? e.Cx : e.Cy, sgn = ext > c ? 1 : -1;
                    if (Math.Abs(c + sgn * e.R - ext) > SameCoord) continue;
                    double px = horizontal ? ext : e.Cx, py = horizontal ? e.Cy : ext;
                    if (e.Kind == DwgKind.Arc && !OnArc(e, px, py)) continue;
                    // 关键点 = 圆弧的象限点（天工圆弧有上下左右四个轮廓关键点）；按"线上一点"挂会建不出来（e2e-8 的 029 失败 4 个）
                    raw.Add(new DwgTarget { Edge = e, KeyPoint = true, Quadrant = true, X = px, Y = py, Coord = ext });
                    break;
                }
            }
            var result = new List<DwgTarget>();
            foreach (var g in Cluster(raw, t => t.Coord, SameCoord)) {
                var best = g.OrderByDescending(t => t.KeyPoint ? 1 : 0).ThenByDescending(t => t.KeyPoint ? t.Edge.R : t.Edge.Length).First();
                result.Add(best);
            }
            return result;
        }

        // 外形边线里侧 tol 以内的平行线（倒角、倒圆的分界线）不当落点：它们由倒角标注 / 技术要求「未注倒角」表达，
        // 标成坐标尺寸只会在 0 和 50 旁边多出 1、49 这种挤成一团的数。外形边本身（box 极值）保留。
        public static List<DwgTarget> DropNearOutline(List<DwgTarget> targets, double lo, double hi, double tol){
            if (tol <= 0) return targets;
            return targets.Where(t => !t.KeyPoint ? !((t.Coord - lo > SameCoord && t.Coord - lo <= tol + SameCoord) || (hi - t.Coord > SameCoord && hi - t.Coord <= tol + SameCoord)) : true).ToList();
        }

        // 点 (x,y)（已在圆上）是不是落在这段弧扫过的范围里
        static bool OnArc(DwgEdge a, double x, double y){
            double s0 = Math.Atan2(a.Y1 - a.Cy, a.X1 - a.Cx), t = Math.Atan2(y - a.Cy, x - a.Cx), d = t - s0;
            if (a.Sweep >= 0) { while (d < -1e-9) d += 2 * Math.PI; return d <= a.Sweep + 1e-6; }
            while (d > 1e-9) d -= 2 * Math.PI; return d >= a.Sweep - 1e-6;
        }

        // 一串超过 MaxRun 个落点（刻度尺的刻线、刻字、花纹）：标出来没人看得清，天工建几百个尺寸也要好几分钟。
        // 只留孔心和够长的边（≥ 视图另一方向 15%），还多就只留外形两头和孔心
        public const int MaxRun = 40;
        public static List<DwgTarget> Significant(List<DwgTarget> ts, double span){
            if (ts.Count <= MaxRun) return ts;
            double lo = ts.Min(t => t.Coord), hi = ts.Max(t => t.Coord);
            var keep = ts.Where(t => (t.KeyPoint && !t.Quadrant) || t.Edge.Length >= span * 0.15 || Math.Abs(t.Coord - lo) < SameCoord || Math.Abs(t.Coord - hi) < SameCoord).ToList();
            if (keep.Count > MaxRun) keep = ts.Where(t => (t.KeyPoint && !t.Quadrant && t.Edge.Kind == DwgKind.Circle) || Math.Abs(t.Coord - lo) < SameCoord || Math.Abs(t.Coord - hi) < SameCoord).ToList();
            if (keep.Count > MaxRun * 2) keep = ts.Where(t => Math.Abs(t.Coord - lo) < SameCoord || Math.Abs(t.Coord - hi) < SameCoord).ToList();
            return keep;
        }

        static List<List<T>> Cluster<T>(IEnumerable<T> items, Func<T, double> key, double tol){
            var r = new List<List<T>>();
            foreach (var t in items.OrderBy(key)) {
                if (r.Count > 0 && key(t) - key(r[r.Count - 1][0]) <= tol) r[r.Count - 1].Add(t);
                else r.Add(new List<T> { t });
            }
            return r;
        }

        // ---- 基准 ----
        // 默认最小坐标那条边当 0（人画的图大多从左 / 下边起标）；
        // 坐标串关于中间对称、而且正中间有个孔心时，用中间当 0（YF25.07-L1-M70-02-310 的 110-0-110）。
        public static DwgTarget PickOrigin(List<DwgTarget> targets, out bool symmetric){
            symmetric = false;
            if (targets.Count == 0) return null;
            var sorted = targets.OrderBy(t => t.Coord).ToList();
            double lo = sorted[0].Coord, hi = sorted[sorted.Count - 1].Coord, mid = (lo + hi) / 2;
            var center = sorted.FirstOrDefault(t => t.KeyPoint && Math.Abs(t.Coord - mid) <= SameCoord);
            if (center != null && sorted.Count >= 3) {
                bool sym = sorted.All(t => sorted.Any(u => Math.Abs((u.Coord - mid) + (t.Coord - mid)) <= SameCoord * 2));
                if (sym) { symmetric = true; return center; }
            }
            // 最小坐标处挑线（线做基准比圆心直观）
            var atLo = targets.Where(t => t.Coord - lo <= SameCoord).OrderBy(t => t.KeyPoint ? 1 : 0).First();
            return atLo;
        }

        // ---- 文字排开（一维标签布局） ----
        // 每个落点想把文字放在自己的坐标上；相邻文字至少隔 pitch。挤在一起的一簇整体以它们的平均位置为中心摊开，
        // 摊开后又碰到旁边一簇就合并重摊，直到互不重叠。文字离开自己落点的部分由尺寸的折线（jog）连回去。
        public static void Spread(List<DwgTarget> targets, double pitch){
            var order = targets.OrderBy(t => t.Coord).ToList();
            var groups = new List<List<DwgTarget>>();
            foreach (var t in order) groups.Add(new List<DwgTarget> { t });
            var starts = groups.Select(g => g[0].Coord).ToList();
            bool changed = true;
            while (changed) {
                changed = false;
                for (int i = 0; i + 1 < groups.Count; i++) {
                    double endI = starts[i] + (groups[i].Count - 1) * pitch;
                    if (starts[i + 1] - endI < pitch - 1e-12) {
                        groups[i].AddRange(groups[i + 1]); groups.RemoveAt(i + 1); starts.RemoveAt(i + 1);
                        double mean = groups[i].Average(t => t.Coord);
                        starts[i] = mean - (groups[i].Count - 1) * pitch / 2;
                        changed = true; break;
                    }
                }
            }
            for (int i = 0; i < groups.Count; i++) for (int k = 0; k < groups[i].Count; k++) groups[i][k].TextAt = starts[i] + k * pitch;
        }

        // 一整串：落点 → 基准 → 排开。scale = 视图比例（纸面 / 模型）。box = 视图外框（视图坐标），offset = 文字线离外框多远（纸面米）
        public static OrdinateRun Ordinate(IList<DwgEdge> edges, bool horizontal, double scale, double[] box, bool lowSide, double offsetPaper){
            return Ordinate(edges, horizontal, scale, box, lowSide, offsetPaper, 0);
        }
        public static OrdinateRun Ordinate(IList<DwgEdge> edges, bool horizontal, double scale, double[] box, bool lowSide, double offsetPaper, double chamferTol){
            var ts = Targets(edges, horizontal);
            ts = DropNearOutline(ts, horizontal ? box[0] : box[1], horizontal ? box[2] : box[3], chamferTol);
            ts = Significant(ts, horizontal ? box[3] - box[1] : box[2] - box[0]);
            if (ts.Count < 2) return null;
            bool sym;
            var origin = PickOrigin(ts, out sym);
            var run = new OrdinateRun { Horizontal = horizontal, Origin = origin, LowSide = lowSide };
            foreach (var t in ts) if (!ReferenceEquals(t, origin)) run.Targets.Add(t);
            var all = new List<DwgTarget>(run.Targets); all.Add(origin);
            Spread(all, TextPitch / Math.Max(scale, 1e-9));
            double off = offsetPaper / Math.Max(scale, 1e-9);
            run.Line = horizontal ? (lowSide ? box[1] - off : box[3] + off) : (lowSide ? box[0] - off : box[2] + off);
            return run;
        }

        // ---- 孔、圆角分组 ----
        // 同直径（差 ≤ 0.01mm）的整圆归一组；同心圆（沉孔的孔口 / 螺纹的大小径）只留里面那个，外圈另成一组会把沉孔误标成两种孔 ——
        // 沉孔、螺纹的完整写法交给天工的孔标注（读孔特征），这里只负责"哪些圆是一组、挂在哪个上"。
        public static List<HoleGroup> Holes(IList<DwgEdge> edges, double maxDiameterM){
            var circles = edges.Where(e => e.Kind == DwgKind.Circle && 2 * e.R <= maxDiameterM).ToList();
            var inner = new List<DwgEdge>();
            foreach (var c in circles) {
                bool outerOfSomething = circles.Any(o => !ReferenceEquals(o, c) && o.R < c.R - SameCoord && Math.Abs(o.Cx - c.Cx) < SameCoord && Math.Abs(o.Cy - c.Cy) < SameCoord);
                if (!outerOfSomething) inner.Add(c);
            }
            var groups = new List<HoleGroup>();
            foreach (var g in Cluster(inner, c => c.R, SameCoord)) {
                var hg = new HoleGroup { DiameterMm = Math.Round(g[0].R * 2000, 3) };
                // 同一个圆心只算一个（隐藏线 / 两个实体重叠时会读到重复的圆）
                foreach (var c in g) if (!hg.Circles.Any(o => Math.Abs(o.Cx - c.Cx) < SameCoord && Math.Abs(o.Cy - c.Cy) < SameCoord)) hg.Circles.Add(c);
                // 引线挂在最靠右上的那个孔：标注文字往右上方引出，不压视图主体
                hg.Leader = hg.Circles.OrderByDescending(c => c.Cx + c.Cy).First();
                groups.Add(hg);
            }
            return groups;
        }

        // 同心圆里外圈（台阶孔的大径、法兰外圆 Ø64）：Holes 只留了最里面那圈，这里把外圈按半径分组拿出来，调用方再去掉沉孔那圈
        public static List<HoleGroup> OuterRings(IList<DwgEdge> edges, double maxDiameterM){
            var circles = edges.Where(e => e.Kind == DwgKind.Circle && 2 * e.R <= maxDiameterM).ToList();
            var outer = circles.Where(c => circles.Any(o => !ReferenceEquals(o, c) && o.R < c.R - SameCoord && Math.Abs(o.Cx - c.Cx) < SameCoord && Math.Abs(o.Cy - c.Cy) < SameCoord)).ToList();
            var groups = new List<HoleGroup>();
            foreach (var g in Cluster(outer, c => c.R, SameCoord)) {
                var hg = new HoleGroup { DiameterMm = Math.Round(g[0].R * 2000, 3) };
                foreach (var c in g) if (!hg.Circles.Any(o => Math.Abs(o.Cx - c.Cx) < SameCoord && Math.Abs(o.Cy - c.Cy) < SameCoord)) hg.Circles.Add(c);
                hg.Leader = hg.Circles.OrderByDescending(c => c.Cx + c.Cy).First();
                groups.Add(hg);
            }
            return groups;
        }

        // 圆角：扫角小于半圆、半径一样的弧一组（大于等于半圆的是腰孔头 / 半圆缺口，按孔的办法标宽度）。
        public static List<RadiusGroup> Radii(IList<DwgEdge> edges){
            // 半圆以上的弧：成对的是腰孔两头（宽度由两条直边的坐标表达，不标 R）；单独一个的是圆头（销子的 R6 球头）要标
            var big = edges.Where(e => e.Kind == DwgKind.Arc && Math.Abs(e.Sweep) >= Math.PI - 1e-6 && e.R > 1e-5).ToList();
            var lone = big.Where(a => !big.Any(b => !ReferenceEquals(a, b) && Math.Abs(a.R - b.R) < SameCoord && (Math.Abs(a.Cx - b.Cx) > SameCoord || Math.Abs(a.Cy - b.Cy) > SameCoord))).ToList();
            var arcs = edges.Where(e => e.Kind == DwgKind.Arc && Math.Abs(e.Sweep) < Math.PI - 1e-6 && e.R > 1e-5).Concat(lone).ToList();
            var r = new List<RadiusGroup>();
            foreach (var g in Cluster(arcs, a => a.R, SameCoord)) {
                var rg = new RadiusGroup { RadiusMm = Math.Round(g[0].R * 1000, 3) };
                foreach (var a in g) if (!rg.Arcs.Any(o => Math.Abs(o.Cx - a.Cx) < SameCoord && Math.Abs(o.Cy - a.Cy) < SameCoord)) rg.Arcs.Add(a);
                rg.Leader = rg.Arcs.OrderByDescending(a => a.Cx + a.Cy).First();
                r.Add(rg);
            }
            return r;
        }

        // 倒角：视图里 45° 的短斜线（两条直角边各 C），同样大小归一组。C 超过 6mm 的斜线不当倒角（多半是斜面）
        public static List<RadiusGroup> Chamfers(IList<DwgEdge> edges){
            var lines = edges.Where(e => e.Kind == DwgKind.Line && !e.IsHorizontal && !e.IsVertical
                && Math.Abs(Math.Abs(e.X2 - e.X1) - Math.Abs(e.Y2 - e.Y1)) < 1e-5 && Math.Abs(e.X2 - e.X1) <= 0.006 + 1e-9).ToList();
            var r = new List<RadiusGroup>();
            foreach (var g in Cluster(lines, l => Math.Round(Math.Abs(l.X2 - l.X1), 6), SameCoord)) {
                var rg = new RadiusGroup { RadiusMm = Math.Round(Math.Abs(g[0].X2 - g[0].X1) * 1000, 2) };
                rg.Arcs.AddRange(g);
                rg.Leader = g.OrderByDescending(l => (l.X1 + l.X2) + (l.Y1 + l.Y2)).First();
                r.Add(rg);
            }
            return r;
        }
        public static string ChamferText(int count, double c){ return (count > 1 ? count + "-" : "") + "C" + c.ToString("0.##", CultureInfo.InvariantCulture); }

        // 孔组的标注文字（不是孔特征、天工给不出孔标注时用）："4×Ø6.6 贯通"
        public static string HoleText(int count, double diameterMm, string tail){
            string d = diameterMm.ToString("0.##", CultureInfo.InvariantCulture);
            return (count > 1 ? count + "×" : "") + "%DI" + d + (string.IsNullOrEmpty(tail) ? "" : " " + tail);
        }
        // 和孔同心、比孔大的螺纹线 → 螺纹规格（大径取整到 0.5：M4、M5、M6、M8、M10、M12…；认不出给空）
        public static string ThreadFromEdges(DwgEdge hole, IEnumerable<DwgEdge> threads){
            foreach (var t in threads) {
                if (Math.Abs(t.Cx - hole.Cx) > SameCoord || Math.Abs(t.Cy - hole.Cy) > SameCoord || t.R <= hole.R + 1e-6) continue;
                double d = t.R * 2000, m = Math.Round(d * 2) / 2;
                if (Math.Abs(d - m) > 0.15 || m < 1.5 || m > 64) continue;
                return "M" + m.ToString("0.#", CultureInfo.InvariantCulture);
            }
            return "";
        }
        public static string RadiusPrefix(int count){ return count > 1 ? count + "-" : ""; }

        // ---- 比例 ----
        // GB/T 14690 优先比例 + 普查里出现过的（1:3、1:4、1:15、1:25 等）。从大到小试，第一个放得下的。
        public static readonly double[] Scales = { 10, 5, 4, 3, 2, 1.5, 1, 1 / 1.5, 0.5, 0.4, 1 / 3.0, 0.25, 0.2, 0.1, 1 / 15.0, 0.05, 0.04, 1 / 30.0, 0.025, 0.02, 0.01, 0.005, 0.002 };
        public static double PickScale(double needW, double needH, double availW, double availH){
            foreach (double s in Scales) if (needW * s <= availW && needH * s <= availH) return s;
            return Scales[Scales.Length - 1];
        }
        public static string ScaleText(double s){
            if (s >= 1) return Fmt(s) + ":1";
            return "1:" + Fmt(1 / s);
        }
        static string Fmt(double v){ return Math.Round(v, 2).ToString("0.##", CultureInfo.InvariantCulture); }
    }

    // 三维模型里的一个孔（一根轴线）。Merge 之前是一段凹圆柱面，之后是整根孔：Radius = 最细那段（孔本身），
    // CbRadius/CbDepth = 孔口更粗的那段（沉孔），Depth = 孔本身那段的长度，Through = 两端都不封口。
    public sealed class ModelHole {
        public V3 Point, Axis;
        public double Radius, Lo, Hi;            // 段：沿轴线的范围（相对 Point）
        public bool BlindLo, BlindHi;            // 段：这一端封口
        public double CbRadius, CbDepth;
        public double Depth;
        public bool Through = true;
        public HoleSpec Spec;                    // 孔特征的规格（读得到时）
        public readonly List<ModelHole> Segments = new List<ModelHole>();

        public bool OnAxis(V3 p, double tol){
            var d = p - Point; var radial = d - Axis * d.Dot(Axis);
            return radial.Length <= tol;
        }
        public static List<ModelHole> Merge(IEnumerable<ModelHole> segments){
            var holes = new List<ModelHole>();
            foreach (var s in segments) {
                ModelHole h = holes.FirstOrDefault(x => Math.Abs(Math.Abs(x.Axis.Dot(s.Axis)) - 1) < 1e-4 && x.OnAxis(s.Point, 2e-5));
                if (h == null) { h = new ModelHole { Point = s.Point, Axis = s.Axis }; holes.Add(h); }
                // 统一到 h 的轴向参数
                double shift = (s.Point - h.Point).Dot(h.Axis), sign = h.Axis.Dot(s.Axis) >= 0 ? 1 : -1;
                double lo = shift + sign * s.Lo, hi = shift + sign * s.Hi;
                var seg = new ModelHole { Point = h.Point, Axis = h.Axis, Radius = s.Radius, Lo = Math.Min(lo, hi), Hi = Math.Max(lo, hi), BlindLo = sign > 0 ? s.BlindLo : s.BlindHi, BlindHi = sign > 0 ? s.BlindHi : s.BlindLo };
                // 同一根轴上同半径的两段（被槽打断的孔）合成一段
                var same = h.Segments.FirstOrDefault(x => Math.Abs(x.Radius - seg.Radius) < 1e-6);
                if (same != null) { same.Lo = Math.Min(same.Lo, seg.Lo); same.Hi = Math.Max(same.Hi, seg.Hi); same.BlindLo |= seg.BlindLo; same.BlindHi |= seg.BlindHi; }
                else h.Segments.Add(seg);
            }
            foreach (var h in holes) {
                var main = h.Segments.OrderBy(x => x.Radius).First();
                h.Radius = main.Radius; h.Lo = main.Lo; h.Hi = main.Hi;
                h.Depth = main.Hi - main.Lo;
                h.Through = !(main.BlindLo || main.BlindHi);
                h.BlindLo = main.BlindLo; h.BlindHi = main.BlindHi;
                // 沉孔：比孔粗、贴着孔的一端的那段
                var cb = h.Segments.Where(x => x.Radius > main.Radius + 1e-6 && (Math.Abs(x.Hi - main.Lo) < 2e-5 || Math.Abs(x.Lo - main.Hi) < 2e-5)).OrderBy(x => x.Radius).FirstOrDefault();
                if (cb != null) { h.CbRadius = cb.Radius; h.CbDepth = cb.Hi - cb.Lo; if (!h.Through && Math.Abs(cb.Lo - main.Hi) < 2e-5 && main.BlindLo) { } }
            }
            return holes;
        }
    }

    public static class DrawingText {
        // 粗牙普通螺纹的标准攻丝底孔（GB/T 20330），查不到就用模型里的孔径
        public static double TapDrill(string thread, double modelMm){
            switch ((thread ?? "").ToUpperInvariant()) {
                case "M2": return 1.6; case "M2.5": return 2.05; case "M3": return 2.5; case "M4": return 3.3; case "M5": return 4.2; case "M6": return 5;
                case "M8": return 6.8; case "M10": return 8.5; case "M12": return 10.2; case "M14": return 12; case "M16": return 14; case "M20": return 17.5; case "M24": return 21; case "M30": return 26.5;
            }
            return modelMm;
        }
        // 只有螺纹线（没有孔特征）的螺纹孔：螺纹深度不知道，第一行只写规格（盲孔不写贯通），第二行写底孔和钻孔深度
        public static void ThreadOnly(string thread, ModelHole h, out string suffix, out string subfix){
            suffix = ""; subfix = "";
            if (h == null || h.Through) { suffix = " 贯通"; return; }
            subfix = "%DI" + N(TapDrill(thread, h.Radius * 2000)) + "%DP" + N(h.Depth * 1000);
        }
        static string N(double mm){ return Math.Round(mm, 2).ToString("0.##", CultureInfo.InvariantCulture); }
        // 孔标注的三段（和人画的图一个写法，普查 YF25.07 / SK26.01 的孔标注）：
        //   前缀：数量 "4×"；螺纹孔再接 "M5"（这时尺寸值用替换文字盖掉，不显示底孔直径）
        //   后缀：" 贯通" 或 "%DP10"（深度符号 + 深度）
        //   下标：沉孔 "%CB%DI11%DP6.8"
        public static void Hole(int count, ModelHole h, double diameterMm, out string prefix, out string valueOverride, out string suffix, out string subfix){
            Hole(count, h, diameterMm, true, out prefix, out valueOverride, out suffix, out subfix);
        }
        // isHole=false：凸台 / 轴 / 圆形零件外圆，只写直径（不写贯通）
        public static void Hole(int count, ModelHole h, double diameterMm, bool isHole, out string prefix, out string valueOverride, out string suffix, out string subfix){
            prefix = count > 1 ? count + "×" : ""; valueOverride = ""; suffix = ""; subfix = "";
            if (!isHole) return;
            var spec = h == null ? null : h.Spec;
            bool through = h == null ? true : h.Through;
            if (spec != null && spec.Kind == HoleKind.Tapped && spec.ThreadSize.Length > 0) {
                valueOverride = spec.ThreadSize;
                if (spec.ThreadFull && (h == null || h.Through)) suffix = " 贯通";
                else suffix = "%DP" + N(spec.ThreadFull ? (h != null ? h.Depth * 1000 : spec.EffectiveThreadDepth) : spec.ThreadDepth);
                // 盲螺纹孔第二行写底孔：Ø3.3↧10.1（人画的图都带，钻孔深度机加工要用）。底孔直径写标准攻丝底孔（M4→3.3），不写模型里的小径 3.24
                if (h != null && !h.Through && h.Radius > 0) subfix = "%DI" + N(TapDrill(spec.ThreadSize, h.Radius * 2000)) + "%DP" + N(h.Depth * 1000);
                return;
            }
            if (h != null && h.CbRadius > 0) subfix = "%CB%DI" + N(h.CbRadius * 2000) + "%DP" + N(h.CbDepth * 1000);
            else if (spec != null && spec.Kind == HoleKind.Counterbore && spec.CounterboreDiameter > 0) subfix = "%CB%DI" + N(spec.CounterboreDiameter) + "%DP" + N(spec.CounterboreDepth);
            if (through) suffix = " 贯通";
            else if (h != null) suffix = "%DP" + N(h.Depth * 1000);
        }
    }
}
