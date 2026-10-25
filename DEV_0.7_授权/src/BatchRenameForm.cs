using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using F = SolidEdgeFramework;

namespace TianGongCadSuite {
    // 批量改名（命令 12）窗口：一行一个文件，「新文件名」一列像 Excel 一样改，点「改名」一次做完。
    //
    // 批量生成新名字三招：查找替换、前后加字、按模板（{原名} {序号} {任意属性}，比如 {图号}-{零件名称}）。
    // 选中两行以上 = 只改选中的；否则改筛出来的全部。同名工程图 / 显示配置自动跟着改（灰字行）。
    // 改名后装配、工程图里的引用一起改好（见 BatchRenameCad），不会断开。
    public sealed class BatchRenameForm : Form, IBusyTool {
        const string Title = "批量改名";
        const int ColNew = 2;
        readonly F.Application app;
        readonly BatchRenameCad cad;
        readonly List<RenameRow> rows = new List<RenameRow>();
        readonly DataGridView grid = new DataGridView();
        readonly TextBox filter = new TextBox();
        readonly TextBox findBox = new TextBox(), replaceBox = new TextBox(), prefixBox = new TextBox(), suffixBox = new TextBox(), templateBox = new TextBox();
        readonly CheckBox follow = new CheckBox();
        readonly Label status = new Label();
        readonly Button run;
        readonly WizardShell shell = new WizardShell { StepCaption = false };
        int stateFilter;   // 0 全部 / 1 要改名的 / 2 有问题的
        bool loading, busy;
        public bool Busy { get { return busy; } }
        public string BusyWhat { get { return "改名"; } }
        readonly Dictionary<string, string[]> folderFiles = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        readonly System.Diagnostics.Stopwatch pulse = new System.Diagnostics.Stopwatch();
        void Pulse(string text){
            if (pulse.IsRunning && pulse.ElapsedMilliseconds < 200) return;
            pulse.Restart();
            status.Text = text; status.ForeColor = Ui.Muted; status.Refresh();
            try { Application.DoEvents(); } catch { }
        }
        void BeginBusy(){ busy = true; Enabled = false; Cursor = Cursors.WaitCursor; pulse.Reset(); }
        void EndBusy(){ busy = false; Enabled = true; Cursor = Cursors.Default; }

