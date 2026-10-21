using System;
using System.Collections.Generic;
using A=SolidEdgeAssembly;
using F=SolidEdgeFramework;
using G=SolidEdgeGeometry;
using P=SolidEdgePart;
using S=SolidEdgeFrameworkSupport;

namespace TianGongCadSuite {
    // 天工原生跨零件关联（0.7.8）：配做孔挂在参考孔上，参考零件一挪，天工自己把孔带过去 —— 特征树里有锁链，不靠插件同步。
    //
    // 真机走通的唯一一条路（tools/hole-feature-probe/AsmLinkProbe*.cs，2026-10-20，记录见 AUTO-HOLE.md 0.7.8 一节）：
    //   1) 原位编辑打孔零件：选中实例 + StartCommand(57872 激活嵌入对象) = 在装配里双击零件；
    //      零件在子装配里就一级一级进（先进子装配、再在里面进零件），直接选深层实例进不去。
    //   2) 在打孔零件里建"零件间复制"（Constructions.InterpartConstructions.Add），来源 = 参考孔所在的**面**的装配引用
    //      （平面或孔壁圆柱面都行；引用一条边会建失败）。子装配里的参考零件：先在子装配里 CreateReference，再到上一级包一层。
    //      复制出来的圆边就在打孔零件的局部坐标里，零件 HasInterpartLinks=True。
    //   3) 孔草图里 IncludeEdge(复制来的圆边) + Hole2d 关键点约束到圆心 + 圆改构造线，再建孔。
    //   4) 参考零件挪了以后 UpdateAll（或天工里「更新所有链接」），孔自己跟过去；保存重开还在。
    // 直接 IncludeEdge 另一个零件的边（不管在不在原位）一律 0x80040224 —— 所以必须经过零件间复制这一步。
    //
    // 走不通的情况（不在顶层装配上下文、实例认不出、进不了原位、斜孔……）一律退回 0.7.3 的属性集关联 + 「同步孔位」，结果里说明。
    public sealed class NativeAnchor {
        public P.InterpartConstruction Copy;
        public CopyCircles Circles; // 同一个复制的锚点共用的圆边表（null = 每次现读）
        public V3 Centre;   // 要打的孔心（打孔零件局部）
        public V3 Normal;   // 打孔面法向（打孔零件局部）
        public string Label = "";

        // 复制来的圆边里"就是这个孔"的那条
        public G.Edge Edge(){
            if (Copy == null) return null;
            return (Circles ?? new CopyCircles(Copy)).Match(Centre, Normal);
        }
    }

    // 一个零件间复制上的全部圆边（0.7.10）：读一遍，这个复制上的所有孔共用。
    // 原来每个孔（Begin 里一次、Attach 里一次）都把复制的全部边从 CAD 读一遍 —— 20 个孔 × 几十条边 × 每条 3 次 COM 调用。
    // 对不上时重读一次再比：刚建的复制偶尔还没算完、圆边缺一条（NativeLinkE2E 实测）；打完孔后旧的边对象也可能失效（Attach 包含失败时 Reset）。
    public sealed class CopyCircles {
        public readonly P.InterpartConstruction Copy;
        List<G.Edge> edges; List<NativeLinkMath.Circle> circles;
        public CopyCircles(P.InterpartConstruction copy){ Copy = copy; }
        public void Reset(){ edges = null; circles = null; }

        public G.Edge Match(V3 centre, V3 normal){
            bool justRead = edges == null;
            if (justRead) Read();
            double off;
            int i = NativeLinkMath.Match(circles, centre, normal, out off);
            if (i >= 0 || justRead) return i < 0 ? null : edges[i];
            Reset(); Read();
            i = NativeLinkMath.Match(circles, centre, normal, out off);
            return i < 0 ? null : edges[i];
        }

        void Read(){
            edges = new List<G.Edge>(); circles = new List<NativeLinkMath.Circle>();
            object all = null;
            try { all = Copy.get_Edges(G.FeatureTopologyQueryTypeConstants.igQueryAll); } catch (Exception e) { Log.Write("NativeAnchorEdges", e); return; }
            if (all == null) return;
            foreach (object o in (System.Collections.IEnumerable)all) {
                var e = o as G.Edge; if (e == null) continue;
                G.Circle c = null; try { c = e.Geometry as G.Circle; } catch { }
                if (c == null) continue;
                Array cc = new double[3], ax = new double[3]; double r = 0;
                try { c.GetCircleData(ref cc, ref ax, out r); } catch { continue; }
                edges.Add(e); circles.Add(new NativeLinkMath.Circle(V3.From(cc), V3.From(ax), r));
            }
        }
    }

