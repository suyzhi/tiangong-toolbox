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
        public int ProcessId {get;private set;}

        static readonly int BusyTimeoutMs=180000;
        public CadConvertSession(Action<string> log,string lockRoot){
            TianGongCadSuite.Licensing.LicenseGate.Require();
            this.log=log==null?delegate(string s){}:log;
            this.lockRoot=lockRoot;
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
            string stage=null;Dictionary<string,string> staged=null;   // 临时文件夹里的拷贝 → 原文件
            try{
                top=Retry(delegate(){ return (F.SolidEdgeDocument)app.Documents.Open(item.Source); },"打开 "+Path.GetFileName(item.Source),7200);
                // 0.10.2：SolidWorks 装配打开后一个零件都没有 = 天工没找到它引用的零件（零件不在装配的文件夹里）。
                // 原来照样存出空装配、报成功。把同一项目里的 SolidWorks 文件拷到一个临时文件夹再开；还是空的就报失败、说清楚
                if(EmptyAssembly(top)&&ConvertPlanner.IsSolidWorks(item.Source)){
                    var files=ConvertPlanner.StageSources(item.Source,item.Root);
                    if(files.Count>1){
                        CloseAll();top=null;
                        stage=Path.Combine(Path.GetTempPath(),"TianGongConverter","stage-"+Guid.NewGuid().ToString("N").Substring(0,8));
                        Directory.CreateDirectory(stage);
                        staged=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
                        foreach(string f in files){ string copy=Path.Combine(stage,Path.GetFileName(f)); File.Copy(f,copy,true); staged[StemKey(copy)]=f; }   // 导入后文档叫「临时文件夹\名字.par」，按文件夹 + 主文件名对
                        log("「"+Path.GetFileName(item.Source)+"」打开后（或它的子装配）是空的：它用到的零件不在装配所在的文件夹，天工找不到。已把同一项目里的 "+files.Count+" 个 SolidWorks 文件拷到临时文件夹再打开。");
                        string again=Path.Combine(stage,Path.GetFileName(item.Source));
                        top=Retry(delegate(){ return (F.SolidEdgeDocument)app.Documents.Open(again); },"打开 "+Path.GetFileName(item.Source),7200);
                    }
                    // 拷到一起还有找不到的：整个装配是空的就报失败；只缺几个（比如项目外的标准件）照样转，日志里说一声
                    if(EmptyAssembly(top)&&!TopEmpty(top))log("「"+Path.GetFileName(item.Source)+"」里还有零件找不到（不在这个项目的文件夹里），转出来的装配会缺这些件。");
                    else if(EmptyAssembly(top))
                        throw new InvalidOperationException("装配打开后是空的：天工找不到这个 SolidWorks 装配引用的零件（装配里记的是零件原来的位置）。天工找不到这个 SolidWorks 装配引用的零件（装配里记的是零件原来的位置）。把装配和它用到的零件放进同一个文件夹，或者输入里加上零件所在的上级文件夹，再转。");
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
                    if(staged!=null&&assigned!=null&&staged.TryGetValue(StemKey(assigned),out original))assigned=Path.Combine(Path.GetDirectoryName(original),Path.GetFileName(assigned));   // 临时拷贝 → 按原文件的位置放输出（镜像目录照常）
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
                        if(!TryClaim(lockRoot,target)){
                            log("另一个进程已生成 "+finalName+"，本次跳过写入。");
                            continue;
                        }
                        string local=target;
                        using(AcquireSaveLock()){ Retry(delegate(){ doc.SaveAs(local); },"保存 "+finalName,1800); }
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
                log("转换失败 "+Path.GetFileName(item.Source)+"："+e.Message+Hr(e));
            }finally{
                CloseAll();
                if(stage!=null){ try{ Directory.Delete(stage,true); }catch(Exception e){ log("临时文件夹没删掉（"+e.Message+"）："+stage); } }
                row.TotalSeconds=total.Elapsed.TotalSeconds;
            }
            if(row.Status=="ok"&&options.VerifyReopen)Verify(item,row);
            return row;
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
                    int count=0;try{ count=assembly.Occurrences.Count; }catch{}
                    for(int i=1;i<=count&&!missing;i++){
                        try{ if(((A.Occurrence)assembly.Occurrences.Item(i)).FileMissing())missing=true; }catch{}
                    }
                    if(missing){ row.Status="fail";row.Error="校验时发现丢失的引用"; }
                }
                row.TotalSeconds+=watch.Elapsed.TotalSeconds;
            }catch(Exception e){
                row.Status="fail";row.Error="校验失败："+e.Message+Hr(e);
            }finally{ CloseAll(); }
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
        }
    }
}
