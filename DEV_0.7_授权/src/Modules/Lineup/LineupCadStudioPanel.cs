using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using F = SolidEdgeFramework;
using A = SolidEdgeAssembly;

namespace TianGongCadSuite {
    // 嵌入式「标位置图」工作台控件（步骤②主界面，也供 LineupCadPageForm 复用）：
    // 把旧版左/右/顶共 19 个按钮收敛为：顶部页签条 + 超大 WYSIWYG 画布 + 右侧简洁清单，
    // 空态给出 3 个清晰入口，非高频页操作收进「管理本页 ▾」，右键画布/列表可直达单条微调。
    public sealed class LineupCadStudioPanel : Panel {
        readonly F.Application app;
        readonly A.AssemblyDocument assembly;
        readonly Func<LineupProject> getProject;
        readonly Action<LineupProject, string> commitProject;
        readonly Action<LineupRecord> locateRecord;
        readonly Action<bool> suspendOwner;
        readonly Action<string> reportStatus;

        static readonly StringFormat SfCenter = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
        static readonly StringFormat SfCalloutLeft = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        static readonly StringFormat SfCalloutRight = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };

        LineupProject project;
        int activePageIndex = -1;
        Bitmap currentImage;
        Bitmap scaledImage;
        Size scaledImageSize;
        LineupCadPagePlan currentPlan;
        RectangleF canvasImageRect;
        RectangleF canvasPageRect;
        float canvasScale = 1f;
        bool syncing;
        bool capturing;
        readonly Timer saveDebounce = new Timer { Interval = 450 };
        bool hasPendingSave;
        string pendingSaveMsg = "";

        // 顶部页签条
        readonly Panel topBar = new Panel();
        readonly FlowLayoutPanel tabFlow = new FlowLayoutPanel();
        readonly Button btnNewPage;
        readonly Button btnManagePage;
        readonly ContextMenuStrip newPageMenu = Ui.Menu();
        readonly ContextMenuStrip managePageMenu = Ui.Menu();
        readonly ContextMenuStrip pointMenu = Ui.Menu();

        // 空态引导
        readonly Panel emptyHost = new Panel();

        // 主编辑区（画布 + 可拖拽分隔条 + 右侧精简列表）
        readonly Panel workHost = new Panel();
        readonly CanvasPanel canvas = new CanvasPanel();
        readonly Label canvasHint = new Label();
        readonly Panel rightPane = new Panel();
        Splitter rightSplit;
        bool userDraggedRightPane;
        readonly Label rightHeader = new Label();
        readonly Button btnPickItems;
        readonly Button btnAppendHw;
        readonly ListBox pointList = new ListBox();
        readonly Segmented sideSeg = new Segmented("左栏", "自动", "右栏");
        readonly Button btnTag;
        readonly Button btnLocate;
        readonly Button btnRemovePt;

        sealed class CanvasPanel : Panel {
            public CanvasPanel() {
                DoubleBuffered = true;
                ResizeRedraw = true;
                BackColor = Color.FromArgb(0xF4, 0xF5, 0xF7);
            }
        }

        sealed class PointListEntry {
            public LineupLocationPoint Point;
            public string Number = "";
            public string Description = "";
        }

        public LineupCadStudioPanel(
            F.Application host,
            A.AssemblyDocument doc,
            Func<LineupProject> getProjectFn,
            Action<LineupProject, string> commitProjectFn,
            Action<LineupRecord> locateRecordFn,
            Action<bool> suspendOwnerFn,
            Action<string> reportStatusFn) {

            app = host;
            assembly = doc;
            getProject = getProjectFn;
            commitProject = commitProjectFn;
            locateRecord = locateRecordFn;
            suspendOwner = suspendOwnerFn;
            reportStatus = reportStatusFn;

            Dock = DockStyle.Fill;
            BackColor = Ui.Bg;
            Font = Ui.F9;

            btnNewPage = Ui.Primary("+ 新建图页 ▾", 118, 30);
            btnManagePage = Ui.Ghost("管理本页 ▾");
            ((FlatButton)btnManagePage).SetFore(Ui.Text2);
            btnPickItems = Ui.Small("选器件");
            btnAppendHw = Ui.Small("+ 补带 H/W");
            btnTag = Ui.Small("备忘");
            btnLocate = Ui.Small("CAD定位");
            btnRemovePt = Ui.Small("移除");
            ((FlatButton)btnTag).SetFore(Ui.Text2);
            ((FlatButton)btnLocate).SetFore(Ui.Text2);
            ((FlatButton)btnRemovePt).SetFore(Ui.Bad);

            BuildMenus();
            BuildLayout();
            saveDebounce.Tick += (s, e) => FlushPendingSave();
            Ui.EnableDoubleBuffer(this);
            Ui.EnableDoubleBuffer(workHost);
            Ui.EnableDoubleBuffer(rightPane);
            Ui.EnableDoubleBuffer(pointList);
            SyncFromProject(true);
        }

        protected override void Dispose(bool disposing) {
            if (disposing) {
                // 销毁阶段刷盘必须容错，防止文件被占或装配已关闭导致抛异常跳过位图/定时器/菜单释放
                try { FlushPendingSave(); } catch { }
                saveDebounce.Dispose();
                DisposeImage();
                newPageMenu.Dispose();
                managePageMenu.Dispose();
                pointMenu.Dispose();
            }
            base.Dispose(disposing);
        }

        public void FlushPendingSave() {
            saveDebounce.Stop();
            if (!hasPendingSave) return;
            hasPendingSave = false;
            string msg = pendingSaveMsg;
            pendingSaveMsg = "";
            if (project != null) Persist(msg);
        }

        void SchedulePersist(string msg) {
            pendingSaveMsg = msg;
            hasPendingSave = true;
            saveDebounce.Stop();
            saveDebounce.Start();
            NotifySummary(msg);
        }

        // 外部切换到步骤②或 BOM 变更时刷新数据
        public void SyncFromProject(bool preservePageIndex = true) {
            FlushPendingSave();
            var latest = getProject != null ? getProject() : null;
            if (latest == null) return;
            project = LineupEntry.Clone(latest);
            if (project.CadPages == null) project.CadPages = new List<LineupCadPage>();
            int want = preservePageIndex ? activePageIndex : 0;
            if (project.CadPages.Count == 0) activePageIndex = -1;
            else if (want < 0 || want >= project.CadPages.Count) activePageIndex = 0;
            else activePageIndex = want;
            RefreshView();
        }

        void BuildMenus() {
            Action<ContextMenuStrip, string, Action> add = (m, t, act) => {
                var mi = new ToolStripMenuItem(t);
                mi.Click += (s, e) => Try(act);
                m.Items.Add(mi);
            };
            add(newPageMenu, "截取当前 CAD 画面（推荐）", () => CreateNewPage(false));
            add(newPageMenu, "切换正交等轴测并截图", () => CreateNewPage(true));
            newPageMenu.Items.Add(new ToolStripSeparator());
            add(newPageMenu, "从本地图片导入…", ImportImagePage);

            add(managePageMenu, "重拍当前页 CAD 底图", RecaptureCurrentPage);
            add(managePageMenu, "重命名本页…", RenameCurrentPage);
            managePageMenu.Items.Add(new ToolStripSeparator());
            add(managePageMenu, "向前移一页", () => MoveCurrentPage(-1));
            add(managePageMenu, "向后移一页", () => MoveCurrentPage(1));
            managePageMenu.Items.Add(new ToolStripSeparator());
            add(managePageMenu, "删除当前页", DeleteCurrentPage);

            add(pointMenu, "放到左栏", () => SetSelectedSide("left"));
            add(pointMenu, "自动分配左右栏", () => SetSelectedSide(""));
            add(pointMenu, "放到右栏", () => SetSelectedSide("right"));
            pointMenu.Items.Add(new ToolStripSeparator());
            add(pointMenu, "填写备忘 / 接线箱号…", EditSelectedTag);
            add(pointMenu, "在 CAD 中定位该器件", LocateSelectedPointInCad);
            pointMenu.Items.Add(new ToolStripSeparator());
            add(pointMenu, "清除箭头坐标（重新点选）", ClearSelectedCoord);
            add(pointMenu, "从本页移除", RemoveSelectedPoint);
        }

