using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace TianGongCadSuite {
    // 嵌入式「检查与导出」面板（步骤③主界面）：
    // 收纳「签字页规范自检」「版本与更新日志（含自动升版与 BOM 差异草拟）」「抬头设置与导出选项」三张卡片，
    // 进入本页即自动检查、自动展示相对上一版的增/改/删差异，支持直接新增版本并按公司规范标红导出 .xlsx。
    public sealed class LineupExportPanel : Panel {
        readonly Func<LineupProject> getProject;
        readonly Action<LineupProject, string> commitMeta;
        readonly Action<string> jumpToRecord;
        readonly Action autoFixAll;
        readonly Action copyTsv11;
        readonly Action copyTsv10;
        readonly Action exportCsv;

        // 卡片 1：签字页规范自检
        readonly Card auditCard;
        readonly RoundBox auditBanner = new RoundBox();
        readonly Label auditSummary = new Label();
        readonly Button btnAutoFix;
        readonly DataGridView auditGrid = new DataGridView();
        List<LineupAuditIssue> currentIssues = new List<LineupAuditIssue>();

        // 卡片 2：版本与更新日志
        readonly Card versionCard;
        readonly Button btnSetBase;
        readonly Button btnDelVer;
        readonly Button btnEditVer;
        readonly Button btnAddVer;
        readonly DataGridView logGrid = new DataGridView();
        readonly Label logEmptyLabel = new Label();

        // 卡片 3：抬头信息与导出选项
        readonly Card exportCard;
        readonly TextBox tbProject = new TextBox { BorderStyle = BorderStyle.FixedSingle };
        readonly TextBox tbStation = new TextBox { BorderStyle = BorderStyle.FixedSingle };
        readonly TextBox tbLine = new TextBox { BorderStyle = BorderStyle.FixedSingle };
        readonly TextBox tbRevision = new TextBox { BorderStyle = BorderStyle.FixedSingle };
        readonly TextBox tbDesigner = new TextBox { BorderStyle = BorderStyle.FixedSingle };
        readonly TextBox tbEditor = new TextBox { BorderStyle = BorderStyle.FixedSingle };
        readonly CheckBox chkCad = new CheckBox { AutoSize = true, Checked = true, BackColor = Color.Transparent };
        readonly CheckBox chk11 = new CheckBox { Text = "BOM 包含「机构名称」列（模板标准 11 列；不勾为精简 10 列）", AutoSize = true, Checked = true, BackColor = Color.Transparent };
        readonly CheckBox chkHighlight = new CheckBox { Text = "在导出的 BOM 中用淡黄色标出待完善行", AutoSize = true, BackColor = Color.Transparent };
        readonly CheckBox chkMarkChanges = new CheckBox { Text = "按规范标红新增项，并对取消项红底加删除线保留", AutoSize = true, Checked = true, BackColor = Color.Transparent };
        readonly ToolTip tips = new ToolTip { AutoPopDelay = 10000, InitialDelay = 250 };

        bool loading, synced;

        protected override void Dispose(bool disposing) {
            if (disposing) tips.Dispose();
            base.Dispose(disposing);
        }

        void TryUi(Action act, string title) {
            try { act(); }
            catch (Exception ex) {
                MessageBox.Show(FindForm(), ex.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        public LineupExportPanel(
            Func<LineupProject> getProjectFn,
            Action<LineupProject, string> commitMetaFn,
            Action<string> jumpToRecordFn,
            Action autoFixAllFn,
            Action copyTsv11Fn,
            Action copyTsv10Fn,
            Action exportCsvFn) {

            getProject = getProjectFn;
            commitMeta = commitMetaFn;
            jumpToRecord = jumpToRecordFn;
            autoFixAll = autoFixAllFn;
            copyTsv11 = copyTsv11Fn;
            copyTsv10 = copyTsv10Fn;
            exportCsv = exportCsvFn;

            Dock = DockStyle.Fill;
            BackColor = Ui.Bg;
            Font = Ui.F9;

            var stack = new VerticalStack { Dock = DockStyle.Fill, Padding = new Padding(0), Gap = Ui.S(10), AutoScroll = true };

            // ---- 卡片 1：签字页规范自检 ----
            auditCard = Ui.Card("签字页规范自检", 0, "进入本页自动核查，双击条目可跳回清单修改");
            auditCard.MinimumSize = new Size(0, Ui.S(136));
            btnAutoFix = Ui.Small("一键自动补齐");
            btnAutoFix.Click += (s, e) => {
                try {
                    if (autoFixAll != null) autoFixAll();
                    RefreshFromProject();
                } catch (Exception ex) {
                    MessageBox.Show(FindForm(), ex.Message, "自动补齐", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
            auditCard.AddHeader(btnAutoFix);

            var auditBody = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
            auditBanner.Dock = DockStyle.Top;
            auditBanner.Height = Ui.S(38);
            auditBanner.Padding = Ui.SPad(14, 0, 14, 0);
            auditSummary.Dock = DockStyle.Fill;
            auditSummary.Font = Ui.F9B;
            auditSummary.BackColor = Color.Transparent;
            auditSummary.TextAlign = ContentAlignment.MiddleLeft;
            auditSummary.AutoEllipsis = true;
            auditBanner.Controls.Add(auditSummary);

            var spacer = new Panel { Dock = DockStyle.Top, Height = Ui.S(6), BackColor = Color.White };

            ConfigureReadOnlyGrid(auditGrid);
            auditGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "级别", HeaderText = "级别", Width = 68, AutoSizeMode = DataGridViewAutoSizeColumnMode.None });
            auditGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "类目", HeaderText = "类目", Width = 112, AutoSizeMode = DataGridViewAutoSizeColumnMode.None });
            auditGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "编号", HeaderText = "编号", Width = 158, AutoSizeMode = DataGridViewAutoSizeColumnMode.None });
            auditGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "说明", HeaderText = "问题与建议（双击直接定位修改）", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
            Ui.FitGrid(auditGrid);
            auditGrid.CellDoubleClick += (s, e) => {
                if (e.RowIndex >= 0 && e.RowIndex < currentIssues.Count) {
                    string id = currentIssues[e.RowIndex].RecordId;
                    if (!string.IsNullOrEmpty(id) && jumpToRecord != null) jumpToRecord(id);
                }
            };

            auditBody.Controls.Add(auditGrid);
            auditBody.Controls.Add(spacer);
            auditBody.Controls.Add(auditBanner);
            auditCard.Controls.Add(auditBody);

            // ---- 卡片 2：版本与更新日志（自动升版 + BOM 差异草拟 + 历史保留） ----
            versionCard = Ui.Card("版本与更新日志", 184, "支持累加历次版本号与更新内容，导出自动写入「LineUP更新日志」");
            btnSetBase = Ui.Small("重设基准");
            ((FlatButton)btnSetBase).SetFore(Ui.Text2);
            btnSetBase.Click += (s, e) => TryUi(ResetBaselineToCurrent, "重设基准");

            btnDelVer = Ui.Small("删除");
            ((FlatButton)btnDelVer).SetFore(Ui.Bad);
            btnDelVer.Click += (s, e) => TryUi(DeleteSelectedVersionLog, "删除版本记录");

            btnEditVer = Ui.Small("编辑");
            ((FlatButton)btnEditVer).SetFore(Ui.Text2);
            btnEditVer.Click += (s, e) => TryUi(EditSelectedVersionLog, "编辑版本记录");

            btnAddVer = Ui.Primary("+ 新增版本", 96, 26);
            btnAddVer.Font = Ui.F8;
            btnAddVer.Click += (s, e) => TryUi(AddNewVersionLog, "新增版本记录");

            // AddHeader 顺序从左到右排列，最右侧放主动作「+ 新增版本」
            versionCard.AddHeader(btnSetBase);
            versionCard.AddHeader(btnDelVer);
            versionCard.AddHeader(btnEditVer);
            versionCard.AddHeader(btnAddVer);

            var verBody = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
            ConfigureReadOnlyGrid(logGrid);
            logGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "序号", HeaderText = "#", Width = 38, AutoSizeMode = DataGridViewAutoSizeColumnMode.None });
            logGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "版本号", HeaderText = "版本号", Width = 74, AutoSizeMode = DataGridViewAutoSizeColumnMode.None });
            logGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "日期", HeaderText = "日期", Width = 92, AutoSizeMode = DataGridViewAutoSizeColumnMode.None });
            logGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "更新内容", HeaderText = "更新内容（双击编辑）", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
            logGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "更新人", HeaderText = "更新人", Width = 78, AutoSizeMode = DataGridViewAutoSizeColumnMode.None });
            logGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "备注", HeaderText = "备注", Width = 112, AutoSizeMode = DataGridViewAutoSizeColumnMode.None });
            Ui.FitGrid(logGrid);
            logGrid.SelectionChanged += (s, e) => UpdateVersionButtons();
            logGrid.CellDoubleClick += (s, e) => {
                if (e.RowIndex >= 0) TryUi(EditSelectedVersionLog, "编辑版本记录");
            };

            logEmptyLabel.Dock = DockStyle.Fill;
            logEmptyLabel.TextAlign = ContentAlignment.MiddleCenter;
            logEmptyLabel.ForeColor = Ui.Muted;
            logEmptyLabel.Font = Ui.F8;
            logEmptyLabel.BackColor = Color.White;
            logEmptyLabel.Text = "暂无版本更新记录。点击右上角「+ 新增版本」填写版本号与变更说明，或导入旧版 Lineup Excel 自动带入历史日志。";

            verBody.Controls.Add(logGrid);
            verBody.Controls.Add(logEmptyLabel);
            versionCard.Controls.Add(verBody);

            // ---- 卡片 3：抬头信息与导出选项 ----
            exportCard = Ui.Card("导出标准 5 页签 Excel (.xlsx)", 214, "抬头信息自动保存到本项目");
            var exBody = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };

            var metaGrid = new TableLayoutPanel {
                Dock = DockStyle.Top, Height = Ui.S(66), ColumnCount = 6, RowCount = 2,
                BackColor = Color.White, Margin = new Padding(0)
            };
            int lblW = Ui.S(72);
            for (int c = 0; c < 3; c++) {
                metaGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, lblW));
                metaGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
            }
            metaGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            metaGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

            Action<int, int, string, TextBox> addMeta = (r, c, label, box) => {
                box.Dock = DockStyle.Fill;
                box.Margin = Ui.SPad(0, 3, 16, 3);
                box.Leave += (s, e) => TryUi(CommitMetadataIfChanged, "保存抬头设置");
                metaGrid.Controls.Add(new Label {
                    Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
                    ForeColor = Ui.Text2, Font = Ui.F9B, BackColor = Color.White
                }, c * 2, r);
                metaGrid.Controls.Add(box, c * 2 + 1, r);
            };
            addMeta(0, 0, "项目编号", tbProject);
            addMeta(0, 1, "工位号", tbStation);
            addMeta(0, 2, "线体号", tbLine);
            addMeta(1, 0, "版本号", tbRevision);
            addMeta(1, 1, "机械设计", tbDesigner);
            addMeta(1, 2, "编辑人", tbEditor);

            var optGrid = new TableLayoutPanel {
                Dock = DockStyle.Top, Height = Ui.S(54), ColumnCount = 2, RowCount = 2,
                BackColor = Color.White, Padding = Ui.SPad(0, 4, 0, 0)
            };
            optGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48));
            optGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52));
            optGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            optGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            chkCad.Margin = Ui.SPad(0, 2, 8, 0);
            chk11.Margin = Ui.SPad(0, 2, 0, 0);
            chkHighlight.Margin = Ui.SPad(0, 2, 8, 0);
            chkMarkChanges.Margin = Ui.SPad(0, 2, 0, 0);
            chk11.CheckedChanged += (s, e) => TryUi(CommitMetadataIfChanged, "保存导出选项");
            chkMarkChanges.CheckedChanged += (s, e) => TryUi(CommitMetadataIfChanged, "保存导出选项");
            optGrid.Controls.Add(chkCad, 0, 0);
            optGrid.Controls.Add(chk11, 1, 0);
            optGrid.Controls.Add(chkHighlight, 0, 1);
            optGrid.Controls.Add(chkMarkChanges, 1, 1);

            var altRow = new FlowLayoutPanel {
                Dock = DockStyle.Bottom, Height = Ui.S(30), WrapContents = false, BackColor = Color.White
            };
            altRow.Controls.Add(new Label { Text = "其他格式：", AutoSize = true, ForeColor = Ui.Muted, Font = Ui.F8, Margin = Ui.SPad(0, 5, 4, 0) });
            Action<string, Action> addAlt = (t, act) => {
                var b = Ui.Small(t);
                ((FlatButton)b).SetFore(Ui.Text2);
                b.Margin = Ui.SPad(0, 0, 6, 0);
                b.Click += (s, e) => { if (act != null) act(); };
                altRow.Controls.Add(b);
            };
            addAlt("复制到 WPS / Excel (11列)", copyTsv11);
            addAlt("复制参考 BOM (10列)", copyTsv10);
            addAlt("导出 CSV", exportCsv);

            exBody.Controls.Add(altRow);
            exBody.Controls.Add(optGrid);
            exBody.Controls.Add(metaGrid);
            exportCard.Controls.Add(exBody);

            stack.Add(auditCard, true);
            stack.Add(versionCard, false);
            stack.Add(exportCard, false);
            Controls.Add(stack);
        }

        static void ConfigureReadOnlyGrid(DataGridView g) {
            try {
                var pi = typeof(DataGridView).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (pi != null) pi.SetValue(g, true, null);
            } catch { }
            g.Dock = DockStyle.Fill;
            g.ReadOnly = true;
            g.AllowUserToAddRows = false;
            g.AllowUserToDeleteRows = false;
            g.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            g.MultiSelect = false;
            g.RowHeadersVisible = false;
            g.BackgroundColor = Color.White;
            g.BorderStyle = BorderStyle.None;
            g.GridColor = Color.FromArgb(0xF0, 0xF0, 0xF3);
            g.EnableHeadersVisualStyles = false;
            g.ColumnHeadersDefaultCellStyle.BackColor = Ui.Rail;
            g.ColumnHeadersDefaultCellStyle.ForeColor = Ui.Muted;
            g.ColumnHeadersDefaultCellStyle.Font = Ui.F8;
            g.ColumnHeadersDefaultCellStyle.SelectionBackColor = Ui.Rail;
            g.DefaultCellStyle.SelectionBackColor = Ui.AccentSoft;
            g.DefaultCellStyle.SelectionForeColor = Ui.Text;
            g.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        }

        public LineupXlsxOptions CurrentExportOptions() {
            if (!synced) {
                var p = getProject != null ? getProject() : null;
                return new LineupXlsxOptions {
                    Export11Columns = p == null || p.Export11Columns,
                    IncludeCadPages = true,
                    HighlightAudit = false,
                    MarkBomChanges = p == null || p.MarkBomChanges
                };
            }
            CommitMetadataIfChanged();
            return new LineupXlsxOptions {
                Export11Columns = chk11.Checked,
                IncludeCadPages = chkCad.Checked,
                HighlightAudit = chkHighlight.Checked,
                MarkBomChanges = chkMarkChanges.Checked
            };
        }

        public LineupAuditResult RefreshFromProject() {
            var p = getProject != null ? getProject() : null;
            if (p == null) return new LineupAuditResult();

            loading = true;
            tbProject.Text = p.ProjectCode ?? "";
            tbStation.Text = !string.IsNullOrWhiteSpace(p.StationCode) ? p.StationCode : (p.NumberPrefix ?? "");
            tbLine.Text = string.IsNullOrWhiteSpace(p.LineCode) ? "L1" : p.LineCode;
            tbRevision.Text = LineupRevision.CurrentVersion(p);
            // 已有版本更新日志时，LineupRevision.CurrentVersion 永远取日志末条版本；此时将抬头版本号框设为只读，避免用户手改后导出被静默覆回旧值
            bool hasLogs = p.ChangeLogs != null && p.ChangeLogs.Count > 0;
            tbRevision.ReadOnly = hasLogs;
            tbRevision.BackColor = hasLogs ? Ui.Rail : Color.White;
            tips.SetToolTip(tbRevision, hasLogs
                ? "当前版本号由上方「版本与更新日志」最新一条记录决定；升版请点「+ 新增版本」，改当前号请双击末条日志编辑。"
                : "首版初始版本号（例如 V1.0）。");
            tbDesigner.Text = p.Designer ?? "";
            tbEditor.Text = p.Editor ?? "";
            chk11.Checked = p.Export11Columns;
            chkMarkChanges.Checked = p.MarkBomChanges;
            int pages = p.CadPages == null ? 0 : p.CadPages.Count;
            chkCad.Text = pages > 0
                ? "包含 CAD 位置图工作表（已建 " + pages + " 页）"
                : "包含 CAD 位置图空白模板页（当前尚未新建图页）";
            loading = false;
            synced = true;

            RefreshVersionCard(p);

            var res = LineupAudit.Run(p);
            currentIssues = res.Issues;
            auditGrid.SuspendLayout();
            try {
                auditGrid.Rows.Clear();
                if (currentIssues.Count > 0) {
                    var batch = new DataGridViewRow[currentIssues.Count];
                    for (int i = 0; i < currentIssues.Count; i++) {
                        var iss = currentIssues[i];
                        string sev = iss.Severity == LineupAuditSeverity.Error ? "错误" : iss.Severity == LineupAuditSeverity.Warning ? "提醒" : "建议";
                        var row = new DataGridViewRow();
                        row.CreateCells(auditGrid, sev, iss.Category, iss.Number, iss.Message);
                        if (iss.Severity == LineupAuditSeverity.Error) row.DefaultCellStyle.BackColor = Color.FromArgb(0xFE, 0xF2, 0xF2);
                        else if (iss.Severity == LineupAuditSeverity.Warning) row.DefaultCellStyle.BackColor = Color.FromArgb(0xFF, 0xFB, 0xEB);
                        batch[i] = row;
                    }
                    auditGrid.Rows.AddRange(batch);
                }
            } finally {
                auditGrid.ResumeLayout();
            }

            bool hasAutoFixable = currentIssues.Any(i => i.RuleCode == "CYL_NO_VALVE" || i.RuleCode == "CYL_NO_SENSOR" || i.RuleCode == "CSEN_NOTE_DIFF");
            btnAutoFix.Visible = hasAutoFixable;

            if (p.Records.Count == 0) {
                auditBanner.Fill = Ui.Rail;
                auditSummary.ForeColor = Ui.Muted;
                auditSummary.Text = "清单里还没有器件记录，请先回到「① 整理清单」添加器件。";
                auditGrid.Visible = false;
            } else if (currentIssues.Count == 0) {
                auditBanner.Fill = Ui.OkSoft;
                auditSummary.ForeColor = Ui.Ok;
                auditSummary.Text = "✓ 全部检查通过！编号、阀片、传感器配对与必填项均符合签字页规范，可以直接导出。";
                auditGrid.Visible = false;
            } else {
                auditBanner.Fill = res.ErrorCount > 0 ? Ui.BadSoft : Ui.WarnSoft;
                auditSummary.ForeColor = res.ErrorCount > 0 ? Ui.Bad : Ui.Warn;
                auditSummary.Text = "发现 " + currentIssues.Count + " 项待确认（错误 " + res.ErrorCount + " · 提醒 " + res.WarningCount + " · 建议 " + res.InfoCount + "），双击条目可立即定位修改";
                auditGrid.Visible = true;
            }
            auditBanner.Invalidate();
            return res;
        }

        void RefreshVersionCard(LineupProject p) {
            var diff = LineupRevision.CompareToBaseline(p);
            if (!diff.HasBaseline) {
                versionCard.Hint = "尚未建立对比基准（导入旧表或首次导出后自动记录）";
                chkMarkChanges.Text = "按规范标红新增项，并对取消项红底加删除线保留";
            } else if (!diff.HasAnyChange) {
                versionCard.Hint = "对比基准 " + (diff.BaselineVersion.Length > 0 ? diff.BaselineVersion : "上一版") + "：当前 BOM 无增删改变更";
                chkMarkChanges.Text = "按规范标红新增/取消项（相对 " + (diff.BaselineVersion.Length > 0 ? diff.BaselineVersion : "上一版") + " 无变更）";
            } else {
                string bVer = diff.BaselineVersion.Length > 0 ? diff.BaselineVersion : "上一版";
                versionCard.Hint = "相对基准 " + bVer + "：新增 " + diff.Added.Count + " · 变更 " + diff.Modified.Count + " · 取消 " + diff.Removed.Count;
                chkMarkChanges.Text = "按规范标红变更（新增 " + diff.Added.Count + " 红底 / 取消 " + diff.Removed.Count + " 红底+删除线）";
            }
            versionCard.Invalidate();

            var logs = p.ChangeLogs ?? new List<LineupChangeLog>();
            logGrid.SuspendLayout();
            try {
                logGrid.Rows.Clear();
                if (logs.Count > 0) {
                    var rows = new DataGridViewRow[logs.Count];
                    for (int i = 0; i < logs.Count; i++) {
                        var lg = logs[i];
                        string preview = (lg.Content ?? "").Replace("\r\n", "\n").Replace("\n", " / ").Trim();
                        var r = new DataGridViewRow();
                        r.CreateCells(logGrid, (i + 1).ToString(CultureInfo.InvariantCulture), lg.Version ?? "", lg.Date ?? "", preview, lg.Editor ?? "", lg.Notes ?? "");
                        r.Cells[3].ToolTipText = lg.Content ?? "";
                        rows[i] = r;
                    }
                    logGrid.Rows.AddRange(rows);
                    logGrid.ClearSelection();
                    logGrid.Rows[logs.Count - 1].Selected = true;
                    try { logGrid.FirstDisplayedScrollingRowIndex = Math.Max(0, logs.Count - 4); } catch { }
                }
            } finally {
                logGrid.ResumeLayout();
            }
            logGrid.Visible = logs.Count > 0;
            logEmptyLabel.Visible = logs.Count == 0;
            btnSetBase.Visible = p.Records != null && p.Records.Count > 0;
            UpdateVersionButtons();
        }

        void UpdateVersionButtons() {
            bool hasSel = logGrid.Visible && logGrid.SelectedRows.Count > 0;
            btnEditVer.Enabled = hasSel;
            btnDelVer.Enabled = hasSel;
        }

        void AddNewVersionLog() {
            CommitMetadataIfChanged();
            var p = getProject != null ? getProject() : null;
            if (p == null) return;

            bool canPromote = LineupRevision.ShouldPromoteExportedToBaseline(p);
            string suggestedVer = (p.ChangeLogs == null || p.ChangeLogs.Count == 0)
                ? (string.IsNullOrWhiteSpace(p.Revision) ? "V1.0" : p.Revision.Trim())
                : LineupRevision.NextVersion(LineupRevision.CurrentVersion(p));

            string today = DateTime.Today.ToString("yyyy/M/d", CultureInfo.InvariantCulture);
            string modelDate = today;
            try {
                if (!string.IsNullOrEmpty(p.AssemblyFile) && File.Exists(p.AssemblyFile)) {
                    modelDate = File.GetLastWriteTime(p.AssemblyFile).ToString("yyyy/M/d", CultureInfo.InvariantCulture);
                }
            } catch { }

            var initial = new LineupChangeLog {
                Version = suggestedVer,
                Date = today,
                ModelDate = modelDate,
                Designer = p.Designer ?? "",
                Editor = p.Editor ?? "",
                Notes = ""
            };

            Func<bool, LineupBomDiff> diffForPromote = promote => {
                if (!promote || !canPromote) return LineupRevision.CompareToBaseline(p);
                var tmp = p.Copy();
                tmp.Baseline = p.LatestExported.Copy();
                return LineupRevision.CompareToBaseline(tmp);
            };

            using (var dlg = new LineupChangeLogDialog(
                "新增版本记录",
                initial,
                canPromote ? (p.LatestExported.Version ?? "上一导出版") : "",
                diffForPromote)) {
                if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
                var next = p.Copy();
                if (next.ChangeLogs == null) next.ChangeLogs = new List<LineupChangeLog>();
                if (canPromote && dlg.PromoteExportedBaseline) {
                    next.Baseline = next.LatestExported.Copy();
                }
                next.ChangeLogs.Add(dlg.ResultEntry);
                if (!string.IsNullOrWhiteSpace(dlg.ResultEntry.Version)) next.Revision = dlg.ResultEntry.Version.Trim();
                if (!string.IsNullOrWhiteSpace(dlg.ResultEntry.Designer)) next.Designer = dlg.ResultEntry.Designer.Trim();
                if (!string.IsNullOrWhiteSpace(dlg.ResultEntry.Editor)) next.Editor = dlg.ResultEntry.Editor.Trim();
                if (commitMeta != null) commitMeta(next, "已新增版本记录 " + next.Revision);
                RefreshFromProject();
            }
        }

        void EditSelectedVersionLog() {
            if (!logGrid.Visible || logGrid.SelectedRows.Count == 0) return;
            int idx = logGrid.SelectedRows[0].Index;
            var p = getProject != null ? getProject() : null;
            if (p == null || p.ChangeLogs == null || idx < 0 || idx >= p.ChangeLogs.Count) return;

            var existing = p.ChangeLogs[idx].Copy();
            using (var dlg = new LineupChangeLogDialog(
                "编辑版本记录 #" + (idx + 1),
                existing,
                "",
                _ => LineupRevision.CompareToBaseline(p))) {
                if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
                var next = p.Copy();
                next.ChangeLogs[idx] = dlg.ResultEntry;
                if (idx == next.ChangeLogs.Count - 1 && !string.IsNullOrWhiteSpace(dlg.ResultEntry.Version)) {
                    next.Revision = dlg.ResultEntry.Version.Trim();
                }
                if (commitMeta != null) commitMeta(next, "已更新版本记录 " + dlg.ResultEntry.Version);
                RefreshFromProject();
            }
        }

        void DeleteSelectedVersionLog() {
            if (!logGrid.Visible || logGrid.SelectedRows.Count == 0) return;
            int idx = logGrid.SelectedRows[0].Index;
            var p = getProject != null ? getProject() : null;
            if (p == null || p.ChangeLogs == null || idx < 0 || idx >= p.ChangeLogs.Count) return;

            string ver = p.ChangeLogs[idx].Version;
            if (MessageBox.Show(FindForm(), "确定删除第 " + (idx + 1) + " 条版本记录（" + (string.IsNullOrWhiteSpace(ver) ? "未命名版本" : ver) + "）吗？", "删除版本记录", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) {
                return;
            }
            var next = p.Copy();
            next.ChangeLogs.RemoveAt(idx);
            if (next.ChangeLogs.Count > 0) {
                string latest = LineupRevision.CurrentVersion(next);
                if (!string.IsNullOrWhiteSpace(latest)) next.Revision = latest;
            }
            if (commitMeta != null) commitMeta(next, "已删除版本记录 " + ver);
            RefreshFromProject();
        }

        void ResetBaselineToCurrent() {
            CommitMetadataIfChanged();
            var p = getProject != null ? getProject() : null;
            if (p == null || p.Records == null || p.Records.Count == 0) return;
            string ver = LineupRevision.CurrentVersion(p);
            if (MessageBox.Show(FindForm(),
                "将当前清单（共 " + p.Records.Count + " 条，版本 " + ver + "）设为新的对比基准吗？\n\n设为基准后，当前已有的红底新增/删除线取消标记会清零，之后的新修改将以当前清单为基准进行对比与标红。",
                "重设对比基准", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) {
                return;
            }
            var next = p.Copy();
            next.Baseline = LineupRevision.CaptureSnapshot(next, ver);
            next.LatestExported = next.Baseline.Copy();
            if (commitMeta != null) commitMeta(next, "已将 " + ver + " 设为新对比基准");
            RefreshFromProject();
        }

        public void CommitMetadataIfChanged() {
            if (loading || !synced || getProject == null || commitMeta == null) return;
            var p = getProject();
            if (p == null) return;
            string proj = tbProject.Text.Trim();
            string sta = tbStation.Text.Trim();
            string line = string.IsNullOrWhiteSpace(tbLine.Text) ? "L1" : tbLine.Text.Trim();
            bool hasLogs = p.ChangeLogs != null && p.ChangeLogs.Count > 0;
            string rev = hasLogs
                ? LineupRevision.CurrentVersion(p)
                : (string.IsNullOrWhiteSpace(tbRevision.Text) ? "V1.0" : tbRevision.Text.Trim());
            string des = tbDesigner.Text.Trim();
            string ed = tbEditor.Text.Trim();
            bool exp11 = chk11.Checked;
            bool markChg = chkMarkChanges.Checked;

            bool changed = (p.ProjectCode ?? "") != proj
                || (p.StationCode ?? "") != sta
                || (p.LineCode ?? "L1") != line
                || (p.Revision ?? "V1.0") != rev
                || (p.Designer ?? "") != des
                || (p.Editor ?? "") != ed
                || p.Export11Columns != exp11
                || p.MarkBomChanges != markChg;
            if (!changed) return;

            var next = p.Copy();
            next.ProjectCode = proj;
            next.StationCode = sta;
            if (string.IsNullOrWhiteSpace(next.NumberPrefix) && sta.Length > 0) next.NumberPrefix = sta;
            next.LineCode = line;
            next.Revision = rev;
            next.Designer = des;
            next.Editor = ed;
            next.Export11Columns = exp11;
            next.MarkBomChanges = markChg;
            commitMeta(next, "已保存导出抬头设置");
        }

        // 简洁优雅的「新增 / 编辑版本记录」弹窗（支持一键按 BOM 差异草拟多行更新内容）
        sealed class LineupChangeLogDialog : Form {
            readonly TextBox tbVersion = new TextBox { BorderStyle = BorderStyle.FixedSingle };
            readonly TextBox tbDate = new TextBox { BorderStyle = BorderStyle.FixedSingle };
            readonly TextBox tbModelDate = new TextBox { BorderStyle = BorderStyle.FixedSingle };
            readonly TextBox tbDesigner = new TextBox { BorderStyle = BorderStyle.FixedSingle };
            readonly TextBox tbEditor = new TextBox { BorderStyle = BorderStyle.FixedSingle };
            readonly TextBox tbApprover = new TextBox { BorderStyle = BorderStyle.FixedSingle };
            readonly TextBox tbContent = new TextBox { BorderStyle = BorderStyle.FixedSingle, Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true };
            readonly TextBox tbNotes = new TextBox { BorderStyle = BorderStyle.FixedSingle };
            readonly CheckBox chkPromote = new CheckBox { AutoSize = true, BackColor = Color.Transparent };
            readonly Label diffHint = new Label();
            readonly Func<bool, LineupBomDiff> getDiff;

            public LineupChangeLog ResultEntry { get; private set; }
            public bool PromoteExportedBaseline { get { return chkPromote.Visible && chkPromote.Checked; } }

            public LineupChangeLogDialog(string title, LineupChangeLog initial, string promoteVersionLabel, Func<bool, LineupBomDiff> diffFn) {
                getDiff = diffFn;
                Ui.Shell(this, title, 640, 470, 560, 420);
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MaximizeBox = false;
                MinimizeBox = false;

                var root = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Bg, Padding = Ui.SPad(16, 14, 16, 12) };

                var meta = new TableLayoutPanel {
                    Dock = DockStyle.Top, Height = Ui.S(68), ColumnCount = 6, RowCount = 2,
                    BackColor = Ui.Bg, Margin = new Padding(0)
                };
                int lw = Ui.S(76);
                for (int c = 0; c < 3; c++) {
                    meta.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, lw));
                    meta.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
                }
                meta.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
                meta.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

                Action<int, int, string, TextBox> addCell = (r, c, lbl, box) => {
                    box.Dock = DockStyle.Fill;
                    box.Margin = Ui.SPad(0, 3, c == 2 ? 0 : 12, 3);
                    meta.Controls.Add(new Label {
                        Text = lbl, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
                        ForeColor = Ui.Text2, Font = Ui.F9B, BackColor = Ui.Bg
                    }, c * 2, r);
                    meta.Controls.Add(box, c * 2 + 1, r);
                };
                addCell(0, 0, "版本号", tbVersion);
                addCell(0, 1, "更新日期", tbDate);
                addCell(0, 2, "数模日期", tbModelDate);
                addCell(1, 0, "设计人", tbDesigner);
                addCell(1, 1, "更新人", tbEditor);
                addCell(1, 2, "确认人", tbApprover);

                tbVersion.Text = initial.Version ?? "";
                tbDate.Text = initial.Date ?? "";
                tbModelDate.Text = initial.ModelDate ?? "";
                tbDesigner.Text = initial.Designer ?? "";
                tbEditor.Text = initial.Editor ?? "";
                tbApprover.Text = initial.Approver ?? "";
                tbContent.Text = initial.Content ?? "";
                tbNotes.Text = initial.Notes ?? "";

                var contentBar = new Panel { Dock = DockStyle.Top, Height = Ui.S(32), BackColor = Ui.Bg, Padding = Ui.SPad(0, 6, 0, 2) };
                var contentLbl = new Label {
                    Text = "更新内容（支持多行，导出时自动撑高行高）",
                    Dock = DockStyle.Left, AutoSize = true, TextAlign = ContentAlignment.MiddleLeft,
                    ForeColor = Ui.Text2, Font = Ui.F9B, BackColor = Ui.Bg
                };
                var btnDraft = Ui.Small("↻ 从当前 BOM 差异草拟");
                btnDraft.Dock = DockStyle.Right;
                btnDraft.Click += (s, e) => FillDraftFromDiff(true);
                contentBar.Controls.Add(contentLbl);
                contentBar.Controls.Add(btnDraft);

                var contentHost = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Bg, Padding = Ui.SPad(0, 2, 0, 8) };
                tbContent.Dock = DockStyle.Fill;
                contentHost.Controls.Add(tbContent);

                var noteRow = new Panel { Dock = DockStyle.Bottom, Height = Ui.S(34), BackColor = Ui.Bg, Padding = Ui.SPad(0, 2, 0, 4) };
                var noteLbl = new Label {
                    Text = "备注", Dock = DockStyle.Left, Width = lw, TextAlign = ContentAlignment.MiddleLeft,
                    ForeColor = Ui.Text2, Font = Ui.F9B, BackColor = Ui.Bg
                };
                tbNotes.Dock = DockStyle.Fill;
                noteRow.Controls.Add(tbNotes);
                noteRow.Controls.Add(noteLbl);

                var promoRow = new Panel { Dock = DockStyle.Bottom, Height = Ui.S(26), BackColor = Ui.Bg, Visible = !string.IsNullOrWhiteSpace(promoteVersionLabel) };
                chkPromote.Text = "将上一导出版（" + promoteVersionLabel + "）设为本次新对比基准";
                chkPromote.Checked = !string.IsNullOrWhiteSpace(promoteVersionLabel);
                chkPromote.Dock = DockStyle.Left;
                chkPromote.CheckedChanged += (s, e) => RefreshDiffHint();
                promoRow.Controls.Add(chkPromote);

                diffHint.Dock = DockStyle.Bottom;
                diffHint.Height = Ui.S(24);
                diffHint.ForeColor = Ui.Muted;
                diffHint.Font = Ui.F8;
                diffHint.TextAlign = ContentAlignment.MiddleLeft;
                diffHint.BackColor = Ui.Bg;

                var btnBar = new Panel { Dock = DockStyle.Bottom, Height = Ui.S(42), BackColor = Ui.Bg, Padding = Ui.SPad(0, 8, 0, 0) };
                var btnOk = Ui.Primary("保存版本记录", 116, 32);
                btnOk.Dock = DockStyle.Right;
                btnOk.Click += (s, e) => {
                    if (string.IsNullOrWhiteSpace(tbVersion.Text)) {
                        MessageBox.Show(this, "请填写版本号（例如 V1.0、V3.4）。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        tbVersion.Focus();
                        return;
                    }
                    ResultEntry = new LineupChangeLog {
                        Version = tbVersion.Text.Trim(),
                        Date = tbDate.Text.Trim(),
                        ModelDate = tbModelDate.Text.Trim(),
                        Designer = tbDesigner.Text.Trim(),
                        Editor = tbEditor.Text.Trim(),
                        Approver = tbApprover.Text.Trim(),
                        Content = tbContent.Text.Trim(),
                        Notes = tbNotes.Text.Trim()
                    };
                    DialogResult = DialogResult.OK;
                    Close();
                };
                var btnCancel = Ui.Secondary("取消", 76, 32);
                btnCancel.Dock = DockStyle.Right;
                var gap = new Panel { Dock = DockStyle.Right, Width = Ui.S(8), BackColor = Ui.Bg };
                btnCancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
                btnBar.Controls.Add(btnCancel);
                btnBar.Controls.Add(gap);
                btnBar.Controls.Add(btnOk);

                root.Controls.Add(contentHost);
                root.Controls.Add(contentBar);
                root.Controls.Add(meta);
                root.Controls.Add(noteRow);
                root.Controls.Add(promoRow);
                root.Controls.Add(diffHint);
                root.Controls.Add(btnBar);
                Controls.Add(root);

                CancelButton = btnCancel;
                RefreshDiffHint();
                // 新增版本且尚未填更新内容时，自动根据相对基准的 BOM 差异预填草稿
                if (string.IsNullOrWhiteSpace(tbContent.Text)) FillDraftFromDiff(false);
                Ui.StyleTree(this);
            }

            void RefreshDiffHint() {
                var diff = getDiff != null ? getDiff(PromoteExportedBaseline) : null;
                if (diff == null || !diff.HasBaseline) {
                    diffHint.Text = "提示：尚未建立历史基准，导出本版后将自动记录为后续版本的对比基准。";
                    diffHint.ForeColor = Ui.Muted;
                } else if (!diff.HasAnyChange) {
                    diffHint.Text = "相对基准 " + (diff.BaselineVersion.Length > 0 ? diff.BaselineVersion : "上一版") + "：当前 BOM 器件无增删改变更。";
                    diffHint.ForeColor = Ui.Muted;
                } else {
                    diffHint.Text = "相对基准 " + (diff.BaselineVersion.Length > 0 ? diff.BaselineVersion : "上一版")
                        + "：新增 " + diff.Added.Count + " 项 · 变更 " + diff.Modified.Count + " 项 · 取消 " + diff.Removed.Count + " 项（导出将自动标红/加删除线）";
                    diffHint.ForeColor = Ui.Accent;
                }
            }

            void FillDraftFromDiff(bool notifyIfNone) {
                var diff = getDiff != null ? getDiff(PromoteExportedBaseline) : null;
                if (diff == null || !diff.HasBaseline) {
                    if (notifyIfNone) MessageBox.Show(this, "当前还没有上一版对比基准（请先导入旧版 Lineup 或完成首次导出）。", "差异草拟", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                string draft = LineupRevision.DraftChangeSummary(diff);
                if (!diff.HasAnyChange && !notifyIfNone) return;
                tbContent.Text = draft;
            }
        }
    }
}
