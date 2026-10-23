using System;
using System.Collections.Generic;
using System.IO;
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
    // 已经在 CAD 内存里的文件（打开着的文档、装配里加载着的零件）要多想一步（真机实测）：
    //   · 装配里加载着的零件：属性组件照样写得进磁盘，但内存里那份还是旧属性 —— 之后用户改了这个零件再保存，
    //     会把新属性冲掉。所以写完磁盘，内存里那份也照写一遍（它因此变成"已修改"，跟着装配一起保存，值是一样的）；
    //   · 当前打开的文档本身（比如正开着的装配）：属性组件打不开（文件被占用），只写进内存，**不替用户保存** ——
    //     从插件里保存装配会弹对话框把 CAD 卡住（真机），让用户自己 Ctrl+S。
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
            dynamic sets = null;
            try {
                sets = NewFileProperties();
                sets.Open(path, true);
                foreach (var pair in ReadCustom(sets.Item("Custom"), FileBase)) row.Original[pair.Key] = pair.Value;
                try { row.Material = Convert.ToString(sets.Item("MechanicalModeling").Item("Material").Value); } catch { }
            } catch (Exception e) {
                // 读不了文件（打开着的加密文件就读不了）：退回已打开的文档
                var doc = LoadedDocument(path);
                if (doc == null) { row.Problem = "读不了：" + e.Message; Log.Write("PropCardRead " + path, e); }
                else {
                    try { foreach (var pair in ReadCustom(DocCustom(doc), DocBase)) row.Original[pair.Key] = pair.Value; }
                    catch (Exception e2) { row.Problem = "读不了：" + e2.Message; Log.Write("PropCardReadDoc " + path, e2); }
                }
            } finally { Close(sets); }
            return row;
        }

        // 下标起点写死，绝不"试一下"：属性组件的集合从 0 数；文档的 Properties 从 1 数，
        // 而且在文档集合上取越界的下标（Item(0)）不是报错，是 FWKAUTO.DLL 里直接访问冲突、天工整个崩掉
        // （0.8.0 现场：打开加密零件点本命令就崩，.NET 拦不住这种异常）。按名字取不存在的属性也不碰，先列出已有的名字。
        const int FileBase = 0, DocBase = 1;

        static Dictionary<string, string> ReadCustom(dynamic custom, int start){
            var map = new Dictionary<string, string>();
            int count = (int)custom.Count;
            for (int i = start; i < start + count; i++) {
                try {
                    dynamic p = custom.Item(i);
                    string name = Convert.ToString(p.Name);
                    object value = null;
                    try { value = p.Value; } catch { }
                    if (!string.IsNullOrEmpty(name)) map[name] = Convert.ToString(value);
                } catch { }
            }
            return map;
        }

        // ---------- 写 ----------

        // 返回空串 = 写好并已存盘；SaveHint = 只写进了打开着的文档（要用户自己保存）；其他 = 失败原因
        public string Write(PropCardRow row, List<KeyValuePair<string, string>> writes){
            if (writes.Count == 0) return "";
            object doc = LoadedDocument(row.Path);
            dynamic sets = null;
            Exception fileError = null;
            try {
                sets = NewFileProperties();
                sets.Open(row.Path, false);
                Put(sets.Item("Custom"), writes, false);
                sets.Save();
            } catch (Exception e) { fileError = e; } finally { Close(sets); }

            if (fileError == null) {
                // 磁盘写好了；内存里那份同步一下，失败不影响结果（磁盘上已经是新值）
                if (doc != null) try { Put(DocProperties(doc).Item("Custom"), writes, true); } catch (Exception e) { Log.Write("PropCardMirror " + row.Path, e); }
                return "";
            }
            if (doc == null) { Log.Write("PropCardWrite " + row.Path, fileError); return "写不进去：" + fileError.Message; }
            Log.Write("PropCardWrite 改写内存里的文档 " + row.Path, fileError.Message);
            try {
                Put(DocProperties(doc).Item("Custom"), writes, true);
                return SaveHint;
            } catch (Exception e) { Log.Write("PropCardWriteDoc " + row.Path, e); return "写不进去：" + e.Message; }
        }

        // typed：打开着的文档的属性集合要按接口类型调 Add（dynamic 调报 DISP_E_NOTACOLLECTION，真机）；
        // 属性组件那边反过来，只能 dynamic（按接口类型转会转成功、一调就 DISP_E_MEMBERNOTFOUND）
        static void Put(dynamic custom, List<KeyValuePair<string, string>> writes, bool typed){
            // 先按下标列出已有的属性（名字 → 下标），只对已有的按下标改值；没有的才 Add（不靠"取一下看报不报错"）
            int start = typed ? DocBase : FileBase, count = (int)custom.Count;
            var at = new Dictionary<string, int>();
            for (int i = start; i < start + count; i++) {
                try { string name = Convert.ToString(custom.Item(i).Name); if (!string.IsNullOrEmpty(name) && !at.ContainsKey(name)) at[name] = i; } catch { }
            }
            foreach (var pair in writes) {
                int index;
                if (at.TryGetValue(pair.Key, out index)) {
                    try { dynamic p = custom.Item(index); p.Value = pair.Value; continue; }
                    catch (Exception e) { throw new InvalidOperationException("「" + pair.Key + "」写不进去（原来不是文字属性？）：" + e.Message); }
                }
                if (typed) ((F.Properties)custom).Add(pair.Key, pair.Value);
                else custom.Add(pair.Key, pair.Value);
            }
        }

        // ---------- 已打开的文档 ----------

        // 文档的属性集合按接口类型取：从 Documents.Item / OccurrenceDocument 拿到的是裸 IDispatch，
        // 在它上面用 dynamic 调 Properties.Item("Custom") 会报 DISP_E_NOTACOLLECTION（真机，打开着的装配）
        static F.PropertySets DocProperties(object doc){
            object sets = doc.GetType().InvokeMember("Properties", System.Reflection.BindingFlags.GetProperty, null, doc, null);
            return (F.PropertySets)sets;
        }
        static F.Properties DocCustom(object doc){ return DocProperties(doc).Item("Custom"); }

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

        static dynamic NewFileProperties(){
            var type = Type.GetTypeFromProgID("SolidEdge.FileProperties");
            if (type == null) throw new InvalidOperationException("找不到天工属性组件 SolidEdge.FileProperties。");
            return Activator.CreateInstance(type);
        }

        // 及时关掉并释放：不释放的话文件句柄留在 CAD 进程里，后面保存这个文件会报占用
        static void Close(dynamic sets){
            if (sets == null) return;
            try { sets.Close(); } catch { }
            try { Marshal.FinalReleaseComObject((object)sets); } catch { }
        }
    }
}
