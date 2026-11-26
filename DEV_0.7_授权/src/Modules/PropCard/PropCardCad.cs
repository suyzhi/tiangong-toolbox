using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using F = SolidEdgeFramework;
using A = SolidEdgeAssembly;

namespace TianGongCadSuite {
    // 属性卡填写：从当前文档收集文件、读写自定义属性。
    //
    // 读写一律先走天工自带的属性组件（SolidEdge.FileProperties，PropAuto.dll，进程内）：
    // 不打开文档、不加载几何，一个文件几十毫秒。它在天工进程里运行，加密（DLP）文件照样能读写 ——
    // 同一个组件放在 Python 这类外部进程里就打不开加密文件。
    //
    // 已经在 CAD 内存里的文件（打开着的文档、装配里加载着的零件、工程图引用的模型）只写内存那份，绝不碰磁盘（真机实测）：
    //   · 先写磁盘再改内存（0.8.0 / 0.8.1 的做法）：天工发现文件"加载后已被其他用户修改"，拒绝给写权限，
    //     每个文件弹一个对话框（0.8.1 现场，写 64 个文件）；
    //   · 只写磁盘不改内存：内存里还是旧值，工程图标题栏照旧，用户之后改这个零件也会被同一个对话框拦住。
    //   所以内存里的文件一律写内存，**不替用户保存**（从插件里保存装配会弹对话框把 CAD 卡住，真机），让用户自己 Ctrl+S。
    //
    // 两边的属性集合是同一个接口类型、成员编号却不一样：.NET 的 dynamic 按接口类型缓存调用方式，
    // 在一边用过 dynamic，另一边的调用就会调错成员（0.8.1 现场：碰过内存文档之后，属性组件的 Add 全报 TargetParameterCountException）。
    // 所以属性组件一律按名字后期绑定（Late，每次现查编号），内存文档一律按接口类型调，两边都不用 dynamic。
    public sealed class PropCardCad {
        public const string SaveHint = "提示：已写进 CAD 里打开着的文件，请在 CAD 里保存";
        readonly F.Application app;
        object root;   // 收集时的当前文档（装配 / 工程图 / 零件），写入时用来找已加载的文档
        Dictionary<string, object> opened;   // 文件路径 → CAD 里打开着的文档（Documents + 工程图 ModelLinks）；每次写会话重建
        // 文件路径 → 装配里引用它的第一个实例。收集时顺手记下（只读文件名，便宜）；
        // 写到哪个文件才取它的 OccurrenceDocument —— 一次把全部实例的文档取一遍，105 个零件的装配要 9 秒（真机）
        readonly Dictionary<string, A.Occurrence> occurrences = new Dictionary<string, A.Occurrence>(StringComparer.OrdinalIgnoreCase);
        // 扫描 CAD 里所有已打开装配/装配图间接加载着的实例（弥补「扫描文件夹 / 添加文件」时 root 为空导致误写磁盘）
        Dictionary<string, A.Occurrence> openAsmOccurrences;

        public PropCardCad(F.Application application){ app = application; }

        // ---------- 收集 ----------