        void BuildLayout() {
            // ---- 顶部页签条 ----
            topBar.Dock = DockStyle.Top;
            topBar.Height = Ui.S(46);
            topBar.BackColor = Ui.Bg;
            topBar.Padding = Ui.SPad(0, 6, 0, 6);
            topBar.Paint += (s, e) => {
                using (var p = new Pen(Ui.Line)) e.Graphics.DrawLine(p, 0, topBar.Height - 1, topBar.Width, topBar.Height - 1);
            };

            tabFlow.Dock = DockStyle.Fill;
            tabFlow.WrapContents = false;
            tabFlow.AutoScroll = true;
            tabFlow.BackColor = Ui.Bg;
            tabFlow.Padding = Ui.SPad(0, 1, 0, 0);

            var topRight = new FlowLayoutPanel {
                Dock = DockStyle.Right, AutoSize = true, WrapContents = false,
                BackColor = Ui.Bg, Padding = Ui.SPad(6, 1, 0, 0)
            };
            btnManagePage.Margin = Ui.SPad(0, 1, 6, 0);
            Ui.BindDropDownMenu(btnManagePage, managePageMenu);
            btnNewPage.Margin = Ui.SPad(0, 0, 0, 0);
            Ui.BindDropDownMenu(btnNewPage, newPageMenu);
            topRight.Controls.Add(btnManagePage);
            topRight.Controls.Add(btnNewPage);

            topBar.Controls.Add(tabFlow);
            topBar.Controls.Add(topRight);

            // ---- 空态友好引导 ----
            emptyHost.Dock = DockStyle.Fill;
            emptyHost.BackColor = Ui.Bg;
            var emptyCard = new Card { Fill = true, Padding = Ui.SPad(28, 28, 28, 24) };
            emptyCard.Size = new Size(Ui.S(560), Ui.S(250));
            var eTitle = new Label {
                Text = "给 Lineup 配上 CAD 位置图（可选）",
                Font = Ui.F12B, ForeColor = Ui.Text, BackColor = Ui.Rail,
                AutoSize = false, TextAlign = ContentAlignment.MiddleCenter,
                Dock = DockStyle.Top, Height = Ui.S(34)
            };
            var eSub = new Label {
                Text = "先把天工三维模型摆到合适角度，点击下方按钮抓图；\n随后在图上左键点哪儿，红色箭头就从哪儿指向对应编号。",
                Font = Ui.F9, ForeColor = Ui.Muted, BackColor = Ui.Rail,
                AutoSize = false, TextAlign = ContentAlignment.MiddleCenter,
                Dock = DockStyle.Top, Height = Ui.S(52)
            };
            var eBtnRow = new FlowLayoutPanel {
                Dock = DockStyle.Top, Height = Ui.S(52), WrapContents = false,
                BackColor = Ui.Rail, Padding = Ui.SPad(0, 10, 0, 0)
            };
            var bCur = Ui.Primary("截取当前 CAD 画面", 162, 36);
            var bIso = Ui.Secondary("正交等轴测截图", 136, 36);
            var bImg = Ui.Secondary("导入本地图片…", 124, 36);
            bCur.Margin = Ui.SPad(0, 0, 10, 0);
            bIso.Margin = Ui.SPad(0, 0, 10, 0);
            bCur.Click += (s, e) => Try(() => CreateNewPage(false));
            bIso.Click += (s, e) => Try(() => CreateNewPage(true));
            bImg.Click += (s, e) => Try(ImportImagePage);
            eBtnRow.Controls.AddRange(new Control[] { bCur, bIso, bImg });
            Action centerEmptyButtons = () => {
                int sum = bCur.Width + bIso.Width + bImg.Width + Ui.S(20);
                int left = Math.Max(0, (eBtnRow.ClientSize.Width - sum) / 2);
                eBtnRow.Padding = new Padding(left, Ui.S(8), 0, 0);
            };
            eBtnRow.Resize += (s, e) => centerEmptyButtons();
            var eFoot = new Label {
                Text = "如果暂时不需要位置图，可以直接点击右下角「下一步：检查与导出」。",
                Font = Ui.F8, ForeColor = Ui.Muted, BackColor = Ui.Rail,
                AutoSize = false, TextAlign = ContentAlignment.MiddleCenter,
                Dock = DockStyle.Bottom, Height = Ui.S(26)
            };
            emptyCard.Controls.Add(eFoot);
            emptyCard.Controls.Add(eBtnRow);
            emptyCard.Controls.Add(eSub);
            emptyCard.Controls.Add(eTitle);
            emptyHost.Controls.Add(emptyCard);
            emptyHost.Resize += (s, e) => {
                int w = Math.Min(Ui.S(560), Math.Max(Ui.S(360), emptyHost.ClientSize.Width - Ui.S(32)));
                int h = Ui.S(244);
                emptyCard.SetBounds((emptyHost.ClientSize.Width - w) / 2, Math.Max(Ui.S(24), (emptyHost.ClientSize.Height - h) / 2 - Ui.S(16)), w, h);
                centerEmptyButtons();
            };

            // ---- 主编辑区（左侧超大画布 + 右侧极简列表） ----
            workHost.Dock = DockStyle.Fill;
            workHost.BackColor = Ui.Bg;

            var centerPane = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(0xF4, 0xF5, 0xF7) };
            canvasHint.Dock = DockStyle.Top;
            canvasHint.Height = Ui.S(30);
            canvasHint.BackColor = Color.FromArgb(0xF5, 0xF6, 0xFA);
            canvasHint.ForeColor = Ui.Text2;
            canvasHint.Font = Ui.F8;
            canvasHint.Padding = Ui.SPad(10, 0, 10, 0);
            canvasHint.TextAlign = ContentAlignment.MiddleLeft;

            canvas.Dock = DockStyle.Fill;
            canvas.Cursor = Cursors.Cross;
            canvas.Paint += PaintCanvas;
            canvas.MouseClick += CanvasClick;
            centerPane.Controls.Add(canvas);
            centerPane.Controls.Add(canvasHint);

            int savedRightW;
            int initRightW = 264;
            if (int.TryParse(Choices.Get("lineup.cadright.w", ""), out savedRightW) && savedRightW >= 220 && savedRightW <= 420) {
                initRightW = savedRightW;
                userDraggedRightPane = true;
            }
            rightPane.Dock = DockStyle.Right;
            rightPane.Width = Ui.S(initRightW);
            rightPane.BackColor = Ui.Bg;
            rightPane.Padding = Ui.SPad(10, 6, 0, 0);
            rightPane.Paint += (s, e) => {
                using (var p = new Pen(Ui.Line)) e.Graphics.DrawLine(p, 0, 0, 0, rightPane.Height);
            };
            rightSplit = new Splitter { Dock = DockStyle.Right, Width = Ui.S(6), MinSize = Ui.S(220), MinExtra = Ui.S(420), BackColor = Ui.Bg };
            rightSplit.SplitterMoved += (s, e) => {
                userDraggedRightPane = true;
                int lw = (int)Math.Round(rightPane.Width / Math.Max(1f, Ui.DpiScale));
                if (lw >= 220 && lw <= 420) Choices.Set("lineup.cadright.w", lw.ToString(System.Globalization.CultureInfo.InvariantCulture));
                canvas.Invalidate();
            };

            var rHeadRow = new Panel { Dock = DockStyle.Top, Height = Ui.S(32), BackColor = Ui.Bg };
            rightHeader.Dock = DockStyle.Fill;
            rightHeader.Font = Ui.F9B;
            rightHeader.ForeColor = Ui.Text;
            rightHeader.TextAlign = ContentAlignment.MiddleLeft;
            rightHeader.Text = "本页标注项";
            btnPickItems.Dock = DockStyle.Right;
            btnPickItems.Click += (s, e) => Try(EditPagePointsDialog);
            rHeadRow.Controls.Add(rightHeader);
            rHeadRow.Controls.Add(btnPickItems);

            var rSubRow = new Panel { Dock = DockStyle.Top, Height = Ui.S(28), BackColor = Ui.Bg, Visible = false };
            btnAppendHw.Dock = DockStyle.Left;
            btnAppendHw.Click += (s, e) => Try(AppendCylinderSensorsToPage);
            rSubRow.Controls.Add(btnAppendHw);
            btnAppendHw.Tag = rSubRow;

            pointList.Dock = DockStyle.Fill;
            pointList.BorderStyle = BorderStyle.None;
            pointList.BackColor = Ui.Bg;
            pointList.DrawMode = DrawMode.OwnerDrawFixed;
            pointList.ItemHeight = Ui.S(42);
            pointList.IntegralHeight = false;
            pointList.DrawItem += DrawPointListItem;
            pointList.SelectedIndexChanged += (s, e) => {
                SyncSideControl();
                UpdateCanvasHint();
                canvas.Invalidate();
            };
            pointList.MouseUp += (s, e) => {
                if (e.Button != MouseButtons.Right) return;
                int idx = pointList.IndexFromPoint(e.Location);
                if (idx >= 0 && idx < pointList.Items.Count) {
                    pointList.SelectedIndex = idx;
                    pointMenu.Show(pointList, e.Location);
                }
            };

