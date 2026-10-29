using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using A=SolidEdgeAssembly;
using D=SolidEdgeDraft;
using F=SolidEdgeFramework;

// 0.10.0 批量改名端到端（真天工，可见 CAD + 对话框监视）：
//   audit  —— 初筛核对：文件夹里每个文件，按字节初筛 vs 设计管理器列出的链接，漏一个就 FAIL；
//   main   —— 打开装配 + 一张工程图 → 窗口真实路径「查找替换」把 YF25.07G01-L1-M30-02 换成 SK99-TEST → 改名；
//             核对：装配重新打开、0 缺件、全是新名字；改名的工程图链到新零件、视图不过期；同文件夹里没改名的兄弟装配也 0 缺件；
//             再把 SK99-TEST 只改大小写成 sk99-test（走临时名两步改名），同样核对；
//   lock   —— 占住一个零件再改名：必须整体不改（文件名、引用原样），装配重新打开；
//   dirty  —— 改了装配里零件的内存属性（像属性卡那样）不保存就改名：必须拦下来。
// 只在测试副本里改：窗口只留副本文件夹里的文件（装配还引用着副本外面的原件）。
// 用法: RenameE2E.exe <输出目录> <模式> <副本文件夹>     （经 run-cad-queue.ps1 在私有桌面跑；注册的要是开发构建）
class RenameE2E {
    const string AddInGuid = "{8C05165C-65A4-4EF2-A138-508589D82004}";
    const string Asm = "YF25.07G01-L1-M30-02.asm", Dft = "YF25.07G01-L1-M30-02-07.dft", Sibling = "YF25.07兼容小车手推车.asm";
    static readonly string[] Siblings = { "车把手.asm", "定位滚轮.asm", "聚氧模组侧向支撑子装配.asm", "快拆侧向支撑.asm", "铭牌.asm", "支撑板.asm", "MC模组侧向支撑.asm", "YF25.07兼容小车手推车.asm", "YF25.07兼容小车托盘部分.asm" };
    static F.Application app;
    static dynamic diag;
    static int fails;
    static readonly List<string> dialogs = new List<string>();
    static void L(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    static void Check(bool ok, string what){ L((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; }
    static T Try<T>(Func<T> f, T d){ try { return f(); } catch { return d; } }
    static void Idle(int n){ for (int i = 0; i < n; i++) { try { app.DoIdle(); } catch { } Application.DoEvents(); Thread.Sleep(50); } }

    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = Encoding.UTF8;
        string mode = args[1], dir = Path.GetFullPath(args[2]);
        E2EFilter.Register();
        try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); } catch (Exception e) { L("连不上 CAD：" + e.Message); return 2; }
        object blank = null;
        try { blank = app.Documents.Add("SolidEdge.AssemblyDocument"); } catch { }
        dynamic addin = null;
        var until = DateTime.Now.AddSeconds(60);
        while (addin == null && DateTime.Now < until) {
            try { foreach (dynamic a in (dynamic)app.AddIns) { if (string.Equals((string)a.GUID, AddInGuid, StringComparison.OrdinalIgnoreCase)) { addin = a; break; } } } catch { }
            if (addin == null) Thread.Sleep(1000);
        }
        if (addin == null) { L("找不到插件"); return 2; }
        addin.Connect = true;
        for (int i = 0; i < 30 && diag == null; i++) { try { diag = addin.Object; } catch { } if (diag == null) { Idle(5); Thread.Sleep(1000); } }
        if (diag == null) { L("插件没有给出 Diagnostics（注册的不是开发构建？）"); return 2; }
        L("插件 DLL：" + (string)diag.LoadedLibrary);
        try { ((dynamic)blank).Close(false); } catch { }
        var watcher = new Thread(Watch) { IsBackground = true }; watcher.Start();
        try {
            if (mode == "audit") Audit(dir);
            else if (mode == "main") MainRun(dir);
            else if (mode == "lock") LockRun(dir);
            else if (mode == "dirty") DirtyRun(dir);
            else if (mode == "stale") StaleRun(dir);
            else if (mode == "propcard") PropCardRun(dir, args.Length > 3 ? args[3] : Asm);
            else if (mode == "sibling") SiblingRun(dir, args.Length > 3 ? args[3] : "");
            else if (mode == "generic") GenericRun(dir, args[3], args[4], args[5]);
        } catch (Exception e) { L("FAIL 异常 " + e); fails++; }
        stop = true;
        L("对话框 " + dialogs.Count + " 个" + (dialogs.Count > 0 ? "：" + string.Join(" | ", dialogs.ToArray()) : ""));
        L("RESULT " + (fails == 0 ? "ALL PASS" : fails + " FAIL"));
        return fails == 0 ? 0 : 1;
    }

