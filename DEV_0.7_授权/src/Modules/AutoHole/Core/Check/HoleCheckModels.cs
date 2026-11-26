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
        // 展开 X/Y/Z 标量计算，避免热点循环里每次求交在托管堆上分配 4 个 double[3] 数组。
        public bool Clip(V3 o, V3 d, out double t0, out double t1){
            t0 = double.NegativeInfinity; t1 = double.PositiveInfinity;
            if (!ClipAxis(o.X, d.X, Lo.X, Hi.X, ref t0, ref t1)) return false;
            if (!ClipAxis(o.Y, d.Y, Lo.Y, Hi.Y, ref t0, ref t1)) return false;
            if (!ClipAxis(o.Z, d.Z, Lo.Z, Hi.Z, ref t0, ref t1)) return false;
            return true;
        }
        static bool ClipAxis(double o, double d, double lo, double hi, ref double t0, ref double t1){
            if (Math.Abs(d) < 1e-12) return o >= lo - 1e-9 && o <= hi + 1e-9;
            double inv = 1.0 / d;
            double a = (lo - o) * inv, b = (hi - o) * inv;
            if (a > b) { double tmp = a; a = b; b = tmp; }
            if (a > t0) t0 = a;
            if (b < t1) t1 = b;
            return t0 <= t1 + 1e-12;
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
        public string Done = "";          // 「去配孔」以后打上了：写一句（"已配 2 个孔"），列表里标出来（0.8.5）
        public int Places = 1;            // 合并了几处一模一样的问题（HoleCheck.Merge）
        // 配错孔 / 孔偏了：提示语说的是哪几个零件（「不查的零件」按它们判，不按整串孔 —— 串里常带着别的零件）
        public List<string> Parts = new List<string>();
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

    // 配孔检查的判定口径。原来是写死的常量；现在窗口里「判定口径」可以改，存在
    // %LOCALAPPDATA%\TianGongCadSuite\hole-check.tsv（一行一项：名字 \t 值），默认值和原来一样。
    public sealed class HoleCheckOptions {
        public const string FileName = "hole-check.tsv";
        public double MinDiameterMm = 3.0;     // 小于这个直径不当作连接孔
        public double MaxDiameterMm = 25.0;
        public double AxisAngleTolDeg = 2.0;   // 轴线方向容差
        public double AxisOffsetTolMm = 0.2;   // 同轴偏移容差（超过即"孔偏了"）
        public double CoaxialSpanTolMm = 3.0;  // 沿轴线的间隙不超过这个才认为挨着（成对 / 漏打孔判定的范围）
        // 第 4 轮：不列「名字里含这些字」的零件内部的问题（外购件：FESTO 气缸、BOSCH 导轨、LEUZE 传感器……）。逗号 / 空格 / 顿号隔开，默认空。
        // SolidWorks 转过来的 NOK_Drawer3 实测 340 条里大半在外购件子装配里（气缸活塞杆、接头内部的孔），用户改不了外购件，只会被淹没。
        // 不默认一刀切：支架上真漏了装气缸的孔（孔在气缸上、缺的是支架）照样报，见 HoleCheck.Skipped
        public string SkipNames = "";

        public HoleCheckOptions Clone(){ return (HoleCheckOptions)MemberwiseClone(); }

        public string Problem(){
            if (!(MinDiameterMm > 0) || !(MaxDiameterMm > MinDiameterMm)) return "孔径范围要满足 0 < 最小 < 最大。";
            if (!(AxisAngleTolDeg > 0 && AxisAngleTolDeg < 30)) return "方向容差要在 0～30° 之间。";
            if (!(AxisOffsetTolMm > 0)) return "同轴偏差容差要大于 0。";
            if (!(CoaxialSpanTolMm >= 0)) return "轴向间隙不能为负。";
            return "";
        }

        const string SkipKey = "不查名字含";
        // 拆成一个个关键字（去重，忽略大小写）
        public string[] SkipWords(){
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<string>();
            foreach (var w in (SkipNames ?? "").Split(new[]{ ',', '，', ' ', '　', '、', ';', '；', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)) {
                string t = w.Trim();
                if (t.Length > 0 && seen.Add(t)) list.Add(t);
            }
            return list.ToArray();
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
                if (r[0] == SkipKey) { o.SkipNames = r[1]; continue; }
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
            rows.Add(new[]{ SkipKey, string.Join(" ", SkipWords()) });
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
                (SkipWords().Length > 0 ? "· 名字（含所在子装配名）里有「" + string.Join("、", SkipWords()) + "」的零件：它们内部的问题不列出；孔在它们上、缺孔的是别的零件照样报。\r\n" : "") +
                "（点「判定口径」可以改这些数）";
        }
        static string N(double v){ return v.ToString("0.###", CultureInfo.InvariantCulture); }
    }
}
