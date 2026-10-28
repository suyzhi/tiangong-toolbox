using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using F = SolidEdgeFramework;

namespace TianGongCadSuite {
    // 批量改名：收集文件、关掉 / 重新打开 CAD 里的文档、按顺序做完一次改名。
    //
    // 一次改名的步骤（每一步失败都退回到改名前，见 Run）：
    //   1. 初筛：同文件夹里的工程图、装配 + 表里的文件，读字节看有没有可能链着要改名的文件（RenameRefScan，毫秒级）；
    //   2. 关掉 CAD 里用到这些文件的文档 —— 打开着的文件被天工占着，改不了名，链接也写不进去；
    //      不替用户保存（从插件里保存装配会弹框卡住 CAD，0.8.0 真机）。显示"有修改"的在确认框里点名，让用户选：
    //      改过东西就取消、回去 Ctrl+S；没改过就不保存直接关。天工**刚打开、什么都没动**的装配 / 工程图 Dirty 就已经是真
    //      （打开时自己会更新文件，0.10.0 真机），所以不能一见 Dirty 就拦死，那样永远改不了名；
    //   3. 设计管理器逐个打开初筛剩下的文件，记下要改的链接（RenameLinkFixer.Scan）；
    //   4. 检查要动的文件都没被占用、不是只读；
    //   5. 改链接并存盘（RenameLinkFixer.Apply）→ 改磁盘文件名（新名字撞上这批里别的旧名字时经临时名两步走）；
    //   6. 重新打开原来的文档（改了名的按新名字打开）。
    public sealed class BatchRenameCad {
        readonly F.Application app;
        public BatchRenameCad(F.Application application){ app = application; }

        public sealed class Result {
            public int Renamed, LinksFixed, ParentsFixed, Drafts, Scanned, Skipped;
            public long StartMs, ScanMs, ApplyMs, CloseMs, ReopenMs, TotalMs, FilterMs;
            public string Error = "";
            public readonly List<string> Warnings = new List<string>();
            public readonly List<string> Reopened = new List<string>();
            public string Summary(){
                var sb = new StringBuilder();
                sb.Append("改好 " + Renamed + " 个文件，更新了 " + ParentsFixed + " 个文件里的 " + LinksFixed + " 处引用");
                if (Drafts > 0) sb.Append("（其中工程图 " + Drafts + " 张）");
                sb.Append("。用时 " + Seconds(TotalMs));
                if (ReopenMs > 0) sb.Append("（重新打开 " + Seconds(ReopenMs) + "）");
                return sb.ToString();
            }
            static string Seconds(long ms){ return (ms / 1000.0).ToString("0.0") + " 秒"; }
        }

        // ---------- 收集 ----------

        // 当前文档涉及的文件（同属性卡：装配逐层去重 / 工程图 + 它引用的模型 / 零件自己）
        public readonly List<string> SkippedSubs = new List<string>();
        string rootPath = "";   // 收集时的当前文档：它把整棵装配树都加载着，改名前一定要关
        string activePath = ""; // 点「改名」时的当前文档：改完最后重新打开它，用户回来看到的还是它
        public List<string> Collect(out string source){
            var cad = new PropCardCad(app);
            var list = cad.Collect(out source);
            SkippedSubs.Clear(); SkippedSubs.AddRange(cad.skippedSubs);
            object doc = null;
            try { doc = app.ActiveDocument; } catch { }
            rootPath = doc == null ? "" : Full(FullName(doc));
            // 工程图本身也可能要改名：放在最前面
            if (doc is SolidEdgeDraft.DraftDocument) {
                string self = FullName(doc);
                if (self.Length > 0 && !list.Contains(self, StringComparer.OrdinalIgnoreCase)) list.Insert(0, self);
            }
            return list;
        }

        // 读文件的自定义属性（按属性命名用），和属性卡同一条读法
        public void ReadProps(RenameRow row){
            if (row.PropsLoaded) return;
            var r = new PropCardCad(app).Read(row.Path);
            row.Props.Clear();
            foreach (var p in r.Original) row.Props[p.Key] = p.Value;
            row.ReadProblem = r.Problem;
            row.PropsLoaded = true;
        }

        // ---------- 改名 ----------

