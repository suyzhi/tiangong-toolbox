using System;
using System.Collections.Generic;
using System.Linq;

namespace TianGongCadSuite {
    // 车床回转件（轴、销、衬套、圆形法兰）专项视图与尺寸规划纯逻辑（不依赖 CAD / WinForms）。
    //
    // 对齐公司车床图习惯（实测 YF25.07G01-L1-M30-02-029 / 01-007 / SKSD-L01-001-001 / SK26.01-W1197 传动轴）：
    //   1. 主视图：旋转轴线水平放置；有台阶内孔时主视图/剖视图全剖露出内孔台阶；
    //   2. 端面视图：仅当存在端面非同心特征（螺栓孔/PCD、键槽、扁位、端面盲孔）时才补 1 个端面图，纯光轴/纯衬套省略多余端面图；
    //   3. 直径标注：在水平主视图/剖视图直接标各段外径与内径（带 Ø 前缀线性尺寸），外径放上方/右侧、内径放另一侧；
    //   4. 轴向台阶长度：默认用阶梯式线性尺寸（最外总长 + 内层逐段台阶长，主动留出一段不标避免封闭尺寸链），
    //      仅当轴上有 ≥4 个不同轴向位置的径向孔时切换回统一 3D 基准坐标串。

    public sealed class LatheDiameterPair {
        public DwgEdge TopEdge;
        public DwgEdge BottomEdge;
        public double DiameterM;      // 直径（米）
        public double DiameterMm { get { return Math.Round(DiameterM * 1000.0, 2); } }
        public double XMin, XMax;     // 该圆柱段沿水平轴线的起止范围
        public bool IsInnerBore;      // true = 剖视内孔直径，false = 外圆直径
        public bool IsUndercut;       // 是否为窄退刀槽/卡簧槽（轴向宽 ≤ 5.2mm 且比邻段更细）
        public int Count = 1;         // 同规格窄槽/同径段数量（如 2 个相同卡簧槽合并为 2×Ø38）
    }

    public sealed class LatheLinearSpan {
        public DwgTarget From;
        public DwgTarget To;
        public double SpanM { get { return Math.Abs(To.Coord - From.Coord); } }
        public bool IsOverall;        // 最外层总长
        public int Layer;             // 0 = 最贴零件，逐层外推（总长在最外层）
    }

    public static class DrawingLatheRules {
        const double Tol = 5e-5; // 0.05mm

        // 判定是否为车床回转件（排除矩形方块带孔如 02-003 衬套块、02-010 带方耳定位销）
        public static bool IsLathePart(ModelFacts f){
            if (f == null || f.SheetMetal) return false;
            if (f.RevAxis < 0 || f.RevAxis > 2) return false;
            if (f.TotalSurfaceArea <= 1e-8) return false;
            int a = f.RevAxis;
            int u = (a + 1) % 3, v = (a + 2) % 3;
            double su = f.Size[u], sv = f.Size[v];
            double maxCross = Math.Max(su, sv), minCross = Math.Min(su, sv);
            if (maxCross < 0.003) return false;
            // 横截面外框必须近似正方形（圆形外轮廓在两正交轴上等宽，允许 ≤5% 扁位/键槽削边）
            if (Math.Abs(su - sv) > maxCross * 0.05 + 1e-4) return false;
            // 最大同轴外圆柱/圆锥直径必须占满横截面（≥88%），排除方板中间挖个大孔的块件
            if (f.RevMaxOuterDiameter < minCross * 0.88) return false;
            // 同轴回转面（外圆/内孔/圆锥）面积占总面积比例需够高（≥38%，短圆法兰盘端面大所以门槛设 38%）
            double revFrac = f.RevSurfaceArea / f.TotalSurfaceArea;
            if (revFrac < 0.38) return false;
            return true;
        }

