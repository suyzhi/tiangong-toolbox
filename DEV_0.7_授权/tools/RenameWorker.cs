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
// 用法: 本程序 <清单文件> <结果文件>
// 结果文件：第一行 "OK <改了几个>" 或 "FAIL <改了几个>"，第二行是原因
static class RenameWorker {
    const string Head = "%TSD-Header";
    static int Main(string[] args){
        int done = 0;
        string why = "";
        try {
            var lines = File.ReadAllLines(args[0], Encoding.UTF8);
            foreach (var line in lines) {
                if (line.Length == 0) continue;
                var ab = line.Split('\t');
                if (ab.Length != 2) throw new InvalidOperationException("清单格式不对：" + line);
                bool before = Encrypted(ab[0]);
                Move(ab[0], ab[1]);
                if (before && !Encrypted(ab[1])) {
                    // 改回去（同一个进程改名不会再变），然后报错
                    try { Move(ab[1], ab[0]); } catch { }
                    throw new InvalidOperationException("改名后文件不再是加密的，已停止（" + Path.GetFileName(ab[0]) + "）");
                }
                done++;
                // 每改好一个记一笔：万一中途被结束，插件也知道前几个已经改了、要改回来
                File.WriteAllText(args[1], "RUNNING " + done + "\r\n", Encoding.UTF8);
            }
            File.WriteAllText(args[1], "OK " + done + "\r\n", Encoding.UTF8);
            return 0;
        } catch (Exception e) {
            why = e.Message;
            try { File.WriteAllText(args[1], "FAIL " + done + "\r\n" + why.Replace("\r", " ").Replace("\n", " ") + "\r\n", Encoding.UTF8); } catch { }
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

    static bool Encrypted(string path){
        var b = new byte[Head.Length];
        int n;
        using (var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) n = s.Read(b, 0, b.Length);
        if (n < b.Length) return false;
        for (int i = 0; i < b.Length; i++) if (b[i] != Head[i]) return false;
        return true;
    }
}
