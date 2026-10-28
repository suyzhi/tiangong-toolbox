using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using F = SolidEdgeFramework;

namespace TianGongCadSuite {
    // 属性卡填写（命令 10）窗口：一行一个文件、一列一个自定义属性，像 Excel 一样改，点「写入」一次落盘。
    //
    // 好用的三招：选一片格子 → 上面输入框填值 → 「填入」；从 Excel 复制一块 → Ctrl+V；选中 → Delete 清空。
    // 改过的格子标黄，文件里还没有的属性标灰；只写改过的文件（标准件、外购件不会被碰）。
    public sealed class PropCardForm : Form, IBusyTool {
        static readonly Color EditedBg = Ui.WarnSoft;
        static readonly Color MissingBg = Ui.Subtle;
        const int FixedColumns = 2;   // 文件、材料

        readonly F.Application app;
        readonly PropCardCad cad;
        readonly List<PropCardRow> rows = new List<PropCardRow>();
        List<string> fields;
        readonly DataGridView grid = new DataGridView();
        readonly TextBox filter = new TextBox();
        readonly TextBox fillBox = new TextBox();
        readonly CheckBox fillMissing = new CheckBox();
        readonly Label status = new Label();
        readonly Button write;
        bool loading, busy;
        public bool Busy { get { return busy; } }
        public string BusyWhat { get { return "读写属性"; } }
        // 一百多个文件要读写十几秒到几十秒：期间隔一会儿泵一次消息（窗口禁用着，点不了），不然 Windows 把天工判成"未响应"
        readonly System.Diagnostics.Stopwatch pulse = new System.Diagnostics.Stopwatch();
        void Pulse(string text){
            if (pulse.IsRunning && pulse.ElapsedMilliseconds < 250) return;
            pulse.Restart();
            status.Text = text; status.Refresh();
            try { Application.DoEvents(); } catch { }
        }
        void BeginBusy(){ busy = true; Enabled = false; Cursor = Cursors.WaitCursor; pulse.Reset(); }
        void EndBusy(){ busy = false; Enabled = true; Cursor = Cursors.Default; }

