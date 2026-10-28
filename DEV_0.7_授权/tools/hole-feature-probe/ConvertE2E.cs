using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

// 0.10.2：格式转换真机——把一个真实的 SolidWorks 装配（零件、装配体分在不同文件夹）用转换器的 worker 转成天工格式。
// 转换器要独占一个天工：先把队列起的私有桌面天工关掉，再起 worker（它自己 CreateInstance 一个隐藏的天工）。
// 用法: ConvertE2E.exe <输出目录> <TianGongConverter.exe> <源装配> <源根目录> <flat 0/1> [分钟]
class ConvertE2E {
    static void L(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    static int Main(string[] args){
        Console.OutputEncoding = Encoding.UTF8;
        string outDir = Path.GetFullPath(args[0]), exe = args[1], src = args[2], root = args[3], flat = args[4];
        int minutes = args.Length > 5 ? int.Parse(args[5]) : 30;
        // ① 关掉队列起的天工（就是这个私有桌面上的那个）
        try {
            dynamic app = Marshal.GetActiveObject("SolidEdge.Application");
            int pid = 0; try { pid = app.ProcessID; } catch { }
            try { app.Quit(); } catch { }
            Marshal.FinalReleaseComObject(app);
            for (int i = 0; i < 60 && pid != 0; i++) { try { if (Process.GetProcessById(pid).HasExited) break; } catch { break; } Thread.Sleep(1000); }
            L("队列的天工已关（pid " + pid + "）");
        } catch (Exception e) { L("没连上队列的天工（" + e.Message + "），直接转"); }
        string work = Path.Combine(outDir, "work"); Directory.CreateDirectory(work);
        string output = Path.Combine(outDir, "out"); Directory.CreateDirectory(output);
        string job = Path.Combine(work, "job-0.txt"), status = Path.Combine(work, "status-0.txt");
        var lines = new StringBuilder(); int k = 0;
        foreach (var one in src.Split(';')) if (one.Trim().Length > 0) lines.Append(k++).Append('\t').Append(one.Trim()).Append('\t').Append(root).Append("\t0\r\n");   // 多个源用 ; 隔开
        File.WriteAllText(job, lines.ToString(), new UTF8Encoding(false));
        File.WriteAllText(status, "", new UTF8Encoding(false));
        var t0 = DateTime.Now;
        var p = Process.Start(new ProcessStartInfo(exe, "--worker \"" + job + "\" \"" + status + "\" \"" + output + "\" " + flat + " 0 1 \"" + work + "\"") { UseShellExecute = false, CreateNoWindow = true });
        if (!p.WaitForExit(minutes * 60000)) { L("FAIL 超时 " + minutes + " 分钟，杀掉 worker"); try { p.Kill(); } catch { } }
        L("worker 退出码 " + (p.HasExited ? p.ExitCode.ToString() : "?") + "，用时 " + (DateTime.Now - t0).TotalSeconds.ToString("0") + "s");
        foreach (var line in File.ReadAllLines(status, Encoding.UTF8)) L("  " + (line.Length > 300 ? line.Substring(0, 300) + "…" : line));
        var files = Directory.GetFiles(output, "*.*", SearchOption.AllDirectories);
        L("输出文件 " + files.Length + "：" + string.Join("，", files.GroupBy(f => Path.GetExtension(f).ToLowerInvariant()).Select(g => g.Key + "×" + g.Count()).ToArray()));
        foreach (var d in Directory.GetDirectories(output, "*", SearchOption.AllDirectories)) L("  目录 " + d.Substring(output.Length));
        // worker 起的天工应该自己退了
        Thread.Sleep(3000);
        L("残留天工进程 " + Process.GetProcessesByName("TianGong").Length);
        return 0;
    }
}
