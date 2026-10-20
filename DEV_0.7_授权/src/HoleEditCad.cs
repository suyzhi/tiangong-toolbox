using System;
using System.Collections.Generic;
using G=SolidEdgeGeometry;
using P=SolidEdgePart;

namespace TianGongCadSuite {
    // 「改孔型」要改的一个孔：点中的那个孔特征（点到阵列复制出来的孔 = 它的种子孔），以及它所在的特征组。
    public sealed class EditableHole {
        public P.PartDocument Part;
        public string PartName = "";
        public P.Hole Hole;                  // 要改 HoleData 的孔特征
        public P.Pattern Pattern;            // 点到的是阵列里的孔时为这个阵列，否则 null
        public P.UserDefinedPattern Multi;   // 点到的是多孔孔特征里的孔时为这个特征（Hole = 它的种子孔），否则 null
        public P.FeatureGroup Group;         // 孔（或阵列）所在的组，没有为 null
        public string GroupName = "";
        public int GroupCount;               // 组代表的孔数（阵列按实例数算）
        public HoleSpec Spec;                // 现在的规格（回读）
        public bool Through = true;
        public bool HasWhere;                // 点中的位置（装配坐标），列表里点一条能跳过去
        public V3 Where;
        public double DiameterMm;
        public string Sys { get { return HoleGrouping.Sys(Hole); } }
        public string Label {
            get {
                string where = Group != null ? "「" + GroupName + "」" : "「" + HoleGrouping.NameOf(Hole) + "」";
                string what = Pattern != null ? "（阵列里的孔 → 改种子孔，整排跟着变）"
                            : Multi != null ? "（多孔孔特征「" + HoleGrouping.NameOf(Multi) + "」里的孔 → 整个特征 " + HoleCombine.CountOf(Multi) + " 个孔一起变）" : "";
                return PartName + "：" + where + what + "　现在：" + (Spec == null ? "?" : Spec.Summary);
            }
        }
    }

    // 「改孔型」的 CAD 层：认孔、回读规格、换孔型、改名、记撤销。
    //
    // 真机实测（tools/hole-feature-probe/HoleEditProbe.cs，2026-10-14）决定了做法：
    //   · 换孔型 = hole.HoleData = 新 HoleData（和打孔同一个 BuildHoleData，标准孔表、螺纹数据都对）；
    //   · **只换对象孔径不会变**：Φ6 换成 Φ9 的 HoleData，回读说 Φ9，实际切的还是 Φ6（X3）——
    //     HoleMultiProbe P2 切除体积少 7% 就是这个。换完再写一次 HoleData.HoleDiameter 就对了（X1，体积和理论一致）；
    //   · 盲孔改深度、盲孔改贯通都行（X9）；**贯通孔改不成盲孔**（ExtentType=igFinite 后孔失效，翻面也不行，X5/X5b）——
    //     遇到就拒绝并说明，不去删了重建（会打乱特征树和编组）；
    //   · 把原来那个 HoleData 对象赋回去 + 补写孔径就完全复原（X4）—— 撤销靠这个；
    //   · 阵列的种子孔改了，阵列跟着变（X6）。
    //   · 多孔孔特征（0.7.9，UserDefinedPattern）：换种子孔的 HoleData 整个特征的几何一起变；换别的输入孔也是整个特征一起变（J3）——
    //     所以多孔特征里"只改点中的那个孔"做不到，改的永远是整个特征。
    //     但插件的换法（螺纹孔两步换 + 补写螺纹描述）只换种子孔时，其余输入孔回读的还是旧螺纹（EditPatternE2E 实测，出图会标错），
    //     所以多孔特征的**每个输入孔**都换一遍。
    public static class HoleEditCad {
        const double MmToM = 0.001;
        const double ParallelSin = 0.02, CoaxialM = 0.00005;   // 认孔：轴线夹角 ≈1°、径向 0.05mm

