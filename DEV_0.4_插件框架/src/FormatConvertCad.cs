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
            this.log=log==null?delegate(string s){}:log;
            this.lockRoot=lockRoot;
            var watch=Stopwatch.StartNew();
            app=Retry(delegate(){ return (F.Application)Activator.CreateInstance(Type.GetTypeFromProgID("SolidEdge.Application")); },"启动天工CAD",600);
            BootSeconds=watch.Elapsed.TotalSeconds;
            try{ ProcessId=app.ProcessID; }catch{}
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
            try{
                top=Retry(delegate(){ return (F.SolidEdgeDocument)app.Documents.Open(item.Source); },"打开 "+Path.GetFileName(item.Source),7200);
                row.OpenSeconds=total.Elapsed.TotalSeconds;
                var root=BuildNode(top,0,new Dictionary<string,Node>(),log);
                var documents=new List<Node>();
                PostOrder(root,documents,new HashSet<Node>());
                row.Documents=documents.Count;
                var saveWatch=Stopwatch.StartNew();
                int failures=0;string firstError=null;
                var written=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach(Node node in documents){
                    F.SolidEdgeDocument doc=node.Doc;
                    string name=null;F.DocumentTypeConstants type=F.DocumentTypeConstants.igUnknownDocument;
                    try{ name=doc.Name; }catch{}
                    try{ type=doc.Type; }catch{}
                    string ext=ExtensionFor(type);
                    string assigned=SafeFullNameOrNull(doc);
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
                    if(!written.Add(target)){
                        log("同一零件的重复实例，已并入 "+finalName+"。");
                        continue;
                    }
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
                row.TotalSeconds=total.Elapsed.TotalSeconds;
            }
            if(row.Status=="ok"&&options.VerifyReopen)Verify(item,row);
            return row;
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
