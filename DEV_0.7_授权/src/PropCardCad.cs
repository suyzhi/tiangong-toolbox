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
        Dictionary<string, object> opened;   // 文件路径 → CAD 里打开着的文档（Documents，通常就几个）；第一次写时建
        // 文件路径 → 装配里引用它的第一个实例。收集时顺手记下（只读文件名，便宜）；
        // 写到哪个文件才取它的 OccurrenceDocument —— 一次把全部实例的文档取一遍，105 个零件的装配要 9 秒（真机）
        readonly Dictionary<string, A.Occurrence> occurrences = new Dictionary<string, A.Occurrence>(StringComparer.OrdinalIgnoreCase);

        public PropCardCad(F.Application application){ app = application; }

        // ---------- 收集 ----------

        // 当前文档涉及的文件：装配 = 自己 + 全部零件和子装配（逐层，去重）；工程图 = 它引用的模型；零件 = 自己。
        public List<string> Collect(out string source){
            var list = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            source = "没有打开的文档";
            object doc = null;
            try { doc = app.ActiveDocument; } catch { }
            root = doc;
            opened = null;
            occurrences.Clear();
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
                try {
                    dynamic links = ((dynamic)doc).ModelLinks;
                    for (int i = 1; i <= (int)links.Count; i++) Add(list, seen, Convert.ToString(links.Item(i).FileName));
                } catch (Exception e) { Log.Write("PropCardCollect", e); }
                source = "工程图「" + Path.GetFileName(FullName(doc)) + "」引用的模型";
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
                    try { var child = o.OccurrenceDocument as A.AssemblyDocument; if (child != null) Walk(child, list, seen); }
                    catch (Exception e) { Log.Write("PropCardWalk " + file, e); }
                }
            }
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
            object sets = null;
            try {
                sets = NewFileProperties();
                Late.Call(sets, "Open", path, true);
                object custom = Late.Get(sets, "Item", "Custom");
                foreach (var pair in FileNames(custom)) row.Original[pair.Key] = Convert.ToString(Late.Get(Late.Get(custom, "Item", pair.Value), "Value"));
                try { row.Material = Convert.ToString(Late.Get(Late.Get(Late.Get(sets, "Item", "MechanicalModeling"), "Item", "Material"), "Value")); } catch { }
            } catch (Exception e) {
                // 读不了文件（打开着的加密文件就读不了）：退回已打开的文档
                var doc = LoadedDocument(path);
                if (doc == null) { row.Problem = "读不了：" + e.Message; Log.Write("PropCardRead " + path, e); }
                else {
                    try {
                        var custom = DocCustom(doc);
                        foreach (var pair in DocNames(custom)) row.Original[pair.Key] = Convert.ToString(custom.Item(pair.Value).get_Value());
                    } catch (Exception e2) { row.Problem = "读不了：" + e2.Message; Log.Write("PropCardReadDoc " + path, e2); }
                }
            } finally { Close(sets); }
            return row;
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
                try { PutDoc(DocCustom(doc), writes, row.Edited); return SaveHint; }
                // 内存那份写不进（比如只读打开）：退回写磁盘，磁盘上的才算数
                catch (Exception e) { Log.Write("PropCardWriteDoc " + row.Path, e); }
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

        // 文件在不在 CAD 内存里：打开着的文档、当前工程图引用的模型、当前装配里引用它的实例
        // （加密文件在内存里时属性组件写不进去，只能写内存里那份 —— 工程图引用的零件就是这样，0.8.0 现场）
        object LoadedDocument(string path){
            if (opened == null) {
                opened = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                try {
                    var docs = app.Documents;
                    for (int i = 1; i <= docs.Count; i++) { object d = docs.Item(i); Remember(FullName(d), d); }
                } catch (Exception e) { Log.Write("PropCardDocs", e); }
                if (root is SolidEdgeDraft.DraftDocument) {
                    try {
                        var links = ((SolidEdgeDraft.DraftDocument)root).ModelLinks;
                        for (int i = 1; i <= links.Count; i++) {
                            var link = links.Item(i);
                            try { Remember(link.FileName, link.ModelDocument); } catch (Exception e) { Log.Write("PropCardModelLink", e); }
                        }
                    } catch (Exception e) { Log.Write("PropCardModelLinks", e); }
                }
            }
            object hit;
            if (opened.TryGetValue(Key(path), out hit)) return hit;
            A.Occurrence o;
            if (!occurrences.TryGetValue(Key(path), out o)) return null;
            try { return o.OccurrenceDocument; } catch { return null; }
        }

        void Remember(string file, object doc){ if (!string.IsNullOrEmpty(file) && !opened.ContainsKey(Key(file))) opened[Key(file)] = doc; }

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
