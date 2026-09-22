using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
namespace TianGongCadSuite {
    public sealed partial class LineupTableForm {
        Panel entryPanel;
        TextBox entryPrefix,entryBrand,entryConnector,entryDescription,entryMechanism,entryHome,entryMounting;
        ComboBox entryModel,entryParent,entryPosition;
        NumericUpDown entryIsland,entryQuantity;
        CheckBox entrySensors;
        Label entryHint;
        readonly Dictionary<string,LineupEntryPreset> entryDrafts=new Dictionary<string,LineupEntryPreset>();
        readonly Timer entryWatcher=new Timer{Interval=350};
        string entryKind,watchMode="",watchFingerprint="",watchHandled="",entryAutoBrand="",entryAutoConnector="";
        bool entryLoading,watchBusy;
        LineupEntryPreset watchPreset;
        Button watchStop;
        sealed class ParentChoice {public string Id,Text;public override string ToString(){return Text;}}
        static TextBox EntryText(){return new TextBox{Dock=DockStyle.Fill};}
        void InitializeEntry(){
            entryPanel=new Panel{Dock=DockStyle.Top,Height=142,BackColor=Color.FromArgb(238,246,242),Padding=new Padding(6)};
            var setup=new FlowLayoutPanel{Dock=DockStyle.Top,Height=34,WrapContents=false,AutoScroll=true};
            entryPrefix=EntryText();entryPrefix.Width=86;entryPrefix.Text=project.NumberPrefix;
            entryIsland=new NumericUpDown{Width=54,Minimum=1,Maximum=9999,Value=Math.Min(9999,Math.Max(1,project.ActiveIsland))};
            entryParent=new ComboBox{Width=205,DropDownStyle=ComboBoxStyle.DropDownList};
            entryQuantity=new NumericUpDown{Width=53,Minimum=1,Maximum=500,Value=1};
            entrySensors=new CheckBox{Text="同时生成 H/W",AutoSize=true,Margin=new Padding(6,5,2,0)};
            entryPosition=new ComboBox{Width=50,DropDownStyle=ComboBoxStyle.DropDownList};entryPosition.Items.AddRange(new[]{"H","W","M"});entryPosition.SelectedIndex=0;
            Action<string,Control> add=(label,control)=>{setup.Controls.Add(new Label{Text=label,AutoSize=true,Margin=new Padding(3,5,2,0)});setup.Controls.Add(control);};
            add("前缀",entryPrefix);add("Y",entryIsland);add("上级",entryParent);setup.Controls.Add(entrySensors);setup.Controls.Add(entryPosition);add("数量",entryQuantity);
            Button(setup,"加入 / 新增 ↵",()=>QuickEntry(false));Button(setup,"连续新增",()=>StartContinuous("add"));Button(setup,"清空共用字段",()=>WriteEntry(new LineupEntryPreset{Category=entryKind}));
            var fields=new TableLayoutPanel{Dock=DockStyle.Top,Height=66,ColumnCount=8,RowCount=2};
            for(int i=0;i<4;i++){fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,45));fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,i==0?34:i==1?22:22));}
            entryModel=new ComboBox{Dock=DockStyle.Fill,DropDownStyle=ComboBoxStyle.DropDown,AutoCompleteMode=AutoCompleteMode.SuggestAppend,AutoCompleteSource=AutoCompleteSource.ListItems};
            entryBrand=EntryText();entryConnector=EntryText();entryDescription=EntryText();entryMechanism=EntryText();entryHome=EntryText();entryMounting=EntryText();
            Action<int,int,string,Control> field=(row,col,label,control)=>{fields.Controls.Add(new Label{Text=label,Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft},col*2,row);fields.Controls.Add(control,col*2+1,row);};
            field(0,0,"型号",entryModel);field(0,1,"品牌",entryBrand);field(0,2,"接口",entryConnector);field(0,3,"机构",entryMechanism);
            field(1,0,"描述",entryDescription);field(1,1,"原位",entryHome);field(1,2,"安装",entryMounting);
            var reuse=new Button{Text="沿用当前行",Dock=DockStyle.Fill};reuse.Click+=(s,e)=>Try(UseCurrentEntry);fields.Controls.Add(reuse,6,1);fields.SetColumnSpan(reuse,2);
            entryHint=new Label{Dock=DockStyle.Bottom,Height=24,Text="共用字段只填一次；选模型后加入，无选择则按数量新增。",Padding=new Padding(2,3,0,0)};
            entryPanel.Controls.Add(fields);entryPanel.Controls.Add(setup);entryPanel.Controls.Add(entryHint);
            Controls.Add(entryPanel);Controls.SetChildIndex(entryPanel,Controls.IndexOf(helpBar));
            entryModel.TextChanged+=(s,e)=>CompleteEntryModel();
            category.SelectedIndexChanged+=(s,e)=>ChangeEntryCategory();
            foreach(Control control in EntryControls(entryPanel))control.KeyDown+=(s,e)=>{if(e.KeyCode==Keys.Enter&&!(control is Button)&&!(control is ComboBox&&((ComboBox)control).DroppedDown)){e.Handled=true;e.SuppressKeyPress=true;Try(()=>QuickEntry(false));}};
            entryWatcher.Tick+=(s,e)=>PollContinuous();
            FormClosing+=(s,e)=>StopContinuous();FormClosed+=(s,e)=>entryWatcher.Dispose();
            KeyDown+=(s,e)=>{if(e.KeyCode==Keys.Escape&&watchMode.Length>0){e.SuppressKeyPress=true;StopContinuous();}if(e.Control&&e.KeyCode==Keys.Enter){e.SuppressKeyPress=true;Try(()=>QuickEntry(false));}if(e.Control&&e.KeyCode==Keys.D&&grid.ContainsFocus){e.SuppressKeyPress=true;Try(FillDown);}};
            ChangeEntryCategory();
        }
        static IEnumerable<Control> EntryControls(Control root){foreach(Control c in root.Controls){yield return c;foreach(var nested in EntryControls(c))yield return nested;}}
        LineupEntryPreset ReadEntry(){return new LineupEntryPreset{Category=entryKind??ActiveCategory,Model=entryModel.Text,Brand=entryBrand.Text,Connector=entryConnector.Text,Description=entryDescription.Text,Mechanism=entryMechanism.Text,Home=entryHome.Text,Mounting=entryMounting.Text,ParentId=entryParent.SelectedItem is ParentChoice?((ParentChoice)entryParent.SelectedItem).Id:"",Position=entryPosition.Text,GenerateSensors=entrySensors.Checked};}
        void ChangeEntryCategory(){
            if(entryPanel==null)return;
            if(entryKind!=null)entryDrafts[entryKind]=ReadEntry();
            entryKind=ActiveCategory;
            LineupEntryPreset preset;if(!entryDrafts.TryGetValue(entryKind,out preset))preset=project.EntryPresets.LastOrDefault(x=>x.Category==entryKind)??new LineupEntryPreset{Category=entryKind};
            WriteEntry(preset);
        }
        internal void WriteEntry(LineupEntryPreset preset){
            entryLoading=true;entryKind=preset.Category;
            entryModel.Items.Clear();entryModel.Items.AddRange(catalog.Entries.Where(x=>x.Category==entryKind).Select(x=>x.Model).Distinct().OrderBy(x=>x).Cast<object>().ToArray());
            entryModel.Text=preset.Model;entryBrand.Text=preset.Brand;entryConnector.Text=preset.Connector;entryDescription.Text=preset.Description;entryMechanism.Text=preset.Mechanism;entryHome.Text=preset.Home;entryMounting.Text=preset.Mounting;entrySensors.Checked=preset.GenerateSensors;entryPosition.SelectedItem=preset.Position;entryAutoBrand="";entryAutoConnector="";
            RefreshEntryParents(preset.ParentId);
            entrySensors.Visible=entryKind=="气缸";entryPosition.Visible=entryKind=="气缸传感器";entryQuantity.Enabled=entryKind!="气缸传感器";
            entryLoading=false;CompleteEntryModel();
        }
        void RefreshEntryParents(string preferred=null){
            if(entryParent==null)return;
            string id=preferred??(entryParent.SelectedItem is ParentChoice?((ParentChoice)entryParent.SelectedItem).Id:"");
            string kind=entryKind=="气缸"?"阀片":entryKind=="气缸传感器"?"气缸":"";
            entryParent.Items.Clear();entryParent.Items.Add(new ParentChoice{Id="",Text=kind.Length==0?"按当前类目自动处理":"选择"+kind+"（可搜索编号）"});
            foreach(var r in project.Records.Where(x=>kind.Length>0&&x.Category==kind))entryParent.Items.Add(new ParentChoice{Id=r.Id,Text=r.Number+" · "+r.Description});
            entryParent.SelectedIndex=0;
            for(int i=1;i<entryParent.Items.Count;i++)if(((ParentChoice)entryParent.Items[i]).Id==id)entryParent.SelectedIndex=i;
            if(id.Length>0&&entryParent.SelectedIndex==0){entryParent.Items.Add(new ParentChoice{Id=id,Text="上级已失效，请重新选择"});entryParent.SelectedIndex=entryParent.Items.Count-1;}
            entryParent.Enabled=kind.Length>0;entryParent.AutoCompleteSource=AutoCompleteSource.ListItems;entryParent.AutoCompleteMode=AutoCompleteMode.SuggestAppend;
        }
        void CompleteEntryModel(){
            if(entryLoading||entryModel==null)return;
            if(entryAutoBrand.Length>0&&entryBrand.Text==entryAutoBrand)entryBrand.Clear();if(entryAutoConnector.Length>0&&entryConnector.Text==entryAutoConnector)entryConnector.Clear();entryAutoBrand="";entryAutoConnector="";
            var matches=catalog.Match(entryKind,entryModel.Text).ToList();
            if(matches.Count==1){if(entryBrand.Text.Length==0){entryAutoBrand=matches[0].Brand;entryBrand.Text=entryAutoBrand;}if(entryConnector.Text.Length==0){entryAutoConnector=matches[0].Connector;entryConnector.Text=entryAutoConnector;}}
            entryHint.Text=matches.Count>1?"此型号有多种品牌/接口，请填写对应规格；手填值会保留。":"共用字段自动记忆；Enter 加入，Ctrl+D 向下填充；连续模式只需点模型。";
            if(matches.Count==1&&(entryBrand.Text!=matches[0].Brand||entryConnector.Text!=matches[0].Connector))entryHint.Text="品牌或接口与型号库不同，已保留手填值；F4 可选择库中规格。";
        }
        void UseCurrentEntry(){
            var r=Snapshot().Records.FirstOrDefault(x=>x.Id==CurrentId());if(r==null)throw new InvalidOperationException("请先选中要沿用的行。");
            category.SelectedItem=r.Category;
            WriteEntry(new LineupEntryPreset{Category=r.Category,Model=r.Model,Brand=r.Brand,Connector=r.Connector,Description=r.Description,Mechanism=r.Mechanism,Home=r.Home,Mounting=r.Mounting,ParentId=r.Category=="气缸"?r.ValveId:r.Category=="气缸传感器"?r.CylinderId:"",Position=string.IsNullOrEmpty(r.Position)?"H":r.Position});
            entryHint.Text="已沿用共用字段；后续新增自动编号，IO 地址留空。";
        }
        void PickEntryModel(){
            if(catalogError.Length>0)throw new InvalidOperationException(catalogError);
            using(var dialog=new Form{Text="本批录入型号 · "+entryKind,Font=Font,Width=650,Height=390,StartPosition=FormStartPosition.CenterParent}){
                var query=new TextBox{Dock=DockStyle.Top};var list=new ListBox{Dock=DockStyle.Fill,HorizontalScrollbar=true};
                Action refresh=()=>{list.Items.Clear();list.Items.AddRange(catalog.Entries.Where(x=>x.Category==entryKind&&x.ToString().IndexOf(query.Text,StringComparison.OrdinalIgnoreCase)>=0).Cast<object>().ToArray());if(list.Items.Count>0)list.SelectedIndex=0;};query.TextChanged+=(s,e)=>refresh();refresh();
                var ok=new Button{Text="用于后续录入",Dock=DockStyle.Bottom,Height=34,DialogResult=DialogResult.OK};dialog.AcceptButton=ok;list.DoubleClick+=(s,e)=>{if(list.SelectedItem!=null)dialog.DialogResult=DialogResult.OK;};dialog.Controls.Add(list);dialog.Controls.Add(query);dialog.Controls.Add(ok);
                if(dialog.ShowDialog(this)!=DialogResult.OK||list.SelectedItem==null)return;
                var preset=ReadEntry();var model=(LineupModelEntry)list.SelectedItem;preset.Model=model.Model;preset.Brand=model.Brand;preset.Connector=model.Connector;WriteEntry(preset);
            }
        }
        LineupProject EntrySnapshot(){
            var next=Snapshot();string prefix=entryPrefix.Text.Trim();int island=(int)entryIsland.Value;
            if(prefix.Length>0){string key=prefix+"-Y"+island;var configured=next.Islands.FirstOrDefault(x=>string.Equals(x.Key,key,StringComparison.OrdinalIgnoreCase));LineupNumbering.SetIsland(next,prefix,island,configured==null?"":configured.Model);}
            else {next.NumberPrefix="";next.ActiveIsland=island;}
            return next;
        }
        List<LineupRecord> CaptureEntrySelections(){
            var incoming=new List<LineupRecord>();int count=assembly.SelectSet.Count;
            for(int i=1;i<=count;i++){var row=new LineupRecord();LineupCad.CaptureOccurrence(assembly.SelectSet.Item(i),row);incoming.Add(row);}
            if(assembly.SelectSet.Count!=count)throw new InvalidOperationException("选择正在变化，请完成选择后重试。");return incoming;
        }
        internal int QuickEntry(bool requireSelection){
            Ensure();var incoming=CaptureEntrySelections();if(requireSelection&&incoming.Count==0)return 0;
            return CommitEntry(incoming,watchMode=="add"?watchPreset:ReadEntry(),requireSelection||entryKind=="气缸传感器"?1:(int)entryQuantity.Value);
        }
        int CommitEntry(List<LineupRecord> incoming,LineupEntryPreset preset,int quantity){
            var result=LineupEntry.Create(EntrySnapshot(),preset,quantity,incoming);
            if(result.AddedIds.Count==0){UpdateStatus("所选模型均已有记录，未重复录入");if(pickingMode)pickingLabel.Text="所选模型已有记录，请选择其他模型；待录入状态保持。";return 0;}
            string message="已录入 "+result.AddedIds.Count+" 条"+(result.SensorCount>0?"，同时生成 "+result.SensorCount+" 条 H/W":"")+(result.Skipped>0?"，跳过 "+result.Skipped+" 个重复模型":"")+"；已保存，可撤销";
            search.Clear();category.SelectedItem=preset.Category;
            Commit(result.Project,result.AddedIds.Last(),message);entryDrafts[preset.Category]=preset.Copy();RefreshEntryParents(preset.ParentId);
            if(incoming.Count>0){try{assembly.SelectSet.RemoveAll();}catch{message+="；CAD 选择未清空，请选择下一模型";}}
            if(pickingMode)pickingLabel.Text=message+"\n继续选择模型，或点“停止连续”。";return result.AddedIds.Count;
        }
        internal bool ContinuousActive {get{return watchMode.Length>0;}}
        internal void StartContinuous(string mode){
            Ensure();SaveDraft();if(mode!="add"&&mode!="bind")throw new ArgumentException("Unknown continuous mode");
            if(mode=="add"){
                watchPreset=ReadEntry();LineupEntry.Create(EntrySnapshot(),watchPreset,1,null);
            }else{
                var current=CurrentId();var r=project.Records.FirstOrDefault(x=>x.Id==current);
                if(r==null||!string.IsNullOrEmpty(r.ReferenceKey)){current=VisibleEntryIds().FirstOrDefault(id=>project.Records.Any(x=>x.Id==id&&string.IsNullOrEmpty(x.ReferenceKey)));if(current==null)throw new InvalidOperationException("当前筛选内没有待绑定行。");SelectId(current);}
            }
            watchMode=mode;watchFingerprint="";watchHandled="";SetPickingMode(true);watchStop.Visible=true;SetContinuousControls(true);
            pickingLabel.Text=mode=="add"?"连续新增 · "+watchPreset.Category+" · "+watchPreset.Model+"\n选中模型后自动编号、录入并保存。":"连续绑定 · "+project.Records.First(r=>r.Id==CurrentId()).Number+"\n每次选一个模型，自动跳过已绑定行。";
            entryWatcher.Start();
        }
        List<string> VisibleEntryIds(){return grid.Rows.Cast<DataGridViewRow>().Select(r=>Convert.ToString(((DataRowView)r.DataBoundItem)["Id"])).ToList();}
        void SetContinuousControls(bool active){if(pickingPanel==null)return;foreach(var button in EntryControls(pickingPanel).OfType<Button>())if(button.Text=="绑定并下一条"||button.Text=="另建记录")button.Enabled=!active;}
        internal void StopContinuous(){watchMode="";entryWatcher.Stop();watchFingerprint="";watchHandled="";if(watchStop!=null)watchStop.Visible=false;SetContinuousControls(false);RefreshPickingLabel();}
        internal void PollContinuous(){
            if(watchMode.Length==0||watchBusy||IsDisposed)return;watchBusy=true;
            try{
                Ensure();var incoming=CaptureEntrySelections();if(incoming.Count==0){watchFingerprint="";watchHandled="";return;}
                string fingerprint=string.Join("|",incoming.Select(r=>r.ReferenceKey).OrderBy(x=>x));
                if(fingerprint!=watchFingerprint){watchFingerprint=fingerprint;return;}if(fingerprint==watchHandled)return;watchHandled=fingerprint;
                if(watchMode=="add")CommitEntry(incoming,watchPreset,1);
                else {
                    if(incoming.Count!=1)throw new InvalidOperationException("连续绑定每次只选一个模型；当前待绑定行保持不变。");
                    string id=CurrentId();var next=Snapshot();var record=next.Records.Single(r=>r.Id==id);
                    var conflict=next.Records.FirstOrDefault(r=>r.ReferenceKey==incoming[0].ReferenceKey);
                    if(conflict!=null)throw new InvalidOperationException("模型已属于 "+conflict.Number+"；待绑定 "+record.Number+" 保持不变。");
                    if(!string.IsNullOrEmpty(record.ReferenceKey))throw new InvalidOperationException("当前行已绑定，请返回表格选择待绑定行。");
                    record.ReferenceKey=incoming[0].ReferenceKey;record.SourceFile=incoming[0].SourceFile;record.InstancePath=incoming[0].InstancePath;
                    string following=LineupEntry.NextUnbound(next,VisibleEntryIds(),id);
                    Commit(next,following??id,"已自动绑定 "+record.Number);
                    try{assembly.SelectSet.RemoveAll();}catch{}
                    if(following==null){StopContinuous();pickingLabel.Text="本次待绑定队列已完成，已自动停止；可返回表格检查。";}
                    else pickingLabel.Text="已绑定 "+record.Number+"\n下一条："+next.Records.Single(r=>r.Id==following).Number+"，请选择对应模型。";
                }
            }catch(System.Runtime.InteropServices.COMException){pickingLabel.Text="CAD 正忙，等待恢复；当前记录保持。";watchFingerprint="";watchHandled="";}
            catch(Exception ex){pickingLabel.Text=ex.Message;status.Text=ex.Message;}
            finally{watchBusy=false;}
        }
    }
}
