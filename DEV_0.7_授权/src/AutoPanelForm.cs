using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using F=SolidEdgeFramework;
using A=SolidEdgeAssembly;

namespace TianGongCadSuite {
    // 命令 2：型材自动填充。在装配里多选型材 → 自动识别闭合矩形框口 → 每个框口生成一块内嵌板（独立 PAR）插进装配。
    //
    // 2026-10-14 重写界面（和打孔系列同一套 UiKit 样式），并且：
    //   · 输入厚度/间隙不再"敲一个字符就重算一遍"：停手 350ms 才算（原来输到"6."这种半截数字还会报红字）；
    //   · 一次可以选好几个面的型材，按平面自动分开识别（FrameGrouping），不用一面一面地框选；
    //   · 框口列表可以勾选，只生成勾上的；
    //   · 材质、尺寸写进零件文件属性（BOM/明细栏直接引用）；
    //   · 输出目录、厚度、间隙、材质记住（autopanel.tsv），默认放在装配所在目录；
    //   · 生成完窗口不关，生成过的框口标出来、默认不再勾选（免得重复生成）。
    // 每个框口仍然各生成一个 PAR（不合并成一个零件）。
    //
    // 2026-10-15 跨子装配：围栏常是四个子装配环起来的，某一面的立柱属于相邻的子装配。现在在顶层装配里
    //   · 可以 Ctrl 多选不同子装配里的型材（FrameReader 用 OccurrencePick 解析子装配内部的选择），也可以直接选几个整的子装配；
    //   · 「追加选择」把这次的选择加到已读的型材里，可以分几次点；「清空」重来；
    //   · 子装配里已有的板子（填充板、玻璃板）不当型材，盖住的框口标「已有板」、默认不勾。
    // 生成的板子插在当前打开的装配里（跨子装配时就是顶层装配）。
    public sealed class AutoPanelForm : Form, IBusyTool {
        // 生成中（会泵消息）点功能区别的命令：不能把这个窗口连同生成到一半的板子一起关掉（见 IBusyTool）
        public bool Busy { get { return busy; } }
        public string BusyWhat { get { return "生成板子"; } }
        protected override void OnFormClosing(FormClosingEventArgs e){
            if (busy && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; return; }
            base.OnFormClosing(e);
        }
        const string SettingsName = "autopanel.tsv";
        static readonly string[] Materials = { "", "玻璃", "钢化玻璃", "亚克力", "PC 耐力板", "铝板", "不锈钢板", "钢板", "木板" };

        readonly F.Application app;
        readonly A.AssemblyDocument assembly;
        readonly NumericUpDown thickness = Ui.Num(0.1M, 500M, 6M, 2, 0.5M, 80);
        readonly NumericUpDown gap = Ui.Num(0M, 100M, 0M, 2, 0.5M, 80);
        readonly ComboBox material = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Width = 150 };
        readonly Label memberInfo = new Label();
        readonly CheckedListBox openings = new CheckedListBox();
        readonly Label outputLabel = new Label();
        readonly Label status = new Label();
        readonly Button read = Ui.Primary("读取当前选择", 150, 40);
        readonly Button append = Ui.Secondary("追加选择", 100, 38);
        readonly Button clear = Ui.Ghost("清空");
        readonly Button generate = Ui.Primary("生成选中的框口", 220, 44);
        readonly Preview preview = new Preview();
        readonly Timer debounce = new Timer { Interval = 350 };
        readonly ToolTip tips = new ToolTip { AutoPopDelay = 15000, InitialDelay = 400 };

        List<object> selection = new List<object>();
        List<FrameMember> cachedMembers;
        List<FrameMember> plates = new List<FrameMember>();   // 已有的板子（标「已有板」用）
        List<FoundOpening> found = new List<FoundOpening>();
        List<PanelSpec> specs = new List<PanelSpec>();
        List<bool> hasPanel = new List<bool>();
        readonly HashSet<string> generated = new HashSet<string>();   // 生成过的框口（按中心点）
        string outputDir = "";
        bool busy, loading;

        internal int OpeningCount { get { return specs.Count; } }
        internal string StatusText { get { return status.Text; } }
        internal string MemberText { get { return memberInfo.Text; } }
        internal string[] OpeningTexts { get { return openings.Items.Cast<object>().Select(x => x.ToString()).ToArray(); } }
        internal bool[] OpeningChecked { get { return Enumerable.Range(0, openings.Items.Count).Select(i => openings.GetItemChecked(i)).ToArray(); } }
        internal PanelSpec Opening(int i){ return specs[i]; }
        internal void CheckOnly(int i){ for (int k = 0; k < openings.Items.Count; k++) openings.SetItemChecked(k, k == i); UpdateGenerateButton(); }