        // ---- 认孔：点孔口圆边 / 孔内壁 ----
        public static EditableHole Resolve(object selection){
            var pg = PickGeometry.Unwrap(selection);
            ScannedHole sh;
            if (pg.Geometry is G.Edge) sh = HoleScanCad.ScanEdge(selection);
            else if (pg.Geometry is G.Face && !(((G.Face)pg.Geometry).Geometry is G.Plane)) { string label; sh = HoleScanCad.ScanSurface(selection, out label); }
            else throw new ArgumentException("请点孔口的圆形边线，或者孔的内壁。");
            var part = sh.Owner as P.PartDocument;
            if (part == null || sh.Placement == null) throw new ArgumentException("读不到这个孔所在的零件，请在装配里直接点零件上的孔。");
            var c = sh.Placement.InversePoint(sh.Center);
            var ax = sh.Placement.InverseNormal(sh.Axis).Unit();
            var model = ModelOf(part);
            int hc = 0; try { hc = model.Holes.Count; } catch { }
            for (int i = 1; i <= hc; i++) {
                P.Hole h = null;
                try { h = model.Holes.Item(i); } catch { }
                if (h != null && Owns(h, c, ax)) return At(Fill(new EditableHole { Part = part, Hole = h }, model), sh);
            }
            int pc = 0; try { pc = model.Patterns.Count; } catch { }
            for (int i = 1; i <= pc; i++) {
                P.Pattern p = null;
                try { p = model.Patterns.Item(i); } catch { }
                if (p == null || !Owns(p, c, ax)) continue;
                var seed = SeedOf(p);
                if (seed == null) throw new ArgumentException("点到的孔在阵列「" + HoleGrouping.NameOf(p) + "」里，但这个阵列的源特征不是孔，没法改孔型。");
                return At(Fill(new EditableHole { Part = part, Hole = seed, Pattern = p }, model), sh);
            }
            int uc = 0; try { uc = model.UserDefinedPatterns.Count; } catch { }
            for (int i = 1; i <= uc; i++) {
                P.UserDefinedPattern u = null;
                try { u = model.UserDefinedPatterns.Item(i); } catch { }
                if (u == null || !Owns(u, c, ax)) continue;
                var seed = HoleCombine.SeedOf(u);
                if (seed == null) throw new ArgumentException("点到的孔在「" + HoleGrouping.NameOf(u) + "」里，但读不到它的孔特征，没法改孔型。");
                return At(Fill(new EditableHole { Part = part, Hole = seed, Multi = u }, model), sh);
            }
            throw new ArgumentException("这个孔不是孔特征打出来的（可能是拉伸切除或别的特征），没法改孔型。");
        }

        // 「自动打孔 / 批量排孔」刚打的那批：按 SystemName 找回孔或阵列
        public static List<EditableHole> FromCreated(IList<KeyValuePair<P.PartDocument, string>> created, List<string> notes){
            var list = new List<EditableHole>();
            foreach (var kv in created) {
                try {
                    var model = ModelOf(kv.Key);
                    var h = FindHole(model, kv.Value);
                    if (h != null) { list.Add(Fill(new EditableHole { Part = kv.Key, Hole = h }, model)); continue; }
                    var p = FindPattern(model, kv.Value);
                    var seed = p == null ? null : SeedOf(p);
                    if (seed != null) { list.Add(Fill(new EditableHole { Part = kv.Key, Hole = seed, Pattern = p }, model)); continue; }
                    var u = HoleUndo.FindMulti(model, kv.Value);
                    var useed = u == null ? null : HoleCombine.SeedOf(u);
                    if (useed != null) list.Add(Fill(new EditableHole { Part = kv.Key, Hole = useed, Multi = u }, model));
                } catch (Exception e) { Log.Write("HoleEditFromCreated", e); notes.Add("有一个孔没找回来：" + e.Message); }
            }
            return list;
        }

        static EditableHole At(EditableHole e, ScannedHole sh){
            e.HasWhere = sh.Center.Finite; e.Where = sh.Center; e.DiameterMm = sh.MouthDiameterMm > 0 ? sh.MouthDiameterMm : sh.DiameterMm;
            return e;
        }

