using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using D=SolidEdgeDraft;
using F=SolidEdgeFramework;
using S=SolidEdgeFrameworkSupport;

namespace TianGongCadSuite {
    // Wave 2 工程图引擎（部分类）：
    //   1. 细长件自动断裂视图（BreakLinePairs + DisplayBreakLinesInBrokenState + 比例升级与视图重排）；
    //   2. 多孔大板规范孔表规划、主视图孔编号气泡（A1/A2...）与统一 3D 基准坐标表；
    //   3. 局部放大详图（AddDetailView）与溢出补视图（底面/背面/右端面盲孔 + 斜孔向视图）第 1 页空位 / 第 2 页自动排版与标注。
    public static partial class DrawingMaker {
        sealed class Wave2Carry {
            public List<HoleTableRow> HoleRows = new List<HoleTableRow>();
            public List<DetailCluster> DetailClusters = new List<DetailCluster>();
            public double MainOriginXView, MainOriginYView;
        }

        // 1. 细长件自动断裂视图（两阶段协议：准备比例 → 主线性/坐标标注 → 落位断裂）：
        // 实测（2026-11-26 BreakOrientProbe）：在天工 225 里必须先在干净未断裂视图上创建坐标尺寸与直径/长度线性尺寸，
        // 再调用 BreakLinePairs.Add + DisplayBreakLinesInBrokenState = true，天工原生关联尺寸才会 100% 精准收拢到断口两端，
        // 彻底杜绝先断后标或反复切换断裂态引发的坐标链竖翻飞线与总长丢失。
        sealed class PendingBreakPlan {
            public BreakPlan Plan;
            public double OldScale, SafeScale, MidOther;
            public bool BelowIsSection;
            public D.DrawingView Main, Below, Right;
            public DwgViewSpec MainSpec, BelowSpec, RightSpec;
        }

        static PendingBreakPlan PrepareBreakViewScale(D.DraftDocument dft, D.Sheet sheet, Dictionary<DwgViewRole, D.DrawingView> views,
            DwgLayout lay, ModelFacts facts, DrawingOptions opt, F.Application app, DrawingOutcome o){
            if (opt == null || !opt.AutoBreakView || lay == null || views == null) return null;
            if (!views.ContainsKey(DwgViewRole.Main)) return null;
            if (views.ContainsKey(DwgViewRole.Left) || views.ContainsKey(DwgViewRole.Back)) return null;
            if (lay.Scale >= 0.5 - 1e-9) return null;

            var main = views[DwgViewRole.Main];
            var mainEdges = DrawingViewRead.Edges(main);
            if (mainEdges.Count == 0) return null;
            var box = DrawingPlan.Box(mainEdges);
            var mainSpec = lay.Get(DwgViewRole.Main);
            var belowSpec = lay.Get(DwgViewRole.Below);
            var rightSpec = lay.Get(DwgViewRole.Right);

            double fixedW = (rightSpec != null ? 3 : 2) * DrawingLayout.DimBand + (rightSpec != null ? DrawingLayout.Gap : 0);
            double fixedH = (belowSpec != null ? 3 : 2) * DrawingLayout.DimBand + (belowSpec != null ? DrawingLayout.Gap : 0)
                + ((belowSpec != null && belowSpec.Section) ? 0.010 : 0.0);
            double availW = Math.Max(0.04, lay.Zones.MainW - fixedW);
            double availH = Math.Max(0.04, lay.Zones.MainH - fixedH);

            double wModel = box[2] - box[0], hModel = box[3] - box[1];
            bool horizLong = wModel >= hModel;
            double planAvailW = horizLong && rightSpec != null ? Math.Max(0.04, availW - rightSpec.W * Math.Min(0.5, lay.Scale * 2.0)) : availW;
            double planAvailH = !horizLong && belowSpec != null ? Math.Max(0.04, availH - belowSpec.H * Math.Min(0.5, lay.Scale * 2.0))
                : (horizLong && belowSpec != null ? Math.Max(0.03, availH * 0.52) : availH);

            var plan = DrawingWave2Rules.PlanBreakView(mainEdges, box, lay.Scale, planAvailW, planAvailH);
            if (plan == null) return null;

            D.DrawingView below = views.ContainsKey(DwgViewRole.Below) ? views[DwgViewRole.Below] : null;
            D.DrawingView right = views.ContainsKey(DwgViewRole.Right) ? views[DwgViewRole.Right] : null;
            bool belowIsSection = belowSpec != null && belowSpec.Section;

            // 先按解析等效宽高算目标安全比例，确保升档后断裂完能稳稳放进 Main 区
            double effMainW = plan.VerticalBreak ? plan.EffectiveLongSpan : wModel;
            double effMainH = plan.VerticalBreak ? hModel : plan.EffectiveLongSpan;
            double totalEffW = effMainW + (right != null ? rightSpec.W : 0);
            double totalEffH = effMainH + (below != null ? belowSpec.H : 0);
            double safeScale = DrawingPlan.PickScale(totalEffW, totalEffH, availW, availH);
            if (safeScale > plan.UpgradedScale) safeScale = plan.UpgradedScale;
            if (safeScale > 1.0) safeScale = 1.0;
            double oldScale = lay.Scale;
            if (safeScale <= oldScale * 1.15) return null;

            try {
                // 升级比例并把未断裂主视图临时居中到正坐标纸面区，使 Phase 1 坐标/直径标注 100% 落在正坐标安全区
                main.ScaleFactor = safeScale; SetViewScaleText(main, safeScale); main.Update();
                if (below != null) { try { below.ScaleFactor = safeScale; SetViewScaleText(below, safeScale); below.Update(); } catch { } }
                if (right != null) { try { right.ScaleFactor = safeScale; SetViewScaleText(right, safeScale); right.Update(); } catch { } }
                Idle(app);

                double tempCx = Math.Max(lay.Zones.Width * 0.42, (wModel * safeScale) * 0.5 + 0.032);
                double tempCy = lay.Zones.Main[1] + lay.Zones.MainH * 0.56;
                Center(main, tempCx, tempCy, true, true);
                Idle(app);

                lay.Scale = safeScale;
                if (mainSpec != null) mainSpec.Scale = safeScale;
                if (belowSpec != null) belowSpec.Scale = safeScale;
                if (rightSpec != null) rightSpec.Scale = safeScale;
                o.Scale = DrawingPlan.ScaleText(safeScale);
                try { if (safeScale >= 1) sheet.SheetSetup.SetScale(safeScale, 1); else sheet.SheetSetup.SetScale(1, Math.Round(1 / safeScale, 3)); } catch { }

                double midOther = plan.VerticalBreak ? (box[1] + box[3]) * 0.5 : (box[0] + box[2]) * 0.5;
                return new PendingBreakPlan {
                    Plan = plan, OldScale = oldScale, SafeScale = safeScale, MidOther = midOther,
                    BelowIsSection = belowIsSection, Main = main, Below = below, Right = right,
                    MainSpec = mainSpec, BelowSpec = belowSpec, RightSpec = rightSpec
                };
            } catch (Exception ex) {
                Log.Write("DrawingPrepareBreakView", ex);
                RevertBreak(main, below, right, below != null, right != null, oldScale, app);
                return null;
            }
        }

