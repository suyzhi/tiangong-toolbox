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
    public sealed partial class AutoHolePatternForm {
        // ---------------- 布局 ----------------
        // 0.9.0「D · 侧栏向导」：① 打孔面 → ② 排布（右边实时排孔示意）→ ③ 孔规格 → ④ 预览并打孔（大示意图 + 结果）。
        // 预设放在侧栏底部的小卡片里；点了面自动翻到「排布」。
        const int PageFace = 0, PageLayout = 1, PageSpec = 2, PagePreview = 3;
        readonly WizardShell shell = new WizardShell();
        readonly FlatButton nextButton = (FlatButton)Ui.Primary("下一步", 220, 44);
        readonly FlatButton backButton = (FlatButton)Ui.Secondary("上一步", 84, 38);
        readonly FlatButton moreButton = (FlatButton)Ui.Secondary("更多 ▾", 84, 38);
        readonly EmptyHint faceEmpty = new EmptyHint("正在等你在模型里点要排孔的平面…");
        Panel previewCanvas2;
        int lastHoles;
        bool drilledOnce;

        void BuildLayout(){
            Controls.Add(shell);
            var pFace = shell.AddPage("打孔面", "打孔面", "");
            var pLayout = shell.AddPage("排布", "排布", "");
            var pSpec = shell.AddPage("孔规格", "孔规格", "");
            var pPrev = shell.AddPage("预览并打孔", "预览并打孔", "");

            // ---- ① 打孔面 ----
            var reselect = Ui.Small("重选");
            ((FlatButton)reselect).SetFore(Ui.Text2);
            reselect.Click += (s, e) => { if (busy) return; faceSelection = null; dirSelection = null; target = null; frame = null; circleReady = false; faceText = ""; ConfigureFilter(); RefreshPreview(); SetStatus("重新点一个面。", Ui.Accent); };
            shell.AddHeader(pFace, reselect);
            var fstack = new VerticalStack { Dock = DockStyle.Fill, Padding = new Padding(0), Gap = Ui.S(10), AutoScroll = false };
            pFace.Body.Controls.Add(fstack);
            fstack.Add(new Label { Text = "点要排孔的那个零件平面。圆周均布时再点一条圆形边线定圆心和半径；沿线排布时可以再点一条直线边指定方向。", AutoSize = false, Height = Ui.S(50), ForeColor = Ui.Text2, BackColor = Ui.Bg });
            fstack.Add(faceEmpty);
            var faceCard = faceCardD = Ui.Card("", 86, "");
            faceCard.Padding = Ui.SPad(16, 12, 16, 12);
            faceInfo.Dock = DockStyle.Fill; faceInfo.AutoSize = false; faceInfo.ForeColor = Ui.Text; faceInfo.BackColor = Color.White;
            faceInfo.AutoEllipsis = true;
            faceCard.Controls.Add(faceInfo);
            fstack.Add(faceCard);

            // ---- ② 排布：左参数，右示意 ----
            var lcols = new SplitColumns(0.6f) { Gap = Ui.S(20) };   // 0.56 时窗口一缩小，「末端 … mm」的单位被截掉
            var lb = new Panel { BackColor = Ui.Bg };
            layoutGrid.BackColor = Ui.Bg;
            lb.Controls.Add(layoutGrid);
            modeSeg.Width = Math.Max(Ui.S(300), modeSeg.PreferredWidth);
            layoutGrid.Add("方式", modeSeg);
            countRow = layoutGrid.Add("孔数", count, FieldGrid.Unit("个"));
            pitchRow = layoutGrid.Add("间距", pitch, FieldGrid.Unit("mm"));
            edgeRow = layoutGrid.Add("边距", Ui.Caption("起端"), edge, FieldGrid.Unit("mm"), FieldGrid.Space(8), Ui.Caption("末端"), endEdge, FieldGrid.Unit("mm"));
            var sameRow = layoutGrid.Add("", sameEdge);
            latMode.Width = Math.Max(Ui.S(170), latMode.PreferredWidth);
            lateralRow = layoutGrid.Add("横向", latMode, lateral, FieldGrid.Unit("mm"));
            angleRow = layoutGrid.Add("起始角", startAngle, FieldGrid.Unit("°"));
            sameEdgeRow = sameRow;
            tips.SetToolTip(edge, "第一个孔离起点那条边的距离（起点在示意图左端）。");
            tips.SetToolTip(endEdge, "最后一个孔离终点那条边的距离。定距排孔排不满时，末端实际会多出一点，预览里会写。");
            tips.SetToolTip(lateral, "孔心离方向边的距离（往面里面量）。方向边：自动时是面上最长的那条边，也可以在模型上点一条直线边指定（会高亮）。");
            var dirHint = new Label { Text = "方向默认取面上最长的边；在模型里点另一条直线边可以换方向。", AutoSize = false, ForeColor = Ui.Muted, Font = Ui.F8, BackColor = Ui.Bg };
            lb.Controls.Add(dirHint);
            lb.Resize += (s, e) => { layoutGrid.SetBounds(0, 0, Math.Max(Ui.S(80), lb.ClientSize.Width), layoutGrid.PreferredHeight); dirHint.SetBounds(0, layoutGrid.PreferredHeight + Ui.S(6), lb.ClientSize.Width, Ui.S(42)); };
            layoutGrid.Resize += (s, e) => dirHint.Top = layoutGrid.Bottom + Ui.S(6);
            var sketch = Ui.Card("排孔示意", 0, "改了数字马上重画");
            sketch.Fill = true; sketch.Padding = Ui.SPad(10, 38, 10, 10);
            var canvas = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Rail };
            canvas.Paint += DrawPreview;
            canvas.Resize += (s, e) => canvas.Invalidate();
            sketch.Controls.Add(canvas);
            previewCanvas = canvas;
            lcols.Add(lb); lcols.Add(sketch);
            lcols.Dock = DockStyle.Fill;
            pLayout.Body.Controls.Add(lcols);

            // ---- ③ 孔规格 ----
            var sb = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Bg };
            pSpec.Body.Controls.Add(sb);
            specGrid.BackColor = Ui.Bg;
            sb.Controls.Add(specGrid);
            stdInfo.AutoSize = false; stdInfo.Font = Ui.F8; stdInfo.ForeColor = Ui.Muted; stdInfo.AutoEllipsis = true; stdInfo.BackColor = Ui.Bg;
            sb.Controls.Add(stdInfo);
            kindSeg.Width = Math.Max(Ui.S(340), kindSeg.PreferredWidth);
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
            sb.Layout += (s, e) => {
                int w = Math.Max(Ui.S(80), sb.ClientSize.Width);
                specGrid.SetBounds(0, 0, w, specGrid.PreferredHeight);
                stdInfo.SetBounds(0, specGrid.PreferredHeight + Ui.S(6), w, Ui.S(24));
            };

            // ---- ④ 预览并打孔 ----
            var pv = rootStack = new VerticalStack { Dock = DockStyle.Fill, Padding = new Padding(0), Gap = Ui.S(12), AutoScroll = false };
            pPrev.Body.Controls.Add(pv);
            previewText.Height = Ui.S(72); previewText.AutoSize = false; previewText.Font = Ui.F9B;
            previewText.AutoEllipsis = true; previewText.BackColor = Ui.Bg;
            var big = Ui.Card("排孔示意", 0, "");
            big.Fill = true; big.Padding = Ui.SPad(10, 38, 10, 10);
            var canvas2 = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Rail };
            canvas2.Paint += DrawPreview;
            canvas2.Resize += (s, e) => canvas2.Invalidate();
            big.Controls.Add(canvas2);
            previewCanvas2 = canvas2;

            // 打孔结果（打完才出现）：失败/跳过/打好的孔逐条列出，点一条跳过去
            resultCard = Ui.Card("本次结果", 190, "点一条跳到那个孔");
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
            pv.Add(previewText); pv.Add(big, true); pv.Add(resultCard);

            // ---- 侧栏底部：预设 ----
            var presetCard = new RailCard("预设", 120);
            var savePreset = Ui.Small("存为预设"); var delPreset = Ui.Small("删除");
            ((FlatButton)delPreset).SetFore(Ui.Muted);
            presetBox.Font = Ui.F8;
            savePreset.Click += (s, e) => SavePreset();
            delPreset.Click += (s, e) => DeletePreset();
            presetBox.SelectedIndexChanged += (s, e) => { if (!loading && presetBox.SelectedIndex > 0) ApplyPreset(presetBox.SelectedItem.ToString()); };
            tips.SetToolTip(savePreset, "把现在这套排布 + 孔规格存个名字，下次在下拉里一点就填好。");
            presetCard.Controls.Add(presetBox); presetCard.Controls.Add(savePreset); presetCard.Controls.Add(delPreset);
            presetCard.Resize += (s, e) => {
                presetBox.SetBounds(Ui.S(12), Ui.S(36), Math.Max(40, presetCard.Width - Ui.S(24)), Ui.S(28));
                savePreset.Location = new Point(Ui.S(6), presetCard.Height - Ui.S(34));
                delPreset.Location = new Point(savePreset.Right + Ui.S(2), presetCard.Height - Ui.S(34));
            };
            shell.RailBottom = presetCard;

            // ---- 底部操作栏 ----
            var more = new ContextMenuStrip { ShowImageMargin = false, Font = Ui.F9 };
            var miEdit = more.Items.Add("改孔型…（刚排的这批孔整组换孔型）");
            var miUndo = more.Items.Add("撤销本次打孔");
            miEdit.Click += (s, e) => OpenEdit();
            miUndo.Click += (s, e) => UndoLastBatch();
            more.Opening += (s, e) => { miEdit.Enabled = editButton.Enabled && !busy; miUndo.Enabled = undoButton.Enabled && !busy; };
            moreButton.Click += (s, e) => more.Show(moreButton, new Point(0, -more.PreferredSize.Height - 4));
            backButton.Click += (s, e) => shell.Go(shell.Current - 1);
            nextButton.Click += (s, e) => shell.Go(shell.Current + 1);
            run.Click += (s, e) => Run();
            tips.SetToolTip(run, "Ctrl+Enter");
            undoButton.Enabled = false;
            undoButton.Click += (s, e) => UndoLastBatch();
            editButton.Enabled = false;
            editButton.Click += (s, e) => OpenEdit();
            shell.Footer.AddLeft(backButton); shell.Footer.AddLeft(moreButton);
            shell.Footer.AddRight(run); shell.Footer.AddRight(nextButton);
            shell.Footer.Status = status;
            status.ForeColor = Ui.Accent;
            status.Text = "点要排孔的那个平面。";
            status.TextChanged += (s, e) => tips.SetToolTip(status, status.Text);
            shell.PageChanged += (s, e) => UpdatePrimary();
            LoadPresets(null);
        }
        FieldGrid.Row sameEdgeRow;
        Card faceCardD;

        void UpdatePrimary(){
            int cur = shell.Current;
            backButton.Enabled = cur > 0 && !busy;
            bool canRun = run.Enabled;
            bool showRun = busy || canRun || cur >= PagePreview;
            run.Visible = showRun; nextButton.Visible = !showRun;
            if (!busy) { run.Text = canRun ? "开始打孔 " + lastHoles + " 个孔" : "开始打孔"; Ui.FitButton(run); }
            if (!showRun) { nextButton.Text = "下一步：" + shell.Pages[Math.Min(cur + 1, shell.Pages.Count - 1)].Title; Ui.FitButton(nextButton); }
            shell.Footer.PerformLayout();
        }

        void RefreshRail(bool ok, int holes, string problem){
            var k = Kind;
            string face = target == null ? "点要排孔的平面" : (target.PartName != null && target.PartName.Length > 0 ? target.PartName : "已选面")
                        + (frame != null && k != HolePatternKind.Circular ? " · " + Num(frame.LengthMm) + "×" + Num(frame.WidthMm) : "");
            shell.Rail.Set(PageFace, face, target != null, false);
            string lay = modeSeg.SelectedText + (k == HolePatternKind.Pitch ? " · 间距 " + Num((double)pitch.Value) : " · " + count.Value + " 个");
            shell.Rail.Set(PageLayout, lay, ok, false);
            string sp = SlotMode ? "腰孔 " + Num((double)slotLen.Value) + "×" + Num((double)slotWidth.Value) : ShortName(HoleSpecNow());
            shell.Rail.Set(PageSpec, problem.Length > 0 ? "规格有问题，打不了" : sp, problem.Length == 0, problem.Length > 0);
            shell.Rail.Set(PagePreview, ok ? "将打 " + holes + " 个孔" : (drilledOnce ? "已打完一批" : "确认后一次打完"), drilledOnce && !ok, false);
            faceEmpty.Visible = target == null;
            if (faceCardD != null) faceCardD.Visible = target != null;
            lastHoles = holes;
            UpdatePrimary();
        }
        static string ShortName(HoleSpec sp){ try { return sp == null ? "" : HoleNaming.Short(sp); } catch { return ""; } }

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
                // 和另一个已有预设重名：原来一声不响覆盖掉（0.8.5）。改的就是当前选中的那个时不问
                if (name != current && presets.Find(name) != null && !SuppressDialogs
                    && MessageBox.Show(this, "已经有一个叫「" + name + "」的预设，覆盖它吗？", "批量排孔", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
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
    }
}
