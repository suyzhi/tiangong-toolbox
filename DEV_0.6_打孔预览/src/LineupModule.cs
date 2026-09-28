using System;
using System.Drawing;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using F=SolidEdgeFramework;
using A=SolidEdgeAssembly;
namespace TianGongCadSuite {
    public sealed class LineupModule : IToolModule {
        readonly ToolContext context; public LineupModule(ToolContext c){context=c;}
        public string Id {get{return "lineup";}}
        public IEnumerable<ToolCommand> Commands {get{yield return new ToolCommand(3,"Lineup 模型标记","五类清单、自动编号与零件关联","保存装配 → 选择零件或子装配 → 批量加入 / 绑定清单 → 导出。",context.HasAssembly,()=>{var d=context.RequireAssembly();context.Show(()=>new LineupTableForm(context.Application,d),null);});}}
        public void Dispose(){}
    }
    public sealed class LineupForm:Form {
        readonly F.Application app;readonly A.AssemblyDocument assembly;readonly string assemblyFile,store;
        LineupProject project;LineupRecord editing,binding=new LineupRecord();
        readonly ListBox records=new ListBox();readonly TextBox[] fields=new TextBox[13];
        readonly ComboBox category=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList,Dock=DockStyle.Fill};
        readonly Label status=new Label{Dock=DockStyle.Bottom,Height=44,AutoEllipsis=true};
        readonly TextBox location=new TextBox{ReadOnly=true,Dock=DockStyle.Fill};
        dynamic highlight;
        internal static readonly string[] Names={"编号","地址","功能参数","描述","型号","品牌","原位","安装方式","机构名称","传感器接口类型","备注","所属阀岛","所属阀片"};
        public LineupForm(F.Application a,A.AssemblyDocument d){
            app=a;assembly=d;assemblyFile=LineupCad.AssemblyFile(d);store=Path.ChangeExtension(assemblyFile,".lineup.xml");
            // Never replace a corrupt or foreign project with an empty one.
            project=File.Exists(store)?LineupProject.Load(store,assemblyFile):new LineupProject{AssemblyFile=assemblyFile};
            Text="Lineup 模型标记";Font=new Font("Microsoft YaHei UI",9F);Width=1000;Height=780;MinimumSize=new Size(850,650);
            var split=new SplitContainer{Size=ClientSize,Dock=DockStyle.Fill,FixedPanel=FixedPanel.Panel1,SplitterDistance=280,Panel1MinSize=200,Panel2MinSize=500};Controls.Add(split);Controls.Add(status);
            records.Dock=DockStyle.Fill;split.Panel1.Controls.Add(records);records.SelectedIndexChanged+=(s,e)=>SelectRecord();
            var panel=new Panel{Dock=DockStyle.Fill,AutoScroll=true};split.Panel2.Controls.Add(panel);
            var p=new TableLayoutPanel{Dock=DockStyle.Top,AutoSize=true,Padding=new Padding(10),ColumnCount=2};
            p.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,130));p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));panel.Controls.Add(p);
            for(int i=0;i<Names.Length;i++){
                fields[i]=new TextBox{Dock=DockStyle.Fill,Name="Field"+i};
                p.Controls.Add(new Label{Text=Names[i],AutoSize=true,Anchor=AnchorStyles.Left},0,i);p.Controls.Add(fields[i],1,i);
            }
            category.Items.AddRange(new object[]{"","阀岛","阀片","气缸","传感器","其他"});category.SelectedIndex=0;
            p.Controls.Add(new Label{Text="类别",AutoSize=true},0,13);p.Controls.Add(category,1,13);
            p.Controls.Add(new Label{Text="关联模型",AutoSize=true},0,14);p.Controls.Add(location,1,14);
            var actions=new FlowLayoutPanel{AutoSize=true,Dock=DockStyle.Fill,WrapContents=true};
            AddButton(actions,"新建记录",NewRecord);AddButton(actions,"读取当前选择",CaptureSelection);
            AddButton(actions,"保存记录",()=>SaveRecord());AddButton(actions,"定位模型",()=>Locate(true));
            AddButton(actions,"清除高亮",ClearHighlight);AddButton(actions,"检查关系",()=>{var errors=project.Validate();status.Text=errors.Count==0?"检查通过。":string.Join("；",errors);});
            AddButton(actions,"导出 CSV",()=>{using(var dialog=new SaveFileDialog{Filter="CSV 文件|*.csv",FileName="Lineup.csv"})if(dialog.ShowDialog(this)==DialogResult.OK)ExportTo(dialog.FileName);});
            p.Controls.Add(actions,0,15);p.SetColumnSpan(actions,2);
            RefreshRecords(null);status.Text="选择一个顶层模型实例。所属阀岛/阀片填写对应记录编号；数据保存在装配旁的 .lineup.xml。";
            FormClosed+=(s,e)=>ClearHighlight();
        }
        void AddButton(FlowLayoutPanel panel,string text,Action action){var button=new Button{Text=text,AutoSize=true};button.Click+=(s,e)=>{try{EnsureDocument();action();}catch(Exception ex){status.Text=ex.Message;}};panel.Controls.Add(button);}
        void EnsureDocument(){if(!string.Equals(LineupCad.AssemblyFile(assembly),assemblyFile,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("装配已另存，请关闭并重新打开 Lineup。");if(!string.Equals(Convert.ToString(((dynamic)app.ActiveDocument).FullName),assemblyFile,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("请先激活 Lineup 所属装配。");}
        public int RecordCount {get{return project.Records.Count;}}
        public string StatusText {get{return status.Text;}}
        internal void SetField(int index,string value){fields[index].Text=value;}
        internal string GetField(int index){return fields[index].Text;}
        internal int FieldWidth {get{return fields[4].Width;}}
        internal void SetCategory(string value){category.SelectedItem=value;}
        internal void SelectIndex(int index){records.SelectedIndex=index;}
        internal void NewRecord(){records.ClearSelected();editing=null;binding=new LineupRecord();foreach(var field in fields)field.Clear();category.SelectedIndex=0;location.Clear();ClearHighlight();}
        internal void CaptureSelection(){EnsureDocument();var next=new LineupRecord();LineupCad.Capture(assembly,next);binding=next;location.Text=string.Join(" / ",binding.InstancePath);status.Text="已读取模型持久化关联；型号字段保持不变。";}
        void RefreshRecords(LineupRecord selected){records.Items.Clear();foreach(var record in project.Records)records.Items.Add(record);if(selected!=null)records.SelectedItem=selected;}
        string LinkNumber(string id){var r=project.Records.FirstOrDefault(x=>x.Id==id);return r==null?id:r.Number;}
        string LinkId(string number,string kind){if(string.IsNullOrWhiteSpace(number))return "";var target=project.Records.FirstOrDefault(x=>x.Category==kind&&string.Equals(x.Number,number.Trim(),StringComparison.OrdinalIgnoreCase));if(target==null)throw new InvalidOperationException("未找到"+kind+"编号："+number);return target.Id;}
        static string[] Cells(LineupRecord r){return new[]{r.Number,r.Address,r.Parameters,r.Description,r.Model,r.Brand,r.Home,r.Mounting,r.Mechanism,r.Connector,r.Notes,r.IslandId,r.ValveId};}
        void SelectRecord(){
            var r=records.SelectedItem as LineupRecord;if(r==null)return;editing=r;binding=r;var cells=Cells(r);
            for(int i=0;i<fields.Length;i++)fields[i].Text=cells[i];fields[11].Text=LinkNumber(r.IslandId);fields[12].Text=LinkNumber(r.ValveId);
            category.SelectedItem=r.Category;location.Text=string.Join(" / ",r.InstancePath??new string[0]);
            try{EnsureDocument();Locate(false);}catch(Exception ex){status.Text="记录已加载；定位失败："+ex.Message;}
        }
        internal LineupRecord SaveRecord(){
            EnsureDocument();string number=fields[0].Text.Trim();if(number.Length==0)throw new InvalidOperationException("编号不能为空。");
            if(project.Records.Any(x=>x!=editing&&string.Equals((x.Number??"").Trim(),number,StringComparison.OrdinalIgnoreCase)))throw new InvalidOperationException("编号重复。");
            var r=new LineupRecord{Id=editing==null?Guid.NewGuid().ToString("N"):editing.Id,Number=number,Category=Convert.ToString(category.SelectedItem),Address=fields[1].Text,Parameters=fields[2].Text,Description=fields[3].Text,Model=fields[4].Text,Brand=fields[5].Text,Home=fields[6].Text,Mounting=fields[7].Text,Mechanism=fields[8].Text,Connector=fields[9].Text,Notes=fields[10].Text,IslandId=LinkId(fields[11].Text,"阀岛"),ValveId=LinkId(fields[12].Text,"阀片"),CylinderId=editing==null?"":editing.CylinderId,Position=editing==null?"":editing.Position,SourceFile=binding.SourceFile,ReferenceKey=binding.ReferenceKey,InstancePath=binding.InstancePath};
            var next=new LineupProject{AssemblyFile=assemblyFile,Records=new List<LineupRecord>(project.Records)};
            if(editing==null)next.Records.Add(r);else next.Records[next.Records.IndexOf(editing)]=r;
            var errors=next.Validate();if(errors.Count>0)throw new InvalidOperationException(string.Join("；",errors));
            next.Save(store);project=next;editing=r;binding=r;RefreshRecords(r);status.Text="已保存："+store;return r;
        }
        internal void ExportTo(string file){var rows=new List<string[]>();rows.Add(Names);foreach(var r in project.Records){var cells=Cells(r);cells[11]=LinkNumber(r.IslandId);cells[12]=LinkNumber(r.ValveId);rows.Add(cells);}File.WriteAllText(file,LineupCsv.Write(rows),System.Text.Encoding.UTF8);status.Text="已导出 "+project.Records.Count+" 条记录。";}
        internal A.Occurrence Locate(bool zoom){
            EnsureDocument();if(editing==null)throw new InvalidOperationException("请先选择列表记录。");
            ClearHighlight();var target=LineupCad.Resolve(assembly,editing);highlight=assembly.HighlightSets.Add();highlight.Color=0x00FFFF;highlight.AddItem(target);highlight.Draw();
            if(zoom){double x1,y1,z1,x2,y2,z2;target.Range(out x1,out y1,out z1,out x2,out y2,out z2);double pad=Math.Max(Math.Max(x2-x1,y2-y1),z2-z1)*.15+.001;dynamic view=((dynamic)app.ActiveWindow).View;view.RangeZoomCamera(x1-pad,y1-pad,z1-pad,x2+pad,y2+pad,z2+pad);view.Update();}
            status.Text="已定位："+target.Name;return target;
        }
        void ClearHighlight(){object previous=highlight;highlight=null;if(previous!=null){try{((dynamic)previous).Delete();}catch{}}}
    }
}
