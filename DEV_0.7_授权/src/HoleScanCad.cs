using System;
using System.Collections.Generic;
using System.Globalization;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;
using F=SolidEdgeFramework;

namespace TianGongCadSuite {
    // 面扫描 / 圆柱面认孔的 CAD 侧读取：把边和面读成 CircleCandidate，判据全部交给
    // HoleScan（纯逻辑，可脱离 CAD 回归）。这里只做三件事：枚举、判"是不是孔口"、坐标换算。
    public static class HoleScanCad {
        // 一个零件实体上所有整圆（**零件局部坐标**）。点一次面就要把所有圆和孔口比一遍同轴，
        // 而每个圆的 geometry 都是一次 COM 调用（大零件上千条边），所以按零件缓存一份。
        // 缓存带时限：打完孔/换零件以后模型变了，靠 Invalidate + 时限双保险。
        static readonly Dictionary<string, List<CircleCandidate>> circles = new Dictionary<string, List<CircleCandidate>>();
        static readonly Dictionary<string, DateTime> stamps = new Dictionary<string, DateTime>();
        const double CacheSeconds = 30;

        public static void Invalidate(P.PartDocument part){
            string key = Key(part);
            if (key.Length == 0) { circles.Clear(); stamps.Clear(); walls.Clear(); wallStamps.Clear(); return; }
            circles.Remove(key); stamps.Remove(key); walls.Remove(key); wallStamps.Remove(key);
        }
        static string Key(P.PartDocument part){
            if (part == null) return "";
            try { string s = part.FullName; if (!string.IsNullOrEmpty(s)) return s; } catch { }
            try { return part.Name; } catch { }
            return "";
        }

        public static G.Body BodyOf(P.PartDocument part){
            try { if (part != null && part.Models.Count >= 1) return (G.Body)((P.Model)part.Models.Item(1)).Body; } catch { }
            return null;
        }

        // 一条边 -> 一个圆候选（读不到 / 半径 0 就返回 null）。
        static CircleCandidate Circle(G.Edge edge){
            G.Circle circle = null;
            try { circle = edge.Geometry as G.Circle; } catch { return null; }
            if (circle == null) return null;
            Array c = new double[3], ax = new double[3]; double r = 0;
            try { circle.GetCircleData(ref c, ref ax, out r); } catch { return null; }
            if (!(r > 1e-6)) return null;
            var cand = new CircleCandidate { Center = V3.From(c), Axis = V3.From(ax).Unit(), RadiusMm = r * 1000.0, Tag = edge };
            try { cand.Closed = edge.IsClosed; } catch { cand.Closed = true; }   // 老版本 CAD 读不到就按整圆算，后面还有环过滤
            try { cand.Source = "边#" + edge.ID; } catch { }
            return cand;
        }

        // 这个圆旁边的壁面是什么（圆柱/圆锥/圆环）——既用于措辞，也用于判"孔还是凸台"。
        static string WallOf(G.Edge edge, G.Face self){
            G.Face wall;
            return WallOf(edge, self, out wall);
        }
        static string WallOf(G.Edge edge, G.Face self, out G.Face wallFace){
            wallFace = null;
            try {
                int count = 0;
                Array faces = new object[0];   // 传 null 时实测会抛 NullReference（真机 2026-10-05），给个空数组
                edge.GetFaces(out count, ref faces);
                G.Face firstAny = null; string firstName = "";
                for (int i = 0; i < count; i++) {
                    var f = faces.GetValue(i) as G.Face;
                    if (f == null) continue;
                    bool same = false;
                    try { same = (self != null && f.ID == self.ID); } catch { }
                    if (same) continue;                       // 跳过自己（用户点的那张面）
                    string name = GeometryName(f.Geometry);
                    if (firstAny == null) { firstAny = f; firstName = name; }
                    if (name != "Plane") { wallFace = f; return name; }   // 圆孔口旁边一定有一张非平面的壁，优先取它
                }
                wallFace = firstAny;
                return firstName;
            } catch { }
            return "";
        }

