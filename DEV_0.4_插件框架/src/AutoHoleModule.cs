using System;
using System.Collections.Generic;
using A=SolidEdgeAssembly;
using F=SolidEdgeFramework;

namespace TianGongCadSuite {
    // 命令 6：自动打孔。零门槛交互 —— 点孔、点面、点打孔。
    public sealed class AutoHoleModule : IToolModule {
        readonly ToolContext context;
        public AutoHoleModule(ToolContext value){ context = value; }
        public string Id { get { return "auto-hole"; } }
        public IEnumerable<ToolCommand> Commands {
            get {
                yield return new ToolCommand(6, "自动打孔",
                    "照着已有的孔（可多选、大小可不同），在其他零件上配做",
                    "① 点已有的孔（孔的圆形边线，可以连点好几个，大小可以不一样）→ ② 点要打孔的面（可多选）→ ③ 点「开始打孔」。\n" +
                    "点圆边就是加参考孔，点平面就是加打孔面，不需要切模式。\n" +
                    "孔型和规格自动从每个参考孔反推：螺纹底孔配沉孔，过孔配螺纹孔；投影落在面外的自动跳过。\n" +
                    "螺纹孔按螺纹内小径建模、以装饰螺纹显示；盲孔默认平底，要钻尖就选 V 型底。",
                    context.HasAssembly,
                    () => { var doc = context.RequireAssembly(); context.Show(() => new AutoHoleForm(context.Application, doc), f => ((AutoHoleForm)f).StartPicking()); });
                yield return new ToolCommand(7, "批量排孔",
                    "在一个面上沿线等分/定距排孔，或沿圆周均布",
                    "① 点要打孔的面（方向自动取该面最长的边）→ ② 选排布方式并填孔数或间距 → ③ 点「开始打孔」。\n" +
                    "圆周均布时第 ② 步改成点一条圆形边线来定位圆心和半径。\n" +
                    "勾「腰孔」可排长圆孔，总长和槽宽分开填；窗口下半部分会画出排孔示意图。",
                    context.HasAssembly,
                    () => { var doc = context.RequireAssembly(); context.Show(() => new AutoHolePatternForm(context.Application, doc), f => ((AutoHolePatternForm)f).StartPicking()); });
                yield return new ToolCommand(8, "配孔检查",
                    "扫一遍装配，查漏打孔、孔偏了、配错孔",
                    "打开装配，点「开始检查」。结果列表里点一条可以高亮对应的孔。\n" +
                    "判定口径：孔径 3–25mm 视为连接孔；同轴容差 0.2mm；轴线方向容差 2°；\n" +
                    "配做规则——参考孔是螺纹底孔就配沉孔，是过孔就配螺纹孔。",
                    context.HasAssembly,
                    () => { var doc = context.RequireAssembly(); context.Show(() => new AutoHoleCheckForm(context.Application, doc), null); });
            }
        }
        public void Dispose(){}
    }
}