        // 当前文档涉及的文件：装配 = 自己 + 全部零件和子装配（逐层，去重）；工程图 = 它引用的模型；零件 = 自己。
        // 收集时读不出来、只好跳过的子装配（文件名）：窗口里要说出来，不然用户以为全收齐了
        public readonly List<string> skippedSubs = new List<string>();
        public List<string> Collect(out string source){
            var list = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            source = "没有打开的文档";
            object doc = null;
            try { doc = app.ActiveDocument; } catch { }
            root = doc;
            rootPath = doc == null ? "" : FullName(doc);
            opened = null;
            openAsmOccurrences = null;
            occurrences.Clear(); skippedSubs.Clear();
            if (doc == null) return list;
            var assembly = doc as A.AssemblyDocument;
            if (assembly != null) {
                Add(list, seen, FullName(assembly));
                Walk(assembly, list, seen);
                source = "装配「" + Path.GetFileName(FullName(assembly)) + "」及其全部零件";
                return list;
            }
            // 工程图：ModelLinks 里是它引用的零件 / 装配
            if (doc is SolidEdgeDraft.DraftDocument) {
                bool anyAssembly = false;
                try {
                    var links = ((SolidEdgeDraft.DraftDocument)doc).ModelLinks;
                    for (int i = 1; i <= links.Count; i++) {
                        var link = links.Item(i);
                        string file = ""; try { file = link.FileName; } catch { }
                        Add(list, seen, file);
                        // 装配图：原来只列出装配文件本身一行，明细栏里的零件一个都不在表里。装配往下展开（同「载入装配」）
                        A.AssemblyDocument asm = null;
                        try { asm = link.ModelDocument as A.AssemblyDocument; } catch { }
                        if (asm != null) { anyAssembly = true; try { Walk(asm, list, seen); } catch (Exception e) { Log.Write("PropCardDraftAsm " + file, e); } }
                    }
                } catch (Exception e) { Log.Write("PropCardCollect", e); }
                source = "工程图「" + Path.GetFileName(FullName(doc)) + "」引用的模型" + (anyAssembly ? "（含装配里的全部零件）" : "");
                return list;
            }
            Add(list, seen, FullName(doc));
            source = "当前文件「" + Path.GetFileName(FullName(doc)) + "」";
            return list;
        }

        void Walk(A.AssemblyDocument assembly, List<string> list, HashSet<string> seen){
            foreach (A.Occurrence o in assembly.Occurrences) {
                string file = null;
                try { file = o.OccurrenceFileName; } catch { }
                if (string.IsNullOrEmpty(file)) continue;
                bool first = Add(list, seen, file);
                if (first) occurrences[Key(file)] = o;
                bool sub = false;
                try { sub = o.Subassembly; } catch { }
                // 子装配只展开一次（同一个子装配用了好几次，零件已经收过了）
                if (sub && first) {
                    // 天工正忙（0x8001010A，大装配刚打开、还在加载）时原来直接跳过这个子装配，里面的零件一声不响地不在表里（10-23 日志两次）：等一下重试
                    try { var oc = o; var child = AutoHoleWriter.Attempt("读子装配", () => oc.OccurrenceDocument) as A.AssemblyDocument; if (child != null) Walk(child, list, seen); }
                    catch (Exception e) { Log.Write("PropCardWalk " + file, e); skippedSubs.Add(System.IO.Path.GetFileName(file)); }
                }
            }
        }

        // 收集时的那个文档还活着吗（窗口开着时用户关掉又打开了装配、天工重新加载过）：
        // 死了的话记下的"哪些文件在内存里"全作废，写入会退回写磁盘 —— 而这些文件现在又被新打开的装配加载着，
        // 正是 0.8.1 的「加载后已被其他用户修改」对话框。没有收集过文档（只「添加文件」）算活着
        string rootPath = "";
        // 写之前调：每次写会话都先失效已打开文档缓存（防止窗口开着期间用户新打开/重开了文件导致漏判）；
        // 文档死了但用户又把同一个文件打开着（关掉重开过）→ 按新打开的那份重新记一遍"哪些文件在内存里"，表里的修改不动；
        // 当前文档换成了别的文件 → 返回 false，让用户先切回去
        public bool Rebind(){
            InvalidateOpenCache();
            if (RootAlive()) return true;
            object doc = null;
            try { doc = app.ActiveDocument; } catch { }
            if (doc == null || !string.Equals(FullName(doc), rootPath, StringComparison.OrdinalIgnoreCase)) return false;
            string ignored;
            Collect(out ignored);
            return true;
        }
        public void InvalidateOpenCache(){
            opened = null;
            openAsmOccurrences = null;
        }
        public string RootName { get { return Path.GetFileName(rootPath); } }
        public bool RootAlive(){
            if (root == null) return true;
            try { var n = Convert.ToString(root.GetType().InvokeMember("FullName", BindingFlags.GetProperty, null, root, null)); return true; }
            catch { return false; }
        }

