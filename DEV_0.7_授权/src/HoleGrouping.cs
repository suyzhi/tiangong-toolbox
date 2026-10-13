using System;
using System.Collections.Generic;
using A=SolidEdgeAssembly;
using F=SolidEdgeFramework;
using P=SolidEdgePart;

namespace TianGongCadSuite {
    // 打完一批孔以后：同一张面、同一规格的孔编成一个特征组，并且**并进之前那批的组**。
    //
    // 用户反馈：同一个面分两次打 M6 螺纹孔，特征树里出来两个组「…×4」「…×2 (2)」，应该是一个「…×6」。
    // 天工接口没有"往已有组里加成员"的方法（FeatureGroup 只有 Ungroup），所以做法是：
    //   找到之前那批（同零件 + 同一张面 + 同规格）→ 散掉旧组 → 旧成员 + 新孔重新编一组 → 组名改成 ×总数。
    //   真机实测（tools/hole-feature-probe R4）：散组再编组可行，成员、名字、属性都在。
    // 安全边界（宁可不并，也不能把模型弄乱）：
    //   · 特征树里旧组和新孔之间夹着别的特征（顺序建模里重编组可能牵动特征顺序）→ 不并，新孔单独成组；
    //   · 重编失败 → 按原成员、原名字把旧组恢复回去，新孔单独成组；
    //   · 之前那批只有一个孔（没建组）→ 和新孔一起编组，旧孔改成「…-1」的成员名。
    // 怎么认"之前那批"：孔特征/组上挂的 TGAutoHoleSpec 属性（规格键）；0.7.3 打的孔没挂这个属性，
    // 按名字认（「规格短名-序号」「规格短名」「规格短名 ×N」）。面：孔特征轮廓所在平面相同。
    public static class HoleGrouping {
        public const string SpecSet = "TGAutoHoleSpec", SpecAttr = "Key";
        const double PlaneTol = 1e-6;   // 米

        sealed class Batch {
            public readonly List<P.Hole> Holes = new List<P.Hole>();   // 之前那批（按特征树里的顺序）
            public P.FeatureGroup Group;                               // 它们所在的组（没有组为 null）
            public string GroupName = "";
        }

        // 新打的同规格孔 newHoles（同一张面）。taken：零件里已占用的名字（会就地更新）。
        // 返回特征树里出现的节点名（组名或单孔名），供结果框显示。
        public static string Apply(P.Model model, HoleSpec spec, IList<P.Hole> newHoles, HashSet<string> taken, List<string> notes){
            if (model == null || newHoles == null || newHoles.Count == 0) return "";
            string key = HoleNaming.SpecKey(spec);
            foreach (var h in newHoles) Tag(h, key);
            Batch old = null;
            try { old = FindPrevious(model, spec, key, newHoles); } catch (Exception e) { Log.Write("HoleGroupingFind", e); }
            int total = newHoles.Count + (old == null ? 0 : old.Holes.Count);

            if (total == 1) {
                string single = HoleNaming.Unique(HoleNaming.Short(spec), taken);
                if (TryName(newHoles[0], single)) taken.Add(single);
                return single;
            }

            if (old != null && !Contiguous(model, old, newHoles)) {
                notes.Add("「" + HoleNaming.Short(spec) + "」之前那批孔和这次的孔之间夹着别的特征，没有并进同一个组（避免牵动特征顺序），这次的孔单独成组。");
                old = null;
            }

            // 之前那批是单孔（没建组）：先把它改成成员名「…-1」
            if (old != null && old.Group == null && old.Holes.Count == 1) {
                var nm = old.Holes[0].Name;
                taken.Remove(nm);
                var n1 = HoleNaming.Members(spec, 1, taken);
                if (n1.Count == 1 && TryName(old.Holes[0], n1[0])) taken.Add(n1[0]); else taken.Add(nm);
            }
            var names = HoleNaming.Members(spec, newHoles.Count, taken);
            for (int k = 0; k < newHoles.Count; k++) if (TryName(newHoles[k], names[k])) taken.Add(names[k]);

            if (old != null) {
                string merged = Merge(model, spec, key, old, newHoles, taken, notes);
                if (merged.Length > 0) return merged;
            }
            if (newHoles.Count == 1) return newHoles[0].Name;   // 并组失败、这次又只有一个孔：不单独成组
            return NewGroup(model, spec, key, newHoles, taken, notes);
        }

