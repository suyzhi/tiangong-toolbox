using System;
using System.Collections.Generic;
using System.Globalization;
using A=SolidEdgeAssembly;
using F=SolidEdgeFramework;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;
using S=SolidEdgeFrameworkSupport;

namespace TianGongCadSuite {
    public static partial class AutoHoleWriter {
        // 全装配孔采集：遍历每个零件实例的所有圆形边，换算到装配坐标。
        // 用于配孔检查。返回的记录带 Selection（可高亮）。
        public static List<HoleRecord> CollectAssemblyHoles(A.AssemblyDocument assembly, out List<string> warnings){
            List<HoleRecord> shafts;
            return CollectAssemblyHoles(assembly, out warnings, out shafts);
        }

        // shafts：凸台外圆（螺栓杆、销、轴），不当孔，但配孔检查要知道它们在哪 —— 孔的轴线穿过同轴的螺栓杆不算漏打孔。
        public static List<HoleRecord> CollectAssemblyHoles(A.AssemblyDocument assembly, out List<string> warnings, out List<HoleRecord> shafts){
            List<AssemblyPart> parts;
            return CollectAssemblyHoles(assembly, out warnings, out shafts, out parts);
        }

        // parts：装配里所有零件实例（含子装配里的，见 AssemblyPart）。配孔检查拿它建射线表、列零件名，只遍历一次。
        public static List<HoleRecord> CollectAssemblyHoles(A.AssemblyDocument assembly, out List<string> warnings, out List<HoleRecord> shafts, out List<AssemblyPart> parts){
            return CollectAssemblyHoles(assembly, out warnings, out shafts, out parts, null);
        }
        // progress(第几个零件, 共几个)：每读完一个零件报一次（窗口拿它刷状态栏、泵消息，免得天工被 Windows 判成"未响应"）
        public static List<HoleRecord> CollectAssemblyHoles(A.AssemblyDocument assembly, out List<string> warnings, out List<HoleRecord> shafts, out List<AssemblyPart> parts, Action<int, int> progress){
            warnings = new List<string>();
            shafts = new List<HoleRecord>();
            var result = new List<HoleRecord>();
            parts = AssemblyPart.List(assembly, warnings);
            var byBucket = new Dictionary<string, List<HoleRecord>>(StringComparer.Ordinal);
            var shaftBuckets = new Dictionary<string, List<HoleRecord>>(StringComparer.Ordinal);
            var edgeCache = new Dictionary<string, LocalCircles>(StringComparer.OrdinalIgnoreCase);
            // 只有判定口径里的孔径（默认 3–25mm）会参与检查；螺栓杆/销只拿来和这些孔比同轴，比孔大的不可能穿过孔。
            // 口径以外的圆边（圆管、大圆角、细小倒圆）原来也逐条判凸台、建引用 —— 每条五六次 COM 调用，白花。
            double minHole = HoleCheck.MinDiameterMm - 0.001, maxHole = HoleCheck.MaxDiameterMm + 0.001;
            double minShaft = HoleCheck.MinDiameterMm * 0.5, maxShaft = HoleCheck.MaxDiameterMm + 1.0;
            int done = 0;
            foreach (var ap in parts) {
            if (progress != null) { try { progress(++done, parts.Count); } catch { } }
            // 同一个实例里所有边共用一个"零件 → 装配"变换：读一次（原来每条圆边各建一个装配引用再取矩阵，2～4 次 COM 调用/边）
            Transform placement;
            try { placement = ap.Placement; }
            catch (Exception e) { if (!ap.NoEdges) warnings.Add(ap.Name + " 读不到装配位置：" + e.Message); continue; }
            for (int bi = 0; bi < ap.Bodies.Count; bi++) {   // 多实体零件每个实体都要看（0.7.15）
                var body = ap.Bodies[bi];
                string partName = ap.Name;
                object rawEdges;
                try { rawEdges = body.get_Edges(G.FeatureTopologyQueryTypeConstants.igQueryAll); }
                catch (Exception e) { warnings.Add(partName + " 读取边失败：" + e.Message); continue; }
                // 同一个零件文件在装配里用了很多次（SolidWorks 转来的 ST064：3533 个实例只有 469 个文件，一个阀岛 3.7 万条边）：
                // 圆边在零件坐标里读一次记下来，别的实例只换算位置。原来每个实例都把每条边重读一遍（每条四五次 COM 调用）。
                // 边数对不上（可调零件之类每个实例几何不同）就重读
                int edgeCount = -1;
                try { edgeCount = ((G.Edges)rawEdges).Count; } catch { }
                string key = ap.File.Length > 0 && edgeCount >= 0 ? ap.File + "|" + bi : null;
                LocalCircles circles;
                if (key == null || !edgeCache.TryGetValue(key, out circles) || circles.EdgeCount != edgeCount) {
                    circles = ReadCircles((G.Edges)rawEdges, edgeCount, minHole, maxHole, minShaft, maxShaft);
                    if (key != null) edgeCache[key] = circles;
                }
                foreach (var lc in circles.Items) {
                    V3 centre, axis;
                    try { centre = placement.Point(lc.Centre); axis = placement.Vector(lc.Axis).Unit(); } catch { continue; }
                    if (!centre.Finite) continue;
                    double dia = lc.Diameter;
                    if (lc.Boss) {           // 螺栓杆、销、圆凸台的外圆不是孔，单独记下；同轴同径两端圆边合并扩展 AxialMin..AxialMax
                        if (dia >= minShaft && dia <= maxShaft) {
                            var srec = new HoleRecord { Center = centre, Axis = axis, DiameterMm = dia, PartName = partName };
                            AddDedupedHole(shaftBuckets, shafts, srec);
                        }
                        continue;
                    }
                    if (dia < minHole || dia > maxHole) continue;
                    var owner = ap; var picked = lc.Edge;
                    var rec = new HoleRecord { Center = centre, Axis = axis, DiameterMm = dia, PartName = partName,
                                               MakeSelection = () => owner.Reference(picked) };
                    // 通孔有上下两条圆边，同一个孔只保留一条记录；按「零件名 + 0.1mm 直径分桶」并检查相邻桶 (b-1,b,b+1)，
                    // 防止 6.3499999 与 6.3500001 这类跨 .x5 边界的上下孔口落进不同桶导致重复记录。
                    AddDedupedHole(byBucket, result, rec);
                }
            }
            }
            return result;
        }
        static void AddDedupedHole(Dictionary<string, List<HoleRecord>> buckets, List<HoleRecord> target, HoleRecord rec){
            int bin = (int)Math.Round(rec.DiameterMm * 10.0);
            for (int db = -1; db <= 1; db++) {
                string nb = rec.PartName + "|" + (bin + db).ToString(CultureInfo.InvariantCulture);
                List<HoleRecord> same;
                if (!buckets.TryGetValue(nb, out same)) continue;
                for (int i = 0; i < same.Count; i++) {
                    if (HoleCheck.SameHoleLine(same[i], rec)) { same[i].Extend(rec.Center); return; }
                }
            }
            string bucket = rec.PartName + "|" + bin.ToString(CultureInfo.InvariantCulture);
            List<HoleRecord> list;
            if (!buckets.TryGetValue(bucket, out list)) { list = new List<HoleRecord>(); buckets[bucket] = list; }
            list.Add(rec);
            target.Add(rec);
        }

