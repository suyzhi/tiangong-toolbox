using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using F=SolidEdgeFramework;
using A=SolidEdgeAssembly;
[assembly:ComVisible(false)]
[assembly:System.Reflection.AssemblyVersion("0.2.0.0")]
[assembly:System.Runtime.CompilerServices.InternalsVisibleTo("PanelTests")]
namespace TianGongPanelAuto {
    [ComVisible(true),Guid("B3B28C54-11D0-4488-B226-AD62429CEED2"),ProgId("TianGongPanelAuto.AutoDevAddIn"),ClassInterface(ClassInterfaceType.None)]
    public sealed class PanelAddIn : F.ISolidEdgeAddIn,F.ISEAddInEvents {
        const string AssemblyCategory="{26618395-09D6-11D1-BA07-080036230602}";
        F.Application app;F.AddIn addin;Connection events;PanelForm form;AutoPanelForm autoForm;int commandId=1,autoCommandId=2;Diagnostics diagnostics;
        public void OnConnection(object application,F.SeConnectMode mode,F.AddIn instance){try{app=(F.Application)application;addin=instance;addin.Description="矩形板生成工具（DEV 0.2.0）";addin.GuiVersion=10;addin.Visible=true;events=new Connection(addin.AddInEvents,typeof(F.ISEAddInEvents),this);diagnostics=new Diagnostics(app,()=>OnCommand(commandId),()=>OnCommand(autoCommandId));addin.Object=diagnostics;}catch(Exception e){Log.Write("OnConnection",e);throw;}}
        public void OnConnectToEnvironment(string category,object environment,bool firstTime){
            if(!string.Equals(category,AssemblyCategory,StringComparison.OrdinalIgnoreCase))return;
            try{Array names=new string[]{"{B3B28C54-11D0-4488-B226-AD62429CEED2}_1\n生成矩形板\n四面和定位点生成独立板子\n生成矩形板","{B3B28C54-11D0-4488-B226-AD62429CEED2}_2\n多型材自动填充\n批量选择型材并识别闭合矩形框口\n多型材自动填充"};Array ids=new int[]{1,2};Array styles=new F.SeButtonStyle[]{F.SeButtonStyle.seButtonCaption,F.SeButtonStyle.seButtonCaption};
                ((F.ISEAddInEx2)addin).SetAddInInfoEx2(typeof(PanelAddIn).Assembly.Location,category,"自动填充 DEV\n板子",0,0,0,0,2,ref names,ref ids,ref styles);
                if(firstTime){var button=addin.AddCommandBarButton(category,"自动填充 DEV\n板子",1);button.Style=F.SeButtonStyle.seButtonCaption;button.Caption="生成矩形板";}
                if(firstTime){var button=addin.AddCommandBarButton(category,"自动填充 DEV\n板子",2);button.Style=F.SeButtonStyle.seButtonCaption;button.Caption="多型材自动填充";}
                commandId=Convert.ToInt32(ids.GetValue(0));autoCommandId=Convert.ToInt32(ids.GetValue(1));
                diagnostics.MenuStatus="Registered commands manual="+commandId+", auto="+autoCommandId+", firstTime="+firstTime;
            }catch(Exception e){diagnostics.MenuStatus="ERROR: "+e.Message;Log.Write("RegisterCommand",e);}
        }
        public void OnDisconnection(F.SeDisconnectMode mode){if(autoForm!=null&&!autoForm.IsDisposed)autoForm.Close();if(form!=null&&!form.IsDisposed)form.Close();if(events!=null)events.Dispose();events=null;addin=null;app=null;}
        public void OnCommand(int id){if(id==autoCommandId||id==2){try{if(form!=null&&!form.IsDisposed)form.Close();if(autoForm!=null&&!autoForm.IsDisposed){autoForm.Activate();return;}var target=app.ActiveDocument as A.AssemblyDocument;if(target==null)throw new InvalidOperationException("请先打开装配并多选型材。");autoForm=new AutoPanelForm(app,target);autoForm.Show(new CadOwner(app.ActiveFramehWnd));}catch(Exception e){Log.Write("AutoCommand",e);MessageBox.Show(e.Message,"多型材自动填充");}return;}if(id!=commandId&&id!=1)return;try{if(form!=null&&!form.IsDisposed){form.Activate();return;}var asm=app.ActiveDocument as A.AssemblyDocument;if(asm==null)throw new InvalidOperationException("请先打开装配文件。");form=new PanelForm(app,asm);form.Show(new CadOwner(app.ActiveFramehWnd));form.StartPicking();}catch(Exception e){if(form!=null&&!form.IsDisposed)form.Close();Log.Write("Command",e);MessageBox.Show(e.Message,"矩形板工具",MessageBoxButtons.OK,MessageBoxIcon.Error);}}
        public void OnCommandHelp(int id,int frame,int help){if(id==commandId)MessageBox.Show("选四个内侧平面 → 选择定位点/直线边/圆形边 → 输入厚度和间隙 → 保存新 PAR。","矩形板帮助");}
        public void OnCommandUpdateUI(int id,ref int flags,out string text,ref int bitmap){text=(id==autoCommandId||id==2)?"多型材自动填充":"生成矩形板";if(id==commandId||id==1||id==autoCommandId||id==2){try{flags=app.ActiveDocument is A.AssemblyDocument?1:0;}catch{flags=0;}}}
    }
    [ComVisible(true),Guid("A8F55C45-9042-4095-9A9B-444108513EF1"),ClassInterface(ClassInterfaceType.AutoDual)]
    public sealed class Diagnostics {
        internal F.Application Host;internal Action Open,OpenAuto;readonly Control dispatcher;
        public string Result {get;private set;}
        public string MenuStatus {get;internal set;}
        public Diagnostics(F.Application host,Action open,Action openAuto){Host=host;Open=open;OpenAuto=openAuto;dispatcher=new Control();var handle=dispatcher.Handle;}
        public void OpenPanel(){dispatcher.BeginInvoke(Open);}
        public void OpenAutoPanel(){dispatcher.BeginInvoke(OpenAuto);}
        public void StartTests(string output){Result="";dispatcher.BeginInvoke((Action)(()=>{Result=RunTests(output);}));}
        // DEV-only local integration-test entry point. Only loads the sibling test executable.
        public void StartAutoTests(string output){Result="";dispatcher.BeginInvoke((Action)(()=>{Result=RunTests(output,true);}));}
        public string RunTests(string output){return RunTests(output,false);}
        string RunTests(string output,bool autoOnly){
            var previous=Console.Out;var previousError=Console.Error;var writer=new System.IO.StringWriter();
            try{Console.SetOut(writer);Console.SetError(writer);var path=System.IO.Path.Combine(System.IO.Path.GetDirectoryName(typeof(Diagnostics).Assembly.Location),"PanelTests.exe");var test=System.Reflection.Assembly.LoadFrom(path);test.GetType("Program").GetField("Host").SetValue(null,Host);object result=test.EntryPoint.Invoke(null,new object[]{new[]{autoOnly?"--auto":"--cad",output}});writer.WriteLine("EXIT "+result);}
            catch(Exception e){writer.WriteLine(e);}finally{Console.SetOut(previous);Console.SetError(previousError);}return writer.ToString();
        }
    }
}
