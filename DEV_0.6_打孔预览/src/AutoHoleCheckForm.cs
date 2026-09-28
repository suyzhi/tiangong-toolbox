using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using A=SolidEdgeAssembly;
using F=SolidEdgeFramework;

namespace TianGongCadSuite {
    // 命令 8：配孔检查。扫一遍装配里所有零件的孔，报漏打孔 / 孔偏了 / 配错孔。
    public sealed class AutoHoleCheckForm : Form {
        readonly F.Application app;
        readonly A.AssemblyDocument assembly;
        F.HighlightSet highlights;
        List<HoleIssue> issues = new List<HoleIssue>();
        List<HoleIssue> shown = new List<HoleIssue>();
        int holeCount, groupCount, failedParts;
        bool busy;                    // 扫描中：RunCheck 里有 DoEvents，必须挡住重入
        readonly Label status = new Label();
        readonly ListBox list = new ListBox();
        readonly Button run = Ui.Primary("开始检查", 140, 34);
        readonly Button copy = Ui.Secondary("复制结果", 96, 30);
        readonly CheckBox ignoreSingle = new CheckBox { Text = "忽略孤孔（只有一个零件有孔、轴线也没穿过别的零件）" };
        readonly CheckBox fMissing = new CheckBox { Text = "漏打孔" };
        readonly CheckBox fMis = new CheckBox { Text = "孔偏了" };
        readonly CheckBox fWrong = new CheckBox { Text = "配错孔" };
        readonly Chip cMissing = new Chip(), cMis = new Chip(), cWrong = new Chip(), cUnpaired = new Chip();

