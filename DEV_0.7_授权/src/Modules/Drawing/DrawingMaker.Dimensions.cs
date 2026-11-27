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
        // 主视图：上方一串水平坐标、左边一串竖直坐标；孔 / 圆角 / 腰孔 / PCD 分度圆 / 斜面角按空位紧凑排开。
        // 全视图共享同一套三维 ModelDatum3D (Ox, Oy, Oz) 基准：俯视/左视/右视/后视/剖视一律投影该三维基准，绝不换边。
        // 车床回转件自动改走水平主视/剖视直接标 Ø 外径/内径 + 阶梯式轴向线性尺寸；铝型材/框架自动屏蔽 T 槽内线。
        static void Annotate(D.DraftDocument dft, D.Sheet sheet1, DwgWriter w, Dictionary<DwgViewRole, D.DrawingView> views, DwgLayout lay, List<ModelHole> holes, double[] range, ModelFacts facts, ModelDatum3D datum3D, DrawingOptions opt, string centerStyle, PendingBreakPlan pendingBreak, F.Application app, DrawingOutcome o, out Wave2Carry wave2){
            wave2 = new Wave2Carry();
            var brokenStates = new Dictionary<DwgViewRole, BrokenViewState>();
            var postBreakActions = new List<Action>();
            if (pendingBreak != null) w.UnbrokenTempMode = true;
            Func<D.DrawingView, bool, int> axisIdxOf = (vv, hz) => {
                try {
                    double dx, dy, dz, ux, uy, uz; D.ViewOrientationConstants kk; vv.ViewOrientation(out dx, out dy, out dz, out ux, out uy, out uz, out kk);
                    var xv = new V3(ux, uy, uz); var yv = new V3(dx, dy, dz).Cross(xv); var a = hz ? xv : yv;
                    double[] c = { Math.Abs(a.X), Math.Abs(a.Y), Math.Abs(a.Z) };
                    return c[0] >= c[1] && c[0] >= c[2] ? 0 : c[1] >= c[2] ? 1 : 2;
                } catch { return -1; }
            };
            Func<D.DrawingView, bool, double> ctOf = (vv, hz) => { int idx = axisIdxOf(vv, hz); return idx >= 0 ? facts.EdgeTol[idx] : 0; };
            Func<D.DrawingView, bool, IList<double>> offsOf = (vv, hz) => { int idx = axisIdxOf(vv, hz); return idx >= 0 ? (IList<double>)facts.EdgeOffsets[idx] : new double[0]; };
            const double off = 0.0075;   // 坐标串 / 单跨线性尺寸文字线离视图轮廓（纸面 7.5mm，与公司人图 6.5~8mm 一致，紧凑美观）
            bool isFrame = facts.PartKind == DrawingPartKind.ProfileFrame;
            bool isSingleProfile = facts.PartKind == DrawingPartKind.ProfileSingle && facts.ProfileExtrusionAxis >= 0;
            bool isLathe = facts.PartKind == DrawingPartKind.Lathe && facts.RevAxis >= 0;

            var rawEdges = new Dictionary<DwgViewRole, List<DwgEdge>>();
            var edges = new Dictionary<DwgViewRole, List<DwgEdge>>();
            var ordEdges = new Dictionary<DwgViewRole, List<DwgEdge>>();
            var viewCboxes = new Dictionary<DwgViewRole, double[]>();
            var unbrokenCboxes = new Dictionary<DwgViewRole, double[]>();
            var viewObs = new Dictionary<DwgViewRole, double[]>();
            var viewFrames = new Dictionary<DwgViewRole, ViewFrameInfo>();
            var viewEndProfile = new Dictionary<DwgViewRole, bool>();
            var viewPcds = new Dictionary<DwgViewRole, List<KeyValuePair<HoleGroup, PcdPattern>>>();
            var viewSlots = new Dictionary<DwgViewRole, List<SlotGroup>>();

            foreach (var kv in views) if (kv.Key != DwgViewRole.Iso) {
                bool isSection = false; try { isSection = kv.Value.DrawingViewType == D.DrawingViewTypeConstants.igXSectionView; } catch { }
                var raw = DrawingViewRead.Edges(kv.Value);
                rawEdges[kv.Key] = raw;
                var vBox = DrawingPlan.Box(raw);
                var filt = HoleInternal(kv.Value, raw, isSection ? holes.Where(h => !ModelFacts.IsStepped(h)).ToList() : holes, range);

                // 读取视图坐标系并映射 3D → 2D（供统一 XYZ 基准投影与型材方向判定）
                V3 fx3, fy3, fd3; double fou, fov;
                bool okFrame = Frame(kv.Value, raw, range, out fx3, out fy3, out fd3, out fou, out fov);
                int axX = axisIdxOf(kv.Value, true), axY = axisIdxOf(kv.Value, false);
                int axD = DominantAxis(fd3);
                viewFrames[kv.Key] = new ViewFrameInfo { Valid = okFrame, X = fx3, Y = fy3, D = fd3, Ou = fou, Ov = fov, AxisX = axX, AxisY = axY, AxisD = axD };

                // 单根铝型材：彻底屏蔽 T 型槽与中空内腔平行线，保留外框、总长与机加孔/切口
                bool endProf = false;
                if (isSingleProfile) {
                    endProf = (axD == facts.ProfileExtrusionAxis);
                    bool extAlongX = (axX == facts.ProfileExtrusionAxis);
                    filt = DrawingProfileRules.FilterSingleProfileView(filt, endProf, extAlongX, vBox);
                }
                viewEndProfile[kv.Key] = endProf;
                edges[kv.Key] = filt;

                // 预先识别本视图的 PCD 圆周均布孔与腰孔：从坐标串里剔除 PCD 小数 XY 孔心和腰孔平行直壁，避免坐标爆炸
                var pcdList = new List<KeyValuePair<HoleGroup, PcdPattern>>();
                var skipForOrd = new HashSet<DwgEdge>();
                double maxD0 = Math.Min(vBox[2] - vBox[0], vBox[3] - vBox[1]) * 1.01;
                if (!isSection && !endProf) {
                    var preMap = HoleMap(kv.Value, filt, holes, range);
                    var preThreads = DrawingViewRead.Threads(kv.Value);
                    var preAxisHoles = holes.Where(h => Math.Abs(Math.Abs(h.Axis.Dot(fd3)) - 1) < 1e-4).ToList();
                    foreach (var rg in DrawingPlan.Holes(filt, maxD0)) {
                        var subs = DrawingPlan.SplitHoleGroupByKey(rg, c => HoleGroupKey(c, rg.DiameterMm, preMap, preThreads, preAxisHoles));
                        foreach (var sg in subs) {
                            var pcd = DrawingFeatureRules.TryDetectPcd(sg, filt);
                            if (pcd != null) {
                                pcdList.Add(new KeyValuePair<HoleGroup, PcdPattern>(sg, pcd));
                                foreach (var hc in pcd.Holes) skipForOrd.Add(hc);
                            }
                        }
                    }
                }
                viewPcds[kv.Key] = pcdList;

                var slots = (!isSection && !endProf) ? DrawingFeatureRules.FindSlots(filt) : new List<SlotGroup>();
                viewSlots[kv.Key] = slots;
                foreach (var sg in slots) {
                    foreach (var sf in sg.Slots) {
                        if (sf.Line1 != null) skipForOrd.Add(sf.Line1);
                        if (sf.Line2 != null) skipForOrd.Add(sf.Line2);
                    }
                }
                ordEdges[kv.Key] = skipForOrd.Count > 0 ? filt.Where(ed => !skipForOrd.Contains(ed)).ToList() : filt;

                var p0 = DwgWriter.ToSheet(kv.Value, vBox[0], vBox[1]);
                var p1 = DwgWriter.ToSheet(kv.Value, vBox[2], vBox[3]);
                var ucb = new[] { Math.Min(p0[0], p1[0]), Math.Min(p0[1], p1[1]), Math.Max(p0[0], p1[0]), Math.Max(p0[1], p1[1]) };
                unbrokenCboxes[kv.Key] = ucb;
                var cb = ClampBoxForBrokenView(kv.Value, ucb);
                var ob = new[] { cb[0] - 0.002, cb[1] - 0.002, cb[2] + 0.002, cb[3] + 0.002 };
                viewCboxes[kv.Key] = cb;
                viewObs[kv.Key] = ob;
                if (w.Space != null && pendingBreak == null) {
                    w.Space.AddView(ob);
                    // 剖视图上方留出「剖面 A-A 1:1」标题区，避免引线文字压住剖面标题
                    if (isSection) w.Space.Add(cb[0] + 0.002, cb[3] + 0.001, cb[2] - 0.002, cb[3] + 0.014);
                }
            }

            // 辅助函数：将全局 3D XYZ 基准投影为任意视图水平 / 竖直方向上的强制 0 坐标
            Func<DwgViewRole, bool, double?> forcedOriginOf = (role, horiz) => {
                if (datum3D == null || !viewFrames.ContainsKey(role)) return null;
                var vf = viewFrames[role];
                if (!vf.Valid) return null;
                int ax = horiz ? vf.AxisX : vf.AxisY;
                if (ax < 0) return null;
                return datum3D.ProjectToView(ax, horiz ? vf.X : vf.Y, horiz ? vf.Ou : vf.Ov);
            };

            // 两遍对称一致性校验：若某轴计划采用对称中心作 0，核查主视图及会标该轴的投影视图在中心处是否都有可挂 0 的几何落点；
            // 只要任一需要标该轴的视图中心落空，就全局统一回退到外轮廓端面 EdgeCoord，确保全图 XYZ 基准 100% 统一。
            if (datum3D != null && !isLathe) {
                EnsureDatumConsistency(datum3D, views, ordEdges, viewFrames, facts, ctOf, offsOf);
            }

            var main = views[DwgViewRole.Main];
            var em = ordEdges[DwgViewRole.Main];
            var boxM = DrawingPlan.Box(em.Count > 0 ? em : edges[DwgViewRole.Main]);

            // Wave 2：多孔大板（≥16 孔）在标主视图坐标串前切换为规范孔表（坐标相对全局 3D 基准 0,0），主视图只保留清爽外形/台阶坐标
            var pcdOnMain = new HashSet<DwgEdge>();
            if (viewPcds.ContainsKey(DwgViewRole.Main)) {
                foreach (var pair in viewPcds[DwgViewRole.Main]) foreach (var hc in pair.Value.Holes) pcdOnMain.Add(hc);
            }
            double oxMain = forcedOriginOf(DwgViewRole.Main, true) ?? boxM[0];
            double oyMain = forcedOriginOf(DwgViewRole.Main, false) ?? boxM[1];
            List<HoleTableRow> htRows;
            var holeTableCircles = PlanMainHoleTable(main, edges[DwgViewRole.Main], boxM, holes, range, facts, opt, pcdOnMain, oxMain, oyMain, out htRows);
            if (holeTableCircles.Count > 0) {
                wave2.HoleRows = htRows;
                wave2.MainOriginXView = oxMain;
                wave2.MainOriginYView = oyMain;
                ordEdges[DwgViewRole.Main] = em.Where(ed => !holeTableCircles.Contains(ed)).ToList();
                em = ordEdges[DwgViewRole.Main];
                if (em.Count > 0) boxM = DrawingPlan.Box(em);
            }

            var tolStats = new Wave3TolStats();
            if (isLathe) {
                AnnotateLathe(w, views, lay, edges, ordEdges, viewCboxes, unbrokenCboxes, brokenStates, postBreakActions, pendingBreak != null, viewFrames, holes, facts, datum3D, opt, tolStats, ctOf, offsOf, off, o);
            } else {
                // 主视图两串（强制锁全局 3D 基准；纯同心圆端面跳过冗余 6-0-6）
                var runH = DrawingPlan.Ordinate(em, true, lay.Scale, boxM, false, off, ctOf(main, true), offsOf(main, true), facts.SheetMetal, forcedOriginOf(DwgViewRole.Main, true), isFrame);
                var runV = DrawingPlan.Ordinate(em, false, lay.Scale, boxM, true, off, ctOf(main, false), offsOf(main, false), facts.SheetMetal, forcedOriginOf(DwgViewRole.Main, false), isFrame);
                if (DrawingPlan.IsRedundantCircleSpan(runH)) runH = null;
                if (DrawingPlan.IsRedundantCircleSpan(runV)) runV = null;
                Count(o, runH); Count(o, runV);
                o.Covered += Covered(w.Ordinate(main, runH, lay.Scale, boxM)) + Covered(w.Ordinate(main, runV, lay.Scale, boxM));
                var sheetXs = Sheet(main, runH, true); var sheetYs = Sheet(main, runV, false);
                bool depthDone = false;
                D.DrawingView depthRefView = null;
                double[] depthRefBox = null;
                bool depthRefHoriz = false;
                var sheetDepths = new List<double>();
                D.DrawingView vb;
                if (views.TryGetValue(DwgViewRole.Below, out vb)) {
                    var eb = ordEdges[DwgViewRole.Below]; var boxB = DrawingPlan.Box(eb.Count > 0 ? eb : edges[DwgViewRole.Below]);
                    var rv = DrawingPlan.Ordinate(eb, false, lay.Scale, boxB, true, off, ctOf(vb, false), offsOf(vb, false), facts.SheetMetal, forcedOriginOf(DwgViewRole.Below, false), isFrame);
                    if (DrawingPlan.IsRedundantCircleSpan(rv)) rv = null;
                    Count(o, rv); o.Covered += Covered(w.Ordinate(vb, rv, lay.Scale, boxB)); depthDone = rv != null;
                    if (rv != null) { depthRefView = vb; depthRefBox = boxB; depthRefHoriz = false; sheetDepths = Sheet(vb, rv, false); }
                    var rh = Only(DrawingPlan.Ordinate(eb, true, lay.Scale, boxB, true, off, ctOf(vb, true), offsOf(vb, true), facts.SheetMetal, forcedOriginOf(DwgViewRole.Below, true), isFrame), vb, sheetXs, true, main, boxM, boxB, forcedOriginOf(DwgViewRole.Below, true));
                    if (DrawingPlan.IsRedundantCircleSpan(rh)) rh = null;
                    Count(o, rh); o.Covered += Covered(w.Ordinate(vb, rh, lay.Scale, boxB));
                }
                D.DrawingView vr;
                if (views.TryGetValue(DwgViewRole.Right, out vr)) {
                    var er = ordEdges[DwgViewRole.Right]; var boxR = DrawingPlan.Box(er.Count > 0 ? er : edges[DwgViewRole.Right]);
                    if (!depthDone) {
                        var rh = DrawingPlan.Ordinate(er, true, lay.Scale, boxR, true, off, ctOf(vr, true), offsOf(vr, true), facts.SheetMetal, forcedOriginOf(DwgViewRole.Right, true), isFrame);
                        if (DrawingPlan.IsRedundantCircleSpan(rh)) rh = null;
                        Count(o, rh); o.Covered += Covered(w.Ordinate(vr, rh, lay.Scale, boxR));
                        if (rh != null) { depthDone = true; depthRefView = vr; depthRefBox = boxR; depthRefHoriz = true; sheetDepths = Sheet(vr, rh, true); }
                    }
                    var rv = Only(DrawingPlan.Ordinate(er, false, lay.Scale, boxR, false, off, ctOf(vr, false), offsOf(vr, false), facts.SheetMetal, forcedOriginOf(DwgViewRole.Right, false), isFrame), vr, sheetYs, false, main, boxM, boxR, forcedOriginOf(DwgViewRole.Right, false));
                    if (DrawingPlan.IsRedundantCircleSpan(rv)) rv = null;
                    Count(o, rv); o.Covered += Covered(w.Ordinate(vr, rv, lay.Scale, boxR));
                }
                // 补的反方向视图（右视图 Left / 后视图 Back）：严格投影全局 3D 基准（镜像宁可 0 在右侧也不换边）
                foreach (var role in new[] { DwgViewRole.Left, DwgViewRole.Back }) {
                    D.DrawingView ve; if (!views.TryGetValue(role, out ve)) continue;
                    var ee = ordEdges[role]; var boxE = DrawingPlan.Box(ee.Count > 0 ? ee : edges[role]);
                    var fh = forcedOriginOf(role, true);
                    var rawRh = DrawingPlan.Ordinate(ee, true, lay.Scale, boxE, true, off, ctOf(ve, true), offsOf(ve, true), facts.SheetMetal, fh, isFrame);
                    OrdinateRun rh;
                    if (role == DwgViewRole.Left) {
                        rh = depthRefView != null
                            ? OnlyAxis(rawRh, ve, true, sheetDepths, depthRefView, depthRefHoriz, depthRefBox, boxE, fh)
                            : rawRh;
                    } else {
                        rh = Only(rawRh, ve, sheetXs, true, main, boxM, boxE, fh);
                    }
                    if (DrawingPlan.IsRedundantCircleSpan(rh)) rh = null;
                    Count(o, rh); o.Covered += Covered(w.Ordinate(ve, rh, lay.Scale, boxE));
                    var fv = forcedOriginOf(role, false);
                    var rv = Only(DrawingPlan.Ordinate(ee, false, lay.Scale, boxE, role == DwgViewRole.Left, off, ctOf(ve, false), offsOf(ve, false), facts.SheetMetal, fv, isFrame), ve, sheetYs, false, main, boxM, boxE, fv);
                    if (DrawingPlan.IsRedundantCircleSpan(rv)) rv = null;
                    Count(o, rv); o.Covered += Covered(w.Ordinate(ve, rv, lay.Scale, boxE));
                }
            }

            // 若为细长件计划断裂视图：此刻 Phase 1 坐标串与主线性直径已在未断裂视图上精准建完，
            // 立即插入 BreakLinePairs 并居中落位，天工自动收拢关联尺寸；随后按断裂后真实坐标重建碰撞空间并执行延迟引线标注。
            if (pendingBreak != null) {
                brokenStates = CommitBreakView(pendingBreak, sheet1, lay, app, o);
                w.UnbrokenTempMode = false;
                w.AuditBrokenSheetBounds();
                // 重建图纸碰撞空间（轴测图 + 刻字区 + 断裂落位后的全部视图框 + Phase 1 已收拢的真实尺寸框）
                var freshSpace = new DwgSpace(lay.Zones);
                D.DrawingView isoV;
                if (views.TryGetValue(DwgViewRole.Iso, out isoV) && isoV != null) {
                    var ib = DwgWriter.SheetBox(isoV);
                    freshSpace.AddView(new[] { ib[0] - 0.004, ib[1] - 0.004, ib[2] + 0.004, ib[3] + 0.004 });
                }
                foreach (var kv in views) {
                    if (kv.Key == DwgViewRole.Iso || !rawEdges.ContainsKey(kv.Key)) continue;
                    var vBox = DrawingPlan.Box(rawEdges[kv.Key]);
                    var p0 = DwgWriter.ToSheet(kv.Value, vBox[0], vBox[1]);
                    var p1 = DwgWriter.ToSheet(kv.Value, vBox[2], vBox[3]);
                    var ucb = new[] { Math.Min(p0[0], p1[0]), Math.Min(p0[1], p1[1]), Math.Max(p0[0], p1[0]), Math.Max(p0[1], p1[1]) };
                    double[] cb = ucb;
                    BrokenViewState bvs;
                    if (brokenStates.TryGetValue(kv.Key, out bvs)) {
                        cb = bvs.VerticalBreak
                            ? new[] { ucb[0] + bvs.ShiftLo, ucb[1], ucb[2] + bvs.ShiftHi, ucb[3] }
                            : new[] { ucb[0], ucb[1] + bvs.ShiftLo, ucb[2], ucb[3] + bvs.ShiftHi };
                        bvs.BrokenBox = cb;
                    }
                    var ob = new[] { cb[0] - 0.002, cb[1] - 0.002, cb[2] + 0.002, cb[3] + 0.002 };
                    viewCboxes[kv.Key] = cb;
                    viewObs[kv.Key] = ob;
                    freshSpace.AddView(ob);
                }
                try {
                    var allDims = (S.Dimensions)sheet1.Dimensions;
                    for (int i = 1; i <= allDims.Count; i++) {
                        try {
                            double dx0, dy0, dx1, dy1; allDims.Item(i).Range(out dx0, out dy0, out dx1, out dy1);
                            if (dx1 > dx0 + 1e-6 && dy1 > dy0 + 1e-6) freshSpace.Add(dx0, dy0, dx1, dy1);
                        } catch { }
                    }
                } catch { }
                w.Space = freshSpace;
                foreach (var act in postBreakActions) { try { act(); } catch { } }
            }

            // 孔、PCD 分度圆、腰孔、外圆、圆角、倒角、斜面角度：统一收集后按锚点排序紧凑落位
            var pins = new List<ModelHole>();
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
                var viewDir = SightAxis(v);
                var axisHoles = holes.Where(h => Math.Abs(Math.Abs(h.Axis.Dot(viewDir)) - 1) < 1e-4).ToList();
                bool viewHasModelHoles = axisHoles.Count > 0;
                bool endProf = viewEndProfile.ContainsKey(kv.Key) && viewEndProfile[kv.Key];
                double maxD = Math.Min(box[2] - box[0], box[3] - box[1]) * 1.01;
                double fallY = cbox[3] - 0.002;
                bool preferRight = true;
                var usedLeaderY = new List<double>();
                Func<DwgEdge, double[]> toAnchorSheet = ed => { var a = DrawingPlan.AnchorOf(ed); return DwgWriter.ToSheet(v, a[0], a[1]); };

                var sp0 = DwgWriter.ToSheet(v, rawBox[0], rawBox[1]);
                var sp1 = DwgWriter.ToSheet(v, rawBox[2], rawBox[3]);
                BrokenViewState bvsCallout;
                brokenStates.TryGetValue(kv.Key, out bvsCallout);
                Func<double[], bool> geomClear = null;
                if (bvsCallout == null && Math.Abs(sp1[0] - sp0[0]) > 1e-6 && Math.Abs(sp1[1] - sp0[1]) > 1e-6) {
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

                // 车床件端面视图：已由主/剖视图标过外径和中心通孔直径，这里仅标端面螺纹孔 / PCD 偏心孔 / 键槽
                bool latheEndView = isLathe && kv.Key == DwgViewRole.Right;
                var pcdList = viewPcds.ContainsKey(kv.Key) ? viewPcds[kv.Key] : new List<KeyValuePair<HoleGroup, PcdPattern>>();
                var pcdByCircle = new Dictionary<DwgEdge, PcdPattern>();
                foreach (var pair in pcdList) foreach (var hc in pair.Value.Holes) if (!pcdByCircle.ContainsKey(hc)) pcdByCircle[hc] = pair.Value;

                var callouts = new List<PlannedCallout>();
                var rawHgs = DrawingPlan.Holes(e, maxD);
                var hgs = new List<HoleGroup>();
                foreach (var rg in rawHgs) {
                    var subs = DrawingPlan.SplitHoleGroupByKey(rg, c => HoleGroupKey(c, rg.DiameterMm, map, threads, axisHoles));
                    hgs.AddRange(subs);
                }
                hgs = hgs.OrderByDescending(g => g.Count).ToList();
                if (hgs.Count > 12) { o.Notes.Add("孔的规格太多（" + hgs.Count + " 种），只标了 12 种，请人工补"); hgs = hgs.Take(12).ToList(); }
                bool extra = kv.Key == DwgViewRole.Left || kv.Key == DwgViewRole.Back;
                var drawnPcds = new HashSet<PcdPattern>();
                bool hasCompanionMounting = hgs.Any(hg => {
                    ModelHole cmh; map.TryGetValue(hg.Leader, out cmh);
                    if (cmh != null && (cmh.CbRadius > 0 || cmh.CsRadius > 0 || (cmh.Spec != null && cmh.Spec.Kind == HoleKind.Tapped))) return true;
                    if (DrawingPlan.ThreadFromEdges(hg.Leader, threads).Length > 0) return true;
                    return hg.DiameterMm >= 3.2 && hg.DiameterMm <= 14.0 && !DrawingTolerance.IsPinHole(hg.DiameterMm, true, false, false);
                });
                foreach (var g in hgs) {
                    if (kv.Key == DwgViewRole.Main && holeTableCircles.Count > 0 && g.Circles.All(holeTableCircles.Contains)) continue;
                    var picked = DrawingPlan.PickSeparatedLeader(g.Circles, box, preferRight, usedLeaderY, toAnchorSheet, 0.008);
                    if (picked != null) g.Leader = picked;
                    ModelHole mh; map.TryGetValue(g.Leader, out mh);
                    if (extra && (mh == null || mh.Through)) continue;
                    bool isHole = mh != null || (viewHasModelHoles && map.Count == 0);
                    string pre, val, suf, sub;
                    DrawingText.Hole(g.Count, mh, g.DiameterMm, isHole, out pre, out val, out suf, out sub);
                    if (val.Length == 0 && isHole) {
                        string m = DrawingPlan.ThreadFromEdges(g.Leader, threads);
                        if (m.Length > 0) { val = m; DrawingText.ThreadOnly(m, mh, out suf, out sub); }
                    }
                    // 单根铝型材端面图：仅标端面明确攻丝孔（M 几），不标型材自带的中心挤压空腔孔
                    if (endProf && val.Length == 0) continue;
                    // 车床件端面视图：若是无螺纹的纯同心外圆/已剖中心孔，避免与主/剖视图 Ø 重复
                    if (latheEndView && !isHole) continue;
                    if (isHole) o.Holes += g.Count;
                    o.Required++;

                    PcdPattern pcd = null;
                    if (g.Leader != null) pcdByCircle.TryGetValue(g.Leader, out pcd);
                    if (pcd != null && pcd.FullEqualSpacing && !suf.Contains("均布")) {
                        suf = suf + " 均布";
                    }

                    var pinHole = ResolvePinHole(mh, isHole, g.DiameterMm, axisHoles);
                    bool cbOrCsPin = pinHole != null && (pinHole.CbRadius > 0 || pinHole.CsRadius > 0
                        || (pinHole.Spec != null && (pinHole.Spec.Kind == HoleKind.Counterbore || pinHole.Spec.Kind == HoleKind.Countersink)));
                    bool pin = (opt == null || opt.TolPinHoles)
                        && DrawingTolerance.IsContextPinHole(g.DiameterMm, g.Count, isHole && pinHole != null, val.Length > 0, cbOrCsPin || sub.Length > 0, hasCompanionMounting);
                    // 标准轴承孔（如内孔自身就是带台阶/卡簧槽的 Ø28 轴承位）
                    bool hasConcentric = e.Any(oc => oc.Kind == DwgKind.Circle && !ReferenceEquals(oc, g.Leader)
                        && Math.Abs(oc.Cx - g.Leader.Cx) < 1e-4 && Math.Abs(oc.Cy - g.Leader.Cy) < 1e-4 && Math.Abs(oc.R - g.Leader.R) > 5e-4);
                    double holeDepthMm = mh != null ? DrawingStyleRules.NominalHoleDepthMm(mh) : 10.0;
                    bool bearingBore = !pin && (opt == null || opt.TolBearings)
                        && DrawingTolerance.IsBearingBore(g.DiameterMm, isHole, val.Length > 0, hasConcentric, holeDepthMm);

                    var ls = DwgWriter.ToSheet(v, g.Leader.Cx, g.Leader.Cy);
                    usedLeaderY.Add(ls[1]);
                    string shown = pre + (val.Length > 0 ? val : "Ø" + g.DiameterMm.ToString("0.##")) + ((pin || bearingBore) ? " H7(+0.021/0)" : "") + suf;
                    callouts.Add(new PlannedCallout {
                        Kind = CalloutKind.Hole, Hole = g, Pre = pre, Val = val, Suf = suf, Sub = sub,
                        Pin = pin, BearingBore = bearingBore, PinHole = pinHole, Line1 = shown, Line2 = sub,
                        Lines = sub.Length > 0 ? 2 : 1, BelowH = pin ? 0.0065 : 0.0,
                        Fx = ls[0], Fy = ls[1], AllowVoid = false
                    });
                    if (pcd != null && drawnPcds.Add(pcd)) {
                        o.Required++;
                        var ps = DwgWriter.ToSheet(v, pcd.Cx + pcd.Radius * 0.707, pcd.Cy + pcd.Radius * 0.707);
                        usedLeaderY.Add(ps[1]);
                        callouts.Add(new PlannedCallout {
                            Kind = CalloutKind.Pcd, Pcd = pcd,
                            Line1 = "Ø" + pcd.DiameterMm.ToString("0.##"), Line2 = "",
                            Lines = 1, BelowH = 0, Fx = ps[0], Fy = ps[1], AllowVoid = false
                        });
                    }
                }

                // 腰孔（长圆孔）标注
                var slotGroups = viewSlots.ContainsKey(kv.Key) ? viewSlots[kv.Key] : new List<SlotGroup>();
                if (!extra && !endProf) {
                    foreach (var sg in slotGroups) {
                        o.Required++;
                        var lead = sg.Leader;
                        var ls = DwgWriter.ToSheet(v, lead.Cx, lead.Cy);
                        usedLeaderY.Add(ls[1]);
                        string txt = isLathe
                            ? ((sg.Count > 1 ? sg.Count + "×" : "") + "键槽 " + sg.WidthMm.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "×" + sg.TotalLengthMm.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture))
                            : sg.CalloutText(true, 0);
                        callouts.Add(new PlannedCallout {
                            Kind = CalloutKind.Slot, Slot = sg, Line1 = txt, Line2 = "",
                            Lines = 1, BelowH = 0, Fx = ls[0], Fy = ls[1], AllowVoid = false
                        });
                    }
                }

                bool sectionView = false; try { sectionView = v.DrawingViewType == D.DrawingViewTypeConstants.igXSectionView; } catch { }
                if (!extra && !endProf) {
                    // 同心圆外圈：车床件端面视图跳过外圆（已在水平主视标 Ø）；若是轴承座大孔（如 02-003 的 Ø28），同步加 H7！
                    if (!latheEndView) {
                        foreach (var g in DrawingPlan.OuterRings(e, maxD)) {
                            if (kv.Key == DwgViewRole.Main && holeTableCircles.Count > 0 && g.Circles.All(holeTableCircles.Contains)) continue;
                            if (hgs.Any(h => Math.Abs(h.DiameterMm - g.DiameterMm) < 0.05)) continue;
                            bool cbOrCs = map.Any(mp =>
                                Math.Abs(mp.Key.Cx - g.Leader.Cx) < 1e-5 && Math.Abs(mp.Key.Cy - g.Leader.Cy) < 1e-5
                                && MatchesOuterMouth(mp.Value, g.DiameterMm));
                            if (!cbOrCs && map.Count == 0 && viewHasModelHoles) {
                                cbOrCs = axisHoles.Any(h => MatchesOuterMouth(h, g.DiameterMm));
                            }
                            string mNom = "M" + (Math.Round(g.DiameterMm * 2) / 2).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
                            if (cbOrCs || DrawingPlan.ThreadFromEdges(new DwgEdge { Kind = DwgKind.Circle, Cx = g.Leader.Cx, Cy = g.Leader.Cy, R = 0 }, threads) == mNom) continue;
                            var picked = DrawingPlan.PickSeparatedLeader(g.Circles, box, preferRight, usedLeaderY, toAnchorSheet, 0.008);
                            if (picked != null) g.Leader = picked;
                            o.Required++;
                            var ls2 = DwgWriter.ToSheet(v, g.Leader.Cx, g.Leader.Cy);
                            usedLeaderY.Add(ls2[1]);
                            string p2 = g.Count > 1 ? g.Count + "×" : "";
                            // 衬套块/轴承座同心台阶大孔（如 02-003 的 Ø28 轴承位）：必须确认为真实凹圆柱孔段（排除 02-010 Ø24 外凸台！）才标 H7
                            bool hasConcaveMatch = map.Any(mp =>
                                Math.Abs(mp.Key.Cx - g.Leader.Cx) < 1e-4 && Math.Abs(mp.Key.Cy - g.Leader.Cy) < 1e-4
                                && mp.Value != null && mp.Value.Segments.Any(sg => Math.Abs(sg.Radius - g.Leader.R) < 1e-4));
                            bool outerIsBearing = (opt == null || opt.TolBearings) && hasConcaveMatch
                                && e.Any(ic => ic.Kind == DwgKind.Circle && Math.Abs(ic.Cx - g.Leader.Cx) < 1e-4 && Math.Abs(ic.Cy - g.Leader.Cy) < 1e-4 && ic.R < g.Leader.R - 5e-4)
                                && DrawingTolerance.IsBearingBore(g.DiameterMm, true, false, true, Math.Max(6.0, facts.Size[Math.Max(0, viewFrames[kv.Key].AxisD)] * 500.0));
                            callouts.Add(new PlannedCallout {
                                Kind = CalloutKind.OuterRing, Hole = g, Pre = p2, Val = "", Suf = "", Sub = "",
                                BearingBore = outerIsBearing,
                                Line1 = p2 + "Ø" + g.DiameterMm.ToString("0.##") + (outerIsBearing ? " H7(+0.021/0)" : ""), Line2 = "",
                                Lines = 1, BelowH = 0, Fx = ls2[0], Fy = ls2[1], AllowVoid = false
                            });
                        }
                    }
                    var radii = sectionView ? new List<RadiusGroup>() : DrawingPlan.Radii(e).Where(g => g.RadiusMm >= 0.3).OrderByDescending(g => g.Count).ThenByDescending(g => g.RadiusMm).ToList();
                    // 型材框架自带大量铝型材挤压外圆角（如 28-R2 / 28-R4），非加工圆角不逐个标出
                    if (isFrame) radii = radii.Where(g => !(g.Count >= 10 && g.RadiusMm <= 5.0)).ToList();
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
                    // 斜面 / 锥尖角度标注
                    if (!sectionView && !latheEndView) {
                        foreach (var af in DrawingFeatureRules.FindAngles(e)) {
                            o.Required++;
                            if (w.Angle(v, af, cbox)) o.Covered++;
                        }
                    }
                }

                if (bvsCallout != null) {
                    foreach (var it in callouts) {
                        it.Fx = bvsCallout.MapCoord(it.Fx, true);
                        it.Fy = bvsCallout.MapCoord(it.Fy, false);
                    }
                }
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
                                tolStats.PinGroups++; tolStats.PinHoles += item.Hole.Count; pins.Add(item.PinHole);
                                string posFrame = (opt == null || opt.TolDatumsAndGdt) ? DrawingTolerance.PositionFrameDatumA : DrawingTolerance.PositionFrame;
                                w.Position(w.LastCallout, posFrame, placedBox);
                            } else if (item.BearingBore && w.Fit(w.LastCallout, item.Hole.DiameterMm)) {
                                tolStats.BearingBores += item.Hole.Count;
                            }
                        }
                    } else if (item.Kind == CalloutKind.Pcd) {
                        if (w.PitchCircle(dft, v, item.Pcd, centerStyle, text)) {
                            o.Covered++;
                            if (item.Pcd.NeedsStartAngle) o.Notes.Add("分度圆 Ø" + item.Pcd.DiameterMm + " 起始角 " + item.Pcd.StartAngleDeg + "°，请确认是否补标定位角");
                        }
                    } else if (item.Kind == CalloutKind.Slot) {
                        var lead = item.Slot.Leader;
                        var anchorEdge = lead.Arc1 ?? lead.Line1;
                        var mp = DrawingPlan.ArcMidpoint(anchorEdge);
                        double[] anchorS = null;
                        if (bvsCallout != null) {
                            var rs = DwgWriter.ToSheet(v, mp[0], mp[1]);
                            anchorS = new[] { bvsCallout.MapCoord(rs[0], true), bvsCallout.MapCoord(rs[1], false) };
                        }
                        if (w.Note(v, anchorEdge, mp[0], mp[1], item.Line1, text, anchorS) != null) o.Covered++;
                    } else if (item.Kind == CalloutKind.OuterRing) {
                        if (w.HoleCallout(v, item.Hole, item.Pre, "", "", "", text)) {
                            o.Covered++;
                            if (item.BearingBore && w.Fit(w.LastCallout, item.Hole.DiameterMm)) tolStats.BearingBores += item.Hole.Count;
                        }
                    } else if (item.Kind == CalloutKind.Radius) {
                        if (w.Radius(v, item.Rad, text)) o.Covered++;
                    } else if (item.Kind == CalloutKind.Chamfer) {
                        double[] chTarget = placedBox != null ? new[] { (placedBox[0] + placedBox[2]) * 0.5, (placedBox[1] + placedBox[3]) * 0.5 } : text;
                        if (w.Chamfer(v, item.Rad, e, chTarget)) o.Covered++;
                    }
                }
                if (kv.Key == DwgViewRole.Main) {
                    if (wave2.HoleRows.Count > 0) {
                        PlaceMainHoleTags(dft, sheet1, w, v, cbox, wave2, o);
                        int htPins = wave2.HoleRows.Count(r => r.IsPinHole);
                        if (htPins > 0) {
                            tolStats.PinGroups++;
                            tolStats.PinHoles += htPins;
                        }
                    } else if (opt != null && opt.AutoDetailView && !isLathe && !isSingleProfile) {
                        var mainCircles = e.Where(ed => ed.Kind == DwgKind.Circle && !pcdOnMain.Contains(ed)).ToList();
                        wave2.DetailClusters = DrawingWave2Rules.FindDetailClusters(mainCircles, box, lay.Scale, c => {
                            ModelHole cmh; map.TryGetValue(c, out cmh);
                            return cmh != null && DrawingTolerance.IsPinHole(c.R * 2000.0, true, cmh.Spec != null && cmh.Spec.Kind == HoleKind.Tapped, cmh.CbRadius > 0 || cmh.CsRadius > 0);
                        }, 2);
                    }
                }
            }
            ApplySmartPlateAndBearingTolerances(w, views, edges, viewCboxes, holes, pins, range, facts, opt, tolStats);
            string tn = DrawingTolerance.SummaryNote(tolStats.PinGroups, tolStats.PinHoles, tolStats.BearingBores, tolStats.BearingShafts, tolStats.GuidePins, tolStats.HasGuideRail, tolStats.HasDatumA, tolStats.HasRoughness);
            if (tn.Length > 0) o.Notes.Insert(0, tn);
        }

        sealed class BrokenViewState {
            public D.DrawingView View;
            public double[] BrokenBox;
            public bool VerticalBreak;
            public double CutMidSheet;
            public double ShiftLo, ShiftHi;
            public double MapCoord(double sheetPos, bool horiz){
                if (horiz != VerticalBreak) return sheetPos;
                return sheetPos < CutMidSheet ? (sheetPos + ShiftLo) : (sheetPos + ShiftHi);
            }
        }

        sealed class ViewFrameInfo {
            public bool Valid;
            public V3 X, Y, D;
            public double Ou, Ov;
            public int AxisX, AxisY, AxisD;
        }

        static int DominantAxis(V3 v){
            double ax = Math.Abs(v.X), ay = Math.Abs(v.Y), az = Math.Abs(v.Z);
            return ax >= ay && ax >= az ? 0 : (ay >= az ? 1 : 2);
        }

        static string HoleGroupKey(DwgEdge c, double diaMm, Dictionary<DwgEdge, ModelHole> map, List<DwgEdge> threads, List<ModelHole> axisHoles){
            ModelHole cmh; map.TryGetValue(c, out cmh);
            bool cisHole = cmh != null || (axisHoles.Count > 0 && map.Count == 0);
            string cpre, cval, csuf, csub;
            DrawingText.Hole(1, cmh, diaMm, cisHole, out cpre, out cval, out csuf, out csub);
            if (cval.Length == 0 && cisHole) {
                string cm = DrawingPlan.ThreadFromEdges(c, threads);
                if (cm.Length > 0) { cval = cm; DrawingText.ThreadOnly(cm, cmh, out csuf, out csub); }
            }
            var cpinHole = ResolvePinHole(cmh, cisHole, diaMm, axisHoles);
            bool cpin = IsPinHoleCandidate(diaMm, cisHole, cpinHole, cval, csub);
            return (cisHole ? "H" : "B") + "|" + cval + "|" + csuf + "|" + csub + "|" + (cpin ? "P" : "");
        }

        // 检查开启了对称中心的轴在主视图（以及确实会标该轴的副视图）中是否都能挂上 0 基准；
        // 只要任一主/关键视图在投影中心处没有几何落点，就统一回退该轴到外轮廓边，杜绝跨视图基准漂移。
        static void EnsureDatumConsistency(ModelDatum3D datum3D, Dictionary<DwgViewRole, D.DrawingView> views,
            Dictionary<DwgViewRole, List<DwgEdge>> ordEdges, Dictionary<DwgViewRole, ViewFrameInfo> frames,
            ModelFacts facts, Func<D.DrawingView, bool, double> ctOf, Func<D.DrawingView, bool, IList<double>> offsOf){
            for (int a = 0; a < 3; a++) {
                if (!datum3D.Symmetric[a]) continue;
                bool ok = true;
                foreach (var kv in views) {
                    if (kv.Key == DwgViewRole.Iso || !frames.ContainsKey(kv.Key) || !ordEdges.ContainsKey(kv.Key)) continue;
                    var vf = frames[kv.Key];
                    if (!vf.Valid) continue;
                    bool checkH = (vf.AxisX == a), checkV = (vf.AxisY == a);
                    if (!checkH && !checkV) continue;
                    // 只对主视图及必然独立标该轴的视图（如深度轴落在 Below/Right）做硬性校验
                    bool mustMatch = kv.Key == DwgViewRole.Main
                        || (kv.Key == DwgViewRole.Below && checkV)
                        || (kv.Key == DwgViewRole.Right && checkH);
                    if (!mustMatch) continue;
                    var es = ordEdges[kv.Key];
                    var box = DrawingPlan.Box(es);
                    if (checkH) {
                        var ts = DrawingPlan.DropNearOutline(DrawingPlan.Targets(es, true, false), es, true, box, ctOf(kv.Value, true), offsOf(kv.Value, true), facts.SheetMetal);
                        double pc = datum3D.ProjectToView(a, vf.X, vf.Ou);
                        if (!DrawingPlan.HasTargetNear(ts, pc, 2e-4)) { ok = false; break; }
                    }
                    if (checkV) {
                        var ts = DrawingPlan.DropNearOutline(DrawingPlan.Targets(es, false, true), es, false, box, ctOf(kv.Value, false), offsOf(kv.Value, false), facts.SheetMetal);
                        double pc = datum3D.ProjectToView(a, vf.Y, vf.Ov);
                        if (!DrawingPlan.HasTargetNear(ts, pc, 2e-4)) { ok = false; break; }
                    }
                }
                if (!ok) datum3D.FallbackToEdge(a);
            }
        }

        // 车床回转件标注管线：
        //   · 在水平主视图（若存在剖视图 Below 则内孔在剖视图标、外圆在主视图或剖视图标）直接标各段 Ø 直径，
        //     并按 Wave 3 单零件规则自动附加轴承轴颈 k6 / 定位销 g6 / 轴承孔 H7、主轴基准 A 与同轴度 ◎Ø0.02 A、Ra1.6；
        //   · 轴向台阶：径向孔 X 坐标 < 4 处时走阶梯式线性尺寸（内层台阶段长 + 外层总长，留出开放环）；
        //     若存在密集径向孔（≥ 4 处），自动切回统一 3D 基准坐标串。
        static void AnnotateLathe(DwgWriter w, Dictionary<DwgViewRole, D.DrawingView> views, DwgLayout lay,
            Dictionary<DwgViewRole, List<DwgEdge>> edges, Dictionary<DwgViewRole, List<DwgEdge>> ordEdges,
            Dictionary<DwgViewRole, double[]> cboxes, Dictionary<DwgViewRole, double[]> unbrokenCboxes,
            Dictionary<DwgViewRole, BrokenViewState> brokenStates, List<Action> postBreakActions, bool deferCallouts,
            Dictionary<DwgViewRole, ViewFrameInfo> frames,
            List<ModelHole> holes, ModelFacts facts, ModelDatum3D datum3D, DrawingOptions opt, Wave3TolStats tolStats,
            Func<D.DrawingView, bool, double> ctOf, Func<D.DrawingView, bool, IList<double>> offsOf, double off, DrawingOutcome o){
            var main = views[DwgViewRole.Main];
            var em = ordEdges[DwgViewRole.Main];
            var boxM = DrawingPlan.Box(em.Count > 0 ? em : edges[DwgViewRole.Main]);
            double axisY = (boxM[1] + boxM[3]) / 2.0;
            double totalLenMm = (boxM[2] - boxM[0]) * 1000.0;
            var mainUnbrokenBox = unbrokenCboxes.ContainsKey(DwgViewRole.Main) ? unbrokenCboxes[DwgViewRole.Main] : cboxes[DwgViewRole.Main];

            // 1. 直径标注（外径放主视图右/左侧，中部窄卡簧槽/退刀槽在下方拉干净引线，剖视内孔放剖视图右侧）
            var odPairs = DrawingLatheRules.ExtractDiameters(edges[DwgViewRole.Main], axisY, false, facts.RevMaxOuterDiameter);
            int leftLayer = 0, rightLayer = 0, undercutLayer = 0;
            double midX = (boxM[0] + boxM[2]) / 2.0;
            LatheDiameterPair mainBodyPair = odPairs.OrderByDescending(p => p.DiameterM).ThenByDescending(p => p.XMax - p.XMin).FirstOrDefault();
            var fittedShaftPairs = new List<LatheDiameterPair>();
            foreach (var p in odPairs) {
                bool onRight = (p.XMin + p.XMax) / 2.0 >= midX;
                string fitClass = (opt == null || opt.TolBearings) ? DrawingTolerance.ClassifyShaftDiameterFit(p, odPairs, totalLenMm) : "";
                o.Required++;
                double segMidX = (p.XMin + p.XMax) * 0.5;
                var segPt = DwgWriter.ToSheet(main, segMidX, p.BottomEdge.Y1);
                bool deepInteriorUndercut = p.IsUndercut
                    && (segPt[0] - mainUnbrokenBox[0] > 0.014) && (mainUnbrokenBox[2] - segPt[0] > 0.014);
                if (deepInteriorUndercut) {
                    var capP = p;
                    int capLayer = undercutLayer++;
                    Action drawUndercut = () => {
                        var mc = cboxes[DwgViewRole.Main];
                        BrokenViewState bsM; brokenStates.TryGetValue(DwgViewRole.Main, out bsM);
                        double mx2 = (capP.XMin + capP.XMax) * 0.5;
                        var sp2 = DwgWriter.ToSheet(main, mx2, capP.BottomEdge.Y1);
                        double bx = bsM != null ? bsM.MapCoord(sp2[0], true) : sp2[0];
                        double by = bsM != null ? bsM.MapCoord(sp2[1], false) : sp2[1];
                        double[] tp = new[] { bx + 0.008, Math.Max(0.052, mc[1] - 0.009 - capLayer * 0.0065) };
                        if (w.UndercutDiameterCallout(main, capP.BottomEdge, capP.DiameterMm, capP.Count, tp, new[] { bx, by })) {
                            o.Covered++;
                            if (w.Space != null) w.Space.Add(tp[0] - 0.002, tp[1] - 0.003, tp[0] + 0.024, tp[1] + 0.004);
                        }
                    };
                    if (deferCallouts && postBreakActions != null) postBreakActions.Add(drawUndercut);
                    else drawUndercut();
                    continue;
                }
                double gap = 0.008 + (onRight ? rightLayer++ : leftLayer++) * 0.007;
                if (w.DiameterSpan(main, p.TopEdge, p.BottomEdge, onRight, gap, lay.Scale, mainUnbrokenBox, fitClass)) {
                    o.Covered++;
                    if (fitClass == DrawingTolerance.BearingShaftFit) { tolStats.BearingShafts++; fittedShaftPairs.Add(p); }
                    else if (fitClass == DrawingTolerance.GuidePinShaftFit) { tolStats.GuidePins++; fittedShaftPairs.Add(p); }
                }
            }
            // 对带轴承轴颈 k6 / 精销 g6 的车床件自动挂主轴基准 A、同轴度 ◎Ø0.02 A 与表面粗糙度 Ra1.6
            if (fittedShaftPairs.Count > 0 && mainBodyPair != null && mainBodyPair.TopEdge != null) {
                Action drawFitted = () => {
                    var mc = cboxes[DwgViewRole.Main];
                    BrokenViewState bsM; brokenStates.TryGetValue(DwgViewRole.Main, out bsM);
                    if (opt == null || opt.TolDatumsAndGdt) {
                        double mx = (mainBodyPair.XMin + mainBodyPair.XMax) * 0.5;
                        var sp = DwgWriter.ToSheet(main, mx, mainBodyPair.BottomEdge.Y1);
                        double sx = bsM != null ? bsM.MapCoord(sp[0], true) : sp[0];
                        if (w.Datum(main, mainBodyPair.BottomEdge, mx, mainBodyPair.BottomEdge.Y1, new[] { sx, mc[1] - 0.0085 }, "A")) {
                            tolStats.HasDatumA = true;
                        }
                    }
                    foreach (var fp in fittedShaftPairs) {
                        double fx = (fp.XMin + fp.XMax) * 0.5;
                        if ((opt == null || opt.TolRoughness) && fp.TopEdge != null) {
                            if (w.Roughness(main, fp.TopEdge, new[] { fx, fp.TopEdge.Y1 }, DrawingTolerance.BearingRoughness)) tolStats.HasRoughness = true;
                        }
                        if ((opt == null || opt.TolDatumsAndGdt) && tolStats.HasDatumA && !ReferenceEquals(fp, mainBodyPair) && fp.BottomEdge != null) {
                            var sp = DwgWriter.ToSheet(main, fx, fp.BottomEdge.Y1);
                            double sx = bsM != null ? bsM.MapCoord(sp[0], true) : sp[0];
                            w.FcfOnEdge(main, fp.BottomEdge, fx, fp.BottomEdge.Y1, new[] { sx + 0.010, mc[1] - 0.010 }, DrawingTolerance.CoaxialityFrameA);
                        }
                    }
                };
                if (deferCallouts && postBreakActions != null) postBreakActions.Add(drawFitted);
                else drawFitted();
            }

            D.DrawingView vb;
            if (views.TryGetValue(DwgViewRole.Below, out vb)) {
                bool isSec = false; try { isSec = vb.DrawingViewType == D.DrawingViewTypeConstants.igXSectionView; } catch { }
                var eb = edges[DwgViewRole.Below];
                var boxB = DrawingPlan.Box(eb);
                var belowUnbrokenBox = unbrokenCboxes.ContainsKey(DwgViewRole.Below) ? unbrokenCboxes[DwgViewRole.Below] : cboxes[DwgViewRole.Below];
                double secAxisY = (boxB[1] + boxB[3]) / 2.0;
                if (isSec) {
                    var secPairs = DrawingLatheRules.ExtractDiameters(eb, secAxisY, true, facts.RevMaxOuterDiameter);
                    int idLayer = 0;
                    foreach (var p in secPairs.Where(x => x.IsInnerBore)) {
                        double gap = 0.008 + (idLayer++) * 0.007;
                        string fitClass = (opt == null || opt.TolBearings) ? DrawingTolerance.ClassifyShaftDiameterFit(p, odPairs, totalLenMm) : "";
                        o.Required++;
                        if (w.DiameterSpan(vb, p.TopEdge, p.BottomEdge, true, gap, lay.Scale, belowUnbrokenBox, fitClass)) {
                            o.Covered++;
                            if (fitClass == DrawingTolerance.BearingBoreFit) tolStats.BearingBores++;
                        }
                    }
                    // 剖视中若有内孔深度台阶，补标剖视轴向台阶
                    var secTs = DrawingPlan.DropNearOutline(DrawingPlan.Targets(ordEdges[DwgViewRole.Below], true, true), ordEdges[DwgViewRole.Below], true, boxB, ctOf(vb, true), offsOf(vb, true), facts.SheetMetal);
                    var mainTs = DrawingPlan.DropNearOutline(DrawingPlan.Targets(em, true, true), em, true, boxM, ctOf(main, true), offsOf(main, true), facts.SheetMetal);
                    if (secTs.Count > mainTs.Count) {
                        var secSpans = DrawingLatheRules.PlanSteppedAxial(secTs);
                        foreach (var sp in secSpans) {
                            o.Required++;
                            double g = 0.008 + sp.Layer * 0.007;
                            if (w.LinearBetween(vb, sp.From, sp.To, true, true, g, lay.Scale, belowUnbrokenBox)) o.Covered++;
                        }
                    }
                }
            }

            // 2. 主视图轴向长度：密集径向孔走统一 3D 基准坐标串，否则走阶梯式线性尺寸
            var axialTs = DrawingPlan.DropNearOutline(DrawingPlan.Targets(em, true, false), em, true, boxM, ctOf(main, true), offsOf(main, true), facts.SheetMetal);
            int radialHoleXs = axialTs.Count(t => t.KeyPoint && !t.Quadrant);
            if (radialHoleXs >= 4) {
                double? fm = null;
                if (datum3D != null && frames.ContainsKey(DwgViewRole.Main) && frames[DwgViewRole.Main].Valid && frames[DwgViewRole.Main].AxisX >= 0) {
                    var vf = frames[DwgViewRole.Main];
                    fm = datum3D.ProjectToView(vf.AxisX, vf.X, vf.Ou);
                }
                var runH = DrawingPlan.Ordinate(em, true, lay.Scale, boxM, false, off, ctOf(main, true), offsOf(main, true), facts.SheetMetal, fm, false);
                Count(o, runH);
                o.Covered += Covered(w.Ordinate(main, runH, lay.Scale, boxM));
            } else {
                var spans = DrawingLatheRules.PlanSteppedAxial(axialTs);
                foreach (var sp in spans) {
                    o.Required++;
                    double g = 0.008 + sp.Layer * 0.007;
                    if (w.LinearBetween(main, sp.From, sp.To, true, false, g, lay.Scale, mainUnbrokenBox)) o.Covered++;
                }
            }
        }

        enum CalloutKind { Hole, Pcd, Slot, OuterRing, Radius, Chamfer }
        sealed class PlannedCallout {
            public CalloutKind Kind;
            public HoleGroup Hole;
            public PcdPattern Pcd;
            public SlotGroup Slot;
            public RadiusGroup Rad;
            public string Pre, Val, Suf, Sub, Line1, Line2;
            public bool Pin, BearingBore, AllowVoid;
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
                if (w.Space.FindFallbackSlot(fx, fy, cbox, tw, th, belowH, out placed, out elbow)) {
                    placedBox = placed;
                    return elbow;
                }
            }
            double sw = w.Space != null ? w.Space.SheetW : 0.420;
            double sh = w.Space != null ? w.Space.SheetH : 0.297;
            double x0 = Math.Max(0.012, Math.Min(sw - tw - 0.012, cbox[2] + 0.006));
            double boxY0 = Math.Max(0.052, Math.Min(sh - th - 0.014, fallY - th));
            fallY = boxY0 - 0.0025;
            placedBox = new[] { x0, boxY0, x0 + tw, boxY0 + th };
            return new[] { x0, DrawingStyleRules.CalloutShelfY(boxY0, belowH) };
        }

        static ModelHole ResolvePinHole(ModelHole mh, bool isHole, double diameterMm, List<ModelHole> axisHoles){
            if (mh != null) return mh;
            if (!isHole || axisHoles == null || axisHoles.Count == 0) return null;
            var same = axisHoles.Where(h => Math.Abs(h.Radius * 2000 - diameterMm) < 0.02).ToList();
            if (same.Count > 0 && same.All(h => h.CbRadius <= 0 && h.CsRadius <= 0
                && (h.Spec == null || (h.Spec.Kind != HoleKind.Tapped && h.Spec.Kind != HoleKind.Counterbore && h.Spec.Kind != HoleKind.Countersink && h.Spec.ThreadSize.Length == 0))))
                return same[0];
            return null;
        }
        static bool IsPinHoleCandidate(double diameterMm, bool isHole, ModelHole pinHole, string val, string sub){
            bool cbOrCs = pinHole != null && (pinHole.CbRadius > 0 || pinHole.CsRadius > 0
                || (pinHole.Spec != null && (pinHole.Spec.Kind == HoleKind.Counterbore || pinHole.Spec.Kind == HoleKind.Countersink)));
            return DrawingTolerance.IsPinHole(diameterMm, isHole && pinHole != null, val.Length > 0, cbOrCs || sub.Length > 0);
        }
        static bool MatchesOuterMouth(ModelHole h, double diameterMm){
            if (h == null) return false;
            if (h.CbRadius > 0 && Math.Abs(h.CbRadius * 2000 - diameterMm) < 0.05) return true;
            if (h.CsRadius > 0 && Math.Abs(h.CsRadius * 2000 - diameterMm) < 0.05) return true;
            if (h.Spec != null) {
                if (h.Spec.Kind == HoleKind.Counterbore && h.Spec.CounterboreDiameter > 0 && Math.Abs(h.Spec.CounterboreDiameter - diameterMm) < 0.05) return true;
                if (h.Spec.Kind == HoleKind.Countersink && h.Spec.CountersinkDiameter > 0 && Math.Abs(h.Spec.CountersinkDiameter - diameterMm) < 0.05) return true;
            }
            return false;
        }
        static V3 SightAxis(D.DrawingView v){
            try { double dx, dy, dz, ux, uy, uz; D.ViewOrientationConstants k; v.ViewOrientation(out dx, out dy, out dz, out ux, out uy, out uz, out k); return new V3(dx, dy, dz).Unit(); }
            catch { return new V3(0, 0, 1); }
        }

        static void Count(DrawingOutcome o, OrdinateRun r){
            if (r == null) return;
            if ((r.Truncated || r.Count >= DrawingPlan.MaxRun) && !o.Notes.Any(n => n.StartsWith("细节太多"))) o.Notes.Add("细节太多（刻线 / 刻字 / 花纹），坐标尺寸只标了外形、孔和长边，请人工补");
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
        // 只留参考视图里没标过的坐标。按"到外形最小边的距离"比（剖视不和主视图投影对齐、还可能左右镜像），
        // 并经 DrawingPlan.FilterNewTargets 保留对称中心 Origin 并重新锚定外形边，杜绝居中孔漏标或脱锚浮空。
        static OrdinateRun Only(OrdinateRun r, D.DrawingView v, List<double> done, bool horizontal, D.DrawingView main, double[] mainBox, double[] vBox){
            return OnlyAxis(r, v, horizontal, done, main, horizontal, mainBox, vBox, null);
        }
        static OrdinateRun Only(OrdinateRun r, D.DrawingView v, List<double> done, bool horizontal, D.DrawingView main, double[] mainBox, double[] vBox, double? forcedOriginCoord){
            return OnlyAxis(r, v, horizontal, done, main, horizontal, mainBox, vBox, forcedOriginCoord);
        }
        static OrdinateRun OnlyAxis(OrdinateRun r, D.DrawingView v, bool horizontal, List<double> done, D.DrawingView refView, bool refHorizontal, double[] refBox, double[] vBox, double? forcedOriginCoord){
            if (r == null) return null;
            var rlo = DwgWriter.ToSheet(refView, refBox[0], refBox[1]);
            var rhi = DwgWriter.ToSheet(refView, refBox[2], refBox[3]);
            double refMin = refHorizontal ? Math.Min(rlo[0], rhi[0]) : Math.Min(rlo[1], rhi[1]);
            var vlo = DwgWriter.ToSheet(v, vBox[0], vBox[1]); var vhi = DwgWriter.ToSheet(v, vBox[2], vBox[3]);
            bool mirror = Axis(v, horizontal).Dot(Axis(refView, refHorizontal)) < 0;
            var rel = done.Select(d => d - refMin).ToList();
            double sc = 1.0; try { sc = v.ScaleFactor; } catch { }
            return DrawingPlan.FilterNewTargets(r, t => {
                var p = DwgWriter.ToSheet(v, t.X, t.Y); double c = horizontal ? p[0] : p[1];
                double cr = mirror
                    ? (horizontal ? Math.Max(vlo[0], vhi[0]) - c : Math.Max(vlo[1], vhi[1]) - c)
                    : c - (horizontal ? Math.Min(vlo[0], vhi[0]) : Math.Min(vlo[1], vhi[1]));
                return rel.Any(d => Math.Abs(d - cr) < 2e-5);
            }, sc, forcedOriginCoord);
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

        static void Engrave(D.DraftDocument dft, D.Sheet sheet, D.DrawingView iso, string engraveStyle, string restoreStyle, bool lockSheetStyle, DrawingOutcome o){
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
                // 模板无公司刻字样式时沿用原活动样式 restoreStyle，避免受前面检索中心线污染成「中心线」
                string useStyle = !string.IsNullOrEmpty(engraveStyle) ? engraveStyle : restoreStyle;
                var w = new DwgWriter(sheet, dft, useStyle);
                var nb = w.Note(iso, best, (best.X1 + best.X2) / 2, (best.Y1 + best.Y2) / 2, text, at);
                if (nb != null && tpl != null) { try { tpl.Delete(); } catch { } }
                if (nb == null) o.Notes.Add("刻字引线没挂上（模板那条还在，请手动挂到零件上）");
            } catch (Exception e) {
                o.Notes.Add("刻字引线没挂上：" + e.Message);
            } finally {
                if (lockSheetStyle) DwgWriter.ApplySheetStyle(dft, sheet, restoreStyle);
                else if (!string.IsNullOrEmpty(restoreStyle)) DwgWriter.SetActiveStyle(dft, restoreStyle);
            }
        }
    }
}
