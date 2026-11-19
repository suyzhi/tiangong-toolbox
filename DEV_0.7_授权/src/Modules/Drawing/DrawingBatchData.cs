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
        static readonly Regex Vendor = new Regex(@"^(MISUMI|OMRON|SMC|CKD|FESTO|AIRTAC|THK|HIWIN|NSK|SKF|KEYENCE|SICK|IGUS|MV-|MVL-|PISCO|KOGANEI|NBK|GB|GB╱T|GB/T|ISO|DIN|JIS|ANSI)", RegexOptions.IgnoreCase);
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
            if (StdWords.IsMatch(name)) { reason = "外购件 / 标准件（看名字）"; return false; }
            string p = Project(name);
            if (topProject.Length > 0 && p.Length > 0 && !string.Equals(p, topProject, StringComparison.OrdinalIgnoreCase)) { reason = "别的项目的零件（" + p + "）"; return false; }
            if (topProject.Length > 0 && p.Length == 0) { reason = "不是公司编号（多半是外购件）"; return false; }
            return true;
        }

        public static string KindOf(string file){
            string ext = (Path.GetExtension(file) ?? "").ToLowerInvariant();
            return ext == ".asm" ? "装配" : ext == ".psm" ? "钣金" : "零件";
        }

        // 已有的图：同名 .dft 在模型旁边
        public static bool HasDrawing(string file){
            try { return File.Exists(Path.ChangeExtension(file, ".dft")); } catch { return false; }
        }
    }
}
