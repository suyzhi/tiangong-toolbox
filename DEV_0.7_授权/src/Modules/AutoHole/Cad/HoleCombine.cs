using System;
using System.Collections.Generic;
using P=SolidEdgePart;

namespace TianGongCadSuite {
    // 多孔合一（0.7.9）：同一张面、同规格的一批孔合成**一个**孔特征 —— 和天工自己「孔」命令点多个孔建出来的一样。
    //
    // 天工的"多孔孔特征"不是 Hole，是 UserDefinedPattern：输入 = N 个单孔 Hole，草图集 1 个、里面 N 个轮廓（每个 1 个 Hole2d），
    // Model.Holes 里不再有它们，特征树里只有一项（真机：私有桌面上手点「孔」命令后回读）。
    // 用 UserDefinedPatterns.AddByProfiles 造（tools/hole-feature-probe/AsmLinkProbe10~15.cs，2026-10-20）：
    //   · 种子孔用第 1 个轮廓建，轮廓数组**包含种子孔自己的轮廓** → 一个 UDP、Holes=0、N 个孔都切对（H3、J1、J5、J6、J8）；
    //   · 轮廓里没有种子孔自己的 → 种子孔留在外面、实体读不出来（H1/H2/H6）；种子给 HoleData → E_FAIL（H4）；
    //   · 盲孔、螺纹（普通孔 + 螺纹数据）都带得过去（J2）；螺纹孔合成后先报 igFeatureWarned，重算以后 OK（J7）；
    //   · 能改名、挂属性、和别的 UDP 编进一个特征组（J4）；删 UDP 连它的输入孔一起删（J4）。
    // **原生关联的孔**（孔心挂在零件间复制的圆边上）：
    //   · 每个孔各自一个草图集、各挂各的圆边 → 只有种子孔那个行，其余输入孔 igFeatureWarned、参考零件挪了也不跟（L1/L2/L5）；
    //   · 一个轮廓里放全部 Hole2d、IncludeEdge 全部圆边、各自约束到圆心，End 拆成 N 个轮廓（同一个草图集）→ UDP OK、
    //     挪参考零件后**每个孔都跟过去**（L4）。这也正是天工自己的结构，所以不管挂没挂关联，一律这样建轮廓。
    // 做法（0.7.10）：**不先打单孔**，直接在打孔基准面上建一个共享轮廓（N 个孔心 + 圆边）→ 用拆出来的第 1 个轮廓打种子孔
    //   （这一侧打不成就删掉整套轮廓、换另一侧重建）→ AddByProfiles → 核对。CAD 只建 2 个特征（种子孔 + UDP）。
    //   0.7.9 是先逐个打 N 个单孔、再删掉、再合成：2N+2 次特征重算，20 个孔进程外实测 25 秒（DrillSpeedE2E）。
    // 任何一步不对：UDP（连种子孔）删掉、轮廓删掉，调用方（AutoHoleWriter.DrillRequests）逐个打 —— 每个孔位单独验、单独报错。
    // 注意：一张基准面上的孔全删光，CAD 连基准面一起删（J9：之后在它上面建轮廓 0x800401FD），所以删过东西（touched）调用方要重建基准面。
    public static class HoleCombine {
        // 直接建一个多孔孔特征（0.7.10）。
        // locals：孔心（零件局部）；anchors：原生关联的圆边（不挂关联的为 null）；data / depthMm：这批孔的规格；sides：种子孔试打的顺序。
        // inputs：合成后每个孔位对应的输入孔（和 locals 一一对应）。
        // touched：失败前在模型里建过又删过特征 —— CAD 可能把手里的模型/基准面对象作废了，调用方要重取再逐个打。
        public static P.UserDefinedPattern Build(P.PartDocument part, P.Model model, P.RefPlane plane, IList<V3> locals, IList<NativeAnchor> anchors,
                                                 P.HoleData data, double depthMm, IList<P.FeaturePropertyConstants> sides,
                                                 out List<P.Hole> inputs, out string why, out bool touched){
            inputs = null; why = ""; touched = false;
            int n = locals.Count;
            if (n < 2) { why = "不到 2 个孔"; return null; }
            var sw = System.Diagnostics.Stopwatch.StartNew(); Timing = "";
            foreach (var side in sides) {
                P.ProfileSet set; List<P.Profile> profiles;
                if (!SharedProfiles(part, plane, locals, anchors, out set, out profiles, out why)) return null;
                Mark(sw, "轮廓");
                // 种子孔：拆出来的第 1 个轮廓。只能试一次 —— 这一侧打不成时 CAD 把轮廓作废，删掉这套轮廓、换另一侧重建
                var seed = AutoHoleWriter.DrillOnProfile(part, model, profiles[0], data, depthMm, side);
                if (seed == null) { DeleteSet(set); why = "种子孔打不成"; continue; }
                AutoHoleWriter.Learned(plane, side);
                Mark(sw, "种子孔");
                P.UserDefinedPattern u = null;
                try {
                    Array arr = profiles.ToArray();
                    u = AutoHoleWriter.Attempt("合成孔特征", () => model.UserDefinedPatterns.AddByProfiles(n, arr, seed));
                    if (u == null) why = "CAD 没有返回孔特征";
                } catch (Exception e) { Log.Write("HoleCombineAdd", e); why = "合成失败（" + AutoHoleWriter.Friendly(e) + "）"; }
                Mark(sw, "合成");
                if (u != null) {
                    if (Verify(u, model, plane, locals, out inputs, out why)) { Mark(sw, "核对"); return u; }
                    // 螺纹孔合成后 CAD 先报 igFeatureWarned，重算一次就是 igFeatureOK、几何也对（真机 J7）—— 重算后再核对一次
                    try { AutoHoleWriter.Attempt("合成·重算", () => model.Recompute()); } catch (Exception e) { Log.Write("HoleCombineRecompute", e); }
                    string first = why;
                    if (Verify(u, model, plane, locals, out inputs, out why)) { Log.Write("HoleCombine", "重算后通过（重算前：" + first + "）"); return u; }
                }
                // 合不成：UDP（连种子孔）或单独的种子孔删掉、轮廓删掉，交给调用方逐个打
                touched = true;
                if (u != null) { try { u.Delete(); } catch (Exception e) { Log.Write("HoleCombineUndo", e); } }
                else { try { seed.Delete(); } catch (Exception e) { Log.Write("HoleCombineUndo", e); } }
                DeleteSet(set);
                inputs = null;
                return null;
            }
            return null;
        }