        // 阶段 2：在 Phase 1 坐标与主线性尺寸建完后真正插入 BreakLinePairs、启用断裂显示并把主/俯/左整组精确落位，
        // 返回各已断裂视图的纯断口位移（ShiftLo/ShiftHi）供 Phase 2 引线标注使用。
        static Dictionary<DwgViewRole, BrokenViewState> CommitBreakView(PendingBreakPlan pb, D.Sheet sheet, DwgLayout lay, F.Application app, DrawingOutcome o){
            var states = new Dictionary<DwgViewRole, BrokenViewState>();
            if (pb == null || pb.Main == null || pb.Plan == null) return states;
            var main = pb.Main;
            var below = pb.Below;
            var right = pb.Right;
            var plan = pb.Plan;
            try {
                var dir = plan.VerticalBreak
                    ? D.BreakLinePairDirConstants.igBreakLinePairDirConstants_Vertical
                    : D.BreakLinePairDirConstants.igBreakLinePairDirConstants_Horizontal;
                var p1 = plan.VerticalBreak ? DwgWriter.ToSheet(main, plan.CutStartView, pb.MidOther) : DwgWriter.ToSheet(main, pb.MidOther, plan.CutStartView);
                var p2 = plan.VerticalBreak ? DwgWriter.ToSheet(main, plan.CutEndView, pb.MidOther) : DwgWriter.ToSheet(main, pb.MidOther, plan.CutEndView);
                double c1 = plan.VerticalBreak ? Math.Min(p1[0], p2[0]) : Math.Min(p1[1], p2[1]);
                double c2 = plan.VerticalBreak ? Math.Max(p1[0], p2[0]) : Math.Max(p1[1], p2[1]);
                if (c2 - c1 < 0.006) return states;

                var mainUnbroken = DwgWriter.SheetBox(main);
                double cutMidView = (plan.CutStartView + plan.CutEndView) * 0.5;
                main.BreakLinePairs.Add(dir, c1, c2, false);
                main.DisplayBreakLinesInBrokenState = true;
                main.Update(); Idle(app);
                var mainBrokenPreCenter = DwgWriter.SheetBox(main);

                bool brokeBelow = false;
                double[] belowUnbroken = null, belowBrokenPre = null;
                if (plan.VerticalBreak && below != null && !pb.BelowIsSection) {
                    try {
                        belowUnbroken = DwgWriter.SheetBox(below);
                        below.BreakLinePairs.Add(dir, c1, c2, false);
                        below.DisplayBreakLinesInBrokenState = true;
                        below.Update(); Idle(app);
                        belowBrokenPre = DwgWriter.SheetBox(below);
                        brokeBelow = true;
                    } catch { }
                }
                bool brokeRight = false;
                double[] rightUnbroken = null, rightBrokenPre = null;
                if (!plan.VerticalBreak && right != null) {
                    try {
                        rightUnbroken = DwgWriter.SheetBox(right);
                        right.BreakLinePairs.Add(dir, c1, c2, false);
                        right.DisplayBreakLinesInBrokenState = true;
                        right.Update(); Idle(app);
                        rightBrokenPre = DwgWriter.SheetBox(right);
                        brokeRight = true;
                    } catch { }
                }

                // 重新居中主视图并紧凑对齐俯视图/左视图（关联坐标与直径尺寸由天工原生自动跟随移动）
                var mb = DwgWriter.SheetBox(main);
                double bw = mb[2] - mb[0], bh = mb[3] - mb[1];
                double rw = 0, bh2 = 0;
                if (right != null) { var rb = DwgWriter.SheetBox(right); rw = rb[2] - rb[0]; }
                if (below != null) { var bb = DwgWriter.SheetBox(below); bh2 = bb[3] - bb[1]; }
                double groupW = bw + (right != null ? DrawingLayout.DimBand + DrawingLayout.Gap + rw : 0);
                double secExtra = pb.BelowIsSection ? 0.010 : 0.0;
                double groupH = bh + (below != null ? DrawingLayout.DimBand + DrawingLayout.Gap + secExtra + bh2 : 0);

                double mx = lay.Zones.Main[0] + DrawingLayout.DimBand + Math.Max(0, (lay.Zones.MainW - 2 * DrawingLayout.DimBand - groupW) * 0.30) + bw * 0.5;
                double my = lay.Zones.Main[3] - DrawingLayout.DimBand - Math.Max(0, (lay.Zones.MainH - 2 * DrawingLayout.DimBand - groupH) * 0.45) - bh * 0.5;
                Center(main, mx, my, true, true);
                if (below != null) {
                    double by = my - bh * 0.5 - DrawingLayout.DimBand - DrawingLayout.Gap - secExtra - bh2 * 0.5;
                    Center(below, mx, by, pb.BelowIsSection, true);
                }
                if (right != null) {
                    double rx = mx + bw * 0.5 + DrawingLayout.DimBand + DrawingLayout.Gap + rw * 0.5;
                    Center(right, rx, my, true, false);
                }
                Idle(app);

                // 记录各断裂视图的纯断口位移（基于断裂前后、平移前的 SheetBox 之差）及最终截断中点
                var cmAfter = plan.VerticalBreak ? DwgWriter.ToSheet(main, cutMidView, pb.MidOther) : DwgWriter.ToSheet(main, pb.MidOther, cutMidView);
                states[DwgViewRole.Main] = new BrokenViewState {
                    View = main,
                    VerticalBreak = plan.VerticalBreak,
                    CutMidSheet = plan.VerticalBreak ? cmAfter[0] : cmAfter[1],
                    ShiftLo = plan.VerticalBreak ? (mainBrokenPreCenter[0] - mainUnbroken[0]) : (mainBrokenPreCenter[1] - mainUnbroken[1]),
                    ShiftHi = plan.VerticalBreak ? (mainBrokenPreCenter[2] - mainUnbroken[2]) : (mainBrokenPreCenter[3] - mainUnbroken[3])
                };
                if (brokeBelow && belowUnbroken != null && belowBrokenPre != null) {
                    var cmB = DwgWriter.ToSheet(below, cutMidView, pb.MidOther);
                    states[DwgViewRole.Below] = new BrokenViewState {
                        View = below, VerticalBreak = true, CutMidSheet = cmB[0],
                        ShiftLo = belowBrokenPre[0] - belowUnbroken[0], ShiftHi = belowBrokenPre[2] - belowUnbroken[2]
                    };
                }
                if (brokeRight && rightUnbroken != null && rightBrokenPre != null) {
                    var cmR = DwgWriter.ToSheet(right, pb.MidOther, cutMidView);
                    states[DwgViewRole.Right] = new BrokenViewState {
                        View = right, VerticalBreak = false, CutMidSheet = cmR[1],
                        ShiftLo = rightBrokenPre[1] - rightUnbroken[1], ShiftHi = rightBrokenPre[3] - rightUnbroken[3]
                    };
                }
                o.Notes.Add("视图 细长件已自动插入断裂视图，比例由 " + DrawingPlan.ScaleText(pb.OldScale) + " 提升至 " + o.Scale);
            } catch (Exception ex) {
                Log.Write("DrawingCommitBreakView", ex);
            }
            return states;
        }

        static void RevertBreak(D.DrawingView main, D.DrawingView below, D.DrawingView right, bool brokeBelow, bool brokeRight, double oldScale, F.Application app){
            try {
                if (main != null) {
                    try { main.DisplayBreakLinesInBrokenState = false; } catch { }
                    try { for (int i = main.BreakLinePairs.Count; i >= 1; i--) main.BreakLinePairs.Item(i).Delete(); } catch { }
                    try { main.ScaleFactor = oldScale; SetViewScaleText(main, oldScale); main.Update(); } catch { }
                }
                if (brokeBelow && below != null) {
                    try { below.DisplayBreakLinesInBrokenState = false; } catch { }
                    try { for (int i = below.BreakLinePairs.Count; i >= 1; i--) below.BreakLinePairs.Item(i).Delete(); } catch { }
                    try { below.ScaleFactor = oldScale; SetViewScaleText(below, oldScale); below.Update(); } catch { }
                }
                if (brokeRight && right != null) {
                    try { right.DisplayBreakLinesInBrokenState = false; } catch { }
                    try { for (int i = right.BreakLinePairs.Count; i >= 1; i--) right.BreakLinePairs.Item(i).Delete(); } catch { }
                    try { right.ScaleFactor = oldScale; SetViewScaleText(right, oldScale); right.Update(); } catch { }
                }
                Idle(app);
            } catch { }
        }

