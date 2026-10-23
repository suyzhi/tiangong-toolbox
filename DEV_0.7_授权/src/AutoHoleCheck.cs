using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace TianGongCadSuite {
    // 一个孔在装配坐标下的记录。
    public sealed class HoleRecord {
        public V3 Center;
        public V3 Axis;
        public double DiameterMm;
        public string PartName = "";
        // 用于结果窗口里高亮定位。配孔检查采集时不再给每条圆边都建装配引用（每条 2～4 次 COM 调用，
        // 围栏上几千条边光这一步就要几十秒）：先记一个"要用时再建"的工厂，点到这一条时才建。
        object selection;
        public Func<object> MakeSelection;
        public object Selection {
            get {
                if (selection == null && MakeSelection != null) {
                    var make = MakeSelection; MakeSelection = null;
                    try { selection = make(); } catch { }
                }
                return selection;
            }
            set { selection = value; MakeSelection = null; }
        }
        // 孔沿自身轴线占的范围（相对 Center，米）。采集时同一个孔的上下两条圆边合并成一条记录，
        // 范围就从 [0,0] 扩成 [0, 孔深]。配孔检查靠它判"两个孔沿轴线挨不挨着"：
        // 同一条直线上隔着几百毫米的两个孔不是一对紧固孔，不能拿来互相比规格。
        public double AxialMin, AxialMax;
        public void Extend(V3 point){
            V3 a;
            try { a = Axis.Unit(); } catch { return; }
            double t = (point - Center).Dot(a);
            if (t < AxialMin) AxialMin = t;
            if (t > AxialMax) AxialMax = t;
        }
        public override string ToString(){
            return PartName + " Φ" + DiameterMm.ToString("0.##", CultureInfo.InvariantCulture) + " @ " + Center;
        }
    }

    // 零件局部坐标下的轴对齐包围盒（米）。
    public struct Box3 {
        public V3 Lo, Hi;
        public Box3(V3 lo, V3 hi){ Lo = lo; Hi = hi; }
        // 读不到包围盒时用它：等价于"整条直线都算命中"（退回原来的判法，宁可多报不漏报）
        public static Box3 Infinite { get { return new Box3(new V3(-1e9, -1e9, -1e9), new V3(1e9, 1e9, 1e9)); } }
        // 直线 o + d·t（d 为单位向量）穿过盒子的参数区间；不穿过返回 false。
        public bool Clip(V3 o, V3 d, out double t0, out double t1){
            t0 = double.NegativeInfinity; t1 = double.PositiveInfinity;
            double[] os = { o.X, o.Y, o.Z }, ds = { d.X, d.Y, d.Z }, los = { Lo.X, Lo.Y, Lo.Z }, his = { Hi.X, Hi.Y, Hi.Z };
            for (int i = 0; i < 3; i++) {
                if (Math.Abs(ds[i]) < 1e-12) {
                    if (os[i] < los[i] - 1e-9 || os[i] > his[i] + 1e-9) return false;
                    continue;
                }
                double a = (los[i] - os[i]) / ds[i], b = (his[i] - os[i]) / ds[i];
                if (a > b) { double tmp = a; a = b; b = tmp; }
                if (a > t0) t0 = a;
                if (b < t1) t1 = b;
                if (t0 > t1 + 1e-12) return false;
            }
            return true;
        }
    }

    public enum HoleIssueKind {
        MissingHole,   // 漏打孔：轴线穿过某个零件，但那个零件在这里没有孔
        Misaligned,    // 孔偏了：同组孔的轴线不重合
        WrongSpec,     // 配错孔：同组孔的直径配不上（既不是底孔+过孔，也不是沉孔）
        Unpaired       // 孤孔：只有一个零件上有孔，无法判断是漏了还是本来就不需要
    }

    public sealed class HoleIssue {
        public HoleIssueKind Kind;
        public string Message = "";
        public string MissingPart = "";   // 漏打孔：没打孔的那个零件（「去配孔」时提示用户点它的面）
        public List<HoleRecord> Holes = new List<HoleRecord>();
        public string KindName {
            get {
                switch (Kind) {
                    case HoleIssueKind.MissingHole: return "漏打孔";
                    case HoleIssueKind.Misaligned: return "孔偏了";
                    case HoleIssueKind.WrongSpec: return "配错孔";
                    default: return "孤孔";
                }
            }
        }
    }

    // 射线判据里的一个零件实例。
    // 零件实体（G.Body）的几何在零件自己的坐标系里，而 HoleRecord 的孔心/轴线是装配坐标：
    // 必须先用这个实例的装配变换换算回零件坐标再打射线，否则零件只要在装配里有平移/旋转，
    // 射线就打在错误的位置（同一个 .par 在装配里用了多次时，每个实例的变换也各不相同）。
    // castLocal(原点, 单位方向) 在零件坐标里打一条单向射线，碰到材料返回 true。
    //
    // 2026-10-13：射线判据加"距离"。真机实测（AUTO-HOLE-SCAN-VALIDATION-20261005）：
    // Body.get_FacesByRay 查的是**整条直线**，不是射线 —— 原来只要孔的轴线在装配里**任何地方**
    // 碰到别的零件（比如一米外的另一根型材），就报"漏打孔"。现在用命中面的包围盒（Face.GetRange，
    // 同样实测过）算出直线穿过那张面的大致位置，只有离这个孔沿轴线不超过 CoaxialSpanTolMm 的才算。
    public sealed class PartRayTarget {
        public readonly string Name;
        public readonly Transform Placement;
        readonly Func<V3, V3, bool> castLocal;
        readonly Func<V3, V3, IList<Box3>> boxesLocal;
        // 这个实例在装配坐标里的包围盒（读不到为 null）。配孔检查要拿每个孤孔去问"装配里每个零件"，
        // 每问一次就是一次 get_FacesByRay（COM）—— 孤孔 × 零件数，围栏上几十万次。先用包围盒在内存里挡掉
        // 根本够不着的零件（直线段离包围盒都远的，不可能碰到它的面），只对够得着的才去问 CAD。
        public Box3? World;
        public PartRayTarget(string name, Transform placement, Func<V3, V3, bool> castLocal){
            if (placement == null) throw new ArgumentNullException("placement");
            Name = name ?? ""; Placement = placement; this.castLocal = castLocal;
        }
        PartRayTarget(string name, Transform placement, Func<V3, V3, IList<Box3>> boxesLocal, bool unused){
            if (placement == null) throw new ArgumentNullException("placement");
            Name = name ?? ""; Placement = placement; this.boxesLocal = boxesLocal;
        }
        // boxesLocal(原点, 单位方向)：零件坐标里这条**直线**碰到的面，各自的包围盒
        public static PartRayTarget WithBoxes(string name, Transform placement, Func<V3, V3, IList<Box3>> boxesLocal){
            return new PartRayTarget(name, placement, boxesLocal, true);
        }
        // 不限距离：轴线所在的直线碰到这个零件就算（只给旧的布尔判据用）。
        public bool Hits(V3 centreAssembly, V3 axisAssembly){
            if (boxesLocal != null) return Hits(centreAssembly, axisAssembly, -1e9, 1e9, 0);
            if (castLocal == null) return false;
            var origin = Placement.InversePoint(centreAssembly);
            var dir = Placement.InverseNormal(axisAssembly);   // 刚体变换：方向的逆旋转就是转置
            if (!origin.Finite || !dir.Finite || dir.Length < 1e-12) return false;
            dir = dir.Unit();
            return castLocal(origin, dir) || castLocal(origin, dir * -1);
        }
        // 限距离：直线 origin + axis·t 只在 t ∈ [lo - tol, hi + tol]（米）这一段碰到零件才算。
        // 直线 origin + axis·t（t ∈ [lo - tol, hi + tol]）这一段够不够得着包围盒（盒子四周放宽 tol）。
        // 只用来"挡掉肯定碰不到的"：face 的包围盒都在实体包围盒里，段碰得到 face 就一定碰得到这个盒子。
        public static bool NearBox(Box3 box, V3 origin, V3 axis, double lo, double hi, double tol){
            if (!axis.Finite || axis.Length < 1e-12) return true;
            V3 d = axis.Unit();
            double pad = Math.Max(tol, 0) + 1e-6;
            var grown = new Box3(new V3(box.Lo.X - pad, box.Lo.Y - pad, box.Lo.Z - pad), new V3(box.Hi.X + pad, box.Hi.Y + pad, box.Hi.Z + pad));
            double t0, t1;
            if (!grown.Clip(origin, d, out t0, out t1)) return false;
            return t1 >= lo - tol && t0 <= hi + tol;
        }
        public bool Hits(V3 originAssembly, V3 axisAssembly, double lo, double hi, double tol){
            if (boxesLocal == null) return Hits(originAssembly, axisAssembly);   // 没有包围盒信息：退回不限距离
            if (World.HasValue && !NearBox(World.Value, originAssembly, axisAssembly, lo, hi, tol)) return false;
            var origin = Placement.InversePoint(originAssembly);
            var dir = Placement.InverseNormal(axisAssembly);
            if (!origin.Finite || !dir.Finite || dir.Length < 1e-12) return false;
            dir = dir.Unit();
            IList<Box3> boxes;
            try { boxes = boxesLocal(origin, dir); } catch { return false; }
            if (boxes == null) return false;
            foreach (var b in boxes) {
                double t0, t1;
                if (!b.Clip(origin, dir, out t0, out t1)) continue;
                if (t1 >= lo - tol && t0 <= hi + tol) return true;
            }
            return false;
        }
    }

    // 「零件实例名 -> 射线目标」表。实例名（occ.Name）在装配里唯一，重复出现时只认第一个。
    public sealed class PartRayIndex {
        readonly Dictionary<string, PartRayTarget> byName = new Dictionary<string, PartRayTarget>(StringComparer.Ordinal);
        public bool Add(PartRayTarget target){
            if (target == null || byName.ContainsKey(target.Name)) return false;
            byName[target.Name] = target; return true;
        }
        public int Count { get { return byName.Count; } }
        // 按这个孔自己的轴向范围 ± CoaxialSpanTolMm 查
        public bool Hits(HoleRecord hole, string partName){
            if (hole == null) return false;
            double tol = HoleCheck.CoaxialSpanTolMm / 1000.0;
            return Hits(partName, hole.Center, hole.Axis, hole.AxialMin, hole.AxialMax, tol);
        }
        public bool Hits(string partName, V3 origin, V3 axis, double lo, double hi, double tol){
            PartRayTarget t;
            if (partName == null || !byName.TryGetValue(partName, out t)) return false;
            return t.Hits(origin, axis, lo, hi, tol);
        }
    }

    // 配孔检查的判定口径。原来是写死的常量；现在窗口里「判定口径」可以改，存在
    // %LOCALAPPDATA%\TianGongCadSuite\hole-check.tsv（一行一项：名字 \t 值），默认值和原来一样。
    public sealed class HoleCheckOptions {
        public const string FileName = "hole-check.tsv";
        public double MinDiameterMm = 3.0;     // 小于这个直径不当作连接孔
        public double MaxDiameterMm = 25.0;
        public double AxisAngleTolDeg = 2.0;   // 轴线方向容差
        public double AxisOffsetTolMm = 0.2;   // 同轴偏移容差（超过即"孔偏了"）
        public double CoaxialSpanTolMm = 3.0;  // 沿轴线的间隙不超过这个才认为挨着（成对 / 漏打孔判定的范围）

        public HoleCheckOptions Clone(){ return (HoleCheckOptions)MemberwiseClone(); }

        public string Problem(){
            if (!(MinDiameterMm > 0) || !(MaxDiameterMm > MinDiameterMm)) return "孔径范围要满足 0 < 最小 < 最大。";
            if (!(AxisAngleTolDeg > 0 && AxisAngleTolDeg < 30)) return "方向容差要在 0～30° 之间。";
            if (!(AxisOffsetTolMm > 0)) return "同轴偏差容差要大于 0。";
            if (!(CoaxialSpanTolMm >= 0)) return "轴向间隙不能为负。";
            return "";
        }

        static readonly string[] Keys = { "最小孔径mm", "最大孔径mm", "方向容差度", "同轴偏差mm", "轴向间隙mm" };
        double[] Values(){ return new[]{ MinDiameterMm, MaxDiameterMm, AxisAngleTolDeg, AxisOffsetTolMm, CoaxialSpanTolMm }; }
        void Set(int i, double v){
            switch (i) { case 0: MinDiameterMm = v; break; case 1: MaxDiameterMm = v; break; case 2: AxisAngleTolDeg = v; break; case 3: AxisOffsetTolMm = v; break; default: CoaxialSpanTolMm = v; break; }
        }

        public static HoleCheckOptions Load(string path){
            var o = new HoleCheckOptions();
            foreach (var r in SettingsFile.Read(path)) {
                if (r.Length < 2) continue;
                int i = Array.IndexOf(Keys, r[0]);
                double v;
                if (i >= 0 && double.TryParse(r[1], NumberStyles.Float, CultureInfo.InvariantCulture, out v)) o.Set(i, v);
            }
            return o.Problem().Length == 0 ? o : new HoleCheckOptions();
        }
        public void Save(string path){
            var rows = new List<string[]>();
            var v = Values();
            for (int i = 0; i < Keys.Length; i++) rows.Add(new[]{ Keys[i], v[i].ToString("R", CultureInfo.InvariantCulture) });
            SettingsFile.Write(path, rows, "天工工具箱 · 配孔检查的判定口径（每行：名字<TAB>值）");
        }

        public string Describe(){
            return "判定口径\r\n" +
                "· 只看 " + N(MinDiameterMm) + "–" + N(MaxDiameterMm) + "mm 的孔（连接孔）；螺栓杆、销、圆凸台的外圆不算孔。\r\n" +
                "· 同一条轴线（方向差 < " + N(AxisAngleTolDeg) + "°）、沿轴线挨着（间隙 ≤ " + N(CoaxialSpanTolMm) + "mm）的孔算一组。\r\n" +
                "· 漏打孔：一个零件有孔，轴线在孔附近（" + N(CoaxialSpanTolMm) + "mm 内）穿过了另一个零件，但那个零件在这里没有孔。\r\n" +
                "· 孔偏了：同一组里不同零件的孔，轴线横向偏差 > " + N(AxisOffsetTolMm) + "mm。\r\n" +
                "· 配错孔：同一组的孔配不上同一个螺纹规格（底孔/内小径配过孔/沉孔才算对）；沉孔按较小的过孔直径比。\r\n" +
                "· 孤孔：只有一个零件有孔、附近也没穿过别的零件（大多数本来就该是孤孔，默认不显示）。\r\n" +
                "· 子装配里的零件也一起检查（零件名写成「子装配/零件」）。\r\n" +
                "（点「判定口径」可以改这些数）";
        }
        static string N(double v){ return v.ToString("0.###", CultureInfo.InvariantCulture); }
    }

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
        static void ChainSpan(IList<HoleRecord> chain, out V3 o, out V3 a, out double lo, out double hi){
            Frame(chain, out o, out a);
            lo = double.PositiveInfinity; hi = double.NegativeInfinity;
            foreach (var h in chain) { double l, u; Span(h, o, a, out l, out u); lo = Math.Min(lo, l); hi = Math.Max(hi, u); }
        }

        // 零件 part 在这个孔的轴线上有没有一根同轴的轴/销/螺栓杆（方向差 < 2°，横向偏差不超过孔半径）
        static bool HasCoaxialShaft(IList<HoleRecord> shafts, string part, HoleRecord hole){
            if (shafts == null || hole == null) return false;
            double cosTol = Math.Cos(AxisAngleTolDeg * Math.PI / 180.0);
            double tol = Math.Max(AxisOffsetTolMm, hole.DiameterMm / 2.0);
            foreach (var s in shafts) {
                if (s == null || s.PartName != part) continue;
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

            int gi = 0;
            foreach (var g in groups) {
                if (progress != null) { try { progress(++gi, groups.Count); } catch { } }
                // 同一条轴线上被同一个零件"挡住"的几段孔，合成一条漏打孔（A、C 有孔、中间的 B 没孔 -> 只报 B 一次）
                var missingByPart = new Dictionary<string, HoleIssue>(StringComparer.Ordinal);
                var missingOrder = new List<string>();
                foreach (var chain in SplitByGap(g, CoaxialSpanTolMm)) {
                    var parts = chain.Select(h => h.PartName).Distinct().ToList();

                    if (parts.Count == 1) {
                        // 只有一个零件有孔：看轴线在这个孔附近是否穿过别的零件
                        var h = chain[0];
                        V3 o, a; double lo, hi; ChainSpan(chain, out o, out a, out lo, out hi);
                        string hitPart = null;
                        foreach (var other in partUniverse) {
                            if (other == h.PartName) continue;
                            if (HasCoaxialShaft(shafts, other, h)) continue;   // 同轴的螺栓杆 / 销：本来就该穿过它
                            bool hit = bodies != null ? bodies.Hits(other, o, a, lo, hi, tol)
                                     : (rayHitsPart != null && rayHitsPart(h, other));
                            if (hit) { hitPart = other; break; }
                        }
                        if (hitPart != null) {
                            HoleIssue mi;
                            if (!missingByPart.TryGetValue(hitPart, out mi)) {
                                mi = new HoleIssue { Kind = HoleIssueKind.MissingHole, Holes = new List<HoleRecord>(), MissingPart = hitPart };
                                missingByPart[hitPart] = mi; missingOrder.Add(hitPart);
                            }
                            mi.Holes.AddRange(chain);
                        } else if (reportUnpaired) {
                            issues.Add(new HoleIssue { Kind = HoleIssueKind.Unpaired, Holes = chain,
                                Message = h.PartName + " 上的 Φ" + N(h.DiameterMm) + " 孔" + Pos(h) + "是孤孔（附近其他零件上没有同轴孔）" });
                        }
                        continue;
                    }

                    // 多零件：先查同轴度。只比不同零件之间的孔（同一零件上的沉孔两条边本来就同轴）。
                    double maxLateral = 0;
                    for (int i = 0; i < chain.Count; i++)
                        for (int j = i + 1; j < chain.Count; j++)
                            if (chain[i].PartName != chain[j].PartName) maxLateral = Math.Max(maxLateral, LateralMm(chain[i], chain[j]));
                    if (maxLateral > AxisOffsetTolMm) {
                        issues.Add(new HoleIssue { Kind = HoleIssueKind.Misaligned, Holes = chain,
                            Message = "同组孔最大偏心 " + N(maxLateral) + "mm（容差 " + N(AxisOffsetTolMm) + "mm），涉及 " + string.Join("、", parts.ToArray()) + Pos(chain[0]) });
                        continue;
                    }

                    // 再查配做：每个零件取该组里**最小**的直径。
                    // 一个圆柱沉孔在实体上有两条不同直径的圆边（Φ11 过孔 + Φ18 沉孔外径），
                    // CollectAssemblyHoles 按边遍历、SameHoleLine 只去重"直径几乎相同"的边，
                    // 所以这里会拿到两个直径。真正跟螺钉杆配合、决定螺纹规格的是**过孔**那个
                    // 较小直径；拿 Φ18 去比对对方零件的螺纹底孔必然对不上，会把一个打得完全
                    // 正确的"沉孔 + 螺纹孔"组合误报成配错孔。
                    var byPart = chain.GroupBy(x => x.PartName).Select(x => new { Part = x.Key, Dia = x.Min(y => y.DiameterMm) }).ToList();
                    for (int i = 0; i < byPart.Count; i++)
                        for (int j = i + 1; j < byPart.Count; j++) {
                            string why;
                            if (!IsValidPair(byPart[i].Dia, byPart[j].Dia, out why)) {
                                issues.Add(new HoleIssue { Kind = HoleIssueKind.WrongSpec, Holes = chain,
                                    Message = byPart[i].Part + " Φ" + N(byPart[i].Dia) + " 与 " + byPart[j].Part + " Φ" + N(byPart[j].Dia) + " " + why + Pos(chain[0]) });
                                i = byPart.Count; break;
                            }
                        }
                }
                foreach (var part in missingOrder) {
                    var mi = missingByPart[part];
                    var owners = mi.Holes.Select(x => x.PartName).Distinct().ToList();
                    var first = mi.Holes[0];
                    mi.Message = string.Join("、", owners.ToArray()) + " 上的 Φ" + N(first.DiameterMm) + " 孔" + Pos(first)
                               + "轴线穿过了 " + part + "，但 " + part + " 在这里没有对应的孔（疑似漏打孔）";
                    issues.Add(mi);
                }
            }
            return issues;
        }

        static string N(double v){ return v.ToString("0.##", CultureInfo.InvariantCulture); }

        // 孔心坐标（装配坐标，毫米）—— 同规格的多个孔只有靠位置才区分得开
        static string Pos(HoleRecord h){
            return "（@X" + (h.Center.X * 1000).ToString("0.#", CultureInfo.InvariantCulture)
                 + " Y" + (h.Center.Y * 1000).ToString("0.#", CultureInfo.InvariantCulture)
                 + " Z" + (h.Center.Z * 1000).ToString("0.#", CultureInfo.InvariantCulture) + "）";
        }

        public static string Summary(IList<HoleIssue> issues, int holeCount, int groupCount){
            if (issues == null || issues.Count == 0)
                return "检查了 " + holeCount + " 个孔（" + groupCount + " 组同轴孔），没有发现问题。";
            int miss = issues.Count(i => i.Kind == HoleIssueKind.MissingHole);
            int mis = issues.Count(i => i.Kind == HoleIssueKind.Misaligned);
            int wrong = issues.Count(i => i.Kind == HoleIssueKind.WrongSpec);
            var parts = new List<string>();
            if (miss > 0) parts.Add("漏打孔 " + miss);
            if (mis > 0) parts.Add("孔偏了 " + mis);
            if (wrong > 0) parts.Add("配错孔 " + wrong);
            return "检查了 " + holeCount + " 个孔（" + groupCount + " 组同轴孔），发现 " + issues.Count + " 处问题：" + string.Join("，", parts.ToArray());
        }
    }
}