        public BatchRenameForm(F.Application application){
            app = application;
            cad = new BatchRenameCad(application);
            Ui.Shell(this, Product.Title(Title), 1120, 680, 860, 480);
            FormBorderStyle = FormBorderStyle.Sizable;
            Controls.Add(shell);
            var page = shell.AddPage(Title, Title, "");

            // ---- 侧栏：来源、筛选、跟着改 ----
            var top = new Panel { BackColor = Ui.Rail, Height = 38 + 6 + 32 + 12 + 22 + 30 };
            var reload = Ui.Secondary("载入当前文档", 200, 38); reload.Click += (s, e) => LoadActive();
            var add = Ui.Ghost("添加文件…"); add.Click += (s, e) => AddFiles();
            var lf = new Label { Text = "按文件名筛选", AutoSize = false, Font = Ui.F8, ForeColor = Ui.Text2, BackColor = Ui.Rail };
            filter.BorderStyle = BorderStyle.FixedSingle; filter.Font = Ui.F9;
            top.Controls.AddRange(new Control[] { reload, add, lf, filter });
            top.Resize += (s, e) => {
                int w = top.Width;
                reload.SetBounds(0, 0, w, 38); add.SetBounds(0, 44, w, 32);
                lf.SetBounds(2, 88, w, 20); filter.SetBounds(0, 110, w, 26);
            };
            shell.RailTop = top;
            shell.Rail.Visible = true;
            shell.Rail.Kind = StepRail.Mode.Nav;
            shell.Rail.Add("全部文件", "");
            shell.Rail.Add("要改名的", "").Dot = Color.FromArgb(0xF5, 0x9E, 0x0B);
            shell.Rail.Add("有问题的", "").Dot = Ui.Bad;
            shell.Rail.SelectedChanged += (s, e) => { stateFilter = shell.Rail.Selected; Rebuild(); };
            filter.TextChanged += (s, e) => Rebuild();
            var foot = new Panel { BackColor = Ui.Rail, Height = 64 };
            follow.Text = "同文件夹里同名的工程图（.dft）、显示配置（.cfg）跟着改名";
            follow.Checked = true; follow.AutoSize = false; follow.BackColor = Ui.Rail; follow.Font = Ui.F8;
            follow.CheckAlign = ContentAlignment.TopLeft; follow.TextAlign = ContentAlignment.TopLeft;
            follow.Dock = DockStyle.Fill;
            follow.CheckedChanged += (s, e) => { SyncFollowers(); Rebuild(); };
            foot.Controls.Add(follow);
            shell.RailBottom = foot;

            // ---- 正文：三种批量生成 + 表格 ----
            var remove = Ui.Small("移除选中行"); remove.Click += (s, e) => RemoveSelectedRows();
            var revert = Ui.Small("撤销修改"); revert.Click += (s, e) => {
                if (!rows.Any(r => r.Changed)) return;
                if (MessageBox.Show(this, "把所有新名字恢复成原来的名字？", Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                foreach (var r in rows) r.Revert(); Rebuild();
            };
            foreach (var b2 in new[] { remove, revert }) ((FlatButton)b2).SetFore(Ui.Text2);
            shell.AddHeader(page, remove); shell.AddHeader(page, revert);
            var stack = new VerticalStack { Dock = DockStyle.Fill, Padding = new Padding(0), Gap = 10, AutoScroll = false };
            page.Body.Controls.Add(stack);
            // 查找替换、前后加字各占一行：原来挤在一行、不换行，窗口窄一点（1366 宽的屏幕）「前面加 / 后面加 / 加上」就被截在外面点不到
            stack.Add(ToolRow(Label("查找"), Box(findBox, 150), Label("替换成"), Box(replaceBox, 150), Act("替换", ApplyReplace)));
            stack.Add(ToolRow(Label("前面加"), Box(prefixBox, 150), Label("后面加"), Box(suffixBox, 150), Act("加上", ApplyAffix)));
            var tip = Ui.Muted8("{原名} 原文件名　{序号} 01、02…（{序号:3} 三位）　{属性名} 零件的自定义属性，比如 {图号}"); tip.BackColor = Ui.Bg; tip.AutoSize = true;
            stack.Add(ToolRow(Label("按模板"), Box(templateBox, 260), Act("生成", ApplyTemplate), FieldGrid.Space(12), tip));
            findBox.KeyDown += OnEnter(ApplyReplace); replaceBox.KeyDown += OnEnter(ApplyReplace);
            prefixBox.KeyDown += OnEnter(ApplyAffix); suffixBox.KeyDown += OnEnter(ApplyAffix);
            templateBox.KeyDown += OnEnter(ApplyTemplate);
            var gridFrame = Ui.Card("", 0, "");
            gridFrame.Padding = new Padding(2, 2, 2, 8);
            BuildGrid();
            gridFrame.Controls.Add(grid);
            var legend = new Label { Text = "选中两行以上 = 只改选中的，否则改表里筛出来的全部　·　Ctrl+V 粘贴一列新名字（可从 Excel 复制）　·　Delete 恢复原名　·　黄底 = 要改名　灰字 = 跟着主文件改的同名工程图", AutoSize = false, Height = 18, Font = Ui.F8, ForeColor = Ui.Muted, BackColor = Ui.Bg };
            stack.Add(gridFrame, true); stack.Add(legend);

            // ---- 底部 ----
            run = Ui.Primary("改名", 220, 44);
            var close = Ui.Secondary("关闭", 84, 38);
            run.Click += (s, e) => RunRename();
            close.Click += (s, e) => Close();
            shell.Footer.Padding = new Padding(28, 0, 20, 0);
            shell.Footer.AddRight(close); shell.Footer.AddRight(run);
            shell.Footer.Status = status;
            status.ForeColor = Ui.Muted;
            FormClosing += (s, e) => {
                if (busy && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; status.Text = "正在改名，做完再关。"; status.ForeColor = Ui.Warn; return; }
                // 填了一表新名字、手一滑点了关闭（或点了功能区别的命令）：原来一声不响全丢
                if (!quiet && e.CloseReason == CloseReason.UserClosing && !ConfirmDiscard()) e.Cancel = true;
            };
            UpdateStatus();
        }

        static Label Label(string text){ return new Label { Text = text, AutoSize = true, Font = Ui.F8, ForeColor = Ui.Text2, BackColor = Ui.Bg, Margin = new Padding(0, 8, 6, 0) }; }
        static TextBox Box(TextBox box, int width){ box.BorderStyle = BorderStyle.FixedSingle; box.Font = Ui.F9; box.Width = width; box.Margin = new Padding(0, 2, 10, 0); return box; }
        static Control Act(string text, Action click){ var b = Ui.Secondary(text, 64, 30); b.Margin = new Padding(0, 0, 0, 0); b.Click += (s, e) => click(); return b; }
        static KeyEventHandler OnEnter(Action a){ return (s, e) => { if (e.KeyCode == Keys.Enter) { a(); e.SuppressKeyPress = true; } }; }
        static Control ToolRow(params Control[] items){
            var row = new FlowLayoutPanel { Height = 34, BackColor = Ui.Bg, WrapContents = false, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(0), Margin = new Padding(0) };
            row.Controls.AddRange(items);
            return row;
        }

        void BuildGrid(){
            grid.Dock = DockStyle.Fill;
            grid.BackgroundColor = Color.White; grid.BorderStyle = BorderStyle.None;
            grid.AllowUserToAddRows = false; grid.AllowUserToDeleteRows = false; grid.AllowUserToResizeRows = false;
            grid.RowHeadersVisible = false;
            grid.SelectionMode = DataGridViewSelectionMode.CellSelect; grid.MultiSelect = true;
            grid.EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2;
            grid.ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithoutHeaderText;
            grid.Font = Ui.F9;
            grid.EnableHeadersVisualStyles = false;
            grid.ColumnHeadersDefaultCellStyle.BackColor = Ui.Rail;
            grid.ColumnHeadersDefaultCellStyle.ForeColor = Ui.Muted;
            grid.ColumnHeadersDefaultCellStyle.Font = Ui.F8;
            grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Ui.Rail;
            grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(6, 0, 0, 0);
            grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            grid.ColumnHeadersHeight = 34;
            grid.GridColor = Color.FromArgb(0xEE, 0xEE, 0xF0);
            grid.RowTemplate.Height = 30;
            grid.DefaultCellStyle.Padding = new Padding(6, 0, 0, 0);
            grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(0xE0, 0xE7, 0xFF);
            grid.DefaultCellStyle.SelectionForeColor = Ui.Text;
            grid.ShowCellToolTips = true;
            Column("类型", 70, true);
            Column("原文件名", 280, true);
            Column("新文件名（不含扩展名）", 280, false);
            Column("扩展名", 64, true);
            Column("说明", 320, true).AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            grid.CellValueChanged += (s, e) => {
                if (loading || e.RowIndex < 0 || e.ColumnIndex != ColNew) return;
                ((RenameRow)grid.Rows[e.RowIndex].Tag).SetNewBase(Convert.ToString(grid.Rows[e.RowIndex].Cells[ColNew].Value));
                Refresh2();
            };
            grid.CellFormatting += OnFormat;
            grid.KeyDown += (s, e) => {
                if (e.Control && e.KeyCode == Keys.V) { Paste(); e.Handled = true; }
                else if (e.KeyCode == Keys.Delete) { foreach (var r in SelectedRows()) r.Revert(); Refresh2(); e.Handled = true; }
            };
            grid.DataError += (s, e) => { e.ThrowException = false; };
        }

        DataGridViewTextBoxColumn Column(string header, int width, bool readOnly){
            var c = new DataGridViewTextBoxColumn { HeaderText = header, Width = width, ReadOnly = readOnly, SortMode = DataGridViewColumnSortMode.NotSortable };
            grid.Columns.Add(c);
            return c;
        }

        // ---------- 载入 ----------

        public void LoadActive(){
            if (!ConfirmDiscard()) return;
            // 在装配里原位编辑着零件：改名时要关掉文档，原来会把这个原位编辑中的零件文档直接关掉（连带打断整个原位编辑）
            try {
                var ip = app == null ? null : app.ActiveDocument as SolidEdgePart.PartDocument;
                if (ip != null && ip.InPlaceActivated) {
                    status.Text = "天工正在原位编辑「" + ip.Name + "」：先退出原位编辑（回到顶层装配），再点「载入当前文档」。";
                    status.ForeColor = Ui.Warn;
                    return;
                }
            } catch { }
            string source;
            List<string> files;
            try { files = cad.Collect(out source); }
            catch (Exception e) { Log.Write("RenameCollect", e); status.Text = "读当前文档失败：" + e.Message; return; }
            rows.Clear(); folderFiles.Clear();
            shell.SetTitle(Title, source);
            AddPaths(files);
            cad.Warm(rows.Select(r => r.Path));
            if (cad.SkippedSubs.Count > 0) {
                status.Text = "注意：" + cad.SkippedSubs.Count + " 个子装配读不出来，里面的零件没收进来（" + string.Join("、", cad.SkippedSubs.Take(3).ToArray()) + (cad.SkippedSubs.Count > 3 ? " 等" : "") + "）。等天工加载完再点「载入当前文档」。";
                status.ForeColor = Ui.Warn;
            }
        }

        void AddFiles(){
            using (var dlg = new OpenFileDialog()) {
                dlg.Multiselect = true;
                dlg.Title = "添加要改名的文件";
                dlg.Filter = "天工零件 / 钣金 / 装配 / 工程图|*.par;*.psm;*.asm;*.dft";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                AddPaths(new List<string>(dlg.FileNames));
            }
        }

        // 只收天工文件；已在表里的跳过（跟随行被手动加进来 = 变回普通行）
        void AddPaths(List<string> files){
            var have = new Dictionary<string, RenameRow>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in rows) have[r.Path] = r;
            foreach (var f in files) {
                string ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext != ".par" && ext != ".psm" && ext != ".asm" && ext != ".dft") continue;
                RenameRow old;
                if (have.TryGetValue(f, out old)) { if (old.Master != null) { old.Master = null; old.Pinned = false; } continue; }
                var row = new RenameRow { Path = f, Locked = LockReason(f) };
                rows.Add(row); have[f] = row;
            }
            SyncFollowers();
            Rebuild();
        }

