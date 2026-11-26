using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Serialization;
namespace TianGongCadSuite {
    // XML 1.0 控制字符清洗器：
    // XmlSerializer.Serialize(Stream) 默认使用 XmlTextWriter，不会拦截 0x01~0x1F（除 \t/\r/\n）并写成 &#xB;，
    // 下次 Deserialize(Stream) 启用 CheckCharacters=true 会直接抛 XmlException 导致整个项目/型号库无法打开。
    internal static class LineupXmlSanitizer {
        public static string Clean(string s) {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            bool dirty = false;
            for (int i = 0; i < s.Length; i++) {
                char ch = s[i];
                if (IsValidXmlChar(ch)) continue;
                if (char.IsHighSurrogate(ch) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { i++; continue; }
                dirty = true;
                break;
            }
            if (!dirty) return s;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++) {
                char ch = s[i];
                if (IsValidXmlChar(ch)) {
                    sb.Append(ch);
                } else if (char.IsHighSurrogate(ch) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) {
                    sb.Append(ch);
                    sb.Append(s[++i]);
                }
            }
            return sb.ToString();
        }

        static bool IsValidXmlChar(char ch) {
            return ch == 0x9 || ch == 0xA || ch == 0xD
                || (ch >= 0x20 && ch <= 0xD7FF)
                || (ch >= 0xE000 && ch <= 0xFFFD);
        }

        public static void CleanRecord(LineupRecord r) {
            if (r == null) return;
            r.Id = Clean(r.Id);
            r.Number = Clean(r.Number);
            r.Category = Clean(r.Category);
            r.Address = Clean(r.Address);
            r.Parameters = Clean(r.Parameters);
            r.Description = Clean(r.Description);
            r.Model = Clean(r.Model);
            r.Brand = Clean(r.Brand);
            r.Home = Clean(r.Home);
            r.Mounting = Clean(r.Mounting);
            r.Mechanism = Clean(r.Mechanism);
            r.Connector = Clean(r.Connector);
            r.Notes = Clean(r.Notes);
            r.IslandId = Clean(r.IslandId);
            r.ValveId = Clean(r.ValveId);
            r.CylinderId = Clean(r.CylinderId);
            r.Position = Clean(r.Position);
            r.SourceFile = Clean(r.SourceFile);
            r.ReferenceKey = Clean(r.ReferenceKey);
            if (r.InstancePath != null) {
                for (int i = 0; i < r.InstancePath.Length; i++) r.InstancePath[i] = Clean(r.InstancePath[i]);
            }
        }

