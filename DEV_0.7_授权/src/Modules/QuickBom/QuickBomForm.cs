using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using F = SolidEdgeFramework;

namespace TianGongCadSuite {
    // 出图清单工作台（0.13.0，命令 14）：
    // 将「批量改名」「属性卡填写」「快速出清单」三合一整合在同一张主表里：
    //   1. 共用同一份文件清单（支持载入当前装配/工程图/零件、递归扫描模型文件夹、手动添加文件）与右侧 3D 预览；
    //   2. 同一张表同时展示与编辑：新文件名、全部属性卡字段（可自定义列）、材料、八大类、批次/优先等级/编码与状态；
    //      改一格三处实时联动（黄底 = 待改名 / 属性未写入，灰底 = 文件尚无该属性，红底 = 重名或图号/数量异常）；
    //   3. 顶部可切换「清单表头 / 批量改名 / 收起」，工具栏可一键切列视图（全部列 / 改名 / 属性 / 清单），
    //      支持「图号 ↔ 文件名」一键互填、Excel Ctrl+V 按可见列顺序整块粘贴；
    //   4. 底部「执行改名」「写入属性」「开始出清单」既可分步独立执行，也可一键串联完成。
    public sealed class QuickBomForm : Form, IBusyTool {
        const string Title = "出图清单工作台";

        // 固定基础列下标（后面紧跟动态扩展属性卡列，最后一列永远是「说明 / 状态」）
        const int ColSel = 0;
        const int ColSeq = 1;
        const int ColKind = 2;
        const int ColOldName = 3;
        const int ColNewName = 4;
        const int ColPartName = 5;
        const int ColDno = 6;
        const int ColMat = 7;
        const int ColQty = 8;
        const int ColSurf = 9;
        const int ColBatch = 10;
        const int ColPrio = 11;
        const int ColCat = 12;
        const int ColChange = 13;
        const int ColRemark = 14;
        const int ColCode = 15;
        const int ColLookupDno = 16;
        const int ColLookupName = 17;
        const int ColDraft = 18;
        const int FixedColCount = 19;

        const string TagColSel = "col:sel";
        const string TagColSeq = "col:seq";
        const string TagColKind = "col:kind";
        const string TagColOld = "col:old";
        const string TagColNew = "col:new";
        const string TagColMat = "col:mat";
        const string TagColCat = "col:cat";
        const string TagColChange = "col:change";
        const string TagColRemark = "col:remark";
        const string TagColCode = "col:code";
        const string TagColLookupDno = "col:ldno";
        const string TagColLookupName = "col:lname";
        const string TagColDraft = "col:draft";
        const string TagColStatus = "col:status";
        const string TagPropPrefix = "prop:";

        const string DefaultLegendText = "点表头排序/▾筛选 · Ctrl+C/V/X/D/Z/Y · Ctrl+F搜 · Ctrl+H替换 · Space切勾选 · 可直接拖入文件/文件夹 · 黄底=待落盘 · 红底=异常";
        const int MaxUndoSteps = 30;
        static readonly Font HeaderActiveFont = new Font(Ui.Family, 9F, FontStyle.Bold);
        static readonly SolidBrush HeaderRailBrush = new SolidBrush(Ui.Rail);
        static readonly SolidBrush HeaderChipBrush = new SolidBrush(Ui.ChipBg);
        static readonly SolidBrush HeaderSortBrush = new SolidBrush(Color.FromArgb(0xF3, 0xF4, 0xF8));
        static readonly SolidBrush HeaderWhiteBrush = new SolidBrush(Color.White);
        static readonly SolidBrush HeaderTextBrush = new SolidBrush(Ui.Text);
        static readonly SolidBrush HeaderFaintBrush = new SolidBrush(Ui.Faint);
        static readonly Pen HeaderLinePen = new Pen(Ui.Line);
        static readonly Pen HeaderAccentPen = new Pen(Ui.Accent, Math.Max(2f, Ui.S(2)));

        readonly CadKeyShield keyShield;

        readonly F.Application app;
        readonly BomCad bomCad;
        readonly PropCardCad propCad;
        readonly BatchRenameCad renameCad;

        readonly List<WorkbenchRow> rows = new List<WorkbenchRow>();
        readonly List<WorkbenchRow> visibleRows = new List<WorkbenchRow>();
        readonly Dictionary<string, HashSet<string>> columnFilters = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        readonly List<Dictionary<WorkbenchRow, WorkbenchRowSnapshot>> undoStack = new List<Dictionary<WorkbenchRow, WorkbenchRowSnapshot>>();
        readonly List<Dictionary<WorkbenchRow, WorkbenchRowSnapshot>> redoStack = new List<Dictionary<WorkbenchRow, WorkbenchRowSnapshot>>();
        readonly Dictionary<string, int> cachedDnoCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<WorkbenchRow> dupDnoRows = new HashSet<WorkbenchRow>();
        readonly Dictionary<int, AutoCompleteStringCollection> acCache = new Dictionary<int, AutoCompleteStringCollection>();
        int lastAcCol = -1;
        string[] colPropFields = new string[0];
        readonly Dictionary<string, int> savedColWidths = new Dictionary<string, int>(StringComparer.Ordinal);
        readonly Timer filterDebounce = new Timer { Interval = 120 };
        readonly Timer colWidthDebounce = new Timer { Interval = 350 };
        List<string> propFields;
        List<string> extraPropFields = new List<string>();
        int statusColIndex = FixedColCount;
        string sortColKey = "";
        int sortDir; // 0 默认 / 1 升序 / -1 降序
        int hoveredFilterCol = -1;
        string lastPreviewPath = "";

        readonly WizardShell shell = new WizardShell { StepCaption = false };
        readonly PartPreview preview = new PartPreview();
        readonly DataGridView grid = new DataGridView();
        readonly ContextMenuStrip gridMenu = Ui.Menu();
        readonly TextBox filterBox = new TextBox();
        Button filterClearBtn;
        readonly TextBox fillBox = new TextBox();
        Label legendLabel;
        FlatButton clearFiltersBtn;

        // 顶部可切换面板（清单表头 / 批量改名 / 收起）
        readonly Panel topDrawer = new Panel();
        Panel headerPanel;
        Panel renamePanel;
        Panel railTopPanel;
        Panel railFootPanel;
        Control toolbarPanel;
        Button[] headerActionButtons;
        VerticalStack mainStack;
        Segmented drawerSeg;
        Segmented colViewSeg;

        // 批量改名控件
        readonly TextBox findBox = new TextBox();
        readonly TextBox replaceBox = new TextBox();
        readonly TextBox prefixBox = new TextBox();
        readonly TextBox suffixBox = new TextBox();
        readonly TextBox templateBox = new TextBox();
        readonly CheckBox chkFollowRename = new CheckBox { Text = "同名 .dft / .cfg 跟着改名", Checked = true };
        readonly CheckBox chkFillMissing = new CheckBox { Text = "写入属性时补齐缺的属性", Checked = true };

        // 表头字段控件
        readonly TextBox tbCustomer = new TextBox();
        readonly TextBox tbProjectCode = new TextBox();
        readonly TextBox tbProjectName = new TextBox();
        readonly TextBox tbLineNo = new TextBox();
        readonly TextBox tbStationNo = new TextBox();
        readonly TextBox tbSeqNo = new TextBox();
        readonly TextBox tbMechNo = new TextBox();
        readonly TextBox tbListTitle = new TextBox();
        readonly TextBox tbEngineer = new TextBox();
        readonly TextBox tbRegion = new TextBox();
        readonly DateTimePicker dpIssue = new DateTimePicker { Format = DateTimePickerFormat.Short };
        readonly DateTimePicker dpDelivery = new DateTimePicker { Format = DateTimePickerFormat.Short };
        readonly TextBox tbOutput = new TextBox();

        // 左侧栏扫描与导出选项
        readonly CheckBox chkRecursive = new CheckBox { Text = "扫描时包含子文件夹", Checked = true };
        readonly CheckBox chk3d = new CheckBox { Text = "原三维", Checked = true };
        readonly CheckBox chkDft = new CheckBox { Text = "DFT", Checked = true };
        readonly CheckBox chkDwg = new CheckBox { Text = "DWG", Checked = true };
        readonly CheckBox chkJpg = new CheckBox { Text = "JPG", Checked = true };
        readonly CheckBox chkPdf = new CheckBox { Text = "PDF", Checked = true };
        readonly CheckBox chkStep = new CheckBox { Text = "STEP", Checked = true };
        readonly CheckBox chkCodeReq = new CheckBox { Text = "同时生成编码申请表 (.xlsx)", Checked = false };
        readonly CheckBox chkThumb = new CheckBox { Text = "在清单中嵌入三维缩略图", Checked = false };
        readonly CheckBox chkZip = new CheckBox { Text = "完成后打包为同名 .zip", Checked = false };
        readonly CheckBox chkOverwrite = new CheckBox { Text = "覆盖输出目录已有文件", Checked = true };

        readonly SlimProgress progress = new SlimProgress { Visible = false, Height = Ui.S(6) };
        readonly Label status = new Label();
        readonly FlatButton runBtn;
        readonly FlatButton writePropsBtn;
        readonly FlatButton renameBtn;
        readonly Button openOutBtn;
        readonly ToolTip tips = new ToolTip { AutoPopDelay = 16000, InitialDelay = 300 };

        string sourceFolder = "";
        string lastScannedFolder = "";
        string customListTemplate = "";
        string customCodeTemplate = "";
        bool outputManuallyEdited;
        bool suppressHeaderEvents;
        bool rebuilding;
        bool busy, canStopBusy, stopRequested, forceClose;
        string busyWhat = "处理中";
        int stateFilter; // 0 全部 / 1 已勾选导出 / 2 待改名 / 3 属性待写入 / 4 缺图或待补
        int colViewMode; // 0 全部列 / 1 改名 / 2 属性 / 3 清单
        int lastRenameProblems;
        string lastExportedDir = "";

        public bool Busy { get { return busy; } }
        public string BusyWhat { get { return busyWhat; } }

        readonly Stopwatch pulseWatch = new Stopwatch();
        void Pulse(string text) {
            if (pulseWatch.IsRunning && pulseWatch.ElapsedMilliseconds < 150) return;
            pulseWatch.Restart();
            SetStatus(text, Ui.Muted);
            try { Application.DoEvents(); } catch { }
        }

        void SetStatus(string text, Color color) {
            status.Text = text ?? "";
            status.ForeColor = color;
            try { status.Refresh(); } catch { }
        }

        public QuickBomForm(F.Application application) {
            app = application;
            bomCad = new BomCad(application);
            propCad = new PropCardCad(application);
            renameCad = new BatchRenameCad(application);
            preview.App = application;

            propFields = PropCardFields.Load(SettingsFile.PathOf(PropCardFields.FileName));
            extraPropFields = WorkbenchRules.ExtraPropFields(propFields);
            foreach (var kv in WindowBoundsRules.ParseWidths(Choices.Get("quickbom.colwidths", ""))) savedColWidths[kv.Key] = kv.Value;

            Ui.Shell(this, Product.Title(Title), 1520, 780, 980, 580);
            FormBorderStyle = FormBorderStyle.Sizable;
            AllowDrop = true;
            DragEnter += OnFormDragEnter;
            DragDrop += OnFormDragDrop;
            keyShield = new CadKeyShield(this, () => busy, ResolveShieldShortcut);
            WindowStateStore.Bind(this, "quickbom");
            Controls.Add(shell);
            var page = shell.AddPage(Title, Title, "");

            // ---- 左侧栏：来源 + 状态筛选 + 导出选项 ----
            BuildLeftRail();

            // ---- 页面右上角按钮 ----
            var btnCols = Ui.Small("属性列…"); btnCols.Click += (s, e) => EditPropColumns();
            var btnTemplates = Ui.Small("模板设置…"); btnTemplates.Click += (s, e) => EditTemplates();
            var btnRevert = Ui.Small("撤销修改"); btnRevert.Click += (s, e) => RevertAllEdits();
            var btnRemove = Ui.Small("移除选中行"); btnRemove.Click += (s, e) => RemoveSelectedRows();
            headerActionButtons = new[] { btnCols, btnTemplates, btnRevert, btnRemove };
            foreach (var b in headerActionButtons) ((FlatButton)b).SetFore(Ui.Text2);
            shell.AddHeader(page, btnCols);
            shell.AddHeader(page, btnTemplates);
            shell.AddHeader(page, btnRevert);
            shell.AddHeader(page, btnRemove);

            // ---- 正文：可切换顶栏（清单表头 / 批量改名 / 收起）+ 快捷工具栏 + 统一主表（配右侧 3D 预览） ----
            mainStack = new VerticalStack { Dock = DockStyle.Fill, Padding = new Padding(0), Gap = Ui.S(8), AutoScroll = false };
            page.Body.Controls.Add(mainStack);

            BuildTopDrawer();
            mainStack.Add(topDrawer);
            toolbarPanel = BuildToolbar();
            mainStack.Add(toolbarPanel);

            var gridFrame = Ui.Card("", 0, "");
            gridFrame.Padding = Ui.SPad(2, 2, 2, 6);
            InitGrid();
            RebuildGridColumns();
            gridFrame.Controls.Add(grid);
            shell.AddHeader(page, PartPreview.Attach(gridFrame, preview, "quickbom.preview"));
            mainStack.Add(gridFrame, true);

            legendLabel = new Label {
                Text = DefaultLegendText,
                AutoSize = false, Height = Ui.S(22), Font = Ui.F8, ForeColor = Ui.Muted, BackColor = Ui.Bg, AutoEllipsis = true
            };
            mainStack.Add(legendLabel);
            mainStack.Add(progress);

            // ---- 底部操作栏：三个核心动作并列，既可独立分步也能一键串联 ----
            runBtn = (FlatButton)Ui.Primary("开始出清单", 156, 42);
            writePropsBtn = (FlatButton)Ui.Secondary("写入属性", 104, 36);
            renameBtn = (FlatButton)Ui.Secondary("执行改名", 104, 36);
            openOutBtn = Ui.Secondary("打开输出目录", 116, 36);
            var closeBtn = Ui.Secondary("关闭", 74, 36);

            writePropsBtn.Enabled = false;
            renameBtn.Enabled = false;
            openOutBtn.Enabled = false;

            runBtn.Click += (s, e) => { if (busy) stopRequested = true; else RunExport(); };
            writePropsBtn.Click += (s, e) => { if (!busy) ExecuteWriteProps(false); };
            renameBtn.Click += (s, e) => { if (!busy) ExecuteRename(false); };
            openOutBtn.Click += (s, e) => OpenOutputFolder();
            closeBtn.Click += (s, e) => Close();

            shell.Footer.Padding = Ui.SPad(20, 0, 18, 0);
            shell.Footer.Gap = Ui.S(8);
            shell.Footer.AddRight(closeBtn);
            shell.Footer.AddRight(openOutBtn);
            shell.Footer.AddRight(renameBtn);
            shell.Footer.AddRight(writePropsBtn);
            shell.Footer.AddRight(runBtn);
            shell.Footer.Status = status;
            status.ForeColor = Ui.Muted;

            LoadRememberedDefaults();
            WireHeaderAutoOutput();
            int savedDrawer, savedColView;
            int initDrawer = int.TryParse(Choices.Get("quickbom.drawer", "0"), out savedDrawer) && savedDrawer >= 0 && savedDrawer <= 2 ? savedDrawer : 0;
            int initColView = int.TryParse(Choices.Get("quickbom.colview", "0"), out savedColView) && savedColView >= 0 && savedColView <= 3 ? savedColView : 0;
            drawerSeg.SelectedIndex = initDrawer;
            colViewSeg.SelectedIndex = initColView;
            ApplyDrawerMode(initDrawer, false);
            ApplyColumnView(initColView, false);
            UpdateActionButtons();

            FormClosing += (s, e) => {
                if (busy) { stopRequested = true; e.Cancel = true; return; }
                if (!forceClose && e.CloseReason == CloseReason.UserClosing && !ConfirmDiscard()) { e.Cancel = true; return; }
                SaveRememberedDefaults();
                SaveColumnWidthsNow();
            };
            FormClosed += (s, e) => { renameCad.CancelWarm(); keyShield.Dispose(); filterDebounce.Dispose(); colWidthDebounce.Dispose(); tips.Dispose(); gridMenu.Dispose(); };
            filterDebounce.Tick += (s, e) => { filterDebounce.Stop(); if (!IsDisposed && !busy) RebuildGrid(); };
            colWidthDebounce.Tick += (s, e) => { colWidthDebounce.Stop(); if (!IsDisposed) SaveColumnWidthsNow(); };
        }

        // 功能区再次点「出图清单工作台」：不关掉重扫，直接把已开着的工作台提回前台（保留未落盘修改与筛选态）
        internal bool ReuseIfOpen() {
            if (IsDisposed) return false;
            if (!busy) {
                SetStatus("工作台一直开着（如需切换为当前 CAD 文档，点左侧「载入当前文档」）。", Ui.Accent);
            }
            return true;
        }

        Action ResolveShieldShortcut(Keys keyData) {
            if (keyData == (Keys.Control | Keys.F)) return FocusFilterBox;
            if (keyData == (Keys.Control | Keys.H)) return ShowFindReplaceDialog;
            if (grid.IsCurrentCellInEditMode) return null;
            if (keyData == (Keys.Control | Keys.C)) return CopySelectedCells;
            if (keyData == (Keys.Control | Keys.V)) return PasteFromClipboard;
            if (keyData == (Keys.Control | Keys.X)) return CutSelectedCells;
            if (keyData == (Keys.Control | Keys.D)) return FillDownSelectedCells;
            if (keyData == (Keys.Control | Keys.Z)) return PerformUndo;
            if (keyData == (Keys.Control | Keys.Y) || keyData == (Keys.Control | Keys.Shift | Keys.Z)) return PerformRedo;
            if (keyData == (Keys.Control | Keys.A)) return () => { if (!IsDisposed) { grid.SelectAll(); UpdateSelectionSummary(); } };
            if (keyData == Keys.Delete) return DeleteSelectedCells;
            if (keyData == Keys.Space && ActiveControl == grid) return TogglePickedRowsSelected;
            return null;
        }

        void FocusFilterBox() {
            if (IsDisposed) return;
            try { filterBox.Focus(); filterBox.SelectAll(); } catch { }
        }

