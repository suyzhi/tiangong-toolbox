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
    // 做法：孔照旧逐个打（每个孔位都验过、打孔方向试出来了、原生关联都挂上了）→ 同一张基准面上建一个共享轮廓（N 个孔心 + 圆边）
    //   → 删第 1 个单孔（别的单孔还在，基准面不会被连带删掉）→ 用拆出来的第 1 个轮廓按已知方向打种子孔 → 删其余单孔 → AddByProfiles。
    // 任何一步不对都退回单孔：UDP 删掉（种子孔跟着没了）→ 没了的孔按原位置重打。
    // 注意：一张基准面上的孔全删光，CAD 连基准面一起删（J9：之后在它上面建轮廓 0x800401FD），重打前要重建基准面（调用方 redrill 里做）。
    public static class HoleCombine {
        // holes：刚打好的同规格单孔；locals：孔心（零件局部）；anchors：原生关联的圆边（没挂的为 null）。
        // drillSeed(轮廓)：在拆出来的第 1 个轮廓上打种子孔（只能试一次：失败时 CAD 会把轮廓作废）。
        // redrill(k)：退回单孔时重打第 k 个孔，返回新孔（打不上为 null）。
        // after：最后每个孔位对应的孔（合成了 = UDP 的输入孔；没合成 = 单孔，重打过的是新对象）。
        public static P.UserDefinedPattern Run(P.PartDocument part, P.Model model, P.RefPlane plane, IList<P.Hole> holes, IList<V3> locals,
                                               IList<NativeAnchor> anchors, Func<P.Profile, P.Hole> drillSeed, Func<int, P.Hole> redrill,
                                               out List<P.Hole> after, out string why){
            after = new List<P.Hole>(holes); why = "";
            int n = holes.Count;
            if (n < 2) return null;

            // ① 共享轮廓：N 个孔心（挂关联的孔各自约束到圆边）→ End 拆成 N 个轮廓。删孔之前出错什么都没动
            P.ProfileSet set = null;
            List<P.Profile> profiles;
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
                        if (!NativeHoleLink.Attach(prof, h2d, anchors[k], out awhy)) throw new InvalidOperationException("第 " + (k + 1) + " 个孔挂不上原生关联：" + awhy);
                    }
                }
                if (AutoHoleWriter.Attempt("合成·闭合", () => prof.End(P.ProfileValidationType.igProfileClosed)) != 0) throw new InvalidOperationException("轮廓没通过闭合检查");
                var split = new List<P.Profile>();
                int pc = ps.Profiles.Count;
                for (int j = 1; j <= pc; j++) split.Add(ps.Profiles.Item(j));
                if (split.Count != n) throw new InvalidOperationException("轮廓拆成了 " + split.Count + " 个，应为 " + n);
                profiles = OrderProfiles(split, locals);
                if (profiles == null) throw new InvalidOperationException("拆出来的轮廓和孔位对不上");
            } catch (Exception e) {
                Log.Write("HoleCombineProfile", e);
                DeleteSet(set);
                why = "建轮廓失败（" + AutoHoleWriter.Friendly(e) + "）";
                return null;
            }

            // ② 删第 1 个单孔 → 在第 1 个轮廓上打种子孔（同一根轴线上已有孔时 CAD 会拒绝，所以先删）
            var gone = new List<int>();
            P.Hole seed = null;
            try { holes[0].Delete(); gone.Add(0); }
            catch (Exception e) { Log.Write("HoleCombineDelete", e); why = "删单孔失败（" + AutoHoleWriter.Friendly(e) + "）"; }
            if (why.Length == 0) {
                try { seed = drillSeed(profiles[0]); } catch (Exception e) { Log.Write("HoleCombineSeed", e); }
                if (seed == null) why = "种子孔没打成";
            }
            // ③ 删其余单孔
            for (int k = 1; k < n && why.Length == 0; k++) {
                try { holes[k].Delete(); gone.Add(k); }
                catch (Exception e) { Log.Write("HoleCombineDelete", e); why = "删单孔失败（" + AutoHoleWriter.Friendly(e) + "）"; }
            }

            // ④ 合成
            P.UserDefinedPattern u = null;
            if (why.Length == 0) {
                try {
                    Array arr = profiles.ToArray();
                    var s0 = seed;
                    u = AutoHoleWriter.Attempt("合成孔特征", () => model.UserDefinedPatterns.AddByProfiles(n, arr, s0));
                    if (u == null) why = "CAD 没有返回孔特征";
                } catch (Exception e) { Log.Write("HoleCombineAdd", e); why = "合成失败（" + AutoHoleWriter.Friendly(e) + "）"; }
            }
            List<P.Hole> inputs = null;
            if (u != null && Verify(u, part, plane, locals, out inputs, out why)) { after = inputs; return u; }
            // 螺纹孔合成后 CAD 先报 igFeatureWarned，重算一次就是 igFeatureOK、几何也对（真机 J7）—— 重算后再核对一次
            if (u != null) {
                try { AutoHoleWriter.Attempt("合成·重算", () => model.Recompute()); } catch (Exception e) { Log.Write("HoleCombineRecompute", e); }
                string first = why;
                if (Verify(u, part, plane, locals, out inputs, out why)) { Log.Write("HoleCombine", "重算后通过（重算前：" + first + "）"); after = inputs; return u; }
            }

            // ⑤ 退回单孔：UDP（连种子孔）或单独的种子孔删掉、共享轮廓删掉，删掉的单孔按原位置重打
            if (u != null) { try { u.Delete(); } catch (Exception e) { Log.Write("HoleCombineUndo", e); } }
            else if (seed != null) { try { seed.Delete(); } catch (Exception e) { Log.Write("HoleCombineUndo", e); } }
            DeleteSet(set);
            foreach (int k in gone) {
                P.Hole again = null;
                try { again = redrill(k); } catch (Exception e) { Log.Write("HoleCombineRedrill", e); }
                after[k] = again;
            }
            Log.Write("HoleCombine", "退回单孔：" + why + "（重打 " + gone.Count + " 个）");
            return null;
        }

        // 合成后核对：特征 OK、输入孔数 = N、每个孔位上都有孔；输入孔按孔位排好（和 locals 一一对应）
        static bool Verify(P.UserDefinedPattern u, P.PartDocument part, P.RefPlane plane, IList<V3> locals, out List<P.Hole> inputs, out string why){
            inputs = null; why = "";
            try {
                object d;
                var st = u.GetStatusEx(out d);
                if ((int)st != (int)P.FeatureStatusConstants.igFeatureOK) { why = "合成后特征状态 " + st; return false; }
            } catch (Exception e) { why = "读不到合成后的特征状态（" + AutoHoleWriter.Friendly(e) + "）"; return false; }
            var all = Inputs(u);
            if (all.Count != locals.Count) { why = "合成后输入孔 " + all.Count + " 个，应为 " + locals.Count; return false; }
            for (int k = 0; k < locals.Count; k++) {
                double dia;
                if (!AutoHoleWriter.ExistingHoleAt(plane, part, locals[k], out dia)) { why = "合成后第 " + (k + 1) + " 个孔位上没有孔"; return false; }
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
            var result = new P.Hole[locals.Count];
            var used = new bool[inputs.Count];
            for (int k = 0; k < locals.Count; k++) {
                int best = -1; double bestD = 0.0005;
                for (int i = 0; i < inputs.Count; i++) {
                    if (used[i]) continue;
                    V3 c;
                    if (!CentreOf(inputs[i], out c)) continue;
                    double dd = (c - locals[k]).Length;
                    if (dd < bestD) { bestD = dd; best = i; }
                }
                if (best >= 0) { result[k] = inputs[best]; used[best] = true; }
            }
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
            var result = new P.Profile[locals.Count];
            var used = new bool[split.Count];
            for (int k = 0; k < locals.Count; k++) {
                for (int j = 0; j < split.Count; j++) {
                    if (used[j]) continue;
                    V3 c;
                    if (!ProfileCentre(split[j], out c) || (c - locals[k]).Length > 0.0005) continue;
                    result[k] = split[j]; used[j] = true; break;
                }
                if (result[k] == null) return null;
            }
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