        public static void CleanProjectInPlace(LineupProject p) {
            if (p == null) return;
            p.AssemblyFile = Clean(p.AssemblyFile);
            p.NumberPrefix = Clean(p.NumberPrefix);
            p.ProjectCode = Clean(p.ProjectCode);
            p.StationCode = Clean(p.StationCode);
            p.LineCode = Clean(p.LineCode);
            p.Designer = Clean(p.Designer);
            p.Editor = Clean(p.Editor);
            p.Revision = Clean(p.Revision);
            if (p.Records != null) {
                for (int i = 0; i < p.Records.Count; i++) CleanRecord(p.Records[i]);
            }
            if (p.Sections != null) {
                for (int i = 0; i < p.Sections.Count; i++) {
                    var s = p.Sections[i];
                    if (s == null) continue;
                    s.Category = Clean(s.Category);
                    if (s.Cells != null) {
                        for (int j = 0; j < s.Cells.Length; j++) s.Cells[j] = Clean(s.Cells[j]);
                    }
                }
            }
            if (p.Islands != null) {
                for (int i = 0; i < p.Islands.Count; i++) {
                    var isl = p.Islands[i];
                    if (isl == null) continue;
                    isl.Key = Clean(isl.Key);
                    isl.Model = Clean(isl.Model);
                    isl.Kind = Clean(isl.Kind);
                }
            }
            if (p.EntryPresets != null) {
                for (int i = 0; i < p.EntryPresets.Count; i++) {
                    var ep = p.EntryPresets[i];
                    if (ep == null) continue;
                    ep.Category = Clean(ep.Category);
                    ep.Model = Clean(ep.Model);
                    ep.Brand = Clean(ep.Brand);
                    ep.Connector = Clean(ep.Connector);
                    ep.Description = Clean(ep.Description);
                    ep.Home = Clean(ep.Home);
                    ep.Mounting = Clean(ep.Mounting);
                    ep.Mechanism = Clean(ep.Mechanism);
                    ep.ParentId = Clean(ep.ParentId);
                    ep.Position = Clean(ep.Position);
                }
            }
            if (p.CadPages != null) {
                for (int i = 0; i < p.CadPages.Count; i++) {
                    var pg = p.CadPages[i];
                    if (pg == null) continue;
                    pg.Id = Clean(pg.Id);
                    pg.Name = Clean(pg.Name);
                    pg.LineCode = Clean(pg.LineCode);
                    pg.ImageFile = Clean(pg.ImageFile);
                    pg.ViewMode = Clean(pg.ViewMode);
                    if (pg.Points != null) {
                        for (int j = 0; j < pg.Points.Count; j++) {
                            var pt = pg.Points[j];
                            if (pt == null) continue;
                            pt.Id = Clean(pt.Id);
                            pt.Number = Clean(pt.Number);
                            pt.Model = Clean(pt.Model);
                            pt.Category = Clean(pt.Category);
                            pt.State = Clean(pt.State);
                            pt.Reason = Clean(pt.Reason);
                            pt.Method = Clean(pt.Method);
                            pt.Side = Clean(pt.Side);
                            pt.Tag = Clean(pt.Tag);
                            pt.Description = Clean(pt.Description);
                        }
                    }
                }
            }
            if (p.ChangeLogs != null) {
                for (int i = 0; i < p.ChangeLogs.Count; i++) {
                    var lg = p.ChangeLogs[i];
                    if (lg == null) continue;
                    lg.Date = Clean(lg.Date);
                    lg.Version = Clean(lg.Version);
                    lg.ModelDate = Clean(lg.ModelDate);
                    lg.Content = Clean(lg.Content);
                    lg.Notes = Clean(lg.Notes);
                    lg.Designer = Clean(lg.Designer);
                    lg.Editor = Clean(lg.Editor);
                    lg.Approver = Clean(lg.Approver);
                }
            }
            CleanBaseline(p.Baseline);
            CleanBaseline(p.LatestExported);
        }

