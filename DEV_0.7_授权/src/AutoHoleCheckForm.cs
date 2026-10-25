using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using A=SolidEdgeAssembly;
using F=SolidEdgeFramework;

namespace TianGongCadSuite {
    // 命令 8：配孔检查。扫一遍装配里所有零件的孔，报漏打孔 / 孔偏了 / 配错孔（孤孔默认不显示）。
    //
    // 2026-10-13 界面重排（和「自动打孔」同一套样式）：
    //   · 顶部一行：开始检查 / 复制结果 / 判定口径（悬停看）+ 一句话结论；
    //   · 结果卡片：标题行上四个可点的筛选胶囊（漏打孔 3 · 孔偏了 1 · 配错孔 0 · 孤孔 12），点一下显示/隐藏；
    //   · 选中一条，下面的「详情」列出涉及的每个孔（零件、孔径、位置），模型里同时高亮。
    // 孤孔原来要先勾"忽略孤孔"再重新检查才能切换；现在检查时一律算出来，显示与否只是筛选。
    public sealed class AutoHoleCheckForm : Form, IBusyTool {
        // 处理中不让关（Esc、关闭按钮、标题栏 ×、CAD 里按 Esc / 右键都会走到这里）：见 IBusyTool
        public bool Busy { get { return busy; } }
        public string BusyWhat { get { return "检查"; } }
        protected override void OnFormClosing(FormClosingEventArgs e){
            if (busy && e.CloseReason == CloseReason.UserClosing) {
                e.Cancel = true;
                try { status.Text = "正在检查，做完再关。"; status.ForeColor = Ui.Warn; } catch { }
                return;
            }
            base.OnFormClosing(e);
        }

        readonly F.Application app;
        readonly A.AssemblyDocument assembly;
        F.HighlightSet highlights;
        List<HoleIssue> issues = new List<HoleIssue>();
        List<HoleIssue> shown = new List<HoleIssue>();
        int holeCount, groupCount, failedParts;
        bool busy, checkedOnce;          // 扫描中：RunCheck 里有 DoEvents，必须挡住重入
        readonly Label status = new Label();
        readonly Label summary = new Label();
        readonly ListBox list = new ListBox();
        readonly TextBox detail = new TextBox();
        readonly Button run = Ui.Primary("开始检查", 200, 44);
        readonly Button copy = Ui.Ghost("复制结果");
        readonly Label rules = new Label();
        readonly ToggleChip cMissing = new ToggleChip { Text = "漏打孔 0", ChipColor = Ui.Bad };
        readonly ToggleChip cMis = new ToggleChip { Text = "孔偏了 0", ChipColor = Ui.Warn };
        readonly ToggleChip cWrong = new ToggleChip { Text = "配错孔 0", ChipColor = Color.FromArgb(0x25, 0x63, 0xEB) };
        readonly ToggleChip cUnpaired = new ToggleChip { Text = "孤孔 0", ChipColor = Ui.Muted };
        readonly ToolTip tips = new ToolTip { AutoPopDelay = 20000, InitialDelay = 300 };
        // 跳转到孔：选中一条就把镜头拉到涉及的孔上（大装配里光高亮看不见几毫米的孔）
        readonly CheckBox autoJump = new CheckBox { Text = "选中即跳转", Checked = true, AutoSize = true };
        readonly Button jump = Ui.Secondary("跳转到孔", 100, 38);
        readonly Button goDrill = Ui.Primary("去配孔", 120, 44);
        // 「去配孔」：打开自动打孔，把这条漏打孔涉及的孔带进去当参考孔（由命令模块接上，见 AutoHoleModule）
        public Func<List<HoleRecord>, string, AutoHoleForm> GoDrill;

