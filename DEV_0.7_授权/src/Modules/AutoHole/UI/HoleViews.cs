using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Windows.Forms;

namespace TianGongCadSuite {
    // 打孔系列预览控件的公共画笔工具。
    internal static class Draw2 {
        public static string N(double v){ return v.ToString("0.##", CultureInfo.InvariantCulture); }

        // 带白色描边的文字：用缓存画刷 + 4 方向描边，比每字 9 次 DrawString + 2 次 SolidBrush 快一倍以上
        public static void Halo(Graphics g, string text, Font font, Color color, float x, float y){
            var save = g.TextRenderingHint;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            var wb = Ui.CachedBrush(Color.White);
            g.DrawString(text, font, wb, x - 1, y);
            g.DrawString(text, font, wb, x + 1, y);
            g.DrawString(text, font, wb, x, y - 1);
            g.DrawString(text, font, wb, x, y + 1);
            g.DrawString(text, font, Ui.CachedBrush(color), x, y);
            g.TextRenderingHint = save;
        }
        public static SizeF Measure(Graphics g, string text, Font font){ return g.MeasureString(text, font); }
        // 文字水平居中画在 (cx, y)
        public static void HaloCenter(Graphics g, string text, Font font, Color color, float cx, float y){
            var sz = Measure(g, text, font);
            Halo(g, text, font, color, cx - sz.Width / 2f, y);
        }
        public static void HaloRight(Graphics g, string text, Font font, Color color, float right, float y){
            var sz = Measure(g, text, font);
            Halo(g, text, font, color, right - sz.Width, y);
        }
        // 带边界钳制的描边文字：窄视图下自动贴边内收，绝不截断在画布之外
        public static void HaloClamped(Graphics g, string text, Font font, Color color, float x, float y, float maxW, float maxH){
            var sz = Measure(g, text, font);
            float cx = Math.Max(2f, Math.Min(x, Math.Max(2f, maxW - sz.Width - 2f)));
            float cy = Math.Max(2f, Math.Min(y, Math.Max(2f, maxH - sz.Height - 2f)));
            Halo(g, text, font, color, cx, cy);
        }

        public static void Arrow(Graphics g, Brush b, PointF tip, PointF from, float len){
            double dx = tip.X - from.X, dy = tip.Y - from.Y;
            double d = Math.Sqrt(dx * dx + dy * dy);
            if (d < 1e-6) return;
            dx /= d; dy /= d;
            double px = -dy, py = dx;
            float w = len * 0.34f;
            var pts = new PointF[] {
                tip,
                new PointF((float)(tip.X - dx * len + px * w), (float)(tip.Y - dy * len + py * w)),
                new PointF((float)(tip.X - dx * len - px * w), (float)(tip.Y - dy * len - py * w))
            };
            g.FillPolygon(b, pts);
        }

        // 水平尺寸：从 x1 到 x2 的尺寸线，画在 y 上（above=true 时文字在线上方）。
        public static void DimH(Graphics g, Pen pen, Brush brush, Font font, Color color,
                                float x1, float x2, float y, string text, bool above, bool inside){
            if (Math.Abs(x2 - x1) < 1) return;
            g.DrawLine(pen, x1, y, x2, y);
            float ext = above ? -6 : 6;
            g.DrawLine(pen, x1, y + ext, x1, y - ext);
            g.DrawLine(pen, x2, y + ext, x2, y - ext);
            var dir = new PointF(x2 > x1 ? x1 : x2, y);
            Arrow(g, brush, new PointF(x1, y), new PointF(x2, y), 7);
            Arrow(g, brush, new PointF(x2, y), new PointF(x1, y), 7);
            var sz = Measure(g, text, font);
            if (inside) {
                // 文字塞在两条尺寸界线中间
                HaloCenter(g, text, font, color, (x1 + x2) / 2f, y - sz.Height - 1);
            } else {
                HaloCenter(g, text, font, color, (x1 + x2) / 2f, above ? y - sz.Height - 3 : y + 3);
            }
        }

        // 竖直尺寸：从 y1 到 y2 的尺寸线，画在 x 上。
        public static void DimV(Graphics g, Pen pen, Brush brush, Font font, Color color,
                                float y1, float y2, float x, string text, bool left){
            if (Math.Abs(y2 - y1) < 1) return;
            g.DrawLine(pen, x, y1, x, y2);
            float ext = left ? -6 : 6;
            g.DrawLine(pen, x + ext, y1, x - ext, y1);
            g.DrawLine(pen, x + ext, y2, x - ext, y2);
            Arrow(g, brush, new PointF(x, y1), new PointF(x, y2), 7);
            Arrow(g, brush, new PointF(x, y2), new PointF(x, y1), 7);
            var sz = Measure(g, text, font);
            Halo(g, text, font, color, left ? x - sz.Width - 4 : x + 4, (y1 + y2) / 2f - sz.Height / 2f);
        }

