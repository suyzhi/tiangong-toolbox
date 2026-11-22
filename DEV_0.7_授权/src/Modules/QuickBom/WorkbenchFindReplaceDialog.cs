using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace TianGongCadSuite {
    // 出图清单工作台的「批量查找替换」小对话框（Ctrl+H）：
    // 顶部批量改名抽屉只作用于「新文件名」列；本窗口支持对任意可编辑列（或全部可编辑列）做精确/模糊批量替换，
    // 并支持按「仅选中行 / 当前可见行 / 全部行」限定范围，且整批替换可一步 Ctrl+Z 撤销。
    internal sealed class WorkbenchFindReplaceDialog : Form {
        internal sealed class Request {
            public int ColumnIndex; // -1 表示全部可编辑列
            public string ColumnLabel;
            public int Scope;       // 0 仅选中行 / 1 当前可见行 / 2 全部行
            public string Find;
            public string Replace;
            public bool MatchCase;
        }

        sealed class ColItem {
            public readonly int Index;
            public readonly string Name;
            public ColItem(int index, string name) { Index = index; Name = name; }
            public override string ToString() { return Name; }
        }

        static string lastFind = "";
        static string lastReplace = "";
        static bool lastMatchCase;

        readonly ComboBox colBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Font = Ui.F9 };
        readonly ComboBox scopeBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Font = Ui.F9 };
        readonly TextBox findBox = new TextBox { BorderStyle = BorderStyle.FixedSingle, Font = Ui.F9 };
        readonly TextBox replaceBox = new TextBox { BorderStyle = BorderStyle.FixedSingle, Font = Ui.F9 };
        readonly CheckBox matchCaseBox = new CheckBox { Text = "区分大小写", AutoSize = true, Font = Ui.F8, ForeColor = Ui.Text2 };
        readonly CadKeyShield keyShield;

        public Request Result { get; private set; }

        public WorkbenchFindReplaceDialog(IList<KeyValuePair<int, string>> editableColumns, int defaultColIndex, int selectedRowCount, int visibleRowCount, int totalRowCount) {
            Ui.Shell(this, "表格批量查找替换", 480, 290, 440, 260);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            keyShield = new CadKeyShield(this);

            int defSel = 0;
            if (editableColumns != null) {
                for (int i = 0; i < editableColumns.Count; i++) {
                    colBox.Items.Add(new ColItem(editableColumns[i].Key, editableColumns[i].Value));
                    if (editableColumns[i].Key == defaultColIndex) defSel = i;
                }
            }
            colBox.Items.Add(new ColItem(-1, "全部可编辑列"));
            if (colBox.Items.Count > 0) colBox.SelectedIndex = defSel;

            scopeBox.Items.Add("仅选中行（" + selectedRowCount + " 行）");
            scopeBox.Items.Add("当前筛选可见行（" + visibleRowCount + " 行）");
            scopeBox.Items.Add("全部行（" + totalRowCount + " 行）");
            scopeBox.SelectedIndex = selectedRowCount >= 2 ? 0 : 1;

            findBox.Text = lastFind;
            replaceBox.Text = lastReplace;
            matchCaseBox.Checked = lastMatchCase;

            var tbl = new TableLayoutPanel {
                Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 5,
                BackColor = Ui.Bg, Padding = Ui.SPad(18, 14, 18, 8)
            };
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Ui.S(82)));
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            for (int r = 0; r < 5; r++) tbl.RowStyles.Add(new RowStyle(SizeType.Absolute, Ui.S(36)));

            AddRow(tbl, 0, "作用列", colBox);
            AddRow(tbl, 1, "作用范围", scopeBox);
            AddRow(tbl, 2, "查找内容", findBox);
            AddRow(tbl, 3, "替换为", replaceBox);
            matchCaseBox.Margin = Ui.SPad(0, 6, 0, 0);
            tbl.Controls.Add(matchCaseBox, 1, 4);

            var foot = new ActionBar { Dock = DockStyle.Bottom, Height = Ui.S(58), Padding = Ui.SPad(18, 0, 18, 0) };
            var hint = Ui.Muted8("替换后可按 Ctrl+Z 整批撤销");
            var cancelBtn = Ui.Secondary("取消", 78, 34);
            var okBtn = Ui.Primary("全部替换", 104, 36);
            cancelBtn.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            okBtn.Click += (s, e) => Submit();
            foot.AddRight(cancelBtn);
            foot.AddRight(okBtn);
            foot.Status = hint;

            Controls.Add(tbl);
            Controls.Add(foot);
            AcceptButton = okBtn;
            CancelButton = cancelBtn;

            Shown += (s, e) => { findBox.SelectAll(); findBox.Focus(); };
        }

        static void AddRow(TableLayoutPanel tbl, int row, string label, Control ctrl) {
            var lbl = new Label {
                Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
                Font = Ui.F9, ForeColor = Ui.Text2, BackColor = Ui.Bg
            };
            ctrl.Dock = DockStyle.Fill;
            ctrl.Margin = Ui.SPad(0, 4, 0, 4);
            tbl.Controls.Add(lbl, 0, row);
            tbl.Controls.Add(ctrl, 1, row);
        }

        void Submit() {
            string find = findBox.Text ?? "";
            if (find.Length == 0) {
                MessageBox.Show(this, "请先输入要查找的文字。", "表格批量查找替换", MessageBoxButtons.OK, MessageBoxIcon.Information);
                findBox.Focus();
                return;
            }
            var ci = colBox.SelectedItem as ColItem;
            lastFind = find;
            lastReplace = replaceBox.Text ?? "";
            lastMatchCase = matchCaseBox.Checked;
            Result = new Request {
                ColumnIndex = ci != null ? ci.Index : -1,
                ColumnLabel = ci != null ? ci.Name : "全部可编辑列",
                Scope = Math.Max(0, scopeBox.SelectedIndex),
                Find = lastFind,
                Replace = lastReplace,
                MatchCase = lastMatchCase
            };
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
