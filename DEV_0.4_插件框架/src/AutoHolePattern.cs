using System;
using System.Collections.Generic;
using System.Globalization;

namespace TianGongCadSuite {
    public enum HolePatternKind { Divide, Pitch, Circular }

    // 排孔参数（全部毫米/度）。默认值刻意选成"拿来就能用"。
    public sealed class HolePatternSpec {
        public HolePatternKind Kind = HolePatternKind.Divide;
        public int Count = 2;            // 等分/圆周的孔数
        public double PitchMm = 50;      // 定距模式的间距
        public double EdgeMm = 10;       // 距两端（或边界）的边距
        public double StartAngleDeg = 0; // 圆周模式的起始角
    }

    // 排孔位置求解。纯数学，无 CAD 依赖。
    public static class HolePatternSolver {
        public const double Tol = 1e-9;

        // 沿线排布：返回沿长度方向、从起点算起的偏移（毫米）。
        // 起点/终点由调用方给出（通常是打孔面上最长边或用户选的边）。
        public static double[] Offsets(double lengthMm, HolePatternSpec spec){
            if (spec == null) throw new ArgumentNullException("spec");
            if (double.IsNaN(lengthMm) || double.IsInfinity(lengthMm) || lengthMm <= Tol)
                throw new ArgumentException("排孔方向的长度必须大于 0。");
            if (spec.EdgeMm < 0) throw new ArgumentException("边距不能为负数。");
            if (spec.EdgeMm * 2 >= lengthMm - Tol) throw new ArgumentException("边距过大，排孔方向剩余长度不足。");
            double usable = lengthMm - 2 * spec.EdgeMm;

            if (spec.Kind == HolePatternKind.Divide) {
                if (spec.Count < 1) throw new ArgumentException("孔数必须至少为 1。");
                if (spec.Count == 1) return new double[]{ spec.EdgeMm + usable / 2 };
                var r = new double[spec.Count];
                for (int i = 0; i < spec.Count; i++) r[i] = spec.EdgeMm + usable * i / (spec.Count - 1);
                return r;
            }
            if (spec.Kind == HolePatternKind.Pitch) {
                if (double.IsNaN(spec.PitchMm) || spec.PitchMm <= Tol) throw new ArgumentException("间距必须大于 0。");
                int n = (int)Math.Floor(usable / spec.PitchMm + Tol) + 1;
                if (n < 1) n = 1;
                if (n > 500) throw new ArgumentException("按此间距会排出超过 500 个孔，请加大间距或减小边距。");
                var r = new double[n];
                for (int i = 0; i < n; i++) r[i] = spec.EdgeMm + i * spec.PitchMm;
                return r;
            }
            throw new ArgumentException("圆周均布不使用沿线排布。");
        }

        // 圆周均布：返回角度（度）。0 度指向 +X。
        public static double[] Angles(HolePatternSpec spec){
            if (spec == null) throw new ArgumentNullException("spec");
            if (spec.Kind != HolePatternKind.Circular) throw new ArgumentException("该排布不是圆周均布。");
            if (spec.Count < 2) throw new ArgumentException("圆周均布至少需要 2 个孔。");
            if (spec.Count > 720) throw new ArgumentException("圆周孔数过多（上限 720）。");
            var r = new double[spec.Count];
            double step = 360.0 / spec.Count;
            for (int i = 0; i < spec.Count; i++) r[i] = spec.StartAngleDeg + i * step;
            return r;
        }

        // 定距模式实际会排几个孔——界面用它做实时预览。
        public static int PitchCount(double lengthMm, HolePatternSpec spec){
            if (spec == null || spec.Kind != HolePatternKind.Pitch) return 0;
            double usable = lengthMm - 2 * spec.EdgeMm;
            if (usable < -Tol || spec.PitchMm <= Tol) return 0;
            return (int)Math.Floor(usable / spec.PitchMm + Tol) + 1;
        }

        public static string Describe(double lengthMm, HolePatternSpec spec){
            if (spec.Kind == HolePatternKind.Circular) {
                var a = Angles(spec);
                return "圆周均布 " + a.Length + " 孔，起始 " + N(spec.StartAngleDeg) + "°，间隔 " + N(360.0 / spec.Count) + "°";
            }
            var o = Offsets(lengthMm, spec);
            string how = spec.Kind == HolePatternKind.Divide ? "等分" : ("定距 " + N(spec.PitchMm) + "mm");
            return "沿 " + N(lengthMm) + "mm " + how + " 排 " + o.Length + " 孔，边距 " + N(spec.EdgeMm) + "mm";
        }
        static string N(double v){ return v.ToString("0.##", CultureInfo.InvariantCulture); }
    }
}
