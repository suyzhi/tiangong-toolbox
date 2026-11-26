using System;
using System.Collections.Generic;
using System.Globalization;
using A=SolidEdgeAssembly;
using F=SolidEdgeFramework;
using P=SolidEdgePart;

namespace TianGongCadSuite {
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
        // 天工原生关联（0.8.4 起也一起数）：装配里各零件的「孔位链接-…」个数、其中状态不正常的（参考零件删了 / 换了 / 链接断了）
        public int NativeLinks, NativeBroken;
        public readonly List<string> NativeBrokenLabels = new List<string>();
        // 给界面的一句话
        public string Summary {
            get {
                // 0.10.2：原生关联只数链接状态，看不出孔有没有跟上 —— 真机（LinkSyncE2E ⑤）在装配里挪了参考零件，孔实际错开 5.8mm，
                // 这里照样说"天工自己跟着走"，用户以为没事就存了。天工要「更新链接」（= 同步孔位）孔才过去，所以话里说清楚
                // 0.7.9 起保持关联走天工原生关联；原来这里只数插件关联记录，刚打完 6 个原生关联孔点「检查」说"还没有关联孔"，
                // 用户会以为关联没挂上。0.8.4 起两种都数、各说各的
                string native = NativeLinks == 0 ? "" : "天工关联的孔位链接 " + NativeLinks + " 个" + (NativeBroken > 0 ? "（" + NativeBroken + " 个断了：参考零件被删掉或换掉了）" : "（天工自己跟着参考孔走；参考零件刚挪过的话，要点「同步孔位」孔才更新过去）");
                if (Linked == 0)
                    return native.Length > 0 ? native + "；没有插件关联的孔。" : "没有关联孔。用「保持与参考孔关联」打的孔才会出现在这里。";
                string s = "插件关联的孔 " + Linked + " 个";
                if (Misplaced > 0) s += "，其中 " + Misplaced + " 个已和参考孔错位（点「同步孔位」挪回去）";
                else s += "，全部与参考孔同轴";
                if (Broken > 0) s += "；" + Broken + " 个找不到参考孔";
                return (native.Length > 0 ? native + "；" : "") + s + "。";
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
                CountNative(node, report);
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

        // 零件里插件建的孔位链接（零件间复制，名字「孔位链接-…」）：数一下，状态不正常的记下来
        static void CountNative(OccurrenceNode node, SyncReport report){
            P.InterpartConstructions all = null;
            try { all = node.Part.Constructions.InterpartConstructions; } catch { return; }
            int n = 0; try { n = all.Count; } catch { }
            for (int i = 1; i <= n; i++) {
                try {
                    var c = (P.InterpartConstruction)all.Item(i);
                    string name = c.Name ?? "";
                    if (!name.StartsWith("孔位链接-", StringComparison.Ordinal)) continue;
                    report.NativeLinks++;
                    object d; var st = c.get_Status(out d);
                    if (st == P.FeatureStatusConstants.igFeatureOK) continue;
                    report.NativeBroken++;
                    report.NativeBrokenLabels.Add(System.IO.Path.GetFileNameWithoutExtension(node.FileName) + " · " + name + "（" + st + "）");
                } catch (Exception e) { Log.Write("HoleSyncNative", e.Message); }
            }
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
                // SystemName 会跟着孔型变（改过孔型的参考孔按名字找不回来）：再按记下的轴线找一次
                if (rf == null) rf = HoleFeatures.OnAxis(refHoles, link.RefCenter, link.RefAxis);
                if (rf == null) { item.Problem = "参考孔找不到了（参考孔被删掉了，或者参考零件里的孔改过）"; return item; }
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

        // 重算零件的每个实体（0.8.5）：多实体零件上挪的孔可能在第 2 个以后的实体上，原来只重算第 1 个实体，
        // 那些实体上的孔几何不跟着动、状态也读不准（0.8.4 交接里记的遗留）
        static void RecomputeAll(P.PartDocument part){
            var models = new List<P.Model>();
            try { if (PartBodies.Multi(part)) models.AddRange(PartBodies.All(part)); else models.Add(part.Models.Item(1)); } catch (Exception e) { Log.Write("HoleSyncModels", e); }
            foreach (var m in models) { try { m.Recompute(); } catch (Exception e) { Log.Write("HoleSyncRecompute", e); } }
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
                try { model = part.Models.Item(1); } catch { }
                RecomputeAll(part);
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
                if (revertedAny && model != null) RecomputeAll(part);
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
            public readonly Dictionary<string, OccurrenceIndex> Chains = new Dictionary<string, OccurrenceIndex>(StringComparer.Ordinal);
            public readonly Dictionary<string, KeyValuePair<OccurrenceNode, string>> Located = new Dictionary<string, KeyValuePair<OccurrenceNode, string>>(StringComparer.Ordinal);
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
            if (s == null || s.Assembly == null) { why = "没有装配"; return null; }
            var refPart = r == null ? null : r.Owner as P.PartDocument;
            var refSheet = r == null ? null : r.Owner as P.SheetMetalDocument;
            object refDoc = (object)refPart ?? refSheet;
            if (refDoc == null || r.Placement == null) { why = "参考孔读不到所在零件"; return null; }
            if (t == null || t.Part == null || t.Placement == null) { why = "打孔面读不到所在零件"; return null; }
            // 只认这两个实例（0.8.4，见 OccurrenceIndex.ForChain）：不再为写一条关联记录把整个装配加载一遍
            string rmode, tmode, cwhy;
            var refNode = Locate(s, r.Pick, refDoc, r.Placement, out rmode, out cwhy);
            if (refNode == null) { why = "认不出参考孔在装配里是哪个零件实例" + (cwhy.Length > 0 ? "（" + cwhy + "）" : ""); return null; }
            var tgtNode = Locate(s, t.Pick, t.Part, t.Placement, out tmode, out cwhy);
            if (tgtNode == null) { why = "认不出打孔面在装配里是哪个零件实例" + (cwhy.Length > 0 ? "（" + cwhy + "）" : ""); return null; }
            V3 rc, ra;
            try { rc = r.Placement.InversePoint(r.Center); ra = r.Placement.InverseNormal(r.Axis).Unit(); }
            catch (Exception e) { why = "参考孔坐标换算失败：" + e.Message; return null; }
            var link = new HoleLink {
                RefOcc = refNode.Path, RefDoc = refNode.FileName, RefMode = rmode,
                RefCenter = rc, RefAxis = ra, RefDiameterMm = r.DiameterMm,
                TgtOcc = tgtNode.Path, TgtDoc = tgtNode.FileName, TgtMode = tmode
            };
            // 参考孔是不是某个孔特征打出来的（仅普通零件 .par 支持回读孔特征表；钣金件 .psm 仍可跟随实例整体移动）
            if (refPart != null) {
                try {
                    List<HoleFeatures.Entry> holes;
                    string key = refNode.FullName.Length > 0 ? refNode.FullName : refNode.Path;
                    if (!s.RefHoles.TryGetValue(key, out holes)) { holes = HoleFeatures.Read(refPart); s.RefHoles[key] = holes; }
                    var f = HoleFeatures.OnAxis(holes, rc, ra);
                    if (f != null) link.RefFeature = f.SystemName;
                } catch (Exception e) { Log.Write("HoleLinkRefFeature", e); }
            }
            return link;
        }

        static OccurrenceNode Locate(Session s, object pick, object doc, Transform placement, out string mode, out string why){
            mode = "R"; why = "";
            // 一批孔大多是同一个参考零件、同一个打孔零件：认过的实例直接用（ChainOf 要沿实例链读矩阵，几十个孔各认一遍白花时间）
            string full = DocFullName(doc);
            var sb = new System.Text.StringBuilder(full);
            foreach (var v in placement.M) sb.Append('|').Append(Math.Round(v, 7).ToString(CultureInfo.InvariantCulture));
            string memo = sb.ToString();
            KeyValuePair<OccurrenceNode, string> hit;
            if (s.Located.TryGetValue(memo, out hit)) { mode = hit.Value; if (hit.Key == null) why = "认不出这个零件实例"; return hit.Key; }
            var node = LocateFresh(s, pick, doc, placement, out mode, out why);
            s.Located[memo] = new KeyValuePair<OccurrenceNode, string>(node, mode);
            return node;
        }
        static string DocFullName(object doc){
            var part = doc as P.PartDocument;
            if (part != null) { try { return part.FullName ?? ""; } catch { return ""; } }
            var sheet = doc as P.SheetMetalDocument;
            if (sheet != null) { try { return sheet.FullName ?? ""; } catch { return ""; } }
            return "";
        }
        static OccurrenceNode LocateFresh(Session s, object pick, object doc, Transform placement, out string mode, out string why){
            mode = "R";
            var chain = NativeHoleLink.ChainOf(s.Assembly, pick, doc, placement, out why);
            if (chain == null) return null;
            string key = chain.Key;
            OccurrenceIndex idx;
            if (!s.Chains.TryGetValue(key, out idx)) { idx = OccurrenceIndex.ForChain(chain); s.Chains[key] = idx; }
            var node = idx.Locate(doc, placement, out mode);
            if (node == null) why = "实例链上的矩阵对不上";
            return node;
        }
    }
}
