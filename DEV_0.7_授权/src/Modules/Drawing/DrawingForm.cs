using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using A=SolidEdgeAssembly;
using F=SolidEdgeFramework;

namespace TianGongCadSuite {
    // 「一键出图」窗口（0.11.0，命令 13；0.13.3 全面增强）：
    //   · 支持载入当前打开的零件 / 钣金 / 装配，也支持直接添加文件、扫描文件夹或从资源管理器拖放模型；
    //   · 工具栏提供关键词搜索（Ctrl+F）、全选 / 全不选 / 只选加工件 / 只选缺图 / 只选失败项、刷新已有图状态；
    //   · 右侧挂载 3D 零件预览（PartPreview），点一行实时看三维模型，不用靠图号盲猜；
    //   · 右键支持打开工程图、在天工打开三维模型、定位文件、仅为此项出图、批量勾选与移除；
    //   · 支持窗口复用（再点功能区不丢进度）、窗口位置/大小记忆与全局快捷键护盾。
    public sealed class DrawingForm : Form, IBusyTool {
        const string Title = "一键出图";
        readonly F.Application app;
        readonly ListView list = new ListView();
        readonly PartPreview preview = new PartPreview();
        readonly ContextMenuStrip menu = Ui.Menu();
        readonly Label head = new Label(), status = new Label();
        readonly TextBox searchBox = new TextBox { BorderStyle = BorderStyle.FixedSingle, Font = Ui.F9 };
        readonly Button searchClearBtn = Ui.Small("×");
        readonly CheckBox overwrite = new CheckBox(), openAfter = new CheckBox();
        readonly ComboBox dimStyleBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Font = Ui.F9 };
        readonly SlimProgress bar = new SlimProgress { Visible = false };
        readonly FlatButton run = (FlatButton)Ui.Primary("出图", 128, 40);
        readonly Button close = Ui.Secondary("关闭", 90, 36);
        readonly ToolTip tips = new ToolTip { AutoPopDelay = 15000, InitialDelay = 300 };
        readonly Timer searchDebounce = new Timer { Interval = 140 };
        readonly CadKeyShield keyShield;

        readonly List<DrawingJob> jobs = new List<DrawingJob>();
        string topProject = "";
        string lastFolder = "";
        string lastPreviewPath = "";
        bool rebuilding, busy, stop;

        public bool Busy { get { return busy; } }
        public string BusyWhat { get { return "出图"; } }

