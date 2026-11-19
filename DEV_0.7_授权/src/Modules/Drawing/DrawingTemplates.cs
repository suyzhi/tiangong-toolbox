using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using D=SolidEdgeDraft;
using F=SolidEdgeFramework;
using S=SolidEdgeFrameworkSupport;

namespace TianGongCadSuite {
    public sealed class DrawingOptions {
        public string TemplateA3, TemplateA4;   // 空 = 自动找（DrawingTemplates）
        public string OutputDir;                // 空 = 放在模型旁边
        public bool Overwrite;                  // 已有同名工程图时：false = 另存为「名字(自动).dft」
        public bool Save = true;
        public string PdfDir;                   // 非空：顺便导出一份 PDF（测试 / 交付用）
        public bool Close;                      // 批量出图：存完就关（单张出图留着给用户看）
    }

    public sealed class DrawingOutcome {
        public string Model, Output, Sheet = "", Scale = "";
        public bool Ok;
        public int Views, Dimensions, Failed, Holes, HoleCallouts, Radii;
        public int Required, Covered;            // 完成度：要标的落点 / 标上的（见 DrawingCoverage）
        public readonly List<string> Notes = new List<string>();
        public double Completeness { get { return Required == 0 ? 1 : (double)Covered / Required; } }
    }

    // 找公司模板：天工安装目录 Template\SK先惠\A3-SK图纸.dft / A4-SK图纸.dft；没有就用天工自带 GB 模板
    public static class DrawingTemplates {
        public static string Root(){
            foreach (var p in new[] { @"C:\Program Files\NDS\TianGong 2025\Template", @"D:\Program Files\NDS\TianGong 2025\Template" }) if (Directory.Exists(p)) return p;
            return "";
        }
        public static string Find(string size){
            string root = Root();
            if (root.Length == 0) return "";
            string sk = Path.Combine(root, "SK先惠", size + "-SK图纸.dft");
            if (File.Exists(sk)) return sk;
            string gb = Path.Combine(root, "GB Metric", "GB 公制工程图.dft");
            return File.Exists(gb) ? gb : "";
        }
    }

    // 一键出图：零件 → 一张可加工的工程图。步骤见 Part() 里的注释。
}
