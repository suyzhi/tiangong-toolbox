using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using D=SolidEdgeDraft;
using F=SolidEdgeFramework;
using S=SolidEdgeFrameworkSupport;

namespace TianGongCadSuite {
    public static partial class DrawingMaker {
        // ---- 尺寸 ----
        // 主视图：上方一串水平坐标、左边一串竖直坐标；孔 / 圆角标注放右边。
        // 俯视图（主视图下面）：左边一串竖直坐标（= 深度方向）；主视图里没有的水平坐标在它下方再标一串。
        // 左视图（主视图右边）：深度方向没在俯视图标过就在它下方标一串水平坐标；主视图里没有的竖直坐标在它右边标。
        static void Annotate(DwgWriter w, Dictionary<DwgViewRole, D.DrawingView> views, DwgLayout lay, List<ModelHole> holes, double[] range, ModelFacts facts, DrawingOutcome o){
            // 每个视图的水平 / 竖直方向对应哪个模型轴 → 用那个轴的倒角容差
            Func<D.DrawingView, bool, double> ctOf = (vv, hz) => {
                try {
                    double dx, dy, dz, ux, uy, uz; D.ViewOrientationConstants kk; vv.ViewOrientation(out dx, out dy, out dz, out ux, out uy, out uz, out kk);
                    var xv = new V3(ux, uy, uz); var yv = new V3(dx, dy, dz).Cross(xv); var a = hz ? xv : yv;
                    double[] c = { Math.Abs(a.X), Math.Abs(a.Y), Math.Abs(a.Z) };
                    int i = c[0] >= c[1] && c[0] >= c[2] ? 0 : c[1] >= c[2] ? 1 : 2;
                    return facts.EdgeTol[i];
                } catch { return 0; }
            };
            const double off = 0.009;   // 坐标串 / 单跨线性尺寸文字线离视图轮廓（纸面，配 2.75~3mm 字体）
            var edges = new Dictionary<DwgViewRole, List<DwgEdge>>();
            foreach (var kv in views) if (kv.Key != DwgViewRole.Iso) {
                bool isSection = false; try { isSection = kv.Value.DrawingViewType == D.DrawingViewTypeConstants.igXSectionView; } catch { }
                // 剖视里台阶孔的孔壁就是要标的台阶，不当"孔带"去掉；顺带剖开的普通孔（法兰上的螺栓孔，OK310-02-002）照样去掉，
                // 不然一圈螺栓孔的孔壁会把坐标尺寸挤成一团 —— 它们的位置在孔是圆的那个视图里标
                edges[kv.Key] = HoleInternal(kv.Value, DrawingViewRead.Edges(kv.Value), isSection ? holes.Where(h => !ModelFacts.IsStepped(h)).ToList() : holes, range);
            }
            var main = views[DwgViewRole.Main];
            var em = edges[DwgViewRole.Main];
            var boxM = DrawingPlan.Box(em);
            // 主视图两串
            var runH = DrawingPlan.Ordinate(em, true, lay.Scale, boxM, false, off, ctOf(main, true));
            var runV = DrawingPlan.Ordinate(em, false, lay.Scale, boxM, true, off, ctOf(main, false));
            Count(o, runH); Count(o, runV);
            o.Covered += Covered(w.Ordinate(main, runH, lay.Scale, boxM)) + Covered(w.Ordinate(main, runV, lay.Scale, boxM));
            var sheetXs = Sheet(main, runH, true); var sheetYs = Sheet(main, runV, false);
            bool depthDone = false;
            D.DrawingView vb;
            if (views.TryGetValue(DwgViewRole.Below, out vb)) {
                var eb = edges[DwgViewRole.Below]; var boxB = DrawingPlan.Box(eb);
                var rv = DrawingPlan.Ordinate(eb, false, lay.Scale, boxB, true, off, ctOf(vb, false));
                Count(o, rv); o.Covered += Covered(w.Ordinate(vb, rv, lay.Scale, boxB)); depthDone = rv != null;
                var rh = Only(DrawingPlan.Ordinate(eb, true, lay.Scale, boxB, true, off, ctOf(vb, true)), vb, sheetXs, true, main, boxM, boxB);
                Count(o, rh); o.Covered += Covered(w.Ordinate(vb, rh, lay.Scale, boxB));
            }
            D.DrawingView vr;
            if (views.TryGetValue(DwgViewRole.Right, out vr)) {
                var er = edges[DwgViewRole.Right]; var boxR = DrawingPlan.Box(er);
                if (!depthDone) { var rh = DrawingPlan.Ordinate(er, true, lay.Scale, boxR, true, off, ctOf(vr, true)); Count(o, rh); o.Covered += Covered(w.Ordinate(vr, rh, lay.Scale, boxR)); }
                var rv = Only(DrawingPlan.Ordinate(er, false, lay.Scale, boxR, false, off, ctOf(vr, false)), vr, sheetYs, false, main, boxM, boxR);
                Count(o, rv); o.Covered += Covered(w.Ordinate(vr, rv, lay.Scale, boxR));
            }
            // 补的反方向视图（右视图 / 后视图）：只标主视图里没标过的坐标（背面盲孔的位置）
            foreach (var role in new[] { DwgViewRole.Left, DwgViewRole.Back }) {
                D.DrawingView ve; if (!views.TryGetValue(role, out ve)) continue;
                var ee = edges[role]; var boxE = DrawingPlan.Box(ee);
                if (role != DwgViewRole.Left) { var rh = Only(DrawingPlan.Ordinate(ee, true, lay.Scale, boxE, true, off, ctOf(ve, true)), ve, sheetXs, true, main, boxM, boxE); Count(o, rh); o.Covered += Covered(w.Ordinate(ve, rh, lay.Scale, boxE)); }
                { var rv = Only(DrawingPlan.Ordinate(ee, false, lay.Scale, boxE, role == DwgViewRole.Left, off, ctOf(ve, false)), ve, sheetYs, false, main, boxM, boxE); Count(o, rv); o.Covered += Covered(w.Ordinate(ve, rv, lay.Scale, boxE)); }
            }
            // 孔、圆角
            int pinGroups = 0, pinCount = 0; var pins = new List<ModelHole>();
            foreach (var kv in views) {
                if (kv.Key == DwgViewRole.Iso) continue;
                var v = kv.Value; var e = edges[kv.Key];
                var box = DrawingPlan.Box(e);
                var sbox = DwgWriter.SheetBox(v);
                var map = HoleMap(v, e, holes, range);
                var threads = DrawingViewRead.Threads(v);
                // 圆形零件的外圆（法兰 Ø64）也要标直径：上限放到视图外框
                double maxD = Math.Min(box[2] - box[0], box[3] - box[1]) * 1.01;
                double fallY = sbox[3] - 0.004;
                var hgs = DrawingPlan.Holes(e, maxD).OrderByDescending(g => g.Count).ToList();
                if (hgs.Count > 12) { o.Notes.Add("孔的规格太多（" + hgs.Count + " 种），只标了 12 种，请人工补"); hgs = hgs.Take(12).ToList(); }
                bool extra = kv.Key == DwgViewRole.Left || kv.Key == DwgViewRole.Back;
                foreach (var g in hgs) {
                    ModelHole mh; map.TryGetValue(g.Leader, out mh);
                    // 同组孔里引线落在阵列 / 镜像副本上、恰好没挂上孔特征规格时，从同组其它圆借用已挂上的规格
                    if (mh == null || mh.Spec == null) {
                        foreach (var cc in g.Circles) {
                            ModelHole alt;
                            if (map.TryGetValue(cc, out alt) && (mh == null || alt.Spec != null)) {
                                mh = alt;
                                if (mh.Spec != null) break;
                            }
                        }
                    }
                    // 补的反方向视图只标盲孔（通孔在对面那个视图里已经是圆、已经标过）
                    if (extra && (mh == null || mh.Through)) continue;
                    // 不是孔（凸台外圆、轴）就不当孔标
                    if (mh == null && holes.Count > 0 && !map.Keys.Any(c => Math.Abs(c.R - g.Leader.R) < 1e-6)) { }
                    o.Required++;
                    string pre, val, suf, sub;
                    // 这个视图里孔能对上三维孔（map 非空）而这组对不上 → 凸台 / 外圆；零件根本没有孔 → 也都是外圆
                    bool isHole = mh != null || (holes.Count > 0 && map.Count == 0);
                    DrawingText.Hole(g.Count, mh, g.DiameterMm, isHole, out pre, out val, out suf, out sub);
                    if (isHole) o.Holes += g.Count;
                    // 没有孔特征的螺纹孔：看视图里的螺纹线
                    if (val.Length == 0 && isHole) {
                        string m = DrawingPlan.ThreadFromEdges(g.Leader, threads);
                        if (m.Length > 0) { val = m; DrawingText.ThreadOnly(m, mh, out suf, out sub); }
                    }
                    // 定位销孔（没螺纹、没沉孔的 Ø4/5/6/8 光孔，DrawingTolerance）：加 H7 + 位置度框，文字下面多留一格放框
                    // 视图里的圆没对上三维孔（HoleMap 偶尔整个视图对不上，02-010）时按直径在三维孔里找：同直径的孔全是光孔才算
                    var pinHole = mh;
                    if (pinHole == null && isHole) {
                        var same = holes.Where(h => Math.Abs(h.Radius * 2000 - g.DiameterMm) < 0.02).ToList();
                        if (same.Count > 0 && same.All(h => h.CbRadius <= 0 && (h.Spec == null || (h.Spec.Kind != HoleKind.Tapped && h.Spec.Kind != HoleKind.Counterbore && h.Spec.ThreadSize.Length == 0)))) pinHole = same[0];
                    }
                    bool cbore = pinHole != null && (pinHole.CbRadius > 0 || (pinHole.Spec != null && pinHole.Spec.Kind == HoleKind.Counterbore));
                    bool pin = DrawingTolerance.IsPinHole(g.DiameterMm, isHole && pinHole != null, val.Length > 0, cbore || sub.Length > 0);
                    var ls = DwgWriter.ToSheet(v, g.Leader.Cx, g.Leader.Cy);
                    string shown = pre + (val.Length > 0 ? val : "Ø" + g.DiameterMm.ToString("0.##")) + suf + (pin ? " H7(+0.012)" : "");
                    var text = Spot(w, sbox, ls[1], shown, sub.Length > 0 ? 2 : 1, ref fallY, pin ? 0.006 : 0);
                    if (w.HoleCallout(v, g, pre, val, suf, sub, text)) {
                        o.Covered++; o.HoleCallouts++;
                        if (pin && w.Fit(w.LastCallout, g.DiameterMm)) {
                            pinGroups++; pinCount += g.Count; pins.Add(pinHole);
                            w.Position(w.LastCallout);
                        }
                    }
                }
                bool sectionView = false; try { sectionView = v.DrawingViewType == D.DrawingViewTypeConstants.igXSectionView; } catch { }
                if (extra) continue;   // 外圆、圆角、倒角在主视图那一侧已经标了
                // 同心圆的外圈：不是沉孔那圈（已写在孔标注的第二行）、且不是本视图已标过的同直径孔（如背/正面同心投影）才单独标直径
                foreach (var g in DrawingPlan.OuterRings(e, maxD)) {
                    if (hgs.Any(h => Math.Abs(h.DiameterMm - g.DiameterMm) < 0.05)) continue;
                    bool cb = map.Any(mp => Math.Abs(mp.Value.CbRadius * 2000 - g.DiameterMm) < 0.02 && Math.Abs(mp.Key.Cx - g.Leader.Cx) < 1e-5 && Math.Abs(mp.Key.Cy - g.Leader.Cy) < 1e-5)
                        || holes.Any(h => Math.Abs(h.CbRadius * 2000 - g.DiameterMm) < 0.02);
                    if (cb || DrawingPlan.ThreadFromEdges(new DwgEdge { Kind = DwgKind.Circle, Cx = g.Leader.Cx, Cy = g.Leader.Cy, R = 0 }, threads) == "M" + g.DiameterMm) continue;
                    o.Required++;
                    var ls2 = DwgWriter.ToSheet(v, g.Leader.Cx, g.Leader.Cy);
                    var t2 = Spot(w, sbox, ls2[1], (g.Count > 1 ? g.Count + "×" : "") + "Ø" + g.DiameterMm.ToString("0.##"), 1, ref fallY);
                    if (w.HoleCallout(v, g, g.Count > 1 ? g.Count + "×" : "", "", "", "", t2)) o.Covered++;
                }
                // 刻字、花纹会带来几百个小圆弧：只标前 10 组（个数多的、半径大的在前），其余提示人工
                var radii = sectionView ? new List<RadiusGroup>() : DrawingPlan.Radii(e).Where(g => g.RadiusMm >= 0.3).OrderByDescending(g => g.Count).ThenByDescending(g => g.RadiusMm).ToList();
                if (radii.Count > 10) { o.Notes.Add("圆弧太多（" + radii.Count + " 种半径，多半是刻字 / 花纹），只标了 10 种，请人工看一眼"); radii = radii.Take(10).ToList(); }
                foreach (var g in radii) {
                    o.Required++; o.Radii++;
                    var ls = DwgWriter.ToSheet(v, g.Leader.Cx, g.Leader.Cy);
                    var text = Spot(w, sbox, ls[1], DrawingPlan.RadiusPrefix(g.Count) + "R" + g.RadiusMm.ToString("0.##"), 1, ref fallY);
                    if (w.Radius(v, g, text)) o.Covered++;
                }
                // 倒角：等于模板「未注倒角」那个值的不标（A3 模板写 C1，A4 写 C0.5）
                double dflt = lay.Zones.Name == "A4" ? 0.5 : 1.0;
                foreach (var g in sectionView ? new List<RadiusGroup>() : DrawingPlan.Chamfers(e)) {
                    if (Math.Abs(g.RadiusMm - dflt) < 0.01) continue;
                    o.Required++;
                    var l = g.Leader;
                    double mx = (l.X1 + l.X2) / 2, my = (l.Y1 + l.Y2) / 2;
                    var sp = DwgWriter.ToSheet(v, mx, my);
                    var text = Spot(w, sbox, sp[1], DrawingPlan.ChamferText(g.Count, g.RadiusMm), 1, ref fallY);
                    if (w.Chamfer(v, g, e, text)) o.Covered++;
                }
            }
            // 销孔所在的面（装配时贴着别的零件、靠销定位的安装面）标 Ra3.2：在孔是侧面的那个视图里找过孔口的那条面线
            bool rough = pins.Count > 0 && MatingFaces(w, views, edges, pins, range) > 0;
            string tn = DrawingTolerance.Note(pinGroups, pinCount, rough);
            if (tn.Length > 0) o.Notes.Insert(0, tn);
        }