        // confirm(要先关掉的文档, 其中显示有修改的)：改之前问一句，返回 false = 取消
        // ---------- 预热 ----------

        // 用户在表里改名字的时候，后台把改名要用的东西先热起来（真机：设计管理器冷启动 1.8 s → 热 0.9 s，初筛读文件 1.1 s → 0.3 s）：
        // 读一遍表里文件所在文件夹的 .asm / .dft（进磁盘缓存），起一次设计管理器再关掉。只读、不改任何东西。
        System.Threading.Thread warm;
        public void Warm(IEnumerable<string> paths){
            if (warm != null && warm.IsAlive) return;
            var folders = paths.Select(p => Path.GetDirectoryName(p) ?? "").Where(f => f.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToList();
            var files = paths.ToList();
            warm = new System.Threading.Thread(() => {
                try {
                    var buffer = new byte[1 << 16];
                    foreach (var f in files.Concat(folders.SelectMany(d => Files(d, "*.dft").Concat(Files(d, "*.asm")))).Take(2000)) {
                        try { using (var s = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) while (s.Read(buffer, 0, buffer.Length) > 0) { } } catch { }
                    }
                    if (Process.GetProcessesByName("DesMgr").Length == 0) {
                        var type = Type.GetTypeFromProgID("DesignManager.Application");
                        if (type != null) {
                            object dm = Activator.CreateInstance(type);
                            try { type.InvokeMember("Quit", BindingFlags.InvokeMethod, null, dm, null); } catch { }
                            try { System.Runtime.InteropServices.Marshal.ReleaseComObject(dm); } catch { }
                            // 等它真退出：还没退时改名会以为是用户自己开着的设计管理器，改完不敢关
                            for (int i = 0; i < 50 && Process.GetProcessesByName("DesMgr").Length > 0; i++) System.Threading.Thread.Sleep(100);
                        }
                    }
                } catch (Exception e) { Log.Write("RenameWarm", e); }
            }) { IsBackground = true, Priority = System.Threading.ThreadPriority.BelowNormal };
            warm.SetApartmentState(System.Threading.ApartmentState.MTA);
            warm.Start();
        }

        public Result Run(IList<RenameRow> rows, Func<string, string, bool> confirm, Action<string> pulse){
            // 预热还没完：等它把自己起的设计管理器关掉（否则改名时会以为是用户开着的、改完不敢关）
            var waited = Stopwatch.StartNew();
            while (warm != null && warm.IsAlive && waited.ElapsedMilliseconds < 15000) { pulse("准备中…"); warm.Join(100); try { System.Windows.Forms.Application.DoEvents(); } catch { } }
            var result = new Result();
            var total = Stopwatch.StartNew();
            var changed = rows.Where(r => r.Changed).ToList();
            if (changed.Count == 0) { result.Error = "没有要改名的文件"; return result; }

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in changed) map[Full(r.Path)] = Full(r.NewPath);
            var oldNames = new List<string>();
            foreach (var r in changed) if (RenameRefScan.Linkable(r.Path)) oldNames.Add(r.FileName);
            var filter = new RenameRefScan(oldNames);

            // 1. 候选父文件 + 初筛
            var sw = Stopwatch.StartNew();
            var candidates = new List<string>();
            if (oldNames.Count > 0) {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                Action<string> consider = path => {
                    string ext = Path.GetExtension(path).ToLowerInvariant();
                    if (ext != ".dft" && !RenameRefScan.Linkable(path)) return;
                    if (!seen.Add(Full(path))) return;
                    if (filter.MayReference(path)) candidates.Add(Full(path));
                    else result.Skipped++;
                };
                foreach (var r in rows) consider(r.Path);
                foreach (var folder in changed.Select(r => r.Folder).Distinct(StringComparer.OrdinalIgnoreCase)) {
                    pulse("查找引用这些文件的工程图、装配…　" + folder);
                    foreach (var f in Files(folder, "*.dft")) consider(f);
                    foreach (var f in Files(folder, "*.asm")) consider(f);
                }
                // 工程图常放在旁边的文件夹里（「项目\工程图\」「…加工件清单\」）：原来只查零件所在的文件夹，那些图改完名就断了。
                // 再查上一级文件夹往下两层的工程图（先按字节初筛，真链着的才交给设计管理器）；有上限，超了在结果里说
                var extraBudget = Stopwatch.StartNew(); int extra = 0; bool cut = false;
                foreach (var root in changed.Select(r => Path.GetDirectoryName(r.Folder.TrimEnd('\\')) ?? "").Where(d => d.Length > 3).Distinct(StringComparer.OrdinalIgnoreCase)) {
                    pulse("查找别的文件夹里引用这些文件的工程图…　" + root);
                    foreach (var f in DraftsUnder(root, 2)) {
                        if (extra >= 400 || extraBudget.ElapsedMilliseconds > 6000) { cut = true; break; }
                        if (seen.Contains(Full(f))) continue;
                        extra++; consider(f);
                    }
                    if (cut) break;
                }
                if (cut) result.Warnings.Add("上一级文件夹里的工程图太多，没有全部查完（查了 " + extra + " 张）：别处的工程图要是也引用了这些文件，用「添加文件…」把它加进表里一起改");
                // 旁边文件夹里的装配（0.10.3）：零件放在「项目\参考\…」、装配在「项目\焊接台车\」这种（用户的 YF25.07 就是），
                // 原来只查上一级往下的工程图，改了参考件的名字，旁边文件夹里用到它的装配全断了。上一级和它下面一层的装配也初筛一遍（有上限，超了说）
                var asmBudget = Stopwatch.StartNew(); int asmN = 0; bool asmCut = false;
                foreach (var root in changed.Select(r => Path.GetDirectoryName(r.Folder.TrimEnd('\\')) ?? "").Where(d => d.Length > 3).Distinct(StringComparer.OrdinalIgnoreCase)) {
                    pulse("查找旁边文件夹里引用这些文件的装配…　" + root);
                    foreach (var f in AssembliesNear(root)) {
                        if (asmN >= 300 || asmBudget.ElapsedMilliseconds > 8000) { asmCut = true; break; }
                        if (seen.Contains(Full(f))) continue;
                        asmN++; consider(f);
                    }
                    if (asmCut) break;
                }
                if (asmCut) result.Warnings.Add("旁边文件夹里的装配太多，没有全部查完（查了 " + asmN + " 个）：别处的装配要是也用了这些文件，用「添加文件…」把它加进表里一起改");
            }
            result.FilterMs = sw.ElapsedMilliseconds;
            Log.Write("RenameFilter", "候选 " + candidates.Count + " 个，初筛排除 " + result.Skipped + " 个，" + sw.ElapsedMilliseconds + " ms");

            // 2. 关掉 CAD 里用到这些文件的文档
            sw.Restart();
            var touched = new HashSet<string>(map.Keys, StringComparer.OrdinalIgnoreCase);
            foreach (var c in candidates) touched.Add(c);
            // 当前文档（装配）把整棵树都加载着；别的打开着的文档只关真用到的。没关、又占着文件的，第 4 步会查出来
            activePath = "";
            try { activePath = Full(FullName(app.ActiveDocument)); } catch { }
            var toClose = OpenDocuments().Where(d => touched.Contains(d.Key) || string.Equals(d.Key, rootPath, StringComparison.OrdinalIgnoreCase)).ToList();
            var dirty = toClose.Where(d => Dirty(d.Value)).Select(d => "　" + Path.GetFileName(d.Key)).ToArray();
            if (confirm != null && !confirm(string.Join("\r\n", toClose.Select(d => "　" + Path.GetFileName(d.Key)).ToArray()), string.Join("\r\n", dirty))) { result.Error = "已取消"; return result; }
            var closed = new List<string>();
            foreach (var d in toClose) {
                pulse("关闭 " + Path.GetFileName(d.Key) + " …");
                try { Late(d.Value, "Close", false); closed.Add(d.Key); }
                catch (Exception e) {
                    Log.Write("RenameClose " + d.Key, e);
                    result.Error = "关不掉「" + Path.GetFileName(d.Key) + "」：" + e.Message;
                    Reopen(closed, null, result);
                    return result;
                }
            }
            Idle();
            result.CloseMs = sw.ElapsedMilliseconds;

            bool done = false;
            RenameLinkFixer fixer = null;
            var journal = new List<KeyValuePair<string, string>>();
            try {
                // 3. 设计管理器读链接
                sw.Restart();
                fixer = new RenameLinkFixer();
                result.StartMs = sw.ElapsedMilliseconds;
                fixer.Scan(candidates, map, (n, p) => pulse("读引用 " + n + " / " + candidates.Count + "　" + Path.GetFileName(p)));
                result.Scanned = fixer.Scanned;
                if (fixer.ScanFailed > 0) throw new InvalidOperationException("这些文件读不出引用，没敢改名（改了它们会找不到文件）：\r\n" + string.Join("\r\n", fixer.ScanProblems.Take(10).ToArray()));
                result.ScanMs = sw.ElapsedMilliseconds;

                // 4. 要动的文件都没被占用
                var busy = new List<string>();
                // 天工刚关掉文档时句柄会晚一点放，所以第一个占用的文件等一等（最多 1.2 秒）；之后的不再每个都等 ——
                // 别人电脑开着几十个文件时，原来每个等 1.2 秒、期间不泵消息，天工"未响应"将近一分钟才报出来
                bool lockWaited = false; int checkedN = 0;
                foreach (var path in map.Keys.Concat(fixer.Parents.Select(p => p.Path)).Distinct(StringComparer.OrdinalIgnoreCase)) {
                    if (++checkedN % 20 == 0) pulse("检查文件有没有被占用… " + checkedN);
                    string why = Locked(path, lockWaited ? 0 : 6);
                    if (why.Length > 0) lockWaited = true;
                    if (why.Length > 0) busy.Add(Path.GetFileName(path) + "：" + why);
                }
                if (busy.Count > 0) throw new InvalidOperationException("这些文件改不了（被占用或只读）。天工里别的打开的文件在用它们？或者别人的电脑开着？\r\n" + string.Join("\r\n", busy.Take(10).ToArray()));

                // 5. 先改链接，再改文件名
                sw.Restart();
                fixer.Apply((n, p) => pulse("改引用 " + n + " / " + fixer.Parents.Count + "　" + Path.GetFileName(p)));
                foreach (var p in fixer.Parents) {
                    result.ParentsFixed++; result.LinksFixed += p.Edits.Count;
                    if (p.Path.EndsWith(".dft", StringComparison.OrdinalIgnoreCase)) result.Drafts++;
                }
                pulse("改文件名…");
                var moves = RenameRules.Moves(changed, TempFor);
                var steps = new List<KeyValuePair<string, string>>();
                foreach (var m in moves) if (m.Temp != null) steps.Add(new KeyValuePair<string, string>(m.From, m.Temp));
                foreach (var m in moves) if (m.Temp == null) steps.Add(new KeyValuePair<string, string>(m.From, m.To));
                foreach (var m in moves) if (m.Temp != null) steps.Add(new KeyValuePair<string, string>(m.Temp, m.To));
                string moveError;
                int moved = MoveFiles(steps, out moveError);
                journal.AddRange(steps.Take(moved));
                if (moveError.Length > 0) throw new InvalidOperationException(moveError);
                result.Renamed = moves.Count;
                result.ApplyMs = sw.ElapsedMilliseconds;
                done = true;
            } catch (Exception e) {
                Log.Write("RenameRun", e);
                result.Error = e.Message;
                // 退回：文件名先改回来，链接再改回来（链接是按原来的路径打开的文件）
                if (journal.Count > 0) {
                    var back = new List<KeyValuePair<string, string>>();
                    for (int i = journal.Count - 1; i >= 0; i--) back.Add(new KeyValuePair<string, string>(journal[i].Value, journal[i].Key));
                    string why2;
                    int undone = MoveFiles(back, out why2);
                    for (int i = undone; i < back.Count; i++) result.Warnings.Add("没能改回：" + back[i].Key + " → " + Path.GetFileName(back[i].Value));
                    if (why2.Length > 0) Log.Write("RenameUndoMove", why2);
                }
                if (fixer != null) foreach (var f in fixer.Revert()) result.Warnings.Add("引用没能改回：" + f);
            } finally {
                if (fixer != null) fixer.Dispose();
            }

            // 装配旁边的 Lineup 数据（「装配名.lineup.xml」，0.10.3）：按名字找的，装配改了名不跟着改，Lineup 打开就是空的（像是清单全丢了）
            if (done) foreach (var r in changed) {
                if (!r.Path.EndsWith(".asm", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var tail in new[] { ".lineup.xml", ".lineup.xml.bak" }) {
                    string from = Path.ChangeExtension(r.Path, null) + tail, to = Path.ChangeExtension(r.NewPath, null) + tail;
                    try { if (File.Exists(from) && !File.Exists(to)) File.Move(from, to); }
                    catch (Exception e) { Log.Write("RenameLineup " + from, e); if (tail == ".lineup.xml") result.Warnings.Add("Lineup 清单「" + Path.GetFileName(from) + "」没能跟着改名（" + e.Message + "），手动改成「" + Path.GetFileName(to) + "」"); }
                }
            }

            // 6. 重新打开
            sw.Restart();
            Reopen(closed, done ? map : null, result);
            result.ReopenMs = closed.Count > 0 ? sw.ElapsedMilliseconds : 0;
            result.TotalMs = total.ElapsedMilliseconds;
            Log.Write("RenameRun", "改名 " + result.Renamed + "、父文件 " + result.ParentsFixed + "、引用 " + result.LinksFixed + "、读引用 " + result.Scanned + "（排除 " + result.Skipped + "）"
                + "；初筛 " + result.FilterMs + " / 关闭 " + result.CloseMs + " / 启动设计管理器 " + result.StartMs + " / 读 " + result.ScanMs + " / 改 " + result.ApplyMs + " / 重开 " + result.ReopenMs + " / 共 " + result.TotalMs + " ms" + (result.Error.Length > 0 ? "；失败：" + result.Error : ""));
            return result;
        }

        // 改磁盘文件名：交给插件旁边的 SuiteRenameWorker.exe（不在加密白名单里的进程）按顺序做。
        // 不能在插件里 File.Move：天工进程在加密软件白名单里，白名单进程给加密文件改名，改出来的文件落盘就是明文
        // （真机 2026-10-25：插件里改名的 44 个文件全成了明文；设计管理器 Document.Rename 也一样，天工自带的改名同理）。
        // 返回改成功了几个（前 n 个），why 非空 = 停在第 n+1 个的原因
        static int MoveFiles(List<KeyValuePair<string, string>> steps, out string why){
            why = "";
            if (steps.Count == 0) return 0;
            string exe = Path.Combine(Path.GetDirectoryName(typeof(BatchRenameCad).Assembly.Location), "SuiteRenameWorker.exe");
            if (!File.Exists(exe)) { why = "找不到改名工作器 " + exe + "（安装不完整，请重新安装天工工具箱）"; return 0; }
            string tmp = Path.Combine(Path.GetTempPath(), "tg-rename-" + Guid.NewGuid().ToString("N"));
            string list = tmp + ".txt", answer = tmp + ".out";
            try {
                File.WriteAllLines(list, steps.Select(s => s.Key + "\t" + s.Value).ToArray(), new UTF8Encoding(false));
                var psi = new ProcessStartInfo(exe, "\"" + list + "\" \"" + answer + "\"") { UseShellExecute = false, CreateNoWindow = true };
                using (var p = Process.Start(psi)) {
                    // 几十个文件不到一秒；等的时候泵消息，天工不会被判成"未响应"
                    var limit = Stopwatch.StartNew();
                    while (!p.WaitForExit(100)) {
                        try { System.Windows.Forms.Application.DoEvents(); } catch { }
                        if (limit.Elapsed.TotalMinutes > 5) { try { p.Kill(); } catch { } break; }
                    }
                }
                var lines = File.Exists(answer) ? File.ReadAllLines(answer, Encoding.UTF8) : new string[0];
                int done = 0;
                var head = lines.Length > 0 ? lines[0].Split(' ') : new string[0];
                if (head.Length == 2) int.TryParse(head[1], out done);
                if (head.Length != 2 || head[0] != "OK") why = "改文件名失败：" + (lines.Length > 1 ? lines[1] : "改名工作器没有回报结果");
                return Math.Min(done, steps.Count);
            } catch (Exception e) {
                Log.Write("RenameWorker", e);
                why = "改文件名失败：" + e.Message;
                return 0;
            } finally {
                try { File.Delete(list); } catch { }
                try { File.Delete(answer); } catch { }
            }
        }

        static string TempFor(string path){
            string dir = Path.GetDirectoryName(path), ext = Path.GetExtension(path);
            for (int i = 0; ; i++) {
                string t = Path.Combine(dir, "~改名中" + Guid.NewGuid().ToString("N").Substring(0, 8) + ext);
                if (!File.Exists(t)) return t;
            }
        }

        // 能不能独占打开（= 没人占着、不是只读）。空串 = 可以
        static string Locked(string path, int retries){
            for (int i = 0; ; i++) {
                try { using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { } return ""; }
                catch (UnauthorizedAccessException) { return "只读或没有权限"; }
                catch (FileNotFoundException) { return "文件不在了"; }
                catch (IOException e) { if (i >= retries) return "被占用（" + e.Message.Trim() + "）"; System.Threading.Thread.Sleep(200); }
            }
        }

        void Reopen(List<string> closed, Dictionary<string, string> map, Result result){
            // 改名前用户正在看的那个文档最后打开：最后打开的就是当前窗口。原来按文档顺序重开，
            // 改完名用户面前常常是一张工程图，而不是刚才在改的装配
            var order = closed.OrderBy(p => string.Equals(p, activePath, StringComparison.OrdinalIgnoreCase) ? 1 : 0).ToList();
            foreach (var path in order) {
                string target = path, mapped;
                if (map != null && map.TryGetValue(path, out mapped)) target = mapped;
                try { app.Documents.Open(target); result.Reopened.Add(target); }
                catch (Exception e) { Log.Write("RenameReopen " + target, e); result.Warnings.Add("没能重新打开 " + Path.GetFileName(target) + "：" + e.Message); }
            }
            Idle();
        }

        // ---------- CAD 文档 ----------

        // 打开着的文档：路径 → 文档
        List<KeyValuePair<string, object>> OpenDocuments(){
            var list = new List<KeyValuePair<string, object>>();
            try {
                var docs = app.Documents;
                for (int i = 1; i <= docs.Count; i++) {
                    object d = docs.Item(i);
                    string name = FullName(d);
                    if (name.Length > 0) list.Add(new KeyValuePair<string, object>(Full(name), d));
                }
            } catch (Exception e) { Log.Write("RenameDocs", e); }
            return list;
        }

        static bool Dirty(object doc){ try { return Convert.ToBoolean(doc.GetType().InvokeMember("Dirty", BindingFlags.GetProperty, null, doc, null)); } catch { return false; } }
        static void Late(object o, string name, params object[] args){ o.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, o, args); }

