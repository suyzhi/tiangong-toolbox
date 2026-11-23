using System;
using System.Linq;
using System.Data;
using System.Drawing;
using System.Windows.Forms;

namespace TianGongCadSuite {
    public sealed partial class LineupTableForm {
        void InitializeHierarchy() {
            data.Columns.Add("上级");
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "上级", HeaderText = "所属上级", DataPropertyName = "上级", ReadOnly = true, Width = Ui.S(140) });
            grid.Columns["上级"].DisplayIndex = 2;
        }

        internal void InsertAt(bool after) {
            if (ActiveCategory == "气缸传感器") throw new InvalidOperationException("传感器请从气缸一键生成 H/W 后调整位置。");
            if (!ReadyNumbering(ActiveCategory)) return;
            var next = Snapshot();
            int index = next.Records.FindIndex(r => r.Id == CurrentId());
            var row = new LineupRecord { Category = ActiveCategory, Number = LineupNumbering.Next(next, ActiveCategory) };
            next.Records.Insert(index < 0 ? next.Records.Count : index + (after ? 1 : 0), row);
            data.DefaultView.Sort = "";
            Commit(next, row.Id, "已插入新行");
        }

        internal void MoveRows(int direction) {
            if (data.DefaultView.Sort.Length > 0) {
                data.DefaultView.Sort = "";
                throw new InvalidOperationException("已取消列排序并恢复清单原顺序，请再点一次上移 / 下移。");
            }
            var ids = SelectedIds();
            if (ids.Count == 0) return;
            var visible = grid.Rows.Cast<DataGridViewRow>().Select(r => Convert.ToString(((DataRowView)r.DataBoundItem)["Id"])).ToList();
            var next = Snapshot();
            LineupHierarchy.Move(next, visible, ids, direction);
            Commit(next, ids[0], "行顺序已更新");
            grid.ClearSelection();
            foreach (DataGridViewRow r in grid.Rows)
                if (ids.Contains(Convert.ToString(((DataRowView)r.DataBoundItem)["Id"]))) r.Cells[1].Selected = true;
        }

        void AssignCylinderDialog() {
            var ids = SelectedIds();
            var next = Snapshot();
            if (ids.Count == 0 || next.Records.Where(r => ids.Contains(r.Id)).Any(r => r.Category != "气缸"))
                throw new InvalidOperationException("请先选择一个或多个气缸行。");
            var valves = next.Records.Where(r => r.Category == "阀片").ToArray();
            if (valves.Length == 0) throw new InvalidOperationException("请先新增或一键生成阀片。");
            using (var d = new Form()) {
                Ui.Shell(d, "气缸归属到指定阀片", 520, 230, 440, 210);
                d.FormBorderStyle = FormBorderStyle.FixedDialog;
                var body = new Panel { Dock = DockStyle.Fill, Padding = Ui.SPad(18, 16, 18, 10), BackColor = Ui.Bg };
                var combo = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };
                combo.Items.AddRange(valves);
                combo.SelectedIndex = 0;
                var note = new Label {
                    Dock = DockStyle.Fill, Padding = Ui.SPad(0, 10, 0, 0), ForeColor = Ui.Muted, Font = Ui.F8,
                    Text = "所选气缸将归属到该阀片并同步编号；一阀控多缸自动生成 C01-1 / C01-2，对应 H/W 传感器同步更新，操作可撤销。"
                };
                var footer = new ActionBar { Dock = DockStyle.Bottom, Height = Ui.S(56), Padding = Ui.SPad(18, 0, 18, 0) };
                var ok = Ui.Primary("确定归属", 104, 34); ok.DialogResult = DialogResult.OK;
                var cancel = Ui.Secondary("取消", 76, 34); cancel.DialogResult = DialogResult.Cancel;
                footer.AddRight(cancel); footer.AddRight(ok);
                d.AcceptButton = ok; d.CancelButton = cancel;
                body.Controls.Add(note); body.Controls.Add(combo);
                d.Controls.Add(body); d.Controls.Add(footer);
                if (d.ShowDialog(this) == DialogResult.OK) AssignCylindersToValve(ids, ((LineupRecord)combo.SelectedItem).Id);
            }
        }

        internal void AssignCylindersToValve(System.Collections.Generic.IEnumerable<string> ids, string valveId) {
            var selected = ids.ToList();
            var next = Snapshot();
            LineupHierarchy.AssignCylinders(next, selected, valveId);
            Commit(next, selected.First(), "气缸与传感器归属已更新");
        }

        void ShowHierarchy() {
            var p = Snapshot();
            using (var d = new Form()) {
                Ui.Shell(d, "阀岛 → 阀片 → 气缸 → 传感器（双击定位）", 660, 560, 480, 400);
                d.FormBorderStyle = FormBorderStyle.Sizable;
                var tree = new TreeView { Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle, Font = Ui.F9, ItemHeight = Ui.S(24) };
                var root = new TreeNode("Lineup 清单层级");
                tree.Nodes.Add(root);
                var nodes = p.Records.ToDictionary(r => r.Id, r => new TreeNode(r.Category + " · " + r) { Tag = r.Id });
                var islands = p.Islands.GroupBy(i => i.Key).ToDictionary(g => g.Key, g => new TreeNode("阀岛 · " + g.Key + (string.IsNullOrWhiteSpace(g.First().Model) ? "" : " · " + g.First().Model)));
                foreach (var n in islands.Values) root.Nodes.Add(n);
                foreach (var r in p.Records) {
                    string parent = r.Category == "气缸传感器" ? r.CylinderId : r.Category == "气缸" ? r.ValveId : "";
                    TreeNode n;
                    if (parent != r.Id && nodes.TryGetValue(parent, out n) && (r.Category == "气缸" ? p.Records.First(x => x.Id == parent).Category == "阀片" : p.Records.First(x => x.Id == parent).Category == "气缸"))
                        n.Nodes.Add(nodes[r.Id]);
                    else if (r.Category == "阀片" && islands.TryGetValue(LineupNumbering.IslandKey(r.Number), out n))
                        n.Nodes.Add(nodes[r.Id]);
                    else
                        root.Nodes.Add(nodes[r.Id]);
                }
                tree.NodeMouseDoubleClick += (s, e) => {
                    if (e.Node.Tag != null) {
                        category.SelectedIndex = 0;
                        search.Clear();
                        GoStep(0);
                        SelectId((string)e.Node.Tag);
                        d.Close();
                    }
                };
                var body = new Panel { Dock = DockStyle.Fill, Padding = Ui.SPad(16, 12, 16, 8), BackColor = Ui.Bg };
                body.Controls.Add(tree);
                var footer = new ActionBar { Dock = DockStyle.Bottom, Height = Ui.S(54), Padding = Ui.SPad(16, 0, 16, 0) };
                var close = Ui.Secondary("关闭", 76, 32); close.Click += (s, e) => d.Close();
                footer.AddRight(close);
                footer.Status.Text = "双击任意节点直接跳到表格对应行";
                d.Controls.Add(body); d.Controls.Add(footer);
                tree.ExpandAll();
                d.ShowDialog(this);
            }
        }
    }
}
