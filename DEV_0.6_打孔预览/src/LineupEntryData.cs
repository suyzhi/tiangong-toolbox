using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
namespace TianGongCadSuite {
    // Only shared entry fields are remembered. IO, numbers and instance keys are never templates.
    public sealed class LineupEntryPreset {
        public string Category="气缸",Model="",Brand="",Connector="",Description="",Home="",Mounting="",Mechanism="";
        public string ParentId="",Position="H";
        public bool GenerateSensors;
        public LineupEntryPreset Copy(){return (LineupEntryPreset)MemberwiseClone();}
    }
    public sealed class LineupEntryResult {
        public LineupProject Project;
        public List<string> AddedIds=new List<string>();
        public int Skipped,SensorCount;
    }
    public static class LineupEntry {
        public static LineupProject Clone(LineupProject source){
            var serializer=new XmlSerializer(typeof(LineupProject));
            using(var stream=new MemoryStream()){serializer.Serialize(stream,source);stream.Position=0;return (LineupProject)serializer.Deserialize(stream);}
        }
        public static LineupEntryResult Create(LineupProject source,LineupEntryPreset preset,int quantity,IEnumerable<LineupRecord> selections){
            if(!LineupTableData.Categories.Contains(preset.Category))throw new InvalidOperationException("请先选择具体录入类目。");
            if(quantity<1||quantity>500)throw new InvalidOperationException("数量范围为 1～500。");
            var p=Clone(source);var result=new LineupEntryResult{Project=p};
            var captured=(selections??Enumerable.Empty<LineupRecord>()).ToList();
            bool binding=captured.Count>0;
            var keys=new HashSet<string>(p.Records.Where(r=>!string.IsNullOrEmpty(r.ReferenceKey)).Select(r=>r.ReferenceKey));
            var incoming=new List<LineupRecord>();
            foreach(var item in captured){if(string.IsNullOrEmpty(item.ReferenceKey))throw new InvalidOperationException("模型关联无效，本批次未录入。");if(keys.Add(item.ReferenceKey))incoming.Add(item);else result.Skipped++;}
            if(binding&&incoming.Count==0)return result;
            if(!binding)for(int i=0;i<quantity;i++)incoming.Add(new LineupRecord());
            LineupRecord parent=null;
            if(!string.IsNullOrEmpty(preset.ParentId)){
                string kind=preset.Category=="气缸"?"阀片":preset.Category=="气缸传感器"?"气缸":"";
                parent=p.Records.FirstOrDefault(r=>r.Id==preset.ParentId&&r.Category==kind);
                if(parent==null)throw new InvalidOperationException("录入区的上级已失效，请重新选择。");
            }
            if(preset.Category=="气缸传感器"&&(parent==null||incoming.Count!=1||!new[]{"H","W","M"}.Contains(preset.Position)))throw new InvalidOperationException("气缸传感器需选择所属气缸及 H/W/M 位置，每次录入一个；已有 H/W 请用连续绑定。");
            LineupRecord island=null;
            if(preset.Category=="阀片"){
                string key=p.NumberPrefix.Trim()+"-Y"+p.ActiveIsland;
                // Validate before adding an auxiliary parent.
                LineupNumbering.Next(p,"阀片");
                island=p.Records.FirstOrDefault(r=>r.Category=="阀岛"&&string.Equals(r.Number,key,StringComparison.OrdinalIgnoreCase));
                if(island==null){var config=p.Islands.FirstOrDefault(x=>string.Equals(x.Key,key,StringComparison.OrdinalIgnoreCase));island=new LineupRecord{Category="阀岛",Number=key,Model=config==null?"":config.Model};p.Records.Add(island);}
            }
            foreach(var capturedRow in incoming){
                var r=new LineupRecord{Category=preset.Category,Model=preset.Model.Trim(),Brand=preset.Brand.Trim(),Connector=preset.Connector.Trim(),Description=preset.Description,Home=preset.Home,Mounting=preset.Mounting,Mechanism=preset.Mechanism,ReferenceKey=capturedRow.ReferenceKey,SourceFile=capturedRow.SourceFile,InstancePath=(capturedRow.InstancePath??new string[0]).ToArray()};
                if(r.Model.Length==0&&!string.IsNullOrEmpty(r.SourceFile)){
                    var known=source.Records.Where(x=>x.Category==r.Category&&string.Equals(x.SourceFile,r.SourceFile,StringComparison.OrdinalIgnoreCase)&&!string.IsNullOrWhiteSpace(x.Model)).Select(x=>new{x.Model,x.Brand,x.Connector}).Distinct().ToList();
                    if(known.Count==1&&(r.Brand.Length==0||r.Brand==known[0].Brand)&&(r.Connector.Length==0||r.Connector==known[0].Connector)){r.Model=known[0].Model;r.Brand=known[0].Brand;r.Connector=known[0].Connector;}
                }
                if(r.Category=="气缸传感器"){
                    if(p.Records.Any(x=>x.CylinderId==parent.Id&&x.Position==preset.Position))throw new InvalidOperationException("该气缸已有 "+preset.Position+" 记录，请绑定已有行。");
                    if(string.IsNullOrWhiteSpace(parent.Number))throw new InvalidOperationException("所属气缸尚未编号。");
                    r.CylinderId=parent.Id;r.ValveId=parent.ValveId;r.IslandId=parent.IslandId;r.Position=preset.Position;r.Number=parent.Number+r.Position;
                }else if(r.Category=="气缸"&&parent!=null){r.ValveId=parent.Id;r.IslandId=parent.IslandId;}
                else r.Number=LineupNumbering.Next(p,r.Category);
                if(island!=null)r.IslandId=island.Id;
                p.Records.Add(r);result.AddedIds.Add(r.Id);
            }
            if(preset.Category=="气缸"&&parent!=null)LineupHierarchy.RenumberChildren(p,parent);
            if(preset.Category=="气缸"&&preset.GenerateSensors){
                var previous=new HashSet<string>(p.Records.Select(r=>r.Id));
                result.SensorCount=LineupNumbering.SensorPairs(p,result.AddedIds);
                var sensor=p.EntryPresets.LastOrDefault(x=>x.Category=="气缸传感器");
                if(sensor!=null)foreach(var r in p.Records.Where(x=>!previous.Contains(x.Id))){r.Model=sensor.Model;r.Brand=sensor.Brand;r.Connector=sensor.Connector;r.Mounting=sensor.Mounting;}
            }
            LineupTableData.RejectDuplicates(p.Records);
            p.EntryPresets.RemoveAll(x=>x.Category==preset.Category);p.EntryPresets.Add(preset.Copy());
            return result;
        }
        public static string NextUnbound(LineupProject p,IList<string> visible,string current){
            int index=visible.IndexOf(current);
            for(int i=Math.Max(0,index+1);i<visible.Count;i++){var r=p.Records.FirstOrDefault(x=>x.Id==visible[i]);if(r!=null&&string.IsNullOrEmpty(r.ReferenceKey))return r.Id;}
            return null;
        }
    }
}