        public AutoHoleCheckForm(F.Application application, A.AssemblyDocument document){
            app = application; assembly = document;
            Ui.Shell(this, "配孔检查", 740, 640, 660, 560);
            BuildLayout();
            FormClosed += (s, e) => Cleanup();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData){
            if (keyData == Keys.Escape) { Close(); return true; }
            // RunCheck 内部会 Application.DoEvents()，Ctrl+Enter 能绕开按钮禁用再进来一次，
            // 两次并发扫描会同时读写 issues/list 并对同一批 COM 对象重入 —— 必须挡。
            if (keyData == (Keys.Control | Keys.Enter)) { if (!busy) RunCheck(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        void BuildLayout(){
            var head = Ui.Card("检查什么", 84, "");
            var t = new Label {
                Dock = DockStyle.Fill, AutoSize = false, ForeColor = Ui.Text,
                Text = "把装配里所有零件的孔读一遍：同一轴线上，一边有孔另一边没有 = 漏打孔；\r\n" +
                       "两件孔不同轴 = 孔偏了；两边孔径配不上同一个规格 = 配错孔。"
            };
            head.Controls.Add(t);

            var bar = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = Ui.Bg };
            run.SetBounds(0, 6, 140, 34);
            run.Click += (s, e) => RunCheck();
            ignoreSingle.SetBounds(152, 12, 330, 22); ignoreSingle.Checked = true;
            ignoreSingle.CheckedChanged += (s, e) => { if (issues.Count > 0) ApplyFilter(); };
            bar.Controls.Add(run); bar.Controls.Add(ignoreSingle);

            var stats = new Panel { Dock = DockStyle.Top, Height = 34, BackColor = Ui.Bg };
            int x = 0;
            foreach (var pair in new[]{ new object[]{ cMissing, "漏打孔" }, new object[]{ cMis, "孔偏了" }, new object[]{ cWrong, "配错孔" }, new object[]{ cUnpaired, "孤孔" } }) {
                var chip = (Chip)pair[0]; chip.Text = (string)pair[1] + " 0"; chip.ChipColor = Ui.Muted;
                chip.SetBounds(x, 4, 118, 24); stats.Controls.Add(chip); x += 126;
            }

            var filters = new Panel { Dock = DockStyle.Top, Height = 30, BackColor = Ui.Bg };
            var fl = new Label {
                Text = "只看（孤孔由上面的「忽略孤孔」控制）：", AutoSize = true,
                Location = new Point(0, 7), ForeColor = Ui.Muted
            };
            filters.Controls.Add(fl);
            int fx = 264;   // 让开上面那句更长的说明文字
            foreach (var cb in new[]{ fMissing, fMis, fWrong }) {
                cb.AutoSize = true; cb.Checked = true; cb.SetBounds(fx, 6, 90, 22);
                cb.CheckedChanged += (s, e) => ApplyFilter();
                filters.Controls.Add(cb); fx += 96;
            }
            fMissing.Checked = fMis.Checked = fWrong.Checked = true;

            var listCard = Ui.Card("检查结果", 300, "点一条就会在模型里高亮对应的孔");
            list.Dock = DockStyle.Fill; list.IntegralHeight = false; list.BorderStyle = BorderStyle.FixedSingle;
            list.DrawMode = DrawMode.OwnerDrawFixed; list.ItemHeight = 42; list.BackColor = Color.White;
            list.DrawItem += DrawIssue;
            list.SelectedIndexChanged += (s, e) => HighlightSelected();
            listCard.Controls.Add(list);

            var actions = new Panel { Height = 46, BackColor = Ui.Bg };
            copy.SetBounds(0, 8, 96, 30); copy.Enabled = false;
            copy.Click += (s, e) => CopyResults();
            actions.Controls.Add(copy);

            status.Height = 28; status.Padding = new Padding(2, 0, 10, 0);
            status.TextAlign = ContentAlignment.MiddleLeft; status.ForeColor = Ui.Muted; status.BackColor = Ui.Bg;
            status.Text = "就绪。点「开始检查」。";

            var stack = new VerticalStack { Dock = DockStyle.Fill };
            stack.Add(head); stack.Add(bar); stack.Add(stats); stack.Add(filters);
            stack.Add(listCard, true);
            stack.Add(actions); stack.Add(status);
            Controls.Add(stack);
        }

        void DrawIssue(object sender, DrawItemEventArgs e){
            e.DrawBackground();
            if (e.Index < 0 || e.Index >= shown.Count) return;
            var it = shown[e.Index];
            bool sel = (e.State & DrawItemState.Selected) != 0;
            using (var b = new SolidBrush(sel ? Ui.ChipBg : Color.White)) e.Graphics.FillRectangle(b, e.Bounds);
            using (var p = new Pen(Ui.Line)) e.Graphics.DrawLine(p, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            Color c = ColorFor(it.Kind);
            var tag = new Rectangle(e.Bounds.X + 8, e.Bounds.Y + 9, 74, 24);
            using (var path = Ui.Rounded(tag, 5)) {
                using (var b = new SolidBrush(Color.FromArgb(26, c))) e.Graphics.FillPath(b, path);
                using (var p = new Pen(Color.FromArgb(90, c))) e.Graphics.DrawPath(p, path);
            }
            TextRenderer.DrawText(e.Graphics, it.KindName, Ui.F9B, tag, c,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            var r1 = new Rectangle(tag.Right + 10, e.Bounds.Y + 5, e.Bounds.Width - tag.Width - 30, 20);
            TextRenderer.DrawText(e.Graphics, it.Message, Ui.F9, r1, Ui.Text, TextFormatFlags.EndEllipsis);
            var r2 = new Rectangle(tag.Right + 10, e.Bounds.Y + 24, e.Bounds.Width - tag.Width - 30, 16);
            var parts = it.Holes.Select(h => h.PartName).Distinct().ToArray();
            TextRenderer.DrawText(e.Graphics, "涉及 " + it.Holes.Count + " 个孔 / " + parts.Length + " 个零件：" + string.Join("、", parts), Ui.F8, r2, Ui.Muted, TextFormatFlags.EndEllipsis);
        }

        static Color ColorFor(HoleIssueKind k){
            switch (k) {
                case HoleIssueKind.MissingHole: return Ui.Bad;
                case HoleIssueKind.Misaligned: return Ui.Warn;
                case HoleIssueKind.WrongSpec: return Ui.Accent;
                default: return Ui.Muted;
            }
        }

        void RunCheck(){
            if (busy) return;                       // DoEvents 期间可能被 Ctrl+Enter 再进来
            busy = true;
            try {
                if (assembly == null) throw new InvalidOperationException("请先打开装配文件。");
                run.Enabled = false; run.Text = "检查中…"; UseWaitCursor = true;
                status.Text = "正在读取装配里的孔…"; status.ForeColor = Ui.Accent;
                Application.DoEvents();

                // 一次性建好「零件名 -> 实体」表：漏打孔判定要按名字查几百次零件，
                // 每次重新遍历 Occurrences 读 occ.Name 都是跨进程 COM 调用（O(S×P²)）。
                var partNames = new List<string>();
                foreach (A.Occurrence occ in assembly.Occurrences) { try { partNames.Add(occ.Name); } catch { } }
                var bodies = AutoHoleWriter.OccurrenceBodies.Build(assembly, partNames);
                var allNames = new List<string>();
                foreach (var n in partNames) if (!allNames.Contains(n)) allNames.Add(n);

                var warnings = new List<string>();
                var holes = AutoHoleWriter.CollectAssemblyHoles(assembly, out warnings);
                status.Text = "读到 " + holes.Count + " 个孔，正在配对…"; Application.DoEvents();
                var groups = HoleCheck.GroupByAxis(holes);
                var found = HoleCheck.Run(holes, null, !ignoreSingle.Checked, allNames, bodies);
                issues = found; holeCount = holes.Count; groupCount = groups.Count; failedParts = warnings.Count;
                ApplyFilter();
                string s = HoleCheck.Summary(issues, holes.Count, groups.Count);
                if (warnings.Count > 0) s += "　（" + warnings.Count + " 个零件读取失败）";
                if (ignoreSingle.Checked) s += "　（已忽略孤孔）";
                status.Text = s;
                status.ForeColor = issues.Count == 0 ? Ui.Ok : Ui.Warn;
                run.Enabled = true; run.Text = "重新检查"; copy.Enabled = issues.Count > 0;
                UseWaitCursor = false;
            } catch (Exception e) {
                Log.Write("AutoHoleCheck", e);
                run.Enabled = true; run.Text = "开始检查"; UseWaitCursor = false;
                status.Text = e.Message; status.ForeColor = Ui.Bad;
                MessageBox.Show(this, e.Message, "配孔检查", MessageBoxButtons.OK, MessageBoxIcon.Error);
            } finally { busy = false; }
        }

        void ApplyFilter(){
            // 孤孔没有自己的"只看"复选框（它只受上面那个「忽略孤孔」控制），
            // 但列表/色块/计数必须跟那个开关一致：勾了忽略就不该还显示孤孔。
            bool showUnpaired = !ignoreSingle.Checked;
            shown = issues.Where(i =>
                (i.Kind == HoleIssueKind.MissingHole && fMissing.Checked) ||
                (i.Kind == HoleIssueKind.Misaligned && fMis.Checked) ||
                (i.Kind == HoleIssueKind.WrongSpec && fWrong.Checked) ||
                (i.Kind == HoleIssueKind.Unpaired && showUnpaired)).ToList();
            list.Items.Clear();
            foreach (var i in shown) list.Items.Add(i.Message);
            if (shown.Count == 0) list.Items.Add(issues.Count == 0 ? "（没有发现问题）" : "（当前筛选下没有问题）");
            int miss = issues.Count(i => i.Kind == HoleIssueKind.MissingHole);
            int mis = issues.Count(i => i.Kind == HoleIssueKind.Misaligned);
            int wrong = issues.Count(i => i.Kind == HoleIssueKind.WrongSpec);
            int unp = showUnpaired ? issues.Count(i => i.Kind == HoleIssueKind.Unpaired) : 0;
            SetChip(cMissing, "漏打孔", miss, Ui.Bad);
            SetChip(cMis, "孔偏了", mis, Ui.Warn);
            SetChip(cWrong, "配错孔", wrong, Ui.Accent);
            SetChip(cUnpaired, "孤孔", unp, Ui.Muted);
        }

        static void SetChip(Chip c, string name, int n, Color color){
            c.Text = name + " " + n;
            c.ChipColor = n > 0 ? color : Ui.Muted;
            c.Invalidate();
        }

        void CopyResults(){
            try {
                var sb = new StringBuilder();
                sb.AppendLine("配孔检查 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine("装配：" + (assembly != null ? assembly.FullName : ""));
                sb.AppendLine(HoleCheck.Summary(issues, holeCount, groupCount));
                sb.AppendLine();
                foreach (var i in issues) {
                    sb.AppendLine("【" + i.KindName + "】" + i.Message);
                    foreach (var h in i.Holes) sb.AppendLine("    " + h.ToString());
                }
                Clipboard.SetText(sb.ToString());
                status.Text = "结果已复制到剪贴板。"; status.ForeColor = Ui.Ok;
            } catch (Exception e) { status.Text = "复制失败：" + e.Message; status.ForeColor = Ui.Bad; }
        }

        void HighlightSelected(){
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

        void Cleanup(){
            if (highlights != null) { try { highlights.Delete(); } catch { } highlights = null; }
        }
    }
}
