using System;
using System.Collections.Generic;
using P=SolidEdgePart;

namespace TianGongCadSuite {
    // 「改孔型」的撤销：记下每个孔原来的规格（回读）、贯通/深度，以及被改了名、换了规格键的特征。
    // 恢复走和换孔型**同一条**已验证的路：按原规格新建 HoleData → 换上 → 补写孔径 → 改回贯通/深度。
    // 为什么不把原来那个 HoleData 对象赋回去：探针 X4（通孔 Φ6.6 ↔ 沉孔 Φ6.6，孔径不变）这样做没问题，
    // 但 M6 螺纹孔改成 M8 沉孔（Φ9）再赋回原对象时，回读说是 Φ4.917，实际切的还是 Φ9，
    // 螺纹还被 CAD 换成了「Tr 11 x 2」（EditPatternE2E 实测）—— 原对象靠不住。
    // 怎么找回孔：**换了 HoleData 以后孔特征的 SystemName 会变**（真机 EditPatternE2E 实测，按旧 SystemName 找不到了），
    // 所以同时留着孔特征的 COM 引用，引用还活着就用它，按 SystemName 找只是兜底。
    public sealed class HoleEditUndo {
        sealed class HoleState {
            public P.PartDocument Part;
            public string Sys = "";
            public P.Hole Ref;               // 孔特征本身（SystemName 会变，引用不变）
            public HoleSpec Spec;            // 原来的规格（回读）
            public bool Through;
            public double Depth;             // 米（盲孔）
        }
        sealed class NameState {
            public P.PartDocument Part;
            public object Ref;
            public string Sys = "", Name = "", Tag = "";
        }

        readonly List<HoleState> holes = new List<HoleState>();
        readonly List<NameState> names = new List<NameState>();

        public bool IsEmpty { get { return holes.Count == 0; } }
        public int Count { get { return holes.Count; } }

        // 改之前记一次（同一个孔只记第一次的样子）
        public void Hole(P.PartDocument part, P.Hole h){
            string sys = HoleGrouping.Sys(h);
            if (sys.Length == 0 || Find(part, sys) != null) return;
            var s = new HoleState { Part = part, Sys = sys, Ref = h };
            s.Spec = HoleEditCad.ReadSpec(h, out s.Through);
            if (!s.Through) { try { s.Depth = h.Depth; } catch { } }
            holes.Add(s);
        }

        public void Named(P.PartDocument part, object feature){
            string sys = HoleGrouping.Sys(feature);
            if (sys.Length == 0) return;
            foreach (var n in names) if (n.Sys == sys && SamePart(n.Part, part)) return;
            names.Add(new NameState { Part = part, Ref = feature, Sys = sys, Name = HoleGrouping.NameOf(feature), Tag = HoleGrouping.ReadTag(feature) });
        }

        // 改到一半出错 / 改完特征失败：只把这一个孔恢复原样，并从撤销记录里拿掉
        public void RestoreOne(P.PartDocument part, P.Hole h){
            var s = Find(part, h);
            if (s == null) return;
            Restore(h, s);
            holes.Remove(s);
        }

        public sealed class Result {
            public int Restored, Missing, Failed;
            public readonly List<string> Notes = new List<string>();
            public string Summary {
                get {
                    string s = "已恢复 " + Restored + " 个孔特征的原孔型";
                    if (Missing > 0) s += "，" + Missing + " 个已经不在模型里";
                    if (Failed > 0) s += "，" + Failed + " 个没能恢复";
                    return s + "。";
                }
            }
        }

