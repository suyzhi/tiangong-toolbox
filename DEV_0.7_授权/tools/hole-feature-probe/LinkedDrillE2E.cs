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

// 0.7.11：带链接的零件（链接是在别的装配里建的）走「自动打孔 · 保持关联」的真实窗口路径。**只在副本上跑**（不保存）。
//   找一个别的零件上的孔，轴线穿过目标零件的一张平面 → AcceptPick(孔口圆边) → AcceptPick(面) → 打孔，
//   看打上几个、原生关联挂上没有、插件日志里报了什么；保持关联 / 不保持关联各一次。
// 用法: LinkedDrillE2E.exe <输出目录> <装配副本路径> <目标零件名关键字> [最多试几个参考孔]
class LinkedDrillE2E {
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
                var lines = text.Split('\n').Select(x => x.TrimEnd('\r')).Where(x => x.Length > 0 && !x.StartsWith("   在 ")).ToList();
                var seen = new Dictionary<string, int>();
                foreach (var x in lines) { string k = x.Length > 20 ? x.Substring(20) : x; int c; seen.TryGetValue(k, out c); seen[k] = c + 1; }
                foreach (var kv in seen) L("   日志 ×" + kv.Value + " " + (kv.Key.Length > 300 ? kv.Key.Substring(0, 300) : kv.Key));
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
    static int WindowCount(){ int n = 0; try { foreach (object w in app.Windows) { try { if ((bool)((dynamic)w).Visible) n++; } catch { n++; } } } catch { return -1; } return n; }