        // 车床件选视图：
        //   · 主视图：旋转轴线 Horizontal = RevAxis 水平放置，视线垂直于 RevAxis（若有径向孔则优先正对径向孔开口面）；
        //   · 端面视图（左视图 Right）：仅当存在端面孔（轴沿 RevAxis 的孔，且不是纯通光内孔或需要端面标注）、
        //     端面 PCD 螺栓孔、或端面非圆特征（键槽/扳手扁位）时才添加；
        //   · 俯视图（Below）：仅当存在第二方向径向孔（与主视图视线垂直的径向孔）或需要台阶内孔剖视时添加。
        public static List<DwgViewSpec> ChooseViews(ModelFacts f){
            int revAxis = f.RevAxis;
            int u = (revAxis + 1) % 3, v = (revAxis + 2) % 3;
            // 优先让主视图视线正对径向孔最多的垂直轴（孔在主视图正面可见）
            int sightAxis = f.Holes[u] >= f.Holes[v] ? u : v;
            int vertAxis = sightAxis == u ? v : u;
            int sign = sightAxis == 1 ? -1 : 1;
            if (f.MouthPlus[sightAxis] > f.MouthMinus[sightAxis]) sign = 1;
            else if (f.MouthMinus[sightAxis] > f.MouthPlus[sightAxis]) sign = -1;

            var r = new List<DwgViewSpec>();
            // GB 第一角右方折叠视图（左视图 Right）视线 = -xMain（看主视图左端面）：
            // 若车床轴仅在 +revAxis 一端有端面盲孔（如传动轴01 的 Ø8.5 盲孔），将主视图水平轴取 -1 让孔口朝左，
            // 这样标准左视图正对孔口，100% 可见、绝不出现看实心盲端的虚空孔标注。
            int horzSign = (f.BlindPlus[revAxis] > f.BlindMinus[revAxis]) ? -1 : 1;
            r.Add(new DwgViewSpec {
                Role = DwgViewRole.Main,
                Axis = sightAxis,
                Sign = sign,
                Horizontal = revAxis,
                HorzSign = horzSign,
                Vertical = vertAxis,
                W = f.Size[revAxis],
                H = f.Size[vertAxis]
            });

            // 端面视图（放主视图右侧 = 左视图，视线沿 revAxis）：
            // 有端面孔/螺纹孔、非圆端面特征（键槽/扁位/法兰孔）、或轴向盲孔需要标孔口时才补；纯光轴/纯台阶销直接省略
            bool needEndView = f.Holes[revAxis] > 0 || f.RevHasNonCircularEnd;
            // 若唯一沿 revAxis 的孔就是盲内孔或台阶内孔且已有剖视，依然保留端面图以看清圆轮廓与中心对称（如 02-029 顶面 M6、SKSD-L01-001）
            if (needEndView) {
                r.Add(new DwgViewSpec {
                    Role = DwgViewRole.Right,
                    Axis = revAxis,
                    Horizontal = sightAxis,
                    Vertical = vertAxis,
                    W = f.Size[sightAxis],
                    H = f.Size[vertAxis]
                });
            }

            // 若有沿 vertAxis 的径向孔，或存在内部台阶孔需要全剖 A-A，补下方俯视/剖视图
            bool needSection = f.RevSteppedBore || f.Stepped[revAxis];
            bool needBelow = f.Holes[vertAxis] > 0 || needSection;
            if (needBelow) {
                r.Add(new DwgViewSpec {
                    Role = DwgViewRole.Below,
                    Axis = vertAxis,
                    Horizontal = revAxis,
                    Vertical = sightAxis,
                    W = f.Size[revAxis],
                    H = f.Size[sightAxis],
                    Section = needSection
                });
            }

            r.Add(new DwgViewSpec { Role = DwgViewRole.Iso, Axis = sightAxis, Horizontal = revAxis, Vertical = vertAxis });
            return r;
        }