        // 装配引用的标准件库（天工安装目录、Program Files 下的）是所有项目共用的：查找替换一不小心把库里的螺钉改了名，
        // 别的项目全部找不到件。只读的文件到最后一步才报"改不了"（那时文档都关了）。载入时就标出来，改了新名字直接报问题
        static string LockReason(string path){
            try {
                string full = Path.GetFullPath(path);
                foreach (var root in new[]{ Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData) }) {
                    if (string.IsNullOrEmpty(root)) continue;
                    if (full.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return "这是标准件库 / 程序目录里的文件（所有项目共用），不改名";
                }
                // 装配引用了、磁盘上却没有的文件（被移走 / 删了）：原来照样能填新名字，到最后一步（文档都关了）才报"被占用或只读"
                if (!File.Exists(full)) return "文件不在了（装配引用的这个文件被移走或删了），改不了名";
                if ((File.GetAttributes(full) & FileAttributes.ReadOnly) != 0) return "文件是只读的，改不了名（先在资源管理器里去掉「只读」）";
            } catch { }
            return "";
        }

        // 跟随行：勾着「跟着改名」时，每个零件 / 装配后面挂上同文件夹里的同名工程图、.cfg；没勾就全拿掉
        void SyncFollowers(){
            rows.RemoveAll(r => r.Master != null);
            if (!follow.Checked) return;
            var have = new HashSet<string>(rows.Select(r => r.Path), StringComparer.OrdinalIgnoreCase);
            var next = new List<RenameRow>();
            foreach (var r in rows) {
                next.Add(r);
                foreach (var c in RenameRules.Companions(r, Listing(r.Folder), have)) {
                    next.Add(new RenameRow { Path = c, Master = r, Locked = LockReason(c) });
                    have.Add(c);
                }
            }
            rows.Clear(); rows.AddRange(next);
        }
        string[] Listing(string folder){
            string[] names;
            if (folderFiles.TryGetValue(folder, out names)) return names;
            try { names = Directory.GetFiles(folder).Select(Path.GetFileName).ToArray(); } catch { names = new string[0]; }
            folderFiles[folder] = names;
            return names;
        }