    static string Wait(int minutes){
        var deadline = DateTime.Now.AddMinutes(minutes);
        while (DateTime.Now < deadline) {
            Thread.Sleep(1000);
            string t = Try(() => (string)diag.Result, "");
            if (!string.IsNullOrEmpty(t)) return t;
        }
        return "TIMEOUT";
    }

    static void Audit(string dir){
        diag.StartRenameFilterAudit(dir);
        string r = Wait(10);
        L(r);
        Check(r.Contains(" missed=0 "), "初筛不漏任何一个链接");
        Check(r.Contains(" encryptedInProc=0 "), "天工进程里读加密文件是明文（初筛有效）");
    }

    static object Open(string path){ var d = app.Documents.Open(path); Idle(20); return d; }
    static void CloseAll(){ try { var docs = app.Documents; for (int i = docs.Count; i >= 1; i--) { try { ((dynamic)docs.Item(i)).Close(false); } catch { } } } catch { } Idle(10); }

    // 窗口回报的改名计划："PLAN a.par→b.par ; c.dft→d.dft"
    static List<KeyValuePair<string, string>> Plan(string r){
        var list = new List<KeyValuePair<string, string>>();
        foreach (var line in r.Split('\n')) {
            if (!line.StartsWith("PLAN ")) continue;
            foreach (var part in line.Substring(5).Split(new[] { " ; " }, StringSplitOptions.RemoveEmptyEntries)) {
                var ab = part.Split('→');
                if (ab.Length == 2) list.Add(new KeyValuePair<string, string>(ab[0].Trim(), ab[1].Trim()));
            }
        }
        return list;
    }

