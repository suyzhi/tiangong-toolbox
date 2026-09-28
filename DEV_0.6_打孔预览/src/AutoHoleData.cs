using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace TianGongCadSuite {
    // 孔型。与天工CAD 原生孔类型一一对应。
    // 注意：螺纹孔在天工CAD（Solid Edge 内核）里不是一个独立的孔类型——
    // 它是「普通孔 + 螺纹数据」，孔按螺纹内小径建模，螺纹以装饰螺纹显示。
    // 实测：AddEx(igTappedHole,…) 会被 CAD 换成 igCounterdrillHole 并带上
    // 保存下来的沉头尺寸（Φ13.71 / 90°），打出来就是一个带锥面的沉头孔。
    // 所以这里一律用 igRegularHole + 螺纹数据来造螺纹孔。
    public enum HoleKind {
        Through,      // 通孔
        Tapped,       // 螺纹孔    -> 普通孔 + 螺纹数据
        Counterbore,  // 圆柱沉孔
        Countersink   // 锥形沉孔
    }

    // 孔底形式。
    public enum HoleBottom {
        Flat,      // 平底（默认，最常用：钣金/型材薄壁）
        VBottom    // 带钻尖（V 型底），角度默认 118°
    }

    // 公制螺纹规格表的一行。全部单位：毫米。
    // 底孔/过孔/沉孔数据取自 GB/T 196、GB/T 70.1（内六角圆柱头螺钉）常用值；
    // MinorDia 取自天工CAD 自带的 ISOHOLES.TXT（螺纹内小径），
    // 与 CAD 自己的孔命令建模出来的直径一致。
    public struct ThreadRow {
        public string Size;          // "M6"
        public double Pitch;         // 螺距
        public double TapDrill;      // 攻丝底孔直径
        public double MinorDia;      // 螺纹内小径（螺纹孔按这个直径建模）
        public double ClearanceClose;// 过孔（精装配）
        public double ClearanceMid;  // 过孔（中等装配）
        public double CounterboreDia;// 沉孔直径
        public double CounterboreDep;// 沉孔深度
        // 螺钉头半径（GB/T 70.1 内六角圆柱头；GB/T 819 沉头十字的标准头径与其一致）。
        // 锥形沉孔的默认锥孔直径 = 2×HeadRadius（90° 沉头螺钉头沉到与表面齐平），
        // 不再是"沉孔直径 + 1mm"那种拍脑袋的经验值。
        public double HeadRadius;
        public ThreadRow(string s,double p,double tap,double minor,double cc,double cm,double cd,double cdep)
            : this(s,p,tap,minor,cc,cm,cd,cdep,0){}
        public ThreadRow(string s,double p,double tap,double minor,double cc,double cm,double cd,double cdep,double headRadius){
            Size=s;Pitch=p;TapDrill=tap;MinorDia=minor;ClearanceClose=cc;ClearanceMid=cm;CounterboreDia=cd;CounterboreDep=cdep;HeadRadius=headRadius;
        }
        public string Display { get { return Size + " × " + Pitch.ToString("0.##",CultureInfo.InvariantCulture); } }
    }

    // 一个具体孔规格（已解析、可直接交给 CAD 层）。
    public sealed class HoleSpec {
        public HoleKind Kind;
        public string ThreadSize = "";   // 关联的螺纹规格，例如 "M6"；自定义孔时为空
        public double HoleDiameter;      // 主孔直径（毫米）
        public double Depth;             // 深度（毫米）；0 表示贯通
        public double CounterboreDiameter, CounterboreDepth;
        public double CountersinkDiameter, CountersinkAngle;
        public HoleBottom Bottom = HoleBottom.Flat;
        public double BottomAngle = 118;  // V 型孔底角度（度）
        public bool Chamfer;              // 孔口倒角
        public double ChamferSetback = 0.5, ChamferAngle = 45;
        public string Note = "";         // 给用户看的一句话说明

        public HoleSpec Clone(){ return (HoleSpec)MemberwiseClone(); }

        public bool Through { get { return Depth <= 1e-9; } }

        public string Summary {
            get {
                string s = KindName(Kind) + " Φ" + N(HoleDiameter);
                if (Kind == HoleKind.Counterbore) s += "　沉孔Φ" + N(CounterboreDiameter) + " 深" + N(CounterboreDepth);
                if (Kind == HoleKind.Countersink) s += "　锥孔Φ" + N(CountersinkDiameter) + " " + N(CountersinkAngle) + "°";
                if (ThreadSize.Length > 0 && Kind == HoleKind.Tapped) s += "（" + ThreadSize + "）";
                else if (ThreadSize.Length > 0 && Kind == HoleKind.Through) { /* 通孔与螺纹无关，不显示规格，免得"通孔 M6"这种说法误导 */ }
                else if (ThreadSize.Length > 0) s += "　(" + ThreadSize + ")";
                s += Through ? "　贯通" : "　深" + N(Depth);
                if (!Through && Bottom == HoleBottom.VBottom) s += "（V 底 " + N(BottomAngle) + "°）";
                if (!Through && Bottom == HoleBottom.Flat) s += "（平底）";
                if (Chamfer) s += "　孔口倒角 " + N(ChamferSetback) + "×" + N(ChamferAngle) + "°";
                return s;
            }
        }
        public static string KindName(HoleKind k){
            switch(k){ case HoleKind.Tapped: return "螺纹孔"; case HoleKind.Counterbore: return "圆柱沉孔";
                       case HoleKind.Countersink: return "锥形沉孔"; default: return "通孔"; }
        }
        static string N(double v){ return v.ToString("0.##",CultureInfo.InvariantCulture); }
    }

    // 匹配结果：参考孔是什么 + 目标件该打什么。
    public sealed class HoleMatch {
        public ThreadRow Row;
        public bool HasRow;              // false = 未命中标准螺纹表，走自定义孔
        public bool ReferenceIsTapped;   // 参考孔是螺纹底孔
        public double ReferenceDiameter; // 参考孔实测直径（毫米）
        public double Deviation;         // 与标准值的偏差（毫米）
        public HoleSpec Target;          // 目标件建议孔规格
        public string Message = "";      // 状态栏那句话，例如 "已匹配到沉孔！"
        // 歧义：参考孔直径同时落在两个"含义相反"的规格窗口里（同一个规格的底孔/内小径
        // 不算歧义）。这时自动推断不可靠，界面必须提示用户手动确认。
        public bool Ambiguous;
        public string AmbiguousWith = "";  // 另一个说得通的解读，例如 "M4 的攻丝底孔（配螺纹孔）"
        public double AmbiguousDeviation;  // 另一个解读与实测直径的偏差（毫米）
        // 命中的是哪个标准值。判"这个孔离两个规格一样近"要用它，
        // 而只留"孔型 + 偏差"是判不出来的。
        internal double MatchedStandard;
        internal bool MatchedIsTap;
    }

    // 直径区间 -> 规格 的匹配器。
    // 规则（对齐 ICAN 的配做逻辑）：
    //   参考孔 = 螺纹底孔  -> 目标件打「圆柱沉孔」（目标件要过螺钉头）
    //   参考孔 = 过孔      -> 目标件打「螺纹孔」  （目标件要攻丝）
    public static class HoleMatcher {
        // 匹配容差（毫米）。标准值附近 ±0.35 视为命中；0.35≈相邻规格间距的一半。
        public const double Tolerance = 0.35;

        public static readonly ThreadRow[] Table = new ThreadRow[] {
            new ThreadRow("M3" ,0.5 ,2.5 ,2.459 ,3.2 ,3.4 ,6.0 ,3.4 ,2.75),
            new ThreadRow("M4" ,0.7 ,3.3 ,3.242 ,4.3 ,4.5 ,8.0 ,4.4 ,3.60),
            new ThreadRow("M5" ,0.8 ,4.2 ,4.134 ,5.3 ,5.5 ,9.5 ,5.4 ,4.50),
            new ThreadRow("M6" ,1.0 ,5.0 ,4.917 ,6.4 ,6.6 ,11.0,6.5 ,5.00),
            new ThreadRow("M8" ,1.25,6.8 ,6.647 ,8.4 ,9.0 ,14.5,8.6 ,6.50),
            new ThreadRow("M10",1.5 ,8.5 ,8.376 ,10.5,11.0,18.0,11.0,8.00),
            new ThreadRow("M12",1.75,10.2,10.106,13.0,13.5,20.0,13.0,9.00),
            new ThreadRow("M16",2.0 ,14.0,13.835,17.0,17.5,26.0,17.0,12.00),
            new ThreadRow("M20",2.5 ,17.5,17.294,21.0,22.0,33.0,21.0,15.00),
        };

        public static ThreadRow? Find(string size){
            foreach (var r in Table) if (string.Equals(r.Size, size, StringComparison.OrdinalIgnoreCase)) return r;
            return null;
        }

        // 相邻规格的容差窗口会重叠：例如 Φ3.4 既是 M3 的中等装配过孔（过孔 -> 目标件配螺纹孔），
        // 也离 M4 的攻丝底孔 3.3 只有 0.1（底孔 -> 目标件配沉孔）。两者物理含义相反，
        // 谁被选中完全取决于表里的遍历顺序。这里把"另一个同样说得通的解读"记下来，
        // 交给界面提示用户确认，而不是让用户以为自动推断是可靠的。
        // 两种解读与实测直径的偏差相差 ≤0.12mm 时，自动推断就是"分不出来"，必须提示用户确认。
        // 判据取的是**标准值间距**（偏差之差就等于两个标准值之差的绝对值），也就是
        // "这两个候选的直径差，还没有小到我能靠 ±0.1mm 的读数量出来"。
        // 它们是真实存在的重叠：M3 过孔 3.4 与 M4 底孔 3.3 只差 0.1；M8 过孔 8.4 与
        // M10 底孔 8.5 也只差 0.1；M8 内小径 6.647 与 M6 过孔 6.6 差 0.047。
        // 这些解读要求目标件打的孔**完全不同**（一个攻丝、一个配沉孔），
        // 静默选错比多问一句的代价大得多。
        public const double AmbiguityBand = 0.12;

        // 由参考孔直径反推。diameterMm 必须是正数。
        public static HoleMatch Match(double diameterMm){
            if (double.IsNaN(diameterMm) || double.IsInfinity(diameterMm) || diameterMm <= 0)
                throw new ArgumentException("参考孔直径必须是正数。");
            HoleMatch best = null, second = null; double bestDev = double.MaxValue, secondDev = double.MaxValue;
            foreach (var r in Table) {
                Consider(ref best, ref bestDev, ref second, ref secondDev, r, r.TapDrill, true, diameterMm);
                Consider(ref best, ref bestDev, ref second, ref secondDev, r, r.MinorDia, true, diameterMm);
                Consider(ref best, ref bestDev, ref second, ref secondDev, r, r.ClearanceMid, false, diameterMm);
                Consider(ref best, ref bestDev, ref second, ref secondDev, r, r.ClearanceClose, false, diameterMm);
            }
            if (Environment.GetEnvironmentVariable("AUTOHOLE_MATCH_DEBUG") != null)
                Console.WriteLine("MATCHDBG d=" + diameterMm.ToString("0.###", CultureInfo.InvariantCulture)
                    + " best=" + (best == null ? "null" : best.Row.Size + "/" + best.ReferenceIsTapped + "/" + best.Deviation.ToString("0.####", CultureInfo.InvariantCulture))
                    + " second=" + (second == null ? "null" : second.Row.Size + "/" + second.ReferenceIsTapped + "/" + second.Deviation.ToString("0.####", CultureInfo.InvariantCulture))
                    + " exactBest=" + IsExactHit(best) + " exactSecond=" + IsExactHit(second)
                    + " gap=" + (second == null ? -1 : Math.Abs(second.Deviation - best.Deviation)).ToString("0.####", CultureInfo.InvariantCulture)
                    + " split=" + (second != null && Math.Abs(second.Deviation - best.Deviation) <= AmbiguityBand)
                    + " nearTie=" + (second != null && second.Deviation <= best.Deviation + 0.02)
                    + " [band=" + AmbiguityBand + "]");
            if (best == null || best.Deviation > Tolerance) return Custom(diameterMm);
            // 判"说不准"：次选也在容差内、与首选是不同的规格/孔型（同一个规格的底孔/内小径
            // 是同一种叫法，不算两种解读），而且两者**几乎一样近**（差 ≤0.05mm）。
            // 为什么门槛卡在 0.05：Φ3.45 离 M3 过孔 3.4 是 0.05、离 M4 底孔 3.3 是 0.15，
            // 谁都说得通 -> 提示；而 Φ3.4 离 M3 过孔 0（正好是标准值）、离 M4 底孔 0.1，
            // 首选明显更合理 -> 不提示。这样"每个标准孔都弹提示"的噪音就没有了。
            double gap = second == null ? double.MaxValue : Math.Abs(second.Deviation - best.Deviation);
            bool differentReading = second != null
                && (second.Row.Size != best.Row.Size || second.ReferenceIsTapped != best.ReferenceIsTapped);
            if (differentReading && second.Deviation <= Tolerance && gap <= AmbiguityBand) {
                best.Ambiguous = true;
                best.AmbiguousDeviation = second.Deviation;
                best.AmbiguousWith = second.Row.Size + (second.ReferenceIsTapped ? " 的螺纹底孔（配沉孔）" : " 的过孔（配螺纹孔）");
            }
            return best;
        }

        // 只在「不同规格 / 不同孔型」之间比"首选 / 次选"。
        // 同一规格重复命中（例如 M3 的 TapDrill 2.5 与 MinorDia 2.459 都靠近 2.5）不算歧义——
        // 它给出的目标孔是一样的，比出歧义只会误报。
        static void Consider(ref HoleMatch best, ref double bestDev, ref HoleMatch second, ref double secondDev,
                             ThreadRow r, double standard, bool isTap, double actual){
            double dev = Math.Abs(actual - standard);
            if (best != null && SameFamily(best, r, isTap) && dev >= bestDev) return;
            if (second != null && SameFamily(second, r, isTap) && dev >= secondDev) return;
            var cand = new HoleMatch { Row = r, HasRow = true, ReferenceIsTapped = isTap, ReferenceDiameter = actual, Deviation = dev,
                                       MatchedStandard = standard, MatchedIsTap = isTap,
                                       Target = Build(r, isTap, actual) };
            if (best == null || dev < bestDev) {
                if (best != null && !SameFamily(best, r, isTap)) { second = best; secondDev = bestDev; }
                best = cand; bestDev = dev; return;
            }
            // 到这里 dev >= bestDev。只有"比现有次选更好"才顶替它 ——
            // 漏了 dev < secondDev 这一句，次选会被最后一个候选（M20）顶掉，歧义永远判不出来。
            if (SameFamily(best, r, isTap)) return;
            if (dev < secondDev) { second = cand; secondDev = dev; }
        }
        static bool SameFamily(HoleMatch m, ThreadRow r, bool isTap){
            return m.Row.Size == r.Size && m.ReferenceIsTapped == isTap;
        }
        // 实测直径是否正好等于命中的那个标准值（精确命中）
        static bool IsExactHit(HoleMatch m){
            return m != null && Math.Abs(m.ReferenceDiameter - m.MatchedStandard) < 1e-6;
        }
        // 仅供探针/诊断用：把"首选/次选"摊开看
        internal static string Explain(double diameterMm){
            var m = Match(diameterMm);
            return "Φ" + diameterMm.ToString("0.###", CultureInfo.InvariantCulture)
                + " -> " + (m.HasRow ? m.Row.Size + (m.ReferenceIsTapped ? " 底孔" : " 过孔") : "自定义")
                + " dev=" + m.Deviation.ToString("0.####", CultureInfo.InvariantCulture)
                + (m.Ambiguous ? " [歧义: " + m.AmbiguousWith + " dev=" + m.AmbiguousDeviation.ToString("0.####", CultureInfo.InvariantCulture) + "]" : "");
        }

        // 参考孔是螺纹底孔 -> 目标件配沉孔；参考孔是过孔 -> 目标件配螺纹孔。
        static HoleSpec Build(ThreadRow r, bool referenceIsTapped, double actual){
            if (referenceIsTapped) {
                var s = new HoleSpec { Kind = HoleKind.Counterbore, ThreadSize = r.Size,
                    HoleDiameter = r.ClearanceMid, CounterboreDiameter = r.CounterboreDia, CounterboreDepth = r.CounterboreDep, Depth = 0 };
                s.Note = "参考孔 Φ" + actual.ToString("0.##",CultureInfo.InvariantCulture) + " 是 " + r.Size + " 的螺纹底孔，目标件配做沉孔。";
                return s;
            }
            var t = new HoleSpec { Kind = HoleKind.Tapped, ThreadSize = r.Size, HoleDiameter = r.MinorDia, Depth = 0 };
            t.Note = "参考孔 Φ" + actual.ToString("0.##",CultureInfo.InvariantCulture) + " 是 " + r.Size + " 的过孔，目标件配做螺纹孔。";
            return t;
        }

        // 未命中标准表：原样复制参考孔径做通孔，并明确告知。
        public static HoleMatch Custom(double diameterMm){
            var s = new HoleSpec { Kind = HoleKind.Through, HoleDiameter = Math.Round(diameterMm,2), Depth = 0 };
            s.Note = "Φ" + diameterMm.ToString("0.##",CultureInfo.InvariantCulture) + " 未命中标准螺纹表，按同直径通孔处理。";
            return new HoleMatch { HasRow = false, ReferenceDiameter = diameterMm, Target = s,
                                   Message = "未匹配到标准规格，按同径通孔处理" };
        }

        // 参考孔直径落在容差边缘、两个规格都说得通时的提示语。
        public const string AmbiguousHint = "这个直径落在容差边缘，请手动确认规格";

        // 把匹配结果翻译成状态栏那句话。
        public static string StatusLine(HoleMatch m){
            if (m == null) return "";
            if (!m.HasRow) return "参考孔直径：" + N(m.ReferenceDiameter) + "　未匹配到标准规格";
            string kind = m.Target.Kind == HoleKind.Counterbore ? "沉孔" : (m.Target.Kind == HoleKind.Tapped ? "螺纹孔" : "通孔");
            string line = "参考孔直径：" + N(m.ReferenceDiameter) + "　已匹配到 " + m.Target.ThreadSize + " " + kind + "！";
            if (m.Ambiguous)
                line += "　⚠ " + AmbiguousHint + "：也可能是 " + m.AmbiguousWith
                      + "（偏差 " + N(m.AmbiguousDeviation) + "mm），自动推断不保证正确。";
            return line;
        }
        static string N(double v){ return v.ToString("0.##",CultureInfo.InvariantCulture); }

        // 锥形沉孔默认锥孔直径：按螺钉头径（= 2×头半径）取，90° 沉头螺钉正好沉到与表面齐平。
        // 表里没填头半径时退回"沉孔直径 + 1"，并仍然是显式标注过的估算值。
        public static double CountersinkDiameterFor(ThreadRow r){
            return r.HeadRadius > 0 ? Math.Round(2.0 * r.HeadRadius, 2) : r.CounterboreDia + 1.0;
        }

        // 手工指定规格（用户在界面上改页签/改规格时用）。
        public static HoleSpec FromRow(ThreadRow r, HoleKind kind){
            switch (kind) {
                case HoleKind.Tapped:
                    return new HoleSpec { Kind = HoleKind.Tapped, ThreadSize = r.Size, HoleDiameter = r.MinorDia, Depth = 0 };
                case HoleKind.Counterbore:
                    return new HoleSpec { Kind = HoleKind.Counterbore, ThreadSize = r.Size, HoleDiameter = r.ClearanceMid,
                                          CounterboreDiameter = r.CounterboreDia, CounterboreDepth = r.CounterboreDep, Depth = 0 };
                case HoleKind.Countersink: {
                    double cd = CountersinkDiameterFor(r);
                    return new HoleSpec { Kind = HoleKind.Countersink, ThreadSize = r.Size, HoleDiameter = r.ClearanceMid,
                                          CountersinkDiameter = cd, CountersinkAngle = 90, Depth = 0,
                                          Note = "锥孔Φ" + N(cd) + " 按 " + r.Size + " 沉头螺钉头径 2×" + N(r.HeadRadius)
                                               + " 估算（90°），请按实际螺钉核对。" };
                }
                default:
                    return new HoleSpec { Kind = HoleKind.Through, ThreadSize = r.Size, HoleDiameter = r.ClearanceMid, Depth = 0 };
            }
        }
    }
}
