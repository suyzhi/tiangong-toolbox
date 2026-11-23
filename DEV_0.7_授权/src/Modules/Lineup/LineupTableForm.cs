using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using F = SolidEdgeFramework;
using A = SolidEdgeAssembly;

namespace TianGongCadSuite {
    // Lineup 全宽三步工作台（① 整理清单 → ② 标位置图 → ③ 检查导出）：
    //   · 顶部 50px 轻量三步导航条取代 222px 左侧栏，把窗口近 80% 面积完整还给类 Excel 表格与 CAD 位置图画布；
    //   · 表格开启双缓冲、单击直接输入（无需双击）、Ctrl+滚轮缩放（70%~160%）与 Shift+滚轮横向滚动；
    //   · 编辑零卡顿：CellBeginEdit 零拷贝、仅改过的值才落盘、仅改型号/品牌/接口才写型号库、单次复用规范自检结果。
    public sealed partial class LineupTableForm : Form {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);
        const int EmSetCueBanner = 0x1501;

        static readonly Color WarmTintBg = Color.FromArgb(0xFF, 0xFD, 0xF2);
        static readonly Color EmptyNumBg = Color.FromArgb(0xFF, 0xFB, 0xEB);
        static readonly HashSet<string> BomCategories = new HashSet<string>(LineupTableData.Categories);

