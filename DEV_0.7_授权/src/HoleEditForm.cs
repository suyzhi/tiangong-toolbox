using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using A=SolidEdgeAssembly;
using F=SolidEdgeFramework;
using P=SolidEdgePart;

namespace TianGongCadSuite {
    // 命令 9：改孔型。打错了孔型、后来要换规格，不用删了重打：
    //   ① 在模型里点孔（孔口圆边 / 孔内壁，可以连续点好几个；点到阵列里的孔 = 改它的种子孔，整排跟着变）
    //   ② 选新的孔型/规格 → ③「应用」。默认整组一起改（自动打孔、批量排孔打的同规格孔是一个特征组）。
    // 「自动打孔」「批量排孔」打完以后点「改孔型…」会把刚打的那批孔直接带进来。
    // 换孔型的做法和限制见 HoleEditCad（真机实测）；改错了点「撤销修改」恢复原孔型和原名字。
    public sealed class HoleEditForm : Form, IBusyTool, F.ISEMouseEvents, F.ISECommandEvents {
        // 处理中不让关（Esc、关闭按钮、标题栏 ×、CAD 里按 Esc / 右键都会走到这里）：见 IBusyTool
        public bool Busy { get { return busy; } }
        public string BusyWhat { get { return "改孔型"; } }
        protected override void OnFormClosing(FormClosingEventArgs e){
            if (busy && e.CloseReason == CloseReason.UserClosing) {
                e.Cancel = true;
                try { status.Text = "正在改孔型，做完再关。"; status.ForeColor = Ui.Warn; } catch { }
                return;
            }
            base.OnFormClosing(e);
        }

        readonly F.Application app;
        readonly A.AssemblyDocument assembly;
        F.ISECommand command; F.ISEMouse mouse; Connection mouseConnection, commandConnection;
        F.HighlightSet highlights;
        readonly List<object> picked = new List<object>();
        List<EditableHole> targets = new List<EditableHole>();
        HoleEditUndo lastUndo;
        bool busy, closing, loading;

        readonly ListBox list = new ListBox();
        readonly CheckBox wholeGroup = new CheckBox { Text = "整组一起改（推荐：自动打孔、批量排孔打的同规格孔是一个组）", AutoSize = true, Checked = true };
        readonly Segmented kindSeg = new Segmented("通孔", "螺纹孔", "圆柱沉孔", "锥形沉孔");
        readonly ComboBox sizeBox = Ui.Combo(84, HoleMatcher.Table.Select(r => r.Size).ToArray());
        readonly NumericUpDown diaNum = Ui.Num(0.1M, 500M, 6.6M, 3, 0.1M, 72);
        readonly NumericUpDown depthNum = Ui.Num(0.1M, 2000M, 10M, 2, 1M, 72);
        readonly RadioButton throughRadio = new RadioButton { Text = "贯通", AutoSize = true, Checked = true };
        readonly RadioButton blindRadio = new RadioButton { Text = "盲孔", AutoSize = true };
        readonly RadioButton threadFullRadio = new RadioButton { Text = "全长", AutoSize = true, Checked = true };
        readonly RadioButton threadPartRadio = new RadioButton { Text = "指定深度", AutoSize = true };
        readonly NumericUpDown threadNum = Ui.Num(0.5M, 2000M, 12M, 2, 1M, 72);
        readonly FieldGrid specGrid = new FieldGrid();
        FieldGrid.Row threadRow;
        readonly Label stdInfo = new Label();
        readonly Label preview = new Label();
        readonly ResultList results = new ResultList();
        Card resultCard;
        VerticalStack rootStack;
        readonly Button apply = Ui.Primary("应用", 130, 36);
        readonly Button undoButton = Ui.Secondary("撤销修改", 96, 30);
        readonly Label status = new Label();
        readonly ToolTip tips = new ToolTip { AutoPopDelay = 15000, InitialDelay = 400 };

