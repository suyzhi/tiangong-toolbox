using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace TianGongCadSuite {
    public sealed partial class LineupTableForm {
        Label pickingTitle, pickingLabel;
        SlimProgress pickingProgress;
        Button pickSkipBtn;
        string pickingId;
        Panel pickingPanel;
        Rectangle normalBounds;
        FormWindowState normalWindowState = FormWindowState.Normal;
        bool pickingMode, fullFieldView;
        string catalogFile, catalogError = "";
        LineupModelCatalog catalog;

        ContextMenuStrip addMenu, kitMenu, moreMenu;
        Button moreMenuBtn;
        ToolStripMenuItem fullFieldMenuItem;
        Panel searchHostPanel;
        Button searchClearBtn;
        bool workspaceTabsCompact;
        readonly FlatButton[] workspacePills = new FlatButton[4];
        readonly Dictionary<string, AutoCompleteStringCollection> gridAcCache = new Dictionary<string, AutoCompleteStringCollection>(StringComparer.Ordinal);
        string lastGridAcKey = "";

        internal Button MoreMenuButton { get { return moreMenuBtn; } }
        internal ContextMenuStrip MoreMenu { get { return moreMenu; } }

        static LineupModelCatalog LoadBuiltInSeed() {
            string seedPath = Path.Combine(Path.GetDirectoryName(typeof(LineupTableForm).Assembly.Location), "lineup-models-seed.xml");
            var seed = LineupModelCatalog.LoadSeed(seedPath);
            seed.EnsureSubCategories();
            return seed;
        }

        void InitializeCatalog(string path) {
            catalogFile = path ?? LineupModelCatalog.UserFile;
            try {
                if (path != null) {
                    // 测试或隔离路径：绝不混入内置种子库
                    catalog = File.Exists(catalogFile) ? LineupModelCatalog.Load(catalogFile) : new LineupModelCatalog();
                    catalog.EnsureSubCategories();
                    return;
                }
                var seed = LoadBuiltInSeed();
                if (File.Exists(catalogFile)) {
                    catalog = LineupModelCatalog.Load(catalogFile);
                    if (!catalog.SeedInitialized) {
                        // 旧版无标记用户库：首次升级时一次性补齐内置种子并标记，之后用户的删除/修改永久保留、不再被覆盖
                        catalog.Merge(seed);
                        catalog.SeedInitialized = true;
                        catalog.EnsureSubCategories();
                        try { catalog.Save(catalogFile); } catch { }
                    } else {
                        catalog.EnsureSubCategories();
                    }
                } else {
                    catalog = seed;
                    catalog.SeedInitialized = true;
                }
            } catch (Exception ex) { catalog = new LineupModelCatalog(); catalogError = "型号库加载失败：" + ex.Message; }
        }

        void InvalidateModelAutoCompleteCaches() {
            gridAcCache.Clear();
            lastGridAcKey = "";
            if (pneumaticPanel != null) pneumaticPanel.InvalidateAutoCompleteCache();
            RefreshEntryModelItems();
        }

        void SaveCatalogDirect(LineupModelCatalog updated) {
            if (catalogError.Length > 0) throw new InvalidOperationException(catalogError);
            using (var mutex = new System.Threading.Mutex(false, "Local\\TianGongLineupModelLibrary")) {
                bool locked = false;
                try {
                    try { locked = mutex.WaitOne(2000); } catch (System.Threading.AbandonedMutexException) { locked = true; }
                    if (!locked) throw new IOException("型号库正被另一窗口更新，请稍后重试。");
                    updated.EnsureSubCategories();
                    updated.Save(catalogFile);
                    catalog = updated;
                    InvalidateModelAutoCompleteCaches();
                } finally { if (locked) mutex.ReleaseMutex(); }
            }
        }

        void DisposeMenus() {
            if (addMenu != null) addMenu.Dispose();
            if (kitMenu != null) kitMenu.Dispose();
            if (moreMenu != null) moreMenu.Dispose();
        }

        // 步骤 ① 方案 A 顶部分区导航条：气动系统(阀岛树) | 普通传感器(SE) | 其他电气件(E) | BOM总表  +  搜索与更多
        Panel BuildWorkspaceBar() {
            var bar = new Panel { Height = Ui.S(38), BackColor = Ui.Bg };

            moreMenu = Ui.Menu();
            fullFieldMenuItem = new ToolStripMenuItem("显示全部字段（含地址 / 参数 / 备注）") { CheckOnClick = true };
            fullFieldMenuItem.Click += (s, e) => { fullFieldView = fullFieldMenuItem.Checked; ApplyFieldVisibility(); };
            moreMenu.Items.Add(fullFieldMenuItem);
            AddMenuItem(moreMenu, "紧凑点选小窗模式 (Ctrl+Shift+P)", () => SetPickingMode(true));
            AddMenuItem(moreMenu, "在 CAD 中定位当前行模型", Locate);
            AddMenuItem(moreMenu, "清除 CAD 高亮", ClearHighlight);
            moreMenu.Items.Add(new ToolStripSeparator());

            var editSub = new ToolStripMenuItem("表格批量编辑");
            AddSubItem(editSub, "向下填充选中列 (Ctrl+D)", FillDown);
            AddSubItem(editSub, "批量设置备注 / 模块名…", BatchSetNotesDialog);
            AddSubItem(editSub, "复制选中行", DuplicateRows);
            AddSubItem(editSub, "删除选中行", DeleteRows);
            editSub.DropDownItems.Add(new ToolStripSeparator());
            AddSubItem(editSub, "上方插入空行", () => InsertAt(false));
            AddSubItem(editSub, "下方插入空行", () => InsertAt(true));
            AddSubItem(editSub, "上移选中行 (Alt+↑)", () => MoveRows(-1));
            AddSubItem(editSub, "下移选中行 (Alt+↓)", () => MoveRows(1));
            editSub.DropDownItems.Add(new ToolStripSeparator());
            AddSubItem(editSub, "重新顺排全部编号", CompactAllNumbers);
            moreMenu.Items.Add(editSub);

            var modelSub = new ToolStripMenuItem("型号库");
            var manageModelItem = new ToolStripMenuItem("管理 / 编辑型号库…") { Tag = "accent" };
            manageModelItem.Click += (s, e) => Try(OpenModelLibraryManager);
            modelSub.DropDownItems.Add(manageModelItem);
            modelSub.DropDownItems.Add(new ToolStripSeparator());
            AddSubItem(modelSub, "选择当前行型号 (F4 / 双击型号)", () => PickModel(false));
            AddSubItem(modelSub, "套用型号到选中多行", () => PickModel(true));
            modelSub.DropDownItems.Add(new ToolStripSeparator());
            AddSubItem(modelSub, "导入型号库 / 从清单提取…", ImportLibrary);
            AddSubItem(modelSub, "收录当前清单型号入库", () => { SaveDraft(false); int n = LearnModels(project.Records); ShowToast("型号库新增 " + n + " 条，现有 " + catalog.Entries.Count + " 条。", Ui.Ok); });
            AddSubItem(modelSub, "导出型号库 CSV…", ExportLibrary);
            moreMenu.Items.Add(modelSub);

            var ioSub = new ToolStripMenuItem("导入 / 导出");
            AddSubItem(ioSub, "导入已有 Excel (.xlsx / .xls)…", ImportXlsxDialog);
            AddSubItem(ioSub, "从剪贴板粘贴整张清单", () => ImportText(Clipboard.GetText(), '\t'));
            AddSubItem(ioSub, "导入 CSV / TSV 文本…", () => {
                using (var d = new OpenFileDialog { Filter = "表格文本|*.tsv;*.txt;*.csv" })
                    if (d.ShowDialog(this) == DialogResult.OK)
                        ImportText(SettingsFile.ReadUserText(d.FileName), Path.GetExtension(d.FileName).Equals(".csv", StringComparison.OrdinalIgnoreCase) ? ',' : '\t');
            });
            ioSub.DropDownItems.Add(new ToolStripSeparator());
            AddSubItem(ioSub, "复制到 WPS / Excel (11列)", () => { ClipboardUtil.SetText(LineupTableData.Tsv(ExportRows())); ShowToast("已复制五类 11 列，可在表格 A1 粘贴。", Ui.Ok); });
            AddSubItem(ioSub, "复制参考 BOM (10列)", () => { ExportRows(); ClipboardUtil.SetText(LineupTableData.Tsv(LineupTableData.ReferenceGrouped(project))); ShowToast("已复制 10 列参考 BOM。", Ui.Ok); });
            AddSubItem(ioSub, "导出五类 CSV…", () => {
                using (var d = new SaveFileDialog { Filter = "CSV 清单|*.csv", FileName = Path.GetFileNameWithoutExtension(file) + "-Lineup.csv" })
                    if (d.ShowDialog(this) == DialogResult.OK) Export(d.FileName);
            });
            AddSubItem(ioSub, "导出旧版位置图包 (PNG + XML)…", ExportLocationDialog);
            moreMenu.Items.Add(ioSub);

            workspaceFlow = new FlowLayoutPanel {
                Dock = DockStyle.Fill, WrapContents = false, AutoScroll = false, BackColor = Ui.Bg, Padding = Ui.SPad(0, 3, 0, 0)
            };

            var rightTools = new FlowLayoutPanel {
                Dock = DockStyle.Right, AutoSize = true, WrapContents = false, BackColor = Ui.Bg, Padding = new Padding(0)
            };
            searchHostPanel = new Panel { Width = Ui.S(210), Height = Ui.S(34), BackColor = Ui.Bg, Padding = Ui.SPad(0, 4, 0, 4), Margin = Ui.SPad(0, 2, 6, 0) };
            search.Dock = DockStyle.Fill;
            search.Font = Ui.F9;
            search.HandleCreated += (s, e) => { try { SendMessage(search.Handle, EmSetCueBanner, (IntPtr)1, "搜索编号、描述、型号…"); } catch { } };
            searchClearBtn = Ui.Small("×");
            searchClearBtn.Dock = DockStyle.Right;
            searchClearBtn.Width = Ui.S(26);
            searchClearBtn.Visible = false;
            ((FlatButton)searchClearBtn).SetFore(Ui.Muted);
            searchClearBtn.Click += (s, e) => { search.Clear(); searchDebounce.Stop(); ApplySearchFilterNow(); search.Focus(); };
            searchHostPanel.Controls.Add(search);
            searchHostPanel.Controls.Add(searchClearBtn);

            moreMenuBtn = Ui.Ghost("更多 ▾");
            ((FlatButton)moreMenuBtn).SetFore(Ui.Text2);
            moreMenuBtn.Margin = Ui.SPad(0, 4, 0, 0);
            Ui.BindDropDownMenu(moreMenuBtn, moreMenu);
            tips.SetToolTip(moreMenuBtn, "显示完整字段、导入/导出 Excel、型号库管理与批量编辑");

            rightTools.Controls.Add(searchHostPanel);
            rightTools.Controls.Add(moreMenuBtn);

            bar.Controls.Add(workspaceFlow);
            bar.Controls.Add(rightTools);
            bar.Resize += (s, e) => AdaptWorkspaceBarToWidth();
            return bar;
        }

        void AdaptWorkspaceBarToWidth() {
            if (workspaceBar == null || searchHostPanel == null) return;
            int w = workspaceBar.ClientSize.Width;
            if (w <= 0) return;
            int wantSearch = w < Ui.S(960) ? Ui.S(142) : (w > Ui.S(1580) ? Ui.S(260) : Ui.S(210));
            if (Math.Abs(searchHostPanel.Width - wantSearch) > Ui.S(4)) searchHostPanel.Width = wantSearch;
            bool wantCompact = w < Ui.S(980);
            if (wantCompact != workspaceTabsCompact) {
                workspaceTabsCompact = wantCompact;
                RebuildWorkspaceTabs();
            }
        }

        void RebuildWorkspaceTabs() {
            if (workspaceFlow == null || project == null) return;
            var islands = LineupPneumaticOps.ListIslands(project);
            int pneuItems = project.Records.Count(r => {
                string c = LineupTableData.Category(r.Category);
                return c == "气缸" || c == "阀片" || c == "气缸传感器";
            });
            int seCount = project.Records.Count(r => LineupTableData.Category(r.Category) == "普通传感器");
            int eCount = project.Records.Count(r => LineupTableData.Category(r.Category) == "其他电气件");
            int bomCount = project.Records.Count(r => BomCategories.Contains(LineupTableData.Category(r.Category)));

            string[] titles;
            if (workspaceTabsCompact) {
                titles = new[] {
                    islands.Count > 0 ? "气动 " + islands.Count + "岛·" + pneuItems : "气动 " + pneuItems,
                    seCount > 0 ? "传感器 " + seCount : "传感器",
                    eCount > 0 ? "电气 " + eCount : "电气",
                    bomCount > 0 ? "总表 " + bomCount : "总表"
                };
            } else {
                string pneuLabel = islands.Count > 0
                    ? "气动系统 · " + islands.Count + "岛 " + pneuItems + "项"
                    : (pneuItems > 0 ? "气动系统 " + pneuItems : "气动系统 (阀岛/阀片/气缸)");
                titles = new[] {
                    pneuLabel,
                    seCount > 0 ? "普通传感器 (SE) " + seCount : "普通传感器 (SE)",
                    eCount > 0 ? "其他电气件 (E) " + eCount : "其他电气件 (E)",
                    bomCount > 0 ? "BOM 总表 " + bomCount : "BOM 总表"
                };
            }

            workspaceFlow.SuspendLayout();
            // 原地复用 4 个分区胶囊按钮，彻底消除切换视图时的 Win32 句柄泄漏与重绘抖动
            for (int i = 0; i < 4; i++) {
                var pill = workspacePills[i];
                if (pill == null) {
                    int idx = i;
                    pill = new FlatButton("", Ui.Rail, Ui.Text2, Ui.Line, Ui.S(80), Ui.S(30));
                    pill.Click += (s, e) => { if (activeViewMode != idx) SetViewMode(idx); };
                    workspacePills[i] = pill;
                    workspaceFlow.Controls.Add(pill);
                }
                bool active = activeViewMode == i;
                Font wantFont = active ? Ui.F9B : Ui.F9;
                if (pill.Font != wantFont) pill.Font = wantFont;
                if (pill.Text != titles[i]) pill.Text = titles[i];
                pill.SetPalette(
                    active ? Ui.Accent : Ui.Rail,
                    active ? Color.White : Ui.Text2,
                    active ? Ui.Accent : Ui.Line);
                int w = TextRenderer.MeasureText(titles[i], wantFont).Width + Ui.S(workspaceTabsCompact ? 16 : 22);
                if (pill.Width != w) pill.Width = w;
                var wantMargin = Ui.SPad(0, 1, workspaceTabsCompact ? 4 : 6, 0);
                if (pill.Margin != wantMargin) pill.Margin = wantMargin;
            }
            workspaceFlow.ResumeLayout();
        }

        // 模式专属精简操作栏（普通传感器 / 其他电气件 / BOM 总表；气动模式自带阀岛头栏故此栏自动隐藏）
        Panel BuildModeActionBar() {
            var host = new Panel { Height = Ui.S(38), BackColor = Ui.Bg, Visible = false };

            // 1. 普通传感器 (SE) 操作条
            sensorActionStrip = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Bg, Visible = false };
            var seLeft = new FlowLayoutPanel { Dock = DockStyle.Left, AutoSize = true, WrapContents = false, BackColor = Ui.Bg, Padding = Ui.SPad(0, 3, 0, 0) };
            var btnAddSe = Ui.Primary("+ 添加传感器 (SE)", 134, 32); btnAddSe.Margin = Ui.SPad(0, 0, 8, 0);
            var btnPickSe = Ui.Secondary("在 CAD 点选加入", 118, 32); btnPickSe.Margin = Ui.SPad(0, 0, 6, 0);
            var btnSelSe = Ui.Secondary("加入 CAD 选中件", 118, 32); btnSelSe.Margin = Ui.SPad(0, 0, 6, 0);
            var btnModelSe = Ui.Secondary("套用型号到选中行", 122, 32); btnModelSe.Margin = new Padding(0);
            btnAddSe.Click += (s, e) => Try(() => AddRecordAndOpenMode("普通传感器", 1));
            btnPickSe.Click += (s, e) => Try(() => { category.SelectedItem = "普通传感器"; StartContinuous("add"); });
            btnSelSe.Click += (s, e) => Try(() => { category.SelectedItem = "普通传感器"; AddSelectedModels(); });
            btnModelSe.Click += (s, e) => Try(() => PickModel(true));
            seLeft.Controls.AddRange(new Control[] { btnAddSe, btnPickSe, btnSelSe, btnModelSe });

            batchEntryToggleBtnSensor = Ui.Small("批量录入 ▾");
            ((FlatButton)batchEntryToggleBtnSensor).SetFore(Ui.Text2);
            batchEntryToggleBtnSensor.Dock = DockStyle.Right;
            batchEntryToggleBtnSensor.Click += (s, e) => ToggleBatchEntry(!batchEntryOpen);
            sensorActionStrip.Controls.Add(seLeft);
            sensorActionStrip.Controls.Add(batchEntryToggleBtnSensor);

            // 2. 其他电气件 (E) 操作条
            electricalActionStrip = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Bg, Visible = false };
            var eLeft = new FlowLayoutPanel { Dock = DockStyle.Left, AutoSize = true, WrapContents = false, BackColor = Ui.Bg, Padding = Ui.SPad(0, 3, 0, 0) };
            var btnAddE = Ui.Primary("+ 添加电气件 (E)", 128, 32); btnAddE.Margin = Ui.SPad(0, 0, 8, 0);
            var btnPickE = Ui.Secondary("在 CAD 点选加入", 118, 32); btnPickE.Margin = Ui.SPad(0, 0, 6, 0);
            var btnSelE = Ui.Secondary("加入 CAD 选中件", 118, 32); btnSelE.Margin = Ui.SPad(0, 0, 6, 0);
            var btnSyncIslands = Ui.Secondary("从阀岛同步本体到 E**", 148, 32); btnSyncIslands.Margin = new Padding(0);
            btnAddE.Click += (s, e) => Try(() => AddRecordAndOpenMode("其他电气件", 2));
            btnPickE.Click += (s, e) => Try(() => { category.SelectedItem = "其他电气件"; StartContinuous("add"); });
            btnSelE.Click += (s, e) => Try(() => { category.SelectedItem = "其他电气件"; AddSelectedModels(); });
            btnSyncIslands.Click += (s, e) => Try(() => {
                var next = Snapshot();
                int added = LineupPneumaticOps.SyncIslandsToElectrical(next);
                if (added > 0) Commit(next, null, "已将 " + added + " 个阀岛本体同步加入「其他电气件 E**」");
                else ShowToast("所有已建阀岛均已列在「其他电气件」中，未重复添加。", Ui.Muted);
            });
            tips.SetToolTip(btnSyncIslands, "把气动区已建立的阀岛/汇流排本体自动同步生成 E** 电气清单条目");
            eLeft.Controls.AddRange(new Control[] { btnAddE, btnPickE, btnSelE, btnSyncIslands });

            batchEntryToggleBtnElec = Ui.Small("批量录入 ▾");
            ((FlatButton)batchEntryToggleBtnElec).SetFore(Ui.Text2);
            batchEntryToggleBtnElec.Dock = DockStyle.Right;
            batchEntryToggleBtnElec.Click += (s, e) => ToggleBatchEntry(!batchEntryOpen);
            electricalActionStrip.Controls.Add(eLeft);
            electricalActionStrip.Controls.Add(batchEntryToggleBtnElec);

            // 3. BOM 总表操作条
            topActionStrip = BuildTopActionStrip();
            topActionStrip.Dock = DockStyle.Fill;
            topActionStrip.Visible = false;

            host.Controls.Add(sensorActionStrip);
            host.Controls.Add(electricalActionStrip);
            host.Controls.Add(topActionStrip);
            return host;
        }

        // BOM 总表模式顶部操作条
        Control BuildTopActionStrip() {
            var bar = new Panel { Height = Ui.S(38), BackColor = Ui.Bg };

            addMenu = Ui.Menu();
            AddMenuItem(addMenu, "新建阀岛 / 阀组 (Y*)…", () => { SetViewMode(0); if (pneumaticPanel != null) pneumaticPanel.OpenCreateIslandDialog(); });
            AddMenuItem(addMenu, "自动扫描装配识别器件…", ScanAssemblyDialog);
            AddMenuItem(addMenu, "在 CAD 中逐个点选加入", () => StartContinuous("add"));
            AddMenuItem(addMenu, "加入 CAD 当前选中的零件", () => AddSelectedModels());
            AddMenuItem(addMenu, "新建一条空白记录", AddEmpty);
            addMenu.Items.Add(new ToolStripSeparator());
            AddMenuItem(addMenu, "导入已有 Excel (.xlsx / .xls)…", ImportXlsxDialog);
            AddMenuItem(addMenu, "从剪贴板粘贴整张清单", () => ImportText(Clipboard.GetText(), '\t'));

            kitMenu = Ui.Menu();
            AddMenuItem(kitMenu, "一键补齐配套阀片 + H/W 传感器（推荐）", AutoCompleteValvesAndSensors);
            AddMenuItem(kitMenu, "仅生成配套阀片", GenerateValvesForSelected);
            AddMenuItem(kitMenu, "仅生成 H/W 传感器", GenerateSensorPairs);
            AddMenuItem(kitMenu, "生成双组传感器 (H1/W1/H2/W2)", GenerateMultiSensorsForSelected);
            kitMenu.Items.Add(new ToolStripSeparator());
            AddMenuItem(kitMenu, "在 CAD 中连续绑定未绑行", () => StartContinuous("bind"));
            var hierarchySub = new ToolStripMenuItem("阀岛与层级管理");
            AddSubItem(hierarchySub, "设置项目抬头与阀岛型号…", () => ShowNumberSettings());
            AddSubItem(hierarchySub, "将选中阀片换到其他阀岛…", AssignIslandDialog);
            AddSubItem(hierarchySub, "气缸归属到指定阀片…", AssignCylinderDialog);
            AddSubItem(hierarchySub, "查看阀岛 → 气缸层级树", ShowHierarchy);
            AddSubItem(hierarchySub, "同步气缸备注到传感器", SyncSensorNotes);
            AddSubItem(hierarchySub, "给选中空编号行自动编号", NumberSelected);
            kitMenu.Items.Add(hierarchySub);

            var leftFlow = new FlowLayoutPanel {
                Dock = DockStyle.Left, AutoSize = true, WrapContents = false, BackColor = Ui.Bg, Padding = new Padding(0, Ui.S(3), 0, 0)
            };
            var btnAdd = Ui.Primary("+ 添加器件 ▾", 118, 32);
            var btnKit = Ui.Secondary("一键配齐 ▾", 104, 32);
            var btnNewY = Ui.Secondary("+ 新建阀岛", 96, 32);
            btnAdd.Margin = Ui.SPad(0, 0, 8, 0);
            btnKit.Margin = Ui.SPad(0, 0, 6, 0);
            btnNewY.Margin = new Padding(0);

            Ui.BindDropDownMenu(btnAdd, addMenu);
            Ui.BindDropDownMenu(btnKit, kitMenu);
            btnNewY.Click += (s, e) => Try(() => { SetViewMode(0); if (pneumaticPanel != null) pneumaticPanel.OpenCreateIslandDialog(); });
            tips.SetToolTip(btnAdd, "扫描装配、在 CAD 点选零件、新增空行或导入 Excel");
            tips.SetToolTip(btnKit, "一键为气缸补齐配套阀片与 H/W 传感器，或去 CAD 连续绑定");
            tips.SetToolTip(btnNewY, "切到气动区并新建阀岛 / 汇流排 / 阀组");
            leftFlow.Controls.AddRange(new Control[] { btnAdd, btnKit, btnNewY });

            bar.Controls.Add(leftFlow);
            return bar;
        }

        // 构建并挂载方案 A 气动联动工作区
        LineupPneumaticPanel BuildPneumaticPanel() {
            var panel = new LineupPneumaticPanel();
            panel.GetProject = () => project;
            panel.GetCatalog = () => catalog;
            panel.HasCad = () => assembly != null;
            panel.RunProjectMutation = (mutate, focusId, message) => {
                var next = Snapshot();
                mutate(next);
                Commit(next, focusId, message);
                ShowToast(message, Ui.Ok, "撤销", (s, e) => Try(Undo));
            };
            panel.ApplyInlineField = ApplyPneumaticInlineField;
            panel.OnSelectionChanged = id => {
                pneumaticSelectedId = id;
                if (preview != null && preview.Visible && !pickingMode) {
                    var r = string.IsNullOrEmpty(id) ? null : project.Records.FirstOrDefault(x => x.Id == id);
                    preview.ShowFile(r != null && !string.IsNullOrEmpty(r.SourceFile) ? r.SourceFile : "");
                }
            };
            panel.OnZoomDelta = delta => ApplyGridZoom(gridZoomPercent + delta, true);
            panel.OnLocateRecord = id => {
                var r = Snapshot().Records.FirstOrDefault(x => x.Id == id);
                if (r == null) throw new InvalidOperationException("找不到该记录。");
                LocateRecord(r);
            };
            panel.OnBindCurrentCadToRecord = id => BindCurrentCadSelectionToRecord(id);
            panel.OnBindCurrentCadToRecords = ids => BindCurrentCadSelectionToRecords(ids);
            panel.OnStartSequentialBindRecords = ids => StartSequentialBindRecords(ids);
            panel.OnBindCurrentCadToIsland = key => BindCurrentCadSelectionToIsland(key);
            panel.OnStartBindIslandIn3D = key => StartBindIslandIn3D(key);
            panel.OnAddCadSelectedAsCylinder = key => AddCadSelectedAsCylinderToIsland(key);
            panel.OnStartContinuousAddCylinder = key => StartContinuousCylinderForIsland(key);
            panel.OnStartContinuousBindIsland = key => StartContinuousBindForIsland(key);
            panel.OnScanAssembly = ScanAssemblyDialog;
            panel.OnImportXlsx = ImportXlsxDialog;
            panel.OnPickModelForRecords = (cat, ids) => PickModelForCategory(cat, ids);
            panel.OnNotifyStatus = msg => { status.Text = msg; ShowToast(msg, Ui.Warn); };
            panel.OnMoveSelectedRows = dir => MoveRows(dir);
            return panel;
        }

        // 零卡顿回写气动面板单元格修改：直改内存记录 + 同步底层 DataTable + 450ms 防抖自动保存
        void ApplyPneumaticInlineField(string recordId, string field, string newValue, bool triggerModelLookup) {
            if (string.IsNullOrEmpty(recordId)) return;
            var r = project.Records.FirstOrDefault(x => x.Id == recordId);
            if (r == null) return;
            if (!dirty) undo.Push(LineupEntry.Clone(project));

            if (field == "气缸描述联动") {
                LineupPneumaticOps.SyncCylinderDescription(project, r, r.Description, newValue);
            } else if (field == "编号") {
                string oldNum = r.Number;
                r.Number = newValue;
                if (r.Category == "阀片" && oldNum != r.Number) {
                    try { LineupHierarchy.RenumberChildren(project, r); } catch { }
                } else if (r.Category == "气缸" && oldNum != r.Number) {
                    foreach (var s in project.Records.Where(x => x.CylinderId == r.Id))
                        if (!string.IsNullOrWhiteSpace(s.Position)) s.Number = r.Number + s.Position;
                }
            } else if (field == "功能参数") r.Parameters = newValue;
            else if (field == "描述") r.Description = newValue;
            else if (field == "型号") {
                r.Model = newValue;
                modelFieldEdited = true;
                if (triggerModelLookup) {
                    var matches = catalog.Match(r.Category, r.Model).ToList();
                    if (matches.Count == 1) {
                        if (string.IsNullOrWhiteSpace(r.Brand)) r.Brand = matches[0].Brand;
                        if (string.IsNullOrWhiteSpace(r.Connector)) r.Connector = matches[0].Connector;
                    }
                }
            } else if (field == "品牌") { r.Brand = newValue; modelFieldEdited = true; }
            else if (field == "原位") r.Home = newValue;
            else if (field == "安装方式") r.Mounting = newValue;
            else if (field == "传感器接口类型") { r.Connector = newValue; modelFieldEdited = true; }
            else if (field == "备注") r.Notes = newValue;

            bool cascaded = field == "气缸描述联动" || field == "编号";
            SyncDataTableRowsFromProject(cascaded ? null : recordId);
            dirty = true;
            if (btnUndoFooter != null) btnUndoFooter.Enabled = undo.Count > 0;
            saver.Stop();
            saver.Start();
        }

        // 将 project.Records 中的字段值增量同步回底层 DataTable（普通单格修改 O(1) 仅同步 1 行，级联改名才扫全表）
        void SyncDataTableRowsFromProject(string onlyRecordId = null) {
            loading = true;
            try {
                if (!string.IsNullOrEmpty(onlyRecordId)) {
                    DataRow row;
                    if (dataRowById.TryGetValue(onlyRecordId, out row)) {
                        var rec = project.Records.FirstOrDefault(x => x.Id == onlyRecordId);
                        if (rec != null) SyncSingleDataRow(row, rec);
                    }
                    return;
                }
                var byId = project.Records.ToDictionary(x => x.Id);
                foreach (DataRow row in data.Rows) {
                    LineupRecord rec;
                    if (!byId.TryGetValue(Convert.ToString(row["Id"]), out rec)) continue;
                    SyncSingleDataRow(row, rec);
                }
            } finally { loading = false; }
        }

        static void SyncSingleDataRow(DataRow row, LineupRecord rec) {
            string[] cells = LineupTableData.Cells(rec);
            for (int i = 0; i < LineupTableData.Headers.Length; i++) {
                string h = LineupTableData.Headers[i];
                string val = cells[i] ?? "";
                if (!string.Equals(Convert.ToString(row[h]), val, StringComparison.Ordinal)) row[h] = val;
            }
        }

        void AddRecordAndOpenMode(string cat, int targetMode) {
            var next = Snapshot();
            var r = new LineupRecord { Category = cat, Number = LineupNumbering.Next(next, cat) };
            next.Records.Add(r);
            activeViewMode = targetMode;
            category.SelectedItem = cat;
            Commit(next, r.Id, "已新增 " + r.Number);
            // 把光标放到「描述」格，选中后直接打字即可输入
            foreach (DataGridViewRow row in grid.Rows) {
                if (Convert.ToString(((DataRowView)row.DataBoundItem)["Id"]) == r.Id && grid.Columns["描述"].Visible) {
                    grid.ClearSelection();
                    grid.CurrentCell = row.Cells["描述"];
                    row.Cells["描述"].Selected = true;
                    break;
                }
            }
        }

        void BindCurrentCadSelectionToRecord(string recordId) {
            BindCurrentCadSelectionToRecords(new[] { recordId });
        }

        void BindCurrentCadSelectionToRecords(IList<string> recordIds) {
            Ensure();
            if (assembly == null) throw new InvalidOperationException("当前未连接天工 CAD 装配。");
            var next = Snapshot();
            var targets = NormalizeBindQueue(next, recordIds);
            if (targets.Count == 0) throw new InvalidOperationException("找不到待绑定的记录。");

            if (targets.Count == 1) {
                string recordId = targets[0];
                var rec = next.Records.Single(x => x.Id == recordId);
                var bind = new LineupRecord();
                LineupCad.Capture(assembly, bind);
                var conflict = next.Records.FirstOrDefault(x => x.Id != recordId && x.ReferenceKey == bind.ReferenceKey);
                if (conflict != null) throw new InvalidOperationException("所选三维模型已绑定到 " + conflict.Number + "。");
                rec.ReferenceKey = bind.ReferenceKey;
                rec.SourceFile = bind.SourceFile;
                rec.InstancePath = bind.InstancePath;
                Commit(next, recordId, "已绑定 3D 模型到 " + rec.Number);
                try { assembly.SelectSet.RemoveAll(); } catch { }
                return;
            }

            int selCount = assembly.SelectSet.Count;
            if (selCount == 0)
                throw new InvalidOperationException("CAD 中尚未选中模型；建议使用「进入 3D 依次点选绑定选中 " + targets.Count + " 行」。");
            if (selCount == 1)
                throw new InvalidOperationException("当前表格已选中 " + targets.Count + " 行，但 CAD 仅选中 1 个模型；请在 CAD 中多选对应模型，或改用「进入 3D 依次点选绑定选中 " + targets.Count + " 行」。");

            var captured = new List<LineupRecord>(selCount);
            for (int i = 1; i <= selCount; i++) {
                var item = new LineupRecord();
                LineupCad.CaptureOccurrence(assembly.SelectSet.Item(i), item);
                captured.Add(item);
            }
            int boundCount = ApplySequentialCadBindings(next, targets, captured);
            string lastId = targets[boundCount - 1];
            string msg = "已按顺序绑定 " + boundCount + " / " + targets.Count + " 行 3D 模型";
            if (captured.Count > targets.Count) msg += "（CAD 多选的其余 " + (captured.Count - targets.Count) + " 个模型未分配）";
            Commit(next, lastId, msg);
            try { assembly.SelectSet.RemoveAll(); } catch { }
            ShowToast("✓ " + msg, Ui.Ok, "撤销", (s, e) => Try(Undo));
        }

        void BindCurrentCadSelectionToIsland(string islandKey) {
            Ensure();
            if (assembly == null) throw new InvalidOperationException("当前未连接天工 CAD 装配。");
            var next = Snapshot();
            var bind = new LineupRecord();
            LineupCad.Capture(assembly, bind);
            var cfg = LineupNumbering.IslandEntry(next, islandKey);
            LineupPneumaticOps.UpdateIsland(next, islandKey, cfg == null ? "阀岛" : cfg.Kind, cfg == null ? "" : cfg.Model, bind);
            Commit(next, null, "已将 CAD 当前选中件绑定为阀岛 " + islandKey);
            try { assembly.SelectSet.RemoveAll(); } catch { }
        }

        void AddCadSelectedAsCylinderToIsland(string islandKey) {
            Ensure();
            if (assembly == null) throw new InvalidOperationException("当前未连接天工 CAD 装配。");
            if (assembly.SelectSet.Count == 0) throw new InvalidOperationException("请先在 CAD 中选择至少一个气缸零件或子装配。");
            var captured = CaptureEntrySelections();
            var next = Snapshot();
            int added = 0;
            string lastId = null;
            foreach (var item in captured) {
                var res = LineupPneumaticOps.AddCylinderBundle(next, islandKey, true, 1, false, null, item);
                lastId = res.CylinderId;
                added++;
            }
            if (added > 0) {
                Commit(next, lastId, "已从 CAD 加入 " + added + " 支气缸到 " + islandKey + "（自动配齐阀片与 H/W）", true);
                try { assembly.SelectSet.RemoveAll(); } catch { }
            }
        }

        void StartContinuousCylinderForIsland(string islandKey) {
            string pre; int num;
            if (LineupPneumaticOps.ParseIslandKey(islandKey, out pre, out num)) {
                var cfg = LineupNumbering.IslandEntry(project, islandKey);
                ConfigureNumbering(pre, num, cfg == null ? "" : cfg.Model, cfg == null ? "阀岛" : cfg.Kind);
            }
            category.SelectedItem = "气缸";
            WriteEntry(new LineupEntryPreset { Category = "气缸", Home = "缩回", Mounting = "水平安装", GenerateSensors = true });
            StartContinuous("add");
        }

        void StartContinuousBindForIsland(string islandKey) {
            var chs = LineupPneumaticOps.BuildChannelRows(project, islandKey);
            var unboundIds = chs.Where(c => c.Cylinder != null && string.IsNullOrEmpty(c.Cylinder.ReferenceKey))
                .Select(c => c.Cylinder.Id).Distinct().ToList();
            if (unboundIds.Count == 0) throw new InvalidOperationException("阀岛 " + islandKey + " 下所有气缸均已绑定三维模型。");
            StartSequentialBindRecords(unboundIds, true);
        }

        // 从步骤 ③ 自检表跳转回步骤 ① 对应的方案 A 分区与精确行
        void JumpToRecordFromAudit(string id) {
            search.Clear();
            searchDebounce.Stop();
            if (pneumaticPanel != null) pneumaticPanel.SetSearchFilter("");
            GoStep(0);
            var r = project.Records.FirstOrDefault(x => x.Id == id);
            if (r == null) return;
            string cat = LineupTableData.Category(r.Category);
            if (cat == "普通传感器") {
                SetViewMode(1);
                SelectId(id);
            } else if (cat == "其他电气件") {
                SetViewMode(2);
                SelectId(id);
            } else if (cat == "气缸" || cat == "阀片" || cat == "气缸传感器") {
                string ik = LineupNumbering.IslandKey(r.Number);
                if (ik.Length > 0 && pneumaticPanel != null) {
                    SetViewMode(0);
                    pneumaticPanel.SelectSubTab(cat == "气缸传感器" ? 3 : (cat == "阀片" ? 2 : 0));
                    pneumaticPanel.RefreshFromProject(id);
                } else {
                    SetViewMode(3);
                    category.SelectedIndex = 0;
                    SelectId(id);
                }
            } else {
                SetViewMode(3);
                category.SelectedIndex = 0;
                SelectId(id);
            }
        }

        void AddMenuItem(ContextMenuStrip menu, string title, Action action) {
            var item = new ToolStripMenuItem(title);
            item.Click += (s, e) => Try(action);
            menu.Items.Add(item);
        }

        void AddSubItem(ToolStripMenuItem parent, string title, Action action) {
            var item = new ToolStripMenuItem(title);
            item.Click += (s, e) => Try(action);
            parent.DropDownItems.Add(item);
        }

        // 一次计算规范自检并同步刷新顶部三步胶囊、工位抬头按钮和步骤①智能引导条
        void RefreshHeaderAndGuide(LineupAuditResult precomputedAudit = null) {
            if (project == null) return;
            try {
                int total = project.Records.Count(r => BomCategories.Contains(LineupTableData.Category(r.Category)));
                var pages = project.CadPages ?? new List<LineupCadPage>();
                int allPts = pages.Sum(pg => pg.Points == null ? 0 : pg.Points.Count);
                int okPts = pages.Sum(pg => pg.Points == null ? 0 : pg.Points.Count(p => p.State == "ok"));
                LineupAuditResult audit = total > 0 ? (precomputedAudit ?? LineupAudit.Run(project)) : null;

                if (stepPills[0] != null) {
                    stepPills[0].SetState(currentStep == 0, total > 0, false, total > 0 ? total + "项" : "");
                    stepPills[1].SetState(currentStep == 1, pages.Count > 0 && allPts > 0 && okPts == allPts, false, pages.Count > 0 ? pages.Count + "页" : "可选");
                    string exBadge = total == 0 ? "" : (audit.ErrorCount > 0 ? audit.ErrorCount + "错" : (audit.WarningCount > 0 ? audit.WarningCount + "提醒" : "通过"));
                    stepPills[2].SetState(currentStep == 2, total > 0 && audit.ErrorCount == 0 && audit.WarningCount == 0, total > 0 && audit.ErrorCount > 0, exBadge);
                }

                if (stationPillBtn != null) {
                    string head = string.Join(" · ", new[] {
                        (project.ProjectCode ?? "").Trim(),
                        !string.IsNullOrWhiteSpace(project.NumberPrefix) ? project.NumberPrefix.Trim() : (project.StationCode ?? "").Trim(),
                        "Y" + Math.Max(1, project.ActiveIsland)
                    }.Where(s => s.Length > 0));
                    stationPillBtn.Text = (head.Length > 0 ? head : "设置抬头 / 阀岛") + " ▾";
                    Ui.FitButton(stationPillBtn);
                }

                RefreshGuideBanner(audit);
            } catch (Exception ex) { Log.Write("LineupHeader", ex.Message); }
        }

        // 一键组合配齐：单次提交同时补齐缺失阀片 + H/W 传感器 + 同步备注，支持单步撤销
        void AutoCompleteValvesAndSensors() {
            var p = Snapshot();
            var selected = SelectedIds();
            var ids = p.Records.Where(r => r.Category == "气缸" && selected.Contains(r.Id) && !string.IsNullOrWhiteSpace(r.Number)).Select(r => r.Id).ToList();
            if (ids.Count == 0) ids = p.Records.Where(r => r.Category == "气缸" && !string.IsNullOrWhiteSpace(r.Number)).Select(r => r.Id).ToList();
            if (ids.Count == 0) throw new InvalidOperationException("请先录入并编号气缸，再使用一键配齐。");
            RunAutoCompleteCombo(ids, false);
        }

        void AutoCompleteAllForAudit() {
            var p = Snapshot();
            var ids = p.Records.Where(r => r.Category == "气缸" && !string.IsNullOrWhiteSpace(r.Number)).Select(r => r.Id).ToList();
            if (ids.Count == 0) throw new InvalidOperationException("清单中尚无已编号的气缸。");
            RunAutoCompleteCombo(ids, true);
        }

        void RunAutoCompleteCombo(List<string> cylIds, bool fileOnly) {
            var next = Snapshot();
            int vAdded = LineupNumbering.GenerateValves(next, cylIds);
            int sAdded = LineupNumbering.SensorPairs(next, cylIds);
            int nSynced = SyncSensorNotesOn(next);
            if (vAdded == 0 && sAdded == 0 && nSynced == 0) {
                ShowToast("所有气缸的阀片、H/W 传感器与备注均已齐全。", Ui.Ok);
                return;
            }
            var parts = new List<string>();
            if (vAdded > 0) parts.Add("阀片 " + vAdded + " 条");
            if (sAdded > 0) parts.Add("H/W 传感器 " + sAdded + " 条");
            if (nSynced > 0) parts.Add("同步备注 " + nSynced + " 条");
            string msg = "已一键补齐：" + string.Join("、", parts);
            if (fileOnly) CommitFileOnly(next, CurrentId(), msg);
            else Commit(next, CurrentId(), msg);
            ShowToast(msg, Ui.Ok, "撤销", (s, e) => Try(Undo));
        }

        static int SyncSensorNotesOn(LineupProject next) {
            var byId = next.Records.ToDictionary(r => r.Id);
            var byNum = new Dictionary<string, LineupRecord>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in next.Records.Where(r => r.Category == "气缸" && !string.IsNullOrWhiteSpace(r.Number)))
                if (!byNum.ContainsKey(c.Number.Trim())) byNum[c.Number.Trim()] = c;
            int updated = 0;
            foreach (var s in next.Records.Where(r => r.Category == "气缸传感器")) {
                LineupRecord parent = null;
                if (!string.IsNullOrEmpty(s.CylinderId)) byId.TryGetValue(s.CylinderId, out parent);
                if (parent == null) {
                    var m = System.Text.RegularExpressions.Regex.Match((s.Number ?? "").Trim(), @"^(.+?)[HWM][0-9]*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (m.Success) byNum.TryGetValue(m.Groups[1].Value.Trim(), out parent);
                }
                if (parent != null && !string.IsNullOrWhiteSpace(parent.Notes) && s.Notes != parent.Notes) { s.Notes = parent.Notes; updated++; }
            }
            return updated;
        }

        internal void ConfigureNumbering(string prefix, int island, string model) { ConfigureNumbering(prefix, island, model, null); }
        internal void ConfigureNumbering(string prefix, int island, string model, string kind) {
            var next = Snapshot();
            LineupNumbering.SetIsland(next, prefix, island, model, kind);
            if (string.IsNullOrWhiteSpace(next.StationCode)) next.StationCode = prefix.Trim();
            Commit(next, CurrentId(), "编号与阀岛设置已保存");
            if (entryPrefix != null) { entryPrefix.Text = prefix; entryIsland.Value = Math.Min(9999, island); RefreshEntryParents(); }
        }

        internal void AssignValvesToIsland(string key, IEnumerable<string> ids) {
            var next = Snapshot();
            var target = next.Islands.FirstOrDefault(i => i.Key == key);
            if (target == null) throw new InvalidOperationException("请先在「设置抬头 / 阀岛」中建立目标阀岛。");
            var selected = next.Records.Where(r => ids.Contains(r.Id)).ToArray();
            if (selected.Length == 0 || selected.Any(r => r.Category != "阀片")) throw new InvalidOperationException("请只选择需要分配的阀片行。");
            var match = System.Text.RegularExpressions.Regex.Match(key, @"^(.+)-Y([0-9]+)$");
            if (!match.Success) throw new InvalidOperationException("阀岛编号无效。");
            var owner = next.Records.FirstOrDefault(x => x.Category == "阀岛" && x.Number == key);
            if (owner == null) { owner = new LineupRecord { Category = "阀岛", Number = key, Model = target.Model }; next.Records.Add(owner); }
            string prefix = next.NumberPrefix;
            int active = next.ActiveIsland;
            next.NumberPrefix = match.Groups[1].Value;
            next.ActiveIsland = int.Parse(match.Groups[2].Value);
            foreach (var r in selected) {
                if (!string.Equals(LineupNumbering.IslandKey(r.Number), key, StringComparison.OrdinalIgnoreCase))
                    r.Number = LineupNumbering.Next(next, "阀片");
                r.IslandId = owner.Id;
                LineupHierarchy.RenumberChildren(next, r);
            }
            next.NumberPrefix = prefix;
            next.ActiveIsland = active;
            Commit(next, CurrentId(), "已将选中阀片分配到 " + key);
        }

        void AssignIslandDialog() {
            var ids = SelectedIds();
            if (ids.Count == 0 || Snapshot().Records.Where(r => ids.Contains(r.Id)).Any(r => r.Category != "阀片"))
                throw new InvalidOperationException("请先选中一个或多个阀片行。");
            if (project.Islands.Count == 0)
                throw new InvalidOperationException("请先点击左侧「设置抬头 / 阀岛…」建立目标阀岛。");
            using (var d = new Form()) {
                Ui.Shell(d, "切换选中阀片的所属阀岛", 480, 220, 420, 200);
                d.FormBorderStyle = FormBorderStyle.FixedDialog;
                var body = new Panel { Dock = DockStyle.Fill, Padding = Ui.SPad(18, 16, 18, 10), BackColor = Ui.Bg };
                var combo = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };
                foreach (var island in project.Islands) combo.Items.Add(island.Key + " · " + (string.IsNullOrWhiteSpace(island.Kind) ? "阀岛" : island.Kind) + " · " + island.Model);
                combo.SelectedIndex = 0;
                var note = new Label {
                    Dock = DockStyle.Fill, ForeColor = Ui.Muted, Font = Ui.F8, Padding = Ui.SPad(0, 10, 0, 0),
                    Text = "选中阀片及其下属气缸、传感器会联动更新编号；零件绑定与阀片自身型号保持不变，操作可撤销。"
                };
                var footer = new ActionBar { Dock = DockStyle.Bottom, Height = Ui.S(56), Padding = Ui.SPad(18, 0, 18, 0) };
                var ok = Ui.Primary("确定分配", 104, 34); ok.DialogResult = DialogResult.OK;
                var cancel = Ui.Secondary("取消", 76, 34); cancel.DialogResult = DialogResult.Cancel;
                footer.AddRight(cancel); footer.AddRight(ok);
                d.AcceptButton = ok; d.CancelButton = cancel;
                body.Controls.Add(note); body.Controls.Add(combo);
                d.Controls.Add(body); d.Controls.Add(footer);
                if (d.ShowDialog(this) == DialogResult.OK) AssignValvesToIsland(project.Islands[combo.SelectedIndex].Key, ids);
            }
        }

        bool ReadyNumbering(string kind) {
            if (kind != "气缸" && kind != "阀片") return true;
            return !string.IsNullOrWhiteSpace(project.NumberPrefix) || ShowNumberSettings();
        }

        bool ShowNumberSettings() {
            using (var dialog = new Form()) {
                Ui.Shell(dialog, "项目抬头与阀岛设置", 520, 430, 460, 400);
                dialog.FormBorderStyle = FormBorderStyle.FixedDialog;

                var body = new Panel { Dock = DockStyle.Fill, Padding = Ui.SPad(20, 16, 20, 10), BackColor = Ui.Bg };
                var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, BackColor = Ui.Bg };
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Ui.S(136)));
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

                var prefix = new TextBox { Text = project.NumberPrefix, Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle };
                var island = new NumericUpDown { Minimum = 1, Maximum = 9999, Value = Math.Max(1, project.ActiveIsland), Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle };
                var kindBox = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };
                kindBox.Items.AddRange(LineupPresets.ValveKinds.Cast<object>().ToArray());
                kindBox.SelectedIndex = 0;
                var model = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown, AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.ListItems };
                model.Items.AddRange(catalog.Entries.Where(x => x.Category == "阀岛").Select(x => x.Model).Distinct().OrderBy(x => x).Cast<object>().ToArray());
                var projCode = new TextBox { Text = project.ProjectCode ?? "", Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle };
                var lineCode = new TextBox { Text = string.IsNullOrWhiteSpace(project.LineCode) ? "L1" : project.LineCode, Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle };
                var designer = new TextBox { Text = project.Designer ?? "", Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle };
                var editor = new TextBox { Text = project.Editor ?? "", Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle };

                Action update = () => {
                    var key = prefix.Text.Trim() + "-Y" + island.Value;
                    var entry = (project.Islands ?? new List<LineupIsland>()).FirstOrDefault(i => string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase));
                    model.Text = entry == null ? "" : entry.Model;
                    string k = entry == null || string.IsNullOrWhiteSpace(entry.Kind) ? "阀岛" : entry.Kind;
                    int idx = kindBox.Items.IndexOf(k);
                    kindBox.SelectedIndex = idx >= 0 ? idx : 0;
                };
                prefix.TextChanged += (s, e) => update();
                island.ValueChanged += (s, e) => update();
                update();

                Action<int, string, Control> row = (r, l, c) => {
                    layout.RowStyles.Add(new RowStyle(SizeType.Absolute, Ui.S(34)));
                    c.Margin = Ui.SPad(0, 3, 0, 3);
                    layout.Controls.Add(new Label { Text = l, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Ui.Text2, Font = Ui.F9B }, 0, r);
                    layout.Controls.Add(c, 1, r);
                };
                row(0, "工位号 / 编号前缀", prefix);
                row(1, "当前阀岛编号 (Y*)", island);
                row(2, "阀组控制方式", kindBox);
                row(3, "阀岛 / 汇流排型号", model);
                row(4, "项目编号", projCode);
                row(5, "线体号", lineCode);
                row(6, "机械设计人", designer);
                row(7, "编辑人", editor);

                var note = new Label {
                    Text = "例：工位号 OK170 + Y1 → 自动生成 OK170-Y1C01 / OK170-Y1V01；同一 Y 阀岛的阀片在导出 Excel 时自动合并标注。",
                    Dock = DockStyle.Bottom, Height = Ui.S(38), ForeColor = Ui.Muted, Font = Ui.F8
                };
                body.Controls.Add(layout);
                body.Controls.Add(note);

                var footer = new ActionBar { Dock = DockStyle.Bottom, Height = Ui.S(58), Padding = Ui.SPad(20, 0, 20, 0) };
                var ok = Ui.Primary("保存设置", 108, 34); ok.DialogResult = DialogResult.OK;
                var cancel = Ui.Secondary("取消", 76, 34); cancel.DialogResult = DialogResult.Cancel;
                footer.AddRight(cancel); footer.AddRight(ok);
                dialog.AcceptButton = ok; dialog.CancelButton = cancel;
                dialog.Controls.Add(body);
                dialog.Controls.Add(footer);

                if (dialog.ShowDialog(this) != DialogResult.OK) return false;
                var next = Snapshot();
                next.ProjectCode = projCode.Text.Trim();
                next.StationCode = prefix.Text.Trim();
                next.LineCode = string.IsNullOrWhiteSpace(lineCode.Text) ? "L1" : lineCode.Text.Trim();
                next.Designer = designer.Text.Trim();
                next.Editor = editor.Text.Trim();
                LineupNumbering.SetIsland(next, prefix.Text, (int)island.Value, model.Text, Convert.ToString(kindBox.SelectedItem));
                Commit(next, CurrentId(), "抬头与阀岛设置已保存");
                if (entryPrefix != null) { entryPrefix.Text = prefix.Text.Trim(); entryIsland.Value = Math.Min(9999, island.Value); RefreshEntryParents(); }
                if (currentStep == 2 && exportPanel != null) exportPanel.RefreshFromProject();
                return true;
            }
        }

        void NumberSelected() {
            var ids = SelectedIds();
            if (ids.Count == 0) throw new InvalidOperationException("请先选择需要自动编号的行。");
            var next = Snapshot();
            if (next.Records.Any(r => ids.Contains(r.Id) && (r.Category == "气缸" || r.Category == "阀片")) && !ReadyNumbering("气缸")) return;
            next = Snapshot();
            LineupNumbering.FillMissing(next, next.Records.Where(r => ids.Contains(r.Id)));
            Commit(next, CurrentId(), "已补全空编号，已有编号保持不变");
        }

        internal int GenerateValvesFor(IEnumerable<string> cylinderIds) {
            var next = Snapshot();
            int added = LineupNumbering.GenerateValves(next, cylinderIds);
            Commit(next, null, "已生成 " + added + " 条配套阀片并建立关联");
            if (added > 0) category.SelectedItem = "阀片";
            return added;
        }

        void GenerateValvesForSelected() {
            var p = Snapshot();
            var selected = SelectedIds();
            var ids = p.Records.Where(r => r.Category == "气缸" && selected.Contains(r.Id) && !string.IsNullOrWhiteSpace(r.Number)).Select(r => r.Id).ToList();
            if (ids.Count == 0) {
                ids = p.Records.Where(r => r.Category == "气缸" && !string.IsNullOrWhiteSpace(r.Number)).Select(r => r.Id).ToList();
                if (ids.Count == 0) throw new InvalidOperationException("请先录入并编号气缸。");
            }
            GenerateValvesFor(ids);
        }

        internal int GenerateSensorPairsFor(IEnumerable<string> ids) {
            var next = Snapshot();
            int count = LineupNumbering.SensorPairs(next, ids);
            Commit(next, null, "已生成 " + count + " 条 H/W 传感器");
            category.SelectedItem = "气缸传感器";
            return count;
        }

        void GenerateSensorPairs() {
            var p = Snapshot();
            var selected = SelectedIds();
            var ids = p.Records.Where(r => r.Category == "气缸" && selected.Contains(r.Id)).Select(r => r.Id).ToList();
            if (ids.Count == 0) {
                var cylinders = p.Records.Where(r => r.Category == "气缸" && !string.IsNullOrWhiteSpace(r.Number)).ToList();
                if (cylinders.Count == 0) throw new InvalidOperationException("请先录入并编号气缸。");
                using (var dialog = new Form()) {
                    Ui.Shell(dialog, "选择气缸生成 H/W 传感器", 520, 400, 420, 320);
                    var clb = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, BorderStyle = BorderStyle.FixedSingle };
                    foreach (var c in cylinders) clb.Items.Add(c, true);
                    var body = new Panel { Dock = DockStyle.Fill, Padding = Ui.SPad(16, 12, 16, 8), BackColor = Ui.Bg };
                    body.Controls.Add(clb);
                    var footer = new ActionBar { Dock = DockStyle.Bottom, Height = Ui.S(56), Padding = Ui.SPad(16, 0, 16, 0) };
                    var ok = Ui.Primary("生成所选 H/W", 126, 34); ok.DialogResult = DialogResult.OK;
                    var cancel = Ui.Secondary("取消", 76, 34); cancel.DialogResult = DialogResult.Cancel;
                    footer.AddRight(cancel); footer.AddRight(ok);
                    dialog.AcceptButton = ok; dialog.CancelButton = cancel;
                    dialog.Controls.Add(body); dialog.Controls.Add(footer);
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    for (int i = 0; i < clb.Items.Count; i++) if (clb.GetItemChecked(i)) ids.Add(cylinders[i].Id);
                    if (ids.Count == 0) return;
                }
            }
            GenerateSensorPairsFor(ids);
        }

        void GenerateMultiSensorsForSelected() {
            var p = Snapshot();
            var selected = SelectedIds();
            var ids = p.Records.Where(r => r.Category == "气缸" && selected.Contains(r.Id) && !string.IsNullOrWhiteSpace(r.Number)).Select(r => r.Id).ToList();
            if (ids.Count == 0) throw new InvalidOperationException("请先选中一个或多个已编号的气缸行。");
            var next = Snapshot();
            int count = LineupNumbering.SensorPairsMulti(next, ids, 2);
            Commit(next, null, "已生成 " + count + " 条双组传感器 (H1/W1/H2/W2)");
            category.SelectedItem = "气缸传感器";
        }

        void SyncSensorNotes() {
            var next = Snapshot();
            int updated = SyncSensorNotesOn(next);
            if (updated > 0) Commit(next, CurrentId(), "已同步 " + updated + " 条气缸传感器备注");
            else ShowToast("所有气缸传感器备注均已与气缸一致。", Ui.Ok);
        }

        void BatchSetNotesDialog() {
            var ids = SelectedIds();
            if (ids.Count == 0) throw new InvalidOperationException("请先选择要设置备注的行。");
            string val = Ui.Prompt(this, "批量设置备注 / 模块名", "将选中 " + ids.Count + " 行的备注统一设为：", "");
            if (val == null) return;
            var next = Snapshot();
            foreach (var r in next.Records.Where(x => ids.Contains(x.Id))) {
                r.Notes = val.Trim();
                if (string.IsNullOrWhiteSpace(r.Mechanism)) r.Mechanism = val.Trim();
            }
            Commit(next, ids[0], "已更新 " + ids.Count + " 行备注");
        }

        void ScanAssemblyDialog() {
            Ensure();
            if (assembly == null) throw new InvalidOperationException("当前未连接天工 CAD 装配。");
            SaveDraft(false);
            status.Text = "正在扫描当前装配中的气缸、阀岛与传感器…";
            Application.DoEvents();
            var found = LineupCad.ScanAssembly(assembly, project, catalog);
            var candidates = found.Where(x => !x.AlreadyBound).ToList();
            if (candidates.Count == 0) {
                string msg = found.Count == 0
                    ? "未自动匹配到内置型号库的气动/电气件，可用「在 CAD 点选加入」手动点选。"
                    : "自动识别到的 " + found.Count + " 个器件均已录入并绑定。";
                UpdateStatus(msg);
                ShowToast(msg, Ui.Muted);
                return;
            }
            using (var d = new Form()) {
                Ui.Shell(d, "扫描到 " + candidates.Count + " 个未录入器件", 760, 500, 560, 380);
                d.FormBorderStyle = FormBorderStyle.Sizable;
                var clb = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, Font = Ui.F9, BorderStyle = BorderStyle.FixedSingle, IntegralHeight = false };
                foreach (var c in candidates)
                    clb.Items.Add("[" + c.Category + "] " + c.Model + (string.IsNullOrEmpty(c.Brand) ? "" : " (" + c.Brand + ")") + "  ←  " + c.DisplayPath, true);

                var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = Ui.S(38), Padding = Ui.SPad(16, 6, 16, 2), BackColor = Ui.Bg };
                var selAll = Ui.Small("全选"); ((FlatButton)selAll).SetFore(Ui.Text2);
                var selNone = Ui.Small("全不选"); ((FlatButton)selNone).SetFore(Ui.Text2);
                selAll.Click += (s, e) => { for (int i = 0; i < clb.Items.Count; i++) clb.SetItemChecked(i, true); };
                selNone.Click += (s, e) => { for (int i = 0; i < clb.Items.Count; i++) clb.SetItemChecked(i, false); };
                var tip = new Label { Text = "取消勾选不需要的件，确定后自动编号并绑定三维模型：", AutoSize = true, ForeColor = Ui.Muted, Font = Ui.F8, Margin = Ui.SPad(8, 5, 0, 0) };
                top.Controls.AddRange(new Control[] { selAll, selNone, tip });

                var body = new Panel { Dock = DockStyle.Fill, Padding = Ui.SPad(16, 4, 16, 8), BackColor = Ui.Bg };
                body.Controls.Add(clb);

                var footer = new ActionBar { Dock = DockStyle.Bottom, Height = Ui.S(58), Padding = Ui.SPad(16, 0, 16, 0) };
                var ok = Ui.Primary("加入清单并自动编号", 158, 36); ok.DialogResult = DialogResult.OK;
                var cancel = Ui.Secondary("取消", 76, 34); cancel.DialogResult = DialogResult.Cancel;
                footer.AddRight(cancel); footer.AddRight(ok);
                d.AcceptButton = ok; d.CancelButton = cancel;
                d.Controls.Add(body); d.Controls.Add(top); d.Controls.Add(footer);

                if (d.ShowDialog(this) != DialogResult.OK) return;
                var picked = new List<LineupCadScanItem>();
                for (int i = 0; i < clb.Items.Count; i++) if (clb.GetItemChecked(i)) picked.Add(candidates[i]);
                if (picked.Count == 0) return;
                if (picked.Any(x => x.Category == "气缸" || x.Category == "阀片") && !ReadyNumbering("气缸")) return;
                var next = Snapshot();
                int added = 0;
                foreach (var c in picked) {
                    string cat = LineupTableData.Categories.Contains(c.Category) ? c.Category : "其他电气件";
                    string num = cat == "气缸传感器" ? "" : LineupNumbering.Next(next, cat);
                    next.Records.Add(new LineupRecord {
                        Category = cat, Number = num, Description = c.Description, Model = c.Model, Brand = c.Brand, Connector = c.Connector,
                        Mechanism = c.Mechanism, Notes = c.Mechanism, ReferenceKey = c.ReferenceKey, SourceFile = c.SourceFile, InstancePath = c.InstancePath
                    });
                    added++;
                }
                Commit(next, null, "已自动识别并加入 " + added + " 个三维器件", true);
                category.SelectedIndex = 0;
                ShowToast("已自动加入 " + added + " 个器件，可点「一键配齐」补齐阀片与 H/W。", Ui.Ok);
            }
        }

        void ImportXlsxDialog() {
            using (var d = new OpenFileDialog { Filter = "Excel Lineup 工作簿|*.xlsx;*.xlsm;*.xls", Title = "选择 Lineup Excel 文件（支持明文与加密表格）" }) {
                if (d.ShowDialog(this) == DialogResult.OK) {
                    status.Text = "正在读取 Excel 工作簿（加密表格将通过本机 Excel / WPS 只读加载）…";
                    Application.DoEvents();
                    var prevCursor = Cursor;
                    Cursor = Cursors.WaitCursor;
                    try {
                        ImportXlsx(d.FileName);
                    } finally {
                        Cursor = prevCursor;
                    }
                }
            }
        }

        // 无多余中间弹窗的直接导出（从步骤③或底部快捷按钮直接弹出文件保存框）
        void QuickExportXlsxDirect(IWin32Window owner) {
            owner = owner ?? this;
            SaveDraft(false);
            var opts = exportPanel != null
                ? exportPanel.CurrentExportOptions()
                : new LineupXlsxOptions { Export11Columns = project.Export11Columns, IncludeCadPages = true, MarkBomChanges = project.MarkBomChanges };
            var p = Snapshot();
            string proj = string.IsNullOrWhiteSpace(p.ProjectCode) ? "" : p.ProjectCode.Trim();
            string sta = !string.IsNullOrWhiteSpace(p.StationCode) ? p.StationCode.Trim() : (p.NumberPrefix ?? "").Trim();
            string rev = LineupRevision.CurrentVersion(p);
            string defName = (proj.Length > 0 ? proj + "-" : "") + (sta.Length > 0 ? sta + "-" : Path.GetFileNameWithoutExtension(file) + "-") + "line up-" + rev + "-" + DateTime.Now.ToString("yyyyMMdd") + ".xlsx";
            using (var sfd = new SaveFileDialog { Filter = "Excel 工作簿|*.xlsx", FileName = defName, InitialDirectory = Path.GetDirectoryName(file) }) {
                if (sfd.ShowDialog(owner) != DialogResult.OK) return;
                var sum = ExportXlsx(sfd.FileName, opts);
                string changeLine = (sum.BomAddedRows > 0 || sum.BomRemovedRows > 0)
                    ? "\n· 变更标记：新增红底 " + sum.BomAddedRows + " 条，取消红底+删除线保留 " + sum.BomRemovedRows + " 条"
                    : "";
                string msg = "导出成功！\n\n文件：" + sum.OutputPath
                    + "\n· 更新日志：共 " + sum.ChangelogRows + " 条版本记录（当前 " + rev + "）"
                    + "\n· BOM 明细：" + sum.BomRows + " 条（" + (opts.Export11Columns ? "模板标准 11 列" : "精简 10 列") + "）"
                    + changeLine
                    + "\n· CAD 位置图：" + sum.CadPages + " 页，" + sum.CadCallouts + " 处标注，" + sum.CadArrows + " 条红色箭头（从图指向单元格）"
                    + "\n· 签字页自检：错误 " + sum.AuditErrors + " 项，提醒 " + sum.AuditWarnings + " 项"
                    + "\n\n是否立即打开该 Excel 文件？";
                UpdateStatus("已导出：" + Path.GetFileName(sum.OutputPath));
                if (MessageBox.Show(owner, msg, "导出完成", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes) {
                    try { Process.Start(new ProcessStartInfo(sum.OutputPath) { UseShellExecute = true }); } catch { }
                }
            }
        }

        // ==================== 紧凑悬浮点选条（兼容 440×180 与“绑定并下一条”测试约定） ====================
        void InitializePickingPanel() {
            pickingPanel = new Panel { Dock = DockStyle.Fill, Visible = false, BackColor = Ui.Bg, Padding = Ui.SPad(14, 10, 14, 10) };

            pickingTitle = new Label {
                Dock = DockStyle.Top, Height = Ui.S(22), Font = Ui.F9B, ForeColor = Ui.Accent, BackColor = Ui.Bg,
                Text = "● CAD 点选模式"
            };
            pickingProgress = new SlimProgress {
                Dock = DockStyle.Top, Height = Ui.S(6), Visible = false, Margin = Ui.SPad(0, 2, 0, 4)
            };
            pickingLabel = new Label {
                Dock = DockStyle.Fill, Padding = Ui.SPad(0, 6, 0, 4), AutoEllipsis = true,
                Font = Ui.F9, ForeColor = Ui.Text, BackColor = Ui.Bg
            };

            var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = Ui.S(40), WrapContents = false, Padding = Ui.SPad(0, 4, 0, 0), BackColor = Ui.Bg };
            Button(actions, "绑定并下一条", BindAndNext);
            Button(actions, "另建记录", () => AddSelectedModels());
            pickSkipBtn = Ui.Secondary("跳过此行", 82, 32);
            pickSkipBtn.Visible = false;
            pickSkipBtn.Margin = Ui.SPad(4, 1, 2, 1);
            pickSkipBtn.Click += (s, e) => Try(SkipCurrentBindTarget);
            actions.Controls.Add(pickSkipBtn);
            Button(actions, "返回表格", () => SetPickingMode(false));
            watchStop = Ui.Secondary("停止连续", 82, 32);
            watchStop.Visible = false;
            watchStop.Margin = Ui.SPad(4, 1, 2, 1);
            ((FlatButton)watchStop).SetFore(Ui.Bad);
            watchStop.Click += (s, e) => StopContinuous();
            actions.Controls.Add(watchStop);
            Button(actions, "撤销", OnPickingUndoClick);

            pickingPanel.Controls.Add(pickingLabel);
            pickingPanel.Controls.Add(pickingProgress);
            pickingPanel.Controls.Add(pickingTitle);
            pickingPanel.Controls.Add(actions);
            Controls.Add(pickingPanel);
            grid.CurrentCellChanged += (s, e) => RefreshPickingLabel();
        }

        int lastFieldVisMode = -1, lastFieldVisCat = -1;
        bool lastFieldVisFull;

        internal void ApplyFieldVisibility() {
            if (grid.Columns.Count < 14) return;
            if (lastFieldVisMode == activeViewMode && lastFieldVisCat == category.SelectedIndex && lastFieldVisFull == fullFieldView) return;
            lastFieldVisMode = activeViewMode;
            lastFieldVisCat = category.SelectedIndex;
            lastFieldVisFull = fullFieldView;
            bool sensorMode = activeViewMode == 1;
            bool elecMode = activeViewMode == 2;
            grid.SuspendLayout();
            try {
                string wantHomeHdr = (sensorMode || (activeViewMode == 3 && ActiveCategory == "普通传感器")) ? "常开/常闭" : "原位";
                if (grid.Columns[7].HeaderText != wantHomeHdr) grid.Columns[7].HeaderText = wantHomeHdr;

                if (sensorMode && !fullFieldView) {
                    var keep = new HashSet<int> { 1, 4, 5, 6, 7, 10, 11, 12 };
                    foreach (DataGridViewColumn col in grid.Columns) { bool v = keep.Contains(col.Index); if (col.Visible != v) col.Visible = v; }
                    grid.Columns[4].DisplayIndex = 2;
                    grid.Columns[5].DisplayIndex = 3;
                    grid.Columns[6].DisplayIndex = 4;
                    grid.Columns[7].DisplayIndex = 5;
                    grid.Columns[10].DisplayIndex = 6;
                    grid.Columns[11].DisplayIndex = 7;
                    grid.Columns[12].DisplayIndex = 8;
                } else if (elecMode && !fullFieldView) {
                    var keep = new HashSet<int> { 1, 4, 5, 6, 11, 12 };
                    foreach (DataGridViewColumn col in grid.Columns) { bool v = keep.Contains(col.Index); if (col.Visible != v) col.Visible = v; }
                    grid.Columns[4].DisplayIndex = 2;
                    grid.Columns[5].DisplayIndex = 3;
                    grid.Columns[6].DisplayIndex = 4;
                    grid.Columns[11].DisplayIndex = 5;
                    grid.Columns[12].DisplayIndex = 6;
                } else {
                    foreach (DataGridViewColumn column in grid.Columns) {
                        bool v = column.Name == "上级" || fullFieldView
                            || column.Index == 0 || column.Index == 1 || column.Index == 4 || column.Index == 5 || column.Index == 6 || column.Index == 7 || column.Index == 8
                            || (column.Index == 10 && (category.SelectedIndex == 0 || ActiveCategory == "普通传感器" || ActiveCategory == "气缸传感器"))
                            || (column.Name == "阀岛型号" && ActiveCategory == "阀片");
                        if (column.Visible != v) column.Visible = v;
                    }
                    grid.Columns[7].DisplayIndex = 2;
                    grid.Columns[8].DisplayIndex = 3;
                    grid.Columns[4].DisplayIndex = 5;
                    grid.Columns[5].DisplayIndex = 6;
                    grid.Columns[6].DisplayIndex = 7;
                }
            } finally {
                grid.ResumeLayout();
            }
            FitMainGridColumns();
        }

        internal bool PickingMode { get { return pickingMode; } }
        internal string BindingRecordId { get { return CurrentId(); } }

        internal void SetPickingMode(bool value) {
            if (value == pickingMode) return;
            if (pneumaticPanel != null) pneumaticPanel.EndEdit();
            grid.EndEdit();
            if (value && watchMode != "bind-queue" && watchMode != "bind-island") pickingId = CurrentId();
            string returnFocusId = pickingId;
            pickingMode = value;
            if (value) {
                normalWindowState = WindowState;
                normalBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                previewWasVisible = preview != null && preview.Visible;
                if (preview != null) { preview.Hold(true); preview.Visible = false; }
                if (previewSplit != null) previewSplit.Visible = false;
                mainShell.Visible = false;
                if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;
                MinimumSize = new Size(Ui.S(400), Ui.S(145));
                Size = new Size(Ui.S(440), Ui.S(180));
                var area = Screen.FromControl(this).WorkingArea;
                Location = new Point(Math.Max(area.Left, area.Right - Width - Ui.S(20)), Math.Max(area.Top, area.Bottom - Height - Ui.S(60)));
                pickingPanel.Visible = true;
                pickingPanel.BringToFront();
            } else {
                StopContinuous();
                pickingPanel.Visible = false;
                var wa = Screen.FromControl(this).WorkingArea;
                MinimumSize = new Size(Math.Min(Ui.S(860), wa.Width - Ui.S(24)), Math.Min(Ui.S(520), wa.Height - Ui.S(24)));
                Bounds = normalBounds;
                if (normalWindowState == FormWindowState.Maximized) WindowState = FormWindowState.Maximized;
                mainShell.Visible = true;
                if (preview != null && previewWasVisible) { preview.Visible = true; if (previewSplit != null) previewSplit.Visible = true; UpdatePreviewForCurrentRow(); }
            }
            if (!value && !string.IsNullOrEmpty(returnFocusId)) {
                if (activeViewMode == 0 && pneumaticPanel != null) pneumaticPanel.FocusRecord(returnFocusId);
                else SelectId(returnFocusId);
            }
            RefreshPickingLabel();
        }

        void RefreshPickingLabel() {
            if (pickingLabel == null) return;
            if (watchMode == "bind-island" && !string.IsNullOrEmpty(bindIslandKey)) {
                if (pickingProgress != null) pickingProgress.Visible = false;
                if (pickingTitle != null) pickingTitle.Text = "● 3D 点选绑定阀岛 · " + bindIslandKey;
                pickingLabel.Text = "待绑定阀岛：" + bindIslandKey + "\n请在天工 3D 视图中直接点击对应阀岛本体，选中后自动绑定并返回表格。";
                return;
            }
            if (watchMode == "bind-queue" && bindQueue != null && bindQueue.Count > 0) {
                int idx = Math.Max(0, Math.Min(bindQueueIndex, bindQueue.Count - 1));
                int total = bindQueue.Count;
                var cur = project.Records.FirstOrDefault(x => x.Id == bindQueue[idx]);
                var nextRec = idx + 1 < total ? project.Records.FirstOrDefault(x => x.Id == bindQueue[idx + 1]) : null;
                if (pickingProgress != null) {
                    pickingProgress.Visible = total > 1;
                    pickingProgress.Maximum = total;
                    pickingProgress.Value = Math.Min(total, bindQueueBoundCount);
                }
                if (pickingTitle != null) {
                    pickingTitle.Text = total == 1
                        ? "● 3D 点选绑定 · 1 / 1"
                        : "● 3D 顺序绑定 · 第 " + (idx + 1) + " / " + total + " 行（已绑 " + bindQueueBoundCount + "）";
                }
                string curDesc = cur == null ? "" : FormatPickBrief(cur);
                string rebindNote = cur != null && !string.IsNullOrEmpty(cur.ReferenceKey) ? "（重绑）" : "";
                if (total == 1) {
                    pickingLabel.Text = "待绑定：" + curDesc + rebindNote + "\n请在天工 3D 视图中直接点选对应模型，选中后自动绑定并返回表格。";
                } else if (nextRec != null) {
                    pickingLabel.Text = "当前目标 [" + (idx + 1) + "/" + total + "]：" + curDesc + rebindNote
                        + "\n下一顺接行：" + FormatPickBrief(nextRec) + "（点选即自动绑定并前进）";
                } else {
                    pickingLabel.Text = "当前目标 [" + (idx + 1) + "/" + total + "]：" + curDesc + rebindNote
                        + "\n最后一行，请在天工 3D 视图中点选模型，绑完自动返回表格。";
                }
                return;
            }
            if (pickingProgress != null) pickingProgress.Visible = false;
            var id = CurrentId();
            var r = project.Records.FirstOrDefault(x => x.Id == id);
            if (pickingTitle != null) {
                pickingTitle.Text = watchMode == "add" ? "● 连续新增中 · 直接在 CAD 中点选零件"
                    : watchMode == "bind" ? "● 连续绑定中 · 直接在 CAD 中点选对应零件"
                    : "● CAD 紧凑点选模式";
            }
            pickingLabel.Text = r == null
                ? "在 CAD 中选中零件后点「另建记录」，或先返回表格选择待绑定行。"
                : "当前行：" + r.Number + "（" + (string.IsNullOrEmpty(r.ReferenceKey) ? "待绑定" : "已绑定") + "）\n"
                  + (string.IsNullOrEmpty(r.ReferenceKey) ? "在三维里点选对应零件，再点「绑定并下一条」。" : "可换绑并前进；新增记录请点「另建记录」。");
        }

        static string FormatPickBrief(LineupRecord r) {
            if (r == null) return "";
            string num = string.IsNullOrWhiteSpace(r.Number) ? "(未编号)" : r.Number.Trim();
            string desc = (r.Description ?? "").Trim();
            return desc.Length > 0 ? (num + " · " + desc) : num;
        }

        void SetupModelEditing() {
            grid.EditingControlShowing += (s, e) => {
                var box = e.Control as TextBox;
                if (box == null) return;
                // 单击直接进编辑后，双击型号文本框依然能弹出型号库，且编辑态也支持 Ctrl+滚轮缩放
                if ((box.Tag as string) != "lineup-hooked") {
                    box.Tag = "lineup-hooked";
                    box.DoubleClick += (bs, be) => {
                        if (grid.CurrentCell != null && grid.CurrentCell.ColumnIndex == 5) {
                            grid.EndEdit();
                            Try(() => PickModel(false));
                        }
                    };
                    box.MouseWheel += HandleGridMouseWheel;
                }
                // 按类别缓存补全集合并跳过重复绑定，避免每次点单元格都重建 Shell COM IAutoComplete2
                string wantKey = "";
                if (grid.CurrentCell != null && grid.CurrentCell.ColumnIndex == 5 && grid.CurrentRow != null)
                    wantKey = "model:" + Convert.ToString(grid.CurrentRow.Cells[0].Value ?? "");
                if (string.Equals(lastGridAcKey, wantKey, StringComparison.Ordinal)) return;
                lastGridAcKey = wantKey;
                if (wantKey.Length > 0) {
                    AutoCompleteStringCollection source;
                    if (!gridAcCache.TryGetValue(wantKey, out source)) {
                        source = new AutoCompleteStringCollection();
                        string kind = wantKey.Substring(6);
                        source.AddRange(catalog.Entries.Where(m => m.Category == kind).Select(m => m.Model).Distinct().ToArray());
                        gridAcCache[wantKey] = source;
                    }
                    if (source.Count > 0) {
                        box.AutoCompleteCustomSource = source;
                        box.AutoCompleteSource = AutoCompleteSource.CustomSource;
                        box.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
                        return;
                    }
                }
                if (box.AutoCompleteMode != AutoCompleteMode.None) box.AutoCompleteMode = AutoCompleteMode.None;
                if (box.AutoCompleteSource != AutoCompleteSource.None) box.AutoCompleteSource = AutoCompleteSource.None;
            };
            grid.CellDoubleClick += (s, e) => {
                if (e.RowIndex < 0) return;
                if (e.ColumnIndex == 5) { Try(() => PickModel(false)); return; }
                if (e.ColumnIndex == 12) {
                    Try(() => {
                        string id = CurrentId();
                        var r = string.IsNullOrEmpty(id) ? null : project.Records.FirstOrDefault(x => x.Id == id);
                        if (r == null) return;
                        if (!string.IsNullOrEmpty(r.ReferenceKey)) Locate();
                        else StartSequentialBindRecords(new[] { r.Id });
                    });
                }
            };
            KeyPreview = true;
            KeyDown += (s, e) => {
                if (e.KeyCode == Keys.F4) {
                    e.SuppressKeyPress = true;
                    Try(() => { if (entryPanel != null && entryPanel.Visible && entryPanel.ContainsFocus) PickEntryModel(); else PickModel(false); });
                }
                if (e.Control && e.Shift && e.KeyCode == Keys.P) {
                    e.SuppressKeyPress = true;
                    Try(() => SetPickingMode(!pickingMode));
                }
            };
        }

        void CompleteKnownModel(int row) {
            string kind = Convert.ToString(grid.Rows[row].Cells[0].Value), model = Convert.ToString(grid.Rows[row].Cells[5].Value);
            var matches = catalog.Match(kind, model).ToList();
            grid.Rows[row].Cells[5].ErrorText = "";
            if (matches.Count != 1) { if (matches.Count > 1) grid.Rows[row].Cells[5].ErrorText = "同型号有多个品牌/接口规格，请按 F4 选择。"; return; }
            var entry = matches[0];
            string brand = Convert.ToString(grid.Rows[row].Cells[6].Value), connector = Convert.ToString(grid.Rows[row].Cells[10].Value);
            if ((brand.Length == 0 || brand == entry.Brand) && (connector.Length == 0 || connector == entry.Connector)) {
                grid.Rows[row].Cells[6].Value = entry.Brand;
                grid.Rows[row].Cells[10].Value = entry.Connector;
            } else {
                grid.Rows[row].Cells[5].ErrorText = "品牌或接口与型号库不同，保留手填值。按 F4 可选择库中规格。";
            }
        }

        internal void ApplyModel(LineupModelEntry entry, IEnumerable<string> ids) {
            var selected = ids.ToList();
            var next = Snapshot();
            foreach (var r in next.Records.Where(r => selected.Contains(r.Id))) {
                if (r.Category != entry.Category) throw new InvalidOperationException("所选行类别与型号库类别不同，请分别选择。");
                r.Model = entry.Model;
                r.Brand = entry.Brand;
                r.Connector = entry.Connector;
            }
            if (next.Records.Any(r => selected.Contains(r.Id) && string.IsNullOrWhiteSpace(r.Number) && (r.Category == "气缸" || r.Category == "阀片")) && !ReadyNumbering("气缸")) return;
            next.NumberPrefix = project.NumberPrefix;
            next.ActiveIsland = project.ActiveIsland;
            next.Islands = project.Islands.Select(i => i.Copy()).ToList();
            LineupNumbering.FillMissing(next, next.Records.Where(r => selected.Contains(r.Id)));
            Commit(next, CurrentId(), "已套用型号、品牌与接口", true);
        }

        void PickModel(bool multiple) {
            if (catalogError.Length > 0) throw new InvalidOperationException(catalogError);
            var id = CurrentId();
            if (id == null) throw new InvalidOperationException("请先在表格中选择一行。");
            var ids = multiple ? SelectedIds() : new List<string> { id };
            string kind = Snapshot().Records.Single(r => r.Id == id).Category;
            PickModelForCategory(kind, ids);
        }

        void CompactAllNumbers() {
            var next = Snapshot();
            int renamed = LineupNumbering.CompactAll(next);
            if (renamed <= 0) {
                ShowToast("所有编号均已连续顺序排列，无需重排。", Ui.Ok);
                return;
            }
            Commit(next, CurrentId(), "已重新顺排 " + renamed + " 条记录编号");
            ShowToast("已重新顺排 " + renamed + " 条记录编号（支持撤销）。", Ui.Ok, "撤销", (s, e) => Try(Undo));
        }

        internal void OpenModelLibraryManager() {
            if (catalogError.Length > 0) throw new InvalidOperationException(catalogError);
            string initCat = activeViewMode == 1 ? "普通传感器"
                : activeViewMode == 2 ? "其他电气件"
                : "";
            using (var dialog = new LineupModelLibraryForm(
                catalog, catalogFile, false, initCat, 0, null,
                SaveCatalogDirect,
                () => {
                    SaveDraft(false);
                    int n = catalog.Learn(project.Records);
                    if (n > 0) SaveCatalogDirect(catalog);
                    return n;
                },
                LoadBuiltInSeed)) {
                dialog.ShowDialog(this);
                if (dialog.CatalogChanged) ShowToast("型号库已更新（现有 " + catalog.Entries.Count + " 条）。", Ui.Ok);
            }
        }

        void PickModelForCategory(string kind, IEnumerable<string> targetIds) {
            if (catalogError.Length > 0) throw new InvalidOperationException(catalogError);
            var ids = targetIds.Distinct().ToList();
            if (ids.Count == 0) throw new InvalidOperationException("没有可套用型号的目标行。");
            using (var dialog = new LineupModelLibraryForm(
                catalog, catalogFile, true, kind, ids.Count, null,
                SaveCatalogDirect,
                () => {
                    SaveDraft(false);
                    int n = catalog.Learn(project.Records);
                    if (n > 0) SaveCatalogDirect(catalog);
                    return n;
                },
                LoadBuiltInSeed)) {
                if (dialog.ShowDialog(this) != DialogResult.OK || dialog.SelectedEntry == null) return;
                ApplyModel(dialog.SelectedEntry, ids);
            }
        }

        int LearnModels(IEnumerable<LineupRecord> records) {
            if (catalogError.Length > 0) throw new InvalidOperationException(catalogError);
            using (var mutex = new System.Threading.Mutex(false, "Local\\TianGongLineupModelLibrary")) {
                bool locked = false;
                try {
                    try { locked = mutex.WaitOne(2000); } catch (System.Threading.AbandonedMutexException) { locked = true; }
                    if (!locked) throw new IOException("型号库正被另一窗口更新，请稍后重试。");
                    LineupModelCatalog latest;
                    bool existed = File.Exists(catalogFile);
                    if (existed) {
                        latest = LineupModelCatalog.Load(catalogFile);
                        if (!latest.SeedInitialized && catalog.SeedInitialized) latest.SeedInitialized = true;
                    } else {
                        latest = catalog.Clone();
                    }
                    int count = latest.Learn(records);
                    latest.EnsureSubCategories();
                    if (count > 0 || (!existed && latest.Entries.Count > 0)) latest.Save(catalogFile);
                    catalog = latest;
                    InvalidateModelAutoCompleteCaches();
                    return count;
                } finally { if (locked) mutex.ReleaseMutex(); }
            }
        }

        void LearnAfterSave() {
            try { LearnModels(project.Records); }
            catch (Exception ex) { status.Text = "清单已保存；型号库未更新：" + ex.Message; }
        }

        void ImportLibrary() {
            using (var dialog = new OpenFileDialog { Filter = "型号库 / 清单|*.xlsx;*.xlsm;*.xls;*.csv;*.tsv;*.txt;*.xml" }) {
                if (dialog.ShowDialog(this) == DialogResult.OK) {
                    string ext = Path.GetExtension(dialog.FileName).ToLowerInvariant();
                    int count;
                    if (ext == ".xlsx" || ext == ".xlsm" || ext == ".xls") {
                        status.Text = "正在从 Excel 清单提取型号…";
                        Application.DoEvents();
                        var prevCursor = Cursor;
                        Cursor = Cursors.WaitCursor;
                        try {
                            var imp = LineupXlsxReader.Read(dialog.FileName, true);
                            count = LearnModels(imp.Records);
                        } finally {
                            Cursor = prevCursor;
                        }
                    } else {
                        var imported = ext == ".xml" ? LineupModelCatalog.Load(dialog.FileName) : LineupModelCatalog.Import(SettingsFile.ReadUserText(dialog.FileName), ext == ".csv" ? ',' : '\t', ActiveCategory);
                        count = catalog.Merge(imported);
                        if (count > 0 || !File.Exists(catalogFile)) SaveCatalogDirect(catalog);
                    }
                    ShowToast("型号库新增 " + count + " 条，现有 " + catalog.Entries.Count + " 条。", Ui.Ok);
                }
            }
        }

        void ExportLibrary() {
            using (var d = new SaveFileDialog { Filter = "型号库 CSV|*.csv", FileName = "Lineup-型号库.csv" }) {
                if (d.ShowDialog(this) == DialogResult.OK) {
                    SettingsFile.WriteUserText(d.FileName, LineupCsv.Write(LineupModelCatalog.ExportRows(catalog.Entries)));
                    ShowToast("型号库已按分类导出。", Ui.Ok);
                }
            }
        }
    }
}