        // 圆柱面法向约定（真机实测，2026-10-05，天工CAD 225.03.00.165，4 张面）：
        //   Φ11 沉孔壁（孔）、Φ6.6 孔壁（孔）：IsParamReversed = True
        //   Φ20 凸台外壁（凸台）：              IsParamReversed = False
        // 与"面外法向 = IsParamReversed ? -几何法向 : 几何法向"这条老规则一致：
        // 圆柱面的几何法向指向背离轴线那一侧，于是
        //   True  → 外法向指向轴线 → 材料在**圆柱外** → 这是孔的壁；
        //   False → 外法向背离轴线 → 材料在**圆柱里** → 这是凸台/销的外壁。
        // 返回 1 = 孔、0 = 凸台、-1 = 判不了（不是圆柱面、或读不出来）。
        static int WallSaysHole(G.Face wall){
            if (wall == null) return -1;
            try {
                if (!(wall.Geometry is G.Cylinder)) return -1;
                return wall.IsParamReversed ? 1 : 0;
            } catch { return -1; }
        }
        const string BossWallMessage = "这是一根圆凸台（或销）的外壁，不是孔。请点孔口的圆形边线，或点孔的内壁圆柱面。";
        static string GeometryName(object geometry){
            if (geometry == null) return "";
            if (geometry is G.Cylinder) return "Cylinder";
            if (geometry is G.Cone) return "Cone";
            if (geometry is G.Torus) return "Torus";
            if (geometry is G.Plane) return "Plane";
            return geometry.GetType().Name;
        }

        // 零件实体上所有整圆（局部坐标，缓存）。
        public static List<CircleCandidate> BodyCircles(P.PartDocument part){
            string key = Key(part);
            DateTime stamp;
            if (key.Length > 0 && circles.ContainsKey(key) && stamps.TryGetValue(key, out stamp)
                && (DateTime.UtcNow - stamp).TotalSeconds < CacheSeconds)
                return circles[key];

            var list = new List<CircleCandidate>();
            try {
                var body = BodyOf(part);
                if (body != null) {
                    foreach (G.Edge e in (G.Edges)body.get_Edges(G.FeatureTopologyQueryTypeConstants.igQueryAll)) {
                        var c = Circle(e);
                        if (c == null) continue;
                        list.Add(c);
                    }
                }
            } catch (Exception e) { Log.Write("HoleScanBodyCircles", e); }

            if (key.Length > 0) { circles[key] = list; stamps[key] = DateTime.UtcNow; }
            return list;
        }

        // 这张面自己的圆口（默认只取**内环**上的整圆：孔口都是内环，外环上的圆是轮廓圆角或凸台顶面）。
        // byLoops=false 表示这个 CAD 版本读不到环信息，调用方要按"可能混进圆角/凸台"来提示。
        public static List<CircleCandidate> FaceCircles(G.Face face, bool innerOnly, out bool byLoops){
            byLoops = false;
            var list = new List<CircleCandidate>();
            if (face == null) return list;
            try {
                var loops = face.Loops as G.Loops;
                if (loops != null && loops.Count > 0) {
                    byLoops = true;
                    foreach (G.Loop loop in loops) {
                        bool outer = true;
                        try { outer = loop.IsOuterLoop; } catch { }
                        if (innerOnly && outer) continue;
                        foreach (G.Edge e in (G.Edges)loop.Edges) {
                            var c = Circle(e);
                            if (c == null) continue;
                            c.Wall = WallOf(e, face);
                            list.Add(c);
                        }
                    }
                }
            } catch (Exception e) {
                Log.Write("HoleScanLoops", e);
                byLoops = false;
                list.Clear();
            }
            if (!byLoops) {   // 退回"面上的所有圆边"：圆角/凸台要另外挡（见 HoleLooksLikeHole）
                try {
                    foreach (G.Edge e in (G.Edges)face.Edges) {
                        var c = Circle(e);
                        if (c == null) continue;
                        c.Wall = WallOf(e, face);
                        list.Add(c);
                    }
                } catch (Exception e) { Log.Write("HoleScanFaceEdges", e); }
            }
            return list;
        }

