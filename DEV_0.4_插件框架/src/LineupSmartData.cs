using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Xml.Serialization;
namespace TianGongCadSuite {
    public sealed class LineupIsland {public string Key="",Model="";}
    public static class LineupNumbering {
        public static string Next(LineupProject p,string category){
            string prefix;
            if(category=="气缸"||category=="阀片"){
                if(string.IsNullOrWhiteSpace(p.NumberPrefix)||p.ActiveIsland<1)throw new InvalidOperationException("请先在“编号与阀岛”设置项目号和阀岛数字。");
                prefix=p.NumberPrefix.Trim()+"-Y"+p.ActiveIsland+(category=="气缸"?"C":"V");
            }else if(category=="普通传感器")prefix="SE";else if(category=="其他电气件")prefix="E";
            else throw new InvalidOperationException("气缸传感器请从气缸生成 H/W；辅助类目保留手动编号。");
            var pattern=new Regex("^"+Regex.Escape(prefix)+"([0-9]+)(?:-[0-9]+)?$",RegexOptions.IgnoreCase);int max=0;
            foreach(var r in p.Records){var m=pattern.Match((r.Number??"").Trim());int value;if(m.Success&&int.TryParse(m.Groups[1].Value,out value))max=Math.Max(max,value);}
            if(max==int.MaxValue)throw new InvalidOperationException("编号已耗尽。");
            return prefix+(max+1).ToString("D2");
        }
        public static string IslandKey(string number){var m=Regex.Match((number??"").Trim(),@"^(.+-Y[0-9]+)[CV][0-9]+(?:-[0-9]+)?$",RegexOptions.IgnoreCase);return m.Success?m.Groups[1].Value:"";}
        public static string IslandModel(LineupProject p,LineupRecord r){if(r.Category!="阀片")return "";string key=IslandKey(r.Number);var island=(p.Islands??new List<LineupIsland>()).FirstOrDefault(i=>string.Equals(i.Key,key,StringComparison.OrdinalIgnoreCase));return island==null?"":island.Model;}
        public static void SetIsland(LineupProject p,string prefix,int island,string model){
            prefix=(prefix??"").Trim();if(prefix.Length==0||prefix.IndexOfAny(new[]{'\r','\n','\t'})>=0||island<1)throw new InvalidOperationException("项目号不能为空，阀岛数字必须大于 0。");
            p.NumberPrefix=prefix;p.ActiveIsland=island;if(p.Islands==null)p.Islands=new List<LineupIsland>();
            string key=prefix+"-Y"+island;var entry=p.Islands.FirstOrDefault(x=>string.Equals(x.Key,key,StringComparison.OrdinalIgnoreCase));
            if(entry==null){entry=new LineupIsland{Key=key};p.Islands.Add(entry);}entry.Model=model??"";
        }
        public static int SensorPairs(LineupProject p,IEnumerable<string> cylinderIds){
            int added=0;foreach(string id in cylinderIds.Distinct()){
                var cylinder=p.Records.FirstOrDefault(r=>r.Id==id&&r.Category=="气缸");
                if(cylinder==null||string.IsNullOrWhiteSpace(cylinder.Number))throw new InvalidOperationException("请先选择已编号的气缸。");
                foreach(string position in new[]{"H","W"}){
                    string number=cylinder.Number.Trim()+position;
                    if(p.Records.Any(r=>r.CylinderId==id&&r.Position==position))continue;
                    if(p.Records.Any(r=>string.Equals((r.Number??"").Trim(),number,StringComparison.OrdinalIgnoreCase)))throw new InvalidOperationException("编号已被其他记录占用："+number);
                    p.Records.Add(new LineupRecord{Number=number,Category="气缸传感器",CylinderId=id,ValveId=cylinder.ValveId,IslandId=cylinder.IslandId,Position=position,Description=cylinder.Description+(position=="H"?"原位":"工作位"),Mechanism=cylinder.Mechanism});added++;
                }
            }return added;
        }
        public static void FillMissing(LineupProject p,IEnumerable<LineupRecord> records){
            foreach(var r in records.Where(r=>string.IsNullOrWhiteSpace(r.Number))){
                if(r.Category=="气缸传感器"){
                    var parent=p.Records.FirstOrDefault(x=>x.Id==r.CylinderId&&x.Category=="气缸");
                    if(parent!=null&&!string.IsNullOrWhiteSpace(parent.Number)&&(r.Position=="H"||r.Position=="W"))r.Number=parent.Number+r.Position;
                }else if(LineupTableData.Categories.Contains(r.Category))r.Number=Next(p,r.Category);
            }LineupTableData.RejectDuplicates(p.Records);
        }
    }
    public sealed class LineupModelEntry {
        public string Category="",Model="",Brand="",Connector="";
        public override string ToString(){return Model+" · "+Brand+(Connector.Length==0?"":" · "+Connector);}
    }
    public sealed class LineupModelCatalog {
        public List<LineupModelEntry> Entries=new List<LineupModelEntry>();
        public static string UserFile {get{return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"TianGongCadSuite","lineup-models.xml");}}
        public static LineupModelCatalog Load(string file){if(!File.Exists(file))return new LineupModelCatalog();using(var s=File.OpenRead(file))return (LineupModelCatalog)new XmlSerializer(typeof(LineupModelCatalog)).Deserialize(s);}
        public void Save(string file){
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file)));string tmp=file+"."+Guid.NewGuid().ToString("N")+".tmp";
            try{using(var s=File.Create(tmp))new XmlSerializer(typeof(LineupModelCatalog)).Serialize(s,this);if(File.Exists(file))File.Replace(tmp,file,file+".bak");else File.Move(tmp,file);}finally{if(File.Exists(tmp))File.Delete(tmp);}
        }
        static bool Same(string a,string b){return string.Equals((a??"").Trim(),(b??"").Trim(),StringComparison.OrdinalIgnoreCase);}
        public int Learn(IEnumerable<LineupRecord> records){
            int count=0;foreach(var r in records){
                string category=LineupTableData.Category(r.Category);
                if(string.IsNullOrWhiteSpace(r.Model)||string.IsNullOrWhiteSpace(r.Brand))continue;
                if((category=="气缸传感器"||category=="普通传感器")&&string.IsNullOrWhiteSpace(r.Connector))continue;
                if(Entries.Any(e=>Same(e.Category,category)&&Same(e.Model,r.Model)&&Same(e.Brand,r.Brand)&&Same(e.Connector,r.Connector)))continue;
                Entries.Add(new LineupModelEntry{Category=category,Model=r.Model.Trim(),Brand=r.Brand.Trim(),Connector=(r.Connector??"").Trim()});count++;
            }return count;
        }
        public IEnumerable<LineupModelEntry> Match(string category,string model){return Entries.Where(e=>Same(e.Category,category)&&Same(e.Model,model));}
        public void Merge(LineupModelCatalog other){foreach(var e in other.Entries)Learn(new[]{new LineupRecord{Category=e.Category,Model=e.Model,Brand=e.Brand,Connector=e.Connector}});}
        public static LineupModelCatalog Import(string text,char delimiter,string defaultCategory){
            var catalog=new LineupModelCatalog();var rows=LineupTableData.ReadDelimited(text,delimiter).Where(r=>r.Any(c=>!string.IsNullOrWhiteSpace(c))).ToList();if(rows.Count==0)return catalog;
            var header=rows[0];int model=Array.IndexOf(header,"型号"),brand=Array.IndexOf(header,"品牌"),connector=Array.IndexOf(header,"传感器接口类型");if(connector<0)connector=Array.IndexOf(header,"接口类型");
            int category=Array.IndexOf(header,"类别");
            if(header.Contains("编号")){catalog.Learn(LineupTableData.Parse(text,defaultCategory,delimiter).Records);return catalog;}
            if(model<0||brand<0)throw new InvalidDataException("型号库表头至少包含：型号、品牌；传感器还需要接口类型。可加类别列。");
            for(int i=1;i<rows.Count;i++){var row=rows[i];Func<int,string> at=index=>index>=0&&index<row.Length?row[index]:"";string kind=category<0?defaultCategory:at(category);if(!LineupTableData.Categories.Contains(kind)&&kind!="阀岛")throw new InvalidDataException("型号库第 "+(i+1)+" 行的类别无效。");var record=new LineupRecord{Category=kind,Model=at(model),Brand=at(brand),Connector=at(connector)};if(string.IsNullOrWhiteSpace(record.Model)||string.IsNullOrWhiteSpace(record.Brand)||((kind=="气缸传感器"||kind=="普通传感器")&&string.IsNullOrWhiteSpace(record.Connector)))throw new InvalidDataException("型号库第 "+(i+1)+" 行字段不完整。");catalog.Learn(new[]{record});}
            return catalog;
        }
    }
}
