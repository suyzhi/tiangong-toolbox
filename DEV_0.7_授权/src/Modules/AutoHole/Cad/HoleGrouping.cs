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
    //
    // 成员不只是孔特征：批量排孔（0.7.5 起）打的是「种子孔 + 阵列」，阵列特征也是组里的成员，
    // 它代表的孔数 = 阵列实例数 - 种子数（种子孔自己另算 1 个）。阵列同样挂规格键（真机实测阵列能挂属性、能和孔编进一组）。
    // 0.7.9 起同规格的一批孔合成一个多孔孔特征（UserDefinedPattern，见 HoleCombine）：它单独就是特征树里的一项，
    // 叫「规格 ×N」，不再建组；同一张面再打一批（又一个多孔特征）时，两个多孔特征编成一组「规格 ×总数」（真机 J4：能编组）。
    // 多孔特征里的输入孔叫「规格-序号」，和原来组里的单孔一样接着往下编号。
    public static class HoleGrouping {
        public const string SpecSet = "TGAutoHoleSpec", SpecAttr = "Key";
        const double PlaneTol = 1e-6;   // 米

        sealed class Batch {
            public readonly List<object> Members = new List<object>();  // 之前那批（孔 / 阵列，按特征树里的顺序）
            public P.FeatureGroup Group;                               // 它们所在的组（没有组为 null）
            public string GroupName = "";
            public int Count;                                          // 代表的孔数
        }

        // 新打的同规格孔 newHoles（同一张面）。taken：零件里已占用的名字（会就地更新）。
        // 返回特征树里出现的节点名（组名或单孔名），供结果框显示。
        // undo/part 可为 null：传了就把"改了什么"记进去，供「撤销本次打孔」倒着恢复（见 HoleUndo）。
        public static string Apply(P.Model model, HoleSpec spec, IList<P.Hole> newHoles, HashSet<string> taken, List<string> notes){
            return Apply(model, spec, newHoles, taken, notes, null, null);
        }
        public static string Apply(P.Model model, HoleSpec spec, IList<P.Hole> newHoles, HashSet<string> taken, List<string> notes, HoleUndo undo, P.PartDocument part){
            if (model == null || newHoles == null || newHoles.Count == 0) return "";
            var fresh = new List<object>();
            foreach (var h in newHoles) fresh.Add(h);
            return Apply(model, spec, fresh, newHoles.Count, taken, notes, undo, part);
        }
        // 多孔孔特征：一个特征算一批，代表它的输入孔数
        public static string ApplyMulti(P.Model model, HoleSpec spec, P.UserDefinedPattern multi, HashSet<string> taken, List<string> notes, HoleUndo undo, P.PartDocument part){
            if (model == null || multi == null) return "";
            return Apply(model, spec, new List<object>{ multi }, HoleCombine.CountOf(multi), taken, notes, undo, part);
        }
        // 种子孔 + 阵列（批量排孔）：两个特征算一批，代表 count 个孔
        public static string ApplyPattern(P.Model model, HoleSpec spec, P.Hole seed, P.Pattern pattern, int count, HashSet<string> taken, List<string> notes, HoleUndo undo, P.PartDocument part){
            if (model == null || seed == null || pattern == null) return "";
            return Apply(model, spec, new List<object>{ seed, pattern }, count, taken, notes, undo, part);
        }

        static string Apply(P.Model model, HoleSpec spec, List<object> fresh, int freshCount, HashSet<string> taken, List<string> notes, HoleUndo undo, P.PartDocument part){
            string key = HoleNaming.SpecKey(spec);
            foreach (var f in fresh) { Tag(f, key); foreach (var h in InputsOf(f)) Tag(h, key); }
            Batch old = null;
            try { old = FindPrevious(model, spec, key, fresh); } catch (Exception e) { Log.Write("HoleGroupingFind", e); }
            int total = freshCount + (old == null ? 0 : old.Count);

            if (total == 1) {
                string single = HoleNaming.Unique(HoleNaming.Short(spec), taken);
                if (TryName(fresh[0], single)) taken.Add(single);
                return single;
            }

            if (old != null && !Contiguous(model, old, fresh)) {
                notes.Add("「" + HoleNaming.Short(spec) + "」之前那批孔和这次的孔之间夹着别的特征，没有并进同一个组（避免牵动特征顺序），这次的孔单独成组。");
                old = null;
            }

            // 之前那批是单孔（没建组）：先把它改成成员名「…-1」
            if (old != null && old.Group == null && old.Members.Count == 1 && old.Members[0] is P.Hole) {
                var oldHole = (P.Hole)old.Members[0];
                var nm = NameOf(oldHole);
                taken.Remove(nm);
                var n1 = HoleNaming.Members(spec, 1, taken);
                if (n1.Count == 1 && TryName(oldHole, n1[0])) { taken.Add(n1[0]); if (undo != null) undo.Renamed(part, oldHole, nm); }
                else taken.Add(nm);
            }
            NameFresh(spec, fresh, taken);

            if (old != null) {
                string merged = Merge(model, spec, key, old, fresh, freshCount, taken, notes, undo, part);
                if (merged.Length > 0) return merged;
            }
            if (fresh.Count == 1) return NameOf(fresh[0]);   // 并组失败、这次又只有一个孔：不单独成组
            return NewGroup(model, spec, key, fresh, freshCount, taken, notes, undo, part);
        }

        // 只命名、挂规格键，不编组、不并进之前那批（0.8.4：挂了天工原生关联的孔要和孔位链接编进一个组，见 MakeLinkGroup）。
        // 返回特征树里的节点名（单孔名 / 多孔特征名，几个单孔时是第一个的名字）。
        internal static string NameOnly(HoleSpec spec, List<object> fresh, HashSet<string> taken){
            if (fresh == null || fresh.Count == 0) return "";
            string key = HoleNaming.SpecKey(spec);
            foreach (var f in fresh) { Tag(f, key); foreach (var h in InputsOf(f)) Tag(h, key); }
            if (fresh.Count == 1 && fresh[0] is P.Hole) {
                string single = HoleNaming.Unique(HoleNaming.Short(spec), taken);
                if (TryName(fresh[0], single)) taken.Add(single);
                return single;
            }
            NameFresh(spec, fresh, taken);
            return fresh.Count == 1 ? NameOf(fresh[0]) : HoleNaming.Short(spec) + " 等 " + fresh.Count + " 个孔";
        }

        // ---- 孔位链接组（0.8.4）----
        // 用户：「最好将打出来的带链接的孔的链接副本和孔特征放在一个组里，方便删除孔特征的时候连带链接副本一起删掉」。
        // 真机 LinkGroupProbe（2026-10-23）：零件间复制和多孔孔特征能用 AddFeatureGroupsBySet 编进一个组（G1）；
        // 编组后挪参考零件 + 更新链接，孔照样跟（G4）；组.Delete() 把复制、孔一起删光，零件不再带链接（G3）。
        // 组上挂 LinkGroupSet 属性认它：改孔型不把它当"同规格孔的组"（里面可能有几种规格），并组也不往里并。
        public const string LinkGroupSet = "TGAutoHoleLinkGroup";
        internal static P.FeatureGroup MakeLinkGroup(P.Model model, IList<object> members, string name, HashSet<string> taken){
            var g = MakeGroup(model, members);
            if (g.Count != members.Count) { try { g.Ungroup(); } catch { } throw new InvalidOperationException("组里只有 " + g.Count + " 个成员，应为 " + members.Count); }
            string want = HoleNaming.Unique(name, taken);
            if (TryName(g, want)) taken.Add(want);
            TagSet(g, LinkGroupSet, "1");
            return g;
        }
        public static bool IsLinkGroup(object group){ return group is P.FeatureGroup && HasSet(group, LinkGroupSet); }

        // 新成员命名：孔按「规格-序号」接着往下编，阵列叫「规格 阵列」，多孔特征叫「规格 ×N」、它的输入孔也按「规格-序号」编
        static void NameFresh(HoleSpec spec, List<object> fresh, HashSet<string> taken){
            int holes = 0;
            foreach (var f in fresh) if (f is P.Hole) holes++;
            var names = HoleNaming.Members(spec, holes, taken);
            int k = 0;
            foreach (var f in fresh) {
                if (f is P.UserDefinedPattern) { NameMulti(spec, (P.UserDefinedPattern)f, taken); continue; }
                string want = f is P.Hole ? names[k++] : HoleNaming.Unique(HoleNaming.PatternName(spec), taken);
                if (TryName(f, want)) taken.Add(want);
            }
        }
        // 多孔特征 + 它的输入孔起名（「改孔型」换了规格也走这里）。名字先从 taken 里拿掉，免得和自己撞名多出「(2)」
        internal static void NameMulti(HoleSpec spec, P.UserDefinedPattern multi, HashSet<string> taken){
            var inputs = HoleCombine.Inputs(multi);
            taken.Remove(NameOf(multi));
            foreach (var h in inputs) taken.Remove(NameOf(h));
            string want = HoleNaming.Unique(HoleNaming.Group(spec, inputs.Count > 0 ? inputs.Count : HoleCombine.CountOf(multi)), taken);
            if (TryName(multi, want)) taken.Add(want);
            var names = HoleNaming.Members(spec, inputs.Count, taken);
            for (int i = 0; i < inputs.Count && i < names.Count; i++) if (TryName(inputs[i], names[i])) taken.Add(names[i]);
        }
        // 多孔特征的输入孔（别的特征返回空）
        internal static List<P.Hole> InputsOf(object feature){
            var u = feature as P.UserDefinedPattern;
            return u == null ? new List<P.Hole>() : HoleCombine.Inputs(u);
        }
        // 一个成员代表几个孔：孔 1、阵列 = 实例数 - 种子、多孔特征 = 输入孔数
        internal static int HolesIn(object member){
            if (member is P.Hole) return 1;
            var p = member as P.Pattern; if (p != null) return PatternExtra(p);
            var u = member as P.UserDefinedPattern; if (u != null) return HoleCombine.CountOf(u);
            return 0;
        }

        static string NewGroup(P.Model model, HoleSpec spec, string key, IList<object> members, int count, HashSet<string> taken, List<string> notes, HoleUndo undo, P.PartDocument part){
            string groupName = HoleNaming.Unique(HoleNaming.Group(spec, count), taken);
            try {
                var g = MakeGroup(model, members);
                if (undo != null) undo.Group(part, g);
                try { g.Name = groupName; } catch (Exception e) { Log.Write("HoleGroupingName", e); }
                Tag(g, key);
                taken.Add(groupName);
                return groupName;
            } catch (Exception e) {
                Log.Write("HoleGroupingNew", e);
                notes.Add(count + " 个「" + HoleNaming.Short(spec) + "」没能编成一组（" + AutoHoleWriter.Friendly(e) + "），已逐个命名。");
                return HoleNaming.Short(spec) + " 等 " + count + " 个孔";
            }
        }

        // 旧组散掉、旧成员 + 新孔重编；失败就把旧组原样恢复，返回 ""（调用方会让新孔单独成组）。
        static string Merge(P.Model model, HoleSpec spec, string key, Batch old, IList<object> fresh, int freshCount, HashSet<string> taken, List<string> notes, HoleUndo undo, P.PartDocument part){
            var all = new List<object>(old.Members);
            all.AddRange(fresh);
            if (old.Group != null) {
                try { old.Group.Ungroup(); }
                catch (Exception e) { Log.Write("HoleGroupingUngroup", e); return ""; }   // 散不了：旧组没动，新孔单独成组
                taken.Remove(old.GroupName);
            }
            string groupName = HoleNaming.Unique(HoleNaming.Group(spec, old.Count + freshCount), taken);
            try {
                var g = MakeGroup(model, all);
                if (g.Count != all.Count) { try { g.Ungroup(); } catch { } throw new InvalidOperationException("重编后的组只有 " + g.Count + " 个成员"); }
                if (undo != null) {
                    undo.Group(part, g);
                    if (old.Group != null) undo.GroupDissolved(part, old.GroupName, key, old.Members);
                }
                try { g.Name = groupName; } catch (Exception e) { Log.Write("HoleGroupingName", e); }
                Tag(g, key);
                taken.Add(groupName);
                return groupName;
            } catch (Exception e) {
                Log.Write("HoleGroupingMerge", e);
                if (old.Group != null) {
                    // 恢复旧组：原成员、原名字
                    try {
                        var back = MakeGroup(model, old.Members);
                        try { back.Name = old.GroupName; } catch { }
                        Tag(back, key);
                        taken.Add(old.GroupName);
                    } catch (Exception e2) {
                        Log.Write("HoleGroupingRestore", e2);
                        if (undo != null) undo.GroupDissolved(part, old.GroupName, key, old.Members);   // 撤销时再试一次
                        notes.Add("之前的特征组「" + old.GroupName + "」散开后没能恢复，组里的孔都还在，只是不再成组。");
                    }
                }
                notes.Add("这次的「" + HoleNaming.Short(spec) + "」没能并进之前的组（" + AutoHoleWriter.Friendly(e) + "），已单独成组。");
                return "";
            }
        }

        // 编组。真机实测（tools/hole-feature-probe/HoleMultiProbe.cs，2026-10-14）：
        //   · 特征组只收特征树里连续的一段 —— 两种规格的孔交错（6、9、6、9）时 BySet 报 E_INVALIDARG，
        //     就是 10-13 现场的那个错误。根治在 AutoHoleWriter.DrillRequests：按规格分批打，同规格的孔天然挨着。
        //   · 孔之间夹着没用掉的轮廓、夹着基准面，都**不影响**编组（G1、G2）。
        //   · 用 Reorder 把孔挪到一起虽然能编上，但挪完孔的状态变成"警告"甚至"失败"（G3/P3）—— 不用。
        // 这里只剩一道兜底：BySet 不行就试 ByRange(首, 尾)，范围里混进了别的特征（成员数对不上）就不要。
        // 失败时把这批孔在特征树里的位置和夹在中间的特征写进日志。
        static P.FeatureGroup MakeGroup(P.Model model, IList<object> members){
            Exception first;
            try { return BySet(model, members); } catch (Exception e) { first = e; }
            Log.Write("HoleGroupingBySet", "失败 0x" + first.HResult.ToString("X8") + " " + first.Message + " ｜ " + Describe(model, members));
            try {
                var g = model.FeatureGroups.AddFeatureGroupsByRange(members[0], members[members.Count - 1]);
                if (g != null && g.Count == members.Count) return g;
                if (g != null) { try { g.Ungroup(); } catch { } }   // 范围里混进了别的特征：不要这个组
                Log.Write("HoleGroupingByRange", "范围编组成员数 " + (g == null ? -1 : g.Count) + "，要求 " + members.Count);
            } catch (Exception e) { Log.Write("HoleGroupingByRange", e); }
            throw first;
        }

        // 撤销用：按原成员、原名字把旧组编回去，并挂上规格键
        internal static void Regroup(P.Model model, IList<object> members, string name, string key){
            var g = MakeGroup(model, members);
            try { if (!string.IsNullOrEmpty(name)) g.Name = name; } catch (Exception e) { Log.Write("HoleGroupingName", e); }
            if (!string.IsNullOrEmpty(key)) Tag(g, key);
        }

        static P.FeatureGroup BySet(P.Model model, IList<object> members){
            var items = new object[members.Count];
            for (int i = 0; i < members.Count; i++) items[i] = members[i];
            Array arr = items;
            var g = model.FeatureGroups.AddFeatureGroupsBySet(items.Length, ref arr);
            if (g == null) throw new InvalidOperationException("CAD 未返回特征组");
            return g;
        }

        // 诊断：建模方式 + 这批孔在特征表里的下标 + 夹在中间的不是这批孔的特征（最多列 8 个）
        static string Describe(P.Model model, IList<object> members){
            var sb = new System.Text.StringBuilder();
            try { var h0 = members[0] as P.Hole; if (h0 != null) sb.Append("模式=").Append(h0.ModelingModeType).Append(' '); } catch { }
            try {
                var order = FeatureOrder(model);
                var mine = new HashSet<string>(StringComparer.Ordinal);
                foreach (var h in members) mine.Add(Sys(h));
                int lo = int.MaxValue, hi = -1; var at = new List<int>();
                for (int i = 0; i < order.Count; i++) if (mine.Contains(order[i])) { at.Add(i + 1); lo = Math.Min(lo, i); hi = Math.Max(hi, i); }
                sb.Append("特征数=").Append(order.Count).Append(" 孔位于[").Append(string.Join(",", at.ConvertAll(x => x.ToString()).ToArray())).Append("] 要求").Append(members.Count).Append("个");
                if (at.Count < members.Count) sb.Append("（有孔不在特征表里）");
                int shown = 0;
                for (int i = lo; i <= hi && i >= 0 && i < order.Count; i++) {
                    if (mine.Contains(order[i])) continue;
                    if (shown++ == 8) { sb.Append(" …"); break; }
                    sb.Append(" 夹着#").Append(i + 1).Append("=").Append(order[i]);
                }
            } catch (Exception e) { sb.Append("读特征表失败:").Append(e.Message); }
            return sb.ToString();
        }

        // ---- 找之前那批：同规格（属性或名字）+ 同一张面（轮廓平面）----
        static Batch FindPrevious(P.Model model, HoleSpec spec, string key, IList<object> fresh){
            var exclude = new HashSet<string>(StringComparer.Ordinal);
            foreach (var f in fresh) exclude.Add(Sys(f));
            foreach (var f in fresh) foreach (var h in InputsOf(f)) exclude.Add(Sys(h));
            var firstHole = fresh[0] as P.Hole ?? (fresh[0] is P.UserDefinedPattern ? HoleCombine.SeedOf((P.UserDefinedPattern)fresh[0]) : null);
            V3 n0, c0;
            if (firstHole == null || !PlaneOf(firstHole, out n0, out c0)) return null;
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
                    try { object m = g.Item(k); if (m is P.Hole || m is P.UserDefinedPattern) groupOf[Sys(m)] = g; } catch { }
                }
            }

            // 候选：单孔 + 多孔特征（多孔特征按它的种子孔认面）
            var candidates = new List<object>();
            var all = new List<object>();
            int hc = 0; try { hc = model.Holes.Count; } catch { }
            for (int i = 1; i <= hc; i++) { try { all.Add(model.Holes.Item(i)); } catch { } }
            int uc = 0; try { uc = model.UserDefinedPatterns.Count; } catch { }
            for (int i = 1; i <= uc; i++) { try { all.Add(model.UserDefinedPatterns.Item(i)); } catch { } }
            foreach (var f in all) {
                if (f == null || exclude.Contains(Sys(f))) continue;
                if (!SameSpec(f, key, shortName)) continue;
                var h = f as P.Hole ?? HoleCombine.SeedOf((P.UserDefinedPattern)f);
                V3 n, c;
                if (h == null || !PlaneOf(h, out n, out c)) continue;
                if (Math.Abs(Math.Abs(n.Dot(n0)) - 1) > 1e-6 || Math.Abs((c - c0).Dot(n0)) > PlaneTol) continue;
                candidates.Add(f);
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
                    object m = null;
                    try { m = best.Item(k); } catch { }
                    // 组里只能是孔、或挂着同一规格键的阵列；别的东西 = 用户编的组，不动
                    if (m is P.Hole) { batch.Members.Add(m); batch.Count++; continue; }
                    if (m is P.UserDefinedPattern && SameSpec(m, key, shortName)) { batch.Members.Add(m); batch.Count += HolesIn(m); continue; }
                    var pat = m as P.Pattern;
                    if (pat != null && SameSpec(pat, key, shortName)) { batch.Members.Add(pat); batch.Count += PatternExtra(pat); continue; }
                    return null;
                }
                return batch.Members.Count > 0 ? batch : null;
            }
            if (candidates.Count != 1) return null;                 // 好几个散着的同规格孔：说不清哪批，不动
            batch.Members.Add(candidates[0]);
            batch.Count = HolesIn(candidates[0]);
            return batch;
        }

        // 阵列代表的孔数（不含种子孔，种子孔是组里单独的成员）
        internal static int PatternExtra(P.Pattern pattern){
            try { return Math.Max(0, pattern.NumberOfOccurrences - Math.Max(1, pattern.NumberOfInputFeatures)); } catch { return 0; }
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
        internal static bool PlaneOf(P.Hole h, out V3 n, out V3 c){
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
        static bool Contiguous(P.Model model, Batch old, IList<object> fresh){
            List<string> order;
            try { order = FeatureOrder(model); } catch { return true; }
            if (order == null || order.Count == 0) return true;
            var ours = new HashSet<string>(StringComparer.Ordinal);
            foreach (var h in old.Members) ours.Add(Sys(h));
            foreach (var h in fresh) ours.Add(Sys(h));
            var top = new HashSet<string>(ours, StringComparer.Ordinal);
            // 多孔特征的输入孔：特征表里有没有它们说不准，有的话也算"我们自己的"
            foreach (var h in old.Members) foreach (var x in InputsOf(h)) ours.Add(Sys(x));
            foreach (var h in fresh) foreach (var x in InputsOf(h)) ours.Add(Sys(x));
            int lo = int.MaxValue, hi = -1, found = 0;
            for (int i = 0; i < order.Count; i++) {
                if (!ours.Contains(order[i])) continue;
                if (top.Contains(order[i])) found++;
                lo = Math.Min(lo, i); hi = Math.Max(hi, i);
            }
            if (found != top.Count) return true;                   // 特征表里认不全我们的孔：判不了
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

        // 孔特征 / 阵列 / 特征组都按接口类型直接调（COM 的带参属性用 dynamic 绑不稳）；特征表里其它类型的特征才走 dynamic
        internal static string Sys(object feature){
            try {
                var h = feature as P.Hole; if (h != null) return h.SystemName ?? "";
                var p = feature as P.Pattern; if (p != null) return p.SystemName ?? "";
                var g = feature as P.FeatureGroup; if (g != null) return g.SystemName ?? "";
                var u = feature as P.UserDefinedPattern; if (u != null) return u.SystemName ?? "";
                string s = ((dynamic)feature).SystemName; return s ?? "";
            } catch { return ""; }
        }
        internal static string NameOf(object feature){
            try {
                var h = feature as P.Hole; if (h != null) return h.Name ?? "";
                var p = feature as P.Pattern; if (p != null) return p.Name ?? "";
                var g = feature as P.FeatureGroup; if (g != null) return g.Name ?? "";
                var u = feature as P.UserDefinedPattern; if (u != null) return u.Name ?? "";
            } catch { }
            return "";
        }
        static object SetsOf(object feature){
            var h = feature as P.Hole; if (h != null) return h.AttributeSets;
            var p = feature as P.Pattern; if (p != null) return p.AttributeSets;
            var g = feature as P.FeatureGroup; if (g != null) return g.AttributeSets;
            var u = feature as P.UserDefinedPattern; if (u != null) return u.AttributeSets;
            return null;
        }
        static bool HasSet(object feature, string set){
            try {
                var h = feature as P.Hole; if (h != null) return h.get_IsAttributeSetPresent(set);
                var p = feature as P.Pattern; if (p != null) return p.get_IsAttributeSetPresent(set);
                var g = feature as P.FeatureGroup; if (g != null) return g.get_IsAttributeSetPresent(set);
                var u = feature as P.UserDefinedPattern; if (u != null) return u.get_IsAttributeSetPresent(set);
            } catch { }
            return false;
        }

        internal static bool TryName(object feature, string name){
            try {
                var h = feature as P.Hole; if (h != null) { h.Name = name; return true; }
                var p = feature as P.Pattern; if (p != null) { p.Name = name; return true; }
                var g = feature as P.FeatureGroup; if (g != null) { g.Name = name; return true; }
                var u = feature as P.UserDefinedPattern; if (u != null) { u.Name = name; return true; }
                return false;
            } catch (Exception e) { Log.Write("HoleGroupingRename", e); return false; }
        }

        // 规格键挂在孔特征 / 阵列 / 特征组的属性集上（随零件文件保存）
        internal static void Tag(object feature, string key){ TagSet(feature, SpecSet, key); }
        static void TagSet(object feature, string setName, string key){
            try {
                var sets = SetsOf(feature) as F.AttributeSets;
                if (sets == null) return;
                F.AttributeSet set = null;
                if (HasSet(feature, setName)) { try { set = (F.AttributeSet)sets.Item(setName); } catch { } }
                if (set == null) set = sets.Add(setName);
                F.Attribute at = null;
                try { at = (F.Attribute)set.Item(SpecAttr); } catch { }
                if (at == null) at = set.Add(SpecAttr, F.AttributeTypeConstants.seStringUnicode);
                at.Value = key;
            } catch (Exception e) { Log.Write("HoleGroupingTag", e); }
        }
        internal static string ReadTag(object feature){
            try {
                if (!HasSet(feature, SpecSet)) return "";
                var set = (F.AttributeSet)((F.AttributeSets)SetsOf(feature)).Item(SpecSet);
                var v = ((F.Attribute)set.Item(SpecAttr)).Value as string;
                return v ?? "";
            } catch { return ""; }
        }
    }
}
