using System;
using System.Collections.Generic;
using System.Linq;
using D=SolidEdgeDraft;
using F=SolidEdgeFramework;
using S=SolidEdgeFrameworkSupport;

namespace TianGongCadSuite {
    // 一键出图：把 DrawingPlan 算好的尺寸写进天工。
    //
    // 两条实测出来的硬规矩（2026-11-03 DrawProbe2/3，天工 225.03）：
    //   1. 传给 Dimensions.AddXxx / Balloons.AddByTerminator 的 x,y 是**图纸坐标**（ViewToSheet 换过的）；
    //   2. 图元要先包成引用 view.GetReferenceToGraphicMember2[图元] 再传。直接传 DVLine2d / DVCircle2d：尺寸值对，
    //      但画在图纸左下角（拿视图坐标当图纸坐标用了）；圆弧直接传 AddRadius 一律 0x80040223，包成引用就好了。
    public sealed class DwgWriter {
        readonly D.Sheet sheet;
        readonly S.Dimensions dims;
        public int Created, Failed;
        public readonly List<string> Notes = new List<string>();
        public DwgSpace Space;   // 建好的尺寸占的地方登记进去，后面的引线文字避开

        public DwgWriter(D.Sheet s){ sheet = s; dims = (S.Dimensions)s.Dimensions; }

        public static object Ref(D.DrawingView v, object member){
            try { return v.GetReferenceToGraphicMember2[member]; } catch { }
            F.Reference r; v.GetReferenceToGraphicMember(member, out r); return r;
        }
        public static double[] ToSheet(D.DrawingView v, double x, double y){ double a, b; v.ViewToSheet(x, y, out a, out b); return new[] { a, b }; }
        public static double[] SheetBox(D.DrawingView v){ double a, b, c, d; v.Range(out a, out b, out c, out d); return new[] { a, b, c, d }; }
        void Occupy(object o){ if (Space == null || o == null) return; try { double a, b, c, d; ((dynamic)o).Range(out a, out b, out c, out d); Space.Add(a, b, c, d); } catch { } }

        // 一串坐标尺寸。run 的坐标都是视图坐标；文字线、文字位置换成图纸坐标再交给天工。
        public int Ordinate(D.DrawingView v, OrdinateRun run, double scale){
            if (run == null) return 0;
            S.Dimension origin;
            var o = run.Origin;
            var op = ToSheet(v, o.X, o.Y);
            // 文字线：Horizontal 时是一条水平线（y = run.Line），文字竖排；否则是竖线（x = run.Line）
            Func<double, double[]> textAt = c => run.Horizontal ? ToSheet(v, c, run.Line) : ToSheet(v, run.Line, c);
            try {
                var tp = textAt(o.TextAt);
                origin = dims.AddCoordinateOriginEx(Ref(v, o.Edge.Tag), op[0], op[1], 0, o.KeyPoint, tp[0], tp[1], 0);
                Created++; Occupy(origin);
            } catch (Exception e) { Failed++; Notes.Add("坐标尺寸基准建不出来：" + e.Message); return 0; }
            int n = 1;
            foreach (var t in run.Targets) {
                try {
                    var p = ToSheet(v, t.X, t.Y);
                    var tp = textAt(t.Coord);
                    var d = dims.AddCoordinateEx(origin, Ref(v, t.Edge.Tag), p[0], p[1], 0, t.KeyPoint, tp[0], tp[1], 0);
                    // 挤在一起的文字错开：坐标尺寸打折线（探针 5：Jogged + SetJogPoint2 = 折过去的那条腿的位置，图纸坐标）
                    if (Math.Abs(t.TextAt - t.Coord) * scale > 0.0005) Jog(d, run, v, t);
                    Created++; n++; Occupy(d);
                } catch (Exception e) { Failed++; Log.Write("DrawingOrdinate", e); }
            }
            return n;
        }

        // 文字被排开的那几个：打折线，折过去的腿落在排开后的位置；两段直腿各 2.5mm
        static void Jog(S.Dimension d, OrdinateRun run, D.DrawingView v, DwgTarget t){
            try {
                var want = run.Horizontal ? ToSheet(v, t.TextAt, run.Line) : ToSheet(v, run.Line, t.TextAt);
                double a0, b0, c0, e0; d.Range(out a0, out b0, out c0, out e0);
                d.Jogged = true;
                try { d.SetJogPoint1Distance(0.0025); d.SetJogPoint3Distance(0.0025); } catch { }
                d.SetJogPoint2(want[0], want[1]);
                // 有时天工把折线甩出去老远（022 顶边那个文字跑到图框外）：折完的尺寸框比原来大出 15mm 以上就不折了，文字挤一点也比丢了强
                double a1, b1, c1, e1; d.Range(out a1, out b1, out c1, out e1);
                if (a1 < a0 - 0.015 || b1 < b0 - 0.015 || c1 > c0 + 0.015 || e1 > e0 + 0.015) d.Jogged = false;
            } catch (Exception e) { Log.Write("DrawingJog", e); }
        }

