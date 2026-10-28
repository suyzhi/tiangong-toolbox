using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Runtime.InteropServices;
using F=SolidEdgeFramework;
using A=SolidEdgeAssembly;

namespace TianGongCadSuite {
    // One CAD session = one hidden TianGong CAD process. Documents are opened, translated by the
    // CAD's own SolidWorks translator, then written out and closed. The session is reused for every
    // file in its work list so the CAD start-up cost is paid once per worker, not once per file.
    public sealed class CadConvertSession : IDisposable {
        F.Application app;
        readonly Action<string> log;
        readonly string lockRoot;
        public double BootSeconds {get;private set;}
        // 这次会话里已经存出来的文件（含装配顺带存的零件）：勾了「覆盖」时，轮到这些零件本身别再转一遍（0.10.3）
        readonly HashSet<string> written=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public bool WroteThisRun(IEnumerable<string> paths){ foreach(string p in paths){ try{ if(written.Contains(Path.GetFullPath(p)))return true; }catch{} } return false; }
        public int ProcessId {get;private set;}

        static readonly int BusyTimeoutMs=180000;
        public CadConvertSession(Action<string> log,string lockRoot){
            TianGongCadSuite.Licensing.LicenseGate.Require();
            this.log=log==null?delegate(string s){}:log;
            this.lockRoot=lockRoot;
            DropStaleStages();
            var watch=Stopwatch.StartNew();
            // 转换器要独占一个 CAD：下面会隐藏窗口、关掉提示、把撤销步数设成 0，每个文件转完还会**不保存**关掉全部文档。
            // 万一 CreateInstance 连上的是用户正在用的那个天工（COM 服务器按多用户注册时就会这样），这些全落在用户头上。
            // 所以先记下已经在跑的天工进程，连上以后核对进程号：是老进程就立刻放手，什么都不动。
            var running=new HashSet<int>();
            try{ foreach(var name in new[]{"TianGong","Edge"})foreach(var p in Process.GetProcessesByName(name)){ running.Add(p.Id); p.Dispose(); } }catch{}
            app=Retry(delegate(){ return (F.Application)Activator.CreateInstance(Type.GetTypeFromProgID("SolidEdge.Application")); },"启动天工CAD",600);
            BootSeconds=watch.Elapsed.TotalSeconds;
            try{ ProcessId=app.ProcessID; }catch{}
            if(ProcessId!=0&&running.Contains(ProcessId)){
                try{ Marshal.FinalReleaseComObject(app); }catch{}
                app=null;
                throw new InvalidOperationException("没能另起一个天工 CAD（连上的是已经打开的那个）。为免关掉你打开的文件，转换已停止：请先关掉天工 CAD 再转换，或直接用 TianGongConverter.exe。");
            }
            Quiet(delegate(){ app.DisplayAlerts=false; },"DisplayAlerts");
            Quiet(delegate(){ app.ScreenUpdating=false; },"ScreenUpdating");
            Quiet(delegate(){ app.ShowStartupScreen=false; },"ShowStartupScreen");
            Quiet(delegate(){ app.SetOLEServerBusyTimeout(BusyTimeoutMs); },"SetOLEServerBusyTimeout");
            Quiet(delegate(){ app.SetOLERequestPendingTimeout(BusyTimeoutMs); },"SetOLERequestPendingTimeout");
            // Undo history is pure overhead for a converter; dropping it removes per-document bookkeeping.
            Quiet(delegate(){ app.SetGlobalParameter(F.ApplicationGlobalConstants.seApplicationGlobalPartandAsmUndoSteps,0); },"UndoSteps");
            try{ app.Visible=false; }catch(Exception e){ log("隐藏窗口失败："+e.Message); }
        }
        void Quiet(Action action,string what){ try{ action(); }catch(Exception e){ log(what+" 不可用："+e.Message); } }
        static string Hr(Exception e){ var c=e as COMException; return c==null?"":" (0x"+c.ErrorCode.ToString("X8")+")"; }
        T Retry<T>(Func<T> action,string what,int seconds){
            var end=DateTime.Now.AddSeconds(seconds);Exception last=null;
            while(DateTime.Now<end){
                try{ return action(); }
                catch(COMException e){
                    last=e;int hr=e.ErrorCode;
                    if(hr==unchecked((int)0x80010001)||hr==unchecked((int)0x8001010A)||hr==unchecked((int)0x80010100)){
                        try{ app.DoIdle(); }catch{}
                        Thread.Sleep(200);continue;
                    }
                    throw;
                }
            }
            throw new TimeoutException(what+" 超时"+(last==null?"":"："+last.Message));
        }
        void Retry(Action action,string what,int seconds){ Retry<object>(delegate(){ action(); return null; },what,seconds); }

