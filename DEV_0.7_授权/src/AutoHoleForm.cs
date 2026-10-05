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
        readonly CheckBox scanFaceCheck = new CheckBox { Checked = true };
        string lastScanNote = "";   // 上一次面扫描的结论，显示在预览的说明行里

        readonly ListBox refList = new ListBox();
        readonly ListBox faceList = new ListBox();
        readonly Chip chip = new Chip();
        readonly Label previewLine = new Label();
        readonly Label previewSub = new Label();
        readonly Label status = new Label();
        readonly Button run = Ui.Primary("开始打孔", 168, 38);

        // 规格来源卡片上的三行字（原来是挤在参数卡片右下角的 8.25pt 小字）
        readonly Label srcWho = new Label();
        readonly Label srcNote = new Label();
        readonly Label srcHint = new Label();

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
            ChamferSetback = 0.5, ChamferAngle = 45
        };

        readonly ComboBox kindBox = Ui.Combo(92, "通孔", "螺纹孔", "圆柱沉孔", "锥形沉孔");
        readonly ComboBox sizeBox = Ui.Combo(84, "自定义");
        readonly ComboBox bottomBox = Ui.Combo(66, "平底", "V 型底");
        readonly NumericUpDown diaNum = Ui.Num(0.1M, 500M, 6M, 2, 0.1M, 66);
        readonly NumericUpDown depthNum = Ui.Num(0.1M, 2000M, 10M, 2, 1M, 66);
        readonly NumericUpDown bottomAngNum = Ui.Num(30M, 179M, 118M, 0, 1M, 50);
        readonly NumericUpDown cboreDiaNum = Ui.Num(0.1M, 500M, 11M, 2, 0.5M, 66);
        readonly NumericUpDown cboreDepNum = Ui.Num(0.1M, 500M, 6.5M, 2, 0.5M, 66);
        readonly NumericUpDown csinkDiaNum = Ui.Num(0.1M, 500M, 12M, 2, 0.5M, 66);
        readonly NumericUpDown csinkAngNum = Ui.Num(10M, 179M, 90M, 0, 1M, 50);
        readonly NumericUpDown chamferSetNum = Ui.Num(0.1M, 20M, 0.5M, 2, 0.1M, 60);
        readonly NumericUpDown chamferAngNum = Ui.Num(10M, 89M, 45M, 0, 1M, 50);
        readonly CheckBox throughCheck = new CheckBox { Text = "贯通" };
        readonly CheckBox depthCheck = new CheckBox { Text = "盲孔" };
        readonly CheckBox chamferCheck = new CheckBox { Text = "孔口倒角" };
        readonly Panel cboreRow = new Panel();
        readonly Panel csinkRow = new Panel();
        readonly Panel chamferRow = new Panel();
        readonly Label refCount = new Label();
        readonly Label faceCount = new Label();
        Card paramCard;            // ShowKindFields 要按行数改它的高度
        VerticalStack rootStack;   // 改完高度要重排一次

        public AutoHoleForm(F.Application application, A.AssemblyDocument document){
            app = application; assembly = document;
            Ui.Shell(this, "自动打孔", 900, 880, 780, 800);

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
        // ---------------- 布局 ----------------
        // 左列 = 选东西（点孔 / 点面），右列 = 定规格；孔形状参考、预览和"开打"通栏放在底部。
        // 这样 2D 剖面和 3D 参考各自都有半屏宽，尺寸标注才排得开。
        void BuildLayout(){
            // 左列按比例而不是固定宽度：窗口缩到最小时，参数那一列还要放得下
            var columns = new SplitColumns(0.365f);
            var left = new VerticalStack();
            var right = new VerticalStack();

            // ---- 左列：① 点孔 / ② 点面 ----
            var step1 = Ui.Step(1, "点孔", "点孔口圆边 / 孔的内壁圆柱面 / 一个带孔的面（自动认孔）");
            var refsCard = Ui.Card("参考孔", 0, "点圆边/内壁/带孔的面都会加到这里，同孔径的孔归成一类");
            var step2 = Ui.Step(2, "点面", "点要打孔的那个平面，可连点多个");
            var facesCard = Ui.Card("打孔面", 128, "孔心按参考孔的轴线投影到这些面上");

            // 点平面的两种含义就在这一个勾上（默认"自动认孔"）：
            // 点在带孔的面（比如型材上已经打好一排 M6 的那张面）上时，自动把孔全加进参考孔列表；
            // 勾掉则维持老行为 —— 点平面就是加打孔面。
            var scanRow = new Panel { Height = 36, BackColor = Ui.Bg };
            scanFaceCheck.Text = "点平面自动认面上的孔（按孔径归类）\r\n勾掉＝点平面只当打孔面";
            scanFaceCheck.SetBounds(2, 0, 400, 34);
            scanFaceCheck.BackColor = Ui.Bg; scanFaceCheck.ForeColor = Ui.Text;
            scanRow.Controls.Add(scanFaceCheck);

            // 参考孔列表
            refList.Dock = DockStyle.Fill; refList.IntegralHeight = false; refList.BorderStyle = BorderStyle.FixedSingle;
            refList.DrawMode = DrawMode.OwnerDrawFixed; refList.ItemHeight = 38; refList.BackColor = Color.White;
            refList.DrawItem += DrawRefItem;
            var refBtns = new Panel { Dock = DockStyle.Right, Width = 96, BackColor = Color.White };
            var delRef = Ui.Secondary("移除选中", 88, 26); delRef.SetBounds(4, 0, 88, 26);
            var clrRef = Ui.Secondary("清空", 88, 26); clrRef.SetBounds(4, 0, 88, 26);
            delRef.Click += (s, e) => RemoveSelected();
            clrRef.Click += (s, e) => { groups.Clear(); scannedPicks.Clear(); lastScanNote = ""; RefreshAll(); SetStatus("已清空参考孔。", Ui.Muted); };
            refCount.SetBounds(4, 2, 88, 18);
            refCount.AutoSize = false; refCount.Font = Ui.F8; refCount.ForeColor = Ui.Muted;
            refCount.TextAlign = ContentAlignment.TopRight;
            // 按钮贴底：列表长起来的时候，按钮就在手边，按钮列上方那块空白也不空了（放计数）
            refBtns.Resize += (s, e) => {
                clrRef.Top = Math.Max(24, refBtns.Height - 28);
                delRef.Top = Math.Max(0, clrRef.Top - 30);
            };
            refBtns.Controls.Add(refCount); refBtns.Controls.Add(delRef); refBtns.Controls.Add(clrRef);
            refsCard.Controls.Add(refList); refsCard.Controls.Add(refBtns);

            // 打孔面列表
            faceList.Dock = DockStyle.Fill; faceList.IntegralHeight = false; faceList.BorderStyle = BorderStyle.FixedSingle;
            faceList.DrawMode = DrawMode.OwnerDrawFixed; faceList.ItemHeight = 30; faceList.BackColor = Color.White;
            faceList.DrawItem += DrawFaceItem;
            var faceBtns = new Panel { Dock = DockStyle.Right, Width = 96, BackColor = Color.White };
            var delFace = Ui.Secondary("移除选中", 88, 26); delFace.SetBounds(4, 0, 88, 26);
            var clrFace = Ui.Secondary("清空", 88, 26); clrFace.SetBounds(4, 0, 88, 26);
            delFace.Click += (s, e) => { RemoveSelectedFace(); };
            clrFace.Click += (s, e) => { targets.Clear(); targetLabels.Clear(); resolved.Clear(); scannedPicks.Clear(); drilledOnce = false; RefreshAll(); SetStatus("已清空打孔面。", Ui.Muted); };
            faceCount.SetBounds(4, 2, 88, 18);
            faceCount.AutoSize = false; faceCount.Font = Ui.F8; faceCount.ForeColor = Ui.Muted;
            faceCount.TextAlign = ContentAlignment.TopRight;
            faceBtns.Resize += (s, e) => {
                clrFace.Top = Math.Max(24, faceBtns.Height - 28);
                delFace.Top = Math.Max(0, clrFace.Top - 30);
            };
            faceBtns.Controls.Add(faceCount); faceBtns.Controls.Add(delFace); faceBtns.Controls.Add(clrFace);
            facesCard.Controls.Add(faceList); facesCard.Controls.Add(faceBtns);

            left.Add(step1); left.Add(refsCard, true); left.Add(step2); left.Add(scanRow); left.Add(facesCard);

            // ---- 右列：③ 定规格 + 规格来源 ----
            var step3 = Ui.Step(3, "定规格", "默认是自动反推的，不动就按它打；要改就改这里");
            paramCard = Ui.Card("孔参数", ParamCardRows4, "改任何一个数字，下面的孔形状立刻跟着变");
            var srcCard = Ui.Card("规格来源", 0, "");
            BuildParamCard(paramCard);
            BuildSourceCard(srcCard);
            right.Add(step3); right.Add(paramCard); right.Add(srcCard, true);

            columns.Add(left); columns.Add(right);

            // ---- 通栏：孔形状参考 ----
            var shapeCard = Ui.Card("孔形状参考", 244, "剖面看尺寸，3D 看打完之后数模长什么样");
            BuildShapeCard(shapeCard);

            // ---- 通栏：预览 ----
            var previewCard = Ui.Card("预览", 88, "打完之前先看清楚要打几个、什么孔");
            var pvBody = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
            previewCard.Controls.Add(pvBody);
            chip.SetBounds(2, 4, 116, 24);
            previewLine.SetBounds(126, 4, 700, 22);
            previewLine.AutoSize = false; previewLine.Font = Ui.F9B; previewLine.ForeColor = Ui.Text;
            previewSub.SetBounds(2, 32, 824, 44);
            previewSub.AutoSize = false; previewSub.ForeColor = Ui.Muted;
            pvBody.Controls.Add(chip); pvBody.Controls.Add(previewLine); pvBody.Controls.Add(previewSub);
            pvBody.Resize += (s, e) => {
                int w = Math.Max(120, pvBody.ClientSize.Width);
                previewLine.Width = Math.Max(60, w - 128);
                previewSub.Width = w - 4;
            };

            // ---- 底部按钮 ----
            var actions = new Panel { Height = 56, BackColor = Ui.Bg };
            run.SetBounds(0, 8, 168, 38);
            var clearAll = Ui.Secondary("全部重选", 104, 30); clearAll.SetBounds(180, 12, 104, 30);
            var close = Ui.Secondary("关闭 (Esc)", 104, 30); close.SetBounds(296, 12, 104, 30);
            clearAll.Click += (s, e) => { groups.Clear(); targets.Clear(); targetLabels.Clear(); resolved.Clear(); scannedPicks.Clear(); lastScanNote = ""; drilledOnce = false; RefreshAll(); SetStatus("已全部清空，重新点孔。", Ui.Accent); };
            close.Click += (s, e) => Close();
            run.Click += (s, e) => RunDrill();
            actions.Controls.Add(run); actions.Controls.Add(clearAll); actions.Controls.Add(close);

            status.Height = 26; status.Padding = new Padding(2, 0, 10, 0);
            status.TextAlign = ContentAlignment.MiddleLeft; status.ForeColor = Ui.Accent; status.BackColor = Ui.Bg;
            status.Text = "点孔口圆边、孔内壁，或直接点一个带孔的面（面上的孔会自动认出来）。";

            // 纵向堆叠：添加顺序就是从上到下的顺序。两列那块吃掉剩余高度。
            var stack = rootStack = new VerticalStack { Dock = DockStyle.Fill };
            stack.Add(columns, true);
            stack.Add(shapeCard);
            stack.Add(previewCard);
            stack.Add(actions);
            stack.Add(status);
            Controls.Add(stack);
        }

        // 孔形状参考：左边 2D 剖面（带尺寸标注），右边 3D 半剖轴测。
        // 两个视图都直接吃 HoleShape —— 和 CAD 实际切出来的孔是同一份几何定义。
        void BuildShapeCard(Card card){
            var split = new SplitColumns(0.52f);
            split.Dock = DockStyle.Fill;
            sectionView.Header = "剖面 · 孔的尺寸";
            modelView.Header = "3D 参考 · 打孔后的样子";
            split.Add(sectionView);
            split.Add(modelView);
            card.Controls.Add(split);
        }

        // 规格来源：把"这个规格是怎么来的 / 正在改谁"从参数网格里挪出来单独成卡。
        // 原来这两句挤在倒角那一行的右边，字号还比输入框小一号，读起来很别扭。
        void BuildSourceCard(Card card){
            var body = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
            card.Controls.Add(body);
            srcWho.SetBounds(0, 0, 470, 22);
            srcWho.AutoSize = false; srcWho.Font = Ui.F9B; srcWho.ForeColor = Ui.Text;
            srcNote.SetBounds(0, 24, 470, 48);
            srcNote.AutoSize = false; srcNote.ForeColor = Ui.Accent;
            srcHint.SetBounds(0, 76, 470, 62);
            srcHint.AutoSize = false; srcHint.ForeColor = Ui.Muted; srcHint.Font = Ui.F8;
            body.Controls.Add(srcWho); body.Controls.Add(srcNote); body.Controls.Add(srcHint);
            // 宽度按父容器现算。用 Anchor 会被"设计时尺寸 -> 实际尺寸"的差值撑到几千像素宽，
            // 于是中文长句一行排到底、被卡片边缘硬切（第一版就是这个毛病）。
            EventHandler fit = (s, e) => {
                int w = Math.Max(80, body.ClientSize.Width);
                srcWho.Width = w; srcNote.Width = w; srcHint.Width = w;
            };
            body.Resize += fit;
        }

        // 三行说明：正在改谁 / 这个规格是怎么来的 / 螺纹与孔底的通用提示。
        void RefreshSourceCard(){
            if (srcWho == null) return;
            var g = Selected;
            if (g == null || g.Spec == null) {
                srcWho.Text = groups.Count == 0 ? "还没有参考孔 · 现在改的是草稿规格" : "没有选中参考孔 · 现在改的是草稿规格";
                srcNote.Text = groups.Count == 0
                    ? "先在模型上点孔口的圆形边线，规格会自动反推；也可以直接改上面的参数，下面画的就是打出来这个孔的样子。"
                    : "在上面点一条就能改它的规格；现在改的是草稿，不影响已经选好的孔。";
                srcNote.ForeColor = Ui.Muted;
            } else {
                srcWho.Text = "正在编辑：" + g.Head + "（共 " + g.Count + " 个孔，改动同步到整组）";
                srcNote.Text = g.Spec.Note.Length > 0 ? g.Spec.Note : "规格已按你的选择手动设定。";
                srcNote.ForeColor = Ui.Accent;
            }
            srcHint.Text = ThreadHint;
        }

        // 参数网格。所有行共用同一套列坐标，同类控件等宽、单位紧跟数字 ——
        // "控件不对齐、字体排列奇怪"的根治办法：位置是算出来的，不是一个个手摆的。
        // 三列的起点固定为 0 / 140 / 272（标签）+ 34 / 174 / 306（控件）。
        // 整个网格按 430px 内宽设计：窗口缩到最小尺寸（780）时右列内宽约 445，正好放得下。
        void BuildParamCard(Card card){
            var body = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
            card.Controls.Add(body);
            const int RowH = 30;
            int y = 2;

            // 第 1 行：孔型 / 规格 / 孔径
            body.Controls.Add(Lbl("孔型", 0, y));
            kindBox.SetBounds(34, y, 92, 24); body.Controls.Add(kindBox);
            body.Controls.Add(Lbl("规格", 140, y));
            sizeBox.SetBounds(174, y, 84, 24); body.Controls.Add(sizeBox);
            body.Controls.Add(Lbl("孔径", 272, y));
            diaNum.SetBounds(306, y, 66, 24); body.Controls.Add(diaNum);
            body.Controls.Add(Unit("mm", 376, y));

            // 第 2 行：深度（贯通/盲孔）/ 孔底
            y += RowH;
            body.Controls.Add(Lbl("深度", 0, y));
            throughCheck.SetBounds(34, y + 1, 50, 22); body.Controls.Add(throughCheck);
            depthCheck.SetBounds(86, y + 1, 50, 22); body.Controls.Add(depthCheck);
            depthNum.SetBounds(138, y, 66, 24); body.Controls.Add(depthNum);
            body.Controls.Add(Unit("mm", 208, y));
            body.Controls.Add(Lbl("孔底", 256, y));
            bottomBox.SetBounds(290, y, 66, 24); body.Controls.Add(bottomBox);
            bottomAngNum.SetBounds(360, y, 50, 24); body.Controls.Add(bottomAngNum);
            body.Controls.Add(Unit("°", 414, y));

            // 第 3 行：沉孔 / 锥形沉孔（同一行的位置，按孔型显示其中一个）
            y += RowH;
            cboreRow.SetBounds(0, y, 420, 26); cboreRow.BackColor = Color.White;
            cboreRow.Controls.Add(Lbl("沉孔Φ", 0, 0));
            cboreDiaNum.SetBounds(48, 0, 66, 24); cboreRow.Controls.Add(cboreDiaNum);
            cboreRow.Controls.Add(Unit("mm", 118, 0));
            cboreRow.Controls.Add(Lbl("沉孔深", 150, 0));
            cboreDepNum.SetBounds(200, 0, 66, 24); cboreRow.Controls.Add(cboreDepNum);
            cboreRow.Controls.Add(Unit("mm", 270, 0));
            csinkRow.SetBounds(0, y, 420, 26); csinkRow.BackColor = Color.White;
            csinkRow.Controls.Add(Lbl("锥孔Φ", 0, 0));
            csinkDiaNum.SetBounds(48, 0, 66, 24); csinkRow.Controls.Add(csinkDiaNum);
            csinkRow.Controls.Add(Unit("mm", 118, 0));
            csinkRow.Controls.Add(Lbl("锥角", 150, 0));
            csinkAngNum.SetBounds(200, 0, 50, 24); csinkRow.Controls.Add(csinkAngNum);
            csinkRow.Controls.Add(Unit("°", 254, 0));
            body.Controls.Add(cboreRow); body.Controls.Add(csinkRow);

            // 第 4 行：孔口倒角（单独一个行面板，沉孔/锥沉那行不显示时要把它顶上去）
            chamferRow.SetBounds(0, y, 420, 26); chamferRow.BackColor = Color.White;
            chamferCheck.SetBounds(0, 1, 88, 22); chamferRow.Controls.Add(chamferCheck);
            chamferSetNum.SetBounds(92, 0, 60, 24); chamferRow.Controls.Add(chamferSetNum);
            var times = Ui.Caption("×"); times.SetBounds(154, 3, 16, 20); chamferRow.Controls.Add(times);
            chamferAngNum.SetBounds(172, 0, 50, 24); chamferRow.Controls.Add(chamferAngNum);
            chamferRow.Controls.Add(Unit("°", 226, 0));
            body.Controls.Add(chamferRow);
        }

        // 参数卡片的两种高度：4 行（显示沉孔/锥沉行）和 3 行（把倒角行顶上去）。
        const int ParamCardRows4 = 164, ParamCardRows3 = 134;

        // 表单里的一行字：标签（正文色）和单位（8.25pt 灰）各一个。
        static Label Lbl(string text, int x, int y){
            return new Label { Text = text, AutoSize = true, Location = new Point(x, y + 3), BackColor = Color.Transparent };
        }
        static Label Unit(string text, int x, int y){
            var l = Ui.Muted8(text);
            l.Location = new Point(x, y + 6);
            return l;
        }

        const string ThreadHint = "螺纹孔按螺纹内小径建模，螺纹以装饰螺纹显示（和 CAD 自己的孔命令一致）；"
                                + "盲孔默认平底，需要钻尖就选 V 型底。";

        // 参数控件当前值 -> 一个 HoleSpec。不依赖"有没有选中参考孔"：
        // 预览画的就是面板上这套参数，没选孔也能先把形状调出来看。
        HoleSpec EditorSpec(){
            var sp = new HoleSpec();
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
                         + "。要把这张面也当打孔面，请先勾掉「点平面自动认面上的孔」再点一次。";
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
                    : ("共 " + groups.Count + " 组 " + groups.Sum(x => x.Count) + " 个");
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
                kindBox.SelectedIndex = (int)sp.Kind;
                int si = 0;
                if (sp.ThreadSize != null && sp.ThreadSize.Length > 0) {
                    var r = HoleMatcher.Find(sp.ThreadSize);
                    si = r.HasValue ? Array.IndexOf(HoleMatcher.Table, r.Value) + 1 : 0;
                }
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
            } finally { loading = false; }
            ShowKindFields();
            RefreshSourceCard();
            RefreshShapeViews();
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
            // 沉孔/锥沉那一行不显示时，别在参数卡片中间留一条空带：把倒角行顶上去，卡片也跟着变矮。
            // 这里必须按孔型算，不能读 cboreRow.Visible —— Control.Visible 的 getter 在
            // "父窗口还没显示"时一律返回 false，拿它当布局依据会时灵时不灵（无窗口测试下就翻车）。
            bool seatRow = k == HoleKind.Counterbore || k == HoleKind.Countersink;
            chamferRow.Top = seatRow ? 92 : 62;
            if (paramCard != null) {
                int h = seatRow ? ParamCardRows4 : ParamCardRows3;
                if (paramCard.Height != h) {
                    paramCard.Height = h;
                    if (rootStack != null) rootStack.Relayout();
                }
            }
        }

        // 换孔型 / 换规格都要按标准表重填尺寸。选中了参考孔就改那一组；没选中就改草稿。
        // 两条路必须一样 —— 否则"没点孔的时候选 M6，孔径还停在 6.00"，看着像没生效。
        void ApplyKindChange(){
            var g = Selected;
            var kind = (HoleKind)kindBox.SelectedIndex;
            var cur = (g != null && g.Spec != null) ? g.Spec : EditorSpec();
            var fresh = HoleMatcher.FromRow(RowOf(cur, g != null ? g.DiameterMm : cur.HoleDiameter), kind);
            CopyUserEdits(cur, fresh);
            fresh.Note = "";          // 规格变了，旧备注（"参考孔 Φx 是 Mx 的底孔…"）不再准确
            CommitSpec(g, fresh);
        }

        // 用户手改过的那几项要跟着走：孔型/规格一换就把"贯通改盲孔、加了倒角"全冲掉，是不能接受的。
        static void CopyUserEdits(HoleSpec from, HoleSpec to){
            to.Depth = from.Depth;
            to.Bottom = from.Bottom;
            to.BottomAngle = from.BottomAngle;
            to.Chamfer = from.Chamfer;
            to.ChamferSetback = from.ChamferSetback;
            to.ChamferAngle = from.ChamferAngle;
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
                var fresh = HoleMatcher.FromRow(row, (HoleKind)kindBox.SelectedIndex);
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
                // 没选中参考孔：面板上的值存成草稿。"孔形状参考"照样实时跟着变，
                // 这样工程师可以先把孔型调出来看清楚，再去模型上点孔。
                draft = EditorSpec();
                // ShowKindFields 一定要调：它负责"哪些框能用"。漏了它，
                // 勾上"盲孔"以后深度框还是灰的 —— 用户根本填不了深度。
                ShowKindFields();
                RefreshSourceCard();
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
            RefreshSourceCard();
            RefreshPreview();
            RefreshShapeViews();
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
                previewLine.Text = "① 先在模型上点孔：点圆边、点孔内壁，或直接点一个带孔的面";
                previewSub.Text = "点一个充满孔的面就会把面上的孔全认出来，同一个孔径的自动归成一类；"
                                + "沉孔/锥沉/倒角孔口会自动取孔口下面那个孔径来配做，不会拿孔口直径去配。";
            } else if (targets.Count == 0) {
                chip.Text = "已选 " + refCount + " 个孔"; chip.ChipColor = Ui.Accent;
                previewLine.Text = "② 再点要打孔的面";
                previewSub.Text = "共 " + groups.Count + " 组参考孔：" + string.Join("；", groups.Select(g => g.Head + " → " + g.Detail).ToArray())
                                  + (lastScanNote.Length > 0 ? "　—　" + lastScanNote : "");
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
                // 沉孔/锥沉/倒角孔口：说明"按的是孔口下面那个孔径"，别让用户以为用错了直径
                foreach (var g in groups) {
                    string mi = g.MouthInfo;
                    if (mi.Length > 0) s += "　·　" + g.Head + "：" + mi + "。";
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
                    // 被"面扫描"点过的那张面也高亮（它不是打孔面，但用户得看见自己点的是哪张）
                    foreach (var s in scannedPicks) { try { highlights.AddItem(s); } catch { } }
                    highlights.Draw();
                } catch (Exception e) { Log.Write("AutoHoleHighlight", e); }
            }
            chip.Invalidate();
        }

        // 打孔面的解析结果缓存：实时预览会被每次数值改动触发，别每次都重读一遍面片的边。
        // 这个判断已经不用了（错诊断的遗留），保留方法体以便将来排查，但不再拦截打孔。
        bool TargetIsActiveDocumentUnused(){
            try {
                var activePart = app.ActiveDocument as P.PartDocument;
                if (activePart == null) return false;
                string activeName = null; try { activeName = activePart.FullName; } catch { }
                if (string.IsNullOrEmpty(activeName)) return false;
                for (int i = 0; i < targets.Count; i++){
                    TargetFace t = i < resolved.Count ? resolved[i] : null;
                    if (t == null){ try { t = AutoHoleReader.ReadTarget(targets[i]); } catch { continue; } }
                    if (t == null || t.Part == null) continue;
                    string name = null; try { name = t.Part.FullName; } catch { }
                    if (!string.IsNullOrEmpty(name) && string.Equals(name, activeName, StringComparison.OrdinalIgnoreCase)) return true;
                }
            } catch (Exception e) { Log.Write("AutoHoleContext", e); }
            return false;
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

        // ---- 测试 / 真机实测脚本用的缝（给自动化读界面状态、免弹框）----
        // 读到的就是窗口上看到的那一份数据；打孔走的是和点按钮同一条路。
        public bool SuppressDialogs;       // 真机脚本里置真：结果框改为写进 LastResult
        public string LastResult = "";
        public void DrillForTest(){ RunDrill(); }
        public string UiSummary(){
            var sb = new System.Text.StringBuilder();
            sb.Append("参考孔 ").Append(groups.Count).Append(" 组 [");
            foreach (var g in groups) sb.Append("(").Append(g.Head).Append(" → ").Append(g.Detail).Append(") ");
            sb.Append("] 打孔面 ").Append(targets.Count).Append(" 个 [");
            foreach (var s in targetLabels) sb.Append(s).Append(" | ");
            sb.Append("] 可打孔=").Append(run.Enabled).Append(" 徽标=").Append(chip.Text);
            sb.Append(" 预览=").Append(previewLine.Text).Append(" / ").Append(previewSub.Text);
            sb.Append(" 状态=").Append(status.Text);
            return sb.ToString();
        }

        // ---------------- 打孔 ----------------
        void RunDrill(){
            if (busy || closing) return;
            try {
                if (groups.Count == 0) { SetStatus("请先点一个已有的孔（点孔口圆边、孔内壁，或点一个带孔的面自动认孔）。", Ui.Warn); return; }
                if (targets.Count == 0) { SetStatus("请先点要打孔的面（面上有孔时会被当成参考孔；要打孔的面请点一个没有孔的面，或勾掉「点平面自动认面上的孔」）。", Ui.Warn); return; }
                // 打完孔以后，那批面的面片对象已经失效。再点一次会对着死对象打：
                // 轻则每个孔都失败，重则 CAD 报一堆"对象已断开" —— 用户会以为软件坏了。
                if (drilledOnce) {
                    SetStatus("这批打孔面已经用过了（打完孔后原来的面对象会失效）。请重新点一个面，或点「全部重选」。", Ui.Warn);
                    return;
                }
                // 原位编辑（双击零件进入）**不是**错误：CAD 只在"正在编辑的那个零件文档"里允许写模型，
                // 所以这里放开它。只读装配仍然拒绝。
                if (assembly.ReadOnly) throw new InvalidOperationException("装配是只读打开的，无法打孔。请用可写方式打开装配后再试。");
                // 关键一步：确认要打孔的那个零件就是当前正在编辑的文档。
                // 装配上下文里直接写实例零件会被 CAD 拒绝（实测 CO_E_OBJNOTREG / RPC_E_DISCONNECTED，
                // 重试、丢缓存重建、MakeWritable、Activate 全都救不回来；只有"顶层零件"或"原位编辑中的零件"
                // 才是可写的）。与其白试 6 次再报一句失败，不如现在就告诉用户怎么做。
                // （这里以前有一条"必须先双击零件进入原位编辑"的守卫。实测那是错诊断：
                //   装配上下文本来就能写模型，真正会失败的是"位置已经有孔"，已由预检处理。）

                busy = true; run.Enabled = false; run.Text = "打孔中…"; UseWaitCursor = true;
                StopCommand();

                var refs = new List<ReferenceHole>(); var specs = new List<HoleSpec>();
                foreach (var g in groups) foreach (var h in g.Holes) { refs.Add(h); specs.Add(g.Spec); }
                // 沉孔/锥沉/倒角孔口：配做用的是孔口**下面**那个孔径，结果框里要说一句，
                // 否则用户看到"参考孔 Φ11、打出来的是 Φ6.6 配 M6"会以为配错了。
                int stepped = 0;
                foreach (var h in refs) if (h.MouthDiameterMm - h.DiameterMm > HoleScan.StepToleranceMm) stepped++;

                int made = 0, skipped = 0, failed = 0, already = 0;
                var problems = new List<string>(); var methods = new List<string>(); var audits = new List<string>();
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
                        requests.Add(new AutoHoleWriter.HoleRequest { Spec = specs[i], Centre = centre,
                            Source = partName + " Φ" + Num(hole.DiameterMm) });
                    }
                    if (requests.Count == 0) continue;
                    // 打孔在主进程内完成。实测（2026-09-27）：进程内、跨进程都能写模型；
                    // 真正会失败的是"往已经有孔的位置再打"——那由 Friendly() 和预检给出人话提示。
                    resolved0.Add(t);   // 打完要作废这些零件的圆缓存（见下）
                    var res = AutoHoleWriter.DrillRequests(t, requests);
                    made += res.Created; failed += res.Failures.Count; already += res.AlreadyOk;
                    foreach (var note in res.Notes) AddProblem(problems, note);
                    if (res.Method.Length > 0 && !methods.Contains(res.Method)) methods.Add(res.Method);
                    if (res.Audit.Length > 0 && !audits.Contains(res.Audit)) audits.Add(res.Audit);
                    foreach (var pf in res.Failures) AddProblem(problems, pf);
                }

                string msg = "共打孔 " + made + " 个";
                if (already > 0) msg += "，" + already + " 个位置本来就有孔（见下方说明）";
                if (skipped > 0) msg += "，跳过 " + skipped + " 个（投影不落在所选面上）";
                if (failed > 0) msg += "，失败 " + failed + " 个";
                if (stepped > 0) msg += "\r\n其中 " + stepped + " 个参考孔是沉孔/锥沉/倒角孔口，按孔口下面的孔径配做";
                msg += "\r\n打孔面 " + targets.Count + " 个";
                if (methods.Count > 0) msg += "\r\n生成方式：" + string.Join("／", methods.ToArray());
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
                try { StartPicking(); } catch (Exception ex) { SetStatus(ex.Message, Ui.Warn); }
                RefreshPreview();
            } catch (Exception e) {
                Log.Write("AutoHoleDrill", e);
                busy = false; run.Text = "开始打孔"; UseWaitCursor = false;
                try { if (command == null) StartPicking(); } catch { }
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
