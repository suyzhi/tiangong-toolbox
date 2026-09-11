using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Serialization;
namespace TianGongCadSuite {
    public sealed class LineupRecord {
        public string Id=Guid.NewGuid().ToString("N");
        public string Number="",Category="",Address="",Parameters="",Description="",Model="",Brand="",Home="",Mounting="",Mechanism="",Connector="",Notes="";
        public string IslandId="",ValveId="",CylinderId="",Position="";
        public string[] InstancePath=new string[0];
        public string SourceFile="",ReferenceKey="";
        public override string ToString(){return Number+"  "+Description+"  "+Model;}
    }
    public sealed class LineupProject {
        public int Version=1;
        public string AssemblyFile="";
        public List<LineupRecord> Records=new List<LineupRecord>();
        public List<LineupSection> Sections=new List<LineupSection>();
        public string NumberPrefix="";
        public int ActiveIsland=1;
        public List<LineupIsland> Islands=new List<LineupIsland>();
        public List<LineupEntryPreset> EntryPresets=new List<LineupEntryPreset>();
        public void Save(string file) {
            var temporary=file+"."+Guid.NewGuid().ToString("N")+".tmp";
            try {
                using(var stream=new FileStream(temporary,FileMode.CreateNew))new XmlSerializer(typeof(LineupProject)).Serialize(stream,this);
                if(File.Exists(file))File.Replace(temporary,file,file+".bak");else File.Move(temporary,file);
            } finally {if(File.Exists(temporary))File.Delete(temporary);}
        }
        public static LineupProject Load(string file,string assembly) {
            LineupProject p;
            using(var stream=File.OpenRead(file))p=(LineupProject)new XmlSerializer(typeof(LineupProject)).Deserialize(stream);
            if(p.Version!=1)throw new InvalidDataException("不支持的 Lineup 文件版本。");
            if(!string.Equals(Path.GetFullPath(p.AssemblyFile),Path.GetFullPath(assembly),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Lineup 数据属于另一装配，请使用导入功能重新绑定模型。");
            return p;
        }
        public List<string> Validate() {
            var errors=new List<string>();
            foreach(var group in Records.GroupBy(r=>(r.Number??"").Trim(),StringComparer.OrdinalIgnoreCase)) {
                if(group.Key.Length==0)errors.Add("存在未编号记录。");
                else if(group.Count()>1)errors.Add("重复编号："+group.Key);
            }
            foreach(var r in Records) {
                CheckLink(r,r.IslandId,"阀岛",errors);CheckLink(r,r.ValveId,"阀片",errors);CheckLink(r,r.CylinderId,"气缸",errors);
                var valve=Records.FirstOrDefault(x=>x.Id==r.ValveId);
                if(valve!=null&&!string.IsNullOrEmpty(r.IslandId)&&valve.IslandId!=r.IslandId)errors.Add(r.Number+"：所属阀岛与阀片所属阀岛不一致。");
            }
            return errors;
        }
        void CheckLink(LineupRecord owner,string id,string category,List<string> errors) {
            if(string.IsNullOrEmpty(id))return;
            var target=Records.FirstOrDefault(x=>x.Id==id);
            if(target==null||target.Category!=category||target==owner)errors.Add(owner.Number+"：无效的"+category+"关联。");
        }
        public string NextNumber(string prefix) {
            var used=new HashSet<string>(Records.Select(x=>x.Number),StringComparer.OrdinalIgnoreCase);
            for(int i=1;i<int.MaxValue;i++){string n=prefix+i.ToString("D2");if(!used.Contains(n))return n;}
            throw new InvalidOperationException("编号已耗尽。");
        }
    }
    public static class LineupCsv {
        public static string Write(IEnumerable<string[]> rows) {
            return string.Join("\r\n",rows.Select(row=>string.Join(",",row.Select(cell=>"\""+(cell??"").Replace("\"","\"\"")+"\""))))+"\r\n";
        }
        public static List<string[]> Read(string text) {
            var rows=new List<string[]>();var row=new List<string>();var cell=new StringBuilder();bool quoted=false,closed=false;
            text=text.TrimStart('\uFEFF');
            for(int i=0;i<text.Length;i++) {
                char c=text[i];
                if(quoted){if(c=='"'){if(i+1<text.Length&&text[i+1]=='"'){cell.Append('"');i++;}else{quoted=false;closed=true;}}else cell.Append(c);continue;}
                if(c==','||c=='\r'||c=='\n'){
                    row.Add(cell.ToString());cell.Length=0;closed=false;
                    if(c!=','){rows.Add(row.ToArray());row.Clear();if(c=='\r'&&i+1<text.Length&&text[i+1]=='\n')i++;}
                }else if(c=='"'&&cell.Length==0&&!closed)quoted=true;
                else {if(closed||c=='"')throw new InvalidDataException("CSV 引号格式错误。");cell.Append(c);}
            }
            if(quoted)throw new InvalidDataException("CSV 存在未闭合引号。");
            if(cell.Length>0||row.Count>0||closed){row.Add(cell.ToString());rows.Add(row.ToArray());}
            return rows;
        }
    }
}
