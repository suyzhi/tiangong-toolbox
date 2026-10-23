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
        static readonly Color EditedBg = Color.FromArgb(0xFF, 0xF4, 0xC2);
        static readonly Color MissingBg = Color.FromArgb(0xEE, 0xF0, 0xF4);
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
        readonly Card fileCard;
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
            Ui.Shell(this, Product.Title("属性卡填写"), 1040, 620, 760, 420);
            FormBorderStyle = FormBorderStyle.Sizable;

            // ---- 文件 + 填值 ----
            fileCard = Ui.Card("文件", 74, "");
            var reload = Ui.Small("载入当前文档"); reload.Click += (s, e) => LoadActive();
            var add = Ui.Small("添加文件…"); add.Click += (s, e) => AddFiles();
            var remove = Ui.Small("移除选中行"); remove.Click += (s, e) => RemoveSelectedRows();
            fileCard.AddHeader(reload); fileCard.AddHeader(add); fileCard.AddHeader(remove);
            var bar = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
            var lf = Ui.Caption("按文件名筛选"); lf.BackColor = Color.White;
            var lv = Ui.Caption("把选中的格子填成"); lv.BackColor = Color.White;
            var fill = Ui.Primary("填入", 64, 26);
            var tip = Ui.Muted8("Ctrl+V 粘贴（可从 Excel 整块复制）· Delete 清空 · 双击改单格"); tip.BackColor = Color.White;
            lf.Location = new Point(0, 7); filter.SetBounds(86, 3, 150, 24);
            lv.Location = new Point(256, 7); fillBox.SetBounds(366, 3, 200, 24);
            fill.Location = new Point(574, 2); tip.Location = new Point(650, 8);
            bar.Controls.AddRange(new Control[] { lf, filter, lv, fillBox, fill, tip });
            fileCard.Controls.Add(bar);
            filter.TextChanged += (s, e) => Rebuild();
            fill.Click += (s, e) => SetSelected(fillBox.Text);
            fillBox.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { SetSelected(fillBox.Text); e.SuppressKeyPress = true; } };

            // ---- 表格 ----
            var gridCard = Ui.Card("属性卡", 0, "黄 = 改过还没写入　灰 = 文件里还没有这个属性");
            var columns = Ui.Small("列…"); columns.Click += (s, e) => EditColumns();
            // 撤销修改 = 丢掉全部没写入的格子：手一滑点到就几十格白填，先问一句
            var revert = Ui.Small("撤销修改"); revert.Click += (s, e) => {
                int dirty = rows.Count(r => r.Dirty);
                if (dirty == 0) return;
                if (MessageBox.Show(this, "丢掉 " + dirty + " 个文件里还没写入的修改？", "属性卡填写", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                foreach (var r in rows) r.Edited.Clear(); Rebuild();
            };
            gridCard.AddHeader(columns); gridCard.AddHeader(revert);
            BuildGrid();
            gridCard.Controls.Add(grid);

            // ---- 底部 ----
            var actions = new Panel { Height = 40, BackColor = Ui.Bg };
            fillMissing.Text = "顺便补齐缺的属性（标题栏不再显示“错误：没有参考”）";
            fillMissing.Checked = true; fillMissing.AutoSize = true; fillMissing.BackColor = Ui.Bg;
            write = Ui.Primary("写入", 150, 32);
            var close = Ui.Secondary("关闭", 80, 30);
            write.Click += (s, e) => WriteAll();
            close.Click += (s, e) => Close();
            status.AutoSize = false; status.BackColor = Ui.Bg; status.ForeColor = Ui.Muted; status.TextAlign = ContentAlignment.MiddleLeft; status.AutoEllipsis = true;
            actions.Controls.AddRange(new Control[] { fillMissing, status, write, close });
            actions.Resize += (s, e) => {
                close.SetBounds(actions.Width - 80, 5, 80, 30);
                write.SetBounds(close.Left - 8 - 150, 4, 150, 32);
                fillMissing.Location = new Point(0, 11);
                status.SetBounds(fillMissing.Right + 12, 4, Math.Max(40, write.Left - fillMissing.Right - 20), 32);
            };

            var stack = new VerticalStack { Dock = DockStyle.Fill };
            stack.Add(fileCard); stack.Add(gridCard, true); stack.Add(actions);
            Controls.Add(stack);
            FormClosing += OnClosing;
            UpdateStatus();
        }

        void BuildGrid(){
            grid.Dock = DockStyle.Fill;
            grid.BackgroundColor = Color.White; grid.BorderStyle = BorderStyle.None;
            grid.AllowUserToAddRows = false; grid.AllowUserToDeleteRows = false; grid.AllowUserToResizeRows = false;
            grid.RowHeadersVisible = false;
            grid.SelectionMode = DataGridViewSelectionMode.CellSelect; grid.MultiSelect = true;
            grid.EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2;
            grid.ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithoutHeaderText;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            grid.Font = Ui.F9;
            grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(0xCF, 0xE3, 0xF8);
            grid.DefaultCellStyle.SelectionForeColor = Ui.Text;
            grid.ShowCellToolTips = true;
            grid.CellBeginEdit += (s, e) => {
                var r = e.RowIndex >= 0 ? grid.Rows[e.RowIndex].Tag as PropCardRow : null;
                if (r != null && r.Problem.Length > 0) { e.Cancel = true; status.Text = "这个文件读取失败，不能改（" + r.Problem + "）"; }
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
            fileCard.Hint = source; fileCard.Invalidate();
            ReadInto(files);
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
                    rows.Add(cad.Read(f));
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

        // ---------- 表格 ----------

        void Rebuild(){
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
                cell.ToolTipText = r.Problem.Length > 0 ? r.Problem : r.Path;
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
            fields = next.Count > 0 ? next : new List<string>(PropCardFields.Default);
            try { PropCardFields.Save(SettingsFile.PathOf(PropCardFields.FileName), fields); } catch (Exception e) { Log.Write("PropCardFieldsSave", e); }
            Rebuild();
        }

        // ---------- 写入 ----------

        void WriteAll(){
            if (grid.IsCurrentCellInEditMode) grid.EndEdit();
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
            write.Text = files > 0 ? "写入（" + files + " 个文件）" : "写入";
            write.Enabled = files > 0;
            status.ForeColor = Ui.Muted;
            status.Text = rows.Count == 0 ? "没有文件：点上面的「载入当前文档」或「添加文件…」"
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
