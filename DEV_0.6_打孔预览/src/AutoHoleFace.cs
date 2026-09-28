using System;
using System.Collections.Generic;
using G=SolidEdgeGeometry;
using S=SolidEdgeFrameworkSupport;
using P=SolidEdgePart;

namespace TianGongCadSuite {
    // 打孔面的局部坐标系：一个起点、一个沿面方向、以及该方向的可用长度。
    // "无脑"的关键——用户只需要点一个面，方向自动取该面最长的直边。
    //
    // Origin 刻意放在"排孔方向的起点 + 面宽方向的正中"，而不是面的角点：
    // 放在角点会让所有孔都压在面的边线上，一半在材料外面（实测会 E_FAIL）。
    public sealed class FaceFrame {
        public V3 Origin;        // 装配坐标。沿排孔方向的起点，面宽方向居中。
        public V3 Direction;     // 单位向量，装配坐标，位于面内
        public V3 Perp;          // 单位向量，装配坐标，位于面内且垂直于 Direction
        public V3 Normal;        // 单位向量，装配坐标，面法向
        public double LengthMm;  // 沿 Direction 的可用长度（毫米）
        public double WidthMm;   // 沿 Perp 的面宽（毫米）
        public string Label = "";
        public V3 At(double offsetMm){ return Origin + Direction * (offsetMm * 0.001); }
    }

    public static class FaceFrameReader {
        // 取面上最长的直边作为排孔方向，孔心横向居中。
        public static FaceFrame Read(TargetFace target){
            if (target == null) throw new ArgumentNullException("target");
            var pts = new List<V3>();
            V3 bestDir = new V3(), bestP1 = new V3(); double bestLen = 0;
            Collect(target, pts, ref bestDir, ref bestP1, ref bestLen);
            if (bestLen <= 1e-9)
                throw new ArgumentException("这个面上找不到可用的直线边，无法确定排孔方向。请在面上选择一条边作为排孔方向。");
            return Build(target, pts, bestDir.Unit(), "自动取最长边");
        }

        // 用户显式指定一条边作为排孔方向。
        public static FaceFrame Read(TargetFace target, object edgeSelection){
            if (edgeSelection == null) return Read(target);
            var pg = PickGeometry.Unwrap(edgeSelection);
            var edge = pg.Geometry as G.Edge;
            if (edge == null) throw new ArgumentException("排孔方向请选择一条直线边。");
            if (!(edge.Geometry is G.Line)) throw new ArgumentException("排孔方向必须是直线边，不能是圆弧或曲线。");
            Array sp = new double[3], ep = new double[3];
            edge.GetEndPoints(ref sp, ref ep);
            var q1 = pg.Transform.Point(V3.From(sp));
            var q2 = pg.Transform.Point(V3.From(ep));
            var dir = q2 - q1;
            if (dir.Length < 1e-9) throw new ArgumentException("所选边长度为 0。");
            var pts = new List<V3>();
            V3 bd = new V3(), bp = new V3(); double bl = 0;
            Collect(target, pts, ref bd, ref bp, ref bl);
            return Build(target, pts, dir.Unit(), "手动指定边");
        }

        static void Collect(TargetFace target, List<V3> pts, ref V3 bestDir, ref V3 bestP1, ref double bestLen){
            foreach (G.Edge e in (G.Edges)target.Face.Edges) {
                Array sp = new double[3], ep = new double[3];
                try { e.GetEndPoints(ref sp, ref ep); } catch { continue; }
                var p1 = target.Placement.Point(V3.From(sp));
                var p2 = target.Placement.Point(V3.From(ep));
                pts.Add(p1); pts.Add(p2);
                if (!(e.Geometry is G.Line)) continue;
                double len = (p2 - p1).Length;
                if (len > bestLen) { bestLen = len; bestDir = p2 - p1; bestP1 = p1; }
            }
        }

        static FaceFrame Build(TargetFace target, List<V3> pts, V3 dir, string how){
            if (pts.Count < 2) throw new ArgumentException("这个面上读不到可用的边，无法排孔。");
            var normal = target.Plane.Normal.Unit();
            var perp = normal.Cross(dir);
            if (perp.Length < 1e-9) throw new ArgumentException("排孔方向与面法向平行，无法在面内排孔。");
            perp = perp.Unit();
            var basePoint = target.Plane.Point;
            double dmin = double.MaxValue, dmax = double.MinValue, pmin = double.MaxValue, pmax = double.MinValue;
            foreach (var q in pts) {
                var rel = q - basePoint;
                double d = rel.Dot(dir), p = rel.Dot(perp);
                if (d < dmin) dmin = d; if (d > dmax) dmax = d;
                if (p < pmin) pmin = p; if (p > pmax) pmax = p;
            }
            double length = dmax - dmin, width = pmax - pmin;
            if (length <= 1e-9) throw new ArgumentException("排孔方向长度为 0。");
            var f = new FaceFrame {
                Direction = dir, Perp = perp, Normal = normal,
                Origin = basePoint + dir * dmin + perp * ((pmin + pmax) / 2),
                LengthMm = length * 1000.0, WidthMm = width * 1000.0,
                Label = how + "（" + (length*1000).ToString("0.##") + " × " + (width*1000).ToString("0.##") + "mm）"
            };
            return f;
        }

        // 面的平面内包围盒（装配坐标下的面内 (u,v) 范围）+ 一组面内正交基。
        // 边遍历是要走 COM 的，而 Caller（实时预览）每改一个数字就会把 N×M 个孔心判一遍，
        // 所以算一次存进 TargetFace，后面只做两次点积。
        public sealed class FaceBounds {
            public V3 BasePoint; public V3 U; public V3 V;
            public double MinU, MaxU, MinV, MaxV;
            public double PlanePointDot;      // 缓存键：平面点沿法向的投影
            public double PlaneNormalKey;
            public bool Any;                  // 有没有读到过边
        }

