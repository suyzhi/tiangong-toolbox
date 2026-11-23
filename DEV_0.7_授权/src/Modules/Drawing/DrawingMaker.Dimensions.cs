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
            const double off = 0.0075;   // 坐标串 / 单跨线性尺寸文字线离视图轮廓（纸面 7.5mm，与公司人图 6.5~8mm 一致，紧凑美观）
            var rawEdges = new Dictionary<DwgViewRole, List<DwgEdge>>();
            var edges = new Dictionary<DwgViewRole, List<DwgEdge>>();
            var viewCboxes = new Dictionary<DwgViewRole, double[]>();
            var viewObs = new Dictionary<DwgViewRole, double[]>();
            foreach (var kv in views) if (kv.Key != DwgViewRole.Iso) {
                bool isSection = false; try { isSection = kv.Value.DrawingViewType == D.DrawingViewTypeConstants.igXSectionView; } catch { }
                var raw = DrawingViewRead.Edges(kv.Value);
                rawEdges[kv.Key] = raw;
                // 剖视里台阶孔的孔壁就是要标的台阶，不当"孔带"去掉；顺带剖开的普通孔（法兰上的螺栓孔，OK310-02-002）照样去掉，
                // 不然一圈螺栓孔的孔壁会把坐标尺寸挤成一团 —— 它们的位置在孔是圆的那个视图里标
                var filt = HoleInternal(kv.Value, raw, isSection ? holes.Where(h => !ModelFacts.IsStepped(h)).ToList() : holes, range);
                edges[kv.Key] = filt;
                var vBox = DrawingPlan.Box(raw.Count > 0 ? raw : filt);
                var p0 = DwgWriter.ToSheet(kv.Value, vBox[0], vBox[1]);
                var p1 = DwgWriter.ToSheet(kv.Value, vBox[2], vBox[3]);
                var cb = new[] { Math.Min(p0[0], p1[0]), Math.Min(p0[1], p1[1]), Math.Max(p0[0], p1[0]), Math.Max(p0[1], p1[1]) };
                var ob = new[] { cb[0] - 0.002, cb[1] - 0.002, cb[2] + 0.002, cb[3] + 0.002 };
                viewCboxes[kv.Key] = cb;
                viewObs[kv.Key] = ob;
                if (w.Space != null) {
                    w.Space.AddView(ob);
                    // 剖视图上方留出「剖面 A-A 1:1」标题区，避免引线文字压住剖面标题
                    if (isSection) w.Space.Add(cb[0] + 0.002, cb[3] + 0.001, cb[2] - 0.002, cb[3] + 0.014);
                }
            }
            var main = views[DwgViewRole.Main];
            var em = edges[DwgViewRole.Main];
            var boxM = DrawingPlan.Box(em);
            // 主视图两串（纯同心圆端面如销子顶视，圆直径已直接标 Ø12，跳过冗余的 6-0-6 坐标串）
            var runH = DrawingPlan.Ordinate(em, true, lay.Scale, boxM, false, off, ctOf(main, true));
            var runV = DrawingPlan.Ordinate(em, false, lay.Scale, boxM, true, off, ctOf(main, false));
            if (DrawingPlan.IsRedundantCircleSpan(runH)) runH = null;
            if (DrawingPlan.IsRedundantCircleSpan(runV)) runV = null;
            Count(o, runH); Count(o, runV);
            o.Covered += Covered(w.Ordinate(main, runH, lay.Scale, boxM)) + Covered(w.Ordinate(main, runV, lay.Scale, boxM));
            var sheetXs = Sheet(main, runH, true); var sheetYs = Sheet(main, runV, false);
            bool depthDone = false;
            D.DrawingView vb;
            if (views.TryGetValue(DwgViewRole.Below, out vb)) {
                var eb = edges[DwgViewRole.Below]; var boxB = DrawingPlan.Box(eb);
                var rv = DrawingPlan.Ordinate(eb, false, lay.Scale, boxB, true, off, ctOf(vb, false));
                if (DrawingPlan.IsRedundantCircleSpan(rv)) rv = null;
                Count(o, rv); o.Covered += Covered(w.Ordinate(vb, rv, lay.Scale, boxB)); depthDone = rv != null;
                var rh = Only(DrawingPlan.Ordinate(eb, true, lay.Scale, boxB, true, off, ctOf(vb, true)), vb, sheetXs, true, main, boxM, boxB);
                if (DrawingPlan.IsRedundantCircleSpan(rh)) rh = null;
                Count(o, rh); o.Covered += Covered(w.Ordinate(vb, rh, lay.Scale, boxB));
            }
            D.DrawingView vr;
            if (views.TryGetValue(DwgViewRole.Right, out vr)) {
                var er = edges[DwgViewRole.Right]; var boxR = DrawingPlan.Box(er);
                if (!depthDone) {
                    var rh = DrawingPlan.Ordinate(er, true, lay.Scale, boxR, true, off, ctOf(vr, true));
                    if (DrawingPlan.IsRedundantCircleSpan(rh)) rh = null;
                    Count(o, rh); o.Covered += Covered(w.Ordinate(vr, rh, lay.Scale, boxR));
                }
                var rv = Only(DrawingPlan.Ordinate(er, false, lay.Scale, boxR, false, off, ctOf(vr, false)), vr, sheetYs, false, main, boxM, boxR);
                if (DrawingPlan.IsRedundantCircleSpan(rv)) rv = null;
                Count(o, rv); o.Covered += Covered(w.Ordinate(vr, rv, lay.Scale, boxR));
            }
            // 补的反方向视图（右视图 / 后视图）：只标主视图里没标过的坐标（背面盲孔的位置）
            foreach (var role in new[] { DwgViewRole.Left, DwgViewRole.Back }) {
                D.DrawingView ve; if (!views.TryGetValue(role, out ve)) continue;
                var ee = edges[role]; var boxE = DrawingPlan.Box(ee);
                if (role != DwgViewRole.Left) { var rh = Only(DrawingPlan.Ordinate(ee, true, lay.Scale, boxE, true, off, ctOf(ve, true)), ve, sheetXs, true, main, boxM, boxE); Count(o, rh); o.Covered += Covered(w.Ordinate(ve, rh, lay.Scale, boxE)); }
                { var rv = Only(DrawingPlan.Ordinate(ee, false, lay.Scale, boxE, role == DwgViewRole.Left, off, ctOf(ve, false)), ve, sheetYs, false, main, boxM, boxE); Count(o, rv); o.Covered += Covered(w.Ordinate(ve, rv, lay.Scale, boxE)); }
            }
            // 孔、外圆、圆角、倒角：先统一收集每视图全部引线标注，挑出就近不打架的代表实例，按锚点 Y 从上到下排序后再紧凑落位，
            // 从源头消除引线乱交叉、跨整张视图拉 100mm 长线、以及 2 行沉孔标注叠字。
            int pinGroups = 0, pinCount = 0; var pins = new List<ModelHole>();
            bool hasRightView = views.ContainsKey(DwgViewRole.Right);
            foreach (var kv in views) {
                if (kv.Key == DwgViewRole.Iso) continue;
                var v = kv.Value; var e = edges[kv.Key];
                var allRaw = rawEdges[kv.Key];
                var box = DrawingPlan.Box(e);
                var rawBox = DrawingPlan.Box(allRaw.Count > 0 ? allRaw : e);
                var cbox = viewCboxes[kv.Key];
                var ownObs = viewObs[kv.Key];
                var map = HoleMap(v, e, holes, range);
                var threads = DrawingViewRead.Threads(v);
                // 圆形零件的外圆（法兰 Ø64）也要标直径：上限放到视图外框
                double maxD = Math.Min(box[2] - box[0], box[3] - box[1]) * 1.01;
                double fallY = cbox[3] - 0.002;
                // 主视图左侧和上方已有坐标尺寸（0 基准在左上角），即使右侧有左视图也绝不往左上 0 角挑孔/倒角，
                // 统一优先靠右挑代表实例（均衡布局已为 Main↔Right 留出通道，避免像 02-003 那样撞到 0）
                bool preferRight = true;
                var usedLeaderY = new List<double>();
                Func<DwgEdge, double[]> toAnchorSheet = ed => { var a = DrawingPlan.AnchorOf(ed); return DwgWriter.ToSheet(v, a[0], a[1]); };

                // 把图纸坐标候选框反向映回视图坐标，用全量可见边（含孔壁）检验空腔净空（留 2.5mm 纸面安全边）
                var sp0 = DwgWriter.ToSheet(v, rawBox[0], rawBox[1]);
                var sp1 = DwgWriter.ToSheet(v, rawBox[2], rawBox[3]);
                Func<double[], bool> geomClear = null;
                if (Math.Abs(sp1[0] - sp0[0]) > 1e-6 && Math.Abs(sp1[1] - sp0[1]) > 1e-6) {
                    double marginView = 0.0025 / Math.Max(lay.Scale, 1e-6);
                    geomClear = sr => {
                        double vx0 = rawBox[0] + (sr[0] - sp0[0]) / (sp1[0] - sp0[0]) * (rawBox[2] - rawBox[0]);
                        double vx1 = rawBox[0] + (sr[2] - sp0[0]) / (sp1[0] - sp0[0]) * (rawBox[2] - rawBox[0]);
                        double vy0 = rawBox[1] + (sr[1] - sp0[1]) / (sp1[1] - sp0[1]) * (rawBox[3] - rawBox[1]);
                        double vy1 = rawBox[1] + (sr[3] - sp0[1]) / (sp1[1] - sp0[1]) * (rawBox[3] - rawBox[1]);
                        var vrRect = new[] { Math.Min(vx0, vx1), Math.Min(vy0, vy1), Math.Max(vx0, vx1), Math.Max(vy0, vy1) };
                        return DrawingPlan.RectClearsViewEdges(vrRect, allRaw, marginView);
                    };
                }

                var callouts = new List<PlannedCallout>();
                var hgs = DrawingPlan.Holes(e, maxD).OrderByDescending(g => g.Count).ToList();
                if (hgs.Count > 12) { o.Notes.Add("孔的规格太多（" + hgs.Count + " 种），只标了 12 种，请人工补"); hgs = hgs.Take(12).ToList(); }
                bool extra = kv.Key == DwgViewRole.Left || kv.Key == DwgViewRole.Back;
                foreach (var g in hgs) {
                    var picked = DrawingPlan.PickSeparatedLeader(g.Circles, box, preferRight, usedLeaderY, toAnchorSheet, 0.008);
                    if (picked != null) g.Leader = picked;
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
                    var pinHole = mh;
                    if (pinHole == null && isHole) {
                        var same = holes.Where(h => Math.Abs(h.Radius * 2000 - g.DiameterMm) < 0.02).ToList();
                        if (same.Count > 0 && same.All(h => h.CbRadius <= 0 && (h.Spec == null || (h.Spec.Kind != HoleKind.Tapped && h.Spec.Kind != HoleKind.Counterbore && h.Spec.ThreadSize.Length == 0)))) pinHole = same[0];
                    }
                    bool cbore = pinHole != null && (pinHole.CbRadius > 0 || (pinHole.Spec != null && pinHole.Spec.Kind == HoleKind.Counterbore));
                    bool pin = DrawingTolerance.IsPinHole(g.DiameterMm, isHole && pinHole != null, val.Length > 0, cbore || sub.Length > 0);
                    var ls = DwgWriter.ToSheet(v, g.Leader.Cx, g.Leader.Cy);
                    usedLeaderY.Add(ls[1]);
                    string shown = pre + (val.Length > 0 ? val : "Ø" + g.DiameterMm.ToString("0.##")) + (pin ? " H7(+0.015/0)" : "") + suf;
                    callouts.Add(new PlannedCallout {
                        Kind = CalloutKind.Hole, Hole = g, Pre = pre, Val = val, Suf = suf, Sub = sub,
                        Pin = pin, PinHole = pinHole, Line1 = shown, Line2 = sub,
                        Lines = sub.Length > 0 ? 2 : 1, BelowH = pin ? 0.0065 : 0.0,
                        Fx = ls[0], Fy = ls[1], AllowVoid = false
                    });
                }
                bool sectionView = false; try { sectionView = v.DrawingViewType == D.DrawingViewTypeConstants.igXSectionView; } catch { }
                if (!extra) {
                    // 同心圆的外圈：不是沉孔那圈、且不是本视图已标过的同直径孔才单独标直径
                    foreach (var g in DrawingPlan.OuterRings(e, maxD)) {
                        if (hgs.Any(h => Math.Abs(h.DiameterMm - g.DiameterMm) < 0.05)) continue;
                        bool cb = map.Any(mp => Math.Abs(mp.Value.CbRadius * 2000 - g.DiameterMm) < 0.02 && Math.Abs(mp.Key.Cx - g.Leader.Cx) < 1e-5 && Math.Abs(mp.Key.Cy - g.Leader.Cy) < 1e-5)
                            || holes.Any(h => Math.Abs(h.CbRadius * 2000 - g.DiameterMm) < 0.02);
                        if (cb || DrawingPlan.ThreadFromEdges(new DwgEdge { Kind = DwgKind.Circle, Cx = g.Leader.Cx, Cy = g.Leader.Cy, R = 0 }, threads) == "M" + g.DiameterMm) continue;
                        var picked = DrawingPlan.PickSeparatedLeader(g.Circles, box, preferRight, usedLeaderY, toAnchorSheet, 0.008);
                        if (picked != null) g.Leader = picked;
                        o.Required++;
                        var ls2 = DwgWriter.ToSheet(v, g.Leader.Cx, g.Leader.Cy);
                        usedLeaderY.Add(ls2[1]);
                        string p2 = g.Count > 1 ? g.Count + "×" : "";
                        callouts.Add(new PlannedCallout {
                            Kind = CalloutKind.OuterRing, Hole = g, Pre = p2, Val = "", Suf = "", Sub = "",
                            Line1 = p2 + "Ø" + g.DiameterMm.ToString("0.##"), Line2 = "",
                            Lines = 1, BelowH = 0, Fx = ls2[0], Fy = ls2[1], AllowVoid = false
                        });
                    }
                    // 圆角：用圆弧真实中点作为锚点（不再用圆心），允许大窗口/槽口内无边空腔就近放
                    var radii = sectionView ? new List<RadiusGroup>() : DrawingPlan.Radii(e).Where(g => g.RadiusMm >= 0.3).OrderByDescending(g => g.Count).ThenByDescending(g => g.RadiusMm).ToList();
                    if (radii.Count > 10) { o.Notes.Add("圆弧太多（" + radii.Count + " 种半径，多半是刻字 / 花纹），只标了 10 种，请人工看一眼"); radii = radii.Take(10).ToList(); }
                    foreach (var g in radii) {
                        var picked = DrawingPlan.PickSeparatedLeader(g.Arcs, box, preferRight, usedLeaderY, toAnchorSheet, 0.007);
                        if (picked != null) g.Leader = picked;
                        o.Required++; o.Radii++;
                        var mid = DrawingPlan.ArcMidpoint(g.Leader);
                        var ls = DwgWriter.ToSheet(v, mid[0], mid[1]);
                        usedLeaderY.Add(ls[1]);
                        callouts.Add(new PlannedCallout {
                            Kind = CalloutKind.Radius, Rad = g,
                            Line1 = DrawingPlan.RadiusPrefix(g.Count) + "R" + g.RadiusMm.ToString("0.##"), Line2 = "",
                            Lines = 1, BelowH = 0, Fx = ls[0], Fy = ls[1], AllowVoid = DrawingPlan.AllowRadiusInVoid(g.RadiusMm, ls, cbox)
                        });
                    }
                    // 倒角：等于模板「未注倒角」那个值的不标（A3 模板写 C1，A4 写 C0.5）
                    double dflt = lay.Zones.Name == "A4" ? 0.5 : 1.0;
                    foreach (var g in sectionView ? new List<RadiusGroup>() : DrawingPlan.Chamfers(e)) {
                        if (Math.Abs(g.RadiusMm - dflt) < 0.01) continue;
                        var picked = DrawingPlan.PickSeparatedLeader(g.Arcs, box, preferRight, usedLeaderY, toAnchorSheet, 0.007);
                        if (picked != null) g.Leader = picked;
                        o.Required++;
                        var l = g.Leader;
                        var sp = DwgWriter.ToSheet(v, (l.X1 + l.X2) / 2, (l.Y1 + l.Y2) / 2);
                        usedLeaderY.Add(sp[1]);
                        callouts.Add(new PlannedCallout {
                            Kind = CalloutKind.Chamfer, Rad = g,
                            Line1 = DrawingPlan.ChamferText(g.Count, g.RadiusMm), Line2 = "",
                            Lines = 1, BelowH = 0, Fx = sp[0], Fy = sp[1], AllowVoid = false
                        });
                    }
                }

                // 按锚点 Y 从上到下统一排序落位（细长水平条视图所有孔几乎同 Y 时，按 X 从左到右排，杜绝 02-023 上方引线互相交叉）
                bool thinHoriz = (cbox[3] - cbox[1]) < 0.014 && (cbox[2] - cbox[0]) > 0.060;
                var orderedCallouts = thinHoriz
                    ? callouts.OrderBy(c => c.Fx).ThenByDescending(c => c.Fy).ToList()
                    : callouts.OrderByDescending(c => c.Fy).ThenByDescending(c => c.Fx).ToList();
                foreach (var item in orderedCallouts) {
                    double[] placedBox;
                    var text = SpotCompact(w, item.Fx, item.Fy, cbox, ownObs, item.Line1, item.Line2, item.Lines, item.BelowH, preferRight, item.AllowVoid, geomClear, ref fallY, out placedBox);
                    if (item.Kind == CalloutKind.Hole) {
                        if (w.HoleCallout(v, item.Hole, item.Pre, item.Val, item.Suf, item.Sub, text)) {
                            o.Covered++; o.HoleCallouts++;
                            if (item.Pin && w.Fit(w.LastCallout, item.Hole.DiameterMm)) {
                                pinGroups++; pinCount += item.Hole.Count; pins.Add(item.PinHole);
                                w.Position(w.LastCallout);
                            }
                        }
                    } else if (item.Kind == CalloutKind.OuterRing) {
                        if (w.HoleCallout(v, item.Hole, item.Pre, "", "", "", text)) o.Covered++;
                    } else if (item.Kind == CalloutKind.Radius) {
                        if (w.Radius(v, item.Rad, text)) o.Covered++;
                    } else if (item.Kind == CalloutKind.Chamfer) {
                        // AddChamfer(Perpendicular) 的末关键点 KP[2] 是文字中心而非引线折点，传入放置框中心可彻底避免文字回缩蹭到零件角点
                        double[] chTarget = placedBox != null ? new[] { (placedBox[0] + placedBox[2]) / 2, placedBox[1] + 0.0038 } : text;
                        if (w.Chamfer(v, item.Rad, e, chTarget)) o.Covered++;
                    }
                }
            }
            // 销孔所在的面（装配时贴着别的零件、靠销定位的安装面）标 Ra3.2：在孔是侧面的那个视图里找过孔口的那条面线
            bool rough = pins.Count > 0 && MatingFaces(w, views, edges, pins, range) > 0;
            string tn = DrawingTolerance.Note(pinGroups, pinCount, rough);
            if (tn.Length > 0) o.Notes.Insert(0, tn);
        }

        enum CalloutKind { Hole, OuterRing, Radius, Chamfer }
        sealed class PlannedCallout {
            public CalloutKind Kind;
            public HoleGroup Hole;
            public RadiusGroup Rad;
            public string Pre, Val, Suf, Sub, Line1, Line2;
            public bool Pin, AllowVoid;
            public ModelHole PinHole;
            public int Lines;
            public double BelowH, Fx, Fy;
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
        // 引线文字紧凑落位：使用实测校准宽度 CalloutWidth、主行+Subfix 均在横线上方的正确高度、视图实际轮廓框 cbox，
        // 经 DwgSpace.PlaceCompact 就近避让（含大空腔圆角净空检验与引线防交叉）；实在放不下才按行高累计向下排。
        static double[] SpotCompact(DwgWriter w, double fx, double fy, double[] cbox, double[] ownObs, string line1, string line2, int lines, double belowH, bool preferRight, bool allowVoid, Func<double[], bool> geomClear, ref double fallY, out double[] placedBox){
            placedBox = null;
            double tw = DwgSpace.CalloutWidth(line1, line2);
            double th = 0.0052 * Math.Max(1, lines) + belowH + 0.0014;
            if (w.Space != null) {
                double[] placed, elbow;
                if (w.Space.PlaceCompact(fx, fy, cbox, ownObs, tw, th, belowH, preferRight, allowVoid, geomClear, out placed, out elbow)) {
                    if (placed[0] >= cbox[2] - 1e-6) fallY = Math.Min(fallY, placed[1] - 0.0025);
                    placedBox = placed;
                    return elbow;
                }
            }
            double boxY0 = fallY - th;
            fallY -= th + 0.0025;
            placedBox = new[] { cbox[2] + 0.006, boxY0, cbox[2] + 0.006 + tw, boxY0 + th };
            return new[] { cbox[2] + 0.006, DrawingStyleRules.CalloutShelfY(boxY0, belowH) };
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
                double[] at = null;
                string text = "刻字：%{文件名（无扩展名）|R1}";
                if (tpl != null) {
                    try { text = tpl.BalloonText; } catch { }
                    try { double a, b, c, d; tpl.Range(out a, out b, out c, out d); at = new[] { a, (b + d) / 2 }; } catch { }
                }
                if (at == null) { var ib = DwgWriter.SheetBox(iso); at = new[] { ib[0] - 0.06, 0.050 }; }
                // 在轴测图够长的边（≥ 最长边 25%）里挑中点离刻字文字位置最近的那条，让刻字引线短而干净、不斜穿整个轴测图和孔标注
                double minLen = edges[0].Length * 0.25;
                var cands = edges.Where(x => x.Length >= minLen).ToList();
                var best = cands[0];
                double bestD2 = double.MaxValue;
                foreach (var ed in cands) {
                    var mp = DwgWriter.ToSheet(iso, (ed.X1 + ed.X2) / 2, (ed.Y1 + ed.Y2) / 2);
                    double dx = mp[0] - at[0], dy = mp[1] - at[1];
                    double d2 = dx * dx + dy * dy;
                    if (d2 < bestD2) { bestD2 = d2; best = ed; }
                }
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
