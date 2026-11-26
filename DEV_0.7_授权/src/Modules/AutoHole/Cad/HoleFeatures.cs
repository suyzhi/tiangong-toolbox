using System;
using System.Collections.Generic;
using System.Globalization;
using A=SolidEdgeAssembly;
using F=SolidEdgeFramework;
using P=SolidEdgePart;

namespace TianGongCadSuite {
    // 孔特征上的关联记录（AttributeSets，随零件文件保存；真机实测保存重开后还在）。
    public static class HoleLinkStore {
        public static void Write(P.Hole hole, HoleLink link){
            if (hole == null || link == null) return;
            var sets = (F.AttributeSets)hole.AttributeSets;
            F.AttributeSet set = null;
            bool present = false;
            try { present = hole.get_IsAttributeSetPresent(HoleLink.AttributeSet); } catch { }
            if (present) { try { set = (F.AttributeSet)sets.Item(HoleLink.AttributeSet); } catch { set = null; } }
            if (set == null) set = sets.Add(HoleLink.AttributeSet);
            F.Attribute at = null;
            try { at = (F.Attribute)set.Item(HoleLink.AttributeName); } catch { at = null; }
            if (at == null) at = set.Add(HoleLink.AttributeName, F.AttributeTypeConstants.seStringUnicode);
            at.Value = link.Format();
        }

        public static HoleLink Read(P.Hole hole){
            if (hole == null) return null;
            try {
                if (!hole.get_IsAttributeSetPresent(HoleLink.AttributeSet)) return null;
                var set = (F.AttributeSet)((F.AttributeSets)hole.AttributeSets).Item(HoleLink.AttributeSet);
                var at = (F.Attribute)set.Item(HoleLink.AttributeName);
                return HoleLink.Parse(at.Value as string);
            } catch (Exception e) { Log.Write("HoleLinkRead", e); return null; }
        }
    }

    // 零件里的孔特征和它们的孔心（零件局部）。认"参考孔是哪个孔特征"、同步时读孔特征现在的位置，都靠它。
    public static class HoleFeatures {
        public sealed class Entry {
            public P.Hole Hole;
            public string SystemName = "";
            public string Name = "";
            public readonly List<V3> Centers = new List<V3>();   // 每个 Hole2d 的孔心（零件局部）
            public V3 Normal = new V3(0, 0, 1);                   // 轮廓平面法向 = 孔轴方向（零件局部）
            public bool HasNormal;
        }

        public static List<Entry> Read(P.PartDocument part){ return Read(part, false); }

        // linkedOnly：只读带关联记录的孔特征（孔位检查用 —— 不相干的孔不去读轮廓，省一大半 COM 调用）
        public static List<Entry> Read(P.PartDocument part, bool linkedOnly){
            var list = new List<Entry>();
            if (part == null) return list;
            // 多实体零件每个实体都读（0.10.1）：原来只读第 1 个实体，实体 2 以后打的插件关联孔「检查 / 同步孔位」永远看不到
            var models = new List<P.Model>(PartBodies.All(part));
            if (models.Count == 0) return list;
            var all = new List<P.Hole>();
            foreach (var model in models) {
                int n = 0; try { n = model.Holes.Count; } catch { }
                for (int i = 1; i <= n; i++) { try { all.Add(model.Holes.Item(i)); } catch { } }
                // 多孔孔特征（0.7.9 起同规格的孔都合在里面）的输入孔不在 Model.Holes 里：原来「同步孔位」和"参考孔是哪个孔特征"都看不到它们。
                // 0.8.4 起挂不上天工关联的孔照样进多孔特征、在输入孔上写插件关联记录（真机 LinkGroupProbe U1～U3：能写、能单独挪）
                int un = 0; try { un = model.UserDefinedPatterns.Count; } catch { }
                for (int i = 1; i <= un; i++) { try { all.AddRange(HoleCombine.Inputs(model.UserDefinedPatterns.Item(i))); } catch { } }
            }
            foreach (var h in all) {
                if (h == null) continue;
                if (linkedOnly) {
                    bool present = false;
                    try { present = h.get_IsAttributeSetPresent(HoleLink.AttributeSet); } catch { }
                    if (!present) continue;
                }
                var e = new Entry { Hole = h };
                try { e.SystemName = h.SystemName ?? ""; } catch { }
                try { e.Name = h.Name ?? ""; } catch { }
                ReadProfile(h, e);
                list.Add(e);
            }
            return list;
        }

        static void ReadProfile(P.Hole h, Entry e){
            P.Profile prof = null;
            try { prof = h.Profile as P.Profile; } catch { }
            if (prof == null) return;
            try {
                int c = prof.Holes2d.Count;
                for (int k = 1; k <= c; k++) {
                    double x2, y2, x, y, z;
                    prof.Holes2d.Item(k).GetCenterPoint(out x2, out y2);
                    prof.Convert2DCoordinate(x2, y2, out x, out y, out z);
                    e.Centers.Add(new V3(x, y, z));
                }
            } catch (Exception ex) { Log.Write("HoleFeatureCenters", ex); }
            try {
                var plane = prof.Plane as P.RefPlane;
                if (plane != null) { Array nv = new double[3]; plane.GetNormal(ref nv); e.Normal = V3.From(nv).Unit(); e.HasNormal = true; }
            } catch { }
        }

        // 参考孔（零件局部的孔心 + 轴线）是哪个孔特征打出来的：孔特征的轴线与参考轴线平行且某个孔心落在这根轴线上（径向 ≤0.02mm）。
        public static Entry OnAxis(List<Entry> entries, V3 center, V3 axis){
            if (entries == null) return null;
            V3 a;
            try { a = axis.Unit(); } catch { return null; }
            Entry best = null; double bestR = 0.00002;
            foreach (var e in entries) {
                // 必须校验轴线平行：否则块件上与参考孔正交的侧孔若孔心恰落在轴线上会被误匹配，进而覆盖 refAxis 导致同步失败
                if (e.HasNormal && Math.Abs(e.Normal.Dot(a)) < 0.99) continue;
                foreach (var c in e.Centers) {
                    var d = c - center;
                    double radial = (d - a * d.Dot(a)).Length;
                    if (radial <= bestR) { bestR = radial; best = e; }
                }
            }
            return best;
        }

        public static Entry BySystemName(List<Entry> entries, string systemName){
            if (entries == null || string.IsNullOrEmpty(systemName)) return null;
            foreach (var e in entries) if (e.SystemName == systemName) return e;
            return null;
        }
    }
}
