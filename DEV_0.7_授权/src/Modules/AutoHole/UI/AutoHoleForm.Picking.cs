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
            foreach (var o in items) AcceptPick(o, PlanePick.Auto);
            int r = groups.Sum(g => g.Count) - refsBefore, f = targets.Count - facesBefore;
            if (r + f > 0) SetStatus("已接过天工里预先选中的对象：参考孔 " + r + " 个、打孔面 " + f + " 个。" + (f == 0 ? "再点要打孔的面。" : "检查预览后点「开始打孔」。"), Ui.Ok);
        }

        void ConfigureFilter(){
            if (mouse == null) return;
            mouse.ClearLocateFilter();
            mouse.AddToLocateFilter((int)SolidEdgeConstants.seLocateFilterConstants.seLocateEdge);
            mouse.AddToLocateFilter((int)SolidEdgeConstants.seLocateFilterConstants.seLocateFace);
        }

        // 点平面当什么：Refs = 认面上的孔（参考孔页）；Face = 打孔面（打孔面页及以后）；
        // Auto = 老规则（面上有孔认孔、没孔或参考孔投到它的空位上就当打孔面），只给"打开窗口前在天工里已经选好的对象"用 —— 那时还没有页面可言
        enum PlanePick { Refs, Face, Auto }

        // 三种点法都从这里进来：
        //   点孔口的圆形边线 / 点孔的内壁圆柱面 → 加一个参考孔（哪一页都一样）；
        //   点平面 → 在 ① 参考孔页：把这个面上的孔全认出来（按孔径归类）；在 ② 打孔面页及以后：加打孔面。
        public void AcceptPick(object selected){ AcceptPick(selected, false); }
        // ctrl：按住 Ctrl 点平面 = 和当前页反着来（参考孔页上当打孔面；打孔面页上认面上的孔）
        public void AcceptPick(object selected, bool ctrl){
            bool refsPage = shell.Current == PageRefs;
            AcceptPick(selected, refsPage != ctrl ? PlanePick.Refs : PlanePick.Face);
        }
        void AcceptPick(object selected, PlanePick mode){
            if (busy || closing || selected == null) return;
            var before = groups.ToDictionary(g => g, g => g.Count);
            int slotsBefore = slots.Count, facesBefore = targets.Count;
            AcceptPickCore(selected, mode);
            AfterPick(before, slotsBefore, facesBefore);
        }
        // 新加的那一项闪一下。**不翻页**（0.9.1，用户要的）：原来点了面就跳到「打孔面」、点了孔就跳回「参考孔」，
        // 在参考孔页上连着点几张带孔的面，认完一张就被带走了。左边步骤栏的计数会跟着变
        void AfterPick(Dictionary<RefGroup, int> before, int slotsBefore, int facesBefore){
            if (closing || IsDisposed) return;
            for (int i = facesBefore; i < targets.Count; i++) faceFlash.Mark(targets[i]);
            foreach (var g in groups) { int n; if (!before.TryGetValue(g, out n) || g.Count > n) refFlash.Mark(g); }
        }
        void AcceptPickCore(object selected, PlanePick mode){
            try {
                var pg = PickGeometry.Unwrap(selected);
                bool plane = pg.Geometry is G.Face && ((G.Face)pg.Geometry).Geometry is G.Plane;
                if (plane && mode == PlanePick.Refs) { PickFaceWithHoles(selected, true); RefreshAll(); return; }
                if (plane && mode == PlanePick.Face) {
                    // 刚才在参考孔页从这张面认进来的孔撤掉：孔投到自己这张面上全是"本来就有孔"，留着只会让人以为要打
                    var tf = AutoHoleReader.ReadTarget(selected);
                    int dropped = DropRefsOn(tf);
                    AddTargetFace(selected, tf);
                    if (dropped > 0) SetStatus("已作为打孔面：" + FaceLabel(tf) + "（之前从这张面认进来的 " + dropped + " 个参考孔已撤掉）。", Ui.Ok);
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
                    } else {
                        PickFaceWithHoles(selected, false);   // 只剩 Auto（预先选好的对象）走到这里
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
            // 按「零件 + 同一根轴线」分：配孔检查把同样的几处漏孔合成了一条（HoleCheck.Merge），同一个零件上有好几个位置，
            // 只按零件分会把它们并成一个参考孔、只打一个
            var clusters = new List<List<HoleRecord>>();
            foreach (var h in holes) {
                var home = clusters.FirstOrDefault(c => c[0].PartName == h.PartName && SameAxisRec(c[0], h));
                if (home == null) { home = new List<HoleRecord>(); clusters.Add(home); }
                home.Add(h);
            }
            foreach (var cl in clusters) {
                var list = cl;
                string partKey = cl[0].PartName;
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
                r.Source = HoleCheck.ShortPart(partKey) + "（配孔检查）";
                if (r.MouthDiameterMm - r.DiameterMm > HoleScan.StepToleranceMm && string.IsNullOrEmpty(r.MouthKind)) r.MouthKind = "孔口台阶";
                try { if (AddHole(r, bottom.Selection, true)) added++; } catch (Exception e) { Log.Write("AutoHolePreload", e); }
            }
            RefreshList(groups.Count - 1);
            RefreshPreview();
            SetStatus("已从配孔检查带入 " + added + " 个参考孔。请点" + (string.IsNullOrEmpty(missingPart) ? "要打孔的零件" : "「" + missingPart + "」") + "上要打孔的面，再点「开始打孔」。", Ui.Accent);
        }

        // 同一个零件上的两条圆边是不是同一个孔（同一根轴线，横向 0.5mm 以内：沉孔的过孔边 + 沉头边）
        static bool SameAxisRec(HoleRecord a, HoleRecord b){
            try {
                var n = a.Axis.Unit();
                if (Math.Abs(Math.Abs(n.Dot(b.Axis.Unit())) - 1) > 1e-3) return false;
                var d = b.Center - a.Center;
                return (d - n * d.Dot(n)).Length < 0.0005;
            } catch { return false; }
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

        // 点平面认孔：面上的圆孔、腰孔（0.9.1）全认出来当参考（**不当打孔面**）。
        // onlyRefs：参考孔页上点的 —— 用户就是要认孔，没认出孔也不往打孔面上拐；
        // 否则（预先选好的对象）一个孔都没有、或已选参考孔投到它的空位上，才当打孔面。
        void PickFaceWithHoles(object selected, bool onlyRefs){
            bool sheetMetal;
            var t = AutoHoleReader.ReadScanFace(selected, out sheetMetal);   // 钣金件的面也能认参考孔（0.10.2），只是不能当打孔面
            string how; int notHole, notSlot, odd;
            var holes = HoleScanCad.ScanFace(t, out how, out notHole);
            var faceSlots = HoleScanCad.ScanFaceSlots(t, out notSlot, out odd);
            string oddNote = odd > 0 ? odd + " 个两头不一样大的异形孔（葫芦孔 / 挂孔之类）认不了，没加进来" : "";
            notHole += notSlot;
            Log.Write("AutoHoleFaceScan", FaceLabel(t) + "：圆孔 " + holes.Count + "，腰孔 " + faceSlots.Count + "，判为凸台 " + notHole + (odd > 0 ? "，异形孔 " + odd : "") + (how.Length > 0 ? "，" + how : ""));
            if (holes.Count + faceSlots.Count == 0 && onlyRefs) {
                SetStatus("这张面上没有认出孔（" + FaceLabel(t) + (notHole > 0 ? "，" + notHole + " 个圆/腰形判定为凸台" : "") + (oddNote.Length > 0 ? "，" + oddNote : "") + "）。"
                          + "要在它上面打孔：点「下一步」到「打孔面」再点它。" + (how.Length > 0 ? "注意：" + how : ""), Ui.Warn);
                return;
            }
            if (holes.Count + faceSlots.Count == 0) {
                if (sheetMetal) throw new ArgumentException("这张钣金件的面上没有认出孔。" + AutoHoleReader.SheetMetalTargetMessage);
                AddTargetFace(selected, t);
                string inst = InstanceNote(t);
                SetStatus("这个面上没有识别到孔，已作为打孔面：" + FaceLabel(t) + (oddNote.Length > 0 ? "（" + oddNote + "）" : "") + (inst.Length > 0 ? "　⚠ " + inst : "") + (how.Length > 0 ? "　注意：" + how : ""), inst.Length > 0 ? Ui.Warn : Ui.Muted);
                return;
            }
            // 0.8.5：已经点好参考孔、再点要打孔的面，而这张面上本来就有几个别的孔（现场最常见：底板上早打过几个安装孔）——
            // 已选的参考孔里有能投到这张面上、而且投到的位置还空着的，这张面就是打孔面。
            // 0.9.1 起点鼠标的不走这里（按页面定），只剩预先选好的对象用这条
            int free = onlyRefs || sheetMetal ? 0 : FreeSpots(t, holes);
            if (free > 0) {
                AddTargetFace(selected, t);
                SetStatus("已作为打孔面：" + FaceLabel(t) + "（面上本来有 " + holes.Count + " 个孔，没有当参考孔；已选的参考孔里 " + free + " 个投到这张面的空位上）。"
                          + "要把面上的孔当参考孔，先在「打孔面」里移除这张面，再到「参考孔」那一步点它。" + (InstanceNote(t).Length > 0 ? "　⚠ " + InstanceNote(t) : ""), InstanceNote(t).Length > 0 ? Ui.Warn : Ui.Ok);
                return;
            }
            int added = 0, dup = 0, slotsAdded = 0;
            foreach (var scanned in holes)
                if (AddHole(AutoHoleReader.ToReference(scanned), null, true)) added++; else dup++;
            foreach (var sl in faceSlots)
                if (slots.Any(x => x.Same(sl))) dup++; else { slots.Add(sl); slotsAdded++; }
            scannedPicks.Add(selected);      // 只用于高亮：让用户看得见"刚才点的是哪张面"
            lastScanNote = (holes.Count > 0 ? HoleScan.StatusLine(holes) : "识别到 0 个圆孔")
                         + (faceSlots.Count > 0 ? "；腰孔 " + faceSlots.Count + " 条（" + string.Join("、", faceSlots.GroupBy(x => x.Label).Select(x => x.Key + " ×" + x.Count()).ToArray()) + "，目标件上配同样的腰孔，通切）" : "")
                         + (dup > 0 ? "；其中 " + dup + " 个已经在列表里" : "")
                         + (notHole > 0 ? "；另有 " + notHole + " 个圆/腰形判定为凸台，已跳过" : "")
                         + (oddNote.Length > 0 ? "；" + oddNote : "")
                         + "。要把这张面当打孔面：到「打孔面」那一步再点它（会撤掉从它认进来的参考孔）。";
            SetStatus("已从 " + FaceLabel(t) + " 上识别到 " + holes.Count + " 个孔" + (faceSlots.Count > 0 ? "、" + faceSlots.Count + " 条腰孔" : "")
                      + "，共 " + groups.Count + " 组（新增 " + added + " 个" + (slotsAdded > 0 ? "、腰孔 " + slotsAdded + " 条" : "") + "）。", Ui.Ok);
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
            // 从这张面认进来的腰孔（同一个零件、中心在这张面上）
            foreach (var sl in slots.ToList()) {
                bool samePart = ReferenceEquals(sl.Owner, t.Part);
                if (!samePart) { try { samePart = string.Equals(((P.PartDocument)sl.Owner).FullName, t.Part.FullName, StringComparison.OrdinalIgnoreCase); } catch { } }
                if (!samePart || Math.Abs((sl.Center - t.Plane.Point).Dot(nrm)) > 0.0002 || !FaceFrameReader.ContainsPoint(t, sl.Center)) continue;
                slots.Remove(sl); slotPicks.Remove(sl); n++;
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
    }
}
