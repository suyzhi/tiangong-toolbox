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

        // 按"同一条轴线"分组。方向相同，且横向偏移不超过两孔中较小的半径（一个孔的轴线还落在
        // 另一个孔里），就算同组 —— 分组容差必须比 AxisOffsetTolMm 宽：两者相同的话，偏了
        // 0.5mm 的两个孔根本进不了同一组，只会变成两个（默认隐藏的）孤孔，"孔偏了"永远报不出来。
        // 组内是否真的偏了，由 Run 再用 AxisOffsetTolMm 判。
        //
        // 原来每来一个新孔都要跟"已有的每一组"比一次，最坏 O(n²)（几千个孔时明显）。
        // 轴线方向先按 ±0.5° 量化成一个桶键：方向差超过 2° 的孔一定不同桶，
        // 所以只需在同一个（或相邻的 26 个）桶里做精确比较。相邻桶必须一起看 ——
        // 两个方向只差 0.4° 但落在桶边界两侧时，粗筛会把它们分到不同桶。
        // 反平行的轴线也算同一条（SameLine 用 |dot|），所以还要再看"反方向"那个桶键的相邻桶。
        public static List<List<HoleRecord>> GroupByAxis(IList<HoleRecord> holes){
            var groups = new List<List<HoleRecord>>();
            if (holes == null) return groups;
            double cosTol = Math.Cos(AxisAngleTolDeg * Math.PI / 180.0);
            var buckets = new Dictionary<string, List<List<HoleRecord>>>(StringComparer.Ordinal);
            foreach (var h in holes) {
                if (h == null) continue;
                var key = DirKey(h.Axis);
                List<HoleRecord> hit = null;
                foreach (var nk in NeighbourKeys(key).Concat(NeighbourKeys(DirKey(h.Axis * -1)))) {
                    List<List<HoleRecord>> cand;
                    if (!buckets.TryGetValue(nk, out cand)) continue;
                    foreach (var g in cand) {
                        // 必须跟组内**每一个**成员比，不能只比 g[0]：
                        // 一个圆柱沉孔在实体上就是"同轴两个直径"（Φ5.5 过孔 + Φ11 沉孔），
                        // 采集顺序决定了 g[0] 是哪一个。只比 g[0] 的话，另一条直径会因为
                        // "跟 g[0] 横向差 0.2mm 以上"（其实是同一轴线的另一个截面）而被拆成
                        // 两条假轴线 —— 于是沉孔的假阳性又从这里绕回来了。
                        bool same = false;
                        foreach (var refh in g) {
                            if (!SameLine(refh, h, cosTol, GroupOffsetMm(refh, h))) continue;
                            same = true; break;
                        }
                        if (!same) continue;
                        hit = g; break;
                    }
                    if (hit != null) break;
                    if (hit != null) break;
                }
                if (hit == null) {
                    hit = new List<HoleRecord>();
                    List<List<HoleRecord>> cand;
                    if (!buckets.TryGetValue(key, out cand)) { cand = new List<List<HoleRecord>>(); buckets[key] = cand; }
                    cand.Add(hit);
                }
                hit.Add(h);
            }
            foreach (var kv in buckets) foreach (var g in kv.Value) groups.Add(g);
            return groups;
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

        // 方向量化：单位向量按 ±0.5° 取整（cos/sin 步长 ≈ 0.0087）。
        // 不做正反规范化：原来按"第一个非零分量为正"翻转，用的是 == 0 精确判断，而装配变换算出来的轴线
        // 带 1e-16 量级的浮点噪声 —— (0,0,1) 落在 z=+115 桶，(1.2e-16,0,-1) 却不翻转、落在 z=-115 桶，
        // 两桶不相邻，同一条轴线的两个孔连候选都进不了（偏 0.5mm 的一对孔被报成两个孤孔而不是"孔偏了"）。
        // 反平行由 GroupByAxis 另查反方向桶键来覆盖。Math.Round 对正负对称，所以反方向的键正好是各分量取负。
        const double DirStep = 0.0087;
        static string DirKey(V3 axis){
            Func<double, int> q = v => (int)Math.Round(v / DirStep);
            return q(axis.X) + "," + q(axis.Y) + "," + q(axis.Z);
        }
        // 27 个相邻桶键（含自身）：覆盖"方向差 0.5° 但被桶边界切开"的情况
        static IEnumerable<string> NeighbourKeys(string key){
            var p = key.Split(',');
            int x = int.Parse(p[0], CultureInfo.InvariantCulture), y = int.Parse(p[1], CultureInfo.InvariantCulture), z = int.Parse(p[2], CultureInfo.InvariantCulture);
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    for (int dz = -1; dz <= 1; dz++)
                        yield return (x + dx) + "," + (y + dy) + "," + (z + dz);
        }

        // 两个直径是否构成合法的"配做"关系：底孔+过孔（同规格），或过孔+沉孔。
        public static bool IsValidPair(double aMm, double bMm, out string why){
            why = "";
            // 几乎一样大（Φ6.6 与 Φ6.65）：同一把钻头打的孔。原来 Φ6.65 先被认成 M8 螺纹内小径，报「M6 与 M8 规格不同」
            if (Math.Abs(aMm - bMm) <= SameDrillMm) return true;
            // 有没有一个螺纹规格能同时解释两个孔（螺纹孔 / 过孔 / 沉头让位孔）。0.10.1 焊接台车实测：
            // 原来只认表里的两档过孔，Φ6（M5 粗装配过孔，HIWIN 导轨的标准孔）配 M5 螺纹孔 Φ4.13 一条导轨报 20 条「配错孔」；
            // Φ7（M6 粗装配过孔）先被认成 M8 底孔，正确的 M6 连接报「M8 与 M6」
            // 正好等于某个规格螺纹孔直径的（Φ17.5 = M20 底孔、Φ4.13 = M5 内小径）就按那个螺纹孔读，
            // 除非另一个也正好是螺纹孔直径 —— 两个不同规格的螺纹孔叠在一根轴上不是真设计，那就让大的那个当过孔（Φ5 + M4 螺纹孔）。
            // 原来没有这一条：M20 螺母 Φ17.5 配 Φ19 的孔被当成「M16 螺栓穿两个过孔」放过了（OK310 围栏副本实测）
            bool exA = ExactTapped(aMm), exB = ExactTapped(bMm);
            if (exA != exB) {
                double tapped = exA ? aMm : bMm, other = exA ? bMm : aMm;
                foreach (var r in HoleMatcher.Table) if (IsTapped(r, tapped) && IsClearance(r, other)) return true;
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
        // 这个直径是不是规格 r 的螺纹孔（底孔 / 内小径，粗牙按表，细牙按螺距算）
        public static bool IsTapped(ThreadRow r, double d){
            if (Math.Abs(d - r.TapDrill) <= HoleMatcher.Tolerance || Math.Abs(d - r.MinorDia) <= HoleMatcher.Tolerance) return true;
            double n = Nominal(r);
            foreach (var f in FinePitches) {
                if (f[0] != n) continue;
                if (Math.Abs(d - (n - 1.0825 * f[1])) <= 0.2 || Math.Abs(d - (n - f[1])) <= 0.2) return true;
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
        // 这个直径能不能让规格 r 的螺钉穿过：比公称直径大、不超过粗装配过孔（≈ 公称 ×1.25，GB/T 5277 粗装配 M5 5.8、M6 7、M8 10……）；
        // M12 以上的铰制孔 / 配合孔（孔径 = 公称直径）也算
        public static bool IsClearance(ThreadRow r, double d){
            double n = Nominal(r);
            if (n <= 0) return false;
            // 正好也是别的规格攻丝底孔的直径（Φ5 = M6 底孔）照样算：两种读法里「两个不同规格的螺纹孔叠在一根轴上」不是真设计，
            // 「M4 螺钉穿 Φ5 孔拧进 M4 螺纹孔」才是（用户 10-25 就照着把手底座的 Φ5 孔打了 M4 螺纹孔）
            // 下限按 GB/T 5277 精装配（M4 4.3、M6 6.4、M10 10.5……）：Φ4.13 这种 M5 螺纹孔不能当 M4 过孔
            if (d >= n + Math.Max(0.2, 0.05 * n) - 0.01 && d <= n * 1.25 + 0.2) return true;
            return n >= 12 && Math.Abs(d - n) <= 0.05;
        }
        // small 和 big 在规格 r 上配得上：螺纹孔 + 过孔、过孔 + 过孔（螺栓 + 螺母），
        // 或者 small 是过孔、big 是螺钉头的让位孔（≈ 沉头直径：沉孔分在两块板上做）。螺纹孔直接配沉头直径的孔不算（中间少了过孔）
        static bool Fits(ThreadRow r, double small, double big){
            bool smallClear = IsClearance(r, small);
            if (!smallClear && !IsTapped(r, small)) return false;
            if (IsClearance(r, big)) return true;
            // 让位孔只配真正的过孔（Φ12 正好等于 M12 公称直径的配合孔不算：Φ12 + Φ20 照样报）
            bool realClear = smallClear && Math.Abs(small - Nominal(r)) > 0.05;
            return realClear && big >= r.CounterboreDia - HoleMatcher.Tolerance && big <= r.CounterboreDia + 1.0;
        }

        // 这一串孔是不是一个完整的螺纹连接（同一规格：一个零件上是螺纹孔、另一个零件上是过孔）。
        // 完整的连接，螺钉到螺纹孔就到头了：轴线再往外穿过的零件（螺纹孔下面垫着的底板、螺钉头上面盖着的盖板）不需要孔，
        // 原来照样报「漏打孔」—— 焊接台车上 17 号板的 M6 螺纹孔下面是 002 底板，一个螺钉报一条
        public static bool CompleteJoint(IList<HoleRecord> chain){
            if (chain == null || chain.Count < 2) return false;
            var minByPart = chain.GroupBy(h => h.PartName).Select(g => g.Min(x => x.DiameterMm)).ToList();
            if (minByPart.Count < 2) return false;
            foreach (var r in HoleMatcher.Table) {
                int tapped = 0, clear = 0;
                foreach (var d in minByPart) { if (IsTapped(r, d)) tapped++; else if (IsClearance(r, d)) clear++; }
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
        static bool HasCoaxialShaft(Dictionary<string, List<HoleRecord>> shaftsByPart, string part, HoleRecord hole){
            List<HoleRecord> shafts;
            if (shaftsByPart == null || hole == null || part == null || !shaftsByPart.TryGetValue(part, out shafts)) return false;
            double cosTol = Math.Cos(AxisAngleTolDeg * Math.PI / 180.0);
            double tol = Math.Max(AxisOffsetTolMm, hole.DiameterMm / 2.0);
            foreach (var s in shafts) {
                if (Math.Abs(s.Axis.Dot(hole.Axis)) < cosTol) continue;
                if (LateralMm(hole, s) <= tol) return true;
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
            // 一次挑掉（原来每串孔 × 每个零件都截一次名字）
            var candidates = partUniverse.Where(p => !ShortPart(p).StartsWith(PanelNaming.Prefix, StringComparison.Ordinal)).ToList();

            int gi = 0;
            foreach (var g in groups) {
                if (progress != null) { try { progress(++gi, groups.Count); } catch { } }
                // 同一条轴线上被同一个零件"挡住"的几段孔，合成一条漏打孔（A、C 有孔、中间的 B 没孔 -> 只报 B 一次）
                var missingByPart = new Dictionary<string, HoleIssue>(StringComparer.Ordinal);
                var missingOrder = new List<string>();
                foreach (var chain in SplitByGap(g, CoaxialSpanTolMm)) {
                    var parts = chain.Select(h => h.PartName).Distinct().ToList();

                    // 轴线在这串孔附近穿过、却没有孔的零件（0.8.5：几个零件都有孔时也查 —— 原来只查"只有一个零件有孔"的，
                    // 上板 + 中间垫板都有孔、下面的块漏了螺纹孔这种最常见的漏孔反而报不出来）
                    string hitPart = null;
                    {
                        var h = chain[0];
                        V3 o, a; double lo, hi; ChainSpan(chain, out o, out a, out lo, out hi);
                        // 已经是完整的螺纹连接（螺纹孔 + 过孔）：只查夹在这串孔中间的零件，两头外面贴着的不算漏孔（见 CompleteJoint）
                        double span = tol;
                        if (hi - lo > 0.0015 && CompleteJoint(chain)) { lo += 0.0005; hi -= 0.0005; span = 0; }
                        // 先碰到的是「不查的零件」（外购件）就接着找：同一条轴线上真漏了孔的支架不能被它挡住（只报一个零件）
                        string skippedHit = null;
                        foreach (var other in candidates) {
                            if (parts.Contains(other)) continue;
                            if (HasCoaxialShaft(shaftsByPart, other, h)) continue;   // 同轴的螺栓杆 / 销：本来就该穿过它
                            bool hit = bodies != null ? bodies.Hits(other, o, a, lo, hi, span)
                                     : (parts.Count == 1 && rayHitsPart != null && rayHitsPart(h, other));
                            if (!hit) continue;
                            if (skip.Length > 0 && NameHas(other, skip)) { if (skippedHit == null) skippedHit = other; continue; }
                            hitPart = other; break;
                        }
                        if (hitPart == null) hitPart = skippedHit;
                        if (hitPart != null) {
                            HoleIssue mi;
                            if (!missingByPart.TryGetValue(hitPart, out mi)) {
                                mi = new HoleIssue { Kind = HoleIssueKind.MissingHole, Holes = new List<HoleRecord>(), MissingPart = hitPart };
                                missingByPart[hitPart] = mi; missingOrder.Add(hitPart);
                            }
                            mi.Holes.AddRange(chain);
                        }
                    }

                    if (parts.Count == 1) {
                        var h = chain[0];
                        if (hitPart == null && reportUnpaired) {
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

        static string N(double v){ return v.ToString("0.##", CultureInfo.InvariantCulture); }

        // 只有位置不同、别的完全一样的问题合成一条（「…（共 8 处）」）：一条导轨 20 个螺钉孔、一排 8 个安装孔，
        // 原来列表里一个位置一条，几十条一模一样的字把别的问题都淹了（0.10.1 焊接台车：152 条里大半是这种）。
        // 涉及的孔全部保留（高亮、跳转、去配孔都还能用到每个位置）
        public static List<HoleIssue> Merge(IList<HoleIssue> issues){
            var list = new List<HoleIssue>();
            if (issues == null) return list;
            var byKey = new Dictionary<string, HoleIssue>(StringComparer.Ordinal);
            foreach (var it in issues) {
                // 实例序号（「底板.par:3」的 :3）也不算区别：同一个外购件用了 64 次，里面同一处孔报 64 条（OK310 围栏副本实测 256 条是这种）
                string key = ((int)it.Kind) + "|" + StripInstance(ShortPart(it.MissingPart)) + "|" + StripInstance(StripPos(it.Message));
                HoleIssue m;
                if (!byKey.TryGetValue(key, out m)) {
                    m = new HoleIssue { Kind = it.Kind, Message = it.Message, MissingPart = it.MissingPart, Done = it.Done, Holes = new List<HoleRecord>(it.Holes), Parts = new List<string>(it.Parts) };
                    byKey[key] = m; list.Add(m);
                    continue;
                }
                m.Places++;
                m.Holes.AddRange(it.Holes);
                m.Message = StripInstance(StripPos(it.Message)) + "（共 " + m.Places + " 处）";
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