        // ---- 这个"圆"到底是孔口还是圆凸台的底圆 ----
        // 孔和圆凸台在拓扑上长得一模一样：一块板上立一根圆凸台，凸台的底圆同样落在板面的一张
        // **内环**上（实测见 tools/facescan-probe 第 1 节），半径也正好等于那根圆柱的半径。
        // 唯一说得清的差别是"壁在材料的哪一侧"：
        //   孔   → 壁往材料里凹（在孔口那张面外法向的**负**侧）；
        //   凸台 → 壁往材料外凸（在**正**侧）。
        // 判法：找出这个圆口那一圈壁（与它同轴的圆柱/圆锥/圆环面），量它的 3D 包围盒中心。
        // 实测：Φ11/Φ6.6 沉孔的壁 z∈[4,10]、孔口面在 z=10 外法向 +Z → 偏 -6mm = 孔；
        //       Φ20 凸台的壁 z∈[10,15] → 偏 +2.5mm = 凸台。
        // 判不了（找不到壁面 / 面片外法向读不出来 / 壁几乎不在两侧）一律按孔处理：
        // 宁可多认一个让用户在预览里看见，也不要静默漏掉该打的孔。
        static bool LooksLikeHole(P.PartDocument part, G.Face planarFace, CircleCandidate mouth){
            if (part == null || planarFace == null || mouth == null) return true;
            V3 outward = OutwardOf(planarFace);
            if (!outward.Finite || outward.Length < 0.5) return true;
            var wall = WallFaceOf(part, mouth);
            if (wall == null) return true;
            Array lo = new double[3], hi = new double[3];
            try { wall.GetRange(ref lo, ref hi); } catch { return true; }
            var centre = (V3.From(lo) + V3.From(hi)) * 0.5;
            double offsetMm = (centre - mouth.Center).Dot(outward) * 1000.0;
            if (Math.Abs(offsetMm) < 0.5) return true;      // 壁几乎不偏：判不出来，不拦
            return offsetMm < 0;
        }

        // 面片外法向（本工程实测并一直在用的规则：IsParamReversed 时几何法向要取反）。
        static V3 OutwardOf(G.Face face){
            try {
                var plane = face.Geometry as G.Plane;
                if (plane == null) return new V3(double.NaN, double.NaN, double.NaN);
                Array p = new double[3], n = new double[3];
                plane.GetPlaneData(ref p, ref n);
                var nv = V3.From(n).Unit();
                bool reversed = false;
                try { reversed = face.IsParamReversed; } catch { }
                return reversed ? nv * -1.0 : nv;
            } catch { return new V3(double.NaN, double.NaN, double.NaN); }
        }

        // 与这个圆口同轴的那圈壁面（圆柱/圆锥/圆环）。按"面的边界里有一条边正好是这个圆"来找。
        // 按零件缓存这张"壁面 + 它的边界圆"表，否则每个孔口都要重新遍历一遍面。
        sealed class WallFace {
            public G.Face Face;
            public readonly List<CircleCandidate> Rims = new List<CircleCandidate>();
        }
        static readonly Dictionary<string, List<WallFace>> walls = new Dictionary<string, List<WallFace>>();
        static readonly Dictionary<string, DateTime> wallStamps = new Dictionary<string, DateTime>();

