using System;
using System.Collections.Generic;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;

namespace TianGongCadSuite {
    // 多实体零件（0.7.13）：一个零件文档里可以有好几个设计体，每个设计体是 part.Models 里的一个 Model。
    // 现场 YF25.07G01-L1-M30-02-07.par：39 个设计体（39 个复制构造 + 切换为设计体）。插件原来一律用 Models.Item(1)，实测
    // （tools/hole-feature-probe/PartDrillProbe.cs，2026-10-22）：
    //   · Holes.AddThroughAll / AddFinite 在这种零件上对哪个实体都打不上（0x80010108 / 0x800401FD），实体 1 也不行，MakeActive 也救不回来；
    //   · Holes.AddMultiBody(…, BodyArray = { 目标实体的 Body }) 能打（BodyArray 放 Model 是 E_FAIL；错的那一侧照样 igFeatureFailed）。
    // 所以：目标面在哪个实体上就用哪个 Model；实体不止一个时建孔走 AddMultiBody。单实体零件照旧走老路。
    public static class PartBodies {
        public static int Count(P.PartDocument part){ try { return part == null ? 0 : part.Models.Count; } catch { return 0; } }
        public static bool Multi(P.PartDocument part){ return Count(part) > 1; }

        // 第 index 个实体（1 起）；越界时退回第 1 个
        public static P.Model Model(P.PartDocument part, int index){
            int n = Count(part);
            if (n < 1) return null;
            if (index < 1 || index > n) index = 1;
            try { return (P.Model)part.Models.Item(index); } catch { }
            try { return (P.Model)part.Models.Item(1); } catch { return null; }
        }
        public static G.Body Body(P.Model model){ try { return model == null ? null : (G.Body)model.Body; } catch { return null; } }
        public static IEnumerable<P.Model> All(P.PartDocument part){
            int n = Count(part);
            for (int i = 1; i <= n; i++) {
                P.Model m = null;
                try { m = (P.Model)part.Models.Item(i); } catch { }
                if (m != null) yield return m;
            }
        }

        // 面片在第几个实体上（1 起，零件局部坐标的平面数据兜底）。单实体恒为 1。
        // 先比 Body.Tag；面片已失效、Tag 对不上时：找和这个平面共面、包围盒含 point 的面在哪个实体上。都认不出按第 1 个。
        public static int IndexOf(P.PartDocument part, G.Face face, V3 point, V3 normal){
            int n = Count(part);
            if (n <= 1) return 1;
            int tag = 0; bool hasTag = false;
            try { tag = ((G.Body)face.Body).Tag; hasTag = true; } catch { }
            if (hasTag)
                for (int i = 1; i <= n; i++) { try { if (((G.Body)((P.Model)part.Models.Item(i)).Body).Tag == tag) return i; } catch { } }
            for (int i = 1; i <= n; i++)
                if (PlaneFace(Body(Model(part, i)), point, normal, true) != null) return i;
            Log.Write("PartBodies", "认不出所选面在第几个实体上（共 " + n + " 个），按第 1 个");
            return 1;
        }

        const double OnTolM = 1e-6;
        const double BoxTolM = 1e-5;

        // 实体上和 (point, normal) 共面的平面面；inBox 时还要求 point 落在那张面的包围盒里（同一实体上共面的面取最贴的一张）
        public static G.Face PlaneFace(G.Body body, V3 point, V3 normal, bool inBox){
            if (body == null) return null;
            G.Face best = null; double bestD = OnTolM;
            try {
                foreach (G.Face f in (G.Faces)body.get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll)) {
                    G.Plane pl = null;
                    try { pl = f.Geometry as G.Plane; } catch { }
                    if (pl == null) continue;
                    Array p = new double[3], nv = new double[3];
                    try { pl.GetPlaneData(ref p, ref nv); } catch { continue; }
                    var u = V3.From(nv).Unit();
                    if (Math.Abs(Math.Abs(u.Dot(normal)) - 1) > 1e-6) continue;
                    double d = Math.Abs((V3.From(p) - point).Dot(normal));
                    if (d > bestD) continue;
                    if (inBox && !InBox(f, point)) continue;
                    bestD = d; best = f;
                }
            } catch (Exception e) { Log.Write("PartBodiesFace", e); }
            return best;
        }
        static bool InBox(G.Face f, V3 point){
            try {
                Array lo = new double[3], hi = new double[3];
                f.GetRange(ref lo, ref hi);
                V3 a = V3.From(lo), b = V3.From(hi);
                return point.X >= a.X - BoxTolM && point.X <= b.X + BoxTolM
                    && point.Y >= a.Y - BoxTolM && point.Y <= b.Y + BoxTolM
                    && point.Z >= a.Z - BoxTolM && point.Z <= b.Z + BoxTolM;
            } catch { return false; }
        }
    }
}
