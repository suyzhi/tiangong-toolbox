using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TianGongCadSuite {
    // 全插件统一的现代控件外观引擎（与 UiTheme 的白底 + 浅灰 Rail + 靛蓝 Accent 语言保持完全一致）：
    // 自动接管窗体树中所有原生输入框（TextBox / NumericUpDown）、复选框（CheckBox）、单选框（RadioButton）
    // 与下拉框（ComboBox），零侵入保留原有控件类型、事件、布局坐标、键盘/IME 与 DataGridView 编辑器兼容性。
    public static class UiSkin {
        static readonly Color BorderNormal   = Color.FromArgb(0xD4, 0xD4, 0xD8); // Zinc-300（在白底与浅灰底上都清晰柔和）
        static readonly Color BorderHover    = Color.FromArgb(0xA1, 0xA1, 0xAA); // Zinc-400
        static readonly Color BorderDisabled = Ui.Line;                          // Zinc-200
        static readonly ConditionalWeakTable<Control, object> attached = new ConditionalWeakTable<Control, object>();
        static readonly ConditionalWeakTable<Control, object> hookedContainers = new ConditionalWeakTable<Control, object>();
        static readonly ConditionalWeakTable<ToolStripDropDown, MenuSkin> styledMenus = new ConditionalWeakTable<ToolStripDropDown, MenuSkin>();
        static readonly MenuRenderer sharedMenuRenderer = new MenuRenderer();

        [DllImport("dwmapi.dll", PreserveSig = true)] static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);
        const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        const int DWMWA_BORDER_COLOR = 34;
        const int DWMWCP_ROUND = 2;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")] static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")] static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
        [DllImport("user32.dll")] static extern IntPtr GetWindowDC(IntPtr hWnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")] static extern int MapWindowPoints(IntPtr hWndFrom, IntPtr hWndTo, ref RECT lpPoints, uint cPoints);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
        [DllImport("user32.dll")] static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);
        [DllImport("user32.dll")] static extern IntPtr BeginPaint(IntPtr hWnd, ref PAINTSTRUCT lpPaint);
        [DllImport("user32.dll")] static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT lpPaint);
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool GetComboBoxInfo(IntPtr hwndCombo, ref COMBOBOXINFO pcbi);
        [DllImport("gdi32.dll")]  static extern int SaveDC(IntPtr hdc);
        [DllImport("gdi32.dll")]  static extern bool RestoreDC(IntPtr hdc, int nSavedDC);
        [DllImport("gdi32.dll")]  static extern int ExcludeClipRect(IntPtr hdc, int nLeftRect, int nTopRect, int nRightRect, int nBottomRect);
        [DllImport("gdi32.dll")]  static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")]  static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")]  static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);
        [DllImport("gdi32.dll")]  static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);
        [DllImport("gdi32.dll")]  static extern bool DeleteObject(IntPtr hObject);
        [DllImport("gdi32.dll")]  static extern bool BitBlt(IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight, IntPtr hdcSrc, int nXSrc, int nYSrc, int dwRop);

        const int GWL_STYLE = -16;
        const int GWL_EXSTYLE = -20;
        const long WS_BORDER = 0x00800000L;
        const long WS_EX_CLIENTEDGE = 0x00000200L;
        const int WM_PAINT = 0x000F;
        const int WM_ERASEBKGND = 0x0014;
        const int WM_NCCALCSIZE = 0x0083;
        const int WM_NCHITTEST = 0x0084;
        const int WM_NCPAINT = 0x0085;
        const int WM_PRINT = 0x0317;
        const int WM_PRINTCLIENT = 0x0318;
        const int CB_SETITEMHEIGHT = 0x0153;
        const int HTCLIENT = 1;
        const int SRCCOPY = 0x00CC0020;
        const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_FRAMECHANGED = 0x0020;
        const uint RDW_INVALIDATE = 0x0001, RDW_ERASE = 0x0004, RDW_FRAME = 0x0400, RDW_UPDATENOW = 0x0100;

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        struct PAINTSTRUCT {
            public IntPtr hdc;
            public int fErase;
            public RECT rcPaint;
            public int fRestore;
            public int fIncUpdate;
            public int r0, r1, r2, r3, r4, r5, r6, r7;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct COMBOBOXINFO {
            public int cbSize;
            public RECT rcItem;
            public RECT rcButton;
            public int stateButton;
            public IntPtr hwndCombo;
            public IntPtr hwndEdit;
            public IntPtr hwndList;
        }

        public static void Attach(Control root) {
            if (root == null || root.IsDisposed) return;
            StyleNode(root);
        }

        // 将右键菜单 / 下拉菜单统一为全插件的现代白底圆角靛蓝外观（与下拉框/卡片/按钮保持一致）
        public static void StyleMenu(ToolStripDropDown menu) {
            if (menu == null || menu.IsDisposed) return;
            MenuSkin existing;
            if (styledMenus.TryGetValue(menu, out existing)) {
                existing.RefreshItems();
                return;
            }
            var skin = new MenuSkin(menu);
            styledMenus.Add(menu, skin);
            skin.RefreshItems();
        }

        static void StyleNode(Control c) {
            if (c == null || c.IsDisposed) return;
            if (c.ContextMenuStrip != null) StyleMenu(c.ContextMenuStrip);
            // 不碰 DataGridView 及其内部编辑控件，不碰 UpDownBase 的内部子控件（由 NumericUpDownSkin 接管）
            if (c is DataGridView) {
                object dgMarker;
                if (!hookedContainers.TryGetValue(c, out dgMarker)) {
                    hookedContainers.Add(c, new object());
                    c.ContextMenuStripChanged += OnContextMenuChanged;
                }
                return;
            }
            if (c is IDataGridViewEditingControl) return;

            ApplyToControl(c);

            if (c is UpDownBase || c is TextBox || c is ComboBox || c is CheckBox || c is RadioButton) return;

            object dummy;
            if (!hookedContainers.TryGetValue(c, out dummy)) {
                hookedContainers.Add(c, new object());
                c.ControlAdded += OnChildAdded;
                c.ContextMenuStripChanged += OnContextMenuChanged;
            }
            foreach (Control child in c.Controls) StyleNode(child);
        }

        static void OnChildAdded(object sender, ControlEventArgs e) {
            if (e != null && e.Control != null) StyleNode(e.Control);
        }

        static void OnContextMenuChanged(object sender, EventArgs e) {
            var c = sender as Control;
            if (c != null && !c.IsDisposed && c.ContextMenuStrip != null) StyleMenu(c.ContextMenuStrip);
        }

        static void ApplyToControl(Control c) {
            object dummy;
            if (attached.TryGetValue(c, out dummy)) return;

            var cb = c as CheckBox;
            if (cb != null) {
                if (cb.Appearance == Appearance.Normal) {
                    attached.Add(c, new CheckBoxSkin(cb));
                }
                return;
            }
            var rb = c as RadioButton;
            if (rb != null) {
                if (rb.Appearance == Appearance.Normal) {
                    attached.Add(c, new RadioButtonSkin(rb));
                }
                return;
            }
            var combo = c as ComboBox;
            if (combo != null) {
                if (combo.DropDownStyle != ComboBoxStyle.Simple) {
                    attached.Add(c, new ComboBoxSkin(combo));
                }
                return;
            }
            var nud = c as NumericUpDown;
            if (nud != null) {
                attached.Add(c, new NumericUpDownSkin(nud));
                return;
            }
            var tb = c as TextBox;
            if (tb != null) {
                if (tb.BorderStyle != BorderStyle.None && !(tb.Parent is UpDownBase)) {
                    attached.Add(c, new TextBoxSkin(tb));
                }
            }
        }

        static Color ResolveBack(Control c) {
            for (Control cur = c; cur != null; cur = cur.Parent) {
                if (cur.BackColor != Color.Transparent && cur.BackColor.A == 255) return cur.BackColor;
            }
            return Ui.Bg;
        }

        static Color ResolveParentBack(Control c) {
            if (c != null && c.Parent != null) return ResolveBack(c.Parent);
            return Ui.Bg;
        }

        static GraphicsPath RoundedF(RectangleF r, float radius) {
            var p = new GraphicsPath();
            float d = radius * 2f;
            if (d <= 0.1f || d > r.Width || d > r.Height) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180f, 90f);
            p.AddArc(r.Right - d, r.Y, d, d, 270f, 90f);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0f, 90f);
            p.AddArc(r.X, r.Bottom - d, d, d, 90f, 90f);
            p.CloseFigure();
            return p;
        }

        static void StripNativeBorder(IntPtr hwnd) {
            if (hwnd == IntPtr.Zero) return;
            try {
                long st = GetWindowLongPtr64(hwnd, GWL_STYLE).ToInt64();
                if ((st & WS_BORDER) != 0) SetWindowLongPtr64(hwnd, GWL_STYLE, new IntPtr(st & ~WS_BORDER));
                long ex = GetWindowLongPtr64(hwnd, GWL_EXSTYLE).ToInt64();
                if ((ex & WS_EX_CLIENTEDGE) != 0) SetWindowLongPtr64(hwnd, GWL_EXSTYLE, new IntPtr(ex & ~WS_EX_CLIENTEDGE));
            } catch { }
        }

        static void DrawRoundedBorder(Graphics g, int w, int h, float radius, Color parentBack, Color innerFill, Color borderColor, float borderWidth) {
            if (w <= 2 || h <= 2) return;
            var sm = g.SmoothingMode;
            // 先在非抗锯齿模式下整块铺满父底色，避免 CreateCompatibleBitmap(初值全黑) 与 AntiAlias FillRectangle 在 x=0/y=0 半像素混出灰黑边；
            // 同时绝不使用 g.Clear，防止在 WM_PRINT 共享 HDC 上误清父窗体区域。
            g.SmoothingMode = SmoothingMode.None;
            g.FillRectangle(Ui.CachedBrush(parentBack), 0, 0, w, h);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float inset = borderWidth > 1.1f ? 0.75f : 0.5f;
            var rf = new RectangleF(inset, inset, Math.Max(1f, w - 1f - inset), Math.Max(1f, h - 1f - inset));
            using (var path = RoundedF(rf, Math.Max(1f, radius - inset))) {
                g.FillPath(Ui.CachedBrush(innerFill), path);
                g.DrawPath(Ui.CachedPen(borderColor, borderWidth), path);
            }
            g.SmoothingMode = sm;
        }

        static void DrawChevron(Graphics g, float cx, float cy, float halfW, float halfH, bool up, Color color, float stroke) {
            var sm = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(color, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round }) {
                if (up) {
                    g.DrawLines(pen, new[] {
                        new PointF(cx - halfW, cy + halfH * 0.55f),
                        new PointF(cx,         cy - halfH * 0.65f),
                        new PointF(cx + halfW, cy + halfH * 0.55f)
                    });
                } else {
                    g.DrawLines(pen, new[] {
                        new PointF(cx - halfW, cy - halfH * 0.55f),
                        new PointF(cx,         cy + halfH * 0.65f),
                        new PointF(cx + halfW, cy - halfH * 0.55f)
                    });
                }
            }
            g.SmoothingMode = sm;
        }

        static void RepaintFrame(Control c) {
            if (c == null || c.IsDisposed || !c.IsHandleCreated) return;
            try { RedrawWindow(c.Handle, IntPtr.Zero, IntPtr.Zero, RDW_FRAME | RDW_INVALIDATE); } catch { }
        }

        // ==================== 1. 复选框 CheckBox ====================
        sealed class CheckBoxSkin {
            readonly CheckBox cb;
            bool hover, down;

            public CheckBoxSkin(CheckBox checkBox) {
                cb = checkBox;
                Ui.EnableDoubleBuffer(cb);
                cb.Cursor = Cursors.Hand;
                if (cb.AutoSize) {
                    cb.Padding = new Padding(cb.Padding.Left, cb.Padding.Top, Math.Max(cb.Padding.Right, Ui.S(5)), cb.Padding.Bottom);
                }
                cb.MouseEnter += OnEnter;
                cb.MouseLeave += OnLeave;
                cb.MouseDown += OnDown;
                cb.MouseUp += OnUp;
                cb.GotFocus += OnState;
                cb.LostFocus += OnState;
                cb.CheckedChanged += OnState;
                cb.CheckStateChanged += OnState;
                cb.EnabledChanged += OnState;
                cb.Paint += OnPaint;
            }

            void OnEnter(object s, EventArgs e) { hover = true; cb.Invalidate(); }
            void OnLeave(object s, EventArgs e) { hover = false; down = false; cb.Invalidate(); }
            void OnDown(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) { down = true; cb.Invalidate(); } }
            void OnUp(object s, MouseEventArgs e) { down = false; cb.Invalidate(); }
            void OnState(object s, EventArgs e) { cb.Invalidate(); }

            void OnPaint(object s, PaintEventArgs e) {
                var g = e.Graphics;
                g.Clear(ResolveBack(cb));

                int bs = cb.Font.Size <= 9.1f ? Ui.S(15) : Ui.S(16);
                int bx = Ui.S(1);
                bool topAlign = cb.CheckAlign == ContentAlignment.TopLeft || cb.CheckAlign == ContentAlignment.TopCenter || cb.CheckAlign == ContentAlignment.TopRight;
                int by = topAlign ? Ui.S(2) : Math.Max(Ui.S(1), (cb.Height - bs) / 2);
                var box = new Rectangle(bx, by, bs, bs);
                int rad = Ui.S(4);

                if (cb.Focused && cb.Enabled) {
                    Ui.DrawRound(g, Rectangle.Inflate(box, 1, 1), rad + 1, Ui.AccentRing, 2f);
                }

                if (cb.CheckState == CheckState.Checked) {
                    Color fill = !cb.Enabled ? Ui.Disabled : (down ? Ui.AccentPress : (hover ? Ui.AccentD : Ui.Accent));
                    Ui.FillRound(g, box, rad, fill);
                    int pad = Math.Max(2, Ui.S(2));
                    Ui.DrawCheck(g, new Rectangle(box.X + pad, box.Y + pad, box.Width - pad * 2, box.Height - pad * 2),
                        cb.Enabled ? Color.White : Ui.DisabledText, 1.8f);
                } else if (cb.CheckState == CheckState.Indeterminate) {
                    Color fill = !cb.Enabled ? Ui.Disabled : (down ? Ui.AccentPress : (hover ? Ui.AccentD : Ui.Accent));
                    Ui.FillRound(g, box, rad, fill);
                    int my = box.Y + box.Height / 2;
                    using (var pen = new Pen(cb.Enabled ? Color.White : Ui.DisabledText, 1.9f) { StartCap = LineCap.Round, EndCap = LineCap.Round }) {
                        g.DrawLine(pen, box.X + Ui.S(4), my, box.Right - Ui.S(4), my);
                    }
                } else {
                    Color fill = !cb.Enabled ? Ui.Rail : (down ? Ui.Line : (hover ? Ui.Subtle : Color.White));
                    Color line = !cb.Enabled ? BorderDisabled : (hover || cb.Focused ? Ui.Accent : BorderNormal);
                    Ui.FillRound(g, box, rad, fill);
                    Ui.DrawRound(g, box, rad, line, hover || cb.Focused ? 1.25f : 1f);
                }

                string text = cb.Text;
                if (!string.IsNullOrEmpty(text)) {
                    int tx = box.Right + Ui.S(6);
                    var tr = new Rectangle(tx, topAlign ? Ui.S(1) : 0, Math.Max(0, cb.Width - tx), Math.Max(1, cb.Height - (topAlign ? Ui.S(1) : 0)));
                    var flags = TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
                    if (topAlign) {
                        flags |= TextFormatFlags.Top | TextFormatFlags.WordBreak;
                    } else {
                        int lineH = TextRenderer.MeasureText("测", cb.Font).Height;
                        if (!cb.AutoSize && cb.Height >= lineH * 2) flags |= TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak;
                        else flags |= TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine;
                    }
                    if (cb.AutoEllipsis) flags |= TextFormatFlags.EndEllipsis;
                    Color fore = !cb.Enabled ? Ui.Faint : (cb.ForeColor == SystemColors.ControlText ? Ui.Text : cb.ForeColor);
                    TextRenderer.DrawText(g, text, cb.Font, tr, fore, flags);
                }
            }
        }

        // ==================== 2. 单选框 RadioButton ====================
        sealed class RadioButtonSkin {
            readonly RadioButton rb;
            bool hover, down;

            public RadioButtonSkin(RadioButton radio) {
                rb = radio;
                Ui.EnableDoubleBuffer(rb);
                rb.Cursor = Cursors.Hand;
                if (rb.AutoSize) {
                    rb.Padding = new Padding(rb.Padding.Left, rb.Padding.Top, Math.Max(rb.Padding.Right, Ui.S(5)), rb.Padding.Bottom);
                }
                rb.MouseEnter += OnEnter;
                rb.MouseLeave += OnLeave;
                rb.MouseDown += OnDown;
                rb.MouseUp += OnUp;
                rb.GotFocus += OnState;
                rb.LostFocus += OnState;
                rb.CheckedChanged += OnState;
                rb.EnabledChanged += OnState;
                rb.Paint += OnPaint;
            }

            void OnEnter(object s, EventArgs e) { hover = true; rb.Invalidate(); }
            void OnLeave(object s, EventArgs e) { hover = false; down = false; rb.Invalidate(); }
            void OnDown(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) { down = true; rb.Invalidate(); } }
            void OnUp(object s, MouseEventArgs e) { down = false; rb.Invalidate(); }
            void OnState(object s, EventArgs e) { rb.Invalidate(); }

            void OnPaint(object s, PaintEventArgs e) {
                var g = e.Graphics;
                g.Clear(ResolveBack(rb));

                int ds = rb.Font.Size <= 9.1f ? Ui.S(15) : Ui.S(16);
                int bx = Ui.S(1);
                int by = Math.Max(Ui.S(1), (rb.Height - ds) / 2);
                var circle = new RectangleF(bx, by, ds - 1f, ds - 1f);

                var sm = g.SmoothingMode;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                if (rb.Focused && rb.Enabled) {
                    var halo = RectangleF.Inflate(circle, 1.5f, 1.5f);
                    g.DrawEllipse(Ui.CachedPen(Ui.AccentRing, 2f), halo);
                }
                if (rb.Checked) {
                    Color fill = !rb.Enabled ? Ui.Disabled : (down ? Ui.AccentPress : (hover ? Ui.AccentD : Ui.Accent));
                    g.FillEllipse(Ui.CachedBrush(fill), circle);
                    float innerD = Math.Max(4f, ds * 0.40f);
                    float ix = circle.X + (circle.Width - innerD) / 2f;
                    float iy = circle.Y + (circle.Height - innerD) / 2f;
                    g.FillEllipse(Ui.CachedBrush(rb.Enabled ? Color.White : Ui.DisabledText), ix, iy, innerD, innerD);
                } else {
                    Color fill = !rb.Enabled ? Ui.Rail : (down ? Ui.Line : (hover ? Ui.Subtle : Color.White));
                    Color line = !rb.Enabled ? BorderDisabled : (hover || rb.Focused ? Ui.Accent : BorderNormal);
                    g.FillEllipse(Ui.CachedBrush(fill), circle);
                    g.DrawEllipse(Ui.CachedPen(line, hover || rb.Focused ? 1.25f : 1f), circle);
                }
                g.SmoothingMode = sm;

                string text = rb.Text;
                if (!string.IsNullOrEmpty(text)) {
                    int tx = bx + ds + Ui.S(6);
                    var tr = new Rectangle(tx, 0, Math.Max(0, rb.Width - tx), rb.Height);
                    var flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
                    if (rb.AutoEllipsis) flags |= TextFormatFlags.EndEllipsis;
                    Color fore = !rb.Enabled ? Ui.Faint : (rb.ForeColor == SystemColors.ControlText ? Ui.Text : rb.ForeColor);
                    TextRenderer.DrawText(g, text, rb.Font, tr, fore, flags);
                }
            }
        }

        // ==================== 3. 输入框 TextBox ====================
        sealed class TextBoxSkin : NativeWindow {
            readonly TextBox tb;
            bool hover;
            bool adjusting;

            public TextBoxSkin(TextBox textBox) {
                tb = textBox;
                NormalizeBorder();
                if (tb.ReadOnly && tb.BackColor == SystemColors.Control) tb.BackColor = Ui.Rail;
                tb.HandleCreated += OnHandleCreated;
                tb.HandleDestroyed += OnHandleDestroyed;
                tb.GotFocus += OnRepaint;
                tb.LostFocus += OnRepaint;
                tb.MouseEnter += OnEnter;
                tb.MouseLeave += OnLeave;
                tb.EnabledChanged += OnRepaint;
                tb.ReadOnlyChanged += OnReadOnlyChanged;
                tb.FontChanged += OnFrameChanged;
                tb.SizeChanged += OnSizeChanged;
                if (tb.IsHandleCreated) AttachHandle();
            }

            void NormalizeBorder() {
                if (adjusting || tb.IsDisposed) return;
                adjusting = true;
                try {
                    int wantH = tb.Multiline ? tb.Height : Math.Max(tb.Height, Math.Max(Ui.S(26), tb.Font.Height + Ui.S(8)));
                    if (!tb.Multiline && tb.AutoSize) tb.AutoSize = false;
                    // 改为 BorderStyle.None 彻底移除 Win32 EDIT 在客户端区画的 1px 纯黑内框；外框与内边距由 WM_NCCALCSIZE + WM_NCPAINT 接管
                    if (tb.BorderStyle != BorderStyle.None) tb.BorderStyle = BorderStyle.None;
                    if (!tb.Multiline && tb.Dock == DockStyle.None && tb.Height != wantH) tb.Height = wantH;
                } finally { adjusting = false; }
            }

            void AttachHandle() {
                NormalizeBorder();
                if (!tb.IsHandleCreated) return;
                if (Handle != IntPtr.Zero) ReleaseHandle();
                AssignHandle(tb.Handle);
                StripNativeBorder(tb.Handle);
                SetWindowPos(tb.Handle, IntPtr.Zero, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
            }

            void OnHandleCreated(object s, EventArgs e) { AttachHandle(); }
            void OnHandleDestroyed(object s, EventArgs e) { if (Handle != IntPtr.Zero) ReleaseHandle(); }
            void OnEnter(object s, EventArgs e) { hover = true; RepaintFrame(tb); }
            void OnLeave(object s, EventArgs e) { hover = false; RepaintFrame(tb); }
            void OnRepaint(object s, EventArgs e) { RepaintFrame(tb); }
            void OnReadOnlyChanged(object s, EventArgs e) {
                if (tb.ReadOnly && tb.BackColor == SystemColors.Control) tb.BackColor = Ui.Rail;
                else if (!tb.ReadOnly && tb.BackColor == Ui.Rail) tb.BackColor = Color.White;
                RepaintFrame(tb);
            }
            void OnFrameChanged(object s, EventArgs e) {
                NormalizeBorder();
                if (tb.IsHandleCreated) SetWindowPos(tb.Handle, IntPtr.Zero, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
            }
            void OnSizeChanged(object s, EventArgs e) { RepaintFrame(tb); }

            Color InnerFillColor() {
                if (!tb.Enabled) return SystemColors.Control;
                if (tb.BackColor == SystemColors.Window) return Color.White;
                return tb.BackColor;
            }

            Color BorderColor(out float width) {
                if (!tb.Enabled) { width = 1f; return BorderDisabled; }
                if (tb.Focused && !tb.ReadOnly) { width = 1.5f; return Ui.Accent; }
                if (hover && !tb.ReadOnly) { width = 1f; return BorderHover; }
                width = 1f;
                return BorderNormal;
            }

            void PaintNc(IntPtr hdc) {
                if (hdc == IntPtr.Zero) return;
                RECT wr, cr;
                if (!GetWindowRect(Handle, out wr) || !GetClientRect(Handle, out cr)) return;
                int w = wr.Right - wr.Left, h = wr.Bottom - wr.Top;
                if (w <= 2 || h <= 2) return;

                RECT crWin = cr;
                MapWindowPoints(Handle, IntPtr.Zero, ref crWin, 2);
                int cx = crWin.Left - wr.Left;
                int cy = crWin.Top - wr.Top;
                int cw = crWin.Right - crWin.Left;
                int ch = crWin.Bottom - crWin.Top;

                int saved = SaveDC(hdc);
                try {
                    bool hasScroll = tb.Multiline && tb.ScrollBars != ScrollBars.None;
                    if (cw > 0 && ch > 0) ExcludeClipRect(hdc, cx, cy, cx + cw, cy + ch);
                    if (hasScroll) {
                        int sbW = SystemInformation.VerticalScrollBarWidth;
                        int sbH = SystemInformation.HorizontalScrollBarHeight;
                        if (tb.ScrollBars == ScrollBars.Vertical || tb.ScrollBars == ScrollBars.Both)
                            ExcludeClipRect(hdc, Math.Max(cx + cw, w - sbW - 2), 2, w - 2, h - 2);
                        if (tb.ScrollBars == ScrollBars.Horizontal || tb.ScrollBars == ScrollBars.Both)
                            ExcludeClipRect(hdc, 2, Math.Max(cy + ch, h - sbH - 2), w - 2, h - 2);
                    }

                    float bw;
                    Color border = BorderColor(out bw);
                    using (var g = Graphics.FromHdc(hdc)) {
                        DrawRoundedBorder(g, w, h, Ui.S(6), ResolveParentBack(tb), InnerFillColor(), border, bw);
                    }
                } finally {
                    if (saved != 0) RestoreDC(hdc, saved);
                }
            }

            protected override void WndProc(ref Message m) {
                if (m.Msg == WM_NCCALCSIZE) {
                    if (tb.Multiline) {
                        base.WndProc(ref m);
                        var r = (RECT)Marshal.PtrToStructure(m.LParam, typeof(RECT));
                        int padX = Ui.S(5), padY = Ui.S(4);
                        int padR = tb.ScrollBars == ScrollBars.Vertical || tb.ScrollBars == ScrollBars.Both ? 1 : padX;
                        if (r.Right - r.Left > padX + padR + 8) { r.Left += padX; r.Right -= padR; }
                        if (r.Bottom - r.Top > padY * 2 + 8) { r.Top += padY; r.Bottom -= padY; }
                        Marshal.StructureToPtr(r, m.LParam, false);
                        return;
                    } else {
                        var r = (RECT)Marshal.PtrToStructure(m.LParam, typeof(RECT));
                        int w = r.Right - r.Left, h = r.Bottom - r.Top;
                        int textH = Math.Max(tb.Font.Height + 1, 12);
                        int padX = w > Ui.S(48) ? Ui.S(7) : Ui.S(3);
                        int availV = h - textH;
                        int padTop = availV >= 4 ? availV / 2 : Math.Max(1, availV / 2);
                        int padBot = availV >= 4 ? (availV - padTop) : Math.Max(1, availV - padTop);
                        if (w > padX * 2 + 6) { r.Left += padX; r.Right -= padX; }
                        if (h > padTop + padBot + 4) { r.Top += padTop; r.Bottom -= padBot; }
                        Marshal.StructureToPtr(r, m.LParam, false);
                        m.Result = IntPtr.Zero;
                        return;
                    }
                }
                if (m.Msg == WM_NCHITTEST && !tb.Multiline) {
                    m.Result = (IntPtr)HTCLIENT;
                    return;
                }
                if (m.Msg == WM_NCPAINT) {
                    if (tb.Multiline && tb.ScrollBars != ScrollBars.None) base.WndProc(ref m);
                    IntPtr hdc = GetWindowDC(Handle);
                    if (hdc != IntPtr.Zero) {
                        try { PaintNc(hdc); } finally { ReleaseDC(Handle, hdc); }
                    }
                    m.Result = IntPtr.Zero;
                    return;
                }
                if (m.Msg == WM_PRINT && m.WParam != IntPtr.Zero) {
                    base.WndProc(ref m);
                    PaintNc(m.WParam);
                    return;
                }
                base.WndProc(ref m);
            }
        }

        // ==================== 4. 数字微调框 NumericUpDown ====================
        sealed class NumericUpDownSkin : NativeWindow {
            readonly NumericUpDown nud;
            SpinnerSkin spinner;
            bool hover;

            public NumericUpDownSkin(NumericUpDown numeric) {
                nud = numeric;
                if (nud.BorderStyle == BorderStyle.Fixed3D) nud.BorderStyle = BorderStyle.FixedSingle;
                nud.HandleCreated += OnHandleCreated;
                nud.HandleDestroyed += OnHandleDestroyed;
                nud.Enter += OnFocusChanged;
                nud.Leave += OnFocusChanged;
                nud.MouseEnter += OnEnter;
                nud.MouseLeave += OnLeave;
                nud.EnabledChanged += OnFocusChanged;
                nud.SizeChanged += OnFocusChanged;

                // 挂接内部按钮与内部文本框的悬停/焦点事件
                if (nud.Controls.Count >= 1) {
                    spinner = new SpinnerSkin(nud, nud.Controls[0], OnChildHover);
                }
                if (nud.Controls.Count >= 2) {
                    var edit = nud.Controls[1];
                    edit.MouseEnter += OnEnter;
                    edit.MouseLeave += OnLeave;
                    edit.GotFocus += OnFocusChanged;
                    edit.LostFocus += OnFocusChanged;
                }
                if (nud.IsHandleCreated) AttachHandle();
            }

            void AttachHandle() {
                if (Handle != IntPtr.Zero) ReleaseHandle();
                AssignHandle(nud.Handle);
                StripNativeBorder(nud.Handle);
                SetWindowPos(nud.Handle, IntPtr.Zero, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
            }

            void OnHandleCreated(object s, EventArgs e) { AttachHandle(); }
            void OnHandleDestroyed(object s, EventArgs e) { if (Handle != IntPtr.Zero) ReleaseHandle(); }
            void OnChildHover(bool h) { if (hover != h) { hover = h; RepaintFrame(nud); } }
            void OnEnter(object s, EventArgs e) { hover = true; RepaintFrame(nud); }
            void OnLeave(object s, EventArgs e) {
                if (!nud.IsDisposed && nud.IsHandleCreated) {
                    var pt = nud.PointToClient(Control.MousePosition);
                    if (nud.ClientRectangle.Contains(pt)) return;
                }
                hover = false;
                RepaintFrame(nud);
            }
            void OnFocusChanged(object s, EventArgs e) {
                RepaintFrame(nud);
                if (nud.Controls.Count >= 1) nud.Controls[0].Invalidate();
            }

            bool IsFocused() {
                return nud.ContainsFocus || nud.Focused;
            }

            void PaintNc(IntPtr hdc) {
                if (hdc == IntPtr.Zero) return;
                RECT wr, cr;
                if (!GetWindowRect(Handle, out wr) || !GetClientRect(Handle, out cr)) return;
                int w = wr.Right - wr.Left, h = wr.Bottom - wr.Top;
                if (w <= 2 || h <= 2) return;

                RECT crWin = cr;
                MapWindowPoints(Handle, IntPtr.Zero, ref crWin, 2);
                int cx = crWin.Left - wr.Left;
                int cy = crWin.Top - wr.Top;
                int cw = crWin.Right - crWin.Left;
                int ch = crWin.Bottom - crWin.Top;
                int saved = SaveDC(hdc);
                try {
                    if (cw > 0 && ch > 0) ExcludeClipRect(hdc, cx, cy, cx + cw, cy + ch);

                    bool focused = IsFocused();
                    float bw = focused && nud.Enabled ? 1.5f : 1f;
                    Color border = !nud.Enabled ? BorderDisabled : (focused ? Ui.Accent : (hover ? BorderHover : BorderNormal));
                    Color fill = !nud.Enabled ? SystemColors.Control : (nud.BackColor == SystemColors.Window ? Color.White : nud.BackColor);
                    using (var g = Graphics.FromHdc(hdc)) {
                        DrawRoundedBorder(g, w, h, Ui.S(6), ResolveParentBack(nud), fill, border, bw);
                    }
                } finally {
                    if (saved != 0) RestoreDC(hdc, saved);
                }
            }

            protected override void WndProc(ref Message m) {
                if (m.Msg == WM_NCCALCSIZE) {
                    var r = (RECT)Marshal.PtrToStructure(m.LParam, typeof(RECT));
                    int w = r.Right - r.Left, h = r.Bottom - r.Top;
                    int padL = Ui.S(3), padR = 2, padV = 2;
                    if (w > padL + padR + 18) { r.Left += padL; r.Right -= padR; }
                    if (h > padV * 2 + 10) { r.Top += padV; r.Bottom -= padV; }
                    Marshal.StructureToPtr(r, m.LParam, false);
                    m.Result = IntPtr.Zero;
                    return;
                }
                if (m.Msg == WM_ERASEBKGND) {
                    m.Result = (IntPtr)1;
                    return;
                }
                if (m.Msg == WM_PAINT) {
                    // 屏蔽 UpDownBase.OnPaint 用 VisualStyleRenderer 在 ClientRectangle 里画的那圈默认直角灰框
                    var ps = new PAINTSTRUCT();
                    IntPtr hdc = BeginPaint(Handle, ref ps);
                    if (hdc != IntPtr.Zero) {
                        try {
                            Color fill = !nud.Enabled ? SystemColors.Control : (nud.BackColor == SystemColors.Window ? Color.White : nud.BackColor);
                            var cr = nud.ClientRectangle;
                            using (var g = Graphics.FromHdc(hdc)) g.FillRectangle(Ui.CachedBrush(fill), cr);
                        } finally { EndPaint(Handle, ref ps); }
                    }
                    m.Result = IntPtr.Zero;
                    return;
                }
                if (m.Msg == WM_PRINTCLIENT && m.WParam != IntPtr.Zero) {
                    Color fill = !nud.Enabled ? SystemColors.Control : (nud.BackColor == SystemColors.Window ? Color.White : nud.BackColor);
                    var cr = nud.ClientRectangle;
                    using (var g = Graphics.FromHdc(m.WParam)) g.FillRectangle(Ui.CachedBrush(fill), cr);
                    m.Result = IntPtr.Zero;
                    return;
                }
                if (m.Msg == WM_NCPAINT) {
                    IntPtr hdc = GetWindowDC(Handle);
                    if (hdc != IntPtr.Zero) {
                        try { PaintNc(hdc); } finally { ReleaseDC(Handle, hdc); }
                    }
                    m.Result = IntPtr.Zero;
                    return;
                }
                if (m.Msg == WM_PRINT && m.WParam != IntPtr.Zero) {
                    base.WndProc(ref m);
                    PaintNc(m.WParam);
                    return;
                }
                base.WndProc(ref m);
            }
        }

        // NumericUpDown 右侧扁平化微调箭头按钮
        sealed class SpinnerSkin : NativeWindow {
            readonly NumericUpDown owner;
            readonly Control ctrl;
            readonly Action<bool> onHover;
            int hoverPart; // 0=无, 1=上, 2=下
            int downPart;

            public SpinnerSkin(NumericUpDown nud, Control buttons, Action<bool> hoverCallback) {
                owner = nud; ctrl = buttons; onHover = hoverCallback;
                ctrl.Cursor = Cursors.Hand;
                ctrl.HandleCreated += OnHandleCreated;
                ctrl.HandleDestroyed += OnHandleDestroyed;
                ctrl.MouseMove += OnMove;
                ctrl.MouseLeave += OnLeave;
                ctrl.MouseDown += OnDown;
                ctrl.MouseUp += OnUp;
                if (ctrl.IsHandleCreated) AssignHandle(ctrl.Handle);
            }

            void OnHandleCreated(object s, EventArgs e) { if (Handle != IntPtr.Zero) ReleaseHandle(); AssignHandle(ctrl.Handle); }
            void OnHandleDestroyed(object s, EventArgs e) { if (Handle != IntPtr.Zero) ReleaseHandle(); }
            void OnMove(object s, MouseEventArgs e) {
                int p = e.Y < ctrl.Height / 2 ? 1 : 2;
                if (hoverPart != p) { hoverPart = p; ctrl.Invalidate(); }
                if (onHover != null) onHover(true);
            }
            void OnLeave(object s, EventArgs e) {
                if (hoverPart != 0 || downPart != 0) { hoverPart = 0; downPart = 0; ctrl.Invalidate(); }
                if (onHover != null) onHover(false);
            }
            void OnDown(object s, MouseEventArgs e) {
                if (e.Button == MouseButtons.Left) { downPart = e.Y < ctrl.Height / 2 ? 1 : 2; ctrl.Invalidate(); }
            }
            void OnUp(object s, MouseEventArgs e) {
                if (downPart != 0) { downPart = 0; ctrl.Invalidate(); }
            }

            void PaintSpinner(IntPtr targetHdc) {
                int w = ctrl.Width, h = ctrl.Height;
                if (w <= 0 || h <= 0) return;
                IntPtr memDc = CreateCompatibleDC(targetHdc);
                if (memDc == IntPtr.Zero) return;
                IntPtr bmp = CreateCompatibleBitmap(targetHdc, w, h);
                if (bmp == IntPtr.Zero) { DeleteDC(memDc); return; }
                IntPtr old = SelectObject(memDc, bmp);
                try {
                    using (var g = Graphics.FromHdc(memDc)) {
                        int mid = h / 2;
                        Color bg = owner.Enabled ? Color.White : SystemColors.Control;
                        g.Clear(bg);
                        if (owner.Enabled) {
                            if (hoverPart == 1 || downPart == 1)
                                g.FillRectangle(Ui.CachedBrush(downPart == 1 ? Ui.AccentSoft : Ui.Subtle), 1, 0, Math.Max(0, w - 1), mid);
                            if (hoverPart == 2 || downPart == 2)
                                g.FillRectangle(Ui.CachedBrush(downPart == 2 ? Ui.AccentSoft : Ui.Subtle), 1, mid, Math.Max(0, w - 1), Math.Max(0, h - mid));
                        }
                        var divPen = Ui.CachedPen(Ui.Line, 1f);
                        g.DrawLine(divPen, 0, 1, 0, h - 2);
                        g.DrawLine(divPen, 1, mid, w - 1, mid);

                        float cx = (w + 1) / 2f;
                        float hw = Math.Max(2.4f, Math.Min(3.4f * Ui.DpiScale, (w - 5) / 2f));
                        float hh = Math.Max(1.6f, Math.Min(2.1f * Ui.DpiScale, mid * 0.28f));
                        Color topCol = !owner.Enabled ? Ui.Faint : (hoverPart == 1 || downPart == 1 ? Ui.Accent : Ui.Muted);
                        Color botCol = !owner.Enabled ? Ui.Faint : (hoverPart == 2 || downPart == 2 ? Ui.Accent : Ui.Muted);
                        DrawChevron(g, cx, mid / 2f, hw, hh, true, topCol, 1.3f);
                        DrawChevron(g, cx, mid + (h - mid) / 2f, hw, hh, false, botCol, 1.3f);
                    }
                    BitBlt(targetHdc, 0, 0, w, h, memDc, 0, 0, SRCCOPY);
                } finally {
                    SelectObject(memDc, old);
                    DeleteObject(bmp);
                    DeleteDC(memDc);
                }
            }

            protected override void WndProc(ref Message m) {
                if (m.Msg == WM_ERASEBKGND) { m.Result = (IntPtr)1; return; }
                if (m.Msg == WM_NCPAINT) { m.Result = IntPtr.Zero; return; }
                if (m.Msg == WM_PAINT) {
                    var ps = new PAINTSTRUCT();
                    IntPtr hdc = BeginPaint(Handle, ref ps);
                    if (hdc != IntPtr.Zero) {
                        try { PaintSpinner(hdc); } finally { EndPaint(Handle, ref ps); }
                    }
                    m.Result = IntPtr.Zero;
                    return;
                }
                if (m.Msg == WM_PRINTCLIENT && m.WParam != IntPtr.Zero) {
                    PaintSpinner(m.WParam);
                    m.Result = IntPtr.Zero;
                    return;
                }
                base.WndProc(ref m);
            }
        }

        // ==================== 5. 下拉框 ComboBox ====================
        sealed class ComboBoxSkin : NativeWindow {
            readonly ComboBox cb;
            bool hover;

            public ComboBoxSkin(ComboBox combo) {
                cb = combo;
                if (cb.DrawMode == DrawMode.Normal) {
                    cb.DrawMode = DrawMode.OwnerDrawFixed;
                    cb.DrawItem += OnDrawItem;
                }
                cb.FlatStyle = FlatStyle.Standard;
                if (cb.DropDownStyle == ComboBoxStyle.DropDownList) cb.Cursor = Cursors.Hand;
                cb.HandleCreated += OnHandleCreated;
                cb.HandleDestroyed += OnHandleDestroyed;
                cb.MouseEnter += OnEnter;
                cb.MouseLeave += OnLeave;
                cb.GotFocus += OnRepaint;
                cb.LostFocus += OnRepaint;
                cb.Enter += OnRepaint;
                cb.Leave += OnRepaint;
                cb.DropDown += OnRepaint;
                cb.DropDownClosed += OnRepaint;
                cb.SelectedIndexChanged += OnRepaint;
                cb.EnabledChanged += OnRepaint;
                cb.FontChanged += OnFontChanged;
                if (cb.IsHandleCreated) AttachHandle();
            }

            void AttachHandle() {
                if (Handle != IntPtr.Zero) ReleaseHandle();
                AssignHandle(cb.Handle);
                StripNativeBorder(cb.Handle);
                ApplyHeights();
                SetWindowPos(cb.Handle, IntPtr.Zero, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
            }

            void ApplyHeights() {
                if (!cb.IsHandleCreated) return;
                try {
                    int lineH = TextRenderer.MeasureText("Ag测", cb.Font).Height;
                    // 关闭态紧凑高度（与 TextBox / NumericUpDown / FieldGrid 视觉对齐），下拉项宽松 28px 高度
                    int closedInner = Math.Max(Ui.S(18), lineH + Ui.S(1));
                    int listItemH = Math.Max(Ui.S(26), lineH + Ui.S(9));
                    SendMessage(cb.Handle, CB_SETITEMHEIGHT, (IntPtr)(-1), (IntPtr)closedInner);
                    SendMessage(cb.Handle, CB_SETITEMHEIGHT, IntPtr.Zero, (IntPtr)listItemH);
                } catch { }
            }

            void OnHandleCreated(object s, EventArgs e) { AttachHandle(); }
            void OnHandleDestroyed(object s, EventArgs e) { if (Handle != IntPtr.Zero) ReleaseHandle(); }
            void OnFontChanged(object s, EventArgs e) { ApplyHeights(); cb.Invalidate(); }
            void OnEnter(object s, EventArgs e) { hover = true; cb.Invalidate(); }
            void OnLeave(object s, EventArgs e) { hover = false; cb.Invalidate(); }
            void OnRepaint(object s, EventArgs e) { cb.Invalidate(); }

            void OnDrawItem(object sender, DrawItemEventArgs e) {
                var g = e.Graphics;
                g.FillRectangle(Ui.CachedBrush(Color.White), e.Bounds);
                if (e.Index < 0 || e.Index >= cb.Items.Count) return;

                bool selected = (e.State & DrawItemState.Selected) != 0;
                bool isEdit = (e.State & DrawItemState.ComboBoxEdit) != 0;
                bool isCurrent = !isEdit && e.Index == cb.SelectedIndex;
                string text = cb.GetItemText(cb.Items[e.Index]) ?? "";

                if (selected && !isEdit) {
                    var pill = new Rectangle(e.Bounds.X + Ui.S(3), e.Bounds.Y + 1, Math.Max(2, e.Bounds.Width - Ui.S(6)), Math.Max(2, e.Bounds.Height - 2));
                    Ui.FillRound(g, pill, Ui.S(5), Ui.AccentSoft);
                }

                bool showCheck = isCurrent && e.Bounds.Width >= Ui.S(64);
                int padL = isEdit ? Ui.S(4) : Ui.S(10);
                int padR = showCheck ? Ui.S(22) : Ui.S(8);
                var tr = new Rectangle(e.Bounds.X + padL, e.Bounds.Y, Math.Max(4, e.Bounds.Width - padL - padR), e.Bounds.Height);
                Color fore = !cb.Enabled ? Ui.DisabledText
                           : ((selected || isCurrent) && !isEdit ? Ui.Accent : Ui.Text);
                TextRenderer.DrawText(g, text, cb.Font, tr, fore,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);

                if (showCheck) {
                    int cs = Ui.S(11);
                    Ui.DrawCheck(g, new Rectangle(e.Bounds.Right - cs - Ui.S(8), e.Bounds.Y + (e.Bounds.Height - cs) / 2, cs, cs), Ui.Accent, 1.6f);
                }
            }

            void PaintCombo(IntPtr targetHdc) {
                int w = cb.Width, h = cb.Height;
                if (w <= 2 || h <= 2) return;

                bool editable = cb.DropDownStyle == ComboBoxStyle.DropDown;
                bool hasEditRect = false;
                RECT editRc = new RECT();
                if (editable) {
                    var cbi = new COMBOBOXINFO { cbSize = Marshal.SizeOf(typeof(COMBOBOXINFO)) };
                    if (GetComboBoxInfo(Handle, ref cbi) && cbi.hwndEdit != IntPtr.Zero) {
                        editRc = cbi.rcItem;
                        hasEditRect = editRc.Right > editRc.Left && editRc.Bottom > editRc.Top;
                    }
                }

                IntPtr memDc = CreateCompatibleDC(targetHdc);
                if (memDc == IntPtr.Zero) return;
                IntPtr bmp = CreateCompatibleBitmap(targetHdc, w, h);
                if (bmp == IntPtr.Zero) { DeleteDC(memDc); return; }
                IntPtr old = SelectObject(memDc, bmp);
                try {
                    bool active = cb.DroppedDown || cb.Focused || cb.ContainsFocus;
                    float bw = active && cb.Enabled ? 1.5f : 1f;
                    Color border = !cb.Enabled ? BorderDisabled : (active ? Ui.Accent : (hover ? BorderHover : BorderNormal));
                    Color fill = !cb.Enabled ? Ui.Rail : Color.White;

                    using (var g = Graphics.FromHdc(memDc)) {
                        DrawRoundedBorder(g, w, h, Ui.S(6), ResolveParentBack(cb), fill, border, bw);

                        // 右侧下拉指示箭头（展开时优雅朝上）
                        int arrowZone = Ui.S(24);
                        float cx = w - arrowZone / 2f;
                        float cy = h / 2f;
                        Color arrowCol = !cb.Enabled ? Ui.Faint : (active ? Ui.Accent : (hover ? Ui.Text2 : Ui.Muted));
                        DrawChevron(g, cx, cy, Ui.S(4), Ui.S(2) + 0.5f, cb.DroppedDown, arrowCol, 1.5f);

                        if (!editable) {
                            string text = cb.SelectedIndex >= 0 ? (cb.GetItemText(cb.SelectedItem) ?? "") : (cb.Text ?? "");
                            if (text.Length > 0) {
                                int padL = Ui.S(9);
                                var tr = new Rectangle(padL, 0, Math.Max(4, w - padL - arrowZone - Ui.S(2)), h);
                                bool placeholder = text.StartsWith("（") || text.StartsWith("(");
                                Color fore = !cb.Enabled ? Ui.DisabledText : (placeholder ? Ui.Muted : Ui.Text);
                                TextRenderer.DrawText(g, text, cb.Font, tr, fore,
                                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                            }
                        }
                    }

                    int saved = hasEditRect ? SaveDC(targetHdc) : 0;
                    try {
                        if (hasEditRect) ExcludeClipRect(targetHdc, editRc.Left, editRc.Top, editRc.Right, editRc.Bottom);
                        BitBlt(targetHdc, 0, 0, w, h, memDc, 0, 0, SRCCOPY);
                    } finally {
                        if (saved != 0) RestoreDC(targetHdc, saved);
                    }
                } finally {
                    SelectObject(memDc, old);
                    DeleteObject(bmp);
                    DeleteDC(memDc);
                }
            }

            protected override void WndProc(ref Message m) {
                if (m.Msg == WM_ERASEBKGND) { m.Result = (IntPtr)1; return; }
                if (m.Msg == WM_NCCALCSIZE) { m.Result = IntPtr.Zero; return; }
                if (m.Msg == WM_NCPAINT) { m.Result = IntPtr.Zero; return; }
                if (m.Msg == WM_PAINT) {
                    var ps = new PAINTSTRUCT();
                    IntPtr hdc = BeginPaint(Handle, ref ps);
                    if (hdc != IntPtr.Zero) {
                        try { PaintCombo(hdc); } finally { EndPaint(Handle, ref ps); }
                    }
                    m.Result = IntPtr.Zero;
                    return;
                }
                if (m.Msg == WM_PRINT && m.WParam != IntPtr.Zero) {
                    if (cb.DropDownStyle == ComboBoxStyle.DropDownList) {
                        PaintCombo(m.WParam);
                        m.Result = IntPtr.Zero;
                        return;
                    }
                    base.WndProc(ref m);
                    PaintCombo(m.WParam);
                    return;
                }
                if (m.Msg == WM_PRINTCLIENT && m.WParam != IntPtr.Zero) {
                    PaintCombo(m.WParam);
                    m.Result = IntPtr.Zero;
                    return;
                }
                base.WndProc(ref m);
            }
        }

        // ==================== 6. 右键菜单 / 下拉菜单 ContextMenuStrip ====================
        static void DrawRightChevron(Graphics g, float cx, float cy, float halfW, float halfH, Color color, float stroke) {
            var sm = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(color, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round }) {
                g.DrawLines(pen, new[] {
                    new PointF(cx - halfW * 0.55f, cy - halfH),
                    new PointF(cx + halfW * 0.65f, cy),
                    new PointF(cx - halfW * 0.55f, cy + halfH)
                });
            }
            g.SmoothingMode = sm;
        }

        static bool IsDangerMenuItem(ToolStripItem item) {
            if (item == null) return false;
            var tag = item.Tag as string;
            if (string.Equals(tag, "danger", StringComparison.OrdinalIgnoreCase)) return true;
            string t = (item.Text ?? "").TrimStart();
            return t.StartsWith("删除", StringComparison.Ordinal)
                || t.StartsWith("仅删除", StringComparison.Ordinal)
                || t.StartsWith("移除", StringComparison.Ordinal)
                || t.StartsWith("从本页移除", StringComparison.Ordinal)
                || t.StartsWith("清空", StringComparison.Ordinal);
        }

        static bool IsAccentMenuItem(ToolStripItem item) {
            if (item == null) return false;
            return string.Equals(item.Tag as string, "accent", StringComparison.OrdinalIgnoreCase);
        }

        sealed class MenuSkin {
            readonly ToolStripDropDown menu;
            public bool DwmRounded;

            public MenuSkin(ToolStripDropDown dropDown) {
                menu = dropDown;
                var ddm = menu as ToolStripDropDownMenu;
                if (ddm != null) {
                    ddm.ShowImageMargin = false;
                    ddm.ShowCheckMargin = false;
                }
                menu.Renderer = sharedMenuRenderer;
                menu.BackColor = Color.White;
                menu.ForeColor = Ui.Text;
                menu.Font = Ui.F9;
                menu.DropShadowEnabled = true;
                menu.Padding = Ui.SPad(5, 6, 5, 6);
                Ui.EnableDoubleBuffer(menu);

                menu.Opening += OnOpening;
                menu.HandleCreated += OnHandleCreated;
                menu.SizeChanged += OnSizeChanged;
                menu.ItemAdded += OnItemAdded;
                if (menu.IsHandleCreated) ApplyCornerClip();
            }

            void OnOpening(object s, System.ComponentModel.CancelEventArgs e) {
                RefreshItems();
                if (menu.IsHandleCreated) ApplyCornerClip();
            }

            void OnHandleCreated(object s, EventArgs e) { ApplyCornerClip(); }
            void OnSizeChanged(object s, EventArgs e) { if (!DwmRounded) ApplyRegionClip(); }

            void OnItemAdded(object s, ToolStripItemEventArgs e) {
                if (e != null && e.Item != null) ConfigureItem(e.Item);
            }

            public void RefreshItems() {
                if (menu == null || menu.IsDisposed) return;
                menu.SuspendLayout();
                try {
                    foreach (ToolStripItem item in menu.Items) ConfigureItem(item);
                } finally {
                    menu.ResumeLayout(true);
                }
            }

            static void ConfigureItem(ToolStripItem item) {
                if (item == null || item.IsDisposed) return;
                var sep = item as ToolStripSeparator;
                if (sep != null) {
                    sep.AutoSize = false;
                    sep.Height = Ui.S(11);
                    sep.Margin = new Padding(0);
                    return;
                }
                var mi = item as ToolStripMenuItem;
                if (mi != null) {
                    Font wantFont = IsAccentMenuItem(mi) ? Ui.F9B : Ui.F9;
                    if (mi.Font != wantFont) mi.Font = wantFont;
                    int rightPad = (mi.HasDropDownItems || mi.CheckOnClick || mi.Checked) ? 24 : 16;
                    mi.Padding = Ui.SPad(8, 5, rightPad, 5);
                    mi.Margin = Ui.SPad(0, 1, 0, 1);
                    if (mi.HasDropDownItems) StyleMenu(mi.DropDown);
                }
            }

            void ApplyCornerClip() {
                if (menu == null || menu.IsDisposed || !menu.IsHandleCreated) return;
                DwmRounded = false;
                try {
                    int pref = DWMWCP_ROUND;
                    int hr = DwmSetWindowAttribute(menu.Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
                    if (hr == 0) {
                        DwmRounded = true;
                        int borderBgr = (BorderNormal.B << 16) | (BorderNormal.G << 8) | BorderNormal.R;
                        DwmSetWindowAttribute(menu.Handle, DWMWA_BORDER_COLOR, ref borderBgr, sizeof(int));
                        var oldReg = menu.Region;
                        if (oldReg != null) { menu.Region = null; try { oldReg.Dispose(); } catch { } }
                        return;
                    }
                } catch { }
                ApplyRegionClip();
            }

            void ApplyRegionClip() {
                if (menu == null || menu.IsDisposed || menu.Width <= 4 || menu.Height <= 4) return;
                try {
                    using (var path = Ui.Rounded(new Rectangle(0, 0, menu.Width, menu.Height), Ui.S(8))) {
                        var oldReg = menu.Region;
                        menu.Region = new Region(path);
                        if (oldReg != null) { try { oldReg.Dispose(); } catch { } }
                    }
                } catch { }
            }
        }

        sealed class MenuRenderer : ToolStripRenderer {
            protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) {
                e.Graphics.FillRectangle(Ui.CachedBrush(Color.White), e.AffectedBounds);
            }

            protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) {
                // 不画原生灰色图标栏，保持全白简洁卡片
            }

            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) {
                var dd = e.ToolStrip as ToolStripDropDown;
                MenuSkin skin = null;
                if (dd != null) styledMenus.TryGetValue(dd, out skin);
                // 始终绘制 1px 圆角细框：Win11 下与 DWM 圆角同色贴合不突兀，Win10 与离屏渲染下同样保持清晰卡片边界
                Color borderCol = (skin != null && skin.DwmRounded) ? Ui.Line : BorderNormal;
                var r = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
                Ui.DrawRound(e.Graphics, r, Ui.S(8), borderCol, 1f);
            }

            static int ItemContentRight(ToolStripItem item, ToolStrip strip) {
                if (strip == null || item == null) return item == null ? 0 : item.Width;
                int visibleW = strip.ClientSize.Width - item.Bounds.Left;
                return Math.Min(item.Width, Math.Max(0, visibleW));
            }

            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e) {
                var item = e.Item;
                if (!item.Enabled) return;
                if (!item.Selected && !item.Pressed) return;
                bool danger = IsDangerMenuItem(item);
                Color fill = danger
                    ? (item.Pressed ? Color.FromArgb(0xFE, 0xCA, 0xCA) : Ui.BadSoft)
                    : (item.Pressed ? Ui.AccentRing : Ui.AccentSoft);
                int padX = Ui.S(2);
                int rightEdge = ItemContentRight(item, e.ToolStrip) - Ui.S(7);
                var pill = new Rectangle(padX, 1, Math.Max(4, rightEdge - padX), Math.Max(4, item.Height - 2));
                Ui.FillRound(e.Graphics, pill, Ui.S(6), fill);
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e) {
                var item = e.Item;
                var mi = item as ToolStripMenuItem;
                bool danger = IsDangerMenuItem(item);
                bool accent = IsAccentMenuItem(item);
                bool isChecked = mi != null && mi.Checked;
                bool hasSub = mi != null && mi.HasDropDownItems;
                bool active = item.Enabled && (item.Selected || item.Pressed);

                Color fore = !item.Enabled ? Ui.Faint
                           : (danger ? Ui.Bad
                           : ((accent || isChecked || active) ? Ui.Accent : Ui.Text));

                int rightEdge = ItemContentRight(item, e.ToolStrip);
                int leftX = Ui.S(12);
                int rightReserve = (hasSub || isChecked) ? Ui.S(30) : Ui.S(14);
                var tr = new Rectangle(leftX, 0, Math.Max(10, rightEdge - leftX - rightReserve), item.Height);
                TextRenderer.DrawText(e.Graphics, e.Text ?? "", e.TextFont ?? Ui.F9, tr, fore,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);

                if (hasSub) {
                    Color arrowCol = !item.Enabled ? Ui.Faint : (active ? Ui.Accent : Ui.Muted);
                    DrawRightChevron(e.Graphics, rightEdge - Ui.S(16), item.Height / 2f, Ui.S(4), Ui.S(4), arrowCol, 1.5f);
                } else if (isChecked) {
                    int cs = Ui.S(11);
                    Ui.DrawCheck(e.Graphics,
                        new Rectangle(rightEdge - cs - Ui.S(12), (item.Height - cs) / 2, cs, cs),
                        item.Enabled ? Ui.Accent : Ui.Faint, 1.7f);
                }
            }

            protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e) {
                // 勾选标记已由 OnRenderItemText 在右侧统一绘制，屏蔽原生复古方框
            }

            protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e) {
                // 子菜单箭头已由 OnRenderItemText 使用与菜单右缘严格对称的矢量尖括号绘制，屏蔽原生实心三角
            }

            protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e) {
                int y = e.Item.Height / 2;
                int leftInset = Ui.S(10);
                int right = Math.Max(leftInset, ItemContentRight(e.Item, e.ToolStrip) - Ui.S(15));
                e.Graphics.DrawLine(Ui.CachedPen(Ui.Line, 1f), leftInset, y, right, y);
            }
        }
    }
}
