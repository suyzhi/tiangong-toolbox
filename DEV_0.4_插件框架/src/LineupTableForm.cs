using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using F=SolidEdgeFramework;
using A=SolidEdgeAssembly;
namespace TianGongCadSuite {
    public sealed partial class LineupTableForm:Form {
        readonly F.Application app;readonly A.AssemblyDocument assembly;readonly string file,store;
        LineupProject project;readonly DataTable data=new DataTable();
        readonly DataGridView grid=new DataGridView();
        readonly ComboBox category=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList,Width=135};
        readonly TextBox search=new TextBox{Width=180};
        readonly Label status=new Label{Dock=DockStyle.Bottom,Height=42};
        readonly Stack<LineupProject> undo=new Stack<LineupProject>();
        readonly Timer saver=new Timer{Interval=600};
        bool loading,dirty;dynamic highlight;
        public LineupTableForm(F.Application host,A.AssemblyDocument doc,string modelLibraryPath=null){
            app=host;assembly=doc;file=LineupCad.AssemblyFile(doc);store=Path.ChangeExtension(file,".lineup.xml");
            project=File.Exists(store)?LineupProject.Load(store,file):new LineupProject{AssemblyFile=file};
            InitializeCatalog(modelLibraryPath);
            Text="Lineup 快速录入";Font=new Font("Microsoft YaHei UI",9F);Width=1280;Height=660;MinimumSize=new Size(1040,520);StartPosition=FormStartPosition.Manual;var area=Screen.PrimaryScreen.WorkingArea;Location=new Point(Math.Max(area.Left,area.Right-Width-20),Math.Max(area.Top,area.Bottom-Height-40));
            var tools=BuildToolbar();
            helpBar=new Label{Text="上方填写本批共用字段，下方直接修改已有记录。F4 型号库 · Ctrl+V 粘贴 · Ctrl+Z 撤销",Dock=DockStyle.Top,Height=27,Padding=new Padding(8,4,0,0)};
            grid.Dock=DockStyle.Fill;grid.AutoGenerateColumns=false;grid.AllowUserToAddRows=false;grid.AllowUserToDeleteRows=false;grid.RowHeadersWidth=50;grid.SelectionMode=DataGridViewSelectionMode.CellSelect;grid.MultiSelect=true;grid.BackgroundColor=Color.White;grid.BorderStyle=BorderStyle.None;
            grid.ClipboardCopyMode=DataGridViewClipboardCopyMode.EnableWithoutHeaderText;
            grid.EnableHeadersVisualStyles=false;grid.ColumnHeadersDefaultCellStyle.BackColor=Color.FromArgb(225,234,242);grid.ColumnHeadersHeight=32;grid.RowTemplate.Height=28;grid.AlternatingRowsDefaultCellStyle.BackColor=Color.FromArgb(247,249,251);
            data.Columns.Add("Id");data.Columns.Add("类别");foreach(var h in LineupTableData.Headers)data.Columns.Add(h);data.Columns.Add("关联模型");data.Columns.Add("阀岛型号");
            var types=new DataGridViewComboBoxColumn{Name="类别",HeaderText="类别",DataPropertyName="类别",Width=110,FlatStyle=FlatStyle.Flat};
            types.Items.AddRange(LineupTableData.Categories);foreach(var c in project.Records.Select(r=>LineupTableData.Category(r.Category)).Distinct())if(!types.Items.Contains(c))types.Items.Add(c);grid.Columns.Add(types);
            int[] widths={155,170,120,170,240,130,90,140,140,130,220};
            for(int i=0;i<11;i++)grid.Columns.Add(new DataGridViewTextBoxColumn{Name=LineupTableData.Headers[i],HeaderText=LineupTableData.Headers[i],DataPropertyName=LineupTableData.Headers[i],Width=widths[i],SortMode=DataGridViewColumnSortMode.Automatic});
            grid.Columns.Add(new DataGridViewTextBoxColumn{Name="关联模型",HeaderText="关联模型",DataPropertyName="关联模型",Width=200,ReadOnly=true});
            grid.Columns.Add(new DataGridViewTextBoxColumn{Name="阀岛型号",HeaderText="所属阀岛的型号",DataPropertyName="阀岛型号",Width=220,ReadOnly=true});
            grid.Columns[0].Frozen=true;grid.Columns[1].Frozen=true;
            grid.DataSource=data;
            Controls.Add(grid);Controls.Add(helpBar);Controls.Add(tools);Controls.Add(status);InitializePickingPanel(tools);
            category.SelectedIndexChanged+=(s,e)=>Filter();search.TextChanged+=(s,e)=>Filter();
            grid.CellBeginEdit+=(s,e)=>{if(!loading)undo.Push(Snapshot(false));};
            grid.CellEndEdit+=(s,e)=>{if(!loading){if(e.ColumnIndex==5)Try(()=>CompleteKnownModel(e.RowIndex));dirty=true;saver.Stop();saver.Start();}};
            grid.DataError+=(s,e)=>{e.ThrowException=false;status.Text="请选择有效类目。";};
            grid.KeyDown+=(s,e)=>{if(e.Control&&e.KeyCode==Keys.V){e.SuppressKeyPress=true;Try(()=>PasteCells(Clipboard.GetText()));}if(e.Control&&e.KeyCode==Keys.Z){e.SuppressKeyPress=true;Try(Undo);}};
            saver.Tick+=(s,e)=>{saver.Stop();Try(SaveDraft);};
            FormClosing+=(s,e)=>{saver.Stop();if(dirty){try{SaveDraft();}catch(Exception ex){if(MessageBox.Show(this,ex.Message+"\n是否放弃尚未保存的修改并关闭？","Lineup",MessageBoxButtons.YesNo,MessageBoxIcon.Warning)!=DialogResult.Yes)e.Cancel=true;}}};
            FormClosed+=(s,e)=>{saver.Dispose();ClearHighlight();};
            SetupModelEditing();InitializeHierarchy();Reload(null);InitializeEntry();UpdateStatus("已加载");
        }
        void Button(FlowLayoutPanel p,string title,Action action){var b=new Button{Text=title,AutoSize=true,Height=28};b.Click+=(s,e)=>Try(action);p.Controls.Add(b);}
        void Try(Action action){try{action();}catch(Exception ex){status.Text=ex.Message;if(pickingMode&&pickingLabel!=null)pickingLabel.Text=ex.Message;}}
        void Ensure(){if(!string.Equals(LineupCad.AssemblyFile(assembly),file,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("装配已另存，请重新打开 Lineup。");if(!string.Equals(Convert.ToString(((dynamic)app.ActiveDocument).FullName),file,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("请先激活本清单所属装配。");}
        string ActiveCategory {get{return category.SelectedIndex>=1&&category.SelectedIndex<=5?Convert.ToString(category.SelectedItem):"气缸";}}
        public int RecordCount {get{return data.Rows.Count;}}
        public string StatusText {get{return status.Text;}}
        internal DataGridView Grid {get{return grid;}}
        internal LineupProject CurrentProject {get{SaveDraft();return project;}}
        internal void SetCategory(string value){category.SelectedItem=value;}
        LineupProject Snapshot(bool endEdit=true){
            if(endEdit)grid.EndEdit();var p=new LineupProject{AssemblyFile=file,Sections=new List<LineupSection>(project.Sections??new List<LineupSection>()),NumberPrefix=project.NumberPrefix,ActiveIsland=project.ActiveIsland,Islands=(project.Islands??new List<LineupIsland>()).Select(i=>new LineupIsland{Key=i.Key,Model=i.Model}).ToList()};
            p.EntryPresets=project.EntryPresets.Select(x=>x.Copy()).ToList();
            var previous=project.Records.ToDictionary(r=>r.Id);
            foreach(DataRow row in data.Rows){string id=(string)row["Id"];LineupRecord original;var r=previous.TryGetValue(id,out original)?LineupTableData.Copy(original):new LineupRecord{Id=id};r.Category=(string)row["类别"];LineupTableData.SetCells(r,LineupTableData.Headers.Select(h=>Convert.ToString(row[h])).ToArray());p.Records.Add(r);}return p;
        }
        void Reload(string selectedId){
            loading=true;data.Rows.Clear();var kinds=(DataGridViewComboBoxColumn)grid.Columns[0];foreach(var record in project.Records)if(!kinds.Items.Contains(record.Category))kinds.Items.Add(record.Category);
            foreach(var r in project.Records){var values=new List<object>{r.Id,LineupTableData.Category(r.Category)};values.AddRange(LineupTableData.Cells(r));values.Add(string.Join(" / ",r.InstancePath??new string[0]));values.Add(LineupNumbering.IslandModel(project,r));values.Add(LineupHierarchy.Parent(project,r));data.Rows.Add(values.ToArray());}
            loading=false;Filter();
            if(selectedId!=null)SelectId(selectedId);
        }
        void Filter(){
            if(loading)return;string filter="";
            if(category.SelectedIndex>=1&&category.SelectedIndex<=5)filter="[类别] = '"+ActiveCategory+"'";
            else if(category.SelectedIndex==6)filter="[类别] NOT IN ('"+string.Join("','",LineupTableData.Categories)+"')";
            string query=search.Text.Replace("'","''").Replace("[","[[]").Replace("%","[%]").Replace("*","[*]");
            if(query.Length>0){string match=string.Join(" OR ",LineupTableData.Headers.Select(h=>"["+h+"] LIKE '%"+query+"%'"));filter=(filter.Length>0?filter+" AND ":"")+"("+match+")";}
            data.DefaultView.RowFilter=filter;ApplyFieldVisibility();
        }
        string CurrentId(){if(pickingMode)return pickingId;if(grid.CurrentRow==null||grid.CurrentRow.DataBoundItem==null)return null;return Convert.ToString(((DataRowView)grid.CurrentRow.DataBoundItem)["Id"]);}
        List<string> SelectedIds(){return grid.SelectedCells.Cast<DataGridViewCell>().Where(c=>c.RowIndex>=0).OrderBy(c=>c.RowIndex).Select(c=>Convert.ToString(((DataRowView)grid.Rows[c.RowIndex].DataBoundItem)["Id"])).Distinct().ToList();}
        void SelectId(string id){if(pickingMode)pickingId=id;foreach(DataGridViewRow row in grid.Rows)if(Convert.ToString(((DataRowView)row.DataBoundItem)["Id"])==id){grid.ClearSelection();grid.CurrentCell=row.Cells[1];row.Cells[1].Selected=true;break;}}
        void Commit(LineupProject next,string selected,string message){
            Ensure();LineupTableData.RejectDuplicates(next.Records);var before=Snapshot();next.Save(store);undo.Push(before);project=next;dirty=false;Reload(selected);UpdateStatus(message);LearnAfterSave();
        }
        void UpdateStatus(string message){RefreshPickingLabel();status.Text=message+" · "+project.NumberPrefix+" Y"+project.ActiveIsland+" · 共 "+project.Records.Count+" 条，未编号 "+project.Records.Count(r=>string.IsNullOrWhiteSpace(r.Number))+" 条，未绑定模型 "+project.Records.Count(r=>string.IsNullOrEmpty(r.ReferenceKey))+" 条。";}
        internal void SaveDraft(){Ensure();var next=Snapshot();var changed=next.Records.Where(r=>string.IsNullOrWhiteSpace(r.Number)&&!string.IsNullOrWhiteSpace(r.Model)&&project.Records.Any(old=>old.Id==r.Id&&old.Model!=r.Model)&&(r.Category=="普通传感器"||r.Category=="其他电气件"||((r.Category=="气缸"||r.Category=="阀片")&&!string.IsNullOrWhiteSpace(next.NumberPrefix)))).ToList();LineupNumbering.FillMissing(next,changed);foreach(var valve in next.Records.Where(r=>r.Category=="阀片"&&project.Records.Any(old=>old.Id==r.Id&&old.Number!=r.Number)))LineupHierarchy.RenumberChildren(next,valve);foreach(var cylinder in next.Records.Where(r=>r.Category=="气缸"&&project.Records.Any(old=>old.Id==r.Id&&old.Number!=r.Number)))foreach(var sensor in next.Records.Where(r=>r.CylinderId==cylinder.Id))sensor.Number=cylinder.Number+sensor.Position;LineupTableData.RejectDuplicates(next.Records);if(dirty){next.Save(store);project=next;dirty=false;foreach(System.Data.DataRow row in data.Rows){var record=project.Records.First(r=>r.Id==(string)row["Id"]);row["编号"]=record.Number;row["阀岛型号"]=LineupNumbering.IslandModel(project,record);row["上级"]=LineupHierarchy.Parent(project,record);}UpdateStatus("已保存");LearnAfterSave();}else UpdateStatus("已保存");}
        internal void ImportText(string text,char separator){
            Ensure();var batch=LineupTableData.Parse(text,ActiveCategory,separator);if(batch.Records.Count==0)throw new InvalidOperationException("没有找到可导入的数据行。");
            var next=Snapshot();next.Records.AddRange(batch.Records);foreach(var section in batch.Sections){next.Sections.RemoveAll(s=>s.Category==section.Category);next.Sections.Add(section);}
            Commit(next,null,"已追加 "+batch.Records.Count+" 条");category.SelectedIndex=0;
        }
        internal void AddEmpty(){if(ActiveCategory=="气缸传感器"){GenerateSensorPairs();return;}if(!ReadyNumbering(ActiveCategory))return;var next=Snapshot();var r=new LineupRecord{Category=ActiveCategory,Number=LineupNumbering.Next(next,ActiveCategory)};next.Records.Add(r);Commit(next,r.Id,"已新增并自动编号");}
        internal int AddSelectedModels(){
            Ensure();if(ActiveCategory=="气缸传感器")throw new InvalidOperationException("请先从气缸生成 H/W 记录，再按行绑定传感器模型。");if(!ReadyNumbering(ActiveCategory))return 0;if(assembly.SelectSet.Count==0)throw new InvalidOperationException("请先在 CAD 中多选模型实例。");
            var incoming=new List<LineupRecord>();
            for(int i=1;i<=assembly.SelectSet.Count;i++){var r=new LineupRecord{Category=ActiveCategory};LineupCad.CaptureOccurrence(assembly.SelectSet.Item(i),r);incoming.Add(r);}
            var next=Snapshot();var keys=new HashSet<string>(next.Records.Where(r=>!string.IsNullOrEmpty(r.ReferenceKey)).Select(r=>r.ReferenceKey));int added=0;string last=CurrentId();
            foreach(var r in incoming)if(keys.Add(r.ReferenceKey)){r.Number=LineupNumbering.Next(next,r.Category);next.Records.Add(r);last=r.Id;added++;}
            if(added>0)Commit(next,last,"已另建 "+added+" 条记录并绑定模型；重复选择已跳过");else UpdateStatus("所选模型均已有记录；未新建，请选择其他模型");return added;
        }
        void DuplicateRows(){var ids=SelectedIds();if(ids.Count==0)throw new InvalidOperationException("请先选择要复制的行。");var next=Snapshot();string last=null;foreach(var source in next.Records.Where(r=>ids.Contains(r.Id)).ToArray()){var r=LineupTableData.Copy(source);r.Id=Guid.NewGuid().ToString("N");r.Number="";r.Address="";r.ReferenceKey="";r.SourceFile="";r.InstancePath=new string[0];if(LineupTableData.Categories.Contains(r.Category)&&r.Category!="气缸传感器")r.Number=LineupNumbering.Next(next,r.Category);next.Records.Add(r);last=r.Id;}Commit(next,last,"已复制通用字段；编号、地址和模型绑定留空");}
        internal void DeleteRows(){
            var ids=SelectedIds();if(ids.Count==0)throw new InvalidOperationException("请先选择要删除的行。");var next=Snapshot();
            if(next.Records.Any(r=>!ids.Contains(r.Id)&&(ids.Contains(r.IslandId)||ids.Contains(r.ValveId)||ids.Contains(r.CylinderId))))throw new InvalidOperationException("选中记录仍被其他记录引用，请先解除关系。");
            next.Records.RemoveAll(r=>ids.Contains(r.Id));Commit(next,null,"已删除 "+ids.Count+" 行，可撤销");
        }
        internal void FillDown(){
            var cells=grid.SelectedCells.Cast<DataGridViewCell>().Where(c=>!c.ReadOnly).ToArray();
            if(cells.Length<2)throw new InvalidOperationException("选中同一列的多行单元格，再向下填充。");
            if(cells.Any(c=>c.ColumnIndex==1||c.ColumnIndex==2))throw new InvalidOperationException("向下填充不包含编号和地址，请只选择需要共用的字段。");
            var before=Snapshot();foreach(var group in cells.GroupBy(c=>c.ColumnIndex)){var ordered=group.OrderBy(c=>c.RowIndex).ToArray();foreach(var cell in ordered.Skip(1))cell.Value=ordered[0].Value;}
            dirty=true;try{SaveDraft();undo.Push(before);}catch{project=before;Reload(null);dirty=false;throw;}
        }
        internal void PasteCells(string text){
            if(grid.CurrentCell==null)throw new InvalidOperationException("先点击要粘贴的起始单元格。");
            var block=LineupTableData.ReadDelimited(text,'\t');int top=grid.CurrentCell.RowIndex,left=grid.CurrentCell.ColumnIndex;
            if(block.Count==0)return;if(top+block.Count>grid.Rows.Count||left<1||block.Any(r=>left+r.Length>12))throw new InvalidOperationException("粘贴区域超出已有业务单元格。新增整表请使用“粘贴整张清单”。");
            var before=Snapshot();for(int y=0;y<block.Count;y++)for(int x=0;x<block[y].Length;x++)grid.Rows[top+y].Cells[left+x].Value=block[y][x];
            dirty=true;try{SaveDraft();undo.Push(before);}catch{project=before;Reload(null);dirty=false;throw;}
        }
        internal void BindAndNext(){
            Ensure();string id=CurrentId();if(id==null)throw new InvalidOperationException("先选择清单中的目标行。");
            var next=Snapshot();var r=next.Records.Single(x=>x.Id==id);var binding=new LineupRecord();LineupCad.Capture(assembly,binding);
            var conflict=next.Records.FirstOrDefault(x=>x.Id!=id&&x.ReferenceKey==binding.ReferenceKey);
            if(conflict!=null)throw new InvalidOperationException("当前待绑定："+r.Number+"。所选模型已属于 "+conflict.Number+"，请选择下一个模型；本行未修改。");
            r.ReferenceKey=binding.ReferenceKey;r.SourceFile=binding.SourceFile;r.InstancePath=binding.InstancePath;
            var visible=grid.Rows.Cast<DataGridViewRow>().Select(row=>Convert.ToString(((DataRowView)row.DataBoundItem)["Id"])).ToList();int index=visible.IndexOf(id);string following=index+1<visible.Count?visible[index+1]:id;
            Commit(next,following,index+1<visible.Count?"已绑定 "+r.Number+"，已移至下一行":"已绑定 "+r.Number+"；已到最后一行");
            assembly.SelectSet.RemoveAll();
        }
        List<string[]> ExportRows(){SaveDraft();int empty=project.Records.Count(r=>LineupTableData.Cells(r).All(string.IsNullOrWhiteSpace));if(empty>0)throw new InvalidDataException("有 "+empty+" 行尚未填写任何清单字段，请先填写或删除这些空行再导出。未编号但有描述的记录可以导出。");return LineupTableData.Grouped(project);}
        internal void Export(string path){File.WriteAllText(path,LineupCsv.Write(ExportRows()),new UTF8Encoding(true));UpdateStatus("已导出全部类目 11 列清单");}
        internal void Undo(){StopContinuous();if(undo.Count==0)return;Ensure();var previous=undo.Peek();LineupTableData.RejectDuplicates(previous.Records);previous.Save(store);undo.Pop();project=previous;dirty=false;Reload(null);RefreshEntryParents();UpdateStatus("已撤销");}
        internal void Locate(){
            Ensure();var r=Snapshot().Records.FirstOrDefault(record=>record.Id==CurrentId());if(r==null)throw new InvalidOperationException("请选择一行。");
            ClearHighlight();object resolved=LineupCad.ResolveTarget(assembly,r);dynamic target=resolved;highlight=assembly.HighlightSets.Add();highlight.Color=0x00FFFF;highlight.AddItem(LineupCad.HighlightTarget(resolved));highlight.Draw();
            double x,y,z,u,v,w;target.Range(out x,out y,out z,out u,out v,out w);double p=Math.Max(Math.Max(u-x,v-y),w-z)*.15+.001;dynamic view=((dynamic)app.ActiveWindow).View;view.RangeZoomCamera(x-p,y-p,z-p,u+p,v+p,w+p);view.Update();status.Text="已定位："+target.Name;
        }
        void ClearHighlight(){if(highlight!=null){try{highlight.Delete();}catch{}highlight=null;}}
    }
}