        static int MatingFaces(DwgWriter w, Dictionary<DwgViewRole, D.DrawingView> views, Dictionary<DwgViewRole, List<DwgEdge>> edges, List<ModelHole> pins, double[] range){
            var done = new HashSet<string>(); int n = 0;
            foreach (var h in pins) {
                if (h == null || h.Segments.Count == 0) continue;
                // 开口那头（盲孔只有一头；通孔取 +轴 那头）
                double t = !h.BlindHi ? h.Segments.Max(sg => sg.Hi) : h.Segments.Min(sg => sg.Lo);
                var mouth = h.Point + h.Axis * t;
                int ax = Math.Abs(h.Axis.X) > 0.9998 ? 0 : Math.Abs(h.Axis.Y) > 0.9998 ? 1 : Math.Abs(h.Axis.Z) > 0.9998 ? 2 : -1;
                if (ax < 0) continue;
                string key = ax + ":" + Math.Round(ax == 0 ? mouth.X : ax == 1 ? mouth.Y : mouth.Z, 5);
                if (done.Contains(key)) continue;
                foreach (var kv in views) {
                    if (kv.Key == DwgViewRole.Iso || !edges.ContainsKey(kv.Key)) continue;
                    try {
                        V3 x, y, d; double ou, ov;
                        if (!Frame(kv.Value, edges[kv.Key], range, out x, out y, out d, out ou, out ov)) continue;
                        if (Math.Abs(h.Axis.Dot(d)) > 1e-3) continue;
                        bool alongX = Math.Abs(h.Axis.Dot(x)) > 0.999, alongY = Math.Abs(h.Axis.Dot(y)) > 0.999;
                        if (!alongX && !alongY) continue;
                        double[] at;
                        var face = DrawingTolerance.MouthFace(edges[kv.Key], alongX, mouth.Dot(x) + ou, mouth.Dot(y) + ov, h.Radius, out at);
                        if (face == null) continue;
                        if (w.Roughness(kv.Value, face, at, DrawingTolerance.MatingRoughness)) { done.Add(key); n++; }
                        break;
                    } catch (Exception e) { Log.Write("DrawingMatingFace", e); }
                }
            }
            return n;
        }

