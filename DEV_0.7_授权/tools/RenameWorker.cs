using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

// 批量改名的"改文件名"那一步（0.10.0）：插件把 旧路径<TAB>新路径 一行一个写进清单，这个小程序按顺序改名。
//
// 为什么不在插件里直接 File.Move：天工进程（以及设计管理器 DesMgr.exe）在加密软件的白名单里，
// 白名单进程给加密文件改名，改出来的文件**落盘就是明文**（真机 2026-10-25：插件里改名的 44 个文件全成了 D0CF11E0 明文头，
// 设计管理器 Document.Rename 也一样；不在白名单里的进程改名仍是 %TSD-Header）。所以改名交给这个不在白名单里的进程做。
// 它看到的是磁盘上的原始字节：改名前后各读一次文件头，原来加密、改完不加密就停下报错（插件会整体退回）。
//
// 用法:
//   本程序 <清单文件> <结果文件>                —— 移动（改名，批量改名用，遇错即停）
//   本程序 --best-effort <清单文件> <结果文件>  —— 回滚移动（单步失败记账并继续退回其余文件，绝不因首个文件被占而滞留临时名）
//   本程序 --copy <清单文件> <结果文件>         —— 复制（快速出清单复制原三维 / 工程图用，保持加密头不变）
// 结果文件：第一行 "OK <处理了几个>" / "FAIL <处理了几个>" / "PARTIAL <处理了几个>"，后续行为原因或 "FAIL_IDX\t<序号>\t<原因>"
static class RenameWorker {
    const string Head = "%TSD-Header";
    static int Main(string[] args){
        bool copy = args.Length >= 3 && args[0] == "--copy";
        bool bestEffort = !copy && args.Length >= 3 && args[0] == "--best-effort";
        string listFile = (copy || bestEffort) ? args[1] : args[0];
        string outFile = (copy || bestEffort) ? args[2] : args[1];
        int done = 0;
        string why = "";
        try {
            var lines = File.ReadAllLines(listFile, Encoding.UTF8);
            if (bestEffort) {
                var failures = new List<string>();
                int stepIdx = 0;
                foreach (var line in lines) {
                    if (line.Length == 0) continue;
                    int curIdx = stepIdx++;
                    try {
                        var ab = line.Split('\t');
                        if (ab.Length != 2) throw new InvalidOperationException("清单格式不对：" + line);
                        bool before = Encrypted(ab[0]);
                        Move(ab[0], ab[1]);
                        if (before && !Encrypted(ab[1])) {
                            try { Move(ab[1], ab[0]); } catch { }
                            throw new InvalidOperationException("改名后文件不再是加密的（" + Path.GetFileName(ab[0]) + "）");
                        }
                        done++;
                    } catch (Exception ex) {
                        string msg = (ex.Message ?? "").Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
                        failures.Add("FAIL_IDX\t" + curIdx + "\t" + msg);
                    }
                    try { File.WriteAllText(outFile, "RUNNING " + done + "\r\n", Encoding.UTF8); } catch { }
                }
                if (failures.Count == 0) {
                    File.WriteAllText(outFile, "OK " + done + "\r\n", Encoding.UTF8);
                    return 0;
                }
                var sb = new StringBuilder();
                sb.Append("PARTIAL ").Append(done).Append("\r\n");
                for (int i = 0; i < failures.Count; i++) sb.Append(failures[i]).Append("\r\n");
                File.WriteAllText(outFile, sb.ToString(), Encoding.UTF8);
                return 1;
            }

            foreach (var line in lines) {
                if (line.Length == 0) continue;
                var ab = line.Split('\t');
                if (ab.Length != 2) throw new InvalidOperationException("清单格式不对：" + line);
                bool before = Encrypted(ab[0]);
                if (copy) {
                    Copy(ab[0], ab[1]);
                    if (before && !Encrypted(ab[1])) {
                        try { File.Delete(ab[1]); } catch { }
                        throw new InvalidOperationException("复制后文件不再是加密的，已停止（" + Path.GetFileName(ab[0]) + "）");
                    }
                } else {
                    Move(ab[0], ab[1]);
                    if (before && !Encrypted(ab[1])) {
                        // 改回去（同一个进程改名不会再变），然后报错
                        try { Move(ab[1], ab[0]); } catch { }
                        throw new InvalidOperationException("改名后文件不再是加密的，已停止（" + Path.GetFileName(ab[0]) + "）");
                    }
                }
                done++;
                // 每做完一个记一笔：万一中途被结束，插件也知道前几个已经处理了
                File.WriteAllText(outFile, "RUNNING " + done + "\r\n", Encoding.UTF8);
            }
            File.WriteAllText(outFile, "OK " + done + "\r\n", Encoding.UTF8);
            return 0;
        } catch (Exception e) {
            why = e.Message;
            try { File.WriteAllText(outFile, "FAIL " + done + "\r\n" + why.Replace("\r", " ").Replace("\n", " ") + "\r\n", Encoding.UTF8); } catch { }
            return 1;
        }
    }

    // 天工刚关掉文档，文件句柄偶尔晚一点才放：等一下再试
    static void Move(string from, string to){
        for (int i = 0; ; i++) {
            try { File.Move(from, to); return; }
            catch (IOException) { if (i >= 10) throw; Thread.Sleep(150); }
        }
    }

    // 用共享读打开源文件、逐块写目标文件：天工正开着这个文件时照样能复制，且不经白名单进程解密
    static void Copy(string from, string to){
        string dir = Path.GetDirectoryName(to);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        for (int i = 0; ; i++) {
            try {
                using (var src = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var dst = new FileStream(to, FileMode.Create, FileAccess.Write, FileShare.None)) {
                    src.CopyTo(dst, 1 << 16);
                }
                try { File.SetLastWriteTimeUtc(to, File.GetLastWriteTimeUtc(from)); } catch { }
                return;
            } catch (IOException) {
                if (i >= 10) throw;
                Thread.Sleep(150);
            }
        }
    }

    static bool Encrypted(string path){
        var b = new byte[Head.Length];
        int n;
        using (var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) n = s.Read(b, 0, b.Length);
        if (n < b.Length) return false;
        for (int i = 0; i < b.Length; i++) if (b[i] != Head[i]) return false;
        return true;
    }
}
