using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using F=SolidEdgeFramework;
[assembly:ComVisible(false)]
[assembly:System.Reflection.AssemblyVersion("0.4.0.0")]
[assembly:System.Runtime.CompilerServices.InternalsVisibleTo("PanelTests")]
namespace TianGongCadSuite {
    [ComVisible(true),Guid("8C05165C-65A4-4EF2-A138-508589D82004"),ProgId("TianGongCadSuite.SuiteDevAddIn"),ClassInterface(ClassInterfaceType.None)]
    public sealed class PanelAddIn : F.ISolidEdgeAddIn,F.ISEAddInEvents {
        const string AssemblyCategory="{26618395-09D6-11D1-BA07-080036230602}";
        F.AddIn addin;Connection events;ToolContext context;ToolRegistry registry;Diagnostics diagnostics;
        public void OnConnection(object application,F.SeConnectMode mode,F.AddIn instance){
            try{addin=instance;context=new ToolContext((F.Application)application);registry=ModuleCatalog.Create(context);
                addin.Description="天工工具箱 DEV 0.4 — 可扩展功能框架";addin.GuiVersion=42;addin.Visible=true;
                events=new Connection(addin.AddInEvents,typeof(F.ISEAddInEvents),this);
                diagnostics=new Diagnostics(context.Application,()=>Run(registry.Commands[0]),()=>Run(registry.Commands[1]),()=>Run(registry.Commands[2]));addin.Object=diagnostics;
                diagnostics.QueryCommand=id=>{int flags=0,bitmap=0;string text;OnCommandUpdateUI(id,ref flags,out text,ref bitmap);return flags;};
                diagnostics.GetNativeId=id=>registry.NativeId(id);
            }catch(Exception e){Log.Write("Connect",e);OnDisconnection(default(F.SeDisconnectMode));throw;}
        }
        public void OnConnectToEnvironment(string category,object environment,bool firstTime){
            if(!SupportedEnvironment(category))return;
            try{int count=registry.Commands.Count;var labels=new string[count];var localIds=new int[count];var buttons=new F.SeButtonStyle[count];
                for(int i=0;i<count;i++){var c=registry.Commands[i];labels[i]="{8C05165C-65A4-4EF2-A138-508589D82004}_"+c.Id+"\n"+c.Title+"\n"+c.Description+"\n"+c.Title;localIds[i]=c.Id;buttons[i]=F.SeButtonStyle.seButtonCaption;}
                Array names=labels,ids=localIds,styles=buttons;
                ((F.ISEAddInEx2)addin).SetAddInInfoEx2(typeof(PanelAddIn).Assembly.Location,category,"天工工具箱 DEV\n功能",0,0,0,0,count,ref names,ref ids,ref styles);
                registry.Bind(ids);
                if(firstTime)foreach(var c in registry.Commands){var button=addin.AddCommandBarButton(category,"天工工具箱 DEV\n功能",c.Id);button.Style=F.SeButtonStyle.seButtonCaption;button.Caption=c.Title;}
                diagnostics.MenuStatus="Registered "+count+" commands";
            }catch(Exception e){diagnostics.MenuStatus="ERROR: "+e.Message;Log.Write("Register",e);}
        }
        static bool SupportedEnvironment(string category){foreach(string id in new[]{AssemblyCategory,TGSDK.CATID.TGPart,TGSDK.CATID.TGDraft,TGSDK.CATID.TGSheetMetal,TGSDK.CATID.TGDMPart,TGSDK.CATID.TGDMSheetMetal})if(string.Equals(category.Trim('{','}'),id.Trim('{','}'),StringComparison.OrdinalIgnoreCase))return true;return false;}
        void Run(ToolCommand c){if(c==null)return;try{if(!c.CanExecute())throw new InvalidOperationException("请先打开适用的 CAD 文件。");c.Execute();}catch(Exception e){Log.Write(c.Title,e);MessageBox.Show(e.Message,"天工工具箱",MessageBoxButtons.OK,MessageBoxIcon.Error);}}
        public void OnCommand(int id){if(diagnostics!=null)diagnostics.LastCommand=id;if(registry!=null)Run(registry.Find(id));}
        public void OnCommandHelp(int id,int frame,int help){var c=registry==null?null:registry.Find(id);if(c!=null)MessageBox.Show(c.Help,c.Title);}
        public void OnCommandUpdateUI(int id,ref int flags,out string text,ref int bitmap){var c=registry==null?null:registry.Find(id);text=c==null?"":c.Title;flags=0;try{if(c!=null&&c.CanExecute())flags=1;}catch{}}
        public void OnDisconnection(F.SeDisconnectMode mode){try{if(context!=null)context.Dispose();}finally{if(registry!=null)registry.Dispose();if(diagnostics!=null)diagnostics.Dispose();if(events!=null)events.Dispose();context=null;registry=null;diagnostics=null;events=null;addin=null;}}
    }
    [ComVisible(true),Guid("68F0F28A-52D6-4B57-BF52-D56CE6807204"),ClassInterface(ClassInterfaceType.AutoDual)]
    public sealed class Diagnostics : IDisposable {
        internal F.Application Host;internal Action Open,OpenAuto,OpenLineup;readonly Control dispatcher;
        internal Func<int,int> QueryCommand,GetNativeId;
        public int LastCommand {get;internal set;}
        public int CommandFlags(int localId){return QueryCommand(localId);}
        public int NativeCommandId(int localId){return GetNativeId(localId);}
        public int LineupWindowCount {get{int count=0;foreach(Form form in Application.OpenForms)if((form is LineupForm||form is LineupTableForm)&&form.Visible)count++;return count;}}
        public string Result {get;private set;}
        public string MenuStatus {get;internal set;}
        public Diagnostics(F.Application host,Action open,Action openAuto,Action openLineup){Host=host;Open=open;OpenAuto=openAuto;OpenLineup=openLineup;dispatcher=new Control();var handle=dispatcher.Handle;}
        public void Dispose(){dispatcher.Dispose();Host=null;Open=null;OpenAuto=null;OpenLineup=null;}
        public void OpenLineupPanel(){dispatcher.BeginInvoke(OpenLineup);}
        public string LoadedLibrary {get{return typeof(Diagnostics).Assembly.Location;}}
        public void StartSmartTests(string output){Result="";dispatcher.BeginInvoke((Action)(()=>{Result=RunTestsMode(output,"--smart-cad");}));}
        public void StartEntryTests(string output){Result="";dispatcher.BeginInvoke((Action)(()=>{Result=RunTestsMode(output,"--entry-cad");}));}
        public void StartTableTests(string output){Result="";dispatcher.BeginInvoke((Action)(()=>{Result=RunTestsMode(output,"--table-cad");}));}
        public void StartLineupTests(string output){Result="";dispatcher.BeginInvoke((Action)(()=>{Result=RunTestsMode(output,"--lineup");}));}
        public void StartLineupReopenTests(string fixture){Result="";dispatcher.BeginInvoke((Action)(()=>{Result=RunTestsMode(fixture,"--lineup-reopen");}));}
        public void OpenPanel(){dispatcher.BeginInvoke(Open);}
        public void OpenAutoPanel(){dispatcher.BeginInvoke(OpenAuto);}
        public void StartTests(string output){Result="";dispatcher.BeginInvoke((Action)(()=>{Result=RunTests(output);}));}
        // DEV-only local integration-test entry point. Only loads the sibling test executable.
        public void StartAutoTests(string output){Result="";dispatcher.BeginInvoke((Action)(()=>{Result=RunTests(output,true);}));}
        public string RunTests(string output){return RunTests(output,false);}
        string RunTests(string output,bool autoOnly){return RunTestsMode(output,autoOnly?"--auto":"--cad");}
        string RunTestsMode(string output,string mode){
            var previous=Console.Out;var previousError=Console.Error;var writer=new System.IO.StringWriter();
            try{Console.SetOut(writer);Console.SetError(writer);var path=System.IO.Path.Combine(System.IO.Path.GetDirectoryName(typeof(Diagnostics).Assembly.Location),"PanelTests.exe");var test=System.Reflection.Assembly.LoadFrom(path);test.GetType("Program").GetField("Host").SetValue(null,Host);object result=test.EntryPoint.Invoke(null,new object[]{new[]{mode,output}});writer.WriteLine("EXIT "+result);}
            catch(Exception e){writer.WriteLine(e);}finally{Console.SetOut(previous);Console.SetError(previousError);}return writer.ToString();
        }
    }
}
