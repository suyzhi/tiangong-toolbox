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
    public sealed partial class AutoHoleForm {
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
            // 腰孔不在列表里（只在计数里写「腰孔 N」）：点面一下认进来好几条、又不想配腰孔时，这里一键去掉（0.9.1）
            ((FlatButton)delSlots).SetFore(Ui.Text2);
            delSlots.Click += (s, e) => { if (busy || slots.Count == 0) return; int k = slots.Count; slots.Clear(); slotPicks.Clear(); RefreshAll(); SetStatus("已去掉 " + k + " 条参考腰孔（圆孔不动）。", Ui.Muted); };
            tips.SetToolTip(delSlots, "去掉全部参考腰孔，圆孔留着。单条腰孔：在模型里再点一次它一头的圆弧 = 取消。");
            shell.AddHeader(pRefs, rulesButton); shell.AddHeader(pRefs, delSlots); shell.AddHeader(pRefs, delRef); shell.AddHeader(pRefs, clrRef);
            refFlash = new Flash(refList);
            refList.IntegralHeight = false; refList.BorderStyle = BorderStyle.None;
            refList.DrawMode = DrawMode.OwnerDrawFixed; refList.ItemHeight = Ui.S(66); refList.BackColor = Color.White;
            refList.DrawItem += DrawRefItem;
            refCount.AutoSize = false; refCount.Font = Ui.F8; refCount.ForeColor = Ui.Muted; refCount.TextAlign = ContentAlignment.MiddleRight;
            ListPage(pRefs.Body, "在模型里点孔口圆边、孔内壁，或直接点一个带孔的面（面上的孔全部认成参考孔）。同孔径的孔自动归成一组，规格按配做规则反推。选完点「下一步」去点打孔面。", refList, refEmpty, null, refCount);

            // ---- ② 打孔面 ----
            var delFace = Ui.Small("移除"); var clrFace = Ui.Small("清空");
            ((FlatButton)delFace).SetFore(Ui.Text2); ((FlatButton)clrFace).SetFore(Ui.Text2);
            delFace.Click += (s, e) => RemoveSelectedFace();
            clrFace.Click += (s, e) => { if (busy) return; targets.Clear(); targetLabels.Clear(); resolved.Clear(); RefreshAll(); SetStatus("已清空打孔面。", Ui.Muted); };
            shell.AddHeader(pFaces, delFace); shell.AddHeader(pFaces, clrFace);
            faceFlash = new Flash(faceList);
            faceList.IntegralHeight = false; faceList.BorderStyle = BorderStyle.None;
            faceList.DrawMode = DrawMode.OwnerDrawFixed; faceList.ItemHeight = Ui.S(52); faceList.BackColor = Color.White;
            faceList.DrawItem += DrawFaceItem;
            faceCount.AutoSize = false; faceCount.Font = Ui.F8; faceCount.ForeColor = Ui.Muted; faceCount.TextAlign = ContentAlignment.MiddleRight;
            ListPage(pFaces.Body, "点要配孔的零件平面（这一步点平面就是选打孔面，面上原有的孔不会认成参考孔）。孔心沿参考孔轴线投到这些面上，落不到面上的会自动跳过。", faceList, faceEmpty, null, faceCount);

            // ---- ③ 孔规格：组标签 → 正在编辑谁 → 参数网格 | 孔形状 ----
            BuildSpecCard(pSpec.Body);

            // ---- ④ 预览并打孔 ----
            var prev = new VerticalStack { Dock = DockStyle.Fill, Padding = new Padding(0), Gap = Ui.S(12) };
            pPrev.Body.Controls.Add(prev);
            var stats = new Panel { Height = statHoles.Height, BackColor = Ui.Bg };
            stats.Controls.Add(statHoles); stats.Controls.Add(statRefs); stats.Controls.Add(statFaces);
            stats.Resize += (s, e) => {
                int g = Ui.S(12), w = (stats.Width - g * 2) / 3;
                statHoles.SetBounds(0, 0, w, stats.Height); statRefs.SetBounds(w + g, 0, w, stats.Height); statFaces.SetBounds(2 * (w + g), 0, stats.Width - 2 * (w + g), stats.Height);
            };
            var pvBody = new Panel { Height = Ui.S(30), BackColor = Ui.Bg };
            int chipW = Ui.S(124);
            chip.SetBounds(0, 1, chipW, Ui.S(28));
            previewLine.SetBounds(chipW + Ui.S(10), 0, Ui.S(600), Ui.S(30));
            previewLine.AutoSize = false; previewLine.Font = Ui.F9B; previewLine.ForeColor = Ui.Text; previewLine.AutoEllipsis = true;
            previewLine.TextAlign = ContentAlignment.MiddleLeft;
            pvBody.Controls.Add(chip); pvBody.Controls.Add(previewLine);
            pvBody.Resize += (s, e) => previewLine.Width = Math.Max(Ui.S(60), pvBody.ClientSize.Width - (chipW + Ui.S(10)));
            previewSub.AutoSize = false; previewSub.ForeColor = Ui.Text2; previewSub.AutoEllipsis = true; previewSub.Height = Ui.S(70);
            previewSub.BackColor = Ui.Bg;
            linkCheck.BackColor = Ui.Bg; linkCheck.ForeColor = Ui.Text; linkCheck.Height = Ui.S(28);
            linkCheck.Text = "保持与参考孔关联";
            tips.SetToolTip(linkCheck, "勾上：用天工原生关联把孔挂在参考孔上（特征树里多一个带锁链的「孔位链接-参考零件」，\r\n" +
                                       "和这张面打的孔编在同一个「关联孔 …」组里 —— 删孔时删这个组，链接一起删掉）。\r\n" +
                                       "参考零件挪动、参考孔改了位置，天工自己把孔带过去（没跟上时点「同步孔位」= 更新所有链接）。\r\n" +
                                       "打孔时插件会自动进出原位编辑，界面会闪一下，这是正常的。\r\n" +
                                       "挂不上天工关联的孔（零件的链接属于别的装配、斜孔、认不准实例……）改用插件自己的关联记录，\r\n" +
                                       "结果里会逐条说明原因；参考零件挪动后点「同步孔位」把它们挪回去。");
            var previewArea = new VerticalStack { Padding = new Padding(0), Gap = Ui.S(10), AutoScroll = false };
            previewArea.Add(stats); previewArea.Add(pvBody); previewArea.Add(previewSub); previewArea.Add(linkCheck);
            previewArea.Height = stats.Height + pvBody.Height + previewSub.Height + linkCheck.Height + Ui.S(32);
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
            var linkCard = new RailCard("关联孔位", 136);
            linkLabel.AutoSize = false; linkLabel.TextAlign = ContentAlignment.TopLeft; linkLabel.AutoEllipsis = true;
            linkLabel.ForeColor = Ui.Muted; linkLabel.BackColor = Color.White; linkLabel.Font = Ui.F8;
            syncButton.Click += (s, e) => SyncLinks();
            rescanButton.Click += (s, e) => ScanLinks(true);
            tips.SetToolTip(syncButton, "天工关联的孔：让天工更新所有链接，孔自己跟上参考孔（顺带查出断了的孔位链接）。\r\n" +
                                        "插件关联的孔（挂不上天工关联时改用的）：按记下的参考孔重新求孔心，把错位的挪回同轴（孔型规格不变）。");
            linkCard.Controls.Add(linkLabel); linkCard.Controls.Add(syncButton); linkCard.Controls.Add(rescanButton);
            linkCard.Resize += (s, e) => {
                int pad = Ui.S(12), w = Math.Max(40, linkCard.Width - pad * 2), bh = Ui.S(30), by = linkCard.Height - bh - Ui.S(10);
                linkLabel.SetBounds(pad, Ui.S(34), w, Math.Max(20, by - Ui.S(38)));
                int syncW = Math.Min(Ui.S(112), Math.Max(Ui.S(60), w - Ui.S(70)));
                syncButton.SetBounds(pad, by, syncW, bh);
                rescanButton.SetBounds(syncButton.Right + Ui.S(6), by, Math.Max(Ui.S(44), w - syncButton.Width - Ui.S(6)), bh);
            };
            shell.RailBottom = linkCard;

            // ---- 底部操作栏 ----
            var more = moreMenu = Ui.Menu();
            var miEdit = more.Items.Add("改孔型…（刚打的这批孔整组换孔型）");
            var miUndo = more.Items.Add("撤销本次打孔");
            more.Items.Add(new ToolStripSeparator());
            var miClear = more.Items.Add("全部重选（清空参考孔和打孔面）");
            miEdit.Click += (s, e) => OpenEdit();
            miUndo.Click += (s, e) => UndoLastBatch();
            miClear.Click += (s, e) => ClearAll();
            more.Opening += (s, e) => { miEdit.Enabled = editButton.Enabled && !busy; miUndo.Enabled = undoButton.Enabled && !busy; miClear.Enabled = !busy; };
            Ui.BindDropDownMenu(moreButton, more, (b, m) => new Point(0, -m.PreferredSize.Height - 4));
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
        readonly Button delSlots = Ui.Small("去掉腰孔");
        readonly EmptyHint refEmpty = new EmptyHint("正在等你在模型里点孔：孔口圆边、孔内壁，或一个带孔的面…");
        readonly EmptyHint faceEmpty = new EmptyHint("正在等你在模型里点要打孔的平面…");
        void ListPage(Panel body, string hint, ListBox list, EmptyHint empty, Control footLeft, Label footRight){
            var stack = new VerticalStack { Dock = DockStyle.Fill, Padding = new Padding(0), Gap = Ui.S(8), AutoScroll = false };
            body.Controls.Add(stack);
            var h = new Label { Text = hint, AutoSize = false, Height = Ui.S(48), ForeColor = Ui.Text2, BackColor = Ui.Bg, Font = Ui.F9 };
            var frame = new Panel { BackColor = Color.White, Padding = new Padding(0) };
            Ui.EnableDoubleBuffer(frame);
            Ui.EnableDoubleBuffer(list);
            list.Dock = DockStyle.Fill; list.Margin = new Padding(0);
            frame.Controls.Add(list);
            empty.Dock = DockStyle.Top; frame.Controls.Add(empty); empty.BringToFront();
            var foot = new Panel { Height = Ui.S(28), BackColor = Ui.Bg };
            if (footLeft != null) { footLeft.Location = new Point(0, Ui.S(4)); foot.Controls.Add(footLeft); }
            foot.Controls.Add(footRight);
            foot.Resize += (s, e) => footRight.SetBounds(Math.Max(0, foot.Width - Ui.S(220)), Ui.S(2), Ui.S(220), Ui.S(24));
            stack.Add(h); stack.Add(frame, true); stack.Add(foot);
        }

        // 孔规格页：组标签 + 两行"正在编辑谁 / 规格怎么来的"，下面左边参数网格、右边孔形状（剖面在上、3D 在下）。
        // 网格每一行都是"标签 + 一串控件"，位置由 FieldGrid 算，不再手摆坐标；换孔型时整行显示/隐藏，下面的行自动顶上去。
        void BuildSpecCard(Panel page){
            var stack = new VerticalStack { Dock = DockStyle.Fill, Padding = new Padding(0), Gap = Ui.S(8) };
            page.Controls.Add(stack);
            groupTabs.SelectedChanged += (s, e) => { if (groupTabs.Selected >= 0 && groupTabs.Selected < refList.Items.Count) refList.SelectedIndex = groupTabs.Selected; };
            editWho.AutoSize = false; editWho.Font = Ui.F9B; editWho.ForeColor = Ui.Text; editWho.AutoEllipsis = true; editWho.Height = Ui.S(24); editWho.BackColor = Ui.Bg;
            editNote.AutoSize = false; editNote.Font = Ui.F8; editNote.ForeColor = Ui.Muted; editNote.AutoEllipsis = true; editNote.Height = Ui.S(22); editNote.BackColor = Ui.Bg;

            kindSeg.Width = Math.Max(Ui.S(340), kindSeg.PreferredWidth);
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

            var cols = new SplitColumns(0.62f) { Gap = Ui.S(20) };
            var left = new Panel { BackColor = Ui.Bg };
            left.Controls.Add(grid);
            left.Resize += (s, e) => grid.SetBounds(0, 0, left.Width, grid.PreferredHeight);
            var shapeCard = Ui.Card("孔形状", 0, "剖面 · 打完后的样子");
            shapeCard.Fill = true;
            shapeCard.Padding = Ui.SPad(10, 38, 10, 10);
            sectionView.Header = ""; modelView.Header = "";
            var views = new VerticalStack { Dock = DockStyle.Fill, Padding = new Padding(0), Gap = Ui.S(8), BackColor = Ui.Rail, AutoScroll = false };
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
            bool wantBack = cur > 0 && !busy;
            if (backButton.Enabled != wantBack) backButton.Enabled = wantBack;
            bool canRun = run.Enabled;
            bool showRun = busy || canRun || cur >= PagePreview;
            bool layoutDirty = false;
            if (run.Visible != showRun) { run.Visible = showRun; layoutDirty = true; }
            if (nextButton.Visible != !showRun) { nextButton.Visible = !showRun; layoutDirty = true; }
            if (!busy) {
                string wantRunText = canRun ? "开始打孔 " + lastTotal + " 个孔" : "开始打孔";
                if (run.Text != wantRunText) {
                    int oldW = run.Width;
                    run.Text = wantRunText;
                    Ui.FitButton(run);
                    if (run.Width != oldW) layoutDirty = true;
                }
            }
            if (!showRun) {
                string wantNextText = "下一步：" + shell.Pages[Math.Min(cur + 1, shell.Pages.Count - 1)].Title;
                if (nextButton.Text != wantNextText) {
                    int oldW = nextButton.Width;
                    nextButton.Text = wantNextText;
                    Ui.FitButton(nextButton);
                    if (nextButton.Width != oldW) layoutDirty = true;
                }
            }
            if (layoutDirty) shell.Footer.PerformLayout();
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
            linkCheck.CheckedChanged += (s, e) => { RefreshPreview(false); faceList.Invalidate(); };
            linkCheck.Click += (s, e) => SaveChoices();
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
            gr.FillRectangle(Ui.CachedBrush(Color.White), e.Bounds);
            if (e.Index < 0 || e.Index >= groups.Count) return;
            var g = groups[e.Index];
            bool sel = (e.State & DrawItemState.Selected) != 0;
            var r = new Rectangle(e.Bounds.X, e.Bounds.Y + Ui.S(3), e.Bounds.Width - 1, e.Bounds.Height - Ui.S(7));
            Ui.FillRound(gr, r, Ui.S(10), refFlash.Back(g, sel ? Ui.AccentSoft : Color.White));
            Ui.DrawRound(gr, r, Ui.S(10), sel ? Ui.Accent : Ui.Line, sel ? 1.5f : 1f);
            int x = r.X + Ui.S(14), y = r.Y + Ui.S(8);
            string head = g.Head.Replace("　", " ");
            var headSz = TextRenderer.MeasureText(gr, head, Ui.NumB);
            TextRenderer.DrawText(gr, head, Ui.NumB, new Point(x, y), Ui.Text, TextFormatFlags.NoPadding);
            x += headSz.Width + Ui.S(10);
            TextRenderer.DrawText(gr, "→", Ui.F9, new Point(x, y), Ui.Muted, TextFormatFlags.NoPadding);
            x += Ui.S(22);
            var r1 = new Rectangle(x, y, Math.Max(0, r.Right - x - Ui.S(10)), Ui.S(22));
            TextRenderer.DrawText(gr, g.Target, sel ? Ui.F9B : Ui.F9, r1, Ui.Accent, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            string note = g.MouthInfo.Length > 0 ? g.MouthInfo : (g.Spec != null && g.Spec.Note.Length > 0 ? g.Spec.Note : " ");
            var r2 = new Rectangle(r.X + Ui.S(14), r.Y + Ui.S(34), r.Width - Ui.S(24), Ui.S(20));
            TextRenderer.DrawText(gr, note, Ui.F8, r2, Ui.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }

        void DrawFaceItem(object sender, DrawItemEventArgs e){
            var gr = e.Graphics;
            gr.FillRectangle(Ui.CachedBrush(Color.White), e.Bounds);
            if (e.Index < 0 || e.Index >= targetLabels.Count) return;
            bool sel = (e.State & DrawItemState.Selected) != 0;
            var r = new Rectangle(e.Bounds.X, e.Bounds.Y + Ui.S(3), e.Bounds.Width - 1, e.Bounds.Height - Ui.S(7));
            Ui.FillRound(gr, r, Ui.S(10), faceFlash.Back(e.Index < targets.Count ? targets[e.Index] : null, sel ? Ui.AccentSoft : Color.White));
            Ui.DrawRound(gr, r, Ui.S(10), sel ? Ui.Accent : Ui.Line, sel ? 1.5f : 1f);
            // 小面片图标
            int ix = r.X + Ui.S(14), iy = r.Y + r.Height / 2;
            var sm = gr.SmoothingMode; gr.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            gr.DrawPolygon(Ui.CachedPen(Ui.Muted, 1.2f), new[]{ new Point(ix, iy - Ui.S(2)), new Point(ix + Ui.S(8), iy - Ui.S(7)), new Point(ix + Ui.S(16), iy - Ui.S(2)), new Point(ix + Ui.S(8), iy + Ui.S(3)) });
            gr.SmoothingMode = sm;
            // 这张面所在零件的链接属于别的装配（挂不了天工关联）：标个 ⚠，悬停看原因
            var t = e.Index < resolved.Count ? resolved[e.Index] : null;
            bool warn = (linkCheck.Checked && t != null && t.LinkNote.Length > 0) || (t != null && (t.Instances > 1 || t.Risk.Length > 0));
            string text = (warn ? "⚠ " : "") + targetLabels[e.Index] + (t != null && t.Instances > 1 ? "（装配里用了 " + t.Instances + " 次）" : "");
            var tr = new Rectangle(ix + Ui.S(28), r.Y, r.Right - ix - Ui.S(38), r.Height);
            TextRenderer.DrawText(gr, text, sel ? Ui.F9B : Ui.F9, tr, warn ? Ui.Warn : Ui.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }
}