        public DrawingForm(F.Application application) {
            app = application;
            preview.App = application;
            Ui.Shell(this, Product.Title(Title), 1240, 680, 860, 480);
            FormBorderStyle = FormBorderStyle.Sizable;
            AllowDrop = true;
            DragEnter += OnFormDragEnter;
            DragDrop += OnFormDragDrop;
            keyShield = new CadKeyShield(this, () => busy, ResolveShortcut);
            WindowStateStore.Bind(this, "drawing");

            var body = new VerticalStack { Dock = DockStyle.Fill, Padding = Ui.SPad(18, 12, 18, 8), Gap = Ui.S(8), AutoScroll = false };

            // ---- 顶部标题与来源按钮行 ----
            var topBar = new Panel { Height = Ui.S(34), BackColor = Ui.Bg };
            head.AutoSize = false; head.Font = Ui.F10B; head.ForeColor = Ui.Text; head.BackColor = Ui.Bg;
            head.TextAlign = ContentAlignment.MiddleLeft; head.AutoEllipsis = true;
            var btnReload = Ui.Small("载入当前文档"); btnReload.Click += (s, e) => LoadActive();
            var btnAdd = Ui.Small("添加文件…"); btnAdd.Click += (s, e) => AddFiles();
            var btnScan = Ui.Small("扫描文件夹…"); btnScan.Click += (s, e) => ScanFolder();
            foreach (var b in new[] { btnReload, btnAdd, btnScan }) ((FlatButton)b).SetFore(Ui.Text2);

            // 主表卡片 + 右侧 3D 预览
            var listFrame = Ui.Card("", 0, "");
            listFrame.Padding = Ui.SPad(2, 2, 2, 2);
            InitListView();
            listFrame.Controls.Add(list);
            var btnPreviewToggle = PartPreview.Attach(listFrame, preview, "drawing.preview");

            var topBtns = new Control[] { btnReload, btnAdd, btnScan, btnPreviewToggle };
            topBar.Controls.Add(head);
            foreach (var b in topBtns) topBar.Controls.Add(b);
            topBar.Resize += (s, e) => {
                int rx = topBar.Width;
                for (int i = topBtns.Length - 1; i >= 0; i--) {
                    var b = topBtns[i];
                    rx -= b.Width;
                    b.Location = new Point(rx, (topBar.Height - b.Height) / 2);
                    rx -= Ui.S(4);
                }
                head.SetBounds(0, 0, Math.Max(40, rx - Ui.S(8)), topBar.Height);
            };
            body.Add(topBar);

            // ---- 快捷筛选与批量勾选工具栏 ----
            var tools = new FlowLayoutPanel {
                Height = Ui.S(36), MinimumSize = new Size(0, Ui.S(34)), Tag = "autoheight",
                FlowDirection = FlowDirection.LeftToRight, WrapContents = true,
                BackColor = Ui.Bg, Margin = new Padding(0), Padding = new Padding(0)
            };

            var searchWrap = new Panel { Width = Ui.S(210), Height = Ui.S(28), BackColor = Ui.Bg, Margin = Ui.SPad(0, 2, 8, 2) };
            ((FlatButton)searchClearBtn).SetFore(Ui.Muted);
            searchClearBtn.Width = Ui.S(24);
            searchClearBtn.Height = Ui.S(26);
            searchClearBtn.Visible = false;
            tips.SetToolTip(searchClearBtn, "清空筛选 (Esc)");
            searchClearBtn.Click += (s, e) => { searchBox.Text = ""; searchDebounce.Stop(); RebuildList(); try { list.Focus(); } catch { } };
            searchBox.SetBounds(0, 0, searchWrap.Width, Ui.S(26));
            searchClearBtn.SetBounds(searchWrap.Width - searchClearBtn.Width, 0, searchClearBtn.Width, Ui.S(26));
            searchDebounce.Tick += (s, e) => { searchDebounce.Stop(); if (!IsDisposed) RebuildList(); };
            searchBox.TextChanged += (s, e) => {
                bool show = searchBox.Text.Length > 0;
                if (searchClearBtn.Visible != show) {
                    searchClearBtn.Visible = show;
                    searchBox.Width = show ? searchWrap.Width - searchClearBtn.Width - Ui.S(2) : searchWrap.Width;
                }
                searchDebounce.Stop();
                searchDebounce.Start();
            };
            searchBox.KeyDown += (s, e) => {
                if (e.KeyCode == Keys.Escape) {
                    if (searchBox.Text.Length > 0) searchBox.Text = "";
                    try { list.Focus(); } catch { }
                    e.SuppressKeyPress = true;
                }
            };
            searchWrap.Controls.Add(searchBox);
            searchWrap.Controls.Add(searchClearBtn);
            tips.SetToolTip(searchBox, "按文件名 / 类型 / 结果搜索 (Ctrl+F)");

            var bAll = Ui.Small("全选"); bAll.Click += (s, e) => SetVisibleChecked(j => true);
            var bNone = Ui.Small("全不选"); bNone.Click += (s, e) => SetVisibleChecked(j => false);
            var bCustom = Ui.Small("只选加工件"); bCustom.Click += (s, e) => SetVisibleChecked(j => string.IsNullOrEmpty(j.Reason));
            var bMissing = Ui.Small("只选缺图"); bMissing.Click += (s, e) => SetVisibleChecked(j => !j.HasDrawing && string.IsNullOrEmpty(j.Reason));
            var bFailed = Ui.Small("只选失败项"); bFailed.Click += (s, e) => SetVisibleChecked(j => j.Result != null && !j.Result.Ok);
            var bRefresh = Ui.Small("刷新已有图"); bRefresh.Click += (s, e) => RefreshHasDrawing();
            tips.SetToolTip(bMissing, "勾选所有尚未生成同名 .dft 的加工件，最适合给装配补图。");
            tips.SetToolTip(bFailed, "勾选刚才出图失败的条目以便单独重试。");
            foreach (var b in new[] { bAll, bNone, bCustom, bMissing, bFailed, bRefresh }) {
                ((FlatButton)b).SetFore(Ui.Text2);
                b.Margin = Ui.SPad(0, 3, 2, 2);
            }
            tools.Controls.Add(searchWrap);
            tools.Controls.AddRange(new Control[] { bAll, bNone, bCustom, bMissing, bFailed, bRefresh });
            body.Add(tools);

            var hint = new Label {
                AutoSize = false, Height = Ui.S(22), Font = Ui.F8, ForeColor = Ui.Muted, BackColor = Ui.Bg, AutoEllipsis = true,
                Text = "按公司模板（SK先惠 A4 / A3）自动出图 · 点一行右侧预览三维 · 双击打开工程图 · 右键单件重跑 · 可直接拖入零件或文件夹"
            };
            body.Add(hint);

            body.Add(listFrame, true);

            // ---- 底部选项 ----
            var opts = new FlowLayoutPanel { Height = Ui.S(34), FlowDirection = FlowDirection.LeftToRight, BackColor = Ui.Bg, WrapContents = true, Tag = "autoheight", MinimumSize = new Size(0, Ui.S(32)) };
            var lblStyle = new Label { Text = "尺寸样式：", AutoSize = true, Font = Ui.F9, ForeColor = Ui.Text2, Margin = Ui.SPad(0, 6, 4, 0) };
            dimStyleBox.Width = Ui.S(126);
            dimStyleBox.Margin = Ui.SPad(0, 2, 18, 0);
            foreach (var st in DrawingStyleRules.DimensionStyleChoices) dimStyleBox.Items.Add(st);
            string savedStyle = DrawingStyleRules.NormalizePreferredStyle(Choices.Get("drawing.dimstyle", DrawingStyleRules.DefaultDimensionStyle));
            int styleIdx = dimStyleBox.Items.IndexOf(savedStyle);
            dimStyleBox.SelectedIndex = styleIdx >= 0 ? styleIdx : 0;
            dimStyleBox.SelectionChangeCommitted += (s, e) => Choices.Set("drawing.dimstyle", SelectedDimensionStyle());
            tips.SetToolTip(dimStyleBox, "新建工程图统一使用的尺寸样式（默认 A3-国标-水平，也可切为 A3-国标，不再沿用模板默认的 A3-孔 / A4-水平2）。");
            overwrite.Text = "已有同名工程图时覆盖（不勾：另存为「名字(自动).dft」）"; overwrite.AutoSize = true; overwrite.Font = Ui.F9; overwrite.Margin = Ui.SPad(0, 5, 18, 0);
            openAfter.Text = "出完打开工程图（批量 > 3 张时仅打开最后一张）"; openAfter.AutoSize = true; openAfter.Checked = true; openAfter.Font = Ui.F9; openAfter.Margin = Ui.SPad(0, 5, 0, 0);
            Choices.Bind(overwrite, "drawing.overwrite");
            opts.Controls.Add(lblStyle); opts.Controls.Add(dimStyleBox); opts.Controls.Add(overwrite); opts.Controls.Add(openAfter);
            body.Add(opts);

            bar.Height = Ui.S(8); body.Add(bar);

            var actions = new ActionBar { Dock = DockStyle.Bottom, Height = Ui.S(64), Padding = Ui.SPad(18, 0, 18, 0) };
            run.Click += (s, e) => { if (busy) stop = true; else RunSelected(); };
            close.Click += (s, e) => Close();
            actions.AddRight(close); actions.AddRight(run);
            actions.Status = status;
            status.ForeColor = Ui.Muted;

            Controls.Add(body); Controls.Add(actions);

            FormClosing += (s, e) => { if (busy) { stop = true; e.Cancel = true; } };
            FormClosed += (s, e) => { searchDebounce.Stop(); searchDebounce.Dispose(); keyShield.Dispose(); tips.Dispose(); menu.Dispose(); };
            UpdateSummaryAndButton();
        }

