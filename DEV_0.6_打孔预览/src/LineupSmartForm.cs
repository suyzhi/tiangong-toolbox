using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
namespace TianGongCadSuite {
    public sealed partial class LineupTableForm {
        Label helpBar,pickingLabel;string pickingId;
        Panel pickingPanel;Control fullToolbar;Rectangle normalBounds;bool pickingMode,fullFieldView;
        string catalogFile,catalogError="";LineupModelCatalog catalog;
        void InitializeCatalog(string path){
            catalogFile=path??LineupModelCatalog.UserFile;
            try{string seed=Path.Combine(Path.GetDirectoryName(typeof(LineupTableForm).Assembly.Location),"lineup-models-seed.xml");catalog=LineupModelCatalog.Load(File.Exists(catalogFile)?catalogFile:seed);}
            catch(Exception ex){catalog=new LineupModelCatalog();catalogError="型号库加载失败："+ex.Message;}
        }
        Control BuildToolbar(){
            var container=new Panel{Dock=DockStyle.Top,Height=74};
            var filters=new FlowLayoutPanel{Dock=DockStyle.Top,Height=34,Padding=new Padding(5)};
            filters.Controls.Add(new Label{Text="类目",AutoSize=true,Margin=new Padding(3,5,2,0)});
            category.Items.Add("全部");category.Items.AddRange(LineupTableData.Categories);category.Items.Add("旧分类/辅助");category.SelectedIndex=1;filters.Controls.Add(category);
            filters.Controls.Add(new Label{Text="查找",AutoSize=true,Margin=new Padding(12,5,2,0)});filters.Controls.Add(search);
            var menu=new ToolStrip{Font=Font,Dock=DockStyle.Bottom,Height=34,GripStyle=ToolStripGripStyle.Hidden};
            var add=Drop(menu,"新增");Item(add,"新增记录（自动编号）",()=>AddEmpty());Item(add,"多选模型加入",()=>AddSelectedModels());Item(add,"复制选中行",DuplicateRows);Item(add,"从气缸生成 H/W 传感器",GenerateSensorPairs);
            var edit=Drop(menu,"编辑");Item(edit,"粘贴到当前单元格",()=>PasteCells(Clipboard.GetText()));Item(edit,"向下填充（不含编号/地址）",FillDown);Item(edit,"删除选中行",DeleteRows);Item(edit,"撤销",Undo);Item(edit,"保存",SaveDraft);
            Item(edit,"上方插入",()=>InsertAt(false));Item(edit,"下方插入",()=>InsertAt(true));Item(edit,"上移",()=>MoveRows(-1));Item(edit,"下移",()=>MoveRows(1));
            var numbering=Drop(menu,"编号与阀岛");Item(numbering,"管理阀岛 / 设置新增记录的阀岛",()=>ShowNumberSettings());Item(numbering,"将选中阀片分配到阀岛…",AssignIslandDialog);Item(numbering,"给选中空编号行自动编号",NumberSelected);Item(numbering,"从选中气缸生成 H/W",GenerateSensorPairs);
            Item(numbering,"气缸归属阀片",AssignCylinderDialog);Item(numbering,"查看层级",ShowHierarchy);
            var models=Drop(menu,"型号库");Item(models,"选择型号（F4 / 双击型号）",()=>PickModel(false));Item(models,"套用型号到选中多行",()=>PickModel(true));Item(models,"导入型号库 / 从清单提取",ImportLibrary);Item(models,"将当前清单的完整型号收录入库",()=>{SaveDraft();int n=LearnModels(project.Records);status.Text="型号库新增 "+n+" 条，现有 "+catalog.Entries.Count+" 条。";});Item(models,"导出型号库 CSV",ExportLibrary);
            var files=Drop(menu,"导入导出");Item(files,"粘贴整张五类清单",()=>ImportText(Clipboard.GetText(),'\t'));Item(files,"导入清单 TSV / CSV",()=>{using(var d=new OpenFileDialog{Filter="表格文本|*.tsv;*.txt;*.csv"})if(d.ShowDialog(this)==DialogResult.OK)ImportText(File.ReadAllText(d.FileName,System.Text.Encoding.UTF8),Path.GetExtension(d.FileName).Equals(".csv",StringComparison.OrdinalIgnoreCase)?',':'\t');});
            Item(files,"复制五类清单到 WPS / Excel",()=>{Clipboard.SetText(LineupTableData.Tsv(ExportRows()));status.Text="已复制全部五类 11 列，在 WPS/Excel 的 A1 粘贴。";});
            Item(files,"导出五类 CSV",()=>{using(var d=new SaveFileDialog{Filter="CSV 清单|*.csv",FileName=Path.GetFileNameWithoutExtension(file)+"-Lineup.csv"})if(d.ShowDialog(this)==DialogResult.OK)Export(d.FileName);});
            Item(files,"复制参考BOM（10列）",()=>{ExportRows();Clipboard.SetText(LineupTableData.Tsv(LineupTableData.ReferenceGrouped(project)));status.Text="已复制参考BOM 10列；机构名称仍保存在插件中。";});
            Item(files,"导出位置图",ExportLocationDialog);
            var view=Drop(menu,"视图");Item(view,"紧凑模型点选模式",()=>SetPickingMode(true));Item(view,"定位当前行模型",Locate);Item(view,"清除高亮",ClearHighlight);Item(view,"常用字段（编号/描述/型号等）",()=>{fullFieldView=false;ApplyFieldVisibility();});Item(view,"完整字段（含地址/参数/安装等）",()=>{fullFieldView=true;ApplyFieldVisibility();});
            menu.Items.Add(new ToolStripSeparator());Quick(menu,"连续绑定",()=>StartContinuous("bind"));Quick(menu,"绑定下一行",BindAndNext);Quick(menu,"点选模型",()=>SetPickingMode(true));Quick(menu,"撤销",Undo);
            container.Controls.Add(filters);container.Controls.Add(menu);return container;
        }
        ToolStripDropDownButton Drop(ToolStrip strip,string title){var item=new ToolStripDropDownButton(title);strip.Items.Add(item);return item;}
        void Item(ToolStripDropDownButton menu,string title,Action action){var item=new ToolStripMenuItem(title);item.Click+=(s,e)=>Try(action);menu.DropDownItems.Add(item);}
        void Quick(ToolStrip menu,string title,Action action){var button=new ToolStripButton(title);button.Click+=(s,e)=>Try(action);menu.Items.Add(button);}
        internal void ConfigureNumbering(string prefix,int island,string model){var next=Snapshot();LineupNumbering.SetIsland(next,prefix,island,model);Commit(next,CurrentId(),"编号与阀岛设置已保存");if(entryPrefix!=null){entryPrefix.Text=prefix;entryIsland.Value=Math.Min(9999,island);RefreshEntryParents();}}
        internal void AssignValvesToIsland(string key,IEnumerable<string> ids){
            var next=Snapshot();var target=next.Islands.FirstOrDefault(i=>i.Key==key);if(target==null)throw new InvalidOperationException("请先在管理阀岛中建立目标阀岛。");
            var selected=next.Records.Where(r=>ids.Contains(r.Id)).ToArray();if(selected.Length==0||selected.Any(r=>r.Category!="阀片"))throw new InvalidOperationException("请只选择需要分配的阀片行。");
            var match=System.Text.RegularExpressions.Regex.Match(key,@"^(.+)-Y([0-9]+)$");if(!match.Success)throw new InvalidOperationException("阀岛编号无效。");
            var owner=next.Records.FirstOrDefault(x=>x.Category=="阀岛"&&x.Number==key);if(owner==null){owner=new LineupRecord{Category="阀岛",Number=key,Model=target.Model};next.Records.Add(owner);}
            string prefix=next.NumberPrefix;int active=next.ActiveIsland;next.NumberPrefix=match.Groups[1].Value;next.ActiveIsland=int.Parse(match.Groups[2].Value);
            foreach(var r in selected){if(!string.Equals(LineupNumbering.IslandKey(r.Number),key,StringComparison.OrdinalIgnoreCase))r.Number=LineupNumbering.Next(next,"阀片");r.IslandId=owner.Id;LineupHierarchy.RenumberChildren(next,r);}
            next.NumberPrefix=prefix;next.ActiveIsland=active;Commit(next,CurrentId(),"选中阀片已分配到 "+key+"，编号按目标岛接续；可撤销");
        }
        void AssignIslandDialog(){
            var ids=SelectedIds();if(ids.Count==0||Snapshot().Records.Where(r=>ids.Contains(r.Id)).Any(r=>r.Category!="阀片"))throw new InvalidOperationException("请先选择阀片行，可多选。");
            if(project.Islands.Count==0)throw new InvalidOperationException("请先通过“管理阀岛”设置各 Y 阀岛及其型号。");
            using(var d=new Form{Text="分配选中阀片的所属阀岛",Width=520,Height=220,StartPosition=FormStartPosition.CenterParent,Font=Font}){
                var combo=new ComboBox{Dock=DockStyle.Top,DropDownStyle=ComboBoxStyle.DropDownList};foreach(var island in project.Islands)combo.Items.Add(island.Key+" · "+island.Model);combo.SelectedIndex=0;
                var note=new Label{Dock=DockStyle.Fill,Text="阀片及其下属气缸、传感器联动更新。编号将按目标阀岛接续生成，型号取该岛设置。\n零件绑定及阀片自身型号保留；操作可撤销。",Padding=new Padding(8)};
                var ok=new Button{Text="分配选中阀片",Dock=DockStyle.Bottom,Height=34,DialogResult=DialogResult.OK};d.Controls.Add(note);d.Controls.Add(combo);d.Controls.Add(ok);d.AcceptButton=ok;
                if(d.ShowDialog(this)==DialogResult.OK)AssignValvesToIsland(project.Islands[combo.SelectedIndex].Key,ids);
            }
        }
        bool ReadyNumbering(string kind){if(kind!="气缸"&&kind!="阀片")return true;return !string.IsNullOrWhiteSpace(project.NumberPrefix)||ShowNumberSettings();}
        bool ShowNumberSettings(){
            using(var dialog=new Form{Text="编号与阀岛设置",Font=Font,Width=520,Height=270,StartPosition=FormStartPosition.CenterParent,FormBorderStyle=FormBorderStyle.FixedDialog,MaximizeBox=false,MinimizeBox=false}){
                var layout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,Padding=new Padding(12)};
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,140));layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
                var prefix=new TextBox{Text=project.NumberPrefix,Dock=DockStyle.Fill};var island=new NumericUpDown{Minimum=1,Maximum=9999,Value=Math.Max(1,project.ActiveIsland),Dock=DockStyle.Fill};
                var model=new TextBox{Dock=DockStyle.Fill};
                Action update=()=>{var key=prefix.Text.Trim()+"-Y"+island.Value;var entry=(project.Islands??new List<LineupIsland>()).FirstOrDefault(i=>string.Equals(i.Key,key,StringComparison.OrdinalIgnoreCase));model.Text=entry==null?"":entry.Model;};
                prefix.TextChanged+=(s,e)=>update();island.ValueChanged+=(s,e)=>update();update();
                layout.Controls.Add(new Label{Text="项目号 / 编号前缀",AutoSize=true},0,0);layout.Controls.Add(prefix,1,0);
                layout.Controls.Add(new Label{Text="阀岛数字",AutoSize=true},0,1);layout.Controls.Add(island,1,1);
                layout.Controls.Add(new Label{Text="当前 Y 阀岛的型号",AutoSize=true},0,2);layout.Controls.Add(model,1,2);
                var note=new Label{Text="例：OK170 + Y1 → OK170-Y1C01 / OK170-Y1V01。\n只影响新增或空编号；已有手填编号不会重排。\n同一项目、同一 Y 数字的阀片共用这里的阀岛型号。",AutoSize=true};layout.Controls.Add(note,0,3);layout.SetColumnSpan(note,2);
                var ok=new Button{Text="保存设置",DialogResult=DialogResult.OK,AutoSize=true};layout.Controls.Add(ok,1,4);dialog.AcceptButton=ok;dialog.Controls.Add(layout);
                if(dialog.ShowDialog(this)!=DialogResult.OK)return false;ConfigureNumbering(prefix.Text,(int)island.Value,model.Text);return true;
            }
        }
        void NumberSelected(){
            var ids=SelectedIds();if(ids.Count==0)throw new InvalidOperationException("请选择需要编号的行。");var next=Snapshot();
            if(next.Records.Any(r=>ids.Contains(r.Id)&&(r.Category=="气缸"||r.Category=="阀片"))&&!ReadyNumbering("气缸"))return;
            next=Snapshot();LineupNumbering.FillMissing(next,next.Records.Where(r=>ids.Contains(r.Id)));Commit(next,CurrentId(),"已填充空编号，已有编号保持不变");
        }
        internal int GenerateSensorPairsFor(IEnumerable<string> ids){var next=Snapshot();int count=LineupNumbering.SensorPairs(next,ids);Commit(next,null,"已生成 "+count+" 条 H/W 传感器");category.SelectedItem="气缸传感器";return count;}
        void GenerateSensorPairs(){
            var p=Snapshot();var selected=SelectedIds();var ids=p.Records.Where(r=>r.Category=="气缸"&&selected.Contains(r.Id)).Select(r=>r.Id).ToList();
            if(ids.Count==0){
                var cylinders=p.Records.Where(r=>r.Category=="气缸"&&!string.IsNullOrWhiteSpace(r.Number)).ToList();if(cylinders.Count==0)throw new InvalidOperationException("请先录入并编号气缸。");
                using(var dialog=new Form{Text="选择气缸，生成 H/W",Font=Font,Width=540,Height=390,StartPosition=FormStartPosition.CenterParent}){
                    var list=new ListBox{Dock=DockStyle.Fill,SelectionMode=SelectionMode.MultiExtended};list.Items.AddRange(cylinders.Cast<object>().ToArray());
                    var ok=new Button{Text="生成所选气缸的 H/W",Dock=DockStyle.Bottom,Height=34,DialogResult=DialogResult.OK};dialog.Controls.Add(list);dialog.Controls.Add(ok);
                    if(dialog.ShowDialog(this)!=DialogResult.OK)return;ids=list.SelectedItems.Cast<LineupRecord>().Select(r=>r.Id).ToList();if(ids.Count==0)return;
                }
            }GenerateSensorPairsFor(ids);
        }
        void InitializePickingPanel(Control toolbar){
            fullToolbar=toolbar;pickingPanel=new Panel{Dock=DockStyle.Fill,Visible=false};pickingLabel=new Label{Dock=DockStyle.Top,Height=59,Padding=new Padding(6),AutoEllipsis=true};
            var actions=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=65};
            Button(actions,"绑定并下一条",BindAndNext);Button(actions,"另建记录",()=>AddSelectedModels());Button(actions,"返回表格",()=>SetPickingMode(false));
            watchStop=new Button{Text="停止连续",AutoSize=true,Visible=false};watchStop.Click+=(s,e)=>StopContinuous();actions.Controls.Add(watchStop);Button(actions,"撤销",Undo);
            pickingPanel.Controls.Add(pickingLabel);pickingPanel.Controls.Add(actions);Controls.Add(pickingPanel);
            grid.CurrentCellChanged+=(s,e)=>RefreshPickingLabel();
        }
        internal void ApplyFieldVisibility(){if(grid.Columns.Count<14)return;foreach(DataGridViewColumn column in grid.Columns)column.Visible=column.Name=="上级"||fullFieldView||new[]{0,1,4,5,6,7,8}.Contains(column.Index)||(column.Index==10&&(category.SelectedIndex==0||ActiveCategory=="普通传感器"||ActiveCategory=="气缸传感器"))||(column.Name=="阀岛型号"&&ActiveCategory=="阀片");grid.Columns[7].DisplayIndex=2;grid.Columns[8].DisplayIndex=3;}
        internal bool PickingMode {get{return pickingMode;}}
        internal string BindingRecordId {get{return CurrentId();}}
        internal void SetPickingMode(bool value){
            if(value==pickingMode)return;grid.EndEdit();if(value)pickingId=CurrentId();pickingMode=value;
            if(value){normalBounds=Bounds;fullToolbar.Visible=false;helpBar.Visible=false;if(entryPanel!=null)entryPanel.Visible=false;grid.Visible=false;status.Visible=false;MinimumSize=new Size(400,140);Size=new Size(440,180);var area=Screen.FromControl(this).WorkingArea;Location=new Point(Math.Max(area.Left,area.Right-Width-20),Math.Max(area.Top,area.Bottom-Height-60));pickingPanel.Visible=true;pickingPanel.BringToFront();}
            else{StopContinuous();pickingPanel.Visible=false;MinimumSize=new Size(1040,520);Bounds=normalBounds;fullToolbar.Visible=true;helpBar.Visible=true;if(entryPanel!=null)entryPanel.Visible=true;grid.Visible=true;status.Visible=true;}
            if(!value&&pickingId!=null)SelectId(pickingId);RefreshPickingLabel();
        }
        void RefreshPickingLabel(){if(pickingLabel==null)return;var id=CurrentId();var r=project.Records.FirstOrDefault(x=>x.Id==id);pickingLabel.Text=r==null?"先在表格选择待绑定行，或选择模型后点“另建记录”。":"当前："+r.Number+"（"+(string.IsNullOrEmpty(r.ReferenceKey)?"待绑定":"已绑定")+"）\n"+(string.IsNullOrEmpty(r.ReferenceKey)?"请选择对应模型，再点“绑定并下一条”。":"可换绑并前进；新增清单行请点“另建记录”。");}
        void SetupModelEditing(){
            grid.EditingControlShowing+=(s,e)=>{var box=e.Control as TextBox;if(box==null)return;box.AutoCompleteMode=AutoCompleteMode.None;box.AutoCompleteSource=AutoCompleteSource.None;if(grid.CurrentCell!=null&&grid.CurrentCell.ColumnIndex==5){var source=new AutoCompleteStringCollection();string kind=Convert.ToString(grid.CurrentRow.Cells[0].Value);source.AddRange(catalog.Entries.Where(m=>m.Category==kind).Select(m=>m.Model).Distinct().ToArray());box.AutoCompleteCustomSource=source;box.AutoCompleteSource=AutoCompleteSource.CustomSource;box.AutoCompleteMode=AutoCompleteMode.SuggestAppend;}};
            grid.CellDoubleClick+=(s,e)=>{if(e.RowIndex>=0&&e.ColumnIndex==5)Try(()=>PickModel(false));};
            KeyPreview=true;KeyDown+=(s,e)=>{if(e.KeyCode==Keys.F4){e.SuppressKeyPress=true;Try(()=>{if(entryPanel!=null&&entryPanel.ContainsFocus)PickEntryModel();else PickModel(false);});}if(e.Control&&e.Shift&&e.KeyCode==Keys.P){e.SuppressKeyPress=true;Try(()=>SetPickingMode(!pickingMode));}};
        }
        void CompleteKnownModel(int row){
            string kind=Convert.ToString(grid.Rows[row].Cells[0].Value),model=Convert.ToString(grid.Rows[row].Cells[5].Value);
            var matches=catalog.Match(kind,model).ToList();grid.Rows[row].Cells[5].ErrorText="";if(matches.Count!=1){if(matches.Count>1)grid.Rows[row].Cells[5].ErrorText="同型号有多个品牌/接口规格，请按 F4 选择。";return;}var entry=matches[0];
            string brand=Convert.ToString(grid.Rows[row].Cells[6].Value),connector=Convert.ToString(grid.Rows[row].Cells[10].Value);
            if((brand.Length==0||brand==entry.Brand)&&(connector.Length==0||connector==entry.Connector)){grid.Rows[row].Cells[6].Value=entry.Brand;grid.Rows[row].Cells[10].Value=entry.Connector;}else grid.Rows[row].Cells[5].ErrorText="品牌或接口与型号库不同，保留手填值。按 F4 可选择库中规格。";
        }
        internal void ApplyModel(LineupModelEntry entry,IEnumerable<string> ids){
            var selected=ids.ToList();var next=Snapshot();foreach(var r in next.Records.Where(r=>selected.Contains(r.Id))){if(r.Category!=entry.Category)throw new InvalidOperationException("所选行类别与型号库类别不同，请分别选择。");r.Model=entry.Model;r.Brand=entry.Brand;r.Connector=entry.Connector;}
            if(next.Records.Any(r=>selected.Contains(r.Id)&&string.IsNullOrWhiteSpace(r.Number)&&(r.Category=="气缸"||r.Category=="阀片"))&&!ReadyNumbering("气缸"))return;
            next.NumberPrefix=project.NumberPrefix;next.ActiveIsland=project.ActiveIsland;next.Islands=project.Islands.Select(i=>new LineupIsland{Key=i.Key,Model=i.Model}).ToList();
            LineupNumbering.FillMissing(next,next.Records.Where(r=>selected.Contains(r.Id)));Commit(next,CurrentId(),"已套用型号、品牌和接口；空编号已生成");
        }
        void PickModel(bool multiple){
            if(catalogError.Length>0)throw new InvalidOperationException(catalogError);
            var id=CurrentId();if(id==null)throw new InvalidOperationException("请先选择一行。");var ids=multiple?SelectedIds():new List<string>{id};string kind=Snapshot().Records.Single(r=>r.Id==id).Category;
            var source=catalog.Entries.Where(e=>e.Category==kind).ToList();
            using(var dialog=new Form{Text="型号库："+kind,Font=Font,Width=690,Height=420,StartPosition=FormStartPosition.CenterParent}){
                var query=new TextBox{Dock=DockStyle.Top};var list=new ListBox{Dock=DockStyle.Fill,HorizontalScrollbar=true};
                Action filter=()=>{list.Items.Clear();list.Items.AddRange(source.Where(e=>e.ToString().IndexOf(query.Text,StringComparison.OrdinalIgnoreCase)>=0).Cast<object>().ToArray());};query.TextChanged+=(s,e)=>filter();filter();
                var info=new Label{Text="搜索型号、品牌或接口。不同品牌/接口以独立规格保留。也可直接在表格中手工输入新型号。",Dock=DockStyle.Top,Height=32};
                var ok=new Button{Text="套用型号、品牌、接口",Dock=DockStyle.Bottom,Height=34,DialogResult=DialogResult.OK};list.DoubleClick+=(s,e)=>{if(list.SelectedItem!=null)dialog.DialogResult=DialogResult.OK;};
                dialog.Controls.Add(list);dialog.Controls.Add(query);dialog.Controls.Add(info);dialog.Controls.Add(ok);if(dialog.ShowDialog(this)!=DialogResult.OK||list.SelectedItem==null)return;ApplyModel((LineupModelEntry)list.SelectedItem,ids);
            }
        }
        int LearnModels(IEnumerable<LineupRecord> records){
            if(catalogError.Length>0)throw new InvalidOperationException(catalogError);
            using(var mutex=new System.Threading.Mutex(false,"Local\\TianGongLineupModelLibrary")){
                bool locked=false;try{try{locked=mutex.WaitOne(2000);}catch(System.Threading.AbandonedMutexException){locked=true;}if(!locked)throw new IOException("型号库正被另一窗口更新，请稍后重试。");
                    var latest=LineupModelCatalog.Load(catalogFile);int previous=latest.Entries.Count;latest.Merge(catalog);int count=latest.Learn(records);if(count>0||!File.Exists(catalogFile)||latest.Entries.Count!=previous)latest.Save(catalogFile);catalog=latest;return count;
                }finally{if(locked)mutex.ReleaseMutex();}
            }
        }
        void LearnAfterSave(){try{LearnModels(project.Records);}catch(Exception ex){status.Text="清单已保存；型号库未更新："+ex.Message;}}
        void ImportLibrary(){
            using(var dialog=new OpenFileDialog{Filter="型号库 / 清单|*.csv;*.tsv;*.txt;*.xml"})if(dialog.ShowDialog(this)==DialogResult.OK){
                string ext=Path.GetExtension(dialog.FileName).ToLowerInvariant();var imported=ext==".xml"?LineupModelCatalog.Load(dialog.FileName):LineupModelCatalog.Import(File.ReadAllText(dialog.FileName,System.Text.Encoding.UTF8),ext==".csv"?',':'\t',ActiveCategory);
                int count=LearnModels(imported.Entries.Select(e=>new LineupRecord{Category=e.Category,Model=e.Model,Brand=e.Brand,Connector=e.Connector}));status.Text="型号库新增 "+count+" 条，现有 "+catalog.Entries.Count+" 条。";
            }
        }
        void ExportLibrary(){using(var d=new SaveFileDialog{Filter="型号库 CSV|*.csv",FileName="Lineup-型号库.csv"})if(d.ShowDialog(this)==DialogResult.OK){var rows=new List<string[]>{new[]{"类别","型号","品牌","传感器接口类型"}};rows.AddRange(catalog.Entries.Select(e=>new[]{e.Category,e.Model,e.Brand,e.Connector}));File.WriteAllText(d.FileName,LineupCsv.Write(rows),new System.Text.UTF8Encoding(true));status.Text="型号库已导出。";}}
    }
}
