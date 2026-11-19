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
            refEmpty.Visible = groups.Count == 0 && slots.Count == 0;
            delSlots.Enabled = slots.Count > 0;
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
    }
}