        // 改完以后名字、规格都变了：按 SystemName 重新读一遍（位置留着）
        public static List<EditableHole> Refresh(IList<EditableHole> targets, List<string> notes){
            var created = new List<KeyValuePair<P.PartDocument, string>>();
            foreach (var t in targets) created.Add(new KeyValuePair<P.PartDocument, string>(t.Part, t.Pattern != null ? HoleGrouping.Sys(t.Pattern) : t.Multi != null ? HoleGrouping.Sys(t.Multi) : t.Sys));
            var fresh = FromCreated(created, notes);
            for (int i = 0; i < fresh.Count && i < targets.Count; i++) {
                if (fresh[i].Sys != targets[i].Sys) continue;
                fresh[i].HasWhere = targets[i].HasWhere; fresh[i].Where = targets[i].Where; fresh[i].DiameterMm = targets[i].DiameterMm;
            }
            return fresh;
        }

        static EditableHole Fill(EditableHole e, P.Model model){
            try { e.PartName = e.Part.Name; } catch { }
            e.Group = GroupOf(model, e.Pattern != null ? (object)e.Pattern : e.Multi != null ? (object)e.Multi : e.Hole);
            if (e.Group == null && e.Pattern != null) e.Group = GroupOf(model, e.Hole);
            if (e.Group != null) {
                try { e.GroupName = e.Group.Name; } catch { }
                e.GroupCount = CountOf(e.Group);
            }
            e.Spec = ReadSpec(e.Hole, out e.Through);
            return e;
        }

        // ---- 回读规格 ----
        public static HoleSpec ReadSpec(P.Hole h, out bool through){
            var v = new HoleDataValues();
            through = true;
            try { through = h.ExtentType != P.FeaturePropertyConstants.igFinite; } catch { }
            v.Through = through;
            if (!through) { try { v.Depth = h.Depth / MmToM; } catch { } }
            P.HoleData d = null;
            try { d = h.HoleData as P.HoleData; } catch { }
            if (d != null) {
                v.HoleType = Str(() => d.HoleType.ToString());
                v.Treatment = Str(() => d.TreatmentType.ToString());
                v.ThreadDescription = Str(() => d.ThreadDescription);
                v.Size = Str(() => d.Size);
                v.HoleDiameter = Num(() => d.HoleDiameter) / MmToM;
                v.CounterboreDiameter = Num(() => d.CounterboreDiameter) / MmToM;
                v.CounterboreDepth = Num(() => d.CounterboreDepth) / MmToM;
                v.CountersinkDiameter = Num(() => d.CountersinkDiameter) / MmToM;
                v.CountersinkAngle = Num(() => d.CountersinkAngle);
                v.ThreadDepthMethod = Str(() => d.ThreadDepthMethod.ToString());
                v.ThreadDepth = Num(() => d.ThreadDepth) / MmToM;
                v.BottomAngle = Num(() => d.BottomAngle);
            }
            return HoleSpecRead.From(v);
        }

        // ---- 改 ----
        public sealed class Outcome {
            public int Changed, Failed;
            public readonly List<string> Notes = new List<string>();
            public readonly List<ResultList.Item> Items = new List<ResultList.Item>();
            public readonly HoleEditUndo Undo = new HoleEditUndo();
        }

        // 要改的孔特征（去重）：整组时把组里的孔、阵列的种子孔都带上
        public static List<P.Hole> Expand(IList<EditableHole> targets, bool wholeGroup){
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var list = new List<P.Hole>();
            foreach (var t in targets) {
                var holes = t.Multi != null ? HoleCombine.Inputs(t.Multi) : new List<P.Hole>{ t.Hole };
                if (holes.Count == 0) holes.Add(t.Hole);
                if (wholeGroup && t.Group != null) holes = GroupHoles(t.Group);
                foreach (var h in holes) {
                    string key = PartKey(t.Part) + "|" + HoleGrouping.Sys(h);
                    if (seen.Add(key)) list.Add(h);
                }
            }
            return list;
        }

