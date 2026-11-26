using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace TianGongCadSuite {
    // 配孔检查。规则轻量，对齐 ICAN 的做法：按孔径范围筛选 + 按轴向间距配对。
    public static class HoleCheck {
        // 当前判定口径（窗口里改了会换成用户的那一份；测试里可以直接换）
        public static HoleCheckOptions Options = new HoleCheckOptions();
        public static double MinDiameterMm { get { return Options.MinDiameterMm; } }     // 小于这个直径不当作连接孔
        public static double MaxDiameterMm { get { return Options.MaxDiameterMm; } }
        public static double AxisAngleTolDeg { get { return Options.AxisAngleTolDeg; } } // 轴线方向容差
        public static double AxisOffsetTolMm { get { return Options.AxisOffsetTolMm; } } // 同轴偏移容差（超过即"孔偏了"）
        public static double CoaxialSpanTolMm { get { return Options.CoaxialSpanTolMm; } } // 沿轴线的间隙不超过这个才认为挨着

        // 两条圆边是否属于"同一个孔"：同一零件、同一轴线、同一直径。
        // 通孔在零件上有上下两条圆边，不去重会被当成两个孔。
        public static bool SameHoleLine(HoleRecord a, HoleRecord b, double diameterTolMm = 0.01){
            if (a == null || b == null) return false;
            if (a.PartName != b.PartName) return false;
            if (Math.Abs(a.DiameterMm - b.DiameterMm) > diameterTolMm) return false;
            return SameLine(a, b, Math.Cos(AxisAngleTolDeg * Math.PI / 180.0));
        }

        // 方向与网格值类型键：消除原字符串拼接 + Split + int.Parse 堆分配
        struct Int3Key : IEquatable<Int3Key> {
            public readonly int X, Y, Z;
            public Int3Key(int x, int y, int z){ X = x; Y = y; Z = z; }
            public bool Equals(Int3Key o){ return X == o.X && Y == o.Y && Z == o.Z; }
            public override bool Equals(object obj){ return obj is Int3Key && Equals((Int3Key)obj); }
            public override int GetHashCode(){ unchecked { int h = X; h = (h * 397) ^ Y; h = (h * 397) ^ Z; return h; } }
        }
        struct Int2Key : IEquatable<Int2Key> {
            public readonly int U, V;
            public Int2Key(int u, int v){ U = u; V = v; }
            public bool Equals(Int2Key o){ return U == o.U && V == o.V; }
            public override bool Equals(object obj){ return obj is Int2Key && Equals((Int2Key)obj); }
            public override int GetHashCode(){ unchecked { return (U * 397) ^ V; } }
        }

        // 按"同一条轴线"分组。方向相同，且横向偏移不超过两孔中较小的半径（一个孔的轴线还落在
        // 另一个孔里），就算同组 —— 分组容差必须比 AxisOffsetTolMm 宽：两者相同的话，偏了
        // 0.5mm 的两个孔根本进不了同一组，只会变成两个（默认隐藏的）孤孔，"孔偏了"永远报不出来。
        // 组内是否真的偏了，由 Run 再用 AxisOffsetTolMm 判。
        //
        // 1. 方向步长按 AxisAngleTolDeg 动态计算（dirStep > 2*sin(tol/2)），保证任意容差（0.1°~29°）内
        //    方向差 <= tol 的两轴一定落在相邻 ±1 桶内，不再出现 0.76°~2° 同轴孔被硬编码 0.0087 步长拆开。
        // 2. 占装配 95%+ 的三正交主轴（±X/±Y/±Z，偏差 <= 1e-7）额外按垂直平面的 2D 空间网格分桶，
        //    避免几千个 Z 向孔全部落进同一方向桶退化成 O(n²)。斜孔仍会与容差内的主轴组交叉比对，结果严格等价。
        public static List<List<HoleRecord>> GroupByAxis(IList<HoleRecord> holes){
            var groups = new List<List<HoleRecord>>();
            if (holes == null || holes.Count == 0) return groups;
            double tolRad = AxisAngleTolDeg * Math.PI / 180.0;
            double cosTol = Math.Cos(tolRad);
            // 弦长 2*sin(tol/2) < dirStep 保证每一分量差 < dirStep，从而 |Round(a/s) - Round(b/s)| <= 1
            double dirStep = Math.Max(0.01, 2.0 * Math.Sin(tolRad * 0.5) * 1.05);
            double maxDia = 0;
            foreach (var h in holes) if (h != null && h.DiameterMm > maxDia) maxDia = h.DiameterMm;
            // 正交主轴 2D 网格步长必须大于最大可能同组横向偏心，保证任意同组主轴孔必落在 3×3 邻格内
            double cellM = Math.Max(0.05, (Math.Max(AxisOffsetTolMm, maxDia * 0.5) + 1.0) / 1000.0);

            var dirBuckets = new Dictionary<Int3Key, List<List<HoleRecord>>>();
            var cardinalGrids = new Dictionary<Int2Key, List<List<HoleRecord>>>[3];
            var cardinalLists = new List<List<HoleRecord>>[3];
            for (int k = 0; k < 3; k++) {
                cardinalGrids[k] = new Dictionary<Int2Key, List<List<HoleRecord>>>();
                cardinalLists[k] = new List<List<HoleRecord>>();
            }
            // 记录已加入某正交列表或方向桶的组，避免跨主轴/斜孔混入时重复索引
            var inCardinal = new HashSet<List<HoleRecord>>[3];
            for (int k = 0; k < 3; k++) inCardinal[k] = new HashSet<List<HoleRecord>>();

            foreach (var h in holes) {
                if (h == null) continue;
                V3 u;
                try { u = h.Axis.Unit(); } catch { continue; }
                if (!u.Finite) continue;
                int card = CardinalAxis(u);
                List<HoleRecord> hit = null;

                if (card >= 0) {
                    // 先查同正交主轴的 3×3 空间邻格（O(1)）
                    int cu, cv;
                    CardinalCell(h.Center, card, cellM, out cu, out cv);
                    var grid = cardinalGrids[card];
                    for (int du = -1; du <= 1 && hit == null; du++) {
                        for (int dv = -1; dv <= 1 && hit == null; dv++) {
                            List<List<HoleRecord>> cand;
                            if (!grid.TryGetValue(new Int2Key(cu + du, cv + dv), out cand)) continue;
                            hit = FindMatchingGroup(cand, h, cosTol);
                        }
                    }
                    // 再查相邻方向桶里的斜孔组（数量极少，兼容先斜后正的到达顺序）
                    if (hit == null && dirBuckets.Count > 0)
                        hit = SearchDirBuckets(dirBuckets, u, dirStep, h, cosTol);
                } else {
                    // 斜孔：查 ±u 方向桶及其 27 邻桶
                    if (dirBuckets.Count > 0)
                        hit = SearchDirBuckets(dirBuckets, u, dirStep, h, cosTol);
                    // 若斜孔轴线与某个正交主轴夹角在容差内，也与该主轴组比对（兼容先正后斜）
                    if (hit == null) {
                        for (int k = 0; k < 3 && hit == null; k++) {
                            double comp = k == 0 ? Math.Abs(u.X) : (k == 1 ? Math.Abs(u.Y) : Math.Abs(u.Z));
                            if (comp < cosTol || cardinalLists[k].Count == 0) continue;
                            hit = FindMatchingGroup(cardinalLists[k], h, cosTol);
                        }
                    }
                }

                if (hit == null) {
                    hit = new List<HoleRecord>();
                    groups.Add(hit);
                }
                hit.Add(h);

                if (card >= 0) {
                    int cu, cv;
                    CardinalCell(h.Center, card, cellM, out cu, out cv);
                    var key2 = new Int2Key(cu, cv);
                    List<List<HoleRecord>> cell;
                    if (!cardinalGrids[card].TryGetValue(key2, out cell)) { cell = new List<List<HoleRecord>>(); cardinalGrids[card][key2] = cell; }
                    if (!cell.Contains(hit)) cell.Add(hit);
                    if (inCardinal[card].Add(hit)) cardinalLists[card].Add(hit);
                } else {
                    var dk = QuantizeDir(u, dirStep);
                    List<List<HoleRecord>> cand;
                    if (!dirBuckets.TryGetValue(dk, out cand)) { cand = new List<List<HoleRecord>>(); dirBuckets[dk] = cand; }
                    if (!cand.Contains(hit)) cand.Add(hit);
                }
            }
            return groups;
        }

        // 判断单位向量是否几乎与 ±X(0) / ±Y(1) / ±Z(2) 重合（偏差 <= 1e-7，即使相距 100m 投影漂移也仅 0.01mm）
        static int CardinalAxis(V3 u){
            const double eps = 1e-7;
            if (Math.Abs(u.Y) <= eps && Math.Abs(u.Z) <= eps) return 0;
            if (Math.Abs(u.X) <= eps && Math.Abs(u.Z) <= eps) return 1;
            if (Math.Abs(u.X) <= eps && Math.Abs(u.Y) <= eps) return 2;
            return -1;
        }
        static void CardinalCell(V3 c, int axis, double cellM, out int cu, out int cv){
            double u = axis == 0 ? c.Y : c.X;
            double v = axis == 2 ? c.Y : c.Z;
            cu = (int)Math.Floor(u / cellM);
            cv = (int)Math.Floor(v / cellM);
        }
        static Int3Key QuantizeDir(V3 axis, double step){
            return new Int3Key((int)Math.Round(axis.X / step), (int)Math.Round(axis.Y / step), (int)Math.Round(axis.Z / step));
        }
        static List<HoleRecord> SearchDirBuckets(Dictionary<Int3Key, List<List<HoleRecord>>> buckets, V3 u, double step, HoleRecord h, double cosTol){
            var k1 = QuantizeDir(u, step);
            var hit = SearchNeighbours(buckets, k1, h, cosTol);
            if (hit != null) return hit;
            var k2 = QuantizeDir(u * -1.0, step);
            if (!k2.Equals(k1)) hit = SearchNeighbours(buckets, k2, h, cosTol);
            return hit;
        }
        static List<HoleRecord> SearchNeighbours(Dictionary<Int3Key, List<List<HoleRecord>>> buckets, Int3Key key, HoleRecord h, double cosTol){
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    for (int dz = -1; dz <= 1; dz++) {
                        List<List<HoleRecord>> cand;
                        if (!buckets.TryGetValue(new Int3Key(key.X + dx, key.Y + dy, key.Z + dz), out cand)) continue;
                        var hit = FindMatchingGroup(cand, h, cosTol);
                        if (hit != null) return hit;
                    }
            return null;
        }
        static List<HoleRecord> FindMatchingGroup(List<List<HoleRecord>> cand, HoleRecord h, double cosTol){
            for (int i = 0; i < cand.Count; i++) {
                var g = cand[i];
                // 必须跟组内**每一个**成员比，不能只比 g[0]：
                // 一个圆柱沉孔在实体上就是"同轴两个直径"（Φ5.5 过孔 + Φ11 沉孔），
                // 采集顺序决定了 g[0] 是哪一个。只比 g[0] 的话，另一条直径会因为
                // "跟 g[0] 横向差 0.2mm 以上"（其实是同一轴线的另一个截面）而被拆成
                // 两条假轴线 —— 于是沉孔的假阳性又从这里绕回来了。
                for (int j = 0; j < g.Count; j++) {
                    if (SameLine(g[j], h, cosTol, GroupOffsetMm(g[j], h))) return g;
                }
            }
            return null;
        }

        // 两个孔是不是同一条轴线：方向相同或**相反**（相差 180° 也算），且横向偏移在容差内。
        // 为什么必须把"相反"算进来：圆柱边的轴线取自面/边的几何法向，同一个物理孔在
        // 装配里读出来可能是 (0,0,-1) 也可能是 (0,0,1)（实测同一块板的沉孔与过孔就是
        // 这两个方向）。按 |dot| 比较即可，否则一条轴线会被拆成两条，沉孔的假阳性
        // 又会从这里绕回来。
        static bool SameLine(HoleRecord a, HoleRecord b, double cosTol){
            return SameLine(a, b, cosTol, AxisOffsetTolMm);
        }
        static bool SameLine(HoleRecord a, HoleRecord b, double cosTol, double offsetTolMm){
            if (a == null || b == null) return false;
            if (Math.Abs(a.Axis.Dot(b.Axis)) < cosTol) return false;
            return LateralMm(a, b) <= offsetTolMm;
        }
        // b 的孔心离 a 的轴线有多远（毫米）
        static double LateralMm(HoleRecord a, HoleRecord b){
            var d = b.Center - a.Center;
            return (d - a.Axis * d.Dot(a.Axis)).Length * 1000.0;
        }
        static double GroupOffsetMm(HoleRecord a, HoleRecord b){
            return Math.Max(AxisOffsetTolMm, Math.Min(a.DiameterMm, b.DiameterMm) / 2.0);
        }

        // 两个直径是否构成合法的"配做"关系：底孔+过孔（同规格），或过孔+沉孔。
        public static bool IsValidPair(double aMm, double bMm, out string why){
            why = "";
            // 几乎一样大（Φ6.6 与 Φ6.65）：同一把钻头打的孔。原来 Φ6.65 先被认成 M8 螺纹内小径，报「M6 与 M8 规格不同」
            if (Math.Abs(aMm - bMm) <= SameDrillMm) return true;
            // 有没有一个螺纹规格能同时解释两个孔（螺纹孔 / 过孔 / 沉头让位孔）。0.10.1 焊接台车实测：
            // 原来只认表里的两档过孔，Φ6（M5 粗装配过孔，HIWIN 导轨的标准孔）配 M5 螺纹孔 Φ4.13 一条导轨报 20 条「配错孔」；
            // Φ7（M6 粗装配过孔）先被认成 M8 底孔，正确的 M6 连接报「M8 与 M6」
            // 正好等于某个规格螺纹孔直径的（Φ17.5 = M20 底孔、Φ4.13 = M5 内小径）优先按螺纹孔读，
            // 除非另一个孔与它在更小规格上构成「标准过孔 + 沉孔让位孔」（如 M16 Φ17.5+Φ26）或「标准过孔 + 同规格过孔」（如 M4 Φ4.5+Φ5.0）。
            // 同时仍拦截「M20 螺母 Φ17.5 配非标准 Φ19」被误读成 M16 两个过孔。
            bool exA = ExactTapped(aMm), exB = ExactTapped(bMm);
            if (exA != exB) {
                double tapped = exA ? aMm : bMm, other = exA ? bMm : aMm;
                foreach (var r in HoleMatcher.Table) {
                    if (IsTapped(r, tapped) && IsClearance(r, other)) return true;
                    // tapped 同时也是 r 的标准过孔，other 是 r 的沉孔让位孔（如 M16 Φ17.5 + Φ26、M20 Φ21 + Φ33）
                    if (IsStandardClearance(r, tapped) && IsCounterboreRelief(r, other)) return true;
                    // other 是 r 的标准过孔且 tapped 也是 r 的过孔（如 M4 标准过孔 Φ4.5 + 粗装配过孔 Φ5.0）
                    if (IsStandardClearance(r, other) && IsClearance(r, tapped)) return true;
                }
            } else {
                foreach (var r in HoleMatcher.Table) {
                    if (Fits(r, aMm, bMm) || Fits(r, bMm, aMm)) return true;
                }
            }
            // 任一方能匹配到标准规格，另一方应能匹配到同规格的另一种孔
            var ma = HoleMatcher.Match(aMm);
            var mb = HoleMatcher.Match(bMm);
            if (ma.HasRow && mb.HasRow) {
                if (ma.Row.Size == mb.Row.Size) return true;
                why = "两个孔的规格不同（" + ma.Row.Size + " 与 " + mb.Row.Size + "）";
                return false;
            }
            // 有一个匹配不上标准表：只要直径差不超过 1mm，按自定义配孔放过
            if (Math.Abs(aMm - bMm) <= 1.0) return true;
            why = "直径相差 " + Math.Abs(aMm - bMm).ToString("0.##", CultureInfo.InvariantCulture) + "mm，且至少一个不在标准螺纹表内";
            return false;
        }

        const double SameDrillMm = 0.1;
        // 细牙螺距（常见的几档）：M20×1.5 螺母的内小径 18.38、M12×1 的 10.92 这种原来全按「不在标准表」报配错孔
        static readonly double[][] FinePitches = {
            new[]{ 8, 1.0 }, new[]{ 10, 1.25 }, new[]{ 10, 1.0 }, new[]{ 12, 1.5 }, new[]{ 12, 1.25 }, new[]{ 12, 1.0 }, new[]{ 14, 1.5 },
            new[]{ 16, 1.5 }, new[]{ 20, 1.5 }, new[]{ 20, 2.0 }, new[]{ 24, 2.0 }, new[]{ 24, 1.5 }, new[]{ 30, 2.0 }
        };
        static double Nominal(ThreadRow r){
            double n; return double.TryParse(r.Size.TrimStart('M', 'm'), NumberStyles.Float, CultureInfo.InvariantCulture, out n) ? n : 0;
        }
        // 是否是规格 r 的标准过孔（精配/中配 ±0.1mm）
        static bool IsStandardClearance(ThreadRow r, double d){
            return Math.Abs(d - r.ClearanceClose) <= 0.1 || Math.Abs(d - r.ClearanceMid) <= 0.1;
        }
        static bool IsAnyStandardClearance(double d){
            foreach (var r in HoleMatcher.Table) if (Math.Abs(d - r.ClearanceClose) <= 0.05 || Math.Abs(d - r.ClearanceMid) <= 0.05) return true;
            return false;
        }
        static bool IsCounterboreRelief(ThreadRow r, double d){
            return d >= r.CounterboreDia - HoleMatcher.Tolerance && d <= r.CounterboreDia + 1.0;
        }
        // 这个直径是不是规格 r 的螺纹孔（底孔 / 内小径，粗牙按表，细牙按螺距算）。
        // 容差不能用 HoleMatcher.Tolerance(0.35mm)：相邻规格的过孔与底孔间距常只有 0.1~0.3mm（如 Φ3.4 与 M4 底孔 3.3、
        // Φ4.5 与 M5 底孔 4.2、Φ5.3 与 M6 底孔 5.0、Φ6.4/6.6 与 M8 内小径 6.647、Φ10.5 与 M12 底孔 10.2），
        // 0.35mm 会把相邻规格过孔错配全部误放过。真实螺纹底孔落在 [MinorDia-0.04, TapDrill+0.05] 区间内。
        public static bool IsTapped(ThreadRow r, double d){
            if (d >= r.MinorDia - 0.04 && d <= r.TapDrill + 0.05) return true;
            double n = Nominal(r);
            foreach (var f in FinePitches) {
                if (f[0] != n) continue;
                if (Math.Abs(d - (n - 1.0825 * f[1])) <= 0.06) return true;
                // 细牙底孔 n-p 可能正好撞上更小规格的标准过孔（如 M10×1=9.0 撞 M8 过孔、M12×1.5=10.5 撞 M10 精配、M12×1=11.0 撞 M10 过孔）
                if (Math.Abs(d - (n - f[1])) <= 0.05 && !IsAnyStandardClearance(d)) return true;
            }
            return false;
        }
        // 直径正好（±0.02mm）是某个规格的攻丝底孔 / 螺纹内小径（粗牙按表，细牙按螺距算）
        public static bool ExactTapped(double d){
            foreach (var r in HoleMatcher.Table) {
                if (Math.Abs(d - r.TapDrill) <= 0.02 || Math.Abs(d - r.MinorDia) <= 0.02) return true;
                double n = Nominal(r);
                foreach (var f in FinePitches) if (f[0] == n && Math.Abs(d - (n - 1.0825 * f[1])) <= 0.02) return true;
            }
            return false;
        }
        // 真正大于公称直径的过孔（不含 d ≈ 公称直径 的铰制/配合孔）
        static bool IsRealClearance(ThreadRow r, double d){
            double n = Nominal(r);
            if (n <= 0) return false;
            return d >= n + Math.Max(0.2, 0.05 * n) - 0.01 && d <= n * 1.25 + 0.2;
        }
        // 这个直径能不能让规格 r 的螺钉穿过：比公称直径大、不超过粗装配过孔（≈ 公称 ×1.25，GB/T 5277 粗装配 M5 5.8、M6 7、M8 10……）；
        // M12 以上的铰制孔 / 配合孔（孔径 = 公称直径）也算
        public static bool IsClearance(ThreadRow r, double d){
            if (IsRealClearance(r, d)) return true;
            double n = Nominal(r);
            return n >= 12 && Math.Abs(d - n) <= 0.05;
        }
        // small 和 big 在规格 r 上配得上：螺纹孔 + 过孔、过孔 + 过孔（螺栓 + 螺母），
        // 或者 small 是过孔、big 是螺钉头的让位孔（≈ 沉头直径：沉孔分在两块板上做）。螺纹孔直接配沉头直径的孔不算（中间少了过孔）
        static bool Fits(ThreadRow r, double small, double big){
            bool smallClear = IsClearance(r, small);
            if (!smallClear && !IsTapped(r, small)) return false;
            if (IsClearance(r, big)) return true;
            // 让位孔只配真正的过孔（Φ12 正好等于 M12 公称直径的配合孔不算：Φ12 + Φ20 照样报）
            return IsRealClearance(r, small) && IsCounterboreRelief(r, big);
        }

        // 这一串孔是不是一个完整的螺纹连接（同一规格：一个零件上是螺纹孔、另一个零件上是真正过孔）。
        // 完整的连接，螺钉到螺纹孔就到头了：轴线再往外穿过的零件（螺纹孔下面垫着的底板、螺钉头上面盖着的盖板）不需要孔，
        // 原来照样报「漏打孔」—— 焊接台车上 17 号板的 M6 螺纹孔下面是 002 底板，一个螺钉报一条。
        // 过孔侧必须用 IsRealClearance（排除 d ≈ 公称直径 的配合孔），防止 {11.0, 12.0} 或 {17.5, 20.0} 误判成完整连接吞掉底板漏孔。
        public static bool CompleteJoint(IList<HoleRecord> chain){
            if (chain == null || chain.Count < 2) return false;
            var minByPart = chain.GroupBy(h => h.PartName).Select(g => g.Min(x => x.DiameterMm)).ToList();
            if (minByPart.Count < 2) return false;
            foreach (var r in HoleMatcher.Table) {
                int tapped = 0, clear = 0;
                foreach (var d in minByPart) { if (IsTapped(r, d)) tapped++; else if (IsRealClearance(r, d)) clear++; }
                if (tapped > 0 && clear > 0) return true;
            }
            return false;
        }

        // 一组同轴孔按"沿轴线挨不挨着"再切成几段（链）。
        //
        // 原来 CoaxialSpanTolMm 定义了（注释写着"轴向距离小于这个才认为是一对"），却从来没用上：
        // 同一条直线上隔着几百毫米、根本不相干的两个孔被当成一对来比规格/同轴度 —— 误报"配错孔""孔偏了"；
        // 反过来，A、C 两块板有孔、中间夹着的 B 板漏打了，A、C 被当成一对"配好了"，B 的漏孔永远报不出来。
        // 现在：每个零件在这条轴线上占一段区间（它所有孔记录的轴向范围的并），按区间排序，
        // 相邻两段的间隙 ≤ CoaxialSpanTolMm 才连成一链；只有同一链里的孔才互相配对。
        public static List<List<HoleRecord>> SplitByGap(IList<HoleRecord> group, double gapMm){
            var chains = new List<List<HoleRecord>>();
            if (group == null || group.Count == 0) return chains;
            V3 o, a;
            if (!Frame(group, out o, out a)) { chains.Add(new List<HoleRecord>(group)); return chains; }
            var spans = new List<KeyValuePair<string, double[]>>();
            var byPart = new Dictionary<string, List<HoleRecord>>(StringComparer.Ordinal);
            foreach (var h in group) {
                double lo, hi; Span(h, o, a, out lo, out hi);
                List<HoleRecord> l;
                if (!byPart.TryGetValue(h.PartName, out l)) { l = new List<HoleRecord>(); byPart[h.PartName] = l; spans.Add(new KeyValuePair<string, double[]>(h.PartName, new[]{ lo, hi })); }
                else {
                    foreach (var kv in spans) if (kv.Key == h.PartName) { kv.Value[0] = Math.Min(kv.Value[0], lo); kv.Value[1] = Math.Max(kv.Value[1], hi); }
                }
                l.Add(h);
            }
            spans.Sort((x, y) => x.Value[0].CompareTo(y.Value[0]));
            double gap = gapMm / 1000.0;
            List<HoleRecord> cur = null; double end = double.NegativeInfinity;
            foreach (var kv in spans) {
                if (cur == null || kv.Value[0] > end + gap) { cur = new List<HoleRecord>(); chains.Add(cur); end = kv.Value[1]; }
                else end = Math.Max(end, kv.Value[1]);
                cur.AddRange(byPart[kv.Key]);
            }
            return chains;
        }

        // 一组孔的公共参考：原点取第一个孔的孔心，方向取它的轴线
        static bool Frame(IList<HoleRecord> g, out V3 o, out V3 a){
            o = g[0].Center; a = new V3(0, 0, 1);
            try { a = g[0].Axis.Unit(); return true; } catch { return false; }
        }
        // 一个孔记录在公共轴线上占的区间（米）。记录的轴线可能和公共方向相反（实测同一个孔两种方向都读到过）。
        static void Span(HoleRecord h, V3 o, V3 a, out double lo, out double hi){
            double s = h.Axis.Dot(a) >= 0 ? 1.0 : -1.0;
            double t0 = (h.Center - o).Dot(a);
            double x1 = t0 + s * h.AxialMin, x2 = t0 + s * h.AxialMax;
            lo = Math.Min(x1, x2); hi = Math.Max(x1, x2);
        }
        // 两个零件的孔在链轴线上的重叠长度（米）；不重叠 / 只是首尾相接返回 ≤ 0
        static double Overlap(IList<HoleRecord> a, IList<HoleRecord> b, V3 o, V3 axis){
            double alo, ahi, blo, bhi; Range(a, o, axis, out alo, out ahi); Range(b, o, axis, out blo, out bhi);
            return Math.Min(ahi, bhi) - Math.Max(alo, blo);
        }
        static void Range(IList<HoleRecord> holes, V3 o, V3 axis, out double lo, out double hi){
            lo = double.PositiveInfinity; hi = double.NegativeInfinity;
            foreach (var h in holes) { double l, u; Span(h, o, axis, out l, out u); lo = Math.Min(lo, l); hi = Math.Max(hi, u); }
        }
        static void ChainSpan(IList<HoleRecord> chain, out V3 o, out V3 a, out double lo, out double hi){
            Frame(chain, out o, out a);
            lo = double.PositiveInfinity; hi = double.NegativeInfinity;
            foreach (var h in chain) { double l, u; Span(h, o, a, out l, out u); lo = Math.Min(lo, l); hi = Math.Max(hi, u); }
        }

        // 零件 part 在这个孔的轴线上有没有一根同轴的轴/销/螺栓杆（方向差 < 2°，横向偏差不超过孔半径）。
        // 螺栓杆先按零件名分好（ShaftsByPart）：原来每串孔 × 每个零件都把全装配的螺栓杆扫一遍（零件数 × 螺栓杆数），
        // SolidWorks 转来的 ST064（8113 组孔、几千个导入件）光这一步配对就要将近两个小时，CAD 调用只占几秒
        static Dictionary<string, List<HoleRecord>> ShaftsByPart(IList<HoleRecord> shafts){
            var map = new Dictionary<string, List<HoleRecord>>(StringComparer.Ordinal);
            if (shafts == null) return map;
            foreach (var s in shafts) {
                if (s == null) continue;
                List<HoleRecord> l;
                if (!map.TryGetValue(s.PartName ?? "", out l)) { l = new List<HoleRecord>(); map[s.PartName ?? ""] = l; }
                l.Add(s);
            }
            return map;
        }
        // 零件 part 在这个孔的轴线上有没有一根同轴的轴/销/螺栓杆：
        // ① 轴线平行（< AxisAngleTolDeg）且横向偏差不超过孔半径；
        // ② 轴径与孔径相称（防止目标件自身 Φ16/Φ20 凸台吞掉 Φ6.6 孔的真实漏打孔）；
        // ③ 轴向区间确实落在孔附近（防止 300mm 外的同轴销钉或目标件背面凸台误豁免）。
        static bool HasCoaxialShaft(Dictionary<string, List<HoleRecord>> shaftsByPart, string part, HoleRecord hole, double minHoleDiaMm, V3 o, V3 a, double lo, double hi, double spanM){
            List<HoleRecord> shafts;
            if (shaftsByPart == null || hole == null || part == null || !shaftsByPart.TryGetValue(part, out shafts)) return false;
            double cosTol = Math.Cos(AxisAngleTolDeg * Math.PI / 180.0);
            double refDia = minHoleDiaMm > 0 ? minHoleDiaMm : hole.DiameterMm;
            double latTol = Math.Max(AxisOffsetTolMm, refDia / 2.0);
            double minDia = refDia * 0.35;
            double maxDia = refDia * 1.25 + 0.5;
            double spanTol = Math.Max(spanM, 0.005);
            double pointTol = Math.Max(0.025, Math.Max(spanM, hi - lo) * 2.0);
            for (int i = 0; i < shafts.Count; i++) {
                var s = shafts[i];
                if (s.DiameterMm < minDia || s.DiameterMm > maxDia) continue;
                if (Math.Abs(s.Axis.Dot(hole.Axis)) < cosTol) continue;
                if (LateralMm(hole, s) > latTol) continue;
                double sLo, sHi;
                Span(s, o, a, out sLo, out sHi);
                if (sHi - sLo > 1e-4) {
                    if (sHi < lo - spanTol || sLo > hi + spanTol) continue;
                } else {
                    if (sHi < lo - pointTol || sLo > hi + pointTol) continue;
                }
                return true;
            }
            return false;
        }

        // 参与检查的孔（3–25mm）
        public static List<HoleRecord> Relevant(IList<HoleRecord> holes){
            if (holes == null) return new List<HoleRecord>();
            return holes.Where(h => h != null && h.DiameterMm >= MinDiameterMm && h.DiameterMm <= MaxDiameterMm).ToList();
        }
        // 一共多少组"挨着的同轴孔"（摘要里那个"N 组同轴孔"）。原来界面拿**没筛过直径**的孔去分组，
        // 和 Run 实际检查的组数对不上。
        public static int CountGroups(IList<HoleRecord> holes){
            int n = 0;
            foreach (var g in GroupByAxis(Relevant(holes))) n += SplitByGap(g, CoaxialSpanTolMm).Count;
            return n;
        }

        // 主检查。holes 为全装配的孔；rayHitsPart 用于判"漏打孔"（可为 null 则跳过该项）。
        // reportUnpaired：是否把"只有一个零件有孔、且轴线没穿过任何其他零件"的孤孔也报出来。
        // 默认不报——真实装配里绝大多数孔本来就是孤孔，全报会淹没有用信息。
        // allParts：装配里所有零件的名字。判"漏打孔"必须知道有哪些零件——
        // 只遍历"有孔的零件"永远发现不了漏孔（漏的那个零件本来就没孔）。
        // bodies：预建好的「零件名 -> 射线目标」表（AutoHoleWriter.OccurrenceBodies.Build）。
        // 给了它就按"孔附近 CoaxialSpanTolMm 以内"判命中（见 PartRayTarget）；为 null 时退回 rayHitsPart（不限距离）。
        // shafts：装配里的轴 / 销 / 螺栓杆（凸台外圆，CollectAssemblyHoles 另外收集）。孔的轴线穿过一根和它同轴的
        // 螺栓杆是天经地义的 —— 不能因此报"螺栓上漏打孔"（原来每个装了螺栓的孔都会这么误报一次）。
        public static List<HoleIssue> Run(IList<HoleRecord> holes, Func<HoleRecord, string, bool> rayHitsPart,
                                          bool reportUnpaired = false, IList<string> allParts = null,
                                          PartRayIndex bodies = null, IList<HoleRecord> shafts = null, Action<int, int> progress = null){
            int unusedGroups;
            return Run(holes, rayHitsPart, reportUnpaired, allParts, bodies, shafts, progress, out unusedGroups);
        }
        // 带出实际同轴孔组数 groupCount，避免调用方在 Run 之后重复调 CountGroups 再跑一遍全量分组。
        public static List<HoleIssue> Run(IList<HoleRecord> holes, Func<HoleRecord, string, bool> rayHitsPart,
                                          bool reportUnpaired, IList<string> allParts,
                                          PartRayIndex bodies, IList<HoleRecord> shafts, Action<int, int> progress, out int groupCount){
            groupCount = 0;
            var issues = new List<HoleIssue>();
            if (holes == null) return issues;
            var relevant = Relevant(holes);
            var groups = GroupByAxis(relevant);
            var partUniverse = allParts != null && allParts.Count > 0
                ? allParts
                : (IList<string>)holes.Select(x => x.PartName).Distinct().ToList();
            double tol = CoaxialSpanTolMm / 1000.0;
            var skip = Options.SkipWords();
            var shaftsByPart = ShaftsByPart(shafts);
            // 「型材自动填充」生成的内嵌板（填充板_….par）：型材侧面的扳手孔正对着槽里的板，没人会在玻璃 / 填充板上配孔（围栏副本实测 4 条）。
            // 一次挑掉（原来每串孔 × 每个零件都截一次名字）；同时预解析射线目标与跳过标记，消除内层百万次字典/子串查询。
            var candidates = partUniverse.Where(p => !ShortPart(p).StartsWith(PanelNaming.Prefix, StringComparison.Ordinal)).ToList();
            var candTargets = bodies != null ? new PartRayTarget[candidates.Count] : null;
            var candSkipped = new bool[candidates.Count];
            for (int ci = 0; ci < candidates.Count; ci++) {
                if (candTargets != null) candTargets[ci] = bodies.Get(candidates[ci]);
                candSkipped[ci] = skip.Length > 0 && NameHas(candidates[ci], skip);
            }

            int gi = 0;
            foreach (var g in groups) {
                if (progress != null) { try { progress(++gi, groups.Count); } catch (OperationCanceledException) { throw; } catch { } }
                // 同一条轴线上被同一个零件"挡住"的几段孔，合成一条漏打孔（A、C 有孔、中间的 B 没孔 -> 只报 B 一次）
                var missingByPart = new Dictionary<string, HoleIssue>(StringComparer.Ordinal);
                var missingOrder = new List<string>();
                var chains = SplitByGap(g, CoaxialSpanTolMm);
                groupCount += chains.Count;
                foreach (var chain in chains) {
                    var parts = chain.Select(h => h.PartName).Distinct().ToList();
                    var partSet = parts.Count > 2 ? new HashSet<string>(parts, StringComparer.Ordinal) : null;

                    // 轴线在这串孔附近穿过、却没有孔的零件（0.8.5：几个零件都有孔时也查 —— 原来只查"只有一个零件有孔"的，
                    // 上板 + 中间垫板都有孔、下面的块漏了螺纹孔这种最常见的漏孔反而报不出来）。
                    // 遍历全部候选（不再遇第一个就 break 依赖装配遍历顺序）：
                    //   · 夹在孔段内部（side==0）的所有缺孔件全部报出；
                    //   · 孔口两端（side==-1 / +1）各按轴向最近距离选最贴近孔口的缺孔件（同距离 ≤0.05mm 的并列件一并报出）；
                    //   · 只要存在非「不查的零件」命中，就优先只报自制件，避免外购件挡住支架的真漏孔。
                    var reportParts = new List<string>();
                    {
                        var h = chain[0];
                        double minDia = h.DiameterMm;
                        for (int k = 1; k < chain.Count; k++) if (chain[k].DiameterMm < minDia) minDia = chain[k].DiameterMm;
                        V3 o, a; double lo, hi; ChainSpan(chain, out o, out a, out lo, out hi);
                        // 已经是完整的螺纹连接（螺纹孔 + 过孔）：只查夹在这串孔中间的零件，两头外面贴着的不算漏孔（见 CompleteJoint）
                        double span = tol;
                        if (hi - lo > 0.0015 && CompleteJoint(chain)) { lo += 0.0005; hi -= 0.0005; span = 0; }
                        var keepMid = new List<string>(); var skipMid = new List<string>();
                        var keepLo = new List<KeyValuePair<string, double>>(); var skipLo = new List<KeyValuePair<string, double>>();
                        var keepHi = new List<KeyValuePair<string, double>>(); var skipHi = new List<KeyValuePair<string, double>>();
                        // 先查射线/包围盒命中再查同轴螺栓杆：大装配上 99.9% 的零件连包围盒都够不着，前置 HasCoaxialShaft 会空耗百万次遍历
                        for (int ci = 0; ci < candidates.Count; ci++) {
                            string other = candidates[ci];
                            if (partSet != null ? partSet.Contains(other) : parts.Contains(other)) continue;
                            bool hit; double gapM = 0; int side = 1;
                            if (candTargets != null) {
                                var target = candTargets[ci];
                                hit = target != null && target.Hits(o, a, lo, hi, span, out gapM, out side);
                            } else {
                                hit = parts.Count == 1 && rayHitsPart != null && rayHitsPart(h, other);
                            }
                            if (!hit) continue;
                            if (HasCoaxialShaft(shaftsByPart, other, h, minDia, o, a, lo, hi, span)) continue;   // 同轴的螺栓杆 / 销：本来就该穿过它
                            bool isSkip = candSkipped[ci];
                            if (side == 0) { if (isSkip) skipMid.Add(other); else keepMid.Add(other); }
                            else if (side < 0) { if (isSkip) skipLo.Add(new KeyValuePair<string, double>(other, gapM)); else keepLo.Add(new KeyValuePair<string, double>(other, gapM)); }
                            else { if (isSkip) skipHi.Add(new KeyValuePair<string, double>(other, gapM)); else keepHi.Add(new KeyValuePair<string, double>(other, gapM)); }
                        }
                        bool anyKeep = keepMid.Count > 0 || keepLo.Count > 0 || keepHi.Count > 0;
                        if (anyKeep) {
                            reportParts.AddRange(keepMid);
                            AddClosest(keepLo, reportParts);
                            AddClosest(keepHi, reportParts);
                        } else {
                            reportParts.AddRange(skipMid);
                            AddClosest(skipLo, reportParts);
                            AddClosest(skipHi, reportParts);
                        }
                        for (int ri = 0; ri < reportParts.Count; ri++) {
                            string hp = reportParts[ri];
                            HoleIssue mi;
                            if (!missingByPart.TryGetValue(hp, out mi)) {
                                mi = new HoleIssue { Kind = HoleIssueKind.MissingHole, Holes = new List<HoleRecord>(), MissingPart = hp };
                                missingByPart[hp] = mi; missingOrder.Add(hp);
                            }
                            mi.Holes.AddRange(chain);
                        }
                    }

                    if (parts.Count == 1) {
                        var h = chain[0];
                        if (reportParts.Count == 0 && reportUnpaired) {
                            issues.Add(new HoleIssue { Kind = HoleIssueKind.Unpaired, Holes = chain,
                                Message = ShortPart(h.PartName) + " 上的 Φ" + N(h.DiameterMm) + " 孔" + Pos(h) + "是孤孔（附近其他零件上没有同轴孔）" });
                        }
                        continue;
                    }

                    // 多零件：先查同轴度。只比不同零件之间的孔（同一零件上的沉孔两条边本来就同轴）。
                    double maxLateral = 0;
                    for (int i = 0; i < chain.Count; i++)
                        for (int j = i + 1; j < chain.Count; j++)
                            if (chain[i].PartName != chain[j].PartName) maxLateral = Math.Max(maxLateral, LateralMm(chain[i], chain[j]));
                    if (maxLateral > AxisOffsetTolMm) {
                        issues.Add(new HoleIssue { Kind = HoleIssueKind.Misaligned, Holes = chain, Parts = parts,
                            Message = "同组孔最大偏心 " + N(maxLateral) + "mm（容差 " + N(AxisOffsetTolMm) + "mm），涉及 " + string.Join("、", parts.Select(ShortPart).ToArray()) + Pos(chain[0]) });
                        continue;
                    }

                    // 再查配做：每个零件取该组里**最小**的直径。
                    // 一个圆柱沉孔在实体上有两条不同直径的圆边（Φ11 过孔 + Φ18 沉孔外径），
                    // CollectAssemblyHoles 按边遍历、SameHoleLine 只去重"直径几乎相同"的边，
                    // 所以这里会拿到两个直径。真正跟螺钉杆配合、决定螺纹规格的是**过孔**那个
                    // 较小直径；拿 Φ18 去比对对方零件的螺纹底孔必然对不上，会把一个打得完全
                    // 正确的"沉孔 + 螺纹孔"组合误报成配错孔。
                    var byPart = chain.GroupBy(x => x.PartName).Select(x => new { Part = x.Key, Dia = x.Min(y => y.DiameterMm), Holes = x.ToList() }).ToList();
                    V3 co, ca; double clo, chi; ChainSpan(chain, out co, out ca, out clo, out chi);
                    // 只报一对：两个都是「不查的零件」的那对先记着，接着找有自制件的一对（外购件内部配不上不能挡住支架真配错）
                    HoleIssue wrongSkipped = null;
                    for (int i = 0; i < byPart.Count; i++)
                        for (int j = i + 1; j < byPart.Count; j++) {
                            string why;
                            // 0.10.2：一个零件的孔沿轴线伸进了另一个零件的孔里（重叠 > 1mm）= 插进去的东西（气管接头、节流阀、衬套、销）
                            // 自己的通道，不是一对连接孔。真机（SolidWorks 转过来的宝马托盘升降门）：FESTO 气缸 G1/8 口 Φ8.57 和拧在里面的
                            // 节流阀 Φ3 气道报"配错孔 M10 与 M2.5"。螺钉连接的两个孔沿轴线是首尾挨着的，不会重叠
                            if (Overlap(byPart[i].Holes, byPart[j].Holes, co, ca) > 0.001) continue;
                            if (!IsValidPair(byPart[i].Dia, byPart[j].Dia, out why)) {
                                var w = new HoleIssue { Kind = HoleIssueKind.WrongSpec, Holes = chain, Parts = new List<string>{ byPart[i].Part, byPart[j].Part },
                                    Message = ShortPart(byPart[i].Part) + " Φ" + N(byPart[i].Dia) + " 与 " + ShortPart(byPart[j].Part) + " Φ" + N(byPart[j].Dia) + " " + why + Pos(chain[0]) };
                                if (skip.Length > 0 && NameHas(byPart[i].Part, skip) && NameHas(byPart[j].Part, skip)) { if (wrongSkipped == null) wrongSkipped = w; continue; }
                                issues.Add(w); wrongSkipped = null;
                                i = byPart.Count; break;
                            }
                        }
                    if (wrongSkipped != null) issues.Add(wrongSkipped);
                }
                foreach (var part in missingOrder) {
                    var mi = missingByPart[part];
                    var owners = mi.Holes.Select(x => x.PartName).Distinct().ToList();
                    var first = mi.Holes[0];
                    mi.Message = string.Join("、", owners.Select(ShortPart).ToArray()) + " 上的 Φ" + N(first.DiameterMm) + " 孔" + Pos(first)
                               + "轴线穿过了 " + ShortPart(part) + "，但 " + ShortPart(part) + " 在这里没有对应的孔（疑似漏打孔）";
                    issues.Add(mi);
                }
            }
            return issues;
        }

        static void AddClosest(List<KeyValuePair<string, double>> sideHits, List<string> dest){
            if (sideHits.Count == 0) return;
            double best = double.PositiveInfinity;
            for (int i = 0; i < sideHits.Count; i++) if (sideHits[i].Value < best) best = sideHits[i].Value;
            for (int i = 0; i < sideHits.Count; i++)
                if (sideHits[i].Value <= best + 5e-5 && !dest.Contains(sideHits[i].Key)) dest.Add(sideHits[i].Key);
        }

        static string N(double v){ return v.ToString("0.##", CultureInfo.InvariantCulture); }

        // 只有位置不同、别的完全一样的问题合成一条（「…（共 8 处）」）：一条导轨 20 个螺钉孔、一排 8 个安装孔，
        // 原来列表里一个位置一条，几十条一模一样的字把别的问题都淹了（0.10.1 焊接台车：152 条里大半是这种）。
        // 涉及的孔全部保留（高亮、跳转、去配孔都还能用到每个位置）
        public static List<HoleIssue> Merge(IList<HoleIssue> issues){
            var list = new List<HoleIssue>();
            if (issues == null) return list;
            var byKey = new Dictionary<string, HoleIssue>(StringComparer.Ordinal);
            foreach (var it in issues) {
                if (it == null) continue;
                // 实例序号（「底板.par:3」的 :3）不算区别：同一个外购件用了 64 次，里面同一处孔报 64 条（OK310 围栏副本实测 256 条是这种）。
                // 但必须保留子装配路径（StripInstance(it.MissingPart) 与 Parts 路径），不能用 ShortPart 剥掉子装配名：
                // 否则「FESTO.asm:1/板.par:1」与「自制.asm:1/板.par:1」会合并成一条，导致外购件筛选串号。
                string partsKey = it.Parts == null || it.Parts.Count == 0
                    ? ""
                    : string.Join(",", it.Parts.Select(StripInstance).ToArray());
                string key = ((int)it.Kind) + "|" + StripInstance(it.MissingPart) + "|" + partsKey + "|" + StripInstance(StripPos(it.Message));
                HoleIssue m;
                if (!byKey.TryGetValue(key, out m)) {
                    m = new HoleIssue {
                        Kind = it.Kind, Message = it.Message, MissingPart = it.MissingPart, Done = it.Done,
                        Places = Math.Max(1, it.Places),
                        Holes = new List<HoleRecord>(it.Holes), Parts = new List<string>(it.Parts)
                    };
                    byKey[key] = m; list.Add(m);
                    continue;
                }
                m.Places += Math.Max(1, it.Places);
                m.Holes.AddRange(it.Holes);
                if (it.Parts != null) {
                    foreach (var p in it.Parts) if (!m.Parts.Contains(p)) m.Parts.Add(p);
                }
                if (m.Done.Length == 0 && !string.IsNullOrEmpty(it.Done)) m.Done = it.Done;
                string baseMsg = StripInstance(StripPos(it.Message));
                baseMsg = System.Text.RegularExpressions.Regex.Replace(baseMsg, "（共 \\d+ 处）$", "");
                m.Message = baseMsg + "（共 " + m.Places + " 处）";
            }
            return list;
        }
        static string StripInstance(string text){
            return System.Text.RegularExpressions.Regex.Replace(text ?? "", @"\.(par|psm|asm):\d+", ".$1", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }
        static string StripPos(string message){
            return System.Text.RegularExpressions.Regex.Replace(message ?? "", "（@X[^）]*）", "");
        }

        // 这一条是不是全在「不查」的零件里（判定口径里的 SkipNames）：
        //   · 漏打孔：缺孔的零件是外购件就不列（外购件改不了）；孔在外购件上、缺孔的是支架 —— 支架真漏了装它的孔，照样列；
        //   · 孔偏了 / 配错孔 / 孤孔：提示语里说的零件（Parts；没有就看涉及的孔）全是外购件才不列（气缸和自己的接头之间）；外购件和自制件之间的照样列。
        //   Run 本身也按 Options 里的关键字挑：一串孔只报一个缺孔零件 / 一对配错孔，先挑有自制件的（见 Run），所以筛掉外购件不会连带藏掉真问题
        // 名字按整条实例路径比（「FESTO_DSBC.asm:1/活塞杆.par:1」里 FESTO 在子装配名上），不分大小写
        public static bool Skipped(HoleIssue it, string[] words){
            if (it == null || words == null || words.Length == 0) return false;
            if (it.Kind == HoleIssueKind.MissingHole) return NameHas(it.MissingPart, words);
            if (it.Parts != null && it.Parts.Count > 0) { foreach (var p in it.Parts) if (!NameHas(p, words)) return false; return true; }
            if (it.Holes == null || it.Holes.Count == 0) return false;
            foreach (var h in it.Holes) if (!NameHas(h.PartName, words)) return false;
            return true;
        }
        static bool NameHas(string name, string[] words){
            if (string.IsNullOrEmpty(name)) return false;
            foreach (var w in words) if (name.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        // 子装配里的零件实例名是一整条路径（「总装.asm:1/子装配.asm:1/托盘.asm:1/底板.par:1」）：问题列表一行只放得下这条路径，
        // 「漏打孔 / 配错孔」到底说的是什么全被截掉了（0.10.1 焊接台车实测）。提示语里只写最后一级零件名，完整路径在「涉及的孔」里
        public static string ShortPart(string partName){
            if (string.IsNullOrEmpty(partName)) return partName ?? "";
            int i = partName.LastIndexOf('/');
            return i >= 0 && i < partName.Length - 1 ? partName.Substring(i + 1) : partName;
        }

        // 孔心坐标（装配坐标，毫米）—— 同规格的多个孔只有靠位置才区分得开
        static string Pos(HoleRecord h){
            return "（@X" + (h.Center.X * 1000).ToString("0.#", CultureInfo.InvariantCulture)
                 + " Y" + (h.Center.Y * 1000).ToString("0.#", CultureInfo.InvariantCulture)
                 + " Z" + (h.Center.Z * 1000).ToString("0.#", CultureInfo.InvariantCulture) + "）";
        }

        public static string Summary(IList<HoleIssue> issues, int holeCount, int groupCount){
            if (issues == null || issues.Count == 0)
                return "检查了 " + holeCount + " 个孔（" + groupCount + " 组同轴孔），没有发现问题。";
            // 按"处"数（合并过的一条算它合并的几处），列表条数少于处数时说一句
            int miss = issues.Where(i => i.Kind == HoleIssueKind.MissingHole).Sum(i => i.Places);
            int mis = issues.Where(i => i.Kind == HoleIssueKind.Misaligned).Sum(i => i.Places);
            int wrong = issues.Where(i => i.Kind == HoleIssueKind.WrongSpec).Sum(i => i.Places);
            int places = issues.Sum(i => i.Places);
            var parts = new List<string>();
            if (miss > 0) parts.Add("漏打孔 " + miss);
            if (mis > 0) parts.Add("孔偏了 " + mis);
            if (wrong > 0) parts.Add("配错孔 " + wrong);
            // 勾着「孤孔」时它也算进来：原来不列，只剩孤孔时结论是"发现 12 处问题："冒号后面什么都没有
            int lone = issues.Where(i => i.Kind == HoleIssueKind.Unpaired).Sum(i => i.Places);
            if (lone > 0) parts.Add("孤孔 " + lone);
            return "检查了 " + holeCount + " 个孔（" + groupCount + " 组同轴孔），发现 " + places + " 处问题" + (places > issues.Count ? "（同样的合成 " + issues.Count + " 条）" : "") + "：" + string.Join("，", parts.ToArray());
        }
    }
}
