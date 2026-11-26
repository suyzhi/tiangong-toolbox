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
        // ---------------- 打孔 ----------------
        void Run(){
            if (busy || closing) return;
            bool hadCommand = command != null;
            try {
                if (assembly == null && partDoc == null) throw new InvalidOperationException("请先打开装配或零件文件。");
                try { var nm = assembly != null ? assembly.Name : partDoc.Name; } catch { throw new InvalidOperationException("打开这个窗口时的文档已经关掉了。请关掉这个窗口重新打开「批量排孔」。"); }
                if (assembly != null && (assembly.ReadOnly || assembly.InPlaceActivated)) throw new InvalidOperationException("装配不可编辑或处于原位编辑中。");
                // 打完孔后缓存的面会失效，重解析一次。选择引用本身也失效时（模型重算过、面被孔切过）别把 COM 错误甩给用户：
                // 沿用上次读到的平面数据接着打（建基准面只用平面数据，FindOrCreatePlane 有退路）
                if (faceSelection != null) {
                    try { target = AutoHoleReader.ReadTarget(faceSelection); }
                    catch (Exception e) {
                        if (target == null) throw new InvalidOperationException("打孔面已经失效，请重新点一次要打孔的面。");
                        Log.Write("AutoHolePatternReread", e.Message);
                    }
                }
                if (target == null) throw new InvalidOperationException("请先点一个要打孔的面。");
                var spec = Spec();
                var hs = HoleSpecNow();
                if (!SlotMode && hs.SpecProblem.Length > 0) throw new InvalidOperationException(hs.SpecProblem);
                if (Kind == HolePatternKind.Circular && !circleReady) throw new InvalidOperationException("圆周均布需要先点一条圆形边线。");
                if (Kind != HolePatternKind.Circular) {
                    string lp = HolePatternSolver.LateralProblem(frame.WidthMm, spec.LateralMm, HalfWidthMm(hs));
                    if (lp.Length > 0) throw new InvalidOperationException(lp);
                }
                var centres = Centres(spec);
                // 落在面外（缺口、开窗、L 形面缺的那一角）的孔位不打：CAD 只会报一句含糊的拒绝，阵列也整排建不成（0.8.5）
                var outside = Outside(centres);
                if (outside.Count == centres.Count) throw new InvalidOperationException("排出来的孔位全都落在打孔面外面（第 " + OutsideText(outside) + " 个），请检查方向边、边距和横向位置。");
                for (int i = outside.Count - 1; i >= 0; i--) centres.RemoveAt(outside[i] - 1);

                // 用户自己在天工里原位编辑着别的零件（双击进去的）：这时天工只许改那一个零件，排到别的零件上每个孔都被拒、只报含糊的 COM 错误。
                // 自动打孔早就先说清楚了（10-22 现场），排孔漏了
                if (assembly != null) {
                    var editing = SafeActive() as P.PartDocument;
                    string ef = ""; try { if (editing != null) ef = editing.FullName; } catch { }
                    string tf = ""; try { tf = target.Part.FullName; } catch { }
                    if (ef.Length > 0 && tf.Length > 0 && !string.Equals(ef, tf, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("天工现在正在原位编辑「" + System.IO.Path.GetFileName(ef) + "」，这时只能改它 —— 先在天工里退出原位编辑（回到顶层装配）再排「" + System.IO.Path.GetFileName(tf) + "」上的孔。");
                }
                busy = true; run.Enabled = false; run.Text = "打孔中…"; UpdatePrimary(); UseWaitCursor = true; StopCommand();
                DrillResult res;
                string what;
                // 带零件间链接的零件（用「自动打孔」保持关联打过孔的零件都是）：天工只许在原位编辑里改它，
                // 在装配里直接建孔每个孔两侧都是 0x80010108（0.7.11 现场，自动打孔早就这么处理了，排孔漏了）。
                object startDoc = null; try { startDoc = app.ActiveDocument; } catch { }
                string targetFullName = ""; try { targetFullName = target.Part.FullName ?? ""; } catch { }
                NativeHoleLink.InPlace inPlace = null;
                if (assembly != null && NativeHoleLink.HasLinks(target.Part)) {   // 单独打开的零件：本来就在零件自己的环境里，不用原位编辑
                    string other = NativeHoleLink.TargetOpenElsewhere(app, assembly, target);
                    if (other.Length > 0) throw new InvalidOperationException(NativeHoleLink.OpenElsewhereMessage(other));
                    string ewhy;
                    inPlace = NativeHoleLink.EnterTarget(app, assembly, target, out ewhy);
                    if (inPlace == null) Log.Write("AutoHolePatternEnter", ewhy);
                }
                try {
                // 进原位后装配侧的 target.Part 代理可能断连（RPC_E_DISCONNECTED），
                // 不能指望 AnchorToActivePart 再从已断连的 target.Part.Application 拿 ActiveDocument；
                // 这里直接用窗口持有的活 app / inPlace.Part 把 target 重锚到当前活零件文档。
                target = ReanchorIfDisconnected(target, inPlace, targetFullName);
                if (SlotMode) {
                    var localDir = target.Placement.InverseNormal(frame.Direction);
                    var lc = new List<V3>(); var ld = new List<V3>();
                    foreach (var c in centres) { lc.Add(target.Placement.InversePoint(target.OntoFace(c))); ld.Add(localDir); }
                    res = AutoHoleWriter.DrillSlots(target, lc, ld, (double)slotLen.Value, (double)slotWidth.Value);
                    if (res.Method.Length == 0) res.Method = "腰孔（通切）";
                    what = "腰孔 " + Num((double)slotLen.Value) + "×" + Num((double)slotWidth.Value);
                } else {
                    if (asPattern.Checked && centres.Count > 1) {
                        // 种子孔 + 阵列；排不成阵列时 HolePatternWriter 自己退回逐个打孔
                        res = HolePatternWriter.Drill(target, hs, centres, Kind == HolePatternKind.Circular ? (V3?)circleCentre : null);
                    } else {
                        // 和「自动打孔」走同一条路：编组命名、并进之前那批、已有孔跳过、天工标准孔
                        var requests = new List<AutoHoleWriter.HoleRequest>();
                        for (int i = 0; i < centres.Count; i++)
                            requests.Add(new AutoHoleWriter.HoleRequest { Spec = hs, Centre = centres[i], Source = "第 " + (i + 1) + " 个孔" });
                        res = AutoHoleWriter.DrillRequests(target, requests);
                    }
                    what = hs.Summary;
                }
                } finally {
                    if (inPlace != null) { try { NativeHoleLink.Leave(app, inPlace); } catch (Exception ex) { Log.Write("AutoHolePatternLeave", ex); } }
                    if (inPlace != null || !ReferenceEquals(SafeActive(), startDoc)) NativeHoleLink.BackTo(app, startDoc);
                }
                // LastResult：给真机脚本/测试读的纯文本（内容和原来的结果框一致）
                var legacy = new List<string>();
                foreach (var o in res.Outcomes) if (o.Kind == ResultList.Kind.Skip) legacy.Add(o.Text);
                legacy.AddRange(res.Notes);
                string msg = "排孔完成：" + res.Created + " / " + res.Requested + " 个"
                           + (res.AlreadyOk > 0 ? "，" + res.AlreadyOk + " 个位置本来就有孔" : "")
                           + "\r\n" + what + "\r\n生成方式：" + res.Method;
                if (res.Features.Count > 0) msg += "\r\n\r\n特征树里：\r\n· " + string.Join("\r\n· ", res.Features.ToArray());
                msg += "\r\n\r\n请保存装配。";
                if (res.Audit.Length > 0) msg += "\r\n\r\n自检：" + res.Audit;
                if (legacy.Count > 0) msg += "\r\n\r\n说明：\r\n· " + string.Join("\r\n· ", legacy.ToArray());
                if (res.Failures.Count > 0) msg += "\r\n\r\n失败原因：\r\n· " + string.Join("\r\n· ", res.Failures.ToArray());
                LastResult = msg;
                // 结果进窗口里的列表（不再弹框），点一条跳到那个孔
                var items = new List<ResultList.Item>(res.Outcomes);
                foreach (var f in res.Features) items.Add(new ResultList.Item(ResultList.Kind.Info, "特征树：" + f));
                if (res.Audit.Length > 0) items.Add(new ResultList.Item(ResultList.Kind.Fail, "自检：" + res.Audit));
                foreach (var n in res.Notes) items.Add(new ResultList.Item(ResultList.Kind.Info, n));
                if (outside.Count > 0) {
                    string skipped = "第 " + OutsideText(outside) + " 个孔位落在打孔面外面（缺口/开窗处），没有打。";
                    items.Add(new ResultList.Item(ResultList.Kind.Warn, skipped));
                    LastResult = msg + "\r\n\r\n" + skipped;
                }
                if (res.Created > 0) items.Add(new ResultList.Item(ResultList.Kind.Info, what + "　·　" + res.Method + "　·　记得保存装配，排错了点「撤销本次打孔」。"));
                string head = "排孔 " + res.Created + " / " + res.Requested + " 个" + (res.AlreadyOk > 0 ? " · 本来就有孔 " + res.AlreadyOk : "") + (res.Failures.Count > 0 ? " · 失败 " + res.Failures.Count : "");
                results.SetResult(head, res.Failures.Count > 0 || res.Audit.Length > 0 ? Ui.Bad : (res.Created > 0 ? Ui.Ok : Ui.Warn), items);
                resultCard.Visible = results.Count > 0; rootStack.Relayout();
                drilledOnce = drilledOnce || res.Created > 0;
                shell.Go(PagePreview);
                if (res.Created > 0) shell.ShowToast("已排 " + res.Created + " 个孔" + (res.Failures.Count > 0 ? "，失败 " + res.Failures.Count + " 个" : ""), res.Failures.Count > 0 ? Ui.Warn : Ui.Ok,
                                                     "撤销本次打孔", (s2, e2) => UndoLastBatch());
                lastUndo = res.Undo;
                undoButton.Enabled = !res.Undo.IsEmpty;
                lastCreated = res.Undo.Created();
                editButton.Enabled = lastCreated.Count > 0;
                SetStatus("完成：" + res.Created + " 个孔" + (res.Failures.Count > 0 ? "，失败 " + res.Failures.Count : ""), res.Failures.Count > 0 ? Ui.Warn : Ui.Ok);
                try { if (target != null && target.Part != null) HoleScanCad.Invalidate(target.Part); } catch { }
                busy = false;
                if (closing || IsDisposed) return;
                run.Text = "开始打孔"; UseWaitCursor = false;
                if (hadCommand) { try { StartPicking(); } catch (Exception ex) { SetStatus(AutoHoleWriter.Friendly(ex), Ui.Warn); } }
                RefreshPreview();
            } catch (Exception e) {
                Log.Write("AutoHolePatternRun", e);
                busy = false;
                LastResult = "排孔中断：" + e.Message;
                if (closing || IsDisposed) return;
                run.Text = "开始打孔"; UseWaitCursor = false;
                try { if (hadCommand && command == null) StartPicking(); } catch { }
                SetStatus(AutoHoleWriter.Friendly(e), Ui.Bad);
                if (!SuppressDialogs) MessageBox.Show(this, AutoHoleWriter.Friendly(e), "批量排孔", MessageBoxButtons.OK, MessageBoxIcon.Error);
                RefreshPreview();
            }
        }

        // 进原位后若 target.Part（装配代理文档）断连，改用窗口持有的活 app / inPlace.Part 重建有效 TargetFace，
        // 避免下游 AnchorToActivePart/DrillRequests 读取 target.Part.Application 抛 RPC_E_DISCONNECTED。
        TargetFace ReanchorIfDisconnected(TargetFace cur, NativeHoleLink.InPlace inPlace, string expectedFullName){
            if (cur == null) return null;
            P.PartDocument active = null;
            if (inPlace != null && TargetFace.PartAlive(inPlace.Part)) active = inPlace.Part;
            if (active == null) {
                var act = SafeActive() as P.PartDocument;
                if (act != null && TargetFace.PartAlive(act)) {
                    string af = ""; try { af = act.FullName ?? ""; } catch { }
                    if (string.IsNullOrEmpty(expectedFullName) || string.Equals(af, expectedFullName, StringComparison.OrdinalIgnoreCase))
                        active = act;
                }
            }
            if (active == null) {
                if (TargetFace.PartAlive(cur.Part)) return cur;
                if (faceSelection != null) {
                    try {
                        var fresh = AutoHoleReader.ReadTarget(faceSelection);
                        fresh.Instances = cur.Instances; fresh.Risk = cur.Risk; fresh.LinkNote = cur.LinkNote;
                        return fresh;
                    } catch (Exception e) { Log.Write("AutoHolePatternReanchorRead", e.Message); }
                }
                return cur;
            }
            // 已拿到活的活动零件文档：当原 Part 已断连或不是当前活动文档时，用活 active 重锚
            if (TargetFace.PartAlive(cur.Part) && ReferenceEquals(cur.Part, active)) return cur;
            G.Face matched = FindMatchingFaceOnActive(active, cur);
            var anchored = new TargetFace {
                Plane = cur.Plane,
                Face = matched,
                Part = active,
                Placement = cur.Placement,
                Pick = cur.Pick,
                Bounds = cur.Bounds,
                Body = Math.Max(1, cur.Body),
                PartName = cur.PartName,
                FaceId = cur.FaceId,
                Label = cur.Label,
                LinkNote = cur.LinkNote,
                Instances = cur.Instances,
                Risk = cur.Risk
            };
            if (matched != null) {
                try { anchored.FaceId = matched.ID.ToString(CultureInfo.InvariantCulture); } catch { }
                anchored.Label = AutoHoleReader.FaceText(active, anchored.Body, anchored.FaceId);
            }
            return anchored;
        }
        static G.Face FindMatchingFaceOnActive(P.PartDocument active, TargetFace cur){
            try {
                if (active == null || cur == null || cur.Plane == null || cur.Placement == null || active.Models.Count < 1) return null;
                int bodyIndex = Math.Max(1, cur.Body);
                var model = PartBodies.Model(active, bodyIndex);
                if (model == null) return null;
                V3 localPoint = cur.Placement.InversePoint(cur.Plane.Point);
                V3 localNormal = cur.Placement.InverseNormal(cur.Plane.Normal).Unit();
                G.Face bestFace = null; double bestDist = double.MaxValue;
                foreach (G.Face cand in (G.Faces)((G.Body)model.Body).get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll)) {
                    var plane = cand.Geometry as G.Plane;
                    if (plane == null) continue;
                    Array pt = new double[3], nv = new double[3];
                    try { plane.GetPlaneData(ref pt, ref nv); } catch { continue; }
                    V3 cn = V3.From(nv).Unit();
                    // 优先按 FaceId 精确匹配；若 FaceId 不可用则要求同向或严格共面且局部点落在该面片包围盒内
                    string cid = ""; try { cid = cand.ID.ToString(CultureInfo.InvariantCulture); } catch { }
                    if (!string.IsNullOrEmpty(cur.FaceId) && cid == cur.FaceId && Math.Abs(cn.Dot(localNormal)) >= 0.999) {
                        double dId = Math.Abs((V3.From(pt) - localPoint).Dot(localNormal));
                        if (dId <= 1e-5) return cand;
                    }
                    if (cn.Dot(localNormal) < 0.999) continue;
                    double dist = Math.Abs((V3.From(pt) - localPoint).Dot(localNormal));
                    if (dist > 1e-6) continue;
                    Array lo = new double[3], hi = new double[3];
                    try {
                        cand.GetRange(ref lo, ref hi);
                        V3 bLo = V3.From(lo), bHi = V3.From(hi);
                        const double pad = 1e-4;
                        if (localPoint.X < bLo.X - pad || localPoint.X > bHi.X + pad
                         || localPoint.Y < bLo.Y - pad || localPoint.Y > bHi.Y + pad
                         || localPoint.Z < bLo.Z - pad || localPoint.Z > bHi.Z + pad) continue;
                    } catch { }
                    if (dist < bestDist) { bestDist = dist; bestFace = cand; }
                }
                return bestFace;
            } catch (Exception e) { Log.Write("AutoHolePatternFindFace", e.Message); return null; }
        }

        // 按现在的参数排出来的孔心（装配坐标，已投到打孔面上）
        List<V3> Centres(HolePatternSpec spec){
            var centres = new List<V3>();
            if (target == null || frame == null) return centres;
            if (Kind == HolePatternKind.Circular) {
                if (!circleReady) return centres;
                var u = frame.Direction; var v = frame.Perp;
                var cc = target.OntoFace(circleCentre);   // 定位圆可能在另一面（法兰背面）：圆心先投到打孔面上
                foreach (var deg in HolePatternSolver.Angles(spec)) {
                    double rad = deg * Math.PI / 180.0;
                    centres.Add(cc + u * (circleRadiusMm / 1000.0 * Math.Cos(rad)) + v * (circleRadiusMm / 1000.0 * Math.Sin(rad)));
                }
            } else {
                var side = frame.Perp * (spec.LateralMm / 1000.0);
                foreach (var o in HolePatternSolver.Offsets(frame.LengthMm, spec)) centres.Add(target.OntoFace(frame.At(o) + side));
            }
            return centres;
        }
        // 落在面外的孔位序号（1 起）
        List<int> Outside(IList<V3> centres){
            var l = new List<int>();
            for (int i = 0; i < centres.Count; i++) { try { if (!FaceFrameReader.ContainsPoint(target, centres[i], 0.5)) l.Add(i + 1); } catch { } }
            return l;
        }
        static string OutsideText(IList<int> idx){
            return string.Join("、", idx.Take(8).Select(i => i.ToString(CultureInfo.InvariantCulture)).ToArray()) + (idx.Count > 8 ? " 等" : "");
        }

        // 撤销上一次「开始打孔」（HoleUndo：删掉那一批孔/腰孔，编组和改名恢复原样）
        void UndoLastBatch(){
            if (busy || closing || lastUndo == null || lastUndo.IsEmpty) return;
            if (!SuppressDialogs && MessageBox.Show(this, "删掉上一次排的 " + lastUndo.HoleCount + " 个孔？\r\n（编组、改名会一并恢复）",
                    "撤销本次打孔", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            bool hadCmd = command != null;
            busy = true; UseWaitCursor = true; undoButton.Enabled = false;
            StopCommand();
            try {
                var outcome = lastUndo.Run();
                lastUndo = null;
                lastCreated = null; editButton.Enabled = false;
                foreach (var part in outcome.Parts) { try { HoleScanCad.Invalidate(part); } catch { } }
                var items = new List<ResultList.Item>();
                foreach (var n in outcome.Notes) items.Add(new ResultList.Item(ResultList.Kind.Info, n));
                results.SetResult(outcome.Summary, outcome.Failed > 0 ? Ui.Warn : Ui.Ok, items);
                resultCard.Visible = true; rootStack.Relayout();
                LastResult = outcome.Summary;
                SetStatus(outcome.Summary, outcome.Failed > 0 ? Ui.Warn : Ui.Ok);
            } catch (Exception e) {
                Log.Write("AutoHolePatternUndo", e);
                SetStatus("撤销没有完成：" + AutoHoleWriter.Friendly(e), Ui.Bad);
                LastResult = "撤销中断：" + e.Message;
            } finally {
                busy = false;
                if (!closing && !IsDisposed) {
                    UseWaitCursor = false;
                    if (hadCmd && command == null) { try { StartPicking(); } catch (Exception ex) { SetStatus(AutoHoleWriter.Friendly(ex), Ui.Warn); } }
                    RefreshPreview();
                }
            }
        }
        void OpenEdit(){
            if (busy || closing || lastCreated == null || lastCreated.Count == 0) return;
            handingOver = command != null;
            try { if (!(assembly != null ? AutoHoleModule.OpenEdit(app, assembly, lastCreated) : AutoHoleModule.OpenEdit(app, partDoc, lastCreated))) { handingOver = false; SetStatus("上一个改孔型窗口正在改孔，等它改完再点。", Ui.Warn); } }
            catch (Exception e) { handingOver = false; Log.Write("AutoHolePatternOpenEdit", e); SetStatus("打不开改孔型窗口：" + e.Message, Ui.Bad); }
        }
    }
}
