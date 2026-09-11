using System;
using System.IO;
using System.Drawing;
using System.Globalization;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using F=SolidEdgeFramework;
using A=SolidEdgeAssembly;
namespace TianGongCadSuite {
    public static class Log {
        public static string FilePath {get{return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"TianGongCadSuite","panel.log");}}
        public static void Write(string area,Exception e){Write(area,e.ToString());}
        public static void Write(string area,string detail){try{Directory.CreateDirectory(Path.GetDirectoryName(FilePath));File.AppendAllText(FilePath,DateTime.Now.ToString("s")+" "+area+" "+detail+Environment.NewLine);}catch{}}
    }
    [ComVisible(true),ClassInterface(ClassInterfaceType.None)]
    public sealed class PanelForm : Form,F.ISEMouseEvents,F.ISECommandEvents {
        readonly F.Application app;readonly A.AssemblyDocument assembly;
        readonly List<object> faces=new List<object>();object point;
        F.ISECommand command;F.ISEMouse mouse;Connection mouseConnection,commandConnection;Preview preview;F.HighlightSet highlights;
        bool closing,busy,moveLogged,hoverLogged;PanelSpec spec;
        F.HighlightSet hoverHighlights;object hovered;
        readonly Label hoverStatus=new Label();
        internal int PickingMode {get{return mouse==null?-1:mouse.LocateMode;}}
        internal bool MoveFeedbackEnabled {get{return mouse!=null&&mouse.EnabledMove;}}
        internal string HoverMessage {get{return hoverStatus.Text;}}
        internal bool InterDocumentPicking {get{return mouse!=null&&((F.ISEMouseEx)mouse).InterDocumentLocate;}}
        internal int PickedFaceCount {get{return faces.Count;}}
        readonly TextBox thickness=new TextBox(),gap=new TextBox{Text="0"};
        readonly Label prompt=new Label(),dimensions=new Label(),message=new Label();
        readonly ListBox picked=new ListBox();readonly Button generate=new Button();
        internal void SetParameters(string t,string g){thickness.Text=t;gap.Text=g;}
        internal bool PreviewReady {get{return generate.Enabled;}}
        internal string Message {get{return message.Text;}}
        internal int PreviewDrawCount {get{return preview==null?0:preview.DrawCount;}}
        public PanelForm(F.Application app,A.AssemblyDocument assembly){
            this.app=app;this.assembly=assembly;
            Text="生成矩形板 · DEV 0.4.0";ClientSize=new Size(390,600);Font=new Font("Microsoft YaHei UI",9);FormBorderStyle=FormBorderStyle.SizableToolWindow;MinimumSize=new Size(406,639);MaximizeBox=false;MinimizeBox=false;ShowInTaskbar=false;StartPosition=FormStartPosition.Manual;Location=new Point(Screen.PrimaryScreen.WorkingArea.Left+12,Screen.PrimaryScreen.WorkingArea.Top+150);
            var split=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=1};split.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,390));split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));Controls.Add(split);
            preview=new Preview();split.Controls.Add(preview,1,0);
            var layout=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(16),ColumnCount=2,RowCount=11};layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,112));layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));split.Controls.Add(layout,0,0);
            prompt.AutoSize=false;prompt.Dock=DockStyle.Fill;prompt.Font=new Font(Font,FontStyle.Bold);layout.Controls.Add(prompt,0,0);layout.SetColumnSpan(prompt,2);layout.RowStyles.Add(new RowStyle(SizeType.Absolute,52));
            picked.Dock=DockStyle.Fill;layout.Controls.Add(picked,0,1);layout.SetColumnSpan(picked,2);layout.RowStyles.Add(new RowStyle(SizeType.Absolute,116));
            var undo=new Button{Text="撤回上次选择",Dock=DockStyle.Fill};undo.Click+=(s,e)=>UndoPick();layout.Controls.Add(undo,0,2);
            var reset=new Button{Text="重新选择",Dock=DockStyle.Fill};reset.Click+=(s,e)=>{faces.Clear();point=null;Changed();};layout.Controls.Add(reset,1,2);layout.RowStyles.Add(new RowStyle(SizeType.Absolute,36));
            layout.Controls.Add(new Label{Text="总厚度（mm）",Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft},0,3);thickness.Dock=DockStyle.Fill;layout.Controls.Add(thickness,1,3);layout.RowStyles.Add(new RowStyle(SizeType.Absolute,38));
            layout.Controls.Add(new Label{Text="每边间隙（mm）",Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft},0,4);gap.Dock=DockStyle.Fill;layout.Controls.Add(gap,1,4);layout.RowStyles.Add(new RowStyle(SizeType.Absolute,38));
            dimensions.Dock=DockStyle.Fill;layout.Controls.Add(dimensions,0,5);layout.SetColumnSpan(dimensions,2);layout.RowStyles.Add(new RowStyle(SizeType.Absolute,48));
            var hint=new Label{Text="蓝色：板子边框；橙色：定位中面。\n选直线边取中点，选圆形边取圆心。\n每侧拉伸总厚度的一半；尺寸按平面延伸计算。",Dock=DockStyle.Fill};layout.Controls.Add(hint,0,6);layout.SetColumnSpan(hint,2);layout.RowStyles.Add(new RowStyle(SizeType.Absolute,64));
            message.Dock=DockStyle.Fill;message.ForeColor=Color.Firebrick;layout.Controls.Add(message,0,7);layout.SetColumnSpan(message,2);layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            generate.Text="保存零件并插入装配";generate.Dock=DockStyle.Fill;generate.Enabled=false;generate.Click+=(s,e)=>Generate();layout.Controls.Add(generate,0,8);layout.SetColumnSpan(generate,2);layout.RowStyles.Add(new RowStyle(SizeType.Absolute,38));
            var cancel=new Button{Text="取消 / Esc",Dock=DockStyle.Fill};cancel.Click+=(s,e)=>Close();layout.Controls.Add(cancel,0,9);layout.SetColumnSpan(cancel,2);layout.RowStyles.Add(new RowStyle(SizeType.Absolute,32));CancelButton=cancel;
            hoverStatus.Dock=DockStyle.Fill;hoverStatus.Text="请将鼠标移入模型，悬停查看可选对象。";layout.Controls.Add(hoverStatus,0,10);layout.SetColumnSpan(hoverStatus,2);layout.RowStyles.Add(new RowStyle(SizeType.Absolute,40));
            thickness.TextChanged+=(s,e)=>UpdatePreview();gap.TextChanged+=(s,e)=>UpdatePreview();FormClosed+=(s,e)=>Cleanup();
            Changed();
        }
        public void StartPicking(){
            try {
                if(assembly.ReadOnly||assembly.InPlaceActivated)throw new InvalidOperationException("请先退出原位编辑，并在可编辑的装配顶层运行。");
                command=(F.ISECommand)app.CreateCommand((int)SolidEdgeConstants.seCmdFlag.seNoDeactivate);commandConnection=new Connection(command,typeof(F.ISECommandEvents),this);
                // Start first: the native command creates/initializes its live mouse service here.
                command.Start();
                mouse=(F.ISEMouse)command.Mouse;mouseConnection=new Connection(mouse,typeof(F.ISEMouseEvents),this);
                // Use the 3D click-locate path; SmartMouse is a different native locate mode.
                mouse.ScaleMode=1;mouse.WindowTypes=1;mouse.LocateMode=(int)SolidEdgeConstants.seLocateModes.seLocateSimple;mouse.EnabledMove=true;
                // Extended locate options fail before Start() in TianGong CAD 2025.
                ((F.ISEMouseEx)mouse).InterDocumentLocate=true;
                ((F.ISEMouseEx3)mouse).PathfinderLocate=false;
                ConfigureFilter();
                moveLogged=false;hoverLogged=false;
                Log.Write("PickStart","build=0.4.0, CAD="+app.Version+", interDocument="+InterDocumentPicking+", locateMode="+mouse.LocateMode+", move="+mouse.EnabledMove+", windowTypes="+mouse.WindowTypes);
                if(highlights!=null)highlights.Delete();highlights=assembly.HighlightSets.Add();highlights.Color=ColorTranslator.ToOle(Color.DeepSkyBlue);
                hoverHighlights=assembly.HighlightSets.Add();hoverHighlights.Color=ColorTranslator.ToOle(Color.Gold);
            }catch{Cleanup();throw;}
        }
        void ConfigureFilter(){if(mouse==null)return;mouse.ClearLocateFilter();
            if(faces.Count<4)mouse.AddToLocateFilter((int)SolidEdgeConstants.seLocateFilterConstants.seLocateFace);
            else {mouse.AddToLocateFilter((int)SolidEdgeConstants.seLocateFilterConstants.seLocateVertex);mouse.AddToLocateFilter((int)SolidEdgeConstants.seLocateFilterConstants.seLocateEdge);mouse.AddToLocateFilter((int)SolidEdgeConstants.seLocateFilterConstants.seLocatePoint);}
        }
        void UndoPick(){if(point!=null)point=null;else if(faces.Count>0)faces.RemoveAt(faces.Count-1);Changed();}
        public void AcceptPick(object selected){
            if(busy||closing||selected==null)return;
            try{
                EnsureContext();var geometry=PickGeometry.Unwrap(selected);
                if(faces.Count<4){
                    if(!(selected is F.Reference)&&!(selected is A.TopologyReference))throw new ArgumentException("请选择装配内零件实例上的平面。");
                    var candidate=geometry.Plane();
                    foreach(var f in faces){var old=PickGeometry.Unwrap(f).Plane();if(old.Normal.Cross(candidate.Normal).Length<PanelGeometry.AngularTolerance&&Math.Abs(old.Normal.Dot(old.Point-candidate.Point))<PanelGeometry.DistanceTolerance)throw new ArgumentException("这个平面已经选择过，请选择另一侧平面。");}
                    if(faces.Count==3)PanelGeometry.Solve(faces.Concat(new[]{selected}).Select(f=>PickGeometry.Unwrap(f).Plane()).ToArray(),candidate.Point,.001,0);
                    faces.Add(selected);
                } else {if(!(selected is F.Reference)&&!(selected is A.TopologyReference)&&!(selected is A.AsmRefPoint))throw new ArgumentException("请选择装配实例上的定位点/边，或装配基准点。");geometry.Point();point=selected;}
                Log.Write("PickAccepted",(faces.Count<4?"face ":"face/point ")+faces.Count+"; "+PickGeometry.Describe(selected));Changed();
            }catch(Exception e){message.Text=e.Message;Log.Write("Pick",e);Log.Write("PickObject",PickGeometry.Describe(selected));}
        }
        void Changed(){picked.Items.Clear();for(int i=0;i<faces.Count;i++)picked.Items.Add("面 "+(i+1)+"  已选择");if(point!=null)picked.Items.Add("定位点  已选择");
            prompt.Text=faces.Count<4?"第 1 步：在模型中点击四个内侧面（"+faces.Count+" / 4）":point==null?"第 2 步：在模型中点击定位点或边":"第 3 步：输入总厚度，检查预览";
            ClearHover();hoverLogged=false;
            if(point!=null&&ClientSize.Width<940)ClientSize=new Size(940,600);
            ConfigureFilter();if(highlights!=null){highlights.RemoveAll();foreach(var f in faces)highlights.AddItem(f);if(point!=null)highlights.AddItem(point);highlights.Draw();}UpdatePreview();
        }
        void EnsureContext(){if(!ReferenceEquals(app.ActiveDocument,assembly))throw new InvalidOperationException("活动装配已改变，请关闭窗口后在目标装配重新运行。");if(assembly.ReadOnly||assembly.InPlaceActivated)throw new InvalidOperationException("装配不可编辑或处于原位编辑中。");}
        PanelSpec ReadSpec(){EnsureContext();double t,g;if(!double.TryParse(thickness.Text,NumberStyles.Float,CultureInfo.CurrentCulture,out t)||!double.TryParse(gap.Text,NumberStyles.Float,CultureInfo.CurrentCulture,out g))throw new ArgumentException("请填写有效的总厚度和间隙，单位 mm。");return PanelGeometry.Solve(faces.Select(f=>PickGeometry.Unwrap(f).Plane()).ToArray(),PickGeometry.Unwrap(point).Point(),t/1000,g/1000);}
        void UpdatePreview(){if(closing||busy)return;spec=null;generate.Enabled=false;dimensions.Text="";message.Text="";
            try{if(faces.Count==4&&point!=null){spec=ReadSpec();dimensions.Text=string.Format("板子：{0:0.###} × {1:0.###} × {2:0.###} mm\n两侧各 {3:0.###} mm",spec.Width*1000,spec.Height*1000,spec.Thickness*1000,spec.Thickness*500);generate.Enabled=true;}}
            catch(Exception e){message.Text=e.Message;}
            if(preview!=null){preview.Spec=spec;try{preview.SourceEdges=faces.SelectMany(f=>PickGeometry.Unwrap(f).Outline()).ToList();if(point!=null)preview.PickedPoint=PickGeometry.Unwrap(point).Point();preview.Refresh();}catch(Exception e){message.Text=e.Message;generate.Enabled=false;}}
        }
        void Generate(){
            try {spec=ReadSpec();using(var dialog=new SaveFileDialog{Title="保存新板子零件",Filter="天工 CAD 零件 (*.par)|*.par",DefaultExt="par",AddExtension=true,OverwritePrompt=false,FileName="矩形板_"+DateTime.Now.ToString("yyyyMMdd_HHmmss")+".par",InitialDirectory=Directory.Exists(assembly.Path)?assembly.Path:Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)}){
                    if(dialog.ShowDialog(this)!=DialogResult.OK)return;if(File.Exists(dialog.FileName)||Directory.Exists(dialog.FileName)){message.Text="目标已存在，请更换文件名。";return;}
                    spec=ReadSpec();busy=true;Enabled=false;StopCommand();
                    var occurrence=CadBuilder.Generate(app,assembly,spec,dialog.FileName);
                    MessageBox.Show(this,"已生成并插入：\n"+dialog.FileName+"\n\n请保存装配。", "矩形板已生成",MessageBoxButtons.OK,MessageBoxIcon.Information);Close();
                }
            }catch(Exception e){Log.Write("Generate",e);busy=false;Enabled=true;try{if(command==null){faces.Clear();point=null;StartPicking();Changed();}else UpdatePreview();message.Text=e.Message+"\n生成未完成，可重新选择后再试。";}catch(Exception restart){message.Text=e.Message+"\n请关闭后重新运行："+restart.Message;generate.Enabled=false;}}
        }
        void StopCommand(){hovered=null;if(hoverHighlights!=null){try{hoverHighlights.Delete();}catch{}hoverHighlights=null;}if(highlights!=null){try{highlights.Delete();}catch{}highlights=null;}if(mouseConnection!=null){mouseConnection.Dispose();mouseConnection=null;}if(commandConnection!=null){commandConnection.Dispose();commandConnection=null;}var c=command;command=null;mouse=null;if(c!=null)try{c.Done=true;}catch{}}
        void Cleanup(){if(closing)return;closing=true;if(highlights!=null){try{highlights.Delete();}catch{}highlights=null;}StopCommand();if(preview!=null){preview.Dispose();preview=null;}faces.Clear();point=null;}
        public new void MouseClick(short b,short s,double x,double y,double z,object w,int k,object g){if(b==1){if(g==null){message.Text="此处未捕捉到面或边，请移动到实体面内部；可拖动窗口让出模型。";Log.Write("PickMiss","key="+k);return;}AcceptPick(g);}else if(b==2)Close();}
        public new void MouseDown(short b,short s,double x,double y,double z,object w,int k,object g){}public new void MouseUp(short b,short s,double x,double y,double z,object w,int k,object g){}
        void ClearHover(){hovered=null;if(hoverHighlights!=null)try{hoverHighlights.RemoveAll();hoverHighlights.Draw();}catch(Exception e){Log.Write("HoverClear",e);}hoverStatus.Text="请将鼠标移入模型，悬停查看可选对象。";hoverStatus.ForeColor=SystemColors.ControlText;}
        public new void MouseMove(short b,short s,double x,double y,double z,object w,int k,object g){
            if(closing||busy)return;
            if(!moveLogged){Log.Write("PickMove","first move; key="+k+", object="+PickGeometry.Describe(g));moveLogged=true;}
            if(g==null){if(hovered!=null)ClearHover();hoverStatus.Text="当前未捕捉到对象，请指向可见的实体面。";return;}
            if(ReferenceEquals(hovered,g))return;
            ClearHover();
            try{
                var geometry=PickGeometry.Unwrap(g);
                if(!(g is F.Reference)&&!(g is A.TopologyReference)&&!(faces.Count>=4&&g is A.AsmRefPoint))throw new ArgumentException("请指向装配内零件的面或边。");
                if(faces.Count<4)geometry.Plane();else geometry.Point();
                hovered=g;hoverStatus.Text=faces.Count<4?"可选平面：单击加入（"+faces.Count+" / 4）":"可选定位点 / 边：单击确认";hoverStatus.ForeColor=Color.DarkGreen;
                if(!hoverLogged){Log.Write("PickHover",PickGeometry.Describe(g));hoverLogged=true;}
                if(hoverHighlights!=null)try{hoverHighlights.AddItem(g);hoverHighlights.Draw();}catch(Exception e){Log.Write("HoverDraw",e);}
            }catch(Exception e){hoverStatus.Text=e.Message;}
        }
        public void MouseDblClick(short b,short s,double x,double y,double z,object w,int k,object g){}
        public void MouseDrag(short b,short s,double x,double y,double z,object w,short ds,int k,object g){}
        void F.ISECommandEvents.Activate(){}void F.ISECommandEvents.Deactivate(){}
        public void Terminate(){if(!closing&&!busy)BeginInvoke((Action)(()=>Close()));}
        public void Idle(int n,out bool more){more=false;}
        public new void KeyDown(ref short key,short shift){if(key==27)Close();}public new void KeyPress(ref short key){}public new void KeyUp(ref short key,short shift){}
    }
    public sealed class CadOwner : IWin32Window {public IntPtr Handle{get;private set;}public CadOwner(int handle){Handle=new IntPtr(handle);}}
}