        static string NewGroup(P.Model model, HoleSpec spec, string key, IList<P.Hole> holes, HashSet<string> taken, List<string> notes){
            string groupName = HoleNaming.Unique(HoleNaming.Group(spec, holes.Count), taken);
            try {
                var g = MakeGroup(model, holes);
                try { g.Name = groupName; } catch (Exception e) { Log.Write("HoleGroupingName", e); }
                Tag(g, key);
                taken.Add(groupName);
                return groupName;
            } catch (Exception e) {
                Log.Write("HoleGroupingNew", e);
                notes.Add(holes.Count + " 个「" + HoleNaming.Short(spec) + "」没能编成一组（" + AutoHoleWriter.Friendly(e) + "），已逐个命名。");
                return HoleNaming.Short(spec) + " 等 " + holes.Count + " 个孔";
            }
        }

        // 旧组散掉、旧成员 + 新孔重编；失败就把旧组原样恢复，返回 ""（调用方会让新孔单独成组）。
        static string Merge(P.Model model, HoleSpec spec, string key, Batch old, IList<P.Hole> newHoles, HashSet<string> taken, List<string> notes){
            var all = new List<P.Hole>(old.Holes);
            all.AddRange(newHoles);
            if (old.Group != null) {
                try { old.Group.Ungroup(); }
                catch (Exception e) { Log.Write("HoleGroupingUngroup", e); return ""; }   // 散不了：旧组没动，新孔单独成组
                taken.Remove(old.GroupName);
            }
            string groupName = HoleNaming.Unique(HoleNaming.Group(spec, all.Count), taken);
            try {
                var g = MakeGroup(model, all);
                if (g.Count != all.Count) throw new InvalidOperationException("重编后的组只有 " + g.Count + " 个成员");
                try { g.Name = groupName; } catch (Exception e) { Log.Write("HoleGroupingName", e); }
                Tag(g, key);
                taken.Add(groupName);
                return groupName;
            } catch (Exception e) {
                Log.Write("HoleGroupingMerge", e);
                if (old.Group != null) {
                    // 恢复旧组：原成员、原名字
                    try {
                        var back = MakeGroup(model, old.Holes);
                        try { back.Name = old.GroupName; } catch { }
                        Tag(back, key);
                        taken.Add(old.GroupName);
                    } catch (Exception e2) {
                        Log.Write("HoleGroupingRestore", e2);
                        notes.Add("之前的特征组「" + old.GroupName + "」散开后没能恢复，组里的孔都还在，只是不再成组。");
                    }
                }
                notes.Add("这次的「" + HoleNaming.Short(spec) + "」没能并进之前的组（" + AutoHoleWriter.Friendly(e) + "），已单独成组。");
                return "";
            }
        }

        static P.FeatureGroup MakeGroup(P.Model model, IList<P.Hole> holes){
            var items = new object[holes.Count];
            for (int i = 0; i < holes.Count; i++) items[i] = holes[i];
            Array arr = items;
            var g = model.FeatureGroups.AddFeatureGroupsBySet(items.Length, ref arr);
            if (g == null) throw new InvalidOperationException("CAD 未返回特征组");
            return g;
        }

