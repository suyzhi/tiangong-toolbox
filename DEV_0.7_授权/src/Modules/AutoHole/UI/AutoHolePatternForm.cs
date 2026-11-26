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
    public sealed partial class AutoHolePatternForm : Form, IBusyTool, F.ISEMouseEvents, F.ISECommandEvents {
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

        readonly F.Application app;
        readonly A.AssemblyDocument assembly;
        // 单独打开的零件里用（0.8.5）：原来只认装配，给一个零件排一排孔得先建个装配把它放进去。这时 assembly 为 null
        readonly P.PartDocument partDoc;
        object HostDoc { get { return (object)assembly ?? partDoc; } }
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
        readonly FlatButton run = (FlatButton)Ui.Primary("开始打孔", 220, 44);
        readonly ToolTip tips = new ToolTip { AutoPopDelay = 15000, InitialDelay = 400 };
        ContextMenuStrip moreMenu;

        readonly FieldGrid layoutGrid = new FieldGrid();
        readonly FieldGrid specGrid = new FieldGrid();
        FieldGrid.Row countRow, pitchRow, edgeRow, lateralRow, angleRow, threadRow, slotRow;
        VerticalStack rootStack;
        Panel previewCanvas;
        readonly CadKeyShield keyShield;

        public AutoHolePatternForm(F.Application application, P.PartDocument part) : this(application, (A.AssemblyDocument)null, part){ }
        public AutoHolePatternForm(F.Application application, A.AssemblyDocument document) : this(application, document, null){ }
        AutoHolePatternForm(F.Application application, A.AssemblyDocument document, P.PartDocument part){
            app = application; assembly = document; partDoc = part;
            Ui.Shell(this, "批量排孔", 1080, 760, 940, 640);
            keyShield = new CadKeyShield(this, () => busy);
            WindowStateStore.Bind(this, "pattern");
            BuildLayout();
            WireEvents();
            loading = true;
            try { sizeBox.SelectedItem = "M6"; } finally { loading = false; }
            ApplySize();
            FormClosed += (s, e) => Cleanup();
            HoleUndo carried; List<KeyValuePair<P.PartDocument, string>> carriedCreated;
            if (Carried.Take(HostDoc, out carried, out carriedCreated)) {
                var aliveCreated = carriedCreated == null ? null : carriedCreated.Where(kv => TargetFace.PartAlive(kv.Key)).ToList();
                bool valid = carriedCreated == null || carriedCreated.Count == 0 || (aliveCreated != null && aliveCreated.Count > 0);
                if (valid) {
                    lastUndo = carried; lastCreated = aliveCreated;
                    undoButton.Enabled = true; editButton.Enabled = lastCreated != null && lastCreated.Count > 0;
                    SetStatus("上次关窗口前排的 " + carried.HoleCount + " 个孔还能撤销（「撤销本次打孔」）。点要排孔的那个平面。", Ui.Muted);
                }
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData){
            if (keyData == Keys.Escape) { if (ConfirmQuickClose("Esc")) Close(); return true; }
            if (keyData == (Keys.Control | Keys.Enter)) { EditCommit.All(this); Run(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ---- 测试 / 截图用的缝 ----
        public bool SuppressDialogs;
        public string LastResult = "";
        public void DrillForTest(){ Run(); }

        public List<KeyValuePair<P.PartDocument, string>> LastCreatedForTest { get { return lastCreated; } }
        public bool AsPattern { get { return asPattern.Checked; } set { asPattern.Checked = value; } }
        public bool SlotForTest { get { return slotCheck.Checked; } set { slotCheck.Checked = value; } }
        public void UndoForTest(){ UndoLastBatch(); }
        public string PreviewForTest { get { return previewText.Text; } }
        public void GoPageForTest(int i){ shell.Go(i, false); }
        // 排布：方式（0 等分 / 1 定距 / 2 圆周）、孔数、边距、横向（0 居中 / 1 离方向边 X mm）
        public void SetLayoutForTest(int mode, int holes, double edgeMm, int lat, double lateralMm){
            modeSeg.SelectedIndex = mode; count.Value = holes; edge.Value = (decimal)edgeMm;
            latMode.SelectedIndex = lat; lateral.Value = (decimal)lateralMm; RefreshPreview();
        }
        public bool CanUndo { get { return lastUndo != null && !lastUndo.IsEmpty; } }

        void StopCommand(){
            if (highlights != null) { try { highlights.Delete(); } catch { } highlights = null; }
            if (mouseConnection != null) { mouseConnection.Dispose(); mouseConnection = null; }
            if (commandConnection != null) { commandConnection.Dispose(); commandConnection = null; }
            var c = command; command = null; mouse = null;
            if (c != null) try { c.Done = true; } catch { }
        }
        internal static readonly CarriedUndo Carried = new CarriedUndo();
        void Cleanup(){ if (closing) return; closing = true; try { Carried.Keep(HostDoc, lastUndo, lastCreated); } catch { } StopCommand(); if (moreMenu != null) { try { moreMenu.Dispose(); } catch { } moreMenu = null; } try { tips.Dispose(); } catch { } }

        public new void MouseClick(short b, short s, double x, double y, double z, object w, int k, object g){
            if (closing || busy || IsDisposed) return;
            if (b == 1) { if (g == null) { SetStatus("这里没有捕捉到对象。零件没激活（重开大装配后常见）时，先在天工里右键它选「激活」。", Ui.Warn); return; } AcceptPick(g); }
            // 右键交给天工弹 QuickPick 选遮挡面（0.8.5）。原来右键 = 关窗口；关窗口用 Esc 或「关闭」按钮
        }
        public new void MouseDown(short b, short s, double x, double y, double z, object w, int k, object g){ }
        public new void MouseUp(short b, short s, double x, double y, double z, object w, int k, object g){ }
        public new void MouseMove(short b, short s, double x, double y, double z, object w, int k, object g){ }
        public void MouseDblClick(short b, short s, double x, double y, double z, object w, int k, object g){ }
        public void MouseDrag(short b, short s, double x, double y, double z, object w, short ds, int k, object g){ }
        // 别的命令接管了鼠标：一般就关窗口（和原来一样）。但如果是我们自己点「改孔型…」开的窗口接管的，
        // 这个窗口说好了要留着（参考孔、规格都还在）—— 原来照样被关掉。现在只是暂停选择，用户回到这个窗口时自动接着选
        bool handingOver, paused;
        bool HasPicks { get { return target != null || (lastUndo != null && !lastUndo.IsEmpty); } }
        // 句柄未创建或窗体已释放时禁止直接 BeginInvoke，并在回调入口再次校验 closing / IsDisposed
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
            if (closing || IsDisposed || !ReferenceEquals((object)assembly ?? partDoc, doc) || !(HasPicks)) return false;
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
            try { var nm = assembly != null ? assembly.Name : partDoc.Name; } catch { SetStatus("打开这个窗口时的文档已经关掉了。请关掉这个窗口重新打开「批量排孔」。", Ui.Bad); return; } paused = false;
            try { StartPicking(); SetStatus("接着选。", Ui.Muted); } catch (Exception ex) { SetStatus(AutoHoleWriter.Friendly(ex), Ui.Warn); }
        }
        public void Idle(int n, out bool more){ more = false; }
        void F.ISECommandEvents.Activate(){ }
        void F.ISECommandEvents.Deactivate(){ }
        public new void KeyDown(ref short key, short shift){ if (key == 27 && ConfirmQuickClose("Esc")) Close(); }
        readonly QuickClose quickClose = new QuickClose();
        bool ConfirmQuickClose(string how){
            if (busy) return false;
            return quickClose.Confirm(how, target == null ? "" : "选好的打孔面和方向", m => SetStatus(m, Ui.Warn));
        }
        public new void KeyPress(ref short key){ }
        public new void KeyUp(ref short key, short shift){ }
    }
}