        public void ResetContext(){
            root = null;
            rootPath = "";
            opened = null;
            openAsmOccurrences = null;
            occurrences.Clear();
            skippedSubs.Clear();
        }

        public void RefreshActiveContext(){
            ResetContext();
            if (app == null) return;
            try {
                if (app.ActiveDocument != null) {
                    string ignored;
                    Collect(out ignored);
                }
            } catch { }
        }

        static bool Add(List<string> list, HashSet<string> seen, string file){
            if (string.IsNullOrEmpty(file) || !seen.Add(file)) return false;
            list.Add(file);
            return true;
        }

        static string FullName(object doc){ try { return Convert.ToString(((dynamic)doc).FullName); } catch { return ""; } }

        // ---------- 读 ----------

        public PropCardRow Read(string path){
            var row = new PropCardRow { Path = path };
            // 已直接打开在 CAD 顶层文档列表（或工程图 ModelLinks）里的文件优先读内存那份，避免读到未 Ctrl+S 存盘的旧磁盘值；
            // 装配子件不提前取 OccurrenceDocument（105 个零件会耗 9 秒），仅在磁盘属性组件读不了时再退回内存文档。
            object topDoc = FindDirectOpenedDocument(path);
            if (topDoc != null && TryReadDocIntoRow(topDoc, row)) return row;

            object sets = null;
            try {
                sets = NewFileProperties();
                Late.Call(sets, "Open", path, true);
                object custom = Late.Get(sets, "Item", "Custom");
                foreach (var pair in FileNames(custom)) row.Original[pair.Key] = Convert.ToString(Late.Get(Late.Get(custom, "Item", pair.Value), "Value"));
                try { row.Material = Convert.ToString(Late.Get(Late.Get(Late.Get(sets, "Item", "MechanicalModeling"), "Item", "Material"), "Value")); } catch { }
                if (string.IsNullOrEmpty(row.Material)) {
                    try { row.Material = Convert.ToString(Late.Get(Late.Get(Late.Get(sets, "Item", "MechanicalModeling"), "Item", "材料"), "Value")); } catch { }
                }
            } catch (Exception e) {
                // 读不了文件（打开着的加密文件就读不了）：退回已打开的文档（含 Custom 与 Material）
                var doc = LoadedDocument(path);
                // 原来显示"读不了：调用的目标发生了异常"（后期绑定的外层异常），10-23 现场 3 个文件其实是装配引用的文件已经不在了（STG_E_FILENOTFOUND）
                if (doc == null) {
                    bool missing = false; try { missing = !File.Exists(path); } catch { }
                    row.Problem = missing ? "文件不在了（装配里引用的这个文件被移走、改名或删了）：" + path : "读不了：" + Message(e);
                    Log.Write("PropCardRead " + path, e);
                }
                else {
                    try {
                        ReadDocIntoRow(doc, row);
                    } catch (Exception e2) { row.Problem = "读不了：" + e2.Message; Log.Write("PropCardReadDoc " + path, e2); }
                }
            } finally { Close(sets); }
            return row;
        }

        bool TryReadDocIntoRow(object doc, PropCardRow row){
            try {
                ReadDocIntoRow(doc, row);
                return true;
            } catch {
                row.Original.Clear();
                row.Material = "";
                return false;
            }
        }

        void ReadDocIntoRow(object doc, PropCardRow row){
            Attempt("读内存文档属性", () => {
                row.Original.Clear();
                var custom = DocCustom(doc);
                foreach (var pair in DocNames(custom)) row.Original[pair.Key] = Convert.ToString(custom.Item(pair.Value).get_Value());
                row.Material = DocMaterial(doc);
                return true;
            });
        }

