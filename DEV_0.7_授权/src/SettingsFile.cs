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
            foreach (var raw in File.ReadAllLines(path, Encoding.UTF8)) {
                string line = raw.TrimEnd('\r');
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                var parts = line.Split('\t');
                for (int i = 0; i < parts.Length; i++) parts[i] = Unescape(parts[i]);
                rows.Add(parts);
            }
            return rows;
        }

        public static void Write(string path, IEnumerable<string[]> rows, params string[] comments){
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
