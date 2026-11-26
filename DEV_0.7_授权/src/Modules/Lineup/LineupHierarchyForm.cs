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
            // 1. 气动系统（阀岛树）视图：按阀路 / 同阀多缸 / 磁开同步上移下移并级联更新 V**/C**/H/W 编号
            if (activeViewMode == 0 && pneumaticPanel != null && pneumaticPanel.Visible) {
                string islandKey = pneumaticPanel.CurrentIslandKey;
                if (string.IsNullOrEmpty(islandKey)) {
                    ShowToast("请先在左侧选择具体阀岛后再上移 / 下移。", Ui.Muted);
                    return;
                }
                int activeCol;
                var pIds = pneumaticPanel.SelectedPrimaryIdsForMove(out activeCol);
                if (pIds.Count == 0) {
                    ShowToast("请先选中要移动的行。", Ui.Muted);
                    return;
                }
                var pNext = Snapshot();
                string focusId, msg;
                bool pMoved = LineupPneumaticOps.MoveIslandRows(pNext, islandKey, pIds, pneumaticPanel.CurrentSubTab, direction, out focusId, out msg);
                if (!pMoved) {
                    ShowToast(string.IsNullOrEmpty(msg) ? (direction < 0 ? "选中行已到最顶部" : "选中行已到最底部") : msg, Ui.Muted);
                    return;
                }
                Commit(pNext, focusId, msg);
                pneumaticPanel.RestoreMoveSelection(pIds, activeCol);
                ShowToast(msg, Ui.Ok, "撤销", (s, e) => Try(Undo));
                return;
            }

            // 2. 普通传感器 / 其他电气件 / BOM 总表视图
            grid.EndEdit();
            if (data.DefaultView.Sort.Length > 0) {
                data.DefaultView.Sort = "";
                foreach (DataGridViewColumn col in grid.Columns) col.HeaderCell.SortGlyphDirection = SortOrder.None;
                ShowToast("已取消列排序并恢复清单原顺序，请再点一次上移 / 下移。", Ui.Warn);
                return;
            }
            var ids = SelectedIds();
            if (ids.Count == 0 && grid.CurrentRow != null && grid.CurrentRow.DataBoundItem != null) {
                string cur = Convert.ToString(((DataRowView)grid.CurrentRow.DataBoundItem)["Id"]);
                if (!string.IsNullOrEmpty(cur)) ids.Add(cur);
            }
            if (ids.Count == 0) {
                ShowToast("请先选中要移动的行。", Ui.Muted);
                return;
            }
            int keepCol = grid.CurrentCell != null ? grid.CurrentCell.ColumnIndex : 1;
            var visible = grid.Rows.Cast<DataGridViewRow>()
                .Where(r => r.DataBoundItem != null)
                .Select(r => Convert.ToString(((DataRowView)r.DataBoundItem)["Id"]))
                .ToList();
            var next = Snapshot();
            var beforeOrder = next.Records.Select(r => r.Id).ToList();
            LineupHierarchy.Move(next, visible, ids, direction);
            if (beforeOrder.SequenceEqual(next.Records.Select(r => r.Id))) {
                ShowToast(direction < 0 ? "选中行已到最顶部" : "选中行已到最底部", Ui.Muted);
                return;
            }
            string moveMsg = direction < 0 ? "已上移选中行" : "已下移选中行";
            Commit(next, ids[0], moveMsg);
            int targetCol = (keepCol >= 0 && keepCol < grid.Columns.Count && grid.Columns[keepCol].Visible) ? keepCol : 1;
            if (targetCol >= grid.Columns.Count || !grid.Columns[targetCol].Visible) {
                var firstVis = grid.Columns.Cast<DataGridViewColumn>().FirstOrDefault(c => c.Visible);
                targetCol = firstVis != null ? firstVis.Index : 0;
            }
            var idSet = new System.Collections.Generic.HashSet<string>(ids, StringComparer.Ordinal);
            var matched = new System.Collections.Generic.List<DataGridViewRow>();
            foreach (DataGridViewRow r in grid.Rows)
                if (r.DataBoundItem != null && idSet.Contains(Convert.ToString(((DataRowView)r.DataBoundItem)["Id"]))) matched.Add(r);
            if (matched.Count > 0 && targetCol < grid.Columns.Count) {
                grid.ClearSelection();
                grid.CurrentCell = matched[0].Cells[targetCol];
                foreach (var r in matched) r.Cells[targetCol].Selected = true;
                try {
                    int fi = matched[0].Index;
                    if (fi < grid.FirstDisplayedScrollingRowIndex || fi >= grid.FirstDisplayedScrollingRowIndex + Math.Max(1, grid.DisplayedRowCount(false)))
                        grid.FirstDisplayedScrollingRowIndex = Math.Max(0, fi - 2);
                } catch { }
            }
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
            LineupNumbering.SyncCadPageNumbers(next);
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