            var rFooter = new Panel { Dock = DockStyle.Bottom, Height = Ui.S(76), BackColor = Ui.Bg, Padding = Ui.SPad(0, 8, 0, 2) };
            rFooter.Paint += (s, e) => {
                using (var p = new Pen(Ui.Line)) e.Graphics.DrawLine(p, 0, 0, rFooter.Width, 0);
            };
            sideSeg.Dock = DockStyle.Top;
            sideSeg.Height = Ui.S(30);
            sideSeg.SelectedIndexChanged += (s, e) => {
                if (syncing) return;
                string side = sideSeg.SelectedIndex == 0 ? "left" : sideSeg.SelectedIndex == 2 ? "right" : "";
                Try(() => SetSelectedSide(side));
            };
            var miniOps = new FlowLayoutPanel {
                Dock = DockStyle.Bottom, Height = Ui.S(30), WrapContents = false, BackColor = Ui.Bg, Padding = Ui.SPad(0, 2, 0, 0)
            };
            btnTag.Margin = Ui.SPad(0, 0, 4, 0);
            btnLocate.Margin = Ui.SPad(0, 0, 4, 0);
            btnRemovePt.Margin = Ui.SPad(0, 0, 0, 0);
            btnTag.Click += (s, e) => Try(EditSelectedTag);
            btnLocate.Click += (s, e) => Try(LocateSelectedPointInCad);
            btnRemovePt.Click += (s, e) => Try(RemoveSelectedPoint);
            miniOps.Controls.AddRange(new Control[] { btnTag, btnLocate, btnRemovePt });
            rFooter.Controls.Add(sideSeg);
            rFooter.Controls.Add(miniOps);

            rightPane.Controls.Add(pointList);
            rightPane.Controls.Add(rSubRow);
            rightPane.Controls.Add(rHeadRow);
            rightPane.Controls.Add(rFooter);

            workHost.Controls.Add(centerPane);
            workHost.Controls.Add(rightSplit);
            workHost.Controls.Add(rightPane);
            workHost.Resize += (s, e) => {
                int w = workHost.ClientSize.Width;
                if (w < Ui.S(400)) return;
                int maxR = Math.Max(Ui.S(220), w - Ui.S(440));
                if (!userDraggedRightPane) {
                    int target = w < Ui.S(980) ? Ui.S(232) : (w > Ui.S(1580) ? Ui.S(310) : Ui.S(264));
                    target = Math.Min(target, maxR);
                    if (Math.Abs(rightPane.Width - target) > Ui.S(4)) rightPane.Width = target;
                } else if (rightPane.Width > maxR) {
                    rightPane.Width = maxR;
                }
                canvas.Invalidate();
            };

