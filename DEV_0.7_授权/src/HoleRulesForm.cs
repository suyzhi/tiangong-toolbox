using System;
using System.Drawing;
using System.Windows.Forms;

namespace TianGongCadSuite {
    // 配做规则编辑窗口（自动打孔 → 参考孔卡片右上角「配做规则」）。
    // 上面选一套预设，下面的表可以逐规格改；「保存并应用」写进 %LOCALAPPDATA%\TianGongCadSuite\hole-rules.tsv，
    // 之后点的参考孔都按新规则反推（已经点好的参考孔由调用方决定要不要重推）。
    public sealed class HoleRulesForm : Form {
        const string AnyLabel = "全部规格";
        readonly Segmented presetSeg;
        readonly DataGridView grid = new DataGridView();
        readonly Label nameLabel = new Label();
        readonly Label status = new Label();
        readonly HoleRuleSet[] presets = HoleRuleBook.Presets;
        bool loading;

        public HoleRuleSet Result { get; private set; }

        public HoleRulesForm(HoleRuleSet current){
            Ui.Shell(this, "配做规则", 620, 470, 560, 420);
            FormBorderStyle = FormBorderStyle.SizableToolWindow;
            var names = new string[presets.Length];
            for (int i = 0; i < presets.Length; i++) names[i] = presets[i].Name;
            presetSeg = new Segmented(names);
            presetSeg.SelectedIndex = -1;

            var presetCard = Ui.Card("预设", 96, "点一套常用的，下面的表会照它填好");
            var pb = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
            presetSeg.SetBounds(0, 0, Math.Max(480, presetSeg.PreferredWidth), 26);
            nameLabel.SetBounds(0, 32, 560, 20); nameLabel.AutoSize = false; nameLabel.Font = Ui.F8; nameLabel.ForeColor = Ui.Muted; nameLabel.BackColor = Color.White;
            pb.Controls.Add(presetSeg); pb.Controls.Add(nameLabel);
            presetCard.Controls.Add(pb);
            presetSeg.SelectedIndexChanged += (s, e) => { if (!loading && presetSeg.SelectedIndex >= 0) Fill(presets[presetSeg.SelectedIndex]); };

            var tableCard = Ui.Card("规则表", 0, "先找这个规格专门的一行，没有再用「全部规格」");
            var add = Ui.Small("添加一行"); var del = Ui.Small("删除选中行");
            add.Click += (s, e) => { grid.Rows.Add("M8", HoleRuleBook.RoleName(HoleRefRole.Clearance), HoleSpec.KindName(HoleKind.Through), HoleStandards.Normal); Changed(); };
            del.Click += (s, e) => { foreach (DataGridViewRow r in grid.SelectedRows) if (!r.IsNewRow) grid.Rows.Remove(r); Changed(); };
            tableCard.AddHeader(add); tableCard.AddHeader(del);
            BuildGrid();
            tableCard.Controls.Add(grid);

            var hint = new Label { AutoSize = false, Height = 36, ForeColor = Ui.Muted, Font = Ui.F8, BackColor = Ui.Bg,
                Text = "参考孔是螺纹底孔 = 参考件上是螺纹孔，螺钉从目标件这边穿过去；参考孔是过孔 = 螺钉穿过参考件拧进/穿过目标件。\r\n过孔配合决定通孔、沉孔、锥沉的孔径（天工孔标准表「常规螺钉间隙」的紧密 / 普通 / 宽松三档）。" };

            var actions = new Panel { Height = 40, BackColor = Ui.Bg };
            var ok = Ui.Primary("保存并应用", 120, 32);
            var cancel = Ui.Secondary("取消", 80, 30);
            ok.Click += (s, e) => Save();
            cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            status.AutoSize = false; status.ForeColor = Ui.Bad; status.BackColor = Ui.Bg; status.TextAlign = ContentAlignment.MiddleLeft; status.AutoEllipsis = true;
            actions.Controls.Add(ok); actions.Controls.Add(cancel); actions.Controls.Add(status);
            actions.Resize += (s, e) => {
                cancel.SetBounds(actions.Width - 80, 5, 80, 30);
                ok.SetBounds(cancel.Left - 8 - 120, 4, 120, 32);
                status.SetBounds(0, 4, Math.Max(40, ok.Left - 8), 32);
            };
            CancelButton = cancel;

            var stack = new VerticalStack { Dock = DockStyle.Fill };
            stack.Add(presetCard); stack.Add(tableCard, true); stack.Add(hint); stack.Add(actions);
            Controls.Add(stack);

            Fill(current ?? HoleRuleBook.Current);
        }

