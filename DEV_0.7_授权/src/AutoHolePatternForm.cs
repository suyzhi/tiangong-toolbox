using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using A=SolidEdgeAssembly;
using F=SolidEdgeFramework;
using G=SolidEdgeGeometry;
using P=SolidEdgePart;

namespace TianGongCadSuite {
    // 命令 7：批量排孔。沿线等分 / 沿线定距 / 圆周均布，可选腰孔。
    // 一切都围着"点一个面"：方向自动取该面最长边，孔心自动居中，参数有能直接用的默认值。
    //
    // 2026-10-13：和「自动打孔」对齐 ——
    //   · 打孔改走 AutoHoleWriter.DrillRequests：同规格的孔编成特征组并按规格命名、并进之前那批的组、
    //     位置上本来就有孔的先认出来跳过、按天工孔标准表写 标准/子类型/大小/配合；
    //   · 螺纹孔可选「全长 / 指定深度」；
    //   · 界面换成同一套：分段按钮 + FieldGrid（按排布方式/孔型整行显隐，不再一堆灰掉的框）。
    public sealed class AutoHolePatternForm : Form, IBusyTool, F.ISEMouseEvents, F.ISECommandEvents {
        // 处理中不让关（Esc、关闭按钮、标题栏 ×、CAD 里按 Esc / 右键都会走到这里）：见 IBusyTool
        public bool Busy { get { return busy; } }
        public string BusyWhat { get { return "打孔"; } }
        protected override void OnFormClosing(FormClosingEventArgs e){
            if (busy && e.CloseReason == CloseReason.UserClosing) {
                e.Cancel = true;
                try { status.Text = "正在打孔，做完再关。"; status.ForeColor = Ui.Warn; } catch { }
                return;
            }
            base.OnFormClosing(e);
        }

        readonly F.Application app;
        readonly A.AssemblyDocument assembly;
        F.ISECommand command; F.ISEMouse mouse; Connection mouseConnection, commandConnection;
        F.HighlightSet highlights;

        object faceSelection, dirSelection;
        TargetFace target; FaceFrame frame; string faceText = "";
        V3 circleCentre; double circleRadiusMm; bool circleReady;
        bool busy, closing, loading;

        readonly Segmented modeSeg = new Segmented("沿线等分", "沿线定距", "圆周均布");
        readonly NumericUpDown count = Ui.Num(1, 720, 4, 0, 1, 72);
        readonly NumericUpDown pitch = Ui.Num(0.5M, 5000M, 50, 1, 5, 72);
        readonly NumericUpDown edge = Ui.Num(0, 5000, 10, 1, 5, 64);
        // 末端边距：勾着「两端相同」时跟着起端走（原来只有一个边距，两端对称）
        readonly NumericUpDown endEdge = Ui.Num(0, 5000, 10, 1, 5, 64);
        readonly CheckBox sameEdge = new CheckBox { Text = "两端相同", AutoSize = true, Checked = true };
        // 横向位置：居中，或「孔心离方向边 X mm」（从看得见的那条边量；方向边 = 自动取的最长边或用户点的那条）
        readonly Segmented latMode = new Segmented("居中", "离方向边");
        readonly NumericUpDown lateral = Ui.Num(0, 5000, 20, 1, 1, 64);
        readonly NumericUpDown startAngle = Ui.Num(-360, 360, 0, 1, 15, 72);
        // 预设：常用的一套参数存个名字，下次一点就填好（PatternPresets）
        readonly ComboBox presetBox = Ui.Combo(132, "（选择预设）");
        PatternPresetStore presets;
        // 打孔结果 + 撤销（和「自动打孔」同一套）
        readonly ResultList results = new ResultList();
        Card resultCard;
        HoleUndo lastUndo;
        readonly Button undoButton = Ui.Secondary("撤销本次打孔", 112, 30);
        readonly Button editButton = Ui.Secondary("改孔型…", 84, 30);
        List<KeyValuePair<P.PartDocument, string>> lastCreated;
        // 种子孔 + 阵列（HolePatternWriter）：在天工里改种子孔整排跟着变。勾掉 = 原来的逐个打孔
        readonly CheckBox asPattern = new CheckBox { Text = "建成阵列（改种子孔，整排跟着变）", AutoSize = true, Checked = true };
        readonly Segmented kindSeg = new Segmented("通孔", "螺纹孔", "圆柱沉孔", "锥形沉孔");
        readonly ComboBox sizeBox = Ui.Combo(84, HoleMatcher.Table.Select(r => r.Size).ToArray());
        // 孔径框 3 位小数：螺纹内小径是 4.917 / 6.647 / 8.376 这种三位数。原来 2 位小数会把 4.917 圆成 4.92，
        // 用户一动别的参数，规格就按 4.92 重建 —— 不再等于天工孔标准表那一行，天工孔命令里又显示 *。
        readonly NumericUpDown diaNum = Ui.Num(0.1M, 500M, 6.6M, 3, 0.1M, 72);
        readonly NumericUpDown depthNum = Ui.Num(0.1M, 2000M, 10M, 2, 1M, 72);
        readonly RadioButton throughRadio = new RadioButton { Text = "贯通", AutoSize = true, Checked = true };
        readonly RadioButton blindRadio = new RadioButton { Text = "盲孔", AutoSize = true };
        readonly RadioButton threadFullRadio = new RadioButton { Text = "全长", AutoSize = true, Checked = true };
        readonly RadioButton threadPartRadio = new RadioButton { Text = "指定深度", AutoSize = true };
        readonly NumericUpDown threadNum = Ui.Num(0.5M, 2000M, 12M, 2, 1M, 72);
        readonly CheckBox slotCheck = new CheckBox { Text = "排成腰孔", AutoSize = true };
        readonly NumericUpDown slotLen = Ui.Num(2M, 2000M, 30M, 1, 5M, 72);
        readonly NumericUpDown slotWidth = Ui.Num(0.5M, 500M, 9M, 2, 0.5M, 72);
        readonly Label faceInfo = new Label();
        readonly Label stdInfo = new Label();
        readonly Label previewText = new Label();
        readonly Label status = new Label();
        readonly Button run = Ui.Primary("开始打孔", 150, 36);
        readonly ToolTip tips = new ToolTip { AutoPopDelay = 15000, InitialDelay = 400 };

        readonly FieldGrid layoutGrid = new FieldGrid();
        readonly FieldGrid specGrid = new FieldGrid();
        FieldGrid.Row countRow, pitchRow, edgeRow, lateralRow, angleRow, threadRow, slotRow;
        Card layoutCard, specCard;
        VerticalStack rootStack;
        Panel previewCanvas;