        // 缓存键：同一张面每次重解析出来的 TargetFace 实例不同，但平面数据是同一份。
        static object BoundsKey(TargetFace target){
            var n = target.Plane.Normal.Unit();
            return new object[]{ Math.Round(target.Plane.Point.Dot(n), 9),
                                 Math.Round(n.X, 9), Math.Round(n.Y, 9), Math.Round(n.Z, 9) };
        }

        static FaceBounds Bounds(TargetFace target){
            var n = target.Plane.Normal.Unit();
            object key = BoundsKey(target);
            var cached = target.Bounds;
            if (cached != null && SameKey(cached, key)) return cached;

            var b = new FaceBounds { BasePoint = target.Plane.Point, Any = false };
            b.PlanePointDot = (double)((object[])key)[0];
            b.PlaneNormalKey = n.X;
            var seed = Math.Abs(n.X) < 0.9 ? new V3(1,0,0) : new V3(0,1,0);
            b.U = n.Cross(seed).Unit();
            b.V = n.Cross(b.U).Unit();
            b.MinU = b.MinV = double.MaxValue; b.MaxU = b.MaxV = double.MinValue;

            Action<V3> take = q => {
                var rel = q - b.BasePoint;
                double a = rel.Dot(b.U), c = rel.Dot(b.V);
                if (a < b.MinU) b.MinU = a; if (a > b.MaxU) b.MaxU = a;
                if (c < b.MinV) b.MinV = c; if (c > b.MaxV) b.MaxV = c;
                b.Any = true;
            };

            try {
                foreach (G.Edge e in (G.Edges)target.Face.Edges) {
                    // 直线：两个端点就够
                    Array sp = new double[3], ep = new double[3];
                    bool endpoints = false;
                    try { e.GetEndPoints(ref sp, ref ep); endpoints = true; } catch { }
                    if (endpoints) { take(target.Placement.Point(V3.From(sp))); take(target.Placement.Point(V3.From(ep))); }
                    // 圆/圆弧：GetEndPoints 对整圆会退化成一个点，最外沿根本没被采样到，
                    // 只按端点算包围盒会把"圆弧鼓出去的那块"判成面外（孔心明明在面上，
                    // 却被静默跳过）。所以把圆心和半径显式补进来。
                    G.Circle circle = null;
                    try { circle = e.Geometry as G.Circle; } catch { }
                    if (circle == null) continue;
                    Array cc = new double[3], ax = new double[3]; double r = 0;
                    try { circle.GetCircleData(ref cc, ref ax, out r); } catch { continue; }
                    if (r <= 0) continue;
                    var c = target.Placement.Point(V3.From(cc));
                    // 圆周上任意点可写成 c + r·(cosθ·u + sinθ·v)，θ 连续取值时
                    // 在 u/v 两个方向上的极值就是 ±r，所以补 4 个方向点即可精确包住整圆。
                    take(c + b.U * r); take(c - b.U * r); take(c + b.V * r); take(c - b.V * r);
                }
            } catch { }

            target.Bounds = b;
            return b;
        }

        static bool SameKey(FaceBounds b, object key){
            var k = (object[])key;
            return Math.Abs(b.PlanePointDot - (double)k[0]) < 1e-12 && Math.Abs(b.PlaneNormalKey - (double)k[1]) < 1e-12;
        }

        // 孔心是否落在打孔面内。用"面内 (u,v) 包围盒"判断（包围盒由面的边算一次、缓存）。
        // 对矩形/凸多边形面是精确的；对带缺口的凹面会略微放宽（包围盒必然包住实际轮廓），
        // 但绝不会漏判——宁可多打一个孔让 CAD 自己报失败，也不要静默跳过该打的孔。
        public static bool ContainsPoint(TargetFace target, V3 pointAssembly, double toleranceMm = 1.0){
            if (target == null) return false;
            var b = Bounds(target);
            if (!b.Any) return true;   // 读不到边就不拦，交给 CAD 自己判断
            var rp = pointAssembly - b.BasePoint;
            double pu = rp.Dot(b.U), pv = rp.Dot(b.V);
            double tol = toleranceMm * 0.001;
            return pu >= b.MinU - tol && pu <= b.MaxU + tol && pv >= b.MinV - tol && pv <= b.MaxV + tol;
        }

        // 面被重新选了，或者模型被改动过：丢掉缓存，下次调用重新遍历边。
        public static void InvalidateBounds(TargetFace target){
            if (target != null) target.Bounds = null;
        }

        // 圆周排孔：用户点一条圆边（分度圆）定圆心与半径。
        public static void ReadCircle(object edgeSelection, out V3 centreAssembly, out double radiusMm, out V3 axisAssembly){
            centreAssembly = new V3(); radiusMm = 0; axisAssembly = new V3(0,0,1);
            var pg = PickGeometry.Unwrap(edgeSelection);
            var edge = pg.Geometry as G.Edge;
            if (edge == null) throw new ArgumentException("圆周排孔请选择一条圆形边线。");
            var circle = edge.Geometry as G.Circle;
            if (circle == null) throw new ArgumentException("圆周排孔需要一个整圆边线（圆弧不行）。");
            Array c = new double[3], ax = new double[3]; double r = 0;
            circle.GetCircleData(ref c, ref ax, out r);
            if (r <= 0) throw new ArgumentException("所选圆的半径为 0。");
            centreAssembly = pg.Transform.Point(V3.From(c));
            axisAssembly = pg.Transform.Vector(V3.From(ax)).Unit();
            radiusMm = r * 1000.0;
        }
    }
}