        // 45° 剖面线，只画在给定的矩形里（调用方负责裁剪）。
        public static void Hatch(Graphics g, RectangleF r, Color color, int step){
            using (var p = new Pen(color, 1f)) {
                float span = r.Width + r.Height;
                for (float o = -r.Height; o < span; o += step) {
                    float x1 = r.Left + o, y1 = r.Bottom;
                    float x2 = r.Left + o + r.Height, y2 = r.Top;
                    g.DrawLine(p, x1, y1, x2, y2);
                }
            }
        }

        public static PointF[] Arc(float cx, float cy, float radius, double a1, double a2, int steps){
            var pts = new PointF[steps + 1];
            for (int i = 0; i <= steps; i++) {
                double a = a1 + (a2 - a1) * i / steps;
                pts[i] = new PointF((float)(cx + radius * Math.Cos(a)), (float)(cy + radius * Math.Sin(a)));
            }
            return pts;
        }

        public static void Placeholder(Graphics g, Rectangle r, string line1, string line2){
            var f1 = Ui.F9B; var f2 = Ui.F8;
            // 第二行可能很长（"螺纹深度 30 超过了孔深 10……"）：按视图宽度折行，不能两头被切掉
            int w = Math.Max(40, r.Width - 24);
            var s1 = g.MeasureString(line1, f1);
            var s2 = g.MeasureString(line2, f2, w);
            float y = r.Top + (r.Height - s1.Height - s2.Height - 6) / 2f;
            using (var b = new SolidBrush(Ui.Muted)) g.DrawString(line1, f1, b, r.Left + (r.Width - s1.Width) / 2f, y);
            using (var fmt = new StringFormat { Alignment = StringAlignment.Center })
            using (var b = new SolidBrush(Ui.Muted))
                g.DrawString(line2, f2, b, new RectangleF(r.Left + (r.Width - w) / 2f, y + s1.Height + 6, w, s2.Height + 2), fmt);
        }
    }

    // 2D 剖面：把一个孔按半剖面画出来，带尺寸标注。
    // 形状完全来自 HoleShape —— 和 CAD 实际切出来的几何是同一份定义。
    public sealed class HoleSectionView : Control {
        HoleShape shape;
        public string Header = "";
        public HoleShape Shape {
            get { return shape; }
            set { shape = value; Invalidate(); }
        }
        public HoleSectionView(){
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Ui.CardBg;
        }