        // 一个实体上口径以内的圆边（零件坐标）：孔口 / 螺栓杆外圆。同一个零件文件的各个实例共用（见 CollectAssemblyHoles）
        sealed class LocalCircle { public V3 Centre, Axis; public double Diameter; public bool Boss; public G.Edge Edge; }
        sealed class LocalCircles { public int EdgeCount; public readonly List<LocalCircle> Items = new List<LocalCircle>(); }
        struct ArcCell : IEquatable<ArcCell> {
            public readonly int X, Y, Z;
            public ArcCell(int x, int y, int z){ X = x; Y = y; Z = z; }
            public bool Equals(ArcCell o){ return X == o.X && Y == o.Y && Z == o.Z; }
            public override bool Equals(object obj){ return obj is ArcCell && Equals((ArcCell)obj); }
            public override int GetHashCode(){ unchecked { int h = X; h = (h * 397) ^ Y; h = (h * 397) ^ Z; return h; } }
        }
        struct ArcEntry { public V3 Centre, Axis; public double Radius; }
        static LocalCircles ReadCircles(G.Edges edges, int edgeCount, double minHole, double maxHole, double minShaft, double maxShaft){
            var res = new LocalCircles { EdgeCount = edgeCount };
            // 单段圆弧（腰孔两头、圆角）不是孔（0.8.5）：原来圆弧也当孔收，腰孔压在螺纹孔上这种调节结构，腰孔两头各报一条假的漏打孔。
            // 但导入件常把一个整圆孔口拆成两段：改用 0.02mm 空间网格 + 27 邻格容差比对（圆心 <0.01mm、半径 <0.005mm、轴线平行，
            // 与 HoleScan.MergeArcs 对齐），消除 1µm 字符串取整跨界分裂与区域文化小数点差异。
            const double arcCellM = 2e-5;
            var arcGrid = new Dictionary<ArcCell, List<ArcEntry>>();
            foreach (G.Edge edge in edges) {
                G.Circle circle = null;
                try { circle = edge.Geometry as G.Circle; } catch { }
                if (circle == null) continue;
                Array c = new double[3], ax = new double[3]; double r = 0;
                try { circle.GetCircleData(ref c, ref ax, out r); } catch { continue; }
                if (r <= 0) continue;
                V3 centre = V3.From(c), axis = V3.From(ax);
                if (!centre.Finite || !axis.Finite || axis.Length < 1e-12) continue;
                bool closedEdge = true; try { closedEdge = edge.IsClosed; } catch { }
                if (!closedEdge) {
                    int gx = (int)Math.Floor(centre.X / arcCellM), gy = (int)Math.Floor(centre.Y / arcCellM), gz = (int)Math.Floor(centre.Z / arcCellM);
                    bool matched = false;
                    for (int dx = -1; dx <= 1 && !matched; dx++)
                        for (int dy = -1; dy <= 1 && !matched; dy++)
                            for (int dz = -1; dz <= 1 && !matched; dz++) {
                                List<ArcEntry> cell;
                                if (!arcGrid.TryGetValue(new ArcCell(gx + dx, gy + dy, gz + dz), out cell)) continue;
                                for (int i = 0; i < cell.Count; i++) {
                                    var prev = cell[i];
                                    if ((prev.Centre - centre).Length < 1e-5 && Math.Abs(prev.Radius - r) < 5e-6 && HoleScan.Parallel(prev.Axis, axis)) { matched = true; break; }
                                }
                            }
                    if (!matched) {
                        var ck = new ArcCell(gx, gy, gz);
                        List<ArcEntry> cell;
                        if (!arcGrid.TryGetValue(ck, out cell)) { cell = new List<ArcEntry>(); arcGrid[ck] = cell; }
                        cell.Add(new ArcEntry { Centre = centre, Axis = axis, Radius = r });
                        continue;
                    }
                }
                double dia = r * 2000.0;
                bool holeSize = dia >= minHole && dia <= maxHole, shaftSize = dia >= minShaft && dia <= maxShaft;
                if (!holeSize && !shaftSize) continue;
                res.Items.Add(new LocalCircle { Centre = centre, Axis = axis, Diameter = dia, Boss = HoleScanCad.IsBossEdge(edge), Edge = edge });
            }
            return res;
        }

