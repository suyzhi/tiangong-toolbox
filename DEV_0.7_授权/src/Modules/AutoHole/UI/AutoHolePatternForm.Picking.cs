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
        // ---------------- 选择 ----------------
        public void StartPicking(){
            try {
                if (command != null) StopCommand();   // 幂等：重复调用不会留下第二条命令
                if (assembly == null && partDoc == null) throw new InvalidOperationException("请先打开装配或零件文件。");
                if (assembly != null && (assembly.ReadOnly || assembly.InPlaceActivated)) throw new InvalidOperationException("请在可编辑的装配顶层运行。");
                if (partDoc != null && partDoc.ReadOnly) throw new InvalidOperationException("这个零件是只读打开的，改不了。请用可写方式打开。");
                command = (F.ISECommand)app.CreateCommand((int)SolidEdgeConstants.seCmdFlag.seNoDeactivate);
                commandConnection = new Connection(command, typeof(F.ISECommandEvents), this);
                command.Start();
                mouse = (F.ISEMouse)command.Mouse;
                mouseConnection = new Connection(mouse, typeof(F.ISEMouseEvents), this);
                mouse.ScaleMode = 1; mouse.WindowTypes = 1;
                // QuickPick（0.8.5，用户要的）：和天工自己的选择一样，右键 / 停一下会弹出光标下层层叠叠的面、边列表，
                // 点里面那个被挡住的就选中它（原来 seLocateSimple 只认最前面那层，右键还被拿去关窗口，遮挡面根本选不到）
                mouse.LocateMode = (int)SolidEdgeConstants.seLocateModes.seLocateQuickPick;
                // MouseMove 为空处理函数，关闭以消除三维视口每像素 COM 回调风暴
                mouse.EnabledMove = false;
                ((F.ISEMouseEx)mouse).InterDocumentLocate = assembly != null;
                ((F.ISEMouseEx2)mouse).LocateFrontToBack = true;
                ((F.ISEMouseEx3)mouse).PathfinderLocate = false;
                ConfigureFilter();
                if (highlights != null) highlights.Delete();
                highlights = assembly != null ? assembly.HighlightSets.Add() : partDoc.HighlightSets.Add();
                highlights.Color = ColorTranslator.ToOle(Color.DeepSkyBlue);
            } catch { Cleanup(); throw; }
            // 打开前在天工里选好的面 / 方向边（0.8.5，同自动打孔）：按先后当成点了一下
            if (!preloaded) {
                preloaded = true;
                try {
                    BeginInvoke((Action)(() => {
                        if (closing || busy) return;
                        try {
                            var set = assembly != null ? assembly.SelectSet : partDoc.SelectSet; int n = Math.Min(set.Count, 3);
                            for (int i = 1; i <= n; i++) { object o = null; try { o = set.Item(i); } catch { } if (o != null && !(o is A.Occurrence) && !(o is A.SubOccurrence)) AcceptPick(o); }
                        } catch (Exception e) { Log.Write("AutoHolePatternPreselect", e.Message); }
                    }));
                } catch { }
            }
        }
        bool preloaded;

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
            var before = target;
            bool willFlip = shell.Current == PageFace;
            bool frozen = willFlip && Ui.BeginRedrawFreeze(shell);
            try {
                AcceptPickCore(selected);
                // 点了新的面：翻到「排布」（第一次选面、或换了一张面）；合帧切页避免先闪一帧 faceCardD 再跳页
                if (target != null && !ReferenceEquals(before, target) && shell.Current == PageFace) shell.Go(PageLayout);
            } finally {
                Ui.EndRedrawFreeze(shell, frozen);
            }
        }
        void AcceptPickCore(object selected){
            try {
                // 点在平面上就（重新）定打孔面；点在边线上就按当前排布方式当"定位圆 / 方向边"。
                var pg = PickGeometry.Unwrap(selected);
                bool isFace = pg.Geometry is G.Face;
                if (isFace || target == null) {
                    if (!isFace) throw new ArgumentException("请先点一个平面作为打孔面。");
                    var fresh = AutoHoleReader.ReadTarget(selected);
                    // 打完一批孔以后旧的面片对象已经失效，读它的 ID 会抛 COM 异常 —— 原来这一下直接让"再点同一个面接着排"报错
                    bool sameFace = false;
                    // 同一张面 = 同实例 + 同实体 + 同 ID + 平面重合（多实体零件的面 ID 跨实体重号：只比 ID 时点另一个实体上的面会沿用旧面的方向边，排孔排到错的方向上）
                    try { sameFace = target != null && frame != null && TargetFace.SameFace(target, fresh); }
                    catch { sameFace = false; }
                    target = fresh; faceSelection = selected;
                    dirSelection = null; circleReady = false;
                    if (sameFace) {
                        // 同一个面再点一次 = 沿用原来选的方向边，不当成"新选面"清掉
                        SetStatus("还是这个面。" + frame.Label, Ui.Accent);
                    } else {
                        frame = FaceFrameReader.Read(target);   // 自动取最长边
                        faceText = FaceLabel(target) + "　方向：" + frame.Label;
                        // 零件在装配里用了好几次：孔打在零件文件上，每处都会有（0.8.5）
                        string f = ""; try { f = target.Part.FullName; } catch { }
                        target.Instances = assembly == null ? 0 : AssemblyPart.CountInstances(assembly, f);
                        string inst = target.Instances > 1 ? "　⚠「" + target.PartName + "」在装配里用了 " + target.Instances + " 次，排的孔每处都会有" : "";
                        try { target.Risk = TargetFace.RiskOf(target.Part, assembly != null ? assembly.FullName : ""); } catch { }
                        if (target.Risk.Length > 0) inst += "　⚠ " + target.Risk;
                        faceText += inst;
                        SetStatus("已选打孔面。" + frame.Label + "　（要手动指定方向就再点一条直线边）" + inst, inst.Length > 0 ? Ui.Warn : Ui.Ok);
                    }
                } else if (Kind == HolePatternKind.Circular) {
                    V3 c, ax; double r;
                    FaceFrameReader.ReadCircle(selected, out c, out r, out ax);
                    // 定位圆要和打孔面平行（法兰上的螺栓分布圆）：斜的圆投到面上是椭圆，均布出来的孔位全不对
                    if (Math.Abs(ax.Unit().Dot(target.Plane.Normal.Unit())) < 0.99) throw new ArgumentException("这条圆边和打孔面不平行，不能当分度圆。请点打孔面上（或和它平行的面上）的圆形边线。");
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
            } catch (Exception e) { SetStatus(AutoHoleWriter.FriendlyPick(e), Ui.Warn); Log.Write("AutoHolePatternPick", e); }
        }

        static string FaceLabel(TargetFace t){
            return (t.PartName != null && t.PartName.Length > 0 ? t.PartName : "零件") + "　" + t.Label;
        }
        static string Num(double v){ return v.ToString("0.##", CultureInfo.InvariantCulture); }

        // ---------------- 预览 ----------------
        void RefreshPreview(bool redrawHighlights = true){
            var k = Kind;
            layoutGrid.Show(countRow, k != HolePatternKind.Pitch);
            layoutGrid.Show(pitchRow, k == HolePatternKind.Pitch);
            layoutGrid.Show(edgeRow, k != HolePatternKind.Circular);
            layoutGrid.Show(sameEdgeRow, k != HolePatternKind.Circular);
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
            int holesN = 0;
            try {
                string tp = SlotMode ? "" : hs.SpecProblem;
                if (target == null) { previewText.Text = "选一个面，这里会画出排孔示意。"; previewText.ForeColor = Ui.Muted; }
                else if (tp.Length > 0) { previewText.Text = "⚠ " + tp; previewText.ForeColor = Ui.Bad; }
                else if (k == HolePatternKind.Circular) {
                    if (!circleReady) { previewText.Text = "圆周均布：再点一条圆形边线（分度圆）。"; previewText.ForeColor = Ui.Warn; }
                    else {
                        var sp = Spec();
                        var a = HolePatternSolver.Angles(sp);
                        previewText.Text = "在 R" + Num(circleRadiusMm) + " 的分度圆上均布 " + a.Length + " 个孔，起始 " + Num(sp.StartAngleDeg) + "°，间隔 " + Num(360.0 / sp.Count) + "°。\r\n"
                                         + HoleNaming.Short(hs) + "　·　" + (asPattern.Checked ? "种子孔 + 圆周阵列" : "同规格的孔合成一个孔特征");
                        var cs = Centres(sp);
                        var outside = Outside(cs);
                        holesN = cs.Count - outside.Count;
                        if (outside.Count > 0) previewText.Text += "\r\n⚠ 第 " + OutsideText(outside) + " 个孔位落在面外，不会打";
                        previewText.ForeColor = outside.Count > 0 ? Ui.Warn : Ui.Ok; ok = true;
                    }
                } else {
                    var sp = Spec();
                    HolePatternSolver.Offsets(frame.LengthMm, sp);
                    string lp = HolePatternSolver.LateralProblem(frame.WidthMm, sp.LateralMm, HalfWidthMm(hs));
                    if (lp.Length > 0) throw new ArgumentException(lp);
                    previewText.Text = HolePatternSolver.Describe(frame.LengthMm, sp)
                                     + (latMode.SelectedIndex == 1 ? "，孔心离方向边 " + Num((double)lateral.Value) + "mm" : "，横向居中") + "\r\n"
                                     + (SlotMode ? "腰孔 " + Num((double)slotLen.Value) + "×" + Num((double)slotWidth.Value) + "（通切）"
                                                 : HoleNaming.Short(hs) + "　·　" + (asPattern.Checked ? "种子孔 + 阵列" : "同规格的孔合成一个孔特征"));
                    var cs = Centres(sp);
                    var outside = Outside(cs);
                    holesN = cs.Count - outside.Count;
                    if (outside.Count > 0) previewText.Text += "\r\n⚠ 第 " + OutsideText(outside) + " 个孔位落在面外（缺口/开窗处），不会打";
                    else {
                        // 孔心在面上、孔口却切到面边（离边太近 / L 形面的内角）：打出来是缺口（0.8.5）
                        var cut = new List<int>();
                        double mouth = SlotMode ? (double)slotWidth.Value : hs.Kind == HoleKind.Counterbore ? hs.CounterboreDiameter : hs.Kind == HoleKind.Countersink ? hs.CountersinkDiameter : hs.HoleDiameter;
                        for (int i = 0; i < cs.Count; i++) if (FaceFrameReader.CutsEdge(target, cs[i], mouth)) cut.Add(i + 1);
                        if (cut.Count > 0) { previewText.Text += "\r\n⚠ 第 " + OutsideText(cut) + " 个孔的孔口会切到面的边上（打出来是缺口），请调边距 / 横向位置"; outside = cut; }
                    }
                    previewText.ForeColor = outside.Count > 0 ? Ui.Warn : Ui.Ok; ok = true;
                }
            } catch (Exception e) { previewText.Text = e.Message; previewText.ForeColor = Ui.Bad; }
            if (tips.GetToolTip(previewText) != previewText.Text) tips.SetToolTip(previewText, previewText.Text);
            bool wantRun = ok && !busy;
            if (run.Enabled != wantRun) run.Enabled = wantRun;
            string specProblem = ""; try { specProblem = SlotMode ? "" : hs.SpecProblem; } catch { }
            RefreshRail(ok, holesN, specProblem);
            if (redrawHighlights && highlights != null) {
                try {
                    highlights.RemoveAll();
                    if (faceSelection != null) highlights.AddItem(faceSelection);
                    if (dirSelection != null) highlights.AddItem(dirSelection);
                    highlights.Draw();
                } catch (Exception e) { Log.Write("AutoHolePatternHighlight", e); }
            }
            if (previewCanvas != null && previewCanvas.Visible) previewCanvas.Invalidate();
            if (previewCanvas2 != null && previewCanvas2.Visible) previewCanvas2.Invalidate();
        }

        // 行数变了：网格跟着变高变矮，下面的说明 / 提示顶上来（高度未变时不触发无意义 Resize/Layout）
        void FitCards(){
            int lh = layoutGrid.PreferredHeight;
            if (layoutGrid.Height != lh) layoutGrid.Height = lh;
            int sh = specGrid.PreferredHeight;
            if (specGrid.Height != sh) specGrid.Height = sh;
            int st = sh + 6;
            if (stdInfo.Top != st) stdInfo.Top = st;
        }

        // 排孔示意图：面轮廓 + 每个孔的位置（腰孔画成长圆）。
        void DrawPreview(object sender, PaintEventArgs e){
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var area = ((Control)sender).ClientRectangle;
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

        void SetStatus(string text, Color color){
            if (status.Text == text && status.ForeColor == color) return;
            status.Text = text; status.ForeColor = color;
        }
        object SafeActive(){ try { return app.ActiveDocument; } catch { return null; } }
    }
}