        public AutoPanelForm(F.Application app, A.AssemblyDocument assembly){
            this.app = app; this.assembly = assembly;
            Ui.Shell(this, Product.Title("型材自动填充"), 1120, 720, 960, 600);
            material.Items.AddRange(Materials);
            BuildLayout();
            LoadSettings();
            debounce.Tick += (s, e) => { debounce.Stop(); Recompute(); };
            foreach (var n in new[]{ thickness, gap }) n.ValueChanged += (s, e) => { if (!loading) { debounce.Stop(); debounce.Start(); } };
            FormClosed += (s, e) => { debounce.Dispose(); tips.Dispose(); };
            ReadSelection();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData){
            // 跨子装配一根根点好的型材、识别出的框口：一个 Esc 就全没了。读过型材时要连按两次（和打孔窗口一样）
            if (keyData == Keys.Escape) { if (busy) return true; if (quickClose.Confirm("Esc", cachedMembers != null && cachedMembers.Count > 0 ? "已读的 " + cachedMembers.Count + " 根型材和识别出的框口" : "", m => SetStatus(m, Ui.Warn))) Close(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }
        readonly QuickClose quickClose = new QuickClose();

        // 0.9.0「D · 侧栏向导」：① 型材（读取 / 追加选择）→ ② 板子（厚度、间隙、材质）→ ③ 框口（勾选 + 右边位置预览）。
        // 读到型材、识别出框口后自动翻到「框口」；输出目录在侧栏底部；主按钮「生成 N 块板子」。
        const int PageMembers = 0, PagePanel = 1, PageOpenings = 2;
        readonly WizardShell shell = new WizardShell();
        readonly FlatButton nextButton = (FlatButton)Ui.Primary("下一步", 200, 44);
        readonly FlatButton backButton = (FlatButton)Ui.Secondary("上一步", 84, 38);

        void BuildLayout(){
            Controls.Add(shell);
            var pM = shell.AddPage("型材", "型材", "");
            var pP = shell.AddPage("板子", "板子", "");
            var pO = shell.AddPage("框口", "框口", "");

            // ---- ① 型材 ----
            var mb = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Bg };
            pM.Body.Controls.Add(mb);
            var mHint = new Label { Text = "在装配里多选型材（可跨子装配：Ctrl 多选型材 / 选整个子装配），然后点「读取当前选择」。会按平面自动分开识别。", AutoSize = false, ForeColor = Ui.Text2, BackColor = Ui.Bg };
            read.Click += (s, e) => ReadSelection();
            append.Click += (s, e) => ReadSelection(true);
            ((FlatButton)clear).SetFore(Ui.Text2);
            clear.Click += (s, e) => ClearMembers();
            tips.SetToolTip(read, "用 CAD 里当前选中的型材重新识别（替换掉已读的）。");
            tips.SetToolTip(append, "把 CAD 里当前选中的型材加到已读的里面再识别。\n例如：先选整个子围栏读取，再点选隔壁子装配的立柱「追加选择」。");
            var infoBox = new RoundBox { Fill = Ui.Rail, Padding = new Padding(16, 12, 16, 12) };
            memberInfo.Dock = DockStyle.Fill; memberInfo.AutoSize = false; memberInfo.ForeColor = Ui.Text2; memberInfo.BackColor = Ui.Rail;
            memberInfo.Text = Hint;
            infoBox.Controls.Add(memberInfo);
            mb.Controls.AddRange(new Control[] { mHint, read, append, clear, infoBox });
            mb.Layout += (s, e) => {
                int w = mb.ClientSize.Width;
                mHint.SetBounds(0, 0, w, 44);
                read.Location = new Point(0, 52); append.Location = new Point(read.Right + 8, 53); clear.Location = new Point(append.Right + 4, 56);
                infoBox.SetBounds(0, 108, w, Math.Max(80, Math.Min(200, mb.ClientSize.Height - 120)));
            };

            // ---- ② 板子 ----
            var grid = new FieldGrid { LabelWidth = 84, BackColor = Ui.Bg };
            grid.Add("总厚度", thickness, FieldGrid.Unit("mm"));
            grid.Add("每边间隙", gap, FieldGrid.Unit("mm"), FieldGrid.Space(8), Ui.Muted8("板边离型材槽底的距离"));
            material.FlatStyle = FlatStyle.Flat; material.Width = 170;
            grid.Add("材质", material, FieldGrid.Space(8), Ui.Muted8("写进零件属性，可留空"));
            tips.SetToolTip(material, "写进零件的自定义属性「材质」，并尝试套用天工材料库里同名的材料。可以直接输入别的名字。");
            var pb = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Bg };
            var pHint = new Label { Text = "改了数字停一下就会重新识别框口。", AutoSize = false, ForeColor = Ui.Muted, Font = Ui.F8, BackColor = Ui.Bg };
            pb.Controls.Add(grid); pb.Controls.Add(pHint);
            pb.Layout += (s, e) => { grid.SetBounds(0, 0, pb.ClientSize.Width, grid.PreferredHeight); pHint.SetBounds(0, grid.PreferredHeight + 6, pb.ClientSize.Width, 22); };
            pP.Body.Controls.Add(pb);

            // ---- ③ 框口：左列表，右位置预览 ----
            var all = Ui.Small("全选"); var none = Ui.Small("全不选");
            ((FlatButton)none).SetFore(Ui.Text2);
            all.Click += (s, e) => SetAllChecked(true);
            none.Click += (s, e) => SetAllChecked(false);
            shell.AddHeader(pO, all); shell.AddHeader(pO, none);
            var cols = new SplitColumns(340) { Dock = DockStyle.Fill, Gap = 20 };
            var listFrame = Ui.Card("", 0, "");
            listFrame.Padding = new Padding(8, 8, 4, 8);
            openings.Dock = DockStyle.Fill; openings.BorderStyle = BorderStyle.None; openings.IntegralHeight = false;
            openings.CheckOnClick = true; openings.BackColor = Color.White; openings.Font = Ui.F9;
            openings.SelectedIndexChanged += (s, e) => ShowPreview();
            // ItemCheck 触发时勾选状态还没改，所以延后刷新按钮；窗口还没显示时（构造里读预选就会 Recompute）
            // 不能 BeginInvoke，而 Recompute 末尾本来就会刷新，跳过即可。
            openings.ItemCheck += (s, e) => { if (IsHandleCreated) BeginInvoke((Action)UpdateGenerateButton); };
            listFrame.Controls.Add(openings);
            // 预览控件自己画标题和操作提示，卡片不再写一遍
            var previewCard = Ui.Card("", 0, "");
            previewCard.Padding = new Padding(2, 2, 2, 2);
            preview.Dock = DockStyle.Fill;
            preview.EmptyText = "读取型材、识别出框口后，点左边列表里的框口在这里看位置。";
            previewCard.Controls.Add(preview);
            cols.Add(listFrame); cols.Add(previewCard);
            pO.Body.Controls.Add(cols);

            // ---- 侧栏底部：输出目录 ----
            var outCard = new RailCard("输出目录", 110);
            var change = Ui.Small("更改…");
            change.Click += (s, e) => PickOutputDir();
            outputLabel.AutoEllipsis = true; outputLabel.BackColor = Color.White; outputLabel.ForeColor = Ui.Muted; outputLabel.Font = Ui.F8;
            outCard.Controls.Add(outputLabel); outCard.Controls.Add(change);
            outCard.Resize += (s, e) => { outputLabel.SetBounds(12, 32, outCard.Width - 24, 40); change.Location = new Point(6, outCard.Height - 34); };
            tips.SetToolTip(outputLabel, "每批一个子目录，每个框口一个 PAR");
            shell.RailBottom = outCard;

            // ---- 底部操作栏 ----
            generate.Enabled = false;
            generate.Click += (s, e) => Generate();
            backButton.Click += (s, e) => shell.Go(shell.Current - 1);
            nextButton.Click += (s, e) => shell.Go(shell.Current + 1);
            shell.Footer.AddLeft(backButton);
            shell.Footer.AddRight(generate); shell.Footer.AddRight(nextButton);
            shell.Footer.Status = status;
            status.TextChanged += (s, e) => tips.SetToolTip(status, status.Text);
            shell.PageChanged += (s, e) => UpdateGenerateButton();
        }

