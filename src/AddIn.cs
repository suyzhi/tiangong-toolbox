using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using F=SolidEdgeFramework;
using A=SolidEdgeAssembly;
[assembly:ComVisible(false)]
[assembly:System.Reflection.AssemblyVersion("0.1.2.0")]
[assembly:System.Runtime.CompilerServices.InternalsVisibleTo("PanelTests")]
namespace TianGongPanel {
    [ComVisible(true),Guid("98BF0FA8-7D65-4A13-9926-6A45836FD6D2"),ProgId("TianGongPanel.AddIn"),ClassInterface(ClassInterfaceType.None)]
    public sealed class PanelAddIn : F.ISolidEdgeAddIn,F.ISEAddInEvents {
        const string AssemblyCategory="{26618395-09D6-11D1-BA07-080036230602}";
        F.Application app;F.AddIn addin;Connection events;PanelForm form;int commandId=1; int lineupCommandId=2; Diagnostics diagnostics;
        public void OnConnection(object application,F.SeConnectMode mode,F.AddIn instance){try{app=(F.Application)application;addin=instance;addin.Description="矩形板生成工具（DEV 0.1.2）";addin.GuiVersion=10;addin.Visible=true;events=new Connection(addin.AddInEvents,typeof(F.ISEAddInEvents),this);diagnostics=new Diagnostics(app,()=>OnCommand(commandId));addin.Object=diagnostics;}catch(Exception e){Log.Write("OnConnection",e);throw;}}
        public void OnConnectToEnvironment(string category,object environment,bool firstTime){
            if(!string.Equals(category,AssemblyCategory,StringComparison.OrdinalIgnoreCase))return;
            try{Array names=new string[]{"{98BF0FA8-7D65-4A13-9926-6A45836FD6D2}_1\n生成矩形板\n四面和定位点生成独立板子\n生成矩形板","{98BF0FA8-7D65-4A13-9926-6A45836FD6D2}_2\nLineup模型标记\n模型属性与清单导出\nLineup模型标记"};Array ids=new int[]{1,2};Array styles=new F.SeButtonStyle[]{F.SeButtonStyle.seButtonCaption};
                ((F.ISEAddInEx2)addin).SetAddInInfoEx2(typeof(PanelAddIn).Assembly.Location,category,"矩形板工具\n板子",0,0,0,0,1,ref names,ref ids,ref styles);
                if(firstTime){var button=addin.AddCommandBarButton(category,"矩形板工具\n板子",1);button.Style=F.SeButtonStyle.seButtonCaption;button.Caption="生成矩形板";}
                commandId=Convert.ToInt32(ids.GetValue(0));lineupCommandId=Convert.ToInt32(ids.GetValue(1));
                diagnostics.MenuStatus="Registered command "+commandId+", firstTime="+firstTime;
            }catch(Exception e){diagnostics.MenuStatus="ERROR: "+e.Message;Log.Write("RegisterCommand",e);}
        }
        public void OnDisconnection(F.SeDisconnectMode mode){if(form!=null&&!form.IsDisposed)form.Close();if(events!=null)events.Dispose();events=null;addin=null;app=null;}
        public void OnCommand(int id){if(id==lineupCommandId){try{var asm2=app.ActiveDocument as A.AssemblyDocument;if(asm2==null)throw new InvalidOperationException("请先打开装配文件。");new LineupForm(app,asm2).Show(new CadOwner(app.ActiveFramehWnd));}catch(Exception e){MessageBox.Show(e.Message,"Lineup");}return;}if(id!=commandId&&id!=1)return;try{if(form!=null&&!form.IsDisposed){form.Activate();return;}var asm=app.ActiveDocument as A.AssemblyDocument;if(asm==null)throw new InvalidOperationException("请先打开装配文件。");form=new PanelForm(app,asm);form.Show(new CadOwner(app.ActiveFramehWnd));form.StartPicking();}catch(Exception e){if(form!=null&&!form.IsDisposed)form.Close();Log.Write("Command",e);MessageBox.Show(e.Message,"矩形板工具",MessageBoxButtons.OK,MessageBoxIcon.Error);}}
        public void OnCommandHelp(int id,int frame,int help){if(id==commandId)MessageBox.Show("选四个内侧平面 → 选择定位点/直线边/圆形边 → 输入厚度和间隙 → 保存新 PAR。","矩形板帮助");}
        public void OnCommandUpdateUI(int id,ref int flags,out string text,ref int bitmap){text="生成矩形板";if(id==commandId||id==1){try{flags=app.ActiveDocument is A.AssemblyDocument?1:0;}catch{flags=0;}}}
    }
    [ComVisible(true),Guid("E4994D7E-B60F-4A9C-9C29-7D2B4B9A4607"),ClassInterface(ClassInterfaceType.AutoDual)]
    public sealed class Diagnostics {
        internal F.Application Host;internal Action Open;readonly Control dispatcher;
        public string Result {get;private set;}
        public string MenuStatus {get;internal set;}
        public Diagnostics(F.Application host,Action open){Host=host;Open=open;dispatcher=new Control();var handle=dispatcher.Handle;}
        public void OpenPanel(){dispatcher.BeginInvoke(Open);}
        public void StartTests(string output){Result="";dispatcher.BeginInvoke((Action)(()=>{Result=RunTests(output);}));}
        // DEV-only local integration-test entry point. Only loads the sibling test executable.
        public string RunTests(string output){
            var previous=Console.Out;var previousError=Console.Error;var writer=new System.IO.StringWriter();
            try{Console.SetOut(writer);Console.SetError(writer);var path=System.IO.Path.Combine(System.IO.Path.GetDirectoryName(typeof(Diagnostics).Assembly.Location),"PanelTests.exe");var test=System.Reflection.Assembly.LoadFrom(path);test.GetType("Program").GetField("Host").SetValue(null,Host);object result=test.EntryPoint.Invoke(null,new object[]{new[]{"--cad",output}});writer.WriteLine("EXIT "+result);}
            catch(Exception e){writer.WriteLine(e);}finally{Console.SetOut(previous);Console.SetError(previousError);}return writer.ToString();
        }
    }
}

