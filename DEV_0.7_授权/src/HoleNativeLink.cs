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
    // 走不通的情况（链接属于别的装配、实例认不出、进不了原位、斜孔……）孔照样打（照样一个多孔特征），只是不挂关联，结果里说明原因。
    // （0.7.11 以前退回属性集关联 +「同步孔位」，只能单孔 + 编组，0.7.12 起不再用。）
    public sealed class NativeAnchor {
        public P.InterpartConstruction Copy;
        public CopyCircles Circles; // 同一个复制的锚点共用的圆边表（null = 每次现读）
        public V3 Centre;   // 要打的孔心（打孔零件局部）
        public V3 Normal;   // 打孔面法向（打孔零件局部）
        public string Label = "";
        public string Why = ""; // 打孔时孔心挂不上这条圆边的原因（Attach 返回的），结果里告诉用户

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
        static object SafeActive(F.Application app){ try { return app.ActiveDocument; } catch { return null; } }
        static bool IsActive(F.Application app, string full){
            try { return string.Equals(FullName(app.ActiveDocument), full, StringComparison.OrdinalIgnoreCase); } catch { return false; }
        }

        // 实例链上的子装配 / 零件在别的窗口（标签页）里作为顶层文档开着：返回它的文件名，没有返回 ""。
        // 0.7.12 真机（OwnerCtxE2E、WindowStateProbe）：在装配 X 里原位编辑、实例链经过的子装配 T 自己也开着窗口 ——
        // 天工把原位编辑"借"到 T 的窗口里（问它顶层是谁，答 T 不答 X），最后一级关闭并返回等不到，
        // 之后这个零件上打孔一律"对象已断开"（CO_E_OBJNOTCONNECTED / 0x80010108），关掉 X 也恢复不了。现场 08:40 那次
        // （进原位 85 秒、建复制 E_UNEXPECTED、退原位两次 15 秒超时）就是这个样子。所以进原位之前先查，有就不进、让用户先关掉那个窗口。
        // 原位编辑自己留下的子文档窗口不在 app.Documents 里（只列顶层打开的文档），不算。
        public static string OpenElsewhere(F.Application app, A.AssemblyDocument top, OccChain chain){
            if (app == null || chain == null) return "";
            var open = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try {
                foreach (object d in app.Documents) {
                    if (ReferenceEquals(d, top)) continue;
                    string fn = FullName(d);
                    if (fn.Length > 0 && !NativeLinkMath.SamePath(fn, FullName(top))) open.Add(System.IO.Path.GetFullPath(fn));
                }
            } catch (Exception e) { Log.Write("NativeOpenElsewhere", e.Message); return ""; }
            if (open.Count == 0) return "";
            foreach (var occ in chain.Items) {
                string fn = ""; try { fn = occ.OccurrenceFileName ?? ""; } catch { }
                if (fn.Length == 0) continue;
                try { fn = System.IO.Path.GetFullPath(fn); } catch { }
                if (open.Contains(fn)) return System.IO.Path.GetFileName(fn);
            }
            return "";
        }
        public static string OpenElsewhereMessage(string name){
            return "「" + name + "」在另一个窗口（标签页）里也开着 —— 天工不能同时在两个窗口里编辑它（硬来会把天工搅乱，之后这个零件怎么打孔都报\"对象已断开\"）。"
                 + "这一面没有打孔：请先关掉「" + name + "」的那个窗口，再点开始打孔";
        }
        // 打孔面所在零件的实例链是否被别的窗口占着（见 OpenElsewhere）；认不出实例链时返回 ""（交给后面照常处理）
        public static string TargetOpenElsewhere(F.Application app, A.AssemblyDocument top, TargetFace t){
            if (app == null || top == null || t == null || t.Part == null) return "";
            string why;
            var chain = ChainOf(top, t.Pick, t.Part, t.Placement, out why);
            return chain == null ? "" : OpenElsewhere(app, top, chain);
        }

        public static InPlace Enter(F.Application app, A.AssemblyDocument top, OccChain chain, out string why){
            why = "";
            string busy = OpenElsewhere(app, top, chain);
            if (busy.Length > 0) { why = OpenElsewhereMessage(busy); Log.Write("NativeEnter", why); return null; }
            var s = new InPlace();
            for (int i = 0; i < chain.Items.Count; i++) {
                var occ = chain.Items[i];
                string want = ""; try { want = occ.OccurrenceFileName ?? ""; } catch { }
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try { app.ActiveSelectSet.RemoveAll(); } catch { }
                try { occ.Select(true); } catch (Exception e) { why = "选不中实例（" + e.Message + "）"; break; }
                long selMs = sw.ElapsedMilliseconds;
                try { app.StartCommand((F.SolidEdgeCommandConstants)CmdEditInPlace); } catch (Exception e) { why = "进不了原位编辑（" + e.Message + "）"; break; }
                long cmdMs = sw.ElapsedMilliseconds;
                bool ok = Pump(app, () => IsActive(app, want), 15000);
                Log.Write("NativeEnter", "第 " + (i + 1) + "/" + chain.Items.Count + " 级 " + System.IO.Path.GetFileName(want) + "：选中 " + selMs + "ms，命令 " + (cmdMs - selMs)
                          + "ms，等待 " + (sw.ElapsedMilliseconds - cmdMs) + "ms，活动文档 " + System.IO.Path.GetFileName(FullName(SafeActive(app))) + (ok ? "" : "（超时）"));
                if (!ok) { why = "等了 15 秒还没进入原位编辑"; break; }
                s.Levels++;
            }
            if (s.Levels < chain.Items.Count) { Leave(app, s); return null; }
            s.Part = app.ActiveDocument as P.PartDocument;
            if (s.Part == null) { why = "进入原位编辑后活动文档不是零件"; Leave(app, s); return null; }
            return s;
        }

        // 原位编辑打孔面所在的零件（= 在装配里双击它）。已经在原位编辑它了就不再进（Levels=0，退出时也不退）；
        // 正在原位编辑别的零件时不动（那是用户自己的状态）。
        public static InPlace EnterTarget(F.Application app, A.AssemblyDocument top, TargetFace t, out string why){
            why = "";
            if (app == null || top == null || t == null || t.Part == null) { why = "读不到打孔零件"; return null; }
            if (IsActive(app, FullName(t.Part)) && InPlaceUnder(t.Part, top)) return new InPlace { Levels = 0, Part = app.ActiveDocument as P.PartDocument };
            object active = null; try { active = app.ActiveDocument; } catch { }
            if (!ReferenceEquals(active, top) && !NativeLinkMath.SamePath(FullName(active), FullName(top))) { why = "现在正在原位编辑别的零件，没法原位编辑「" + SafeName(t.Part) + "」"; return null; }
            var chain = ChainOf(top, t.Pick, t.Part, t.Placement, out why);
            if (chain == null) { why = "打孔面：" + why; return null; }
            var s = Enter(app, top, chain, out why);
            if (s == null) { why = "没能原位编辑「" + SafeName(t.Part) + "」：" + why; return null; }
            // 活动文档是这个零件还不够：现场 0.7.11 进原位"成功"后建零件间复制 E_UNEXPECTED（真机表：不在原位时就是这个错）。
            // 问天工它是不是真的在原位编辑、顶层是不是这个装配；不是就等一会儿再问，还不是就退出来报错。
            if (!Pump(app, () => InPlaceUnder(s.Part, top), 10000)) {
                object topDoc = null, occ = null;
                try { s.Part.GetTopDocumentAndSubOccurrenceOfIPADoc(out topDoc, out occ); } catch { }
                Log.Write("NativeEnter", "活动文档是「" + SafeName(s.Part) + "」但天工不认为它在「" + SafeName(top) + "」里原位编辑（InPlaceActivated=" + InPlaceFlag(s.Part)
                          + "，天工说顶层是「" + SafeName(topDoc) + "」）");
                why = "没能原位编辑「" + SafeName(t.Part) + "」：天工没有进入原位编辑状态";
                Leave(app, s);
                return null;
            }
            return s;
        }

        // 零件正在原位编辑、而且顶层文档就是 top
        static bool InPlaceUnder(P.PartDocument part, A.AssemblyDocument top){
            if (part == null || top == null) return false;
            try {
                if (!part.InPlaceActivated) return false;
                object topDoc = null, occ = null;
                part.GetTopDocumentAndSubOccurrenceOfIPADoc(out topDoc, out occ);
                return ReferenceEquals(topDoc, top) || NativeLinkMath.SamePath(FullName(topDoc), FullName(top));
            } catch (Exception e) { Log.Write("NativeInPlaceState", e.Message); return true; }   // 问不出来就不拦（老版本天工可能没有这两个接口）
        }
        static string InPlaceFlag(P.PartDocument part){ try { return part.InPlaceActivated.ToString(); } catch (Exception e) { return "?" + e.Message; } }

        // 零件有没有零件间链接（零件间复制、原位设计时包含/投影的别的零件的边……天工都算）
        public static bool HasLinks(P.PartDocument part){
            if (part == null) return false;
            bool has = false;
            try { part.HasInterpartLinks(ref has); } catch { return false; }
            return has;
        }

        public static void Leave(F.Application app, InPlace s){
            if (s == null) return;
            for (; s.Levels > 0; s.Levels--) {
                object before = null; try { before = app.ActiveDocument; } catch { }
                string beforeName = FullName(before);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try { app.StartCommand((F.SolidEdgeCommandConstants)CmdCloseAndReturn); } catch (Exception e) { Log.Write("NativeLeave", e); }
                long cmdMs = sw.ElapsedMilliseconds;
                bool ok = Pump(app, () => !ReferenceEquals(app.ActiveDocument, before), 15000);
                Log.Write("NativeLeave", "退一级 " + System.IO.Path.GetFileName(beforeName) + " → " + System.IO.Path.GetFileName(FullName(SafeActive(app)))
                          + "：命令 " + cmdMs + "ms，等待 " + (sw.ElapsedMilliseconds - cmdMs) + "ms" + (ok ? "" : "（超时）"));
            }
            try { app.ActiveSelectSet.RemoveAll(); } catch { }
            HideLeftovers(app);
        }

        // 原位编辑留下的子文档窗口藏起来（0.7.12）。
        // 真机（WindowStateProbe / WindowCloseProbe / WindowHideProbe）：每进一次原位编辑，天工给实例链上的子装配和零件各开一个窗口，
        // 关闭并返回以后**还在**，看得见（标签页）。用户关掉它 = 天工把那个文档重新加载，**刚打的孔没保存就没了**，
        // 插件手里的对象也全部"对象已断开"；零件窗口还会抢走活动窗口，看上去像天工卡住不刷新。
        // 藏起来（Visible=false）：改动都在、下次原位编辑照常而且复用这些窗口、活动窗口还是装配。
        // 只藏"文档不在 app.Documents 里"的窗口 —— 用户自己打开的文档都在 Documents 里，不碰。
        public static void HideLeftovers(F.Application app){
            if (app == null) return;
            object active = SafeActive(app);
            bool inPlace = false; try { inPlace = (bool)((dynamic)active).InPlaceActivated; } catch { }
            if (inPlace) return;   // 还在原位编辑里：等退干净再藏
            var open = new List<object>();
            try { foreach (object d in app.Documents) open.Add(d); } catch (Exception e) { Log.Write("NativeHide", e.Message); return; }
            var windows = new List<object>();
            try { foreach (object w in app.Windows) windows.Add(w); } catch (Exception e) { Log.Write("NativeHide", e.Message); return; }
            foreach (var w in windows) {
                try {
                    dynamic x = w;
                    if (!(bool)x.Visible) continue;
                    object doc = x.Parent;
                    if (doc == null) continue;
                    bool user = false;
                    foreach (var d in open) if (ReferenceEquals(d, doc) || NativeLinkMath.SamePath(FullName(d), FullName(doc))) { user = true; break; }
                    if (user) continue;
                    x.Visible = false;
                    Log.Write("NativeHide", "藏起原位编辑留下的窗口「" + (string)x.Caption + "」");
                } catch (Exception e) { Log.Write("NativeHide", e.Message); }
            }
        }

        // 一批打完（含出错）：活动文档必须回到开始打孔时的那个（一般是顶层装配）。
        // 现场 0.7.11：退出原位等了两次 15 秒都没等到活动文档变化，插件就当退完了 —— 天工停在零件原位编辑里、窗口不刷新，像卡死。
        // 这里逐级"关闭并返回"直到回去（最多 6 级），然后让天工刷新一次视图。
        public static void BackTo(F.Application app, object startDoc){
            if (app == null || startDoc == null) return;
            for (int k = 0; k < 8; k++) {
                object now = SafeActive(app);
                if (now == null || ReferenceEquals(now, startDoc) || NativeLinkMath.SamePath(FullName(now), FullName(startDoc))) break;
                bool inPlace = false; try { inPlace = (bool)((dynamic)now).InPlaceActivated; } catch { }
                if (inPlace) {
                    Log.Write("NativeBackTo", "还在原位编辑 " + System.IO.Path.GetFileName(FullName(now)) + "，关闭并返回（第 " + (k + 1) + " 次）");
                    try { app.StartCommand((F.SolidEdgeCommandConstants)CmdCloseAndReturn); } catch (Exception e) { Log.Write("NativeBackTo", e.Message); }
                } else {
                    // 别的窗口（在建链装配的窗口里打的孔）：切回开始时的窗口，不关任何东西
                    Log.Write("NativeBackTo", "活动窗口是 " + System.IO.Path.GetFileName(FullName(now)) + "，切回 " + System.IO.Path.GetFileName(FullName(startDoc)));
                    try { ((dynamic)startDoc).Activate(); } catch (Exception e) { Log.Write("NativeBackTo", e.Message); break; }
                }
                Pump(app, () => !ReferenceEquals(app.ActiveDocument, now), 20000);
            }
            try { app.ActiveSelectSet.RemoveAll(); } catch { }
            HideLeftovers(app);
            Refresh(app);
        }

        // 让天工把当前窗口重画一遍（打完孔窗口停在旧画面的保险）
        public static void Refresh(F.Application app){
            try { System.Windows.Forms.Application.DoEvents(); } catch { }
            try { app.DoIdle(); } catch { }
            try { ((dynamic)app.ActiveWindow).View.Update(); } catch (Exception e) { Log.Write("NativeRefresh", e.Message); }
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

            void Note(string s){ if (!Notes.Contains(s)) { Notes.Add(s); Log.Write("NativeNote", s); } }
            // 这一批请求里还没挂上的，都记上同一个原因（已经有原因的不覆盖：更具体的那条留着）
            static void Why(IEnumerable<AutoHoleWriter.HoleRequest> rs, string why){ foreach (var r in rs) if (r.Anchor == null && !r.LinkNotNeeded && r.LinkWhy.Length == 0) r.LinkWhy = why; }

            // 每个打孔请求要么挂上 Anchor，要么在 LinkWhy 里写明为什么挂不上（0.8.4：结果里逐条告诉用户，并据此改用插件关联）
            public InPlace Begin(TargetFace t, IList<AutoHoleWriter.HoleRequest> requests, HoleUndo undo){
                if (t == null || t.Part == null || requests == null || requests.Count == 0) return null;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                lastCopyWhy = "";
                // ⓪ 打孔零件已经有零件间链接、而且是在**别的装配**里建的：天工只允许一个零件的链接属于一个装配，
                //     在这里建零件间复制一律 E_FAIL（0.7.11 用户现场：YF25.07G01-L1-M30-02-2.par 的链接属于 YF25.07G01-L1-M30-02.asm，
                //     在下一级的 YF25.07底部…小车.asm 里打孔；LinkedDrillE2E 在副本上复现，换到建链的那个装配里就建得出来）。
                //     所以最先问清楚。孔照样打（原位编辑里、一个多孔特征），只是不挂关联，结果里说清楚去哪个装配打才能关联。
                //     （0.7.12 试过自动切到建链装配去建关联：建链装配一般是当前装配的上一级，那条实例链要经过当前装配自己 ——
                //      它开着自己的窗口，天工在别的窗口里原位编辑它会把两个窗口搅乱，之后这个零件上打孔全部"对象已断开"。OwnerCtxE2E 实测，放弃。）
                string otherAsm;
                if (LinksBelongElsewhere(t.Part, top, out otherAsm)) {
                    string on = System.IO.Path.GetFileName(otherAsm);
                    string why = ElsewhereWhy(SafeName(t.Part), on, SafeName(top));
                    Note(why);
                    Why(requests, why);
                    return null;
                }

                // ① 参考孔 → 装配引用（进原位之前做：引用都对着顶层装配造）
                var need = new List<KeyValuePair<AutoHoleWriter.HoleRequest, string>>();
                foreach (var r in requests) {
                    var h = r.From;
                    if (h == null) { r.LinkWhy = "读不到参考孔"; continue; }
                    var refPart = h.Owner as P.PartDocument;
                    if (refPart != null && string.Equals(FullName(refPart), FullName(t.Part), StringComparison.OrdinalIgnoreCase)) { r.LinkNotNeeded = true; continue; }   // 同一个零件，不用关联
                    if (h.SourceFace == null) { r.LinkWhy = "参考孔不是在模型上点的（配孔检查带进来的），没有可以链接的面"; continue; }
                    if (!NativeLinkMath.Perpendicular(h.Axis, t.Plane.Normal)) { r.LinkWhy = "参考孔轴线和打孔面不垂直（斜孔），天工关联只能做垂直的孔"; Note(r.LinkWhy); continue; }
                    string key = SourceKey(h);
                    if (key.Length == 0) { r.LinkWhy = "读不到参考孔所在零件在装配里的位置"; continue; }
                    if (!refs.ContainsKey(key)) {
                        string why;
                        var chain = ChainOf(top, h.Pick, refPart, h.Placement, out why);
                        if (chain == null) { Note("参考孔：" + why); refs[key] = null; refWhy[key] = "参考零件「" + SafeName(refPart) + "」：" + why; }
                        else {
                            try { refs[key] = ReferenceTo(top, chain, h.SourceFace); }
                            catch (Exception e) { Log.Write("NativeRef", e); Note("参考孔的装配引用建不出来（" + e.Message + "）"); refs[key] = null; refWhy[key] = "参考孔的装配引用建不出来（" + e.Message + "）"; }
                        }
                    }
                    if (refs[key] != null) need.Add(new KeyValuePair<AutoHoleWriter.HoleRequest, string>(r, key));
                    else { string w; r.LinkWhy = refWhy.TryGetValue(key, out w) ? w : "参考孔的装配引用建不出来"; }
                }
                if (need.Count == 0) return null;
                long refsMs = sw.ElapsedMilliseconds;

                // ② 进原位编辑打孔零件（已经在原位编辑它了就不用进）
                string ewhy;
                InPlace s = EnterTarget(app, top, t, out ewhy);
                if (s == null) { Note(ewhy); Why(requests, ewhy); return null; }
                var part = s.Part ?? t.Part;
                long enterMs = sw.ElapsedMilliseconds;

                // ③ 零件间复制（每张打孔面 + 每个来源各建一个：0.8.4 起孔位链接要和这张面的孔编进一个组，
                //    一个特征只能进一个组，所以不再跨面共用）+ 每个孔配上圆边
                V3 normalLocal;
                try { normalLocal = t.Placement.InverseNormal(t.Plane.Normal).Unit(); } catch { Leave(app, s); Why(requests, "打孔面坐标换算失败"); return null; }
                string faceKey = t.InstanceKey + "|" + Math.Max(1, t.Body) + "|" + Round(normalLocal) + "|" + Math.Round(t.Placement.InversePoint(t.Plane.Point).Dot(normalLocal), 7).ToString(System.Globalization.CultureInfo.InvariantCulture);
                int anchored = 0;
                foreach (var kv in need) {
                    var r = kv.Key;
                    bool made;
                    var copy = CopyFor(part, faceKey, kv.Value, r.From, undo, out made);
                    if (copy == null) { r.LinkWhy = "零件间复制（孔位链接）建不出来" + (lastCopyWhy.Length > 0 ? "（" + lastCopyWhy + "）" : ""); continue; }
                    if (made) s.Made.Add(copy);
                    var anchor = new NativeAnchor { Copy = copy, Circles = TableFor(copy), Centre = t.Placement.InversePoint(r.Centre), Normal = normalLocal, Label = r.Source };
                    G.Edge e = null;
                    try { e = anchor.Edge(); } catch { }
                    // 子装配里两级原位编辑时，刚建的零件间复制偶尔还没算完、圆边缺一条（NativeLinkE2E 实测一次）：等 CAD 空闲一下再取
                    if (e == null) Pump(app, () => { try { e = anchor.Edge(); } catch { } return e != null; }, 2000);
                    if (e == null) { r.LinkWhy = "孔位链接里找不到和孔心对得上的参考孔圆边（参考孔口被切成几段、或孔心不在参考孔轴线上）"; Note(r.LinkWhy); continue; }
                    r.Anchor = anchor; anchored++;
                }
                Log.Write("AutoHoleTiming", "关联准备：参考孔引用 " + refsMs + "ms，进原位 " + (enterMs - refsMs) + "ms，零件间复制 + 圆边 " + (sw.ElapsedMilliseconds - enterMs) + "ms");
                // 一个孔都没挂上也**不退出原位**（0.7.11）：孔就在原位编辑里打（不挂关联）。原来退回装配里打 ——
                // 用户现场 0.7.9 13:41 那批：零件间复制 E_FAIL → 退出原位 → 装配里打，17 个孔全部 0x80010108。
                // 没用上的零件间复制照样在 End 里删掉。
                return s;
            }

            readonly Dictionary<string, string> refWhy = new Dictionary<string, string>(StringComparer.Ordinal);
            string lastCopyWhy = "";
            static string Round(V3 v){
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                return Math.Round(v.X, 6).ToString(ci) + "," + Math.Round(v.Y, 6).ToString(ci) + "," + Math.Round(v.Z, 6).ToString(ci);
            }

            CopyCircles TableFor(P.InterpartConstruction copy){
                foreach (var t in circleTables) if (ReferenceEquals(t.Copy, copy)) return t;
                var n = new CopyCircles(copy);
                circleTables.Add(n);
                return n;
            }

            public void End(InPlace s){ End(s, null, null, null); }
            // requests：这一面的打孔请求（打完以后）。这次新建、却一个孔都没挂上的零件间复制在退出原位前删掉 ——
            // 孔全没打上时撤销按钮是灰的，留下的「孔位链接-…」就删不掉了（FenceHoleE2E 在用户围栏上留下过两个）。
            // result：这张面的打孔结果。有孔挂上了关联，就把用上的孔位链接 + 这张面新打的孔特征编成一个「孔位链接组」（0.8.4），
            // 用户删这个组 = 孔和链接一起删（见 HoleGrouping.MakeLinkGroup）。还在原位编辑里做（带链接的零件只有原位编辑里改得动）。
            public void End(InPlace s, IList<AutoHoleWriter.HoleRequest> requests, DrillResult result, HoleUndo undo){
                var used = new List<P.InterpartConstruction>();
                if (s != null && requests != null) {
                    foreach (var c in s.Made) {
                        bool isUsed = false;
                        foreach (var r in requests) if (r.NativeLinked && r.Anchor != null && SameFeature(r.Anchor.Copy, c)) { isUsed = true; break; }
                        if (isUsed) { used.Add(c); continue; }
                        try { c.Delete(); } catch (Exception e) { Log.Write("NativeCopyUnused", e); continue; }
                        foreach (var kv in new List<KeyValuePair<string, P.InterpartConstruction>>(copies)) if (ReferenceEquals(kv.Value, c)) copies.Remove(kv.Key);
                        circleTables.RemoveAll(t => ReferenceEquals(t.Copy, c));
                        CopiesMade--;
                    }
                }
                if (s != null && used.Count > 0 && result != null && result.TopFeatures.Count > 0) {
                    try { GroupLinks(s.Part, used, requests, result, undo); }
                    catch (Exception e) {
                        Log.Write("NativeLinkGroup", e);
                        string n = "孔位链接没能和孔编成一组（" + AutoHoleWriter.Friendly(e) + "）：孔和关联都在，删孔时请把特征树里的「孔位链接-…」一起删掉";
                        if (!result.Notes.Contains(n)) result.Notes.Add(n);
                    }
                }
                Leave(app, s);
            }

            void GroupLinks(P.PartDocument part, List<P.InterpartConstruction> copiesUsed, IList<AutoHoleWriter.HoleRequest> requests, DrillResult result, HoleUndo undo){
                var model = PartBodies.Model(part, 1);   // 特征组整个零件共用（真机：每个 Model 都看得到全部组）
                if (model == null) return;
                var members = new List<object>();
                foreach (var c in copiesUsed) members.Add(c);
                foreach (var f in result.TopFeatures) if (HoleGrouping.Sys(f).Length > 0) members.Add(f);
                var specs = new List<KeyValuePair<HoleSpec, int>>();
                var refNames = new List<string>();
                foreach (var r in requests) {
                    if (!r.Created) continue;
                    string k = HoleNaming.SpecKey(r.Spec);
                    int at = specs.FindIndex(kv => HoleNaming.SpecKey(kv.Key) == k);
                    if (at < 0) specs.Add(new KeyValuePair<HoleSpec, int>(r.Spec, 1)); else specs[at] = new KeyValuePair<HoleSpec, int>(specs[at].Key, specs[at].Value + 1);
                    if (r.NativeLinked && r.From != null) refNames.Add(System.IO.Path.GetFileNameWithoutExtension(SafeName(r.From.Owner)));
                }
                var taken = AutoHoleWriter.ExistingFeatureNames(model);
                var g = HoleGrouping.MakeLinkGroup(model, members, HoleNaming.LinkGroup(specs, refNames), taken);
                if (undo != null) undo.Group(part, g);
                string name = HoleGrouping.NameOf(g);
                Log.Write("NativeLinkGroup", SafeName(part) + "：「" + name + "」= " + copiesUsed.Count + " 个孔位链接 + " + (members.Count - copiesUsed.Count) + " 个孔特征");
                // 结果里"特征树里"那几行换成组名（孔特征的名字还在，只是收进了组里）
                result.Features.Clear();
                result.Features.Add(name + "（孔 + 孔位链接，删组即一起删）");
            }

            P.InterpartConstruction CopyFor(P.PartDocument part, string faceKey, string sourceKey, ReferenceHole h, HoleUndo undo, out bool made){
                made = false;
                string key = FullName(part) + "|" + faceKey + "|" + sourceKey;   // 每张面一个（见 Begin ③）；sourceKey 也是 refs 的键
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
                        lastCopyWhy = "建出来状态是 " + st;
                        Note("零件间复制建出来状态是 " + st);
                        try { c.Delete(); } catch { }
                        return null;
                    }
                } catch (Exception e) {
                    string ctx = ""; bool has = false;
                    try { part.HasInterpartLinks(ref has); if (has) part.GetInContextAssemblyNameForInterpartLinks(ref ctx); } catch { }
                    Log.Write("NativeCopy", "零件 " + SafeName(part) + " 有链接=" + has + " 建链装配=" + System.IO.Path.GetFileName(ctx ?? "") + " 顶层=" + SafeName(top)
                              + " 活动文档=" + System.IO.Path.GetFileName(FullName(SafeActive(app))) + "：" + Friendly(e));
                    lastCopyWhy = Friendly(e);
                    Note("零件间复制建不出来（" + Friendly(e) + "）");
                    return null;
                }
                // 0.8.4 起不再挪到前面的零件间复制后面：孔位链接要紧挨着这张面的孔，才能和孔编进一个组（特征组只收连续的一段；
                // 中间夹着的基准面、草图不算，HoleMultiProbe G1/G2）。新建的特征本来就排在最后，紧跟着就是这张面要打的孔。
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

        public static string ElsewhereWhy(string part, string owner, string top){
            return "「" + part + "」的零件间链接已经属于「" + owner + "」（天工规定一个零件的链接只能属于一个装配），在「" + top
                 + "」里挂不了天工关联 —— 要天工关联请打开「" + owner + "」在那里打孔";
        }
        // 点打孔面时的预判（不进原位、不建东西，两次接口调用）：零件的链接属于别的装配就返回那个装配的文件名，预览里提前说
        public static string LinkOwnerElsewhere(A.AssemblyDocument top, TargetFace t){
            if (top == null || t == null || t.Part == null) return "";
            string owner;
            return LinksBelongElsewhere(t.Part, top, out owner) ? System.IO.Path.GetFileName(owner) : "";
        }

        // 零件的零件间链接属于别的装配（不是当前顶层装配）。没有链接、读不出来时都当"不属于别处"，照常试原生关联。
        public static bool LinksBelongElsewhere(P.PartDocument part, A.AssemblyDocument top, out string owner){
            owner = "";
            if (part == null || top == null) return false;
            bool has = false;
            try { part.HasInterpartLinks(ref has); } catch (Exception e) { Log.Write("NativeLinkOwner", e.Message); return false; }
            if (!has) return false;
            string ctx = "";
            try { part.GetInContextAssemblyNameForInterpartLinks(ref ctx); } catch (Exception e) { Log.Write("NativeLinkOwner", e.Message); return false; }
            ctx = (ctx ?? "").Trim();
            if (ctx.Length == 0) return false;
            if (NativeLinkMath.SamePath(ctx, FullName(top))) return false;
            owner = ctx;
            return true;
        }

        static string SourceKey(ReferenceHole h){
            string doc = FullName(h.Owner);
            if (doc.Length == 0 || h.Placement == null) return "";
            string face = "";
            try { face = ((dynamic)h.SourceFace).ID.ToString(); } catch { }
            // 面 ID 只在一个实体里唯一（多实体零件跨实体重号，见 TargetFace.SameFace）：带上实体的 Tag，
            // 否则参考零件两个实体上同号的面会共用一个装配引用 / 孔位链接，复制的是错的那张面
            try { face += "@" + ((G.Body)((dynamic)h.SourceFace).Body).Tag; } catch { }
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
