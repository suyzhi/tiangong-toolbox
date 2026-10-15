using System;
using System.Collections.Generic;
using P=SolidEdgePart;
using S=SolidEdgeFrameworkSupport;

namespace TianGongCadSuite {
    // 批量排孔：「种子孔 + 阵列」。
    //
    // 原来一排 8 个孔 = 8 个孔特征，打完想把 M6 改成 M8 只能删了重打。现在打 1 个种子孔，
    // 其余用一个阵列特征（沿线 = 矩形阵列 1×N，圆周 = 圆周阵列）复制出来 —— 在天工里改种子孔，整排跟着变。
    // 真机实测（tools/hole-feature-probe/HoleEditProbe.cs，2026-10-14）：
    //   · AddByRectangular 的角度是弧度、从基准面参考方向绕法向逆时针量；AddByCircular 的角度同样，轴点给 3D 点；
    //   · 种子孔换孔型（HoleData + 补写孔径，见 HoleEditCad）后阵列跟着变，切除体积和理论值一致；
    //   · 阵列能改名、挂属性、和种子孔编成一个特征组；撤销时先删阵列再删种子孔；
    //   · 阵列建好以后天工接口改不了个数/间距（SetRectangularPatternData 等都是 0x80004021），要改就撤销重排。
    // 宁可退回逐个打孔，也不能打出位置不对的孔：
    //   · 有位置上本来就有孔（阵列要求每个位置都是空的）、孔心排不成等距直线/均布圆 → 直接逐个打；
    //   · 建完阵列逐个核对孔心，对不上就删掉阵列换个方向再试，还不行就删掉种子孔和阵列、逐个打。
    public static class HolePatternWriter {
        const double MmToM = 0.001;
        const int Rounds = 3;   // 种子孔和 DrillRequests 一样按轮重试（原位编辑时前两次可能被 CAD 拒绝）

