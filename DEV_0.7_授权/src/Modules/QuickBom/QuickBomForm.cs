using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using F = SolidEdgeFramework;

namespace TianGongCadSuite {
    // 快速出清单（0.12.0，命令 14）窗口：
    //   1. 从当前装配/工程图/零件载入，或扫描指定模型文件夹 / 添加文件；
    //   2. 自动带出属性卡（名称、图号、材料、数量、表面处理、批次、优先等级）并按公司规则预判八大类；
    //   3. 支持右侧 3D 预览、表格直接改、Excel Ctrl+V 粘贴（如粘贴 ERP 编码/反查列）、批量填入；
    //   4. 一键输出《加工件清单.xlsx》（保留公司模板 LOGO、八大类规则图与公式，可选嵌入三维缩略图）、
    //      可选《编码申请-加工件.xlsx》，以及每个零件的 6 种格式（原三维 / .dft / .dwg / .jpg / .pdf / .step）。
    public sealed class QuickBomForm : Form, IBusyTool {
        const string Title = "快速出清单";

        const int ColSel = 0;
        const int ColSeq = 1;
        const int ColName = 2;
        const int ColDno = 3;
        const int ColMat = 4;
        const int ColQty = 5;
        const int ColCat = 6;
        const int ColSurf = 7;
        const int ColBatch = 8;
        const int ColPrio = 9;
        const int ColChange = 10;
        const int ColRemark = 11;
        const int ColCode = 12;
        const int ColLookupDno = 13;
        const int ColLookupName = 14;
        const int ColDraft = 15;
        const int ColStatus = 16;

        readonly F.Application app;
        readonly BomCad cad;
        readonly List<BomItem> items = new List<BomItem>();
        readonly List<BomItem> visibleItems = new List<BomItem>();

        readonly WizardShell shell = new WizardShell { StepCaption = false };
        readonly PartPreview preview = new PartPreview();
        readonly DataGridView grid = new DataGridView();
        readonly TextBox filterBox = new TextBox();
        readonly TextBox fillBox = new TextBox();

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

        // 选项复选框
        readonly CheckBox chk3d = new CheckBox { Text = "原三维", Checked = true };
        readonly CheckBox chkDft = new CheckBox { Text = "DFT", Checked = true };
        readonly CheckBox chkDwg = new CheckBox { Text = "DWG", Checked = true };
        readonly CheckBox chkJpg = new CheckBox { Text = "JPG", Checked = true };
        readonly CheckBox chkPdf = new CheckBox { Text = "PDF", Checked = true };
        readonly CheckBox chkStep = new CheckBox { Text = "STEP", Checked = true };
        readonly CheckBox chkCodeReq = new CheckBox { Text = "同时生成编码申请表 (.xlsx)", Checked = false };
        readonly CheckBox chkThumb = new CheckBox { Text = "在清单中嵌入三维缩略图", Checked = false };
        readonly CheckBox chkZip = new CheckBox { Text = "完成后打成同名 .zip 压缩包", Checked = false };
        readonly CheckBox chkOverwrite = new CheckBox { Text = "覆盖输出目录已有文件", Checked = true };

        readonly SlimProgress progress = new SlimProgress { Visible = false, Height = 6 };
        readonly Label status = new Label();
        readonly FlatButton runBtn;
        readonly Button openOutBtn;

        string sourceFolder = "";
        string customListTemplate = "";
        string customCodeTemplate = "";
        bool outputManuallyEdited;
        bool suppressHeaderEvents;
        bool rebuilding;
        bool busy, stopRequested;
        int stateFilter; // 0 全部 / 1 已勾选 / 2 缺工程图 / 3 待补信息
        string lastExportedDir = "";

        public bool Busy { get { return busy; } }
        public string BusyWhat { get { return "导出加工件清单"; } }

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
            cad = new BomCad(application);
            preview.App = application;

            Ui.Shell(this, Product.Title(Title), 1480, 760, 960, 560);
            FormBorderStyle = FormBorderStyle.Sizable;
            Controls.Add(shell);
            var page = shell.AddPage(Title, Title, "");

            // ---- 左侧栏：来源 + 筛选 + 输出项勾选 ----
            var railTop = new Panel { BackColor = Ui.Rail, Height = 38 + 6 + 32 + 6 + 32 + 12 + 20 + 28 };
            var btnLoadActive = Ui.Secondary("载入当前文档", 200, 38); btnLoadActive.Click += (s, e) => LoadActive();
            var btnScanFolder = Ui.Ghost("扫描模型文件夹…"); btnScanFolder.Click += (s, e) => PickAndScanFolder();
            var btnAddFiles = Ui.Ghost("添加零件 / 工程图…"); btnAddFiles.Click += (s, e) => AddFiles();
            var lblFilter = new Label { Text = "按名称 / 图号筛选", AutoSize = false, Font = Ui.F8, ForeColor = Ui.Text2, BackColor = Ui.Rail };
            filterBox.BorderStyle = BorderStyle.FixedSingle; filterBox.Font = Ui.F9;
            railTop.Controls.AddRange(new Control[] { btnLoadActive, btnScanFolder, btnAddFiles, lblFilter, filterBox });
            railTop.Resize += (s, e) => {
                int w = railTop.Width;
                btnLoadActive.SetBounds(0, 0, w, 38);
                btnScanFolder.SetBounds(0, 44, w, 32);
                btnAddFiles.SetBounds(0, 82, w, 32);
                lblFilter.SetBounds(2, 126, w, 18);
                filterBox.SetBounds(0, 146, w, 26);
            };
            shell.RailTop = railTop;