        // 判断视图是否已启用断裂显示，若是则将由 ViewToSheet 推导的轮廓框收敛到天工实测的断裂后 SheetBox
        static double[] ClampBoxForBrokenView(D.DrawingView v, double[] cbox){
            if (v == null || cbox == null || cbox.Length < 4) return cbox;
            try {
                if (v.DisplayBreakLinesInBrokenState && v.BreakLinePairs.Count > 0) {
                    var sb = DwgWriter.SheetBox(v);
                    // SheetBox 四周通常有约 2~3mm 留白，收紧 2mm 贴合真实轮廓
                    double sx0 = sb[0] + 0.002, sy0 = sb[1] + 0.002, sx1 = sb[2] - 0.002, sy1 = sb[3] - 0.002;
                    if (sx1 > sx0 + 0.004 && sy1 > sy0 + 0.004) {
                        return new[] { Math.Max(cbox[0], sx0), Math.Max(cbox[1], sy0), Math.Min(cbox[2], sx1), Math.Min(cbox[3], sy1) };
                    }
                    return sb;
                }
            } catch { }
            return cbox;
        }

        // 2. 多孔大板（≥16 孔）孔表规划：
        // 汇总主视图中不属于 PCD 分度圆的全部加工孔，按规格分组编号（A1/A2/B1…），
        // 坐标严格相对全局统一 3D XYZ 基准投影原点计算；随后把这些孔圆从主视图坐标串中移除，避免 40 条坐标线挤爆图面。
        static HashSet<DwgEdge> PlanMainHoleTable(D.DrawingView main, List<DwgEdge> mainEdges, double[] mainBox,
            List<ModelHole> holes, double[] range, ModelFacts facts, DrawingOptions opt,
            HashSet<DwgEdge> pcdCircles, double originXView, double originYView,
            out List<HoleTableRow> tableRows){
            tableRows = new List<HoleTableRow>();
            var handled = new HashSet<DwgEdge>();
            if (opt == null || !opt.AutoHoleTable || main == null || mainEdges == null || mainEdges.Count == 0) return handled;
            if (facts.PartKind != DrawingPartKind.Standard) return handled;

            double maxD = Math.Min(mainBox[2] - mainBox[0], mainBox[3] - mainBox[1]) * 1.01;
            var map = HoleMap(main, mainEdges, holes, range);
            var threads = DrawingViewRead.Threads(main);
            var viewDir = SightAxis(main);
            var axisHoles = holes.Where(h => Math.Abs(Math.Abs(h.Axis.Dot(viewDir)) - 1) < 1e-4).ToList();
            bool viewHasModelHoles = axisHoles.Count > 0;

            var specGroups = new List<HoleTableGroupInput>();
            var extraHandled = new List<DwgEdge>();
            int totalNonPcd = 0;
            V3 fx3, fy3, fd3; double fou, fov;
            bool okFrame = Frame(main, mainEdges, range, out fx3, out fy3, out fd3, out fou, out fov);

            var allMainGroups = new List<HoleGroup>();
            foreach (var rg in DrawingPlan.Holes(mainEdges, maxD)) {
                allMainGroups.AddRange(DrawingPlan.SplitHoleGroupByKey(rg, c => HoleGroupKey(c, rg.DiameterMm, map, threads, axisHoles)));
            }
            bool hasCompanionMounting = allMainGroups.Any(hg => {
                ModelHole cmh; map.TryGetValue(hg.Leader, out cmh);
                if (cmh != null && (cmh.CbRadius > 0 || cmh.CsRadius > 0 || (cmh.Spec != null && cmh.Spec.Kind == HoleKind.Tapped))) return true;
                if (DrawingPlan.ThreadFromEdges(hg.Leader, threads).Length > 0) return true;
                return hg.DiameterMm >= 3.2 && hg.DiameterMm <= 14.0 && !DrawingTolerance.IsPinHole(hg.DiameterMm, true, false, false);
            });

            foreach (var g in allMainGroups) {
                var nonPcd = g.Circles.Where(c => pcdCircles == null || !pcdCircles.Contains(c)).ToList();
                if (nonPcd.Count == 0) continue;
                ModelHole mh; map.TryGetValue(nonPcd[0], out mh);
                bool isHole = mh != null || (viewHasModelHoles && map.Count == 0);
                if (!isHole) continue;
                string pre, val, suf, sub;
                DrawingText.Hole(1, mh, g.DiameterMm, true, out pre, out val, out suf, out sub);
                if (val.Length == 0) {
                    string m = DrawingPlan.ThreadFromEdges(nonPcd[0], threads);
                    if (m.Length > 0) { val = m; DrawingText.ThreadOnly(m, mh, out suf, out sub); }
                }
                var pinHole = ResolvePinHole(mh, true, g.DiameterMm, axisHoles);
                bool cbOrCs = pinHole != null && (pinHole.CbRadius > 0 || pinHole.CsRadius > 0
                    || (pinHole.Spec != null && (pinHole.Spec.Kind == HoleKind.Counterbore || pinHole.Spec.Kind == HoleKind.Countersink)));
                bool directPin = opt.TolPinHoles
                    && DrawingTolerance.IsContextPinHole(g.DiameterMm, nonPcd.Count, pinHole != null, val.Length > 0, cbOrCs || sub.Length > 0, hasCompanionMounting);
                string spec = FormatHoleTableSpec(val, g.DiameterMm, suf, sub, directPin);
                bool isPin = directPin;

                // 识别与通孔同轴的背面/正面定位销座孔（如 02-031 的 2×Ø6.6 贯通 + 同轴 2×Ø12 ↧5 H7）：
                // 合并进同一孔表行规格，并按销孔在规格旁挂同轴度形位公差框，同时抑制第 1 页重复外圈标注。
                if (!isPin && opt.TolPinHoles && val.Length == 0 && okFrame && nonPcd.Count >= 2 && nonPcd.Count <= 8) {
                    double matchedOuterDiaMm = 0, matchedOuterDepthMm = 0;
                    bool allHaveLocatingBore = true;
                    foreach (var c in nonPcd) {
                        double cOuterDia = 0, cOuterDepth = 0;
                        bool foundOuter = false;
                        ModelHole cmh; map.TryGetValue(c, out cmh);
                        if (cmh != null && cmh.Segments != null) {
                            foreach (var sg in cmh.Segments) {
                                if (sg.Radius > c.R + 5e-4) {
                                    double od = Math.Round(sg.Radius * 2000.0, 2);
                                    double dp = Math.Round(Math.Abs(sg.Hi - sg.Lo) * 1000.0, 1);
                                    if (DrawingTolerance.IsConcentricLocatingPinBore(od, g.DiameterMm, dp, nonPcd.Count, false)) {
                                        cOuterDia = od; cOuterDepth = dp; foundOuter = true; break;
                                    }
                                }
                            }
                        }
                        if (!foundOuter) {
                            foreach (var oh in axisHoles) {
                                if (oh == null || oh.Through || oh.Radius <= c.R + 5e-4) continue;
                                double cu = oh.Point.Dot(fx3) + fou, cv = oh.Point.Dot(fy3) + fov;
                                if (Math.Abs(cu - c.Cx) < 2e-4 && Math.Abs(cv - c.Cy) < 2e-4) {
                                    bool ohThread = oh.Spec != null && oh.Spec.Kind == HoleKind.Tapped;
                                    double od = Math.Round(oh.Radius * 2000.0, 2);
                                    double dp = Math.Round(DrawingStyleRules.NominalHoleDepthMm(oh), 1);
                                    if (DrawingTolerance.IsConcentricLocatingPinBore(od, g.DiameterMm, dp, nonPcd.Count, ohThread)) {
                                        cOuterDia = od; cOuterDepth = dp; foundOuter = true; break;
                                    }
                                }
                            }
                        }
                        if (!foundOuter) { allHaveLocatingBore = false; break; }
                        if (matchedOuterDiaMm <= 0) { matchedOuterDiaMm = cOuterDia; matchedOuterDepthMm = cOuterDepth; }
                        else if (Math.Abs(matchedOuterDiaMm - cOuterDia) > 0.1) { allHaveLocatingBore = false; break; }
                    }
                    if (allHaveLocatingBore && matchedOuterDiaMm > 0) {
                        isPin = true;
                        string outerStr = "Ø" + matchedOuterDiaMm.ToString("0.##", CultureInfo.InvariantCulture)
                            + " H7深" + matchedOuterDepthMm.ToString("0.#", CultureInfo.InvariantCulture);
                        spec = spec.Replace(" ", "") + "/" + outerStr;
                        foreach (var c in nonPcd) {
                            foreach (var oc in mainEdges) {
                                if (oc.Kind == DwgKind.Circle && Math.Abs(oc.Cx - c.Cx) < 2e-4 && Math.Abs(oc.Cy - c.Cy) < 2e-4
                                    && Math.Abs(oc.R * 2000.0 - matchedOuterDiaMm) < 0.15) {
                                    extraHandled.Add(oc);
                                }
                            }
                        }
                    }
                }

                string gdt = isPin
                    ? (opt.TolDatumsAndGdt ? DrawingTolerance.HoleTableCoaxialityFrameA : DrawingTolerance.HoleTableCoaxialityFrame)
                    : "";
                var grpInput = new HoleTableGroupInput { SpecText = spec, IsPinHole = isPin, GdtFrame = gdt };
                grpInput.Circles.AddRange(nonPcd);
                specGroups.Add(grpInput);
                totalNonPcd += nonPcd.Count;
            }

            if (!DrawingWave2Rules.ShouldUseHoleTable(facts.PartKind, totalNonPcd, 16)) return handled;

            tableRows = DrawingWave2Rules.BuildHoleTable(specGroups, originXView, originYView);
            foreach (var r in tableRows) if (r.Circle != null) handled.Add(r.Circle);
            foreach (var ec in extraHandled) handled.Add(ec);
            return handled;
        }

