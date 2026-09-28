using System;
using System.Linq;
using System.Collections.Generic;
using System.Text.RegularExpressions;
namespace TianGongCadSuite {
    public static class LineupHierarchy {
        public static string Parent(LineupProject p,LineupRecord r){
            string id=r.Category=="气缸传感器"?r.CylinderId:r.Category=="气缸"?r.ValveId:r.IslandId;
            var parent=p.Records.FirstOrDefault(x=>x.Id==id);
            return parent!=null?parent.Number:r.Category=="阀片"?LineupNumbering.IslandKey(r.Number):"未归类";
        }
        public static void AssignCylinders(LineupProject p,IEnumerable<string> ids,string valveId){
            var valve=p.Records.Single(r=>r.Id==valveId&&r.Category=="阀片");
            var selected=p.Records.Where(r=>ids.Contains(r.Id)).ToArray();
            if(selected.Length==0||selected.Any(r=>r.Category!="气缸"))throw new InvalidOperationException("请只选择气缸行。");
            foreach(var r in selected){r.ValveId=valve.Id;r.IslandId=valve.IslandId;}
            RenumberChildren(p,valve);
            LineupTableData.RejectDuplicates(p.Records);
        }
        public static void RenumberChildren(LineupProject p,LineupRecord valve){
            var children=p.Records.Where(r=>r.Category=="气缸"&&r.ValveId==valve.Id).ToArray();
            if(children.Length==0)return;
            var match=Regex.Match(valve.Number??"",@"^(.+-Y[0-9]+)V([0-9]+)$",RegexOptions.IgnoreCase);
            if(!match.Success)throw new InvalidOperationException("阀片编号需要类似 OK170-Y1V01，才能联动气缸编号。");
            string stem=match.Groups[1].Value+"C"+match.Groups[2].Value;
            for(int i=0;i<children.Length;i++){
                var c=children[i];c.Number=stem+(children.Length>1?"-"+(i+1):"");c.IslandId=valve.IslandId;
                foreach(var s in p.Records.Where(r=>r.CylinderId==c.Id&&r.Category=="气缸传感器")){
                    if(s.Position!="H"&&s.Position!="W"&&s.Position!="M")throw new InvalidOperationException("传感器位置须为 H/W/M："+s.Number);
                    s.Number=c.Number+s.Position;s.ValveId=valve.Id;s.IslandId=valve.IslandId;
                }
            }
        }
        public static void Move(LineupProject p,IList<string> visible,IEnumerable<string> selection,int direction){
            var ids=new HashSet<string>(selection);var order=visible.ToList();
            if(direction<0){for(int i=1;i<order.Count;i++)if(ids.Contains(order[i])&&!ids.Contains(order[i-1])){string t=order[i-1];order[i-1]=order[i];order[i]=t;}}
            else{for(int i=order.Count-2;i>=0;i--)if(ids.Contains(order[i])&&!ids.Contains(order[i+1])){string t=order[i+1];order[i+1]=order[i];order[i]=t;}}
            var map=p.Records.ToDictionary(r=>r.Id);int n=0;var set=new HashSet<string>(visible);
            for(int i=0;i<p.Records.Count;i++)if(set.Contains(p.Records[i].Id))p.Records[i]=map[order[n++]];
        }
    }
}
