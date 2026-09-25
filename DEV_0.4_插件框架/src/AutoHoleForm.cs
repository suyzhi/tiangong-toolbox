using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using A=SolidEdgeAssembly;
using F=SolidEdgeFramework;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;

namespace TianGongCadSuite {
    // 命令 6：自动打孔。
    // 交互：点孔口的圆形边线 = 加参考孔；点零件平面 = 加打孔面；没有模式切换。
    // 孔型和规格从参考孔反推，但每一条都能在窗口里改，改完实时看到"将打哪些孔"。
    public sealed class AutoHoleForm : Form, F.ISEMouseEvents, F.ISECommandEvents {
        // 一组同直径的参考孔，共用一份可编辑的孔规格。
        sealed class RefGroup {
            public double DiameterMm;
            public readonly List<ReferenceHole> Holes = new List<ReferenceHole>();
            public readonly List<object> Selections = new List<object>();   // 只用于高亮；打完孔会失效，故与打孔数据分开存
            public HoleSpec Spec;
            public int Count { get { return Holes.Count; } }
            public string Head { get { return "Φ" + M(DiameterMm) + "　×" + Holes.Count; } }
            public string Body { get { return Spec == null ? "" : Spec.Summary; } }
            static string M(double v){ return v.ToString("0.##", CultureInfo.InvariantCulture); }
        }

        readonly F.Application app;
        readonly A.AssemblyDocument assembly;
        F.ISECommand command; F.ISEMouse mouse; Connection mouseConnection, commandConnection;
        F.HighlightSet highlights;

        readonly List<RefGroup> groups = new List<RefGroup>();
        readonly List<object> targets = new List<object>();
        readonly List<string> targetLabels = new List<string>();
        bool busy, closing, loading;
        bool drilledOnce;                 // 这一批面已经打过孔：面片对象已失效，再点一次会对着死对象打
        // 已解析的打孔面缓存（与 targets 一一对应）。实时预览会被数值框的每次改动触发，
        // 把"读面 + 算面内包围盒"缓起来，避免同一张面被反复走一遍 COM 边遍历。
        readonly List<TargetFace> resolved = new List<TargetFace>();

        readonly ListBox refList = new ListBox();
        readonly ListBox faceList = new ListBox();
        readonly Chip chip = new Chip();
        readonly Label previewLine = new Label();
        readonly Label previewSub = new Label();
        readonly Label status = new Label();
        readonly Label editWho = new Label();
        readonly Button run = Ui.Primary("开始打孔", 168, 38);

        readonly ComboBox kindBox = Ui.Combo(112, "通孔", "螺纹孔", "圆柱沉孔", "锥形沉孔");
        readonly ComboBox sizeBox = Ui.Combo(92, "自定义");
        readonly ComboBox bottomBox = Ui.Combo(84, "平底", "V 型底");
        readonly NumericUpDown diaNum = Ui.Num(0.1M, 500M, 6M, 2, 0.1M, 78);
        readonly NumericUpDown depthNum = Ui.Num(0.1M, 2000M, 10M, 2, 1M, 78);
        readonly NumericUpDown bottomAngNum = Ui.Num(30M, 179M, 118M, 0, 1M, 58);
        readonly NumericUpDown cboreDiaNum = Ui.Num(0.1M, 500M, 11M, 2, 0.5M, 78);
        readonly NumericUpDown cboreDepNum = Ui.Num(0.1M, 500M, 6.5M, 2, 0.5M, 78);
        readonly NumericUpDown csinkDiaNum = Ui.Num(0.1M, 500M, 12M, 2, 0.5M, 78);
        readonly NumericUpDown csinkAngNum = Ui.Num(10M, 179M, 90M, 0, 1M, 78);
        readonly NumericUpDown chamferSetNum = Ui.Num(0.1M, 20M, 0.5M, 2, 0.1M, 62);
        readonly NumericUpDown chamferAngNum = Ui.Num(10M, 89M, 45M, 0, 1M, 58);
        readonly CheckBox throughCheck = new CheckBox { Text = "贯通" };
        readonly CheckBox depthCheck = new CheckBox { Text = "盲孔" };
        readonly CheckBox chamferCheck = new CheckBox { Text = "孔口倒角" };
        readonly Panel cboreRow = new Panel();
        readonly Panel csinkRow = new Panel();
        readonly Panel threadNote = new Panel();

        public AutoHoleForm(F.Application application, A.AssemblyDocument document){
            app = application; assembly = document;
            Ui.Shell(this, "自动打孔", 680, 800, 620, 680);

            // 规格下拉框：第 0 项是「自定义」，后面跟表里的全部规格。
            // 漏了这段会怎样：参考孔是 Φ13.5（M12 过孔）时要选中第 7 项，而框里只有 1 项 →
            // InvalidArgument "7" 对于 SelectedIndex 无效 —— 就是用户截图里那个报错。
            sizeBox.Items.Clear();
            sizeBox.Items.Add("自定义");
            foreach (var row in HoleMatcher.Table) sizeBox.Items.Add(row.Size);
            sizeBox.SelectedIndex = 0;

            BuildLayout();
            WireEvents();
            RefreshAll();

            FormClosed += (s, e) => Cleanup();
            // StartPicking 由宿主 ToolContext.Show 的启动回调调用，这里不能重复调，
            // 否则第二次 command.Start() 会触发 Terminate() 把窗口关掉。
        }

