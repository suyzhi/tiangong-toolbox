using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace TianGongCadSuite {
    // 方案 A 核心工作区：「气动系统（阀岛 → 阀片 → 气缸 → H/W 磁开）」联动面板
    //   · 左侧：显式阀岛/汇流排/阀组/单片阀列表 + 醒目「+ 新建阀岛」按钮 + 编辑/绑3D/删除；
    //   · 右侧：当前阀岛的「按阀路对照 / 气缸明细 / 阀片明细 / 气缸传感器」四视图切换 + 一键加气缸(自动配阀+H/W) / 加阀片与预留盖板 / 一阀控多缸 / 统一套用磁开型号；
    //   · 交互零妥协：双缓冲、单击直接输入（无需双击）、Ctrl+滚轮缩放与主表同步、Shift+滚轮横移、右键直达全操作。
    public sealed class LineupPneumaticPanel : Panel {
        static readonly Color WarmTintBg = Color.FromArgb(0xFF, 0xFD, 0xF2);
        static readonly Color SpareRowBg = Color.FromArgb(0xF8, 0xF9, 0xFA);
        static readonly Color ContRowBg = Color.FromArgb(0xF7, 0xFA, 0xFF);
        static readonly Color WarnText = Color.FromArgb(0xB4, 0x53, 0x09);

        // 供父窗口注入的数据与动作委托（保持松耦合、全操作统一走父窗口 Snapshot/Commit/Undo 通道）
        public Func<LineupProject> GetProject;
        public Func<LineupModelCatalog> GetCatalog;
        public Func<bool> HasCad;
        public Action<Action<LineupProject>, string, string> RunProjectMutation; // (mutate(next), focusRecordId, message)
        public Action<string, string, string, bool> ApplyInlineField;           // (recordId, fieldName, newValue, triggerModelLookup)
        public Action<string> OnSelectionChanged;
        public Action<int> OnZoomDelta;
        public Action<string> OnLocateRecord;
        public Action<string> OnBindCurrentCadToRecord;
        public Action<IList<string>> OnBindCurrentCadToRecords;
        public Action<IList<string>> OnStartSequentialBindRecords;
        public Action<string> OnBindCurrentCadToIsland;
        public Action<string> OnStartBindIslandIn3D;
        public Action<string> OnAddCadSelectedAsCylinder;
        public Action<string> OnStartContinuousAddCylinder;
        public Action<string> OnStartContinuousBindIsland;
        public Action OnScanAssembly;
        public Action OnImportXlsx;
        public Action<string, IEnumerable<string>> OnPickModelForRecords; // (category, recordIds)
        public Action<string> OnNotifyStatus;
        public Action<int> OnMoveSelectedRows; // direction: -1 上移, +1 下移

        // 左侧阀岛栏
        readonly Card sidebarCard;
        Splitter sidebarSplit;
        bool userDraggedSidebar;
        readonly ListBox islandList = new ListBox();
        readonly Button btnNewIsland;
        readonly Button btnEditIsland;
        readonly Button btnBindIsland3D;
        readonly Button btnDeleteIsland;
        readonly ContextMenuStrip islandMenu = Ui.Menu();

        // 右侧主工作区
        readonly Panel rightHost = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Bg };
        readonly Card rightCard;
        readonly Panel noIslandHost = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Visible = false };
        readonly Panel orphanHost = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Visible = false };
        readonly Panel islandWorkHost = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Visible = false };

        // 选中阀岛头部与操作条
        Panel topHeaderBar;
        FlowLayoutPanel headerRightBtns;
        readonly Label islandTitleLbl = new Label();
        readonly Label islandKindBadge = new Label();
        readonly Label islandModelLbl = new Label();
        readonly Button btnAddCyl;
        readonly Button btnAddValve;
        readonly Button btnCadOps;
        readonly ContextMenuStrip addValveMenu = Ui.Menu();
        readonly ContextMenuStrip cadOpsMenu = Ui.Menu();
        bool subTabsCompact;

        // 子视图切换栏（按阀路对照 / 气缸明细 / 阀片明细 / 气缸传感器）
        readonly Panel subTabBar = new Panel();
        readonly FlowLayoutPanel subTabFlow = new FlowLayoutPanel();
        readonly FlatButton[] subTabPills = new FlatButton[4];
        readonly Button btnSubQuickAction;
        readonly Panel gridWrap = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
        readonly Panel emptyIslandHost = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Visible = false };
        readonly Label emptyIslandTitle = new Label();

        // 核心表格与孤立件表格
        readonly SpreadsheetGrid pGrid = new SpreadsheetGrid();
        readonly DataGridView orphanGrid = new DataGridView();
        readonly Label orphanTitleLbl = new Label();
        readonly ContextMenuStrip rowMenu = Ui.Menu();
        readonly ToolTip tips = new ToolTip { AutoPopDelay = 12000, InitialDelay = 250 };

        // 状态
        List<LineupIslandSummary> islands = new List<LineupIslandSummary>();
        List<LineupRecord> orphans = new List<LineupRecord>();
        List<LineupChannelRow> currentChannels = new List<LineupChannelRow>();
        List<GridRowMeta> rowMetas = new List<GridRowMeta>();
        readonly Dictionary<string, int> baseWidths = new Dictionary<string, int>(StringComparer.Ordinal);
        string selectedIslandKey = "";
        bool viewingOrphans;
        int activeSubTab; // 0=按阀路对照, 1=气缸明细, 2=阀片明细, 3=气缸传感器
        string searchFilter = "";
        int zoomPercent = 100;
        bool loadingGrid, applyingZoom, syncingIslandList, hasLoadedOnce;
        int builtSubTab = -1;
        int lastFitGridW = -1, lastFitZoom = -1, lastFitSubTab = -1;
        string editStartVal = "";
        Font cellFont, cellBoldFont, headerFont;
        readonly Dictionary<string, AutoCompleteStringCollection> acCache = new Dictionary<string, AutoCompleteStringCollection>(StringComparer.Ordinal);
        string lastAcKey = "";

        sealed class IslandListItem {
            public LineupIslandSummary Island;
            public bool IsOrphanBucket;
            public int OrphanCount;
        }

        sealed class GridRowMeta {
            public LineupChannelRow Channel;
            public LineupRecord Primary;
            public string ValveId = "";
            public string CylinderId = "";
            public string SensorId = "";
            public bool IsContinuation;
            public bool IsSpare;
            public bool IsBlowOrVacuum;
        }

        public LineupPneumaticPanel() {
            Dock = DockStyle.Fill;
            BackColor = Ui.Bg;
            Font = Ui.F9;

            int savedSideW;
            int initSideW = 256;
            if (int.TryParse(Choices.Get("lineup.sidebar.w", ""), out savedSideW) && savedSideW >= 196 && savedSideW <= 380) {
                initSideW = savedSideW;
                userDraggedSidebar = true;
            }
            sidebarCard = new Card { Fill = true, Dock = DockStyle.Left, Width = Ui.S(initSideW), Padding = Ui.SPad(10, 8, 10, 8) };
            rightCard = new Card { Fill = false, Dock = DockStyle.Fill, Padding = Ui.SPad(10, 8, 10, 6) };

            btnNewIsland = Ui.Primary("+ 新建阀岛", 96, 28);
            btnNewIsland.Font = Ui.F8;
            btnEditIsland = Ui.Small("编辑阀岛"); ((FlatButton)btnEditIsland).SetFore(Ui.Text2);
            btnBindIsland3D = Ui.Small("绑 3D"); ((FlatButton)btnBindIsland3D).SetFore(Ui.Text2);
            btnDeleteIsland = Ui.Small("删除"); ((FlatButton)btnDeleteIsland).SetFore(Ui.Bad);

            btnAddCyl = Ui.Primary("+ 添加气缸", 104, 30);
            btnAddCyl.Font = Ui.F8;
            btnAddValve = Ui.Secondary("+ 添加阀片 ▾", 106, 30);
            btnAddValve.Font = Ui.F8;
            btnCadOps = Ui.Secondary("CAD 识件 ▾", 96, 30);
            btnCadOps.Font = Ui.F8;

            btnSubQuickAction = Ui.Small("+ 一阀控多缸");
            btnSubQuickAction.Font = Ui.F8;

            BuildSidebar();
            BuildRightArea();
            BuildMenus();
            SetupPGrid();
            SetupOrphanGrid();

            sidebarSplit = new Splitter { Dock = DockStyle.Left, Width = Ui.S(8), MinSize = Ui.S(196), MinExtra = Ui.S(460), BackColor = Ui.Bg };
            sidebarSplit.SplitterMoved += (s, e) => {
                userDraggedSidebar = true;
                int lw = (int)Math.Round(sidebarCard.Width / Math.Max(1f, Ui.DpiScale));
                if (lw >= 196 && lw <= 380) Choices.Set("lineup.sidebar.w", lw.ToString(System.Globalization.CultureInfo.InvariantCulture));
                UpdateSidebarFooterCompact();
                FitPGridColumns();
            };

            rightHost.Controls.Add(rightCard);
            Controls.Add(rightHost);
            Controls.Add(sidebarSplit);
            Controls.Add(sidebarCard);

            Ui.EnableDoubleBuffer(this);
            Ui.EnableDoubleBuffer(rightHost);
            Ui.EnableDoubleBuffer(islandWorkHost);
            Ui.EnableDoubleBuffer(gridWrap);
            Ui.EnableDoubleBuffer(islandList);
            Ui.EnableDoubleBuffer(subTabFlow);

            Resize += (s, e) => AdaptToWidth();
            ApplyZoom(100);
        }

        void AdaptToWidth() {
            if (ClientSize.Width < Ui.S(300)) return;
            // 窄屏自动收紧左侧阀岛栏、宽屏适度舒展；若用户手动拖过分隔条则尊重其宽度（仅防越界）
            int maxSide = Math.Max(Ui.S(204), ClientSize.Width - Ui.S(480));
            if (!userDraggedSidebar) {
                int target = ClientSize.Width < Ui.S(1020) ? Ui.S(224)
                    : ClientSize.Width > Ui.S(1600) ? Ui.S(284)
                    : Ui.S(256);
                target = Math.Min(target, maxSide);
                if (Math.Abs(sidebarCard.Width - target) > Ui.S(4)) sidebarCard.Width = target;
            } else if (sidebarCard.Width > maxSide) {
                sidebarCard.Width = maxSide;
            }
            UpdateSidebarFooterCompact();
            LayoutIslandHeaderModel();
            CheckSubTabCompactness();
            FitPGridColumns();
        }

        static void FitButtonExact(Button b, int minLogicalW) {
            if (b == null) return;
            int need = TextRenderer.MeasureText(b.Text ?? "", b.Font ?? Ui.F9).Width + Ui.S(20);
            int target = Math.Max(Ui.S(minLogicalW), need);
            if (b.Width != target) b.Width = target;
        }

        void UpdateSidebarFooterCompact() {
            if (sidebarCard == null) return;
            bool compact = sidebarCard.Width < Ui.S(236);
            var cur = islands.FirstOrDefault(x => string.Equals(x.Key, selectedIslandKey, StringComparison.OrdinalIgnoreCase));
            btnEditIsland.Text = compact ? "编辑" : "编辑阀岛";
            btnBindIsland3D.Text = (cur != null && cur.Has3DBind) ? (compact ? "定位" : "定位 3D") : "绑 3D";
            // 按当前文本实测重设宽度（允许回缩），避免窄侧栏下旧宽按钮把「删除」挤出右缘
            FitButtonExact(btnEditIsland, 46);
            FitButtonExact(btnBindIsland3D, 46);
            FitButtonExact(btnDeleteIsland, 46);
        }

        protected override void Dispose(bool disposing) {
            if (disposing) {
                DisposeFonts();
                islandMenu.Dispose();
                addValveMenu.Dispose();
                cadOpsMenu.Dispose();
                rowMenu.Dispose();
                tips.Dispose();
            }
            base.Dispose(disposing);
        }

        void DisposeFonts() {
            if (cellFont != null) { cellFont.Dispose(); cellFont = null; }
            if (cellBoldFont != null) { cellBoldFont.Dispose(); cellBoldFont = null; }
            if (headerFont != null) { headerFont.Dispose(); headerFont = null; }
        }

        public string SelectedIslandKey { get { return selectedIslandKey; } }
        public int ActiveSubTab { get { return activeSubTab; } }
        public bool HasLoadedOnce { get { return hasLoadedOnce; } }
        public bool IsEditing { get { return pGrid.IsCurrentCellInEditMode || orphanGrid.IsCurrentCellInEditMode; } }
        internal DataGridView Grid { get { return pGrid; } }
        internal ContextMenuStrip RowMenu { get { return rowMenu; } }
        internal Button AddValveButton { get { return btnAddValve; } }
        internal ContextMenuStrip AddValveMenu { get { return addValveMenu; } }
        internal Button CadOpsButton { get { return btnCadOps; } }
        internal ContextMenuStrip CadOpsMenu { get { return cadOpsMenu; } }
        public void InvalidateAutoCompleteCache() { acCache.Clear(); lastAcKey = ""; }
        public void FocusRecord(string recordId) { if (!string.IsNullOrEmpty(recordId)) FocusRecordInPGrid(recordId); }

        public string SelectedRecordId {
            get {
                if (viewingOrphans) {
                    if (orphanGrid.CurrentRow == null) return null;
                    string tagId = orphanGrid.CurrentRow.Tag as string;
                    if (!string.IsNullOrEmpty(tagId)) return tagId;
                    int idx = orphanGrid.CurrentRow.Index;
                    return idx >= 0 && idx < orphans.Count ? orphans[idx].Id : null;
                }
                var meta = CurrentRowMeta();
                if (meta == null) return null;
                if (activeSubTab == 2 && !string.IsNullOrEmpty(meta.ValveId)) return meta.ValveId;
                if (activeSubTab == 3 && !string.IsNullOrEmpty(meta.SensorId)) return meta.SensorId;
                return !string.IsNullOrEmpty(meta.CylinderId) ? meta.CylinderId : meta.ValveId;
            }
        }

        public void SetSearchFilter(string query) {
            string q = (query ?? "").Trim();
            if (string.Equals(searchFilter, q, StringComparison.Ordinal)) return;
            searchFilter = q;
            RebuildActiveGrid(null);
        }

        public void SelectSubTab(int index) {
            if (index < 0 || index > 3) return;
            activeSubTab = index;
            Ui.BeginRedrawFreeze(islandWorkHost);
            try {
                RebuildSubTabs();
                RebuildActiveGrid(null);
            } finally {
                Ui.EndRedrawFreeze(islandWorkHost);
            }
        }

        public void SelectIsland(string islandKey) {
            if (string.IsNullOrWhiteSpace(islandKey)) return;
            string key = islandKey.Trim();
            selectedIslandKey = key;
            viewingOrphans = false;
            if (hasLoadedOnce) {
                int idx = islands.FindIndex(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0 && idx < islandList.Items.Count) {
                    syncingIslandList = true;
                    try {
                        if (islandList.SelectedIndex != idx) islandList.SelectedIndex = idx;
                    } finally { syncingIslandList = false; }
                    UpdateSidebarFooterCompact();
                    RefreshRightView(null);
                    return;
                }
            }
            RefreshFromProject(null);
        }

        // 与主表格缩放完全同步
        public void ApplyZoom(int percent) {
            int clamped = Math.Max(70, Math.Min(160, percent));
            if (clamped == zoomPercent && cellFont != null) return;
            zoomPercent = clamped;
            float scale = zoomPercent / 100f;
            var oldF = cellFont; var oldB = cellBoldFont; var oldH = headerFont;
            cellFont = new Font(Ui.Family, Math.Max(7f, 9.25f * scale), FontStyle.Regular);
            cellBoldFont = new Font(Ui.Family, Math.Max(7f, 9.25f * scale), FontStyle.Bold);
            headerFont = new Font(Ui.Family, Math.Max(7f, 8.5f * scale), FontStyle.Regular);

            ApplyZoomToGrid(pGrid, scale, false);
            ApplyZoomToGrid(orphanGrid, scale, true);
            // 左侧阀岛卡片高度随缩放档位柔和微调（85%~125%），放大看表时侧栏卡片也更舒展
            float sideScale = Math.Max(0.90f, Math.Min(1.22f, 0.5f + 0.5f * scale));
            int wantItemH = Math.Max(Ui.S(58), (int)Math.Round(Ui.S(64) * sideScale));
            if (islandList.ItemHeight != wantItemH) { islandList.ItemHeight = wantItemH; islandList.Invalidate(); }
            FitPGridColumns();

            if (oldF != null) oldF.Dispose();
            if (oldB != null) oldB.Dispose();
            if (oldH != null) oldH.Dispose();
        }

        // 智能满屏列宽适配：宽屏/最大化时把视口富余空间按权重分给文本列，窄屏/高倍缩放时严守最小可读列宽并启用横滚
        public void FitPGridColumns() {
            if (pGrid == null || pGrid.Columns.Count == 0 || pGrid.ClientSize.Width < Ui.S(200)) return;
            float scale = zoomPercent / 100f;
            int rowHdr = pGrid.RowHeadersVisible ? pGrid.RowHeadersWidth : 0;
            int vScroll = SystemInformation.VerticalScrollBarWidth + Ui.S(4);
            int avail = pGrid.ClientSize.Width - rowHdr - vScroll;
            if (avail < Ui.S(160)) return;

            var visCols = pGrid.Columns.Cast<DataGridViewColumn>().Where(c => c.Visible).ToList();
            if (visCols.Count == 0) return;

            var minWidths = new int[visCols.Count];
            var weights = new float[visCols.Count];
            int totalMin = 0;
            float totalWeight = 0f;
            for (int i = 0; i < visCols.Count; i++) {
                string colName = visCols[i].Name;
                string key = activeSubTab + ":" + colName;
                int bw;
                if (!baseWidths.TryGetValue(key, out bw)) { bw = visCols[i].Width; baseWidths[key] = bw; }
                int mw = Math.Max(Ui.S(52), (int)Math.Round(bw * scale));
                minWidths[i] = mw;
                totalMin += mw;
                float w = ColumnFlexWeight(colName);
                weights[i] = w;
                totalWeight += w;
            }

            int extra = avail - totalMin;
            applyingZoom = true;
            pGrid.SuspendLayout();
            try {
                int distributed = 0;
                for (int i = 0; i < visCols.Count; i++) {
                    int add = 0;
                    if (extra > 0 && totalWeight > 0f && weights[i] > 0f) {
                        add = (int)Math.Floor(extra * (weights[i] / totalWeight));
                        distributed += add;
                    }
                    int targetW = minWidths[i] + add;
                    if (visCols[i].Width != targetW) visCols[i].Width = targetW;
                }
                if (extra > distributed && visCols.Count > 0) {
                    int lastFlex = -1;
                    for (int i = visCols.Count - 1; i >= 0; i--) if (weights[i] > 0f) { lastFlex = i; break; }
                    if (lastFlex >= 0) visCols[lastFlex].Width += (extra - distributed);
                }
            } finally {
                pGrid.ResumeLayout();
                applyingZoom = false;
            }
        }

        static float ColumnFlexWeight(string colName) {
            if (colName == "描述" || colName == "气缸描述" || colName == "功能参数") return 3.0f;
            if (colName == "型号" || colName == "气缸型号" || colName == "阀片型号") return 2.4f;
            if (colName == "阀片类型" || colName == "所属阀岛" || colName == "备注") return 1.8f;
            if (colName == "接口类型" || colName == "3D绑定") return 1.4f;
            return 0f;
        }

        void ApplyZoomToGrid(DataGridView g, float scale, bool adjustColWidth) {
            if (g == null) return;
            applyingZoom = true;
            g.SuspendLayout();
            try {
                g.DefaultCellStyle.Font = cellFont;
                g.ColumnHeadersDefaultCellStyle.Font = headerFont;
                int rowH = Math.Max(Ui.S(22), (int)Math.Round(Ui.S(31) * scale));
                int hdrH = Math.Max(Ui.S(26), (int)Math.Round(Ui.S(33) * scale));
                g.RowTemplate.Height = rowH;
                g.ColumnHeadersHeight = hdrH;
                foreach (DataGridViewRow r in g.Rows) if (r.Height != rowH) r.Height = rowH;
                if (adjustColWidth) {
                    // 孤立件表使用独立键前缀并跳过 Fill 列，防止与 pGrid 各子页同名列（编号/描述/型号/品牌）互相污染宽度
                    string keyPrefix = g == orphanGrid ? "orphan:" : (activeSubTab + ":");
                    foreach (DataGridViewColumn col in g.Columns) {
                        if (col.AutoSizeMode == DataGridViewAutoSizeColumnMode.Fill) continue;
                        string key = keyPrefix + col.Name;
                        int bw;
                        if (!baseWidths.TryGetValue(key, out bw)) { bw = col.Width; baseWidths[key] = bw; }
                        col.Width = Math.Max(Ui.S(52), (int)Math.Round(bw * scale));
                    }
                }
            } finally {
                g.ResumeLayout();
                applyingZoom = false;
            }
        }

        public void EndEdit() {
            try { if (pGrid.IsCurrentCellInEditMode) pGrid.EndEdit(); } catch { }
            try { if (orphanGrid.IsCurrentCellInEditMode) orphanGrid.EndEdit(); } catch { }
        }

        // 从最新 project 刷新整个气动面板（保持当前选中的阀岛与子页）
        public void RefreshFromProject(string focusRecordId = null) {
            var p = GetProject != null ? GetProject() : null;
            if (p == null) return;
            hasLoadedOnce = true;

            islands = LineupPneumaticOps.ListIslands(p);
            orphans = LineupPneumaticOps.FindOrphanPneumaticRecords(p);

            // 若有待聚焦记录，自动定位到其所属阀岛
            if (!string.IsNullOrEmpty(focusRecordId)) {
                var target = p.Records.FirstOrDefault(r => r.Id == focusRecordId);
                if (target != null) {
                    string ik = target.Category == "阀岛" ? target.Number : LineupNumbering.IslandKey(target.Number);
                    if (ik.Length > 0 && islands.Any(x => string.Equals(x.Key, ik, StringComparison.OrdinalIgnoreCase))) {
                        selectedIslandKey = ik;
                        viewingOrphans = false;
                    }
                }
            }

            if (!viewingOrphans) {
                if (islands.Count == 0) {
                    selectedIslandKey = "";
                    viewingOrphans = orphans.Count > 0;
                } else if (!islands.Any(x => string.Equals(x.Key, selectedIslandKey, StringComparison.OrdinalIgnoreCase))) {
                    string activeKey = !string.IsNullOrWhiteSpace(p.NumberPrefix) ? (p.NumberPrefix.Trim() + "-Y" + Math.Max(1, p.ActiveIsland)) : "";
                    var hit = islands.FirstOrDefault(x => string.Equals(x.Key, activeKey, StringComparison.OrdinalIgnoreCase)) ?? islands[0];
                    selectedIslandKey = hit.Key;
                }
            } else if (orphans.Count == 0 && islands.Count > 0) {
                viewingOrphans = false;
                selectedIslandKey = islands[0].Key;
            }

            RebuildIslandList();
            RefreshRightView(focusRecordId);
        }

        // ==================== 构建左侧阀岛树 ====================
        void BuildSidebar() {
            var hdr = new Panel { Dock = DockStyle.Top, Height = Ui.S(38), BackColor = Ui.Rail };
            var lbl = new Label {
                Text = "阀岛 / 阀组", Font = Ui.F9B, ForeColor = Ui.Text, BackColor = Ui.Rail,
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft
            };
            btnNewIsland.Dock = DockStyle.Right;
            btnNewIsland.Click += (s, e) => Try(OpenCreateIslandDialog);
            tips.SetToolTip(btnNewIsland, "新建一个阀岛、汇流排、阀组或单片阀（如 Y1、Y2）");
            var newBtnWrap = new Panel { Dock = DockStyle.Right, Width = btnNewIsland.Width, Padding = Ui.SPad(0, 4, 0, 6), BackColor = Ui.Rail };
            newBtnWrap.Controls.Add(btnNewIsland);
            hdr.Controls.Add(lbl);
            hdr.Controls.Add(newBtnWrap);

            islandList.Dock = DockStyle.Fill;
            islandList.DrawMode = DrawMode.OwnerDrawFixed;
            islandList.ItemHeight = Ui.S(64);
            islandList.BorderStyle = BorderStyle.None;
            islandList.BackColor = Ui.Rail;
            islandList.IntegralHeight = false;
            islandList.DrawItem += DrawIslandListItem;
            islandList.SelectedIndexChanged += (s, e) => {
                if (syncingIslandList) return;
                var item = islandList.SelectedItem as IslandListItem;
                if (item == null) return;
                if (item.IsOrphanBucket) {
                    viewingOrphans = true;
                } else {
                    viewingOrphans = false;
                    selectedIslandKey = item.Island.Key;
                }
                RefreshRightView(null);
            };
            islandList.DoubleClick += (s, e) => {
                if (!viewingOrphans && !string.IsNullOrEmpty(selectedIslandKey)) Try(OpenEditIslandDialog);
            };
            islandList.MouseUp += (s, e) => {
                if (e.Button != MouseButtons.Right) return;
                int idx = islandList.IndexFromPoint(e.Location);
                if (idx >= 0 && idx < islandList.Items.Count) islandList.SelectedIndex = idx;
                if (!viewingOrphans && !string.IsNullOrEmpty(selectedIslandKey))
                    islandMenu.Show(islandList, e.Location);
            };

            var foot = new FlowLayoutPanel {
                Dock = DockStyle.Bottom, Height = Ui.S(34), WrapContents = false, BackColor = Ui.Rail,
                Padding = Ui.SPad(0, 5, 0, 0)
            };
            btnEditIsland.Margin = Ui.SPad(0, 0, 4, 0);
            btnBindIsland3D.Margin = Ui.SPad(0, 0, 4, 0);
            btnDeleteIsland.Margin = new Padding(0);
            btnEditIsland.Click += (s, e) => Try(OpenEditIslandDialog);
            btnBindIsland3D.Click += (s, e) => Try(OnBindOrLocateIslandClick);
            btnDeleteIsland.Click += (s, e) => Try(DeleteSelectedIsland);
            tips.SetToolTip(btnEditIsland, "修改当前阀岛的形态（阀岛/汇流排/单片阀）与型号");
            tips.SetToolTip(btnBindIsland3D, "进入 3D 点选模型绑定到此阀岛；已绑时可定位");
            tips.SetToolTip(btnDeleteIsland, "删除当前选中的阀岛");
            foot.Controls.AddRange(new Control[] { btnEditIsland, btnBindIsland3D, btnDeleteIsland });

            sidebarCard.Controls.Add(islandList);
            sidebarCard.Controls.Add(hdr);
            sidebarCard.Controls.Add(foot);
        }

        void DrawIslandListItem(object sender, DrawItemEventArgs e) {
            if (e.Index < 0 || e.Index >= islandList.Items.Count) return;
            var g = e.Graphics;
            var item = (IslandListItem)islandList.Items[e.Index];
            bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            var outer = e.Bounds;
            g.FillRectangle(Ui.CachedBrush(Ui.Rail), outer);

            var card = new Rectangle(outer.X + 1, outer.Y + Ui.S(3), outer.Width - 3, outer.Height - Ui.S(6));
            int rad = Ui.S(8);
            if (item.IsOrphanBucket) {
                Ui.FillRound(g, card, rad, selected ? Ui.WarnSoft : Color.White);
                Ui.DrawRound(g, card, rad, selected ? Color.FromArgb(0xF5, 0x9E, 0x0B) : Ui.Line, 1);
                TextRenderer.DrawText(g, "未归属气动件 (" + item.OrphanCount + ")", Ui.F9B,
                    new Rectangle(card.X + Ui.S(10), card.Y + Ui.S(8), card.Width - Ui.S(20), Ui.S(20)),
                    WarnText, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, "点击查看并一键归属到阀岛", Ui.F8,
                    new Rectangle(card.X + Ui.S(10), card.Y + Ui.S(30), card.Width - Ui.S(20), Ui.S(18)),
                    Ui.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                return;
            }

            var isl = item.Island;
            Ui.FillRound(g, card, rad, selected ? Color.White : Color.FromArgb(0xFC, 0xFC, 0xFD));
            Ui.DrawRound(g, card, rad, selected ? Ui.Accent : Ui.Subtle, selected ? 1.5f : 1f);
            if (selected) {
                g.FillRectangle(Ui.CachedBrush(Ui.Accent), card.X + 1, card.Y + Ui.S(8), Ui.S(3), card.Height - Ui.S(16));
            }

            int x = card.X + Ui.S(10);
            int rightW = isl.Has3DBind ? Ui.S(38) : Ui.S(8);
            int lineH = Math.Max(Ui.S(15), (card.Height - Ui.S(10)) / 3);
            int y1 = card.Y + Ui.S(4);
            int y2 = y1 + lineH;
            int y3 = y2 + lineH;
            var fTitle = selected ? (cellBoldFont ?? Ui.F9B) : (cellFont ?? Ui.F9);
            var fSub = zoomPercent >= 120 ? (cellFont ?? Ui.F8) : Ui.F8;

            string title = isl.Key + " · " + isl.Kind;
            TextRenderer.DrawText(g, title, fTitle,
                new Rectangle(x, y1, Math.Max(20, card.Width - Ui.S(14) - rightW), lineH),
                selected ? Ui.Accent : Ui.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            if (isl.Has3DBind) {
                TextRenderer.DrawText(g, "3D✓", Ui.F8,
                    new Rectangle(card.Right - Ui.S(38), y1, Ui.S(32), lineH),
                    Ui.Ok, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            }

            string modelLine = string.IsNullOrWhiteSpace(isl.Model) ? "未填阀岛型号 · 双击编辑" : isl.Model;
            TextRenderer.DrawText(g, modelLine, fSub,
                new Rectangle(x, y2, card.Width - Ui.S(16), lineH),
                string.IsNullOrWhiteSpace(isl.Model) ? Ui.Faint : Ui.Text2,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            string stats = isl.ValveCount == 0 && isl.CylinderCount == 0
                ? "空阀岛 · 点右侧添加气缸或阀片"
                : (isl.ValveCount + " 阀片 · " + isl.CylinderCount + " 气缸 · " + isl.SensorCount + " 磁开");
            bool hasWarn = isl.MissingValveCylCount > 0 || isl.MissingSensorCylCount > 0;
            TextRenderer.DrawText(g, hasWarn ? "● " + stats : stats, Ui.F8,
                new Rectangle(x, y3, card.Width - Ui.S(16), lineH),
                hasWarn ? WarnText : Ui.Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        void RebuildIslandList() {
            // 同步列表项时屏蔽 SelectedIndexChanged，避免 RefreshFromProject 连续触发两次 RefreshRightView 整表重建
            syncingIslandList = true;
            islandList.BeginUpdate();
            try {
                islandList.Items.Clear();
                int targetIndex = -1;
                for (int i = 0; i < islands.Count; i++) {
                    islandList.Items.Add(new IslandListItem { Island = islands[i] });
                    if (!viewingOrphans && string.Equals(islands[i].Key, selectedIslandKey, StringComparison.OrdinalIgnoreCase))
                        targetIndex = i;
                }
                if (orphans.Count > 0) {
                    islandList.Items.Add(new IslandListItem { IsOrphanBucket = true, OrphanCount = orphans.Count });
                    if (viewingOrphans) targetIndex = islandList.Items.Count - 1;
                }
                if (targetIndex >= 0 && targetIndex < islandList.Items.Count)
                    islandList.SelectedIndex = targetIndex;
            } finally {
                islandList.EndUpdate();
                syncingIslandList = false;
            }

            bool hasIsland = !viewingOrphans && !string.IsNullOrEmpty(selectedIslandKey);
            btnEditIsland.Enabled = hasIsland;
            btnBindIsland3D.Enabled = hasIsland;
            btnDeleteIsland.Enabled = hasIsland;
            UpdateSidebarFooterCompact();
        }

        // ==================== 构建右侧工作区 ====================
        void BuildRightArea() {
            // 1. 尚无阀岛引导页
            var noInner = new Panel { BackColor = Color.White };
            var noTitle = new Label {
                Text = "先新建一个阀岛 / 阀组（如 Y1）", Font = Ui.F12B, ForeColor = Ui.Text, BackColor = Color.White,
                Dock = DockStyle.Top, Height = Ui.S(34), TextAlign = ContentAlignment.MiddleCenter
            };
            var noSub = new Label {
                Text = "建好阀岛后，每次点击「+ 添加气缸」都会自动配齐配套阀片和 H/W 磁开，也能先铺阀片/预留盖板。",
                Font = Ui.F9, ForeColor = Ui.Muted, BackColor = Color.White,
                Dock = DockStyle.Top, Height = Ui.S(42), TextAlign = ContentAlignment.MiddleCenter
            };
            var noBtnRow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = Ui.S(46), WrapContents = false, BackColor = Color.White };
            var btnFirstIsland = Ui.Primary("+ 新建第 1 个阀岛 (Y1)", 178, 36);
            var btnScanFromEmpty = Ui.Secondary("自动扫描装配", 118, 36);
            var btnImportFromEmpty = Ui.Secondary("导入已有 Excel…", 128, 36);
            btnFirstIsland.Margin = Ui.SPad(0, 4, 8, 0);
            btnScanFromEmpty.Margin = Ui.SPad(0, 4, 8, 0);
            btnImportFromEmpty.Margin = Ui.SPad(0, 4, 0, 0);
            btnFirstIsland.Click += (s, e) => Try(OpenCreateIslandDialog);
            btnScanFromEmpty.Click += (s, e) => { if (OnScanAssembly != null) Try(OnScanAssembly); };
            btnImportFromEmpty.Click += (s, e) => { if (OnImportXlsx != null) Try(OnImportXlsx); };
            noBtnRow.Controls.AddRange(new Control[] { btnFirstIsland, btnScanFromEmpty, btnImportFromEmpty });
            noInner.Controls.Add(noBtnRow);
            noInner.Controls.Add(noSub);
            noInner.Controls.Add(noTitle);
            noIslandHost.Controls.Add(noInner);
            noIslandHost.Resize += (s, e) => {
                int availW = Math.Max(Ui.S(260), noIslandHost.ClientSize.Width - Ui.S(24));
                int singleW = btnFirstIsland.Width + btnScanFromEmpty.Width + btnImportFromEmpty.Width + Ui.S(24);
                bool wrap = availW < singleW + Ui.S(16);
                noBtnRow.WrapContents = wrap;
                int w = Math.Min(availW, Math.Max(Ui.S(340), singleW + Ui.S(20)));
                noBtnRow.Height = wrap ? Ui.S(92) : Ui.S(46);
                noSub.Height = availW < Ui.S(460) ? Ui.S(54) : Ui.S(40);
                int h = noTitle.Height + noSub.Height + noBtnRow.Height + Ui.S(8);
                noInner.SetBounds(Math.Max(Ui.S(12), (noIslandHost.ClientSize.Width - w) / 2), Math.Max(Ui.S(16), (noIslandHost.ClientSize.Height - h) / 2 - Ui.S(8)), w, h);
                noBtnRow.Padding = new Padding(wrap ? Ui.S(8) : Math.Max(0, (w - singleW) / 2), 0, 0, 0);
            };

            // 2. 孤立件归属页
            var orphanTop = new Panel { Dock = DockStyle.Top, Height = Ui.S(42), BackColor = Color.White };
            orphanTitleLbl.Dock = DockStyle.Fill;
            orphanTitleLbl.Font = Ui.F10B;
            orphanTitleLbl.ForeColor = WarnText;
            orphanTitleLbl.TextAlign = ContentAlignment.MiddleLeft;
            orphanTitleLbl.AutoEllipsis = true;
            var btnAssignAll = Ui.Primary("一键归属到阀岛…", 142, 30);
            btnAssignAll.Font = Ui.F8;
            btnAssignAll.Click += (s, e) => Try(AssignOrphansDialog);
            var orphanBtnWrap = new Panel { Dock = DockStyle.Right, Width = btnAssignAll.Width, Padding = Ui.SPad(0, 5, 0, 7), BackColor = Color.White };
            orphanBtnWrap.Controls.Add(btnAssignAll);
            orphanTop.Controls.Add(orphanTitleLbl);
            orphanTop.Controls.Add(orphanBtnWrap);
            orphanHost.Controls.Add(orphanGrid);
            orphanHost.Controls.Add(orphanTop);

            // 3. 选中阀岛主工作区
            topHeaderBar = new Panel { Dock = DockStyle.Top, Height = Ui.S(42), BackColor = Color.White };
            var leftInfo = new FlowLayoutPanel {
                Dock = DockStyle.Fill, WrapContents = false, BackColor = Color.White, Padding = Ui.SPad(0, 7, 0, 0)
            };
            islandTitleLbl.AutoSize = true;
            islandTitleLbl.Font = Ui.F10B;
            islandTitleLbl.ForeColor = Ui.Text;
            islandTitleLbl.Margin = Ui.SPad(0, 2, 8, 0);

            islandKindBadge.AutoSize = true;
            islandKindBadge.Font = Ui.F8;
            islandKindBadge.ForeColor = Ui.Accent;
            islandKindBadge.BackColor = Ui.AccentSoft;
            islandKindBadge.Padding = Ui.SPad(7, 2, 7, 2);
            islandKindBadge.Margin = Ui.SPad(0, 3, 8, 0);

            islandModelLbl.AutoSize = true;
            islandModelLbl.AutoEllipsis = true;
            islandModelLbl.Font = Ui.F9;
            islandModelLbl.ForeColor = Ui.Muted;
            islandModelLbl.Cursor = Cursors.Hand;
            islandModelLbl.Margin = Ui.SPad(0, 3, 0, 0);
            islandModelLbl.Click += (s, e) => Try(OpenEditIslandDialog);
            tips.SetToolTip(islandModelLbl, "点击修改本阀岛型号与控制方式");
            leftInfo.Controls.AddRange(new Control[] { islandTitleLbl, islandKindBadge, islandModelLbl });

            headerRightBtns = new FlowLayoutPanel {
                Dock = DockStyle.Right, AutoSize = true, WrapContents = false, BackColor = Color.White, Padding = Ui.SPad(0, 5, 0, 0)
            };
            btnAddCyl.Margin = Ui.SPad(0, 0, 6, 0);
            btnAddValve.Margin = Ui.SPad(0, 0, 6, 0);
            btnCadOps.Margin = new Padding(0);
            btnAddCyl.Click += (s, e) => Try(() => AddCylinderQuick(false));
            Ui.BindDropDownMenu(btnAddValve, addValveMenu);
            Ui.BindDropDownMenu(btnCadOps, cadOpsMenu);
            tips.SetToolTip(btnAddCyl, "在当前阀岛新增气缸，自动生成配套阀片 (V**) 和 H/W 磁开");
            tips.SetToolTip(btnAddValve, "单独添加空阀片、预留盖板、吹气口，或批量铺设 N 片阀位");
            tips.SetToolTip(btnCadOps, "从 CAD 装配扫描、点选加入气缸，或绑定当前阀岛的 3D 模型");
            headerRightBtns.Controls.AddRange(new Control[] { btnAddCyl, btnAddValve, btnCadOps });

            topHeaderBar.Controls.Add(leftInfo);
            topHeaderBar.Controls.Add(headerRightBtns);
            topHeaderBar.Resize += (s, e) => LayoutIslandHeaderModel();

            // 子视图标签栏
            subTabBar.Dock = DockStyle.Top;
            subTabBar.Height = Ui.S(34);
            subTabBar.BackColor = Color.White;
            subTabBar.Padding = Ui.SPad(0, 2, 0, 4);
            subTabBar.Paint += (s, e) => {
                using (var pen = new Pen(Ui.Subtle)) e.Graphics.DrawLine(pen, 0, subTabBar.Height - 1, subTabBar.Width, subTabBar.Height - 1);
            };
            subTabBar.Resize += (s, e) => CheckSubTabCompactness();
            subTabFlow.Dock = DockStyle.Fill;
            subTabFlow.WrapContents = false;
            subTabFlow.BackColor = Color.White;
            btnSubQuickAction.Dock = DockStyle.Right;
            btnSubQuickAction.Click += (s, e) => Try(OnSubQuickActionClick);
            var quickWrap = new Panel { Dock = DockStyle.Right, Width = Ui.S(154), BackColor = Color.White, Padding = Ui.SPad(0, 2, 0, 4) };
            quickWrap.Controls.Add(btnSubQuickAction);
            subTabBar.Controls.Add(subTabFlow);
            subTabBar.Controls.Add(quickWrap);

            // 空阀岛友好引导
            var eiInner = new Panel { BackColor = Color.White };
            emptyIslandTitle.Font = Ui.F10B;
            emptyIslandTitle.ForeColor = Ui.Text;
            emptyIslandTitle.BackColor = Color.White;
            emptyIslandTitle.Dock = DockStyle.Top;
            emptyIslandTitle.Height = Ui.S(30);
            emptyIslandTitle.TextAlign = ContentAlignment.MiddleCenter;
            var eiSub = new Label {
                Text = "推荐直接点「+ 添加第 1 支气缸」，系统会自动配好 V01 阀片与 H/W 传感器；也可先铺设多片阀位。",
                Font = Ui.F8, ForeColor = Ui.Muted, BackColor = Color.White,
                Dock = DockStyle.Top, Height = Ui.S(28), TextAlign = ContentAlignment.MiddleCenter
            };
            var eiBtns = new FlowLayoutPanel { Dock = DockStyle.Top, Height = Ui.S(44), WrapContents = false, BackColor = Color.White };
            var bEiCyl = Ui.Primary("+ 添加第 1 支气缸（配阀+H/W）", 210, 34);
            var bEiValves = Ui.Secondary("+ 批量铺设阀片 / 预留位…", 174, 34);
            bEiCyl.Margin = Ui.SPad(0, 4, 10, 0);
            bEiValves.Margin = Ui.SPad(0, 4, 0, 0);
            bEiCyl.Click += (s, e) => Try(() => AddCylinderQuick(false));
            bEiValves.Click += (s, e) => Try(() => BatchAddValveSlicesDialog(false));
            eiBtns.Controls.AddRange(new Control[] { bEiCyl, bEiValves });
            eiInner.Controls.Add(eiBtns);
            eiInner.Controls.Add(eiSub);
            eiInner.Controls.Add(emptyIslandTitle);
            emptyIslandHost.Controls.Add(eiInner);
            emptyIslandHost.Resize += (s, e) => {
                int availW = Math.Max(Ui.S(260), emptyIslandHost.ClientSize.Width - Ui.S(20));
                int bw = bEiCyl.Width + bEiValves.Width + Ui.S(16);
                bool wrap = availW < bw + Ui.S(16);
                eiBtns.WrapContents = wrap;
                eiBtns.Height = wrap ? Ui.S(88) : Ui.S(44);
                eiSub.Height = availW < Ui.S(460) ? Ui.S(44) : Ui.S(28);
                int w = Math.Min(availW, Math.Max(Ui.S(340), bw + Ui.S(20)));
                int h = emptyIslandTitle.Height + eiSub.Height + eiBtns.Height + Ui.S(8);
                eiInner.SetBounds(Math.Max(Ui.S(10), (emptyIslandHost.ClientSize.Width - w) / 2), Math.Max(Ui.S(16), (emptyIslandHost.ClientSize.Height - h) / 2 - Ui.S(8)), w, h);
                eiBtns.Padding = new Padding(wrap ? Ui.S(8) : Math.Max(0, (w - bw) / 2), 0, 0, 0);
            };

            gridWrap.Controls.Add(pGrid);
            gridWrap.Controls.Add(emptyIslandHost);
            emptyIslandHost.BringToFront();

            islandWorkHost.Controls.Add(gridWrap);
            islandWorkHost.Controls.Add(subTabBar);
            islandWorkHost.Controls.Add(topHeaderBar);

            rightCard.Controls.Add(islandWorkHost);
            rightCard.Controls.Add(orphanHost);
            rightCard.Controls.Add(noIslandHost);
        }

        void LayoutIslandHeaderModel() {
            if (topHeaderBar == null || headerRightBtns == null) return;
            int avail = topHeaderBar.ClientSize.Width - headerRightBtns.Width - islandTitleLbl.Width - islandKindBadge.Width - Ui.S(32);
            islandModelLbl.Visible = avail >= Ui.S(60);
            if (islandModelLbl.Visible) islandModelLbl.MaximumSize = new Size(Math.Max(Ui.S(60), avail), Ui.S(24));
        }

        void CheckSubTabCompactness() {
            if (subTabBar == null) return;
            bool wantCompact = subTabBar.ClientSize.Width > 0 && subTabBar.ClientSize.Width < Ui.S(600);
            if (wantCompact != subTabsCompact) {
                subTabsCompact = wantCompact;
                RebuildSubTabs();
            }
        }

        void BuildMenus() {
            Action<ContextMenuStrip, string, Action, string> add = (m, t, act, tone) => {
                var mi = new ToolStripMenuItem(t);
                if (!string.IsNullOrEmpty(tone)) mi.Tag = tone;
                mi.Click += (s, e) => Try(act);
                m.Items.Add(mi);
            };

            add(islandMenu, "编辑阀岛型号 / 形态…", OpenEditIslandDialog, null);
            add(islandMenu, "+ 添加气缸到此阀岛（配阀+H/W）", () => AddCylinderQuick(false), null);
            add(islandMenu, "+ 批量添加阀片…", () => BatchAddValveSlicesDialog(false), null);
            islandMenu.Items.Add(new ToolStripSeparator());
            add(islandMenu, "进入 3D 点选模型绑定本阀岛", () => { if (OnStartBindIslandIn3D != null) OnStartBindIslandIn3D(selectedIslandKey); }, "accent");
            add(islandMenu, "绑定 CAD 当前选中件为本阀岛", () => { if (OnBindCurrentCadToIsland != null) OnBindCurrentCadToIsland(selectedIslandKey); }, null);
            add(islandMenu, "在 CAD 中定位本阀岛", LocateSelectedIsland, null);
            add(islandMenu, "将本阀岛本体加入「其他电气件 E**」", SyncSelectedIslandToElectrical, null);
            islandMenu.Items.Add(new ToolStripSeparator());
            add(islandMenu, "删除此阀岛…", DeleteSelectedIsland, "danger");

            add(addValveMenu, "+ 添加 1 片标准阀片 (V**)", () => AddValveSlicesQuick(false, 1), null);
            add(addValveMenu, "+ 添加 1 个预留位 / 盖板 (V**)", () => AddValveSlicesQuick(true, 1), null);
            add(addValveMenu, "+ 添加吹气 / 破真空阀路（无磁开）", () => AddCylinderQuick(true), null);
            addValveMenu.Items.Add(new ToolStripSeparator());
            add(addValveMenu, "批量铺设 N 片标准阀片…", () => BatchAddValveSlicesDialog(false), null);
            add(addValveMenu, "批量铺设 N 个预留盖板…", () => BatchAddValveSlicesDialog(true), null);

            add(cadOpsMenu, "加入 CAD 当前选中件为气缸（自动配阀+H/W）", () => { if (OnAddCadSelectedAsCylinder != null) OnAddCadSelectedAsCylinder(selectedIslandKey); }, null);
            add(cadOpsMenu, "在 CAD 连续点选加入气缸（到本阀岛）", () => { if (OnStartContinuousAddCylinder != null) OnStartContinuousAddCylinder(selectedIslandKey); }, null);
            add(cadOpsMenu, "自动扫描整机装配识别器件…", () => { if (OnScanAssembly != null) OnScanAssembly(); }, null);
            cadOpsMenu.Items.Add(new ToolStripSeparator());
            add(cadOpsMenu, "在 CAD 连续绑定本岛未绑气缸", () => { if (OnStartContinuousBindIsland != null) OnStartContinuousBindIsland(selectedIslandKey); }, "accent");
            add(cadOpsMenu, "进入 3D 点选模型绑定本阀岛", () => { if (OnStartBindIslandIn3D != null) OnStartBindIslandIn3D(selectedIslandKey); }, null);
            add(cadOpsMenu, "绑定 CAD 当前选中件为本阀岛本体", () => { if (OnBindCurrentCadToIsland != null) OnBindCurrentCadToIsland(selectedIslandKey); }, null);
        }

        void SetupPGrid() {
            StyleGrid(pGrid);
            pGrid.SuppressDoubleClickEdit = (rowIndex, colIndex) => {
                if (colIndex < 0 || colIndex >= pGrid.Columns.Count) return false;
                string cn = pGrid.Columns[colIndex].Name;
                return cn == "型号" || cn == "气缸型号" || cn == "阀片型号";
            };
            pGrid.CellBeginEdit += (s, e) => {
                if (loadingGrid || e.RowIndex < 0 || e.ColumnIndex < 0) return;
                editStartVal = Convert.ToString(pGrid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value ?? "");
            };
            pGrid.CellEndEdit += OnPGridCellEndEdit;
            pGrid.CellFormatting += OnPGridCellFormatting;
            pGrid.CellDoubleClick += OnPGridCellDoubleClick;
            pGrid.CellMouseUp += OnPGridCellMouseUp;
            pGrid.CurrentCellChanged += (s, e) => {
                if (!loadingGrid && OnSelectionChanged != null) OnSelectionChanged(SelectedRecordId);
            };
            pGrid.EditingControlShowing += OnPGridEditingControlShowing;
            pGrid.MouseWheel += HandleGridWheel;
            pGrid.KeyDown += OnPGridKeyDown;
            pGrid.DataError += (s, e) => { e.ThrowException = false; };
            pGrid.ColumnWidthChanged += (s, e) => {
                if (applyingZoom || loadingGrid || e.Column == null) return;
                float sc = Math.Max(0.5f, zoomPercent / 100f);
                baseWidths[activeSubTab + ":" + e.Column.Name] = (int)Math.Round(e.Column.Width / sc);
            };
            pGrid.Resize += (s, e) => FitPGridColumns();
        }

        void SetupOrphanGrid() {
            StyleGrid(orphanGrid);
            orphanGrid.ReadOnly = true;
            orphanGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            // 禁止点击表头自动排序（并配合 Row.Tag 存 Record.Id），避免排序后视图行号与 orphans 列表错位
            orphanGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "类别", HeaderText = "类别", Width = Ui.S(86), SortMode = DataGridViewColumnSortMode.NotSortable });
            orphanGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "编号", HeaderText = "当前编号", Width = Ui.S(136), SortMode = DataGridViewColumnSortMode.NotSortable });
            orphanGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "描述", HeaderText = "描述", Width = Ui.S(210), SortMode = DataGridViewColumnSortMode.NotSortable });
            orphanGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "型号", HeaderText = "型号", Width = Ui.S(200), SortMode = DataGridViewColumnSortMode.NotSortable });
            orphanGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "品牌", HeaderText = "品牌", Width = Ui.S(96), SortMode = DataGridViewColumnSortMode.NotSortable });
            orphanGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "绑定", HeaderText = "3D 绑定", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, SortMode = DataGridViewColumnSortMode.NotSortable });
            orphanGrid.MouseWheel += HandleGridWheel;
        }

        static void StyleGrid(DataGridView g) {
            try {
                var pi = typeof(DataGridView).GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic);
                if (pi != null) pi.SetValue(g, true, null);
            } catch { }
            g.Dock = DockStyle.Fill;
            g.AutoGenerateColumns = false;
            g.AllowUserToAddRows = false;
            g.AllowUserToDeleteRows = false;
            g.AllowUserToResizeColumns = true;
            g.AllowUserToResizeRows = false;
            g.EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2;
            g.RowHeadersWidth = Ui.S(34);
            g.SelectionMode = DataGridViewSelectionMode.CellSelect;
            g.MultiSelect = true;
            g.BackgroundColor = Color.White;
            g.BorderStyle = BorderStyle.None;
            g.ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithoutHeaderText;
            g.EnableHeadersVisualStyles = false;
            g.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
            g.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            g.ColumnHeadersDefaultCellStyle.BackColor = Ui.Rail;
            g.ColumnHeadersDefaultCellStyle.ForeColor = Ui.Muted;
            g.ColumnHeadersDefaultCellStyle.SelectionBackColor = Ui.Rail;
            g.ColumnHeadersDefaultCellStyle.Padding = Ui.SPad(5, 0, 5, 0);
            g.DefaultCellStyle.Padding = Ui.SPad(5, 0, 5, 0);
            g.DefaultCellStyle.ForeColor = Ui.Text;
            g.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(0xFB, 0xFB, 0xFC);
            g.GridColor = Color.FromArgb(0xEE, 0xEE, 0xF2);
            g.DefaultCellStyle.SelectionBackColor = Ui.AccentSoft;
            g.DefaultCellStyle.SelectionForeColor = Ui.Text;
            g.RowHeadersDefaultCellStyle.BackColor = Ui.Rail;
            g.RowHeadersDefaultCellStyle.SelectionBackColor = Ui.AccentSoft;
            g.RowHeadersDefaultCellStyle.ForeColor = Ui.Muted;
        }

        void HandleGridWheel(object sender, MouseEventArgs e) {
            var g = sender as DataGridView ?? pGrid;
            if ((ModifierKeys & Keys.Control) == Keys.Control) {
                var he = e as HandledMouseEventArgs;
                if (he != null) he.Handled = true;
                if (OnZoomDelta != null) OnZoomDelta(e.Delta > 0 ? 10 : -10);
            } else if ((ModifierKeys & Keys.Shift) == Keys.Shift) {
                var he = e as HandledMouseEventArgs;
                if (he != null) he.Handled = true;
                int step = Ui.S(80);
                g.HorizontalScrollingOffset = Math.Max(0, g.HorizontalScrollingOffset + (e.Delta > 0 ? -step : step));
            }
        }

        // 直接在控件命令键阶段响应 Alt+↑ / Alt+↓，配合 CadKeyShield 确保所有气动子视图快捷键立即可用
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData) {
            if (!IsEditing && !viewingOrphans && pGrid.Visible && OnMoveSelectedRows != null
                && (keyData == (Keys.Alt | Keys.Up) || keyData == (Keys.Alt | Keys.Down))) {
                int dir = keyData == (Keys.Alt | Keys.Up) ? -1 : 1;
                Try(() => OnMoveSelectedRows(dir));
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        void OnPGridKeyDown(object sender, KeyEventArgs e) {
            if (pGrid.IsCurrentCellInEditMode) return;
            if (e.Alt && !e.Control && !e.Shift && (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down)) {
                e.Handled = true;
                e.SuppressKeyPress = true;
                if (OnMoveSelectedRows != null) Try(() => OnMoveSelectedRows(e.KeyCode == Keys.Up ? -1 : 1));
            } else if (e.KeyCode == Keys.Delete) {
                e.SuppressKeyPress = true;
                Try(ClearSelectedPGridCells);
            } else if (e.Control && e.KeyCode == Keys.D) {
                e.SuppressKeyPress = true;
                Try(FillDownPGrid);
            } else if (e.Control && e.KeyCode == Keys.C) {
                e.SuppressKeyPress = true;
                Try(CopySelectedPGridCells);
            } else if (e.Control && e.KeyCode == Keys.V) {
                e.SuppressKeyPress = true;
                // 统一经 ClipboardUtil 重试读取，规避 DLP / Excel 瞬时锁剪贴板导致的 ExternalException
                Try(() => PasteSelectedPGridCells(ClipboardUtil.GetText()));
            }
        }

        // ==================== 刷新右侧视图 ====================
        void RefreshRightView(string focusRecordId) {
            Ui.BeginRedrawFreeze(rightHost);
            try {
                if (viewingOrphans) {
                    noIslandHost.Visible = false;
                    islandWorkHost.Visible = false;
                    orphanHost.Visible = true;
                    orphanHost.BringToFront();
                    RebuildOrphanGrid();
                    return;
                }
                var isl = islands.FirstOrDefault(x => string.Equals(x.Key, selectedIslandKey, StringComparison.OrdinalIgnoreCase));
                if (isl == null) {
                    orphanHost.Visible = false;
                    islandWorkHost.Visible = false;
                    noIslandHost.Visible = true;
                    noIslandHost.BringToFront();
                    return;
                }

                noIslandHost.Visible = false;
                orphanHost.Visible = false;
                islandWorkHost.Visible = true;
                islandWorkHost.BringToFront();

                islandTitleLbl.Text = isl.Key;
                islandKindBadge.Text = isl.Kind;
                islandModelLbl.Text = string.IsNullOrWhiteSpace(isl.Model) ? "未填阀岛型号（点此设置）" : "型号：" + isl.Model;
                islandModelLbl.ForeColor = string.IsNullOrWhiteSpace(isl.Model) ? Ui.Faint : Ui.Text2;

                var p = GetProject();
                currentChannels = LineupPneumaticOps.BuildChannelRows(p, isl.Key);
                LayoutIslandHeaderModel();
                RebuildSubTabs();
                RebuildActiveGrid(focusRecordId);
            } finally {
                Ui.EndRedrawFreeze(rightHost);
            }
        }

        void RebuildOrphanGrid() {
            orphanTitleLbl.Text = "未归属到任何阀岛的气缸 / 阀片（共 " + orphans.Count + " 项）";
            orphanGrid.SuspendLayout();
            try {
                orphanGrid.Rows.Clear();
                if (orphans.Count > 0) {
                    var batch = new DataGridViewRow[orphans.Count];
                    int rowH = orphanGrid.RowTemplate.Height;
                    for (int i = 0; i < orphans.Count; i++) {
                        var r = orphans[i];
                        string bind = string.Join(" / ", r.InstancePath ?? new string[0]);
                        int slash = bind.LastIndexOf('/');
                        string bindShort = bind.Length == 0 ? "未绑定" : "✓ " + (slash >= 0 ? bind.Substring(slash + 1).Trim() : bind);
                        var row = new DataGridViewRow();
                        row.CreateCells(orphanGrid, r.Category, r.Number, r.Description, r.Model, r.Brand, bindShort);
                        row.Height = rowH;
                        row.Tag = r.Id;
                        batch[i] = row;
                    }
                    orphanGrid.Rows.AddRange(batch);
                }
            } finally {
                orphanGrid.ResumeLayout();
            }
        }

        void RebuildSubTabs() {
            int chCount = currentChannels.Count;
            int cylCount = currentChannels.Count(c => c.Cylinder != null);
            int valveCount = currentChannels.Where(c => c.Valve != null).Select(c => c.Valve.Id).Distinct().Count();
            int senCount = currentChannels.Sum(c => c.Sensors.Count);

            string[] labels = subTabsCompact
                ? new[] { "阀路 " + chCount, "气缸 " + cylCount, "阀片 " + valveCount, "磁开 " + senCount }
                : new[] { "按阀路对照 " + chCount, "气缸明细 " + cylCount, "阀片明细 " + valveCount, "气缸传感器 " + senCount };

            // 原地复用 4 个子页签胶囊，彻底消除切换阀岛/子视图时的 Win32 句柄泄漏
            subTabFlow.SuspendLayout();
            for (int i = 0; i < 4; i++) {
                var pill = subTabPills[i];
                if (pill == null) {
                    int idx = i;
                    pill = new FlatButton("", Ui.Rail, Ui.Text2, Ui.Subtle, Ui.S(76), Ui.S(25));
                    pill.Click += (s, e) => { if (activeSubTab != idx) SelectSubTab(idx); };
                    subTabPills[i] = pill;
                    subTabFlow.Controls.Add(pill);
                }
                bool act = activeSubTab == i;
                Font wantFont = act ? Ui.F9B : Ui.F8;
                if (pill.Font != wantFont) pill.Font = wantFont;
                if (pill.Text != labels[i]) pill.Text = labels[i];
                pill.SetPalette(
                    act ? Ui.AccentSoft : Ui.Rail,
                    act ? Ui.Accent : Ui.Text2,
                    act ? Ui.AccentRing : Ui.Subtle);
                int w = TextRenderer.MeasureText(labels[i], wantFont).Width + Ui.S(subTabsCompact ? 12 : 18);
                if (pill.Width != w) pill.Width = w;
                var wantMargin = Ui.SPad(0, 2, subTabsCompact ? 4 : 6, 0);
                if (pill.Margin != wantMargin) pill.Margin = wantMargin;
            }
            subTabFlow.ResumeLayout();

            // 右侧语境快捷按钮
            bool hasMissing = currentChannels.Any(c => c.Cylinder != null && !c.IsSpare && (c.Valve == null || (!c.IsBlowOrVacuum && c.Sensors.Count < 2)));
            if (activeSubTab == 0) {
                btnSubQuickAction.Text = hasMissing ? (subTabsCompact ? "补齐阀/H/W" : "一键补齐本岛阀片/H/W") : (subTabsCompact ? "+多缸" : "+ 一阀控多缸");
                ((FlatButton)btnSubQuickAction).SetFore(hasMissing ? Ui.Accent : Ui.Text2);
                tips.SetToolTip(btnSubQuickAction, hasMissing ? "给本阀岛下缺少阀片或 H/W 磁开的气缸一键补齐" : "在当前选中的阀片下再追加一支联动气缸（自动编为 C**-1 / C**-2）");
            } else if (activeSubTab == 1) {
                btnSubQuickAction.Text = subTabsCompact ? "补 H/W" : "补齐本岛 H/W";
                ((FlatButton)btnSubQuickAction).SetFore(Ui.Accent);
                tips.SetToolTip(btnSubQuickAction, "为本阀岛下缺少 H/W 传感器的气缸自动补齐");
            } else if (activeSubTab == 2) {
                btnSubQuickAction.Text = subTabsCompact ? "批量选阀型" : "批量选阀片型号";
                ((FlatButton)btnSubQuickAction).SetFore(Ui.Accent);
                tips.SetToolTip(btnSubQuickAction, "从型号库挑选一款阀片型号，一键填到本阀岛所有非盖板阀片");
            } else {
                btnSubQuickAction.Text = subTabsCompact ? "统一磁开型" : "统一套用磁开型号";
                ((FlatButton)btnSubQuickAction).SetFore(Ui.Accent);
                tips.SetToolTip(btnSubQuickAction, "从型号库挑选一款磁开（型号/品牌/接口），一键填满本岛全部气缸传感器");
            }
            FitButtonExact(btnSubQuickAction, 64);
            if (btnSubQuickAction.Parent != null) btnSubQuickAction.Parent.Width = btnSubQuickAction.Width + Ui.S(4);
        }

        // ==================== 构建并填充当前子页表格 ====================
        void RebuildActiveGrid(string focusRecordId) {
            var p = GetProject();
            if (p == null) return;
            var isl = islands.FirstOrDefault(x => string.Equals(x.Key, selectedIslandKey, StringComparison.OrdinalIgnoreCase));
            bool islandEmpty = currentChannels.Count == 0 && string.IsNullOrEmpty(searchFilter);

            emptyIslandTitle.Text = isl != null ? "阀岛 " + isl.Key + " 已建好，请添加阀片或气缸" : "请先新建阀岛";
            emptyIslandHost.Visible = islandEmpty;
            pGrid.Visible = !islandEmpty;
            if (islandEmpty) { emptyIslandHost.BringToFront(); return; }

            loadingGrid = true;
            bool columnsChanged = false;
            pGrid.SuspendLayout();
            try {
                pGrid.Rows.Clear();
                rowMetas.Clear();

                float scale = zoomPercent / 100f;
                columnsChanged = builtSubTab != activeSubTab || pGrid.Columns.Count == 0;
                if (columnsChanged) {
                    pGrid.Columns.Clear();
                    Func<string, int, int> colW = (name, logical) => {
                        string k = activeSubTab + ":" + name;
                        int bw;
                        if (!baseWidths.TryGetValue(k, out bw)) { bw = Ui.S(logical); baseWidths[k] = bw; }
                        return Math.Max(Ui.S(52), (int)Math.Round(bw * scale));
                    };

                    if (activeSubTab == 0) BuildChannelColumns(colW);
                    else if (activeSubTab == 1) BuildCylinderColumns(colW);
                    else if (activeSubTab == 2) BuildValveColumns(colW);
                    else BuildSensorColumns(colW);
                    builtSubTab = activeSubTab;
                }

                int rowH = Math.Max(Ui.S(22), (int)Math.Round(Ui.S(31) * scale));
                if (pGrid.RowTemplate.Height != rowH) pGrid.RowTemplate.Height = rowH;
                int hdrH = Math.Max(Ui.S(26), (int)Math.Round(Ui.S(33) * scale));
                if (pGrid.ColumnHeadersHeight != hdrH) pGrid.ColumnHeadersHeight = hdrH;

                if (activeSubTab == 0) PopulateChannelRows(rowH);
                else if (activeSubTab == 1) PopulateCylinderRows(rowH);
                else if (activeSubTab == 2) PopulateValveRows(p, rowH);
                else PopulateSensorRows(rowH);
            } finally {
                pGrid.ResumeLayout();
                loadingGrid = false;
            }

            if (pGrid.Columns.Count > 0 && (columnsChanged || lastFitGridW != pGrid.ClientSize.Width || lastFitZoom != zoomPercent || lastFitSubTab != activeSubTab)) {
                FitPGridColumns();
                lastFitGridW = pGrid.ClientSize.Width;
                lastFitZoom = zoomPercent;
                lastFitSubTab = activeSubTab;
            }
            if (!string.IsNullOrEmpty(focusRecordId)) FocusRecordInPGrid(focusRecordId);
            if (OnSelectionChanged != null) OnSelectionChanged(SelectedRecordId);
        }

        void BuildChannelColumns(Func<string, int, int> w) {
            pGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "阀片号", HeaderText = "阀片号", Width = w("阀片号", 124), ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable });
            pGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "阀片类型", HeaderText = "阀片类型 / 盖板", Width = w("阀片类型", 126), SortMode = DataGridViewColumnSortMode.NotSortable });
            pGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "阀片型号", HeaderText = "阀片型号", Width = w("阀片型号", 132), SortMode = DataGridViewColumnSortMode.NotSortable });
            pGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "气缸号", HeaderText = "所控气缸", Width = w("气缸号", 132), ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable });
            pGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "气缸描述", HeaderText = "气缸描述", Width = w("气缸描述", 126), SortMode = DataGridViewColumnSortMode.NotSortable });
            pGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "气缸型号", HeaderText = "气缸型号", Width = w("气缸型号", 128), SortMode = DataGridViewColumnSortMode.NotSortable });
            pGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "品牌", HeaderText = "品牌", Width = w("品牌", 58), SortMode = DataGridViewColumnSortMode.NotSortable });
            pGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "原位", HeaderText = "原位", Width = w("原位", 66), SortMode = DataGridViewColumnSortMode.NotSortable });
            pGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "安装方式", HeaderText = "安装方式", Width = w("安装方式", 78), SortMode = DataGridViewColumnSortMode.NotSortable });
            pGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "磁开", HeaderText = "磁开 H/W", Width = w("磁开", 82), ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable });
            pGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "3D绑定", HeaderText = "3D 绑定", Width = w("3D绑定", 118), ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable });
            pGrid.Columns[0].Frozen = true;
        }

        void BuildCylinderColumns(Func<string, int, int> w) {
            pGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "编号", HeaderText = "气缸编号", Width = w("编号", 136), SortMode = DataGridViewColumnSortMode.NotSortable });
            pGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "描述", HeaderText = "描述", Width = w("描述", 180), SortMode = DataGridViewColumnSortMode.NotSortable });
            pGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "型号", HeaderText = "气缸型号", Width = w("型号", 190), SortMode = DataGridViewColumnSortMode.NotSortable });
            pGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "品牌", HeaderText = "品牌", Width = w("品牌", 86), SortMode = DataGridViewColumnSortMode.NotSortable });
            pGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "原位", HeaderText = "原位", Width = w("原位", 76), SortMode = DataGridViewColumnSortMode.NotSortable });
            pGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "安装方式", HeaderText = "安装方式", Width = w("安装方式", 104), SortMode = DataGridViewColumnSortMode.NotSortable });
            pGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "所属阀片", HeaderText = "所属阀片", Width = w("所属阀片", 126), ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable });
            pGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "备注", HeaderText = "备注 / 模块", Width = w("备注", 132), SortMode = DataGridViewColumnSortMode.NotSortable });
            pGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "3D绑定", HeaderText = "3D 绑定", Width = w("3D绑定", 120), ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable });
            pGrid.Columns[0].Frozen = true;
        }

        void BuildValveColumns(Func<string, int, int> w) {
            pGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "编号", HeaderText = "阀片编号", Width = w("编号", 136), SortMode = DataGridViewColumnSortMode.NotSortable });
            pGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "功能参数", HeaderText = "功能参数（控制对象）", Width = w("功能参数", 170), SortMode = DataGridViewColumnSortMode.NotSortable });
            pGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "描述", HeaderText = "阀片类型描述", Width = w("描述", 160), SortMode = DataGridViewColumnSortMode.NotSortable });
            pGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "型号", HeaderText = "阀片型号", Width = w("型号", 180), SortMode = DataGridViewColumnSortMode.NotSortable });
            pGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "品牌", HeaderText = "品牌", Width = w("品牌", 86), SortMode = DataGridViewColumnSortMode.NotSortable });
            pGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "所属阀岛", HeaderText = "所属阀岛配置", Width = w("所属阀岛", 170), ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable });
            pGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "备注", HeaderText = "备注", Width = w("备注", 124), SortMode = DataGridViewColumnSortMode.NotSortable });
            pGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "3D绑定", HeaderText = "3D 绑定", Width = w("3D绑定", 114), ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable });
            pGrid.Columns[0].Frozen = true;
        }

        void BuildSensorColumns(Func<string, int, int> w) {
            pGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "编号", HeaderText = "传感器编号", Width = w("编号", 148), SortMode = DataGridViewColumnSortMode.NotSortable });
            pGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "位置", HeaderText = "位置", Width = w("位置", 60), ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable });
            pGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "描述", HeaderText = "描述", Width = w("描述", 175), SortMode = DataGridViewColumnSortMode.NotSortable });
            pGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "型号", HeaderText = "磁开型号", Width = w("型号", 170), SortMode = DataGridViewColumnSortMode.NotSortable });
            pGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "品牌", HeaderText = "品牌", Width = w("品牌", 82), SortMode = DataGridViewColumnSortMode.NotSortable });
            pGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "接口类型", HeaderText = "传感器接口类型", Width = w("接口类型", 136), SortMode = DataGridViewColumnSortMode.NotSortable });
            pGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "所属气缸", HeaderText = "所属气缸", Width = w("所属气缸", 126), ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable });
            pGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "备注", HeaderText = "备注", Width = w("备注", 120), SortMode = DataGridViewColumnSortMode.NotSortable });
            pGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "3D绑定", HeaderText = "3D 绑定", Width = w("3D绑定", 110), ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable });
            pGrid.Columns[0].Frozen = true;
        }

        bool MatchSearch(params string[] vals) {
            if (searchFilter.Length == 0) return true;
            foreach (var v in vals)
                if (!string.IsNullOrEmpty(v) && v.IndexOf(searchFilter, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        static string Format3D(LineupRecord r) {
            if (r == null || string.IsNullOrEmpty(r.ReferenceKey)) return "";
            string raw = string.Join(" / ", r.InstancePath ?? new string[0]);
            int slash = raw.LastIndexOf('/');
            return slash >= 0 ? raw.Substring(slash + 1).Trim() : raw;
        }

        static string FormatSensorSummary(LineupChannelRow ch) {
            if (ch.Cylinder == null) return "—";
            if (ch.IsBlowOrVacuum && ch.Sensors.Count == 0) return "无需磁开";
            if (ch.Sensors.Count == 0) return "缺 H/W";
            var pos = ch.Sensors.Select(s => (s.Position ?? "").ToUpperInvariant()).Where(x => x.Length > 0).ToList();
            if (ch.Sensors.Count >= 4) return "✓ 双组 (" + ch.Sensors.Count + ")";
            bool hasH = ch.Sensors.Any(s => (s.Number ?? "").EndsWith("H", StringComparison.OrdinalIgnoreCase) || (s.Position ?? "").StartsWith("H", StringComparison.OrdinalIgnoreCase));
            bool hasW = ch.Sensors.Any(s => (s.Number ?? "").EndsWith("W", StringComparison.OrdinalIgnoreCase) || (s.Position ?? "").StartsWith("W", StringComparison.OrdinalIgnoreCase));
            if (hasH && hasW) return "✓ H / W";
            return "缺 " + (hasH ? "W" : "H");
        }

        void PopulateChannelRows(int rowH) {
            var batch = new List<DataGridViewRow>(currentChannels.Count);
            foreach (var ch in currentChannels) {
                var v = ch.Valve;
                var c = ch.Cylinder;
                if (!MatchSearch(v == null ? "" : v.Number, v == null ? "" : v.Description, v == null ? "" : v.Model,
                    c == null ? "" : c.Number, c == null ? "" : c.Description, c == null ? "" : c.Model, c == null ? "" : c.Brand, c == null ? "" : c.Notes))
                    continue;

                string vNum = v == null ? "待补阀片" : (ch.IsContinuation ? "↳ 同 " + ShortSlot(v.Number) : v.Number);
                string vDesc = v == null ? "" : (ch.IsContinuation ? "" : v.Description);
                string vModel = v == null ? "" : (ch.IsContinuation ? "" : v.Model);
                string cNum = c == null ? (ch.IsSpare ? "— 预留位" : "— 空阀片") : c.Number;
                string cDesc = c == null ? "" : c.Description;
                string cModel = c == null ? "" : c.Model;
                string cBrand = c == null ? "" : c.Brand;
                string cHome = c == null ? "" : c.Home;
                string cMount = c == null ? "" : c.Mounting;
                string senTxt = FormatSensorSummary(ch);
                string bindTxt = Format3D(c ?? v);

                var row = new DataGridViewRow();
                row.CreateCells(pGrid, vNum, vDesc, vModel, cNum, cDesc, cModel, cBrand, cHome, cMount, senTxt, bindTxt);
                row.Height = rowH;
                if (ch.IsContinuation || v == null) {
                    row.Cells[1].ReadOnly = true;
                    row.Cells[2].ReadOnly = true;
                }
                if (c == null) {
                    for (int ci = 4; ci <= 8; ci++) row.Cells[ci].ReadOnly = true;
                }
                batch.Add(row);
                rowMetas.Add(new GridRowMeta {
                    Channel = ch,
                    Primary = c ?? v,
                    ValveId = v == null ? "" : v.Id,
                    CylinderId = c == null ? "" : c.Id,
                    IsContinuation = ch.IsContinuation,
                    IsSpare = ch.IsSpare,
                    IsBlowOrVacuum = ch.IsBlowOrVacuum
                });
            }
            if (batch.Count > 0) pGrid.Rows.AddRange(batch.ToArray());
        }

        static string ShortSlot(string fullNum) {
            if (string.IsNullOrEmpty(fullNum)) return "";
            int dash = fullNum.LastIndexOf('-');
            return dash >= 0 && dash + 1 < fullNum.Length ? fullNum.Substring(dash + 1) : fullNum;
        }

        void PopulateCylinderRows(int rowH) {
            var batch = new List<DataGridViewRow>(currentChannels.Count);
            foreach (var ch in currentChannels) {
                var c = ch.Cylinder;
                if (c == null) continue;
                if (!MatchSearch(c.Number, c.Description, c.Model, c.Brand, c.Notes, ch.Valve == null ? "" : ch.Valve.Number)) continue;
                var row = new DataGridViewRow();
                row.CreateCells(pGrid, c.Number, c.Description, c.Model, c.Brand, c.Home, c.Mounting, ch.Valve == null ? "待补阀片" : ch.Valve.Number, c.Notes, Format3D(c));
                row.Height = rowH;
                batch.Add(row);
                rowMetas.Add(new GridRowMeta { Channel = ch, Primary = c, ValveId = ch.Valve == null ? "" : ch.Valve.Id, CylinderId = c.Id, IsBlowOrVacuum = ch.IsBlowOrVacuum });
            }
            if (batch.Count > 0) pGrid.Rows.AddRange(batch.ToArray());
        }

        void PopulateValveRows(LineupProject p, int rowH) {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var batch = new List<DataGridViewRow>(currentChannels.Count);
            foreach (var ch in currentChannels) {
                var v = ch.Valve;
                if (v == null || !seen.Add(v.Id)) continue;
                if (!MatchSearch(v.Number, v.Parameters, v.Description, v.Model, v.Brand, v.Notes)) continue;
                string islLbl = LineupNumbering.IslandLabel(p, v);
                if (string.IsNullOrWhiteSpace(islLbl)) islLbl = selectedIslandKey;
                var row = new DataGridViewRow();
                row.CreateCells(pGrid, v.Number, v.Parameters, v.Description, v.Model, v.Brand, islLbl, v.Notes, Format3D(v));
                row.Height = rowH;
                batch.Add(row);
                rowMetas.Add(new GridRowMeta { Channel = ch, Primary = v, ValveId = v.Id, CylinderId = ch.Cylinder == null ? "" : ch.Cylinder.Id, IsSpare = ch.IsSpare });
            }
            if (batch.Count > 0) pGrid.Rows.AddRange(batch.ToArray());
        }

        void PopulateSensorRows(int rowH) {
            var batch = new List<DataGridViewRow>(currentChannels.Count * 2);
            foreach (var ch in currentChannels) {
                foreach (var s in ch.Sensors) {
                    if (!MatchSearch(s.Number, s.Position, s.Description, s.Model, s.Brand, s.Connector, s.Notes, ch.Cylinder == null ? "" : ch.Cylinder.Number)) continue;
                    var row = new DataGridViewRow();
                    row.CreateCells(pGrid, s.Number, s.Position, s.Description, s.Model, s.Brand, s.Connector, ch.Cylinder == null ? "" : ch.Cylinder.Number, s.Notes, Format3D(s));
                    row.Height = rowH;
                    batch.Add(row);
                    rowMetas.Add(new GridRowMeta {
                        Channel = ch, Primary = s,
                        ValveId = ch.Valve == null ? "" : ch.Valve.Id,
                        CylinderId = ch.Cylinder == null ? "" : ch.Cylinder.Id,
                        SensorId = s.Id
                    });
                }
            }
            if (batch.Count > 0) pGrid.Rows.AddRange(batch.ToArray());
        }

        void FocusRecordInPGrid(string recordId) {
            int hit = -1;
            for (int i = 0; i < rowMetas.Count && i < pGrid.Rows.Count; i++) {
                var m = rowMetas[i];
                if (m.Primary != null && m.Primary.Id == recordId) { hit = i; break; }
            }
            if (hit < 0) {
                for (int i = 0; i < rowMetas.Count && i < pGrid.Rows.Count; i++) {
                    var m = rowMetas[i];
                    if (m.CylinderId == recordId || m.ValveId == recordId || m.SensorId == recordId) { hit = i; break; }
                }
            }
            if (hit >= 0) {
                var m = rowMetas[hit];
                int targetCol = activeSubTab == 0 ? (string.IsNullOrEmpty(m.CylinderId) ? 1 : 4) : 1;
                if (targetCol < pGrid.Columns.Count) {
                    pGrid.ClearSelection();
                    pGrid.CurrentCell = pGrid.Rows[hit].Cells[targetCol];
                    pGrid.Rows[hit].Cells[targetCol].Selected = true;
                    try { pGrid.FirstDisplayedScrollingRowIndex = Math.Max(0, hit - 2); } catch { }
                }
            }
        }

        List<int> SelectedPGridRowIndices() {
            var set = new SortedSet<int>();
            foreach (DataGridViewCell cell in pGrid.SelectedCells)
                if (cell.RowIndex >= 0 && cell.RowIndex < rowMetas.Count) set.Add(cell.RowIndex);
            foreach (DataGridViewRow row in pGrid.SelectedRows)
                if (row.Index >= 0 && row.Index < rowMetas.Count) set.Add(row.Index);
            return set.ToList();
        }

        List<string> SelectedPrimaryRecordIds(int fallbackRowIndex) {
            var ids = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (int ri in SelectedPGridRowIndices()) {
                var m = rowMetas[ri];
                if (m.Primary != null && !string.IsNullOrEmpty(m.Primary.Id) && seen.Add(m.Primary.Id))
                    ids.Add(m.Primary.Id);
            }
            if (ids.Count == 0 && fallbackRowIndex >= 0 && fallbackRowIndex < rowMetas.Count) {
                var fm = rowMetas[fallbackRowIndex];
                if (fm.Primary != null && !string.IsNullOrEmpty(fm.Primary.Id)) ids.Add(fm.Primary.Id);
            }
            return ids;
        }

        internal string CurrentIslandKey {
            get { return (orphanHost.Visible || noIslandHost.Visible) ? "" : selectedIslandKey; }
        }

        internal int CurrentSubTab { get { return activeSubTab; } }

        internal List<string> SelectedPrimaryIdsForMove(out int activeCol) {
            EndEdit();
            activeCol = pGrid.CurrentCell != null ? pGrid.CurrentCell.ColumnIndex : -1;
            int fallback = pGrid.CurrentRow != null ? pGrid.CurrentRow.Index : -1;
            return SelectedPrimaryRecordIds(fallback);
        }

        internal void RestoreMoveSelection(IList<string> recordIds, int preferredCol) {
            if (recordIds == null || recordIds.Count == 0 || pGrid.Rows.Count == 0 || pGrid.Columns.Count == 0) return;
            var idSet = new HashSet<string>(recordIds, StringComparer.Ordinal);
            var matched = new List<int>();
            for (int i = 0; i < rowMetas.Count && i < pGrid.Rows.Count; i++) {
                var m = rowMetas[i];
                if (m.Primary != null && idSet.Contains(m.Primary.Id)) matched.Add(i);
            }
            if (matched.Count == 0) return;
            int col = (preferredCol >= 0 && preferredCol < pGrid.Columns.Count && pGrid.Columns[preferredCol].Visible)
                ? preferredCol
                : (activeSubTab == 0 ? (string.IsNullOrEmpty(rowMetas[matched[0]].CylinderId) ? 1 : 4) : 1);
            if (col >= pGrid.Columns.Count) col = 0;
            pGrid.ClearSelection();
            pGrid.CurrentCell = pGrid.Rows[matched[0]].Cells[col];
            foreach (int ri in matched) pGrid.Rows[ri].Cells[col].Selected = true;
            try {
                int first = matched[0];
                if (first < pGrid.FirstDisplayedScrollingRowIndex || first >= pGrid.FirstDisplayedScrollingRowIndex + Math.Max(1, pGrid.DisplayedRowCount(false)))
                    pGrid.FirstDisplayedScrollingRowIndex = Math.Max(0, first - 2);
            } catch { }
        }

        GridRowMeta CurrentRowMeta() {
            if (pGrid.CurrentRow == null) return null;
            int idx = pGrid.CurrentRow.Index;
            return idx >= 0 && idx < rowMetas.Count ? rowMetas[idx] : null;
        }

        // ==================== 单元格样式、类 Excel 编辑与自动补全 ====================
        void OnPGridCellFormatting(object sender, DataGridViewCellFormattingEventArgs e) {
            if (e.RowIndex < 0 || e.RowIndex >= rowMetas.Count || e.ColumnIndex < 0) return;
            var meta = rowMetas[e.RowIndex];
            string colName = pGrid.Columns[e.ColumnIndex].Name;
            string raw = Convert.ToString(e.Value ?? "").Trim();

            if (activeSubTab == 0) {
                if (meta.IsSpare) e.CellStyle.BackColor = SpareRowBg;
                else if (meta.IsContinuation) e.CellStyle.BackColor = ContRowBg;

                if (colName == "阀片号") {
                    if (string.IsNullOrEmpty(meta.ValveId)) { e.CellStyle.ForeColor = WarnText; e.CellStyle.Font = cellBoldFont; }
                    else if (meta.IsContinuation) { e.CellStyle.ForeColor = Ui.Accent; }
                    else { e.CellStyle.Font = cellBoldFont; e.CellStyle.ForeColor = Ui.Text; }
                    return;
                }
                if (colName == "气缸号") {
                    if (string.IsNullOrEmpty(meta.CylinderId)) e.CellStyle.ForeColor = Ui.Faint;
                    else { e.CellStyle.Font = cellBoldFont; e.CellStyle.ForeColor = Ui.Text; }
                    return;
                }
                if (colName == "磁开") {
                    if (raw.StartsWith("✓", StringComparison.Ordinal)) e.CellStyle.ForeColor = Ui.Ok;
                    else if (raw.StartsWith("缺", StringComparison.Ordinal)) { e.CellStyle.ForeColor = WarnText; e.CellStyle.BackColor = WarmTintBg; }
                    else e.CellStyle.ForeColor = Ui.Faint;
                    return;
                }
                if (colName == "气缸描述" && string.IsNullOrEmpty(meta.CylinderId)) {
                    e.Value = meta.IsSpare ? "预留盖板（右键挂气缸）" : "空阀片（右键挂气缸）";
                    e.CellStyle.ForeColor = Ui.Faint;
                    e.FormattingApplied = true;
                    return;
                }
                bool editingThis = pGrid.IsCurrentCellInEditMode && pGrid.CurrentCell != null
                    && pGrid.CurrentCell.RowIndex == e.RowIndex && pGrid.CurrentCell.ColumnIndex == e.ColumnIndex;
                if (!editingThis && raw.Length == 0) {
                    if (colName == "气缸型号" && !string.IsNullOrEmpty(meta.CylinderId)) {
                        e.Value = "双击选型号"; e.CellStyle.ForeColor = Ui.Faint; e.CellStyle.BackColor = WarmTintBg; e.FormattingApplied = true; return;
                    }
                    if (colName == "阀片型号" && !string.IsNullOrEmpty(meta.ValveId) && !meta.IsContinuation && !meta.IsSpare) {
                        e.Value = "双击选型号"; e.CellStyle.ForeColor = Ui.Faint; e.CellStyle.BackColor = WarmTintBg; e.FormattingApplied = true; return;
                    }
                    if (colName == "气缸描述" && !string.IsNullOrEmpty(meta.CylinderId)) {
                        e.CellStyle.BackColor = WarmTintBg; return;
                    }
                }
            } else {
                if (colName == "编号") {
                    if (raw.Length > 0 && cellBoldFont != null) e.CellStyle.Font = cellBoldFont;
                    else e.CellStyle.BackColor = WarmTintBg;
                    return;
                }
                if (colName == "所属阀片" && raw == "待补阀片") { e.CellStyle.ForeColor = WarnText; return; }
                bool editingThis = pGrid.IsCurrentCellInEditMode && pGrid.CurrentCell != null
                    && pGrid.CurrentCell.RowIndex == e.RowIndex && pGrid.CurrentCell.ColumnIndex == e.ColumnIndex;
                if (!editingThis && raw.Length == 0 && colName == "型号" && !meta.IsSpare) {
                    e.Value = "双击选型号"; e.CellStyle.ForeColor = Ui.Faint; e.CellStyle.BackColor = WarmTintBg; e.FormattingApplied = true; return;
                }
            }

            if (colName == "3D绑定") {
                if (raw.Length == 0) { e.Value = "未绑定"; e.CellStyle.ForeColor = Ui.Faint; }
                else { e.Value = "✓ " + raw; e.CellStyle.ForeColor = Ui.Ok; }
                e.FormattingApplied = true;
            }
        }

        void OnPGridCellMouseUp(object sender, DataGridViewCellMouseEventArgs e) {
            if (e.RowIndex < 0) return;
            if (e.Button == MouseButtons.Right) {
                var selRows = SelectedPGridRowIndices();
                bool clickedInSelectedRow = selRows.Contains(e.RowIndex);
                if (e.ColumnIndex < 0) {
                    if (!clickedInSelectedRow) {
                        pGrid.ClearSelection();
                        pGrid.Rows[e.RowIndex].Selected = true;
                        if (pGrid.Columns.Count > 0) pGrid.CurrentCell = pGrid.Rows[e.RowIndex].Cells[0];
                    }
                    ShowRowContextMenu(e.RowIndex, 0);
                    return;
                }
                if (!pGrid.Rows[e.RowIndex].Cells[e.ColumnIndex].Selected && !(selRows.Count > 1 && clickedInSelectedRow)) {
                    pGrid.ClearSelection();
                    pGrid.CurrentCell = pGrid.Rows[e.RowIndex].Cells[e.ColumnIndex];
                    pGrid.Rows[e.RowIndex].Cells[e.ColumnIndex].Selected = true;
                }
                ShowRowContextMenu(e.RowIndex, e.ColumnIndex);
                return;
            }
            // 默认单击只选中单元格、不强制进入编辑态；直接打字（含中文输入法）、双击或按 F2 自动进入编辑
        }

        void OnPGridCellDoubleClick(object sender, DataGridViewCellEventArgs e) {
            if (e.RowIndex < 0 || e.RowIndex >= rowMetas.Count || e.ColumnIndex < 0) return;
            var meta = rowMetas[e.RowIndex];
            string colName = pGrid.Columns[e.ColumnIndex].Name;
            if (colName == "阀片型号" && !string.IsNullOrEmpty(meta.ValveId) && !meta.IsContinuation) {
                Try(() => { if (OnPickModelForRecords != null) OnPickModelForRecords("阀片", new[] { meta.ValveId }); });
            } else if (colName == "气缸型号" && !string.IsNullOrEmpty(meta.CylinderId)) {
                Try(() => { if (OnPickModelForRecords != null) OnPickModelForRecords("气缸", new[] { meta.CylinderId }); });
            } else if (colName == "型号") {
                string cat = activeSubTab == 1 ? "气缸" : activeSubTab == 2 ? "阀片" : "气缸传感器";
                if (meta.Primary != null && OnPickModelForRecords != null) Try(() => OnPickModelForRecords(cat, new[] { meta.Primary.Id }));
            } else if (colName == "磁开" && !string.IsNullOrEmpty(meta.CylinderId)) {
                Try(() => CycleSensorModeForCylinder(meta));
            } else if (colName == "3D绑定" && meta.Primary != null) {
                Try(() => {
                    if (!string.IsNullOrEmpty(meta.Primary.ReferenceKey)) {
                        if (OnLocateRecord != null) OnLocateRecord(meta.Primary.Id);
                    } else if (OnStartSequentialBindRecords != null) {
                        OnStartSequentialBindRecords(new[] { meta.Primary.Id });
                    } else if (OnBindCurrentCadToRecord != null) {
                        OnBindCurrentCadToRecord(meta.Primary.Id);
                    }
                });
            }
        }

        void OnPGridEditingControlShowing(object sender, DataGridViewEditingControlShowingEventArgs e) {
            var box = e.Control as TextBox;
            if (box == null || pGrid.CurrentCell == null) return;
            if ((box.Tag as string) != "pgrid-hooked") {
                box.Tag = "pgrid-hooked";
                box.MouseWheel += HandleGridWheel;
                box.DoubleClick += (bs, be) => {
                    if (pGrid.CurrentCell == null) return;
                    string cn = pGrid.Columns[pGrid.CurrentCell.ColumnIndex].Name;
                    if (cn == "型号" || cn == "气缸型号" || cn == "阀片型号") {
                        int r = pGrid.CurrentCell.RowIndex, c = pGrid.CurrentCell.ColumnIndex;
                        pGrid.EndEdit();
                        OnPGridCellDoubleClick(pGrid, new DataGridViewCellEventArgs(c, r));
                    }
                };
            }

            // 缓存每类补全集合，且同类型单元格连续编辑时跳过重设 Shell COM IAutoComplete2
            string colName = pGrid.Columns[pGrid.CurrentCell.ColumnIndex].Name;
            string acKey = "";
            if (colName == "阀片类型" || (activeSubTab == 2 && colName == "描述")) acKey = "preset:valve";
            else if (colName == "原位") acKey = "preset:home";
            else if (colName == "安装方式") acKey = "preset:mount";
            else if (colName == "接口类型") acKey = "preset:conn";
            else if (colName == "阀片型号" || colName == "气缸型号" || colName == "型号") {
                string kind = colName == "阀片型号" ? "阀片" : colName == "气缸型号" ? "气缸"
                    : activeSubTab == 1 ? "气缸" : activeSubTab == 2 ? "阀片" : "气缸传感器";
                acKey = "model:" + kind;
            }
            if (string.Equals(lastAcKey, acKey, StringComparison.Ordinal)) return;
            lastAcKey = acKey;
            if (acKey.Length > 0) {
                AutoCompleteStringCollection ac;
                if (!acCache.TryGetValue(acKey, out ac)) {
                    ac = new AutoCompleteStringCollection();
                    if (acKey == "preset:valve") ac.AddRange(LineupPresets.ValveDescriptions);
                    else if (acKey == "preset:home") ac.AddRange(LineupPresets.Homes);
                    else if (acKey == "preset:mount") ac.AddRange(LineupPresets.CylinderMountings);
                    else if (acKey == "preset:conn") ac.AddRange(LineupPresets.Connectors);
                    else if (acKey.StartsWith("model:", StringComparison.Ordinal)) {
                        var cat = GetCatalog != null ? GetCatalog() : null;
                        if (cat != null) {
                            string kind = acKey.Substring(6);
                            ac.AddRange(cat.Entries.Where(x => x.Category == kind).Select(x => x.Model).Distinct().ToArray());
                        }
                    }
                    acCache[acKey] = ac;
                }
                if (ac.Count > 0) {
                    box.AutoCompleteCustomSource = ac;
                    box.AutoCompleteSource = AutoCompleteSource.CustomSource;
                    box.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
                    return;
                }
            }
            if (box.AutoCompleteMode != AutoCompleteMode.None) box.AutoCompleteMode = AutoCompleteMode.None;
            if (box.AutoCompleteSource != AutoCompleteSource.None) box.AutoCompleteSource = AutoCompleteSource.None;
        }

        void OnPGridCellEndEdit(object sender, DataGridViewCellEventArgs e) {
            if (loadingGrid || e.RowIndex < 0 || e.RowIndex >= rowMetas.Count || e.ColumnIndex < 0) return;
            string after = Convert.ToString(pGrid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value ?? "");
            if (string.Equals(after, editStartVal, StringComparison.Ordinal)) return;
            CommitCellEdit(e.RowIndex, e.ColumnIndex, after);
        }

        void CommitCellEdit(int rowIndex, int colIndex, string after) {
            var meta = rowMetas[rowIndex];
            string colName = pGrid.Columns[colIndex].Name;
            string recId = "";
            string field = "";
            bool lookupModel = false;

            if (activeSubTab == 0) {
                if (colName == "阀片类型") { recId = meta.ValveId; field = "描述"; }
                else if (colName == "阀片型号") { recId = meta.ValveId; field = "型号"; lookupModel = true; }
                else if (colName == "气缸描述") { recId = meta.CylinderId; field = "气缸描述联动"; }
                else if (colName == "气缸型号") { recId = meta.CylinderId; field = "型号"; lookupModel = true; }
                else if (colName == "品牌") { recId = meta.CylinderId; field = "品牌"; }
                else if (colName == "原位") { recId = meta.CylinderId; field = "原位"; }
                else if (colName == "安装方式") { recId = meta.CylinderId; field = "安装方式"; }
            } else {
                recId = meta.Primary == null ? "" : meta.Primary.Id;
                if (colName == "编号") field = "编号";
                else if (colName == "功能参数") field = "功能参数";
                else if (colName == "描述") field = activeSubTab == 1 ? "气缸描述联动" : "描述";
                else if (colName == "型号") { field = "型号"; lookupModel = true; }
                else if (colName == "品牌") field = "品牌";
                else if (colName == "原位") field = "原位";
                else if (colName == "安装方式") field = "安装方式";
                else if (colName == "接口类型") field = "传感器接口类型";
                else if (colName == "备注") field = "备注";
            }

            if (recId.Length == 0 || field.Length == 0 || ApplyInlineField == null) return;
            Try(() => {
                ApplyInlineField(recId, field, after, lookupModel);
                // 若自动补全了品牌或接口，回显到当前行（不触发整表刷新）
                if (lookupModel) SyncAutoFilledRowDisplay(rowIndex, recId);
            });
        }

        void SyncAutoFilledRowDisplay(int rowIndex, string recId) {
            var p = GetProject != null ? GetProject() : null;
            if (p == null || rowIndex < 0 || rowIndex >= pGrid.Rows.Count) return;
            var r = p.Records.FirstOrDefault(x => x.Id == recId);
            if (r == null) return;
            loadingGrid = true;
            try {
                if (activeSubTab == 0 && r.Category == "气缸") {
                    pGrid.Rows[rowIndex].Cells["品牌"].Value = r.Brand ?? "";
                } else if (activeSubTab != 0) {
                    if (pGrid.Columns.Contains("品牌")) pGrid.Rows[rowIndex].Cells["品牌"].Value = r.Brand ?? "";
                    if (pGrid.Columns.Contains("接口类型")) pGrid.Rows[rowIndex].Cells["接口类型"].Value = r.Connector ?? "";
                }
            } finally { loadingGrid = false; }
        }

        public void CopySelectedCells() { Try(CopySelectedPGridCells); }
        public void PasteSelectedCells(string text) { Try(() => PasteSelectedPGridCells(text)); }
        public void FillDownSelectedCells() { Try(FillDownPGrid); }
        public void ClearSelectedCells() { Try(ClearSelectedPGridCells); }

        void CopySelectedPGridCells() {
            var dataObj = pGrid.GetClipboardContent();
            if (dataObj != null) {
                string t = dataObj.GetText();
                if (t != null) ClipboardUtil.SetText(t);
            }
        }

        static bool IsNumberColumn(string colName) {
            return colName == "编号" || colName == "阀片号" || colName == "气缸号";
        }

        void PasteSelectedPGridCells(string text) {
            if (string.IsNullOrEmpty(text) || pGrid.CurrentCell == null) return;
            var block = LineupTableData.ReadDelimited(text, '\t');
            if (block.Count == 0) return;
            var editableTargets = pGrid.SelectedCells.Cast<DataGridViewCell>().Where(c => c.RowIndex >= 0 && !c.ReadOnly).ToList();
            if (block.Count == 1 && block[0].Length == 1 && editableTargets.Count > 1) {
                if (editableTargets.Any(c => IsNumberColumn(pGrid.Columns[c.ColumnIndex].Name)))
                    throw new InvalidOperationException("编号列不支持批量填入相同值。");
                foreach (var c in editableTargets) {
                    c.Value = block[0][0];
                    CommitCellEdit(c.RowIndex, c.ColumnIndex, block[0][0]);
                }
                return;
            }
            var visCols = pGrid.Columns.Cast<DataGridViewColumn>().Where(c => c.Visible).OrderBy(c => c.DisplayIndex).ToList();
            int top = pGrid.CurrentCell.RowIndex;
            int start = visCols.IndexOf(pGrid.CurrentCell.OwningColumn);
            if (start < 0) return;

            // 若粘贴块触及「编号」列，先做原子重号预检，避免半途写入重复编号导致自动保存失败且无法回滚
            var pendingNumberUpdates = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int y = 0; y < block.Count && top + y < pGrid.Rows.Count; y++) {
                for (int x = 0; x < block[y].Length && start + x < visCols.Count; x++) {
                    var col = visCols[start + x];
                    var cell = pGrid.Rows[top + y].Cells[col.Index];
                    if (cell.ReadOnly || !IsNumberColumn(col.Name)) continue;
                    int ri = cell.RowIndex;
                    if (ri < 0 || ri >= rowMetas.Count || rowMetas[ri].Primary == null) continue;
                    string nv = (block[y][x] ?? "").Trim();
                    if (nv.Length == 0) throw new InvalidOperationException("编号列不能为空，已取消粘贴。");
                    pendingNumberUpdates[rowMetas[ri].Primary.Id] = nv;
                }
            }
            if (pendingNumberUpdates.Count > 0) {
                var usedNums = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var proj = GetProject != null ? GetProject() : null;
                if (proj != null && proj.Records != null) {
                    foreach (var r in proj.Records) {
                        if (pendingNumberUpdates.ContainsKey(r.Id)) continue;
                        string rn = (r.Number ?? "").Trim();
                        if (rn.Length > 0) usedNums.Add(rn);
                    }
                }
                foreach (var kv in pendingNumberUpdates) {
                    if (!usedNums.Add(kv.Value))
                        throw new InvalidOperationException("粘贴包含重复编号：" + kv.Value + "，已取消此次粘贴。");
                }
            }

            for (int y = 0; y < block.Count && top + y < pGrid.Rows.Count; y++) {
                for (int x = 0; x < block[y].Length && start + x < visCols.Count; x++) {
                    var cell = pGrid.Rows[top + y].Cells[visCols[start + x].Index];
                    if (cell.ReadOnly) continue;
                    cell.Value = block[y][x];
                    CommitCellEdit(cell.RowIndex, cell.ColumnIndex, block[y][x]);
                }
            }
        }

        void ClearSelectedPGridCells() {
            var allEditable = pGrid.SelectedCells.Cast<DataGridViewCell>().Where(c => c.RowIndex >= 0 && !c.ReadOnly).ToList();
            if (allEditable.Count == 0) return;
            var cells = allEditable.Where(c => !IsNumberColumn(pGrid.Columns[c.ColumnIndex].Name)).ToList();
            if (cells.Count == 0)
                throw new InvalidOperationException("编号列不支持直接清空，请双击修改或右键删除整行。");
            foreach (var c in cells) {
                c.Value = "";
                CommitCellEdit(c.RowIndex, c.ColumnIndex, "");
            }
        }

        void FillDownPGrid() {
            var cells = pGrid.SelectedCells.Cast<DataGridViewCell>().Where(c => c.RowIndex >= 0 && !c.ReadOnly).ToArray();
            if (cells.Length < 2) throw new InvalidOperationException("请先在同一列选中多行单元格，再按 Ctrl+D 向下填充。");
            foreach (var grp in cells.GroupBy(c => c.ColumnIndex)) {
                string cn = pGrid.Columns[grp.Key].Name;
                if (cn == "编号" || cn == "阀片号" || cn == "气缸号") throw new InvalidOperationException("编号列不支持向下填充。");
                var ordered = grp.OrderBy(c => c.RowIndex).ToArray();
                string val = Convert.ToString(ordered[0].Value ?? "");
                foreach (var c in ordered.Skip(1)) {
                    c.Value = val;
                    CommitCellEdit(c.RowIndex, c.ColumnIndex, val);
                }
            }
        }

        // ==================== 右键上下文菜单 ====================
        internal void BuildRowContextMenu(int rowIndex, int colIndex) {
            if (rowIndex < 0 || rowIndex >= rowMetas.Count) return;
            var meta = rowMetas[rowIndex];
            var bindIds = SelectedPrimaryRecordIds(rowIndex);
            Ui.DisposeItems(rowMenu.Items);

            Action<string, Action, string, bool> add = (t, act, tone, enabled) => {
                var mi = new ToolStripMenuItem(t);
                if (!string.IsNullOrEmpty(tone)) mi.Tag = tone;
                mi.Enabled = enabled;
                mi.Click += (s, e) => Try(act);
                rowMenu.Items.Add(mi);
            };

            if (!string.IsNullOrEmpty(meta.ValveId)) {
                string label = string.IsNullOrEmpty(meta.CylinderId)
                    ? "+ 给此阀片挂载气缸（自动配 H/W）"
                    : "+ 追加同阀气缸（一阀控多缸 C**-1 / C**-2）";
                add(label, () => AddCylinderToSelectedValve(meta.ValveId), null, true);
            } else if (!string.IsNullOrEmpty(meta.CylinderId)) {
                add("+ 为此气缸生成配套阀片 (V**)", () => GenerateValveForSingleCylinder(meta.CylinderId), null, true);
            }

            if (!string.IsNullOrEmpty(meta.CylinderId)) {
                var senSub = new ToolStripMenuItem("磁开 (H/W) 配置");
                Action<string, int> addSen = (t, pairs) => {
                    var mi = new ToolStripMenuItem(t);
                    mi.Click += (s, e) => Try(() => SetSensorMode(meta.CylinderId, pairs));
                    senSub.DropDownItems.Add(mi);
                };
                addSen("标准双磁开（H 原位 + W 工作位）", 1);
                addSen("双组磁开（H1 / W1 / H2 / W2）", 2);
                addSen("清除磁开（吹气 / 破真空无磁开）", 0);
                rowMenu.Items.Add(senSub);
            } else if (!string.IsNullOrEmpty(meta.ValveId) && !meta.IsSpare) {
                add("标记为预留位 / 盖板", () => MarkValveAsSpare(meta.ValveId), null, true);
            }

            rowMenu.Items.Add(new ToolStripSeparator());
            if (bindIds.Count > 1 && activeSubTab >= 1 && activeSubTab <= 3) {
                string cat = activeSubTab == 1 ? "气缸" : activeSubTab == 2 ? "阀片" : "气缸传感器";
                string catShort = activeSubTab == 1 ? "气缸" : activeSubTab == 2 ? "阀片" : "磁开";
                add("从型号库批量选" + catShort + "型号（" + bindIds.Count + " 行）…",
                    () => { if (OnPickModelForRecords != null) OnPickModelForRecords(cat, bindIds); }, null, true);
            } else {
                if (!string.IsNullOrEmpty(meta.CylinderId))
                    add("从型号库选气缸型号…", () => { if (OnPickModelForRecords != null) OnPickModelForRecords("气缸", new[] { meta.CylinderId }); }, null, true);
                if (!string.IsNullOrEmpty(meta.ValveId))
                    add("从型号库选阀片型号…", () => { if (OnPickModelForRecords != null) OnPickModelForRecords("阀片", new[] { meta.ValveId }); }, null, true);
                if (!string.IsNullOrEmpty(meta.SensorId))
                    add("从型号库选磁开型号…", () => { if (OnPickModelForRecords != null) OnPickModelForRecords("气缸传感器", new[] { meta.SensorId }); }, null, true);
            }

            if (!string.IsNullOrEmpty(meta.ValveId) && islands.Count > 1)
                add("将此阀路移动到其他阀岛…", () => MoveValveDialog(meta.ValveId), null, true);

            if (pGrid.Rows.Count > 1 && OnMoveSelectedRows != null) {
                rowMenu.Items.Add(new ToolStripSeparator());
                add("上移选中行 (Alt+↑)", () => OnMoveSelectedRows(-1), null, true);
                add("下移选中行 (Alt+↓)", () => OnMoveSelectedRows(1), null, true);
            }

            if (bindIds.Count > 0) {
                rowMenu.Items.Add(new ToolStripSeparator());
                if (bindIds.Count == 1) {
                    add("进入 3D 点选模型绑定到此行", () => { if (OnStartSequentialBindRecords != null) OnStartSequentialBindRecords(bindIds); }, "accent", true);
                    add("绑定 CAD 当前选中件到此行", () => {
                        if (OnBindCurrentCadToRecords != null) OnBindCurrentCadToRecords(bindIds);
                        else if (OnBindCurrentCadToRecord != null) OnBindCurrentCadToRecord(bindIds[0]);
                    }, null, true);
                    bool bound = meta.Primary != null && !string.IsNullOrEmpty(meta.Primary.ReferenceKey);
                    add("在 CAD 中定位模型", () => { if (OnLocateRecord != null) OnLocateRecord(bindIds[0]); }, null, bound);
                } else {
                    add("进入 3D 依次点选绑定选中 " + bindIds.Count + " 行", () => { if (OnStartSequentialBindRecords != null) OnStartSequentialBindRecords(bindIds); }, "accent", true);
                    add("按顺序绑定 CAD 已选中件到选中 " + bindIds.Count + " 行", () => {
                        if (OnBindCurrentCadToRecords != null) OnBindCurrentCadToRecords(bindIds);
                    }, null, true);
                    bool bound = meta.Primary != null && !string.IsNullOrEmpty(meta.Primary.ReferenceKey);
                    add("在 CAD 中定位当前行模型", () => { if (OnLocateRecord != null) OnLocateRecord(meta.Primary.Id); }, null, bound);
                }
            }

            rowMenu.Items.Add(new ToolStripSeparator());
            var selRowIndices = SelectedPGridRowIndices();
            if (activeSubTab == 3) {
                // 气缸传感器子页优先提供删除选中磁开，避免右键磁开行误删整条气缸与阀片
                var sensorIds = selRowIndices.Count > 0
                    ? selRowIndices.Select(i => rowMetas[i].SensorId).Where(id => !string.IsNullOrEmpty(id)).Distinct(StringComparer.Ordinal).ToList()
                    : (string.IsNullOrEmpty(meta.SensorId) ? new List<string>() : new List<string> { meta.SensorId });
                if (sensorIds.Count > 1) {
                    add("删除选中的 " + sensorIds.Count + " 条磁开", () => DeleteRowSensors(sensorIds), "danger", true);
                } else if (sensorIds.Count == 1) {
                    add("删除此磁开", () => DeleteRowSensors(sensorIds), "danger", true);
                }
                if (selRowIndices.Count <= 1 && !string.IsNullOrEmpty(meta.CylinderId)) {
                    add("仅删除所属气缸与全部磁开（保留空阀片）", () => DeleteRowChannel(meta, false), "danger", true);
                }
            } else if (selRowIndices.Count > 1) {
                var selMetas = selRowIndices.Select(i => rowMetas[i]).ToList();
                add("删除选中的 " + selMetas.Count + " 条阀路（阀片 + 气缸 + 磁开，自动重排）", () => DeleteWholeRowChannels(selMetas), "danger", true);
                if (selMetas.Any(m => !string.IsNullOrEmpty(m.CylinderId) && !string.IsNullOrEmpty(m.ValveId)))
                    add("仅删除选中气缸与磁开（保留空阀片）", () => DeleteRowChannels(selMetas, false), "danger", true);
            } else if (!string.IsNullOrEmpty(meta.CylinderId) && !string.IsNullOrEmpty(meta.ValveId) && !meta.IsContinuation) {
                add("删除整条阀路（阀片 + 气缸 + 磁开）", () => DeleteWholeRowChannels(new[] { meta }), "danger", true);
                add("仅删除气缸与磁开（保留空阀片）", () => DeleteRowChannel(meta, false), "danger", true);
            } else if (!string.IsNullOrEmpty(meta.CylinderId)) {
                add("删除此气缸及关联磁开", () => DeleteRowChannel(meta, false), "danger", true);
            } else if (!string.IsNullOrEmpty(meta.ValveId)) {
                add("删除此阀片", () => DeleteWholeRowChannels(new[] { meta }), "danger", true);
            }
            Ui.StyleMenu(rowMenu);
        }

        void ShowRowContextMenu(int rowIndex, int colIndex) {
            if (rowIndex < 0 || rowIndex >= rowMetas.Count) return;
            BuildRowContextMenu(rowIndex, colIndex);
            rowMenu.Show(Cursor.Position);
        }

        // ==================== 业务交互动作 ====================
        void OnSubQuickActionClick() {
            if (activeSubTab == 0) {
                bool hasMissing = currentChannels.Any(c => c.Cylinder != null && !c.IsSpare && (c.Valve == null || (!c.IsBlowOrVacuum && c.Sensors.Count < 2)));
                if (hasMissing) CompleteMissingForCurrentIsland();
                else {
                    var meta = CurrentRowMeta();
                    if (meta == null || string.IsNullOrEmpty(meta.ValveId)) throw new InvalidOperationException("请先点击选中一条阀路，再追加同阀气缸。");
                    AddCylinderToSelectedValve(meta.ValveId);
                }
            } else if (activeSubTab == 1) {
                CompleteMissingForCurrentIsland();
            } else if (activeSubTab == 2) {
                var ids = currentChannels.Where(c => c.Valve != null && !c.IsSpare).Select(c => c.Valve.Id).Distinct().ToList();
                if (ids.Count == 0) throw new InvalidOperationException("本阀岛暂无非盖板阀片。");
                if (OnPickModelForRecords != null) OnPickModelForRecords("阀片", ids);
            } else {
                var ids = currentChannels.SelectMany(c => c.Sensors).Select(s => s.Id).Distinct().ToList();
                if (ids.Count == 0) throw new InvalidOperationException("本阀岛尚无气缸传感器，请先为气缸补齐 H/W。");
                if (OnPickModelForRecords != null) OnPickModelForRecords("气缸传感器", ids);
            }
        }

        void AddCylinderQuick(bool isBlowOrVacuum) {
            if (string.IsNullOrEmpty(selectedIslandKey)) { OpenCreateIslandDialog(); return; }
            string addedCylId = null;
            RunProjectMutation(next => {
                var res = LineupPneumaticOps.AddCylinderBundle(next, selectedIslandKey, true, isBlowOrVacuum ? 0 : 1, isBlowOrVacuum);
                addedCylId = res.CylinderId;
            }, null, isBlowOrVacuum ? "已添加吹气/破真空阀路" : "已添加气缸并自动配齐阀片与 H/W");
            if (!string.IsNullOrEmpty(addedCylId)) {
                FocusRecordInPGrid(addedCylId);
            }
        }

        void AddValveSlicesQuick(bool isSpare, int count) {
            if (string.IsNullOrEmpty(selectedIslandKey)) { OpenCreateIslandDialog(); return; }
            string lastId = null;
            RunProjectMutation(next => {
                var ids = LineupPneumaticOps.AddValveSlices(next, selectedIslandKey, isSpare, count);
                if (ids.Count > 0) lastId = ids[ids.Count - 1];
            }, null, "已向 " + selectedIslandKey + " 添加 " + count + (isSpare ? " 个预留盖板" : " 片阀片"));
            if (!string.IsNullOrEmpty(lastId)) FocusRecordInPGrid(lastId);
        }

        void BatchAddValveSlicesDialog(bool isSpare) {
            if (string.IsNullOrEmpty(selectedIslandKey)) { OpenCreateIslandDialog(); return; }
            string val = Ui.Prompt(FindForm(), isSpare ? "批量添加预留位 / 盖板" : "批量铺设阀片",
                "向阀岛 " + selectedIslandKey + " 一次添加几" + (isSpare ? "个预留盖板" : "片阀片") + "？（1～32）", "4");
            if (val == null) return;
            int n;
            if (!int.TryParse(val.Trim(), out n) || n < 1 || n > 32) throw new InvalidOperationException("请输入 1～32 之间的整数。");
            AddValveSlicesQuick(isSpare, n);
        }

        void AddCylinderToSelectedValve(string valveId) {
            string newCylId = null;
            RunProjectMutation(next => {
                var res = LineupPneumaticOps.AddCylinderToValve(next, valveId, 1);
                newCylId = res.CylinderId;
            }, null, "已挂载同阀气缸并自动重编（含 H/W）");
            if (!string.IsNullOrEmpty(newCylId)) FocusRecordInPGrid(newCylId);
        }

        void GenerateValveForSingleCylinder(string cylId) {
            RunProjectMutation(next => LineupNumbering.GenerateValves(next, new[] { cylId }), cylId, "已生成配套阀片");
        }

        void MarkValveAsSpare(string valveId) {
            RunProjectMutation(next => LineupPneumaticOps.SetValveAsSpare(next, valveId), valveId, "已设为预留位 / 盖板");
        }

        void SetSensorMode(string cylId, int pairs) {
            string msg = pairs == 0 ? "已清除该气缸磁开" : (pairs == 2 ? "已升级为双组磁开 (H1/W1/H2/W2)" : "已配置标准 H/W 磁开");
            RunProjectMutation(next => LineupPneumaticOps.SetCylinderSensorMode(next, cylId, pairs), cylId, msg);
        }

        void CycleSensorModeForCylinder(GridRowMeta meta) {
            int curCount = meta.Channel == null ? 0 : meta.Channel.Sensors.Count;
            int target = curCount == 0 ? 1 : (curCount <= 2 ? 2 : 0);
            SetSensorMode(meta.CylinderId, target);
        }

        void DeleteRowChannel(GridRowMeta meta, bool deleteValveWhenLast) {
            DeleteRowChannels(new[] { meta }, deleteValveWhenLast);
        }

        // 删除整条阀路（含一阀控多缸下的全部气缸、磁开及阀片本身）：对含阀片行显式传入 (ValveId, "") 以触发整阀级联删除
        void DeleteWholeRowChannels(IList<GridRowMeta> metas) {
            if (metas == null || metas.Count == 0) return;
            var targets = new List<KeyValuePair<string, string>>();
            var seenValves = new HashSet<string>(StringComparer.Ordinal);
            var seenUnattachedCyls = new HashSet<string>(StringComparer.Ordinal);
            foreach (var m in metas) {
                if (m == null) continue;
                if (!string.IsNullOrEmpty(m.ValveId)) {
                    if (seenValves.Add(m.ValveId)) targets.Add(new KeyValuePair<string, string>(m.ValveId, ""));
                } else if (!string.IsNullOrEmpty(m.CylinderId)) {
                    if (seenUnattachedCyls.Add(m.CylinderId)) targets.Add(new KeyValuePair<string, string>("", m.CylinderId));
                }
            }
            if (targets.Count == 0) return;
            RunProjectMutation(next => LineupPneumaticOps.DeleteChannelItems(next, targets, true),
                null, "已删除选中整条阀路，剩余编号已自动顺延重排（支持撤销）");
        }

        void DeleteRowChannels(IList<GridRowMeta> metas, bool deleteValveWhenLast) {
            if (metas == null || metas.Count == 0) return;
            var targets = metas.Select(m => new KeyValuePair<string, string>(m.ValveId ?? "", m.CylinderId ?? "")).ToList();
            RunProjectMutation(next => LineupPneumaticOps.DeleteChannelItems(next, targets, deleteValveWhenLast),
                null, "已删除选中气动项，剩余编号已自动顺延重排（支持撤销）");
        }

        void DeleteRowSensors(IList<string> sensorIds) {
            if (sensorIds == null || sensorIds.Count == 0) return;
            RunProjectMutation(next => LineupPneumaticOps.DeleteSensorItems(next, sensorIds),
                null, "已删除选中的 " + sensorIds.Count + " 条磁开（支持撤销）");
        }

        void CompleteMissingForCurrentIsland() {
            EndEdit();
            var cylIds = currentChannels.Where(c => c.Cylinder != null && !c.IsSpare && !string.IsNullOrWhiteSpace(c.Cylinder.Number))
                .Select(c => c.Cylinder.Id).ToList();
            if (cylIds.Count == 0) throw new InvalidOperationException("本阀岛暂无已编号气缸。");
            // 同步过滤已编号气缸，避免无编号气缸让 SensorPairs 抛异常打断整批补齐
            var normalCyls = currentChannels
                .Where(c => c.Cylinder != null && !c.IsSpare && !c.IsBlowOrVacuum && !string.IsNullOrWhiteSpace(c.Cylinder.Number))
                .Select(c => c.Cylinder.Id).ToList();

            Action<LineupProject, Action<int, int>> applyComplete = (target, report) => {
                int v = LineupNumbering.GenerateValves(target, cylIds);
                int s = 0;
                if (normalCyls.Count > 0) {
                    var before = new HashSet<string>(target.Records.Select(r => r.Id), StringComparer.Ordinal);
                    s = LineupNumbering.SensorPairs(target, normalCyls);
                    var sample = target.Records.FirstOrDefault(r => before.Contains(r.Id) && r.Category == "气缸传感器" && !string.IsNullOrWhiteSpace(r.Model));
                    if (sample != null) {
                        foreach (var sen in target.Records.Where(r => !before.Contains(r.Id))) {
                            sen.Model = sample.Model; sen.Brand = sample.Brand; sen.Connector = sample.Connector;
                        }
                    }
                }
                if (report != null) report(v, s);
            };

            // RunProjectMutation 接收 string 实参会先于 lambda 求值；先在纯内存拷贝预演拿到真实新增条数，避免提示恒报 0 条或空压撤销栈
            int vAdd = 0, sAdd = 0;
            var probe = LineupEntry.Clone(GetProject());
            applyComplete(probe, (v, s) => { vAdd = v; sAdd = s; });
            if (vAdd == 0 && sAdd == 0) {
                if (OnNotifyStatus != null) OnNotifyStatus("本阀岛已编号气缸均已配齐阀片与 H/W，无需补齐");
                return;
            }
            RunProjectMutation(next => applyComplete(next, null),
                null, "本阀岛已补齐：阀片 " + vAdd + " 条，H/W 传感器 " + sAdd + " 条");
        }

        void OnBindOrLocateIslandClick() {
            var cur = islands.FirstOrDefault(x => string.Equals(x.Key, selectedIslandKey, StringComparison.OrdinalIgnoreCase));
            if (cur == null) return;
            if (cur.Has3DBind) LocateSelectedIsland();
            else if (OnStartBindIslandIn3D != null) OnStartBindIslandIn3D(cur.Key);
            else if (OnBindCurrentCadToIsland != null) OnBindCurrentCadToIsland(cur.Key);
        }

        void LocateSelectedIsland() {
            var cur = islands.FirstOrDefault(x => string.Equals(x.Key, selectedIslandKey, StringComparison.OrdinalIgnoreCase));
            if (cur == null || string.IsNullOrEmpty(cur.IslandRecordId) || !cur.Has3DBind)
                throw new InvalidOperationException("此阀岛尚未绑定 3D 模型，请先在 CAD 选中阀岛本体后点「绑 3D」。");
            if (OnLocateRecord != null) OnLocateRecord(cur.IslandRecordId);
        }

        void SyncSelectedIslandToElectrical() {
            if (string.IsNullOrEmpty(selectedIslandKey)) return;
            EndEdit();
            var probe = LineupEntry.Clone(GetProject());
            int added = LineupPneumaticOps.SyncIslandsToElectrical(probe, selectedIslandKey);
            if (added <= 0) {
                var cur = islands.FirstOrDefault(x => string.Equals(x.Key, selectedIslandKey, StringComparison.OrdinalIgnoreCase));
                string msg = cur != null && cur.Kind == "单片阀"
                    ? selectedIslandKey + " 为单片阀，无需同步阀岛本体到「其他电气件」"
                    : selectedIslandKey + " 已在「其他电气件」中，未重复添加";
                if (OnNotifyStatus != null) OnNotifyStatus(msg);
                return;
            }
            RunProjectMutation(next => LineupPneumaticOps.SyncIslandsToElectrical(next, selectedIslandKey),
                null, "已将 " + selectedIslandKey + " 本体加入「其他电气件 E**」");
        }

        // ==================== 新建 / 编辑 / 删除 / 换岛 对话框 ====================
        public void OpenCreateIslandDialog() {
            var p = GetProject != null ? GetProject() : null;
            if (p == null) return;
            string defPrefix = LineupPneumaticOps.DefaultPrefix(p);
            int defNum = LineupPneumaticOps.NextIslandNumber(p, defPrefix);

            using (var d = new Form()) {
                Ui.Shell(d, "新建阀岛 / 阀组", 500, 388, 460, 360);
                d.FormBorderStyle = FormBorderStyle.FixedDialog;

                var body = new Panel { Dock = DockStyle.Fill, Padding = Ui.SPad(20, 14, 20, 8), BackColor = Ui.Bg };
                var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, BackColor = Ui.Bg };
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Ui.S(128)));
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

                var txtPrefix = new TextBox { Text = defPrefix, Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle };
                var numIsland = new NumericUpDown { Minimum = 1, Maximum = 999, Value = Math.Min(999, defNum), Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle };
                var cmbKind = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };
                cmbKind.Items.AddRange(LineupPresets.ValveKinds.Cast<object>().ToArray());
                cmbKind.SelectedIndex = 0;
                var cmbModel = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown, AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.ListItems };
                var cat = GetCatalog != null ? GetCatalog() : null;
                if (cat != null) cmbModel.Items.AddRange(cat.Entries.Where(x => x.Category == "阀岛").Select(x => x.Model).Distinct().OrderBy(x => x).Cast<object>().ToArray());
                var numPreSlices = new NumericUpDown { Minimum = 0, Maximum = 32, Value = 0, Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle };
                var chkSyncE = new CheckBox { Text = "同时将此阀岛本体加入「其他电气件 (E**)」清单", AutoSize = true, Checked = false, BackColor = Ui.Bg, ForeColor = Ui.Text2, Font = Ui.F8 };

                var previewLbl = new Label { Dock = DockStyle.Bottom, Height = Ui.S(34), ForeColor = Ui.Accent, Font = Ui.F8 };
                Action updatePrev = () => {
                    string k = txtPrefix.Text.Trim() + "-Y" + numIsland.Value;
                    previewLbl.Text = "将生成编号示例：" + k + "V01（阀片） / " + k + "C01（气缸） / " + k + "C01H（原位磁开）";
                };
                txtPrefix.TextChanged += (s, e) => updatePrev();
                numIsland.ValueChanged += (s, e) => updatePrev();
                updatePrev();

                Action<int, string, Control> row = (r, l, c) => {
                    layout.RowStyles.Add(new RowStyle(SizeType.Absolute, Ui.S(36)));
                    c.Margin = Ui.SPad(0, 4, 0, 4);
                    layout.Controls.Add(new Label { Text = l, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Ui.Text2, Font = Ui.F9B }, 0, r);
                    layout.Controls.Add(c, 1, r);
                };
                row(0, "工位号 / 前缀", txtPrefix);
                row(1, "阀岛序号 (Y*)", numIsland);
                row(2, "阀组控制方式", cmbKind);
                row(3, "阀岛 / 汇流排型号", cmbModel);
                row(4, "预铺空阀片数 (可选)", numPreSlices);
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, Ui.S(28)));
                layout.Controls.Add(chkSyncE, 1, 5);

                body.Controls.Add(layout);
                body.Controls.Add(previewLbl);

                var footer = new ActionBar { Dock = DockStyle.Bottom, Height = Ui.S(56), Padding = Ui.SPad(20, 0, 20, 0) };
                var ok = Ui.Primary("创建阀岛", 104, 34); ok.DialogResult = DialogResult.OK;
                var cancel = Ui.Secondary("取消", 76, 34); cancel.DialogResult = DialogResult.Cancel;
                footer.AddRight(cancel); footer.AddRight(ok);
                d.AcceptButton = ok; d.CancelButton = cancel;
                d.Controls.Add(body); d.Controls.Add(footer);

                if (d.ShowDialog(FindForm()) != DialogResult.OK) return;
                string pre = txtPrefix.Text.Trim();
                int idx = (int)numIsland.Value;
                string kind = Convert.ToString(cmbKind.SelectedItem);
                string model = cmbModel.Text.Trim();
                int preSlices = (int)numPreSlices.Value;
                bool syncE = chkSyncE.Checked;
                string createdKey = "";
                RunProjectMutation(next => {
                    createdKey = LineupPneumaticOps.CreateIsland(next, pre, idx, kind, model);
                    if (preSlices > 0) LineupPneumaticOps.AddValveSlices(next, createdKey, false, preSlices);
                    if (syncE) LineupPneumaticOps.SyncIslandsToElectrical(next, createdKey);
                }, null, "已创建 " + pre + "-Y" + idx + "（" + kind + "）" + (preSlices > 0 ? "并预铺 " + preSlices + " 片阀位" : ""));
                if (createdKey.Length > 0) { selectedIslandKey = createdKey; viewingOrphans = false; RefreshFromProject(null); }
            }
        }

        void OpenEditIslandDialog() {
            var cur = islands.FirstOrDefault(x => string.Equals(x.Key, selectedIslandKey, StringComparison.OrdinalIgnoreCase));
            if (cur == null) return;
            using (var d = new Form()) {
                Ui.Shell(d, "编辑阀岛 · " + cur.Key, 480, 280, 440, 260);
                d.FormBorderStyle = FormBorderStyle.FixedDialog;
                var body = new Panel { Dock = DockStyle.Fill, Padding = Ui.SPad(20, 14, 20, 8), BackColor = Ui.Bg };
                var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, BackColor = Ui.Bg };
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Ui.S(126)));
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

                var cmbKind = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };
                cmbKind.Items.AddRange(LineupPresets.ValveKinds.Cast<object>().ToArray());
                int ki = cmbKind.Items.IndexOf(cur.Kind);
                cmbKind.SelectedIndex = ki >= 0 ? ki : 0;

                var cmbModel = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown, AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.ListItems };
                var cat = GetCatalog != null ? GetCatalog() : null;
                if (cat != null) cmbModel.Items.AddRange(cat.Entries.Where(x => x.Category == "阀岛").Select(x => x.Model).Distinct().OrderBy(x => x).Cast<object>().ToArray());
                cmbModel.Text = cur.Model;

                Action<int, string, Control> row = (r, l, c) => {
                    layout.RowStyles.Add(new RowStyle(SizeType.Absolute, Ui.S(38)));
                    c.Margin = Ui.SPad(0, 4, 0, 4);
                    layout.Controls.Add(new Label { Text = l, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Ui.Text2, Font = Ui.F9B }, 0, r);
                    layout.Controls.Add(c, 1, r);
                };
                row(0, "阀组控制方式", cmbKind);
                row(1, "阀岛 / 汇流排型号", cmbModel);

                var note = new Label {
                    Dock = DockStyle.Bottom, Height = Ui.S(38), ForeColor = Ui.Muted, Font = Ui.F8,
                    Text = "修改后将自动同步到本阀岛下全部阀片的「所属阀岛型号」列，并在导出 Excel 时合并显示。"
                };
                body.Controls.Add(layout);
                body.Controls.Add(note);

                var footer = new ActionBar { Dock = DockStyle.Bottom, Height = Ui.S(56), Padding = Ui.SPad(20, 0, 20, 0) };
                var ok = Ui.Primary("保存修改", 100, 34); ok.DialogResult = DialogResult.OK;
                var cancel = Ui.Secondary("取消", 76, 34); cancel.DialogResult = DialogResult.Cancel;
                footer.AddRight(cancel); footer.AddRight(ok);
                d.AcceptButton = ok; d.CancelButton = cancel;
                d.Controls.Add(body); d.Controls.Add(footer);

                if (d.ShowDialog(FindForm()) != DialogResult.OK) return;
                string newKind = Convert.ToString(cmbKind.SelectedItem);
                string newModel = cmbModel.Text.Trim();
                RunProjectMutation(next => LineupPneumaticOps.UpdateIsland(next, cur.Key, newKind, newModel),
                    null, "已更新 " + cur.Key + " 阀岛配置");
            }
        }

        void DeleteSelectedIsland() {
            var cur = islands.FirstOrDefault(x => string.Equals(x.Key, selectedIslandKey, StringComparison.OrdinalIgnoreCase));
            if (cur == null) return;
            int totalChildren = cur.ValveCount + cur.CylinderCount + cur.SensorCount;
            if (totalChildren > 0) {
                string msg = "阀岛 " + cur.Key + " 下包含 " + cur.ValveCount + " 片阀、" + cur.CylinderCount + " 支气缸、" + cur.SensorCount + " 个磁开。\n\n确定将此阀岛及其下全部器件一并删除吗？（删除后可用底部「撤销」恢复）";
                if (MessageBox.Show(FindForm(), msg, "删除阀岛", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            }
            RunProjectMutation(next => LineupPneumaticOps.DeleteIsland(next, cur.Key, true), null, "已删除阀岛 " + cur.Key + "，剩余阀岛编号已自动顺延重排");
        }

        void MoveValveDialog(string valveId) {
            var others = islands.Where(x => !string.Equals(x.Key, selectedIslandKey, StringComparison.OrdinalIgnoreCase)).ToList();
            if (others.Count == 0) throw new InvalidOperationException("请先再新建另一个目标阀岛（如 Y2），才能换岛。");
            using (var d = new Form()) {
                Ui.Shell(d, "移动到其他阀岛", 460, 220, 420, 200);
                d.FormBorderStyle = FormBorderStyle.FixedDialog;
                var body = new Panel { Dock = DockStyle.Fill, Padding = Ui.SPad(18, 16, 18, 10), BackColor = Ui.Bg };
                var cmb = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };
                foreach (var o in others) cmb.Items.Add(o.Key + " · " + o.Kind + (string.IsNullOrWhiteSpace(o.Model) ? "" : " · " + o.Model));
                cmb.SelectedIndex = 0;
                var tip = new Label {
                    Dock = DockStyle.Fill, ForeColor = Ui.Muted, Font = Ui.F8, Padding = Ui.SPad(0, 10, 0, 0),
                    Text = "此阀片及其下全部气缸、H/W 磁开将自动顺延重编到目标阀岛，3D 绑定与型号完整保留。"
                };
                body.Controls.Add(tip); body.Controls.Add(cmb);
                var footer = new ActionBar { Dock = DockStyle.Bottom, Height = Ui.S(54), Padding = Ui.SPad(18, 0, 18, 0) };
                var ok = Ui.Primary("确定移动", 100, 34); ok.DialogResult = DialogResult.OK;
                var cancel = Ui.Secondary("取消", 76, 34); cancel.DialogResult = DialogResult.Cancel;
                footer.AddRight(cancel); footer.AddRight(ok);
                d.AcceptButton = ok; d.CancelButton = cancel;
                d.Controls.Add(body); d.Controls.Add(footer);
                if (d.ShowDialog(FindForm()) != DialogResult.OK) return;
                string targetKey = others[cmb.SelectedIndex].Key;
                RunProjectMutation(next => LineupPneumaticOps.MoveValvesToIsland(next, new[] { valveId }, targetKey),
                    valveId, "已将该阀路移动到 " + targetKey);
            }
        }

        void AssignOrphansDialog() {
            if (orphans.Count == 0) return;
            if (islands.Count == 0) {
                OpenCreateIslandDialog();
                var p = GetProject();
                islands = LineupPneumaticOps.ListIslands(p);
                if (islands.Count == 0) return;
            }
            var targetIds = orphanGrid.SelectedRows.Count > 0
                ? orphanGrid.SelectedRows.Cast<DataGridViewRow>()
                    .Select(r => (r.Tag as string) ?? (r.Index >= 0 && r.Index < orphans.Count ? orphans[r.Index].Id : ""))
                    .Where(id => !string.IsNullOrEmpty(id))
                    .Distinct(StringComparer.Ordinal)
                    .ToList()
                : orphans.Select(r => r.Id).ToList();

            using (var d = new Form()) {
                Ui.Shell(d, "归属未分配气动件到阀岛", 460, 250, 420, 230);
                d.FormBorderStyle = FormBorderStyle.FixedDialog;
                var body = new Panel { Dock = DockStyle.Fill, Padding = Ui.SPad(18, 14, 18, 8), BackColor = Ui.Bg };
                var lbl = new Label { Text = "将 " + targetIds.Count + " 个未归属器件分配到：", Dock = DockStyle.Top, Height = Ui.S(24), ForeColor = Ui.Text2, Font = Ui.F9B };
                var cmb = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };
                foreach (var isl in islands) cmb.Items.Add(isl.Key + " · " + isl.Kind + (string.IsNullOrWhiteSpace(isl.Model) ? "" : " · " + isl.Model));
                cmb.SelectedIndex = 0;
                var chkV = new CheckBox { Text = "同时自动生成配套阀片 (V**)", Dock = DockStyle.Top, Height = Ui.S(28), Checked = true, BackColor = Ui.Bg, ForeColor = Ui.Text, Font = Ui.F9 };
                var chkS = new CheckBox { Text = "同时自动生成 H/W 气缸传感器", Dock = DockStyle.Top, Height = Ui.S(28), Checked = true, BackColor = Ui.Bg, ForeColor = Ui.Text, Font = Ui.F9 };
                body.Controls.Add(chkS); body.Controls.Add(chkV); body.Controls.Add(cmb); body.Controls.Add(lbl);

                var footer = new ActionBar { Dock = DockStyle.Bottom, Height = Ui.S(54), Padding = Ui.SPad(18, 0, 18, 0) };
                var ok = Ui.Primary("一键归属", 104, 34); ok.DialogResult = DialogResult.OK;
                var cancel = Ui.Secondary("取消", 76, 34); cancel.DialogResult = DialogResult.Cancel;
                footer.AddRight(cancel); footer.AddRight(ok);
                d.AcceptButton = ok; d.CancelButton = cancel;
                d.Controls.Add(body); d.Controls.Add(footer);

                if (d.ShowDialog(FindForm()) != DialogResult.OK) return;
                string targetKey = islands[cmb.SelectedIndex].Key;
                bool genV = chkV.Checked, genS = chkS.Checked;
                RunProjectMutation(next => LineupPneumaticOps.AssignOrphansToIsland(next, targetIds, targetKey, genV, genS),
                    null, "已将 " + targetIds.Count + " 项归属到 " + targetKey);
                selectedIslandKey = targetKey;
                viewingOrphans = false;
                RefreshFromProject(null);
            }
        }

        void Try(Action action) {
            try { action(); }
            catch (Exception ex) {
                if (OnNotifyStatus != null) OnNotifyStatus(ex.Message);
                else MessageBox.Show(FindForm(), ex.Message, "Lineup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }
}