        static List<WallFace> WallFaces(P.PartDocument part){
            string key = Key(part);
            DateTime stamp;
            if (key.Length > 0 && walls.ContainsKey(key) && wallStamps.TryGetValue(key, out stamp)
                && (DateTime.UtcNow - stamp).TotalSeconds < CacheSeconds)
                return walls[key];

            var list = new List<WallFace>();
            try {
                var body = BodyOf(part);
                if (body != null) {
                    foreach (G.Face f in (G.Faces)body.get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll)) {
                        object geometry = null;
                        try { geometry = f.Geometry; } catch { continue; }
                        if (!(geometry is G.Cylinder) && !(geometry is G.Cone) && !(geometry is G.Torus)) continue;
                        var rec = new WallFace { Face = f };
                        try {
                            foreach (G.Edge e in (G.Edges)f.Edges) {
                                var c = Circle(e);
                                if (c != null) rec.Rims.Add(c);
                            }
                        } catch { }
                        if (rec.Rims.Count > 0) list.Add(rec);
                    }
                }
            } catch (Exception e) { Log.Write("HoleScanWallFaces", e); }

            if (key.Length > 0) { walls[key] = list; wallStamps[key] = DateTime.UtcNow; }
            return list;
        }

        static G.Face WallFaceOf(P.PartDocument part, CircleCandidate mouth){
            try {
                foreach (var w in WallFaces(part)) {
                    foreach (var rim in w.Rims) {
                        if (Math.Abs(rim.RadiusMm - mouth.RadiusMm) > 0.01) continue;
                        if (!HoleScan.Parallel(rim.Axis, mouth.Axis)) continue;
                        if (HoleScan.RadialOffsetMm(mouth.Center, mouth.Axis, rim.Center) > 0.01) continue;
                        return w.Face;
                    }
                }
            } catch (Exception e) { Log.Write("HoleScanWallFace", e); }
            return null;
        }

        // ---- 点一条圆形边线：认出这一个孔（含沉孔识别） ----
        public static ScannedHole ScanEdge(object selection){
            var pg = PickGeometry.Unwrap(selection);
            var edge = pg.Geometry as G.Edge;
            if (edge == null) throw new ArgumentException("请选择一条圆形孔边线（孔的轮廓圆）。");
            G.Circle circle = null;
            try { circle = edge.Geometry as G.Circle; } catch { }
            if (circle == null) throw new ArgumentException("选中的边不是圆孔。请在模型上点击孔口的圆形边线。");
            Array c = new double[3], axis = new double[3]; double radius = 0;
            circle.GetCircleData(ref c, ref axis, out radius);
            if (!(radius > 1e-6)) throw new ArgumentException("读取到的孔半径为 0，请重新选择孔边线。");
            P.PartDocument part = null;
            try { part = edge.Document as P.PartDocument; } catch { }
            var mouth = new CircleCandidate { Center = V3.From(c), Axis = V3.From(axis).Unit(), RadiusMm = radius * 1000.0, Tag = edge };
            G.Face wallFace;
            mouth.Wall = WallOf(edge, null, out wallFace);
            if (WallSaysHole(wallFace) == 0) throw new ArgumentException(BossWallMessage);
            var all = part != null ? BodyCircles(part) : new List<CircleCandidate>();
            var hole = HoleScan.FromCircle(mouth, all);
            if (hole == null) throw new ArgumentException("这条边读不出孔径，请重新点一次孔口的圆形边线。");
            hole.Center = pg.Transform.Point(hole.Center);
            hole.Axis = pg.Transform.Vector(hole.Axis).Unit();
            hole.Source = "孔口圆边";
            return hole;
        }

        // ---- 点一个平面：把这个面上的孔全认出来（含沉孔/锥沉/倒角孔口下面的孔径） ----
        public static List<ScannedHole> ScanFace(TargetFace target, out string how, out int skippedCount){
            how = ""; skippedCount = 0;
            if (target == null || target.Face == null) return new List<ScannedHole>();
            var all = BodyCircles(target.Part);
            bool byLoops;
            var mouths = FaceCircles(target.Face, true, out byLoops);
            // 内环上的整圆里仍然可能混进"圆凸台的底圆"（实测它同样长在内环上），逐个判一遍。
            var kept = new List<CircleCandidate>();
            foreach (var m in mouths) {
                if (LooksLikeHole(target.Part, target.Face, m)) kept.Add(m);
                else skippedCount++;
            }
            var holes = HoleScan.FromCircles(kept, all);
            foreach (var h in holes) {
                h.Center = target.Placement.Point(h.Center);
                h.Axis = target.Placement.Vector(h.Axis).Unit();
                h.Source = target.Label;
            }
            if (!byLoops) how = "这个 CAD 版本读不到面的环信息，识别结果里可能混进轮廓圆角或凸台";
            return holes;
        }

