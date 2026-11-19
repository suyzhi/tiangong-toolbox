using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TianGongCadSuite {
    // 批量改名 / 属性卡填写 表格旁边的零件预览：点一行，这里显示那个文件的三维模型，鼠标拖动旋转、滚轮缩放。
    //
    // 用的是天工自带的预览控件 SEPreview.ocx（天工打开文件对话框里的那个预览），读的是文件里存的显示数据，
    // 不在 CAD 里打开文档：零件十来毫秒，几十个零件的装配两百毫秒左右（引用的零件要在原来的位置才显示得全）。
    // 实测的几条（2026-11-04，探针在 scratchpad 的 PreviewSpike）：
    //   * 控件占着当前显示的文件 —— 别的进程改不了名（「另一个进程正在使用此文件」），换文件或清空才放开。
    //     所以改名、写属性之前 Hold(true) 放开；窗口切到天工时也放开（用户可能在天工里 Ctrl+S 存的正是这个文件），
    //     放开前截一张图顶着，回到窗口再重新显示；
    //   * 控件原生是中键拖动旋转、Shift+中键平移、Ctrl+中键缩放，左键 / 右键不响应：左键拖动换成中键交给它；
    //   * 工程图（.dft）显示不出来（控件保持上一个文件），改成显示同名的零件 / 装配，没有就提示；显示配置（.cfg）显示同名装配；
    //   * 文件不在了控件也不报错、只显示空白，所以先查文件在不在；
    //   * 换文件后视角沿用上一个，所以新文件一律回到等轴测再「适合」；同一个文件重新显示（切窗口回来）不动视角；
    //   * 有的装配控件一片空白（焊接台车里的「聚氧模组侧向支撑子装配」，零部件单独都画得出）：装配显示后从屏幕读一次预览区，
    //     只剩背景渐变就改用天工里开着的这个装配自己画（PreviewMesh + MeshView），文件里没有存缩略图可用。
    public sealed class PartPreview : Panel {
        const string Clsid = "7f0aa8a5-3928-4b4b-ae60-4e3c1e331103";   // SEPREVIEW.SEPreviewCtrl.1

        // AxHost 不经 aximp 生成包装：只要 CLSID，属性用 IDispatch 按名字调
        sealed class Host : AxHost {
            public Action Fit;
            public Host() : base(Clsid) { }
            public object Ocx { get { return GetOcx(); } }
            protected override void WndProc(ref Message m){
                const int MK_LBUTTON = 1, MK_MBUTTON = 0x10;
                switch (m.Msg) {
                    case 0x0201:   // WM_LBUTTONDOWN → 中键按下（控件用中键旋转）
                        try { Focus(); } catch { }
                        m.Msg = 0x0207; m.WParam = (IntPtr)(((int)m.WParam & ~MK_LBUTTON) | MK_MBUTTON); break;
                    case 0x0202:   // WM_LBUTTONUP
                        m.Msg = 0x0208; m.WParam = (IntPtr)((int)m.WParam & ~MK_LBUTTON); break;
                    case 0x0203:   // WM_LBUTTONDBLCLK：双击 = 适合窗口
                        if (Fit != null) Fit();
                        return;
                    case 0x0200:   // WM_MOUSEMOVE：按着左键 = 按着中键
                        if (((int)m.WParam & MK_LBUTTON) != 0) m.WParam = (IntPtr)(((int)m.WParam & ~MK_LBUTTON) | MK_MBUTTON);
                        break;
                }
                base.WndProc(ref m);
            }
        }

        [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

        readonly Label title = new Label();
        readonly Panel view = new Panel();
        readonly Label hint = new Label();
        readonly PictureBox still = new PictureBox();
        readonly Timer debounce = new Timer { Interval = 160 };
        readonly Timer settle = new Timer { Interval = 120 };
        readonly Timer check = new Timer { Interval = 700 };   // 装配显示后多久读屏判断画没画出来
        public SolidEdgeFramework.Application App;            // 兜底自己画时从这里找开着的装配
        MeshView meshView;
        string meshPath = "";                                  // 正在自己画的装配
        readonly System.Collections.Generic.HashSet<string> blankAsm = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly System.Collections.Generic.Dictionary<string, PreviewMesh> meshes = new System.Collections.Generic.Dictionary<string, PreviewMesh>(StringComparer.OrdinalIgnoreCase);
        Host host;
        bool broken;          // 控件建不出来（没装 / 天工版本不同）：只显示提示
        string row = "";      // 表里选中的那一行的文件
        string wanted = "";   // 要显示的文件（工程图 / 显示配置已换成同名模型）
        string wantedNote = "";
        string loaded = "";   // 控件里现在打开着的文件
        string lastShown = "";
        int fits;
        bool busy, inactive;
        bool pinned;          // 测试用：私有桌面上窗口总被判成"没激活"，钉住才看得到真实显示
        bool forceEmpty;      // 测试用：私有桌面上读不了屏（CopyFromScreen 报句柄无效），强制当成"没画出来"测兜底
        Form owner;

        public PartPreview(){
            BackColor = Ui.Bg;
            Padding = new Padding(10, 0, 0, 0);
            title.AutoSize = false; title.Dock = DockStyle.Top; title.Height = 26;
            title.Font = Ui.F9B; title.ForeColor = Ui.Text; title.BackColor = Ui.Bg;
            title.TextAlign = ContentAlignment.MiddleLeft; title.AutoEllipsis = true;
            title.Text = "零件预览";
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 30, BackColor = Ui.Bg, WrapContents = false, Padding = new Padding(0), Margin = new Padding(0) };
            foreach (var v in new[]{ new[]{ "等轴测", "Iso" }, new[]{ "前", "Front" }, new[]{ "上", "Top" }, new[]{ "右", "Right" } }) {
                string type = v[1];
                var b = Ui.Small(v[0]); b.Margin = new Padding(0, 0, 2, 0); ((FlatButton)b).SetFore(Ui.Text2);
                b.Click += (s, e) => SetView(type);
                bar.Controls.Add(b);
            }
            var fit = Ui.Small("适合"); fit.Margin = new Padding(0, 0, 2, 0); ((FlatButton)fit).SetFore(Ui.Text2);
            fit.Click += (s, e) => FitView();
            bar.Controls.Add(fit);
            var tip = new Label { Dock = DockStyle.Bottom, Height = 22, AutoSize = false, Font = Ui.F8, ForeColor = Ui.Muted, BackColor = Ui.Bg,
                Text = "拖动旋转 · 滚轮缩放 · Shift+拖动平移 · 双击适合", TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
            view.Dock = DockStyle.Fill; view.BackColor = Color.FromArgb(0xE9, 0xED, 0xF3);
            hint.Dock = DockStyle.Fill; hint.AutoSize = false; hint.TextAlign = ContentAlignment.MiddleCenter;
            hint.Font = Ui.F9; hint.ForeColor = Ui.Muted; hint.BackColor = view.BackColor; hint.Padding = new Padding(16);
            still.Dock = DockStyle.Fill; still.SizeMode = PictureBoxSizeMode.Normal; still.Visible = false; still.BackColor = view.BackColor;
            view.Controls.Add(hint); view.Controls.Add(still);
            Controls.Add(view); Controls.Add(tip); Controls.Add(bar); Controls.Add(title);
            debounce.Tick += (s, e) => { debounce.Stop(); Apply(); };
            settle.Tick += (s, e) => {
                settle.Stop(); if (loaded.Length == 0) return; SetView("Iso"); lastShown = loaded;
                if (string.Equals(Path.GetExtension(loaded), ".asm", StringComparison.OrdinalIgnoreCase)) { check.Stop(); check.Start(); }
            };
            check.Tick += (s, e) => { check.Stop(); CheckDrawn(); };
            Say("点表里的一行，这里显示它的三维模型");
        }

        // 放到表格所在的卡片右边（中间一条可拖的分隔条），返回标题栏上的「隐藏预览 / 显示预览」按钮；开关记在 choices.tsv 的 key 下。
        // 卡片里要先放好 Dock=Fill 的表格：停靠按 Controls 倒序排，后加的预览先占右边，表格填剩下的
        public static Button Attach(Control frame, PartPreview preview, string key){
            var split = new Splitter { Dock = DockStyle.Right, Width = 6, MinExtra = 360, MinSize = 220, BackColor = Ui.Bg };
            preview.Dock = DockStyle.Right; preview.Width = 340;
            frame.Controls.Add(split); frame.Controls.Add(preview);
            var toggle = Ui.Small("隐藏预览"); ((FlatButton)toggle).SetFore(Ui.Text2);
            Action sync = () => { bool on = Choices.Get(key, "1") != "0"; preview.Visible = on; split.Visible = on; toggle.Text = on ? "隐藏预览" : "显示预览"; };
            toggle.Click += (s, e) => { Choices.Set(key, preview.Visible ? "0" : "1"); sync(); };
            sync();
            return toggle;
        }

        // 表里选中了一行（path 为空 = 没选中）。连按方向键时只显示最后停下的那个
        public void ShowFile(string path){
            string note;
            row = path ?? "";
            wanted = ModelFor(row, out note);
            wantedNote = note;
            title.Text = string.IsNullOrEmpty(path) ? "零件预览" : Path.GetFileName(path) + (note.Length > 0 && wanted.Length > 0 ? "　（" + note + "）" : "");
            debounce.Stop(); debounce.Start();
        }

        // 批量改名做完：正在显示的文件换成新名字（旧名字的文件已经不在了），视角不变
        public void Renamed(System.Collections.Generic.IDictionary<string, string> moved){
            string to;
            if (lastShown.Length > 0 && moved.TryGetValue(lastShown, out to)) lastShown = to;
            if (row.Length > 0 && moved.TryGetValue(row, out to)) ShowFile(to);
            else if (wanted.Length > 0 && moved.TryGetValue(wanted, out to)) { wanted = to; Kick(); }
        }

        // 改名 / 写属性期间放开文件（控件占着文件，别的进程改不了名）
        public void Hold(bool on){
            busy = on;
            if (on) { debounce.Stop(); Release(); } else Kick();
        }

        protected override void OnHandleCreated(EventArgs e){
            base.OnHandleCreated(e);
            var f = FindForm();
            if (f != null && f != owner) {
                owner = f;
                // 切到天工（或别的窗口）就放开文件：天工里保存 / 另存这个文件时不会被占着
                f.Deactivate += (s, a) => { if (pinned) return; inactive = true; debounce.Stop(); Release(); };
                f.Activated += (s, a) => { inactive = false; Kick(); };
                f.FormClosed += (s, a) => { debounce.Stop(); Release(); };
            }
        }
        protected override void OnVisibleChanged(EventArgs e){
            base.OnVisibleChanged(e);
            if (!Visible) { debounce.Stop(); Release(); } else Kick();
        }
        protected override void Dispose(bool disposing){
            if (disposing) { debounce.Dispose(); settle.Dispose(); check.Dispose(); Release(); if (host != null) { try { host.Dispose(); } catch { } host = null; } }
            base.Dispose(disposing);
        }

        void Kick(){ debounce.Stop(); debounce.Start(); }

        // 工程图显示同名的零件 / 钣金 / 装配，显示配置（.cfg）显示同名装配（批量改名里跟着改的就是这样配对的）
        static string ModelFor(string path, out string note){
            note = "";
            if (path.Length == 0) return "";
            string ext0 = Path.GetExtension(path).ToLowerInvariant();
            bool draft = ext0 == ".dft";
            if (!draft && ext0 != ".cfg") return path;
            string what = draft ? "工程图" : "显示配置";
            try {
                string stem = Path.Combine(Path.GetDirectoryName(path) ?? "", Path.GetFileNameWithoutExtension(path));
                foreach (var ext in draft ? new[]{ ".par", ".psm", ".asm" } : new[]{ ".asm" }) if (File.Exists(stem + ext)) { note = what + "：显示同名的" + Path.GetFileName(stem + ext); return stem + ext; }
            } catch { }
            note = what + "没有三维预览（同一文件夹里也没有同名的" + (draft ? "零件 / 装配" : "装配") + "）";
            return "";
        }

        void Apply(){
            if (busy || inactive || !Visible || IsDisposed) return;
            if (wanted.Length == 0) { Release(); Say(wantedNote.Length > 0 ? wantedNote : "点表里的一行，这里显示它的三维模型"); return; }
            if (!File.Exists(wanted)) { Release(); Say("文件不在了：\r\n" + wanted); return; }
            if (blankAsm.Contains(wanted)) { ShowMesh(wanted); return; }   // 已经知道控件画不出来的装配：直接自己画
            if (!EnsureHost()) return;
            if (string.Equals(loaded, wanted, StringComparison.OrdinalIgnoreCase)) { Reveal(); return; }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try {
                Cursor = Cursors.WaitCursor;
                // 装配要读每个零件的显示数据（一百多个零件 3 秒多），先说一声，不然像卡住了
                if (string.Equals(Path.GetExtension(wanted), ".asm", StringComparison.OrdinalIgnoreCase)) { Say("正在加载装配…"); hint.Refresh(); }
                object ocx = host.Ocx;
                Set(ocx, "Filename", wanted);
                loaded = wanted;
                Reveal();
                // 换了文件才回到等轴测；同一个文件（切窗口回来、改名 / 写属性做完）保持用户转好的视角。
                // 稍等一下再调：新建的控件加载第一个文件后自己会异步「适合」一次，马上调的留边会被冲掉
                if (!string.Equals(lastShown, wanted, StringComparison.OrdinalIgnoreCase)) { settle.Stop(); settle.Start(); }
                if (sw.ElapsedMilliseconds > 500) Log.Write("PartPreview", "slow " + sw.ElapsedMilliseconds + "ms " + wanted);
            } catch (Exception e) {
                loaded = "";
                Log.Write("PartPreview", wanted + " " + e);
                Say("这个文件预览不了：" + (e.InnerException ?? e).Message);
            } finally { Cursor = Cursors.Default; }
        }

        // 控件第一次用到时才建；建不出来（没注册、换了天工版本）就只显示提示，表格照常用
        bool EnsureHost(){
            if (host != null) return true;
            if (broken) return false;
            try {
                var h = new Host { Dock = DockStyle.Fill };
                h.Fit = FitView;
                view.Controls.Add(h);
                h.CreateControl();
                object ocx = h.Ocx;
                if (ocx == null) throw new InvalidOperationException("控件没有返回对象");
                // 浅色渐变底（默认是深蓝），和窗口其它部分一致；第一个参数是下边的颜色（COLORREF = 0x00BBGGRR）
                try { Call(ocx, "SetGradientBackground", (uint)0xE0D6CC, (uint)0xF8F5F2); } catch (Exception e) { Log.Write("PartPreview", "background " + e.Message); }
                host = h;
                return true;
            } catch (Exception e) {
                broken = true;
                Log.Write("PartPreview", "create " + e);
                foreach (Control c in view.Controls) if (c is Host) { view.Controls.Remove(c); try { c.Dispose(); } catch { } break; }
                Say("预览不可用：天工的预览控件（SEPreview.ocx）没能加载。\r\n不影响表格的其它功能。");
                return false;
            }
        }

        // 放开文件：先截一张当前画面顶着，再让控件关掉文件
        void Release(){
            if (host == null || loaded.Length == 0) return;
            Bitmap shot = host.Visible ? Snapshot() : null;
            if (shot != null) { var old = still.Image; still.Image = shot; if (old != null) old.Dispose(); still.Visible = true; still.BringToFront(); hint.Visible = false; }
            else if (host.Visible) Say("回到这个窗口就接着显示预览");
            try { Set(host.Ocx, "Filename", ""); } catch (Exception e) { Log.Write("PartPreview", "release " + e.Message); }
            loaded = "";
            host.Visible = false;
        }

        // 截当前画面：天工进程里 PrintWindow 截三维窗口常常是一片白（独立进程里正常），那就从屏幕上拷；
        // 都是一种颜色（白 / 被别的窗口挡着）就不要，改显示一句提示
        Bitmap Snapshot(){
            if (!host.IsHandleCreated || host.Width <= 0 || host.Height <= 0) return null;
            for (int way = 0; way < 2; way++) {
                Bitmap bmp = null;
                try {
                    bmp = new Bitmap(host.Width, host.Height);
                    using (var g = Graphics.FromImage(bmp)) {
                        if (way == 0) { IntPtr dc = g.GetHdc(); try { PrintWindow(host.Handle, dc, 2); } finally { g.ReleaseHdc(dc); } }
                        else g.CopyFromScreen(host.PointToScreen(Point.Empty), Point.Empty, host.Size);
                    }
                    if (!Blank(bmp)) return bmp;
                } catch (Exception e) { Log.Write("PartPreview", "snapshot " + way + " " + e.Message); }
                if (bmp != null) bmp.Dispose();
            }
            return null;
        }
        static bool Blank(Bitmap b){
            var first = b.GetPixel(b.Width / 2, b.Height / 2);
            int distinct = 0;
            for (int y = 1; y < 10; y++) for (int x = 1; x < 10; x++) if (b.GetPixel(b.Width * x / 10, b.Height * y / 10) != first) distinct++;
            return distinct < 3;
        }

        // 装配显示后读一次屏：预览区只剩背景渐变（每一行颜色都一样）= 控件没画出来 → 自己画
        void CheckDrawn(){
            if (host == null || !host.Visible || loaded.Length == 0 || busy || inactive) return;
            string file = loaded;
            bool empty = forceEmpty;
            if (!empty) try {
                using (var bmp = new Bitmap(host.Width, host.Height)) {
                    using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(host.PointToScreen(Point.Empty), Point.Empty, host.Size);
                    empty = NothingDrawn(bmp);
                }
            } catch (Exception e) { Log.Write("PartPreview", "check " + e.Message); return; }
            if (!empty) return;
            Log.Write("PartPreview", "control drew nothing, drawing it ourselves: " + file);
            blankAsm.Add(file);
            ShowMesh(file);
        }
        internal static bool NothingDrawn(Bitmap b){
            if (b.Width < 8 || b.Height < 8) return false;
            int busyRows = 0;
            for (int y = 2; y < 40; y++) {
                int py = b.Height * y / 42;
                var first = b.GetPixel(2, py);
                for (int x = 1; x < 40; x++) {
                    var c = b.GetPixel(2 + (b.Width - 5) * x / 40, py);
                    if (Math.Abs(c.R - first.R) + Math.Abs(c.G - first.G) + Math.Abs(c.B - first.B) > 12) { busyRows++; break; }
                }
            }
            return busyRows <= 1;
        }

        // 用天工里开着的装配自己画（不占文件）
        void ShowMesh(string file){
            check.Stop(); settle.Stop();
            if (host != null && loaded.Length > 0) { try { Set(host.Ocx, "Filename", ""); } catch { } loaded = ""; }
            PreviewMesh mesh;
            if (!meshes.TryGetValue(file, out mesh)) {
                var asm = PreviewMesh.Find(App, file);
                if (asm == null) { Say("天工的预览控件画不出这个装配。\r\n在天工里打开它（或载入用到它的总装配）后再点这一行，插件会用天工里的模型画出来。"); return; }
                Say("天工的预览控件画不出这个装配，\r\n正在用天工里的模型画…"); hint.Refresh();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                Cursor = Cursors.WaitCursor;
                try { mesh = PreviewMesh.Build(asm, null); }
                catch (Exception e) { Log.Write("PartPreview", "mesh " + e); Say("这个装配画不出来：" + e.Message); return; }
                finally { Cursor = Cursors.Default; }
                Log.Write("PartPreview", "mesh " + mesh.Parts + " parts " + mesh.Triangles + " triangles " + sw.ElapsedMilliseconds + "ms " + file);
                if (meshes.Count > 4) meshes.Clear();
                meshes[file] = mesh;
            }
            if (mesh.Triangles == 0) { Say("这个装配里没有读到能画的零件"); return; }
            if (meshView == null) { meshView = new MeshView { Dock = DockStyle.Fill }; view.Controls.Add(meshView); }
            if (!string.Equals(meshPath, file, StringComparison.OrdinalIgnoreCase)) meshView.Mesh = mesh;
            meshPath = file;
            meshView.Visible = true; meshView.BringToFront();
            hint.Visible = false; still.Visible = false;
            if (host != null) host.Visible = false;
            string name = row.Length > 0 ? Path.GetFileName(row) : Path.GetFileName(file);
            title.Text = name + "　（预览控件画不出，按天工里的模型画）";
        }
        bool MeshShown { get { return meshView != null && meshView.Visible; } }

        void Reveal(){
            if (meshView != null) meshView.Visible = false;
            host.Visible = true; host.BringToFront();
            still.Visible = false;
            hint.Visible = false;
        }
        void Say(string text){
            if (meshView != null) meshView.Visible = false;
            hint.Text = text; hint.Visible = true; hint.BringToFront();
            still.Visible = false;
            if (host != null) host.Visible = false;
        }

        void SetView(string type){
            if (MeshShown) { meshView.SetView(type); return; }
            if (host == null || loaded.Length == 0) return;
            try { Set(host.Ocx, "ViewType", type); } catch (Exception e) { Log.Write("PartPreview", "view " + e.Message); }
            FitView();
        }
        void FitView(){
            if (MeshShown) { meshView.Fit(); return; }
            if (host == null || loaded.Length == 0) return;
            try { Call(host.Ocx, "Fit"); fits++; } catch (Exception e) { Log.Write("PartPreview", "fit " + e.Message); return; }
            // 控件的「适合」是贴边的（零件两头顶着窗口边、一点不留），再缩小一点留出边：
            // 控件没有缩放比例的接口，用它自己的 Ctrl+中键上下拖动缩放，往上拖 12 像素约缩到 0.85 倍
            try {
                const int MK_CONTROL = 8, MK_MBUTTON = 0x10;
                int cx = host.Width / 2, cy = host.Height / 2;
                Func<int, int, IntPtr> at = (x, y) => (IntPtr)((y << 16) | (x & 0xFFFF));
                SendMessage(host.Handle, 0x0200, (IntPtr)MK_CONTROL, at(cx, cy));   // 先移到起点：没有这一下，新控件的第一次拖动不认
                SendMessage(host.Handle, 0x0207, (IntPtr)(MK_CONTROL | MK_MBUTTON), at(cx, cy));
                for (int i = 1; i <= 4; i++) SendMessage(host.Handle, 0x0200, (IntPtr)(MK_CONTROL | MK_MBUTTON), at(cx, cy - 3 * i));
                SendMessage(host.Handle, 0x0208, (IntPtr)MK_CONTROL, at(cx, cy - 12));
            } catch (Exception e) { Log.Write("PartPreview", "margin " + e.Message); }
        }

        static void Set(object o, string name, object value){ o.GetType().InvokeMember(name, BindingFlags.SetProperty, null, o, new[]{ value }); }
        static void Call(object o, string name, params object[] args){ o.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, o, args); }

#if TG_DEV_BUILD
        internal string TestState(){ return "fits=" + fits + " loaded=" + loaded + " wanted=" + wanted + " busy=" + busy + " inactive=" + inactive + " broken=" + broken + " hint=" + (hint.Visible ? hint.Text.Replace("\r\n", " ") : "") + " still=" + still.Visible + " host=" + (host != null && host.Visible) + (host != null ? " " + host.Width + "x" + host.Height + " view=" + view.Width + "x" + view.Height : ""); }
        internal void TestFit(){ FitView(); }
        internal void TestApplyNow(){ debounce.Stop(); Apply(); }
        // 私有桌面上窗口拿不到"激活"：当它是激活的，马上显示，再等调视角的定时器走完
        internal void TestShowNow(){
            pinned = true; inactive = false; debounce.Stop(); Apply();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 400) { Application.DoEvents(); System.Threading.Thread.Sleep(10); }
        }
        internal string TestMeshState(){ return MeshShown ? "mesh " + meshView.Mesh.Parts + " parts " + meshView.Mesh.Triangles + " tris" : "no mesh"; }
        internal void TestMeshSave(string png, double daz){ if (!MeshShown) return; if (daz != 0) meshView.TestRotate(daz, 0); meshView.TestSave(png); }
        internal void TestCommand(string cmd){
            if (cmd == "forceempty") { forceEmpty = true; return; }
            if (host == null) return;
            if (cmd == "zoomout" || cmd == "zoomin") {
                var c = host.PointToScreen(new Point(host.Width / 2, host.Height / 2));   // WM_MOUSEWHEEL 的坐标是屏幕坐标
                for (int i = 0; i < 3; i++) SendMessage(host.Handle, 0x020A, (IntPtr)((cmd == "zoomin" ? 120 : -120) << 16), (IntPtr)((c.Y << 16) | (c.X & 0xFFFF)));
            } else SetView(cmd);
        }
        internal void TestDeactivate(){ inactive = true; debounce.Stop(); Release(); }
        internal string LoadedFile { get { return loaded; } }
        internal void TestShot(string png){
            var f = FindForm();
            using (var bmp = new Bitmap(f.Width, f.Height)) {
                using (var g = Graphics.FromImage(bmp)) { IntPtr dc = g.GetHdc(); try { PrintWindow(f.Handle, dc, 2); } finally { g.ReleaseHdc(dc); } }
                bmp.Save(png);
            }
        }
        internal void TestDrag(int dx, int dy, int keys){
            if (host == null) return;
            const int MK_LBUTTON = 1;
            Func<int, int, IntPtr> lp = (x, y) => (IntPtr)((y << 16) | (x & 0xFFFF));
            int cx = host.Width / 2, cy = host.Height / 2;
            SendMessage(host.Handle, 0x0201, (IntPtr)(MK_LBUTTON | keys), lp(cx, cy));
            for (int i = 1; i <= 10; i++) { SendMessage(host.Handle, 0x0200, (IntPtr)(MK_LBUTTON | keys), lp(cx + dx * i / 10, cy + dy * i / 10)); Application.DoEvents(); }
            SendMessage(host.Handle, 0x0202, (IntPtr)keys, lp(cx + dx, cy + dy));
        }
#endif
    }
}