        public void GoPageForTest(int i){ shell.Go(i, false); }
        void RefreshRail(){
            int members = cachedMembers == null ? 0 : cachedMembers.Count;
            shell.Rail.Set(PageMembers, members == 0 ? "选型材后点读取" : members + " 根型材", members > 0, false);
            shell.Rail.Set(PagePanel, "厚 " + thickness.Value.ToString("0.##", CultureInfo.InvariantCulture) + " · 间隙 " + gap.Value.ToString("0.##", CultureInfo.InvariantCulture), true, false);
            int n = openings.CheckedIndices.Count;
            shell.Rail.Set(PageOpenings, specs.Count == 0 ? "读取型材后自动识别" : "识别出 " + specs.Count + " 个，勾选 " + n + " 个", n > 0, false);
        }

        // ---------------- 设置（记住上次的输入）----------------
        string savedDir = "", savedFor = "";
        string AssemblyPath(){
            try { string f = assembly.FullName; if (!string.IsNullOrEmpty(f) && File.Exists(f)) return f; } catch { }
            return "";
        }

        void LoadSettings(){
            loading = true;
            try {
                foreach (var r in SettingsFile.Read(SettingsFile.PathOf(SettingsName))) {
                    if (r.Length < 2) continue;
                    decimal d;
                    if (r[0] == "thickness" && decimal.TryParse(r[1], NumberStyles.Float, CultureInfo.InvariantCulture, out d)) thickness.Value = Ui.Clamp(d, thickness.Minimum, thickness.Maximum);
                    else if (r[0] == "gap" && decimal.TryParse(r[1], NumberStyles.Float, CultureInfo.InvariantCulture, out d)) gap.Value = Ui.Clamp(d, gap.Minimum, gap.Maximum);
                    else if (r[0] == "material") material.Text = r[1];
                    else if (r[0] == "outputDir" && Directory.Exists(r[1])) savedDir = r[1];
                    else if (r[0] == "outputFor") savedFor = r[1];
                }
            } catch (Exception e) { Log.Write("AutoPanelSettings", e); }
            finally { loading = false; }
            // 记下的目录只在同一个装配上沿用；默认是装配所在目录（新装配还没保存时没有目录，生成前会让用户选）
            outputDir = PanelNaming.OutputDirFor(AssemblyPath(), savedDir, savedFor);
            ShowOutputDir();
        }