    // 实例链：顶层装配里的实例 → 子装配文档里的实例 → … → 零件实例（每一项都是所在装配文档里的 Occurrence）
    public sealed class OccChain {
        public readonly List<A.Occurrence> Items = new List<A.Occurrence>();
        public string Key = "";
        public A.Occurrence Leaf { get { return Items.Count == 0 ? null : Items[Items.Count - 1]; } }
    }

    public static class NativeHoleLink {
        const int CmdEditInPlace = 57872;     // AssemblyEditActivateembeddedorlinkedobject（= 双击零件原位编辑）
        const int CmdCloseAndReturn = 40013;  // 关闭并返回
        static object M = Type.Missing;

        // ---------------- 认实例 ----------------
        // 选择引用（Reference / TopologyReference）+ 零件文档 + 零件局部→顶层的变换 → 实例链。
        // 先用引用自己给的顶层实例（不用遍历整个装配 —— 遍历会把所有零件加载一遍，就是"点命令卡很久"那个问题）；
        // 引用给不出来时只在顶层实例里按文件名 + 矩阵找。
        public static OccChain ChainOf(A.AssemblyDocument top, object pick, P.PartDocument part, Transform placement, out string why){
            why = "";
            if (top == null || part == null || placement == null) { why = "读不到零件或它在装配里的位置"; return null; }
            string full = FullName(part);
            A.Occurrence topOcc = null;
            if (pick != null) {
                try {
                    object o = null; int nSub = 0, nBound = 0; Array bound = new object[0];
                    var r = pick as F.Reference;
                    if (r != null) r.GetOccurrencesInPath(out o, out nSub, out nBound, ref bound);
                    else ((dynamic)pick).GetOccurrencesInPath(out o, out nSub, out nBound, ref bound);
                    topOcc = o as A.Occurrence;
                } catch (Exception e) { Log.Write("NativeChainPath", e.Message); }
            }
            var candidates = new List<A.Occurrence>();
            if (topOcc != null) candidates.Add(topOcc);
            else {
                int n = 0; try { n = top.Occurrences.Count; } catch { }
                for (int i = 1; i <= n; i++) {
                    A.Occurrence occ = null; try { occ = top.Occurrences.Item(i); } catch { }
                    if (occ == null) continue;
                    string fn = ""; try { fn = occ.OccurrenceFileName ?? ""; } catch { }
                    if (string.Equals(fn, full, StringComparison.OrdinalIgnoreCase)) candidates.Add(occ);
                }
            }
            var found = new List<OccChain>();
            var byName = new List<OccChain>();
            foreach (var c in candidates) Walk(c, null, new List<A.Occurrence>(), full, placement, found, byName, 0);
            if (found.Count == 1) return Finish(found[0]);
            if (found.Count == 0 && byName.Count == 1) return Finish(byName[0]);
            why = found.Count > 1 || byName.Count > 1 ? "这个零件在装配里有好几个实例，认不准是哪一个" : "认不出这个零件是装配里的哪个实例";
            return null;
        }
        static OccChain Finish(OccChain c){
            var parts = new List<string>();
            foreach (var o in c.Items) { try { parts.Add(o.Name); } catch { parts.Add("?"); } }
            c.Key = string.Join("/", parts.ToArray());
            return c;
        }
        static void Walk(A.Occurrence occ, Transform parent, List<A.Occurrence> path, string full, Transform want, List<OccChain> found, List<OccChain> byName, int depth){
            if (occ == null || depth > 8) return;
            Transform here;
            try { Array m = new double[16]; occ.GetMatrix(ref m); here = NativeLinkMath.Compose(parent, new Transform(m)); } catch { return; }
            var chain = new List<A.Occurrence>(path); chain.Add(occ);
            bool sub = false; try { sub = occ.Subassembly; } catch { }
            if (!sub) {
                string fn = ""; try { fn = occ.OccurrenceFileName ?? ""; } catch { }
                if (!string.Equals(fn, full, StringComparison.OrdinalIgnoreCase)) return;
                var c = new OccChain(); c.Items.AddRange(chain);
                byName.Add(c);
                if (NativeLinkMath.Same(here, want)) found.Add(c);
                return;
            }
            A.AssemblyDocument doc = null;
            try { doc = occ.OccurrenceDocument as A.AssemblyDocument; } catch { }
            if (doc == null) return;
            int n = 0; try { n = doc.Occurrences.Count; } catch { }
            for (int i = 1; i <= n; i++) {
                A.Occurrence child = null; try { child = doc.Occurrences.Item(i); } catch { }
                Walk(child, here, chain, full, want, found, byName, depth + 1);
            }
        }