        static string FormatHoleTableSpec(string val, double diaMm, string suf, string sub, bool pin){
            string baseSpec = val.Length > 0 ? val : ("Ø" + diaMm.ToString("0.##", CultureInfo.InvariantCulture));
            if (pin) baseSpec += " H7";
            string s = (baseSpec + (suf ?? "") + (string.IsNullOrEmpty(sub) ? "" : " " + sub))
                .Replace("%DP", "深")
                .Replace("%DI", "Ø")
                .Replace("%CB", "沉孔Ø")
                .Replace("%CS", "锥沉Ø")
                .Replace("%DG", "°")
                .Trim();
            return s;
        }

        // 在主视图各孔旁打上紧贴的 A1 / A2 编号气泡，并优先尝试把 ≤14 行的小孔表放到第 1 页空位
        static void PlaceMainHoleTags(D.DraftDocument dft, D.Sheet sheet1, DwgWriter w, D.DrawingView main,
            double[] mainCbox, Wave2Carry wave2, DrawingOutcome o){
            if (wave2 == null || wave2.HoleRows == null || wave2.HoleRows.Count == 0) return;
            var centerS = new[] { (mainCbox[0] + mainCbox[2]) * 0.5, (mainCbox[1] + mainCbox[3]) * 0.5 };
            var usedTagPts = new List<double[]>();
            int tagged = 0;
            foreach (var row in wave2.HoleRows) {
                if (row.Circle == null || row.Circle.Tag == null) continue;
                if (w.HoleTag(main, row.Circle, row.Tag, centerS, usedTagPts)) tagged++;
            }
            o.Holes += wave2.HoleRows.Count;
            o.Required += wave2.HoleRows.Count;
            o.Covered += tagged;
            o.Notes.Add("多孔表 主视图 " + wave2.HoleRows.Count + " 个孔已切换规范孔表（坐标相对统一 3D 基准 0,0）");
        }