        void SaveSettings(){
            try {
                var ci = CultureInfo.InvariantCulture;
                SettingsFile.Write(SettingsFile.PathOf(SettingsName), new List<string[]>{
                    new[]{ "thickness", thickness.Value.ToString(ci) }, new[]{ "gap", gap.Value.ToString(ci) },
                    new[]{ "material", material.Text.Trim() }, new[]{ "outputDir", outputDir }, new[]{ "outputFor", AssemblyPath() } },
                    "天工工具箱 · 型材自动填充上次的输入");
            } catch (Exception e) { Log.Write("AutoPanelSettingsSave", e); }
        }

        void ShowOutputDir(){
            outputLabel.Text = outputDir.Length > 0 ? outputDir : "（还没选 —— 生成时会让你选）";
            outputLabel.ForeColor = outputDir.Length > 0 ? Ui.Text : Ui.Muted;
            tips.SetToolTip(outputLabel, outputLabel.Text);
        }

        bool PickOutputDir(){
            using (var dialog = new FolderBrowserDialog { Description = "选择输出目录：每批会建一个子目录，每个框口一个 PAR", SelectedPath = outputDir }) {
                if (dialog.ShowDialog(this) != DialogResult.OK) return false;
                outputDir = dialog.SelectedPath;
            }
            ShowOutputDir();
            SaveSettings();
            return true;
        }

        // ---------------- 读型材 / 识别框口 ----------------
        void Context(){
            if (!ReferenceEquals(app.ActiveDocument, assembly) || assembly.ReadOnly || assembly.InPlaceActivated)
                throw new InvalidOperationException("请保持目标装配处于活动、可编辑的顶层状态。");
        }

        const string Hint = "一次可以选好几个面的型材，会按平面自动分开识别。\n边框型材在隔壁子装配里的，一起选上（或「追加选择」）就行。";

        internal void ReadSelection(){ ReadSelection(false); }