        public static string ExtensionFor(F.DocumentTypeConstants type){
            if(type==F.DocumentTypeConstants.igPartDocument)return ".par";
            if(type==F.DocumentTypeConstants.igAssemblyDocument)return ".asm";
            if(type==F.DocumentTypeConstants.igSheetMetalDocument)return ".psm";
            if(type==F.DocumentTypeConstants.igDraftDocument)return ".dft";
            if(type==F.DocumentTypeConstants.igWeldmentAssemblyDocument)return ".asm";
            return ".par";
        }
        static bool HasNativeExtension(string name){
            string lower=(name??"").ToLowerInvariant();
            return lower.EndsWith(".par")||lower.EndsWith(".asm")||lower.EndsWith(".psm")||lower.EndsWith(".dft");
        }

        sealed class Node { public F.SolidEdgeDocument Doc; public List<Node> Kids=new List<Node>(); }
        static Node BuildNode(F.SolidEdgeDocument doc,int depth,Dictionary<string,Node> seen,Action<string> log){
            string key="";
            try{ key=doc.FullName+"|"+doc.Type; }catch{}
            if(key.Length>0){ Node existing; if(seen.TryGetValue(key,out existing))return existing; }
            var node=new Node();node.Doc=doc;
            if(key.Length>0)seen[key]=node;
            var assembly=doc as A.AssemblyDocument;
            if(assembly!=null&&depth<12){
                A.Occurrences occurrences=null;
                try{ occurrences=assembly.Occurrences; }catch(Exception e){ log("读取装配结构失败："+e.Message); }
                if(occurrences!=null){
                    int count=0;
                    try{ count=occurrences.Count; }catch(Exception e){ log("装配实例数读取失败："+e.Message); }
                    for(int i=1;i<=count;i++){
                        object child=null;
                        try{ child=((A.Occurrence)occurrences.Item(i)).OccurrenceDocument; }catch{}
                        var childDoc=child as F.SolidEdgeDocument;
                        if(childDoc==null)continue;
                        node.Kids.Add(BuildNode(childDoc,depth+1,seen,log));
                    }
                }
            }
            return node;
        }
        // Post-order: every component is written before the assembly that references it, so the
        // assembly records the final (output) paths. One pass over the occurrence tree = O(documents).
        static void PostOrder(Node node,List<Node> output,HashSet<Node> visited){
            foreach(Node kid in node.Kids){ if(visited.Add(kid))PostOrder(kid,output,visited); }
            output.Add(node);
        }

