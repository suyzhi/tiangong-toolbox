using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
namespace TianGongCadSuite {
    public sealed class LineupSection {public string Category="";public string[] Cells=new string[0];}
    public sealed class LineupImport {
        public List<LineupRecord> Records=new List<LineupRecord>();
        public List<LineupSection> Sections=new List<LineupSection>();
    }
    public static class LineupTableData {
        public static readonly string[] Headers={"编号","地址","功能参数","描述","型号","品牌","原位","安装方式","机构名称","传感器接口类型","备注"};
        public static readonly string[] ReferenceHeaders=Headers.Where(h=>h!="机构名称").ToArray();
        public static List<string[]> ReferenceGrouped(LineupProject project){return Grouped(project).Select(row=>row.Where((value,index)=>index!=8).ToArray()).ToList();}
        public static readonly string[] Categories={"气缸","阀片","气缸传感器","普通传感器","其他电气件"};
        public static readonly string[] Titles={
            "CYLINDER LIST (Y*C**)  气缸列表",
            "PNEUMATIC SOLENOID/VALVE LIST (Y*V**)  气动线圈/阀片列表 (气动填写) A是工作位,B是原位",
            "CYLINDER POSITION SENSOR LIST (原位Y*C**H or 工作位CYLY*C**W or 中间位Y*C**M)  气缸位置传感器列表,I小号缩回,大号伸出",
            "GENERAL CHECK SENSOR LIST (SE**)  普通检测类传感器列表（除气缸位置检测之外的传感器）",
            "OTHER ELECTRICAL PARTS LIST （E**）其他电气器件      编号和地址由电气填写"};
        static readonly string[] Prefixes={"CYLINDER LIST","PNEUMATIC SOLENOID/VALVE LIST","CYLINDER POSITION SENSOR LIST","GENERAL CHECK SENSOR LIST","OTHER ELECTRICAL PARTS LIST"};
        public static string Category(string value){return value=="传感器"?"普通传感器":value=="其他"?"其他电气件":value??"";}
        public static string[] Cells(LineupRecord r){return new[]{r.Number,r.Address,r.Parameters,r.Description,r.Model,r.Brand,r.Home,r.Mounting,r.Mechanism,r.Connector,r.Notes};}
        public static LineupRecord Copy(LineupRecord r){
            return new LineupRecord{Id=r.Id,Category=r.Category,Number=r.Number,Address=r.Address,Parameters=r.Parameters,Description=r.Description,Model=r.Model,Brand=r.Brand,Home=r.Home,Mounting=r.Mounting,Mechanism=r.Mechanism,Connector=r.Connector,Notes=r.Notes,IslandId=r.IslandId,ValveId=r.ValveId,CylinderId=r.CylinderId,Position=r.Position,SourceFile=r.SourceFile,ReferenceKey=r.ReferenceKey,InstancePath=r.InstancePath==null?new string[0]:(string[])r.InstancePath.Clone()};
        }
        public static void SetCells(LineupRecord r,string[] c){r.Number=c[0];r.Address=c[1];r.Parameters=c[2];r.Description=c[3];r.Model=c[4];r.Brand=c[5];r.Home=c[6];r.Mounting=c[7];r.Mechanism=c[8];r.Connector=c[9];r.Notes=c[10];}
        public static List<string[]> ReadDelimited(string text,char separator){
            var rows=new List<string[]>();var row=new List<string>();var cell=new StringBuilder();bool quoted=false,closed=false;
            text=(text??"").TrimStart('\uFEFF');
            for(int i=0;i<text.Length;i++){
                char c=text[i];
                if(quoted){if(c=='"'){if(i+1<text.Length&&text[i+1]=='"'){cell.Append('"');i++;}else{quoted=false;closed=true;}}else cell.Append(c);continue;}
                if(c==separator||c=='\r'||c=='\n'){row.Add(cell.ToString());cell.Clear();closed=false;if(c!=separator){rows.Add(row.ToArray());row.Clear();if(c=='\r'&&i+1<text.Length&&text[i+1]=='\n')i++;}}
                else if(c=='"'&&cell.Length==0&&!closed)quoted=true;
                else{if(closed)throw new InvalidDataException("引号结束后出现多余内容。");cell.Append(c);}
            }
            if(quoted)throw new InvalidDataException("粘贴内容存在未闭合引号。");
            if(cell.Length>0||row.Count>0||closed){row.Add(cell.ToString());rows.Add(row.ToArray());}return rows;
        }
        public static LineupImport Parse(string text,string defaultCategory,char separator){
            var result=new LineupImport();string category=defaultCategory;int[] mapping=Enumerable.Range(0,11).ToArray();int line=0;
            foreach(var row in ReadDelimited(text,separator)){
                line++;if(row.All(string.IsNullOrWhiteSpace))continue;
                int section=Array.FindIndex(Prefixes,p=>row[0].Trim().StartsWith(p,StringComparison.OrdinalIgnoreCase));
                if(section>=0){category=Categories[section];var sectionCells=Pad(row,line);result.Sections.Add(new LineupSection{Category=category,Cells=mapping.Select(index=>index<0?"":sectionCells[index]).ToArray()});continue;}
                if(row.Contains("编号")&&row.Contains("型号")){
                    var expected=row.Length==10?ReferenceHeaders:Headers;
                    if(row.Length!=expected.Length||expected.Any(h=>row.Count(x=>x.Trim()==h)!=1))throw new InvalidDataException("表头需要完整且不重复的 10 列参考BOM，或含机构名称的 11 列清单。");
                    mapping=Headers.Select(h=>Array.FindIndex(row,x=>x.Trim()==h)).ToArray();continue;
                }
                if(!Categories.Contains(category))throw new InvalidDataException("第 "+line+" 行没有五大类目，请选择类目或粘贴分类标题。");
                var source=Pad(row,line);var record=new LineupRecord{Category=category};
                SetCells(record,mapping.Select(index=>index<0?"":source[index]).ToArray());result.Records.Add(record);
            }
            RejectDuplicates(result.Records);return result;
        }
        static string[] Pad(string[] row,int line){if(row.Length>11)throw new InvalidDataException("第 "+line+" 行超过 11 列，请只复制样表的业务列。");return Enumerable.Range(0,11).Select(i=>i<row.Length?row[i]:"").ToArray();}
        public static void RejectDuplicates(IEnumerable<LineupRecord> records){
            var duplicates=records.Where(r=>!string.IsNullOrWhiteSpace(r.Number)).GroupBy(r=>r.Number.Trim(),StringComparer.OrdinalIgnoreCase).Where(g=>g.Count()>1).Select(g=>g.Key).ToArray();
            if(duplicates.Length>0)throw new InvalidDataException("编号重复，未应用此次操作："+string.Join("、",duplicates.Take(12)));
        }
        public static List<string[]> Grouped(LineupProject project){
            var rows=new List<string[]>{Headers};
            foreach(string category in Categories.Concat(project.Records.Select(r=>Category(r.Category)).Where(c=>!Categories.Contains(c)).Distinct())){
                int index=Array.IndexOf(Categories,category);
                var saved=(project.Sections??new List<LineupSection>()).LastOrDefault(s=>s.Category==category);
                var header=saved==null?Enumerable.Repeat("",11).ToArray():(string[])saved.Cells.Clone();
                if(saved==null){header[0]=index>=0?Titles[index]:"未分类/辅助记录："+category;if(category=="普通传感器")header[6]="传感器常开/常闭（只有常闭的需要填写）";}
                rows.Add(header);
                var displayedIslands=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach(var r in project.Records.Where(r=>Category(r.Category)==category)){
                    var cells=Cells(r);string model=LineupNumbering.IslandModel(project,r),key=LineupNumbering.IslandKey(r.Number);
                    if(!string.IsNullOrWhiteSpace(model)){
                        // A configured island model is the authority; preserve unrelated mounting text.
                        cells[7]=System.Text.RegularExpressions.Regex.Replace(cells[7]??"", @"(?:阀岛|阀组)：[^\r\n]*","").Trim();
                        if(displayedIslands.Add(key))cells[7]=(cells[7].Length>0?cells[7]+"\n":"")+"阀岛："+model;
                    }rows.Add(cells);
                }
                rows.Add(Enumerable.Repeat("",11).ToArray());
            }return rows;
        }
        public static string Tsv(IEnumerable<string[]> rows){return string.Join("\r\n",rows.Select(r=>string.Join("\t",r.Select(c=>{c=c??"";return c.IndexOfAny(new[]{'\t','\r','\n','"'})<0?c:"\""+c.Replace("\"","\"\"")+"\"";}))))+"\r\n";}
    }
}
