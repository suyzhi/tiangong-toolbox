using System.Collections.Generic;

namespace TianGongCadSuite {
    // 批量改名（0.10.0 新增，命令 12）：装配里的零件 / 子装配 / 工程图一次改名，引用它们的装配、工程图跟着改好，不断开。
    // 天工自带的重命名要逐个查"谁用到了"、一卡就是很久；这里只读写文件里的链接表（设计管理器接口），不加载几何。
    public sealed class BatchRenameModule : IToolModule {
        public const int CommandId = 12;
        readonly ToolContext context;
        public BatchRenameModule(ToolContext value){ context = value; }
        public string Id { get { return "batch-rename"; } }
        public IEnumerable<ToolCommand> Commands { get {
            yield return new ToolCommand(CommandId, "批量改名", "一次改一批文件名，装配和工程图的引用跟着改",
                "打开装配（或工程图、零件）→ 点本命令，涉及的文件列成一张表。\r\n"
                + "在「新文件名」一列直接改，或者用上面的查找替换、前后加字、按模板（比如 {图号}-{零件名称}）一次生成。\r\n"
                + "同文件夹里同名的工程图会跟着改名。点「改名」：天工里开着的这些文件先关掉，改完名、改好装配和工程图里的引用，再自动打开。",
                delegate { return true; },
                () => context.Show(() => new BatchRenameForm(context.Application), f => ((BatchRenameForm)f).LoadActive()));
        } }
        public void Dispose(){}
    }
}