        // ---- 左侧栏 ----
        void BuildLeftRail() {
            int hLoad = Ui.S(36), hBtn = Ui.S(28), hChk = Ui.S(22), hLbl = Ui.S(18), hBox = Ui.S(26);
            int railTopH = hLoad + Ui.S(4) + hBtn + Ui.S(2) + hChk + Ui.S(2) + hBtn + Ui.S(8) + hLbl + Ui.S(2) + hBox + Ui.S(2);
            var railTop = new Panel { BackColor = Ui.Rail, Height = railTopH };
            railTopPanel = railTop;
            var btnLoadActive = Ui.Secondary("载入当前文档", 200, 36); btnLoadActive.Click += (s, e) => LoadActive();
            var btnScanFolder = Ui.Ghost("扫描模型文件夹…"); btnScanFolder.Click += (s, e) => PickAndScanFolder();
            chkRecursive.AutoSize = false; chkRecursive.Height = hChk; chkRecursive.Font = Ui.F8; chkRecursive.ForeColor = Ui.Text2; chkRecursive.BackColor = Ui.Rail; chkRecursive.AutoEllipsis = true;
            Choices.Bind(chkRecursive, "quickbom.recursive");
            chkRecursive.CheckedChanged += (s, e) => {
                if (!busy && lastScannedFolder.Length > 0 && Directory.Exists(lastScannedFolder)) {
                    LoadFolder(lastScannedFolder, chkRecursive.Checked);
                }
            };
            var btnAddFiles = Ui.Ghost("添加零件 / 工程图…"); btnAddFiles.Click += (s, e) => AddFiles();
            var lblFilter = new Label { Text = "按文件名 / 名称 / 图号筛选 (Ctrl+F)", AutoSize = false, Font = Ui.F8, ForeColor = Ui.Text2, BackColor = Ui.Rail };
            filterBox.BorderStyle = BorderStyle.FixedSingle; filterBox.Font = Ui.F9;
            filterClearBtn = Ui.Small("×");
            ((FlatButton)filterClearBtn).SetFore(Ui.Muted);
            filterClearBtn.Width = Ui.S(24);
            filterClearBtn.Height = hBox;
            filterClearBtn.Visible = false;
            tips.SetToolTip(filterClearBtn, "清空关键词筛选 (Esc)");
            filterClearBtn.Click += (s, e) => { filterBox.Text = ""; try { grid.Focus(); } catch { } };
            filterBox.KeyDown += (s, e) => {
                if (e.KeyCode == Keys.Escape) {
                    if (filterBox.Text.Length > 0) filterBox.Text = "";
                    try { grid.Focus(); } catch { }
                    e.SuppressKeyPress = true;
                }
            };
            railTop.Controls.AddRange(new Control[] { btnLoadActive, btnScanFolder, chkRecursive, btnAddFiles, lblFilter, filterBox, filterClearBtn });
            railTop.Resize += (s, e) => {
                int w = railTop.Width, y = 0;
                btnLoadActive.SetBounds(0, y, w, hLoad); y += hLoad + Ui.S(4);
                btnScanFolder.SetBounds(0, y, w, hBtn); y += hBtn + Ui.S(2);
                chkRecursive.SetBounds(Ui.S(6), y, Math.Max(10, w - Ui.S(6)), hChk); y += hChk + Ui.S(2);
                btnAddFiles.SetBounds(0, y, w, hBtn); y += hBtn + Ui.S(8);
                lblFilter.SetBounds(Ui.S(2), y, w, hLbl); y += hLbl + Ui.S(2);
                int cw = filterClearBtn.Visible ? filterClearBtn.Width + Ui.S(2) : 0;
                filterBox.SetBounds(0, y, Math.Max(40, w - cw), hBox);
                filterClearBtn.SetBounds(w - filterClearBtn.Width, y, filterClearBtn.Width, hBox);
            };
            shell.RailTop = railTop;

            shell.Rail.Visible = true;
            shell.Rail.Kind = StepRail.Mode.Nav;
            shell.Rail.Add("全部文件", "");
            shell.Rail.Add("已勾选导出", "").Dot = Ui.Ok;
            shell.Rail.Add("待改名", "").Dot = Color.FromArgb(0xF5, 0x9E, 0x0B);
            shell.Rail.Add("属性待写入", "").Dot = Ui.Accent;
            shell.Rail.Add("缺图 / 待补", "").Dot = Ui.Bad;
            shell.Rail.SelectedChanged += (s, e) => { if (busy) return; stateFilter = shell.Rail.Selected; RebuildGrid(); };
            filterBox.TextChanged += (s, e) => {
                bool showClear = filterBox.Text.Length > 0;
                if (filterClearBtn.Visible != showClear) {
                    filterClearBtn.Visible = showClear;
                    int w = railTop.Width;
                    int cw = showClear ? filterClearBtn.Width + Ui.S(2) : 0;
                    filterBox.Width = Math.Max(40, w - cw);
                }
                filterDebounce.Stop(); filterDebounce.Start();
            };

            // 侧栏底部：6 种导出格式 + 附加项
            int fmtRowH = Ui.S(22), footChkH = Ui.S(23), footGap = Ui.S(1);
            var railFoot = new Panel { BackColor = Ui.Rail, Height = Ui.S(18) + fmtRowH * 2 + Ui.S(6) + (footChkH + footGap) * 4 + Ui.S(2) };
            railFootPanel = railFoot;
            var lblFmt = new Label { Text = "导出格式（每件 6 种）", AutoSize = false, Font = Ui.F8, ForeColor = Ui.Text2, BackColor = Ui.Rail };
            var fmtPanel = new TableLayoutPanel { ColumnCount = 3, RowCount = 2, BackColor = Ui.Rail, Margin = new Padding(0) };
            fmtPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38f));
            fmtPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 31f));
            fmtPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 31f));
            for (int r = 0; r < 2; r++) fmtPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, fmtRowH));
            foreach (var cb in new[] { chk3d, chkDft, chkDwg, chkJpg, chkPdf, chkStep }) {
                cb.AutoSize = true; cb.Font = Ui.F8; cb.BackColor = Ui.Rail; cb.Margin = new Padding(0);
            }
            fmtPanel.Controls.Add(chk3d, 0, 0); fmtPanel.Controls.Add(chkDft, 1, 0); fmtPanel.Controls.Add(chkDwg, 2, 0);
            fmtPanel.Controls.Add(chkJpg, 0, 1); fmtPanel.Controls.Add(chkPdf, 1, 1); fmtPanel.Controls.Add(chkStep, 2, 1);

            foreach (var cb in new[] { chkCodeReq, chkThumb, chkZip, chkOverwrite }) {
                cb.AutoSize = false; cb.Height = footChkH; cb.Font = Ui.F8; cb.BackColor = Ui.Rail; cb.AutoEllipsis = true;
            }
            Choices.Bind(chk3d, "quickbom.fmt.3d");
            Choices.Bind(chkDft, "quickbom.fmt.dft");
            Choices.Bind(chkDwg, "quickbom.fmt.dwg");
            Choices.Bind(chkJpg, "quickbom.fmt.jpg");
            Choices.Bind(chkPdf, "quickbom.fmt.pdf");
            Choices.Bind(chkStep, "quickbom.fmt.step");
            Choices.Bind(chkCodeReq, "quickbom.codereq");
            Choices.Bind(chkThumb, "quickbom.thumb");
            Choices.Bind(chkZip, "quickbom.zip");
            Choices.Bind(chkOverwrite, "quickbom.overwrite");

            railFoot.Controls.AddRange(new Control[] { lblFmt, fmtPanel, chkCodeReq, chkThumb, chkZip, chkOverwrite });
            railFoot.Resize += (s, e) => {
                int w = railFoot.Width, y = 0;
                lblFmt.SetBounds(0, y, w, Ui.S(18)); y += Ui.S(20);
                fmtPanel.SetBounds(0, y, w, fmtRowH * 2 + Ui.S(2)); y += fmtRowH * 2 + Ui.S(4);
                chkCodeReq.SetBounds(0, y, w, footChkH); y += footChkH + footGap;
                chkThumb.SetBounds(0, y, w, footChkH); y += footChkH + footGap;
                chkZip.SetBounds(0, y, w, footChkH); y += footChkH + footGap;
                chkOverwrite.SetBounds(0, y, w, footChkH);
            };
            shell.RailBottom = railFoot;
        }

        // ---- 顶部可切换面板：清单表头 / 批量改名规则 / 收起 ----
        void BuildTopDrawer() {
            int rowH = Ui.S(36);
            int cardH = rowH * 3 + Ui.S(18);
            topDrawer.Height = cardH;
            topDrawer.BackColor = Ui.Rail;
            topDrawer.Paint += (s, e) => {
                using (var p = new Pen(Ui.Line)) e.Graphics.DrawRectangle(p, 0, 0, topDrawer.Width - 1, topDrawer.Height - 1);
            };

            headerPanel = BuildHeaderCardContent(rowH);
            renamePanel = BuildRenameCardContent(rowH);
            topDrawer.Controls.Add(headerPanel);
            topDrawer.Controls.Add(renamePanel);
            Ui.EnableDoubleBuffer(topDrawer);
            Ui.EnableDoubleBuffer(headerPanel);
            Ui.EnableDoubleBuffer(renamePanel);
        }

        Panel BuildHeaderCardContent(int rowH) {
            var p = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Rail, Padding = Ui.SPad(10, 8, 10, 8) };
            var tbl = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 12, RowCount = 3, BackColor = Ui.Rail, Margin = new Padding(0) };
            int wL0 = MeasureHeaderCol("输出目录", 68);
            int wL2 = MeasureHeaderCol("项目编号", 68);
            int wL4 = MeasureHeaderCol("项目名称", 68);
            int wL6 = MeasureHeaderCol("线体号", 54);
            int wL8 = MeasureHeaderCol("工位号", 54);
            int wL10 = MeasureHeaderCol("单号", 42);
            float[] colWidths = { wL0, 96, wL2, 92, wL4, 140, wL6, 62, wL8, 118, wL10, 118 };
            for (int c = 0; c < 12; c++) {
                tbl.ColumnStyles.Add(c % 2 == 0
                    ? new ColumnStyle(SizeType.Absolute, colWidths[c])
                    : new ColumnStyle(SizeType.Percent, colWidths[c]));
            }
            for (int r = 0; r < 3; r++) tbl.RowStyles.Add(new RowStyle(SizeType.Absolute, rowH));

            StyleBox(tbCustomer); StyleBox(tbProjectCode); StyleBox(tbProjectName);
            StyleBox(tbLineNo); StyleBox(tbStationNo); StyleBox(tbSeqNo);
            StyleBox(tbMechNo); StyleBox(tbListTitle); StyleBox(tbEngineer);
            StyleBox(tbRegion); StyleBox(tbOutput);
            dpIssue.Format = DateTimePickerFormat.Custom; dpIssue.CustomFormat = "yyyy/MM/dd";
            dpDelivery.Format = DateTimePickerFormat.Custom; dpDelivery.CustomFormat = "yyyy/MM/dd";
            dpIssue.Font = Ui.F8; dpIssue.Margin = Ui.SPad(0, 3, 6, 3); dpIssue.Dock = DockStyle.Fill;
            dpDelivery.Font = Ui.F8; dpDelivery.Margin = Ui.SPad(0, 3, 2, 3); dpDelivery.Dock = DockStyle.Fill;

            AddField(tbl, 0, 0, "客户", tbCustomer);
            AddField(tbl, 2, 0, "项目编号", tbProjectCode);
            AddField(tbl, 4, 0, "项目名称", tbProjectName);
            AddField(tbl, 6, 0, "线体号", tbLineNo);
            AddField(tbl, 8, 0, "工位号", tbStationNo);
            AddField(tbl, 10, 0, "单号", tbSeqNo);

            AddField(tbl, 0, 1, "机构号", tbMechNo);
            AddField(tbl, 2, 1, "清单名称", tbListTitle);
            AddField(tbl, 4, 1, "工程师", tbEngineer);
            AddField(tbl, 6, 1, "地区", tbRegion);
            AddField(tbl, 8, 1, "下发", dpIssue);
            AddField(tbl, 10, 1, "交期", dpDelivery);

            tbl.Controls.Add(HeaderLabel("输出目录"), 0, 2);
            tbl.Controls.Add(tbOutput, 1, 2);
            tbl.SetColumnSpan(tbOutput, 10);
            var btnBrowse = Ui.Small("浏览…");
            btnBrowse.Dock = DockStyle.Fill;
            btnBrowse.Margin = Ui.SPad(2, 2, 0, 2);
            btnBrowse.Click += (s, e) => BrowseOutput();
            tbl.Controls.Add(btnBrowse, 11, 2);

            p.Controls.Add(tbl);
            return p;
        }

        Panel BuildRenameCardContent(int rowH) {
            var p = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Rail, Padding = Ui.SPad(10, 6, 10, 6) };
            var rowsBox = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Ui.Rail, Margin = new Padding(0) };
            for (int r = 0; r < 3; r++) rowsBox.RowStyles.Add(new RowStyle(SizeType.Absolute, rowH));

            // 第 1 行：查找替换 + 前后加字
            var r1 = MakeStrip();
            r1.Controls.AddRange(new Control[] {
                StripLabel("查找"), StripBox(findBox, 120),
                StripLabel("替换成"), StripBox(replaceBox, 120),
                StripAct("替换", ApplyRenameReplace),
                FieldGrid.Space(16),
                StripLabel("前面加"), StripBox(prefixBox, 110),
                StripLabel("后面加"), StripBox(suffixBox, 110),
                StripAct("加上", ApplyRenameAffix)
            });

            // 第 2 行：按模板生成（支持实时未写入的属性卡值！）
            var r2 = MakeStrip();
            var tplTip = new Label {
                Text = "{原名} 原文件名 · {序号} 01,02…（{序号:3} 三位）· {图号} / {零件名称} / {任意属性名}（支持刚改完未落盘的值）",
                AutoSize = true, Font = Ui.F8, ForeColor = Ui.Muted, BackColor = Ui.Rail, Margin = Ui.SPad(10, 7, 0, 0)
            };
            r2.Controls.AddRange(new Control[] {
                StripLabel("按模板"), StripBox(templateBox, 230),
                StripAct("生成", ApplyRenameTemplate),
                tplTip
            });

            // 第 3 行：双向联动按钮 + 跟随与补齐开关
            var r3 = MakeStrip();
            var bDnoToName = Ui.Secondary("用图号 → 填新文件名", 150, 28);
            bDnoToName.Margin = Ui.SPad(0, 2, 8, 0);
            bDnoToName.Click += (s, e) => SyncDrawingNoToNewName();

            var bNameToDno = Ui.Secondary("用文件名 → 填图号", 142, 28);
            bNameToDno.Margin = Ui.SPad(0, 2, 16, 0);
            bNameToDno.Click += (s, e) => SyncFileNameToDrawingNo();

            chkFollowRename.AutoSize = true; chkFollowRename.Font = Ui.F8; chkFollowRename.ForeColor = Ui.Text2; chkFollowRename.BackColor = Ui.Rail; chkFollowRename.Margin = Ui.SPad(0, 6, 16, 0);
            Choices.Bind(chkFollowRename, "rename.follow");
            chkFollowRename.CheckedChanged += (s, e) => { if (busy) return; ValidateRenames(); RebuildGrid(); };

            chkFillMissing.AutoSize = true; chkFillMissing.Font = Ui.F8; chkFillMissing.ForeColor = Ui.Text2; chkFillMissing.BackColor = Ui.Rail; chkFillMissing.Margin = Ui.SPad(0, 6, 0, 0);
            Choices.Bind(chkFillMissing, "propcard.fillMissing");
            tips.SetToolTip(chkFillMissing, "写入属性卡时顺便把文件里缺的列补成空值，工程图标题栏不再显示「错误：没有参考」。");

            r3.Controls.AddRange(new Control[] { bDnoToName, bNameToDno, chkFollowRename, chkFillMissing });

            findBox.KeyDown += OnEnter(ApplyRenameReplace);
            replaceBox.KeyDown += OnEnter(ApplyRenameReplace);
            prefixBox.KeyDown += OnEnter(ApplyRenameAffix);
            suffixBox.KeyDown += OnEnter(ApplyRenameAffix);
            templateBox.KeyDown += OnEnter(ApplyRenameTemplate);

            rowsBox.Controls.Add(r1, 0, 0);
            rowsBox.Controls.Add(r2, 0, 1);
            rowsBox.Controls.Add(r3, 0, 2);
            p.Controls.Add(rowsBox);
            return p;
        }

        static FlowLayoutPanel MakeStrip() {
            return new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = Ui.Rail, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0), Padding = new Padding(0) };
        }
        static Label StripLabel(string text) {
            return new Label { Text = text, AutoSize = true, Font = Ui.F8, ForeColor = Ui.Text2, BackColor = Ui.Rail, Margin = Ui.SPad(0, 7, 4, 0) };
        }
        static TextBox StripBox(TextBox tb, int w) {
            tb.BorderStyle = BorderStyle.FixedSingle; tb.Font = Ui.F8; tb.Width = Ui.S(w); tb.Margin = Ui.SPad(0, 4, 8, 0);
            return tb;
        }
        static Control StripAct(string text, Action click) {
            var b = Ui.Secondary(text, 58, 28); b.Margin = Ui.SPad(0, 2, 0, 0); b.Click += (s, e) => click();
            return b;
        }
        static KeyEventHandler OnEnter(Action a) { return (s, e) => { if (e.KeyCode == Keys.Enter) { a(); e.SuppressKeyPress = true; } }; }

        static int MeasureHeaderCol(string sample, int minPx) {
            return Math.Max(Ui.S(minPx), TextRenderer.MeasureText(sample, Ui.F8).Width + Ui.S(14));
        }
        static Label HeaderLabel(string text) {
            return new Label {
                Text = text, TextAlign = ContentAlignment.MiddleRight, Dock = DockStyle.Fill,
                Font = Ui.F8, ForeColor = Ui.Text2, BackColor = Ui.Rail, Margin = Ui.SPad(0, 0, 4, 0)
            };
        }
        static void StyleBox(TextBox tb) {
            tb.BorderStyle = BorderStyle.FixedSingle;
            tb.Font = Ui.F8;
            tb.Dock = DockStyle.Fill;
            tb.Margin = Ui.SPad(0, 3, 6, 3);
        }
        static void AddField(TableLayoutPanel tbl, int col, int row, string label, Control ctrl) {
            tbl.Controls.Add(HeaderLabel(label), col, row);
            tbl.Controls.Add(ctrl, col + 1, row);
        }

        // ---- 表格上方常驻快捷工具栏（使用 VerticalStack autoheight，绝不出现首屏空白区） ----
        Control BuildToolbar() {
            var bar = new FlowLayoutPanel {
                Height = Ui.S(38), MinimumSize = new Size(0, Ui.S(36)), Tag = "autoheight",
                FlowDirection = FlowDirection.LeftToRight, WrapContents = true,
                BackColor = Ui.Bg, Margin = new Padding(0), Padding = Ui.SPad(0, 2, 0, 2)
            };

            drawerSeg = new Segmented("清单表头", "批量改名", "收起");
            drawerSeg.Height = Ui.S(30);
            drawerSeg.Width = Math.Max(Ui.S(200), drawerSeg.PreferredWidth);
            drawerSeg.Margin = Ui.SPad(0, 1, 6, 2);
            drawerSeg.SelectedIndexChanged += (s, e) => { if (!busy) ApplyDrawerMode(drawerSeg.SelectedIndex, true); };

            colViewSeg = new Segmented("全部列", "改名", "属性", "清单");
            colViewSeg.Height = Ui.S(30);
            colViewSeg.Width = Math.Max(Ui.S(210), colViewSeg.PreferredWidth);
            colViewSeg.Margin = Ui.SPad(0, 1, 6, 2);
            colViewSeg.SelectedIndexChanged += (s, e) => { if (!busy) ApplyColumnView(colViewSeg.SelectedIndex, true); };

            var bAll = Ui.Small("全选"); bAll.Click += (s, e) => SetVisibleSelected(true);
            var bNone = Ui.Small("全不选"); bNone.Click += (s, e) => SetVisibleSelected(false);
            var bDraftOnly = Ui.Small("只勾有图"); bDraftOnly.Click += (s, e) => { if (busy) return; PushUndo(); foreach (var r in visibleRows) r.Bom.Selected = !r.IsDraftOnly && r.Bom.HasDraft; RefreshAllVisibleRowsInPlace(); };
            var bMetrics = Ui.Small("重算八大类"); bMetrics.Click += (s, e) => LoadMetricsForSelected();
            tips.SetToolTip(bMetrics, "读取勾选零件的三维外形尺寸与质量，按公司八大类规则重算分类。");
            var bOccQty = Ui.Small("按实例数"); bOccQty.Click += (s, e) => {
                if (busy) return;
                PushUndo();
                foreach (var r in rows) if (r.Bom.Selected) r.SetPropValue("数量", Math.Max(1, r.Bom.OccCount).ToString());
                RefreshAllVisibleRowsInPlace();
                SetStatus("已将勾选行的数量切换为装配实例数（同步记入待写入属性）。", Ui.Muted);
            };
            tips.SetToolTip(bOccQty, "把勾选行的数量设为装配里的实例数。");
            var bReplace = Ui.Small("查找替换"); bReplace.Click += (s, e) => ShowFindReplaceDialog();
            tips.SetToolTip(bReplace, "对任意可编辑列做批量查找替换 (Ctrl+H)，整批可 Ctrl+Z 撤销。");
            foreach (var b in new[] { bAll, bNone, bDraftOnly, bMetrics, bOccQty, bReplace }) b.Margin = Ui.SPad(0, 3, 1, 2);

            clearFiltersBtn = (FlatButton)Ui.Small("清除筛选");
            clearFiltersBtn.SetFore(Ui.Accent);
            clearFiltersBtn.Margin = Ui.SPad(2, 3, 2, 2);
            clearFiltersBtn.Visible = false;
            clearFiltersBtn.Click += (s, e) => { if (!busy) ClearAllFiltersAndSort(); };
            tips.SetToolTip(clearFiltersBtn, "清除所有表头列筛选并恢复默认排序。");

            // 把「选中格填成 + 输入框 + 填入」绑成整体，窗口变窄时整组换行，绝不出现标签和输入框分离
            var fillGroup = new FlowLayoutPanel {
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight, WrapContents = false,
                BackColor = Ui.Bg, Margin = Ui.SPad(4, 0, 0, 0), Padding = new Padding(0)
            };
            var lblFill = new Label { Text = "选中格填成：", AutoSize = true, Font = Ui.F8, ForeColor = Ui.Text2, Margin = Ui.SPad(0, 8, 2, 0) };
            fillBox.BorderStyle = BorderStyle.FixedSingle; fillBox.Font = Ui.F9; fillBox.Width = Ui.S(98); fillBox.Margin = Ui.SPad(0, 3, 3, 2);
            var bFill = Ui.Small("填入"); bFill.Margin = Ui.SPad(0, 3, 0, 2); bFill.Click += (s, e) => FillSelectedCells(fillBox.Text);
            fillBox.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { FillSelectedCells(fillBox.Text); e.SuppressKeyPress = true; } };
            fillGroup.Controls.AddRange(new Control[] { lblFill, fillBox, bFill });

            bar.Controls.AddRange(new Control[] {
                drawerSeg, colViewSeg,
                bAll, bNone, bDraftOnly, bMetrics, bOccQty, bReplace, clearFiltersBtn,
                fillGroup
            });
            Ui.EnableDoubleBuffer(bar);
            return bar;
        }

        void ApplyDrawerMode(int mode, bool persist) {
            bool freeze = mainStack != null && !preview.Visible;
            if (freeze) Ui.BeginRedrawFreeze(mainStack);
            try {
                if (mode == 2) {
                    topDrawer.Visible = false;
                } else {
                    topDrawer.Visible = true;
                    headerPanel.Visible = (mode == 0);
                    renamePanel.Visible = (mode == 1);
                }
                if (persist) Choices.Set("quickbom.drawer", mode.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (mainStack != null) mainStack.Relayout();
            } finally {
                if (freeze) Ui.EndRedrawFreeze(mainStack);
            }
        }

        void ApplyColumnView(int mode, bool persist) {
            colViewMode = mode;
            if (persist) Choices.Set("quickbom.colview", mode.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (grid.Columns.Count < FixedColCount) return;
            Ui.BeginRedrawFreeze(grid);
            grid.SuspendLayout();
            try {
                // 先逆序取消全部冻结列，避免 WinForms 在隐藏列时抛出冻结冲突异常
                for (int i = grid.Columns.Count - 1; i >= 0; i--) if (grid.Columns[i].Frozen) grid.Columns[i].Frozen = false;
                for (int i = 0; i < grid.Columns.Count; i++) {
                    var col = grid.Columns[i];
                    bool want;
                    if (mode == 0) {
                        want = true;
                    } else if (mode == 1) {
                        // 改名视图：勾选、序号、类型、原文件名、新文件名、零件名称、图号、工程图、说明
                        want = i == ColSel || i == ColSeq || i == ColKind || i == ColOldName || i == ColNewName
                            || i == ColPartName || i == ColDno || i == ColDraft || i == statusColIndex;
                    } else if (mode == 2) {
                        // 属性卡视图：勾选、原文件名、材料、核心 6 属性 + 全部扩展属性卡列、说明
                        bool isExtraProp = i >= FixedColCount && i < statusColIndex;
                        want = i == ColSel || i == ColOldName || i == ColMat
                            || i == ColPartName || i == ColDno || i == ColQty || i == ColSurf || i == ColBatch || i == ColPrio
                            || isExtraProp || i == statusColIndex;
                    } else {
                        // 清单视图：勾选、序号、核心清单列、说明（隐藏类型、原文件名、新文件名与扩展属性）
                        want = i == ColSel || i == ColSeq || i == ColPartName || i == ColDno || i == ColMat
                            || i == ColQty || i == ColSurf || i == ColBatch || i == ColPrio || i == ColCat
                            || i == ColChange || i == ColRemark || i == ColCode || i == ColLookupDno || i == ColLookupName
                            || i == ColDraft || i == statusColIndex;
                    }
                    if (col.Visible != want) col.Visible = want;
                }
                // 冻结左侧关键标识列（像 Excel 冻结窗格，横向滚动查看后面列时原文件名/零件名称始终固定在左）
                ApplyFreezePanes(false);
            } finally {
                grid.ResumeLayout();
                Ui.EndRedrawFreeze(grid);
            }
        }

        void ApplyFreezePanes(bool resetExisting = true) {
            if (grid.Columns.Count < FixedColCount) return;
            try {
                if (resetExisting) {
                    for (int i = grid.Columns.Count - 1; i >= 0; i--) if (grid.Columns[i].Frozen) grid.Columns[i].Frozen = false;
                }
                int target = colViewMode == 3 ? ColPartName : ColOldName;
                if (target < 0 || target >= grid.Columns.Count || !grid.Columns[target].Visible) return;
                int frozenW = 0;
                for (int i = 0; i <= target; i++) if (grid.Columns[i].Visible) frozenW += grid.Columns[i].Width;
                // 若当前可视宽度太窄（冻结区占满 65% 以上），自动不冻结以保留横向滚动空间
                if (grid.ClientSize.Width > Ui.S(200) && frozenW > grid.ClientSize.Width * 0.65) return;
                grid.Columns[target].Frozen = true;
            } catch { }
        }

        // ---- 统一主表 DataGridView ----
        void InitGrid() {
            grid.Dock = DockStyle.Fill;
            grid.BackgroundColor = Ui.Bg;
            grid.BorderStyle = BorderStyle.None;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false;
            grid.AllowUserToResizeColumns = true;
            grid.RowHeadersVisible = false;
            grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
            grid.MultiSelect = true;
            grid.EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2;
            grid.ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithoutHeaderText;
            grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
            grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
            grid.EnableHeadersVisualStyles = false;
            grid.ColumnHeadersDefaultCellStyle.BackColor = Ui.Rail;
            grid.ColumnHeadersDefaultCellStyle.ForeColor = Ui.Text2;
            grid.ColumnHeadersDefaultCellStyle.Font = Ui.F8;
            grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Ui.Rail;
            grid.DefaultCellStyle.Font = Ui.F8;
            grid.DefaultCellStyle.ForeColor = Ui.Text;
            grid.DefaultCellStyle.SelectionBackColor = Ui.AccentSoft;
            grid.DefaultCellStyle.SelectionForeColor = Ui.Text;
            grid.GridColor = Ui.Line;
            grid.ShowCellToolTips = true;

            // 开启双缓冲，表头自定义重绘与滚屏更顺滑
            try {
                var pi = typeof(DataGridView).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (pi != null) pi.SetValue(grid, true, null);
            } catch { }

            grid.CurrentCellDirtyStateChanged += (s, e) => {
                if (grid.IsCurrentCellDirty && grid.CurrentCell != null && (grid.CurrentCell.ColumnIndex == ColSel || grid.CurrentCell.ColumnIndex == ColCat)) {
                    grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
                }
            };
            grid.CellBeginEdit += (s, e) => {
                if (busy) { e.Cancel = true; return; }
                if (e.RowIndex < 0 || e.RowIndex >= grid.Rows.Count) return;
                var r = grid.Rows[e.RowIndex].Tag as WorkbenchRow;
                if (r == null) return;
                if (e.ColumnIndex == ColNewName && r.Rename.Locked.Length > 0) {
                    e.Cancel = true;
                    SetStatus(r.Rename.FileName + "：" + r.Rename.Locked, Ui.Warn);
                } else if (IsPropertyColumn(e.ColumnIndex) && !r.CanWriteProps && e.ColumnIndex >= FixedColCount) {
                    e.Cancel = true;
                    SetStatus(r.Rename.FileName + "：" + (r.Prop.Locked.Length > 0 ? r.Prop.Locked : (r.Prop.Problem.Length > 0 ? r.Prop.Problem : "工程图无零件属性卡")), Ui.Warn);
                }
            };
            grid.CellValueChanged += OnGridCellValueChanged;
            grid.CellFormatting += OnGridCellFormatting;
            grid.CellToolTipTextNeeded += OnGridCellToolTipTextNeeded;
            grid.CurrentCellChanged += (s, e) => ShowCurrentPreview();
            grid.CellClick += (s, e) => { if (e.RowIndex >= 0 && lastPreviewPath.Length == 0) ShowCurrentPreview(); };
            grid.SelectionChanged += (s, e) => UpdateSelectionSummary();
            grid.CellPainting += OnGridCellPainting;
            grid.ColumnHeaderMouseClick += OnGridColumnHeaderMouseClick;
            grid.CellMouseMove += OnGridCellMouseMove;
            grid.MouseLeave += (s, e) => SetHoveredFilterCol(-1);
            grid.CellMouseDown += OnGridCellMouseDown;
            grid.ColumnDividerDoubleClick += OnGridColumnDividerDoubleClick;
            grid.ColumnWidthChanged += OnGridColumnWidthChanged;
            grid.EditingControlShowing += OnGridEditingControlShowing;
            grid.KeyDown += OnGridKeyDown;
            grid.DataError += (s, e) => { e.ThrowException = false; };
        }

        void OnGridColumnWidthChanged(object sender, DataGridViewColumnEventArgs e) {
            if (rebuilding || e.Column == null) return;
            string key = (e.Column.Tag as string) ?? "";
            if (key.Length == 0) return;
            int logicalW = (int)Math.Round(e.Column.Width / Math.Max(1f, Ui.DpiScale));
            if (logicalW >= 24 && logicalW <= 2000) {
                savedColWidths[key] = logicalW;
                colWidthDebounce.Stop();
                colWidthDebounce.Start();
            }
        }

        void SaveColumnWidthsNow() {
            try { Choices.Set("quickbom.colwidths", WindowBoundsRules.FormatWidths(savedColWidths)); } catch { }
        }

        void ResetColumnWidths() {
            savedColWidths.Clear();
            SaveColumnWidthsNow();
            RebuildGridColumns();
            RebuildGrid();
            SetStatus("已恢复默认列宽。", Ui.Muted);
        }

        List<string> erpMaterials;
        List<string> erpSurfaces;
        bool erpDictLoaded;

        void EnsureErpDictionaries() {
            if (erpDictLoaded) return;
            erpDictLoaded = true;
            try { BomXlsxWriter.ReadErpDictionaries(customCodeTemplate, out erpMaterials, out erpSurfaces); } catch { }
        }

        void OnGridEditingControlShowing(object sender, DataGridViewEditingControlShowingEventArgs e) {
            var box = e.Control as TextBox;
            if (box == null) return;
            int col = grid.CurrentCell != null ? grid.CurrentCell.ColumnIndex : -1;
            int keyCol = (col == ColMat || col == ColSurf || col == ColBatch || col == ColPrio || col == ColChange) ? col : -1;
            // 同列连续编辑跳过重设 Shell COM IAutoComplete2，避免每次点单元格卡顿
            if (keyCol == lastAcCol) return;
            lastAcCol = keyCol;
            if (keyCol < 0) {
                if (box.AutoCompleteMode != AutoCompleteMode.None) box.AutoCompleteMode = AutoCompleteMode.None;
                if (box.AutoCompleteSource != AutoCompleteSource.None) box.AutoCompleteSource = AutoCompleteSource.None;
                return;
            }
            AutoCompleteStringCollection src;
            if (!acCache.TryGetValue(keyCol, out src)) {
                IEnumerable<string> presets = null;
                IEnumerable<string> existing = null;
                if (keyCol == ColMat) {
                    EnsureErpDictionaries();
                    presets = erpMaterials != null && erpMaterials.Count > 0 ? WorkbenchRules.SuggestedMaterials.Concat(erpMaterials) : (IEnumerable<string>)WorkbenchRules.SuggestedMaterials;
                    existing = rows.Select(r => r.Bom.Material);
                } else if (keyCol == ColSurf) {
                    EnsureErpDictionaries();
                    presets = erpSurfaces != null && erpSurfaces.Count > 0 ? WorkbenchRules.SuggestedSurfaceTreatments.Concat(erpSurfaces) : (IEnumerable<string>)WorkbenchRules.SuggestedSurfaceTreatments;
                    existing = rows.Select(r => r.GetPropValue("表面处理"));
                } else if (keyCol == ColBatch) {
                    presets = WorkbenchRules.SuggestedBatches;
                    existing = rows.Select(r => r.GetPropValue("批次"));
                } else if (keyCol == ColPrio) {
                    presets = WorkbenchRules.SuggestedPriorities;
                    existing = rows.Select(r => r.GetPropValue("优先等级"));
                } else if (keyCol == ColChange) {
                    presets = WorkbenchRules.SuggestedChangeReasons;
                    existing = rows.Select(r => r.Bom.ChangeReason);
                }
                src = new AutoCompleteStringCollection();
                if (presets != null) src.AddRange(WorkbenchRules.BuildSuggestions(presets, existing));
                acCache[keyCol] = src;
            }
            box.AutoCompleteCustomSource = src;
            box.AutoCompleteSource = AutoCompleteSource.CustomSource;
            box.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
        }

        void RebuildGridColumns() {
            rebuilding = true;
            grid.SuspendLayout();
            try {
                grid.Rows.Clear();
                grid.Columns.Clear();

                grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "选", Width = 44, Tag = TagColSel });
                grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "序号", Width = 42, ReadOnly = true, Tag = TagColSeq });
                grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "类型", Width = 64, ReadOnly = true, Tag = TagColKind });
                var colOld = new DataGridViewTextBoxColumn { HeaderText = "原文件名", Width = 176, ReadOnly = true, Tag = TagColOld };
                colOld.DefaultCellStyle.ForeColor = Ui.Muted;
                grid.Columns.Add(colOld);
                grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "新文件名", Width = 176, Tag = TagColNew });

                // 核心共用属性列（改一格同时更新属性卡与加工件清单）
                grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "零件名称", Width = 140, Tag = TagPropPrefix + "零件名称" });
                grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "图号", Width = 152, Tag = TagPropPrefix + "图号" });
                grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "材料", Width = 88, Tag = TagColMat });
                grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "数量", Width = 62, Tag = TagPropPrefix + "数量" });
                grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "表面处理", Width = 132, Tag = TagPropPrefix + "表面处理" });
                grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "批次", Width = 74, Tag = TagPropPrefix + "批次" });
                grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "优先等级", Width = 84, Tag = TagPropPrefix + "优先等级" });

                // 清单专用列
                var catCol = new DataGridViewComboBoxColumn {
                    HeaderText = "八大类", Width = 104, FlatStyle = FlatStyle.Flat, DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton, Tag = TagColCat
                };
                foreach (var lbl in BomRules.CategoryLabels) catCol.Items.Add(lbl);
                grid.Columns.Add(catCol);
                grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "变更原因", Width = 86, Tag = TagColChange });
                grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "备注", Width = 88, Tag = TagColRemark });
                grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "编码", Width = 96, Tag = TagColCode });
                grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "反查图号", Width = 120, Tag = TagColLookupDno });
                grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "反查名称", Width = 120, Tag = TagColLookupName });
                grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "工程图", Width = 76, ReadOnly = true, Tag = TagColDraft });

                // 扩展属性卡列（来自 PropCardFields，自动去除与核心区重复的 6 个字段）
                extraPropFields = WorkbenchRules.ExtraPropFields(propFields);
                foreach (var f in extraPropFields) {
                    grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = f, Width = 96, Tag = TagPropPrefix + f });
                }

                statusColIndex = grid.Columns.Count;
                grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "说明 / 状态", Width = 200, ReadOnly = true, Tag = TagColStatus });

                // 预计算每列对应的属性字段名，消除 CellFormatting 高频绘制时的 Substring 分配
                colPropFields = new string[grid.Columns.Count];
                for (int i = 0; i < grid.Columns.Count; i++) {
                    var tag = grid.Columns[i].Tag as string;
                    colPropFields[i] = (tag != null && tag.StartsWith(TagPropPrefix, StringComparison.Ordinal))
                        ? tag.Substring(TagPropPrefix.Length) : "";
                }

                foreach (DataGridViewColumn col in grid.Columns) col.SortMode = DataGridViewColumnSortMode.NotSortable;
                Ui.FitGrid(grid);
                // 为每个可筛选列的表头留足「标题 + 排序箭头 + 右侧筛选按钮」宽度，避免 125% DPI 重叠；同时应用用户手动调过的列宽
                var hf = HeaderActiveFont;
                foreach (DataGridViewColumn col in grid.Columns) {
                    string key = (col.Tag as string) ?? "";
                    int savedW;
                    if (key.Length > 0 && savedColWidths.TryGetValue(key, out savedW)) {
                        col.Width = Ui.S(savedW);
                    }
                    if (key == TagColSeq) continue;
                    int need = TextRenderer.MeasureText((col.HeaderText ?? "") + " ▲", hf).Width + Ui.S(34);
                    if (col.Width < need) col.Width = need;
                }
                ApplyColumnView(colViewMode, false);
            } finally {
                grid.ResumeLayout();
                rebuilding = false;
            }
        }

        string ColKey(int colIndex) {
            if (colIndex < 0 || colIndex >= grid.Columns.Count) return "";
            return (grid.Columns[colIndex].Tag as string) ?? "";
        }

        bool IsFilterableColumn(int colIndex) {
            string key = ColKey(colIndex);
            return key.Length > 0 && key != TagColSeq;
        }

        // ---- 类 Excel 表头自定义绘制、点击排序与列筛选弹窗 ----

        void SetHoveredFilterCol(int colIndex) {
            if (hoveredFilterCol == colIndex) return;
            int prev = hoveredFilterCol;
            hoveredFilterCol = colIndex;
            try {
                if (prev >= 0 && prev < grid.Columns.Count) grid.InvalidateCell(prev, -1);
                if (hoveredFilterCol >= 0 && hoveredFilterCol < grid.Columns.Count) grid.InvalidateCell(hoveredFilterCol, -1);
            } catch { }
        }

        void OnGridCellMouseMove(object sender, DataGridViewCellMouseEventArgs e) {
            if (e.RowIndex != -1) {
                if (hoveredFilterCol != -1) SetHoveredFilterCol(-1);
                return;
            }
            if (e.ColumnIndex >= 0 && IsFilterableColumn(e.ColumnIndex)) {
                int w = grid.Columns[e.ColumnIndex].Width;
                SetHoveredFilterCol(e.X >= w - Ui.S(24) ? e.ColumnIndex : -1);
            } else {
                SetHoveredFilterCol(-1);
            }
        }

        void OnGridCellPainting(object sender, DataGridViewCellPaintingEventArgs e) {
            if (e.RowIndex != -1 || e.ColumnIndex < 0 || e.ColumnIndex >= grid.Columns.Count) return;
            var g = e.Graphics;
            string key = ColKey(e.ColumnIndex);
            bool filterable = key.Length > 0 && key != TagColSeq;
            bool filtered = filterable && columnFilters.ContainsKey(key);
            bool sorted = filterable && sortDir != 0 && string.Equals(sortColKey, key, StringComparison.Ordinal);

            g.FillRectangle(filtered ? HeaderChipBrush : (sorted ? HeaderSortBrush : HeaderRailBrush), e.CellBounds);
            g.DrawLine(HeaderLinePen, e.CellBounds.Left, e.CellBounds.Bottom - 1, e.CellBounds.Right - 1, e.CellBounds.Bottom - 1);
            g.DrawLine(HeaderLinePen, e.CellBounds.Right - 1, e.CellBounds.Top, e.CellBounds.Right - 1, e.CellBounds.Bottom - 1);
            if (sorted || filtered) {
                g.DrawLine(HeaderAccentPen, e.CellBounds.Left, e.CellBounds.Bottom - 1, e.CellBounds.Right - 1, e.CellBounds.Bottom - 1);
            }

            int bw = Ui.S(18);
            int rightReserve = filterable ? bw + Ui.S(8) : Ui.S(6);
            string title = grid.Columns[e.ColumnIndex].HeaderText ?? "";
            if (sorted) title += sortDir > 0 ? " ▲" : " ▼";

            var textRect = new Rectangle(
                e.CellBounds.X + Ui.S(6),
                e.CellBounds.Y,
                Math.Max(4, e.CellBounds.Width - Ui.S(6) - rightReserve),
                e.CellBounds.Height);
            Font font = (sorted || filtered) ? HeaderActiveFont : (grid.ColumnHeadersDefaultCellStyle.Font ?? Ui.F8);
            Color fg = (sorted || filtered) ? Ui.Accent : Ui.Text2;
            TextRenderer.DrawText(g, title, font, textRect, fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);

            if (filterable) {
                int bh = Math.Max(Ui.S(16), e.CellBounds.Height - Ui.S(10));
                var btnRect = new Rectangle(
                    e.CellBounds.Right - bw - Ui.S(3),
                    e.CellBounds.Y + (e.CellBounds.Height - bh) / 2,
                    bw, bh);
                if (filtered) {
                    Ui.FillRound(g, btnRect, Ui.S(4), Ui.Accent);
                    DrawDownTriangle(g, btnRect, HeaderWhiteBrush);
                } else if (hoveredFilterCol == e.ColumnIndex) {
                    Ui.FillRound(g, btnRect, Ui.S(4), Ui.Line);
                    DrawDownTriangle(g, btnRect, HeaderTextBrush);
                } else {
                    DrawDownTriangle(g, btnRect, HeaderFaintBrush);
                }
            }
            e.Handled = true;
        }

        static void DrawDownTriangle(Graphics g, Rectangle r, Brush brush) {
            var sm = g.SmoothingMode;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            float cx = r.X + r.Width * 0.5f;
            float cy = r.Y + r.Height * 0.52f;
            float hw = Math.Max(3.0f, r.Width * 0.22f);
            float hh = Math.Max(2.2f, hw * 0.65f);
            var pts = new[] {
                new PointF(cx - hw, cy - hh * 0.7f),
                new PointF(cx + hw, cy - hh * 0.7f),
                new PointF(cx, cy + hh)
            };
            g.FillPolygon(brush, pts);
            g.SmoothingMode = sm;
        }

        void OnGridColumnHeaderMouseClick(object sender, DataGridViewCellMouseEventArgs e) {
            if (busy || e.ColumnIndex < 0 || e.ColumnIndex >= grid.Columns.Count) return;
            grid.EndEdit();
            string key = ColKey(e.ColumnIndex);
            if (key == TagColSeq) {
                if (sortDir != 0) {
                    sortColKey = ""; sortDir = 0;
                    RebuildGrid();
                    SetStatus("已恢复默认顺序。", Ui.Muted);
                }
                return;
            }
            if (!IsFilterableColumn(e.ColumnIndex)) return;

            int colW = grid.Columns[e.ColumnIndex].Width;
            bool hitFilter = (e.Button == MouseButtons.Right) || (e.X >= colW - Ui.S(24));
            if (hitFilter) {
                OpenColumnFilterPopup(e.ColumnIndex);
                return;
            }
            if (e.Button == MouseButtons.Left) {
                if (!string.Equals(sortColKey, key, StringComparison.Ordinal)) {
                    sortColKey = key; sortDir = 1;
                } else if (sortDir == 1) {
                    sortDir = -1;
                } else {
                    sortColKey = ""; sortDir = 0;
                }
                RebuildGrid();
                string h = grid.Columns[e.ColumnIndex].HeaderText;
                SetStatus(sortDir == 0 ? "已恢复默认顺序。" : ("已按「" + h + "」" + (sortDir > 0 ? "升序" : "降序") + "排列。"), Ui.Muted);
            }
        }

        void OnGridColumnDividerDoubleClick(object sender, DataGridViewColumnDividerDoubleClickEventArgs e) {
            if (e.ColumnIndex < 0 || e.ColumnIndex >= grid.Columns.Count) return;
            e.Handled = true;
            var col = grid.Columns[e.ColumnIndex];
            string key = ColKey(e.ColumnIndex);
            int minW = TextRenderer.MeasureText((col.HeaderText ?? "") + " ▲", HeaderActiveFont).Width + (key == TagColSeq ? Ui.S(18) : Ui.S(34));
            int best = minW;
            var cf = grid.DefaultCellStyle.Font ?? Ui.F8;
            int maxScan = Math.Min(grid.Rows.Count, 300);
            for (int r = 0; r < maxScan; r++) {
                string txt = Convert.ToString(grid.Rows[r].Cells[e.ColumnIndex].FormattedValue) ?? "";
                if (txt.Length == 0) continue;
                int w = TextRenderer.MeasureText(txt, cf).Width + Ui.S(20);
                if (w > best) best = w;
            }
            col.Width = Math.Min(best, Ui.S(460));
        }

        ToolStripDropDown OpenColumnFilterPopup(int colIndex) {
            if (colIndex < 0 || colIndex >= grid.Columns.Count || !IsFilterableColumn(colIndex)) return null;
            string key = ColKey(colIndex);
            var col = grid.Columns[colIndex];
            Dictionary<string, int> dnoCounts;
            var candidates = CollectCandidateRows(key, out dnoCounts);
            var distinct = WorkbenchRules.DistinctColumnValues(candidates, key, BuildRowStatus);
            HashSet<string> currentAllowed;
            columnFilters.TryGetValue(key, out currentAllowed);

            var rect = grid.GetCellDisplayRectangle(colIndex, -1, true);
            Point screenPt = grid.PointToScreen(new Point(rect.Left, rect.Bottom));

            var drop = ColumnFilterPopup.Show(
                grid,
                screenPt,
                col.HeaderText,
                distinct,
                currentAllowed,
                string.Equals(sortColKey, key, StringComparison.Ordinal) ? sortDir : 0,
                dir => {
                    sortColKey = dir == 0 ? "" : key;
                    sortDir = dir;
                    RebuildGrid();
                    SetStatus(dir == 0 ? "已恢复默认顺序。" : ("已按「" + col.HeaderText + "」" + (dir > 0 ? "升序" : "降序") + "排列。"), Ui.Muted);
                },
                allowed => {
                    if (allowed == null) columnFilters.Remove(key);
                    else columnFilters[key] = allowed;
                    RebuildGrid();
                    SetStatus(allowed == null
                        ? ("已清除「" + col.HeaderText + "」列筛选。")
                        : ("已按「" + col.HeaderText + "」筛选：显示 " + visibleRows.Count + " / " + rows.Count + " 行。"), Ui.Muted);
                });
            if (drop != null) {
                keyShield.AttachPopup(drop);
                drop.Closed += (s, ev) => keyShield.DetachPopup(drop);
            }
            return drop;
        }

        void ClearAllFiltersAndSort() {
            columnFilters.Clear();
            sortColKey = "";
            sortDir = 0;
            RebuildGrid();
            SetStatus("已清除所有列筛选并恢复默认排序。", Ui.Muted);
        }

        void UpdateFilterUi() {
            if (clearFiltersBtn == null) return;
            bool show = columnFilters.Count > 0 || sortDir != 0;
            bool visChanged = clearFiltersBtn.Visible != show;
            if (show) {
                string txt = columnFilters.Count > 0 ? "清筛选(" + columnFilters.Count + ")" : "重置排序";
                clearFiltersBtn.Text = txt;
                clearFiltersBtn.Width = TextRenderer.MeasureText(txt, Ui.F8).Width + Ui.S(18);
            }
            clearFiltersBtn.Visible = show;
            if (visChanged && mainStack != null) mainStack.Relayout();
        }

        bool IsPropertyColumn(int colIndex) {
            return colIndex >= 0 && colIndex < colPropFields.Length && colPropFields[colIndex].Length > 0;
        }

        string PropFieldOfColumn(int colIndex) {
            return colIndex >= 0 && colIndex < colPropFields.Length ? colPropFields[colIndex] : "";
        }

        // ---- 载入来源 ----

        public void LoadActive() {
            if (busy || !ConfirmDiscard()) return;
            BeginBusy("载入当前文档");
            try {
                string source, srcPath;
                var list = bomCad.CollectFromActive(out source, out srcPath, Pulse);
                if (list.Count == 0) {
                    SetStatus("当前没有打开适用文档。可点左侧「扫描模型文件夹…」直接选模型文件夹。", Ui.Muted);
                    return;
                }
                propCad.RefreshActiveContext();
                renameCad.SetRootPath(srcPath);
                ReplaceRows(list);
                lastScannedFolder = "";
                sourceFolder = string.IsNullOrEmpty(srcPath) ? "" : (Directory.Exists(srcPath) ? srcPath : Path.GetDirectoryName(srcPath));
                // 切到新文档时恢复按新表头自动推荐输出目录，避免复用窗口后误覆盖上一装配手动选过的旧输出目录
                outputManuallyEdited = false;
                shell.SetTitle(Title, source);
                ApplyInferredHeader(srcPath, true);
                ValidateRenames();
                RebuildGrid();
                renameCad.Warm(rows.Select(r => r.Rename.Path));
                string msg = "已载入 " + source + "：共 " + rows.Count + " 个文件，默认勾选 " + rows.Count(r => r.Bom.Selected) + " 个加工件";
                if (bomCad.SkippedSubs.Count > 0) msg += "（跳过 " + bomCad.SkippedSubs.Count + " 个子装配）";
                SetStatus(msg, Ui.Text);
            } catch (Exception e) {
                Log.Write("QuickBomLoadActive", e);
                SetStatus("载入失败：" + e.Message, Ui.Bad);
            } finally {
                EndBusy();
            }
        }

        void PickAndScanFolder() {
            if (busy) return;
            string init = !string.IsNullOrEmpty(sourceFolder) && Directory.Exists(sourceFolder)
                ? sourceFolder : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string dir = FolderPicker.Pick(this, "选择包含三维零件 / 装配及工程图（.dft）的模型文件夹", init, "quickbom.scan");
            if (!string.IsNullOrEmpty(dir)) LoadFolder(dir, chkRecursive.Checked);
        }

        void OnFormDragEnter(object sender, DragEventArgs e) {
            if (!busy && e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
            else e.Effect = DragDropEffects.None;
        }

        void OnFormDragDrop(object sender, DragEventArgs e) {
            if (busy || e.Data == null || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            var dropped = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (dropped == null || dropped.Length == 0) return;

            var dirs = new List<string>();
            var files = new List<string>();
            foreach (string raw in dropped) {
                if (string.IsNullOrEmpty(raw)) continue;
                if (Directory.Exists(raw)) dirs.Add(raw);
                else if (File.Exists(raw) && (BomRules.IsModelExtension(raw) || raw.EndsWith(".dft", StringComparison.OrdinalIgnoreCase))) files.Add(raw);
            }

            if (dirs.Count == 1 && files.Count == 0) {
                if (rows.Count == 0) {
                    LoadFolder(dirs[0], chkRecursive.Checked);
                    return;
                }
                var ask = MessageBox.Show(this,
                    "已拖入文件夹：\r\n" + dirs[0] + "\r\n\r\n"
                    + "• 点「是」：扫描此文件夹并替换当前列表\r\n"
                    + "• 点「否」：把该文件夹内的模型追加到当前列表\r\n"
                    + "• 点「取消」：不操作",
                    Title, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (ask == DialogResult.Cancel) return;
                if (ask == DialogResult.Yes) { LoadFolder(dirs[0], chkRecursive.Checked); return; }
            }

            // 展开拖入的文件夹与文件并追加到当前列表（自动去重）
            BeginBusy("添加拖入文件");
            try {
                var addedItems = new List<BomItem>();
                foreach (string d in dirs) {
                    addedItems.AddRange(bomCad.CollectFromFolder(d, chkRecursive.Checked, Pulse));
                }
                if (files.Count > 0) {
                    addedItems.AddRange(bomCad.CollectFromFiles(files, Pulse));
                }
                var existing = new HashSet<string>(rows.Select(r => r.Rename.Path), StringComparer.OrdinalIgnoreCase);
                int count = 0;
                foreach (var it in addedItems) {
                    if (existing.Add(it.ModelPath)) {
                        rows.Add(WorkbenchRules.CreateRow(it, File.Exists));
                        count++;
                    }
                }
                if (string.IsNullOrEmpty(sourceFolder)) {
                    if (dirs.Count > 0) sourceFolder = dirs[0];
                    else if (files.Count > 0) sourceFolder = Path.GetDirectoryName(files[0]);
                }
                if (count > 0 && string.IsNullOrWhiteSpace(tbProjectCode.Text)) ApplyInferredHeader(sourceFolder, false);
                ValidateRenames();
                RebuildGrid();
                if (count > 0) renameCad.Warm(rows.Select(r => r.Rename.Path));
                SetStatus(count > 0 ? ("已拖入并新增 " + count + " 个文件。") : "拖入的文件已在当前列表中（或没有天工模型/工程图）。", count > 0 ? Ui.Text : Ui.Muted);
            } catch (Exception ex) {
                Log.Write("QuickBomDragDrop", ex);
                SetStatus("添加拖入文件失败：" + ex.Message, Ui.Bad);
            } finally {
                EndBusy();
            }
        }

        public void LoadFolder(string folder, bool recursive) {
            if (busy || string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
            if (!ConfirmDiscard()) return;
            BeginBusy("扫描文件夹");
            try {
                var list = bomCad.CollectFromFolder(folder, recursive, Pulse);
                propCad.ResetContext();
                renameCad.SetRootPath("");
                ReplaceRows(list);
                sourceFolder = folder;
                lastScannedFolder = folder;
                outputManuallyEdited = false;
                string folderName = Path.GetFileName(folder.TrimEnd('\\', '/'));
                shell.SetTitle(Title, "文件夹「" + folderName + "」" + (recursive ? "（含子文件夹）" : "（仅当前层）"));
                ApplyInferredHeader(folder, true);
                ValidateRenames();
                RebuildGrid();
                renameCad.Warm(rows.Select(r => r.Rename.Path));
                string scope = recursive ? "（含子文件夹）" : "（仅当前层）";
                SetStatus("已扫描文件夹「" + folderName + "」" + scope + "：共 " + rows.Count + " 个三维文件，默认勾选 " + rows.Count(r => r.Bom.Selected) + " 个加工件。", Ui.Text);
            } catch (Exception e) {
                Log.Write("QuickBomScanFolder", e);
                SetStatus("扫描失败：" + e.Message, Ui.Bad);
            } finally {
                EndBusy();
            }
        }

        void AddFiles() {
            if (busy) return;
            using (var dlg = new OpenFileDialog {
                Title = "添加三维模型或工程图",
                Multiselect = true,
                Filter = "天工文件 (*.par;*.psm;*.asm;*.dft)|*.par;*.psm;*.asm;*.dft|所有文件 (*.*)|*.*"
            }) {
                if (!string.IsNullOrEmpty(sourceFolder) && Directory.Exists(sourceFolder)) dlg.InitialDirectory = sourceFolder;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                BeginBusy("读取文件");
                try {
                    var added = bomCad.CollectFromFiles(dlg.FileNames, Pulse);
                    var existing = new HashSet<string>(rows.Select(r => r.Rename.Path), StringComparer.OrdinalIgnoreCase);
                    int count = 0;
                    foreach (var it in added) {
                        if (existing.Add(it.ModelPath)) {
                            rows.Add(WorkbenchRules.CreateRow(it, File.Exists));
                            count++;
                        }
                    }
                    if (string.IsNullOrEmpty(sourceFolder) && dlg.FileNames.Length > 0) {
                        sourceFolder = Path.GetDirectoryName(dlg.FileNames[0]);
                    }
                    if (count > 0 && string.IsNullOrWhiteSpace(tbProjectCode.Text)) ApplyInferredHeader(sourceFolder, false);
                    ValidateRenames();
                    RebuildGrid();
                    if (count > 0) renameCad.Warm(rows.Select(r => r.Rename.Path));
                    SetStatus("已添加 " + count + " 个文件。", Ui.Text);
                } finally {
                    EndBusy();
                }
            }
        }

        void ReplaceRows(IList<BomItem> list) {
            rows.Clear();
            columnFilters.Clear();
            sortColKey = "";
            sortDir = 0;
            undoStack.Clear();
            redoStack.Clear();
            acCache.Clear();
            lastAcCol = -1;
            lastPreviewPath = "";
            foreach (var it in list) rows.Add(WorkbenchRules.CreateRow(it, File.Exists));
        }

        void LoadMetricsForSelected() {
            if (busy) return;
            var todo = rows.Where(r => r.Bom.Selected && !r.IsDraftOnly).Select(r => r.Bom).ToList();
            if (todo.Count == 0) { SetStatus("没有勾选的零件。", Ui.Muted); return; }
            BeginBusy("读取尺寸与重量", true);
            runBtn.Text = "停止";
            try {
                int n = bomCad.LoadMetricsForItems(todo, Pulse, () => stopRequested);
                RebuildGrid();
                SetStatus("已读取 " + n + " 个零件的尺寸与重量，并按规则重算八大类。", Ui.Ok);
            } finally {
                runBtn.Text = "开始出清单";
                EndBusy();
            }
        }

        // ---- 批量改名工具（查找替换 / 前后加字 / 按模板 / 图号↔文件名） ----

        List<WorkbenchRow> TargetRowsForBatch() {
            var picked = new HashSet<WorkbenchRow>();
            foreach (DataGridViewCell c in grid.SelectedCells) {
                if (c.RowIndex >= 0 && c.RowIndex < grid.Rows.Count) {
                    var r = grid.Rows[c.RowIndex].Tag as WorkbenchRow;
                    if (r != null) picked.Add(r);
                }
            }
            if (picked.Count >= 2) return visibleRows.Where(picked.Contains).ToList();
            return new List<WorkbenchRow>(visibleRows);
        }

        void ApplyRenameReplace() {
            if (busy) return;
            string find = findBox.Text ?? "";
            if (find.Length == 0) { SetStatus("请先输入要查找的文字。", Ui.Warn); return; }
            var targets = TargetRowsForBatch();
            PushUndo();
            int n = 0;
            foreach (var r in targets) {
                if (r.Rename.Locked.Length > 0) continue;
                string next = RenameRules.Replace(r.Rename.NewBase, find, replaceBox.Text ?? "");
                if (!string.Equals(next, r.Rename.NewBase, StringComparison.Ordinal)) { r.Rename.SetNewBase(next); n++; }
            }
            if (n == 0) PopUnusedUndo();
            ValidateRenames();
            RefreshAllVisibleRowsInPlace();
            SetStatus("已对 " + n + " 个文件应用查找替换。", Ui.Muted);
        }

        void ApplyRenameAffix() {
            if (busy) return;
            string pre = prefixBox.Text ?? "", suf = suffixBox.Text ?? "";
            if (pre.Length == 0 && suf.Length == 0) { SetStatus("请先输入前面加或后面加的文字。", Ui.Warn); return; }
            var targets = TargetRowsForBatch();
            PushUndo();
            int n = 0;
            foreach (var r in targets) {
                if (r.Rename.Locked.Length > 0) continue;
                r.Rename.SetNewBase(pre + r.Rename.NewBase + suf);
                n++;
            }
            if (n == 0) PopUnusedUndo();
            ValidateRenames();
            RefreshAllVisibleRowsInPlace();
            SetStatus("已对 " + n + " 个文件加上前后缀。", Ui.Muted);
        }

        void ApplyRenameTemplate() {
            if (busy) return;
            string tpl = (templateBox.Text ?? "").Trim();
            if (tpl.Length == 0) { SetStatus("请先输入命名模板（例如 {图号} 或 {图号}-{零件名称}）。", Ui.Warn); return; }
            var targets = TargetRowsForBatch();
            PushUndo();
            int ok = 0, skipped = 0;
            var missingFields = new List<string>();
            int idx = 0;
            foreach (var r in targets) {
                if (r.Rename.Locked.Length > 0) continue;
                idx++;
                var miss = new List<string>();
                string next = RenameRules.Template(tpl, r.Rename.OldBase, idx, r.EffectiveTemplateProps(), miss);
                if (miss.Count > 0) {
                    skipped++;
                    foreach (var m in miss) if (!missingFields.Contains(m)) missingFields.Add(m);
                    continue;
                }
                if (next.Length > 0) { r.Rename.SetNewBase(next); ok++; }
            }
            if (ok == 0) PopUnusedUndo();
            ValidateRenames();
            RefreshAllVisibleRowsInPlace();
            string msg = "按模板生成 " + ok + " 个新文件名";
            if (skipped > 0) msg += "；跳过 " + skipped + " 个缺属性的文件（缺：" + string.Join("、", missingFields.ToArray()) + "）";
            SetStatus(msg + "。", skipped > 0 ? Ui.Warn : Ui.Ok);
        }

        void SyncDrawingNoToNewName() {
            if (busy) return;
            var targets = TargetRowsForBatch();
            PushUndo();
            int n = WorkbenchRules.SyncDrawingNoToNewName(targets);
            if (n == 0) PopUnusedUndo();
            ValidateRenames();
            RefreshAllVisibleRowsInPlace();
            SetStatus("已按图号为 " + n + " 个文件填入新文件名（点「执行改名」生效）。", Ui.Text);
        }

        void SyncFileNameToDrawingNo() {
            if (busy) return;
            var targets = TargetRowsForBatch();
            PushUndo();
            int n = WorkbenchRules.SyncFileNameToDrawingNo(targets);
            if (n == 0) PopUnusedUndo();
            RefreshAllVisibleRowsInPlace();
            SetStatus("已按文件名同步 " + n + " 个文件的图号（黄底，点「写入属性」落盘）。", Ui.Text);
        }

        void ValidateRenames() {
            bool anyChangedOrProblem = false;
            for (int i = 0; i < rows.Count; i++) {
                if (rows[i].RenameChanged || rows[i].RenameProblem.Length > 0) { anyChangedOrProblem = true; break; }
            }
            if (!anyChangedOrProblem) { lastRenameProblems = 0; return; }
            // 按目录一次性枚举磁盘文件名做内存缓存，避免对透明加密盘同步执行数百次 File.Exists 卡顿界面
            var folderCache = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            Func<string, bool> fastExists = path => {
                if (string.IsNullOrEmpty(path)) return false;
                try {
                    string dir = Path.GetDirectoryName(path) ?? "";
                    HashSet<string> set;
                    if (!folderCache.TryGetValue(dir, out set)) {
                        set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        if (dir.Length > 0 && Directory.Exists(dir)) {
                            foreach (var f in Directory.GetFiles(dir)) set.Add(f);
                        }
                        folderCache[dir] = set;
                    }
                    return set.Contains(path);
                } catch { return false; }
            };
            WorkbenchRules.BuildAndValidateRenameBatch(rows, chkFollowRename.Checked, fastExists, out lastRenameProblems);
        }

        // ---- 表头与设置 ----

        void LoadRememberedDefaults() {
            suppressHeaderEvents = true;
            try {
                tbCustomer.Text = Choices.Get("quickbom.customer", "");
                tbLineNo.Text = Choices.Get("quickbom.lineNo", "");
                tbStationNo.Text = Choices.Get("quickbom.stationNo", "");
                tbSeqNo.Text = Choices.Get("quickbom.seqNo", "01");
                tbRegion.Text = Choices.Get("quickbom.region", "上海");
                tbEngineer.Text = Choices.Get("quickbom.engineer", "");
                dpIssue.Value = DateTime.Today;
                dpDelivery.Value = DateTime.Today.AddDays(12);
                customListTemplate = Choices.Get("quickbom.tpl.list", "");
                customCodeTemplate = Choices.Get("quickbom.tpl.code", "");
            } finally {
                suppressHeaderEvents = false;
            }
        }

        void SaveRememberedDefaults() {
            Choices.Set("quickbom.customer", tbCustomer.Text.Trim());
            Choices.Set("quickbom.lineNo", tbLineNo.Text.Trim());
            Choices.Set("quickbom.stationNo", tbStationNo.Text.Trim());
            Choices.Set("quickbom.seqNo", tbSeqNo.Text.Trim());
            Choices.Set("quickbom.region", tbRegion.Text.Trim());
            Choices.Set("quickbom.engineer", tbEngineer.Text.Trim());
            Choices.Set("quickbom.tpl.list", customListTemplate);
            Choices.Set("quickbom.tpl.code", customCodeTemplate);
        }

        void ApplyInferredHeader(string srcPath, bool clearPerAssembly) {
            var h = ReadHeaderFromUi();
            // 切换到新装配 / 新文件夹时先清空按装配归属的表头字段，避免新装配缺项目号或项目名称时静默沿用上一装配的残留值
            if (clearPerAssembly) {
                h.ProjectCode = "";
                h.ProjectName = "";
                h.MechanismNo = "";
                h.ListTitle = "";
            }
            BomRules.InferHeader(rows.Select(r => r.Bom).ToList(), srcPath, h);
            suppressHeaderEvents = true;
            try {
                tbProjectCode.Text = h.ProjectCode;
                tbProjectName.Text = h.ProjectName;
                tbMechNo.Text = h.MechanismNo;
                tbListTitle.Text = h.ListTitle;
                if (!string.IsNullOrWhiteSpace(h.EngineerName)) tbEngineer.Text = h.EngineerName;
            } finally {
                suppressHeaderEvents = false;
            }
            UpdateSuggestedOutput();
        }

        void WireHeaderAutoOutput() {
            EventHandler onChange = (s, e) => { if (!suppressHeaderEvents) UpdateSuggestedOutput(); };
            tbProjectCode.TextChanged += onChange;
            tbStationNo.TextChanged += onChange;
            tbSeqNo.TextChanged += onChange;
            tbListTitle.TextChanged += onChange;
            tbEngineer.TextChanged += onChange;
            dpIssue.ValueChanged += onChange;
            tbOutput.TextChanged += (s, e) => { if (!suppressHeaderEvents) outputManuallyEdited = true; };
        }

        void UpdateSuggestedOutput() {
            if (outputManuallyEdited) return;
            suppressHeaderEvents = true;
            try {
                tbOutput.Text = BomRules.SuggestDefaultOutputFolder(sourceFolder, ReadHeaderFromUi());
            } finally {
                suppressHeaderEvents = false;
            }
        }

        BomHeader ReadHeaderFromUi() {
            return new BomHeader {
                Customer = tbCustomer.Text.Trim(),
                ProjectCode = tbProjectCode.Text.Trim(),
                ProjectName = tbProjectName.Text.Trim(),
                ProjectAttr = "加工件",
                LineNo = tbLineNo.Text.Trim(),
                StationNo = tbStationNo.Text.Trim(),
                SequenceNo = tbSeqNo.Text.Trim(),
                MechanismNo = tbMechNo.Text.Trim(),
                ListTitle = tbListTitle.Text.Trim(),
                EngineerRegion = tbRegion.Text.Trim(),
                EngineerName = tbEngineer.Text.Trim(),
                IssueDate = dpIssue.Value.Date,
                DeliveryDate = dpDelivery.Value.Date
            };
        }

        BomOptions ReadOptionsFromUi() {
            return new BomOptions {
                OutputFolder = tbOutput.Text.Trim(),
                IncludeProcessingList = true,
                IncludeCodeRequest = chkCodeReq.Checked,
                IncludeThumbnails = chkThumb.Checked,
                IncludeNative3d = chk3d.Checked,
                IncludeDraft = chkDft.Checked,
                IncludeDwg = chkDwg.Checked,
                IncludeJpg = chkJpg.Checked,
                IncludePdf = chkPdf.Checked,
                IncludeStep = chkStep.Checked,
                CreateZip = chkZip.Checked,
                OverwriteExisting = chkOverwrite.Checked,
                CustomListTemplate = customListTemplate,
                CustomCodeTemplate = customCodeTemplate
            };
        }

        void BrowseOutput() {
            string dir = FolderPicker.Pick(this, "选择清单输出目录", tbOutput.Text.Trim(), "quickbom.output");
            if (string.IsNullOrEmpty(dir)) return;
            outputManuallyEdited = true;
            suppressHeaderEvents = true;
            try { tbOutput.Text = dir; } finally { suppressHeaderEvents = false; }
        }

        // ---- 表格渲染与实时联动 ----

        // 收集通过左侧状态导航 + 关键词搜索 + 列筛选（可跳过 excludeColKey 以支持级联筛选菜单）的行
        List<WorkbenchRow> CollectCandidateRows(string excludeColKey, out Dictionary<string, int> dnoCounts) {
            var list = new List<WorkbenchRow>();
            string kw = (filterBox.Text ?? "").Trim();
            dnoCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in rows) {
                if (!r.Bom.Selected) continue;
                string k = BomRules.SanitizeFileName(r.Bom.DrawingNo);
                int c; dnoCounts.TryGetValue(k, out c); dnoCounts[k] = c + 1;
            }
            foreach (var r in rows) {
                var it = r.Bom;
                bool dup = it.Selected && dnoCounts.ContainsKey(BomRules.SanitizeFileName(it.DrawingNo)) && dnoCounts[BomRules.SanitizeFileName(it.DrawingNo)] > 1;
                bool hasBomIssue = it.Selected && (!it.HasDraft || string.IsNullOrWhiteSpace(it.PartName) || string.IsNullOrWhiteSpace(it.DrawingNo) || string.IsNullOrWhiteSpace(it.Material) || it.Quantity <= 0 || dup);
                bool hasProblem = hasBomIssue || r.RenameProblem.Length > 0 || r.Prop.Problem.Length > 0;

                if (stateFilter == 1 && !it.Selected) continue;
                if (stateFilter == 2 && !r.RenameChanged) continue;
                if (stateFilter == 3 && !r.PropDirty) continue;
                if (stateFilter == 4 && !hasProblem) continue;

                if (kw.Length > 0
                    && (r.Rename.FileName ?? "").IndexOf(kw, StringComparison.OrdinalIgnoreCase) < 0
                    && (r.Rename.NewBase ?? "").IndexOf(kw, StringComparison.OrdinalIgnoreCase) < 0
                    && (it.PartName ?? "").IndexOf(kw, StringComparison.OrdinalIgnoreCase) < 0
                    && (it.DrawingNo ?? "").IndexOf(kw, StringComparison.OrdinalIgnoreCase) < 0
                    && (it.SubFolder ?? "").IndexOf(kw, StringComparison.OrdinalIgnoreCase) < 0) continue;

                if (columnFilters.Count > 0 && !WorkbenchRules.PassesColumnFilters(r, BuildRowStatus(r), columnFilters, excludeColKey)) continue;
                list.Add(r);
            }
            return list;
        }

        void RefreshRailAndMetrics() {
            cachedDnoCounts.Clear();
            dupDnoRows.Clear();
            foreach (var r in rows) {
                if (!r.Bom.Selected) continue;
                string k = BomRules.SanitizeFileName(r.Bom.DrawingNo);
                int c; cachedDnoCounts.TryGetValue(k, out c); cachedDnoCounts[k] = c + 1;
            }
            int totalAll = rows.Count;
            int totalSel = 0, totalRename = 0, totalPropDirty = 0, totalAttention = 0;
            foreach (var r in rows) {
                var it = r.Bom;
                if (it.Selected) totalSel++;
                if (r.RenameChanged) totalRename++;
                if (r.PropDirty) totalPropDirty++;
                int cnt;
                bool dup = it.Selected && cachedDnoCounts.TryGetValue(BomRules.SanitizeFileName(it.DrawingNo), out cnt) && cnt > 1;
                if (dup) dupDnoRows.Add(r);
                bool hasBomIssue = it.Selected && (!it.HasDraft || string.IsNullOrWhiteSpace(it.PartName) || string.IsNullOrWhiteSpace(it.DrawingNo) || string.IsNullOrWhiteSpace(it.Material) || it.Quantity <= 0 || dup);
                if (hasBomIssue || r.RenameProblem.Length > 0 || r.Prop.Problem.Length > 0) totalAttention++;
            }
            shell.Rail.Items[0].Count = totalAll.ToString();
            shell.Rail.Items[1].Count = totalSel.ToString();
            shell.Rail.Items[2].Count = totalRename.ToString();
            shell.Rail.Items[3].Count = totalPropDirty.ToString();
            shell.Rail.Items[4].Count = totalAttention.ToString();
            shell.Rail.Invalidate();
        }

        object[] BuildRowValues(WorkbenchRow r, int seq, int totalCols) {
            var it = r.Bom;
            var vals = new object[totalCols];
            vals[ColSel] = it.Selected;
            vals[ColSeq] = it.Selected ? seq.ToString() : "";
            vals[ColKind] = r.Rename.Kind;
            vals[ColOldName] = r.Rename.FileName;
            vals[ColNewName] = r.Rename.NewBase;
            vals[ColPartName] = r.GetPropValue("零件名称");
            vals[ColDno] = r.GetPropValue("图号");
            vals[ColMat] = it.Material;
            vals[ColQty] = r.GetPropValue("数量");
            vals[ColSurf] = r.GetPropValue("表面处理");
            vals[ColBatch] = r.GetPropValue("批次");
            vals[ColPrio] = r.GetPropValue("优先等级");
            vals[ColCat] = BomRules.CategoryLabel(it.Category);
            vals[ColChange] = it.ChangeReason;
            vals[ColRemark] = it.Remark;
            vals[ColCode] = it.Code;
            vals[ColLookupDno] = it.LookupDrawingNo;
            vals[ColLookupName] = it.LookupPartName;
            vals[ColDraft] = r.DraftDisplay;
            for (int k = 0; k < extraPropFields.Count; k++) {
                vals[FixedColCount + k] = r.GetPropValue(extraPropFields[k]);
            }
            vals[statusColIndex] = BuildRowStatus(r);
            return vals;
        }

        void UpdateGridRowValuesInPlace(DataGridViewRow gRow, WorkbenchRow r, int seq) {
            var vals = BuildRowValues(r, seq, grid.Columns.Count);
            for (int c = 0; c < vals.Length; c++) {
                if (!object.Equals(gRow.Cells[c].Value, vals[c])) gRow.Cells[c].Value = vals[c];
            }
        }

        // 单行就地刷新（编辑一个格子只需 <1ms，绝不清空重建整表、不丢选区、不触发 3D 预览重载）
        void RefreshSingleRow(int rowIndex, bool updateSeqAndDups) {
            if (rowIndex < 0 || rowIndex >= grid.Rows.Count) return;
            rebuilding = true;
            try {
                RefreshRailAndMetrics();
                if (updateSeqAndDups) {
                    int seq = 0;
                    for (int i = 0; i < grid.Rows.Count; i++) {
                        var rr = grid.Rows[i].Tag as WorkbenchRow;
                        if (rr == null) continue;
                        string seqText = rr.Bom.Selected ? (++seq).ToString() : "";
                        if (!object.Equals(grid.Rows[i].Cells[ColSeq].Value, seqText)) grid.Rows[i].Cells[ColSeq].Value = seqText;
                    }
                }
                var r = grid.Rows[rowIndex].Tag as WorkbenchRow;
                if (r != null) {
                    int seq = 0;
                    if (r.Bom.Selected) {
                        for (int i = 0; i <= rowIndex; i++) {
                            var rr = grid.Rows[i].Tag as WorkbenchRow;
                            if (rr != null && rr.Bom.Selected) seq++;
                        }
                    }
                    UpdateGridRowValuesInPlace(grid.Rows[rowIndex], r, seq);
                }
            } finally {
                rebuilding = false;
            }
            if (updateSeqAndDups) grid.Invalidate();
            else grid.InvalidateRow(rowIndex);
            UpdateActionButtons();
            UpdateSelectionSummary();
        }

        // 批量粘贴 / 向下填充 / 撤销 / 批量改名后就地刷新所有可见行（保留选区与滚动条，极速无闪烁）
        void RefreshAllVisibleRowsInPlace() {
            if (grid.Rows.Count != visibleRows.Count) { RebuildGrid(); return; }
            rebuilding = true;
            try {
                RefreshRailAndMetrics();
                int seq = 0;
                for (int i = 0; i < grid.Rows.Count; i++) {
                    var r = grid.Rows[i].Tag as WorkbenchRow;
                    if (r == null) continue;
                    int rowSeq = r.Bom.Selected ? ++seq : 0;
                    UpdateGridRowValuesInPlace(grid.Rows[i], r, rowSeq);
                }
            } finally {
                rebuilding = false;
            }
            grid.Invalidate();
            UpdateActionButtons();
            UpdateSelectionSummary();
        }

        // 仅在增删行、切换筛选条件或排序时全量重建可见行（SuspendLayout + AddRange 批量写入）
        void RebuildGrid() {
            rebuilding = true;
            grid.SuspendLayout();
            try {
                Dictionary<string, int> dnoCounts;
                var matched = CollectCandidateRows(null, out dnoCounts);
                if (sortDir != 0 && !string.IsNullOrEmpty(sortColKey)) {
                    WorkbenchRules.SortRows(matched, sortColKey, sortDir, BuildRowStatus);
                }
                visibleRows.Clear();
                visibleRows.AddRange(matched);
                RefreshRailAndMetrics();

                int firstRow = -1;
                try { firstRow = grid.FirstDisplayedScrollingRowIndex; } catch { }
                WorkbenchRow curRowTag = grid.CurrentCell != null && grid.CurrentCell.RowIndex >= 0 && grid.CurrentCell.RowIndex < grid.Rows.Count
                    ? grid.Rows[grid.CurrentCell.RowIndex].Tag as WorkbenchRow : null;
                int curColIdx = grid.CurrentCell != null ? grid.CurrentCell.ColumnIndex : -1;

                grid.Rows.Clear();
                int totalCols = grid.Columns.Count;
                if (visibleRows.Count > 0) {
                    var batch = new DataGridViewRow[visibleRows.Count];
                    int seq = 0;
                    for (int i = 0; i < visibleRows.Count; i++) {
                        var r = visibleRows[i];
                        int rowSeq = r.Bom.Selected ? ++seq : 0;
                        var gRow = new DataGridViewRow();
                        gRow.CreateCells(grid, BuildRowValues(r, rowSeq, totalCols));
                        gRow.Tag = r;
                        batch[i] = gRow;
                    }
                    grid.Rows.AddRange(batch);
                }

                try {
                    if (curRowTag != null && curColIdx >= 0 && curColIdx < grid.Columns.Count && grid.Columns[curColIdx].Visible) {
                        foreach (DataGridViewRow gr in grid.Rows) {
                            if (gr.Tag == curRowTag) { grid.CurrentCell = gr.Cells[curColIdx]; break; }
                        }
                    }
                    if (firstRow >= 0 && grid.Rows.Count > 0) grid.FirstDisplayedScrollingRowIndex = Math.Min(firstRow, grid.Rows.Count - 1);
                } catch { }
            } finally {
                grid.ResumeLayout();
                rebuilding = false;
            }
            UpdateFilterUi();
            UpdateActionButtons();
            UpdateSelectionSummary();
            try { grid.Invalidate(new Rectangle(0, 0, grid.Width, grid.ColumnHeadersHeight + 2)); } catch { }
        }

        string BuildRowStatus(WorkbenchRow r) {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(r.Bom.SubFolder)) parts.Add("[" + r.Bom.SubFolder + "]");
            if (r.RenameProblem.Length > 0) parts.Add("改名冲突：" + r.RenameProblem);
            if (r.Bom.ExportStatus.Length > 0) parts.Add(r.Bom.ExportStatus);
            else if (r.Prop.Problem.Length > 0) parts.Add(r.Prop.Problem);
            else if (r.Prop.Locked.Length > 0) parts.Add("（" + r.Prop.Locked + "）");
            else if (!r.Bom.Selected && r.Bom.SkipReason.Length > 0) parts.Add("（" + r.Bom.SkipReason + "）");
            else if (r.Bom.CategoryReason.Length > 0) parts.Add(r.Bom.CategoryReason);
            return string.Join(" ", parts.ToArray());
        }

        // 按需着色（只绘屏幕可见行，不给每个格子分配独立 DataGridViewCellStyle，彻底消除滚动与编辑卡顿）
        void OnGridCellFormatting(object sender, DataGridViewCellFormattingEventArgs e) {
            if (e.RowIndex < 0 || e.RowIndex >= grid.Rows.Count || e.ColumnIndex < 0) return;
            var r = grid.Rows[e.RowIndex].Tag as WorkbenchRow;
            if (r == null) return;
            var it = r.Bom;
            int col = e.ColumnIndex;

            e.CellStyle.BackColor = Ui.Bg;
            e.CellStyle.ForeColor = (col == ColOldName) ? Ui.Muted : ((it.Selected || r.RenameChanged || r.PropDirty) ? Ui.Text : Ui.Muted);

            if (col == ColNewName) {
                if (r.RenameProblem.Length > 0) { e.CellStyle.BackColor = Ui.BadSoft; e.CellStyle.ForeColor = Ui.Bad; }
                else if (r.RenameChanged) { e.CellStyle.BackColor = Ui.WarnSoft; e.CellStyle.ForeColor = Ui.Text; }
                return;
            }

            string pf = PropFieldOfColumn(col);
            if (pf.Length > 0) {
                if (r.IsPropEdited(pf)) { e.CellStyle.BackColor = Ui.WarnSoft; e.CellStyle.ForeColor = Ui.Text; }
                else if (r.IsPropMissing(pf)) { e.CellStyle.BackColor = Ui.Subtle; }
            }

            if (it.Selected) {
                if (col == ColDraft) {
                    if (!it.HasDraft) { e.CellStyle.BackColor = Ui.WarnSoft; e.CellStyle.ForeColor = Ui.Warn; }
                    else { e.CellStyle.ForeColor = Ui.Ok; }
                } else if (col == ColDno) {
                    if (string.IsNullOrWhiteSpace(it.DrawingNo) || dupDnoRows.Contains(r)) { e.CellStyle.BackColor = Ui.BadSoft; e.CellStyle.ForeColor = Ui.Bad; }
                } else if (col == ColMat) {
                    if (string.IsNullOrWhiteSpace(it.Material)) e.CellStyle.BackColor = Ui.WarnSoft;
                } else if (col == ColQty) {
                    if (it.Quantity <= 0) { e.CellStyle.BackColor = Ui.BadSoft; e.CellStyle.ForeColor = Ui.Bad; }
                }
            }

            if (col == statusColIndex) {
                if (r.RenameProblem.Length > 0 || it.ExportStatus.Contains("失败")) e.CellStyle.ForeColor = Ui.Bad;
                else if (it.ExportStatus.Contains("完成")) e.CellStyle.ForeColor = Ui.Ok;
            }
        }

        // 悬停时才按需计算提示文案，零预计算开销
        void OnGridCellToolTipTextNeeded(object sender, DataGridViewCellToolTipTextNeededEventArgs e) {
            if (e.RowIndex < 0 || e.RowIndex >= grid.Rows.Count || e.ColumnIndex < 0) return;
            var r = grid.Rows[e.RowIndex].Tag as WorkbenchRow;
            if (r == null) return;
            var it = r.Bom;
            int col = e.ColumnIndex;

            if (col == ColOldName) { e.ToolTipText = r.Rename.Path; return; }
            if (col == ColNewName) {
                if (r.RenameProblem.Length > 0) e.ToolTipText = r.RenameProblem;
                else if (r.RenameChanged) e.ToolTipText = "待改名：" + r.Rename.FileName + " → " + r.Rename.NewFileName + (chkFollowRename.Checked && (it.HasDraft || r.HasCfgFollower) ? "（同名 .dft/.cfg 跟随）" : "");
                return;
            }
            if (col == ColDno && it.Selected) {
                if (dupDnoRows.Contains(r)) { e.ToolTipText = "勾选行里存在重复图号，导出会互相覆盖"; return; }
                if (string.IsNullOrWhiteSpace(it.DrawingNo)) { e.ToolTipText = "图号为空"; return; }
            }
            string pf = PropFieldOfColumn(col);
            if (pf.Length > 0) {
                if (r.IsPropEdited(pf)) e.ToolTipText = "已修改「" + r.PropKeyFor(pf) + "」，待点「写入属性」落盘";
                else if (r.IsPropMissing(pf)) e.ToolTipText = "文件里还没有「" + r.PropKeyFor(pf) + "」属性";
            }
        }

        void UpdateActionButtons() {
            int renCount = rows.Count(r => r.RenameChanged);
            int propCount = rows.Count(r => r.PropDirty);

            string renText = renCount > 0 ? "执行改名 (" + renCount + ")" : "执行改名";
            bool renTextChanged = renameBtn.Text != renText;
            if (renTextChanged) renameBtn.Text = renText;
            renameBtn.Enabled = !busy && renCount > 0 && lastRenameProblems == 0;
            renameBtn.SetFore(renCount > 0 ? (lastRenameProblems > 0 ? Ui.Bad : Ui.Accent) : Ui.Text);
            if (renTextChanged) Ui.FitButton(renameBtn);

            string propText = propCount > 0 ? "写入属性 (" + propCount + ")" : "写入属性";
            bool propTextChanged = writePropsBtn.Text != propText;
            if (propTextChanged) writePropsBtn.Text = propText;
            writePropsBtn.Enabled = !busy && propCount > 0;
            writePropsBtn.SetFore(propCount > 0 ? Ui.Accent : Ui.Text);
            if (propTextChanged) Ui.FitButton(writePropsBtn);
            runBtn.Enabled = !busy || canStopBusy;

            if (renTextChanged || propTextChanged) shell.Footer.PerformLayout();
        }

        void OnGridCellValueChanged(object sender, DataGridViewCellEventArgs e) {
            if (busy || rebuilding || e.RowIndex < 0 || e.RowIndex >= grid.Rows.Count) return;
            var r = grid.Rows[e.RowIndex].Tag as WorkbenchRow;
            if (r == null) return;
            // 单格修改仅快照当前 1 行，彻底消除几百行整表深拷贝卡顿
            PushUndoRow(r);
            if (acCache.Remove(e.ColumnIndex) && lastAcCol == e.ColumnIndex) lastAcCol = -1;
            object raw = grid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value;
            if (e.ColumnIndex == ColSel) {
                r.Bom.Selected = !r.IsDraftOnly && raw is bool && (bool)raw;
                RefreshSingleRow(e.RowIndex, true);
                return;
            }
            string s = raw == null ? "" : Convert.ToString(raw).Trim();
            ApplyCellText(r, e.ColumnIndex, s);
            if (e.ColumnIndex == ColNewName) {
                ValidateRenames();
                RefreshAllVisibleRowsInPlace();
                return;
            }
            RefreshSingleRow(e.RowIndex, e.ColumnIndex == ColDno);
        }

        void ApplyCellText(WorkbenchRow r, int col, string text) {
            string s = (text ?? "").Trim();
            if (col == ColNewName) {
                if (r.Rename.Locked.Length == 0) r.Rename.SetNewBase(s);
                return;
            }
            string pf = PropFieldOfColumn(col);
            if (pf.Length > 0) {
                r.SetPropValue(pf, s);
                return;
            }
            switch (col) {
                case ColMat: r.SetMaterial(s); break;
                case ColCat:
                    int c = BomRules.ParseCategory(s);
                    if (c >= 1 && c <= 8) { r.Bom.Category = c; r.Bom.CategoryManual = true; r.Bom.CategoryReason = "手动指定"; }
                    break;
                case ColChange: r.Bom.ChangeReason = s; break;
                case ColRemark: r.Bom.Remark = s; break;
                case ColCode: r.Bom.Code = s; break;
                case ColLookupDno: r.Bom.LookupDrawingNo = s; break;
                case ColLookupName: r.Bom.LookupPartName = s; break;
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData) {
            if (!busy) {
                if (keyData == (Keys.Control | Keys.F)) { FocusFilterBox(); return true; }
                if (keyData == (Keys.Control | Keys.H)) { ShowFindReplaceDialog(); return true; }
            }
            if (!busy && !grid.IsCurrentCellInEditMode && !(ActiveControl is TextBoxBase)) {
                if (keyData == (Keys.Control | Keys.C)) { CopySelectedCells(); return true; }
                if (keyData == (Keys.Control | Keys.V)) { PasteFromClipboard(); return true; }
                if (keyData == (Keys.Control | Keys.X)) { CutSelectedCells(); return true; }
                if (keyData == (Keys.Control | Keys.D)) { FillDownSelectedCells(); return true; }
                if (keyData == (Keys.Control | Keys.Z)) { PerformUndo(); return true; }
                if (keyData == (Keys.Control | Keys.Y) || keyData == (Keys.Control | Keys.Shift | Keys.Z)) { PerformRedo(); return true; }
                if (keyData == Keys.Space && ActiveControl == grid) { TogglePickedRowsSelected(); return true; }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        void OnGridKeyDown(object sender, KeyEventArgs e) {
            if (busy || grid.IsCurrentCellInEditMode) return;
            if (e.Control && !e.Alt) {
                if (!e.Shift) {
                    if (e.KeyCode == Keys.C) {
                        CopySelectedCells();
                        e.Handled = true; e.SuppressKeyPress = true;
                    } else if (e.KeyCode == Keys.V) {
                        PasteFromClipboard();
                        e.Handled = true; e.SuppressKeyPress = true;
                    } else if (e.KeyCode == Keys.X) {
                        CutSelectedCells();
                        e.Handled = true; e.SuppressKeyPress = true;
                    } else if (e.KeyCode == Keys.D) {
                        FillDownSelectedCells();
                        e.Handled = true; e.SuppressKeyPress = true;
                    } else if (e.KeyCode == Keys.Z) {
                        PerformUndo();
                        e.Handled = true; e.SuppressKeyPress = true;
                    } else if (e.KeyCode == Keys.Y) {
                        PerformRedo();
                        e.Handled = true; e.SuppressKeyPress = true;
                    } else if (e.KeyCode == Keys.F) {
                        FocusFilterBox();
                        e.Handled = true; e.SuppressKeyPress = true;
                    } else if (e.KeyCode == Keys.H) {
                        ShowFindReplaceDialog();
                        e.Handled = true; e.SuppressKeyPress = true;
                    } else if (e.KeyCode == Keys.A) {
                        grid.SelectAll();
                        UpdateSelectionSummary();
                        e.Handled = true; e.SuppressKeyPress = true;
                    }
                } else if (e.KeyCode == Keys.Z) {
                    PerformRedo();
                    e.Handled = true; e.SuppressKeyPress = true;
                }
            } else if (e.KeyCode == Keys.Delete) {
                DeleteSelectedCells();
                e.Handled = true; e.SuppressKeyPress = true;
            } else if (e.KeyCode == Keys.Space) {
                TogglePickedRowsSelected();
                e.Handled = true; e.SuppressKeyPress = true;
            }
        }

        // ---- 撤销 / 重做栈（Ctrl+Z / Ctrl+Y；单行编辑按行增量快照，批量操作全表快照）----

        Dictionary<WorkbenchRow, WorkbenchRowSnapshot> CaptureAllSnapshots() {
            var snap = new Dictionary<WorkbenchRow, WorkbenchRowSnapshot>(rows.Count);
            foreach (var r in rows) snap[r] = r.CaptureSnapshot();
            return snap;
        }

        static Dictionary<WorkbenchRow, WorkbenchRowSnapshot> CaptureRowSnapshots(IEnumerable<WorkbenchRow> subset) {
            var snap = new Dictionary<WorkbenchRow, WorkbenchRowSnapshot>();
            foreach (var r in subset) if (r != null && !snap.ContainsKey(r)) snap[r] = r.CaptureSnapshot();
            return snap;
        }

        void PushUndo() {
            undoStack.Add(CaptureAllSnapshots());
            if (undoStack.Count > MaxUndoSteps) undoStack.RemoveAt(0);
            redoStack.Clear();
        }

        void PushUndoRow(WorkbenchRow r) {
            if (r == null) return;
            var snap = new Dictionary<WorkbenchRow, WorkbenchRowSnapshot>(1);
            snap[r] = r.CaptureSnapshot();
            undoStack.Add(snap);
            if (undoStack.Count > MaxUndoSteps) undoStack.RemoveAt(0);
            redoStack.Clear();
        }

        void PopUnusedUndo() {
            if (undoStack.Count > 0) undoStack.RemoveAt(undoStack.Count - 1);
        }

        void PerformUndo() {
            if (busy) return;
            if (undoStack.Count == 0) {
                SetStatus("没有可撤销的上一步表格修改。", Ui.Muted);
                return;
            }
            grid.EndEdit();
            var snap = undoStack[undoStack.Count - 1];
            undoStack.RemoveAt(undoStack.Count - 1);
            redoStack.Add(CaptureRowSnapshots(snap.Keys));
            if (redoStack.Count > MaxUndoSteps) redoStack.RemoveAt(0);
            foreach (var kv in snap) kv.Key.RestoreSnapshot(kv.Value);
            acCache.Clear();
            lastAcCol = -1;
            ValidateRenames();
            RefreshAllVisibleRowsInPlace();
            SetStatus("已撤销上一步修改（可撤销 " + undoStack.Count + " 步 · Ctrl+Y 可重做）。", Ui.Muted);
        }

        void PerformRedo() {
            if (busy) return;
            if (redoStack.Count == 0) {
                SetStatus("没有可重做的修改。", Ui.Muted);
                return;
            }
            grid.EndEdit();
            var snap = redoStack[redoStack.Count - 1];
            redoStack.RemoveAt(redoStack.Count - 1);
            undoStack.Add(CaptureRowSnapshots(snap.Keys));
            if (undoStack.Count > MaxUndoSteps) undoStack.RemoveAt(0);
            foreach (var kv in snap) kv.Key.RestoreSnapshot(kv.Value);
            acCache.Clear();
            lastAcCol = -1;
            ValidateRenames();
            RefreshAllVisibleRowsInPlace();
            SetStatus("已重做修改（剩余可重做 " + redoStack.Count + " 步）。", Ui.Muted);
        }

        // ---- 表格编辑与 Excel 快捷操作（复制 / 剪切 / 粘贴 / 向下填充 Ctrl+D / 右键菜单 / 选区统计） ----

        static bool SetClipboardTextSafe(string text) {
            return ClipboardUtil.SetText(text);
        }

        static string GetClipboardTextSafe(out bool busy) {
            return ClipboardUtil.GetText(out busy);
        }

        void CopySelectedCells() {
            if (busy) return;
            if (grid.IsCurrentCellInEditMode) grid.EndEdit();
            var cells = new List<DataGridViewCell>();
            foreach (DataGridViewCell c in grid.SelectedCells) {
                if (c.RowIndex >= 0 && c.ColumnIndex >= 0 && c.OwningColumn != null && c.OwningColumn.Visible) cells.Add(c);
            }
            if (cells.Count == 0 && grid.CurrentCell != null && grid.CurrentCell.OwningColumn != null && grid.CurrentCell.OwningColumn.Visible) {
                cells.Add(grid.CurrentCell);
            }
            if (cells.Count == 0) return;

            var visCols = grid.Columns.Cast<DataGridViewColumn>().Where(c => c.Visible).OrderBy(c => c.DisplayIndex).ToList();
            var colPos = new Dictionary<int, int>(visCols.Count);
            for (int i = 0; i < visCols.Count; i++) colPos[visCols[i].Index] = i;

            var rowSet = new SortedDictionary<int, Dictionary<int, DataGridViewCell>>();
            var usedColPos = new SortedDictionary<int, int>();
            foreach (var c in cells) {
                int p;
                if (!colPos.TryGetValue(c.ColumnIndex, out p)) continue;
                usedColPos[p] = c.ColumnIndex;
                Dictionary<int, DataGridViewCell> rowMap;
                if (!rowSet.TryGetValue(c.RowIndex, out rowMap)) { rowMap = new Dictionary<int, DataGridViewCell>(); rowSet[c.RowIndex] = rowMap; }
                rowMap[p] = c;
            }
            if (rowSet.Count == 0 || usedColPos.Count == 0) return;

            var orderedPos = usedColPos.Keys.ToList();
            var matrix = new List<IList<string>>(rowSet.Count);
            foreach (var rkv in rowSet) {
                var r = grid.Rows[rkv.Key].Tag as WorkbenchRow;
                var line = new List<string>(orderedPos.Count);
                foreach (int p in orderedPos) {
                    DataGridViewCell cell;
                    if (!rkv.Value.TryGetValue(p, out cell)) { line.Add(""); continue; }
                    int ci = cell.ColumnIndex;
                    if (ci == ColSeq) line.Add(Convert.ToString(cell.Value) ?? "");
                    else if (r != null) line.Add(WorkbenchRules.CellTextByKey(r, ColKey(ci), BuildRowStatus(r)));
                    else line.Add(Convert.ToString(cell.FormattedValue) ?? "");
                }
                matrix.Add(line);
            }

            string tsv = WorkbenchRules.FormatClipboardBlock(matrix);
            if (SetClipboardTextSafe(tsv)) {
                string msg = (rowSet.Count == 1 && orderedPos.Count == 1)
                    ? "已复制 1 个单元格。"
                    : ("已复制 " + rowSet.Count + " 行 × " + orderedPos.Count + " 列到剪贴板。");
                SetStatus(msg, Ui.Muted);
            } else {
                SetStatus("剪贴板正被其他程序占用，请重试一次 Ctrl+C。", Ui.Warn);
            }
        }

        void CutSelectedCells() {
            if (busy) return;
            CopySelectedCells();
            DeleteSelectedCells();
        }

        void PasteFromClipboard() {
            if (busy) return;
            if (grid.IsCurrentCellInEditMode) grid.EndEdit();
            bool clipBusy;
            string text = GetClipboardTextSafe(out clipBusy);
            var clip = PropCardFields.ParseClipboard(text);
            if (clip.Count == 0) {
                SetStatus(clipBusy ? "剪贴板正被其他程序占用，请重试一次 Ctrl+V。" : "剪贴板里没有可粘贴的文字。", clipBusy ? Ui.Warn : Ui.Muted);
                return;
            }
            if (grid.SelectedCells.Count == 0 && grid.CurrentCell == null) {
                SetStatus("请先在表格里点选要粘贴的单元格。", Ui.Muted);
                return;
            }

            // 单值粘贴（例如复制了 1 个格，不管当前选中 1 格还是框选了多格，全部填入；必须跳过当前列视图下隐藏的列）
            if (clip.Count == 1 && clip[0].Length == 1) {
                string val = clip[0][0];
                var targets = new List<DataGridViewCell>();
                foreach (DataGridViewCell c in grid.SelectedCells) targets.Add(c);
                if (targets.Count == 0 && grid.CurrentCell != null) targets.Add(grid.CurrentCell);

                PushUndo();
                bool touchedRename = false;
                int written = 0;
                foreach (var cell in targets) {
                    if (cell.OwningColumn == null || !cell.OwningColumn.Visible || cell.ReadOnly || cell.ColumnIndex == ColSel) continue;
                    var r = grid.Rows[cell.RowIndex].Tag as WorkbenchRow;
                    if (r == null) continue;
                    if (cell.ColumnIndex == ColNewName && r.Rename.Locked.Length > 0) continue;
                    if (IsPropertyColumn(cell.ColumnIndex) && !r.CanWriteProps && cell.ColumnIndex >= FixedColCount) continue;
                    if (cell.ColumnIndex == ColNewName) touchedRename = true;
                    ApplyCellText(r, cell.ColumnIndex, val);
                    written++;
                }
                if (written == 0) {
                    PopUnusedUndo();
                    SetStatus("选中的列是只读的，请选中「新文件名」或右侧可编辑列再粘贴。", Ui.Warn);
                    return;
                }
                if (touchedRename) ValidateRenames();
                RefreshAllVisibleRowsInPlace();
                SetStatus("已粘贴到 " + written + " 个单元格（Ctrl+Z 可撤销）。", Ui.Muted);
                return;
            }

            // 多格块粘贴：始终以选区左上角（minRow, minVisCol）为起点，避免从上往下拖选时落到底部
            var visCols = grid.Columns.Cast<DataGridViewColumn>().Where(c => c.Visible).OrderBy(c => c.DisplayIndex).ToList();
            var colPos = new Dictionary<int, int>(visCols.Count);
            for (int i = 0; i < visCols.Count; i++) colPos[visCols[i].Index] = i;

            int r0 = int.MaxValue, startCol = int.MaxValue;
            foreach (DataGridViewCell c in grid.SelectedCells) {
                int p;
                if (c.RowIndex >= 0 && colPos.TryGetValue(c.ColumnIndex, out p)) {
                    if (c.RowIndex < r0) r0 = c.RowIndex;
                    if (p < startCol) startCol = p;
                }
            }
            if (r0 == int.MaxValue && grid.CurrentCell != null) {
                int p;
                if (colPos.TryGetValue(grid.CurrentCell.ColumnIndex, out p)) {
                    r0 = grid.CurrentCell.RowIndex;
                    startCol = p;
                }
            }
            if (r0 == int.MaxValue || startCol == int.MaxValue) return;

            PushUndo();
            bool blockTouchedRename = false;
            int blockWritten = 0;
            for (int dr = 0; dr < clip.Count && r0 + dr < grid.Rows.Count; dr++) {
                var r = grid.Rows[r0 + dr].Tag as WorkbenchRow;
                if (r == null) continue;
                var rowVals = clip[dr];
                for (int dc = 0; dc < rowVals.Length && startCol + dc < visCols.Count; dc++) {
                    var col = visCols[startCol + dc];
                    if (col.ReadOnly || col.Index == ColSel) continue;
                    if (col.Index == ColNewName && r.Rename.Locked.Length > 0) continue;
                    if (IsPropertyColumn(col.Index) && !r.CanWriteProps && col.Index >= FixedColCount) continue;
                    if (col.Index == ColNewName) blockTouchedRename = true;
                    ApplyCellText(r, col.Index, rowVals[dc]);
                    blockWritten++;
                }
            }
            if (blockWritten == 0) {
                PopUnusedUndo();
                SetStatus("目标区域均为只读列，请将光标移到可编辑列（如「新文件名」「零件名称」「图号」）再粘贴。", Ui.Warn);
                return;
            }
            if (blockTouchedRename) ValidateRenames();
            RefreshAllVisibleRowsInPlace();
            SetStatus("已粘贴 " + blockWritten + " 个单元格（Ctrl+Z 可撤销）。", Ui.Muted);
        }

        void FillDownSelectedCells() {
            if (busy || grid.SelectedCells.Count == 0) return;
            var byCol = new Dictionary<int, List<int>>();
            foreach (DataGridViewCell cell in grid.SelectedCells) {
                if (cell.RowIndex < 0 || cell.ColumnIndex < 0) continue;
                var col = grid.Columns[cell.ColumnIndex];
                // 跨隐藏列拖选或 Ctrl+A 时跳过不可见列，防止静默篡改当前视图隐藏的新文件名 / 属性列
                if (!col.Visible || col.ReadOnly || cell.ColumnIndex == ColSeq) continue;
                List<int> list;
                if (!byCol.TryGetValue(cell.ColumnIndex, out list)) { list = new List<int>(); byCol[cell.ColumnIndex] = list; }
                if (!list.Contains(cell.RowIndex)) list.Add(cell.RowIndex);
            }
            if (byCol.Count == 0) return;

            PushUndo();
            bool touchedRename = false;
            int filled = 0;
            foreach (var kv in byCol) {
                int colIdx = kv.Key;
                var rowIndices = kv.Value;
                rowIndices.Sort();
                int srcRowIdx;
                int dstStart;
                if (rowIndices.Count == 1) {
                    // Excel 习惯：只选中单个单元格按 Ctrl+D 时，把正上方那一格填下来
                    if (rowIndices[0] <= 0) continue;
                    srcRowIdx = rowIndices[0] - 1;
                    dstStart = 0;
                } else {
                    srcRowIdx = rowIndices[0];
                    dstStart = 1;
                }
                var srcRow = grid.Rows[srcRowIdx].Tag as WorkbenchRow;
                if (srcRow == null) continue;
                string srcText = WorkbenchRules.CellTextByKey(srcRow, ColKey(colIdx), BuildRowStatus(srcRow));
                bool srcSel = srcRow.Bom.Selected;

                for (int i = dstStart; i < rowIndices.Count; i++) {
                    var dstRow = grid.Rows[rowIndices[i]].Tag as WorkbenchRow;
                    if (dstRow == null) continue;
                    if (colIdx == ColSel) {
                        dstRow.Bom.Selected = srcSel && !dstRow.IsDraftOnly;
                        filled++;
                    } else {
                        if (colIdx == ColNewName && dstRow.Rename.Locked.Length > 0) continue;
                        if (IsPropertyColumn(colIdx) && !dstRow.CanWriteProps && colIdx >= FixedColCount) continue;
                        if (colIdx == ColNewName) touchedRename = true;
                        ApplyCellText(dstRow, colIdx, srcText);
                        filled++;
                    }
                }
            }
            if (filled == 0) { PopUnusedUndo(); return; }
            if (touchedRename) ValidateRenames();
            RefreshAllVisibleRowsInPlace();
            SetStatus("已向下填充 " + filled + " 个单元格（Ctrl+Z 可撤销）。", Ui.Muted);
        }

        void DeleteSelectedCells() {
            if (busy || grid.SelectedCells.Count == 0) return;
            PushUndo();
            bool touchedRename = false;
            int cleared = 0;
            foreach (DataGridViewCell cell in grid.SelectedCells) {
                if (cell.OwningColumn == null || !cell.OwningColumn.Visible || cell.ReadOnly || cell.ColumnIndex == ColSel) continue;
                var r = grid.Rows[cell.RowIndex].Tag as WorkbenchRow;
                if (r == null) continue;
                if (cell.ColumnIndex == ColNewName) {
                    if (r.Rename.Locked.Length > 0) continue;
                    r.Rename.Revert(); touchedRename = true; cleared++;
                } else {
                    if (IsPropertyColumn(cell.ColumnIndex) && !r.CanWriteProps && cell.ColumnIndex >= FixedColCount) continue;
                    ApplyCellText(r, cell.ColumnIndex, ""); cleared++;
                }
            }
            if (cleared == 0) { PopUnusedUndo(); return; }
            if (touchedRename) ValidateRenames();
            RefreshAllVisibleRowsInPlace();
        }

        void FillSelectedCells(string text) {
            if (busy) return;
            if (grid.SelectedCells.Count == 0) { SetStatus("请先在表格里选中要填入的单元格。", Ui.Muted); return; }
            PushUndo();
            bool touchedRename = false;
            int n = 0;
            foreach (DataGridViewCell cell in grid.SelectedCells) {
                if (cell.OwningColumn == null || !cell.OwningColumn.Visible || cell.ReadOnly || cell.ColumnIndex == ColSel) continue;
                var r = grid.Rows[cell.RowIndex].Tag as WorkbenchRow;
                if (r == null) continue;
                if (cell.ColumnIndex == ColNewName && r.Rename.Locked.Length > 0) continue;
                if (IsPropertyColumn(cell.ColumnIndex) && !r.CanWriteProps && cell.ColumnIndex >= FixedColCount) continue;
                if (cell.ColumnIndex == ColNewName) touchedRename = true;
                ApplyCellText(r, cell.ColumnIndex, text);
                n++;
            }
            if (n == 0) {
                PopUnusedUndo();
                SetStatus("选中的单元格属于只读列，无法填入。", Ui.Warn);
                return;
            }
            if (touchedRename) ValidateRenames();
            RefreshAllVisibleRowsInPlace();
            SetStatus("已填入 " + n + " 个单元格（Ctrl+Z 可撤销）。", Ui.Muted);
        }

        void SetVisibleSelected(bool sel) {
            if (busy) return;
            PushUndo();
            foreach (var r in visibleRows) r.Bom.Selected = sel && !r.IsDraftOnly;
            RefreshAllVisibleRowsInPlace();
        }

        void SetPickedRowsSelected(bool sel) {
            if (busy) return;
            var picked = CollectPickedRows();
            if (picked.Count == 0) return;
            PushUndo();
            foreach (var r in picked) r.Bom.Selected = sel && !r.IsDraftOnly;
            RefreshAllVisibleRowsInPlace();
        }

        List<WorkbenchRow> CollectPickedRows() {
            var picked = new HashSet<WorkbenchRow>();
            foreach (DataGridViewCell c in grid.SelectedCells) {
                if (c.RowIndex >= 0 && c.RowIndex < grid.Rows.Count) {
                    var r = grid.Rows[c.RowIndex].Tag as WorkbenchRow;
                    if (r != null) picked.Add(r);
                }
            }
            if (picked.Count == 0 && grid.CurrentCell != null && grid.CurrentCell.RowIndex >= 0 && grid.CurrentCell.RowIndex < grid.Rows.Count) {
                var r = grid.Rows[grid.CurrentCell.RowIndex].Tag as WorkbenchRow;
                if (r != null) picked.Add(r);
            }
            return visibleRows.Where(picked.Contains).ToList();
        }

        void TogglePickedRowsSelected() {
            if (busy) return;
            var picked = CollectPickedRows().Where(r => !r.IsDraftOnly).ToList();
            if (picked.Count == 0) return;
            bool nextSel = picked.Any(r => !r.Bom.Selected);
            PushUndo();
            foreach (var r in picked) r.Bom.Selected = nextSel;
            RefreshAllVisibleRowsInPlace();
            SetStatus((nextSel ? "已勾选 " : "已取消勾选 ") + picked.Count + " 行（Space 切勾选 · Ctrl+Z 可撤销）。", Ui.Muted);
        }

        void InvertVisibleSelected() {
            if (busy) return;
            PushUndo();
            foreach (var r in visibleRows) {
                if (!r.IsDraftOnly) r.Bom.Selected = !r.Bom.Selected;
            }
            RefreshAllVisibleRowsInPlace();
            SetStatus("已反选当前可见行的勾选状态。", Ui.Muted);
        }

        void SelectMissingDraftOnly() {
            if (busy) return;
            PushUndo();
            int n = 0;
            foreach (var r in visibleRows) {
                bool pick = !r.IsDraftOnly && !r.Bom.HasDraft;
                r.Bom.Selected = pick;
                if (pick) n++;
            }
            RefreshAllVisibleRowsInPlace();
            SetStatus("已只勾选缺 .dft 的零件（共 " + n + " 个）。", Ui.Muted);
        }

        void ShowFindReplaceDialog() {
            if (busy) return;
            grid.EndEdit();
            var editableCols = new List<KeyValuePair<int, string>>();
            foreach (DataGridViewColumn col in grid.Columns) {
                if (!col.Visible || col.ReadOnly || col.Index == ColSel || col.Index == ColCat) continue;
                editableCols.Add(new KeyValuePair<int, string>(col.Index, col.HeaderText));
            }
            if (editableCols.Count == 0) return;
            int defaultCol = grid.CurrentCell != null ? grid.CurrentCell.ColumnIndex : ColPartName;
            var pickedRows = CollectPickedRows();
            using (var dlg = new WorkbenchFindReplaceDialog(editableCols, defaultCol, pickedRows.Count, visibleRows.Count, rows.Count)) {
                if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Result == null) return;
                ApplyFindReplace(dlg.Result);
            }
        }

        int ApplyFindReplace(WorkbenchFindReplaceDialog.Request req) {
            if (req == null || string.IsNullOrEmpty(req.Find)) return 0;
            List<WorkbenchRow> scopeRows;
            if (req.Scope == 0) scopeRows = CollectPickedRows();
            else if (req.Scope == 1) scopeRows = new List<WorkbenchRow>(visibleRows);
            else scopeRows = new List<WorkbenchRow>(rows);
            if (scopeRows.Count == 0) {
                SetStatus("目标范围内没有可替换的行。", Ui.Muted);
                return 0;
            }

            var targetCols = new List<int>();
            if (req.ColumnIndex >= 0 && req.ColumnIndex < grid.Columns.Count) {
                targetCols.Add(req.ColumnIndex);
            } else {
                foreach (DataGridViewColumn col in grid.Columns) {
                    if (!col.Visible || col.ReadOnly || col.Index == ColSel || col.Index == ColCat) continue;
                    targetCols.Add(col.Index);
                }
            }

            PushUndo();
            bool touchedRename = false;
            int hitCells = 0, hitRows = 0;
            foreach (var r in scopeRows) {
                bool rowHit = false;
                foreach (int ci in targetCols) {
                    if (ci == ColNewName && r.Rename.Locked.Length > 0) continue;
                    if (IsPropertyColumn(ci) && !r.CanWriteProps && ci >= FixedColCount) continue;
                    string key = ColKey(ci);
                    string cur = WorkbenchRules.CellTextByKey(r, key, BuildRowStatus(r));
                    bool changed;
                    string next = WindowBoundsRules.ReplaceInText(cur, req.Find, req.Replace, req.MatchCase, out changed);
                    if (!changed) continue;
                    if (ci == ColNewName) touchedRename = true;
                    ApplyCellText(r, ci, next);
                    hitCells++;
                    rowHit = true;
                }
                if (rowHit) hitRows++;
            }

            if (hitCells == 0) {
                PopUnusedUndo();
                SetStatus("在「" + req.ColumnLabel + "」里没有找到「" + req.Find + "」。", Ui.Muted);
                return 0;
            }
            if (touchedRename) ValidateRenames();
            RefreshAllVisibleRowsInPlace();
            SetStatus("已在「" + req.ColumnLabel + "」替换 " + hitCells + " 处（涉及 " + hitRows + " 行 · Ctrl+Z 可撤销）。", Ui.Ok);
            return hitCells;
        }

        void OpenFileInCad(string path) {
            if (busy || string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            if (app == null) { SetStatus("未连接到天工 CAD，无法直接打开。", Ui.Warn); return; }
            preview.Hold(true);
            try {
                app.Documents.Open(path);
                propCad.InvalidateOpenCache();
                SetStatus("已在天工中打开：" + Path.GetFileName(path), Ui.Ok);
            } catch (Exception ex) {
                Log.Write("WorkbenchOpenInCad", ex);
                SetStatus("在天工中打开失败：" + ex.Message, Ui.Bad);
                MessageBox.Show(this, "打不开「" + Path.GetFileName(path) + "」：" + ex.Message, Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            } finally {
                preview.Hold(false);
            }
        }

        void RefreshAllDraftLinks() {
            if (busy || rows.Count == 0) return;
            int foundNew = 0;
            foreach (var r in rows) {
                bool had = r.Bom.HasDraft;
                r.RefreshCompanions(File.Exists);
                if (!had && r.Bom.HasDraft) foundNew++;
            }
            RefreshAllVisibleRowsInPlace();
            SetStatus(foundNew > 0 ? ("已刷新配对：新发现 " + foundNew + " 张工程图（.dft）。") : "已刷新工程图配对状态。", foundNew > 0 ? Ui.Ok : Ui.Muted);
        }

        void QuickDrawMissingRows(IList<WorkbenchRow> targets) {
            if (busy || targets == null || targets.Count == 0) return;
            if (app == null) { SetStatus("未连接到天工 CAD，无法一键出图。", Ui.Warn); return; }
            var todo = targets.Where(r => !r.IsDraftOnly && !r.Bom.HasDraft && File.Exists(r.Rename.Path)).ToList();
            if (todo.Count == 0) { SetStatus("选中的行都已有工程图或不支持自动出图。", Ui.Muted); return; }
            if (todo.Count > 1) {
                var ask = MessageBox.Show(this,
                    "即将为选中的 " + todo.Count + " 个缺图模型按公司模板一键出图（保存为模型旁边的同名 .dft）。\r\n继续吗？",
                    Title + " · 一键补出图", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (ask != DialogResult.Yes) return;
            }

            BeginBusy("补出工程图", true);
            stopRequested = false;
            runBtn.Text = "停止";
            progress.Visible = true;
            progress.Maximum = todo.Count;
            progress.Value = 0;
            int done = 0, ok = 0;
            var errs = new List<string>();
            try {
                foreach (var r in todo) {
                    if (stopRequested) { SetStatus("已停止补出图。", Ui.Warn); break; }
                    SetStatus("正在出图 " + (done + 1) + "/" + todo.Count + "：" + r.Rename.FileName, Ui.Muted);
                    try { Application.DoEvents(); } catch { }
                    var opt = new DrawingOptions {
                        Overwrite = false,
                        Close = todo.Count > 1,
                        DimensionStyle = DrawingStyleRules.NormalizePreferredStyle(Choices.Get("drawing.dimstyle", DrawingStyleRules.DefaultDimensionStyle))
                    };
                    var o = r.Bom.IsAssembly ? DrawingMaker.Assembly(app, r.Rename.Path, opt) : DrawingMaker.Part(app, r.Rename.Path, opt);
                    if (o.Ok) {
                        ok++;
                        r.RefreshCompanions(File.Exists);
                        if (!r.Bom.HasDraft && !string.IsNullOrEmpty(o.Output) && File.Exists(o.Output)) {
                            r.Bom.DraftPath = o.Output;
                        }
                    } else {
                        errs.Add(r.Rename.FileName + "：" + string.Join("；", o.Notes.Take(2).ToArray()));
                    }
                    done++;
                    progress.Value = done;
                    RefreshAllVisibleRowsInPlace();
                    try { Application.DoEvents(); } catch { }
                }
                string msg = "补出图完成：成功 " + ok + " / " + done + " 张" + (errs.Count > 0 ? "（" + errs.Count + " 张失败）" : "");
                SetStatus(msg, errs.Count > 0 ? Ui.Warn : Ui.Ok);
                if (errs.Count > 0) {
                    MessageBox.Show(this, msg + "\r\n\r\n" + string.Join("\r\n", errs.Take(8).ToArray()), Title + " · 一键补出图", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            } catch (Exception ex) {
                Log.Write("WorkbenchQuickDraw", ex);
                SetStatus("补出图出错：" + ex.Message, Ui.Bad);
            } finally {
                progress.Visible = false;
                runBtn.Text = "开始出清单";
                EndBusy();
            }
        }

        void OnGridCellMouseDown(object sender, DataGridViewCellMouseEventArgs e) {
            if (busy || e.Button != MouseButtons.Right || e.RowIndex < 0 || e.ColumnIndex < 0) return;
            grid.EndEdit();
            if (!grid.Rows[e.RowIndex].Cells[e.ColumnIndex].Selected) {
                grid.ClearSelection();
                try { grid.CurrentCell = grid.Rows[e.RowIndex].Cells[e.ColumnIndex]; } catch { }
            }
            var r = grid.Rows[e.RowIndex].Tag as WorkbenchRow;
            if (r == null) return;

            string key = ColKey(e.ColumnIndex);
            var col = grid.Columns[e.ColumnIndex];
            string tok = WorkbenchRules.FilterToken(WorkbenchRules.CellTextByKey(r, key, BuildRowStatus(r)));
            string shortTok = tok.Length > 24 ? tok.Substring(0, 24) + "…" : tok;
            var pickedRows = CollectPickedRows();
            var missingPicked = pickedRows.Where(x => !x.IsDraftOnly && !x.Bom.HasDraft && File.Exists(x.Rename.Path)).ToList();

            Ui.DisposeItems(gridMenu.Items);
            gridMenu.Items.Add("复制 (Ctrl+C)", null, (s, ev) => CopySelectedCells());
            gridMenu.Items.Add("剪切 (Ctrl+X)", null, (s, ev) => CutSelectedCells());
            gridMenu.Items.Add("粘贴 (Ctrl+V)", null, (s, ev) => PasteFromClipboard());
            gridMenu.Items.Add("向下填充 (Ctrl+D)", null, (s, ev) => FillDownSelectedCells());
            gridMenu.Items.Add("批量查找替换… (Ctrl+H)", null, (s, ev) => ShowFindReplaceDialog());
            gridMenu.Items.Add("清空 / 恢复原名 (Delete)", null, (s, ev) => DeleteSelectedCells());
            var undoItem = gridMenu.Items.Add("撤销上一步 (Ctrl+Z)", null, (s, ev) => PerformUndo());
            undoItem.Enabled = undoStack.Count > 0;
            var redoItem = gridMenu.Items.Add("重做 (Ctrl+Y)", null, (s, ev) => PerformRedo());
            redoItem.Enabled = redoStack.Count > 0;

            gridMenu.Items.Add(new ToolStripSeparator());
            if (IsFilterableColumn(e.ColumnIndex)) {
                gridMenu.Items.Add("按此值筛选（=「" + shortTok + "」）", null, (s, ev) => {
                    columnFilters[key] = new HashSet<string>(new[] { tok }, StringComparer.Ordinal);
                    RebuildGrid();
                    SetStatus("已筛选「" + col.HeaderText + " = " + shortTok + "」：显示 " + visibleRows.Count + " / " + rows.Count + " 行。", Ui.Muted);
                });
                gridMenu.Items.Add("排除此值（≠「" + shortTok + "」）", null, (s, ev) => {
                    Dictionary<string, int> dummy;
                    var cands = CollectCandidateRows(key, out dummy);
                    var vals = WorkbenchRules.DistinctColumnValues(cands, key, BuildRowStatus);
                    var keep = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var kv in vals) if (!string.Equals(kv.Key, tok, StringComparison.Ordinal)) keep.Add(kv.Key);
                    columnFilters[key] = keep;
                    RebuildGrid();
                    SetStatus("已排除「" + col.HeaderText + " = " + shortTok + "」：显示 " + visibleRows.Count + " / " + rows.Count + " 行。", Ui.Muted);
                });
                gridMenu.Items.Add("筛选「" + col.HeaderText + "」列…", null, (s, ev) => OpenColumnFilterPopup(e.ColumnIndex));
            }
            var clearItem = gridMenu.Items.Add("清除全部列筛选与排序", null, (s, ev) => ClearAllFiltersAndSort());
            clearItem.Enabled = columnFilters.Count > 0 || sortDir != 0;

            gridMenu.Items.Add(new ToolStripSeparator());
            gridMenu.Items.Add("勾选选中行（Space 切换）", null, (s, ev) => SetPickedRowsSelected(true));
            gridMenu.Items.Add("取消勾选选中行", null, (s, ev) => SetPickedRowsSelected(false));
            gridMenu.Items.Add("反选当前可见行", null, (s, ev) => InvertVisibleSelected());
            gridMenu.Items.Add("只勾选缺 .dft 的零件", null, (s, ev) => SelectMissingDraftOnly());

            gridMenu.Items.Add(new ToolStripSeparator());
            var openModel = gridMenu.Items.Add("在天工中打开三维模型", null, (s, ev) => OpenFileInCad(r.Rename.Path));
            openModel.Enabled = !r.IsDraftOnly && File.Exists(r.Rename.Path) && app != null;
            var openDraft = gridMenu.Items.Add("在天工中打开工程图 (.dft)", null, (s, ev) => OpenFileInCad(r.Bom.DraftPath));
            openDraft.Enabled = r.Bom.HasDraft && File.Exists(r.Bom.DraftPath) && app != null;
            if (missingPicked.Count > 0 && app != null) {
                string drawLabel = missingPicked.Count == 1
                    ? "为此缺图零件一键出图…"
                    : "为选中的 " + missingPicked.Count + " 个缺图零件一键出图…";
                gridMenu.Items.Add(drawLabel, null, (s, ev) => QuickDrawMissingRows(missingPicked));
            }
            gridMenu.Items.Add("刷新全部工程图配对状态", null, (s, ev) => RefreshAllDraftLinks());
            var openLoc = gridMenu.Items.Add("在资源管理器中定位源文件", null, (s, ev) => {
                if (File.Exists(r.Rename.Path)) {
                    try { Process.Start("explorer.exe", "/select,\"" + r.Rename.Path + "\""); } catch { }
                }
            });
            openLoc.Enabled = File.Exists(r.Rename.Path);

            gridMenu.Items.Add(new ToolStripSeparator());
            gridMenu.Items.Add("恢复默认列宽", null, (s, ev) => ResetColumnWidths());

            gridMenu.Show(Cursor.Position);
        }

        void UpdateSelectionSummary() {
            if (rebuilding || legendLabel == null) return;
            string filterPrefix = (visibleRows.Count != rows.Count || columnFilters.Count > 0)
                ? "当前显示 " + visibleRows.Count + " / " + rows.Count + " 行 · "
                : "";
            int cellCount = grid.SelectedCells.Count;
            if (cellCount >= 2) {
                var rowSet = new HashSet<int>();
                int numCount = 0;
                double numSum = 0;
                foreach (DataGridViewCell c in grid.SelectedCells) {
                    rowSet.Add(c.RowIndex);
                    if (c.ColumnIndex == ColQty || c.ColumnIndex >= FixedColCount) {
                        double v;
                        string raw = c.Value == null ? "" : Convert.ToString(c.Value).Trim();
                        if (raw.Length > 0 && double.TryParse(raw, out v)) { numCount++; numSum += v; }
                    }
                }
                string stats = "已选 " + cellCount + " 格（" + rowSet.Count + " 行）";
                if (numCount >= 2) stats += " · 数值合计 " + numSum.ToString("0.##");
                legendLabel.Text = filterPrefix + stats + " · Ctrl+D 向下填充 · Ctrl+C 复制 · Ctrl+Z 撤销 · 右键可按值筛选";
                legendLabel.ForeColor = Ui.Text2;
            } else {
                legendLabel.Text = filterPrefix + DefaultLegendText;
                legendLabel.ForeColor = columnFilters.Count > 0 ? Ui.Text2 : Ui.Muted;
            }
        }

        void RevertAllEdits() {
            if (busy) return;
            int ren = rows.Count(r => r.RenameChanged);
            int prop = rows.Count(r => r.PropDirty);
            if (ren == 0 && prop == 0) { SetStatus("没有待撤销的改名或属性修改。", Ui.Muted); return; }
            if (MessageBox.Show(this, "撤销全部未落盘的修改（" + ren + " 个待改名、" + prop + " 个待写入属性）？", Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            PushUndo();
            foreach (var r in rows) r.RevertAllEdits();
            ValidateRenames();
            RefreshAllVisibleRowsInPlace();
            SetStatus("已撤销所有未落盘的改名与属性修改。", Ui.Muted);
        }

        void RemoveSelectedRows() {
            if (busy) return;
            var toRemove = new HashSet<WorkbenchRow>();
            foreach (DataGridViewCell c in grid.SelectedCells) {
                var r = grid.Rows[c.RowIndex].Tag as WorkbenchRow;
                if (r != null) toRemove.Add(r);
            }
            if (toRemove.Count == 0) return;
            int dirty = toRemove.Count(r => r.RenameChanged || r.PropDirty);
            if (dirty > 0 && MessageBox.Show(this, "选中的行里有 " + dirty + " 个文件有未落盘的修改，移除后会丢掉。继续吗？", Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            rows.RemoveAll(toRemove.Contains);
            ValidateRenames();
            RebuildGrid();
        }

        void ShowCurrentPreview() {
            if (rebuilding || busy || grid.CurrentCell == null || grid.CurrentCell.RowIndex < 0 || grid.CurrentCell.RowIndex >= grid.Rows.Count) return;
            var r = grid.Rows[grid.CurrentCell.RowIndex].Tag as WorkbenchRow;
            if (r == null) return;
            string p = r.Rename.Path ?? "";
            if (string.Equals(p, lastPreviewPath, StringComparison.OrdinalIgnoreCase)) return;
            lastPreviewPath = p;
            preview.ShowFile(p);
        }

        // ---- 自定义属性列 & 模板设置 ----

        void EditPropColumns() {
            if (busy) return;
            string text = Ui.Prompt(this, "属性卡列设置", "要显示和写入的自定义属性名，用顿号或逗号隔开（留空恢复默认）", PropCardFields.Join(propFields));
            if (text == null) return;
            var next = PropCardFields.Parse(text);
            next = next.Count > 0 ? next : new List<string>(PropCardFields.Default);

            // 检查被移除的扩展属性列上是否有未落盘的编辑：先确认再清理，避免残留不可见的脏标记与隐形写入
            var nextExtras = new HashSet<string>(WorkbenchRules.ExtraPropFields(next), StringComparer.Ordinal);
            var lostPerRow = new List<KeyValuePair<WorkbenchRow, List<string>>>();
            int lostCells = 0;
            foreach (var r in rows) {
                var keptKeys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var cf in WorkbenchRules.CoreSharedPropFields) keptKeys.Add(r.PropKeyFor(cf));
                foreach (var ef in nextExtras) keptKeys.Add(r.PropKeyFor(ef));
                List<string> dropped = null;
                foreach (var k in r.Prop.Edited.Keys) {
                    if (!keptKeys.Contains(k)) {
                        if (dropped == null) dropped = new List<string>();
                        dropped.Add(k);
                        lostCells++;
                    }
                }
                if (dropped != null) lostPerRow.Add(new KeyValuePair<WorkbenchRow, List<string>>(r, dropped));
            }
            if (lostCells > 0) {
                if (MessageBox.Show(this, "去掉的属性列里有 " + lostCells + " 个格子改过还没写入，去掉列将丢掉这些修改。继续吗？", Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                PushUndo();
                foreach (var pair in lostPerRow) {
                    foreach (var k in pair.Value) pair.Key.Prop.Edited.Remove(k);
                }
            }

            propFields = next;
            try { PropCardFields.Save(SettingsFile.PathOf(PropCardFields.FileName), propFields); } catch (Exception e) { Log.Write("PropCardFieldsSave", e); }
            RebuildGridColumns();

            // 同步清理已不存在列对应的筛选与排序键，避免删列后残留无法清除的隐形筛选
            var validColKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (DataGridViewColumn col in grid.Columns) {
                string ck = col.Tag as string;
                if (!string.IsNullOrEmpty(ck)) validColKeys.Add(ck);
            }
            foreach (var fk in columnFilters.Keys.ToList()) {
                if (!validColKeys.Contains(fk)) columnFilters.Remove(fk);
            }
            if (sortColKey.Length > 0 && !validColKeys.Contains(sortColKey)) {
                sortColKey = "";
                sortDir = 0;
            }

            RebuildGrid();
            SetStatus("已更新属性卡列配置。", Ui.Muted);
        }

        void EditTemplates() {
            if (busy) return;
            using (var dlg = new Form()) {
                Ui.Shell(dlg, "自定义 Excel 清单模板", 640, 270, 540, 240);
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.MaximizeBox = false; dlg.MinimizeBox = false;
                var body = new VerticalStack { Dock = DockStyle.Fill, Padding = Ui.SPad(18, 14, 18, 8), Gap = Ui.S(8) };
                var hint = Ui.Muted8("留空则使用插件内置的公司标准模板（含 SK LOGO、八大类规则图、样式与 ERP 词典）。\r\n如果部门更新了模板，可在下面指定明文 .xlsx 模板路径：");
                hint.AutoSize = false; hint.Height = Ui.S(40);
                body.Add(hint);

                var tbList = new TextBox { Text = customListTemplate, BorderStyle = BorderStyle.FixedSingle, Font = Ui.F8, Width = Ui.S(400) };
                var tbCode = new TextBox { Text = customCodeTemplate, BorderStyle = BorderStyle.FixedSingle, Font = Ui.F8, Width = Ui.S(400) };
                body.Add(TemplateRow("加工件清单模板：", tbList));
                body.Add(TemplateRow("编码申请表模板：", tbCode));

                var foot = new ActionBar { Dock = DockStyle.Bottom, Height = Ui.S(56), Padding = Ui.SPad(16, 0, 16, 0) };
                var bReset = Ui.Ghost("恢复内置模板");
                bReset.Click += (s, e) => { tbList.Text = ""; tbCode.Text = ""; };
                var bCancel = Ui.Secondary("取消", 80, 34); bCancel.Click += (s, e) => dlg.DialogResult = DialogResult.Cancel;
                var bOk = Ui.Primary("确定", 92, 34); bOk.Click += (s, e) => { customListTemplate = tbList.Text.Trim(); customCodeTemplate = tbCode.Text.Trim(); SaveRememberedDefaults(); dlg.DialogResult = DialogResult.OK; };
                foot.AddLeft(bReset); foot.AddRight(bCancel); foot.AddRight(bOk);
                dlg.Controls.Add(body); dlg.Controls.Add(foot);
                dlg.ShowDialog(this);
            }
        }

        Control TemplateRow(string label, TextBox box) {
            var p = new FlowLayoutPanel { Height = Ui.S(34), FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Ui.Bg };
            int lw = Math.Max(Ui.S(116), TextRenderer.MeasureText(label, Ui.F8).Width + Ui.S(10));
            var l = new Label { Text = label, Width = lw, Height = Ui.S(28), TextAlign = ContentAlignment.MiddleLeft, Font = Ui.F8, ForeColor = Ui.Text2 };
            var b = Ui.Small("选择…");
            b.Click += (s, e) => {
                using (var fd = new OpenFileDialog { Filter = "Excel 工作簿 (*.xlsx)|*.xlsx" }) {
                    if (fd.ShowDialog(this) == DialogResult.OK) box.Text = fd.FileName;
                }
            };
            p.Controls.AddRange(new Control[] { l, box, b });
            return p;
        }

        // ---- 动作 1：执行批量改名 ----

        bool ExecuteRename(bool chained) {
            grid.EndEdit();
            int probs;
            var batch = WorkbenchRules.BuildAndValidateRenameBatch(rows, chkFollowRename.Checked, File.Exists, out probs);
            lastRenameProblems = probs;
            if (probs > 0) {
                RebuildGrid();
                MessageBox.Show(this, "有 " + probs + " 处改名冲突或非法文件名（已在「新文件名」列标红），请先修正。", Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            var changed = batch.Where(r => r.Changed).ToList();
            if (changed.Count == 0) return true;

            // 用反射统一检查原位编辑状态，兼容 PartDocument / SheetMetalDocument / AssemblyDocument
            try {
                object activeDoc = app == null ? null : app.ActiveDocument;
                bool ipa = false;
                if (activeDoc != null) {
                    try { ipa = Convert.ToBoolean(activeDoc.GetType().InvokeMember("InPlaceActivated", System.Reflection.BindingFlags.GetProperty, null, activeDoc, null)); } catch { }
                }
                if (ipa) {
                    string name = "";
                    try { name = Convert.ToString(activeDoc.GetType().InvokeMember("Name", System.Reflection.BindingFlags.GetProperty, null, activeDoc, null)); } catch { }
                    MessageBox.Show(this, "天工正在原位编辑「" + name + "」，请先退出原位编辑（回到顶层装配）再改名。", Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }
            } catch { }

            var moved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in changed) moved[r.Path] = r.NewPath;

            BeginBusy("批量改名");
            try {
                var res = renameCad.Run(batch, (toClose, dirty) => {
                    if (toClose.Length == 0) return true;
                    string msg = "改名前会先关闭天工里打开的这些文件（改完后自动重新打开）：\r\n" + toClose;
                    if (dirty.Length > 0) msg += "\r\n\r\n其中这些文件显示有未保存修改：\r\n" + dirty + "\r\n如果刚才动过模型，请先点「否」回天工按 Ctrl+S 保存；没改过模型点「是」继续。";
                    return MessageBox.Show(this, msg, Title + " · 批量改名", MessageBoxButtons.YesNo, dirty.Length > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Question) == DialogResult.Yes;
                }, Pulse);

                if (res.Error.Length > 0) {
                    SetStatus(res.Error == "已取消" ? "已取消改名。" : "改名失败：" + res.Error, res.Error == "已取消" ? Ui.Muted : Ui.Bad);
                    if (res.Error != "已取消") MessageBox.Show(this, res.Error, Title + " · 批量改名", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }

                WorkbenchRules.ApplyRenamedPaths(rows, moved, File.Exists);
                undoStack.Clear();
                redoStack.Clear();
                lastPreviewPath = "";
                preview.Renamed(moved);
                propCad.RefreshActiveContext();
                try {
                    object curDoc = app == null ? null : app.ActiveDocument;
                    string curPath = curDoc == null ? "" : Convert.ToString(curDoc.GetType().InvokeMember("FullName", System.Reflection.BindingFlags.GetProperty, null, curDoc, null));
                    if (!string.IsNullOrEmpty(curPath)) renameCad.SetRootPath(curPath);
                } catch { }
                ValidateRenames();
                RebuildGrid();
                SetStatus(res.Summary(), Ui.Ok);
                if (!chained && res.Warnings.Count > 0) {
                    MessageBox.Show(this, res.Summary() + "\r\n\r\n注意：\r\n" + string.Join("\r\n", res.Warnings.ToArray()), Title + " · 批量改名", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                return true;
            } catch (Exception e) {
                Log.Write("WorkbenchRename", e);
                SetStatus("改名异常：" + e.Message, Ui.Bad);
                MessageBox.Show(this, "改名出错：" + e.Message, Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            } finally {
                EndBusy();
            }
        }

        // ---- 动作 2：写入属性卡 ----

        bool ExecuteWriteProps(bool chained, out List<string> unsavedNames) {
            unsavedNames = new List<string>();
            grid.EndEdit();
            var dirtyRows = rows.Where(r => r.PropDirty).ToList();
            if (dirtyRows.Count == 0) return true;

            if (!propCad.Rebind()) {
                string msg = "载入这张表的「" + propCad.RootName + "」已经关掉了：请在天工里重新打开它（设为当前文档）再点「写入属性」，表里改过的值都还在。";
                SetStatus(msg, Ui.Bad);
                MessageBox.Show(this, msg, Title + " · 写入属性", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            int ok = 0;
            var problems = new List<string>();
            BeginBusy("写入属性卡");
            try {
                for (int i = 0; i < dirtyRows.Count; i++) {
                    var r = dirtyRows[i];
                    Pulse("写入属性 " + (i + 1) + "/" + dirtyRows.Count + "：" + r.Rename.FileName);
                    var writes = WorkbenchRules.CollectPropWrites(r, propFields, chkFillMissing.Checked);
                    if (writes.Count == 0) continue;
                    string result = propCad.Write(r.Prop, writes);
                    if (result == PropCardCad.SaveHint) { r.Prop.Committed(writes); ok++; unsavedNames.Add(r.Rename.FileName); }
                    else if (result.Length == 0) { r.Prop.Committed(writes); ok++; }
                    else problems.Add(r.Rename.FileName + "：" + result);
                }
            } finally {
                EndBusy();
            }

            if (ok > 0 && problems.Count == 0) { undoStack.Clear(); redoStack.Clear(); }
            RebuildGrid();
            string unsavedBrief = unsavedNames.Count > 0
                ? "；其中 " + unsavedNames.Count + " 个在天工里开着（" + string.Join("、", unsavedNames.Take(3).ToArray()) + (unsavedNames.Count > 3 ? " 等" : "") + "），请回天工按 Ctrl+S 保存"
                : "";
            string summary = "属性卡写好 " + ok + " / " + dirtyRows.Count + " 个文件"
                + unsavedBrief
                + (problems.Count > 0 ? "；" + problems.Count + " 个失败" : "");
            SetStatus(summary, problems.Count > 0 || unsavedNames.Count > 0 ? Ui.Warn : Ui.Ok);

            if (problems.Count > 0) {
                var show = problems.Count > 15 ? problems.GetRange(0, 15) : problems;
                MessageBox.Show(this, string.Join("\r\n", show.ToArray()) + (problems.Count > 15 ? "\r\n……共 " + problems.Count + " 条，详见 panel.log" : ""),
                    Title + " · 写入属性", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            return true;
        }

        void ExecuteWriteProps(bool chained) {
            List<string> unsaved;
            ExecuteWriteProps(chained, out unsaved);
        }

        // ---- 动作 3：开始出清单（支持一键先改名/写属性再出清单） ----

        void RunExport() {
            grid.EndEdit();
            int pendingRen = rows.Count(r => r.RenameChanged);
            int pendingProp = rows.Count(r => r.PropDirty);
            if (pendingRen > 0 || pendingProp > 0) {
                var ask = MessageBox.Show(this,
                    "表里还有未落盘的改动（" + pendingRen + " 个待改名、" + pendingProp + " 个待写入属性）：\r\n\r\n"
                    + "• 点「是」：先执行改名并写入属性卡，再出清单（推荐）\r\n"
                    + "• 点「否」：直接按当前表格内容出清单（不改源文件名和源文件属性）\r\n"
                    + "• 点「取消」：返回检查",
                    Title, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (ask == DialogResult.Cancel) return;
                if (ask == DialogResult.Yes) {
                    if (pendingRen > 0 && !ExecuteRename(true)) return;
                    List<string> unsavedNames = new List<string>();
                    if (pendingProp > 0 && !ExecuteWriteProps(true, out unsavedNames)) return;
                    if (unsavedNames.Count > 0) {
                        string listText = string.Join("\r\n• ", unsavedNames.Take(6).ToArray())
                            + (unsavedNames.Count > 6 ? "\r\n• ……共 " + unsavedNames.Count + " 个文件" : "");
                        var cont = MessageBox.Show(this,
                            "已将属性写入天工内存中的以下打开文档：\r\n• " + listText + "\r\n\r\n"
                            + "若需要导出的原三维文件（.par/.asm）磁盘副本也包含刚写入的属性，请点「取消」先回天工按一次 Ctrl+S 保存，再点「开始出清单」。\r\n\r\n直接继续导出清单吗？",
                            Title, MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
                        if (cont != DialogResult.OK) return;
                    }
                }
            }

            var header = ReadHeaderFromUi();
            var options = ReadOptionsFromUi();
            var bomItems = rows.Select(r => r.Bom).ToList();

            if (string.IsNullOrWhiteSpace(options.OutputFolder)) {
                MessageBox.Show(this, "请先指定输出目录。", Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            var issues = BomRules.Validate(bomItems, options);
            var blocking = issues.Where(x => x.Blocking).ToList();
            if (blocking.Count > 0) {
                MessageBox.Show(this, "以下问题会阻止导出，请先修正：\r\n• " + string.Join("\r\n• ", blocking.Take(10).Select(x => x.Message).ToArray()),
                    Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            var warnings = issues.Where(x => !x.Blocking).ToList();
            if (warnings.Count > 0) {
                var r = MessageBox.Show(this, "导出前提示（共 " + warnings.Count + " 条，可继续导出）：\r\n• "
                    + string.Join("\r\n• ", warnings.Take(8).Select(x => x.Message).ToArray())
                    + (warnings.Count > 8 ? "\r\n• ……" : "")
                    + "\r\n\r\n是否继续导出清单？",
                    Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r != DialogResult.Yes) return;
            }

            var selectedItems = bomItems.Where(i => i.Selected).ToList();
            SaveRememberedDefaults();
            BeginBusy("导出加工件清单", true);
            stopRequested = false;
            runBtn.Text = "停止";
            progress.Visible = true;
            progress.Maximum = Math.Max(1, selectedItems.Count);
            progress.Value = 0;

            try {
                var summary = bomCad.Export(header, selectedItems, options,
                    (done, total, msg) => {
                        progress.Maximum = Math.Max(1, total);
                        progress.Value = Math.Min(done, progress.Maximum);
                        SetStatus(msg, Ui.Muted);
                        try { Application.DoEvents(); } catch { }
                    },
                    item => { RefreshAllVisibleRowsInPlace(); },
                    () => stopRequested);

                lastExportedDir = summary.OutputFolder;
                openOutBtn.Enabled = !string.IsNullOrEmpty(lastExportedDir) && Directory.Exists(lastExportedDir);
                RebuildGrid();
                SetStatus(summary.ShortSummary(), summary.Errors.Count > 0 ? Ui.Warn : Ui.Ok);

                string detail = summary.ShortSummary() + "\r\n输出目录：" + summary.OutputFolder;
                if (summary.Errors.Count > 0) {
                    detail += "\r\n\r\n失败项：\r\n• " + string.Join("\r\n• ", summary.Errors.Take(10).ToArray());
                }
                MessageBox.Show(this, detail, Title, MessageBoxButtons.OK, summary.Errors.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
            } catch (Exception e) {
                Log.Write("QuickBomRunExport", e);
                SetStatus("导出失败：" + e.Message, Ui.Bad);
                MessageBox.Show(this, "导出过程中出现错误：" + e.Message, Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
            } finally {
                progress.Visible = false;
                runBtn.Text = "开始出清单";
                EndBusy();
            }
        }

        void OpenOutputFolder() {
            string dir = !string.IsNullOrEmpty(lastExportedDir) && Directory.Exists(lastExportedDir) ? lastExportedDir : tbOutput.Text.Trim();
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            try { Process.Start("explorer.exe", "\"" + dir + "\""); } catch { }
        }

        bool ConfirmDiscard() {
            int ren = rows.Count(r => r.RenameChanged);
            int prop = rows.Count(r => r.PropDirty);
            if (ren == 0 && prop == 0) return true;
            return MessageBox.Show(this, "还有 " + ren + " 个待改名文件、" + prop + " 个待写入属性的文件未落盘，继续将丢掉这些修改。确定吗？",
                Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        }

        void SetInteractiveEnabled(bool enabled) {
            // 忙态期间禁用周边按钮/侧栏/抽屉，彻底杜绝 Application.DoEvents() 重入中途篡改行列表或列结构；
            // 注意不可切 grid.ReadOnly（WinForms 置回 false 会清掉只读列的列级 ReadOnly），表格写操作由 CellBeginEdit / OnGridKeyDown / 全部编辑入口的 busy 守卫拦截。
            if (topDrawer != null) topDrawer.Enabled = enabled;
            if (toolbarPanel != null) toolbarPanel.Enabled = enabled;
            if (railTopPanel != null) railTopPanel.Enabled = enabled;
            if (railFootPanel != null) railFootPanel.Enabled = enabled;
            if (shell != null && shell.Rail != null) shell.Rail.Enabled = enabled;
            if (headerActionButtons != null) {
                for (int i = 0; i < headerActionButtons.Length; i++) {
                    if (headerActionButtons[i] != null) headerActionButtons[i].Enabled = enabled;
                }
            }
            if (openOutBtn != null) openOutBtn.Enabled = enabled && !string.IsNullOrEmpty(lastExportedDir) && Directory.Exists(lastExportedDir);
        }

        void BeginBusy(string what, bool allowStop = false) {
            if (grid.IsCurrentCellInEditMode) { try { grid.EndEdit(); } catch { } }
            busy = true;
            canStopBusy = allowStop;
            busyWhat = what ?? "处理中";
            stopRequested = false;
            SetInteractiveEnabled(false);
            preview.Hold(true);
            Cursor = Cursors.WaitCursor;
            UpdateActionButtons();
        }

        void EndBusy() {
            busy = false;
            canStopBusy = false;
            SetInteractiveEnabled(true);
            Cursor = Cursors.Default;
            preview.Hold(false);
            UpdateActionButtons();
        }

#if TG_DEV_BUILD
        internal void TestSwitchDrawer(int mode) { if (drawerSeg != null) drawerSeg.SelectedIndex = mode; }
        internal void TestSwitchCols(int mode) { if (colViewSeg != null) colViewSeg.SelectedIndex = mode; }
        internal void TestForceClose() { forceClose = true; Close(); }
        internal int TestVisibleRowCount { get { return visibleRows.Count; } }
        internal void TestSortByColumn(int colIndex, int dir) {
            sortColKey = dir == 0 ? "" : ColKey(colIndex);
            sortDir = dir;
            RebuildGrid();
        }
        internal void TestFilterColumn(string colKey, params string[] allowed) {
            if (allowed == null || allowed.Length == 0) columnFilters.Remove(colKey);
            else columnFilters[colKey] = new HashSet<string>(allowed, StringComparer.Ordinal);
            RebuildGrid();
        }
        internal void TestClearFilters() { ClearAllFiltersAndSort(); }
        internal ToolStripDropDown TestOpenFilterPopup(int colIndex) { return OpenColumnFilterPopup(colIndex); }
        internal string TestVerifyFillDownAndUndo() {
            if (grid.Rows.Count < 2) return "skip:rows<2";
            string before = Convert.ToString(grid.Rows[1].Cells[ColRemark].Value) ?? "";
            grid.ClearSelection();
            grid.Rows[0].Cells[ColRemark].Value = "测试填充备注";
            grid.ClearSelection();
            grid.Rows[0].Cells[ColRemark].Selected = true;
            grid.Rows[1].Cells[ColRemark].Selected = true;
            FillDownSelectedCells();
            string afterFill = Convert.ToString(grid.Rows[1].Cells[ColRemark].Value) ?? "";
            PerformUndo();
            string afterUndo = Convert.ToString(grid.Rows[1].Cells[ColRemark].Value) ?? "";
            PerformUndo();
            return "before=" + before + "|afterFill=" + afterFill + "|afterUndo=" + afterUndo;
        }
        internal string TestVerifyCopyPasteAndPerf() {
            if (grid.Rows.Count < 4) return "skip:rows<4";
            // 1. 单格复制 -> 多格粘贴
            grid.ClearSelection();
            grid.Rows[0].Cells[ColRemark].Value = "喷塑特注";
            grid.ClearSelection();
            grid.CurrentCell = grid.Rows[0].Cells[ColRemark];
            grid.Rows[0].Cells[ColRemark].Selected = true;
            CopySelectedCells();

            grid.ClearSelection();
            grid.CurrentCell = grid.Rows[2].Cells[ColRemark]; // 模拟从上往下拖选后光标在底部
            grid.Rows[1].Cells[ColRemark].Selected = true;
            grid.Rows[2].Cells[ColRemark].Selected = true;
            PasteFromClipboard();
            string r1 = Convert.ToString(grid.Rows[1].Cells[ColRemark].Value) ?? "";
            string r2 = Convert.ToString(grid.Rows[2].Cells[ColRemark].Value) ?? "";

            // 2. 多行多列块复制 -> 从底部拖选区粘贴（应从左上角对齐！）
            grid.Rows[0].Cells[ColChange].Value = "ECN-01";
            grid.Rows[1].Cells[ColChange].Value = "ECN-02";
            grid.ClearSelection();
            grid.Rows[0].Cells[ColChange].Selected = true; grid.Rows[0].Cells[ColRemark].Selected = true;
            grid.Rows[1].Cells[ColChange].Selected = true; grid.Rows[1].Cells[ColRemark].Selected = true;
            CopySelectedCells();

            grid.ClearSelection();
            grid.CurrentCell = grid.Rows[3].Cells[ColChange]; // 光标在底行，左上角应是 row 2
            grid.Rows[2].Cells[ColChange].Selected = true;
            grid.Rows[3].Cells[ColChange].Selected = true;
            PasteFromClipboard();
            string b2c = Convert.ToString(grid.Rows[2].Cells[ColChange].Value) ?? "";
            string b3c = Convert.ToString(grid.Rows[3].Cells[ColChange].Value) ?? "";
            string b3r = Convert.ToString(grid.Rows[3].Cells[ColRemark].Value) ?? "";

            // 3. 测 50 次连续单格编辑耗时（应 < 150ms，验证就地刷新彻底不卡顿）
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 50; i++) {
                grid.Rows[0].Cells[ColRemark].Value = "edit-" + i;
            }
            sw.Stop();

            // 全部撤销清理
            for (int i = 0; i < 60 && undoStack.Count > 0; i++) PerformUndo();
            return "single=" + r1 + "/" + r2 + "|block=" + b2c + "/" + b3c + "/" + b3r + "|edit50ms=" + sw.ElapsedMilliseconds;
        }
        internal string TestVerifyRedoAndReplace() {
            if (grid.Rows.Count < 2) return "skip:rows<2";
            string orig = Convert.ToString(grid.Rows[0].Cells[ColRemark].Value) ?? "";
            grid.Rows[0].Cells[ColRemark].Value = "ALPHA-01";
            grid.Rows[1].Cells[ColRemark].Value = "ALPHA-02";
            int replaced = ApplyFindReplace(new WorkbenchFindReplaceDialog.Request {
                ColumnIndex = ColRemark, ColumnLabel = "备注", Scope = 1, Find = "alpha", Replace = "BETA", MatchCase = false
            });
            string afterRep = (Convert.ToString(grid.Rows[0].Cells[ColRemark].Value) ?? "") + "/" + (Convert.ToString(grid.Rows[1].Cells[ColRemark].Value) ?? "");
            PerformUndo();
            string afterUndo = Convert.ToString(grid.Rows[0].Cells[ColRemark].Value) ?? "";
            PerformRedo();
            string afterRedo = Convert.ToString(grid.Rows[0].Cells[ColRemark].Value) ?? "";
            for (int i = 0; i < 10 && undoStack.Count > 0; i++) PerformUndo();
            return "orig=" + orig + "|rep=" + replaced + "|afterRep=" + afterRep + "|undo=" + afterUndo + "|redo=" + afterRedo;
        }
        internal string TestRunFolderExport(string modelFolder, string outputFolder, bool includeCodeReq, bool includeThumbs) {
            LoadFolder(modelFolder, true);
            suppressHeaderEvents = true;
            try { tbOutput.Text = outputFolder; outputManuallyEdited = true; } finally { suppressHeaderEvents = false; }
            chkCodeReq.Checked = includeCodeReq;
            chkThumb.Checked = includeThumbs;
            var header = ReadHeaderFromUi();
            var options = ReadOptionsFromUi();
            var selected = rows.Select(r => r.Bom).Where(i => i.Selected).ToList();
            var sum = bomCad.Export(header, selected, options, (d, t, m) => { }, it => { }, () => false);
            return sum.ShortSummary() + " | errors=" + string.Join("; ", sum.Errors.ToArray());
        }
#endif
    }
}
