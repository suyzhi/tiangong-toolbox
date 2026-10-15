using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace TianGongCadSuite {
    // 从孔特征上回读出来的原始值（全部毫米/度，枚举按名字给）。CAD 层负责读，这里只管翻译。
    public sealed class HoleDataValues {
        public string HoleType = "";         // igRegularHole / igCounterboreHole / igCountersinkHole / ...
        public string Treatment = "";        // igTappedHole / igNone ...
        public string ThreadDescription = "";
        public string Size = "";             // 天工孔标准表里的「大小」（M6 等），没有为空
        public double HoleDiameter, CounterboreDiameter, CounterboreDepth, CountersinkDiameter, CountersinkAngle;
        public bool Through = true;
        public double Depth;                 // 盲孔深度，贯通时不用
        public string ThreadDepthMethod = ""; // igThroughAll / igFinite
        public double ThreadDepth;
        public double BottomAngle;           // 盲孔孔底角度（度），平底为 0
    }

    // 「改孔型」窗口打开时，按选中的孔现在是什么来填界面；改完以后按规格重新命名。
    // 插件打的螺纹孔是"普通孔 + 螺纹数据"（AutoHoleWriter.BuildHoleData 第 2 条），所以螺纹孔要看处理方式/螺纹描述，
    // 不能只看孔型。纯逻辑：tests 直接断言。
    public static class HoleSpecRead {
        static readonly Regex Metric = new Regex(@"M\d+(\.\d+)?", RegexOptions.IgnoreCase);

        public static HoleSpec From(HoleDataValues v){
            if (v == null) throw new ArgumentNullException("v");
            var s = new HoleSpec();
            bool tapped = v.Treatment == "igTappedHole" || (v.ThreadDescription ?? "").Trim().Length > 0;
            if (v.HoleType == "igCounterboreHole") s.Kind = HoleKind.Counterbore;
            else if (v.HoleType == "igCountersinkHole") s.Kind = HoleKind.Countersink;
            else s.Kind = tapped ? HoleKind.Tapped : HoleKind.Through;
            s.HoleDiameter = Round(v.HoleDiameter);
            s.Depth = v.Through ? 0 : Round(v.Depth);
            if (!v.Through && v.BottomAngle > 0.5) { s.Bottom = HoleBottom.VBottom; s.BottomAngle = Round(v.BottomAngle); }
            s.CounterboreDiameter = Round(v.CounterboreDiameter);
            s.CounterboreDepth = Round(v.CounterboreDepth);
            s.CountersinkDiameter = Round(v.CountersinkDiameter);
            s.CountersinkAngle = v.CountersinkAngle > 0 ? Round(v.CountersinkAngle) : 90;
            s.ThreadSize = MetricSize(s.Kind == HoleKind.Tapped ? v.ThreadDescription : "");
            if (s.ThreadSize.Length == 0) s.ThreadSize = MetricSize(v.Size);
            if (s.Kind == HoleKind.Tapped) {
                s.ThreadFull = v.ThreadDepthMethod != "igFinite";
                s.ThreadDepth = s.ThreadFull ? HoleSpec.DefaultThreadDepth(s.ThreadSize) : Round(v.ThreadDepth);
            }
            return s;
        }

        // "M6" / "M6x1" / "GB M6 普通" → "M6"；认不出返回空
        public static string MetricSize(string text){
            if (string.IsNullOrEmpty(text)) return "";
            var m = Metric.Match(text);
            return m.Success ? "M" + m.Value.Substring(1) : "";
        }

        // 回读值有浮点噪声（6.6 → 6.6000000001），界面和命名按 3 位小数
        static double Round(double v){ return double.IsNaN(v) || double.IsInfinity(v) ? 0 : Math.Round(v, 3); }
    }
}
