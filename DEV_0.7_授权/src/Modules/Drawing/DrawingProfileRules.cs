using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace TianGongCadSuite {
    // 零件出图形态：后台自动判别，决定选视图、线条过滤与标注风格。
    public enum DrawingPartKind {
        Standard,      // 常规机加件 / 钣金件
        ProfileSingle, // 单根铝型材（40×40 / 40×80 等 T 型槽型材）
        ProfileFrame,  // 型材框架 / 大型空心方通焊接框架
        Lathe          // 车床回转件（台阶轴 / 销 / 衬套 / 圆形法兰）
    }

    // 全视图统一的 3D XYZ 基准（米，模型坐标系）：
    //   · 过去每个视图各自调用 DrawingPlan.PickOrigin 挑本视图最小坐标，导致俯视图与左视图在深度轴上 0 落在相反两面，
    //     后视图/右视图因镜像 0 跳到对侧，主视图对称居中而投影视图落回边线；
    //   · 现在先在三维模型上锁定唯一一套 (Coord[0], Coord[1], Coord[2])，所有视图按各自投影方向换算成视图坐标后强制锁 0，
    //     镜像视图宁可 0 落在右侧也绝不换边。
    public sealed class ModelDatum3D {
        public readonly double[] Coord = new double[3];       // 当前采用的 3D 基准面坐标（X/Y/Z，米）
        public readonly double[] EdgeCoord = new double[3];   // 非对称回退用的外轮廓端面 3D 坐标（米）
        public readonly double[] MidCoord = new double[3];    // 模型包围盒中心 3D 坐标（米）
        public readonly bool[] Symmetric = new bool[3];       // 该轴是否全局采用对称中心作 0

        // 根据三维包围盒、主视图朝向、平面分布与孔心分布一次性定死全局 3D XYZ 基准：
        //   · 非对称轴：主视图水平轴锁左边面、竖直轴锁下边面、深度轴锁主视图背面（安装底面）；
        //   · 对称轴：该轴全部平面位置与孔心均关于中心面对称，且中心面上存在真实特征（孔心或对称中心线/特征）时，
        //     全局启用对称中心作 0（后续若发现有需要标该轴的投影视图在中心处无几何落点，再统一回退到 EdgeCoord）。
        public static ModelDatum3D Compute(double[] range, ModelFacts facts, IList<ModelHole> holes, DwgViewSpec mainSpec){
            var d = new ModelDatum3D();
            if (range == null || range.Length < 6) return d;
            for (int a = 0; a < 3; a++) {
                double lo = Math.Min(range[a], range[a + 3]);
                double hi = Math.Max(range[a], range[a + 3]);
                d.MidCoord[a] = (lo + hi) / 2.0;
                d.EdgeCoord[a] = lo;
                d.Coord[a] = lo;
            }
            if (mainSpec != null) {
                int ax = mainSpec.Axis;
                int hz = mainSpec.Horizontal;
                int vt = mainSpec.Vertical;
                // 主视图水平轴：左边面由 HorzSign 决定（+1 取 min，-1 取 max）
                if (hz >= 0 && hz <= 2) d.EdgeCoord[hz] = mainSpec.HorzSign >= 0 ? Math.Min(range[hz], range[hz + 3]) : Math.Max(range[hz], range[hz + 3]);
                // 主视图竖直轴（yMain = dMain × xMain）：下边面由 yMain 符号决定
                if (vt >= 0 && vt <= 2 && ax >= 0 && ax <= 2 && hz >= 0 && hz <= 2) {
                    var dm = new V3(ax == 0 ? mainSpec.Sign : 0, ax == 1 ? mainSpec.Sign : 0, ax == 2 ? mainSpec.Sign : 0);
                    var xm = new V3(hz == 0 ? mainSpec.HorzSign : 0, hz == 1 ? mainSpec.HorzSign : 0, hz == 2 ? mainSpec.HorzSign : 0);
                    var ym = dm.Cross(xm);
                    double vy = vt == 0 ? ym.X : vt == 1 ? ym.Y : ym.Z;
                    d.EdgeCoord[vt] = vy >= 0 ? Math.Min(range[vt], range[vt + 3]) : Math.Max(range[vt], range[vt + 3]);
                }
                // 深度轴：锁在主视图背面 / 安装底面（主视图从 +axis 看则背面是 min，从 -axis 看则背面是 max）
                if (ax >= 0 && ax <= 2) {
                    d.EdgeCoord[ax] = mainSpec.Sign >= 0 ? Math.Min(range[ax], range[ax + 3]) : Math.Max(range[ax], range[ax + 3]);
                }
            }
            for (int a = 0; a < 3; a++) {
                bool sym = IsAxisSymmetric(a, range, facts, holes);
                d.Symmetric[a] = sym;
                d.Coord[a] = sym ? d.MidCoord[a] : d.EdgeCoord[a];
            }
            return d;
        }

        // 把轴 a 统一回退到外轮廓端面（当某个必须标该轴的视图在中心面处找不到可挂 0 的几何落点时调用，保证所有视图一致）
        public void FallbackToEdge(int axis){
            if (axis < 0 || axis > 2) return;
            Symmetric[axis] = false;
            Coord[axis] = EdgeCoord[axis];
        }

        // 把三维基准面 P[axis] = Coord[axis] 精确投影到指定视图的水平 / 竖直视图坐标。
        // 数学关系来自 DrawingMaker.Frame：任意三维点 P 映射到视图坐标 u = P·dir + offset。
        public double ProjectToView(int axis, V3 viewDir3D, double viewOffset){
            if (axis < 0 || axis > 2) return 0;
            double comp = axis == 0 ? viewDir3D.X : axis == 1 ? viewDir3D.Y : viewDir3D.Z;
            double sign = comp >= 0 ? 1.0 : -1.0;
            return Coord[axis] * sign + viewOffset;
        }

        static bool IsAxisSymmetric(int axis, double[] range, ModelFacts facts, IList<ModelHole> holes){
            double lo = Math.Min(range[axis], range[axis + 3]);
            double hi = Math.Max(range[axis], range[axis + 3]);
            if (hi - lo < 1e-4) return false;
            double mid = (lo + hi) / 2.0;
            const double tol = 4e-5; // 0.04mm 容差，兼容 STEP 导入微小舍入误差

            // 1. 必须至少存在一个落在中心面上的真实特征（孔心或过中心的圆柱/平面特征），否则坐标 0 无几何可挂
            bool hasCenterFeature = false;
            var holeCoords = new List<double>();
            if (holes != null) {
                foreach (var h in holes) {
                    if (h == null) continue;
                    // 只统计不沿本轴的孔的轴心位置（沿本轴的孔在垂直于本轴的面上位置由端面决定）
                    double axComp = Math.Abs(axis == 0 ? h.Axis.X : axis == 1 ? h.Axis.Y : h.Axis.Z);
                    if (axComp > 0.99) continue;
                    double c = axis == 0 ? h.Point.X : axis == 1 ? h.Point.Y : h.Point.Z;
                    holeCoords.Add(c);
                    if (Math.Abs(c - mid) <= tol) hasCenterFeature = true;
                }
            }
            if (!hasCenterFeature) return false;

            // 2. 所有非沿本轴的孔心必须关于 mid 对称
            foreach (double c in holeCoords) {
                double mirror = 2 * mid - c;
                if (!holeCoords.Any(u => Math.Abs(u - mirror) <= tol)) return false;
            }

            // 3. 沿本轴的所有有效平面位置（滤除极近外缘的微小倒角/圆角分界线）也必须关于 mid 对称
            if (facts != null && facts.PlaneOffsets != null && facts.PlaneOffsets[axis] != null && facts.PlaneOffsets[axis].Count > 0) {
                double edgeTol = facts.EdgeTol != null ? facts.EdgeTol[axis] : 0;
                var planes = facts.PlaneOffsets[axis].Where(p => {
                    if (Math.Abs(p - lo) <= tol || Math.Abs(p - hi) <= tol) return true;
                    if (edgeTol > 0 && ((p - lo <= edgeTol + tol) || (hi - p <= edgeTol + tol))) return false;
                    return true;
                }).ToList();
                foreach (double p in planes) {
                    double mirror = 2 * mid - p;
                    if (!planes.Any(u => Math.Abs(u - mirror) <= tol)) return false;
                }
            }
            return true;
        }
    }

    // 铝型材与型材框架件的自动识别及 T 型槽 / 内部壁线屏蔽纯逻辑（不依赖 CAD / WinForms）。
    // 解决用户反馈核心痛点：「型材框架件坐标尺寸太多，基本每条线都打了一个坐标，完全没法用」。
    public static class DrawingProfileRules {
        static readonly Regex ProfileHintRegex = new Regex(
            @"(?:6063|铝型材|型材(?!安装板|固定块|支架)|(?:^|[^0-9])(?:2020|2040|3030|3060|4040|4080|4545|4590|5050|6060|8080|80160)(?:[^0-9]|$)|HFS|NFSL|G408|103030)",
            RegexOptions.IgnoreCase);
        static readonly Regex FrameHintRegex = new Regex(@"(?:框架|围栏|骨架|机架|台车架|焊接架)", RegexOptions.IgnoreCase);

        // 标准工业铝型材常见截面边长（毫米）
        static readonly double[] StdProfileSections = { 20, 30, 40, 45, 50, 60, 80, 90, 100, 120, 160 };

        // 判定给定模型是否为单根铝型材、型材/方通框架或常规零件。
        // 判据完全基于三维实体几何特征（可辅以文件名/材料提示），不依赖任何外部配置：
        //   · 单根铝型材：沿最长拉伸轴存在大量全长狭长平行平面（T 型槽槽壁/槽底/内腔壁，单截面 ≥10 条全长槽面），
        //     或文件名/材料带型材提示且截面吻合标准型材尺寸；
        //   · 型材/焊接框架：体积填充率低（实心体积 / 外包围盒体积 ≤ 0.28，即 >72% 为中空框口）且尺寸较大（最长边 ≥ 350mm），
        //     同时至少两个轴向上有 ≥ 6 个独立平面阶梯（多根横竖型材/方管交错围成网格）。
        public static DrawingPartKind ClassifyProfileOrFrame(ModelFacts f, string modelName, string material, out int extrusionAxis){
            extrusionAxis = -1;
            if (f == null || f.SheetMetal) return DrawingPartKind.Standard;
            double[] s = f.Size ?? new double[3];
            int longAxis = 0;
            if (s[1] > s[longAxis]) longAxis = 1;
            if (s[2] > s[longAxis]) longAxis = 2;
            int u = (longAxis + 1) % 3, v = (longAxis + 2) % 3;
            double lenMm = s[longAxis] * 1000.0;
            double w1Mm = s[u] * 1000.0, w2Mm = s[v] * 1000.0;
            double crossMax = Math.Max(w1Mm, w2Mm), crossMin = Math.Min(w1Mm, w2Mm);

            bool nameHint = ProfileHintRegex.IsMatch(modelName ?? "") || ProfileHintRegex.IsMatch(material ?? "");
            bool frameHint = FrameHintRegex.IsMatch(modelName ?? "");

            // 1. 单根直型材判别
            int fullSpanPlanes = f.FullSpanParallelPlanes != null ? f.FullSpanParallelPlanes[longAxis] : 0;
            int crossStepsSum = (f.Steps != null ? f.Steps[u] + f.Steps[v] : 0);
            bool stdCross = IsStandardProfileDim(w1Mm) && IsStandardProfileDim(w2Mm);
            bool elongated = crossMax > 1.0 && lenMm >= crossMax * 1.4;
            // 多实体数很少（≤2）才判单根型材；4 根以上独立直型材组成的是框架
            bool singleBodyLike = f.BodyCount <= 2;

            if (singleBodyLike && crossMin >= 14.0 && crossMax <= 260.0) {
                // 强几何信号：沿长轴至少有 10 张全长平行面（普通实心块只有 4 张外侧面）且横截面阶梯密集（T 型槽特征）
                if (elongated && fullSpanPlanes >= 10 && crossStepsSum >= 10) {
                    extrusionAxis = longAxis;
                    return DrawingPartKind.ProfileSingle;
                }
                // 中等几何信号 + 标准截面或名字/材质提示（覆盖较短的截断铝型材，如 40×40×50）
                if ((stdCross || nameHint) && fullSpanPlanes >= 8 && crossStepsSum >= 8) {
                    extrusionAxis = longAxis;
                    return DrawingPartKind.ProfileSingle;
                }
                // 明确命中 4040/4080/铝型材 提示且截面阶梯明显偏多（比如切了斜口导致全长面略少）
                if (nameHint && elongated && crossStepsSum >= 10) {
                    extrusionAxis = longAxis;
                    return DrawingPartKind.ProfileSingle;
                }
            }

            // 2. 型材框架 / 空心方通焊接框架判别（如 YF25.07G01-L1-M30-02-007 40×40×4 方管焊接框架、多根铝型材围栏框架）
            int richStepAxes = 0;
            if (f.Steps != null) {
                for (int a = 0; a < 3; a++) if (f.Steps[a] >= 6) richStepAxes++;
            }
            bool hollow = f.FillRatio > 0 && f.FillRatio <= 0.28;
            bool bigFrame = lenMm >= 350.0 && Math.Max(w1Mm, w2Mm) >= 180.0;
            if (bigFrame && hollow && (richStepAxes >= 2 || f.BodyCount >= 4 || frameHint)) {
                return DrawingPartKind.ProfileFrame;
            }
            if (bigFrame && f.BodyCount >= 4 && richStepAxes >= 2 && f.TotalTslotPlanes >= 16) {
                return DrawingPartKind.ProfileFrame;
            }
            return DrawingPartKind.Standard;
        }

        static bool IsStandardProfileDim(double mm){
            foreach (double d in StdProfileSections) if (Math.Abs(mm - d) <= 1.2) return true;
            return false;
        }

        // 单根铝型材 2D 视图线条过滤：
        //   · 侧面视图（看垂直于拉伸轴的视图，拉伸轴在视图里沿水平或竖直）：
        //     屏蔽所有落在横截面内部且沿拉伸轴方向的 T 型槽/内腔平行线，只留外轮廓两条边、端面边、加工孔与切口/斜切；
        //   · 端面视图（正视拉伸轴，看到 40×40 T 型槽横截面）：
        //     屏蔽所有内部 T 型槽短线、内腔圆弧与小圆角，仅保留外轮廓极值四条边与非中心工艺孔（或攻丝孔）。
        public static List<DwgEdge> FilterSingleProfileView(IList<DwgEdge> edges, bool isEndView, bool extAlongX, double[] viewBox){
            if (edges == null || edges.Count == 0 || viewBox == null || viewBox.Length < 4)
                return edges == null ? new List<DwgEdge>() : new List<DwgEdge>(edges);
            const double edgeTol = 1.5e-4; // 0.15mm 贴外框容差
            double x0 = viewBox[0], y0 = viewBox[1], x1 = viewBox[2], y1 = viewBox[3];
            var kept = new List<DwgEdge>();

            if (isEndView) {
                // 端面视图：只保留贴合外轮廓四边的直线（给出 40×40 外形）+ 真正的圆孔（供端面攻丝孔识别）
                foreach (var e in edges) {
                    if (e.Kind == DwgKind.Circle) {
                        kept.Add(e);
                    } else if (e.Kind == DwgKind.Line) {
                        bool onOuterX = e.IsVertical && (Math.Abs(e.X1 - x0) <= edgeTol || Math.Abs(e.X1 - x1) <= edgeTol);
                        bool onOuterY = e.IsHorizontal && (Math.Abs(e.Y1 - y0) <= edgeTol || Math.Abs(e.Y1 - y1) <= edgeTol);
                        if (onOuterX || onOuterY) kept.Add(e);
                    }
                }
                return kept.Count > 0 ? kept : new List<DwgEdge>(edges);
            }

            // 侧面视图：平行于拉伸轴且严格落在横截面内部的线条几乎全是 T 型槽壁线 / 空腔中心孔壁线
            double lenSpan = extAlongX ? (x1 - x0) : (y1 - y0);
            double crossLo = extAlongX ? y0 : x0;
            double crossHi = extAlongX ? y1 : x1;
            foreach (var e in edges) {
                if (e.Kind != DwgKind.Line) {
                    // 侧面视图中的圆孔、腰孔半圆弧全部保留；过滤掉极小的型材端角圆弧碎片
                    if (e.Kind == DwgKind.Circle || Math.Abs(e.Sweep) >= Math.PI - 1e-5 || e.R >= 0.0025) kept.Add(e);
                    continue;
                }
                bool parallelExt = extAlongX ? e.IsHorizontal : e.IsVertical;
                if (parallelExt) {
                    double c = extAlongX ? e.Y1 : e.X1;
                    bool onOuter = Math.Abs(c - crossLo) <= edgeTol || Math.Abs(c - crossHi) <= edgeTol;
                    if (onOuter) { kept.Add(e); continue; }
                    // 内部平行线：若贯穿大部分长度（≥ 40% 总长），一定是 T 型槽/中空孔壁线，直接屏蔽；
                    // 若很短且贴在某段槽口内，同时检查它两侧是否存在非端面垂直边（切口/缺口），否则也屏蔽
                    if (e.Length >= lenSpan * 0.35) continue;
                    // 即便被加工孔打断成几段，若同一直线坐标上多段累计长度 ≥ 45% 总长，依然是 T 型槽线
                    double sameCoordLen = SumParallelLengthAt(edges, extAlongX, c, edgeTol);
                    if (sameCoordLen >= lenSpan * 0.45) continue;
                }
                kept.Add(e);
            }
            return kept.Count > 0 ? kept : new List<DwgEdge>(edges);
        }

        static double SumParallelLengthAt(IList<DwgEdge> edges, bool horiz, double coord, double tol){
            double sum = 0;
            foreach (var e in edges) {
                if (e.Kind != DwgKind.Line) continue;
                if (horiz ? !e.IsHorizontal : !e.IsVertical) continue;
                double c = horiz ? e.Y1 : e.X1;
                if (Math.Abs(c - coord) <= tol) sum += e.Length;
            }
            return sum;
        }

        // 型材框架 / 方通焊接框架的坐标落点精简（当原始坐标落点过密时）：
        // 避免每根方管/铝型材的每一条壁线都打一个坐标，优先保留：
        //   1. 外形两端（总长/总宽/总高）与统一 3D 基准 0；
        //   2. 全部孔心（安装孔位）；
        //   3. 跨度最长的主轮廓梁外缘（过滤掉成对壁厚线和密集 T 型槽内部条纹，默认收紧到 ≤ maxTargets 个清爽坐标）。
        public static List<DwgTarget> SimplifyFrameTargets(List<DwgTarget> targets, IList<DwgEdge> edges, bool horizontal, double[] viewBox, double? forcedOrigin, int maxTargets){
            if (targets == null || targets.Count <= maxTargets) return targets;
            if (viewBox == null || viewBox.Length < 4) return targets;
            double lo = horizontal ? viewBox[0] : viewBox[1];
            double hi = horizontal ? viewBox[2] : viewBox[3];
            double perpSpan = horizontal ? (viewBox[3] - viewBox[1]) : (viewBox[2] - viewBox[0]);
            double axisSpan = hi - lo;
            const double tol = 1e-4;

            // 1. 必保项：外形两端 + 统一基准 0；孔心过多时（如大型焊接框架 30 处局部安装板孔）按主梁空间簇精简，细节留给局部放大详图
            var essential = new List<DwgTarget>();
            var holes = new List<DwgTarget>();
            var others = new List<DwgTarget>();
            foreach (var t in targets) {
                bool isEnd = Math.Abs(t.Coord - lo) <= tol || Math.Abs(t.Coord - hi) <= tol;
                bool isOrigin = forcedOrigin.HasValue && Math.Abs(t.Coord - forcedOrigin.Value) <= tol;
                bool isHoleCenter = t.KeyPoint && !t.Quadrant && t.Edge != null && t.Edge.Kind == DwgKind.Circle;
                if (isEnd || isOrigin) essential.Add(t);
                else if (isHoleCenter) holes.Add(t);
                else others.Add(t);
            }

            // 孔心数量合适时全部保留；超过 maxTargets - 4 时合并同安装板近邻孔心（< 12mm），保持总视图清爽
            double holeGap = holes.Count > maxTargets - 4 ? Math.Max(0.015, axisSpan * 0.015) : 0;
            foreach (var h in holes.OrderBy(x => x.Coord)) {
                if (holeGap > 0 && essential.Any(r => Math.Abs(r.Coord - h.Coord) < holeGap)) continue;
                essential.Add(h);
            }

            // 2. 剔除紧挨着已保留坐标的型材 T 型槽/壁厚碎线（在框架尺度下，距主边 < 15mm 且无孔的短线大多是槽壁/管壁）
            //    只保留足够长的主梁/安装板边（累计共线长度 ≥ 12% 垂直跨度），按累计共线长度降序择优补齐到 maxTargets
            var scored = new List<KeyValuePair<DwgTarget, double>>();
            foreach (var t in others) {
                if (t.Edge == null || t.Edge.Kind != DwgKind.Line) continue;
                double totalLen = SumParallelLengthAt(edges, !horizontal, t.Coord, tol);
                if (totalLen < perpSpan * 0.12) continue;
                scored.Add(new KeyValuePair<DwgTarget, double>(t, totalLen));
            }

            var result = new List<DwgTarget>(essential);
            double minGap = Math.Max(0.008, axisSpan * 0.015); // 同一主梁壁厚 4~8mm 不重复双标
            foreach (var kv in scored.OrderByDescending(x => x.Value)) {
                if (result.Count >= maxTargets) break;
                double c = kv.Key.Coord;
                // 跳过贴近已有坐标的管壁厚度/T 槽内线
                if (result.Any(r => Math.Abs(r.Coord - c) < minGap)) continue;
                result.Add(kv.Key);
            }
            return result.OrderBy(t => t.Coord).ToList();
        }
    }
}