        public PropCardForm(F.Application application){
            app = application;
            cad = new PropCardCad(application);
            fields = PropCardFields.Load(SettingsFile.PathOf(PropCardFields.FileName));
            Ui.Shell(this, Product.Title("属性卡填写"), 1120, 680, 860, 480);
            FormBorderStyle = FormBorderStyle.Sizable;

            // ---- 0.9.0「D」：侧栏放文件来源、筛选；正文上面一行"把选中的格子填成"，下面表格；底部写入 ----
            Controls.Add(shell);
            var page = shell.AddPage("属性卡", "属性卡", "");
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
            // 侧栏中段：按状态筛（全部 / 改过未写入 / 缺属性 / 读取出错）
            shell.Rail.Visible = true;
            shell.Rail.Kind = StepRail.Mode.Nav;
            shell.Rail.Add("全部文件", "");
            shell.Rail.Add("改过未写入", "").Dot = Color.FromArgb(0xF5, 0x9E, 0x0B);
            shell.Rail.Add("缺属性", "").Dot = Color.FromArgb(0xA1, 0xA1, 0xAA);
            shell.Rail.Add("读取出错", "").Dot = Ui.Bad;
            shell.Rail.SelectedChanged += (s, e) => { stateFilter = shell.Rail.Selected; Rebuild(); };
            filter.TextChanged += (s, e) => Rebuild();
            // 侧栏底部：顺便补齐缺的属性
            var foot = new Panel { BackColor = Ui.Rail, Height = 64 };
            fillMissing.Text = "写入时顺便补齐缺的属性（标题栏不再显示“错误：没有参考”）";
            fillMissing.Checked = true; Choices.Bind(fillMissing, "propcard.fillMissing"); fillMissing.AutoSize = false; fillMissing.BackColor = Ui.Rail; fillMissing.Font = Ui.F8;
            fillMissing.CheckAlign = ContentAlignment.TopLeft; fillMissing.TextAlign = ContentAlignment.TopLeft;
            fillMissing.Dock = DockStyle.Fill;
            foot.Controls.Add(fillMissing);
            shell.RailBottom = foot;

            // ---- 正文 ----
            var remove = Ui.Small("移除选中行"); remove.Click += (s, e) => RemoveSelectedRows();
            var columns = Ui.Small("列…"); columns.Click += (s, e) => EditColumns();
            // 撤销修改 = 丢掉全部没写入的格子：手一滑点到就几十格白填，先问一句
            var revert = Ui.Small("撤销修改"); revert.Click += (s, e) => {
                int dirty = rows.Count(r => r.Dirty);
                if (dirty == 0) return;
                if (MessageBox.Show(this, "丢掉 " + dirty + " 个文件里还没写入的修改？", "属性卡填写", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                foreach (var r in rows) r.Edited.Clear(); Rebuild();
            };
            foreach (var b2 in new[] { remove, columns, revert }) ((FlatButton)b2).SetFore(Ui.Text2);
            shell.AddHeader(page, remove); shell.AddHeader(page, columns); shell.AddHeader(page, revert);
            var stack = new VerticalStack { Dock = DockStyle.Fill, Padding = new Padding(0), Gap = 12, AutoScroll = false };
            page.Body.Controls.Add(stack);
            var bar = new Panel { Height = 56, BackColor = Ui.Bg };
            var lv = new Label { Text = "把选中的格子填成", AutoSize = false, Font = Ui.F8, ForeColor = Ui.Text2, BackColor = Ui.Bg };
            var fill = Ui.Secondary("填入", 72, 30);
            var tip = Ui.Muted8("Ctrl+V 粘贴（可从 Excel 整块复制）· Delete 清空 · 双击改单格"); tip.BackColor = Ui.Bg;
            fillBox.BorderStyle = BorderStyle.FixedSingle; fillBox.Font = Ui.F9;
            lv.SetBounds(0, 0, 200, 20); fillBox.SetBounds(0, 24, 240, 28);
            fill.Location = new Point(250, 23); tip.Location = new Point(334, 31);
            bar.Controls.AddRange(new Control[] { lv, fillBox, fill, tip });
            fill.Click += (s, e) => SetSelected(fillBox.Text);
            fillBox.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { SetSelected(fillBox.Text); e.SuppressKeyPress = true; } };
            var gridFrame = Ui.Card("", 0, "");
            gridFrame.Padding = new Padding(2, 2, 2, 8);
            BuildGrid();
            gridFrame.Controls.Add(grid);
            var legend = new Label { Text = "黄底 = 改过还没写入　灰底 = 文件里还没有这个属性", AutoSize = false, Height = 18, Font = Ui.F8, ForeColor = Ui.Muted, BackColor = Ui.Bg };
            stack.Add(bar); stack.Add(gridFrame, true); stack.Add(legend);

            // ---- 底部 ----
            write = Ui.Primary("写入", 220, 44);
            var close = Ui.Secondary("关闭", 84, 38);
            write.Click += (s, e) => WriteAll();
            close.Click += (s, e) => Close();
            shell.Footer.Padding = new Padding(28, 0, 20, 0);
            shell.Footer.AddRight(close); shell.Footer.AddRight(write);
            shell.Footer.Status = status;
            status.ForeColor = Ui.Muted;
            FormClosing += OnClosing;
            UpdateStatus();
        }
        readonly WizardShell shell = new WizardShell { StepCaption = false };
        int stateFilter;   // 0 全部 / 1 改过未写入 / 2 缺属性 / 3 读取出错
        bool Missing(PropCardRow r){ foreach (var f in fields) if (!r.Has(f)) return true; return false; }

        void BuildGrid(){
            grid.Dock = DockStyle.Fill;
            grid.BackgroundColor = Color.White; grid.BorderStyle = BorderStyle.None;
            grid.AllowUserToAddRows = false; grid.AllowUserToDeleteRows = false; grid.AllowUserToResizeRows = false;
            grid.RowHeadersVisible = false;
            grid.SelectionMode = DataGridViewSelectionMode.CellSelect; grid.MultiSelect = true;
            grid.EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2;
            grid.ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithoutHeaderText;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
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
            grid.RowTemplate.Height = 32;
            grid.DefaultCellStyle.Padding = new Padding(6, 0, 0, 0);
            grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(0xE0, 0xE7, 0xFF);
            grid.DefaultCellStyle.SelectionForeColor = Ui.Text;
            grid.ShowCellToolTips = true;
            grid.CellBeginEdit += (s, e) => {
                var r = e.RowIndex >= 0 ? grid.Rows[e.RowIndex].Tag as PropCardRow : null;
                if (r != null && r.Problem.Length > 0) { e.Cancel = true; status.Text = "这个文件读取失败，不能改（" + r.Problem + "）"; }
                else if (r != null && r.Locked.Length > 0) { e.Cancel = true; status.Text = r.FileName + "：" + r.Locked; }
            };
            grid.CellValueChanged += (s, e) => {
                if (loading || e.RowIndex < 0 || e.ColumnIndex < FixedColumns) return;
                var cell = grid.Rows[e.RowIndex].Cells[e.ColumnIndex];
                SetCell(grid.Rows[e.RowIndex], e.ColumnIndex, Convert.ToString(cell.Value));
                UpdateStatus();
            };
            grid.CellFormatting += OnFormat;
            grid.KeyDown += (s, e) => {
                if (e.Control && e.KeyCode == Keys.V) { Paste(); e.Handled = true; }
                else if (e.KeyCode == Keys.Delete) { SetSelected(""); e.Handled = true; }
            };
            grid.DataError += (s, e) => { e.ThrowException = false; };
        }

        // ---------- 载入 ----------

        public void LoadActive(){
            string source;
            List<string> files;
            // 先问要不要丢掉没写入的修改，再收集：收集会把"哪些文件在 CAD 内存里"换成新文档的。
            // 原来先收集后问，用户点"否"留下旧修改，之后写入时按新文档去找已加载的零件 —— 找不到就写磁盘，
            // 正是 0.8.2 修掉的那个"加载后已被其他用户修改"对话框，一个文件弹一个。
            if (!ConfirmDiscard()) return;
            try { files = cad.Collect(out source); }
            catch (Exception e) { Log.Write("PropCardCollect", e); status.Text = "读当前文档失败：" + e.Message; return; }
            rows.Clear();
            shell.SetTitle("属性卡", source);
            ReadInto(files);
            if (cad.skippedSubs.Count > 0) {
                status.Text = "注意：" + cad.skippedSubs.Count + " 个子装配读不出来，里面的零件没收进来（" + string.Join("、", cad.skippedSubs.Take(3).ToArray()) + (cad.skippedSubs.Count > 3 ? " 等" : "") + "）。等天工加载完再点「载入当前文档」。";
                status.ForeColor = Ui.Warn;
            }
        }

        void AddFiles(){
            using (var dlg = new OpenFileDialog()) {
                dlg.Multiselect = true;
                dlg.Title = "添加要填属性的文件";
                dlg.Filter = "天工零件 / 钣金 / 装配|*.par;*.psm;*.asm";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                ReadInto(new List<string>(dlg.FileNames));
            }
        }

        // 逐个读属性，已在表里的跳过
        void ReadInto(List<string> files){
            var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in rows) have.Add(r.Path);
            BeginBusy();
            try {
                int n = 0;
                foreach (var f in files) {
                    n++;
                    Pulse("读取中 " + n + " / " + files.Count + " …");
                    if (!have.Add(f)) continue;
                    var row = cad.Read(f);
                    row.Locked = LockReason(f);
                    rows.Add(row);
                }
            } finally { EndBusy(); }
            Rebuild();
        }

