using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace TianGongCadSuite {
    // 纵向堆叠容器：严格按"添加顺序 = 从上到下"摆放。
    // 不用 Dock —— WinForms 的 Dock 是按 z-order 逆序填充的，多个 Dock=Top 控件会整体颠倒，
    // 第一版窗口就是这么翻车的（预览跑到最上面、"开始打孔"被挤没了）。
    // 高度为 0 或 Tag="fill" 的控件分配剩余高度；窗口不够高时自动出现滚动条。
    public sealed class VerticalStack : Panel {
        public int Gap = Ui.S(10);
        int layoutDepth;
        public VerticalStack(){
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            BackColor = Ui.Bg;
            AutoScroll = true;
            Padding = Ui.SPad(12, 10, 12, 10);
        }
        static int MeasureLabelHeight(Label l, int w){
            string text = l.Text ?? "";
            int minH = l.MinimumSize.Height > 0 ? l.MinimumSize.Height : Ui.S(20);
            if (text.Length == 0) return minH;
            int availW = Math.Max(Ui.S(40), w - l.Padding.Horizontal);
            var sz = TextRenderer.MeasureText(text, l.Font ?? Ui.F9, new Size(availW, int.MaxValue),
                TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix);
            int wantH = Math.Max(minH, sz.Height + l.Padding.Vertical + Ui.S(4));
            if (l.MaximumSize.Height > 0 && wantH > l.MaximumSize.Height) wantH = l.MaximumSize.Height;
            return wantH;
        }
        static int MeasureChildHeight(Control c, int w){
            if ((c.Tag as string) != "autoheight") return c.Height;
            if (w <= 0) return c.Height;
            var lbl = c as Label;
            if (lbl != null) return MeasureLabelHeight(lbl, w);
            int pref = c.GetPreferredSize(new Size(w, 0)).Height;
            int minH = c.MinimumSize.Height > 0 ? c.MinimumSize.Height : 0;
            int wantH = Math.Max(minH, pref);
            if (c.MaximumSize.Height > 0 && wantH > c.MaximumSize.Height) wantH = c.MaximumSize.Height;
            return wantH;
        }
        public override Size GetPreferredSize(Size proposedSize){
            int pw = proposedSize.Width > 0 ? proposedSize.Width : ClientSize.Width;
            int w = Math.Max(0, pw - Padding.Horizontal);
            int total = Padding.Vertical, visible = 0;
            foreach (Control c in Controls) {
                if (!c.Visible) continue;
                visible++;
                if ((c.Tag as string) == "fill") {
                    int h = Math.Max(Ui.S(70), c.MinimumSize.Height);
                    var sc = c as SplitColumns;
                    if (sc != null && sc.IsStacked(w)) h = Math.Max(h, sc.GetPreferredSize(new Size(w, 0)).Height);
                    total += h;
                } else {
                    total += MeasureChildHeight(c, w);
                }
            }
            total += Gap * Math.Max(0, visible - 1);
            return new Size(pw, total);
        }
        protected override void OnLayout(LayoutEventArgs e){
            if (layoutDepth >= 2) return;
            layoutDepth++;
            Size wantMin = Size.Empty;
            bool needVScroll = false;
            int contentH = 0;
            // ResumeLayout(false)：严禁在 OnLayout 结尾传 true，否则每次缩放/切页都会同步再触发一遍整棵子树的级联二次布局！
            SuspendLayout();
            try {
                // 预判是否需要垂直滚动条：若需要，预先扣除滚动条宽度排版，防止先按全宽摆完再出垂直滚动条时顺带触发多余的水平滚动条
                int fullW = Math.Max(0, Width - Padding.Horizontal);
                int w = Math.Max(0, ClientSize.Width - Padding.Horizontal);
                if (AutoScroll && Width > 0 && ClientSize.Width == Width) {
                    int estH = EstimateContentHeight(fullW);
                    if (estH > Height) {
                        needVScroll = true;
                        w = Math.Max(0, fullW - SystemInformation.VerticalScrollBarWidth - 1);
                    }
                }
                contentH = LayoutChildren(w);
                if (AutoScroll) {
                    needVScroll = contentH > Height;
                    wantMin = needVScroll ? new Size(0, contentH) : Size.Empty;
                }
            } finally { ResumeLayout(false); }
            try {
                if (AutoScroll) {
                    if (HorizontalScroll.Visible) HorizontalScroll.Visible = false;
                    if (AutoScrollMinSize != wantMin) AutoScrollMinSize = wantMin;
                    // ScrollableControl 必须在布局后调用 AdjustFormScrollbars 才会真正创建/更新原生垂直滚动条
                    AdjustFormScrollbars(true);
                    int actualW = Math.Max(0, ClientSize.Width - Padding.Horizontal);
                    // 若滚动条显隐后实际可用宽度与刚才排版宽度不一致，按实际宽度精确对齐一次
                    if (actualW > 0 && Controls.Count > 0 && NeedsWidthSync(actualW)) {
                        SuspendLayout();
                        try { contentH = LayoutChildren(actualW); }
                        finally { ResumeLayout(false); }
                        var finalMin = contentH > Height ? new Size(0, contentH) : Size.Empty;
                        if (AutoScrollMinSize != finalMin) AutoScrollMinSize = finalMin;
                        AdjustFormScrollbars(true);
                    }
                }
            } finally { layoutDepth--; }
        }
        int EstimateContentHeight(int w){
            int total = Padding.Vertical, visible = 0;
            foreach (Control c in Controls) {
                if (!c.Visible) continue;
                visible++;
                if ((c.Tag as string) == "fill") {
                    int h = Math.Max(Ui.S(70), c.MinimumSize.Height);
                    var sc = c as SplitColumns;
                    if (sc != null && sc.IsStacked(w)) h = Math.Max(h, sc.GetPreferredSize(new Size(w, 0)).Height);
                    total += h;
                } else {
                    total += MeasureChildHeight(c, w);
                }
            }
            return total + Gap * Math.Max(0, visible - 1);
        }
        bool NeedsWidthSync(int actualW){
            foreach (Control c in Controls) {
                if (!c.Visible) continue;
                if (Math.Abs(c.Width - actualW) > 1) return true;
            }
            return false;
        }
        int LayoutChildren(int w){
            var visibleControls = new List<Control>(Controls.Count);
            var measuredHeights = new List<int>(Controls.Count);
            int fillCount = 0, used = 0;
            foreach (Control c in Controls) {
                if (!c.Visible) continue;
                visibleControls.Add(c);
                if ((c.Tag as string) == "fill") {
                    fillCount++;
                    measuredHeights.Add(-1);
                } else {
                    int wantH = MeasureChildHeight(c, w);
                    measuredHeights.Add(wantH);
                    used += wantH;
                }
            }
            int visible = visibleControls.Count;
            used += Gap * Math.Max(0, visible - 1);
            int avail = ClientSize.Height - Padding.Vertical;
            int each = fillCount > 0 ? Math.Max(Ui.S(70), (avail - used) / fillCount) : 0;
            // WinForms ScrollableControl 的子控件坐标是相对于可视视口的，重排时必须叠加 AutoScrollPosition，
            // 否则滚动后再缩放或展开内容会让所有子控件跳回 0 坐标并与滚动范围错位遮挡。
            int scrollX = AutoScroll ? AutoScrollPosition.X : 0;
            int scrollY = AutoScroll ? AutoScrollPosition.Y : 0;
            int y = Padding.Top;
            for (int i = 0; i < visible; i++) {
                var c = visibleControls[i];
                int h;
                if (measuredHeights[i] < 0) {
                    h = Math.Max(each, c.MinimumSize.Height);
                    var sc = c as SplitColumns;
                    if (sc != null && sc.IsStacked(w)) h = Math.Max(h, sc.GetPreferredSize(new Size(w, 0)).Height);
                } else {
                    h = measuredHeights[i];
                }
                // 原子同时设置新宽度与新高度，避免先改高度时子容器（如 FieldGrid）仍按旧宽度重算并改回旧高度
                var want = new Rectangle(Padding.Left + scrollX, y + scrollY, w, h);
                if (c.Bounds != want) c.Bounds = want;
                y += c.Height + Gap;
            }
            return visible > 0 ? Math.Max(0, y - Gap + Padding.Bottom) : 0;
        }
        // 加控件：一律清掉 Dock —— 本容器手动排版，Dock 会跟它打架。
        public void Add(Control c, bool fill = false){
            c.Dock = DockStyle.None;
            if (fill) c.Tag = "fill";
            Controls.Add(c);
        }
        // 子控件显示/隐藏后要重新排：不再 Invalidate(true) 暴力重绘所有子控件（包括整张 DataGridView）
        public void Relayout(){ PerformLayout(); Invalidate(); }
    }

    // 两列容器：左列固定宽（或按比例），右列吃掉剩下的宽度。
    // 当窗口过窄导致两列最小宽度之和放不下且开启 StackWhenNarrow 时，自动切换为上下堆叠，彻底避免互相挤压遮挡。
    // 和 VerticalStack 一样手动排版 —— Dock 的填充顺序按 z-order 逆序，
    // 两个 Dock 混着用很容易变成"左列被右列挤没"。
    public sealed class SplitColumns : Panel {
        public int Gap = Ui.S(20);
        public int LeftWidth;             // >0：左列固定宽度
        public float LeftFraction;        // >0：左列占可用宽度的比例
        public int MinLeftWidth;          // >0：左列最小宽度
        public int MinRightWidth;         // >0：右列最小宽度
        public bool StackWhenNarrow;      // 宽度放不下两列最小宽度之和时自动改为上下堆叠
        public int MinStackedRightHeight; // 上下堆叠时第二项（下方）的最小高度
        public bool Divider;              // 中间画分隔线（D 里一般不画，靠留白分开）
        public SplitColumns(int leftWidth){
            LeftWidth = Ui.S(leftWidth);
            BackColor = Ui.Bg;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        }
        public SplitColumns(float leftFraction){
            LeftFraction = leftFraction;
            BackColor = Ui.Bg;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        }
        public bool IsStacked(int width){
            if (!StackWhenNarrow || Controls.Count < 2 || width <= 0) return false;
            int minL = MinLeftWidth > 0 ? MinLeftWidth : (LeftWidth > 0 ? LeftWidth : Ui.S(240));
            int minR = MinRightWidth > 0 ? MinRightWidth : Ui.S(200);
            return width < minL + minR + Gap;
        }
        int MeasureStackedTop(Control a, int w){
            int pref = a.GetPreferredSize(new Size(w, 0)).Height;
            if (pref <= 0) pref = a.Height;
            return Math.Max(Ui.S(80), Math.Max(a.MinimumSize.Height, pref));
        }
        public override Size GetPreferredSize(Size proposedSize){
            int w = proposedSize.Width > 0 ? proposedSize.Width : ClientSize.Width;
            if (Controls.Count < 2) return base.GetPreferredSize(proposedSize);
            if (IsStacked(w)) {
                int topH = MeasureStackedTop(Controls[0], w);
                int minBot = MinStackedRightHeight > 0 ? MinStackedRightHeight : Math.Max(Ui.S(240), Controls[1].MinimumSize.Height);
                return new Size(w, topH + Gap + minBot);
            }
            return new Size(w, Math.Max(MinimumSize.Height, Height));
        }
        protected override void OnLayout(LayoutEventArgs e){
            SuspendLayout();
            try {
                if (Controls.Count < 2) return;
                var a = Controls[0]; var b = Controls[1];
                int cw = ClientSize.Width, ch = ClientSize.Height;
                if (IsStacked(cw)) {
                    int topH = MeasureStackedTop(a, cw);
                    int minBot = MinStackedRightHeight > 0 ? MinStackedRightHeight : Math.Max(Ui.S(240), b.MinimumSize.Height);
                    int botH = Math.Max(minBot, ch - topH - Gap);
                    var wa = new Rectangle(0, 0, cw, topH);
                    var wb = new Rectangle(0, topH + Gap, cw, botH);
                    if (a.Bounds != wa) a.Bounds = wa;
                    if (b.Bounds != wb) b.Bounds = wb;
                    return;
                }
                int avail = Math.Max(0, cw - Gap);
                int minL = MinLeftWidth > 0 ? MinLeftWidth : Ui.S(60);
                int minR = MinRightWidth > 0 ? MinRightWidth : Ui.S(60);
                int left = LeftWidth > 0 ? LeftWidth : (LeftFraction > 0 ? (int)(avail * LeftFraction) : avail / 2);
                if (left < minL) left = minL;
                if (left > avail - minR) left = Math.Max(Ui.S(60), avail - minR);
                var sa = new Rectangle(0, 0, left, ch);
                var sb = new Rectangle(left + Gap, 0, Math.Max(0, cw - left - Gap), ch);
                if (a.Bounds != sa) a.Bounds = sa;
                if (b.Bounds != sb) b.Bounds = sb;
            } finally { ResumeLayout(false); if (Divider) Invalidate(); }
        }
        public void Add(Control c){
            c.Dock = DockStyle.None;
            Controls.Add(c);
        }
        protected override void OnPaint(PaintEventArgs e){
            base.OnPaint(e);
            if (!Divider || Controls.Count < 2 || IsStacked(ClientSize.Width)) return;
            int x = Controls[0].Right + Gap / 2;
            e.Graphics.DrawLine(Ui.CachedPen(Ui.Line, 1f), x, 4, x, ClientSize.Height - 4);
        }
    }

    // 白底卡片：10px 圆角、1px 细边，标题行在上。Fill=true 时整块铺浅灰（侧边信息块、孔形状这类）。
    public sealed class Card : Panel {
        public string Title = "";
        public string Hint = "";
        public bool Fill;
        readonly List<Control> header = new List<Control>();
        public Card(){
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Ui.Bg;
            Padding = Ui.SPad(14, 38, 14, 12);
            Dock = DockStyle.None;
        }
        // 标题行右侧的小按钮（"移除""清空"这类），从右往左排
        public void AddHeader(Control c){
            header.Add(c);
            Controls.Add(c);
            PerformLayout();
        }
        int HeaderLeft {
            get {
                int x = Width - Ui.S(10);
                foreach (var c in header) x -= c.Width + Ui.S(4);
                return x;
            }
        }
        protected override void OnLayout(LayoutEventArgs e){
            base.OnLayout(e);
            int x = Width - Ui.S(8), topY = Ui.S(7), gap = Ui.S(4);
            for (int i = header.Count - 1; i >= 0; i--) {
                var c = header[i];
                x -= c.Width;
                c.Location = new Point(x, topY);
                x -= gap;
            }
        }
        protected override void OnPaint(PaintEventArgs e){
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Ui.Bg);
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            int rad = Ui.S(10);
            Ui.FillRound(g, r, rad, Fill ? Ui.Rail : Ui.CardBg);
            if (!Fill) Ui.DrawRound(g, r, rad, Ui.Line, 1);
            if (Title.Length > 0) {
                int tx = Ui.S(14), ty = Ui.S(10);
                TextRenderer.DrawText(g, Title, Ui.F9B, new Point(tx, ty), Ui.Text);
                if (Hint.Length > 0) {
                    // 说明文字只能画到标题行按钮的左边，放不下就省略号 —— 不能糊到按钮底下
                    var sz = TextRenderer.MeasureText(g, Title, Ui.F9B);
                    int hx = tx + sz.Width + Ui.S(6);
                    var hr = new Rectangle(hx, Ui.S(9), Math.Max(0, HeaderLeft - hx - Ui.S(4)), Ui.S(24));
                    TextRenderer.DrawText(g, Hint, Ui.F8, hr, Ui.Muted,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                }
            }
        }
        // 卡片里子控件的底色跟卡片走（Fill 的卡片是浅灰）
        public Color Surface { get { return Fill ? Ui.Rail : Ui.CardBg; } }
    }

    // 编号 + 标题 + 说明 的步骤条。
    public sealed class StepHeader : Control {
        public int Number = 1;
        public string Title = "";
        public string Hint = "";
        public StepHeader(){
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Ui.Bg;
            Padding = new Padding(0);
        }
        protected override void OnPaint(PaintEventArgs e){
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int d = Ui.S(22), cy = Height / 2;
            var circle = new Rectangle(1, cy - d / 2, d, d);
            using (var b = new SolidBrush(Ui.Accent)) g.FillEllipse(b, circle);
            TextRenderer.DrawText(g, Number.ToString(), Ui.F8, circle, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            g.SmoothingMode = SmoothingMode.Default;
            var sz = TextRenderer.MeasureText(g, Title, Ui.F10B);
            TextRenderer.DrawText(g, Title, Ui.F10B, new Point(1 + d + Ui.S(10), cy - sz.Height / 2), Ui.Text);
            // 说明文字用带省略号的矩形画：列宽不够时截断，而不是糊到右边那列上去
            if (Hint.Length > 0) {
                int hx = 1 + d + Ui.S(14) + sz.Width;
                var hr = new Rectangle(hx, 0, Math.Max(0, Width - hx - 2), Height);
                TextRenderer.DrawText(g, Hint, Ui.F8, hr, Ui.Muted,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
        }
    }

    // 表单网格：每行 = 左边一个标签 + 右边一串控件（从左往右排，垂直居中；宽度放不下时按语义组自动折行，绝不裁切遮挡）。
    // 行可以整行隐藏，下面的行自动顶上来 —— 不留空带、不用手算坐标。
    // 隐藏状态记在 Row.Shown 里而不是读 Control.Visible：窗口还没显示时 Visible 的 getter 一律是 false，
    // 拿它当排版依据会时灵时不灵（无窗口的单元测试下就翻车）。
    public sealed class FieldGrid : Panel {
        public int RowHeight = Ui.S(40), LabelWidth = Ui.S(56), Gap = Ui.S(8);
        public sealed class Row {
            public Label Caption;
            public Panel Host;
            public readonly List<Control> Items = new List<Control>();
            public bool Shown = true;
            public int Height;
        }
        public readonly List<Row> Rows = new List<Row>();
        readonly Dictionary<Control, int> naturalWidths = new Dictionary<Control, int>();
        bool syncingHeight;
        public FieldGrid(){
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            BackColor = Color.White;
        }

        // 空白占位（控件之间要多空一点时用；折行刚好落在这里时自动省略，不把空白带到下一行行首）
        public static Control Space(int width){ return new Panel { Width = Ui.S(width), Height = 1, BackColor = Color.Transparent, Tag = "space" }; }
        // 单位：紧跟在数字框后面，间距比一般控件小，折行时与前面的控件绑定不拆散
        public static Label Unit(string text){
            var l = new Label { Text = text, AutoSize = true, ForeColor = Ui.Muted, Font = Ui.F8, BackColor = Color.Transparent };
            l.Tag = "unit";
            return l;
        }

        public Row Add(string caption, params Control[] items){
            int needW = string.IsNullOrEmpty(caption) ? 0 : TextRenderer.MeasureText(caption, Ui.F9B).Width + Ui.S(14);
            bool widened = needW > LabelWidth;
            if (widened) LabelWidth = needW;
            var row = new Row { Height = RowHeight };
            row.Host = new Panel { BackColor = BackColor, Height = RowHeight };
            row.Caption = new Label { Text = caption, AutoSize = false, ForeColor = Ui.Text2, BackColor = Color.Transparent,
                                      TextAlign = ContentAlignment.MiddleLeft, Font = Ui.F9B };
            row.Host.Controls.Add(row.Caption);
            foreach (var c in items) {
                row.Items.Add(c);
                row.Host.Controls.Add(c);
                if (IsFlexible(c) && !naturalWidths.ContainsKey(c)) naturalWidths[c] = c.Width;
            }
            Rows.Add(row);
            Controls.Add(row.Host);
            if (widened) { foreach (var r in Rows) LayoutRow(r, ClientSize.Width); } else LayoutRow(row, ClientSize.Width);
            PerformLayout();
            return row;
        }

        public void Show(Row row, bool shown){
            if (row == null || row.Shown == shown) return;
            row.Shown = shown;
            row.Host.Visible = shown;
            PerformLayout();
            var vs = Parent as VerticalStack;
            if (vs != null) vs.PerformLayout();
        }

        public int PreferredHeight { get { return MeasureHeight(ClientSize.Width); } }
        public int MeasureHeight(int width){
            int total = 0;
            foreach (var r in Rows) {
                if (!r.Shown) continue;
                total += MeasureRowHeight(r, width);
            }
            return total;
        }
        public override Size GetPreferredSize(Size proposedSize){
            int w = proposedSize.Width > 0 ? proposedSize.Width : ClientSize.Width;
            return new Size(w, MeasureHeight(w));
        }

        // 行的纵向位置（只算显示的行，按实际折行后的行高累加），测试和外部排版用
        public int TopOf(Row row){
            int y = 0;
            int w = ClientSize.Width;
            foreach (var r in Rows) {
                if (r == row) return y;
                if (r.Shown) y += MeasureRowHeight(r, w);
            }
            return -1;
        }

        static bool IsSpacer(Control c){ return (c.Tag as string) == "space" || (c is Panel && c.Controls.Count == 0 && c.Height <= 2); }
        static bool IsUnit(Control c){ return (c.Tag as string) == "unit"; }
        static bool IsCaptionLabel(Control c){ return c is Label && !IsUnit(c); }
        static bool IsInput(Control c){ return c is NumericUpDown || c is ComboBox || c is TextBox || c is Segmented; }
        static bool IsFlexible(Control c){
            if (c is Segmented) return true;
            var tb = c as TextBox;
            return tb != null && !tb.Multiline && !tb.AutoSize;
        }

        int SubLineHeight { get { return Math.Max(Ui.S(32), RowHeight - Ui.S(4)); } }

        Size MeasureItemSize(Control c, int availRowW){
            Size sz = c.AutoSize ? c.PreferredSize : c.Size;
            if (IsFlexible(c)) {
                int nat;
                if (!naturalWidths.TryGetValue(c, out nat)) { nat = sz.Width; naturalWidths[c] = nat; }
                int targetW = availRowW > 0 ? Math.Min(nat, Math.Max(Ui.S(96), availRowW)) : nat;
                sz = new Size(targetW, sz.Height);
            }
            return sz;
        }

        // 按语义将同行控件切分为不可拆散的原子块（例如「孔底 + 平底 + 118 + °」「末端 + 10.0 + mm」「盲孔 + 10.00 + mm」），
        // Space 占位符单独作为软断行点：同行保留间距，折到下一行时自动吞掉不留行首缩进。
        sealed class Segment {
            public bool Spacer;
            public readonly List<Control> Controls = new List<Control>();
        }
        static List<Segment> GroupSegments(Row row){
            var segs = new List<Segment>();
            int n = row.Items.Count;
            int i = 0;
            while (i < n) {
                var c = row.Items[i];
                if (IsSpacer(c)) {
                    var sp = new Segment { Spacer = true };
                    sp.Controls.Add(c);
                    segs.Add(sp);
                    i++;
                    continue;
                }
                var seg = new Segment();
                seg.Controls.Add(c);
                i++;
                if (IsCaptionLabel(c)) {
                    // 短字段名（孔径 / 孔底 / 起端 / 末端 / 总长 / 槽宽 / Φ / 深 / 角度 / × / ～）绑定后面的输入框与单位
                    while (i < n && !IsSpacer(row.Items[i]) && (IsInput(row.Items[i]) || IsUnit(row.Items[i]))) {
                        seg.Controls.Add(row.Items[i]);
                        i++;
                    }
                } else if (c is RadioButton || c is CheckBox) {
                    // 单选/复选框后若紧跟输入框与单位（如「盲孔 [10.00] mm」「孔口倒角 [0.50] mm」），绑在一起折行更自然
                    if (i < n && !IsSpacer(row.Items[i]) && IsInput(row.Items[i])) {
                        while (i < n && !IsSpacer(row.Items[i]) && (IsInput(row.Items[i]) || IsUnit(row.Items[i]))) {
                            seg.Controls.Add(row.Items[i]);
                            i++;
                        }
                    }
                } else {
                    // 输入控件后紧跟的单位（mm / ° / 个）绝不能拆到下一行
                    while (i < n && IsUnit(row.Items[i])) {
                        seg.Controls.Add(row.Items[i]);
                        i++;
                    }
                }
                segs.Add(seg);
            }
            return segs;
        }

        int SegmentWidth(Segment seg, int availRowW){
            int w = 0;
            Control prev = null;
            foreach (var c in seg.Controls) {
                if (prev != null) w += IsUnit(c) ? Ui.S(3) : Gap;
                w += MeasureItemSize(c, availRowW).Width;
                prev = c;
            }
            return w;
        }

        int MeasureRowHeight(Row row, int totalWidth){
            if (totalWidth <= 0) return RowHeight;
            int availRowW = totalWidth - LabelWidth;
            if (availRowW <= Ui.S(60)) return RowHeight;
            var segs = GroupSegments(row);
            int lines = 1, lineX = 0;
            bool hasOnLine = false;
            int pendingSpacer = 0;
            for (int i = 0; i < segs.Count; i++) {
                var seg = segs[i];
                if (seg.Spacer) {
                    if (hasOnLine) pendingSpacer += Gap + MeasureItemSize(seg.Controls[0], availRowW).Width;
                    continue;
                }
                int sw = SegmentWidth(seg, availRowW);
                int lead = hasOnLine ? (pendingSpacer > 0 ? pendingSpacer + Gap : Gap) : 0;
                if (hasOnLine && lineX + lead + sw > availRowW) {
                    lines++;
                    lineX = sw;
                    hasOnLine = true;
                    pendingSpacer = 0;
                } else {
                    lineX += lead + sw;
                    hasOnLine = true;
                    pendingSpacer = 0;
                }
            }
            return lines <= 1 ? RowHeight : (RowHeight + (lines - 1) * SubLineHeight);
        }

        int LayoutRow(Row row, int totalWidth){
            var capRect = new Rectangle(0, 0, LabelWidth, RowHeight);
            if (row.Caption.Bounds != capRect) row.Caption.Bounds = capRect;
            int availRowW = totalWidth > 0 ? Math.Max(0, totalWidth - LabelWidth) : 0;
            bool canWrap = availRowW > Ui.S(60);
            var segs = GroupSegments(row);
            int line = 0, lineY = 0, lineH = RowHeight;
            int x = LabelWidth;
            bool hasOnLine = false;
            var pendingSpacers = new List<Control>();

            for (int i = 0; i < segs.Count; i++) {
                var seg = segs[i];
                if (seg.Spacer) {
                    pendingSpacers.Add(seg.Controls[0]);
                    continue;
                }
                int sw = SegmentWidth(seg, availRowW);
                int lead = 0;
                if (hasOnLine) {
                    if (pendingSpacers.Count > 0) {
                        foreach (var sp in pendingSpacers) lead += Gap + MeasureItemSize(sp, availRowW).Width;
                        lead += Gap;
                    } else {
                        lead = Gap;
                    }
                }
                if (canWrap && hasOnLine && (x - LabelWidth) + lead + sw > availRowW) {
                    // 折到下一行：隐藏夹在行尾的软占位符，保留其原始宽度以便窗口拉宽后恢复间距
                    foreach (var sp in pendingSpacers) {
                        if (sp.Visible) sp.Visible = false;
                    }
                    pendingSpacers.Clear();
                    line++;
                    lineY = RowHeight + (line - 1) * SubLineHeight;
                    lineH = SubLineHeight;
                    x = LabelWidth;
                    hasOnLine = false;
                } else if (hasOnLine) {
                    foreach (var sp in pendingSpacers) {
                        x += Gap;
                        var spSz = MeasureItemSize(sp, availRowW);
                        var spRect = new Rectangle(x, lineY + Math.Max(0, (lineH - spSz.Height) / 2), spSz.Width, spSz.Height);
                        if (!sp.Visible) sp.Visible = true;
                        if (sp.Bounds != spRect) sp.Bounds = spRect;
                        x += spSz.Width;
                    }
                    pendingSpacers.Clear();
                    x += Gap;
                } else {
                    foreach (var sp in pendingSpacers) {
                        if (sp.Visible) sp.Visible = false;
                    }
                    pendingSpacers.Clear();
                }

                Control prev = null;
                foreach (var c in seg.Controls) {
                    if (prev != null) x += IsUnit(c) ? Ui.S(3) : Gap;
                    var size = MeasureItemSize(c, availRowW);
                    var want = new Rectangle(x, lineY + Math.Max(0, (lineH - size.Height) / 2), size.Width, size.Height);
                    if (c.Bounds != want) c.Bounds = want;
                    x += size.Width;
                    prev = c;
                }
                hasOnLine = true;
            }
            foreach (var sp in pendingSpacers) {
                if (sp.Visible) sp.Visible = false;
            }
            int rh = line == 0 ? RowHeight : (RowHeight + line * SubLineHeight);
            row.Height = rh;
            return rh;
        }

        protected override void OnLayout(LayoutEventArgs e){
            int totalH = 0;
            SuspendLayout();
            try {
                int y = 0, w = ClientSize.Width;
                foreach (var r in Rows) {
                    if (!r.Shown) continue;
                    int rh = LayoutRow(r, w);
                    var want = new Rectangle(0, y, w, rh);
                    if (r.Host.Bounds != want) r.Host.Bounds = want;
                    y += rh;
                }
                totalH = y;
            } finally { ResumeLayout(false); }
            if (!syncingHeight && Dock != DockStyle.Fill && Height != totalH) {
                syncingHeight = true;
                try { Height = totalH; }
                finally { syncingHeight = false; }
            }
        }
        protected override void OnBackColorChanged(EventArgs e){
            base.OnBackColorChanged(e);
            foreach (var r in Rows) r.Host.BackColor = BackColor;
        }
    }
}
