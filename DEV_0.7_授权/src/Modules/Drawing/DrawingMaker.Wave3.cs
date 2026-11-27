using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using D=SolidEdgeDraft;
using S=SolidEdgeFrameworkSupport;

namespace TianGongCadSuite {
    // Wave 3 单零件智能公差引擎（部分类）：
    // 纯基于单零件自身几何与特征规则，无装配体依赖，全部受 DrawingOptions 5 项公差子开关控制：
    //   1. 基准 A 自动挂载（主视图/侧视图/剖视图上的安装底面，或在车床轴上挂在主轴线上）；
    //   2. 轴承座孔（如衬套块 02-003 Ø28）→ H7 + 垂直度 ⊥Ø0.03 A + 平行度 ∥0.03 A + Ra1.6；
    //   3. 直线导轨安装面（≥3 个标准导轨孔距螺纹孔排，如 02-031 的 5×M5@60）→ 基准 A + 平面度 0.02 + Ra3.2 + 靠山垂直度 0.02 A；
    //   4. 车床轴/定位销外径配合（k6 / g6）+ 同轴度 ◎Ø0.02 A + Ra1.6。
    public static partial class DrawingMaker {
        sealed class Wave3TolStats {
            public int PinGroups, PinHoles;
            public int BearingBores, BearingShafts, GuidePins;
            public bool HasGuideRail;
            public bool HasDatumA;
            public bool HasRoughness;
        }

