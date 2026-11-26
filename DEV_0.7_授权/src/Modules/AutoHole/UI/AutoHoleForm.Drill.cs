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
                                                     : "请先点要打孔的面：到「打孔面」那一步再点平面（在「参考孔」那一步点平面是认面上的孔）。", Ui.Warn);
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
                var prevProgress = AutoHoleWriter.Progress;
                AutoHoleWriter.Progress = progress;   // 仅在打孔执行期间挂载进度回调，finally 还原，避免多窗串扰
                hadCommand = command != null;   // 打完只恢复"原来就在选"的状态（外部脚本驱动时没有选择命令）
                StopCommand();
                try {

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
                        AddProblem(y.Problems, targetLabels[ti] + "：读不到这张面了（" + AutoHoleWriter.Friendly(e) + "），没有打。请重新点一次这张面。");
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
                        catch (Exception e) { y.Skipped++; AddProblem(y.Problems, partName + " Φ" + Num(hole.DiameterMm) + " 无法定位：" + AutoHoleWriter.Friendly(e)); continue; }
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

                // 宿主若在 DoEvents 期间关闭销毁了本窗口，不能再访问 UI 控件或重启 StartPicking 泄漏 COM 命令
                if (closing || IsDisposed) return;

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
                if (closing || IsDisposed) return;
                busy = false; run.Text = "开始打孔"; run.Progress = -1; UseWaitCursor = false;
                drilledOnce = drilledOnce || y.Made > 0;
                if (hadCommand) { try { StartPicking(); } catch (Exception ex) { SetStatus(AutoHoleWriter.Friendly(ex), Ui.Warn); } }
                if (y.Made > 0 && lastLinks != null) ShowLinkReport(null, NotScanned);
                RefreshAll();
                SetStatus(statusAfterDrill, statusAfterDrillColor);
                if (y.Made > 0) shell.ShowToast("已打 " + y.Made + " 个孔" + (y.Failed > 0 ? "，失败 " + y.Failed + " 个" : "") + "　点下一个面可以接着打", y.Failed > 0 ? Ui.Warn : Ui.Ok,
                                                "撤销本次打孔", (s2, e2) => UndoLastBatch());
                } finally {
                    busy = false;
                    if (AutoHoleWriter.Progress == progress) AutoHoleWriter.Progress = prevProgress;
                }
            } catch (Exception e) {
                Log.Write("AutoHoleDrill", e);
                // 中途失败也可能已经进出过原位编辑：面片缓存作废，下次从用户点的引用重读（不然每次重试都拿同一份旧面数据）
                resolved.Clear();
                busy = false;
                LastResult = "打孔中断：" + e.Message;
                if (closing || IsDisposed) return;
                run.Text = "开始打孔"; run.Progress = -1; UseWaitCursor = false;
                try { if (hadCommand && command == null) StartPicking(); } catch { }
                SetStatus(AutoHoleWriter.Friendly(e), Ui.Bad);
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
            try { if (!AutoHoleModule.OpenEdit(app, assembly, lastCreated)) { handingOver = false; SetStatus("上一个改孔型窗口正在改孔，等它改完再点。", Ui.Warn); } }
            catch (Exception e) { handingOver = false; Log.Write("AutoHoleOpenEdit", e); SetStatus("打不开改孔型窗口：" + e.Message, Ui.Bad); }
        }
    }
}