        // 自检：把所有「孔型 × 规格」组合都过一遍编辑器加载逻辑，返回发现的问题（空串 = 没问题）。
        // 用户报的 "InvalidArgument: 7 对于 SelectedIndex 无效" 就是规格下拉框没填数据导致的，
        // 这类问题靠它能在测试里直接抓到，不用等人点到。
        public string SelfCheck(){
            var bad = new List<string>();
            if (kindBox.Items.Count != 4) bad.Add("孔型下拉框应有 4 项，实有 " + kindBox.Items.Count);
            if (sizeBox.Items.Count != HoleMatcher.Table.Length + 1)
                bad.Add("规格下拉框应有 " + (HoleMatcher.Table.Length + 1) + " 项，实有 " + sizeBox.Items.Count);
            if (bottomBox.Items.Count != 2) bad.Add("孔底下拉框应有 2 项，实有 " + bottomBox.Items.Count);
            var probe = new RefGroup { DiameterMm = 8.5, Spec = HoleMatcher.Match(8.5).Target };
            groups.Add(probe);
            bool savedLoading = loading;
            try {
                RefreshList(0);
                for (int k = 0; k < kindBox.Items.Count; k++) {
                    for (int s = 0; s < sizeBox.Items.Count; s++) {
                        try {
                            loading = true;
                            kindBox.SelectedIndex = k;
                            sizeBox.SelectedIndex = s;
                            loading = false;
                            ApplyKindChange();
                            ApplySizeChange();
                            LoadEditor();
                            WriteBack();
                        } catch (Exception e) {
                            bad.Add("孔型#" + k + " 规格#" + s + "：" + e.Message);
                        } finally { loading = savedLoading; }
                    }
                }
            } finally {
                groups.Remove(probe);
                loading = savedLoading;
                RefreshList(-1);
            }
            return string.Join("；", bad.ToArray());
        }