        // 在侧视 / 俯视 / 剖视图上给安装底面挂基准 A，并在需要时追加平行度 / 平面度 / 粗糙度 / 轴承孔垂直度
        static void ApplySmartPlateAndBearingTolerances(DwgWriter w, Dictionary<DwgViewRole, D.DrawingView> views,
            Dictionary<DwgViewRole, List<DwgEdge>> edges, Dictionary<DwgViewRole, double[]> cboxes,
            List<ModelHole> holes, List<ModelHole> pins, double[] range, ModelFacts facts,
            DrawingOptions opt, Wave3TolStats stats){
            if (opt == null) return;
            if (facts.PartKind == DrawingPartKind.ProfileSingle || facts.PartKind == DrawingPartKind.ProfileFrame) return;

            var main = views[DwgViewRole.Main];
            var mainEdges = edges[DwgViewRole.Main];
            var mainBox = DrawingPlan.Box(mainEdges);
            var mainMap = HoleMap(main, mainEdges, holes, range);
            var mainThreads = DrawingViewRead.Threads(main);

            // 1. 识别主视图上的直线导轨安装孔排（如 5×M5 间距 60mm）
            GuideRailMatch rail = null;
            if (opt.TolGuideRails && facts.PartKind == DrawingPartKind.Standard) {
                var mainCircles = mainEdges.Where(ed => ed.Kind == DwgKind.Circle).ToList();
                rail = DrawingTolerance.DetectGuideRailPattern(mainCircles, c => {
                    ModelHole mh;
                    if (mainMap.TryGetValue(c, out mh) && mh != null && mh.Spec != null && mh.Spec.Kind == HoleKind.Tapped) return true;
                    return DrawingPlan.ThreadFromEdges(c, mainThreads).Length > 0;
                }, mainBox);
                if (rail != null) stats.HasGuideRail = true;
            }

            // 2. 挑一个最适合展示安装底面轮廓的副视图（优先 Below/剖视，其次 Right）
            DwgViewRole sideRole = DwgViewRole.Iso;
            if (views.ContainsKey(DwgViewRole.Below) && edges.ContainsKey(DwgViewRole.Below)) sideRole = DwgViewRole.Below;
            else if (views.ContainsKey(DwgViewRole.Right) && edges.ContainsKey(DwgViewRole.Right)) sideRole = DwgViewRole.Right;

            bool needDatum = opt.TolDatumsAndGdt && (stats.PinHoles > 0 || stats.BearingBores > 0 || stats.HasGuideRail);
            DwgEdge datumFaceEdge = null;
            DwgEdge oppFaceEdge = null;
            D.DrawingView sideView = sideRole != DwgViewRole.Iso ? views[sideRole] : null;
            double[] sideCbox = sideRole != DwgViewRole.Iso && cboxes.ContainsKey(sideRole) ? cboxes[sideRole] : null;

            if (sideView != null && sideCbox != null) {
                var se = edges[sideRole];
                var sbox = DrawingPlan.Box(se);
                bool horizFace = (sideRole == DwgViewRole.Below);
                double lo = horizFace ? sbox[1] : sbox[0];
                double hi = horizFace ? sbox[3] : sbox[2];
                const double tol = 2e-4;
                // 主视图背面/安装面（Below 里通常是底边或顶边最长轮廓线）
                datumFaceEdge = se.Where(ed => ed.Kind == DwgKind.Line && (horizFace ? (ed.IsHorizontal && Math.Abs(ed.Y1 - lo) <= tol) : (ed.IsVertical && Math.Abs(ed.X1 - lo) <= tol)))
                    .OrderByDescending(ed => ed.Length).FirstOrDefault();
                oppFaceEdge = se.Where(ed => ed.Kind == DwgKind.Line && (horizFace ? (ed.IsHorizontal && Math.Abs(ed.Y1 - hi) <= tol) : (ed.IsVertical && Math.Abs(ed.X1 - hi) <= tol)))
                    .OrderByDescending(ed => ed.Length).FirstOrDefault();

                // 挂基准 A
                if (needDatum && datumFaceEdge != null) {
                    double px = horizFace ? (datumFaceEdge.X1 + datumFaceEdge.X2) * 0.25 + Math.Min(datumFaceEdge.X1, datumFaceEdge.X2) * 0.5 : datumFaceEdge.X1;
                    double py = horizFace ? datumFaceEdge.Y1 : (datumFaceEdge.Y1 + datumFaceEdge.Y2) * 0.5;
                    var sp = DwgWriter.ToSheet(sideView, px, py);
                    var textPos = horizFace ? new[] { sp[0], sideCbox[1] - 0.0085 } : new[] { sideCbox[0] - 0.0085, sp[1] };
                    if (w.Datum(sideView, datumFaceEdge, px, py, textPos, "A")) stats.HasDatumA = true;
                }

                // 轴承座块（如 02-003）：对面加平行度 ∥0.03 A；若 Below 为剖视则给剖视轴承孔壁加 ⊥Ø0.03 A + Ra1.6
                if (opt.TolBearings && stats.BearingBores > 0) {
                    if (opt.TolDatumsAndGdt && stats.HasDatumA && oppFaceEdge != null) {
                        double px = horizFace ? (oppFaceEdge.X1 + oppFaceEdge.X2) * 0.5 : oppFaceEdge.X1;
                        double py = horizFace ? oppFaceEdge.Y1 : (oppFaceEdge.Y1 + oppFaceEdge.Y2) * 0.5;
                        var sp = DwgWriter.ToSheet(sideView, px, py);
                        var tp = horizFace ? new[] { sideCbox[2] + 0.010, sp[1] + 0.006 } : new[] { sp[0] + 0.010, sideCbox[3] + 0.006 };
                        w.FcfOnEdge(sideView, oppFaceEdge, px, py, tp, DrawingTolerance.ParallelismFrameA);
                    }
                    // 在剖视 / 侧视图里查找轴承孔内壁竖线/横线挂 Ra1.6 与垂直度 ⊥Ø0.03 A
                    bool sideIsSec = false; try { sideIsSec = sideView.DrawingViewType == D.DrawingViewTypeConstants.igXSectionView; } catch { }
                    if (sideIsSec) {
                        double midX = (sbox[0] + sbox[2]) * 0.5;
                        // 寻找落在内部对称位置的较长轴承孔壁线（垂直于基准面）
                        var boreWall = se.Where(ed => ed.Kind == DwgKind.Line && ed.IsVertical && ed.X1 > sbox[0] + 0.002 && ed.X1 < sbox[2] - 0.002 && ed.Length >= 0.004)
                            .OrderByDescending(ed => ed.Length).FirstOrDefault();
                        if (boreWall != null) {
                            double wy = (boreWall.Y1 + boreWall.Y2) * 0.5;
                            if (opt.TolRoughness && w.Roughness(sideView, boreWall, new[] { boreWall.X1, wy }, DrawingTolerance.BearingRoughness)) {
                                stats.HasRoughness = true;
                            }
                            if (opt.TolDatumsAndGdt && stats.HasDatumA) {
                                var tp = new[] { sideCbox[2] + 0.010, DwgWriter.ToSheet(sideView, boreWall.X1, wy)[1] - 0.005 };
                                w.FcfOnEdge(sideView, boreWall, boreWall.X1, wy, tp, DrawingTolerance.PerpendicularityDiaFrameA);
                            }
                        }
                    }
                }

                // 导轨安装面：在安装面上加平面度 ⏥0.02 + Ra3.2；若存在 1.5~12mm 低靠山台阶，对靠山侧面加 ⊥0.02 A
                if (opt.TolGuideRails && stats.HasGuideRail && oppFaceEdge != null) {
                    double px = horizFace ? (oppFaceEdge.X1 + oppFaceEdge.X2) * 0.65 + Math.Min(oppFaceEdge.X1, oppFaceEdge.X2) * 0.35 : oppFaceEdge.X1;
                    double py = horizFace ? oppFaceEdge.Y1 : (oppFaceEdge.Y1 + oppFaceEdge.Y2) * 0.5;
                    if (opt.TolRoughness && w.Roughness(sideView, oppFaceEdge, new[] { px, py }, DrawingTolerance.MatingRoughness)) {
                        stats.HasRoughness = true;
                    }
                    if (opt.TolDatumsAndGdt) {
                        var sp = DwgWriter.ToSheet(sideView, px, py);
                        var tp = horizFace ? new[] { sp[0] + 0.012, sideCbox[3] + 0.009 } : new[] { sideCbox[2] + 0.010, sp[1] + 0.005 };
                        w.FcfOnEdge(sideView, oppFaceEdge, px, py, tp, DrawingTolerance.FlatnessFrame);
                        // 寻找邻近 1.5~12mm 的低靠山台阶竖边
                        if (horizFace && stats.HasDatumA) {
                            var shoulder = se.Where(ed => ed.Kind == DwgKind.Line && ed.IsVertical
                                && ed.X1 > sbox[0] + 0.002 && ed.X1 < sbox[2] - 0.002
                                && ed.Length >= 0.0015 && ed.Length <= 0.012
                                && (Math.Abs(Math.Max(ed.Y1, ed.Y2) - hi) <= tol || Math.Abs(Math.Min(ed.Y1, ed.Y2) - lo) <= tol))
                                .OrderByDescending(ed => ed.Length).FirstOrDefault();
                            if (shoulder != null) {
                                double sy = (shoulder.Y1 + shoulder.Y2) * 0.5;
                                var ssp = DwgWriter.ToSheet(sideView, shoulder.X1, sy);
                                w.FcfOnEdge(sideView, shoulder, shoulder.X1, sy, new[] { ssp[0] + 0.012, sideCbox[3] + 0.014 }, DrawingTolerance.PerpendicularityFrameA);
                            }
                        }
                    }
                }
            }

            // 3. 定位销孔开口面粗糙度 Ra3.2（当启用粗糙度且存在销孔时）
            if (opt.TolRoughness && pins.Count > 0) {
                if (MatingFaces(w, views, edges, pins, range) > 0) stats.HasRoughness = true;
            }
        }
    }
}
