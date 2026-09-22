using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace TianGongCadSuite {
    // Pure planning/reporting layer. No CAD, no WinForms: unit-testable.
    public sealed class ConvertOptions {
        public List<string> Inputs;
        public string OutputRoot;
        public bool Recursive;
        public bool FlatOutput;      // true = every output lands in OutputRoot; false = mirror the input tree
        public bool Force;           // re-convert even when the target already exists
        public int Workers;
        public bool IncludeAssemblies;
        public bool IncludeParts;
        public bool IncludeDrawings;
        public bool VerifyReopen;    // reopen the produced top document and check for missing references
        public ConvertOptions(){
            Inputs=new List<string>();OutputRoot="";Recursive=true;FlatOutput=false;Force=false;Workers=1;
            IncludeAssemblies=true;IncludeParts=true;IncludeDrawings=false;VerifyReopen=false;
        }
        public ConvertOptions Clone(){
            var c=new ConvertOptions();
            c.Inputs.AddRange(Inputs);c.OutputRoot=OutputRoot;c.Recursive=Recursive;c.FlatOutput=FlatOutput;
            c.Force=Force;c.Workers=Workers;c.IncludeAssemblies=IncludeAssemblies;c.IncludeParts=IncludeParts;
            c.IncludeDrawings=IncludeDrawings;c.VerifyReopen=VerifyReopen;return c;
        }
        public List<string> Extensions(){
            var list=new List<string>();
            if(IncludeAssemblies)list.Add(".sldasm");
            if(IncludeParts)list.Add(".sldprt");
            if(IncludeDrawings)list.Add(".slddrw");
            return list;
        }
        public string Validate(){
            if(Inputs.Count==0)return "请先添加要转换的 SolidWorks 文件或文件夹。";
            if(OutputRoot==null||OutputRoot.Trim().Length==0)return "请选择输出目录。";
            if(Workers<1||Workers>8)return "并行进程数必须在 1 到 8 之间。";
            if(!Directory.Exists(OutputRoot))return "输出目录不存在："+OutputRoot;
            foreach(string input in Inputs){
                if(!File.Exists(input)&&!Directory.Exists(input))return "输入不存在："+input;
            }
            return null;
        }
    }
    public sealed class ConvertItem {
        public int Index;
        public string Source;      // absolute path of the SolidWorks file
        public string Root;        // the input folder it was discovered under (or its own folder)
        public long Size;
        public string Target;      // expected top-level output (.asm/.par/.psm/.dft)
        public int Worker=-1;
        public bool Skipped;
        public string SkipReason;
    }
    public static class ConvertPlanner {
        static readonly string[] NativeExt={".asm",".par",".psm",".dft"};
        public static string NativeExtension(string source){
            string ext=(Path.GetExtension(source)??"").ToLowerInvariant();
            if(ext==".sldasm")return ".asm";
            if(ext==".sldprt")return ".par";
            if(ext==".slddrw")return ".dft";
            return ".asm";
        }
        public static bool IsSolidWorks(string path){
            string ext=(Path.GetExtension(path)??"").ToLowerInvariant();
            return ext==".sldasm"||ext==".sldprt"||ext==".slddrw";
        }
        public static bool IsLockFile(string name){
            return name.StartsWith("~$",StringComparison.Ordinal)||name.StartsWith(".~",StringComparison.Ordinal);
        }
        // One directory walk per root. Lock files and non-target extensions are dropped; directories
        // are walked once, so scanning cost is O(files) and independent of the number of workers.
        public static List<ConvertItem> Scan(ConvertOptions options){
            var items=new List<ConvertItem>();
            var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var wanted=new HashSet<string>(options.Extensions(),StringComparer.OrdinalIgnoreCase);
            foreach(string raw in options.Inputs){
                string input=Path.GetFullPath(raw);
                if(File.Exists(input)){ Add(items,seen,wanted,input,Path.GetDirectoryName(input)); continue; }
                if(!Directory.Exists(input))continue;
                string root=input;
                var stack=new Stack<string>();stack.Push(input);
                while(stack.Count>0){
                    string dir=stack.Pop();
                    string[] files;
                    try{ files=Directory.GetFiles(dir); }catch{ files=new string[0]; }
                    foreach(string file in files)Add(items,seen,wanted,file,root);
                    if(!options.Recursive)continue;
                    string[] subs;
                    try{ subs=Directory.GetDirectories(dir); }catch{ subs=new string[0]; }
                    foreach(string sub in subs){
                        string name=Path.GetFileName(sub);
                        if(name.StartsWith(".",StringComparison.Ordinal))continue;
                        stack.Push(sub);
                    }
                }
            }
            items.Sort(delegate(ConvertItem a,ConvertItem b){ int c=string.Compare(a.Source,b.Source,StringComparison.OrdinalIgnoreCase); return c; });
            for(int i=0;i<items.Count;i++){ items[i].Index=i; items[i].Target=TopTarget(items[i],options); }
            return items;
        }
        static void Add(List<ConvertItem> items,HashSet<string> seen,HashSet<string> wanted,string file,string root){
            string name=Path.GetFileName(file);
            if(IsLockFile(name))return;
            if(!wanted.Contains(Path.GetExtension(file)))return;
            if(!seen.Add(file))return;
            var item=new ConvertItem();
            item.Source=file;item.Root=root;
            try{ item.Size=new FileInfo(file).Length; }catch{ item.Size=0; }
            items.Add(item);
        }
        // Output path of the converted top-level document.
        public static string TopTarget(ConvertItem item,ConvertOptions options){
            string outRoot=Path.GetFullPath(options.OutputRoot);
            string name=Path.GetFileNameWithoutExtension(item.Source)+NativeExtension(item.Source);
            if(options.FlatOutput)return Path.Combine(outRoot,name);
            return Path.Combine(outRoot,RelativeFolder(Path.GetDirectoryName(item.Source),item.Root),name);
        }
        // Output path of a component document. Solid Edge assigns each translated component a source
        // folder plus a native extension; that assignment is preserved so references stay resolvable.
        public static string ComponentTarget(string assignedPath,string finalName,ConvertItem item,ConvertOptions options){
            string outRoot=Path.GetFullPath(options.OutputRoot);
            string name=finalName;
            if(name==null||name.Length==0)name=Path.GetFileName(assignedPath);
            if(options.FlatOutput)return Path.Combine(outRoot,Sanitize(name));
            string folder=null;
            try{ if(assignedPath!=null&&Path.IsPathRooted(assignedPath))folder=Path.GetDirectoryName(assignedPath); }catch{}
            if(folder==null||folder.Length==0)folder=Path.GetDirectoryName(item.Source);
            string rel=RelativeFolder(folder,item.Root);
            if(rel.StartsWith("_外部",StringComparison.Ordinal)){ rel=Path.Combine("_外部",SanitizeFolder(folder)); }
            return Path.Combine(outRoot,rel,Sanitize(name));
        }
        static string RelativeFolder(string folder,string root){
            try{
                string full=Path.GetFullPath(folder);
                string rootFull=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
                if(string.Equals(full.TrimEnd(Path.DirectorySeparatorChar),rootFull,StringComparison.OrdinalIgnoreCase))return "";
                if(full.StartsWith(rootFull+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
                    return full.Substring(rootFull.Length+1);
            }catch{}
            return Path.Combine("_外部",SanitizeFolder(folder));
        }
        static string SanitizeFolder(string folder){
            try{
                string full=Path.GetFullPath(folder);
                string trimmed=full.Substring(Path.GetPathRoot(full).Length);
                return Sanitize(trimmed.Replace(Path.DirectorySeparatorChar,'_').Replace(':','_'));
            }catch{ return "_未知"; }
        }
        public static string Sanitize(string name){
            var bad=Path.GetInvalidFileNameChars();
            var sb=new StringBuilder(name.Length);
            foreach(char c in name)sb.Append(Array.IndexOf(bad,c)>=0?'_':c);
            return sb.ToString();
        }
        // Work is partitioned per input root, never inside one root. Two assemblies that live in the
        // same folder almost always share standard parts, and the SolidWorks translator cannot convert
        // the same component in two CAD processes at once (measured: the shared part then fails to
        // save). Keeping a root on one worker removes that conflict entirely.
        // Groups are then balanced with longest-processing-time-first, so the makespan stays within
        // 4/3 of optimal; cost is O(n log n) for the sort.
        public static List<List<ConvertItem>> Plan(List<ConvertItem> items,int workers){
            if(workers<1)workers=1;
            var buckets=new List<List<ConvertItem>>();
            var loads=new long[workers];
            for(int i=0;i<workers;i++)buckets.Add(new List<ConvertItem>());
            var groups=new List<List<ConvertItem>>();
            var index=new Dictionary<string,List<ConvertItem>>(StringComparer.OrdinalIgnoreCase);
            var ordered=new List<ConvertItem>(items);
            ordered.Sort(delegate(ConvertItem a,ConvertItem b){ int c=b.Size.CompareTo(a.Size); if(c!=0)return c; return a.Index.CompareTo(b.Index); });
            foreach(ConvertItem item in ordered){
                string key=item.Root==null?"":item.Root;
                List<ConvertItem> group;
                if(!index.TryGetValue(key,out group)){ group=new List<ConvertItem>(); index[key]=group; groups.Add(group); }
                group.Add(item);
            }
            foreach(List<ConvertItem> group in groups){
                long weight=0;
                foreach(ConvertItem item in group)weight+=item.Size;
                int best=0;
                for(int i=1;i<workers;i++)if(loads[i]<loads[best])best=i;
                foreach(ConvertItem item in group){ item.Worker=best; buckets[best].Add(item); }
                loads[best]+=weight;
            }
            return buckets;
        }
        // Sequential is the default on purpose. Measured on a 16-core machine: four concurrent CAD
        // sessions were only 1.23x faster than one (165.7 s vs 204.4 s) while inflating per-file
        // translation time by 1.6x, and every parallel run produced a component that failed to save
        // (DISP_E_EXCEPTION) because the SolidWorks translator is not designed for two CAD processes
        // working on the same source tree. Extra workers stay available as an explicit choice.
        public static int SuggestedWorkers(int fileCount,int logicalCores,long freeMemoryMb){
            return 1;
        }
    }
    public sealed class ConvertRow {
        public int Index;public string Source;public string Status;public double OpenSeconds;public double SaveSeconds;
        public double TotalSeconds;public int Documents;public string Target;public string Error;public int Worker;
        public ConvertRow Clone(){var r=new ConvertRow();r.Index=Index;r.Source=Source;r.Status=Status;r.OpenSeconds=OpenSeconds;r.SaveSeconds=SaveSeconds;r.TotalSeconds=TotalSeconds;r.Documents=Documents;r.Target=Target;r.Error=Error;r.Worker=Worker;return r;}
    }
    // The worker writes one tab-separated line per event, flushed immediately, so the UI can tail it
    // and a crash still leaves a complete audit trail.
    public static class ConvertProtocol {
        public const string Ready="READY";
        public const string Begin="BEGIN";
        public const string End="END";
        public const string Exit="EXIT";
        public static string Line(params string[] fields){
            var sb=new StringBuilder();
            for(int i=0;i<fields.Length;i++){ if(i>0)sb.Append('\t'); sb.Append((fields[i]==null?"":fields[i]).Replace("\t"," ").Replace("\r"," ").Replace("\n"," ")); }
            return sb.ToString();
        }
        public static string[] Split(string line){
            string[] parts=line.Split('\t');
            return parts;
        }
        public static string Number(double value){ return value.ToString("F2",CultureInfo.InvariantCulture); }
        public static double Parse(string text){
            double value; if(double.TryParse(text,NumberStyles.Float,CultureInfo.InvariantCulture,out value))return value; return 0;
        }
        public static bool TryParseEnd(string[] parts,out ConvertRow row){
            row=null;
            if(parts.Length<8||parts[0]!=End)return false;
            var r=new ConvertRow();
            r.Index=(int)Parse(parts[1]);r.Status=parts[2];r.Source=parts[3];
            r.OpenSeconds=Parse(parts[4]);r.SaveSeconds=Parse(parts[5]);r.TotalSeconds=Parse(parts[6]);
            r.Documents=(int)Parse(parts[7]);
            r.Target=parts.Length>8?parts[8]:"";r.Worker=parts.Length>9?(int)Parse(parts[9]):0;r.Error=parts.Length>10?parts[10]:"";
            row=r;return true;
        }
        public static void Append(string path,string line){
            using(var stream=new FileStream(path,FileMode.Append,FileAccess.Write,FileShare.ReadWrite))
            using(var writer=new StreamWriter(stream,new UTF8Encoding(false))){ writer.WriteLine(line); writer.Flush(); stream.Flush(); }
        }
    }
    public sealed class ConvertSummary {
        public int Total,Converted,Skipped,Failed;
        public double SourceSeconds,WallSeconds;
        public double SpeedupFactor { get { return WallSeconds<=0?0:SourceSeconds/WallSeconds; } }
        public static ConvertSummary From(List<ConvertRow> rows,double wallSeconds){
            var s=new ConvertSummary();s.WallSeconds=wallSeconds;s.Total=rows.Count;
            foreach(ConvertRow r in rows){
                if(r.Status=="ok"){s.Converted++;s.SourceSeconds+=r.TotalSeconds;}
                else if(r.Status=="skip")s.Skipped++;
                else if(r.Status=="pending"){ }
                else s.Failed++;
            }
            return s;
        }
        public string Describe(){
            var sb=new StringBuilder();
            sb.Append("总计 ").Append(Total).Append(" 个文件：成功 ").Append(Converted).Append("，跳过 ").Append(Skipped).Append("，失败 ").Append(Failed).Append("。");
            sb.Append(" 单件累计 ").Append(SourceSeconds.ToString("F1")).Append(" 秒，实际耗时 ").Append(WallSeconds.ToString("F1")).Append(" 秒");
            if(WallSeconds>0.5)sb.Append("（并行加速 ").Append(SpeedupFactor.ToString("F2")).Append(" 倍）");
            sb.Append("。");
            return sb.ToString();
        }
        public string ToCsv(List<ConvertRow> rows){
            var sb=new StringBuilder();
            sb.AppendLine("序号,源文件,状态,打开秒,保存秒,合计秒,文档数,输出文件,进程,错误");
            foreach(ConvertRow r in rows){
                sb.Append(r.Index).Append(',')
                  .Append(Csv(r.Source)).Append(',')
                  .Append(Csv(r.Status)).Append(',')
                  .Append(r.OpenSeconds.ToString("F2")).Append(',')
                  .Append(r.SaveSeconds.ToString("F2")).Append(',')
                  .Append(r.TotalSeconds.ToString("F2")).Append(',')
                  .Append(r.Documents).Append(',')
                  .Append(Csv(r.Target)).Append(',')
                  .Append(r.Worker).Append(',')
                  .Append(Csv(r.Error)).AppendLine();
            }
            return sb.ToString();
        }
        static string Csv(string value){
            if(value==null)return "";
            if(value.IndexOfAny(new[]{',','"','\n','\r'})<0)return value;
            return "\""+value.Replace("\"","\"\"")+"\"";
        }
    }
}