        void InitListView() {
            list.Dock = DockStyle.Fill;
            list.View = View.Details;
            list.CheckBoxes = true;
            list.FullRowSelect = true;
            list.GridLines = false;
            list.HideSelection = false;
            list.MultiSelect = true;
            list.BorderStyle = BorderStyle.None;
            list.Font = Ui.F9;
            list.Columns.Add("文件", 280);
            list.Columns.Add("类型", 56);
            list.Columns.Add("已有图纸", 76);
            list.Columns.Add("结果", 220);
            list.Columns.Add("需要人工确认", 320);
            Ui.FitListView(list);
            list.DoubleClick += (s, e) => OpenSelectedDrawing();
            list.ItemChecked += OnListItemChecked;
            list.SelectedIndexChanged += (s, e) => ShowSelectedPreview();
            list.MouseUp += OnListMouseUp;
        }

        internal bool ReuseIfOpen() {
            if (IsDisposed) return false;
            if (!busy) {
                status.Text = "窗口一直开着（如需切换为当前 CAD 文档，点右上角「载入当前文档」）。";
                status.ForeColor = Ui.Accent;
            }
            return true;
        }

        Action ResolveShortcut(Keys keyData) {
            if (keyData == (Keys.Control | Keys.F)) {
                return () => { if (!IsDisposed) { try { searchBox.Focus(); searchBox.SelectAll(); } catch { } } };
            }
            return null;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData) {
            if (!busy && keyData == (Keys.Control | Keys.F)) {
                try { searchBox.Focus(); searchBox.SelectAll(); } catch { }
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ---- 载入来源（当前文档 / 添加文件 / 扫描文件夹 / 拖放） ----

        public void LoadActive() {
            if (busy) return;
            jobs.Clear();
            topProject = "";
            lastPreviewPath = "";

            object doc = null;
            try { doc = app == null ? null : app.ActiveDocument; } catch { }
            string file = "";
            try { file = doc == null ? "" : (string)((dynamic)doc).FullName; } catch { }

            if (doc == null || file.Length == 0 || !File.Exists(file) || !IsSupportedModel(file)) {
                head.Text = "未打开适用模型 —— 可点「添加文件…」「扫描文件夹…」或直接拖入零件/装配";
                status.Text = "就绪。支持 .par / .psm / .asm 文件批量出图。";
                status.ForeColor = Ui.Muted;
                RebuildList();
                return;
            }

            lastFolder = Path.GetDirectoryName(file) ?? "";
            bool dirty = false;
            try { dirty = (bool)((dynamic)doc).Dirty; } catch { }
            var asm = doc as A.AssemblyDocument;
            topProject = DrawingBatch.Project(Path.GetFileNameWithoutExtension(file));
            jobs.Add(new DrawingJob { File = file, Kind = DrawingBatch.KindOf(file), Count = 1, Selected = true, HasDrawing = DrawingBatch.HasDrawing(file) });
            if (asm != null) {
                var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                Collect(asm, counts, 0);
                foreach (var kv in counts.OrderBy(k => Path.GetFileName(k.Key), StringComparer.OrdinalIgnoreCase)) {
                    string why;
                    bool custom = DrawingBatch.Custom(kv.Key, topProject, out why);
                    jobs.Add(new DrawingJob { File = kv.Key, Kind = DrawingBatch.KindOf(kv.Key), Count = kv.Value, Selected = custom, Reason = why, HasDrawing = DrawingBatch.HasDrawing(kv.Key) });
                }
            }
            RebuildList();
            head.Text = (asm != null ? "装配 " : "") + Path.GetFileName(file);
            if (dirty) {
                status.Text = "注意：当前文档有未保存修改 —— 工程图按磁盘版本出，如需带上修改请先在天工按 Ctrl+S 保存。";
                status.ForeColor = Ui.Warn;
            } else {
                status.Text = "已载入 " + jobs.Count + " 项（默认勾选 " + jobs.Count(j => j.Selected) + " 张）。";
                status.ForeColor = Ui.Muted;
            }
            openAfter.Checked = asm == null;
            SelectFirstForPreview();
        }

        void AddFiles() {
            if (busy) return;
            using (var dlg = new OpenFileDialog {
                Title = "添加要出图的三维模型",
                Multiselect = true,
                Filter = "天工模型 (*.par;*.psm;*.asm)|*.par;*.psm;*.asm|所有文件 (*.*)|*.*"
            }) {
                if (!string.IsNullOrEmpty(lastFolder) && Directory.Exists(lastFolder)) dlg.InitialDirectory = lastFolder;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                int added = AppendModelFiles(dlg.FileNames, true);
                if (added > 0 && dlg.FileNames.Length > 0) lastFolder = Path.GetDirectoryName(dlg.FileNames[0]) ?? lastFolder;
                status.Text = added > 0 ? ("已添加 " + added + " 个模型。") : "选中的文件已在列表中。";
                status.ForeColor = Ui.Muted;
            }
        }

        void ScanFolder() {
            if (busy) return;
            string dir = FolderPicker.Pick(this, "选择包含要出图模型的文件夹（递归扫描 .par / .psm / .asm）", lastFolder, "drawing.scan");
            if (string.IsNullOrEmpty(dir)) return;
            lastFolder = dir;
            var found = ScanModelsInFolder(dir);
            if (found.Count == 0) {
                status.Text = "该文件夹下没有找到 .par / .psm / .asm 模型文件。";
                status.ForeColor = Ui.Warn;
                return;
            }
            if (jobs.Count > 0) {
                var ask = MessageBox.Show(this,
                    "扫描到 " + found.Count + " 个模型文件：\r\n• 点「是」：替换当前列表\r\n• 点「否」：追加到当前列表",
                    Title, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (ask == DialogResult.Cancel) return;
                if (ask == DialogResult.Yes) { jobs.Clear(); topProject = ""; lastPreviewPath = ""; }
            }
            int added = AppendModelFiles(found, false);
            head.Text = "文件夹「" + Path.GetFileName(dir.TrimEnd('\\', '/')) + "」";
            status.Text = "已扫描并载入 " + added + " 个模型（默认勾选加工件）。";
            status.ForeColor = Ui.Muted;
            SelectFirstForPreview();
        }

        void OnFormDragEnter(object sender, DragEventArgs e) {
            if (!busy && e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
            else e.Effect = DragDropEffects.None;
        }

        void OnFormDragDrop(object sender, DragEventArgs e) {
            if (busy || e.Data == null || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            var dropped = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (dropped == null || dropped.Length == 0) return;

            var models = new List<string>();
            bool fromFolder = false;
            foreach (string raw in dropped) {
                if (string.IsNullOrEmpty(raw)) continue;
                if (Directory.Exists(raw)) {
                    fromFolder = true;
                    models.AddRange(ScanModelsInFolder(raw));
                } else if (File.Exists(raw) && IsSupportedModel(raw)) {
                    models.Add(raw);
                }
            }
            if (models.Count == 0) {
                status.Text = "拖入的内容里没有可出图的 .par / .psm / .asm 文件。";
                status.ForeColor = Ui.Warn;
                return;
            }
            int added = AppendModelFiles(models, !fromFolder);
            if (jobs.Count > 0 && string.IsNullOrEmpty(head.Text)) head.Text = "已添加模型";
            status.Text = added > 0 ? ("已拖入并新增 " + added + " 个模型。") : "拖入的模型已在列表中。";
            status.ForeColor = Ui.Muted;
            SelectFirstForPreview();
        }

        static bool IsSupportedModel(string path) {
            string ext = (Path.GetExtension(path) ?? "").ToLowerInvariant();
            return ext == ".par" || ext == ".psm" || ext == ".asm";
        }

        static List<string> ScanModelsInFolder(string dir) {
            var res = new List<string>();
            try {
                foreach (string f in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories)) {
                    string name = Path.GetFileName(f) ?? "";
                    if (name.StartsWith("~$", StringComparison.Ordinal)) continue;
                    if (IsSupportedModel(f)) res.Add(f);
                }
            } catch { }
            res.Sort((a, b) => WorkbenchRules.NaturalCompare(Path.GetFileName(a), Path.GetFileName(b)));
            return res;
        }

        int AppendModelFiles(IEnumerable<string> paths, bool selectAllExplicit) {
            var existing = new HashSet<string>(jobs.Select(j => j.File), StringComparer.OrdinalIgnoreCase);
            int added = 0;
            foreach (string f in paths) {
                if (string.IsNullOrEmpty(f) || !File.Exists(f) || !IsSupportedModel(f)) continue;
                if (!existing.Add(f)) continue;
                string why;
                bool custom = DrawingBatch.Custom(f, topProject, out why);
                jobs.Add(new DrawingJob {
                    File = f,
                    Kind = DrawingBatch.KindOf(f),
                    Count = 1,
                    Selected = selectAllExplicit || custom,
                    Reason = selectAllExplicit ? "" : why,
                    HasDrawing = DrawingBatch.HasDrawing(f)
                });
                added++;
            }
            if (added > 0) RebuildList();
            return added;
        }

        static void Collect(A.AssemblyDocument asm, Dictionary<string, int> counts, int depth) {
            if (depth > 8) return;
            foreach (A.Occurrence occ in asm.Occurrences) {
                string f = ""; try { f = occ.OccurrenceFileName; } catch { }
                if (f.Length == 0) continue;
                bool sub = f.EndsWith(".asm", StringComparison.OrdinalIgnoreCase);
                if (sub) {
                    try { var d = occ.OccurrenceDocument as A.AssemblyDocument; if (d != null) Collect(d, counts, depth + 1); } catch { }
                }
                int n; counts.TryGetValue(f, out n); counts[f] = n + 1;
            }
        }

        // ---- 列表渲染与筛选 ----

        void RebuildList() {
            rebuilding = true;
            list.BeginUpdate();
            try {
                string kw = (searchBox.Text ?? "").Trim();
                list.Items.Clear();
                var batch = new List<ListViewItem>(jobs.Count);
                foreach (var j in jobs) {
                    if (kw.Length > 0 && !MatchesSearch(j, kw)) continue;
                    batch.Add(MakeListItem(j));
                }
                if (batch.Count > 0) list.Items.AddRange(batch.ToArray());
            } finally {
                list.EndUpdate();
                rebuilding = false;
            }
            UpdateSummaryAndButton();
        }

        static bool MatchesSearch(DrawingJob j, string kw) {
            if ((Path.GetFileName(j.File) ?? "").IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if ((j.Kind ?? "").IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if ((j.Reason ?? "").IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if ((j.Status ?? "").IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        static ListViewItem MakeListItem(DrawingJob j) {
            string colResult = BuildResultText(j);
            string colNotes = j.Result != null ? string.Join("；", j.Result.Notes.Where(n => !n.StartsWith("视图 ")).Take(4)) : "";
            var it = new ListViewItem(new[] {
                Path.GetFileName(j.File) + (j.Count > 1 ? "  ×" + j.Count : ""),
                j.Kind,
                j.HasDrawing ? "有" : "缺",
                colResult,
                colNotes
            });
            it.Checked = j.Selected;
            it.Tag = j;
            ApplyItemColor(it, j);
            return it;
        }

        static string BuildResultText(DrawingJob j) {
            if (j.Status.Length > 0) return j.Status;
            if (j.Result != null) {
                var o = j.Result;
                return o.Ok ? (o.Sheet + " " + o.Scale + "　尺寸 " + o.Dimensions + "　完成度 " + (int)Math.Round(o.Completeness * 100) + "%") : "失败";
            }
            return j.Reason.Length > 0 ? "（" + j.Reason + "，默认不出）" : "";
        }

        static void ApplyItemColor(ListViewItem it, DrawingJob j) {
            if (j.Result != null) {
                var o = j.Result;
                it.ForeColor = !o.Ok ? Ui.Bad : (o.Completeness < 0.9 || o.Failed > 0 ? Ui.Warn : Ui.Ok);
            } else {
                it.ForeColor = j.Selected ? Ui.Text : Ui.Muted;
            }
        }

        void RefreshItemVisual(ListViewItem it) {
            var j = it.Tag as DrawingJob;
            if (j == null) return;
            it.SubItems[2].Text = j.HasDrawing ? "有" : "缺";
            it.SubItems[3].Text = BuildResultText(j);
            it.SubItems[4].Text = j.Result != null ? string.Join("；", j.Result.Notes.Where(n => !n.StartsWith("视图 ")).Take(4)) : "";
            ApplyItemColor(it, j);
        }

        void OnListItemChecked(object sender, ItemCheckedEventArgs e) {
            if (rebuilding || e.Item == null) return;
            var j = e.Item.Tag as DrawingJob;
            if (j != null) {
                j.Selected = e.Item.Checked;
                ApplyItemColor(e.Item, j);
            }
            UpdateSummaryAndButton();
        }

        void SetVisibleChecked(Func<DrawingJob, bool> pred) {
            if (busy) return;
            rebuilding = true;
            list.BeginUpdate();
            try {
                foreach (ListViewItem it in list.Items) {
                    var j = it.Tag as DrawingJob;
                    if (j == null) continue;
                    bool on = pred(j);
                    j.Selected = on;
                    if (it.Checked != on) it.Checked = on;
                    ApplyItemColor(it, j);
                }
            } finally {
                list.EndUpdate();
                rebuilding = false;
            }
            UpdateSummaryAndButton();
        }

        void RefreshHasDrawing() {
            if (busy) return;
            // 按目录一次枚举 .dft，避免公司加密盘上逐文件 File.Exists 慢 I/O
            var dirCache = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            int foundNew = 0;
            foreach (var j in jobs) {
                bool had = j.HasDrawing;
                string dir = Path.GetDirectoryName(j.File) ?? "";
                HashSet<string> dfts;
                if (!dirCache.TryGetValue(dir, out dfts)) {
                    dfts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    try {
                        if (Directory.Exists(dir))
                            foreach (string f in Directory.GetFiles(dir, "*.dft")) dfts.Add(Path.GetFileName(f));
                    } catch { }
                    dirCache[dir] = dfts;
                }
                string stem = Path.GetFileNameWithoutExtension(j.File) ?? "";
                j.HasDrawing = dfts.Contains(stem + ".dft") || dfts.Contains(stem + "(自动).dft");
                if (!had && j.HasDrawing) foundNew++;
            }
            list.BeginUpdate();
            try {
                foreach (ListViewItem it in list.Items) RefreshItemVisual(it);
            } finally {
                list.EndUpdate();
            }
            UpdateSummaryAndButton();
            status.Text = foundNew > 0 ? ("已刷新：新发现 " + foundNew + " 张工程图。") : "已刷新全部工程图状态。";
            status.ForeColor = foundNew > 0 ? Ui.Ok : Ui.Muted;
        }

        void UpdateSummaryAndButton() {
            int total = jobs.Count;
            int sel = jobs.Count(j => j.Selected);
            int missingSel = jobs.Count(j => j.Selected && !j.HasDrawing);
            if (!busy) {
                run.Text = sel > 0 ? "出图 (" + sel + ")" : "出图";
                run.Enabled = sel > 0 && app != null;
                Ui.FitButton(run);
            }
            if (total > 0 && !busy && string.IsNullOrEmpty(searchBox.Text)) {
                tips.SetToolTip(run, "勾选 " + sel + " 项（其中缺图 " + missingSel + " 项）");
            }
        }

        void SelectFirstForPreview() {
            if (list.Items.Count > 0 && list.SelectedItems.Count == 0) {
                list.Items[0].Selected = true;
            }
            ShowSelectedPreview();
        }

        void ShowSelectedPreview() {
            if (rebuilding || busy || list.SelectedItems.Count == 0) return;
            var j = list.SelectedItems[0].Tag as DrawingJob;
            if (j == null) return;
            string p = j.File ?? "";
            if (string.Equals(p, lastPreviewPath, StringComparison.OrdinalIgnoreCase)) return;
            lastPreviewPath = p;
            preview.ShowFile(p);
        }

        // ---- 右键菜单 ----

        void OnListMouseUp(object sender, MouseEventArgs e) {
            if (busy || e.Button != MouseButtons.Right) return;
            var hit = list.HitTest(e.Location);
            if (hit.Item != null && !hit.Item.Selected) {
                list.SelectedItems.Clear();
                hit.Item.Selected = true;
            }
            if (list.SelectedItems.Count == 0) return;

            var selItems = list.SelectedItems.Cast<ListViewItem>().ToList();
            var firstJob = selItems[0].Tag as DrawingJob;
            if (firstJob == null) return;
            string dftPath = ResolveDrawingPath(firstJob);

            Ui.DisposeItems(menu.Items);
            var mOpenDft = menu.Items.Add("打开工程图 (.dft)", null, (s, ev) => OpenPathInCad(dftPath));
            mOpenDft.Enabled = dftPath.Length > 0 && File.Exists(dftPath) && app != null;

            var mOpenModel = menu.Items.Add("在天工中打开三维模型", null, (s, ev) => OpenPathInCad(firstJob.File));
            mOpenModel.Enabled = File.Exists(firstJob.File) && app != null;

            var mLoc = menu.Items.Add("在资源管理器中定位文件", null, (s, ev) => {
                if (File.Exists(firstJob.File)) {
                    try { Process.Start("explorer.exe", "/select,\"" + firstJob.File + "\""); } catch { }
                }
            });
            mLoc.Enabled = File.Exists(firstJob.File);

            menu.Items.Add(new ToolStripSeparator());
            string runLabel = selItems.Count == 1 ? "仅为此项出图" : "仅为选中的 " + selItems.Count + " 项出图";
            var mRunOne = menu.Items.Add(runLabel, null, (s, ev) => RunItems(selItems));
            mRunOne.Enabled = app != null;

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("勾选选中项", null, (s, ev) => SetItemsChecked(selItems, true));
            menu.Items.Add("取消勾选选中项", null, (s, ev) => SetItemsChecked(selItems, false));
            menu.Items.Add("从列表移除选中项", null, (s, ev) => {
                var drop = new HashSet<DrawingJob>(selItems.Select(x => x.Tag as DrawingJob).Where(x => x != null));
                jobs.RemoveAll(drop.Contains);
                RebuildList();
            });

            menu.Show(Cursor.Position);
        }

        void SetItemsChecked(IList<ListViewItem> items, bool on) {
            rebuilding = true;
            list.BeginUpdate();
            try {
                foreach (var it in items) {
                    var j = it.Tag as DrawingJob;
                    if (j != null) { j.Selected = on; if (it.Checked != on) it.Checked = on; ApplyItemColor(it, j); }
                }
            } finally { list.EndUpdate(); rebuilding = false; }
            UpdateSummaryAndButton();
        }

        // ---- 执行出图 ----

        void RunSelected() {
            var todo = new List<ListViewItem>();
            // 若当前有筛选，只跑全部已勾选的 jobs（保证可见项实时更新；未显示的勾选行也同步处理）
            var itemMap = new Dictionary<DrawingJob, ListViewItem>();
            foreach (ListViewItem it in list.Items) {
                var j = it.Tag as DrawingJob;
                if (j != null) itemMap[j] = it;
            }
            var selectedJobs = jobs.Where(j => j.Selected).ToList();
            if (selectedJobs.Count == 0) {
                status.Text = "没有勾选要出图的文件。";
                status.ForeColor = Ui.Warn;
                return;
            }
            RunJobList(selectedJobs, itemMap);
        }

        void RunItems(IList<ListViewItem> targetItems) {
            var itemMap = new Dictionary<DrawingJob, ListViewItem>();
            var targetJobs = new List<DrawingJob>();
            foreach (var it in targetItems) {
                var j = it.Tag as DrawingJob;
                if (j != null) { targetJobs.Add(j); itemMap[j] = it; }
            }
            if (targetJobs.Count > 0) RunJobList(targetJobs, itemMap);
        }

        string SelectedDimensionStyle() {
            var s = dimStyleBox.SelectedItem as string;
            return DrawingStyleRules.NormalizePreferredStyle(s);
        }

        void RunJobList(IList<DrawingJob> todo, Dictionary<DrawingJob, ListViewItem> itemMap) {
            if (busy || todo.Count == 0) return;
            if (app == null) { status.Text = "未连接到天工 CAD。"; status.ForeColor = Ui.Bad; return; }
            busy = true;
            stop = false;
            preview.Hold(true);
            run.Text = "停止";
            run.Enabled = true;
            close.Enabled = false;
            bar.Visible = true;
            bar.Maximum = todo.Count;
            bar.Value = 0;

            int done = 0, ok = 0;
            string lastOutput = "";
            bool keepEach = openAfter.Checked && todo.Count <= 3;
            try {
                for (int idx = 0; idx < todo.Count; idx++) {
                    if (stop) { status.Text = "已停止。"; status.ForeColor = Ui.Warn; break; }
                    var j = todo[idx];
                    status.Text = "正在出图 " + (done + 1) + "/" + todo.Count + "：" + Path.GetFileName(j.File);
                    status.ForeColor = Ui.Muted;
                    j.Status = "出图中…";
                    ListViewItem it;
                    if (itemMap.TryGetValue(j, out it)) { RefreshItemVisual(it); it.EnsureVisible(); }
                    Application.DoEvents();

                    bool isLast = idx == todo.Count - 1;
                    bool keep = keepEach || (openAfter.Checked && isLast);
                    var opt = new DrawingOptions { Overwrite = overwrite.Checked, Close = !keep, DimensionStyle = SelectedDimensionStyle() };
                    var o = j.Kind == "装配" ? DrawingMaker.Assembly(app, j.File, opt) : DrawingMaker.Part(app, j.File, opt);
                    j.Result = o;
                    j.Status = "";
                    if (o.Ok) {
                        ok++;
                        j.HasDrawing = DrawingBatch.HasDrawing(j.File) || (!string.IsNullOrEmpty(o.Output) && File.Exists(o.Output));
                        if (!string.IsNullOrEmpty(o.Output)) lastOutput = o.Output;
                    }
                    if (itemMap.TryGetValue(j, out it)) RefreshItemVisual(it);
                    done++;
                    bar.Value = done;
                    Application.DoEvents();
                }
                if (!stop) {
                    status.Text = "出完了：成功 " + ok + " 张" + (done - ok > 0 ? "，失败 " + (done - ok) + " 张" : "") + "。双击一行可打开对应工程图。";
                    status.ForeColor = (done - ok) > 0 ? Ui.Warn : Ui.Ok;
                }
            } catch (Exception e) {
                status.Text = "出图中断：" + e.Message;
                status.ForeColor = Ui.Bad;
                Log.Write("DrawingForm", e);
            } finally {
                busy = false;
                close.Enabled = true;
                bar.Visible = false;
                preview.Hold(false);
                UpdateSummaryAndButton();
            }
        }

        static string ResolveDrawingPath(DrawingJob j) {
            if (j == null) return "";
            if (j.Result != null && !string.IsNullOrEmpty(j.Result.Output) && File.Exists(j.Result.Output)) return j.Result.Output;
            if (j.HasDrawing) {
                string cand = Path.ChangeExtension(j.File, ".dft");
                if (File.Exists(cand)) return cand;
            }
            return "";
        }

        void OpenSelectedDrawing() {
            if (busy || list.SelectedItems.Count == 0) return;
            var j = list.SelectedItems[0].Tag as DrawingJob;
            string f = ResolveDrawingPath(j);
            if (f.Length == 0) {
                status.Text = "该零件还没有对应的 .dft 工程图，可右键选择「仅为此项出图」。";
                status.ForeColor = Ui.Muted;
                return;
            }
            OpenPathInCad(f);
        }

        void OpenPathInCad(string f) {
            if (busy || string.IsNullOrEmpty(f) || !File.Exists(f) || app == null) return;
            preview.Hold(true);
            try {
                app.Documents.Open(f);
                status.Text = "已在天工中打开：" + Path.GetFileName(f);
                status.ForeColor = Ui.Ok;
            } catch (Exception e) {
                MessageBox.Show(this, "打不开：" + e.Message, Title);
            } finally {
                preview.Hold(false);
            }
        }

#if TG_DEV_BUILD
        internal int TestAddFiles(IEnumerable<string> files, bool explicitSelect) { return AppendModelFiles(files, explicitSelect); }
        internal void TestSetSearch(string text) { searchBox.Text = text ?? ""; RebuildList(); }
        internal void TestSelectMissingOnly() { SetVisibleChecked(j => !j.HasDrawing && string.IsNullOrEmpty(j.Reason)); }
        internal int TestVisibleCount { get { return list.Items.Count; } }
        internal int TestCheckedCount { get { return jobs.Count(j => j.Selected); } }
        internal string TestDimensionStyle { get { return SelectedDimensionStyle(); } }
        internal void TestSetDimensionStyle(string style) {
            int idx = dimStyleBox.Items.IndexOf(DrawingStyleRules.NormalizePreferredStyle(style));
            if (idx >= 0) dimStyleBox.SelectedIndex = idx;
        }
#endif
    }
}
