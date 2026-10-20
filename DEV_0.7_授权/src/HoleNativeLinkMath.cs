using System;
using System.Collections.Generic;

namespace TianGongCadSuite {
    // 天工原生关联（HoleNativeLink）里不碰 CAD 的那部分：矩阵串接、认实例、在复制来的圆边里挑"就是这个孔"的那一条。
    // tests/AutoHoleLinkPureTests.cs 直接断言。
    public static class NativeLinkMath {
        // outer ∘ inner：先 inner 再 outer（子装配里实例的矩阵是相对父装配的，逐级乘到顶层）
        public static Transform Compose(Transform outer, Transform inner){
            if (outer == null) return inner;
            if (inner == null) return outer;
            var o = outer.Point(new V3(inner.M[12], inner.M[13], inner.M[14]));
            var x = outer.Vector(new V3(inner.M[0], inner.M[1], inner.M[2]));
            var y = outer.Vector(new V3(inner.M[4], inner.M[5], inner.M[6]));
            var z = outer.Vector(new V3(inner.M[8], inner.M[9], inner.M[10]));
            return Transform.Frame(o, x, y, z);
        }

        // 两个零件局部→装配的变换是不是同一个（平移按米比，1e-6 = 0.001mm；旋转分量同一容差）
        public static bool Same(Transform a, Transform b){
            if (a == null || b == null) return false;
            for (int i = 0; i < 16; i++) if (Math.Abs(a.M[i] - b.M[i]) > 1e-6) return false;
            return true;
        }

        // 复制来的一条圆边（打孔零件局部坐标）
        public struct Circle {
            public V3 Centre, Axis; public double Radius;
            public Circle(V3 centre, V3 axis, double radius){ Centre = centre; Axis = axis; Radius = radius; }
        }

        // 只有轴线和打孔面法向几乎平行的孔才走原生关联：孔心约束在"圆边投影到草图面上的圆心"，
        // 斜孔的投影圆心不等于轴线和面的交点（那种孔照旧走属性关联 + 同步孔位）。
        public const double ParallelCos = 0.9998;
        // 圆心投影到打孔面上以后离要打的孔心多远算"就是这个孔"（米）。
        public const double MatchTolM = 0.00005;

        // 在 circles 里找轴线 ∥ normal、投影到打孔面后圆心落在 centre 上的那条；没有返回 -1。
        // 有几条都对得上（孔两端的圆、沉孔的台阶圆）取投影误差最小的 —— 约束的是圆心，半径无所谓。
        public static int Match(IList<Circle> circles, V3 centre, V3 normal, out double offsetM){
            offsetM = double.MaxValue;
            if (circles == null) return -1;
            V3 n;
            try { n = normal.Unit(); } catch { return -1; }
            int best = -1;
            for (int i = 0; i < circles.Count; i++) {
                V3 a;
                try { a = circles[i].Axis.Unit(); } catch { continue; }
                if (Math.Abs(a.Dot(n)) < ParallelCos) continue;
                var d = circles[i].Centre - centre;
                var inPlane = d - n * d.Dot(n);
                double off = inPlane.Length;
                if (off <= MatchTolM && off < offsetM) { offsetM = off; best = i; }
            }
            return best;
        }

        // 参考孔轴线和打孔面法向是不是平行到可以走原生关联
        public static bool Perpendicular(V3 holeAxis, V3 faceNormal){
            try { return Math.Abs(holeAxis.Unit().Dot(faceNormal.Unit())) >= ParallelCos; } catch { return false; }
        }
    }
}