        // 下标起点写死，绝不"试一下"：属性组件的集合从 0 数；文档的 Properties 从 1 数，
        // 而且在文档集合上取越界的下标（Item(0)）不是报错，是 FWKAUTO.DLL 里直接访问冲突、天工整个崩掉
        // （0.8.0 现场：打开加密零件点本命令就崩，.NET 拦不住这种异常）。按名字取不存在的属性也不碰，先列出已有的名字。
        // 返回 名字 → 下标（重名取第一个）
        static Dictionary<string, int> FileNames(object custom){
            var at = new Dictionary<string, int>();
            int count = Convert.ToInt32(Late.Get(custom, "Count"));
            for (int i = 0; i < count; i++) {
                try { Index(at, Convert.ToString(Late.Get(Late.Get(custom, "Item", i), "Name")), i); } catch { }
            }
            return at;
        }
        static Dictionary<string, int> DocNames(F.Properties custom){
            var at = new Dictionary<string, int>();
            int count = custom.Count;
            for (int i = 1; i <= count; i++) {
                try { Index(at, custom.Item(i).Name, i); } catch { }
            }
            return at;
        }
        static void Index(Dictionary<string, int> at, string name, int index){ if (!string.IsNullOrEmpty(name) && !at.ContainsKey(name)) at[name] = index; }

        // ---------- 写 ----------

        // 返回空串 = 写好并已存盘；SaveHint = 写进了 CAD 内存里的文档（要用户自己保存）；其他 = 失败原因
        public string Write(PropCardRow row, List<KeyValuePair<string, string>> writes){
            if (writes.Count == 0) return "";
            object doc = LoadedDocument(row.Path);
            if (doc != null) {
                try {
                    Attempt("写内存文档属性", () => { PutDoc(DocCustom(doc), writes, row.Edited); return true; });
                    return SaveHint;
                } catch (Exception e) {
                    // 文件已在 CAD 内存里加载：写内存失败时绝不穿透写磁盘，否则会触发天工「加载后已被其他用户修改」弹窗或造成磁盘与内存撕裂
                    Log.Write("PropCardWriteDoc " + row.Path, e);
                    return "文件已在天工中打开，写不进内存文档（未改写磁盘以免冲突）：" + Message(e);
                }
            }
            object sets = null;
            try {
                sets = NewFileProperties();
                Late.Call(sets, "Open", row.Path, false);
                PutFile(Late.Get(sets, "Item", "Custom"), writes, row.Edited);
                Late.Call(sets, "Save");
                return "";
            } catch (Exception e) {
                Log.Write("PropCardWrite " + row.Path, e);
                return "写不进去：" + Message(e);
            } finally { Close(sets); }
        }

        // 已有的属性按下标改值，没有的才 Add（不靠"取一下看报不报错"）
        // edited：用户真改过的格子。其余的是「补齐」补的空值 —— 文件里其实已经有这个属性时（表里读到的和文件现在的不一致）绝不覆盖
        static void PutFile(object custom, List<KeyValuePair<string, string>> writes, IDictionary<string, string> edited){
            var at = FileNames(custom);
            foreach (var pair in writes) {
                int index;
                if (!at.TryGetValue(pair.Key, out index)) { Late.Call(custom, "Add", pair.Key, pair.Value); continue; }
                if (edited != null && !edited.ContainsKey(pair.Key)) continue;
                try { Late.Set(Late.Get(custom, "Item", index), "Value", pair.Value); }
                catch (Exception e) { throw new InvalidOperationException("「" + pair.Key + "」写不进去（原来不是文字属性？）：" + Message(e)); }
            }
        }
        internal static void PutDoc(F.Properties custom, List<KeyValuePair<string, string>> writes, IDictionary<string, string> edited){
            var at = DocNames(custom);
            foreach (var pair in writes) {
                int index;
                if (!at.TryGetValue(pair.Key, out index)) { custom.Add(pair.Key, pair.Value); continue; }
                if (edited != null && !edited.ContainsKey(pair.Key)) continue;
                object value = pair.Value;
                try { custom.Item(index).set_Value(ref value); }
                catch (Exception e) { throw new InvalidOperationException("「" + pair.Key + "」写不进去（原来不是文字属性？）：" + e.Message); }
            }
        }

