using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace TianGongCadSuite {
    // 嵌入式「检查与导出」面板（步骤③主界面）：
    // 把旧版「规范自检弹窗 + 抬头设置弹窗 + 导出参数弹窗」三合一收纳到同一页两张清爽卡片里，
    // 消除连环弹窗，进入本页即自动检查、一键自动补齐缺失项，点右下角主按钮直接保存导出 .xlsx。
    public sealed class LineupExportPanel : Panel {
        readonly Func<LineupProject> getProject;
        readonly Action<LineupProject, string> commitMeta;
        readonly Action<string> jumpToRecord;
        readonly Action autoFixAll;
        readonly Action copyTsv11;
        readonly Action copyTsv10;
        readonly Action exportCsv;

        readonly Card auditCard;
        readonly RoundBox auditBanner = new RoundBox();
        readonly Label auditSummary = new Label();
        readonly Button btnAutoFix;
        readonly DataGridView auditGrid = new DataGridView();
        List<LineupAuditIssue> currentIssues = new List<LineupAuditIssue>();

        readonly Card exportCard;
        readonly TextBox tbProject = new TextBox { BorderStyle = BorderStyle.FixedSingle };
        readonly TextBox tbStation = new TextBox { BorderStyle = BorderStyle.FixedSingle };
        readonly TextBox tbLine = new TextBox { BorderStyle = BorderStyle.FixedSingle };
        readonly TextBox tbRevision = new TextBox { BorderStyle = BorderStyle.FixedSingle };
        readonly TextBox tbDesigner = new TextBox { BorderStyle = BorderStyle.FixedSingle };
        readonly TextBox tbEditor = new TextBox { BorderStyle = BorderStyle.FixedSingle };
        readonly CheckBox chkCad = new CheckBox { AutoSize = true, Checked = true, BackColor = Color.Transparent };
        readonly CheckBox chk11 = new CheckBox { Text = "BOM 包含「机构名称」列（11 列；不勾为公司标准 10 列）", AutoSize = true, BackColor = Color.Transparent };
        readonly CheckBox chkHighlight = new CheckBox { Text = "在导出的 BOM 中用淡黄色标出待完善行", AutoSize = true, BackColor = Color.Transparent };

        bool loading, synced;

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

            var stack = new VerticalStack { Dock = DockStyle.Fill, Padding = new Padding(0), Gap = Ui.S(12), AutoScroll = true };

            // ---- 卡片 1：签字页规范自检 ----
            auditCard = Ui.Card("签字页规范自检", 0, "进入本页自动核查，双击条目可跳回清单修改");
            auditCard.MinimumSize = new Size(0, Ui.S(165));
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
            auditBanner.Height = Ui.S(42);
            auditBanner.Padding = Ui.SPad(14, 0, 14, 0);
            auditSummary.Dock = DockStyle.Fill;
            auditSummary.Font = Ui.F9B;
            auditSummary.BackColor = Color.Transparent;
            auditSummary.TextAlign = ContentAlignment.MiddleLeft;
            auditSummary.AutoEllipsis = true;
            auditBanner.Controls.Add(auditSummary);

            var spacer = new Panel { Dock = DockStyle.Top, Height = Ui.S(8), BackColor = Color.White };

            try {
                var pi = typeof(DataGridView).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (pi != null) pi.SetValue(auditGrid, true, null);
            } catch { }
            auditGrid.Dock = DockStyle.Fill;
            auditGrid.ReadOnly = true;
            auditGrid.AllowUserToAddRows = false;
            auditGrid.AllowUserToDeleteRows = false;
            auditGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            auditGrid.MultiSelect = false;
            auditGrid.RowHeadersVisible = false;
            auditGrid.BackgroundColor = Color.White;
            auditGrid.BorderStyle = BorderStyle.None;
            auditGrid.GridColor = Color.FromArgb(0xF0, 0xF0, 0xF3);
            auditGrid.EnableHeadersVisualStyles = false;
            auditGrid.ColumnHeadersDefaultCellStyle.BackColor = Ui.Rail;
            auditGrid.ColumnHeadersDefaultCellStyle.ForeColor = Ui.Muted;
            auditGrid.ColumnHeadersDefaultCellStyle.Font = Ui.F8;
            auditGrid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Ui.Rail;
            auditGrid.DefaultCellStyle.SelectionBackColor = Ui.AccentSoft;
            auditGrid.DefaultCellStyle.SelectionForeColor = Ui.Text;
            auditGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
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

            // ---- 卡片 2：抬头信息与导出选项 ----
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
                box.Leave += (s, e) => CommitMetadataIfChanged();
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
            chk11.CheckedChanged += (s, e) => CommitMetadataIfChanged();
            optGrid.Controls.Add(chkCad, 0, 0);
            optGrid.Controls.Add(chk11, 1, 0);
            optGrid.Controls.Add(chkHighlight, 0, 1);

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
            stack.Add(exportCard, false);
            Controls.Add(stack);
        }

        public LineupXlsxOptions CurrentExportOptions() {
            if (!synced) {
                var p = getProject != null ? getProject() : null;
                return new LineupXlsxOptions {
                    Export11Columns = p != null && p.Export11Columns,
                    IncludeCadPages = true,
                    HighlightAudit = false
                };
            }
            CommitMetadataIfChanged();
            return new LineupXlsxOptions {
                Export11Columns = chk11.Checked,
                IncludeCadPages = chkCad.Checked,
                HighlightAudit = chkHighlight.Checked
            };
        }

        public LineupAuditResult RefreshFromProject() {
            var p = getProject != null ? getProject() : null;
            if (p == null) return new LineupAuditResult();

            loading = true;
            tbProject.Text = p.ProjectCode ?? "";
            tbStation.Text = !string.IsNullOrWhiteSpace(p.StationCode) ? p.StationCode : (p.NumberPrefix ?? "");
            tbLine.Text = string.IsNullOrWhiteSpace(p.LineCode) ? "L1" : p.LineCode;
            tbRevision.Text = string.IsNullOrWhiteSpace(p.Revision) ? "V1.0" : p.Revision;
            tbDesigner.Text = p.Designer ?? "";
            tbEditor.Text = p.Editor ?? "";
            chk11.Checked = p.Export11Columns;
            int pages = p.CadPages == null ? 0 : p.CadPages.Count;
            chkCad.Text = pages > 0
                ? "包含 CAD 位置图工作表（已建 " + pages + " 页）"
                : "包含 CAD 位置图空白模板页（当前尚未新建图页）";
            loading = false;
            synced = true;

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

        public void CommitMetadataIfChanged() {
            if (loading || !synced || getProject == null || commitMeta == null) return;
            var p = getProject();
            if (p == null) return;
            string proj = tbProject.Text.Trim();
            string sta = tbStation.Text.Trim();
            string line = string.IsNullOrWhiteSpace(tbLine.Text) ? "L1" : tbLine.Text.Trim();
            string rev = string.IsNullOrWhiteSpace(tbRevision.Text) ? "V1.0" : tbRevision.Text.Trim();
            string des = tbDesigner.Text.Trim();
            string ed = tbEditor.Text.Trim();
            bool exp11 = chk11.Checked;

            bool changed = (p.ProjectCode ?? "") != proj
                || (p.StationCode ?? "") != sta
                || (p.LineCode ?? "L1") != line
                || (p.Revision ?? "V1.0") != rev
                || (p.Designer ?? "") != des
                || (p.Editor ?? "") != ed
                || p.Export11Columns != exp11;
            if (!changed) return;

            p.ProjectCode = proj;
            p.StationCode = sta;
            if (string.IsNullOrWhiteSpace(p.NumberPrefix) && sta.Length > 0) p.NumberPrefix = sta;
            p.LineCode = line;
            p.Revision = rev;
            p.Designer = des;
            p.Editor = ed;
            p.Export11Columns = exp11;
            commitMeta(p, "已保存导出抬头设置");
        }
    }
}
