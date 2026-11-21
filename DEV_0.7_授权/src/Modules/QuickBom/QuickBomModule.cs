using System.Collections.Generic;

namespace TianGongCadSuite {
    // 快速出清单（0.12.0 新增，命令 14）：
    //   设计完成下图下单时，一键生成《加工件清单.xlsx》（可选同时生成 ERP《编码申请-加工件.xlsx》），
    //   并批量输出每个零件的 6 种标准格式（原三维 .par/.psm/.asm、工程图 .dft、.dwg、.jpg、.pdf、.step）。
    public sealed class QuickBomModule : IToolModule {
        public const int CommandId = 14;
        readonly ToolContext context;
        public QuickBomModule(ToolContext value) { context = value; }
        public string Id { get { return "quick-bom"; } }
        public IEnumerable<ToolCommand> Commands { get {
            yield return new ToolCommand(CommandId, "快速出清单", "一键生成加工件清单、编码申请表及 6 种模型图纸格式",
                "打开装配 / 工程图 / 零件后点本命令（或打开窗口后点「扫描模型文件夹…」）：\r\n"
                + "1. 自动筛出加工件与焊接件、配对同名工程图（.dft），带出属性卡并按公司规则自动预判八大类；\r\n"
                + "2. 支持在表格里直接改、Ctrl+V 从 Excel 粘贴、右侧 3D 预览核对零件；\r\n"
                + "3. 点「开始出清单」一键生成公司标准《加工件清单.xlsx》（保留 LOGO、八大类规则图与公式，可选嵌入三维缩略图）、"
                + "可选《编码申请-加工件.xlsx》，以及每个零件的 6 种格式（原三维 / .dft / .dwg / .jpg / .pdf / .step）。\r\n"
                + "原三维与 .dft 经独立进程复制，保持公司透明加密状态不变。",
                delegate { return true; },
                () => context.Show(() => new QuickBomForm(context.Application), f => ((QuickBomForm)f).LoadActive()));
        } }
        public void Dispose() { }
    }
}
