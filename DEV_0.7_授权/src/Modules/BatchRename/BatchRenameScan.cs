using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace TianGongCadSuite {
    // 批量改名的快速初筛：一个文件里有没有可能链着这批要改名的文件。
    //
    // 设计管理器列一个装配的链接要 38 ms（进程外，一个链接一次调用）；同文件夹里几十上百个装配全列一遍要好几秒，
    // 而真链着要改名文件的通常就几个。链接表里存着被链文件的名字，所以先把文件字节读一遍，
    // 看这批旧文件名有没有出现过：一个都没出现的肯定不链它们，不必再开。
    // 读字节在天工进程里做：DLP 加密文件在天工进程里读出来是明文（属性组件同理）；
    // 读出来还是加密头（%TSD-Header）就说明这台机器上解不开 —— 那就不筛，一律交给设计管理器。
    //
    // 只做"排除"，宁可多开不可漏开：
    //   · 名字的编码 UTF-16 / UTF-8 / GBK 都认，英文字母不分大小写；
    //   · 按"旧名字是否以子串出现"判断，不去切出完整文件名 —— 切的话名字前面紧挨着的长度前缀之类的字节
    //     可能被当成字符粘上去（比如长度 0x4A 读成 'J'），就对不上号、漏掉真链接。
    public sealed class RenameRefScan {
        readonly List<byte[]> patterns = new List<byte[]>();
        readonly List<bool> wide = new List<bool>();   // 这个模式是不是 UTF-16（比较时按两字节一个字符忽略大小写）
        static readonly Encoding Gbk = SafeGbk();
        static Encoding SafeGbk(){ try { return Encoding.GetEncoding(936); } catch { return null; } }

        // oldNames：要改名的文件名（不含路径，带扩展名）
        public RenameRefScan(IEnumerable<string> oldNames){
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var raw in oldNames) {
                string name = LowerAscii(raw);
                if (name.Length == 0 || !seen.Add(name)) continue;
                patterns.Add(Encoding.Unicode.GetBytes(name)); wide.Add(true);
                Add(Encoding.UTF8.GetBytes(name));
                if (Gbk != null) Add(Gbk.GetBytes(name));
            }
        }
        void Add(byte[] p){
            for (int i = 0; i < patterns.Count; i++) if (!wide[i] && Same(patterns[i], p)) return;
            patterns.Add(p); wide.Add(false);
        }
        public int Count { get { return patterns.Count; } }

        // 读不了、解不开 = 不知道 = 当成可能链着
        public bool MayReference(string path){
            byte[] data;
            try {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
                    if (fs.Length > 512L * 1024 * 1024) return true;
                    data = new byte[fs.Length];
                    int at = 0;
                    while (at < data.Length) { int n = fs.Read(data, at, data.Length - at); if (n <= 0) break; at += n; }
                }
            } catch { return true; }
            if (Encrypted(data)) return true;
            return MayReference(data);
        }

        // 旧名字都以扩展名结尾（.par / .psm / .asm），所以只在出现 '.' 的地方往前比
        public bool MayReference(byte[] data){
            if (patterns.Count == 0) return false;
            for (int i = 0; i < data.Length; i++) {
                if (data[i] != (byte)'.') continue;
                for (int k = 0; k < patterns.Count; k++) if (EndsHere(data, i, patterns[k], wide[k])) return true;
            }
            return false;
        }

        // 模式里的 '.' 对上 data[dot] 时，整个模式在不在这里（扩展名在 '.' 后面，名字主体在前面）
        static bool EndsHere(byte[] data, int dot, byte[] p, bool isWide){
            int pd = LastDot(p, isWide);
            if (pd < 0) return false;
            int start = dot - pd;
            if (start < 0 || start + p.Length > data.Length) return false;
            for (int j = 0; j < p.Length; j++) {
                byte b = data[start + j];
                // 只在模式这一位是小写英文字母时忽略大小写（UTF-16 看低字节）。GBK 汉字的第二个字节也可能落在 A～Z，
                // 不能一律转小写，否则同一个汉字反而对不上；放宽只会多开几个文件，不会漏
                if (p[j] >= 'a' && p[j] <= 'z' && (!isWide || j % 2 == 0) && b >= 'A' && b <= 'Z') b = (byte)(b + 32);
                if (b != p[j]) return false;
            }
            return true;
        }
        static int LastDot(byte[] p, bool isWide){
            for (int j = p.Length - (isWide ? 2 : 1); j >= 0; j -= isWide ? 2 : 1) if (p[j] == (byte)'.' && (!isWide || p[j + 1] == 0)) return j;
            return -1;
        }

        public static bool Encrypted(byte[] data){
            const string head = "%TSD-Header";
            if (data.Length < head.Length) return false;
            for (int i = 0; i < head.Length; i++) if (data[i] != head[i]) return false;
            return true;
        }

        // 只有链接里会出现的扩展名才有意义：工程图、显示配置本身不会被谁链着
        public static bool Linkable(string path){
            string e = Path.GetExtension(path).ToLowerInvariant();
            return e == ".par" || e == ".psm" || e == ".asm";
        }

        static string LowerAscii(string s){
            var sb = new StringBuilder(s ?? "");
            for (int i = 0; i < sb.Length; i++) if (sb[i] >= 'A' && sb[i] <= 'Z') sb[i] = (char)(sb[i] + 32);
            return sb.ToString();
        }
        static bool Same(byte[] a, byte[] b){
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
    }
}