        public AutoHolePatternForm(F.Application application, A.AssemblyDocument document){
            app = application; assembly = document;
            Ui.Shell(this, "批量排孔", 680, 800, 600, 700);
            BuildLayout();
            WireEvents();
            loading = true;
            try { sizeBox.SelectedItem = "M6"; } finally { loading = false; }
            ApplySize();
            FormClosed += (s, e) => Cleanup();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData){
            if (keyData == Keys.Escape) { Close(); return true; }
            if (keyData == (Keys.Control | Keys.Enter)) { Run(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ---------------- 布局 ----------------
        // ① 打孔面 → ② 排布 → ③ 孔规格 → ④ 预览（吃掉剩余高度）→ 按钮 → 状态栏
        void BuildLayout(){
            var faceCard = Ui.Card("① 打孔面", 84, "点要排孔的平面；圆周均布再点一条圆形边线定圆心");
            var reselect = Ui.Small("重选");
            reselect.Click += (s, e) => { if (busy) return; faceSelection = null; dirSelection = null; target = null; frame = null; circleReady = false; faceText = ""; ConfigureFilter(); RefreshPreview(); SetStatus("重新点一个面。", Ui.Accent); };
            faceCard.AddHeader(reselect);
            faceInfo.Dock = DockStyle.Fill; faceInfo.AutoSize = false; faceInfo.ForeColor = Ui.Text; faceInfo.BackColor = Color.White;
            faceInfo.AutoEllipsis = true;
            faceCard.Controls.Add(faceInfo);

            layoutCard = Ui.Card("② 排布", 200, "方向默认取面上最长的边");
            // 标题行右侧：预设下拉 + 存为预设 + 删除（Card.AddHeader 从右往左排，所以先加的在最右）
            var savePreset = Ui.Small("存为预设"); var delPreset = Ui.Small("删除");
            presetBox.Font = Ui.F8; presetBox.Height = 22;
            savePreset.Click += (s, e) => SavePreset();
            delPreset.Click += (s, e) => DeletePreset();
            presetBox.SelectedIndexChanged += (s, e) => { if (!loading && presetBox.SelectedIndex > 0) ApplyPreset(presetBox.SelectedItem.ToString()); };
            tips.SetToolTip(savePreset, "把现在这套排布 + 孔规格存个名字，下次在左边下拉里一点就填好。");
            layoutCard.AddHeader(presetBox); layoutCard.AddHeader(savePreset); layoutCard.AddHeader(delPreset);
            var lb = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
            layoutCard.Controls.Add(lb);
            lb.Controls.Add(layoutGrid);
            modeSeg.Width = Math.Max(270, modeSeg.PreferredWidth);
            layoutGrid.Add("方式", modeSeg);
            countRow = layoutGrid.Add("孔数", count, FieldGrid.Unit("个"));
            pitchRow = layoutGrid.Add("间距", pitch, FieldGrid.Unit("mm"));
            edgeRow = layoutGrid.Add("边距", Ui.Caption("起端"), edge, FieldGrid.Unit("mm"), FieldGrid.Space(8), Ui.Caption("末端"), endEdge, FieldGrid.Unit("mm"), FieldGrid.Space(6), sameEdge);
            latMode.Width = Math.Max(150, latMode.PreferredWidth);
            lateralRow = layoutGrid.Add("横向", latMode, lateral, FieldGrid.Unit("mm"));
            angleRow = layoutGrid.Add("起始角", startAngle, FieldGrid.Unit("°"));
            tips.SetToolTip(edge, "第一个孔离起点那条边的距离（起点在示意图左端）。");
            tips.SetToolTip(endEdge, "最后一个孔离终点那条边的距离。定距排孔排不满时，末端实际会多出一点，预览里会写。");
            tips.SetToolTip(lateral, "孔心离方向边的距离（往面里面量）。方向边：自动时是面上最长的那条边，也可以在模型上点一条直线边指定（会高亮）。");
            lb.Resize += (s, e) => layoutGrid.SetBounds(0, 0, Math.Max(80, lb.ClientSize.Width), layoutGrid.PreferredHeight);

            specCard = Ui.Card("③ 孔规格", 260, "规格按天工孔标准表；改了孔径就是非标孔");
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
            specGrid.Add("生成", asPattern);
            tips.SetToolTip(asPattern, "勾着：打 1 个种子孔，其余用一个阵列特征复制出来 —— 之后在天工里改种子孔（或用「改孔型」）整排跟着变。\r\n"
                                     + "有位置本来就有孔、或者阵列没建成时，会自动改成逐个打孔并在结果里说明。腰孔总是逐个通切。");
            slotRow = specGrid.Add("腰孔", slotCheck, FieldGrid.Space(6), Ui.Caption("总长"), slotLen, FieldGrid.Unit("mm"), FieldGrid.Space(6), Ui.Caption("槽宽"), slotWidth, FieldGrid.Unit("mm"));
            tips.SetToolTip(threadFullRadio, "贯通孔整孔攻丝，盲孔攻到孔底。");
            tips.SetToolTip(threadPartRadio, "从孔口往下攻指定深度（盲孔不能超过孔深）。");
            tips.SetToolTip(slotCheck, "沿线排布时可以把每个孔排成长圆孔（腰孔），用通切做；圆周均布不支持。");
            sb.Resize += (s, e) => {
                int w = Math.Max(80, sb.ClientSize.Width);
                specGrid.SetBounds(0, 0, w, specGrid.PreferredHeight);
                stdInfo.SetBounds(0, specGrid.PreferredHeight + 2, w, 18);
            };

            var previewCard = Ui.Card("④ 预览", 0, "");
            var pv = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
            previewCard.Controls.Add(pv);
            previewText.Dock = DockStyle.Top; previewText.Height = 40; previewText.AutoSize = false; previewText.Font = Ui.F9B;
            previewText.AutoEllipsis = true; previewText.BackColor = Color.White;
            var canvas = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
            canvas.Paint += DrawPreview;
            canvas.Resize += (s, e) => canvas.Invalidate();
            pv.Controls.Add(canvas); pv.Controls.Add(previewText);
            previewCanvas = canvas;

            var actions = new Panel { Height = 40, BackColor = Ui.Bg };
            run.SetBounds(0, 2, 150, 36);
            var close = Ui.Secondary("关闭", 80, 30);
            close.Click += (s, e) => Close();
            run.Click += (s, e) => Run();
            tips.SetToolTip(run, "Ctrl+Enter"); tips.SetToolTip(close, "Esc");
            undoButton.Enabled = false;
            undoButton.Click += (s, e) => UndoLastBatch();
            tips.SetToolTip(undoButton, "删掉上一次「开始打孔」排出来的孔（编组、改名一并恢复）。");
            editButton.Enabled = false;
            editButton.Click += (s, e) => OpenEdit();
            tips.SetToolTip(editButton, "把上一次排出来的孔带进「改孔型」窗口：整组换孔型/规格，不用删了重打。");
            actions.Controls.Add(run); actions.Controls.Add(editButton); actions.Controls.Add(undoButton); actions.Controls.Add(close);
            actions.Resize += (s, e) => {
                close.SetBounds(actions.Width - 80, 5, 80, 30);
                undoButton.SetBounds(close.Left - 8 - 112, 5, 112, 30);
                editButton.SetBounds(undoButton.Left - 8 - 84, 5, 84, 30);
            };

            // 打孔结果（打完才出现）：失败/跳过/打好的孔逐条列出，点一条跳过去
            resultCard = Ui.Card("本次结果", 170, "点一条跳到那个孔");
            var copyResult = Ui.Small("复制"); var hideResult = Ui.Small("收起");
            copyResult.Click += (s, e) => { try { Clipboard.SetText(results.AsText()); SetStatus("结果已复制。", Ui.Ok); } catch (Exception ex) { SetStatus("复制失败：" + ex.Message, Ui.Warn); } };
            hideResult.Click += (s, e) => { resultCard.Visible = false; rootStack.Relayout(); };
            resultCard.AddHeader(copyResult); resultCard.AddHeader(hideResult);
            results.Dock = DockStyle.Fill;
            results.Locate += it => {
                if (CadView.ZoomToHole(app, it.Where, it.DiameterMm)) SetStatus("已跳到：" + it.Text, Ui.Accent);
                else SetStatus("没能移动视图（请确认装配窗口是当前窗口）。", Ui.Warn);
            };
            resultCard.Controls.Add(results);
            resultCard.Visible = false;

            status.Height = 24; status.Padding = new Padding(2, 0, 10, 0); status.AutoEllipsis = true;
            status.TextAlign = ContentAlignment.MiddleLeft; status.ForeColor = Ui.Accent; status.BackColor = Ui.Bg;
            status.Text = "点要排孔的那个平面。";

            var stack = rootStack = new VerticalStack { Dock = DockStyle.Fill };
            stack.Add(faceCard); stack.Add(layoutCard); stack.Add(specCard);
            stack.Add(previewCard, true); stack.Add(resultCard); stack.Add(actions); stack.Add(status);
            Controls.Add(stack);
            LoadPresets(null);
        }

        // ---------------- 预设 ----------------
        // 存的是界面上看得见的那些值（键名固定，别随手改：旧预设文件靠它们读回来）
        PatternPreset CapturePreset(string name){
            var p = new PatternPreset { Name = name };
            var ci = CultureInfo.InvariantCulture;
            p.Values["mode"] = modeSeg.SelectedIndex.ToString(ci);
            p.Values["count"] = count.Value.ToString(ci);
            p.Values["pitch"] = pitch.Value.ToString(ci);
            p.Values["edge"] = edge.Value.ToString(ci);
            p.Values["endEdge"] = endEdge.Value.ToString(ci);
            p.Values["sameEdge"] = sameEdge.Checked ? "1" : "0";
            p.Values["latMode"] = latMode.SelectedIndex.ToString(ci);
            p.Values["lateral"] = lateral.Value.ToString(ci);
            p.Values["angle"] = startAngle.Value.ToString(ci);
            p.Values["kind"] = kindSeg.SelectedIndex.ToString(ci);
            p.Values["size"] = sizeBox.SelectedItem == null ? "" : sizeBox.SelectedItem.ToString();
            p.Values["dia"] = diaNum.Value.ToString(ci);
            p.Values["blind"] = blindRadio.Checked ? "1" : "0";
            p.Values["depth"] = depthNum.Value.ToString(ci);
            p.Values["threadFull"] = threadFullRadio.Checked ? "1" : "0";
            p.Values["threadDepth"] = threadNum.Value.ToString(ci);
            p.Values["slot"] = slotCheck.Checked ? "1" : "0";
            p.Values["slotLen"] = slotLen.Value.ToString(ci);
            p.Values["slotWidth"] = slotWidth.Value.ToString(ci);
            p.Values["asPattern"] = asPattern.Checked ? "1" : "0";
            return p;
        }

        void ApplyPreset(string name){
            if (presets == null) return;
            var p = presets.Find(name);
            if (p == null) return;
            loading = true;
            try {
                SetIndex(modeSeg, p.Get("mode", null));
                SetNum(count, p.Get("count", null)); SetNum(pitch, p.Get("pitch", null));
                SetNum(edge, p.Get("edge", null)); SetNum(endEdge, p.Get("endEdge", null));
                sameEdge.Checked = p.Get("sameEdge", sameEdge.Checked ? "1" : "0") == "1";
                SetIndex(latMode, p.Get("latMode", null));
                SetNum(lateral, p.Get("lateral", null)); SetNum(startAngle, p.Get("angle", null));
                SetIndex(kindSeg, p.Get("kind", null));
                string size = p.Get("size", "");
                if (size.Length > 0 && sizeBox.Items.Contains(size)) sizeBox.SelectedItem = size;
                SetNum(diaNum, p.Get("dia", null));
                if (p.Get("blind", "0") == "1") blindRadio.Checked = true; else throughRadio.Checked = true;
                SetNum(depthNum, p.Get("depth", null));
                if (p.Get("threadFull", "1") == "1") threadFullRadio.Checked = true; else threadPartRadio.Checked = true;
                SetNum(threadNum, p.Get("threadDepth", null));
                slotCheck.Checked = p.Get("slot", "0") == "1";
                SetNum(slotLen, p.Get("slotLen", null)); SetNum(slotWidth, p.Get("slotWidth", null));
                asPattern.Checked = p.Get("asPattern", "1") == "1";
            } finally { loading = false; }
            ConfigureFilter();
            RefreshPreview();
            SetStatus("已套用预设「" + p.Name + "」。", Ui.Ok);
        }
        static void SetNum(NumericUpDown n, string v){
            decimal d;
            if (v != null && decimal.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) n.Value = Ui.Clamp(d, n.Minimum, n.Maximum);
        }
        static void SetIndex(Segmented seg, string v){
            int i;
            if (v != null && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out i) && i >= 0 && i < seg.Items.Count) seg.SelectedIndex = i;
        }

        void LoadPresets(string select){
            try { presets = PatternPresetStore.Default().Load(); }
            catch (Exception e) { presets = null; Log.Write("PatternPresetLoad", e); SetStatus("排孔预设文件读不出来：" + e.Message, Ui.Warn); }
            bool was = loading; loading = true;
            try {
                presetBox.Items.Clear();
                presetBox.Items.Add("（选择预设）");
                if (presets != null) foreach (var p in presets.Items) presetBox.Items.Add(p.Name);
                int at = select == null ? -1 : presetBox.Items.IndexOf(select);
                presetBox.SelectedIndex = at > 0 ? at : 0;
            } finally { loading = was; }
        }

        void SavePreset(){
            string current = presetBox.SelectedIndex > 0 ? presetBox.SelectedItem.ToString() : "";
            string name = Ui.Prompt(this, "存为预设", "预设名（同名会覆盖）：", current.Length > 0 ? current : HoleNaming.Short(HoleSpecNow()));
            if (name == null) return;
            name = name.Trim();
            if (name.Length == 0) { SetStatus("预设名不能为空。", Ui.Warn); return; }
            try {
                // 开窗时没读到预设（读失败）：先读一遍再加，别拿一个空表把文件里原有的预设整个覆盖掉
                if (presets == null) presets = PatternPresetStore.Default().Load();
                presets.Put(CapturePreset(name));
                presets.Save();
                LoadPresets(name);
                SetStatus("已存为预设「" + name + "」。", Ui.Ok);
            } catch (Exception e) { Log.Write("PatternPresetSave", e); SetStatus("预设没存上：" + e.Message, Ui.Bad); }
        }

        void DeletePreset(){
            if (presetBox.SelectedIndex <= 0 || presets == null) { SetStatus("先在下拉里选一个预设。", Ui.Warn); return; }
            string name = presetBox.SelectedItem.ToString();
            if (MessageBox.Show(this, "删除预设「" + name + "」？", "批量排孔", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            try { presets.Remove(name); presets.Save(); LoadPresets(null); SetStatus("已删除预设「" + name + "」。", Ui.Ok); }
            catch (Exception e) { Log.Write("PatternPresetDelete", e); SetStatus("删除失败：" + e.Message, Ui.Bad); }
        }

        void WireEvents(){
            modeSeg.SelectedIndexChanged += (s, e) => { if (!loading) { ConfigureFilter(); RefreshPreview(); } };
            latMode.SelectedIndexChanged += (s, e) => { if (!loading) RefreshPreview(); };
            kindSeg.SelectedIndexChanged += (s, e) => { if (!loading) ApplySize(); };
            sizeBox.SelectedIndexChanged += (s, e) => { if (!loading) ApplySize(); };
            foreach (var n in new NumericUpDown[]{ count, pitch, edge, endEdge, lateral, startAngle, diaNum, depthNum, threadNum, slotLen, slotWidth })
                n.ValueChanged += (s, e) => { if (!loading) RefreshPreview(); };
            // 两端相同：末端跟着起端走（勾掉时末端从起端当前值开始改）
            edge.ValueChanged += (s, e) => { if (sameEdge.Checked && endEdge.Value != edge.Value) endEdge.Value = Ui.Clamp(edge.Value, endEdge.Minimum, endEdge.Maximum); };
            sameEdge.CheckedChanged += (s, e) => {
                if (sameEdge.Checked) endEdge.Value = Ui.Clamp(edge.Value, endEdge.Minimum, endEdge.Maximum);
                if (!loading) RefreshPreview();
            };
            foreach (var rb in new[]{ throughRadio, blindRadio, threadFullRadio, threadPartRadio }) {
                var r = rb;
                r.CheckedChanged += (s, e) => { if (!loading && r.Checked) RefreshPreview(); };
            }
            slotCheck.CheckedChanged += (s, e) => { if (!loading) RefreshPreview(); };
            asPattern.CheckedChanged += (s, e) => { if (!loading) RefreshPreview(); };
        }

        HoleKind KindNow { get { return (HoleKind)Math.Max(0, kindSeg.SelectedIndex); } }
        ThreadRow RowNow {
            get {
                var row = HoleMatcher.Find(sizeBox.SelectedItem == null ? "M6" : sizeBox.SelectedItem.ToString());
                return row.HasValue ? row.Value : HoleMatcher.M6;
            }
        }

        // 换孔型 / 换规格：孔径和默认螺纹深度按表重填（用户手填过的螺纹深度保留）
        void ApplySize(){
            var sp = HoleMatcher.FromRow(RowNow, KindNow);
            loading = true;
            try {
                diaNum.Value = Ui.Clamp((decimal)Math.Round(sp.HoleDiameter, 3), diaNum.Minimum, diaNum.Maximum);
                if (!threadPartRadio.Checked) threadNum.Value = Ui.Clamp((decimal)sp.ThreadDepth, threadNum.Minimum, threadNum.Maximum);
            } finally { loading = false; }
            RefreshPreview();
        }

        HolePatternKind Kind { get { return modeSeg.SelectedIndex == 2 ? HolePatternKind.Circular : (modeSeg.SelectedIndex == 1 ? HolePatternKind.Pitch : HolePatternKind.Divide); } }

        HolePatternSpec Spec(){
            var s = new HolePatternSpec { Kind = Kind };
            s.Count = (int)count.Value;
            s.PitchMm = (double)pitch.Value;
            s.EdgeMm = (double)edge.Value;
            s.EndEdgeMm = sameEdge.Checked ? -1 : (double)endEdge.Value;
            s.LateralMm = latMode.SelectedIndex == 1 && frame != null ? frame.LateralFromEdge((double)lateral.Value) : 0;
            s.StartAngleDeg = (double)startAngle.Value;
            return s;
        }

        // 横向偏移检查用的"孔半宽"：腰孔看槽宽，沉孔/锥沉看孔口，其它看孔径
        double HalfWidthMm(HoleSpec hs){
            if (SlotMode) return (double)slotWidth.Value / 2;
            if (hs.Kind == HoleKind.Counterbore) return hs.CounterboreDiameter / 2;
            if (hs.Kind == HoleKind.Countersink) return hs.CountersinkDiameter / 2;
            return hs.HoleDiameter / 2;
        }

        // 面板上这套参数 -> 孔规格。沉孔/锥孔尺寸按表（这里不开放修改），孔径/深度/螺纹深度按手填。
        HoleSpec HoleSpecNow(){
            var sp = HoleMatcher.FromRow(RowNow, KindNow);
            sp.HoleDiameter = (double)diaNum.Value;
            sp.Depth = throughRadio.Checked ? 0 : (double)depthNum.Value;
            sp.ThreadFull = threadFullRadio.Checked;
            sp.ThreadDepth = (double)threadNum.Value;
            sp.Note = "";
            return sp;
        }

        bool SlotMode { get { return slotCheck.Checked && Kind != HolePatternKind.Circular; } }

        // ---------------- 选择 ----------------
        public void StartPicking(){
            try {
                if (command != null) StopCommand();   // 幂等：重复调用不会留下第二条命令
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
                ConfigureFilter();
                if (highlights != null) highlights.Delete();
                highlights = assembly.HighlightSets.Add();
                highlights.Color = ColorTranslator.ToOle(Color.DeepSkyBlue);
            } catch { Cleanup(); throw; }
        }

        // 点面与点边都必须能选到，不能"选完面就只让点边"：
        //   - 圆周均布：打孔面可以是端面也可以是圆柱侧面，所以先点面、再点圆边定位；
        //   - 沿线排孔：uv 基是从目标面算出来的，平面数据要先定下来，再点方向边。
        // 所以选择阶段始终同时开放边和面，点什么就是什么（和命令 6 的无模式交互一致）。
        void ConfigureFilter(){
            if (mouse == null) return;
            mouse.ClearLocateFilter();
            mouse.AddToLocateFilter((int)SolidEdgeConstants.seLocateFilterConstants.seLocateFace);
            mouse.AddToLocateFilter((int)SolidEdgeConstants.seLocateFilterConstants.seLocateEdge);
        }

        public void AcceptPick(object selected){
            if (busy || closing || selected == null) return;
            try {
                // 点在平面上就（重新）定打孔面；点在边线上就按当前排布方式当"定位圆 / 方向边"。
                var pg = PickGeometry.Unwrap(selected);
                bool isFace = pg.Geometry is G.Face;
                if (isFace || target == null) {
                    if (!isFace) throw new ArgumentException("请先点一个平面作为打孔面。");
                    var fresh = AutoHoleReader.ReadTarget(selected);
                    // 打完一批孔以后旧的面片对象已经失效，读它的 ID 会抛 COM 异常 —— 原来这一下直接让"再点同一个面接着排"报错
                    bool sameFace = false;
                    try { sameFace = target != null && frame != null && target.Part == fresh.Part && target.Face != null && fresh.Face != null && target.Face.ID == fresh.Face.ID; }
                    catch { sameFace = false; }
                    target = fresh; faceSelection = selected;
                    dirSelection = null; circleReady = false;
                    if (sameFace) {
                        // 同一个面再点一次 = 沿用原来选的方向边，不当成"新选面"清掉
                        SetStatus("还是这个面。" + frame.Label, Ui.Accent);
                    } else {
                        frame = FaceFrameReader.Read(target);   // 自动取最长边
                        faceText = FaceLabel(target) + "　方向：" + frame.Label;
                        SetStatus("已选打孔面。" + frame.Label + "　（要手动指定方向就再点一条直线边）", Ui.Ok);
                    }
                } else if (Kind == HolePatternKind.Circular) {
                    V3 c, ax; double r;
                    FaceFrameReader.ReadCircle(selected, out c, out r, out ax);
                    circleCentre = c; circleRadiusMm = r; circleReady = true; dirSelection = selected;
                    SetStatus("已选定位圆：R" + Num(r) + "mm。", Ui.Ok);
                } else {
                    frame = FaceFrameReader.Read(target, selected);
                    dirSelection = selected;
                    faceText = FaceLabel(target) + "　方向：" + frame.Label;
                    SetStatus("已指定方向边：" + Num(frame.LengthMm) + "mm。", Ui.Ok);
                }
                ConfigureFilter();
                RefreshPreview();
            } catch (Exception e) { SetStatus(e.Message, Ui.Warn); Log.Write("AutoHolePatternPick", e); }
        }

        static string FaceLabel(TargetFace t){
            return (t.PartName != null && t.PartName.Length > 0 ? t.PartName : "零件") + "　" + t.Label;
        }
        static string Num(double v){ return v.ToString("0.##", CultureInfo.InvariantCulture); }

        // ---------------- 预览 ----------------
        void RefreshPreview(){
            var k = Kind;
            layoutGrid.Show(countRow, k != HolePatternKind.Pitch);
            layoutGrid.Show(pitchRow, k == HolePatternKind.Pitch);
            layoutGrid.Show(edgeRow, k != HolePatternKind.Circular);
            layoutGrid.Show(lateralRow, k != HolePatternKind.Circular);
            layoutGrid.Show(angleRow, k == HolePatternKind.Circular);
            endEdge.Enabled = !sameEdge.Checked;
            lateral.Enabled = latMode.SelectedIndex == 1;
            specGrid.Show(threadRow, KindNow == HoleKind.Tapped && !SlotMode);
            specGrid.Show(slotRow, k != HolePatternKind.Circular);
            depthNum.Enabled = blindRadio.Checked;
            threadNum.Enabled = threadPartRadio.Checked;
            slotLen.Enabled = slotWidth.Enabled = slotCheck.Checked;
            kindSeg.Enabled = sizeBox.Enabled = !SlotMode;
            asPattern.Enabled = !SlotMode;
            throughRadio.Enabled = blindRadio.Enabled = !SlotMode && KindNow != HoleKind.Countersink;
            FitCards();

            var hs = HoleSpecNow();
            stdInfo.Text = SlotMode ? "腰孔用通切建模（不是孔特征），不在天工孔标准表里。" : HoleStandards.Describe(hs);
            stdInfo.ForeColor = SlotMode || HoleStandards.Resolve(hs) != null ? Ui.Muted : Ui.Warn;

            faceInfo.Text = target == null
                ? "还没有选面。把鼠标移到零件平面上点一下。"
                : faceText + (k == HolePatternKind.Circular && !circleReady ? "\r\n再点一条圆形边线来定位圆心和半径。" : "");
            faceInfo.ForeColor = target == null ? Ui.Muted : Ui.Text;

            bool ok = false;
            try {
                string tp = SlotMode ? "" : hs.SpecProblem;
                if (target == null) { previewText.Text = "选一个面，这里会画出排孔示意。"; previewText.ForeColor = Ui.Muted; }
                else if (tp.Length > 0) { previewText.Text = "⚠ " + tp; previewText.ForeColor = Ui.Bad; }
                else if (k == HolePatternKind.Circular) {
                    if (!circleReady) { previewText.Text = "圆周均布：再点一条圆形边线（分度圆）。"; previewText.ForeColor = Ui.Warn; }
                    else {
                        var a = HolePatternSolver.Angles(Spec());
                        previewText.Text = "在 R" + Num(circleRadiusMm) + " 的分度圆上均布 " + a.Length + " 个孔，起始 " + Num(Spec().StartAngleDeg) + "°，间隔 " + Num(360.0 / Spec().Count) + "°。\r\n"
                                         + HoleNaming.Short(hs) + "　·　" + (asPattern.Checked ? "种子孔 + 圆周阵列" : "同规格的孔合成一个孔特征");
                        previewText.ForeColor = Ui.Ok; ok = true;
                    }
                } else {
                    HolePatternSolver.Offsets(frame.LengthMm, Spec());
                    string lp = HolePatternSolver.LateralProblem(frame.WidthMm, Spec().LateralMm, HalfWidthMm(hs));
                    if (lp.Length > 0) throw new ArgumentException(lp);
                    previewText.Text = HolePatternSolver.Describe(frame.LengthMm, Spec())
                                     + (latMode.SelectedIndex == 1 ? "，孔心离方向边 " + Num((double)lateral.Value) + "mm" : "，横向居中") + "\r\n"
                                     + (SlotMode ? "腰孔 " + Num((double)slotLen.Value) + "×" + Num((double)slotWidth.Value) + "（通切）"
                                                 : HoleNaming.Short(hs) + "　·　" + (asPattern.Checked ? "种子孔 + 阵列" : "同规格的孔合成一个孔特征"));
                    previewText.ForeColor = Ui.Ok; ok = true;
                }
            } catch (Exception e) { previewText.Text = e.Message; previewText.ForeColor = Ui.Bad; }
            tips.SetToolTip(previewText, previewText.Text);
            run.Enabled = ok && !busy;
            if (highlights != null) {
                try {
                    highlights.RemoveAll();
                    if (faceSelection != null) highlights.AddItem(faceSelection);
                    if (dirSelection != null) highlights.AddItem(dirSelection);
                    highlights.Draw();
                } catch (Exception e) { Log.Write("AutoHolePatternHighlight", e); }
            }
            if (previewCanvas != null) previewCanvas.Invalidate();
        }

        // 行数变了：两张卡片跟着变高变矮，预览卡片吃掉剩下的
        void FitCards(){
            bool changed = false;
            int lh = 34 + layoutGrid.PreferredHeight + 10;
            int sh = 34 + specGrid.PreferredHeight + 22 + 10;
            if (layoutCard != null && layoutCard.Height != lh) { layoutCard.Height = lh; changed = true; }
            if (specCard != null && specCard.Height != sh) { specCard.Height = sh; changed = true; }
            layoutGrid.Height = layoutGrid.PreferredHeight;
            specGrid.Height = specGrid.PreferredHeight;
            stdInfo.Top = specGrid.PreferredHeight + 2;
            if (changed && rootStack != null) rootStack.Relayout();
        }

        // 排孔示意图：面轮廓 + 每个孔的位置（腰孔画成长圆）。
        void DrawPreview(object sender, PaintEventArgs e){
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var area = previewCanvas.ClientRectangle;
            if (area.Width < 40 || area.Height < 20) return;
            using (var pen = new Pen(Ui.Line))
            using (var accent = new Pen(Ui.Accent, 1.6f))
            using (var dot = new SolidBrush(Ui.Accent))
            using (var dash = new Pen(Ui.Accent, 1f) { DashStyle = DashStyle.Dash }) {
                if (target == null || frame == null) {
                    TextRenderer.DrawText(g, "（还没有选面）", Ui.F9, area, Ui.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    return;
                }
                var spec = Spec();
                var hs = HoleSpecNow();
                bool slot = SlotMode;
                var centres = new List<PointF>();
                double spanX, spanY;
                try {
                    if (Kind == HolePatternKind.Circular) {
                        if (!circleReady) { TextRenderer.DrawText(g, "（等一条圆形边线）", Ui.F9, area, Ui.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter); return; }
                        spanX = spanY = circleRadiusMm * 2 * 1.25;
                        foreach (var deg in HolePatternSolver.Angles(spec)) {
                            double rad = deg * Math.PI / 180.0;
                            centres.Add(new PointF((float)(Math.Cos(rad) * circleRadiusMm), (float)(Math.Sin(rad) * circleRadiusMm)));
                        }
                    } else {
                        var offs = HolePatternSolver.Offsets(frame.LengthMm, spec);
                        spanX = frame.LengthMm * 1.1; spanY = Math.Max(frame.WidthMm, hs.HoleDiameter * 4) * 1.6;
                        // 横向偏移：+Perp 画在上方（屏幕 y 向下，所以取负）
                        float lat = -(float)spec.LateralMm;
                        foreach (var o in offs) centres.Add(new PointF((float)(o - frame.LengthMm / 2.0), lat));
                    }
                } catch { return; }   // 参数不成立：预览文字里已经说了原因
                if (spanX <= 0 || spanY <= 0) return;
                float pad = 16;
                float scale = Math.Min((area.Width - 2 * pad) / (float)spanX, (area.Height - 2 * pad) / (float)spanY);
                if (scale <= 0) return;
                float cx = area.Width / 2f, cy = area.Height / 2f;
                float w = (float)(spanX / 1.25 * scale), h = (float)(spanY / 1.25 * scale);
                if (Kind == HolePatternKind.Circular) {
                    var r = (float)(circleRadiusMm * scale);
                    g.DrawEllipse(pen, cx - r * 1.35f, cy - r * 1.35f, r * 2.7f, r * 2.7f);
                    g.DrawEllipse(dash, cx - r, cy - r, r * 2, r * 2);
                } else {
                    g.DrawRectangle(pen, cx - w / 2, cy - h / 2, w, h);
                }
                float hr = Math.Max(2.5f, (float)(hs.HoleDiameter / 2 * scale));
                foreach (var c in centres) {
                    float px = cx + c.X * scale, py = cy + c.Y * scale;
                    if (slot) {
                        float sw = Math.Max(2f, (float)((double)slotWidth.Value / 2 * scale));
                        float sl = Math.Max(sw * 2, (float)((double)slotLen.Value * scale));
                        using (var path = Ui.Rounded(new Rectangle((int)(px - sl / 2), (int)(py - sw), (int)sl, (int)(sw * 2)), (int)sw)) g.FillPath(dot, path);
                    } else {
                        g.FillEllipse(dot, px - hr, py - hr, hr * 2, hr * 2);
                    }
                }
                if (Kind != HolePatternKind.Circular) {
                    float ly = cy - (float)(spec.LateralMm * scale);
                    if (Math.Abs(spec.LateralMm) > 1e-9) g.DrawLine(dash, cx - w / 2, cy, cx + w / 2, cy);   // 面的中线（虚线）
                    g.DrawLine(accent, cx - w / 2 - 6, ly, cx + w / 2 + 6, ly);                             // 孔心所在的线
                    TextRenderer.DrawText(g, "起端", Ui.F8, new Point((int)(cx - w / 2), (int)(cy + h / 2 + 2)), Ui.Muted);
                    // 方向边（量「离方向边」的那条）：画成粗线
                    float ey = cy - (float)(frame.EdgeLateralMm * scale);
                    using (var edgePen = new Pen(Ui.Warn, 2.4f)) g.DrawLine(edgePen, cx - w / 2, ey, cx + w / 2, ey);
                    TextRenderer.DrawText(g, "方向边", Ui.F8, new Point((int)(cx + w / 2 - 44), (int)(ey + (ey < cy ? 2 : -16))), Ui.Warn);
                }
            }
        }

        void SetStatus(string text, Color color){ status.Text = text; status.ForeColor = color; }
        object SafeActive(){ try { return app.ActiveDocument; } catch { return null; } }

        // ---- 测试 / 截图用的缝 ----
        public bool SuppressDialogs;
        public string LastResult = "";
        public void DrillForTest(){ Run(); }

        // ---------------- 打孔 ----------------
        void Run(){
            if (busy || closing) return;
            bool hadCommand = command != null;
            try {
                if (assembly == null) throw new InvalidOperationException("请先打开装配文件。");
                if (assembly.ReadOnly || assembly.InPlaceActivated) throw new InvalidOperationException("装配不可编辑或处于原位编辑中。");
                // 打完孔后缓存的面会失效，重解析一次。选择引用本身也失效时（模型重算过、面被孔切过）别把 COM 错误甩给用户：
                // 沿用上次读到的平面数据接着打（建基准面只用平面数据，FindOrCreatePlane 有退路）
                if (faceSelection != null) {
                    try { target = AutoHoleReader.ReadTarget(faceSelection); }
                    catch (Exception e) {
                        if (target == null) throw new InvalidOperationException("打孔面已经失效，请重新点一次要打孔的面。");
                        Log.Write("AutoHolePatternReread", e.Message);
                    }
                }
                if (target == null) throw new InvalidOperationException("请先点一个要打孔的面。");
                var spec = Spec();
                var hs = HoleSpecNow();
                if (!SlotMode && hs.SpecProblem.Length > 0) throw new InvalidOperationException(hs.SpecProblem);
                var centres = new List<V3>();
                if (Kind == HolePatternKind.Circular) {
                    if (!circleReady) throw new InvalidOperationException("圆周均布需要先点一条圆形边线。");
                    var u = frame.Direction; var v = frame.Perp;
                    foreach (var deg in HolePatternSolver.Angles(spec)) {
                        double rad = deg * Math.PI / 180.0;
                        centres.Add(circleCentre + u * (circleRadiusMm / 1000.0 * Math.Cos(rad)) + v * (circleRadiusMm / 1000.0 * Math.Sin(rad)));
                    }
                } else {
                    string lp = HolePatternSolver.LateralProblem(frame.WidthMm, spec.LateralMm, HalfWidthMm(hs));
                    if (lp.Length > 0) throw new InvalidOperationException(lp);
                    var side = frame.Perp * (spec.LateralMm / 1000.0);
                    foreach (var o in HolePatternSolver.Offsets(frame.LengthMm, spec)) centres.Add(frame.At(o) + side);
                }

                busy = true; run.Enabled = false; run.Text = "打孔中…"; UseWaitCursor = true; StopCommand();
                DrillResult res;
                string what;
                // 带零件间链接的零件（用「自动打孔」保持关联打过孔的零件都是）：天工只许在原位编辑里改它，
                // 在装配里直接建孔每个孔两侧都是 0x80010108（0.7.11 现场，自动打孔早就这么处理了，排孔漏了）。
                object startDoc = null; try { startDoc = app.ActiveDocument; } catch { }
                NativeHoleLink.InPlace inPlace = null;
                if (NativeHoleLink.HasLinks(target.Part)) {
                    string other = NativeHoleLink.TargetOpenElsewhere(app, assembly, target);
                    if (other.Length > 0) throw new InvalidOperationException(NativeHoleLink.OpenElsewhereMessage(other));
                    string ewhy;
                    inPlace = NativeHoleLink.EnterTarget(app, assembly, target, out ewhy);
                    if (inPlace == null) Log.Write("AutoHolePatternEnter", ewhy);
                }
                try {
                if (SlotMode) {
                    var localDir = target.Placement.InverseNormal(frame.Direction);
                    var lc = new List<V3>(); var ld = new List<V3>();
                    foreach (var c in centres) { lc.Add(target.Placement.InversePoint(c)); ld.Add(localDir); }
                    res = AutoHoleWriter.DrillSlots(target, lc, ld, (double)slotLen.Value, (double)slotWidth.Value);
                    if (res.Method.Length == 0) res.Method = "腰孔（通切）";
                    what = "腰孔 " + Num((double)slotLen.Value) + "×" + Num((double)slotWidth.Value);
                } else {
                    if (asPattern.Checked && centres.Count > 1) {
                        // 种子孔 + 阵列；排不成阵列时 HolePatternWriter 自己退回逐个打孔
                        res = HolePatternWriter.Drill(target, hs, centres, Kind == HolePatternKind.Circular ? (V3?)circleCentre : null);
                    } else {
                        // 和「自动打孔」走同一条路：编组命名、并进之前那批、已有孔跳过、天工标准孔
                        var requests = new List<AutoHoleWriter.HoleRequest>();
                        for (int i = 0; i < centres.Count; i++)
                            requests.Add(new AutoHoleWriter.HoleRequest { Spec = hs, Centre = centres[i], Source = "第 " + (i + 1) + " 个孔" });
                        res = AutoHoleWriter.DrillRequests(target, requests);
                    }
                    what = hs.Summary;
                }
                } finally {
                    if (inPlace != null) { try { NativeHoleLink.Leave(app, inPlace); } catch (Exception ex) { Log.Write("AutoHolePatternLeave", ex); } }
                    if (inPlace != null || !ReferenceEquals(SafeActive(), startDoc)) NativeHoleLink.BackTo(app, startDoc);
                }
                // LastResult：给真机脚本/测试读的纯文本（内容和原来的结果框一致）
                var legacy = new List<string>();
                foreach (var o in res.Outcomes) if (o.Kind == ResultList.Kind.Skip) legacy.Add(o.Text);
                legacy.AddRange(res.Notes);
                string msg = "排孔完成：" + res.Created + " / " + res.Requested + " 个"
                           + (res.AlreadyOk > 0 ? "，" + res.AlreadyOk + " 个位置本来就有孔" : "")
                           + "\r\n" + what + "\r\n生成方式：" + res.Method;
                if (res.Features.Count > 0) msg += "\r\n\r\n特征树里：\r\n· " + string.Join("\r\n· ", res.Features.ToArray());
                msg += "\r\n\r\n请保存装配。";
                if (res.Audit.Length > 0) msg += "\r\n\r\n自检：" + res.Audit;
                if (legacy.Count > 0) msg += "\r\n\r\n说明：\r\n· " + string.Join("\r\n· ", legacy.ToArray());
                if (res.Failures.Count > 0) msg += "\r\n\r\n失败原因：\r\n· " + string.Join("\r\n· ", res.Failures.ToArray());
                LastResult = msg;
                // 结果进窗口里的列表（不再弹框），点一条跳到那个孔
                var items = new List<ResultList.Item>(res.Outcomes);
                foreach (var f in res.Features) items.Add(new ResultList.Item(ResultList.Kind.Info, "特征树：" + f));
                if (res.Audit.Length > 0) items.Add(new ResultList.Item(ResultList.Kind.Fail, "自检：" + res.Audit));
                foreach (var n in res.Notes) items.Add(new ResultList.Item(ResultList.Kind.Info, n));
                if (res.Created > 0) items.Add(new ResultList.Item(ResultList.Kind.Info, what + "　·　" + res.Method + "　·　记得保存装配，排错了点「撤销本次打孔」。"));
                string head = "排孔 " + res.Created + " / " + res.Requested + " 个" + (res.AlreadyOk > 0 ? " · 本来就有孔 " + res.AlreadyOk : "") + (res.Failures.Count > 0 ? " · 失败 " + res.Failures.Count : "");
                results.SetResult(head, res.Failures.Count > 0 || res.Audit.Length > 0 ? Ui.Bad : (res.Created > 0 ? Ui.Ok : Ui.Warn), items);
                resultCard.Visible = results.Count > 0; rootStack.Relayout();
                lastUndo = res.Undo;
                undoButton.Enabled = !res.Undo.IsEmpty;
                lastCreated = res.Undo.Created();
                editButton.Enabled = lastCreated.Count > 0;
                SetStatus("完成：" + res.Created + " 个孔" + (res.Failures.Count > 0 ? "，失败 " + res.Failures.Count : ""), res.Failures.Count > 0 ? Ui.Warn : Ui.Ok);
                try { if (target != null && target.Part != null) HoleScanCad.Invalidate(target.Part); } catch { }
                busy = false; run.Text = "开始打孔"; UseWaitCursor = false;
                if (hadCommand) { try { StartPicking(); } catch (Exception ex) { SetStatus(ex.Message, Ui.Warn); } }
                RefreshPreview();
            } catch (Exception e) {
                Log.Write("AutoHolePatternRun", e);
                busy = false; run.Text = "开始打孔"; UseWaitCursor = false;
                try { if (hadCommand && command == null) StartPicking(); } catch { }
                SetStatus(e.Message, Ui.Bad);
                LastResult = "排孔中断：" + e.Message;
                if (!SuppressDialogs) MessageBox.Show(this, e.Message, "批量排孔", MessageBoxButtons.OK, MessageBoxIcon.Error);
                RefreshPreview();
            }
        }

        // 撤销上一次「开始打孔」（HoleUndo：删掉那一批孔/腰孔，编组和改名恢复原样）
        void UndoLastBatch(){
            if (busy || closing || lastUndo == null || lastUndo.IsEmpty) return;
            if (!SuppressDialogs && MessageBox.Show(this, "删掉上一次排的 " + lastUndo.HoleCount + " 个孔？\r\n（编组、改名会一并恢复）",
                    "撤销本次打孔", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            bool hadCmd = command != null;
            busy = true; UseWaitCursor = true; undoButton.Enabled = false;
            StopCommand();
            try {
                var outcome = lastUndo.Run();
                lastUndo = null;
                lastCreated = null; editButton.Enabled = false;
                foreach (var part in outcome.Parts) { try { HoleScanCad.Invalidate(part); } catch { } }
                var items = new List<ResultList.Item>();
                foreach (var n in outcome.Notes) items.Add(new ResultList.Item(ResultList.Kind.Info, n));
                results.SetResult(outcome.Summary, outcome.Failed > 0 ? Ui.Warn : Ui.Ok, items);
                resultCard.Visible = true; rootStack.Relayout();
                LastResult = outcome.Summary;
                SetStatus(outcome.Summary, outcome.Failed > 0 ? Ui.Warn : Ui.Ok);
            } catch (Exception e) {
                Log.Write("AutoHolePatternUndo", e);
                SetStatus("撤销没有完成：" + e.Message, Ui.Bad);
                LastResult = "撤销中断：" + e.Message;
            } finally {
                busy = false; UseWaitCursor = false;
                if (hadCmd) { try { StartPicking(); } catch (Exception ex) { SetStatus(ex.Message, Ui.Warn); } }
                RefreshPreview();
            }
        }
        void OpenEdit(){
            if (busy || closing || lastCreated == null || lastCreated.Count == 0) return;
            handingOver = command != null;
            try { AutoHoleModule.OpenEdit(app, assembly, lastCreated); }
            catch (Exception e) { handingOver = false; Log.Write("AutoHolePatternOpenEdit", e); SetStatus("打不开改孔型窗口：" + e.Message, Ui.Bad); }
        }
        public List<KeyValuePair<P.PartDocument, string>> LastCreatedForTest { get { return lastCreated; } }
        public bool AsPattern { get { return asPattern.Checked; } set { asPattern.Checked = value; } }
        public bool SlotForTest { get { return slotCheck.Checked; } set { slotCheck.Checked = value; } }
        public void UndoForTest(){ UndoLastBatch(); }
        public bool CanUndo { get { return lastUndo != null && !lastUndo.IsEmpty; } }

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
        // 别的命令接管了鼠标：一般就关窗口（和原来一样）。但如果是我们自己点「改孔型…」开的窗口接管的，
        // 这个窗口说好了要留着（参考孔、规格都还在）—— 原来照样被关掉。现在只是暂停选择，用户回到这个窗口时自动接着选
        bool handingOver, paused;
        public void Terminate(){
            if (closing || busy) return;
            if (handingOver) { handingOver = false; paused = true; BeginInvoke((Action)(() => { StopCommand(); SetStatus("选择已暂停（改孔型窗口在用鼠标）。回到这个窗口点一下就接着选。", Ui.Muted); })); return; }
            BeginInvoke((Action)(() => Close()));
        }
        protected override void OnActivated(EventArgs e){
            base.OnActivated(e);
            if (!paused || closing || busy || command != null) return;
            paused = false;
            try { StartPicking(); SetStatus("接着选。", Ui.Muted); } catch (Exception ex) { SetStatus(ex.Message, Ui.Warn); }
        }
        public void Idle(int n, out bool more){ more = false; }
        void F.ISECommandEvents.Activate(){ }
        void F.ISECommandEvents.Deactivate(){ }
        public new void KeyDown(ref short key, short shift){ if (key == 27) Close(); }
        public new void KeyPress(ref short key){ }
        public new void KeyUp(ref short key, short shift){ }
    }
}
