using System;
using System.Collections.Generic;

namespace TianGongCadSuite {
    // 零件上"哪些位置已经有孔"（0.7.10）：实体的圆边读一遍记下来，之后每个孔位只在内存里比。
    //
    // 原来每打一个孔都把实体的全部边从 CAD 里读一遍（AutoHoleWriter.ExistingHoleAt），打孔前一遍、DrillOne 里又一遍、
    // 合成后核对每个孔位再一遍 —— 孔越打越多边越多，20 个孔就是几十遍全量读边（O(孔数 × 边数) 次 COM 调用）。
    // 现在一张面读一遍（O(边数)），这批里要打的孔也记进来（Plan），同一个位置重复的请求照样认得出来。
    // 判定口径和原来一样：圆的轴线和打孔方向平行（|cos| ≥ 0.99），圆心到孔轴线的径向距离 < 0.5mm；
    // 横着的孔（圆心恰好落在这根轴线上）不算（用户围栏 FenceHoleE2E 第 4 条）。
    public sealed class HoleSpots {
        public const double RadialTolM = 0.0005;
        readonly List<NativeLinkMath.Circle> circles = new List<NativeLinkMath.Circle>();

        public int Count { get { return circles.Count; } }

        // 实体上读到的一条圆边（零件局部坐标，米）
        public void Add(V3 centre, V3 axis, double radius){
            if (radius <= 0 || !centre.Finite || axis.Length < 1e-12) return;
            circles.Add(new NativeLinkMath.Circle(centre, axis.Unit(), radius));
        }
        // 这批里准备打的孔：后面同一位置的请求算"已经有孔"
        public void Plan(V3 centre, V3 normal, double diameterMm){ Add(centre, normal, diameterMm / 2000.0); }

        // 孔心 localCentre、打孔方向 normal 上有没有孔；有的话给出（按读到的顺序第一条）圆的直径 mm
        public bool At(V3 localCentre, V3 normal, out double diameterMm){
            diameterMm = 0;
            V3 n = normal.Length > 1e-12 ? normal.Unit() : new V3(0, 0, 1);
            foreach (var c in circles) {
                if (Math.Abs(c.Axis.Dot(n)) < 0.99) continue;
                V3 delta = c.Centre - localCentre;
                double axial = delta.Dot(n);
                if ((delta - n * axial).Length < RadialTolM) { diameterMm = c.Radius * 2000.0; return true; }
            }
            return false;
        }

        // 把找到的孔心（found，读不出的为 null）一一配给想要的孔位（wanted）：每个孔位取离它最近、还没配出去的那个，
        // 距离超过 tolM 不配。返回每个孔位配到的 found 下标，没配上为 -1。O(N²) 纯内存比较，COM 只在调用方读一遍。
        public static int[] Assign(IList<V3?> found, IList<V3> wanted, double tolM){
            var pick = new int[wanted.Count];
            var used = new bool[found.Count];
            for (int k = 0; k < wanted.Count; k++) {
                int best = -1; double bestD = tolM;
                for (int i = 0; i < found.Count; i++) {
                    if (used[i] || !found[i].HasValue) continue;
                    double d = (found[i].Value - wanted[k]).Length;
                    if (d < bestD) { bestD = d; best = i; }
                }
                pick[k] = best;
                if (best >= 0) used[best] = true;
            }
            return pick;
        }
    }
}