    // 逐层列实例：c[0] 实例数、c[1] 缺件、c[2] 链着 news 里的名字、c[3] 链着 olds 里的名字（只数副本文件夹里的）
    static void Walk(A.AssemblyDocument asm, string dir, ICollection<string> news, ICollection<string> olds, int[] c, HashSet<string> seen){
        foreach (A.Occurrence o in asm.Occurrences) {
            c[0]++;
            string f = Try(() => o.OccurrenceFileName, "?");
            object doc = Try<object>(() => o.OccurrenceDocument, null);
            if (!File.Exists(f) || doc == null) { c[1]++; L("   缺 " + f); }
            string n = Path.GetFileName(f);
            if (f.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) {
                if (news.Contains(n)) c[2]++;
                if (olds.Contains(n)) c[3]++;
            }
            if (Try(() => o.Subassembly, false) && seen.Add(f)) { var s = doc as A.AssemblyDocument; if (s != null) Walk(s, dir, news, olds, c, seen); }
        }
    }
    static int[] WalkFile(string path, string dir, ICollection<string> news, ICollection<string> olds){
        var doc = Open(path) as A.AssemblyDocument;
        var c = new int[4];
        if (doc == null) { L("   打不开 " + path); c[1] = -1; return c; }
        Walk(doc, dir, news, olds, c, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        return c;
    }
    static HashSet<string> Names(IEnumerable<string> s, bool ordinal){ return new HashSet<string>(s, ordinal ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase); }

    // 改了名的工程图：每个链接的文件都在、不是已经改走的旧名字；视图不过期
    // 改名前每张工程图里过期的视图数（原件里本来就有过期的，改名不该让它变多）
    static readonly Dictionary<string, int> staleBefore = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    static int StaleOf(string f){
        object doc = null; int stale = 0;
        try { doc = Open(f); foreach (D.Sheet sh in ((D.DraftDocument)doc).Sheets) foreach (D.DrawingView v in sh.DrawingViews) if (!Try(() => v.IsUpToDate, true)) stale++; }
        catch { stale = -1; }
        finally { try { if (doc != null) ((dynamic)doc).Close(false); } catch { } Idle(3); }
        return stale;
    }
    static void CheckDrafts(string dir, List<KeyValuePair<string, string>> plan){
        var olds = Names(plan.Select(p => p.Key), true);
        var drafts = plan.Where(p => p.Value.EndsWith(".dft", StringComparison.OrdinalIgnoreCase)).Select(p => Path.Combine(dir, p.Value)).ToList();
        int bad = 0, views = 0, stale = 0, worse = 0;
        var oldOf = plan.ToDictionary(p => p.Value, p => p.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var f in drafts) {
            int mine = 0;
            object doc = null;
            try {
                doc = Open(f);
                var dft = (D.DraftDocument)doc;
                var links = dft.ModelLinks;
                for (int i = 1; i <= links.Count; i++) {
                    string fn = Try(() => links.Item(i).FileName, "?");
                    if (!File.Exists(fn) || olds.Contains(Path.GetFileName(fn))) { bad++; L("   " + Path.GetFileName(f) + " 链到 " + fn); }
                }
                foreach (D.Sheet sh in dft.Sheets) foreach (D.DrawingView v in sh.DrawingViews) { views++; if (!Try(() => v.IsUpToDate, true)) { stale++; mine++; } }
                int before; string o = oldOf[Path.GetFileName(f)];
                // 两轮改名：第二轮的旧名字是第一轮的新名字，一路追回原件的名字
                if (!staleBefore.ContainsKey(o) && firstPlan != null && firstPlan.ContainsKey(o)) o = firstPlan[o];
                if (staleBefore.TryGetValue(o, out before) && mine > before) { worse++; L("   " + Path.GetFileName(f) + " 过期视图 " + before + " → " + mine); }
            } catch (Exception e) { bad++; L("   打不开 " + f + "：" + e.Message); }
            finally { try { if (doc != null) ((dynamic)doc).Close(false); } catch { } Idle(3); }
        }
        L("   工程图 " + drafts.Count + " 张，视图 " + views + "，过期 " + stale);
        Check(drafts.Count >= 20 && bad == 0, "改了名的工程图（" + drafts.Count + " 张）都链到改名后的模型");
        Check(worse == 0, "改名没有让任何工程图视图过期（过期 " + stale + " 个，都是原件里本来就过期的）");
    }

    static bool Encrypted(string f){ var b = new byte[11]; using (var s = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) s.Read(b, 0, 11); return Encoding.ASCII.GetString(b) == "%TSD-Header"; }

    static void CheckFiles(string dir, List<KeyValuePair<string, string>> plan, bool caseOnly){
        var actual = Names(Directory.GetFiles(dir).Select(Path.GetFileName), true);
        int moved = plan.Count(p => actual.Contains(p.Value) && (caseOnly || !File.Exists(Path.Combine(dir, p.Key))));
        Check(plan.Count > 0 && moved == plan.Count, "计划里的 " + plan.Count + " 个文件都改好了名（磁盘上是新名字、旧名字不在了）：" + moved);
        Check(plan.Where(p => !p.Value.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase)).All(p => Encrypted(Path.Combine(dir, p.Value))), "改名后的天工文件仍是加密的（.cfg 原件本来就不加密）");
        Check(Directory.GetFiles(dir, "~改名中*").Length == 0, "没有留下临时文件");
    }