        protected override void OnPaint(PaintEventArgs e){
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var all = new Rectangle(0, 0, Width, Height);
            using (var b = new SolidBrush(Ui.CardBg)) g.FillRectangle(b, all);
            if (Header.Length > 0) Draw2.Halo(g, Header, Ui.F8, Ui.Muted, 2, 1);
            if (shape == null || shape.Wall.Count < 3 || shape.Problem.Length > 0) {
                Draw2.Placeholder(g, all, shape != null && shape.Problem.Length > 0 ? "这个规格画不出来" : "还没有孔可以画",
                    shape != null && shape.Problem.Length > 0 ? shape.Problem : "点一个参考孔，或直接改下面的孔参数");
                return;
            }
            float ml = Ui.S(46), mr = Ui.S(48), mt = Ui.S(40), mb = Ui.S(26);
            var box = new RectangleF(ml, mt, Math.Max(20, Width - ml - mr), Math.Max(20, Height - mt - mb));
            double worldW = Math.Max(0.2, shape.MouthDiameterMm);
            double worldH = Math.Max(0.2, shape.ThicknessMm);
            float scale = (float)Math.Min(box.Width / worldW, box.Height / worldH);
            float axisX = box.Left + box.Width / 2f;
            float topY = box.Top + (box.Height - (float)(worldH * scale)) / 2f;
            float plateW = box.Width;
            float plateL = box.Left, plateR = box.Left + plateW;
            float plateT = topY, plateB = topY + (float)(worldH * scale);

            Func<double, float> X = r => axisX + (float)(r * scale);
            Func<double, float> Y = z => topY + (float)(z * scale);

            // 1) 材料块（剖面线）
            var matRect = new RectangleF(plateL, plateT, plateW, plateB - plateT);
            using (var b = new SolidBrush(Color.FromArgb(0xFA, 0xFB, 0xFD))) g.FillRectangle(b, matRect);

            // 2) 孔的空白区（右半轮廓 + 镜像）
            var voidPts = new List<PointF>();
            foreach (var p in shape.Wall) voidPts.Add(new PointF(X(p.R), Y(p.Z)));
            for (int i = shape.Wall.Count - 1; i >= 0; i--) voidPts.Add(new PointF(X(-shape.Wall[i].R), Y(shape.Wall[i].Z)));
            var voidPoly = voidPts.ToArray();

            // 剖面线只画在"材料"上：材料矩形减去孔（用 g.Save/Restore 避免 g.Clip Region 泄漏）
            using (var clip = new Region(matRect)) {
                using (var holePath = new GraphicsPath()) {
                    holePath.AddPolygon(voidPoly);
                    clip.Exclude(holePath);
                    var state = g.Save();
                    try {
                        g.SetClip(clip, CombineMode.Replace);
                        Draw2.Hatch(g, matRect, Color.FromArgb(0xDD, 0xE3, 0xEC), 7);
                    } finally { g.Restore(state); }
                }
            }
            using (var p = new Pen(Ui.Line)) g.DrawRectangle(p, plateL, plateT, plateW, plateB - plateT);

            // 3) 孔：填白 + 描轮廓
            using (var b = new SolidBrush(Ui.CardBg)) g.FillPolygon(b, voidPoly);
            using (var p = new Pen(Ui.Text, 1.7f)) {
                var right = new List<PointF>();
                foreach (var q in shape.Wall) right.Add(new PointF(X(q.R), Y(q.Z)));
                g.DrawLines(p, right.ToArray());
                var left = new List<PointF>();
                foreach (var q in shape.Wall) left.Add(new PointF(X(-q.R), Y(q.Z)));
                g.DrawLines(p, left.ToArray());
            }

            // 4) 装饰螺纹（螺纹孔按内小径建模，螺纹只画出来）
            if (shape.CosmeticThread) {
                float tr = X(shape.ThreadMajorMm / 2.0), tl = X(-shape.ThreadMajorMm / 2.0);
                using (var p = new Pen(Ui.Muted, 1f)) {
                    p.DashStyle = DashStyle.Dash;
                    float zTop = Y(shape.Chamfer ? shape.ChamferDepMm : 0);
                    double cylBottom = shape.VoidDepthMm - (shape.Through ? 0 : shape.TipHeightMm);
                    double threadEnd = Math.Min(shape.ThreadDepthMm > 0 ? shape.ThreadDepthMm : cylBottom, cylBottom);
                    float zBot = Y(threadEnd);
                    g.DrawLine(p, tr, zTop, tr, zBot);
                    g.DrawLine(p, tl, zTop, tl, zBot);
                    // 螺纹终止线：没攻到底时画一条横线，一眼看出"只攻了一段"
                    if (threadEnd < cylBottom - 1e-6) { p.DashStyle = DashStyle.Solid; g.DrawLine(p, tl, zBot, tr, zBot); }
                }
                string tt = shape.ThreadFull ? (shape.ThreadSize + " 螺纹全长") : (shape.ThreadSize + " 螺纹深 " + Draw2.N(shape.ThreadDepthMm));
                var tsz = Draw2.Measure(g, tt, Ui.F8);
                float ty = Y(Math.Min(shape.ThreadDepthMm, shape.VoidDepthMm) / 2.0) - tsz.Height / 2f;
                Draw2.Halo(g, tt, Ui.F8, Ui.Accent, Math.Max(2f, tl - tsz.Width - 10), ty);
            }

            // 5) 中心线
            using (var p = new Pen(Ui.Muted, 1f)) {
                p.DashPattern = new float[] { 7, 3, 2, 3 };
                g.DrawLine(p, axisX, plateT - 13, axisX, plateB + 13);
            }

            // 6) 尺寸标注（dimPen 必须释放，修复原先每次重绘泄漏 GDI+ Pen 句柄）
            using (var dimPen = new Pen(Ui.Accent, 1f))
            using (var dimBrush = new SolidBrush(Ui.Accent)) {
                var f = Ui.F8;
                // 孔口直径：有沉孔/锥沉就标孔口那个，否则标主孔；始终画在顶面上方，避免与倒角顶边和中线重叠
                string mouthText = "Φ" + Draw2.N(shape.MouthDiameterMm);
                if (shape.Kind == HoleKind.Counterbore) mouthText = "沉孔 Φ" + Draw2.N(shape.CounterboreDiaMm);
                else if (shape.Kind == HoleKind.Countersink) mouthText = "锥孔 Φ" + Draw2.N(shape.CountersinkDiaMm);
                else if (shape.Chamfer) mouthText = "Φ" + Draw2.N(shape.HoleDiameterMm);
                float dimY = plateT - Ui.S(14);
                float halfMouth = X(shape.MouthRadiusMm) - axisX;
                // 带孔口倒角时（通孔/螺纹孔/沉孔），标注宽度取倒角下沿实际直径而非倒角顶径 MouthRadiusMm
                float dimHalf = shape.Chamfer
                    ? (shape.Kind == HoleKind.Counterbore ? X(shape.CounterboreDiaMm / 2.0) - axisX : X(shape.HoleRadiusMm) - axisX)
                    : halfMouth;
                Draw2.DimH(g, dimPen, dimBrush, f, Ui.Accent, axisX - dimHalf, axisX + dimHalf, dimY, mouthText, true, false);

                // 主孔直径：孔口那个尺寸标的不是主孔时（沉孔/锥沉），在孔内再标一个。
                // 通孔/螺纹孔即使带了孔口倒角，上面标的也已经是主孔直径，不能再标一遍。
                bool mouthIsMain = shape.Kind == HoleKind.Through || shape.Kind == HoleKind.Tapped;
                if (!mouthIsMain) {
                    double z = shape.Kind == HoleKind.Counterbore ? shape.CounterboreDepMm
                             : (shape.Kind == HoleKind.Countersink ? shape.CountersinkDepMm : shape.ChamferDepMm);
                    float y = Y(z + Math.Min(shape.ThicknessMm * 0.12, 2.5));
                    if (y < plateB - 12) {
                        float h = X(shape.HoleRadiusMm) - axisX;
                        Draw2.DimH(g, dimPen, dimBrush, f, Ui.Accent, axisX - h, axisX + h, y,
                                   "Φ" + Draw2.N(shape.HoleDiameterMm), false, true);
                    }
                }

                // 沉孔深度：画在孔腔里（那里是空的，不挡任何东西）
                if (shape.Kind == HoleKind.Counterbore && shape.CounterboreDepMm > 0.05) {
                    float x = X(-shape.CounterboreDiaMm / 2.0) + 13;
                    Draw2.DimV(g, dimPen, dimBrush, f, Ui.Accent, plateT, Y(Math.Min(shape.CounterboreDepMm, shape.ThicknessMm)), x,
                               "深 " + Draw2.N(shape.CounterboreDepMm), false);
                }
                // 深度：盲孔标在主孔左侧，贯通孔标"贯通"
                if (!shape.Through) {
                    float x = plateL + 14;
                    Draw2.DimV(g, dimPen, dimBrush, f, Ui.Accent, plateT, Y(shape.DepthMm), x, "深 " + Draw2.N(shape.DepthMm), true);
                }
                // 板厚（示意）：右侧
                Draw2.DimV(g, dimPen, dimBrush, f, Ui.Accent, plateT, plateB, plateR - 14, "t " + Draw2.N(shape.ThicknessMm), false);

                // 倒角引线：按可用宽度钳制文字坐标，防止窄视图下右侧裁切
                if (shape.Chamfer) {
                    string chText = Draw2.N(shape.ChamferSetbackMm) + "×" + Draw2.N(shape.ChamferAngleDeg) + "°";
                    var chSz = Draw2.Measure(g, chText, f);
                    float zc = Y(shape.ChamferDepMm);
                    var tip = new PointF(X(shape.MouthRadiusMm), (Y(0) + zc) / 2f);
                    float tx = tip.X + 18;
                    float ty = Math.Max(2f, tip.Y - 12 - chSz.Height / 2f);
                    if (tx + chSz.Width > Width - 4) tx = Math.Max(2f, Width - 4 - chSz.Width);
                    var end = new PointF(tx > tip.X + 4 ? tx - 2 : tip.X + 6, ty + chSz.Height / 2f);
                    g.DrawLine(dimPen, tip, end);
                    Draw2.HaloClamped(g, chText, f, Ui.Accent, tx, ty, Width, Height);
                }
                // V 型底角度
                if (!shape.Through && shape.Bottom == HoleBottom.VBottom && shape.TipHeightMm > 0.05) {
                    var apex = new PointF(axisX, Y(shape.DepthMm));
                    float rr = Math.Min(18, Math.Max(9, (X(shape.HoleRadiusMm) - axisX) * 0.45f));
                    double a1 = Math.Atan2(Y(shape.DepthMm - shape.TipHeightMm) - apex.Y, X(shape.HoleRadiusMm) - apex.X);
                    double a2 = Math.Atan2(Y(shape.DepthMm - shape.TipHeightMm) - apex.Y, X(-shape.HoleRadiusMm) - apex.X);
                    using (var p = new Pen(Ui.Accent, 1f)) g.DrawLines(p, Draw2.Arc(apex.X, apex.Y, rr, a1, a2, 24));
                    Draw2.HaloCenter(g, Draw2.N(shape.BottomAngleDeg) + "°", f, Ui.Accent, apex.X, apex.Y - rr - 12);
                }
                // 锥沉角度
                if (shape.Kind == HoleKind.Countersink) {
                    Draw2.HaloClamped(g, Draw2.N(shape.CountersinkAngleDeg) + "°", f, Ui.Accent,
                                      axisX + halfMouth + 6, plateT + 2, Width, Height);
                }
            }

            // 7) 底部一行小字（宽度不足时自动精简并居中，避免高 DPI / 窄列下被右侧裁切）
            string cap = "剖面：按实际孔型绘制　·　板厚 " + Draw2.N(shape.ThicknessMm) + " 为示意";
            var capSz = Draw2.Measure(g, cap, Ui.F8);
            if (capSz.Width > Width - 8) {
                cap = "剖面　·　板厚 " + Draw2.N(shape.ThicknessMm) + " 为示意";
                capSz = Draw2.Measure(g, cap, Ui.F8);
            }
            if (capSz.Width > Width - 8) {
                cap = "剖面示意";
                capSz = Draw2.Measure(g, cap, Ui.F8);
            }
            Draw2.HaloCenter(g, cap, Ui.F8, Ui.Muted, Width / 2f, Height - capSz.Height - Ui.S(2));
        }
    }

