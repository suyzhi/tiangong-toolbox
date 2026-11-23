using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace TianGongCadSuite {
    // 独立版签字页规范自检窗口（保留兼容；主流程已直接内嵌到步骤③「检查与导出」面板）。
    public sealed class LineupAuditForm : Form {
        readonly Func<LineupProject> getProject;
        readonly Action<string> jumpToRecord;
        readonly Action fixMissingValves;
        readonly Action fixMissingSensors;
        readonly Action syncSensorNotes;

        readonly RoundBox banner = new RoundBox();
        readonly Label summary = new Label();
        readonly DataGridView grid = new DataGridView();
        List<LineupAuditIssue> currentIssues = new List<LineupAuditIssue>();

        public LineupAuditForm(
            Func<LineupProject> getProjectFn,
            Action<string> jumpFn,
            Action fixValvesFn,
            Action fixSensorsFn,
            Action syncNotesFn) {

            getProject = getProjectFn;
            jumpToRecord = jumpFn;
            fixMissingValves = fixValvesFn;
            fixMissingSensors = fixSensorsFn;
            syncSensorNotes = syncNotesFn;

            Ui.Shell(this, "Lineup 签字页规范自检", 900, 560, 680, 420);
            FormBorderStyle = FormBorderStyle.Sizable;

            var body = new Panel { Dock = DockStyle.Fill, Padding = Ui.SPad(18, 14, 18, 10), BackColor = Ui.Bg };
            banner.Dock = DockStyle.Top;
            banner.Height = Ui.S(42);
            banner.Padding = Ui.SPad(14, 0, 14, 0);
            summary.Dock = DockStyle.Fill;
            summary.Font = Ui.F9B;
            summary.BackColor = Color.Transparent;
            summary.TextAlign = ContentAlignment.MiddleLeft;
            banner.Controls.Add(summary);

            var gap = new Panel { Dock = DockStyle.Top, Height = Ui.S(10), BackColor = Ui.Bg };

            grid.Dock = DockStyle.Fill;
            grid.ReadOnly = true;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.MultiSelect = false;
            grid.RowHeadersVisible = false;
            grid.BackgroundColor = Color.White;
            grid.BorderStyle = BorderStyle.None;
            grid.GridColor = Color.FromArgb(0xF0, 0xF0, 0xF3);
            grid.EnableHeadersVisualStyles = false;
            grid.ColumnHeadersDefaultCellStyle.BackColor = Ui.Rail;
            grid.ColumnHeadersDefaultCellStyle.ForeColor = Ui.Muted;
            grid.ColumnHeadersDefaultCellStyle.Font = Ui.F8;
            grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Ui.Rail;
            grid.DefaultCellStyle.SelectionBackColor = Ui.AccentSoft;
            grid.DefaultCellStyle.SelectionForeColor = Ui.Text;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "级别", HeaderText = "级别", FillWeight = 10 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "类目", HeaderText = "类目", FillWeight = 16 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "编号", HeaderText = "编号", FillWeight = 22 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "说明", HeaderText = "检查说明（双击跳转对应行）", FillWeight = 52 });
            Ui.FitGrid(grid);
            grid.CellDoubleClick += (s, e) => {
                if (e.RowIndex >= 0 && e.RowIndex < currentIssues.Count) {
                    string id = currentIssues[e.RowIndex].RecordId;
                    if (!string.IsNullOrEmpty(id) && jumpToRecord != null) jumpToRecord(id);
                }
            };

            var footer = new ActionBar { Dock = DockStyle.Bottom, Height = Ui.S(60), Padding = Ui.SPad(18, 0, 18, 0) };
            var btnFix = Ui.Primary("一键补齐阀片 / H-W / 备注", 186, 36);
            btnFix.Click += (s, e) => {
                try {
                    if (fixMissingValves != null) fixMissingValves();
                    if (fixMissingSensors != null) fixMissingSensors();
                    if (syncSensorNotes != null) syncSensorNotes();
                    RefreshAudit();
                } catch (Exception ex) {
                    MessageBox.Show(this, ex.Message, "规范自检", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
            var btnClose = Ui.Secondary("关闭", 76, 36);
            btnClose.Click += (s, e) => Close();
            footer.AddLeft(btnFix);
            footer.AddRight(btnClose);

            body.Controls.Add(grid);
            body.Controls.Add(gap);
            body.Controls.Add(banner);
            Controls.Add(body);
            Controls.Add(footer);

            RefreshAudit();
        }

        void RefreshAudit() {
            var res = LineupAudit.Run(getProject());
            currentIssues = res.Issues;
            grid.SuspendLayout();
            try {
                grid.Rows.Clear();
                if (currentIssues.Count > 0) {
                    var batch = new DataGridViewRow[currentIssues.Count];
                    for (int i = 0; i < currentIssues.Count; i++) {
                        var iss = currentIssues[i];
                        string sev = iss.Severity == LineupAuditSeverity.Error ? "错误" : iss.Severity == LineupAuditSeverity.Warning ? "提醒" : "建议";
                        var row = new DataGridViewRow();
                        row.CreateCells(grid, sev, iss.Category, iss.Number, iss.Message);
                        if (iss.Severity == LineupAuditSeverity.Error) row.DefaultCellStyle.BackColor = Color.FromArgb(0xFE, 0xF2, 0xF2);
                        else if (iss.Severity == LineupAuditSeverity.Warning) row.DefaultCellStyle.BackColor = Color.FromArgb(0xFF, 0xFB, 0xEB);
                        batch[i] = row;
                    }
                    grid.Rows.AddRange(batch);
                }
            } finally {
                grid.ResumeLayout();
            }
            if (currentIssues.Count == 0) {
                banner.Fill = Ui.OkSoft;
                summary.ForeColor = Ui.Ok;
                summary.Text = "✓ 全部检查通过！未发现编号冲突、缺漏字段或配对异常。";
            } else {
                banner.Fill = res.ErrorCount > 0 ? Ui.BadSoft : Ui.WarnSoft;
                summary.ForeColor = res.ErrorCount > 0 ? Ui.Bad : Ui.Warn;
                summary.Text = "共发现 " + currentIssues.Count + " 项（错误 " + res.ErrorCount + " · 提醒 " + res.WarningCount + " · 建议 " + res.InfoCount + "），双击条目可定位到表格行。";
            }
            banner.Invalidate();
        }
    }
}