        // 贯通孔改不成盲孔（真机 X5）：先挑出来，整批拒绝，免得改到一半
        public static int ThroughToBlind(IList<EditableHole> targets, bool wholeGroup, HoleSpec spec){
            if (spec.Through) return 0;
            int n = 0;
            foreach (var t in targets) {
                var holes = wholeGroup && t.Group != null ? GroupHoles(t.Group) : new List<P.Hole>{ t.Hole };
                foreach (var h in holes) { bool through; ReadSpec(h, out through); if (through) n++; }
            }
            return n;
        }

        public static Outcome Apply(IList<EditableHole> targets, HoleSpec spec, bool wholeGroup){
            var o = new Outcome();
            if (targets == null || targets.Count == 0) return o;
            string problem = spec.ThreadProblem;
            if (problem.Length > 0) throw new ArgumentException(problem);
            int bad = ThroughToBlind(targets, wholeGroup, spec);
            if (bad > 0) throw new InvalidOperationException("要改的孔里有 " + bad + " 个是贯通孔。天工接口不支持把贯通孔直接改成盲孔（实测会让孔失效）。要盲孔请撤销打孔后重新打，或者先改成贯通的其它孔型。");

            // 按零件分开做：每个零件改完重算一次
            var byPart = new List<KeyValuePair<P.PartDocument, List<EditableHole>>>();
            foreach (var t in targets) {
                List<EditableHole> l = null;
                foreach (var kv in byPart) if (PartKey(kv.Key) == PartKey(t.Part)) { l = kv.Value; break; }
                if (l == null) { l = new List<EditableHole>(); byPart.Add(new KeyValuePair<P.PartDocument, List<EditableHole>>(t.Part, l)); }
                l.Add(t);
            }
            foreach (var kv in byPart) ApplyPart(kv.Key, kv.Value, spec, wholeGroup, o);
            return o;
        }

        static void ApplyPart(P.PartDocument part, List<EditableHole> targets, HoleSpec spec, bool wholeGroup, Outcome o){
            string partName = ""; try { partName = part.Name; } catch { }
            var model = ModelOf(part);
            var holes = Expand(targets, wholeGroup);
            var changed = new List<P.Hole>();
            foreach (var h in holes) {
                string name = HoleGrouping.NameOf(h);
                try {
                    o.Undo.Hole(part, h);
                    SwapData(part, model, h, spec);
                    bool through; ReadSpec(h, out through);
                    if (spec.Through && !through) AutoHoleWriter.Attempt("改贯通", () => { h.ExtentType = P.FeaturePropertyConstants.igThroughAll; return 0; });
                    else if (!spec.Through) AutoHoleWriter.Attempt("改深度", () => { h.Depth = spec.Depth * MmToM; return 0; });
                    changed.Add(h);
                } catch (Exception e) {
                    o.Failed++;
                    o.Notes.Add(partName + "：「" + name + "」没改成（" + AutoHoleWriter.Friendly(e) + "）");
                    Log.Write("HoleEditApply", e);
                    o.Undo.RestoreOne(part, h);
                }
            }
            try { AutoHoleWriter.Attempt("重算", () => { model.Recompute(); return 0; }); } catch (Exception e) { Log.Write("HoleEditRecompute", e); }

            // 重算后逐个看状态：失败的退回原样（孔型改错了不能留一个坏特征给用户）
            var failed = new List<P.Hole>();
            foreach (var h in changed) if (!FeatureOk(h)) failed.Add(h);
            foreach (var t in targets) if (t.Pattern != null && !FeatureOk(t.Pattern) && !failed.Contains(t.Hole) && changed.Contains(t.Hole)) failed.Add(t.Hole);
            foreach (var t in targets) if (t.Multi != null && !FeatureOk(t.Multi)) foreach (var x in HoleCombine.Inputs(t.Multi)) if (!failed.Contains(x) && changed.Contains(x)) failed.Add(x);
            if (failed.Count > 0) {
                foreach (var h in failed) {
                    o.Notes.Add(partName + "：「" + HoleGrouping.NameOf(h) + "」改成" + HoleNaming.Short(spec) + "后 CAD 报特征失败，已恢复原样。");
                    o.Undo.RestoreOne(part, h);
                    changed.Remove(h);
                }
                try { model.Recompute(); } catch (Exception e) { Log.Write("HoleEditRecompute", e); }
                o.Failed += failed.Count;
            }

            foreach (var h in changed) {
                string note = AutoHoleWriter.Audit(h, spec);
                if (note.Length > 0) o.Notes.Add(partName + "：「" + HoleGrouping.NameOf(h) + "」" + note);
            }
            Rename(model, part, targets, changed, spec, wholeGroup, o);
            var counted = new HashSet<string>(StringComparer.Ordinal);
            foreach (var h in changed) {
                int extra = 0;
                P.UserDefinedPattern multi = null;
                foreach (var t in targets) if (t.Pattern != null && ReferenceEquals(t.Hole, h)) extra = HoleGrouping.PatternExtra(t.Pattern);
                foreach (var p in PatternsOf(model, h)) extra = Math.Max(extra, HoleGrouping.PatternExtra(p));
                foreach (var t in targets) if (t.Multi != null && HoleCombine.Inputs(t.Multi).Contains(h)) multi = t.Multi;
                if (multi == null) multi = MultiOf(model, h);
                if (multi != null) {
                    if (!counted.Add(HoleGrouping.Sys(multi))) continue;   // 多孔特征的每个输入孔都换过，只算一次
                    int n = HoleCombine.CountOf(multi);
                    o.Changed += n;
                    o.Items.Add(new ResultList.Item(ResultList.Kind.Done, partName + "：" + HoleGrouping.NameOf(multi) + "（多孔孔特征 " + n + " 个孔）"));
                    continue;
                }
                o.Changed += 1 + extra;
                o.Items.Add(new ResultList.Item(ResultList.Kind.Done, partName + "：" + HoleGrouping.NameOf(h) + (extra > 0 ? "（连同阵列 " + extra + " 个）" : "")));
            }
            try { HoleScanCad.Invalidate(part); } catch { }
        }

