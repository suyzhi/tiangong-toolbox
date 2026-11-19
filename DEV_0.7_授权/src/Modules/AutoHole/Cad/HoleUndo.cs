using System;
using System.Collections.Generic;
using P=SolidEdgePart;

namespace TianGongCadSuite {
    // 「撤销本次打孔」：记下一批打孔在每个零件里改了什么，需要时倒着恢复。
    //
    // 为什么不靠 CAD 的 Ctrl+Z：一批 30 个孔 = 30 个孔特征 + 编组 + 改名 + 写关联属性，
    // 要按几十下才退得回去，中间还容易多退一步把用户自己的操作也撤了。
    //
    // 记什么（全部按特征的 SystemName 记 —— 名字会被我们改，SystemName 不变）：
    //   · 新建的孔特征、腰孔（拉伸切除）、阵列（批量排孔的「种子孔 + 阵列」）、多孔孔特征（0.7.9，UserDefinedPattern）；
    //   · 新建的特征组；
    //   · 并组时散掉的旧组：旧组名 + 旧成员（撤销时按原名、原成员编回去）；
    //   · 被改了名的旧孔（之前那批只有一个孔时会被改成「…-1」）。
    // 撤销顺序：散掉新组 → 删多孔特征（连它的输入孔一起删）→ 删阵列（它依赖种子孔，先删）→ 删新孔/腰孔 → 旧孔改回原名 → 旧组按原样编回去。
    // 找不到的特征（用户已经 Ctrl+Z 或手动删掉了）跳过并如实报告，不算失败。
    // 关掉窗口时还没撤销的最后一批（0.8.5）：同一个装配里下次打开同一个窗口接着能「撤销本次打孔」。
    // 原来窗口一关撤销记录就没了，发现打错只能去特征树里一个个删
    public sealed class CarriedUndo {
        public HoleUndo Undo; public List<KeyValuePair<P.PartDocument, string>> Created; string assembly = "";
        static string Key(object asm){ try { return Convert.ToString(((dynamic)asm).FullName) ?? ""; } catch { return ""; } }
        public void Keep(object asm, HoleUndo undo, List<KeyValuePair<P.PartDocument, string>> created){
            if (undo == null || undo.IsEmpty) return;
            Undo = undo; Created = created; assembly = Key(asm);
        }
        // 取走（只给同一个装配；取走以后这里清空，归新窗口管）
        public bool Take(object asm, out HoleUndo undo, out List<KeyValuePair<P.PartDocument, string>> created){
            undo = null; created = null;
            if (Undo == null || assembly.Length == 0 || !string.Equals(assembly, Key(asm), StringComparison.OrdinalIgnoreCase)) return false;
            undo = Undo; created = Created; Undo = null; Created = null; assembly = "";
            return !undo.IsEmpty;
        }
    }

    public sealed class HoleUndo {
        sealed class GroupRestore {
            public string Name = "", Key = "";
            public readonly List<string> Members = new List<string>();
        }
        sealed class Entry {
            public P.PartDocument Part;
            public string PartName = "";
            public readonly List<string> Holes = new List<string>();
            public readonly List<string> Cutouts = new List<string>();
            public readonly List<string> Copies = new List<string>();   // 天工原生关联建的零件间复制（孔删了以后再删它）
            public readonly List<string> Patterns = new List<string>();
            public int PatternHoles;                                    // 阵列代表的孔数（不含种子孔）
            public readonly List<string> Multis = new List<string>();   // 多孔特征（删它 = 删它的全部输入孔）
            public int MultiHoles;
            public readonly List<string> Groups = new List<string>();
            public readonly List<GroupRestore> Restores = new List<GroupRestore>();
            public readonly List<KeyValuePair<string, string>> Renames = new List<KeyValuePair<string, string>>();   // SystemName → 原名
        }

