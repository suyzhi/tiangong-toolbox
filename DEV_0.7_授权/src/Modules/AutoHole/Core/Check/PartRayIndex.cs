using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace TianGongCadSuite {
    // 射线判据里的一个零件实例。
    // 零件实体（G.Body）的几何在零件自己的坐标系里，而 HoleRecord 的孔心/轴线是装配坐标：
    // 必须先用这个实例的装配变换换算回零件坐标再打射线，否则零件只要在装配里有平移/旋转，
    // 射线就打在错误的位置（同一个 .par 在装配里用了多次时，每个实例的变换也各不相同）。
    // castLocal(原点, 单位方向) 在零件坐标里打一条单向射线，碰到材料返回 true。
    //
    // 2026-10-13：射线判据加"距离"。真机实测（AUTO-HOLE-SCAN-VALIDATION-20261005）：
    // Body.get_FacesByRay 查的是**整条直线**，不是射线 —— 原来只要孔的轴线在装配里**任何地方**
    // 碰到别的零件（比如一米外的另一根型材），就报"漏打孔"。现在用命中面的包围盒（Face.GetRange，
    // 同样实测过）算出直线穿过那张面的大致位置，只有离这个孔沿轴线不超过 CoaxialSpanTolMm 的才算。
    public sealed class PartRayTarget {
        public readonly string Name;
        public readonly Transform Placement;
        readonly Func<V3, V3, bool> castLocal;
        readonly Func<V3, V3, IList<Box3>> boxesLocal;
        // 这个实例在装配坐标里的包围盒（读不到为 null）。配孔检查要拿每个孤孔去问"装配里每个零件"，
        // 每问一次就是一次 get_FacesByRay（COM）—— 孤孔 × 零件数，围栏上几十万次。先用包围盒在内存里挡掉
        // 根本够不着的零件（直线段离包围盒都远的，不可能碰到它的面），只对够得着的才去问 CAD。
        public Box3? World;
        // 问了 CAD 几次、一共花了多久（Stopwatch 刻度）：大装配上配孔检查慢在哪个零件，计时探针读它
        public int Casts; public long CastTicks;
        public PartRayTarget(string name, Transform placement, Func<V3, V3, bool> castLocal){
            if (placement == null) throw new ArgumentNullException("placement");
            Name = name ?? ""; Placement = placement; this.castLocal = castLocal;
        }
        PartRayTarget(string name, Transform placement, Func<V3, V3, IList<Box3>> boxesLocal, bool unused){
            if (placement == null) throw new ArgumentNullException("placement");
            Name = name ?? ""; Placement = placement; this.boxesLocal = boxesLocal;
        }
        // boxesLocal(原点, 单位方向)：零件坐标里这条**直线**碰到的面，各自的包围盒
        public static PartRayTarget WithBoxes(string name, Transform placement, Func<V3, V3, IList<Box3>> boxesLocal){
            return new PartRayTarget(name, placement, boxesLocal, true);
        }
        // 不限距离：轴线所在的直线碰到这个零件就算（只给旧的布尔判据用）。
        public bool Hits(V3 centreAssembly, V3 axisAssembly){
            if (boxesLocal != null) return Hits(centreAssembly, axisAssembly, -1e9, 1e9, 0);
            if (castLocal == null) return false;
            var origin = Placement.InversePoint(centreAssembly);
            var dir = Placement.InverseNormal(axisAssembly);   // 刚体变换：方向的逆旋转就是转置
            if (!origin.Finite || !dir.Finite || dir.Length < 1e-12) return false;
            dir = dir.Unit();
            return castLocal(origin, dir) || castLocal(origin, dir * -1);
        }
        // 限距离：直线 origin + axis·t 只在 t ∈ [lo - tol, hi + tol]（米）这一段碰到零件才算。
        // 直线 origin + axis·t（t ∈ [lo - tol, hi + tol]）这一段够不够得着包围盒（盒子四周放宽 tol）。
        // 只用来"挡掉肯定碰不到的"：face 的包围盒都在实体包围盒里，段碰得到 face 就一定碰得到这个盒子。
        public static bool NearBox(Box3 box, V3 origin, V3 axis, double lo, double hi, double tol){
            if (!axis.Finite || axis.Length < 1e-12) return true;
            V3 d = axis.Unit();
            double pad = Math.Max(tol, 0) + 1e-6;
            var grown = new Box3(new V3(box.Lo.X - pad, box.Lo.Y - pad, box.Lo.Z - pad), new V3(box.Hi.X + pad, box.Hi.Y + pad, box.Hi.Z + pad));
            double t0, t1;
            if (!grown.Clip(origin, d, out t0, out t1)) return false;
            return t1 >= lo - tol && t0 <= hi + tol;
        }
        public bool Hits(V3 originAssembly, V3 axisAssembly, double lo, double hi, double tol){
            if (boxesLocal == null) return Hits(originAssembly, axisAssembly);   // 没有包围盒信息：退回不限距离
            if (World.HasValue && !NearBox(World.Value, originAssembly, axisAssembly, lo, hi, tol)) return false;
            var origin = Placement.InversePoint(originAssembly);
            var dir = Placement.InverseNormal(axisAssembly);
            if (!origin.Finite || !dir.Finite || dir.Length < 1e-12) return false;
            dir = dir.Unit();
            IList<Box3> boxes;
            long t0w = System.Diagnostics.Stopwatch.GetTimestamp();
            try { boxes = boxesLocal(origin, dir); } catch { return false; }
            finally { Casts++; CastTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0w; }
            if (boxes == null) return false;
            const double eps = 1e-4;
            double beyondHi = double.PositiveInfinity, beforeLo = double.NegativeInfinity;
            foreach (var b in boxes) {
                double t0, t1;
                if (!b.Clip(origin, dir, out t0, out t1)) continue;
                if (t1 < lo - tol || t0 > hi + tol) continue;
                if (t1 > lo + eps && t0 < hi - eps) return true;      // 面横穿孔自己的那一段：两个零件在这里叠着
                if (t0 >= hi - eps) beyondHi = Math.Min(beyondHi, t0);
                else beforeLo = Math.Max(beforeLo, t1);
            }
            // 0.10.2：孔口贴着对方零件的外轮廓、却没贴着材料（中间空了 > SlotGap）= 孔正对着对方的 T 型槽口 / 凹槽，
            // 螺钉进的是槽里的 T 型螺母，对方本来就不打孔。真机（宝马托盘升降门，SolidWorks 转过来的）：支架 Φ6.2 孔正对铝型材的槽口，
            // 型材材料在孔口外 3mm 才开始（RayDebug），按"3mm 以内算挨着"报了漏打孔；型材上这类误报 40 来条。
            // 孔口在对方外轮廓之外（两个零件之间本来就隔着垫片 / 支座的空当）照旧按 tol 算
            bool hitHi = !double.IsPositiveInfinity(beyondHi), hitLo = !double.IsNegativeInfinity(beforeLo);
            var unit = axisAssembly.Unit();
            if (hitHi && beyondHi - hi > SlotGap && InsideWorld(originAssembly + unit * hi)) hitHi = false;
            if (hitLo && lo - beforeLo > SlotGap && InsideWorld(originAssembly + unit * lo)) hitLo = false;
            return hitHi || hitLo;
        }
        const double SlotGap = 0.0005;   // 孔口和对方材料之间空出来超过 0.5mm 才算"对着槽口"
        // 零件自己坐标里的包围盒（各实体局部范围的并集）。判"孔口在不在对方外轮廓里"用它 —— 斜放的零件，World 换到装配坐标再取范围会胖一圈
        public Box3? Local;
        bool InsideWorld(V3 p){
            const double pad = 0.0002;
            if (Local.HasValue) {
                var q = Placement.InversePoint(p); var l = Local.Value;
                return q.X >= l.Lo.X - pad && q.X <= l.Hi.X + pad && q.Y >= l.Lo.Y - pad && q.Y <= l.Hi.Y + pad && q.Z >= l.Lo.Z - pad && q.Z <= l.Hi.Z + pad;
            }
            if (!World.HasValue) return false;
            var w = World.Value;
            return p.X >= w.Lo.X - pad && p.X <= w.Hi.X + pad && p.Y >= w.Lo.Y - pad && p.Y <= w.Hi.Y + pad && p.Z >= w.Lo.Z - pad && p.Z <= w.Hi.Z + pad;
        }
    }

    // 「零件实例名 -> 射线目标」表。实例名（occ.Name）在装配里唯一，重复出现时只认第一个。
    public sealed class PartRayIndex {
        readonly Dictionary<string, PartRayTarget> byName = new Dictionary<string, PartRayTarget>(StringComparer.Ordinal);
        public bool Add(PartRayTarget target){
            if (target == null || byName.ContainsKey(target.Name)) return false;
            byName[target.Name] = target; return true;
        }
        public int Count { get { return byName.Count; } }
        public IEnumerable<PartRayTarget> All { get { return byName.Values; } }
        // 按这个孔自己的轴向范围 ± CoaxialSpanTolMm 查
        public bool Hits(HoleRecord hole, string partName){
            if (hole == null) return false;
            double tol = HoleCheck.CoaxialSpanTolMm / 1000.0;
            return Hits(partName, hole.Center, hole.Axis, hole.AxialMin, hole.AxialMax, tol);
        }
        public bool Hits(string partName, V3 origin, V3 axis, double lo, double hi, double tol){
            PartRayTarget t;
            if (partName == null || !byName.TryGetValue(partName, out t)) return false;
            return t.Hits(origin, axis, lo, hi, tol);
        }
    }
}
