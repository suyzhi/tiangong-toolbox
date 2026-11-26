using System;
using System.Collections.Generic;
using A=SolidEdgeAssembly;
using F=SolidEdgeFramework;
using G=SolidEdgeGeometry;
using P=SolidEdgePart;
using S=SolidEdgeFrameworkSupport;

namespace TianGongCadSuite {
    public static partial class NativeHoleLink {
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
                        var chain = ChainOf(top, h.Pick, h.Owner, h.Placement, out why);
                        if (chain == null) { Note("参考孔：" + why); refs[key] = null; refWhy[key] = "参考零件「" + SafeName(h.Owner) + "」：" + why; }
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
                s.Side = SideOf(t);
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

            // 打孔面的方位（外法向换到零件坐标；面片读不到朝向时按几何法向）
            static string SideOf(TargetFace t){
                try {
                    var n = t.Placement.InverseNormal(t.Plane.Normal).Unit();
                    bool rev = false; try { rev = t.Face.IsParamReversed; } catch { }
                    return HoleNaming.SideOf(rev ? n * -1.0 : n);
                } catch { return ""; }
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
                    try { GroupLinks(s.Part, used, requests, result, undo, s.Side); }
                    catch (Exception e) {
                        Log.Write("NativeLinkGroup", e);
                        string n = "孔位链接没能和孔编成一组（" + AutoHoleWriter.Friendly(e) + "）：孔和关联都在，删孔时请把特征树里的「孔位链接-…」一起删掉";
                        if (!result.Notes.Contains(n)) result.Notes.Add(n);
                    }
                }
                Leave(app, s);
            }

            void GroupLinks(P.PartDocument part, List<P.InterpartConstruction> copiesUsed, IList<AutoHoleWriter.HoleRequest> requests, DrillResult result, HoleUndo undo, string side){
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
                var g = HoleGrouping.MakeLinkGroup(model, members, HoleNaming.LinkGroup(specs, refNames, side), taken);
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
            // 带上全部 16 项变换矩阵（含旋转/镜像分量）：同一参考零件在同原点处绕轴旋转多个实例时不能撞键共用引用
            var sb = new System.Text.StringBuilder(doc.Length + face.Length + 128);
            sb.Append(doc).Append('|').Append(face).Append('|');
            for (int i = 0; i < 16; i++) {
                if (i > 0) sb.Append(',');
                double v = Math.Round(m[i], 6);
                if (Math.Abs(v) < 1e-12) v = 0.0;
                sb.Append(v.ToString("0.######", ci));
            }
            return sb.ToString();
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