    static Dictionary<string, string> firstPlan;   // 第一轮：新名字 → 旧名字
    static void MainRun(string dir){
        foreach (var f in Directory.GetFiles(dir, "YF25.07G01-L1-M30-02*.dft")) staleBefore[Path.GetFileName(f)] = StaleOf(f);
        L("改名前过期视图：" + string.Join("，", staleBefore.Where(p => p.Value != 0).Select(p => p.Key + " " + p.Value).ToArray()));
        // 用户的样子：开着一张工程图和装配，装配是当前文档
        Open(Path.Combine(dir, Dft));
        Open(Path.Combine(dir, Asm));
        var none = new string[0];
        diag.StartRenameFormTest("YF25.07G01-L1-M30-02", "SK99-TEST", "", dir, true);
        string r = Wait(15);
        L(r);
        var plan = Plan(r);
        Check(r.Contains("error= "), "改名没有报错");
        string active = Try(() => (string)((dynamic)app.ActiveDocument).FullName, "");
        Check(Path.GetFileName(active) == "SK99-TEST.asm", "装配按新名字重新打开：" + active);
        CheckFiles(dir, plan, false);
        var olds = Names(plan.Select(p => p.Key), false); var news = Names(plan.Select(p => p.Value), false);
        CloseAll();
        var after = WalkFile(Path.Combine(dir, "SK99-TEST.asm"), dir, news, olds);
        L("改名后：实例 " + after[0] + "，缺 " + after[1] + "，新名字实例 " + after[2] + "，旧名字实例 " + after[3]);
        Check(after[0] > 100 && after[1] == 0 && after[2] > 0 && after[3] == 0, "装配 0 缺件、没有实例还链着旧名字");
        CloseAll();
        // 同文件夹里引用这些零件的其他装配（有的在当前装配树里、有的不在）：都要 0 缺件、不再链旧名字
        foreach (var name in Siblings) {
            var sib = WalkFile(Path.Combine(dir, name), dir, news, olds);
            L("   " + name + "：实例 " + sib[0] + "，缺 " + sib[1] + "，新名字 " + sib[2] + "，旧名字 " + sib[3]);
            Check(sib[0] > 0 && sib[1] == 0 && sib[3] == 0, "没改名的装配 " + name + " 也跟着更新、0 缺件");
            CloseAll();
        }
        CheckDrafts(dir, plan);
        firstPlan = plan.ToDictionary(p => p.Value, p => p.Key, StringComparer.OrdinalIgnoreCase);

        // 只改大小写（走临时名两步改名）
        Open(Path.Combine(dir, "SK99-TEST.asm"));
        diag.StartRenameFormTest("SK99-TEST", "sk99-test", "", dir, true);
        r = Wait(15);
        L(r);
        var plan2 = Plan(r);
        Check(r.Contains("error= "), "只改大小写也没有报错");
        CheckFiles(dir, plan2, true);
        CloseAll();
        var news2 = Names(plan2.Select(p => p.Value), true);
        var low = WalkFile(Path.Combine(dir, "sk99-test.asm"), dir, news2, Names(plan2.Select(p => p.Key), true));
        Check(low[0] == after[0] && low[1] == 0 && low[2] == after[2] && low[3] == 0, "大小写改名后装配 0 缺件、引用都是新写法（" + low[2] + "）");
        CloseAll();
        CheckDrafts(dir, plan2);
    }

    // 只读：列出每张工程图里过期的视图（对比改名前后是不是同一批）
    static void StaleRun(string dir){
        foreach (var f in Directory.GetFiles(dir, "*.dft")) {
            string n = Path.GetFileName(f);
            if (!n.StartsWith("YF25.07G01-L1-M30-02", StringComparison.OrdinalIgnoreCase) && !n.StartsWith("sk99", StringComparison.OrdinalIgnoreCase)) continue;
            object doc = null;
            try {
                doc = Open(f);
                var dft = (D.DraftDocument)doc;
                var bad = new List<string>(); int views = 0;
                foreach (D.Sheet sh in dft.Sheets) foreach (D.DrawingView v in sh.DrawingViews) { views++; if (!Try(() => v.IsUpToDate, true)) bad.Add(Try(() => v.Name, "?")); }
                L("STALE " + n + " 视图 " + views + " 过期 " + bad.Count + (bad.Count > 0 ? "：" + string.Join(",", bad.ToArray()) : ""));
            } catch (Exception e) { L("   打不开 " + n + "：" + e.Message); }
            finally { try { if (doc != null) ((dynamic)doc).Close(false); } catch { } Idle(3); }
        }
    }

