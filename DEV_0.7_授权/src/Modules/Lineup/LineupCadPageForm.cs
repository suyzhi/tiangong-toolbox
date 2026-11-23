using System;
using System.Windows.Forms;
using F = SolidEdgeFramework;
using A = SolidEdgeAssembly;

namespace TianGongCadSuite {
    // 独立版 CAD 位置图窗口：直接复用嵌入式 LineupCadStudioPanel，保持统一简洁交互与向后兼容。
    public sealed class LineupCadPageForm : Form {
        readonly LineupCadStudioPanel studio;

        public LineupCadPageForm(
            F.Application host,
            A.AssemblyDocument doc,
            Func<LineupProject> getProjectFn,
            Action<LineupProject, string> commitProjectFn,
            Action<LineupRecord> locateRecordFn,
            Action<IWin32Window> exportXlsxFn,
            Action<bool> suspendOwnerFn) {

            Ui.Shell(this, "CAD 位置图工作台", 1320, 800, 980, 580);
            FormBorderStyle = FormBorderStyle.Sizable;

            var footer = new ActionBar { Dock = DockStyle.Bottom, Height = Ui.S(60), Padding = Ui.SPad(18, 0, 18, 0) };
            var btnClose = Ui.Secondary("关闭", 76, 36);
            btnClose.Click += (s, e) => Close();
            footer.AddRight(btnClose);
            if (exportXlsxFn != null) {
                var btnExp = Ui.Primary("导出标准 Excel (.xlsx)", 168, 36);
                btnExp.Click += (s, e) => {
                    try { exportXlsxFn(this); studio.SyncFromProject(true); }
                    catch (Exception ex) { MessageBox.Show(this, ex.Message, "导出", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                };
                footer.AddRight(btnExp);
            }

            studio = new LineupCadStudioPanel(
                host, doc, getProjectFn, commitProjectFn, locateRecordFn,
                hide => {
                    if (hide) { Hide(); if (suspendOwnerFn != null) suspendOwnerFn(true); }
                    else { if (suspendOwnerFn != null) suspendOwnerFn(false); Show(); Activate(); }
                },
                msg => { footer.Status.Text = msg; });
            studio.Padding = Ui.SPad(16, 8, 16, 8);

            Controls.Add(studio);
            Controls.Add(footer);
        }
    }
}