        // 实例链 + 零件里的几何（面）→ 顶层装配的引用。子装配一级一级往外包。
        public static object ReferenceTo(A.AssemblyDocument top, OccChain chain, object entity){
            object r = entity;
            for (int i = chain.Items.Count - 1; i >= 0; i--) {
                var parent = i == 0 ? top : (A.AssemblyDocument)chain.Items[i - 1].OccurrenceDocument;
                r = parent.CreateReference(chain.Items[i], r);
            }
            return r;
        }

        // ---------------- 原位编辑 ----------------
        public sealed class InPlace {
            public int Levels;                      // 这次自己进了几级（退出时退几级）
            public P.PartDocument Part;
            public readonly List<P.InterpartConstruction> Made = new List<P.InterpartConstruction>();   // 这次新建的零件间复制
        }

        // 等 CAD 把命令做完：插件在 CAD 的主线程里，命令要等消息循环跑起来才会执行，所以这里自己泵消息。
        static bool Pump(F.Application app, Func<bool> done, int timeoutMs){
            var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (true) {
                try { if (done()) return true; } catch { }
                if (DateTime.UtcNow > until) return false;
                try { System.Windows.Forms.Application.DoEvents(); } catch { }
                try { app.DoIdle(); } catch { }
                System.Threading.Thread.Sleep(30);
            }
        }
        static string FullName(object doc){ try { return (string)((dynamic)doc).FullName ?? ""; } catch { return ""; } }
        static bool IsActive(F.Application app, string full){
            try { return string.Equals(FullName(app.ActiveDocument), full, StringComparison.OrdinalIgnoreCase); } catch { return false; }
        }

        public static InPlace Enter(F.Application app, A.AssemblyDocument top, OccChain chain, out string why){
            why = "";
            var s = new InPlace();
            for (int i = 0; i < chain.Items.Count; i++) {
                var occ = chain.Items[i];
                string want = ""; try { want = occ.OccurrenceFileName ?? ""; } catch { }
                try { app.ActiveSelectSet.RemoveAll(); } catch { }
                try { occ.Select(true); } catch (Exception e) { why = "选不中实例（" + e.Message + "）"; break; }
                try { app.StartCommand((F.SolidEdgeCommandConstants)CmdEditInPlace); } catch (Exception e) { why = "进不了原位编辑（" + e.Message + "）"; break; }
                if (!Pump(app, () => IsActive(app, want), 15000)) { why = "等了 15 秒还没进入原位编辑"; break; }
                s.Levels++;
            }
            if (s.Levels < chain.Items.Count) { Leave(app, s); return null; }
            s.Part = app.ActiveDocument as P.PartDocument;
            if (s.Part == null) { why = "进入原位编辑后活动文档不是零件"; Leave(app, s); return null; }
            return s;
        }

        public static void Leave(F.Application app, InPlace s){
            if (s == null) return;
            for (; s.Levels > 0; s.Levels--) {
                object before = null; try { before = app.ActiveDocument; } catch { }
                try { app.StartCommand((F.SolidEdgeCommandConstants)CmdCloseAndReturn); } catch (Exception e) { Log.Write("NativeLeave", e); }
                Pump(app, () => !ReferenceEquals(app.ActiveDocument, before), 15000);
            }
            try { app.ActiveSelectSet.RemoveAll(); } catch { }
        }

        // ---------------- 一批孔：准备关联 ----------------
        // 打一张面之前调 Begin：进原位、建（或复用）零件间复制、给每个请求配上 Anchor；打完调 End 退出原位。
        // 任何一步不行就返回 null（或个别请求没 Anchor），那些孔照常打、退回属性集关联。
        public sealed class Session {
            readonly F.Application app; readonly A.AssemblyDocument top;
            readonly Dictionary<string, object> refs = new Dictionary<string, object>(StringComparer.Ordinal);
            readonly Dictionary<string, P.InterpartConstruction> copies = new Dictionary<string, P.InterpartConstruction>(StringComparer.OrdinalIgnoreCase);
            readonly List<CopyCircles> circleTables = new List<CopyCircles>();
            public readonly List<string> Notes = new List<string>();
            public int CopiesMade;
            public Session(F.Application application, A.AssemblyDocument assembly){ app = application; top = assembly; }