    // 0.10.3：零件在 dir\零件\、装配在旁边的 dir\装配\（YF25.07 的 参考\ 和 焊接台车\ 就是这样）。
    // 打开零件本身、只改它的名字：旁边文件夹里用到它的装配也要跟着改好、0 缺件。seed = 拿来当零件的一个现成 .par 副本
    static void SiblingRun(string dir, string seed){
        string st = DateTime.Now.ToString("HHmmss");
        string partDir = Path.Combine(dir, "零件"), asmDir = Path.Combine(dir, "装配");
        Directory.CreateDirectory(partDir); Directory.CreateDirectory(asmDir);
        string part = Path.Combine(partDir, "SibA_" + st + ".par"), top = Path.Combine(asmDir, "SibTop_" + st + ".asm");
        if (seed.Length > 0) File.Copy(seed, part, true);
        else { var pd = app.Documents.Add("SolidEdge.PartDocument"); Idle(10); ((dynamic)pd).SaveAs(part); Idle(5); ((dynamic)pd).Close(false); Idle(5); }   // 新建空零件（拿现成零件当种子会带着别的装配的关联，一打开就问要不要开那个装配）
        var asm = (A.AssemblyDocument)app.Documents.Add("SolidEdge.AssemblyDocument"); Idle(10);
        asm.Occurrences.AddByFilename(part); Idle(10);
        asm.SaveAs(top); Idle(10);
        CloseAll();
        Open(part);
        diag.StartRenameFormTest("SibA_" + st, "SibB_" + st, "", dir, true);
        string r = Wait(10);
        L(r);
        Check(r.Contains("error= "), "只改零件名没有报错");
        string renamed = Path.Combine(partDir, "SibB_" + st + ".par");
        Check(File.Exists(renamed) && !File.Exists(part), "零件改好了名");
        CloseAll();
        var doc = Open(top) as A.AssemblyDocument;
        string occ = Try(() => doc.Occurrences.Item(1).OccurrenceFileName, "");
        bool missing = Try(() => doc.Occurrences.Item(1).FileMissing(), true);
        L("   旁边文件夹的装配现在引用：" + occ + (missing ? "（缺）" : ""));
        Check(string.Equals(occ, renamed, StringComparison.OrdinalIgnoreCase) && !missing, "旁边文件夹（装配\\）里用到它的装配跟着改好、0 缺件");
        CloseAll();
    }

    // 第 4 轮：任意装配（格式转换出来的天工装配，几百个导入零件、名字又长）上「查找替换」改名，
    // 核对改名计划、重新打开装配逐层 0 缺件、副本里不再有链着旧名字的实例。dir = 副本根（窗口只留它下面的文件）
    // 用法: RenameE2E.exe <输出> generic <副本根> <装配相对路径> <查找> <替换>
    static void GenericRun(string dir, string asmRel, string find, string replace){
        string asm = Path.Combine(dir, asmRel);
        var t0 = DateTime.Now;
        Open(asm);
        L("   打开 " + (DateTime.Now - t0).TotalSeconds.ToString("0.0") + "s");
        var t1 = DateTime.Now;
        diag.StartRenameFormTest(find, replace, "", dir, true);
        string r = Wait(30);
        L("   改名 " + (DateTime.Now - t1).TotalSeconds.ToString("0.0") + "s");
        L(r.Length > 3000 ? r.Substring(0, 3000) + "…（共 " + r.Length + " 字）" : r);
        var plan = Plan(r);
        Check(plan.Count > 0, "有改名计划（" + plan.Count + " 个文件）");
        Check(r.Contains("error= "), "改名没有报错");
        CloseAll();
        string newAsm = asm;
        foreach (var p in plan) if (string.Equals(p.Key, Path.GetFileName(asm), StringComparison.OrdinalIgnoreCase)) newAsm = Path.Combine(Path.GetDirectoryName(asm), p.Value);
        var news = Names(plan.Select(p => p.Value), false); var olds = Names(plan.Select(p => p.Key), false);
        var t2 = DateTime.Now;
        var c = WalkFile(newAsm, dir, news, olds);
        L("   重新打开 " + Path.GetFileName(newAsm) + " " + (DateTime.Now - t2).TotalSeconds.ToString("0.0") + "s：实例 " + c[0] + "，缺 " + c[1] + "，链新名字 " + c[2] + "，链旧名字 " + c[3]);
        Check(c[1] == 0, "重新打开 0 缺件");
        Check(c[3] == 0, "副本里没有链着旧名字的实例");
        int missingFiles = plan.Count(p => !Directory.GetFiles(dir, p.Value, SearchOption.AllDirectories).Any());
        Check(missingFiles == 0, "计划里的新文件名都在（缺 " + missingFiles + "）");
        CloseAll();
    }

