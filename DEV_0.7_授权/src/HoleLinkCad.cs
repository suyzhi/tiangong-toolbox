using System;
using System.Collections.Generic;
using System.Globalization;
using A=SolidEdgeAssembly;
using F=SolidEdgeFramework;
using P=SolidEdgePart;

namespace TianGongCadSuite {
    // 装配里的一个零件实例（含嵌套子装配里的）。
    public sealed class OccurrenceNode {
        public string Path = "";        // "子装配.asm:1/板A.par:1"
        public string Name = "";
        public string FullName = "";    // 零件文件全路径
        public string FileName = "";
        public P.PartDocument Part;     // 子装配节点为 null
        public Transform Raw;           // 实例自己 GetMatrix 读到的
        public Transform Composed;      // 从顶层逐级乘下来的
        public Transform Placement(string mode){ return mode == "C" ? Composed : Raw; }
    }

    // 装配实例索引：打孔时用它认出"参考孔/打孔面在哪个实例上"，同步时用它找回实例、读现在的位置。
    //
    // 认实例的办法是"零件文件相同 + 变换矩阵相同"：选择引用（Reference.GetMatrix）给的就是
    // 零件局部 → 顶层装配的变换，拿它去和每个实例的矩阵比。嵌套子装配里 SubOccurrence.GetMatrix
    // 到底是相对父装配还是相对顶层，手册没说清，所以两种都算（Raw / Composed），哪个对得上就记哪个，
    // 同步时按同一种取法读，不用赌。
    public sealed class OccurrenceIndex {
        public readonly List<OccurrenceNode> Nodes = new List<OccurrenceNode>();
        const int MaxDepth = 8;

        public static OccurrenceIndex Build(A.AssemblyDocument asm){ return Build(asm, null); }
        // progress(第几个顶层实例, 共几个)：大装配要把每个零件加载一遍、一两分钟，窗口拿它刷状态栏、泵消息
        public static OccurrenceIndex Build(A.AssemblyDocument asm, Action<int, int> progress){
            var idx = new OccurrenceIndex();
            if (asm == null) return idx;
            int n = 0;
            try { n = asm.Occurrences.Count; } catch (Exception e) { Log.Write("OccIndexCount", e); }
            for (int i = 1; i <= n; i++) {
                if (progress != null) { try { progress(i, n); } catch { } }
                A.Occurrence occ = null;
                try { occ = asm.Occurrences.Item(i); } catch { }
                if (occ == null) continue;
                try {
                    string name = occ.Name;
                    Array m = new double[16]; occ.GetMatrix(ref m);
                    var raw = new Transform(m);
                    var node = new OccurrenceNode { Path = name, Name = name, Raw = raw, Composed = raw };
                    object doc = null; try { doc = occ.OccurrenceDocument; } catch { }
                    Fill(node, doc);
                    idx.Nodes.Add(node);
                    bool sub = false; try { sub = occ.Subassembly; } catch { }
                    if (sub) { try { WalkSubs(idx, occ.SubOccurrences, node.Path, raw, 1); } catch (Exception e) { Log.Write("OccIndexSub " + name, e); } }
                } catch (Exception e) { Log.Write("OccIndexNode", e); }
            }
            return idx;
        }

        static void WalkSubs(OccurrenceIndex idx, A.SubOccurrences subs, string parentPath, Transform parent, int depth){
            if (subs == null || depth > MaxDepth) return;
            int n = 0; try { n = subs.Count; } catch { }
            for (int i = 1; i <= n; i++) {
                A.SubOccurrence s = null;
                try { s = subs.Item(i); } catch { }
                if (s == null) continue;
                try {
                    string name = s.Name;
                    Array m = new double[16]; s.GetMatrix(ref m);
                    var raw = new Transform(m);
                    var node = new OccurrenceNode { Path = parentPath + "/" + name, Name = name, Raw = raw, Composed = Compose(parent, raw) };
                    object doc = null; try { doc = s.SubOccurrenceDocument; } catch { }
                    Fill(node, doc);
                    idx.Nodes.Add(node);
                    bool sub = false; try { sub = s.Subassembly; } catch { }
                    if (sub) WalkSubs(idx, s.SubOccurrences, node.Path, node.Composed, depth + 1);
                } catch (Exception e) { Log.Write("OccIndexSubNode", e); }
            }
        }