        readonly F.Application app;
        readonly A.AssemblyDocument assembly;
        readonly string file, store;
        LineupProject project;
        readonly DataTable data = new DataTable();
        readonly SpreadsheetGrid grid = new SpreadsheetGrid();
        ContextMenuStrip gridMenu;
        readonly ComboBox category = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = Ui.S(135) };
        readonly TextBox search = new TextBox { BorderStyle = BorderStyle.FixedSingle };
        readonly Label status = new Label();
        readonly Stack<LineupProject> undo = new Stack<LineupProject>();
        readonly Timer saver = new Timer { Interval = 550 };
        readonly Timer searchDebounce = new Timer { Interval = 140 };
        readonly CadKeyShield keyShield;
        readonly ToolTip tips = new ToolTip { AutoPopDelay = 12000, InitialDelay = 250 };

        // 全宽三步主框架
        readonly Panel mainShell = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Bg };
        readonly Panel topStepBar = new Panel();
        readonly Panel pageHost = new Panel();
        readonly ActionBar footer = new ActionBar();
        readonly Toast toast = new Toast();
        readonly StepPill[] stepPills = new StepPill[3];
        int currentStep;
        EventHandler toastHandler;

        Panel pageListBody;
        VerticalStack listStack;
        Panel workspaceBar;
        FlowLayoutPanel workspaceFlow;
        Panel modeActionBar;
        Panel sensorActionStrip;
        Panel electricalActionStrip;
        Control topActionStrip;
        RoundBox guideBanner;
        Label guideText;
        Button guideActionBtn;
        Action guideAction;
        Label tableTipLabel;
        Panel workAreaHost;
        Panel workContentHost;
        LineupPneumaticPanel pneumaticPanel;
        Card gridCard;
        Panel gridHost;
        Panel emptyHost;
        Panel catBar;
        FlowLayoutPanel catFlow;
        readonly FlatButton[] catPills = new FlatButton[7];
        Button batchEntryToggleBtn;
        Button batchEntryToggleBtnSensor;
        Button batchEntryToggleBtnElec;
        Button previewToggleBtn;
        Button modelLibraryBtn;
        Button stationPillBtn;
        Button btnUndoFooter;
        Panel zoomBar;
        Button btnZoomOut, btnZoomReset, btnZoomIn;
        Button btnFooterSec;
        Button btnFooterPri;
        PartPreview preview;
        Splitter previewSplit;
        LineupCadStudioPanel cadStudio;
        LineupExportPanel exportPanel;
        int activeViewMode; // 0=气动系统(阀岛树), 1=普通传感器(SE), 2=其他电气件(E), 3=BOM总表(全部)
        string pneumaticSelectedId;

        // 表格缩放与流畅编辑状态
        readonly Dictionary<string, int> baseColWidths = new Dictionary<string, int>(StringComparer.Ordinal);
        int gridZoomPercent = 100;
        bool applyingZoom;
        Font gridFont, gridBoldFont, gridHeaderFont;
        string editStartVal = "";
        bool modelFieldEdited;
        bool loading, dirty, previewWasVisible, batchEntryOpen;
        bool pneumaticDirty = true;
        bool lastEmptyStateKnown, lastEmptyState;
        readonly Dictionary<string, DataRow> dataRowById = new Dictionary<string, DataRow>(StringComparer.Ordinal);
        dynamic highlight;

        // 顶部三步导航胶囊控件：比 222px 左侧栏更紧凑，把横向空间全部留给表格和画布
        sealed class StepPill : Control {
            public int StepNumber;
            public string Title = "";
            public string Badge = "";
            public bool Active, Done, Error;
            bool hover;
            public StepPill(int num, string title) {
                StepNumber = num;
                Title = title;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                Height = Ui.S(34);
                Cursor = Cursors.Hand;
                BackColor = Ui.Rail;
                RecalcWidth();
            }
            public void SetState(bool active, bool done, bool error, string badge) {
                string b = badge ?? "";
                if (Active == active && Done == done && Error == error && Badge == b) return;
                Active = active; Done = done; Error = error; Badge = b;
                RecalcWidth();
                Invalidate();
            }
            void RecalcWidth() {
                int tw = TextRenderer.MeasureText(Title, Ui.F9B).Width;
                int bw = Badge.Length > 0 ? TextRenderer.MeasureText(Badge, Ui.F8).Width + Ui.S(14) : 0;
                Width = Ui.S(40) + tw + bw + Ui.S(12);
            }
            protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
            protected override void OnPaint(PaintEventArgs e) {
                var g = e.Graphics;
                g.Clear(Parent != null ? Parent.BackColor : Ui.Rail);
                var r = new Rectangle(0, 0, Width - 1, Height - 1);
                int rad = Ui.S(8);
                if (Active) {
                    Ui.FillRound(g, r, rad, Color.White);
                    Ui.DrawRound(g, r, rad, Ui.Line, 1);
                } else if (hover) {
                    Ui.FillRound(g, r, rad, Color.FromArgb(0xEC, 0xEC, 0xEF));
                }

                g.SmoothingMode = SmoothingMode.AntiAlias;
                int d = Ui.S(20), cx = Ui.S(10), cy = Height / 2;
                var circle = new Rectangle(cx, cy - d / 2, d, d);
                if (Error) {
                    using (var br = new SolidBrush(Ui.BadSoft)) g.FillEllipse(br, circle);
                    TextRenderer.DrawText(g, "!", Ui.F8, circle, Ui.Bad, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                } else if (Done && !Active) {
                    using (var br = new SolidBrush(Ui.OkSoft)) g.FillEllipse(br, circle);
                    Ui.DrawCheck(g, Rectangle.Inflate(circle, -Ui.S(4), -Ui.S(4)), Ui.Ok, 1.6f);
                } else if (Active) {
                    using (var br = new SolidBrush(Ui.Accent)) g.FillEllipse(br, circle);
                    TextRenderer.DrawText(g, StepNumber.ToString(), Ui.F8, circle, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                } else {
                    using (var pen = new Pen(Ui.Faint, 1.3f)) g.DrawEllipse(pen, circle.X + 0.6f, circle.Y + 0.6f, d - 1.2f, d - 1.2f);
                    TextRenderer.DrawText(g, StepNumber.ToString(), Ui.F8, circle, Ui.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
                g.SmoothingMode = SmoothingMode.Default;

                int tx = cx + d + Ui.S(8);
                int tw = TextRenderer.MeasureText(Title, Ui.F9B).Width;
                TextRenderer.DrawText(g, Title, Active ? Ui.F9B : Ui.F9,
                    new Rectangle(tx, 0, tw + Ui.S(4), Height),
                    Active ? Ui.Accent : (Done ? Ui.Text : Ui.Text2),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

                if (Badge.Length > 0) {
                    int bx = tx + tw + Ui.S(6);
                    int bw = Math.Max(Ui.S(20), Width - bx - Ui.S(8));
                    var br = new Rectangle(bx, Ui.S(7), bw, Height - Ui.S(14));
                    Color bg = Error ? Ui.BadSoft : (Active ? Ui.AccentSoft : Color.FromArgb(0xEB, 0xEB, 0xEE));
                    Color fg = Error ? Ui.Bad : (Active ? Ui.Accent : Ui.Muted);
                    Ui.FillRound(g, br, Ui.S(5), bg);
                    TextRenderer.DrawText(g, Badge, Ui.F8, br, fg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                }
            }
        }

        public LineupTableForm(F.Application host, A.AssemblyDocument doc, string modelLibraryPath = null)
            : this(host, doc, LineupCad.AssemblyFile(doc), modelLibraryPath) {
        }

        // 供离线视觉验证与纯 UI 测试构造（无 CAD 实例时预览真实界面）
        internal LineupTableForm(string assemblyPath, string modelLibraryPath = null)
            : this(null, null, Path.GetFullPath(assemblyPath), modelLibraryPath) {
        }

        LineupTableForm(F.Application host, A.AssemblyDocument doc, string assemblyFile, string modelLibraryPath) {
            app = host;
            assembly = doc;
            file = assemblyFile;
            store = Path.ChangeExtension(file, ".lineup.xml");
            project = File.Exists(store) ? LineupProject.Load(store, file) : new LineupProject { AssemblyFile = file };
            project.InferMetadataFromAssembly(file);
            InitializeCatalog(modelLibraryPath);

            int savedZoom;
            if (int.TryParse(Choices.Get("lineup.grid.zoom", "100"), out savedZoom) && savedZoom >= 70 && savedZoom <= 160)
                gridZoomPercent = savedZoom;

            Ui.Shell(this, Product.Title("Lineup 排位清单"), 1380, 780, 860, 520);
            FormBorderStyle = FormBorderStyle.Sizable;
            WindowStateStore.Bind(this, "lineup", () => !pickingMode);
            keyShield = new CadKeyShield(this, null, ResolveLineupShortcut);
            SizeChanged += (s, e) => {
                if (pickingMode) return;
                AdaptWorkspaceBarToWidth();
                FitMainGridColumns();
            };

            category.Items.Add("全部");
            category.Items.AddRange(LineupTableData.Categories);
            category.Items.Add("旧分类/辅助");
            category.SelectedIndex = 0;

            SetupGrid();
            BuildGridContextMenu();
            InitShellAndPages();
            InitializeEntry();
            InitializeHierarchy();
            InitializePickingPanel();
            ApplyGridZoom(gridZoomPercent, false);

            category.SelectedIndexChanged += (s, e) => { Filter(); RebuildCategoryPills(); };
            searchDebounce.Tick += (s, e) => {
                searchDebounce.Stop();
                ApplySearchFilterNow();
            };
            search.TextChanged += (s, e) => {
                if (searchClearBtn != null) searchClearBtn.Visible = search.Text.Length > 0;
                searchDebounce.Stop();
                searchDebounce.Start();
            };
            grid.CellBeginEdit += OnGridCellBeginEdit;
            grid.CellEndEdit += OnGridCellEndEdit;
            grid.CurrentCellChanged += (s, e) => {
                if (preview != null && preview.Visible && !pickingMode) UpdatePreviewForCurrentRow();
                if (pickingMode) RefreshPickingLabel();
            };
            grid.DataError += (s, e) => { e.ThrowException = false; status.Text = "请选择有效的器件类别。"; };
            grid.KeyDown += (s, e) => {
                if (e.Control && e.KeyCode == Keys.V) { e.SuppressKeyPress = true; Try(() => PasteCells(Clipboard.GetText())); }
                if (e.Control && e.KeyCode == Keys.Z) { e.SuppressKeyPress = true; Try(Undo); }
            };
            saver.Tick += (s, e) => {
                // 用户仍在编辑单元格时绝不强行 EndEdit 打断输入，延后到下个周期保存
                if (grid.IsCurrentCellInEditMode || (pneumaticPanel != null && pneumaticPanel.IsEditing)) return;
                saver.Stop();
                Try(() => SaveDraft(false));
            };

            FormClosing += (s, e) => {
                saver.Stop();
                searchDebounce.Stop();
                if (cadStudio != null) cadStudio.FlushPendingSave();
                if (pneumaticPanel != null) pneumaticPanel.EndEdit();
                grid.EndEdit();
                if (exportPanel != null) exportPanel.CommitMetadataIfChanged();
                if (preview != null) preview.Hold(true);
                if (dirty) {
                    try { SaveDraft(false); }
                    catch (Exception ex) {
                        if (MessageBox.Show(this, ex.Message + "\n是否放弃尚未保存的修改并关闭？", "Lineup", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                            e.Cancel = true;
                    }
                }
                if (modelFieldEdited) LearnAfterSave();
            };
            FormClosed += (s, e) => {
                saver.Dispose();
                searchDebounce.Dispose();
                keyShield.Dispose();
                tips.Dispose();
                DisposeGridFonts();
                ClearHighlight();
                if (preview != null) preview.Dispose();
                if (gridMenu != null) gridMenu.Dispose();
                if (grid.ContextMenuStrip != null) grid.ContextMenuStrip.Dispose();
                DisposeMenus();
            };

            SetupModelEditing();
            Reload(null);
            UpdateStatus("已加载");
        }

        void OnGridCellBeginEdit(object sender, DataGridViewCellCancelEventArgs e) {
            if (loading || e.RowIndex < 0 || e.ColumnIndex < 0) return;
            // O(1) 只记原值，绝不在进入编辑时全表深拷贝，彻底消除单击编辑卡顿
            editStartVal = Convert.ToString(grid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value ?? "");
        }

        void OnGridCellEndEdit(object sender, DataGridViewCellEventArgs e) {
            if (loading || e.RowIndex < 0 || e.ColumnIndex < 0) return;
            string after = Convert.ToString(grid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value ?? "");
            if (string.Equals(after, editStartVal, StringComparison.Ordinal)) return;
            if (!dirty) undo.Push(project);
            if (e.ColumnIndex == 5) Try(() => CompleteKnownModel(e.RowIndex));
            if (e.ColumnIndex == 5 || e.ColumnIndex == 6 || e.ColumnIndex == 10) modelFieldEdited = true;
            dirty = true;
            pneumaticDirty = true;
            if (btnUndoFooter != null) btnUndoFooter.Enabled = undo.Count > 0;
            saver.Stop();
            saver.Start();
        }

        void ApplySearchFilterNow() {
            if (activeViewMode == 0 && pneumaticPanel != null) pneumaticPanel.SetSearchFilter(search.Text);
            else Filter();
            bool nowEmpty = project.Records.Count == 0 && (project.Islands == null || project.Islands.Count == 0) && string.IsNullOrEmpty(search.Text);
            if (!lastEmptyStateKnown || lastEmptyState != nowEmpty) UpdateEmptyState();
        }

        internal bool IsSameAssembly(A.AssemblyDocument doc) {
            if (doc == null || IsDisposed) return false;
            try { return string.Equals(LineupCad.AssemblyFile(doc), file, StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        internal void GoStep(int stepIndex) {
            if (stepIndex < 0 || stepIndex > 2) return;
            if (stepIndex == currentStep && pageListBody != null) { OnStepChanged(); return; }
            currentStep = stepIndex;
            OnStepChanged();
        }

        internal void ToggleBatchEntry(bool open) {
            batchEntryOpen = open;
            if (entryPanel != null) entryPanel.Visible = open && activeViewMode != 0;
            string txt = open ? "收起批量录入 ▴" : "批量录入 ▾";
            foreach (var b in new[] { batchEntryToggleBtn, batchEntryToggleBtnSensor, batchEntryToggleBtnElec }) {
                if (b != null) { b.Text = txt; Ui.FitButton(b); }
            }
            if (listStack != null) listStack.Relayout();
        }

        internal void SetViewMode(int mode) {
            if (mode < 0 || mode > 3) return;
            if (pneumaticPanel != null) pneumaticPanel.EndEdit();
            grid.EndEdit();
            activeViewMode = mode;
            Control freezeTarget = (preview != null && preview.Visible) ? (Control)workContentHost : listStack;
            Ui.BeginRedrawFreeze(freezeTarget);
            try {
                if (mode == 1) {
                    category.SelectedItem = "普通传感器";
                } else if (mode == 2) {
                    category.SelectedItem = "其他电气件";
                } else if (mode == 3) {
                    if (category.SelectedIndex != 0 && (ActiveCategory == "普通传感器" || ActiveCategory == "其他电气件"))
                        category.SelectedIndex = 0;
                }
                if (mode == 0 && pneumaticPanel != null) {
                    pneumaticPanel.SetSearchFilter(search.Text);
                    if (pneumaticDirty || !pneumaticPanel.HasLoadedOnce) {
                        pneumaticPanel.RefreshFromProject(null);
                        pneumaticDirty = false;
                    }
                } else {
                    Filter();
                }
                UpdateEmptyState();
                RebuildWorkspaceTabs();
                UpdateTableTipForMode();
            } finally {
                Ui.EndRedrawFreeze(freezeTarget);
            }
            if (preview != null && preview.Visible) UpdatePreviewForCurrentRow();
        }

        void UpdateTableTipForMode() {
            if (tableTipLabel == null) return;
            if (activeViewMode == 0)
                tableTipLabel.Text = "气动主线：左侧建/选阀岛 · 右侧「+ 添加气缸」自动配阀片与 H/W · 选中格子直接打字输入（双击/F2 编辑） · 双击型号选库 · 右键进入 3D 点选/多行顺序绑定";
            else if (activeViewMode == 1)
                tableTipLabel.Text = "普通传感器 (SE)：选中格子直接打字输入（双击/F2 编辑） · 双击「型号」选库 · 只有常闭需填「常开/常闭」 · 右键进入 3D 点选/多行顺序绑定";
            else if (activeViewMode == 2)
                tableTipLabel.Text = "其他电气件 (E)：三联件、伺服、相机、安全门锁、按钮盒等 · 选中格子直接打字输入 · 右键可进入 3D 点选/多行顺序绑定 · Ctrl+滚轮缩放";
            else
                tableTipLabel.Text = "BOM 总表：类 Excel 全表视图 · 单击选中、直接打字输入（双击/F2 编辑） · 支持 Ctrl+C/V 粘贴、Ctrl+D 向下填充、Delete 清空、右键 3D 顺序绑定";
        }

        void InitShellAndPages() {
            // ---- 顶部 50px 全宽三步导航条 ----
            topStepBar.Dock = DockStyle.Top;
            topStepBar.Height = Ui.S(50);
            topStepBar.BackColor = Ui.Rail;
            topStepBar.Padding = Ui.SPad(14, 8, 14, 8);
            topStepBar.Paint += (s, e) => {
                using (var p = new Pen(Ui.Line)) e.Graphics.DrawLine(p, 0, topStepBar.Height - 1, topStepBar.Width, topStepBar.Height - 1);
            };

            var stepFlow = new FlowLayoutPanel {
                Dock = DockStyle.Left, AutoSize = true, WrapContents = false, BackColor = Ui.Rail, Padding = new Padding(0)
            };
            string[] titles = { "整理清单", "标位置图", "检查导出" };
            for (int i = 0; i < 3; i++) {
                int idx = i;
                stepPills[i] = new StepPill(i + 1, titles[i]) { Margin = new Padding(0) };
                stepPills[i].Click += (s, e) => GoStep(idx);
                stepFlow.Controls.Add(stepPills[i]);
                if (i < 2) {
                    stepFlow.Controls.Add(new Label {
                        Text = "›", AutoSize = true, Font = Ui.F10B, ForeColor = Ui.Faint, BackColor = Ui.Rail,
                        Margin = Ui.SPad(6, 6, 6, 0)
                    });
                }
            }

            var topRightFlow = new FlowLayoutPanel {
                Dock = DockStyle.Right, AutoSize = true, WrapContents = false, BackColor = Ui.Rail, Padding = Ui.SPad(0, 2, 0, 0)
            };
            previewToggleBtn = Ui.Small("显示 3D 预览");
            ((FlatButton)previewToggleBtn).SetFore(Ui.Text2);
            previewToggleBtn.Margin = Ui.SPad(0, 2, 8, 0);
            previewToggleBtn.Click += (s, e) => Try(TogglePreview);
            tips.SetToolTip(previewToggleBtn, "在表格右侧开关三维模型预览，选中已绑行即可旋转查看");

            modelLibraryBtn = Ui.Secondary("型号库", 74, 30);
            modelLibraryBtn.Font = Ui.F8;
            modelLibraryBtn.Margin = Ui.SPad(0, 0, 8, 0);
            modelLibraryBtn.Click += (s, e) => Try(OpenModelLibraryManager);
            tips.SetToolTip(modelLibraryBtn, "打开型号库管理：按气缸 / 阀片 / 阀岛 / 传感器分类查看、新增、编辑、导入与导出");

            stationPillBtn = Ui.Secondary("设置抬头 / 阀岛 ▾", 148, 30);
            stationPillBtn.Font = Ui.F8;
            stationPillBtn.Margin = new Padding(0);
            stationPillBtn.Click += (s, e) => Try(() => ShowNumberSettings());
            tips.SetToolTip(stationPillBtn, "点击修改项目编号、工位前缀、当前阀岛 (Y*) 及阀岛型号");

            topRightFlow.Controls.Add(previewToggleBtn);
            topRightFlow.Controls.Add(modelLibraryBtn);
            topRightFlow.Controls.Add(stationPillBtn);
            topStepBar.Controls.Add(stepFlow);
            topStepBar.Controls.Add(topRightFlow);

            // ---- 中央全宽工作区 ----
            pageHost.Dock = DockStyle.Fill;
            pageHost.BackColor = Ui.Bg;
            pageHost.Padding = Ui.SPad(14, 8, 14, 6);

            // 步骤 ①：方案 A 整理清单（气动主线：阀岛树 + 阀路联动 / 普通传感器 / 其他电气件 / BOM 总表）
            pageListBody = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Bg };
            listStack = new VerticalStack { Dock = DockStyle.Fill, Padding = new Padding(0), Gap = Ui.S(6), AutoScroll = false };
            pageListBody.Controls.Add(listStack);

            workspaceBar = BuildWorkspaceBar();
            listStack.Add(workspaceBar, false);
            listStack.Add(BuildGuideBanner(), false);
            modeActionBar = BuildModeActionBar();
            listStack.Add(modeActionBar, false);
            listStack.Add(BuildCategoryBar(), false);

            workAreaHost = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Bg };
            workContentHost = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Bg };

            gridCard = Ui.Card("", 0, "");
            gridCard.Dock = DockStyle.Fill;
            gridCard.Padding = Ui.SPad(2, 2, 2, 2);
            gridHost = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
            gridHost.Controls.Add(grid);
            gridCard.Controls.Add(gridHost);
            emptyHost = BuildEmptyStateOverlay();
            gridCard.Controls.Add(emptyHost);
            emptyHost.BringToFront();

            pneumaticPanel = BuildPneumaticPanel();
            pneumaticPanel.Visible = false;

            workContentHost.Controls.Add(gridCard);
            workContentHost.Controls.Add(pneumaticPanel);
            workAreaHost.Controls.Add(workContentHost);
            InitPreviewPane();
            listStack.Add(workAreaHost, true);

            tableTipLabel = new Label {
                Text = "气动主线：左侧建/选阀岛 · 右侧「+ 添加气缸」自动配阀片与 H/W · 选中格子直接打字输入（双击/F2 编辑） · 双击型号选库 · 右键进入 3D 点选/多行顺序绑定",
                AutoSize = false, Height = Ui.S(20), Font = Ui.F8, ForeColor = Ui.Muted, BackColor = Ui.Bg,
                TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true
            };
            listStack.Add(tableTipLabel, false);

            // 步骤 ②：嵌入式 CAD 位置图工作台（轻量提交：仅落盘并更新顶栏徽标，不重建 BOM 表和气动树）
            cadStudio = new LineupCadStudioPanel(
                app, assembly,
                () => Snapshot(),
                (updated, msg) => CommitLightFileOnly(updated, msg),
                rec => Try(() => LocateRecord(rec)),
                hide => { if (hide) { ClearHighlight(); Hide(); } else { Show(); Activate(); } },
                msg => { status.Text = msg; RefreshHeaderAndGuide(null); });
            cadStudio.Visible = false;

            // 步骤 ③：嵌入式检查与导出面板（抬头元数据变更走轻量提交，不触发整表重载）
            exportPanel = new LineupExportPanel(
                () => Snapshot(),
                (updated, msg) => CommitLightFileOnly(updated, msg),
                id => JumpToRecordFromAudit(id),
                () => Try(AutoCompleteAllForAudit),
                () => Try(() => { ClipboardUtil.SetText(LineupTableData.Tsv(ExportRows())); ShowToast("已复制全部五类 11 列，可在 WPS/Excel 直接粘贴。", Ui.Ok); }),
                () => Try(() => { ExportRows(); ClipboardUtil.SetText(LineupTableData.Tsv(LineupTableData.ReferenceGrouped(project))); ShowToast("已复制标准 10 列参考 BOM。", Ui.Ok); }),
                () => Try(() => {
                    using (var d = new SaveFileDialog { Filter = "CSV 清单|*.csv", FileName = Path.GetFileNameWithoutExtension(file) + "-Lineup.csv" })
                        if (d.ShowDialog(this) == DialogResult.OK) Export(d.FileName);
                }));
            exportPanel.Visible = false;

            pageHost.Controls.Add(pageListBody);
            pageHost.Controls.Add(cadStudio);
            pageHost.Controls.Add(exportPanel);

            Ui.EnableDoubleBuffer(mainShell);
            Ui.EnableDoubleBuffer(topStepBar);
            Ui.EnableDoubleBuffer(pageHost);
            Ui.EnableDoubleBuffer(pageListBody);
            Ui.EnableDoubleBuffer(workAreaHost);
            Ui.EnableDoubleBuffer(workContentHost);
            Ui.EnableDoubleBuffer(gridHost);
            Ui.EnableDoubleBuffer(catFlow);
            Ui.EnableDoubleBuffer(workspaceFlow);

            // ---- 底部 52px 紧凑操作栏（含缩放器） ----
            footer.Dock = DockStyle.Bottom;
            footer.Height = Ui.S(52);
            footer.Padding = Ui.SPad(14, 0, 14, 0);
            footer.Gap = Ui.S(8);
            footer.Status = status;
            status.ForeColor = Ui.Muted;

            btnUndoFooter = Ui.Ghost("撤销");
            ((FlatButton)btnUndoFooter).SetFore(Ui.Text2);
            btnUndoFooter.Click += (s, e) => Try(Undo);
            footer.AddLeft(btnUndoFooter);

            zoomBar = BuildZoomControl();
            footer.AddLeft(zoomBar);

            btnFooterSec = Ui.Secondary("直接导出 .xlsx", 118, 34);
            btnFooterPri = Ui.Primary("下一步：标位置图 →", 156, 36);
            btnFooterSec.Click += (s, e) => Try(OnFooterSecondaryClick);
            btnFooterPri.Click += (s, e) => Try(OnFooterPrimaryClick);
            footer.AddRight(btnFooterSec);
            footer.AddRight(btnFooterPri);

            mainShell.Controls.Add(toast);
            mainShell.Controls.Add(pageHost);
            mainShell.Controls.Add(topStepBar);
            mainShell.Controls.Add(footer);
            mainShell.Resize += (s, e) => LayoutToast();
            Controls.Add(mainShell);

            OnStepChanged();
        }

        Panel BuildZoomControl() {
            var p = new Panel { Width = Ui.S(112), Height = Ui.S(28), BackColor = Ui.Bg };
            btnZoomOut = Ui.Small("−"); btnZoomOut.Width = Ui.S(26); btnZoomOut.Height = Ui.S(26); ((FlatButton)btnZoomOut).SetFore(Ui.Text2);
            btnZoomReset = Ui.Small("100%"); btnZoomReset.Width = Ui.S(52); btnZoomReset.Height = Ui.S(26); ((FlatButton)btnZoomReset).SetFore(Ui.Muted);
            btnZoomIn = Ui.Small("+"); btnZoomIn.Width = Ui.S(26); btnZoomIn.Height = Ui.S(26); ((FlatButton)btnZoomIn).SetFore(Ui.Text2);

            btnZoomOut.Location = new Point(0, 1);
            btnZoomReset.Location = new Point(Ui.S(28), 1);
            btnZoomIn.Location = new Point(Ui.S(82), 1);

            btnZoomOut.Click += (s, e) => ApplyGridZoom(gridZoomPercent - 10, true);
            btnZoomReset.Click += (s, e) => ApplyGridZoom(100, true);
            btnZoomIn.Click += (s, e) => ApplyGridZoom(gridZoomPercent + 10, true);
            tips.SetToolTip(btnZoomOut, "缩小表格字号与行高（Ctrl+滚轮向下）");
            tips.SetToolTip(btnZoomReset, "点击恢复 100% 默认缩放（支持 Ctrl+滚轮缩放）");
            tips.SetToolTip(btnZoomIn, "放大表格字号与行高（Ctrl+滚轮向上）");

            p.Controls.AddRange(new Control[] { btnZoomOut, btnZoomReset, btnZoomIn });
            return p;
        }

        void LayoutToast() {
            int pad = Ui.S(18);
            toast.Rest = Math.Max(Ui.S(60), mainShell.ClientSize.Height - footer.Height - Ui.S(10));
            toast.SetBounds(pad, toast.Top, Math.Max(Ui.S(200), mainShell.ClientSize.Width - pad * 2), toast.Height);
            if (!toast.Visible) toast.Top = toast.Rest - toast.Height;
        }

        void OnStepChanged() {
            if (currentStep != 1 && cadStudio != null) cadStudio.FlushPendingSave();
            LineupAuditResult cachedAudit = null;
            bool freeze = preview == null || !preview.Visible;
            if (freeze) Ui.BeginRedrawFreeze(pageHost);
            try {
                if (currentStep == 1 && cadStudio != null) {
                    if (dirty) SaveDraft(false);
                    cadStudio.SyncFromProject(true);
                } else if (currentStep == 2 && exportPanel != null) {
                    if (dirty) SaveDraft(false);
                    cachedAudit = exportPanel.RefreshFromProject();
                } else if (currentStep == 0 && exportPanel != null) {
                    exportPanel.CommitMetadataIfChanged();
                    if (activeViewMode == 0 && pneumaticPanel != null && (pneumaticDirty || !pneumaticPanel.HasLoadedOnce)) {
                        pneumaticPanel.RefreshFromProject(null);
                        pneumaticDirty = false;
                    }
                    UpdateEmptyState();
                    UpdatePreviewForCurrentRow();
                }

                if (pageListBody != null) pageListBody.Visible = currentStep == 0;
                if (cadStudio != null) cadStudio.Visible = currentStep == 1;
                if (exportPanel != null) exportPanel.Visible = currentStep == 2;
                bool hasData = project != null && (project.Records.Count > 0 || (project.Islands != null && project.Islands.Count > 0));
                if (previewToggleBtn != null) previewToggleBtn.Visible = currentStep == 0 && hasData;
                if (zoomBar != null) zoomBar.Visible = currentStep == 0 && hasData;

                if (currentStep == 0) {
                    btnFooterSec.Text = "直接导出 .xlsx";
                    btnFooterPri.Text = "下一步：标位置图 →";
                } else if (currentStep == 1) {
                    btnFooterSec.Text = "← 返回清单";
                    btnFooterPri.Text = "下一步：检查导出 →";
                } else {
                    btnFooterSec.Text = "← 返回位置图";
                    btnFooterPri.Text = "导出标准 Excel (.xlsx)";
                }
                Ui.FitButton(btnFooterSec);
                Ui.FitButton(btnFooterPri);
                footer.PerformLayout();
                RefreshHeaderAndGuide(cachedAudit);
            } finally {
                if (freeze) Ui.EndRedrawFreeze(pageHost);
            }
        }

        void OnFooterSecondaryClick() {
            if (currentStep == 0) QuickExportXlsxDirect(this);
            else if (currentStep == 1) GoStep(0);
            else GoStep(1);
        }

        void OnFooterPrimaryClick() {
            if (currentStep == 0) GoStep(1);
            else if (currentStep == 1) GoStep(2);
            else QuickExportXlsxDirect(this);
        }

        void ShowToast(string text, Color tone, string actionText = "", EventHandler onAction = null) {
            if (!IsHandleCreated || pickingMode) return;
            LayoutToast();
            toast.ActionClick -= toastHandler;
            toastHandler = onAction;
            if (onAction != null) toast.ActionClick += onAction;
            toast.Show(text, tone, actionText, 3800);
        }

        void DisposeGridFonts() {
            if (gridFont != null) { gridFont.Dispose(); gridFont = null; }
            if (gridBoldFont != null) { gridBoldFont.Dispose(); gridBoldFont = null; }
            if (gridHeaderFont != null) { gridHeaderFont.Dispose(); gridHeaderFont = null; }
        }

        // 平滑缩放表格（70% ~ 160%）：同步缩放字号、行高、表头高度与列宽，支持 Ctrl+滚轮与底部按钮
        internal void ApplyGridZoom(int percent, bool persist) {
            int clamped = Math.Max(70, Math.Min(160, ((percent + 5) / 10) * 10));
            if (clamped == gridZoomPercent && gridFont != null) return;
            gridZoomPercent = clamped;
            if (persist) Choices.Set("lineup.grid.zoom", clamped.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (btnZoomReset != null) btnZoomReset.Text = clamped + "%";

            float scale = clamped / 100f;
            var oldFont = gridFont;
            var oldBold = gridBoldFont;
            var oldHdr = gridHeaderFont;
            gridFont = new Font(Ui.Family, Math.Max(7f, 9.25f * scale), FontStyle.Regular);
            gridBoldFont = new Font(Ui.Family, Math.Max(7f, 9.25f * scale), FontStyle.Bold);
            gridHeaderFont = new Font(Ui.Family, Math.Max(7f, 8.5f * scale), FontStyle.Regular);

            Ui.BeginRedrawFreeze(workContentHost);
            try {
                applyingZoom = true;
                grid.SuspendLayout();
                try {
                    grid.DefaultCellStyle.Font = gridFont;
                    grid.ColumnHeadersDefaultCellStyle.Font = gridHeaderFont;
                    int rowH = Math.Max(Ui.S(22), (int)Math.Round(Ui.S(31) * scale));
                    int hdrH = Math.Max(Ui.S(26), (int)Math.Round(Ui.S(33) * scale));
                    grid.RowTemplate.Height = rowH;
                    grid.ColumnHeadersHeight = hdrH;
                    foreach (DataGridViewRow row in grid.Rows) if (row.Height != rowH) row.Height = rowH;
                } finally {
                    grid.ResumeLayout();
                    applyingZoom = false;
                }
                FitMainGridColumns();
                if (pneumaticPanel != null) pneumaticPanel.ApplyZoom(clamped);
            } finally {
                Ui.EndRedrawFreeze(workContentHost);
            }
            if (oldFont != null) oldFont.Dispose();
            if (oldBold != null) oldBold.Dispose();
            if (oldHdr != null) oldHdr.Dispose();
        }

        // 智能满屏列宽适配：最大化/宽屏或缩小缩放比时自动将富余宽度按权重分配给文本列，彻底消除右侧大片空白；
        // 窗口缩窄或放大到 120%+ 时严格保持按缩放比例计算的最小可读宽度并平滑启用横滚。
        internal void FitMainGridColumns() {
            if (grid == null || grid.Columns.Count == 0 || grid.ClientSize.Width < Ui.S(200)) return;
            float scale = gridZoomPercent / 100f;
            int rowHdr = grid.RowHeadersVisible ? grid.RowHeadersWidth : 0;
            int vScroll = SystemInformation.VerticalScrollBarWidth + Ui.S(4);
            int avail = grid.ClientSize.Width - rowHdr - vScroll;
            if (avail < Ui.S(160)) return;

            var visCols = grid.Columns.Cast<DataGridViewColumn>().Where(c => c.Visible).ToList();
            if (visCols.Count == 0) return;

            var minW = new int[visCols.Count];
            var weights = new float[visCols.Count];
            int totalMin = 0;
            float totalWeight = 0f;
            for (int i = 0; i < visCols.Count; i++) {
                var col = visCols[i];
                int bw;
                if (!baseColWidths.TryGetValue(col.Name, out bw)) { bw = col.Width; baseColWidths[col.Name] = bw; }
                int mw = Math.Max(Ui.S(48), (int)Math.Round(bw * scale));
                minW[i] = mw;
                totalMin += mw;
                float w = MainGridFlexWeight(col.Name);
                weights[i] = w;
                totalWeight += w;
            }

            int extra = avail - totalMin;
            applyingZoom = true;
            grid.SuspendLayout();
            try {
                int used = 0;
                for (int i = 0; i < visCols.Count; i++) {
                    int add = 0;
                    if (extra > 0 && totalWeight > 0f && weights[i] > 0f) {
                        add = (int)Math.Floor(extra * (weights[i] / totalWeight));
                        used += add;
                    }
                    int target = minW[i] + add;
                    if (visCols[i].Width != target) visCols[i].Width = target;
                }
                if (extra > used) {
                    int lastFlex = -1;
                    for (int i = visCols.Count - 1; i >= 0; i--) if (weights[i] > 0f) { lastFlex = i; break; }
                    if (lastFlex >= 0) visCols[lastFlex].Width += (extra - used);
                }
            } finally {
                grid.ResumeLayout();
                applyingZoom = false;
            }
        }

        static float MainGridFlexWeight(string colName) {
            if (colName == "描述") return 3.0f;
            if (colName == "型号") return 2.5f;
            if (colName == "备注" || colName == "功能参数") return 2.0f;
            if (colName == "传感器接口类型" || colName == "关联模型" || colName == "阀岛型号") return 1.5f;
            if (colName == "品牌" || colName == "机构名称") return 0.8f;
            return 0f;
        }

        void HandleGridMouseWheel(object sender, MouseEventArgs e) {
            if ((ModifierKeys & Keys.Control) == Keys.Control) {
                var he = e as HandledMouseEventArgs;
                if (he != null) he.Handled = true;
                ApplyGridZoom(gridZoomPercent + (e.Delta > 0 ? 10 : -10), true);
            } else if ((ModifierKeys & Keys.Shift) == Keys.Shift) {
                var he = e as HandledMouseEventArgs;
                if (he != null) he.Handled = true;
                int step = Ui.S(80);
                int next = grid.HorizontalScrollingOffset + (e.Delta > 0 ? -step : step);
                grid.HorizontalScrollingOffset = Math.Max(0, next);
            }
        }

        void SetupGrid() {
            // 双缓冲彻底消除滚动闪烁和重绘卡顿
            try {
                var pi = typeof(DataGridView).GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic);
                if (pi != null) pi.SetValue(grid, true, null);
            } catch { }

            grid.Dock = DockStyle.Fill;
            grid.AutoGenerateColumns = false;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeColumns = true;
            grid.AllowUserToResizeRows = true;
            grid.EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2;
            grid.RowHeadersWidth = Ui.S(36);
            grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
            grid.MultiSelect = true;
            grid.BackgroundColor = Color.White;
            grid.BorderStyle = BorderStyle.None;
            grid.ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithoutHeaderText;
            grid.EnableHeadersVisualStyles = false;
            grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
            grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            grid.ColumnHeadersDefaultCellStyle.BackColor = Ui.Rail;
            grid.ColumnHeadersDefaultCellStyle.ForeColor = Ui.Muted;
            grid.ColumnHeadersDefaultCellStyle.Font = Ui.F8;
            grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Ui.Rail;
            grid.ColumnHeadersDefaultCellStyle.Padding = Ui.SPad(5, 0, 5, 0);
            grid.DefaultCellStyle.Padding = Ui.SPad(5, 0, 5, 0);
            grid.DefaultCellStyle.ForeColor = Ui.Text;
            grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(0xFB, 0xFB, 0xFC);
            grid.GridColor = Color.FromArgb(0xEE, 0xEE, 0xF2);
            grid.DefaultCellStyle.SelectionBackColor = Ui.AccentSoft;
            grid.DefaultCellStyle.SelectionForeColor = Ui.Text;
            grid.RowHeadersDefaultCellStyle.BackColor = Ui.Rail;
            grid.RowHeadersDefaultCellStyle.SelectionBackColor = Ui.AccentSoft;
            grid.RowHeadersDefaultCellStyle.ForeColor = Ui.Muted;

            data.Columns.Add("Id");
            data.Columns.Add("类别");
            foreach (var h in LineupTableData.Headers) data.Columns.Add(h);
            data.Columns.Add("关联模型");
            data.Columns.Add("阀岛型号");

            var types = new DataGridViewComboBoxColumn {
                Name = "类别", HeaderText = "类别", DataPropertyName = "类别",
                Width = 116, FlatStyle = FlatStyle.Flat
            };
            types.Items.AddRange(LineupTableData.Categories);
            foreach (var c in project.Records.Select(r => LineupTableData.Category(r.Category)).Distinct())
                if (!types.Items.Contains(c)) types.Items.Add(c);
            grid.Columns.Add(types);

            int[] widths = { 138, 110, 110, 215, 205, 96, 78, 112, 120, 126, 170 };
            for (int i = 0; i < 11; i++) {
                grid.Columns.Add(new DataGridViewTextBoxColumn {
                    Name = LineupTableData.Headers[i],
                    HeaderText = LineupTableData.Headers[i],
                    DataPropertyName = LineupTableData.Headers[i],
                    Width = widths[i],
                    SortMode = DataGridViewColumnSortMode.Automatic
                });
            }
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "关联模型", HeaderText = "3D 绑定", DataPropertyName = "关联模型", Width = 140, ReadOnly = true });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "阀岛型号", HeaderText = "所属阀岛型号", DataPropertyName = "阀岛型号", Width = 165, ReadOnly = true });
            Ui.FitGrid(grid);
            foreach (DataGridViewColumn col in grid.Columns) baseColWidths[col.Name] = col.Width;
            grid.ColumnWidthChanged += (s, e) => {
                if (applyingZoom || e.Column == null) return;
                float sc = Math.Max(0.5f, gridZoomPercent / 100f);
                baseColWidths[e.Column.Name] = (int)Math.Round(e.Column.Width / sc);
            };
            grid.Resize += (s, e) => FitMainGridColumns();
            grid.Columns[0].Frozen = true;
            grid.Columns[1].Frozen = true;
            grid.DataSource = data;

            grid.SuppressDoubleClickEdit = (rowIndex, colIndex) => colIndex == 5;

            // 类 Excel 交互：左键单击只选中不强行进编辑；右键保留多行选区（含行头右键）并弹出与主题一致的上下文菜单
            grid.CellMouseUp += (s, e) => {
                if (e.Button != MouseButtons.Right || e.RowIndex < 0) return;
                var selRows = grid.SelectedCells.Cast<DataGridViewCell>().Where(c => c.RowIndex >= 0).Select(c => c.RowIndex)
                    .Concat(grid.SelectedRows.Cast<DataGridViewRow>().Where(r => r.Index >= 0).Select(r => r.Index))
                    .Distinct().ToList();
                bool clickedInSelectedRow = selRows.Contains(e.RowIndex);
                if (e.ColumnIndex < 0) {
                    if (!clickedInSelectedRow) {
                        grid.ClearSelection();
                        grid.Rows[e.RowIndex].Selected = true;
                        var firstVis = grid.Columns.Cast<DataGridViewColumn>().FirstOrDefault(c => c.Visible);
                        if (firstVis != null) grid.CurrentCell = grid.Rows[e.RowIndex].Cells[firstVis.Index];
                    }
                    ShowGridContextMenu(e.RowIndex);
                    return;
                }
                if (!grid.Rows[e.RowIndex].Cells[e.ColumnIndex].Selected && !(selRows.Count > 1 && clickedInSelectedRow)) {
                    grid.ClearSelection();
                    grid.CurrentCell = grid.Rows[e.RowIndex].Cells[e.ColumnIndex];
                    grid.Rows[e.RowIndex].Cells[e.ColumnIndex].Selected = true;
                }
                ShowGridContextMenu(e.RowIndex);
            };

            grid.MouseWheel += HandleGridMouseWheel;

            // 零分配 CellFormatting：预建常量颜色与静态集合，不拖慢滚动帧率
            grid.CellFormatting += (s, e) => {
                if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
                var col = grid.Columns[e.ColumnIndex];
                string colName = col.Name;
                if (colName == "编号") {
                    string raw = Convert.ToString(e.Value ?? "").Trim();
                    if (raw.Length == 0) e.CellStyle.BackColor = EmptyNumBg;
                    else if (gridBoldFont != null) e.CellStyle.Font = gridBoldFont;
                    return;
                }
                if (colName == "关联模型") {
                    string raw = Convert.ToString(e.Value ?? "");
                    if (raw.Length == 0) {
                        e.Value = "未绑定";
                        e.CellStyle.ForeColor = Ui.Faint;
                    } else {
                        int slash = raw.LastIndexOf('/');
                        e.Value = "✓ " + (slash >= 0 ? raw.Substring(slash + 1).Trim() : raw);
                        e.CellStyle.ForeColor = Ui.Ok;
                    }
                    e.FormattingApplied = true;
                    return;
                }
                string cat = Convert.ToString(grid.Rows[e.RowIndex].Cells[0].Value ?? "");
                if (colName == "上级") {
                    string raw = Convert.ToString(e.Value ?? "").Trim();
                    if (raw == "未归类" || raw.Length == 0) {
                        if (cat == "气缸") { e.Value = "待配阀片"; e.CellStyle.ForeColor = Ui.Warn; }
                        else { e.Value = "—"; e.CellStyle.ForeColor = Ui.Faint; }
                        e.FormattingApplied = true;
                    }
                } else if (colName == "型号") {
                    string raw = Convert.ToString(e.Value ?? "").Trim();
                    bool editingCell = grid.IsCurrentCellInEditMode && grid.CurrentCell != null
                        && grid.CurrentCell.RowIndex == e.RowIndex && grid.CurrentCell.ColumnIndex == e.ColumnIndex;
                    if (raw.Length == 0 && !editingCell && BomCategories.Contains(cat)) {
                        e.Value = "双击选型号";
                        e.CellStyle.ForeColor = Ui.Faint;
                        e.CellStyle.BackColor = WarmTintBg;
                        e.FormattingApplied = true;
                    }
                } else if ((colName == "描述" && BomCategories.Contains(cat)) || (colName == "安装方式" && cat == "气缸")) {
                    string raw = Convert.ToString(e.Value ?? "").Trim();
                    if (raw.Length == 0) e.CellStyle.BackColor = WarmTintBg;
                }
            };
        }

        // 步骤 ① 智能“下一步建议”引导条
        Control BuildGuideBanner() {
            guideBanner = new RoundBox { Height = Ui.S(36), Fill = Ui.AccentSoft, Radius = Ui.S(8), Padding = Ui.SPad(12, 0, 8, 0), Visible = false };
            guideText = new Label {
                Dock = DockStyle.Fill, Font = Ui.F9, ForeColor = Ui.Accent, BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true
            };
            guideActionBtn = Ui.Primary("一键补齐", 128, 26);
            guideActionBtn.Font = Ui.F8;
            guideActionBtn.Click += (s, e) => { if (guideAction != null) Try(guideAction); };
            var btnHost = new Panel { Dock = DockStyle.Right, Width = Ui.S(160), BackColor = Ui.AccentSoft };
            btnHost.Controls.Add(guideActionBtn);
            btnHost.Resize += (s, e) => {
                guideActionBtn.Location = new Point(Math.Max(0, btnHost.Width - guideActionBtn.Width), (btnHost.Height - guideActionBtn.Height) / 2);
            };
            guideBanner.Controls.Add(guideText);
            guideBanner.Controls.Add(btnHost);
            return guideBanner;
        }

        void RefreshGuideBanner(LineupAuditResult audit) {
            if (guideBanner == null || project == null) return;
            bool isEmpty = project.Records.Count == 0 && (project.Islands == null || project.Islands.Count == 0) && string.IsNullOrEmpty(search.Text);
            if (isEmpty) { guideBanner.Visible = false; return; }
            guideBanner.Visible = true;
            if (audit == null) audit = LineupAudit.Run(project);

            var islands = LineupPneumaticOps.ListIslands(project);
            var missPairCyls = new HashSet<string>(
                audit.Issues.Where(i => i.RuleCode == "CYL_NO_VALVE" || i.RuleCode == "CYL_NO_SENSOR").Select(i => i.RecordId));
            int fieldMiss = audit.Issues.Count(i =>
                i.RuleCode == "CYL_FIELD" || i.RuleCode == "VALVE_FIELD" || i.RuleCode == "CSEN_FIELD"
                || i.RuleCode == "GSEN_FIELD" || i.RuleCode == "OTHER_FIELD");
            int unboundPrimary = project.Records.Count(r => {
                string c = LineupTableData.Category(r.Category);
                return (c == "气缸" || c == "普通传感器" || c == "其他电气件") && string.IsNullOrEmpty(r.ReferenceKey);
            });

            if (islands.Count > 0 && project.Records.Count(r => BomCategories.Contains(LineupTableData.Category(r.Category))) == 0) {
                guideBanner.Fill = Ui.AccentSoft;
                guideText.ForeColor = Ui.Accent;
                guideText.Text = "阀岛 " + islands[0].Key + " 已就绪！点右侧「+ 添加气缸」自动配齐阀片与 H/W，或先批量铺设阀片 / 预留位。";
                guideActionBtn.Text = "+ 新建更多阀岛";
                guideAction = () => { SetViewMode(0); if (pneumaticPanel != null) pneumaticPanel.OpenCreateIslandDialog(); };
            } else if (missPairCyls.Count > 0) {
                guideBanner.Fill = Ui.AccentSoft;
                guideText.ForeColor = Ui.Accent;
                guideText.Text = "下一步建议：有 " + missPairCyls.Count + " 个气缸还没配齐阀片或 H/W 传感器，点右侧按钮一键自动补齐。";
                guideActionBtn.Text = "一键补齐阀片与 H/W";
                guideAction = AutoCompleteValvesAndSensors;
            } else if (fieldMiss > 0) {
                guideBanner.Fill = Ui.WarnSoft;
                guideText.ForeColor = Ui.Warn;
                guideText.Text = "下一步建议：还有 " + fieldMiss + " 处必填字段待补充（淡黄格子），选中直接打字输入，双击「型号」从库中挑。";
                guideActionBtn.Text = "查看缺漏明细 →";
                guideAction = () => GoStep(2);
            } else if (unboundPrimary > 0 && app != null) {
                guideBanner.Fill = Ui.AccentSoft;
                guideText.ForeColor = Ui.Accent;
                guideText.Text = "小提示：有 " + unboundPrimary + " 个主器件尚未关联三维模型，去 CAD 点选绑定后可自动标位置图。";
                guideActionBtn.Text = "去 CAD 连续绑定";
                guideAction = () => StartContinuous("bind");
            } else {
                guideBanner.Fill = Ui.OkSoft;
                guideText.ForeColor = Ui.Ok;
                guideText.Text = "✓ 清单已整理齐全！选中任意格子可直接打字修改，现在可以进入下一步标位置图或直接导出。";
                guideActionBtn.Text = "去标位置图 →";
                guideAction = () => GoStep(1);
            }
            Ui.FitButton(guideActionBtn);
            var host = guideActionBtn.Parent;
            if (host != null) {
                host.BackColor = guideBanner.Fill;
                host.Width = guideActionBtn.Width + Ui.S(8);
                guideActionBtn.Location = new Point(Math.Max(0, host.Width - guideActionBtn.Width), (host.Height - guideActionBtn.Height) / 2);
            }
            guideBanner.Invalidate();
        }

        // 清单为空时的友好引导页（响应宽窄屏：宽屏 4×1 横排，窄屏自动折成 2×2 网格，绝不裁切卡片）
        Panel BuildEmptyStateOverlay() {
            var host = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Visible = false, AutoScroll = true };
            var inner = new Panel { BackColor = Color.White };
            var title = new Label {
                Text = "开始制作本装配的 Lineup 清单",
                Font = Ui.F12B, ForeColor = Ui.Text, BackColor = Color.White,
                AutoSize = false, TextAlign = ContentAlignment.MiddleCenter,
                Dock = DockStyle.Top, Height = Ui.S(34)
            };
            var flowChip = new RoundBox {
                Dock = DockStyle.Top, Height = Ui.S(34), Fill = Ui.Rail, Radius = Ui.S(8), Padding = Ui.SPad(12, 0, 12, 0)
            };
            var flowLbl = new Label {
                Text = "推荐流程：  ① 先建阀岛并加气缸/阀片（自动配 H/W）   →   ② 补充 SE 传感器与 E 电气件   →   ③ 标 CAD 位置图并一键导出",
                Font = Ui.F8, ForeColor = Ui.Text2, BackColor = Color.Transparent,
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, AutoEllipsis = true
            };
            flowChip.Controls.Add(flowLbl);

            var sub = new Label {
                Text = "先选一种最顺手的方式开始（正向先搭阀岛、或直接从三维扫描/点选均可）：",
                Font = Ui.F9, ForeColor = Ui.Muted, BackColor = Color.White,
                AutoSize = false, TextAlign = ContentAlignment.MiddleCenter,
                Dock = DockStyle.Top, Height = Ui.S(34)
            };

            var cardsRow = new FlowLayoutPanel {
                Dock = DockStyle.Top, Height = Ui.S(126), WrapContents = true, BackColor = Color.White
            };
            Func<string, string, string, bool, Action, Panel> makeCard = (head, desc, btnText, primary, act) => {
                var c = new Card { Fill = true, Width = Ui.S(212), Height = Ui.S(116), Padding = Ui.SPad(12, 12, 12, 10) };
                c.Margin = Ui.SPad(6, 4, 6, 4);
                var lh = new Label { Text = head, Font = Ui.F9B, ForeColor = primary ? Ui.Accent : Ui.Text, BackColor = Ui.Rail, Dock = DockStyle.Top, Height = Ui.S(24) };
                var ld = new Label { Text = desc, Font = Ui.F8, ForeColor = Ui.Muted, BackColor = Ui.Rail, Dock = DockStyle.Top, Height = Ui.S(38) };
                var b = primary ? Ui.Primary(btnText, 184, 32) : Ui.Secondary(btnText, 184, 32);
                b.Dock = DockStyle.Bottom;
                b.Click += (s, e) => Try(act);
                c.Controls.Add(b); c.Controls.Add(ld); c.Controls.Add(lh);
                return c;
            };

            var c1 = makeCard("① 先建阀岛 (Y1)", "手动搭气动框架，加气缸自动配阀片与 H/W", "+ 新建阀岛 (Y1)", true, () => {
                activeViewMode = 0;
                if (pneumaticPanel != null) pneumaticPanel.OpenCreateIslandDialog();
            });
            var c2 = makeCard("② 自动扫描装配", "一键识别三维里的气缸、阀岛与传感器", "扫描当前装配", false, ScanAssemblyDialog);
            var c3 = makeCard("③ 在 CAD 点选加入", "在三维里点哪个零件就录入哪个", "开始点选零件", false, () => StartContinuous("add"));
            var c4 = makeCard("④ 导入已有清单", "打开明文/加密 Excel 或粘贴表格继续改", "导入 Excel…", false, ImportXlsxDialog);
            cardsRow.Controls.AddRange(new Control[] { c1, c2, c3, c4 });

            var linkRow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = Ui.S(38), WrapContents = false, BackColor = Color.White };
            var lOr = new Label { Text = "或者直接新增：", AutoSize = true, ForeColor = Ui.Muted, Font = Ui.F8, Margin = Ui.SPad(0, 7, 2, 0) };
            var bAddSe = Ui.Small("+ 普通传感器 (SE01)");
            var bAddE = Ui.Small("+ 其他电气件 (E01)"); ((FlatButton)bAddE).SetFore(Ui.Text2);
            var bPaste = Ui.Small("从剪贴板粘贴清单"); ((FlatButton)bPaste).SetFore(Ui.Text2);
            bAddSe.Click += (s, e) => Try(() => AddRecordAndOpenMode("普通传感器", 1));
            bAddE.Click += (s, e) => Try(() => AddRecordAndOpenMode("其他电气件", 2));
            bPaste.Click += (s, e) => Try(() => ImportText(Clipboard.GetText(), '\t'));
            linkRow.Controls.AddRange(new Control[] { lOr, bAddSe, bAddE, bPaste });

            inner.Controls.Add(linkRow);
            inner.Controls.Add(cardsRow);
            inner.Controls.Add(sub);
            inner.Controls.Add(flowChip);
            inner.Controls.Add(title);
            host.Controls.Add(inner);

            host.Resize += (s, e) => {
                int availW = Math.Max(Ui.S(460), host.ClientSize.Width - Ui.S(36));
                int oneRowW = c1.Width * 4 + Ui.S(48);
                bool twoByTwo = availW < oneRowW + Ui.S(12);
                flowChip.Height = twoByTwo ? Ui.S(38) : Ui.S(34);
                int rowW = twoByTwo ? (c1.Width * 2 + Ui.S(24)) : oneRowW;
                int w = Math.Min(availW, Math.Max(rowW + Ui.S(12), twoByTwo ? Ui.S(520) : oneRowW + Ui.S(16)));
                cardsRow.WrapContents = twoByTwo;
                cardsRow.Height = twoByTwo ? (c1.Height * 2 + Ui.S(18)) : (c1.Height + Ui.S(10));
                int h = title.Height + flowChip.Height + sub.Height + cardsRow.Height + linkRow.Height + Ui.S(6);
                host.AutoScroll = h > host.ClientSize.Height;
                int top = Math.Max(Ui.S(6), (host.ClientSize.Height - h) / 2 - Ui.S(4));
                inner.SetBounds(Math.Max(Ui.S(12), (host.ClientSize.Width - w) / 2), top, w, h);
                int cPad = Math.Max(0, (w - rowW) / 2);
                cardsRow.Padding = new Padding(cPad, Ui.S(2), 0, 0);
                int linkW = lOr.PreferredSize.Width + bAddSe.Width + bAddE.Width + bPaste.Width + Ui.S(24);
                linkRow.Padding = new Padding(Math.Max(0, (w - linkW) / 2), Ui.S(4), 0, 0);
            };
            return host;
        }

        Control BuildCategoryBar() {
            catBar = new Panel { Height = Ui.S(30), BackColor = Ui.Bg };
            catFlow = new FlowLayoutPanel {
                Dock = DockStyle.Fill, WrapContents = false, AutoScroll = false, BackColor = Ui.Bg, Padding = new Padding(0)
            };
            batchEntryToggleBtn = Ui.Small("批量录入 ▾");
            ((FlatButton)batchEntryToggleBtn).SetFore(Ui.Text2);
            batchEntryToggleBtn.Dock = DockStyle.Right;
            batchEntryToggleBtn.Click += (s, e) => ToggleBatchEntry(!batchEntryOpen);
            tips.SetToolTip(batchEntryToggleBtn, "展开或收起批量录入条：需要连续录入多条同规格器件时使用");
            catBar.Controls.Add(catFlow);
            catBar.Controls.Add(batchEntryToggleBtn);
            return catBar;
        }

        void RebuildCategoryPills() {
            if (catFlow == null) return;
            // 原地复用 7 个胶囊按钮，彻底消除 Controls.Clear() 带来的 Win32 USER 句柄永久泄漏与重绘闪烁
            int[] counts = new int[7];
            counts[0] = project.Records.Count;
            for (int r = 0; r < project.Records.Count; r++) {
                string c = LineupTableData.Category(project.Records[r].Category);
                int ci = Array.IndexOf(LineupTableData.Categories, c);
                if (ci >= 0 && ci < 5) counts[ci + 1]++;
                else counts[6]++;
            }
            int maxIndex = counts[6] > 0 ? 6 : 5;
            catFlow.SuspendLayout();
            for (int i = 0; i < 7; i++) {
                var pill = catPills[i];
                if (pill == null) {
                    int idx = i;
                    pill = new FlatButton("", Color.White, Ui.Text2, Ui.Subtle, Ui.S(52), Ui.S(26));
                    pill.Margin = Ui.SPad(0, 2, 5, 0);
                    pill.Click += (s, e) => { if (category.SelectedIndex != idx) category.SelectedIndex = idx; };
                    catPills[i] = pill;
                    catFlow.Controls.Add(pill);
                }
                if (i > maxIndex) {
                    if (pill.Visible) pill.Visible = false;
                    continue;
                }
                if (!pill.Visible) pill.Visible = true;
                string name = i == 0 ? "全部" : (i <= 5 ? LineupTableData.Categories[i - 1] : "阀岛/辅助");
                int count = counts[i];
                string label = count > 0 ? name + " " + count : name;
                bool active = category.SelectedIndex == i;
                Font wantFont = active ? Ui.F9B : Ui.F8;
                if (pill.Font != wantFont) pill.Font = wantFont;
                if (pill.Text != label) pill.Text = label;
                pill.SetPalette(
                    active ? Ui.AccentSoft : Color.White,
                    active ? Ui.Accent : (count == 0 && i > 0 ? Ui.Faint : Ui.Text2),
                    active ? Ui.AccentRing : Ui.Subtle);
                int w = TextRenderer.MeasureText(label, wantFont).Width + Ui.S(18);
                if (pill.Width != w) pill.Width = w;
            }
            catFlow.ResumeLayout();
        }

        void InitPreviewPane() {
            preview = new PartPreview { App = app };
            previewSplit = new Splitter { Dock = DockStyle.Right, Width = Ui.S(6), MinExtra = Ui.S(360), MinSize = Ui.S(220), BackColor = Ui.Bg };
            int logicalW = 290;
            int parsed;
            if (int.TryParse(Choices.Get("lineup.preview.w", ""), out parsed) && parsed >= 220 && parsed <= 640) logicalW = parsed;
            preview.Dock = DockStyle.Right;
            preview.Width = Ui.S(logicalW);
            previewSplit.SplitterMoved += (s, e) => {
                if (!preview.Visible || preview.Width < Ui.S(200)) return;
                int lw = (int)Math.Round(preview.Width / Math.Max(1f, Ui.DpiScale));
                if (lw >= 220 && lw <= 640) Choices.Set("lineup.preview.w", lw.ToString(System.Globalization.CultureInfo.InvariantCulture));
                FitMainGridColumns();
            };
            workAreaHost.Controls.Add(previewSplit);
            workAreaHost.Controls.Add(preview);
            bool show = Choices.Get("lineup.preview", "0") == "1";
            preview.Visible = show;
            previewSplit.Visible = show;
            SyncPreviewButtonText();
        }

        void SyncPreviewButtonText() {
            if (previewToggleBtn != null && preview != null) {
                previewToggleBtn.Text = preview.Visible ? "隐藏 3D 预览" : "显示 3D 预览";
                Ui.FitButton(previewToggleBtn);
            }
        }

        void TogglePreview() {
            if (preview == null) return;
            bool next = !preview.Visible;
            preview.Visible = next;
            previewSplit.Visible = next;
            Choices.Set("lineup.preview", next ? "1" : "0");
            SyncPreviewButtonText();
            if (next) UpdatePreviewForCurrentRow();
            else preview.Hold(true);
        }

        void UpdatePreviewForCurrentRow() {
            if (preview == null || !preview.Visible || pickingMode) return;
            try {
                string id = CurrentId();
                var r = id == null ? null : project.Records.FirstOrDefault(x => x.Id == id);
                preview.ShowFile(r != null && !string.IsNullOrEmpty(r.SourceFile) ? r.SourceFile : "");
            } catch { }
        }

        void BuildGridContextMenu() {
            gridMenu = Ui.Menu();
        }

        internal ContextMenuStrip GridMenu { get { return gridMenu; } }

        internal void BuildGridContextMenuForRow(int rowIndex) {
            if (gridMenu == null) gridMenu = Ui.Menu();
            Ui.DisposeItems(gridMenu.Items);

            var ids = SelectedIds();
            if (ids.Count == 0 && rowIndex >= 0 && rowIndex < grid.Rows.Count && grid.Rows[rowIndex].DataBoundItem != null) {
                string rid = Convert.ToString(((DataRowView)grid.Rows[rowIndex].DataBoundItem)["Id"]);
                if (!string.IsNullOrEmpty(rid)) ids.Add(rid);
            }
            string focusId = ids.Count > 0 ? ids[0] : CurrentId();
            var focusRec = string.IsNullOrEmpty(focusId) ? null : project.Records.FirstOrDefault(x => x.Id == focusId);
            bool focusBound = focusRec != null && !string.IsNullOrEmpty(focusRec.ReferenceKey);

            Action<string, Action, string, bool> add = (t, a, tone, enabled) => {
                var mi = new ToolStripMenuItem(t);
                if (!string.IsNullOrEmpty(tone)) mi.Tag = tone;
                mi.Enabled = enabled;
                mi.Click += (s, e) => Try(a);
                gridMenu.Items.Add(mi);
            };

            if (ids.Count <= 1) {
                add("进入 3D 点选模型绑定到此行", () => StartSequentialBindRecords(ids), "accent", ids.Count == 1);
                add("绑定 CAD 当前选中件到此行", () => BindCurrentCadSelectionToRecords(ids), null, ids.Count == 1);
                add("在 CAD 中定位模型", Locate, null, focusBound);
            } else {
                add("进入 3D 依次点选绑定选中 " + ids.Count + " 行", () => StartSequentialBindRecords(ids), "accent", true);
                add("按顺序绑定 CAD 已选中件到选中 " + ids.Count + " 行", () => BindCurrentCadSelectionToRecords(ids), null, true);
                add("在 CAD 中定位当前行模型", Locate, null, focusBound);
            }

            gridMenu.Items.Add(new ToolStripSeparator());
            add("选择型号（F4）", () => PickModel(false), null, ids.Count > 0);
            add(ids.Count > 1 ? ("套用型号到选中 " + ids.Count + " 行") : "套用型号到选中多行", () => PickModel(true), null, ids.Count > 0);
            gridMenu.Items.Add(new ToolStripSeparator());
            add("一键配齐配套阀片 + H/W", AutoCompleteValvesAndSensors, null, true);
            add("批量设置备注 / 模块名…", BatchSetNotesDialog, null, ids.Count > 0);
            gridMenu.Items.Add(new ToolStripSeparator());
            add("复制选中行", DuplicateRows, null, ids.Count > 0);
            add("删除选中行", DeleteRows, "danger", ids.Count > 0);
            Ui.StyleMenu(gridMenu);
        }

        void ShowGridContextMenu(int rowIndex) {
            if (rowIndex < 0 || rowIndex >= grid.Rows.Count) return;
            BuildGridContextMenuForRow(rowIndex);
            gridMenu.Show(Cursor.Position);
        }

        void ClearSelectedCells() {
            var cells = grid.SelectedCells.Cast<DataGridViewCell>()
                .Where(c => c.RowIndex >= 0 && !c.ReadOnly && c.ColumnIndex >= 1 && c.ColumnIndex <= 11).ToList();
            if (cells.Count == 0) return;
            if (!dirty) undo.Push(project);
            foreach (var c in cells) {
                if (c.ColumnIndex == 5 || c.ColumnIndex == 6 || c.ColumnIndex == 10) modelFieldEdited = true;
                c.Value = "";
            }
            dirty = true;
            pneumaticDirty = true;
            saver.Stop();
            saver.Start();
        }

        Action ResolveLineupShortcut(Keys keyData) {
            if (keyData == (Keys.Control | Keys.F)) return () => { if (search.Visible && search.CanFocus) { search.Focus(); search.SelectAll(); } };
            if (keyData == (Keys.Control | Keys.S)) return () => { Try(SaveDraft); };
            if (activeViewMode == 0 && pneumaticPanel != null && pneumaticPanel.Visible && pneumaticPanel.ContainsFocus) {
                if (pneumaticPanel.IsEditing || ActiveControl is TextBoxBase) return null;
                if (keyData == (Keys.Control | Keys.C)) return () => pneumaticPanel.CopySelectedCells();
                if (keyData == (Keys.Control | Keys.V)) return () => pneumaticPanel.PasteSelectedCells(Clipboard.GetText());
                if (keyData == (Keys.Control | Keys.D)) return () => pneumaticPanel.FillDownSelectedCells();
                if (keyData == (Keys.Control | Keys.Z)) return () => { Try(Undo); };
                if (keyData == Keys.Delete) return () => pneumaticPanel.ClearSelectedCells();
                return null;
            }
            if (grid.IsCurrentCellInEditMode) return null;
            if (keyData == (Keys.Control | Keys.C)) return () => { Try(() => { var dataObj = grid.GetClipboardContent(); if (dataObj != null) { string t = dataObj.GetText(); if (t != null) ClipboardUtil.SetText(t); } }); };
            if (keyData == (Keys.Control | Keys.V)) return () => { Try(() => PasteCells(ClipboardUtil.GetText())); };
            if (keyData == (Keys.Control | Keys.D)) return () => { Try(FillDown); };
            if (keyData == (Keys.Control | Keys.Z)) return () => { Try(Undo); };
            if (keyData == Keys.Delete && grid.ContainsFocus) return () => { Try(ClearSelectedCells); };
            return null;
        }

        void Button(FlowLayoutPanel p, string title, Action action) {
            var b = title.StartsWith("加入", StringComparison.Ordinal) || title == "绑定并下一条"
                ? Ui.Primary(title, 60, 32)
                : Ui.Secondary(title, 60, 32);
            b.Margin = Ui.SPad(4, 1, 2, 1);
            b.Click += (s, e) => Try(action);
            p.Controls.Add(b);
        }

        void Try(Action action) {
            try { action(); }
            catch (Exception ex) {
                string m = Plain(ex);
                status.Text = m;
                if (pickingMode && pickingLabel != null) pickingLabel.Text = m;
                else ShowToast(m, Ui.Warn);
            }
        }

        static string Plain(Exception ex) {
            if (ex is TargetInvocationException && ex.InnerException != null) ex = ex.InnerException;
            if (!(ex is COMException)) return ex.Message;
            int hr = ex.HResult;
            string m = ex.Message ?? "";
            if (hr == unchecked((int)0x80010001) || hr == unchecked((int)0x8001010A) || m.Contains("拒绝")) return "天工正忙，请稍后再点一次。";
            if (hr == unchecked((int)0x80010108) || m.Contains("断开")) return "选中的模型已失效（装配刚重载过），请在装配里重新点选一次。";
            string brief = System.Text.RegularExpressions.Regex.Replace(m, @"\s*[（(]异常来自 HRESULT[^)）]*[)）]", "").Trim();
            return "天工没能完成这一步（" + (brief.Length > 0 ? brief : "0x" + hr.ToString("X8")) + "）。请再试一次。";
        }

        void EnsureFile() {
            if (assembly == null) return;
            if (!string.Equals(LineupCad.AssemblyFile(assembly), file, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("装配已另存，请重新打开 Lineup。");
        }

        void Ensure() {
            if (assembly == null || app == null) return;
            EnsureFile();
            if (!string.Equals(Convert.ToString(((dynamic)app.ActiveDocument).FullName), file, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("请先激活本清单所属装配。");
        }

        string ActiveCategory { get { return category.SelectedIndex >= 1 && category.SelectedIndex <= 5 ? Convert.ToString(category.SelectedItem) : "气缸"; } }
        public int RecordCount { get { return data.Rows.Count; } }
        public string StatusText { get { return status.Text; } }
        internal DataGridView Grid {
            get {
                // 仅被既有自动化测试直接访问底层表格时自动切到 BOM 总表视图，避免任何重叠控件冲突
                if (activeViewMode == 0 && !loading && gridCard != null && !gridCard.Visible && project != null && project.Records.Count > 0)
                    SetViewMode(3);
                return grid;
            }
        }
        internal LineupProject CurrentProject { get { SaveDraft(); return project; } }
        internal LineupPneumaticPanel PneumaticView { get { return pneumaticPanel; } }
        internal int ActiveViewMode { get { return activeViewMode; } }
        internal Button ModelLibraryButton { get { return modelLibraryBtn; } }
        internal LineupModelCatalog CatalogForTest { get { return catalog; } }
        internal void SetCategory(string value) {
            // 兼容既有测试与脚本：程序化切类目时确保切换到 BOM 总表并让底层总表可见可编辑
            if (activeViewMode != 3) activeViewMode = 3;
            category.SelectedItem = value;
            Filter();
            UpdateEmptyState();
            RebuildWorkspaceTabs();
        }

        LineupProject Snapshot(bool endEdit = true) {
            if (endEdit) {
                if (pneumaticPanel != null) pneumaticPanel.EndEdit();
                grid.EndEdit();
            }
            var p = new LineupProject {
                AssemblyFile = file,
                Sections = new List<LineupSection>(project.Sections ?? new List<LineupSection>()),
                NumberPrefix = project.NumberPrefix,
                ActiveIsland = project.ActiveIsland,
                Islands = (project.Islands ?? new List<LineupIsland>()).Select(i => i.Copy()).ToList(),
                ProjectCode = project.ProjectCode ?? "",
                StationCode = project.StationCode ?? "",
                LineCode = project.LineCode ?? "L1",
                Designer = project.Designer ?? "",
                Editor = project.Editor ?? "",
                Revision = project.Revision ?? "V1.0",
                Export11Columns = project.Export11Columns,
                MarkBomChanges = project.MarkBomChanges,
                CadPages = (project.CadPages ?? new List<LineupCadPage>()).Select(pg => pg.Copy()).ToList(),
                ChangeLogs = (project.ChangeLogs ?? new List<LineupChangeLog>()).Select(lg => lg.Copy()).ToList(),
                Baseline = project.Baseline == null ? null : project.Baseline.Copy(),
                LatestExported = project.LatestExported == null ? null : project.LatestExported.Copy()
            };
            p.EntryPresets = project.EntryPresets.Select(x => x.Copy()).ToList();
            var previous = project.Records.ToDictionary(r => r.Id);
            foreach (DataRow row in data.Rows) {
                string id = (string)row["Id"];
                LineupRecord original;
                var r = previous.TryGetValue(id, out original) ? LineupTableData.Copy(original) : new LineupRecord { Id = id };
                r.Category = (string)row["类别"];
                LineupTableData.SetCells(r, LineupTableData.Headers.Select(h => Convert.ToString(row[h])).ToArray());
                p.Records.Add(r);
            }
            return p;
        }

        void Reload(string selectedId) {
            loading = true;
            grid.SuspendLayout();
            data.BeginLoadData();
            dataRowById.Clear();
            try {
                data.Rows.Clear();
                var kinds = (DataGridViewComboBoxColumn)grid.Columns[0];
                foreach (var record in project.Records)
                    if (!kinds.Items.Contains(record.Category)) kinds.Items.Add(record.Category);
                foreach (var r in project.Records) {
                    var values = new List<object>(16) { r.Id, LineupTableData.Category(r.Category) };
                    values.AddRange(LineupTableData.Cells(r));
                    values.Add(string.Join(" / ", r.InstancePath ?? new string[0]));
                    values.Add(LineupNumbering.IslandModel(project, r));
                    values.Add(LineupHierarchy.Parent(project, r));
                    var row = data.Rows.Add(values.ToArray());
                    dataRowById[r.Id] = row;
                }
            } finally {
                data.EndLoadData();
                grid.ResumeLayout();
                loading = false;
            }
            if (activeViewMode != 0) Filter();
            UpdateEmptyState();
            RebuildWorkspaceTabs();
            RebuildCategoryPills();
            pneumaticDirty = true;
            if (pneumaticPanel != null && (activeViewMode == 0 || !pneumaticPanel.HasLoadedOnce)) {
                pneumaticPanel.RefreshFromProject(selectedId);
                pneumaticDirty = false;
            }
            if (selectedId != null && activeViewMode != 0) SelectId(selectedId);
            if (preview != null && preview.Visible) UpdatePreviewForCurrentRow();
        }

        void UpdateEmptyState() {
            if (emptyHost == null) return;
            bool isEmpty = project.Records.Count == 0 && (project.Islands == null || project.Islands.Count == 0) && string.IsNullOrEmpty(search.Text);
            lastEmptyStateKnown = true;
            lastEmptyState = isEmpty;
            if (workspaceBar != null) workspaceBar.Visible = !isEmpty;
            if (tableTipLabel != null) tableTipLabel.Visible = !isEmpty;
            if (previewToggleBtn != null && currentStep == 0) previewToggleBtn.Visible = !isEmpty;
            if (zoomBar != null && currentStep == 0) zoomBar.Visible = !isEmpty;

            if (isEmpty) {
                if (pneumaticPanel != null) pneumaticPanel.Visible = false;
                if (modeActionBar != null) modeActionBar.Visible = false;
                if (catBar != null) catBar.Visible = false;
                if (entryPanel != null) entryPanel.Visible = false;
                if (gridCard != null) { gridCard.Visible = true; gridCard.BringToFront(); }
                if (gridHost != null) gridHost.Visible = false;
                emptyHost.Visible = true;
                emptyHost.BringToFront();
            } else {
                emptyHost.Visible = false;
                bool inPneumatic = activeViewMode == 0;
                if (pneumaticPanel != null) pneumaticPanel.Visible = inPneumatic;
                if (gridCard != null) gridCard.Visible = !inPneumatic;
                if (gridHost != null) gridHost.Visible = !inPneumatic;
                grid.TabStop = !inPneumatic;
                if (modeActionBar != null) modeActionBar.Visible = !inPneumatic;
                if (sensorActionStrip != null) sensorActionStrip.Visible = activeViewMode == 1;
                if (electricalActionStrip != null) electricalActionStrip.Visible = activeViewMode == 2;
                if (topActionStrip != null) topActionStrip.Visible = activeViewMode == 3;
                if (catBar != null) catBar.Visible = activeViewMode == 3;
                if (entryPanel != null) entryPanel.Visible = !inPneumatic && batchEntryOpen;
            }
            if (footer != null) footer.PerformLayout();
            if (listStack != null) listStack.Relayout();
        }

        void Filter() {
            if (loading) return;
            string filter = "";
            if (category.SelectedIndex >= 1 && category.SelectedIndex <= 5) filter = "[类别] = '" + ActiveCategory + "'";
            else if (category.SelectedIndex == 6) filter = "[类别] NOT IN ('" + string.Join("','", LineupTableData.Categories) + "')";
            string query = search.Text.Replace("'", "''").Replace("[", "[[]").Replace("%", "[%]").Replace("*", "[*]");
            if (query.Length > 0) {
                string match = string.Join(" OR ", LineupTableData.Headers.Select(h => "[" + h + "] LIKE '%" + query + "%'"));
                filter = (filter.Length > 0 ? filter + " AND " : "") + "(" + match + ")";
            }
            data.DefaultView.RowFilter = filter;
            ApplyFieldVisibility();
            int rowH = grid.RowTemplate.Height;
            foreach (DataGridViewRow row in grid.Rows) if (row.Height != rowH) row.Height = rowH;
        }

        string CurrentId() {
            if (pickingMode) return pickingId;
            if (activeViewMode == 0 && pneumaticPanel != null && pneumaticPanel.Visible) {
                string pid = pneumaticPanel.SelectedRecordId ?? pneumaticSelectedId;
                if (!string.IsNullOrEmpty(pid) && (pneumaticPanel.ContainsFocus || grid.CurrentRow == null)) return pid;
            }
            if (grid.CurrentRow == null || grid.CurrentRow.DataBoundItem == null) return pneumaticSelectedId;
            return Convert.ToString(((DataRowView)grid.CurrentRow.DataBoundItem)["Id"]);
        }

        List<string> SelectedIds() {
            return grid.SelectedCells.Cast<DataGridViewCell>()
                .Where(c => c.RowIndex >= 0)
                .OrderBy(c => c.RowIndex)
                .Select(c => Convert.ToString(((DataRowView)grid.Rows[c.RowIndex].DataBoundItem)["Id"]))
                .Distinct().ToList();
        }

        void SelectId(string id) {
            if (pickingMode) pickingId = id;
            foreach (DataGridViewRow row in grid.Rows) {
                if (Convert.ToString(((DataRowView)row.DataBoundItem)["Id"]) == id) {
                    grid.ClearSelection();
                    grid.CurrentCell = row.Cells[1];
                    row.Cells[1].Selected = true;
                    break;
                }
            }
        }

        void Commit(LineupProject next, string selected, string message, bool learnModels = false) {
            Ensure();
            LineupTableData.RejectDuplicates(next.Records);
            var before = Snapshot();
            next.Save(store);
            undo.Push(before);
            project = next;
            dirty = false;
            modelFieldEdited = false;
            Reload(selected);
            UpdateStatus(message);
            if (learnModels) LearnAfterSave();
        }

        void CommitFileOnly(LineupProject next, string selected, string message, bool learnModels = false) {
            EnsureFile();
            LineupTableData.RejectDuplicates(next.Records);
            var before = Snapshot();
            next.Save(store);
            undo.Push(before);
            project = next;
            dirty = false;
            modelFieldEdited = false;
            Reload(selected);
            UpdateStatus(message);
            if (learnModels) LearnAfterSave();
        }

        // 位置图页点选与抬头元数据保存专用轻量通道：只落盘并刷新顶栏徽标，不重建 BOM/气动树，不读写型号库
        void CommitLightFileOnly(LineupProject next, string message) {
            EnsureFile();
            var before = project;
            next.Save(store);
            if (before != null) undo.Push(before);
            project = next;
            dirty = false;
            if (btnUndoFooter != null) btnUndoFooter.Enabled = undo.Count > 0;
            RefreshHeaderAndGuide(null);
            status.Text = message;
        }

        void UpdateStatus(string message) {
            if (pickingMode) RefreshPickingLabel();
            RefreshHeaderAndGuide(null);
            if (btnUndoFooter != null) btnUndoFooter.Enabled = undo.Count > 0;
            int total = project.Records.Count(r => BomCategories.Contains(LineupTableData.Category(r.Category)));
            int bound = project.Records.Count(r => !string.IsNullOrEmpty(r.ReferenceKey));
            int islandCnt = LineupPneumaticOps.ListIslands(project).Count;
            status.Text = (total == 0 && islandCnt == 0)
                ? "请先新建阀岛、扫描装配、CAD 点选或导入 Excel 开始录入"
                : message + " · " + (islandCnt > 0 ? islandCnt + " 阀岛 · " : "") + "BOM 共 " + total + " 项（已绑 3D " + bound + " 项）";
        }

        internal void SaveDraft() { SaveDraft(true); }

        internal void SaveDraft(bool requireActive) {
            if (requireActive) Ensure(); else EnsureFile();
            if (!dirty) { UpdateStatus("已保存"); return; }
            var next = Snapshot();
            var oldById = project.Records.ToDictionary(r => r.Id);
            var changed = new List<LineupRecord>();
            foreach (var r in next.Records) {
                LineupRecord old;
                if (!oldById.TryGetValue(r.Id, out old)) continue;
                if (string.IsNullOrWhiteSpace(r.Number) && !string.IsNullOrWhiteSpace(r.Model) && old.Model != r.Model
                    && (r.Category == "普通传感器" || r.Category == "其他电气件" || ((r.Category == "气缸" || r.Category == "阀片") && !string.IsNullOrWhiteSpace(next.NumberPrefix))))
                    changed.Add(r);
            }
            if (changed.Count > 0) LineupNumbering.FillMissing(next, changed);
            foreach (var valve in next.Records) {
                LineupRecord old;
                if (valve.Category == "阀片" && oldById.TryGetValue(valve.Id, out old) && old.Number != valve.Number)
                    LineupHierarchy.RenumberChildren(next, valve);
            }
            foreach (var cylinder in next.Records) {
                LineupRecord old;
                if (cylinder.Category == "气缸" && oldById.TryGetValue(cylinder.Id, out old) && old.Number != cylinder.Number)
                    foreach (var sensor in next.Records.Where(r => r.CylinderId == cylinder.Id))
                        sensor.Number = cylinder.Number + sensor.Position;
            }
            LineupTableData.RejectDuplicates(next.Records);
            next.Save(store);
            project = next;
            dirty = false;
            if (activeViewMode != 0) pneumaticDirty = true;
            var newById = project.Records.ToDictionary(r => r.Id);
            // 只回写实际发生变化的衍生单元格，避免触发整表刷新闪烁
            foreach (DataRow row in data.Rows) {
                LineupRecord record;
                if (!newById.TryGetValue((string)row["Id"], out record)) continue;
                string num = record.Number ?? "";
                string isl = LineupNumbering.IslandModel(project, record) ?? "";
                string par = LineupHierarchy.Parent(project, record) ?? "";
                if (!string.Equals(Convert.ToString(row["编号"]), num, StringComparison.Ordinal)) row["编号"] = num;
                if (!string.Equals(Convert.ToString(row["阀岛型号"]), isl, StringComparison.Ordinal)) row["阀岛型号"] = isl;
                if (!string.Equals(Convert.ToString(row["上级"]), par, StringComparison.Ordinal)) row["上级"] = par;
            }
            UpdateStatus("已自动保存");
            if (modelFieldEdited) {
                modelFieldEdited = false;
                LearnAfterSave();
            }
        }

        internal void ImportText(string text, char separator) {
            Ensure();
            var batch = LineupTableData.Parse(text, ActiveCategory, separator);
            if (batch.Records.Count == 0) throw new InvalidOperationException("没有找到可导入的数据行。");
            var next = Snapshot();
            next.Records.AddRange(batch.Records);
            foreach (var section in batch.Sections) { next.Sections.RemoveAll(s => s.Category == section.Category); next.Sections.Add(section); }
            LineupTableData.InferHierarchy(next);
            Commit(next, null, "已导入 " + batch.Records.Count + " 条", true);
            category.SelectedIndex = 0;
        }

        internal void ImportXlsx(string xlsxPath) {
            Ensure();
            var batch = LineupXlsxReader.Read(xlsxPath);
            var next = Snapshot();
            bool wasEmpty = next.Records.Count == 0;
            next.Records.AddRange(batch.Records);
            foreach (var section in batch.Sections) { next.Sections.RemoveAll(s => s.Category == section.Category); next.Sections.Add(section); }
            if (batch.Islands != null) foreach (var isl in batch.Islands) {
                var old = next.Islands.FirstOrDefault(x => string.Equals(x.Key, isl.Key, StringComparison.OrdinalIgnoreCase));
                if (old == null) next.Islands.Add(isl.Copy());
                else if (string.IsNullOrWhiteSpace(old.Model)) { old.Model = isl.Model; old.Kind = isl.Kind; }
            }
            if (string.IsNullOrWhiteSpace(next.ProjectCode) && !string.IsNullOrWhiteSpace(batch.ProjectCode)) next.ProjectCode = batch.ProjectCode;
            if (string.IsNullOrWhiteSpace(next.StationCode) && !string.IsNullOrWhiteSpace(batch.StationCode)) next.StationCode = batch.StationCode;
            if (!string.IsNullOrWhiteSpace(batch.LineCode)) next.LineCode = batch.LineCode;

            // 合并导入的历史更新日志（保留历次版本记录，自动去重）
            int importedLogs = 0;
            if (batch.ChangeLogs != null && batch.ChangeLogs.Count > 0) {
                if (next.ChangeLogs == null) next.ChangeLogs = new List<LineupChangeLog>();
                foreach (var lg in batch.ChangeLogs) {
                    bool dup = next.ChangeLogs.Any(x =>
                        string.Equals((x.Version ?? "").Trim(), (lg.Version ?? "").Trim(), StringComparison.OrdinalIgnoreCase)
                        && string.Equals((x.Date ?? "").Trim(), (lg.Date ?? "").Trim(), StringComparison.OrdinalIgnoreCase)
                        && string.Equals((x.Content ?? "").Trim(), (lg.Content ?? "").Trim(), StringComparison.Ordinal));
                    if (!dup) { next.ChangeLogs.Add(lg.Copy()); importedLogs++; }
                }
            }
            if (!string.IsNullOrWhiteSpace(batch.LatestRevision)) next.Revision = batch.LatestRevision.Trim();
            if (string.IsNullOrWhiteSpace(next.Designer) && !string.IsNullOrWhiteSpace(batch.LatestDesigner)) next.Designer = batch.LatestDesigner.Trim();
            if (string.IsNullOrWhiteSpace(next.Editor) && !string.IsNullOrWhiteSpace(batch.LatestEditor)) next.Editor = batch.LatestEditor.Trim();

            LineupTableData.InferHierarchy(next);
            // 空项目首次导入已有 Excel 或尚未建立基准时，自动将导入版本设为对比基准（后续修改即可自动草拟日志并标红）
            bool capturedBase = false;
            if ((wasEmpty || next.Baseline == null || next.Baseline.Records == null || next.Baseline.Records.Count == 0) && batch.Records.Count > 0) {
                next.Baseline = LineupRevision.CaptureSnapshot(next, LineupRevision.CurrentVersion(next));
                next.LatestExported = next.Baseline.Copy();
                capturedBase = true;
            }
            string status = "已从 Excel 导入 " + batch.Records.Count + " 条记录"
                + (importedLogs > 0 ? "、" + importedLogs + " 条更新日志" : "")
                + (capturedBase ? "（已设为对比基准 " + next.Baseline.Version + "）" : "");
            Commit(next, null, status, true);
            if (entryPrefix != null && !string.IsNullOrWhiteSpace(project.NumberPrefix)) {
                entryPrefix.Text = project.NumberPrefix;
                entryIsland.Value = Math.Min(9999, Math.Max(1, project.ActiveIsland));
            }
            category.SelectedIndex = 0;
        }

        internal LineupXlsxSummary ExportXlsx(string path, LineupXlsxOptions opts = null) {
            SaveDraft(false);
            var effective = opts ?? new LineupXlsxOptions {
                Export11Columns = project.Export11Columns,
                IncludeCadPages = true,
                MarkBomChanges = project.MarkBomChanges
            };
            var sum = LineupXlsxWriter.Write(path, project, effective);

            // 导出成功后自动记录导出快照（首次导出顺带初始化第一条更新日志与基准快照，便于下一版直接追踪差异）
            string ver = LineupRevision.CurrentVersion(project);
            if (project.ChangeLogs == null) project.ChangeLogs = new List<LineupChangeLog>();
            if (project.ChangeLogs.Count == 0) {
                string today = DateTime.Today.ToString("yyyy/M/d", System.Globalization.CultureInfo.InvariantCulture);
                string mDate = today;
                try { if (!string.IsNullOrEmpty(project.AssemblyFile) && File.Exists(project.AssemblyFile)) mDate = File.GetLastWriteTime(project.AssemblyFile).ToString("yyyy/M/d", System.Globalization.CultureInfo.InvariantCulture); } catch { }
                project.ChangeLogs.Add(new LineupChangeLog {
                    Date = today,
                    Version = ver,
                    ModelDate = mDate,
                    Content = !string.IsNullOrWhiteSpace(effective.VersionNote)
                        ? effective.VersionNote.Trim()
                        : ((project.CadPages != null && project.CadPages.Count > 0) ? "导出 LineUP第二版_BOM 及 LineUP第三版_CAD（共 " + project.CadPages.Count + " 页位置图）" : "导出 LineUP第二版_BOM"),
                    Notes = "由天工工具箱自动导出",
                    Designer = project.Designer ?? "",
                    Editor = project.Editor ?? ""
                });
            }
            project.LatestExported = LineupRevision.CaptureSnapshot(project, ver);
            if (project.Baseline == null || project.Baseline.Records == null || project.Baseline.Records.Count == 0) {
                project.Baseline = project.LatestExported.Copy();
            }
            try { if (!string.IsNullOrEmpty(store)) project.Save(store); } catch { }
            if (exportPanel != null) exportPanel.RefreshFromProject();
            return sum;
        }

        internal void AddEmpty() {
            if (ActiveCategory == "气缸传感器") { GenerateSensorPairs(); return; }
            if (!ReadyNumbering(ActiveCategory)) return;
            var next = Snapshot();
            var r = new LineupRecord { Category = ActiveCategory, Number = LineupNumbering.Next(next, ActiveCategory) };
            next.Records.Add(r);
            Commit(next, r.Id, "已新增 " + r.Number);
        }

        internal int AddSelectedModels() {
            Ensure();
            if (assembly == null) throw new InvalidOperationException("当前未连接天工 CAD 装配。");
            if (ActiveCategory == "气缸传感器") throw new InvalidOperationException("请先从气缸一键生成 H/W，再按行绑定传感器模型。");
            if (!ReadyNumbering(ActiveCategory)) return 0;
            if (assembly.SelectSet.Count == 0) throw new InvalidOperationException("请先在 CAD 中选择至少一个零件或子装配。");
            var incoming = new List<LineupRecord>();
            for (int i = 1; i <= assembly.SelectSet.Count; i++) {
                var r = new LineupRecord { Category = ActiveCategory };
                LineupCad.CaptureOccurrence(assembly.SelectSet.Item(i), r);
                try {
                    var meta = LineupCad.ReadPartMeta(r.SourceFile, r.InstancePath, catalog, ActiveCategory);
                    if (!string.IsNullOrWhiteSpace(meta.Model)) r.Model = meta.Model;
                    if (!string.IsNullOrWhiteSpace(meta.Brand)) r.Brand = meta.Brand;
                    if (!string.IsNullOrWhiteSpace(meta.Connector)) r.Connector = meta.Connector;
                    if (!string.IsNullOrWhiteSpace(meta.Title)) r.Description = meta.Title;
                    if (!string.IsNullOrWhiteSpace(meta.Mechanism)) { r.Mechanism = meta.Mechanism; r.Notes = meta.Mechanism; }
                } catch { }
                incoming.Add(r);
            }
            var next = Snapshot();
            var keys = new HashSet<string>(next.Records.Where(r => !string.IsNullOrEmpty(r.ReferenceKey)).Select(r => r.ReferenceKey));
            int added = 0;
            string last = CurrentId();
            foreach (var r in incoming) {
                if (keys.Add(r.ReferenceKey)) {
                    r.Number = LineupNumbering.Next(next, r.Category);
                    next.Records.Add(r);
                    last = r.Id;
                    added++;
                }
            }
            if (added > 0) Commit(next, last, "已加入 " + added + " 个三维零件并自动编号", true);
            else UpdateStatus("所选零件均已录入，未重复添加");
            return added;
        }

        void DuplicateRows() {
            var ids = SelectedIds();
            if (ids.Count == 0) throw new InvalidOperationException("请先选中要复制的行。");
            var next = Snapshot();
            string last = null;
            foreach (var source in next.Records.Where(r => ids.Contains(r.Id)).ToArray()) {
                var r = LineupTableData.Copy(source);
                r.Id = Guid.NewGuid().ToString("N");
                r.Number = ""; r.Address = ""; r.ReferenceKey = ""; r.SourceFile = ""; r.InstancePath = new string[0];
                if (BomCategories.Contains(r.Category) && r.Category != "气缸传感器")
                    r.Number = LineupNumbering.Next(next, r.Category);
                next.Records.Add(r);
                last = r.Id;
            }
            Commit(next, last, "已复制 " + ids.Count + " 行");
        }

        internal void DeleteRows() {
            var ids = SelectedIds();
            if (ids.Count == 0) throw new InvalidOperationException("请先选中要删除的行。");
            var next = Snapshot();
            if (next.Records.Any(r => !ids.Contains(r.Id) && (ids.Contains(r.IslandId) || ids.Contains(r.ValveId) || ids.Contains(r.CylinderId))))
                throw new InvalidOperationException("选中行仍被下级器件引用，请先删除或解除下级关系。");
            var removedIds = new HashSet<string>(ids, StringComparer.Ordinal);
            var removed = next.Records.Where(r => removedIds.Contains(r.Id)).ToList();
            next.Records.RemoveAll(r => removedIds.Contains(r.Id));
            int renumbered = LineupNumbering.CompactAfterDelete(next, removed, removedIds);
            Commit(next, null, "已删除 " + ids.Count + " 行" + (renumbered > 0 ? "，剩余编号已自动顺延重排" : ""));
        }

        internal void FillDown() {
            var cells = grid.SelectedCells.Cast<DataGridViewCell>().Where(c => !c.ReadOnly).ToArray();
            if (cells.Length < 2) throw new InvalidOperationException("请先在同一列选中多行单元格，再向下填充。");
            if (cells.Any(c => c.ColumnIndex == 1 || c.ColumnIndex == 2))
                throw new InvalidOperationException("向下填充不包含编号和地址，请只选需要共用的列。");
            var before = Snapshot();
            foreach (var group in cells.GroupBy(c => c.ColumnIndex)) {
                var ordered = group.OrderBy(c => c.RowIndex).ToArray();
                if (group.Key == 5 || group.Key == 6 || group.Key == 10) modelFieldEdited = true;
                foreach (var cell in ordered.Skip(1)) cell.Value = ordered[0].Value;
            }
            dirty = true;
            try { SaveDraft(); undo.Push(before); }
            catch { project = before; Reload(null); dirty = false; throw; }
        }

        internal void PasteCells(string text) {
            if (grid.CurrentCell == null) throw new InvalidOperationException("先点击要粘贴的起始单元格。");
            var block = LineupTableData.ReadDelimited(text, '\t');
            int top = grid.CurrentCell.RowIndex;
            if (block.Count == 0) return;
            var cols = grid.Columns.Cast<DataGridViewColumn>().Where(c => c.Visible).OrderBy(c => c.DisplayIndex).ToList();
            int start = cols.IndexOf(grid.CurrentCell.OwningColumn);
            Func<DataGridViewColumn, bool> business = c => c.Index >= 1 && c.Index <= 11;
            var targets = grid.SelectedCells.Cast<DataGridViewCell>().Where(c => c.RowIndex >= 0 && business(c.OwningColumn)).ToList();
            bool fill = block.Count == 1 && block[0].Length == 1 && targets.Count > 1;
            if (!fill && (top + block.Count > grid.Rows.Count || start < 0 || block.Any(r => start + r.Length > cols.Count || cols.Skip(start).Take(r.Length).Any(c => !business(c)))))
                throw new InvalidOperationException("粘贴区域超出已有单元格。新增整表请点「+ 添加器件 ▾ → 从剪贴板粘贴整张清单」。");
            var before = Snapshot();
            if (fill) {
                foreach (var cell in targets) {
                    if (cell.ColumnIndex == 5 || cell.ColumnIndex == 6 || cell.ColumnIndex == 10) modelFieldEdited = true;
                    cell.Value = block[0][0];
                }
            } else {
                for (int y = 0; y < block.Count; y++)
                    for (int x = 0; x < block[y].Length; x++) {
                        int colIdx = cols[start + x].Index;
                        if (colIdx == 5 || colIdx == 6 || colIdx == 10) modelFieldEdited = true;
                        grid.Rows[top + y].Cells[colIdx].Value = block[y][x];
                    }
            }
            dirty = true;
            try { SaveDraft(); undo.Push(before); }
            catch { project = before; Reload(null); dirty = false; throw; }
        }

        internal void BindAndNext() {
            Ensure();
            if (assembly == null) throw new InvalidOperationException("当前未连接天工 CAD 装配。");
            string id = CurrentId();
            if (id == null) throw new InvalidOperationException("先选择清单中的目标行。");
            var next = Snapshot();
            var r = next.Records.Single(x => x.Id == id);
            var binding = new LineupRecord();
            LineupCad.Capture(assembly, binding);
            var conflict = next.Records.FirstOrDefault(x => x.Id != id && x.ReferenceKey == binding.ReferenceKey);
            if (conflict != null) throw new InvalidOperationException("当前待绑定：" + r.Number + "。所选模型已属于 " + conflict.Number + "，请选择下一个模型；本行未修改。");
            r.ReferenceKey = binding.ReferenceKey;
            r.SourceFile = binding.SourceFile;
            r.InstancePath = binding.InstancePath;
            var visible = grid.Rows.Cast<DataGridViewRow>().Select(row => Convert.ToString(((DataRowView)row.DataBoundItem)["Id"])).ToList();
            int index = visible.IndexOf(id);
            string following = index + 1 < visible.Count ? visible[index + 1] : id;
            Commit(next, following, index + 1 < visible.Count ? "已绑定 " + r.Number + "，已移至下一行" : "已绑定 " + r.Number + "；已到最后一行");
            assembly.SelectSet.RemoveAll();
        }

        List<string[]> ExportRows() {
            SaveDraft(false);
            int empty = project.Records.Count(r => LineupTableData.Cells(r).All(string.IsNullOrWhiteSpace));
            if (empty > 0) throw new InvalidDataException("有 " + empty + " 行尚未填写任何字段，请先填写或删除空行再导出。");
            return LineupTableData.Grouped(project);
        }

        internal void Export(string path) {
            SettingsFile.WriteUserText(path, LineupCsv.Write(ExportRows()));
            UpdateStatus("已导出 CSV 清单");
        }

        internal void Undo() {
            StopContinuous();
            if (undo.Count == 0) return;
            Ensure();
            var previous = undo.Peek();
            LineupTableData.RejectDuplicates(previous.Records);
            previous.Save(store);
            undo.Pop();
            project = previous;
            dirty = false;
            modelFieldEdited = false;
            Reload(null);
            RefreshEntryParents();
            if (currentStep == 1 && cadStudio != null) cadStudio.SyncFromProject(true);
            else if (currentStep == 2 && exportPanel != null) exportPanel.RefreshFromProject();
            UpdateStatus("已撤销上一步操作");
        }

        internal void Locate() {
            Ensure();
            var r = Snapshot().Records.FirstOrDefault(record => record.Id == CurrentId());
            if (r == null) throw new InvalidOperationException("请先选择一行记录。");
            LocateRecord(r);
        }

        internal void LocateRecord(LineupRecord r) {
            Ensure();
            if (r == null || assembly == null || app == null) return;
            ClearHighlight();
            object resolved = LineupCad.ResolveTarget(assembly, r);
            dynamic target = resolved;
            highlight = assembly.HighlightSets.Add();
            highlight.Color = 0x00FFFF;
            highlight.AddItem(LineupCad.HighlightTarget(resolved));
            highlight.Draw();
            double x, y, z, u, v, w;
            target.Range(out x, out y, out z, out u, out v, out w);
            double p = Math.Max(Math.Max(u - x, v - y), w - z) * .15 + .001;
            dynamic view = ((dynamic)app.ActiveWindow).View;
            view.RangeZoomCamera(x - p, y - p, z - p, u + p, v + p, w + p);
            view.Update();
            status.Text = "已在 CAD 中定位：" + target.Name;
        }

        void ClearHighlight() {
            object previous = highlight;
            highlight = null;
            if (previous != null) { try { ((dynamic)previous).Delete(); } catch { } }
        }
    }
}
