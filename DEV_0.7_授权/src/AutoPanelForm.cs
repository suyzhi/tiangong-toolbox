using System;
using System.IO;
using System.Linq;
using System.Drawing;
using System.Globalization;
using System.Collections.Generic;
using System.Windows.Forms;
using F=SolidEdgeFramework;
using A=SolidEdgeAssembly;
namespace TianGongCadSuite {
    public sealed class AutoPanelForm:Form,IPickingWindow {
        readonly F.Application app;readonly A.AssemblyDocument assembly;
        readonly TextBox thickness=new TextBox{Text="6"},gap=new TextBox{Text="0"};
        readonly Label status=new Label{AutoSize=false,Dock=DockStyle.Fill};
        readonly ListBox openings=new ListBox{Dock=DockStyle.Fill};readonly Preview preview=new Preview();
        readonly Button generate=Ui.Primary("批量保存并插入全部框口",210,38);
        List<object> selection=new List<object>();List<FrameMember> cachedMembers;List<PanelSpec> specs=new List<PanelSpec>();bool busy;
        internal int OpeningCount{get{return specs.Count;}}
        internal string StatusText{get{return status.Text;}}
        public AutoPanelForm(F.Application app,A.AssemblyDocument assembly){
            this.app=app;this.assembly=assembly;
            // 和打孔三件套同一套外观（UiKit）：左边按 ①②③ 步骤走，右边是位置预览。
            // 原来是系统默认的灰按钮 + 表格排版，和打孔窗口放一起像两个产品；宽度也从 1020 收到 880。
            Ui.Shell(this,"型材自动填充",880,660,760,620);
            generate.Enabled=false;
            BuildLayout();
            openings.SelectedIndexChanged+=(s,e)=>ShowPreview();thickness.TextChanged+=(s,e)=>Recompute();gap.TextChanged+=(s,e)=>Recompute();generate.Click+=(s,e)=>Generate();
            ReadSelection();
        }
        void BuildLayout(){
            var read=Ui.Primary("读取当前选择并识别框口",200,34);read.Click+=(s,e)=>ReadSelection();
            var paramCard=Ui.Card("板子参数",104,"改完自动重新识别");
            var body=new Panel{Dock=DockStyle.Fill,BackColor=Color.White};paramCard.Controls.Add(body);
            FieldRow(body,"总厚度",thickness,2);FieldRow(body,"每边间隙",gap,34);
            var listCard=Ui.Card("识别到的框口",0,"点一条看预览；生成时包含全部");
            openings.IntegralHeight=false;openings.BorderStyle=BorderStyle.FixedSingle;listCard.Controls.Add(openings);
            var note=Ui.Muted8("定位：型材厚度中心的共同平面；边界取中心剖面的材料面，可识别槽内边界。");note.AutoSize=false;note.Height=32;
            status.Height=56;status.ForeColor=Ui.Muted;
            var actions=new Panel{Height=46,BackColor=Ui.Bg};
            generate.SetBounds(0,4,210,38);
            var close=Ui.Secondary("关闭 (Esc)",96,30);close.SetBounds(222,8,96,30);close.Click+=(s,e)=>Close();CancelButton=close;
            actions.Controls.Add(generate);actions.Controls.Add(close);
            // 纵向堆叠：添加顺序 = 从上到下；框口列表吃掉剩余高度
            var left=new VerticalStack();
            left.Add(Ui.Step(1,"选型材","在模型里 Ctrl 多选或框选；也可以选一个框架子装配"));left.Add(read);
            left.Add(Ui.Step(2,"定板厚","总厚度和每边间隙"));left.Add(paramCard);
            left.Add(Ui.Step(3,"检查并生成","同一平面上的闭合矩形框口会自动识别"));left.Add(listCard,true);
            left.Add(note);left.Add(status);left.Add(actions);
            var columns=new SplitColumns(380){Dock=DockStyle.Fill};
            columns.Add(left);columns.Add(preview);
            Controls.Add(columns);
        }
        static void FieldRow(Panel p,string label,TextBox box,int y){
            var l=Ui.Caption(label);l.Location=new Point(0,y+4);p.Controls.Add(l);
            box.SetBounds(72,y,90,24);box.TextAlign=HorizontalAlignment.Right;p.Controls.Add(box);
            var u=Ui.Muted8("mm");u.Location=new Point(168,y+6);p.Controls.Add(u);
        }
        // IPickingWindow（见 ToolWindow）：在模型里多选型材时窗口自动收起；收起条上直接放「读取选择」，
        // 选完不用先展开再找按钮 —— 点它就读取、识别框口，然后窗口自己展开给你看结果。
        Label IPickingWindow.StatusLabel{get{return status;}}
        bool IPickingWindow.AutoCollapseByDefault{get{return true;}}
        string IPickingWindow.QuickActionText{get{return "读取选择";}}
        void IPickingWindow.QuickAction(){ReadSelection();}
        void Context(){if(!ReferenceEquals(app.ActiveDocument,assembly)||assembly.ReadOnly||assembly.InPlaceActivated)throw new InvalidOperationException("请保持目标装配处于活动、可编辑的顶层状态。");}
        internal void ReadSelection(){try{Context();selection=assembly.SelectSet.Cast<object>().ToList();cachedMembers=null;if(selection.Count>0)cachedMembers=FrameReader.Read(assembly,selection);Recompute();}catch(Exception e){selection.Clear();cachedMembers=null;InvalidateResult(e.Message);}}
        internal void SetParameters(string t,string g){thickness.Text=t;gap.Text=g;}
        void InvalidateResult(string text){specs.Clear();openings.Items.Clear();generate.Enabled=false;preview.Spec=null;preview.SourceEdges.Clear();preview.Invalidate();status.Text=text;status.ForeColor=Ui.Bad;}
        List<PanelSpec> ReadSpecs(bool refresh){Context();double t,g;if(!double.TryParse(thickness.Text,NumberStyles.Float,CultureInfo.CurrentCulture,out t)||!double.TryParse(gap.Text,NumberStyles.Float,CultureInfo.CurrentCulture,out g))throw new ArgumentException("请输入有效的厚度和间隙（mm）。");
            if(refresh||cachedMembers==null)cachedMembers=FrameReader.Read(assembly,selection);var found=FrameDetection.Detect(cachedMembers,t/1000,g/1000);var result=found.Select(o=>o.Solve(t/1000,g/1000)).ToList();preview.SourceEdges=cachedMembers.SelectMany(m=>m.Faces).SelectMany(f=>f.Edges).ToList();return result;
        }
        void Recompute(){if(busy)return;InvalidateResult("");if(selection.Count==0){status.Text="尚未读取型材，请在模型中多选后点击读取。";status.ForeColor=Ui.Muted;return;}
            try{specs=ReadSpecs(false);for(int i=0;i<specs.Count;i++){var s=specs[i];openings.Items.Add(string.Format("框口 {0}：{1:0.###} × {2:0.###} × {3:0.###} mm",i+1,s.Width*1000,s.Height*1000,s.Thickness*1000));}openings.SelectedIndex=0;generate.Enabled=true;status.ForeColor=Ui.Ok;status.Text="识别到 "+specs.Count+" 个闭合框口。请检查预览后生成。";}catch(Exception e){InvalidateResult(e.Message);Log.Write("AutoDetect",e);}
        }
        void ShowPreview(){int i=openings.SelectedIndex;preview.Spec=i>=0&&i<specs.Count?specs[i]:null;if(preview.Spec!=null)preview.PickedPoint=preview.Spec.Corner(0,0);preview.Invalidate();}
        void Generate(){try{specs=ReadSpecs(true);using(var dialog=new FolderBrowserDialog{Description="选择输出目录；会创建独立批次子目录，每个框口一个 PAR"}){if(dialog.ShowDialog(this)!=DialogResult.OK)return;
                    specs=ReadSpecs(true);busy=true;Enabled=false;
                    string dir=BatchPanels.Generate(app,assembly,specs,dialog.SelectedPath);
                    MessageBox.Show(this,"已生成 "+specs.Count+" 块板子：\n"+dir+"\n请保存装配。","自动填充完成");Close();
                }}catch(Exception e){Log.Write("AutoGenerate",e);busy=false;Enabled=true;Recompute();status.Text=e.Message;status.ForeColor=Ui.Bad;}}
    }
    public static class BatchPanels {
        public static string Generate(F.Application app,A.AssemblyDocument assembly,IList<PanelSpec> specs,string parent){
            if(specs==null||specs.Count==0)throw new ArgumentException("没有待生成框口。");
            if(!Directory.Exists(parent))throw new DirectoryNotFoundException(parent);
            // 文件名必须带批次时间戳（原因见 PanelNaming 的注释：CAD 会按文件名把装配目录里的旧同名零件插进来）。
            string stamp=PanelNaming.Stamp(DateTime.Now);
            Func<int,string> name=i=>PanelNaming.FileName(stamp,i);
            string directory=Path.Combine(parent,PanelNaming.BatchDirectoryName(stamp,Guid.NewGuid().ToString("N").Substring(0,8)));Directory.CreateDirectory(directory);
            var created=new List<Tuple<A.Occurrence,string>>();
            try{for(int i=0;i<specs.Count;i++){string file=Path.Combine(directory,name(i+1));created.Add(Tuple.Create(CadBuilder.Generate(app,assembly,specs[i],file),file));}
                File.WriteAllLines(Path.Combine(directory,"尺寸清单.csv"),new[]{"文件,宽mm,高mm,厚mm,每边间隙mm"}.Concat(specs.Select((s,i)=>string.Format(CultureInfo.InvariantCulture,"{0},{1:R},{2:R},{3:R},{4:R}",name(i+1),s.Width*1000,s.Height*1000,s.Thickness*1000,s.Gap*1000))),System.Text.Encoding.UTF8);return directory;
            }catch(Exception original){var errors=new List<string>();foreach(var item in created.AsEnumerable().Reverse()){try{item.Item1.Delete();File.Delete(item.Item2);}catch(Exception e){errors.Add(item.Item2+": "+e.Message);}}
                throw new InvalidOperationException("批量生成未完成："+original.Message+(errors.Count>0?"\n部分结果未能撤回，请检查：\n"+string.Join("\n",errors):"\n本批已插入的板子已撤回。")+"\n批次目录："+directory,original);
            }
        }
    }
}