        // ---- 找之前那批：同规格（属性或名字）+ 同一张面（轮廓平面）----
        static Batch FindPrevious(P.Model model, HoleSpec spec, string key, IList<P.Hole> newHoles){
            var exclude = new HashSet<string>(StringComparer.Ordinal);
            foreach (var h in newHoles) exclude.Add(Sys(h));
            V3 n0, c0;
            if (!PlaneOf(newHoles[0], out n0, out c0)) return null;
            string shortName = HoleNaming.Short(spec);

            // 每个孔属于哪个组
            var groupOf = new Dictionary<string, P.FeatureGroup>(StringComparer.Ordinal);
            int gc = 0; try { gc = model.FeatureGroups.Count; } catch { }
            for (int i = 1; i <= gc; i++) {
                P.FeatureGroup g = null;
                try { g = model.FeatureGroups.Item(i); } catch { }
                if (g == null) continue;
                int mc = 0; try { mc = g.Count; } catch { }
                for (int k = 1; k <= mc; k++) {
                    try { var m = g.Item(k) as P.Hole; if (m != null) groupOf[Sys(m)] = g; } catch { }
                }
            }

            var candidates = new List<P.Hole>();
            int hc = 0; try { hc = model.Holes.Count; } catch { }
            for (int i = 1; i <= hc; i++) {
                P.Hole h = null;
                try { h = model.Holes.Item(i); } catch { }
                if (h == null || exclude.Contains(Sys(h))) continue;
                if (!SameSpec(h, key, shortName)) continue;
                V3 n, c;
                if (!PlaneOf(h, out n, out c)) continue;
                if (Math.Abs(Math.Abs(n.Dot(n0)) - 1) > 1e-6 || Math.Abs((c - c0).Dot(n0)) > PlaneTol) continue;
                candidates.Add(h);
            }
            if (candidates.Count == 0) return null;

            // 有组的优先：取特征树里最后的那个组（就是上一批）；都没组时只认唯一的单孔
            P.FeatureGroup best = null;
            foreach (var h in candidates) { P.FeatureGroup g; if (groupOf.TryGetValue(Sys(h), out g)) best = g; }
            var batch = new Batch();
            if (best != null) {
                if (!SameSpec(best, key, shortName)) return null;   // 孔在别人的组里（用户自己编的组）：不动
                batch.Group = best;
                try { batch.GroupName = best.Name; } catch { }
                int mc = 0; try { mc = best.Count; } catch { }
                for (int k = 1; k <= mc; k++) {
                    P.Hole m = null;
                    try { m = best.Item(k) as P.Hole; } catch { }
                    if (m == null) return null;                     // 组里有孔以外的东西：用户编的组，不动
                    batch.Holes.Add(m);
                }
                return batch.Holes.Count > 0 ? batch : null;
            }
            if (candidates.Count != 1) return null;                 // 好几个散着的同规格孔：说不清哪批，不动
            batch.Holes.Add(candidates[0]);
            return batch;
        }

        static bool SameSpec(object feature, string key, string shortName){
            string tag = ReadTag(feature);
            if (tag.Length > 0) return tag == key;
            string name = NameOf(feature);
            if (string.IsNullOrEmpty(name)) return false;
            if (name == shortName) return true;
            if (name.StartsWith(shortName + "-", StringComparison.Ordinal)) {
                string rest = name.Substring(shortName.Length + 1);
                int sp = rest.IndexOf(' ');
                if (sp > 0) rest = rest.Substring(0, sp);
                int n; return int.TryParse(rest, out n);
            }
            if (name.StartsWith(shortName + " ×", StringComparison.Ordinal)) return true;
            if (name.StartsWith(shortName + " (", StringComparison.Ordinal)) return true;
            return false;
        }

        // 孔特征轮廓所在平面（零件局部）：法向 + 平面上一点
        static bool PlaneOf(P.Hole h, out V3 n, out V3 c){
            n = new V3(0, 0, 1); c = new V3();
            try {
                var prof = h.Profile as P.Profile;
                if (prof == null) return false;
                var plane = prof.Plane as P.RefPlane;
                if (plane == null) return false;
                Array nv = new double[3], rp = new double[3];
                plane.GetNormal(ref nv); plane.GetRootPoint(ref rp);
                n = V3.From(nv).Unit(); c = V3.From(rp);
                return true;
            } catch { return false; }
        }