        // 轴线是否穿过某个零件的材料（用于判"漏打孔"）。
        //
        // 这个函数会被调用 S×P 次（S = 待判的孤孔数，P = 装配零件数）。原来的写法每次都要
        // 重新遍历一遍 assembly.Occurrences 去按名字找零件 —— 每次 occ.Name 都是一次跨进程
        // COM 调用，于是整体变成 O(S×P²) 次 COM 调用，大装配上要跑分钟级。
        // 改成：按名字一次性建好「零件名 -> 射线目标」的表，后面查表 O(1)。
        //
        // 每个实例同时记下自己的装配变换：实体几何在零件坐标系里，孔心/轴线是装配坐标，
        // 打射线前要换算（见 PartRayTarget）。原来直接拿装配坐标打，零件只要不在原点就会打偏。
        // 另外原来的 Build 会先把传进来的名字全部占位成 null，随后遍历实例时又因为"名字已存在"
        // 跳过 —— 配孔检查窗口传的是全部零件名，于是所有实体都是 null，漏打孔一条也报不出来。
        public static class OccurrenceBodies {
            // names 仅为兼容旧调用保留，不再使用：实例名直接从装配里读。
            public static PartRayIndex Build(A.AssemblyDocument assembly, List<string> names){
                return Build(AssemblyPart.List(assembly, null));
            }
            // 子装配里的零件也进表（AssemblyPart）：它们一样可能是"漏打孔的那个零件"
            public static PartRayIndex Build(IList<AssemblyPart> parts){
                var index = new PartRayIndex();
                if (parts == null) return index;
                foreach (var ap in parts) {
                    Transform placement;
                    try { placement = ap.Placement; }
                    catch (Exception e) { if (!ap.NoEdges) Log.Write("AutoHoleCheckMatrix " + ap.Name, e); continue; }   // 读不到变换宁可不判，也不按原点瞎判
                    var bodies = ap.Bodies;
                    // 每个实体自己的包围盒（零件坐标，读不到为 null）：直线连某个实体的包围盒都碰不到，它的面就不可能被射中，不用问 CAD。
                    // SolidWorks 转来的导入件常是几百个实体的一个零件（ST064 的 BOSCH 柔性线 434 个实体）：原来每打一条射线
                    // 把 434 个实体挨个问一遍，这一个零件就占了配孔检查一分多钟
                    var bodyBoxes = new Box3?[bodies.Count];
                    for (int i = 0; i < bodies.Count; i++) bodyBoxes[i] = BodyBox(bodies[i]);
                    var target = PartRayTarget.WithBoxes(ap.Name, placement, (o, d) => {
                        var all = new List<Box3>();
                        for (int i = 0; i < bodies.Count; i++) {   // 多实体零件：哪个实体的材料都算
                            if (bodyBoxes[i].HasValue && !PartRayTarget.NearBox(bodyBoxes[i].Value, o, d, -1e9, 1e9, 1e-5)) continue;
                            all.AddRange(CastBoxes(bodies[i], o, d));
                        }
                        return all;
                    });
                    target.World = WorldBox(bodyBoxes, placement);
                    target.Local = LocalBox(bodyBoxes);
                    target.BodyLocals = LocalBoxes(bodyBoxes);
                    index.Add(target);
                }
                return index;
            }
        }

