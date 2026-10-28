using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace TianGongCadSuite {
    // 插件自己的小设置文件（排孔预设、配孔规则表……），放在 %LOCALAPPDATA%\TianGongCadSuite\ 下。
    //
    // 格式故意做成"记事本能看懂、Excel 能打开"的制表符分隔文本（UTF-8 带 BOM）：
    //   · # 开头的行是注释；第一行注释写明这是什么文件、每列什么意思；
    //   · 一行一条记录，字段之间一个制表符；字段里的 \ 制表符 换行 写成 \\ \t \n。
    // 写入先写临时文件再替换：写到一半断电/崩溃，旧文件还在，不会只剩半个文件。
    // 纯逻辑，不依赖 CAD / WinForms（tests/pure 里测）。
    public static class SettingsFile {
        public static string PathOf(string fileName){ return Path.Combine(Log.Folder, fileName); }

        public static List<string[]> Read(string path){
            var rows = new List<string[]>();
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return rows;
            // 共享读：文件头写着"Excel 能打开"——用户真用 Excel 开着它时，File.ReadAllLines 会因为占用直接抛，
            // 属性卡填写 / 批量排孔的窗口就打不开了。读不了就当没有这个文件（用默认值），记一笔日志。
            string[] lines;
            try {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(fs, Encoding.UTF8, true)) {
                    var all = new List<string>();
                    string l;
                    while ((l = reader.ReadLine()) != null) all.Add(l);
                    lines = all.ToArray();
                }
            } catch (IOException e) { Unreadable(path, e); return rows; }
            catch (UnauthorizedAccessException e) { Unreadable(path, e); return rows; }
            lock (unreadable) unreadable.Remove(Path.GetFullPath(path));
            foreach (var raw in lines) {
                string line = raw.TrimEnd('\r');
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                var parts = line.Split('\t');
                for (int i = 0; i < parts.Length; i++) parts[i] = Unescape(parts[i]);
                rows.Add(parts);
            }
            return rows;
        }

        // 这次读不出来的文件：调用方手里只有默认值，这时候按"整份重写"保存会把文件里原有的内容（排孔预设、列设置……）全冲掉。
        // 重新读成功之前不许写。
        static readonly HashSet<string> unreadable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static void Unreadable(string path, Exception e){
            Log.Write("SettingsRead " + Path.GetFileName(path), e.Message);
            lock (unreadable) unreadable.Add(Path.GetFullPath(path));
        }

        public static void Write(string path, IEnumerable<string[]> rows, params string[] comments){
            bool blocked;
            lock (unreadable) blocked = unreadable.Contains(Path.GetFullPath(path));
            if (blocked && File.Exists(path))
                throw new IOException("设置文件「" + Path.GetFileName(path) + "」刚才读不出来（可能被 Excel 之类的程序开着），为免把里面原有的内容覆盖掉，这次没保存。关掉占用它的程序、重新打开窗口后再改。");
            var sb = new StringBuilder();
            if (comments != null) foreach (var c in comments) sb.Append("# ").Append(c ?? "").Append("\r\n");
            foreach (var r in rows) {
                for (int i = 0; i < r.Length; i++) { if (i > 0) sb.Append('\t'); sb.Append(Escape(r[i])); }
                sb.Append("\r\n");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(true));
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }

        // 用户自己拿来导入的表格文本（排位表清单 / 型号库 CSV）（0.10.2）：
        //   · 原来按 UTF-8 读：WPS / Excel「另存为 CSV」在中文 Windows 上默认是 GBK，导进来中文全成乱码；
        //   · 原来 File.ReadAllText：文件还在 Excel 里开着就"被另一进程占用"直接失败。
        // 有 BOM 按 BOM；没有就先按严格 UTF-8 解，解不通再按 GBK（GB18030 兼容）。共享读。
        public static string ReadUserText(string path){
            byte[] data;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var ms = new MemoryStream()) { fs.CopyTo(ms); data = ms.ToArray(); }
            return DecodeUserText(data);
        }
        // 导出给用户的表格（UTF-8 带 BOM，Excel 才认中文）。上次导出的文件还在 Excel / WPS 里开着时，原来报一句
        // "文件正由另一进程使用"，用户不知道是哪个程序；这里说清楚（0.10.2）
        public static void WriteUserText(string path, string text){
            try { File.WriteAllText(path, text, new UTF8Encoding(true)); }
            catch (IOException e) {
                int hr = e.HResult & 0xFFFF;
                if (hr == 32 || hr == 33) throw new IOException("「" + Path.GetFileName(path) + "」正被别的程序（多半是 Excel / WPS）打开着，写不进去。先在那边关掉它再导出，或者换个文件名。", e);
                throw;
            }
        }
        public static string DecodeUserText(byte[] data){
            if (data == null || data.Length == 0) return "";
            if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) return Encoding.UTF8.GetString(data, 3, data.Length - 3);
            if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE) return Encoding.Unicode.GetString(data, 2, data.Length - 2);
            if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2);
            try { return new UTF8Encoding(false, true).GetString(data); }
            catch (DecoderFallbackException) { }
            try { return Encoding.GetEncoding(936).GetString(data); } catch (Exception) { return Encoding.Default.GetString(data); }
        }

        public static string Escape(string s){
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 4);
            foreach (char c in s) {
                if (c == '\\') sb.Append("\\\\");
                else if (c == '\t') sb.Append("\\t");
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') { }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        public static string Unescape(string s){
            if (string.IsNullOrEmpty(s) || s.IndexOf('\\') < 0) return s ?? "";
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++) {
                char c = s[i];
                if (c != '\\' || i + 1 >= s.Length) { sb.Append(c); continue; }
                char n = s[++i];
                if (n == 't') sb.Append('\t'); else if (n == 'n') sb.Append('\n'); else sb.Append(n);
            }
            return sb.ToString();
        }
    }
}