        void RemoveSelectedRows(){
            var drop = new HashSet<PropCardRow>();
            foreach (DataGridViewCell c in grid.SelectedCells) drop.Add((PropCardRow)grid.Rows[c.RowIndex].Tag);
            // 移掉的行里有改过没写入的：原来一声不响就丢了
            int dirty = drop.Count(r => r.Dirty);
            if (dirty > 0 && MessageBox.Show(this, "要移除的行里有 " + dirty + " 个文件改过还没写入，移除就丢掉这些修改。继续吗？", "属性卡填写",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            rows.RemoveAll(r => drop.Contains(r));
            Rebuild();
        }

        // 装配引用的标准件库（天工安装目录、Program Files 下）是所有项目共用的：整列填「项目名称」时把库里的螺钉也写了，
        // 保存装配时要么改了共用库、要么因为只读一个个弹框。这些行灰掉、不参与填值
        static string LockReason(string path){
            try {
                string full = System.IO.Path.GetFullPath(path);
                foreach (var root in new[]{ Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData) }) {
                    if (string.IsNullOrEmpty(root)) continue;
                    if (full.StartsWith(root.TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return "标准件库 / 程序目录里的共用文件，不写属性";
                }
            } catch { }
            return "";
        }

        // ---------- 表格 ----------

        void Rebuild(){
            // 写入 / 换筛选 / 改列以后表格是重建的：原来排序、滚动位置、当前格子全回到开头 ——
            // 一百多行的表按某列排好、填到一半点「写入」，又得重新排、重新找到刚才那一行（0.10.3）
            var sortCol = grid.SortedColumn; string sortField = sortCol == null ? null : (sortCol.Tag as string ?? "#" + sortCol.Index);
            var sortDir = grid.SortOrder == SortOrder.Descending ? System.ComponentModel.ListSortDirection.Descending : System.ComponentModel.ListSortDirection.Ascending;
            int firstRow = -1; try { firstRow = grid.FirstDisplayedScrollingRowIndex; } catch { }
            PropCardRow curRow = grid.CurrentCell != null && grid.CurrentCell.RowIndex >= 0 ? grid.Rows[grid.CurrentCell.RowIndex].Tag as PropCardRow : null;
            string curField = grid.CurrentCell != null ? (grid.Columns[grid.CurrentCell.ColumnIndex].Tag as string ?? "#" + grid.CurrentCell.ColumnIndex) : null;
            RebuildRows();
            try {
                if (sortField != null) foreach (DataGridViewColumn c in grid.Columns) if ((c.Tag as string ?? "#" + c.Index) == sortField) { grid.Sort(c, sortDir); break; }
                if (curRow != null) foreach (DataGridViewRow r in grid.Rows) if (r.Tag == curRow) {
                    foreach (DataGridViewColumn c in grid.Columns) if ((c.Tag as string ?? "#" + c.Index) == curField && c.Visible) { grid.CurrentCell = r.Cells[c.Index]; break; }
                    break;
                }
                if (firstRow >= 0 && grid.Rows.Count > 0) grid.FirstDisplayedScrollingRowIndex = Math.Min(firstRow, grid.Rows.Count - 1);
            } catch (Exception e) { Log.Write("PropCardRestoreView", e.Message); }
        }
        void RebuildRows(){
            loading = true;
            grid.SuspendLayout();
            try {
                grid.Rows.Clear(); grid.Columns.Clear();
                AddColumn("文件", null, 230, true).Frozen = true;
                AddColumn("材料", null, 90, true);
                foreach (var f in fields) AddColumn(f, f, Math.Max(80, TextRenderer.MeasureText(f, Ui.F9B).Width + 24), false);
                string key = filter.Text.Trim();
                foreach (var r in rows) {
                    if (key.Length > 0 && r.FileName.IndexOf(key, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (stateFilter == 1 && !r.Dirty) continue;
                    if (stateFilter == 2 && (r.Problem.Length > 0 || !Missing(r))) continue;
                    if (stateFilter == 3 && r.Problem.Length == 0) continue;
                    var values = new object[FixedColumns + fields.Count];
                    values[0] = r.FileName; values[1] = r.Problem.Length > 0 ? "（读取失败）" : r.Material;
                    for (int i = 0; i < fields.Count; i++) values[FixedColumns + i] = r.Value(fields[i]);
                    int at = grid.Rows.Add(values);
                    grid.Rows[at].Tag = r;
                }
            } finally { grid.ResumeLayout(); loading = false; }
            UpdateStatus();
        }

        DataGridViewTextBoxColumn AddColumn(string header, string field, int width, bool readOnly){
            var c = new DataGridViewTextBoxColumn { HeaderText = header, Tag = field, Width = width, ReadOnly = readOnly, SortMode = DataGridViewColumnSortMode.Automatic };
            if (readOnly) c.DefaultCellStyle.ForeColor = Ui.Muted;
            grid.Columns.Add(c);
            return c;
        }

        void OnFormat(object sender, DataGridViewCellFormattingEventArgs e){
            if (e.RowIndex < 0) return;
            var r = grid.Rows[e.RowIndex].Tag as PropCardRow;
            if (r == null) return;
            var cell = grid.Rows[e.RowIndex].Cells[e.ColumnIndex];
            if (e.ColumnIndex < FixedColumns) {
                cell.ToolTipText = r.Problem.Length > 0 ? r.Problem : r.Locked.Length > 0 ? r.Path + "\r\n" + r.Locked : r.Path;
                if (r.Locked.Length > 0) e.CellStyle.ForeColor = Ui.Muted;
                if (r.Problem.Length > 0) e.CellStyle.ForeColor = Ui.Bad;
                return;
            }
            string field = (string)grid.Columns[e.ColumnIndex].Tag;
            if (r.IsEdited(field)) { e.CellStyle.BackColor = EditedBg; cell.ToolTipText = "改过，还没写入"; }
            else if (!r.Has(field)) { e.CellStyle.BackColor = MissingBg; cell.ToolTipText = "文件里还没有「" + field + "」这个属性"; }
            else cell.ToolTipText = "";
        }

        void SetCell(DataGridViewRow gridRow, int column, string value){
            if (column < FixedColumns) return;
            var r = (PropCardRow)gridRow.Tag;
            if (r.Problem.Length > 0) return;   // 读失败的文件不改（见 WriteAll）
            if (r.Locked.Length > 0) return;    // 标准件库里的共用件：整列填值、粘贴都跳过
            string field = (string)grid.Columns[column].Tag;
            r.Set(field, value);
            loading = true;
            try { gridRow.Cells[column].Value = r.Value(field); } finally { loading = false; }
            grid.InvalidateCell(column, gridRow.Index);
        }

        void SetSelected(string value){
            if (grid.IsCurrentCellInEditMode) grid.EndEdit();
            int n = 0;
            foreach (DataGridViewCell c in grid.SelectedCells) {
                if (c.ColumnIndex < FixedColumns) continue;
                SetCell(grid.Rows[c.RowIndex], c.ColumnIndex, value); n++;
            }
            if (n == 0) status.Text = "先在表里选中要填的格子（文件名、材料两列不能改）";
            else UpdateStatus();
        }

        // 一个值：填满所有选中的格子；一块值：从选区左上角开始照搬
        void Paste(){
            string text = "";
            try { if (Clipboard.ContainsText()) text = Clipboard.GetText(); } catch { }
            var block = PropCardFields.ParseClipboard(text);
            if (block.Count == 0) return;
            if (block.Count == 1 && block[0].Length == 1) { SetSelected(block[0][0]); return; }
            int top = int.MaxValue, left = int.MaxValue;
            foreach (DataGridViewCell c in grid.SelectedCells) { top = Math.Min(top, c.RowIndex); left = Math.Min(left, c.ColumnIndex); }
            if (top == int.MaxValue) return;
            for (int i = 0; i < block.Count && top + i < grid.Rows.Count; i++)
                for (int j = 0; j < block[i].Length && left + j < grid.Columns.Count; j++)
                    SetCell(grid.Rows[top + i], left + j, block[i][j]);
            UpdateStatus();
        }

        void EditColumns(){
            string text = Ui.Prompt(this, "列", "要填的属性名，用顿号或逗号隔开（留空恢复默认）", PropCardFields.Join(fields));
            if (text == null) return;
            var next = PropCardFields.Parse(text);
            next = next.Count > 0 ? next : new List<string>(PropCardFields.Default);
            // 去掉的列上有改过还没写入的格子（0.8.5）：原来这些修改既不写、也不清，行一直标着"改过"，写入按钮永远灰不下去
            int lost = 0;
            foreach (var r in rows) foreach (var k in r.Edited.Keys) if (!next.Contains(k)) lost++;
            if (lost > 0) {
                if (MessageBox.Show(this, "去掉的列里有 " + lost + " 个格子改过还没写入，去掉列就丢掉这些修改。继续吗？", "属性卡填写", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                foreach (var r in rows) foreach (var k in r.Edited.Keys.ToList()) if (!next.Contains(k)) r.Edited.Remove(k);
            }
            fields = next;
            try { PropCardFields.Save(SettingsFile.PathOf(PropCardFields.FileName), fields); } catch (Exception e) { Log.Write("PropCardFieldsSave", e); }
            Rebuild();
        }

        // ---------- 写入 ----------

        void WriteAll(){
            if (grid.IsCurrentCellInEditMode) grid.EndEdit();
            if (!cad.Rebind()) {
                status.Text = "载入这张表的「" + cad.RootName + "」已经关掉了：在天工里重新打开它（设成当前文档）再点写入，表里改过的值都还在。";
                status.ForeColor = Ui.Bad;
                return;
            }
            int ok = 0, total = 0, unsaved = 0;
            var problems = new List<string>();
            BeginBusy();
            write.Enabled = false;
            try {
                foreach (var r in rows.ToArray()) {
                    if (!r.Dirty) continue;
                    total++;
                    // 读失败的文件不知道原来有哪些属性：照写的话「补齐」会把它原有的属性全当成缺的、写成空值
                    if (r.Problem.Length > 0) { problems.Add(r.FileName + "：读取失败的文件不写（" + r.Problem + "），请先点「载入当前文档」重读"); continue; }
                    Pulse("写入中 " + total + " …　" + r.FileName);
                    var writes = r.Writes(fields, fillMissing.Checked);
                    string result = cad.Write(r, writes);
                    if (result == PropCardCad.SaveHint) { r.Committed(writes); ok++; unsaved++; }
                    else if (result.Length == 0) { r.Committed(writes); ok++; }
                    else problems.Add(r.FileName + "：" + result);
                }
            } finally { EndBusy(); }
            Rebuild();
            status.Text = "写好 " + ok + " / " + total + " 个文件"
                + (unsaved > 0 ? "；其中 " + unsaved + " 个在 CAD 里开着，写进了 CAD，请回 CAD 按 Ctrl+S 保存（装配 / 工程图里的零件会一起保存）" : "")
                + (problems.Count > 0 ? "；" + problems.Count + " 个没写进去" : "");
            status.ForeColor = problems.Count > 0 || unsaved > 0 ? Ui.Warn : Ui.Ok;
            lastProblems = problems;
            if (problems.Count > 0 && !quiet) {
                var lines = problems.Count > 15 ? problems.GetRange(0, 15) : problems;
                MessageBox.Show(this, string.Join("\r\n", lines.ToArray()) + (problems.Count > 15 ? "\r\n……共 " + problems.Count + " 条，详见 panel.log" : ""),
                    "属性卡填写", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        void UpdateStatus(){
            int files = 0, cells = 0;
            foreach (var r in rows) if (r.Dirty) { files++; cells += r.Edited.Count; }
            write.Text = files > 0 ? "写入 " + files + " 个文件" : "写入";
            write.Enabled = files > 0;
            shell.Rail.SetCount(0, rows.Count.ToString(), false);
            shell.Rail.SetCount(1, files.ToString(), files == 0);
            int miss = rows.Count(r => r.Problem.Length == 0 && Missing(r)), bad = rows.Count(r => r.Problem.Length > 0);
            shell.Rail.SetCount(2, miss.ToString(), miss == 0);
            shell.Rail.SetCount(3, bad.ToString(), bad == 0);
            status.ForeColor = Ui.Muted;
            status.Text = rows.Count == 0 ? "没有文件：点左边的「载入当前文档」或「添加文件…」"
                : "共 " + rows.Count + " 个文件" + (grid.Rows.Count != rows.Count ? "（筛出 " + grid.Rows.Count + " 个）" : "") + (files > 0 ? "　·　改了 " + files + " 个文件 " + cells + " 格" : "");
        }

        bool ConfirmDiscard(){
            int files = 0;
            foreach (var r in rows) if (r.Dirty) files++;
            if (files == 0) return true;
            return MessageBox.Show(this, files + " 个文件的修改还没写入，继续就丢掉这些修改。继续吗？", "属性卡填写",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        }

        bool forceClose, quiet;
        List<string> lastProblems = new List<string>();
        void OnClosing(object sender, FormClosingEventArgs e){
            if (busy && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; return; }
            if (!forceClose && !ConfirmDiscard()) e.Cancel = true;
        }

#if TG_DEV_BUILD
        // 真机探针用（Diagnostics.PropCardFormTest）：走窗口的真实路径 —— 选中某列前 n 行 → 填入 → 写入
        internal string TestFillAndWrite(string field, string value, int rowCount){
            int col = -1;
            for (int i = 0; i < grid.Columns.Count; i++) if ((string)grid.Columns[i].Tag == field) col = i;
            if (col < 0) return "no column " + field;
            grid.ClearSelection();
            for (int i = 0; i < rowCount && i < grid.Rows.Count; i++) grid.Rows[i].Cells[col].Selected = true;
            quiet = true;
            fillBox.Text = value;
            SetSelected(value);
            string before = status.Text + " | button=" + write.Text;
            WriteAll();
            return "rows=" + rows.Count + " | " + before + " | after: " + status.Text + " | problems: " + string.Join(" ; ", lastProblems.ToArray());
        }
        internal void TestForceClose(){ forceClose = true; Close(); }
        // 截图用：改几格，让黄色 / 灰色都出现
        internal void TestEditSome(){
            for (int i = 0; i < 3 && i < grid.Rows.Count; i++) SetCell(grid.Rows[i], FixedColumns, "示例零件" + (i + 1));
            if (grid.Rows.Count > 4) SetCell(grid.Rows[4], FixedColumns + 2, "模组PACK实验室项目");
            UpdateStatus();
        }
#endif
    }
}