        // 在水平主视图/剖视图的 2D 边里，按旋转轴线 axisY 配对上下对称的水平线段，识别各段外径 Ø 与内径 Ø。
        public static List<LatheDiameterPair> ExtractDiameters(IList<DwgEdge> edges, double axisY, bool isSection, double maxOuterDiaM){
            var pairs = new List<LatheDiameterPair>();
            if (edges == null || edges.Count == 0) return pairs;
            var hLines = edges.Where(e => e.Kind == DwgKind.Line && e.IsHorizontal && e.Length >= 0.0005).ToList();
            var used = new bool[hLines.Count];

            for (int i = 0; i < hLines.Count; i++) {
                if (used[i]) continue;
                var a = hLines[i];
                double dyA = a.Y1 - axisY;
                if (Math.Abs(dyA) < 0.0005) continue;
                double aLo = Math.Min(a.X1, a.X2), aHi = Math.Max(a.X1, a.X2);
                int bestJ = -1;
                double bestErr = double.MaxValue;
                for (int j = i + 1; j < hLines.Count; j++) {
                    if (used[j]) continue;
                    var b = hLines[j];
                    double dyB = b.Y1 - axisY;
                    // 必须分布在轴线异侧且到轴线距离相等
                    if (dyA * dyB >= 0) continue;
                    double rErr = Math.Abs(Math.Abs(dyA) - Math.Abs(dyB));
                    if (rErr > Tol * 2) continue;
                    double bLo = Math.Min(b.X1, b.X2), bHi = Math.Max(b.X1, b.X2);
                    // X 范围高度重合
                    double ov = Math.Min(aHi, bHi) - Math.Max(aLo, bLo);
                    if (ov < Math.Min(a.Length, b.Length) * 0.75) continue;
                    double err = rErr + Math.Abs(aLo - bLo) + Math.Abs(aHi - bHi);
                    if (err < bestErr) { bestErr = err; bestJ = j; }
                }
                if (bestJ < 0) continue;
                used[i] = true; used[bestJ] = true;
                var top = dyA > 0 ? a : hLines[bestJ];
                var bot = dyA > 0 ? hLines[bestJ] : a;
                double dia = Math.Abs(top.Y1 - bot.Y1);
                if (dia < 0.001) continue;
                double xMin = Math.Min(Math.Min(top.X1, top.X2), Math.Min(bot.X1, bot.X2));
                double xMax = Math.Max(Math.Max(top.X1, top.X2), Math.Max(bot.X1, bot.X2));
                pairs.Add(new LatheDiameterPair {
                    TopEdge = top, BottomEdge = bot,
                    DiameterM = dia, XMin = xMin, XMax = xMax
                });
            }

            // 按相同 X 区间去重并判别内外径：同一 X 区间内最外一对是外径，更小的是剖视内孔直径
            var dedup = new List<LatheDiameterPair>();
            foreach (var p in pairs.OrderByDescending(x => x.DiameterM).ThenByDescending(x => x.XMax - x.XMin)) {
                if (dedup.Any(d => Math.Abs(d.DiameterM - p.DiameterM) < Tol * 2 && Math.Abs(d.XMin - p.XMin) < Tol * 4 && Math.Abs(d.XMax - p.XMax) < Tol * 4))
                    continue;
                // 同一段轴向上已存在明显更大的外径：
                //   · 剖视图（isSection=true）里是内孔直径；
                //   · 非剖视外视图（isSection=false）里严格落在外轮廓内部的一对水平线是键槽/铣扁/径向孔壁，绝不是外圆柱直径！
                bool insideBigger = dedup.Any(d => d.DiameterM > p.DiameterM + 0.0005
                    && Math.Min(d.XMax, p.XMax) - Math.Max(d.XMin, p.XMin) > (p.XMax - p.XMin) * 0.5);
                if (!isSection && insideBigger) continue;
                p.IsInnerBore = isSection && insideBigger;
                // 窄退刀槽判别：轴向宽 ≤ 5mm 且不是最大外径、左右紧邻有更大直径
                p.IsUndercut = !p.IsInnerBore && (p.XMax - p.XMin) <= 0.0052 && p.DiameterM < maxOuterDiaM - 0.0004;
                dedup.Add(p);
            }

            // 同直径不重复标（同一轴上两段相同外径保留更长那段代表；多个相同卡簧槽/退刀槽累计 Count），按从左到右排序
            var finalList = new List<LatheDiameterPair>();
            foreach (var p in dedup.OrderByDescending(x => x.XMax - x.XMin)) {
                var dup = finalList.FirstOrDefault(x => x.IsInnerBore == p.IsInnerBore && Math.Abs(x.DiameterM - p.DiameterM) < Tol * 2);
                if (dup != null) {
                    if (dup.IsUndercut && p.IsUndercut) dup.Count++;
                    continue;
                }
                finalList.Add(p);
            }
            return finalList.OrderBy(p => p.XMin).ToList();
        }

        // 规划车床件轴向阶梯式线性尺寸（不封闭尺寸链）：
        //   · targets 已按 X 坐标升序排列（过滤掉倒角线）；
        //   · 若仅有 2 个点：只标 1 个总长；
        //   · 若有 k >= 3 个点（x0..x_{k-1}）：
        //     - 最外层标总长 [x0, x_{k-1}]；
        //     - 内层标 k-2 个相邻段（主动省掉最长的那一段中间光杆作开放环，符合机加工艺不封闭链规矩！），
        //       相邻不重叠的段共占 Layer 0，避免尺寸层数堆得太高。
        public static List<LatheLinearSpan> PlanSteppedAxial(IList<DwgTarget> sortedTargets){
            var spans = new List<LatheLinearSpan>();
            if (sortedTargets == null || sortedTargets.Count < 2) return spans;
            var ts = sortedTargets.OrderBy(t => t.Coord).ToList();
            int n = ts.Count;
            if (n == 2) {
                spans.Add(new LatheLinearSpan { From = ts[0], To = ts[1], IsOverall = true, Layer = 0 });
                return spans;
            }
            // 找最长一段相邻间距作为留空的开放环（若全等则留最后一段）
            int skipSeg = n - 2;
            double bestLen = -1;
            for (int i = 0; i < n - 1; i++) {
                double len = ts[i + 1].Coord - ts[i].Coord;
                if (len > bestLen + 1e-9) { bestLen = len; skipSeg = i; }
            }
            for (int i = 0; i < n - 1; i++) {
                if (i == skipSeg) continue;
                spans.Add(new LatheLinearSpan { From = ts[i], To = ts[i + 1], IsOverall = false, Layer = 0 });
            }
            spans.Add(new LatheLinearSpan { From = ts[0], To = ts[n - 1], IsOverall = true, Layer = 1 });
            return spans;
        }
    }
}
