using System;
using System.Collections.Generic;
using A=SolidEdgeAssembly;
using F=SolidEdgeFramework;
using G=SolidEdgeGeometry;
using P=SolidEdgePart;
using S=SolidEdgeFrameworkSupport;

namespace TianGongCadSuite {
    public static partial class NativeHoleLink {
        const int CmdEditInPlace = 57872;     // AssemblyEditActivateembeddedorlinkedobject（= 双击零件原位编辑）
        const int CmdCloseAndReturn = 40013;  // 关闭并返回
        static object M = Type.Missing;

        // ---------------- 认实例 ----------------
        // 选择引用（Reference / TopologyReference）+ 零件文档 + 零件局部→顶层的变换 → 实例链。
        // 先用引用自己给的顶层实例（不用遍历整个装配 —— 遍历会把所有零件加载一遍，就是"点命令卡很久"那个问题）；
        // 引用给不出来时只在顶层实例里按文件名 + 矩阵找。
        public static OccChain ChainOf(A.AssemblyDocument top, object pick, object part, Transform placement, out string why){   // part：零件或钣金件文档（0.10.2 钣金件的孔也能当参考孔挂关联）
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
            public string Side = "";                // 打孔面在零件里的方位（链接组名用，见 HoleNaming.SideOf）
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
    }
}
