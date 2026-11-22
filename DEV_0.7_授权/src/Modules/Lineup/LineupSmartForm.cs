using System;
using System.Collections.Generic;
using System.Diagnostics;
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
            try{
                string seedPath=Path.Combine(Path.GetDirectoryName(typeof(LineupTableForm).Assembly.Location),"lineup-models-seed.xml");
                var seed=LineupModelCatalog.LoadSeed(seedPath);
                if(File.Exists(catalogFile)){catalog=LineupModelCatalog.Load(catalogFile);if(path==null)catalog.Merge(seed);}
                else catalog=path==null?seed:new LineupModelCatalog();
            }
            catch(Exception ex){catalog=new LineupModelCatalog();catalogError="型号库加载失败："+ex.Message;}
        }
        Control BuildToolbar(){
            // 0.9.0：类目、查找挪到左侧导航（BuildNav），这里只剩一排菜单
            var container=new Panel{Dock=DockStyle.Top,Height=Ui.S(46),BackColor=Ui.Bg};
            category.Items.Add("全部");category.Items.AddRange(LineupTableData.Categories);category.Items.Add("旧分类/辅助");category.SelectedIndex=1;
            var menu=new ToolStrip{Font=Ui.F9,Dock=DockStyle.Fill,GripStyle=ToolStripGripStyle.Hidden,BackColor=Ui.Bg,Padding=Ui.SPad(12,6,12,6),Renderer=new FlatMenuRenderer()};
            var add=Drop(menu,"新增");
            Item(add,"从当前装配自动识别气缸/阀/传感器…",ScanAssemblyDialog);
            add.DropDownItems.Add(new ToolStripSeparator());
            Item(add,"新增记录（自动编号）",()=>AddEmpty());Item(add,"多选模型加入",()=>AddSelectedModels());Item(add,"复制选中行",DuplicateRows);Item(add,"从气缸生成 H/W 传感器",GenerateSensorPairs);
            var edit=Drop(menu,"编辑");Item(edit,"粘贴到当前单元格",()=>PasteCells(Clipboard.GetText()));Item(edit,"向下填充（不含编号/地址）",FillDown);Item(edit,"批量设置备注 / 模块名…",BatchSetNotesDialog);Item(edit,"删除选中行",DeleteRows);Item(edit,"撤销",Undo);Item(edit,"保存",SaveDraft);
            Item(edit,"上方插入",()=>InsertAt(false));Item(edit,"下方插入",()=>InsertAt(true));Item(edit,"上移",()=>MoveRows(-1));Item(edit,"下移",()=>MoveRows(1));
            var numbering=Drop(menu,"编号与阀岛");
            Item(numbering,"管理阀岛 / 设置项目抬头与控制方式…",()=>ShowNumberSettings());
            Item(numbering,"将选中阀片分配到阀岛…",AssignIslandDialog);
            Item(numbering,"给选中空编号行自动编号",NumberSelected);
            numbering.DropDownItems.Add(new ToolStripSeparator());
            Item(numbering,"从选中气缸一键生成配套阀片",GenerateValvesForSelected);
            Item(numbering,"从选中气缸生成 H/W",GenerateSensorPairs);
            Item(numbering,"从选中气缸生成双组传感器 (H1/W1/H2/W2)",GenerateMultiSensorsForSelected);
            Item(numbering,"气缸归属阀片",AssignCylinderDialog);
            Item(numbering,"同步气缸备注到传感器",SyncSensorNotes);
            Item(numbering,"查看层级",ShowHierarchy);
            var models=Drop(menu,"型号库");Item(models,"选择型号（F4 / 双击型号）",()=>PickModel(false));Item(models,"套用型号到选中多行",()=>PickModel(true));Item(models,"导入型号库 / 从清单提取",ImportLibrary);Item(models,"将当前清单的完整型号收录入库",()=>{SaveDraft();int n=LearnModels(project.Records);status.Text="型号库新增 "+n+" 条，现有 "+catalog.Entries.Count+" 条。";});Item(models,"导出型号库 CSV",ExportLibrary);
            var files=Drop(menu,"导入导出");
            Item(files,"导出标准 Lineup 工作簿 (.xlsx)…",ExportXlsxDialog);
            Item(files,"CAD 位置图工作台（多页截图与箭头）…",OpenCadPageStudio);
            Item(files,"导入 Excel Lineup (.xlsx / .xlsm)…",ImportXlsxDialog);
            files.DropDownItems.Add(new ToolStripSeparator());
            Item(files,"粘贴整张五类清单",()=>ImportText(Clipboard.GetText(),'\t'));
            Item(files,"导入清单 TSV / CSV",()=>{using(var d=new OpenFileDialog{Filter="表格文本|*.tsv;*.txt;*.csv"})if(d.ShowDialog(this)==DialogResult.OK)ImportText(SettingsFile.ReadUserText(d.FileName),Path.GetExtension(d.FileName).Equals(".csv",StringComparison.OrdinalIgnoreCase)?',':'\t');});
            Item(files,"复制五类清单到 WPS / Excel",()=>{Clipboard.SetText(LineupTableData.Tsv(ExportRows()));status.Text="已复制全部五类 11 列，在 WPS/Excel 的 A1 粘贴。";});
            Item(files,"导出五类 CSV",()=>{using(var d=new SaveFileDialog{Filter="CSV 清单|*.csv",FileName=Path.GetFileNameWithoutExtension(file)+"-Lineup.csv"})if(d.ShowDialog(this)==DialogResult.OK)Export(d.FileName);});
            Item(files,"复制参考BOM（10列）",()=>{ExportRows();Clipboard.SetText(LineupTableData.Tsv(LineupTableData.ReferenceGrouped(project)));status.Text="已复制参考BOM 10列；机构名称仍保存在插件中。";});
            Item(files,"导出旧版位置图包 (PNG + XML)",ExportLocationDialog);
            var view=Drop(menu,"视图");Item(view,"显示 / 隐藏右侧 3D 预览",TogglePreview);Item(view,"紧凑模型点选模式",()=>SetPickingMode(true));Item(view,"定位当前行模型",Locate);Item(view,"清除高亮",ClearHighlight);Item(view,"常用字段（编号/描述/型号等）",()=>{fullFieldView=false;ApplyFieldVisibility();});Item(view,"完整字段（含地址/参数/安装等）",()=>{fullFieldView=true;ApplyFieldVisibility();});
            menu.Items.Add(new ToolStripSeparator());
            Quick(menu,"连续绑定",()=>StartContinuous("bind"));
            Quick(menu,"CAD 位置图",OpenCadPageStudio);
            Quick(menu,"规范自检",OpenAuditDialog);
            var expBtn=new ToolStripButton("导出 .xlsx"){ForeColor=Ui.Accent,Font=Ui.F9B};expBtn.Click+=(s,e)=>Try(ExportXlsxDialog);menu.Items.Add(expBtn);
            Quick(menu,"3D 预览",TogglePreview);
            Quick(menu,"撤销",Undo);
            container.Controls.Add(menu);container.Paint+=(s,e)=>{using(var p=new Pen(Ui.Line))e.Graphics.DrawLine(p,0,container.Height-1,container.Width,container.Height-1);};return container;
        }
        ToolStripDropDownButton Drop(ToolStrip strip,string title){var item=new ToolStripDropDownButton(title);strip.Items.Add(item);return item;}
        void Item(ToolStripDropDownButton menu,string title,Action action){var item=new ToolStripMenuItem(title);item.Click+=(s,e)=>Try(action);menu.DropDownItems.Add(item);}
        void Quick(ToolStrip menu,string title,Action action){var button=new ToolStripButton(title);button.Click+=(s,e)=>Try(action);menu.Items.Add(button);}
        internal void ConfigureNumbering(string prefix,int island,string model){ConfigureNumbering(prefix,island,model,null);}
        internal void ConfigureNumbering(string prefix,int island,string model,string kind){
            var next=Snapshot();LineupNumbering.SetIsland(next,prefix,island,model,kind);
            if(string.IsNullOrWhiteSpace(next.StationCode))next.StationCode=prefix.Trim();
            Commit(next,CurrentId(),"编号与阀岛设置已保存");
            if(entryPrefix!=null){entryPrefix.Text=prefix;entryIsland.Value=Math.Min(9999,island);RefreshEntryParents();}
        }
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
            using(var d=new Form{Text="分配选中阀片的所属阀岛",Width=Ui.S(520),Height=Ui.S(230),StartPosition=FormStartPosition.CenterParent,Font=Font}){
                var combo=new ComboBox{Dock=DockStyle.Top,DropDownStyle=ComboBoxStyle.DropDownList};foreach(var island in project.Islands)combo.Items.Add(island.Key+" · "+(string.IsNullOrWhiteSpace(island.Kind)?"阀岛":island.Kind)+" · "+island.Model);combo.SelectedIndex=0;
                var note=new Label{Dock=DockStyle.Fill,Text="阀片及其下属气缸、传感器联动更新。编号将按目标阀岛接续生成，型号取该岛设置。\n零件绑定及阀片自身型号保留；操作可撤销。",Padding=new Padding(8)};
                var ok=new Button{Text="分配选中阀片",Dock=DockStyle.Bottom,Height=Ui.S(34),DialogResult=DialogResult.OK};d.Controls.Add(note);d.Controls.Add(combo);d.Controls.Add(ok);d.AcceptButton=ok;
                if(d.ShowDialog(this)==DialogResult.OK)AssignValvesToIsland(project.Islands[combo.SelectedIndex].Key,ids);
            }
        }
        bool ReadyNumbering(string kind){if(kind!="气缸"&&kind!="阀片")return true;return !string.IsNullOrWhiteSpace(project.NumberPrefix)||ShowNumberSettings();}
        bool ShowNumberSettings(){
            using(var dialog=new Form{Text="编号、阀岛与项目抬头设置",Font=Font,Width=Ui.S(560),Height=Ui.S(420),StartPosition=FormStartPosition.CenterParent,FormBorderStyle=FormBorderStyle.FixedDialog,MaximizeBox=false,MinimizeBox=false}){
                var layout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,Padding=new Padding(14)};
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,Ui.S(148)));layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
                var prefix=new TextBox{Text=project.NumberPrefix,Dock=DockStyle.Fill};
                var island=new NumericUpDown{Minimum=1,Maximum=9999,Value=Math.Max(1,project.ActiveIsland),Dock=DockStyle.Fill};
                var kindBox=new ComboBox{Dock=DockStyle.Fill,DropDownStyle=ComboBoxStyle.DropDownList};kindBox.Items.AddRange(LineupPresets.ValveKinds.Cast<object>().ToArray());kindBox.SelectedIndex=0;
                var model=new ComboBox{Dock=DockStyle.Fill,DropDownStyle=ComboBoxStyle.DropDown,AutoCompleteMode=AutoCompleteMode.SuggestAppend,AutoCompleteSource=AutoCompleteSource.ListItems};
                model.Items.AddRange(catalog.Entries.Where(x=>x.Category=="阀岛").Select(x=>x.Model).Distinct().OrderBy(x=>x).Cast<object>().ToArray());
                var projCode=new TextBox{Text=project.ProjectCode??"",Dock=DockStyle.Fill};
                var lineCode=new TextBox{Text=string.IsNullOrWhiteSpace(project.LineCode)?"L1":project.LineCode,Dock=DockStyle.Fill};
                var designer=new TextBox{Text=project.Designer??"",Dock=DockStyle.Fill};
                var editor=new TextBox{Text=project.Editor??"",Dock=DockStyle.Fill};
                Action update=()=>{
                    var key=prefix.Text.Trim()+"-Y"+island.Value;
                    var entry=(project.Islands??new List<LineupIsland>()).FirstOrDefault(i=>string.Equals(i.Key,key,StringComparison.OrdinalIgnoreCase));
                    model.Text=entry==null?"":entry.Model;
                    string k=entry==null||string.IsNullOrWhiteSpace(entry.Kind)?"阀岛":entry.Kind;
                    int idx=kindBox.Items.IndexOf(k);kindBox.SelectedIndex=idx>=0?idx:0;
                };
                prefix.TextChanged+=(s,e)=>update();island.ValueChanged+=(s,e)=>update();update();
                Action<int,string,Control> row=(r,l,c)=>{layout.Controls.Add(new Label{Text=l,AutoSize=true,Anchor=AnchorStyles.Left},0,r);layout.Controls.Add(c,1,r);};
                row(0,"工位号 / 编号前缀",prefix);
                row(1,"阀岛数字 (Y*)",island);
                row(2,"控制方式",kindBox);
                row(3,"当前 Y 阀岛/汇流排型号",model);
                row(4,"项目编号（如 SK26.01）",projCode);
                row(5,"线体号（如 L1）",lineCode);
                row(6,"机械设计人",designer);
                row(7,"Lineup 编辑人",editor);
                var note=new Label{Text="例：OK170 + Y1 → OK170-Y1C01 / OK170-Y1V01。\n同一 Y 阀岛下的阀片在导出 Excel 时将自动在安装方式列纵向合并标注。",AutoSize=true,ForeColor=Ui.Muted};layout.Controls.Add(note,0,8);layout.SetColumnSpan(note,2);
                var ok=Ui.Primary("保存设置",110,32);ok.DialogResult=DialogResult.OK;layout.Controls.Add(ok,1,9);dialog.AcceptButton=ok;dialog.Controls.Add(layout);
                if(dialog.ShowDialog(this)!=DialogResult.OK)return false;
                var next=Snapshot();
                next.ProjectCode=projCode.Text.Trim();
                next.StationCode=prefix.Text.Trim();
                next.LineCode=string.IsNullOrWhiteSpace(lineCode.Text)?"L1":lineCode.Text.Trim();
                next.Designer=designer.Text.Trim();
                next.Editor=editor.Text.Trim();
                LineupNumbering.SetIsland(next,prefix.Text,(int)island.Value,model.Text,Convert.ToString(kindBox.SelectedItem));
                Commit(next,CurrentId(),"编号、阀岛与项目抬头设置已保存");
                if(entryPrefix!=null){entryPrefix.Text=prefix.Text.Trim();entryIsland.Value=Math.Min(9999,island.Value);RefreshEntryParents();}
                return true;
            }
        }
        void NumberSelected(){
            var ids=SelectedIds();if(ids.Count==0)throw new InvalidOperationException("请选择需要编号的行。");var next=Snapshot();
            if(next.Records.Any(r=>ids.Contains(r.Id)&&(r.Category=="气缸"||r.Category=="阀片"))&&!ReadyNumbering("气缸"))return;
            next=Snapshot();LineupNumbering.FillMissing(next,next.Records.Where(r=>ids.Contains(r.Id)));Commit(next,CurrentId(),"已填充空编号，已有编号保持不变");
        }
        internal int GenerateValvesFor(IEnumerable<string> cylinderIds){
            var next=Snapshot();int added=LineupNumbering.GenerateValves(next,cylinderIds);
            Commit(next,null,"已自动生成 "+added+" 条配套阀片并建立关联");
            if(added>0)category.SelectedItem="阀片";
            return added;
        }
        void GenerateValvesForSelected(){
            var p=Snapshot();var selected=SelectedIds();
            var ids=p.Records.Where(r=>r.Category=="气缸"&&selected.Contains(r.Id)&&!string.IsNullOrWhiteSpace(r.Number)).Select(r=>r.Id).ToList();
            if(ids.Count==0){
                ids=p.Records.Where(r=>r.Category=="气缸"&&!string.IsNullOrWhiteSpace(r.Number)).Select(r=>r.Id).ToList();
                if(ids.Count==0)throw new InvalidOperationException("请先录入并编号气缸。");
            }
            GenerateValvesFor(ids);
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
        void GenerateMultiSensorsForSelected(){
            var p=Snapshot();var selected=SelectedIds();
            var ids=p.Records.Where(r=>r.Category=="气缸"&&selected.Contains(r.Id)&&!string.IsNullOrWhiteSpace(r.Number)).Select(r=>r.Id).ToList();
            if(ids.Count==0)throw new InvalidOperationException("请先选中一个或多个已编号的气缸行。");
            var next=Snapshot();int count=LineupNumbering.SensorPairsMulti(next,ids,2);
            Commit(next,null,"已生成 "+count+" 条双组传感器 (H1/W1/H2/W2)");category.SelectedItem="气缸传感器";
        }
        void SyncSensorNotes(){
            var next=Snapshot();
            var byId=next.Records.ToDictionary(r=>r.Id);
            var byNum=new Dictionary<string,LineupRecord>(StringComparer.OrdinalIgnoreCase);
            foreach(var c in next.Records.Where(r=>r.Category=="气缸"&&!string.IsNullOrWhiteSpace(r.Number)))if(!byNum.ContainsKey(c.Number.Trim()))byNum[c.Number.Trim()]=c;
            int updated=0;
            foreach(var s in next.Records.Where(r=>r.Category=="气缸传感器")){
                LineupRecord parent=null;
                if(!string.IsNullOrEmpty(s.CylinderId))byId.TryGetValue(s.CylinderId,out parent);
                if(parent==null){
                    var m=System.Text.RegularExpressions.Regex.Match((s.Number??"").Trim(),@"^(.+?)[HWM][0-9]*$",System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if(m.Success)byNum.TryGetValue(m.Groups[1].Value.Trim(),out parent);
                }
                if(parent!=null&&!string.IsNullOrWhiteSpace(parent.Notes)&&s.Notes!=parent.Notes){s.Notes=parent.Notes;updated++;}
            }
            if(updated>0)Commit(next,CurrentId(),"已同步 "+updated+" 条气缸传感器备注");
            else UpdateStatus("所有气缸传感器备注均已与气缸一致");
        }
        void BatchSetNotesDialog(){
            var ids=SelectedIds();if(ids.Count==0)throw new InvalidOperationException("请先选择要设置备注的行。");
            using(var d=new Form{Text="批量设置备注 / 模块名",Font=Font,Width=Ui.S(420),Height=Ui.S(165),StartPosition=FormStartPosition.CenterParent,FormBorderStyle=FormBorderStyle.FixedDialog,MaximizeBox=false,MinimizeBox=false}){
                var l=new Label{Text="将选中 "+ids.Count+" 行的备注统一设为：",Left=Ui.S(14),Top=Ui.S(16),AutoSize=true};
                var tb=new TextBox{Left=Ui.S(14),Top=Ui.S(42),Width=Ui.S(374)};
                var ok=Ui.Primary("应用",86,28);ok.Left=Ui.S(212);ok.Top=Ui.S(84);ok.DialogResult=DialogResult.OK;
                var cancel=Ui.Secondary("取消",80,28);cancel.Left=Ui.S(306);cancel.Top=Ui.S(84);cancel.DialogResult=DialogResult.Cancel;
                d.Controls.AddRange(new Control[]{l,tb,ok,cancel});d.AcceptButton=ok;d.CancelButton=cancel;
                if(d.ShowDialog(this)!=DialogResult.OK)return;
                var next=Snapshot();
                foreach(var r in next.Records.Where(x=>ids.Contains(x.Id))){r.Notes=tb.Text.Trim();if(string.IsNullOrWhiteSpace(r.Mechanism))r.Mechanism=tb.Text.Trim();}
                Commit(next,ids[0],"已更新 "+ids.Count+" 行备注");
            }
        }
        void ScanAssemblyDialog(){
            Ensure();SaveDraft();
            status.Text="正在扫描当前装配中的气缸、阀岛与传感器…";Application.DoEvents();
            var found=LineupCad.ScanAssembly(assembly,project,catalog);
            var candidates=found.Where(x=>!x.AlreadyBound).ToList();
            if(candidates.Count==0){
                UpdateStatus(found.Count==0?"当前装配未自动识别到匹配内置型号库的气动/电气件（可手动点选加入）":"识别到的 "+found.Count+" 个器件均已录入并绑定");
                return;
            }
            using(var d=new Form{Text="从当前装配自动识别器件（发现 "+candidates.Count+" 个未录入）",Font=Font,Width=Ui.S(820),Height=Ui.S(520),StartPosition=FormStartPosition.CenterParent,BackColor=Ui.Bg}){
                var clb=new CheckedListBox{Dock=DockStyle.Fill,CheckOnClick=true,Font=Ui.F9};
                foreach(var c in candidates)clb.Items.Add("["+c.Category+"] "+c.Model+(string.IsNullOrEmpty(c.Brand)?"":" ("+c.Brand+")")+"  ←  "+c.DisplayPath,true);
                var bar=new FlowLayoutPanel{Dock=DockStyle.Top,Height=Ui.S(36),Padding=Ui.SPad(10,4,10,4),BackColor=Ui.Rail};
                var selAll=Ui.Secondary("全选",70,26);selAll.Click+=(s,e)=>{for(int i=0;i<clb.Items.Count;i++)clb.SetItemChecked(i,true);};
                var selNone=Ui.Secondary("全不选",70,26);selNone.Click+=(s,e)=>{for(int i=0;i<clb.Items.Count;i++)clb.SetItemChecked(i,false);};
                bar.Controls.Add(selAll);bar.Controls.Add(selNone);
                var bottom=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=Ui.S(46),FlowDirection=FlowDirection.RightToLeft,Padding=Ui.SPad(12,8,12,8),BackColor=Ui.Rail};
                var ok=Ui.Primary("批量加入并自动编号",156,30);ok.DialogResult=DialogResult.OK;
                var cancel=Ui.Secondary("取消",80,30);cancel.DialogResult=DialogResult.Cancel;
                bottom.Controls.Add(ok);bottom.Controls.Add(cancel);
                d.AcceptButton=ok;d.CancelButton=cancel;
                d.Controls.Add(clb);d.Controls.Add(bar);d.Controls.Add(bottom);
                if(d.ShowDialog(this)!=DialogResult.OK)return;
                var picked=new List<LineupCadScanItem>();
                for(int i=0;i<clb.Items.Count;i++)if(clb.GetItemChecked(i))picked.Add(candidates[i]);
                if(picked.Count==0)return;
                if(picked.Any(x=>x.Category=="气缸"||x.Category=="阀片")&&!ReadyNumbering("气缸"))return;
                var next=Snapshot();int added=0;
                foreach(var c in picked){
                    string cat=LineupTableData.Categories.Contains(c.Category)?c.Category:"其他电气件";
                    string num=cat=="气缸传感器"?"":LineupNumbering.Next(next,cat);
                    next.Records.Add(new LineupRecord{
                        Category=cat,Number=num,Description=c.Description,Model=c.Model,Brand=c.Brand,Connector=c.Connector,
                        Mechanism=c.Mechanism,Notes=c.Mechanism,ReferenceKey=c.ReferenceKey,SourceFile=c.SourceFile,InstancePath=c.InstancePath
                    });
                    added++;
                }
                Commit(next,null,"已自动识别并加入 "+added+" 个三维器件（已绑定模型）");
                category.SelectedIndex=0;
            }
        }
        void OpenCadPageStudio(){
            SaveDraft(false);
            if(preview!=null)preview.Hold(true);
            using(var studio=new LineupCadPageForm(
                app,assembly,
                ()=>Snapshot(),
                (updated,msg)=>CommitFileOnly(updated,CurrentId(),msg),
                rec=>LocateRecord(rec),
                owner=>ExportXlsxDialog(owner),
                hide=>{if(hide){ClearHighlight();Hide();}else{Show();}})){
                studio.ShowDialog(this);
            }
            UpdatePreviewForCurrentRow();
        }
        void OpenAuditDialog(){
            SaveDraft(false);
            using(var dlg=new LineupAuditForm(
                ()=>Snapshot(),
                id=>{category.SelectedIndex=0;search.Clear();SelectId(id);},
                ()=>{var p=Snapshot();var ids=p.Records.Where(r=>r.Category=="气缸"&&!string.IsNullOrWhiteSpace(r.Number)).Select(r=>r.Id).ToList();if(ids.Count>0)GenerateValvesFor(ids);},
                ()=>{var p=Snapshot();var ids=p.Records.Where(r=>r.Category=="气缸"&&!string.IsNullOrWhiteSpace(r.Number)).Select(r=>r.Id).ToList();if(ids.Count>0)GenerateSensorPairsFor(ids);},
                SyncSensorNotes)){
                dlg.ShowDialog(this);
            }
        }
        void ImportXlsxDialog(){
            using(var d=new OpenFileDialog{Filter="Excel Lineup 工作簿|*.xlsx;*.xlsm",Title="选择明文 Lineup Excel 文件（自动读取 LineUP第二版_BOM）"}){
                if(d.ShowDialog(this)==DialogResult.OK)ImportXlsx(d.FileName);
            }
        }
        void ExportXlsxDialog(){ExportXlsxDialog(this);}
        void ExportXlsxDialog(IWin32Window owner){
            owner=owner??this;
            SaveDraft(false);
            var p=Snapshot();
            string proj=string.IsNullOrWhiteSpace(p.ProjectCode)?"":p.ProjectCode.Trim();
            string sta=!string.IsNullOrWhiteSpace(p.StationCode)?p.StationCode.Trim():(p.NumberPrefix??"").Trim();
            string rev=string.IsNullOrWhiteSpace(p.Revision)?"V1.0":p.Revision.Trim();
            using(var optDlg=new Form{Text="导出公司标准 Lineup 工作簿 (.xlsx)",Font=Font,Width=Ui.S(520),Height=Ui.S(360),StartPosition=FormStartPosition.CenterParent,FormBorderStyle=FormBorderStyle.FixedDialog,MaximizeBox=false,MinimizeBox=false,BackColor=Ui.Bg}){
                var tl=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,Padding=new Padding(14)};
                tl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,Ui.S(125)));tl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
                var tbProj=new TextBox{Text=proj,Dock=DockStyle.Fill};
                var tbSta=new TextBox{Text=sta,Dock=DockStyle.Fill};
                var tbLine=new TextBox{Text=string.IsNullOrWhiteSpace(p.LineCode)?"L1":p.LineCode,Dock=DockStyle.Fill};
                var tbRev=new TextBox{Text=rev,Dock=DockStyle.Fill};
                var tbDes=new TextBox{Text=p.Designer??"",Dock=DockStyle.Fill};
                var tbEd=new TextBox{Text=p.Editor??"",Dock=DockStyle.Fill};
                var chk11=new CheckBox{Text="BOM 导出 11 列（包含「机构名称」列；取消勾选为标准 10 列）",AutoSize=true,Checked=p.Export11Columns};
                var chkCad=new CheckBox{Text="导出 LineUP第三版_CAD 位置图（当前已建 "+(p.CadPages==null?0:p.CadPages.Count)+" 页）",AutoSize=true,Checked=true};
                var chkWarn=new CheckBox{Text="在 BOM 中淡黄标出规范自检缺漏行",AutoSize=true,Checked=false};
                Action<int,string,Control> addRow=(r,l,c)=>{tl.Controls.Add(new Label{Text=l,AutoSize=true,Anchor=AnchorStyles.Left},0,r);tl.Controls.Add(c,1,r);};
                addRow(0,"项目编号",tbProj);addRow(1,"工位号",tbSta);addRow(2,"线体号",tbLine);addRow(3,"版本号",tbRev);addRow(4,"设计人 / 编辑人",new Panel{Height=Ui.S(26),Dock=DockStyle.Fill});
                var ppl=(Panel)tl.GetControlFromPosition(1,4);tbDes.Dock=DockStyle.Left;tbDes.Width=Ui.S(165);tbEd.Dock=DockStyle.Fill;ppl.Controls.Add(tbEd);ppl.Controls.Add(new Panel{Dock=DockStyle.Left,Width=Ui.S(8)});ppl.Controls.Add(tbDes);
                tl.Controls.Add(chk11,0,5);tl.SetColumnSpan(chk11,2);
                tl.Controls.Add(chkCad,0,6);tl.SetColumnSpan(chkCad,2);
                tl.Controls.Add(chkWarn,0,7);tl.SetColumnSpan(chkWarn,2);
                var btns=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=Ui.S(46),FlowDirection=FlowDirection.RightToLeft,Padding=Ui.SPad(12,8,12,8),BackColor=Ui.Rail};
                var ok=Ui.Primary("选择保存位置并导出",162,30);ok.DialogResult=DialogResult.OK;
                var cancel=Ui.Secondary("取消",80,30);cancel.DialogResult=DialogResult.Cancel;
                btns.Controls.Add(ok);btns.Controls.Add(cancel);
                optDlg.AcceptButton=ok;optDlg.CancelButton=cancel;
                optDlg.Controls.Add(tl);optDlg.Controls.Add(btns);
                if(optDlg.ShowDialog(owner)!=DialogResult.OK)return;

                p.ProjectCode=tbProj.Text.Trim();p.StationCode=tbSta.Text.Trim();p.LineCode=string.IsNullOrWhiteSpace(tbLine.Text)?"L1":tbLine.Text.Trim();
                p.Revision=string.IsNullOrWhiteSpace(tbRev.Text)?"V1.0":tbRev.Text.Trim();p.Designer=tbDes.Text.Trim();p.Editor=tbEd.Text.Trim();
                p.Export11Columns=chk11.Checked;
                CommitFileOnly(p,CurrentId(),"已更新项目导出配置");

                string defName=(p.ProjectCode.Length>0?p.ProjectCode+"-":"")+(p.StationCode.Length>0?p.StationCode+"-":Path.GetFileNameWithoutExtension(file)+"-")+"line up-"+p.Revision+"-"+DateTime.Now.ToString("yyyyMMdd")+".xlsx";
                using(var sfd=new SaveFileDialog{Filter="Excel 工作簿|*.xlsx",FileName=defName,InitialDirectory=Path.GetDirectoryName(file)}){
                    if(sfd.ShowDialog(owner)!=DialogResult.OK)return;
                    var opts=new LineupXlsxOptions{Export11Columns=chk11.Checked,IncludeCadPages=chkCad.Checked,HighlightAudit=chkWarn.Checked};
                    var sum=ExportXlsx(sfd.FileName,opts);
                    string msg="导出成功！\n\n文件："+sum.OutputPath
                        +"\n· LineUP第二版_BOM："+sum.BomRows+" 条记录（"+(opts.Export11Columns?"11 列":"标准 10 列")+"）"
                        +"\n· LineUP第三版_CAD："+sum.CadPages+" 页，"+sum.CadCallouts+" 处标注，"+sum.CadArrows+" 条原生红色箭头"
                        +"\n· 签字页自动核验：错误 "+sum.AuditErrors+" 项，提醒 "+sum.AuditWarnings+" 项"
                        +"\n\n是否立即打开该 Excel 文件？";
                    UpdateStatus("已导出标准 Lineup 工作簿："+Path.GetFileName(sum.OutputPath));
                    if(MessageBox.Show(owner,msg,"导出完成",MessageBoxButtons.YesNo,MessageBoxIcon.Information)==DialogResult.Yes){
                        try{Process.Start(new ProcessStartInfo(sum.OutputPath){UseShellExecute=true});}catch{}
                    }
                }
            }
        }
        // 左侧类目导航（0.9.0）：点一个类目 = 原来的类目下拉框选它（下拉框还在，代码照旧读 category.SelectedIndex）
        Panel nav;readonly StepRail navRail=new StepRail{Kind=StepRail.Mode.Nav};readonly Label navStats=new Label();
        Control BuildNav(){
            nav=new Panel{Dock=DockStyle.Left,Width=Ui.S(230),BackColor=Ui.Rail,Padding=Ui.SPad(12,16,12,16)};
            var label=new Label{Text="查找",Font=Ui.F8,ForeColor=Ui.Text2,BackColor=Ui.Rail,AutoSize=false};
            search.BorderStyle=BorderStyle.FixedSingle;search.Font=Ui.F9;
            foreach(var c in category.Items)navRail.Add(Convert.ToString(c),"");
            navRail.Selected=Math.Max(0,category.SelectedIndex);
            navRail.SelectedChanged+=(s,e)=>{if(category.SelectedIndex!=navRail.Selected)category.SelectedIndex=navRail.Selected;};
            category.SelectedIndexChanged+=(s,e)=>{if(category.SelectedIndex>=0)navRail.Selected=category.SelectedIndex;};
            var card=new RailCard("清单状态",118);
            navStats.AutoSize=false;navStats.Font=Ui.F8;navStats.ForeColor=Ui.Text2;navStats.BackColor=Color.White;
            card.Controls.Add(navStats);card.Resize+=(s,e)=>navStats.SetBounds(Ui.S(12),Ui.S(34),Math.Max(20,card.Width-Ui.S(24)),Math.Max(20,card.Height-Ui.S(42)));
            nav.Controls.AddRange(new Control[]{label,search,navRail,card});
            nav.Layout+=(s,e)=>{int p=Ui.S(12),t=Ui.S(16),w=Math.Max(20,nav.ClientSize.Width-p*2),h=nav.ClientSize.Height;label.SetBounds(p,t,w,Ui.S(20));search.SetBounds(p,t+Ui.S(22),w,Ui.S(28));card.SetBounds(p,h-t-card.Height,w,card.Height);int ry=t+Ui.S(60);navRail.SetBounds(p,ry,w,Math.Max(0,card.Top-p-ry));};
            nav.Paint+=(s,e)=>{using(var p=new Pen(Ui.Line))e.Graphics.DrawLine(p,nav.Width-1,0,nav.Width-1,nav.Height);};
            return nav;
        }
        void RefreshNav(){
            if(nav==null)return;
            try{
                for(int i=0;i<navRail.Items.Count;i++){
                    int n=i==0?project.Records.Count:i<=LineupTableData.Categories.Length?project.Records.Count(r=>LineupTableData.Category(r.Category)==LineupTableData.Categories[i-1]):project.Records.Count(r=>!LineupTableData.Categories.Contains(LineupTableData.Category(r.Category)));
                    navRail.SetCount(i,n.ToString(),n==0&&i>0);
                }
                int unnumbered=project.Records.Count(r=>string.IsNullOrWhiteSpace(r.Number)),unbound=project.Records.Count(r=>string.IsNullOrEmpty(r.ReferenceKey));
                int cadPages=project.CadPages==null?0:project.CadPages.Count;
                navStats.Text="未编号　"+unnumbered+" 条"+Environment.NewLine+"未绑定模型　"+unbound+" 条"+Environment.NewLine+"CAD 位置图　"+cadPages+" 页"+Environment.NewLine+project.NumberPrefix+" Y"+project.ActiveIsland;
            }catch(Exception ex){Log.Write("LineupNav",ex.Message);}
        }
        // 菜单的扁平外观：白底、没有边框，悬停浅灰圆角
        sealed class FlatMenuRenderer:ToolStripProfessionalRenderer{
            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e){}
            protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e){using(var b=new SolidBrush(Ui.Bg))e.Graphics.FillRectangle(b,e.AffectedBounds);}
            protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e){if(e.Item.Selected||e.Item.Pressed)Ui.FillRound(e.Graphics,new Rectangle(0,0,e.Item.Width-1,e.Item.Height-1),6,Ui.Subtle);}
            protected override void OnRenderDropDownButtonBackground(ToolStripItemRenderEventArgs e){if(e.Item.Selected||e.Item.Pressed)Ui.FillRound(e.Graphics,new Rectangle(0,0,e.Item.Width-1,e.Item.Height-1),6,Ui.Subtle);}
            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e){if(e.Item.Selected&&e.Item.Enabled)using(var b=new SolidBrush(Ui.AccentSoft))e.Graphics.FillRectangle(b,new Rectangle(2,0,e.Item.Width-4,e.Item.Height));}
            protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e){using(var p=new Pen(Ui.Line)){if(e.Vertical)e.Graphics.DrawLine(p,e.Item.Width/2,4,e.Item.Width/2,e.Item.Height-4);else e.Graphics.DrawLine(p,4,e.Item.Height/2,e.Item.Width-4,e.Item.Height/2);}}
        }
        void InitializePickingPanel(Control toolbar){
            fullToolbar=toolbar;pickingPanel=new Panel{Dock=DockStyle.Fill,Visible=false};pickingLabel=new Label{Dock=DockStyle.Top,Height=Ui.S(64),Padding=Ui.SPad(10,8,10,4),AutoEllipsis=true,BackColor=Ui.Bg};
            var actions=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=Ui.S(68),Padding=Ui.SPad(6,4,6,4),BackColor=Ui.Bg};
            Button(actions,"绑定并下一条",BindAndNext);Button(actions,"另建记录",()=>AddSelectedModels());Button(actions,"返回表格",()=>SetPickingMode(false));
            watchStop=Ui.Secondary("停止连续",88,32);watchStop.Visible=false;watchStop.Margin=Ui.SPad(4,1,2,1);((FlatButton)watchStop).SetFore(Ui.Bad);watchStop.Click+=(s,e)=>StopContinuous();actions.Controls.Add(watchStop);Button(actions,"撤销",Undo);
            pickingPanel.Controls.Add(pickingLabel);pickingPanel.Controls.Add(actions);Controls.Add(pickingPanel);
            grid.CurrentCellChanged+=(s,e)=>RefreshPickingLabel();
        }
        internal void ApplyFieldVisibility(){if(grid.Columns.Count<14)return;foreach(DataGridViewColumn column in grid.Columns)column.Visible=column.Name=="上级"||fullFieldView||new[]{0,1,4,5,6,7,8}.Contains(column.Index)||(column.Index==10&&(category.SelectedIndex==0||ActiveCategory=="普通传感器"||ActiveCategory=="气缸传感器"))||(column.Name=="阀岛型号"&&ActiveCategory=="阀片");grid.Columns[7].DisplayIndex=2;grid.Columns[8].DisplayIndex=3;}
        internal bool PickingMode {get{return pickingMode;}}
        internal string BindingRecordId {get{return CurrentId();}}
        internal void SetPickingMode(bool value){
            if(value==pickingMode)return;grid.EndEdit();if(value)pickingId=CurrentId();pickingMode=value;
            if(value){
                normalBounds=Bounds;
                previewWasVisible=preview!=null&&preview.Visible;
                if(preview!=null){preview.Hold(true);preview.Visible=false;}
                if(previewSplit!=null)previewSplit.Visible=false;
                fullToolbar.Visible=false;helpBar.Visible=false;if(nav!=null)nav.Visible=false;if(entryPanel!=null)entryPanel.Visible=false;gridHost.Visible=false;status.Visible=false;
                MinimumSize=new Size(Ui.S(400),Ui.S(145));Size=new Size(Ui.S(440),Ui.S(180));
                var area=Screen.FromControl(this).WorkingArea;Location=new Point(Math.Max(area.Left,area.Right-Width-Ui.S(20)),Math.Max(area.Top,area.Bottom-Height-Ui.S(60)));
                pickingPanel.Visible=true;pickingPanel.BringToFront();
            }else{
                StopContinuous();pickingPanel.Visible=false;var wa=Screen.FromControl(this).WorkingArea;MinimumSize=new Size(Math.Min(Ui.S(1040),wa.Width-Ui.S(24)),Math.Min(Ui.S(520),wa.Height-Ui.S(24)));Bounds=normalBounds;
                fullToolbar.Visible=true;helpBar.Visible=true;if(nav!=null)nav.Visible=true;if(entryPanel!=null)entryPanel.Visible=true;gridHost.Visible=true;status.Visible=true;
                if(preview!=null&&previewWasVisible){preview.Visible=true;if(previewSplit!=null)previewSplit.Visible=true;UpdatePreviewForCurrentRow();}
            }
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
            next.NumberPrefix=project.NumberPrefix;next.ActiveIsland=project.ActiveIsland;next.Islands=project.Islands.Select(i=>i.Copy()).ToList();
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
            using(var dialog=new OpenFileDialog{Filter="型号库 / 清单|*.xlsx;*.xlsm;*.csv;*.tsv;*.txt;*.xml"})if(dialog.ShowDialog(this)==DialogResult.OK){
                string ext=Path.GetExtension(dialog.FileName).ToLowerInvariant();
                int count;
                if(ext==".xlsx"||ext==".xlsm"){
                    var imp=LineupXlsxReader.Read(dialog.FileName,true);
                    count=LearnModels(imp.Records);
                }else{
                    var imported=ext==".xml"?LineupModelCatalog.Load(dialog.FileName):LineupModelCatalog.Import(SettingsFile.ReadUserText(dialog.FileName),ext==".csv"?',':'\t',ActiveCategory);
                    count=LearnModels(imported.Entries.Select(e=>new LineupRecord{Category=e.Category,Model=e.Model,Brand=e.Brand,Connector=e.Connector}));
                }
                status.Text="型号库新增 "+count+" 条，现有 "+catalog.Entries.Count+" 条。";
            }
        }
        void ExportLibrary(){using(var d=new SaveFileDialog{Filter="型号库 CSV|*.csv",FileName="Lineup-型号库.csv"})if(d.ShowDialog(this)==DialogResult.OK){var rows=new List<string[]>{new[]{"类别","型号","品牌","传感器接口类型"}};rows.AddRange(catalog.Entries.Select(e=>new[]{e.Category,e.Model,e.Brand,e.Connector}));SettingsFile.WriteUserText(d.FileName,LineupCsv.Write(rows));status.Text="型号库已导出。";}}
    }
}