            Controls.Add(workHost);
            Controls.Add(emptyHost);
            Controls.Add(topBar);
        }

        void DrawPointListItem(object sender, DrawItemEventArgs e) {
            if (e.Index < 0 || e.Index >= pointList.Items.Count) return;
            var g = e.Graphics;
            var entry = (PointListEntry)pointList.Items[e.Index];
            var pt = entry.Point;
            bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            var outer = e.Bounds;
            g.FillRectangle(Ui.CachedBrush(Ui.Bg), outer);
            var card = new Rectangle(outer.X + 1, outer.Y + 2, outer.Width - 3, outer.Height - 4);
            if (selected) {
                Ui.FillRound(g, card, Ui.S(7), Ui.AccentSoft);
                Ui.DrawRound(g, card, Ui.S(7), Ui.AccentRing, 1);
            }

            // 左侧状态徽标：绿勾=已定位，橙点=待点选
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int ds = Ui.S(14);
            int cx = card.X + Ui.S(8), cy = card.Y + (card.Height - ds) / 2;
            var circle = new Rectangle(cx, cy, ds, ds);
            if (pt.State == "ok") {
                g.FillEllipse(Ui.CachedBrush(Ui.OkSoft), circle);
                Ui.DrawCheck(g, Rectangle.Inflate(circle, -Ui.S(3), -Ui.S(3)), Ui.Ok, 1.5f);
            } else {
                g.FillEllipse(Ui.CachedBrush(Ui.WarnSoft), circle);
                int dot = Ui.S(6);
                g.FillEllipse(Ui.CachedBrush(Color.FromArgb(0xD9, 0x77, 0x06)), cx + (ds - dot) / 2, cy + (ds - dot) / 2, dot, dot);
            }
            g.SmoothingMode = SmoothingMode.Default;

            int tx = cx + ds + Ui.S(8);
            int rightPad = Ui.S(34);
            string sideText = pt.Side == "left" ? "左" : pt.Side == "right" ? "右" : "";
            string numText = !string.IsNullOrWhiteSpace(entry.Number) ? entry.Number : (pt.Number ?? "");
            string title = numText + (string.IsNullOrWhiteSpace(pt.Tag) ? "" : " [" + pt.Tag.Trim() + "]");
            string sub = string.IsNullOrWhiteSpace(entry.Description)
                ? (pt.State == "ok" ? "已定位" : "点左图设置箭头")
                : entry.Description;

            TextRenderer.DrawText(g, title, selected ? Ui.F9B : Ui.F9,
                new Rectangle(tx, card.Y + Ui.S(3), Math.Max(10, card.Right - tx - rightPad), Ui.S(18)),
                selected ? Ui.Accent : Ui.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, sub, Ui.F8,
                new Rectangle(tx, card.Y + Ui.S(20), Math.Max(10, card.Right - tx - Ui.S(8)), Ui.S(16)),
                pt.State == "ok" ? Ui.Muted : Ui.Warn, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if (sideText.Length > 0) {
                TextRenderer.DrawText(g, sideText, Ui.F8,
                    new Rectangle(card.Right - rightPad, card.Y + Ui.S(3), Ui.S(26), Ui.S(18)),
                    Ui.Muted, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            }
        }

        LineupCadPage CurrentPage() {
            if (project == null || project.CadPages == null || activePageIndex < 0 || activePageIndex >= project.CadPages.Count) return null;
            return project.CadPages[activePageIndex];
        }

        LineupLocationPoint CurrentPoint() {
            var item = pointList.SelectedItem as PointListEntry;
            return item == null ? null : item.Point;
        }

        void RefreshView() {
            RebuildPageTabs();
            bool hasPages = project != null && project.CadPages != null && project.CadPages.Count > 0;
            topBar.Visible = hasPages;
            btnManagePage.Visible = hasPages;
            emptyHost.Visible = !hasPages;
            workHost.Visible = hasPages;
            if (!hasPages) {
                DisposeImage();
                currentPlan = null;
                NotifySummary("尚无位置图页");
                return;
            }
            LoadActivePageImage();
            RefreshPoints(null);
        }

        void RebuildPageTabs() {
            tabFlow.SuspendLayout();
            Ui.DisposeChildren(tabFlow);
            var pages = project != null && project.CadPages != null ? project.CadPages : new List<LineupCadPage>();
            for (int i = 0; i < pages.Count; i++) {
                int idx = i;
                var pg = pages[i];
                int total = pg.Points == null ? 0 : pg.Points.Count;
                int ok = pg.Points == null ? 0 : pg.Points.Count(p => p.State == "ok");
                string label = (string.IsNullOrWhiteSpace(pg.Name) ? "图页" + (i + 1) : pg.Name) + "  " + ok + "/" + total;
                bool active = i == activePageIndex;
                var tab = new FlatButton(
                    label,
                    active ? Ui.AccentSoft : Ui.Subtle,
                    active ? Ui.Accent : Ui.Text2,
                    active ? Ui.AccentRing : Ui.Line,
                    TextRenderer.MeasureText(label, Ui.F9B).Width + Ui.S(24),
                    Ui.S(30));
                tab.Font = active ? Ui.F9B : Ui.F9;
                tab.Margin = Ui.SPad(0, 0, 6, 0);
                tab.Click += (s, e) => {
                    if (activePageIndex != idx) {
                        FlushPendingSave();
                        activePageIndex = idx;
                        RefreshView();
                    }
                };
                tabFlow.Controls.Add(tab);
            }
            tabFlow.ResumeLayout();
        }

        void UpdateActivePageTabLabel() {
            var pg = CurrentPage();
            if (pg == null || activePageIndex < 0 || activePageIndex >= tabFlow.Controls.Count) return;
            var tab = tabFlow.Controls[activePageIndex] as FlatButton;
            if (tab == null) return;
            int total = pg.Points == null ? 0 : pg.Points.Count;
            int ok = pg.Points == null ? 0 : pg.Points.Count(p => p.State == "ok");
            string label = (string.IsNullOrWhiteSpace(pg.Name) ? "图页" + (activePageIndex + 1) : pg.Name) + "  " + ok + "/" + total;
            if (tab.Text != label) {
                tab.Text = label;
                tab.Width = TextRenderer.MeasureText(label, Ui.F9B).Width + Ui.S(24);
            }
            rightHeader.Text = "本页器件 (" + ok + "/" + total + ")";
        }

        void LoadActivePageImage() {
            var pg = CurrentPage();
            DisposeImage();
            if (pg == null) return;
            string path = LineupProject.ResolveCadImagePath(project.AssemblyFile, pg.ImageFile);
            if (!string.IsNullOrEmpty(path) && File.Exists(path)) {
                try {
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var tmp = Image.FromStream(fs)) {
                        currentImage = new Bitmap(tmp);
                    }
                    if (pg.Width <= 0) pg.Width = currentImage.Width;
                    if (pg.Height <= 0) pg.Height = currentImage.Height;
                } catch { }
            }
        }

        Dictionary<string, LineupRecord> BuildRecordMapById() {
            var byId = new Dictionary<string, LineupRecord>(StringComparer.Ordinal);
            if (project != null && project.Records != null) {
                foreach (var r in project.Records)
                    if (r != null && !string.IsNullOrEmpty(r.Id) && !byId.ContainsKey(r.Id)) byId[r.Id] = r;
            }
            return byId;
        }

        void RefreshPoints(string selectId) {
            var pg = CurrentPage();
            var byId = BuildRecordMapById();
            pointList.BeginUpdate();
            pointList.Items.Clear();
            int ok = 0, total = 0;
            if (pg != null && pg.Points != null) {
                total = pg.Points.Count;
                foreach (var pt in pg.Points) {
                    if (pt.State == "ok") ok++;
                    LineupRecord rec;
                    bool hasRec = byId.TryGetValue(pt.Id ?? "", out rec);
                    // 同步 BOM 最新编号与描述，避免重编号/换岛/一阀多缸后画布显示新号而右侧列表仍留旧号
                    string num = hasRec && !string.IsNullOrWhiteSpace(rec.Number) ? rec.Number.Trim() : (pt.Number ?? "").Trim();
                    if (num.Length > 0) pt.Number = num;
                    string desc = hasRec ? (rec.Description ?? "") : (pt.Description ?? "");
                    pointList.Items.Add(new PointListEntry { Point = pt, Number = num, Description = desc });
                }
            }
            pointList.EndUpdate();

            rightHeader.Text = "本页器件 (" + ok + "/" + total + ")";
            UpdateAppendHwVisibility();

            if (selectId != null) {
                for (int i = 0; i < pointList.Items.Count; i++) {
                    if (((PointListEntry)pointList.Items[i]).Point.Id == selectId) { pointList.SelectedIndex = i; break; }
                }
            } else if (pointList.Items.Count > 0) {
                int firstUnresolved = -1;
                for (int i = 0; i < pointList.Items.Count; i++) {
                    if (((PointListEntry)pointList.Items[i]).Point.State != "ok") { firstUnresolved = i; break; }
                }
                pointList.SelectedIndex = firstUnresolved >= 0 ? firstUnresolved : 0;
            }

            SyncSideControl();
            RebuildPlan();
            UpdateCanvasHint();
            canvas.Invalidate();
            NotifySummary("就绪");
        }

        // 只在本页有气缸且 BOM 中存在尚未加入本页的 H/W 传感器时才显示「+ 补带 H/W」
        void UpdateAppendHwVisibility() {
            var host = btnAppendHw.Tag as Panel;
            if (host == null) return;
            var pg = CurrentPage();
            if (pg == null || pg.Points == null) { host.Visible = false; return; }
            var existingIds = new HashSet<string>(pg.Points.Select(p => p.Id));
            var byId = BuildRecordMapById();
            var sensors = project.Records.Where(r => LineupTableData.Category(r.Category) == "气缸传感器" && !existingIds.Contains(r.Id)).ToList();
            bool hasCandidate = false;
            if (sensors.Count > 0) {
                foreach (var pt in pg.Points) {
                    LineupRecord rec;
                    bool isCyl = (byId.TryGetValue(pt.Id ?? "", out rec) && LineupTableData.Category(rec.Category) == "气缸")
                        || LineupTableData.Category(pt.Category) == "气缸";
                    if (!isCyl) continue;
                    string cn = (rec != null && !string.IsNullOrWhiteSpace(rec.Number) ? rec.Number : pt.Number ?? "").Trim();
                    if (sensors.Any(s => (!string.IsNullOrEmpty(pt.Id) && s.CylinderId == pt.Id)
                        || (cn.Length > 0 && (s.Number ?? "").Trim().StartsWith(cn, StringComparison.OrdinalIgnoreCase)))) {
                        hasCandidate = true;
                        break;
                    }
                }
            }
            host.Visible = hasCandidate;
        }

        void SyncSideControl() {
            var pt = CurrentPoint();
            bool has = pt != null;
            sideSeg.Enabled = has;
            btnTag.Enabled = has;
            btnLocate.Enabled = has && app != null;
            btnRemovePt.Enabled = has;
            if (!has) return;
            syncing = true;
            sideSeg.SelectedIndex = pt.Side == "left" ? 0 : pt.Side == "right" ? 2 : 1;
            syncing = false;
        }

        void RebuildPlan() {
            var pg = CurrentPage();
            if (pg == null) { currentPlan = null; return; }
            var byId = new Dictionary<string, LineupRecord>();
            var byNum = new Dictionary<string, LineupRecord>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in project.Records) {
                if (!byId.ContainsKey(r.Id)) byId[r.Id] = r;
                string k = (r.Number ?? "").Trim();
                if (k.Length > 0 && !byNum.ContainsKey(k)) byNum[k] = r;
            }
            currentPlan = LineupCadLayout.Solve(pg, 1, byId, byNum, null);
        }

        void UpdateCanvasHint() {
            var entry = pointList.SelectedItem as PointListEntry;
            var pt = entry == null ? null : entry.Point;
            string num = entry != null && !string.IsNullOrWhiteSpace(entry.Number) ? entry.Number : (pt == null ? "" : (pt.Number ?? ""));
            if (CurrentPage() == null) {
                canvasHint.Text = "点击右上角「+ 新建图页 ▾」截取当前 CAD 视角或导入图片。";
            } else if (pt == null) {
                canvasHint.Text = "本页还没有器件，点击右侧「选器件…」勾选要标注的 BOM 项。";
            } else if (pt.State == "ok") {
                canvasHint.Text = "已定位「" + num + "」 · 左键点三维图可微调箭头起点 · 右键文字行可换左右栏或填备忘";
            } else {
                canvasHint.Text = "待定位「" + num + "」 · 直接在下方三维图上左键点一下它所在的位置（箭头从图指向单元格，点完自动跳下一条）";
            }
        }

        void Persist(string msg) {
            // 即时保存前先取消尚未触发的防抖定时器，防止 450ms 内再触发二次落盘、压入重复撤销栈并覆盖状态栏
            saveDebounce.Stop();
            hasPendingSave = false;
            pendingSaveMsg = "";
            if (commitProject != null) commitProject(LineupEntry.Clone(project), msg);
            NotifySummary(msg);
        }

        void NotifySummary(string msg) {
            if (reportStatus == null || project == null) return;
            var pages = project.CadPages ?? new List<LineupCadPage>();
            var byId = BuildRecordMapById();
            var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pg in pages) {
                if (pg == null || pg.Points == null) continue;
                foreach (var pt in pg.Points) {
                    if (pt == null) continue;
                    LineupRecord rec;
                    string num = (!string.IsNullOrEmpty(pt.Id) && byId.TryGetValue(pt.Id, out rec) && !string.IsNullOrWhiteSpace(rec.Number))
                        ? rec.Number.Trim()
                        : (pt.Number ?? "").Trim();
                    if (num.Length > 0) covered.Add(num);
                }
            }
            int totalBom = project.Records.Count(r => LineupTableData.Categories.Contains(LineupTableData.Category(r.Category)) && !string.IsNullOrWhiteSpace(r.Number));
            reportStatus(msg + " · 共 " + pages.Count + " 页位置图 · 已标 " + covered.Count + " / " + totalBom + " 项");
        }

        // ==================== WYSIWYG 预览画布绘制（与 Excel 导出完全同源） ====================
        void PaintCanvas(object sender, PaintEventArgs e) {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.Clear(Color.FromArgb(0xF4, 0xF5, 0xF7));
            if (currentPlan == null) return;

            int pad = Ui.S(14);
            float availW = Math.Max(100, canvas.ClientSize.Width - pad * 2);
            float availH = Math.Max(100, canvas.ClientSize.Height - pad * 2);
            float pageW = currentPlan.TotalWidthEmu;
            float pageH = currentPlan.TotalHeightEmu;
            canvasScale = Math.Min(availW / pageW, availH / pageH);
            float drawW = pageW * canvasScale;
            float drawH = pageH * canvasScale;
            float ox = pad + (availW - drawW) / 2f;
            float oy = pad + (availH - drawH) / 2f;
            canvasPageRect = new RectangleF(ox, oy, drawW, drawH);

            // 白纸底板与柔边
            g.FillRectangle(Ui.CachedBrush(Color.FromArgb(14, 0, 0, 0)), ox + 2, oy + 2, drawW, drawH);
            g.FillRectangle(Brushes.White, canvasPageRect);
            g.DrawRectangle(Ui.CachedPen(Color.FromArgb(0xD4, 0xD4, 0xD8), 1f), ox, oy, drawW, drawH);

            // 头部标题栏（严格同步公司模板 V3.0：左右白底粗体列头 + 中央 D..I 亮黄底粗黑框插图标题块）
            float hdrH = LineupCadLayout.RowTopEmu(3) * canvasScale;
            float imgColL = ox + LineupCadLayout.ColLeftEmu(LineupCadLayout.ImageFirstCol) * canvasScale;
            float imgColR = ox + LineupCadLayout.ColLeftEmu(LineupCadLayout.ImageLastColExcl) * canvasScale;
            var hdrLinePen = Ui.CachedPen(Color.FromArgb(0x22, 0x22, 0x22), 1.5f);
            g.FillRectangle(Brushes.White, ox, oy, drawW, hdrH);
            g.FillRectangle(Ui.CachedBrush(Color.FromArgb(0xFF, 0xFF, 0x00)), imgColL, oy, imgColR - imgColL, hdrH);
            g.DrawRectangle(Ui.CachedPen(Color.FromArgb(0x88, 0x88, 0x88), 1f), ox, oy, drawW, hdrH);
            g.DrawRectangle(hdrLinePen, imgColL, oy, imgColR - imgColL, hdrH);
            string sta = !string.IsNullOrWhiteSpace(project.StationCode) ? project.StationCode : (project.NumberPrefix ?? "");
            string titleInfo = "插图区域  |  "
                + (string.IsNullOrWhiteSpace(project.ProjectCode) ? "" : project.ProjectCode + " · ")
                + (string.IsNullOrWhiteSpace(sta) ? "" : sta + " · ")
                + (string.IsNullOrWhiteSpace(project.LineCode) ? "" : project.LineCode + " · ")
                + (currentPlan.Page.Name ?? "总图");
            // 画布坐标按物理像素缩放，字体显式使用 GraphicsUnit.Pixel，避免 125%/150% 高 DPI 下被系统按 Point 二次放大裁切
            float hdrFontPx = Math.Max(9f, Math.Min(14f, hdrH * 0.32f));
            using (var fnt = new Font(Ui.Family, hdrFontPx, FontStyle.Bold, GraphicsUnit.Pixel)) {
                var br = Ui.CachedBrush(Color.Black);
                g.DrawString(titleInfo, fnt, br, new RectangleF(imgColL, oy, imgColR - imgColL, hdrH), SfCenter);
                g.DrawString("名称/描述  |  IO点位  |  SK编号", fnt, br, new RectangleF(ox, oy, imgColL - ox, hdrH), SfCenter);
                g.DrawString("SK编号  |  IO点位  |  名称/描述", fnt, br, new RectangleF(imgColR, oy, ox + drawW - imgColR, hdrH), SfCenter);
            }

            // 中央底图（缓存与当前视口等大的预缩放位图，避免每次点选箭头都 CPU 重采样大图）
            float ix = ox + currentPlan.ImageLeftEmu * canvasScale;
            float iy = oy + currentPlan.ImageTopEmu * canvasScale;
            float iw = currentPlan.ImageWidthEmu * canvasScale;
            float ih = currentPlan.ImageHeightEmu * canvasScale;
            canvasImageRect = new RectangleF(ix, iy, iw, ih);
            if (currentImage != null) {
                int tw = Math.Max(1, (int)Math.Round(iw));
                int th = Math.Max(1, (int)Math.Round(ih));
                if (scaledImage == null || scaledImageSize.Width != tw || scaledImageSize.Height != th) {
                    if (scaledImage != null) { scaledImage.Dispose(); scaledImage = null; }
                    scaledImage = new Bitmap(tw, th, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                    using (var sg = Graphics.FromImage(scaledImage)) {
                        sg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        sg.DrawImage(currentImage, 0, 0, tw, th);
                    }
                    scaledImageSize = new Size(tw, th);
                }
                g.DrawImage(scaledImage, (int)Math.Round(ix), (int)Math.Round(iy), tw, th);
            } else {
                g.FillRectangle(Ui.CachedBrush(Color.FromArgb(0xF8, 0xFA, 0xFC)), canvasImageRect);
                g.DrawString("（底图文件缺失，可在右上角「管理本页 ▾ → 重拍底图」）", Ui.F9, Ui.CachedBrush(Ui.Muted), canvasImageRect, SfCenter);
            }
            // 中央 D..I 粗黑外框（与导出 Excel 一致）
            g.DrawRectangle(hdrLinePen, imgColL, oy + hdrH, imgColR - imgColL, drawH - hdrH);

            // 左右标注行 + 红色箭头（Start = 三维图落点，End = 单元格边缘，箭头指向单元格；所有 GDI+ Pen / Cap 严格释放）
            var selectedPt = CurrentPoint();
            float rowPx = LineupCadLayout.RowHeightEmu(3) * canvasScale;
            float fontPx = Math.Max(6f, Math.Min(13f, rowPx * 0.72f));
            var gridPen = Ui.CachedPen(Color.FromArgb(0xD4, 0xD4, 0xD8), 1f);
            using (var rowFont = new Font(Ui.Family, fontPx, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var numFont = new Font(Ui.Family, fontPx, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var arrowPen = new Pen(Color.FromArgb(0xE0, 0x00, 0x00), 1.8f))
            using (var selArrowPen = new Pen(Ui.Accent, 2.4f))
            using (var selRingPen = new Pen(Ui.Accent, 2.0f))
            using (var cap = new AdjustableArrowCap(4.5f, 5.5f, true)) {
                arrowPen.CustomEndCap = cap;
                selArrowPen.CustomEndCap = cap;

                foreach (var co in currentPlan.Callouts) {
                    int rowOff = co.ExcelRow - currentPlan.StartRow;
                    float ry = oy + LineupCadLayout.RowTopEmu(rowOff) * canvasScale;
                    float rh = LineupCadLayout.RowHeightEmu(rowOff) * canvasScale;
                    bool isSel = selectedPt != null && co.Point == selectedPt;

                    RectangleF box = co.Side == "left"
                        ? new RectangleF(ox + 1, ry, imgColL - ox - 2, rh)
                        : new RectangleF(imgColR + 1, ry, ox + drawW - imgColR - 2, rh);

                    Color bg = isSel ? Ui.AccentSoft : (co.HasArrow ? Color.White : Color.FromArgb(0xFF, 0xFB, 0xEB));
                    g.FillRectangle(Ui.CachedBrush(bg), box);
                    g.DrawRectangle(gridPen, box.X, box.Y, box.Width, box.Height);

                    string tagPart = string.IsNullOrWhiteSpace(co.Tag) ? "" : " [" + co.Tag.Trim() + "]";
                    string text = co.Side == "left"
                        ? (string.IsNullOrEmpty(co.Description) ? co.Number + tagPart : co.Description + tagPart + "  |  " + co.Number)
                        : (string.IsNullOrEmpty(co.Description) ? co.Number + tagPart : co.Number + tagPart + "  |  " + co.Description);
                    g.DrawString(text, isSel ? numFont : rowFont, Ui.CachedBrush(co.HasArrow ? Ui.Text : Ui.Warn),
                        RectangleF.Inflate(box, -4, 0), co.Side == "left" ? SfCalloutLeft : SfCalloutRight);

                    if (co.HasArrow) {
                        float sx = ox + co.StartX * canvasScale;
                        float sy = oy + co.StartY * canvasScale;
                        float ex = ox + co.EndX * canvasScale;
                        float ey = oy + co.EndY * canvasScale;
                        g.DrawLine(isSel ? selArrowPen : arrowPen, sx, sy, ex, ey);
                        g.FillEllipse(Ui.CachedBrush(isSel ? Ui.Accent : Color.FromArgb(0xE0, 0x00, 0x00)), sx - 2.5f, sy - 2.5f, 5f, 5f);
                        if (isSel) g.DrawEllipse(selRingPen, sx - 6.5f, sy - 6.5f, 13f, 13f);
                    }
                }
            }
        }

        void CanvasClick(object sender, MouseEventArgs e) {
            if (currentPlan == null) return;
            float ox = canvasPageRect.Left, oy = canvasPageRect.Top;
            float imgColL = ox + LineupCadLayout.ColLeftEmu(LineupCadLayout.ImageFirstCol) * canvasScale;
            float imgColR = ox + LineupCadLayout.ColLeftEmu(LineupCadLayout.ImageLastColExcl) * canvasScale;

            // 点左右标注行：左键选中，右键弹出快捷微调菜单
            foreach (var co in currentPlan.Callouts) {
                int rowOff = co.ExcelRow - currentPlan.StartRow;
                float ry = oy + LineupCadLayout.RowTopEmu(rowOff) * canvasScale;
                float rh = LineupCadLayout.RowHeightEmu(rowOff) * canvasScale;
                RectangleF box = co.Side == "left"
                    ? new RectangleF(ox, ry, imgColL - ox, rh)
                    : new RectangleF(imgColR, ry, canvasPageRect.Right - imgColR, rh);
                if (box.Contains(e.Location)) {
                    for (int i = 0; i < pointList.Items.Count; i++) {
                        if (((PointListEntry)pointList.Items[i]).Point == co.Point) { pointList.SelectedIndex = i; break; }
                    }
                    if (e.Button == MouseButtons.Right) pointMenu.Show(canvas, e.Location);
                    return;
                }
            }

            if (e.Button == MouseButtons.Right) {
                if (CurrentPoint() != null) pointMenu.Show(canvas, e.Location);
                return;
            }
            if (e.Button != MouseButtons.Left) return;

            // 左键点底图：落点并自动跳到下一条未定位项
            if (canvasImageRect.Width > 10 && canvasImageRect.Height > 10 && canvasImageRect.Contains(e.Location)) {
                var pt = CurrentPoint();
                if (pt == null) return;
                bool wasUnresolved = pt.State != "ok";
                pt.X = Math.Max(0.005, Math.Min(0.995, (e.X - canvasImageRect.Left) / (double)canvasImageRect.Width));
                pt.Y = Math.Max(0.005, Math.Min(0.995, (e.Y - canvasImageRect.Top) / (double)canvasImageRect.Height));
                pt.State = "ok";
                pt.Method = "manual";
                pt.Reason = "";

                // 增量更新画布、列表项与页签计数，450ms 防抖落盘，连点多处箭头彻底无卡顿
                RebuildPlan();
                UpdateActivePageTabLabel();
                int nextIdx = pointList.SelectedIndex;
                if (wasUnresolved) {
                    for (int i = 0; i < pointList.Items.Count; i++) {
                        if (((PointListEntry)pointList.Items[i]).Point.State != "ok") { nextIdx = i; break; }
                    }
                }
                if (nextIdx >= 0 && nextIdx < pointList.Items.Count && pointList.SelectedIndex != nextIdx)
                    pointList.SelectedIndex = nextIdx;
                else {
                    SyncSideControl();
                    UpdateCanvasHint();
                    canvas.Invalidate();
                }
                pointList.Invalidate();
                SchedulePersist("已设置 " + pt.Number + " 箭头落点");
            }
        }

        // ==================== 页面操作 ====================
        void CreateNewPage(bool useIsometric) {
            if (capturing) return;
            LineupLocation.EnsureActiveAssemblyWindow(app, assembly);
            var picked = PromptNewPageOptions(useIsometric);
            if (picked == null) return;

            // 抓图前先刷掉待落盘的防抖改动，防止 CapturePage 期间 Application.DoEvents 重入触发旧状态刷盘
            FlushPendingSave();
            LineupLocation.EnsureActiveAssemblyWindow(app, assembly);
            capturing = true;
            if (suspendOwner != null) suspendOwner(true);
            try {
                var page = LineupLocation.CapturePage(
                    app, assembly, project, picked.RecordIds, picked.PageName,
                    useIsometric, picked.ZoomToTargets, picked.AutoDetect,
                    msg => { if (reportStatus != null) reportStatus(msg); Application.DoEvents(); });
                project.CadPages.Add(page);
                activePageIndex = project.CadPages.Count - 1;
                Persist("已新建位置图「" + page.Name + "」（识别 " + page.Points.Count(p => p.State == "ok") + "/" + page.Points.Count + "）");
                RefreshView();
            } finally {
                capturing = false;
                if (suspendOwner != null) suspendOwner(false);
            }
        }

        void ImportImagePage() {
            using (var ofd = new OpenFileDialog { Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp", Title = "选择三维截图文件" }) {
                if (ofd.ShowDialog(FindForm()) != DialogResult.OK) return;
                var picked = PromptNewPageOptions(false, true, Path.GetFileNameWithoutExtension(ofd.FileName));
                if (picked == null) return;
                string assets = LineupProject.CadAssetsDir(project.AssemblyFile);
                Directory.CreateDirectory(assets);
                string ext = Path.GetExtension(ofd.FileName).ToLowerInvariant();
                if (ext != ".png" && ext != ".jpg" && ext != ".jpeg") ext = ".png";
                string fileName = "page-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ext;
                string dst = Path.Combine(assets, fileName);
                int w, h;
                using (var bmp = new Bitmap(ofd.FileName)) {
                    w = bmp.Width; h = bmp.Height;
                    if (ext == ".png") bmp.Save(dst, System.Drawing.Imaging.ImageFormat.Png);
                    else File.Copy(ofd.FileName, dst, true);
                }
                var pts = new List<LineupLocationPoint>();
                foreach (var r in project.Records.Where(x => picked.RecordIds.Contains(x.Id))) {
                    pts.Add(new LineupLocationPoint { Id = r.Id, Number = r.Number, Model = r.Model, Category = r.Category, Description = r.Description, State = "unresolved", Reason = "请在图上左键点选箭头位置" });
                }
                var page = new LineupCadPage { Name = picked.PageName, LineCode = project.LineCode, ImageFile = fileName, ViewMode = "image", Width = w, Height = h, Points = pts };
                project.CadPages.Add(page);
                activePageIndex = project.CadPages.Count - 1;
                Persist("已从图片新建「" + page.Name + "」");
                RefreshView();
            }
        }

        void RecaptureCurrentPage() {
            if (capturing) return;
            var pg = CurrentPage();
            if (pg == null) throw new InvalidOperationException("请先选择一页位置图。");
            LineupLocation.EnsureActiveAssemblyWindow(app, assembly);
            var ask = MessageBox.Show(FindForm(),
                "用当前 CAD 画面重拍「" + pg.Name + "」并重新识别各件位置吗？\n\n· 选「是」：重拍并自动重新识别坐标\n· 选「否」：仅更换底图，保留已有手动箭头",
                "重拍底图", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (ask == DialogResult.Cancel) return;
            bool redetect = (ask == DialogResult.Yes);
            FlushPendingSave();
            LineupLocation.EnsureActiveAssemblyWindow(app, assembly);
            capturing = true;
            if (suspendOwner != null) suspendOwner(true);
            try {
                var oldPts = pg.Points ?? new List<LineupLocationPoint>();
                var ids = oldPts.Select(p => p.Id).ToList();
                var captured = LineupLocation.CapturePage(app, assembly, project, ids, pg.Name, false, false, redetect, null);
                pg.ImageFile = captured.ImageFile;
                pg.Width = captured.Width;
                pg.Height = captured.Height;
                if (redetect) {
                    // 重识别后按 Id（兜底按 Number）保留原有手工指定的左/右栏位与备忘标签，并保留未关联 BOM Id 的手工点
                    var oldById = new Dictionary<string, LineupLocationPoint>(StringComparer.Ordinal);
                    var oldByNum = new Dictionary<string, LineupLocationPoint>(StringComparer.OrdinalIgnoreCase);
                    foreach (var op in oldPts) {
                        if (op == null) continue;
                        if (!string.IsNullOrEmpty(op.Id) && !oldById.ContainsKey(op.Id)) oldById[op.Id] = op;
                        string on = (op.Number ?? "").Trim();
                        if (on.Length > 0 && !oldByNum.ContainsKey(on)) oldByNum[on] = op;
                    }
                    var matchedOld = new HashSet<LineupLocationPoint>();
                    foreach (var np in captured.Points) {
                        LineupLocationPoint prev = null;
                        if (!string.IsNullOrEmpty(np.Id)) oldById.TryGetValue(np.Id, out prev);
                        if (prev == null && !string.IsNullOrWhiteSpace(np.Number)) oldByNum.TryGetValue(np.Number.Trim(), out prev);
                        if (prev != null) {
                            np.Side = prev.Side ?? "";
                            np.Tag = prev.Tag ?? "";
                            matchedOld.Add(prev);
                        }
                    }
                    foreach (var op in oldPts) {
                        if (op != null && !matchedOld.Contains(op)) {
                            var keep = op.Copy();
                            keep.State = "unresolved";
                            keep.X = 0; keep.Y = 0; keep.Method = "";
                            keep.Reason = "重拍后未自动匹配到 BOM 项，请重新点选箭头位置";
                            captured.Points.Add(keep);
                        }
                    }
                    pg.Points = captured.Points;
                }
                Persist("已重拍「" + pg.Name + "」底图");
                RefreshView();
            } finally {
                capturing = false;
                if (suspendOwner != null) suspendOwner(false);
            }
        }

        void RenameCurrentPage() {
            var pg = CurrentPage(); if (pg == null) return;
            string name = Ui.Prompt(FindForm(), "重命名位置图页", "机构名称 / 页标题：", pg.Name);
            if (string.IsNullOrWhiteSpace(name)) return;
            pg.Name = name.Trim();
            Persist("已重命名为「" + pg.Name + "」");
            RebuildPageTabs();
            RebuildPlan();
            canvas.Invalidate();
        }

        void MoveCurrentPage(int delta) {
            int i = activePageIndex, j = i + delta;
            if (i < 0 || j < 0 || j >= project.CadPages.Count) return;
            var tmp = project.CadPages[i]; project.CadPages[i] = project.CadPages[j]; project.CadPages[j] = tmp;
            activePageIndex = j;
            Persist("已调整图页顺序");
            RefreshView();
        }

        void DeleteCurrentPage() {
            int i = activePageIndex;
            if (i < 0 || i >= project.CadPages.Count) return;
            string name = project.CadPages[i].Name;
            if (MessageBox.Show(FindForm(), "确定删除位置图页「" + name + "」？（不会影响清单数据）", "删除位置图页", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            project.CadPages.RemoveAt(i);
            activePageIndex = project.CadPages.Count == 0 ? -1 : Math.Max(0, i - 1);
            Persist("已删除位置图页「" + name + "」");
            RefreshView();
        }

        // ==================== 标注项操作 ====================
        void EditPagePointsDialog() {
            var pg = CurrentPage(); if (pg == null) throw new InvalidOperationException("请先新建一页位置图。");
            var currentIds = new HashSet<string>(pg.Points.Select(p => p.Id));
            var selected = PickBomRecordsDialog("勾选「" + pg.Name + "」要标注的器件", currentIds);
            if (selected == null) return;
            var existingById = new Dictionary<string, LineupLocationPoint>(StringComparer.Ordinal);
            foreach (var p in pg.Points)
                if (p != null && !string.IsNullOrEmpty(p.Id) && !existingById.ContainsKey(p.Id)) existingById[p.Id] = p;
            var nextPts = new List<LineupLocationPoint>();
            foreach (var r in project.Records.Where(x => selected.Contains(x.Id))) {
                LineupLocationPoint old;
                if (existingById.TryGetValue(r.Id, out old)) {
                    old.Number = r.Number; old.Model = r.Model; old.Category = r.Category; old.Description = r.Description;
                    nextPts.Add(old);
                } else {
                    nextPts.Add(new LineupLocationPoint { Id = r.Id, Number = r.Number, Model = r.Model, Category = r.Category, Description = r.Description, State = "unresolved", Reason = "请在图上点选箭头落点" });
                }
            }
            pg.Points = nextPts;
            Persist("已更新「" + pg.Name + "」器件（共 " + nextPts.Count + " 项）");
            RebuildPageTabs();
            RefreshPoints(null);
        }

        void SetSelectedSide(string side) {
            var pt = CurrentPoint(); if (pt == null) return;
            if (pt.Side == side) return;
            pt.Side = side;
            Persist("已调整 " + pt.Number + " 栏位");
            RefreshPoints(pt.Id);
        }

        void EditSelectedTag() {
            var pt = CurrentPoint(); if (pt == null) return;
            string tag = Ui.Prompt(FindForm(), "备忘 / 接线箱号", "填入该项备忘标签（如 1JB，留空清除）：", pt.Tag ?? "");
            if (tag == null) return;
            pt.Tag = tag.Trim();
            Persist("已更新 " + pt.Number + " 备忘");
            RefreshPoints(pt.Id);
        }

        void AppendCylinderSensorsToPage() {
            var pg = CurrentPage();
            if (pg == null) return;
            var existingIds = new HashSet<string>(pg.Points.Select(p => p.Id));
            var existingNums = new HashSet<string>(pg.Points.Select(p => (p.Number ?? "").Trim()).Where(s => s.Length > 0), StringComparer.OrdinalIgnoreCase);
            var sensors = project.Records.Where(r => LineupTableData.Category(r.Category) == "气缸传感器").ToList();
            var byId = BuildRecordMapById();
            var nextPts = new List<LineupLocationPoint>();
            int added = 0;
            foreach (var pt in pg.Points) {
                nextPts.Add(pt);
                LineupRecord rec;
                bool isCyl = (byId.TryGetValue(pt.Id ?? "", out rec) && LineupTableData.Category(rec.Category) == "气缸")
                    || LineupTableData.Category(pt.Category) == "气缸";
                if (!isCyl) continue;
                string cylNum = (rec != null && !string.IsNullOrWhiteSpace(rec.Number) ? rec.Number : pt.Number ?? "").Trim();
                var matched = sensors.Where(s =>
                    (!string.IsNullOrEmpty(pt.Id) && s.CylinderId == pt.Id)
                    || (cylNum.Length > 0 && System.Text.RegularExpressions.Regex.IsMatch((s.Number ?? "").Trim(), "^" + System.Text.RegularExpressions.Regex.Escape(cylNum) + "[HWM][0-9]*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
                    .OrderBy(s => s.Number, StringComparer.OrdinalIgnoreCase).ToList();
                int k = 0;
                foreach (var s in matched) {
                    if (existingIds.Contains(s.Id) || (!string.IsNullOrWhiteSpace(s.Number) && existingNums.Contains(s.Number.Trim()))) continue;
                    k++;
                    nextPts.Add(new LineupLocationPoint {
                        Id = s.Id, Number = s.Number, Model = s.Model, Category = s.Category, Description = s.Description,
                        Side = pt.Side,
                        State = pt.State == "ok" ? "ok" : "unresolved",
                        Method = pt.State == "ok" ? "manual" : "",
                        Reason = pt.State == "ok" ? "" : "继承自未定位气缸，请在图上点选位置",
                        X = pt.State == "ok" ? pt.X : 0,
                        Y = pt.State == "ok" ? Math.Min(0.995, pt.Y + k * 0.014) : 0
                    });
                    existingIds.Add(s.Id);
                    if (!string.IsNullOrWhiteSpace(s.Number)) existingNums.Add(s.Number.Trim());
                    added++;
                }
            }
            if (added == 0) { NotifySummary("本页气缸没有需要补充的 H/W 传感器"); return; }
            pg.Points = nextPts;
            Persist("已为本页气缸补带 " + added + " 条 H/W 传感器");
            RebuildPageTabs();
            RefreshPoints(null);
        }

        void ClearSelectedCoord() {
            var pt = CurrentPoint(); if (pt == null) return;
            pt.State = "unresolved"; pt.X = 0; pt.Y = 0; pt.Method = ""; pt.Reason = "已清除坐标，请重新点选";
            Persist("已清除 " + pt.Number + " 坐标");
            RebuildPageTabs();
            RefreshPoints(pt.Id);
        }

        void RemoveSelectedPoint() {
            var pg = CurrentPage(); var pt = CurrentPoint();
            if (pg == null || pt == null) return;
            pg.Points.Remove(pt);
            Persist("已从本页移除 " + pt.Number);
            RebuildPageTabs();
            RefreshPoints(null);
        }

        void LocateSelectedPointInCad() {
            var pt = CurrentPoint(); if (pt == null || locateRecord == null) return;
            var rec = project.Records.FirstOrDefault(r => r.Id == pt.Id);
            if (rec == null) throw new InvalidOperationException("找不到对应的清单记录。");
            locateRecord(rec);
        }

        // ==================== 精简版新建图页 / 勾选器件弹窗 ====================
        sealed class NewPageRequest {
            public string PageName = "总图";
            public List<string> RecordIds = new List<string>();
            public bool ZoomToTargets;
            public bool AutoDetect;
        }

        NewPageRequest PromptNewPageOptions(bool isIso, bool isImageImport = false, string defaultName = null) {
            string defName = !string.IsNullOrWhiteSpace(defaultName) ? defaultName : (project.CadPages.Count == 0 ? "总图" : "机构" + project.CadPages.Count);
            using (var d = new Form()) {
                Ui.Shell(d, isImageImport ? "从图片新建位置图" : (isIso ? "正交等轴测新建位置图" : "截取当前 CAD 画面"), 640, 520, 520, 420);
                d.FormBorderStyle = FormBorderStyle.Sizable;

                var top = new Panel { Dock = DockStyle.Top, Height = Ui.S(isImageImport ? 54 : 82), Padding = Ui.SPad(16, 12, 16, 6), BackColor = Ui.Rail };
                var lbl = new Label { Text = "页标题 / 机构名", Left = Ui.S(16), Top = Ui.S(15), AutoSize = true, Font = Ui.F9B, ForeColor = Ui.Text2 };
                var txtName = new TextBox { Left = Ui.S(136), Top = Ui.S(12), Width = Ui.S(240), Text = defName, BorderStyle = BorderStyle.FixedSingle };
                var chkZoom = new CheckBox { Text = "自动缩放到所选件", Left = Ui.S(396), Top = Ui.S(14), AutoSize = true, Checked = isIso, Visible = !isImageImport };
                bool anyBound = project.Records.Any(r => !string.IsNullOrEmpty(r.ReferenceKey));
                var chkDetect = new CheckBox {
                    Text = anyBound ? "自动识别已绑三维件的箭头位置（不勾则秒截图后手工点选）" : "自动识别三维位置（当前尚未绑定 3D 件，建议不勾直接手工点选）",
                    Left = Ui.S(16), Top = Ui.S(48), AutoSize = true, Checked = !isImageImport && anyBound, Visible = !isImageImport, ForeColor = Ui.Muted
                };
                top.Controls.AddRange(new Control[] { lbl, txtName, chkZoom, chkDetect });
                top.Paint += (s, e) => { using (var p = new Pen(Ui.Line)) e.Graphics.DrawLine(p, 0, top.Height - 1, top.Width, top.Height - 1); };

                var covered = new HashSet<string>(project.CadPages.SelectMany(pg => pg.Points ?? new List<LineupLocationPoint>()).Select(p => p.Id));
                var items = project.Records.Where(r => LineupTableData.Categories.Contains(LineupTableData.Category(r.Category))).ToList();
                var checkedIds = new HashSet<string>();
                foreach (var r in items) {
                    string cat = LineupTableData.Category(r.Category);
                    bool primary = cat == "气缸" || cat == "普通传感器" || cat == "其他电气件";
                    if (project.CadPages.Count == 0 && primary) checkedIds.Add(r.Id);
                    else if (covered.Count > 0 && !covered.Contains(r.Id) && primary) checkedIds.Add(r.Id);
                }

                var picker = BuildRecordChecklist(items, checkedIds, covered, covered.Count > 0 ? 0 : 1);
                var bottom = new ActionBar { Dock = DockStyle.Bottom, Height = Ui.S(58), Padding = Ui.SPad(16, 0, 16, 0) };
                var ok = Ui.Primary(isImageImport ? "创建位置图" : "开始截图建页", 136, 36); ok.DialogResult = DialogResult.OK;
                var cancel = Ui.Secondary("取消", 76, 34); cancel.DialogResult = DialogResult.Cancel;
                bottom.AddRight(cancel); bottom.AddRight(ok);
                bottom.Status.Text = "勾选需要在这张图上标箭头的器件（建页后随时可增删）";
                d.AcceptButton = ok; d.CancelButton = cancel;

                d.Controls.Add(picker);
                d.Controls.Add(top);
                d.Controls.Add(bottom);

                if (d.ShowDialog(FindForm()) != DialogResult.OK) return null;
                return new NewPageRequest {
                    PageName = string.IsNullOrWhiteSpace(txtName.Text) ? defName : txtName.Text.Trim(),
                    RecordIds = items.Where(r => checkedIds.Contains(r.Id)).Select(r => r.Id).ToList(),
                    ZoomToTargets = chkZoom.Checked,
                    AutoDetect = chkDetect.Checked
                };
            }
        }

        HashSet<string> PickBomRecordsDialog(string title, HashSet<string> preChecked) {
            using (var d = new Form()) {
                Ui.Shell(d, title, 620, 490, 500, 380);
                d.FormBorderStyle = FormBorderStyle.Sizable;
                var coveredOther = new HashSet<string>(project.CadPages.Where(pg => pg != CurrentPage()).SelectMany(pg => pg.Points ?? new List<LineupLocationPoint>()).Select(p => p.Id));
                var items = project.Records.Where(r => LineupTableData.Categories.Contains(LineupTableData.Category(r.Category))).ToList();
                var checkedIds = new HashSet<string>(preChecked ?? Enumerable.Empty<string>());

                var picker = BuildRecordChecklist(items, checkedIds, coveredOther, 1);
                var bottom = new ActionBar { Dock = DockStyle.Bottom, Height = Ui.S(58), Padding = Ui.SPad(16, 0, 16, 0) };
                var ok = Ui.Primary("确定", 92, 36); ok.DialogResult = DialogResult.OK;
                var cancel = Ui.Secondary("取消", 76, 34); cancel.DialogResult = DialogResult.Cancel;
                bottom.AddRight(cancel); bottom.AddRight(ok);
                d.AcceptButton = ok; d.CancelButton = cancel;
                d.Controls.Add(picker);
                d.Controls.Add(bottom);
                return d.ShowDialog(FindForm()) == DialogResult.OK ? checkedIds : null;
            }
        }

        // 统一简洁的器件勾选面板：用 Segmented 替代旧版 6 个杂乱筛选按钮
        Control BuildRecordChecklist(List<LineupRecord> items, HashSet<string> checkedIds, HashSet<string> coveredOther, int initFilterIndex) {
            var host = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Bg, Padding = Ui.SPad(16, 10, 16, 8) };
            var bar = new Panel { Dock = DockStyle.Top, Height = Ui.S(38), BackColor = Ui.Bg };
            var seg = new Segmented("未上图", "主器件", "全部") { Height = Ui.S(30), Dock = DockStyle.Left };
            seg.Width = seg.PreferredWidth + Ui.S(10);
            seg.SelectedIndex = initFilterIndex;

            var searchBox = new TextBox { Width = Ui.S(160), BorderStyle = BorderStyle.FixedSingle };
            var btnAll = Ui.Small("全选"); ((FlatButton)btnAll).SetFore(Ui.Text2);
            var btnNone = Ui.Small("清空"); ((FlatButton)btnNone).SetFore(Ui.Text2);
            var rightTools = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false, BackColor = Ui.Bg };
            searchBox.Margin = Ui.SPad(0, 3, 6, 0);
            btnAll.Margin = Ui.SPad(0, 2, 2, 0);
            btnNone.Margin = Ui.SPad(0, 2, 0, 0);
            rightTools.Controls.AddRange(new Control[] { searchBox, btnAll, btnNone });
            bar.Controls.Add(seg);
            bar.Controls.Add(rightTools);

            var clb = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, Font = Ui.F9, BorderStyle = BorderStyle.FixedSingle, IntegralHeight = false };
            var rows = new List<LineupRecord>();
            bool updating = false;
            Action refresh = () => {
                updating = true;
                clb.Items.Clear(); rows.Clear();
                string q = (searchBox.Text ?? "").Trim();
                int mode = seg.SelectedIndex;
                foreach (var r in items) {
                    string cat = LineupTableData.Category(r.Category);
                    if (mode == 0 && coveredOther.Contains(r.Id)) continue;
                    if (mode == 1 && cat != "气缸" && cat != "普通传感器" && cat != "其他电气件") continue;
                    if (q.Length > 0) {
                        string hay = (r.Number ?? "") + " " + (r.Description ?? "") + " " + (r.Notes ?? "") + " " + (r.Model ?? "");
                        if (hay.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    }
                    rows.Add(r);
                    string badge = (coveredOther.Contains(r.Id) ? "[已上图] " : "") + (string.IsNullOrEmpty(r.ReferenceKey) ? "" : "[3D] ");
                    clb.Items.Add(badge + r.Number + "  ·  " + r.Description + (string.IsNullOrWhiteSpace(r.Notes) ? "" : " (" + r.Notes + ")"), checkedIds.Contains(r.Id));
                }
                updating = false;
            };
            clb.ItemCheck += (s, e) => {
                if (updating || e.Index < 0 || e.Index >= rows.Count) return;
                if (e.NewValue == CheckState.Checked) checkedIds.Add(rows[e.Index].Id);
                else checkedIds.Remove(rows[e.Index].Id);
            };
            seg.SelectedIndexChanged += (s, e) => refresh();
            searchBox.TextChanged += (s, e) => refresh();
            btnAll.Click += (s, e) => { updating = true; for (int i = 0; i < rows.Count; i++) { checkedIds.Add(rows[i].Id); clb.SetItemChecked(i, true); } updating = false; };
            btnNone.Click += (s, e) => { updating = true; for (int i = 0; i < rows.Count; i++) { checkedIds.Remove(rows[i].Id); clb.SetItemChecked(i, false); } updating = false; };
            refresh();

            host.Controls.Add(clb);
            host.Controls.Add(bar);
            return host;
        }

        void Try(Action act) {
            try { act(); }
            catch (Exception ex) {
                MessageBox.Show(FindForm(), ex.Message, "CAD 位置图", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        void DisposeImage() {
            if (scaledImage != null) { scaledImage.Dispose(); scaledImage = null; scaledImageSize = Size.Empty; }
            if (currentImage != null) { currentImage.Dispose(); currentImage = null; }
        }
    }
}
