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
    public sealed class AutoHoleForm : Form, IBusyTool, F.ISEMouseEvents, F.ISECommandEvents {
        // 处理中不让关（Esc、关闭按钮、标题栏 ×、CAD 里按 Esc 都会走到这里）：见 IBusyTool
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

        // 一组同直径的参考孔，共用一份可编辑的孔规格。
        sealed class RefGroup {
            public double DiameterMm;
            public readonly List<ReferenceHole> Holes = new List<ReferenceHole>();
            // 只用于高亮（参考孔 → 点它时的选择引用）；打完孔会失效，故与打孔数据分开存
            public readonly Dictionary<ReferenceHole, object> Selections = new Dictionary<ReferenceHole, object>();
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
        // 参考腰孔（0.8.5）：点腰孔一头的圆弧认出整条腰孔，目标件上配同样的腰孔（通切）。和圆孔分开存：腰孔没有孔型规格可改
        readonly List<SlotReference> slots = new List<SlotReference>();
        readonly Dictionary<SlotReference, object> slotPicks = new Dictionary<SlotReference, object>();
        readonly List<object> targets = new List<object>();
        readonly List<string> targetLabels = new List<string>();
        bool busy, closing, loading;
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
        readonly FlatButton run = (FlatButton)Ui.Primary("开始打孔", 240, 44);
        // 保持关联：把"这个孔照哪个孔配的"写进孔特征，参考零件挪了以后能一键同步回去
        readonly CheckBox linkCheck = new CheckBox { Checked = true, Text = "保持与参考孔关联", AutoSize = true };

        // 打孔结果：窗口里的列表（原来是一个长到一屏放不下的弹框）。点一条就把镜头拉到那个孔。
        readonly ResultList results = new ResultList();
        Card resultCard; Control previewCard;
        // 「撤销本次打孔」：上一批打孔改了模型的哪些地方（见 HoleUndo）
        HoleUndo lastUndo;
        readonly Button undoButton = Ui.Secondary("撤销本次打孔", 112, 30);
        // 打完发现孔型不对：把刚打的这批孔带进「改孔型」窗口，换孔型不用删了重打
        readonly Button editButton = Ui.Secondary("改孔型…", 84, 30);
        List<KeyValuePair<SolidEdgePart.PartDocument, string>> lastCreated;

        // 关联孔位条：窗口一打开就查一遍，有错位的孔就亮出「同步孔位」
        readonly Panel linkBar = new Panel();
        readonly Label linkLabel = new Label();
        readonly Button syncButton = Ui.Secondary("同步孔位", 112, 30);
        readonly Button rescanButton = Ui.Secondary("检查", 60, 30);
        SyncReport lastLinks;

        // "正在编辑谁 / 规格怎么来的"两行字（原来是单独一张大半空着的卡片）
        readonly Label editWho = new Label();
        readonly Label editNote = new Label();
        readonly ToolTip tips = new ToolTip { AutoPopDelay = 15000, InitialDelay = 400 };
        Card refsCard;

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

        public AutoHoleForm(F.Application application, A.AssemblyDocument document){
            app = application; assembly = document;
            Ui.Shell(this, "自动打孔", 1160, 790, 1000, 680);

            // 规格下拉框：第 0 项是「自定义」，后面跟表里的全部规格。
            // 漏了这段会怎样：参考孔是 Φ13.5（M12 过孔）时要选中第 7 项，而框里只有 1 项 →
            // InvalidArgument "7" 对于 SelectedIndex 无效 —— 就是用户截图里那个报错。
            sizeBox.Items.Clear();
            sizeBox.Items.Add("自定义");
            foreach (var row in HoleMatcher.Table) sizeBox.Items.Add(row.Size);
            sizeBox.SelectedIndex = 0;

            HoleRuleBook.LoadFromDisk();   // 用户改过配做规则就按他的来（文件读不到就是默认规则）
            LoadChoices();
            BuildLayout();
            WireEvents();
            RefreshAll();
            ShowLinkReport(null, assembly == null ? "（没有打开装配，不检查关联孔）" : NotScanned);
            // 「自动识别」被用户关掉了（记住的选择）：开窗时说一句现在点平面是什么意思、Ctrl 能临时反过来
            if (!scanFaceCheck.Checked) SetStatus("「点平面时自动识别面上的孔」关着：点平面 = 打孔面；按住 Ctrl 点带孔的面 = 把面上的孔认成参考孔。参考孔也可以点孔口圆边或孔内壁。", Ui.Muted);
            // 上次关窗口前打的那批还没撤销：接着能撤
            HoleUndo carried; List<KeyValuePair<SolidEdgePart.PartDocument, string>> carriedCreated;
            if (Carried.Take(assembly, out carried, out carriedCreated)) {
                lastUndo = carried; lastCreated = carriedCreated;
                undoButton.Enabled = true; editButton.Enabled = lastCreated != null && lastCreated.Count > 0;
                SetStatus("上次关窗口前打的 " + carried.HoleCount + " 个孔还能撤销（「撤销本次打孔」）。点孔口圆边、孔内壁，或点一个带孔的面开始。", Ui.Muted);
            }

            FormClosed += (s, e) => Cleanup();
            // 关联孔**不再自动查**（0.7.8）：查一遍要取装配里每个实例的 OccurrenceDocument，
            // 天工会把轻量化/未激活的零件逐个真正加载进来 —— 用户看到的就是「点了命令卡很久、左下角在扫文件」。
            // 现在只在用户点「检查」或「同步孔位」时才查。
            // StartPicking 由宿主 ToolContext.Show 的启动回调调用，这里不能重复调，
            // 否则第二次 command.Start() 会触发 Terminate() 把窗口关掉。
        }

        // 「保持与参考孔关联」「点平面时自动识别面上的孔」记住上次的选择（0.8.4）：原来每次打开窗口都回到默认，
        // 不要关联 / 不要自动认孔的人每次都得先去勾掉。文件 autohole.tsv：每行「名字<TAB>1/0」
        const string ChoicesFile = "autohole.tsv";
        void LoadChoices(){
#if TG_DEV_BUILD
            // 真机测试脚本（放行授权闸的那些）用默认选择：用户自己关掉的「自动识别」不能让测试的第一步就走岔（0.8.5 整批 E2E 因此误报）
            if (TianGongCadSuite.Licensing.LicenseTestHooks.GateOverride >= 0) return;
#endif
            try {
                foreach (var r in SettingsFile.Read(SettingsFile.PathOf(ChoicesFile))) {
                    if (r.Length < 2) continue;
                    if (r[0] == "keepLink") linkCheck.Checked = r[1] == "1";
                    else if (r[0] == "scanFace") scanFaceCheck.Checked = r[1] == "1";
                }
            } catch (Exception e) { Log.Write("AutoHoleChoicesLoad", e.Message); }
        }
        void SaveChoices(){
            try {
                SettingsFile.Write(SettingsFile.PathOf(ChoicesFile), new List<string[]>{
                    new[]{ "keepLink", linkCheck.Checked ? "1" : "0" },
                    new[]{ "scanFace", scanFaceCheck.Checked ? "1" : "0" } },
                    "天工工具箱 · 自动打孔窗口记住的选择（每行：名字<TAB>1=勾上 0=不勾）");
            } catch (Exception e) { Log.Write("AutoHoleChoicesSave", e.Message); }
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
            if (keyData == Keys.Escape) { if (ConfirmQuickClose("Esc")) Close(); return true; }
            if (keyData == (Keys.Control | Keys.Enter)) { RunDrill(); return true; }
            if (keyData == Keys.Delete && (ActiveControl == refList || ActiveControl == faceList)) {
                if (ActiveControl == refList) RemoveSelected(); else RemoveSelectedFace();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ---------------- 布局 ----------------
        // 0.9.0「D · 侧栏向导」：
        // ┌ 侧栏 ───────────┬ 第 n 步，共 4 步 ─────────────────── [标题行按钮] ┐
        // │ ① 参考孔  2 组  │ 当前步骤（参考孔列表 / 打孔面列表 / 孔规格 + 孔形状 / 预览 + 结果） │
        // │ ② 打孔面  2 个面│                                                                     │
        // │ ③ 孔规格        │                                                                     │
        // │ ④ 预览并打孔    │                                                                     │
        // │ [关联孔位小卡片]├─────────────────────────────────────────────────────────────────────┤
        // │                 │ [上一步] [更多▾]  状态字 ……                 [开始打孔 6 个孔]      │
        // 交互还是"没有模式切换"：随时点圆边 = 加参考孔、点平面 = 加打孔面；点了什么就自动翻到那一步、新项闪一下。
        // 主按钮只要能打就是「开始打孔 N 个孔」（哪一步都能直接打），还不能打才是「下一步：…」。
        const int PageRefs = 0, PageFaces = 1, PageSpec = 2, PagePreview = 3;
        readonly WizardShell shell = new WizardShell();
        readonly FlatButton nextButton = (FlatButton)Ui.Primary("下一步", 240, 44);
        readonly FlatButton backButton = (FlatButton)Ui.Secondary("上一步", 84, 38);
        readonly FlatButton moreButton = (FlatButton)Ui.Secondary("更多 ▾", 84, 38);
        readonly ChipTabs groupTabs = new ChipTabs { EmptyText = "还没有参考孔：现在改的是草稿规格，点了孔以后按孔径分组反推。" };
        readonly StatTile statHoles = new StatTile("要打的孔"), statRefs = new StatTile("参考孔"), statFaces = new StatTile("打孔面");
        Flash refFlash, faceFlash;
        bool drilledOnce;   // 打过一批：第 4 步打勾

        void BuildLayout(){
            Controls.Add(shell);
            var pRefs = shell.AddPage("参考孔", "参考孔", "");
            var pFaces = shell.AddPage("打孔面", "打孔面", "");
            var pSpec = shell.AddPage("孔规格", "孔规格", "");
            var pPrev = shell.AddPage("预览并打孔", "预览并打孔", "");

            // ---- ① 参考孔 ----
            var rulesButton = Ui.Ghost("配做规则…");
            rulesButton.Click += (s, e) => EditRules();
            tips.SetToolTip(rulesButton, "参考孔是螺纹底孔/过孔时，目标件各配什么孔（沉头螺钉、螺钉头露在外面、锥形沉头、螺栓+螺母，或按规格自己定）。");
            var delRef = Ui.Small("移除"); var clrRef = Ui.Small("清空");
            ((FlatButton)delRef).SetFore(Ui.Text2); ((FlatButton)clrRef).SetFore(Ui.Text2);
            delRef.Click += (s, e) => RemoveSelected();
            clrRef.Click += (s, e) => { if (busy) return; groups.Clear(); slots.Clear(); slotPicks.Clear(); scannedPicks.Clear(); lastScanNote = ""; RefreshAll(); SetStatus("已清空参考孔。", Ui.Muted); };
            shell.AddHeader(pRefs, rulesButton); shell.AddHeader(pRefs, delRef); shell.AddHeader(pRefs, clrRef);
            refFlash = new Flash(refList);
            refList.IntegralHeight = false; refList.BorderStyle = BorderStyle.None;
            refList.DrawMode = DrawMode.OwnerDrawFixed; refList.ItemHeight = 62; refList.BackColor = Color.White;
            refList.DrawItem += DrawRefItem;
            scanFaceCheck.BackColor = Ui.Bg; scanFaceCheck.ForeColor = Ui.Text;
            refCount.AutoSize = false; refCount.Font = Ui.F8; refCount.ForeColor = Ui.Muted; refCount.TextAlign = ContentAlignment.MiddleRight;
            tips.SetToolTip(scanFaceCheck, "勾上：点一个带孔的平面，就把面上的孔全部加成参考孔（同孔径自动归一组）；面上没有孔才当打孔面（已经选了参考孔时，参考孔投得到的空位上没有孔的面也当打孔面）。\r\n按住 Ctrl 点：一律当打孔面。\r\n勾掉：点平面一律当打孔面；这时按住 Ctrl 点带孔的面 = 把面上的孔认成参考孔。");
            ListPage(pRefs.Body, "在模型里点孔口圆边、孔内壁，或直接点一个带孔的面。同孔径的孔自动归成一组，规格按配做规则反推。", refList, refEmpty, scanFaceCheck, refCount);

            // ---- ② 打孔面 ----
            var delFace = Ui.Small("移除"); var clrFace = Ui.Small("清空");
            ((FlatButton)delFace).SetFore(Ui.Text2); ((FlatButton)clrFace).SetFore(Ui.Text2);
            delFace.Click += (s, e) => RemoveSelectedFace();
            clrFace.Click += (s, e) => { if (busy) return; targets.Clear(); targetLabels.Clear(); resolved.Clear(); RefreshAll(); SetStatus("已清空打孔面。", Ui.Muted); };
            shell.AddHeader(pFaces, delFace); shell.AddHeader(pFaces, clrFace);
            faceFlash = new Flash(faceList);
            faceList.IntegralHeight = false; faceList.BorderStyle = BorderStyle.None;
            faceList.DrawMode = DrawMode.OwnerDrawFixed; faceList.ItemHeight = 50; faceList.BackColor = Color.White;
            faceList.DrawItem += DrawFaceItem;
            faceCount.AutoSize = false; faceCount.Font = Ui.F8; faceCount.ForeColor = Ui.Muted; faceCount.TextAlign = ContentAlignment.MiddleRight;
            ListPage(pFaces.Body, "点要配孔的零件平面。孔心沿参考孔轴线投到这些面上，落不到面上的会自动跳过。", faceList, faceEmpty, null, faceCount);

            // ---- ③ 孔规格：组标签 → 正在编辑谁 → 参数网格 | 孔形状 ----
            BuildSpecCard(pSpec.Body);

            // ---- ④ 预览并打孔 ----
            var prev = new VerticalStack { Dock = DockStyle.Fill, Padding = new Padding(0), Gap = 12 };
            pPrev.Body.Controls.Add(prev);
            var stats = new Panel { Height = statHoles.Height, BackColor = Ui.Bg };
            stats.Controls.Add(statHoles); stats.Controls.Add(statRefs); stats.Controls.Add(statFaces);
            stats.Resize += (s, e) => {
                int w = (stats.Width - 24) / 3;
                statHoles.SetBounds(0, 0, w, stats.Height); statRefs.SetBounds(w + 12, 0, w, stats.Height); statFaces.SetBounds(2 * (w + 12), 0, stats.Width - 2 * (w + 12), stats.Height);
            };
            var pvBody = new Panel { Height = 28, BackColor = Ui.Bg };
            chip.SetBounds(0, 1, 120, 26);
            previewLine.SetBounds(130, 0, 600, 28);
            previewLine.AutoSize = false; previewLine.Font = Ui.F9B; previewLine.ForeColor = Ui.Text; previewLine.AutoEllipsis = true;
            previewLine.TextAlign = ContentAlignment.MiddleLeft;
            pvBody.Controls.Add(chip); pvBody.Controls.Add(previewLine);
            pvBody.Resize += (s, e) => previewLine.Width = Math.Max(60, pvBody.ClientSize.Width - 130);
            previewSub.AutoSize = false; previewSub.ForeColor = Ui.Text2; previewSub.AutoEllipsis = true; previewSub.Height = 64;
            previewSub.BackColor = Ui.Bg;
            linkCheck.BackColor = Ui.Bg; linkCheck.ForeColor = Ui.Text; linkCheck.Height = 26;
            linkCheck.Text = "保持与参考孔关联";
            tips.SetToolTip(linkCheck, "勾上：用天工原生关联把孔挂在参考孔上（特征树里多一个带锁链的「孔位链接-参考零件」，\r\n" +
                                       "和这张面打的孔编在同一个「关联孔 …」组里 —— 删孔时删这个组，链接一起删掉）。\r\n" +
                                       "参考零件挪动、参考孔改了位置，天工自己把孔带过去（没跟上时点「同步孔位」= 更新所有链接）。\r\n" +
                                       "打孔时插件会自动进出原位编辑，界面会闪一下，这是正常的。\r\n" +
                                       "挂不上天工关联的孔（零件的链接属于别的装配、斜孔、认不准实例……）改用插件自己的关联记录，\r\n" +
                                       "结果里会逐条说明原因；参考零件挪动后点「同步孔位」把它们挪回去。");
            var previewArea = new VerticalStack { Padding = new Padding(0), Gap = 10, AutoScroll = false };
            previewArea.Add(stats); previewArea.Add(pvBody); previewArea.Add(previewSub); previewArea.Add(linkCheck);
            previewArea.Height = stats.Height + 28 + 64 + 26 + 30;
            previewCard = previewArea;

            // 打孔结果：打完出现在预览下面，点一条跳到那个孔
            resultCard = Ui.Card("本次结果", 0, "点一条跳到那个孔");
            var copyResult = Ui.Small("复制"); var hideResult = Ui.Small("收起");
            copyResult.Click += (s, e) => { try { Clipboard.SetText(results.AsText()); SetStatus("结果已复制。", Ui.Ok); } catch (Exception ex) { SetStatus("复制失败：" + ex.Message, Ui.Warn); } };
            hideResult.Click += (s, e) => ShowResults(false);
            resultCard.AddHeader(copyResult); resultCard.AddHeader(hideResult);
            results.Dock = DockStyle.Fill;
            results.Locate += it => {
                if (CadView.ZoomToHole(app, it.Where, it.DiameterMm)) SetStatus("已跳到：" + it.Text, Ui.Accent);
                else SetStatus("没能移动视图（请确认装配窗口是当前窗口）。", Ui.Warn);
            };
            resultCard.Controls.Add(results);
            resultCard.Visible = false;
            prev.Add(previewArea); prev.Add(resultCard, true);

            // ---- 侧栏底部：关联孔位 ----
            var linkCard = new RailCard("关联孔位", 132);
            linkLabel.AutoSize = false; linkLabel.TextAlign = ContentAlignment.TopLeft; linkLabel.AutoEllipsis = true;
            linkLabel.ForeColor = Ui.Muted; linkLabel.BackColor = Color.White; linkLabel.Font = Ui.F8;
            syncButton.Click += (s, e) => SyncLinks();
            rescanButton.Click += (s, e) => ScanLinks(true);
            tips.SetToolTip(syncButton, "天工关联的孔：让天工更新所有链接，孔自己跟上参考孔（顺带查出断了的孔位链接）。\r\n" +
                                        "插件关联的孔（挂不上天工关联时改用的）：按记下的参考孔重新求孔心，把错位的挪回同轴（孔型规格不变）。");
            linkCard.Controls.Add(linkLabel); linkCard.Controls.Add(syncButton); linkCard.Controls.Add(rescanButton);
            linkCard.Resize += (s, e) => {
                int w = linkCard.Width - 24;
                linkLabel.SetBounds(12, 32, w, 54);
                syncButton.SetBounds(12, linkCard.Height - 40, Math.Min(112, w - 70), 30);
                rescanButton.SetBounds(syncButton.Right + 6, linkCard.Height - 40, Math.Max(40, w - syncButton.Width - 6), 30);
            };
            shell.RailBottom = linkCard;

            // ---- 底部操作栏 ----
            var more = new ContextMenuStrip { ShowImageMargin = false, Font = Ui.F9 };
            var miEdit = more.Items.Add("改孔型…（刚打的这批孔整组换孔型）");
            var miUndo = more.Items.Add("撤销本次打孔");
            more.Items.Add(new ToolStripSeparator());
            var miClear = more.Items.Add("全部重选（清空参考孔和打孔面）");
            miEdit.Click += (s, e) => OpenEdit();
            miUndo.Click += (s, e) => UndoLastBatch();
            miClear.Click += (s, e) => ClearAll();
            more.Opening += (s, e) => { miEdit.Enabled = editButton.Enabled && !busy; miUndo.Enabled = undoButton.Enabled && !busy; miClear.Enabled = !busy; };
            moreButton.Click += (s, e) => more.Show(moreButton, new Point(0, -more.PreferredSize.Height - 4));
            backButton.Click += (s, e) => shell.Go(shell.Current - 1);
            nextButton.Click += (s, e) => shell.Go(shell.Current + 1);
            run.Click += (s, e) => RunDrill();
            tips.SetToolTip(run, "Ctrl+Enter");
            undoButton.Enabled = false;
            undoButton.Click += (s, e) => UndoLastBatch();
            editButton.Enabled = false;
            editButton.Click += (s, e) => OpenEdit();
            shell.Footer.AddLeft(backButton); shell.Footer.AddLeft(moreButton);
            shell.Footer.AddRight(run); shell.Footer.AddRight(nextButton);
            shell.Footer.Status = status;
            status.ForeColor = Ui.Accent;
            status.Text = "点孔口圆边、孔内壁，或直接点一个带孔的面（面上的孔会自动认出来）。";
            status.TextChanged += (s, e) => tips.SetToolTip(status, status.Text);
            shell.PageChanged += (s, e) => UpdatePrimary();
        }

        // 列表页：一行说明 → 列表（每一项自己画成圆角小卡片）→ 底行（左：勾选，右：计数）
        readonly EmptyHint refEmpty = new EmptyHint("正在等你在模型里点孔：孔口圆边、孔内壁，或一个带孔的面…");
        readonly EmptyHint faceEmpty = new EmptyHint("正在等你在模型里点要打孔的平面…");
        void ListPage(Panel body, string hint, ListBox list, EmptyHint empty, Control footLeft, Label footRight){
            var stack = new VerticalStack { Dock = DockStyle.Fill, Padding = new Padding(0), Gap = 8, AutoScroll = false };
            body.Controls.Add(stack);
            var h = new Label { Text = hint, AutoSize = false, Height = 40, ForeColor = Ui.Text2, BackColor = Ui.Bg, Font = Ui.F9 };
            var frame = new Panel { BackColor = Color.White, Padding = new Padding(0) };
            list.Dock = DockStyle.Fill; list.Margin = new Padding(0);
            frame.Controls.Add(list);
            empty.Dock = DockStyle.Top; frame.Controls.Add(empty); empty.BringToFront();
            var foot = new Panel { Height = 28, BackColor = Ui.Bg };
            if (footLeft != null) { footLeft.Location = new Point(0, 4); foot.Controls.Add(footLeft); }
            foot.Controls.Add(footRight);
            foot.Resize += (s, e) => footRight.SetBounds(Math.Max(0, foot.Width - 200), 2, 200, 24);
            stack.Add(h); stack.Add(frame, true); stack.Add(foot);
        }

        // 孔规格页：组标签 + 两行"正在编辑谁 / 规格怎么来的"，下面左边参数网格、右边孔形状（剖面在上、3D 在下）。
        // 网格每一行都是"标签 + 一串控件"，位置由 FieldGrid 算，不再手摆坐标；换孔型时整行显示/隐藏，下面的行自动顶上去。
        void BuildSpecCard(Panel page){
            var stack = new VerticalStack { Dock = DockStyle.Fill, Padding = new Padding(0), Gap = 8 };
            page.Controls.Add(stack);
            groupTabs.SelectedChanged += (s, e) => { if (groupTabs.Selected >= 0 && groupTabs.Selected < refList.Items.Count) refList.SelectedIndex = groupTabs.Selected; };
            editWho.AutoSize = false; editWho.Font = Ui.F9B; editWho.ForeColor = Ui.Text; editWho.AutoEllipsis = true; editWho.Height = 22; editWho.BackColor = Ui.Bg;
            editNote.AutoSize = false; editNote.Font = Ui.F8; editNote.ForeColor = Ui.Muted; editNote.AutoEllipsis = true; editNote.Height = 20; editNote.BackColor = Ui.Bg;

            kindSeg.Width = Math.Max(340, kindSeg.PreferredWidth);
            grid.BackColor = Ui.Bg;
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

            var cols = new SplitColumns(0.62f) { Gap = 20 };
            var left = new Panel { BackColor = Ui.Bg };
            left.Controls.Add(grid);
            left.Resize += (s, e) => grid.SetBounds(0, 0, left.Width, grid.PreferredHeight);
            var shapeCard = Ui.Card("孔形状", 0, "剖面 · 打完后的样子");
            shapeCard.Fill = true;
            shapeCard.Padding = new Padding(10, 36, 10, 10);
            sectionView.Header = ""; modelView.Header = "";
            var views = new VerticalStack { Dock = DockStyle.Fill, Padding = new Padding(0), Gap = 8, BackColor = Ui.Rail, AutoScroll = false };
            views.Add(sectionView, true); views.Add(modelView, true);
            shapeCard.Controls.Add(views);
            cols.Add(left); cols.Add(shapeCard);
            stack.Add(groupTabs); stack.Add(editWho); stack.Add(editNote); stack.Add(cols, true);
        }

        // 参考孔组变了 → 组标签跟着变
        void RefreshGroupTabs(){
            groupTabs.SetItems(groups.Select(g => g.Head.Replace("　", " ")).ToList(), groups.Select(g => g.Target).ToList(), refList.SelectedIndex);
        }

        static string ShortName(HoleSpec sp){ try { return sp == null ? "" : HoleNaming.Short(sp); } catch { return ""; } }

        // 主按钮：能打 = 「开始打孔 N 个孔」（哪一步都行）；还不能打 = 「下一步：…」（最后一步就是灰的开始打孔）
        int lastTotal;
        void UpdatePrimary(){
            int cur = shell.Current;
            backButton.Enabled = cur > 0 && !busy;
            bool canRun = run.Enabled;
            bool showRun = busy || canRun || cur >= PagePreview;
            run.Visible = showRun; nextButton.Visible = !showRun;
            if (!busy) run.Text = canRun ? "开始打孔 " + lastTotal + " 个孔" : "开始打孔";
            if (!showRun) nextButton.Text = "下一步：" + shell.Pages[Math.Min(cur + 1, shell.Pages.Count - 1)].Title;
            shell.Footer.PerformLayout();
        }

        // 侧栏每一步的小字和勾（RefreshPreview 每次都调）
        void RefreshRail(int total, bool specOk){
            int refTotal = groups.Sum(g => g.Count);
            string refSub = refTotal + slots.Count == 0 ? "点孔口圆边或带孔的面"
                          : (groups.Count > 0 ? groups.Count + " 组 · " + refTotal + " 个孔" : "") + (slots.Count > 0 ? (groups.Count > 0 ? " · " : "") + "腰孔 " + slots.Count : "");
            shell.Rail.Set(PageRefs, refSub, refTotal + slots.Count > 0, false);
            string faceSub = targets.Count == 0 ? (lastDrilled.Length > 0 ? "点下一个面接着打" : "点要配孔的平面") : targets.Count + " 个面";
            shell.Rail.Set(PageFaces, faceSub, targets.Count > 0, false);
            var sel = Selected;
            var sp = sel != null && sel.Spec != null ? sel.Spec : draft;
            string specSub = !specOk ? "规格有问题，打不了" : (groups.Count == 0 ? "草稿：" + ShortName(sp) : (groups.Count == 1 ? ShortName(groups[0].Spec) : groups.Count + " 组，按参考孔反推"));
            shell.Rail.Set(PageSpec, specSub, groups.Count > 0 && specOk, !specOk);
            shell.Rail.Set(PagePreview, total > 0 ? "将打 " + total + " 个孔" : (drilledOnce ? "已打完一批" : "确认后一次打完"), drilledOnce && targets.Count == 0, false);
            statHoles.Set(total.ToString(), total > 0 ? Ui.Ok : Ui.Text);
            statRefs.Set((refTotal + slots.Count).ToString(), Ui.Text);
            statFaces.Set(targets.Count.ToString(), Ui.Text);
            lastTotal = total;
            UpdatePrimary();
        }

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
            string problem = cur == null ? "" : cur.SpecProblem;
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
            refList.SelectedIndexChanged += (s, e) => { groupTabs.Selected = refList.SelectedIndex; if (!loading) LoadEditor(); };
            // 两个勾选记住用户上次的选择（只在用户自己点的时候存：Click 不会被代码改 Checked 触发，测试脚本改它不会写进用户设置）
            linkCheck.CheckedChanged += (s, e) => { RefreshPreview(); faceList.Invalidate(); };
            linkCheck.Click += (s, e) => SaveChoices();
            scanFaceCheck.Click += (s, e) => SaveChoices();
            // 打孔重试期间 CAD 正忙，这里把重试进度写到状态栏并让界面刷一次，
            // 用户看到的就不是"卡死了"，而是"正在重试 2/6"。
            progress = msg => {
                if (closing || IsDisposed) return;
                SetStatus(msg, Ui.Warn);
                try { status.Update(); System.Windows.Forms.Application.DoEvents(); } catch { }
            };
            AutoHoleWriter.Progress = progress;
        }

        // ---------------- 列表绘制 ----------------
        // 一组参考孔两行：上行「Φ6.6 ×4 → M6 螺纹孔（螺纹全长）」，下行灰字说明（规格是怎么来的 / 沉孔取下面的孔径）
        void DrawRefItem(object sender, DrawItemEventArgs e){
            var gr = e.Graphics;
            using (var w = new SolidBrush(Color.White)) gr.FillRectangle(w, e.Bounds);
            if (e.Index < 0 || e.Index >= groups.Count) return;
            var g = groups[e.Index];
            bool sel = (e.State & DrawItemState.Selected) != 0;
            var r = new Rectangle(e.Bounds.X, e.Bounds.Y + 3, e.Bounds.Width - 1, e.Bounds.Height - 7);
            Ui.FillRound(gr, r, 10, refFlash.Back(g, sel ? Ui.AccentSoft : Color.White));
            Ui.DrawRound(gr, r, 10, sel ? Ui.Accent : Ui.Line, sel ? 1.5f : 1f);
            int x = r.X + 14, y = r.Y + 9;
            string head = g.Head.Replace("　", " ");
            var headSz = TextRenderer.MeasureText(gr, head, Ui.NumB);
            TextRenderer.DrawText(gr, head, Ui.NumB, new Point(x, y), Ui.Text, TextFormatFlags.NoPadding);
            x += headSz.Width + 10;
            TextRenderer.DrawText(gr, "→", Ui.F9, new Point(x, y), Ui.Muted, TextFormatFlags.NoPadding);
            x += 22;
            var r1 = new Rectangle(x, y, Math.Max(0, r.Right - x - 10), 20);
            TextRenderer.DrawText(gr, g.Target, sel ? Ui.F9B : Ui.F9, r1, Ui.Accent, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            string note = g.MouthInfo.Length > 0 ? g.MouthInfo : (g.Spec != null && g.Spec.Note.Length > 0 ? g.Spec.Note : " ");
            var r2 = new Rectangle(r.X + 14, r.Y + 31, r.Width - 24, 18);
            TextRenderer.DrawText(gr, note, Ui.F8, r2, Ui.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }

        void DrawFaceItem(object sender, DrawItemEventArgs e){
            var gr = e.Graphics;
            using (var w = new SolidBrush(Color.White)) gr.FillRectangle(w, e.Bounds);
            if (e.Index < 0 || e.Index >= targetLabels.Count) return;
            bool sel = (e.State & DrawItemState.Selected) != 0;
            var r = new Rectangle(e.Bounds.X, e.Bounds.Y + 3, e.Bounds.Width - 1, e.Bounds.Height - 7);
            Ui.FillRound(gr, r, 10, faceFlash.Back(e.Index < targets.Count ? targets[e.Index] : null, sel ? Ui.AccentSoft : Color.White));
            Ui.DrawRound(gr, r, 10, sel ? Ui.Accent : Ui.Line, sel ? 1.5f : 1f);
            // 小面片图标
            int ix = r.X + 14, iy = r.Y + r.Height / 2;
            var sm = gr.SmoothingMode; gr.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var pen = new Pen(Ui.Muted, 1.2f))
                gr.DrawPolygon(pen, new[]{ new Point(ix, iy - 2), new Point(ix + 8, iy - 7), new Point(ix + 16, iy - 2), new Point(ix + 8, iy + 3) });
            gr.SmoothingMode = sm;
            // 这张面所在零件的链接属于别的装配（挂不了天工关联）：标个 ⚠，悬停看原因
            var t = e.Index < resolved.Count ? resolved[e.Index] : null;
            bool warn = (linkCheck.Checked && t != null && t.LinkNote.Length > 0) || (t != null && (t.Instances > 1 || t.Risk.Length > 0));
            string text = (warn ? "⚠ " : "") + targetLabels[e.Index] + (t != null && t.Instances > 1 ? "（装配里用了 " + t.Instances + " 次）" : "");
            var tr = new Rectangle(ix + 28, r.Y, r.Right - ix - 38, r.Height);
            TextRenderer.DrawText(gr, text, sel ? Ui.F9B : Ui.F9, tr, warn ? Ui.Warn : Ui.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
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
                // QuickPick（0.8.5，用户要的）：和天工自己的选择一样，右键 / 停一下会弹出光标下层层叠叠的面、边列表，
                // 点里面那个被挡住的就选中它（原来 seLocateSimple 只认最前面那层，右键还被拿去关窗口，遮挡面根本选不到）
                mouse.LocateMode = (int)SolidEdgeConstants.seLocateModes.seLocateQuickPick;
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
            if (!preloaded) { preloaded = true; try { BeginInvoke((Action)PreloadSelection); } catch { } }
        }

        // 打开窗口前在天工里已经选好的孔边 / 面（0.8.5）：很多人习惯先选再点命令，原来窗口打开后完全不管，还得重新点一遍。
        // 按选择的先后当成点了一下（带孔的面 = 参考孔，之后的面 = 打孔面，和用鼠标点同一套规则）。零件、子装配这类整件选择跳过
        bool preloaded;
        void PreloadSelection(){
            if (closing || busy || assembly == null) return;
            var items = new List<object>();
            try {
                var set = assembly.SelectSet; int n = set.Count;
                for (int i = 1; i <= n && i <= 200; i++) { object o = null; try { o = set.Item(i); } catch { } if (o != null && !(o is A.Occurrence) && !(o is A.SubOccurrence)) items.Add(o); }
            } catch (Exception e) { Log.Write("AutoHolePreselect", e.Message); return; }
            if (items.Count == 0) return;
            int refsBefore = groups.Sum(g => g.Count), facesBefore = targets.Count;
            foreach (var o in items) AcceptPick(o);
            int r = groups.Sum(g => g.Count) - refsBefore, f = targets.Count - facesBefore;
            if (r + f > 0) SetStatus("已接过天工里预先选中的对象：参考孔 " + r + " 个、打孔面 " + f + " 个。" + (f == 0 ? "再点要打孔的面。" : "检查预览后点「开始打孔」。"), Ui.Ok);
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
        public void AcceptPick(object selected){ AcceptPick(selected, false); }
        // ctrl：按住 Ctrl 点的面 —— 和「点平面时自动识别面上的孔」反着来（0.8.5）：
        //   勾着时 Ctrl 点 = 一定当打孔面（并撤掉刚从这张面认进来的参考孔）；勾掉时 Ctrl 点 = 把面上的孔认成参考孔
        public void AcceptPick(object selected, bool ctrl){
            if (busy || closing || selected == null) return;
            var before = groups.ToDictionary(g => g, g => g.Count);
            int slotsBefore = slots.Count, facesBefore = targets.Count;
            AcceptPickCore(selected, ctrl);
            AfterPick(before, slotsBefore, facesBefore);
        }
        // 点了什么就翻到那一步，新加的那一项闪一下（点面 → ② 打孔面；点孔 → ① 参考孔）
        void AfterPick(Dictionary<RefGroup, int> before, int slotsBefore, int facesBefore){
            if (closing || IsDisposed) return;
            if (targets.Count > facesBefore) {
                for (int i = facesBefore; i < targets.Count; i++) faceFlash.Mark(targets[i]);
                shell.Go(PageFaces);
                return;
            }
            bool grew = slots.Count > slotsBefore;
            foreach (var g in groups) { int n; if (!before.TryGetValue(g, out n) || g.Count > n) { refFlash.Mark(g); grew = true; } }
            if (grew) shell.Go(PageRefs);
        }
        void AcceptPickCore(object selected, bool ctrl){
            try {
                var pg = PickGeometry.Unwrap(selected);
                bool plane = pg.Geometry is G.Face && ((G.Face)pg.Geometry).Geometry is G.Plane;
                if (ctrl && plane && !scanFaceCheck.Checked) { PickFaceWithHoles(selected, true); RefreshAll(); return; }
                if (ctrl && plane) {
                    var tf = AutoHoleReader.ReadTarget(selected);
                    int dropped = DropRefsOn(tf);
                    AddTargetFace(selected, tf);
                    if (dropped > 0) SetStatus("已按 Ctrl 当打孔面：" + FaceLabel(tf) + "（刚才从这张面认进来的 " + dropped + " 个参考孔已撤掉）。", Ui.Ok);
                    RefreshAll();
                    return;
                }
                if (pg.Geometry is G.Edge) {
                    // 腰孔的圆弧：认整条腰孔（再点一次 = 取消）
                    bool arc = false;
                    try { arc = ((G.Edge)pg.Geometry).Geometry is G.Circle && !((G.Edge)pg.Geometry).IsClosed; } catch { }
                    SlotReference slot = arc ? SlotScanCad.FromArc(selected) : null;
                    if (slot != null) {
                        var dup = slots.FirstOrDefault(x => x.Same(slot));
                        if (dup != null) { slots.Remove(dup); slotPicks.Remove(dup); SetStatus("已取消这条参考腰孔。", Ui.Muted); }
                        else { slots.Add(slot); slotPicks[slot] = selected; SetStatus("已认出参考腰孔：" + slot.Label + "（目标件上配同样的腰孔，通切）。共 " + slots.Count + " 条腰孔。", Ui.Ok); }
                        RefreshAll();
                        return;
                    }
                    AddHole(AutoHoleReader.ReadReference(selected), selected, false);
                } else if (pg.Geometry is G.Face) {
                    var face = (G.Face)pg.Geometry;
                    if (!(face.Geometry is G.Plane)) {
                        string label;
                        var scanned = HoleScanCad.ScanSurface(selected, out label);
                        var r = AutoHoleReader.ToReference(scanned);
                        if (AddHole(r, selected, false)) SetStatus("已从" + label + "认出 Φ" + Num(r.DiameterMm)
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
                SetStatus(AutoHoleWriter.FriendlyPick(e), Ui.Warn);
                Log.Write("AutoHolePick", e);
            }
        }

        // 加一个参考孔：按孔径归到已有的那一类；没有就新建一类（规格自动反推）。
        // batch = 面扫描批量加：重复的孔静默跳过（返回 false），不打断整批。
        // 配孔检查「去配孔」带进来的孔：每个零件上的同一个孔可能有几条圆边（沉孔 = 过孔边 + 沉头边），
        // 配做取最小的那个直径（和面扫描"按孔口下面的孔径配做"同一个道理），最大的当孔口直径。
        // 0.8.4：带进来的孔按它的圆边选择引用再认一遍（和点孔口圆边同一条路），拿到零件、装配位置、孔所在的面 ——
        // 原来这些孔只有坐标，「保持关联」对它们一律挂不上，从配孔检查去配的孔参考零件一挪就全错位。认不出来才退回只用坐标。
        public void PreloadReferences(IList<HoleRecord> holes, string missingPart){
            if (holes == null || holes.Count == 0) return;
            int added = 0;
            foreach (var byPart in holes.GroupBy(h => h.PartName)) {
                var list = byPart.ToList();
                var bottom = list.OrderBy(h => h.DiameterMm).First();
                ReferenceHole r = null;
                try {
                    var sel = bottom.Selection;
                    if (sel != null) {
                        r = AutoHoleReader.ReadReference(sel);
                        // 认出来的孔径以配孔检查的为准（沉孔取下面的孔径），位置也要对得上，否则不用
                        double off = (r.Center - bottom.Center - bottom.Axis.Unit() * (r.Center - bottom.Center).Dot(bottom.Axis.Unit())).Length;
                        if (off > 0.0001) r = null;
                    }
                } catch (Exception e) { Log.Write("AutoHolePreloadRead", e.Message); r = null; }
                if (r == null) r = new ReferenceHole { Center = bottom.Center, Axis = bottom.Axis };
                r.DiameterMm = bottom.DiameterMm; r.MouthDiameterMm = Math.Max(r.MouthDiameterMm, list.Max(h => h.DiameterMm));
                r.Source = byPart.Key + "（配孔检查）";
                if (r.MouthDiameterMm - r.DiameterMm > HoleScan.StepToleranceMm && string.IsNullOrEmpty(r.MouthKind)) r.MouthKind = "孔口台阶";
                try { if (AddHole(r, bottom.Selection, true)) added++; } catch (Exception e) { Log.Write("AutoHolePreload", e); }
            }
            RefreshList(groups.Count - 1);
            RefreshPreview();
            SetStatus("已从配孔检查带入 " + added + " 个参考孔。请点" + (string.IsNullOrEmpty(missingPart) ? "要打孔的零件" : "「" + missingPart + "」") + "上要打孔的面，再点「开始打孔」。", Ui.Accent);
        }

        bool AddHole(ReferenceHole r, object selection, bool batch){
            // 同一根轴线上已经有参考孔了（0.8.5）：
            //   · 同一个孔再点一次 = 取消选它（原来报"已经选过了"，点错了只能去列表里找、整组移除）；
            //   · 别的零件上同轴的孔（上板 Φ6.6、垫板 Φ7 是同一根螺钉）：原来按孔径分进两组，投到打孔面上是同一个点，
            //     先打的那组说了算、后一组报"已经有孔、规格不同"，看着像出错。现在不再加，说清楚
            foreach (var og in groups) {
                var dup = og.Holes.FirstOrDefault(o => (o.Center - r.Center).Length < 1e-6 || SameAxis(o, r));
                if (dup == null) continue;
                if (batch) return false;
                bool samePart = ReferenceEquals(dup.Owner, r.Owner) || (dup.Placement != null && r.Placement != null && TargetFace.SamePlacement(dup.Placement, r.Placement));
                if (samePart && Math.Abs(og.DiameterMm - r.DiameterMm) <= HoleScan.GroupToleranceMm) {
                    int i = og.Holes.IndexOf(dup);
                    og.Holes.RemoveAt(i);
                    og.Selections.Remove(dup);
                    if (og.Holes.Count == 0) groups.Remove(og);
                    RefreshList(Math.Min(refList.SelectedIndex, groups.Count - 1));
                    SetStatus("已取消这个参考孔（再点一次又会加上）。还剩 " + groups.Sum(x => x.Count) + " 个。", Ui.Muted);
                    return false;
                }
                throw new ArgumentException("这个孔和已选的 Φ" + Num(og.DiameterMm) + " 参考孔同轴（同一根螺钉穿过的），按已选的那个配做，没有再加。");
            }
            var g = groups.FirstOrDefault(x => Math.Abs(x.DiameterMm - r.DiameterMm) <= HoleScan.GroupToleranceMm);
            bool isNew = g == null;
            HoleMatch match = null;
            if (isNew) { match = HoleMatcher.Match(r.DiameterMm); g = new RefGroup { DiameterMm = r.DiameterMm, Spec = match.Target }; groups.Add(g); }
            g.Holes.Add(r);
            if (selection != null) g.Selections[r] = selection;
            if (!batch) {
                RefreshList(groups.IndexOf(g));
                // 新的一组：把匹配结论（含"这个直径有歧义"）完整说出来，而不是只说"已匹配到 X"
                SetStatus(isNew ? HoleMatcher.StatusLine(match)
                                : ("同一组又加了一个 Φ" + Num(r.DiameterMm) + "，共 " + groups.Sum(x => x.Count) + " 个孔"),
                          isNew && match != null && match.Ambiguous ? Ui.Warn : Ui.Ok);
            }
            return true;
        }

        static bool SameAxis(ScannedHole a, ScannedHole b){
            try {
                var n = a.Axis.Unit();
                if (Math.Abs(Math.Abs(n.Dot(b.Axis.Unit())) - 1) > 1e-4) return false;
                var d = b.Center - a.Center;
                return (d - n * d.Dot(n)).Length < 0.00005;   // 横向 0.05mm 以内
            } catch { return false; }
        }

        // 点平面 + 勾着"自动认孔"：面上有孔就全认出来当参考孔（**不当打孔面**）；
        // 一个孔都没有才当打孔面 —— 这样"点带孔的面"和"点要打孔的面"不用切模式。
        void PickFaceWithHoles(object selected){ PickFaceWithHoles(selected, false); }
        // onlyRefs：Ctrl 点的（自动识别关着时）—— 用户就是要认孔，不往打孔面上拐
        void PickFaceWithHoles(object selected, bool onlyRefs){
            var t = AutoHoleReader.ReadTarget(selected);
            string how; int notHole;
            var holes = HoleScanCad.ScanFace(t, out how, out notHole);
            if (holes.Count == 0 && onlyRefs) { SetStatus("这张面上没有认出孔（" + FaceLabel(t) + "）。" + (how.Length > 0 ? "注意：" + how : ""), Ui.Warn); return; }
            if (holes.Count == 0) {
                AddTargetFace(selected, t);
                string inst = InstanceNote(t);
                SetStatus("这个面上没有识别到孔，已作为打孔面：" + FaceLabel(t) + (inst.Length > 0 ? "　⚠ " + inst : "") + (how.Length > 0 ? "　注意：" + how : ""), inst.Length > 0 ? Ui.Warn : Ui.Muted);
                return;
            }
            // 0.8.5：已经点好参考孔、再点要打孔的面，而这张面上本来就有几个别的孔（现场最常见：底板上早打过几个安装孔）——
            // 原来一律当"带孔的面"扫成参考孔，打孔面加不进去，用户得先去勾掉「自动识别」。
            // 现在：已选的参考孔里有能投到这张面上、而且投到的位置还空着的，这张面就是打孔面
            int free = onlyRefs ? 0 : FreeSpots(t, holes);
            if (free > 0) {
                AddTargetFace(selected, t);
                SetStatus("已作为打孔面：" + FaceLabel(t) + "（面上本来有 " + holes.Count + " 个孔，没有当参考孔；已选的参考孔里 " + free + " 个投到这张面的空位上）。"
                          + "要把面上的孔当参考孔，先在②里移除这张面、再点一次。" + (InstanceNote(t).Length > 0 ? "　⚠ " + InstanceNote(t) : ""), InstanceNote(t).Length > 0 ? Ui.Warn : Ui.Ok);
                return;
            }
            int added = 0, dup = 0;
            foreach (var scanned in holes)
                if (AddHole(AutoHoleReader.ToReference(scanned), null, true)) added++; else dup++;
            scannedPicks.Add(selected);      // 只用于高亮：让用户看得见"刚才点的是哪张面"
            lastScanNote = HoleScan.StatusLine(holes)
                         + (dup > 0 ? "；其中 " + dup + " 个已经在列表里" : "")
                         + (notHole > 0 ? "；另有 " + notHole + " 个圆判定为凸台/非孔，已跳过" : "")
                         + "。要把这张面当打孔面：按住 Ctrl 再点一次（会撤掉从它认进来的参考孔）。";
            SetStatus("已从 " + FaceLabel(t) + " 上识别到 " + holes.Count + " 个孔，共 " + groups.Count + " 组（新增 " + added + " 个）。", Ui.Ok);
        }

        // 从这张面认进来的参考孔（同一个零件实例、孔口在这张面上）撤掉，返回撤掉几个
        int DropRefsOn(TargetFace t){
            int n = 0;
            var nrm = t.Plane.Normal.Unit();
            foreach (var g in groups.ToList()) {
                foreach (var h in g.Holes.ToList()) {
                    bool samePart = h.Placement != null && TargetFace.SamePlacement(h.Placement, t.Placement) && ReferenceEquals(h.Owner, t.Part);
                    if (!samePart) { try { samePart = h.Placement != null && TargetFace.SamePlacement(h.Placement, t.Placement) && string.Equals(((P.PartDocument)h.Owner).FullName, t.Part.FullName, StringComparison.OrdinalIgnoreCase); } catch { } }
                    if (!samePart || Math.Abs((h.Center - t.Plane.Point).Dot(nrm)) > 0.0002) continue;
                    g.Holes.Remove(h); g.Selections.Remove(h); n++;
                }
                if (g.Holes.Count == 0) groups.Remove(g);
            }
            if (n > 0) { scannedPicks.Clear(); lastScanNote = ""; }
            return n;
        }

        // 参考腰孔沿它的贯穿方向投到打孔面上的中心（斜的 > 2° 不投）
        static bool SlotCentreOn(SlotReference sl, TargetFace t, out V3 centre){
            centre = new V3();
            var n = t.Plane.Normal.Unit();
            double denom = sl.Axis.Unit().Dot(n);
            if (Math.Abs(denom) < Math.Cos(AutoHoleReader.ObliqueLimitDeg * Math.PI / 180.0)) return false;
            centre = sl.Center + sl.Axis.Unit() * ((t.Plane.Point - sl.Center).Dot(n) / denom);
            return centre.Finite;
        }
        // 这张面上配参考腰孔：按尺寸分批走 AutoHoleWriter.DrillSlots（通切）；带零件间链接的零件要在原位编辑里切（同排孔）
        void DrillSlotsOn(TargetFace t, string partName, Tally y){
            var todo = new List<SlotReference>(); var centres = new List<V3>();
            foreach (var sl in slots) { V3 c; if (SlotCentreOn(sl, t, out c) && FaceFrameReader.ContainsPoint(t, c)) { todo.Add(sl); centres.Add(c); } }
            if (todo.Count == 0) return;
            try {
                if (!InPlaceFor(t) && NativeHoleLink.HasLinks(t.Part)) {
                    string ewhy; var ip = NativeHoleLink.EnterTarget(app, assembly, t, out ewhy);
                    if (ip == null) Log.Write("AutoHoleSlotEnter", ewhy); else Hold(ip, t);
                }
                if (!y.Parts.Contains(t)) y.Parts.Add(t);
                var n = t.Plane.Normal.Unit();
                foreach (var size in todo.Select((sl, i) => new { sl, i }).GroupBy(x => x.sl.LengthMm.ToString("0.###") + "x" + x.sl.WidthMm.ToString("0.###"))) {
                    var lc = new List<V3>(); var ld = new List<V3>();
                    foreach (var x in size) {
                        var d = x.sl.Direction - n * x.sl.Direction.Dot(n);
                        lc.Add(t.Placement.InversePoint(t.OntoFace(centres[x.i])));
                        ld.Add(t.Placement.InverseNormal(d.Unit()).Unit());
                    }
                    var first = size.First().sl;
                    var res = AutoHoleWriter.DrillSlots(t, lc, ld, first.LengthMm, first.WidthMm);
                    y.Made += res.Created; y.Failed += res.Failures.Count;
                    foreach (var o in res.Outcomes) { o.Text = partName + " " + o.Text; y.Outcomes.Add(o); }
                    y.Undo.Absorb(res.Undo);
                    if (res.Created > 0) y.Features.Add(partName + "：腰孔 " + first.LengthMm.ToString("0.##") + "×" + first.WidthMm.ToString("0.##") + " ×" + res.Created + "（通切，不挂关联）");
                    if (!y.Methods.Contains("腰孔（通切）")) y.Methods.Add("腰孔（通切）");
                }
            } catch (Exception e) {
                Log.Write("AutoHoleSlots", e);
                y.Failed += todo.Count;
                AddProblem(y.Problems, partName + "：腰孔没切成 —— " + AutoHoleWriter.Friendly(e));
            }
        }

        // 已选的参考孔投到这张面上、落在面内、而且那里还没有孔（面上扫到的孔都不同轴）的个数
        int FreeSpots(TargetFace t, IList<ScannedHole> onFace){
            int free = 0;
            foreach (var g in groups) foreach (var h in g.Holes) {
                try {
                    var c = AutoHoleReader.Intersect(h, t);
                    if (!FaceFrameReader.ContainsPoint(t, c)) continue;
                    bool taken = false;
                    foreach (var s in onFace) {
                        var n = s.Axis.Unit(); var d = c - s.Center;
                        if ((d - n * d.Dot(n)).Length < HoleSpots.RadialTolM) { taken = true; break; }
                    }
                    if (!taken) free++;
                } catch { }
            }
            return free;
        }

        // 加一个打孔面（老行为）。known 非空时不再重复解析面片。
        void AddTargetFace(object selected, TargetFace known){
            var t = known != null ? known : AutoHoleReader.ReadTarget(selected);
            // 这张面挂不挂得上天工关联，点的时候就预判一下（零件的链接属于别的装配就挂不上），预览里提前说
            try { t.LinkNote = NativeHoleLink.LinkOwnerElsewhere(assembly, t); } catch (Exception e) { Log.Write("AutoHoleLinkPrecheck", e.Message); }
            t.Instances = InstancesOf(t.Part);
            try { t.Risk = TargetFace.RiskOf(t.Part, assembly == null ? "" : assembly.FullName); } catch { }
            for (int i = 0; i < targets.Count; i++) {
                TargetFace old = i < resolved.Count ? resolved[i] : null;
                if (old == null) { try { old = AutoHoleReader.ReadTarget(targets[i]); } catch { } }   // 老面片已经失效：判断不了就当它不是重复的
                // 同一张面 = 同一个零件实例 + 同一个实体 + 同一个面 ID + 平面重合（只比面 ID 不行：多实体零件的面 ID 跨实体重号，见 TargetFace.SameFace）
                if (!TargetFace.SameFace(old, t)) continue;
                // 0.8.5：面对象还活着 = 用户是想取消这张面（和参考孔一样，再点一次 = 取消）；
                // 已经失效（打孔失败提示"请重新点一次这张面"的那种）才是重新读取
                bool alive = false;
                try { if (old != null && old.Face != null) { var id = old.Face.ID; alive = TargetFace.PartAlive(old.Part); } } catch { alive = false; }
                if (alive && i < resolved.Count && resolved[i] != null) {
                    targets.RemoveAt(i); targetLabels.RemoveAt(i); resolved.RemoveAt(i);
                    RefreshAll();
                    SetStatus("已取消打孔面：" + FaceLabel(t) + "（再点一次又会加上）。", Ui.Muted);
                    return;
                }
                // 同一张面再点一次 = 用新点的这次换掉旧的（0.7.13 现场：打孔失败提示"请重新点一次这个面"，
                // 用户照做却被"这个面已经选过了"挡回去，后面每次都拿旧的面数据重试，一直失败）
                targets[i] = selected;
                targetLabels[i] = FaceLabel(t);
                while (resolved.Count <= i) resolved.Add(null);
                resolved[i] = t;
                RefreshAll();
                SetStatus("已重新读取打孔面：" + FaceLabel(t), Ui.Ok);
                return;
            }
            targets.Add(selected);
            targetLabels.Add(FaceLabel(t));
            while (resolved.Count < targets.Count - 1) resolved.Add(null);
            resolved.Add(t);
            RefreshAll();
            bool linkWarn = t.LinkNote.Length > 0 && linkCheck.Checked;
            string inst = InstanceNote(t);
            SetStatus("已选 " + targets.Count + " 个打孔面：" + FaceLabel(t)
                      + (inst.Length > 0 ? "　⚠ " + inst : "")
                      + (linkWarn ? "　⚠ 这个零件的链接属于「" + t.LinkNote + "」，这里打的孔挂不了天工关联，会改用插件关联（同步孔位）" : ""),
                      linkWarn || inst.Length > 0 ? Ui.Warn : Ui.Ok);
        }

        // 零件文件在装配里用了几次（同一个文件只数一次；打完孔不变，窗口开着时一直有效）
        readonly Dictionary<string, int> instanceCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int InstancesOf(P.PartDocument part){
            string f = ""; try { f = part.FullName; } catch { }
            if (string.IsNullOrEmpty(f)) return 0;
            int n;
            if (!instanceCount.TryGetValue(f, out n)) { n = AssemblyPart.CountInstances(assembly, f); instanceCount[f] = n; }
            return n;
        }
        static string InstanceNote(TargetFace t){
            if (t == null) return "";
            var l = new List<string>();
            if (t.Instances > 1) l.Add("「" + t.PartName + "」在装配里用了 " + t.Instances + " 次：孔打在零件文件上，" + t.Instances + " 处都会有这些孔");
            if (t.Risk.Length > 0) l.Add(t.Risk);
            return string.Join("；", l.ToArray());
        }

        static string FaceLabel(TargetFace t){
            string part = t.PartName != null && t.PartName.Length > 0 ? t.PartName : "零件";
            return part + "　" + t.Label;   // 多实体零件的 Label 带「实体 N ·」（见 AutoHoleReader.FaceText）
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
            RefreshGroupTabs();
            LoadEditor();
        }

        RefGroup Selected { get { int i = refList.SelectedIndex; return (i >= 0 && i < groups.Count) ? groups[i] : null; } }

        void RefreshAll(){
            RefreshFaces();
            RefreshList(refList.SelectedIndex);
            RefreshPreview();
            refEmpty.Visible = groups.Count == 0;
            faceEmpty.Visible = targets.Count == 0;
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
            // 行数变了：网格跟着变高/变矮（下面的行自动顶上来，不留空带）
            grid.Height = grid.PreferredHeight;
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
            return m.HasRow ? m.Row : HoleMatcher.M6;
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
            RefreshGroupTabs();
            ShowKindFields();
            RefreshEditHeader();
            RefreshPreview();
            RefreshShapeViews();
        }

        void RemoveSelected(){
            if (busy) return;   // 打孔中（状态栏刷新时会泵消息）删列表 = 循环底下的数据没了
            int i = refList.SelectedIndex;
            if (i < 0 || i >= groups.Count) { SetStatus("先在列表里选中一组参考孔。", Ui.Warn); return; }
            groups.RemoveAt(i);
            RefreshAll();
            SetStatus("已移除一组参考孔。", Ui.Muted);
        }
        void RemoveSelectedFace(){
            if (busy) return;
            int i = faceList.SelectedIndex;
            if (i < 0 || i >= targets.Count) { SetStatus("先在列表里选中一个打孔面。", Ui.Warn); return; }
            targets.RemoveAt(i); targetLabels.RemoveAt(i);
            if (i < resolved.Count) resolved.RemoveAt(i);
            RefreshAll();
            SetStatus("已移除一个打孔面。", Ui.Muted);
        }

        // ---------------- 预览 ----------------
        // 不打孔，先算一遍：每个参考孔的轴线投到每张面上，落在面内才算一个孔。
        void RefreshPreview(){
            int total = 0, outside = 0, oblique = 0, already = 0, edgeCut = 0, slotHere = 0; double obliqueDeg = 0;
            var perFace = new List<string>();
            var linkElsewhere = new List<string>();   // 要打孔、但零件的链接属于别的装配（挂不了天工关联）的那些装配名
            var multiUse = new List<string>();        // 要打孔的零件在装配里用了好几次（每处都会长出孔）
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
                        // 这个位置本来就有孔（孔口在这张面上）：不算"将打"，单独说（0.8.5，原来预览说将打 4 个、打完才说 2 个本来就有）
                        if (t.HasHoleAt(c)) { already++; continue; }
                        here++;
                        if (CutsEdge(t, c, g.Spec)) edgeCut++;
                    } catch (ObliqueHoleException e) {
                        oblique++; obliqueDeg = Math.Max(obliqueDeg, e.Degrees); continue;
                    } catch { skip++; continue; }
                    try {
                        string name = HoleNaming.Short(g.Spec);
                        if (!specCount.ContainsKey(name)) { specCount[name] = 0; perSpec.Add(name); }
                        specCount[name]++;
                    } catch { }
                }
                foreach (var sl in slots) {
                    V3 sc; if (SlotCentreOn(sl, t, out sc) && FaceFrameReader.ContainsPoint(t, sc)) { here++; slotHere++; } else skip++;
                }
                total += here; outside += skip;
                if (here > 0 || skip > 0) perFace.Add(t.PartName + (PartBodies.Multi(t.Part) ? " 实体 " + Math.Max(1, t.Body) : "") + " " + here + " 个" + (skip > 0 ? "（跳过 " + skip + "）" : ""));
                if (here > 0 && t.LinkNote.Length > 0 && !linkElsewhere.Contains(t.LinkNote)) linkElsewhere.Add(t.LinkNote);
                string inst = InstanceNote(t);
                if (here > 0 && inst.Length > 0 && !multiUse.Contains(inst)) multiUse.Add(inst);
            }
            int refTotal = groups.Sum(g => g.Count) + slots.Count;
            if (refTotal == 0) {
                chip.Text = "等待点孔"; chip.ChipColor = Ui.Muted;
                previewLine.Text = "① 先在模型上点孔：点圆边、点孔内壁，或直接点一个带孔的面";
                previewSub.Text = "点一个充满孔的面会把面上的孔全认出来，同孔径的自动归一组；沉孔/锥沉/倒角孔口自动取孔口下面的孔径来配做。";
            } else if (targets.Count == 0) {
                chip.Text = "已选 " + refTotal + " 个孔"; chip.ChipColor = Ui.Accent;
                previewLine.Text = lastDrilled.Length > 0 ? "② 上一批面已经打完，点下一个要打孔的面接着打" : "② 再点要打孔的面";
                previewSub.Text = (lastDrilled.Length > 0 ? "已打：" + lastDrilled + "　—　" : "")
                                  + groups.Count + " 组参考孔：" + string.Join("；", groups.Select(g => g.Head + " → " + g.Target).ToArray())
                                  + (slots.Count > 0 ? "；参考腰孔 " + string.Join("、", slots.Select(x => x.Label).ToArray()) : "")
                                  + (lastScanNote.Length > 0 && lastDrilled.Length == 0 ? "　—　" + lastScanNote : "");
            } else {
                chip.Text = "将打 " + total + " 个孔"; chip.ChipColor = total > 0 ? Ui.Ok : Ui.Warn;
                previewLine.Text = total > 0
                    ? string.Join("；", perSpec.Select(n => n + " ×" + specCount[n]).Concat(slotHere > 0 ? new[]{ "腰孔 ×" + slotHere } : new string[0]).ToArray())
                    : already > 0 ? "这些位置本来就都有孔了，不用再打" : "没有孔可以打，请看下面的原因";
                string s = "打孔面：" + string.Join("；", perFace.ToArray());
                if (outside > 0 && total == 0)
                    s = "选中的面与参考孔不同轴：孔心的投影落在面外面，所以没有孔可打。"
                      + "请点与这些孔轴线相交的那个面（比如孔正上方/正下方零件的那个面）。";
                else if (outside > 0)
                    s += "　—　另有 " + outside + " 个孔心投影落在面外，会自动跳过。";
                if (total > 0) s += "　·　同规格的孔合成一个孔特征" + (linkCheck.Checked ? "，和孔位链接编进一个「关联孔」组" : "");
                if (already > 0) s += "　—　另有 " + already + " 个位置本来就有孔，不会再打。";
                if (edgeCut > 0) s = "⚠ " + edgeCut + " 个孔的孔口会切到面的边上、或和旁边已有的孔相交（打出来是缺口），请确认打孔面和规格。　" + s;
                if (oblique > 0) s = "⚠ " + oblique + " 个参考孔和打孔面不垂直（最斜 " + Num(obliqueDeg) + "°），打出来不同轴，不会打。　" + s;
                // 保持关联、但零件的链接属于别的装配：打之前就说清楚这些孔挂不了天工关联、会改用插件关联
                if (linkCheck.Checked && linkElsewhere.Count > 0)
                    s = "⚠ 有零件的链接属于「" + string.Join("」「", linkElsewhere.ToArray()) + "」，那里打的孔挂不了天工关联，会改用插件关联（参考零件挪了点「同步孔位」）。要天工关联请到那个装配里打。　" + s;
                if (multiUse.Count > 0) s = "⚠ " + string.Join("；", multiUse.ToArray()) + "。　" + s;
                // 锥形沉孔的默认锥孔直径是按螺钉头径估算的，必须让用户看见这句话
                foreach (var g in groups) {
                    if (g.Spec != null && g.Spec.Kind == HoleKind.Countersink && g.Spec.Note.Length > 0) {
                        s += "　⚠ " + g.Head + "：" + g.Spec.Note;
                        break;
                    }
                }
                foreach (var g in groups) {
                    if (g.Spec == null) continue;
                    string tp = g.Spec.SpecProblem;
                    if (tp.Length > 0) { s = "⚠ " + g.Head + "：" + tp + "　" + s; break; }
                }
                previewSub.Text = s;
            }
            tips.SetToolTip(previewSub, previewSub.Text);
            tips.SetToolTip(previewLine, previewLine.Text);
            bool specOk = groups.All(g => g.Spec == null || g.Spec.SpecProblem.Length == 0);
            refCount.Text = refCount.Text.Split('·')[0].TrimEnd() + (slots.Count > 0 ? " · 腰孔 " + slots.Count : "");
            run.Enabled = total > 0 && !busy && specOk;
            RefreshRail(total, specOk);
            if (highlights != null) {
                try {
                    highlights.RemoveAll();
                    foreach (var t in targets) { try { highlights.AddItem(t); } catch { } }
                    // 参考孔也高亮，用户才看得见自己点了哪几个孔。
                    // 打完孔以后这些选择引用会失效，失效的直接跳过（打孔用的是纯数据，不依赖它们）。
                    foreach (var g in groups) foreach (var s in g.Selections.Values) { try { highlights.AddItem(s); } catch { } }
                    // 被"面扫描"点过的那张面也高亮（它不是打孔面，但用户得看见自己点的是哪张）
                    foreach (var s in scannedPicks) { try { highlights.AddItem(s); } catch { } }
                    foreach (var s in slotPicks.Values) { try { highlights.AddItem(s); } catch { } }
                    highlights.Draw();
                } catch (Exception e) { Log.Write("AutoHoleHighlight", e); }
            }
            chip.Invalidate();
        }

        // 孔口（沉孔/锥孔取外径）一圈上有没有点落在面外（0.8.5）：孔心在面上、孔却切到面边上 —— 多半是点错了面或参考孔对着的是别的零件
        static bool CutsEdge(TargetFace t, V3 centre, HoleSpec spec){
            if (spec == null) return false;
            double d = spec.Kind == HoleKind.Counterbore ? spec.CounterboreDiameter : spec.Kind == HoleKind.Countersink ? spec.CountersinkDiameter : spec.HoleDiameter;
            return FaceFrameReader.CutsEdge(t, centre, d);
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
        object SafeActive(){ try { return app.ActiveDocument; } catch { return null; } }

        // ---------------- 关联孔位 ----------------
        const string NotScanned = "点「检查」查看本装配里的关联孔（要遍历全部零件，所以不自动查）";

        void ShowLinkReport(SyncReport report, string fallback){
            lastLinks = report;
            if (report == null) {
                linkLabel.Text = fallback;
                linkLabel.ForeColor = Ui.Muted;
                // 0.7.8：原生关联的孔靠「同步孔位」里的"更新链接"，所以不用先检查也能点
                syncButton.Enabled = assembly != null && !busy;
                syncButton.Text = "同步孔位";
                rescanButton.Enabled = assembly != null;
                return;
            }
            linkLabel.Text = report.Summary;
            linkLabel.ForeColor = report.Misplaced > 0 ? Ui.Warn : (report.Broken > 0 ? Ui.Warn : Ui.Text);
            syncButton.Enabled = assembly != null && !busy;
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
        // 查关联孔要把装配里每个零件加载一遍（大装配一两分钟）：期间刷状态栏、泵消息，别让 Windows 把天工判成"未响应"。
        // 泵消息时窗口按 busy 挡住关闭和改列表（IBusyTool），所以查的时候也要置 busy（原来「检查」不置 busy）
        Action<int, int> LinkPulse(){
            var tick = System.Diagnostics.Stopwatch.StartNew();
            return (i, n) => {
                if (tick.ElapsedMilliseconds < 300) return;
                tick.Restart();
                SetStatus("正在读取装配里的零件 " + i + " / " + n + " …", Ui.Accent);
                try { status.Update(); System.Windows.Forms.Application.DoEvents(); } catch { }
            };
        }
        void ScanLinks(bool manual){
            if (assembly == null || closing) return;
            if (busy) return;
            try {
                UseWaitCursor = true; busy = true; rescanButton.Enabled = false; syncButton.Enabled = false; run.Enabled = false;
                var report = HoleSync.Scan(assembly, LinkPulse());
                busy = false;
                ShowLinkReport(report, "");
                if (manual) SetStatus(report.Summary, report.Misplaced > 0 ? Ui.Warn : Ui.Ok);
            } catch (Exception e) {
                busy = false;
                Log.Write("AutoHoleLinkScan", e);
                ShowLinkReport(null, "检查失败：" + e.Message);
            } finally { busy = false; UseWaitCursor = false; RefreshPreview(); }
        }

        // 一键同步：先重新查一遍（别拿着几分钟前的结果去改模型），再把错位的孔挪回去。
        void SyncLinks(){
            if (assembly == null || busy || closing) return;
            hadCommand = command != null;
            try {
                busy = true; UseWaitCursor = true; syncButton.Enabled = false; run.Enabled = false;
                SetStatus("正在同步孔位…", Ui.Accent);
                StopCommand();
                // 天工原生关联的孔：让天工更新链接（= 天工里「更新所有链接」），孔自己跟上参考孔
                try { NativeHoleLink.UpdateLinks(assembly); } catch (Exception e) { Log.Write("AutoHoleUpdateLinks", e); }
                var report = HoleSync.Scan(assembly, LinkPulse());
                int want = report.Misplaced;
                string native = report.NativeLinks > 0
                    ? "天工关联：已更新 " + report.NativeLinks + " 个孔位链接" + (report.NativeBroken > 0 ? "，其中 " + report.NativeBroken + " 个断了（参考零件被删掉或换掉了）" : "")
                    : "";
                if (want == 0) {
                    ShowLinkReport(report, "");
                    string ok = (native.Length > 0 ? native + "；" : "已让天工更新链接；")
                              + (report.Linked > 0 ? "插件关联的 " + report.Linked + " 个孔都已和参考孔同轴" : "没有插件关联的孔")
                              + (report.Broken > 0 ? "（" + report.Broken + " 个找不到参考孔）" : "") + "。";
                    LastResult = ok;
                    SetStatus(ok, report.NativeBroken > 0 || report.Broken > 0 ? Ui.Warn : Ui.Ok);
                    if (report.NativeBroken > 0 && !SuppressDialogs)
                        MessageBox.Show(this, ok + "\r\n\r\n断了的孔位链接：\r\n· " + string.Join("\r\n· ", report.NativeBrokenLabels.Take(12).ToArray())
                                        + "\r\n\r\n这些孔不会再跟着参考孔走。在天工特征树里看这个链接的状态；不要了就删掉它所在的「关联孔 …」组重新打。",
                                        "同步孔位", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                } else {
                    HoleSync.Apply(report);
                    string msg = "同步孔位：挪回 " + report.Moved + " 个孔" + (report.Failed > 0 ? "，失败 " + report.Failed + " 个" : "") + "。";
                    if (native.Length > 0) msg += "\r\n" + native + "。";
                    if (report.NativeBrokenLabels.Count > 0) msg += "\r\n断了的孔位链接：" + string.Join("；", report.NativeBrokenLabels.Take(8).ToArray());
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
                    // Apply 已经把挪好的孔在报告里改成"同轴"：直接显示，不再整装配重扫一遍（建实例索引要把每个零件加载一遍，大装配上一分多钟）
                    ShowLinkReport(report, "");
                }
            } catch (Exception e) {
                Log.Write("AutoHoleSync", e);
                SetStatus("同步孔位失败：" + AutoHoleWriter.Friendly(e), Ui.Bad);
                LastResult = "同步中断：" + e.Message;
                if (!SuppressDialogs) MessageBox.Show(this, AutoHoleWriter.Friendly(e), "同步孔位", MessageBoxButtons.OK, MessageBoxIcon.Error);
            } finally {
                busy = false; UseWaitCursor = false;
                if (hadCommand) { try { StartPicking(); } catch (Exception ex) { SetStatus(AutoHoleWriter.Friendly(ex), Ui.Warn); } }
                if (lastLinks != null) ShowLinkReport(lastLinks, "");
                RefreshPreview();
            }
        }

        // ---- 测试 / 真机实测脚本用的缝（给自动化读界面状态、免弹框）----
        // 读到的就是窗口上看到的那一份数据；打孔走的是和点按钮同一条路。
        public bool SuppressDialogs;       // 真机脚本里置真：结果框改为写进 LastResult
        public string LastResult = "";
        public void DrillForTest(){ RunDrill(); }
        // 翻到第 i 步（0 参考孔 / 1 打孔面 / 2 孔规格 / 3 预览并打孔），测试和截图用
        public void GoPageForTest(int i){ shell.Go(i, false); }
        public int PageForTest { get { return shell.Current; } }
        public void ClearAllForTest(){ ClearAll(); }
        void ClearAll(){ if (busy) return; shell.Go(PageRefs); groups.Clear(); slots.Clear(); slotPicks.Clear(); targets.Clear(); targetLabels.Clear(); resolved.Clear(); scannedPicks.Clear(); lastScanNote = ""; lastDrilled = ""; RefreshAll(); SetStatus("已全部清空，重新点孔。", Ui.Accent); }
        public void SyncForTest(){ SyncLinks(); }
        public SyncReport LinkReportForTest(){ ScanLinks(false); return lastLinks; }
        public bool KeepLink { get { return linkCheck.Checked; } set { linkCheck.Checked = value; } }
        public int LocateModeForTest { get { try { return mouse == null ? -1 : mouse.LocateMode; } catch { return -2; } } }
        public void StopPickingForTest(){ StopCommand(); }
        public bool ScanFaceOnPick { get { return scanFaceCheck.Checked; } set { scanFaceCheck.Checked = value; } }
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
            sb.Append(" 关联=关联孔位：").Append(linkLabel.Text);
            sb.Append(" 状态=").Append(status.Text);
            return sb.ToString();
        }

        // ---------------- 打孔 ----------------
        bool hadCommand;
        Action<string> progress;
        // 一次「开始打孔」的累计（各张面打完往里加）
        sealed class Tally {
            public int Made, Skipped, Failed, Already, Linked, NativeLinked, NativeMissed, PluginLinked, Unlinked;
            public readonly List<string> Problems = new List<string>(), Methods = new List<string>(), Audits = new List<string>(), Features = new List<string>();
            public readonly List<ResultList.Item> Outcomes = new List<ResultList.Item>();
            public readonly HoleUndo Undo = new HoleUndo();
            public readonly List<TargetFace> Parts = new List<TargetFace>();   // 真正打过的面（收尾时作废这些零件的圆缓存）
            // 没挂上天工关联的原因 → 孔数（按零件分开说）；以及其中改写了插件关联的孔数
            public readonly List<string> LinkReasons = new List<string>();
            public readonly Dictionary<string, int> LinkCount = new Dictionary<string, int>(), LinkPlugin = new Dictionary<string, int>();
        }

        // 打孔面按零件实例排：同一个零件（多实体零件的几张面最常见）的面挨着打，一次原位编辑打完（见 RunDrill）。
        // 原来每张面都进一次、退一次原位编辑（每次 1～2 秒、窗口闪一下），一个零件点了五张面就进出五次。
        List<int> FaceOrder(){
            var keys = new List<string>(); var by = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            for (int i = 0; i < targets.Count; i++) {
                string k;
                try { k = ResolvedFace(i).InstanceKey; } catch { k = "#" + i; }
                List<int> l; if (!by.TryGetValue(k, out l)) { l = new List<int>(); by[k] = l; keys.Add(k); }
                l.Add(i);
            }
            var order = new List<int>();
            foreach (var k in keys) order.AddRange(by[k]);
            return order;
        }

        // 正在原位编辑的零件实例：同一个零件的下一张面接着用，换零件 / 打完 / 出错时再退（Release）
        NativeHoleLink.InPlace held; string heldKey = "";
        // 这张面进了原位：把"要退几级"交给 held，这张面自己的 InPlace 只剩"建了哪些孔位链接"（End 时不退）
        void Hold(NativeHoleLink.InPlace s, TargetFace t){
            if (s == null || s.Levels <= 0) return;
            if (held != null && heldKey != t.InstanceKey) Release();
            if (held == null) { held = new NativeHoleLink.InPlace { Levels = s.Levels, Part = s.Part }; heldKey = t.InstanceKey; }
            else held.Levels += s.Levels;
            s.Levels = 0;
        }
        void Release(){
            var h = held; held = null; heldKey = "";
            if (h != null) { try { NativeHoleLink.Leave(app, h); } catch (Exception e) { Log.Write("AutoHoleLeave", e); } }
        }
        bool InPlaceFor(TargetFace t){ return held != null && heldKey == t.InstanceKey; }

        void RunDrill(){
            if (busy || closing) return;
            try {
                if (groups.Count == 0 && slots.Count == 0) { SetStatus("请先点一个已有的孔（点孔口圆边、孔内壁，或点一个带孔的面自动认孔；腰孔点它一头的圆弧）。", Ui.Warn); return; }
                if (targets.Count == 0) {
                    SetStatus(lastDrilled.Length > 0 ? "上一批打孔面已经打完了，请点下一个要打孔的面（参考孔不用重选）。"
                                                     : "请先点要打孔的面（面上有孔时会被当成参考孔；要打孔的面请点一个没有孔的面，或勾掉「点平面时自动识别面上的孔」）。", Ui.Warn);
                    return;
                }
                foreach (var g in groups) {
                    string tp = g.Spec == null ? "" : g.Spec.SpecProblem;
                    if (tp.Length > 0) { SetStatus(g.Head + "：" + tp, Ui.Warn); return; }
                }
                // 装配本身还开着吗（窗口开着时用户把装配关了、又重新打开）：原来往下走报"被调用的对象已与其客户端断开连接"
                try { var nm = assembly.Name; } catch { SetStatus("打开这个窗口时的装配已经关掉了。请关掉这个窗口，在装配里重新点「自动打孔」。", Ui.Bad); LastResult = "打孔中断：装配已关闭"; return; }
                // 参考孔的零件 / 面对象还活着吗：天工重新加载过那个文档（比如关掉了原位编辑留下的那个零件窗口）以后，
                // 原来点的对象全部"对象已断开"（WindowCloseProbe 实测）。与其每个孔失败一次，不如先说清楚、让用户重选。
                string dead = DeadReference();
                if (dead.Length > 0) { SetStatus(dead, Ui.Warn); LastResult = "打孔中断：" + dead; return; }
                // 原位编辑（双击零件进入）**不是**错误：CAD 只在"正在编辑的那个零件文档"里允许写模型，
                // 所以这里放开它。只读装配仍然拒绝。
                if (assembly.ReadOnly) throw new InvalidOperationException("装配是只读打开的，无法打孔。请用可写方式打开装配后再试。");

                busy = true; run.Enabled = false; run.Text = "打孔中…"; run.Progress = 0; UpdatePrimary(); UseWaitCursor = true;
                AutoHoleWriter.Progress = progress;   // 开着两个自动打孔窗口时，进度写到正在打孔的这一个
                hadCommand = command != null;   // 打完只恢复"原来就在选"的状态（外部脚本驱动时没有选择命令）
                StopCommand();

                var refs = new List<ReferenceHole>(); var specs = new List<HoleSpec>();
                foreach (var g in groups) foreach (var h in g.Holes) { refs.Add(h); specs.Add(g.Spec); }
                // 沉孔/锥沉/倒角孔口：配做用的是孔口**下面**那个孔径，结果框里要说一句，
                // 否则用户看到"参考孔 Φ11、打出来的是 Φ6.6 配 M6"会以为配错了。
                int stepped = 0;
                foreach (var h in refs) if (h.MouthDiameterMm - h.DiameterMm > HoleScan.StepToleranceMm) stepped++;

                // 保持关联：先用天工原生关联（零件间复制 + 孔心挂在复制来的圆边上，特征树里带锁链，
                // 参考零件挪了天工自己带着孔走，见 HoleNativeLink）。0.8.4：挂不上的孔（链接属于别的装配、斜孔、认不准实例……）
                // 改写插件自己的关联记录，照样进同一个多孔特征，参考零件挪了点「同步孔位」挪回去；结果里逐条说清楚为什么、怎么办。
                bool keep = linkCheck.Checked;
                NativeHoleLink.Session native = keep ? new NativeHoleLink.Session(app, assembly) : null;
                HoleLinkFactory.Session plugin = keep ? HoleLinkFactory.Begin(assembly) : null;

                var y = new Tally();
                var drilledLabels = new List<string>();
                object startDoc = null; try { startDoc = app.ActiveDocument; } catch { }
                // 0.7.10 打孔期间关过 CAD 的屏幕刷新（提速）。0.7.12 去掉：和原位编辑的进出叠在一起时，现场打完孔天工窗口不刷新、像卡死。
                // 速度现在不是指标，可靠第一。打完在 finally 里确认回到了顶层装配、刷新一次视图。
                var order = FaceOrder();
                try {
                for (int oi = 0; oi < order.Count; oi++) {
                    int ti = order[oi];
                    // 一张面一张面地报进度、泵一次消息：十几张面要打一两分钟，原来整段不泵消息，Windows 把天工判成"未响应"
                    // （打孔中窗口不能关、列表不能改，见 IBusyTool / RemoveSelected，所以这里泵消息是安全的）
                    run.Progress = (float)oi / Math.Max(1, order.Count);
                    if (targets.Count > 1) { run.Text = "打孔中 " + (oi + 1) + " / " + order.Count + " 个面"; SetStatus("正在打第 " + (oi + 1) + " / " + targets.Count + " 个面：" + targetLabels[ti] + " …", Ui.Accent); try { status.Update(); System.Windows.Forms.Application.DoEvents(); } catch { } }
                    if (closing) break;
                    drilledLabels.Add(targetLabels[ti]);
                    TargetFace t;
                    // 一张面读不出来（面所在的零件被重新加载过……）只跳过这一张，别让整批中断（原来直接抛出去，前面几张面打好的孔连撤销记录都没有）
                    try { t = ResolvedFace(ti); }
                    catch (Exception e) {
                        Log.Write("AutoHoleFaceRead", e);
                        AddProblem(y.Problems, targetLabels[ti] + "：读不到这张面了（" + e.Message + "），没有打。请重新点一次这张面。");
                        y.Failed++;
                        continue;
                    }
                    var partName = t.PartName != null && t.PartName.Length > 0 ? t.PartName : "零件";
                    t.Spots = null;   // 预览时读的"面上已有的孔"可能过时了（期间在 CAD 里删过/撤过孔）：打之前重读一遍
                    var requests = new List<AutoHoleWriter.HoleRequest>();
                    for (int i = 0; i < refs.Count; i++) {
                        var hole = refs[i];
                        V3 centre;
                        try { centre = AutoHoleReader.Intersect(hole, t); }
                        catch (ObliqueHoleException e) { y.Failed++; y.Outcomes.Add(new ResultList.Item(ResultList.Kind.Fail, partName + " Φ" + Num(hole.DiameterMm) + "：" + e.Message).At(hole.Center, hole.DiameterMm)); continue; }
                        catch (Exception e) { y.Skipped++; AddProblem(y.Problems, partName + " Φ" + Num(hole.DiameterMm) + " 无法定位：" + e.Message); continue; }
                        if (!FaceFrameReader.ContainsPoint(t, centre)) { y.Skipped++; continue; }
                        // 这个位置本来就有孔：先在这里挡掉（0.8.5）。原来照样进原位编辑（深一点的子装配 8～12 秒、窗口闪几下），
                        // 进去了才发现一个都不用打 —— 现场日志 19:39 那次就是这样，2 个孔、等了 8 秒、什么也没做
                        double existing;
                        if (t.HasHoleAt(centre, out existing)) {
                            y.Already++;
                            y.Outcomes.Add(new ResultList.Item(ResultList.Kind.Skip, TargetFace.AlreadySay(partName + " Φ" + Num(hole.DiameterMm), existing, specs[i])).At(centre, existing));
                            continue;
                        }
                        // Source 带上目标零件名：同一批里不同零件失败时，界面上的
                        // "失败 N 个"和列出的说明条数才对得上，用户也能直接定位是哪个零件。
                        requests.Add(new AutoHoleWriter.HoleRequest { Spec = specs[i], Centre = centre,
                            Source = partName + " Φ" + Num(hole.DiameterMm), From = hole });
                    }
                    if (requests.Count == 0 && slots.Count > 0) { if (held != null && !InPlaceFor(t)) Release(); DrillSlotsOn(t, partName, y); continue; }
                    if (requests.Count == 0) continue;
                    // 换零件了：上一个零件的原位编辑先退出来
                    if (held != null && !InPlaceFor(t)) Release();
                    try { DrillFace(t, requests, native, plugin, y); }
                    catch (Exception e) {
                        // 这一张面出错：其余面照打；这张面已经打好的孔照样记进撤销（DrillFace 的 finally 里记账）
                        Log.Write("AutoHoleFace", e);
                        foreach (var r in requests) if (!r.Done) {
                            y.Failed++;
                            y.Outcomes.Add(new ResultList.Item(ResultList.Kind.Fail, r.Source + "：没有打 —— " + AutoHoleWriter.Friendly(e)).At(r.Centre, r.Spec == null ? 6 : r.Spec.HoleDiameter));
                        }
                        AddProblem(y.Problems, partName + "（" + t.Label + "）：打孔中断 —— " + AutoHoleWriter.Friendly(e) + "。其余面照常打了；这张面请重新点一次再打。");
                        Release();
                    }
                    if (slots.Count > 0) DrillSlotsOn(t, partName, y);
                }
                } finally { Release(); NativeHoleLink.BackTo(app, startDoc); }

                ShowDrillResult(y, keep, stepped, drilledLabels);
                { var done = DrillDone; if (done != null) { try { done(y.Made); } catch (Exception e) { Log.Write("AutoHoleDrillDone", e); } } }

                // 这批面已经用掉了（面片对象失效）：从列表里拿掉，点下一个面就能接着打。
                // 原来面留在列表里、只是不让再打；点了新面以后旧面也跟着再打一遍，读失效的面片直接把整批打断（「只能选一个面打一次」）
                lastDrilled = drilledLabels.Count == 0 ? "" : string.Join("；", drilledLabels.ToArray());
                targets.Clear(); targetLabels.Clear(); resolved.Clear();
                foreach (var g in groups) g.Selections.Clear();   // 高亮引用也失效了，别再往上加
                slotPicks.Clear();
                scannedPicks.Clear();
                // 打完孔模型变了：面扫描的圆缓存要作废，下一张面重新读一遍。
                foreach (var t in y.Parts) if (t != null && t.Part != null) HoleScanCad.Invalidate(t.Part);
                busy = false; run.Text = "开始打孔"; run.Progress = -1; UseWaitCursor = false;
                drilledOnce = drilledOnce || y.Made > 0;
                if (hadCommand) { try { StartPicking(); } catch (Exception ex) { SetStatus(AutoHoleWriter.Friendly(ex), Ui.Warn); } }
                if (y.Made > 0 && lastLinks != null) ShowLinkReport(null, NotScanned);
                RefreshAll();
                SetStatus(statusAfterDrill, statusAfterDrillColor);
                if (y.Made > 0) shell.ShowToast("已打 " + y.Made + " 个孔" + (y.Failed > 0 ? "，失败 " + y.Failed + " 个" : "") + "　点下一个面可以接着打", y.Failed > 0 ? Ui.Warn : Ui.Ok,
                                                "撤销本次打孔", (s2, e2) => UndoLastBatch());
            } catch (Exception e) {
                Log.Write("AutoHoleDrill", e);
                // 中途失败也可能已经进出过原位编辑：面片缓存作废，下次从用户点的引用重读（不然每次重试都拿同一份旧面数据）
                resolved.Clear();
                busy = false; run.Text = "开始打孔"; run.Progress = -1; UseWaitCursor = false;
                try { if (hadCommand && command == null) StartPicking(); } catch { }
                SetStatus(AutoHoleWriter.Friendly(e), Ui.Bad);
                LastResult = "打孔中断：" + e.Message;
                if (!SuppressDialogs) MessageBox.Show(this, AutoHoleWriter.Friendly(e), "自动打孔", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        string lastDrilled = "";           // 上一批打过的面（预览里提一句）
        public event Action<int> DrillDone; // 一次「开始打孔」打完（参数 = 新打的孔数）。配孔检查「去配孔」据此把那一条标成已配
        string statusAfterDrill = ""; Color statusAfterDrillColor = Ui.Ok;
        readonly HashSet<string> linkWarned = new HashSet<string>(StringComparer.Ordinal);   // 这个窗口里已经弹框提醒过的关联失败原因（同一原因不反复弹）

        // 打一张面：原生关联准备 → 打孔（"对象已断开"换环境补打）→ 孔位链接编组 → 记账
        void DrillFace(TargetFace t, List<AutoHoleWriter.HoleRequest> requests, NativeHoleLink.Session native, HoleLinkFactory.Session plugin, Tally y){
            var partName = t.PartName != null && t.PartName.Length > 0 ? t.PartName : "零件";
            bool reuse = InPlaceFor(t);
            // 用户自己在天工里原位编辑着别的零件（双击进去的）：这时天工只许改那一个零件，别的零件上每个孔都会被拒、只报含糊的 COM 错误
            // （10-22 现场「进不了原位：现在正在原位编辑别的零件」）。先说清楚，这张面不打
            if (!reuse) {
                var editing = SafeActive() as P.PartDocument;
                string ef = ""; try { if (editing != null) ef = editing.FullName; } catch { }
                string tf = ""; try { tf = t.Part.FullName; } catch { }
                if (ef.Length > 0 && !string.Equals(ef, tf, StringComparison.OrdinalIgnoreCase)) {
                    string why = "天工现在正在原位编辑「" + System.IO.Path.GetFileName(ef) + "」，这时只能改它 —— 先在天工里退出原位编辑（或者回到顶层装配）再打「" + partName + "」";
                    y.Failed += requests.Count;
                    foreach (var r in requests) y.Outcomes.Add(new ResultList.Item(ResultList.Kind.Fail, r.Source + "：没有打 —— " + why).At(r.Centre, r.Spec == null ? 6 : r.Spec.HoleDiameter));
                    AddProblem(y.Problems, partName + "：" + why + "。");
                    return;
                }
            }
            // 要原位编辑（保持关联，或零件带链接）时：实例链上的子装配/零件在别的窗口里也开着，就不碰这一面（见 NativeHoleLink.OpenElsewhere）。
            // 同一个零件上一张面已经进了原位：查过了
            string busyDoc = !reuse && (native != null || NativeHoleLink.HasLinks(t.Part)) ? NativeHoleLink.TargetOpenElsewhere(app, assembly, t) : "";
            if (busyDoc.Length > 0) {
                y.Failed += requests.Count;
                foreach (var r in requests)
                    y.Outcomes.Add(new ResultList.Item(ResultList.Kind.Fail, r.Source + "：没有打 ——「" + busyDoc + "」在另一个窗口里也开着，先关掉它").At(r.Centre, r.Spec == null ? 6 : r.Spec.HoleDiameter));
                AddProblem(y.Problems, partName + "：" + NativeHoleLink.OpenElsewhereMessage(busyDoc));
                Log.Write("AutoHoleBusyDoc", partName + "：" + busyDoc + " 在别的窗口里开着，这一面不打");
                return;
            }
            // 插件关联记录先备好（挂上天工关联的孔用不着它，打完只给没挂上的写）。只认参考零件 / 打孔零件这两个实例，不遍历装配
            if (plugin != null) {
                foreach (var r in requests) {
                    string w;
                    try { r.Link = HoleLinkFactory.Make(plugin, r.From, t, out w); } catch (Exception e) { r.Link = null; w = e.Message; }
                    if (r.Link == null) r.PluginWhy = w;
                }
            }
            // 天工原生关联：进原位编辑这个零件、建零件间复制、给每个孔配圆边（挂不上的孔 Anchor 为空、LinkWhy 写着原因）
            NativeHoleLink.InPlace inPlace = null;
            var faceClock = System.Diagnostics.Stopwatch.StartNew();
            if (native != null) {
                SetStatus("正在给「" + partName + "」建立与参考孔的关联…", Ui.Accent);
                try { inPlace = native.Begin(t, requests, y.Undo); }
                catch (Exception e) { Log.Write("AutoHoleNativeBegin", e); foreach (var r in requests) if (r.Anchor == null && r.LinkWhy.Length == 0) r.LinkWhy = "准备天工关联时出错（" + AutoHoleWriter.Friendly(e) + "）"; }
            }
            // 带零件间链接的零件（0.7.11）：在原位编辑里打，和天工自己的孔命令一样（双击零件再打孔）。
            // 用户现场：这类零件在装配里直接改，每个孔两侧都是 0x80010108「系统无法找到指定的资源」，断开链接后才打得上；
            // 现场日志里打不上的那几批全都是在装配上下文里打的（原生关联没挂上 → 退出原位 → 在装配里打）。
            if (inPlace == null && (reuse || NativeHoleLink.HasLinks(t.Part))) {
                string ewhy;
                inPlace = NativeHoleLink.EnterTarget(app, assembly, t, out ewhy);
                if (inPlace == null) Log.Write("AutoHoleLinkedEnter", partName + "：" + ewhy);
            }
            Hold(inPlace, t);
            // 进出过原位编辑（原生关联没挂上退出来、或者进原位失败）以后，零件可能被天工重新加载，手里的面/零件对象全断开了：
            // 按用户点的引用重读一次面（10-23 现场：这一步没做，接着就报"目标零件没有可用的实体模型"）
            if (!TargetFace.PartAlive(t.Part)) {
                try {
                    var fresh = AutoHoleReader.ReadTarget(t.Pick);
                    fresh.LinkNote = t.LinkNote; fresh.Instances = t.Instances;
                    Log.Write("AutoHoleReread", partName + "：零件对象断开了，按点选引用重读了面");
                    t = fresh;
                } catch (Exception e) { Log.Write("AutoHoleReread", e.Message); throw new InvalidOperationException(TargetFace.DeadPartMessage(partName)); }
            }
            // 打孔在主进程内完成。实测（2026-09-27）：进程内、跨进程都能写模型；
            // 真正会失败的是"往已经有孔的位置再打"——那由 Friendly() 和预检给出人话提示。
            y.Parts.Add(t);
            long prepMs = faceClock.ElapsedMilliseconds, drillMs = 0;
            DrillResult res = null;
            try {
                res = AutoHoleWriter.DrillRequests(t, requests);
                // 有孔因为"对象已断开"没打上：换个编辑环境（装配里 ↔ 原位编辑里）把没打上的再打一次
                if (res.Disconnected > 0) {
                    var left = requests.FindAll(r => !r.Done);
                    bool wasIn = InPlaceFor(t);
                    string where = wasIn ? "装配里" : "原位编辑里";
                    Log.Write("AutoHoleRetryContext", partName + "：" + res.Disconnected + " 个孔 CAD 报对象已断开，换到" + where + "再打 " + left.Count + " 个");
                    NativeHoleLink.InPlace retryIn = null;
                    bool switched = true;
                    if (wasIn) { EndFace(native, inPlace, requests, res, y); inPlace = null; Release(); }
                    else {
                        string ewhy;
                        retryIn = NativeHoleLink.EnterTarget(app, assembly, t, out ewhy);
                        if (retryIn == null) { switched = false; Log.Write("AutoHoleRetryContext", "进不了原位：" + ewhy); }
                    }
                    if (switched) {
                        foreach (var r in left) { if (r.Anchor != null && r.LinkWhy.Length == 0) r.LinkWhy = "第一次没打上（CAD 报对象已断开），换环境补打的孔挂不了天工关联"; r.Anchor = null; }
                        try {
                            var again = AutoHoleWriter.DrillRequests(t, left);
                            AutoHoleWriter.MergeRetry(res, again);
                            if (again.Created > 0) res.Notes.Add(again.Created + " 个孔第一次没打上（CAD 报对象已断开），已换到" + where + "补打上。");
                        } finally { if (retryIn != null) NativeHoleLink.Leave(app, retryIn); }
                    }
                }
                drillMs = faceClock.ElapsedMilliseconds - prepMs;
            }
            finally {
                if (inPlace != null) EndFace(native, inPlace, requests, res, y);
                if (res != null) Account(y, res, requests, partName, native != null);
            }
            Log.Write("AutoHoleTiming", partName + " " + requests.Count + " 个孔：关联准备 " + prepMs + "ms，打孔 " + drillMs
                      + "ms，编组 " + (faceClock.ElapsedMilliseconds - prepMs - drillMs) + "ms，合计 " + faceClock.ElapsedMilliseconds + "ms");
        }

        // 这张面打完：原生关联建的（删掉没用上的零件间复制、把用上的和孔编成孔位链接组）走 Session.End。
        // 原位编辑的退出交给 held（同一个零件的下一张面接着用）
        void EndFace(NativeHoleLink.Session native, NativeHoleLink.InPlace inPlace, IList<AutoHoleWriter.HoleRequest> requests, DrillResult res, Tally y){
            try { if (native != null) native.End(inPlace, requests, res, res != null ? res.Undo : y.Undo); }
            catch (Exception e) { Log.Write("AutoHoleNativeEnd", e); }
        }

        void Account(Tally y, DrillResult res, List<AutoHoleWriter.HoleRequest> requests, string partName, bool keep){
            y.Made += res.Created; y.Failed += res.Failures.Count; y.Already += res.AlreadyOk; y.Linked += res.Linked;
            y.NativeLinked += res.NativeLinked; y.NativeMissed += res.NativeMissed;
            foreach (var note in res.Notes) AddProblem(y.Problems, note);
            if (res.Method.Length > 0 && !y.Methods.Contains(res.Method)) y.Methods.Add(res.Method);
            if (res.Audit.Length > 0 && !y.Audits.Contains(res.Audit)) y.Audits.Add(res.Audit);
            foreach (var f in res.Features) y.Features.Add(partName + "：" + f);
            y.Outcomes.AddRange(res.Outcomes);
            y.Undo.Absorb(res.Undo);
            if (!keep) return;
            // 关联：每个新打的孔要么挂上了天工关联，要么记下原因（按零件 + 原因归拢）
            foreach (var r in requests) {
                if (!r.Created || r.NativeLinked || r.LinkNotNeeded) continue;
                string why = r.LinkWhy.Length > 0 ? r.LinkWhy : "没能挂上天工关联（原因见 panel.log）";
                if (!r.PluginLinked && r.PluginWhy.Length > 0) why += "；插件关联也写不了（" + r.PluginWhy + "）";
                string key = partName + "\u0001" + why;
                if (!y.LinkCount.ContainsKey(key)) { y.LinkCount[key] = 0; y.LinkPlugin[key] = 0; y.LinkReasons.Add(key); }
                y.LinkCount[key]++;
                if (r.PluginLinked) { y.LinkPlugin[key]++; y.PluginLinked++; } else y.Unlinked++;
            }
        }

        // 结果：列表 + 给脚本读的纯文本 + 状态栏；关联没挂上的单独醒目地说（0.8.4，用户：「对链接失败的打孔提醒更加明确一点」）
        void ShowDrillResult(Tally y, bool keep, int stepped, List<string> faces){
            if (y.NativeMissed > 0 && y.LinkReasons.Count == 0) AddProblem(y.Problems, y.NativeMissed + " 个孔没能挂上天工原生关联（孔照常打好了）");
            var linkLines = new List<string>();
            var linkKeys = new List<string>();
            foreach (var key in y.LinkReasons) {
                int k = key.IndexOf('\u0001');
                string part = key.Substring(0, k), why = key.Substring(k + 1);
                int n = y.LinkCount[key], p = y.LinkPlugin[key];
                string then = p == n ? "已改用插件关联：参考零件挪动后点「同步孔位」把孔挪回去"
                            : p > 0 ? "其中 " + p + " 个改用插件关联（参考零件挪动后点「同步孔位」），另 " + (n - p) + " 个不会跟着参考孔走"
                            : "这些孔不会跟着参考孔走";
                linkLines.Add(part + "：" + n + " 个孔没挂上天工关联 —— " + why + "。" + then + "。");
                linkKeys.Add(part + "|" + why);
            }

            string msg = "共打孔 " + y.Made + " 个";
            if (y.Already > 0) msg += "，" + y.Already + " 个位置本来就有孔（见下方说明）";
            if (y.Skipped > 0) msg += "，跳过 " + y.Skipped + " 个（投影不落在所选面上）";
            if (y.Failed > 0) msg += "，失败 " + y.Failed + " 个";
            if (stepped > 0) msg += "\r\n其中 " + stepped + " 个参考孔是沉孔/锥沉/倒角孔口，按孔口下面的孔径配做";
            msg += "\r\n打孔面 " + faces.Count + " 个";
            if (y.Methods.Count > 0) msg += "\r\n生成方式：" + string.Join("／", y.Methods.ToArray());
            if (y.Features.Count > 0) msg += "\r\n\r\n特征树里：\r\n· " + string.Join("\r\n· ", y.Features.ToArray());
            if (y.NativeLinked > 0)
                msg += "\r\n\r\n天工原生关联 " + y.NativeLinked + " 个孔：特征树里的「孔位链接-…」带锁链，和孔编在同一个「关联孔 …」组里（删组 = 孔和链接一起删），"
                     + "参考零件挪动后天工自己把孔带过去（没跟上时点「同步孔位」或天工里「更新所有链接」）。";
            if (y.Linked > 0)
                msg += "\r\n\r\n插件关联 " + y.Linked + " 个孔：参考零件挪动后，点窗口底部的「同步孔位」，孔会重新对准。";
            if (linkLines.Count > 0) msg += "\r\n\r\n⚠ 关联没挂上：\r\n· " + string.Join("\r\n· ", linkLines.ToArray());
            if (y.Audits.Count > 0) msg += "\r\n\r\n自检：" + string.Join("；", y.Audits.ToArray());
            msg += "\r\n\r\n请保存装配。";
            // LastResult：给真机脚本/测试读的纯文本，内容和窗口里的列表一致（失败、本来就有孔也在"说明"里）
            var legacy = new List<string>();
            foreach (var o in y.Outcomes) if (o.Kind == ResultList.Kind.Fail || o.Kind == ResultList.Kind.Skip) legacy.Add(o.Text);
            legacy.AddRange(y.Problems);
            if (legacy.Count > 0) msg += "\r\n\r\n说明：\r\n· " + string.Join("\r\n· ", legacy.ToArray());
            LastResult = msg;

            // 结果进窗口里的列表（不再弹框）：失败/关联没挂上/跳过/打好的孔逐条列出，点一条跳过去。
            var items = new List<ResultList.Item>(y.Outcomes);
            foreach (var l in linkLines) items.Add(new ResultList.Item(ResultList.Kind.Warn, l));
            foreach (var f in y.Features) items.Add(new ResultList.Item(ResultList.Kind.Info, "特征树：" + f));
            foreach (var au in y.Audits) items.Add(new ResultList.Item(ResultList.Kind.Fail, "自检：" + au));
            foreach (var pr in y.Problems) items.Add(new ResultList.Item(ResultList.Kind.Info, pr));
            if (y.Skipped > 0) items.Add(new ResultList.Item(ResultList.Kind.Info, y.Skipped + " 个参考孔的投影不落在所选面上，已跳过（参考孔只对着其中一部分面时是正常的）。"));
            if (stepped > 0) items.Add(new ResultList.Item(ResultList.Kind.Info, stepped + " 个参考孔是沉孔/锥沉/倒角孔口，按孔口下面的孔径配做。"));
            if (y.NativeLinked > 0) items.Add(new ResultList.Item(ResultList.Kind.Info, "天工原生关联 " + y.NativeLinked + " 个孔：孔和「孔位链接-…」在同一个「关联孔 …」组里，删组 = 一起删；参考零件挪动后天工自己把孔带过去。"));
            if (y.Linked > 0) items.Add(new ResultList.Item(ResultList.Kind.Info, "插件关联 " + y.Linked + " 个孔：参考零件挪动后点底部「同步孔位」。"));
            if (y.Made > 0) items.Add(new ResultList.Item(ResultList.Kind.Info, "记得保存装配。打错了点「撤销本次打孔」。"));
            string head = "打孔 " + y.Made + " 个";
            if (keep && y.Made > 0) {
                head += " · 天工关联 " + y.NativeLinked;
                if (y.PluginLinked > 0) head += " · 插件关联 " + y.PluginLinked;
                if (y.Unlinked > 0) head += " · 未关联 " + y.Unlinked;
            }
            head += (y.Already > 0 ? " · 本来就有孔 " + y.Already : "") + (y.Skipped > 0 ? " · 跳过 " + y.Skipped : "") + (y.Failed > 0 ? " · 失败 " + y.Failed : "");
            bool linkShort = linkLines.Count > 0;
            results.SetResult(head, y.Failed > 0 || y.Audits.Count > 0 ? Ui.Bad : linkShort ? Ui.Warn : (y.Made > 0 ? Ui.Ok : Ui.Warn), items);
            ShowResults(true);
            lastUndo = y.Undo;
            undoButton.Enabled = !y.Undo.IsEmpty;
            lastCreated = y.Undo.Created();
            editButton.Enabled = lastCreated.Count > 0;

            statusAfterDrill = "完成：打孔 " + y.Made + " 个" + (y.Skipped > 0 ? "，跳过 " + y.Skipped : "") + (y.Failed > 0 ? "，失败 " + y.Failed : "")
                             + (linkShort ? "，其中 " + (y.PluginLinked + y.Unlinked) + " 个没挂上天工关联（原因见结果列表）" : "")
                             + "　—　点下一个面接着打（参考孔不用重选）。";
            statusAfterDrillColor = y.Failed > 0 || linkShort ? Ui.Warn : Ui.Ok;

            // 关联没挂上：弹一次框说清楚（同一个零件 + 原因在这个窗口里只弹一次，免得一个零件打几批就被弹几次）
            var fresh = new List<string>();
            for (int i = 0; i < linkLines.Count; i++) if (linkWarned.Add(linkKeys[i])) fresh.Add(linkLines[i]);
            if (fresh.Count > 0 && !SuppressDialogs)
                MessageBox.Show(this, "这次打的孔里有 " + (y.PluginLinked + y.Unlinked) + " 个没挂上天工关联（孔都打好了）：\r\n\r\n· " + string.Join("\r\n\r\n· ", fresh.ToArray()),
                                "自动打孔 · 关联没挂上", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        // 参考孔里第一个已经失效的零件/面：返回给用户看的说明，都活着返回 ""
        string DeadReference(){
            foreach (var g in groups) foreach (var h in g.Holes) {
                if (h == null) continue;
                string part = h.Where != null && h.Where.Length > 0 ? h.Where : "参考孔所在的零件";
                try { if (h.Owner != null) { var n = (string)((dynamic)h.Owner).FullName; } if (h.SourceFace != null) { var id = ((dynamic)h.SourceFace).ID; } }
                catch (Exception e) {
                    Log.Write("AutoHoleDeadRef", part + "：" + e.Message);
                    return "参考孔（" + part + "）所在的文档被天工重新加载过（比如关掉过它的窗口），原来点的参考孔已经失效。请点「全部重选」，重新点参考孔和打孔面。";
                }
            }
            return "";
        }

        // 改配做规则：保存后，已经点好的参考孔按新规则重推规格（用户改过的深度/倒角/螺纹深度保留）
        void EditRules(){
            if (busy) return;
            using (var f = new HoleRulesForm(HoleRuleBook.Current)) {
                if (f.ShowDialog(this) != DialogResult.OK || f.Result == null) return;
            }
            int n = 0;
            foreach (var g in groups) {
                var fresh = HoleMatcher.Match(g.DiameterMm).Target;
                if (g.Spec != null) CopyUserEdits(g.Spec, fresh);
                g.Spec = fresh; n++;
            }
            RefreshAll();
            SetStatus("配做规则已改成「" + HoleRuleBook.NameOf(HoleRuleBook.Current) + "」" + (n > 0 ? "，已按新规则重推 " + n + " 组参考孔的规格。" : "。"), Ui.Ok);
        }

        // 结果卡片和预览卡片占同一个位置：打完显示结果，「收起」回到预览
        void ShowResults(bool show){
            if (resultCard == null) return;
            resultCard.Visible = show && results.Count > 0;
            var stack = resultCard.Parent as VerticalStack;
            if (stack != null) stack.Relayout();
            if (resultCard.Visible) shell.Go(PagePreview);
        }

        // 撤销上一次「开始打孔」：删掉那一批孔，编组和改名恢复原样（HoleUndo）。
        void UndoLastBatch(){
            if (busy || closing || lastUndo == null || lastUndo.IsEmpty) return;
            if (!SuppressDialogs && MessageBox.Show(this,
                    "删掉上一次打的 " + lastUndo.HoleCount + " 个孔？\r\n（编组、改名会一并恢复，挂关联建的「孔位链接-…」也一起删掉；之后你在 CAD 里对这些孔做的修改也会一起没掉）",
                    "撤销本次打孔", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            busy = true; UseWaitCursor = true; undoButton.Enabled = false;
            bool hadCmd = command != null;
            StopCommand();
            try {
                var outcome = lastUndo.Run();
                lastUndo = null;
                lastCreated = null; editButton.Enabled = false;
                foreach (var part in outcome.Parts) { try { HoleScanCad.Invalidate(part); } catch { } }
                resolved.Clear();
                var items = new List<ResultList.Item>();
                foreach (var n in outcome.Notes) items.Add(new ResultList.Item(ResultList.Kind.Info, n));
                results.SetResult(outcome.Summary, outcome.Failed > 0 ? Ui.Warn : Ui.Ok, items);
                ShowResults(true);
                LastResult = outcome.Summary + (outcome.Notes.Count > 0 ? "\r\n· " + string.Join("\r\n· ", outcome.Notes.ToArray()) : "");
                SetStatus(outcome.Summary + "　参考孔还在，重新点一个打孔面就能再打。", outcome.Failed > 0 ? Ui.Warn : Ui.Ok);
                if (lastLinks != null) ShowLinkReport(null, NotScanned);
            } catch (Exception e) {
                Log.Write("AutoHoleUndo", e);
                SetStatus("撤销没有完成：" + AutoHoleWriter.Friendly(e), Ui.Bad);
                LastResult = "撤销中断：" + e.Message;
            } finally {
                busy = false; UseWaitCursor = false;
                if (hadCmd) { try { StartPicking(); } catch (Exception ex) { SetStatus(AutoHoleWriter.Friendly(ex), Ui.Warn); } }
                RefreshPreview();
            }
        }
        void OpenEdit(){
            if (busy || closing || lastCreated == null || lastCreated.Count == 0) return;
            handingOver = command != null;
            try { AutoHoleModule.OpenEdit(app, assembly, lastCreated); }
            catch (Exception e) { handingOver = false; Log.Write("AutoHoleOpenEdit", e); SetStatus("打不开改孔型窗口：" + e.Message, Ui.Bad); }
        }
        public List<KeyValuePair<SolidEdgePart.PartDocument, string>> LastCreatedForTest { get { return lastCreated; } }
        public void UndoForTest(){ UndoLastBatch(); }
        public bool CanUndo { get { return lastUndo != null && !lastUndo.IsEmpty; } }
        public IList<ResultList.Item> ResultItemsForTest { get { return results.Items; } }

        // Esc：有选择时要再按一次（见 QuickClose）
        readonly QuickClose quickClose = new QuickClose();
        bool ConfirmQuickClose(string how){
            if (busy) return false;
            string lost = groups.Count == 0 && targets.Count == 0 ? "" : "已选的 " + groups.Sum(g => g.Count) + " 个参考孔、" + targets.Count + " 个打孔面";
            return quickClose.Confirm(how, lost, m => SetStatus(m, Ui.Warn));
        }

        void StopCommand(){
            if (highlights != null) { try { highlights.Delete(); } catch { } highlights = null; }
            if (mouseConnection != null) { mouseConnection.Dispose(); mouseConnection = null; }
            if (commandConnection != null) { commandConnection.Dispose(); commandConnection = null; }
            var c = command; command = null; mouse = null;
            if (c != null) try { c.Done = true; } catch { }
        }

        internal static readonly CarriedUndo Carried = new CarriedUndo();
        void Cleanup(){
            if (closing) return; closing = true;
            try { Carried.Keep(assembly, lastUndo, lastCreated); } catch { }
            // 只摘自己挂的：「去配孔」会另开一个自动打孔窗口，后开的那个关掉时不能把前一个的进度回调一起摘掉
            if (AutoHoleWriter.Progress == progress) AutoHoleWriter.Progress = null;
            StopCommand();
            groups.Clear(); targets.Clear(); targetLabels.Clear(); resolved.Clear(); scannedPicks.Clear();
            lastScanNote = "";
            try { tips.Dispose(); } catch { }
        }

        public new void MouseClick(short b, short s, double x, double y, double z, object w, int k, object g){
            if (b == 1) { if (g == null) { SetStatus("这里没有捕捉到对象。请点到孔口的圆边，或零件平面上；零件没激活（重开大装配后常见）时，先在天工里右键它选「激活」。", Ui.Warn); return; } AcceptPick(g, (s & 2) != 0); }
            // 右键交给天工弹 QuickPick 选遮挡面（0.8.5）。原来右键 = 关窗口；关窗口用 Esc 或「关闭」按钮
        }
        public new void MouseDown(short b, short s, double x, double y, double z, object w, int k, object g){ }
        public new void MouseUp(short b, short s, double x, double y, double z, object w, int k, object g){ }
        public new void MouseMove(short b, short s, double x, double y, double z, object w, int k, object g){
            if (closing || busy) return;
            if (g == null) SetStatus("可以点：孔口的圆形边线、孔的内壁圆柱面、带孔的面（自动认孔）、要打孔的平面。被挡住的面：右键（或停一下）从列表里选。", Ui.Muted);
        }
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
            try { StartPicking(); SetStatus("接着选。", Ui.Muted); } catch (Exception ex) { SetStatus(AutoHoleWriter.Friendly(ex), Ui.Warn); }
        }
        public void Idle(int n, out bool more){ more = false; }
        void F.ISECommandEvents.Activate(){ }
        void F.ISECommandEvents.Deactivate(){ }
        public new void KeyDown(ref short key, short shift){ if (key == 27 && ConfirmQuickClose("Esc")) Close(); }
        public new void KeyPress(ref short key){ }
        public new void KeyUp(ref short key, short shift){ }
    }
}