        public AutoHoleCheckForm(F.Application application, A.AssemblyDocument document){
            app = application; assembly = document;
            Ui.Shell(this, "配孔检查", 900, 680, 760, 560);
            // 用户改过判定口径就按他的来（读不到就是默认值）
            try { HoleCheck.Options = HoleCheckOptions.Load(SettingsFile.PathOf(HoleCheckOptions.FileName)); }
            catch (Exception e) { Log.Write("HoleCheckOptionsLoad", e); }
            cUnpaired.Checked = false;
            // 显示哪几类、选中即跳转：记住上次的（0.10.2）
            foreach (var kv in new[]{ new KeyValuePair<ToggleChip, string>(cMissing, "check.missing"), new KeyValuePair<ToggleChip, string>(cMis, "check.misaligned"), new KeyValuePair<ToggleChip, string>(cWrong, "check.wrong"), new KeyValuePair<ToggleChip, string>(cUnpaired, "check.unpaired") }) {
                var chip = kv.Key; string key = kv.Value;
                chip.Checked = Choices.Get(key, chip.Checked ? "1" : "0") == "1";
                chip.Click += (s, e) => Choices.Set(key, chip.Checked ? "1" : "0");
            }
            Choices.Bind(autoJump, "check.autoJump");
            BuildLayout();
            ApplyFilter();
            FormClosed += (s, e) => Cleanup();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData){
            // 大装配查一遍要一两分钟：查完一个 Esc 就把结果全关掉了。有结果时要连按两次（和自动打孔一样）
            if (keyData == Keys.Escape) { if (busy) return true; if (quickClose.Confirm("Esc", checkedOnce && issues.Count > 0 ? "这次检查的结果" : "", m => SetStatus(m, Ui.Warn))) Close(); return true; }
            // RunCheck 内部会 Application.DoEvents()，Ctrl+Enter 能绕开按钮禁用再进来一次，
            // 两次并发扫描会同时读写 issues/list 并对同一批 COM 对象重入 —— 必须挡。
            if (keyData == (Keys.Control | Keys.Enter)) { if (!busy) RunCheck(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ---------------- 布局 ----------------
        // 0.9.0「D · 侧栏向导」：
        // ┌ 侧栏 ─────────────┬ 发现 3 处问题　点一条在模型里高亮 ─────────────┐
        // │ [开始检查]         │ 检查了 42 个孔（17 组同轴孔）……                  │
        // │ ● 漏打孔        1  │ ┌ 漏打孔 ┐ 中间板在 Φ6.6 孔的轴线上没有孔……    │
        // │ ● 孔偏了        1  │ ┌ 孔偏了 ┐ ……                                    │
        // │ ● 配错孔        1  │                                                    │
        // │ ○ 孤孔          0  │ ┌ 涉及的孔（浅灰块）───────────────────────────┐ │
        // │ 复制结果 · 判定口径│ [☑ 选中即跳转]  状态字 ……   [跳转到孔] [去配孔] │
        // 侧栏里的四类是可以同时勾几类的筛选（孤孔默认不显示），不是单选。
        readonly WizardShell shell = new WizardShell { StepCaption = false };
        readonly Label countLine = new Label();
        readonly QuickClose quickClose = new QuickClose();
        List<string> lastWarnings = new List<string>();   // 这次读取失败的零件（状态栏只说个数，悬停 / 复制结果里列出是哪些）

        void BuildLayout(){
            Controls.Add(shell);
            var page = shell.AddPage("检查结果", "配孔检查", "");

            // ---- 侧栏顶部：开始检查 + 四类筛选 ----
            var top = new Panel { BackColor = Ui.Rail, Height = 44 + 34 + 4 * 40 };
            run.Font = Ui.BtnB;
            top.Controls.Add(run);
            countLine.AutoSize = false; countLine.Font = Ui.F8; countLine.ForeColor = Ui.Muted; countLine.BackColor = Ui.Rail; countLine.Text = "还没检查";
            countLine.TextAlign = ContentAlignment.MiddleLeft;
            top.Controls.Add(countLine);
            foreach (var c in new[]{ cMissing, cMis, cWrong, cUnpaired }) {
                c.RailStyle = true; c.Height = 36;
                c.CheckedChanged += (s, e) => ApplyFilter();
                top.Controls.Add(c);
            }
            top.Resize += (s, e) => {
                int w = top.Width;
                run.SetBounds(0, 0, w, 44);
                countLine.SetBounds(10, 48, w - 10, 24);
                int y = 78;
                foreach (var c in new[]{ cMissing, cMis, cWrong, cUnpaired }) { c.SetBounds(0, y, w, 36); y += 40; }
            };
            run.Click += (s, e) => RunCheck();
            tips.SetToolTip(run, "Ctrl+Enter");
            tips.SetToolTip(cUnpaired, "只有一个零件有孔、附近也没穿过别的零件。真实装配里大多数孔都是孤孔，默认不显示。");
            shell.RailTop = top;

            // ---- 侧栏底部：复制结果 · 判定口径 ----
            var links = new Panel { BackColor = Ui.Rail, Height = 72 };
            copy.Enabled = false;
            copy.Click += (s, e) => CopyResults();
            rules.Text = "判定口径…"; rules.AutoSize = true; rules.ForeColor = Ui.Accent; rules.Cursor = Cursors.Hand; rules.BackColor = Ui.Rail;
            tips.SetToolTip(rules, HoleCheck.Options.Describe());
            rules.Click += (s, e) => EditOptions();
            links.Controls.Add(copy); links.Controls.Add(rules);
            copy.Location = new Point(0, 4); rules.Location = new Point(10, 44);
            shell.RailBottom = links;

            // ---- 正文：一句话结论 → 问题列表 → 涉及的孔 ----
            var stack = new VerticalStack { Dock = DockStyle.Fill, Padding = new Padding(0), Gap = 10, AutoScroll = false };
            page.Body.Controls.Add(stack);
            summary.AutoSize = false; summary.TextAlign = ContentAlignment.MiddleLeft; summary.AutoEllipsis = true;
            summary.ForeColor = Ui.Muted; summary.BackColor = Ui.Bg; summary.Font = Ui.F9; summary.Height = 22;
            summary.Text = "还没检查";
            summary.TextChanged += (s, e) => tips.SetToolTip(summary, summary.Text);
            list.IntegralHeight = false; list.BorderStyle = BorderStyle.None;
            list.DrawMode = DrawMode.OwnerDrawFixed; list.ItemHeight = 66; list.BackColor = Color.White;
            list.DrawItem += DrawIssue;
            list.SelectedIndexChanged += (s, e) => { HighlightSelected(); ShowDetail(); if (autoJump.Checked) JumpToSelected(false); };
            list.DoubleClick += (s, e) => JumpToSelected(true);
            var detailCard = Ui.Card("涉及的孔", 140, "");
            detailCard.Fill = true;
            detail.Dock = DockStyle.Fill; detail.Multiline = true; detail.ReadOnly = true; detail.BorderStyle = BorderStyle.None;
            detail.BackColor = Ui.Rail; detail.ForeColor = Ui.Text; detail.ScrollBars = ScrollBars.Vertical; detail.Font = Ui.F9;
            detailCard.Controls.Add(detail);
            stack.Add(summary); stack.Add(list, true); stack.Add(detailCard);

            // ---- 底部操作栏 ----
            autoJump.Font = Ui.F9; autoJump.BackColor = Ui.Bg;
            jump.Click += (s, e) => JumpToSelected(true);
            goDrill.Click += (s, e) => GoDrillSelected();
            goDrill.Enabled = false; jump.Enabled = false;
            tips.SetToolTip(jump, "把视图镜头拉到这一条涉及的孔上（双击列表也行）。");
            tips.SetToolTip(goDrill, "漏打孔：打开「自动打孔」，这几个孔直接带进去当参考孔，再点没打孔那个零件上的面就能配做。");
            shell.Footer.AddLeft(autoJump);
            shell.Footer.AddRight(jump); shell.Footer.AddRight(goDrill);
            shell.Footer.Status = status;
            status.ForeColor = Ui.Muted;
            status.Text = "就绪。点「开始检查」（Ctrl+Enter）。";
            status.TextChanged += (s, e) => tips.SetToolTip(status, status.Text);
        }

        void DrawIssue(object sender, DrawItemEventArgs e){
            var g = e.Graphics;
            using (var b = new SolidBrush(Color.White)) g.FillRectangle(b, e.Bounds);
            if (shown.Count == 0) {
                // 空状态：一句居中的话，不是一条"假的结果"
                string text = !checkedOnce ? "点左边「开始检查」扫一遍装配里的孔。"
                            : (issues.Count == 0 ? "没有发现问题。" : "当前筛选下没有问题（在左边勾选要看哪几类）。");
                TextRenderer.DrawText(g, text, Ui.F9, e.Bounds, Ui.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }
            if (e.Index < 0 || e.Index >= shown.Count) return;
            var it = shown[e.Index];
            bool sel = (e.State & DrawItemState.Selected) != 0;
            var r = new Rectangle(e.Bounds.X, e.Bounds.Y + 3, e.Bounds.Width - 1, e.Bounds.Height - 7);
            Ui.FillRound(g, r, 10, sel ? Ui.AccentSoft : Color.White);
            Ui.DrawRound(g, r, 10, sel ? Ui.Accent : Ui.Line, sel ? 1.5f : 1f);
            Color c = ColorFor(it.Kind);
            bool done = it.Done.Length > 0;
            string tagText = done ? "已配" : it.KindName;
            int tw = TextRenderer.MeasureText(tagText, Ui.F8).Width + 16;
            var tag = new Rectangle(r.X + 14, r.Y + 11, tw, 24);
            Ui.FillRound(g, tag, 6, done ? Ui.OkSoft : SoftFor(it.Kind));
            TextRenderer.DrawText(g, tagText, Ui.F8, tag, done ? Ui.Ok : c, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            int x = tag.Right + 12, w = Math.Max(10, r.Right - x - 12);
            TextRenderer.DrawText(g, it.Message, Ui.F9, new Rectangle(x, r.Y + 10, w, 22), Ui.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            var parts = it.Holes.Select(h => HoleCheck.ShortPart(h.PartName)).Distinct().ToArray();
            TextRenderer.DrawText(g, it.Holes.Count + " 个孔 · " + parts.Length + " 个零件：" + string.Join("、", parts), Ui.F8,
                new Rectangle(x, r.Y + 34, w, 18), Ui.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }

        static Color SoftFor(HoleIssueKind k){
            switch (k) {
                case HoleIssueKind.MissingHole: return Ui.BadSoft;
                case HoleIssueKind.Misaligned: return Ui.WarnSoft;
                case HoleIssueKind.WrongSpec: return Ui.AccentSoft;
                default: return Ui.Subtle;
            }
        }

        static Color ColorFor(HoleIssueKind k){
            switch (k) {
                case HoleIssueKind.MissingHole: return Ui.Bad;
                case HoleIssueKind.Misaligned: return Ui.Warn;
                case HoleIssueKind.WrongSpec: return Ui.Accent;
                default: return Ui.Muted;
            }
        }

        // ---------------- 检查 ----------------
        void RunCheck(){
            if (busy) return;                       // DoEvents 期间可能被 Ctrl+Enter 再进来
            busy = true;
            try {
                if (assembly == null) throw new InvalidOperationException("请先打开装配文件。");
                // 窗口开着时装配被关掉（或关了又打开）：原来往下走报一句看不懂的"对象已断开"
                try { var nm = assembly.Name; } catch { throw new InvalidOperationException("打开这个窗口时的装配已经关掉了。请关掉这个窗口，在装配里重新点「配孔检查」。"); }
                run.Enabled = false; run.Text = "检查中…"; UseWaitCursor = true;
                SetStatus("正在读取装配里的孔…", Ui.Accent);
                Application.DoEvents();

                // 一次性建好「零件名 -> 实体」表：漏打孔判定要按名字查几百次零件，
                // 每次重新遍历 Occurrences 读 occ.Name 都是跨进程 COM 调用（O(S×P²)）。
                // 零件实例只遍历一次（含子装配里的，见 AssemblyPart）：孔、射线表、零件名都从这一份来
                var warnings = new List<string>();
                List<HoleRecord> shafts;
                List<AssemblyPart> parts;
                // 大装配要读一分多钟：期间隔一会儿刷一下状态栏、泵一次消息（不然 Windows 把天工判成"未响应"，用户以为卡死去强关）
                var tick = System.Diagnostics.Stopwatch.StartNew();
                Action<string, int, int> pulse = (what, i, n) => {
                    if (tick.ElapsedMilliseconds < 300) return;
                    tick.Restart();
                    SetStatus(what + " " + i + " / " + n + " …", Ui.Accent);
                    try { status.Update(); Application.DoEvents(); } catch { }
                };
                var holes = AutoHoleWriter.CollectAssemblyHoles(assembly, out warnings, out shafts, out parts, (i, n) => pulse("正在读取零件", i, n));
                var bodies = AutoHoleWriter.OccurrenceBodies.Build(parts);
                var allNames = new List<string>();
                foreach (var ap in parts) if (!ap.Ignored && !allNames.Contains(ap.Name)) allNames.Add(ap.Name);   // 隐藏 / 参考件不当漏打孔的零件
                int ignored = parts.Count(x => x.Ignored);
                int nested = parts.Count(x => x.Nested);
                lastHoles = holes; lastShafts = shafts;
                SetStatus("读到 " + holes.Count + " 个孔，正在配对…", Ui.Accent); Application.DoEvents();
                // 孤孔一律算出来，显示与否交给筛选胶囊（不用为了看孤孔再检查一遍）
                var found = HoleCheck.Run(holes, null, true, allNames, bodies, shafts, (i, n) => pulse("正在配对同轴孔", i, n));
                MarkFailedFeatures(found, parts);
                ShowIssues(HoleCheck.Merge(found), HoleCheck.Relevant(holes).Count, HoleCheck.CountGroups(holes));
                failedParts = warnings.Count; lastWarnings = warnings;
                string s = summary.Text;
                if (nested > 0) s += "　（含子装配里的 " + nested + " 个零件）";
                if (ignored > 0) s += "　（" + ignored + " 个隐藏 / 参考零件不查漏孔）";
                if (warnings.Count > 0) s += "　（" + warnings.Count + " 个零件读取失败）";
                SetStatus(s, issues.Count(i => i.Kind != HoleIssueKind.Unpaired) == 0 ? Ui.Ok : Ui.Warn);
                // 读取失败的是哪几个零件：原来只说"N 个零件读取失败"，不知道是哪几个、漏查了谁
                if (warnings.Count > 0) tips.SetToolTip(status, s + "\r\n\r\n读取失败的零件（这些零件上的孔没查）：\r\n· " + string.Join("\r\n· ", warnings.Take(20).ToArray()) + (warnings.Count > 20 ? "\r\n……共 " + warnings.Count + " 个" : ""));
            } catch (Exception e) {
                Log.Write("AutoHoleCheck", e);
                SetStatus(AutoHoleWriter.Friendly(e), Ui.Bad);
                MessageBox.Show(this, AutoHoleWriter.Friendly(e), "配孔检查", MessageBoxButtons.OK, MessageBoxIcon.Error);
            } finally {
                busy = false; run.Enabled = true; run.Text = checkedOnce ? "重新检查" : "开始检查"; UseWaitCursor = false;
            }
        }

        // 被报"漏打孔"的零件里有出错的孔特征：多半孔本来打了、只是没生成出来（参考零件挪了、链接断了……），先说清楚
        public static void MarkFailedFeatures(List<HoleIssue> found, List<AssemblyPart> parts){
            var bad = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var it in found) {
                if (it.Kind != HoleIssueKind.MissingHole || string.IsNullOrEmpty(it.MissingPart)) continue;
                int n;
                if (!bad.TryGetValue(it.MissingPart, out n)) {
                    var ap = parts.FirstOrDefault(p => p.Name == it.MissingPart);
                    n = ap == null ? 0 : PartBodies.FailedHoles(ap.Part);
                    bad[it.MissingPart] = n;
                }
                if (n > 0) it.Message += "　⚠ 这个零件有 " + n + " 个孔特征出错（特征树里标红，孔没生成出来），先在天工里修好它们再看这条";
            }
        }

        void ShowIssues(List<HoleIssue> found, int holes, int groups){
            issues = found ?? new List<HoleIssue>(); holeCount = holes; groupCount = groups; checkedOnce = true;
            ApplyFilter();
            copy.Enabled = issues.Count > 0;
        }

        // 胶囊上的数是"一共找到几条"（不受筛选影响）；列表只显示勾着的几类
        void ApplyFilter(){
            shown = issues.Where(i =>
                (i.Kind == HoleIssueKind.MissingHole && cMissing.Checked) ||
                (i.Kind == HoleIssueKind.Misaligned && cMis.Checked) ||
                (i.Kind == HoleIssueKind.WrongSpec && cWrong.Checked) ||
                (i.Kind == HoleIssueKind.Unpaired && cUnpaired.Checked)).ToList();
            list.BeginUpdate();
            try {
                list.Items.Clear();
                foreach (var i in shown) list.Items.Add(i.Message);
                if (shown.Count == 0) list.Items.Add("");   // 空状态那一行（DrawIssue 画成居中提示，不能选）
            } finally { list.EndUpdate(); }
            list.Enabled = shown.Count > 0;
            cMissing.Text = "漏打孔 " + issues.Count(i => i.Kind == HoleIssueKind.MissingHole);
            cMis.Text = "孔偏了 " + issues.Count(i => i.Kind == HoleIssueKind.Misaligned);
            cWrong.Text = "配错孔 " + issues.Count(i => i.Kind == HoleIssueKind.WrongSpec);
            cUnpaired.Text = "孤孔 " + issues.Count(i => i.Kind == HoleIssueKind.Unpaired);
            foreach (var c in new[]{ cMissing, cMis, cWrong, cUnpaired }) c.Invalidate();
            if (checkedOnce) {
                // 结论只数"该关心的"三类；孤孔显示出来时才算进去
                var counted = issues.Where(i => i.Kind != HoleIssueKind.Unpaired || cUnpaired.Checked).ToList();
                summary.Text = HoleCheck.Summary(counted, holeCount, groupCount);
                summary.ForeColor = counted.Count == 0 ? Ui.Ok : Ui.Warn;
                shell.SetTitle(counted.Count == 0 ? "没有发现问题" : "发现 " + counted.Sum(i => i.Places) + " 处问题", "点一条在模型里高亮");
                countLine.Text = holeCount + " 个孔 · " + groupCount + " 组同轴孔";
            }
            ShowDetail();
        }

        void ShowDetail(){
            int idx = list.SelectedIndex;
            if (idx < 0 || idx >= shown.Count) {
                jump.Enabled = false; goDrill.Enabled = false;
                detail.Text = shown.Count == 0 ? "" : "在上面选一条，这里列出涉及的每个孔（零件、孔径、装配坐标位置）。";
                detail.ForeColor = Ui.Muted;
                return;
            }
            var it = shown[idx];
            jump.Enabled = it.Holes.Count > 0;
            goDrill.Enabled = it.Kind == HoleIssueKind.MissingHole && GoDrill != null;
            var sb = new StringBuilder();
            sb.Append("【").Append(it.KindName).Append("】").Append(it.Done.Length > 0 ? "（" + it.Done + "）" : "").Append(it.Message).Append("\r\n");
            foreach (var h in it.Holes.OrderBy(x => x.PartName, StringComparer.Ordinal))
                sb.Append("· ").Append(h.PartName).Append("　Φ").Append(h.DiameterMm.ToString("0.##", CultureInfo.InvariantCulture))
                  .Append("　@X").Append((h.Center.X * 1000).ToString("0.#", CultureInfo.InvariantCulture))
                  .Append(" Y").Append((h.Center.Y * 1000).ToString("0.#", CultureInfo.InvariantCulture))
                  .Append(" Z").Append((h.Center.Z * 1000).ToString("0.#", CultureInfo.InvariantCulture)).Append("\r\n");
            detail.Text = sb.ToString();
            detail.ForeColor = Ui.Text;
        }

        void SetStatus(string text, Color color){ status.Text = text; status.ForeColor = color; }

        void CopyResults(){
            try {
                var sb = new StringBuilder();
                sb.AppendLine("配孔检查 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine("装配：" + (assembly != null ? assembly.FullName : ""));
                sb.AppendLine(summary.Text);
                if (lastWarnings.Count > 0) { sb.AppendLine("读取失败的零件（没查）："); foreach (var w in lastWarnings) sb.AppendLine("    " + w); }
                sb.AppendLine();
                foreach (var i in shown) {
                    sb.AppendLine("【" + i.KindName + "】" + i.Message);
                    foreach (var h in i.Holes) sb.AppendLine("    " + h.ToString());
                }
                Clipboard.SetText(sb.ToString());
                SetStatus("已复制当前显示的 " + shown.Count + " 条结果。", Ui.Ok);
            } catch (Exception e) { SetStatus("复制失败：" + e.Message, Ui.Bad); }
        }

        void HighlightSelected(){
            if (assembly == null) return;
            try {
                if (highlights == null) {
                    highlights = assembly.HighlightSets.Add();
                    highlights.Color = ColorTranslator.ToOle(Color.OrangeRed);
                }
                highlights.RemoveAll();
                int idx = list.SelectedIndex;
                if (idx < 0 || idx >= shown.Count) { highlights.Draw(); return; }
                // 合并过的一条可能带几百个孔（同一个外购件用了 64 次 × 3 个孔）：每个孔建高亮引用都是 COM 调用，
                // 全高亮的话每点一下卡好几秒。高亮前 60 个就够看清是哪一处了
                int lit = 0;
                foreach (var h in shown[idx].Holes) { if (lit >= 60) break; if (h.Selection != null) { highlights.AddItem(h.Selection); lit++; } }
                highlights.Draw();
            } catch (Exception e) { Log.Write("AutoHoleCheckHighlight", e); }
        }

        // 跳转到孔：镜头框住这一条涉及的所有孔（只有一个孔时按孔径放大）
        void JumpToSelected(bool explicitly){
            int idx = list.SelectedIndex;
            if (idx < 0 || idx >= shown.Count) return;
            var it = shown[idx];
            if (it.Holes.Count == 0) return;
            // 合并过的一条（共 N 处）：只框第一处附近（50mm 内）的孔 —— 原来框住全部几十处，镜头拉到整个装配，什么也看不清
            var holes = it.Places > 1 ? it.Holes.Where(h => (h.Center - it.Holes[0].Center).Length < 0.05).ToList() : it.Holes;
            bool ok;
            if (holes.Count == 1 || holes.All(h => (h.Center - holes[0].Center).Length < 0.002))
                ok = CadView.ZoomToHole(app, holes[0].Center, holes.Max(h => h.DiameterMm));
            else
                ok = CadView.ZoomToPoints(app, holes.Select(h => h.Center).ToList(), Math.Max(0.01, holes.Max(h => h.DiameterMm) * 3 / 1000.0));
            if (ok) SetStatus("已跳到：" + it.KindName + "　" + HoleCheck.ShortPart(holes[0].PartName) + " Φ" + holes[0].DiameterMm.ToString("0.##", CultureInfo.InvariantCulture) + (it.Places > 1 ? "（第 1 处，共 " + it.Places + " 处，位置见「涉及的孔」）" : ""), Ui.Accent);
            else if (explicitly) SetStatus("没能移动视图（请确认装配窗口是当前窗口）。", Ui.Warn);
        }

        // 去配孔：漏打孔那一条的孔带进「自动打孔」当参考孔
        void GoDrillSelected(){
            int idx = list.SelectedIndex;
            if (idx < 0 || idx >= shown.Count || GoDrill == null) return;
            var it = shown[idx];
            if (it.Kind != HoleIssueKind.MissingHole) { SetStatus("只有「漏打孔」能直接去配孔。", Ui.Warn); return; }
            // 上一条「去配孔」开的打孔窗口还开着：没在打孔就先关掉它（原来一条开一个，窗口越叠越多，抢同一个鼠标选择）
            if (lastDrill != null && !lastDrill.IsDisposed) {
                if (lastDrill.Busy) { SetStatus("上一个「去配孔」的打孔窗口正在打孔，等它打完。", Ui.Warn); return; }
                lastDrill.Close();
                if (!lastDrill.IsDisposed) return;
            }
            try {
                var f = GoDrill(new List<HoleRecord>(it.Holes), HoleCheck.ShortPart(it.MissingPart));
                lastDrill = f;
                // 去配孔打完：这一条标成"已配"（0.8.5）。原来检查窗口里这条一直挂着红的「漏打孔」，配了哪几条、还剩哪几条只能再整装配查一遍
                if (f != null) f.DrillDone += made => {
                    if (made <= 0 || IsDisposed) return;
                    it.Done = "已配 " + made + " 个孔";
                    try { list.Invalidate(); ShowDetail(); SetStatus("「" + HoleCheck.ShortPart(it.MissingPart) + "」上配好了 " + made + " 个孔（这一条已标成已配；要确认可以点「重新检查」）。", Ui.Ok); } catch { }
                };
            }
            catch (Exception e) { Log.Write("AutoHoleCheckGoDrill", e); SetStatus("打不开自动打孔：" + e.Message, Ui.Bad); }
        }

        AutoHoleForm lastDrill;
        void EditOptions(){
            // 检查中（会泵消息）改口径：HoleCheck.Run 一半用旧口径一半用新口径，结果对不上
            if (busy) { SetStatus("正在检查，做完再改判定口径。", Ui.Warn); return; }
            using (var f = new HoleCheckOptionsForm(HoleCheck.Options)) {
                if (f.ShowDialog(this) != DialogResult.OK || f.Result == null) return;
                HoleCheck.Options = f.Result;
            }
            tips.SetToolTip(rules, HoleCheck.Options.Describe());
            SetStatus(checkedOnce ? "判定口径已改，点「重新检查」按新口径再查一遍。" : "判定口径已改。", Ui.Ok);
        }

        void Cleanup(){
            if (highlights != null) { try { highlights.Delete(); } catch { } highlights = null; }
            // 检查窗口关了（点了别的命令）：它开的「去配孔」窗口不归 ToolContext 管，没在打孔就一起关
            if (lastDrill != null && !lastDrill.IsDisposed && !lastDrill.Busy) { try { lastDrill.Close(); } catch { } }
            try { tips.Dispose(); } catch { }
        }

        // ---- 测试 / 截图用的缝：不连 CAD，直接把一批结果灌进界面 ----
        List<HoleRecord> lastHoles = new List<HoleRecord>(), lastShafts = new List<HoleRecord>();
        public void RunCheckForTest(){ RunCheck(); }
        public List<HoleIssue> IssuesForTest { get { return issues; } }
        public List<HoleRecord> HolesForTest { get { return lastHoles; } }
        public List<HoleRecord> ShaftsForTest { get { return lastShafts; } }
        public string StatusForTest { get { return status.Text; } }
        public void ShowIssuesForTest(List<HoleIssue> found, int holes, int groups){ ShowIssues(found, holes, groups); }
        public int ListedCount { get { return shown.Count; } }
        public string SummaryText { get { return summary.Text; } }
        public void SelectForTest(int index){ if (index >= 0 && index < list.Items.Count) list.SelectedIndex = index; }
        public string DetailText { get { return detail.Text; } }
    }
}