    // 3D 参考：把孔打在材料上的样子画成轴测图。
    // 用"半剖轴测"——切掉靠观察者的前半块，露出孔的剖面，不然从外面看只有一个椭圆，
    // 分不出通孔/盲孔/沉孔。
    public sealed class HoleModelView : Control {
        HoleShape shape;
        public string Header = "";
        public HoleShape Shape {
            get { return shape; }
            set { shape = value; Invalidate(); }
        }
        public HoleModelView(){
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Ui.CardBg;
        }

        const double Cos30 = 0.8660254037844386;
        const double Sin30 = 0.5;

        protected override void OnPaint(PaintEventArgs e){
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(Ui.CardBg)) g.FillRectangle(b, ClientRectangle);
            if (Header.Length > 0) Draw2.Halo(g, Header, Ui.F8, Ui.Muted, 2, 1);
            if (shape == null || shape.Wall.Count < 3 || shape.Problem.Length > 0) {
                Draw2.Placeholder(g, ClientRectangle, shape != null && shape.Problem.Length > 0 ? "这个规格打不出来" : "还没有孔可以画",
                    shape != null && shape.Problem.Length > 0 ? shape.Problem : "点一个参考孔，就能看到打孔后的样子");
                return;
            }

            // 材料块：够大才看得出是一块板，但别把孔画成一个小点
            double t = shape.ThicknessMm;
            double slab = Math.Max(shape.MouthDiameterMm * 1.5, t * 1.6);
            double W = slab, D = slab;
            double cx = W / 2, cy = D / 2;
            double keep = cy;                  // 保留 y<=cy 的后半块，切掉靠观察者的前半块

