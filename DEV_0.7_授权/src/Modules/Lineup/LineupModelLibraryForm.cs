using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TianGongCadSuite {
    // 分类化型号库管理与选型一体化窗口：
    //   · 左侧两级分类导航（大类：气缸 / 阀片 / 阀岛 / 气缸传感器 / 普通传感器 / 其他电气件 → 细分类型），彻底告别扁平混堆；
    //   · 右侧全功能维护工具栏与双缓冲表格：支持新增、编辑、复制新增、批量改分类、删除、Excel/CSV/剪贴板导入、按分类导出及定位本地库文件；
    //   · 同时承担「选型模式」（双击型号列 / F4 / 批量套用）与「管理模式」（顶栏「型号库」按钮直达），所有下拉菜单均支持二次点击收起。
    public sealed class LineupModelLibraryForm : Form {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);
        const int EmSetCueBanner = 0x1501;

        readonly LineupModelCatalog catalog;
        readonly string catalogFilePath;
        readonly bool pickMode;
        readonly string targetCategory;
        readonly int applyTargetCount;
        readonly string primaryActionText;
        readonly Action<LineupModelCatalog> onPersistCatalog;
        readonly Func<int> onLearnCurrentProject;
        readonly Func<LineupModelCatalog> onLoadSeed;

        readonly SmoothListBox navList = new SmoothListBox();
        readonly DataGridView grid = new DataGridView();
        readonly TextBox searchBox = new TextBox { BorderStyle = BorderStyle.FixedSingle };
        readonly Button searchClearBtn;
        readonly Label scopeLabel = new Label();
        readonly Label footerStatus = new Label();
        readonly Button btnAdd, btnEdit, btnRecat, btnDelete, btnImport, btnExport;
        readonly Button btnApply, btnClose;
        readonly ContextMenuStrip importMenu = Ui.Menu();
        readonly ContextMenuStrip exportMenu = Ui.Menu();
        readonly ContextMenuStrip rowMenu = Ui.Menu();
        readonly ToolTip tips = new ToolTip { AutoPopDelay = 10000, InitialDelay = 250 };

        readonly HashSet<string> expandedCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly List<NavNode> navNodes = new List<NavNode>();
        List<LineupModelEntry> visibleEntries = new List<LineupModelEntry>();

        string selectedCategory = "";     // "" 表示全部型号
        string selectedSubCategory = "";  // "" 表示该大类下全部细分类型
        bool syncingNav;

        public LineupModelEntry SelectedEntry { get; private set; }
        public bool CatalogChanged { get; private set; }

        internal DataGridView Grid { get { return grid; } }
        internal ListBox NavList { get { return navList; } }
        internal Button ImportButton { get { return btnImport; } }
        internal ContextMenuStrip ImportMenu { get { return importMenu; } }
        internal Button ExportButton { get { return btnExport; } }
        internal ContextMenuStrip ExportMenu { get { return exportMenu; } }
        internal IList<LineupModelEntry> VisibleEntriesForTest { get { return visibleEntries; } }
        internal void SelectCategoryForTest(string cat, string sub = "") {
            selectedCategory = cat ?? "";
            selectedSubCategory = sub ?? "";
            if (selectedCategory.Length > 0) expandedCategories.Add(selectedCategory);
            RebuildNavAndGrid(null);
        }

        sealed class NavNode {
            public bool IsAll;
            public bool IsCategory;
            public bool IsSeparator;
            public string Category = "";
            public string SubCategory = "";
            public string Title = "";
            public int Count;
            public bool Expanded;
        }

        public LineupModelLibraryForm(
            LineupModelCatalog modelCatalog,
            string filePath,
            bool isPickMode,
            string initialCategory,
            int targetRowCount,
            string applyButtonLabel,
            Action<LineupModelCatalog> persistCatalog,
            Func<int> learnCurrentProject,
            Func<LineupModelCatalog> loadSeed) {

            catalog = modelCatalog ?? new LineupModelCatalog();
            catalogFilePath = filePath ?? "";
            pickMode = isPickMode;
            targetCategory = string.IsNullOrWhiteSpace(initialCategory) ? "" : LineupTableData.Category(initialCategory.Trim());
            applyTargetCount = Math.Max(1, targetRowCount);
            primaryActionText = string.IsNullOrWhiteSpace(applyButtonLabel)
                ? (applyTargetCount > 1 ? "批量套用 (" + applyTargetCount + ")" : "套用选中型号")
                : applyButtonLabel;
            onPersistCatalog = persistCatalog;
            onLearnCurrentProject = learnCurrentProject;
            onLoadSeed = loadSeed;

            catalog.EnsureSubCategories();

            string title = pickMode
                ? ("型号库选型 · " + (targetCategory.Length > 0 ? targetCategory : "全部") + (applyTargetCount > 1 ? "（批量套用 " + applyTargetCount + " 项）" : ""))
                : "Lineup 型号库管理";
            Ui.Shell(this, title, 980, 620, 740, 460);
            FormBorderStyle = FormBorderStyle.Sizable;

            if (targetCategory.Length > 0) {
                selectedCategory = targetCategory;
                expandedCategories.Add(targetCategory);
            } else {
                selectedCategory = "";
                expandedCategories.Add("气缸");
                expandedCategories.Add("普通传感器");
            }

            btnAdd = Ui.Primary("+ 新增型号", 104, 30); btnAdd.Font = Ui.F8;
            btnEdit = Ui.Secondary("编辑", 62, 30); btnEdit.Font = Ui.F8;
            btnRecat = Ui.Secondary("批量改分类", 92, 30); btnRecat.Font = Ui.F8;
            btnDelete = Ui.Secondary("删除", 62, 30); btnDelete.Font = Ui.F8;
            ((FlatButton)btnDelete).SetFore(Ui.Bad);
            btnImport = Ui.Secondary("导入 ▾", 78, 30); btnImport.Font = Ui.F8;
            btnExport = Ui.Secondary("导出 ▾", 78, 30); btnExport.Font = Ui.F8;
            searchClearBtn = Ui.Small("×");
            ((FlatButton)searchClearBtn).SetFore(Ui.Muted);

            btnApply = Ui.Primary(primaryActionText, 128, 34);
            btnClose = Ui.Secondary(pickMode ? "取消" : "关闭", 76, 34);

            BuildMenus();
            BuildLayout();
            SetupGrid();
            SetupNavList();
            BindEvents();

            RebuildNavAndGrid(null);
            UpdateFooterStatus("");
        }

        protected override void Dispose(bool disposing) {
            if (disposing) {
                importMenu.Dispose();
                exportMenu.Dispose();
                rowMenu.Dispose();
                tips.Dispose();
            }
            base.Dispose(disposing);
        }

        static Color CategoryDotColor(string cat) {
            if (cat == "气缸") return Ui.Accent;
            if (cat == "阀片") return Color.FromArgb(0x02, 0x84, 0xC7);
            if (cat == "阀岛") return Color.FromArgb(0x7C, 0x3A, 0xED);
            if (cat == "气缸传感器") return Color.FromArgb(0x05, 0x96, 0x69);
            if (cat == "普通传感器") return Color.FromArgb(0x0D, 0x94, 0x88);
            if (cat == "其他电气件") return Color.FromArgb(0xD9, 0x77, 0x06);
            return Ui.Muted;
        }

        void BuildMenus() {
            Action<ContextMenuStrip, string, Action, string> add = (m, t, act, tone) => {
                var mi = new ToolStripMenuItem(t);
                if (!string.IsNullOrEmpty(tone)) mi.Tag = tone;
                mi.Click += (s, e) => Try(act);
                m.Items.Add(mi);
            };

            add(importMenu, "从文件导入（Excel / CSV / TSV / XML）…", ImportFromFile, null);
            if (onLearnCurrentProject != null)
                add(importMenu, "从当前 Lineup 清单收录全部型号", LearnFromProject, null);
            add(importMenu, "从剪贴板粘贴型号表（支持 Excel 复制）", ImportFromClipboard, null);
            if (onLoadSeed != null) {
                importMenu.Items.Add(new ToolStripSeparator());
                add(importMenu, "补充缺失的内置默认型号", MergeMissingSeed, null);
            }

            add(exportMenu, "导出全部型号库（按分类排序 CSV）…", () => ExportCsv(false), null);
            add(exportMenu, "仅导出当前分类（CSV）…", () => ExportCsv(true), null);
            if (!string.IsNullOrEmpty(catalogFilePath)) {
                exportMenu.Items.Add(new ToolStripSeparator());
                add(exportMenu, "打开本地型号库文件位置", OpenCatalogFolder, null);
            }
        }

        void BuildLayout() {
            var body = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Bg, Padding = Ui.SPad(14, 12, 14, 8) };

            // ---- 左侧分类导航卡片 ----
            var leftCard = new Card { Fill = true, Dock = DockStyle.Left, Width = Ui.S(236), Padding = Ui.SPad(8, 8, 8, 8) };
            var leftHdr = new Label {
                Text = "型号分类",
                Dock = DockStyle.Top, Height = Ui.S(28), Font = Ui.F9B, ForeColor = Ui.Text, BackColor = Ui.Rail,
                TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Padding = Ui.SPad(4, 0, 0, 0)
            };
            var leftHint = new Label {
                Text = "点大类展开细分类型 · 点击即筛选",
                Dock = DockStyle.Bottom, Height = Ui.S(22), Font = Ui.F8, ForeColor = Ui.Muted, BackColor = Ui.Rail,
                TextAlign = ContentAlignment.MiddleLeft, Padding = Ui.SPad(4, 2, 0, 0)
            };
            leftCard.Controls.Add(navList);
            leftCard.Controls.Add(leftHdr);
            leftCard.Controls.Add(leftHint);

            var split = new Splitter { Dock = DockStyle.Left, Width = Ui.S(10), MinSize = Ui.S(180), MinExtra = Ui.S(460), BackColor = Ui.Bg };

            // ---- 右侧主区：工具栏 + 搜索条 + 表格 ----
            var rightHost = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Bg };

            var toolbar = new FlowLayoutPanel {
                Dock = DockStyle.Top, Height = Ui.S(38), WrapContents = false, BackColor = Ui.Bg, Padding = Ui.SPad(0, 2, 0, 4)
            };
            btnAdd.Margin = Ui.SPad(0, 0, 6, 0);
            btnEdit.Margin = Ui.SPad(0, 0, 6, 0);
            btnRecat.Margin = Ui.SPad(0, 0, 6, 0);
            btnDelete.Margin = Ui.SPad(0, 0, 10, 0);
            btnImport.Margin = Ui.SPad(0, 0, 6, 0);
            btnExport.Margin = new Padding(0);
            toolbar.Controls.AddRange(new Control[] { btnAdd, btnEdit, btnRecat, btnDelete, btnImport, btnExport });

            tips.SetToolTip(btnAdd, "新增一条型号记录（Insert / Ctrl+N），默认归入当前选中分类");
            tips.SetToolTip(btnEdit, "编辑选中型号的大类、细分类型、型号、品牌、接口与备注");
            tips.SetToolTip(btnRecat, "将选中的 1 条或多条型号一键归入指定大类与细分类型");
            tips.SetToolTip(btnDelete, "从本机型号库删除选中条目（永久生效，不再被内置库覆盖）");
            tips.SetToolTip(btnImport, "从 Excel/CSV/剪贴板导入型号，或补充内置默认型号");
            tips.SetToolTip(btnExport, "按分类导出 CSV 共享给同事，或打开本机 XML 文件位置");

            var searchRow = new Panel { Dock = DockStyle.Top, Height = Ui.S(36), BackColor = Ui.Bg, Padding = Ui.SPad(0, 2, 0, 6) };
            var searchWrap = new Panel { Dock = DockStyle.Left, Width = Ui.S(300), BackColor = Ui.Bg };
            searchBox.Dock = DockStyle.Fill;
            searchBox.Font = Ui.F9;
            searchBox.HandleCreated += (s, e) => { try { SendMessage(searchBox.Handle, EmSetCueBanner, (IntPtr)1, "搜索型号、品牌、细分类型、接口或备注…"); } catch { } };
            searchClearBtn.Dock = DockStyle.Right;
            searchClearBtn.Width = Ui.S(26);
            searchClearBtn.Visible = false;
            searchWrap.Controls.Add(searchBox);
            searchWrap.Controls.Add(searchClearBtn);

            scopeLabel.Dock = DockStyle.Fill;
            scopeLabel.Font = Ui.F8;
            scopeLabel.ForeColor = Ui.Muted;
            scopeLabel.TextAlign = ContentAlignment.MiddleRight;
            scopeLabel.AutoEllipsis = true;
            searchRow.Controls.Add(scopeLabel);
            searchRow.Controls.Add(searchWrap);

            var gridCard = new Card { Fill = false, Dock = DockStyle.Fill, Padding = Ui.SPad(2, 2, 2, 2) };
            gridCard.Controls.Add(grid);

            rightHost.Controls.Add(gridCard);
            rightHost.Controls.Add(searchRow);
            rightHost.Controls.Add(toolbar);

            body.Controls.Add(rightHost);
            body.Controls.Add(split);
            body.Controls.Add(leftCard);

            // ---- 底部操作栏 ----
            var footer = new ActionBar { Dock = DockStyle.Bottom, Height = Ui.S(56), Padding = Ui.SPad(14, 0, 14, 0) };
            footerStatus.ForeColor = Ui.Muted;
            footer.Status = footerStatus;
            btnClose.DialogResult = DialogResult.Cancel;
            footer.AddRight(btnClose);
            if (pickMode) {
                footer.AddRight(btnApply);
                AcceptButton = btnApply;
            }
            CancelButton = btnClose;

            Controls.Add(body);
            Controls.Add(footer);

            Ui.EnableDoubleBuffer(body);
            Ui.EnableDoubleBuffer(rightHost);
        }

        void SetupNavList() {
            navList.Dock = DockStyle.Fill;
            navList.DrawMode = DrawMode.OwnerDrawFixed;
            navList.ItemHeight = Ui.S(30);
            navList.BorderStyle = BorderStyle.None;
            navList.BackColor = Ui.Rail;
            navList.IntegralHeight = false;
            navList.DrawItem += DrawNavItem;
            navList.MouseDown += OnNavMouseDown;
            navList.SelectedIndexChanged += (s, e) => {
                if (syncingNav) return;
                int idx = navList.SelectedIndex;
                if (idx < 0 || idx >= navNodes.Count) return;
                var node = navNodes[idx];
                if (node.IsSeparator) return;
                if (node.IsAll) {
                    selectedCategory = "";
                    selectedSubCategory = "";
                } else if (node.IsCategory) {
                    selectedCategory = node.Category;
                    selectedSubCategory = "";
                    if (!expandedCategories.Contains(node.Category)) {
                        expandedCategories.Add(node.Category);
                        RebuildNavAndGrid(null);
                        return;
                    }
                } else {
                    selectedCategory = node.Category;
                    selectedSubCategory = node.SubCategory;
                }
                RefreshGrid(null);
            };
            navList.DoubleClick += (s, e) => {
                int idx = navList.SelectedIndex;
                if (idx < 0 || idx >= navNodes.Count) return;
                var node = navNodes[idx];
                if (!node.IsCategory) return;
                ToggleCategoryExpanded(node.Category);
            };
        }

        void OnNavMouseDown(object sender, MouseEventArgs e) {
            if (e.Button != MouseButtons.Left) return;
            int idx = navList.IndexFromPoint(e.Location);
            if (idx < 0 || idx >= navNodes.Count) return;
            var node = navNodes[idx];
            if (node.IsCategory && e.X <= Ui.S(24)) {
                ToggleCategoryExpanded(node.Category);
            }
        }

        void ToggleCategoryExpanded(string cat) {
            if (expandedCategories.Contains(cat)) expandedCategories.Remove(cat);
            else expandedCategories.Add(cat);
            RebuildNavAndGrid(CurrentEntry());
        }

        void DrawNavItem(object sender, DrawItemEventArgs e) {
            if (e.Index < 0 || e.Index >= navNodes.Count) return;
            var g = e.Graphics;
            var node = navNodes[e.Index];
            var bounds = e.Bounds;
            g.FillRectangle(Ui.CachedBrush(Ui.Rail), bounds);

            if (node.IsSeparator) {
                int y = bounds.Top + bounds.Height / 2;
                g.DrawLine(Ui.CachedPen(Ui.Line, 1f), bounds.Left + Ui.S(6), y, bounds.Right - Ui.S(6), y);
                if (!string.IsNullOrEmpty(node.Title)) {
                    var sz = TextRenderer.MeasureText(node.Title, Ui.F8);
                    var pill = new Rectangle(bounds.Left + Ui.S(8), bounds.Top + Ui.S(5), sz.Width + Ui.S(8), bounds.Height - Ui.S(10));
                    g.FillRectangle(Ui.CachedBrush(Ui.Rail), pill);
                    TextRenderer.DrawText(g, node.Title, Ui.F8, pill, Ui.Faint, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                }
                return;
            }

            bool active = (!node.IsAll && !node.IsCategory && string.Equals(selectedCategory, node.Category, StringComparison.OrdinalIgnoreCase) && string.Equals(selectedSubCategory, node.SubCategory, StringComparison.OrdinalIgnoreCase))
                || (node.IsCategory && string.Equals(selectedCategory, node.Category, StringComparison.OrdinalIgnoreCase) && selectedSubCategory.Length == 0)
                || (node.IsAll && selectedCategory.Length == 0);

            var rowRect = new Rectangle(bounds.X + Ui.S(2), bounds.Y + Ui.S(2), Math.Max(4, bounds.Width - Ui.S(4)), Math.Max(4, bounds.Height - Ui.S(4)));
            if (active) {
                Ui.FillRound(g, rowRect, Ui.S(6), Color.White);
                Ui.DrawRound(g, rowRect, Ui.S(6), Ui.AccentRing, 1f);
                g.FillRectangle(Ui.CachedBrush(Ui.Accent), rowRect.X + 1, rowRect.Y + Ui.S(5), Ui.S(3), Math.Max(4, rowRect.Height - Ui.S(10)));
            }

            string countTxt = node.Count.ToString();
            int countW = Math.Max(Ui.S(26), TextRenderer.MeasureText(countTxt, Ui.F8).Width + Ui.S(8));
            var countRect = new Rectangle(rowRect.Right - countW - Ui.S(6), rowRect.Y, countW, rowRect.Height);

            if (node.IsAll) {
                int x = rowRect.X + Ui.S(10);
                var tr = new Rectangle(x, rowRect.Y, Math.Max(10, countRect.Left - x - Ui.S(4)), rowRect.Height);
                TextRenderer.DrawText(g, node.Title, active ? Ui.F9B : Ui.F9, tr, active ? Ui.Accent : Ui.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            } else if (node.IsCategory) {
                int x = rowRect.X + Ui.S(6);
                TextRenderer.DrawText(g, node.Expanded ? "▾" : "▸", Ui.F8,
                    new Rectangle(x, rowRect.Y, Ui.S(14), rowRect.Height),
                    Ui.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                x += Ui.S(16);
                int dot = Ui.S(7);
                var sm = g.SmoothingMode;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.FillEllipse(Ui.CachedBrush(CategoryDotColor(node.Category)), x, rowRect.Y + (rowRect.Height - dot) / 2, dot, dot);
                g.SmoothingMode = sm;
                x += dot + Ui.S(7);
                var tr = new Rectangle(x, rowRect.Y, Math.Max(10, countRect.Left - x - Ui.S(4)), rowRect.Height);
                TextRenderer.DrawText(g, node.Title, active ? Ui.F9B : Ui.F9, tr, active ? Ui.Accent : Ui.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            } else {
                int x = rowRect.X + Ui.S(30);
                var tr = new Rectangle(x, rowRect.Y, Math.Max(10, countRect.Left - x - Ui.S(4)), rowRect.Height);
                TextRenderer.DrawText(g, node.Title, active ? Ui.F8 : Ui.F8, tr, active ? Ui.Accent : Ui.Text2,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }

            TextRenderer.DrawText(g, countTxt, Ui.F8, countRect, active ? Ui.Accent : Ui.Muted,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        void SetupGrid() {
            Ui.EnableDoubleBuffer(grid);
            grid.Dock = DockStyle.Fill;
            grid.AutoGenerateColumns = false;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false;
            grid.ReadOnly = true;
            grid.RowHeadersVisible = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.MultiSelect = true;
            grid.BackgroundColor = Color.White;
            grid.BorderStyle = BorderStyle.None;
            grid.EnableHeadersVisualStyles = false;
            grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
            grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            grid.ColumnHeadersDefaultCellStyle.BackColor = Ui.Rail;
            grid.ColumnHeadersDefaultCellStyle.ForeColor = Ui.Muted;
            grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Ui.Rail;
            grid.ColumnHeadersDefaultCellStyle.Font = Ui.F8;
            grid.ColumnHeadersDefaultCellStyle.Padding = Ui.SPad(6, 0, 6, 0);
            grid.DefaultCellStyle.Font = Ui.F9;
            grid.DefaultCellStyle.ForeColor = Ui.Text;
            grid.DefaultCellStyle.Padding = Ui.SPad(6, 0, 6, 0);
            grid.DefaultCellStyle.SelectionBackColor = Ui.AccentSoft;
            grid.DefaultCellStyle.SelectionForeColor = Ui.Text;
            grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(0xFB, 0xFB, 0xFC);
            grid.GridColor = Color.FromArgb(0xEE, 0xEE, 0xF2);

            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "大类", HeaderText = "大类", Width = 84 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "细分类型", HeaderText = "细分类型", Width = 116 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "型号", HeaderText = "型号", Width = 240, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 62 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "品牌", HeaderText = "品牌", Width = 118 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "接口", HeaderText = "传感器接口类型", Width = 132 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "备注", HeaderText = "备注", Width = 96, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 18 });
            Ui.FitGrid(grid);
        }

        void BindEvents() {
            btnAdd.Click += (s, e) => Try(() => OpenEntryDialog(null, null));
            btnEdit.Click += (s, e) => Try(EditCurrentEntry);
            btnRecat.Click += (s, e) => Try(BatchRecategorizeSelected);
            btnDelete.Click += (s, e) => Try(DeleteSelectedEntries);

            Ui.BindDropDownMenu(btnImport, importMenu);
            Ui.BindDropDownMenu(btnExport, exportMenu);

            searchBox.TextChanged += (s, e) => {
                searchClearBtn.Visible = searchBox.Text.Length > 0;
                RefreshGrid(CurrentEntry());
            };
            searchClearBtn.Click += (s, e) => {
                searchBox.Clear();
                searchBox.Focus();
            };

            btnApply.Click += (s, e) => Try(ConfirmApplySelection);

            grid.SelectionChanged += (s, e) => UpdateButtonsState();
            grid.CellDoubleClick += (s, e) => {
                if (e.RowIndex < 0 || e.RowIndex >= visibleEntries.Count) return;
                if (pickMode) Try(ConfirmApplySelection);
                else Try(EditCurrentEntry);
            };
            grid.CellMouseUp += (s, e) => {
                if (e.Button != MouseButtons.Right || e.RowIndex < 0 || e.RowIndex >= visibleEntries.Count) return;
                if (!grid.Rows[e.RowIndex].Selected) {
                    grid.ClearSelection();
                    grid.Rows[e.RowIndex].Selected = true;
                    grid.CurrentCell = grid.Rows[e.RowIndex].Cells[FirstVisibleColIndex()];
                }
                ShowRowMenu();
            };

            KeyPreview = true;
            KeyDown += (s, e) => {
                if (e.Control && e.KeyCode == Keys.F) {
                    e.SuppressKeyPress = true;
                    searchBox.Focus();
                    searchBox.SelectAll();
                } else if (e.KeyCode == Keys.Insert || (e.Control && e.KeyCode == Keys.N)) {
                    e.SuppressKeyPress = true;
                    Try(() => OpenEntryDialog(null, null));
                } else if (e.KeyCode == Keys.Delete && grid.ContainsFocus) {
                    e.SuppressKeyPress = true;
                    Try(DeleteSelectedEntries);
                } else if (e.KeyCode == Keys.Enter && !pickMode && grid.ContainsFocus) {
                    e.SuppressKeyPress = true;
                    Try(EditCurrentEntry);
                } else if (e.KeyCode == Keys.Down && searchBox.Focused && grid.Rows.Count > 0) {
                    e.SuppressKeyPress = true;
                    grid.Focus();
                }
            };
        }

        int FirstVisibleColIndex() {
            foreach (DataGridViewColumn c in grid.Columns) if (c.Visible) return c.Index;
            return 0;
        }

        void ShowRowMenu() {
            var sel = SelectedEntries();
            if (sel.Count == 0) return;
            Ui.DisposeItems(rowMenu.Items);

            Action<string, Action, string, bool> add = (t, act, tone, en) => {
                var mi = new ToolStripMenuItem(t);
                if (!string.IsNullOrEmpty(tone)) mi.Tag = tone;
                mi.Enabled = en;
                mi.Click += (s, e) => Try(act);
                rowMenu.Items.Add(mi);
            };

            if (pickMode) {
                bool canPick = sel.Count == 1 && (targetCategory.Length == 0 || string.Equals(sel[0].Category, targetCategory, StringComparison.OrdinalIgnoreCase));
                add(primaryActionText, ConfirmApplySelection, "accent", canPick);
                rowMenu.Items.Add(new ToolStripSeparator());
            }
            add("编辑此条目…", EditCurrentEntry, null, sel.Count == 1);
            add("以此条目为模板复制新增…", () => OpenEntryDialog(null, sel[0]), null, sel.Count == 1);
            add(sel.Count > 1 ? "批量更改选中 " + sel.Count + " 项分类…" : "更改分类…", BatchRecategorizeSelected, null, true);
            rowMenu.Items.Add(new ToolStripSeparator());
            add("复制型号文本", () => {
                ClipboardUtil.SetText(string.Join("\r\n", sel.Select(x => x.Model).Where(x => !string.IsNullOrEmpty(x)).ToArray()));
                UpdateFooterStatus("已复制 " + sel.Count + " 条型号到剪贴板。");
            }, null, true);
            add(sel.Count > 1 ? "删除选中 " + sel.Count + " 条型号" : "删除此型号", DeleteSelectedEntries, "danger", true);

            Ui.StyleMenu(rowMenu);
            rowMenu.Show(Cursor.Position);
        }

        // ==================== 构建左侧分类树与右侧表格 ====================
        void RebuildNavAndGrid(LineupModelEntry focusEntry) {
            catalog.EnsureSubCategories();
            navNodes.Clear();

            var orderedCats = new List<string>();
            if (pickMode && targetCategory.Length > 0) {
                orderedCats.Add(targetCategory);
                foreach (var c in LineupModelCatalog.Categories)
                    if (!orderedCats.Contains(c)) orderedCats.Add(c);
            } else {
                orderedCats.AddRange(LineupModelCatalog.Categories);
            }
            foreach (var extra in catalog.Entries.Select(e => LineupTableData.Category(e.Category)).Distinct()) {
                if (!string.IsNullOrWhiteSpace(extra) && !orderedCats.Contains(extra)) orderedCats.Add(extra);
            }

            if (!pickMode) {
                navNodes.Add(new NavNode { IsAll = true, Title = "全部型号", Count = catalog.Entries.Count });
            }

            for (int ci = 0; ci < orderedCats.Count; ci++) {
                string cat = orderedCats[ci];
                if (pickMode && targetCategory.Length > 0 && ci == 1) {
                    navNodes.Add(new NavNode { IsSeparator = true, Title = "其他分类（浏览/维护）" });
                }
                var catItems = catalog.Entries.Where(e => string.Equals(LineupTableData.Category(e.Category), cat, StringComparison.OrdinalIgnoreCase)).ToList();
                bool exp = expandedCategories.Contains(cat);
                navNodes.Add(new NavNode { IsCategory = true, Category = cat, Title = cat, Count = catItems.Count, Expanded = exp });
                if (exp) {
                    var subs = catItems
                        .GroupBy(e => e.EffectiveSubCategory, StringComparer.OrdinalIgnoreCase)
                        .Select(g => new { Name = g.First().EffectiveSubCategory, Count = g.Count() })
                        .OrderBy(x => SubCategoryOrder(cat, x.Name))
                        .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    foreach (var sub in subs) {
                        navNodes.Add(new NavNode { Category = cat, SubCategory = sub.Name, Title = sub.Name, Count = sub.Count });
                    }
                }
            }

            // 如果当前选中的子分类已被删空，自动回退到其大类
            if (selectedSubCategory.Length > 0 && !navNodes.Any(n => !n.IsCategory && !n.IsAll && !n.IsSeparator
                && string.Equals(n.Category, selectedCategory, StringComparison.OrdinalIgnoreCase)
                && string.Equals(n.SubCategory, selectedSubCategory, StringComparison.OrdinalIgnoreCase))) {
                selectedSubCategory = "";
            }

            syncingNav = true;
            navList.BeginUpdate();
            try {
                navList.Items.Clear();
                int selIdx = -1;
                for (int i = 0; i < navNodes.Count; i++) {
                    var n = navNodes[i];
                    navList.Items.Add(n);
                    if (n.IsAll && selectedCategory.Length == 0) selIdx = i;
                    else if (n.IsCategory && string.Equals(n.Category, selectedCategory, StringComparison.OrdinalIgnoreCase) && selectedSubCategory.Length == 0) selIdx = i;
                    else if (!n.IsAll && !n.IsCategory && !n.IsSeparator
                        && string.Equals(n.Category, selectedCategory, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(n.SubCategory, selectedSubCategory, StringComparison.OrdinalIgnoreCase)) selIdx = i;
                }
                if (selIdx >= 0 && selIdx < navList.Items.Count) navList.SelectedIndex = selIdx;
            } finally {
                navList.EndUpdate();
                syncingNav = false;
            }

            RefreshGrid(focusEntry);
        }

        static int SubCategoryOrder(string cat, string sub) {
            var defs = LineupModelCatalog.DefaultSubCategories(cat);
            for (int i = 0; i < defs.Length; i++)
                if (string.Equals(defs[i], sub, StringComparison.OrdinalIgnoreCase)) return i;
            return 99;
        }

        void RefreshGrid(LineupModelEntry focusEntry) {
            string q = (searchBox.Text ?? "").Trim();
            IEnumerable<LineupModelEntry> query = catalog.Entries;
            if (selectedCategory.Length > 0)
                query = query.Where(e => string.Equals(LineupTableData.Category(e.Category), selectedCategory, StringComparison.OrdinalIgnoreCase));
            if (selectedSubCategory.Length > 0)
                query = query.Where(e => string.Equals(e.EffectiveSubCategory, selectedSubCategory, StringComparison.OrdinalIgnoreCase));
            if (q.Length > 0) {
                query = query.Where(e =>
                    (e.Model ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                    || (e.Brand ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                    || (e.EffectiveSubCategory ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                    || (e.Connector ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                    || (e.Notes ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                    || (e.Category ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            visibleEntries = query
                .OrderBy(e => LineupModelCatalog.CategoryOrder(e.Category))
                .ThenBy(e => SubCategoryOrder(e.Category, e.EffectiveSubCategory))
                .ThenBy(e => e.EffectiveSubCategory, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.Brand ?? "", StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.Model ?? "", StringComparer.OrdinalIgnoreCase)
                .ToList();

            bool showCatCol = selectedCategory.Length == 0;
            bool showConnCol = selectedCategory.Length == 0 || LineupModelCatalog.IsSensorCategory(selectedCategory) || selectedCategory == "其他电气件";
            grid.Columns["大类"].Visible = showCatCol;
            grid.Columns["接口"].Visible = showConnCol;

            grid.SuspendLayout();
            try {
                grid.Rows.Clear();
                if (visibleEntries.Count > 0) {
                    var rows = new DataGridViewRow[visibleEntries.Count];
                    int rowH = grid.RowTemplate.Height;
                    for (int i = 0; i < visibleEntries.Count; i++) {
                        var e = visibleEntries[i];
                        var r = new DataGridViewRow();
                        r.CreateCells(grid, e.Category, e.EffectiveSubCategory, e.Model, e.Brand, e.Connector, e.Notes);
                        r.Height = rowH;
                        rows[i] = r;
                    }
                    grid.Rows.AddRange(rows);
                }
            } finally {
                grid.ResumeLayout();
            }

            int focusIdx = focusEntry != null ? visibleEntries.IndexOf(focusEntry) : (visibleEntries.Count > 0 ? 0 : -1);
            if (focusIdx >= 0 && focusIdx < grid.Rows.Count) {
                grid.ClearSelection();
                grid.Rows[focusIdx].Selected = true;
                grid.CurrentCell = grid.Rows[focusIdx].Cells[FirstVisibleColIndex()];
            }

            string scopeName = selectedCategory.Length == 0
                ? "全部型号"
                : (selectedSubCategory.Length == 0 ? selectedCategory : selectedCategory + " · " + selectedSubCategory);
            scopeLabel.Text = scopeName + "（显示 " + visibleEntries.Count + " / 库内共 " + catalog.Entries.Count + " 条）";
            UpdateButtonsState();
        }

        LineupModelEntry CurrentEntry() {
            if (grid.CurrentRow != null && grid.CurrentRow.Index >= 0 && grid.CurrentRow.Index < visibleEntries.Count)
                return visibleEntries[grid.CurrentRow.Index];
            if (grid.SelectedRows.Count > 0) {
                int idx = grid.SelectedRows[0].Index;
                if (idx >= 0 && idx < visibleEntries.Count) return visibleEntries[idx];
            }
            return null;
        }

        List<LineupModelEntry> SelectedEntries() {
            var indices = new SortedSet<int>();
            foreach (DataGridViewRow r in grid.SelectedRows)
                if (r.Index >= 0 && r.Index < visibleEntries.Count) indices.Add(r.Index);
            if (indices.Count == 0 && grid.CurrentRow != null && grid.CurrentRow.Index >= 0 && grid.CurrentRow.Index < visibleEntries.Count)
                indices.Add(grid.CurrentRow.Index);
            var list = new List<LineupModelEntry>(indices.Count);
            foreach (int i in indices) list.Add(visibleEntries[i]);
            return list;
        }

        void UpdateButtonsState() {
            var sel = SelectedEntries();
            btnEdit.Enabled = sel.Count == 1;
            btnRecat.Enabled = sel.Count >= 1;
            btnDelete.Enabled = sel.Count >= 1;
            if (pickMode) {
                var cur = sel.Count == 1 ? sel[0] : CurrentEntry();
                bool match = cur != null && (targetCategory.Length == 0 || string.Equals(cur.Category, targetCategory, StringComparison.OrdinalIgnoreCase));
                btnApply.Enabled = match;
                if (cur != null && !match) {
                    footerStatus.ForeColor = Ui.Warn;
                    footerStatus.Text = "当前行需要「" + targetCategory + "」，左侧点回「" + targetCategory + "」即可套用";
                } else {
                    footerStatus.ForeColor = Ui.Muted;
                    footerStatus.Text = "双击可直接套用 · 顶部按钮可随时新增、编辑与分类维护型号库";
                }
            }
        }

        void UpdateFooterStatus(string msg) {
            if (!string.IsNullOrEmpty(msg)) {
                footerStatus.ForeColor = Ui.Ok;
                footerStatus.Text = msg;
                return;
            }
            footerStatus.ForeColor = Ui.Muted;
            footerStatus.Text = pickMode
                ? "双击可直接套用 · 顶部按钮可随时新增、编辑与分类维护型号库"
                : "共 " + catalog.Entries.Count + " 条型号 · 新增/修改/删除后自动保存到本机型号库";
        }

        void ConfirmApplySelection() {
            var entry = CurrentEntry();
            if (entry == null) throw new InvalidOperationException("请先在右侧列表选择一条型号。");
            if (targetCategory.Length > 0 && !string.Equals(entry.Category, targetCategory, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("当前表格行类别为「" + targetCategory + "」，请选择「" + targetCategory + "」分类下的型号。");
            SelectedEntry = entry;
            DialogResult = DialogResult.OK;
        }

        static string EntryKeyOf(LineupModelEntry e) {
            if (e == null) return "";
            return LineupTableData.Category(e.Category) + "\u001F" + (e.Model ?? "").Trim() + "\u001F" + (e.Brand ?? "").Trim() + "\u001F" + (e.Connector ?? "").Trim();
        }

        // 先在副本 draft 上持久化成功，再回写本窗口持有的 catalog，确保落盘失败时内存自动回滚零脏写
        void PersistDraft(LineupModelCatalog draft, string toastMsg, string focusKey) {
            draft.Deduplicate();
            draft.SortEntries();
            draft.SeedInitialized = true;
            if (onPersistCatalog != null) onPersistCatalog(draft);
            catalog.Entries = new List<LineupModelEntry>(draft.Entries);
            catalog.SeedInitialized = draft.SeedInitialized;
            CatalogChanged = true;
            LineupModelEntry focusEntry = string.IsNullOrEmpty(focusKey)
                ? null
                : catalog.Entries.FirstOrDefault(e => string.Equals(EntryKeyOf(e), focusKey, StringComparison.OrdinalIgnoreCase));
            RebuildNavAndGrid(focusEntry);
            UpdateFooterStatus(toastMsg);
        }

        // ==================== 型号库维护操作（新增 / 编辑 / 批量改分类 / 删除 / 导入 / 导出） ====================
        void EditCurrentEntry() {
            var cur = CurrentEntry();
            if (cur == null) throw new InvalidOperationException("请先选择要编辑的型号条目。");
            OpenEntryDialog(cur, cur);
        }

        void OpenEntryDialog(LineupModelEntry editing, LineupModelEntry template) {
            bool isNew = editing == null;
            string initCat = template != null ? template.Category
                : (selectedCategory.Length > 0 ? selectedCategory : (targetCategory.Length > 0 ? targetCategory : "气缸"));
            string initSub = template != null ? template.EffectiveSubCategory : selectedSubCategory;

            using (var d = new Form()) {
                Ui.Shell(d, isNew ? "新增型号库条目" : "编辑型号库条目", 500, 392, 440, 360);
                d.FormBorderStyle = FormBorderStyle.FixedDialog;

                var body = new Panel { Dock = DockStyle.Fill, Padding = Ui.SPad(20, 14, 20, 8), BackColor = Ui.Bg };
                var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, BackColor = Ui.Bg };
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Ui.S(116)));
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

                var cmbCat = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };
                cmbCat.Items.AddRange(LineupModelCatalog.Categories.Cast<object>().ToArray());
                int ci = cmbCat.Items.IndexOf(initCat);
                cmbCat.SelectedIndex = ci >= 0 ? ci : 0;

                var cmbSub = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown, AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.ListItems };
                var txtModel = new TextBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle, Text = template != null ? template.Model : "" };
                var cmbBrand = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown, AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.ListItems };
                cmbBrand.Items.AddRange(catalog.Entries.Select(e => (e.Brand ?? "").Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).Cast<object>().ToArray());
                cmbBrand.Text = template != null ? template.Brand : "";

                var cmbConn = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown, AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.ListItems };
                var conns = LineupPresets.Connectors.Concat(catalog.Entries.Select(e => (e.Connector ?? "").Trim()).Where(x => x.Length > 0))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                cmbConn.Items.AddRange(conns.Cast<object>().ToArray());
                cmbConn.Text = template != null ? template.Connector : "";

                var txtNotes = new TextBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle, Text = template != null ? template.Notes : "" };
                var lblConn = new Label { Text = "传感器接口类型", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Ui.Text2, Font = Ui.F9B };

                Action<string, string> fillSubs = (catName, preferSub) => {
                    var subs = LineupModelCatalog.DefaultSubCategories(catName)
                        .Concat(catalog.Entries.Where(e => string.Equals(e.Category, catName, StringComparison.OrdinalIgnoreCase)).Select(e => e.EffectiveSubCategory))
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    cmbSub.Items.Clear();
                    cmbSub.Items.AddRange(subs.Cast<object>().ToArray());
                    cmbSub.Text = !string.IsNullOrWhiteSpace(preferSub) ? preferSub : (subs.Length > 0 ? subs[0] : "");
                    bool sensor = LineupModelCatalog.IsSensorCategory(catName);
                    lblConn.Text = sensor ? "传感器接口类型 *" : "接口类型 (可选)";
                };

                cmbCat.SelectedIndexChanged += (s, e) => fillSubs(Convert.ToString(cmbCat.SelectedItem), "");
                fillSubs(Convert.ToString(cmbCat.SelectedItem), initSub);

                Action<int, string, Control, Label> addRow = (r, l, c, customLbl) => {
                    layout.RowStyles.Add(new RowStyle(SizeType.Absolute, Ui.S(36)));
                    c.Margin = Ui.SPad(0, 4, 0, 4);
                    var lb = customLbl ?? new Label { Text = l, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Ui.Text2, Font = Ui.F9B };
                    layout.Controls.Add(lb, 0, r);
                    layout.Controls.Add(c, 1, r);
                };
                addRow(0, "器件大类 *", cmbCat, null);
                addRow(1, "细分类型", cmbSub, null);
                addRow(2, "型号 *", txtModel, null);
                addRow(3, "品牌 *", cmbBrand, null);
                addRow(4, "传感器接口类型", cmbConn, lblConn);
                addRow(5, "备注 (可选)", txtNotes, null);

                var hint = new Label {
                    Dock = DockStyle.Bottom, Height = Ui.S(30), ForeColor = Ui.Muted, Font = Ui.F8,
                    Text = "细分类型可直接下拉选择或手工输入新分类名；留空时将按型号前缀自动归类。"
                };
                body.Controls.Add(layout);
                body.Controls.Add(hint);

                var footer = new ActionBar { Dock = DockStyle.Bottom, Height = Ui.S(56), Padding = Ui.SPad(20, 0, 20, 0) };
                var ok = Ui.Primary(isNew ? "添加并保存" : "保存修改", 108, 34);
                var cancel = Ui.Secondary("取消", 76, 34); cancel.DialogResult = DialogResult.Cancel;
                footer.AddRight(cancel); footer.AddRight(ok);
                d.AcceptButton = ok; d.CancelButton = cancel;
                d.Controls.Add(body); d.Controls.Add(footer);

                LineupModelCatalog validatedDraft = null;
                LineupModelEntry resultEntry = null;
                int editIdx = editing == null ? -1 : catalog.Entries.IndexOf(editing);
                ok.Click += (s, e) => {
                    try {
                        var candidate = new LineupModelEntry {
                            Category = Convert.ToString(cmbCat.SelectedItem),
                            SubCategory = cmbSub.Text.Trim(),
                            Model = txtModel.Text.Trim(),
                            Brand = cmbBrand.Text.Trim(),
                            Connector = cmbConn.Text.Trim(),
                            Notes = txtNotes.Text.Trim()
                        };
                        var draft = catalog.Clone();
                        var draftEditing = (editIdx >= 0 && editIdx < draft.Entries.Count) ? draft.Entries[editIdx] : null;
                        draft.AddOrUpdate(draftEditing, candidate);
                        validatedDraft = draft;
                        resultEntry = draftEditing ?? draft.Entries[draft.Entries.Count - 1];
                        d.DialogResult = DialogResult.OK;
                    } catch (Exception ex) {
                        MessageBox.Show(d, ex.Message, "型号库", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                };
                d.Shown += (s, e) => { txtModel.Focus(); txtModel.SelectAll(); };

                if (d.ShowDialog(this) != DialogResult.OK || validatedDraft == null || resultEntry == null) return;
                string nextCat = resultEntry.Category;
                string nextSub = resultEntry.EffectiveSubCategory;
                string focusKey = EntryKeyOf(resultEntry);
                string modelName = resultEntry.Model;
                PersistDraft(validatedDraft, (isNew ? "已新增型号：" : "已更新型号：") + modelName, focusKey);
                selectedCategory = nextCat;
                expandedCategories.Add(nextCat);
                if (selectedSubCategory.Length > 0 && !string.Equals(selectedSubCategory, nextSub, StringComparison.OrdinalIgnoreCase))
                    selectedSubCategory = "";
                RebuildNavAndGrid(catalog.Entries.FirstOrDefault(e => string.Equals(EntryKeyOf(e), focusKey, StringComparison.OrdinalIgnoreCase)));
            }
        }

        void BatchRecategorizeSelected() {
            var sel = SelectedEntries();
            if (sel.Count == 0) throw new InvalidOperationException("请先选择要更改分类的型号条目。");

            using (var d = new Form()) {
                Ui.Shell(d, "更改型号分类（已选 " + sel.Count + " 条）", 460, 240, 420, 220);
                d.FormBorderStyle = FormBorderStyle.FixedDialog;
                var body = new Panel { Dock = DockStyle.Fill, Padding = Ui.SPad(20, 14, 20, 8), BackColor = Ui.Bg };
                var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, BackColor = Ui.Bg };
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Ui.S(96)));
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

                var cmbCat = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };
                cmbCat.Items.AddRange(LineupModelCatalog.Categories.Cast<object>().ToArray());
                int ci = cmbCat.Items.IndexOf(sel[0].Category);
                cmbCat.SelectedIndex = ci >= 0 ? ci : 0;

                var cmbSub = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown, AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.ListItems };
                Action fillSubs = () => {
                    string c = Convert.ToString(cmbCat.SelectedItem);
                    var subs = new[] { "（按型号自动归类）" }
                        .Concat(LineupModelCatalog.DefaultSubCategories(c))
                        .Concat(catalog.Entries.Where(e => string.Equals(e.Category, c, StringComparison.OrdinalIgnoreCase)).Select(e => e.EffectiveSubCategory))
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    cmbSub.Items.Clear();
                    cmbSub.Items.AddRange(subs.Cast<object>().ToArray());
                    cmbSub.SelectedIndex = 0;
                };
                cmbCat.SelectedIndexChanged += (s, e) => fillSubs();
                fillSubs();

                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, Ui.S(36)));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, Ui.S(36)));
                cmbCat.Margin = Ui.SPad(0, 4, 0, 4);
                cmbSub.Margin = Ui.SPad(0, 4, 0, 4);
                layout.Controls.Add(new Label { Text = "目标大类", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Ui.Text2, Font = Ui.F9B }, 0, 0);
                layout.Controls.Add(cmbCat, 1, 0);
                layout.Controls.Add(new Label { Text = "细分类型", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Ui.Text2, Font = Ui.F9B }, 0, 1);
                layout.Controls.Add(cmbSub, 1, 1);

                var tip = new Label {
                    Dock = DockStyle.Bottom, Height = Ui.S(34), ForeColor = Ui.Muted, Font = Ui.F8,
                    Text = "可将误分类的型号（如调压阀、磁开等）批量移至正确的大类和细分类型。"
                };
                body.Controls.Add(layout);
                body.Controls.Add(tip);

                var footer = new ActionBar { Dock = DockStyle.Bottom, Height = Ui.S(54), Padding = Ui.SPad(20, 0, 20, 0) };
                var ok = Ui.Primary("确定移动", 104, 34); ok.DialogResult = DialogResult.OK;
                var cancel = Ui.Secondary("取消", 76, 34); cancel.DialogResult = DialogResult.Cancel;
                footer.AddRight(cancel); footer.AddRight(ok);
                d.AcceptButton = ok; d.CancelButton = cancel;
                d.Controls.Add(body); d.Controls.Add(footer);

                if (d.ShowDialog(this) != DialogResult.OK) return;
                string newCat = Convert.ToString(cmbCat.SelectedItem);
                string rawSub = (cmbSub.Text ?? "").Trim();
                bool autoSub = rawSub.Length == 0 || rawSub == "（按型号自动归类）";
                var draft = catalog.Clone();
                var draftSel = new List<LineupModelEntry>();
                foreach (var orig in sel) {
                    int idx = catalog.Entries.IndexOf(orig);
                    if (idx >= 0 && idx < draft.Entries.Count) draftSel.Add(draft.Entries[idx]);
                }
                var toRemoveDup = new List<LineupModelEntry>();
                foreach (var item in draftSel) {
                    string targetSub = autoSub ? LineupModelCatalog.InferSubCategory(newCat, item.Model, item.Brand) : rawSub;
                    string targetConn = (LineupModelCatalog.IsSensorCategory(newCat) && string.IsNullOrWhiteSpace(item.Connector)) ? "M8/3针" : (item.Connector ?? "");
                    bool dupExists = draft.Entries.Any(e => !ReferenceEquals(e, item) && !toRemoveDup.Contains(e)
                        && string.Equals(e.Category, newCat, StringComparison.OrdinalIgnoreCase)
                        && string.Equals((e.Model ?? "").Trim(), (item.Model ?? "").Trim(), StringComparison.OrdinalIgnoreCase)
                        && string.Equals((e.Brand ?? "").Trim(), (item.Brand ?? "").Trim(), StringComparison.OrdinalIgnoreCase)
                        && string.Equals((e.Connector ?? "").Trim(), targetConn.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (dupExists) {
                        toRemoveDup.Add(item);
                        continue;
                    }
                    item.Category = newCat;
                    item.SubCategory = targetSub;
                    item.Connector = targetConn;
                }
                if (toRemoveDup.Count > 0) draft.RemoveEntries(toRemoveDup);
                var focusItem = draftSel.FirstOrDefault(x => !toRemoveDup.Contains(x));
                string focusKey = EntryKeyOf(focusItem);
                PersistDraft(draft, "已将 " + draftSel.Count + " 条型号移入「" + newCat + (autoSub ? "」" : " · " + rawSub + "」") + (toRemoveDup.Count > 0 ? "（自动合并重复 " + toRemoveDup.Count + " 条）" : ""), focusKey);
                selectedCategory = newCat;
                selectedSubCategory = autoSub ? "" : rawSub;
                expandedCategories.Add(newCat);
                RebuildNavAndGrid(string.IsNullOrEmpty(focusKey) ? null : catalog.Entries.FirstOrDefault(e => string.Equals(EntryKeyOf(e), focusKey, StringComparison.OrdinalIgnoreCase)));
            }
        }

        void DeleteSelectedEntries() {
            var sel = SelectedEntries();
            if (sel.Count == 0) throw new InvalidOperationException("请先选择要删除的型号条目。");
            string prompt = sel.Count == 1
                ? "确定从本机型号库中删除「" + sel[0].Model + "（" + sel[0].Brand + "）」吗？"
                : "确定从本机型号库中删除选中的 " + sel.Count + " 条型号记录吗？";
            if (MessageBox.Show(this, prompt, "删除型号", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            var draft = catalog.Clone();
            var toRemove = new List<LineupModelEntry>();
            foreach (var orig in sel) {
                int idx = catalog.Entries.IndexOf(orig);
                if (idx >= 0 && idx < draft.Entries.Count) toRemove.Add(draft.Entries[idx]);
            }
            int removed = draft.RemoveEntries(toRemove);
            PersistDraft(draft, "已删除 " + removed + " 条型号记录。", null);
        }

        void ImportFromFile() {
            using (var dialog = new OpenFileDialog { Filter = "型号库 / 清单|*.xlsx;*.xlsm;*.xls;*.csv;*.tsv;*.txt;*.xml" }) {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                string ext = Path.GetExtension(dialog.FileName).ToLowerInvariant();
                string defCat = selectedCategory.Length > 0 ? selectedCategory : "气缸";
                var draft = catalog.Clone();
                int added;
                if (ext == ".xlsx" || ext == ".xlsm" || ext == ".xls") {
                    var prev = Cursor;
                    Cursor = Cursors.WaitCursor;
                    try {
                        var imp = LineupXlsxReader.Read(dialog.FileName, true);
                        added = draft.Learn(imp.Records);
                    } finally { Cursor = prev; }
                } else {
                    var imported = ext == ".xml"
                        ? LineupModelCatalog.Load(dialog.FileName)
                        : LineupModelCatalog.Import(SettingsFile.ReadUserText(dialog.FileName), ext == ".csv" ? ',' : '\t', defCat);
                    added = draft.Merge(imported);
                }
                PersistDraft(draft, "已导入新增 " + added + " 条，当前库内共 " + draft.Entries.Count + " 条。", null);
                UpdateFooterStatus("已导入新增 " + added + " 条，当前库内共 " + catalog.Entries.Count + " 条。");
            }
        }

        void ImportFromClipboard() {
            string text = ClipboardUtil.GetText();
            if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("剪贴板为空。请先从 Excel 复制含「型号、品牌」表头的表格。");
            string defCat = selectedCategory.Length > 0 ? selectedCategory : "气缸";
            char delim = text.IndexOf('\t') >= 0 ? '\t' : ',';
            var imported = LineupModelCatalog.Import(text, delim, defCat);
            var draft = catalog.Clone();
            int added = draft.Merge(imported);
            PersistDraft(draft, "从剪贴板新增 " + added + " 条，当前库内共 " + draft.Entries.Count + " 条。", null);
            UpdateFooterStatus("从剪贴板新增 " + added + " 条，当前库内共 " + catalog.Entries.Count + " 条。");
        }

        void LearnFromProject() {
            if (onLearnCurrentProject == null) return;
            int added = onLearnCurrentProject();
            if (added > 0) CatalogChanged = true;
            RebuildNavAndGrid(CurrentEntry());
            UpdateFooterStatus("已从当前清单收录 " + added + " 条新型号，库内共 " + catalog.Entries.Count + " 条。");
        }

        void MergeMissingSeed() {
            if (onLoadSeed == null) return;
            var seed = onLoadSeed();
            var draft = catalog.Clone();
            int added = draft.Merge(seed);
            PersistDraft(draft, added > 0 ? "已补充 " + added + " 条内置默认型号。" : "内置默认型号已全部在库中，无新增。", null);
        }

        void ExportCsv(bool currentCategoryOnly) {
            var source = currentCategoryOnly && selectedCategory.Length > 0 ? visibleEntries : catalog.Entries;
            string name = currentCategoryOnly && selectedCategory.Length > 0
                ? "Lineup-型号库-" + selectedCategory + ".csv"
                : "Lineup-型号库.csv";
            using (var d = new SaveFileDialog { Filter = "型号库 CSV|*.csv", FileName = name }) {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                SettingsFile.WriteUserText(d.FileName, LineupCsv.Write(LineupModelCatalog.ExportRows(source)));
                UpdateFooterStatus("已按分类导出 " + source.Count + " 条型号到 " + Path.GetFileName(d.FileName));
            }
        }

        void OpenCatalogFolder() {
            if (string.IsNullOrEmpty(catalogFilePath)) return;
            try {
                if (File.Exists(catalogFilePath))
                    Process.Start("explorer.exe", "/select,\"" + catalogFilePath + "\"");
                else {
                    string dir = Path.GetDirectoryName(catalogFilePath);
                    if (!string.IsNullOrEmpty(dir)) { Directory.CreateDirectory(dir); Process.Start("explorer.exe", "\"" + dir + "\""); }
                }
            } catch (Exception ex) {
                throw new InvalidOperationException("无法打开文件夹：" + ex.Message);
            }
        }

        void Try(Action act) {
            try { act(); }
            catch (Exception ex) {
                footerStatus.ForeColor = Ui.Bad;
                footerStatus.Text = ex.Message;
                MessageBox.Show(this, ex.Message, "型号库", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }
}
