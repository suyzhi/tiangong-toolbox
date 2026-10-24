using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace TianGongCadSuite {
    // ---------------------------------------------------------------------------------------------
    // 0.9.0「D · 侧栏向导」的骨架：动效引擎 Anim、左侧步骤栏 StepRail、整窗骨架 WizardShell、
    // 底部操作栏 ActionBar、提示条 Toast、列表新增项闪一下 Flash。
    //
    // 原则：动效只管"看起来"，状态一律同步改完。切步骤时页面立刻换好，动画只是盖在上面的一张图；
    // 测试、脚本、PrintWindow 截图读到的永远是最终状态。系统关了动画 / 远程桌面 / ui.tsv 里 motion=0 时全部瞬变。
    // ---------------------------------------------------------------------------------------------

    public static class Anim {
        sealed class Tween {
            public Control Owner; public string Key;
            public DateTime Start; public int Ms;
            public Action<double> Step; public Action Done; public Func<double, double> Ease;
        }
        static readonly List<Tween> live = new List<Tween>();
        static Timer timer;
        static int setting = -1;   // -1 = 还没读；0 = 关；1 = 开
        public static bool ForceOff;   // 测试 / 截图工具用

        // 时长（毫秒），跟设计规范一一对应
        public const int Hover = 120, Select = 180, Page = 240, Pop = 180, FlashMs = 800, ToastMs = 200;

        public static bool On {
            get {
                if (ForceOff) return false;
                if (setting < 0) {
                    setting = 1;
                    try {
                        foreach (var r in SettingsFile.Read(SettingsFile.PathOf("ui.tsv")))
                            if (r.Length >= 2 && r[0] == "motion") setting = r[1] == "0" ? 0 : 1;
                    } catch (Exception e) { Log.Write("UiMotionSetting", e.Message); }
                }
                try { return setting == 1 && SystemInformation.UIEffectsEnabled && !SystemInformation.TerminalServerSession; }
                catch { return false; }
            }
        }

        public static double EaseOut(double t){ double u = 1 - t; return 1 - u * u * u; }
        public static double Linear(double t){ return t; }
        // 打勾弹一下：先冲过头一点再回来
        public static double Back(double t){ const double c1 = 1.70158, c3 = c1 + 1; double u = t - 1; return 1 + c3 * u * u * u + c1 * u * u; }

        public static Color Mix(Color a, Color b, double t){
            if (t <= 0) return a; if (t >= 1) return b;
            return Color.FromArgb((int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        // 跑一段动画：step(进度 0..1，已过缓动)。同一个 owner + key 的旧动画会被新的顶掉。
        // 动画关着、控件还没句柄、已经销毁：直接 step(1) + done()，结果和动画跑完一样。
        public static void Run(Control owner, int ms, Action<double> step, Action done = null, Func<double, double> ease = null, string key = null){
            if (owner == null || step == null) return;
            if (key != null) Cancel(owner, key);
            if (!On || ms <= 0 || owner.IsDisposed || !owner.IsHandleCreated || !owner.Visible) {
                Safe(step, 1); if (done != null) Safe(done); return;
            }
            live.Add(new Tween { Owner = owner, Key = key, Start = DateTime.Now, Ms = ms, Step = step, Done = done, Ease = ease ?? EaseOut });
            if (timer == null) { timer = new Timer { Interval = 15 }; timer.Tick += Tick; }
            if (!timer.Enabled) timer.Start();
        }
        public static void Cancel(Control owner, string key){
            for (int i = live.Count - 1; i >= 0; i--) if (live[i].Owner == owner && live[i].Key == key) live.RemoveAt(i);
        }
        public static bool Running(Control owner, string key){
            foreach (var t in live) if (t.Owner == owner && t.Key == key) return true;
            return false;
        }

        static void Tick(object sender, EventArgs e){
            var now = DateTime.Now;
            foreach (var t in live.ToArray()) {
                if (!live.Contains(t)) continue;
                if (t.Owner.IsDisposed) { live.Remove(t); continue; }
                double p = Math.Min(1, (now - t.Start).TotalMilliseconds / t.Ms);
                Safe(t.Step, t.Ease(p));
                if (p >= 1) { live.Remove(t); if (t.Done != null) Safe(t.Done); }
            }
            if (live.Count == 0) timer.Stop();
        }
        static void Safe(Action<double> a, double v){ try { a(v); } catch (Exception ex) { Log.Write("UiMotion", ex.Message); } }
        static void Safe(Action a){ try { a(); } catch (Exception ex) { Log.Write("UiMotion", ex.Message); } }
    }

    // 列表新增项闪一下：底色从浅靛蓝 800ms 褪成白。自绘列表在画每一项时问 Back(项, 原底色)。
    public sealed class Flash {
        readonly Control owner;
        readonly Dictionary<object, DateTime> marks = new Dictionary<object, DateTime>();
        public Flash(Control owner){ this.owner = owner; }
        public void Mark(object item){
            if (item == null || !Anim.On) return;
            marks[item] = DateTime.Now;
            Anim.Run(owner, Anim.FlashMs + 40, t => owner.Invalidate(), () => { marks.Clear(); owner.Invalidate(); }, Anim.Linear, "flash");
        }
        public Color Back(object item, Color normal){
            DateTime at;
            if (item == null || !marks.TryGetValue(item, out at)) return normal;
            double t = (DateTime.Now - at).TotalMilliseconds / Anim.FlashMs;
            if (t >= 1) return normal;
            return Anim.Mix(Ui.AccentRing, normal, Anim.EaseOut(t));
        }
    }

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
        public int ItemHeight { get { return Kind == Mode.Steps ? 62 : 36; } }
        int Pitch { get { return ItemHeight + (Kind == Mode.Steps ? 4 : 2); } }
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
                float to = v * Pitch, from = hiY < 0 ? to : hiY;
                Anim.Run(this, Anim.Page, t => { hiY = from + (to - from) * (float)t; Invalidate(); }, null, null, "hi");
            }
        }
        // 改一项的状态；从"没做完"变成"做完"时勾弹一下
        public void Set(int i, string sub, bool done, bool error){
            if (i < 0 || i >= Items.Count) return;
            var it = Items[i];
            bool popNow = done && !it.Done;
            if (it.Sub == (sub ?? "") && it.Done == done && it.Error == error) return;
            it.Sub = sub ?? ""; it.Done = done; it.Error = error;
            if (popNow && IsHandleCreated) Anim.Run(this, Anim.Pop, t => { it.Pop = 0.6f + 0.4f * (float)t; Invalidate(); }, null, Anim.Back, "pop" + i);
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
            int d = 24, cx = r.X + 12, cy = r.Y + r.Height / 2;
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
            int tx = cx + d + 12, tw = Math.Max(10, r.Right - tx - 8);
            bool hasSub = it.Sub.Length > 0;
            var tr = new Rectangle(tx, hasSub ? r.Y + 10 : r.Y, tw, hasSub ? 22 : r.Height);
            TextRenderer.DrawText(g, it.Title, Ui.F10B, tr, cur ? Ui.Accent : (it.Done ? Ui.Text : Ui.Text2),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if (hasSub)
                TextRenderer.DrawText(g, it.Sub, Ui.F8, new Rectangle(tx, r.Y + 32, tw, 20), it.Error ? Ui.Bad : Ui.Muted,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
        void PaintNav(Graphics g, Item it, int i, Rectangle r){
            bool cur = i == selected;
            int x = r.X + 12;
            if (!it.Dot.IsEmpty) {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var b = new SolidBrush(it.Dot)) g.FillEllipse(b, x, r.Y + r.Height / 2 - 4, 8, 8);
                g.SmoothingMode = SmoothingMode.Default;
                x += 18;
            }
            var cw = TextRenderer.MeasureText(it.Count, Ui.F9).Width;
            Color fore = it.Dim && !cur ? Ui.DisabledText : (cur ? Ui.Accent : Ui.Text);
            TextRenderer.DrawText(g, it.Title, cur ? Ui.F9B : Ui.F9, new Rectangle(x, r.Y, Math.Max(10, r.Right - x - cw - 14), r.Height), fore,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, it.Count, cur ? Ui.F9B : Ui.F9, new Rectangle(r.Right - cw - 12, r.Y, cw + 4, r.Height), it.Dim ? Ui.DisabledText : (cur ? Ui.Text : Ui.Muted),
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
                Controls.Remove(status); status = value;
                Style(status); Controls.Add(status); PerformLayout();
            }
        }
        public int Gap = 10;
        public ActionBar(){
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer, true);
            BackColor = Ui.Bg; Height = 72;
            Padding = new Padding(28, 0, 20, 0);
            Style(status); status.ForeColor = Ui.Muted;
            Controls.Add(status);
        }
        static void Style(Label l){
            l.AutoSize = false; l.AutoEllipsis = true; l.TextAlign = ContentAlignment.MiddleLeft;
            l.BackColor = Color.Transparent; l.Font = Ui.F8; l.Padding = new Padding(0);
        }
        public void AddLeft(Control c){ Left.Add(c); Controls.Add(c); PerformLayout(); }
        public void AddRight(Control c){ Right.Add(c); Controls.Add(c); PerformLayout(); }
        protected override void OnLayout(LayoutEventArgs e){
            base.OnLayout(e);
            int x = Padding.Left;
            foreach (var c in Left) { if (!c.Visible && IsHandleCreated) continue; c.Location = new Point(x, (Height - c.Height) / 2); x += c.Width + Gap; }
            int rx = Width - Padding.Right;
            for (int i = Right.Count - 1; i >= 0; i--) {
                var c = Right[i];
                if (!c.Visible && IsHandleCreated) continue;
                rx -= c.Width;
                c.Location = new Point(rx, (Height - c.Height) / 2);
                rx -= Gap;
            }
            status.SetBounds(x + 4, 6, Math.Max(0, rx - x - 8), Height - 12);
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
            Height = 42; Visible = false; Font = Ui.F9;
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
        void Place(){ Top = Rest - Height - (int)Math.Round(12 * shown) + 12; }
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
            Ui.FillRound(g, r, 10, Anim.Mix(back, dark, shown));
            var white = Anim.Mix(back, Color.White, shown);
            int x = 16;
            Color mark = tone == Ui.Ok ? Color.FromArgb(0x86, 0xEF, 0xAC) : (tone == Ui.Bad ? Color.FromArgb(0xFC, 0xA5, 0xA5) : (tone == Ui.Warn ? Color.FromArgb(0xFD, 0xE6, 0x8A) : Color.FromArgb(0xC7, 0xD2, 0xFE)));
            mark = Anim.Mix(back, mark, shown);
            if (tone == Ui.Ok) Ui.DrawCheck(g, new Rectangle(x, Height / 2 - 7, 14, 14), mark, 1.8f);
            else using (var b = new SolidBrush(mark)) { g.SmoothingMode = SmoothingMode.AntiAlias; g.FillEllipse(b, x + 3, Height / 2 - 4, 8, 8); g.SmoothingMode = SmoothingMode.Default; }
            x += 26;
            int aw = Action.Length > 0 ? TextRenderer.MeasureText(Action, Ui.F9B).Width + 16 : 0;
            actionRect = new Rectangle(Width - aw - 8, 0, aw, Height);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(x, 0, Math.Max(10, Width - x - aw - 16), Height), white,
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
        public int RailWidth = 232;
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
            var p = new Page { Title = title, Body = new Panel { BackColor = Ui.Bg, Visible = false } };
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
            Bitmap before = null;
            if (animate && old >= 0 && Anim.On && IsHandleCreated && host.Width > 0 && host.Height > 0) before = Snap(host);
            if (old >= 0) { Pages[old].Body.Visible = false; foreach (var c in Pages[old].HeaderControls) c.Visible = false; }
            current = index;
            var p = Pages[index];
            p.Body.Visible = true; p.Body.BringToFront();
            foreach (var c in p.HeaderControls) c.Visible = true;
            stepLabel.Text = StepCaption && Pages.Count > 1 ? "第 " + (index + 1) + " 步，共 " + Pages.Count + " 步" : "";
            titleLabel.Text = p.Title; subLabel.Text = "";
            LayoutHeader(); LayoutContent();
            if (before != null) Transition(before, index > old);
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
            int w = RailWidth - 24, y = 20;
            if (railTop != null) { railTop.SetBounds(12, y, w, railTop.Height); y += railTop.Height + 14; }
            int bottom = Height - 16;
            if (railBottom != null) { bottom -= railBottom.Height; railBottom.SetBounds(12, bottom, w, railBottom.Height); bottom -= 12; }
            Rail.SetBounds(12, y, w, Math.Max(Rail.PreferredHeight, Math.Max(0, bottom - y)));
        }
        void LayoutContent(){
            int w = content.Width, h = content.Height;
            bool cap = stepLabel.Text.Length > 0;
            int hh = cap ? 74 : 62;
            header.SetBounds(0, 0, w, hh);
            Footer.SetBounds(0, Math.Max(hh, h - Footer.Height), w, Footer.Height);
            host.SetBounds(28, hh + 14, Math.Max(0, w - 28 - 24), Math.Max(0, Footer.Top - hh - 14 - 12));
            foreach (var p in Pages) p.Body.SetBounds(0, 0, host.Width, host.Height);
            if (overlay != null) overlay.Bounds = host.Bounds;
            Toast.Rest = Footer.Top - 12;
            Toast.SetBounds(28, Toast.Top, Math.Max(0, w - 28 - 24), Toast.Height);
            if (!Toast.Visible) Toast.Top = Toast.Rest - Toast.Height;
        }
        void LayoutHeader(){
            int w = header.Width, h = header.Height;
            bool cap = stepLabel.Text.Length > 0;
            int rx = w - 20;
            if (current >= 0)
                for (int i = Pages[current].HeaderControls.Count - 1; i >= 0; i--) {
                    var c = Pages[current].HeaderControls[i];
                    rx -= c.Width; c.Location = new Point(rx, h - c.Height - 2); rx -= 4;
                }
            stepLabel.SetBounds(28, 22, Math.Max(0, w - 56), cap ? 18 : 0);
            var tw = TextRenderer.MeasureText(titleLabel.Text ?? "", Ui.Title).Width + 8;
            int top = cap ? 40 : 22;
            titleLabel.SetBounds(28, top, Math.Max(0, Math.Min(tw, rx - 28 - 8)), h - top);
            int sx = titleLabel.Right + 8;
            subLabel.SetBounds(sx, top, Math.Max(0, rx - sx - 8), h - top - 4);
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
            Title = title; Height = height;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.White;
            Padding = new Padding(12, 34, 12, 10);
        }
        protected override void OnPaint(PaintEventArgs e){
            var g = e.Graphics;
            g.Clear(Ui.Rail);
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            Ui.FillRound(g, r, 10, Color.White);
            Ui.DrawRound(g, r, 10, Ui.Line, 1);
            using (var f = new Font(Ui.Family, 9F, FontStyle.Bold)) TextRenderer.DrawText(g, Title, f, new Point(12, 11), Ui.Text);
        }
    }

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