            shell.Rail.Visible = true;
            shell.Rail.Kind = StepRail.Mode.Nav;
            shell.Rail.Add("全部零件", "");
            shell.Rail.Add("已勾选导出", "").Dot = Ui.Ok;
            shell.Rail.Add("缺工程图 (.dft)", "").Dot = Color.FromArgb(0xF5, 0x9E, 0x0B);
            shell.Rail.Add("待补信息", "").Dot = Ui.Bad;
            shell.Rail.SelectedChanged += (s, e) => { stateFilter = shell.Rail.Selected; RebuildGrid(); };
            filterBox.TextChanged += (s, e) => RebuildGrid();

            // 侧栏底部：6 种格式 + 附加表格选项
            var railFoot = new Panel { BackColor = Ui.Rail, Height = 178 };
            var lblFmt = new Label { Text = "导出格式（每件 6 种）", AutoSize = false, Font = Ui.F8, ForeColor = Ui.Text2, BackColor = Ui.Rail };
            var fmtPanel = new TableLayoutPanel { ColumnCount = 3, RowCount = 2, BackColor = Ui.Rail, Margin = new Padding(0) };
            for (int c = 0; c < 3; c++) fmtPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
            for (int r = 0; r < 2; r++) fmtPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            foreach (var cb in new[] { chk3d, chkDft, chkDwg, chkJpg, chkPdf, chkStep }) {
                cb.AutoSize = true; cb.Font = Ui.F8; cb.BackColor = Ui.Rail; cb.Margin = new Padding(0);
            }
            fmtPanel.Controls.Add(chk3d, 0, 0); fmtPanel.Controls.Add(chkDft, 1, 0); fmtPanel.Controls.Add(chkDwg, 2, 0);
            fmtPanel.Controls.Add(chkJpg, 0, 1); fmtPanel.Controls.Add(chkPdf, 1, 1); fmtPanel.Controls.Add(chkStep, 2, 1);

            foreach (var cb in new[] { chkCodeReq, chkThumb, chkZip, chkOverwrite }) {
                cb.AutoSize = false; cb.Height = 22; cb.Font = Ui.F8; cb.BackColor = Ui.Rail;
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
                int w = railFoot.Width;
                lblFmt.SetBounds(0, 0, w, 18);
                fmtPanel.SetBounds(0, 20, w, 46);
                chkCodeReq.SetBounds(0, 72, w, 24);
                chkThumb.SetBounds(0, 98, w, 24);
                chkZip.SetBounds(0, 124, w, 24);
                chkOverwrite.SetBounds(0, 150, w, 24);
            };
            shell.RailBottom = railFoot;

            // ---- 页面右上角按钮 ----
            var btnTemplates = Ui.Small("模板设置…"); btnTemplates.Click += (s, e) => EditTemplates();
            var btnRemove = Ui.Small("移除选中行"); btnRemove.Click += (s, e) => RemoveSelectedRows();
            foreach (var b in new[] { btnTemplates, btnRemove }) ((FlatButton)b).SetFore(Ui.Text2);
            shell.AddHeader(page, btnTemplates);
            shell.AddHeader(page, btnRemove);

            // ---- 正文：表头信息卡片 + 快捷工具栏 + 数据表格（配右侧 3D 预览） ----
            var stack = new VerticalStack { Dock = DockStyle.Fill, Padding = new Padding(0), Gap = 8, AutoScroll = false };
            page.Body.Controls.Add(stack);

            stack.Add(BuildHeaderCard());
            stack.Add(BuildToolbar());

            var gridFrame = Ui.Card("", 0, "");
            gridFrame.Padding = new Padding(2, 2, 2, 6);
            BuildGrid();
            gridFrame.Controls.Add(grid);
            shell.AddHeader(page, PartPreview.Attach(gridFrame, preview, "quickbom.preview"));
            stack.Add(gridFrame, true);

            var legend = new Label {
                Text = "Ctrl+V 可从 Excel 整块粘贴（如批量贴入 ERP 编码/反查列）· Delete 清空单元格 · 浅红底 = 图号空/重复或数量无效 · 琥珀底 = 缺少同名 .dft",
                AutoSize = false, Height = 18, Font = Ui.F8, ForeColor = Ui.Muted, BackColor = Ui.Bg
            };
            stack.Add(legend);
            stack.Add(progress);

            // ---- 底部操作栏 ----
            runBtn = (FlatButton)Ui.Primary("开始出清单", 180, 42);
            openOutBtn = Ui.Secondary("打开输出目录", 112, 36);
            openOutBtn.Enabled = false;
            var closeBtn = Ui.Secondary("关闭", 80, 36);
            runBtn.Click += (s, e) => { if (busy) stopRequested = true; else RunExport(); };
            openOutBtn.Click += (s, e) => OpenOutputFolder();
            closeBtn.Click += (s, e) => Close();