        // 视图的屏幕轴（x 右、y 上、d 视线）和"模型坐标投影 → 视图坐标"的平移（零件投影外框对齐视图图元外框，同 HoleMap）
        static bool Frame(D.DrawingView v, List<DwgEdge> edges, double[] range, out V3 x, out V3 y, out V3 d, out double ou, out double ov){
            double dx, dy, dz, ux, uy, uz; D.ViewOrientationConstants k; v.ViewOrientation(out dx, out dy, out dz, out ux, out uy, out uz, out k);
            d = new V3(dx, dy, dz).Unit(); x = new V3(ux, uy, uz).Unit(); y = d.Cross(x);
            double pu = double.MaxValue, pv = double.MaxValue;
            for (int i = 0; i < 8; i++) { var c = new V3(range[(i & 1) == 0 ? 0 : 3], range[(i & 2) == 0 ? 1 : 4], range[(i & 4) == 0 ? 2 : 5]); pu = Math.Min(pu, c.Dot(x)); pv = Math.Min(pv, c.Dot(y)); }
            var box = DrawingPlan.Box(edges);
            ou = box[0] - pu; ov = box[1] - pv;
            return edges.Count > 0;
        }
        // 引线文字放哪：在视图旁边找一块空地（DwgSpace）；实在没有就按行高累计往下排（防止两行沉孔 / H7 位置度框叠字）
        // 天工把 SubfixString（第二行沉孔 / 底孔）画在引线下方、主行画在引线上方，因此返回引线 y 时要给第二行和位置度框预留高度。
        static double[] Spot(DwgWriter w, double[] sbox, double y, string text, int lines, ref double fallY, double below = 0){
            double subH = 0.0052 * Math.Max(0, lines - 1) + below;
            double tw = DwgSpace.TextWidth(text) + 0.002, th = 0.0052 + subH;
            var r = w.Space == null ? null : w.Space.Place(sbox, y, tw, th);
            if (r != null) {
                // 同步压低右侧兜底游标：若后续某条超宽标注（如 H7+偏差）放不进右侧走廊走兜底，不再回弹压到已排好的标注上
                if (r[0] >= sbox[2] - 1e-6) fallY = Math.Min(fallY, r[1] - 0.003);
                return new[] { r[0], r[1] + 0.001 + subH };
            }
            double py = fallY - 0.0052;
            fallY -= th + 0.003;
            return new[] { sbox[2] + 0.006, py };
        }

