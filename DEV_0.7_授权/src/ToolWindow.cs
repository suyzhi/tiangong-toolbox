using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TianGongCadSuite {
    // 工具窗口的"给模型让路"行为。用户反馈（2026-09-29）：窗口都比较大，很多功能要点数模，遮挡很难受。
    //  1. 第一次打开贴到 CAD 窗口右侧（原来 CenterParent，正好盖在模型中间）；之后记住用户拖到的位置和大小。
    //  2. 窗口顶部加一条窄条：◀ 靠左 / 靠右 ▶ / 收起。收起后只剩标题栏 + 一行实时状态。
    //  3. 勾「点模型时自动收起」：在 CAD 里一按鼠标（点选、旋转视图），窗口就缩成一条；点条上的字再展开。
    // 只挂在实现了 IPickingWindow 的窗口上（ToolContext.Show 统一挂），各窗口自己的排版一行不用改。
    public sealed class ToolWindow {
        public const int BarHeight = 32;
        const int StripWidth = 460, Margin = 8, RibbonInset = 150;

        readonly Form form;
        readonly IPickingWindow tool;
        readonly IntPtr cadFrame, cadRoot;
        readonly string key;
        readonly Panel bar = new Panel();
        readonly Label text = new Label();
        readonly Button dockLeft = Ui.Secondary("◀ 靠左", 66, 24);
        readonly Button dockRight = Ui.Secondary("靠右 ▶", 66, 24);
        readonly Button toggle = Ui.Primary("收起 ▲", 76, 24);
        readonly Button quick;
        readonly CheckBox auto = new CheckBox();
        readonly ToolTip tips = new ToolTip();
        readonly Timer deferred = new Timer { Interval = 60 };
        readonly List<Control> hiddenContent = new List<Control>();
        bool collapsed, anchorRight, pendingCollapse;
        Size expandedSize, expandedMin;

        // 动效只做两个、都很短：界面跑在 CAD 的界面线程上，做重了会拖慢 CAD 本身。
        //  · 收起/展开：窗口外框 120ms 缓动过去（瞬间跳变时，第一次用的人会以为窗口没了）；
        //  · 收起状态下状态变了：条的底色闪一下再褪回去（"收到了"）。
        // 设成 0 就是瞬间完成（测试里用）。
        public static int AnimationMs = 120;
        const int FlashMs = 700;
        readonly Timer anim = new Timer { Interval = 15 };
        readonly Timer flash = new Timer { Interval = 30 };
        Rectangle animFrom, animTo;
        DateTime animStart, flashStart;
        Action animDone;
        Color flashColor;

        // 挂不上（窗口没实现 IPickingWindow，或中途出错）返回 null，窗口照常显示 —— 界面辅助绝不能挡住命令。
        // storeKey 只给测试用：换一个键，免得测试冲掉用户记住的窗口位置。
        public static ToolWindow Attach(Form form, int cadFrameHandle){ return Attach(form, cadFrameHandle, null); }
        public static ToolWindow Attach(Form form, int cadFrameHandle, string storeKey){
            var tool = form as IPickingWindow;
            if (tool == null) return null;
            try { return new ToolWindow(form, tool, new IntPtr(cadFrameHandle), storeKey ?? form.GetType().Name); }
            catch (Exception e) { Log.Write("ToolWindow.Attach", e); return null; }
        }

        public bool Collapsed { get { return collapsed; } }
        public bool AutoCollapse { get { return auto.Checked; } set { auto.Checked = value; } }

        ToolWindow(Form form, IPickingWindow tool, IntPtr frame, string key){
            this.form = form; this.tool = tool; this.key = key;
            cadFrame = frame;
            cadRoot = frame == IntPtr.Zero ? IntPtr.Zero : GetAncestor(frame, GA_ROOT);
            if (cadRoot == IntPtr.Zero) cadRoot = frame;
            string q = tool.QuickActionText;
            if (!string.IsNullOrEmpty(q)) quick = Ui.Primary(q, Math.Max(76, TextRenderer.MeasureText(q, Ui.F9B).Width + 20), 24);

            BuildBar();
            auto.Checked = WindowLayoutStore.ReadFlag(key, "AutoCollapse", tool.AutoCollapseByDefault);
            auto.CheckedChanged += (s, e) => WindowLayoutStore.WriteFlag(this.key, "AutoCollapse", auto.Checked);
            Place();

            var status = tool.StatusLabel;
            if (status != null) {
                status.TextChanged += (s, e) => { if (collapsed) { MirrorStatus(); Flash(); } };
                status.ForeColorChanged += (s, e) => { if (collapsed) MirrorStatus(); };
            }
            form.Deactivate += OnDeactivate;
            deferred.Tick += OnDeferred;
            anim.Tick += OnAnimTick;
            flash.Tick += OnFlashTick;
            form.ResizeEnd += (s, e) => SaveBounds();          // 拖动、拉伸结束都会触发
            form.FormClosing += (s, e) => SaveBounds();
            form.FormClosed += (s, e) => {
                deferred.Stop(); anim.Stop(); flash.Stop();
                deferred.Dispose(); anim.Dispose(); flash.Dispose(); tips.Dispose();
            };
            UpdateBarMode();
        }

        // ---------------- 顶部条 ----------------
        void BuildBar(){
            bar.Height = BarHeight; bar.BackColor = Ui.ChipBg;
            bar.Paint += (s, e) => { using (var p = new Pen(Ui.Line)) e.Graphics.DrawLine(p, 0, bar.Height - 1, bar.Width, bar.Height - 1); };
            text.AutoSize = false; text.AutoEllipsis = true; text.UseMnemonic = false;
            text.TextAlign = ContentAlignment.MiddleLeft; text.BackColor = Color.Transparent; text.Font = Ui.F9;
            auto.Text = "点模型时自动收起"; auto.AutoSize = true; auto.BackColor = Color.Transparent; auto.Font = Ui.F9;
            // 条上的按钮不参与 Tab 顺序：Tab 仍然在窗口原来的输入框之间走
            foreach (Control c in new Control[]{ dockLeft, dockRight, toggle, auto }) { c.TabStop = false; bar.Controls.Add(c); }
            if (quick != null) { quick.TabStop = false; bar.Controls.Add(quick); }
            bar.Controls.Add(text);

            dockLeft.Click += (s, e) => DockTo(false);
            dockRight.Click += (s, e) => DockTo(true);
            toggle.Click += (s, e) => { if (collapsed) Expand(); else Collapse(); };
            text.Click += (s, e) => { if (collapsed) Expand(); };
            bar.Click += (s, e) => { if (collapsed) Expand(); };
            if (quick != null) quick.Click += (s, e) => {
                try { tool.QuickAction(); } catch (Exception ex) { Log.Write("ToolWindow.QuickAction", ex); }
                Expand();
            };
            bar.Resize += (s, e) => LayoutBar();

            tips.SetToolTip(dockLeft, "贴到 CAD 窗口左边");
            tips.SetToolTip(dockRight, "贴到 CAD 窗口右边");
            tips.SetToolTip(auto, "在模型上点选或旋转视图时，窗口自动缩成一条，不挡模型；点条上的文字再展开。");

            // Dock 按 z-order 逆序排：条放到最底层 = 最先停靠，先占住顶部，原来 Fill 的内容排在它下面。
            bar.Dock = DockStyle.Top;
            form.Controls.Add(bar);
            bar.SendToBack();
        }

        void LayoutBar(){
            int w = bar.ClientSize.Width, y = (BarHeight - 24) / 2, pad = 8;
            int right = w - pad;
            toggle.SetBounds(right - toggle.Width, y, toggle.Width, 24);
            right = toggle.Left - 8;
            if (collapsed) {
                if (quick != null) { quick.SetBounds(right - quick.Width, y, quick.Width, 24); right = quick.Left - 8; }
                text.SetBounds(pad, 0, Math.Max(0, right - pad), BarHeight - 1);
            } else {
                var ps = auto.PreferredSize;
                auto.SetBounds(right - ps.Width, (BarHeight - ps.Height) / 2, ps.Width, ps.Height);
                right = auto.Left - 12;
                dockLeft.SetBounds(pad, y, dockLeft.Width, 24);
                dockRight.SetBounds(dockLeft.Right + 6, y, dockRight.Width, 24);
                int tx = dockRight.Right + 12;
                text.SetBounds(tx, 0, Math.Max(0, right - tx), BarHeight - 1);
            }
        }

        void UpdateBarMode(){
            dockLeft.Visible = dockRight.Visible = auto.Visible = !collapsed;
            if (quick != null) quick.Visible = collapsed;
            toggle.Text = collapsed ? "展开 ▼" : "收起 ▲";
            text.Cursor = collapsed ? Cursors.Hand : Cursors.Default;
            bar.Cursor = collapsed ? Cursors.Hand : Cursors.Default;
            tips.SetToolTip(text, collapsed ? "点这里展开窗口" : "拖到哪里，下次就在哪里打开");
            MirrorStatus();
            LayoutBar();
        }

        // 收起时：条上就是窗口底部那行状态（点了什么、还差什么），不用展开也知道进度。
        // 展开时：状态在窗口里看得到，条上只放一句提示。
        void MirrorStatus(){
            if (collapsed) {
                var s = tool.StatusLabel;
                string t = s == null ? "" : (s.Text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
                text.Text = "● " + (t.Length > 0 ? t : "点这里展开");
                text.ForeColor = s == null ? Ui.Text : s.ForeColor;
            } else {
                text.Text = "位置大小会记住";
                text.ForeColor = Ui.Muted;
            }
        }

        // ---------------- 收起 / 展开 ----------------
        // 收起：先藏内容（不让整片卡片跟着每一帧重排），外框缩过去，到位后再把高度钉死。
        public void Collapse(){
            FinishAnimation();
            if (collapsed || form.IsDisposed) return;
            if (form.WindowState != FormWindowState.Normal) form.WindowState = FormWindowState.Normal;
            var expanded = form.Bounds;
            expandedSize = expanded.Size; expandedMin = form.MinimumSize;
            anchorRight = ToolWindowPlacement.AnchorRight(expanded, CurrentWork());
            int chrome = form.Height - form.ClientSize.Height;   // 标题栏 + 边框
            hiddenContent.Clear();
            foreach (Control c in form.Controls) if (c != bar) hiddenContent.Add(c);
            foreach (var c in hiddenContent) c.Visible = false;
            collapsed = true;
            // 先清掉最小尺寸，否则缩不下去；缩完把高度钉死（只许左右拉宽），免得拉出一块空白
            form.MinimumSize = Size.Empty; form.MaximumSize = Size.Empty;
            var strip = ToolWindowPlacement.Collapse(expanded, new Size(StripWidth, chrome + BarHeight), anchorRight);
            UpdateBarMode();
            AnimateTo(strip, () => {
                form.MinimumSize = new Size(Math.Min(strip.Width, 240), strip.Height);
                form.MaximumSize = new Size(10000, strip.Height);
            });
        }

        // 展开：外框先长到原来的大小，到位后再放出内容、恢复最小尺寸（中途设最小尺寸会一下子撑满，动画就没了）。
        public void Expand(){
            FinishAnimation();
            if (!collapsed || form.IsDisposed) return;
            var target = ToolWindowPlacement.Expand(form.Bounds, expandedSize, anchorRight, CurrentWork());
            collapsed = false;
            form.MaximumSize = Size.Empty;
            flash.Stop(); bar.BackColor = Ui.ChipBg;
            UpdateBarMode();
            AnimateTo(target, () => {
                form.SuspendLayout();
                try {
                    form.MinimumSize = expandedMin;
                    form.Bounds = target;
                    foreach (var c in hiddenContent) c.Visible = true;
                    hiddenContent.Clear();
                } finally { form.ResumeLayout(true); }
            });
        }

        void AnimateTo(Rectangle to, Action done){
            anim.Stop();
            if (AnimationMs <= 0 || !form.Visible || form.Bounds == to) {
                form.Bounds = to;
                if (done != null) done();
                return;
            }
            animFrom = form.Bounds; animTo = to; animDone = done; animStart = DateTime.Now;
            anim.Start();
        }

        void OnAnimTick(object sender, EventArgs e){
            double t = (DateTime.Now - animStart).TotalMilliseconds / AnimationMs;
            if (t >= 1 || form.IsDisposed) { FinishAnimation(); return; }
            double k = 1 - Math.Pow(1 - t, 3);   // 先快后慢
            form.Bounds = new Rectangle(
                Lerp(animFrom.X, animTo.X, k), Lerp(animFrom.Y, animTo.Y, k),
                Lerp(animFrom.Width, animTo.Width, k), Lerp(animFrom.Height, animTo.Height, k));
        }

        // 动画没播完又被打断（连点、关窗口、存位置）：直接跳到终点并做完收尾，状态不会卡在半路。
        void FinishAnimation(){
            if (!anim.Enabled) return;
            anim.Stop();
            var done = animDone; animDone = null;
            if (form.IsDisposed) return;
            form.Bounds = animTo;
            if (done != null) done();
        }

        static int Lerp(int a, int b, double k){ return (int)Math.Round(a + (b - a) * k); }

        // 收起状态下状态行变了：条的底色用状态的颜色闪一下，再褪回原色。
        void Flash(){
            if (AnimationMs <= 0 || form.IsDisposed) return;
            var s = tool.StatusLabel;
            flashColor = Blend(s == null ? Ui.Accent : s.ForeColor, Color.White, 0.72);
            flashStart = DateTime.Now;
            bar.BackColor = flashColor;
            flash.Start();
        }

        void OnFlashTick(object sender, EventArgs e){
            double t = (DateTime.Now - flashStart).TotalMilliseconds / FlashMs;
            if (t >= 1 || !collapsed) { flash.Stop(); bar.BackColor = Ui.ChipBg; return; }
            bar.BackColor = Blend(flashColor, Ui.ChipBg, t * t);
        }

        static Color Blend(Color a, Color b, double k){
            return Color.FromArgb(
                (int)Math.Round(a.R + (b.R - a.R) * k),
                (int)Math.Round(a.G + (b.G - a.G) * k),
                (int)Math.Round(a.B + (b.B - a.B) * k));
        }

        public void DockTo(bool right){
            FinishAnimation();
            if (collapsed) { Expand(); FinishAnimation(); }
            if (form.WindowState != FormWindowState.Normal) form.WindowState = FormWindowState.Normal;
            form.Bounds = DockBounds(right, form.Size);
            SaveBounds();
        }

        // 自动收起只认"用户在 CAD 里按了鼠标"：
        //  · 失去激活的那一刻鼠标键是按着的、且光标不在本窗口上；
        //  · 稍等激活切换完成后，前台窗口确实是 CAD 主窗口。
        // 这样弹出的消息框 / 文件夹对话框、切到别的程序、CAD 命令启动时抢焦点，都不会误收。
        void OnDeactivate(object sender, EventArgs e){
            if (!auto.Checked || collapsed) return;
            pendingCollapse = AnyMouseButtonDown() && !form.Bounds.Contains(Cursor.Position);
            if (pendingCollapse) { deferred.Stop(); deferred.Start(); }
        }

        void OnDeferred(object sender, EventArgs e){
            deferred.Stop();
            if (!pendingCollapse) return;
            pendingCollapse = false;
            if (form.IsDisposed || collapsed || !form.Visible || !form.Enabled || !auto.Checked) return;
            if (!ForegroundIsCad()) return;
            Collapse();
        }

        // ---------------- 摆放与记忆 ----------------
        void Place(){
            form.StartPosition = FormStartPosition.Manual;
            Rectangle saved;
            if (WindowLayoutStore.TryReadBounds(key, out saved) && ToolWindowPlacement.Reachable(saved, WorkAreas())) {
                // 大小不小于窗口现在的最小尺寸（升级后最小尺寸可能变了），再塞回它所在屏幕的工作区
                var min = form.MinimumSize;
                var size = new Size(Math.Max(saved.Width, min.Width), Math.Max(saved.Height, min.Height));
                form.Bounds = ToolWindowPlacement.Fit(new Rectangle(saved.Location, size), Screen.FromRectangle(saved).WorkingArea);
                return;
            }
            form.Bounds = DockBounds(true, form.Size);
        }

        void SaveBounds(){
            try {
                if (form.IsDisposed) return;
                FinishAnimation();
                Rectangle r;
                if (form.WindowState != FormWindowState.Normal) r = form.RestoreBounds;
                else if (collapsed) r = ToolWindowPlacement.Expand(form.Bounds, expandedSize, anchorRight, CurrentWork());
                else r = form.Bounds;
                if (r.Width > 0 && r.Height > 0) WindowLayoutStore.WriteBounds(key, r);
            } catch (Exception e) { Log.Write("ToolWindow.Save", e); }
        }

        Rectangle DockBounds(bool right, Size size){
            Rectangle work = cadRoot != IntPtr.Zero ? Screen.FromHandle(cadRoot).WorkingArea : Screen.PrimaryScreen.WorkingArea;
            Rectangle area = work; int inset = 0;
            RECT r;
            if (cadRoot != IntPtr.Zero && GetWindowRect(cadRoot, out r)) {
                area = r.ToRectangle();
                inset = ScalePx(RibbonInset);   // 功能区高度读不到，按 100% 缩放下约 150 像素估
                // ActiveFramehWnd 若是文档子窗口（图形区），它的上沿就是功能区下沿，比估的准
                RECT child;
                if (cadFrame != cadRoot && GetWindowRect(cadFrame, out child) && child.Right - child.Left >= 300 && child.Bottom - child.Top >= 200 && child.Top > r.Top)
                    inset = Math.Max(inset, child.Top - Math.Max(r.Top, work.Top));
            }
            return ToolWindowPlacement.Dock(area, work, size, form.MinimumSize, right, inset, Margin);
        }

        Rectangle CurrentWork(){ return Screen.FromRectangle(form.Bounds).WorkingArea; }

        static Rectangle[] WorkAreas(){
            var screens = Screen.AllScreens;
            var areas = new Rectangle[screens.Length];
            for (int i = 0; i < screens.Length; i++) areas[i] = screens[i].WorkingArea;
            return areas;
        }

        int ScalePx(int px){
            try { using (var g = Graphics.FromHwnd(cadRoot)) return (int)Math.Round(px * g.DpiY / 96f); }
            catch { return px; }
        }

        bool ForegroundIsCad(){
            if (cadRoot == IntPtr.Zero) return false;
            var fg = GetForegroundWindow();
            return fg != IntPtr.Zero && GetAncestor(fg, GA_ROOT) == cadRoot;
        }

        // 查物理按键状态（GetAsyncKeyState）：失去激活发生在鼠标按下的过程中，这时线程的键状态还没更新。
        // 左中右键都算：中键/右键在 CAD 里是旋转、平移视图，同样说明用户正在看模型。
        static bool AnyMouseButtonDown(){
            return (GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0 || (GetAsyncKeyState(0x04) & 0x8000) != 0;
        }

        const uint GA_ROOT = 2;
        [StructLayout(LayoutKind.Sequential)]
        struct RECT {
            public int Left, Top, Right, Bottom;
            public Rectangle ToRectangle(){ return Rectangle.FromLTRB(Left, Top, Right, Bottom); }
        }
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    }
}
