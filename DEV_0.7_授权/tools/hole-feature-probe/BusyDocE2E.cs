using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using TianGongCadSuite;
using A=SolidEdgeAssembly;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;
using F=SolidEdgeFramework;

// 0.7.12：实例链上的子装配在别的窗口（标签页）里也开着时，「自动打孔 · 保持关联」必须**拒绝**这一面、不把天工搅乱；之后照常能打。**只在副本上跑**（不保存）。
//   两个装配都打开：X（上一级）和 T（X 里的子装配，自己也开着窗口）。
//   ① 在 T 里挑一个没有链接、一张面能打 ≥2 个孔的零件；
//   ② 在 X 里对它打孔：实例链经过 T（T 开着自己的窗口）→ 插件要拒绝、说清楚关掉哪个窗口，一个孔都不动、活动文档还是 X、零件不停在原位编辑里；
//   ③ 回到 T 打同一批孔：全部挂上天工原生关联、进多孔特征、打完回到 T、看得见的窗口不多；撤销；
//   ④ 关掉 X 再打一遍（只开着 T 的常见情况）：同 ③。
// 用法: BusyDocE2E.exe <输出目录> <X 装配副本> <T 装配副本> [目标零件名关键字]
class BusyDocE2E {
    static F.Application app;
    static int fail = 0;
    static string panelLog = Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA"), @"TianGongCadSuite\panel.log");
    static void L(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    static void Check(bool ok, string label){ L((ok ? "PASS: " : "FAIL: ") + label); if (!ok) fail++; }
    static void Pump(int n){ for (int i = 0; i < n; i++) { try { app.DoIdle(); } catch { } try { Application.DoEvents(); } catch { } Thread.Sleep(60); } }
    static double D(Array a,int i){ return Convert.ToDouble(a.GetValue(i)); }
    static long LogLen(){ try { return new FileInfo(panelLog).Length; } catch { return 0; } }
    static void LogSince(long from){
        try {
            using (var fs = new FileStream(panelLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) {
                if (fs.Length <= from) return;
                fs.Seek(from, SeekOrigin.Begin);
                var text = new StreamReader(fs, Encoding.UTF8).ReadToEnd();
                foreach (var x in text.Split('\n').Select(x => x.TrimEnd('\r')).Where(x => x.Length > 0 && !x.StartsWith("   在 ")))
                    L("   日志 " + (x.Length > 20 ? x.Substring(20) : x).Substring(0, Math.Min(400, (x.Length > 20 ? x.Substring(20) : x).Length)));
            }
        } catch (Exception e) { L("   读日志失败 " + e.Message); }
    }
    static int HoleCount(P.PartDocument part){
        try {
            var m = (P.Model)part.Models.Item(1);
            int inputs = 0; for (int i = 1; i <= m.UserDefinedPatterns.Count; i++) inputs += m.UserDefinedPatterns.Item(i).NumberInputFeatures;
            return m.Holes.Count + inputs;
        } catch { return -1; }
    }
    static int CopyCount(P.PartDocument part){ try { return part.Constructions.InterpartConstructions.Count; } catch { return -1; } }
    static int UdpCount(P.PartDocument part){ try { return ((P.Model)part.Models.Item(1)).UserDefinedPatterns.Count; } catch { return -1; } }
    static string Windows(){
        var l = new List<string>();
        try { foreach (object w in app.Windows) { try { l.Add((string)((dynamic)w).Caption); } catch { l.Add("?"); } } } catch { }
        return l.Count + "［" + string.Join("｜", l.ToArray()) + "］";
    }
    static int WindowCount(){ int n = 0; try { foreach (object w in app.Windows) { try { if ((bool)((dynamic)w).Visible) n++; } catch { n++; } } } catch { return -1; } return n; }
    static string Ctx(P.PartDocument part){ string c = ""; try { bool h = false; part.HasInterpartLinks(ref h); if (h) part.GetInContextAssemblyNameForInterpartLinks(ref c); } catch { } return c ?? ""; }
    static bool HasLinks(P.PartDocument part){ bool h = false; try { part.HasInterpartLinks(ref h); } catch { } return h; }

    // 同 LinkedDrillE2E.TargetFace：参考孔轴线穿过、和轴线垂直、孔心离边界 ≥3mm、这个位置还没孔的平面
    static G.Face TargetFace(AssemblyPart ap, HoleRecord h){
        V3 c = ap.Placement.InversePoint(h.Center), ax = ap.Placement.InverseNormal(h.Axis).Unit();
        G.Face best = null; double bestT = double.MaxValue;
        var body = (G.Body)((P.Model)ap.Part.Models.Item(1)).Body;
        foreach (G.Face f in (G.Faces)body.get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll)) {
            var pl = f.Geometry as G.Plane; if (pl == null) continue;
            Array p = new double[3], n = new double[3]; pl.GetPlaneData(ref p, ref n);
            var nv = V3.From(n).Unit();
            double dn = ax.Dot(nv);
            if (Math.Abs(dn) < 0.999) continue;
            double t = (V3.From(p) - c).Dot(nv) / dn;
            if (Math.Abs(t) > 0.05) continue;
            var hit = c + ax * t;
            try {
                Array lo = new double[3], hi = new double[3]; f.GetRange(ref lo, ref hi);
                bool ok = true;
                for (int k = 0; k < 3; k++) { double lo_ = D(lo,k), hi_ = D(hi,k); if (hi_ - lo_ < 1e-6) continue; double v = k == 0 ? hit.X : k == 1 ? hit.Y : hit.Z; if (v < lo_ + 0.003 || v > hi_ - 0.003) ok = false; }
                if (!ok) continue;
            } catch { continue; }
            bool holed = false;
            foreach (G.Edge e in (G.Edges)f.Edges) {
                var ci = e.Geometry as G.Circle; if (ci == null) continue;
                Array cc = new double[3], ca = new double[3]; double r; ci.GetCircleData(ref cc, ref ca, out r);
                var d = V3.From(cc) - hit; double along = d.Dot(ax); var radial = d - ax * along;
                if (radial.Length < 0.001) { holed = true; break; }
            }
            if (holed) continue;
            if (Math.Abs(t) < bestT) { bestT = Math.Abs(t); best = f; }
        }
        return best;
    }

    // 目标零件上（按面分组）能打的参考孔：同一张面、轴线不重复
    static Dictionary<int, List<HoleRecord>> ByFace(AssemblyPart target, List<HoleRecord> holes){
        var byFace = new Dictionary<int, List<HoleRecord>>();
        // 先用零件包围盒粗筛（纯算术）：轴线在 ±50mm 内碰不到包围盒的孔不用逐面去比（逐面比是 COM，大零件一个要几十秒）
        Array blo = new double[3], bhi = new double[3];
        try { ((G.Body)((P.Model)target.Part.Models.Item(1)).Body).GetRange(ref blo, ref bhi); } catch { return byFace; }
        var place = target.Placement;
        foreach (var h in holes) {
            if (h.PartName == target.Name || h.Selection == null || h.DiameterMm < 3 || h.DiameterMm > 22) continue;
            V3 c0 = place.InversePoint(h.Center), a0 = place.InverseNormal(h.Axis).Unit();
            double t0 = -0.05, t1 = 0.05; bool hitBox = true;
            for (int k = 0; k < 3 && hitBox; k++) {
                double o = k == 0 ? c0.X : k == 1 ? c0.Y : c0.Z, d = k == 0 ? a0.X : k == 1 ? a0.Y : a0.Z;
                double lo = D(blo, k) - 0.0005, hi = D(bhi, k) + 0.0005;
                if (Math.Abs(d) < 1e-9) { if (o < lo || o > hi) hitBox = false; continue; }
                double ta = (lo - o) / d, tb = (hi - o) / d; if (ta > tb) { var x = ta; ta = tb; tb = x; }
                t0 = Math.Max(t0, ta); t1 = Math.Min(t1, tb); if (t0 > t1) hitBox = false;
            }
            if (!hitBox) continue;
            G.Face f = null; try { f = TargetFace(target, h); } catch { }
            if (f == null) continue;
            int id = f.ID; List<HoleRecord> l;
            if (!byFace.TryGetValue(id, out l)) { l = new List<HoleRecord>(); byFace[id] = l; }
            V3 ax = h.Axis.Unit();
            if (l.Any(o => { var d = o.Center - h.Center; return (d - ax * d.Dot(ax)).Length < 0.001; })) continue;
            if (l.Count < 12) l.Add(h);
        }
        return byFace;
    }

    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        TianGongCadSuite.Licensing.LicenseTestHooks.GateOverride = 0;
        BusyFilter.Register();
        string ownerPath = args[1], topPath = args[2], want = args.Length > 3 ? args[3] : "";
        try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); }
        catch (Exception e) { L("连不上 CAD：" + e.Message); return 2; }
        try { app.DisplayAlerts = false; } catch { }
        L("已连上 CAD " + app.Version + "　插件 DLL " + typeof(AutoHoleForm).Assembly.GetName().Version);
        A.AssemblyDocument owner = null, top = null;
        try {
            // 先开 T 再开 X（反过来开时 T 的窗口里只认出 24 个实例 —— 子装配没展开）
            top = (A.AssemblyDocument)app.Documents.Open(topPath); Pump(20);
            owner = (A.AssemblyDocument)app.Documents.Open(ownerPath); Pump(20);
            ((dynamic)top).Activate(); Pump(20);
            L("打开 X=" + Path.GetFileName(ownerPath) + "　T=" + Path.GetFileName(topPath) + "　活动 " + ((dynamic)app.ActiveDocument).Name + "　窗口 " + Windows());

            // ① 在 T 里挑目标零件 + 面
            List<string> warn; List<HoleRecord> shafts; List<AssemblyPart> parts;
            var holes = AutoHoleWriter.CollectAssemblyHoles(top, out warn, out shafts, out parts);
            L("T 里零件实例 " + parts.Count + "，孔 " + holes.Count);
            AssemblyPart target = null; List<HoleRecord> batch = null; int faceId = 0;
            var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var ap in parts) {
                string full = ""; try { full = ap.Part.FullName; } catch { continue; }
                if (!tried.Add(full)) continue;
                if (want.Length > 0 && full.IndexOf(want, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (HasLinks(ap.Part)) continue;
                if (parts.Count(p => { try { return string.Equals(p.Part.FullName, full, StringComparison.OrdinalIgnoreCase); } catch { return false; } }) != 1) continue;   // 只有一个实例的，免得认歧义
                Dictionary<int, List<HoleRecord>> bf; try { bf = ByFace(ap, holes); } catch { continue; }
                var best = bf.OrderByDescending(kv => kv.Value.Count).FirstOrDefault();
                if (best.Value != null) L("   候选 " + Path.GetFileName(full) + " 面 " + best.Key + " 上 " + best.Value.Count + " 个");
                if (best.Value == null || best.Value.Count < 2) continue;
                target = ap; batch = best.Value; faceId = best.Key; break;
            }
            if (target == null) { L("T 里找不到没有链接、一张面能打 ≥3 个孔的零件"); return 3; }
            string targetFull = target.Part.FullName;
            L("目标 " + target.Name + "（" + Path.GetFileName(targetFull) + "）　面 " + faceId + " 上 " + batch.Count + " 个参考孔　建链装配=" + Ctx(target.Part));

            // ② 在 X 里打：T 开着自己的窗口 → 拒绝
            ((dynamic)owner).Activate(); Pump(20);
            var holesX = AutoHoleWriter.CollectAssemblyHoles(owner, out warn, out shafts, out parts);
            var targetX = parts.FirstOrDefault(p => { try { return string.Equals(p.Part.FullName, targetFull, StringComparison.OrdinalIgnoreCase); } catch { return false; } });
            if (targetX == null) { L("X 里找不到目标零件"); fail++; return 1; }
            var first = batch[0];
            V3 localWant = target.Placement.InversePoint(first.Center), axWant = target.Placement.InverseNormal(first.Axis).Unit();
            HoleRecord firstX = holesX.FirstOrDefault(h => {
                if (h.Selection == null || h.PartName == targetX.Name) return false;
                V3 c = targetX.Placement.InversePoint(h.Center), a = targetX.Placement.InverseNormal(h.Axis).Unit();
                var d = c - localWant; return Math.Abs(a.Dot(axWant)) > 0.999 && (d - axWant * d.Dot(axWant)).Length < 0.0005 && Math.Abs(h.DiameterMm - first.DiameterMm) < 0.05;
            });
            if (firstX == null) { L("X 里找不到同一个参考孔"); fail++; return 1; }
            Drill("② 在 X 里打（T 也开着窗口，应拒绝）", owner, new List<HoleRecord>{ firstX }, targetX, TargetFace(targetX, firstX), false, true);

            // ③ 回到 T 打这一批
            ((dynamic)top).Activate(); Pump(20);
            Check(ReferenceEquals(app.ActiveDocument, top), "③ 之前活动文档是 T");
            for (int phase = 3; phase <= 4; phase++) {
                if (phase == 4) {
                    try { owner.Close(false); } catch (Exception e) { L("关 X 失败 " + e.Message); }
                    owner = null; Pump(20);
                    try { ((dynamic)top).Activate(); } catch { }
                    Pump(10);
                    L("④ 关掉 X 以后：活动 " + ((dynamic)app.ActiveDocument).Name + "　窗口 " + Windows());
                }
                parts = AssemblyPart.List(top, new List<string>());
                target = parts.First(p => { try { return string.Equals(p.Part.FullName, targetFull, StringComparison.OrdinalIgnoreCase); } catch { return false; } });
                var rest = batch.Where(h => { try { return TargetFace(target, h) != null; } catch { return false; } }).ToList();
                if (rest.Count == 0) { L("没有能打的参考孔了"); fail++; break; }
                Drill((phase == 3 ? "③ X 也开着，" : "④ 只开着 T，") + "在 T 里打 " + rest.Count + " 个", top, rest, target, TargetFace(target, rest[0]), true, false);
                Check(NativeLinkMath.SamePath(Ctx(target.Part), top.FullName) || Ctx(target.Part).Length == 0, "链接属于 T（撤销后可以没有）：" + Ctx(target.Part));
            }
        } catch (Exception e) {
            L("E2E 异常：" + e.GetType().Name + " 0x" + e.HResult.ToString("X8") + " " + e.Message + "\r\n" + e.StackTrace);
            fail++;
        } finally {
            try { if (top != null) top.Close(false); } catch { }
            try { if (owner != null) owner.Close(false); } catch { }
            Pump(4);
        }
        L(fail == 0 ? "BUSY-DOC E2E OK" : ("BUSY-DOC E2E FAILED " + fail));
        return fail == 0 ? 0 : 1;
    }

    static void Drill(string title, A.AssemblyDocument asm, List<HoleRecord> hs, AssemblyPart ap, G.Face face, bool undo, bool refuse){
        L(""); L("── " + title + "：参考孔 " + hs.Count + " 个 → " + ap.Name);
        int before = HoleCount(ap.Part), copiesBefore = CopyCount(ap.Part), udpBefore = UdpCount(ap.Part), winBefore = WindowCount();
        L("   开始前窗口 " + Windows());
        object faceRef = ap.Reference(face);
        AutoHoleForm form = null;
        long mark = LogLen();
        try {
            form = new AutoHoleForm(app, asm);
            form.SuppressDialogs = true;
            form.Show(); Pump(6);
            form.KeepLink = true;
            foreach (var h in hs) { form.AcceptPick(h.Selection); Pump(2); }
            form.ScanFaceOnPick = false;
            form.AcceptPick(faceRef); Pump(4);
            form.ScanFaceOnPick = true;
            L("   UI: " + form.UiSummary());
            var t0 = DateTime.Now;
            form.DrillForTest(); Pump(10);
            string res = form.LastResult ?? "";
            int after = HoleCount(ap.Part);
            L("   用时 " + (DateTime.Now - t0).TotalSeconds.ToString("0.0") + "s　孔 " + before + " → " + after + "　零件间复制 " + copiesBefore + " → " + CopyCount(ap.Part)
              + "　多孔特征 " + udpBefore + " → " + UdpCount(ap.Part) + "　窗口 " + Windows());
            L("   RESULT: " + res.Replace("\r\n", " ⏎ "));
            LogSince(mark);
            if (refuse) {
                Check(after == before, "拒绝了，一个孔都没打（" + before + " → " + after + "）");
                Check(res.IndexOf("另一个窗口") >= 0, "结果里说清楚是哪个窗口占着");
                Check(CopyCount(ap.Part) == copiesBefore, "没有建零件间复制");
            } else {
                Check(after - before == hs.Count, "打上了 " + hs.Count + " 个孔（" + before + " → " + after + "）");
                int k = res.IndexOf("天工原生关联 "); int linkedN = -1;
                if (k >= 0) { var mm = System.Text.RegularExpressions.Regex.Match(res.Substring(k), @"天工原生关联 (\d+) 个孔"); if (mm.Success) linkedN = int.Parse(mm.Groups[1].Value); }
                Check(linkedN == after - before, "全部挂上天工原生关联（" + linkedN + "）");
                if (hs.Count >= 2) Check(UdpCount(ap.Part) > udpBefore && res.IndexOf("没能合成") < 0, "进了多孔特征（不是单孔 + 编组）");
            }
            Check(ReferenceEquals(app.ActiveDocument, asm), "打完回到开始时的装配（" + Path.GetFileName(asm.FullName) + "）");
            bool notInPlace = true; try { notInPlace = !ap.Part.InPlaceActivated; } catch { }
            Check(notInPlace, "零件没有停在原位编辑里");
            Check(WindowCount() <= winBefore, "看得见的窗口没有变多（" + winBefore + " → " + WindowCount() + "）");
            if (undo && after > before) { form.UndoForTest(); Pump(8); L("   撤销后孔 " + HoleCount(ap.Part) + "，零件间复制 " + CopyCount(ap.Part)); Check(HoleCount(ap.Part) == before, "撤销干净"); }
        } catch (Exception e) {
            L("   异常：" + e.GetType().Name + " 0x" + e.HResult.ToString("X8") + " " + e.Message + "\r\n" + e.StackTrace); fail++;
            LogSince(mark);
        } finally { if (form != null) try { form.Close(); } catch { } Pump(4); }
    }
}

[ComImport, Guid("00000016-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IBusyMessageFilter {
    [PreserveSig] int HandleInComingCall(int type, IntPtr task, int ticks, IntPtr info);
    [PreserveSig] int RetryRejectedCall(IntPtr task, int ticks, int reject);
    [PreserveSig] int MessagePending(IntPtr task, int ticks, int pending);
}
sealed class BusyFilter : IBusyMessageFilter {
    [DllImport("ole32.dll")] static extern int CoRegisterMessageFilter(IBusyMessageFilter f, out IBusyMessageFilter old);
    static BusyFilter keep;
    public static void Register(){ keep = new BusyFilter(); IBusyMessageFilter old; CoRegisterMessageFilter(keep, out old); }
    public int HandleInComingCall(int a, IntPtr b, int c, IntPtr d){ return 0; }
    public int RetryRejectedCall(IntPtr a, int ticks, int reject){ return ticks < 120000 ? 200 : -1; }
    public int MessagePending(IntPtr a, int b, int c){ return 2; }
}
