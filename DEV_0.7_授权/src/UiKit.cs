using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace TianGongCadSuite {
    // 全部窗口共用的外观（0.9.0 起是「D · 侧栏向导」：白底、左侧浅灰步骤栏、靛蓝主色、底部一条操作栏）。
    // 颜色名沿用老名字（Bg / Accent / Warn …），老代码不用改就换上新配色；新加的名字见下半段。
    public static class Ui {
        public static readonly Color Bg      = Color.White;
        public static readonly Color CardBg  = Color.White;
        public static readonly Color Line    = Color.FromArgb(0xE4, 0xE4, 0xE7);
        public static readonly Color Text    = Color.FromArgb(0x18, 0x18, 0x1B);
        public static readonly Color Muted   = Color.FromArgb(0x63, 0x63, 0x6B);
        public static readonly Color Accent  = Color.FromArgb(0x4F, 0x46, 0xE5);
        public static readonly Color AccentD = Color.FromArgb(0x43, 0x38, 0xCA);
        public static readonly Color Ok      = Color.FromArgb(0x15, 0x80, 0x3D);
        public static readonly Color Warn    = Color.FromArgb(0x92, 0x40, 0x0E);
        public static readonly Color Bad     = Color.FromArgb(0xB9, 0x1C, 0x1C);
        public static readonly Color ChipBg  = Color.FromArgb(0xEE, 0xF2, 0xFF);
        // D 新加
        public static readonly Color Rail      = Color.FromArgb(0xF7, 0xF7, 0xF8);   // 侧栏、次要面板
        public static readonly Color Subtle    = Color.FromArgb(0xF4, 0xF4, 0xF5);   // 分段按钮底槽、悬停
        public static readonly Color Text2     = Color.FromArgb(0x3F, 0x3F, 0x46);   // 字段名
        public static readonly Color Faint     = Color.FromArgb(0xA1, 0xA1, 0xAA);   // 未开始的步骤圈
        public static readonly Color AccentSoft = ChipBg;                             // 选中底
        public static readonly Color AccentRing = Color.FromArgb(0xE0, 0xE7, 0xFF);   // 新增项闪一下的底色
        public static readonly Color AccentPress = Color.FromArgb(0x37, 0x30, 0xA3);  // 主按钮按下 / 进度条
        public static readonly Color OkSoft    = Color.FromArgb(0xDC, 0xFC, 0xE7);
        public static readonly Color WarnSoft  = Color.FromArgb(0xFE, 0xF3, 0xC7);
        public static readonly Color BadSoft   = Color.FromArgb(0xFE, 0xE2, 0xE2);
        public static readonly Color Disabled  = Color.FromArgb(0xE4, 0xE4, 0xE7);
        public static readonly Color DisabledText = Color.FromArgb(0x71, 0x71, 0x7A);

        public const string Family = "Microsoft YaHei UI";
        public const string Mono = "Consolas";
        public static readonly Font F9    = new Font(Family, 9.75F);                       // 正文 13px
        public static readonly Font F9B   = new Font(Family, 9.75F, FontStyle.Bold);
        public static readonly Font F10B  = new Font(Family, 10.5F, FontStyle.Bold);       // 步骤名、按钮 14px
        public static readonly Font F12B  = new Font(Family, 12.5F, FontStyle.Bold);
        public static readonly Font F8    = new Font(Family, 9F);                          // 说明小字 12px
        public static readonly Font Title = new Font(Family, 16.5F, FontStyle.Bold);       // 步骤标题 22px
        public static readonly Font Btn   = new Font(Family, 10.5F);
        public static readonly Font BtnB  = new Font(Family, 11.25F, FontStyle.Bold);      // 主按钮 15px
        public static readonly Font NumF  = new Font(Mono, 9.75F);
        public static readonly Font NumB  = new Font(Mono, 9.75F, FontStyle.Bold);

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
        public static void FillRound(Graphics g, Rectangle r, int radius, Color fill){
            var sm = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Rounded(r, radius)) using (var b = new SolidBrush(fill)) g.FillPath(b, path);
            g.SmoothingMode = sm;
        }
        public static void DrawRound(Graphics g, Rectangle r, int radius, Color line, float width){
            var sm = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Rounded(r, radius)) using (var p = new Pen(line, width)) g.DrawPath(p, path);
            g.SmoothingMode = sm;
        }
        // 勾：步骤做完、提示条成功
        public static void DrawCheck(Graphics g, Rectangle r, Color c, float width){
            var sm = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias;
            float x = r.X, y = r.Y, w = r.Width, h = r.Height;
            using (var p = new Pen(c, width) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                g.DrawLines(p, new[]{ new PointF(x + w * 0.2f, y + h * 0.52f), new PointF(x + w * 0.42f, y + h * 0.72f), new PointF(x + w * 0.8f, y + h * 0.3f) });
            g.SmoothingMode = sm;
        }

        // 窗口外壳：白底、统一字体、居中到 CAD 主窗口、Esc 关闭。
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
            return new Label { Text = text, AutoSize = true, ForeColor = Text2, BackColor = Color.Transparent };
        }
        public static Label Muted8(string text){
            return new Label { Text = text, AutoSize = true, ForeColor = Muted, Font = F8, BackColor = Color.Transparent };
        }

        public static NumericUpDown Num(decimal min, decimal max, decimal value, int decimals, decimal step, int width){
            var n = new NumericUpDown();
            n.Minimum = min; n.Maximum = max; n.Value = Clamp(value, min, max);
            n.DecimalPlaces = decimals; n.Increment = step;
            n.Width = width + 8; n.TextAlign = HorizontalAlignment.Right;
            n.BorderStyle = BorderStyle.FixedSingle;
            n.Font = NumF;
            return n;
        }
        public static decimal Clamp(decimal v, decimal min, decimal max){ return v < min ? min : (v > max ? max : v); }

        public static ComboBox Combo(int width, params string[] items){
            var c = new ComboBox();
            c.DropDownStyle = ComboBoxStyle.DropDownList;
            c.FlatStyle = FlatStyle.Flat;
            c.Width = width + 8;
            c.Items.AddRange(items);
            if (c.Items.Count > 0) c.SelectedIndex = 0;
            return c;
        }

        public static Button Primary(string text, int width, int height){
            return new FlatButton(text, FlatButton.Kind.Primary, width, height);
        }
        public static Button Secondary(string text, int width, int height){
            return new FlatButton(text, FlatButton.Kind.Secondary, width, height);
        }
        // 文字按钮（「配做规则…」「清空」这类，不抢主按钮的眼）
        public static Button Ghost(string text){
            int w = TextRenderer.MeasureText(text, F9).Width + 20;
            return new FlatButton(text, FlatButton.Kind.Ghost, w, 30);
        }
        // 卡片标题行上的小按钮：宽度按文字算，不用手填
        public static Button Small(string text){
            int w = TextRenderer.MeasureText(text, F8).Width + 20;
            var b = new FlatButton(text, FlatButton.Kind.Ghost, w, 26);
            b.Font = F8;
            return b;
        }

        // 一行文字输入的小对话框（WinForms 没有现成的 InputBox；VB 的那个要多引一个程序集）。取消返回 null。
        public static string Prompt(IWin32Window owner, string title, string label, string initial){
            using (var f = new Form()) {
                Shell(f, title, 400, 150, 320, 150);
                f.FormBorderStyle = FormBorderStyle.FixedToolWindow;
                var l = new Label { Text = label, AutoSize = false, Bounds = new Rectangle(18, 14, 364, 22), ForeColor = Text2, BackColor = Color.Transparent };
                var t = new TextBox { Text = initial ?? "", Bounds = new Rectangle(18, 40, 364, 28), BorderStyle = BorderStyle.FixedSingle };
                var ok = Primary("确定", 88, 36); ok.Bounds = new Rectangle(294, 98, 88, 36); ok.DialogResult = DialogResult.OK;
                var cancel = Secondary("取消", 80, 34); cancel.Bounds = new Rectangle(202, 99, 80, 34); cancel.DialogResult = DialogResult.Cancel;
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
        public int Gap = 10;
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

    // 两列容器：左列固定宽（或按比例），右列吃掉剩下的宽度。
    // 和 VerticalStack 一样手动排版 —— Dock 的填充顺序按 z-order 逆序，
    // 两个 Dock 混着用很容易变成"左列被右列挤没"。
    public sealed class SplitColumns : Panel {
        public int Gap = 20;
        public int LeftWidth;        // >0：左列固定宽度
        public float LeftFraction;   // >0：左列占可用宽度的比例
        public bool Divider;         // 中间画分隔线（D 里一般不画，靠留白分开）
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
            if (!Divider || Controls.Count < 2) return;
            int x = Controls[0].Right + Gap / 2;
            using (var p = new Pen(Ui.Line)) e.Graphics.DrawLine(p, x, 4, x, ClientSize.Height - 4);
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
            Padding = new Padding(14, 38, 14, 12);
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
                foreach (var c in header) x -= c.Width + 4;
                return x;
            }
        }
        protected override void OnLayout(LayoutEventArgs e){
            base.OnLayout(e);
            int x = Width - 8;
            for (int i = header.Count - 1; i >= 0; i--) {
                var c = header[i];
                x -= c.Width;
                c.Location = new Point(x, 7);
                x -= 4;
            }
        }
        protected override void OnPaint(PaintEventArgs e){
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Ui.Bg);
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            Ui.FillRound(g, r, 10, Fill ? Ui.Rail : Ui.CardBg);
            if (!Fill) Ui.DrawRound(g, r, 10, Ui.Line, 1);
            if (Title.Length > 0) {
                TextRenderer.DrawText(g, Title, Ui.F9B, new Point(14, 11), Ui.Text);
                if (Hint.Length > 0) {
                    // 说明文字只能画到标题行按钮的左边，放不下就省略号 —— 不能糊到按钮底下
                    var sz = TextRenderer.MeasureText(g, Title, Ui.F9B);
                    int hx = 14 + sz.Width + 6;
                    var hr = new Rectangle(hx, 10, Math.Max(0, HeaderLeft - hx - 4), 22);
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
            int d = 22, cy = Height / 2;
            var circle = new Rectangle(1, cy - d / 2, d, d);
            using (var b = new SolidBrush(Ui.Accent)) g.FillEllipse(b, circle);
            TextRenderer.DrawText(g, Number.ToString(), Ui.F8, circle, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            g.SmoothingMode = SmoothingMode.Default;
            var sz = TextRenderer.MeasureText(g, Title, Ui.F10B);
            TextRenderer.DrawText(g, Title, Ui.F10B, new Point(1 + d + 10, cy - sz.Height / 2), Ui.Text);
            // 说明文字用带省略号的矩形画：列宽不够时截断，而不是糊到右边那列上去
            if (Hint.Length > 0) {
                int hx = 1 + d + 14 + sz.Width;
                var hr = new Rectangle(hx, 0, Math.Max(0, Width - hx - 2), Height);
                TextRenderer.DrawText(g, Hint, Ui.F8, hr, Ui.Muted,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
        }
    }

    // 按钮：主按钮（靛蓝实心）/ 次按钮（白底细边）/ 文字按钮。悬停、按下的颜色是渐变过去的（Anim）。
    // Progress ≥ 0 时主按钮自己变成进度条（打孔中 3 / 6）。
    public sealed class FlatButton : Button {
        public enum Kind { Primary, Secondary, Ghost }
        public readonly Kind Style;
        Color back, fore, hover, press, line;
        float hot;            // 0 = 常态，1 = 悬停（渐变用）
        bool down;
        float progress = -1;  // <0：不画进度
        public int Radius = 8;

        public FlatButton(string text, Kind kind, int width, int height){
            Style = kind;
            Init(text, width, height);
            switch (kind) {
                case Kind.Primary:   back = Ui.Accent; fore = Color.White; hover = Ui.AccentD; press = Ui.AccentPress; line = Color.Empty; Font = height >= 40 ? Ui.BtnB : Ui.F9B; Radius = height >= 40 ? 10 : 8; break;
                case Kind.Secondary: back = Color.White; fore = Ui.Text; hover = Ui.Subtle; press = Ui.Line; line = Ui.Line; Font = height >= 36 ? Ui.Btn : Ui.F9; break;
                default:             back = Color.Empty; fore = Ui.Accent; hover = Ui.Subtle; press = Ui.Line; line = Color.Empty; Font = Ui.F9; break;
            }
        }
        // 老构造（自定颜色），留给零星的调用
        public FlatButton(string text, Color backColor, Color foreColor, Color hoverColor, int width, int height){
            Style = Kind.Secondary;
            Init(text, width, height);
            back = backColor; fore = foreColor; hover = hoverColor; press = hoverColor; line = Color.Empty;
            Font = Ui.F9B;
        }
        void Init(string text, int width, int height){
            Text = text;
            Width = width; Height = height;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
        // 文字按钮的字色（「清空」用灰字）
        public void SetFore(Color c){ fore = c; Invalidate(); }

        // 0..1 = 进度；负数 = 不显示进度
        public float Progress {
            get { return progress; }
            set {
                float v = value < 0 ? -1 : Math.Min(1f, value);
                if (Math.Abs(v - progress) < 0.0001f) return;
                float from = progress < 0 ? 0 : progress;
                if (v < 0 || !Anim.On) { progress = v; Invalidate(); return; }
                Anim.Run(this, 260, t => { progress = from + (v - from) * (float)t; Invalidate(); }, null, Anim.Linear, "progress");
            }
        }

        protected override void OnMouseEnter(EventArgs e){ Hover(1); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e){ down = false; Hover(0); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e){ down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e){ down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e){ Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnTextChanged(EventArgs e){ Invalidate(); base.OnTextChanged(e); }
        void Hover(float to){
            float from = hot;
            Anim.Run(this, Anim.Hover, t => { hot = from + (to - from) * (float)t; Invalidate(); }, null, null, "hover");
        }

        protected override void OnPaint(PaintEventArgs pe){
            var g = pe.Graphics;
            var parentBack = Parent != null ? Parent.BackColor : Ui.Bg;
            g.Clear(parentBack);
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            Color b, f;
            if (!Enabled && !(progress >= 0 && Style == Kind.Primary)) {
                b = Style == Kind.Primary ? Ui.Disabled : (Style == Kind.Ghost ? Color.Empty : Color.White);
                f = Style == Kind.Primary ? Ui.DisabledText : Ui.Faint;
            } else {
                Color baseBack = back.IsEmpty ? parentBack : back;
                b = down ? press : Anim.Mix(baseBack, hover, hot);
                f = fore;
            }
            if (!b.IsEmpty) Ui.FillRound(g, r, Radius, b);
            if (progress >= 0 && Style == Kind.Primary) {
                int w = (int)Math.Round(r.Width * progress);
                if (w > 0) {
                    var clip = g.Clip;
                    using (var path = Ui.Rounded(r, Radius)) {
                        g.SetClip(path, CombineMode.Intersect);
                        using (var br = new SolidBrush(Ui.AccentPress)) g.FillRectangle(br, 0, 0, w, Height);
                    }
                    g.Clip = clip;
                }
            }
            if (!line.IsEmpty) Ui.DrawRound(g, r, Radius, Enabled ? line : Ui.Line, 1);
            if (Focused && ShowFocusCues && Enabled) Ui.DrawRound(g, Rectangle.Inflate(r, -2, -2), Math.Max(2, Radius - 2), Style == Kind.Primary ? Color.FromArgb(160, Color.White) : Ui.AccentRing, 2);
            TextRenderer.DrawText(g, Text, Font, r, f,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    // 分段按钮：几个互斥选项排成一排，点一下就切换（比下拉框少点一次，选了什么一眼看得见）。
    // 用法和 ComboBox 一样：Items / SelectedIndex / SelectedIndexChanged。
    // 外观：浅灰底槽里一块白色滑块，选中项变了滑块滑过去（180ms）。
    public sealed class Segmented : Control {
        public readonly List<string> Items = new List<string>();
        public event EventHandler SelectedIndexChanged;
        int selected = -1, hover = -1;
        float thumbX = -1, thumbW;   // 滑块当前位置（动画中间值）
        public Segmented(params string[] items){
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Items.AddRange(items);
            Font = Ui.F9; Height = 32; BackColor = Color.White; TabStop = true; Cursor = Cursors.Hand;
            if (Items.Count > 0) selected = 0;
        }
        public int SelectedIndex {
            get { return selected; }
            set {
                int v = Math.Max(-1, Math.Min(Items.Count - 1, value));
                if (v == selected) return;
                int old = selected;
                selected = v;
                SlideThumb(old);
                var h = SelectedIndexChanged; if (h != null) h(this, EventArgs.Empty);
            }
        }
        public string SelectedText { get { return selected >= 0 && selected < Items.Count ? Items[selected] : ""; } }
        // 按文字宽度给每段分宽度：中文"圆柱沉孔"和"通孔"一样宽会很难看
        int[] Edges(){
            var e = new int[Items.Count + 1];
            if (Items.Count == 0) return e;
            int inner = Width - 6;
            var w = new int[Items.Count]; int total = 0;
            for (int i = 0; i < Items.Count; i++) { w[i] = TextRenderer.MeasureText(Items[i], Font).Width + 22; total += w[i]; }
            double scale = (double)inner / Math.Max(1, total);
            double x = 3;
            for (int i = 0; i < Items.Count; i++) { e[i] = (int)Math.Round(x); x += w[i] * scale; }
            e[Items.Count] = Width - 3;
            return e;
        }
        void SlideThumb(int old){
            var e = Edges();
            if (selected < 0 || e.Length <= selected + 1) { Invalidate(); return; }
            float toX = e[selected], toW = e[selected + 1] - e[selected];
            if (old < 0 || thumbX < 0 || !IsHandleCreated) { thumbX = toX; thumbW = toW; Invalidate(); return; }
            float fx = thumbX, fw = thumbW;
            Anim.Run(this, Anim.Select, t => { thumbX = fx + (toX - fx) * (float)t; thumbW = fw + (toW - fw) * (float)t; Invalidate(); }, null, null, "thumb");
        }
        protected override void OnResize(EventArgs e){ thumbX = -1; base.OnResize(e); }
        int HitTest(int x){
            var e = Edges();
            for (int i = 0; i < Items.Count; i++) if (x >= e[i] && x < e[i + 1]) return i;
            return -1;
        }
        // 刚好放得下全部文字的宽度
        public int PreferredWidth { get { int t = 7; foreach (var s in Items) t += TextRenderer.MeasureText(s, Font).Width + 22; return t; } }
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
            Ui.FillRound(g, outer, 8, Ui.Subtle);
            if (Focused) Ui.DrawRound(g, outer, 8, Ui.AccentRing, 2);
            if (selected >= 0 && e.Length > selected + 1) {
                if (thumbX < 0) { thumbX = e[selected]; thumbW = e[selected + 1] - e[selected]; }
                var tr = new Rectangle((int)thumbX, 3, (int)thumbW, Height - 7);
                Ui.FillRound(g, new Rectangle(tr.X, tr.Y + 1, tr.Width, tr.Height), 6, Color.FromArgb(24, 24, 24, 27));
                Ui.FillRound(g, tr, 6, Color.White);
            }
            for (int i = 0; i < Items.Count; i++) {
                var r = new Rectangle(e[i], 0, e[i + 1] - e[i], Height - 1);
                if (i == hover && i != selected && Enabled) Ui.FillRound(g, Rectangle.Inflate(new Rectangle(r.X, 3, r.Width, Height - 7), -1, 0), 6, Color.FromArgb(0xEA, 0xEA, 0xEC));
                Color fore = i == selected ? (Enabled ? Ui.Accent : Ui.Muted) : (Enabled ? Ui.Text : Ui.Faint);
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
        public int RowHeight = 40, LabelWidth = 52, Gap = 8;
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
            row.Caption = new Label { Text = caption, AutoSize = false, ForeColor = Ui.Text2, BackColor = Color.Transparent,
                                      TextAlign = ContentAlignment.MiddleLeft, Font = Ui.F9B };
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
                if (prev != null) x += unit ? 3 : Gap;
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
        protected override void OnBackColorChanged(EventArgs e){
            base.OnBackColorChanged(e);
            foreach (var r in Rows) r.Host.BackColor = BackColor;
        }
    }

    // 可点的筛选胶囊："漏打孔 3"，点一下切换显示/隐藏这一类。选中时浅色底 + 彩色字，没选中只留描边、字变灰。
    public sealed class ToggleChip : Control {
        public Color ChipColor = Ui.Accent;
        bool on = true, hover;
        public event EventHandler CheckedChanged;
        public ToggleChip(){
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.White; Font = Ui.F8; Height = 26; Cursor = Cursors.Hand;
        }
        public bool Checked {
            get { return on; }
            set { if (on == value) return; on = value; Invalidate(); var h = CheckedChanged; if (h != null) h(this, EventArgs.Empty); }
        }
        protected override void OnClick(EventArgs e){ Checked = !Checked; base.OnClick(e); }
        protected override void OnMouseEnter(EventArgs e){ hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e){ hover = false; Invalidate(); base.OnMouseLeave(e); }
        // 侧栏样式（配孔检查左边的四类）：整行、左边色点、右边数量；勾着 = 白底细边，没勾 = 灰字空心点
        public bool RailStyle;
        protected override void OnPaint(PaintEventArgs e){
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Color.White);
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            if (RailStyle) { PaintRail(g, r); return; }
            if (on) Ui.FillRound(g, r, 8, Color.FromArgb(hover ? 44 : 26, ChipColor));
            else if (hover) Ui.FillRound(g, r, 8, Ui.Subtle);
            Ui.DrawRound(g, r, 8, on ? Color.FromArgb(110, ChipColor) : Ui.Line, 1);
            TextRenderer.DrawText(g, (on ? "✓ " : "") + Text, on ? Ui.F9B : Font, r, on ? ChipColor : Ui.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
        void PaintRail(Graphics g, Rectangle r){
            if (on) { Ui.FillRound(g, r, 8, Color.White); Ui.DrawRound(g, r, 8, Ui.Line, 1); }
            else if (hover) Ui.FillRound(g, r, 8, Color.FromArgb(0xEF, 0xEF, 0xF1));
            string t = Text ?? ""; int sp = t.LastIndexOf(' ');
            string name = sp > 0 ? t.Substring(0, sp) : t, count = sp > 0 ? t.Substring(sp + 1) : "";
            var sm = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias;
            var dot = new Rectangle(12, r.Height / 2 - 4, 8, 8);
            if (on) using (var b = new SolidBrush(ChipColor)) g.FillEllipse(b, dot);
            else using (var p = new Pen(Ui.Faint, 1.4f)) g.DrawEllipse(p, dot);
            if (on) Ui.DrawCheck(g, new Rectangle(r.Right - 22, r.Height / 2 - 6, 12, 12), ChipColor, 1.6f);
            g.SmoothingMode = sm;
            TextRenderer.DrawText(g, name, on ? Ui.F9B : Ui.F9, new Rectangle(30, 0, r.Width - 90, r.Height), on ? Ui.Text : Ui.DisabledText,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, count, on ? Ui.F9B : Ui.F9, new Rectangle(r.Right - 70, 0, 42, r.Height), on ? Ui.Text : Ui.DisabledText,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        }
    }

    // 状态徽标：一行小字 + 浅色底（「漏打孔」「已完成」）。
    public sealed class Chip : Control {
        public Color ChipColor = Ui.Accent;
        public Chip(){
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Ui.Bg; Font = Ui.F9B; Height = 26;
        }
        protected override void OnPaint(PaintEventArgs e){
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Ui.Bg);
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            Ui.FillRound(g, r, 6, Color.FromArgb(30, ChipColor));
            TextRenderer.DrawText(g, Text, Font, r, ChipColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    // Esc 关窗口（0.8.5）：窗口里已经点好东西时，按一次只提醒，3 秒内再按一次才关。
    // 原来一按就关 —— 在数值框里按 Esc 想撤销输入、点了半天的东西全没了（右键现在留给天工的 QuickPick 选遮挡面）。「关闭」按钮照旧直接关
    // 数字框里刚敲的数还没"提交"（焦点还在框里）：读一次 Value 就会按框里的字提交并触发 ValueChanged。
    // 快捷键（Ctrl+Enter 打孔 / 应用）不经过按钮、焦点不离开数字框，原来用的还是改之前的数（孔径敲成 8、打出来还是 6.6）
    public static class EditCommit {
        public static void All(Control root){
            if (root == null) return;
            var n = root as NumericUpDown;
            if (n != null) { try { var v = n.Value; } catch { } return; }
            foreach (Control c in root.Controls) All(c);
        }
    }
    public sealed class QuickClose {
        DateTime asked = DateTime.MinValue;
        // lost：会丢掉什么（"3 个参考孔、1 个打孔面"），空 = 没什么可丢的，直接关
        public bool Confirm(string how, string lost, Action<string> warn){
            if (string.IsNullOrEmpty(lost)) return true;
            if ((DateTime.Now - asked).TotalSeconds < 3) return true;
            asked = DateTime.Now;
            if (warn != null) warn("再按一次" + how + "关闭窗口（" + lost + "会丢掉）。");
            return false;
        }
    }
}