            shell.Footer.Padding = new Padding(24, 0, 20, 0);
            shell.Footer.AddRight(closeBtn);
            shell.Footer.AddRight(openOutBtn);
            shell.Footer.AddRight(runBtn);
            shell.Footer.Status = status;
            status.ForeColor = Ui.Muted;

            LoadRememberedDefaults();
            WireHeaderAutoOutput();

            FormClosing += (s, e) => {
                if (busy) { stopRequested = true; e.Cancel = true; return; }
                SaveRememberedDefaults();
            };
        }

        // ---- 表头卡片 ----
        Control BuildHeaderCard() {
            var card = new Panel { Height = 112, BackColor = Ui.Rail, Padding = new Padding(10, 8, 10, 8) };
            card.Paint += (s, e) => {
                using (var p = new Pen(Ui.Line)) e.Graphics.DrawRectangle(p, 0, 0, card.Width - 1, card.Height - 1);
            };
            var tbl = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 12, RowCount = 3, BackColor = Ui.Rail, Margin = new Padding(0) };
            float[] colWidths = { 62, 100, 62, 86, 62, 150, 48, 60, 46, 102, 42, 102 };
            for (int c = 0; c < 12; c++) {
                tbl.ColumnStyles.Add(c % 2 == 0
                    ? new ColumnStyle(SizeType.Absolute, colWidths[c])
                    : new ColumnStyle(SizeType.Percent, colWidths[c]));
            }
            tbl.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            tbl.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            tbl.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));

            StyleBox(tbCustomer); StyleBox(tbProjectCode); StyleBox(tbProjectName);
            StyleBox(tbLineNo); StyleBox(tbStationNo); StyleBox(tbSeqNo);
            StyleBox(tbMechNo); StyleBox(tbListTitle); StyleBox(tbEngineer);
            StyleBox(tbRegion); StyleBox(tbOutput);
            dpIssue.Format = DateTimePickerFormat.Custom; dpIssue.CustomFormat = "yyyy/MM/dd";
            dpDelivery.Format = DateTimePickerFormat.Custom; dpDelivery.CustomFormat = "yyyy/MM/dd";
            dpIssue.Font = Ui.F8; dpIssue.Margin = new Padding(0, 3, 6, 3); dpIssue.Dock = DockStyle.Fill;
            dpDelivery.Font = Ui.F8; dpDelivery.Margin = new Padding(0, 3, 2, 3); dpDelivery.Dock = DockStyle.Fill;

            // 第 1 行
            AddField(tbl, 0, 0, "客户", tbCustomer);
            AddField(tbl, 2, 0, "项目编号", tbProjectCode);
            AddField(tbl, 4, 0, "项目名称", tbProjectName);
            AddField(tbl, 6, 0, "线体号", tbLineNo);
            AddField(tbl, 8, 0, "工位号", tbStationNo);
            AddField(tbl, 10, 0, "单号", tbSeqNo);

            // 第 2 行
            AddField(tbl, 0, 1, "机构号", tbMechNo);
            AddField(tbl, 2, 1, "清单名称", tbListTitle);
            AddField(tbl, 4, 1, "工程师", tbEngineer);
            AddField(tbl, 6, 1, "地区", tbRegion);
            AddField(tbl, 8, 1, "下发", dpIssue);
            AddField(tbl, 10, 1, "交期", dpDelivery);

            // 第 3 行：输出目录 + 浏览
            tbl.Controls.Add(HeaderLabel("输出目录"), 0, 2);
            tbl.Controls.Add(tbOutput, 1, 2);
            tbl.SetColumnSpan(tbOutput, 10);
            var btnBrowse = Ui.Small("浏览…");
            btnBrowse.Dock = DockStyle.Fill;
            btnBrowse.Margin = new Padding(2, 2, 0, 2);
            btnBrowse.Click += (s, e) => BrowseOutput();
            tbl.Controls.Add(btnBrowse, 11, 2);

            card.Controls.Add(tbl);
            return card;
        }

        static Label HeaderLabel(string text) {
            return new Label {
                Text = text, TextAlign = ContentAlignment.MiddleRight, Dock = DockStyle.Fill,
                Font = Ui.F8, ForeColor = Ui.Text2, BackColor = Ui.Rail, Margin = new Padding(0, 0, 4, 0)
            };
        }
        static void StyleBox(TextBox tb) {
            tb.BorderStyle = BorderStyle.FixedSingle;
            tb.Font = Ui.F8;
            tb.Dock = DockStyle.Fill;
            tb.Margin = new Padding(0, 3, 6, 3);
        }
        static void AddField(TableLayoutPanel tbl, int col, int row, string label, Control ctrl) {
            tbl.Controls.Add(HeaderLabel(label), col, row);
            tbl.Controls.Add(ctrl, col + 1, row);
        }

        // ---- 表格上方快捷工具栏 ----
        Control BuildToolbar() {
            var bar = new FlowLayoutPanel {
                Height = 34, FlowDirection = FlowDirection.LeftToRight, WrapContents = false,
                BackColor = Ui.Bg, Margin = new Padding(0), Padding = new Padding(0, 2, 0, 0)
            };
            var bAll = Ui.Small("全选"); bAll.Click += (s, e) => SetVisibleSelected(true);
            var bNone = Ui.Small("全不选"); bNone.Click += (s, e) => SetVisibleSelected(false);
            var bDraftOnly = Ui.Small("只勾有工程图"); bDraftOnly.Click += (s, e) => { foreach (var it in visibleItems) it.Selected = it.HasDraft; RebuildGrid(); };
            var bMetrics = Ui.Small("读取尺寸/重量重算八大类"); bMetrics.Click += (s, e) => LoadMetricsForSelected();
            var bOccQty = Ui.Small("数量按装配实例数"); bOccQty.Click += (s, e) => {
                foreach (var it in items) if (it.Selected) it.Quantity = Math.Max(1, it.OccCount);
                RebuildGrid();
                SetStatus("已将勾选行的数量切换为装配实例数。", Ui.Muted);
            };
            var lblFill = new Label { Text = "  把选中格填成：", AutoSize = true, Font = Ui.F8, ForeColor = Ui.Text2, Margin = new Padding(8, 7, 2, 0) };
            fillBox.BorderStyle = BorderStyle.FixedSingle; fillBox.Font = Ui.F9; fillBox.Width = 140; fillBox.Margin = new Padding(0, 3, 4, 0);
            var bFill = Ui.Small("填入"); bFill.Click += (s, e) => FillSelectedCells(fillBox.Text);
            fillBox.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { FillSelectedCells(fillBox.Text); e.SuppressKeyPress = true; } };

            bar.Controls.AddRange(new Control[] { bAll, bNone, bDraftOnly, bMetrics, bOccQty, lblFill, fillBox, bFill });
            return bar;
        }

        // ---- DataGridView ----
        void BuildGrid() {
            grid.Dock = DockStyle.Fill;
            grid.BackgroundColor = Ui.Bg;
            grid.BorderStyle = BorderStyle.None;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.RowHeadersVisible = false;
            grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
            grid.MultiSelect = true;
            grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
            grid.RowTemplate.Height = 28;
            grid.ColumnHeadersHeight = 32;
            grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
            grid.EnableHeadersVisualStyles = false;
            grid.ColumnHeadersDefaultCellStyle.BackColor = Ui.Rail;
            grid.ColumnHeadersDefaultCellStyle.ForeColor = Ui.Text2;
            grid.ColumnHeadersDefaultCellStyle.Font = Ui.F8;
            grid.DefaultCellStyle.Font = Ui.F8;
            grid.DefaultCellStyle.ForeColor = Ui.Text;
            grid.DefaultCellStyle.SelectionBackColor = Ui.AccentSoft;
            grid.DefaultCellStyle.SelectionForeColor = Ui.Text;
            grid.GridColor = Ui.Line;

            grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "选", Width = 34 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "序号", Width = 42, ReadOnly = true });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "名称", Width = 148 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "图号", Width = 152 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "材料", Width = 84 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "数量", Width = 50 });
            var catCol = new DataGridViewComboBoxColumn {
                HeaderText = "八大类", Width = 96, FlatStyle = FlatStyle.Flat, DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton
            };
            foreach (var lbl in BomRules.CategoryLabels) catCol.Items.Add(lbl);
            grid.Columns.Add(catCol);
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "表面处理", Width = 136 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "批次", Width = 66 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "优先等级", Width = 60 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "变更原因", Width = 78 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "备注", Width = 88 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "编码", Width = 96 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "反查图号", Width = 120 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "反查名称", Width = 120 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "工程图", Width = 56, ReadOnly = true });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "说明 / 状态", Width = 160, ReadOnly = true });

            foreach (DataGridViewColumn col in grid.Columns) col.SortMode = DataGridViewColumnSortMode.NotSortable;

            grid.CurrentCellDirtyStateChanged += (s, e) => {
                if (grid.IsCurrentCellDirty && grid.CurrentCell != null && (grid.CurrentCell.ColumnIndex == ColSel || grid.CurrentCell.ColumnIndex == ColCat)) {
                    grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
                }
            };
            grid.CellValueChanged += OnGridCellValueChanged;
            grid.CurrentCellChanged += (s, e) => ShowCurrentPreview();
            grid.CellClick += (s, e) => ShowCurrentPreview();
            grid.KeyDown += OnGridKeyDown;
            grid.DataError += (s, e) => { e.ThrowException = false; };
        }

        // ---- 载入来源 ----

        public void LoadActive() {
            if (busy) return;
            BeginBusy("载入当前文档");
            try {
                string source, srcPath;
                var list = cad.CollectFromActive(out source, out srcPath, Pulse);
                if (list.Count == 0) {
                    SetStatus("当前没有打开适用文档。可点左侧「扫描模型文件夹…」直接选模型文件夹。", Ui.Muted);
                    return;
                }
                items.Clear();
                items.AddRange(list);
                sourceFolder = string.IsNullOrEmpty(srcPath) ? "" : (Directory.Exists(srcPath) ? srcPath : Path.GetDirectoryName(srcPath));
                ApplyInferredHeader(srcPath);
                RebuildGrid();
                string msg = "已载入 " + source + "：共 " + items.Count + " 个文件，默认勾选 " + items.Count(i => i.Selected) + " 个加工件";
                if (cad.SkippedSubs.Count > 0) msg += "（跳过 " + cad.SkippedSubs.Count + " 个子装配）";
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
            using (var dlg = new FolderBrowserDialog { Description = "选择包含三维零件/装配及工程图（.dft）的模型文件夹" }) {
                if (!string.IsNullOrEmpty(sourceFolder) && Directory.Exists(sourceFolder)) dlg.SelectedPath = sourceFolder;
                else dlg.SelectedPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                LoadFolder(dlg.SelectedPath, false);
            }
        }

        public void LoadFolder(string folder, bool recursive) {
            if (busy || string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
            BeginBusy("扫描文件夹");
            try {
                var list = cad.CollectFromFolder(folder, recursive, Pulse);
                items.Clear();
                items.AddRange(list);
                sourceFolder = folder;
                outputManuallyEdited = false;
                ApplyInferredHeader(folder);
                RebuildGrid();
                SetStatus("已扫描文件夹「" + Path.GetFileName(folder.TrimEnd('\\', '/')) + "」：共 " + items.Count + " 个三维文件，默认勾选 " + items.Count(i => i.Selected) + " 个加工件。", Ui.Text);
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
                    var added = cad.CollectFromFiles(dlg.FileNames, Pulse);
                    var existing = new HashSet<string>(items.Select(i => i.ModelPath), StringComparer.OrdinalIgnoreCase);
                    int count = 0;
                    foreach (var it in added) {
                        if (existing.Add(it.ModelPath)) { items.Add(it); count++; }
                    }
                    if (string.IsNullOrEmpty(sourceFolder) && dlg.FileNames.Length > 0) {
                        sourceFolder = Path.GetDirectoryName(dlg.FileNames[0]);
                    }
                    if (count > 0 && string.IsNullOrWhiteSpace(tbProjectCode.Text)) ApplyInferredHeader(sourceFolder);
                    RebuildGrid();
                    SetStatus("已添加 " + count + " 个文件。", Ui.Text);
                } finally {
                    EndBusy();
                }
            }
        }

        void LoadMetricsForSelected() {
            if (busy) return;
            var todo = items.Where(i => i.Selected).ToList();
            if (todo.Count == 0) { SetStatus("没有勾选的零件。", Ui.Muted); return; }
            BeginBusy("读取尺寸与重量");
            try {
                int n = cad.LoadMetricsForItems(todo, Pulse, () => stopRequested);
                RebuildGrid();
                SetStatus("已读取 " + n + " 个零件的尺寸与重量，并按规则重算八大类。", Ui.Ok);
            } finally {
                EndBusy();
            }
        }

        // ---- 表头同步 ----

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

        void ApplyInferredHeader(string srcPath) {
            var h = ReadHeaderFromUi();
            BomRules.InferHeader(items, srcPath, h);
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
            using (var dlg = new FolderBrowserDialog { Description = "选择清单输出目录" }) {
                string cur = tbOutput.Text.Trim();
                if (cur.Length > 0) {
                    string p = Directory.Exists(cur) ? cur : Path.GetDirectoryName(cur);
                    if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) dlg.SelectedPath = p;
                }
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                outputManuallyEdited = true;
                suppressHeaderEvents = true;
                try { tbOutput.Text = dlg.SelectedPath; } finally { suppressHeaderEvents = false; }
            }
        }

        // ---- 表格渲染与编辑 ----

        void RebuildGrid() {
            rebuilding = true;
            try {
                visibleItems.Clear();
                string kw = (filterBox.Text ?? "").Trim();
                var dnoCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var it in items) {
                    if (!it.Selected) continue;
                    string k = BomRules.SanitizeFileName(it.DrawingNo);
                    int c; dnoCounts.TryGetValue(k, out c); dnoCounts[k] = c + 1;
                }

                int totalAll = items.Count;
                int totalSel = 0, totalNoDft = 0, totalIssue = 0;

                foreach (var it in items) {
                    if (it.Selected) totalSel++;
                    bool noDft = !it.HasDraft;
                    if (it.Selected && noDft) totalNoDft++;
                    bool dup = it.Selected && dnoCounts.ContainsKey(BomRules.SanitizeFileName(it.DrawingNo)) && dnoCounts[BomRules.SanitizeFileName(it.DrawingNo)] > 1;
                    bool hasIssue = it.Selected && (string.IsNullOrWhiteSpace(it.PartName) || string.IsNullOrWhiteSpace(it.DrawingNo) || string.IsNullOrWhiteSpace(it.Material) || it.Quantity <= 0 || dup);
                    if (hasIssue) totalIssue++;

                    if (stateFilter == 1 && !it.Selected) continue;
                    if (stateFilter == 2 && !(it.Selected && noDft)) continue;
                    if (stateFilter == 3 && !hasIssue) continue;
                    if (kw.Length > 0
                        && (it.PartName ?? "").IndexOf(kw, StringComparison.OrdinalIgnoreCase) < 0
                        && (it.DrawingNo ?? "").IndexOf(kw, StringComparison.OrdinalIgnoreCase) < 0
                        && (it.ModelFileName ?? "").IndexOf(kw, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    visibleItems.Add(it);
                }

                shell.Rail.Items[0].Count = totalAll.ToString();
                shell.Rail.Items[1].Count = totalSel.ToString();
                shell.Rail.Items[2].Count = totalNoDft.ToString();
                shell.Rail.Items[3].Count = totalIssue.ToString();
                shell.Rail.Invalidate();

                grid.Rows.Clear();
                int seq = 0;
                foreach (var it in visibleItems) {
                    string seqText = it.Selected ? (++seq).ToString() : "";
                    string note = it.ExportStatus.Length > 0 ? it.ExportStatus
                        : (it.ReadProblem.Length > 0 ? it.ReadProblem
                        : (!it.Selected && it.SkipReason.Length > 0 ? "（" + it.SkipReason + "）"
                        : it.CategoryReason));
                    int idx = grid.Rows.Add(
                        it.Selected,
                        seqText,
                        it.PartName,
                        it.DrawingNo,
                        it.Material,
                        it.Quantity.ToString(),
                        BomRules.CategoryLabel(it.Category),
                        it.SurfaceTreatment,
                        it.Batch,
                        it.Priority,
                        it.ChangeReason,
                        it.Remark,
                        it.Code,
                        it.LookupDrawingNo,
                        it.LookupPartName,
                        it.HasDraft ? "有" : "缺 .dft",
                        note
                    );
                    var row = grid.Rows[idx];
                    row.Tag = it;
                    StyleGridRow(row, it, dnoCounts);
                }
            } finally {
                rebuilding = false;
            }
        }

        void StyleGridRow(DataGridViewRow row, BomItem it, Dictionary<string, int> dnoCounts) {
            row.DefaultCellStyle.ForeColor = it.Selected ? Ui.Text : Ui.Muted;
            row.DefaultCellStyle.BackColor = Ui.Bg;
            if (!it.Selected) return;

            if (!it.HasDraft) {
                row.Cells[ColDraft].Style.BackColor = Ui.WarnSoft;
                row.Cells[ColDraft].Style.ForeColor = Ui.Warn;
            } else {
                row.Cells[ColDraft].Style.ForeColor = Ui.Ok;
            }

            bool dup = dnoCounts != null && dnoCounts.ContainsKey(BomRules.SanitizeFileName(it.DrawingNo)) && dnoCounts[BomRules.SanitizeFileName(it.DrawingNo)] > 1;
            if (string.IsNullOrWhiteSpace(it.DrawingNo) || dup) {
                row.Cells[ColDno].Style.BackColor = Ui.BadSoft;
                row.Cells[ColDno].Style.ForeColor = Ui.Bad;
            }
            if (string.IsNullOrWhiteSpace(it.PartName)) row.Cells[ColName].Style.BackColor = Ui.WarnSoft;
            if (string.IsNullOrWhiteSpace(it.Material)) row.Cells[ColMat].Style.BackColor = Ui.WarnSoft;
            if (it.Quantity <= 0) row.Cells[ColQty].Style.BackColor = Ui.BadSoft;
            if (it.ExportStatus.Contains("完成")) row.Cells[ColStatus].Style.ForeColor = Ui.Ok;
            else if (it.ExportStatus.Contains("失败")) row.Cells[ColStatus].Style.ForeColor = Ui.Bad;
        }

        void OnGridCellValueChanged(object sender, DataGridViewCellEventArgs e) {
            if (rebuilding || e.RowIndex < 0 || e.RowIndex >= grid.Rows.Count) return;
            var it = grid.Rows[e.RowIndex].Tag as BomItem;
            if (it == null) return;
            object raw = grid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value;
            string s = raw == null ? "" : Convert.ToString(raw).Trim();
            switch (e.ColumnIndex) {
                case ColSel:
                    it.Selected = raw is bool && (bool)raw;
                    RebuildGrid();
                    return;
                case ColName: it.PartName = s; break;
                case ColDno: it.DrawingNo = s; break;
                case ColMat:
                    it.Material = s;
                    BomCad.ApplyAutoCategory(it);
                    break;
                case ColQty:
                    int q;
                    it.Quantity = int.TryParse(s, out q) ? q : 0;
                    break;
                case ColCat:
                    int cat = BomRules.ParseCategory(s);
                    if (cat >= 1 && cat <= 8) { it.Category = cat; it.CategoryManual = true; it.CategoryReason = "手动指定"; }
                    break;
                case ColSurf:
                    it.SurfaceTreatment = s;
                    BomCad.ApplyAutoCategory(it);
                    break;
                case ColBatch: it.Batch = s; break;
                case ColPrio: it.Priority = s; break;
                case ColChange: it.ChangeReason = s; break;
                case ColRemark: it.Remark = s; break;
                case ColCode: it.Code = s; break;
                case ColLookupDno: it.LookupDrawingNo = s; break;
                case ColLookupName: it.LookupPartName = s; break;
            }
            RebuildGrid();
        }

        void OnGridKeyDown(object sender, KeyEventArgs e) {
            if (e.Control && e.KeyCode == Keys.V) {
                PasteFromClipboard();
                e.Handled = true;
            } else if (e.KeyCode == Keys.Delete && !grid.IsCurrentCellInEditMode) {
                FillSelectedCells("");
                e.Handled = true;
            }
        }

        void PasteFromClipboard() {
            string text = "";
            try { if (Clipboard.ContainsText()) text = Clipboard.GetText(); } catch { }
            var clip = PropCardFields.ParseClipboard(text);
            if (clip.Count == 0 || grid.CurrentCell == null) return;
            int r0 = grid.CurrentCell.RowIndex;
            int c0 = grid.CurrentCell.ColumnIndex;
            rebuilding = true;
            try {
                for (int dr = 0; dr < clip.Count && r0 + dr < grid.Rows.Count; dr++) {
                    var it = grid.Rows[r0 + dr].Tag as BomItem;
                    if (it == null) continue;
                    var cells = clip[dr];
                    for (int dc = 0; dc < cells.Length && c0 + dc < grid.Columns.Count; dc++) {
                        ApplyCellText(it, c0 + dc, cells[dc]);
                    }
                }
            } finally {
                rebuilding = false;
            }
            RebuildGrid();
        }

        void FillSelectedCells(string text) {
            if (grid.SelectedCells.Count == 0) return;
            rebuilding = true;
            try {
                foreach (DataGridViewCell cell in grid.SelectedCells) {
                    if (cell.ReadOnly || cell.ColumnIndex == ColSel) continue;
                    var it = grid.Rows[cell.RowIndex].Tag as BomItem;
                    if (it != null) ApplyCellText(it, cell.ColumnIndex, text);
                }
            } finally {
                rebuilding = false;
            }
            RebuildGrid();
        }

        static void ApplyCellText(BomItem it, int col, string text) {
            string s = (text ?? "").Trim();
            switch (col) {
                case ColName: it.PartName = s; break;
                case ColDno: it.DrawingNo = s; break;
                case ColMat: it.Material = s; BomCad.ApplyAutoCategory(it); break;
                case ColQty: int q; if (int.TryParse(s, out q) && q > 0) it.Quantity = q; break;
                case ColCat: int c = BomRules.ParseCategory(s); if (c >= 1 && c <= 8) { it.Category = c; it.CategoryManual = true; it.CategoryReason = "手动指定"; } break;
                case ColSurf: it.SurfaceTreatment = s; BomCad.ApplyAutoCategory(it); break;
                case ColBatch: it.Batch = s; break;
                case ColPrio: it.Priority = s; break;
                case ColChange: it.ChangeReason = s; break;
                case ColRemark: it.Remark = s; break;
                case ColCode: it.Code = s; break;
                case ColLookupDno: it.LookupDrawingNo = s; break;
                case ColLookupName: it.LookupPartName = s; break;
            }
        }

        void SetVisibleSelected(bool sel) {
            foreach (var it in visibleItems) it.Selected = sel;
            RebuildGrid();
        }

        void RemoveSelectedRows() {
            var toRemove = new HashSet<BomItem>();
            foreach (DataGridViewCell c in grid.SelectedCells) {
                var it = grid.Rows[c.RowIndex].Tag as BomItem;
                if (it != null) toRemove.Add(it);
            }
            if (toRemove.Count == 0) return;
            items.RemoveAll(toRemove.Contains);
            RebuildGrid();
        }

        void ShowCurrentPreview() {
            if (rebuilding || busy || grid.CurrentCell == null || grid.CurrentCell.RowIndex < 0 || grid.CurrentCell.RowIndex >= grid.Rows.Count) return;
            var it = grid.Rows[grid.CurrentCell.RowIndex].Tag as BomItem;
            if (it != null) preview.ShowFile(it.ModelPath);
        }

        // ---- 模板设置弹窗 ----
        void EditTemplates() {
            using (var dlg = new Form()) {
                Ui.Shell(dlg, "自定义 Excel 清单模板", 620, 260, 520, 230);
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.MaximizeBox = false; dlg.MinimizeBox = false;
                var body = new VerticalStack { Dock = DockStyle.Fill, Padding = new Padding(18, 14, 18, 8), Gap = 8 };
                body.Add(Ui.Muted8("留空则使用插件内置的公司标准模板（含 SK LOGO、八大类规则图、样式与 ERP 词典）。\r\n如果部门更新了模板，可在下面指定明文 .xlsx 模板路径："));

                var tbList = new TextBox { Text = customListTemplate, BorderStyle = BorderStyle.FixedSingle, Font = Ui.F8, Width = 440 };
                var tbCode = new TextBox { Text = customCodeTemplate, BorderStyle = BorderStyle.FixedSingle, Font = Ui.F8, Width = 440 };
                body.Add(TemplateRow("加工件清单模板：", tbList));
                body.Add(TemplateRow("编码申请表模板：", tbCode));

                var foot = new ActionBar { Dock = DockStyle.Bottom, Height = 54, Padding = new Padding(16, 0, 16, 0) };
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
            var p = new FlowLayoutPanel { Height = 32, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Ui.Bg };
            var l = new Label { Text = label, Width = 110, Height = 26, TextAlign = ContentAlignment.MiddleLeft, Font = Ui.F8, ForeColor = Ui.Text2 };
            var b = Ui.Small("选择…");
            b.Click += (s, e) => {
                using (var fd = new OpenFileDialog { Filter = "Excel 工作簿 (*.xlsx)|*.xlsx" }) {
                    if (fd.ShowDialog(this) == DialogResult.OK) box.Text = fd.FileName;
                }
            };
            p.Controls.AddRange(new Control[] { l, box, b });
            return p;
        }

        // ---- 执行导出 ----

        void RunExport() {
            grid.EndEdit();
            var header = ReadHeaderFromUi();
            var options = ReadOptionsFromUi();
            var selected = items.Where(i => i.Selected).ToList();

            if (string.IsNullOrWhiteSpace(options.OutputFolder)) {
                MessageBox.Show(this, "请先指定输出目录。", Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            var issues = BomRules.Validate(items, options);
            var blocking = issues.Where(x => x.Blocking).ToList();
            if (blocking.Count > 0) {
                MessageBox.Show(this, "以下问题会阻止导出，请先修正：\r\n• " + string.Join("\r\n• ", blocking.Take(10).Select(x => x.Message).ToArray()),
                    Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            var warnings = issues.Where(x => !x.Blocking).ToList();
            if (warnings.Count > 0) {
                string msg = "发现 " + warnings.Count + " 条提醒：\r\n• "
                    + string.Join("\r\n• ", warnings.Take(8).Select(x => x.Message).ToArray())
                    + (warnings.Count > 8 ? "\r\n… 等共 " + warnings.Count + " 条" : "")
                    + "\r\n\r\n仍要继续出清单吗？";
                if (MessageBox.Show(this, msg, Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            }

            SaveRememberedDefaults();
            BeginBusy("导出清单");
            progress.Visible = true;
            progress.Maximum = Math.Max(1, selected.Count);
            progress.Value = 0;
            runBtn.Text = "停止";
            runBtn.Enabled = true;

            BomExportSummary result = null;
            try {
                result = cad.Export(header, selected, options,
                    (cur, tot, text) => {
                        progress.Maximum = Math.Max(1, tot);
                        progress.Value = Math.Min(tot, Math.Max(0, cur));
                        SetStatus(text, Ui.Muted);
                        try { Application.DoEvents(); } catch { }
                    },
                    doneItem => {
                        // 即时刷新该行状态
                        RebuildGrid();
                        try { Application.DoEvents(); } catch { }
                    },
                    () => stopRequested);
            } catch (Exception e) {
                Log.Write("QuickBomExport", e);
                SetStatus("导出出错：" + e.Message, Ui.Bad);
                MessageBox.Show(this, "导出过程中出现错误：\r\n" + e.Message, Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
            } finally {
                runBtn.Text = "开始出清单";
                progress.Visible = false;
                EndBusy();
            }

            if (result != null) {
                lastExportedDir = result.OutputFolder;
                openOutBtn.Enabled = Directory.Exists(lastExportedDir);
                RebuildGrid();
                Color col = result.Errors.Count > 0 ? Ui.Warn : Ui.Ok;
                SetStatus(result.ShortSummary(), col);
                if (result.Errors.Count > 0) {
                    MessageBox.Show(this, result.ShortSummary() + "\r\n\r\n部分失败明细：\r\n• "
                        + string.Join("\r\n• ", result.Errors.Take(10).ToArray()), Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        void OpenOutputFolder() {
            string dir = !string.IsNullOrEmpty(lastExportedDir) && Directory.Exists(lastExportedDir) ? lastExportedDir : tbOutput.Text.Trim();
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) {
                try { Process.Start("explorer.exe", "\"" + dir + "\""); } catch { }
            }
        }

        void BeginBusy(string what) {
            busy = true;
            stopRequested = false;
            pulseWatch.Reset();
            preview.Hold(true);
            Cursor = Cursors.WaitCursor;
            SetStatus("正在" + what + "…", Ui.Muted);
        }

        void EndBusy() {
            busy = false;
            stopRequested = false;
            Cursor = Cursors.Default;
            preview.Hold(false);
        }

#if TG_DEV_BUILD
        internal string TestRunFolderExport(string modelFolder, string outputFolder, bool includeCodeReq, bool includeThumbs) {
            LoadFolder(modelFolder, false);
            outputManuallyEdited = true;
            tbOutput.Text = outputFolder;
            tbCustomer.Text = "丰田雷克萨斯";
            tbLineNo.Text = "L2";
            tbStationNo.Text = "C10";
            tbSeqNo.Text = "01";
            chkCodeReq.Checked = includeCodeReq;
            chkThumb.Checked = includeThumbs;
            var selected = items.Where(i => i.Selected).ToList();
            var res = cad.Export(ReadHeaderFromUi(), selected, ReadOptionsFromUi(), null, null, () => false);
            return res.ShortSummary() + "\nlist=" + res.ListXlsxPath + "\ncode=" + res.CodeXlsxPath + "\nthumbs=" + res.ThumbnailsRendered + "\nerrors=" + string.Join("; ", res.Errors.ToArray());
        }
#endif
    }
}
