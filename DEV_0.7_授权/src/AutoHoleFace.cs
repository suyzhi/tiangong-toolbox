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
        // 方向边（自动取的最长边，或用户点的那条）离面中线多远（毫米，沿 Perp 带符号）。
        // 批量排孔的「离方向边 X mm」靠它换算成横向偏移：用户量尺寸都是从一条看得见的边量，不是从中线量。
        public double EdgeLateralMm;
        // 「孔心离方向边 d 毫米」→ 沿 Perp 的横向偏移（毫米）：从方向边往面里面走 d
        public double LateralFromEdge(double d){
            double toward = EdgeLateralMm > 1e-6 ? -1 : 1;   // 方向边在 +Perp 一侧就往 -Perp 走
            return EdgeLateralMm + toward * d;
        }
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
            return Build(target, pts, bestDir.Unit(), "自动取最长边", bestP1);
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
            // 方向边得和打孔面平行（0.8.5）：点到侧面的竖边时原来照样用，孔心沿一条斜线排、投到面上间距被压短，排出来全不对
            double tilt = Math.Abs(dir.Unit().Dot(target.Plane.Normal.Unit()));
            if (tilt > 0.02) throw new ArgumentException("这条边和打孔面不平行（斜 " + (Math.Asin(Math.Min(1, tilt)) * 180 / Math.PI).ToString("0.#") + "°），不能当排孔方向。请点打孔面上（或和它平行）的直线边。");
            var pts = new List<V3>();
            V3 bd = new V3(), bp = new V3(); double bl = 0;
            Collect(target, pts, ref bd, ref bp, ref bl);
            return Build(target, pts, dir.Unit(), "手动指定边", q1);
        }

        static void Collect(TargetFace target, List<V3> pts, ref V3 bestDir, ref V3 bestP1, ref double bestLen){
            foreach (G.Edge e in (G.Edges)target.Face.Edges) {
                Array sp = new double[3], ep = new double[3];
                try { e.GetEndPoints(ref sp, ref ep); } catch { continue; }
                var p1 = target.Placement.Point(V3.From(sp));
                var p2 = target.Placement.Point(V3.From(ep));
                pts.Add(p1); pts.Add(p2);
                if (!(e.Geometry is G.Line)) {
                    // 圆弧 / 整圆 / 样条：两个端点包不住鼓出去的那段（腰形面、两头半圆的面，最外沿在弧的中间）。
                    // 只按端点算，面长少算一个半径，「起端边距」从错的地方量起，整排孔偏一个半径。弧上再采几个点
                    try {
                        Array data = new double[0], prm = new double[0]; int count;
                        e.GetStrokeData(0.0001, out count, ref data, ref prm);
                        for (int i = 0; i + 2 < data.Length; i += 3)
                            pts.Add(target.Placement.Point(new V3(Convert.ToDouble(data.GetValue(i)), Convert.ToDouble(data.GetValue(i + 1)), Convert.ToDouble(data.GetValue(i + 2)))));
                    } catch { }
                    continue;
                }
                double len = (p2 - p1).Length;
                if (len > bestLen) { bestLen = len; bestDir = p2 - p1; bestP1 = p1; }
            }
        }

        static FaceFrame Build(TargetFace target, List<V3> pts, V3 dir, string how, V3 edgePoint){
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
                EdgeLateralMm = ((edgePoint - basePoint).Dot(perp) - (pmin + pmax) / 2) * 1000.0,
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
            // 0.8.5：面的实际形状（面内 (u,v) 三角形，每 6 个数一个）+ 面上圆边（孔口，u,v,r）。
            // 只用包围盒时 L 形面、开了窗的面、两块分开的面上，落在空处的孔心也算"在面内"，
            // 打的时候 CAD 只报一句含糊的"拒绝建孔"，同一批合成多孔特征也整批失败。读不到面片时为 null，退回只看包围盒
            public double[] Tris;
            public List<double[]> Circles = new List<double[]>();
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
                    var rc = c - b.BasePoint;
                    b.Circles.Add(new[]{ rc.Dot(b.U), rc.Dot(b.V), r });
                }
            } catch { }
            b.Tris = Facets(target, b);

            target.Bounds = b;
            return b;
        }

        // 面片三角形投到面内 (u,v)。一次 COM 调用；面很大、孔很多时也就几千个三角形
        static double[] Facets(TargetFace target, FaceBounds b){
            try {
                int n = 0; Array pts = new double[0]; object nrm = null, tex = null;
                target.Face.GetFacetData(0.0002, out n, ref pts, out nrm, out tex);
                if (n <= 0 || pts == null || pts.Length < n * 9) return null;
                var uv = new double[n * 6];
                for (int i = 0; i < n * 3; i++) {
                    var q = target.Placement.Point(new V3(Convert.ToDouble(pts.GetValue(i * 3)), Convert.ToDouble(pts.GetValue(i * 3 + 1)), Convert.ToDouble(pts.GetValue(i * 3 + 2)))) - b.BasePoint;
                    uv[i * 2] = q.Dot(b.U); uv[i * 2 + 1] = q.Dot(b.V);
                }
                return uv;
            } catch (Exception e) { Log.Write("FaceFacets", e.Message); return null; }
        }

        static bool SameKey(FaceBounds b, object key){
            var k = (object[])key;
            return Math.Abs(b.PlanePointDot - (double)k[0]) < 1e-12 && Math.Abs(b.PlaneNormalKey - (double)k[1]) < 1e-12;
        }

        // 孔心是否落在打孔面内：先看"面内 (u,v) 包围盒"，再看面的实际形状（面片三角形，0.8.5，见 FaceBounds.Tris）。
        // 读不到面片时只看包围盒（宁可多打一个孔让 CAD 自己报失败，也不要静默跳过该打的孔）。
        public static bool ContainsPoint(TargetFace target, V3 pointAssembly, double toleranceMm = 1.0){
            if (target == null) return false;
            var b = Bounds(target);
            if (!b.Any) return true;   // 读不到边就不拦，交给 CAD 自己判断
            var rp = pointAssembly - b.BasePoint;
            double pu = rp.Dot(b.U), pv = rp.Dot(b.V);
            double tol = toleranceMm * 0.001;
            if (!(pu >= b.MinU - tol && pu <= b.MaxU + tol && pv >= b.MinV - tol && pv <= b.MaxV + tol)) return false;
            return FaceShape.Inside(b.Tris, b.Circles, pu, pv, tol);
        }

        // 孔口（直径 mouthMm）一圈上有没有点落在面外（0.8.5）：孔心在面上、孔口却切到面边上 / 旁边的孔 —— 打出来是个缺口
        public static bool CutsEdge(TargetFace t, V3 centre, double mouthMm){
            double r = mouthMm / 2000.0;
            if (t == null || !(r > 0)) return false;
            var n = t.Plane.Normal.Unit();
            var u = n.Cross(Math.Abs(n.X) < 0.9 ? new V3(1, 0, 0) : new V3(0, 1, 0)).Unit(); var v = n.Cross(u).Unit();
            for (int k = 0; k < 8; k++) {
                double a = k * Math.PI / 4;
                if (!ContainsPoint(t, centre + u * (r * Math.Cos(a)) + v * (r * Math.Sin(a)), 0.2)) return true;
            }
            return false;
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