        readonly List<Entry> entries = new List<Entry>();
        // 新建孔特征的 COM 引用（零件 + SystemName → 孔）。孔被「改孔型」换过 HoleData 以后 SystemName 会变（真机实测），
        // 按旧 SystemName 找不到时用引用兜底，免得"改过孔型再撤销本次打孔"删不掉。
        // 键必须带零件：不同零件里的特征 SystemName 是一样的（都从「… 孔 1」编起）。0.7.10 以前只按 SystemName 存，
        // 一批打到几个零件上时后一个零件把前一个的引用覆盖掉，撤销时第一个零件的孔删不掉（DrillSpeedE2E bars4 实测）。
        readonly Dictionary<string, P.Hole> refs = new Dictionary<string, P.Hole>(StringComparer.Ordinal);
        // 多孔特征同理：SystemName 跟着孔型走（「3-6 简单孔 1」），改过孔型就按引用找
        readonly Dictionary<string, P.UserDefinedPattern> multiRefs = new Dictionary<string, P.UserDefinedPattern>(StringComparer.Ordinal);

        public int HoleCount { get { int n = 0; foreach (var e in entries) n += e.Holes.Count + e.Cutouts.Count + e.PatternHoles + e.MultiHoles; return n; } }
        public bool IsEmpty { get { return HoleCount == 0; } }

        // 同一个零件的记录放一起（同一批里可能点了同一个零件上的几张面）
        Entry For(P.PartDocument part){
            foreach (var e in entries) if (ReferenceEquals(e.Part, part) || (Key(e.Part) == Key(part) && Key(part).Length > 0)) return e;
            var n = new Entry { Part = part };
            try { n.PartName = part.Name; } catch { }
            entries.Add(n);
            return n;
        }
        static string Key(P.PartDocument part){
            if (part == null) return "";
            try { string f = part.FullName; if (!string.IsNullOrEmpty(f)) return f; } catch { }
            try { return part.Name ?? ""; } catch { return ""; }
        }

        public void Hole(P.PartDocument part, P.Hole hole){ string s = Sys(hole); if (s.Length > 0) { For(part).Holes.Add(s); refs[RefKey(part, s)] = hole; } }
        public void Copy(P.PartDocument part, P.InterpartConstruction copy){ string s = SysOf(copy); if (s.Length > 0) For(part).Copies.Add(s); }
        public void Cutout(P.PartDocument part, P.ExtrudedCutout cut){ string s = SysOf(cut); if (s.Length > 0) For(part).Cutouts.Add(s); }
        public void Group(P.PartDocument part, P.FeatureGroup group){ string s = SysOf(group); if (s.Length > 0) For(part).Groups.Add(s); }
        public void Pattern(P.PartDocument part, P.Pattern pattern, int extraHoles){
            string s = HoleGrouping.Sys(pattern);
            if (s.Length == 0) return;
            var e = For(part); e.Patterns.Add(s); e.PatternHoles += Math.Max(0, extraHoles);
        }
        // 多孔合一以后：这批单孔不再单独记 —— 合成了记多孔特征；退回单孔记重打出来的孔（种子孔没动的照旧）
        public void Combined(P.PartDocument part, IList<string> oldSys, P.UserDefinedPattern multi, IList<P.Hole> now){
            var e = For(part);
            foreach (var s in oldSys) { e.Holes.Remove(s); refs.Remove(RefKey(part, s)); }
            if (multi != null) {
                string s = HoleGrouping.Sys(multi);
                if (s.Length == 0) return;
                e.Multis.Add(s); e.MultiHoles += now.Count; multiRefs[RefKey(part, s)] = multi;
                return;
            }
            foreach (var h in now) if (h != null) Hole(part, h);
        }
        public void Renamed(P.PartDocument part, P.Hole hole, string oldName){
            string s = Sys(hole);
            if (s.Length == 0 || oldName == null) return;
            var e = For(part);
            foreach (var kv in e.Renames) if (kv.Key == s) return;   // 只记第一次的原名
            e.Renames.Add(new KeyValuePair<string, string>(s, oldName));
        }
        public void GroupDissolved(P.PartDocument part, string oldName, string key, IList<object> members){
            var r = new GroupRestore { Name = oldName ?? "", Key = key ?? "" };
            foreach (var m in members) { string s = HoleGrouping.Sys(m); if (s.Length > 0) r.Members.Add(s); }
            For(part).Restores.Add(r);
        }