        // 孔：引线式直径，文字按 DrawingText.Hole 拼好的四段写（"4×" + 值/螺纹 + 贯通/深度 + 沉孔）。
        public bool HoleCallout(D.DrawingView v, HoleGroup g, string prefix, string valueOverride, string suffix, string subfix, double[] textPos){
            var c = g.Leader;
            S.Dimension d = null;
            try { d = dims.AddRadialDiameter(Ref(v, c.Tag)); }
            catch { try { d = dims.AddCircularDiameter(Ref(v, c.Tag)); } catch (Exception e) { Failed++; Notes.Add("孔 Ø" + g.DiameterMm + " 标不上：" + e.Message); return false; } }
            Created++;
            // 螺纹孔不显示底孔直径：规格写进前缀（「4×M5」），尺寸值设成不显示（OverrideString 在天工里显示不出来，e2e-4 的 017）
            if (valueOverride.Length > 0) {
                try { d.PrefixString = prefix + valueOverride; } catch { }
                try { d.DisplayType = S.DimDispTypeConstants.igDimDisplayTypeBlank; } catch { }
            } else { try { if (prefix.Length > 0) d.PrefixString = prefix; } catch { } }
            try { if (suffix.Length > 0) d.SuffixString = suffix; } catch { }
            try { if (subfix.Length > 0) d.SubfixString = subfix; } catch { }
            MoveText(d, textPos); Occupy(d);
            return true;
        }

        public bool Radius(D.DrawingView v, RadiusGroup g, double[] textPos){
            try {
                var d = dims.AddRadius(Ref(v, g.Leader.Tag));
                Created++;
                try { if (g.Count > 1) d.PrefixString = DrawingPlan.RadiusPrefix(g.Count); } catch { }
                MoveText(d, textPos); Occupy(d);
                return true;
            } catch (Exception e) { Failed++; Notes.Add("圆角 R" + g.RadiusMm + " 标不上：" + e.Message); return false; }
        }

        // 引线式尺寸的文字挪到 textPos（图纸坐标）：最后一个关键点是文字点（探针 2：E5 挪后文字跟着走、引线自动拉长）
        static void MoveText(S.Dimension d, double[] textPos){
            if (textPos == null) return;
            try { int k = d.KeyPointCount - 1; if (k >= 0) d.SetKeyPoint(k, textPos[0], textPos[1], 0); } catch (Exception e) { Log.Write("DrawingMoveText", e); }
        }

        // 引线文字（倒角「4-C2」、刻字）：箭头挂在图元上 (px,py 视图坐标)，文字放在 textPos（图纸坐标）。
        // 引线的折点用 AddVertex 加（文字跟着最后一个点走）—— 出图训练数据那套夹具里基准 / 形位公差 / 粗糙度都是这么挂的，真机验证过
        public S.Balloon Note(D.DrawingView v, DwgEdge e, double px, double py, string text, double[] textPos){
            try {
                var p = ToSheet(v, px, py);
                var b = ((S.Balloons)sheet.Balloons).AddByTerminator(Ref(v, e.Tag), p[0], p[1], 0, false);
                try { b.Callout = 1; } catch { }
                b.BalloonText = text;
                if (textPos != null) try { b.AddVertex(textPos[0], textPos[1], 0); } catch (Exception ex) { Log.Write("DrawingNoteVertex", ex); }
                Created++; Occupy(b);
                return b;
            } catch (Exception ex) { Failed++; Notes.Add("「" + text + "」引线没挂上：" + ex.Message); return null; }
        }

        // 两条平行线之间的距离（外形尺寸），horizontal=true 量水平距离，尺寸线放在视图外 gap（图纸米）：水平的放上方、竖直的放左边。
        // 选取点用线的上端 / 左端；TrackDistance 的正负天工没说清，建完量一下尺寸框，压在视图上就反过来
        public bool Distance(D.DrawingView v, DwgEdge a, DwgEdge b, bool horizontal, double gap){
            try {
                var box = SheetBox(v);
                Func<DwgEdge, double[]> pick = e => horizontal ? (e.Y1 > e.Y2 ? ToSheet(v, e.X1, e.Y1) : ToSheet(v, e.X2, e.Y2)) : (e.X1 < e.X2 ? ToSheet(v, e.X1, e.Y1) : ToSheet(v, e.X2, e.Y2));
                var pa = pick(a); var pb = pick(b);
                var d = dims.AddDistanceBetweenObjects(Ref(v, a.Tag), pa[0], pa[1], 0, false, Ref(v, b.Tag), pb[0], pb[1], 0, false);
                d.SetOrientation(horizontal ? S.DimensionOrientationConstants.igOrientationHorizontal : S.DimensionOrientationConstants.igOrientationVertical);
                double want = horizontal ? box[3] + gap - Math.Max(pa[1], pb[1]) : Math.Min(pa[0], pb[0]) - (box[0] - gap);
                d.TrackDistance = want;
                double x0, y0, x1, y1; d.Range(out x0, out y0, out x1, out y1);
                bool inside = horizontal ? y0 < box[3] - 1e-4 : x1 > box[0] + 1e-4;
                if (inside) { d.TrackDistance = -want; d.Range(out x0, out y0, out x1, out y1); }
                Created++; Occupy(d); return true;
            } catch (Exception e) { Failed++; Log.Write("DrawingDistance", e); return false; }
        }
    }
}