        static Box3[] LocalBoxes(IList<Box3?> boxes){
            if (boxes == null || boxes.Count == 0) return null;
            var arr = new Box3[boxes.Count];
            for (int i = 0; i < boxes.Count; i++) {
                if (!boxes[i].HasValue) return null;
                arr[i] = boxes[i].Value;
            }
            return arr;
        }

        static Box3? BodyBox(G.Body b){
            Array mn = new double[3], mx = new double[3];
            try { b.GetRange(ref mn, ref mx); } catch { return null; }
            V3 a = V3.From(mn), z = V3.From(mx);
            if (!a.Finite || !z.Finite) return null;
            return new Box3(a, z);
        }
        // 各实体局部包围盒的并集；任何一个实体读不到就返回 null（不预筛）
        static Box3? LocalBox(IList<Box3?> boxes){
            if (boxes == null || boxes.Count == 0) return null;
            V3 lo = new V3(double.MaxValue, double.MaxValue, double.MaxValue), hi = new V3(double.MinValue, double.MinValue, double.MinValue);
            foreach (var bb in boxes) {
                if (!bb.HasValue) return null;
                V3 a = bb.Value.Lo, z = bb.Value.Hi;
                lo = new V3(Math.Min(lo.X, a.X), Math.Min(lo.Y, a.Y), Math.Min(lo.Z, a.Z)); hi = new V3(Math.Max(hi.X, z.X), Math.Max(hi.Y, z.Y), Math.Max(hi.Z, z.Z));
            }
            return new Box3(lo, hi);
        }
        // 实例的全部实体在装配坐标里的包围盒（局部包围盒 8 个角变换过去再取范围）。任何一个实体读不到就返回 null（不预筛）
        static Box3? WorldBox(IList<Box3?> boxes, Transform placement){
            if (boxes == null || boxes.Count == 0 || placement == null) return null;
            double[] lo = { double.MaxValue, double.MaxValue, double.MaxValue }, hi = { double.MinValue, double.MinValue, double.MinValue };
            foreach (var bb in boxes) {
                if (!bb.HasValue) return null;
                V3 a = bb.Value.Lo, z = bb.Value.Hi;
                for (int k = 0; k < 8; k++) {
                    var p = placement.Point(new V3((k & 1) == 0 ? a.X : z.X, (k & 2) == 0 ? a.Y : z.Y, (k & 4) == 0 ? a.Z : z.Z));
                    lo[0] = Math.Min(lo[0], p.X); lo[1] = Math.Min(lo[1], p.Y); lo[2] = Math.Min(lo[2], p.Z);
                    hi[0] = Math.Max(hi[0], p.X); hi[1] = Math.Max(hi[1], p.Y); hi[2] = Math.Max(hi[2], p.Z);
                }
            }
            return new Box3(new V3(lo[0], lo[1], lo[2]), new V3(hi[0], hi[1], hi[2]));
        }

