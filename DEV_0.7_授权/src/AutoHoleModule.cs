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
                    "点孔口圆边 / 点孔内壁 / 点一个带孔的面（自动认孔），在其他零件上配做",
                    "① 点已有的孔 → ② 点要打孔的面（可多选）→ ③ 点「开始打孔」。\n" +
                    "点孔的三种方式：① 点孔口的圆形边线；② 点孔的内壁圆柱面（圆锥面也行）；\n" +
                    "③ 直接点一个充满孔的面 —— 面上的孔会自动全部认出来，同一个孔径的归成一类，不用一个一个点。\n" +
                    "沉孔/锥沉/倒角孔口会自动取孔口**下面**那个孔径来配做（否则 Φ11 的沉孔会被当成 M10 过孔，配错）。\n" +
                    "孔型和规格自动从每个参考孔反推：螺纹底孔配沉孔，过孔配螺纹孔；投影落在面外的自动跳过。\n" +
                    "螺纹孔按螺纹内小径建模、以装饰螺纹显示，螺纹默认全长，也可指定深度；盲孔默认平底，要钻尖就选 V 型底。\n" +
                    "同一张面上同规格的孔合成一个孔特征（和天工「孔」命令点多个孔一样），按规格命名（如「M6 螺纹孔（螺纹全长） ×4」）。\n" +
                    "打出来的孔会记住参考孔：参考零件挪动后，窗口底部「同步孔位」一键把孔对准。",
                    context.HasEditableContext,
                    () => { var doc = context.RequireEditableAssembly(); context.Show(() => new AutoHoleForm(context.Application, doc), f => ((AutoHoleForm)f).StartPicking()); });
                yield return new ToolCommand(7, "批量排孔",
                    "在一个面上沿线等分/定距排孔，或沿圆周均布",
                    "① 点要打孔的面（方向自动取该面最长的边）→ ② 选排布方式并填孔数或间距 → ③ 点「开始打孔」。\n" +
                    "圆周均布时第 ② 步改成点一条圆形边线来定位圆心和半径。\n" +
                    "勾「腰孔」可排长圆孔，总长和槽宽分开填；窗口下半部分会画出排孔示意图。",
                    context.HasEditableContext,
                    () => { var doc = context.RequireEditableAssembly(); context.Show(() => new AutoHolePatternForm(context.Application, doc), f => ((AutoHolePatternForm)f).StartPicking()); });
                yield return new ToolCommand(9, "改孔型",
                    "把已经打好的孔换成别的孔型/规格（整组一起改），不用删了重打",
                    "① 在模型里点要改的孔（孔口圆边或孔内壁，可以连续点几个）→ ② 选新的孔型和规格 → ③ 点「应用」。\n" +
                    "默认整组一起改：自动打孔、批量排孔打的同规格孔是一个特征组，组名和孔名按新规格重起。\n" +
                    "点到批量排孔阵列里的孔 = 改它的种子孔，整排跟着变。\n" +
                    "盲孔可以改深度或改成贯通；贯通孔不能直接改成盲孔（天工接口限制）。改错了点「撤销修改」。",
                    context.HasEditableContext,
                    () => { var doc = context.RequireEditableAssembly(); context.Show(() => new HoleEditForm(context.Application, doc), f => ((HoleEditForm)f).StartPicking()); });
                yield return new ToolCommand(8, "配孔检查",
                    "扫一遍装配，查漏打孔、孔偏了、配错孔",
                    "打开装配，点「开始检查」。结果列表里点一条会高亮并跳到对应的孔（双击或「跳转到孔」也行）。\n" +
                    "漏打孔可以点「去配孔」：涉及的孔直接带进「自动打孔」当参考孔。子装配里的零件也一起检查。\n" +
                    "判定口径（默认孔径 3–25mm、同轴容差 0.2mm、方向容差 2°）点窗口上的「判定口径」可以改。",
                    context.HasAssembly,
                    () => {
                        var doc = context.RequireAssembly();
                        context.Show(() => new AutoHoleCheckForm(context.Application, doc) { GoDrill = (holes, part) => OpenDrillFromCheck(doc, holes, part) }, null);
                    });
            }
        }
        // 配孔检查里的「去配孔」：另开一个自动打孔窗口（检查窗口留着，还能接着看下一条），
        // 把漏打孔那一条的孔带进去当参考孔，用户只要再点没打孔那个零件上的面。
        void OpenDrillFromCheck(A.AssemblyDocument doc, List<HoleRecord> holes, string missingPart){
            if (!TianGongCadSuite.Licensing.LicenseGate.EnsureInteractive(new CadOwner(context.Application.ActiveFramehWnd))) return;
            var f = new AutoHoleForm(context.Application, doc);
            f.Show(new CadOwner(context.Application.ActiveFramehWnd));
            f.StartPicking();
            f.PreloadReferences(holes, missingPart);
        }
        // 「自动打孔 / 批量排孔」的「改孔型…」：另开改孔型窗口，把刚打的那批孔带进去（打孔窗口留着）
        internal static void OpenEdit(F.Application app, A.AssemblyDocument doc, IList<KeyValuePair<SolidEdgePart.PartDocument, string>> created){
            var f = new HoleEditForm(app, doc);
            f.Show(new CadOwner(app.ActiveFramehWnd));
            f.StartPicking();
            f.Preload(created);
        }
        public void Dispose(){}
    }
}