        // 给孔换上 spec 的 HoleData。真机实测的两个坑（HoleEditProbe X3、EditPatternE2E）：
        //   · 只换对象孔径不变 —— 换完在孔现在挂着的 HoleData 上再写一次孔径；
        //   · 换成**螺纹孔**时，CAD 按孔"现在的几何孔径"去查螺纹表，把螺纹改写掉：Φ9 的孔换成 M6 螺纹数据，
        //     螺纹变成「Tr 11 x 2」（小径 9），孔径也还是 Φ9。所以螺纹孔分两步：先换一个同孔径的普通孔把几何孔径改过来、
        //     重算，再换螺纹数据；螺纹描述还不对就再写一次。
        // 撤销（HoleEditUndo）走同一条路。
        internal static void SwapData(P.PartDocument part, P.Model model, P.Hole h, HoleSpec spec){
            if (spec.Kind == HoleKind.Tapped) {
                var plain = spec.Clone();
                plain.Kind = HoleKind.Through; plain.Chamfer = false;
                var pd = AutoHoleWriter.BuildHoleData(part, plain, new DrillResult());
                AutoHoleWriter.Attempt("先换孔径", () => { h.HoleData = pd; return 0; });
                AutoHoleWriter.Attempt("写孔径", () => { ((P.HoleData)h.HoleData).HoleDiameter = spec.HoleDiameter * MmToM; return 0; });
                AutoHoleWriter.Attempt("重算", () => { model.Recompute(); return 0; });
            }
            var data = AutoHoleWriter.BuildHoleData(part, spec, new DrillResult());
            AutoHoleWriter.Attempt("换孔型", () => { h.HoleData = data; return 0; });
            AutoHoleWriter.Attempt("写孔径", () => { ((P.HoleData)h.HoleData).HoleDiameter = spec.HoleDiameter * MmToM; return 0; });
            if (spec.Kind == HoleKind.Tapped && spec.ThreadSize.Length > 0) {
                var now = (P.HoleData)h.HoleData;
                string td = Str(() => now.ThreadDescription);
                if (td.IndexOf(spec.ThreadSize, StringComparison.OrdinalIgnoreCase) < 0) {
                    Log.Write("HoleEditThread", "换螺纹后回读 " + td + "，要求 " + spec.ThreadSize + "，再写一次");
                    try { now.ThreadDescription = spec.ThreadSize; } catch (Exception e) { Log.Write("HoleEditThread", e); }
                }
            }
        }