            // 投影包围盒按"没切过的整块板"算：缩放稳定，切出来也不会忽大忽小
            double minEx = 1e9, maxEx = -1e9, minEy = 1e9, maxEy = -1e9;
            for (int i = 0; i < 8; i++) {
                double x = (i & 1) == 0 ? 0 : W;
                double y = (i & 2) == 0 ? 0 : D;
                double z = (i & 4) == 0 ? 0 : t;
                double ex = (x - y) * Cos30, ey = (x + y) * Sin30 + z;
                if (ex < minEx) minEx = ex; if (ex > maxEx) maxEx = ex;
                if (ey < minEy) minEy = ey; if (ey > maxEy) maxEy = ey;
            }
            // 孔口比板还宽时，投影范围要算上它，免得画到框外
            minEx -= shape.MouthRadiusMm; maxEx += shape.MouthRadiusMm;

            float pad = Ui.S(16);
            float topPad = Ui.S(16);
            float capH = Math.Max(Ui.S(16), Draw2.Measure(g, "半", Ui.F8).Height + Ui.S(4));
            float availW = Math.Max(30, Width - pad * 2);
            float availH = Math.Max(30, Height - pad * 2 - capH - topPad);
            float scale = (float)Math.Min(availW / Math.Max(1e-6, maxEx - minEx), availH / Math.Max(1e-6, maxEy - minEy));
            float offX = pad + (availW - (float)((maxEx - minEx) * scale)) / 2f - (float)(minEx * scale);
            float offY = pad + topPad + (availH - (float)((maxEy - minEy) * scale)) / 2f - (float)(minEy * scale);

            Func<double, double, double, PointF> P = (x, y, z) =>
                new PointF(offX + (float)((x - y) * Cos30 * scale), offY + (float)(((x + y) * Sin30 + z) * scale));

