using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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

        // 当前进程的 DPI 缩放比例（96 DPI = 1.0，125% = 1.25，150% = 1.5）。
        // 字体按 pt 创建会随系统缩放变大，所有像素尺寸与坐标都经 S() 同比放大，避免高 DPI 下文字截断或诡异换行。
        public static readonly float DpiScale = DetectDpiScale();
        static float DetectDpiScale(){
            try {
                using (var g = Graphics.FromHwnd(IntPtr.Zero)) {
                    float s = g.DpiX / 96f;
                    if (float.IsNaN(s) || float.IsInfinity(s) || s < 1f) return 1f;
                    return s > 2.5f ? 2.5f : s;
                }
            } catch { return 1f; }
        }
        public static int S(int px){ return (int)Math.Round(px * DpiScale); }
        public static Padding SPad(int left, int top, int right, int bottom){ return new Padding(S(left), S(top), S(right), S(bottom)); }

        static readonly ConditionalWeakTable<object, object> scaledCols = new ConditionalWeakTable<object, object>();
        static readonly PropertyInfo doubleBufferedProp = typeof(Control).GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic);
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);
        const int WM_SETREDRAW = 0x000B;
        const uint RDW_INVALIDATE = 0x0001, RDW_ERASE = 0x0004, RDW_ALLCHILDREN = 0x0080;

        [ThreadStatic] static Dictionary<int, SolidBrush> brushCache;
        [ThreadStatic] static Dictionary<long, Pen> penCache;
        [ThreadStatic] static Dictionary<IntPtr, int> redrawDepth;

        // 统一开启双缓冲（DataGridView / ListView / ListBox / Panel 等 WinForms 控件默认关闭，是滚动与重绘白闪的主因）
        public static void EnableDoubleBuffer(Control c){
            if (c == null || doubleBufferedProp == null) return;
            try { doubleBufferedProp.SetValue(c, true, null); } catch { }
        }

        // 批量切视图/改列可见性/切步骤时暂挂 Win32 重绘，等布局全部改完再一次性合帧绘制，避免逐控件闪烁卡顿。
        public static bool BeginRedrawFreeze(Control c){
            if (c == null || c.IsDisposed || !c.IsHandleCreated) return false;
            if (redrawDepth == null) redrawDepth = new Dictionary<IntPtr, int>();
            IntPtr h = c.Handle;
            int d;
            redrawDepth.TryGetValue(h, out d);
            redrawDepth[h] = d + 1;
            if (d == 0) {
                try { SendMessage(h, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero); } catch { }
            }
            return true;
        }
        public static void EndRedrawFreeze(Control c){ EndRedrawFreeze(c, true, true); }
        public static void EndRedrawFreeze(Control c, bool token, bool repaint = true){
            if (!token || c == null || c.IsDisposed || !c.IsHandleCreated) return;
            if (redrawDepth == null) return;
            IntPtr h = c.Handle;
            int d;
            if (!redrawDepth.TryGetValue(h, out d)) return;
            d--;
            if (d > 0) { redrawDepth[h] = d; return; }
            redrawDepth.Remove(h);
            try {
                SendMessage(h, WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
                if (repaint && c.Visible) RedrawWindow(h, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_ERASE | RDW_ALLCHILDREN);
            } catch { }
        }

        // 安全清空并释放容器内的所有子控件：WinForms 的 Controls.Clear() 不会释放 Win32 HWND，反复重建按钮会永久泄漏句柄并越用越卡。
        public static void DisposeChildren(Control parent){
            if (parent == null || parent.Controls.Count == 0) return;
            var old = new Control[parent.Controls.Count];
            parent.Controls.CopyTo(old, 0);
            parent.Controls.Clear();
            for (int i = 0; i < old.Length; i++) {
                try { if (old[i] != null && !old[i].IsDisposed) old[i].Dispose(); } catch { }
            }
        }

        // 安全清空并释放右键菜单项，防止每次右键重建菜单时泄漏 ToolStripItem 与闭包。
        public static void DisposeItems(ToolStripItemCollection items){
            if (items == null || items.Count == 0) return;
            var old = new ToolStripItem[items.Count];
            items.CopyTo(old, 0);
            items.Clear();
            for (int i = 0; i < old.Length; i++) {
                try { if (old[i] != null && !old[i].IsDisposed) old[i].Dispose(); } catch { }
            }
        }

        internal static SolidBrush CachedBrush(Color c){
            if (brushCache == null) brushCache = new Dictionary<int, SolidBrush>(32);
            int key = c.ToArgb();
            SolidBrush b;
            if (brushCache.TryGetValue(key, out b)) return b;
            if (brushCache.Count >= 64) {
                foreach (var kv in brushCache) try { kv.Value.Dispose(); } catch { }
                brushCache.Clear();
            }
            b = new SolidBrush(c);
            brushCache[key] = b;
            return b;
        }
        internal static Pen CachedPen(Color c, float width){
            if (penCache == null) penCache = new Dictionary<long, Pen>(32);
            int wKey = (int)Math.Round(width * 100f);
            long key = ((long)(uint)c.ToArgb() << 20) ^ (uint)wKey;
            Pen p;
            if (penCache.TryGetValue(key, out p)) return p;
            if (penCache.Count >= 64) {
                foreach (var kv in penCache) try { kv.Value.Dispose(); } catch { }
                penCache.Clear();
            }
            p = new Pen(c, width);
            penCache[key] = p;
            return p;
        }

        // 让 DataGridView 表头不换行截断、行高与列宽随 DPI 放大（可重复调用，不会二次放大同一列），并自动开启双缓冲
        public static void FitGrid(DataGridView grid){
            if (grid == null) return;
            EnableDoubleBuffer(grid);
            grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.False;
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            grid.ColumnHeadersHeight = Math.Max(S(34), TextRenderer.MeasureText("测", grid.ColumnHeadersDefaultCellStyle.Font ?? F8).Height + S(16));
            grid.RowTemplate.Height = Math.Max(S(30), TextRenderer.MeasureText("测", grid.DefaultCellStyle.Font ?? F9).Height + S(12));
            var hf = grid.ColumnHeadersDefaultCellStyle.Font ?? grid.Font ?? F8;
            foreach (DataGridViewColumn col in grid.Columns) {
                if (!col.Visible) continue;
                int need = TextRenderer.MeasureText(col.HeaderText ?? "", hf).Width + S(26);
                object marker;
                bool first = !scaledCols.TryGetValue(col, out marker);
                if (first) scaledCols.Add(col, new object());
                if (col.AutoSizeMode == DataGridViewAutoSizeColumnMode.Fill) {
                    col.MinimumWidth = Math.Max(col.MinimumWidth, Math.Min(need, S(220)));
                } else {
                    int baseW = first ? S(col.Width) : col.Width;
                    col.Width = Math.Max(baseW, need);
                }
            }
        }

        // 让 ListView 表头文字随 DPI 不被截成「已有...」（可重复调用），并自动开启双缓冲
        public static void FitListView(ListView lv){
            if (lv == null) return;
            EnableDoubleBuffer(lv);
            var f = lv.Font ?? F9;
            foreach (ColumnHeader col in lv.Columns) {
                int need = TextRenderer.MeasureText(col.Text ?? "", f).Width + S(24);
                object marker;
                bool first = !scaledCols.TryGetValue(col, out marker);
                if (first) scaledCols.Add(col, new object());
                int baseW = first ? S(col.Width) : col.Width;
                col.Width = Math.Max(baseW, need);
            }
        }

        // 按钮文字动态改长后确保不截断
        public static void FitButton(Button b){
            if (b == null) return;
            int need = TextRenderer.MeasureText(b.Text ?? "", b.Font ?? F9).Width + S(26);
            if (b.Width < need) b.Width = need;
        }

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
            if (r.Width <= 0 || r.Height <= 0) return;
            var b = CachedBrush(fill);
            if (radius <= 0) { g.FillRectangle(b, r); return; }
            var sm = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Rounded(r, radius)) g.FillPath(b, path);
            g.SmoothingMode = sm;
        }
        public static void DrawRound(Graphics g, Rectangle r, int radius, Color line, float width){
            if (r.Width <= 0 || r.Height <= 0) return;
            var p = CachedPen(line, width);
            if (radius <= 0) { g.DrawRectangle(p, r); return; }
            var sm = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Rounded(r, radius)) g.DrawPath(p, path);
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

        // 窗口外壳：白底、统一字体、按当前 DPI 放大后再收进屏幕工作区、居中到 CAD 主窗口、Esc 关闭。
        public static void Shell(Form f, string title, int w, int h, int minW, int minH){
            f.AutoScaleMode = AutoScaleMode.None;
            f.Text = title;
            f.Font = F9;
            f.BackColor = Bg;
            f.ForeColor = Text;
            w = S(w); h = S(h); minW = S(minW); minH = S(minH);
            // 小屏幕（1366×768 的笔记本，任务栏一占只剩 728 高）：原来窗口固定 790 高、最小 680，底部「开始打孔」那一栏落在屏幕外，
            // 最小尺寸又不让缩。按 CAD 所在屏幕的工作区收一收（留出标题栏和边框），最小尺寸也不超过它
            Rectangle area;
            try { area = Screen.FromPoint(Cursor.Position).WorkingArea; } catch { area = new Rectangle(0, 0, 1920, 1040); }
            int chromeW = S(16), chromeH = S(40);
            int maxW = Math.Max(S(400), area.Width - chromeW - S(20)), maxH = Math.Max(S(300), area.Height - chromeH - S(20));
            f.ClientSize = new Size(Math.Min(w, maxW), Math.Min(h, maxH));
            f.MinimumSize = new Size(Math.Min(minW, maxW + chromeW), Math.Min(minH, maxH + chromeH));
            f.FormBorderStyle = FormBorderStyle.SizableToolWindow;
            f.StartPosition = FormStartPosition.CenterParent;
            f.ShowInTaskbar = false;
            f.KeyPreview = true;
            UiSkin.Attach(f);
        }

        // 手动对非 Ui.Shell 创建的弹出面板/子容器应用统一控件外观
        public static void StyleTree(Control root){ UiSkin.Attach(root); }

        public static Card Card(string title, int height, string hint){
            var c = new Card();
            c.Title = title; c.Hint = hint;
            c.Height = height > 0 ? S(height) : 0;
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
            n.Width = S(width + 8); n.TextAlign = HorizontalAlignment.Right;
            n.BorderStyle = BorderStyle.FixedSingle;
            n.Font = NumF;
            return n;
        }
        public static decimal Clamp(decimal v, decimal min, decimal max){ return v < min ? min : (v > max ? max : v); }

        public static ComboBox Combo(int width, params string[] items){
            var c = new ComboBox();
            c.DropDownStyle = ComboBoxStyle.DropDownList;
            c.Width = S(width + 8);
            c.Items.AddRange(items);
            if (c.Items.Count > 0) c.SelectedIndex = 0;
            return c;
        }

        public static Button Primary(string text, int width, int height){
            int sh = S(height);
            var f = sh >= S(40) ? BtnB : F9B;
            int sw = Math.Max(S(width), TextRenderer.MeasureText(text ?? "", f).Width + S(28));
            return new FlatButton(text, FlatButton.Kind.Primary, sw, sh);
        }
        public static Button Secondary(string text, int width, int height){
            int sh = S(height);
            var f = sh >= S(36) ? Btn : F9;
            int sw = Math.Max(S(width), TextRenderer.MeasureText(text ?? "", f).Width + S(26));
            return new FlatButton(text, FlatButton.Kind.Secondary, sw, sh);
        }
        // 文字按钮（「配做规则…」「清空」这类，不抢主按钮的眼）
        public static Button Ghost(string text){
            int w = TextRenderer.MeasureText(text ?? "", F9).Width + S(22);
            return new FlatButton(text, FlatButton.Kind.Ghost, w, S(30));
        }
        // 卡片标题行上的小按钮：宽度按文字算，不用手填
        public static Button Small(string text){
            int w = TextRenderer.MeasureText(text ?? "", F8).Width + S(20);
            var b = new FlatButton(text, FlatButton.Kind.Ghost, w, S(26));
            b.Font = F8;
            return b;
        }

        // 一行文字输入的小对话框（WinForms 没有现成的 InputBox；VB 的那个要多引一个程序集）。取消返回 null。
        public static string Prompt(IWin32Window owner, string title, string label, string initial){
            using (var f = new Form()) {
                Shell(f, title, 400, 150, 320, 150);
                f.FormBorderStyle = FormBorderStyle.FixedToolWindow;
                var l = new Label { Text = label, AutoSize = false, Bounds = new Rectangle(S(18), S(14), S(364), S(24)), ForeColor = Text2, BackColor = Color.Transparent };
                var t = new TextBox { Text = initial ?? "", Bounds = new Rectangle(S(18), S(42), S(364), S(28)), BorderStyle = BorderStyle.FixedSingle };
                var ok = Primary("确定", 88, 36); ok.Location = new Point(f.ClientSize.Width - S(18) - ok.Width, S(96)); ok.DialogResult = DialogResult.OK;
                var cancel = Secondary("取消", 80, 34); cancel.Location = new Point(ok.Left - S(10) - cancel.Width, S(97)); cancel.DialogResult = DialogResult.Cancel;
                f.Controls.Add(l); f.Controls.Add(t); f.Controls.Add(ok); f.Controls.Add(cancel);
                f.AcceptButton = ok; f.CancelButton = cancel;
                f.Shown += (s, e) => { t.SelectAll(); t.Focus(); };
                return f.ShowDialog(owner) == DialogResult.OK ? t.Text : null;
            }
        }

        // 步骤标题：编号圆点 + 加粗标题 + 灰色说明。
        public static StepHeader Step(int number, string title, string hint){
            return new StepHeader { Number = number, Title = title, Hint = hint, Height = S(28) };
        }
    }
}