        void RemoveSelectedRows(){
            var drop = new HashSet<RenameRow>(SelectedRows(true));
            // 主行移掉了，它的跟随行一起走
            foreach (var r in rows) if (r.Master != null && drop.Contains(r.Master)) drop.Add(r);
            rows.RemoveAll(r => drop.Contains(r));
            Rebuild();
        }

        // ---------- 批量生成 ----------

        // 选中两行以上 = 选中的；否则 = 表里筛出来的全部（跟随行不算：它们跟着主行变）
        List<RenameRow> Targets(){
            // 标准件库里的、只读的不参与批量改（改了也只会标红、把整批挡住）
            var picked = SelectedRows(false).Where(r => r.Locked.Length == 0).ToList();
            if (picked.Count >= 2) return picked;
            var list = new List<RenameRow>();
            foreach (DataGridViewRow g in grid.Rows) { var r = (RenameRow)g.Tag; if (r.Master == null && r.Locked.Length == 0) list.Add(r); }
            return list;
        }
        List<RenameRow> SelectedRows(bool withFollowers = true){
            var set = new SortedDictionary<int, RenameRow>();
            foreach (DataGridViewCell c in grid.SelectedCells) {
                var r = (RenameRow)grid.Rows[c.RowIndex].Tag;
                if (withFollowers || r.Master == null) set[c.RowIndex] = r;
            }
            return set.Values.ToList();
        }