    static void LockRun(string dir){
        Open(Path.Combine(dir, Asm));
        string locked = Path.Combine(dir, "YF25.07G01-L1-M30-02-09.par");
        var before = Directory.GetFiles(dir).ToDictionary(f => f, f => File.GetLastWriteTimeUtc(f));
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read)) {
            diag.StartRenameFormTest("YF25.07G01-L1-M30-02", "LOCK-TEST", "", dir, true);
            string r = Wait(15);
            L(r);
            Check(r.Contains("error=") && !r.Contains("error= "), "有文件被占用 → 报错不改");
        }
        var now = Directory.GetFiles(dir).ToDictionary(f => f, f => File.GetLastWriteTimeUtc(f));
        var changed = before.Where(p => !now.ContainsKey(p.Key) || now[p.Key] != p.Value).Select(p => Path.GetFileName(p.Key)).ToList();
        var added = now.Keys.Where(k => !before.ContainsKey(k)).Select(Path.GetFileName).ToList();
        L("   改动 " + changed.Count + "：" + string.Join(",", changed.Take(10).ToArray()) + "　新增 " + added.Count + "：" + string.Join(",", added.Take(10).ToArray()));
        Check(added.Count == 0 && changed.Count == 0, "失败时一个文件都没动（没改名、没改链接）");
        string active = Try(() => (string)((dynamic)app.ActiveDocument).FullName, "");
        Check(Path.GetFileName(active) == Asm, "装配按原名重新打开");
        CloseAll();
        var c = WalkFile(Path.Combine(dir, Asm), dir, new string[0], new string[0]);
        Check(c[0] > 100 && c[1] == 0, "装配照旧 0 缺件");
    }

    static void DirtyRun(string dir){
        var asm = (A.AssemblyDocument)Open(Path.Combine(dir, Asm));
        L("   打开后 Dirty=" + Try(() => (bool)((dynamic)asm).Dirty, false));
        // 像属性卡那样改装配里一个零件的内存属性（不保存）
        object part = null;
        foreach (A.Occurrence o in asm.Occurrences) { if (!o.Subassembly && o.OccurrenceFileName.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) { part = o.OccurrenceDocument; L("   改内存属性：" + o.OccurrenceFileName); break; } }
        var sets = (F.PropertySets)part.GetType().InvokeMember("Properties", System.Reflection.BindingFlags.GetProperty, null, part, null);
        var custom = sets.Item("Custom");
        custom.Add("改名测试", "未保存");
        Idle(5);
        L("   改零件内存后：装配 Dirty=" + Try(() => (bool)((dynamic)asm).Dirty, false) + " 零件 Dirty=" + Try(() => (bool)((dynamic)part).Dirty, false));
        diag.StartRenameFormTest("YF25.07G01-L1-M30-02", "DIRTY-TEST", "", dir, false);
        string r = Wait(15);
        L(r);
        Check(r.Contains("显示有没保存的修改") && r.Contains("YF25.07G01-L1-M30-02.asm"), "确认框里点名有修改的装配，提醒先保存");
        Check(r.Contains("error=已取消"), "用户点取消 → 什么都不改");
        string active = Try(() => (string)((dynamic)app.ActiveDocument).FullName, "");
        Check(Path.GetFileName(active) == Asm && Try(() => (bool)((dynamic)app.ActiveDocument).Dirty, false), "装配还开着、内存里的修改还在（没被关掉丢掉）");
        Check(Directory.GetFiles(dir, "DIRTY-TEST*").Length == 0, "没有改名");
    }

    // 0.10.1：属性卡填写走窗口真实路径 —— 打开大装配（几百个零件、加密副本），「批次」列前 120 行填同一个值再写入；
    // 必须全写进内存、不弹「加载后已被其他用户修改」对话框（0.8.1 现场），再改一次（第二轮）也一样
    static void PropCardRun(string dir, string asmName){
        var t0 = DateTime.Now;
        Open(Path.Combine(dir, asmName));
        L("   打开 " + asmName + " " + (DateTime.Now - t0).TotalSeconds.ToString("0.0") + "s");
        for (int round = 1; round <= 2; round++) {
            int before = dialogs.Count;
            var t1 = DateTime.Now;
            diag.StartPropCardFormTest("批次", "E2E-" + round, 120);
            string r = Wait(20);
            L("   第 " + round + " 轮 " + (DateTime.Now - t1).TotalSeconds.ToString("0.0") + "s：" + r);
            var m = System.Text.RegularExpressions.Regex.Match(r, @"after: 写好 (\d+) / (\d+) ");
            Check(m.Success && m.Groups[1].Value == m.Groups[2].Value && int.Parse(m.Groups[1].Value) > 50, "第 " + round + " 轮全部写好（" + (m.Success ? m.Value : "没找到") + "）");
            Check(!r.Contains("problems: ") || r.TrimEnd().EndsWith("problems:"), "第 " + round + " 轮没有失败的文件");
            Check(dialogs.Count == before, "第 " + round + " 轮没有弹对话框");
        }
    }

    // ---------- 对话框监视：这个桌面上冒出来的 #32770 都记下来，点默认按钮 ----------
    static volatile bool stop;
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr h, EnumProc f, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetDlgItem(IntPtr h, int id);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    static readonly HashSet<IntPtr> seenDlg = new HashSet<IntPtr>();
    static void Watch(){
        while (!stop) {
            EnumWindows((h, l) => {
                var cls = new StringBuilder(64); GetClassName(h, cls, 64);
                if (cls.ToString() != "#32770" || !IsWindowVisible(h) || seenDlg.Contains(h)) return true;
                seenDlg.Add(h);
                var title = new StringBuilder(256); GetWindowText(h, title, 256);
                var text = new StringBuilder();
                EnumChildWindows(h, (c, l2) => { var t = new StringBuilder(512); GetWindowText(c, t, 512); if (t.Length > 0) text.Append(t).Append(' '); return true; }, IntPtr.Zero);
                string s = "[" + title + "] " + text.ToString().Trim();
                lock (dialogs) dialogs.Add(s);
                Console.WriteLine("DIALOG " + s); Console.Out.Flush();
                foreach (int id in new[] { 1, 6, 2 }) { var b = GetDlgItem(h, id); if (b != IntPtr.Zero) { SendMessage(b, 0x00F5, IntPtr.Zero, IntPtr.Zero); break; } }
                return true;
            }, IntPtr.Zero);
            Thread.Sleep(300);
        }
    }
}

[ComImport, Guid("00000016-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IE2EMessageFilter {
    [PreserveSig] int HandleInComingCall(int type, IntPtr task, int ticks, IntPtr info);
    [PreserveSig] int RetryRejectedCall(IntPtr task, int ticks, int reject);
    [PreserveSig] int MessagePending(IntPtr task, int ticks, int pending);
}
sealed class E2EFilter : IE2EMessageFilter {
    [DllImport("ole32.dll")] static extern int CoRegisterMessageFilter(IE2EMessageFilter f, out IE2EMessageFilter old);
    static E2EFilter keep;
    public static void Register(){ keep = new E2EFilter(); IE2EMessageFilter old; CoRegisterMessageFilter(keep, out old); }
    public int HandleInComingCall(int a, IntPtr b, int c, IntPtr d){ return 0; }
    public int RetryRejectedCall(IntPtr a, int ticks, int reject){ return ticks < 600000 ? 200 : -1; }
    public int MessagePending(IntPtr a, int b, int c){ return 2; }
}
