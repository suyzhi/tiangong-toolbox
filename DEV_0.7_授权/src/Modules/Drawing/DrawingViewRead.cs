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
                    if (!Wanted(l.EdgeType) || IsHidden(l)) continue;
                    double a, b, c, d; l.GetStartPoint(out a, out b); l.GetEndPoint(out c, out d);
                    var e = DwgEdge.Line(a, b, c, d); e.Tag = l;
                    if (e.Length > 1e-7) r.Add(e);
                }
            } catch (Exception e) { Log.Write("DrawingViewRead.Lines", e); }
            try {
                foreach (D.DVCircle2d c in view.DVCircles2d) {
                    if (!Wanted(c.EdgeType) || IsHidden(c)) continue;
                    double x, y; c.GetCenterPoint(out x, out y);
                    var e = DwgEdge.Circle(x, y, c.Radius); e.Tag = c; r.Add(e);
                }
            } catch (Exception e) { Log.Write("DrawingViewRead.Circles", e); }
            try {
                foreach (D.DVArc2d a in view.DVArcs2d) {
                    if (!Wanted(a.EdgeType) || IsHidden(a)) continue;
                    double x, y, sx, sy, ex, ey; a.GetCenterPoint(out x, out y); a.GetStartPoint(out sx, out sy); a.GetEndPoint(out ex, out ey);
                    double sweep = 0; try { sweep = a.SweepAngle; } catch { }
                    var e = DwgEdge.Arc(x, y, a.Radius, sweep, sx, sy, ex, ey); e.Tag = a; r.Add(e);
                }
            } catch (Exception e) { Log.Write("DrawingViewRead.Arcs", e); }
            return r;
        }
        // 统计视图中被实体遮挡的隐藏模型圆数量（ShowHideEdgeOverride == Hide）：
        // 用于自动核验主视图/端面图是否看反了朝向（如电机法兰正面遮住背面圆法兰孔、车床轴看成了实心盲端）。
        public static int HiddenCircleCount(D.DrawingView view){
            return HiddenCircles(view).Count;
        }
        // 读取视图中被实体遮挡的隐藏模型圆（供精确核查是否存在被完全遮挡的真实三维孔）
        public static List<DwgEdge> HiddenCircles(D.DrawingView view){
            var r = new List<DwgEdge>();
            try {
                foreach (D.DVCircle2d c in view.DVCircles2d) {
                    if (!Wanted(c.EdgeType) || !IsHidden(c)) continue;
                    double x, y; c.GetCenterPoint(out x, out y);
                    var e = DwgEdge.Circle(x, y, c.Radius); e.Tag = c; r.Add(e);
                }
            } catch { }
            return r;
        }
        // 统计视图中被实体遮挡的隐藏模型圆弧数量（如车床轴背面的键槽圆弧）：用于车床主视图自动翻到键槽可见一侧。
        public static int HiddenArcCount(D.DrawingView view){
            int n = 0;
            try {
                foreach (D.DVArc2d a in view.DVArcs2d) {
                    if (Wanted(a.EdgeType) && IsHidden(a)) n++;
                }
            } catch { }
            return n;
        }
        // 螺纹线（天工把螺纹大径画成 3/4 圆的细实线，边类型 seThreadEdgeType）：同样过滤被遮挡的隐藏螺纹线
        public static List<DwgEdge> Threads(D.DrawingView view){
            var r = new List<DwgEdge>();
            try {
                foreach (D.DVCircle2d c in view.DVCircles2d) { if (c.EdgeType != D.GraphicMemberEdgeTypeConstants.seThreadEdgeType || IsHidden(c)) continue; double x, y; c.GetCenterPoint(out x, out y); r.Add(DwgEdge.Circle(x, y, c.Radius)); }
                foreach (D.DVArc2d a in view.DVArcs2d) { if (a.EdgeType != D.GraphicMemberEdgeTypeConstants.seThreadEdgeType || IsHidden(a)) continue; double x, y; a.GetCenterPoint(out x, out y); r.Add(DwgEdge.Circle(x, y, a.Radius)); }
            } catch (Exception e) { Log.Write("DrawingViewRead.Threads", e); }
            return r;
        }

        // 天工 VHL 引擎对被实体完全遮挡且未开启隐藏线的图元会标记 ShowHideEdgeOverride == DVShowHideEdgeOverrideHide；
        // 绝不能对不可见图元打孔标注或坐标尺寸（彻底杜绝"虚空孔尺寸"）。
        static bool IsHidden(D.DVLine2d e){ try { return e.ShowHideEdgeOverride == D.DVShowHideEdgeOverrideType.DVShowHideEdgeOverrideHide; } catch { return false; } }
        static bool IsHidden(D.DVCircle2d e){ try { return e.ShowHideEdgeOverride == D.DVShowHideEdgeOverrideType.DVShowHideEdgeOverrideHide; } catch { return false; } }
        static bool IsHidden(D.DVArc2d e){ try { return e.ShowHideEdgeOverride == D.DVShowHideEdgeOverrideType.DVShowHideEdgeOverrideHide; } catch { return false; } }

        static bool Wanted(D.GraphicMemberEdgeTypeConstants t){
            return t == D.GraphicMemberEdgeTypeConstants.seModelEdgeType || t == D.GraphicMemberEdgeTypeConstants.seSilhouetteEdgeType || t == D.GraphicMemberEdgeTypeConstants.seSectionEdgeType;
        }
    }
}