        void ApplyReplace(){
            if (findBox.Text.Length == 0) { status.Text = "先填要查找的字"; return; }
            int n = 0;
            foreach (var r in Targets()) { string next = RenameRules.Replace(r.NewBase, findBox.Text, replaceBox.Text); if (next != r.NewBase) { r.SetNewBase(next); n++; } }
            Done(n, "替换");
        }
        void ApplyAffix(){
            if (prefixBox.Text.Length == 0 && suffixBox.Text.Length == 0) { status.Text = "先填要加在前面或后面的字"; return; }
            var targets = Targets();
            foreach (var r in targets) r.SetNewBase(prefixBox.Text + r.NewBase + suffixBox.Text);
            Done(targets.Count, "加字");
        }
        void ApplyTemplate(){
            string template = templateBox.Text.Trim();
            if (template.Length == 0) { status.Text = "先填模板，比如 {图号}-{零件名称}"; return; }
            var targets = Targets();
            var fields = RenameRules.TemplateFields(template);
            var lacking = new Dictionary<string, int>();
            int n = 0, skipped = 0;
            BeginBusy();
            try {
                for (int i = 0; i < targets.Count; i++) {
                    var r = targets[i];
                    if (fields.Count > 0 && r.Extension.ToLowerInvariant() != ".dft") { Pulse("读属性 " + (i + 1) + " / " + targets.Count + " …"); cad.ReadProps(r); }
                    var missing = new List<string>();
                    string name = RenameRules.Template(template, r.OldBase, i + 1, r.Props, missing);
                    // 缺属性的不改（改成半截名字比不改更糟）
                    if (missing.Count > 0 || name.Trim().Length == 0) { skipped++; foreach (var m in missing) lacking[m] = lacking.ContainsKey(m) ? lacking[m] + 1 : 1; continue; }
                    r.SetNewBase(name); n++;
                }
            } finally { EndBusy(); }
            Done(n, "按模板生成");
            if (skipped > 0) {
                status.Text += "；" + skipped + " 个文件没改：缺属性 " + string.Join("、", lacking.Select(p => "「" + p.Key + "」" + p.Value + " 个").ToArray());
                status.ForeColor = Ui.Warn;
            }
        }
        void Done(int n, string what){
            Refresh2();
            status.Text = what + "：改了 " + n + " 个新名字（还没真正改名，点「改名」才生效）";
            status.ForeColor = Ui.Muted;
        }