        static void Count(DrawingOutcome o, OrdinateRun r){
            if (r == null) return;
            if (r.Count >= DrawingPlan.MaxRun && !o.Notes.Any(n => n.StartsWith("细节太多"))) o.Notes.Add("细节太多（刻线 / 刻字 / 花纹），坐标尺寸只标了外形、孔和长边，请人工补");
            o.Required += r.Count - 1;
            if (r.Count > 28 && !o.Notes.Any(n => n.StartsWith("尺寸很密"))) o.Notes.Add("尺寸很密（一串 " + r.Count + " 个），建议人工整理或加局部视图");
        }
        static int Covered(int created){ return Math.Max(0, created - 1); }
        static List<double> Sheet(D.DrawingView v, OrdinateRun r, bool horizontal){
            var l = new List<double>();
            if (r == null) return l;
            foreach (var t in new[] { r.Origin }.Concat(r.Targets)) { var p = DwgWriter.ToSheet(v, t.X, t.Y); l.Add(horizontal ? p[0] : p[1]); }
            return l;
        }
        // 只留主视图里没标过的坐标（比较图纸坐标：投影对齐的视图同一个模型坐标落在同一条图纸线上）
        // 只留主视图里没标过的坐标。按"到外形最小边的距离"比（剖视不和主视图投影对齐、还可能左右镜像），
        // 镜像（两个视图同一方向的屏幕轴反向）时从另一头量
        static OrdinateRun Only(OrdinateRun r, D.DrawingView v, List<double> done, bool horizontal, D.DrawingView main, double[] mainBox, double[] vBox){
            if (r == null) return null;
            var mlo = DwgWriter.ToSheet(main, mainBox[0], mainBox[1]);
            var vlo = DwgWriter.ToSheet(v, vBox[0], vBox[1]); var vhi = DwgWriter.ToSheet(v, vBox[2], vBox[3]);
            bool mirror = Axis(v, horizontal).Dot(Axis(main, horizontal)) < 0;
            var rel = done.Select(d => d - (horizontal ? mlo[0] : mlo[1])).ToList();
            var keep = r.Targets.Where(t => {
                var p = DwgWriter.ToSheet(v, t.X, t.Y); double c = horizontal ? p[0] : p[1];
                double cr = mirror ? (horizontal ? Math.Max(vlo[0], vhi[0]) - c : Math.Max(vlo[1], vhi[1]) - c) : c - (horizontal ? Math.Min(vlo[0], vhi[0]) : Math.Min(vlo[1], vhi[1]));
                return !rel.Any(d => Math.Abs(d - cr) < 2e-5);
            }).ToList();
            if (keep.Count == 0) return null;
            r.Targets.Clear(); r.Targets.AddRange(keep);
            DrawingPlan.Spread(new List<DwgTarget>(keep) { r.Origin }, DrawingPlan.TextPitch / Math.Max(v.ScaleFactor, 1e-9));
            return r;
        }
        static V3 Axis(D.DrawingView v, bool horizontal){
            try { double dx, dy, dz, ux, uy, uz; D.ViewOrientationConstants k; v.ViewOrientation(out dx, out dy, out dz, out ux, out uy, out uz, out k); var x = new V3(ux, uy, uz); return horizontal ? x : new V3(dx, dy, dz).Cross(x); } catch { return new V3(1, 0, 0); }
        }