            // 1) 被切掉那半块的"虚影"（虚线）。不画它，看图的会以为这块料本来就长这样；
            //    画上它才读得出"这是剖开看里面的"。
            using (var ghost = new Pen(Color.FromArgb(0xC4, 0xCF, 0xDD), 1f)) {
                ghost.DashStyle = DashStyle.Dash;
                DrawBox(g, P, 0, W, keep, D, 0, t, ghost);
            }

            // 2) 保留半块的实体面：顶面 + 右侧面（观察者在 +x+y+z 方向）
            var topFace = new PointF[] { P(0,0,0), P(W,0,0), P(W,keep,0), P(0,keep,0) };
            var rightFace = new PointF[] { P(W,0,0), P(W,keep,0), P(W,keep,t), P(W,0,t) };
            using (var b = new SolidBrush(Color.FromArgb(0xEA, 0xF0, 0xF7))) g.FillPolygon(b, topFace);
            using (var b = new SolidBrush(Color.FromArgb(0xD9, 0xE2, 0xEE))) g.FillPolygon(b, rightFace);
            using (var p = new Pen(Color.FromArgb(0xA9, 0xB8, 0xCB), 1f)) {
                g.DrawPolygon(p, topFace);
                g.DrawPolygon(p, rightFace);
            }

            // 3) 孔口和看得进去的那部分内壁（剖面最后画，会自然遮住它前面该遮的部分）
            DrawHole(g, P, shape, cx, cy, t);

            // 4) 剖面：材料 + 剖面线 + 孔的空腔
            var cutFace = new PointF[] { P(0,keep,0), P(W,keep,0), P(W,keep,t), P(0,keep,t) };
            var voidPoly = new List<PointF>();
            foreach (var q in shape.Wall) voidPoly.Add(P(cx + q.R, keep, q.Z));
            for (int i = shape.Wall.Count - 1; i >= 0; i--) voidPoly.Add(P(cx - shape.Wall[i].R, keep, shape.Wall[i].Z));
            var voidArr = voidPoly.ToArray();

            using (var b = new SolidBrush(Color.FromArgb(0xF7, 0xFA, 0xFD))) g.FillPolygon(b, cutFace);
            using (var facePath = new GraphicsPath()) {
                facePath.AddPolygon(cutFace);
                using (var clip = new Region(facePath)) {
                    using (var hole = new GraphicsPath()) {
                        hole.AddPolygon(voidArr);
                        clip.Exclude(hole);
                        var state = g.Save();
                        try {
                            g.SetClip(clip, CombineMode.Replace);
                            Draw2.Hatch(g, new RectangleF(0, 0, Width, Height), Color.FromArgb(0xCF, 0xDB, 0xE8), 7);
                        } finally { g.Restore(state); }
                    }
                }
            }
            // 空腔按"背景色"填 —— 剖开看到的是空的，不该画成一块浅色实体
            using (var b = new SolidBrush(Ui.CardBg)) g.FillPolygon(b, voidArr);
            using (var p = new Pen(Ui.Text, 1.4f)) g.DrawPolygon(p, voidArr);
            using (var p = new Pen(Color.FromArgb(0x9A, 0xAB, 0xBF), 1.2f)) g.DrawPolygon(p, cutFace);

            // 5) 装饰螺纹：孔口一圈细虚线（CAD 里就是这么显示的）
            if (shape.CosmeticThread) {
                var pts = HalfArc(P, cx, cy, shape.ThreadMajorMm / 2.0, 0, 48);
                using (var p = new Pen(Ui.Muted, 1f)) { p.DashStyle = DashStyle.Dash; g.DrawLines(p, pts); }
            }

            // 6) 标注：只标最容易看错的那个特征
            var f = Ui.F8;
            if (shape.Kind == HoleKind.Counterbore)
                Leader(g, f, P(cx + shape.CounterboreDiaMm / 2.0, cy - shape.CounterboreDiaMm / 2.0 * 0.5, shape.CounterboreDepMm * 0.5),
                       "沉孔 Φ" + Draw2.N(shape.CounterboreDiaMm) + " 深 " + Draw2.N(shape.CounterboreDepMm));
            else if (shape.Kind == HoleKind.Countersink)
                Leader(g, f, P(cx + shape.CountersinkDiaMm / 2.0, cy - shape.CountersinkDiaMm / 2.0 * 0.5, shape.CountersinkDepMm * 0.5),
                       "锥孔 Φ" + Draw2.N(shape.CountersinkDiaMm) + " " + Draw2.N(shape.CountersinkAngleDeg) + "°");
            else if (shape.Chamfer)
                Leader(g, f, P(cx + shape.MouthRadiusMm, cy - shape.MouthRadiusMm * 0.5, shape.ChamferDepMm * 0.5),
                       "孔口倒角 " + Draw2.N(shape.ChamferSetbackMm) + "×" + Draw2.N(shape.ChamferAngleDeg) + "°");
            if (shape.CosmeticThread)
                Leader(g, f, P(cx + shape.ThreadMajorMm / 2.0, cy - shape.ThreadMajorMm / 2.0 * 0.55, 0),
                       shape.ThreadSize + " 装饰螺纹 " + (shape.ThreadFull ? "全长" : "深 " + Draw2.N(shape.ThreadDepthMm)));