        // 特征树里，旧组成员和新孔之间是否只有"我们自己的东西"。判不了（接口读不出顺序）就当可以并 ——
        // 并组失败有恢复兜底；读得出来、而且夹着别的实体特征时才拒绝。
        static bool Contiguous(P.Model model, Batch old, IList<P.Hole> newHoles){
            List<string> order;
            try { order = FeatureOrder(model); } catch { return true; }
            if (order == null || order.Count == 0) return true;
            var ours = new HashSet<string>(StringComparer.Ordinal);
            foreach (var h in old.Holes) ours.Add(Sys(h));
            foreach (var h in newHoles) ours.Add(Sys(h));
            int lo = int.MaxValue, hi = -1, found = 0;
            for (int i = 0; i < order.Count; i++) {
                if (!ours.Contains(order[i])) continue;
                found++; lo = Math.Min(lo, i); hi = Math.Max(hi, i);
            }
            if (found != ours.Count) return true;                  // 特征表里认不全我们的孔：判不了
            var groupNames = new HashSet<string>(StringComparer.Ordinal);
            try {
                int gc = model.FeatureGroups.Count;
                for (int i = 1; i <= gc; i++) { try { groupNames.Add(Sys(model.FeatureGroups.Item(i))); } catch { } }
            } catch { }
            for (int i = lo; i <= hi; i++) {
                if (ours.Contains(order[i]) || groupNames.Contains(order[i])) continue;
                return false;
            }
            return true;
        }

        static List<string> FeatureOrder(P.Model model){
            var list = new List<string>();
            dynamic feats = model.Features;
            int n = feats.Count;
            for (int i = 1; i <= n; i++) {
                string s = "";
                try { s = Sys(feats.Item(i)); } catch { }
                list.Add(s);
            }
            return list;
        }

        // 孔特征 / 特征组都按接口类型直接调（COM 的带参属性用 dynamic 绑不稳）；特征表里其它类型的特征才走 dynamic
        static string Sys(object feature){
            try {
                var h = feature as P.Hole; if (h != null) return h.SystemName ?? "";
                var g = feature as P.FeatureGroup; if (g != null) return g.SystemName ?? "";
                string s = ((dynamic)feature).SystemName; return s ?? "";
            } catch { return ""; }
        }
        static string NameOf(object feature){
            try {
                var h = feature as P.Hole; if (h != null) return h.Name ?? "";
                var g = feature as P.FeatureGroup; if (g != null) return g.Name ?? "";
            } catch { }
            return "";
        }
        static object SetsOf(object feature){
            var h = feature as P.Hole; if (h != null) return h.AttributeSets;
            var g = feature as P.FeatureGroup; if (g != null) return g.AttributeSets;
            return null;
        }
        static bool HasSet(object feature, string set){
            try {
                var h = feature as P.Hole; if (h != null) return h.get_IsAttributeSetPresent(set);
                var g = feature as P.FeatureGroup; if (g != null) return g.get_IsAttributeSetPresent(set);
            } catch { }
            return false;
        }

        static bool TryName(P.Hole hole, string name){
            try { hole.Name = name; return true; }
            catch (Exception e) { Log.Write("HoleGroupingRename", e); return false; }
        }

        // 规格键挂在孔特征 / 特征组的属性集上（随零件文件保存）
        static void Tag(object feature, string key){
            try {
                var sets = SetsOf(feature) as F.AttributeSets;
                if (sets == null) return;
                F.AttributeSet set = null;
                if (HasSet(feature, SpecSet)) { try { set = (F.AttributeSet)sets.Item(SpecSet); } catch { } }
                if (set == null) set = sets.Add(SpecSet);
                F.Attribute at = null;
                try { at = (F.Attribute)set.Item(SpecAttr); } catch { }
                if (at == null) at = set.Add(SpecAttr, F.AttributeTypeConstants.seStringUnicode);
                at.Value = key;
            } catch (Exception e) { Log.Write("HoleGroupingTag", e); }
        }
        static string ReadTag(object feature){
            try {
                if (!HasSet(feature, SpecSet)) return "";
                var set = (F.AttributeSet)((F.AttributeSets)SetsOf(feature)).Item(SpecSet);
                var v = ((F.Attribute)set.Item(SpecAttr)).Value as string;
                return v ?? "";
            } catch { return ""; }
        }
    }
}
