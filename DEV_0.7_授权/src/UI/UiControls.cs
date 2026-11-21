using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace TianGongCadSuite {
    // 按钮：主按钮（靛蓝实心）/ 次按钮（白底细边）/ 文字按钮。悬停、按下的颜色是渐变过去的（Anim）。
    // Progress ≥ 0 时主按钮自己变成进度条（打孔中 3 / 6）。
    public sealed class FlatButton : Button {
        public enum Kind { Primary, Secondary, Ghost }
        public readonly Kind Style;
        Color back, fore, hover, press, line;
        float hot;            // 0 = 常态，1 = 悬停（渐变用）
        bool down;
        float progress = -1;  // <0：不画进度
        public int Radius = Ui.S(8);

        public FlatButton(string text, Kind kind, int width, int height){
            Style = kind;
            Init(text, width, height);
            switch (kind) {
                case Kind.Primary:   back = Ui.Accent; fore = Color.White; hover = Ui.AccentD; press = Ui.AccentPress; line = Color.Empty; Font = height >= Ui.S(40) ? Ui.BtnB : Ui.F9B; Radius = Ui.S(height >= Ui.S(40) ? 10 : 8); break;
                case Kind.Secondary: back = Color.White; fore = Ui.Text; hover = Ui.Subtle; press = Ui.Line; line = Ui.Line; Font = height >= Ui.S(36) ? Ui.Btn : Ui.F9; break;
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
            Font = Ui.F9; Height = Ui.S(34); BackColor = Color.White; TabStop = true; Cursor = Cursors.Hand;
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
            int m = Ui.S(3), pad = Ui.S(22);
            int inner = Math.Max(1, Width - m * 2);
            var w = new int[Items.Count]; int total = 0;
            for (int i = 0; i < Items.Count; i++) { w[i] = TextRenderer.MeasureText(Items[i], Ui.F9B).Width + pad; total += w[i]; }
            double scale = (double)inner / Math.Max(1, total);
            double x = m;
            for (int i = 0; i < Items.Count; i++) { e[i] = (int)Math.Round(x); x += w[i] * scale; }
            e[Items.Count] = Width - m;
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
        public int PreferredWidth { get { int t = Ui.S(7), pad = Ui.S(22); foreach (var s in Items) t += TextRenderer.MeasureText(s, Ui.F9B).Width + pad; return t; } }
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
            int m = Ui.S(3), rOut = Ui.S(8), rIn = Ui.S(6);
            Ui.FillRound(g, outer, rOut, Ui.Subtle);
            if (Focused) Ui.DrawRound(g, outer, rOut, Ui.AccentRing, 2);
            if (selected >= 0 && e.Length > selected + 1) {
                if (thumbX < 0) { thumbX = e[selected]; thumbW = e[selected + 1] - e[selected]; }
                var tr = new Rectangle((int)thumbX, m, (int)thumbW, Height - m * 2 - 1);
                Ui.FillRound(g, new Rectangle(tr.X, tr.Y + 1, tr.Width, tr.Height), rIn, Color.FromArgb(24, 24, 24, 27));
                Ui.FillRound(g, tr, rIn, Color.White);
            }
            for (int i = 0; i < Items.Count; i++) {
                var r = new Rectangle(e[i], 0, e[i + 1] - e[i], Height - 1);
                if (i == hover && i != selected && Enabled) Ui.FillRound(g, Rectangle.Inflate(new Rectangle(r.X, m, r.Width, Height - m * 2 - 1), -1, 0), rIn, Color.FromArgb(0xEA, 0xEA, 0xEC));
                Color fore = i == selected ? (Enabled ? Ui.Accent : Ui.Muted) : (Enabled ? Ui.Text : Ui.Faint);
                TextRenderer.DrawText(g, Items[i], i == selected ? Ui.F9B : Font, r, fore,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }
    }

    // 可点的筛选胶囊："漏打孔 3"，点一下切换显示/隐藏这一类。选中时浅色底 + 彩色字，没选中只留描边、字变灰。
    public sealed class ToggleChip : Control {
        public Color ChipColor = Ui.Accent;
        bool on = true, hover;
        public event EventHandler CheckedChanged;
        public ToggleChip(){
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.White; Font = Ui.F8; Height = Ui.S(28); Cursor = Cursors.Hand;
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
            int rad = Ui.S(8);
            if (on) Ui.FillRound(g, r, rad, Color.FromArgb(hover ? 44 : 26, ChipColor));
            else if (hover) Ui.FillRound(g, r, rad, Ui.Subtle);
            Ui.DrawRound(g, r, rad, on ? Color.FromArgb(110, ChipColor) : Ui.Line, 1);
            TextRenderer.DrawText(g, (on ? "✓ " : "") + Text, on ? Ui.F9B : Font, r, on ? ChipColor : Ui.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
        void PaintRail(Graphics g, Rectangle r){
            int rad = Ui.S(8);
            if (on) { Ui.FillRound(g, r, rad, Color.White); Ui.DrawRound(g, r, rad, Ui.Line, 1); }
            else if (hover) Ui.FillRound(g, r, rad, Color.FromArgb(0xEF, 0xEF, 0xF1));
            string t = Text ?? ""; int sp = t.LastIndexOf(' ');
            string name = sp > 0 ? t.Substring(0, sp) : t, count = sp > 0 ? t.Substring(sp + 1) : "";
            var sm = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias;
            int ds = Ui.S(8), cs = Ui.S(12);
            var dot = new Rectangle(Ui.S(12), r.Height / 2 - ds / 2, ds, ds);
            if (on) using (var b = new SolidBrush(ChipColor)) g.FillEllipse(b, dot);
            else using (var p = new Pen(Ui.Faint, 1.4f)) g.DrawEllipse(p, dot);
            if (on) Ui.DrawCheck(g, new Rectangle(r.Right - Ui.S(22), r.Height / 2 - cs / 2, cs, cs), ChipColor, 1.6f);
            g.SmoothingMode = sm;
            int nameX = Ui.S(30), countW = Ui.S(42), countX = r.Right - Ui.S(70);
            TextRenderer.DrawText(g, name, on ? Ui.F9B : Ui.F9, new Rectangle(nameX, 0, Math.Max(10, countX - nameX - Ui.S(4)), r.Height), on ? Ui.Text : Ui.DisabledText,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, count, on ? Ui.F9B : Ui.F9, new Rectangle(countX, 0, countW, r.Height), on ? Ui.Text : Ui.DisabledText,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        }
    }

    // 状态徽标：一行小字 + 浅色底（「漏打孔」「已完成」）。
    public sealed class Chip : Control {
        public Color ChipColor = Ui.Accent;
        public Chip(){
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Ui.Bg; Font = Ui.F9B; Height = Ui.S(26);
        }
        protected override void OnPaint(PaintEventArgs e){
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Ui.Bg);
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            Ui.FillRound(g, r, Ui.S(6), Color.FromArgb(30, ChipColor));
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
