using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using TianGongCadSuite;
using A=SolidEdgeAssembly;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;
using F=SolidEdgeFramework;

// 0.7.9 用户真实围栏装配上的自动打孔（原生关联 + 多孔合一）。**只在副本上跑**：先把整个目录复制到输出目录。
//   ① 打开副本 → 配孔检查，列出问题（计时）
//   ② 取前几条「漏打孔」：点参考孔的孔口圆边（走 AcceptPick，和用户点一样）→ 在没打孔的零件上找轴线穿过的那张平面、点它 → 打孔
//      看：打了几个、挂上原生关联几个、合成了多孔特征没有、跨子装配的两级原位编辑、多实例零件、多实体件
//   ③ 同步孔位（UpdateAll）→ 撤销本次打孔 → 那个零件的孔特征数回到打孔前
// 用法: FenceHoleE2E.exe <输出目录> [围栏目录] [最多几条]
class FenceHoleE2E {
    static F.Application app;
    static int fail = 0;
    static void L(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    static void Check(bool ok, string label){ L((ok ? "PASS: " : "FAIL: ") + label); if (!ok) fail++; }
    static void Pump(int n){ for (int i = 0; i < n; i++) { try { app.DoIdle(); } catch { } try { Application.DoEvents(); } catch { } Thread.Sleep(60); } }
    static double D(Array a,int i){ return Convert.ToDouble(a.GetValue(i)); }
    static void Retry(Action a, string what){
        Exception last = null;
        for (int i = 0; i < 8; i++) {
            try { a(); return; }
            catch (Exception e) { last = e; L("   " + what + " 被 CAD 拒绝（0x" + e.HResult.ToString("X8") + "），重试 " + (i + 1)); Pump(8); }
        }
        throw last;
    }
    static void CopyDir(string from, string to){
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
        foreach (var d in Directory.GetDirectories(from)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
    }

    // 孔特征数：单孔 + 多孔特征的输入孔；多孔特征数
    static string Count(P.PartDocument part, out int holes){
        holes = -1;
        try {
            var m = (P.Model)part.Models.Item(1);
            int udp = m.UserDefinedPatterns.Count, inputs = 0;
            for (int i = 1; i <= udp; i++) inputs += m.UserDefinedPatterns.Item(i).NumberInputFeatures;
            holes = m.Holes.Count + inputs;
            int copies = 0; try { copies = part.Constructions.InterpartConstructions.Count; } catch { }
            return "单孔 " + m.Holes.Count + "、多孔特征 " + udp + "（" + inputs + " 个孔）、零件间复制 " + copies + "、建模 " + part.ModelingMode + "、实体模型 " + part.Models.Count;
        } catch (Exception e) { return "读不出：" + e.Message; }
    }

    // 目标零件上，参考孔轴线穿过、和轴线垂直、离孔最近的那张平面（零件局部算）
    static G.Face TargetFace(AssemblyPart ap, HoleRecord h, out string why){
        why = "";
        V3 c = ap.Placement.InversePoint(h.Center), ax = ap.Placement.InverseNormal(h.Axis).Unit();
        G.Face best = null; double bestT = double.MaxValue;
        var body = (G.Body)((P.Model)ap.Part.Models.Item(1)).Body;   // 每次现取：打过孔/撤销以后 ap.Body 已经失效（E_FAIL）
        foreach (G.Face f in (G.Faces)body.get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll)) {
            var pl = f.Geometry as G.Plane; if (pl == null) continue;
            Array p = new double[3], n = new double[3]; pl.GetPlaneData(ref p, ref n);
            var nv = V3.From(n).Unit();
            double dn = ax.Dot(nv);
            if (Math.Abs(dn) < 0.999) continue;
            double t = (V3.From(p) - c).Dot(nv) / dn;
            var hit = c + ax * t;
            try {
                Array lo = new double[3], hi = new double[3]; f.GetRange(ref lo, ref hi);
                const double tol = 0.0002;
                bool inside = hit.X >= D(lo,0) - tol && hit.X <= D(hi,0) + tol && hit.Y >= D(lo,1) - tol && hit.Y <= D(hi,1) + tol && hit.Z >= D(lo,2) - tol && hit.Z <= D(hi,2) + tol;
                if (!inside) continue;
            } catch { }
            if (Math.Abs(t) < bestT) { bestT = Math.Abs(t); best = f; }
        }
        if (best == null) why = "轴线穿过的垂直平面一张都没有";
        return best;
    }

    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        TianGongCadSuite.Licensing.LicenseTestHooks.GateOverride = 0;
        FenceFilter.Register();   // 进程外调 CAD：CAD 忙时 COM 自动等（第 4 次跑配孔检查撞上 RPC_E_CALL_REJECTED、弹框卡住）
        string outDir = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
        int num;
        bool countOnly = args.Length > 1 && int.TryParse(args[1].Trim(), out num);   // FenceHoleE2E.exe <输出> 5 = 默认围栏、最多 5 条
        string src = args.Length > 1 && args[1].Trim().Length > 0 && !countOnly ? args[1] : @"H:\桌面\测试\SK26.01-OK310-03-001";
        int max = countOnly ? int.Parse(args[1].Trim()) : args.Length > 2 ? int.Parse(args[2]) : 3;
        Directory.CreateDirectory(outDir);
        try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); }
        catch (Exception e) { L("连不上 CAD：" + e.Message); return 2; }
        try { app.DisplayAlerts = false; } catch { }
        L("已连上 CAD " + app.Version);
        string copy = Path.Combine(outDir, "fence-" + DateTime.Now.ToString("HHmmss"));
        L("复制 " + src + " → " + copy);
        CopyDir(src, copy);
        string asmPath = Directory.GetFiles(copy, "SK26.01-OK310-03-001.asm").FirstOrDefault() ?? Directory.GetFiles(copy, "*.asm").First();
        A.AssemblyDocument asm = null;
        AutoHoleCheckForm cf = null;
        try {
            var t0 = DateTime.Now;
            Retry(delegate { asm = (A.AssemblyDocument)app.Documents.Open(asmPath); }, "打开副本");
            Pump(20);
            L("打开 " + Path.GetFileName(asmPath) + " 用时 " + (DateTime.Now - t0).TotalSeconds.ToString("0.0") + "s");

            L(""); L("① 配孔检查");
            Retry(delegate { cf = new AutoHoleCheckForm(app, asm); }, "打开配孔检查");
            Retry(delegate { cf.Show(); }, "显示"); Pump(6);
            t0 = DateTime.Now;
            Retry(delegate { cf.RunCheckForTest(); }, "开始检查"); Pump(6);
            var issues = cf.IssuesForTest ?? new List<HoleIssue>();
            L("   用时 " + (DateTime.Now - t0).TotalSeconds.ToString("0.0") + "s　孔 " + cf.HolesForTest.Count + " 个　" + cf.StatusForTest);
            foreach (var g in issues.GroupBy(i => i.KindName)) L("   " + g.Key + "：" + g.Count() + " 条");
            foreach (var it in issues.Take(12)) L("   · " + it.KindName + "　" + it.Message + (it.MissingPart.Length > 0 ? "　缺：" + it.MissingPart : ""));
            Check(issues != null, "配孔检查跑完（没有异常）");

            var parts = AssemblyPart.List(asm, new List<string>());
            L("   零件实例 " + parts.Count + " 个（子装配里的 " + parts.Count(p => p.Nested) + " 个）");
            var missing = issues.Where(i => i.Kind == HoleIssueKind.MissingHole && i.Holes.Count > 0 && i.Holes[0].Selection != null).ToList();
            // 跨子装配的优先（参考孔和缺孔零件不在同一个子装配里）
            missing = missing.OrderByDescending(i => Top(i.Holes[0].PartName) != Top(i.MissingPart)).ToList();
            int done = 0;
            foreach (var it in missing) {
                if (done >= max) break;
                parts = AssemblyPart.List(asm, new List<string>());   // 每条现取：打过孔/撤销以后旧的实体、文档对象可能失效（E_FAIL）
                var ap = parts.FirstOrDefault(p => p.Name == it.MissingPart);
                if (ap == null) { L("   （跳过：零件表里找不到「" + it.MissingPart + "」）"); continue; }
                done++;
                Drill(asm, it, ap, done);
            }
            if (done == 0) L("   没有能自动配的「漏打孔」—— 打孔部分没跑");
        } catch (Exception e) {
            L("E2E 异常：" + e.GetType().Name + ": " + e.Message + "\r\n" + e.StackTrace);
            fail++;
        } finally {
            if (cf != null) try { cf.Close(); } catch { }
            try { if (asm != null) asm.Close(false); } catch { }
            Pump(4);
        }
        L("");
        L(fail == 0 ? "FENCE-HOLE E2E OK" : ("FENCE-HOLE E2E FAILED " + fail));
        return fail == 0 ? 0 : 1;
    }
    static string Top(string path){ int i = path.IndexOfAny(new[]{ '/', '\\', '>' }); return i < 0 ? path : path.Substring(0, i); }

    static void Drill(A.AssemblyDocument asm, HoleIssue it, AssemblyPart ap, int n){
        var h = it.Holes[0];
        L(""); L("② 第 " + n + " 条：" + it.Message);
        L("   参考孔 " + h + "　（参考孔 " + it.Holes.Count + " 个）→ 缺孔零件 " + it.MissingPart + (ap.Nested ? "（子装配里）" : "") + "　跨子装配=" + (Top(h.PartName) != Top(it.MissingPart)));
        int before; string sb = Count(ap.Part, out before);
        L("   打孔前：" + sb);
        string why;
        var face = TargetFace(ap, h, out why);
        if (face == null) { L("   （跳过：" + why + "）"); return; }
        object faceRef = ap.Reference(face);
        AutoHoleForm form = null;
        try {
            Retry(delegate { form = new AutoHoleForm(app, asm); }, "打开自动打孔");
            form.SuppressDialogs = true;   // 撤销的确认框在私有桌面上没人点（第一次跑就卡在这里）
            Retry(delegate { form.Show(); }, "显示"); Pump(6);
            Retry(delegate { form.AcceptPick(h.Selection); }, "点参考孔圆边"); Pump(4);
            form.ScanFaceOnPick = false;
            Retry(delegate { form.AcceptPick(faceRef); }, "点缺孔零件的面"); Pump(4);
            form.ScanFaceOnPick = true;
            L("   UI: " + form.UiSummary());
            var t0 = DateTime.Now;
            Retry(delegate { form.DrillForTest(); }, "打孔"); Pump(10);
            string res = form.LastResult ?? "";
            L("   用时 " + (DateTime.Now - t0).TotalSeconds.ToString("0.0") + "s　RESULT: " + res.Replace("\r\n", " ⏎ "));
            int after; string sa = Count(ap.Part, out after);
            L("   打孔后：" + sa);
            bool already = res.IndexOf("本来就有孔") >= 0 && after == before;   // 插件认出这个位置已有同轴孔（规格不同）而跳过，也是对的
            Check((res.IndexOf("共打孔") >= 0 && after > before) || already, "第 " + n + " 条：" + (already ? "位置上本来就有孔，跳过" : "打上了孔（" + before + " → " + after + "）"));
            L("   原生关联：" + (res.IndexOf("天工原生关联") >= 0 ? "挂上了" : "没挂上") + "　多孔合一：" + (res.IndexOf("没能合成") >= 0 ? "没合成" : "（见上）"));
            Check(ReferenceEquals(app.ActiveDocument, asm), "第 " + n + " 条：打完回到顶层装配");
            Retry(delegate { form.SyncForTest(); }, "同步孔位"); Pump(8);
            int synced; Count(ap.Part, out synced);
            Check(synced == after, "第 " + n + " 条：同步孔位后孔数不变（" + synced + "）");
            Retry(delegate { form.UndoForTest(); }, "撤销"); Pump(8);
            int undone; string su = Count(ap.Part, out undone);
            L("   撤销后：" + su + "　" + (form.LastResult ?? "").Replace("\r\n", " ⏎ "));
            Check(undone == before, "第 " + n + " 条：撤销后孔数回到 " + before + "（实 " + undone + "）");
        } catch (Exception e) {
            L("   异常：" + e.GetType().Name + " 0x" + e.HResult.ToString("X8") + " " + e.Message); fail++;
        } finally { if (form != null) try { form.Close(); } catch { } Pump(4); }
    }
}

// 进程外调 CAD：CAD 忙（RPC_E_CALL_REJECTED / RETRYLATER）时由 COM 自动等，最多 120 秒（同 AsmLinkProbe 的 ProbeFilter）
[ComImport, Guid("00000016-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IFenceMessageFilter {
    [PreserveSig] int HandleInComingCall(int type, IntPtr task, int ticks, IntPtr info);
    [PreserveSig] int RetryRejectedCall(IntPtr task, int ticks, int reject);
    [PreserveSig] int MessagePending(IntPtr task, int ticks, int pending);
}
sealed class FenceFilter : IFenceMessageFilter {
    [DllImport("ole32.dll")] static extern int CoRegisterMessageFilter(IFenceMessageFilter f, out IFenceMessageFilter old);
    static FenceFilter keep;
    public static void Register(){ keep = new FenceFilter(); IFenceMessageFilter old; CoRegisterMessageFilter(keep, out old); }
    public int HandleInComingCall(int a, IntPtr b, int c, IntPtr d){ return 0; }
    public int RetryRejectedCall(IntPtr a, int ticks, int reject){ return ticks < 120000 ? 200 : -1; }
    public int MessagePending(IntPtr a, int b, int c){ return 2; }
}