        static void Fill(OccurrenceNode node, object doc){
            var part = doc as P.PartDocument;
            node.Part = part;
            if (part == null) return;
            try { node.FullName = part.FullName ?? ""; } catch { }
            if (node.FullName.Length == 0) { try { node.FullName = part.Name ?? ""; } catch { } }
            node.FileName = System.IO.Path.GetFileName(node.FullName);
        }

        // outer ∘ inner：先 inner 再 outer
        static Transform Compose(Transform outer, Transform inner){
            var o = outer.Point(new V3(inner.M[12], inner.M[13], inner.M[14]));
            var x = outer.Vector(new V3(inner.M[0], inner.M[1], inner.M[2]));
            var y = outer.Vector(new V3(inner.M[4], inner.M[5], inner.M[6]));
            var z = outer.Vector(new V3(inner.M[8], inner.M[9], inner.M[10]));
            return Transform.Frame(o, x, y, z);
        }

        static bool Same(Transform a, Transform b){
            if (a == null || b == null) return false;
            for (int i = 0; i < 16; i++) if (Math.Abs(a.M[i] - b.M[i]) > 1e-7) return false;
            return true;
        }

        static string DocName(P.PartDocument part){
            string s = "";
            try { s = part.FullName ?? ""; } catch { }
            if (s.Length == 0) { try { s = part.Name ?? ""; } catch { } }
            return s;
        }

        // 选择引用（零件文档 + 零件局部→装配的变换）对应哪个实例。mode 返回矩阵取法。
        public OccurrenceNode Locate(object partDocument, Transform placement, out string mode){
            mode = "R";
            var part = partDocument as P.PartDocument;
            if (part == null) return null;
            string full = DocName(part);
            OccurrenceNode only = null; int same = 0;
            foreach (var n in Nodes) {
                if (n.Part == null || !string.Equals(n.FullName, full, StringComparison.OrdinalIgnoreCase)) continue;
                same++; only = n;
                if (Same(n.Raw, placement)) { mode = "R"; return n; }
                if (Same(n.Composed, placement)) { mode = "C"; return n; }
            }
            // 矩阵谁都对不上（例如原位编辑时引用矩阵的取法不同），但这个零件在装配里只有一个实例：就是它
            return same == 1 ? only : null;
        }

        // 同步时按记下的路径找回实例；路径变了（实例改名、删了重插）就按文件名兜底，但必须唯一。
        public OccurrenceNode Find(string path, string fileName){
            if (!string.IsNullOrEmpty(path))
                foreach (var n in Nodes) if (n.Part != null && n.Path == path) return n;
            if (string.IsNullOrEmpty(fileName)) return null;
            OccurrenceNode only = null; int count = 0;
            foreach (var n in Nodes) {
                if (n.Part == null) continue;
                if (string.Equals(n.FileName, fileName, StringComparison.OrdinalIgnoreCase)) { only = n; count++; }
            }
            return count == 1 ? only : null;
        }