        // 零件坐标里这条**直线**碰到的面，各自的包围盒（零件坐标）。
        // get_FacesByRay 实测是整条直线的查询、只给面不给交点位置（AUTO-HOLE-SCAN-VALIDATION-20261005），
        // 所以再用 Face.GetRange（同样实测：3D 包围盒）把交点的大致位置框出来，供配孔检查判"离孔多远"。
        // 读不到包围盒的面按无穷大处理（= 原来的判法，宁可多报）。
        static IList<Box3> CastBoxes(G.Body body, V3 origin, V3 dir){
            var boxes = new List<Box3>();
            object faces;
            try { faces = body.get_FacesByRay(origin.X, origin.Y, origin.Z, dir.X, dir.Y, dir.Z); } catch { return boxes; }
            var coll = faces as System.Collections.IEnumerable;
            if (coll == null) return boxes;
            foreach (object o in coll) {
                var f = o as G.Face;
                if (f == null) { if (o != null) boxes.Add(Box3.Infinite); continue; }
                Array lo = new double[3], hi = new double[3];
                try { f.GetRange(ref lo, ref hi); boxes.Add(new Box3(V3.From(lo), V3.From(hi))); }
                catch { boxes.Add(Box3.Infinite); }
            }
            return boxes;
        }

        // 在零件坐标里打一条单向射线；命中任何面即认为穿过了该零件的材料。
        static bool CastLocal(G.Body body, V3 origin, V3 dir){
            try {
                var faces = body.get_FacesByRay(origin.X, origin.Y, origin.Z, dir.X, dir.Y, dir.Z);
                var coll = faces as System.Collections.IEnumerable;
                if (coll != null) foreach (object o in coll) if (o != null) return true;
            } catch { }
            return false;
        }

        // 兼容旧签名（没有预建表时现建一次，慢但不至于错）
        public static bool RayPassesThrough(A.AssemblyDocument assembly, HoleRecord hole, string partName){
            return OccurrenceBodies.Build(assembly, null).Hits(hole, partName);
        }
    }
}
