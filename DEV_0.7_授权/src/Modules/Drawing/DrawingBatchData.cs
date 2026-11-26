using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace TianGongCadSuite {
    // 一键出图的批量清单（纯逻辑）：装配里哪些零件要出图。
    // 普查：出了图的都是公司编号的加工件（SK26.01-OK310-02-004、YF25.07G01-L1-M30-02-015），
    // 外购件 / 标准件（MISUMI-、OMRON-、GB╱T 6177.1、B6901ZZ 轴承……）没有图。默认只勾加工件，用户可以自己改勾。
    public sealed class DrawingJob {
        public string File = "";
        public string Kind = "";        // 装配 / 零件 / 钣金
        public int Count;               // 装配里用了几个
        public bool Selected;
        public bool HasDrawing;         // 旁边已经有同名 .dft
        public string Reason = "";      // 没勾上的原因（外购件 / 标准件）
        public string Status = "";
        public DrawingOutcome Result;
        public string Name { get { return Path.GetFileNameWithoutExtension(File); } }
    }

    public static class DrawingBatch {
        // GB 后若紧跟「两位数字.两位数字」（如 GB26.01 项目编号）不视为国标标准件前缀
        static readonly Regex Vendor = new Regex(@"^(MISUMI|OMRON|SMC|CKD|FESTO|AIRTAC|THK|HIWIN|NSK|SKF|KEYENCE|SICK|IGUS|MV-|MVL-|PISCO|KOGANEI|NBK|GB╱T|GB/T|GB(?!\d{2}\.\d{2})|ISO|DIN|JIS|ANSI)", RegexOptions.IgnoreCase);
        static readonly Regex StdWords = new Regex("螺钉|螺栓|螺母|垫圈|垫片|弹垫|平垫|销钉|圆柱销|轴承|弹簧|卡簧|挡圈|铆钉|拉铆|铰链|把手|脚杯|万向轮|气缸|电机|传感器|接头|堵头|导轨|滑块|丝杆|联轴器|型材|Bearing|Screw|Bolt|Nut|Washer", RegexOptions.IgnoreCase);
        static readonly Regex Bearing = new Regex(@"^\d{3,5}(ZZ|2RS|RS)?$", RegexOptions.IgnoreCase);

        // 项目前缀：「SK26.01-OK310-02-01A」→「SK26.01」；没有就空
        public static string Project(string name){
            var m = Regex.Match(name ?? "", @"^([A-Za-z]{1,6}\d{2}\.\d{2}[A-Za-z0-9]*)");
            return m.Success ? m.Groups[1].Value : "";
        }

        // 这个文件默认要不要出图；不要时给原因
        public static bool Custom(string file, string topProject, out string reason){
            reason = "";
            string name = Path.GetFileNameWithoutExtension(file ?? "");
            string dir = Path.GetDirectoryName(file ?? "") ?? "";
            if (dir.IndexOf("Standard Parts", StringComparison.OrdinalIgnoreCase) >= 0 || dir.IndexOf("标准件", StringComparison.Ordinal) >= 0) { reason = "标准件库里的零件"; return false; }
            if (Vendor.IsMatch(name)) { reason = "外购件 / 标准件（看名字）"; return false; }
            if (Bearing.IsMatch(name) || Regex.IsMatch(name, @"^B?\d{4,5}ZZ", RegexOptions.IgnoreCase)) { reason = "轴承"; return false; }
            // 先查公司项目图号再匹配通用外购词：避免「SK26.01-OK310-02-015-气缸安装板 / 电机座 / 轴承座 / 传感器支架」等自制加工件被误杀
            string p = Project(name);
            if (p.Length > 0) {
                if (!string.IsNullOrEmpty(topProject) && !string.Equals(p, topProject, StringComparison.OrdinalIgnoreCase)) { reason = "别的项目的零件（" + p + "）"; return false; }
                return true;
            }
            if (StdWords.IsMatch(name)) { reason = "外购件 / 标准件（看名字）"; return false; }
            if (!string.IsNullOrEmpty(topProject)) { reason = "不是公司编号（多半是外购件）"; return false; }
            return true;
        }

        public static string KindOf(string file){
            string ext = (Path.GetExtension(file) ?? "").ToLowerInvariant();
            return ext == ".asm" ? "装配" : ext == ".psm" ? "钣金" : "零件";
        }

        // 查找模型旁边已有的工程图：依次匹配「名字.dft」「名字(自动).dft」「名字(自动N).dft」，保证状态列与双击打开一致
        public static string FindDrawing(string file){
            if (string.IsNullOrEmpty(file)) return "";
            try {
                string dir = Path.GetDirectoryName(file) ?? "";
                string stem = Path.GetFileNameWithoutExtension(file) ?? "";
                if (dir.Length == 0 || stem.Length == 0) return "";
                string direct = Path.Combine(dir, stem + ".dft");
                if (File.Exists(direct)) return direct;
                string auto = Path.Combine(dir, stem + "(自动).dft");
                if (File.Exists(auto)) return auto;
                for (int k = 2; k <= 20; k++) {
                    string alt = Path.Combine(dir, stem + "(自动" + k + ").dft");
                    if (File.Exists(alt)) return alt;
                }
            } catch { }
            return "";
        }

        // 已有的图：同名 .dft 或 (自动).dft 在模型旁边
        public static bool HasDrawing(string file){
            return FindDrawing(file).Length > 0;
        }

        // 按目录一次枚举 .dft 缓存检查（供载入装配 / 添加文件 / 刷新状态共用，避免加密盘上逐文件 File.Exists 慢 I/O）
        public static bool HasDrawingCached(string file, Dictionary<string, HashSet<string>> dirCache){
            if (string.IsNullOrEmpty(file)) return false;
            try {
                string dir = Path.GetDirectoryName(file) ?? "";
                string stem = Path.GetFileNameWithoutExtension(file) ?? "";
                if (dir.Length == 0 || stem.Length == 0) return false;
                HashSet<string> dfts;
                if (!dirCache.TryGetValue(dir, out dfts)) {
                    dfts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    try {
                        if (Directory.Exists(dir))
                            foreach (string f in Directory.GetFiles(dir, "*.dft")) dfts.Add(Path.GetFileName(f));
                    } catch { }
                    dirCache[dir] = dfts;
                }
                if (dfts.Contains(stem + ".dft") || dfts.Contains(stem + "(自动).dft")) return true;
                string autoPrefix = stem + "(自动";
                foreach (string name in dfts) {
                    if (name.Length > autoPrefix.Length + 5
                        && name.StartsWith(autoPrefix, StringComparison.OrdinalIgnoreCase)
                        && name.EndsWith(").dft", StringComparison.OrdinalIgnoreCase)) {
                        string num = name.Substring(autoPrefix.Length, name.Length - autoPrefix.Length - 5);
                        int k;
                        if (num.Length > 0 && int.TryParse(num, out k) && k >= 2) return true;
                    }
                }
            } catch { }
            return false;
        }
    }
}