        // 改名：只改插件起的名字（挂着规格键的特征），用户自己起的名字不动
        static void Rename(P.Model model, P.PartDocument part, List<EditableHole> targets, List<P.Hole> changed, HoleSpec spec, bool wholeGroup, Outcome o){
            if (changed.Count == 0) return;
            string key = HoleNaming.SpecKey(spec);
            var taken = AutoHoleWriter.ExistingFeatureNames(model);
            var done = new HashSet<string>(StringComparer.Ordinal);
            foreach (var t in targets) {
                if (wholeGroup && t.Group != null) {
                    string gs = HoleGrouping.Sys(t.Group);
                    if (!done.Add(gs)) continue;
                    var members = Members(t.Group);
                    bool allChanged = true;
                    foreach (var m in members) { var mh = m as P.Hole; if (mh != null && !changed.Contains(mh)) allChanged = false; }
                    foreach (var m in members) { var mu = m as P.UserDefinedPattern; if (mu != null) foreach (var x in HoleCombine.Inputs(mu)) if (!changed.Contains(x)) allChanged = false; }
                    if (!allChanged) { o.Notes.Add("「" + t.GroupName + "」里有孔没改成，组名和孔名保持原样。"); continue; }
                    if (HoleGrouping.ReadTag(t.Group).Length == 0) {
                        foreach (var m in members) RecordAndTag(o, part, m, key);
                        continue;   // 用户自己编的组：只换孔型，不改名
                    }
                    foreach (var m in members) {
                        o.Undo.Named(part, m); taken.Remove(HoleGrouping.NameOf(m));
                        foreach (var x in HoleGrouping.InputsOf(m)) { o.Undo.Named(part, x); taken.Remove(HoleGrouping.NameOf(x)); }
                    }
                    o.Undo.Named(part, t.Group); taken.Remove(HoleGrouping.NameOf(t.Group));
                    int holes = 0; foreach (var m in members) if (m is P.Hole) holes++;
                    var names = HoleNaming.Members(spec, holes, taken);
                    int k = 0;
                    foreach (var m in members) {
                        HoleGrouping.Tag(m, key);
                        var mu = m as P.UserDefinedPattern;
                        if (mu != null) { HoleGrouping.NameMulti(spec, mu, taken); foreach (var x in HoleGrouping.InputsOf(mu)) HoleGrouping.Tag(x, key); continue; }
                        string want = m is P.Hole ? names[k++] : HoleNaming.Unique(HoleNaming.PatternName(spec), taken);
                        if (HoleGrouping.TryName(m, want)) taken.Add(want);
                    }
                    string gname = HoleNaming.Unique(HoleNaming.Group(spec, CountOf(t.Group)), taken);
                    if (HoleGrouping.TryName(t.Group, gname)) taken.Add(gname);
                    HoleGrouping.Tag(t.Group, key);
                } else {
                    if (!changed.Contains(t.Hole) || !done.Add(HoleGrouping.Sys(t.Hole))) continue;
                    if (t.Multi != null) {
                        // 多孔特征整个换了规格：特征名「规格 ×N」、输入孔「规格-序号」都按新规格重起（用户自己起的名字不动）
                        if (HoleGrouping.ReadTag(t.Multi).Length > 0) {
                            o.Undo.Named(part, t.Multi);
                            foreach (var x in HoleGrouping.InputsOf(t.Multi)) o.Undo.Named(part, x);
                            HoleGrouping.NameMulti(spec, t.Multi, taken);
                            HoleGrouping.Tag(t.Multi, key);
                            foreach (var x in HoleGrouping.InputsOf(t.Multi)) HoleGrouping.Tag(x, key);
                        }
                        if (t.Group != null) o.Notes.Add("只改了「" + t.GroupName + "」里的「" + HoleGrouping.NameOf(t.Multi) + "」，组名没改（组里其它孔还是原来的规格）。");
                        continue;
                    }
                    var feats = new List<object>{ t.Hole };
                    if (t.Pattern != null) feats.Add(t.Pattern);
                    foreach (var f in feats) {
                        if (HoleGrouping.ReadTag(f).Length == 0) continue;
                        o.Undo.Named(part, f);
                        taken.Remove(HoleGrouping.NameOf(f));
                        string want = f is P.Pattern ? HoleNaming.PatternName(spec) : (t.Group == null && t.Pattern == null ? HoleNaming.Short(spec) : HoleNaming.Members(spec, 1, taken)[0]);
                        want = HoleNaming.Unique(want, taken);
                        if (HoleGrouping.TryName(f, want)) taken.Add(want);
                        HoleGrouping.Tag(f, key);
                    }
                    if (t.Group != null) o.Notes.Add("只改了「" + t.GroupName + "」里点中的孔，组名没改（组里其它孔还是原来的规格）。");
                }
            }
        }

