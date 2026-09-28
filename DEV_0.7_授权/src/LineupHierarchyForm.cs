using System;
using System.Linq;
using System.Data;
using System.Drawing;
using System.Windows.Forms;
namespace TianGongCadSuite {
    public sealed partial class LineupTableForm {
        void InitializeHierarchy(){
            data.Columns.Add("上级");grid.Columns.Add(new DataGridViewTextBoxColumn{Name="上级",HeaderText="所属上级",DataPropertyName="上级",ReadOnly=true,Width=180});
            grid.Columns["上级"].DisplayIndex=2;
        }
        internal void InsertAt(bool after){
            if(ActiveCategory=="气缸传感器")throw new InvalidOperationException("传感器请从气缸生成 H/W 后移动到所需位置。");
            if(!ReadyNumbering(ActiveCategory))return;var next=Snapshot();int index=next.Records.FindIndex(r=>r.Id==CurrentId());
            var row=new LineupRecord{Category=ActiveCategory,Number=LineupNumbering.Next(next,ActiveCategory)};
            next.Records.Insert(index<0?next.Records.Count:index+(after?1:0),row);data.DefaultView.Sort="";Commit(next,row.Id,"已插入，可撤销");
        }
        internal void MoveRows(int direction){
            if(data.DefaultView.Sort.Length>0)throw new InvalidOperationException("请先取消列排序（重新打开清单），再调整行位置。");
            var ids=SelectedIds();if(ids.Count==0)return;var visible=grid.Rows.Cast<DataGridViewRow>().Select(r=>Convert.ToString(((DataRowView)r.DataBoundItem)["Id"])).ToList();
            var next=Snapshot();LineupHierarchy.Move(next,visible,ids,direction);Commit(next,ids[0],"行位置已保存，可撤销");
            grid.ClearSelection();foreach(DataGridViewRow r in grid.Rows)if(ids.Contains(Convert.ToString(((DataRowView)r.DataBoundItem)["Id"])))r.Cells[1].Selected=true;
        }
        void AssignCylinderDialog(){
            var ids=SelectedIds();var next=Snapshot();if(ids.Count==0||next.Records.Where(r=>ids.Contains(r.Id)).Any(r=>r.Category!="气缸"))throw new InvalidOperationException("先选择一个或多个气缸行。");
            var valves=next.Records.Where(r=>r.Category=="阀片").ToArray();if(valves.Length==0)throw new InvalidOperationException("请先新增阀片并编号。");
            using(var d=new Form{Text="气缸批量归属阀片",Width=620,Height=240,Font=Font,StartPosition=FormStartPosition.CenterParent}){
                var combo=new ComboBox{Dock=DockStyle.Top,DropDownStyle=ComboBoxStyle.DropDownList};combo.Items.AddRange(valves);combo.SelectedIndex=0;
                var note=new Label{Dock=DockStyle.Fill,Padding=new Padding(10),Text="所选气缸将归到该阀片，气缸编号跟随阀片。\n一片阀控制多个气缸时，使用 C01-1、C01-2…；H/W/M 同步更新。\n重复编号会阻止保存。操作可撤销。"};
                var ok=new Button{Text="应用归属并更新编号",Dock=DockStyle.Bottom,Height=40,DialogResult=DialogResult.OK};d.Controls.Add(note);d.Controls.Add(combo);d.Controls.Add(ok);
                if(d.ShowDialog(this)==DialogResult.OK)AssignCylindersToValve(ids,((LineupRecord)combo.SelectedItem).Id);
            }
        }
        internal void AssignCylindersToValve(System.Collections.Generic.IEnumerable<string> ids,string valveId){var selected=ids.ToList();var next=Snapshot();LineupHierarchy.AssignCylinders(next,selected,valveId);Commit(next,selected.First(),"气缸及传感器归属、编号已更新");}
        void ShowHierarchy(){
            var p=Snapshot();using(var d=new Form{Text="阀岛 → 阀片 → 气缸 → 传感器（双击跳转）",Width=720,Height=600,Font=Font,StartPosition=FormStartPosition.CenterParent}){
                var tree=new TreeView{Dock=DockStyle.Fill};var root=new TreeNode("Lineup");tree.Nodes.Add(root);
                var nodes=p.Records.ToDictionary(r=>r.Id,r=>new TreeNode(r.Category+" · "+r){Tag=r.Id});
                var islands=p.Islands.GroupBy(i=>i.Key).ToDictionary(g=>g.Key,g=>new TreeNode("阀岛 · "+g.Key+" · "+g.First().Model));foreach(var n in islands.Values)root.Nodes.Add(n);
                foreach(var r in p.Records){string parent=r.Category=="气缸传感器"?r.CylinderId:r.Category=="气缸"?r.ValveId:"";TreeNode n;
                    if(parent!=r.Id&&nodes.TryGetValue(parent,out n)&&(r.Category=="气缸"?p.Records.First(x=>x.Id==parent).Category=="阀片":p.Records.First(x=>x.Id==parent).Category=="气缸"))n.Nodes.Add(nodes[r.Id]);
                    else if(r.Category=="阀片"&&islands.TryGetValue(LineupNumbering.IslandKey(r.Number),out n))n.Nodes.Add(nodes[r.Id]);else root.Nodes.Add(nodes[r.Id]);}
                tree.NodeMouseDoubleClick+=(s,e)=>{if(e.Node.Tag!=null){category.SelectedIndex=0;search.Clear();SelectId((string)e.Node.Tag);d.Close();}};d.Controls.Add(tree);tree.ExpandAll();d.ShowDialog(this);
            }
        }
    }
}
