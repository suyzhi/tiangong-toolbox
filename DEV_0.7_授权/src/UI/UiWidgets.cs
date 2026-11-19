using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace TianGongCadSuite {
    // 一排可点的组标签（孔规格页上：「Φ6.6 ×4  M6 螺纹孔」「Φ5 ×2  M6 圆柱沉孔」），点一个 = 编辑那一组。
    public sealed class ChipTabs : Control {
        readonly List<string> heads = new List<string>(), subs = new List<string>();
        readonly List<Rectangle> rects = new List<Rectangle>();
        float[] heat = new float[0];
        int selected = -1, hover = -1;
        public string EmptyText = "";
        public event EventHandler SelectedChanged;   // 只在用户点击时触发
        public ChipTabs(){
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = 42; BackColor = Ui.Bg; Cursor = Cursors.Hand;
        }
        public int Count { get { return heads.Count; } }
        public void SetItems(IList<string> h, IList<string> sub, int sel){
            heads.Clear(); heads.AddRange(h); subs.Clear(); subs.AddRange(sub);
            var nh = new float[heads.Count];
            for (int i = 0; i < nh.Length; i++) nh[i] = i == sel ? 1 : 0;
            heat = nh; selected = sel; Invalidate();
        }
        public int Selected {
            get { return selected; }
            set {
                if (value == selected) return;
                int old = selected; selected = value;
                var h = heat;
                Anim.Run(this, Anim.Select, t => {
                    for (int i = 0; i < h.Length; i++) {
                        if (i == selected) h[i] = Math.Max(h[i], (float)t);
                        else if (i == old) h[i] = Math.Min(h[i], 1 - (float)t);
                        else h[i] = 0;
                    }
                    Invalidate();
                }, null, null, "tabs");
            }
        }
        protected override void OnMouseMove(MouseEventArgs e){ int h = Hit(e.Location); if (h != hover) { hover = h; Invalidate(); } base.OnMouseMove(e); }
        protected override void OnMouseLeave(EventArgs e){ hover = -1; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseClick(MouseEventArgs e){
            int h = Hit(e.Location);
            if (h >= 0 && Enabled) { Selected = h; var ev = SelectedChanged; if (ev != null) ev(this, EventArgs.Empty); }
            base.OnMouseClick(e);
        }
        int Hit(Point p){ for (int i = 0; i < rects.Count; i++) if (rects[i].Contains(p)) return i; return -1; }
        protected override void OnPaint(PaintEventArgs e){
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Ui.Bg);
            rects.Clear();
            if (heads.Count == 0) {
                TextRenderer.DrawText(g, EmptyText, Ui.F9, new Rectangle(0, 0, Width, Height), Ui.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                return;
            }
            int x = 0;
            for (int i = 0; i < heads.Count; i++) {
                int hw = TextRenderer.MeasureText(heads[i], Ui.NumB).Width, sw = TextRenderer.MeasureText(subs[i], Ui.F9).Width;
                var r = new Rectangle(x, 1, hw + sw + 34, Height - 3);
                rects.Add(r);
                float k = i < heat.Length ? heat[i] : 0;
                Ui.FillRound(g, r, 10, Anim.Mix(i == hover ? Ui.Subtle : Color.White, Ui.AccentSoft, k));
                Ui.DrawRound(g, r, 10, Anim.Mix(Ui.Line, Ui.Accent, k), 1 + k * 0.5f);
                TextRenderer.DrawText(g, heads[i], Ui.NumB, new Point(r.X + 12, r.Y + (r.Height - 18) / 2), Ui.Text, TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, subs[i], k > 0.5f ? Ui.F9B : Ui.F9, new Point(r.X + 12 + hw + 10, r.Y + (r.Height - 18) / 2), Anim.Mix(Ui.Muted, Ui.Accent, k), TextFormatFlags.NoPadding);
                x = r.Right + 8;
                if (x > Width) break;
            }
        }
    }

    // 数字小方块（「要打的孔 6」「零件 2」）：浅灰底，上面一行小字，下面一个大数字。
    public sealed class StatTile : Control {
        public string Caption = "";
        public Color ValueColor = Ui.Text;
        public StatTile(string caption){
            Caption = caption; Text = "0";
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = 76;
        }
        public void Set(string value, Color color){ if (Text == value && ValueColor == color) return; Text = value; ValueColor = color; Invalidate(); }
        protected override void OnPaint(PaintEventArgs e){
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Ui.Bg);
            Ui.FillRound(g, new Rectangle(0, 0, Width - 1, Height - 1), 12, Ui.Rail);
            TextRenderer.DrawText(g, Caption, Ui.F8, new Point(16, 12), Ui.Muted);
            using (var f = new Font(Ui.Mono, 18F, FontStyle.Bold))
                TextRenderer.DrawText(g, Text, f, new Rectangle(14, 30, Width - 20, Height - 34), ValueColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    // 列表还空着时的提示：虚线圆角框 + 一个慢慢呼吸的小圆点（「正在等你在模型里点孔…」）。
    public sealed class EmptyHint : Control {
        readonly Timer pulse = new Timer { Interval = 40 };
        float phase;
        public EmptyHint(string text){
            Text = text; Height = 58; Font = Ui.F9;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            pulse.Tick += (s, e) => { phase += 0.04f; if (phase > 1) phase -= 1; Invalidate(new Rectangle(0, 0, 40, Height)); };
        }
        protected override void OnVisibleChanged(EventArgs e){
            base.OnVisibleChanged(e);
            if (Visible && Anim.On) pulse.Start(); else pulse.Stop();
        }
        protected override void Dispose(bool disposing){ if (disposing) pulse.Dispose(); base.Dispose(disposing); }
        protected override void OnPaint(PaintEventArgs e){
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Ui.Bg);
            var r = new Rectangle(0, 3, Width - 1, Height - 7);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Ui.Rounded(r, 10)) using (var pen = new Pen(Color.FromArgb(0xD4, 0xD4, 0xD8), 1.5f) { DashStyle = DashStyle.Dash }) g.DrawPath(pen, path);
            double k = 0.5 + 0.5 * Math.Sin(phase * Math.PI * 2);
            int cy = r.Y + r.Height / 2;
            using (var halo = new SolidBrush(Color.FromArgb((int)(60 * k), Ui.Accent))) g.FillEllipse(halo, 14, cy - 8, 16, 16);
            using (var dot = new SolidBrush(Ui.Accent)) g.FillEllipse(dot, 18, cy - 4, 8, 8);
            g.SmoothingMode = SmoothingMode.Default;
            TextRenderer.DrawText(g, Text, Font, new Rectangle(40, r.Y, Width - 50, r.Height), Ui.Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    // 圆角色块容器（「要改 6 个孔 → M6 螺纹孔」这种醒目的说明块）
    public sealed class RoundBox : Panel {
        public Color Fill = Ui.Rail;
        public int Radius = 10;
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
            Height = 10;
        }
        public int Maximum { get { return max; } set { max = Math.Max(1, value); Slide(); } }
        public int Value { get { return val; } set { val = Math.Max(0, Math.Min(max, value)); Slide(); } }
        void Slide(){
            float to = (float)val / max, from = shownFrac;
            if (Math.Abs(to - from) < 0.0001f) return;
            Anim.Run(this, 300, t => { shownFrac = from + (to - from) * (float)t; Invalidate(); }, null, null, "bar");
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