        // 3. 溢出补视图 / 局部放大详图 / 孔表排版总控（第 1 页空位优先，放不下自动建第 2 页「图页2」）：
        //   · 盲孔溢出（lay.Dropped 中的 Left/Back + 底面盲孔 facts.BottomBlind）；
        //   · 斜孔向视图（DrawingWave2Rules.PlanObliqueViews）；
        //   · 局部放大详图（DrawingWave2Rules.FindDetailClusters，框架安装板 / 小比例密孔簇）；
        //   · 多孔大板孔表（DrawingWave2Rules.BuildHoleTable）。
        static void BuildSheet2AndDetails(D.DraftDocument dft, D.Sheet sheet1, D.ModelLink link, DwgWriter w1,
            Dictionary<DwgViewRole, D.DrawingView> views, DwgLayout lay, List<ModelHole> holes, double[] range,
            ModelFacts facts, ModelDatum3D datum3D, DrawingOptions opt, Wave2Carry wave2,
            string dimStyle, string centerStyle, string restoreStyle, F.Application app, DrawingOutcome o){
            if (opt == null) return;
            var mainSpec = lay.Get(DwgViewRole.Main);
            var belowSpec = lay.Get(DwgViewRole.Below);
            var main = views[DwgViewRole.Main];

            // 收集所有需要补画的溢出正投影 / 斜孔向视图
            var overflowPlans = new List<OverflowViewPlan>();
            var dMain = Unit(mainSpec.Axis, mainSpec.Sign);
            var xMain = Unit(mainSpec.Horizontal, mainSpec.HorzSign);
            var yMain = dMain.Cross(xMain);

            if (opt.AutoMultiSheet) {
                int occThrough;
                CountOccludedModelHoles(main, holes, range, out occThrough);
                foreach (var dr in lay.Dropped) {
                    if (dr == DwgViewRole.Back) {
                        overflowPlans.Add(new OverflowViewPlan {
                            Caption = "背面视图",
                            SightDir = dMain * -1.0,
                            HorzDir = xMain * -1.0,
                            ModelW = mainSpec.W,
                            ModelH = mainSpec.H,
                            BlindOnly = occThrough == 0 && facts.PartKind != DrawingPartKind.ProfileFrame,
                            RoleHint = DwgViewRole.Back
                        });
                    } else if (dr == DwgViewRole.Left) {
                        // 右视图看右端面（视线 = +xMain，屏幕水平 = -dMain 或 +dMain，垂直 = yMain）
                        overflowPlans.Add(new OverflowViewPlan {
                            Caption = "右视图",
                            SightDir = xMain,
                            HorzDir = dMain * -1.0,
                            ModelW = facts.Size[mainSpec.Axis],
                            ModelH = mainSpec.H,
                            BlindOnly = true,
                            RoleHint = DwgViewRole.Left
                        });
                    }
                }
                if (facts.BottomBlind > 0 && (belowSpec == null || !belowSpec.Section)) {
                    // 底面仰视图：视线从下往上看（-yMain），屏幕水平沿 xMain（保持 X 对齐）
                    overflowPlans.Add(new OverflowViewPlan {
                        Caption = "底面视图",
                        SightDir = yMain * -1.0,
                        HorzDir = xMain,
                        ModelW = mainSpec.W,
                        ModelH = facts.Size[mainSpec.Axis],
                        BlindOnly = true,
                        RoleHint = DwgViewRole.Below
                    });
                }
                var obliquePlans = DrawingWave2Rules.PlanObliqueViews(holes, range);
                overflowPlans.AddRange(obliquePlans);
            }

            var detailClusters = (opt.AutoDetailView && wave2 != null) ? wave2.DetailClusters : new List<DetailCluster>();
            var holeRows = (wave2 != null && wave2.HoleRows != null) ? wave2.HoleRows : new List<HoleTableRow>();

            // 若第 1 页无溢出视图与孔表，仅剩 1 个局部放大详图，先尝试摆在第 1 页空白区（如轴测图下方或主区右下）
            if (overflowPlans.Count == 0 && holeRows.Count == 0 && detailClusters.Count == 1 && w1.Space != null) {
                var cl = detailClusters[0];
                double dDiam = Math.Max(0.044, cl.RadiusView * 2.0 * cl.TargetScale + 0.016);
                double[] spot = FindFreeDetailBox(lay.Zones, w1.Space, dDiam, dDiam);
                if (spot != null) {
                    if (CreateAndAnnotateDetailOnSheet1(dft, sheet1, main, cl, (spot[0] + spot[2]) * 0.5, (spot[1] + spot[3]) * 0.5,
                        "I", w1, facts, centerStyle, dimStyle, restoreStyle, app)) {
                        o.Views++;
                        detailClusters.Clear();
                    }
                }
            }

            // 实测（天工 225）：AddDetailView 跨页迁移与 CroppingBoundaryProfile 圆形裁剪对零件视图均不生效，
            // 因此不在第 2 页为不可裁剪详图预留空网格（杜绝 02-007 第 2 页出现上下半区畸形留白及误报详图数量）。
            if (overflowPlans.Count == 0 && holeRows.Count == 0) return;
            if (!opt.AutoMultiSheet) return;

            // 创建第 2 页「图页2」，继承公司模板背景图框与纸张规格（标题栏自动显示「共 2 页 第 2 页」）
            D.Sheet sheet2 = null;
            bool keepSheet2 = false;
            try {
                sheet2 = dft.Sheets.AddSheet("图页2", D.SheetSectionTypeConstants.igWorkingSection, Type.Missing, sheet1);
                Idle(app);
                try { sheet2.Background = sheet1.Background; sheet2.BackgroundVisible = true; } catch { }
                try { sheet2.SheetSetup.SheetSizeOption = sheet1.SheetSetup.SheetSizeOption; } catch { }
                try {
                    if (lay.Scale >= 1) sheet2.SheetSetup.SetScale(lay.Scale, 1);
                    else sheet2.SheetSetup.SetScale(1, Math.Round(1 / lay.Scale, 3));
                } catch { }
                sheet2.Activate();
                Idle(app);
                DwgWriter.ApplySheetStyle(dft, sheet2, dimStyle);

                var w2 = new DwgWriter(sheet2, dft, dimStyle);
                var s2Zones = lay.Zones;
                w2.Space = new DwgSpace(s2Zones);
                int s2ViewsCreated = 0;

                bool hasViewsOnS2 = overflowPlans.Count > 0;
                bool hasTableOnS2 = holeRows.Count > 0;

                double tolZoneX = s2Zones.Name == "A4" ? 0.210 : 0.292;
                double tableX0 = s2Zones.Main[0] + 0.012;
                double tableYTop = s2Zones.Main[3] - 0.008;
                double maxTableH = Math.Max(0.10, tableYTop - s2Zones.Main[1] - 0.002);
                SheetZones viewLayoutZone = s2Zones;

                // 1. 先按真实分栏宽度规划并落位第 2 页规范孔表，再给左侧补视图留出完全独立的防碰撞半区
                if (hasTableOnS2) {
                    if (hasViewsOnS2) {
                        // 右对齐贴近图框右沿、顶边严守公差表下方（A3 y≤240mm / A4 y≤168mm），单栏 5 列表总宽仅 ~114mm
                        tableYTop = Math.Min(tableYTop, s2Zones.Iso[3] - 0.002);
                        maxTableH = Math.Max(0.10, tableYTop - s2Zones.Main[1] - 0.002);
                        double tblW, tblH;
                        DrawingWave2Rules.PlanHoleTableChunks(holeRows, maxTableH, out tblW, out tblH);
                        tableX0 = Math.Max(s2Zones.Main[0] + 0.140, s2Zones.Iso[2] - tblW - 0.006);
                        double splitX = tableX0 - 0.008;
                        viewLayoutZone = new SheetZones {
                            Name = s2Zones.Name, Width = s2Zones.Width, Height = s2Zones.Height,
                            Main = new[] { s2Zones.Main[0], s2Zones.Main[1], splitX, s2Zones.Main[3] },
                            Iso = new[] { splitX, s2Zones.Main[1], splitX, s2Zones.Main[3] }
                        };
                        w2.Space.AddView(new[] { tableX0 - 0.004, tableYTop - tblH - 0.004, tableX0 + tblW + 0.004, tableYTop + 0.004 });
                    } else {
                        double tblW, tblH;
                        DrawingWave2Rules.PlanHoleTableChunks(holeRows, maxTableH, out tblW, out tblH);
                        if (tableX0 + tblW > tolZoneX) {
                            tableYTop = Math.Min(tableYTop, s2Zones.Iso[3] - 0.002);
                            maxTableH = Math.Max(0.10, tableYTop - s2Zones.Main[1] - 0.002);
                            DrawingWave2Rules.PlanHoleTableChunks(holeRows, maxTableH, out tblW, out tblH);
                        }
                        w2.Space.AddView(new[] { tableX0 - 0.004, tableYTop - tblH - 0.004, tableX0 + tblW + 0.004, tableYTop + 0.004 });
                    }
                    bool tblOk = w2.HoleTable(dft, sheet2, tableX0, tableYTop, holeRows, maxTableH);
                    if (tblOk) {
                        keepSheet2 = true;
                        int pinCount = holeRows.Count(r => r.IsPinHole && !string.IsNullOrEmpty(r.GdtFrame));
                        o.Notes.Add("第2页 已生成 " + holeRows.Count + " 行规范孔表（相对主视图 0,0 基准"
                            + (pinCount > 0 ? "，含 " + pinCount + " 个销孔同轴度标注" : "") + "）");
                    }
                }

                // 2. 两阶段创建溢出补视图：先探测有效目标孔并淘汰 0 标注空视图（如 02-007 右视图），
                //    再按真实存活视图数量做居中均匀网格排版，彻底杜绝半页空洞、撞公差表或越界裁字。
                if (hasViewsOnS2) {
                    var surviving = new List<OverflowViewCreated>();
                    double initScale = Math.Max(0.05, Math.Min(lay.Scale, 0.5));
                    foreach (var p in overflowPlans) {
                        var created = TryCreateAndCheckOverflowView(dft, sheet1, sheet2, link, main, mainSpec, p, initScale, holes, range, app);
                        if (created != null) surviving.Add(created);
                    }

                    if (surviving.Count > 0) {
                        var sizes = new List<double[]>();
                        foreach (var item in surviving) sizes.Add(new[] { item.EffectiveModelW, item.EffectiveModelH });
                        // 非斜孔正投影尽量与第 1 页主比例一致（仅当主比例过小 <1:12 时允许适度放大至 1:10）
                        double maxS2Scale = surviving.Any(x => x.Plan.IsOblique)
                            ? Math.Max(lay.Scale, 0.5)
                            : (lay.Scale < 0.08 ? Math.Min(0.125, lay.Scale * 1.5) : lay.Scale);
                        var slots = DrawingWave2Rules.LayoutSheet2(viewLayoutZone, sizes, maxS2Scale);

                        // 先把全部存活视图统一缩放到位、居中落位并登记进 w2.Space，避免视图 1 的引线闯入视图 2 本体
                        for (int i = 0; i < surviving.Count; i++) {
                            var item = surviving[i];
                            var sl = slots[i];
                            double sc = item.Plan.IsOblique ? sl.Scale : Math.Min(sl.Scale, maxS2Scale);
                            item.FinalScale = sc;
                            try {
                                if (Math.Abs(item.View.ScaleFactor - sc) > 1e-6) item.View.ScaleFactor = sc;
                            } catch { }
                            SetViewScaleText(item.View, sc);
                            SetViewCaption(item.View, item.Plan.Caption);
                            try { item.View.Update(); } catch { }
                            Idle(app);
                            Center(item.View, sl.X, sl.Y, true, true);
                            Idle(app);
                            RefreshOverflowViewBoxes(item, holes, range, w2.Space);
                        }

                        bool drewBottom = false, drewBack = false, drewLeft = false, drewOblique = false;
                        foreach (var item in surviving) {
                            int dimsBefore = w2.Created;
                            bool ok = AnnotateOverflowView(dft, sheet2, item, w2, holes, range, facts, datum3D, centerStyle, dimStyle, restoreStyle, o);
                            if (ok && w2.Created > dimsBefore) {
                                s2ViewsCreated++;
                                keepSheet2 = true;
                                if (item.Plan.IsOblique) drewOblique = true;
                                else if (item.Plan.RoleHint == DwgViewRole.Below) drewBottom = true;
                                else if (item.Plan.RoleHint == DwgViewRole.Back) drewBack = true;
                                else if (item.Plan.RoleHint == DwgViewRole.Left) drewLeft = true;
                            } else {
                                try { item.View.Delete(); } catch { }
                            }
                        }

                        if (drewBottom) o.Notes.RemoveAll(n => n.Contains("底面有") && n.Contains("请人工补仰视图"));
                        if (drewBack) o.Notes.RemoveAll(n => n.Contains("背面有盲孔") && n.Contains("没补"));
                        if (drewLeft) o.Notes.RemoveAll(n => n.Contains("右端面有盲孔") && n.Contains("没补"));
                        if (drewOblique) o.Notes.RemoveAll(n => n.Contains("个斜孔") && n.Contains("请人工补"));
                        var addedLabels = new List<string>();
                        if (drewBottom) addedLabels.Add("底面视图");
                        if (drewBack) addedLabels.Add("背面视图");
                        if (drewLeft) addedLabels.Add("右视图");
                        if (drewOblique) addedLabels.Add("斜孔向视图");
                        if (addedLabels.Count > 0) o.Notes.Add("第2页 已自动补画 " + string.Join("、", addedLabels));
                    }
                }

                if (!keepSheet2) {
                    try { sheet1.Activate(); Idle(app); sheet2.Delete(); } catch { }
                    return;
                }

                DwgWriter.ClearCollectionStyles(sheet2);
                o.Views += s2ViewsCreated;
                o.Dimensions += w2.Created;
                o.Failed += w2.Failed;
                o.Notes.AddRange(w2.Notes);
                o.Sheet = lay.Zones.Name + "(2页)";
            } catch (Exception ex) {
                Log.Write("DrawingSheet2", ex);
                o.Notes.Add("第 2 页创建异常：" + ex.Message);
            } finally {
                // 务必切回第 1 页作为默认打开页，并恢复样式
                try { sheet1.Activate(); Idle(app); } catch { }
                if (!string.IsNullOrEmpty(dimStyle)) DwgWriter.ApplySheetStyle(dft, sheet1, dimStyle);
                else if (!string.IsNullOrEmpty(restoreStyle)) DwgWriter.SetActiveStyle(dft, restoreStyle);
            }
        }

