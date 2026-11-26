using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace TianGongCadSuite {
    // 多行自动换行的可点组标签（孔规格页上：「Φ6.6 ×4  M6 螺纹孔」「Φ5 ×2  M6 圆柱沉孔」），点一个 = 编辑那一组。
    // 组数多时自动折行增高（最多展示 MaxRows 行，超出后右侧出现现代细滚动条，滚轮/拖动/点选自动滚动可见），彻底消除溢出截断与遮挡。
    public sealed class ChipTabs : Control {
        readonly List<string> heads = new List<string>(), subs = new List<string>();
        readonly List<Rectangle> rects = new List<Rectangle>();
        float[] heat = new float[0];
        int selected = -1, hover = -1;
        int scrollY, scrollMax, contentHeight, cachedWidth = -1;
        bool hoverThumb, draggingThumb;
        int dragStartY, dragStartScroll;
        Rectangle thumbRect, trackRect;
        public int MaxRows = 3;
        public int RowHeight = Ui.S(38);
        public int RowGap = Ui.S(6);
        public string EmptyText = "";
        public event EventHandler SelectedChanged;   // 只在用户点击时触发
        public ChipTabs(){
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = Ui.S(42); BackColor = Ui.Bg; Cursor = Cursors.Hand;
        }
        public int Count { get { return heads.Count; } }
        public int ScrollMax { get { return scrollMax; } }
        public int ScrollOffset { get { return scrollY; } }
        public int VisibleChipCount { get { return rects.Count; } }
        public Rectangle ChipBounds(int index){
            if (cachedWidth != Width) ComputeLayout(Width);
            if (index < 0 || index >= rects.Count) return Rectangle.Empty;
            var r = rects[index];
            return new Rectangle(r.X, r.Y - scrollY, r.Width, r.Height);
        }

        int EffectiveMaxRows(){
            int mr = Math.Max(1, MaxRows);
            if (Parent != null && Parent.ClientSize.Height > 0 && Parent.ClientSize.Height < Ui.S(420)) mr = Math.Min(mr, 2);
            return mr;
        }
        int ComputeLayoutForWidth(int width, List<Rectangle> targetRects, out int totalH, out bool needScroll){
            if (targetRects != null) targetRects.Clear();
            int padTop = 1, padBot = 2;
            if (heads.Count == 0) {
                totalH = RowHeight + padTop + padBot;
                needScroll = false;
                return totalH;
            }
            int extra = Ui.S(34), tabGap = Ui.S(8), sbW = Ui.S(12);
            int maxRows = EffectiveMaxRows();
            int maxVisH = maxRows * RowHeight + Math.Max(0, maxRows - 1) * RowGap + padTop + padBot;

            int availW = Math.Max(Ui.S(120), width);
            int rows1;
            int h1 = FlowChips(availW, extra, tabGap, padTop, padBot, targetRects, out rows1);
            if (rows1 > maxRows && width > Ui.S(140)) {
                needScroll = true;
                int rows2;
                totalH = FlowChips(Math.Max(Ui.S(100), width - sbW), extra, tabGap, padTop, padBot, targetRects, out rows2);
                return maxVisH;
            }
            needScroll = false;
            totalH = h1;
            return Math.Min(h1, maxVisH);
        }
        int FlowChips(int availW, int extra, int tabGap, int padTop, int padBot, List<Rectangle> targetRects, out int rows){
            if (targetRects != null) targetRects.Clear();
            int x = 0, y = padTop;
            rows = 1;
            for (int i = 0; i < heads.Count; i++) {
                var hsz = TextRenderer.MeasureText(heads[i], Ui.NumB);
                var ssz = TextRenderer.MeasureText(subs[i], Ui.F9B);
                int w = Math.Min(availW, Math.Max(Ui.S(72), hsz.Width + ssz.Width + extra));
                if (x > 0 && x + w > availW) {
                    x = 0;
                    y += RowHeight + RowGap;
                    rows++;
                }
                if (targetRects != null) targetRects.Add(new Rectangle(x, y, w, RowHeight));
                x += w + tabGap;
            }
            return y + RowHeight + padBot;
        }
        void ComputeLayout(int width){
            cachedWidth = width;
            int totalH; bool needScroll;
            int visH = ComputeLayoutForWidth(width, rects, out totalH, out needScroll);
            contentHeight = totalH;
            int curH = Height > 0 ? Height : visH;
            scrollMax = Math.Max(0, contentHeight - curH);
            if (scrollY > scrollMax) scrollY = scrollMax;
            if (scrollY < 0) scrollY = 0;
            UpdateScrollbarRects();
        }
        void UpdateScrollbarRects(){
            if (scrollMax <= 0 || Height <= Ui.S(16) || Width <= Ui.S(16)) {
                trackRect = Rectangle.Empty; thumbRect = Rectangle.Empty;
                return;
            }
            int sbW = Ui.S(6), pad = Ui.S(2);
            trackRect = new Rectangle(Width - sbW - pad, pad, sbW, Math.Max(1, Height - pad * 2));
            int th = Math.Max(Ui.S(22), (int)Math.Round((double)trackRect.Height * Height / Math.Max(1, contentHeight)));
            if (th > trackRect.Height) th = trackRect.Height;
            int travel = Math.Max(0, trackRect.Height - th);
            int ty = trackRect.Top + (scrollMax > 0 ? (int)Math.Round((double)travel * scrollY / scrollMax) : 0);
            thumbRect = new Rectangle(trackRect.X, ty, trackRect.Width, th);
        }
        public void EnsureSelectedVisible(){
            if (cachedWidth != Width) ComputeLayout(Width);
            if (selected < 0 || selected >= rects.Count || scrollMax <= 0) return;
            var r = rects[selected];
            int target = scrollY;
            if (r.Top - 1 < target) target = Math.Max(0, r.Top - 1);
            else if (r.Bottom + 2 > target + Height) target = Math.Min(scrollMax, r.Bottom + 2 - Height);
            if (target != scrollY) {
                scrollY = target;
                UpdateScrollbarRects();
                Invalidate();
            }
        }
        public override Size GetPreferredSize(Size proposedSize){
            int w = proposedSize.Width > 0 ? proposedSize.Width : (Width > 0 ? Width : Ui.S(400));
            int totalH; bool needScroll;
            int visH = ComputeLayoutForWidth(w, null, out totalH, out needScroll);
            return new Size(w, visH);
        }

        public void SetItems(IList<string> h, IList<string> sub, int sel){
            if (selected == sel && heads.Count == h.Count && subs.Count == sub.Count) {
                bool same = true;
                for (int i = 0; i < h.Count; i++) {
                    if (heads[i] != h[i] || subs[i] != sub[i]) { same = false; break; }
                }
                if (same) return;
            }
            heads.Clear(); heads.AddRange(h); subs.Clear(); subs.AddRange(sub);
            var nh = new float[heads.Count];
            for (int i = 0; i < nh.Length; i++) nh[i] = i == sel ? 1 : 0;
            heat = nh; selected = sel;
            cachedWidth = -1;
            int wantH = GetPreferredSize(new Size(Width > 0 ? Width : Ui.S(400), 0)).Height;
            if (Height != wantH) {
                Height = wantH;
                if (Parent != null) Parent.PerformLayout();
            }
            ComputeLayout(Width);
            EnsureSelectedVisible();
            Invalidate();
        }
        public int Selected {
            get { return selected; }
            set {
                if (value == selected) { EnsureSelectedVisible(); return; }
                int old = selected; selected = value;
                var h = heat;
                for (int i = 0; i < h.Length; i++) h[i] = i == selected ? 1f : 0f;
                EnsureSelectedVisible();
                Invalidate();
            }
        }
        protected override void OnResize(EventArgs e){
            base.OnResize(e);
            ComputeLayout(Width);
            EnsureSelectedVisible();
        }
        protected override void OnMouseWheel(MouseEventArgs e){
            if (scrollMax > 0) {
                int step = RowHeight + RowGap;
                int delta = e.Delta > 0 ? -step : step;
                int next = Math.Max(0, Math.Min(scrollMax, scrollY + delta));
                if (next != scrollY) {
                    scrollY = next;
                    UpdateScrollbarRects();
                    Invalidate();
                }
            }
            base.OnMouseWheel(e);
        }
        protected override void OnMouseDown(MouseEventArgs e){
            if (e.Button == MouseButtons.Left && scrollMax > 0 && !trackRect.IsEmpty) {
                var hitTrack = Rectangle.Inflate(trackRect, Ui.S(3), 0);
                if (hitTrack.Contains(e.Location)) {
                    var hitThumb = Rectangle.Inflate(thumbRect, Ui.S(3), 0);
                    if (hitThumb.Contains(e.Location)) {
                        draggingThumb = true;
                        dragStartY = e.Y;
                        dragStartScroll = scrollY;
                        Capture = true;
                        Invalidate();
                        return;
                    }
                    int next = e.Y < thumbRect.Top ? Math.Max(0, scrollY - Height + RowGap) : Math.Min(scrollMax, scrollY + Height - RowGap);
                    if (next != scrollY) { scrollY = next; UpdateScrollbarRects(); Invalidate(); }
                    return;
                }
            }
            base.OnMouseDown(e);
        }
        protected override void OnMouseUp(MouseEventArgs e){
            if (draggingThumb) {
                draggingThumb = false;
                Capture = false;
                Invalidate();
            }
            base.OnMouseUp(e);
        }
        protected override void OnMouseMove(MouseEventArgs e){
            if (draggingThumb && scrollMax > 0 && trackRect.Height > thumbRect.Height) {
                int travel = trackRect.Height - thumbRect.Height;
                int dy = e.Y - dragStartY;
                int next = Math.Max(0, Math.Min(scrollMax, dragStartScroll + (int)Math.Round((double)dy * scrollMax / travel)));
                if (next != scrollY) { scrollY = next; UpdateScrollbarRects(); Invalidate(); }
                return;
            }
            bool overSb = scrollMax > 0 && !trackRect.IsEmpty && Rectangle.Inflate(trackRect, Ui.S(3), 0).Contains(e.Location);
            int h = overSb ? -1 : Hit(e.Location);
            if (h != hover || overSb != hoverThumb) {
                hover = h; hoverThumb = overSb;
                Cursor = (h >= 0 || overSb) ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
            base.OnMouseMove(e);
        }
        protected override void OnMouseLeave(EventArgs e){
            if (!draggingThumb && (hover != -1 || hoverThumb)) { hover = -1; hoverThumb = false; Invalidate(); }
            base.OnMouseLeave(e);
        }
        protected override void OnMouseClick(MouseEventArgs e){
            if (scrollMax > 0 && !trackRect.IsEmpty && Rectangle.Inflate(trackRect, Ui.S(3), 0).Contains(e.Location)) { base.OnMouseClick(e); return; }
            int h = Hit(e.Location);
            if (h >= 0 && Enabled) { Selected = h; var ev = SelectedChanged; if (ev != null) ev(this, EventArgs.Empty); }
            base.OnMouseClick(e);
        }
        int Hit(Point p){
            if (cachedWidth != Width) ComputeLayout(Width);
            var vp = new Point(p.X, p.Y + scrollY);
            for (int i = 0; i < rects.Count; i++) if (rects[i].Contains(vp)) return i;
            return -1;
        }
        protected override void OnPaint(PaintEventArgs e){
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Ui.Bg);
            if (cachedWidth != Width) ComputeLayout(Width);
            if (heads.Count == 0) {
                TextRenderer.DrawText(g, EmptyText, Ui.F9, new Rectangle(0, 0, Width, Height), Ui.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                return;
            }
            int padL = Ui.S(12), padR = Ui.S(10), midGap = Ui.S(10), rad = Ui.S(10);
            for (int i = 0; i < heads.Count && i < rects.Count; i++) {
                var src = rects[i];
                var r = new Rectangle(src.X, src.Y - scrollY, src.Width, src.Height);
                if (r.Bottom < 0 || r.Top > Height) continue;
                var hsz = TextRenderer.MeasureText(heads[i], Ui.NumB);
                float k = i < heat.Length ? heat[i] : 0;
                var sf = k > 0.5f ? Ui.F9B : Ui.F9;
                Ui.FillRound(g, r, rad, Anim.Mix(i == hover ? Ui.Subtle : Color.White, Ui.AccentSoft, k));
                Ui.DrawRound(g, r, rad, Anim.Mix(Ui.Line, Ui.Accent, k), 1 + k * 0.5f);
                int hx = r.X + padL;
                int hw = Math.Min(hsz.Width, Math.Max(0, r.Width - padL - padR));
                TextRenderer.DrawText(g, heads[i], Ui.NumB, new Rectangle(hx, r.Y, hw, r.Height), Ui.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                int sx = hx + hw + midGap;
                int sw = Math.Max(0, r.Right - padR - sx);
                if (sw > Ui.S(16)) {
                    TextRenderer.DrawText(g, subs[i], sf, new Rectangle(sx, r.Y, sw, r.Height), Anim.Mix(Ui.Muted, Ui.Accent, k),
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                }
            }
            if (scrollMax > 0 && !trackRect.IsEmpty) {
                int trRad = Math.Max(2, trackRect.Width / 2);
                Ui.FillRound(g, trackRect, trRad, Ui.Subtle);
                Color thumbCol = draggingThumb ? Ui.Accent : (hoverThumb ? Ui.Muted : Color.FromArgb(0xC4, 0xC4, 0xCC));
                Ui.FillRound(g, thumbRect, trRad, thumbCol);
            }
        }
    }

    // 数字小方块（「要打的孔 6」「零件 2」）：浅灰底，上面一行小字，下面一个大数字。
    public sealed class StatTile : Control {
        static readonly Font BigNumFont = new Font(Ui.Mono, 18F, FontStyle.Bold);
        public string Caption = "";
        public Color ValueColor = Ui.Text;
        public StatTile(string caption){
            Caption = caption; Text = "0";
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = Ui.S(82);
        }
        public void Set(string value, Color color){ if (Text == value && ValueColor == color) return; Text = value; ValueColor = color; Invalidate(); }
        protected override void OnPaint(PaintEventArgs e){
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Ui.Bg);
            Ui.FillRound(g, new Rectangle(0, 0, Width - 1, Height - 1), Ui.S(12), Ui.Rail);
            TextRenderer.DrawText(g, Caption, Ui.F8, new Point(Ui.S(16), Ui.S(12)), Ui.Muted);
            TextRenderer.DrawText(g, Text, BigNumFont, new Rectangle(Ui.S(14), Ui.S(32), Width - Ui.S(20), Height - Ui.S(36)), ValueColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    // 列表还空着时的提示：虚线圆角框 + 静态靛蓝小圆点（不再常驻 40ms/25FPS 定时器反复唤醒天工主消息循环）。
    public sealed class EmptyHint : Control {
        static readonly Color HaloColor = Color.FromArgb(46, Ui.Accent);
        public EmptyHint(string text){
            Text = text; Height = Ui.S(60); Font = Ui.F9;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
        protected override void OnPaint(PaintEventArgs e){
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Ui.Bg);
            var r = new Rectangle(0, Ui.S(3), Width - 1, Height - Ui.S(7));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Ui.Rounded(r, Ui.S(10))) using (var pen = new Pen(Color.FromArgb(0xD4, 0xD4, 0xD8), 1.5f) { DashStyle = DashStyle.Dash }) g.DrawPath(pen, path);
            int cy = r.Y + r.Height / 2;
            int hs = Ui.S(16), ds = Ui.S(8);
            g.FillEllipse(Ui.CachedBrush(HaloColor), Ui.S(14), cy - hs / 2, hs, hs);
            g.FillEllipse(Ui.CachedBrush(Ui.Accent), Ui.S(18), cy - ds / 2, ds, ds);
            g.SmoothingMode = SmoothingMode.Default;
            TextRenderer.DrawText(g, Text, Font, new Rectangle(Ui.S(40), r.Y, Width - Ui.S(50), r.Height), Ui.Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    // 圆角色块容器（「要改 6 个孔 → M6 螺纹孔」这种醒目的说明块）
    public sealed class RoundBox : Panel {
        public Color Fill = Ui.Rail;
        public int Radius = Ui.S(10);
        public RoundBox(){ SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); }
        protected override void OnPaint(PaintEventArgs e){
            e.Graphics.Clear(Parent != null ? Parent.BackColor : Ui.Bg);
            Ui.FillRound(e.Graphics, new Rectangle(0, 0, Width - 1, Height - 1), Radius, Fill);
        }
    }

    // 细进度条（10px 高、圆头、靛蓝），值变了宽度滑过去。用法和 ProgressBar 一样：Maximum / Value。
    public sealed class SlimProgress : Control {
        int max = 100, val;
        float shownFrac;
        public SlimProgress(){
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = Ui.S(10);
        }
        public int Maximum { get { return max; } set { max = Math.Max(1, value); Slide(); } }
        public int Value { get { return val; } set { val = Math.Max(0, Math.Min(max, value)); Slide(); } }
        void Slide(){
            float to = (float)val / max;
            if (Math.Abs(to - shownFrac) < 0.0001f) return;
            shownFrac = to;
            Invalidate();
        }
        protected override void OnPaint(PaintEventArgs e){
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Ui.Bg);
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            Ui.FillRound(g, r, Height / 2, Ui.AccentSoft);
            int w = (int)Math.Round(r.Width * shownFrac);
            if (w >= Height) Ui.FillRound(g, new Rectangle(0, 0, w, Height - 1), Height / 2, Ui.Accent);
        }
    }
}