        // append=true：这次的选择加到已读的上面（FrameReader 按实例去重，重复选不会算两次）
        internal void ReadSelection(bool append){
            if (busy) return;   // 读型材 / 生成时泵消息，别再点进来
            var previous = selection; var previousMembers = cachedMembers; var previousPlates = plates; string previousInfo = memberInfo.Text;
            string summary = null;
            try {
                Context();
                var picked = assembly.SelectSet.Cast<object>().ToList();
                if (append && picked.Count == 0) { SetStatus("CAD 里没有选中东西：先选要加的型材，再点「追加选择」。", Ui.Warn); return; }
                var next = append ? previous.Concat(picked).ToList() : picked;
                List<FrameMember> members = null, newPlates = new List<FrameMember>();
                if (next.Count > 0) {
                    UseWaitCursor = true;
                    // 读的时候泵消息（整个围栏要读好一阵，原来不泵消息天工被判成"未响应"）；泵消息期间窗口不许关（busy）
                    busy = true;
                    var tick = System.Diagnostics.Stopwatch.StartNew();
                    FrameSelection read;
                    try {
                        read = FrameReader.ReadAll(assembly, next, n => {
                            if (tick.ElapsedMilliseconds < 300) return;
                            tick.Restart();
                            SetStatus("正在读取型材… 已读 " + n + " 个零件", Ui.Accent);
                            try { status.Update(); Application.DoEvents(); } catch { }
                        });
                    } finally { busy = false; }
                    summary = read.Summary();
                    tips.SetToolTip(memberInfo, summary + (read.Skipped.Count > 0 ? "\n跳过的零件：" + string.Join("、", read.Skipped.ToArray()) : ""));
                    if (read.Members.Count < 4) throw new ArgumentException("请至少选择围成框口的四根型材，或选择包含它们的框架子装配。");
                    members = read.Members; newPlates = read.Plates;
                }
                selection = next; cachedMembers = members; plates = newPlates;
                memberInfo.Text = summary ?? Hint;
                if (!append) generated.Clear();
                Recompute();
            } catch (Exception e) {
                if (append && previous.Count > 0) {
                    // 追加失败不丢掉已读的：保持上一次的型材和框口
                    selection = previous; cachedMembers = previousMembers; plates = previousPlates; memberInfo.Text = previousInfo;
                    SetStatus("追加失败（已读的型材保持不变）：" + e.Message, Ui.Bad); tips.SetToolTip(status, e.Message);
                    return;
                }
                selection = new List<object>(); cachedMembers = null; plates = new List<FrameMember>();
                memberInfo.Text = summary ?? Hint;
                ClearResult(e.Message);
                tips.SetToolTip(status, e.Message);
            } finally { UseWaitCursor = false; }
        }

        void ClearMembers(){
            if (busy) return;
            selection = new List<object>(); cachedMembers = null; plates = new List<FrameMember>();
            generated.Clear();
            memberInfo.Text = Hint; tips.SetToolTip(memberInfo, "");
            Recompute();
        }

