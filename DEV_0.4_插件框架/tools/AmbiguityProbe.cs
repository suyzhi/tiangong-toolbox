using System;
using System.Globalization;
using TianGongCadSuite;

// 诊断：扫一遍 3.0~14.0mm（每 0.05）看哪些直径会被判"歧义"，确认提示频率可接受。
class AmbiguityProbe {
    [STAThread] static int Main(string[] args){
        Console.WriteLine("Tolerance=" + HoleMatcher.Tolerance + " band=" + HoleMatcher.AmbiguityBand);
        foreach (double d in new double[]{ 3.3, 3.4, 3.45, 3.5, 3.6, 5.0, 6.6, 8.4, 8.5, 12.7, 13.5 }) {
            var m = HoleMatcher.Match(d);
            Console.WriteLine("d=" + d.ToString("0.###", CultureInfo.InvariantCulture)
                + " -> " + (m.HasRow ? m.Row.Size : "(custom)")
                + " tap=" + m.ReferenceIsTapped
                + " dev=" + m.Deviation.ToString("0.####", CultureInfo.InvariantCulture)
                + " ambiguous=" + m.Ambiguous
                + (m.Ambiguous ? " with=" + m.AmbiguousWith + " ambDev=" + m.AmbiguousDeviation.ToString("0.####", CultureInfo.InvariantCulture) : ""));
        }
        int n = 0, amb = 0;
        for (double d = 3.0; d <= 14.0001; d += 0.05) {
            var m = HoleMatcher.Match(Math.Round(d, 2));
            if (m.HasRow) { n++; if (m.Ambiguous) amb++; }
        }
        Console.WriteLine("区间 3.0~14.0 每 0.05 取样：命中标准表 " + n + " 个，其中提示歧义 " + amb + " 个 (" + (100.0*amb/n).ToString("0.#") + "%)");
        return 0;
    }
}
