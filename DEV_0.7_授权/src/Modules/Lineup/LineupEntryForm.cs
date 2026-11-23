using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace TianGongCadSuite {
    public sealed partial class LineupTableForm {
        // 可折叠的精简批量录入卡片（默认收起，点「批量录入 ▾」才展开，不再常驻霸占屏幕）
        Panel entryPanel;
        TextBox entryPrefix, entryBrand, entryDescription, entryMechanism;
        ComboBox entryModel, entryParent, entryPosition, entryConnector, entryHome, entryMounting;
        NumericUpDown entryIsland, entryQuantity;
        CheckBox entrySensors;
        Label entryHint;
        readonly Dictionary<string, LineupEntryPreset> entryDrafts = new Dictionary<string, LineupEntryPreset>();
        readonly Timer entryWatcher = new Timer { Interval = 350 };
        string entryKind, watchMode = "", watchFingerprint = "", watchHandled = "", entryAutoBrand = "", entryAutoConnector = "";
        bool entryLoading, watchBusy;
        LineupEntryPreset watchPreset;
        Button watchStop;

        sealed class ParentChoice { public string Id, Text; public override string ToString() { return Text; } }
        static TextBox EntryText() { return new TextBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle }; }
        static ComboBox EntryCombo(string[] items) {
            var c = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown, AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.ListItems };
            if (items != null) c.Items.AddRange(items.Cast<object>().ToArray());
            return c;
        }

        void InitializeEntry() {
            var card = new Card { Fill = true, Height = Ui.S(126), Padding = Ui.SPad(12, 8, 12, 6), Visible = false };
            entryPanel = card;

            var setup = new FlowLayoutPanel { Dock = DockStyle.Top, Height = Ui.S(36), WrapContents = false, AutoScroll = false, BackColor = Ui.Rail };
            entryPrefix = EntryText(); entryPrefix.Width = Ui.S(76); entryPrefix.Text = project.NumberPrefix;
            entryIsland = new NumericUpDown { Width = Ui.S(52), Minimum = 1, Maximum = 9999, Value = Math.Min(9999, Math.Max(1, project.ActiveIsland)), BorderStyle = BorderStyle.FixedSingle };
            entryParent = new ComboBox { Width = Ui.S(156), DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };
            entryQuantity = new NumericUpDown { Width = Ui.S(52), Minimum = 1, Maximum = 500, Value = 1, BorderStyle = BorderStyle.FixedSingle };
            entrySensors = new CheckBox { Text = "带 H/W", AutoSize = true, Margin = Ui.SPad(6, 5, 4, 0), BackColor = Ui.Rail };
            entryPosition = new ComboBox { Width = Ui.S(52), DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };
            entryPosition.Items.AddRange(new[] { "H", "W", "M" });
            entryPosition.SelectedIndex = 0;

            Action<string, Control> add = (label, control) => {
                setup.Controls.Add(new Label { Text = label, AutoSize = true, Margin = Ui.SPad(4, 7, 2, 0), ForeColor = Ui.Text2, Font = Ui.F8, BackColor = Ui.Rail });
                control.Margin = Ui.SPad(0, 3, 4, 0);
                setup.Controls.Add(control);
            };
            add("前缀", entryPrefix);
            add("Y", entryIsland);
            add("上级", entryParent);
            setup.Controls.Add(entrySensors);
            setup.Controls.Add(entryPosition);
            add("数量", entryQuantity);

            var btnJoin = Ui.Primary("加入 / 新增 ↵", 108, 28); btnJoin.Margin = Ui.SPad(6, 1, 4, 0); btnJoin.Click += (s, e) => Try(() => QuickEntry(false));
            var btnReuse = Ui.Small("沿用当前行"); ((FlatButton)btnReuse).SetFore(Ui.Text2); btnReuse.Margin = Ui.SPad(2, 2, 2, 0); btnReuse.Click += (s, e) => Try(UseCurrentEntry);
            var btnClear = Ui.Small("清空"); ((FlatButton)btnClear).SetFore(Ui.Muted); btnClear.Margin = Ui.SPad(0, 2, 0, 0); btnClear.Click += (s, e) => Try(() => WriteEntry(new LineupEntryPreset { Category = entryKind }));
            setup.Controls.AddRange(new Control[] { btnJoin, btnReuse, btnClear });
            setup.Resize += (s, e) => {
                // 窗口收窄时温和压缩前缀与上级下拉宽度，确保右侧「加入 / 沿用 / 清空」永不被裁切
                int w = setup.ClientSize.Width;
                if (w <= 0) return;
                entryParent.Width = w < Ui.S(860) ? Ui.S(102) : (w < Ui.S(980) ? Ui.S(126) : Ui.S(156));
                entryPrefix.Width = w < Ui.S(860) ? Ui.S(60) : Ui.S(76);
            };

            var fields = new TableLayoutPanel { Dock = DockStyle.Top, Height = Ui.S(60), ColumnCount = 8, RowCount = 2, BackColor = Ui.Rail };
            int lblW = Ui.S(40);
            for (int i = 0; i < 4; i++) {
                fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, lblW));
                fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, i == 0 ? 32 : 22.6f));
            }
            entryModel = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown, AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.ListItems };
            entryBrand = EntryText();
            entryConnector = EntryCombo(LineupPresets.Connectors);
            entryDescription = EntryText();
            entryMechanism = EntryText();
            entryHome = EntryCombo(LineupPresets.Homes);
            entryMounting = EntryCombo(LineupPresets.CylinderMountings);

            Action<int, int, string, Control> field = (row, col, label, control) => {
                control.Margin = Ui.SPad(0, 2, 8, 2);
                fields.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Ui.Text2, Font = Ui.F8, BackColor = Ui.Rail }, col * 2, row);
                fields.Controls.Add(control, col * 2 + 1, row);
            };
            field(0, 0, "型号", entryModel); field(0, 1, "品牌", entryBrand); field(0, 2, "接口", entryConnector); field(0, 3, "机构", entryMechanism);
            field(1, 0, "描述", entryDescription); field(1, 1, "原位", entryHome); field(1, 2, "安装", entryMounting);

            entryHint = new Label {
                Dock = DockStyle.Bottom, Height = Ui.S(18), Font = Ui.F8, ForeColor = Ui.Muted, BackColor = Ui.Rail,
                Text = "共用字段按类目记忆；按 Enter 立即加入，按 F4 选型号库。", AutoEllipsis = true
            };

            entryPanel.Controls.Add(fields);
            entryPanel.Controls.Add(setup);
            entryPanel.Controls.Add(entryHint);

            listStack.Add(entryPanel, false);
            listStack.Controls.SetChildIndex(entryPanel, listStack.Controls.IndexOf(workAreaHost));

            entryModel.TextChanged += (s, e) => CompleteEntryModel();
            category.SelectedIndexChanged += (s, e) => ChangeEntryCategory();
            foreach (Control control in EntryControls(entryPanel)) {
                control.KeyDown += (s, e) => {
                    if (e.KeyCode == Keys.Enter && !(control is Button) && !(control is ComboBox && ((ComboBox)control).DroppedDown)) {
                        e.Handled = true;
                        e.SuppressKeyPress = true;
                        Try(() => QuickEntry(false));
                    }
                };
            }
            entryWatcher.Tick += (s, e) => PollContinuous();
            FormClosing += (s, e) => StopContinuous();
            FormClosed += (s, e) => entryWatcher.Dispose();
            KeyDown += (s, e) => {
                if (e.KeyCode == Keys.Escape && watchMode.Length > 0) { e.SuppressKeyPress = true; StopContinuous(); }
                if (e.Control && e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Try(() => QuickEntry(false)); }
                if (e.Control && e.KeyCode == Keys.D && grid.ContainsFocus) { e.SuppressKeyPress = true; Try(FillDown); }
            };
            ChangeEntryCategory();
        }

        static IEnumerable<Control> EntryControls(Control root) {
            foreach (Control c in root.Controls) {
                yield return c;
                foreach (var nested in EntryControls(c)) yield return nested;
            }
        }

        LineupEntryPreset ReadEntry() {
            return new LineupEntryPreset {
                Category = entryKind ?? ActiveCategory,
                Model = entryModel.Text, Brand = entryBrand.Text, Connector = entryConnector.Text,
                Description = entryDescription.Text, Mechanism = entryMechanism.Text, Home = entryHome.Text, Mounting = entryMounting.Text,
                ParentId = entryParent.SelectedItem is ParentChoice ? ((ParentChoice)entryParent.SelectedItem).Id : "",
                Position = entryPosition.Text, GenerateSensors = entrySensors.Checked
            };
        }

        void ChangeEntryCategory() {
            if (entryPanel == null) return;
            if (entryKind != null) entryDrafts[entryKind] = ReadEntry();
            entryKind = ActiveCategory;
            LineupEntryPreset preset;
            if (!entryDrafts.TryGetValue(entryKind, out preset))
                preset = project.EntryPresets.LastOrDefault(x => x.Category == entryKind) ?? new LineupEntryPreset { Category = entryKind };
            WriteEntry(preset);
        }

        internal void WriteEntry(LineupEntryPreset preset) {
            entryLoading = true;
            entryKind = preset.Category;
            entryModel.Items.Clear();
            entryModel.Items.AddRange(catalog.Entries.Where(x => x.Category == entryKind).Select(x => x.Model).Distinct().OrderBy(x => x).Cast<object>().ToArray());
            entryMounting.Items.Clear();
            entryMounting.Items.AddRange((entryKind == "阀片" ? LineupPresets.ValveKinds : LineupPresets.CylinderMountings).Cast<object>().ToArray());
            entryModel.Text = preset.Model; entryBrand.Text = preset.Brand; entryConnector.Text = preset.Connector;
            entryDescription.Text = preset.Description; entryMechanism.Text = preset.Mechanism;
            entryHome.Text = preset.Home; entryMounting.Text = preset.Mounting;
            entrySensors.Checked = preset.GenerateSensors; entryPosition.SelectedItem = preset.Position;
            entryAutoBrand = ""; entryAutoConnector = "";
            RefreshEntryParents(preset.ParentId);
            entrySensors.Visible = entryKind == "气缸";
            entryPosition.Visible = entryKind == "气缸传感器";
            entryQuantity.Enabled = entryKind != "气缸传感器";
            entryLoading = false;
            CompleteEntryModel();
        }

        void RefreshEntryParents(string preferred = null) {
            if (entryParent == null) return;
            string id = preferred ?? (entryParent.SelectedItem is ParentChoice ? ((ParentChoice)entryParent.SelectedItem).Id : "");
            string kind = entryKind == "气缸" ? "阀片" : entryKind == "气缸传感器" ? "气缸" : "";
            entryParent.Items.Clear();
            entryParent.Items.Add(new ParentChoice { Id = "", Text = kind.Length == 0 ? "自动处理" : "选择" + kind });
            foreach (var r in project.Records.Where(x => kind.Length > 0 && x.Category == kind))
                entryParent.Items.Add(new ParentChoice { Id = r.Id, Text = r.Number + " · " + r.Description });
            entryParent.SelectedIndex = 0;
            for (int i = 1; i < entryParent.Items.Count; i++) if (((ParentChoice)entryParent.Items[i]).Id == id) entryParent.SelectedIndex = i;
            if (id.Length > 0 && entryParent.SelectedIndex == 0) {
                entryParent.Items.Add(new ParentChoice { Id = id, Text = "上级已失效，请重选" });
                entryParent.SelectedIndex = entryParent.Items.Count - 1;
            }
            entryParent.Enabled = kind.Length > 0;
            entryParent.AutoCompleteSource = AutoCompleteSource.ListItems;
            entryParent.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
        }

        void CompleteEntryModel() {
            if (entryLoading || entryModel == null) return;
            if (entryAutoBrand.Length > 0 && entryBrand.Text == entryAutoBrand) entryBrand.Clear();
            if (entryAutoConnector.Length > 0 && entryConnector.Text == entryAutoConnector) entryConnector.Text = "";
            entryAutoBrand = ""; entryAutoConnector = "";
            var matches = catalog.Match(entryKind, entryModel.Text).ToList();
            if (matches.Count == 1) {
                if (entryBrand.Text.Length == 0) { entryAutoBrand = matches[0].Brand; entryBrand.Text = entryAutoBrand; }
                if (entryConnector.Text.Length == 0) { entryAutoConnector = matches[0].Connector; entryConnector.Text = entryAutoConnector; }
            }
            entryHint.Text = matches.Count > 1
                ? "此型号有多种品牌/接口，请按 F4 选定规格。"
                : "共用字段按类目记忆；按 Enter 立即加入，按 F4 选型号库。";
        }

        void UseCurrentEntry() {
            var r = Snapshot().Records.FirstOrDefault(x => x.Id == CurrentId());
            if (r == null) throw new InvalidOperationException("请先在表中选中要沿用的行。");
            category.SelectedItem = r.Category;
            WriteEntry(new LineupEntryPreset {
                Category = r.Category, Model = r.Model, Brand = r.Brand, Connector = r.Connector,
                Description = r.Description, Mechanism = r.Mechanism, Home = r.Home, Mounting = r.Mounting,
                ParentId = r.Category == "气缸" ? r.ValveId : r.Category == "气缸传感器" ? r.CylinderId : "",
                Position = string.IsNullOrEmpty(r.Position) ? "H" : r.Position
            });
            entryHint.Text = "已沿用当前行共用字段；新增将自动递增编号。";
        }

        void PickEntryModel() {
            if (catalogError.Length > 0) throw new InvalidOperationException(catalogError);
            using (var dialog = new Form()) {
                Ui.Shell(dialog, "选择本批录入型号 · " + entryKind, 620, 400, 460, 320);
                var body = new Panel { Dock = DockStyle.Fill, Padding = Ui.SPad(16, 12, 16, 8), BackColor = Ui.Bg };
                var query = new TextBox { Dock = DockStyle.Top, BorderStyle = BorderStyle.FixedSingle };
                var gap = new Panel { Dock = DockStyle.Top, Height = Ui.S(8), BackColor = Ui.Bg };
                var list = new ListBox { Dock = DockStyle.Fill, HorizontalScrollbar = true, BorderStyle = BorderStyle.FixedSingle };
                Action refresh = () => {
                    list.Items.Clear();
                    list.Items.AddRange(catalog.Entries.Where(x => x.Category == entryKind && x.ToString().IndexOf(query.Text, StringComparison.OrdinalIgnoreCase) >= 0).Cast<object>().ToArray());
                    if (list.Items.Count > 0) list.SelectedIndex = 0;
                };
                query.TextChanged += (s, e) => refresh();
                refresh();
                list.DoubleClick += (s, e) => { if (list.SelectedItem != null) dialog.DialogResult = DialogResult.OK; };
                body.Controls.Add(list); body.Controls.Add(gap); body.Controls.Add(query);

                var footer = new ActionBar { Dock = DockStyle.Bottom, Height = Ui.S(56), Padding = Ui.SPad(16, 0, 16, 0) };
                var ok = Ui.Primary("用于后续录入", 122, 34); ok.DialogResult = DialogResult.OK;
                var cancel = Ui.Secondary("取消", 76, 34); cancel.DialogResult = DialogResult.Cancel;
                footer.AddRight(cancel); footer.AddRight(ok);
                dialog.AcceptButton = ok; dialog.CancelButton = cancel;
                dialog.Controls.Add(body); dialog.Controls.Add(footer);
                if (dialog.ShowDialog(this) != DialogResult.OK || list.SelectedItem == null) return;
                var preset = ReadEntry();
                var model = (LineupModelEntry)list.SelectedItem;
                preset.Model = model.Model; preset.Brand = model.Brand; preset.Connector = model.Connector;
                WriteEntry(preset);
            }
        }

        LineupProject EntrySnapshot() {
            var next = Snapshot();
            string prefix = entryPrefix.Text.Trim();
            int island = (int)entryIsland.Value;
            if (prefix.Length > 0) {
                string key = prefix + "-Y" + island;
                var configured = next.Islands.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
                LineupNumbering.SetIsland(next, prefix, island, configured == null ? "" : configured.Model, configured == null ? "阀岛" : configured.Kind);
            } else {
                next.NumberPrefix = "";
                next.ActiveIsland = island;
            }
            return next;
        }

        List<LineupRecord> CaptureEntrySelections() {
            var incoming = new List<LineupRecord>();
            if (assembly == null) return incoming;
            int count = assembly.SelectSet.Count;
            for (int i = 1; i <= count; i++) {
                var row = new LineupRecord();
                LineupCad.CaptureOccurrence(assembly.SelectSet.Item(i), row);
                try {
                    var meta = LineupCad.ReadPartMeta(row.SourceFile, row.InstancePath, catalog, entryKind ?? ActiveCategory);
                    if (!string.IsNullOrWhiteSpace(meta.Model)) row.Model = meta.Model;
                    if (!string.IsNullOrWhiteSpace(meta.Brand)) row.Brand = meta.Brand;
                    if (!string.IsNullOrWhiteSpace(meta.Connector)) row.Connector = meta.Connector;
                    if (!string.IsNullOrWhiteSpace(meta.Title)) row.Description = meta.Title;
                    if (!string.IsNullOrWhiteSpace(meta.Mechanism)) row.Mechanism = meta.Mechanism;
                } catch { }
                incoming.Add(row);
            }
            if (assembly.SelectSet.Count != count) throw new InvalidOperationException("选择正在变化，请完成选择后重试。");
            return incoming;
        }

        internal int QuickEntry(bool requireSelection) {
            Ensure();
            var incoming = CaptureEntrySelections();
            if (requireSelection && incoming.Count == 0) return 0;
            return CommitEntry(incoming, watchMode == "add" ? watchPreset : ReadEntry(), requireSelection || entryKind == "气缸传感器" ? 1 : (int)entryQuantity.Value);
        }

        int CommitEntry(List<LineupRecord> incoming, LineupEntryPreset preset, int quantity) {
            var result = LineupEntry.Create(EntrySnapshot(), preset, quantity, incoming);
            if (result.AddedIds.Count == 0) {
                UpdateStatus("所选模型均已有记录，未重复录入");
                if (pickingMode) pickingLabel.Text = "所选模型已有记录，请点下一个模型。";
                return 0;
            }
            string message = "已录入 " + result.AddedIds.Count + " 条"
                + (result.SensorCount > 0 ? "（含 " + result.SensorCount + " 条 H/W）" : "")
                + (result.Skipped > 0 ? "，跳过 " + result.Skipped + " 个重复件" : "");
            search.Clear();
            category.SelectedItem = preset.Category;
            Commit(result.Project, result.AddedIds.Last(), message);
            entryDrafts[preset.Category] = preset.Copy();
            RefreshEntryParents(preset.ParentId);
            if (incoming.Count > 0 && assembly != null) {
                try { assembly.SelectSet.RemoveAll(); } catch { message += "；请取消原选择后再点下一个"; }
            }
            if (pickingMode) pickingLabel.Text = message + "\n继续在 CAD 中点选下一个零件，或点「停止连续」返回。";
            return result.AddedIds.Count;
        }

        internal bool ContinuousActive { get { return watchMode.Length > 0; } }

        internal void StartContinuous(string mode) {
            Ensure();
            if (assembly == null) throw new InvalidOperationException("当前未连接天工 CAD 装配。");
            SaveDraft();
            if (mode != "add" && mode != "bind") throw new ArgumentException("Unknown continuous mode");
            if (mode == "add") {
                if (!ReadyNumbering(ActiveCategory)) return;
                watchPreset = ReadEntry();
                LineupEntry.Create(EntrySnapshot(), watchPreset, 1, null);
            } else {
                var current = CurrentId();
                var r = project.Records.FirstOrDefault(x => x.Id == current);
                if (r == null || !string.IsNullOrEmpty(r.ReferenceKey)) {
                    current = VisibleEntryIds().FirstOrDefault(id => project.Records.Any(x => x.Id == id && string.IsNullOrEmpty(x.ReferenceKey)));
                    if (current == null) throw new InvalidOperationException("当前列表里没有尚未绑定模型的行。");
                    SelectId(current);
                }
            }
            watchMode = mode;
            watchFingerprint = "";
            watchHandled = "";
            SetPickingMode(true);
            watchStop.Visible = true;
            SetContinuousControls(true);
            if (pickingTitle != null) pickingTitle.Text = mode == "add" ? "● 连续新增中 · 在 CAD 里点一个就录一个" : "● 连续绑定中 · 逐个点选对应三维件";
            pickingLabel.Text = mode == "add"
                ? "当前类目：" + watchPreset.Category + (string.IsNullOrWhiteSpace(watchPreset.Model) ? "（自动识件）" : " · " + watchPreset.Model) + "\n直接在 CAD 里点击零件，自动编号并保存。"
                : "待绑定行：" + project.Records.First(r => r.Id == CurrentId()).Number + "\n每次点选一个零件，绑完自动跳下一行。";
            entryWatcher.Start();
        }

        List<string> VisibleEntryIds() {
            return grid.Rows.Cast<DataGridViewRow>().Select(r => Convert.ToString(((DataRowView)r.DataBoundItem)["Id"])).ToList();
        }

        void SetContinuousControls(bool active) {
            if (pickingPanel == null) return;
            foreach (var button in EntryControls(pickingPanel).OfType<Button>())
                if (button.Text == "绑定并下一条" || button.Text == "另建记录") { button.Enabled = !active; button.Visible = !active; }
        }

        internal void StopContinuous() {
            watchMode = "";
            entryWatcher.Stop();
            watchFingerprint = "";
            watchHandled = "";
            if (watchStop != null) watchStop.Visible = false;
            SetContinuousControls(false);
            RefreshPickingLabel();
        }

        internal void PollContinuous() {
            if (watchMode.Length == 0 || watchBusy || IsDisposed) return;
            watchBusy = true;
            try {
                Ensure();
                var incoming = CaptureEntrySelections();
                if (incoming.Count == 0) { watchFingerprint = ""; watchHandled = ""; return; }
                string fingerprint = string.Join("|", incoming.Select(r => r.ReferenceKey).OrderBy(x => x));
                if (fingerprint != watchFingerprint) { watchFingerprint = fingerprint; return; }
                if (fingerprint == watchHandled) return;
                watchHandled = fingerprint;
                if (watchMode == "add") CommitEntry(incoming, watchPreset, 1);
                else {
                    if (incoming.Count != 1) throw new InvalidOperationException("连续绑定每次只选一个模型；当前行保持不变。");
                    string id = CurrentId();
                    var next = Snapshot();
                    var record = next.Records.Single(r => r.Id == id);
                    var conflict = next.Records.FirstOrDefault(r => r.ReferenceKey == incoming[0].ReferenceKey);
                    if (conflict != null) throw new InvalidOperationException("该模型已属于 " + conflict.Number + "；待绑定 " + record.Number + " 保持不变。");
                    if (!string.IsNullOrEmpty(record.ReferenceKey)) throw new InvalidOperationException("当前行已绑定，请返回表格选择其他待绑定行。");
                    record.ReferenceKey = incoming[0].ReferenceKey;
                    record.SourceFile = incoming[0].SourceFile;
                    record.InstancePath = incoming[0].InstancePath;
                    string following = LineupEntry.NextUnbound(next, VisibleEntryIds(), id);
                    Commit(next, following ?? id, "已绑定 " + record.Number);
                    try { assembly.SelectSet.RemoveAll(); } catch { }
                    if (following == null) {
                        StopContinuous();
                        pickingLabel.Text = "所有待绑定行已完成！可点「返回表格」查看。";
                    } else {
                        pickingLabel.Text = "已绑定 " + record.Number + "\n下一条：" + next.Records.Single(r => r.Id == following).Number + "，请点选对应零件。";
                    }
                }
            } catch (System.Runtime.InteropServices.COMException) {
                pickingLabel.Text = "CAD 正忙，等待恢复；当前记录保持。";
                watchFingerprint = ""; watchHandled = "";
            } catch (Exception ex) {
                pickingLabel.Text = ex.Message;
                status.Text = ex.Message;
            } finally { watchBusy = false; }
        }
    }
}
