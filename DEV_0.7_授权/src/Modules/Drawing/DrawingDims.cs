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
        public string StyleName = "";
        readonly List<double[]> calloutBoxes = new List<double[]>();   // 已放置的引线标注文字框（实测碰撞兜底防重叠）

        public DwgWriter(D.Sheet s){ sheet = s; dims = (S.Dimensions)s.Dimensions; }
        public DwgWriter(D.Sheet s, D.DraftDocument doc, string styleName) : this(s){
            StyleName = styleName ?? "";
            if (!string.IsNullOrEmpty(StyleName)) ApplySheetStyle(doc, s, StyleName);
        }

        // 读取工程图文档里全部尺寸样式名称（供 DrawingStyleRules 择优）
        public static List<string> AvailableStyles(D.DraftDocument doc){
            var list = new List<string>();
            if (doc == null) return list;
            try {
                var styles = (S.DimensionStyles)doc.DimensionStyles;
                for (int i = 1; i <= styles.Count; i++) {
                    try { string n = styles.Item(i).Name; if (!string.IsNullOrEmpty(n)) list.Add(n); } catch { }
                }
            } catch (Exception e) { Log.Write("DrawingAvailableStyles", e); }
            return list;
        }

        // 按 1-based 索引查找指定名称的 DimensionStyle 对象（天工 225 上 styles.Item(string) 报 0x80040223，必须按索引遍历）
        public static S.DimensionStyle FindStyleObject(D.DraftDocument doc, string styleName){
            if (doc == null || string.IsNullOrEmpty(styleName)) return null;
            try {
                var styles = (S.DimensionStyles)doc.DimensionStyles;
                S.DimensionStyle ci = null;
                for (int i = 1; i <= styles.Count; i++) {
                    try {
                        var st = styles.Item(i);
                        string n = st.Name ?? "";
                        if (string.Equals(n, styleName, StringComparison.Ordinal)) return st;
                        if (ci == null && string.Equals(n, styleName, StringComparison.OrdinalIgnoreCase)) ci = st;
                    } catch { }
                }
                return ci;
            } catch (Exception e) {
                Log.Write("DrawingFindStyleObject", e);
                return null;
            }
        }

        // 读取工程图文档当前的活动尺寸样式名（缺失公司样式时用于检索中心线后恢复原活动样式）
        public static string GetActiveStyle(D.DraftDocument doc){
            if (doc == null) return "";
            try { return ((S.DimensionStyles)doc.DimensionStyles).Active ?? ""; }
            catch (Exception e) { Log.Write("DrawingGetActiveStyle", e); return ""; }
        }

        // 切换工程图文档的当前活动尺寸样式（新建尺寸 / 气泡 / 形位公差 / 粗糙度一律继承当前活动样式）
        public static bool SetActiveStyle(D.DraftDocument doc, string styleName){
            if (doc == null || string.IsNullOrEmpty(styleName)) return false;
            try {
                var styles = (S.DimensionStyles)doc.DimensionStyles;
                styles.Active = styleName;
                return true;
            } catch (Exception e) {
                Log.Write("DrawingSetActiveStyle", e);
                return false;
            }
        }

        // 双重锁定图纸级尺寸样式：同时设文档活动样式 + 4 类标注集合（Dimensions/Balloons/FCF/SurfaceFinish）的 Style 对象，
        // 避免天工在交互界面、样式映射或视图操作下回退到模板保存时的默认样式（A3-孔 / A4-水平2 / ISO）。
        public static bool ApplySheetStyle(D.DraftDocument doc, D.Sheet sheet, string styleName){
            if (doc == null || string.IsNullOrEmpty(styleName)) return false;
            bool ok = SetActiveStyle(doc, styleName);
            var st = FindStyleObject(doc, styleName);
            if (sheet != null && st != null) {
                try { ((S.Dimensions)sheet.Dimensions).Style = st; ok = true; } catch (Exception e) { Log.Write("DrawingDimsStyle", e); }
                try { ((S.Balloons)sheet.Balloons).Style = st; } catch { }
                try { ((S.FeatureControlFrames)sheet.FeatureControlFrames).Style = st; } catch { }
                try { ((S.SurfaceFinishSymbols)sheet.SurfaceFinishSymbols).Style = st; } catch { }
            }
            return ok;
        }

        public static void ClearCollectionStyles(D.Sheet sheet){
            if (sheet == null) return;
            try { ((S.Dimensions)sheet.Dimensions).ClearStyle(); } catch { }
            try { ((S.Balloons)sheet.Balloons).ClearStyle(); } catch { }
            try { ((S.FeatureControlFrames)sheet.FeatureControlFrames).ClearStyle(); } catch { }
            try { ((S.SurfaceFinishSymbols)sheet.SurfaceFinishSymbols).ClearStyle(); } catch { }
        }

        // 逐个新建对象兜底核验：紧跟在 AddXxx 之后、修改前后缀/公差/折线之前调用；若样式不是目标样式则强制写回 Style.Name。
        public static void EnsureStyle(S.Dimension d, string styleName){
            if (d == null || string.IsNullOrEmpty(styleName)) return;
            try {
                var st = d.Style;
                if (st != null && !string.Equals(st.Name, styleName, StringComparison.Ordinal)) st.Name = styleName;
            } catch (Exception e) { Log.Write("DrawingEnsureDimStyle", e); }
        }
        public static void EnsureStyle(S.Balloon b, string styleName){
            if (b == null || string.IsNullOrEmpty(styleName)) return;
            try {
                var st = b.Style;
                if (st != null && !string.Equals(st.Name, styleName, StringComparison.Ordinal)) st.Name = styleName;
            } catch (Exception e) { Log.Write("DrawingEnsureBalloonStyle", e); }
        }
        public static void EnsureStyle(S.FeatureControlFrame f, string styleName){
            if (f == null || string.IsNullOrEmpty(styleName)) return;
            try {
                var st = f.Style;
                if (st != null && !string.Equals(st.Name, styleName, StringComparison.Ordinal)) st.Name = styleName;
            } catch (Exception e) { Log.Write("DrawingEnsureFcfStyle", e); }
        }
        public static void EnsureStyle(S.SurfaceFinishSymbol s, string styleName){
            if (s == null || string.IsNullOrEmpty(styleName)) return;
            try {
                var st = s.Style;
                if (st != null && !string.Equals(st.Name, styleName, StringComparison.Ordinal)) st.Name = styleName;
            } catch (Exception e) { Log.Write("DrawingEnsureSfsStyle", e); }
        }

        public static object Ref(D.DrawingView v, object member){
            try { return v.GetReferenceToGraphicMember2[member]; } catch { }
            F.Reference r; v.GetReferenceToGraphicMember(member, out r); return r;
        }
        public static double[] ToSheet(D.DrawingView v, double x, double y){ double a, b; v.ViewToSheet(x, y, out a, out b); return new[] { a, b }; }
        public static double[] SheetBox(D.DrawingView v){ double a, b, c, d; v.Range(out a, out b, out c, out d); return new[] { a, b, c, d }; }
        static double[] ContourSheetBox(D.DrawingView v, double[] contourBox){
            if (contourBox != null && contourBox.Length >= 4 && contourBox[2] > contourBox[0] && contourBox[3] > contourBox[1]) {
                var p0 = ToSheet(v, contourBox[0], contourBox[1]);
                var p1 = ToSheet(v, contourBox[2], contourBox[3]);
                return new[] { Math.Min(p0[0], p1[0]), Math.Min(p0[1], p1[1]), Math.Max(p0[0], p1[0]), Math.Max(p0[1], p1[1]) };
            }
            return SheetBox(v);
        }
        void Occupy(object o){ if (Space == null || o == null) return; try { double a, b, c, d; ((dynamic)o).Range(out a, out b, out c, out d); Space.Add(a, b, c, d); } catch { } }

        // 一串坐标尺寸。run 的坐标都是视图坐标；文字线、文字位置换成图纸坐标再交给天工。
        // 只有 2 个落点（例如板厚 1 / 4 / 20）时改用干净的线性尺寸（并按跨度自动翻转端符），避免「0」「1」叠成蓝块。
        public int Ordinate(D.DrawingView v, OrdinateRun run, double scale){
            return Ordinate(v, run, scale, null);
        }
        public int Ordinate(D.DrawingView v, OrdinateRun run, double scale, double[] contourBox){
            if (run == null) return 0;
            var cbox = ContourSheetBox(v, contourBox);
            if (DrawingStyleRules.ShouldUseLinearSpan(run) && LinearSpan(v, run, scale, cbox)) return 2;

            S.Dimension origin;
            var o = run.Origin;
            var op = ToSheet(v, o.X, o.Y);
            // 文字线：Horizontal 时是一条水平线（y = run.Line），文字竖排；否则是竖线（x = run.Line）
            Func<double, double[]> textAt = c => run.Horizontal ? ToSheet(v, c, run.Line) : ToSheet(v, run.Line, c);
            try {
                // 基准固定落在自身真实坐标上（AddCoordinateOriginEx 无法被折走）
                var tp = textAt(o.Coord);
                origin = dims.AddCoordinateOriginEx(Ref(v, o.Edge.Tag), op[0], op[1], 0, o.KeyPoint, tp[0], tp[1], 0);
                EnsureStyle(origin, StyleName);
                try { if (Math.Abs(origin.TrackDistance) > 1e-6) origin.TrackDistance = 0; } catch { }
                Created++; Occupy(origin); RecordCoordText(origin);
            } catch (Exception e) { Failed++; Notes.Add("坐标尺寸基准建不出来：" + e.Message); return 0; }
            int n = 1;
            // 按到基准的距离由近及远依次添加，交给天工原生坐标串自动避让折线（2026-11-23 DrawJogProbe 实测：
            // 天工 AddCoordinateEx 已按字高自动做 3.49/3.81mm 等距无重叠折线；此前手工调用 SetJogPoint2 会把
            // 已自动折线的末尾尺寸甩飞 -205mm 触发 flung 复位 Jogged=false，反把 290/300、16/21、12/15 砸在一起）。
            foreach (var t in run.Targets.OrderBy(x => Math.Abs(x.Coord - o.Coord))) {
                try {
                    var p = ToSheet(v, t.X, t.Y);
                    var tp = textAt(t.Coord);
                    var d = dims.AddCoordinateEx(origin, Ref(v, t.Edge.Tag), p[0], p[1], 0, t.KeyPoint, tp[0], tp[1], 0);
                    EnsureStyle(d, StyleName);
                    // 落点在零件深处时天工新建的坐标尺寸会带负的 TrackDistance，归零即与基准齐平
                    try { if (Math.Abs(d.TrackDistance) > 1e-6) d.TrackDistance = 0; } catch { }
                    Created++; n++; Occupy(d); RecordCoordText(d);
                } catch (Exception e) { Failed++; Log.Write("DrawingOrdinate", e); }
            }
            return n;
        }

        // 把坐标尺寸文字/基准圈区域登记进 DwgSpace.dimTexts，使后续孔/圆弧引线自动绕开「0」圈和坐标数字
        void RecordCoordText(S.Dimension d){
            if (Space == null || d == null) return;
            try {
                int kc = d.KeyPointCount;
                int idx = kc > 5 ? 5 : (kc >= 3 ? 2 : kc - 1);
                if (idx < 0) return;
                double tx, ty, kz; F.KeyPointType kt; int hh;
                d.GetKeyPoint(idx, out tx, out ty, out kz, out kt, out hh);
                if (Math.Abs(tx) < 1e-9 && Math.Abs(ty) < 1e-9) return;
                Space.AddDimText(new[] { tx - 0.0028, ty - 0.0028, tx + 0.0028, ty + 0.0028 });
            } catch { }
        }

        // 2 落点单跨度（板厚、单台阶、侧视总宽）用线性尺寸代替 2 点坐标串，摆在 run 指定的 LowSide 一侧。
        // 窄跨度自动翻转端符（外箭头朝里指 -->|<--），极窄跨度将数字引到箭头外侧防止压线。
        bool LinearSpan(D.DrawingView v, OrdinateRun run, double scale, double[] cbox){
            var a = run.Origin;
            var b = run.Targets[0];
            S.Dimension d = null;
            try {
                var pa = ToSheet(v, a.X, a.Y);
                var pb = ToSheet(v, b.X, b.Y);
                d = dims.AddDistanceBetweenObjects(
                    Ref(v, a.Edge.Tag), pa[0], pa[1], 0, a.KeyPoint,
                    Ref(v, b.Edge.Tag), pb[0], pb[1], 0, b.KeyPoint);
                EnsureStyle(d, StyleName);
                d.SetOrientation(run.Horizontal ? S.DimensionOrientationConstants.igOrientationHorizontal : S.DimensionOrientationConstants.igOrientationVertical);
                double expected = Math.Abs(b.Coord - a.Coord);
                if (expected > 1e-6 && Math.Abs(d.Value - expected) > Math.Max(1e-4, expected * 0.02)) {
                    try { d.Delete(); } catch { }
                    return false;
                }
                var linePt = run.Horizontal ? ToSheet(v, (a.X + b.X) / 2, run.Line) : ToSheet(v, run.Line, (a.Y + b.Y) / 2);
                double targetCoord = run.Horizontal ? linePt[1] : linePt[0];
                double want;
                if (run.Horizontal) want = run.LowSide ? Math.Min(pa[1], pb[1]) - targetCoord : targetCoord - Math.Max(pa[1], pb[1]);
                else want = run.LowSide ? Math.Min(pa[0], pb[0]) - targetCoord : targetCoord - Math.Max(pa[0], pb[0]);
                if (want < 0.005) want = 0.0075;
                d.TrackDistance = want;
                double x0, y0, x1, y1; d.Range(out x0, out y0, out x1, out y1);
                double err1 = LinearSideError(x0, y0, x1, y1, cbox, targetCoord, run.Horizontal, run.LowSide);
                if (err1 > 0.001) {
                    d.TrackDistance = -want;
                    double ax0, ay0, ax1, ay1; d.Range(out ax0, out ay0, out ax1, out ay1);
                    double err2 = LinearSideError(ax0, ay0, ax1, ay1, cbox, targetCoord, run.Horizontal, run.LowSide);
                    if (err2 > err1) d.TrackDistance = want;
                }
                ApplyLinearTerminator(d, scale);
                Created++; Occupy(d);
                return true;
            } catch (Exception e) {
                if (d != null) try { d.Delete(); } catch { }
                Log.Write("DrawingLinearSpan", e);
                return false;
            }
        }

        // 线性尺寸端符优化：窄跨度翻转端符让两个箭头从外朝里指（-->| 4 |<--），极窄跨度顺带把数字移到单侧箭头外
        static void ApplyLinearTerminator(S.Dimension d, double scale){
            if (d == null) return;
            try {
                string valText = Math.Round(d.Value * 1000.0, 2).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
                if (DrawingStyleRules.ShouldFlipLinearTerminator(d.Value, scale, valText)) {
                    d.TerminatorPosition = true;
                    if (DrawingStyleRules.ShouldBreakNarrowLinearText(d.Value, scale, valText)) {
                        try {
                            d.BreakPosition = S.DimBreakPositionConstants.igDimBreakRight;
                            d.BreakDistance = DrawingStyleRules.NarrowLinearBreakDistance(valText);
                        } catch { }
                    }
                }
            } catch (Exception e) { Log.Write("DrawingLinearTerminator", e); }
        }

        static double LinearSideError(double x0, double y0, double x1, double y1, double[] cbox, double targetCoord, bool horizontal, bool lowSide){
            // 优先确保尺寸不在视图轮廓反侧，再尽量靠近 targetCoord
            double pen = 0;
            if (horizontal) {
                if (lowSide) { if (y0 > cbox[1] + 0.001) pen += 1.0; return pen + Math.Abs(y0 - targetCoord); }
                if (y1 < cbox[3] - 0.001) pen += 1.0; return pen + Math.Abs(y1 - targetCoord);
            }
            if (lowSide) { if (x0 > cbox[0] + 0.001) pen += 1.0; return pen + Math.Abs(x0 - targetCoord); }
            if (x1 < cbox[2] - 0.001) pen += 1.0; return pen + Math.Abs(x1 - targetCoord);
        }

        // 孔：引线式直径，文字按 DrawingText.Hole 拼好的四段写（"4×" + 值/螺纹 + 贯通/深度 + 沉孔）。
        // 统一翻转 TerminatorPosition=true 让两端箭头均落于圆外朝圆心指（-->○<--），杜绝小孔箭头缩在孔内从内朝外挤成一团。
        public bool HoleCallout(D.DrawingView v, HoleGroup g, string prefix, string valueOverride, string suffix, string subfix, double[] textPos){
            var c = g.Leader;
            S.Dimension d = null;
            try { d = dims.AddRadialDiameter(Ref(v, c.Tag)); }
            catch { try { d = dims.AddCircularDiameter(Ref(v, c.Tag)); } catch (Exception e) { Failed++; Notes.Add("孔 Ø" + g.DiameterMm + " 标不上：" + e.Message); return false; } }
            EnsureStyle(d, StyleName);
            Created++;
            // 螺纹孔不显示底孔直径：规格写进前缀（「4×M5」），尺寸值设成不显示（OverrideString 在天工里显示不出来，e2e-4 的 017）
            if (valueOverride.Length > 0) {
                try { d.PrefixString = prefix + valueOverride; } catch { }
                try { d.DisplayType = S.DimDispTypeConstants.igDimDisplayTypeBlank; } catch { }
            } else { try { if (prefix.Length > 0) d.PrefixString = prefix; } catch { } }
            try { if (suffix.Length > 0) d.SuffixString = suffix; } catch { }
            try { if (subfix.Length > 0) d.SubfixString = subfix; } catch { }
            var hs = ToSheet(v, c.Cx, c.Cy);
            PlaceAndGuardCallout(d, textPos, () => {
                try { d.TerminatorPosition = true; } catch { }
                // 实测（02-023 D33 / 02-029 D8）：引线往下拉时天工偶尔会把 LeaderDistance 塌缩成 0（水平横线消失、文字回缩压线）；
                // 检测到塌缩时强制恢复 ±4.5mm 水平引线肩部，保持干净的水平下划线引出。
                try {
                    if (Math.Abs(d.LeaderDistance) < 0.001) {
                        double sign = (textPos != null && textPos[0] < hs[0] - 0.0005) ? -1.0 : 1.0;
                        d.LeaderDistance = sign * 0.0045;
                    }
                } catch { }
            });
            LastCallout = d;
            return true;
        }
        public S.Dimension LastCallout;   // 刚建的孔标注（加公差、位置度框用）

        // ---- 公差（0.11.1，规矩见 DrawingTolerance）----
        // 孔标注加 H7，显示成「Ø6 H7(+0.012/0)」。和人图里的 H7 尺寸属性一模一样的设法（探针 7c）：
        // 标准配合 = true + 孔公差带 H7 + 公差符号 H7 + 显示类型 ClassPlusMinus，上下偏差天工自己按标准算。
        // 少了 PrimaryToleranceSymbol 图上只有 Ø6.6、偏差读不出（探针 7）。万一标准表算不出，就自己写 H7 的数值
        public bool Fit(S.Dimension d, double diameterMm){
            if (d == null) return false;
            try {
                d.UseStandardLimitAndFitTolerance = true;
                d.HoleClassString = DrawingTolerance.PinFit;
                d.PrimaryToleranceSymbol = DrawingTolerance.PinFit;
                d.DisplayType = S.DimDispTypeConstants.igDimDisplayTypeClassPlusMinus;
                string up = ""; try { up = d.PrimaryUpperTolerance ?? ""; } catch { }
                if (up.Length == 0) {
                    d.UseStandardLimitAndFitTolerance = false;
                    d.PrimaryUpperTolerance = "+" + DrawingTolerance.H7Upper(diameterMm).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
                    d.PrimaryLowerTolerance = "0";
                }
                try { d.TerminatorPosition = true; } catch { }
                RecordCalloutShelf(d);
                return true;
            } catch (Exception e) { Notes.Add("Ø" + diameterMm + " 的 H7 没设上：" + e.Message); return false; }
        }

        // 位置度框：不带引线，紧贴在孔标注文字正下方居中（人图就这么放，普查 76% 的 H7 孔都有）。
        // 实测（DrawApiProbe2）：AddRadialDiameter 的 KP[3] 是文字中心、KP[4] 是横线最外端，以前拿 KP[4] 对齐会把框甩到文字最右端撞轴测图。
        public bool Position(S.Dimension d){
            if (d == null) return false;
            try {
                int kc = d.KeyPointCount;
                double cx, cy;
                if (kc >= 5) {
                    double k3x, k3y, k4x, k4y, kz; F.KeyPointType kt; int hh;
                    d.GetKeyPoint(3, out k3x, out k3y, out kz, out kt, out hh);
                    d.GetKeyPoint(4, out k4x, out k4y, out kz, out kt, out hh);
                    cx = k3x; cy = k4y - 0.0036;
                } else {
                    double kx, ky, kz; F.KeyPointType kt; int hh; d.GetKeyPoint(kc - 1, out kx, out ky, out kz, out kt, out hh);
                    double a, b, c, e; d.Range(out a, out b, out c, out e);
                    cx = (a + c) / 2; cy = ky - 0.0040;
                }
                const double fw = 0.014, fh = 0.0045;
                var f = ((S.FeatureControlFrames)sheet.FeatureControlFrames).Add(cx, cy, 0);
                EnsureStyle(f, StyleName);
                f.PrimaryFrame = DrawingTolerance.PositionFrame;
                Created++;
                var fb = new[] { cx - fw / 2, cy - fh / 2, cx + fw / 2, cy + fh / 2 };
                if (Space != null) Space.Add(fb[0] - 0.001, fb[1] - 0.001, fb[2] + 0.001, fb[3] + 0.001);
                calloutBoxes.Add(fb);
                Occupy(f);
                return true;
            } catch (Exception ex) { Failed++; Notes.Add("位置度框没加上：" + ex.Message); return false; }
        }

        // 粗糙度：挂在视图里那条面的线上（at = 视图坐标），写 Ra 值（天工把最小值也跟着设成一样，和人图读回的一致，探针 7）
        public bool Roughness(D.DrawingView v, DwgEdge face, double[] at, string ra){
            try {
                var p = ToSheet(v, at[0], at[1]);
                var s = ((S.SurfaceFinishSymbols)sheet.SurfaceFinishSymbols).AddByTerminator(Ref(v, face.Tag), p[0], p[1], 0, false);
                EnsureStyle(s, StyleName);
                try { s.SurfaceFinishSymbol = S.DimSurfTextureSymTypeConstants.igDimSurfaceFinishMachined; } catch { }
                s.RoughnessValue = ra;
                Created++; Occupy(s);
                return true;
            } catch (Exception ex) { Failed++; Notes.Add("粗糙度 Ra" + ra + " 没挂上：" + ex.Message); return false; }
        }

        public bool Radius(D.DrawingView v, RadiusGroup g, double[] textPos){
            try {
                var d = dims.AddRadius(Ref(v, g.Leader.Tag));
                EnsureStyle(d, StyleName);
                Created++;
                try { if (g.Count > 1) d.PrefixString = DrawingPlan.RadiusPrefix(g.Count); } catch { }
                PlaceAndGuardCallout(d, textPos, () => EnsureInwardRadiusTerminator(d));
                return true;
            } catch (Exception e) { Failed++; Notes.Add("圆角 R" + g.RadiusMm + " 标不上：" + e.Message); return false; }
        }

        // 半径标注确保箭头落在弧外侧朝圆心指（朝里指），而不是缩在圆角内部从内朝外指
        static void EnsureInwardRadiusTerminator(S.Dimension d){
            if (d == null) return;
            try {
                if (d.KeyPointCount < 3) return;
                double x0, y0, x1, y1, x2, y2, z; F.KeyPointType kt; int h;
                d.GetKeyPoint(0, out x0, out y0, out z, out kt, out h);
                d.GetKeyPoint(1, out x1, out y1, out z, out kt, out h);
                d.GetKeyPoint(2, out x2, out y2, out z, out kt, out h);
                if (DrawingStyleRules.ShouldFlipRadiusTerminator(x0, y0, x1, y1, x2, y2)) {
                    d.TerminatorPosition = !d.TerminatorPosition;
                }
            } catch (Exception e) { Log.Write("DrawingRadiusTerminator", e); }
        }

        // 引线式尺寸的文字挪到 textPos（图纸坐标）：直径/半径固定改 KP[4]（防 6 关键点延长弧误改 KP[5]），倒角改 KP[2]
        static void MoveText(S.Dimension d, double[] textPos){
            if (textPos == null) return;
            try { int k = DrawingStyleRules.CalloutTextKeyPointIndex(d.KeyPointCount); if (k >= 0) d.SetKeyPoint(k, textPos[0], textPos[1], 0); } catch (Exception e) { Log.Write("DrawingMoveText", e); }
        }

        // 放置引线标注、校准端符朝向，并按天工实际渲染出的文字架范围复核是否与已放文字重叠；
        // 若实测字体比预估宽/高导致撞到前一条文字，沿单调方向按重叠框并集外推避让并校验图纸/视图边界，
        // 杜绝双框夹缝间往复振荡或把文字推入标题栏 / 视图本体。
        void PlaceAndGuardCallout(S.Dimension d, double[] textPos, Action fixTerminator){
            MoveText(d, textPos);
            if (fixTerminator != null) fixTerminator();
            if (textPos != null && calloutBoxes.Count > 0) {
                var origPos = new[] { textPos[0], textPos[1] };
                var tb0 = ReadCalloutShelfBox(d);
                double[] hit0 = tb0 == null ? null : FindOverlap(tb0, calloutBoxes);
                if (tb0 != null && hit0 != null) {
                    double sd0 = hit0[1] - tb0[3] - 0.0018;
                    double su0 = hit0[3] - tb0[1] + 0.0018;
                    int firstDir = Math.Abs(sd0) <= Math.Abs(su0) ? -1 : 1;
                    double[] bestPos = origPos;
                    int bestOverlaps = CountOverlaps(tb0, calloutBoxes);
                    bool solved = false;
                    foreach (int dir in new[] { firstDir, -firstDir }) {
                        if (dir != firstDir) {
                            MoveText(d, origPos);
                            if (fixTerminator != null) fixTerminator();
                        }
                        var curPos = new[] { origPos[0], origPos[1] };
                        for (int attempt = 0; attempt < 6; attempt++) {
                            var tb = ReadCalloutShelfBox(d);
                            if (tb == null) break;
                            double minY, maxY;
                            int ov = OverlapYUnion(tb, calloutBoxes, out minY, out maxY);
                            if (ov == 0) { bestPos = curPos; bestOverlaps = 0; solved = true; break; }
                            double dy = dir < 0 ? (minY - tb[3] - 0.0018) : (maxY - tb[1] + 0.0018);
                            if (Math.Abs(dy) < 1e-6) break;
                            var pred = new[] { tb[0], tb[1] + dy, tb[2], tb[3] + dy };
                            if (Space != null && !Space.InsideAndClearOfViews(pred)) break;
                            var nextPos = new[] { curPos[0], curPos[1] + dy };
                            MoveText(d, nextPos);
                            if (fixTerminator != null) fixTerminator();
                            var nextTb = ReadCalloutShelfBox(d);
                            if (nextTb == null) break;
                            if (Space != null && !Space.InsideAndClearOfViews(nextTb)) break;
                            curPos = nextPos;
                            int nextOv = CountOverlaps(nextTb, calloutBoxes);
                            if (nextOv < bestOverlaps) { bestOverlaps = nextOv; bestPos = curPos; }
                            if (nextOv == 0) { solved = true; break; }
                        }
                        if (solved) break;
                    }
                    if (!solved) {
                        MoveText(d, bestPos);
                        if (fixTerminator != null) fixTerminator();
                    }
                }
            }
            RecordCalloutShelf(d);
        }

        static int CountOverlaps(double[] r, List<double[]> boxes){
            int n = 0;
            foreach (var u in boxes) {
                if (r[0] < u[2] && u[0] < r[2] && r[1] < u[3] && u[1] < r[3]) n++;
            }
            return n;
        }

        static int OverlapYUnion(double[] r, List<double[]> boxes, out double minY, out double maxY){
            minY = double.MaxValue; maxY = double.MinValue;
            int n = 0;
            foreach (var u in boxes) {
                if (r[0] < u[2] && u[0] < r[2] && r[1] < u[3] && u[1] < r[3]) {
                    if (u[1] < minY) minY = u[1];
                    if (u[3] > maxY) maxY = u[3];
                    n++;
                }
            }
            return n;
        }

        void RecordCalloutShelf(S.Dimension d){
            var tb = ReadCalloutShelfBox(d);
            if (tb != null) {
                calloutBoxes.Add(tb);
                if (Space != null) Space.Add(tb[0] - 0.0008, tb[1] - 0.001, tb[2] + 0.0008, tb[3] + 0.001);
            } else {
                Occupy(d);
            }
        }

        // 读取引线标注的真实文字架矩形（不含斜引线本身，避免向下斜引线导致 Range.y1 虚高产生假碰撞或挡死整片空白）：
        //   · AddRadialDiameter / AddRadius (kps=5 或 6)：KP[3]=文字中心，KP[4]=横线外端；
        //   · AddChamfer Perpendicular (kps=3)：KP[1]=横线外端，KP[2]=文字中心。
        static double[] ReadCalloutShelfBox(S.Dimension d){
            if (d == null) return null;
            try {
                int kc = d.KeyPointCount;
                double cx, cy, ex, ey, z; F.KeyPointType kt; int h;
                if (kc >= 5) {
                    d.GetKeyPoint(3, out cx, out cy, out z, out kt, out h);
                    d.GetKeyPoint(4, out ex, out ey, out z, out kt, out h);
                } else if (kc == 3) {
                    d.GetKeyPoint(2, out cx, out cy, out z, out kt, out h);
                    d.GetKeyPoint(1, out ex, out ey, out z, out kt, out h);
                } else {
                    double a, b, c, e; d.Range(out a, out b, out c, out e);
                    return new[] { a, b, c, e };
                }
                var tb = DrawingStyleRules.CalloutShelfBox(cx, cy, ex, ey);
                if (tb[2] <= tb[0] + 0.002 || tb[3] <= tb[1] + 0.002) {
                    double a, b, c, e; d.Range(out a, out b, out c, out e);
                    return new[] { a, b, c, e };
                }
                return tb;
            } catch { return null; }
        }

        static double[] FindOverlap(double[] r, List<double[]> boxes){
            foreach (var u in boxes) {
                if (r[0] < u[2] && u[0] < r[2] && r[1] < u[3] && u[1] < r[3]) return u;
            }
            return null;
        }

        // 倒角标注：采用公司人图一致的 igDimChamferModePerpendicular（垂直于倒角斜边的单箭头引出「4-C4 / C2」），
        // 彻底替代此前 AlongAxis 生成的窄双箭头挤块与 90° 竖排歪字；失败时平滑回退到带引线文字。
        public bool Chamfer(D.DrawingView v, RadiusGroup g, IList<DwgEdge> allEdges, double[] textPos){
            var l = g.Leader;
            double mx = (l.X1 + l.X2) / 2, my = (l.Y1 + l.Y2) / 2;
            var adj = DrawingStyleRules.ChamferReferenceEdge(l, allEdges);
            if (adj != null && adj.Tag != null && l.Tag != null) {
                S.Dimension d = null;
                try {
                    var p1 = ToSheet(v, mx, my);
                    var p2 = ToSheet(v, (adj.X1 + adj.X2) / 2, (adj.Y1 + adj.Y2) / 2);
                    // 实测（2026-11-21 / 2026-11-23）：第 1 参数是正交参考边，第 2 参数是倒角斜边；
                    // Perpendicular 模式与公司 100% 人图一致，输出干净的 45° 单箭头「4-C4 / C2」。
                    d = dims.AddChamfer(
                        Ref(v, adj.Tag), p2[0], p2[1], 0, false,
                        Ref(v, l.Tag), p1[0], p1[1], 0, false,
                        S.DimChamferModeConstants.igDimChamferModePerpendicular);
                    EnsureStyle(d, StyleName);
                    if (Math.Abs(d.Value * 1000.0 - g.RadiusMm) <= 0.08) {
                        if (g.Count > 1) try { d.PrefixString = g.Count + "-"; } catch { }
                        PlaceAndGuardCallout(d, textPos, () => { try { d.TerminatorPosition = true; } catch { } });
                        Created++;
                        return true;
                    }
                    try { d.Delete(); } catch { }
                } catch (Exception ex) {
                    if (d != null) try { d.Delete(); } catch { }
                    Log.Write("DrawingAddChamfer", ex);
                }
            }
            return Note(v, l, mx, my, DrawingPlan.ChamferText(g.Count, g.RadiusMm), textPos) != null;
        }

        // 引线文字（倒角回退、刻字）：箭头挂在图元上 (px,py 视图坐标)，文字放在 textPos（图纸坐标）。
        // 引线的折点用 AddVertex 加（文字跟着最后一个点走）—— 出图训练数据那套夹具里基准 / 形位公差 / 粗糙度都是这么挂的，真机验证过
        public S.Balloon Note(D.DrawingView v, DwgEdge e, double px, double py, string text, double[] textPos){
            try {
                var p = ToSheet(v, px, py);
                var b = ((S.Balloons)sheet.Balloons).AddByTerminator(Ref(v, e.Tag), p[0], p[1], 0, false);
                EnsureStyle(b, StyleName);
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
                EnsureStyle(d, StyleName);
                d.SetOrientation(horizontal ? S.DimensionOrientationConstants.igOrientationHorizontal : S.DimensionOrientationConstants.igOrientationVertical);
                double want = horizontal ? box[3] + gap - Math.Max(pa[1], pb[1]) : Math.Min(pa[0], pb[0]) - (box[0] - gap);
                d.TrackDistance = want;
                double x0, y0, x1, y1; d.Range(out x0, out y0, out x1, out y1);
                bool inside = horizontal ? y0 < box[3] - 1e-4 : x1 > box[0] + 1e-4;
                if (inside) { d.TrackDistance = -want; d.Range(out x0, out y0, out x1, out y1); }
                double sc = 1.0; try { sc = v.ScaleFactor; } catch { }
                ApplyLinearTerminator(d, sc);
                Created++; Occupy(d); return true;
            } catch (Exception e) { Failed++; Log.Write("DrawingDistance", e); return false; }
        }
    }
}
