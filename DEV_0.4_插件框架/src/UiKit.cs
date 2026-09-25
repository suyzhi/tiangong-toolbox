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

    // 白底卡片，带 1px 边框和标题行。
    public sealed class Card : Panel {
        public string Title = "";
        public string Hint = "";
        public Card(){
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Ui.Bg;
            Padding = new Padding(12, 34, 12, 10);
            Dock = DockStyle.None;
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
                    var sz = TextRenderer.MeasureText(g, Title, Ui.F9B);
                    TextRenderer.DrawText(g, Hint, Ui.F8, new Point(13 + sz.Width + 8, 11), Ui.Muted);
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
            if (Hint.Length > 0)
                TextRenderer.DrawText(g, Hint, Ui.F9, new Point(2 + d + 12 + sz.Width, cy - sz.Height / 2), Ui.Muted);
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
