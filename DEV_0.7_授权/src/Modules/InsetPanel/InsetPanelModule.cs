using System.Collections.Generic;

namespace TianGongCadSuite {
    // 内嵌板 / 型材自动填充模块（命令 1、2）。
    public sealed class InsetPanelModule : IToolModule {
        readonly ToolContext context;
        public InsetPanelModule(ToolContext value){context=value;}
        public string Id {get{return "inset-panel";}}
        public IEnumerable<ToolCommand> Commands {get{
            yield return new ToolCommand(1,"四面生成内嵌板","四面和定位点生成独立板子","选四个内侧平面 → 定位点 → 厚度和间隙 → 保存新 PAR。",context.HasAssembly,()=>{var doc=context.RequireAssembly();context.Show(()=>new PanelForm(context.Application,doc),f=>((PanelForm)f).StartPicking());});
            yield return new ToolCommand(2,"型材自动填充","识别型材闭合框口并生成板子","在装配中多选型材，再设置板厚及间隙，检查框口并生成。",context.HasAssembly,()=>{var doc=context.RequireAssembly();context.Show(()=>new AutoPanelForm(context.Application,doc),null,f=>{var p=f as AutoPanelForm;if(p==null||!ReferenceEquals(p.Assembly,doc))return false;p.NoteReopened();return true;});});
        }}
        public void Dispose(){}
    }
}