        static void CleanBaseline(LineupBaselineSnapshot b) {
            if (b == null) return;
            b.Version = Clean(b.Version);
            b.CapturedAt = Clean(b.CapturedAt);
            if (b.Records != null) {
                for (int i = 0; i < b.Records.Count; i++) CleanRecord(b.Records[i]);
            }
        }
    }
    public sealed class LineupRecord {
        public string Id=Guid.NewGuid().ToString("N");
        public string Number="",Category="",Address="",Parameters="",Description="",Model="",Brand="",Home="",Mounting="",Mechanism="",Connector="",Notes="";
        public string IslandId="",ValveId="",CylinderId="",Position="";
        public string[] InstancePath=new string[0];
        public string SourceFile="",ReferenceKey="";
        public LineupRecord Copy(){
            var c=(LineupRecord)MemberwiseClone();
            c.InstancePath=InstancePath==null?new string[0]:(string[])InstancePath.Clone();
            return c;
        }
        public override string ToString(){return Number+"  "+Description+"  "+Model;}
    }
    public sealed class LineupLocationPoint {
        public string Id="",Number="",Model="",Category="",State="",Reason="",Method="";
        public string Side="",Tag="",Description="";
        public double X,Y;public int Pixels;
        public LineupLocationPoint Copy(){return (LineupLocationPoint)MemberwiseClone();}
    }
    public sealed class LineupLocationPackage {
        public int Version=1,Width,Height;public string Image="view.png",AssemblyFile="";
        public List<LineupLocationPoint> Points=new List<LineupLocationPoint>();
    }
    // 持久化在 .lineup.xml 里的「LineUP第三版_CAD」单页位置图数据；截图文件存在装配旁 .lineup.cad 目录
    public sealed class LineupCadPage {
        public string Id=Guid.NewGuid().ToString("N");
        public string Name="总图",LineCode="",ImageFile="",ViewMode="isometric";
        public int Width,Height;
        public List<LineupLocationPoint> Points=new List<LineupLocationPoint>();
        public LineupCadPage Copy(){
            return new LineupCadPage{
                Id=Id,Name=Name,LineCode=LineCode,ImageFile=ImageFile,ViewMode=ViewMode,Width=Width,Height=Height,
                Points=(Points??new List<LineupLocationPoint>()).Select(p=>p.Copy()).ToList()
            };
        }
    }
    // 「LineUP更新日志」单条版本记录（持久化在 .lineup.xml，与公司模板 9 列一一对应）
    public sealed class LineupChangeLog {
        public string Date="",Version="",ModelDate="",Content="",Notes="",Designer="",Editor="",Approver="";
        public LineupChangeLog Copy(){return (LineupChangeLog)MemberwiseClone();}
    }
    // BOM 基准快照（记录上一发布/导入版本的 BOM，用于自动生成更新摘要与红底/删除线标记）
    public sealed class LineupBaselineSnapshot {
        public string Version="",CapturedAt="";
        public List<LineupRecord> Records=new List<LineupRecord>();
        public LineupBaselineSnapshot Copy(){
            var c=new LineupBaselineSnapshot{Version=Version??"",CapturedAt=CapturedAt??""};
            if(Records!=null){c.Records=new List<LineupRecord>(Records.Count);for(int i=0;i<Records.Count;i++)c.Records.Add(Records[i].Copy());}
            return c;
        }
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
        // 项目抬头元数据与多页 CAD 位置图（兼容旧 .lineup.xml：缺省值均为空/空集合）
        public string ProjectCode="",StationCode="",LineCode="L1",Designer="",Editor="",Revision="V1.0";
        public bool Export11Columns=true;
        public bool MarkBomChanges=true;
        public List<LineupCadPage> CadPages=new List<LineupCadPage>();
        public List<LineupChangeLog> ChangeLogs=new List<LineupChangeLog>();
        public LineupBaselineSnapshot Baseline;
        public LineupBaselineSnapshot LatestExported;
        public static string CadAssetsDir(string assemblyFile){
            return string.IsNullOrEmpty(assemblyFile)?"":Path.ChangeExtension(Path.GetFullPath(assemblyFile),".lineup.cad");
        }
        public static string ResolveCadImagePath(string assemblyFile,string imageFile){
            if(string.IsNullOrWhiteSpace(imageFile))return "";
            if(Path.IsPathRooted(imageFile))return imageFile;
            string dir=CadAssetsDir(assemblyFile);
            return string.IsNullOrEmpty(dir)?imageFile:Path.Combine(dir,imageFile);
        }
        public void InferMetadataFromAssembly(string assemblyPath){
            if(string.IsNullOrWhiteSpace(assemblyPath))return;
            string stem=Path.GetFileNameWithoutExtension(assemblyPath)??"";
            if(string.IsNullOrWhiteSpace(ProjectCode)){
                var m=Regex.Match(stem,@"\b([A-Z]{2}\d{2}\.\d{2}[A-Z0-9]*)\b",RegexOptions.IgnoreCase);
                if(m.Success)ProjectCode=m.Groups[1].Value.ToUpperInvariant();
            }
            if(string.IsNullOrWhiteSpace(StationCode)){
                var m=Regex.Match(stem,@"(?:^|[-_\s])((?:OK|OP|ST|AF|M)\d{2,4}|C\d{2})(?:$|[-_\s（(])",RegexOptions.IgnoreCase);
                if(m.Success)StationCode=m.Groups[1].Value.ToUpperInvariant();
            }
            if(string.IsNullOrWhiteSpace(NumberPrefix)&&!string.IsNullOrWhiteSpace(StationCode))NumberPrefix=StationCode;
            if(string.IsNullOrWhiteSpace(LineCode))LineCode="L1";
            if(string.IsNullOrWhiteSpace(Revision))Revision="V1.0";
        }
        static readonly XmlSerializer ProjectSerializer=new XmlSerializer(typeof(LineupProject));
        // 纯内存深拷贝：比 XmlSerializer 序列化+反序列化快数十倍，消除编辑与 CAD 页同步卡顿
        public LineupProject Copy(){
            var p=new LineupProject{
                Version=Version,AssemblyFile=AssemblyFile??"",NumberPrefix=NumberPrefix??"",ActiveIsland=ActiveIsland,
                ProjectCode=ProjectCode??"",StationCode=StationCode??"",LineCode=LineCode??"L1",
                Designer=Designer??"",Editor=Editor??"",Revision=Revision??"V1.0",
                Export11Columns=Export11Columns,MarkBomChanges=MarkBomChanges,
                Baseline=Baseline==null?null:Baseline.Copy(),
                LatestExported=LatestExported==null?null:LatestExported.Copy()
            };
            if(Records!=null){p.Records=new List<LineupRecord>(Records.Count);for(int i=0;i<Records.Count;i++)p.Records.Add(Records[i].Copy());}
            if(Sections!=null){p.Sections=new List<LineupSection>(Sections.Count);for(int i=0;i<Sections.Count;i++){var s=Sections[i];p.Sections.Add(new LineupSection{Category=s.Category??"",Cells=s.Cells==null?new string[0]:(string[])s.Cells.Clone()});}}
            if(Islands!=null){p.Islands=new List<LineupIsland>(Islands.Count);for(int i=0;i<Islands.Count;i++)p.Islands.Add(Islands[i].Copy());}
            if(EntryPresets!=null){p.EntryPresets=new List<LineupEntryPreset>(EntryPresets.Count);for(int i=0;i<EntryPresets.Count;i++)p.EntryPresets.Add(EntryPresets[i].Copy());}
            if(CadPages!=null){p.CadPages=new List<LineupCadPage>(CadPages.Count);for(int i=0;i<CadPages.Count;i++)p.CadPages.Add(CadPages[i].Copy());}
            if(ChangeLogs!=null){p.ChangeLogs=new List<LineupChangeLog>(ChangeLogs.Count);for(int i=0;i<ChangeLogs.Count;i++)p.ChangeLogs.Add(ChangeLogs[i].Copy());}
            return p;
        }
        public void Save(string file) {
            // 在副本上清洗全部 XML 1.0 非法控制字符，配合 CheckCharacters=true 写出，彻底杜绝写坏 .lineup.xml
            var clean = Copy();
            LineupXmlSanitizer.CleanProjectInPlace(clean);
            var dir = Path.GetDirectoryName(Path.GetFullPath(file));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var ws = new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true, CheckCharacters = true };
            try {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = XmlWriter.Create(stream, ws)) {
                    ProjectSerializer.Serialize(writer, clean);
                }
                if (File.Exists(file)) {
                    try { File.Replace(temporary, file, file + ".bak"); }
                    catch (IOException) { File.Replace(temporary, file, null); }
                } else {
                    File.Move(temporary, file);
                }
            } finally {
                if (File.Exists(temporary)) { try { File.Delete(temporary); } catch { } }
            }
        }
        public static LineupProject Load(string file,string assembly) {
            LineupProject p;
            // 关闭 XmlReader CheckCharacters 以兼容历史已写出 &#xB; 等控制字符的旧 .lineup.xml，加载后即时清洗自愈
            var rs = new XmlReaderSettings { CheckCharacters = false, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = XmlReader.Create(stream, rs)) {
                p = (LineupProject)ProjectSerializer.Deserialize(reader);
            }
            if(p.Version!=1)throw new InvalidDataException("不支持的 Lineup 文件版本。");
            if(p.Records==null)p.Records=new List<LineupRecord>();
            if(p.Sections==null)p.Sections=new List<LineupSection>();
            if(p.Islands==null)p.Islands=new List<LineupIsland>();
            if(p.EntryPresets==null)p.EntryPresets=new List<LineupEntryPreset>();
            if(p.CadPages==null)p.CadPages=new List<LineupCadPage>();
            if(p.ChangeLogs==null)p.ChangeLogs=new List<LineupChangeLog>();
            LineupXmlSanitizer.CleanProjectInPlace(p);
            if(!string.Equals(Path.GetFullPath(p.AssemblyFile),Path.GetFullPath(assembly),StringComparison.OrdinalIgnoreCase)){
                // 0.10.3：数据文件就是这个装配旁边、同名的那个 .lineup.xml → 装配连同文件夹被拷走 / 挪走 / 改过名（记的还是老路径）。
                // 原来一律报"属于另一装配"、Lineup 打不开：H 盘上到处是「项目 - 副本」，拷一份接着做就再也打不开清单。
                // 记录靠装配里的持久化关联键找模型，和文件夹在哪无关，所以直接改记成现在的路径（下次保存时写回）
                if(string.Equals(Path.GetFullPath(file),Path.GetFullPath(Path.ChangeExtension(assembly,".lineup.xml")),StringComparison.OrdinalIgnoreCase)){p.AssemblyFile=Path.GetFullPath(assembly);return p;}
                throw new InvalidDataException("Lineup 数据属于另一装配，请使用导入功能重新绑定模型。");
            }
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
