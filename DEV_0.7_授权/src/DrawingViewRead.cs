using System;
using System.Collections.Generic;
using D=SolidEdgeDraft;

namespace TianGongCadSuite {
    // 一键出图：把一个视图里的二维图元读成 DwgEdge（视图坐标，米）。
    // 只要模型边和轮廓边（seModelEdgeType / seSilhouetteEdgeType）：螺纹线、折弯中心线、坐标系线不参与标注。
    // 实测（2026-11-03 DrawProbe）：DVLines2d 的端点是视图坐标（模型单位、未乘比例），ViewToSheet 才换到纸面；
    // 建尺寸时传的选取点也必须是视图坐标 —— 传纸面坐标尺寸会挂到错的位置、量出错的值。
    public static class DrawingViewRead {
        public static List<DwgEdge> Edges(D.DrawingView view){
            var r = new List<DwgEdge>();
            try {
                foreach (D.DVLine2d l in view.DVLines2d) {
                    if (!Wanted(l.EdgeType)) continue;
                    double a, b, c, d; l.GetStartPoint(out a, out b); l.GetEndPoint(out c, out d);
                    var e = DwgEdge.Line(a, b, c, d); e.Tag = l;
                    if (e.Length > 1e-7) r.Add(e);
                }
            } catch (Exception e) { Log.Write("DrawingViewRead.Lines", e); }
            try {
                foreach (D.DVCircle2d c in view.DVCircles2d) {
                    if (!Wanted(c.EdgeType)) continue;
                    double x, y; c.GetCenterPoint(out x, out y);
                    var e = DwgEdge.Circle(x, y, c.Radius); e.Tag = c; r.Add(e);
                }
            } catch (Exception e) { Log.Write("DrawingViewRead.Circles", e); }
            try {
                foreach (D.DVArc2d a in view.DVArcs2d) {
                    if (!Wanted(a.EdgeType)) continue;
                    double x, y, sx, sy, ex, ey; a.GetCenterPoint(out x, out y); a.GetStartPoint(out sx, out sy); a.GetEndPoint(out ex, out ey);
                    double sweep = 0; try { sweep = a.SweepAngle; } catch { }
                    var e = DwgEdge.Arc(x, y, a.Radius, sweep, sx, sy, ex, ey); e.Tag = a; r.Add(e);
                }
            } catch (Exception e) { Log.Write("DrawingViewRead.Arcs", e); }
            return r;
        }
        // 螺纹线（天工把螺纹大径画成 3/4 圆的细实线，边类型 seThreadEdgeType）：没有孔特征的螺纹孔靠它认 M 几
        public static List<DwgEdge> Threads(D.DrawingView view){
            var r = new List<DwgEdge>();
            try {
                foreach (D.DVCircle2d c in view.DVCircles2d) { if (c.EdgeType != D.GraphicMemberEdgeTypeConstants.seThreadEdgeType) continue; double x, y; c.GetCenterPoint(out x, out y); r.Add(DwgEdge.Circle(x, y, c.Radius)); }
                foreach (D.DVArc2d a in view.DVArcs2d) { if (a.EdgeType != D.GraphicMemberEdgeTypeConstants.seThreadEdgeType) continue; double x, y; a.GetCenterPoint(out x, out y); r.Add(DwgEdge.Circle(x, y, a.Radius)); }
            } catch (Exception e) { Log.Write("DrawingViewRead.Threads", e); }
            return r;
        }

        static bool Wanted(D.GraphicMemberEdgeTypeConstants t){
            return t == D.GraphicMemberEdgeTypeConstants.seModelEdgeType || t == D.GraphicMemberEdgeTypeConstants.seSilhouetteEdgeType || t == D.GraphicMemberEdgeTypeConstants.seSectionEdgeType;
        }
    }
}