        // centres：装配坐标的孔心（顺序 = 排布顺序，第一个是种子孔）；circleCentre：圆周均布的分度圆圆心（装配坐标），沿线时为 null
        public static DrillResult Drill(TargetFace target, HoleSpec spec, IList<V3> centres, V3? circleCentre){
            if (target == null) throw new ArgumentNullException("target");
            if (spec == null) throw new ArgumentNullException("spec");
            if (centres == null || centres.Count < 2) return Fallback(target, spec, centres, null);
            target = AutoHoleWriter.AnchorToActivePart(target);
            var part = target.Part;
            P.Model model = part.Models.Count >= 1 ? part.Models.Item(1) : null;
            if (model == null) throw new InvalidOperationException("目标零件没有可用的实体模型。");
            P.RefPlane plane = AutoHoleWriter.FindOrCreatePlane(part, target);
            if (plane == null) throw new InvalidOperationException("无法在所选面上建立打孔基准面。");

            var local = new List<V3>();
            foreach (var c in centres) {
                var lp = target.Placement.InversePoint(c);
                if (!lp.Finite) return Fallback(target, spec, centres, "孔心换算失败");
                local.Add(lp);
            }
            int busy = 0;
            foreach (var lp in local) { double d; if (AutoHoleWriter.ExistingHoleAt(plane, part, lp, out d)) busy++; }
            if (busy > 0) return Fallback(target, spec, centres, "有 " + busy + " 个位置本来就有孔，这一排改成逐个打孔（阵列要求每个位置都是空的）。");

            Layout layout;
            string why;
            if (!Plan(plane, local, circleCentre.HasValue ? (V3?)target.Placement.InversePoint(circleCentre.Value) : null, out layout, out why))
                return Fallback(target, spec, centres, why);

            var result = new DrillResult { Requested = centres.Count };
            P.HoleData data;
            try { data = AutoHoleWriter.BuildHoleData(part, spec, result); }
            catch (Exception e) { throw new InvalidOperationException("孔规格无效：" + e.Message); }

            // 种子孔
            P.Hole seed = null;
            string seedWhy = "";
            for (int round = 1; round <= Rounds && seed == null; round++) {
                if (round > 1) {
                    AutoHoleWriter.Info("种子孔第 " + round + " 轮（重建基准面后重来）");
                    AutoHoleWriter.DropCache(part);
                    try {
                        plane = AutoHoleWriter.FindOrCreatePlane(part, target);
                        if (part.Models.Count >= 1) model = part.Models.Item(1);
                        if (!Plan(plane, local, layout.Circular ? (V3?)layout.Axis : null, out layout, out why)) return Fallback(target, spec, centres, why);
                        data = AutoHoleWriter.BuildHoleData(part, spec, result);
                    } catch (Exception e) { seedWhy = AutoHoleWriter.Friendly(e); continue; }
                }
                P.Hole made;
                if (AutoHoleWriter.DrillOne(part, model, plane, local[0], data, spec.Depth, out seedWhy, out made)) seed = made;
            }
            if (seed == null) return Fallback(target, spec, centres, "种子孔没打上（" + seedWhy + "），改成逐个打孔。");

            // 阵列：按约定的方向建，核对孔心；不对就删掉换另一个方向
            P.Pattern pattern = null;
            string patWhy = "";
            foreach (double turn in layout.Candidates) {
                P.Pattern made = null;
                try { made = Create(model, plane, seed, layout, turn); }
                catch (Exception e) { patWhy = AutoHoleWriter.Friendly(e); continue; }
                if (made == null) { patWhy = "CAD 未返回阵列特征"; continue; }
                string bad = Verify(made, plane, part, local);
                if (bad.Length == 0) { pattern = made; break; }
                patWhy = bad;
                AutoHoleWriter.Info("阵列核对不通过（" + bad + "），删掉换方向");
                try { made.Delete(); } catch (Exception e) { Log.Write("HolePatternDelete", e); }
            }
            if (pattern == null) {
                try { seed.Delete(); } catch (Exception e) { Log.Write("HolePatternSeedDelete", e); }
                return Fallback(target, spec, centres, "阵列没建成（" + patWhy + "），改成逐个打孔。");
            }

            result.Created = centres.Count;
            result.Method = (result.Method.Length > 0 ? result.Method : HoleNaming.Short(spec)) + "（种子孔 + " + (layout.Circular ? "圆周阵列" : "矩形阵列") + "）";
            result.Undo.Hole(part, seed);
            result.Undo.Pattern(part, pattern, centres.Count - 1);
            string note = AutoHoleWriter.Audit(seed, spec);
            if (note.Length > 0) result.Audit = note;

            string node = "";
            try {
                var taken = AutoHoleWriter.ExistingFeatureNames(model);
                node = HoleGrouping.ApplyPattern(model, spec, seed, pattern, centres.Count, taken, result.Notes, result.Undo, part);
            } catch (Exception e) { Log.Write("HolePatternGrouping", e); }
            if (node.Length > 0) result.Features.Add(node);

            string partName = "";
            try { partName = part.Name; } catch { }
            string seedName = HoleGrouping.NameOf(seed), patName = HoleGrouping.NameOf(pattern);
            for (int i = 0; i < centres.Count; i++) {
                string what = i == 0 ? (seedName.Length > 0 ? seedName : "种子孔") + "（种子孔）"
                                     : (patName.Length > 0 ? patName : "阵列") + " 第 " + (i + 1) + " 个";
                result.Outcomes.Add(new ResultList.Item(ResultList.Kind.Done, (partName.Length > 0 ? partName + "：" : "") + what).At(centres[i], spec.HoleDiameter));
            }
            return result;
        }