        // 后期绑定的异常包在 TargetInvocationException 里，取里面那层的话
        static string Message(Exception e){ return (e is TargetInvocationException && e.InnerException != null ? e.InnerException : e).Message; }

        // ---------- 已打开的文档 ----------

        // 文档的属性集合按接口类型取：从 Documents.Item / OccurrenceDocument 拿到的是裸 IDispatch
        internal static F.Properties DocCustom(object doc){
            object sets = doc.GetType().InvokeMember("Properties", BindingFlags.GetProperty, null, doc, null);
            return ((F.PropertySets)sets).Item("Custom");
        }

        // 从内存文档安全读取材料（1-based 枚举，避免按名取不存在属性导致原生异常）
        static string DocMaterial(object doc){
            try {
                var sets = (F.PropertySets)doc.GetType().InvokeMember("Properties", BindingFlags.GetProperty, null, doc, null);
                for (int s = 1; s <= sets.Count; s++) {
                    var set = sets.Item(s);
                    string sname = ""; try { sname = set.Name; } catch { }
                    if (!string.Equals(sname, "MechanicalModeling", StringComparison.OrdinalIgnoreCase)) continue;
                    for (int j = 1; j <= set.Count; j++) {
                        try {
                            var p = set.Item(j);
                            if (string.Equals(p.Name, "Material", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(p.Name, "材料", StringComparison.Ordinal)) {
                                string v = (Convert.ToString(p.get_Value()) ?? "").Trim();
                                if (v.Length > 0) return v;
                            }
                        } catch { }
                    }
                }
            } catch { }
            return "";
        }

        void EnsureOpenedIndex(){
            if (opened != null) return;
            opened = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (app == null) return;
            try {
                var docs = app.Documents;
                for (int i = 1; i <= docs.Count; i++) {
                    object d = docs.Item(i);
                    Remember(FullName(d), d);
                    var dft = d as SolidEdgeDraft.DraftDocument;
                    if (dft != null) RememberDraftLinks(dft);
                }
            } catch (Exception e) { Log.Write("PropCardDocs", e); }
            if (root is SolidEdgeDraft.DraftDocument) RememberDraftLinks((SolidEdgeDraft.DraftDocument)root);
        }

        void RememberDraftLinks(SolidEdgeDraft.DraftDocument dft){
            try {
                var links = dft.ModelLinks;
                for (int i = 1; i <= links.Count; i++) {
                    var link = links.Item(i);
                    try { Remember(link.FileName, link.ModelDocument); } catch (Exception e) { Log.Write("PropCardModelLink", e); }
                }
            } catch (Exception e) { Log.Write("PropCardModelLinks", e); }
        }

        void EnsureOpenAsmOccurrences(){
            if (openAsmOccurrences != null) return;
            openAsmOccurrences = new Dictionary<string, A.Occurrence>(StringComparer.OrdinalIgnoreCase);
            EnsureOpenedIndex();
            var seenAsms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in opened) {
                var asm = kv.Value as A.AssemblyDocument;
                if (asm != null && seenAsms.Add(kv.Key)) WalkOpenOccurrences(asm, openAsmOccurrences, seenAsms);
            }
        }

        void WalkOpenOccurrences(A.AssemblyDocument asm, Dictionary<string, A.Occurrence> target, HashSet<string> seenAsms){
            try {
                foreach (A.Occurrence o in asm.Occurrences) {
                    string file = null;
                    try { file = o.OccurrenceFileName; } catch { }
                    if (string.IsNullOrEmpty(file)) continue;
                    string k = Key(file);
                    if (!target.ContainsKey(k)) target[k] = o;
                    bool sub = false;
                    try { sub = o.Subassembly; } catch { }
                    if (sub && seenAsms.Add(k)) {
                        try {
                            var oc = o;
                            var child = Attempt("读打开装配的子装配", () => oc.OccurrenceDocument) as A.AssemblyDocument;
                            if (child != null) WalkOpenOccurrences(child, target, seenAsms);
                        } catch { }
                    }
                }
            } catch { }
        }