    // 目标零件上，参考孔轴线穿过、和轴线垂直的平面里离孔最近的那张（零件局部算）—— 同 FenceHoleE2E
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
            if (Math.Abs(t) > 0.05) continue;   // 离参考孔 50mm 以外的面不要（不是一对紧固孔）
            var hit = c + ax * t;
            try {
                Array lo = new double[3], hi = new double[3]; f.GetRange(ref lo, ref hi);
                const double tol = -0.003;   // 孔心离面的边界至少 3mm
                bool inside = hit.X >= D(lo,0) - tol && hit.X <= D(hi,0) + tol && hit.Y >= D(lo,1) - tol && hit.Y <= D(hi,1) + tol && hit.Z >= D(lo,2) - tol && hit.Z <= D(hi,2) + tol;
                bool flat = Math.Abs(D(hi,0) - D(lo,0)) < 1e-6 || Math.Abs(D(hi,1) - D(lo,1)) < 1e-6 || Math.Abs(D(hi,2) - D(lo,2)) < 1e-6;
                if (!inside && !flat) continue;
                if (!inside) {
                    // 面的包围盒在法向上是扁的：另外两个方向按 3mm 边距判
                    bool ok = true;
                    for (int k = 0; k < 3; k++) { double lo_ = D(lo,k), hi_ = D(hi,k); if (hi_ - lo_ < 1e-6) continue; double v = k == 0 ? hit.X : k == 1 ? hit.Y : hit.Z; if (v < lo_ + 0.003 || v > hi_ - 0.003) ok = false; }
                    if (!ok) continue;
                }
            } catch { continue; }
            // 这个位置目标零件上已经有孔了（圆边圆心就在轴线上）就不要
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

    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        TianGongCadSuite.Licensing.LicenseTestHooks.GateOverride = 0;
        DrillFilter.Register();
        string asmPath = args[1], want = args[2];
        int max = args.Length > 3 ? int.Parse(args[3]) : 2;
        try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); }
        catch (Exception e) { L("连不上 CAD：" + e.Message); return 2; }
        try { app.DisplayAlerts = false; } catch { }
        L("已连上 CAD " + app.Version + "　插件 DLL " + typeof(AutoHoleForm).Assembly.GetName().Version);
        A.AssemblyDocument asm = null;
        try {
            var t0 = DateTime.Now;
            asm = (A.AssemblyDocument)app.Documents.Open(asmPath);
            Pump(20);
            L("打开 " + Path.GetFileName(asmPath) + " 用时 " + (DateTime.Now - t0).TotalSeconds.ToString("0.0") + "s");
            List<string> warn; List<HoleRecord> shafts; List<AssemblyPart> parts;
            var holes = AutoHoleWriter.CollectAssemblyHoles(asm, out warn, out shafts, out parts);
            L("零件实例 " + parts.Count + "，孔 " + holes.Count);
            var target = want == "*" ? AutoPick(asm, parts, holes) : parts.FirstOrDefault(p => { try { return p.Part.FullName.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0; } catch { return false; } });
            if (target == null) { L("找不到含「" + want + "」的零件；装配里的零件："); foreach (var p in parts.Take(80)) L("   " + p.Name); return 3; }
            bool has = false; try { target.Part.HasInterpartLinks(ref has); } catch { }
            string ctx = ""; try { target.Part.GetInContextAssemblyNameForInterpartLinks(ref ctx); } catch { }
            L("目标 " + target.Name + "　HasInterpartLinks=" + has + "　建链装配=" + ctx + "　当前装配=" + asm.FullName);
            string targetFull = target.Part.FullName;
            var cands = new List<HoleRecord>();
            var own = holes.Where(x => x.PartName == target.Name).ToList();
            foreach (var h in holes) {
                if (h.PartName == target.Name || h.Selection == null) continue;
                // 目标零件在这根轴线上已经有孔了（配孔检查采到的目标零件的孔，装配坐标里比）就不要
                V3 ax = h.Axis.Unit();
                if (own.Any(o => { var d = o.Center - h.Center; var radial = d - ax * d.Dot(ax); return Math.Abs(o.Axis.Unit().Dot(ax)) > 0.999 && radial.Length < 0.001; })) continue;
                if (h.DiameterMm < 3 || h.DiameterMm > 22) continue;
                G.Face f = null; try { f = TargetFace(target, h); } catch { }
                if (f != null) cands.Add(h);
            }
            L("轴线穿过目标零件平面的参考孔 " + cands.Count + " 个");
            int tried = 0;
            if (args.Length > 4 && args[4] == "multi") {
                // 一次打一批（现场是 17 个孔一次打）：按落在哪张面分组，取孔最多的那张面，最多 20 个、轴线不重复
                var byFace = new Dictionary<int, List<HoleRecord>>();
                foreach (var h in cands) {
                    G.Face f = null; try { f = TargetFace(target, h); } catch { }
                    if (f == null) continue;
                    int id = f.ID; List<HoleRecord> l;
                    if (!byFace.TryGetValue(id, out l)) { l = new List<HoleRecord>(); byFace[id] = l; }
                    V3 ax = h.Axis.Unit();
                    if (l.Any(o => { var d = o.Center - h.Center; return (d - ax * d.Dot(ax)).Length < 0.001; })) continue;
                    if (l.Count < 20) l.Add(h);
                }
                var best = byFace.OrderByDescending(kv => kv.Value.Count).FirstOrDefault();
                L("一批打：面 " + best.Key + " 上 " + (best.Value == null ? 0 : best.Value.Count) + " 个参考孔（共 " + byFace.Count + " 张面）");
                foreach (var keep in new[]{ true, false }) {
                    if (best.Value == null) break;
                    parts = AssemblyPart.List(asm, new List<string>());
                    target = parts.First(p => { try { return string.Equals(p.Part.FullName, targetFull, StringComparison.OrdinalIgnoreCase); } catch { return false; } });
                    G.Face face = null; try { face = TargetFace(target, best.Value[0]); } catch { }
                    if (face == null) { L("面找不回来了"); fail++; break; }
                    tried++;
                    Drill(asm, best.Value, target, face, keep, tried);
                }
                cands.Clear();
            }
            foreach (var keep in new[]{ true, false }) {
                int runs = 0;
                foreach (var h in cands) {
                    if (runs >= max) break;
                    parts = AssemblyPart.List(asm, new List<string>());   // 每次现取（打过孔以后旧的实体/文档对象可能失效）
                    target = parts.First(p => { try { return string.Equals(p.Part.FullName, targetFull, StringComparison.OrdinalIgnoreCase); } catch { return false; } });
                    G.Face face = null; try { face = TargetFace(target, h); } catch { }
                    if (face == null) continue;
                    runs++; tried++;
                    Drill(asm, new List<HoleRecord>{ h }, target, face, keep, tried);
                }
            }
            if (tried == 0) { L("没有可用的参考孔"); fail++; }
        } catch (Exception e) {
            L("E2E 异常：" + e.GetType().Name + " 0x" + e.HResult.ToString("X8") + " " + e.Message + "\r\n" + e.StackTrace);
            fail++;
        } finally {
            try { if (asm != null) asm.Close(false); } catch { }
            Pump(4);
        }
        L(fail == 0 ? "LINKED-DRILL E2E OK" : ("LINKED-DRILL E2E FAILED " + fail));
        return fail == 0 ? 0 : 1;
    }

    // 关键字 "*"（0.7.12）：自动挑一个能验证"一个特征里很多关联孔"的零件 —— 只有一个实例、没有别处的链接，
    // 某一张面上有 ≥3 个参考孔：轴线穿过、目标零件这根轴线上还没孔、彼此不重叠。先用包围盒粗筛（纯算术），再逐面比。
    static AssemblyPart AutoPick(A.AssemblyDocument asm, List<AssemblyPart> parts, List<HoleRecord> holes){
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ap in parts) {
            string full = ""; try { full = ap.Part.FullName; } catch { continue; }
            if (!seen.Add(full)) continue;
            if (parts.Count(p => { try { return string.Equals(p.Part.FullName, full, StringComparison.OrdinalIgnoreCase); } catch { return false; } }) != 1) continue;
            bool has = false; try { ap.Part.HasInterpartLinks(ref has); } catch { }
            if (has) { string c = ""; try { ap.Part.GetInContextAssemblyNameForInterpartLinks(ref c); } catch { } if (!NativeLinkMath.SamePath(c, asm.FullName)) continue; }
            Array blo = new double[3], bhi = new double[3];
            try { ((G.Body)((P.Model)ap.Part.Models.Item(1)).Body).GetRange(ref blo, ref bhi); } catch { continue; }
            var own = holes.Where(x => x.PartName == ap.Name).ToList();
            var byFace = new Dictionary<int, List<HoleRecord>>();
            foreach (var h in holes) {
                if (h.PartName == ap.Name || h.Selection == null || h.DiameterMm < 3 || h.DiameterMm > 22) continue;
                V3 c0 = ap.Placement.InversePoint(h.Center), a0 = ap.Placement.InverseNormal(h.Axis).Unit();
                double t0 = -0.05, t1 = 0.05; bool hitBox = true;
                for (int k = 0; k < 3 && hitBox; k++) {
                    double o = k == 0 ? c0.X : k == 1 ? c0.Y : c0.Z, d = k == 0 ? a0.X : k == 1 ? a0.Y : a0.Z;
                    double lo = D(blo, k) - 0.0005, hi = D(bhi, k) + 0.0005;
                    if (Math.Abs(d) < 1e-9) { if (o < lo || o > hi) hitBox = false; continue; }
                    double ta = (lo - o) / d, tb = (hi - o) / d; if (ta > tb) { var x = ta; ta = tb; tb = x; }
                    t0 = Math.Max(t0, ta); t1 = Math.Min(t1, tb); if (t0 > t1) hitBox = false;
                }
                if (!hitBox) continue;
                V3 ax = h.Axis.Unit();
                if (own.Any(o => { var d = o.Center - h.Center; return Math.Abs(o.Axis.Unit().Dot(ax)) > 0.999 && (d - ax * d.Dot(ax)).Length < 0.001; })) continue;
                G.Face f = null; try { f = TargetFace(ap, h); } catch { }
                if (f == null) continue;
                List<HoleRecord> l; if (!byFace.TryGetValue(f.ID, out l)) { l = new List<HoleRecord>(); byFace[f.ID] = l; }
                // 和已收的同轴 = 重复；圆心距不到两孔半径和 + 1mm = 重叠（腰孔两头）：都不要
                if (l.Any(o => { var d = o.Center - h.Center; double r = (d - ax * d.Dot(ax)).Length; return r < (o.DiameterMm + h.DiameterMm) / 2000.0 * 1.2 + 0.001; })) continue;
                if (l.Count < 20) l.Add(h);
            }
            var best = byFace.OrderByDescending(kv => kv.Value.Count).FirstOrDefault();
            if (best.Value != null) L("   候选 " + Path.GetFileName(full) + " 面 " + best.Key + " 上 " + best.Value.Count + " 个");
            if (best.Value != null && best.Value.Count >= 3) { L("自动挑中 " + ap.Name); return ap; }
        }
        return null;
    }

    static void Drill(A.AssemblyDocument asm, List<HoleRecord> hs, AssemblyPart ap, G.Face face, bool keep, int n){
        L(""); L("── 第 " + n + " 次（" + (keep ? "保持关联" : "不保持关联") + "）：参考孔 " + hs.Count + " 个（" + string.Join("；", hs.Take(4).Select(x => x.ToString()).ToArray()) + (hs.Count > 4 ? "…" : "") + "）→ " + ap.Name);
        int before = HoleCount(ap.Part), copiesBefore = CopyCount(ap.Part), udpBefore = UdpCount(ap.Part), winBefore = WindowCount();
        object faceRef = ap.Reference(face);
        AutoHoleForm form = null;
        long mark = LogLen();
        try {
            form = new AutoHoleForm(app, asm);
            form.SuppressDialogs = true;
            form.Show(); Pump(6);
            form.KeepLink = keep;
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
              + "　多孔特征 " + udpBefore + " → " + UdpCount(ap.Part) + "　窗口 " + winBefore + " → " + WindowCount());
            string ctxAfter = ""; try { bool h2 = false; ap.Part.HasInterpartLinks(ref h2); if (h2) ap.Part.GetInContextAssemblyNameForInterpartLinks(ref ctxAfter); } catch { }
            L("   打完后建链装配：" + ctxAfter);
            L("   RESULT: " + res.Replace("\r\n", " ⏎ "));
            LogSince(mark);
            bool already = res.IndexOf("本来就有孔") >= 0 && after == before;
            Check((res.IndexOf("共打孔") >= 0 && after > before) || already, "第 " + n + " 次：" + (already ? "位置上本来就有孔，跳过" : "打上了孔（" + before + " → " + after + "）"));
            Check(ReferenceEquals(app.ActiveDocument, asm), "第 " + n + " 次：打完回到顶层装配");
            bool notInPlace = true; try { notInPlace = !ap.Part.InPlaceActivated; } catch { }
            Check(notInPlace, "第 " + n + " 次：零件没有停在原位编辑里");
            Check(WindowCount() <= winBefore, "第 " + n + " 次：看得见的窗口没有变多（" + winBefore + " → " + WindowCount() + "）");
            if (hs.Count >= 2 && after > before) Check(UdpCount(ap.Part) > udpBefore && res.IndexOf("没能合成") < 0, "第 " + n + " 次：孔进了多孔特征（不是单孔 + 编组）");
            if (keep && after > before && res.IndexOf("挂不了新的关联") < 0) {
                int k = res.IndexOf("天工原生关联 "); int linkedN = -1;
                if (k >= 0) { var mm = System.Text.RegularExpressions.Regex.Match(res.Substring(k), @"天工原生关联 (\d+) 个孔"); if (mm.Success) linkedN = int.Parse(mm.Groups[1].Value); }
                Check(linkedN == after - before, "第 " + n + " 次：打上的 " + (after - before) + " 个孔全部挂上天工原生关联（" + linkedN + "）");
            }
            if (after > before) { form.UndoForTest(); Pump(8); L("   撤销后孔 " + HoleCount(ap.Part) + "，零件间复制 " + CopyCount(ap.Part)); }
        } catch (Exception e) {
            L("   异常：" + e.GetType().Name + " 0x" + e.HResult.ToString("X8") + " " + e.Message + "\r\n" + e.StackTrace); fail++;
            LogSince(mark);
        } finally { if (form != null) try { form.Close(); } catch { } Pump(4); }
    }
}

[ComImport, Guid("00000016-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IDrillMessageFilter {
    [PreserveSig] int HandleInComingCall(int type, IntPtr task, int ticks, IntPtr info);
    [PreserveSig] int RetryRejectedCall(IntPtr task, int ticks, int reject);
    [PreserveSig] int MessagePending(IntPtr task, int ticks, int pending);
}
sealed class DrillFilter : IDrillMessageFilter {
    [DllImport("ole32.dll")] static extern int CoRegisterMessageFilter(IDrillMessageFilter f, out IDrillMessageFilter old);
    static DrillFilter keep;
    public static void Register(){ keep = new DrillFilter(); IDrillMessageFilter old; CoRegisterMessageFilter(keep, out old); }
    public int HandleInComingCall(int a, IntPtr b, int c, IntPtr d){ return 0; }
    public int RetryRejectedCall(IntPtr a, int ticks, int reject){ return ticks < 120000 ? 200 : -1; }
    public int MessagePending(IntPtr a, int b, int c){ return 2; }
}