        static string Describe(D.DrawingView v){
            try {
                double dx, dy, dz, ux, uy, uz; D.ViewOrientationConstants k; v.ViewOrientation(out dx, out dy, out dz, out ux, out uy, out uz, out k);
                var b = DwgWriter.SheetBox(v);
                Func<double, string> f = x => Math.Round(x, 3).ToString(System.Globalization.CultureInfo.InvariantCulture);
                return k + " 视线(" + f(dx) + "," + f(dy) + "," + f(dz) + ") x(" + f(ux) + "," + f(uy) + "," + f(uz) + ") 图纸框 " + f(b[0] * 1000) + "," + f(b[1] * 1000) + " - " + f(b[2] * 1000) + "," + f(b[3] * 1000);
            } catch (Exception e) { return e.Message; }
        }

        // 视线和孔轴垂直时，孔在视图里是一条带（孔壁、沉孔壁的轮廓线 / 隐藏线，沉孔底那条横线）。
        // 这些线不是零件外形，坐标尺寸不该标它们 —— 孔的位置在孔是圆的那个视图里标，深度写在孔标注里。
        static List<DwgEdge> HoleInternal(D.DrawingView v, List<DwgEdge> edges, List<ModelHole> holes, double[] range){
            if (holes.Count == 0) return edges;
            try {
                double dx, dy, dz, ux, uy, uz; D.ViewOrientationConstants k; v.ViewOrientation(out dx, out dy, out dz, out ux, out uy, out uz, out k);
                var d = new V3(dx, dy, dz).Unit(); var x = new V3(ux, uy, uz).Unit(); var y = d.Cross(x);
                double pu = double.MaxValue, pv = double.MaxValue;
                for (int i = 0; i < 8; i++) { var c = new V3(range[(i & 1) == 0 ? 0 : 3], range[(i & 2) == 0 ? 1 : 4], range[(i & 4) == 0 ? 2 : 5]); pu = Math.Min(pu, c.Dot(x)); pv = Math.Min(pv, c.Dot(y)); }
                var box = DrawingPlan.Box(edges);
                double ou = box[0] - pu, ov = box[1] - pv;
                var bands = new List<double[]>();   // {沿 x 走(1)/沿 y 走(0), 中心, 半宽, 起, 止}
                foreach (var h in holes) {
                    if (Math.Abs(h.Axis.Dot(d)) > 1e-3) continue;
                    double hw = Math.Max(h.Radius, h.CbRadius);
                    double ax = Math.Abs(h.Axis.Dot(x)), ay = Math.Abs(h.Axis.Dot(y));
                    var p0 = h.Point + h.Axis * h.Segments.Min(sg => sg.Lo); var p1 = h.Point + h.Axis * h.Segments.Max(sg => sg.Hi);
                    if (ax > 0.999) bands.Add(new[] { 1.0, h.Point.Dot(y) + ov, hw, Math.Min(p0.Dot(x), p1.Dot(x)) + ou, Math.Max(p0.Dot(x), p1.Dot(x)) + ou });
                    else if (ay > 0.999) bands.Add(new[] { 0.0, h.Point.Dot(x) + ou, hw, Math.Min(p0.Dot(y), p1.Dot(y)) + ov, Math.Max(p0.Dot(y), p1.Dot(y)) + ov });
                }
                if (bands.Count == 0) return edges;
                const double tol = 2e-5;
                return edges.Where(e => {
                    if (e.Kind != DwgKind.Line) return true;
                    foreach (var b in bands) {
                        bool alongX = b[0] > 0.5; double c = b[1], hw = b[2], s0 = b[3] - tol, s1 = b[4] + tol;
                        double pmin = alongX ? Math.Min(e.Y1, e.Y2) : Math.Min(e.X1, e.X2), pmax = alongX ? Math.Max(e.Y1, e.Y2) : Math.Max(e.X1, e.X2);
                        double qmin = alongX ? Math.Min(e.X1, e.X2) : Math.Min(e.Y1, e.Y2), qmax = alongX ? Math.Max(e.X1, e.X2) : Math.Max(e.Y1, e.Y2);
                        // 整条线落在孔带里（横向在 c±hw 内、沿孔方向在孔的长度内）
                        if (pmin >= c - hw - tol && pmax <= c + hw + tol && qmin >= s0 && qmax <= s1) return false;
                    }
                    return true;
                }).ToList();
            } catch (Exception ex) { Log.Write("DrawingHoleInternal", ex); return edges; }
        }