        // 这一批新建的孔特征和阵列（零件 + SystemName）：「改孔型」把刚打的这批孔直接带过去
        public List<KeyValuePair<P.PartDocument, string>> Created(){
            var list = new List<KeyValuePair<P.PartDocument, string>>();
            foreach (var e in entries) {
                foreach (var s in e.Holes) list.Add(new KeyValuePair<P.PartDocument, string>(e.Part, Current(e.Part, s)));
                foreach (var s in e.Patterns) list.Add(new KeyValuePair<P.PartDocument, string>(e.Part, s));
                foreach (var s in e.Multis) list.Add(new KeyValuePair<P.PartDocument, string>(e.Part, CurrentMulti(e.Part, s)));
            }
            return list;
        }

        // 把另一份记录并进来（一批里每张面各有一份）
        public void Absorb(HoleUndo other){
            if (other == null || ReferenceEquals(other, this)) return;
            foreach (var kv in other.refs) refs[kv.Key] = kv.Value;
            foreach (var kv in other.multiRefs) multiRefs[kv.Key] = kv.Value;
            foreach (var o in other.entries) {
                var e = For(o.Part);
                e.Holes.AddRange(o.Holes); e.Cutouts.AddRange(o.Cutouts); e.Groups.AddRange(o.Groups); e.Copies.AddRange(o.Copies);
                e.Patterns.AddRange(o.Patterns); e.PatternHoles += o.PatternHoles;
                e.Multis.AddRange(o.Multis); e.MultiHoles += o.MultiHoles;
                e.Restores.AddRange(o.Restores); e.Renames.AddRange(o.Renames);
            }
        }

        public sealed class Outcome {
            public int Removed, Missing, Failed;
            public readonly List<string> Notes = new List<string>();
            public readonly List<P.PartDocument> Parts = new List<P.PartDocument>();
            public string Summary {
                get {
                    string s = "已撤销 " + Removed + " 个孔";
                    if (Missing > 0) s += "，" + Missing + " 个已经不在模型里（可能已手动删除或用 Ctrl+Z 撤过）";
                    if (Failed > 0) s += "，" + Failed + " 个没能删除";
                    return s + "。";
                }
            }
        }

