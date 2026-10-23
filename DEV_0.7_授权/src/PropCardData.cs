using System;
using System.Collections.Generic;

namespace TianGongCadSuite {
    // 属性卡填写（命令 10）的一行 = 一个文件。
    //
    // 只记两样：文件里原来是什么（Original，没有这个属性就不在字典里）、用户改成了什么（Edited）。
    // 写入时只动"改过的文件"：写改过的格子，再（可选）把这个文件缺的属性补成空值 ——
    // 工程图标题栏引用了零件里没有的属性就显示「错误：没有参考」，补个空值就变成空白。
    // 一个格子都没改的文件绝不写（装配里的标准件、外购件不会被碰）。
    // 纯逻辑，不依赖 CAD / WinForms（tests/pure 里测）。
    public sealed class PropCardRow {
        public string Path = "";
        public string Material = "";   // 只读显示（材料走材料库，不在这里改）
        public string Problem = "";    // 读的时候出的问题，显示在材料列旁边
        public readonly Dictionary<string, string> Original = new Dictionary<string, string>();
        public readonly Dictionary<string, string> Edited = new Dictionary<string, string>();

        public string FileName { get { return System.IO.Path.GetFileName(Path); } }
        public bool Has(string field){ return Original.ContainsKey(field); }
        public bool IsEdited(string field){ return Edited.ContainsKey(field); }
        public bool Dirty { get { return Edited.Count > 0; } }

        public string Value(string field){
            string v;
            if (Edited.TryGetValue(field, out v)) return v;
            return Original.TryGetValue(field, out v) ? v : "";
        }

        // 改回原值就不算改；文件里没有、又填成空的，也不算改（要补空值由"补齐"负责）
        public void Set(string field, string value){
            value = Clean(value);
            string old;
            bool had = Original.TryGetValue(field, out old);
            if ((had && old == value) || (!had && value.Length == 0)) Edited.Remove(field);
            else Edited[field] = value;
        }

        // 这个文件要写的内容：改过的格子 + （fillMissing）缺的属性补空值。没改过的文件返回空表。
        public List<KeyValuePair<string, string>> Writes(IList<string> fields, bool fillMissing){
            var list = new List<KeyValuePair<string, string>>();
            if (!Dirty) return list;
            foreach (var f in fields) {
                string v;
                if (Edited.TryGetValue(f, out v)) list.Add(new KeyValuePair<string, string>(f, v));
                else if (fillMissing && !Has(f)) list.Add(new KeyValuePair<string, string>(f, ""));
            }
            // 列被删掉以后，之前在那一列改过的值照样写（用户改过就该落盘）
            foreach (var pair in Edited) if (!fields.Contains(pair.Key)) list.Add(pair);
            return list;
        }

        // 写成功以后：改过的值变成"原值"
        public void Committed(IEnumerable<KeyValuePair<string, string>> written){
            foreach (var pair in written) { Original[pair.Key] = pair.Value; Edited.Remove(pair.Key); }
        }

        // 粘贴进来的值去掉首尾空白和换行（Excel 复制一格会带回车）
        public static string Clean(string value){ return (value ?? "").Trim(); }
    }

    // 列（要填的属性）和剪贴板。
    public static class PropCardFields {
        public const string FileName = "propcard-fields.tsv";

        // 默认列 = SK 模板标题栏引用的自定义属性（见 C:\Program Files\NDS\TianGong 2025\Template\SK先惠）。
        // 「表面处理」就是标题栏的「材料处理」；「优先等级」和「加工优先等级」两套模板各用一个，都给出来。
        public static readonly string[] Default = {
            "零件名称", "设计", "项目名称", "表面处理", "优先等级", "加工优先等级",
            "批次", "版本号1", "数量", "图号", "子装配体图号", "总装配图号"
        };

        // 用户在「列」里输入的一串名字：逗号、顿号、分号、空白都能分隔，去重、去空
        public static List<string> Parse(string text){
            var list = new List<string>();
            foreach (var raw in (text ?? "").Split(new[] { ',', '，', '、', ';', '；', '\t', '\r', '\n', ' ', '　' }, StringSplitOptions.RemoveEmptyEntries)) {
                string name = raw.Trim();
                if (name.Length > 0 && !list.Contains(name)) list.Add(name);
            }
            return list;
        }

        public static string Join(IEnumerable<string> fields){ return string.Join("、", new List<string>(fields).ToArray()); }

        public static List<string> Load(string path){
            var list = new List<string>();
            foreach (var row in SettingsFile.Read(path)) if (row.Length > 0 && row[0].Trim().Length > 0 && !list.Contains(row[0].Trim())) list.Add(row[0].Trim());
            return list.Count > 0 ? list : new List<string>(Default);
        }

        public static void Save(string path, IEnumerable<string> fields){
            var rows = new List<string[]>();
            foreach (var f in fields) rows.Add(new[] { f });
            SettingsFile.Write(path, rows, "属性卡填写的列：一行一个自定义属性名，按这个顺序显示。删掉本文件恢复默认。");
        }

        // 剪贴板文本 → 行 × 列（Excel / 本表格复制出来都是制表符分隔、回车分行；末尾那个空行不算）
        public static List<string[]> ParseClipboard(string text){
            var rows = new List<string[]>();
            if (string.IsNullOrEmpty(text)) return rows;
            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            int count = lines.Length;
            if (count > 0 && lines[count - 1].Length == 0) count--;
            for (int i = 0; i < count; i++) rows.Add(lines[i].Split('\t'));
            return rows;
        }
    }
}