        // 键盘：Esc 关闭、Ctrl+Enter 打孔、列表里 Delete 移除。
        // 注意不能订阅 this.KeyDown —— 本类实现的 ISECommandEvents 里有同名方法，事件被遮住了。
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData){
            if (keyData == Keys.Escape) { Close(); return true; }
            if (keyData == (Keys.Control | Keys.Enter)) { RunDrill(); return true; }
            if (keyData == Keys.Delete && (ActiveControl == refList || ActiveControl == faceList)) {
                if (ActiveControl == refList) RemoveSelected(); else RemoveSelectedFace();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ---------------- 布局 ----------------
        void BuildLayout(){
            var step1 = Ui.Step(1, "点孔", "在任意零件上点孔口的圆形边线，可以连点好几个，大小可以不一样");
            var refsCard = Ui.Card("参考孔", 168, "点圆边就会加到这里");
            var step2 = Ui.Step(2, "点面", "点要打孔的那个平面，可以连点好几个面");
            var facesCard = Ui.Card("打孔面", 104, "孔心按参考孔的轴线投影到这些面上");
            var step3 = Ui.Step(3, "定规格", "默认是自动反推的，不动就按它打；要改就改这里");
            var paramCard = Ui.Card("孔参数", 226, "");
            var previewCard = Ui.Card("预览", 104, "打完之前先看清楚要打几个、什么孔");
            var actions = new Panel { Height = 56, BackColor = Ui.Bg, Padding = new Padding(0, 8, 0, 8) };

            // 参考孔列表
            refList.Dock = DockStyle.Fill; refList.IntegralHeight = false; refList.BorderStyle = BorderStyle.FixedSingle;
            refList.DrawMode = DrawMode.OwnerDrawFixed; refList.ItemHeight = 38; refList.BackColor = Color.White;
            refList.DrawItem += DrawRefItem;
            var refBtns = new Panel { Dock = DockStyle.Right, Width = 96, BackColor = Color.White };
            var delRef = Ui.Secondary("移除选中", 92, 26); delRef.SetBounds(4, 2, 92, 26);
            var clrRef = Ui.Secondary("清空", 92, 26); clrRef.SetBounds(4, 32, 92, 26);
            delRef.Click += (s, e) => RemoveSelected();
            clrRef.Click += (s, e) => { groups.Clear(); RefreshAll(); SetStatus("已清空参考孔。", Ui.Muted); };
            refBtns.Controls.Add(delRef); refBtns.Controls.Add(clrRef);
            refsCard.Controls.Add(refList); refsCard.Controls.Add(refBtns);

            // 打孔面列表
            faceList.Dock = DockStyle.Fill; faceList.IntegralHeight = false; faceList.BorderStyle = BorderStyle.FixedSingle;
            faceList.DrawMode = DrawMode.OwnerDrawFixed; faceList.ItemHeight = 30; faceList.BackColor = Color.White;
            faceList.DrawItem += DrawFaceItem;
            var faceBtns = new Panel { Dock = DockStyle.Right, Width = 96, BackColor = Color.White };
            var delFace = Ui.Secondary("移除选中", 92, 26); delFace.SetBounds(4, 2, 92, 26);
            var clrFace = Ui.Secondary("清空", 92, 26); clrFace.SetBounds(4, 32, 92, 26);
            delFace.Click += (s, e) => { RemoveSelectedFace(); };
            clrFace.Click += (s, e) => { targets.Clear(); targetLabels.Clear(); resolved.Clear(); drilledOnce = false; RefreshAll(); SetStatus("已清空打孔面。", Ui.Muted); };
            faceBtns.Controls.Add(delFace); faceBtns.Controls.Add(clrFace);
            facesCard.Controls.Add(faceList); facesCard.Controls.Add(faceBtns);

            BuildParamCard(paramCard);

            // 预览（子控件放进 body，免得盖住卡片标题）
            var pvBody = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
            previewCard.Controls.Add(pvBody);
            chip.SetBounds(2, 4, 132, 24);
            previewLine.SetBounds(144, 4, 500, 22);
            previewLine.AutoSize = false; previewLine.Font = Ui.F9B; previewLine.ForeColor = Ui.Text;
            previewSub.SetBounds(2, 32, 640, 48);
            previewSub.AutoSize = false; previewSub.ForeColor = Ui.Muted;
            pvBody.Controls.Add(chip); pvBody.Controls.Add(previewLine); pvBody.Controls.Add(previewSub);

            // 底部按钮
            run.SetBounds(0, 8, 168, 38);
            var clearAll = Ui.Secondary("全部重选", 96, 30); clearAll.SetBounds(178, 12, 96, 30);
            var close = Ui.Secondary("关闭 (Esc)", 96, 30); close.SetBounds(282, 12, 96, 30);
            clearAll.Click += (s, e) => { groups.Clear(); targets.Clear(); targetLabels.Clear(); resolved.Clear(); drilledOnce = false; RefreshAll(); SetStatus("已全部清空，重新点孔。", Ui.Accent); };
            close.Click += (s, e) => Close();
            run.Click += (s, e) => RunDrill();
            actions.Controls.Add(run); actions.Controls.Add(clearAll); actions.Controls.Add(close);

            status.Height = 26; status.Padding = new Padding(2, 0, 10, 0);
            status.TextAlign = ContentAlignment.MiddleLeft; status.ForeColor = Ui.Accent; status.BackColor = Ui.Bg;
            status.Text = "把鼠标移到孔口的圆形边线上点一下，就能加一个参考孔。";

            // 纵向堆叠：添加顺序就是从上到下的顺序。参考孔列表吃掉剩余高度。
            var stack = new VerticalStack { Dock = DockStyle.Fill };
            stack.Add(step1); stack.Add(refsCard, true); stack.Add(step2); stack.Add(facesCard);
            stack.Add(step3); stack.Add(paramCard); stack.Add(previewCard); stack.Add(actions); stack.Add(status);
            Controls.Add(stack);
        }

        void BuildParamCard(Card card){
            var body = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
            card.Controls.Add(body);
            int y = 2;
            body.Controls.Add(Lbl("孔型", 2, y)); kindBox.SetBounds(40, y - 4, 112, 24); body.Controls.Add(kindBox);
            body.Controls.Add(Lbl("规格", 164, y)); sizeBox.SetBounds(202, y - 4, 92, 24); body.Controls.Add(sizeBox);
            body.Controls.Add(Lbl("孔径", 306, y)); diaNum.SetBounds(344, y - 4, 78, 24); body.Controls.Add(diaNum);
            var mm = Ui.Muted8("mm"); mm.Location = new Point(428, y + 4); body.Controls.Add(mm);
            y += 32;
            body.Controls.Add(Lbl("深度", 2, y));
            throughCheck.SetBounds(40, y, 58, 22); depthCheck.SetBounds(102, y, 62, 22);
            depthNum.SetBounds(168, y - 3, 78, 24);
            body.Controls.Add(throughCheck); body.Controls.Add(depthCheck); body.Controls.Add(depthNum);
            body.Controls.Add(Lbl("孔底", 258, y)); bottomBox.SetBounds(296, y - 4, 84, 24); body.Controls.Add(bottomBox);
            bottomAngNum.SetBounds(386, y - 4, 58, 24); body.Controls.Add(bottomAngNum);
            var deg = Ui.Caption("°"); deg.SetBounds(448, y, 20, 20); body.Controls.Add(deg);
            y += 32;
            cboreRow.SetBounds(0, y, 520, 26); cboreRow.BackColor = Color.White;
            cboreRow.Controls.Add(Lbl("沉孔Φ", 2, 2)); cboreDiaNum.SetBounds(56, 0, 78, 24); cboreRow.Controls.Add(cboreDiaNum);
            cboreRow.Controls.Add(Lbl("沉孔深", 146, 2)); cboreDepNum.SetBounds(200, 0, 78, 24); cboreRow.Controls.Add(cboreDepNum);
            csinkRow.SetBounds(0, y, 520, 26); csinkRow.BackColor = Color.White;
            csinkRow.Controls.Add(Lbl("锥孔Φ", 2, 2)); csinkDiaNum.SetBounds(56, 0, 78, 24); csinkRow.Controls.Add(csinkDiaNum);
            csinkRow.Controls.Add(Lbl("锥角", 146, 2)); csinkAngNum.SetBounds(200, 0, 78, 24); csinkRow.Controls.Add(csinkAngNum);
            csinkRow.Controls.Add(Lbl("°", 284, 2));
            body.Controls.Add(cboreRow); body.Controls.Add(csinkRow);
            y += 32;
            chamferCheck.SetBounds(2, y, 86, 22); body.Controls.Add(chamferCheck);
            chamferSetNum.SetBounds(92, y - 3, 62, 24); body.Controls.Add(chamferSetNum);
            var x = Ui.Caption("×"); x.SetBounds(158, y, 16, 20); body.Controls.Add(x);
            chamferAngNum.SetBounds(176, y - 3, 58, 24); body.Controls.Add(chamferAngNum);
            var d2 = Ui.Caption("°"); d2.SetBounds(238, y, 20, 20); body.Controls.Add(d2);
            editWho.SetBounds(268, y, 330, 20); editWho.AutoSize = false; editWho.ForeColor = Ui.Muted; editWho.Font = Ui.F8;
            body.Controls.Add(editWho);
            y += 30;
            threadNote.SetBounds(0, y, 580, 40); threadNote.BackColor = Color.White;
            var tn = new Label { Dock = DockStyle.Fill, ForeColor = Ui.Muted, Font = Ui.F8, AutoSize = false };
            tn.Text = "螺纹孔按螺纹内小径建模，螺纹以装饰螺纹显示（和 CAD 自己的孔命令一致）；" +
                      "盲孔默认平底，需要钻尖就选 V 型底。";
            threadNote.Controls.Add(tn);
            body.Controls.Add(threadNote);
        }

        static Label Lbl(string text, int x, int y){
            return new Label { Text = text, AutoSize = true, Location = new Point(x, y + 4), BackColor = Color.Transparent };
        }

        void WireEvents(){
            kindBox.SelectedIndexChanged += (s, e) => { if (!loading) ApplyKindChange(); };
            sizeBox.SelectedIndexChanged += (s, e) => { if (!loading) ApplySizeChange(); };
            bottomBox.SelectedIndexChanged += (s, e) => { if (!loading) WriteBack(); };
            throughCheck.CheckedChanged += (s, e) => { if (loading) return; loading = true; depthCheck.Checked = !throughCheck.Checked; loading = false; WriteBack(); };
            depthCheck.CheckedChanged += (s, e) => { if (loading) return; loading = true; throughCheck.Checked = !depthCheck.Checked; loading = false; WriteBack(); };
            chamferCheck.CheckedChanged += (s, e) => { if (!loading) WriteBack(); };
            foreach (var n in new Control[]{ diaNum, depthNum, bottomAngNum, cboreDiaNum, cboreDepNum, csinkDiaNum, csinkAngNum, chamferSetNum, chamferAngNum })
                ((NumericUpDown)n).ValueChanged += (s, e) => { if (!loading) WriteBack(); };
            refList.SelectedIndexChanged += (s, e) => { if (!loading) LoadEditor(); };
            // 打孔重试期间 CAD 正忙，这里把重试进度写到状态栏并让界面刷一次，
            // 用户看到的就不是"卡死了"，而是"正在重试 2/6"。
            AutoHoleWriter.Progress = msg => {
                SetStatus(msg, Ui.Warn);
                try { status.Update(); System.Windows.Forms.Application.DoEvents(); } catch { }
            };
        }

        // ---------------- 列表绘制 ----------------
        void DrawRefItem(object sender, DrawItemEventArgs e){
            e.DrawBackground();
            if (e.Index < 0 || e.Index >= groups.Count) return;
            var g = groups[e.Index];
            bool sel = (e.State & DrawItemState.Selected) != 0;
            using (var b = new SolidBrush(sel ? Ui.ChipBg : Color.White)) e.Graphics.FillRectangle(b, e.Bounds);
            using (var p = new Pen(Ui.Line)) e.Graphics.DrawLine(p, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            // 标题只占它自己的宽度：占满整行会跟右边的规格文字叠在一起（用户截图里的"文字重叠"）
            var sz = TextRenderer.MeasureText(e.Graphics, g.Head, Ui.F9B);
            int headW = Math.Min(sz.Width, e.Bounds.Width / 2);
            var r1 = new Rectangle(e.Bounds.X + 8, e.Bounds.Y + 4, headW, 18);
            TextRenderer.DrawText(e.Graphics, g.Head, Ui.F9B, r1, sel ? Ui.AccentD : Ui.Text, TextFormatFlags.NoPadding);
            var r2 = new Rectangle(e.Bounds.X + 16 + headW, e.Bounds.Y + 5, e.Bounds.Width - headW - 28, 18);
            TextRenderer.DrawText(e.Graphics, g.Body, Ui.F9, r2, Ui.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            var r3 = new Rectangle(e.Bounds.X + 8, e.Bounds.Y + 21, e.Bounds.Width - 16, 16);
            string note = g.Spec != null && g.Spec.Note.Length > 0 ? g.Spec.Note : " ";
            TextRenderer.DrawText(e.Graphics, note, Ui.F8, r3, Ui.Muted, TextFormatFlags.EndEllipsis);
        }

        void DrawFaceItem(object sender, DrawItemEventArgs e){
            e.DrawBackground();
            if (e.Index < 0 || e.Index >= targetLabels.Count) return;
            bool sel = (e.State & DrawItemState.Selected) != 0;
            using (var b = new SolidBrush(sel ? Ui.ChipBg : Color.White)) e.Graphics.FillRectangle(b, e.Bounds);
            using (var p = new Pen(Ui.Line)) e.Graphics.DrawLine(p, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            var r = new Rectangle(e.Bounds.X + 8, e.Bounds.Y + 5, e.Bounds.Width - 16, 20);
            TextRenderer.DrawText(e.Graphics, targetLabels[e.Index], Ui.F9, r, sel ? Ui.AccentD : Ui.Text, TextFormatFlags.EndEllipsis);
        }

        // ---------------- 选择 ----------------
        public void StartPicking(){
            try {
                if (command != null) StopCommand();   // 幂等
                if (assembly == null) throw new InvalidOperationException("请先打开装配文件。");
                if (assembly.ReadOnly || assembly.InPlaceActivated) throw new InvalidOperationException("请在可编辑的装配顶层运行，先退出原位编辑。");
                command = (F.ISECommand)app.CreateCommand((int)SolidEdgeConstants.seCmdFlag.seNoDeactivate);
                commandConnection = new Connection(command, typeof(F.ISECommandEvents), this);
                command.Start();
                mouse = (F.ISEMouse)command.Mouse;
                mouseConnection = new Connection(mouse, typeof(F.ISEMouseEvents), this);
                mouse.ScaleMode = 1; mouse.WindowTypes = 1;
                mouse.LocateMode = (int)SolidEdgeConstants.seLocateModes.seLocateSimple;
                mouse.EnabledMove = true;
                ((F.ISEMouseEx)mouse).InterDocumentLocate = true;      // 允许跨零件选
                ((F.ISEMouseEx2)mouse).LocateFrontToBack = true;       // 只选最前面那层
                ((F.ISEMouseEx3)mouse).PathfinderLocate = false;
                ConfigureFilter();
                if (highlights != null) highlights.Delete();
                highlights = assembly.HighlightSets.Add();
                highlights.Color = ColorTranslator.ToOle(Color.DeepSkyBlue);
            } catch { Cleanup(); throw; }
        }

        void ConfigureFilter(){
            if (mouse == null) return;
            mouse.ClearLocateFilter();
            mouse.AddToLocateFilter((int)SolidEdgeConstants.seLocateFilterConstants.seLocateEdge);
            mouse.AddToLocateFilter((int)SolidEdgeConstants.seLocateFilterConstants.seLocateFace);
        }

        public void AcceptPick(object selected){
            if (busy || closing || selected == null) return;
            try {
                var pg = PickGeometry.Unwrap(selected);
                if (pg.Geometry is G.Edge) {
                    var r = AutoHoleReader.ReadReference(selected);
                    var g = groups.FirstOrDefault(x => Math.Abs(x.DiameterMm - r.DiameterMm) < 0.01);
                    bool isNew = g == null;
                    HoleMatch match = null;
                    if (isNew) { match = HoleMatcher.Match(r.DiameterMm); g = new RefGroup { DiameterMm = r.DiameterMm, Spec = match.Target }; groups.Add(g); }
                    foreach (var old in g.Holes)
                        if ((old.Center - r.Center).Length < 1e-6) throw new ArgumentException("这个孔已经选过了。");
                    g.Holes.Add(r);
                    g.Selections.Add(selected);
                    RefreshList(groups.IndexOf(g));
                    // 新的一组：把匹配结论（含"这个直径有歧义"）完整说出来，而不是只说"已匹配到 X"
                    SetStatus(isNew ? HoleMatcher.StatusLine(match)
                                    : ("同一组又加了一个 Φ" + Num(r.DiameterMm) + "，共 " + groups.Sum(x => x.Count) + " 个孔"),
                              isNew && match != null && match.Ambiguous ? Ui.Warn : Ui.Ok);
                } else if (pg.Geometry is G.Face) {
                    var t = AutoHoleReader.ReadTarget(selected);
                    foreach (var existing in targets) {
                        var old = AutoHoleReader.ReadTarget(existing);
                        if (old.Part == t.Part && old.Face.ID == t.Face.ID) throw new ArgumentException("这个面已经选过了。");
                    }
                    targets.Add(selected);
                    targetLabels.Add(FaceLabel(t));
                    resolved.Add(t);
                    drilledOnce = false;
                    RefreshAll();
                    SetStatus("已选 " + targets.Count + " 个打孔面：" + FaceLabel(t), Ui.Ok);
                } else {
                    throw new ArgumentException("请点孔口的圆形边线，或者点要打孔的面。");
                }
                RefreshAll();
            } catch (Exception e) {
                SetStatus(e.Message, Ui.Warn);
                Log.Write("AutoHolePick", e);
            }
        }

        static string FaceLabel(TargetFace t){
            string part = t.PartName != null && t.PartName.Length > 0 ? t.PartName : "零件";
            return part + "　" + t.Label;
        }
        static string Num(double v){ return v.ToString("0.##", CultureInfo.InvariantCulture); }

        // 失败原因去重：按【零件 + 原因】而不是单按原因文本。
        // 原来同一直径的孔在两个零件上失败时，原因文字一模一样就被去重成一条，
        // 界面上"失败 N 个"和列出的说明条数对不上，用户也看不出是哪个零件出的问题。
        static void AddProblem(List<string> problems, string text){
            if (string.IsNullOrEmpty(text)) return;
            if (!problems.Contains(text)) problems.Add(text);
        }

        // ---------------- 刷新 ----------------
        void RefreshList(int select){
            loading = true;
            try {
                refList.Items.Clear();
                foreach (var g in groups) refList.Items.Add(g.Head);
                if (select >= 0 && select < refList.Items.Count) refList.SelectedIndex = select;
            } finally { loading = false; }
            LoadEditor();
        }

        RefGroup Selected { get { int i = refList.SelectedIndex; return (i >= 0 && i < groups.Count) ? groups[i] : null; } }

        void RefreshAll(){
            RefreshFaces();
            RefreshList(refList.SelectedIndex);
            RefreshPreview();
        }

        void RefreshFaces(){
            faceList.Items.Clear();
            foreach (var s in targetLabels) faceList.Items.Add(s);
            if (faceList.Items.Count > 0 && faceList.SelectedIndex < 0) faceList.SelectedIndex = 0;
        }

        void LoadEditor(){
            var g = Selected;
            loading = true;
            try {
                if (g == null || g.Spec == null) {
                    kindBox.SelectedIndex = 0; sizeBox.SelectedIndex = 0; bottomBox.SelectedIndex = 0;
                    diaNum.Value = 6; throughCheck.Checked = true; depthCheck.Checked = false; depthNum.Value = 10;
                    cboreDiaNum.Value = 11; cboreDepNum.Value = 6.5M; csinkDiaNum.Value = 12; csinkAngNum.Value = 90;
                    chamferCheck.Checked = false;
                    editWho.Text = groups.Count == 0 ? "还没有参考孔：先在模型上点一个孔。" : "选中上面一条来编辑它的规格。";
                } else {
                    var sp = g.Spec;
                    kindBox.SelectedIndex = (int)sp.Kind;
                    int si = 0;
                    if (sp.ThreadSize.Length > 0) { var r = HoleMatcher.Find(sp.ThreadSize); si = r.HasValue ? Array.IndexOf(HoleMatcher.Table, r.Value) + 1 : 0; }
                    sizeBox.SelectedIndex = si;
                    diaNum.Value = Ui.Clamp((decimal)Math.Round(sp.HoleDiameter, 2), diaNum.Minimum, diaNum.Maximum);
                    throughCheck.Checked = sp.Through; depthCheck.Checked = !sp.Through;
                    depthNum.Value = Ui.Clamp((decimal)Math.Round(sp.Depth <= 0 ? 10 : sp.Depth, 2), depthNum.Minimum, depthNum.Maximum);
                    bottomBox.SelectedIndex = sp.Bottom == HoleBottom.VBottom ? 1 : 0;
                    bottomAngNum.Value = Ui.Clamp((decimal)Math.Round(sp.BottomAngle, 0), bottomAngNum.Minimum, bottomAngNum.Maximum);
                    cboreDiaNum.Value = Ui.Clamp((decimal)Math.Round(sp.CounterboreDiameter, 2), cboreDiaNum.Minimum, cboreDiaNum.Maximum);
                    cboreDepNum.Value = Ui.Clamp((decimal)Math.Round(sp.CounterboreDepth, 2), cboreDepNum.Minimum, cboreDepNum.Maximum);
                    csinkDiaNum.Value = Ui.Clamp((decimal)Math.Round(sp.CountersinkDiameter, 2), csinkDiaNum.Minimum, csinkDiaNum.Maximum);
                    csinkAngNum.Value = Ui.Clamp((decimal)Math.Round(sp.CountersinkAngle, 0), csinkAngNum.Minimum, csinkAngNum.Maximum);
                    chamferCheck.Checked = sp.Chamfer;
                    chamferSetNum.Value = Ui.Clamp((decimal)Math.Round(sp.ChamferSetback, 2), chamferSetNum.Minimum, chamferSetNum.Maximum);
                    chamferAngNum.Value = Ui.Clamp((decimal)Math.Round(sp.ChamferAngle, 0), chamferAngNum.Minimum, chamferAngNum.Maximum);
                    editWho.Text = "正在编辑：" + g.Head + " —— 改动会同步到这一组的全部 " + g.Count + " 个孔。";
                }
            } finally { loading = false; }
            ShowKindFields();
        }

        void ShowKindFields(){
            var k = (HoleKind)Math.Max(0, kindBox.SelectedIndex);
            cboreRow.Visible = k == HoleKind.Counterbore;
            csinkRow.Visible = k == HoleKind.Countersink;
            sizeBox.Enabled = k != HoleKind.Through;            // 通孔不需要螺纹规格
            throughCheck.Enabled = k != HoleKind.Countersink;   // 锥形沉孔本身就是贯通的定位
            depthCheck.Enabled = throughCheck.Enabled;
            depthNum.Enabled = !throughCheck.Checked && throughCheck.Enabled;
            bottomBox.Enabled = depthCheck.Checked;
            bottomAngNum.Enabled = depthCheck.Checked && bottomBox.SelectedIndex == 1;
            chamferCheck.Enabled = k != HoleKind.Countersink;
        }

        void ApplyKindChange(){
            var g = Selected;
            var kind = (HoleKind)kindBox.SelectedIndex;
            loading = true;
            try {
                if (g != null) {
                    var fresh = HoleMatcher.FromRow(RowOf(g), kind);
                    fresh.Depth = g.Spec.Depth;
                    fresh.Bottom = g.Spec.Bottom;
                    fresh.BottomAngle = g.Spec.BottomAngle;
                    fresh.Chamfer = g.Spec.Chamfer;
                    fresh.ChamferSetback = g.Spec.ChamferSetback;
                    fresh.ChamferAngle = g.Spec.ChamferAngle;
                    fresh.Note = "";          // 规格变了，旧备注（"参考孔 Φx 是 Mx 的底孔…"）不再准确
                    g.Spec = fresh;
                }
            } finally { loading = false; }
            LoadEditor();
            WriteBack();
        }

        ThreadRow RowOf(RefGroup g){
            if (g.Spec != null && g.Spec.ThreadSize.Length > 0) {
                var r = HoleMatcher.Find(g.Spec.ThreadSize);
                if (r.HasValue) return r.Value;
            }
            var m = HoleMatcher.Match(g.DiameterMm);
            return m.HasRow ? m.Row : HoleMatcher.Table[3];
        }

        void ApplySizeChange(){
            var g = Selected;
            int si = sizeBox.SelectedIndex;
            if (g == null || si <= 0) { WriteBack(); return; }
            var row = HoleMatcher.Table[si - 1];
            loading = true;
            try {
                var fresh = HoleMatcher.FromRow(row, (HoleKind)kindBox.SelectedIndex);
                fresh.Depth = g.Spec.Depth;
                fresh.Bottom = g.Spec.Bottom;
                fresh.BottomAngle = g.Spec.BottomAngle;
                fresh.Chamfer = g.Spec.Chamfer;
                fresh.ChamferSetback = g.Spec.ChamferSetback;
                fresh.ChamferAngle = g.Spec.ChamferAngle;
                fresh.Note = "";
                g.Spec = fresh;
            } finally { loading = false; }
            LoadEditor();
            WriteBack();
        }

        void WriteBack(){
            var g = Selected;
            if (g == null) { RefreshPreview(); return; }
            var sp = g.Spec == null ? new HoleSpec() : g.Spec;
            sp.Kind = (HoleKind)Math.Max(0, kindBox.SelectedIndex);
            sp.HoleDiameter = (double)diaNum.Value;
            sp.Depth = throughCheck.Checked ? 0 : (double)depthNum.Value;
            sp.Bottom = bottomBox.SelectedIndex == 1 ? HoleBottom.VBottom : HoleBottom.Flat;
            sp.BottomAngle = (double)bottomAngNum.Value;
            sp.CounterboreDiameter = (double)cboreDiaNum.Value;
            sp.CounterboreDepth = (double)cboreDepNum.Value;
            sp.CountersinkDiameter = (double)csinkDiaNum.Value;
            sp.CountersinkAngle = (double)csinkAngNum.Value;
            sp.Chamfer = chamferCheck.Checked;
            sp.ChamferSetback = (double)chamferSetNum.Value;
            sp.ChamferAngle = (double)chamferAngNum.Value;
            sp.Note = "";
            // 选「自定义」就必须把螺纹规格清掉，**不管当前孔型是不是螺纹孔**。
            // 原来的写法在"参考孔自动匹配出 M6 螺纹孔 → 用户把规格改成自定义"这条路上
            // 保留了 ThreadSize="M6"：于是孔径随便改、标注还写着 M6，模型上打的是对的孔，
            // 工程图/标注上是错的螺纹 —— 这种错误在生产里是要出事的。
            // 要打"自定义螺纹孔"就靠孔型=螺纹孔 + 手填孔径，标注留空（而不是留一个对不上的旧规格）。
            if (sizeBox.SelectedIndex > 0) sp.ThreadSize = HoleMatcher.Table[sizeBox.SelectedIndex - 1].Size;
            else sp.ThreadSize = "";
            g.Spec = sp;
            int idx = refList.SelectedIndex;
            loading = true;
            try { if (idx >= 0 && idx < refList.Items.Count) refList.Items[idx] = g.Head; }
            finally { loading = false; }
            refList.Invalidate();
            ShowKindFields();
            RefreshPreview();
        }

        void RemoveSelected(){
            int i = refList.SelectedIndex;
            if (i < 0 || i >= groups.Count) { SetStatus("先在上面选中一组参考孔。", Ui.Warn); return; }
            groups.RemoveAt(i);
            RefreshAll();
            SetStatus("已移除一组参考孔。", Ui.Muted);
        }
        void RemoveSelectedFace(){
            int i = faceList.SelectedIndex;
            if (i < 0 || i >= targets.Count) { SetStatus("先在列表里选中一个打孔面。", Ui.Warn); return; }
            targets.RemoveAt(i); targetLabels.RemoveAt(i);
            if (i < resolved.Count) resolved.RemoveAt(i);
            drilledOnce = false;
            RefreshAll();
            SetStatus("已移除一个打孔面。", Ui.Muted);
        }

        // ---------------- 预览 ----------------
        // 不打孔，先算一遍：每个参考孔的轴线投到每张面上，落在面内才算一个孔。
        void RefreshPreview(){
            int total = 0, outside = 0;
            var perFace = new List<string>();
            var methods = new List<string>();
            for (int ti = 0; ti < targets.Count; ti++) {
                TargetFace t;
                try { t = ResolvedFace(ti); }
                catch { continue; }
                int here = 0, skip = 0;
                foreach (var g in groups) foreach (var h in g.Holes) {
                    try {
                        var c = AutoHoleReader.Intersect(h, t);
                        if (!FaceFrameReader.ContainsPoint(t, c)) { skip++; continue; }
                        here++;
                        if (!methods.Contains(g.Spec.Summary)) methods.Add(g.Spec.Summary);
                    } catch { skip++; }
                }
                total += here; outside += skip;
                if (here > 0 || skip > 0) perFace.Add(t.PartName + " " + here + " 个" + (skip > 0 ? "（跳过 " + skip + "）" : ""));
            }
            int refCount = groups.Sum(g => g.Count);
            if (refCount == 0) {
                chip.Text = "等待点孔"; chip.ChipColor = Ui.Muted;
                previewLine.Text = "① 先在模型上点孔口的圆形边线";
                previewSub.Text = "点圆边就是加参考孔；参考孔的孔型和规格会自动反推出来。";
            } else if (targets.Count == 0) {
                chip.Text = "已选 " + refCount + " 个孔"; chip.ChipColor = Ui.Accent;
                previewLine.Text = "② 再点要打孔的面";
                previewSub.Text = "共 " + groups.Count + " 组参考孔：" + string.Join("；", groups.Select(g => g.Head + " → " + g.Body).ToArray());
            } else {
                chip.Text = "将打 " + total + " 个孔"; chip.ChipColor = total > 0 ? Ui.Ok : Ui.Warn;
                previewLine.Text = total > 0 ? ("孔型：" + string.Join("；", methods.ToArray())) : "没有孔可以打，请看下面的原因";
                string s;
                if (drilledOnce) {
                    s = "这批打孔面已经用过了（打完孔后原来的面对象会失效），请重新点一个面；参考孔和规格都还在，不用重选。";
                } else {
                    s = "打孔面分布：" + string.Join("；", perFace.ToArray());
                    if (outside > 0 && total == 0)
                        s = "选中的面与参考孔不同轴：孔心的投影落在面外面，所以没有孔可打。"
                          + "请点与这些孔轴线相交的那个面（比如孔正上方/正下方零件的那个面）。";
                    else if (outside > 0)
                        s += "　—　另有 " + outside + " 个孔心投影落在所选面外，会自动跳过（不会打到材料外面）。";
                }
                // 锥形沉孔的默认锥孔直径是按螺钉头径估算的，必须让用户看见这句话
                foreach (var g in groups) {
                    if (g.Spec != null && g.Spec.Kind == HoleKind.Countersink && g.Spec.Note.Length > 0) {
                        s += "　⚠ " + g.Head + "：" + g.Spec.Note;
                        break;
                    }
                }
                previewSub.Text = s;
            }
            run.Enabled = total > 0 && !busy && !drilledOnce;
            if (highlights != null) {
                try {
                    highlights.RemoveAll();
                    foreach (var t in targets) { try { highlights.AddItem(t); } catch { } }
                    // 参考孔也高亮，用户才看得见自己点了哪几个孔。
                    // 打完孔以后这些选择引用会失效，失效的直接跳过（打孔用的是纯数据，不依赖它们）。
                    foreach (var g in groups) foreach (var s in g.Selections) { try { highlights.AddItem(s); } catch { } }
                    highlights.Draw();
                } catch (Exception e) { Log.Write("AutoHoleHighlight", e); }
            }
            chip.Invalidate();
        }

        // 打孔面的解析结果缓存：实时预览会被每次数值改动触发，别每次都重读一遍面片的边。
        TargetFace ResolvedFace(int index){
            var sel = targets[index];
            var t = index < resolved.Count ? resolved[index] : null;
            if (t == null || t.Face == null) {
                t = AutoHoleReader.ReadTarget(sel);
                while (resolved.Count <= index) resolved.Add(null);
                resolved[index] = t;
            }
            return t;
        }

        void SetStatus(string text, Color color){
            status.Text = text; status.ForeColor = color;
        }

        // ---------------- 打孔 ----------------
        void RunDrill(){
            if (busy || closing) return;
            try {
                if (groups.Count == 0) { SetStatus("请先点一个已有的孔。", Ui.Warn); return; }
                if (targets.Count == 0) { SetStatus("请先点要打孔的面。", Ui.Warn); return; }
                // 打完孔以后，那批面的面片对象已经失效。再点一次会对着死对象打：
                // 轻则每个孔都失败，重则 CAD 报一堆"对象已断开" —— 用户会以为软件坏了。
                if (drilledOnce) {
                    SetStatus("这批打孔面已经用过了（打完孔后原来的面对象会失效）。请重新点一个面，或点「全部重选」。", Ui.Warn);
                    return;
                }
                if (assembly.ReadOnly || assembly.InPlaceActivated) throw new InvalidOperationException("装配不可编辑或处于原位编辑中。");

                busy = true; run.Enabled = false; run.Text = "打孔中…"; UseWaitCursor = true;
                StopCommand();

                var refs = new List<ReferenceHole>(); var specs = new List<HoleSpec>();
                foreach (var g in groups) foreach (var h in g.Holes) { refs.Add(h); specs.Add(g.Spec); }

                int made = 0, skipped = 0, failed = 0;
                var problems = new List<string>(); var methods = new List<string>(); var audits = new List<string>();
                for (int ti = 0; ti < targets.Count; ti++) {
                    var t = ResolvedFace(ti);
                    var partName = t.PartName != null && t.PartName.Length > 0 ? t.PartName : "零件";
                    var requests = new List<AutoHoleWriter.HoleRequest>();
                    for (int i = 0; i < refs.Count; i++) {
                        var hole = refs[i];
                        V3 centre;
                        try { centre = AutoHoleReader.Intersect(hole, t); }
                        catch (Exception e) { skipped++; AddProblem(problems, partName + " Φ" + Num(hole.DiameterMm) + " 无法定位：" + e.Message); continue; }
                        if (!FaceFrameReader.ContainsPoint(t, centre)) { skipped++; continue; }
                        // Source 带上目标零件名：同一批里不同零件失败时，界面上的
                        // "失败 N 个"和列出的说明条数才对得上，用户也能直接定位是哪个零件。
                        requests.Add(new AutoHoleWriter.HoleRequest { Spec = specs[i], Centre = centre,
                            Source = partName + " Φ" + Num(hole.DiameterMm) });
                    }
                    if (requests.Count == 0) continue;
                    var res = AutoHoleWriter.DrillRequests(t, requests);
                    made += res.Created; failed += res.Failures.Count;
                    if (res.Method.Length > 0 && !methods.Contains(res.Method)) methods.Add(res.Method);
                    if (res.Audit.Length > 0 && !audits.Contains(res.Audit)) audits.Add(res.Audit);
                    foreach (var pf in res.Failures) AddProblem(problems, pf);
                }

                string msg = "共打孔 " + made + " 个";
                if (skipped > 0) msg += "，跳过 " + skipped + " 个（投影不落在所选面上）";
                if (failed > 0) msg += "，失败 " + failed + " 个";
                msg += "\r\n打孔面 " + targets.Count + " 个";
                if (methods.Count > 0) msg += "\r\n生成方式：" + string.Join("／", methods.ToArray());
                if (audits.Count > 0) msg += "\r\n\r\n自检：" + string.Join("；", audits.ToArray());
                msg += "\r\n\r\n请保存装配。";
                if (problems.Count > 0) msg += "\r\n\r\n说明：\r\n· " + string.Join("\r\n· ", problems.ToArray());
                MessageBox.Show(this, msg, "自动打孔完成", MessageBoxButtons.OK,
                    (failed > 0 || problems.Count > 0 || audits.Count > 0) ? MessageBoxIcon.Warning : MessageBoxIcon.Information);

                // 这批面已经用掉了：面片对象失效，解析缓存也一并作废，逼着用户重新点一个面。
                // 参考孔（纯数据）和规格保留 —— 换个面就能接着打同一批孔，不用重选。
                drilledOnce = true;
                resolved.Clear();
                foreach (var g in groups) g.Selections.Clear();   // 高亮引用也失效了，别再往上加
                SetStatus("完成：打孔 " + made + " 个" + (skipped > 0 ? "，跳过 " + skipped : "") + (failed > 0 ? "，失败 " + failed : "")
                          + "　—　这批打孔面已用完，换个面打下一批（参考孔不用重选）。",
                          failed > 0 ? Ui.Warn : Ui.Ok);
                busy = false; run.Text = "开始打孔"; UseWaitCursor = false;
                try { StartPicking(); } catch (Exception ex) { SetStatus(ex.Message, Ui.Warn); }
                RefreshPreview();
            } catch (Exception e) {
                Log.Write("AutoHoleDrill", e);
                busy = false; run.Text = "开始打孔"; UseWaitCursor = false;
                try { if (command == null) StartPicking(); } catch { }
                SetStatus(e.Message, Ui.Bad);
                MessageBox.Show(this, e.Message, "自动打孔", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        void StopCommand(){
            if (highlights != null) { try { highlights.Delete(); } catch { } highlights = null; }
            if (mouseConnection != null) { mouseConnection.Dispose(); mouseConnection = null; }
            if (commandConnection != null) { commandConnection.Dispose(); commandConnection = null; }
            var c = command; command = null; mouse = null;
            if (c != null) try { c.Done = true; } catch { }
        }

        void Cleanup(){
            if (closing) return; closing = true;
            AutoHoleWriter.Progress = null;
            StopCommand();
            groups.Clear(); targets.Clear(); targetLabels.Clear(); resolved.Clear();
            drilledOnce = false;
        }

        public new void MouseClick(short b, short s, double x, double y, double z, object w, int k, object g){
            if (b == 1) { if (g == null) { SetStatus("这里没有捕捉到对象。请点到孔口的圆边，或零件平面上。", Ui.Warn); return; } AcceptPick(g); }
            else if (b == 2) Close();
        }
        public new void MouseDown(short b, short s, double x, double y, double z, object w, int k, object g){ }
        public new void MouseUp(short b, short s, double x, double y, double z, object w, int k, object g){ }
        public new void MouseMove(short b, short s, double x, double y, double z, object w, int k, object g){
            if (closing || busy) return;
            if (g == null) SetStatus("把鼠标移到孔口的圆形边线，或零件平面上。", Ui.Muted);
        }
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