        internal void SetParameters(string t, string g){
            decimal a, b;
            loading = true;
            try {
                if (decimal.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out a)) thickness.Value = Ui.Clamp(a, thickness.Minimum, thickness.Maximum);
                if (decimal.TryParse(g, NumberStyles.Float, CultureInfo.InvariantCulture, out b)) gap.Value = Ui.Clamp(b, gap.Minimum, gap.Maximum);
            } finally { loading = false; }
            Recompute();   // 脚本/测试调用：立刻算，不等防抖
        }

        void ClearResult(string text){
            found.Clear(); specs.Clear(); hasPanel.Clear(); openings.Items.Clear();
            preview.Spec = null; preview.SourceEdges.Clear(); preview.Invalidate();
            SetStatus(text, Ui.Bad);
            UpdateGenerateButton();
        }

        static string Key(PanelSpec s){
            var c = s.Corner(0, 0) + s.U * (s.Width / 2) + s.V * (s.Height / 2);
            return Math.Round(c.X, 5) + "," + Math.Round(c.Y, 5) + "," + Math.Round(c.Z, 5);
        }

        void Recompute(){
            if (busy) return;
            debounce.Stop();
            if (selection.Count == 0 || cachedMembers == null) {
                ClearResult("");
                SetStatus("还没读取型材：在模型里多选型材后点「读取当前选择」。", Ui.Muted);
                return;
            }
            try {
                Context();
                double t = (double)thickness.Value / 1000, g = (double)gap.Value / 1000;
                var notes = new List<string>();
                found = FrameGrouping.Detect(cachedMembers, t, g, notes);
                specs = found.Select(o => o.Opening.Solve(t, g)).ToList();
                hasPanel = specs.Select(s => plates.Any(p => PanelPlates.Covers(s, p.Points))).ToList();
                int planes = found.Select(o => o.PlaneIndex).Distinct().Count();
                int sources = cachedMembers.Select(m => m.Source).Distinct().Count();
                openings.BeginUpdate();
                try {
                    openings.Items.Clear();
                    for (int i = 0; i < specs.Count; i++) {
                        var s = specs[i];
                        bool done = generated.Contains(Key(s));
                        string mark = done ? "　（已生成）" : hasPanel[i] ? "　（已有板）" : "";
                        // 框口的型材来自不止一个子装配时标出来（正是以前生成不了的那种）
                        // Source 为空 = 顶层零件（不属于任何子装配）
                        var from = found[i].Members.Where(m => PanelPlates.Borders(s, m.Points)).Select(m => m.Source ?? "").Distinct().ToList();
                        string cross = from.Count < 2 ? "" : from.Contains("") ? "　跨子装配（含顶层零件）" : "　跨 " + from.Count + " 个子装配";
                        string text = string.Format(CultureInfo.InvariantCulture, "{0} · 框口 {1}：{2:0.###} × {3:0.###} × {4:0.###} mm{5}{6}",
                            planes > 1 ? found[i].PlaneLabel : "", i + 1, s.Width * 1000, s.Height * 1000, s.Thickness * 1000,
                            sources > 1 ? cross : "", mark);
                        openings.Items.Add(text.TrimStart(' ', '·'), !done && !hasPanel[i]);
                    }
                } finally { openings.EndUpdate(); }
                if (openings.Items.Count > 0) openings.SelectedIndex = 0;
                int existing = hasPanel.Count(x => x);
                string msg = "识别到 " + specs.Count + " 个闭合框口" + (planes > 1 ? "（" + planes + " 个平面）" : "")
                    + (existing > 0 ? "，其中 " + existing + " 个已经有板（默认不勾）" : "") + "。检查预览，勾上要生成的。";
                // 选整个围栏时，横梁那几层（只有平行型材）、顶框之类每层都会有一条说明，全写进状态栏就淹没了结果：
                // 多于 2 条只报条数，原因放在提示里
                string detail = notes.Count > 0 ? "\n\n没识别出框口的平面：\n" + string.Join("\n", notes.ToArray()) : "";
                if (notes.Count > 2) msg += "　另有 " + notes.Count + " 个平面没识别出框口（鼠标停在这里看原因）。";
                else if (notes.Count > 0) msg += "　另有：" + string.Join("；", notes.ToArray());
                SetStatus(msg, notes.Count > 0 ? Ui.Warn : Ui.Ok);
                // 读到型材、识别出框口：翻到「框口」（在「板子」页改数字时不跳）
                if (specs.Count > 0 && shell.Current == PageMembers) shell.Go(PageOpenings);
                tips.SetToolTip(status, msg + (notes.Count > 2 ? detail : ""));
            } catch (Exception e) {
                ClearResult(e.Message);
                tips.SetToolTip(status, e.Message);
                Log.Write("AutoDetect", e);
            }
            UpdateGenerateButton();
        }

        void ShowPreview(){
            int i = openings.SelectedIndex;
            preview.Spec = i >= 0 && i < specs.Count ? specs[i] : null;
            preview.SourceEdges = i >= 0 && i < found.Count
                ? found[i].Members.SelectMany(m => m.Faces).SelectMany(f => f.Edges).ToList()
                : new List<V3[]>();
            if (preview.Spec != null) preview.PickedPoint = preview.Spec.Corner(0, 0);
            preview.Invalidate();
        }

        void SetAllChecked(bool on){
            for (int i = 0; i < openings.Items.Count; i++) openings.SetItemChecked(i, on);
            UpdateGenerateButton();
        }

        void UpdateGenerateButton(){
            int n = openings.CheckedIndices.Count;
            generate.Text = n > 0 ? "生成 " + n + " 块板子" : "生成选中的框口";
            generate.Enabled = n > 0 && !busy;
            bool showGen = busy || n > 0 || shell.Current == PageOpenings;
            generate.Visible = showGen; nextButton.Visible = !showGen;
            if (!showGen) nextButton.Text = "下一步：" + shell.Pages[Math.Min(shell.Current + 1, shell.Pages.Count - 1)].Title;
            backButton.Enabled = shell.Current > 0 && !busy;
            shell.Footer.PerformLayout();
            RefreshRail();
        }

        // ---------------- 生成 ----------------
        void Generate(){
            if (busy) return;
            try {
                var pick = openings.CheckedIndices.Cast<int>().Where(i => i < specs.Count).ToList();
                if (pick.Count == 0) { SetStatus("先勾上要生成的框口。", Ui.Warn); return; }
                if (outputDir.Length == 0 || !Directory.Exists(outputDir)) { if (!PickOutputDir()) return; }
                // 生成前按当前选择重读一遍型材（几何可能在窗口开着的时候改过），框口对不上就不生成
                // 重读同样要泵消息（整个围栏要读好一阵）
                FrameSelection reread;
                busy = true;
                try {
                    var rt = System.Diagnostics.Stopwatch.StartNew();
                    reread = FrameReader.ReadAll(assembly, selection, n => {
                        if (rt.ElapsedMilliseconds < 300) return;
                        rt.Restart();
                        SetStatus("生成前核对型材… 已读 " + n + " 个零件", Ui.Accent);
                        try { status.Update(); Application.DoEvents(); } catch { }
                    });
                } finally { busy = false; }
                if (reread.Members.Count < 4) throw new InvalidOperationException("型材在窗口打开期间改过（读到的型材不足 4 根）。请重新选择型材后点「读取当前选择」。");
                cachedMembers = reread.Members; plates = reread.Plates;
                double t = (double)thickness.Value / 1000, g = (double)gap.Value / 1000;
                var fresh = FrameGrouping.Detect(cachedMembers, t, g, null).Select(o => o.Opening.Solve(t, g)).ToList();
                var chosen = new List<PanelSpec>();
                foreach (var s in pick.Select(i => specs[i])) {
                    var f = fresh.FirstOrDefault(x => Key(x) == Key(s));
                    if (f == null) throw new InvalidOperationException("型材在窗口打开期间改过，框口和刚才识别的不一样了。请点「读取当前选择」重新识别。");
                    // 用按现在的厚度 / 间隙重新解出来的那一块：改了板厚马上点生成（停手 350ms 的重算还没轮到），
                    // 原来拿的是列表里按旧厚度算的板，生成出来还是旧厚度
                    chosen.Add(f);
                }
                debounce.Stop();

                busy = true; Enabled = false; UseWaitCursor = true;
                SetStatus("正在生成 " + chosen.Count + " 块板子…", Ui.Accent); Application.DoEvents();
                var props = new PanelProps { Material = material.Text.Trim() };
                // 一块一块报进度、泵消息（窗口禁用着，点不了）：几十块板要一两分钟，原来整段不泵消息，Windows 把天工判成"未响应"
                var tick = System.Diagnostics.Stopwatch.StartNew();
                string dir = BatchPanels.Generate(app, assembly, chosen, outputDir, props, (i, n) => {
                    if (tick.ElapsedMilliseconds < 300) return;
                    tick.Restart();
                    SetStatus("正在生成第 " + i + " / " + n + " 块板子…", Ui.Accent);
                    try { status.Update(); Application.DoEvents(); } catch { }
                });
                for (int i = 0; i < chosen.Count; i++) if (!props.Failed.Contains(i)) generated.Add(Key(chosen[i]));
                SaveSettings();
                busy = false; Enabled = true; UseWaitCursor = false;
                Recompute();   // 刷新列表：生成过的标出来、取消勾选
                string msg = "已生成 " + (chosen.Count - props.Failed.Count) + " 块板子" + (props.Failed.Count > 0 ? "（" + props.Failed.Count + " 块没生成，见下面）" : "") + "：" + dir + "　记得保存装配。";
                if (props.Problems.Count > 0) msg += "　注意：" + string.Join("；", props.Problems.ToArray());
                SetStatus(msg, props.Problems.Count > 0 ? Ui.Warn : Ui.Ok);
                tips.SetToolTip(status, msg);
            } catch (Exception e) {
                Log.Write("AutoGenerate", e);
                busy = false; Enabled = true; UseWaitCursor = false;
                SetStatus(e.Message, Ui.Bad);
                tips.SetToolTip(status, e.Message);
                UpdateGenerateButton();
            }
        }

        void SetStatus(string text, Color color){ status.Text = text; status.ForeColor = color; }
    }
}
