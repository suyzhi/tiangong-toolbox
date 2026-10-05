using System;
using System.Collections.Generic;
using System.Globalization;

namespace TianGongCadSuite {
    // 模型上读到的一个"圆"：圆形边（孔口圆）、圆柱面的端面圆、圆锥面的端面圆。
    // 纯数据，不碰 CAD —— 面扫描、沉孔识别、归类的判据全在这里，脱离 CAD 也能跑回归。
    // 坐标单位与 V3 一致（米），直径/半径对外一律用毫米。
    public sealed class CircleCandidate {
        public V3 Center;           // 圆心
        public V3 Axis;             // 单位轴向
        public double RadiusMm;     // 半径（毫米）
        public bool Closed = true;  // 是不是整圆（圆弧不算孔口）
        public string Wall = "";    // 这个圆旁边的壁面类型：Cylinder / Cone / Torus / ""（只用于措辞）
        public string Source = "";  // 来源（边号等），出问题时能说清是哪个圆
        public object Tag;          // 原始 CAD 对象，调用方自己用
        public double DiameterMm { get { return RadiusMm * 2.0; } }
        public override string ToString(){
            return "Φ" + HoleScan.N(DiameterMm) + " @ " + Center + " 轴 " + Axis + (Wall.Length > 0 ? " 壁=" + Wall : "");
        }
    }

    // 识别出来的一个孔。
    public class ScannedHole {
        public V3 Center;              // 孔口圆心（用来和打孔面求交）
        public V3 Axis;                // 孔轴线（单位向量）
        public double DiameterMm;      // **配做用的孔径**：沉孔/锥沉/倒角时＝孔口下面那个孔径
        public double MouthDiameterMm; // 面上看到的孔口直径
        public string MouthKind = "";  // 圆柱沉孔 / 锥形沉孔 / 孔口倒角 / 孔口圆角 / 孔口台阶；平孔为 ""
        public string Source = "";     // 这个孔是从哪来的（面名/圆柱面），给界面用
        public object Tag;
        public bool Stepped { get { return MouthDiameterMm - DiameterMm > HoleScan.StepToleranceMm; } }
        public string Note {
            get {
                if (!Stepped) return "";
                return "孔口 Φ" + HoleScan.N(MouthDiameterMm) + "（" + MouthKind + "），按下面的 Φ" + HoleScan.N(DiameterMm) + " 配做";
            }
        }
        public override string ToString(){ return "Φ" + HoleScan.N(DiameterMm) + " @ " + Center + (Stepped ? "（孔口 Φ" + HoleScan.N(MouthDiameterMm) + "）" : ""); }
    }

    // 面扫描 / 沉孔识别的纯逻辑。
    public static class HoleScan {
        internal static string N(double v){ return v.ToString("0.##", CultureInfo.InvariantCulture); }

        // ---- 判据（都有实测/几何依据，改之前先想清楚代价） ----
        // 同轴：两条轴线夹角 ≤ 约 1.1°（cos ≥ 0.9998），且轴线的径向距离 ≤ 0.05mm。
        public const double ParallelCos = 0.9998;
        public const double CoaxialOffsetMm = 0.05;
        // 直径差小于它算"一样大"。不能放宽：M6 过孔 Φ6.6 与 M8 螺纹内小径 Φ6.647
        // 只差 0.047mm，放宽到 0.05 就会把两种孔并成一组。
        public const double StepToleranceMm = 0.02;
        public const double GroupToleranceMm = 0.02;

        public static bool Parallel(V3 a, V3 b){
            double d = Math.Abs(a.Unit().Dot(b.Unit()));
            return d >= ParallelCos;
        }

        // other 的圆心相对 mouth 轴线的径向偏移（毫米）。
        public static double RadialOffsetMm(V3 mouthCenter, V3 axis, V3 otherCenter){
            var d = otherCenter - mouthCenter;
            double axial = d.Dot(axis);
            return (d - axis * axial).Length * 1000.0;
        }
        // other 的圆心沿 mouth 轴线的偏移（毫米），带符号。
        public static double AxialOffsetMm(V3 mouthCenter, V3 axis, V3 otherCenter){
            return (otherCenter - mouthCenter).Dot(axis) * 1000.0;
        }