        // 计时（AutoHoleTiming 日志）
        public static string Timing = "";
        static void Mark(System.Diagnostics.Stopwatch sw, string what){ Timing += what + " " + sw.ElapsedMilliseconds + "ms "; sw.Restart(); }
        public static long AttachMs, IncludeMs, KeypointMs;

        // 共享轮廓：一个轮廓里放 N 个孔心（挂关联的各自约束到复制来的圆边圆心，真机 L4）→ End 拆成同一个草图集里的 N 个轮廓，按孔位排好。
        // 有一个孔挂不上关联就整个不合（返回 false）：调用方逐个打，挂不上的那个在 DrillOne 里退回属性集关联。
        static bool SharedProfiles(P.PartDocument part, P.RefPlane plane, IList<V3> locals, IList<NativeAnchor> anchors,
                                   out P.ProfileSet set, out List<P.Profile> profiles, out string why){
            set = null; profiles = null; why = "";
            int n = locals.Count;
            try {
                set = AutoHoleWriter.Attempt("合成·草图集", () => part.ProfileSets.Add());
                var ps = set;
                var prof = AutoHoleWriter.Attempt("合成·轮廓", () => ps.Profiles.Add(plane));
                for (int k = 0; k < n; k++) {
                    double x2 = 0, y2 = 0; var c = locals[k];
                    AutoHoleWriter.Attempt("合成·坐标", () => prof.Convert3DCoordinate(c.X, c.Y, c.Z, out x2, out y2));
                    var h2d = AutoHoleWriter.Attempt("合成·孔心", () => prof.Holes2d.Add(x2, y2));
                    if (anchors != null && anchors[k] != null) {
                        string awhy;
                        var aw = System.Diagnostics.Stopwatch.StartNew();
                        bool ok = NativeHoleLink.Attach(prof, h2d, anchors[k], out awhy);
                        AttachMs += aw.ElapsedMilliseconds;
                        // 0.7.12：一个孔挂不上关联不再让整批散成单孔（用户不要单孔 + 编组）—— 这个孔不挂关联、照样进多孔特征。
                        // Attach 失败时已经把包含进来的东西删干净；anchors[k] 置空，调用方据此统计哪些孔挂上了。
                        if (!ok) { Log.Write("HoleCombineAttach", "第 " + (k + 1) + " 个孔挂不上原生关联：" + awhy); anchors[k].Why = awhy; anchors[k] = null; }
                    }
                }
                Timing += "（挂关联 " + AttachMs + "ms：包含圆边 " + IncludeMs + "ms，约束 " + KeypointMs + "ms）";
                if (AutoHoleWriter.Attempt("合成·闭合", () => prof.End(P.ProfileValidationType.igProfileClosed)) != 0) throw new InvalidOperationException("轮廓没通过闭合检查");
                var split = new List<P.Profile>();
                int pc = ps.Profiles.Count;
                for (int j = 1; j <= pc; j++) split.Add(ps.Profiles.Item(j));
                if (split.Count != n) throw new InvalidOperationException("轮廓拆成了 " + split.Count + " 个，应为 " + n);
                profiles = OrderProfiles(split, locals);
                if (profiles == null) throw new InvalidOperationException("拆出来的轮廓和孔位对不上");
                return true;
            } catch (Exception e) {
                Log.Write("HoleCombineProfile", e);
                DeleteSet(set); set = null;
                why = "建轮廓失败（" + AutoHoleWriter.Friendly(e) + "）";
                return false;
            }
        }