        static void RecordAndTag(Outcome o, P.PartDocument part, object m, string key){
            if (HoleGrouping.ReadTag(m).Length == 0) return;
            o.Undo.Named(part, m);
            HoleGrouping.Tag(m, key);
        }

        // ---- 查找 ----
        internal static P.Model ModelOf(P.PartDocument part){
            if (part == null || part.Models.Count < 1) throw new InvalidOperationException("这个零件没有实体模型。");
            return part.Models.Item(1);
        }
        static string PartKey(P.PartDocument part){
            try { string f = part.FullName; if (!string.IsNullOrEmpty(f)) return f; } catch { }
            try { return part.Name ?? ""; } catch { return ""; }
        }
        internal static P.Hole FindHole(P.Model model, string sys){
            int n = 0; try { n = model.Holes.Count; } catch { }
            for (int i = 1; i <= n; i++) { try { var h = model.Holes.Item(i); if (HoleGrouping.Sys(h) == sys) return h; } catch { } }
            return null;
        }
        internal static P.Pattern FindPattern(P.Model model, string sys){
            int n = 0; try { n = model.Patterns.Count; } catch { }
            for (int i = 1; i <= n; i++) { try { var p = model.Patterns.Item(i); if (HoleGrouping.Sys(p) == sys) return p; } catch { } }
            return null;
        }
        internal static P.FeatureGroup FindGroup(P.Model model, string sys){
            int n = 0; try { n = model.FeatureGroups.Count; } catch { }
            for (int i = 1; i <= n; i++) { try { var g = model.FeatureGroups.Item(i); if (HoleGrouping.Sys(g) == sys) return g; } catch { } }
            return null;
        }
        static P.Hole SeedOf(P.Pattern p){
            try {
                Array arr = new object[0];
                p.GetInputFeatures(ref arr);
                foreach (object o in arr) { var h = o as P.Hole; if (h != null) return h; }
            } catch (Exception e) { Log.Write("HoleEditSeed", e); }
            return null;
        }
        // hole 是哪个多孔特征的输入孔（不是的返回 null）
        static P.UserDefinedPattern MultiOf(P.Model model, P.Hole hole){
            string sys = HoleGrouping.Sys(hole);
            int n = 0; try { n = model.UserDefinedPatterns.Count; } catch { }
            for (int i = 1; i <= n; i++) {
                try { var u = model.UserDefinedPatterns.Item(i); foreach (var h in HoleCombine.Inputs(u)) if (ReferenceEquals(h, hole) || HoleGrouping.Sys(h) == sys) return u; } catch { }
            }
            return null;
        }
        // 以 hole 为种子的阵列
        static List<P.Pattern> PatternsOf(P.Model model, P.Hole hole){
            var list = new List<P.Pattern>();
            string sys = HoleGrouping.Sys(hole);
            int n = 0; try { n = model.Patterns.Count; } catch { }
            for (int i = 1; i <= n; i++) {
                try { var p = model.Patterns.Item(i); var s = SeedOf(p); if (s != null && HoleGrouping.Sys(s) == sys) list.Add(p); } catch { }
            }
            return list;
        }
        static P.FeatureGroup GroupOf(P.Model model, object feature){
            string sys = HoleGrouping.Sys(feature);
            int n = 0; try { n = model.FeatureGroups.Count; } catch { }
            for (int i = 1; i <= n; i++) {
                P.FeatureGroup g = null;
                try { g = model.FeatureGroups.Item(i); } catch { }
                if (g == null) continue;
                foreach (var m in Members(g)) if (HoleGrouping.Sys(m) == sys) return g;
            }
            return null;
        }
        internal static List<object> Members(P.FeatureGroup g){
            var list = new List<object>();
            int mc = 0; try { mc = g.Count; } catch { }
            for (int k = 1; k <= mc; k++) { try { list.Add(g.Item(k)); } catch { } }
            return list;
        }
        // 组里要改 HoleData 的孔：组成员里的孔 + 组里阵列的种子孔
        static List<P.Hole> GroupHoles(P.FeatureGroup g){
            var list = new List<P.Hole>();
            foreach (var m in Members(g)) {
                var h = m as P.Hole;
                if (h != null) { if (!list.Contains(h)) list.Add(h); continue; }
                var u = m as P.UserDefinedPattern;
                if (u != null) { foreach (var x in HoleCombine.Inputs(u)) if (!list.Contains(x)) list.Add(x); continue; }
                var p = m as P.Pattern;
                var s = p == null ? null : SeedOf(p);
                if (s != null && !list.Exists(x => HoleGrouping.Sys(x) == HoleGrouping.Sys(s))) list.Add(s);
            }
            return list;
        }
        static int CountOf(P.FeatureGroup g){
            int n = 0;
            foreach (var m in Members(g)) n += HoleGrouping.HolesIn(m);
            return n;
        }
        internal static bool FeatureOk(object feature){
            try {
                object d;
                var h = feature as P.Hole; if (h != null) return (int)h.GetStatusEx(out d) == (int)P.FeatureStatusConstants.igFeatureOK;
                var p = feature as P.Pattern; if (p != null) return (int)p.GetStatusEx(out d) == (int)P.FeatureStatusConstants.igFeatureOK;
                var u = feature as P.UserDefinedPattern; if (u != null) return (int)u.GetStatusEx(out d) == (int)P.FeatureStatusConstants.igFeatureOK;
            } catch { }
            return false;
        }