        // Ctrl+V：一个值 = 填满选中的行；一列值 = 从当前行往下依次填
        void Paste(){
            if (grid.IsCurrentCellInEditMode) return;
            string text = "";
            try { if (Clipboard.ContainsText()) text = Clipboard.GetText(); } catch { }
            var block = PropCardFields.ParseClipboard(text);
            if (block.Count == 0) return;
            if (block.Count == 1) { foreach (var r in SelectedRows()) r.SetNewBase(block[0][0]); Refresh2(); return; }
            int top = int.MaxValue;
            foreach (DataGridViewCell c in grid.SelectedCells) top = Math.Min(top, c.RowIndex);
            if (top == int.MaxValue) return;
            // 跟着改名的同名工程图（灰字行）夹在零件中间：从 Excel 粘一列零件新名字时跳过它们，
            // 原来逐行往下填，第一张工程图之后的名字全错位一行（工程图拿到下一个零件的名字）
            int at = top;
            for (int i = 0; i < block.Count && at < grid.Rows.Count; i++, at++) {
                while (at < grid.Rows.Count && ((RenameRow)grid.Rows[at].Tag).Master != null && at != top) at++;
                if (at >= grid.Rows.Count) break;
                ((RenameRow)grid.Rows[at].Tag).SetNewBase(block[i][0]);
            }
            Refresh2();
        }

        // ---------- 表格 ----------