        sealed class OverflowViewCreated {
            public OverflowViewPlan Plan;
            public D.DrawingView View;
            public double FinalScale;
            public double EffectiveModelW, EffectiveModelH;
            public double[] SolidSubBox;
            public List<DwgEdge> Raw;
            public List<DwgEdge> Filt;
            public double[] Box;
            public double[] Cbox;
            public double[] OwnObs;
        }

        static double[] FindFreeDetailBox(SheetZones z, DwgSpace space, double w, double h){
            // 在轴测图下方、轴测图上方以及主区右下寻找空白区
            var cands = new[] {
                new[] { z.Iso[0] + 0.004, z.Main[1] + 0.004, z.Iso[0] + 0.004 + w, z.Main[1] + 0.004 + h },
                new[] { z.Main[2] - w - 0.006, z.Main[1] + 0.004, z.Main[2] - 0.006, z.Main[1] + 0.004 + h },
                new[] { z.Iso[2] - w - 0.004, z.Main[1] + 0.004, z.Iso[2] - 0.004, z.Main[1] + 0.004 + h }
            };
            foreach (var r in cands) {
                if (space.Free(r)) return r;
            }
            return null;
        }

        // 阶段 1：在第 2 页独立创建溢出视图并核查是否存在真实待标特征（零标注空视图立刻删除）。
        // 严禁在第 1 页对主视图调用 AddByFold（会拖动第 1 页主视图位置），也严禁启用 v.Crop=true（天工 225 上 Crop=true 会让整张视图消失）；
        // 若零件含超出实体的构造曲面/参考草图（如 02-023 底面视图），后续由 HideEdgesOutsideBox 按逐边 ShowHideEdgeOverrideHide 精准隐去杂线。
        static OverflowViewCreated TryCreateAndCheckOverflowView(D.DraftDocument dft, D.Sheet sheet1, D.Sheet sheet2,
            D.ModelLink link, D.DrawingView main, DwgViewSpec mainSpec, OverflowViewPlan plan, double initScale,
            List<ModelHole> holes, double[] range, F.Application app){
            D.DrawingView v = null;
            try {
                sheet2.Activate(); Idle(app);
                v = sheet2.DrawingViews.AddPartView(link, D.ViewOrientationConstants.igTopView, initScale, 0.20, 0.15, D.PartDrawingViewTypeConstants.sePartDesignedView);
                v.SetViewOrientation(plan.SightDir.X, plan.SightDir.Y, plan.SightDir.Z, plan.HorzDir.X, plan.HorzDir.Y, plan.HorzDir.Z);
                try { v.Update(); } catch { }
                Idle(app);

                var raw = DrawingViewRead.Edges(v);
                if (raw.Count == 0) { try { v.Delete(); } catch { } return null; }

                // 仅在内存层面过滤超出实体的远处草图（绝不调用 v.Crop=true 导致视图消失）
                double[] solidSub = null;
                var rawBox0 = DrawingPlan.Box(raw);
                double rawW = rawBox0[2] - rawBox0[0], rawH = rawBox0[3] - rawBox0[1];
                if (!plan.IsOblique && (rawW > plan.ModelW * 1.35 + 0.005 || rawH > plan.ModelH * 1.35 + 0.005)) {
                    solidSub = EstimateSolidSubBox(raw, plan.ModelW, plan.ModelH);
                    if (solidSub != null) raw = raw.Where(ed => EdgeInBox(ed, solidSub, 0.003)).ToList();
                }

                var filt = HoleInternal(v, raw, holes, range);
                if (filt.Count == 0) { try { v.Delete(); } catch { } return null; }

                // 预检该视图是否存在至少 1 个待标盲孔或斜孔（如 02-007 右视图 0 待标孔则直接剔除，不留空视图；
                // 对非 BlindOnly 视图如 02-007 背面视图，若视图内含可见孔圆且同轴存在三维孔也保留，确保背面孔群正常补标）
                var map = HoleMap(v, filt, holes, range);
                var viewDir = SightAxis(v);
                var axisHoles = holes.Where(h => Math.Abs(Math.Abs(h.Axis.Dot(viewDir)) - 1) < 1e-3).ToList();
                int targetHoles = 0;
                foreach (var kv in map) {
                    if (plan.BlindOnly && kv.Value.Through) continue;
                    targetHoles++;
                }
                bool hasUnmappedBackHoles = !plan.BlindOnly && axisHoles.Count > 0 && filt.Any(ed => ed.Kind == DwgKind.Circle);
                if (targetHoles == 0 && !hasUnmappedBackHoles && !plan.IsOblique) {
                    try { v.Delete(); } catch { }
                    return null;
                }

                var box = DrawingPlan.Box(filt);
                return new OverflowViewCreated {
                    Plan = plan,
                    View = v,
                    FinalScale = initScale,
                    EffectiveModelW = Math.Max(1e-4, box[2] - box[0]),
                    EffectiveModelH = Math.Max(1e-4, box[3] - box[1]),
                    SolidSubBox = solidSub,
                    Raw = raw,
                    Filt = filt,
                    Box = box
                };
            } catch (Exception ex) {
                if (v != null) try { v.Delete(); } catch { }
                Log.Write("DrawingCreateOverflowView", ex);
                return null;
            }
        }

