using System;
using System.Collections.Generic;
using System.Text;

namespace TianGongCadSuite {
    // 批量改名（0.10.0 新增，命令 12）的一行 = 一个文件。
    //
    // 新名字只改"文件名主体"，扩展名不让改（.par 改成 .asm 天工就不认了）。
    // 跟随行（Master != null）：零件改名时同文件夹里同名的工程图（.dft）、装配的显示配置（.cfg）跟着改，
    // 名字永远等于主行的新名字，除非用户直接改了跟随行（Pinned）。
    // 纯逻辑，不依赖 CAD / WinForms（tests/pure 里测）。
    public sealed class RenameRow {
        public string Path = "";
        public string Problem = "";          // 校验出的问题（空 = 可以改）
        public string ReadProblem = "";      // 读属性时出的问题（只影响「按属性命名」）
        public string Locked = "";           // 不该 / 不能改名的原因（标准件库里的、只读的）：载入时查一次，改了新名字就报问题
        public RenameRow Master;             // 跟随谁（同名工程图 / .cfg）；null = 普通行
        public bool Pinned;                  // 跟随行被用户直接改过：不再跟着主行变
        public readonly Dictionary<string, string> Props = new Dictionary<string, string>();
        public bool PropsLoaded;
        string newBase;

        public string FileName { get { return System.IO.Path.GetFileName(Path); } }
        public string Folder { get { return System.IO.Path.GetDirectoryName(Path) ?? ""; } }
        public string Extension { get { return System.IO.Path.GetExtension(Path); } }
        public string OldBase { get { return System.IO.Path.GetFileNameWithoutExtension(Path); } }
        public string Kind { get { return KindOf(Path); } }

        // 新名字主体（不含扩展名）；跟随行没被改过时等于主行的
        public string NewBase {
            get {
                if (Master != null && !Pinned) return Master.NewBase;
                return newBase ?? OldBase;
            }
        }
        public string NewPath { get { return System.IO.Path.Combine(Folder, NewBase + Extension); } }
        public string NewFileName { get { return NewBase + Extension; } }
        // 只改了大小写也算改（Windows 上 A.par → a.par 是一次真改名）
        public bool Changed { get { return !string.Equals(NewBase, OldBase, StringComparison.Ordinal); } }

        public void SetNewBase(string value){
            value = RenameRules.Clean(value);
            // 粘进来带了扩展名（从资源管理器复制的整个文件名）：去掉，扩展名不让改
            if (value.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) && value.Length > Extension.Length)
                value = value.Substring(0, value.Length - Extension.Length);
            if (Master != null) {
                Pinned = !string.Equals(value, Master.NewBase, StringComparison.Ordinal);
                newBase = Pinned ? value : null;
                return;
            }
            newBase = string.Equals(value, OldBase, StringComparison.Ordinal) ? null : value;
        }
        public void Revert(){ newBase = null; Pinned = false; }

