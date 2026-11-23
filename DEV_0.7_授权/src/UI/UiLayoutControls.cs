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
        public VerticalStack(){
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            BackColor = Ui.Bg;
            AutoScroll = true;
            Padding = Ui.SPad(12, 10, 12, 10);
        }
        protected override void OnLayout(LayoutEventArgs e){
            // ResumeLayout(false)：严禁在 OnLayout 结尾传 true，否则每次缩放/切页都会同步再触发一遍整棵子树的级联二次布局！
            SuspendLayout();
            try {
                int w = ClientSize.Width - Padding.Horizontal;
                if (w < 0) w = 0;
                var fills = new List<Control>();
                int used = 0, visible = 0;
                foreach (Control c in Controls) {
                    if (!c.Visible) continue;
                    visible++;
                    if ((c.Tag as string) == "fill") {
                        fills.Add(c);
                    } else {
                        if ((c.Tag as string) == "autoheight" && w >= Ui.S(200)) {
                            int pref = c.GetPreferredSize(new Size(w, 0)).Height;
                            int minH = c.MinimumSize.Height > 0 ? c.MinimumSize.Height : Ui.S(34);
                            int wantH = Math.Max(minH, pref + Ui.S(4));
                            if (c.Height != wantH) c.Height = wantH;
                        }
                        used += c.Height;
                    }
                }
                used += Gap * Math.Max(0, visible - 1);
                int avail = ClientSize.Height - Padding.Vertical;
                int each = fills.Count > 0 ? Math.Max(Ui.S(70), (avail - used) / fills.Count) : 0;
                int y = Padding.Top;
                foreach (Control c in Controls) {
                    if (!c.Visible) continue;
                    int h = (c.Tag as string) == "fill" ? Math.Max(each, c.MinimumSize.Height) : c.Height;
                    var want = new Rectangle(Padding.Left, y, w, h);
                    if (c.Bounds != want) c.Bounds = want;
                    y += c.Height + Gap;
                }
                if (AutoScroll) {
                    var wantMin = new Size(0, Math.Max(0, y - Gap + Padding.Bottom));
                    if (AutoScrollMinSize != wantMin) AutoScrollMinSize = wantMin;
                }
            } finally { ResumeLayout(false); }
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
    // 和 VerticalStack 一样手动排版 —— Dock 的填充顺序按 z-order 逆序，
    // 两个 Dock 混着用很容易变成"左列被右列挤没"。
    public sealed class SplitColumns : Panel {
        public int Gap = Ui.S(20);
        public int LeftWidth;        // >0：左列固定宽度
        public float LeftFraction;   // >0：左列占可用宽度的比例
        public bool Divider;         // 中间画分隔线（D 里一般不画，靠留白分开）
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
        protected override void OnLayout(LayoutEventArgs e){
            SuspendLayout();
            try {
                if (Controls.Count < 2) return;
                int avail = ClientSize.Width - Gap;
                int minCol = Ui.S(60);
                int left = LeftWidth > 0 ? LeftWidth : (LeftFraction > 0 ? (int)(avail * LeftFraction) : avail / 2);
                if (left < minCol) left = minCol;
                if (left > avail - minCol) left = Math.Max(minCol, avail - minCol);
                var a = Controls[0]; var b = Controls[1];
                var wa = new Rectangle(0, 0, left, ClientSize.Height);
                var wb = new Rectangle(left + Gap, 0, Math.Max(0, ClientSize.Width - left - Gap), ClientSize.Height);
                if (a.Bounds != wa) a.Bounds = wa;
                if (b.Bounds != wb) b.Bounds = wb;
            } finally { ResumeLayout(false); if (Divider) Invalidate(); }
        }
        public void Add(Control c){
            c.Dock = DockStyle.None;
            Controls.Add(c);
        }
        protected override void OnPaint(PaintEventArgs e){
            base.OnPaint(e);
            if (!Divider || Controls.Count < 2) return;
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

    // 表单网格：每行 = 左边一个标签 + 右边一串控件（从左往右排，垂直居中）。
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
        }
        public readonly List<Row> Rows = new List<Row>();
        public FieldGrid(){
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            BackColor = Color.White;
        }

        // 空白占位（控件之间要多空一点时用）
        public static Control Space(int width){ return new Panel { Width = Ui.S(width), Height = 1, BackColor = Color.Transparent }; }
        // 单位：紧跟在数字框后面，间距比一般控件小
        public static Label Unit(string text){
            var l = new Label { Text = text, AutoSize = true, ForeColor = Ui.Muted, Font = Ui.F8, BackColor = Color.Transparent };
            l.Tag = "unit";
            return l;
        }

        public Row Add(string caption, params Control[] items){
            int needW = string.IsNullOrEmpty(caption) ? 0 : TextRenderer.MeasureText(caption, Ui.F9B).Width + Ui.S(14);
            bool widened = needW > LabelWidth;
            if (widened) LabelWidth = needW;
            var row = new Row();
            row.Host = new Panel { BackColor = BackColor, Height = RowHeight };
            row.Caption = new Label { Text = caption, AutoSize = false, ForeColor = Ui.Text2, BackColor = Color.Transparent,
                                      TextAlign = ContentAlignment.MiddleLeft, Font = Ui.F9B };
            row.Host.Controls.Add(row.Caption);
            foreach (var c in items) { row.Items.Add(c); row.Host.Controls.Add(c); }
            Rows.Add(row);
            Controls.Add(row.Host);
            if (widened) { foreach (var r in Rows) LayoutRow(r); } else LayoutRow(row);
            PerformLayout();
            return row;
        }

        public void Show(Row row, bool shown){
            if (row == null || row.Shown == shown) return;
            row.Shown = shown;
            row.Host.Visible = shown;
            PerformLayout();
        }

        public int PreferredHeight { get { int n = 0; foreach (var r in Rows) if (r.Shown) n++; return n * RowHeight; } }

        // 行的纵向位置（只算显示的行），测试和外部排版用
        public int TopOf(Row row){
            int y = 0;
            foreach (var r in Rows) { if (r == row) return y; if (r.Shown) y += RowHeight; }
            return -1;
        }

        void LayoutRow(Row row){
            var capRect = new Rectangle(0, 0, LabelWidth, RowHeight);
            if (row.Caption.Bounds != capRect) row.Caption.Bounds = capRect;
            int x = LabelWidth;
            Control prev = null;
            foreach (var c in row.Items) {
                bool unit = (c.Tag as string) == "unit";
                if (prev != null) x += unit ? Ui.S(3) : Gap;
                var size = c.AutoSize ? c.PreferredSize : c.Size;
                var want = new Rectangle(x, Math.Max(0, (RowHeight - size.Height) / 2), size.Width, size.Height);
                if (c.Bounds != want) c.Bounds = want;
                x += size.Width;
                prev = c;
            }
        }

        protected override void OnLayout(LayoutEventArgs e){
            SuspendLayout();
            try {
                int y = 0, w = ClientSize.Width;
                foreach (var r in Rows) {
                    if (!r.Shown) continue;
                    var want = new Rectangle(0, y, w, RowHeight);
                    if (r.Host.Bounds != want) r.Host.Bounds = want;
                    LayoutRow(r);
                    y += RowHeight;
                }
            } finally { ResumeLayout(false); }
        }
        protected override void OnBackColorChanged(EventArgs e){
            base.OnBackColorChanged(e);
            foreach (var r in Rows) r.Host.BackColor = BackColor;
        }
    }
}