        static void RefreshOverflowViewBoxes(OverflowViewCreated item, List<ModelHole> holes, double[] range, DwgSpace space){
            if (item.SolidSubBox != null) {
                HideEdgesOutsideBox(item.View, item.SolidSubBox, 0.003);
            }
            var raw = DrawingViewRead.Edges(item.View);
            if (item.SolidSubBox != null) raw = raw.Where(ed => EdgeInBox(ed, item.SolidSubBox, 0.003)).ToList();
            if (raw.Count > 0) {
                item.Raw = raw;
                item.Filt = HoleInternal(item.View, raw, holes, range);
                if (item.Filt.Count == 0) item.Filt = raw;
                item.Box = DrawingPlan.Box(item.Filt);
            }
            var p0 = DwgWriter.ToSheet(item.View, item.Box[0], item.Box[1]);
            var p1 = DwgWriter.ToSheet(item.View, item.Box[2], item.Box[3]);
            item.Cbox = new[] { Math.Min(p0[0], p1[0]), Math.Min(p0[1], p1[1]), Math.Max(p0[0], p1[0]), Math.Max(p0[1], p1[1]) };
            item.OwnObs = new[] { item.Cbox[0] - 0.002, item.Cbox[1] - 0.002, item.Cbox[2] + 0.002, item.Cbox[3] + 0.002 };
            if (space != null) {
                space.AddView(item.OwnObs);
                // 给视图底部标题留空间
                space.Add(item.Cbox[0], item.Cbox[1] - 0.014, item.Cbox[2], item.Cbox[1]);
            }
        }

        // 用逐边 ShowHideEdgeOverrideHide 隐藏实体包围盒之外的构造曲面/参考草图图元（实测 02-023 底面视图）：
        // 绝不碰 v.Crop=true（天工 225 上 Crop=true 会让整个视图消失），同时彻底消除多余的构造板轮廓线和假孔圆。
        static void HideEdgesOutsideBox(D.DrawingView v, double[] b, double pad){
            if (v == null || b == null || b.Length < 4) return;
            double x0 = b[0] - pad, y0 = b[1] - pad, x1 = b[2] + pad, y1 = b[3] + pad;
            try {
                var lines = v.DVLines2d;
                for (int i = 1; i <= lines.Count; i++) {
                    try {
                        var ln = lines.Item(i);
                        double lx1, ly1, lx2, ly2; ln.GetStartPoint(out lx1, out ly1); ln.GetEndPoint(out lx2, out ly2);
                        // 任一端点落出实体包围盒均隐藏（同时清除横跨实体中心但两端伸出数百毫米的构造参考线）
                        if (lx1 < x0 || lx1 > x1 || ly1 < y0 || ly1 > y1 || lx2 < x0 || lx2 > x1 || ly2 < y0 || ly2 > y1) {
                            ln.ShowHideEdgeOverride = D.DVShowHideEdgeOverrideType.DVShowHideEdgeOverrideHide;
                        }
                    } catch { }
                }
            } catch { }
            try {
                var circles = v.DVCircles2d;
                for (int i = 1; i <= circles.Count; i++) {
                    try {
                        var cc = circles.Item(i);
                        double cx, cy; cc.GetCenterPoint(out cx, out cy);
                        if (cx < x0 || cx > x1 || cy < y0 || cy > y1) {
                            cc.ShowHideEdgeOverride = D.DVShowHideEdgeOverrideType.DVShowHideEdgeOverrideHide;
                        }
                    } catch { }
                }
            } catch { }
            try {
                var arcs = v.DVArcs2d;
                for (int i = 1; i <= arcs.Count; i++) {
                    try {
                        var ar = arcs.Item(i);
                        double cx, cy; ar.GetCenterPoint(out cx, out cy);
                        if (cx < x0 || cx > x1 || cy < y0 || cy > y1) {
                            ar.ShowHideEdgeOverride = D.DVShowHideEdgeOverrideType.DVShowHideEdgeOverrideHide;
                        }
                    } catch { }
                }
            } catch { }
        }

        // 阶段 2：在已精确定位并防碰撞登记的溢出视图上补标定位坐标串与孔规格引线
        static bool AnnotateOverflowView(D.DraftDocument dft, D.Sheet sheet2, OverflowViewCreated item, DwgWriter w2,
            List<ModelHole> holes, double[] range, ModelFacts facts, ModelDatum3D datum3D,
            string centerStyle, string dimStyle, string restoreStyle, DrawingOutcome o){
            var v = item.View;
            var plan = item.Plan;
            var raw = item.Raw;
            var filt = item.Filt;
            var box = item.Box;
            var cbox = item.Cbox;
            var ownObs = item.OwnObs;
            double scale = item.FinalScale;
            try {
                // 计算该视图上的全局 3D 基准投影
                V3 fx3, fy3, fd3; double fou, fov;
                bool okFrame = Frame(v, raw, range, out fx3, out fy3, out fd3, out fou, out fov);
                double? fh = null, fv = null;
                if (!plan.IsOblique && okFrame && datum3D != null) {
                    int axX = DominantAxis(fx3), axY = DominantAxis(fy3);
                    fh = datum3D.ProjectToView(axX, fx3, fou);
                    fv = datum3D.ProjectToView(axY, fy3, fov);
                }

                // 仅保留本视图目标孔（盲孔视图留盲孔，斜孔视图留斜孔）+ 外轮廓边，做清爽坐标定位
                var map = HoleMap(v, filt, holes, range);
                var threads = DrawingViewRead.Threads(v);
                var viewDir = SightAxis(v);
                var axisHoles = holes.Where(h => Math.Abs(Math.Abs(h.Axis.Dot(viewDir)) - 1) < 1e-3).ToList();

                var targetCircles = new HashSet<DwgEdge>();
                foreach (var kv in map) {
                    if (plan.BlindOnly && kv.Value.Through) continue;
                    targetCircles.Add(kv.Key);
                }
                double tol = 2e-4;
                var ordList = new List<DwgEdge>();
                foreach (var ed in filt) {
                    if (ed.Kind == DwgKind.Circle) {
                        if (targetCircles.Contains(ed) || map.Count == 0) ordList.Add(ed);
                    } else if (ed.Kind == DwgKind.Line) {
                        bool outerX = ed.IsVertical && (Math.Abs(ed.X1 - box[0]) <= tol || Math.Abs(ed.X1 - box[2]) <= tol);
                        bool outerY = ed.IsHorizontal && (Math.Abs(ed.Y1 - box[1]) <= tol || Math.Abs(ed.Y1 - box[3]) <= tol);
                        if (outerX || outerY) ordList.Add(ed);
                    }
                }

                if (targetCircles.Count > 0 || plan.IsOblique) {
                    var runH = DrawingPlan.Ordinate(ordList, true, scale, box, false, 0.0075, 0, null, facts.SheetMetal, fh, false);
                    var runV = DrawingPlan.Ordinate(ordList, false, scale, box, true, 0.0075, 0, null, facts.SheetMetal, fv, false);
                    if (DrawingPlan.IsRedundantCircleSpan(runH)) runH = null;
                    if (DrawingPlan.IsRedundantCircleSpan(runV)) runV = null;
                    Count(o, runH); Count(o, runV);
                    o.Covered += Covered(w2.Ordinate(v, runH, scale, box)) + Covered(w2.Ordinate(v, runV, scale, box));
                }

                // 标盲孔 / 斜孔规格引线（按锚点自上而下排布并避让全部视图与坐标带）
                double maxD = Math.Min(box[2] - box[0], box[3] - box[1]) * 1.01;
                double fallY = cbox[3] - 0.002;
                var hgs = new List<HoleGroup>();
                foreach (var rg in DrawingPlan.Holes(filt, maxD)) {
                    var subs = DrawingPlan.SplitHoleGroupByKey(rg, c => HoleGroupKey(c, rg.DiameterMm, map, threads, axisHoles));
                    hgs.AddRange(subs);
                }
                var usedLeaderY = new List<double>();
                Func<DwgEdge, double[]> toAnchorSheet = ed => { var a = DrawingPlan.AnchorOf(ed); return DwgWriter.ToSheet(v, a[0], a[1]); };
                foreach (var g in hgs.OrderByDescending(x => x.Count)) {
                    var picked = DrawingPlan.PickSeparatedLeader(g.Circles, box, true, usedLeaderY, toAnchorSheet, 0.008);
                    if (picked != null) g.Leader = picked;
                    ModelHole mh; map.TryGetValue(g.Leader, out mh);
                    if (plan.BlindOnly && (mh == null || mh.Through)) continue;
                    bool isHole = mh != null || axisHoles.Count > 0;
                    if (!isHole) continue;
                    string pre, val, suf, sub;
                    DrawingText.Hole(g.Count, mh, g.DiameterMm, true, out pre, out val, out suf, out sub);
                    if (val.Length == 0) {
                        string m = DrawingPlan.ThreadFromEdges(g.Leader, threads);
                        if (m.Length > 0) { val = m; DrawingText.ThreadOnly(m, mh, out suf, out sub); }
                    }
                    var ls = DwgWriter.ToSheet(v, g.Leader.Cx, g.Leader.Cy);
                    usedLeaderY.Add(ls[1]);
                    string shown = pre + (val.Length > 0 ? val : "Ø" + g.DiameterMm.ToString("0.##", CultureInfo.InvariantCulture)) + suf;
                    double[] placedBox;
                    var text = SpotCompact(w2, ls[0], ls[1], cbox, ownObs, shown, sub, sub.Length > 0 ? 2 : 1, 0, true, false, null, ref fallY, out placedBox);
                    o.Required++;
                    if (w2.HoleCallout(v, g, pre, val, suf, sub, text)) { o.Covered++; o.HoleCallouts++; o.Holes += g.Count; }
                }

                // 若该视图做了构造面逐边隐藏（如 02-023），跳过全视图 RetrieveCenters 以免在已隐藏构造孔上生成漂浮十字中心线
                if (item.SolidSubBox == null) {
                    if (!string.IsNullOrEmpty(centerStyle)) DwgWriter.SetActiveStyle(dft, centerStyle);
                    try { RetrieveCenters(v, centerStyle); }
                    finally {
                        if (!string.IsNullOrEmpty(dimStyle)) DwgWriter.ApplySheetStyle(dft, sheet2, dimStyle);
                        else if (!string.IsNullOrEmpty(restoreStyle)) DwgWriter.SetActiveStyle(dft, restoreStyle);
                    }
                }
                return true;
            } catch (Exception ex) {
                Log.Write("DrawingAnnotateOverflowView", ex);
                return false;
            }
        }