        void Rebuild(){
            RenameRules.Validate(rows, File.Exists);
            loading = true;
            grid.SuspendLayout();
            try {
                grid.Rows.Clear();
                string key = filter.Text.Trim();
                foreach (var r in rows) {
                    if (key.Length > 0 && r.FileName.IndexOf(key, StringComparison.OrdinalIgnoreCase) < 0 && r.NewFileName.IndexOf(key, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (stateFilter == 1 && !r.Changed) continue;
                    if (stateFilter == 2 && r.Problem.Length == 0) continue;
                    int at = grid.Rows.Add(r.Kind, r.FileName, r.NewBase, r.Extension, Note(r));
                    grid.Rows[at].Tag = r;
                }
            } finally { grid.ResumeLayout(); loading = false; }
            UpdateStatus();
        }

        // 改了名字后只刷新格子，不重建（重建会丢掉选区和滚动位置）
        void Refresh2(){
            RenameRules.Validate(rows, File.Exists);
            loading = true;
            try {
                foreach (DataGridViewRow g in grid.Rows) {
                    var r = (RenameRow)g.Tag;
                    g.Cells[ColNew].Value = r.NewBase;
                    g.Cells[4].Value = Note(r);
                }
            } finally { loading = false; }
            grid.Invalidate();
            UpdateStatus();
        }

        static string Note(RenameRow r){
            if (r.Problem.Length > 0) return r.Problem;
            if (r.Locked.Length > 0) return "（" + r.Locked + "）";
            if (r.Master != null) return r.Pinned ? "同名" + r.Kind + "（单独改过名字）" : "跟着「" + r.Master.FileName + "」改名";
            if (r.Changed) return "→ " + r.NewFileName;
            return "";
        }

        void OnFormat(object sender, DataGridViewCellFormattingEventArgs e){
            if (e.RowIndex < 0) return;
            var r = grid.Rows[e.RowIndex].Tag as RenameRow;
            if (r == null) return;
            var cell = grid.Rows[e.RowIndex].Cells[e.ColumnIndex];
            if (e.ColumnIndex == 1) cell.ToolTipText = r.Path;
            if (r.Master != null && e.ColumnIndex != 4) e.CellStyle.ForeColor = Ui.Muted;
            else if (e.ColumnIndex != ColNew) e.CellStyle.ForeColor = Ui.Text2;
            if (e.ColumnIndex == 4 && r.Problem.Length > 0) e.CellStyle.ForeColor = Ui.Bad;
            if (e.ColumnIndex == ColNew && r.Changed) e.CellStyle.BackColor = r.Problem.Length > 0 ? Color.FromArgb(0xFE, 0xE2, 0xE2) : Ui.WarnSoft;
        }

        void UpdateStatus(){
            int changed = rows.Count(r => r.Changed), bad = rows.Count(r => r.Problem.Length > 0);
            run.Text = changed > 0 ? "改名 " + changed + " 个文件" : "改名";
            run.Enabled = changed > 0 && bad == 0;
            shell.Rail.SetCount(0, rows.Count.ToString(), false);
            shell.Rail.SetCount(1, changed.ToString(), changed == 0);
            shell.Rail.SetCount(2, bad.ToString(), bad == 0);
            status.ForeColor = bad > 0 ? Ui.Bad : Ui.Muted;
            status.Text = rows.Count == 0 ? "没有文件：点左边的「载入当前文档」或「添加文件…」"
                : bad > 0 ? bad + " 个新名字有问题（看「说明」一列），改好才能改名"
                : "共 " + rows.Count + " 个文件" + (grid.Rows.Count != rows.Count ? "（筛出 " + grid.Rows.Count + " 个）" : "") + (changed > 0 ? "　·　要改名 " + changed + " 个" : "");
        }

        bool ConfirmDiscard(){
            int n = rows.Count(r => r.Changed);
            if (n == 0) return true;
            return MessageBox.Show(this, n + " 个新名字还没改名，继续就丢掉。继续吗？", Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        }

        // ---------- 改名 ----------

        bool quiet, quietAnswer = true;   // 测试用：不弹框，确认框按 quietAnswer 答，问的内容记在 lastConfirm
        string lastConfirm = "";
        void RunRename(){
            if (grid.IsCurrentCellInEditMode) grid.EndEdit();
            if (RenameRules.Validate(rows, File.Exists) > 0) { Rebuild(); return; }
            var changed = rows.Where(r => r.Changed).ToList();
            if (changed.Count == 0) return;
            int drafts = changed.Count(r => r.Master != null);
            BatchRenameCad.Result result;
            BeginBusy();
            run.Enabled = false;
            try {
                result = cad.Run(rows, (closing, dirty) => {
                    string text = "把 " + changed.Count + " 个文件改名" + (drafts > 0 ? "（含 " + drafts + " 个跟着改的同名工程图 / 显示配置）" : "") + "？\r\n"
                        + "装配、工程图里引用它们的地方会一起改好（查的是表里的文件、这些文件所在的文件夹，和上一级文件夹往下两层的工程图；"
                        + "放在更远地方、也引用了它们的装配不会跟着改，要改就用「添加文件…」把它加进表里）。";
                    if (closing.Length > 0) text += "\r\n\r\n天工里开着的这些文件要先关掉，改完自动重新打开：\r\n" + closing;
                    // 天工刚打开、没动过的文件也常显示"有修改"，所以只提醒、让用户自己判断
                    if (dirty.Length > 0) text += "\r\n\r\n其中这些显示有没保存的修改，关掉时不会保存：\r\n" + dirty
                        + "\r\n如果你改过东西，请点「取消」，回天工 Ctrl+S 保存后再改名；没改过（天工打开文件时自己也会标成已修改）就点「确定」。";
                    lastConfirm = text;
                    if (quiet) return quietAnswer;
                    return MessageBox.Show(this, text, Title, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK;
                }, Pulse);
            } finally { EndBusy(); }
            lastResult = result;
            if (result.Error.Length == 0) {
                // 表里换成新名字（行还在，方便接着改）
                foreach (var r in changed) { r.Path = r.NewPath; r.Revert(); }
                folderFiles.Clear();
                Rebuild();
                status.Text = result.Summary() + (result.Warnings.Count > 0 ? "；" + string.Join("；", result.Warnings.ToArray()) : "");
                status.ForeColor = result.Warnings.Count > 0 ? Ui.Warn : Ui.Ok;
                shell.ShowToast("已改名 " + result.Renamed + " 个文件，引用都已更新", result.Warnings.Count > 0 ? Ui.Warn : Ui.Ok, null, null);
            } else {
                Rebuild();
                if (result.Error == "已取消") { status.Text = "已取消，什么都没改"; return; }
                status.Text = "没有改名：" + result.Error.Split('\r')[0];
                status.ForeColor = Ui.Bad;
                if (!quiet) MessageBox.Show(this, "没有改名，文件和引用都保持原样。\r\n\r\n" + result.Error + (result.Warnings.Count > 0 ? "\r\n\r\n" + string.Join("\r\n", result.Warnings.ToArray()) : ""), Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        BatchRenameCad.Result lastResult;

#if TG_DEV_BUILD
        // 真机探针用（Diagnostics.StartRenameFormTest）：走窗口的真实路径 —— 载入 → 查找替换 / 模板 → 改名
        internal string TestRun(string find, string replace, string template, string onlyUnder, bool answer){
            quiet = true; quietAnswer = answer;
            LoadActive();
            int loaded = rows.Count;
            // 只留 onlyUnder 文件夹里的文件（测试副本的装配还引用着副本外面的原件，绝不能碰）
            if (!string.IsNullOrEmpty(onlyUnder)) { string under = Path.GetFullPath(onlyUnder).TrimEnd('\\') + "\\"; rows.RemoveAll(r => !Path.GetFullPath(r.Path).StartsWith(under, StringComparison.OrdinalIgnoreCase)); SyncFollowers(); Rebuild(); }
            grid.ClearSelection();
            if (!string.IsNullOrEmpty(find)) { findBox.Text = find; replaceBox.Text = replace ?? ""; ApplyReplace(); }
            if (!string.IsNullOrEmpty(template)) { templateBox.Text = template; ApplyTemplate(); }
            string before = status.Text + " | button=" + run.Text + " | followers=" + rows.Count(r => r.Master != null);
            var plan = string.Join(" ; ", rows.Where(r => r.Changed).Select(r => r.FileName + "→" + r.NewFileName).ToArray());
            RunRename();
            var res = lastResult;
            return "CONFIRM " + lastConfirm.Replace("\r\n", " / ") + "\nloaded=" + loaded + " rows=" + rows.Count + " | " + before + "\nPLAN " + plan + "\nAFTER " + status.Text
                + (res == null ? "" : "\nTIMING filter=" + res.FilterMs + " start=" + res.StartMs + " close=" + res.CloseMs + " scan=" + res.ScanMs + " apply=" + res.ApplyMs + " reopen=" + res.ReopenMs + " total=" + res.TotalMs + " scanned=" + res.Scanned + " skipped=" + res.Skipped + " error=" + res.Error.Replace("\r\n", " / ") + " warnings=" + string.Join(" / ", res.Warnings.ToArray()) + " reopened=" + string.Join(",", res.Reopened.ToArray()));
        }
        internal void TestClose(){ Close(); }
        internal void TestShot(string png){
            Application.DoEvents();
            using (var bmp = new Bitmap(Width, Height)) { DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height)); bmp.Save(png); }
        }
#endif
    }
}
