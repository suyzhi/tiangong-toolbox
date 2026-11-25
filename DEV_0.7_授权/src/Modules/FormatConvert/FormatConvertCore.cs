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
        public bool IncludeStep;     // .stp/.step through the STEP translator
        public bool VerifyReopen;    // reopen the produced top document and check for missing references
        public ConvertOptions(){
            Inputs=new List<string>();OutputRoot="";Recursive=true;FlatOutput=false;Force=false;Workers=1;
            IncludeAssemblies=true;IncludeParts=true;IncludeDrawings=false;IncludeStep=true;VerifyReopen=false;
        }
        public ConvertOptions Clone(){
            var c=new ConvertOptions();
            c.Inputs.AddRange(Inputs);c.OutputRoot=OutputRoot;c.Recursive=Recursive;c.FlatOutput=FlatOutput;
            c.Force=Force;c.Workers=Workers;c.IncludeAssemblies=IncludeAssemblies;c.IncludeParts=IncludeParts;
            c.IncludeDrawings=IncludeDrawings;c.IncludeStep=IncludeStep;c.VerifyReopen=VerifyReopen;return c;
        }
        public List<string> Extensions(){
            var list=new List<string>();
            if(IncludeAssemblies)list.Add(".sldasm");
            if(IncludeParts)list.Add(".sldprt");
            if(IncludeDrawings)list.Add(".slddrw");
            if(IncludeStep){ list.Add(".stp"); list.Add(".step"); }
            return list;
        }
        public string Validate(){
            if(Inputs.Count==0)return "请先添加要转换的文件或文件夹。";
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
            // STEP: provisional only. A .stp usually imports as a single part (.par) but a STEP file
            // that carries assembly structure imports as .asm; the CAD decides, so this is corrected
            // right after the file is opened.
            if(ext==".stp"||ext==".step")return ".par";
            return ".asm";
        }
        public static bool IsSolidWorks(string path){
            string ext=(Path.GetExtension(path)??"").ToLowerInvariant();
            return ext==".sldasm"||ext==".sldprt"||ext==".slddrw";
        }
        // STEP carries no hint about the result: the same .stp can import as a single part or as a
        // whole assembly, so the top-level extension is decided after the CAD has opened the file.
        public static bool IsStep(string path){
            string ext=(Path.GetExtension(path)??"").ToLowerInvariant();
            return ext==".stp"||ext==".step";
        }
        public static bool IsSupported(string path){ return IsSolidWorks(path)||IsStep(path); }
        public static readonly string[] StepNativeExtensions={".par",".asm"};
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
                var visited=new HashSet<string>(StringComparer.OrdinalIgnoreCase){ input };
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
                        // 目录联接 / 符号链接不跟进去：指回上级目录的联接（用户目录里的 Application Data 之类）会让扫描永远转圈、内存涨满
                        try{ if((File.GetAttributes(sub)&FileAttributes.ReparsePoint)!=0)continue; }catch{ continue; }
                        if(!visited.Add(Path.GetFullPath(sub)))continue;
                        stack.Push(sub);
                    }
                }
            }
            items.Sort(delegate(ConvertItem a,ConvertItem b){ int c=string.Compare(a.Source,b.Source,StringComparison.OrdinalIgnoreCase); return c; });
            // 还没选输出目录也能扫（0.10.3）：原来按空的输出目录算输出路径，Path.GetFullPath("") 直接抛「路径的形式不合法」，点「扫描文件」就弹未处理的异常
            bool hasOut=!string.IsNullOrWhiteSpace(options.OutputRoot);
            // 路径超长（H 盘 SolidWorks 项目里有 266 个字符的）算不出输出路径时这一个 Target 留空，别让整次扫描失败
            for(int i=0;i<items.Count;i++){ items[i].Index=i; try{ items[i].Target=hasOut?TopTarget(items[i],options):null; }catch(PathTooLongException){ items[i].Target=null; } }
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
        // Output path of the converted top-level document. For STEP the real extension is only known
        // once the CAD has imported the file, so callers pass it in; the default is the provisional one.
        public static string TopTarget(ConvertItem item,ConvertOptions options){
            return TopTargetFor(item,options,NativeExtension(item.Source));
        }
        // SolidWorks 装配打开后一个零件都没有时，拿来"拷到一起再开"的那批文件（0.10.2）。
        // 真机（宝马托盘 140-2线：零件、装配体分两个文件夹）：天工只在装配自己的文件夹里找它引用的零件，找不到就静默打开成空装配，
        // 原来照样存出一个空的 .asm、报"成功"。从用户加的输入文件夹（就是装配所在文件夹时退一级到上级）里收 .sldprt/.sldasm，
        // 同名的只要第一个（拷到一个文件夹里放不下两个同名的），源装配自己排第一。不扫整个盘符，最多 3000 个。
        // 收哪个文件夹（null = 不收）：输入文件夹；输入就是装配所在文件夹且该文件夹里没有 .sldprt（典型的 装配体\ 与 零件\ 分开放）时才退一级；
        // 若装配所在文件夹本身已有 .sldprt（典型站位总装单目录打包），绝不越界爬到上一级把其他无关站位的几千个文件拷进临时目录；盘符根不收。
        public static string StageFolder(string source,string root){
            if(string.IsNullOrEmpty(source))return null;
            try{
                string own=Path.GetDirectoryName(Path.GetFullPath(source));
                string dir;
                if(!string.IsNullOrEmpty(root)&&Directory.Exists(root)&&!SameDir(root,own))dir=Path.GetFullPath(root);
                else if(HasSolidWorksPartsInDir(own))return null;
                else dir=Path.GetDirectoryName(own);
                if(dir!=null&&SameDir(dir,Path.GetPathRoot(dir)))dir=null;
                return dir;
            }catch{ return null; }
        }
        static bool HasSolidWorksPartsInDir(string dir){
            if(string.IsNullOrEmpty(dir)||!Directory.Exists(dir))return false;
            try{
                foreach(string f in Directory.EnumerateFiles(dir)){
                    if(IsLockFile(Path.GetFileName(f)))continue;
                    if(string.Equals(Path.GetExtension(f),".sldprt",StringComparison.OrdinalIgnoreCase))return true;
                }
            }catch{}
            return false;
        }
        // 解析天工 SolidWorks 转换器写到源装配旁边的 <主文件名>.log，提取缺失文件名列表（不重样，保留顺序）。
        // 用于：① 触发 StageFor 前核实缺失文件是否真的存在于其他子目录（避免为本来就缺件的残缺包白拷几 GB 文件、双倍重开 15 分钟大装配）；
        // ② 报空装配失败时精准列出真正缺失的文件名。
        public static List<string> ParseMissingFromTranslatorLog(string sourcePath){
            var missing=new List<string>();
            if(string.IsNullOrEmpty(sourcePath))return missing;
            try{
                string dir=Path.GetDirectoryName(sourcePath);
                string stem=Path.GetFileNameWithoutExtension(sourcePath);
                if(string.IsNullOrEmpty(dir)||string.IsNullOrEmpty(stem))return missing;
                string logPath=Path.Combine(dir,stem+".log");
                if(!File.Exists(logPath))return missing;
                var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string content;
                using(var fs=new FileStream(logPath,FileMode.Open,FileAccess.Read,FileShare.ReadWrite))
                using(var sr=new StreamReader(fs,Encoding.Default,true))
                    content=sr.ReadToEnd();
                // 匹配两类天工日志：
                // 1) "Warn: The body <path> miss file"
                // 2) "错误: ----> 丢失 - <path>"（可能多个拼在同一行）
                foreach(System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                    content,@"Warn:\s*The body\s+(.+?)\s+miss file",System.Text.RegularExpressions.RegexOptions.IgnoreCase)){
                    AddMissingName(missing,seen,m.Groups[1].Value);
                }
                if(missing.Count==0){
                    foreach(System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                        content,@"丢失\s*-\s*([^\r\n]+?)(?=错误:|[\r\n]|$)")){
                        AddMissingName(missing,seen,m.Groups[1].Value);
                    }
                }
            }catch{}
            return missing;
        }
        static void AddMissingName(List<string> list,HashSet<string> seen,string rawPath){
            if(string.IsNullOrWhiteSpace(rawPath))return;
            string p=rawPath.Trim();
            int slash=p.LastIndexOfAny(new[]{'\\','/'});
            string name=slash>=0?p.Substring(slash+1):p;
            name=name.Trim();
            if(name.Length>0&&seen.Add(name))list.Add(name);
        }
        // 判断「拷到临时目录再开」是否真的可能补齐缺失零件：
        // 1) 必须存在 StageFolder 且其中有位于装配自身目录之外的 .sldprt/.sldasm；
        // 2) 若天工日志已明确给出缺失文件名，则其中至少有 1 个必须真的存在于 StageFolder（装配目录之外），
        //    否则拷贝重开 100% 徒劳（典型：源压缩包本身缺件或引用了设计员本机标准件库）。
        public static bool StageCanSupplyMissing(string source,string root,ICollection<string> missingNames){
            if(string.IsNullOrEmpty(source))return false;
            string dir=StageFolder(source,root);
            if(dir==null)return false;
            string own;
            try{ own=Path.GetDirectoryName(Path.GetFullPath(source)); }catch{ return false; }
            HashSet<string> want=null;
            if(missingNames!=null&&missingNames.Count>0){
                want=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach(string m in missingNames)if(!string.IsNullOrEmpty(m))want.Add(m);
            }
            int scanned=0;
            try{
                foreach(string f in Directory.EnumerateFiles(dir,"*.*",SearchOption.AllDirectories)){
                    if(++scanned>15000)break;
                    string ext=Path.GetExtension(f).ToLowerInvariant();
                    if(ext!=".sldprt"&&ext!=".sldasm")continue;
                    string fdir=Path.GetDirectoryName(f);
                    if(SameDir(fdir,own))continue;
                    string fname=Path.GetFileName(f);
                    if(IsLockFile(fname))continue;
                    if(want==null)return true;
                    if(want.Contains(fname))return true;
                }
            }catch{}
            return false;
        }
        public static List<string> StageSources(string source,string root){
            var list=new List<string>();
            if(string.IsNullOrEmpty(source))return list;
            string dir=StageFolder(source,root);
            var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            list.Add(Path.GetFullPath(source));names.Add(Path.GetFileName(source));
            if(dir==null)return list;
            try{
                foreach(string f in Directory.EnumerateFiles(dir,"*.*",SearchOption.AllDirectories)){
                    if(list.Count>=3000)break;
                    string ext=Path.GetExtension(f).ToLowerInvariant();
                    if(ext!=".sldprt"&&ext!=".sldasm")continue;
                    if(IsLockFile(Path.GetFileName(f)))continue;
                    if(names.Add(Path.GetFileName(f)))list.Add(f);
                }
            }catch{}
            return list;
        }
        public static bool SameDir(string a,string b){
            try{ return string.Equals(Path.GetFullPath(a).TrimEnd('\\','/'),Path.GetFullPath(b).TrimEnd('\\','/'),StringComparison.OrdinalIgnoreCase); }catch{ return false; }
        }

        // SolidWorks 组件被天工导入时，若历史 MRU 或同批其他装配已存在同名文件，天工会自动给 doc.FullName 追加 _0/_1/_5 后缀。
        // 这里通过核对源磁盘真实文件，区分「真实文件名就叫 Bolt_2.sldprt」和「天工给 Bolt.sldprt 追加了 _5」：
        // · 优先按 assignedPath 扩展名匹配同类型源文件（.asm → 先查 .sldasm，.par/.psm → 先查 .sldprt），
        //   避免同一文件夹内同名 Foo.sldasm + Foo.sldprt 互相串号导致漏转或重复转；
        // · 若 <dir>\<stem>.sldprt/.sldasm 存在 → 原样返回，wasSuffixed=false；
        // · 否则若 <stem> 匹配 ^(.*)_\d+$ 且 <dir>\<base>.sldprt/.sldasm 存在 → 返回真实源文件，wasSuffixed=true；
        // · 否则返回 null（虚拟零件或非 SolidWorks）。
        public static string ResolveSolidWorksSource(string assignedPath,out bool wasSuffixed){
            wasSuffixed=false;
            if(string.IsNullOrEmpty(assignedPath))return null;
            try{
                string dir=Path.GetDirectoryName(assignedPath);
                string stem=Path.GetFileNameWithoutExtension(assignedPath);
                string ext=(Path.GetExtension(assignedPath)??"").ToLowerInvariant();
                if(string.IsNullOrEmpty(dir)||string.IsNullOrEmpty(stem))return null;
                bool preferAsm=ext==".asm";
                string exact=FindSolidWorksSource(dir,stem,preferAsm);
                if(exact!=null)return exact;
                int sep=stem.LastIndexOf('_');
                if(sep>0&&sep<stem.Length-1&&AllDigits(stem,sep+1)){
                    string baseStem=stem.Substring(0,sep);
                    string orig=FindSolidWorksSource(dir,baseStem,preferAsm);
                    if(orig!=null){ wasSuffixed=true; return orig; }
                }
            }catch{}
            return null;
        }
        static string FindSolidWorksSource(string dir,string stem,bool preferAsm){
            string first=Path.Combine(dir,stem+(preferAsm?".sldasm":".sldprt"));
            if(File.Exists(first))return first;
            string second=Path.Combine(dir,stem+(preferAsm?".sldprt":".sldasm"));
            if(File.Exists(second))return second;
            return null;
        }
        static bool AllDigits(string text,int start){
            for(int i=start;i<text.Length;i++)if(text[i]<'0'||text[i]>'9')return false;
            return true;
        }

        // 一个装配里各文档的输出路径（0.10.2）：同一个源文件的重复实例并成一个（返回 null）；
        // 不同源文件撞了同一个输出路径（平铺输出时两个文件夹里各有一个同名零件）→ 依次改成「名字 (2)」「名字 (3)」，clashWith = 先占了这个名字的源文件。
        // 原来按输出路径判重复，第二个同名零件直接被丢掉，装配两处都引用了第一个
        public static string ClaimTarget(Dictionary<string,string> writtenFrom,string target,string sourceKey,out string clashWith){
            clashWith=null;
            string dir=Path.GetDirectoryName(target),stem=Path.GetFileNameWithoutExtension(target),tail=Path.GetExtension(target);
            string candidate=target;
            for(int n=2;;n++){
                string first;
                if(!writtenFrom.TryGetValue(candidate,out first)){ writtenFrom[candidate]=sourceKey; return candidate; }
                if(string.Equals(first,sourceKey,StringComparison.OrdinalIgnoreCase))return null;
                if(clashWith==null)clashWith=first;
                candidate=Path.Combine(dir,stem+" ("+n+")"+tail);
            }
        }

        public static string TopTargetFor(ConvertItem item,ConvertOptions options,string extension){
            string outRoot=Path.GetFullPath(options.OutputRoot);
            string name=Path.GetFileNameWithoutExtension(item.Source)+extension;
            if(options.FlatOutput)return Path.Combine(outRoot,name);
            return Path.Combine(outRoot,RelativeFolder(Path.GetDirectoryName(item.Source),item.Root),name);
        }
        // Every path the top-level document could end up at. Used for the "already converted" check so
        // a STEP file that turns out to be an assembly is still recognised on the next run.
        public static List<string> CandidateTargets(ConvertItem item,ConvertOptions options){
            var list=new List<string>();
            if(IsStep(item.Source)){
                foreach(string ext in StepNativeExtensions)list.Add(TopTargetFor(item,options,ext));
                return list;
            }
            list.Add(TopTarget(item,options));
            // A SolidWorks part can import as a sheet-metal document, so both names are checked.
            if(string.Equals(Path.GetExtension(item.Source),".sldprt",StringComparison.OrdinalIgnoreCase))
                list.Add(TopTargetFor(item,options,".psm"));
            return list;
        }
        // Resume marker. Written before an item is converted and removed only once it succeeded, so a
        // marker that is still there means the last attempt did not finish (a component failed to save,
        // the worker crashed or was stopped). The top-level file may exist in that case, but it can be
        // incomplete, so it must not count as "already converted". The name keeps the source extension
        // (Foo.SLDASM / Foo.SLDPRT in one folder need separate markers) and does not depend on the
        // output extension, which for STEP is only known after the import.
        public const string PendingSuffix=".tgconvert-pending";
        public static string PendingMarker(ConvertItem item,ConvertOptions options){
            return TopTargetFor(item,options,Path.GetExtension(item.Source))+PendingSuffix;
        }
        public static void MarkPending(ConvertItem item,ConvertOptions options){
            string marker=PendingMarker(item,options);
            Directory.CreateDirectory(Path.GetDirectoryName(marker));
            File.WriteAllText(marker,DateTime.Now.ToString("s",CultureInfo.InvariantCulture)+"\t"+item.Source+Environment.NewLine,new UTF8Encoding(false));
        }
        public static void ClearPending(ConvertItem item,ConvertOptions options){
            string marker=PendingMarker(item,options);
            if(File.Exists(marker))File.Delete(marker);
        }
        public static bool IsAlreadyConverted(ConvertItem item,ConvertOptions options){
            try{ if(File.Exists(PendingMarker(item,options)))return false; }catch{}
            foreach(string candidate in CandidateTargets(item,options)){
                try{ if(File.Exists(candidate)&&new FileInfo(candidate).Length>0)return true; }catch{}
            }
            return false;
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
            // 0.10.3：零件在输入文件夹的「兄弟」文件夹里（SolidWorks 项目常把 零件\ 装配体\ 分开放，用户只加了装配所在的那个）：
            // 按和上一级的相对位置放（输出里也是 零件\），原来塞进「_外部\<整条源路径拼成的文件夹名>」，又难找、深路径下还会超过 260 字符存不上
            if(rel.StartsWith("_外部",StringComparison.Ordinal)){ string sibling=SiblingFolder(folder,item.Root); if(sibling!=null)rel=sibling; }
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
        // folder 在 root 的上一级文件夹里（不在 root 里）→ 相对上一级的路径；上一级是盘符根（太宽）或不相干 → null
        static string SiblingFolder(string folder,string root){
            try{
                string parent=Path.GetDirectoryName(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar));
                if(string.IsNullOrEmpty(parent)||SameDir(parent,Path.GetPathRoot(parent)))return null;
                string full=Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
                string p=parent.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
                if(!full.StartsWith(p,StringComparison.OrdinalIgnoreCase))return null;
                return full.Substring(p.Length);
            }catch{ return null; }
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
        // 工作单元划分（兼容旧调用）：同一文件夹下的 .SLDASM 尽量按目录分组，而完全独立的 .STEP 和 .SLDPRT
        // 拆成独立工作单元按 LPT（最大优先）均匀分配到所有并行进程，不再把单输入目录下的所有文件全塞进 1 个进程。
        // 实际批量转换走下方的 ConvertQueue 动态抢占队列，这里保留给单元测试和静态估算。
        public static List<List<ConvertItem>> Plan(List<ConvertItem> items,int workers){
            if(workers<1)workers=1;
            var buckets=new List<List<ConvertItem>>();
            var loads=new long[workers];
            for(int i=0;i<workers;i++)buckets.Add(new List<ConvertItem>());
            var groups=new List<List<ConvertItem>>();
            var index=new Dictionary<string,List<ConvertItem>>(StringComparer.OrdinalIgnoreCase);
            var ordered=OrderForQueue(items);
            foreach(ConvertItem item in ordered){
                // STEP 和单个 SLDPRT 彼此无源依赖，直接按单文件独立分发；
                // 同目录的 SLDASM 在静态分桶下按自身目录聚合，不同子目录的 SLDASM 可并行分布到不同进程。
                string key;
                if(IsStep(item.Source)||string.Equals(Path.GetExtension(item.Source),".sldprt",StringComparison.OrdinalIgnoreCase)){
                    key="F|"+item.Index;
                } else {
                    string dir=null;
                    try{ dir=Path.GetDirectoryName(item.Source); }catch{}
                    key="D|"+(dir??item.Root??"");
                }
                List<ConvertItem> group;
                if(!index.TryGetValue(key,out group)){ group=new List<ConvertItem>(); index[key]=group; groups.Add(group); }
                group.Add(item);
            }
            // 按组权重从大到小 LPT 配平（保持同权重时的优先级顺序）
            groups.Sort(delegate(List<ConvertItem> a,List<ConvertItem> b){
                int ra=Rank(a[0]),rb=Rank(b[0]);
                if(ra!=rb)return ra.CompareTo(rb);
                long wa=GroupWeight(a),wb=GroupWeight(b);
                if(wa!=wb)return wb.CompareTo(wa);
                return a[0].Index.CompareTo(b[0].Index);
            });
            foreach(List<ConvertItem> group in groups){
                long weight=GroupWeight(group);
                int best=0;
                for(int i=1;i<workers;i++)if(loads[i]<loads[best])best=i;
                foreach(ConvertItem item in group){ item.Worker=best; buckets[best].Add(item); }
                loads[best]+=weight;
            }
            return buckets;
        }
        static long GroupWeight(List<ConvertItem> group){
            long w=0;
            foreach(ConvertItem item in group)w+=Math.Max(1L,item.Size);
            return w;
        }
        // 统一队列排序：装配 (.sldasm) 最优先（转完装配会顺带落盘其零件，后续零件条目直接跳过），
        // 其次 STEP (.stp/.step)，再其次独立零件 (.sldprt)，最后工程图；同类内大文件优先（LPT），减少尾部空等。
        public static List<ConvertItem> OrderForQueue(List<ConvertItem> items){
            var ordered=new List<ConvertItem>(items);
            ordered.Sort(delegate(ConvertItem a,ConvertItem b){
                int c=Rank(a).CompareTo(Rank(b));
                if(c!=0)return c;
                c=b.Size.CompareTo(a.Size);
                if(c!=0)return c;
                return a.Index.CompareTo(b.Index);
            });
            return ordered;
        }
        // 启动任何 CAD 进程前先过滤已转换文件（!Force 时）：
        // 若整批或大部分文件已存在，避免白等 13~30 秒启动 CAD；同时让实际并发进程数只按真正待转文件数计算。
        public static List<ConvertItem> FilterPending(List<ConvertItem> items,ConvertOptions options,out List<ConvertRow> skipped){
            skipped=new List<ConvertRow>();
            if(options==null||options.Force)return new List<ConvertItem>(items);
            var pending=new List<ConvertItem>(items.Count);
            foreach(ConvertItem item in items){
                if(IsAlreadyConverted(item,options)){
                    var row=new ConvertRow();
                    row.Index=item.Index;
                    row.Source=item.Source;
                    row.Status="skip";
                    row.Target=item.Target;
                    row.Worker=-1;
                    row.Error="输出已存在";
                    skipped.Add(row);
                } else {
                    pending.Add(item);
                }
            }
            return pending;
        }
        static int Rank(ConvertItem item){
            string ext=(Path.GetExtension(item.Source)??"").ToLowerInvariant();
            if(ext==".sldasm")return 0;
            if(ext==".stp"||ext==".step")return 1;
            if(ext==".sldprt")return 2;
            return 3;
        }
        // 同目录装配体串行化判定：
        // 实测大型站位装配（如 OK160 136 个 .SLDASM、OK070 608 个、OK190 787 个、SD-01 32 个）全部位于同一文件夹，
        // 最大顶层总装（50~200 MB）内部已包含 70%~99% 的子装配与零件；若多进程同时打开同目录装配：
        // ① 重复翻译相同的 150~215 MB 超大零件与几十个子装配；② 天工在源目录落盘冲突引发 DISP_E_EXCEPTION 与数百个 _0 副本；
        // ③ 峰值内存翻 3 倍触发页面文件抖动。因此当目录内含大装配（≥5 MB）或装配数 >8 时，该目录按最大总装优先串行转换，
        // 总装落盘后其包含的子装配/零件全部 0 秒跳过；而少量小独立装配（≤8 个且均 <5 MB）仍保持多进程并发。
        public const long SerializedAsmSizeThresholdBytes=5L*1024L*1024L;
        public const int SerializedAsmCountThreshold=8;
        public const long HeavyAsmSizeBytes=20L*1024L*1024L;
        public const long HeavyDirTotalBytes=250L*1024L*1024L;

        public static bool IsSerializedAsmDir(int asmCountInDir,long maxAsmBytesInDir){
            return maxAsmBytesInDir>=SerializedAsmSizeThresholdBytes||asmCountInDir>SerializedAsmCountThreshold;
        }
        public static bool IsHeavyAssembly(long asmBytes,long dirTotalSwBytes){
            return asmBytes>=HeavyAsmSizeBytes||(asmBytes>=SerializedAsmSizeThresholdBytes&&dirTotalSwBytes>=HeavyDirTotalBytes);
        }

        // 智能并发进程数：结合并行预拉起、同目录大装配串行化与大装配真实内存预算（单实例 5 GB）：
        // · 单个文件或单站位大型装配目录（无其他独立 STEP/目录）：1 进程，把 CPU/内存全给顶层总装和天工 IOPS 内置多进程；
        // · 单站位目录 + 独立 STEP（如 BOM 下 56 个 STEP）：允许 2+ 进程，1 个专心转总装，其余并行消化 STEP；
        // · 多站位目录：按真实可用物理内存（重型装配预留 5 GB/进程）动态并发，绝不把 32 GB 机器拉爆换页；
        // · 少量小型装配 / STEP / 散件：保持原有 2~4 进程高并发加速。
        public static int SuggestedWorkers(int fileCount,int logicalCores,long freeMemoryMb){
            return SuggestedWorkers(fileCount,logicalCores,freeMemoryMb,-1);
        }
        public static int SuggestedWorkers(List<ConvertItem> pending,int logicalCores,long freeMemoryMb){
            if(pending==null||pending.Count<=1)return 1;
            long totalBytes=0;
            int stepCount=0,standalonePrt=0;
            var asmCountByDir=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
            var asmMaxByDir=new Dictionary<string,long>(StringComparer.OrdinalIgnoreCase);
            var swBytesByDir=new Dictionary<string,long>(StringComparer.OrdinalIgnoreCase);
            foreach(ConvertItem item in pending){
                totalBytes+=item.Size;
                string ext=(Path.GetExtension(item.Source)??"").ToLowerInvariant();
                string d="";
                try{ d=Path.GetDirectoryName(item.Source)??""; }catch{}
                if(ext==".sldasm"||ext==".sldprt"){
                    long curSw; swBytesByDir.TryGetValue(d,out curSw); swBytesByDir[d]=curSw+item.Size;
                }
                if(ext==".sldasm"){
                    int c; asmCountByDir.TryGetValue(d,out c); asmCountByDir[d]=c+1;
                    long m; asmMaxByDir.TryGetValue(d,out m); if(item.Size>m)asmMaxByDir[d]=item.Size;
                } else if(ext==".stp"||ext==".step"){
                    stepCount++;
                }
            }
            int asmUnits=0;
            int heavyAsmDirs=0;
            int serializedAsmDirs=0;
            foreach(var kv in asmCountByDir){
                string d=kv.Key;
                int c=kv.Value;
                long maxSz=asmMaxByDir[d];
                long dirSw=0; swBytesByDir.TryGetValue(d,out dirSw);
                if(IsSerializedAsmDir(c,maxSz)){
                    asmUnits+=1;
                    serializedAsmDirs++;
                } else {
                    asmUnits+=c;
                }
                if(IsHeavyAssembly(maxSz,dirSw))heavyAsmDirs++;
            }
            foreach(ConvertItem item in pending){
                string ext=(Path.GetExtension(item.Source)??"").ToLowerInvariant();
                if(ext==".sldprt"){
                    string d=""; try{ d=Path.GetDirectoryName(item.Source)??""; }catch{}
                    // 与装配同目录的零件大概率随装配一并转出，不虚增并发进程数；独立目录或无装配批次的零件正常计入并发度。
                    if(asmCountByDir.Count==0||!asmCountByDir.ContainsKey(d))standalonePrt++;
                }
            }
            int lightUnits=stepCount+standalonePrt;
            int effectiveUnits=asmUnits+lightUnits;
            // 同目录有大量零件（>30）且无重型/串行大装配时，允许适度并发处理可能未被引用的散件
            if(effectiveUnits<=1&&pending.Count>30&&serializedAsmDirs==0&&heavyAsmDirs==0)effectiveUnits=2;
            if(effectiveUnits<=1)return 1;
            int chosen=SuggestedWorkers(effectiveUnits,logicalCores,freeMemoryMb,totalBytes);
            if(heavyAsmDirs>0){
                int heavyMemCap;
                if(lightUnits==0){
                    // 纯大型站位装配批次：每个重型装配实例预留 5000 MB 物理内存；每个站位本身即数分钟大任务，
                    // 内存与核数充裕时直接按独立装配单元并发，不受小文件 fileCount<=3→2 的启动摊销阈值压制
                    int byCores=logicalCores<6?1:(logicalCores<12?2:(logicalCores<28?3:4));
                    heavyMemCap=Math.Max(1,(int)(freeMemoryMb/5000L));
                    chosen=Math.Max(1,Math.Min(4,Math.Min(Math.Min(byCores,heavyMemCap),Math.Max(1,asmUnits))));
                } else {
                    // 混合批次（大装配 + 独立 STEP/零件）：1 个重型装配预留 4500 MB，其余轻量进程各预留 2500 MB
                    heavyMemCap=1+Math.Max(0,(int)((freeMemoryMb-4500L)/2500L));
                    chosen=Math.Max(1,Math.Min(chosen,heavyMemCap));
                }
            }
            return chosen;
        }
        public static int SuggestedWorkers(int fileCount,int logicalCores,long freeMemoryMb,long totalBytes){
            if(fileCount<=1)return 1;
            // 极小的 2~3 个小零件（合计 < 1 MB）：单进程几秒即完，不必多起第二个 CAD
            if(totalBytes>=0&&fileCount<=3&&totalBytes<1024L*1024L)return 1;
            int byCores;
            if(logicalCores<6)byCores=1;
            else if(logicalCores<12)byCores=2;
            else if(logicalCores<28)byCores=3;
            else byCores=4;
            int byMem=Math.Max(1,(int)(freeMemoryMb/2500L));
            int byFiles;
            if(fileCount<=3)byFiles=2;
            else if(fileCount<=11)byFiles=3;
            else byFiles=4;
            int chosen=Math.Min(4,Math.Min(byFiles,Math.Min(byCores,byMem)));
            return Math.Max(1,chosen);
        }
    }

    // 跨进程无锁动态任务队列：所有待转文件按优先级（0=装配 → 1=STEP → 2=零件 → 3=工程图）+ LPT（大文件优先）写入队列目录，
    // 各个 worker 通过 NTFS 原子重命名（File.Move）动态抢占下一个任务，消除静态分桶的长尾空等。
    // 三重护栏：
    // 1. 同目录大装配串行（.d<hash>）：大装配/多装配目录同一时刻只允许 1 个进程打开其 .SLDASM（最大总装最先开），
    //    避免子装配与总装并发重转、杜绝源目录 .par 写锁碰撞（DISP_E_EXCEPTION）与 _0 副本泛滥；其他进程自动跳过去并行跑 STEP 或其他站位目录。
    // 2. 重型装配内存门控（.h）：已有重型装配（≥20 MB 或目录 ≥250 MB）在跑时，仅当实时可用物理内存 ≥5500 MB 才放行第二个并发重型装配，防爆内存换页。
    // 3. 装配体相位屏障：仍有装配体在转换中时暂缓抢占 .SLDPRT（2_），待装配落盘后毫秒级跳过已转零件。
    public static class ConvertQueue {
        public const string TaskExtension=".task";
        public const long HeavyConcurrentMinFreeMb=5500L;
        static int TaskPhase(string source){
            string ext=(Path.GetExtension(source)??"").ToLowerInvariant();
            if(ext==".sldasm")return 0;
            if(ext==".stp"||ext==".step")return 1;
            if(ext==".sldprt")return 2;
            return 3;
        }
        public static int Write(string queueDir,List<ConvertItem> items){
            return Write(queueDir,items,null);
        }
        public static int Write(string queueDir,List<ConvertItem> items,ConvertOptions options){
            Directory.CreateDirectory(queueDir);
            var ordered=ConvertPlanner.OrderForQueue(items);
            var asmCountByDir=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
            var asmMaxByDir=new Dictionary<string,long>(StringComparer.OrdinalIgnoreCase);
            var swBytesByDir=new Dictionary<string,long>(StringComparer.OrdinalIgnoreCase);
            foreach(ConvertItem item in ordered){
                string ext=(Path.GetExtension(item.Source)??"").ToLowerInvariant();
                string d="";
                try{ d=Path.GetDirectoryName(Path.GetFullPath(item.Source))??""; }catch{}
                if(ext==".sldasm"||ext==".sldprt"){
                    long cur; swBytesByDir.TryGetValue(d,out cur); swBytesByDir[d]=cur+item.Size;
                }
                if(ext==".sldasm"){
                    int c; asmCountByDir.TryGetValue(d,out c); asmCountByDir[d]=c+1;
                    long m; asmMaxByDir.TryGetValue(d,out m); if(item.Size>m)asmMaxByDir[d]=item.Size;
                }
            }
            var utf8=new UTF8Encoding(false);
            for(int i=0;i<ordered.Count;i++){
                ConvertItem item=ordered[i];
                int phase=TaskPhase(item.Source);
                string tag="";
                if(phase==0){
                    string d="";
                    try{ d=Path.GetDirectoryName(Path.GetFullPath(item.Source))??""; }catch{}
                    int dc=0; asmCountByDir.TryGetValue(d,out dc);
                    long dm=0; asmMaxByDir.TryGetValue(d,out dm);
                    long dsw=0; swBytesByDir.TryGetValue(d,out dsw);
                    bool sameOut=false;
                    if(options!=null&&!string.IsNullOrWhiteSpace(options.OutputRoot)){
                        try{
                            string tgtDir=Path.GetDirectoryName(ConvertPlanner.TopTarget(item,options));
                            sameOut=ConvertPlanner.SameDir(d,tgtDir);
                        }catch{}
                    }
                    if(ConvertPlanner.IsSerializedAsmDir(dc,dm)||sameOut){
                        string dh=ConvertWrittenIndex.HashPath(d);
                        if(dh.Length>8)dh=dh.Substring(0,8);
                        tag+=".d"+dh;
                    }
                    if(ConvertPlanner.IsHeavyAssembly(item.Size,dsw)){
                        tag+=".h";
                    }
                }
                string file=Path.Combine(queueDir,phase.ToString(CultureInfo.InvariantCulture)+"_"+i.ToString("D6",CultureInfo.InvariantCulture)+tag+TaskExtension);
                string line=item.Index.ToString(CultureInfo.InvariantCulture)+"\t"+item.Source+"\t"+(item.Root??"")+"\t"+item.Size.ToString(CultureInfo.InvariantCulture);
                File.WriteAllText(file,line,utf8);
            }
            return ordered.Count;
        }
        public static bool HasPending(string queueDir){
            if(string.IsNullOrEmpty(queueDir)||!Directory.Exists(queueDir))return false;
            try{
                foreach(string _ in Directory.EnumerateFiles(queueDir,"*"+TaskExtension))return true;
            }catch{}
            return false;
        }
        static void InspectActiveAssemblies(string queueDir,out bool anyActive,out HashSet<string> activeDirs,out int activeHeavy){
            anyActive=false;
            activeDirs=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            activeHeavy=0;
            try{
                foreach(string active in Directory.EnumerateFiles(queueDir,"0_*.task.w*")){
                    if(!IsClaimOwnerAlive(active))continue;
                    anyActive=true;
                    string name=Path.GetFileName(active);
                    string dirTag=ExtractDirTag(name);
                    if(dirTag!=null)activeDirs.Add(dirTag);
                    if(IsHeavyTaskName(name))activeHeavy++;
                }
            }catch{}
        }
        static string ExtractDirTag(string fileName){
            int idx=fileName.IndexOf(".d",StringComparison.OrdinalIgnoreCase);
            if(idx<0||idx+10>fileName.Length)return null;
            int end=fileName.IndexOf('.',idx+2);
            if(end<0)return null;
            string tag=fileName.Substring(idx+2,end-(idx+2));
            return tag.Length==8?tag:null;
        }
        static bool IsHeavyTaskName(string fileName){
            return fileName.IndexOf(".h"+TaskExtension,StringComparison.OrdinalIgnoreCase)>=0;
        }
        static bool IsClaimOwnerAlive(string claimedPath){
            try{
                string name=Path.GetFileName(claimedPath);
                int pIdx=name.LastIndexOf(".p",StringComparison.OrdinalIgnoreCase);
                if(pIdx<0)return true;
                int pid;
                if(!int.TryParse(name.Substring(pIdx+2),NumberStyles.Integer,CultureInfo.InvariantCulture,out pid)||pid<=0)return true;
                using(var p=System.Diagnostics.Process.GetProcessById(pid)){ return !p.HasExited; }
            }catch{ return false; }
        }
        public static Func<long> FreeMemoryProvider;
        public static long CurrentFreeMemoryMb(){ return LiveFreeMemoryMb(); }
        static long LiveFreeMemoryMb(){
            if(FreeMemoryProvider!=null){ try{ return FreeMemoryProvider(); }catch{} }
            try{
                var st=new MEMORYSTATUSEX();st.dwLength=(uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(MEMORYSTATUSEX));
                if(GlobalMemoryStatusEx(ref st))return (long)(st.ullAvailPhys/(1024UL*1024UL));
            }catch{}
            return 8192L;
        }
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct MEMORYSTATUSEX {
            public uint dwLength;public uint dwMemoryLoad;public ulong ullTotalPhys;public ulong ullAvailPhys;
            public ulong ullTotalPageFile;public ulong ullAvailPageFile;public ulong ullTotalVirtual;public ulong ullAvailVirtual;public ulong ullAvailExtendedVirtual;
        }
        [System.Runtime.InteropServices.DllImport("kernel32.dll",SetLastError=true)]
        static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

        public static bool TryClaimNext(string queueDir,int workerId,out ConvertItem item,out string claimedPath){
            return TryClaimNext(queueDir,workerId,out item,out claimedPath,null);
        }
        public static bool TryClaimNext(string queueDir,int workerId,out ConvertItem item,out string claimedPath,Action<int> onDeferredBehindHeavy){
            item=null;claimedPath=null;
            if(string.IsNullOrEmpty(queueDir)||!Directory.Exists(queueDir))return false;
            int selfPid=0;
            try{ selfPid=System.Diagnostics.Process.GetCurrentProcess().Id; }catch{}
            string mutexName="Local\\TianGongFormatConvert.Queue."+ConvertWrittenIndex.HashPath(Path.GetFullPath(queueDir));
            DateTime? heavyWaitStart=null;
            while(true){
                bool deferred=false;
                bool claimed=false;
                int lastActiveHeavy=0;
                System.Threading.Mutex mutex=null;
                bool owned=false;
                try{
                    try{
                        mutex=new System.Threading.Mutex(false,mutexName);
                        try{ owned=mutex.WaitOne(TimeSpan.FromSeconds(30)); }
                        catch(System.Threading.AbandonedMutexException){ owned=true; }
                    }catch{}
                    string[] files;
                    try{ files=Directory.GetFiles(queueDir,"*"+TaskExtension); }catch{ return false; }
                    if(files.Length==0)return false;
                    Array.Sort(files,StringComparer.OrdinalIgnoreCase);
                    bool anyActiveAsm;HashSet<string> activeDirs;int activeHeavy;
                    InspectActiveAssemblies(queueDir,out anyActiveAsm,out activeDirs,out activeHeavy);
                    lastActiveHeavy=activeHeavy;
                    long freeMemMb=-1;
                    for(int i=0;i<files.Length;i++){
                        string src=files[i];
                        string fname=Path.GetFileName(src);
                        if(fname.StartsWith("0_",StringComparison.Ordinal)){
                            string dirTag=ExtractDirTag(fname);
                            if(dirTag!=null&&activeDirs.Contains(dirTag)){
                                deferred=true;
                                continue;
                            }
                            if(IsHeavyTaskName(fname)&&activeHeavy>=1){
                                if(freeMemMb<0)freeMemMb=LiveFreeMemoryMb();
                                if(freeMemMb<HeavyConcurrentMinFreeMb){
                                    deferred=true;
                                    continue;
                                }
                            }
                        }
                        // 若当前排在前面的只剩 .SLDPRT（2_），而仍有装配体（0_*.task.w*）在转换中，短暂等待装配体保存完零件，避免重复转换
                        if(fname.StartsWith("2_",StringComparison.Ordinal)&&anyActiveAsm){
                            deferred=true;
                            break;
                        }
                        string mine=src+".w"+workerId.ToString(CultureInfo.InvariantCulture)+".p"+selfPid.ToString(CultureInfo.InvariantCulture);
                        try{
                            File.Move(src,mine);
                        }catch(IOException){
                            continue;
                        }catch(UnauthorizedAccessException){
                            continue;
                        }
                        string raw=ReadAllTextShared(mine);
                        if(raw==null){ Complete(mine); continue; }
                        string[] parts=raw.Trim().Split('\t');
                        if(parts.Length<3){ Complete(mine); continue; }
                        var parsed=new ConvertItem();
                        int idx;
                        if(!int.TryParse(parts[0],NumberStyles.Integer,CultureInfo.InvariantCulture,out idx)){ Complete(mine); continue; }
                        parsed.Index=idx;
                        parsed.Source=parts[1];
                        parsed.Root=parts[2];
                        parsed.Worker=workerId;
                        long sz;
                        if(parts.Length>3&&long.TryParse(parts[3],NumberStyles.Integer,CultureInfo.InvariantCulture,out sz))parsed.Size=sz;
                        else{ try{ parsed.Size=new FileInfo(parsed.Source).Length; }catch{ parsed.Size=0; } }
                        item=parsed;
                        claimedPath=mine;
                        claimed=true;
                        break;
                    }
                }finally{
                    if(owned&&mutex!=null)try{ mutex.ReleaseMutex(); }catch{}
                    if(mutex!=null)try{ mutex.Dispose(); }catch{}
                }
                if(claimed)return true;
                if(deferred){
                    if(lastActiveHeavy>0){
                        if(heavyWaitStart==null)heavyWaitStart=DateTime.UtcNow;
                        if(onDeferredBehindHeavy!=null){
                            try{ onDeferredBehindHeavy((int)(DateTime.UtcNow-heavyWaitStart.Value).TotalSeconds); }catch{}
                        }
                    } else {
                        heavyWaitStart=null;
                    }
                    System.Threading.Thread.Sleep(400);
                    continue;
                }
                return false;
            }
        }
        static string ReadAllTextShared(string path){
            for(int attempt=0;attempt<5;attempt++){
                try{
                    using(var fs=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
                    using(var sr=new StreamReader(fs,new UTF8Encoding(false)))
                        return sr.ReadToEnd();
                }catch(IOException){
                    System.Threading.Thread.Sleep(20);
                }catch{ return null; }
            }
            return null;
        }
        public static void Complete(string claimedPath){
            if(string.IsNullOrEmpty(claimedPath))return;
            try{ if(File.Exists(claimedPath))File.Delete(claimedPath); }catch{}
        }
    }

    // 跨进程共享的"本次运行已落盘"索引：某个 worker 转换装配体时顺带保存了零件 P.par，
    // 在 workRoot\written\ 下同时登记目标输出路径哈希和源文件路径哈希（src- 前缀）；
    // 即使天工因历史记录给组件追加了 _0/_5 后缀，其他 worker 领到原 P.SLDPRT 时仍能凭源文件索引 O(1) 精准跳过，避免重复转换。
    public static class ConvertWrittenIndex {
        public static void Mark(string workRoot,string targetPath){
            WriteMarker(workRoot,"",targetPath);
        }
        public static void MarkSource(string workRoot,string sourcePath){
            WriteMarker(workRoot,"src-",sourcePath);
        }
        public static bool Contains(string workRoot,string targetPath){
            return HasMarker(workRoot,"",targetPath);
        }
        public static bool ContainsSource(string workRoot,string sourcePath){
            return HasMarker(workRoot,"src-",sourcePath);
        }
        public static bool ContainsAny(string workRoot,IEnumerable<string> targetPaths){
            if(string.IsNullOrEmpty(workRoot)||targetPaths==null)return false;
            foreach(string p in targetPaths)if(Contains(workRoot,p))return true;
            return false;
        }
        // 不依赖活跃 CAD 会话的跨进程已落盘校验：让释放掉空闲 CAD 内存的工作进程也能 0 秒跳过已随装配转好的零件，避免白起 CAD。
        public static bool ContainsAnyOrSource(string workRoot,IEnumerable<string> targetPaths,string sourcePath){
            if(ContainsAny(workRoot,targetPaths))return true;
            if(!string.IsNullOrEmpty(sourcePath)&&ContainsSource(workRoot,sourcePath))return true;
            return false;
        }
        static void WriteMarker(string workRoot,string prefix,string path){
            if(string.IsNullOrEmpty(workRoot)||string.IsNullOrEmpty(path))return;
            try{
                string full=Path.GetFullPath(path);
                string dir=Path.Combine(workRoot,"written");
                Directory.CreateDirectory(dir);
                string marker=Path.Combine(dir,prefix+HashPath(full)+".done");
                if(!File.Exists(marker)){
                    using(var fs=new FileStream(marker,FileMode.OpenOrCreate,FileAccess.Write,FileShare.ReadWrite)){}
                }
            }catch{}
        }
        static bool HasMarker(string workRoot,string prefix,string path){
            if(string.IsNullOrEmpty(workRoot)||string.IsNullOrEmpty(path))return false;
            try{
                string full=Path.GetFullPath(path);
                string marker=Path.Combine(workRoot,"written",prefix+HashPath(full)+".done");
                return File.Exists(marker);
            }catch{ return false; }
        }
        public static string HashPath(string normalizedPath){
            using(var sha=System.Security.Cryptography.SHA256.Create()){
                byte[] bytes=sha.ComputeHash(Encoding.UTF8.GetBytes((normalizedPath??"").ToLowerInvariant()));
                var sb=new StringBuilder(16);
                for(int i=0;i<8;i++)sb.Append(bytes[i].ToString("x2"));
                return sb.ToString();
            }
        }
    }
    public sealed class ConvertRow {
        public int Index;public string Source;public string Status;public double OpenSeconds;public double SaveSeconds;
        public double TotalSeconds;public int Documents;public string Target;public string Error;public int Worker;
        public bool NoRetry;   // 重试也没用的失败（天工打不开 SolidWorks 工程图），worker 不再白转一遍
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
    // Builds a Windows command line that CommandLineToArgvW (and the .NET runtime) splits back into
    // exactly the given arguments. Wrapping a path as "\""+path+"\"" breaks when the path ends with a
    // backslash: a drive root D:\ becomes "D:\" whose \" is read as a literal quote, and every
    // following argument shifts.
    public static class CommandLine {
        public static string Quote(string arg){
            if(arg==null)arg="";
            if(arg.Length>0&&arg.IndexOfAny(new[]{' ','\t','\n','\v','"'})<0)return arg;
            var sb=new StringBuilder();sb.Append('"');
            int i=0;
            while(true){
                int slashes=0;
                while(i<arg.Length&&arg[i]=='\\'){ slashes++; i++; }
                if(i==arg.Length){ sb.Append('\\',slashes*2); break; }            // before the closing quote
                if(arg[i]=='"')sb.Append('\\',slashes*2+1).Append('"');           // escaped literal quote
                else sb.Append('\\',slashes).Append(arg[i]);                      // backslashes stay literal
                i++;
            }
            sb.Append('"');
            return sb.ToString();
        }
        public static string Join(params string[] args){
            var parts=new string[args.Length];
            for(int i=0;i<args.Length;i++)parts[i]=Quote(args[i]);
            return string.Join(" ",parts);
        }
    }
    public sealed class ConvertSummary {
        public int Total,Converted,Skipped,Failed,Pending;
        public int Workers=1;   // 只有真的并行（>1 个进程）才报"并行加速"：单进程时报出"并行加速 0.93 倍"让人以为出了问题
        public double SourceSeconds,WallSeconds;
        public double SpeedupFactor { get { return WallSeconds<=0?0:SourceSeconds/WallSeconds; } }
        public static ConvertSummary From(List<ConvertRow> rows,double wallSeconds){
            var s=new ConvertSummary();s.WallSeconds=wallSeconds;s.Total=rows.Count;
            foreach(ConvertRow r in rows){
                if(r.Status=="ok"){s.Converted++;s.SourceSeconds+=r.TotalSeconds;}
                else if(r.Status=="skip")s.Skipped++;
                else if(r.Status=="pending")s.Pending++;
                else s.Failed++;
            }
            return s;
        }
        public string Describe(){
            var sb=new StringBuilder();
            sb.Append("总计 ").Append(Total).Append(" 个文件：成功 ").Append(Converted).Append("，跳过 ").Append(Skipped).Append("，失败 ").Append(Failed);
            // 停止了 / 工作进程中途退出（未激活、天工崩了）时剩下的文件一个都没转：原来不提，50 个只转了 3 个也报"成功 3，失败 0"像是全做完了
            if(Pending>0)sb.Append("，没转 ").Append(Pending).Append("（转换被停止或工作进程中途退出，见日志；再点「开始转换」会接着转）");
            sb.Append("。");
            sb.Append(" 单件累计 ").Append(SourceSeconds.ToString("F1")).Append(" 秒，实际耗时 ").Append(WallSeconds.ToString("F1")).Append(" 秒");
            if(Workers>1&&WallSeconds>0.5)sb.Append("（并行加速 ").Append(SpeedupFactor.ToString("F2")).Append(" 倍）");
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