        public ConvertRow Convert(ConvertItem item,ConvertOptions options){
            var row=new ConvertRow();
            row.Index=item.Index;row.Source=item.Source;row.Target=item.Target;row.Worker=item.Worker;
            var total=Stopwatch.StartNew();
            F.SolidEdgeDocument top=null;
            Dictionary<string,string> staged=null;   // 临时文件夹里的拷贝 → 原文件
            try{
                top=Retry(delegate(){ return (F.SolidEdgeDocument)app.Documents.Open(item.Source); },"打开 "+Path.GetFileName(item.Source),7200);
                // 0.10.2：SolidWorks 装配打开后一个零件都没有 = 天工没找到它引用的零件（零件不在装配的文件夹里）。
                // 原来照样存出空装配、报成功。把同一项目里的 SolidWorks 文件拷到一个临时文件夹再开；还是空的就报失败、说清楚
                if(EmptyAssembly(top)&&ConvertPlanner.IsSolidWorks(item.Source)){
                    var st=StageFor(item);
                    if(st!=null){
                        CloseAll();top=null;
                        staged=st.Map;
                        string again=Path.Combine(st.Dir,Path.GetFileName(item.Source));
                        top=Retry(delegate(){ return (F.SolidEdgeDocument)app.Documents.Open(again); },"打开 "+Path.GetFileName(item.Source),7200);
                    }
                    // 拷到一起还有找不到的：整个装配是空的就报失败；只缺几个（比如项目外的标准件）照样转，日志里说一声
                    if(EmptyAssembly(top)&&!TopEmpty(top))log("「"+Path.GetFileName(item.Source)+"」里还有零件找不到（不在这个项目的文件夹里），转出来的装配会缺这些件。");
                    else if(EmptyAssembly(top))
                        throw new InvalidOperationException("装配打开后是空的：天工找不到这个 SolidWorks 装配引用的零件（装配里记的是零件原来的位置）。把装配和它用到的零件放进同一个文件夹，或者输入里加上零件所在的上级文件夹，再转。");
                }
                row.OpenSeconds=total.Elapsed.TotalSeconds;
                var root=BuildNode(top,0,new Dictionary<string,Node>(),log);
                var documents=new List<Node>();
                PostOrder(root,documents,new HashSet<Node>());
                row.Documents=documents.Count;
                var saveWatch=Stopwatch.StartNew();
                int failures=0;string firstError=null;
                var writtenFrom=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);   // 输出路径 → 源文件
                foreach(Node node in documents){
                    F.SolidEdgeDocument doc=node.Doc;
                    string name=null;F.DocumentTypeConstants type=F.DocumentTypeConstants.igUnknownDocument;
                    try{ name=doc.Name; }catch{}
                    try{ type=doc.Type; }catch{}
                    string ext=ExtensionFor(type);
                    string assigned=SafeFullNameOrNull(doc);
                    string original;
                    if(staged!=null&&assigned!=null)assigned=Unstage(assigned,staged,item);   // 临时拷贝 → 按原文件的位置放输出（镜像目录照常）
                    // The CAD renames repeat components of one session to "Part_1", "Part_2"... Using the
                    // assigned source name instead keeps the output deterministic and collapses those
                    // duplicates onto a single file, exactly like the source project is organised.
                    if(assigned!=null)name=Path.GetFileName(assigned);
                    string finalName=name;
                    if(finalName==null||finalName.Length==0)finalName=Path.GetFileNameWithoutExtension(item.Source)+ext;
                    if(!HasNativeExtension(finalName))finalName=finalName+ext;
                    // The top-level extension follows the document the CAD actually produced: a .stp can
                    // import as a part or as an assembly, and a SolidWorks part can arrive as sheet metal.
                    string target;
                    if(node==root){
                        target=ConvertPlanner.TopTargetFor(item,options,ext);
                        item.Target=target;row.Target=target;
                    } else {
                        target=ConvertPlanner.ComponentTarget(assigned,finalName,item,options);
                    }
                    // 0.10.2：只有"同一个源文件"才并。原来按输出路径判：不镜像目录（平铺输出）时，两个文件夹里各有一个
                    // 「零件1.par」（不同零件），第二个被当成"重复实例"直接丢掉，输出的装配两处都引用了第一个 —— 零件悄悄错了
                    string clash;
                    string claimed=ConvertPlanner.ClaimTarget(writtenFrom,target,assigned??finalName,out clash);
                    if(claimed==null){
                        log("同一零件的重复实例，已并入 "+finalName+"。");
                        continue;
                    }
                    if(clash!=null)log("别的文件夹里还有一个不同的 "+finalName+"（"+clash+"），这一个另存为 "+Path.GetFileName(claimed)+"。");
                    target=claimed;
                    try{
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        if(written.Contains(Path.GetFullPath(target))){ log(finalName+" 这次前面已经存过，不再重存。"); continue; }
                        if(!TryClaim(lockRoot,target)){
                            log("另一个进程已生成 "+finalName+"，本次跳过写入。");
                            continue;
                        }
                        string local=target;
                        using(AcquireSaveLock()){ Retry(delegate(){ doc.SaveAs(local); },"保存 "+finalName,1800); }
                        written.Add(Path.GetFullPath(local));
                    }catch(Exception e){
                        failures++;if(firstError==null)firstError=finalName+"："+e.Message+Hr(e);
                        log("保存失败 "+finalName+" "+e.Message+Hr(e));
                    }
                }
                row.SaveSeconds=saveWatch.Elapsed.TotalSeconds;
                if(failures>0){ row.Status="fail"; row.Error=failures+" 个文档保存失败；首个："+firstError; }
                else row.Status="ok";
            }catch(Exception e){
                row.Status="fail";row.Error=e.Message+Hr(e);
                // 0.10.2：SolidWorks 工程图天工打不开（宝马托盘里试了一张零件图、一张装配图，都是一打开就 E_FAIL）。
                // 原来每张报一句"未指定的错误"、再白重试一遍；这里说清楚、不重试
                if(top==null&&string.Equals(Path.GetExtension(item.Source),".slddrw",StringComparison.OrdinalIgnoreCase)){
                    row.Error="天工打不开这张 SolidWorks 工程图（.slddrw）：天工的 SolidWorks 转换只认零件和装配。工程图请对转好的模型在天工里重新出图，或者在 SolidWorks 里另存为 DWG / DXF 再导入天工。";
                    row.NoRetry=true;
                }
                log("转换失败 "+Path.GetFileName(item.Source)+"："+e.Message+Hr(e));
            }finally{
                CloseAll();
                row.TotalSeconds=total.Elapsed.TotalSeconds;
            }
            if(row.Status=="ok"&&options.VerifyReopen)Verify(item,row);
            return row;
        }
        // 「拷到一起再开」的临时文件夹（0.10.3）：同一个项目（StageFolder）整批只拷一次，后面的装配接着用，会话结束才删。
        // 0.10.2 是每个装配各拷一次再删：零件 / 装配分文件夹的大项目（几百个装配、几千个零件、几个 GB）每个装配都把整个项目拷一遍，转一晚上也转不完、C 盘塞满
        sealed class Stage { public string Dir; public Dictionary<string,string> Map=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase); }
        readonly Dictionary<string,Stage> stages=new Dictionary<string,Stage>(StringComparer.OrdinalIgnoreCase);
        Stage StageFor(ConvertItem item){
            string key=ConvertPlanner.StageFolder(item.Source,item.Root)??("*"+item.Source);
            Stage st;
            if(!stages.TryGetValue(key,out st)){
                var files=ConvertPlanner.StageSources(item.Source,item.Root);
                if(files.Count<=1){ stages[key]=null; return null; }
                st=new Stage();
                st.Dir=Path.Combine(Path.GetTempPath(),"TianGongConverter","stage-"+Guid.NewGuid().ToString("N").Substring(0,8));
                Directory.CreateDirectory(st.Dir);
                stages[key]=st;
                // 一个一个拷、拷不了的跳过（0.10.3）：原来项目里有一个路径超长 / 打不开的文件，File.Copy 一抛整个装配失败，
                // 重试时又拿到拷了一半的临时文件夹，报"装配打开后是空的"
                int skipped=0;string firstSkip=null;
                foreach(string f in files){
                    try{ string copy=Path.Combine(st.Dir,Path.GetFileName(f)); File.Copy(f,copy,true); st.Map[StemKey(copy)]=f; }
                    catch(Exception e){ skipped++; if(firstSkip==null)firstSkip=Path.GetFileName(f)+"："+e.Message; }
                }
                if(skipped>0)log("有 "+skipped+" 个 SolidWorks 文件没能拷到临时文件夹（路径太长或打不开），用到它们的装配会缺这些件。首个："+firstSkip);   // 导入后文档叫「临时文件夹\名字.par」，按文件夹 + 主文件名对
                log("「"+Path.GetFileName(item.Source)+"」打开后（或它的子装配）是空的：它用到的零件不在装配所在的文件夹，天工找不到。已把同一项目里的 "+files.Count+" 个 SolidWorks 文件拷到临时文件夹再打开（这一批里同项目的其他装配接着用，不再重拷）。");
            }
            if(st==null)return null;
            // 项目里有同名文件时临时文件夹里只放了第一个：轮到的这个装配自己必须是它本身
            string mine=Path.Combine(st.Dir,Path.GetFileName(item.Source)),have;
            if(!st.Map.TryGetValue(StemKey(mine),out have)||!string.Equals(have,item.Source,StringComparison.OrdinalIgnoreCase)){ File.Copy(item.Source,mine,true); st.Map[StemKey(mine)]=item.Source; }
            return st;
        }
        void DropStages(){
            foreach(var st in stages.Values){
                if(st==null)continue;
                // 天工刚退出时文件句柄晚一点放：删不掉就等一下再试
                for(int i=0;;i++){
                    try{ Directory.Delete(st.Dir,true); break; }
                    catch(Exception e){ if(i>=10){ log("临时文件夹没删掉（"+e.Message+"）："+st.Dir); break; } Thread.Sleep(500); }
                }
            }
            stages.Clear();
        }
        // 上次被「停止」掉 / 崩掉的转换留下的临时文件夹（拷的是整个项目，可能几个 GB）：一天以前的删掉
        static void DropStaleStages(){
            try{
                string dir=Path.Combine(Path.GetTempPath(),"TianGongConverter");
                if(!Directory.Exists(dir))return;
                foreach(string d in Directory.GetDirectories(dir,"stage-*")){
                    try{ if(DateTime.Now-Directory.GetLastWriteTime(d)>TimeSpan.FromDays(1))Directory.Delete(d,true); }catch{}
                }
            }catch{}
        }
        // 临时文件夹里的文档 → 原文件所在位置（0.10.3 真机 NOK_Drawer3：没对上的全落进「_外部\Users_…_stage-xxxx」）：
        //   · 同一个零件这次前面已经转过，天工给后来的那份起名「名字_0」—— 去掉 _数字 再对；
        //   · SolidWorks 的虚拟零件（「零件^装配」，只存在装配里、项目里没有单独的文件）—— 放在这个装配原来所在的文件夹
        static string Unstage(string assigned,Dictionary<string,string> staged,ConvertItem item){
            string original;
            if(staged.TryGetValue(StemKey(assigned),out original))return Path.Combine(Path.GetDirectoryName(original),Path.GetFileName(assigned));
            string dir=Path.GetDirectoryName(assigned)??"",stem=Path.GetFileNameWithoutExtension(assigned);
            var m=System.Text.RegularExpressions.Regex.Match(stem,@"^(.*)_\d+$");
            if(m.Success&&staged.TryGetValue(Path.Combine(dir,m.Groups[1].Value),out original))return Path.Combine(Path.GetDirectoryName(original),Path.GetFileName(assigned));
            bool inStage=false;
            foreach(string v in staged.Keys){ inStage=string.Equals(Path.GetDirectoryName(v),dir,StringComparison.OrdinalIgnoreCase); break; }
            return inStage?Path.Combine(Path.GetDirectoryName(item.Source),Path.GetFileName(assigned)):assigned;
        }
        static string StemKey(string path){ return Path.Combine(Path.GetDirectoryName(path)??"",Path.GetFileNameWithoutExtension(path)); }
        // 装配树里有没有"空的装配 / 打不开的实例"：顶层的子装配找到了、子装配里的零件没找到（装配体、零件分文件夹）也算
        static bool TopEmpty(F.SolidEdgeDocument doc){ var a=doc as A.AssemblyDocument; if(a==null)return false; try{ return a.Occurrences.Count==0; }catch{ return false; } }
        static bool EmptyAssembly(F.SolidEdgeDocument doc){ return EmptyAssembly(doc,0,new HashSet<string>(StringComparer.OrdinalIgnoreCase)); }
        static bool EmptyAssembly(F.SolidEdgeDocument doc,int depth,HashSet<string> seen){
            var a=doc as A.AssemblyDocument;
            if(a==null||depth>12)return false;
            try{ if(!seen.Add(a.FullName))return false; }catch{}
            int n=0;try{ n=a.Occurrences.Count; }catch{ return false; }
            if(n==0)return true;
            for(int i=1;i<=n;i++){
                object child=null;
                try{ child=((A.Occurrence)a.Occurrences.Item(i)).OccurrenceDocument; }catch{}
                if(child==null)return true;
                if(EmptyAssembly(child as F.SolidEdgeDocument,depth+1,seen))return true;
            }
            return false;
        }
        static string SafeFullName(F.SolidEdgeDocument doc,ConvertItem item){
            string full=SafeFullNameOrNull(doc);
            return full==null?item.Source:full;
        }
        static string SafeFullNameOrNull(F.SolidEdgeDocument doc){
            try{ string full=doc.FullName; if(full!=null&&Path.IsPathRooted(full))return full; }catch{}
            return null;
        }
        // Optional acceptance check: reopen the produced top document from disk and confirm the CAD
        // still resolves every reference. Catches half-written component files.
        void Verify(ConvertItem item,ConvertRow row){
            var watch=Stopwatch.StartNew();
            try{
                if(!File.Exists(item.Target)){ row.Status="fail"; row.Error="未生成输出文件"; return; }
                var reopened=(F.SolidEdgeDocument)Retry(delegate(){ return app.Documents.Open(item.Target); },"校验打开",1800);
                var assembly=reopened as A.AssemblyDocument;
                if(assembly!=null){
                    bool missing=false;
                    try{ missing=assembly.HasMissingFiles; }catch{}
                    // 子装配里的也查（0.10.3）：原来只看顶层实例，子装配里丢了零件照样报"校验通过"
                    string where=missing?"":MissingIn(assembly,0,new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                    if(missing||where!=null){ row.Status="fail";row.Error="校验时发现丢失的引用"+(string.IsNullOrEmpty(where)?"":"（"+where+"）"); }
                }
                row.TotalSeconds+=watch.Elapsed.TotalSeconds;
            }catch(Exception e){
                row.Status="fail";row.Error="校验失败："+e.Message+Hr(e);
            }finally{ CloseAll(); }
        }
        // 第一个找不到文件的实例（"子装配名 / 实例名"）；都在返回 null
        static string MissingIn(A.AssemblyDocument asm,int depth,HashSet<string> seen){
            if(depth>12)return null;
            try{ if(!seen.Add(asm.FullName))return null; }catch{}
            int count=0;try{ count=asm.Occurrences.Count; }catch{ return null; }
            for(int i=1;i<=count;i++){
                A.Occurrence o=null;try{ o=(A.Occurrence)asm.Occurrences.Item(i); }catch{ continue; }
                string name="";try{ name=o.Name; }catch{}
                try{ if(o.FileMissing())return name; }catch{}
                A.AssemblyDocument sub=null;
                try{ if(o.Subassembly)sub=o.OccurrenceDocument as A.AssemblyDocument; }catch{}
                if(sub!=null){ string hit=MissingIn(sub,depth+1,seen); if(hit!=null)return name+" / "+hit; }
            }
            return null;
        }
        void CloseAll(){
            for(int guard=0;guard<4096;guard++){
                int count=0;
                try{ count=app.Documents.Count; }catch{ return; }
                if(count<=0)return;
                try{ ((F.SolidEdgeDocument)app.Documents.Item(1)).Close(false); }
                catch(Exception e){ log("关闭文档失败："+e.Message); return; }
            }
        }
        // Two workers can reach the same component (shared standard parts are common). Each output
        // path is claimed through a lock file so only one worker writes it; the others wait for the
        // file to appear and then skip it. Bounded wait, then take over, so a dead worker cannot
        // stall the batch.
        public static bool TryClaim(string lockRoot,string targetPath){
            if(lockRoot==null||lockRoot.Length==0)return true;
            string directory=Path.Combine(lockRoot,"locks");
            string lockPath;
            try{
                Directory.CreateDirectory(directory);
                lockPath=Path.Combine(directory,Hash(targetPath)+".lock");
            }catch{ return true; }
            // Wait for the owning worker to finish. A large part can take several seconds to write, so
            // poll for up to a minute; only take over when the claim is clearly stale (owner died).
            for(int attempt=0;attempt<120;attempt++){
                try{
                    using(var stream=new FileStream(lockPath,FileMode.CreateNew,FileAccess.Write,FileShare.None)){ stream.WriteByte(1); }
                    return true;
                }catch(IOException){
                    if(File.Exists(targetPath)&&new FileInfo(targetPath).Length>0)return false;
                    try{ if(DateTime.UtcNow-new FileInfo(lockPath).LastWriteTimeUtc>TimeSpan.FromSeconds(180))return true; }catch{}
                    Thread.Sleep(500);
                }catch{ return true; }
            }
            return true;
        }
        static string Hash(string value){
            using(var sha=new System.Security.Cryptography.SHA256Managed()){
                byte[] bytes=sha.ComputeHash(Encoding.UTF8.GetBytes(value.ToLowerInvariant()));
                var text=new StringBuilder(16);
                for(int i=0;i<8;i++)text.Append(bytes[i].ToString("x2"));
                return text.ToString();
            }
        }
        // Two CAD processes writing at the same moment can collide inside their own save path (the
        // CAD writes through temporary files). All workers therefore take a machine-wide named mutex
        // around SaveAs. Saves are short, so serialising them costs almost nothing.
        sealed class MutexScope : IDisposable {
            readonly Mutex mutex; readonly bool owned;
            public MutexScope(Mutex mutex,bool owned){ this.mutex=mutex; this.owned=owned; }
            public void Dispose(){ if(owned)try{ mutex.ReleaseMutex(); }catch{} try{ mutex.Dispose(); }catch{} }
        }
        static IDisposable AcquireSaveLock(){
            Mutex mutex=null;
            try{
                mutex=new Mutex(false,"Local\\TianGongFormatConvert.SaveAs");
                bool owned;
                try{ owned=mutex.WaitOne(TimeSpan.FromSeconds(180)); }
                catch(AbandonedMutexException){ owned=true; }
                if(!owned){ mutex.Dispose(); return null; }
                return new MutexScope(mutex,true);
            }catch{ if(mutex!=null)try{ mutex.Dispose(); }catch{} return null; }
        }
        public void Dispose(){
            if(app==null)return;
            try{ CloseAll(); }catch{}
            try{ app.Quit(); }catch{}
            app=null;
            DropStages();
        }
    }
}