        public static string KindOf(string path){
            switch (System.IO.Path.GetExtension(path).ToLowerInvariant()) {
                case ".par": return "零件";
                case ".psm": return "钣金";
                case ".asm": return "装配";
                case ".dft": return "工程图";
                case ".cfg": return "显示配置";
                default: return "文件";
            }
        }
    }

    // 一次真实的改名：From → To。Temp 非空 = 两步走（先改成临时名，最后再改成 To），
    // 用于"新名字正好是这批里另一个文件的旧名字"（序号整体后移 01→02、02→03，或两个文件互换名字）
    public sealed class RenameMove {
        public string From, To, Temp;
        public override string ToString(){ return From + " → " + To + (Temp != null ? "（经 " + Temp + "）" : ""); }
    }

    public static class RenameRules {
        static readonly char[] Bad = System.IO.Path.GetInvalidFileNameChars();
        static readonly string[] Reserved = { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };
        // 天工 / Windows 文件路径上限（MAX_PATH 260，含结尾 0）
        public const int MaxPath = 259;

        // 粘贴带的空白、回车去掉
        public static string Clean(string value){
            if (value == null) return "";
            value = value.Replace("\r", "").Replace("\n", "").Replace("\t", " ");
            return value.Trim();
        }

        // 新名字主体本身合不合法（不看重名）。返回空串 = 合法
        public static string CheckName(string name){
            if (name.Length == 0) return "新名字是空的";
            int bad = name.IndexOfAny(Bad);
            if (bad >= 0) return "名字里不能有「" + name[bad] + "」（\\ / : * ? \" < > | 都不行）";
            if (name.EndsWith(".") || name.EndsWith(" ")) return "名字不能以点或空格结尾";
            string upper = name.ToUpperInvariant();
            foreach (var r in Reserved) if (upper == r) return "「" + name + "」是 Windows 保留的名字";
            return "";
        }

        // 校验整张表：名字合法、路径不超长、同一个文件夹里不重名、不撞上磁盘上已有的文件。
        // exists：磁盘上有没有这个文件（测试里注入假的）。撞上的若正是这批里也要改走的文件，不算撞（两步改名）。
        // 返回有问题的行数
        public static int Validate(IList<RenameRow> rows, Func<string, bool> exists){
            var leaving = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var staying = new Dictionary<string, RenameRow>(StringComparer.OrdinalIgnoreCase);
            var targets = new Dictionary<string, RenameRow>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in rows) { if (r.Changed) leaving.Add(r.Path); else staying[r.Path] = r; }
            int problems = 0;
            foreach (var r in rows) {
                r.Problem = "";
                if (!r.Changed) continue;
                string p = r.Locked.Length > 0 ? r.Locked : CheckName(r.NewBase);
                if (p.Length == 0 && r.NewPath.Length > MaxPath) p = "改完路径太长（" + r.NewPath.Length + " 个字符，最多 " + MaxPath + "）";
                if (p.Length == 0) {
                    RenameRow other;
                    bool caseOnly = string.Equals(r.NewPath, r.Path, StringComparison.OrdinalIgnoreCase);
                    if (targets.TryGetValue(r.NewPath, out other)) {
                        p = "和「" + other.FileName + "」改成了同一个名字";
                        // 先出现的那一行也标出来：原来只有后一行标红，前一行看着没问题，用户不知道该改哪个
                        if (other.Problem.Length == 0) { other.Problem = "和「" + r.FileName + "」改成了同一个名字"; problems++; }
                    }
                    // A 改成 B，B 在表里却没改名
                    else if (!caseOnly && staying.TryGetValue(r.NewPath, out other)) p = "「" + other.FileName + "」还在用这个名字（它没改名）";
                    else if (!caseOnly && !leaving.Contains(r.NewPath) && exists(r.NewPath)) p = "文件夹里已经有「" + r.NewFileName + "」了";
                    targets[r.NewPath] = r;
                }
                if (p.Length > 0) { r.Problem = p; problems++; }
            }
            return problems;
        }

        // 改名步骤：新名字撞上这批里别的旧名字时两步走（先全改成临时名，再改成新名字），否则一步到位。
        // tempFor：给一个路径起不冲突的临时名（测试里注入）
        public static List<RenameMove> Moves(IList<RenameRow> rows, Func<string, string> tempFor){
            var from = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in rows) if (r.Changed) from.Add(r.Path);
            var list = new List<RenameMove>();
            foreach (var r in rows) {
                if (!r.Changed) continue;
                var m = new RenameMove { From = r.Path, To = r.NewPath };
                // 只改大小写也走临时名：同名不同大小写，有的文件系统 / 网络盘直接 Move 会报"已存在"
                bool caseOnly = string.Equals(r.NewPath, r.Path, StringComparison.OrdinalIgnoreCase);
                if (caseOnly || from.Contains(r.NewPath)) m.Temp = tempFor(r.Path);
                list.Add(m);
            }
            return list;
        }

        // ---------- 批量生成新名字 ----------

        // 查找替换（不区分大小写；find 为空 = 不动）
        public static string Replace(string name, string find, string replacement){
            if (string.IsNullOrEmpty(find)) return name;
            var sb = new StringBuilder();
            int at = 0;
            while (true) {
                int hit = name.IndexOf(find, at, StringComparison.OrdinalIgnoreCase);
                if (hit < 0) break;
                sb.Append(name, at, hit - at).Append(replacement ?? "");
                at = hit + find.Length;
            }
            sb.Append(name, at, name.Length - at);
            return sb.ToString();
        }

        // 按模板生成：{原名} = 原来的文件名主体，{序号} / {序号:3} = 第几个（补零到 2 / 3 位），
        // 其他 {xxx} = 文件的自定义属性 xxx（没有这个属性返回 null，让调用方提示）。
        // missing：模板里用到、但这个文件没有（或是空值）的属性名
        public static string Template(string template, string oldBase, int index, IDictionary<string, string> props, List<string> missing){
            var sb = new StringBuilder();
            int i = 0;
            while (i < template.Length) {
                char c = template[i];
                int close = c == '{' ? template.IndexOf('}', i + 1) : -1;
                if (close < 0) { sb.Append(c); i++; continue; }
                string key = template.Substring(i + 1, close - i - 1).Trim();
                i = close + 1;
                if (key == "原名") { sb.Append(oldBase); continue; }
                if (key == "序号" || key.StartsWith("序号:")) {
                    int width = 2, w;
                    if (key.Length > 3 && int.TryParse(key.Substring(3), out w) && w > 0 && w < 10) width = w;
                    sb.Append(index.ToString().PadLeft(width, '0'));
                    continue;
                }
                string v;
                if (props != null && props.TryGetValue(key, out v) && Clean(v).Length > 0) sb.Append(Clean(v));
                else if (missing != null && !missing.Contains(key)) missing.Add(key);
            }
            return sb.ToString();
        }

        // 模板里用到了哪些属性（不含 {原名} / {序号}）：用到了才去读文件属性
        public static List<string> TemplateFields(string template){
            var list = new List<string>();
            int i = 0;
            while (i < template.Length) {
                int open = template.IndexOf('{', i);
                if (open < 0) break;
                int close = template.IndexOf('}', open + 1);
                if (close < 0) break;
                string key = template.Substring(open + 1, close - open - 1).Trim();
                if (key.Length > 0 && key != "原名" && key != "序号" && !key.StartsWith("序号:") && !list.Contains(key)) list.Add(key);
                i = close + 1;
            }
            return list;
        }

        // ---------- 跟着改名的同名文件 ----------

        // 主行（零件 / 装配）在同一个文件夹里的同名工程图、装配的同名 .cfg。
        // folderFiles：文件夹里的全部文件名（不含路径）。已经在表里的文件不重复加
        public static List<string> Companions(RenameRow master, IEnumerable<string> folderFiles, ICollection<string> already){
            var list = new List<string>();
            string ext = master.Extension.ToLowerInvariant();
            if (ext != ".par" && ext != ".psm" && ext != ".asm") return list;
            foreach (var name in folderFiles) {
                string e = System.IO.Path.GetExtension(name).ToLowerInvariant();
                if (e != ".dft" && !(e == ".cfg" && ext == ".asm")) continue;
                if (!string.Equals(System.IO.Path.GetFileNameWithoutExtension(name), master.OldBase, StringComparison.OrdinalIgnoreCase)) continue;
                string full = System.IO.Path.Combine(master.Folder, name);
                if (already != null && already.Contains(full)) continue;
                list.Add(full);
            }
            return list;
        }
    }
}