            void Note(string s){ if (!Notes.Contains(s)) Notes.Add(s); }

            public InPlace Begin(TargetFace t, IList<AutoHoleWriter.HoleRequest> requests, HoleUndo undo){
                if (t == null || t.Part == null || requests == null || requests.Count == 0) return null;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                // ① 参考孔 → 装配引用（进原位之前做：引用都对着顶层装配造）
                var need = new List<KeyValuePair<AutoHoleWriter.HoleRequest, string>>();
                foreach (var r in requests) {
                    var h = r.From;
                    if (h == null || h.SourceFace == null) continue;
                    if (!NativeLinkMath.Perpendicular(h.Axis, t.Plane.Normal)) { Note("参考孔和打孔面不垂直的孔没法用天工原生关联"); continue; }
                    var refPart = h.Owner as P.PartDocument;
                    if (refPart != null && string.Equals(FullName(refPart), FullName(t.Part), StringComparison.OrdinalIgnoreCase)) continue;   // 同一个零件，不用关联
                    string key = SourceKey(h);
                    if (key.Length == 0) continue;
                    if (!refs.ContainsKey(key)) {
                        string why;
                        var chain = ChainOf(top, h.Pick, refPart, h.Placement, out why);
                        if (chain == null) { Note("参考孔：" + why); refs[key] = null; }
                        else {
                            try { refs[key] = ReferenceTo(top, chain, h.SourceFace); }
                            catch (Exception e) { Log.Write("NativeRef", e); Note("参考孔的装配引用建不出来（" + e.Message + "）"); refs[key] = null; }
                        }
                    }
                    if (refs[key] != null) need.Add(new KeyValuePair<AutoHoleWriter.HoleRequest, string>(r, key));
                }
                if (need.Count == 0) return null;
                long refsMs = sw.ElapsedMilliseconds;

                // ② 进原位编辑打孔零件（已经在原位编辑它了就不用进）
                InPlace s = null;
                string tgtFull = FullName(t.Part);
                if (IsActive(app, tgtFull)) {
                    s = new InPlace { Levels = 0, Part = app.ActiveDocument as P.PartDocument };
                } else if (ReferenceEquals(app.ActiveDocument, top)) {
                    string why;
                    var chain = ChainOf(top, t.Pick, t.Part, t.Placement, out why);
                    if (chain == null) { Note("打孔面：" + why); return null; }
                    s = Enter(app, top, chain, out why);
                    if (s == null) { Note("没能原位编辑「" + SafeName(t.Part) + "」：" + why); return null; }
                } else {
                    Note("现在正在原位编辑别的零件，没法给「" + SafeName(t.Part) + "」建天工原生关联");
                    return null;
                }
                var part = s.Part ?? t.Part;
                long enterMs = sw.ElapsedMilliseconds;

                // ③ 零件间复制（同一个打孔零件 + 同一个来源只建一次）+ 每个孔配上圆边
                V3 normalLocal;
                try { normalLocal = t.Placement.InverseNormal(t.Plane.Normal).Unit(); } catch { Leave(app, s); return null; }
                int anchored = 0;
                foreach (var kv in need) {
                    var r = kv.Key;
                    bool made;
                    var copy = CopyFor(part, kv.Value, r.From, undo, out made);
                    if (copy == null) continue;
                    if (made) s.Made.Add(copy);
                    var anchor = new NativeAnchor { Copy = copy, Circles = TableFor(copy), Centre = t.Placement.InversePoint(r.Centre), Normal = normalLocal, Label = r.Source };
                    G.Edge e = null;
                    try { e = anchor.Edge(); } catch { }
                    // 子装配里两级原位编辑时，刚建的零件间复制偶尔还没算完、圆边缺一条（NativeLinkE2E 实测一次）：等 CAD 空闲一下再取
                    if (e == null) Pump(app, () => { try { e = anchor.Edge(); } catch { } return e != null; }, 2000);
                    if (e == null) { Note("零件间复制里找不到和孔心对得上的圆边（孔心要在参考孔轴线上）"); continue; }
                    r.Anchor = anchor; anchored++;
                }
                Log.Write("AutoHoleTiming", "关联准备：参考孔引用 " + refsMs + "ms，进原位 " + (enterMs - refsMs) + "ms，零件间复制 + 圆边 " + (sw.ElapsedMilliseconds - enterMs) + "ms");
                if (anchored == 0) { Leave(app, s); return null; }
                return s;
            }

