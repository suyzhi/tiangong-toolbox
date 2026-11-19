using System;
using System.IO;

namespace TianGongCadSuite {
    // 插件日志：%LOCALAPPDATA%\TianGongCadSuite\panel.log。
    //
    // 两条规矩（2026-10-14 整理时定的）：
    //   · 有上限：超过 MaxBytes 就把当前文件改名成 panel.log.1（只留一份旧的），再从空文件写起。
    //     原来没有上限，用了一个月涨到 2MB，其中 1.3 万行是下面那种"诊断用"的 ContextProbe。
    //   · 分级：Write 是"出了事要看的"（异常、打孔失败、版本自报），一律落盘；
    //     Debug 是排查时才要的流水（按钮为什么变灰之类），平时不写，
    //     要看时设环境变量 TG_DEBUG_LOG=1 再启动 CAD。
    public static class Log {
        public const long MaxBytes = 1024 * 1024;
        static readonly object gate = new object();

        public static string Folder { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TianGongCadSuite"); } }
        public static string FilePath { get { return Path.Combine(Folder, "panel.log"); } }

        static bool? debug;
        public static bool DebugEnabled {
            get {
                if (!debug.HasValue) {
                    string v = null;
                    try { v = Environment.GetEnvironmentVariable("TG_DEBUG_LOG"); } catch { }
                    debug = v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
                }
                return debug.Value;
            }
            set { debug = value; }
        }

        public static void Write(string area, Exception e){ Write(area, e == null ? "" : e.ToString()); }
        public static void Write(string area, string detail){ Append(FilePath, area, detail); }
        public static void Debug(string area, string detail){ if (DebugEnabled) Write(area, detail); }

        // 测试用：往指定文件写（滚动规则相同）
        internal static void Append(string file, string area, string detail){
            try {
                lock (gate) {
                    Directory.CreateDirectory(Path.GetDirectoryName(file));
                    Roll(file, MaxBytes);
                    File.AppendAllText(file, DateTime.Now.ToString("s") + " " + area + " " + detail + Environment.NewLine);
                }
            } catch { }
        }

        // 超过上限：当前文件 → .1（覆盖更早的那份），返回是否滚动过
        internal static bool Roll(string file, long maxBytes){
            try {
                var info = new FileInfo(file);
                if (!info.Exists || info.Length < maxBytes) return false;
                string old = file + ".1";
                if (File.Exists(old)) File.Delete(old);
                File.Move(file, old);
                return true;
            } catch { return false; }
        }
    }
}