        // 装配里出现过的零件文档（去重）
        public List<OccurrenceNode> DistinctParts(){
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<OccurrenceNode>();
            foreach (var n in Nodes) {
                if (n.Part == null) continue;
                string key = n.FullName.Length > 0 ? n.FullName : n.Path;
                if (seen.Add(key)) list.Add(n);
            }
            return list;
        }
    }

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
            P.Model model = null;
            try { if (part.Models.Count >= 1) model = part.Models.Item(1); } catch { }
            if (model == null) return list;
            int n = 0; try { n = model.Holes.Count; } catch { }
            for (int i = 1; i <= n; i++) {
                P.Hole h = null;
                try { h = model.Holes.Item(i); } catch { }
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

        // 参考孔（零件局部的孔心 + 轴线）是哪个孔特征打出来的：孔特征的某个孔心落在这根轴线上（径向 ≤0.02mm）。
        public static Entry OnAxis(List<Entry> entries, V3 center, V3 axis){
            if (entries == null) return null;
            V3 a;
            try { a = axis.Unit(); } catch { return null; }
            Entry best = null; double bestR = 0.00002;
            foreach (var e in entries) {
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

    // 关联孔的一条检查结果。
    public sealed class LinkedHole {
        public P.PartDocument Part;
        public P.Hole Hole;
        public HoleLink Link;
        public OccurrenceNode Target, Reference;
        public string FeatureName = "";
        public string PartName = "";
        public V3 Current, Desired;
        public V3 RefCenterNow;          // 参考孔现在的孔心（参考零件局部）
        public double OffsetMm;
        public string Problem = "";
        public bool NeedsMove { get { return Problem.Length == 0 && OffsetMm > HoleLink.SyncToleranceMm; } }
        public string Label { get { return PartName + " · " + FeatureName; } }
    }

    public sealed class SyncReport {
        public readonly List<LinkedHole> Items = new List<LinkedHole>();
        public int Moved, Failed;
        public readonly List<string> Notes = new List<string>();
        public int Linked { get { return Items.Count; } }
        public int Misplaced { get { int k = 0; foreach (var i in Items) if (i.NeedsMove) k++; return k; } }
        public int Broken { get { int k = 0; foreach (var i in Items) if (i.Problem.Length > 0) k++; return k; } }
        // 给界面的一句话
        public string Summary {
            get {
                // 0.7.9 起保持关联走天工原生关联，这里只查得到老办法（插件关联记录）打的孔；
                // 原来这句说"还没有关联孔"，刚打完 6 个原生关联孔点「检查」也这么说，用户会以为关联没挂上
                if (Linked == 0) return "没有需要插件检查的关联孔。天工原生关联的孔（特征树里带锁链的「孔位链接-…」）由天工自己跟着参考孔走；没跟上时点「同步孔位」或天工里「更新所有链接」。";
                string s = "关联孔 " + Linked + " 个";
                if (Misplaced > 0) s += "，其中 " + Misplaced + " 个已和参考孔错位";
                else s += "，全部与参考孔同轴";
                if (Broken > 0) s += "；" + Broken + " 个找不到参考孔";
                return s + "。";
            }
        }
    }

    // 孔位同步：按关联记录重新求孔心，把错位的孔挪回与参考孔同轴。
    // 挪孔用的是"改孔特征轮廓里的 Hole2d 圆心 + 重算"，真机实测孔特征照常 igFeatureOK、孔跟着走
    // （tools/hole-feature-probe T5 / R3）。挪完回读状态，失败的就地挪回原位，不留坏特征。
    public static class HoleSync {
        public static SyncReport Scan(A.AssemblyDocument asm){ return Scan(asm, null); }
        public static SyncReport Scan(A.AssemblyDocument asm, Action<int, int> progress){
            var report = new SyncReport();
            if (asm == null) return report;
            var idx = OccurrenceIndex.Build(asm, progress);
            var refCache = new Dictionary<string, List<HoleFeatures.Entry>>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in idx.DistinctParts()) {
                List<HoleFeatures.Entry> holes;
                try { holes = HoleFeatures.Read(node.Part, true); } catch (Exception e) { Log.Write("HoleSyncRead", e); continue; }
                foreach (var entry in holes) {
                    var link = HoleLinkStore.Read(entry.Hole);
                    if (link == null) continue;
                    report.Items.Add(Check(idx, node, entry, link, refCache));
                }
            }
            return report;
        }

        static LinkedHole Check(OccurrenceIndex idx, OccurrenceNode partNode, HoleFeatures.Entry entry, HoleLink link,
                                Dictionary<string, List<HoleFeatures.Entry>> refCache){
            var item = new LinkedHole { Part = partNode.Part, Hole = entry.Hole, Link = link, FeatureName = entry.Name };
            item.PartName = partNode.FileName.Length > 0 ? System.IO.Path.GetFileNameWithoutExtension(partNode.FileName) : partNode.Name;
            if (entry.Centers.Count == 0) { item.Problem = "读不到孔特征的孔心"; return item; }
            item.Current = entry.Centers[0];
            item.Target = idx.Find(link.TgtOcc, link.TgtDoc);
            if (item.Target == null || !string.Equals(item.Target.FullName, partNode.FullName, StringComparison.OrdinalIgnoreCase))
                item.Target = idx.Find(null, partNode.FileName) ?? item.Target;
            if (item.Target == null) { item.Problem = "打孔零件在当前装配里有多个实例，分不清是哪一个"; return item; }
            item.Reference = idx.Find(link.RefOcc, link.RefDoc);
            if (item.Reference == null) { item.Problem = "参考零件（" + (link.RefDoc.Length > 0 ? link.RefDoc : link.RefOcc) + "）不在当前装配里"; return item; }

            // 参考孔现在在哪：参考孔是孔特征的，按 SystemName 找回特征读它现在的孔心（孔在零件里被挪了也跟得上）；
            // 不是孔特征的（拉伸切除、导入的孔……）只能用打孔时记下的位置，跟得上的是"参考零件整体挪动"。
            V3 refCenter = link.RefCenter, refAxis = link.RefAxis;
            if (link.RefFeature.Length > 0 && item.Reference.Part != null) {
                List<HoleFeatures.Entry> refHoles;
                if (!refCache.TryGetValue(item.Reference.FullName, out refHoles)) {
                    try { refHoles = HoleFeatures.Read(item.Reference.Part); } catch { refHoles = new List<HoleFeatures.Entry>(); }
                    refCache[item.Reference.FullName] = refHoles;
                }
                var rf = HoleFeatures.BySystemName(refHoles, link.RefFeature);
                if (rf == null) { item.Problem = "参考孔特征已被删除"; return item; }
                if (rf.Centers.Count > 0) {
                    V3 best = rf.Centers[0]; double bd = (best - link.RefCenter).Length;
                    foreach (var c in rf.Centers) { double d = (c - link.RefCenter).Length; if (d < bd) { bd = d; best = c; } }
                    // 孔心是轮廓平面上的点；只要它和原来的轴线位置有变化，就以它为准
                    refCenter = best;
                }
                if (rf.HasNormal) refAxis = rf.Normal;
            }
            item.RefCenterNow = refCenter;

            V3 planePoint = item.Current, planeNormal = entry.Normal;
            if (!entry.HasNormal) { item.Problem = "读不到孔特征所在的平面"; return item; }
            V3 desired; string why;
            if (!HoleLink.Solve(item.Reference.Placement(link.RefMode), refCenter, refAxis,
                                item.Target.Placement(link.TgtMode), planePoint, planeNormal, out desired, out why)) {
                item.Problem = why; return item;
            }
            item.Desired = desired;
            item.OffsetMm = HoleLink.OffsetMm(item.Current, desired);
            return item;
        }

        // 把错位的孔挪回去。返回同一份报告（Moved / Failed / Notes 填好）。
        public static SyncReport Apply(SyncReport report){
            if (report == null) return report;
            TianGongCadSuite.Licensing.LicenseGate.Require();
            var byPart = new Dictionary<P.PartDocument, List<LinkedHole>>();
            foreach (var it in report.Items) {
                if (!it.NeedsMove) continue;
                List<LinkedHole> l;
                if (!byPart.TryGetValue(it.Part, out l)) { l = new List<LinkedHole>(); byPart[it.Part] = l; }
                l.Add(it);
            }
            foreach (var kv in byPart) {
                var part = kv.Key;
                var moved = new List<KeyValuePair<LinkedHole, double[]>>();   // 挪成功待验收的（带原 2D 孔心，失败时挪回）
                foreach (var it in kv.Value) {
                    try {
                        var prof = (P.Profile)it.Hole.Profile;
                        var h2 = prof.Holes2d.Item(1);
                        double ox, oy; h2.GetCenterPoint(out ox, out oy);
                        double nx, ny; prof.Convert3DCoordinate(it.Desired.X, it.Desired.Y, it.Desired.Z, out nx, out ny);
                        h2.SetCenterPoint(nx, ny);
                        prof.End(P.ProfileValidationType.igProfileClosed);
                        moved.Add(new KeyValuePair<LinkedHole, double[]>(it, new[]{ ox, oy }));
                    } catch (Exception e) {
                        report.Failed++;
                        report.Notes.Add(it.Label + "：挪孔失败（" + AutoHoleWriter.Friendly(e) + "）");
                        Log.Write("HoleSyncMove", e);
                    }
                }
                if (moved.Count == 0) continue;
                P.Model model = null;
                try { model = part.Models.Item(1); model.Recompute(); } catch (Exception e) { Log.Write("HoleSyncRecompute", e); }
                bool revertedAny = false;
                foreach (var kvm in moved) {
                    var it = kvm.Key;
                    object desc = null; bool ok = false;
                    try { ok = (int)it.Hole.GetStatusEx(out desc) == (int)P.FeatureStatusConstants.igFeatureOK; } catch { }
                    if (ok) {
                        report.Moved++;
                        it.Current = it.Desired; it.OffsetMm = 0;   // 报告就地更新：调用方不用为了刷新界面再把整个装配扫一遍
                        it.Link.TgtCenter = it.Desired;
                        it.Link.RefCenter = it.RefCenterNow;
                        try { HoleLinkStore.Write(it.Hole, it.Link); } catch (Exception e) { Log.Write("HoleSyncWriteLink", e); }
                        continue;
                    }
                    // 新位置建不出孔（多半是孔心挪到了材料外、或撞上了别的孔）：挪回原位
                    report.Failed++;
                    report.Notes.Add(it.Label + "：新位置建不出孔（偏了 " + it.OffsetMm.ToString("0.##", CultureInfo.InvariantCulture)
                                     + "mm，可能挪出了打孔面或撞上别的孔），已保留原位");
                    try {
                        var prof = (P.Profile)it.Hole.Profile;
                        prof.Holes2d.Item(1).SetCenterPoint(kvm.Value[0], kvm.Value[1]);
                        prof.End(P.ProfileValidationType.igProfileClosed);
                        revertedAny = true;
                    } catch (Exception e) { Log.Write("HoleSyncRevert", e); }
                }
                if (revertedAny && model != null) { try { model.Recompute(); } catch (Exception e) { Log.Write("HoleSyncRecompute2", e); } }
                try { HoleScanCad.Invalidate(part); } catch { }
            }
            return report;
        }
    }

    // 打孔时生成关联记录。一批孔开打前 Begin 一次（建实例索引、缓存参考零件的孔特征表），
    // 然后每个孔 Make 一条；认不出实例的孔返回 null 并说明原因 —— 孔照打，只是不关联。
    public static class HoleLinkFactory {
        public sealed class Session {
            public A.AssemblyDocument Assembly;
            public OccurrenceIndex Index;
            public readonly Dictionary<string, List<HoleFeatures.Entry>> RefHoles =
                new Dictionary<string, List<HoleFeatures.Entry>>(StringComparer.OrdinalIgnoreCase);
        }

        // 实例索引要遍历整个装配（会把所有零件加载一遍，大装配很慢），所以**用到时才建**：
        // 0.7.8 起孔优先走天工原生关联，只有原生关联挂不上的孔才需要这份索引。
        public static Session Begin(A.AssemblyDocument asm){
            var s = new Session();
            s.Assembly = asm;
            return s;
        }

        public static HoleLink Make(Session s, ScannedHole r, TargetFace t, out string why){
            why = "";
            if (s != null && s.Index == null && s.Assembly != null) {
                try { s.Index = OccurrenceIndex.Build(s.Assembly); } catch (Exception e) { Log.Write("HoleLinkBegin", e); s.Index = new OccurrenceIndex(); }
            }
            if (s == null || s.Index == null) { why = "没有装配实例索引"; return null; }
            var refPart = r == null ? null : r.Owner as P.PartDocument;
            if (refPart == null || r.Placement == null) { why = "参考孔读不到所在零件"; return null; }
            if (t == null || t.Part == null || t.Placement == null) { why = "打孔面读不到所在零件"; return null; }
            string rmode, tmode;
            var refNode = s.Index.Locate(refPart, r.Placement, out rmode);
            if (refNode == null) { why = "认不出参考孔在装配里是哪个零件实例"; return null; }
            var tgtNode = s.Index.Locate(t.Part, t.Placement, out tmode);
            if (tgtNode == null) { why = "认不出打孔面在装配里是哪个零件实例"; return null; }
            V3 rc, ra;
            try { rc = r.Placement.InversePoint(r.Center); ra = r.Placement.InverseNormal(r.Axis).Unit(); }
            catch (Exception e) { why = "参考孔坐标换算失败：" + e.Message; return null; }
            var link = new HoleLink {
                RefOcc = refNode.Path, RefDoc = refNode.FileName, RefMode = rmode,
                RefCenter = rc, RefAxis = ra, RefDiameterMm = r.DiameterMm,
                TgtOcc = tgtNode.Path, TgtDoc = tgtNode.FileName, TgtMode = tmode
            };
            // 参考孔是不是某个孔特征打出来的：是的话记下特征的 SystemName，参考孔在零件里被挪了也能跟上
            try {
                List<HoleFeatures.Entry> holes;
                string key = refNode.FullName.Length > 0 ? refNode.FullName : refNode.Path;
                if (!s.RefHoles.TryGetValue(key, out holes)) { holes = HoleFeatures.Read(refPart); s.RefHoles[key] = holes; }
                var f = HoleFeatures.OnAxis(holes, rc, ra);
                if (f != null) link.RefFeature = f.SystemName;
            } catch (Exception e) { Log.Write("HoleLinkRefFeature", e); }
            return link;
        }
    }
}
