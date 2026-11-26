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

        // 过期的缓存项清掉（0.10.2）：原来过了 30 秒只是不再用，项还一直留着 —— 每个点过的零件的圆边 / 壁面 COM 对象
        // 在整个天工会话里都攥在手里，零件关掉了也放不掉（点过几百个零件的大装配，内存只涨不落）
        static void Prune(){
            var now = DateTime.UtcNow;
            foreach (var pair in new[]{ new KeyValuePair<System.Collections.IDictionary, Dictionary<string, DateTime>>(circles, stamps), new KeyValuePair<System.Collections.IDictionary, Dictionary<string, DateTime>>(walls, wallStamps) }) {
                var drop = new List<string>();
                foreach (var kv in pair.Value) if ((now - kv.Value).TotalSeconds >= CacheSeconds) drop.Add(kv.Key);
                foreach (var k in drop) { pair.Key.Remove(k); pair.Value.Remove(k); }
            }
        }

        public static void Invalidate(P.PartDocument part){
            string key = Key(part);
            if (key.Length == 0) { circles.Clear(); stamps.Clear(); walls.Clear(); wallStamps.Clear(); return; }
            // 缓存键是「零件|实体」，这个零件的每个实体都要清
            foreach (var d in new System.Collections.IDictionary[]{ circles, stamps, walls, wallStamps }) {
                var drop = new List<string>();
                foreach (string k in d.Keys) if (k == key || k.StartsWith(key + "|", StringComparison.Ordinal)) drop.Add(k);
                foreach (var k in drop) d.Remove(k);
            }
        }
        static string Key(P.PartDocument part){
            if (part == null) return "";
            try { string s = part.FullName; if (!string.IsNullOrEmpty(s)) return s; } catch { }
            try { return part.Name; } catch { }
            return "";
        }

        // 选中的面 / 边所在的那个实体（多实体零件，0.7.15）。原来一律取第 1 个实体：第 2 个以后的实体上点面扫孔，
        // 沉孔下面的孔径、"孔还是凸台"都拿第 1 个实体的几何去比，认错或认不出。读不到时退回第 1 个实体。
        public static G.Body BodyOf(P.PartDocument part, object topology){
            if (topology != null) { try { var b = ((dynamic)topology).Body as G.Body; if (b != null) return b; } catch { } }
            try { if (part != null && part.Models.Count >= 1) return (G.Body)((P.Model)part.Models.Item(1)).Body; } catch { }
            return null;
        }
        static string Key(P.PartDocument part, G.Body body){
            string key = Key(part);
            if (key.Length == 0 || !PartBodies.Multi(part)) return key;
            try { return key + "|" + body.Tag; } catch { return key; }
        }

        // 一条边 -> 一个圆候选（读不到 / 半径 0 就返回 null）。
        static object DocOf(object topology){ try { return ((dynamic)topology).Document; } catch { return null; } }

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
        // 这条圆边是不是圆凸台 / 销 / 轴 / 螺栓杆的外壁（不是孔）。配孔检查采集孔时用：
        // 原来装配里每一条圆边都当孔，装配里的螺栓、销钉、圆柱凸台都会混进来，
        // 和真孔配对后误报"配错孔"、轴线碰到别的零件还会误报"漏打孔"。
        // 判据与点圆边认孔同一条（真机实测：孔壁圆柱面 IsParamReversed=True，凸台外壁 False）；
        // 判不出来（孔口是锥面/圆角、读不到面）一律当孔 —— 宁可多查，不漏查。
        public static bool IsBossEdge(G.Edge edge){
            if (edge == null) return false;
            G.Face wall;
            WallOf(edge, null, out wall);
            return WallSaysHole(wall) == 0;
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
        public static List<CircleCandidate> BodyCircles(P.PartDocument part, object topology){
            var body = BodyOf(part, topology);
            string key = Key(part, body);
            DateTime stamp;
            if (key.Length > 0 && circles.ContainsKey(key) && stamps.TryGetValue(key, out stamp)
                && (DateTime.UtcNow - stamp).TotalSeconds < CacheSeconds)
                return circles[key];

            var list = new List<CircleCandidate>();
            try {
                if (body != null) {
                    foreach (G.Edge e in (G.Edges)body.get_Edges(G.FeatureTopologyQueryTypeConstants.igQueryAll)) {
                        var c = Circle(e);
                        if (c == null) continue;
                        G.Face wf;
                        c.Wall = WallOf(e, null, out wf);
                        c.WallFace = wf;
                        // 标记凸台/销外壁圆边，避免 InnerBelow 把同轴凸台当成沉孔下面的孔；
                        // 若相邻壁面为孔内壁圆柱面，记下其沿轴线总长度作为二级台阶（如倒角+沉孔）的深度上限。
                        c.IsBoss = WallSaysHole(wf) == 0;
                        if (!c.IsBoss && wf != null && IsRoundWall(wf)) {
                            double spanM = FaceAxisSpan(wf, c.Axis);
                            if (spanM > 1e-4) c.MaxStepDepthMm = spanM * 1000.0 + 0.5;
                        }
                        list.Add(c);
                    }
                }
            } catch (Exception e) { Log.Write("HoleScanBodyCircles", e); }

            Prune();
            if (key.Length > 0) { circles[key] = list; stamps[key] = DateTime.UtcNow; }
            return list;
        }
        // 壁面沿指定单位轴向的包围盒投影长度（米）
        static double FaceAxisSpan(G.Face wallFace, V3 axis){
            if (wallFace == null || !axis.Finite || axis.Length < 0.5) return 0;
            Array lo = new double[3], hi = new double[3];
            try { wallFace.GetRange(ref lo, ref hi); } catch { return 0; }
            V3 bLo = V3.From(lo), bHi = V3.From(hi);
            if (!bLo.Finite || !bHi.Finite) return 0;
            V3 u = axis.Unit();
            double minP = double.PositiveInfinity, maxP = double.NegativeInfinity;
            for (int k = 0; k < 8; k++) {
                var p = new V3((k & 1) == 0 ? bLo.X : bHi.X, (k & 2) == 0 ? bLo.Y : bHi.Y, (k & 4) == 0 ? bLo.Z : bHi.Z);
                double d = p.Dot(u);
                if (d < minP) minP = d;
                if (d > maxP) maxP = d;
            }
            return Math.Max(0, maxP - minP);
        }
        // 根据孔口相邻壁面与已知入孔方向，写入 InwardAxis 与 MaxStepDepthMm，
        // 避免薄壁双面方管/型材对壁的异径孔被跨空腔误判成沉孔。
        static void ApplyWallHint(CircleCandidate c, G.Face wallFace, V3 inwardHint){
            if (c == null) return;
            V3 dir = inwardHint;
            if (wallFace != null && IsRoundWall(wallFace)) {
                Array lo = new double[3], hi = new double[3];
                try {
                    wallFace.GetRange(ref lo, ref hi);
                    V3 bLo = V3.From(lo), bHi = V3.From(hi);
                    if (bLo.Finite && bHi.Finite) {
                        if (!dir.Finite || dir.Length < 0.5) {
                            V3 mid = (bLo + bHi) * 0.5;
                            double proj = (mid - c.Center).Dot(c.Axis);
                            if (Math.Abs(proj) > 1e-4) dir = proj > 0 ? c.Axis : c.Axis * -1.0;
                        }
                        if (dir.Finite && dir.Length > 0.5) {
                            dir = dir.Unit();
                            c.InwardAxis = dir;
                            double reachM = double.NegativeInfinity;
                            for (int k = 0; k < 8; k++) {
                                var p = new V3((k & 1) == 0 ? bLo.X : bHi.X, (k & 2) == 0 ? bLo.Y : bHi.Y, (k & 4) == 0 ? bLo.Z : bHi.Z);
                                double d = (p - c.Center).Dot(dir);
                                if (d > reachM) reachM = d;
                            }
                            if (reachM > 1e-4) c.MaxStepDepthMm = reachM * 1000.0 + 0.5;
                            return;
                        }
                    }
                } catch { }
            }
            if (dir.Finite && dir.Length > 0.5) c.InwardAxis = dir.Unit();
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
                            G.Face wf;
                            c.Wall = WallOf(e, face, out wf);
                            c.WallFace = wf;
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
                        G.Face wf;
                        c.Wall = WallOf(e, face, out wf);
                        c.WallFace = wf;
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
        // 注意：不能因 part == null 就跳过 —— 钣金件（.psm）同样有 planarFace 与 mouth.WallFace / BodyOf(null, planarFace)。
        static bool LooksLikeHole(P.PartDocument part, G.Face planarFace, CircleCandidate mouth){
            if (planarFace == null || mouth == null) return true;
            V3 outward = OutwardOf(planarFace);
            if (!outward.Finite || outward.Length < 0.5) return true;
            // 0.9.1：先用孔口这条边自己旁边的那张壁面。原来按"同轴同径"去整个实体里找第一张，**不看轴向位置** ——
            // 型材/方管上下两壁打穿的孔、两头都有同径孔的轴类件，常常找到对面那个孔的壁，
            // 它在这张面的外侧，就把这个孔判成"凸台"跳过：点面认孔时一张面上少几个孔
            var wall = mouth.WallFace as G.Face;
            if (wall != null && !IsRoundWall(wall)) wall = null;
            if (wall == null) wall = WallFaceOf(part, BodyOf(part, planarFace), mouth);
            if (wall == null) return true;
            if (WallSaysHole(wall) == 0) return false;
            Array lo = new double[3], hi = new double[3];
            try { wall.GetRange(ref lo, ref hi); } catch { return true; }
            var centre = (V3.From(lo) + V3.From(hi)) * 0.5;
            double offsetMm = (centre - mouth.Center).Dot(outward) * 1000.0;
            if (Math.Abs(offsetMm) < 0.5) return true;      // 壁几乎不偏：判不出来，不拦
            return offsetMm < 0;
        }
        // 给 SlotScanCad.FromArc 复用：判断平面上的圆弧边相邻壁面是否往材料内部凹（孔）而非向外凸（腰形凸台）
        internal static bool LooksLikeHoleEdge(G.Face planarFace, G.Edge edge, V3 circleCenter){
            if (planarFace == null || edge == null) return true;
            G.Face wf;
            WallOf(edge, planarFace, out wf);
            if (WallSaysHole(wf) == 0) return false;
            P.PartDocument part = null;
            try { part = planarFace.Document as P.PartDocument; } catch { }
            var c = new CircleCandidate { Center = circleCenter, WallFace = wf };
            return LooksLikeHole(part, planarFace, c);
        }

        static bool IsRoundWall(G.Face f){
            try { var g = f.Geometry; return g is G.Cylinder || g is G.Cone || g is G.Torus; } catch { return false; }
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

        static List<WallFace> WallFaces(P.PartDocument part, G.Body body){
            string key = Key(part, body);
            DateTime stamp;
            if (key.Length > 0 && walls.ContainsKey(key) && wallStamps.TryGetValue(key, out stamp)
                && (DateTime.UtcNow - stamp).TotalSeconds < CacheSeconds)
                return walls[key];

            var list = new List<WallFace>();
            try {
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

            Prune();
            if (key.Length > 0) { walls[key] = list; wallStamps[key] = DateTime.UtcNow; }
            return list;
        }

        static G.Face WallFaceOf(P.PartDocument part, G.Body body, CircleCandidate mouth){
            try {
                foreach (var w in WallFaces(part, body)) {
                    foreach (var rim in w.Rims) {
                        if (Math.Abs(rim.RadiusMm - mouth.RadiusMm) > 0.01) continue;
                        if (!HoleScan.Parallel(rim.Axis, mouth.Axis)) continue;
                        if (HoleScan.RadialOffsetMm(mouth.Center, mouth.Axis, rim.Center) > 0.01) continue;
                        if (Math.Abs(HoleScan.AxialOffsetMm(mouth.Center, mouth.Axis, rim.Center)) > 0.01) continue;   // 必须就是这个圆，不是同轴线上别处的同径圆
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
            // 圆弧（腰孔两头、倒圆角）不是孔（0.8.5）：原来照样按"孔"配，在腰孔一头打出一个圆孔
            // 但导入的零件（STEP）常把一个整圆孔口拆成两段半圆：同一个圆上还有别的弧段就照常认。
            // 注意：钣金件（.psm）的 owner 为 null，BodyCircles(null, edge) 仍会经 BodyOf 从 edge.Body 读取同实体圆边；
            // 容差与 HoleScan.MergeArcs 对齐（圆心 0.01mm、半径 0.005mm），避免两段半圆跨 1µm 漏拼。
            bool closed = true; try { closed = edge.IsClosed; } catch { }
            if (!closed) {
                P.PartDocument owner = null; try { owner = edge.Document as P.PartDocument; } catch { }
                Array cc = new double[3], ax = new double[3]; double rr = 0;
                circle.GetCircleData(ref cc, ref ax, out rr);
                V3 c0 = V3.From(cc), a0 = V3.From(ax);
                int same = 0;
                foreach (var bc in BodyCircles(owner, edge))
                    if ((bc.Center - c0).Length < 1e-5 && Math.Abs(bc.RadiusMm - rr * 1000.0) < 0.005 && HoleScan.Parallel(bc.Axis, a0)) same++;
                if (same < 2) throw new ArgumentException("这是一段圆弧（腰孔的一头或圆角），不是整圆的孔，没法按它配孔。腰孔请用「批量排孔」勾「排成腰孔」打。");
            }
            Array c = new double[3], axis = new double[3]; double radius = 0;
            circle.GetCircleData(ref c, ref axis, out radius);
            if (!(radius > 1e-6)) throw new ArgumentException("读取到的孔半径为 0，请重新选择孔边线。");
            P.PartDocument part = null;
            try { part = edge.Document as P.PartDocument; } catch { }
            var mouth = new CircleCandidate { Center = V3.From(c), Axis = V3.From(axis).Unit(), RadiusMm = radius * 1000.0, Tag = edge };
            G.Face wallFace;
            mouth.Wall = WallOf(edge, null, out wallFace);
            mouth.WallFace = wallFace;
            if (WallSaysHole(wallFace) == 0) throw new ArgumentException(BossWallMessage);
            ApplyWallHint(mouth, wallFace, new V3());
            // 钣金件（.psm）part 为 null 时也照常从 edge.Body 读取全实体圆边，保证沉孔/锥沉/倒角能认出下面的孔径
            var all = BodyCircles(part, edge);
            var hole = HoleScan.FromCircle(mouth, HoleScan.MergeArcs(all));
            if (hole == null) throw new ArgumentException("这条边读不出孔径，请重新点一次孔口的圆形边线。");
            hole.Center = pg.Transform.Point(hole.Center);
            hole.Axis = pg.Transform.Vector(hole.Axis).Unit();
            hole.Owner = (object)part ?? DocOf(edge); hole.Placement = pg.Transform;   // 钣金件：记下钣金件文档（0.10.2，原来是空，挂不了天工关联、提示"读不到所在零件"）
            hole.Pick = selection; hole.SourceFace = wallFace;
            hole.Source = "孔口圆边";
            return hole;
        }

        // ---- 点一个平面：把这个面上的孔全认出来（含沉孔/锥沉/倒角孔口下面的孔径） ----
        public static List<ScannedHole> ScanFace(TargetFace target, out string how, out int skippedCount){
            how = ""; skippedCount = 0;
            if (target == null || target.Face == null) return new List<ScannedHole>();
            var all = BodyCircles(target.Part, target.Face);
            bool byLoops;
            var mouths = FaceCircles(target.Face, true, out byLoops);
            V3 outward = OutwardOf(target.Face);
            V3 inward = outward.Finite && outward.Length > 0.5 ? outward * -1.0 : new V3();
            // 内环上的整圆里仍然可能混进"圆凸台的底圆"（实测它同样长在内环上），逐个判一遍。
            var kept = new List<CircleCandidate>();
            foreach (var m in mouths) {
                if (LooksLikeHole(target.Part, target.Face, m)) {
                    ApplyWallHint(m, m.WallFace as G.Face, inward);
                    kept.Add(m);
                } else skippedCount++;
            }
            var holes = HoleScan.FromCircles(kept, all);
            foreach (var h in holes) {
                h.Center = target.Placement.Point(h.Center);
                h.Axis = target.Placement.Vector(h.Axis).Unit();
                h.Owner = (object)target.Part ?? DocOf(target.Face); h.Placement = target.Placement;
                h.Pick = target.Pick; h.SourceFace = target.Face;
                h.Source = target.Label;
            }
            if (!byLoops) how = "这个 CAD 版本读不到面的环信息，识别结果里可能混进轮廓圆角或凸台";
            return holes;
        }

        // ---- 点一个平面：面上的腰孔（0.9.1） ----
        // 原来点面只认圆孔，腰孔一条都不认（得一头一头去点圆弧）。用户实测「点面认不全面上的孔」：
        // 在用户 30 个零件上逐面比对（tools/hole-feature-probe/FaceScanAudit.cs），圆孔没有漏的，漏掉的是腰孔（7 条；另有约 50 个圆角矩形窗口，不是孔）。
        // 判据和点圆弧认腰孔（SlotScanCad.FromArc）一样：内环 = 两段同半径圆弧 + 两条直边；再按圆弧那头的壁判一下是不是腰形凸台。
        public static List<SlotReference> ScanFaceSlots(TargetFace target, out int skippedCount){
            int odd; return ScanFaceSlots(target, out skippedCount, out odd);
        }
        // oddCount（0.10.2）：两段圆弧 + 两条直边、但两头半径不一样的内环（葫芦孔 / 挂孔）。不是腰孔，配不出来；
        // 原来一声不吭跳过，用户点了面以为面上的孔都认进来了（OK310 围栏侧板实测：每面 7 个葫芦孔，一个都没提）。
        public static List<SlotReference> ScanFaceSlots(TargetFace target, out int skippedCount, out int oddCount){
            skippedCount = 0; oddCount = 0;
            var list = new List<SlotReference>();
            if (target == null || target.Face == null) return list;
            var face = target.Face;
            V3 n;
            try { Array p = new double[3], nv = new double[3]; ((G.Plane)face.Geometry).GetPlaneData(ref p, ref nv); n = V3.From(nv).Unit(); }
            catch { return list; }
            try {
                foreach (G.Loop loop in (G.Loops)face.Loops) {
                    bool outer = true;
                    try { outer = loop.IsOuterLoop; } catch { }
                    if (outer) continue;
                    var arcs = new List<CircleCandidate>(); var arcEdges = new List<G.Edge>(); int lines = 0, others = 0;
                    foreach (G.Edge e in (G.Edges)loop.Edges) {
                        object g = null; try { g = e.Geometry; } catch { }
                        if (g is G.Circle) { var c = Circle(e); if (c == null) others++; else { arcs.Add(c); arcEdges.Add(e); } }
                        else if (g is G.Line) lines++;
                        else others++;
                    }
                    if (arcs.Count != 2 || lines != 2 || others != 0) continue;
                    if (!HoleScan.Parallel(arcs[0].Axis, n) || !HoleScan.Parallel(arcs[1].Axis, n)) continue;
                    if (Math.Abs(arcs[0].RadiusMm - arcs[1].RadiusMm) > 0.001) { oddCount++; continue; }
                    double dist = (arcs[1].Center - arcs[0].Center).Length;
                    if (dist < 1e-6) continue;   // 两段圆弧同心 = 拆成两半的圆孔，归 ScanFace 管
                    G.Face wf; WallOf(arcEdges[0], face, out wf); arcs[0].WallFace = wf;
                    if (!LooksLikeHole(target.Part, face, arcs[0])) { skippedCount++; continue; }
                    var mid = (arcs[0].Center + arcs[1].Center) * 0.5;
                    list.Add(new SlotReference {
                        Center = target.Placement.Point(mid),
                        Axis = target.Placement.Vector(n).Unit(),
                        Direction = target.Placement.Vector((arcs[1].Center - arcs[0].Center) * (1.0 / dist)).Unit(),
                        LengthMm = dist * 1000.0 + 2 * arcs[0].RadiusMm, WidthMm = 2 * arcs[0].RadiusMm,
                        Owner = (object)target.Part ?? DocOf(target.Face), Placement = target.Placement, Source = target.Label
                    });
                }
            } catch (Exception e) { Log.Write("HoleScanSlots", e); }
            return list;
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
            P.PartDocument part = null; object owner = null;
            try { owner = face.Document; part = owner as P.PartDocument; } catch { }
            // 钣金件（.psm）上的孔内壁也认（0.10.2；和点钣金件孔边一样，part 为空：只用这个面所在实体的圆边）
            if (part == null && !(owner is P.SheetMetalDocument)) throw new ArgumentException("这个面不属于零件文档，请在装配里直接点零件上的孔内壁。");

            var all = BodyCircles(part, face);
            var geometry = face.Geometry;
            CircleCandidate mouth = null;

            var cylinder = geometry as G.Cylinder;
            if (cylinder != null) {
                Array bp = new double[3], ax = new double[3]; double r = 0;
                cylinder.GetCylinderData(ref bp, ref ax, out r);
                if (!(r > 1e-6)) throw new ArgumentException("这个圆柱面的半径读出来是 0，请重新点一次。");
                if (WallSaysHole(face) == 0) throw new ArgumentException(BossWallMessage);
                V3 basePt = V3.From(bp), unitAx = V3.From(ax).Unit();
                // 圆柱基点可能在轴线任意位置：投影面片包围盒中心到轴线上，并用面片半高限制 InnerBelow 深度
                V3 centerOnAxis = basePt;
                double halfSpanMm = 0;
                Array flo = new double[3], fhi = new double[3];
                try {
                    face.GetRange(ref flo, ref fhi);
                    V3 bLo = V3.From(flo), bHi = V3.From(fhi);
                    if (bLo.Finite && bHi.Finite) {
                        V3 mid = (bLo + bHi) * 0.5;
                        centerOnAxis = basePt + unitAx * (mid - basePt).Dot(unitAx);
                        double spanM = FaceAxisSpan(face, unitAx);
                        if (spanM > 1e-4) halfSpanMm = spanM * 500.0 + 0.5;
                    }
                } catch { }
                mouth = new CircleCandidate { Center = centerOnAxis, Axis = unitAx, RadiusMm = r * 1000.0, Wall = "Cylinder", WallFace = face, MaxStepDepthMm = halfSpanMm };
                label = "圆柱面";
            }

            var cone = geometry as G.Cone;
            if (cone != null) {
                bool byLoops;
                // 导入 STEP 零件的圆锥面边界常把整圆拆成两段半圆：先 MergeArcs 拼整圆再挑最小可用圆口
                var own = HoleScan.MergeArcs(FaceCircles(face, false, out byLoops));
                foreach (var c in own) {
                    if (!HoleScan.Usable(c)) continue;
                    if (mouth == null || c.RadiusMm < mouth.RadiusMm) mouth = c;
                }
                if (mouth == null)
                    throw new ArgumentException("这个圆锥面上读不到圆边（多半是孔口倒角面）。请点孔的圆柱内壁，或点孔口的圆形边线。");
                mouth.Wall = "Cone";
                double coneSpanM = FaceAxisSpan(face, mouth.Axis);
                if (coneSpanM > 1e-4) mouth.MaxStepDepthMm = Math.Max(coneSpanM * 1000.0 + 0.5, mouth.DiameterMm * 1.5);
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

            // all 同样需要 MergeArcs，否则导入件沉孔内壁/孔底的拆段圆弧无法被 InnerBelow 识别
            var hole = HoleScan.FromCircle(mouth, HoleScan.MergeArcs(all));
            if (hole == null) throw new ArgumentException("没能从这条面上算出孔径，请重新点一次。");
            hole.Center = pg.Transform.Point(hole.Center);
            hole.Axis = pg.Transform.Vector(hole.Axis).Unit();
            hole.Owner = (object)part ?? owner; hole.Placement = pg.Transform;
            hole.Pick = selection; hole.SourceFace = face;
            hole.Source = label;
            return hole;
        }
    }
}