            // 卡片标题已经写了"打完后的样子"，这里只留孔型和深度；太窄放不下就再短一点，别两头被切
            string cap = "半剖轴测　·　" + shape.KindName + (shape.Through ? " 贯通" : " 深 " + Draw2.N(shape.DepthMm))
                       + "　·　板厚 " + Draw2.N(shape.ThicknessMm) + " 为示意";
            var capSz = Draw2.Measure(g, cap, Ui.F8);
            if (capSz.Width > Width - 8) { cap = shape.KindName + (shape.Through ? " 贯通" : " 深 " + Draw2.N(shape.DepthMm)); capSz = Draw2.Measure(g, cap, Ui.F8); }
            Draw2.HaloCenter(g, cap, Ui.F8, Ui.Muted, Width / 2f, Height - capSz.Height - Ui.S(2));
        }

        // 立方体的 12 条棱（用来画"被切掉那半块"的虚影）
        static void DrawBox(Graphics g, Func<double,double,double,PointF> P,
                            double x0, double x1, double y0, double y1, double z0, double z1, Pen pen){
            var c = new PointF[8];
            for (int i = 0; i < 8; i++)
                c[i] = P((i & 1) == 0 ? x0 : x1, (i & 2) == 0 ? y0 : y1, (i & 4) == 0 ? z0 : z1);
            int[,] e = { {0,1},{1,3},{3,2},{2,0}, {4,5},{5,7},{7,6},{6,4}, {0,4},{1,5},{2,6},{3,7} };
            for (int i = 0; i < 12; i++) g.DrawLine(pen, c[e[i,0]], c[e[i,1]]);
        }

        // 保留 y<=cy 的那半圈：整圆的参数角从 π 走到 2π 时 sin<=0。
        static PointF[] HalfArc(Func<double,double,double,PointF> P, double cx, double cy, double r, double z, int steps){
            var pts = new PointF[steps + 1];
            for (int i = 0; i <= steps; i++) {
                double a = Math.PI + Math.PI * i / steps;
                pts[i] = P(cx + r * Math.Cos(a), cy + r * Math.Sin(a), z);
            }
            return pts;
        }

        // 两圈半圆弧之间夹出来的那片面（圆柱侧壁 / 圆锥面），也用来画沉孔底那种半环。
        static void Surface(Graphics g, Func<double,double,double,PointF> P, double cx, double cy,
                            double rTop, double zTop, double rBot, double zBot, Color fill){
            var top = HalfArc(P, cx, cy, rTop, zTop, 40);
            var bot = HalfArc(P, cx, cy, rBot, zBot, 40);
            var poly = new List<PointF>(top);
            for (int i = bot.Length - 1; i >= 0; i--) poly.Add(bot[i]);
            using (var b = new SolidBrush(fill)) g.FillPolygon(b, poly.ToArray());
            using (var p = new Pen(Color.FromArgb(0x7E, 0x90, 0xA6), 1f)) g.DrawLines(p, top);
        }