        public Result Run(){
            var r = new Result();
            var parts = new List<P.PartDocument>();
            var todo = new List<KeyValuePair<HoleState, P.Hole>>();
            foreach (var s in holes) {
                P.Model model = null;
                try { model = HoleEditCad.ModelOf(s.Part); } catch (Exception e) { r.Failed++; r.Notes.Add(e.Message); continue; }
                var h = Alive(s.Ref) ? s.Ref : HoleEditCad.FindHole(model, s.Sys);
                if (h == null) { r.Missing++; continue; }
                todo.Add(new KeyValuePair<HoleState, P.Hole>(s, h));
                if (!parts.Exists(p => SamePart(p, s.Part))) parts.Add(s.Part);
            }
            // 倒着恢复：多孔孔特征的种子孔（第 1 个输入孔，记的时候排在最前）要**最后**换回去。
            // 真机 EditUndoProbe（0.8.4）：种子孔先换、其余输入孔后换，多孔特征的几何停在改过的样子（M6 ×4 改 M8 沉孔再撤销，
            // 孔口 9/9/9/9 或 9/4.9/4.9/4.9，0.8.3 一样）；种子孔最后换就全部回到原样
            todo.Reverse();
            // 原来是螺纹孔的要先换成同孔径普通孔、重算，再换回螺纹数据（见 HoleEditCad.SwapData）。
            // 原来每个孔各重算一次整个零件（撤销一组 20 个 M6 = 20 次全零件重算）；现在先全部换普通孔，每个零件只重算一次
            var bad = new HashSet<HoleState>();
            var touched = new List<P.PartDocument>();
            foreach (var kv in todo) {
                if (kv.Key.Spec == null || kv.Key.Spec.Kind != HoleKind.Tapped) continue;
                try { HoleEditCad.SwapPlain(kv.Key.Part, kv.Value, kv.Key.Spec); if (!touched.Exists(p => SamePart(p, kv.Key.Part))) touched.Add(kv.Key.Part); }
                catch (Exception e) { bad.Add(kv.Key); r.Failed++; Log.Write("HoleEditUndo", e); r.Notes.Add("有一个孔没能恢复（" + AutoHoleWriter.Friendly(e) + "）"); }
            }
            foreach (var p in touched) { try { var m = HoleEditCad.ModelOf(p); AutoHoleWriter.Attempt("重算", () => { m.Recompute(); return 0; }); } catch (Exception e) { Log.Write("HoleEditUndoRecompute", e); } }
            foreach (var kv in todo) {
                if (bad.Contains(kv.Key)) continue;
                try { Finish(kv.Value, kv.Key); r.Restored++; }
                catch (Exception e) { r.Failed++; Log.Write("HoleEditUndo", e); r.Notes.Add("有一个孔没能恢复（" + AutoHoleWriter.Friendly(e) + "）"); }
            }
            // 原来是螺纹孔时上面是"先全部换普通孔、重算、再换螺纹数据"两步走：多孔孔特征的几何还会停在种子孔改过的孔径上
            // （EditUndoProbe：M6 ×4 → M8 沉孔 → 撤销，种子孔那个孔口还是 Φ9）。种子孔单独按原规格完整再换一次（换普通孔 → 重算 → 换回）就对了
            foreach (var kv in todo) {
                if (bad.Contains(kv.Key)) continue;
                try {
                    var model = HoleEditCad.ModelOf(kv.Key.Part);
                    var u = HoleEditCad.MultiOf(model, kv.Value);
                    if (u == null || HoleGrouping.Sys(HoleCombine.SeedOf(u)) != HoleGrouping.Sys(kv.Value)) continue;
                    Restore(kv.Value, kv.Key);
                } catch (Exception e) { Log.Write("HoleEditUndoSeed", e); }
            }
            foreach (var n in names) {
                try {
                    var model = HoleEditCad.ModelOf(n.Part);
                    object f = HoleGrouping.Sys(n.Ref).Length > 0 ? n.Ref
                             : (object)HoleEditCad.FindHole(model, n.Sys) ?? (object)HoleEditCad.FindPattern(model, n.Sys) ?? HoleEditCad.FindGroup(model, n.Sys);
                    if (f == null) continue;
                    HoleGrouping.TryName(f, n.Name);
                    if (n.Tag.Length > 0) HoleGrouping.Tag(f, n.Tag);
                } catch (Exception e) { Log.Write("HoleEditUndoName", e); }
            }
            foreach (var p in parts) {
                try { HoleEditCad.ModelOf(p).Recompute(); } catch (Exception e) { Log.Write("HoleEditUndoRecompute", e); }
                try { HoleScanCad.Invalidate(p); } catch { }
            }
            holes.Clear(); names.Clear();
            return r;
        }

        static void Restore(P.Hole h, HoleState s){
            HoleEditCad.SwapData(s.Part, HoleEditCad.ModelOf(s.Part), h, s.Spec);
            Extent(h, s);
        }
        // 螺纹孔第一步（换普通孔 + 重算）已经批量做过：只差换回原规格的数据、改回贯通/深度
        static void Finish(P.Hole h, HoleState s){
            HoleEditCad.SwapFinal(s.Part, h, s.Spec);
            Extent(h, s);
        }
        static void Extent(P.Hole h, HoleState s){
            bool through = true;
            try { through = h.ExtentType != P.FeaturePropertyConstants.igFinite; } catch { }
            if (!s.Through) {
                if (through) h.ExtentType = P.FeaturePropertyConstants.igFinite;
                if (s.Depth > 0) h.Depth = s.Depth;
            }
        }

        HoleState Find(P.PartDocument part, string sys){
            foreach (var s in holes) if (s.Sys == sys && SamePart(s.Part, part)) return s;
            return null;
        }
        HoleState Find(P.PartDocument part, P.Hole h){
            foreach (var s in holes) if (ReferenceEquals(s.Ref, h) && SamePart(s.Part, part)) return s;
            return Find(part, HoleGrouping.Sys(h));
        }
        // 引用还连着 CAD 里的那个特征吗（特征被删或文档关了会抛异常）
        internal static bool Alive(P.Hole h){
            if (h == null) return false;
            try { var n = h.SystemName; return !string.IsNullOrEmpty(n); } catch { return false; }
        }
        static bool SamePart(P.PartDocument a, P.PartDocument b){
            if (ReferenceEquals(a, b)) return true;
            try { return string.Equals(a.FullName, b.FullName, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(a.FullName); } catch { return false; }
        }
    }
}
