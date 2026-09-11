using System;
using System.IO;
using System.Linq;
using System.Drawing;
using System.Globalization;
using System.Collections.Generic;
using System.Windows.Forms;
using F=SolidEdgeFramework;
using A=SolidEdgeAssembly;
namespace TianGongPanelAuto {
    public sealed class AutoPanelForm:Form {
        readonly F.Application app;readonly A.AssemblyDocument assembly;
        readonly TextBox thickness=new TextBox{Text="6"},gap=new TextBox{Text="0"};
        readonly Label status=new Label{AutoSize=false,Dock=DockStyle.Fill};
        readonly ListBox openings=new ListBox{Dock=DockStyle.Fill};readonly Preview preview=new Preview();
        readonly Button generate=new Button{Text="批量保存并插入全部框口",Dock=DockStyle.Fill,Enabled=false};
        List<object> selection=new List<object>();List<FrameMember> cachedMembers;List<PanelSpec> specs=new List<PanelSpec>();bool busy;
        internal int OpeningCount{get{return specs.Count;}}
        internal string StatusText{get{return status.Text;}}
        public AutoPanelForm(F.Application app,A.AssemblyDocument assembly){
            this.app=app;this.assembly=assembly;Text="多型材自动填充 · DEV 0.3";ClientSize=new Size(1020,660);MinimumSize=new Size(960,660);Font=new Font("Microsoft YaHei UI",9);ShowInTaskbar=false;
            var split=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2};split.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,365));split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));Controls.Add(split);split.Controls.Add(preview,1,0);
            var left=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,Padding=new Padding(16)};left.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,125));left.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));split.Controls.Add(left,0,0);
            Add(left,new Label{Text="先用 CAD 的 Ctrl 多选或框选选择型材，再点击下方读取。也可选一个框架子装配。\n自动识别同一平面上的闭合矩形框口。",Dock=DockStyle.Fill},0,76);
            var read=new Button{Text="读取当前选择并识别框口",Dock=DockStyle.Fill};read.Click+=(s,e)=>ReadSelection();Add(left,read,1,40);
            left.Controls.Add(new Label{Text="总厚度（mm）",Dock=DockStyle.Fill},0,2);left.Controls.Add(thickness,1,2);left.RowStyles.Add(new RowStyle(SizeType.Absolute,38));
            left.Controls.Add(new Label{Text="每边间隙（mm）",Dock=DockStyle.Fill},0,3);left.Controls.Add(gap,1,3);left.RowStyles.Add(new RowStyle(SizeType.Absolute,38));
            Add(left,new Label{Text="定位：采用型材厚度中心的共同平面。\n边界取中心剖面的材料面，可识别槽内边界。\n点击列表逐个预览，生成时包含全部框口。",Dock=DockStyle.Fill},4,68);
            Add(left,openings,5,160);Add(left,status,6,100);Add(left,generate,7,42);
            var close=new Button{Text="关闭",Dock=DockStyle.Fill};close.Click+=(s,e)=>Close();Add(left,close,8,34);CancelButton=close;
            openings.SelectedIndexChanged+=(s,e)=>ShowPreview();thickness.TextChanged+=(s,e)=>Recompute();gap.TextChanged+=(s,e)=>Recompute();generate.Click+=(s,e)=>Generate();
            ReadSelection();
        }
        static void Add(TableLayoutPanel p,Control c,int row,int height){p.Controls.Add(c,0,row);p.SetColumnSpan(c,2);p.RowStyles.Add(new RowStyle(SizeType.Absolute,height));}
        void Context(){if(!ReferenceEquals(app.ActiveDocument,assembly)||assembly.ReadOnly||assembly.InPlaceActivated)throw new InvalidOperationException("请保持目标装配处于活动、可编辑的顶层状态。");}
        internal void ReadSelection(){try{Context();selection=assembly.SelectSet.Cast<object>().ToList();cachedMembers=null;if(selection.Count>0)cachedMembers=FrameReader.Read(assembly,selection);Recompute();}catch(Exception e){selection.Clear();cachedMembers=null;InvalidateResult(e.Message);}}
        internal void SetParameters(string t,string g){thickness.Text=t;gap.Text=g;}
        void InvalidateResult(string text){specs.Clear();openings.Items.Clear();generate.Enabled=false;preview.Spec=null;preview.SourceEdges.Clear();preview.Invalidate();status.Text=text;status.ForeColor=Color.Firebrick;}
        List<PanelSpec> ReadSpecs(bool refresh){Context();double t,g;if(!double.TryParse(thickness.Text,NumberStyles.Float,CultureInfo.CurrentCulture,out t)||!double.TryParse(gap.Text,NumberStyles.Float,CultureInfo.CurrentCulture,out g))throw new ArgumentException("请输入有效的厚度和间隙（mm）。");
            if(refresh||cachedMembers==null)cachedMembers=FrameReader.Read(assembly,selection);var found=FrameDetection.Detect(cachedMembers,t/1000,g/1000);var result=found.Select(o=>o.Solve(t/1000,g/1000)).ToList();preview.SourceEdges=cachedMembers.SelectMany(m=>m.Faces).SelectMany(f=>f.Edges).ToList();return result;
        }
        void Recompute(){if(busy)return;InvalidateResult("");if(selection.Count==0){status.Text="尚未读取型材，请在模型中多选后点击读取。";return;}
            try{specs=ReadSpecs(false);for(int i=0;i<specs.Count;i++){var s=specs[i];openings.Items.Add(string.Format("框口 {0}：{1:0.###} × {2:0.###} × {3:0.###} mm",i+1,s.Width*1000,s.Height*1000,s.Thickness*1000));}openings.SelectedIndex=0;generate.Enabled=true;status.ForeColor=Color.DarkGreen;status.Text="识别到 "+specs.Count+" 个闭合框口。请检查预览后生成。";}catch(Exception e){InvalidateResult(e.Message);Log.Write("AutoDetect",e);}
        }
        void ShowPreview(){int i=openings.SelectedIndex;preview.Spec=i>=0&&i<specs.Count?specs[i]:null;if(preview.Spec!=null)preview.PickedPoint=preview.Spec.Corner(0,0);preview.Invalidate();}
        void Generate(){try{specs=ReadSpecs(true);using(var dialog=new FolderBrowserDialog{Description="选择输出目录；会创建独立批次子目录，每个框口一个 PAR"}){if(dialog.ShowDialog(this)!=DialogResult.OK)return;
                    specs=ReadSpecs(true);busy=true;Enabled=false;
                    string dir=BatchPanels.Generate(app,assembly,specs,dialog.SelectedPath);
                    MessageBox.Show(this,"已生成 "+specs.Count+" 块板子：\n"+dir+"\n请保存装配。","自动填充完成");Close();
                }}catch(Exception e){Log.Write("AutoGenerate",e);busy=false;Enabled=true;Recompute();status.Text=e.Message;status.ForeColor=Color.Firebrick;}}
    }
    public static class BatchPanels {
        public static string Generate(F.Application app,A.AssemblyDocument assembly,IList<PanelSpec> specs,string parent){
            if(specs==null||specs.Count==0)throw new ArgumentException("没有待生成框口。");
            if(!Directory.Exists(parent))throw new DirectoryNotFoundException(parent);
            string directory=Path.Combine(parent,"自动填充_"+DateTime.Now.ToString("yyyyMMdd_HHmmss")+"_"+Guid.NewGuid().ToString("N").Substring(0,8));Directory.CreateDirectory(directory);
            var created=new List<Tuple<A.Occurrence,string>>();
            try{for(int i=0;i<specs.Count;i++){string file=Path.Combine(directory,"填充板_"+(i+1).ToString("D3")+".par");created.Add(Tuple.Create(CadBuilder.Generate(app,assembly,specs[i],file),file));}
                File.WriteAllLines(Path.Combine(directory,"尺寸清单.csv"),new[]{"文件,宽mm,高mm,厚mm,每边间隙mm"}.Concat(specs.Select((s,i)=>string.Format(CultureInfo.InvariantCulture,"填充板_{0:D3}.par,{1:R},{2:R},{3:R},{4:R}",i+1,s.Width*1000,s.Height*1000,s.Thickness*1000,s.Gap*1000))),System.Text.Encoding.UTF8);return directory;
            }catch(Exception original){var errors=new List<string>();foreach(var item in created.AsEnumerable().Reverse()){try{item.Item1.Delete();File.Delete(item.Item2);}catch(Exception e){errors.Add(item.Item2+": "+e.Message);}}
                throw new InvalidOperationException("批量生成未完成："+original.Message+(errors.Count>0?"\n部分结果未能撤回，请检查：\n"+string.Join("\n",errors):"\n本批已插入的板子已撤回。")+"\n批次目录："+directory,original);
            }
        }
    }
}
