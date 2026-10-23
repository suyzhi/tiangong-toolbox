using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using F=SolidEdgeFramework;
[assembly:ComVisible(false)]
// 程序集版本由 tools/build.ps1 生成在 License/LicenseBuild.cs 里（授权自检会比对）。
// 测试程序访问内部成员、以及 Diagnostics 里加载 PanelTests.exe 的测试入口，只在开发构建（build.ps1 -DevBuild）里存在。
#if TG_DEV_BUILD
[assembly:System.Runtime.CompilerServices.InternalsVisibleTo("PanelTests")]
#endif
namespace TianGongCadSuite {
    [ComVisible(true),Guid("8C05165C-65A4-4EF2-A138-508589D82004"),ProgId("TianGongCadSuite.SuiteDevAddIn"),ClassInterface(ClassInterfaceType.None)]
    public sealed class PanelAddIn : F.ISolidEdgeAddIn,F.ISEAddInEvents {
        // 功能区：「天工工具箱」→ 组「功能」（原来叫"天工工具箱 DEV"，日常用的版本不该带 DEV 字样）
        const string RibbonGroup="天工工具箱\n功能";
        const string AssemblyCategory="{26618395-09D6-11D1-BA07-080036230602}";
        F.AddIn addin;Connection events;ToolContext context;ToolRegistry registry;Diagnostics diagnostics;
        public void OnConnection(object application,F.SeConnectMode mode,F.AddIn instance){
            // 自报家门：把实际加载的 DLL 路径与版本写进 panel.log。
            // 换版本做 A/B 时先看这一行，避免"注册表换了、CAD 还加载旧 DLL"的误判。
            try{Log.Write("AddInConnect","dll="+typeof(PanelAddIn).Assembly.Location+" ver="+typeof(PanelAddIn).Assembly.GetName().Version+" cad="+((F.Application)application).Version);}catch{}
            Guard.Install();
            try{addin=instance;context=new ToolContext((F.Application)application);registry=ModuleCatalog.Create(context);
                // GuiVersion 变了 CAD 才会按新的组名重建功能区按钮（firstTime=true）；改组名/增删按钮时要跟着加一。
                addin.Description=Product.Name+" "+Product.Version;addin.GuiVersion=45;addin.Visible=true;
                events=new Connection(addin.AddInEvents,typeof(F.ISEAddInEvents),this);
                // 按命令 ID 取（不要用下标：命令 1/4 已从功能区下线，下标会错位）
diagnostics=new Diagnostics(context.Application,()=>Run(registry.Find(1)),()=>Run(registry.Find(2)),()=>Run(registry.Find(3)));addin.Object=diagnostics;
                diagnostics.QueryCommand=id=>{int flags=0,bitmap=0;string text;OnCommandUpdateUI(id,ref flags,out text,ref bitmap);return flags;};
                diagnostics.GetNativeId=id=>registry.NativeId(id);
            }catch(Exception e){Log.Write("Connect",e);OnDisconnection(default(F.SeDisconnectMode));throw;}
        }
        public void OnConnectToEnvironment(string category,object environment,bool firstTime){
            if(!SupportedEnvironment(category))return;
            try{int count=registry.Commands.Count;var labels=new string[count];var localIds=new int[count];var buttons=new F.SeButtonStyle[count];
                for(int i=0;i<count;i++){var c=registry.Commands[i];labels[i]="{8C05165C-65A4-4EF2-A138-508589D82004}_"+c.Id+"\n"+c.Title+"\n"+c.Description+"\n"+c.Title;localIds[i]=c.Id;buttons[i]=F.SeButtonStyle.seButtonCaption;}
                Array names=labels,ids=localIds,styles=buttons;
                ((F.ISEAddInEx2)addin).SetAddInInfoEx2(typeof(PanelAddIn).Assembly.Location,category,RibbonGroup,0,0,0,0,count,ref names,ref ids,ref styles);
                registry.Bind(ids);
                if(firstTime)foreach(var c in registry.Commands){var button=addin.AddCommandBarButton(category,RibbonGroup,c.Id);button.Style=F.SeButtonStyle.seButtonCaption;button.Caption=c.Title;}
                diagnostics.MenuStatus="Registered "+count+" commands";
            }catch(Exception e){diagnostics.MenuStatus="ERROR: "+e.Message;Log.Write("Register",e);}
        }
        static bool SupportedEnvironment(string category){foreach(string id in new[]{AssemblyCategory,TGSDK.CATID.TGPart,TGSDK.CATID.TGDraft,TGSDK.CATID.TGSheetMetal,TGSDK.CATID.TGDMPart,TGSDK.CATID.TGDMSheetMetal})if(string.Equals(category.Trim('{','}'),id.Trim('{','}'),StringComparison.OrdinalIgnoreCase))return true;return false;}
        void Run(ToolCommand c){if(c==null)return;try{
            // 命令入口栅栏：未激活、过期、换机器、时间回调或环境异常都在此拦截。
            if(!TianGongCadSuite.Licensing.LicenseGate.EnsureInteractive(context==null?null:new CadOwner(context.Application.ActiveFramehWnd)))return;
            if(!c.CanExecute())throw new InvalidOperationException("请先打开适用的 CAD 文件。");c.Execute();}catch(Exception e){Log.Write(c.Title,e);MessageBox.Show(e.Message,"天工工具箱",MessageBoxButtons.OK,MessageBoxIcon.Error);}}
        public void OnCommand(int id){if(diagnostics!=null)diagnostics.LastCommand=id;if(registry!=null)Run(registry.Find(id));}
        public void OnCommandHelp(int id,int frame,int help){var c=registry==null?null:registry.Find(id);if(c!=null)MessageBox.Show(c.Help,c.Title);}
        public void OnCommandUpdateUI(int id,ref int flags,out string text,ref int bitmap){var c=registry==null?null:registry.Find(id);text=c==null?"":c.Title;flags=0;try{if(c!=null&&c.CanExecute())flags=1;}catch{}}
        public void OnDisconnection(F.SeDisconnectMode mode){Guard.Remove();try{if(context!=null)context.Dispose();}finally{if(registry!=null)registry.Dispose();if(diagnostics!=null)diagnostics.Dispose();if(events!=null)events.Dispose();context=null;registry=null;diagnostics=null;events=null;addin=null;}}
    }
    // 窗口事件（按钮、定时器、列表选择……）里漏网的异常：没人接的话 WinForms 弹自己的「未处理的异常」框，
    // 上面有「退出」—— 点了会把插件所有窗口一起关掉（处理中的打孔、原位编辑停在半截）。这里统一接住：记日志 + 提示一句。
    static class Guard {
        static bool on;
        public static void Install(){ if(on)return; try{Application.ThreadException+=OnThread;AppDomain.CurrentDomain.UnhandledException+=OnDomain;on=true;}catch{} }
        public static void Remove(){ if(!on)return; try{Application.ThreadException-=OnThread;AppDomain.CurrentDomain.UnhandledException-=OnDomain;}catch{} on=false; }
        static void OnThread(object sender,System.Threading.ThreadExceptionEventArgs e){
            try{Log.Write("Unhandled",e.Exception);}catch{}
            try{MessageBox.Show("天工工具箱出了一个意外错误（已记进 panel.log），可以继续使用：\r\n"+(e.Exception==null?"":e.Exception.Message),"天工工具箱",MessageBoxButtons.OK,MessageBoxIcon.Warning);}catch{}
        }
        static void OnDomain(object sender,UnhandledExceptionEventArgs e){ try{Log.Write("UnhandledDomain",Convert.ToString(e.ExceptionObject));}catch{} }
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
#if TG_DEV_BUILD
        public void StartSmartTests(string output){Result="";dispatcher.BeginInvoke((Action)(()=>{Result=RunTestsMode(output,"--smart-cad");}));}
        public void StartEntryTests(string output){Result="";dispatcher.BeginInvoke((Action)(()=>{Result=RunTestsMode(output,"--entry-cad");}));}
        public void StartEntryReopenTests(string output){Result="";dispatcher.BeginInvoke((Action)(()=>{Result=RunTestsMode(output,"--entry-reopen");}));}
        public void StartTableTests(string output){Result="";dispatcher.BeginInvoke((Action)(()=>{Result=RunTestsMode(output,"--table-cad");}));}
        public void StartLineupTests(string output){Result="";dispatcher.BeginInvoke((Action)(()=>{Result=RunTestsMode(output,"--lineup");}));}
        public void StartLineupReopenTests(string fixture){Result="";dispatcher.BeginInvoke((Action)(()=>{Result=RunTestsMode(fixture,"--lineup-reopen");}));}
        public void StartSpeedTests(string output){Result="";dispatcher.BeginInvoke((Action)(()=>{Result=RunTestsMode(output,"--autohole-speed");}));}   // 0.7.10 打孔计时（进程内）
        // 0.8.0 属性卡填写的真机探针（在 CAD 进程里跑：加密文件、装配里加载着的零件只有这里才测得到）
        public string PropCardCollect(){var cad=new PropCardCad(Host);string source;var list=cad.Collect(out source);return source+"\n"+string.Join("\n",list.ToArray());}
        public string PropCardRead(string path){var r=new PropCardCad(Host).Read(path);var sb=new System.Text.StringBuilder("problem="+r.Problem+"\nmaterial="+r.Material+"\n");foreach(var p in r.Original)sb.Append(p.Key+"="+p.Value+"\n");return sb.ToString();}
        public string PropCardWriteTest(string path,string field,string value,bool fillMissing){
            var cad=new PropCardCad(Host);string source;cad.Collect(out source);
            var r=cad.Read(path);if(r.Problem.Length>0)return "READ FAIL "+r.Problem;
            r.Set(field,value);var writes=r.Writes(new System.Collections.Generic.List<string>(PropCardFields.Default),fillMissing);
            var sw=System.Diagnostics.Stopwatch.StartNew();string result=cad.Write(r,writes);sw.Stop();
            var back=cad.Read(path);
            return "result=["+result+"] writes="+writes.Count+" ms="+sw.ElapsedMilliseconds+" back["+field+"]="+(back.Has(field)?back.Original[field]:"<missing>")+" missingAfter="+Missing(back);
        }
        // 直接用属性组件对一个文件做一步：add=加一个属性 / set=改第 0 个属性的值；save 决定存不存
        public string PropCardRaw(string path,string op,string name,string value,bool save){
            var sb=new System.Text.StringBuilder();dynamic sets=null;
            Func<Exception,string> hr=e=>{var c=e as System.Runtime.InteropServices.COMException;var i=e.InnerException as System.Runtime.InteropServices.COMException;return e.GetType().Name+" 0x"+((c??i)!=null?((c??i).ErrorCode).ToString("X8"):"?")+" "+e.Message;};
            try{sets=Activator.CreateInstance(Type.GetTypeFromProgID("SolidEdge.FileProperties"));sets.Open(path,false);sb.Append("open ok;");
                dynamic c=sets.Item("Custom");sb.Append("count="+c.Count+";");
                try{if(op=="add")c.Add(name,value);else{dynamic p=c.Item(0);sb.Append("p0="+p.Name+";");p.Value=value;}sb.Append(op+" ok;");}catch(Exception e){sb.Append(op+" FAIL "+hr(e)+";");}
                if(save){try{sets.Save();sb.Append("save ok;");}catch(Exception e){sb.Append("save FAIL "+hr(e)+";");}}
            }catch(Exception e){sb.Append("EXC "+hr(e));}
            finally{if(sets!=null){try{sets.Close();}catch{}try{System.Runtime.InteropServices.Marshal.FinalReleaseComObject((object)sets);}catch{}}}
            return sb.ToString();
        }
        // 0.8.1 的旧顺序复现：先取实例文档（加载）→ 属性组件写盘 → 再改内存那份
        public void StartPropCardOldOrder(string path){Result="";dispatcher.BeginInvoke((Action)(()=>{try{Result=PropCardOldOrder(path);}catch(Exception e){Result="EXC "+e;}}));}
        string PropCardOldOrder(string path){
            object doc=FindOccurrenceDoc((SolidEdgeAssembly.AssemblyDocument)Host.ActiveDocument,path);
            if(doc==null)return "no occurrence";
            string disk=PropCardRaw(path,"set","","旧顺序",true);
            var sets=(F.PropertySets)doc.GetType().InvokeMember("Properties",System.Reflection.BindingFlags.GetProperty,null,doc,null);
            object v="旧顺序";
            try{sets.Item("Custom").Item(1).set_Value(ref v);return disk+" mem ok";}catch(Exception e){return disk+" mem FAIL "+e.Message;}
        }
        static object FindOccurrenceDoc(SolidEdgeAssembly.AssemblyDocument asm,string path){
            foreach(SolidEdgeAssembly.Occurrence o in asm.Occurrences){
                if(string.Equals(o.OccurrenceFileName,path,StringComparison.OrdinalIgnoreCase))return o.OccurrenceDocument;
                if(o.Subassembly){var hit=FindOccurrenceDoc((SolidEdgeAssembly.AssemblyDocument)o.OccurrenceDocument,path);if(hit!=null)return hit;}
            }
            return null;
        }
        static string Missing(PropCardRow r){var l=new System.Collections.Generic.List<string>();foreach(var f in PropCardFields.Default)if(!r.Has(f))l.Add(f);return string.Join(",",l.ToArray());}
        public string PropCardFormShot(string png){
            var form=new PropCardForm(Host);
            try{form.Show();form.LoadActive();form.TestEditSome();Application.DoEvents();
                using(var bmp=new System.Drawing.Bitmap(form.Width,form.Height)){form.DrawToBitmap(bmp,new System.Drawing.Rectangle(0,0,form.Width,form.Height));bmp.Save(png);}
                return "ok";}
            finally{form.TestForceClose();}
        }
        // 异步版：像点功能区按钮一样在 CAD 自己的消息循环里跑（同步版是在外部 COM 调用里跑，
        // CAD 正忙着处理这次调用，嵌套调用文档会被消息筛选器拒绝 RPC_E_SERVERCALL_RETRYLATER）。跑完读 Result。
        public void StartPropCardFormTest(string field,string value,int rowCount){Result="";dispatcher.BeginInvoke((Action)(()=>{try{Result=PropCardFormTest(field,value,rowCount);}catch(Exception e){Result="EXC "+e;}}));}
        public string PropCardFormTest(string field,string value,int rowCount){
            string text="";var form=new PropCardForm(Host);
            try{form.Show();form.LoadActive();text=form.TestFillAndWrite(field,value,rowCount);}finally{form.TestForceClose();}
            return text;
        }
#endif
        public void OpenPanel(){dispatcher.BeginInvoke(Open);}
        public void OpenActivation(){dispatcher.BeginInvoke((Action)(()=>TianGongCadSuite.Licensing.LicenseActivation.OpenStatus()));}
        public string LicenseState(){return TianGongCadSuite.Licensing.LicenseLibrary.Current().Describe();}
        public string MachineCode(){return TianGongCadSuite.Licensing.LicenseLibrary.MachineCode();}
        public int GateFlags(){return TianGongCadSuite.Licensing.LicenseLibrary.Gate()?1:0;}
        public void OpenAutoPanel(){dispatcher.BeginInvoke(OpenAuto);}
#if TG_DEV_BUILD
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
#endif
    }
}