        // 合成后核对：特征 OK、输入孔数 = N、每个孔位上都有孔（实体圆边读一遍，见 HoleSpots）；输入孔按孔位排好（和 locals 一一对应）
        static bool Verify(P.UserDefinedPattern u, P.Model model, P.RefPlane plane, IList<V3> locals, out List<P.Hole> inputs, out string why){
            inputs = null; why = "";
            try {
                object d;
                var st = u.GetStatusEx(out d);
                if ((int)st != (int)P.FeatureStatusConstants.igFeatureOK) { why = "合成后特征状态 " + st; return false; }
            } catch (Exception e) { why = "读不到合成后的特征状态（" + AutoHoleWriter.Friendly(e) + "）"; return false; }
            var all = Inputs(u);
            if (all.Count != locals.Count) { why = "合成后输入孔 " + all.Count + " 个，应为 " + locals.Count; return false; }
            var spots = AutoHoleWriter.ScanSpots(model);
            var normal = AutoHoleWriter.PlaneNormal(plane);
            for (int k = 0; k < locals.Count; k++) {
                double dia;
                if (!spots.At(locals[k], normal, out dia)) { why = "合成后第 " + (k + 1) + " 个孔位上没有孔"; return false; }
            }
            inputs = Order(all, locals);
            return true;
        }

        // UDP 的输入孔（多孔特征里的每个孔）
        internal static List<P.Hole> Inputs(P.UserDefinedPattern u){
            var list = new List<P.Hole>();
            if (u == null) return list;
            try {
                Array arr = new object[0];
                u.GetInputFeatures(ref arr);
                foreach (object o in arr) { var h = o as P.Hole; if (h != null) list.Add(h); }
            } catch (Exception e) { Log.Write("HoleCombineInputs", e); }
            return list;
        }
        internal static P.Hole SeedOf(P.UserDefinedPattern u){ var l = Inputs(u); return l.Count > 0 ? l[0] : null; }
        internal static int CountOf(P.UserDefinedPattern u){ try { return u.NumberInputFeatures; } catch { return Inputs(u).Count; } }

        // 输入孔按孔心对到 locals 上（读不出孔心就按原顺序）
        static List<P.Hole> Order(List<P.Hole> inputs, IList<V3> locals){
            var centres = new List<V3?>();
            foreach (var h in inputs) { V3 c; centres.Add(CentreOf(h, out c) ? (V3?)c : null); }   // 每个孔只读一次（原来两层循环里反复读）
            var pick = HoleSpots.Assign(centres, locals, HoleSpots.RadialTolM);
            var result = new P.Hole[locals.Count];
            var used = new bool[inputs.Count];
            for (int k = 0; k < locals.Count; k++) if (pick[k] >= 0) { result[k] = inputs[pick[k]]; used[pick[k]] = true; }
            for (int k = 0, i = 0; k < result.Length; k++) {
                if (result[k] != null) continue;
                while (i < inputs.Count && used[i]) i++;
                if (i < inputs.Count) { result[k] = inputs[i]; used[i] = true; }
            }
            return new List<P.Hole>(result);
        }
        static bool CentreOf(P.Hole h, out V3 c){
            c = new V3();
            try { return ProfileCentre((P.Profile)h.Profile, out c); } catch { return false; }
        }

        static void DeleteSet(P.ProfileSet set){
            if (set == null) return;
            try { set.Delete(); } catch (Exception e) { Log.Write("HoleCombineSet", e); }
        }
        // 拆出来的轮廓按孔心对到 locals 上（第 k 个轮廓 = 第 k 个孔位）；对不上返回 null
        static List<P.Profile> OrderProfiles(List<P.Profile> split, IList<V3> locals){
            var centres = new List<V3?>();
            foreach (var pr in split) { V3 c; centres.Add(ProfileCentre(pr, out c) ? (V3?)c : null); }
            var pick = HoleSpots.Assign(centres, locals, HoleSpots.RadialTolM);
            var result = new P.Profile[locals.Count];
            for (int k = 0; k < locals.Count; k++) { if (pick[k] < 0) return null; result[k] = split[pick[k]]; }
            return new List<P.Profile>(result);
        }
        static bool ProfileCentre(P.Profile prof, out V3 c){
            c = new V3();
            try {
                double x2, y2, x, y, z;
                prof.Holes2d.Item(1).GetCenterPoint(out x2, out y2);
                prof.Convert2DCoordinate(x2, y2, out x, out y, out z);
                c = new V3(x, y, z);
                return true;
            } catch { return false; }
        }
    }
}
