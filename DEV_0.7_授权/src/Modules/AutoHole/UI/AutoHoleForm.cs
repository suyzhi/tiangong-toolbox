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
    public sealed partial class AutoHoleForm : Form, IBusyTool, F.ISEMouseEvents, F.ISECommandEvents {
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

        // 点平面是什么意思由当前页面决定（0.9.1，用户要的；原来的「点平面时自动识别面上的孔」开关去掉了）：
        //   ① 参考孔页：点平面 = 把面上的孔全认成参考孔（一个面上几十个孔不用一个一个点圆边）；
        //   ② 打孔面页及以后：点平面 = 打孔面。按住 Ctrl 点 = 反过来。点了以后不再自动翻页。
        string lastScanNote = "";   // 上一次面扫描的结论，显示在预览的说明行里

        readonly SmoothListBox refList = new SmoothListBox();
        readonly SmoothListBox faceList = new SmoothListBox();
        readonly Chip chip = new Chip();
        readonly Label previewLine = new Label();
        readonly Label previewSub = new Label();
        readonly Label status = new Label();
        readonly FlatButton run = (FlatButton)Ui.Primary("开始打孔", 136, 44);
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
        ContextMenuStrip moreMenu;
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
        readonly CadKeyShield keyShield;

        public AutoHoleForm(F.Application application, A.AssemblyDocument document){
            app = application; assembly = document;
            Ui.Shell(this, "自动打孔", 1160, 790, 1000, 680);
            keyShield = new CadKeyShield(this, () => busy);
            WindowStateStore.Bind(this, "autohole");

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
            // 上次关窗口前打的那批还没撤销：校验零件 COM 引用仍存活再恢复，避免持死引用启用无效按钮
            HoleUndo carried; List<KeyValuePair<SolidEdgePart.PartDocument, string>> carriedCreated;
            if (Carried.Take(assembly, out carried, out carriedCreated)) {
                var aliveCreated = carriedCreated == null ? null : carriedCreated.Where(kv => TargetFace.PartAlive(kv.Key)).ToList();
                bool valid = carriedCreated == null || carriedCreated.Count == 0 || (aliveCreated != null && aliveCreated.Count > 0);
                if (valid) {
                    lastUndo = carried; lastCreated = aliveCreated;
                    undoButton.Enabled = true; editButton.Enabled = lastCreated != null && lastCreated.Count > 0;
                    SetStatus("上次关窗口前打的 " + carried.HoleCount + " 个孔还能撤销（「撤销本次打孔」）。点孔口圆边、孔内壁，或点一个带孔的面开始。", Ui.Muted);
                }
            }

            FormClosed += (s, e) => Cleanup();
            // 关联孔**不再自动查**（0.7.8）：查一遍要取装配里每个实例的 OccurrenceDocument，
            // 天工会把轻量化/未激活的零件逐个真正加载进来 —— 用户看到的就是「点了命令卡很久、左下角在扫文件」。
            // 现在只在用户点「检查」或「同步孔位」时才查。
            // StartPicking 由宿主 ToolContext.Show 的启动回调调用，这里不能重复调，
            // 否则第二次 command.Start() 会触发 Terminate() 把窗口关掉。
        }

        // 「保持与参考孔关联」记住上次的选择（0.8.4）：原来每次打开窗口都回到默认，
        // 不要关联的人每次都得先去勾掉。文件 autohole.tsv：每行「名字<TAB>1/0」。
        // 老版本存的 scanFace（「点平面时自动识别」开关）0.9.1 起不读了：点平面的意思改由页面决定
        const string ChoicesFile = "autohole.tsv";
        void LoadChoices(){
#if TG_DEV_BUILD
            // 真机测试脚本（放行授权闸的那些）用默认选择，用户自己的设置不能让测试的第一步就走岔（0.8.5 整批 E2E 因此误报）
            if (TianGongCadSuite.Licensing.LicenseTestHooks.GateOverride >= 0) return;
#endif
            try {
                foreach (var r in SettingsFile.Read(SettingsFile.PathOf(ChoicesFile))) {
                    if (r.Length < 2) continue;
                    if (r[0] == "keepLink") linkCheck.Checked = r[1] == "1";
                }
            } catch (Exception e) { Log.Write("AutoHoleChoicesLoad", e.Message); }
        }
        void SaveChoices(){
            try {
                SettingsFile.Write(SettingsFile.PathOf(ChoicesFile), new List<string[]>{
                    new[]{ "keepLink", linkCheck.Checked ? "1" : "0" } },
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
            if (keyData == (Keys.Control | Keys.Enter)) { EditCommit.All(this); RunDrill(); return true; }
            if (keyData == Keys.Delete && (ActiveControl == refList || ActiveControl == faceList)) {
                if (ActiveControl == refList) RemoveSelected(); else RemoveSelectedFace();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
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
        // 老测试脚本的开关（0.9.1 起开关没了）：true = 翻到参考孔页（点平面认孔），false = 翻到打孔面页（点平面当打孔面）
        public bool ScanFaceOnPick { get { return shell.Current == PageRefs; } set { shell.Go(value ? PageRefs : PageFaces, false); } }
        // 预先在天工里选好的对象走的那条老规则（有孔认孔、没孔当打孔面），测试用
        public void AcceptPreselectedForTest(object selected){ AcceptPick(selected, PlanePick.Auto); }
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
            if (slots.Count > 0) sb.Append("腰孔 ").Append(slots.Count).Append(" 条 ");
            sb.Append("] 打孔面 ").Append(targets.Count).Append(" 个 [");
            foreach (var s in targetLabels) sb.Append(s).Append(" | ");
            sb.Append("] 可打孔=").Append(run.Enabled).Append(" 徽标=").Append(chip.Text);
            sb.Append(" 预览=").Append(previewLine.Text).Append(" / ").Append(previewSub.Text);
            sb.Append(" 关联=关联孔位：").Append(linkLabel.Text);
            sb.Append(" 状态=").Append(status.Text);
            return sb.ToString();
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
            if (moreMenu != null) { try { moreMenu.Dispose(); } catch { } moreMenu = null; }
            try { tips.Dispose(); } catch { }
        }

        public new void MouseClick(short b, short s, double x, double y, double z, object w, int k, object g){
            if (closing || busy || IsDisposed) return;
            if (b == 1) { if (g == null) { SetStatus("这里没有捕捉到对象。请点到孔口的圆边，或零件平面上；零件没激活（重开大装配后常见）时，先在天工里右键它选「激活」。", Ui.Warn); return; } AcceptPick(g, (s & 2) != 0); }
            // 右键交给天工弹 QuickPick 选遮挡面（0.8.5）。原来右键 = 关窗口；关窗口用 Esc 或「关闭」按钮
        }
        public new void MouseDown(short b, short s, double x, double y, double z, object w, int k, object g){ }
        public new void MouseUp(short b, short s, double x, double y, double z, object w, int k, object g){ }
        public new void MouseMove(short b, short s, double x, double y, double z, object w, int k, object g){
            if (closing || busy || IsDisposed) return;
            if (g == null) SetStatus("可以点：孔口的圆形边线、孔的内壁圆柱面、带孔的面（自动认孔）、要打孔的平面。被挡住的面：右键（或停一下）从列表里选。", Ui.Muted);
        }
        public void MouseDblClick(short b, short s, double x, double y, double z, object w, int k, object g){ }
        public void MouseDrag(short b, short s, double x, double y, double z, object w, short ds, int k, object g){ }
        // 别的命令接管了鼠标：一般就关窗口（和原来一样）。但如果是我们自己点「改孔型…」开的窗口接管的，
        // 这个窗口说好了要留着（参考孔、规格都还在）—— 原来照样被关掉。现在只是暂停选择，用户回到这个窗口时自动接着选
        bool handingOver, paused;
        bool HasPicks { get { return groups.Count > 0 || slots.Count > 0 || targets.Count > 0 || (lastUndo != null && !lastUndo.IsEmpty); } }
        // 句柄未创建或窗体已释放时不能直接 BeginInvoke，排队后的回调也必须复核 closing / IsDisposed
        void SafeBeginInvoke(Action action){
            if (closing || IsDisposed || !IsHandleCreated) return;
            try {
                BeginInvoke((Action)(() => {
                    if (closing || IsDisposed) return;
                    action();
                }));
            } catch { }
        }
        // 功能区上又点了本命令（0.10.3）：已经选了东西就把这个窗口提到前面、接着选，不再连同选好的东西关掉重开
        // （窗口暂停选择以后，用户很自然地再点一下功能区按钮想"接着来"）。见 ToolContext.Show 的 reuse
        internal bool ReuseFor(object doc){
            if (closing || IsDisposed || !ReferenceEquals(assembly, doc) || !(HasPicks)) return false;
            SafeBeginInvoke(() => OnActivated(EventArgs.Empty));
            return true;
        }
        public void Terminate(){
            if (closing || busy || IsDisposed) return;
            if (handingOver) { handingOver = false; paused = true; SafeBeginInvoke(() => { StopCommand(); SetStatus("选择已暂停（改孔型窗口在用鼠标）。回到这个窗口点一下就接着选。", Ui.Muted); }); return; }
            // 用户在天工里点了别的命令（选择工具、测量、隐藏零件……）：原来窗口直接关掉，选好的参考孔、打孔面全丢，还得从头点。
            // 已经选了东西就只暂停选择、窗口留着，回到窗口点一下接着选；什么都没选才照旧关掉
            if (HasPicks) { paused = true; SafeBeginInvoke(() => { StopCommand(); SetStatus("天工里开始了别的命令，选择已暂停（已选的都还在）。回到这个窗口点一下就接着选。", Ui.Muted); }); return; }
            SafeBeginInvoke(() => Close());
        }
        protected override void OnActivated(EventArgs e){
            base.OnActivated(e);
            if (!paused || closing || busy || command != null) return;
            if (!AssemblyAlive()) return; paused = false;
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
