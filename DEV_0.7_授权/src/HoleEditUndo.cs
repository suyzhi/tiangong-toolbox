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
            foreach (var s in holes) {
                P.Model model = null;
                try { model = HoleEditCad.ModelOf(s.Part); } catch (Exception e) { r.Failed++; r.Notes.Add(e.Message); continue; }
                var h = Alive(s.Ref) ? s.Ref : HoleEditCad.FindHole(model, s.Sys);
                if (h == null) { r.Missing++; continue; }
                try { Restore(h, s); r.Restored++; }
                catch (Exception e) { r.Failed++; Log.Write("HoleEditUndo", e); r.Notes.Add("有一个孔没能恢复（" + AutoHoleWriter.Friendly(e) + "）"); }
                if (!parts.Exists(p => SamePart(p, s.Part))) parts.Add(s.Part);
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
