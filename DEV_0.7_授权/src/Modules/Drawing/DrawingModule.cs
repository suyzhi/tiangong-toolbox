using System.Collections.Generic;
using A=SolidEdgeAssembly;
using P=SolidEdgePart;

namespace TianGongCadSuite {
    // 一键出图（0.11.0 新增，命令 13）：零件 / 钣金 / 装配 → 按公司模板出一张可加工的工程图。
    // 零件：主视图 + 投影 + 轴测图，坐标尺寸 + 孔标注 + 圆角 + 刻字；装配：外形尺寸 + 轴测图序号 + 明细表；
    // 装配上点还会列出它用到的加工件，一次全出。规矩来自 734 张公司图纸的普查（见 DRAWING.md）。
    public sealed class DrawingModule : IToolModule {
        public const int CommandId = 13;
        readonly ToolContext context;
        public DrawingModule(ToolContext value){ context = value; }
        public string Id { get { return "drawing"; } }
        public IEnumerable<ToolCommand> Commands { get {
            yield return new ToolCommand(CommandId, "一键出图", "按公司模板自动出零件图 / 装配图",
                "打开零件、钣金件或装配（或直接扫描模型文件夹 / 拖入文件）→ 点「出图」。\r\n"
                + "零件：自动选主视图和投影、排版、选 A4/A3 和比例，标坐标尺寸、孔（数量 / 贯通 / 深度 / 螺纹 / 沉孔）、圆角、刻字，标题栏从零件属性带出。\r\n"
                + "装配：出装配图（外形尺寸、零件序号、明细表），并列出装配里的加工件，勾上的一起出图。\r\n"
                + "支持右侧 3D 预览、一键只选缺图、右键单件重跑与打开模型/工程图。",
                delegate { return true; },
                () => context.Show(
                    () => new DrawingForm(context.Application),
                    f => ((DrawingForm)f).LoadActive(),
                    f => f is DrawingForm && ((DrawingForm)f).ReuseIfOpen()));
        } }
        public void Dispose(){}
    }
}