        public Outcome Run(){
            var o = new Outcome();
            foreach (var e in entries) {
                P.Model model = null;
                try { if (e.Part.Models.Count >= 1) model = e.Part.Models.Item(1); } catch (Exception ex) { o.Notes.Add(e.PartName + "：读不到模型（" + ex.Message + "）"); }
                if (model == null) { o.Failed += e.Holes.Count + e.Cutouts.Count + e.PatternHoles + e.MultiHoles; continue; }
                o.Parts.Add(e.Part);
                // ① 散掉这批新建的组（成员留着，下一步删）
                foreach (string s in e.Groups) {
                    var g = InAny(e.Part, model, m => FindGroup(m, s));
                    if (g == null) continue;
                    try { g.Ungroup(); } catch (Exception ex) { Log.Write("HoleUndoUngroup", ex); }
                }
                // ①b 删多孔特征（输入孔跟着一起删）
                int perMulti = e.Multis.Count == 0 ? 0 : e.MultiHoles / e.Multis.Count;
                for (int i = e.Multis.Count - 1; i >= 0; i--) {
                    var u = MultiRef(e.Part, e.Multis[i]) ?? InAny(e.Part, model, m => FindMulti(m, e.Multis[i]));
                    if (u == null) { o.Missing += perMulti; continue; }
                    int count = HoleCombine.CountOf(u);
                    try { u.Delete(); o.Removed += count; }
                    catch (Exception ex) { o.Failed += count; Log.Write("HoleUndoDeleteMulti", ex); o.Notes.Add(e.PartName + "：有一个多孔孔特征没能删除（" + AutoHoleWriter.Friendly(ex) + "）"); }
                }
                // ② 删阵列（阵列挂在种子孔上，先删它；它代表的孔数算进"已撤销"）
                int perPattern = e.Patterns.Count == 0 ? 0 : e.PatternHoles / e.Patterns.Count;
                for (int i = e.Patterns.Count - 1; i >= 0; i--) {
                    var pat = InAny(e.Part, model, m => FindPattern(m, e.Patterns[i]));
                    if (pat == null) { o.Missing += perPattern; continue; }
                    int extra = HoleGrouping.PatternExtra(pat);
                    try { pat.Delete(); o.Removed += extra; }
                    catch (Exception ex) { o.Failed += extra; Log.Write("HoleUndoDeletePattern", ex); o.Notes.Add(e.PartName + "：有一个阵列没能删除（" + AutoHoleWriter.Friendly(ex) + "）"); }
                }
                // ③ 删新孔、新腰孔（倒着删：后建的先删）
                for (int i = e.Holes.Count - 1; i >= 0; i--) {
                    var h = Ref(e.Part, e.Holes[i]) ?? InAny(e.Part, model, m => FindHole(m, e.Holes[i]));
                    if (h == null) { o.Missing++; continue; }
                    try { h.Delete(); o.Removed++; }
                    catch (Exception ex) { o.Failed++; Log.Write("HoleUndoDelete", ex); o.Notes.Add(e.PartName + "：有一个孔没能删除（" + AutoHoleWriter.Friendly(ex) + "）"); }
                }
                for (int i = e.Cutouts.Count - 1; i >= 0; i--) {
                    var c = FindCutout(e.Part, model, e.Cutouts[i]);
                    if (c == null) { o.Missing++; continue; }
                    try { c.Delete(); o.Removed++; }
                    catch (Exception ex) { o.Failed++; Log.Write("HoleUndoDeleteSlot", ex); o.Notes.Add(e.PartName + "：有一个腰孔没能删除（" + ex.Message + "）"); }
                }
                // ③b 这批孔挂关联用的零件间复制（孔已经删了，没有谁再依赖它）
                for (int i = e.Copies.Count - 1; i >= 0; i--) {
                    var c = FindCopy(e.Part, e.Copies[i]);
                    if (c == null) continue;
                    try { c.Delete(); }
                    catch (Exception ex) { Log.Write("HoleUndoDeleteCopy", ex); o.Notes.Add(e.PartName + "：孔位链接（零件间复制）没能删除，可以在特征树里手动删掉。"); }
                }
                // ④ 旧孔改回原名
                foreach (var kv in e.Renames) {
                    var h = InAny(e.Part, model, m => FindHole(m, kv.Key));
                    if (h == null) continue;
                    try { h.Name = kv.Value; } catch (Exception ex) { Log.Write("HoleUndoRename", ex); }
                }
                // ⑤ 并组时散掉的旧组：按原成员、原名字编回去
                foreach (var r in e.Restores) {
                    var members = new List<object>();
                    foreach (string s in r.Members) { object m = (object)InAny(e.Part, model, x => FindHole(x, s)) ?? (object)InAny(e.Part, model, x => FindPattern(x, s)) ?? InAny(e.Part, model, x => FindMulti(x, s)); if (m != null) members.Add(m); }
                    if (members.Count < 2) continue;
                    try { HoleGrouping.Regroup(model, members, r.Name, r.Key); }
                    catch (Exception ex) {
                        Log.Write("HoleUndoRegroup", ex);
                        o.Notes.Add(e.PartName + "：之前的特征组「" + r.Name + "」没能恢复成组（孔都在，只是不再成组）。");
                    }
                }
            }
            entries.Clear(); refs.Clear(); multiRefs.Clear();
            return o;
        }

