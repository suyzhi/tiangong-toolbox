using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using DM = DesignManager;

namespace TianGongCadSuite {
    // 批量改名：改"谁引用了这些文件"那一头的链接（装配 → 零件、工程图 → 模型、零件间复制 → 源文件）。
    //
    // 走天工自带的设计管理器接口（DesignManager.Application，DesMgr.exe 进程外）：只读写文件里的链接表，
    // 不加载几何。真机实测（2026-10-25，加密副本 YF25.07G01-L1-M30-02）：
    //   · DesMgr.exe 读写 DLP 加密文件没问题（写完仍是 %TSD-Header）；
    //   · 打开一个文件 1 ms，列链接：工程图 15 ms、装配 38 ms、零件 5 ms；改链接 + 存一个父文件 6～14 ms；
    //   · 改链接要在**每个父文件自己打开的那份**上改（SetPath + SaveAllLinks）。同一个零件从工程图那头改名（Document.Rename），
    //     只有那张工程图跟着变，子装配里的引用不变 —— 天工自带"重命名"漏改的就是这种；
    //   · 先改链接（目标文件还不存在也行）、再改磁盘文件名：子装配自己也改名时顺序不打架，也不依赖"找不到文件时去哪找"。
    //     反过来先改文件名，设计管理器解析断了的链接会按原来存的绝对路径去找（测试副本里找回到了原件目录 H:\…），对不上号。
    //   · **不要用 Document.Rename 改名**：DesMgr.exe 在加密白名单里，白名单进程改名，改出来的文件落盘是明文。
    //     改链接（原地写）不会；改磁盘文件名交给 SuiteRenameWorker.exe（见 BatchRenameCad.MoveFiles）。
    // 改完用真天工打开核对过：装配实例 0 缺件，工程图链到新文件、视图不过期。
    public sealed class RenameLinkFixer : IDisposable {
        // 一个父文件里要改的链接
        public sealed class Parent {
            public string Path;
            internal DM.Document Doc;
            public readonly List<Edit> Edits = new List<Edit>();
            public bool Saved;
        }
        public sealed class Edit {
            internal DM.Document Child;
            public string OldPath, NewPath;
        }

        DM.Application dm;
        bool ownServer;
        readonly List<Parent> parents = new List<Parent>();
        public IList<Parent> Parents { get { return parents.AsReadOnly(); } }
        public int Scanned { get; private set; }
        public int ScanFailed { get; private set; }
        public readonly List<string> ScanProblems = new List<string>();

        public RenameLinkFixer(){
            // 用户自己开着设计管理器时，接口可能连到同一个进程：那就不能 Quit（会把用户的窗口关掉）
            ownServer = Process.GetProcessesByName("DesMgr").Length == 0;
            var type = Type.GetTypeFromProgID("DesignManager.Application");
            if (type == null) throw new InvalidOperationException("找不到天工设计管理器的接口（DesignManager.Application），天工没装完整？");
            dm = (DM.Application)Activator.CreateInstance(type);
            try { dm.DisplayAlerts = 0; } catch { }
        }

        // 逐个打开候选父文件，记下链到 map 里旧路径的那些链接。map：旧路径 → 新路径（不区分大小写）。
        // 候选文件自己也在 map 里（子装配改名）也照样扫：它里面的链接要先改好再改它的名字
        public void Scan(IEnumerable<string> candidates, IDictionary<string, string> map, Action<int, string> progress){
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in parents) seen.Add(p.Path);
            foreach (var path in candidates) {
                string key = Full(path);
                if (!seen.Add(key)) continue;
                Scanned++;
                if (progress != null) progress(Scanned, path);
                DM.Document doc = null;
                try {
                    doc = (DM.Document)dm.Open(key);
                    var links = (DM.LinkedDocuments)doc.get_LinkedDocuments(Type.Missing);
                    Parent hit = null;
                    int count = links.Count;
                    for (int i = 1; i <= count; i++) {
                        var child = (DM.Document)links.get_Item(i);
                        string target;
                        if (!map.TryGetValue(Full(child.FullName), out target)) { Release(child); continue; }
                        if (hit == null) hit = new Parent { Path = key, Doc = doc };
                        hit.Edits.Add(new Edit { Child = child, OldPath = Full(child.FullName), NewPath = target });
                    }
                    Release(links);
                    if (hit != null) { parents.Add(hit); doc = null; }
                } catch (Exception e) {
                    ScanFailed++;
                    ScanProblems.Add(System.IO.Path.GetFileName(key) + "：" + e.Message);
                    Log.Write("RenameScan " + key, e);
                } finally { if (doc != null) Release(doc); }
            }
        }

        // 把链接改到新路径并存盘。哪个父文件存不进去：已经改好的退回原样，再抛出
        public void Apply(Action<int, string> progress){
            int n = 0;
            foreach (var p in parents) {
                n++;
                if (progress != null) progress(n, p.Path);
                try {
                    foreach (var e in p.Edits) Check(e.Child.SetPath(e.NewPath), "改链接 " + System.IO.Path.GetFileName(e.OldPath));
                    Check(p.Doc.SaveAllLinks(), "保存");
                    p.Saved = true;
                } catch (Exception ex) {
                    Log.Write("RenameApply " + p.Path, ex);
                    string why = System.IO.Path.GetFileName(p.Path) + " 里的引用改不进去：" + ex.Message;
                    // 这一个可能改了一半（SetPath 了、没存上）：也按原样退一遍
                    p.Saved = true;
                    Revert();
                    throw new InvalidOperationException(why, ex);
                }
            }
        }

        // 退回原来的链接（Apply 失败、或者后面改磁盘文件名失败时）。尽力而为，失败的记日志、返回失败的文件
        public List<string> Revert(){
            var failed = new List<string>();
            foreach (var p in parents) {
                if (!p.Saved) continue;
                try {
                    foreach (var e in p.Edits) e.Child.SetPath(e.OldPath);
                    Check(p.Doc.SaveAllLinks(), "保存");
                    p.Saved = false;
                } catch (Exception ex) { failed.Add(p.Path); Log.Write("RenameRevert " + p.Path, ex); }
            }
            return failed;
        }

        static void Check(int code, string what){ if (code != 0) throw new InvalidOperationException(what + "失败（返回 " + code + "）"); }

        static string Full(string path){ try { return System.IO.Path.GetFullPath(path); } catch { return path ?? ""; } }
        static void Release(object o){ try { if (o != null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o); } catch { } }

        public void Dispose(){
            foreach (var p in parents) { foreach (var e in p.Edits) Release(e.Child); Release(p.Doc); }
            parents.Clear();
            if (dm == null) return;
            if (ownServer) { try { dm.Quit(); } catch { } }
            Release(dm);
            dm = null;
        }
    }
}