        static void SetViewCaption(D.DrawingView v, string caption){
            if (v == null || string.IsNullOrEmpty(caption)) return;
            try { v.Caption = caption; } catch { }
            try { v.CaptionDefinitionTextPrimary = caption; } catch { }
            try { v.CaptionLocation = S.DimViewCaptionLocationConstants.igDimViewCaptionLocationBottom; } catch { }
            try { v.DisplayCaptionPrimary = true; } catch { }
            try { v.DisplayCaption = true; } catch { }
            try { v.DisplayScale = true; } catch { }
        }

        static double[] EstimateSolidSubBox(List<DwgEdge> raw, double modelW, double modelH){
            // 找到宽≈modelW、高≈modelH 的实体长边所在窗口，剥离远处多余草图矩形
            double tolW = Math.Max(0.005, modelW * 0.05), tolH = Math.Max(0.005, modelH * 0.08);
            var longH = raw.Where(e => e.Kind == DwgKind.Line && e.IsHorizontal && Math.Abs(e.Length - modelW) <= tolW).ToList();
            if (longH.Count >= 2) {
                double x0 = longH.Min(e => Math.Min(e.X1, e.X2));
                double x1 = longH.Max(e => Math.Max(e.X1, e.X2));
                var ys = longH.Select(e => e.Y1).OrderBy(y => y).ToList();
                for (int i = 0; i < ys.Count; i++) {
                    for (int j = i + 1; j < ys.Count; j++) {
                        if (Math.Abs((ys[j] - ys[i]) - modelH) <= tolH) {
                            return new[] { x0, ys[i], x1, ys[j] };
                        }
                    }
                }
            }
            return null;
        }

        static bool EdgeInBox(DwgEdge e, double[] b, double pad){
            double x0 = b[0] - pad, y0 = b[1] - pad, x1 = b[2] + pad, y1 = b[3] + pad;
            if (e.Kind == DwgKind.Line) {
                double mx = (e.X1 + e.X2) * 0.5, my = (e.Y1 + e.Y2) * 0.5;
                return mx >= x0 && mx <= x1 && my >= y0 && my <= y1;
            }
            return e.Cx >= x0 && e.Cx <= x1 && e.Cy >= y0 && e.Cy <= y1;
        }

        // 在第 1 页空位创建并标注同页原生局部放大详图（AddDetailView）
        static bool CreateAndAnnotateDetailOnSheet1(D.DraftDocument dft, D.Sheet sheet1, D.DrawingView main,
            DetailCluster cl, double destX, double destY, string label, DwgWriter wTarget, ModelFacts facts,
            string centerStyle, string dimStyle, string restoreStyle, F.Application app){
            D.DrawingView dv = null;
            try {
                var cs = DwgWriter.ToSheet(main, cl.Cx, cl.Cy);
                var rs = DwgWriter.ToSheet(main, cl.Cx + cl.RadiusView, cl.Cy);
                double radSheet = Math.Max(0.005, Math.Abs(rs[0] - cs[0]));
                dv = sheet1.DrawingViews.AddDetailView(main, cs[0], cs[1], radSheet, cl.TargetScale, destX, destY, true);
                Idle(app);
                if (dv == null) return false;
                SetViewCaption(dv, "详图 " + label);
                SetViewScaleText(dv, cl.TargetScale);
                try { dv.Update(); } catch { }
                Idle(app);
                Center(dv, destX, destY, true, true);

                var raw = DrawingViewRead.Edges(dv);
                if (raw.Count == 0) return true;
                var localCircles = raw.Where(e => e.Kind == DwgKind.Circle
                    && Math.Sqrt((e.Cx - cl.Cx) * (e.Cx - cl.Cx) + (e.Cy - cl.Cy) * (e.Cy - cl.Cy)) <= cl.RadiusView * 1.15).ToList();
                var box = localCircles.Count >= 2 ? DrawingPlan.Box(localCircles) : DrawingPlan.Box(raw);
                var p0 = DwgWriter.ToSheet(dv, box[0], box[1]);
                var p1 = DwgWriter.ToSheet(dv, box[2], box[3]);
                var cbox = new[] { Math.Min(p0[0], p1[0]), Math.Min(p0[1], p1[1]), Math.Max(p0[0], p1[0]), Math.Max(p0[1], p1[1]) };
                var ownObs = new[] { cbox[0] - 0.002, cbox[1] - 0.002, cbox[2] + 0.002, cbox[3] + 0.002 };
                if (wTarget.Space != null) wTarget.Space.AddView(ownObs);

                if (localCircles.Count >= 2) {
                    var runH = DrawingPlan.Ordinate(localCircles, true, cl.TargetScale, box, false, 0.007, 0, null, facts.SheetMetal, null, false);
                    var runV = DrawingPlan.Ordinate(localCircles, false, cl.TargetScale, box, true, 0.007, 0, null, facts.SheetMetal, null, false);
                    if (runH != null) wTarget.Ordinate(dv, runH, cl.TargetScale, box);
                    if (runV != null) wTarget.Ordinate(dv, runV, cl.TargetScale, box);
                }

                if (!string.IsNullOrEmpty(centerStyle)) DwgWriter.SetActiveStyle(dft, centerStyle);
                try { RetrieveCenters(dv, centerStyle); }
                finally {
                    if (!string.IsNullOrEmpty(dimStyle)) DwgWriter.ApplySheetStyle(dft, sheet1, dimStyle);
                    else if (!string.IsNullOrEmpty(restoreStyle)) DwgWriter.SetActiveStyle(dft, restoreStyle);
                }
                return true;
            } catch (Exception ex) {
                if (dv != null) try { dv.Delete(); } catch { }
                Log.Write("DrawingDetailView", ex);
                return false;
            }
        }
    }
}