        // 视图里的圆 ↔ 模型里的孔：按视图朝向把孔轴线投影到视图平面，再用整个零件投影外框和视图图元外框对齐平移
        static Dictionary<DwgEdge, ModelHole> HoleMap(D.DrawingView v, List<DwgEdge> edges, List<ModelHole> holes, double[] range){
            var map = new Dictionary<DwgEdge, ModelHole>();
            if (holes.Count == 0) return map;
            try {
                double dx, dy, dz, ux, uy, uz; D.ViewOrientationConstants k; v.ViewOrientation(out dx, out dy, out dz, out ux, out uy, out uz, out k);
                var d = new V3(dx, dy, dz).Unit(); var x = new V3(ux, uy, uz).Unit(); var y = d.Cross(x);
                double pu = double.MaxValue, pv = double.MaxValue;
                for (int i = 0; i < 8; i++) { var c = new V3(range[(i & 1) == 0 ? 0 : 3], range[(i & 2) == 0 ? 1 : 4], range[(i & 4) == 0 ? 2 : 5]); pu = Math.Min(pu, c.Dot(x)); pv = Math.Min(pv, c.Dot(y)); }
                var box = DrawingPlan.Box(edges);
                double ou = box[0] - pu, ov = box[1] - pv;
                foreach (var e in edges) {
                    if (e.Kind != DwgKind.Circle) continue;
                    foreach (var h in holes) {
                        if (Math.Abs(Math.Abs(h.Axis.Dot(d)) - 1) > 1e-4) continue;
                        double cu = h.Point.Dot(x) + ou, cv = h.Point.Dot(y) + ov;
                        if (Math.Abs(cu - e.Cx) < 1e-4 && Math.Abs(cv - e.Cy) < 1e-4 && (Math.Abs(h.Radius - e.R) < 1e-5 || Math.Abs(h.CbRadius - e.R) < 1e-5)) { map[e] = h; break; }
                    }
                }
            } catch (Exception e) { Log.Write("DrawingHoleMap", e); }
            return map;
        }

