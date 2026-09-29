using System;
using System.Collections.Generic;
using System.Windows.Forms;
using F=SolidEdgeFramework;
using A=SolidEdgeAssembly;
using P=SolidEdgePart;
namespace TianGongCadSuite {
    // Stable IDs must never be reused, even after removing a command.
    public sealed class ToolCommand {
        public readonly int Id;
        public readonly string Title, Description, Help;
        public readonly Func<bool> CanExecute;
        public readonly Action Execute;
        public ToolCommand(int id,string title,string description,string help,Func<bool> canExecute,Action execute){
            if(id<=0 || string.IsNullOrWhiteSpace(title) || canExecute==null || execute==null)throw new ArgumentException("Invalid command.");
            Id=id;Title=title;Description=description;Help=help;CanExecute=canExecute;Execute=execute;
        }
    }
    public interface IToolModule : IDisposable {
        string Id {get;}
        IEnumerable<ToolCommand> Commands {get;}
    }
    public sealed class ToolRegistry : IDisposable {
        readonly List<IToolModule> modules=new List<IToolModule>();
        readonly List<ToolCommand> commands=new List<ToolCommand>();
        readonly Dictionary<int,ToolCommand> runtime=new Dictionary<int,ToolCommand>();
        readonly HashSet<int> retired=new HashSet<int>();
        public IList<ToolCommand> Commands {get{return commands.AsReadOnly();}}
        // 下线某个命令：不再往功能区注册（实现仍编译在 DLL 里，把这个 Retire 调用删掉就能恢复）。
        // 注意：ToolCommand 的 ID 用过就永久保留，不要再分配给别的命令。
        public void Retire(int id){retired.Add(id);}
        public void Add(IToolModule module){
            if(module==null)throw new ArgumentNullException("module");
            foreach(var m in modules)if(m.Id==module.Id)throw new ArgumentException("Duplicate module: "+module.Id);
            var pending=new List<ToolCommand>();foreach(var c in module.Commands)if(c!=null&&!retired.Contains(c.Id))pending.Add(c);
            var ids=new HashSet<int>();
            foreach(var c in commands)ids.Add(c.Id);
            foreach(var c in pending)if(c==null || !ids.Add(c.Id))throw new ArgumentException("Duplicate or null command.");
            modules.Add(module);commands.AddRange(pending);
        }
        public void Bind(Array ids){
            if(ids.Length!=commands.Count)throw new ArgumentException("Command count mismatch.");
            var mapped=new Dictionary<int,ToolCommand>();
            for(int i=0;i<commands.Count;i++)mapped.Add(Convert.ToInt32(ids.GetValue(i)),commands[i]);
            runtime.Clear();foreach(var pair in mapped)runtime.Add(pair.Key,pair.Value);
        }
        // CAD event callbacks carry the plugin's original ID, not SetAddInInfoEx2's output ID.
        public ToolCommand Find(int id){foreach(var c in commands)if(c.Id==id)return c;return null;}
        public int NativeId(int id){foreach(var pair in runtime)if(pair.Value.Id==id)return pair.Key;throw new ArgumentException("Command not registered: "+id);}
        public void Dispose(){foreach(var m in modules)try{m.Dispose();}catch(Exception e){Log.Write("Dispose module "+m.Id,e);}runtime.Clear();commands.Clear();modules.Clear();}
    }
    public sealed class ToolContext : IDisposable {
        public readonly F.Application Application;
        Form active;
        public ToolContext(F.Application application){Application=application;}
        public bool HasAssembly(){try{return Application.ActiveDocument is A.AssemblyDocument;}catch{return false;}}
        public A.AssemblyDocument RequireAssembly(){var document=Application.ActiveDocument as A.AssemblyDocument;if(document==null)throw new InvalidOperationException("请先打开装配文件。");return document;}
        // 原位编辑（在装配里双击零件）时，活动文档是**零件**而不是装配。
        // 而天工CAD 只允许往"正在编辑的那个零件文档"里写模型，所以这个上下文恰恰是打孔唯一能成功的地方
        // —— 插件必须在这里也能用。做法：遍历打开的装配，谁的实例零件就是当前活动零件，就返回谁。
        public A.AssemblyDocument EditingAssembly(){
            try{
                var part=Application.ActiveDocument as P.PartDocument;
                if(part==null){Log.Write("ContextProbe","活动文档不是零件文档");return null;}
                string partName=null;try{partName=part.FullName;}catch(Exception e){Log.Write("ContextProbe","读活动零件 FullName 失败:"+e.Message);}
                if(string.IsNullOrEmpty(partName)){Log.Write("ContextProbe","活动零件 FullName 为空");return null;}
                int documents=Application.Documents.Count;
                for(int i=1;i<=documents;i++){
                    var assembly=Application.Documents.Item(i) as A.AssemblyDocument;
                    if(assembly==null)continue;
                    try{
                        foreach(A.Occurrence occurrence in assembly.Occurrences){
                            var occurrencePart=occurrence.OccurrenceDocument as P.PartDocument;
                            if(occurrencePart==null)continue;
                            string name=null;try{name=occurrencePart.FullName;}catch{}
                            if(string.Equals(name,partName,StringComparison.OrdinalIgnoreCase))return assembly;
                        }
                    }catch{}
                }
            }catch(Exception e){Log.Write("EditingAssembly",e);}
            Log.Write("ContextProbe","打开的装配里没有匹配该零件的实例：" + SafeName(Application.ActiveDocument));
            return null;
        }
        // 可用上下文 = 活动文档是装配，或者正在原位编辑装配里的某个零件。
        // 判定结果变化时写一行日志：命令禁用（按钮变灰）时用户看不到任何提示，
        // 这行日志是唯一能自证"为什么灰"的地方。
        static string lastContextProbe;
        public bool HasEditableContext(){
            bool hasAssembly=HasAssembly();
            var editing=hasAssembly?null:EditingAssembly();
            bool result=hasAssembly||editing!=null;
            string summary="activeIsAssembly="+hasAssembly+" editingAssembly="+(editing==null?"null":SafeName(editing))+" => "+result;
            if(summary!=lastContextProbe){lastContextProbe=summary;Log.Write("ContextProbe",summary);}
            return result;
        }
        static string SafeName(object document){try{dynamic d=document;return Convert.ToString(d.Name);}catch(Exception e){return "读名字失败:"+e.Message;}}
        public A.AssemblyDocument RequireEditableAssembly(){
            var assembly=Application.ActiveDocument as A.AssemblyDocument;
            if(assembly!=null)return assembly;
            assembly=EditingAssembly();
            if(assembly==null)throw new InvalidOperationException("请先打开装配文件，或在装配里双击要打孔的零件进入原位编辑。");
            return assembly;
        }
        // All modeless tools share one selection session; switching tools closes the previous form.
        // 要在模型上点选的窗口（IPickingWindow）挂上"停靠 / 收起"条：贴 CAD 右边打开、记住位置、点模型自动收起。
        public void Show(Func<Form> create,Action<Form> start){
            TianGongCadSuite.Licensing.LicenseGate.Require();
            Dispose();var next=create();active=next;
            try{int frame=Application.ActiveFramehWnd;ToolWindow.Attach(next,frame);next.Show(new CadOwner(frame));if(start!=null)start(next);}catch{Dispose();throw;}
        }
        public void Dispose(){if(active!=null){if(!active.IsDisposed)active.Close();active.Dispose();active=null;}}
    }
    public sealed class InsetPanelModule : IToolModule {
        readonly ToolContext context;
        public InsetPanelModule(ToolContext value){context=value;}
        public string Id {get{return "inset-panel";}}
        public IEnumerable<ToolCommand> Commands {get{
            yield return new ToolCommand(1,"四面生成内嵌板","四面和定位点生成独立板子","选四个内侧平面 → 定位点 → 厚度和间隙 → 保存新 PAR。",context.HasAssembly,()=>{var doc=context.RequireAssembly();context.Show(()=>new PanelForm(context.Application,doc),f=>((PanelForm)f).StartPicking());});
            yield return new ToolCommand(2,"型材自动填充","识别型材闭合框口并生成板子","在装配中多选型材，再设置板厚及间隙，检查框口并生成。",context.HasAssembly,()=>{var doc=context.RequireAssembly();context.Show(()=>new AutoPanelForm(context.Application,doc),null);});
        }}
        public void Dispose(){}
    }
    public static class ModuleCatalog {
        // 已在功能区下线的命令（客户版不再出现）：
        //   1 = 四面生成内嵌板（手工选四面，已被"型材自动填充"取代）
        //   4 = 导出出图训练数据（内部研究用）
        // 只影响"注册哪些按钮"；对应实现仍编译在 DLL 里，命令 ID 也永久保留。
        static readonly int[] Hidden=new int[]{1,4};
        public static ToolRegistry Create(ToolContext context){var registry=new ToolRegistry();foreach(int id in Hidden)registry.Retire(id);registry.Add(new InsetPanelModule(context));registry.Add(new LineupModule(context));registry.Add(new TrainingExportModule(context));registry.Add(new FormatConvertModule(context));registry.Add(new AutoHoleModule(context));return registry;}
    }
}
