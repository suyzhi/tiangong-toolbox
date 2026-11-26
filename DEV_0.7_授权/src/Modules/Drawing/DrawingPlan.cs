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
        public bool Truncated;           // 原始落点超过 MaxRun 被 Significant 裁剪过（提醒人工补）
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
            for (int q = 0; q < 4; q++) {
                double ang = q * Math.PI / 2;
                if (OnArcAngle(a, ang)) yield return new[] { a.Cx + a.R * Math.Cos(ang), a.Cy + a.R * Math.Sin(ang) };
            }
        }
        // 圆弧实际扫过的范围（逆时针：从 start 转 extent）。按两个端点定，天工报的扫角只用它的大小来挑是哪一段：
        // 视图转过 90° 时（OK310-02-010 主视图 x 沿模型 Y）DVArc2d 的扫角正负和起点对不上，按扫角正负算会把 R20 圆角
        // 当成往下扫的那段，外框多出 20mm、竖直坐标串的基准落到零件外面（第 21 轮 010 / 011 都是 75%）。
        static void ArcSpan(DwgEdge a, out double start, out double extent){
            double s0 = Math.Atan2(a.Y1 - a.Cy, a.X1 - a.Cx), e0 = Math.Atan2(a.Y2 - a.Cy, a.X2 - a.Cx);
            double ccw = e0 - s0; while (ccw < 0) ccw += 2 * Math.PI; while (ccw >= 2 * Math.PI) ccw -= 2 * Math.PI;
            double sw = Math.Abs(a.Sweep);
            if (sw >= 2 * Math.PI - 1e-6 || ccw < 1e-9) { start = s0; extent = sw > 1e-9 ? sw : 2 * Math.PI; return; }
            double cw = 2 * Math.PI - ccw;
            // 正好半圆：两个端点分不出是哪一半，只能按扫角正负（销子球头）
            bool fromStart = Math.Abs(ccw - cw) < 1e-3 ? a.Sweep >= 0 : Math.Abs(ccw - sw) <= Math.Abs(cw - sw);
            if (fromStart) { start = s0; extent = ccw; } else { start = e0; extent = cw; }
        }
        static bool OnArcAngle(DwgEdge a, double t){
            double start, extent; ArcSpan(a, out start, out extent);
            double d = t - start; while (d < -1e-12) d += 2 * Math.PI; while (d >= 2 * Math.PI) d -= 2 * Math.PI;
            return d <= extent + 1e-6;
        }

        // ---- 坐标尺寸要标的落点 ----
        // horizontal=true：量 x —— 竖线、圆心、整半圆以上的弧心（腰孔两头）都是落点；
        // 同一个 x 有好几个图元时挑一个：圆心优先（孔位是加工最要的），同类别优先挑离尺寸文字线最近的那一个
        // （上方坐标串挂在靠上的孔 / 边端，避免延伸线从零件底边贯穿整张视图），再按半径 / 长度决胜。
        // 圆角（扫角不到半圆的弧）不当落点：它的位置由两条直边定，人画的图也不标圆角心。
        public static List<DwgTarget> Targets(IList<DwgEdge> edges, bool horizontal){
            return Targets(edges, horizontal, true);
        }
        public static List<DwgTarget> Targets(IList<DwgEdge> edges, bool horizontal, bool lowSide){
            var raw = new List<DwgTarget>();
            foreach (var e in edges) {
                if (e.Kind == DwgKind.Line) {
                    if (horizontal ? e.IsVertical : e.IsHorizontal) {
                        double lo = horizontal ? Math.Min(e.Y1, e.Y2) : Math.Min(e.X1, e.X2);
                        double hi = horizontal ? Math.Max(e.Y1, e.Y2) : Math.Max(e.X1, e.X2);
                        // 落点选在靠近文字线一端往里缩一点的位置（≤2mm 或 10% 边长），既避开角点交汇，又避免延伸线穿过整个零件
                        double inset = Math.Min((hi - lo) * 0.1, 0.002);
                        double along = lowSide ? lo + inset : hi - inset;
                        raw.Add(new DwgTarget {
                            Edge = e, KeyPoint = false,
                            X = horizontal ? e.X1 : along,
                            Y = horizontal ? along : e.Y1,
                            Coord = horizontal ? e.X1 : e.Y1
                        });
                    }
                } else if (e.Kind == DwgKind.Circle || Math.Abs(e.Sweep) >= Math.PI - 1e-6) {
                    raw.Add(new DwgTarget { Edge = e, KeyPoint = true, X = e.Cx, Y = e.Cy, Coord = horizontal ? e.Cx : e.Cy });
                }
            }
            // 外形的最外端是圆弧（销子的球头、圆头板）时那里没有直线，总长就标不出来：补一个落在圆弧最外点上的落点
            var box = Box(edges);
            double blo = horizontal ? box[0] : box[1], bhi = horizontal ? box[2] : box[3];
            foreach (double ext in new[] { blo, bhi }) {
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
            // 图元向尺寸线一侧伸到的极值（按 0.5mm 量化，同侧平齐的两条边再按长度挑更长的主轮廓边）
            Func<DwgTarget, double> reach = t => {
                double p;
                if (t.Edge.Kind == DwgKind.Line) {
                    double l = horizontal ? Math.Min(t.Edge.Y1, t.Edge.Y2) : Math.Min(t.Edge.X1, t.Edge.X2);
                    double h = horizontal ? Math.Max(t.Edge.Y1, t.Edge.Y2) : Math.Max(t.Edge.X1, t.Edge.X2);
                    p = lowSide ? -l : h;
                } else {
                    double c = horizontal ? t.Y : t.X;
                    p = lowSide ? -c : c;
                }
                return Math.Round(p * 2000.0);
            };
            var result = new List<DwgTarget>();
            foreach (var g in Cluster(raw, t => t.Coord, SameCoord)) {
                var best = g.OrderByDescending(t => t.KeyPoint ? 1 : 0)
                    .ThenByDescending(reach)
                    .ThenByDescending(t => t.KeyPoint ? t.Edge.R : t.Edge.Length)
                    .First();
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
        // 精确版轮廓内分界线过滤：仅当距离确实匹配该轴记录的倒角边长 / 非钣金外倒圆半径，且该边在二维上确属贴外缘的倒角/圆角分界线时才滤除，
        // 避免按全零件最大倒角/折弯半径（≤6mm）一刀切删掉钣金板厚、薄壁内腔壁或台阶特征。
        public static List<DwgTarget> DropNearOutline(List<DwgTarget> targets, IList<DwgEdge> edges, bool horizontal, double[] box, double tol, IList<double> exactOffsets, bool sheetMetal){
            if (targets == null || targets.Count == 0) return targets;
            if (exactOffsets == null) return DropNearOutline(targets, horizontal ? box[0] : box[1], horizontal ? box[2] : box[3], tol);
            if (exactOffsets.Count == 0) return targets;
            double lo = horizontal ? box[0] : box[1], hi = horizontal ? box[2] : box[3];
            double perpLo = horizontal ? box[1] : box[0], perpHi = horizontal ? box[3] : box[2];
            double maxOff = Math.Max(tol, exactOffsets.Max());
            if (maxOff <= 0) return targets;
            const double offTol = 5e-5;
            return targets.Where(t => {
                if (t.KeyPoint || t.Edge == null || t.Edge.Kind != DwgKind.Line) return true;
                double dLo = t.Coord - lo, dHi = hi - t.Coord;
                bool hitLo = dLo > SameCoord && exactOffsets.Any(o => Math.Abs(dLo - o) <= offTol);
                bool hitHi = dHi > SameCoord && exactOffsets.Any(o => Math.Abs(dHi - o) <= offTol);
                if (!hitLo && !hitHi) return true;
                // 钣金件折弯内壁不是外棱倒角切线：仅当线端直连外侧 45° 倒角斜线时才视作倒角分界线
                if (sheetMetal) {
                    bool isCh = (hitLo && TouchesOutwardChamferOrArc(t.Edge, edges, horizontal, t.Coord, true, true))
                        || (hitHi && TouchesOutwardChamferOrArc(t.Edge, edges, horizontal, t.Coord, false, true));
                    return !isCh;
                }
                if ((hitLo && IsOutlineSeamLine(t.Edge, edges, horizontal, t.Coord, perpLo, perpHi, true))
                    || (hitHi && IsOutlineSeamLine(t.Edge, edges, horizontal, t.Coord, perpLo, perpHi, false))) return false;
                return true;
            }).ToList();
        }
        static bool IsOutlineSeamLine(DwgEdge line, IList<DwgEdge> edges, bool horizontal, double coord, double perpLo, double perpHi, bool towardLo){
            double eLo = horizontal ? Math.Min(line.Y1, line.Y2) : Math.Min(line.X1, line.X2);
            double eHi = horizontal ? Math.Max(line.Y1, line.Y2) : Math.Max(line.X1, line.X2);
            // 贴着垂向外形端面直达外沿（如正对面的外棱圆角切线 / 端面 C 角线），或端点连着向外伸到外框的倒角斜线/圆弧
            if (eLo <= perpLo + 5e-5 || eHi >= perpHi - 5e-5) return true;
            return TouchesOutwardChamferOrArc(line, edges, horizontal, coord, towardLo, false);
        }
        static bool TouchesOutwardChamferOrArc(DwgEdge line, IList<DwgEdge> edges, bool horizontal, double coord, bool towardLo, bool chamferOnly){
            if (edges == null) return false;
            const double ptTol = 5e-5;
            double[] xs = { line.X1, line.X2 }, ys = { line.Y1, line.Y2 };
            foreach (var o in edges) {
                if (o == null || ReferenceEquals(o, line)) continue;
                if (o.Kind == DwgKind.Circle) continue;
                if (chamferOnly && o.Kind != DwgKind.Line) continue;
                bool touches = false;
                for (int i = 0; i < 2 && !touches; i++) {
                    if ((Math.Abs(o.X1 - xs[i]) <= ptTol && Math.Abs(o.Y1 - ys[i]) <= ptTol)
                        || (Math.Abs(o.X2 - xs[i]) <= ptTol && Math.Abs(o.Y2 - ys[i]) <= ptTol)) touches = true;
                }
                if (!touches) continue;
                if (o.Kind == DwgKind.Line) {
                    if (chamferOnly && (o.IsHorizontal || o.IsVertical)) continue;
                    double oMin = horizontal ? Math.Min(o.X1, o.X2) : Math.Min(o.Y1, o.Y2);
                    double oMax = horizontal ? Math.Max(o.X1, o.X2) : Math.Max(o.Y1, o.Y2);
                    if (towardLo ? oMin < coord - SameCoord : oMax > coord + SameCoord) return true;
                } else if (o.Kind == DwgKind.Arc) {
                    var ab = Box(new[] { o });
                    double oMin = horizontal ? ab[0] : ab[1], oMax = horizontal ? ab[2] : ab[3];
                    if (towardLo ? oMin < coord - SameCoord : oMax > coord + SameCoord) return true;
                }
            }
            return false;
        }

        // 点 (x,y)（已在圆上）是不是落在这段弧扫过的范围里
        static bool OnArc(DwgEdge a, double x, double y){ return OnArcAngle(a, Math.Atan2(y - a.Cy, x - a.Cx)); }

        // 圆弧真正的中点（弧曲线上，而非圆心）：给 R 标注找最近空地与测引线长度用。
        // 以前拿圆心当锚点，大半径圆弧（02-010 的 R35/R34）会被误以为在零件正中间，把引线横穿整张视图拉到对侧。
        public static double[] ArcMidpoint(DwgEdge a){
            if (a == null) return new[] { 0.0, 0.0 };
            if (a.Kind == DwgKind.Line) return new[] { (a.X1 + a.X2) / 2, (a.Y1 + a.Y2) / 2 };
            if (a.Kind == DwgKind.Circle) return new[] { a.Cx, a.Cy };
            double start, extent; ArcSpan(a, out start, out extent);
            double mid = start + extent / 2;
            return new[] { a.Cx + a.R * Math.Cos(mid), a.Cy + a.R * Math.Sin(mid) };
        }

        // 判定一串坐标尺寸是不是"纯同心圆端面的 6-0-6 冗余串"（只有圆心 + 两个对称象限点，如销子 02-029 顶视）。
        // 这种端面的外圆已经由 Ø12 直接标出直径，再在上下左右画两套 6-0-6 坐标串既冗余又把视图挤乱。
        public static bool IsRedundantCircleSpan(OrdinateRun run){
            if (run == null || run.Origin == null || run.Targets.Count != 2) return false;
            if (!run.Origin.KeyPoint || run.Origin.Edge == null || run.Origin.Edge.Kind != DwgKind.Circle) return false;
            return run.Targets.All(t => t.Quadrant && t.Edge != null && t.Edge.Kind == DwgKind.Circle);
        }

        // 在一组同类孔 / 圆弧 / 倒角中挑选最合适的引线代表：
        // 优先靠向开放标注侧（通常为右侧外沿）以缩短引线、避免引线穿零件内部；
        // 当视图里有多组孔同时竞争同一侧时，尽量选 Y 坐标与已选组错开的实例，从源头杜绝引线交叉成麻花（02-031）。
        public static DwgEdge PickSeparatedLeader(IList<DwgEdge> candidates, double[] viewBox, bool preferRight, IList<double> usedSheetY, Func<DwgEdge, double[]> toAnchorSheet, double minSepSheet){
            if (candidates == null || candidates.Count == 0) return null;
            if (candidates.Count == 1) return candidates[0];
            double vx0 = viewBox != null && viewBox.Length >= 4 ? viewBox[0] : 0;
            double vy0 = viewBox != null && viewBox.Length >= 4 ? viewBox[1] : 0;
            double vx1 = viewBox != null && viewBox.Length >= 4 ? viewBox[2] : 0;
            double vy1 = viewBox != null && viewBox.Length >= 4 ? viewBox[3] : 0;
            double spanX = Math.Max(vx1 - vx0, 1e-6), spanY = Math.Max(vy1 - vy0, 1e-6);
            DwgEdge best = candidates[0];
            double bestScore = double.MaxValue;
            foreach (var c in candidates) {
                var a = AnchorOf(c);
                // 到偏好侧边缘的归一化距离（越小越靠近外沿，引线越短）
                double distPref = preferRight ? (vx1 - a[0]) / spanX : (a[0] - vx0) / spanX;
                double distTop = (vy1 - a[1]) / spanY;
                double score = distPref * 1.6 + distTop * 0.15;
                if (usedSheetY != null && usedSheetY.Count > 0 && toAnchorSheet != null) {
                    var s = toAnchorSheet(c);
                    double minDy = double.MaxValue;
                    foreach (double uy in usedSheetY) minDy = Math.Min(minDy, Math.Abs(s[1] - uy));
                    if (minDy < minSepSheet) score += (minSepSheet - minDy) / Math.Max(minSepSheet, 1e-6) * 0.85;
                }
                if (score < bestScore) { bestScore = score; best = c; }
            }
            return best;
        }

        public static double[] AnchorOf(DwgEdge e){
            if (e == null) return new[] { 0.0, 0.0 };
            if (e.Kind == DwgKind.Line) return new[] { (e.X1 + e.X2) / 2, (e.Y1 + e.Y2) / 2 };
            if (e.Kind == DwgKind.Arc) return ArcMidpoint(e);
            return new[] { e.Cx, e.Cy };
        }

        // 判断某个圆角（纸面弧中点 anchorSheet，视图轮廓框 cbox）是否允许在内部无边空腔就近放置：
        //   · 仅对小圆角（≤R12）开启，外轮廓大弧（02-010 R34/R35）一律放视图外侧；
        //   · 完全在轮廓内部（01-004 内窗口 4-R2、02-010 内凸台 4-R2）或深居开口槽/U 槽内（02-015 底部 U 槽 4-R10，
        //     水平距两侧轮廓均 >15mm、竖向离底边约 1.95mm）允许进空腔；外角圆角（02-001 4-R5）和外侧耳边圆角（02-015 10-R10）保持放外侧。
        public static bool AllowRadiusInVoid(double radiusMm, double[] anchorSheet, double[] cbox){
            if (radiusMm > 12.0 || anchorSheet == null || anchorSheet.Length < 2 || cbox == null || cbox.Length < 4) return false;
            double distX = Math.Min(anchorSheet[0] - cbox[0], cbox[2] - anchorSheet[0]);
            double distY = Math.Min(anchorSheet[1] - cbox[1], cbox[3] - anchorSheet[1]);
            if (distX > 0.003 && distY > 0.003) return true;
            if (distX > 0.015 && distY > 0.001) return true;
            if (distY > 0.015 && distX > 0.001) return true;
            return false;
        }

        // 视图坐标下的候选文字框 rect={x0,y0,x1,y1} 是否完全避开视图里的所有模型边线/圆/弧（保留 margin 间隙）。
        // 用于凹圆角（01-004 居中大窗口里的 4-R2、02-015 U 型槽里的 4-R10）或台阶缺口旁就近放文字，不拉 100mm 长引线。
        public static bool RectClearsViewEdges(double[] rect, IList<DwgEdge> edges, double margin){
            if (rect == null || rect.Length < 4 || edges == null) return false;
            double x0 = rect[0] - margin, y0 = rect[1] - margin, x1 = rect[2] + margin, y1 = rect[3] + margin;
            foreach (var e in edges) {
                if (e.Kind == DwgKind.Line) {
                    if (SegmentIntersectsRect(e.X1, e.Y1, e.X2, e.Y2, x0, y0, x1, y1)) return false;
                } else if (e.Kind == DwgKind.Circle) {
                    double cx = Math.Max(x0, Math.Min(e.Cx, x1)), cy = Math.Max(y0, Math.Min(e.Cy, y1));
                    double dx = e.Cx - cx, dy = e.Cy - cy;
                    if (dx * dx + dy * dy <= e.R * e.R + 1e-12) return false;
                } else {
                    // 圆弧用若干采样点 + 端点近似，够快且对小/大弧都保守安全
                    double start, extent; ArcSpan(e, out start, out extent);
                    int steps = Math.Max(4, (int)Math.Ceiling(extent / (Math.PI / 12)));
                    double px = e.Cx + e.R * Math.Cos(start), py = e.Cy + e.R * Math.Sin(start);
                    for (int i = 1; i <= steps; i++) {
                        double ang = start + extent * i / steps;
                        double nx = e.Cx + e.R * Math.Cos(ang), ny = e.Cy + e.R * Math.Sin(ang);
                        if (SegmentIntersectsRect(px, py, nx, ny, x0, y0, x1, y1)) return false;
                        px = nx; py = ny;
                    }
                }
            }
            return true;
        }

        // 线段 (ax,ay)-(bx,by) 是否与轴对齐矩形 [x0,y0,x1,y1] 相交或包含于其内（Liang-Barsky）
        public static bool SegmentIntersectsRect(double ax, double ay, double bx, double by, double x0, double y0, double x1, double y1){
            double dx = bx - ax, dy = by - ay;
            double t0 = 0, t1 = 1;
            double[] p = { -dx, dx, -dy, dy };
            double[] q = { ax - x0, x1 - ax, ay - y0, y1 - ay };
            for (int i = 0; i < 4; i++) {
                if (Math.Abs(p[i]) < 1e-12) { if (q[i] < 0) return false; }
                else {
                    double r = q[i] / p[i];
                    if (p[i] < 0) { if (r > t1) return false; if (r > t0) t0 = r; }
                    else { if (r < t0) return false; if (r < t1) t1 = r; }
                }
            }
            return t0 <= t1;
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
            return Ordinate(edges, horizontal, scale, box, lowSide, offsetPaper, 0, null, false);
        }
        public static OrdinateRun Ordinate(IList<DwgEdge> edges, bool horizontal, double scale, double[] box, bool lowSide, double offsetPaper, double chamferTol){
            return Ordinate(edges, horizontal, scale, box, lowSide, offsetPaper, chamferTol, null, false);
        }
        public static OrdinateRun Ordinate(IList<DwgEdge> edges, bool horizontal, double scale, double[] box, bool lowSide, double offsetPaper, double chamferTol, IList<double> exactOffsets, bool sheetMetal){
            var ts = Targets(edges, horizontal, lowSide);
            ts = DropNearOutline(ts, edges, horizontal, box, chamferTol, exactOffsets, sheetMetal);
            int beforeSig = ts.Count;
            ts = Significant(ts, horizontal ? box[3] - box[1] : box[2] - box[0]);
            bool truncated = beforeSig > MaxRun && ts.Count < beforeSig;
            if (ts.Count < 2) return null;
            bool sym;
            var origin = PickOrigin(ts, out sym);
            var run = new OrdinateRun { Horizontal = horizontal, Origin = origin, LowSide = lowSide, Truncated = truncated };
            foreach (var t in ts) if (!ReferenceEquals(t, origin)) run.Targets.Add(t);
            var all = new List<DwgTarget>(run.Targets); all.Add(origin);
            Spread(all, TextPitch / Math.Max(scale, 1e-9));
            double off = offsetPaper / Math.Max(scale, 1e-9);
            run.Line = horizontal ? (lowSide ? box[1] - off : box[3] + off) : (lowSide ? box[0] - off : box[2] + off);
            return run;
        }

        // 投影/补充视图去重：将 Origin 与 Targets 一并纳入已标集合比对；
        // 当原对称中心 Origin 未在主视图标过时，改以已定位的外形最小边作新基准并将该中心孔降入 Targets，
        // 避免居中单孔整串被删空或对称多孔脱锚浮空（20-0-20 却不交代 0 到外边的距离）。
        public static OrdinateRun FilterNewTargets(OrdinateRun r, Func<DwgTarget, bool> isAlreadyDone, double scale){
            if (r == null || r.Origin == null) return null;
            var all = new List<DwgTarget>(r.Targets.Count + 1) { r.Origin };
            all.AddRange(r.Targets);
            var novel = all.Where(t => !isAlreadyDone(t)).ToList();
            if (novel.Count == 0) return null;
            if (isAlreadyDone(r.Origin)) {
                r.Targets.Clear();
                r.Targets.AddRange(novel);
            } else {
                // 原 Origin 本身是需要新标的坐标（典型为对称中心孔）：优先找已标过的最小边线做新基准
                var anchor = all.Where(t => isAlreadyDone(t))
                    .OrderBy(t => t.KeyPoint ? 1 : 0)
                    .ThenBy(t => t.Coord)
                    .FirstOrDefault();
                if (anchor != null) {
                    r.Origin = anchor;
                    r.Targets.Clear();
                    r.Targets.AddRange(novel);
                } else {
                    // 参考视图没有任何已标坐标（如纯圆端面被跳过）：保留整串不动
                    r.Targets.Clear();
                    foreach (var t in all) if (!ReferenceEquals(t, r.Origin)) r.Targets.Add(t);
                }
            }
            if (r.Targets.Count == 0) return null;
            var spreadList = new List<DwgTarget>(r.Targets) { r.Origin };
            Spread(spreadList, TextPitch / Math.Max(scale, 1e-9));
            return r;
        }

        // 按工艺签名拆分同直径孔组：避免同径不同深、通孔/盲孔、光孔/沉孔、M6底孔Ø5/Ø5 H7销孔被并成同一个标注互相覆盖
        public static List<HoleGroup> SplitHoleGroupByKey(HoleGroup g, Func<DwgEdge, string> keyOf){
            var result = new List<HoleGroup>();
            if (g == null || g.Circles.Count == 0) return result;
            var order = new List<string>();
            var buckets = new Dictionary<string, List<DwgEdge>>(StringComparer.Ordinal);
            foreach (var c in g.Circles) {
                string k = keyOf != null ? (keyOf(c) ?? "") : "";
                List<DwgEdge> list;
                if (!buckets.TryGetValue(k, out list)) {
                    list = new List<DwgEdge>();
                    buckets[k] = list;
                    order.Add(k);
                }
                list.Add(c);
            }
            foreach (var k in order) {
                var circles = buckets[k];
                var sub = new HoleGroup { DiameterMm = g.DiameterMm };
                sub.Circles.AddRange(circles);
                sub.Leader = circles.OrderByDescending(c => c.Cx + c.Cy).First();
                result.Add(sub);
            }
            return result;
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

        // 倒角：视图里 45° 的短斜线（两条直角边各 C），同样大小归一组。
        // 下限 0.1mm 过滤掉三维投影里的微小数值碎片（避免出现「2-C0」），上限 6mm 排除斜面。
        public static List<RadiusGroup> Chamfers(IList<DwgEdge> edges){
            var lines = edges.Where(e => e.Kind == DwgKind.Line && !e.IsHorizontal && !e.IsVertical
                && Math.Abs(Math.Abs(e.X2 - e.X1) - Math.Abs(e.Y2 - e.Y1)) < 1e-5
                && Math.Abs(e.X2 - e.X1) >= 0.0001 - 1e-9
                && Math.Abs(e.X2 - e.X1) <= 0.006 + 1e-9).ToList();
            var r = new List<RadiusGroup>();
            foreach (var g in Cluster(lines, l => Math.Round(Math.Abs(l.X2 - l.X1), 6), SameCoord)) {
                var rg = new RadiusGroup { RadiusMm = Math.Round(Math.Abs(g[0].X2 - g[0].X1) * 1000, 2) };
                // 对重合的 45° 斜线按端点外框+中点去重（多实体/台阶重叠投影时会读出重复线段，避免 4-C2 被翻倍计成 8-C2）
                foreach (var l in g) {
                    double lminX = Math.Min(l.X1, l.X2), lminY = Math.Min(l.Y1, l.Y2);
                    double lmaxX = Math.Max(l.X1, l.X2), lmaxY = Math.Max(l.Y1, l.Y2);
                    bool dup = rg.Arcs.Any(o =>
                        Math.Abs(Math.Min(o.X1, o.X2) - lminX) < SameCoord && Math.Abs(Math.Min(o.Y1, o.Y2) - lminY) < SameCoord
                        && Math.Abs(Math.Max(o.X1, o.X2) - lmaxX) < SameCoord && Math.Abs(Math.Max(o.Y1, o.Y2) - lmaxY) < SameCoord);
                    if (!dup) rg.Arcs.Add(l);
                }
                if (rg.Arcs.Count == 0) continue;
                rg.Leader = rg.Arcs.OrderByDescending(l => (l.X1 + l.X2) + (l.Y1 + l.Y2)).First();
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
        // GB/T 14690 优先比例 + 公司图纸普查里常用比例（1:3、1:4、1:6、1:8、1:12.5、1:15、1:25 等）。
        // 补入 1:6 / 1:8 / 1:12.5 填平 1:5→1:10 的 2 倍断档，避免 1.3m 细长件在 A3 上被迫掉到 1:10 被判回 A4 缩成细条。
        public static readonly double[] Scales = { 10, 5, 4, 3, 2, 1.5, 1, 1 / 1.5, 0.5, 0.4, 1 / 3.0, 0.25, 0.2, 1 / 6.0, 0.125, 0.1, 0.08, 1 / 15.0, 0.05, 0.04, 1 / 30.0, 0.025, 0.02, 0.01, 0.005, 0.002 };
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
        public double CsRadius, CsAngle;         // 锥形沉孔几何外径半径（米）与锥角（度）
        public double Depth;
        public bool Through = true;
        public HoleSpec Spec;                    // 孔特征的规格（读得到时）
        public readonly List<ModelHole> Segments = new List<ModelHole>();

        public bool OnAxis(V3 p, double tol){
            var d = p - Point; var radial = d - Axis * d.Dot(Axis);
            return radial.Length <= tol;
        }
        public static List<ModelHole> Merge(IEnumerable<ModelHole> segments){
            // 先按同轴无限直线分组并统一到同一轴向坐标系，再按轴向连续性拆分成独立孔：
            //   1. U 型支架/双凸缘上隔空同轴的分离孔（如一侧 M5、另一侧 Ø5.5 过孔）不再误并成单个台阶孔；
            //   2. 厚块正反面对钻同径盲孔因各自有封底且不连通，保留为两个独立盲孔与各自真实深度和孔口朝向。
            const double AdjTol = 5e-5;
            var axisLines = new List<ModelHole>();
            var lineSegs = new List<List<ModelHole>>();
            foreach (var s in segments) {
                int idx = -1;
                for (int i = 0; i < axisLines.Count; i++) {
                    if (Math.Abs(Math.Abs(axisLines[i].Axis.Dot(s.Axis)) - 1) < 1e-4 && axisLines[i].OnAxis(s.Point, 2e-5)) { idx = i; break; }
                }
                if (idx < 0) {
                    idx = axisLines.Count;
                    axisLines.Add(new ModelHole { Point = s.Point, Axis = s.Axis });
                    lineSegs.Add(new List<ModelHole>());
                }
                var refLine = axisLines[idx];
                double shift = (s.Point - refLine.Point).Dot(refLine.Axis), sign = refLine.Axis.Dot(s.Axis) >= 0 ? 1 : -1;
                double lo = shift + sign * s.Lo, hi = shift + sign * s.Hi;
                var seg = new ModelHole {
                    Point = refLine.Point, Axis = refLine.Axis, Radius = s.Radius,
                    Lo = Math.Min(lo, hi), Hi = Math.Max(lo, hi),
                    BlindLo = sign > 0 ? s.BlindLo : s.BlindHi,
                    BlindHi = sign > 0 ? s.BlindHi : s.BlindLo,
                    CsRadius = s.CsRadius, CsAngle = s.CsAngle
                };
                lineSegs[idx].Add(seg);
            }

            var holes = new List<ModelHole>();
            for (int li = 0; li < axisLines.Count; li++) {
                var ordered = lineSegs[li].OrderBy(x => x.Lo).ThenBy(x => x.Hi).ToList();
                var clusters = new List<List<ModelHole>>();
                foreach (var seg in ordered) {
                    if (clusters.Count == 0) {
                        clusters.Add(new List<ModelHole> { seg });
                        continue;
                    }
                    var cur = clusters[clusters.Count - 1];
                    double curHi = cur.Max(x => x.Hi);
                    // 仅当两段轴向重叠或紧贴（≤0.05mm），且顺序衔接处没有任何封底（BlindHi / BlindLo）时才属同一个物理孔
                    bool overlaps = seg.Lo < curHi - AdjTol;
                    bool touches = !overlaps && seg.Lo <= curHi + AdjTol;
                    bool blockedByBlind = touches && (seg.BlindLo || cur.Any(x => Math.Abs(x.Hi - curHi) <= AdjTol && x.BlindHi));
                    if (overlaps || (touches && !blockedByBlind)) cur.Add(seg);
                    else clusters.Add(new List<ModelHole> { seg });
                }

                foreach (var cl in clusters) {
                    var h = new ModelHole { Point = axisLines[li].Point, Axis = axisLines[li].Axis };
                    foreach (var seg in cl) {
                        // 同一孔内同半径的拆分圆柱面（如 STEP 两半圆柱或紧邻接段）合成一段，端面封口按实际外端归属合并
                        var same = h.Segments.FirstOrDefault(x => Math.Abs(x.Radius - seg.Radius) < 1e-6);
                        if (same != null) {
                            double newLo = Math.Min(same.Lo, seg.Lo), newHi = Math.Max(same.Hi, seg.Hi);
                            bool bLo = (Math.Abs(same.Lo - newLo) <= AdjTol && same.BlindLo) || (Math.Abs(seg.Lo - newLo) <= AdjTol && seg.BlindLo);
                            bool bHi = (Math.Abs(same.Hi - newHi) <= AdjTol && same.BlindHi) || (Math.Abs(seg.Hi - newHi) <= AdjTol && seg.BlindHi);
                            same.Lo = newLo; same.Hi = newHi; same.BlindLo = bLo; same.BlindHi = bHi;
                            if (seg.CsRadius > same.CsRadius) { same.CsRadius = seg.CsRadius; same.CsAngle = seg.CsAngle; }
                        } else {
                            h.Segments.Add(seg);
                        }
                    }
                    var main = h.Segments.OrderBy(x => x.Radius).First();
                    h.Radius = main.Radius; h.Lo = main.Lo; h.Hi = main.Hi;
                    h.Through = !(main.BlindLo || main.BlindHi);
                    h.BlindLo = main.BlindLo; h.BlindHi = main.BlindHi;
                    foreach (var sg in h.Segments) if (sg.CsRadius > h.CsRadius) { h.CsRadius = sg.CsRadius; h.CsAngle = sg.CsAngle; }
                    // 沉孔：比孔粗、贴着孔的一端的那段
                    var cb = h.Segments.Where(x => x.Radius > main.Radius + 1e-6 && (Math.Abs(x.Hi - main.Lo) < AdjTol || Math.Abs(x.Lo - main.Hi) < AdjTol)).OrderBy(x => x.Radius).FirstOrDefault();
                    if (cb != null) {
                        h.CbRadius = cb.Radius;
                        h.CbDepth = cb.Hi - cb.Lo;
                    }
                    // 盲沉孔总深度应从开口表面量至孔底（包含沉孔深），与工程图习惯和孔特征 Depth 一致
                    h.Depth = (!h.Through && cb != null)
                        ? (Math.Max(main.Hi, cb.Hi) - Math.Min(main.Lo, cb.Lo))
                        : (main.Hi - main.Lo);
                    holes.Add(h);
                }
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
            double holeDepthMm = DrawingStyleRules.NominalHoleDepthMm(h);
            // 双重校验孔径合理性：过孔（Ø5.5 / Ø6.6）绝不能按 M5 / M6 螺纹孔输出
            bool validTapped = spec != null && spec.Kind == HoleKind.Tapped && spec.ThreadSize.Length > 0
                && DrawingStyleRules.IsNominalTappedDiameter(h != null && h.Radius > 0 ? h.Radius * 2000 : diameterMm, spec.ThreadSize);
            if (validTapped) {
                valueOverride = spec.ThreadSize;
                if (spec.ThreadFull && (h == null || h.Through)) {
                    suffix = " 贯通";
                } else if (spec.ThreadFull) {
                    // 全长盲螺纹孔：优先用特征名义深度（10mm），避免把 V 底钻尖扣掉写成 8.76 / 8.52；
                    // 写法对齐公司模板 HoleCalloutSimpleThreaded（%QN%TS %ZT → 「4×M5↧10」），不重复写同深度底孔。
                    double td = holeDepthMm > 0 ? holeDepthMm : spec.EffectiveThreadDepth;
                    suffix = "%DP" + N(td);
                } else {
                    double td = spec.ThreadDepth > 0 ? spec.ThreadDepth : spec.EffectiveThreadDepth;
                    suffix = "%DP" + N(td);
                    // 指定较浅螺纹深度的盲螺纹孔（例如 M5↧10，底孔深 12）：第二行标标准攻丝底孔与钻孔总深
                    if (h != null && !h.Through && h.Radius > 0 && holeDepthMm > td + 0.1)
                        subfix = "%DI" + N(TapDrill(spec.ThreadSize, h.Radius * 2000)) + "%DP" + N(holeDepthMm);
                }
                return;
            }
            if (h != null && h.CbRadius > 0) subfix = "%CB%DI" + N(h.CbRadius * 2000) + "%DP" + N(h.CbDepth * 1000);
            else if (spec != null && spec.Kind == HoleKind.Counterbore && spec.CounterboreDiameter > 0) subfix = "%CB%DI" + N(spec.CounterboreDiameter) + "%DP" + N(spec.CounterboreDepth);
            else if (spec != null && spec.Kind == HoleKind.Countersink && spec.CountersinkDiameter > 0) subfix = "%CS%DI" + N(spec.CountersinkDiameter) + "×" + N(spec.CountersinkAngle > 0 ? spec.CountersinkAngle : 90) + "°";
            else if (h != null && h.CsRadius > 0) subfix = "%CS%DI" + N(h.CsRadius * 2000) + "×" + N(h.CsAngle > 0 ? h.CsAngle : 90) + "°";
            if (through) suffix = " 贯通";
            else if (h != null && holeDepthMm > 0) suffix = "%DP" + N(holeDepthMm);
        }
    }
}