            CopyCircles TableFor(P.InterpartConstruction copy){
                foreach (var t in circleTables) if (ReferenceEquals(t.Copy, copy)) return t;
                var n = new CopyCircles(copy);
                circleTables.Add(n);
                return n;
            }

            public void End(InPlace s){ End(s, null); }
            // requests：这一面的打孔请求（打完以后）。这次新建、却一个孔都没挂上的零件间复制在退出原位前删掉 ——
            // 孔全没打上时撤销按钮是灰的，留下的「孔位链接-…」就删不掉了（FenceHoleE2E 在用户围栏上留下过两个）。
            public void End(InPlace s, IList<AutoHoleWriter.HoleRequest> requests){
                if (s != null && requests != null) {
                    foreach (var c in s.Made) {
                        bool used = false;
                        foreach (var r in requests) if (r.NativeLinked && r.Anchor != null && SameFeature(r.Anchor.Copy, c)) { used = true; break; }
                        if (used) continue;
                        try { c.Delete(); } catch (Exception e) { Log.Write("NativeCopyUnused", e); continue; }
                        foreach (var kv in new List<KeyValuePair<string, P.InterpartConstruction>>(copies)) if (ReferenceEquals(kv.Value, c)) copies.Remove(kv.Key);
                        circleTables.RemoveAll(t => ReferenceEquals(t.Copy, c));
                        CopiesMade--;
                    }
                }
                Leave(app, s);
            }

            P.InterpartConstruction CopyFor(P.PartDocument part, string sourceKey, ReferenceHole h, HoleUndo undo, out bool made){
                made = false;
                string key = FullName(part) + "|" + sourceKey;
                P.InterpartConstruction c;
                if (copies.TryGetValue(key, out c)) {
                    try { object d; var st = c.get_Status(out d); if (st == P.FeatureStatusConstants.igFeatureOK) return c; } catch { }
                    copies.Remove(key);
                }
                object src = refs[sourceKey];
                try {
                    c = part.Constructions.InterpartConstructions.Add(src);
                    object d; var st = c.get_Status(out d);
                    if (st != P.FeatureStatusConstants.igFeatureOK) {
                        Note("零件间复制建出来状态是 " + st);
                        try { c.Delete(); } catch { }
                        return null;
                    }
                } catch (Exception e) {
                    Log.Write("NativeCopy", e);
                    Note("零件间复制建不出来（" + Friendly(e) + "）");
                    return null;
                }
                // 挪到上一个零件间复制后面：复制都排在孔前面，同规格的孔在特征树里保持连续，
                // 第二批孔才并得进已有的特征组（特征组只收连续的一段；真机 E2E 里没挪时第二批另起了一组）。
                // 挪到 RefPlane 前面会 E_FAIL，挪到某个特征之后可以（AsmLinkProbe6 R4）。
                try {
                    var all = part.Constructions.InterpartConstructions;
                    P.InterpartConstruction last = null;
                    for (int i = 1; i <= all.Count; i++) { var x = (P.InterpartConstruction)all.Item(i); if (!SameFeature(x, c)) last = x; }
                    if (last != null) c.Reorder(last, false);
                } catch (Exception e) { Log.Write("NativeCopyReorder", e.Message); }
                string refName = SafeName(h.Owner);
                try { c.Name = "孔位链接-" + System.IO.Path.GetFileNameWithoutExtension(refName); } catch { }
                try { c.Visible = false; } catch { }   // 复制来的面只当定位用，不显示
                copies[key] = c;
                CopiesMade++;
                made = true;
                if (undo != null) undo.Copy(part, c);
                return c;
            }
        }

        static string SourceKey(ReferenceHole h){
            string doc = FullName(h.Owner);
            if (doc.Length == 0 || h.Placement == null) return "";
            string face = "";
            try { face = ((dynamic)h.SourceFace).ID.ToString(); } catch { }
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var m = h.Placement.M;
            return doc + "|" + face + "|" + m[12].ToString("0.######", ci) + "," + m[13].ToString("0.######", ci) + "," + m[14].ToString("0.######", ci);
        }
        static bool SameFeature(P.InterpartConstruction a, P.InterpartConstruction b){
            try { return string.Equals(a.SystemName, b.SystemName, StringComparison.Ordinal); } catch { return ReferenceEquals(a, b); }
        }
        static string SafeName(object doc){ try { return (string)((dynamic)doc).Name ?? ""; } catch { return ""; } }
        static string Friendly(Exception e){ return e.Message + " 0x" + e.HResult.ToString("X8"); }