        // 模板里自带一条「刻字：%{文件名（无扩展名）|R1}」引线，没挂在任何东西上（文字在标题栏左上方）。
        // 天工不让把它直接改挂（SetTerminator 0x80040223，探针 3），所以照它的文字和位置新建一条挂到轴测图最长的边上，再删掉模板那条。
        // 模板刻字引线文字占的地方（图纸坐标）：按模板那条的位置，宽度按展开后的「刻字：文件名」估
        static double[] EngraveBox(D.Sheet sheet, string name){
            try {
                var bs = (S.Balloons)sheet.Balloons;
                for (int i = 1; i <= bs.Count; i++) {
                    var b = bs.Item(i); string t = ""; try { t = b.BalloonText ?? ""; } catch { }
                    if (!t.Contains("刻字")) continue;
                    double a, c, d, e; b.Range(out a, out c, out d, out e);
                    double wd = Math.Max(d - a, DwgSpace.TextWidth("刻字：" + name) + 0.004);
                    return new[] { a - 0.002, Math.Min(c, e) - 0.002, a + wd, Math.Max(c, e) + 0.002 };
                }
            } catch (Exception ex) { Log.Write("DrawingEngraveBox", ex); }
            return null;
        }

        static void Engrave(D.DraftDocument dft, D.Sheet sheet, D.DrawingView iso, string engraveStyle, string restoreStyle, DrawingOutcome o){
            try {
                var bs = (S.Balloons)sheet.Balloons;
                S.Balloon tpl = null;
                for (int i = 1; i <= bs.Count; i++) { var b = bs.Item(i); string t = ""; try { t = b.BalloonText ?? ""; } catch { } if (t.Contains("刻字")) { tpl = b; break; } }
                var edges = DrawingViewRead.Edges(iso).Where(x => x.Kind == DwgKind.Line).OrderByDescending(x => x.Length).ToList();
                if (edges.Count == 0) return;
                var best = edges[0];
                double[] at = null;
                string text = "刻字：%{文件名（无扩展名）|R1}";
                if (tpl != null) {
                    try { text = tpl.BalloonText; } catch { }
                    try { double a, b, c, d; tpl.Range(out a, out b, out c, out d); at = new[] { a, (b + d) / 2 }; } catch { }
                }
                if (at == null) { var ib = DwgWriter.SheetBox(iso); at = new[] { ib[0] - 0.06, 0.050 }; }
                var w = new DwgWriter(sheet, dft, engraveStyle);
                var nb = w.Note(iso, best, (best.X1 + best.X2) / 2, (best.Y1 + best.Y2) / 2, text, at);
                if (nb != null && tpl != null) { try { tpl.Delete(); } catch { } }
                if (nb == null) o.Notes.Add("刻字引线没挂上（模板那条还在，请手动挂到零件上）");
            } catch (Exception e) {
                o.Notes.Add("刻字引线没挂上：" + e.Message);
            } finally {
                DwgWriter.ApplySheetStyle(dft, sheet, restoreStyle);
            }
        }
    }
}
