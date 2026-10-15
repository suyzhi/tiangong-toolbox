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
    public sealed class AutoHoleCheckForm : Form {
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
        readonly Button run = Ui.Primary("开始检查", 130, 34);
        readonly Button copy = Ui.Secondary("复制结果", 96, 30);
        readonly Label rules = new Label();
        readonly ToggleChip cMissing = new ToggleChip { Text = "漏打孔 0", ChipColor = Ui.Bad };
        readonly ToggleChip cMis = new ToggleChip { Text = "孔偏了 0", ChipColor = Ui.Warn };
        readonly ToggleChip cWrong = new ToggleChip { Text = "配错孔 0", ChipColor = Ui.Accent };
        readonly ToggleChip cUnpaired = new ToggleChip { Text = "孤孔 0", ChipColor = Ui.Muted };
        readonly ToolTip tips = new ToolTip { AutoPopDelay = 20000, InitialDelay = 300 };
        // 跳转到孔：选中一条就把镜头拉到涉及的孔上（大装配里光高亮看不见几毫米的孔）
        readonly CheckBox autoJump = new CheckBox { Text = "选中即跳转", Checked = true, AutoSize = true };
        readonly Button jump = Ui.Small("跳转到孔");
        readonly Button goDrill = Ui.Small("去配孔");
        // 「去配孔」：打开自动打孔，把这条漏打孔涉及的孔带进去当参考孔（由命令模块接上，见 AutoHoleModule）
        public Action<List<HoleRecord>, string> GoDrill;

        public AutoHoleCheckForm(F.Application application, A.AssemblyDocument document){
            app = application; assembly = document;
            Ui.Shell(this, "配孔检查", 840, 680, 700, 560);
            // 用户改过判定口径就按他的来（读不到就是默认值）
            try { HoleCheck.Options = HoleCheckOptions.Load(SettingsFile.PathOf(HoleCheckOptions.FileName)); }
            catch (Exception e) { Log.Write("HoleCheckOptionsLoad", e); }
            cUnpaired.Checked = false;
            BuildLayout();
            ApplyFilter();
            FormClosed += (s, e) => Cleanup();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData){
            if (keyData == Keys.Escape) { Close(); return true; }
            // RunCheck 内部会 Application.DoEvents()，Ctrl+Enter 能绕开按钮禁用再进来一次，
            // 两次并发扫描会同时读写 issues/list 并对同一批 COM 对象重入 —— 必须挡。
            if (keyData == (Keys.Control | Keys.Enter)) { if (!busy) RunCheck(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ---------------- 布局 ----------------
        // [开始检查] [复制结果]  判定口径ⓘ                    一句话结论
        // ┌ 检查结果 ─────────────── [漏打孔 3][孔偏了 1][配错孔 0][孤孔 12] ┐
        // │ 列表（吃掉剩余高度）                                            │
        // └──────────────────────────────────────────────────────────────────┘
        // ┌ 详情：涉及的孔 ─────────────────────────────────────────────────┐
        // 状态栏
        void BuildLayout(){
            var bar = new Panel { Height = 40, BackColor = Ui.Bg };
            run.SetBounds(0, 2, 130, 34);
            copy.SetBounds(140, 5, 96, 30); copy.Enabled = false;
            rules.Text = "判定口径 ⓘ"; rules.AutoSize = true; rules.ForeColor = Ui.Accent; rules.Cursor = Cursors.Hand;
            rules.Location = new Point(250, 12); rules.BackColor = Ui.Bg;
            tips.SetToolTip(rules, HoleCheck.Options.Describe());
            rules.Click += (s, e) => EditOptions();
            summary.AutoSize = false; summary.TextAlign = ContentAlignment.MiddleRight; summary.AutoEllipsis = true;
            summary.ForeColor = Ui.Muted; summary.BackColor = Ui.Bg; summary.Font = Ui.F9B;
            summary.Text = "还没检查";
            bar.Controls.Add(run); bar.Controls.Add(copy); bar.Controls.Add(rules); bar.Controls.Add(summary);
            bar.Resize += (s, e) => summary.SetBounds(340, 0, Math.Max(40, bar.Width - 340), bar.Height);
            run.Click += (s, e) => RunCheck();
            copy.Click += (s, e) => CopyResults();
            tips.SetToolTip(run, "Ctrl+Enter");

            var listCard = Ui.Card("检查结果", 0, "点一条在模型里高亮");
            foreach (var c in new[]{ cMissing, cMis, cWrong, cUnpaired }) {
                c.Width = 86;
                c.CheckedChanged += (s, e) => ApplyFilter();
                listCard.AddHeader(c);
            }
            tips.SetToolTip(cUnpaired, "只有一个零件有孔、附近也没穿过别的零件。真实装配里大多数孔都是孤孔，默认不显示。");
            list.Dock = DockStyle.Fill; list.IntegralHeight = false; list.BorderStyle = BorderStyle.None;
            list.DrawMode = DrawMode.OwnerDrawFixed; list.ItemHeight = 46; list.BackColor = Color.White;
            list.DrawItem += DrawIssue;
            list.SelectedIndexChanged += (s, e) => { HighlightSelected(); ShowDetail(); if (autoJump.Checked) JumpToSelected(false); };
            list.DoubleClick += (s, e) => JumpToSelected(true);
            listCard.Controls.Add(list);

            var detailCard = Ui.Card("详情", 132, "选中一条，这里列出涉及的每个孔");
            autoJump.Font = Ui.F8; autoJump.BackColor = Color.White; autoJump.Height = 22;
            jump.Click += (s, e) => JumpToSelected(true);
            goDrill.Click += (s, e) => GoDrillSelected();
            goDrill.Enabled = false; jump.Enabled = false;
            tips.SetToolTip(jump, "把视图镜头拉到这一条涉及的孔上（双击列表也行）。");
            tips.SetToolTip(goDrill, "漏打孔：打开「自动打孔」，这几个孔直接带进去当参考孔，再点没打孔那个零件上的面就能配做。");
            detailCard.AddHeader(goDrill); detailCard.AddHeader(jump); detailCard.AddHeader(autoJump);
            detail.Dock = DockStyle.Fill; detail.Multiline = true; detail.ReadOnly = true; detail.BorderStyle = BorderStyle.None;
            detail.BackColor = Color.White; detail.ForeColor = Ui.Text; detail.ScrollBars = ScrollBars.Vertical; detail.Font = Ui.F9;
            detailCard.Controls.Add(detail);

            status.Height = 24; status.Padding = new Padding(2, 0, 10, 0); status.AutoEllipsis = true;
            status.TextAlign = ContentAlignment.MiddleLeft; status.ForeColor = Ui.Muted; status.BackColor = Ui.Bg;
            status.Text = "就绪。点「开始检查」（Ctrl+Enter）。";

            var stack = new VerticalStack { Dock = DockStyle.Fill };
            stack.Add(bar); stack.Add(listCard, true); stack.Add(detailCard); stack.Add(status);
            Controls.Add(stack);
        }

        void DrawIssue(object sender, DrawItemEventArgs e){
            e.DrawBackground();
            var g = e.Graphics;
            using (var b = new SolidBrush(Color.White)) g.FillRectangle(b, e.Bounds);
            if (shown.Count == 0) {
                // 空状态：一句居中的话，不是一条"假的结果"
                string text = !checkedOnce ? "点「开始检查」扫一遍装配里的孔。"
                            : (issues.Count == 0 ? "没有发现问题。" : "当前筛选下没有问题（点上面的胶囊切换显示哪几类）。");
                TextRenderer.DrawText(g, text, Ui.F9, e.Bounds, Ui.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }
            if (e.Index < 0 || e.Index >= shown.Count) return;
            var it = shown[e.Index];
            bool sel = (e.State & DrawItemState.Selected) != 0;
            if (sel) {
                using (var b = new SolidBrush(Ui.ChipBg)) g.FillRectangle(b, e.Bounds);
                using (var b = new SolidBrush(Ui.Accent)) g.FillRectangle(b, e.Bounds.X, e.Bounds.Y + 4, 3, e.Bounds.Height - 8);
            }
            using (var p = new Pen(Ui.Line)) g.DrawLine(p, e.Bounds.Left + 8, e.Bounds.Bottom - 1, e.Bounds.Right - 8, e.Bounds.Bottom - 1);
            Color c = ColorFor(it.Kind);
            var tag = new Rectangle(e.Bounds.X + 12, e.Bounds.Y + 11, 64, 22);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var path = Ui.Rounded(tag, 10)) {
                using (var b = new SolidBrush(Color.FromArgb(26, c))) g.FillPath(b, path);
                using (var p = new Pen(Color.FromArgb(90, c))) g.DrawPath(p, path);
            }
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.Default;
            TextRenderer.DrawText(g, it.KindName, Ui.F8, tag, c, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            int x = tag.Right + 10, w = Math.Max(10, e.Bounds.Right - x - 10);
            TextRenderer.DrawText(g, it.Message, Ui.F9, new Rectangle(x, e.Bounds.Y + 5, w, 20), Ui.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            var parts = it.Holes.Select(h => h.PartName).Distinct().ToArray();
            TextRenderer.DrawText(g, it.Holes.Count + " 个孔 · " + parts.Length + " 个零件：" + string.Join("、", parts), Ui.F8,
                new Rectangle(x, e.Bounds.Y + 26, w, 16), Ui.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
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
                run.Enabled = false; run.Text = "检查中…"; UseWaitCursor = true;
                SetStatus("正在读取装配里的孔…", Ui.Accent);
                Application.DoEvents();

                // 一次性建好「零件名 -> 实体」表：漏打孔判定要按名字查几百次零件，
                // 每次重新遍历 Occurrences 读 occ.Name 都是跨进程 COM 调用（O(S×P²)）。
                // 零件实例只遍历一次（含子装配里的，见 AssemblyPart）：孔、射线表、零件名都从这一份来
                var warnings = new List<string>();
                List<HoleRecord> shafts;
                List<AssemblyPart> parts;
                var holes = AutoHoleWriter.CollectAssemblyHoles(assembly, out warnings, out shafts, out parts);
                var bodies = AutoHoleWriter.OccurrenceBodies.Build(parts);
                var allNames = new List<string>();
                foreach (var ap in parts) if (!allNames.Contains(ap.Name)) allNames.Add(ap.Name);
                int nested = parts.Count(x => x.Nested);
                lastHoles = holes; lastShafts = shafts;
                SetStatus("读到 " + holes.Count + " 个孔，正在配对…", Ui.Accent); Application.DoEvents();
                // 孤孔一律算出来，显示与否交给筛选胶囊（不用为了看孤孔再检查一遍）
                ShowIssues(HoleCheck.Run(holes, null, true, allNames, bodies, shafts), HoleCheck.Relevant(holes).Count, HoleCheck.CountGroups(holes));
                failedParts = warnings.Count;
                string s = summary.Text;
                if (nested > 0) s += "　（含子装配里的 " + nested + " 个零件）";
                if (warnings.Count > 0) s += "　（" + warnings.Count + " 个零件读取失败）";
                SetStatus(s, issues.Count(i => i.Kind != HoleIssueKind.Unpaired) == 0 ? Ui.Ok : Ui.Warn);
            } catch (Exception e) {
                Log.Write("AutoHoleCheck", e);
                SetStatus(e.Message, Ui.Bad);
                MessageBox.Show(this, e.Message, "配孔检查", MessageBoxButtons.OK, MessageBoxIcon.Error);
            } finally {
                busy = false; run.Enabled = true; run.Text = checkedOnce ? "重新检查" : "开始检查"; UseWaitCursor = false;
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
            sb.Append("【").Append(it.KindName).Append("】").Append(it.Message).Append("\r\n");
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
                foreach (var h in shown[idx].Holes) if (h.Selection != null) highlights.AddItem(h.Selection);
                highlights.Draw();
            } catch (Exception e) { Log.Write("AutoHoleCheckHighlight", e); }
        }

        // 跳转到孔：镜头框住这一条涉及的所有孔（只有一个孔时按孔径放大）
        void JumpToSelected(bool explicitly){
            int idx = list.SelectedIndex;
            if (idx < 0 || idx >= shown.Count) return;
            var it = shown[idx];
            if (it.Holes.Count == 0) return;
            bool ok;
            if (it.Holes.Count == 1 || it.Holes.All(h => (h.Center - it.Holes[0].Center).Length < 0.002))
                ok = CadView.ZoomToHole(app, it.Holes[0].Center, it.Holes.Max(h => h.DiameterMm));
            else
                ok = CadView.ZoomToPoints(app, it.Holes.Select(h => h.Center).ToList(), Math.Max(0.01, it.Holes.Max(h => h.DiameterMm) * 3 / 1000.0));
            if (ok) SetStatus("已跳到：" + it.KindName + "　" + it.Holes[0].PartName + " Φ" + it.Holes[0].DiameterMm.ToString("0.##", CultureInfo.InvariantCulture), Ui.Accent);
            else if (explicitly) SetStatus("没能移动视图（请确认装配窗口是当前窗口）。", Ui.Warn);
        }

        // 去配孔：漏打孔那一条的孔带进「自动打孔」当参考孔
        void GoDrillSelected(){
            int idx = list.SelectedIndex;
            if (idx < 0 || idx >= shown.Count || GoDrill == null) return;
            var it = shown[idx];
            if (it.Kind != HoleIssueKind.MissingHole) { SetStatus("只有「漏打孔」能直接去配孔。", Ui.Warn); return; }
            try { GoDrill(new List<HoleRecord>(it.Holes), it.MissingPart); }
            catch (Exception e) { Log.Write("AutoHoleCheckGoDrill", e); SetStatus("打不开自动打孔：" + e.Message, Ui.Bad); }
        }

        void EditOptions(){
            using (var f = new HoleCheckOptionsForm(HoleCheck.Options)) {
                if (f.ShowDialog(this) != DialogResult.OK || f.Result == null) return;
                HoleCheck.Options = f.Result;
            }
            tips.SetToolTip(rules, HoleCheck.Options.Describe());
            SetStatus(checkedOnce ? "判定口径已改，点「重新检查」按新口径再查一遍。" : "判定口径已改。", Ui.Ok);
        }

        void Cleanup(){
            if (highlights != null) { try { highlights.Delete(); } catch { } highlights = null; }
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
