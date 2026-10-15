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
    //   · 新建的孔特征、腰孔（拉伸切除）、阵列（批量排孔的「种子孔 + 阵列」）；
    //   · 新建的特征组；
    //   · 并组时散掉的旧组：旧组名 + 旧成员（撤销时按原名、原成员编回去）；
    //   · 被改了名的旧孔（之前那批只有一个孔时会被改成「…-1」）。
    // 撤销顺序：散掉新组 → 删阵列（它依赖种子孔，先删）→ 删新孔/腰孔 → 旧孔改回原名 → 旧组按原样编回去。
    // 找不到的特征（用户已经 Ctrl+Z 或手动删掉了）跳过并如实报告，不算失败。
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
            public readonly List<string> Patterns = new List<string>();
            public int PatternHoles;                                    // 阵列代表的孔数（不含种子孔）
            public readonly List<string> Groups = new List<string>();
            public readonly List<GroupRestore> Restores = new List<GroupRestore>();
            public readonly List<KeyValuePair<string, string>> Renames = new List<KeyValuePair<string, string>>();   // SystemName → 原名
        }

        readonly List<Entry> entries = new List<Entry>();
        // 新建孔特征的 COM 引用（SystemName → 孔）。孔被「改孔型」换过 HoleData 以后 SystemName 会变（真机实测），
        // 按旧 SystemName 找不到时用引用兜底，免得"改过孔型再撤销本次打孔"删不掉。
        readonly Dictionary<string, P.Hole> refs = new Dictionary<string, P.Hole>(StringComparer.Ordinal);

        public int HoleCount { get { int n = 0; foreach (var e in entries) n += e.Holes.Count + e.Cutouts.Count + e.PatternHoles; return n; } }
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

        public void Hole(P.PartDocument part, P.Hole hole){ string s = Sys(hole); if (s.Length > 0) { For(part).Holes.Add(s); refs[s] = hole; } }
        public void Cutout(P.PartDocument part, P.ExtrudedCutout cut){ string s = SysOf(cut); if (s.Length > 0) For(part).Cutouts.Add(s); }
        public void Group(P.PartDocument part, P.FeatureGroup group){ string s = SysOf(group); if (s.Length > 0) For(part).Groups.Add(s); }
        public void Pattern(P.PartDocument part, P.Pattern pattern, int extraHoles){
            string s = HoleGrouping.Sys(pattern);
            if (s.Length == 0) return;
            var e = For(part); e.Patterns.Add(s); e.PatternHoles += Math.Max(0, extraHoles);
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
                foreach (var s in e.Holes) list.Add(new KeyValuePair<P.PartDocument, string>(e.Part, Current(s)));
                foreach (var s in e.Patterns) list.Add(new KeyValuePair<P.PartDocument, string>(e.Part, s));
            }
            return list;
        }

        // 把另一份记录并进来（一批里每张面各有一份）
        public void Absorb(HoleUndo other){
            if (other == null || ReferenceEquals(other, this)) return;
            foreach (var o in other.entries) {
                var e = For(o.Part);
                e.Holes.AddRange(o.Holes); e.Cutouts.AddRange(o.Cutouts); e.Groups.AddRange(o.Groups);
                e.Patterns.AddRange(o.Patterns); e.PatternHoles += o.PatternHoles;
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
                if (model == null) { o.Failed += e.Holes.Count + e.Cutouts.Count + e.PatternHoles; continue; }
                o.Parts.Add(e.Part);
                // ① 散掉这批新建的组（成员留着，下一步删）
                foreach (string s in e.Groups) {
                    var g = FindGroup(model, s);
                    if (g == null) continue;
                    try { g.Ungroup(); } catch (Exception ex) { Log.Write("HoleUndoUngroup", ex); }
                }
                // ② 删阵列（阵列挂在种子孔上，先删它；它代表的孔数算进"已撤销"）
                int perPattern = e.Patterns.Count == 0 ? 0 : e.PatternHoles / e.Patterns.Count;
                for (int i = e.Patterns.Count - 1; i >= 0; i--) {
                    var pat = FindPattern(model, e.Patterns[i]);
                    if (pat == null) { o.Missing += perPattern; continue; }
                    int extra = HoleGrouping.PatternExtra(pat);
                    try { pat.Delete(); o.Removed += extra; }
                    catch (Exception ex) { o.Failed += extra; Log.Write("HoleUndoDeletePattern", ex); o.Notes.Add(e.PartName + "：有一个阵列没能删除（" + AutoHoleWriter.Friendly(ex) + "）"); }
                }
                // ③ 删新孔、新腰孔（倒着删：后建的先删）
                for (int i = e.Holes.Count - 1; i >= 0; i--) {
                    var h = Ref(e.Holes[i]) ?? FindHole(model, e.Holes[i]);
                    if (h == null) { o.Missing++; continue; }
                    try { h.Delete(); o.Removed++; }
                    catch (Exception ex) { o.Failed++; Log.Write("HoleUndoDelete", ex); o.Notes.Add(e.PartName + "：有一个孔没能删除（" + AutoHoleWriter.Friendly(ex) + "）"); }
                }
                for (int i = e.Cutouts.Count - 1; i >= 0; i--) {
                    var c = FindCutout(model, e.Cutouts[i]);
                    if (c == null) { o.Missing++; continue; }
                    try { c.Delete(); o.Removed++; }
                    catch (Exception ex) { o.Failed++; Log.Write("HoleUndoDeleteSlot", ex); o.Notes.Add(e.PartName + "：有一个腰孔没能删除（" + ex.Message + "）"); }
                }
                // ④ 旧孔改回原名
                foreach (var kv in e.Renames) {
                    var h = FindHole(model, kv.Key);
                    if (h == null) continue;
                    try { h.Name = kv.Value; } catch (Exception ex) { Log.Write("HoleUndoRename", ex); }
                }
                // ⑤ 并组时散掉的旧组：按原成员、原名字编回去
                foreach (var r in e.Restores) {
                    var members = new List<object>();
                    foreach (string s in r.Members) { object m = (object)FindHole(model, s) ?? FindPattern(model, s); if (m != null) members.Add(m); }
                    if (members.Count < 2) continue;
                    try { HoleGrouping.Regroup(model, members, r.Name, r.Key); }
                    catch (Exception ex) {
                        Log.Write("HoleUndoRegroup", ex);
                        o.Notes.Add(e.PartName + "：之前的特征组「" + r.Name + "」没能恢复成组（孔都在，只是不再成组）。");
                    }
                }
            }
            entries.Clear(); refs.Clear();
            return o;
        }

        static P.Hole FindHole(P.Model model, string sys){
            int n = 0; try { n = model.Holes.Count; } catch { }
            for (int i = 1; i <= n; i++) { try { var h = model.Holes.Item(i); if (Sys(h) == sys) return h; } catch { } }
            return null;
        }
        P.Hole Ref(string sys){ P.Hole h; return refs.TryGetValue(sys, out h) && HoleEditUndo.Alive(h) ? h : null; }
        // 记下时的 SystemName → 现在的（孔被改过孔型就变了）
        string Current(string sys){ var h = Ref(sys); return h == null ? sys : Sys(h); }
        static P.Pattern FindPattern(P.Model model, string sys){
            int n = 0; try { n = model.Patterns.Count; } catch { }
            for (int i = 1; i <= n; i++) { try { var p = model.Patterns.Item(i); if (HoleGrouping.Sys(p) == sys) return p; } catch { } }
            return null;
        }
        static P.FeatureGroup FindGroup(P.Model model, string sys){
            int n = 0; try { n = model.FeatureGroups.Count; } catch { }
            for (int i = 1; i <= n; i++) { try { var g = model.FeatureGroups.Item(i); if (SysOf(g) == sys) return g; } catch { } }
            return null;
        }
        static P.ExtrudedCutout FindCutout(P.Model model, string sys){
            int n = 0; try { n = model.ExtrudedCutouts.Count; } catch { }
            for (int i = 1; i <= n; i++) { try { var c = model.ExtrudedCutouts.Item(i); if (SysOf(c) == sys) return c; } catch { } }
            return null;
        }
        static string Sys(P.Hole h){ try { return h == null ? "" : (h.SystemName ?? ""); } catch { return ""; } }
        static string SysOf(P.FeatureGroup g){ try { return g == null ? "" : (g.SystemName ?? ""); } catch { return ""; } }
        static string SysOf(P.ExtrudedCutout c){ try { return c == null ? "" : (c.SystemName ?? ""); } catch { return ""; } }
    }
}