        // 这个圆是不是"一个真正的孔口"：整圆 + 半径有效。
        public static bool Usable(CircleCandidate c){
            return c != null && c.Closed && c.RadiusMm > 0.05 && c.Center.Finite && c.Axis.Finite;
        }

        // 沉孔/锥沉/倒角的关键一步：**孔口下面还有一个更小的同轴整圆**，那才是要配做的孔径。
        //
        // 为什么要这样找（真机几何，见 tools/facescan-probe）：
        //   圆柱沉孔 Φ11/Φ6.6：顶面是 Φ11 的孔口圆，沉孔底是 Φ6.6，底面出口也是 Φ6.6；
        //   锥形沉孔 Φ11 90°/Φ5：顶面是 Φ11，锥面与圆柱交界处是 Φ5，出口也是 Φ5；
        //   孔口倒角 0.5×45°：顶面是 Φ7.6，出口是 Φ6.6。
        // 平孔则只有自己这一个直径（上下两条圆边同径），所以"找不到更小的同轴圆"就是平孔。
        // 取**最小**的那个同轴圆：沉孔底与出口同径时它是同一个答案，倒角孔也只有出口这一个候选。
        public static bool InnerBelow(CircleCandidate mouth, IList<CircleCandidate> all, out CircleCandidate inner){
            inner = null;
            if (!Usable(mouth)) return false;
            foreach (var other in all) {
                if (other == null || ReferenceEquals(other, mouth)) continue;
                if (!Usable(other)) continue;
                if (other.RadiusMm > mouth.RadiusMm - StepToleranceMm) continue;   // 不比孔口小
                if (!Parallel(other.Axis, mouth.Axis)) continue;                   // 不同轴
                if (RadialOffsetMm(mouth.Center, mouth.Axis, other.Center) > CoaxialOffsetMm) continue;
                // 不再要求"不同平面"：点沉孔底那个环形面时，外圆 Φ11 与内圆 Φ6.6 是共面的，
                // 而按它们算出来的孔径都是 Φ6.6（环形面的外圆会顺着出口圆落到 6.6），去不去掉这一条结果一样。
                // 去掉它的好处是"圆柱面认孔"那条路：圆柱的基点是轴上任意一点，
                // 要求"不同平面"会把它与某个圆共面时该圆的候选资格误删掉。
                if (inner == null || other.RadiusMm < inner.RadiusMm) inner = other;
            }
            return inner != null;
        }

        // 孔口下面的那个孔叫什么（只影响给用户看的话）。
        public static string MouthKindOf(string wall, double mouthMm, double innerMm){
            double step = (mouthMm - innerMm) / 2.0;
            if (step <= StepToleranceMm) return "";
            if (wall == "Cylinder") return "圆柱沉孔";
            if (wall == "Cone") return step >= 1.0 ? "锥形沉孔" : "孔口倒角";   // 倒角一般 0.3~1mm，锥沉按螺钉头径通常 ≥2mm 半增量
            if (wall == "Torus") return "孔口圆角";
            // 读不到壁面类型（老版本 CAD、或 GetFaces 失败）时按尺寸粗分，
            // 别写"孔口台阶"这种用户看不懂的说法。
            return step >= 1.0 ? "沉孔" : "孔口倒角";
        }

        // 一个圆口 -> 一个孔（含沉孔识别）。
        public static ScannedHole FromCircle(CircleCandidate mouth, IList<CircleCandidate> all){
            if (!Usable(mouth)) return null;
            CircleCandidate inner;
            var h = new ScannedHole { Center = mouth.Center, Axis = mouth.Axis.Unit(), MouthDiameterMm = mouth.DiameterMm,
                                      DiameterMm = mouth.DiameterMm, Source = mouth.Source, Tag = mouth.Tag };
            if (InnerBelow(mouth, all, out inner)) {
                h.DiameterMm = inner.DiameterMm;
                h.MouthKind = MouthKindOf(mouth.Wall, h.MouthDiameterMm, h.DiameterMm);
            }
            return h;
        }