        void Idle(){ for (int i = 0; i < 3; i++) { try { app.DoIdle(); } catch { } } }

        // folder 下（含 depth 层子文件夹）的工程图；读不了的文件夹跳过
        // 上一级文件夹里的装配 + 它下面一层各文件夹里的装配（不再往深走：大项目一层就有几百个装配、几百 MB 要读）
        static IEnumerable<string> AssembliesNear(string folder){
            foreach (var f in Files(folder, "*.asm")) yield return f;
            string[] subs;
            try { subs = Directory.GetDirectories(folder); } catch { yield break; }
            foreach (var d in subs) {
                try { if ((File.GetAttributes(d) & (FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System)) != 0) continue; } catch { continue; }
                foreach (var f in Files(d, "*.asm")) yield return f;
            }
        }
        static IEnumerable<string> DraftsUnder(string folder, int depth){
            foreach (var f in Files(folder, "*.dft")) yield return f;
            if (depth <= 0) yield break;
            string[] subs;
            try { subs = Directory.GetDirectories(folder); } catch { yield break; }
            foreach (var d in subs) {
                try { if ((File.GetAttributes(d) & (FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System)) != 0) continue; } catch { continue; }
                foreach (var f in DraftsUnder(d, depth - 1)) yield return f;
            }
        }

        static IEnumerable<string> Files(string folder, string pattern){
            try { return Directory.GetFiles(folder, pattern); } catch { return new string[0]; }
        }
        static string FullName(object doc){ try { return Convert.ToString(doc.GetType().InvokeMember("FullName", BindingFlags.GetProperty, null, doc, null)); } catch { return ""; } }
        static string Full(string path){ try { return Path.GetFullPath(path); } catch { return path ?? ""; } }
    }
}
