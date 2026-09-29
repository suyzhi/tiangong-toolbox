using System.Windows.Forms;

namespace TianGongCadSuite {
    // 要在模型上点选的工具窗口实现它：ToolContext.Show 据此给窗口挂上"停靠 / 收起"条（见 ToolWindow）。
    // 没实现的窗口（格式转换、Lineup 等）行为不变。
    public interface IPickingWindow {
        // 收起后条上显示的就是这行字，跟着它实时变（颜色也跟）。
        Label StatusLabel { get; }
        // 第一次打开时「点模型时自动收起」是否勾上；之后以用户上次的选择为准。
        bool AutoCollapseByDefault { get; }
        // 收起状态下条上额外放的一个按钮（null = 不放）。点了先执行它，再展开窗口。
        string QuickActionText { get; }
        void QuickAction();
    }
}