        // ---------------- 孔草图里挂上关联 ----------------
        // 在已经放好 Hole2d 的孔轮廓里：IncludeEdge(复制来的圆边) → Hole2d 关键点约束到圆心 → 圆改构造线。
        // 孔口圆边在面上被切成几段时（用户围栏里 40X80WXR1 上的 Φ5 孔，FenceHoleE2E 实测），包含进来的是圆弧不是圆：约束到弧心。
        // 挂不上时把包含进来的东西删干净 —— 留在轮廓里会让孔轮廓过不了闭合检查，孔都打不上。
        public static bool Attach(P.Profile profile, P.Hole2d hole2d, NativeAnchor anchor, out string why){
            why = "";
            if (profile == null || hole2d == null || anchor == null) { why = "参数不全"; return false; }
            var edge = anchor.Edge();
            if (edge == null) { why = "零件间复制里找不到这个孔的圆边"; return false; }
            object inc = null;
            int circles = Count(() => profile.Circles2d.Count), arcs = Count(() => profile.Arcs2d.Count), lines = Count(() => profile.Lines2d.Count);
            var iw = System.Diagnostics.Stopwatch.StartNew();
            try { profile.IncludeEdge(edge, out inc); }
            catch (Exception e) {
                // 圆边表里的边对象可能已经失效（复制重算过）：重读一遍再包含一次
                if (anchor.Circles == null) { why = "包含圆边失败（" + Friendly(e) + "）"; return false; }
                anchor.Circles.Reset();
                edge = anchor.Edge();
                if (edge == null) { why = "包含圆边失败（" + Friendly(e) + "）"; return false; }
                try { profile.IncludeEdge(edge, out inc); }
                catch (Exception e2) { why = "包含圆边失败（" + Friendly(e2) + "）"; return false; }
            }
            HoleCombine.IncludeMs += iw.ElapsedMilliseconds;
            object curve = null;
            int key = (int)SolidEdgeConstants.KeypointIndexConstants.igCircleCenter;
            try { if (profile.Circles2d.Count > circles) curve = profile.Circles2d.Item(profile.Circles2d.Count); } catch { }
            if (curve == null) {
                try { if (profile.Arcs2d.Count > arcs) { curve = profile.Arcs2d.Item(profile.Arcs2d.Count); key = (int)SolidEdgeConstants.KeypointIndexConstants.igArcCenter; } } catch { }
            }
            if (curve == null && inc != null) curve = inc;
            if (curve == null) { why = "包含进来的圆边读不到"; Clean(profile, inc, circles, arcs, lines); return false; }
            var kw = System.Diagnostics.Stopwatch.StartNew();
            try { ((S.Relations2d)profile.Relations2d).AddKeypoint(hole2d, 0, curve, key, M); HoleCombine.KeypointMs += kw.ElapsedMilliseconds; }
            catch (Exception e) {
                why = "孔心约束到圆心失败（" + Friendly(e) + "）";
                Clean(profile, inc, circles, arcs, lines);
                return false;
            }
            try { profile.ToggleConstruction(curve); } catch (Exception e) { Log.Write("NativeToggle", e); }
            return true;
        }
        static int Count(Func<int> f){ try { return f(); } catch { return 0; } }
        // 删掉 IncludeEdge 新加进来的圆、弧、线（倒着删）
        static void Clean(P.Profile profile, object inc, int circles, int arcs, int lines){
            try { for (int i = profile.Circles2d.Count; i > circles; i--) ((dynamic)profile.Circles2d.Item(i)).Delete(); } catch (Exception e) { Log.Write("NativeClean", e); }
            try { for (int i = profile.Arcs2d.Count; i > arcs; i--) ((dynamic)profile.Arcs2d.Item(i)).Delete(); } catch (Exception e) { Log.Write("NativeClean", e); }
            try { for (int i = profile.Lines2d.Count; i > lines; i--) ((dynamic)profile.Lines2d.Item(i)).Delete(); } catch (Exception e) { Log.Write("NativeClean", e); }
        }

        // 原生关联的孔怎么"同步"：参考零件挪了以后让天工更新链接（= 天工里「更新所有链接」）。
        public static void UpdateLinks(A.AssemblyDocument top){ top.UpdateAll(); }
    }
}
