using System.Collections.Generic;

namespace TianGongCadSuite {
    // 出图清单工作台（0.13.0，命令 14）：
    //   整合「批量改名」「属性卡填写」「快速出清单」三大功能于同一张大表，
    //   一表完成零件改名（联动更新装配/工程图引用）、属性卡批量写入、加工件清单 + 编码申请表 + 每件 6 种格式导出。
    public sealed class QuickBomModule : IToolModule {
        public const int CommandId = 14;
        readonly ToolContext context;
        public QuickBomModule(ToolContext value) { context = value; }
        public string Id { get { return "quick-bom"; } }
        public IEnumerable<ToolCommand> Commands {
            get {
                yield return new ToolCommand(CommandId, "出图清单工作台", "属性卡填写 · 批量改名 · 快速出清单三合一工作台",
                    "将「批量改名」「属性卡填写」「快速出清单」整合在同一张大表里（共用文件清单与右侧 3D 预览）：\r\n"
                    + "1. 支持载入当前装配/工程图/零件，或递归扫描模型文件夹（含子文件夹）/ 添加文件；\r\n"
                    + "2. 同一张表同时编辑新文件名、全部自定义属性卡（支持自定义属性列）、材料、数量、八大类与编码，支持「图号 ↔ 文件名」一键互填及 Excel Ctrl+V 粘贴；\r\n"
                    + "3. 底部可分步点「执行改名」「写入属性」「开始出清单」，也可点「开始出清单」一键串联执行；\r\n"
                    + "4. 输出公司标准《加工件清单.xlsx》、可选《编码申请-加工件.xlsx》及每件 6 种格式（原三维 / .dft / .dwg / .jpg / .pdf / .step），原三维与 .dft 经独立进程复制保持公司透明加密不变。",
                    delegate { return true; },
                    () => context.Show(
                        () => new QuickBomForm(context.Application),
                        f => ((QuickBomForm)f).LoadActive(),
                        f => f is QuickBomForm && ((QuickBomForm)f).ReuseIfOpen()));
            }
        }
        public void Dispose() { }
    }
}
