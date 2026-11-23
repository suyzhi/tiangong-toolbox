using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Serialization;
namespace TianGongCadSuite {
    public static class LineupPresets {
        public static readonly string[] Homes={"缩回","伸出","松开","关闭","非真空"};
        public static readonly string[] CylinderMountings={"水平安装","垂直朝上","垂直朝下","竖直向上","竖直向下"};
        public static readonly string[] ValveKinds={"阀岛","汇流排","阀组","单片阀"};
        public static readonly string[] Connectors={"M8/3针","M8/4针","M12/4针","螺丝端子 M20 x 1.5","2 m 导线引出型","3线式","散线/3针"};
        public static readonly string[] ValveDescriptions={"三位五通阀，中封式","三位五通中泄阀","二位五通阀，双电控","二位五通阀，单电控","二位三通阀","盖板"};
    }
    public sealed class LineupIsland {
        public string Key="",Model="",Kind="阀岛";
        public LineupIsland Copy(){return new LineupIsland{Key=Key??"",Model=Model??"",Kind=string.IsNullOrWhiteSpace(Kind)?"阀岛":Kind};}
    }
    public static class LineupNumbering {
        public static string Next(LineupProject p,string category){
            string prefix;
            if(category=="气缸"||category=="阀片"){
                if(string.IsNullOrWhiteSpace(p.NumberPrefix)||p.ActiveIsland<1)throw new InvalidOperationException("请先在“编号与阀岛”设置项目号和阀岛数字。");
                prefix=p.NumberPrefix.Trim()+"-Y"+p.ActiveIsland+(category=="气缸"?"C":"V");
            }else if(category=="普通传感器")prefix="SE";else if(category=="其他电气件")prefix="E";
            else throw new InvalidOperationException("气缸传感器请从气缸生成 H/W；辅助类目保留手动编号。");
            var pattern=new Regex("^"+Regex.Escape(prefix)+"([0-9]+)(?:-[0-9]+|[a-z])?$",RegexOptions.IgnoreCase);int max=0;
            foreach(var r in p.Records){var m=pattern.Match((r.Number??"").Trim());int value;if(m.Success&&int.TryParse(m.Groups[1].Value,out value))max=Math.Max(max,value);}
            if(max==int.MaxValue)throw new InvalidOperationException("编号已耗尽。");
            return prefix+(max+1).ToString("D2");
        }
        public static string IslandKey(string number){
            var m=Regex.Match((number??"").Trim(),@"^(.+-Y[0-9]+)[CV][0-9]+(?:-[0-9]+)?[a-z]?(?:[HWM][0-9]*)?$",RegexOptions.IgnoreCase);
            return m.Success?m.Groups[1].Value:"";
        }
        public static LineupIsland IslandEntry(LineupProject p,string key){
            if(string.IsNullOrEmpty(key)||p==null||p.Islands==null)return null;
            return p.Islands.FirstOrDefault(i=>string.Equals(i.Key,key,StringComparison.OrdinalIgnoreCase));
        }
        public static string IslandModel(LineupProject p,LineupRecord r){
            if(r.Category!="阀片")return "";
            var island=IslandEntry(p,IslandKey(r.Number));
            return island==null?"":(island.Model??"");
        }
        public static string IslandLabel(LineupProject p,LineupRecord r){
            if(r.Category!="阀片")return "";
            var island=IslandEntry(p,IslandKey(r.Number));
            if(island==null)return "";
            string kind=string.IsNullOrWhiteSpace(island.Kind)?"阀岛":island.Kind.Trim();
            string model=(island.Model??"").Trim();
            if(kind=="单片阀")return model.Length==0?"单片阀":"单片阀："+model;
            return model.Length==0?"":kind+"："+model;
        }
        public static void SetIsland(LineupProject p,string prefix,int island,string model){
            SetIsland(p,prefix,island,model,null);
        }
        public static void SetIsland(LineupProject p,string prefix,int island,string model,string kind){
            prefix=(prefix??"").Trim();if(prefix.Length==0||prefix.IndexOfAny(new[]{'\r','\n','\t'})>=0||island<1)throw new InvalidOperationException("项目号不能为空，阀岛数字必须大于 0。");
            p.NumberPrefix=prefix;p.ActiveIsland=island;if(p.Islands==null)p.Islands=new List<LineupIsland>();
            string key=prefix+"-Y"+island;var entry=p.Islands.FirstOrDefault(x=>string.Equals(x.Key,key,StringComparison.OrdinalIgnoreCase));
            if(entry==null){entry=new LineupIsland{Key=key,Kind="阀岛"};p.Islands.Add(entry);}
            entry.Model=model??"";
            if(!string.IsNullOrWhiteSpace(kind))entry.Kind=kind.Trim();
            else if(string.IsNullOrWhiteSpace(entry.Kind))entry.Kind="阀岛";
        }
        // 根据气缸编号推导配套阀片编号：OK170-Y1C05-1 → OK170-Y1V05，OK270-Y2C02a → OK270-Y2V02
        public static string ValveNumberForCylinder(string cylinderNumber){
            var m=Regex.Match((cylinderNumber??"").Trim(),@"^(.+-Y[0-9]+)C([0-9]+)(?:-[0-9]+)?[a-z]?$",RegexOptions.IgnoreCase);
            return m.Success?m.Groups[1].Value+"V"+m.Groups[2].Value:"";
        }
        // 从所选气缸一键生成对应阀片并建立关联；同组 -1/-2 气缸共用同一阀片，阀片功能参数自动带入气缸描述
        public static int GenerateValves(LineupProject p,IEnumerable<string> cylinderIds){
            int added=0;
            var cylinders=cylinderIds.Distinct().Select(id=>p.Records.FirstOrDefault(r=>r.Id==id&&r.Category=="气缸")).Where(r=>r!=null).ToList();
            if(cylinders.Count==0)throw new InvalidOperationException("请先选择已编号的气缸。");
            foreach(var group in cylinders.GroupBy(c=>ValveNumberForCylinder(c.Number),StringComparer.OrdinalIgnoreCase)){
                string valveNum=group.Key;
                if(string.IsNullOrEmpty(valveNum))continue;
                var first=group.First();
                string ikey=IslandKey(valveNum);
                LineupRecord islandRec=null;
                if(ikey.Length>0){
                    islandRec=p.Records.FirstOrDefault(r=>r.Category=="阀岛"&&string.Equals(r.Number,ikey,StringComparison.OrdinalIgnoreCase));
                    if(islandRec==null){
                        var cfg=IslandEntry(p,ikey);
                        if(cfg!=null&&!string.IsNullOrWhiteSpace(cfg.Model)){islandRec=new LineupRecord{Category="阀岛",Number=ikey,Model=cfg.Model};p.Records.Add(islandRec);}
                    }
                }
                var valve=p.Records.FirstOrDefault(r=>r.Category=="阀片"&&string.Equals((r.Number??"").Trim(),valveNum,StringComparison.OrdinalIgnoreCase));
                if(valve==null){
                    if(p.Records.Any(r=>string.Equals((r.Number??"").Trim(),valveNum,StringComparison.OrdinalIgnoreCase)))throw new InvalidOperationException("编号已被其他类别占用："+valveNum);
                    var preset=p.EntryPresets==null?null:p.EntryPresets.LastOrDefault(x=>x.Category=="阀片");
                    string func=string.Join(" / ",group.Select(c=>(c.Description??"").Trim()).Where(s=>s.Length>0).Distinct());
                    valve=new LineupRecord{
                        Number=valveNum,Category="阀片",Parameters=func,
                        Description=preset!=null&&!string.IsNullOrWhiteSpace(preset.Description)?preset.Description:"三位五通阀，中封式",
                        Model=preset!=null?preset.Model:"",Brand=preset!=null?preset.Brand:"",
                        Mounting=preset!=null?preset.Mounting:"",
                        Mechanism=first.Mechanism,Notes=first.Notes,
                        IslandId=islandRec==null?"":islandRec.Id
                    };
                    p.Records.Add(valve);added++;
                }else{
                    if(string.IsNullOrWhiteSpace(valve.Parameters)&&!string.IsNullOrWhiteSpace(first.Description))valve.Parameters=first.Description;
                    if(string.IsNullOrWhiteSpace(valve.Notes)&&!string.IsNullOrWhiteSpace(first.Notes))valve.Notes=first.Notes;
                }
                foreach(var cyl in group){
                    cyl.ValveId=valve.Id;
                    if(!string.IsNullOrEmpty(valve.IslandId))cyl.IslandId=valve.IslandId;
                    foreach(var sen in p.Records.Where(r=>r.Category=="气缸传感器"&&r.CylinderId==cyl.Id)){
                        sen.ValveId=valve.Id;if(!string.IsNullOrEmpty(valve.IslandId))sen.IslandId=valve.IslandId;
                    }
                }
            }
            return added;
        }
        public static int SensorPairs(LineupProject p,IEnumerable<string> cylinderIds){
            return SensorPairsMulti(p,cylinderIds,1);
        }
        // pairCount=1 → H / W；pairCount=2 → H1 / W1 / H2 / W2（线体大行程提升气缸常用双组传感器）
        public static int SensorPairsMulti(LineupProject p,IEnumerable<string> cylinderIds,int pairCount){
            if(pairCount<1||pairCount>4)throw new ArgumentOutOfRangeException("pairCount");
            int added=0;foreach(string id in cylinderIds.Distinct()){
                var cylinder=p.Records.FirstOrDefault(r=>r.Id==id&&r.Category=="气缸");
                if(cylinder==null||string.IsNullOrWhiteSpace(cylinder.Number))throw new InvalidOperationException("请先选择已编号的气缸。");
                var suffixes=new List<KeyValuePair<string,string>>();
                if(pairCount==1){suffixes.Add(new KeyValuePair<string,string>("H","原位"));suffixes.Add(new KeyValuePair<string,string>("W","工作位"));}
                else for(int k=1;k<=pairCount;k++){suffixes.Add(new KeyValuePair<string,string>("H"+k,"原位"+k));suffixes.Add(new KeyValuePair<string,string>("W"+k,"工作位"+k));}
                foreach(var pair in suffixes){
                    string position=pair.Key,label=pair.Value;
                    string number=cylinder.Number.Trim()+position;
                    if(p.Records.Any(r=>r.CylinderId==id&&string.Equals(r.Position,position,StringComparison.OrdinalIgnoreCase)))continue;
                    if(p.Records.Any(r=>string.Equals((r.Number??"").Trim(),number,StringComparison.OrdinalIgnoreCase)))throw new InvalidOperationException("编号已被其他记录占用："+number);
                    p.Records.Add(new LineupRecord{
                        Number=number,Category="气缸传感器",CylinderId=id,ValveId=cylinder.ValveId,IslandId=cylinder.IslandId,
                        Position=position,Description=(cylinder.Description??"")+label,Mechanism=cylinder.Mechanism,Notes=cylinder.Notes
                    });
                    added++;
                }
            }return added;
        }
        public static void FillMissing(LineupProject p,IEnumerable<LineupRecord> records){
            foreach(var r in records.Where(r=>string.IsNullOrWhiteSpace(r.Number))){
                if(r.Category=="气缸传感器"){
                    var parent=p.Records.FirstOrDefault(x=>x.Id==r.CylinderId&&x.Category=="气缸");
                    if(parent!=null&&!string.IsNullOrWhiteSpace(parent.Number)&&!string.IsNullOrWhiteSpace(r.Position))r.Number=parent.Number+r.Position;
                }else if(LineupTableData.Categories.Contains(r.Category))r.Number=Next(p,r.Category);
            }LineupTableData.RejectDuplicates(p.Records);
        }
    }
    public sealed class LineupModelEntry {
        public string Category="",Model="",Brand="",Connector="";
        public override string ToString(){return Model+" · "+Brand+(Connector.Length==0?"":" · "+Connector);}
    }
    public sealed class LineupModelCatalog {
        public const string SeedResource="TianGongCadSuite.Lineup.lineup-models-seed.xml";
        public List<LineupModelEntry> Entries=new List<LineupModelEntry>();
        static readonly XmlSerializer CatalogSerializer=new XmlSerializer(typeof(LineupModelCatalog));
        public static string UserFile {get{return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"TianGongCadSuite","lineup-models.xml");}}
        public static LineupModelCatalog Load(string file){if(!File.Exists(file))return new LineupModelCatalog();using(var s=File.OpenRead(file))return (LineupModelCatalog)CatalogSerializer.Deserialize(s);}
        public static LineupModelCatalog LoadSeed(string diskPath){
            if(!string.IsNullOrEmpty(diskPath)&&File.Exists(diskPath))return Load(diskPath);
            using(var s=typeof(LineupModelCatalog).Assembly.GetManifestResourceStream(SeedResource)){
                if(s!=null)return (LineupModelCatalog)CatalogSerializer.Deserialize(s);
            }
            return new LineupModelCatalog();
        }
        public void Save(string file){
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file)));string tmp=file+"."+Guid.NewGuid().ToString("N")+".tmp";
            try{using(var s=File.Create(tmp))CatalogSerializer.Serialize(s,this);if(File.Exists(file))File.Replace(tmp,file,file+".bak");else File.Move(tmp,file);}finally{if(File.Exists(tmp))File.Delete(tmp);}
        }
        static bool Same(string a,string b){return string.Equals((a??"").Trim(),(b??"").Trim(),StringComparison.OrdinalIgnoreCase);}
        static string Norm(string s){
            if(string.IsNullOrEmpty(s))return "";
            string t=Regex.Replace(s.Trim(),@"\(\s*0[_,]0\s*\)|_0$|-\s*副本.*$","",RegexOptions.IgnoreCase);
            return Regex.Replace(t,@"[\s_\-]+","").ToUpperInvariant();
        }
        // 根据 CAD 零件文件名或属性名反查最匹配的型号库条目（忽略下划线/连字符/(0_0) 差异）
        public LineupModelEntry MatchByFileName(string fileNameOrTitle,string preferredCategory=null){
            string stem=Norm(Path.GetFileNameWithoutExtension(fileNameOrTitle??""));
            if(stem.Length<4)return null;
            var candidates=Entries.Where(e=>e.Category!="阀岛"&&!string.IsNullOrWhiteSpace(e.Model)).ToList();
            if(!string.IsNullOrEmpty(preferredCategory)){
                var exact=MatchInList(stem,candidates.Where(e=>Same(e.Category,preferredCategory)));
                if(exact!=null)return exact;
            }
            return MatchInList(stem,candidates);
        }
        static LineupModelEntry MatchInList(string normStem,IEnumerable<LineupModelEntry> list){
            LineupModelEntry best=null;int bestLen=0;
            foreach(var e in list){
                string nm=Norm(e.Model);if(nm.Length<4)continue;
                if(normStem==nm)return e;
                if(normStem.Contains(nm)&&nm.Length>bestLen){best=e;bestLen=nm.Length;}
                else if(nm.Contains(normStem)&&normStem.Length>=6&&normStem.Length>bestLen){best=e;bestLen=normStem.Length;}
            }
            return best;
        }
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
