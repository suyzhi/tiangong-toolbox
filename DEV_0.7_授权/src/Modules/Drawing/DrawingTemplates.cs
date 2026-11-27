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
        public string DimensionStyle;           // 空 = 默认按公司标准择优（A3-国标-水平 / A3-国标）
        public string OutputDir;                // 空 = 放在模型旁边
        public string OutputName;               // 空 = 与模型同名（批量遇同目录同名 .asm/.par 冲突时由调用方区分）
        public bool Overwrite;                  // 已有同名工程图时：false = 另存为「名字(自动).dft」
        public bool Save = true;
        public string PdfDir;                   // 非空：顺便导出一份 PDF（测试 / 交付用）
        public bool Close;                      // 批量出图：存完就关（单张出图留着给用户看）

        // Wave 2 / Wave 3 智能出图规则开关（默认开启，DrawingForm 底部快捷开关可随时切换并持久化）
        public bool AutoBreakView = true;       // 细长件自动断裂视图
        public bool AutoHoleTable = true;       // 多孔大板（≥16 孔）自动切换规范孔表
        public bool AutoDetailView = true;      // 大件/框架局部密集区自动拉详图放大
        public bool AutoMultiSheet = true;      // 底面/背面/右端面盲孔与斜孔溢出自动建第 2 页

        // Wave 3 单零件智能公差子开关（可在「公差规则…」小弹窗里逐项开关）
        public bool TolPinHoles = true;         // 定位销孔 H7 + ⌖Ø0.03
        public bool TolBearings = true;         // 轴承孔 H7 / 轴承轴颈 k6 + 同轴度 + Ra1.6
        public bool TolGuideRails = true;       // 导轨安装面基准 A + 平面度 + 靠山垂直度 + Ra3.2
        public bool TolDatumsAndGdt = true;     // 基准 A/B 与关联平行度 / 垂直度 / 同轴度
        public bool TolRoughness = true;        // 配合面粗糙度 Ra3.2 / Ra1.6
    }

    public sealed class DrawingOutcome {
        public string Model, Output, Sheet = "", Scale = "", Style = "";
        public bool Ok;
        public int Views, Dimensions, Failed, Holes, HoleCallouts, Radii;
        public int Required, Covered;            // 完成度：要标的落点 / 标上的（见 DrawingCoverage）
        public readonly List<string> Notes = new List<string>();
        public double Completeness { get { return Required == 0 ? 1 : (double)Covered / Required; } }
    }

    // 找公司模板：优先读天工已配置的模板目录，再查安装目录 Template\SK先惠\A3-SK图纸.dft / A4-SK图纸.dft；没有才回退天工自带 GB 模板
    public static class DrawingTemplates {
        public static string Root(){
            try {
                string inst = Path.Combine(CadBuilder.CadInstallRoot, "Template");
                if (Directory.Exists(inst)) return inst;
            } catch { }
            foreach (var p in new[] { @"C:\Program Files\NDS\TianGong 2025\Template", @"D:\Program Files\NDS\TianGong 2025\Template" }) if (Directory.Exists(p)) return p;
            return "";
        }
        public static string Find(string size){
            return Find(null, size);
        }
        public static string Find(F.Application app, string size){
            string company = FindCompany(app, size);
            if (company.Length > 0) return company;
            return FindFallback(app);
        }
        // 只查公司模板（A3-SK图纸.dft / A4-SK图纸.dft），避免 A4 缺失时误回退到非公司 GB 模板而放弃已存在的公司 A3 模板
        public static string FindCompany(F.Application app, string size){
            string fileName = size + "-SK图纸.dft";
            foreach (var dir in CandidateDirs(app)) {
                string direct = Path.Combine(dir, fileName);
                if (File.Exists(direct)) return direct;
                string sk = Path.Combine(dir, "SK先惠", fileName);
                if (File.Exists(sk)) return sk;
            }
            return "";
        }
        // 仅在公司 A3/A4 模板均缺失时回退天工自带 GB 模板
        public static string FindFallback(F.Application app){
            foreach (var dir in CandidateDirs(app)) {
                string gb = Path.Combine(dir, "GB Metric", "GB 公制工程图.dft");
                if (File.Exists(gb)) return gb;
                try {
                    string parent = Path.GetDirectoryName(dir);
                    if (!string.IsNullOrEmpty(parent)) {
                        string pgb = Path.Combine(parent, "GB Metric", "GB 公制工程图.dft");
                        if (File.Exists(pgb)) return pgb;
                    }
                } catch { }
            }
            return "";
        }

        static List<string> CandidateDirs(F.Application app){
            var dirs = new List<string>();
            Action<string> add = p => {
                string clean = DrawingStyleRules.ParseTemplateFolder(p);
                if (clean.Length > 0 && Directory.Exists(clean) && !dirs.Any(x => string.Equals(x, clean, StringComparison.OrdinalIgnoreCase)))
                    dirs.Add(clean);
            };
            if (app != null) {
                foreach (var c in new[] {
                    F.ApplicationGlobalConstants.seApplicationGlobalTemplatePathFolder,
                    F.ApplicationGlobalConstants.seApplicationGlobalTemplateRootFolder
                }) {
                    try {
                        object raw = null;
                        app.GetGlobalParameter(c, ref raw);
                        if (raw != null) add(raw.ToString());
                    } catch { }
                }
            }
            try { add(Path.Combine(CadBuilder.CadInstallRoot, "Template")); } catch { }
            foreach (var p in new[] { @"C:\Program Files\NDS\TianGong 2025\Template", @"D:\Program Files\NDS\TianGong 2025\Template" })
                add(p);
            return dirs;
        }
    }

    // 一键出图：零件 → 一张可加工的工程图。步骤见 Part() 里的注释。
}