        object FindDirectOpenedDocument(string path){
            if (app == null) return null;
            EnsureOpenedIndex();
            object hit;
            if (opened.TryGetValue(Key(path), out hit) && IsDocAlive(hit)) return hit;
            return null;
        }

        static bool IsDocAlive(object doc){
            if (doc == null) return false;
            try { return Convert.ToString(doc.GetType().InvokeMember("FullName", BindingFlags.GetProperty, null, doc, null)).Length > 0; }
            catch { return false; }
        }

        // 文件在不在 CAD 内存里：打开着的文档、打开着的工程图引用的模型、当前装配及所有已打开装配里引用它的实例
        // （加密文件在内存里时属性组件写不进去，只能写内存里那份 —— 工程图引用的零件就是这样，0.8.0 现场）
        object LoadedDocument(string path){
            if (app == null) return null;
            string k = Key(path);
            EnsureOpenedIndex();
            object hit;
            if (opened.TryGetValue(k, out hit) && IsDocAlive(hit)) return hit;
            A.Occurrence o;
            if (occurrences.TryGetValue(k, out o)) {
                try { object d = o.OccurrenceDocument; if (d != null) return d; } catch { }
            }
            // 「扫描文件夹 / 添加文件」或用户另行打开了别的装配时，也检查全部已打开装配里的实例，防止误写磁盘触发天工冲突弹窗
            EnsureOpenAsmOccurrences();
            if (openAsmOccurrences.TryGetValue(k, out o)) {
                try { object d = o.OccurrenceDocument; if (d != null) return d; } catch { }
            }
            return null;
        }

        T Attempt<T>(string what, Func<T> action){
            for (int i = 0; ; i++) {
                try { return action(); }
                catch (Exception e) {
                    var ce = e as COMException ?? (e.InnerException as COMException);
                    if (ce == null) throw;
                    int hr = ce.ErrorCode;
                    bool retryable = hr == unchecked((int)0x80010001) || hr == unchecked((int)0x8001010A) || hr == unchecked((int)0x80010100);
                    if (!retryable || i >= 8) throw;
                    try { if (app != null) app.DoIdle(); } catch { }
                    System.Threading.Thread.Sleep(200);
                }
            }
        }

        void Remember(string file, object doc){ if (!string.IsNullOrEmpty(file) && doc != null && !opened.ContainsKey(Key(file))) opened[Key(file)] = doc; }

        static string Key(string path){ try { return Path.GetFullPath(path); } catch { return path ?? ""; } }

        // ---------- 属性组件 ----------

        static object NewFileProperties(){
            var type = Type.GetTypeFromProgID("SolidEdge.FileProperties");
            if (type == null) throw new InvalidOperationException("找不到天工属性组件 SolidEdge.FileProperties。");
            return Activator.CreateInstance(type);
        }

        // 及时关掉并释放：不释放的话文件句柄留在 CAD 进程里，后面保存这个文件会报占用
        static void Close(object sets){
            if (sets == null) return;
            try { Late.Call(sets, "Close"); } catch { }
            try { Marshal.FinalReleaseComObject(sets); } catch { }
        }

        // 按名字后期绑定（IDispatch 每次现查成员编号，不像 dynamic 那样按接口类型缓存，见文件头）
        static class Late {
            const BindingFlags Read = BindingFlags.InvokeMethod | BindingFlags.GetProperty;
            public static object Get(object o, string name, params object[] args){ return o.GetType().InvokeMember(name, Read, null, o, args); }
            public static void Call(object o, string name, params object[] args){ o.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, o, args); }
            public static void Set(object o, string name, object value){ o.GetType().InvokeMember(name, BindingFlags.SetProperty, null, o, new[] { value }); }
        }
    }
}
