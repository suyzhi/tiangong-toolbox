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
            // 小屏幕（1366×768 的笔记本，任务栏一占只剩 728 高）：原来窗口固定 790 高、最小 680，底部「开始打孔」那一栏落在屏幕外，
            // 最小尺寸又不让缩。按 CAD 所在屏幕的工作区收一收（留出标题栏和边框），最小尺寸也不超过它
            Rectangle area;
            try { area = Screen.FromPoint(Cursor.Position).WorkingArea; } catch { area = new Rectangle(0, 0, 1920, 1040); }
            int chromeW = 16, chromeH = 40;
            int maxW = Math.Max(400, area.Width - chromeW - 20), maxH = Math.Max(300, area.Height - chromeH - 20);
            f.ClientSize = new Size(Math.Min(w, maxW), Math.Min(h, maxH));
            f.MinimumSize = new Size(Math.Min(minW, maxW + chromeW), Math.Min(minH, maxH + chromeH));
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
}
