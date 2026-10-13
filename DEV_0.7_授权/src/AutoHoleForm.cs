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
    //
    // 0.7.3 起：
    //   · 同规格的孔编成一个特征组并按规格命名（「M6 螺纹孔（螺纹全长）×4」），不再是一长串"简单孔 N"；
    //   · 螺纹孔可选「全长 / 指定深度」（原来 CAD 默认只攻 2mm）；
    //   · 打出来的孔记住参考孔（HoleLink），参考零件挪了以后底部「同步孔位」一键跟回去；
    //   · 界面重排：左列选东西，右列定规格 + 看孔形状，底部预览/打孔/关联孔位。
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
            public string Target { get { return Spec == null ? "" : HoleNaming.Short(Spec); } }
            // 这一组里有没有"孔口比孔径大"的孔（沉孔/锥沉/倒角孔口），有就写清楚是按下面的孔径配做的。
            public string MouthInfo { get { return HoleScan.MouthSummary(Holes); } }
            public string Detail { get { string m = MouthInfo; return m.Length == 0 ? Body : (Body + "　·　" + m); } }
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
        // 被"面扫描"点过的面（只用于高亮：这些面不是打孔面，但要让用户看见自己点了哪张）。
        readonly List<object> scannedPicks = new List<object>();

        // 点平面时先当"带孔的面"扫一遍（默认开）：一个面上几十个孔不用一个一个点圆边。
        // 勾掉就是老用法 —— 点平面＝加打孔面。
        readonly CheckBox scanFaceCheck = new CheckBox { Checked = true, Text = "点平面时自动识别面上的孔", AutoSize = true };
        string lastScanNote = "";   // 上一次面扫描的结论，显示在预览的说明行里

        readonly ListBox refList = new ListBox();
        readonly ListBox faceList = new ListBox();
        readonly Chip chip = new Chip();
        readonly Label previewLine = new Label();
        readonly Label previewSub = new Label();
        readonly Label status = new Label();
        readonly Button run = Ui.Primary("开始打孔", 150, 36);
        // 保持关联：把"这个孔照哪个孔配的"写进孔特征，参考零件挪了以后能一键同步回去
        readonly CheckBox linkCheck = new CheckBox { Checked = true, Text = "保持与参考孔关联", AutoSize = true };

        // 关联孔位条：窗口一打开就查一遍，有错位的孔就亮出「同步孔位」
        readonly Panel linkBar = new Panel();
        readonly Label linkLabel = new Label();
        readonly Button syncButton = Ui.Primary("同步孔位", 96, 26);
        readonly Button rescanButton = Ui.Secondary("重新检查", 84, 26);
        SyncReport lastLinks;

        // "正在编辑谁 / 规格怎么来的"两行字（原来是单独一张大半空着的卡片）
        readonly Label editWho = new Label();
        readonly Label editNote = new Label();
        readonly ToolTip tips = new ToolTip { AutoPopDelay = 15000, InitialDelay = 400 };

        // 孔形状参考：左 2D 剖面（带尺寸），右 3D 半剖轴测
        readonly HoleSectionView sectionView = new HoleSectionView();
        readonly HoleModelView modelView = new HoleModelView();

        // 没选中参考孔时，参数面板编辑的是这份"草稿"规格。
        // 有了它，工程师可以先在面板上把孔型调出来、对着下面的剖面和 3D 参考看清楚，
        // 再去模型上点孔；而不是"没先点孔，改了参数也没反应"。
        HoleSpec draft = new HoleSpec {
            Kind = HoleKind.Through, HoleDiameter = 6, Depth = 0,
            Bottom = HoleBottom.Flat, BottomAngle = 118,
            CounterboreDiameter = 11, CounterboreDepth = 6.5,
            CountersinkDiameter = 12, CountersinkAngle = 90,
            ChamferSetback = 0.5, ChamferAngle = 45,
            ThreadFull = true, ThreadDepth = 12
        };

        readonly Segmented kindSeg = new Segmented("通孔", "螺纹孔", "圆柱沉孔", "锥形沉孔");
        readonly ComboBox sizeBox = Ui.Combo(84, "自定义");
        readonly ComboBox bottomBox = Ui.Combo(72, "平底", "V 型底");
        // 孔径框 3 位小数：螺纹内小径是 4.917 / 6.647 / 8.376 这种三位数。原来 2 位小数会把 4.917 圆成 4.92，
        // 用户一动别的参数，规格就按 4.92 重建 —— 不再等于天工孔标准表那一行，天工孔命令里又显示 *。
        readonly NumericUpDown diaNum = Ui.Num(0.1M, 500M, 6M, 3, 0.1M, 72);
        readonly NumericUpDown depthNum = Ui.Num(0.1M, 2000M, 10M, 2, 1M, 72);
        readonly NumericUpDown bottomAngNum = Ui.Num(30M, 179M, 118M, 0, 1M, 54);
        readonly NumericUpDown threadNum = Ui.Num(0.5M, 2000M, 12M, 2, 1M, 72);
        readonly NumericUpDown cboreDiaNum = Ui.Num(0.1M, 500M, 11M, 2, 0.5M, 72);
        readonly NumericUpDown cboreDepNum = Ui.Num(0.1M, 500M, 6.5M, 2, 0.5M, 72);
        readonly NumericUpDown csinkDiaNum = Ui.Num(0.1M, 500M, 12M, 2, 0.5M, 72);
        readonly NumericUpDown csinkAngNum = Ui.Num(10M, 179M, 90M, 0, 1M, 54);
        readonly NumericUpDown chamferSetNum = Ui.Num(0.1M, 20M, 0.5M, 2, 0.1M, 64);
        readonly NumericUpDown chamferAngNum = Ui.Num(10M, 89M, 45M, 0, 1M, 54);
        readonly RadioButton throughRadio = new RadioButton { Text = "贯通", AutoSize = true, Checked = true };
        readonly RadioButton blindRadio = new RadioButton { Text = "盲孔", AutoSize = true };
        readonly RadioButton threadFullRadio = new RadioButton { Text = "全长", AutoSize = true, Checked = true };
        readonly RadioButton threadPartRadio = new RadioButton { Text = "指定深度", AutoSize = true };
        readonly CheckBox chamferCheck = new CheckBox { Text = "孔口倒角", AutoSize = true };
        readonly Label bottomCaption = new Label { Text = "孔底", AutoSize = true, ForeColor = Ui.Muted };
        readonly Label refCount = new Label();
        readonly Label faceCount = new Label();

        readonly FieldGrid grid = new FieldGrid();
        FieldGrid.Row kindRow, sizeRow, depthRow, threadRow, cboreRow, csinkRow, chamferRow;
        Card specCard;             // 规格卡片：按显示的行数改高度
        VerticalStack rightStack;  // 改完高度要重排一次

        public AutoHoleForm(F.Application application, A.AssemblyDocument document){
            app = application; assembly = document;
            Ui.Shell(this, "自动打孔", 1040, 780, 900, 700);

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
            ShowLinkReport(null, assembly == null ? "（没有打开装配，不检查关联孔）" : "正在检查关联孔…");

            FormClosed += (s, e) => Cleanup();
            // 窗口出来以后再查关联孔：大装配要遍历一遍零件，别让窗口迟迟不出来
            Shown += (s, e) => { if (assembly != null) BeginInvoke((Action)(() => ScanLinks(false))); };
            // StartPicking 由宿主 ToolContext.Show 的启动回调调用，这里不能重复调，
            // 否则第二次 command.Start() 会触发 Terminate() 把窗口关掉。
        }

        // 自检：把所有「孔型 × 规格」组合都过一遍编辑器加载逻辑，返回发现的问题（空串 = 没问题）。
        // 用户报的 "InvalidArgument: 7 对于 SelectedIndex 无效" 就是规格下拉框没填数据导致的，
        // 这类问题靠它能在测试里直接抓到，不用等人点到。
        public string SelfCheck(){
            var bad = new List<string>();
            if (kindSeg.Items.Count != 4) bad.Add("孔型选项应有 4 个，实有 " + kindSeg.Items.Count);
            if (sizeBox.Items.Count != HoleMatcher.Table.Length + 1)
                bad.Add("规格下拉框应有 " + (HoleMatcher.Table.Length + 1) + " 项，实有 " + sizeBox.Items.Count);
            if (bottomBox.Items.Count != 2) bad.Add("孔底下拉框应有 2 项，实有 " + bottomBox.Items.Count);
            var probe = new RefGroup { DiameterMm = 8.5, Spec = HoleMatcher.Match(8.5).Target };
            groups.Add(probe);
            bool savedLoading = loading;
            try {
                RefreshList(0);
                for (int k = 0; k < kindSeg.Items.Count; k++) {
                    for (int s = 0; s < sizeBox.Items.Count; s++) {
                        try {
                            loading = true;
                            kindSeg.SelectedIndex = k;
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
        // ┌──────────── 左列：选东西 ────────────┬──────────── 右列：定规格 ────────────┐
        // │ ① 参考孔（列表，吃掉剩余高度）        │ ③ 孔规格（正在编辑谁 + 参数网格）       │
        // │   [☑ 点平面时自动识别面上的孔]         │ 孔形状（剖面 | 3D，吃掉剩余高度）       │
        // │ ② 打孔面                              │                                         │
        // ├──────────────────────────────────────┴─────────────────────────────────────────┤
        // │ ④ 预览：将打 N 个孔 · 每组打成什么                                                │
        // │ [开始打孔] [☑ 保持与参考孔关联]                              [全部重选] [关闭]     │
        // │ 关联孔位：xx 个关联孔，yy 个已错位                       [同步孔位] [重新检查]     │
        // │ 状态栏                                                                           │
        // └──────────────────────────────────────────────────────────────────────────────────┘
        void BuildLayout(){
            var columns = new SplitColumns(0.36f);
            var left = new VerticalStack { Padding = new Padding(0) };
            var right = rightStack = new VerticalStack { Padding = new Padding(0) };

            // ---- 左列 ----
            var refsCard = Ui.Card("① 参考孔", 0, "点孔口圆边 / 孔内壁 / 带孔的面");
            var delRef = Ui.Small("移除"); var clrRef = Ui.Small("清空");
            delRef.Click += (s, e) => RemoveSelected();
            clrRef.Click += (s, e) => { groups.Clear(); scannedPicks.Clear(); lastScanNote = ""; RefreshAll(); SetStatus("已清空参考孔。", Ui.Muted); };
            refsCard.AddHeader(delRef); refsCard.AddHeader(clrRef);
            refList.Dock = DockStyle.Fill; refList.IntegralHeight = false; refList.BorderStyle = BorderStyle.None;
            refList.DrawMode = DrawMode.OwnerDrawFixed; refList.ItemHeight = 46; refList.BackColor = Color.White;
            refList.DrawItem += DrawRefItem;
            var refFoot = new Panel { Dock = DockStyle.Bottom, Height = 30, BackColor = Color.White };
            scanFaceCheck.Location = new Point(0, 6); scanFaceCheck.BackColor = Color.White; scanFaceCheck.ForeColor = Ui.Text;
            refCount.AutoSize = false; refCount.Font = Ui.F8; refCount.ForeColor = Ui.Muted; refCount.TextAlign = ContentAlignment.MiddleRight;
            refFoot.Controls.Add(scanFaceCheck); refFoot.Controls.Add(refCount);
            refFoot.Resize += (s, e) => refCount.SetBounds(Math.Max(0, refFoot.Width - 130), 4, 130, 22);
            tips.SetToolTip(scanFaceCheck, "勾上：点一个带孔的平面，就把面上的孔全部加成参考孔（同孔径自动归一组）；面上没有孔才当打孔面。\r\n勾掉：点平面一律当打孔面。");
            refsCard.Controls.Add(refList); refsCard.Controls.Add(refFoot);

            var facesCard = Ui.Card("② 打孔面", 170, "孔心沿参考孔轴线投到这些面上");
            var delFace = Ui.Small("移除"); var clrFace = Ui.Small("清空");
            delFace.Click += (s, e) => RemoveSelectedFace();
            clrFace.Click += (s, e) => { targets.Clear(); targetLabels.Clear(); resolved.Clear(); drilledOnce = false; RefreshAll(); SetStatus("已清空打孔面。", Ui.Muted); };
            facesCard.AddHeader(delFace); facesCard.AddHeader(clrFace);
            faceList.Dock = DockStyle.Fill; faceList.IntegralHeight = false; faceList.BorderStyle = BorderStyle.None;
            faceList.DrawMode = DrawMode.OwnerDrawFixed; faceList.ItemHeight = 28; faceList.BackColor = Color.White;
            faceList.DrawItem += DrawFaceItem;
            var faceFoot = new Panel { Dock = DockStyle.Bottom, Height = 22, BackColor = Color.White };
            faceCount.Dock = DockStyle.Fill; faceCount.AutoSize = false; faceCount.Font = Ui.F8; faceCount.ForeColor = Ui.Muted;
            faceCount.TextAlign = ContentAlignment.MiddleRight;
            faceFoot.Controls.Add(faceCount);
            facesCard.Controls.Add(faceList); facesCard.Controls.Add(faceFoot);

            left.Add(refsCard, true); left.Add(facesCard);

            // ---- 右列 ----
            specCard = Ui.Card("③ 孔规格", 300, "默认按参考孔自动反推，改了实时生效");
            BuildSpecCard(specCard);
            var shapeCard = Ui.Card("孔形状", 0, "左：剖面尺寸　右：打完后的样子");
            var split = new SplitColumns(0.52f) { Dock = DockStyle.Fill, BackColor = Color.White };
            sectionView.Header = "";
            modelView.Header = "";
            split.Add(sectionView); split.Add(modelView);
            shapeCard.Controls.Add(split);
            right.Add(specCard); right.Add(shapeCard, true);

            columns.Add(left); columns.Add(right);

            // ---- 底部：预览 ----
            var previewCard = Ui.Card("④ 预览", 96, "打之前先看清楚要打几个、什么孔");
            var pvBody = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
            previewCard.Controls.Add(pvBody);
            chip.SetBounds(0, 0, 120, 24);
            previewLine.SetBounds(128, 2, 700, 22);
            previewLine.AutoSize = false; previewLine.Font = Ui.F9B; previewLine.ForeColor = Ui.Text; previewLine.AutoEllipsis = true;
            previewSub.SetBounds(0, 28, 824, 22);
            previewSub.AutoSize = false; previewSub.ForeColor = Ui.Muted; previewSub.AutoEllipsis = true;
            pvBody.Controls.Add(chip); pvBody.Controls.Add(previewLine); pvBody.Controls.Add(previewSub);
            pvBody.Resize += (s, e) => {
                int w = Math.Max(120, pvBody.ClientSize.Width);
                previewLine.Width = Math.Max(60, w - 130);
                previewSub.Width = w;
            };

            // ---- 底部：按钮 ----
            var actions = new Panel { Height = 40, BackColor = Ui.Bg };
            run.SetBounds(0, 2, 150, 36);
            linkCheck.Location = new Point(166, 11); linkCheck.BackColor = Ui.Bg; linkCheck.ForeColor = Ui.Text;
            tips.SetToolTip(linkCheck, "勾上：每个孔都记住它是照哪个参考孔配的。参考零件挪动（或参考孔改了位置）以后，\r\n点下面的「同步孔位」，所有配做孔会重新对准参考孔。");
            var clearAll = Ui.Secondary("全部重选", 96, 30);
            var close = Ui.Secondary("关闭", 80, 30);
            clearAll.Click += (s, e) => { groups.Clear(); targets.Clear(); targetLabels.Clear(); resolved.Clear(); scannedPicks.Clear(); lastScanNote = ""; drilledOnce = false; RefreshAll(); SetStatus("已全部清空，重新点孔。", Ui.Accent); };
            close.Click += (s, e) => Close();
            run.Click += (s, e) => RunDrill();
            tips.SetToolTip(run, "Ctrl+Enter");
            tips.SetToolTip(close, "Esc");
            actions.Controls.Add(run); actions.Controls.Add(linkCheck); actions.Controls.Add(clearAll); actions.Controls.Add(close);
            actions.Resize += (s, e) => {
                close.SetBounds(actions.Width - 80, 5, 80, 30);
                clearAll.SetBounds(actions.Width - 80 - 8 - 96, 5, 96, 30);
            };

            // ---- 底部：关联孔位条 ----
            linkBar.Height = 34; linkBar.BackColor = Ui.ChipBg;
            linkLabel.AutoSize = false; linkLabel.TextAlign = ContentAlignment.MiddleLeft; linkLabel.AutoEllipsis = true;
            linkLabel.ForeColor = Ui.Text; linkLabel.BackColor = Color.Transparent;
            syncButton.Click += (s, e) => SyncLinks();
            rescanButton.Click += (s, e) => ScanLinks(true);
            tips.SetToolTip(syncButton, "把和参考孔错位的孔挪回同轴（改孔特征的孔心位置，孔型规格不变）");
            linkBar.Controls.Add(linkLabel); linkBar.Controls.Add(syncButton); linkBar.Controls.Add(rescanButton);
            linkBar.Resize += (s, e) => {
                rescanButton.SetBounds(linkBar.Width - 84 - 4, 4, 84, 26);
                syncButton.SetBounds(rescanButton.Left - 6 - 96, 4, 96, 26);
                linkLabel.SetBounds(10, 0, Math.Max(40, syncButton.Left - 16), linkBar.Height);
            };

            status.Height = 24; status.Padding = new Padding(2, 0, 10, 0);
            status.TextAlign = ContentAlignment.MiddleLeft; status.ForeColor = Ui.Accent; status.BackColor = Ui.Bg;
            status.AutoEllipsis = true;
            status.Text = "点孔口圆边、孔内壁，或直接点一个带孔的面（面上的孔会自动认出来）。";

            // 纵向堆叠：添加顺序就是从上到下的顺序。两列那块吃掉剩余高度。
            var stack = new VerticalStack { Dock = DockStyle.Fill };
            stack.Add(columns, true);
            stack.Add(previewCard);
            stack.Add(actions);
            stack.Add(linkBar);
            stack.Add(status);
            Controls.Add(stack);
        }

        // 规格卡片：上面两行"正在编辑谁 / 规格怎么来的"，下面是参数网格。
        // 网格每一行都是"标签 + 一串控件"，位置由 FieldGrid 算，不再手摆坐标；
        // 换孔型时整行显示/隐藏，下面的行自动顶上去，不留空带。
        void BuildSpecCard(Card card){
            var body = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
            card.Controls.Add(body);
            editWho.AutoSize = false; editWho.Font = Ui.F9B; editWho.ForeColor = Ui.Text; editWho.AutoEllipsis = true;
            editNote.AutoSize = false; editNote.Font = Ui.F8; editNote.ForeColor = Ui.Muted; editNote.AutoEllipsis = true;
            body.Controls.Add(editWho); body.Controls.Add(editNote); body.Controls.Add(grid);

            kindSeg.Width = Math.Max(300, kindSeg.PreferredWidth);
            kindRow = grid.Add("孔型", kindSeg);
            sizeRow = grid.Add("规格", sizeBox, FieldGrid.Space(10), Ui.Caption("孔径"), diaNum, FieldGrid.Unit("mm"));
            depthRow = grid.Add("深度", throughRadio, blindRadio, depthNum, FieldGrid.Unit("mm"), FieldGrid.Space(10),
                                bottomCaption, bottomBox, bottomAngNum, FieldGrid.Unit("°"));
            threadRow = grid.Add("螺纹", threadFullRadio, threadPartRadio, threadNum, FieldGrid.Unit("mm"));
            cboreRow = grid.Add("沉孔", Ui.Caption("Φ"), cboreDiaNum, FieldGrid.Unit("mm"), FieldGrid.Space(10), Ui.Caption("深"), cboreDepNum, FieldGrid.Unit("mm"));
            csinkRow = grid.Add("锥孔", Ui.Caption("Φ"), csinkDiaNum, FieldGrid.Unit("mm"), FieldGrid.Space(10), Ui.Caption("角度"), csinkAngNum, FieldGrid.Unit("°"));
            chamferRow = grid.Add("倒角", chamferCheck, chamferSetNum, FieldGrid.Unit("mm"), Ui.Caption("×"), chamferAngNum, FieldGrid.Unit("°"));
            tips.SetToolTip(threadFullRadio, "贯通孔整孔攻丝，盲孔攻到孔底。");
            tips.SetToolTip(threadPartRadio, "从孔口往下攻指定深度（盲孔不能超过孔深）。\r\n螺纹孔按螺纹内小径建模，螺纹以装饰螺纹显示（和 CAD 自己的孔命令一致）。");
            tips.SetToolTip(bottomBox, "盲孔默认平底；需要钻尖就选 V 型底（默认 118°）。");

            EventHandler fit = (s, e) => {
                int w = Math.Max(80, body.ClientSize.Width);
                editWho.SetBounds(0, 0, w, 20);
                editNote.SetBounds(0, 20, w, 18);
                grid.SetBounds(0, 44, w, grid.PreferredHeight);
            };
            body.Resize += fit;
            fit(null, null);
        }

        // 规格卡片的高度：标题 34 + 两行说明 44 + 网格 + 底边 10
        int SpecCardHeight { get { return 34 + 44 + grid.PreferredHeight + 10; } }

        // 正在编辑谁 / 这个规格是怎么来的。
        void RefreshEditHeader(){
            var g = Selected;
            if (g == null || g.Spec == null) {
                editWho.Text = groups.Count == 0 ? "还没有参考孔 · 现在改的是草稿规格" : "没有选中参考孔 · 现在改的是草稿规格";
                editNote.Text = groups.Count == 0
                    ? "先在模型上点孔，规格会自动反推；也可以先在这里把孔型调出来，下面画的就是打出来的样子。"
                    : "在左边点一组参考孔就能改它的规格；草稿不影响已经选好的孔。";
                editNote.ForeColor = Ui.Muted;
            } else {
                editWho.Text = "正在编辑：" + g.Head + "　→　" + g.Target + "（改动作用于整组 " + g.Count + " 个孔）";
                string note = g.Spec.Note.Length > 0 ? g.Spec.Note : "规格已按你的选择手动设定。";
                string mi = g.MouthInfo;
                editNote.Text = mi.Length > 0 ? note + "　" + mi : note;
                editNote.ForeColor = Ui.Accent;
            }
            // 是不是天工孔标准表里的标准孔：是就写出是哪一行；不是就说明天工自带的孔命令里会显示 *
            var cur = (g != null && g.Spec != null) ? g.Spec : draft;
            if (cur != null) {
                editNote.Text = editNote.Text + "　·　" + HoleStandards.Describe(cur);
                if (HoleStandards.Resolve(cur) == null && editNote.ForeColor != Ui.Bad) editNote.ForeColor = Ui.Warn;
            }
            // 规格本身不成立（例如盲孔的螺纹深度超过孔深）：这一行改成红字说原因，开始打孔同时变灰
            string problem = cur == null ? "" : cur.ThreadProblem;
            if (problem.Length > 0) { editNote.Text = "⚠ " + problem; editNote.ForeColor = Ui.Bad; }
            tips.SetToolTip(editNote, editNote.Text);
        }

        // 参数控件当前值 -> 一个 HoleSpec。不依赖"有没有选中参考孔"：
        // 预览画的就是面板上这套参数，没选孔也能先把形状调出来看。
        HoleSpec EditorSpec(){
            var sp = new HoleSpec();
            sp.Kind = (HoleKind)Math.Max(0, kindSeg.SelectedIndex);
            sp.HoleDiameter = (double)diaNum.Value;
            sp.Depth = throughRadio.Checked ? 0 : (double)depthNum.Value;
            sp.Bottom = bottomBox.SelectedIndex == 1 ? HoleBottom.VBottom : HoleBottom.Flat;
            sp.BottomAngle = (double)bottomAngNum.Value;
            sp.CounterboreDiameter = (double)cboreDiaNum.Value;
            sp.CounterboreDepth = (double)cboreDepNum.Value;
            sp.CountersinkDiameter = (double)csinkDiaNum.Value;
            sp.CountersinkAngle = (double)csinkAngNum.Value;
            sp.Chamfer = chamferCheck.Checked;
            sp.ChamferSetback = (double)chamferSetNum.Value;
            sp.ChamferAngle = (double)chamferAngNum.Value;
            sp.ThreadFull = threadFullRadio.Checked;
            sp.ThreadDepth = (double)threadNum.Value;
            // 选「自定义」就必须把螺纹规格清掉，**不管当前孔型是不是螺纹孔**。
            // 原来的写法在"参考孔自动匹配出 M6 螺纹孔 → 用户把规格改成自定义"这条路上
            // 保留了 ThreadSize="M6"：于是孔径随便改、标注还写着 M6，模型上打的是对的孔，
            // 工程图/标注上是错的螺纹 —— 这种错误在生产里是要出事的。
            // 要打"自定义螺纹孔"就靠孔型=螺纹孔 + 手填孔径，标注留空（而不是留一个对不上的旧规格）。
            sp.ThreadSize = sizeBox.SelectedIndex > 0 ? HoleMatcher.Table[sizeBox.SelectedIndex - 1].Size : "";
            return sp;
        }

        // 实时重画两个孔形状视图。WriteBack / LoadEditor 每次都会调到这里，
        // 所以改一个数字、换一条参考孔、换孔型，图都会立刻变。
        void RefreshShapeViews(){
            HoleShape shape = null;
            try { shape = HoleShapeBuilder.Build(EditorSpec(), 0); }
            catch (Exception e) { Log.Write("AutoHoleShape", e); }
            sectionView.Shape = shape;
            modelView.Shape = shape;
        }

        void WireEvents(){
            kindSeg.SelectedIndexChanged += (s, e) => { if (!loading) ApplyKindChange(); };
            sizeBox.SelectedIndexChanged += (s, e) => { if (!loading) ApplySizeChange(); };
            bottomBox.SelectedIndexChanged += (s, e) => { if (!loading) WriteBack(); };
            // 单选按钮成对触发（一个取消、一个选中），只在"被选中"的那一下处理，免得写两遍
            throughRadio.CheckedChanged += (s, e) => { if (!loading && throughRadio.Checked) WriteBack(); };
            blindRadio.CheckedChanged += (s, e) => { if (!loading && blindRadio.Checked) WriteBack(); };
            threadFullRadio.CheckedChanged += (s, e) => { if (!loading && threadFullRadio.Checked) WriteBack(); };
            threadPartRadio.CheckedChanged += (s, e) => { if (!loading && threadPartRadio.Checked) WriteBack(); };
            chamferCheck.CheckedChanged += (s, e) => { if (!loading) WriteBack(); };
            foreach (var n in new NumericUpDown[]{ diaNum, depthNum, bottomAngNum, threadNum, cboreDiaNum, cboreDepNum, csinkDiaNum, csinkAngNum, chamferSetNum, chamferAngNum })
                n.ValueChanged += (s, e) => { if (!loading) WriteBack(); };
            refList.SelectedIndexChanged += (s, e) => { if (!loading) LoadEditor(); };
            // 打孔重试期间 CAD 正忙，这里把重试进度写到状态栏并让界面刷一次，
            // 用户看到的就不是"卡死了"，而是"正在重试 2/6"。
            AutoHoleWriter.Progress = msg => {
                SetStatus(msg, Ui.Warn);
                try { status.Update(); System.Windows.Forms.Application.DoEvents(); } catch { }
            };
        }

        // ---------------- 列表绘制 ----------------
        // 一组参考孔两行：上行「Φ6.6 ×4 → M6 螺纹孔（螺纹全长）」，下行灰字说明（规格是怎么来的 / 沉孔取下面的孔径）
        void DrawRefItem(object sender, DrawItemEventArgs e){
            e.DrawBackground();
            if (e.Index < 0 || e.Index >= groups.Count) return;
            var g = groups[e.Index];
            bool sel = (e.State & DrawItemState.Selected) != 0;
            using (var b = new SolidBrush(sel ? Ui.ChipBg : Color.White)) e.Graphics.FillRectangle(b, e.Bounds);
            if (sel) using (var b = new SolidBrush(Ui.Accent)) e.Graphics.FillRectangle(b, e.Bounds.X, e.Bounds.Y + 4, 3, e.Bounds.Height - 8);
            using (var p = new Pen(Ui.Line)) e.Graphics.DrawLine(p, e.Bounds.Left + 8, e.Bounds.Bottom - 1, e.Bounds.Right - 8, e.Bounds.Bottom - 1);
            int x = e.Bounds.X + 12, y = e.Bounds.Y + 6;
            var headSz = TextRenderer.MeasureText(e.Graphics, g.Head, Ui.F9B);
            TextRenderer.DrawText(e.Graphics, g.Head, Ui.F9B, new Point(x, y), sel ? Ui.AccentD : Ui.Text, TextFormatFlags.NoPadding);
            x += headSz.Width + 6;
            TextRenderer.DrawText(e.Graphics, "→", Ui.F9, new Point(x, y), Ui.Muted, TextFormatFlags.NoPadding);
            x += 18;
            var r1 = new Rectangle(x, y, Math.Max(0, e.Bounds.Right - x - 8), 18);
            TextRenderer.DrawText(e.Graphics, g.Target, Ui.F9, r1, Ui.Accent, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            string note = g.MouthInfo.Length > 0 ? g.MouthInfo : (g.Spec != null && g.Spec.Note.Length > 0 ? g.Spec.Note : " ");
            var r2 = new Rectangle(e.Bounds.X + 12, e.Bounds.Y + 26, e.Bounds.Width - 20, 16);
            TextRenderer.DrawText(e.Graphics, note, Ui.F8, r2, Ui.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }

        void DrawFaceItem(object sender, DrawItemEventArgs e){
            e.DrawBackground();
            if (e.Index < 0 || e.Index >= targetLabels.Count) return;
            bool sel = (e.State & DrawItemState.Selected) != 0;
            using (var b = new SolidBrush(sel ? Ui.ChipBg : Color.White)) e.Graphics.FillRectangle(b, e.Bounds);
            if (sel) using (var b = new SolidBrush(Ui.Accent)) e.Graphics.FillRectangle(b, e.Bounds.X, e.Bounds.Y + 4, 3, e.Bounds.Height - 8);
            using (var p = new Pen(Ui.Line)) e.Graphics.DrawLine(p, e.Bounds.Left + 8, e.Bounds.Bottom - 1, e.Bounds.Right - 8, e.Bounds.Bottom - 1);
            var r = new Rectangle(e.Bounds.X + 12, e.Bounds.Y + 6, e.Bounds.Width - 20, 18);
            TextRenderer.DrawText(e.Graphics, targetLabels[e.Index], Ui.F9, r, sel ? Ui.AccentD : Ui.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }

        // ---------------- 选择 ----------------
        public void StartPicking(){
            try {
                if (command != null) StopCommand();   // 幂等
                if (assembly == null) throw new InvalidOperationException("请先打开装配文件。");
                if (assembly.ReadOnly) throw new InvalidOperationException("请在可写的装配里运行（当前装配是只读打开）。");
                command = (F.ISECommand)app.CreateCommand((int)SolidEdgeConstants.seCmdFlag.seNoDeactivate);
                commandConnection = new Connection(command, typeof(F.ISECommandEvents), this);
                command.Start();
                mouse = (F.ISEMouse)command.Mouse;
                mouseConnection = new Connection(mouse, typeof(F.ISEMouseEvents), this);
                mouse.ScaleMode = 1; mouse.WindowTypes = 1;
                mouse.LocateMode = (int)SolidEdgeConstants.seLocateModes.seLocateSimple;
                mouse.EnabledMove = true;
                // 装配上下文里要允许跨零件选（点这个零件的孔、选那个零件的面）；
                // 但**原位编辑**（活动文档是零件）时不能开：实测一开就把上下文切回装配，
                // 随后写模型会被 CAD 拒绝（RPC_E_DISCONNECTED）——而原位编辑正是唯一能写的上下文。
                bool editingPart=false;
                try{editingPart=app.ActiveDocument is P.PartDocument;}catch{}
                ((F.ISEMouseEx)mouse).InterDocumentLocate=!editingPart;
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

        // 三种点法都从这里进来：
        //   点孔口的圆形边线 / 点孔的内壁圆柱面 → 加一个参考孔；
        //   点平面 → 勾着"自动认孔"就先把这个面上的孔全认出来（按孔径归类），
        //            一个孔都没扫到才当打孔面；勾掉则永远是打孔面。
        public void AcceptPick(object selected){
            if (busy || closing || selected == null) return;
            try {
                var pg = PickGeometry.Unwrap(selected);
                if (pg.Geometry is G.Edge) {
                    AddHole(AutoHoleReader.ReadReference(selected), selected, false);
                } else if (pg.Geometry is G.Face) {
                    var face = (G.Face)pg.Geometry;
                    if (!(face.Geometry is G.Plane)) {
                        string label;
                        var scanned = HoleScanCad.ScanSurface(selected, out label);
                        var r = AutoHoleReader.ToReference(scanned);
                        AddHole(r, selected, false);
                        SetStatus("已从" + label + "认出 Φ" + Num(r.DiameterMm)
                                  + (r.MouthDiameterMm - r.DiameterMm > HoleScan.StepToleranceMm
                                     ? ("　（孔口 Φ" + Num(r.MouthDiameterMm) + "，" + r.MouthKind + "，按下面的孔径配做）") : "")
                                  + "。", Ui.Ok);
                    } else if (scanFaceCheck.Checked) {
                        PickFaceWithHoles(selected);
                    } else {
                        AddTargetFace(selected, null);
                    }
                } else {
                    throw new ArgumentException("请点孔口的圆形边线、孔的内壁圆柱面（圆柱面/圆锥面），或者要打孔的平面。");
                }
                RefreshAll();
            } catch (Exception e) {
                SetStatus(e.Message, Ui.Warn);
                Log.Write("AutoHolePick", e);
            }
        }

        // 加一个参考孔：按孔径归到已有的那一类；没有就新建一类（规格自动反推）。
        // batch = 面扫描批量加：重复的孔静默跳过（返回 false），不打断整批。
        bool AddHole(ReferenceHole r, object selection, bool batch){
            var g = groups.FirstOrDefault(x => Math.Abs(x.DiameterMm - r.DiameterMm) <= HoleScan.GroupToleranceMm);
            bool isNew = g == null;
            HoleMatch match = null;
            if (isNew) { match = HoleMatcher.Match(r.DiameterMm); g = new RefGroup { DiameterMm = r.DiameterMm, Spec = match.Target }; groups.Add(g); }
            foreach (var old in g.Holes) {
                if ((old.Center - r.Center).Length < 1e-6) {
                    if (batch) return false;
                    throw new ArgumentException("这个孔已经选过了。");
                }
            }
            g.Holes.Add(r);
            if (selection != null) g.Selections.Add(selection);
            if (!batch) {
                RefreshList(groups.IndexOf(g));
                // 新的一组：把匹配结论（含"这个直径有歧义"）完整说出来，而不是只说"已匹配到 X"
                SetStatus(isNew ? HoleMatcher.StatusLine(match)
                                : ("同一组又加了一个 Φ" + Num(r.DiameterMm) + "，共 " + groups.Sum(x => x.Count) + " 个孔"),
                          isNew && match != null && match.Ambiguous ? Ui.Warn : Ui.Ok);
            }
            return true;
        }

        // 点平面 + 勾着"自动认孔"：面上有孔就全认出来当参考孔（**不当打孔面**）；
        // 一个孔都没有才当打孔面 —— 这样"点带孔的面"和"点要打孔的面"不用切模式。
        void PickFaceWithHoles(object selected){
            var t = AutoHoleReader.ReadTarget(selected);
            string how; int notHole;
            var holes = HoleScanCad.ScanFace(t, out how, out notHole);
            if (holes.Count == 0) {
                AddTargetFace(selected, t);
                SetStatus("这个面上没有识别到孔，已作为打孔面：" + FaceLabel(t) + (how.Length > 0 ? "　注意：" + how : ""), Ui.Muted);
                return;
            }
            int added = 0, dup = 0;
            foreach (var scanned in holes)
                if (AddHole(AutoHoleReader.ToReference(scanned), null, true)) added++; else dup++;
            scannedPicks.Add(selected);      // 只用于高亮：让用户看得见"刚才点的是哪张面"
            lastScanNote = HoleScan.StatusLine(holes)
                         + (dup > 0 ? "；其中 " + dup + " 个已经在列表里" : "")
                         + (notHole > 0 ? "；另有 " + notHole + " 个圆判定为凸台/非孔，已跳过" : "")
                         + "。要把这张面也当打孔面，请先勾掉「点平面时自动识别面上的孔」再点一次。";
            SetStatus("已从 " + FaceLabel(t) + " 上识别到 " + holes.Count + " 个孔，共 " + groups.Count + " 组（新增 " + added + " 个）。", Ui.Ok);
        }

        // 加一个打孔面（老行为）。known 非空时不再重复解析面片。
        void AddTargetFace(object selected, TargetFace known){
            var t = known != null ? known : AutoHoleReader.ReadTarget(selected);
            foreach (var existing in targets) {
                try {
                    var old = AutoHoleReader.ReadTarget(existing);
                    if (old.Part == t.Part && old.Face.ID == t.Face.ID) throw new ArgumentException("这个面已经选过了。");
                } catch (ArgumentException) { throw; }
                catch { }    // 老面片已经失效（打完孔了）：判断不了就当它不是重复的
            }
            targets.Add(selected);
            targetLabels.Add(FaceLabel(t));
            while (resolved.Count < targets.Count - 1) resolved.Add(null);
            resolved.Add(t);
            drilledOnce = false;
            RefreshAll();
            SetStatus("已选 " + targets.Count + " 个打孔面：" + FaceLabel(t), Ui.Ok);
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
                refCount.Text = groups.Count == 0 ? "共 0 个孔"
                    : ("共 " + groups.Count + " 组 " + groups.Sum(x => x.Count) + " 个孔");
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
            faceCount.Text = "共 " + targets.Count + " 个面";
        }

        void LoadEditor(){
            // 选中的那一组优先，没选中就加载草稿 —— 两条路共用同一套加载代码，
            // 免得"有参考孔"和"没参考孔"两种状态下界面长得不一样。
            var g = Selected;
            var sp = (g != null && g.Spec != null) ? g.Spec : draft;
            loading = true;
            try {
                kindSeg.SelectedIndex = (int)sp.Kind;
                int si = 0;
                if (sp.ThreadSize != null && sp.ThreadSize.Length > 0) {
                    var r = HoleMatcher.Find(sp.ThreadSize);
                    si = r.HasValue ? Array.IndexOf(HoleMatcher.Table, r.Value) + 1 : 0;
                }
                sizeBox.SelectedIndex = si;
                diaNum.Value = Ui.Clamp((decimal)Math.Round(sp.HoleDiameter, 3), diaNum.Minimum, diaNum.Maximum);
                throughRadio.Checked = sp.Through; blindRadio.Checked = !sp.Through;
                depthNum.Value = Ui.Clamp((decimal)Math.Round(sp.Depth <= 0 ? 10 : sp.Depth, 2), depthNum.Minimum, depthNum.Maximum);
                bottomBox.SelectedIndex = sp.Bottom == HoleBottom.VBottom ? 1 : 0;
                bottomAngNum.Value = Ui.Clamp((decimal)Math.Round(sp.BottomAngle, 0), bottomAngNum.Minimum, bottomAngNum.Maximum);
                threadFullRadio.Checked = sp.ThreadFull; threadPartRadio.Checked = !sp.ThreadFull;
                threadNum.Value = Ui.Clamp((decimal)Math.Round(sp.ThreadDepth, 2), threadNum.Minimum, threadNum.Maximum);
                cboreDiaNum.Value = Ui.Clamp((decimal)Math.Round(sp.CounterboreDiameter, 2), cboreDiaNum.Minimum, cboreDiaNum.Maximum);
                cboreDepNum.Value = Ui.Clamp((decimal)Math.Round(sp.CounterboreDepth, 2), cboreDepNum.Minimum, cboreDepNum.Maximum);
                csinkDiaNum.Value = Ui.Clamp((decimal)Math.Round(sp.CountersinkDiameter, 2), csinkDiaNum.Minimum, csinkDiaNum.Maximum);
                csinkAngNum.Value = Ui.Clamp((decimal)Math.Round(sp.CountersinkAngle, 0), csinkAngNum.Minimum, csinkAngNum.Maximum);
                chamferCheck.Checked = sp.Chamfer;
                chamferSetNum.Value = Ui.Clamp((decimal)Math.Round(sp.ChamferSetback, 2), chamferSetNum.Minimum, chamferSetNum.Maximum);
                chamferAngNum.Value = Ui.Clamp((decimal)Math.Round(sp.ChamferAngle, 0), chamferAngNum.Minimum, chamferAngNum.Maximum);
            } finally { loading = false; }
            ShowKindFields();
            RefreshEditHeader();
            RefreshShapeViews();
        }

        void ShowKindFields(){
            var k = (HoleKind)Math.Max(0, kindSeg.SelectedIndex);
            grid.Show(threadRow, k == HoleKind.Tapped);
            grid.Show(cboreRow, k == HoleKind.Counterbore);
            grid.Show(csinkRow, k == HoleKind.Countersink);
            sizeBox.Enabled = k != HoleKind.Through;            // 通孔不需要螺纹规格
            throughRadio.Enabled = k != HoleKind.Countersink;   // 锥形沉孔本身就是贯通的定位
            blindRadio.Enabled = throughRadio.Enabled;
            depthNum.Enabled = blindRadio.Checked && blindRadio.Enabled;
            bottomBox.Enabled = blindRadio.Checked;
            bottomCaption.Enabled = bottomBox.Enabled;
            bottomAngNum.Enabled = blindRadio.Checked && bottomBox.SelectedIndex == 1;
            threadNum.Enabled = threadPartRadio.Checked;
            chamferCheck.Enabled = k != HoleKind.Countersink;
            chamferSetNum.Enabled = chamferAngNum.Enabled = chamferCheck.Enabled && chamferCheck.Checked;
            // 行数变了：网格和卡片跟着变高/变矮，下面的孔形状卡片吃掉剩下的高度
            grid.Height = grid.PreferredHeight;
            if (specCard != null && specCard.Height != SpecCardHeight) {
                specCard.Height = SpecCardHeight;
                if (rightStack != null) rightStack.Relayout();
            }
        }

        // 换孔型 / 换规格都要按标准表重填尺寸。选中了参考孔就改那一组；没选中就改草稿。
        // 两条路必须一样 —— 否则"没点孔的时候选 M6，孔径还停在 6.00"，看着像没生效。
        void ApplyKindChange(){
            var g = Selected;
            var kind = (HoleKind)kindSeg.SelectedIndex;
            var cur = (g != null && g.Spec != null) ? g.Spec : EditorSpec();
            var fresh = HoleMatcher.FromRow(RowOf(cur, g != null ? g.DiameterMm : cur.HoleDiameter), kind);
            CopyUserEdits(cur, fresh);
            fresh.Note = "";          // 规格变了，旧备注（"参考孔 Φx 是 Mx 的底孔…"）不再准确
            CommitSpec(g, fresh);
        }

        // 用户手改过的那几项要跟着走：孔型/规格一换就把"贯通改盲孔、加了倒角、螺纹只攻 10mm"全冲掉，是不能接受的。
        // 例外：螺纹深度只有"用户指定过"才带过去；还是全长时，指定深度跟着新规格的默认值走（M6→12、M8→16）。
        static void CopyUserEdits(HoleSpec from, HoleSpec to){
            to.Depth = from.Depth;
            to.Bottom = from.Bottom;
            to.BottomAngle = from.BottomAngle;
            to.Chamfer = from.Chamfer;
            to.ChamferSetback = from.ChamferSetback;
            to.ChamferAngle = from.ChamferAngle;
            to.ThreadFull = from.ThreadFull;
            if (!from.ThreadFull) to.ThreadDepth = from.ThreadDepth;
            else if (to.ThreadSize.Length == 0) to.ThreadDepth = from.ThreadDepth;
        }

        // 有选中的参考孔就写那一组，否则写草稿。
        void CommitSpec(RefGroup g, HoleSpec spec){
            if (g != null && g.Spec != null) g.Spec = spec; else draft = spec;
            LoadEditor();
            WriteBack();
        }

        // 用哪个标准行：优先看已经选定的螺纹规格，否则按参考孔直径/当前孔径去匹配。
        ThreadRow RowOf(HoleSpec sp, double referenceDiameterMm){
            if (sp != null && sp.ThreadSize != null && sp.ThreadSize.Length > 0) {
                var r = HoleMatcher.Find(sp.ThreadSize);
                if (r.HasValue) return r.Value;
            }
            var m = HoleMatcher.Match(referenceDiameterMm > 0 ? referenceDiameterMm : 6.0);
            return m.HasRow ? m.Row : HoleMatcher.Table[3];
        }

        void ApplySizeChange(){
            var g = Selected;
            int si = sizeBox.SelectedIndex;
            if (si <= 0) { WriteBack(); return; }          // 自定义：尺寸按手填的来，不动
            var row = HoleMatcher.Table[si - 1];
            loading = true;
            try {
                var cur = (g != null && g.Spec != null) ? g.Spec : EditorSpec();
                var fresh = HoleMatcher.FromRow(row, (HoleKind)kindSeg.SelectedIndex);
                CopyUserEdits(cur, fresh);
                fresh.Note = "";
                if (g != null && g.Spec != null) g.Spec = fresh; else draft = fresh;
            } finally { loading = false; }
            LoadEditor();
            WriteBack();
        }

        void WriteBack(){
            var g = Selected;
            if (g == null || g.Spec == null) {
                // 没选中参考孔：面板上的值存成草稿。"孔形状"照样实时跟着变，
                // 这样工程师可以先把孔型调出来看清楚，再去模型上点孔。
                draft = EditorSpec();
                // ShowKindFields 一定要调：它负责"哪些框能用"。漏了它，
                // 勾上"盲孔"以后深度框还是灰的 —— 用户根本填不了深度。
                ShowKindFields();
                RefreshEditHeader();
                RefreshPreview();
                RefreshShapeViews();
                return;
            }
            // 规格一律由 EditorSpec() 从控件现读，不再"在旧 spec 上逐项覆盖"。
            // 覆盖式写法漏掉任何一项，都会留下上一次的残留值（"自定义规格却还写着 M6"就是这么来的）。
            var sp = EditorSpec();
            sp.Note = "";
            g.Spec = sp;
            int idx = refList.SelectedIndex;
            loading = true;
            try { if (idx >= 0 && idx < refList.Items.Count) refList.Items[idx] = g.Head; }
            finally { loading = false; }
            refList.Invalidate();
            ShowKindFields();
            RefreshEditHeader();
            RefreshPreview();
            RefreshShapeViews();
        }

        void RemoveSelected(){
            int i = refList.SelectedIndex;
            if (i < 0 || i >= groups.Count) { SetStatus("先在列表里选中一组参考孔。", Ui.Warn); return; }
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
            var perSpec = new List<string>(); var specCount = new Dictionary<string, int>();
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
                        string name = HoleNaming.Short(g.Spec);
                        if (!specCount.ContainsKey(name)) { specCount[name] = 0; perSpec.Add(name); }
                        specCount[name]++;
                    } catch { skip++; }
                }
                total += here; outside += skip;
                if (here > 0 || skip > 0) perFace.Add(t.PartName + " " + here + " 个" + (skip > 0 ? "（跳过 " + skip + "）" : ""));
            }
            int refTotal = groups.Sum(g => g.Count);
            if (refTotal == 0) {
                chip.Text = "等待点孔"; chip.ChipColor = Ui.Muted;
                previewLine.Text = "① 先在模型上点孔：点圆边、点孔内壁，或直接点一个带孔的面";
                previewSub.Text = "点一个充满孔的面会把面上的孔全认出来，同孔径的自动归一组；沉孔/锥沉/倒角孔口自动取孔口下面的孔径来配做。";
            } else if (targets.Count == 0) {
                chip.Text = "已选 " + refTotal + " 个孔"; chip.ChipColor = Ui.Accent;
                previewLine.Text = "② 再点要打孔的面";
                previewSub.Text = groups.Count + " 组参考孔：" + string.Join("；", groups.Select(g => g.Head + " → " + g.Target).ToArray())
                                  + (lastScanNote.Length > 0 ? "　—　" + lastScanNote : "");
            } else {
                chip.Text = "将打 " + total + " 个孔"; chip.ChipColor = total > 0 ? Ui.Ok : Ui.Warn;
                previewLine.Text = total > 0
                    ? string.Join("；", perSpec.Select(n => n + " ×" + specCount[n]).ToArray())
                    : "没有孔可以打，请看下面的原因";
                string s;
                if (drilledOnce) {
                    s = "这批打孔面已经用过了（打完孔后原来的面对象会失效），请重新点一个面；参考孔和规格都还在，不用重选。";
                } else {
                    s = "打孔面：" + string.Join("；", perFace.ToArray());
                    if (outside > 0 && total == 0)
                        s = "选中的面与参考孔不同轴：孔心的投影落在面外面，所以没有孔可打。"
                          + "请点与这些孔轴线相交的那个面（比如孔正上方/正下方零件的那个面）。";
                    else if (outside > 0)
                        s += "　—　另有 " + outside + " 个孔心投影落在面外，会自动跳过。";
                    if (total > 0) s += "　·　同规格的孔会编成一个特征组";
                }
                // 锥形沉孔的默认锥孔直径是按螺钉头径估算的，必须让用户看见这句话
                foreach (var g in groups) {
                    if (g.Spec != null && g.Spec.Kind == HoleKind.Countersink && g.Spec.Note.Length > 0) {
                        s += "　⚠ " + g.Head + "：" + g.Spec.Note;
                        break;
                    }
                }
                foreach (var g in groups) {
                    if (g.Spec == null) continue;
                    string tp = g.Spec.ThreadProblem;
                    if (tp.Length > 0) { s = "⚠ " + g.Head + "：" + tp + "　" + s; break; }
                }
                previewSub.Text = s;
            }
            tips.SetToolTip(previewSub, previewSub.Text);
            tips.SetToolTip(previewLine, previewLine.Text);
            bool specOk = groups.All(g => g.Spec == null || g.Spec.ThreadProblem.Length == 0);
            run.Enabled = total > 0 && !busy && !drilledOnce && specOk;
            if (highlights != null) {
                try {
                    highlights.RemoveAll();
                    foreach (var t in targets) { try { highlights.AddItem(t); } catch { } }
                    // 参考孔也高亮，用户才看得见自己点了哪几个孔。
                    // 打完孔以后这些选择引用会失效，失效的直接跳过（打孔用的是纯数据，不依赖它们）。
                    foreach (var g in groups) foreach (var s in g.Selections) { try { highlights.AddItem(s); } catch { } }
                    // 被"面扫描"点过的那张面也高亮（它不是打孔面，但用户得看见自己点的是哪张）
                    foreach (var s in scannedPicks) { try { highlights.AddItem(s); } catch { } }
                    highlights.Draw();
                } catch (Exception e) { Log.Write("AutoHoleHighlight", e); }
            }
            chip.Invalidate();
        }

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

        // ---------------- 关联孔位 ----------------
        void ShowLinkReport(SyncReport report, string fallback){
            lastLinks = report;
            if (report == null) {
                linkLabel.Text = "关联孔位：" + fallback;
                linkLabel.ForeColor = Ui.Muted;
                syncButton.Enabled = false;
                syncButton.Text = "同步孔位";
                rescanButton.Enabled = assembly != null;
                return;
            }
            linkLabel.Text = "关联孔位：" + report.Summary;
            linkLabel.ForeColor = report.Misplaced > 0 ? Ui.Warn : (report.Broken > 0 ? Ui.Warn : Ui.Text);
            syncButton.Enabled = report.Misplaced > 0 && !busy;
            syncButton.Text = report.Misplaced > 0 ? "同步孔位 (" + report.Misplaced + ")" : "同步孔位";
            rescanButton.Enabled = !busy;
            var detail = new List<string>();
            foreach (var it in report.Items) {
                if (it.NeedsMove) detail.Add(it.Label + "：偏了 " + it.OffsetMm.ToString("0.###", CultureInfo.InvariantCulture) + "mm");
                else if (it.Problem.Length > 0) detail.Add(it.Label + "：" + it.Problem);
                if (detail.Count >= 12) { detail.Add("……"); break; }
            }
            tips.SetToolTip(linkLabel, detail.Count == 0 ? report.Summary : string.Join("\r\n", detail.ToArray()));
        }

        // 查一遍装配里的关联孔。manual = 用户点了「重新检查」（要在状态栏给回应）。
        void ScanLinks(bool manual){
            if (assembly == null || closing) return;
            if (busy) return;
            try {
                UseWaitCursor = true;
                var report = HoleSync.Scan(assembly);
                ShowLinkReport(report, "");
                if (manual) SetStatus(report.Summary, report.Misplaced > 0 ? Ui.Warn : Ui.Ok);
            } catch (Exception e) {
                Log.Write("AutoHoleLinkScan", e);
                ShowLinkReport(null, "检查失败：" + e.Message);
            } finally { UseWaitCursor = false; }
        }

        // 一键同步：先重新查一遍（别拿着几分钟前的结果去改模型），再把错位的孔挪回去。
        void SyncLinks(){
            if (assembly == null || busy || closing) return;
            hadCommand = command != null;
            try {
                busy = true; UseWaitCursor = true; syncButton.Enabled = false; run.Enabled = false;
                SetStatus("正在同步孔位…", Ui.Accent);
                StopCommand();
                var report = HoleSync.Scan(assembly);
                int want = report.Misplaced;
                if (want == 0) {
                    ShowLinkReport(report, "");
                    SetStatus("所有关联孔都已和参考孔同轴，不用同步。", Ui.Ok);
                } else {
                    HoleSync.Apply(report);
                    string msg = "同步孔位：挪回 " + report.Moved + " 个孔" + (report.Failed > 0 ? "，失败 " + report.Failed + " 个" : "") + "。";
                    if (report.Notes.Count > 0) msg += "\r\n\r\n说明：\r\n· " + string.Join("\r\n· ", report.Notes.ToArray());
                    msg += "\r\n\r\n请保存装配。";
                    LastResult = msg;
                    if (!SuppressDialogs)
                        MessageBox.Show(this, msg, "同步孔位", MessageBoxButtons.OK, report.Failed > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
                    SetStatus("同步完成：挪回 " + report.Moved + " 个孔" + (report.Failed > 0 ? "，失败 " + report.Failed + " 个" : "") + "。",
                              report.Failed > 0 ? Ui.Warn : Ui.Ok);
                    // 模型改过了：打孔面的面片缓存全部作废，下次用到时重读
                    resolved.Clear();
                    busy = false;
                    ShowLinkReport(HoleSync.Scan(assembly), "");
                }
            } catch (Exception e) {
                Log.Write("AutoHoleSync", e);
                SetStatus("同步孔位失败：" + e.Message, Ui.Bad);
                LastResult = "同步中断：" + e.Message;
                if (!SuppressDialogs) MessageBox.Show(this, e.Message, "同步孔位", MessageBoxButtons.OK, MessageBoxIcon.Error);
            } finally {
                busy = false; UseWaitCursor = false;
                if (hadCommand) { try { StartPicking(); } catch (Exception ex) { SetStatus(ex.Message, Ui.Warn); } }
                if (lastLinks != null) ShowLinkReport(lastLinks, "");
                RefreshPreview();
            }
        }

        // ---- 测试 / 真机实测脚本用的缝（给自动化读界面状态、免弹框）----
        // 读到的就是窗口上看到的那一份数据；打孔走的是和点按钮同一条路。
        public bool SuppressDialogs;       // 真机脚本里置真：结果框改为写进 LastResult
        public string LastResult = "";
        public void DrillForTest(){ RunDrill(); }
        public void SyncForTest(){ SyncLinks(); }
        public SyncReport LinkReportForTest(){ ScanLinks(false); return lastLinks; }
        public bool KeepLink { get { return linkCheck.Checked; } set { linkCheck.Checked = value; } }
        // 不连 CAD 也能把参考孔列表填起来（界面截图 / 界面单测用）：count 个同孔径的假参考孔，规格照常自动反推
        public void AddReferenceForTest(double diameterMm, int count){
            for (int i = 0; i < count; i++)
                AddHole(new ReferenceHole { Center = new V3(0.01 * groups.Count + 0.02 * i, 0.03, 0), Axis = new V3(0, 0, 1),
                                            DiameterMm = diameterMm, MouthDiameterMm = diameterMm, Source = "测试" }, null, true);
            RefreshList(groups.Count - 1);
            RefreshPreview();
        }
        public string UiSummary(){
            var sb = new System.Text.StringBuilder();
            sb.Append("参考孔 ").Append(groups.Count).Append(" 组 [");
            foreach (var g in groups) sb.Append("(").Append(g.Head).Append(" → ").Append(g.Detail).Append(") ");
            sb.Append("] 打孔面 ").Append(targets.Count).Append(" 个 [");
            foreach (var s in targetLabels) sb.Append(s).Append(" | ");
            sb.Append("] 可打孔=").Append(run.Enabled).Append(" 徽标=").Append(chip.Text);
            sb.Append(" 预览=").Append(previewLine.Text).Append(" / ").Append(previewSub.Text);
            sb.Append(" 关联=").Append(linkLabel.Text);
            sb.Append(" 状态=").Append(status.Text);
            return sb.ToString();
        }

        // ---------------- 打孔 ----------------
        bool hadCommand;
        void RunDrill(){
            if (busy || closing) return;
            try {
                if (groups.Count == 0) { SetStatus("请先点一个已有的孔（点孔口圆边、孔内壁，或点一个带孔的面自动认孔）。", Ui.Warn); return; }
                if (targets.Count == 0) { SetStatus("请先点要打孔的面（面上有孔时会被当成参考孔；要打孔的面请点一个没有孔的面，或勾掉「点平面时自动识别面上的孔」）。", Ui.Warn); return; }
                // 打完孔以后，那批面的面片对象已经失效。再点一次会对着死对象打：
                // 轻则每个孔都失败，重则 CAD 报一堆"对象已断开" —— 用户会以为软件坏了。
                if (drilledOnce) {
                    SetStatus("这批打孔面已经用过了（打完孔后原来的面对象会失效）。请重新点一个面，或点「全部重选」。", Ui.Warn);
                    return;
                }
                foreach (var g in groups) {
                    string tp = g.Spec == null ? "" : g.Spec.ThreadProblem;
                    if (tp.Length > 0) { SetStatus(g.Head + "：" + tp, Ui.Warn); return; }
                }
                // 原位编辑（双击零件进入）**不是**错误：CAD 只在"正在编辑的那个零件文档"里允许写模型，
                // 所以这里放开它。只读装配仍然拒绝。
                if (assembly.ReadOnly) throw new InvalidOperationException("装配是只读打开的，无法打孔。请用可写方式打开装配后再试。");

                busy = true; run.Enabled = false; run.Text = "打孔中…"; UseWaitCursor = true;
                hadCommand = command != null;   // 打完只恢复"原来就在选"的状态（外部脚本驱动时没有选择命令）
                StopCommand();

                var refs = new List<ReferenceHole>(); var specs = new List<HoleSpec>();
                foreach (var g in groups) foreach (var h in g.Holes) { refs.Add(h); specs.Add(g.Spec); }
                // 沉孔/锥沉/倒角孔口：配做用的是孔口**下面**那个孔径，结果框里要说一句，
                // 否则用户看到"参考孔 Φ11、打出来的是 Φ6.6 配 M6"会以为配错了。
                int stepped = 0;
                foreach (var h in refs) if (h.MouthDiameterMm - h.DiameterMm > HoleScan.StepToleranceMm) stepped++;

                // 保持关联：开打前建一次装配实例索引；每个孔记住它照哪个参考孔配的
                HoleLinkFactory.Session linkSession = linkCheck.Checked ? HoleLinkFactory.Begin(assembly) : null;
                var unlinked = new List<string>();

                int made = 0, skipped = 0, failed = 0, already = 0, linked = 0;
                var problems = new List<string>(); var methods = new List<string>(); var audits = new List<string>();
                var features = new List<string>();
                var resolved0 = new List<TargetFace>();   // 这一批真正打过的零件（收尾时作废它们的圆缓存）
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
                        var req = new AutoHoleWriter.HoleRequest { Spec = specs[i], Centre = centre,
                            Source = partName + " Φ" + Num(hole.DiameterMm) };
                        if (linkSession != null) {
                            string why;
                            req.Link = HoleLinkFactory.Make(linkSession, hole, t, out why);
                            if (req.Link == null && why.Length > 0 && !unlinked.Contains(why)) unlinked.Add(why);
                        }
                        requests.Add(req);
                    }
                    if (requests.Count == 0) continue;
                    // 打孔在主进程内完成。实测（2026-09-27）：进程内、跨进程都能写模型；
                    // 真正会失败的是"往已经有孔的位置再打"——那由 Friendly() 和预检给出人话提示。
                    resolved0.Add(t);   // 打完要作废这些零件的圆缓存（见下）
                    var res = AutoHoleWriter.DrillRequests(t, requests);
                    made += res.Created; failed += res.Failures.Count; already += res.AlreadyOk; linked += res.Linked;
                    foreach (var note in res.Notes) AddProblem(problems, note);
                    if (res.Method.Length > 0 && !methods.Contains(res.Method)) methods.Add(res.Method);
                    if (res.Audit.Length > 0 && !audits.Contains(res.Audit)) audits.Add(res.Audit);
                    foreach (var f in res.Features) features.Add(partName + "：" + f);
                    foreach (var pf in res.Failures) AddProblem(problems, pf);
                }
                foreach (var why in unlinked) AddProblem(problems, "有的孔没能关联参考孔：" + why + "（孔照常打好了，只是参考零件挪动后不会自动跟上）");

                string msg = "共打孔 " + made + " 个";
                if (already > 0) msg += "，" + already + " 个位置本来就有孔（见下方说明）";
                if (skipped > 0) msg += "，跳过 " + skipped + " 个（投影不落在所选面上）";
                if (failed > 0) msg += "，失败 " + failed + " 个";
                if (stepped > 0) msg += "\r\n其中 " + stepped + " 个参考孔是沉孔/锥沉/倒角孔口，按孔口下面的孔径配做";
                msg += "\r\n打孔面 " + targets.Count + " 个";
                if (methods.Count > 0) msg += "\r\n生成方式：" + string.Join("／", methods.ToArray());
                if (features.Count > 0) msg += "\r\n\r\n特征树里：\r\n· " + string.Join("\r\n· ", features.ToArray());
                if (linkSession != null && made > 0)
                    msg += "\r\n\r\n已关联参考孔 " + linked + " 个：参考零件挪动后，点窗口底部的「同步孔位」，孔会重新对准。";
                if (audits.Count > 0) msg += "\r\n\r\n自检：" + string.Join("；", audits.ToArray());
                msg += "\r\n\r\n请保存装配。";
                if (problems.Count > 0) msg += "\r\n\r\n说明：\r\n· " + string.Join("\r\n· ", problems.ToArray());
                // 只有"真的失败/自检异常"才用警告图标；"位置本来就有孔"是正常情况，不该吓人。
                LastResult = msg;
                if (!SuppressDialogs)
                    MessageBox.Show(this, msg, "自动打孔完成", MessageBoxButtons.OK,
                        (failed > 0 || audits.Count > 0) ? MessageBoxIcon.Warning : MessageBoxIcon.Information);

                // 这批面已经用掉了：面片对象失效，解析缓存也一并作废，逼着用户重新点一个面。
                // 参考孔（纯数据）和规格保留 —— 换个面就能接着打同一批孔，不用重选。
                drilledOnce = true;
                resolved.Clear();
                foreach (var g in groups) g.Selections.Clear();   // 高亮引用也失效了，别再往上加
                scannedPicks.Clear();
                // 打完孔模型变了：面扫描的圆缓存要作废，下一张面重新读一遍。
                foreach (var t in resolved0) if (t != null && t.Part != null) HoleScanCad.Invalidate(t.Part);
                SetStatus("完成：打孔 " + made + " 个" + (skipped > 0 ? "，跳过 " + skipped : "") + (failed > 0 ? "，失败 " + failed : "")
                          + "　—　这批打孔面已用完，换个面打下一批（参考孔不用重选）。",
                          failed > 0 ? Ui.Warn : Ui.Ok);
                busy = false; run.Text = "开始打孔"; UseWaitCursor = false;
                if (hadCommand) { try { StartPicking(); } catch (Exception ex) { SetStatus(ex.Message, Ui.Warn); } }
                if (made > 0) ScanLinks(false);
                RefreshPreview();
            } catch (Exception e) {
                Log.Write("AutoHoleDrill", e);
                busy = false; run.Text = "开始打孔"; UseWaitCursor = false;
                try { if (hadCommand && command == null) StartPicking(); } catch { }
                SetStatus(e.Message, Ui.Bad);
                LastResult = "打孔中断：" + e.Message;
                if (!SuppressDialogs) MessageBox.Show(this, e.Message, "自动打孔", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
            groups.Clear(); targets.Clear(); targetLabels.Clear(); resolved.Clear(); scannedPicks.Clear();
            lastScanNote = "";
            drilledOnce = false;
            try { tips.Dispose(); } catch { }
        }

        public new void MouseClick(short b, short s, double x, double y, double z, object w, int k, object g){
            if (b == 1) { if (g == null) { SetStatus("这里没有捕捉到对象。请点到孔口的圆边，或零件平面上。", Ui.Warn); return; } AcceptPick(g); }
            else if (b == 2) Close();
        }
        public new void MouseDown(short b, short s, double x, double y, double z, object w, int k, object g){ }
        public new void MouseUp(short b, short s, double x, double y, double z, object w, int k, object g){ }
        public new void MouseMove(short b, short s, double x, double y, double z, object w, int k, object g){
            if (closing || busy) return;
            if (g == null) SetStatus("可以点：孔口的圆形边线、孔的内壁圆柱面、带孔的面（自动认孔）、要打孔的平面。", Ui.Muted);
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