        // ---- 点一个圆柱面/圆锥面/圆环面：认出这一个孔 ----
        // 圆柱面：直径 = 2×圆柱半径（沉孔壁会再按"下面的孔径"修正）；
        // 圆锥面（锥形沉孔/倒角）：取这个面上最小的圆边，那就是锥面与孔的交界圆；
        // 圆环面（孔口圆角）：孔半径 = 主半径 - 次半径。
        public static ScannedHole ScanSurface(object selection, out string label){
            label = "";
            var pg = PickGeometry.Unwrap(selection);
            var face = pg.Geometry as G.Face;
            if (face == null) throw new ArgumentException("请点孔的内壁（圆柱面），或者孔口的圆锥面、圆角面。");
            P.PartDocument part = null;
            try { part = face.Document as P.PartDocument; } catch { }
            if (part == null) throw new ArgumentException("这个面不属于零件文档，请在装配里直接点零件上的孔内壁。");

            var all = BodyCircles(part);
            var geometry = face.Geometry;
            CircleCandidate mouth = null;

            var cylinder = geometry as G.Cylinder;
            if (cylinder != null) {
                Array bp = new double[3], ax = new double[3]; double r = 0;
                cylinder.GetCylinderData(ref bp, ref ax, out r);
                if (!(r > 1e-6)) throw new ArgumentException("这个圆柱面的半径读出来是 0，请重新点一次。");
                if (WallSaysHole(face) == 0) throw new ArgumentException(BossWallMessage);
                mouth = new CircleCandidate { Center = V3.From(bp), Axis = V3.From(ax).Unit(), RadiusMm = r * 1000.0, Wall = "Cylinder" };
                label = "圆柱面";
            }

            var cone = geometry as G.Cone;
            if (cone != null) {
                bool byLoops;
                var own = FaceCircles(face, false, out byLoops);
                foreach (var c in own) if (mouth == null || c.RadiusMm < mouth.RadiusMm) mouth = c;
                if (mouth == null)
                    throw new ArgumentException("这个圆锥面上读不到圆边（多半是孔口倒角面）。请点孔的圆柱内壁，或点孔口的圆形边线。");
                mouth.Wall = "Cone";
                label = "圆锥面";
            }

            var torus = geometry as G.Torus;
            if (torus != null) {
                Array bp = new double[3], ax = new double[3]; double major = 0, minor = 0;
                torus.GetTorusData(ref bp, ref ax, out major, out minor);
                double r = major - minor;                     // 孔口圆角：孔半径 = 主半径 - 次半径
                if (!(r > 1e-6)) throw new ArgumentException("这个圆角面上算不出孔径，请点孔的圆柱内壁或孔口的圆形边线。");
                mouth = new CircleCandidate { Center = V3.From(bp), Axis = V3.From(ax).Unit(), RadiusMm = r * 1000.0, Wall = "Torus" };
                label = "孔口圆角面";
            }

            if (mouth == null) {
                if (geometry is G.Plane) throw new ArgumentException("这是一个平面，不是孔的内壁。认孔请点圆柱内壁；要扫面上的孔请点平面。");
                throw new ArgumentException("这个面的类型是 " + GeometryName(geometry) + "，认不出孔径。请点孔口的圆形边线或孔的圆柱内壁。");
            }

            var hole = HoleScan.FromCircle(mouth, all);
            if (hole == null) throw new ArgumentException("没能从这条面上算出孔径，请重新点一次。");
            hole.Center = pg.Transform.Point(hole.Center);
            hole.Axis = pg.Transform.Vector(hole.Axis).Unit();
            hole.Source = label;
            return hole;
        }
    }
}
