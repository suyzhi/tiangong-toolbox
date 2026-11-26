using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TianGongCadSuite {
    // Excel / WPS 风格表头筛选与排序弹出面板（用于「出图清单工作台」列头 ▾ 按钮与表头右键）。
    // 支持：一键升序/降序/恢复默认 + 实时搜索本列值 + 唯一值多选（含计数与空白项）+ 清除本列筛选。
    internal sealed class ColumnFilterPopup {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);
        const int EM_SETCUEBANNER = 0x1501;
        sealed class Entry {
            public readonly string Value;
            public readonly int Count;
            public Entry(string value, int count) { Value = value; Count = count; }
            public override string ToString() { return Value + "  (" + Count + ")"; }
        }

        public static ToolStripDropDown Show(
            Control anchor,
            Point screenPoint,
            string headerText,
            IList<KeyValuePair<string, int>> distinctValues,
            HashSet<string> currentAllowed, // null = 当前未筛选（全选）
            int currentSortDir,             // 0 默认 / 1 升序 / -1 降序
            Action<int> onSort,
            Action<HashSet<string>> onApplyFilter) {

            var drop = new ToolStripDropDown {
                Padding = new Padding(0),
                Margin = new Padding(0),
                AutoClose = true,
                DropShadowEnabled = true
            };

            int w = Ui.S(316), h = Ui.S(376);
            var root = new Panel { Size = new Size(w, h), BackColor = Ui.Bg, Padding = Ui.SPad(10, 8, 10, 10) };
            Ui.StyleTree(root);
            root.Paint += (s, e) => {
                using (var p = new Pen(Ui.Line)) e.Graphics.DrawRectangle(p, 0, 0, root.Width - 1, root.Height - 1);
            };

            // 1. 标题
            var lblTitle = new Label {
                Text = "筛选与排序 · " + headerText,
                AutoSize = false, Height = Ui.S(22), Dock = DockStyle.Top,
                Font = Ui.F9B, ForeColor = Ui.Text, BackColor = Ui.Bg, AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft
            };

            // 2. 排序行
            var sortBar = new FlowLayoutPanel {
                Dock = DockStyle.Top, Height = Ui.S(34), BackColor = Ui.Bg,
                FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Padding = Ui.SPad(0, 4, 0, 2)
            };
            var bAsc = Ui.Small(currentSortDir == 1 ? "✓ ↑ 升序" : "↑ 升序");
            var bDesc = Ui.Small(currentSortDir == -1 ? "✓ ↓ 降序" : "↓ 降序");
            var bDef = Ui.Small("默认顺序");
            if (currentSortDir == 1) ((FlatButton)bAsc).SetFore(Ui.Accent); else ((FlatButton)bAsc).SetFore(Ui.Text2);
            if (currentSortDir == -1) ((FlatButton)bDesc).SetFore(Ui.Accent); else ((FlatButton)bDesc).SetFore(Ui.Text2);
            ((FlatButton)bDef).SetFore(Ui.Muted);
            bAsc.Click += (s, e) => { drop.Close(); if (onSort != null) onSort(1); };
            bDesc.Click += (s, e) => { drop.Close(); if (onSort != null) onSort(-1); };
            bDef.Click += (s, e) => { drop.Close(); if (onSort != null) onSort(0); };
            sortBar.Controls.AddRange(new Control[] { bAsc, bDesc, bDef });

            // 3. 搜索框 + 快捷全选/反选行
            var searchWrap = new Panel { Dock = DockStyle.Top, Height = Ui.S(32), BackColor = Ui.Bg, Padding = Ui.SPad(0, 3, 0, 3) };
            var searchBox = new TextBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle, Font = Ui.F9 };
            searchBox.HandleCreated += (s, e) => { try { SendMessage(searchBox.Handle, EM_SETCUEBANNER, (IntPtr)1, "搜索本列的值…"); } catch { } };
            searchWrap.Controls.Add(searchBox);

            var selBar = new FlowLayoutPanel {
                Dock = DockStyle.Top, Height = Ui.S(28), BackColor = Ui.Bg,
                FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Padding = new Padding(0)
            };
            var bAll = Ui.Small("全选");
            var bNone = Ui.Small("全不选");
            var bInvert = Ui.Small("反选");
            var bOnlyBlank = Ui.Small("只选空白");
            foreach (var b in new[] { bAll, bNone, bInvert, bOnlyBlank }) ((FlatButton)b).SetFore(Ui.Text2);
            bool hasBlank = distinctValues.Any(kv => kv.Key == WorkbenchRules.BlankToken);
            bOnlyBlank.Visible = hasBlank;
            selBar.Controls.AddRange(new Control[] { bAll, bNone, bInvert, bOnlyBlank });

            // 4. 勾选列表（与 Excel / WPS 一致：输入搜索词时自动勾中全部搜索匹配项，直接回车就能筛选；清空搜索词恢复原勾选态）
            var allEntries = new List<Entry>();
            var baseChecked = new HashSet<string>(StringComparer.Ordinal);
            foreach (var kv in distinctValues) {
                allEntries.Add(new Entry(kv.Key, kv.Value));
                if (currentAllowed == null || currentAllowed.Contains(kv.Key)) baseChecked.Add(kv.Key);
            }
            var searchChecked = new HashSet<string>(StringComparer.Ordinal);

            var list = new CheckedListBox {
                Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle, IntegralHeight = false,
                CheckOnClick = true, HorizontalScrollbar = true, Font = Ui.F8, BackColor = Color.White
            };
            bool populating = false;
            Action<bool> rebuildList = resetSearchChecks => {
                populating = true;
                list.BeginUpdate();
                try {
                    list.Items.Clear();
                    string kw = (searchBox.Text ?? "").Trim();
                    if (kw.Length > 0) {
                        if (resetSearchChecks) searchChecked.Clear();
                        foreach (var en in allEntries) {
                            if (en.Value.IndexOf(kw, StringComparison.OrdinalIgnoreCase) < 0) continue;
                            if (resetSearchChecks) searchChecked.Add(en.Value);
                            list.Items.Add(en, searchChecked.Contains(en.Value));
                        }
                    } else {
                        foreach (var en in allEntries) {
                            list.Items.Add(en, baseChecked.Contains(en.Value));
                        }
                    }
                } finally {
                    list.EndUpdate();
                    populating = false;
                }
            };
            list.ItemCheck += (s, e) => {
                if (populating || e.Index < 0 || e.Index >= list.Items.Count) return;
                var en = list.Items[e.Index] as Entry;
                if (en == null) return;
                var activeSet = (searchBox.Text ?? "").Trim().Length > 0 ? searchChecked : baseChecked;
                if (e.NewValue == CheckState.Checked) activeSet.Add(en.Value);
                else activeSet.Remove(en.Value);
            };
            searchBox.TextChanged += (s, e) => rebuildList(true);

            bAll.Click += (s, e) => {
                var activeSet = (searchBox.Text ?? "").Trim().Length > 0 ? searchChecked : baseChecked;
                for (int i = 0; i < list.Items.Count; i++) {
                    var en = list.Items[i] as Entry;
                    if (en != null) activeSet.Add(en.Value);
                }
                rebuildList(false);
            };
            bNone.Click += (s, e) => {
                var activeSet = (searchBox.Text ?? "").Trim().Length > 0 ? searchChecked : baseChecked;
                for (int i = 0; i < list.Items.Count; i++) {
                    var en = list.Items[i] as Entry;
                    if (en != null) activeSet.Remove(en.Value);
                }
                rebuildList(false);
            };
            bInvert.Click += (s, e) => {
                var activeSet = (searchBox.Text ?? "").Trim().Length > 0 ? searchChecked : baseChecked;
                for (int i = 0; i < list.Items.Count; i++) {
                    var en = list.Items[i] as Entry;
                    if (en == null) continue;
                    if (activeSet.Contains(en.Value)) activeSet.Remove(en.Value);
                    else activeSet.Add(en.Value);
                }
                rebuildList(false);
            };
            bOnlyBlank.Click += (s, e) => {
                searchBox.Text = "";
                baseChecked.Clear();
                baseChecked.Add(WorkbenchRules.BlankToken);
                rebuildList(false);
            };

            // 5. 底部按钮行
            var foot = new Panel { Dock = DockStyle.Bottom, Height = Ui.S(40), BackColor = Ui.Bg, Padding = Ui.SPad(0, 8, 0, 0) };
            var bClear = Ui.Small("清除本列"); ((FlatButton)bClear).SetFore(Ui.Muted);
            bClear.Enabled = currentAllowed != null;
            var bCancel = Ui.Secondary("取消", 62, 30);
            var bOk = Ui.Primary("确定", 68, 30);

            Action applyAndClose = () => {
                drop.Close();
                if (onApplyFilter == null) return;
                var chosen = (searchBox.Text ?? "").Trim().Length > 0 ? searchChecked : baseChecked;
                // 如果最终选中了本列全部唯一值，等效于清除本列筛选
                if (chosen.Count >= allEntries.Count) onApplyFilter(null);
                else onApplyFilter(new HashSet<string>(chosen, StringComparer.Ordinal));
            };

            bClear.Click += (s, e) => { drop.Close(); if (onApplyFilter != null) onApplyFilter(null); };
            bCancel.Click += (s, e) => drop.Close();
            bOk.Click += (s, e) => applyAndClose();
            searchBox.KeyDown += (s, e) => {
                if (e.KeyCode == Keys.Enter) { applyAndClose(); e.SuppressKeyPress = true; }
                else if (e.KeyCode == Keys.Escape) { drop.Close(); e.SuppressKeyPress = true; }
            };

            foot.Controls.AddRange(new Control[] { bClear, bCancel, bOk });
            Action layoutFoot = () => {
                int y = Ui.S(8);
                bClear.Location = new Point(0, y + Ui.S(2));
                bOk.Location = new Point(Math.Max(0, foot.Width - bOk.Width), y);
                bCancel.Location = new Point(Math.Max(0, bOk.Left - Ui.S(6) - bCancel.Width), y);
            };
            foot.Resize += (s, e) => layoutFoot();

            // 停靠顺序：Fill 先加，Bottom/Top 后加（逆序布局）
            root.Controls.Add(list);
            root.Controls.Add(foot);
            root.Controls.Add(selBar);
            root.Controls.Add(searchWrap);
            root.Controls.Add(sortBar);
            root.Controls.Add(lblTitle);

            rebuildList(false);

            var host = new ToolStripControlHost(root) { Padding = new Padding(0), Margin = new Padding(0), AutoSize = false, Size = root.Size };
            drop.Items.Add(host);
            drop.Opened += (s, e) => { layoutFoot(); try { searchBox.Focus(); } catch { } };
            // ToolStripDropDown.Close() 只隐藏不释放：关闭后延后一拍 Dispose 掉整棵弹出控件树，避免每次点列筛选泄漏 ~15 个 Win32 USER 句柄
            drop.Closed += (s, e) => {
                Action disposeDrop = () => { try { if (!drop.IsDisposed) drop.Dispose(); } catch { } };
                try {
                    if (anchor != null && !anchor.IsDisposed && anchor.IsHandleCreated) anchor.BeginInvoke(disposeDrop);
                    else disposeDrop();
                } catch { disposeDrop(); }
            };
            drop.Show(screenPoint);
            return drop;
        }
    }
}
