using System.Collections.Generic;

namespace TianGongCadSuite {
    // 属性卡填写（0.8.0 新增，命令 10）：批量填零件 / 装配的自定义属性（工程图标题栏引用的那些）。
    // 天工自带的批量写属性不好用：一次只能填一种值、看不到哪些文件缺属性。
    public sealed class PropCardModule : IToolModule {
        readonly ToolContext context;
        public PropCardModule(ToolContext value){ context = value; }
        public string Id { get { return "prop-card"; } }
        public IEnumerable<ToolCommand> Commands { get {
            yield return new ToolCommand(10, "属性卡填写", "像 Excel 一样批量填零件属性卡",
                "打开装配（或工程图、零件）→ 点本命令，当前文档涉及的所有文件列成一张表：一行一个文件，一列一个属性。\r\n"
                + "选一片格子 → 上面输入框填值 →「填入」；也可以从 Excel 整块复制后 Ctrl+V。\r\n"
                + "黄色 = 改过还没写入，灰色 = 文件里还没有这个属性。点「写入」只写改过的文件，"
                + "勾上「顺便补齐缺的属性」时会把缺的列补成空值，工程图标题栏就不再显示“错误：没有参考”。\r\n"
                + "要填哪些属性点表格右上角「列…」。",
                delegate { return true; },
                () => context.Show(() => new PropCardForm(context.Application), f => ((PropCardForm)f).LoadActive()));
        } }
        public void Dispose(){}
    }
}