        // 退回原来的路：逐个打孔（编组、并组、已有孔跳过都照旧），并在结果里说明为什么没用阵列
        static DrillResult Fallback(TargetFace target, HoleSpec spec, IList<V3> centres, string why){
            var requests = new List<AutoHoleWriter.HoleRequest>();
            if (centres != null)
                for (int i = 0; i < centres.Count; i++)
                    requests.Add(new AutoHoleWriter.HoleRequest { Spec = spec, Centre = centres[i], Source = "第 " + (i + 1) + " 个孔" });
            var res = AutoHoleWriter.DrillRequests(target, requests);
            if (!string.IsNullOrEmpty(why)) res.Notes.Insert(0, why);
            return res;
        }

        sealed class Layout {
            public bool Circular;
            public int Count;
            public double Spacing;           // 沿线：间距（米）
            public V3 Axis;                  // 圆周：轴点（零件局部，已投影到孔心平面上）
            public readonly List<double> Candidates = new List<double>();   // 依次尝试的角度（弧度）
        }

        static bool Plan(P.RefPlane plane, IList<V3> local, V3? axisLocal, out Layout layout, out string why){
            layout = new Layout { Count = local.Count, Circular = axisLocal.HasValue };
            why = "";
            V3 n, u;
            try {
                Array nv = new double[3], uv = new double[3];
                plane.GetNormal(ref nv); plane.GetReferenceDirection(ref uv);
                n = V3.From(nv); u = V3.From(uv);
            } catch (Exception e) { why = "读不到基准面方向（" + AutoHoleWriter.Friendly(e) + "），改成逐个打孔。"; return false; }
            if (layout.Circular) {
                double step; V3 axis;
                if (!PatternFit.Circular(local, axisLocal.Value, n, out step, out axis)) { why = "孔心不是均布在一个圆上，改成逐个打孔。"; return false; }
                layout.Axis = axis;
                layout.Candidates.Add(step); layout.Candidates.Add(-step);
            } else {
                double spacing, angle;
                if (!PatternFit.Linear(local, n, u, out spacing, out angle)) { why = "孔心不是等距排在一条直线上，改成逐个打孔。"; return false; }
                layout.Spacing = spacing;
                layout.Candidates.Add(angle); layout.Candidates.Add(-angle);
                if (Math.Abs(angle) < 1e-9 || Math.Abs(Math.Abs(angle) - Math.PI) < 1e-9) layout.Candidates.RemoveAt(1);
            }
            return true;
        }

        static P.Pattern Create(P.Model model, P.RefPlane plane, P.Hole seed, Layout layout, double turn){
            Array feats = new object[]{ seed };
            if (layout.Circular) {
                Array axis = new double[]{ layout.Axis.X, layout.Axis.Y, layout.Axis.Z };
                return AutoHoleWriter.Attempt("圆周阵列", () => model.Patterns.AddByCircular(1, feats, plane, layout.Count, turn, axis,
                    S.PatternOffsetTypeConstants.sePatternFixedOffset, P.PatternCurveAnchorSideConstants.sePatternCurveLeftSide, false));
            }
            return AutoHoleWriter.Attempt("矩形阵列", () => model.Patterns.AddByRectangular(1, feats, plane, layout.Count, 1, layout.Spacing, layout.Spacing, turn,
                S.PatternOffsetTypeConstants.sePatternFixedOffset, 1));
        }

        // 阵列建好以后：状态 OK，而且每个孔心位置上都真的有孔（零件实体上的圆边）
        static string Verify(P.Pattern pattern, P.RefPlane plane, P.PartDocument part, IList<V3> local){
            try {
                object desc;
                var st = pattern.GetStatusEx(out desc);
                if ((int)st != (int)P.FeatureStatusConstants.igFeatureOK) return "阵列状态 " + st + (desc == null ? "" : "（" + desc + "）");
            } catch (Exception e) { return "读不到阵列状态（" + AutoHoleWriter.Friendly(e) + "）"; }
            int missing = 0;
            foreach (var lp in local) { double d; if (!AutoHoleWriter.ExistingHoleAt(plane, part, lp, out d)) missing++; }
            return missing == 0 ? "" : missing + " 个孔位上没有孔";
        }
    }
}
