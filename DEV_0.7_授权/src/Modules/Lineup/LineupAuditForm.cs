using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace TianGongCadSuite {
    // LineUP《签字页》自动规范自检窗口：列出所有错误/警告/提示，双击跳到对应 BOM 行，并提供一键补齐阀片、补齐 H/W、同步备注
    public sealed class LineupAuditForm : Form {
        readonly Func<LineupProject> getProject;
        readonly Action<string> jumpToRecord;
        readonly Action fixMissingValves;
        readonly Action fixMissingSensors;
        readonly Action syncSensorNotes;

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

            AutoScaleMode = AutoScaleMode.None;
            Text = "LineUP 规范自检（对照公司签字页标准）";
            Font = Ui.F9;
            BackColor = Ui.Bg;
            ForeColor = Ui.Text;
            Width = Ui.S(920);
            Height = Ui.S(580);
            StartPosition = FormStartPosition.CenterParent;

            var top = new Panel { Dock = DockStyle.Top, Height = Ui.S(48), BackColor = Ui.Rail, Padding = Ui.SPad(12, 8, 12, 8) };
            var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, BackColor = Ui.Rail };
            Action<string, bool, Action> addBtn = (text, primary, act) => {
                var b = primary ? Ui.Primary(text, 140, 30) : Ui.Secondary(text, 144, 30);
                b.Margin = Ui.SPad(0, 0, 8, 0);
                b.Click += (s, e) => {
                    try { act(); RefreshAudit(); }
                    catch (Exception ex) { MessageBox.Show(this, ex.Message, "规范自检", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                };
                bar.Controls.Add(b);
            };
            addBtn("一键补齐缺失阀片", true, () => { if (fixMissingValves != null) fixMissingValves(); });
            addBtn("一键补齐缺失 H/W", false, () => { if (fixMissingSensors != null) fixMissingSensors(); });
            addBtn("同步气缸备注到传感器", false, () => { if (syncSensorNotes != null) syncSensorNotes(); });
            addBtn("重新检查", false, () => { });
            top.Controls.Add(bar);

            summary.Dock = DockStyle.Top;
            summary.Height = Ui.S(34);
            summary.BackColor = Ui.Bg;
            summary.Font = Ui.F9B;
            summary.Padding = Ui.SPad(14, 0, 14, 0);
            summary.TextAlign = ContentAlignment.MiddleLeft;

            grid.Dock = DockStyle.Fill;
            grid.ReadOnly = true;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.MultiSelect = false;
            grid.RowHeadersVisible = false;
            grid.BackgroundColor = Color.White;
            grid.BorderStyle = BorderStyle.None;
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

            Controls.Add(grid);
            Controls.Add(summary);
            Controls.Add(top);

            RefreshAudit();
        }

        void RefreshAudit() {
            var res = LineupAudit.Run(getProject());
            currentIssues = res.Issues;
            grid.Rows.Clear();
            foreach (var iss in currentIssues) {
                string sev = iss.Severity == LineupAuditSeverity.Error ? "错误" : iss.Severity == LineupAuditSeverity.Warning ? "警告" : "提示";
                int idx = grid.Rows.Add(sev, iss.Category, iss.Number, iss.Message);
                var row = grid.Rows[idx];
                if (iss.Severity == LineupAuditSeverity.Error) row.DefaultCellStyle.BackColor = Color.FromArgb(0xFC, 0xE4, 0xD6);
                else if (iss.Severity == LineupAuditSeverity.Warning) row.DefaultCellStyle.BackColor = Color.FromArgb(0xFF, 0xF8, 0xE1);
            }
            if (currentIssues.Count == 0) {
                summary.ForeColor = Color.FromArgb(0x15, 0x80, 0x3D);
                summary.Text = "全部检查通过！未发现编号冲突、缺漏字段或配对异常。";
            } else {
                summary.ForeColor = res.ErrorCount > 0 ? Color.FromArgb(0xB9, 0x1C, 0x1C) : Color.FromArgb(0x92, 0x40, 0x0E);
                summary.Text = "共发现 " + currentIssues.Count + " 项（错误 " + res.ErrorCount + " · 警告 " + res.WarningCount + " · 提示 " + res.InfoCount + "），双击条目可定位到表格行。";
            }
        }
    }
}
