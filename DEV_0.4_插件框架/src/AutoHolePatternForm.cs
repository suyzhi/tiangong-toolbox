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
    public sealed class AutoHolePatternForm : Form, F.ISEMouseEvents, F.ISECommandEvents {
        readonly F.Application app;
        readonly A.AssemblyDocument assembly;
        F.ISECommand command; F.ISEMouse mouse; Connection mouseConnection, commandConnection;
        F.HighlightSet highlights;

        object faceSelection, dirSelection;
        TargetFace target; FaceFrame frame; string faceText = "";
        V3 circleCentre; double circleRadiusMm; bool circleReady;
        bool busy, closing, loading;

        readonly RadioButton modeDivide = new RadioButton { Text = "沿线等分" };
        readonly RadioButton modePitch = new RadioButton { Text = "沿线定距" };
        readonly RadioButton modeCircle = new RadioButton { Text = "圆周均布" };
        readonly NumericUpDown count = Ui.Num(1, 720, 4, 0, 1, 60);
        readonly NumericUpDown pitch = Ui.Num(0.5M, 5000M, 50, 1, 5, 70);
        readonly NumericUpDown edge = Ui.Num(0, 5000, 10, 1, 5, 70);
        readonly NumericUpDown startAngle = Ui.Num(-360, 360, 0, 1, 15, 70);
        readonly ComboBox kindBox = Ui.Combo(104, "通孔", "螺纹孔", "圆柱沉孔", "锥形沉孔");
        readonly ComboBox sizeBox = Ui.Combo(84, HoleMatcher.Table.Select(r => r.Size).ToArray());
        readonly NumericUpDown diaNum = Ui.Num(0.1M, 500M, 9M, 2, 0.1M, 74);
        readonly NumericUpDown depthNum = Ui.Num(0.1M, 2000M, 10M, 2, 1M, 74);
        readonly CheckBox throughCheck = new CheckBox { Text = "贯通", Checked = true };   // 默认贯通：不勾会默认打成盲孔
        readonly CheckBox depthCheck = new CheckBox { Text = "盲孔" };
        readonly CheckBox slotCheck = new CheckBox { Text = "腰孔" };
        readonly NumericUpDown slotLen = Ui.Num(2M, 2000M, 30M, 1, 5M, 70);
        readonly NumericUpDown slotWidth = Ui.Num(0.5M, 500M, 9M, 2, 0.5M, 70);
        readonly Label faceInfo = new Label();
        readonly Label previewText = new Label();
        readonly Label status = new Label();
        readonly Button run = Ui.Primary("开始打孔", 168, 38);
        readonly List<string> problems = new List<string>();

        public AutoHolePatternForm(F.Application application, A.AssemblyDocument document){
            app = application; assembly = document;
            Ui.Shell(this, "批量排孔", 620, 740, 560, 640);
            BuildLayout();
            WireEvents();
            sizeBox.SelectedIndex = 3;   // M6
            RefreshPreview();
            FormClosed += (s, e) => Cleanup();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData){
            if (keyData == Keys.Escape) { Close(); return true; }
            if (keyData == (Keys.Control | Keys.Enter)) { Run(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        void BuildLayout(){
            var step1 = Ui.Step(1, "点面", "点要排孔的那个平面；圆周均布时再点一条圆形边线定位圆心");
            var faceCard = Ui.Card("打孔面", 92, "");
            faceInfo.Dock = DockStyle.Fill; faceInfo.AutoSize = false; faceInfo.ForeColor = Ui.Text;
            faceInfo.Padding = new Padding(0, 4, 0, 0);
            var reselect = Ui.Secondary("重选", 76, 26); reselect.Dock = DockStyle.Right;
            reselect.Click += (s, e) => { faceSelection = null; dirSelection = null; target = null; frame = null; circleReady = false; faceText = ""; ConfigureFilter(); RefreshPreview(); SetStatus("重新点一个面。", Ui.Accent); };
            faceCard.Controls.Add(faceInfo); faceCard.Controls.Add(reselect);

            var step2 = Ui.Step(2, "排布", "选一种排法，填孔数或间距；孔心默认居中");
            var modeCard = Ui.Card("排布方式", 106, "");
            var modeRow = new Panel { Dock = DockStyle.Top, Height = 30, BackColor = Color.White };
            int x = 0;
            foreach (var rb in new Control[]{ modeDivide, modePitch, modeCircle }) {
                rb.AutoSize = true; rb.SetBounds(x, 4, 96, 22); x += 104; modeRow.Controls.Add(rb);
            }
            modeDivide.Checked = true;
            var numRow = new Panel { Dock = DockStyle.Top, Height = 34, BackColor = Color.White };
            int nx = AddField(numRow, "孔数", count, 0);
            nx = AddField(numRow, "间距", pitch, nx);
            nx = AddField(numRow, "边距", edge, nx);
            AddField(numRow, "起始角", startAngle, nx);
            modeCard.Controls.Add(numRow); modeCard.Controls.Add(modeRow);

            var step3 = Ui.Step(3, "定孔型", "孔型和规格；勾「腰孔」排长圆孔（只对沿线模式有效）");
            var specCard = Ui.Card("孔型与尺寸", 116, "");
            var row1 = new Panel { Dock = DockStyle.Top, Height = 32, BackColor = Color.White };
            int sx = AddField(row1, "孔型", kindBox, 0);
            sx = AddField(row1, "规格", sizeBox, sx);
            AddField(row1, "孔径", diaNum, sx);
            var row2 = new Panel { Dock = DockStyle.Top, Height = 34, BackColor = Color.White };
            throughCheck.SetBounds(2, 6, 58, 22); depthCheck.SetBounds(64, 6, 62, 22); depthNum.SetBounds(132, 3, 74, 24);
            row2.Controls.Add(throughCheck); row2.Controls.Add(depthCheck); row2.Controls.Add(depthNum);
            slotCheck.SetBounds(224, 6, 62, 22); row2.Controls.Add(slotCheck);
            slotLen.SetBounds(292, 3, 70, 24); row2.Controls.Add(slotLen);
            slotWidth.SetBounds(370, 3, 70, 24); row2.Controls.Add(slotWidth);
            var h = Ui.Muted8("腰孔：总长 / 槽宽"); h.Location = new Point(450, 9); row2.Controls.Add(h);
            specCard.Controls.Add(row2); specCard.Controls.Add(row1);

            var previewCard = Ui.Card("预览", 190, "");
            previewText.Dock = DockStyle.Top; previewText.Height = 40; previewText.AutoSize = false; previewText.Font = Ui.F9B;
            var canvas = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
            canvas.Paint += DrawPreview;
            previewCard.Controls.Add(canvas); previewCard.Controls.Add(previewText);
            canvas.Resize += (s, e) => canvas.Invalidate();
            this.previewCanvas = canvas;

            var actions = new Panel { Height = 56, BackColor = Ui.Bg, Padding = new Padding(0, 8, 0, 8) };
            run.SetBounds(0, 8, 168, 38);
            var close = Ui.Secondary("关闭 (Esc)", 96, 30); close.SetBounds(178, 12, 96, 30);
            close.Click += (s, e) => Close();
            run.Click += (s, e) => Run();
            actions.Controls.Add(run); actions.Controls.Add(close);

            status.Height = 26; status.Padding = new Padding(2, 0, 10, 0);
            status.TextAlign = ContentAlignment.MiddleLeft; status.ForeColor = Ui.Accent; status.BackColor = Ui.Bg;

            // 纵向堆叠：添加顺序 = 从上到下。示意图那张卡吃掉剩余高度。
            var stack = new VerticalStack { Dock = DockStyle.Fill };
            stack.Add(step1); stack.Add(faceCard); stack.Add(step2); stack.Add(modeCard);
            stack.Add(step3); stack.Add(specCard); stack.Add(previewCard, true); stack.Add(actions); stack.Add(status);
            Controls.Add(stack);
        }

        Panel previewCanvas;

        // 一列一列往后排：标签宽度按实际文字量，不再出现"标签被下拉框盖住"。
        static int AddField(Panel parent, string label, Control field, int x){
            var l = new Label { Text = label, AutoSize = true, Location = new Point(x + 2, 7), BackColor = Color.Transparent };
            parent.Controls.Add(l);
            int lw = TextRenderer.MeasureText(label, Ui.F9).Width;
            int fx = x + 6 + lw + 6;
            field.SetBounds(fx, 3, field.Width, 24);
            parent.Controls.Add(field);
            return fx + field.Width + 18;
        }

        void WireEvents(){
            foreach (var rb in new[]{ modeDivide, modePitch, modeCircle })
                rb.CheckedChanged += (s, e) => { if (rb.Checked && !loading) { ConfigureFilter(); RefreshPreview(); } };
            kindBox.SelectedIndexChanged += (s, e) => { if (!loading) ApplyKind(); };
            sizeBox.SelectedIndexChanged += (s, e) => { if (!loading) ApplySize(); };
            foreach (var n in new Control[]{ count, pitch, edge, startAngle, diaNum, depthNum, slotLen, slotWidth })
                ((NumericUpDown)n).ValueChanged += (s, e) => { if (!loading) RefreshPreview(); };
            throughCheck.CheckedChanged += (s, e) => { if (loading) return; loading = true; depthCheck.Checked = !throughCheck.Checked; loading = false; RefreshPreview(); };
            depthCheck.CheckedChanged += (s, e) => { if (loading) return; loading = true; throughCheck.Checked = !depthCheck.Checked; loading = false; RefreshPreview(); };
            slotCheck.CheckedChanged += (s, e) => RefreshPreview();
        }

        void ApplyKind(){
            HoleKind k;
            switch (kindBox.SelectedIndex) { case 1: k = HoleKind.Tapped; break; case 2: k = HoleKind.Counterbore; break; case 3: k = HoleKind.Countersink; break; default: k = HoleKind.Through; break; }
            var row = HoleMatcher.Find(sizeBox.SelectedItem == null ? "M6" : sizeBox.SelectedItem.ToString());
            var sp = HoleMatcher.FromRow(row.HasValue ? row.Value : HoleMatcher.Table[3], k);
            loading = true;
            try { diaNum.Value = Ui.Clamp((decimal)Math.Round(sp.HoleDiameter, 2), diaNum.Minimum, diaNum.Maximum); } finally { loading = false; }
            RefreshPreview();
        }
        void ApplySize(){
            var row = HoleMatcher.Find(sizeBox.SelectedItem == null ? "M6" : sizeBox.SelectedItem.ToString());
            if (!row.HasValue) return;
            HoleKind k;
            switch (kindBox.SelectedIndex) { case 1: k = HoleKind.Tapped; break; case 2: k = HoleKind.Counterbore; break; case 3: k = HoleKind.Countersink; break; default: k = HoleKind.Through; break; }
            var sp = HoleMatcher.FromRow(row.Value, k);
            loading = true;
            try { diaNum.Value = Ui.Clamp((decimal)Math.Round(sp.HoleDiameter, 2), diaNum.Minimum, diaNum.Maximum); } finally { loading = false; }
            RefreshPreview();
        }

        HolePatternKind Kind { get { return modeCircle.Checked ? HolePatternKind.Circular : (modePitch.Checked ? HolePatternKind.Pitch : HolePatternKind.Divide); } }

        HolePatternSpec Spec(){
            var s = new HolePatternSpec { Kind = Kind };
            s.Count = (int)count.Value;
            s.PitchMm = (double)pitch.Value;
            s.EdgeMm = (double)edge.Value;
            s.StartAngleDeg = (double)startAngle.Value;
            return s;
        }

        HoleSpec HoleSpecNow(){
            var row = HoleMatcher.Find(sizeBox.SelectedItem == null ? "M6" : sizeBox.SelectedItem.ToString());
            HoleKind k;
            switch (kindBox.SelectedIndex) { case 1: k = HoleKind.Tapped; break; case 2: k = HoleKind.Counterbore; break; case 3: k = HoleKind.Countersink; break; default: k = HoleKind.Through; break; }
            var sp = HoleMatcher.FromRow(row.HasValue ? row.Value : HoleMatcher.Table[3], k);
            sp.HoleDiameter = (double)diaNum.Value;
            sp.Depth = throughCheck.Checked ? 0 : (double)depthNum.Value;
            return sp;
        }

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
                // 原来这里必须先有 target 才认边，而选完面之后过滤器还卡在"只能点面"，
                // 于是"手动指定方向边"这条路根本点不到 —— 用户只会看到点面又换了个面。
                var pg = PickGeometry.Unwrap(selected);
                bool isFace = pg.Geometry is G.Face;
                if (isFace || target == null) {
                    if (!isFace) throw new ArgumentException("请先点一个平面作为打孔面。");
                    var fresh = AutoHoleReader.ReadTarget(selected);
                    bool sameFace = target != null && target.Part == fresh.Part && target.Face != null && fresh.Face != null && target.Face.ID == fresh.Face.ID;
                    target = fresh; faceSelection = selected;
                    dirSelection = null; circleReady = false;
                    if (sameFace) {
                        // 同一个面再点一次 = 沿用原来选的圆边/方向边，不当成"新选面"清掉
                        SetStatus("还是这个面。" + frame.Label, Ui.Accent);
                    } else {
                        frame = FaceFrameReader.Read(target);   // 自动取最长边
                        faceText = AutoHoleFormFaceLabel(target) + "　方向：" + frame.Label;
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
                    faceText = AutoHoleFormFaceLabel(target) + "　方向：" + frame.Label;
                    SetStatus("已指定方向边：" + Num(frame.LengthMm) + "mm。", Ui.Ok);
                }
                ConfigureFilter();
                RefreshPreview();
            } catch (Exception e) { SetStatus(e.Message, Ui.Warn); Log.Write("AutoHolePatternPick", e); }
        }

        static string AutoHoleFormFaceLabel(TargetFace t){
            return (t.PartName != null && t.PartName.Length > 0 ? t.PartName : "零件") + "　" + t.Label;
        }
        static string Num(double v){ return v.ToString("0.##", CultureInfo.InvariantCulture); }

        // ---------------- 预览 ----------------
        void RefreshPreview(){
            count.Enabled = Kind != HolePatternKind.Pitch;
            pitch.Enabled = Kind == HolePatternKind.Pitch;
            edge.Enabled = Kind != HolePatternKind.Circular;
            startAngle.Enabled = Kind == HolePatternKind.Circular;
            depthNum.Enabled = depthCheck.Checked;
            slotLen.Enabled = slotCheck.Checked;
            slotWidth.Enabled = slotCheck.Checked;
            faceInfo.Text = target == null
                ? "还没有选面。把鼠标移到零件平面上点一下。"
                : faceText + (Kind == HolePatternKind.Circular && !circleReady ? "\r\n再点一条圆形边线来定位圆心和半径。" : "");
            faceInfo.ForeColor = target == null ? Ui.Muted : Ui.Text;

            bool ok = false;
            try {
                if (target == null) { previewText.Text = "选一个面，这里会画出排孔示意。"; previewText.ForeColor = Ui.Muted; }
                else if (Kind == HolePatternKind.Circular) {
                    if (!circleReady) { previewText.Text = "圆周均布：再点一条圆形边线（分度圆）。"; previewText.ForeColor = Ui.Warn; }
                    else {
                        var a = HolePatternSolver.Angles(Spec());
                        previewText.Text = "在 R" + Num(circleRadiusMm) + " 的分度圆上均布 " + a.Length + " 个孔，起始 " + Num(Spec().StartAngleDeg) + "°，间隔 " + Num(360.0 / Spec().Count) + "°。";
                        previewText.ForeColor = Ui.Ok; ok = true;
                    }
                } else {
                    var o = HolePatternSolver.Offsets(frame.LengthMm, Spec());
                    var hs = HoleSpecNow();
                    previewText.Text = HolePatternSolver.Describe(frame.LengthMm, Spec()) + "\r\n" + hs.Summary + (slotCheck.Checked ? "　→　腰孔 " + Num((double)slotLen.Value) + "×" + Num((double)slotWidth.Value) : "");
                    previewText.ForeColor = Ui.Ok; ok = true;
                }
            } catch (Exception e) { previewText.Text = e.Message; previewText.ForeColor = Ui.Bad; }
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

        // 排孔示意图：面轮廓 + 每个孔的位置（腰孔画成长圆）。
        void DrawPreview(object sender, PaintEventArgs e){
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var area = previewCanvas.ClientRectangle;
            if (area.Width < 40 || area.Height < 20) return;
            using (var pen = new Pen(Ui.Line))
            using (var accent = new Pen(Ui.Accent, 1.6f))
            using (var dot = new SolidBrush(Ui.Accent))
            using (var dash = new Pen(Ui.Accent, 1f) { DashStyle = DashStyle.Dash })
            try {
                if (target == null || frame == null) {
                    TextRenderer.DrawText(g, "（还没有选面）", Ui.F9, area, Ui.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    return;
                }
                var spec = Spec();
                var hs = HoleSpecNow();
                bool slot = slotCheck.Checked && Kind != HolePatternKind.Circular;
                var centres = new List<PointF>();
                double spanX, spanY;
                if (Kind == HolePatternKind.Circular) {
                    if (!circleReady) { TextRenderer.DrawText(g, "（等一条圆形边线）", Ui.F9, area, Ui.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter); return; }
                    spanX = spanY = circleRadiusMm * 2 * 1.25;
                    var angs = HolePatternSolver.Angles(spec);
                    foreach (var deg in angs) {
                        double rad = deg * Math.PI / 180.0;
                        centres.Add(new PointF((float)(Math.Cos(rad) * circleRadiusMm), (float)(Math.Sin(rad) * circleRadiusMm)));
                    }
                } else {
                    var offs = HolePatternSolver.Offsets(frame.LengthMm, spec);
                    spanX = frame.LengthMm * 1.1; spanY = Math.Max(frame.WidthMm, hs.HoleDiameter * 4) * 1.6;
                    foreach (var o in offs) centres.Add(new PointF((float)(o - frame.LengthMm / 2.0), 0));
                }
                if (spanX <= 0 || spanY <= 0) return;
                float pad = 16;
                float scale = Math.Min((area.Width - 2 * pad) / (float)spanX, (area.Height - 2 * pad) / (float)spanY);
                if (scale <= 0) return;
                float cx = area.Width / 2f, cy = area.Height / 2f;
                // 面轮廓
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
                        g.FillPath(dot, Ui.Rounded(new Rectangle((int)(px - sl / 2), (int)(py - sw), (int)sl, (int)(sw * 2)), (int)sw));
                    } else {
                        g.FillEllipse(dot, px - hr, py - hr, hr * 2, hr * 2);
                    }
                }
                g.DrawLine(accent, cx - w / 2 - 6, cy, cx + w / 2 + 6, cy);
            } finally { }
        }

        void SetStatus(string text, Color color){ status.Text = text; status.ForeColor = color; }

        // ---------------- 打孔 ----------------
        void Run(){
            if (busy || closing) return;
            try {
                if (assembly.ReadOnly || assembly.InPlaceActivated) throw new InvalidOperationException("装配不可编辑或处于原位编辑中。");
                if (faceSelection != null) target = AutoHoleReader.ReadTarget(faceSelection);   // 打完孔后缓存的面会失效，重解析一次
                if (target == null) throw new InvalidOperationException("请先点一个要打孔的面。");
                var spec = Spec();
                var hs = HoleSpecNow();
                var centres = new List<V3>();
                var dirs = new List<V3>();
                if (Kind == HolePatternKind.Circular) {
                    if (!circleReady) throw new InvalidOperationException("圆周均布需要先点一条圆形边线。");
                    var angles = HolePatternSolver.Angles(spec);
                    var u = frame.Direction; var v = frame.Perp;
                    foreach (var deg in angles) {
                        double rad = deg * Math.PI / 180.0;
                        centres.Add(circleCentre + u * (circleRadiusMm / 1000.0 * Math.Cos(rad)) + v * (circleRadiusMm / 1000.0 * Math.Sin(rad)));
                        dirs.Add(u);
                    }
                } else {
                    var offsets = HolePatternSolver.Offsets(frame.LengthMm, spec);
                    foreach (var o in offsets) { centres.Add(frame.At(o)); dirs.Add(frame.Direction); }
                }

                busy = true; run.Enabled = false; run.Text = "打孔中…"; UseWaitCursor = true; StopCommand();
                DrillResult res;
                if (slotCheck.Checked && Kind != HolePatternKind.Circular) {
                    var localDir = target.Placement.InverseNormal(frame.Direction);
                    var lc = new List<V3>(); var ld = new List<V3>();
                    foreach (var c in centres) { lc.Add(target.Placement.InversePoint(c)); ld.Add(localDir); }
                    res = AutoHoleWriter.DrillSlots(target, lc, ld, (double)slotLen.Value, (double)slotWidth.Value);
                    if (res.Method.Length == 0) res.Method = "腰孔（通切）";
                } else {
                    res = AutoHoleWriter.Drill(target, hs, centres);
                }
                string msg = "排孔完成：" + res.Created + " / " + res.Requested + " 个\r\n" + hs.Summary + "\r\n生成方式：" + res.Method + "\r\n\r\n请保存装配。";
                if (res.Audit.Length > 0) msg += "\r\n\r\n自检：" + res.Audit;
                if (res.Failures.Count > 0) msg += "\r\n\r\n失败原因：\r\n· " + string.Join("\r\n· ", res.Failures.ToArray());
                MessageBox.Show(this, msg, "批量排孔", MessageBoxButtons.OK,
                    (res.Failures.Count > 0 || res.Audit.Length > 0) ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
                SetStatus("完成：" + res.Created + " 个孔" + (res.Failures.Count > 0 ? "，失败 " + res.Failures.Count : ""), res.Failures.Count > 0 ? Ui.Warn : Ui.Ok);
                busy = false; run.Text = "开始打孔"; UseWaitCursor = false;
                try { StartPicking(); } catch (Exception ex) { SetStatus(ex.Message, Ui.Warn); }
                RefreshPreview();
            } catch (Exception e) {
                Log.Write("AutoHolePatternRun", e);
                busy = false; run.Text = "开始打孔"; UseWaitCursor = false;
                try { if (command == null) StartPicking(); } catch { }
                SetStatus(e.Message, Ui.Bad);
                MessageBox.Show(this, e.Message, "批量排孔", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        void StopCommand(){
            if (highlights != null) { try { highlights.Delete(); } catch { } highlights = null; }
            if (mouseConnection != null) { mouseConnection.Dispose(); mouseConnection = null; }
            if (commandConnection != null) { commandConnection.Dispose(); commandConnection = null; }
            var c = command; command = null; mouse = null;
            if (c != null) try { c.Done = true; } catch { }
        }
        void Cleanup(){ if (closing) return; closing = true; StopCommand(); }

        public new void MouseClick(short b, short s, double x, double y, double z, object w, int k, object g){
            if (b == 1) { if (g == null) { SetStatus("这里没有捕捉到对象。", Ui.Warn); return; } AcceptPick(g); }
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
