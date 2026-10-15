using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace TianGongCadSuite {
    // 打孔系列三个窗口共用的外观。目标是：一眼能看出"现在该干什么"，
    // 数字对齐、留白一致、状态有颜色。
    public static class Ui {
        public static readonly Color Bg      = Color.FromArgb(0xF4, 0xF6, 0xFA);
        public static readonly Color CardBg  = Color.White;
        public static readonly Color Line    = Color.FromArgb(0xDD, 0xE3, 0xEC);
        public static readonly Color Text    = Color.FromArgb(0x1B, 0x24, 0x33);
        public static readonly Color Muted   = Color.FromArgb(0x6B, 0x78, 0x8C);
        public static readonly Color Accent  = Color.FromArgb(0x0F, 0x6C, 0xBD);
        public static readonly Color AccentD = Color.FromArgb(0x0A, 0x50, 0x8C);
        public static readonly Color Ok      = Color.FromArgb(0x0F, 0x7B, 0x3F);
        public static readonly Color Warn    = Color.FromArgb(0xA9, 0x62, 0x0A);
        public static readonly Color Bad     = Color.FromArgb(0xBE, 0x2C, 0x2C);
        public static readonly Color ChipBg  = Color.FromArgb(0xEC, 0xF2, 0xFB);

        public const string Family = "Microsoft YaHei UI";
        public static readonly Font F9    = new Font(Family, 9F);
        public static readonly Font F9B   = new Font(Family, 9F, FontStyle.Bold);
        public static readonly Font F10B  = new Font(Family, 10.5F, FontStyle.Bold);
        public static readonly Font F12B  = new Font(Family, 12.5F, FontStyle.Bold);
        public static readonly Font F8    = new Font(Family, 8.25F);

        public static GraphicsPath Rounded(Rectangle r, int radius){
            var p = new GraphicsPath();
            int d = radius * 2;
            if (d <= 0 || d > r.Width || d > r.Height) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d - 1, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d - 1, r.Bottom - d - 1, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d - 1, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // 窗口外壳：浅灰底、统一字体、居中到 CAD 主窗口、Esc 关闭。
        public static void Shell(Form f, string title, int w, int h, int minW, int minH){
            f.Text = title;
            f.Font = F9;
            f.BackColor = Bg;
            f.ForeColor = Text;
            f.ClientSize = new Size(w, h);
            f.MinimumSize = new Size(minW, minH);
            f.FormBorderStyle = FormBorderStyle.SizableToolWindow;
            f.StartPosition = FormStartPosition.CenterParent;
            f.ShowInTaskbar = false;
            f.KeyPreview = true;
        }

        public static Card Card(string title, int height, string hint){
            var c = new Card();
            c.Title = title; c.Hint = hint;
            c.Height = height;
            return c;
        }

        public static Label Caption(string text){
            return new Label { Text = text, AutoSize = true, ForeColor = Text, BackColor = Color.Transparent };
        }
        public static Label Muted8(string text){
            return new Label { Text = text, AutoSize = true, ForeColor = Muted, Font = F8, BackColor = Color.Transparent };
        }

        public static NumericUpDown Num(decimal min, decimal max, decimal value, int decimals, decimal step, int width){
            var n = new NumericUpDown();
            n.Minimum = min; n.Maximum = max; n.Value = Clamp(value, min, max);
            n.DecimalPlaces = decimals; n.Increment = step;
            n.Width = width; n.TextAlign = HorizontalAlignment.Right;
            n.BorderStyle = BorderStyle.FixedSingle;
            return n;
        }
        public static decimal Clamp(decimal v, decimal min, decimal max){ return v < min ? min : (v > max ? max : v); }

        public static ComboBox Combo(int width, params string[] items){
            var c = new ComboBox();
            c.DropDownStyle = ComboBoxStyle.DropDownList;
            c.Width = width;
            c.Items.AddRange(items);
            if (c.Items.Count > 0) c.SelectedIndex = 0;
            return c;
        }

        public static Button Primary(string text, int width, int height){
            return new FlatButton(text, Accent, Color.White, AccentD, width, height);
        }
        public static Button Secondary(string text, int width, int height){
            return new FlatButton(text, Color.FromArgb(0xE8, 0xEC, 0xF3), Text, Color.FromArgb(0xD5, 0xDC, 0xE8), width, height);
        }
        // 卡片标题行上的小按钮：宽度按文字算，不用手填
        public static Button Small(string text){
            int w = TextRenderer.MeasureText(text, F8).Width + 18;
            var b = new FlatButton(text, Color.FromArgb(0xEE, 0xF1, 0xF6), Text, Color.FromArgb(0xD5, 0xDC, 0xE8), w, 22);
            b.Font = F8;
            return b;
        }

        // 一行文字输入的小对话框（WinForms 没有现成的 InputBox；VB 的那个要多引一个程序集）。取消返回 null。
        public static string Prompt(IWin32Window owner, string title, string label, string initial){
            using (var f = new Form()) {
                Shell(f, title, 360, 116, 300, 116);
                f.FormBorderStyle = FormBorderStyle.FixedToolWindow;
                var l = new Label { Text = label, AutoSize = false, Bounds = new Rectangle(12, 10, 336, 20), ForeColor = Text, BackColor = Color.Transparent };
                var t = new TextBox { Text = initial ?? "", Bounds = new Rectangle(12, 34, 336, 24) };
                var ok = Primary("确定", 80, 28); ok.Bounds = new Rectangle(176, 76, 80, 28); ok.DialogResult = DialogResult.OK;
                var cancel = Secondary("取消", 80, 28); cancel.Bounds = new Rectangle(266, 76, 80, 28); cancel.DialogResult = DialogResult.Cancel;
                f.Controls.Add(l); f.Controls.Add(t); f.Controls.Add(ok); f.Controls.Add(cancel);
                f.AcceptButton = ok; f.CancelButton = cancel;
                f.Shown += (s, e) => { t.SelectAll(); t.Focus(); };
                return f.ShowDialog(owner) == DialogResult.OK ? t.Text : null;
            }
        }

        // 步骤标题：编号圆点 + 加粗标题 + 灰色说明。
        public static StepHeader Step(int number, string title, string hint){
            return new StepHeader { Number = number, Title = title, Hint = hint, Height = 26 };
        }
    }

    // 纵向堆叠容器：严格按"添加顺序 = 从上到下"摆放。
    // 不用 Dock —— WinForms 的 Dock 是按 z-order 逆序填充的，多个 Dock=Top 控件会整体颠倒，
    // 第一版窗口就是这么翻车的（预览跑到最上面、"开始打孔"被挤没了）。
    // 高度为 0 或 Tag="fill" 的控件分配剩余高度；窗口不够高时自动出现滚动条。
    public sealed class VerticalStack : Panel {
        public int Gap = 8;
        public VerticalStack(){
            SetStyle(ControlStyles.ResizeRedraw, true);
            BackColor = Ui.Bg;
            AutoScroll = true;
            Padding = new Padding(12, 10, 12, 10);
        }
        protected override void OnLayout(LayoutEventArgs e){
            SuspendLayout();
            try {
                var fills = new List<Control>();
                int used = 0, visible = 0;
                foreach (Control c in Controls) {
                    if (!c.Visible) continue;
                    visible++;
                    if ((c.Tag as string) == "fill") fills.Add(c); else used += c.Height;
                }
                used += Gap * Math.Max(0, visible - 1);
                int avail = ClientSize.Height - Padding.Vertical;
                int each = fills.Count > 0 ? Math.Max(70, (avail - used) / fills.Count) : 0;
                int y = Padding.Top;
                int w = ClientSize.Width - Padding.Horizontal;
                foreach (Control c in Controls) {
                    if (!c.Visible) continue;
                    int h = (c.Tag as string) == "fill" ? each : c.Height;
                    var want = new Rectangle(Padding.Left, y, w, h);
                    if (c.Bounds != want) c.Bounds = want;
                    y += h + Gap;
                }
                AutoScrollMinSize = new Size(0, y - Gap + Padding.Bottom);
            } finally { ResumeLayout(true); }
        }
        // 加控件：一律清掉 Dock —— 本容器手动排版，Dock 会跟它打架。
        public void Add(Control c, bool fill = false){
            c.Dock = DockStyle.None;
            if (fill) c.Tag = "fill";
            Controls.Add(c);
        }
        // 子控件显示/隐藏后要重新排
        public void Relayout(){ PerformLayout(); Invalidate(true); }
    }

    // 两列容器：左列固定宽（或按比例），右列吃掉剩下的宽度，中间留一条分隔线。
    // 和 VerticalStack 一样手动排版 —— Dock 的填充顺序按 z-order 逆序，
    // 两个 Dock 混着用很容易变成"左列被右列挤没"。
    public sealed class SplitColumns : Panel {
        public int Gap = 14;
        public int LeftWidth;        // >0：左列固定宽度
        public float LeftFraction;   // >0：左列占可用宽度的比例
        public SplitColumns(int leftWidth){
            LeftWidth = leftWidth;
            BackColor = Ui.Bg;
            SetStyle(ControlStyles.ResizeRedraw, true);
        }
        public SplitColumns(float leftFraction){
            LeftFraction = leftFraction;
            BackColor = Ui.Bg;
            SetStyle(ControlStyles.ResizeRedraw, true);
        }
        protected override void OnLayout(LayoutEventArgs e){
            SuspendLayout();
            try {
                if (Controls.Count < 2) return;
                int avail = ClientSize.Width - Gap;
                int left = LeftWidth > 0 ? LeftWidth : (LeftFraction > 0 ? (int)(avail * LeftFraction) : avail / 2);
                if (left < 60) left = 60;
                if (left > avail - 60) left = Math.Max(60, avail - 60);
                var a = Controls[0]; var b = Controls[1];
                var wa = new Rectangle(0, 0, left, ClientSize.Height);
                var wb = new Rectangle(left + Gap, 0, Math.Max(0, ClientSize.Width - left - Gap), ClientSize.Height);
                if (a.Bounds != wa) a.Bounds = wa;
                if (b.Bounds != wb) b.Bounds = wb;
            } finally { ResumeLayout(true); Invalidate(); }
        }
        public void Add(Control c){
            c.Dock = DockStyle.None;
            Controls.Add(c);
        }
        protected override void OnPaint(PaintEventArgs e){
            base.OnPaint(e);
            if (Controls.Count < 2) return;
            int x = Controls[0].Right + Gap / 2;
            using (var p = new Pen(Ui.Line)) e.Graphics.DrawLine(p, x, 4, x, ClientSize.Height - 4);
        }
    }

    // 白底卡片，带 1px 边框和标题行。
    public sealed class Card : Panel {
        public string Title = "";
        public string Hint = "";
        readonly List<Control> header = new List<Control>();
        public Card(){
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Ui.Bg;
            Padding = new Padding(12, 34, 12, 10);
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
                int x = Width - 10;
                foreach (var c in header) x -= c.Width + 6;
                return x;
            }
        }
        protected override void OnLayout(LayoutEventArgs e){
            base.OnLayout(e);
            int x = Width - 10;
            for (int i = header.Count - 1; i >= 0; i--) {
                var c = header[i];
                x -= c.Width;
                c.Location = new Point(x, 6);
                x -= 6;
            }
        }
        protected override void OnPaint(PaintEventArgs e){
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = Ui.Rounded(r, 6)) {
                using (var b = new SolidBrush(Ui.CardBg)) g.FillPath(b, path);
                using (var p = new Pen(Ui.Line)) g.DrawPath(p, path);
            }
            g.SmoothingMode = SmoothingMode.Default;
            if (Title.Length > 0) {
                TextRenderer.DrawText(g, Title, Ui.F9B, new Point(13, 9), Ui.Text);
                if (Hint.Length > 0) {
                    // 说明文字只能画到标题行按钮的左边，放不下就省略号 —— 不能糊到按钮底下
                    var sz = TextRenderer.MeasureText(g, Title, Ui.F9B);
                    int hx = 13 + sz.Width + 6;
                    var hr = new Rectangle(hx, 9, Math.Max(0, HeaderLeft - hx - 4), 20);
                    TextRenderer.DrawText(g, Hint, Ui.F8, hr, Ui.Muted,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                }
            }
        }
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
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int d = 17, cy = Height / 2;
            var circle = new Rectangle(2, cy - d / 2, d, d);
            using (var b = new SolidBrush(Ui.Accent)) g.FillEllipse(b, circle);
            TextRenderer.DrawText(g, Number.ToString(), Ui.F8, circle, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            g.SmoothingMode = SmoothingMode.Default;
            var sz = TextRenderer.MeasureText(g, Title, Ui.F9B);
            TextRenderer.DrawText(g, Title, Ui.F9B, new Point(2 + d + 8, cy - sz.Height / 2), Ui.Text);
            // 说明文字用带省略号的矩形画：列宽不够时截断，而不是糊到右边那列上去
            if (Hint.Length > 0) {
                int hx = 2 + d + 12 + sz.Width;
                var hr = new Rectangle(hx, 0, Math.Max(0, Width - hx - 2), Height);
                TextRenderer.DrawText(g, Hint, Ui.F9, hr, Ui.Muted,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
        }
    }

    // 扁平按钮，带悬停/按下反馈。
    public sealed class FlatButton : Button {
        readonly Color back, fore, hover;
        public FlatButton(string text, Color backColor, Color foreColor, Color hoverColor, int width, int height){
            Text = text; back = backColor; fore = foreColor; hover = hoverColor;
            Width = width; Height = height;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            BackColor = back; ForeColor = fore;
            Font = Ui.F9B;
            UseVisualStyleBackColor = false;
            SetStyle(ControlStyles.OptimizedDoubleBuffer, true);
        }
        protected override void OnMouseEnter(EventArgs e){ if (Enabled) BackColor = hover; base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e){ if (Enabled) BackColor = back; base.OnMouseLeave(e); }
        protected override void OnEnabledChanged(EventArgs e){
            BackColor = Enabled ? back : Color.FromArgb(0xD8, 0xDD, 0xE5);
            ForeColor = Enabled ? fore : Color.FromArgb(0x8A, 0x93, 0xA3);
            base.OnEnabledChanged(e);
        }
        protected override void OnPaint(PaintEventArgs pevent){
            base.OnPaint(pevent);
        }
    }

    // 分段按钮：几个互斥选项排成一排，点一下就切换（比下拉框少点一次，选了什么一眼看得见）。
    // 用法和 ComboBox 一样：Items / SelectedIndex / SelectedIndexChanged。
    public sealed class Segmented : Control {
        public readonly List<string> Items = new List<string>();
        public event EventHandler SelectedIndexChanged;
        int selected = -1, hover = -1;
        public Segmented(params string[] items){
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Items.AddRange(items);
            Font = Ui.F9; Height = 26; BackColor = Color.White; TabStop = true;
            if (Items.Count > 0) selected = 0;
        }
        public int SelectedIndex {
            get { return selected; }
            set {
                int v = Math.Max(-1, Math.Min(Items.Count - 1, value));
                if (v == selected) return;
                selected = v; Invalidate();
                var h = SelectedIndexChanged; if (h != null) h(this, EventArgs.Empty);
            }
        }
        public string SelectedText { get { return selected >= 0 && selected < Items.Count ? Items[selected] : ""; } }
        // 按文字宽度给每段分宽度：中文"圆柱沉孔"和"通孔"一样宽会很难看
        int[] Edges(){
            var e = new int[Items.Count + 1];
            if (Items.Count == 0) return e;
            var w = new int[Items.Count]; int total = 0;
            for (int i = 0; i < Items.Count; i++) { w[i] = TextRenderer.MeasureText(Items[i], Font).Width + 14; total += w[i]; }
            double scale = (double)(Width - 1) / Math.Max(1, total);
            double x = 0;
            for (int i = 0; i < Items.Count; i++) { e[i] = (int)Math.Round(x); x += w[i] * scale; }
            e[Items.Count] = Width - 1;
            return e;
        }
        int HitTest(int x){
            var e = Edges();
            for (int i = 0; i < Items.Count; i++) if (x >= e[i] && x < e[i + 1]) return i;
            return -1;
        }
        // 刚好放得下全部文字的宽度
        public int PreferredWidth { get { int t = 1; foreach (var s in Items) t += TextRenderer.MeasureText(s, Font).Width + 14; return t; } }
        protected override void OnMouseMove(MouseEventArgs e){ int h = HitTest(e.X); if (h != hover) { hover = h; Invalidate(); } base.OnMouseMove(e); }
        protected override void OnMouseLeave(EventArgs e){ hover = -1; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e){
            if (Enabled && e.Button == MouseButtons.Left) { Focus(); int h = HitTest(e.X); if (h >= 0) SelectedIndex = h; }
            base.OnMouseDown(e);
        }
        protected override bool IsInputKey(Keys k){ return k == Keys.Left || k == Keys.Right || base.IsInputKey(k); }
        protected override void OnKeyDown(KeyEventArgs e){
            if (e.KeyCode == Keys.Left && selected > 0) SelectedIndex = selected - 1;
            else if (e.KeyCode == Keys.Right && selected < Items.Count - 1) SelectedIndex = selected + 1;
            base.OnKeyDown(e);
        }
        protected override void OnGotFocus(EventArgs e){ Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e){ Invalidate(); base.OnLostFocus(e); }
        protected override void OnEnabledChanged(EventArgs e){ Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnPaint(PaintEventArgs pe){
            var g = pe.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Color.White);
            var e = Edges();
            var outer = new Rectangle(0, 0, Width - 1, Height - 1);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Ui.Rounded(outer, 4)) {
                using (var b = new SolidBrush(Color.White)) g.FillPath(b, path);
                for (int i = 0; i < Items.Count; i++) {
                    var r = new Rectangle(e[i], 0, e[i + 1] - e[i], Height - 1);
                    Color fill = i == selected ? (Enabled ? Ui.Accent : Color.FromArgb(0xB8, 0xC2, 0xD0))
                               : (i == hover && Enabled ? Ui.ChipBg : Color.White);
                    var clip = g.Clip; g.SetClip(path, CombineMode.Intersect);
                    using (var b = new SolidBrush(fill)) g.FillRectangle(b, r);
                    g.Clip = clip;
                }
                using (var p = new Pen(Focused ? Ui.Accent : Ui.Line)) g.DrawPath(p, path);
            }
            g.SmoothingMode = SmoothingMode.Default;
            for (int i = 0; i < Items.Count; i++) {
                var r = new Rectangle(e[i], 0, e[i + 1] - e[i], Height - 1);
                if (i > 0 && i != selected && i - 1 != selected) using (var p = new Pen(Ui.Line)) g.DrawLine(p, e[i], 4, e[i], Height - 5);
                Color fore = i == selected ? Color.White : (Enabled ? Ui.Text : Ui.Muted);
                TextRenderer.DrawText(g, Items[i], i == selected ? Ui.F9B : Font, r, fore,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }
    }

    // 表单网格：每行 = 左边一个标签 + 右边一串控件（从左往右排，垂直居中）。
    // 行可以整行隐藏，下面的行自动顶上来 —— 不留空带、不用手算坐标。
    // 隐藏状态记在 Row.Shown 里而不是读 Control.Visible：窗口还没显示时 Visible 的 getter 一律是 false，
    // 拿它当排版依据会时灵时不灵（无窗口的单元测试下就翻车）。
    public sealed class FieldGrid : Panel {
        public int RowHeight = 32, LabelWidth = 44, Gap = 6;
        public sealed class Row {
            public Label Caption;
            public Panel Host;
            public readonly List<Control> Items = new List<Control>();
            public bool Shown = true;
        }
        public readonly List<Row> Rows = new List<Row>();
        public FieldGrid(){ BackColor = Color.White; }

        // 空白占位（控件之间要多空一点时用）
        public static Control Space(int width){ return new Panel { Width = width, Height = 1, BackColor = Color.Transparent }; }
        // 单位：紧跟在数字框后面，间距比一般控件小
        public static Label Unit(string text){
            var l = new Label { Text = text, AutoSize = true, ForeColor = Ui.Muted, Font = Ui.F8, BackColor = Color.Transparent };
            l.Tag = "unit";
            return l;
        }

        public Row Add(string caption, params Control[] items){
            var row = new Row();
            row.Host = new Panel { BackColor = BackColor, Height = RowHeight };
            row.Caption = new Label { Text = caption, AutoSize = false, ForeColor = Ui.Muted, BackColor = Color.Transparent,
                                      TextAlign = ContentAlignment.MiddleLeft, Font = Ui.F9 };
            row.Host.Controls.Add(row.Caption);
            foreach (var c in items) { row.Items.Add(c); row.Host.Controls.Add(c); }
            Rows.Add(row);
            Controls.Add(row.Host);
            LayoutRow(row);
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
            row.Caption.SetBounds(0, 0, LabelWidth, RowHeight);
            int x = LabelWidth;
            Control prev = null;
            foreach (var c in row.Items) {
                bool unit = (c.Tag as string) == "unit";
                if (prev != null) x += unit ? 2 : Gap;
                var size = c.AutoSize ? c.PreferredSize : c.Size;
                c.SetBounds(x, Math.Max(0, (RowHeight - size.Height) / 2), size.Width, size.Height);
                x += size.Width;
                prev = c;
            }
        }

        protected override void OnLayout(LayoutEventArgs e){
            base.OnLayout(e);
            int y = 0, w = ClientSize.Width;
            foreach (var r in Rows) {
                if (!r.Shown) continue;
                r.Host.SetBounds(0, y, w, RowHeight);
                LayoutRow(r);
                y += RowHeight;
            }
        }
    }

    // 可点的筛选胶囊："漏打孔 3"，点一下切换显示/隐藏这一类。选中时实心底色，没选中只留描边、字变灰。
    public sealed class ToggleChip : Control {
        public Color ChipColor = Ui.Accent;
        bool on = true, hover;
        public event EventHandler CheckedChanged;
        public ToggleChip(){
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.White; Font = Ui.F8; Height = 22; Cursor = Cursors.Hand;
        }
        public bool Checked {
            get { return on; }
            set { if (on == value) return; on = value; Invalidate(); var h = CheckedChanged; if (h != null) h(this, EventArgs.Empty); }
        }
        protected override void OnClick(EventArgs e){ Checked = !Checked; base.OnClick(e); }
        protected override void OnMouseEnter(EventArgs e){ hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e){ hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnPaint(PaintEventArgs e){
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Color.White);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = Ui.Rounded(r, 10)) {
                if (on) using (var b = new SolidBrush(Color.FromArgb(hover ? 48 : 30, ChipColor))) g.FillPath(b, path);
                else if (hover) using (var b = new SolidBrush(Color.FromArgb(14, Ui.Muted))) g.FillPath(b, path);
                using (var p = new Pen(on ? Color.FromArgb(120, ChipColor) : Ui.Line)) g.DrawPath(p, path);
            }
            g.SmoothingMode = SmoothingMode.Default;
            TextRenderer.DrawText(g, (on ? "✓ " : "") + Text, on ? Ui.F9B : Font, r, on ? ChipColor : Ui.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    // 状态胶囊：一行小字 + 底色。
    public sealed class Chip : Control {
        public Color ChipColor = Ui.Accent;
        public Chip(){
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Ui.Bg; Font = Ui.F9B; Height = 24;
        }
        protected override void OnPaint(PaintEventArgs e){
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = Ui.Rounded(r, 5)) {
                using (var b = new SolidBrush(Color.FromArgb(28, ChipColor))) g.FillPath(b, path);
                using (var p = new Pen(Color.FromArgb(90, ChipColor))) g.DrawPath(p, path);
            }
            g.SmoothingMode = SmoothingMode.Default;
            TextRenderer.DrawText(g, Text, Font, r, ChipColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }
}
