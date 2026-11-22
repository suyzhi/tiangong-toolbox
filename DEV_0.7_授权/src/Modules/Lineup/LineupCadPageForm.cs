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
    // 「LineUP第三版_CAD」可视化多页位置图工作台：
    //   · 左侧：管理多页位置图（总图 / 各子机构图），支持当前 CAD 视角抓图、正交等轴测抓图、本地图片导入；
    //   · 中央：与最终导出 Excel 完全同源（LineupCadLayout）的 WYSIWYG 预览画布，左键点图直接落点/微调红色箭头；
    //   · 右侧：管理本页标注项、左右栏分配、增删 BOM 项（自动标出未上图项），一键导出标准 5 页签 .xlsx。
    public sealed class LineupCadPageForm : Form {
        readonly F.Application app;
        readonly A.AssemblyDocument assembly;
        readonly Func<LineupProject> getProject;
        readonly Action<LineupProject, string> commitProject;
        readonly Action<LineupRecord> locateRecord;
        readonly Action<IWin32Window> exportXlsxAction;
        readonly Action<bool> suspendOwner;

        LineupProject project;
        readonly ListBox pageList = new ListBox();
        readonly ListBox pointList = new ListBox();
        readonly CanvasPanel canvas = new CanvasPanel();
        readonly TextBox txtProject = new TextBox();
        readonly TextBox txtStation = new TextBox();
        readonly TextBox txtLine = new TextBox();
        readonly Label statusLabel = new Label();
        readonly Label canvasHint = new Label();

        Bitmap currentImage;
        string currentImagePath = "";
        LineupCadPagePlan currentPlan;
        RectangleF canvasImageRect;
        RectangleF canvasPageRect;
        float canvasScale = 1f;
        bool syncing;

        sealed class CanvasPanel : Panel {
            public CanvasPanel() {
                DoubleBuffered = true;
                ResizeRedraw = true;
                BackColor = Color.FromArgb(0xF3, 0xF4, 0xF6);
            }
        }

        sealed class PageListEntry {
            public LineupCadPage Page;
            public override string ToString() {
                int total = Page.Points == null ? 0 : Page.Points.Count;
                int ok = Page.Points == null ? 0 : Page.Points.Count(p => p.State == "ok");
                return (string.IsNullOrWhiteSpace(Page.Name) ? "未命名" : Page.Name) + "  (" + ok + "/" + total + ")";
            }
        }

        sealed class PointListEntry {
            public LineupLocationPoint Point;
            public string Description = "";
            public override string ToString() {
                string badge = Point.State == "ok" ? (Point.Method == "manual" ? "[手] " : "[✓] ") : "[待] ";
                string side = Point.Side == "left" ? "← " : Point.Side == "right" ? "→ " : "";
                string tag = string.IsNullOrWhiteSpace(Point.Tag) ? "" : " [" + Point.Tag.Trim() + "]";
                return badge + side + Point.Number + tag + (string.IsNullOrEmpty(Description) ? "" : " · " + Description);
            }
        }

        public LineupCadPageForm(
            F.Application host,
            A.AssemblyDocument doc,
            Func<LineupProject> getProjectFn,
            Action<LineupProject, string> commitProjectFn,
            Action<LineupRecord> locateRecordFn,
            Action<IWin32Window> exportXlsxFn,
            Action<bool> suspendOwnerFn) {

            app = host; assembly = doc;
            getProject = getProjectFn; commitProject = commitProjectFn;
            locateRecord = locateRecordFn; exportXlsxAction = exportXlsxFn;
            suspendOwner = suspendOwnerFn;
            project = LineupEntry.Clone(getProject());

            AutoScaleMode = AutoScaleMode.None;
            Text = "LineUP 第三版 CAD 位置图工作台";
            Font = Ui.F9;
            BackColor = Ui.Bg;
            ForeColor = Ui.Text;
            var area = Screen.PrimaryScreen.WorkingArea;
            Width = Math.Min(Ui.S(1360), area.Width - Ui.S(24));
            Height = Math.Min(Ui.S(820), area.Height - Ui.S(24));
            MinimumSize = new Size(Math.Min(Ui.S(1020), area.Width - Ui.S(24)), Math.Min(Ui.S(600), area.Height - Ui.S(24)));
            StartPosition = FormStartPosition.CenterParent;

            BuildLayout();
            LoadMetadataBoxes();
            RefreshPages(0);

            FormClosed += (s, e) => DisposeImage();
        }

        void BuildLayout() {
            // 顶部元数据 + 主导出条
            var top = new Panel { Dock = DockStyle.Top, Height = Ui.S(48), BackColor = Ui.Rail, Padding = Ui.SPad(12, 8, 12, 8) };
            var topFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, BackColor = Ui.Rail };
            Action<string, TextBox, int> addMeta = (lbl, box, w) => {
                topFlow.Controls.Add(new Label { Text = lbl, AutoSize = true, Margin = Ui.SPad(4, 7, 2, 0), ForeColor = Ui.Text2, Font = Ui.F9B });
                box.Width = Ui.S(w); box.Margin = Ui.SPad(0, 3, 10, 0);
                box.Leave += (s, e) => SaveMeta();
                topFlow.Controls.Add(box);
            };
            addMeta("项目编号", txtProject, 100);
            addMeta("工位号", txtStation, 90);
            addMeta("线体号", txtLine, 60);

            var btnSaveMeta = Ui.Secondary("应用抬头", 82, 30);
            btnSaveMeta.Margin = Ui.SPad(2, 1, 12, 0);
            btnSaveMeta.Click += (s, e) => { SaveMeta(); RebuildPlan(); canvas.Invalidate(); };
            topFlow.Controls.Add(btnSaveMeta);

            var btnExport = Ui.Primary("导出标准 Lineup (.xlsx)", 172, 30);
            btnExport.Margin = Ui.SPad(6, 1, 4, 0);
            btnExport.Click += (s, e) => Try(() => {
                SaveMeta();
                if (exportXlsxAction != null) {
                    exportXlsxAction(this);
                    var fresh = getProject();
                    if (fresh != null) {
                        project.ProjectCode = fresh.ProjectCode;
                        project.StationCode = fresh.StationCode;
                        project.LineCode = fresh.LineCode;
                        project.Revision = fresh.Revision;
                        project.Designer = fresh.Designer;
                        project.Editor = fresh.Editor;
                        project.Export11Columns = fresh.Export11Columns;
                        LoadMetadataBoxes();
                        RebuildPlan();
                        canvas.Invalidate();
                    }
                }
            });
            topFlow.Controls.Add(btnExport);
            top.Controls.Add(topFlow);
            top.Paint += (s, e) => { using (var pen = new Pen(Ui.Line)) e.Graphics.DrawLine(pen, 0, top.Height - 1, top.Width, top.Height - 1); };

            // 底部状态条
            statusLabel.Dock = DockStyle.Bottom;
            statusLabel.Height = Ui.S(34);
            statusLabel.BackColor = Ui.Bg;
            statusLabel.ForeColor = Ui.Muted;
            statusLabel.Font = Ui.F8;
            statusLabel.Padding = Ui.SPad(12, 0, 12, 0);
            statusLabel.TextAlign = ContentAlignment.MiddleLeft;

            // 左侧：位置图页管理
            var leftPane = new Panel { Dock = DockStyle.Left, Width = Ui.S(230), BackColor = Ui.Rail, Padding = Ui.SPad(10, 10, 10, 10) };
            var leftTitle = new Label { Text = "CAD 位置图页（打印一页一图）", Dock = DockStyle.Top, Height = Ui.S(24), Font = Ui.F9B, ForeColor = Ui.Text };
            pageList.Dock = DockStyle.Fill;
            pageList.Font = Ui.F9;
            pageList.BorderStyle = BorderStyle.FixedSingle;
            pageList.SelectedIndexChanged += (s, e) => { if (!syncing) OnPageSelected(); };

            var leftBtns = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = Ui.S(198), FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = Ui.SPad(0, 6, 0, 0), BackColor = Ui.Rail };
            Action<string, bool, Action> addLBtn = (txt, primary, act) => {
                var b = primary ? Ui.Primary(txt, 206, 29) : Ui.Secondary(txt, 206, 28);
                b.Margin = Ui.SPad(0, 2, 0, 2);
                b.Click += (s, e) => Try(act);
                leftBtns.Controls.Add(b);
            };
            addLBtn("+ 当前 CAD 视角新建页", true, () => CreateNewPage(false));
            addLBtn("+ 正交等轴测新建页", false, () => CreateNewPage(true));
            addLBtn("+ 导入本地截图建页…", false, ImportImagePage);
            addLBtn("重拍当前页 CAD 底图", false, RecaptureCurrentPage);

            var miniRow = new FlowLayoutPanel { Width = Ui.S(208), Height = Ui.S(32), WrapContents = false, Margin = new Padding(0, 2, 0, 0), BackColor = Ui.Rail };
            Action<string, int, Action> addMini = (txt, w, act) => {
                var b = Ui.Secondary(txt, w, 26); b.Margin = Ui.SPad(0, 0, 4, 0); b.Click += (s, e) => Try(act); miniRow.Controls.Add(b);
            };
            addMini("改名", 48, RenameCurrentPage);
            addMini("上移", 46, () => MoveCurrentPage(-1));
            addMini("下移", 46, () => MoveCurrentPage(1));
            addMini("删除", 48, DeleteCurrentPage);
            leftBtns.Controls.Add(miniRow);

            leftPane.Controls.Add(pageList);
            leftPane.Controls.Add(leftTitle);
            leftPane.Controls.Add(leftBtns);
            leftPane.Paint += (s, e) => { using (var pen = new Pen(Ui.Line)) e.Graphics.DrawLine(pen, leftPane.Width - 1, 0, leftPane.Width - 1, leftPane.Height); };

            // 右侧：本页标注项管理
            var rightPane = new Panel { Dock = DockStyle.Right, Width = Ui.S(290), BackColor = Ui.Rail, Padding = Ui.SPad(10, 10, 10, 10) };
            var rightTitle = new Label { Text = "本页标注项（点选后在左图点击落点）", Dock = DockStyle.Top, Height = Ui.S(24), Font = Ui.F9B, ForeColor = Ui.Text };
            pointList.Dock = DockStyle.Fill;
            pointList.Font = Ui.F9;
            pointList.BorderStyle = BorderStyle.FixedSingle;
            pointList.SelectedIndexChanged += (s, e) => { UpdateCanvasHint(); canvas.Invalidate(); };

            var rightBtns = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = Ui.S(200), FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = Ui.SPad(0, 6, 0, 0), BackColor = Ui.Rail };
            Action<string, bool, Action> addRBtn = (txt, primary, act) => {
                var b = primary ? Ui.Primary(txt, 266, 29) : Ui.Secondary(txt, 266, 28);
                b.Margin = Ui.SPad(0, 2, 0, 2);
                b.Click += (s, e) => Try(act);
                rightBtns.Controls.Add(b);
            };
            addRBtn("勾选增删本页 BOM 项…", true, EditPagePointsDialog);
            addRBtn("为本页气缸补带 H/W 传感器", false, AppendCylinderSensorsToPage);
            addRBtn("在 CAD 中定位当前件", false, LocateSelectedPointInCad);

            var sideRow = new FlowLayoutPanel { Width = Ui.S(268), Height = Ui.S(32), WrapContents = false, Margin = new Padding(0, 2, 0, 0), BackColor = Ui.Rail };
            Action<string, int, Action> addSide = (txt, w, act) => {
                var b = Ui.Secondary(txt, w, 26); b.Margin = Ui.SPad(0, 0, 4, 0); b.Click += (s, e) => Try(act); sideRow.Controls.Add(b);
            };
            addSide("放左栏", 58, () => SetSelectedSide("left"));
            addSide("自动侧", 58, () => SetSelectedSide(""));
            addSide("放右栏", 58, () => SetSelectedSide("right"));
            addSide("填备忘", 68, EditSelectedTag);
            rightBtns.Controls.Add(sideRow);

            var opRow = new FlowLayoutPanel { Width = Ui.S(268), Height = Ui.S(32), WrapContents = false, Margin = new Padding(0, 2, 0, 0), BackColor = Ui.Rail };
            var btnClearPt = Ui.Secondary("清除箭头坐标", 128, 26); btnClearPt.Margin = Ui.SPad(0, 0, 6, 0); btnClearPt.Click += (s, e) => Try(ClearSelectedCoord);
            var btnRemovePt = Ui.Secondary("从本页移除", 128, 26); btnRemovePt.Click += (s, e) => Try(RemoveSelectedPoint);
            opRow.Controls.Add(btnClearPt); opRow.Controls.Add(btnRemovePt);
            rightBtns.Controls.Add(opRow);

            rightPane.Controls.Add(pointList);
            rightPane.Controls.Add(rightTitle);
            rightPane.Controls.Add(rightBtns);
            rightPane.Paint += (s, e) => { using (var pen = new Pen(Ui.Line)) e.Graphics.DrawLine(pen, 0, 0, 0, rightPane.Height); };

            // 中央：WYSIWYG 画布
            var centerPane = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(0xF3, 0xF4, 0xF6) };
            canvasHint.Dock = DockStyle.Top;
            canvasHint.Height = Ui.S(28);
            canvasHint.BackColor = Color.FromArgb(0xEE, 0xF2, 0xFF);
            canvasHint.ForeColor = Ui.Accent;
            canvasHint.Font = Ui.F8;
            canvasHint.Padding = Ui.SPad(10, 0, 10, 0);
            canvasHint.TextAlign = ContentAlignment.MiddleLeft;
            canvasHint.Text = "提示：先在右侧选中一项，再左键点击中央三维图即可设置或微调其红色箭头落点；点击左右列表文字可直接切换选中项。";

            canvas.Dock = DockStyle.Fill;
            canvas.Cursor = Cursors.Cross;
            canvas.Paint += PaintCanvas;
            canvas.MouseClick += CanvasClick;

            centerPane.Controls.Add(canvas);
            centerPane.Controls.Add(canvasHint);

            Controls.Add(centerPane);
            Controls.Add(leftPane);
            Controls.Add(rightPane);
            Controls.Add(top);
            Controls.Add(statusLabel);
        }

        void LoadMetadataBoxes() {
            txtProject.Text = project.ProjectCode ?? "";
            txtStation.Text = !string.IsNullOrWhiteSpace(project.StationCode) ? project.StationCode : (project.NumberPrefix ?? "");
            txtLine.Text = string.IsNullOrWhiteSpace(project.LineCode) ? "L1" : project.LineCode;
        }

        void SaveMeta() {
            bool changed = (project.ProjectCode ?? "") != txtProject.Text.Trim()
                || (project.StationCode ?? "") != txtStation.Text.Trim()
                || (project.LineCode ?? "") != txtLine.Text.Trim();
            project.ProjectCode = txtProject.Text.Trim();
            project.StationCode = txtStation.Text.Trim();
            project.LineCode = string.IsNullOrWhiteSpace(txtLine.Text) ? "L1" : txtLine.Text.Trim();
            if (changed) Persist("已更新 CAD 页抬头");
        }

        void Persist(string msg) {
            commitProject(LineupEntry.Clone(project), msg);
            UpdateStatus(msg);
        }

        void UpdateStatus(string msg) {
            var pages = project.CadPages ?? new List<LineupCadPage>();
            var covered = new HashSet<string>(pages.SelectMany(pg => pg.Points ?? new List<LineupLocationPoint>()).Select(pt => (pt.Number ?? "").Trim()).Where(s => s.Length > 0), StringComparer.OrdinalIgnoreCase);
            int totalBom = project.Records.Count(r => LineupTableData.Categories.Contains(LineupTableData.Category(r.Category)) && !string.IsNullOrWhiteSpace(r.Number));
            statusLabel.Text = msg + " · 共 " + pages.Count + " 页位置图 · 已覆盖 BOM 编号 " + covered.Count + " / " + totalBom;
        }

        LineupCadPage CurrentPage() {
            var item = pageList.SelectedItem as PageListEntry;
            return item == null ? null : item.Page;
        }

        LineupLocationPoint CurrentPoint() {
            var item = pointList.SelectedItem as PointListEntry;
            return item == null ? null : item.Point;
        }

        void RefreshPages(int selectIndex) {
            syncing = true;
            pageList.Items.Clear();
            foreach (var pg in project.CadPages ?? new List<LineupCadPage>()) pageList.Items.Add(new PageListEntry { Page = pg });
            if (pageList.Items.Count > 0) {
                pageList.SelectedIndex = Math.Max(0, Math.Min(pageList.Items.Count - 1, selectIndex));
            }
            syncing = false;
            OnPageSelected();
        }

        void OnPageSelected() {
            var pg = CurrentPage();
            DisposeImage();
            if (pg != null) {
                string path = LineupProject.ResolveCadImagePath(project.AssemblyFile, pg.ImageFile);
                if (!string.IsNullOrEmpty(path) && File.Exists(path)) {
                    try {
                        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        using (var tmp = Image.FromStream(fs)) {
                            currentImage = new Bitmap(tmp);
                        }
                        currentImagePath = path;
                        if (pg.Width <= 0) pg.Width = currentImage.Width;
                        if (pg.Height <= 0) pg.Height = currentImage.Height;
                    } catch { }
                }
            }
            RefreshPoints(null);
        }

        void RefreshPoints(string selectId) {
            var pg = CurrentPage();
            var byId = project.Records.ToDictionary(r => r.Id);
            pointList.Items.Clear();
            if (pg != null && pg.Points != null) {
                foreach (var pt in pg.Points) {
                    LineupRecord rec;
                    string desc = byId.TryGetValue(pt.Id ?? "", out rec) ? rec.Description : (pt.Description ?? "");
                    pointList.Items.Add(new PointListEntry { Point = pt, Description = desc });
                }
            }
            if (selectId != null) {
                for (int i = 0; i < pointList.Items.Count; i++) {
                    if (((PointListEntry)pointList.Items[i]).Point.Id == selectId) { pointList.SelectedIndex = i; break; }
                }
            } else if (pointList.Items.Count > 0) {
                // 默认选中第一个待定位项
                int firstUnresolved = -1;
                for (int i = 0; i < pointList.Items.Count; i++) {
                    if (((PointListEntry)pointList.Items[i]).Point.State != "ok") { firstUnresolved = i; break; }
                }
                pointList.SelectedIndex = firstUnresolved >= 0 ? firstUnresolved : 0;
            }
            RebuildPlan();
            UpdateCanvasHint();
            canvas.Invalidate();
            UpdateStatus("就绪");
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
            var pt = CurrentPoint();
            if (CurrentPage() == null) {
                canvasHint.Text = "当前还没有 CAD 位置图页。请点击左侧「+ 当前 CAD 视角新建页」或「+ 正交等轴测新建页」开始制作。";
            } else if (pt == null) {
                canvasHint.Text = "本页尚无标注项，请点击右侧「勾选增删本页 BOM 项…」添加需要标注的器件。";
            } else {
                canvasHint.Text = "当前选中：「" + pt.Number + "」(" + (pt.State == "ok" ? "已定位，可点击图上任意处微调" : "待定位 — 请在中央图片上左键点击该器件位置") + ")";
            }
        }

        // ==================== WYSIWYG 预览绘制（与导出 Excel 完全同源） ====================
        void PaintCanvas(object sender, PaintEventArgs e) {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.Clear(Color.FromArgb(0xF3, 0xF4, 0xF6));

            if (currentPlan == null) {
                using (var br = new SolidBrush(Ui.Muted)) {
                    var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    g.DrawString("暂无位置图页\n点击左侧「+ 当前 CAD 视角新建页」或「+ 正交等轴测新建页」创建", Ui.F9B, br, canvas.ClientRectangle, sf);
                }
                return;
            }

            int pad = Ui.S(16);
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

            // 白纸底板与阴影
            g.FillRectangle(Brushes.White, canvasPageRect);
            using (var pen = new Pen(Color.FromArgb(0xCB, 0xD5, 0xE1), 1f)) g.DrawRectangle(pen, ox, oy, drawW, drawH);

            // 头部 3 行标题栏
            float hdrH = LineupCadLayout.RowTopEmu(3) * canvasScale;
            float imgColL = ox + LineupCadLayout.ColLeftEmu(LineupCadLayout.ImageFirstCol) * canvasScale;
            float imgColR = ox + LineupCadLayout.ColLeftEmu(LineupCadLayout.ImageLastColExcl) * canvasScale;
            using (var hdrFill = new SolidBrush(Color.FromArgb(0xD9, 0xE1, 0xF2))) {
                g.FillRectangle(hdrFill, ox, oy, drawW, hdrH);
            }
            string titleInfo = "插图区域  |  项目:" + txtProject.Text + "  工位:" + txtStation.Text + "  线体:" + txtLine.Text + "  机构:" + (currentPlan.Page.Name ?? "总图");
            using (var fnt = new Font("Microsoft YaHei UI", Math.Max(7f, 9.5f * canvasScale * 12000f), FontStyle.Bold))
            using (var br = new SolidBrush(Color.FromArgb(0x00, 0x20, 0x60))) {
                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
                g.DrawString(titleInfo, fnt, br, new RectangleF(imgColL, oy, imgColR - imgColL, hdrH), sf);
                g.DrawString("IO / 描述 / SK编号", fnt, br, new RectangleF(ox, oy, imgColL - ox, hdrH), sf);
                g.DrawString("SK编号 / 描述 / IO", fnt, br, new RectangleF(imgColR, oy, ox + drawW - imgColR, hdrH), sf);
            }

            // 中央三维底图
            float ix = ox + currentPlan.ImageLeftEmu * canvasScale;
            float iy = oy + currentPlan.ImageTopEmu * canvasScale;
            float iw = currentPlan.ImageWidthEmu * canvasScale;
            float ih = currentPlan.ImageHeightEmu * canvasScale;
            canvasImageRect = new RectangleF(ix, iy, iw, ih);
            if (currentImage != null) {
                g.DrawImage(currentImage, canvasImageRect);
            } else {
                using (var br = new SolidBrush(Color.FromArgb(0xF8, 0xFA, 0xFC))) g.FillRectangle(br, canvasImageRect);
                using (var br = new SolidBrush(Ui.Muted)) {
                    var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    g.DrawString("（底图缺失或未截图）", Ui.F9, br, canvasImageRect, sf);
                }
            }
            using (var pen = new Pen(Color.FromArgb(0x94, 0xA3, 0xB8), 1f)) g.DrawRectangle(pen, ix, iy, iw, ih);

            // 左右数据行与红色箭头
            var selectedPt = CurrentPoint();
            float fontSize = Math.Max(6.5f, Math.Min(9.5f, LineupCadLayout.RowHeightEmu(3) * canvasScale * 0.56f));
            using (var rowFont = new Font("Microsoft YaHei UI", fontSize, FontStyle.Regular))
            using (var numFont = new Font("Microsoft YaHei UI", fontSize, FontStyle.Bold))
            using (var gridPen = new Pen(Color.FromArgb(0xE2, 0xE8, 0xF0), 1f))
            using (var arrowPen = new Pen(Color.Red, 1.5f))
            using (var selArrowPen = new Pen(Color.FromArgb(0x25, 0x63, 0xEB), 2.4f)) {
                var cap = new AdjustableArrowCap(4.5f, 5.5f, true);
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

                    Color bg = isSel ? Color.FromArgb(0xDB, 0xEA, 0xFE) : (co.HasArrow ? Color.White : Color.FromArgb(0xFF, 0xF2, 0xCC));
                    using (var fill = new SolidBrush(bg)) g.FillRectangle(fill, box);
                    g.DrawRectangle(gridPen, box.X, box.Y, box.Width, box.Height);

                    string tagPart = string.IsNullOrWhiteSpace(co.Tag) ? "" : " [" + co.Tag.Trim() + "]";
                    string text = co.Side == "left"
                        ? (string.IsNullOrEmpty(co.Description) ? co.Number + tagPart : co.Description + tagPart + "  |  " + co.Number)
                        : (string.IsNullOrEmpty(co.Description) ? co.Number + tagPart : co.Number + tagPart + "  |  " + co.Description);
                    var sf = new StringFormat {
                        Alignment = co.Side == "left" ? StringAlignment.Far : StringAlignment.Near,
                        LineAlignment = StringAlignment.Center,
                        Trimming = StringTrimming.EllipsisCharacter,
                        FormatFlags = StringFormatFlags.NoWrap
                    };
                    using (var tb = new SolidBrush(co.HasArrow ? Color.FromArgb(0x00, 0x20, 0x60) : Color.FromArgb(0x9C, 0x00, 0x06))) {
                        g.DrawString(text, isSel ? numFont : rowFont, tb, RectangleF.Inflate(box, -4, 0), sf);
                    }

                    if (co.HasArrow) {
                        float sx = ox + co.StartX * canvasScale;
                        float sy = oy + co.StartY * canvasScale;
                        float ex = ox + co.EndX * canvasScale;
                        float ey = oy + co.EndY * canvasScale;
                        g.DrawLine(isSel ? selArrowPen : arrowPen, sx, sy, ex, ey);
                        if (isSel) {
                            g.DrawEllipse(selArrowPen, ex - 6, ey - 6, 12, 12);
                        }
                    }
                }
            }
        }

        void CanvasClick(object sender, MouseEventArgs e) {
            if (currentPlan == null || e.Button != MouseButtons.Left) return;

            // 1. 若点在左右标注行上，直接选中该标注项
            float ox = canvasPageRect.Left, oy = canvasPageRect.Top;
            float imgColL = ox + LineupCadLayout.ColLeftEmu(LineupCadLayout.ImageFirstCol) * canvasScale;
            float imgColR = ox + LineupCadLayout.ColLeftEmu(LineupCadLayout.ImageLastColExcl) * canvasScale;
            foreach (var co in currentPlan.Callouts) {
                int rowOff = co.ExcelRow - currentPlan.StartRow;
                float ry = oy + LineupCadLayout.RowTopEmu(rowOff) * canvasScale;
                float rh = LineupCadLayout.RowHeightEmu(rowOff) * canvasScale;
                RectangleF box = co.Side == "left"
                    ? new RectangleF(ox, ry, imgColL - ox, rh)
                    : new RectangleF(imgColR, ry, canvasPageRect.Right - imgColR, rh);
                if (box.Contains(e.Location)) {
                    for (int i = 0; i < pointList.Items.Count; i++) {
                        if (((PointListEntry)pointList.Items[i]).Point == co.Point) { pointList.SelectedIndex = i; return; }
                    }
                }
            }

            // 2. 若点在中央底图上，设置当前选中项的箭头落点！
            if (canvasImageRect.Width > 10 && canvasImageRect.Height > 10 && canvasImageRect.Contains(e.Location)) {
                var pt = CurrentPoint();
                if (pt == null) return;
                bool wasUnresolved = pt.State != "ok";
                pt.X = Math.Max(0.005, Math.Min(0.995, (e.X - canvasImageRect.Left) / (double)canvasImageRect.Width));
                pt.Y = Math.Max(0.005, Math.Min(0.995, (e.Y - canvasImageRect.Top) / (double)canvasImageRect.Height));
                pt.State = "ok";
                pt.Method = "manual";
                pt.Reason = "";
                Persist("已设置 " + pt.Number + " 箭头落点");

                // 刷新列表并自动跳到下一条待定位项（方便连续点选）
                string nextId = pt.Id;
                if (wasUnresolved) {
                    var pg = CurrentPage();
                    var nextUn = pg.Points.FirstOrDefault(x => x.State != "ok");
                    if (nextUn != null) nextId = nextUn.Id;
                }
                int pgIdx = pageList.SelectedIndex;
                RefreshPageListLabels(pgIdx);
                RefreshPoints(nextId);
            }
        }

        void RefreshPageListLabels(int keepIndex) {
            syncing = true;
            pageList.Items.Clear();
            foreach (var pg in project.CadPages) pageList.Items.Add(new PageListEntry { Page = pg });
            if (keepIndex >= 0 && keepIndex < pageList.Items.Count) pageList.SelectedIndex = keepIndex;
            syncing = false;
        }

        // ==================== 页面操作 ====================
        void CreateNewPage(bool useIsometric) {
            if (app == null || assembly == null) throw new InvalidOperationException("当前未连接天工 CAD 装配。");
            var picked = PromptNewPageOptions(useIsometric);
            if (picked == null) return;

            Hide();
            if (suspendOwner != null) suspendOwner(true);
            try {
                var page = LineupLocation.CapturePage(
                    app, assembly, project, picked.RecordIds, picked.PageName,
                    useIsometric, picked.ZoomToTargets, picked.AutoDetect,
                    msg => { statusLabel.Text = msg; Application.DoEvents(); });
                project.CadPages.Add(page);
                Persist("已新增位置图页「" + page.Name + "」（识别 " + page.Points.Count(p => p.State == "ok") + "/" + page.Points.Count + "）");
                RefreshPages(project.CadPages.Count - 1);
            } finally {
                if (suspendOwner != null) suspendOwner(false);
                Show();
                Activate();
            }
        }

        void ImportImagePage() {
            using (var ofd = new OpenFileDialog { Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp", Title = "选择三维截图文件" }) {
                if (ofd.ShowDialog(this) != DialogResult.OK) return;
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
                Persist("已从本地图片新建位置图页「" + page.Name + "」");
                RefreshPages(project.CadPages.Count - 1);
            }
        }

        void RecaptureCurrentPage() {
            var pg = CurrentPage();
            if (pg == null) throw new InvalidOperationException("请先选择一页位置图。");
            if (app == null || assembly == null) throw new InvalidOperationException("当前未连接天工 CAD 装配。");
            var ask = MessageBox.Show(this,
                "是否用当前 CAD 画面重新拍摄「" + pg.Name + "」并重新自动识别各件坐标？\n（选“是”= 重拍并重新识别；选“否”= 仅换底图，保留已有手动箭头）",
                "重拍底图", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (ask == DialogResult.Cancel) return;
            bool redetect = (ask == DialogResult.Yes);
            Hide();
            if (suspendOwner != null) suspendOwner(true);
            try {
                var ids = pg.Points.Select(p => p.Id).ToList();
                var captured = LineupLocation.CapturePage(app, assembly, project, ids, pg.Name, false, false, redetect, null);
                pg.ImageFile = captured.ImageFile;
                pg.Width = captured.Width;
                pg.Height = captured.Height;
                if (redetect) pg.Points = captured.Points;
                Persist("已重拍「" + pg.Name + "」底图");
                RefreshPages(pageList.SelectedIndex);
            } finally {
                if (suspendOwner != null) suspendOwner(false);
                Show();
                Activate();
            }
        }

        void RenameCurrentPage() {
            var pg = CurrentPage(); if (pg == null) return;
            string name = PromptText("重命名位置图页", "机构名称 / 页标题：", pg.Name);
            if (string.IsNullOrWhiteSpace(name)) return;
            pg.Name = name.Trim();
            Persist("已重命名为「" + pg.Name + "」");
            RefreshPageListLabels(pageList.SelectedIndex);
            RebuildPlan();
            canvas.Invalidate();
        }

        void MoveCurrentPage(int delta) {
            int i = pageList.SelectedIndex;
            int j = i + delta;
            if (i < 0 || j < 0 || j >= project.CadPages.Count) return;
            var tmp = project.CadPages[i]; project.CadPages[i] = project.CadPages[j]; project.CadPages[j] = tmp;
            Persist("已调整页面顺序");
            RefreshPages(j);
        }

        void DeleteCurrentPage() {
            int i = pageList.SelectedIndex;
            if (i < 0) return;
            string name = project.CadPages[i].Name;
            if (MessageBox.Show(this, "确定删除位置图页「" + name + "」？（不会影响 BOM 清单数据）", "删除位置图页", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            project.CadPages.RemoveAt(i);
            Persist("已删除位置图页「" + name + "」");
            RefreshPages(Math.Max(0, i - 1));
        }

        // ==================== 标注点操作 ====================
        void EditPagePointsDialog() {
            var pg = CurrentPage(); if (pg == null) throw new InvalidOperationException("请先选择或新建一页位置图。");
            var currentIds = new HashSet<string>(pg.Points.Select(p => p.Id));
            var selected = PickBomRecordsDialog("勾选本页「" + pg.Name + "」需要标注的 BOM 项", currentIds);
            if (selected == null) return;
            var existingById = pg.Points.ToDictionary(p => p.Id);
            var nextPts = new List<LineupLocationPoint>();
            foreach (var r in project.Records.Where(x => selected.Contains(x.Id))) {
                LineupLocationPoint old;
                if (existingById.TryGetValue(r.Id, out old)) {
                    old.Number = r.Number; old.Model = r.Model; old.Category = r.Category; old.Description = r.Description;
                    nextPts.Add(old);
                } else {
                    nextPts.Add(new LineupLocationPoint { Id = r.Id, Number = r.Number, Model = r.Model, Category = r.Category, Description = r.Description, State = "unresolved", Reason = "新增项，请在图上点选箭头落点" });
                }
            }
            pg.Points = nextPts;
            Persist("已更新「" + pg.Name + "」标注项（共 " + nextPts.Count + " 项）");
            RefreshPageListLabels(pageList.SelectedIndex);
            RefreshPoints(null);
        }

        void SetSelectedSide(string side) {
            var pt = CurrentPoint(); if (pt == null) return;
            pt.Side = side;
            Persist("已调整 " + pt.Number + " 标注栏位");
            RefreshPoints(pt.Id);
        }

        void EditSelectedTag() {
            var pt = CurrentPoint(); if (pt == null) return;
            string tag = PromptText("备忘 / 接线箱号", "填入该行 C/L 列备忘（如 1JB，留空清除）：", pt.Tag ?? "");
            if (tag == null) return;
            pt.Tag = tag.Trim();
            Persist("已更新 " + pt.Number + " 备忘标签");
            RefreshPoints(pt.Id);
        }

        // 一键为本页已选气缸补带配套 H/W 传感器（贴近真实 OK270/OK170 做法：沿用气缸侧别与微偏移坐标，可再单独微调）
        void AppendCylinderSensorsToPage() {
            var pg = CurrentPage();
            if (pg == null) throw new InvalidOperationException("请先选择或新建一页位置图。");
            var existingIds = new HashSet<string>(pg.Points.Select(p => p.Id));
            var existingNums = new HashSet<string>(pg.Points.Select(p => (p.Number ?? "").Trim()).Where(s => s.Length > 0), StringComparer.OrdinalIgnoreCase);
            var sensors = project.Records.Where(r => LineupTableData.Category(r.Category) == "气缸传感器").ToList();
            var byId = project.Records.ToDictionary(r => r.Id);

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
                    .OrderBy(s => s.Number, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                int k = 0;
                foreach (var s in matched) {
                    if (existingIds.Contains(s.Id) || (!string.IsNullOrWhiteSpace(s.Number) && existingNums.Contains(s.Number.Trim()))) continue;
                    k++;
                    var sp = new LineupLocationPoint {
                        Id = s.Id, Number = s.Number, Model = s.Model, Category = s.Category, Description = s.Description,
                        Side = pt.Side,
                        State = pt.State == "ok" ? "ok" : "unresolved",
                        Method = pt.State == "ok" ? "manual" : "",
                        Reason = pt.State == "ok" ? "" : "继承自未定位气缸，请在图上点选位置",
                        X = pt.State == "ok" ? pt.X : 0,
                        Y = pt.State == "ok" ? Math.Min(0.995, pt.Y + k * 0.014) : 0
                    };
                    nextPts.Add(sp);
                    existingIds.Add(s.Id);
                    if (!string.IsNullOrWhiteSpace(s.Number)) existingNums.Add(s.Number.Trim());
                    added++;
                }
            }
            if (added == 0) {
                UpdateStatus("本页气缸没有需要补充的 H/W 传感器（均已存在或 BOM 中尚未生成 H/W）");
                return;
            }
            pg.Points = nextPts;
            Persist("已为本页气缸补带 " + added + " 条 H/W 传感器");
            RefreshPageListLabels(pageList.SelectedIndex);
            RefreshPoints(null);
        }

        void ClearSelectedCoord() {
            var pt = CurrentPoint(); if (pt == null) return;
            pt.State = "unresolved"; pt.X = 0; pt.Y = 0; pt.Method = ""; pt.Reason = "已清除坐标，请重新点选";
            Persist("已清除 " + pt.Number + " 坐标");
            RefreshPageListLabels(pageList.SelectedIndex);
            RefreshPoints(pt.Id);
        }

        void RemoveSelectedPoint() {
            var pg = CurrentPage(); var pt = CurrentPoint();
            if (pg == null || pt == null) return;
            pg.Points.Remove(pt);
            Persist("已从本页移除 " + pt.Number);
            RefreshPageListLabels(pageList.SelectedIndex);
            RefreshPoints(null);
        }

        void LocateSelectedPointInCad() {
            var pt = CurrentPoint(); if (pt == null || locateRecord == null) return;
            var rec = project.Records.FirstOrDefault(r => r.Id == pt.Id);
            if (rec == null) throw new InvalidOperationException("找不到对应的 BOM 记录。");
            locateRecord(rec);
        }

        // ==================== 弹窗辅助 ====================
        sealed class NewPageRequest {
            public string PageName = "总图";
            public List<string> RecordIds = new List<string>();
            public bool ZoomToTargets;
            public bool AutoDetect = true;
        }

        NewPageRequest PromptNewPageOptions(bool isIso, bool isImageImport = false, string defaultName = null) {
            string defName = !string.IsNullOrWhiteSpace(defaultName) ? defaultName : (project.CadPages.Count == 0 ? "总图" : "机构" + project.CadPages.Count);
            using (var d = new Form { Text = isImageImport ? "从图片新建位置图页" : (isIso ? "正交等轴测新建位置图页" : "当前 CAD 视角新建位置图页"), Font = Ui.F9, Width = Ui.S(700), Height = Ui.S(550), StartPosition = FormStartPosition.CenterParent, BackColor = Ui.Bg }) {
                var top = new Panel { Dock = DockStyle.Top, Height = Ui.S(76), Padding = Ui.SPad(12, 10, 12, 6), BackColor = Ui.Rail };
                var lbl = new Label { Text = "机构名称 / 页标题：", Left = Ui.S(12), Top = Ui.S(14), AutoSize = true, Font = Ui.F9B };
                var txtName = new TextBox { Left = Ui.S(140), Top = Ui.S(10), Width = Ui.S(260), Text = defName };
                var chkDetect = new CheckBox { Text = "自动双色高亮识别三维坐标（较慢，关闭则秒截底图手工点选）", Left = Ui.S(12), Top = Ui.S(42), AutoSize = true, Checked = !isImageImport, Visible = !isImageImport };
                var chkZoom = new CheckBox { Text = "自动缩放到所选件", Left = Ui.S(420), Top = Ui.S(12), AutoSize = true, Checked = isIso, Visible = !isImageImport };
                top.Controls.AddRange(new Control[] { lbl, txtName, chkDetect, chkZoom });

                var clb = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, Font = Ui.F9, BorderStyle = BorderStyle.FixedSingle };
                var covered = new HashSet<string>(project.CadPages.SelectMany(pg => pg.Points ?? new List<LineupLocationPoint>()).Select(p => p.Id));
                var items = project.Records.Where(r => LineupTableData.Categories.Contains(LineupTableData.Category(r.Category))).ToList();
                var checkedIds = new HashSet<string>();
                foreach (var r in items) {
                    string cat = LineupTableData.Category(r.Category);
                    bool primaryCat = cat == "气缸" || cat == "普通传感器" || cat == "其他电气件";
                    if (project.CadPages.Count == 0 && !string.IsNullOrEmpty(r.ReferenceKey)) checkedIds.Add(r.Id);
                    else if (covered.Count > 0 && !covered.Contains(r.Id) && primaryCat) checkedIds.Add(r.Id);
                }

                var rows = new List<LineupRecord>();
                string curMode = covered.Count > 0 ? "uncovered" : "primary";
                var searchBox = new TextBox { Width = Ui.S(150), Margin = Ui.SPad(0, 2, 8, 0) };
                bool updatingClb = false;

                Action refreshList = () => {
                    updatingClb = true;
                    clb.Items.Clear(); rows.Clear();
                    string q = (searchBox.Text ?? "").Trim();
                    foreach (var r in items) {
                        string cat = LineupTableData.Category(r.Category);
                        if (curMode == "primary" && cat != "气缸" && cat != "普通传感器" && cat != "其他电气件") continue;
                        if (curMode == "uncovered" && covered.Contains(r.Id)) continue;
                        if (curMode == "bound" && string.IsNullOrEmpty(r.ReferenceKey)) continue;
                        if (q.Length > 0) {
                            string hay = (r.Number ?? "") + " " + (r.Description ?? "") + " " + (r.Notes ?? "") + " " + (r.Mechanism ?? "") + " " + (r.Model ?? "");
                            if (hay.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        }
                        rows.Add(r);
                        string tag = (string.IsNullOrEmpty(r.ReferenceKey) ? "[未绑3D] " : "[已绑] ") + (covered.Contains(r.Id) ? "[已上图] " : "");
                        clb.Items.Add(tag + r.Category + " | " + r.Number + " · " + r.Description + (string.IsNullOrWhiteSpace(r.Notes) ? "" : " (" + r.Notes + ")"), checkedIds.Contains(r.Id));
                    }
                    updatingClb = false;
                };
                clb.ItemCheck += (s, e) => {
                    if (updatingClb || e.Index < 0 || e.Index >= rows.Count) return;
                    if (e.NewValue == CheckState.Checked) checkedIds.Add(rows[e.Index].Id);
                    else checkedIds.Remove(rows[e.Index].Id);
                };
                searchBox.TextChanged += (s, e) => refreshList();
                refreshList();

                var filterBar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = Ui.S(38), Padding = Ui.SPad(10, 5, 10, 4), BackColor = Ui.Bg, WrapContents = false };
                filterBar.Controls.Add(new Label { Text = "筛选：", AutoSize = true, Margin = Ui.SPad(0, 6, 2, 0), ForeColor = Ui.Text2 });
                filterBar.Controls.Add(searchBox);
                Action<string, int, Action> addF = (t, w, a) => { var b = Ui.Secondary(t, w, 26); b.Margin = Ui.SPad(0, 0, 5, 0); b.Click += (s, e) => a(); filterBar.Controls.Add(b); };
                addF("主器件", 64, () => { curMode = "primary"; refreshList(); });
                addF("未上图", 64, () => { curMode = "uncovered"; refreshList(); });
                addF("已绑3D", 64, () => { curMode = "bound"; refreshList(); });
                addF("全部", 52, () => { curMode = "all"; refreshList(); });
                addF("全选可见", 74, () => { updatingClb = true; for (int i = 0; i < rows.Count; i++) { checkedIds.Add(rows[i].Id); clb.SetItemChecked(i, true); } updatingClb = false; });
                addF("清空", 52, () => { updatingClb = true; for (int i = 0; i < rows.Count; i++) { checkedIds.Remove(rows[i].Id); clb.SetItemChecked(i, false); } updatingClb = false; });

                var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = Ui.S(46), FlowDirection = FlowDirection.RightToLeft, Padding = Ui.SPad(12, 8, 12, 8), BackColor = Ui.Rail };
                var ok = Ui.Primary(isImageImport ? "创建位置图页" : "开始抓图并建页", 140, 30); ok.DialogResult = DialogResult.OK;
                var cancel = Ui.Secondary("取消", 80, 30); cancel.DialogResult = DialogResult.Cancel;
                bottom.Controls.Add(ok); bottom.Controls.Add(cancel);
                d.AcceptButton = ok; d.CancelButton = cancel;
                d.Controls.Add(clb); d.Controls.Add(filterBar); d.Controls.Add(top); d.Controls.Add(bottom);

                if (d.ShowDialog(this) != DialogResult.OK) return null;
                var ids = items.Where(r => checkedIds.Contains(r.Id)).Select(r => r.Id).ToList();
                return new NewPageRequest {
                    PageName = string.IsNullOrWhiteSpace(txtName.Text) ? defName : txtName.Text.Trim(),
                    RecordIds = ids,
                    ZoomToTargets = chkZoom.Checked,
                    AutoDetect = chkDetect.Checked
                };
            }
        }

        HashSet<string> PickBomRecordsDialog(string title, HashSet<string> preChecked) {
            using (var d = new Form { Text = title, Font = Ui.F9, Width = Ui.S(660), Height = Ui.S(520), StartPosition = FormStartPosition.CenterParent, BackColor = Ui.Bg }) {
                var clb = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, Font = Ui.F9, BorderStyle = BorderStyle.FixedSingle };
                var coveredOther = new HashSet<string>(project.CadPages.Where(pg => pg != CurrentPage()).SelectMany(pg => pg.Points ?? new List<LineupLocationPoint>()).Select(p => p.Id));
                var items = project.Records.Where(r => LineupTableData.Categories.Contains(LineupTableData.Category(r.Category))).ToList();
                var checkedIds = new HashSet<string>(preChecked ?? Enumerable.Empty<string>());
                var rows = new List<LineupRecord>();
                string curMode = "all";
                var searchBox = new TextBox { Width = Ui.S(180), Margin = Ui.SPad(0, 2, 8, 0) };
                bool updatingClb = false;

                Action refreshList = () => {
                    updatingClb = true;
                    clb.Items.Clear(); rows.Clear();
                    string q = (searchBox.Text ?? "").Trim();
                    foreach (var r in items) {
                        string cat = LineupTableData.Category(r.Category);
                        if (curMode == "primary" && cat != "气缸" && cat != "普通传感器" && cat != "其他电气件") continue;
                        if (curMode == "checked" && !checkedIds.Contains(r.Id)) continue;
                        if (q.Length > 0) {
                            string hay = (r.Number ?? "") + " " + (r.Description ?? "") + " " + (r.Notes ?? "") + " " + (r.Mechanism ?? "") + " " + (r.Model ?? "");
                            if (hay.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        }
                        rows.Add(r);
                        string tag = coveredOther.Contains(r.Id) ? "[别页已标] " : "";
                        clb.Items.Add(tag + r.Category + " | " + r.Number + " · " + r.Description + (string.IsNullOrWhiteSpace(r.Notes) ? "" : " (" + r.Notes + ")"), checkedIds.Contains(r.Id));
                    }
                    updatingClb = false;
                };
                clb.ItemCheck += (s, e) => {
                    if (updatingClb || e.Index < 0 || e.Index >= rows.Count) return;
                    if (e.NewValue == CheckState.Checked) checkedIds.Add(rows[e.Index].Id);
                    else checkedIds.Remove(rows[e.Index].Id);
                };
                searchBox.TextChanged += (s, e) => refreshList();
                refreshList();

                var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = Ui.S(38), Padding = Ui.SPad(10, 5, 10, 4), BackColor = Ui.Rail, WrapContents = false };
                bar.Controls.Add(new Label { Text = "查找：", AutoSize = true, Margin = Ui.SPad(0, 6, 2, 0), ForeColor = Ui.Text2 });
                bar.Controls.Add(searchBox);
                Action<string, int, Action> addB = (t, w, a) => { var b = Ui.Secondary(t, w, 26); b.Margin = Ui.SPad(0, 0, 5, 0); b.Click += (s, e) => a(); bar.Controls.Add(b); };
                addB("全部", 54, () => { curMode = "all"; refreshList(); });
                addB("主器件", 66, () => { curMode = "primary"; refreshList(); });
                addB("已勾选", 66, () => { curMode = "checked"; refreshList(); });
                addB("全选可见", 76, () => { updatingClb = true; for (int i = 0; i < rows.Count; i++) { checkedIds.Add(rows[i].Id); clb.SetItemChecked(i, true); } updatingClb = false; });

                var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = Ui.S(46), FlowDirection = FlowDirection.RightToLeft, Padding = Ui.SPad(12, 8, 12, 8), BackColor = Ui.Rail };
                var ok = Ui.Primary("确定", 90, 30); ok.DialogResult = DialogResult.OK;
                var cancel = Ui.Secondary("取消", 80, 30); cancel.DialogResult = DialogResult.Cancel;
                bottom.Controls.Add(ok); bottom.Controls.Add(cancel);
                d.AcceptButton = ok; d.CancelButton = cancel;
                d.Controls.Add(clb); d.Controls.Add(bar); d.Controls.Add(bottom);
                if (d.ShowDialog(this) != DialogResult.OK) return null;
                return checkedIds;
            }
        }

        string PromptText(string title, string label, string value) {
            using (var d = new Form { Text = title, Font = Ui.F9, Width = Ui.S(420), Height = Ui.S(160), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, BackColor = Ui.Bg }) {
                var l = new Label { Text = label, Left = Ui.S(14), Top = Ui.S(16), AutoSize = true };
                var tb = new TextBox { Left = Ui.S(14), Top = Ui.S(42), Width = Ui.S(374), Text = value ?? "" };
                var ok = Ui.Primary("确定", 86, 28); ok.Left = Ui.S(212); ok.Top = Ui.S(82); ok.DialogResult = DialogResult.OK;
                var cancel = Ui.Secondary("取消", 80, 28); cancel.Left = Ui.S(306); cancel.Top = Ui.S(82); cancel.DialogResult = DialogResult.Cancel;
                d.Controls.AddRange(new Control[] { l, tb, ok, cancel });
                d.AcceptButton = ok; d.CancelButton = cancel;
                return d.ShowDialog(this) == DialogResult.OK ? tb.Text : null;
            }
        }

        void Try(Action act) {
            try { act(); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "CAD 位置图", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        void DisposeImage() {
            if (currentImage != null) { currentImage.Dispose(); currentImage = null; }
            currentImagePath = "";
        }
    }
}
