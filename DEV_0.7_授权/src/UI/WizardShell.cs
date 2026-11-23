using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace TianGongCadSuite {
    // 左侧步骤栏。两种用法：
    //   Steps：①②③④ 有先后的流程（自动打孔、批量排孔……），做完的打勾，当前步白底高亮；
    //   Nav：  分类导航（配孔检查的问题分类、Lineup 的类目），左边色点、右边数量。
    // 当前项的白块是滑过去的（240ms），做完打勾时勾弹一下（180ms）。
    public sealed class StepRail : Control {
        public enum Mode { Steps, Nav }
        public sealed class Item {
            public string Title = "", Sub = "", Count = "";
            public bool Done, Error, Dim;
            public Color Dot = Color.Empty;
            internal float Pop = 1;
        }
        public readonly List<Item> Items = new List<Item>();
        public Mode Kind = Mode.Steps;
        public event EventHandler SelectedChanged;   // 只在用户点击时触发
        int selected, hover = -1;
        float hiY = -1;

        public StepRail(){
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Ui.Rail; Cursor = Cursors.Hand;
        }
        public int ItemHeight { get { return Kind == Mode.Steps ? Ui.S(64) : Ui.S(34); } }
        int Pitch { get { return ItemHeight + Ui.S(Kind == Mode.Steps ? 4 : 2); } }
        public int PreferredHeight { get { return Items.Count * Pitch; } }

        public Item Add(string title, string sub){
            var it = new Item { Title = title, Sub = sub ?? "" };
            Items.Add(it); Invalidate();
            return it;
        }
        public int Selected {
            get { return selected; }
            set {
                int v = Math.Max(0, Math.Min(Items.Count - 1, value));
                if (v == selected && hiY >= 0) return;
                selected = v;
                hiY = v * Pitch;
                Invalidate();
            }
        }
        // 改一项的状态（即时切换，不启高频定时器干扰天工主消息循环）
        public void Set(int i, string sub, bool done, bool error){
            if (i < 0 || i >= Items.Count) return;
            var it = Items[i];
            if (it.Sub == (sub ?? "") && it.Done == done && it.Error == error) return;
            it.Sub = sub ?? ""; it.Done = done; it.Error = error; it.Pop = 1f;
            Invalidate();
        }
        public void SetCount(int i, string count, bool dim){
            if (i < 0 || i >= Items.Count) return;
            if (Items[i].Count == count && Items[i].Dim == dim) return;
            Items[i].Count = count; Items[i].Dim = dim; Invalidate();
        }

        int HitTest(Point p){
            int i = p.Y / Pitch;
            return i >= 0 && i < Items.Count && p.Y - i * Pitch < ItemHeight ? i : -1;
        }
        protected override void OnMouseMove(MouseEventArgs e){ int h = HitTest(e.Location); if (h != hover) { hover = h; Invalidate(); } base.OnMouseMove(e); }
        protected override void OnMouseLeave(EventArgs e){ hover = -1; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseClick(MouseEventArgs e){
            int h = HitTest(e.Location);
            if (h >= 0 && e.Button == MouseButtons.Left && Enabled) {
                Selected = h;
                var ev = SelectedChanged; if (ev != null) ev(this, EventArgs.Empty);
            }
            base.OnMouseClick(e);
        }

        protected override void OnPaint(PaintEventArgs e){
            var g = e.Graphics;
            g.Clear(BackColor);
            if (Items.Count == 0) return;
            if (hiY < 0) hiY = selected * Pitch;
            int w = Width - 1;
            // 当前项白块（带一点阴影）
            var hr = new Rectangle(0, (int)hiY, w, ItemHeight);
            Ui.FillRound(g, new Rectangle(hr.X, hr.Y + 1, hr.Width, hr.Height), 10, Color.FromArgb(14, 24, 24, 27));
            Ui.FillRound(g, hr, 10, Color.White);
            Ui.DrawRound(g, hr, 10, Ui.Line, 1);
            for (int i = 0; i < Items.Count; i++) {
                var it = Items[i];
                var r = new Rectangle(0, i * Pitch, w, ItemHeight);
                if (i == hover && i != selected && Enabled) Ui.FillRound(g, r, 10, Color.FromArgb(0xEF, 0xEF, 0xF1));
                if (Kind == Mode.Steps) PaintStep(g, it, i, r); else PaintNav(g, it, i, r);
            }
        }
        void PaintStep(Graphics g, Item it, int i, Rectangle r){
            bool cur = i == selected;
            int d = Ui.S(24), cx = r.X + Ui.S(12), cy = r.Y + r.Height / 2;
            var circle = new Rectangle(cx, cy - d / 2, d, d);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (it.Error) {
                using (var b = new SolidBrush(Ui.BadSoft)) g.FillEllipse(b, circle);
                TextRenderer.DrawText(g, "!", Ui.F9B, circle, Ui.Bad, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            } else if (it.Done && !cur) {
                float s = it.Pop;
                var c2 = new RectangleF(cx + d * (1 - s) / 2, cy - d * s / 2, d * s, d * s);
                using (var b = new SolidBrush(Ui.OkSoft)) g.FillEllipse(b, c2);
                Ui.DrawCheck(g, Rectangle.Round(RectangleF.Inflate(c2, -d * s * 0.22f, -d * s * 0.22f)), Ui.Ok, 1.8f);
            } else if (cur) {
                using (var b = new SolidBrush(Ui.Accent)) g.FillEllipse(b, circle);
                TextRenderer.DrawText(g, (i + 1).ToString(), Ui.F8, circle, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            } else {
                using (var p = new Pen(Ui.Faint, 1.5f)) g.DrawEllipse(p, circle.X + 0.75f, circle.Y + 0.75f, d - 1.5f, d - 1.5f);
                TextRenderer.DrawText(g, (i + 1).ToString(), Ui.F8, circle, Ui.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            g.SmoothingMode = SmoothingMode.Default;
            int tx = cx + d + Ui.S(12), tw = Math.Max(10, r.Right - tx - Ui.S(8));
            bool hasSub = it.Sub.Length > 0;
            var tr = new Rectangle(tx, hasSub ? r.Y + Ui.S(8) : r.Y, tw, hasSub ? Ui.S(24) : r.Height);
            TextRenderer.DrawText(g, it.Title, Ui.F10B, tr, cur ? Ui.Accent : (it.Done ? Ui.Text : Ui.Text2),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if (hasSub)
                TextRenderer.DrawText(g, it.Sub, Ui.F8, new Rectangle(tx, r.Y + Ui.S(34), tw, Ui.S(22)), it.Error ? Ui.Bad : Ui.Muted,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
        void PaintNav(Graphics g, Item it, int i, Rectangle r){
            bool cur = i == selected;
            int x = r.X + Ui.S(12);
            if (!it.Dot.IsEmpty) {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                int ds = Ui.S(8);
                using (var b = new SolidBrush(it.Dot)) g.FillEllipse(b, x, r.Y + (r.Height - ds) / 2, ds, ds);
                g.SmoothingMode = SmoothingMode.Default;
                x += Ui.S(18);
            }
            var cw = TextRenderer.MeasureText(it.Count, Ui.F9B).Width;
            Color fore = it.Dim && !cur ? Ui.DisabledText : (cur ? Ui.Accent : Ui.Text);
            TextRenderer.DrawText(g, it.Title, cur ? Ui.F9B : Ui.F9, new Rectangle(x, r.Y, Math.Max(10, r.Right - x - cw - Ui.S(14)), r.Height), fore,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, it.Count, cur ? Ui.F9B : Ui.F9, new Rectangle(r.Right - cw - Ui.S(12), r.Y, cw + Ui.S(4), r.Height), it.Dim ? Ui.DisabledText : (cur ? Ui.Text : Ui.Muted),
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }

    // 底部操作栏：左边次要按钮 → 中间一行状态字（吃掉剩余宽度）→ 右边按钮，主按钮放最右。
    public sealed class ActionBar : Panel {
        public readonly List<Control> Left = new List<Control>();
        public readonly List<Control> Right = new List<Control>();   // 从左到右的顺序，最后一个贴右边
        Label status = new Label();
        // 状态字：窗口可以把自己原有的状态 Label 换进来（老代码到处 status.Text = …，不用改）
        public Label Status {
            get { return status; }
            set {
                if (value == null || value == status) return;
                var old = status;
                Controls.Remove(old);
                if (old != null && !old.IsDisposed) old.Dispose();
                status = value;
                Style(status); Controls.Add(status); PerformLayout();
            }
        }
        public int Gap = Ui.S(10);
        public ActionBar(){
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            BackColor = Ui.Bg; Height = Ui.S(72);
            Padding = Ui.SPad(28, 0, 20, 0);
            Style(status); status.ForeColor = Ui.Muted;
            Controls.Add(status);
        }
        static void Style(Label l){
            l.AutoSize = false; l.AutoEllipsis = true; l.TextAlign = ContentAlignment.MiddleLeft;
            // 不透明纯底色：避免每次 SetStatus 更新文字时穿透父容器 ActionBar 引起整条底栏擦除重绘闪烁
            l.BackColor = Ui.Bg; l.Font = Ui.F8; l.Padding = new Padding(0);
        }
        public void AddLeft(Control c){ Left.Add(c); Controls.Add(c); PerformLayout(); }
        public void AddRight(Control c){ Right.Add(c); Controls.Add(c); PerformLayout(); }
        protected override void OnLayout(LayoutEventArgs e){
            base.OnLayout(e);
            int x = Padding.Left;
            foreach (var c in Left) {
                if (!c.Visible && IsHandleCreated) continue;
                var pt = new Point(x, (Height - c.Height) / 2);
                if (c.Location != pt) c.Location = pt;
                x += c.Width + Gap;
            }
            int rx = Width - Padding.Right;
            for (int i = Right.Count - 1; i >= 0; i--) {
                var c = Right[i];
                if (!c.Visible && IsHandleCreated) continue;
                rx -= c.Width;
                var pt = new Point(rx, (Height - c.Height) / 2);
                if (c.Location != pt) c.Location = pt;
                rx -= Gap;
            }
            var sr = new Rectangle(x + Ui.S(4), Ui.S(6), Math.Max(0, rx - x - Ui.S(8)), Height - Ui.S(12));
            if (status.Bounds != sr) status.Bounds = sr;
        }
        protected override void OnPaint(PaintEventArgs e){
            base.OnPaint(e);
            using (var p = new Pen(Ui.Line)) e.Graphics.DrawLine(p, 0, 0, Width, 0);
        }
    }

    // 底部弹出的提示条：深色底、白字，可带一个动作（「撤销本次打孔」）。上滑出现，停 4 秒，淡出。
    public sealed class Toast : Control {
        public string Action = "";
        public event EventHandler ActionClick;
        Color tone = Ui.Ok;
        float shown;          // 0..1（出现 / 淡出动画）
        Rectangle actionRect;
        readonly Timer life = new Timer();
        public int Rest;      // 停靠位置（父控件里底边的 Y）
        public Toast(){
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = Ui.S(42); Visible = false; Font = Ui.F9;
            life.Tick += (s, e) => { life.Stop(); Hide(true); };
        }
        public void Show(string text, Color kind, string action, int ms){
            Text = text; tone = kind; Action = action ?? "";
            life.Stop();
            Visible = true; BringToFront();
            float from = shown;
            Anim.Run(this, Anim.ToastMs, t => { shown = from + (1 - from) * (float)t; Place(); Invalidate(); }, null, null, "toast");
            if (ms > 0) { life.Interval = ms; life.Start(); }
        }
        public void Hide(bool animate){
            life.Stop();
            if (!Visible) return;
            float from = shown;
            Anim.Run(this, animate ? Anim.ToastMs : 0, t => { shown = from * (1 - (float)t); Place(); Invalidate(); }, () => { Visible = false; }, null, "toast");
        }
        void Place(){ int lift = Ui.S(12); Top = Rest - Height - (int)Math.Round(lift * shown) + lift; }
        protected override void OnMouseClick(MouseEventArgs e){
            if (Action.Length > 0 && actionRect.Contains(e.Location)) { var h = ActionClick; if (h != null) h(this, EventArgs.Empty); Hide(true); }
            else Hide(true);
            base.OnMouseClick(e);
        }
        protected override void OnMouseMove(MouseEventArgs e){ Cursor = Action.Length > 0 && actionRect.Contains(e.Location) ? Cursors.Hand : Cursors.Default; base.OnMouseMove(e); }
        protected override void Dispose(bool disposing){ if (disposing) life.Dispose(); base.Dispose(disposing); }
        protected override void OnPaint(PaintEventArgs e){
            var g = e.Graphics;
            var back = Parent != null ? Parent.BackColor : Ui.Bg;
            g.Clear(back);
            var dark = Color.FromArgb(0x18, 0x18, 0x1B);
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            Ui.FillRound(g, r, Ui.S(10), Anim.Mix(back, dark, shown));
            var white = Anim.Mix(back, Color.White, shown);
            int x = Ui.S(16);
            Color mark = tone == Ui.Ok ? Color.FromArgb(0x86, 0xEF, 0xAC) : (tone == Ui.Bad ? Color.FromArgb(0xFC, 0xA5, 0xA5) : (tone == Ui.Warn ? Color.FromArgb(0xFD, 0xE6, 0x8A) : Color.FromArgb(0xC7, 0xD2, 0xFE)));
            mark = Anim.Mix(back, mark, shown);
            int cs = Ui.S(14), ds = Ui.S(8);
            if (tone == Ui.Ok) Ui.DrawCheck(g, new Rectangle(x, Height / 2 - cs / 2, cs, cs), mark, 1.8f);
            else using (var b = new SolidBrush(mark)) { g.SmoothingMode = SmoothingMode.AntiAlias; g.FillEllipse(b, x + Ui.S(3), Height / 2 - ds / 2, ds, ds); g.SmoothingMode = SmoothingMode.Default; }
            x += Ui.S(26);
            int aw = Action.Length > 0 ? TextRenderer.MeasureText(Action, Ui.F9B).Width + Ui.S(16) : 0;
            actionRect = new Rectangle(Width - aw - Ui.S(8), 0, aw, Height);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(x, 0, Math.Max(10, Width - x - aw - Ui.S(16)), Height), white,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if (aw > 0)
                TextRenderer.DrawText(g, Action, Ui.F9B, actionRect, Anim.Mix(back, Color.FromArgb(0xC7, 0xD2, 0xFE), shown),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }

    // 整窗骨架：
    // ┌ 侧栏（浅灰）──────┬ 第 n 步，共 m 步 ───────────────── [标题行按钮] ┐
    // │ [RailTop]         │ 步骤标题（22px 粗体）                            │
    // │ ① 参考孔  ✓       │                                                   │
    // │ ② 打孔面  ✓       │   当前页（切换时：旧页淡出，新页从右 16px 滑入）│
    // │ ③ 孔规格  ●       │                                                   │
    // │ ④ 预览并打孔      │                                     [提示条]      │
    // │ [RailBottom]      ├───────────────────────────────────────────────────┤
    // │                   │ [上一步] [更多▾]  状态字 ……          [主按钮]     │
    // └───────────────────┴───────────────────────────────────────────────────┘
    public sealed class WizardShell : Panel {
        public sealed class Page {
            public string Title = "";
            public Panel Body;
            public readonly List<Control> HeaderControls = new List<Control>();
        }
        public readonly StepRail Rail = new StepRail();
        public readonly ActionBar Footer = new ActionBar();
        public readonly Toast Toast = new Toast();
        public readonly List<Page> Pages = new List<Page>();
        public int RailWidth = Ui.S(246);
        public bool StepCaption = true;   // 标题上方的「第 n 步，共 m 步」；false = 单页窗口（配孔检查、属性卡……），侧栏里也不列步骤
        public event EventHandler PageChanged;

        readonly Panel railPanel = new RailPanel();
        readonly Panel content = new Panel();
        readonly Panel header = new Panel();
        readonly Label stepLabel = new Label();
        readonly Label titleLabel = new Label();
        readonly Label subLabel = new Label();
        readonly Panel host = new Panel();
        Control railTop, railBottom;
        int current = -1;
        PageOverlay overlay;

        public WizardShell(){
            Dock = DockStyle.Fill; BackColor = Ui.Bg;
            Ui.EnableDoubleBuffer(this);
            Ui.EnableDoubleBuffer(content);
            Ui.EnableDoubleBuffer(header);
            Ui.EnableDoubleBuffer(host);
            railPanel.BackColor = Ui.Rail;
            railPanel.Controls.Add(Rail);
            content.BackColor = Ui.Bg;
            stepLabel.AutoSize = false; stepLabel.Font = Ui.F8; stepLabel.ForeColor = Ui.Muted; stepLabel.BackColor = Ui.Bg;
            titleLabel.AutoSize = false; titleLabel.Font = Ui.Title; titleLabel.ForeColor = Ui.Text; titleLabel.BackColor = Ui.Bg; titleLabel.AutoEllipsis = true;
            titleLabel.TextAlign = ContentAlignment.BottomLeft;
            subLabel.AutoSize = false; subLabel.Font = Ui.F8; subLabel.ForeColor = Ui.Muted; subLabel.BackColor = Ui.Bg; subLabel.AutoEllipsis = true;
            subLabel.TextAlign = ContentAlignment.BottomLeft;
            header.BackColor = Ui.Bg;
            header.Controls.Add(stepLabel); header.Controls.Add(titleLabel); header.Controls.Add(subLabel);
            host.BackColor = Ui.Bg;
            content.Controls.Add(Toast);
            content.Controls.Add(header); content.Controls.Add(host); content.Controls.Add(Footer);
            Controls.Add(railPanel); Controls.Add(content);
            Rail.SelectedChanged += (s, e) => Go(Rail.Selected);
            header.Resize += (s, e) => LayoutHeader();
            content.Resize += (s, e) => LayoutContent();
        }

        // 侧栏顶部 / 底部放的东西（「开始检查」按钮、关联孔位小卡片……）
        public Control RailTop { get { return railTop; } set { if (railTop != null) railPanel.Controls.Remove(railTop); railTop = value; if (value != null) railPanel.Controls.Add(value); LayoutRail(); } }
        public Control RailBottom { get { return railBottom; } set { if (railBottom != null) railPanel.Controls.Remove(railBottom); railBottom = value; if (value != null) railPanel.Controls.Add(value); LayoutRail(); } }

        public Page AddPage(string railTitle, string title, string railSub){
            var body = new Panel { BackColor = Ui.Bg, Visible = false };
            Ui.EnableDoubleBuffer(body);
            var p = new Page { Title = title, Body = body };
            Pages.Add(p);
            host.Controls.Add(p.Body);
            if (StepCaption) Rail.Add(railTitle, railSub); else Rail.Visible = false;
            if (current < 0) Go(0, false);
            stepLabel.Text = StepCaption && Pages.Count > 1 ? "第 " + (current + 1) + " 步，共 " + Pages.Count + " 步" : "";
            LayoutHeader(); LayoutContent();
            return p;
        }
        public void AddHeader(Page p, Control c){ p.HeaderControls.Add(c); c.Visible = Pages.IndexOf(p) == current; header.Controls.Add(c); LayoutHeader(); }

        public int Current { get { return current; } }
        public Page CurrentPage { get { return current >= 0 ? Pages[current] : null; } }

        // 只有一页、标题随内容变的窗口（配孔检查「发现 3 处问题」）
        public void SetTitle(string title, string sub){
            if (current >= 0) Pages[current].Title = title;
            titleLabel.Text = title; subLabel.Text = sub ?? ""; LayoutHeader();
        }

        public void Go(int index){ Go(index, true); }
        public void Go(int index, bool animate){
            if (index < 0 || index >= Pages.Count) return;
            Rail.Selected = index;
            if (index == current) return;
            int old = current;
            // 不再做两次全树 DrawToBitmap + CPU ColorMatrix Alpha 混合（大页面会冻住 80~400ms）；
            // 用 WM_SETREDRAW 合帧瞬时切换，且按需同步新页尺寸，彻底消除切步骤卡顿与白闪。
            bool frozen = Ui.BeginRedrawFreeze(content);
            try {
                if (old >= 0) {
                    Pages[old].Body.Visible = false;
                    foreach (var c in Pages[old].HeaderControls) c.Visible = false;
                }
                current = index;
                var p = Pages[index];
                var bodyRect = new Rectangle(0, 0, host.Width, host.Height);
                if (p.Body.Bounds != bodyRect) p.Body.Bounds = bodyRect;
                p.Body.Visible = true; p.Body.BringToFront();
                foreach (var c in p.HeaderControls) c.Visible = true;
                stepLabel.Text = StepCaption && Pages.Count > 1 ? "第 " + (index + 1) + " 步，共 " + Pages.Count + " 步" : "";
                titleLabel.Text = p.Title; subLabel.Text = "";
                LayoutHeader(); LayoutContent();
            } finally {
                Ui.EndRedrawFreeze(content, frozen);
            }
            var h = PageChanged; if (h != null) h(this, EventArgs.Empty);
        }

        public void ShowToast(string text, Color kind, string action, EventHandler onAction){
            Toast.ActionClick -= toastHandler;
            toastHandler = onAction;
            if (onAction != null) Toast.ActionClick += onAction;
            Toast.Show(text, kind, action, 4000);
        }
        EventHandler toastHandler;

        static Bitmap Snap(Control c){
            var b = new Bitmap(c.Width, c.Height);
            try { c.DrawToBitmap(b, new Rectangle(0, 0, c.Width, c.Height)); } catch { b.Dispose(); return null; }
            return b;
        }
        void Transition(Bitmap before, bool forward){
            if (overlay != null) { content.Controls.Remove(overlay); overlay.Dispose(); overlay = null; }
            var after = Snap(host);
            if (after == null) { before.Dispose(); return; }
            var ov = overlay = new PageOverlay(before, after, forward) { Bounds = host.Bounds };
            content.Controls.Add(ov); ov.BringToFront(); Toast.BringToFront();
            Anim.Run(ov, Anim.Page, t => { ov.T = (float)t; ov.Invalidate(); }, () => {
                if (overlay == ov) overlay = null;
                content.Controls.Remove(ov); ov.Dispose();
            }, null, "page");
        }

        protected override void OnLayout(LayoutEventArgs e){
            base.OnLayout(e);
            railPanel.SetBounds(0, 0, RailWidth, Height);
            content.SetBounds(RailWidth, 0, Math.Max(0, Width - RailWidth), Height);
            LayoutRail();
        }
        void LayoutRail(){
            int pad = Ui.S(12);
            int w = RailWidth - pad * 2, y = Ui.S(18);
            if (railTop != null) { railTop.SetBounds(pad, y, w, railTop.Height); y += railTop.Height + Ui.S(12); }
            int bottom = Height - Ui.S(14);
            if (railBottom != null) { bottom -= railBottom.Height; railBottom.SetBounds(pad, bottom, w, railBottom.Height); bottom -= Ui.S(10); }
            Rail.SetBounds(pad, y, w, Math.Max(0, bottom - y));
        }
        void LayoutContent(){
            int w = content.Width, h = content.Height;
            bool cap = stepLabel.Text.Length > 0;
            int hh = cap ? Ui.S(84) : Ui.S(64);
            header.SetBounds(0, 0, w, hh);
            Footer.SetBounds(0, Math.Max(hh, h - Footer.Height), w, Footer.Height);
            int lpad = Ui.S(28), rpad = Ui.S(24), topGap = Ui.S(12), botGap = Ui.S(10);
            var hostRect = new Rectangle(lpad, hh + topGap, Math.Max(0, w - lpad - rpad), Math.Max(0, Footer.Top - hh - topGap - botGap));
            if (host.Bounds != hostRect) host.Bounds = hostRect;
            // 仅调整当前可见页尺寸，避免窗口拖动缩放时同步重排全部隐藏步骤页
            if (current >= 0 && current < Pages.Count) {
                var bodyRect = new Rectangle(0, 0, host.Width, host.Height);
                if (Pages[current].Body.Bounds != bodyRect) Pages[current].Body.Bounds = bodyRect;
            }
            if (overlay != null) overlay.Bounds = host.Bounds;
            Toast.Rest = Footer.Top - Ui.S(12);
            Toast.SetBounds(lpad, Toast.Top, Math.Max(0, w - lpad - rpad), Toast.Height);
            if (!Toast.Visible) Toast.Top = Toast.Rest - Toast.Height;
        }
        void LayoutHeader(){
            int w = header.Width, h = header.Height;
            bool cap = stepLabel.Text.Length > 0;
            int rx = w - Ui.S(20);
            int lpad = Ui.S(28);
            if (current >= 0)
                for (int i = Pages[current].HeaderControls.Count - 1; i >= 0; i--) {
                    var c = Pages[current].HeaderControls[i];
                    rx -= c.Width; c.Location = new Point(rx, Math.Max(Ui.S(8), h - c.Height - Ui.S(4))); rx -= Ui.S(4);
                }
            stepLabel.SetBounds(lpad, Ui.S(16), Math.Max(0, w - lpad * 2), cap ? Ui.S(22) : 0);
            var tw = TextRenderer.MeasureText(titleLabel.Text ?? "", Ui.Title).Width + Ui.S(10);
            int top = cap ? Ui.S(38) : Ui.S(16);
            titleLabel.SetBounds(lpad, top, Math.Max(0, Math.Min(tw, rx - lpad - Ui.S(8))), Math.Max(Ui.S(28), h - top));
            int sx = titleLabel.Right + Ui.S(8);
            int subH = Ui.S(22);
            subLabel.SetBounds(sx, Math.Max(top, h - subH - Ui.S(6)), Math.Max(0, rx - sx - Ui.S(8)), subH);
        }

        // 侧栏底色 + 右边一条分隔线
        sealed class RailPanel : Panel {
            public RailPanel(){ SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer, true); }
            protected override void OnPaint(PaintEventArgs e){
                base.OnPaint(e);
                using (var p = new Pen(Ui.Line)) e.Graphics.DrawLine(p, Width - 1, 0, Width - 1, Height);
            }
        }
        // 切步骤时盖在页面上的一张图：旧页淡出，新页从右（后退时从左）16px 滑入
        sealed class PageOverlay : Control {
            readonly Bitmap before, after; readonly bool forward;
            public float T;
            public PageOverlay(Bitmap before, Bitmap after, bool forward){
                this.before = before; this.after = after; this.forward = forward;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            }
            protected override void OnPaint(PaintEventArgs e){
                var g = e.Graphics;
                g.Clear(Ui.Bg);
                float t = T;
                using (var ia = new System.Drawing.Imaging.ImageAttributes()) {
                    var cm = new System.Drawing.Imaging.ColorMatrix { Matrix33 = Math.Max(0, 1 - t * 1.6f) };
                    ia.SetColorMatrix(cm);
                    g.DrawImage(before, new Rectangle(0, 0, before.Width, before.Height), 0, 0, before.Width, before.Height, GraphicsUnit.Pixel, ia);
                    cm.Matrix33 = t; ia.SetColorMatrix(cm);
                    int dx = (int)Math.Round((forward ? 16 : -16) * (1 - t));
                    g.DrawImage(after, new Rectangle(dx, 0, after.Width, after.Height), 0, 0, after.Width, after.Height, GraphicsUnit.Pixel, ia);
                }
            }
            protected override void Dispose(bool disposing){ if (disposing) { before.Dispose(); after.Dispose(); } base.Dispose(disposing); }
        }
    }

    // 侧栏底部的小信息卡（关联孔位、输出目录、预设……）：白底细边，里面自己放控件。
    public sealed class RailCard : Panel {
        public string Title = "";
        public RailCard(string title, int height){
            Title = title; Height = Ui.S(height);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.White;
            Padding = Ui.SPad(12, 36, 12, 10);
        }
        protected override void OnPaint(PaintEventArgs e){
            var g = e.Graphics;
            g.Clear(Ui.Rail);
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            Ui.FillRound(g, r, 10, Color.White);
            Ui.DrawRound(g, r, 10, Ui.Line, 1);
            TextRenderer.DrawText(g, Title, Ui.F9B, new Point(Ui.S(12), Ui.S(10)), Ui.Text);
        }
    }
}