        // 一组圆口 -> 孔（逐个识别 + 去重）。
        public static List<ScannedHole> FromCircles(IList<CircleCandidate> mouths, IList<CircleCandidate> all){
            var list = new List<ScannedHole>();
            if (mouths == null) return list;
            foreach (var m in mouths) {
                var h = FromCircle(m, all);
                if (h != null) list.Add(h);
            }
            return Dedupe(list);
        }

        // 去重：同一条轴线上、配做孔径相同的，就是同一个孔。
        // 典型场景——点沉孔底那个环形面：外圆（Φ11）与内圆（Φ6.6）都在这个面上，
        // 两个圆都会识别成"Φ6.6 的孔"，必须只留一个。
        // 保留信息更全的那个（孔口更大 = 认出了沉孔）。
        public static List<ScannedHole> Dedupe(IList<ScannedHole> holes){
            var kept = new List<ScannedHole>();
            if (holes == null) return kept;
            foreach (var h in holes) {
                bool merged = false;
                for (int i = 0; i < kept.Count; i++) {
                    var k = kept[i];
                    if (Math.Abs(k.DiameterMm - h.DiameterMm) > StepToleranceMm) continue;
                    if (!Parallel(k.Axis, h.Axis)) continue;
                    if (RadialOffsetMm(k.Center, k.Axis, h.Center) > CoaxialOffsetMm) continue;
                    if (h.MouthDiameterMm > k.MouthDiameterMm + StepToleranceMm) kept[i] = h;   // 换成孔口信息更全的那个
                    merged = true;
                    break;
                }
                if (!merged) kept.Add(h);
            }
            return kept;
        }

        // 按配做孔径归类：同一类的孔共用一份可编辑的规格（界面上就是一行 "Φ6.6 ×12"）。
        // 顺序按首次出现的顺序，和用户点选的先后一致。
        public static List<List<ScannedHole>> GroupByDiameter(IEnumerable<ScannedHole> holes){
            var groups = new List<List<ScannedHole>>();
            if (holes == null) return groups;
            foreach (var h in holes) {
                List<ScannedHole> found = null;
                foreach (var g in groups) if (Math.Abs(g[0].DiameterMm - h.DiameterMm) <= GroupToleranceMm) { found = g; break; }
                if (found == null) { found = new List<ScannedHole>(); groups.Add(found); }
                found.Add(h);
            }
            return groups;
        }

        // 一组孔的"孔口情况"汇总，给界面上的说明行用。
        // 例："其中 8 个是圆柱沉孔（孔口 Φ11，按 Φ6.6 配做）"
        public static string MouthSummary(IEnumerable<ScannedHole> group){
            if (group == null) return "";
            var list = new List<ScannedHole>(group);
            if (list.Count == 0) return "";
            var kinds = new List<string>();
            var counts = new List<int>();
            foreach (var h in list) {
                if (!h.Stepped) continue;
                string key = h.MouthKind + "|" + N(h.MouthDiameterMm) + "|" + N(h.DiameterMm);
                int i = kinds.IndexOf(key);
                if (i < 0) { kinds.Add(key); counts.Add(0); i = kinds.Count - 1; }
                counts[i]++;
            }
            if (kinds.Count == 0) return "";
            var parts = new List<string>();
            for (int i = 0; i < kinds.Count; i++) {
                string[] f = kinds[i].Split('|');
                parts.Add(counts[i] + " 个是" + f[0] + "（孔口 Φ" + f[1] + "，按 Φ" + f[2] + " 配做）");
            }
            return "其中 " + string.Join("；", parts.ToArray());
        }

        // 扫描结果的一句话（状态栏用）。
        public static string StatusLine(IEnumerable<ScannedHole> holes){
            if (holes == null) return "这个面上没有识别到孔。";
            var list = new List<ScannedHole>(holes);
            if (list.Count == 0) return "这个面上没有识别到孔。";
            var groups = GroupByDiameter(list);
            var parts = new List<string>();
            foreach (var g in groups) {
                string s = "Φ" + N(g[0].DiameterMm) + " ×" + g.Count;
                string extra = MouthSummary(g);
                if (extra.Length > 0) s += "（" + extra + "）";
                parts.Add(s);
            }
            return "识别到 " + list.Count + " 个孔：" + string.Join("；", parts.ToArray());
        }
    }
}