        // 孔口那半圈 + 能看进去的内壁。按实际 VoidDepthMm（而非板厚 t）截断盲孔，并绘制沉孔倒角与平底/V 型底。
        void DrawHole(Graphics g, Func<double,double,double,PointF> P, HoleShape s, double cx, double cy, double t){
            double r = s.HoleRadiusMm;
            double zEnd = Math.Min(s.VoidDepthMm, t);
            double zCylEnd = s.Through ? zEnd : Math.Max(0, zEnd - s.TipHeightMm);
            var deep = Color.FromArgb(0x6B, 0x7E, 0x95);      // 孔内壁（背光，深）
            var seat = Color.FromArgb(0x93, 0xA6, 0xBC);      // 锥座 / 倒角 / 钻尖锥底
            var floor = Color.FromArgb(0xC9, 0xD7, 0xE6);     // 沉孔底 / 平底（平面，受光）

            if (s.Kind == HoleKind.Counterbore) {
                double cbR = s.CounterboreDiaMm / 2.0;
                double zTop = 0;
                if (s.Chamfer) {
                    zTop = Math.Min(s.ChamferDepMm, zEnd);
                    Surface(g, P, cx, cy, s.MouthRadiusMm, 0, cbR, zTop, seat);   // 沉孔口倒角
                }
                double dep = Math.Max(zTop, Math.Min(s.CounterboreDepMm, zEnd));
                if (dep > zTop) Surface(g, P, cx, cy, cbR, zTop, cbR, dep, deep); // 沉孔侧壁
                Surface(g, P, cx, cy, r, dep, cbR, dep, floor);                   // 沉孔底（半环）
                double mainEnd = Math.Max(dep, zCylEnd);
                if (mainEnd > dep) Surface(g, P, cx, cy, r, dep, r, mainEnd, deep); // 下面那段主孔
                DrawBlindBottom(g, P, s, cx, cy, r, mainEnd, zEnd, seat, floor);
                using (var p = new Pen(Color.FromArgb(0x5C, 0x6E, 0x84), 1.4f)) g.DrawLines(p, HalfArc(P, cx, cy, s.Chamfer ? s.MouthRadiusMm : cbR, 0, 48));
            } else if (s.Kind == HoleKind.Countersink) {
                double csR = s.CountersinkDiaMm / 2.0;
                double dep = Math.Min(s.CountersinkDepMm, zEnd);
                Surface(g, P, cx, cy, csR, 0, r, dep, seat);                      // 锥座
                double mainEnd = Math.Max(dep, zCylEnd);
                if (mainEnd > dep) Surface(g, P, cx, cy, r, dep, r, mainEnd, deep); // 主孔
                DrawBlindBottom(g, P, s, cx, cy, r, mainEnd, zEnd, seat, floor);
                using (var p = new Pen(Color.FromArgb(0x5C, 0x6E, 0x84), 1.4f)) g.DrawLines(p, HalfArc(P, cx, cy, csR, 0, 48));
            } else {
                double z0 = 0;
                if (s.Chamfer) {
                    z0 = Math.Min(s.ChamferDepMm, zEnd);
                    Surface(g, P, cx, cy, s.MouthRadiusMm, 0, r, z0, seat);
                }
                double mainEnd = Math.Max(z0, zCylEnd);
                if (mainEnd > z0) Surface(g, P, cx, cy, r, z0, r, mainEnd, deep);
                DrawBlindBottom(g, P, s, cx, cy, r, mainEnd, zEnd, seat, floor);
                using (var p = new Pen(Color.FromArgb(0x5C, 0x6E, 0x84), 1.4f)) g.DrawLines(p, HalfArc(P, cx, cy, s.MouthRadiusMm, 0, 48));
            }
        }
        static void DrawBlindBottom(Graphics g, Func<double,double,double,PointF> P, HoleShape s, double cx, double cy, double r, double zCylEnd, double zEnd, Color seat, Color floor){
            if (s.Through) return;
            if (s.Bottom == HoleBottom.VBottom && zEnd > zCylEnd + 1e-6) {
                var rim = HalfArc(P, cx, cy, r, zCylEnd, 40);
                var poly = new List<PointF>(rim);
                poly.Add(P(cx, cy, zEnd));
                using (var b = new SolidBrush(seat)) g.FillPolygon(b, poly.ToArray());
                using (var p = new Pen(Color.FromArgb(0x7E, 0x90, 0xA6), 1f)) g.DrawLines(p, rim);
            } else {
                var rim = HalfArc(P, cx, cy, r, zEnd, 40);
                using (var b = new SolidBrush(floor)) g.FillPolygon(b, rim);
                using (var p = new Pen(Color.FromArgb(0x7E, 0x90, 0xA6), 1f)) g.DrawLines(p, rim);
            }
        }

        void Leader(Graphics g, Font f, PointF at, string text){
            var sz = Draw2.Measure(g, text, f);
            float x = at.X + 20, y = at.Y - sz.Height - 6;
            if (x + sz.Width > Width - 4) x = Width - 4 - sz.Width;
            if (x < 4) x = 4;
            if (y < 4) y = at.Y + 10;
            float maxY = Math.Max(4f, Height - sz.Height - Ui.S(22));
            if (y > maxY) y = maxY;
            using (var p = new Pen(Ui.Accent, 1f)) {
                g.DrawLine(p, at, new PointF(x - 3, y + sz.Height / 2f));
                using (var b = new SolidBrush(Ui.Accent)) Draw2.Arrow(g, b, at, new PointF(x, y + sz.Height / 2f), 7);
            }
            Draw2.Halo(g, text, f, Ui.Accent, x, y);
        }
    }
}
