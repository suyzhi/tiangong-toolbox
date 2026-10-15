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
        public double EdgeMm = 10;       // 起端边距：第一个孔离起点那条边的距离
        // 末端边距：最后一个孔离终点那条边的距离。< 0 表示"和起端一样"（原来只有一个边距，两端对称）。
        // 型材上经常要"一头 20、一头 35"（避开另一根型材的连接件），所以两端分开填。
        public double EndEdgeMm = -1;
        // 横向偏移：孔心离面中线的距离，0 = 居中；正值偏向 FaceFrame.Perp 那一侧（预览图上方）。
        // 型材上的孔经常不在正中（例如靠近一侧的槽口），原来只能居中。
        public double LateralMm = 0;
        public double StartAngleDeg = 0; // 圆周模式的起始角

        public double EndEdge { get { return EndEdgeMm < 0 ? EdgeMm : EndEdgeMm; } }
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
            double a = spec.EdgeMm, b = spec.EndEdge;
            if (a < 0 || b < 0) throw new ArgumentException("边距不能为负数。");
            if (a + b >= lengthMm - Tol) throw new ArgumentException("边距过大，排孔方向剩余长度不足。");
            double usable = lengthMm - a - b;

            if (spec.Kind == HolePatternKind.Divide) {
                if (spec.Count < 1) throw new ArgumentException("孔数必须至少为 1。");
                if (spec.Count == 1) return new double[]{ a + usable / 2 };
                var r = new double[spec.Count];
                for (int i = 0; i < spec.Count; i++) r[i] = a + usable * i / (spec.Count - 1);
                return r;
            }
            if (spec.Kind == HolePatternKind.Pitch) {
                if (double.IsNaN(spec.PitchMm) || spec.PitchMm <= Tol) throw new ArgumentException("间距必须大于 0。");
                int n = (int)Math.Floor(usable / spec.PitchMm + Tol) + 1;
                if (n < 1) n = 1;
                if (n > 500) throw new ArgumentException("按此间距会排出超过 500 个孔，请加大间距或减小边距。");
                var r = new double[n];
                for (int i = 0; i < n; i++) r[i] = a + i * spec.PitchMm;
                return r;
            }
            throw new ArgumentException("圆周均布不使用沿线排布。");
        }

        // 横向偏移是否还在面内：孔（或腰孔的半宽）不能伸出面的两条长边。
        // widthMm：面宽；holeHalfMm：孔半径（腰孔为槽宽一半）。返回空串 = 没问题。
        public static string LateralProblem(double widthMm, double lateralMm, double holeHalfMm){
            if (double.IsNaN(lateralMm) || double.IsInfinity(lateralMm)) return "横向偏移不是有效数字。";
            if (widthMm <= Tol) return "";   // 面宽读不出来（例如圆面）：不拦，交给 CAD
            double room = widthMm / 2 - Math.Abs(lateralMm) - Math.Max(0, holeHalfMm);
            if (room < -Tol) return "横向偏移 " + N(lateralMm) + "mm 太大：孔会伸出面外（面宽 " + N(widthMm) + "mm）。";
            return "";
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
            double usable = lengthMm - spec.EdgeMm - spec.EndEdge;
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
            string edges = Math.Abs(spec.EdgeMm - spec.EndEdge) < 1e-9 ? "边距 " + N(spec.EdgeMm) + "mm" : "边距 " + N(spec.EdgeMm) + " / " + N(spec.EndEdge) + "mm";
            string s = "沿 " + N(lengthMm) + "mm " + how + " 排 " + o.Length + " 孔，" + edges;
            if (spec.Kind == HolePatternKind.Pitch && o.Length > 0) {
                double tail = lengthMm - o[o.Length - 1];   // 定距排不满时，末端实际剩多少
                if (Math.Abs(tail - spec.EndEdge) > 0.01) s += "（末端实际余 " + N(tail) + "mm）";
            }
            return s;
        }
        static string N(double v){ return v.ToString("0.##", CultureInfo.InvariantCulture); }
    }

    // 「种子孔 + 阵列」的参数：一排孔心（零件局部，米）能不能用一个阵列特征做出来，阵列参数是多少。
    // 天工接口的约定（真机实测 tools/hole-feature-probe/HoleEditProbe.cs X6/X7，2026-10-14）：
    //   · 矩形阵列 AddByRectangular 的 RectangleAngle 是**弧度**，从基准面的参考方向（GetReferenceDirection）
    //     绕基准面法向**逆时针**量；第一个实例就是种子孔；
    //   · 圆周阵列 AddByCircular 的 AngleSpacing 也是弧度，正值 = 绕基准面法向逆时针，AxisPoint 传 3D 点。
    // 求出来的方向只是"按约定应当如此"——CAD 层建完阵列还会逐个核对孔心，对不上就换方向或退回逐个打孔。
    // 纯数学，无 CAD 依赖。
    public static class PatternFit {
        public const double TolM = 1e-6;   // 0.001mm：孔心是我们自己算的，等距误差只有浮点噪声

        // 等距直线排布：pts[i] = pts[0] + i × spacing × 方向。成立时给出间距（米）和
        // 方向相对参考方向 refDir 绕法向 normal 的角度（弧度，逆时针为正）。
        public static bool Linear(IList<V3> pts, V3 normal, V3 refDir, out double spacing, out double angle){
            spacing = 0; angle = 0;
            if (pts == null || pts.Count < 2) return false;
            var step = pts[1] - pts[0];
            spacing = step.Length;
            if (spacing < 1e-5) return false;
            for (int i = 2; i < pts.Count; i++)
                if ((pts[i] - (pts[0] + step * i)).Length > TolM) return false;
            V3 n, u;
            if (!Frame(normal, refDir, out n, out u)) return false;
            var d = step * (1 / spacing);
            if (Math.Abs(d.Dot(n)) > 1e-6) return false;          // 方向不在基准面里
            var v = n.Cross(u);
            angle = Math.Atan2(d.Dot(v), d.Dot(u));
            return true;
        }

        // 圆周均布：所有孔心在以 axisPoint 为圆心、法向 normal 的同一个圆上，相邻两个转过同一个角度。
        // 成立时给出每步转角（弧度，绕法向逆时针为正）；axisOnPlane = 圆心投影到孔心所在平面上的点。
        public static bool Circular(IList<V3> pts, V3 axisPoint, V3 normal, out double step, out V3 axisOnPlane){
            step = 0; axisOnPlane = axisPoint;
            if (pts == null || pts.Count < 2) return false;
            V3 n;
            try { n = normal.Unit(); } catch { return false; }
            axisOnPlane = axisPoint - n * (axisPoint - pts[0]).Dot(n);
            double r = (pts[0] - axisOnPlane).Length;
            if (r < 1e-5) return false;
            for (int i = 0; i < pts.Count; i++) {
                var rel = pts[i] - axisOnPlane;
                if (Math.Abs(rel.Dot(n)) > TolM || Math.Abs(rel.Length - r) > TolM) return false;
            }
            step = Turn(pts[0] - axisOnPlane, pts[1] - axisOnPlane, n);
            if (Math.Abs(step) < 1e-6) return false;
            for (int i = 2; i < pts.Count; i++) {
                double s = Turn(pts[i - 1] - axisOnPlane, pts[i] - axisOnPlane, n);
                if (Math.Abs(s - step) * r > TolM) return false;
            }
            return true;
        }

        // a 转到 b 的角度（绕 n，逆时针为正，-π..π）
        public static double Turn(V3 a, V3 b, V3 n){ return Math.Atan2(a.Cross(b).Dot(n), a.Dot(b)); }

        // 法向 + 参考方向 → 单位法向 + 在平面里的单位参考方向（参考方向不严格垂直时投影一下）
        static bool Frame(V3 normal, V3 refDir, out V3 n, out V3 u){
            n = new V3(0, 0, 1); u = new V3(1, 0, 0);
            try {
                n = normal.Unit();
                u = (refDir - n * refDir.Dot(n)).Unit();
                return true;
            } catch { return false; }
        }
    }
}
