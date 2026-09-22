using System;
using System.Collections.Generic;
using System.Windows.Forms;
using F=SolidEdgeFramework;
using A=SolidEdgeAssembly;
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
        public IList<ToolCommand> Commands {get{return commands.AsReadOnly();}}
        public void Add(IToolModule module){
            if(module==null)throw new ArgumentNullException("module");
            foreach(var m in modules)if(m.Id==module.Id)throw new ArgumentException("Duplicate module: "+module.Id);
            var pending=new List<ToolCommand>(module.Commands);var ids=new HashSet<int>();
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
        // All modeless tools share one selection session; switching tools closes the previous form.
        public void Show(Func<Form> create,Action<Form> start){
            Dispose();var next=create();active=next;
            try{next.Show(new CadOwner(Application.ActiveFramehWnd));if(start!=null)start(next);}catch{Dispose();throw;}
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
        public static ToolRegistry Create(ToolContext context){var registry=new ToolRegistry();registry.Add(new InsetPanelModule(context));registry.Add(new LineupModule(context));registry.Add(new TrainingExportModule(context));registry.Add(new FormatConvertModule(context));return registry;}
    }
}