        // 这个特征的圆柱面里有没有和 (c, ax) 同轴的：孔壁 / 阵列复制出来的孔壁
        static bool Owns(object feature, V3 c, V3 ax){
            object faces = null;
            try {
                var h = feature as P.Hole; if (h != null) faces = h.get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll);
                var p = feature as P.Pattern; if (p != null) faces = p.get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll);
                var u = feature as P.UserDefinedPattern; if (u != null) faces = u.get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll);
            } catch { return false; }
            var fs = faces as G.Faces;
            if (fs == null) return false;
            foreach (G.Face f in fs) {
                G.Cylinder cyl = null;
                try { cyl = f.Geometry as G.Cylinder; } catch { }
                if (cyl == null) continue;
                Array bp = new double[3], av = new double[3]; double r = 0;
                try { cyl.GetCylinderData(ref bp, ref av, out r); } catch { continue; }
                V3 a2;
                try { a2 = V3.From(av).Unit(); } catch { continue; }
                if (a2.Cross(ax).Length > ParallelSin) continue;
                var d = V3.From(bp) - c;
                if ((d - a2 * d.Dot(a2)).Length <= CoaxialM) return true;
            }
            return false;
        }

        static string Str(Func<string> f){ try { return f() ?? ""; } catch { return ""; } }
        static double Num(Func<double> f){ try { return f(); } catch { return 0; } }
    }
}
