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
        public object Selection;          // 用于结果窗口里高亮定位
        public override string ToString(){
            return PartName + " Φ" + DiameterMm.ToString("0.##", CultureInfo.InvariantCulture) + " @ " + Center;
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

    // 配孔检查。规则轻量，对齐 ICAN 的做法：按孔径范围筛选 + 按轴向间距配对。
    public static class HoleCheck {
        public const double MinDiameterMm = 3.0;     // 小于这个直径不当作连接孔
        public const double MaxDiameterMm = 25.0;
        public const double AxisAngleTolDeg = 2.0;   // 轴线方向容差
        public const double AxisOffsetTolMm = 0.2;   // 同轴偏移容差（超过即"孔偏了"）
        public const double CoaxialSpanTolMm = 3.0;  // 轴向距离小于这个才认为是一对

        // 两条圆边是否属于"同一个孔"：同一零件、同一轴线、同一直径。
        // 通孔在零件上有上下两条圆边，不去重会被当成两个孔。
        public static bool SameHoleLine(HoleRecord a, HoleRecord b, double diameterTolMm = 0.01){
            if (a == null || b == null) return false;
            if (a.PartName != b.PartName) return false;
            if (Math.Abs(a.DiameterMm - b.DiameterMm) > diameterTolMm) return false;
            return SameLine(a, b, Math.Cos(AxisAngleTolDeg * Math.PI / 180.0));
        }

        // 按"同一条轴线"分组。方向相同且横向偏移在容差内算同组。
        //
        // 原来每来一个新孔都要跟"已有的每一组"比一次，最坏 O(n²)（几千个孔时明显）。
        // 轴线方向先按 ±0.5° 量化成一个桶键：方向差超过 2° 的孔一定不同桶，
        // 所以只需在同一个（或相邻的 26 个）桶里做精确比较。相邻桶必须一起看 ——
        // 两个方向只差 0.4° 但落在桶边界两侧时，粗筛会把它们分到不同桶。
        public static List<List<HoleRecord>> GroupByAxis(IList<HoleRecord> holes){
            var groups = new List<List<HoleRecord>>();
            if (holes == null) return groups;
            double cosTol = Math.Cos(AxisAngleTolDeg * Math.PI / 180.0);
            var buckets = new Dictionary<string, List<List<HoleRecord>>>(StringComparer.Ordinal);
            foreach (var h in holes) {
                if (h == null) continue;
                var key = DirKey(h.Axis);
                List<HoleRecord> hit = null;
                foreach (var nk in NeighbourKeys(key)) {
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
                            if (!SameLine(refh, h, cosTol)) continue;
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
            if (a == null || b == null) return false;
            if (Math.Abs(a.Axis.Dot(b.Axis)) < cosTol) return false;
            var d = b.Center - a.Center;
            var lateral = d - a.Axis * d.Dot(a.Axis);
            return lateral.Length * 1000.0 <= AxisOffsetTolMm;
        }

        // 方向量化：单位向量按 ±0.5° 取整（cos/sin 步长 ≈ 0.0087）。
        // 先做符号规范化（让第一个非零分量为正），这样 (0,0,-1) 与 (0,0,1) 落在同一个桶 ——
        // SameLine 用 |dot| 比较，桶键也必须"正反同键"，否则反平行的两条同轴孔连候选都进不了。
        const double DirStep = 0.0087;
        static string DirKey(V3 axis){
            var a = axis;
            if (a.X < 0 || (a.X == 0 && (a.Y < 0 || (a.Y == 0 && a.Z < 0)))) a = new V3(-a.X, -a.Y, -a.Z);
            Func<double, int> q = v => (int)Math.Round(v / DirStep);
            return q(a.X) + "," + q(a.Y) + "," + q(a.Z);
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

        // 主检查。holes 为全装配的孔；rayHitsPart 用于判"漏打孔"（可为 null 则跳过该项）。
        // reportUnpaired：是否把"只有一个零件有孔、且轴线没穿过任何其他零件"的孤孔也报出来。
        // 默认不报——真实装配里绝大多数孔本来就是孤孔，全报会淹没有用信息。
        // allParts：装配里所有零件的名字。判"漏打孔"必须知道有哪些零件——
        // 只遍历"有孔的零件"永远发现不了漏孔（漏的那个零件本来就没孔）。
        // bodies：预建好的「零件名 -> 实体」表（AutoHoleWriter.OccurrenceBodies）。
        // 给了它就直接按名字查零件，不再让 rayHitsPart 每次重新扫装配 —— 漏打孔判定原本是
        // O(孤孔数 × 零件数²) 次 COM 调用，大装配上要跑分钟级。为 null 时退回 rayHitsPart。
        public static List<HoleIssue> Run(IList<HoleRecord> holes, Func<HoleRecord, string, bool> rayHitsPart,
                                          bool reportUnpaired = false, IList<string> allParts = null,
                                          AutoHoleWriter.OccurrenceBodies bodies = null){
            var issues = new List<HoleIssue>();
            if (holes == null) return issues;
            var relevant = holes.Where(h => h.DiameterMm >= MinDiameterMm && h.DiameterMm <= MaxDiameterMm).ToList();
            var groups = GroupByAxis(relevant);
            var partUniverse = allParts != null && allParts.Count > 0
                ? allParts
                : (IList<string>)holes.Select(x => x.PartName).Distinct().ToList();
            Func<HoleRecord, string, bool> hits = rayHitsPart;
            if (bodies != null) hits = (hh, pn) => AutoHoleWriter.RayPassesThrough(bodies, hh, pn);

            foreach (var g in groups) {
                // 同一零件上重复的孔（同轴同径）先去重，避免同一块板两个面各算一次
                var parts = g.Select(h => h.PartName).Distinct().ToList();

                if (parts.Count == 1) {
                    // 只有一个零件有孔：看轴线是否穿过别的零件
                    var h = g[0];
                    bool penetrates = false;
                    if (hits != null) {
                        foreach (var other in partUniverse) {
                            if (other == h.PartName) continue;
                            if (hits(h, other)) { penetrates = true; break; }
                        }
                    }
                    if (penetrates) {
                        issues.Add(new HoleIssue { Kind = HoleIssueKind.MissingHole, Holes = g,
                            Message = h.PartName + " 上的 Φ" + N(h.DiameterMm) + " 孔" + Pos(h)
                                    + "轴线穿过了其他零件，但对方没有对应的孔（疑似漏打孔）" });
                    } else if (reportUnpaired) {
                        issues.Add(new HoleIssue { Kind = HoleIssueKind.Unpaired, Holes = g,
                            Message = h.PartName + " 上的 Φ" + N(h.DiameterMm) + " 孔是孤孔（其他零件上没有同轴孔）" });
                    }
                    continue;
                }

                // 多零件：先查同轴度
                double maxLateral = 0;
                for (int i = 1; i < g.Count; i++) {
                    var d = g[i].Center - g[0].Center;
                    var lat = d - g[0].Axis * d.Dot(g[0].Axis);
                    maxLateral = Math.Max(maxLateral, lat.Length * 1000.0);
                }
                if (maxLateral > AxisOffsetTolMm) {
                    issues.Add(new HoleIssue { Kind = HoleIssueKind.Misaligned, Holes = g,
                        Message = "同组孔最大偏心 " + N(maxLateral) + "mm（容差 " + N(AxisOffsetTolMm) + "mm），涉及 " + string.Join("、", parts.ToArray()) });
                    continue;
                }

                // 再查配做：每个零件取该组里**最小**的直径。
                // 一个圆柱沉孔在实体上有两条不同直径的圆边（Φ11 过孔 + Φ18 沉孔外径），
                // CollectAssemblyHoles 按边遍历、SameHoleLine 只去重"直径几乎相同"的边，
                // 所以这里会拿到两个直径。真正跟螺钉杆配合、决定螺纹规格的是**过孔**那个
                // 较小直径；拿 Φ18 去比对对方零件的螺纹底孔必然对不上，会把一个打得完全
                // 正确的"沉孔 + 螺纹孔"组合误报成配错孔。
                // 不让用户去猜：最小直径就是这条轴线上与紧固件配合的直径。
                var byPart = g.GroupBy(h => h.PartName).Select(x => new { Part = x.Key, Dia = x.Min(y => y.DiameterMm) }).ToList();
                if (byPart.Count >= 2) {
                    for (int i = 0; i < byPart.Count; i++)
                        for (int j = i + 1; j < byPart.Count; j++) {
                            string why;
                            if (!IsValidPair(byPart[i].Dia, byPart[j].Dia, out why)) {
                                issues.Add(new HoleIssue { Kind = HoleIssueKind.WrongSpec, Holes = g,
                                    Message = byPart[i].Part + " Φ" + N(byPart[i].Dia) + " 与 " + byPart[j].Part + " Φ" + N(byPart[j].Dia) + " " + why });
                                i = byPart.Count; break;
                            }
                        }
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