        void BuildGrid(){
            grid.Dock = DockStyle.Fill;
            grid.BackgroundColor = Color.White; grid.BorderStyle = BorderStyle.None;
            grid.AllowUserToAddRows = false; grid.AllowUserToResizeRows = false;
            grid.RowHeadersVisible = false; grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            grid.EditMode = DataGridViewEditMode.EditOnEnter;
            grid.Font = Ui.F9;
            var size = Combo("规格", 22);
            size.Items.Add(AnyLabel);
            foreach (var r in HoleMatcher.Table) size.Items.Add(r.Size);
            var role = Combo("参考孔是", 26);
            role.Items.Add(HoleRuleBook.RoleName(HoleRefRole.TapDrill)); role.Items.Add(HoleRuleBook.RoleName(HoleRefRole.Clearance));
            var target = Combo("目标件配做", 26);
            foreach (HoleKind k in Enum.GetValues(typeof(HoleKind))) target.Items.Add(HoleSpec.KindName(k));
            var fit = Combo("过孔配合", 26);
            foreach (var f in HoleRuleBook.Fits) fit.Items.Add(f);
            grid.Columns.Add(size); grid.Columns.Add(role); grid.Columns.Add(target); grid.Columns.Add(fit);
            grid.CellValueChanged += (s, e) => { if (!loading) Changed(); };
            grid.CurrentCellDirtyStateChanged += (s, e) => { if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            grid.DataError += (s, e) => { e.ThrowException = false; };
        }
        static DataGridViewComboBoxColumn Combo(string header, int weight){
            return new DataGridViewComboBoxColumn { HeaderText = header, FillWeight = weight, FlatStyle = FlatStyle.Flat, DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton };
        }

        void Fill(HoleRuleSet set){
            loading = true;
            try {
                grid.Rows.Clear();
                foreach (var r in set.Rules)
                    grid.Rows.Add(r.Size == HoleRuleSet.AnySize ? AnyLabel : r.Size, HoleRuleBook.RoleName(r.Role), HoleSpec.KindName(r.Target), r.Fit);
            } finally { loading = false; }
            Changed();
        }

        // 表 → 规则（读不懂的格子当成问题报出来）
        HoleRuleSet Read(out string problem){
            problem = "";
            var set = new HoleRuleSet();
            foreach (DataGridViewRow row in grid.Rows) {
                if (row.IsNewRow) continue;
                string size = Convert.ToString(row.Cells[0].Value), role = Convert.ToString(row.Cells[1].Value),
                       target = Convert.ToString(row.Cells[2].Value), fit = Convert.ToString(row.Cells[3].Value);
                var rule = new HoleRule { Size = size == AnyLabel ? HoleRuleSet.AnySize : size, Fit = fit };
                if (role == HoleRuleBook.RoleName(HoleRefRole.TapDrill)) rule.Role = HoleRefRole.TapDrill;
                else if (role == HoleRuleBook.RoleName(HoleRefRole.Clearance)) rule.Role = HoleRefRole.Clearance;
                else { problem = "第 " + (row.Index + 1) + " 行没选「参考孔是」。"; return set; }
                if (!HoleRuleBook.TryKind(target, out rule.Target)) { problem = "第 " + (row.Index + 1) + " 行没选「目标件配做」。"; return set; }
                set.Rules.Add(rule);
            }
            problem = set.Problem();
            set.Name = HoleRuleBook.NameOf(set);
            return set;
        }

        void Changed(){
            string problem;
            var set = Read(out problem);
            loading = true;
            try {
                int at = -1;
                for (int i = 0; i < presets.Length; i++) if (presets[i].SameRules(set)) at = i;
                presetSeg.SelectedIndex = at;
            } finally { loading = false; }
            nameLabel.Text = problem.Length > 0 ? "" : "现在是：" + set.Name + "　·　" + set.Describe();
            status.Text = problem;
        }

        void Save(){
            string problem;
            var set = Read(out problem);
            if (problem.Length > 0) { status.Text = problem; return; }
            try { HoleRuleBook.SaveToDisk(set); }
            catch (Exception e) { Log.Write("HoleRulesSave", e); status.Text = "没存上：" + e.Message; return; }
            Result = set;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