        public HoleEditForm(F.Application application, A.AssemblyDocument document){
            app = application; assembly = document;
            Ui.Shell(this, "改孔型", 640, 720, 560, 620);
            BuildLayout();
            WireEvents();
            loading = true;
            try { sizeBox.SelectedItem = "M6"; } finally { loading = false; }
            ApplySize();
            FormClosed += (s, e) => Cleanup();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData){
            if (keyData == Keys.Escape) { Close(); return true; }
            if (keyData == (Keys.Control | Keys.Enter)) { Apply(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ---------------- 布局 ----------------
        // ① 要改的孔（吃掉剩余高度）→ ② 改成 → 结果 → 按钮 → 状态栏
        void BuildLayout(){
            var holeCard = Ui.Card("① 要改的孔", 0, "在模型里点孔口圆边或孔内壁，可以连续点");
            var remove = Ui.Small("移除"); var clear = Ui.Small("清空");
            remove.Click += (s, e) => RemoveSelected();
            clear.Click += (s, e) => { if (busy) return; targets.Clear(); picked.Clear(); RefreshAll(); SetStatus("已清空。重新点要改的孔。", Ui.Accent); };
            holeCard.AddHeader(clear); holeCard.AddHeader(remove);
            var hb = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
            list.Dock = DockStyle.Fill; list.BorderStyle = BorderStyle.None; list.IntegralHeight = false; list.Font = Ui.F9;
            list.HorizontalScrollbar = true;
            list.SelectedIndexChanged += (s, e) => JumpToSelected();
            wholeGroup.Dock = DockStyle.Bottom; wholeGroup.Font = Ui.F9; wholeGroup.BackColor = Color.White;
            tips.SetToolTip(wholeGroup, "勾着：点中一个孔，它所在特征组里的孔全部一起改（组名、孔名按新规格重起）。\r\n不勾：只改点中的那个孔特征（阵列里的孔仍然整排跟着种子孔变）。");
            hb.Controls.Add(list); hb.Controls.Add(wholeGroup);
            holeCard.Controls.Add(hb);

            var specCard = Ui.Card("② 改成", 34 + 4 * 32 + 22 + 10, "规格按天工孔标准表；改了孔径就是非标孔");
            var sb = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
            specCard.Controls.Add(sb);
            sb.Controls.Add(specGrid);
            stdInfo.AutoSize = false; stdInfo.Font = Ui.F8; stdInfo.ForeColor = Ui.Muted; stdInfo.AutoEllipsis = true; stdInfo.BackColor = Color.White;
            sb.Controls.Add(stdInfo);
            kindSeg.Width = Math.Max(300, kindSeg.PreferredWidth);
            specGrid.Add("孔型", kindSeg);
            specGrid.Add("规格", sizeBox, FieldGrid.Space(10), Ui.Caption("孔径"), diaNum, FieldGrid.Unit("mm"));
            specGrid.Add("深度", throughRadio, blindRadio, depthNum, FieldGrid.Unit("mm"));
            threadRow = specGrid.Add("螺纹", threadFullRadio, threadPartRadio, threadNum, FieldGrid.Unit("mm"));
            tips.SetToolTip(blindRadio, "贯通孔不能直接改成盲孔（天工接口限制，实测会让孔失效）；盲孔可以改深度、也可以改成贯通。");
            sb.Resize += (s, e) => {
                int w = Math.Max(80, sb.ClientSize.Width);
                specGrid.SetBounds(0, 0, w, specGrid.PreferredHeight);
                stdInfo.SetBounds(0, specGrid.PreferredHeight + 2, w, 18);
            };

            preview.Height = 40; preview.AutoSize = false; preview.Font = Ui.F9B; preview.AutoEllipsis = true; preview.BackColor = Ui.Bg;
            preview.Padding = new Padding(2, 2, 10, 0);

            resultCard = Ui.Card("本次结果", 150, "点一条跳到那个孔");
            var hideResult = Ui.Small("收起");
            hideResult.Click += (s, e) => { resultCard.Visible = false; rootStack.Relayout(); };
            resultCard.AddHeader(hideResult);
            results.Dock = DockStyle.Fill;
            results.Locate += it => { if (it.HasWhere && CadView.ZoomToHole(app, it.Where, it.DiameterMm)) SetStatus("已跳到：" + it.Text, Ui.Accent); };
            resultCard.Controls.Add(results);
            resultCard.Visible = false;

            var actions = new Panel { Height = 40, BackColor = Ui.Bg };
            apply.SetBounds(0, 2, 130, 36);
            var close = Ui.Secondary("关闭", 80, 30);
            close.Click += (s, e) => Close();
            apply.Click += (s, e) => Apply();
            undoButton.Enabled = false;
            undoButton.Click += (s, e) => UndoLast();
            tips.SetToolTip(apply, "Ctrl+Enter"); tips.SetToolTip(close, "Esc");
            tips.SetToolTip(undoButton, "把上一次「应用」改过的孔恢复成原来的孔型和名字。");
            actions.Controls.Add(apply); actions.Controls.Add(undoButton); actions.Controls.Add(close);
            actions.Resize += (s, e) => {
                close.SetBounds(actions.Width - 80, 5, 80, 30);
                undoButton.SetBounds(close.Left - 8 - 96, 5, 96, 30);
            };

            status.Height = 24; status.Padding = new Padding(2, 0, 10, 0); status.AutoEllipsis = true;
            status.TextAlign = ContentAlignment.MiddleLeft; status.ForeColor = Ui.Accent; status.BackColor = Ui.Bg;
            status.Text = "在模型里点要改的孔（孔口圆边或孔内壁）。";

            var stack = rootStack = new VerticalStack { Dock = DockStyle.Fill };
            stack.Add(holeCard, true); stack.Add(specCard); stack.Add(preview); stack.Add(resultCard); stack.Add(actions); stack.Add(status);
            Controls.Add(stack);
        }

        void WireEvents(){
            kindSeg.SelectedIndexChanged += (s, e) => { if (!loading) ApplySize(); };
            sizeBox.SelectedIndexChanged += (s, e) => { if (!loading) ApplySize(); };
            foreach (var n in new[]{ diaNum, depthNum, threadNum }) n.ValueChanged += (s, e) => { if (!loading) RefreshAll(); };
            foreach (var rb in new[]{ throughRadio, blindRadio, threadFullRadio, threadPartRadio }) {
                var r = rb;
                r.CheckedChanged += (s, e) => { if (!loading && r.Checked) RefreshAll(); };
            }
            wholeGroup.CheckedChanged += (s, e) => { if (!loading) RefreshAll(); };
        }

        HoleKind KindNow { get { return (HoleKind)Math.Max(0, kindSeg.SelectedIndex); } }
        ThreadRow RowNow {
            get {
                var row = HoleMatcher.Find(sizeBox.SelectedItem == null ? "M6" : sizeBox.SelectedItem.ToString());
                return row.HasValue ? row.Value : HoleMatcher.M6;
            }
        }

        // 换孔型 / 换规格：孔径和默认螺纹深度按表重填（和批量排孔同一套）
        void ApplySize(){
            var sp = HoleMatcher.FromRow(RowNow, KindNow);
            loading = true;
            try {
                diaNum.Value = Ui.Clamp((decimal)Math.Round(sp.HoleDiameter, 3), diaNum.Minimum, diaNum.Maximum);
                if (!threadPartRadio.Checked) threadNum.Value = Ui.Clamp((decimal)sp.ThreadDepth, threadNum.Minimum, threadNum.Maximum);
            } finally { loading = false; }
            RefreshAll();
        }

        // 面板 → 规格。沉孔/锥孔尺寸按表，孔径/深度/螺纹深度按手填。
        HoleSpec SpecNow(){
            var sp = HoleMatcher.FromRow(RowNow, KindNow);
            sp.HoleDiameter = (double)diaNum.Value;
            sp.Depth = throughRadio.Checked ? 0 : (double)depthNum.Value;
            sp.ThreadFull = threadFullRadio.Checked;
            sp.ThreadDepth = (double)threadNum.Value;
            sp.Note = "";
            return sp;
        }

        // 第一次点中孔时，界面按这个孔现在的规格填好（只改想改的那一项就行）
        void LoadSpec(EditableHole t){
            if (t == null || t.Spec == null) return;
            var s = t.Spec;
            loading = true;
            try {
                kindSeg.SelectedIndex = (int)s.Kind;
                if (s.ThreadSize.Length > 0 && sizeBox.Items.Contains(s.ThreadSize)) sizeBox.SelectedItem = s.ThreadSize;
                if (s.HoleDiameter > 0) diaNum.Value = Ui.Clamp((decimal)s.HoleDiameter, diaNum.Minimum, diaNum.Maximum);
                if (s.Through) throughRadio.Checked = true;
                else { blindRadio.Checked = true; depthNum.Value = Ui.Clamp((decimal)s.Depth, depthNum.Minimum, depthNum.Maximum); }
                if (s.Kind == HoleKind.Tapped) {
                    if (s.ThreadFull) threadFullRadio.Checked = true;
                    else { threadPartRadio.Checked = true; threadNum.Value = Ui.Clamp((decimal)s.ThreadDepth, threadNum.Minimum, threadNum.Maximum); }
                }
            } finally { loading = false; }
        }

        void RefreshAll(){
            specGrid.Show(threadRow, KindNow == HoleKind.Tapped);
            depthNum.Enabled = blindRadio.Checked;
            threadNum.Enabled = threadPartRadio.Checked;
            throughRadio.Enabled = blindRadio.Enabled = KindNow != HoleKind.Countersink;
            var hs = SpecNow();
            stdInfo.Text = HoleStandards.Describe(hs);
            stdInfo.ForeColor = HoleStandards.Resolve(hs) != null ? Ui.Muted : Ui.Warn;

            int sel = list.SelectedIndex;
            list.BeginUpdate();
            try {
                list.Items.Clear();
                foreach (var t in targets) list.Items.Add(t.Label);
                if (sel >= 0 && sel < list.Items.Count) list.SelectedIndex = sel;
            } finally { list.EndUpdate(); }

            bool ok = false;
            if (targets.Count == 0) { preview.Text = "还没有选孔。"; preview.ForeColor = Ui.Muted; }
            else if (hs.SpecProblem.Length > 0) { preview.Text = "⚠ " + hs.SpecProblem; preview.ForeColor = Ui.Bad; }
            else {
                int n = 0;
                try { n = HoleEditCad.Expand(targets, wholeGroup.Checked).Count; } catch { }
                int bad = 0;
                if (!hs.Through) { try { bad = HoleEditCad.ThroughToBlind(targets, wholeGroup.Checked, hs); } catch { } }
                if (bad > 0) { preview.Text = "⚠ 有 " + bad + " 个是贯通孔，天工不支持把贯通孔直接改成盲孔。"; preview.ForeColor = Ui.Bad; }
                else {
                    // 多孔孔特征：点中的是其中一个孔，改的是整个特征（每个输入孔都换）
                    var multis = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var t in targets) if (t.Multi != null) multis.Add(HoleGrouping.Sys(t.Multi));
                    string what = multis.Count > 0 ? n + " 个孔（" + multis.Count + " 个多孔孔特征，整个一起改）" : n + " 个孔特征";
                    preview.Text = "要改 " + what + " → " + HoleNaming.Short(hs) + "\r\n" + hs.Summary;
                    preview.ForeColor = Ui.Ok; ok = true;
                }
            }
            tips.SetToolTip(preview, preview.Text);
            apply.Enabled = ok && !busy;
            undoButton.Enabled = lastUndo != null && !lastUndo.IsEmpty && !busy;
            DrawHighlights();
        }

        void DrawHighlights(){
            if (highlights == null) return;
            try {
                highlights.RemoveAll();
                foreach (var o in picked) { try { highlights.AddItem(o); } catch { } }
                highlights.Draw();
            } catch (Exception e) { Log.Write("HoleEditHighlight", e); }
        }

        void RemoveSelected(){
            if (busy) return;
            int i = list.SelectedIndex;
            if (i < 0 || i >= targets.Count) { SetStatus("先在列表里选一条。", Ui.Warn); return; }
            targets.RemoveAt(i);
            if (i < picked.Count) picked.RemoveAt(i);
            RefreshAll();
        }

        void JumpToSelected(){
            int i = list.SelectedIndex;
            if (i < 0 || i >= targets.Count || !targets[i].HasWhere) return;
            CadView.ZoomToHole(app, targets[i].Where, targets[i].DiameterMm);
        }

        void SetStatus(string text, Color color){ status.Text = text; status.ForeColor = color; }

        // ---- 测试 / 截图用的缝 ----
        public bool SuppressDialogs;
        public string LastResult = "";
        public int TargetCount { get { return targets.Count; } }
        public string PreviewText { get { return preview.Text; } }
        public bool CanApply { get { return apply.Enabled; } }
        public bool CanUndo { get { return lastUndo != null && !lastUndo.IsEmpty; } }
        public bool WholeGroup { get { return wholeGroup.Checked; } set { wholeGroup.Checked = value; } }
        public void ApplyForTest(){ Apply(); }
        public void UndoForTest(){ UndoLast(); }
        public HoleSpec CurrentSpecForTest { get { return SpecNow(); } }
        public void SetSpecForTest(HoleKind kind, string size, double depthMm){
            loading = true;
            try { kindSeg.SelectedIndex = (int)kind; if (size != null && sizeBox.Items.Contains(size)) sizeBox.SelectedItem = size; }
            finally { loading = false; }
            ApplySize();
            loading = true;
            try {
                if (depthMm > 0) { blindRadio.Checked = true; depthNum.Value = Ui.Clamp((decimal)depthMm, depthNum.Minimum, depthNum.Maximum); }
                else throughRadio.Checked = true;
            } finally { loading = false; }
            RefreshAll();
        }

        // 「自动打孔 / 批量排孔」带过来的那一批
        public void Preload(IList<KeyValuePair<P.PartDocument, string>> created){
            var notes = new List<string>();
            var got = HoleEditCad.FromCreated(created, notes);
            foreach (var t in got) AddTarget(t, null);
            if (got.Count > 0) LoadSpec(got[0]);
            RefreshAll();
            SetStatus(got.Count > 0 ? "已带入刚打的 " + got.Count + " 个孔特征。选好新规格点「应用」。" : "刚打的孔没找回来（可能已删除）。在模型里点要改的孔。",
                      got.Count > 0 ? Ui.Ok : Ui.Warn);
            foreach (var n in notes) Log.Write("HoleEditPreload", n);
        }

        bool AddTarget(EditableHole t, object selection){
            foreach (var x in targets)
                if (x.Sys == t.Sys && x.PartName == t.PartName) return false;
            targets.Add(t);
            picked.Add(selection);
            return true;
        }

        // ---------------- 应用 / 撤销 ----------------
        void Apply(){
            if (busy || closing || targets.Count == 0) return;
            var spec = SpecNow();
            bool hadCommand = command != null;
            busy = true; apply.Enabled = false; UseWaitCursor = true;
            StopCommand();
            try {
                var o = HoleEditCad.Apply(targets, spec, wholeGroup.Checked);
                lastUndo = o.Undo;
                var items = new List<ResultList.Item>(o.Items);
                foreach (var n in o.Notes) items.Add(new ResultList.Item(ResultList.Kind.Info, n));
                if (o.Changed > 0) items.Add(new ResultList.Item(ResultList.Kind.Info, "记得保存装配。改错了点「撤销修改」。"));
                string head = "改成 " + HoleNaming.Short(spec) + "：" + o.Changed + " 个孔" + (o.Failed > 0 ? " · 失败 " + o.Failed : "");
                results.SetResult(head, o.Failed > 0 ? Ui.Warn : (o.Changed > 0 ? Ui.Ok : Ui.Warn), items);
                resultCard.Visible = true; rootStack.Relayout();
                LastResult = head + (o.Notes.Count > 0 ? "\r\n" + string.Join("\r\n", o.Notes.ToArray()) : "");
                var notes = new List<string>();
                targets = HoleEditCad.Refresh(targets, notes);
                SetStatus(head, o.Failed > 0 ? Ui.Warn : Ui.Ok);
            } catch (Exception e) {
                Log.Write("HoleEditRun", e);
                SetStatus(e.Message, Ui.Bad);
                LastResult = "改孔型中断：" + e.Message;
                if (!SuppressDialogs) MessageBox.Show(this, e.Message, "改孔型", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            } finally {
                busy = false; UseWaitCursor = false;
                if (hadCommand) { try { StartPicking(); } catch (Exception ex) { SetStatus(ex.Message, Ui.Warn); } }
                RefreshAll();
            }
        }

        void UndoLast(){
            if (busy || closing || lastUndo == null || lastUndo.IsEmpty) return;
            if (!SuppressDialogs && MessageBox.Show(this, "把上一次改过的 " + lastUndo.Count + " 个孔特征恢复成原来的孔型和名字？",
                    "撤销修改", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            bool hadCommand = command != null;
            busy = true; UseWaitCursor = true;
            StopCommand();
            try {
                var r = lastUndo.Run();
                lastUndo = null;
                var items = new List<ResultList.Item>();
                foreach (var n in r.Notes) items.Add(new ResultList.Item(ResultList.Kind.Info, n));
                results.SetResult(r.Summary, r.Failed > 0 ? Ui.Warn : Ui.Ok, items);
                resultCard.Visible = true; rootStack.Relayout();
                LastResult = r.Summary;
                var notes = new List<string>();
                targets = HoleEditCad.Refresh(targets, notes);
                SetStatus(r.Summary, r.Failed > 0 ? Ui.Warn : Ui.Ok);
            } catch (Exception e) {
                Log.Write("HoleEditUndoRun", e);
                SetStatus("撤销没有完成：" + e.Message, Ui.Bad);
                LastResult = "撤销中断：" + e.Message;
            } finally {
                busy = false; UseWaitCursor = false;
                if (hadCommand) { try { StartPicking(); } catch (Exception ex) { SetStatus(ex.Message, Ui.Warn); } }
                RefreshAll();
            }
        }

        // ---------------- 选择 ----------------
        public void StartPicking(){
            try {
                if (command != null) StopCommand();
                if (assembly == null) throw new InvalidOperationException("请先打开装配文件。");
                if (assembly.ReadOnly || assembly.InPlaceActivated) throw new InvalidOperationException("请在可编辑的装配顶层运行。");
                command = (F.ISECommand)app.CreateCommand((int)SolidEdgeConstants.seCmdFlag.seNoDeactivate);
                commandConnection = new Connection(command, typeof(F.ISECommandEvents), this);
                command.Start();
                mouse = (F.ISEMouse)command.Mouse;
                mouseConnection = new Connection(mouse, typeof(F.ISEMouseEvents), this);
                mouse.ScaleMode = 1; mouse.WindowTypes = 1;
                mouse.LocateMode = (int)SolidEdgeConstants.seLocateModes.seLocateSimple;
                mouse.EnabledMove = true;
                ((F.ISEMouseEx)mouse).InterDocumentLocate = true;
                ((F.ISEMouseEx2)mouse).LocateFrontToBack = true;
                ((F.ISEMouseEx3)mouse).PathfinderLocate = false;
                mouse.ClearLocateFilter();
                mouse.AddToLocateFilter((int)SolidEdgeConstants.seLocateFilterConstants.seLocateEdge);
                mouse.AddToLocateFilter((int)SolidEdgeConstants.seLocateFilterConstants.seLocateFace);
                if (highlights != null) highlights.Delete();
                highlights = assembly.HighlightSets.Add();
                highlights.Color = ColorTranslator.ToOle(Color.DeepSkyBlue);
                DrawHighlights();
            } catch { Cleanup(); throw; }
        }

        public void AcceptPick(object selected){
            if (busy || closing || selected == null) return;
            try {
                var t = HoleEditCad.Resolve(selected);
                bool first = targets.Count == 0;
                if (!AddTarget(t, selected)) { SetStatus("这个孔已经在列表里了。", Ui.Muted); return; }
                if (first) LoadSpec(t);
                RefreshAll();
                SetStatus("已加入：" + t.Label, Ui.Ok);
            } catch (Exception e) {
                SetStatus(e.Message, Ui.Warn);
                Log.Write("HoleEditPick", e);
            }
        }

        void StopCommand(){
            if (highlights != null) { try { highlights.Delete(); } catch { } highlights = null; }
            if (mouseConnection != null) { mouseConnection.Dispose(); mouseConnection = null; }
            if (commandConnection != null) { commandConnection.Dispose(); commandConnection = null; }
            var c = command; command = null; mouse = null;
            if (c != null) try { c.Done = true; } catch { }
        }
        void Cleanup(){ if (closing) return; closing = true; StopCommand(); try { tips.Dispose(); } catch { } }

        public new void MouseClick(short b, short s, double x, double y, double z, object w, int k, object g){
            if (b == 1) { if (g == null) { SetStatus("这里没有捕捉到对象。零件没激活（重开大装配后常见）时，先在天工里右键它选「激活」。", Ui.Warn); return; } AcceptPick(g); }
            else if (b == 2) Close();
        }
        public new void MouseDown(short b, short s, double x, double y, double z, object w, int k, object g){ }
        public new void MouseUp(short b, short s, double x, double y, double z, object w, int k, object g){ }
        public new void MouseMove(short b, short s, double x, double y, double z, object w, int k, object g){ }
        public void MouseDblClick(short b, short s, double x, double y, double z, object w, int k, object g){ }
        public void MouseDrag(short b, short s, double x, double y, double z, object w, short ds, int k, object g){ }
        public void Terminate(){ if (!closing && !busy) BeginInvoke((Action)(() => Close())); }
        public void Idle(int n, out bool more){ more = false; }
        void F.ISECommandEvents.Activate(){ }
        void F.ISECommandEvents.Deactivate(){ }
        public new void KeyDown(ref short key, short shift){ if (key == 27) Close(); }
        public new void KeyPress(ref short key){ }
        public new void KeyUp(ref short key, short shift){ }
    }
}