        // 先在第 1 个实体里找，多实体零件找不到再看别的实体：原来只看第 1 个，实体 2 以后的孔（0.7.13 起按目标面所在实体建）
        // 一旦 COM 引用失效（进出过原位编辑就会），撤销时报"已经不在模型里"、孔留着删不掉
        static T InAny<T>(P.PartDocument part, P.Model first, Func<P.Model, T> find) where T : class {
            var hit = find(first);
            if (hit != null || !PartBodies.Multi(part)) return hit;
            foreach (var m in PartBodies.All(part)) { hit = find(m); if (hit != null) return hit; }
            return null;
        }
        static P.Hole FindHole(P.Model model, string sys){
            int n = 0; try { n = model.Holes.Count; } catch { }
            for (int i = 1; i <= n; i++) { try { var h = model.Holes.Item(i); if (Sys(h) == sys) return h; } catch { } }
            return null;
        }
        static string RefKey(P.PartDocument part, string sys){ return Key(part) + "|" + sys; }
        P.Hole Ref(P.PartDocument part, string sys){ P.Hole h; return refs.TryGetValue(RefKey(part, sys), out h) && HoleEditUndo.Alive(h) ? h : null; }
        // 记下时的 SystemName → 现在的（孔被改过孔型就变了）
        string Current(P.PartDocument part, string sys){ var h = Ref(part, sys); return h == null ? sys : Sys(h); }
        static P.Pattern FindPattern(P.Model model, string sys){
            int n = 0; try { n = model.Patterns.Count; } catch { }
            for (int i = 1; i <= n; i++) { try { var p = model.Patterns.Item(i); if (HoleGrouping.Sys(p) == sys) return p; } catch { } }
            return null;
        }
        internal static P.UserDefinedPattern FindMulti(P.Model model, string sys){
            int n = 0; try { n = model.UserDefinedPatterns.Count; } catch { }
            for (int i = 1; i <= n; i++) { try { var u = model.UserDefinedPatterns.Item(i); if (HoleGrouping.Sys(u) == sys) return u; } catch { } }
            return null;
        }
        P.UserDefinedPattern MultiRef(P.PartDocument part, string sys){ P.UserDefinedPattern u; return multiRefs.TryGetValue(RefKey(part, sys), out u) && HoleGrouping.Sys(u).Length > 0 ? u : null; }
        string CurrentMulti(P.PartDocument part, string sys){ var u = MultiRef(part, sys); return u == null ? sys : HoleGrouping.Sys(u); }
        static P.FeatureGroup FindGroup(P.Model model, string sys){
            int n = 0; try { n = model.FeatureGroups.Count; } catch { }
            for (int i = 1; i <= n; i++) { try { var g = model.FeatureGroups.Item(i); if (SysOf(g) == sys) return g; } catch { } }
            return null;
        }
        // 腰孔（拉伸切除）切在哪个实体上就记在哪个 Model 下（多实体零件，0.7.13 起腰孔用目标实体的 Model 切）：
        // 原来只在第 1 个实体里找，第 2 个以后实体上的腰孔撤销时报"已经不在模型里"、删不掉。先找第 1 个，找不到再看别的实体
        static P.ExtrudedCutout FindCutout(P.PartDocument part, P.Model first, string sys){
            var c = FindCutout(first, sys);
            if (c != null || !PartBodies.Multi(part)) return c;
            foreach (var m in PartBodies.All(part)) { c = FindCutout(m, sys); if (c != null) return c; }
            return null;
        }
        static P.ExtrudedCutout FindCutout(P.Model model, string sys){
            int n = 0; try { n = model.ExtrudedCutouts.Count; } catch { }
            for (int i = 1; i <= n; i++) { try { var c = model.ExtrudedCutouts.Item(i); if (SysOf(c) == sys) return c; } catch { } }
            return null;
        }
        static P.InterpartConstruction FindCopy(P.PartDocument part, string sys){
            P.InterpartConstructions all = null;
            try { all = part.Constructions.InterpartConstructions; } catch { return null; }
            int n = 0; try { n = all.Count; } catch { }
            for (int i = 1; i <= n; i++) { try { var c = (P.InterpartConstruction)all.Item(i); if (SysOf(c) == sys) return c; } catch { } }
            return null;
        }
        static string SysOf(P.InterpartConstruction c){ try { return c == null ? "" : (c.SystemName ?? ""); } catch { return ""; } }
        static string Sys(P.Hole h){ try { return h == null ? "" : (h.SystemName ?? ""); } catch { return ""; } }
        static string SysOf(P.FeatureGroup g){ try { return g == null ? "" : (g.SystemName ?? ""); } catch { return ""; } }
        static string SysOf(P.ExtrudedCutout c){ try { return c == null ? "" : (c.SystemName ?? ""); } catch { return ""; } }
    }
}
