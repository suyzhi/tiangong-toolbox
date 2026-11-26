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
        Action<string> log;
        string lockRoot;
        public double BootSeconds {get;private set;}
        // 这次会话里已经存出来的文件（含装配顺带存的零件）：勾了「覆盖」时，轮到这些零件本身别再转一遍（0.10.3）
        readonly HashSet<string> written=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // 平铺输出防跨装配同名错替：记录本会话中每个目标路径对应的真实源路径
        readonly Dictionary<string,string> writtenTargetSources=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        // 这次批次已随装配转换过的源 SolidWorks 文件路径（解决天工因 MRU 自动追加 _0/_5 后缀导致零件重转的问题）
        readonly HashSet<string> writtenSources=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public bool WroteThisRun(IEnumerable<string> paths){ return WroteThisRun(paths,null,false); }
        public bool WroteThisRun(IEnumerable<string> paths,string sourcePath){ return WroteThisRun(paths,sourcePath,false); }
        public bool WroteThisRun(IEnumerable<string> paths,string sourcePath,bool flatOutput){
            string fullSrc=null;
            if(!string.IsNullOrEmpty(sourcePath)){
                try{ fullSrc=Path.GetFullPath(sourcePath); }catch{ fullSrc=sourcePath; }
                if(writtenSources.Contains(fullSrc))return true;
            }
            if(flatOutput){
                // 平铺输出模式下多个不同目录可能映射为同一个文件名：只认源路径命中或目标路径明确属于同一源文件。
                if(paths!=null&&fullSrc!=null){
                    foreach(string p in paths){
                        try{
                            string fp=Path.GetFullPath(p);
                            string owner;
                            if(writtenTargetSources.TryGetValue(fp,out owner)&&string.Equals(owner,fullSrc,StringComparison.OrdinalIgnoreCase))return true;
                        }catch{}
                    }
                }
                return ConvertWrittenIndex.ContainsForFlatSource(lockRoot,paths,sourcePath);
            }
            if(paths!=null){
                foreach(string p in paths){
                    try{ if(written.Contains(Path.GetFullPath(p)))return true; }catch{}
                }
            }
            return ConvertWrittenIndex.ContainsAnyOrSource(lockRoot,paths,sourcePath);
        }
        public long PrivateMemoryMb(){
            if(ProcessId<=0)return 0L;
            try{
                using(var p=Process.GetProcessById(ProcessId)){
                    if(p.HasExited)return 0L;
                    return p.PrivateMemorySize64/(1024L*1024L);
                }
            }catch{ return 0L; }
        }
        // 热驻留工作池切入新批次时复位批次状态并重定向日志与锁目录，无需重启隐藏 CAD。
        public void BeginBatch(string newLockRoot,Action<string> newLog){
            lockRoot=newLockRoot;
            log=newLog==null?delegate(string s){}:newLog;
            written.Clear();
            writtenTargetSources.Clear();
            writtenSources.Clear();
        }
        // 批次结束后立刻清理临时暂存目录，避免热驻留空闲期占着磁盘空间。
        public void EndBatch(){
            DropStages();
            written.Clear();
            writtenTargetSources.Clear();
            writtenSources.Clear();
        }
        public int ProcessId {get;private set;}
        public long ProcessStartUtcTicks {get;private set;}
        public bool SkippedBecauseEmpty {get;private set;}

        static readonly int BusyTimeoutMs=180000;
        public CadConvertSession(Action<string> log,string lockRoot)
            : this(log,lockRoot,0,null,null,null){}

        public CadConvertSession(Action<string> log,string lockRoot,int rotTarget,HashSet<int> forbiddenPids,string queueDir)
            : this(log,lockRoot,rotTarget,forbiddenPids,queueDir,null){}

        public CadConvertSession(Action<string> log,string lockRoot,int rotTarget,HashSet<int> forbiddenPids,string queueDir,ICollection<int> prelaunchedPids){
            TianGongCadSuite.Licensing.LicenseGate.Require();
            this.log=log==null?delegate(string s){}:log;
            this.lockRoot=lockRoot;
            DropStaleStages();
            var watch=Stopwatch.StartNew();
            // 转换器要独占一个 CAD：下面会隐藏窗口、关掉提示、把撤销步数设成 0，每个文件转完还会**不保存**关掉全部文档。
            // 万一 CreateInstance 连上的是用户正在用的那个天工，这些全落在用户头上。
            // 关键安全边界：当 rotTarget<=0（非绑定本批预拉起实例，如传统 DCOM 启动、崩溃自愈或 4GB 回收重建）时，
            // 绝不能只依赖启动时传入的过期 forbiddenPids，必须无条件实时快照当前所有 TianGong/Edge PID 并入禁用集。
            var running=new HashSet<int>();
            if(forbiddenPids!=null){
                foreach(int pid in forbiddenPids)running.Add(pid);
            }
            if(rotTarget<=0||forbiddenPids==null){
                try{ foreach(var name in new[]{"TianGong","Edge"})foreach(var p in Process.GetProcessesByName(name)){ running.Add(p.Id); p.Dispose(); } }catch{}
            }
            // 并行预拉起门控：只读等待 ROT 中出现足够多已注册的隐藏天工，绝不提前调 CoCreateInstance 导致 DCOM 额外拉起多余进程。
            // 动态追踪进入等待时存活的外部 CAD 与本次预拉起进程：任一退出即下调目标阈值，避免用户关 CAD 或预拉起失败触发 75 秒死等。
            if(rotTarget>0){
                var aliveForbiddenAtEntry=new List<int>();
                foreach(int pid in running)if(IsPidAlive(pid))aliveForbiddenAtEntry.Add(pid);
                var trackedPrelaunched=new List<int>();
                if(prelaunchedPids!=null)foreach(int pid in prelaunchedPids)if(pid>0)trackedPrelaunched.Add(pid);
                var waitEnd=DateTime.UtcNow.AddSeconds(75);
                while(DateTime.UtcNow<waitEnd){
                    if(!string.IsNullOrEmpty(queueDir)&&!ConvertQueue.HasPending(queueDir)){
                        SkippedBecauseEmpty=true;
                        BootSeconds=watch.Elapsed.TotalSeconds;
                        return;
                    }
                    int exitedForbidden=0;
                    for(int i=0;i<aliveForbiddenAtEntry.Count;i++)if(!IsPidAlive(aliveForbiddenAtEntry[i]))exitedForbidden++;
                    int deadPrelaunched=0;
                    for(int i=0;i<trackedPrelaunched.Count;i++)if(!IsPidAlive(trackedPrelaunched[i]))deadPrelaunched++;
                    if(trackedPrelaunched.Count>0&&deadPrelaunched>=trackedPrelaunched.Count)break;
                    int effectiveTarget=rotTarget-deadPrelaunched-exitedForbidden;
                    if(effectiveTarget<=0||CadPrelauncher.CountRotSolidEdge()>=effectiveTarget)break;
                    Thread.Sleep(150);
                }
                if(!string.IsNullOrEmpty(queueDir)&&!ConvertQueue.HasPending(queueDir)){
                    SkippedBecauseEmpty=true;
                    BootSeconds=watch.Elapsed.TotalSeconds;
                    return;
                }
            }
            // 所有 COM CreateInstance 统一经由 ComBind 互斥锁串行，防止多 worker 争抢同一个 REGCLS_SINGLEUSE 槽位，
            // 或 rotTarget==0 重建会话时抢占另一批次正在预拉起的槽位。
            IDisposable bindScope=AcquireNamedMutex("Local\\TianGongFormatConvert.ComBind",90);
            try{
                if(rotTarget<=0){
                    // 拿锁后再刷新一次实时进程，兜住等待锁期间新出现的用户 CAD 或同伴预拉起 CAD
                    try{ foreach(var name in new[]{"TianGong","Edge"})foreach(var p in Process.GetProcessesByName(name)){ running.Add(p.Id); p.Dispose(); } }catch{}
                }
                app=Retry(delegate(){ return (F.Application)Activator.CreateInstance(Type.GetTypeFromProgID("SolidEdge.Application")); },"启动天工CAD",600);
            }finally{
                if(bindScope!=null)bindScope.Dispose();
            }
            BootSeconds=watch.Elapsed.TotalSeconds;
            try{ ProcessId=app.ProcessID; }catch{}
            if(ProcessId!=0&&running.Contains(ProcessId)){
                try{ Marshal.FinalReleaseComObject(app); }catch{}
                app=null;
                throw new InvalidOperationException("没能另起一个天工 CAD（连上的是已经打开的那个）。为免关掉你打开的文件，转换已停止：请先关掉天工 CAD 再转换，或直接用 TianGongConverter.exe。");
            }
            ProcessStartUtcTicks=WorkerChildren.CaptureStartUtcTicks(ProcessId);
            ConfigureSession();
        }
        static bool IsPidAlive(int pid){
            if(pid<=0)return false;
            try{ using(var p=Process.GetProcessById(pid)){ return !p.HasExited; } }catch{ return false; }
        }
        void ConfigureSession(){
            Quiet(delegate(){ app.DisplayAlerts=false; },"DisplayAlerts");
            Quiet(delegate(){ app.ScreenUpdating=false; },"ScreenUpdating");
            Quiet(delegate(){ app.ShowStartupScreen=false; },"ShowStartupScreen");
            // 仅设置当前进程内存级无头性能属性，不修改用户的全局持久化偏好或停靠面板布局
            // （实测关闭 EdgeBarVisible/StatusBarVisible 会触发 jengine!SaveClosedByUserLayoutToTheme 并发写共享主题 XML 导致崩溃）。
            Quiet(delegate(){ app.Interactive=false; },"Interactive");
            Quiet(delegate(){ app.DelayCompute=true; },"DelayCompute");
            Quiet(delegate(){ app.StatusBarDelayUpdate=true; },"StatusBarDelayUpdate");
            Quiet(delegate(){ app.DisplayRecentFiles=false; },"DisplayRecentFiles");
            Quiet(delegate(){ app.SetOLEServerBusyTimeout(BusyTimeoutMs); },"SetOLEServerBusyTimeout");
            Quiet(delegate(){ app.SetOLERequestPendingTimeout(BusyTimeoutMs); },"SetOLERequestPendingTimeout");
            // Undo history is pure overhead for a converter; dropping it removes per-document bookkeeping.
            Quiet(delegate(){ app.SetGlobalParameter(F.ApplicationGlobalConstants.seApplicationGlobalPartandAsmUndoSteps,0); },"UndoSteps");
            try{ app.Visible=false; }catch(Exception e){ log("隐藏窗口失败："+e.Message); }
            try{ app.DoIdle(); }catch{}
        }
        public bool IsAlive(){
            if(app==null)return false;
            try{
                if(ProcessId>0){
                    using(var p=Process.GetProcessById(ProcessId)){ if(p.HasExited)return false; }
                }
                int _=app.Documents.Count;
                return true;
            }catch{ return false; }
        }
        public static bool IsDeadCom(Exception e){
            var c=e as COMException;
            if(c==null)return false;
            int hr=c.ErrorCode;
            return hr==unchecked((int)0x80010108) // RPC_E_DISCONNECTED
                ||hr==unchecked((int)0x800706BA)  // RPC_S_SERVER_UNAVAILABLE
                ||hr==unchecked((int)0x800706BE)  // RPC_S_CALL_FAILED
                ||hr==unchecked((int)0x800706BF); // RPC_S_CALL_FAILED_DNE
        }
        public static string FriendlyError(Exception e){
            if(e==null)return "";
            var c=e as COMException;
            if(c!=null){
                int hr=c.ErrorCode;
                if(hr==unchecked((int)0x80030021))return "目标文件被暂时锁定占用（STG_E_LOCKVIOLATION），请稍后重试或检查是否有程序打开该文件"+Hr(e);
                if(hr==unchecked((int)0x80010105))return "天工 CAD 转换服务出现瞬时异常（RPC_E_SERVERFAULT）"+Hr(e);
                if(IsDeadCom(e))return "隐藏天工 CAD 进程异常退出或连接中断"+Hr(e);
            }
            return e.Message+Hr(e);
        }
        void Quiet(Action action,string what){ try{ action(); }catch(Exception e){ log(what+" 不可用："+e.Message); } }
        static string Hr(Exception e){ var c=e as COMException; return c==null?"":" (0x"+c.ErrorCode.ToString("X8")+")"; }
        T Retry<T>(Func<T> action,string what,int seconds){
            var end=DateTime.Now.AddSeconds(seconds);Exception last=null;
            int serverFaults=0;
            while(DateTime.Now<end){
                try{ return action(); }
                catch(COMException e){
                    last=e;int hr=e.ErrorCode;
                    if(hr==unchecked((int)0x80010001)||hr==unchecked((int)0x8001010A)||hr==unchecked((int)0x80010100)){
                        try{ if(app!=null)app.DoIdle(); }catch{}
                        Thread.Sleep(200);continue;
                    }
                    // 多进程冷启动瞬间并发打开首个文件偶发 RPC_E_SERVERFAULT (0x80010105)，退避重试 2 次即可恢复
                    if(hr==unchecked((int)0x80010105)&&serverFaults<2){
                        serverFaults++;
                        try{ if(app!=null)app.DoIdle(); }catch{}
                        Thread.Sleep(500*serverFaults);continue;
                    }
                    throw;
                }
            }
            throw new TimeoutException(what+" 超时"+(last==null?"":"："+last.Message));
        }
        void Retry(Action action,string what,int seconds){ Retry<object>(delegate(){ action(); return null; },what,seconds); }
        // 保存阶段瞬时锁定与异常重试：
        // 并发转换或目标文件被杀毒/加密驱动/天工自身短暂占用时，除 STG_E_LOCKVIOLATION (0x80030021) / SHARING_VIOLATION (0x80070020)
        // 外，天工还常抛出 DISP_E_EXCEPTION (0x80020009)；清只读属性 + DoIdle 退避重试可稳定恢复，避免大装配前功尽弃。
        void RetrySave(Action action,string what,int seconds,string targetPath){
            var end=DateTime.Now.AddSeconds(seconds);
            var lockEnd=DateTime.Now.AddSeconds(Math.Min(seconds,30));
            Exception last=null;
            int serverFaults=0;
            int dispFaults=0;
            while(DateTime.Now<end){
                try{ action(); return; }
                catch(COMException e){
                    last=e;int hr=e.ErrorCode;
                    if(hr==unchecked((int)0x80010001)||hr==unchecked((int)0x8001010A)||hr==unchecked((int)0x80010100)){
                        try{ if(app!=null)app.DoIdle(); }catch{}
                        Thread.Sleep(200);continue;
                    }
                    if((hr==unchecked((int)0x80030021)||hr==unchecked((int)0x80070020))&&DateTime.Now<lockEnd){
                        TryClearReadOnly(targetPath);
                        Thread.Sleep(300);continue;
                    }
                    if(hr==unchecked((int)0x80020009)&&dispFaults<3){
                        dispFaults++;
                        TryClearReadOnly(targetPath);
                        try{ if(app!=null)app.DoIdle(); }catch{}
                        Thread.Sleep(400*dispFaults);continue;
                    }
                    if(hr==unchecked((int)0x80010105)&&serverFaults<2){
                        serverFaults++;
                        try{ if(app!=null)app.DoIdle(); }catch{}
                        Thread.Sleep(500*serverFaults);continue;
                    }
                    throw;
                }catch(IOException e){
                    last=e;
                    if(DateTime.Now<lockEnd){ TryClearReadOnly(targetPath); Thread.Sleep(300); continue; }
                    throw;
                }
            }
            throw new TimeoutException(what+" 超时"+(last==null?"":"："+last.Message));
        }
        static void TryClearReadOnly(string path){
            if(string.IsNullOrEmpty(path))return;
            try{
                if(File.Exists(path)){
                    var attr=File.GetAttributes(path);
                    if((attr&FileAttributes.ReadOnly)!=0)File.SetAttributes(path,attr&~FileAttributes.ReadOnly);
                }
            }catch{}
        }
        static bool SamePath(string a,string b){
            if(string.IsNullOrEmpty(a)||string.IsNullOrEmpty(b))return false;
            try{ return string.Equals(Path.GetFullPath(a),Path.GetFullPath(b),StringComparison.OrdinalIgnoreCase); }catch{ return false; }
        }

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

        sealed class Node {
            public F.SolidEdgeDocument Doc;
            public string SortKey="";
            public List<Node> Kids=new List<Node>();
            public List<Node> Parents=new List<Node>();
        }
        static bool IsTransientOrDeadRpc(Exception e){
            if(e is TimeoutException)return true;
            var c=e as COMException;
            if(c==null)return false;
            int hr=c.ErrorCode;
            return IsDeadCom(c)
                ||hr==unchecked((int)0x80010001)
                ||hr==unchecked((int)0x8001010A)
                ||hr==unchecked((int)0x80010100)
                ||hr==unchecked((int)0x80010105);
        }
        // 单遍扫描装配树（支持深达 32 层嵌套）：
        // 1. 优先通过 OccurrenceFileName 命中 seenByPath 缓存，跳过大装配中数百个重复紧固件的 OccurrenceDocument/FullName/Type 跨进程 COM 往返；
        // 2. 用 kidSet 对子节点去重，避免同一零件 200 个实例往 node.Kids 塞 200 次；
        // 3. Occurrences / OccurrenceDocument 走瞬时 COM 忙重试并显式统计 treeErrors，防止 COM 异常被吞掉后把空壳装配保存并报 ok；
        // 4. 当 OccurrenceDocument 为空时用 FileMissing() 区分「真缺件」与「被抑制实例」，防止抑制件误触发整项目拷贝重开。
        Node BuildNode(F.SolidEdgeDocument doc,int depth,Dictionary<string,Node> seen,Dictionary<string,Node> seenByPath,
            ref int missingOccs,ref int emptySubAsms,ref int topOccCount,ref int treeErrors){
            string full="";
            string key="";
            try{ full=doc.FullName??""; key=full+"|"+doc.Type; }catch{}
            if(key.Length>0){ Node existing; if(seen.TryGetValue(key,out existing))return existing; }
            var node=new Node();node.Doc=doc;node.SortKey=key;
            if(key.Length>0)seen[key]=node;
            if(full.Length>0&&!seenByPath.ContainsKey(full))seenByPath[full]=node;
            var assembly=doc as A.AssemblyDocument;
            if(assembly!=null&&depth<32){
                A.Occurrences occurrences=null;
                try{ occurrences=Retry(delegate(){ return assembly.Occurrences; },"读取装配结构",60); }
                catch(Exception e){ treeErrors++; log("读取装配结构失败："+e.Message); }
                if(occurrences!=null){
                    int count=-1;
                    try{ count=Retry(delegate(){ return occurrences.Count; },"读取装配实例数",60); }
                    catch(Exception e){ treeErrors++; log("装配实例数读取失败："+e.Message); }
                    if(depth==0)topOccCount=count;
                    if(count==0&&depth>0)emptySubAsms++;
                    if(count>0){
                        var kidSet=new HashSet<Node>();
                        for(int i=1;i<=count;i++){
                            int idx=i;
                            A.Occurrence occ=null;
                            try{ occ=Retry(delegate(){ return (A.Occurrence)occurrences.Item(idx); },"读取装配实例",30); }
                            catch(Exception e){ treeErrors++; log("读取装配实例 #"+idx+" 失败："+e.Message); continue; }
                            string ofn=null;
                            try{ ofn=occ.OccurrenceFileName; }catch{}
                            if(!string.IsNullOrEmpty(ofn)){
                                Node cached;
                                if(seenByPath.TryGetValue(ofn,out cached)){
                                    if(kidSet.Add(cached)){ node.Kids.Add(cached); cached.Parents.Add(node); }
                                    continue;
                                }
                            }
                            object child=null;
                            Exception docEx=null;
                            try{ child=Retry(delegate(){ return occ.OccurrenceDocument; },"读取组件文档",30); }
                            catch(Exception e){ docEx=e; }
                            var childDoc=child as F.SolidEdgeDocument;
                            if(childDoc==null){
                                bool isMissing=true;
                                try{ isMissing=occ.FileMissing(); }catch{}
                                if(isMissing)missingOccs++;
                                else if(docEx!=null&&IsTransientOrDeadRpc(docEx)){
                                    treeErrors++;
                                    log("读取组件文档 COM 异常："+docEx.Message);
                                }
                                continue;
                            }
                            Node childNode=BuildNode(childDoc,depth+1,seen,seenByPath,ref missingOccs,ref emptySubAsms,ref topOccCount,ref treeErrors);
                            if(!string.IsNullOrEmpty(ofn)&&!seenByPath.ContainsKey(ofn))seenByPath[ofn]=childNode;
                            if(kidSet.Add(childNode)){ node.Kids.Add(childNode); childNode.Parents.Add(node); }
                        }
                        if(node.Kids.Count>1){
                            node.Kids.Sort(delegate(Node a,Node b){ return string.Compare(a.SortKey,b.SortKey,StringComparison.OrdinalIgnoreCase); });
                        }
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

        // 记录打开源装配前源文件夹里本来就有的 .par/.asm/.psm/.cfg/.log，
        // 转换并关闭文档后仅清理本次 Open() 新生成在源目录里的临时副本（绝不删源目录原有文件，也不删输出目录里的文件）。
        static HashSet<string> SnapshotSourceNativeFiles(string srcDir){
            return ConvertSourceBaseline.SnapshotNativeFiles(srcDir);
        }
        static void CleanupCreatedSourceStubs(IEnumerable<string> candidates,HashSet<string> preExisting,HashSet<string> keptOutputs,string outputRoot,bool requireExclusive,DateTime? minCreatedUtc){
            if(candidates==null)return;
            foreach(string raw in candidates){
                if(string.IsNullOrEmpty(raw))continue;
                try{
                    string full=Path.GetFullPath(raw);
                    if(preExisting!=null&&preExisting.Contains(full))continue;
                    if(keptOutputs!=null&&keptOutputs.Contains(full))continue;
                    if(!string.IsNullOrEmpty(outputRoot)&&ConvertSourceBaseline.IsUnderDir(full,outputRoot))continue;
                    if(!File.Exists(full))continue;
                    if(minCreatedUtc.HasValue){
                        var fi=new FileInfo(full);
                        if(fi.CreationTimeUtc<minCreatedUtc.Value&&fi.LastWriteTimeUtc<minCreatedUtc.Value)continue;
                    }
                    if(requireExclusive&&!CanOpenExclusive(full))continue;
                    File.Delete(full);
                }catch{}
            }
        }
        static bool CanOpenExclusive(string path){
            try{
                using(var fs=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None)){ return true; }
            }catch{ return false; }
        }
        static bool SameFileName(string a,string b){
            if(string.IsNullOrEmpty(a)||string.IsNullOrEmpty(b))return false;
            try{ return string.Equals(Path.GetFileName(a),Path.GetFileName(b),StringComparison.OrdinalIgnoreCase); }catch{ return false; }
        }
        // 平铺输出跨装配防同名不同源错替：在跨进程互斥锁下按「目标路径 -> 源文件」原子占位，
        // 即使两个并发进程同时撞上不同目录的同名零件，也能确定分配 「名字 (2)」「名字 (3)」…，杜绝覆盖或错替。
        string ResolveFlatTargetCrossRun(string candidate,string sourceKey,out string crossClash){
            crossClash=null;
            if(string.IsNullOrEmpty(candidate)||string.IsNullOrEmpty(sourceKey))return candidate;
            string normSrc=sourceKey;
            try{ if(Path.IsPathRooted(sourceKey))normSrc=Path.GetFullPath(sourceKey); }catch{}
            string dir=Path.GetDirectoryName(candidate),stem=Path.GetFileNameWithoutExtension(candidate),tail=Path.GetExtension(candidate);
            string claimDir=!string.IsNullOrEmpty(lockRoot)?Path.Combine(lockRoot,"flat-claims"):null;
            using(AcquireNamedMutex("Local\\TianGongFormatConvert.FlatName",15)){
                if(claimDir!=null)try{ Directory.CreateDirectory(claimDir); }catch{}
                string cur=candidate;
                for(int n=2;;n++){
                    string fullCur;
                    try{ fullCur=Path.GetFullPath(cur); }catch{ return cur; }
                    string owner=null;
                    if(!writtenTargetSources.TryGetValue(fullCur,out owner)){
                        owner=ConvertWrittenIndex.GetTargetSource(lockRoot,fullCur);
                    }
                    string claimFile=claimDir!=null?Path.Combine(claimDir,Hash(fullCur)+".src"):null;
                    if(owner==null&&claimFile!=null&&File.Exists(claimFile)){
                        try{
                            using(var fs=new FileStream(claimFile,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
                            using(var sr=new StreamReader(fs,new UTF8Encoding(false))){
                                string txt=sr.ReadToEnd().Trim();
                                if(txt.Length>0)owner=txt;
                            }
                        }catch{}
                    }
                    if(owner==null){
                        if(claimFile!=null){
                            try{ File.WriteAllText(claimFile,normSrc,new UTF8Encoding(false)); }catch{}
                        }
                        return cur;
                    }
                    if(string.Equals(owner,normSrc,StringComparison.OrdinalIgnoreCase)){
                        return cur;
                    }
                    if(crossClash==null)crossClash=owner;
                    cur=Path.Combine(dir,stem+" ("+n+")"+tail);
                }
            }
        }
        static string FormatMissingHint(List<string> missingNames){
            if(missingNames==null||missingNames.Count==0)return "";
            int show=Math.Min(3,missingNames.Count);
            var sb=new StringBuilder();
            for(int i=0;i<show;i++){ if(i>0)sb.Append("、"); sb.Append(missingNames[i]); }
            if(missingNames.Count>show)sb.Append(" 等共 ").Append(missingNames.Count).Append(" 个文件");
            return sb.ToString();
        }

        public ConvertRow Convert(ConvertItem item,ConvertOptions options){
            return Convert(item,options,false);
        }
        public ConvertRow Convert(ConvertItem item,ConvertOptions options,bool isRetry){
            var row=new ConvertRow();
            row.Index=item.Index;row.Source=item.Source;row.Target=item.Target;row.Worker=item.Worker;
            var total=Stopwatch.StartNew();
            DateTime openStartUtc=DateTime.UtcNow;
            F.SolidEdgeDocument top=null;
            Dictionary<string,string> staged=null;   // 临时文件夹里的拷贝 → 原文件
            // 提前占住顶层 SolidWorks 目标路径锁：防止其他并发装配把本文件当子装配顺带归一回同一个文件名，
            // 在本进程 SaveAs 或 Verify 重开校验期间发生写冲突（DISP_E_EXCEPTION）。
            string preReservedRootLock=null;
            bool rootSaved=false;
            string deferredRootMarkTarget=null;
            string deferredRootMarkSource=null;
            bool isSw=ConvertPlanner.IsSolidWorks(item.Source);
            string srcDir=null;
            try{ srcDir=Path.GetDirectoryName(Path.GetFullPath(item.Source)); }catch{}
            string itemOutRoot=ConvertPlanner.OutputRootFor(item,options);
            bool sameFolderOutput=options!=null&&options.OutputMode==ConvertOutputMode.InPlace;
            if(!sameFolderOutput){
                try{
                    string expectedTopDir=Path.GetDirectoryName(ConvertPlanner.TopTarget(item,options));
                    sameFolderOutput=ConvertPlanner.SameDir(srcDir,expectedTopDir);
                }catch{}
            }
            bool hasBatchBaseline=false;
            HashSet<string> preExistingSrcFiles=!sameFolderOutput?ConvertSourceBaseline.LoadOrCapture(lockRoot,srcDir,out hasBatchBaseline):null;
            var sourceStubsToClean=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var touchedSrcDirs=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if(!sameFolderOutput&&!string.IsNullOrEmpty(srcDir))touchedSrcDirs.Add(srcDir);
            var writtenThisItem=new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if(isSw&&!string.IsNullOrEmpty(item.Target)&&!isRetry){
                TryClaimNonBlocking(lockRoot,item.Target,out preReservedRootLock);
            }
            try{
                if(isSw&&item.Size>=10L*1024L*1024L){
                    log("正在打开大型装配 "+Path.GetFileName(item.Source)+"（"+(item.Size/1048576.0).ToString("F1")+" MB），天工几何转换耗时较长，请稍候…");
                }
                PaceOpen();
                openStartUtc=DateTime.UtcNow;
                top=Retry(delegate(){ return (F.SolidEdgeDocument)app.Documents.Open(item.Source); },"打开 "+Path.GetFileName(item.Source),7200);
                int missingOccs=0,emptySubAsms=0,topOccCount=-1,treeErrors=0;
                var seenDocs=new Dictionary<string,Node>(StringComparer.OrdinalIgnoreCase);
                var seenPaths=new Dictionary<string,Node>(StringComparer.OrdinalIgnoreCase);
                var root=BuildNode(top,0,seenDocs,seenPaths,ref missingOccs,ref emptySubAsms,ref topOccCount,ref treeErrors);
                double firstOpenSec=total.Elapsed.TotalSeconds;

                // 读取天工转换日志里的缺失文件清单（在删除源目录临时日志前先读出）；STEP 也会在源目录留下同名 .log，一并纳入非同目录清理
                List<string> logMissing=isSw?ConvertPlanner.ParseMissingFromTranslatorLog(item.Source):null;
                if(!sameFolderOutput&&!string.IsNullOrEmpty(srcDir)){
                    string logFile=Path.Combine(srcDir,Path.GetFileNameWithoutExtension(item.Source)+".log");
                    if(File.Exists(logFile))sourceStubsToClean.Add(logFile);
                    if(isSw){
                        // 非空 TGImportFile.log 同样由本次导入产生：由批次基线 preExistingSrcFiles 保护原有文件，不再因长度>0而残留。
                        string tgImp=Path.Combine(srcDir,"TGImportFile.log");
                        if(File.Exists(tgImp))sourceStubsToClean.Add(tgImp);
                    }
                }

                // 0.10.2 / 0.13.9：SolidWorks 装配打开后为空（含 0 实例或全部实例缺失）或有缺失实例时的智能暂存：
                // 仅在整装为空或小型装配（打开 <45 秒）有缺失实例、且 StageCanSupplyMissing 实查确认其他子目录确有缺失件时才暂存重开。
                bool isAsmDoc=top is A.AssemblyDocument;
                bool isAllMissing=isAsmDoc&&topOccCount>0&&root.Kids.Count==0&&missingOccs>0;
                bool isCompletelyEmpty=isAsmDoc&&(topOccCount==0||isAllMissing);
                bool hasMissing=(missingOccs>0||emptySubAsms>0||(logMissing!=null&&logMissing.Count>0));
                if(isSw&&(isCompletelyEmpty||(hasMissing&&firstOpenSec<45.0))&&ConvertPlanner.StageCanSupplyMissing(item.Source,item.Root,logMissing)){
                    var st=StageFor(item);
                    if(st!=null){
                        if(!sameFolderOutput){
                            foreach(string p in seenPaths.Keys){
                                try{
                                    string pdir=Path.GetDirectoryName(p);
                                    if(!string.IsNullOrEmpty(pdir)&&!ConvertSourceBaseline.IsUnderDir(p,itemOutRoot)){
                                        sourceStubsToClean.Add(p);
                                        touchedSrcDirs.Add(pdir);
                                    }
                                }catch{}
                            }
                        }
                        CloseAll(top);top=null;
                        staged=st.Map;
                        string again=Path.Combine(st.Dir,Path.GetFileName(item.Source));
                        PaceOpen();
                        top=Retry(delegate(){ return (F.SolidEdgeDocument)app.Documents.Open(again); },"打开 "+Path.GetFileName(item.Source),7200);
                        missingOccs=0;emptySubAsms=0;topOccCount=-1;treeErrors=0;
                        seenDocs.Clear();seenPaths.Clear();
                        root=BuildNode(top,0,seenDocs,seenPaths,ref missingOccs,ref emptySubAsms,ref topOccCount,ref treeErrors);
                        isAsmDoc=top is A.AssemblyDocument;
                        isAllMissing=isAsmDoc&&topOccCount>0&&root.Kids.Count==0&&missingOccs>0;
                        isCompletelyEmpty=isAsmDoc&&(topOccCount==0||isAllMissing);
                        hasMissing=(missingOccs>0||emptySubAsms>0);
                    }
                }
                // COM 读装配结构异常导致顶层实例数不可读或全漏子件：绝不静默保存空壳，抛可重试异常。
                if(isAsmDoc&&treeErrors>0&&(topOccCount<0||root.Kids.Count==0)){
                    throw new InvalidOperationException("读取装配结构时发生 COM 异常（"+treeErrors+" 处），已中止保存空壳装配。");
                }
                if(isSw&&isCompletelyEmpty){
                    row.NoRetry=true;
                    string hint=FormatMissingHint(logMissing);
                    string msg=string.IsNullOrEmpty(hint)
                        ?"装配打开后是空的（或全部引用零件缺失）：天工找不到这个 SolidWorks 装配引用的零件（源压缩包或源文件夹本身缺件，或零件放在未选中的其他文件夹里）。"
                        :"装配打开后是空的（或全部引用零件缺失）：源目录缺少该装配引用的文件（"+hint+"），请确认源压缩包是否完整。";
                    throw new InvalidOperationException(msg);
                }
                if(!isSw&&isAsmDoc&&topOccCount==0){
                    row.NoRetry=true;
                    throw new InvalidOperationException("STEP 文件导入为空装配（0 个组件），已中止保存空壳装配。");
                }
                if(isSw&&hasMissing){
                    string hint=FormatMissingHint(logMissing);
                    log("提示：「"+Path.GetFileName(item.Source)+"」缺少部分参考文件"+(string.IsNullOrEmpty(hint)?"":"（"+hint+"）")+"，已按实际加载的 "+seenDocs.Count+" 个文档继续保存。");
                }

                row.OpenSeconds=total.Elapsed.TotalSeconds;
                var documents=new List<Node>();
                PostOrder(root,documents,new HashSet<Node>());
                row.Documents=documents.Count;
                if(row.OpenSeconds>=10.0||documents.Count>=30){
                    log("打开完成 "+Path.GetFileName(item.Source)+"（用时 "+row.OpenSeconds.ToString("F1")+" 秒，共 "+documents.Count+" 个文档），开始保存…");
                }
                // 预计算每个节点的基准输出目录，供共享子件判断是否与全部父装配同目录（非同目录时必须执行 SaveAs 更新引用路径）。
                var nodeOutDirs=new Dictionary<Node,string>();
                foreach(Node nd in documents){
                    try{
                        F.DocumentTypeConstants nt=F.DocumentTypeConstants.igUnknownDocument;
                        try{ nt=nd.Doc.Type; }catch{}
                        string next=ExtensionFor(nt);
                        if(nd==root){
                            nodeOutDirs[nd]=Path.GetDirectoryName(ConvertPlanner.TopTargetFor(item,options,next));
                        } else {
                            string ra=SafeFullNameOrNull(nd.Doc);
                            string aa=staged!=null&&ra!=null?Unstage(ra,staged,item):ra;
                            string nm=aa!=null?Path.GetFileName(aa):null;
                            if(string.IsNullOrEmpty(nm)){ try{ nm=nd.Doc.Name; }catch{} }
                            if(string.IsNullOrEmpty(nm))nm=Path.GetFileNameWithoutExtension(item.Source)+next;
                            if(!HasNativeExtension(nm))nm=nm+next;
                            nodeOutDirs[nd]=Path.GetDirectoryName(ConvertPlanner.ComponentTarget(aa,nm,item,options));
                        }
                    }catch{}
                }

                var saveWatch=Stopwatch.StartNew();
                int failures=treeErrors;
                string firstError=treeErrors>0?("遍历装配树时有 "+treeErrors+" 处 COM 读取异常"):null;
                bool anyDeadCom=false;
                var writtenFrom=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);   // 输出路径 → 源文件
                int docIdx=0;
                foreach(Node node in documents){
                    docIdx++;
                    if(documents.Count>=60&&(docIdx%50==0)){
                        log("保存进度 "+Path.GetFileName(item.Source)+"："+docIdx+" / "+documents.Count+"（已用 "+saveWatch.Elapsed.TotalSeconds.ToString("F0")+" 秒）…");
                    }
                    F.SolidEdgeDocument doc=node.Doc;
                    string name=null;F.DocumentTypeConstants type=F.DocumentTypeConstants.igUnknownDocument;
                    try{ name=doc.Name; }catch{}
                    try{ type=doc.Type; }catch{}
                    string ext=ExtensionFor(type);
                    string rawAssigned=SafeFullNameOrNull(doc);
                    if(isSw&&!sameFolderOutput&&rawAssigned!=null&&staged==null){
                        try{
                            if(!ConvertSourceBaseline.IsUnderDir(rawAssigned,itemOutRoot)){
                                sourceStubsToClean.Add(rawAssigned);
                                string rdir=Path.GetDirectoryName(rawAssigned);
                                if(!string.IsNullOrEmpty(rdir))touchedSrcDirs.Add(rdir);
                            }
                        }catch{}
                    }
                    string assigned=rawAssigned;
                    if(staged!=null&&assigned!=null)assigned=Unstage(assigned,staged,item);   // 临时拷贝 → 按原文件的位置放输出（镜像目录照常）
                    if(assigned!=null)name=Path.GetFileName(assigned);
                    string finalName=name;
                    if(finalName==null||finalName.Length==0)finalName=Path.GetFileNameWithoutExtension(item.Source)+ext;
                    if(!HasNativeExtension(finalName))finalName=finalName+ext;
                    string target;
                    string trueSource=null;
                    string preClaimedLock=null;
                    if(node==root){
                        target=ConvertPlanner.TopTargetFor(item,options,ext);
                        if(isSw)trueSource=item.Source;
                    } else {
                        target=ConvertPlanner.ComponentTarget(assigned,finalName,item,options);
                        if(isSw&&assigned!=null){
                            bool wasSuffixed;
                            trueSource=ConvertPlanner.ResolveSolidWorksSource(assigned,out wasSuffixed);
                            if(wasSuffixed&&trueSource!=null){
                                string cleanName=Path.GetFileNameWithoutExtension(trueSource)+ext;
                                string cleanAssigned=Path.Combine(Path.GetDirectoryName(trueSource),cleanName);
                                string cleanTarget=ConvertPlanner.ComponentTarget(cleanAssigned,cleanName,item,options);
                                string cleanFull="";
                                try{ cleanFull=Path.GetFullPath(cleanTarget); }catch{}
                                if(cleanFull.Length>0&&!writtenFrom.ContainsKey(cleanTarget)&&!written.Contains(cleanFull)
                                    &&!ConvertWrittenIndex.ContainsAny(lockRoot,new[]{cleanFull})
                                    &&TryClaimNonBlocking(lockRoot,cleanTarget,out preClaimedLock)){
                                    target=cleanTarget;
                                    finalName=cleanName;
                                }
                            }
                        }
                    }
                    string sourceKey=trueSource??assigned??finalName;
                    bool isFlat=options.FlatOutput&&options.OutputMode!=ConvertOutputMode.InPlace;
                    if(isFlat){
                        string flatClash;
                        string flatResolved=ResolveFlatTargetCrossRun(target,sourceKey,out flatClash);
                        if(!string.Equals(flatResolved,target,StringComparison.OrdinalIgnoreCase)){
                            if(preClaimedLock!=null){ try{ if(File.Exists(preClaimedLock))File.Delete(preClaimedLock); }catch{} preClaimedLock=null; }
                            log("平铺输出跨装配同名冲突："+finalName+" 与其他目录零件重名（"+flatClash+"），本件另存为 "+Path.GetFileName(flatResolved)+"。");
                            target=flatResolved;
                            finalName=Path.GetFileName(flatResolved);
                        }
                    }
                    if(node==root){
                        item.Target=target;row.Target=target;
                    }
                    // 0.10.2：只有"同一个源文件"才并。
                    string clash;
                    string claimed=ConvertPlanner.ClaimTarget(writtenFrom,target,sourceKey,out clash);
                    if(claimed==null){
                        if(preClaimedLock!=null)try{ if(File.Exists(preClaimedLock))File.Delete(preClaimedLock); }catch{}
                        log("同一零件的重复实例，已并入 "+finalName+"。");
                        continue;
                    }
                    if(clash!=null)log("别的文件夹里还有一个不同的 "+finalName+"（"+clash+"），这一个另存为 "+Path.GetFileName(claimed)+"。");
                    if(!string.Equals(claimed,target,StringComparison.OrdinalIgnoreCase)&&preClaimedLock!=null){
                        try{ if(File.Exists(preClaimedLock))File.Delete(preClaimedLock); }catch{}
                        preClaimedLock=null;
                    }
                    target=claimed;
                    if(node==root){ item.Target=target; row.Target=target; }
                    if(target.Length>259){
                        if(preClaimedLock!=null)try{ if(File.Exists(preClaimedLock))File.Delete(preClaimedLock); }catch{}
                        failures++;
                        string msg="输出路径超过 259 字符（"+target.Length+"）："+finalName;
                        if(firstError==null)firstError=msg;
                        log("保存失败 "+msg);
                        continue;
                    }
                    // 大型装配（打开 ≥60 秒）若仅有个别子件确定性保存失败且 CAD 进程仍存活，仍保存顶层装配，
                    // 避免把耗时十几分钟的整装全部作废重试；小/中装配或 CAD 崩溃则照旧跳过顶层以便干净重试。
                    if(node==root&&failures>0&&(row.OpenSeconds<60.0||anyDeadCom)){
                        log("子文档已有 "+failures+" 个保存失败，跳过顶层 "+finalName+" 保存以便干净重试。");
                        continue;
                    }
                    string claimLockFile=preClaimedLock;
                    try{
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        string fullTarget=Path.GetFullPath(target);
                        // 仅当不是重试、且该子件与所有引用它的父装配处于同一输出目录、且文件名未改变时，
                        // 才可安全跳过重复 SaveAs（同目录回落可解析）；若子件位于子/兄弟目录或已改名，或正在重试，
                        // 必须执行 SaveAs 将内存文档路径重定向到输出位置，避免父装配落盘后仍指向源目录形成断链。
                        bool sameDirAsAllParents=true;
                        if(node!=root){
                            string myDir=Path.GetDirectoryName(fullTarget);
                            if(node.Parents.Count==0)sameDirAsAllParents=false;
                            for(int pi=0;pi<node.Parents.Count;pi++){
                                string pdir;
                                if(!nodeOutDirs.TryGetValue(node.Parents[pi],out pdir)||!ConvertPlanner.SameDir(myDir,pdir)){
                                    sameDirAsAllParents=false;
                                    break;
                                }
                            }
                        }
                        bool canSkipAlreadySaved=!isRetry&&sameDirAsAllParents&&SameFileName(rawAssigned,fullTarget);
                        if(node!=root&&preClaimedLock==null&&canSkipAlreadySaved&&written.Contains(fullTarget)){
                            log(finalName+" 这次前面已经存过，不再重存。");
                            continue;
                        }
                        if(node!=root&&preClaimedLock==null&&canSkipAlreadySaved&&!TryClaim(lockRoot,target,out claimLockFile)){
                            log("另一个进程已生成 "+finalName+"，本次跳过写入。");
                            continue;
                        }
                        if(node==root&&preReservedRootLock==null){
                            TryClaimNonBlocking(lockRoot,target,out preReservedRootLock);
                        }
                        // 同目录输出优化：天工 Open() 阶段对子装配通常已落盘且 Dirty==false，
                        // 当 doc.FullName 恰好就是目标路径且文件已存在非空且未脏时，免重复 SaveAs（省去几十秒并避免锁碰撞）。
                        bool alreadySavedInPlace=false;
                        if(node!=root&&!isRetry&&rawAssigned!=null&&SamePath(rawAssigned,fullTarget)){
                            bool isDirty=true;
                            try{ isDirty=doc.Dirty; }catch{}
                            if(!isDirty&&File.Exists(fullTarget)){
                                try{ if(new FileInfo(fullTarget).Length>0)alreadySavedInPlace=true; }catch{}
                            }
                        }
                        if(!alreadySavedInPlace){
                            string local=target;
                            // 细粒度按目标文件路径加锁（取代过去的全局单把互斥锁）：不同文件的 SaveAs 100% 真正并发执行，
                            // 仅当两个进程恰好碰撞到同一路径时才针对该路径串行并退避重试锁定冲突。
                            using(AcquireTargetSaveLock(fullTarget)){ RetrySave(delegate(){ doc.SaveAs(local); },"保存 "+finalName,1800,fullTarget); }
                        }
                        if(node==root)rootSaved=true;
                        writtenThisItem.Add(fullTarget);
                        if(node!=root||(failures==0&&!options.VerifyReopen)){
                            written.Add(fullTarget);
                            RecordTargetSource(fullTarget,sourceKey);
                            ConvertWrittenIndex.MarkTargetWithSource(lockRoot,fullTarget,sourceKey);
                            if(trueSource!=null){
                                try{ writtenSources.Add(Path.GetFullPath(trueSource)); }catch{}
                                ConvertWrittenIndex.MarkSource(lockRoot,trueSource);
                            }
                        } else if(node==root&&failures==0&&options.VerifyReopen){
                            deferredRootMarkTarget=fullTarget;
                            deferredRootMarkSource=trueSource??sourceKey;
                        }
                    }catch(Exception e){
                        if(claimLockFile!=null)try{ if(File.Exists(claimLockFile))File.Delete(claimLockFile); }catch{}
                        if(IsDeadCom(e))anyDeadCom=true;
                        failures++;if(firstError==null)firstError=finalName+"："+FriendlyError(e);
                        log("保存失败 "+finalName+" "+FriendlyError(e));
                    }
                }
                row.SaveSeconds=saveWatch.Elapsed.TotalSeconds;
                if(failures>0){
                    row.Status="fail";
                    row.Error=(rootSaved?"顶层装配已保存，但有 ":"")+failures+" 个文档保存失败；首个："+firstError;
                    if(rootSaved&&row.OpenSeconds>=60.0&&!anyDeadCom){
                        row.NoRetry=true;
                        // 虽已保存顶层装配但子件不完整：必须保留 .tgconvert-pending，避免下次续跑静默跳过损坏装配。
                        row.KeepPending=true;
                    }
                }
                else row.Status="ok";
            }catch(Exception e){
                row.Status="fail";row.Error=FriendlyError(e);
                if(top==null&&string.Equals(Path.GetExtension(item.Source),".slddrw",StringComparison.OrdinalIgnoreCase)){
                    row.Error="天工打不开这张 SolidWorks 工程图（.slddrw）：天工的 SolidWorks 转换只认零件和装配。工程图请对转好的模型在天工里重新出图，或者在 SolidWorks 里另存为 DWG / DXF 再导入天工。";
                    row.NoRetry=true;
                }
                log("转换失败 "+Path.GetFileName(item.Source)+"："+FriendlyError(e));
            }finally{
                CloseAll(top);
                // 按目录逐个对照各自批次启动前基线快照清理（禁止用 srcDir 的基线去清理其他子目录的 stub，免误删子目录原有文件）；
                // 全程使用 requireExclusive=true，确保非串行并发小装配不会误删同伴进程正在使用的临时副本。
                if(!sameFolderOutput){
                    foreach(string c in sourceStubsToClean){
                        try{
                            string cdir=Path.GetDirectoryName(Path.GetFullPath(c));
                            if(!string.IsNullOrEmpty(cdir))touchedSrcDirs.Add(cdir);
                        }catch{}
                    }
                    foreach(string dir in touchedSrcDirs){
                        if(string.IsNullOrEmpty(dir)||ConvertSourceBaseline.IsUnderDir(dir,itemOutRoot))continue;
                        HashSet<string> baseSnap=ConvertPlanner.SameDir(dir,srcDir)
                            ?preExistingSrcFiles
                            :ConvertSourceBaseline.TryLoadExisting(lockRoot,dir);
                        var dirStubs=new List<string>();
                        foreach(string c in sourceStubsToClean){
                            try{ if(ConvertPlanner.SameDir(Path.GetDirectoryName(c),dir))dirStubs.Add(c); }catch{}
                        }
                        if(baseSnap!=null){
                            DateTime? minUtc=(ConvertPlanner.SameDir(dir,srcDir)&&!hasBatchBaseline)?(DateTime?)openStartUtc.AddSeconds(-2):null;
                            if(dirStubs.Count>0){
                                CleanupCreatedSourceStubs(dirStubs,baseSnap,writtenThisItem,itemOutRoot,true,minUtc);
                            }
                            if(isSw){
                                CleanupCreatedSourceStubs(SnapshotSourceNativeFiles(dir),baseSnap,writtenThisItem,itemOutRoot,true,minUtc);
                            }
                        } else if(dirStubs.Count>0){
                            // 未在批次前基线内的外部目录：仅清理明确属于本次 Open 时间之后产生的临时副本。
                            CleanupCreatedSourceStubs(dirStubs,null,writtenThisItem,itemOutRoot,true,openStartUtc.AddSeconds(-2));
                        }
                    }
                }
                if(!rootSaved&&preReservedRootLock!=null){
                    try{ if(File.Exists(preReservedRootLock))File.Delete(preReservedRootLock); }catch{}
                }
                row.TotalSeconds=total.Elapsed.TotalSeconds;
            }
            if(row.Status=="ok"&&options.VerifyReopen)Verify(item,row);
            if(row.Status=="ok"&&deferredRootMarkTarget!=null){
                written.Add(deferredRootMarkTarget);
                RecordTargetSource(deferredRootMarkTarget,deferredRootMarkSource);
                ConvertWrittenIndex.MarkTargetWithSource(lockRoot,deferredRootMarkTarget,deferredRootMarkSource);
                if(isSw&&!string.IsNullOrEmpty(item.Source)){
                    try{ writtenSources.Add(Path.GetFullPath(item.Source)); }catch{}
                    ConvertWrittenIndex.MarkSource(lockRoot,item.Source);
                }
            }
            return row;
        }
        void RecordTargetSource(string fullTarget,string sourceKey){
            if(string.IsNullOrEmpty(fullTarget)||string.IsNullOrEmpty(sourceKey))return;
            string norm=sourceKey;
            try{ if(Path.IsPathRooted(sourceKey))norm=Path.GetFullPath(sourceKey); }catch{}
            if(!writtenTargetSources.ContainsKey(fullTarget))writtenTargetSources[fullTarget]=norm;
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
                    try{ string copy=Path.Combine(st.Dir,Path.GetFileName(f)); File.Copy(f,copy,true); st.Map[StageKey(copy)]=f; }
                    catch(Exception e){ skipped++; if(firstSkip==null)firstSkip=Path.GetFileName(f)+"："+e.Message; }
                }
                if(skipped>0)log("有 "+skipped+" 个 SolidWorks 文件没能拷到临时文件夹（路径太长或打不开），用到它们的装配会缺这些件。首个："+firstSkip);   // 导入后文档叫「临时文件夹\名字.par」，按类型 + 文件夹 + 主文件名对
                log("「"+Path.GetFileName(item.Source)+"」打开后（或它的子装配）是空的：它用到的零件不在装配所在的文件夹，天工找不到。已把同一项目里的 "+files.Count+" 个 SolidWorks 文件拷到临时文件夹再打开（这一批里同项目的其他装配接着用，不再重拷）。");
            }
            if(st==null)return null;
            // 项目里有同名文件时临时文件夹里只放了第一个：轮到的这个装配自己必须是它本身
            string mine=Path.Combine(st.Dir,Path.GetFileName(item.Source)),have;
            if(!st.Map.TryGetValue(StageKey(mine),out have)||!string.Equals(have,item.Source,StringComparison.OrdinalIgnoreCase)){ File.Copy(item.Source,mine,true); st.Map[StageKey(mine)]=item.Source; }
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
        // 临时文件夹里的文档 → 原文件所在位置（按装配/零件类型 + 主文件名精确映射，防止同名 .sldasm + .sldprt 互相覆盖串号）：
        //   · 同一个零件这次前面已经转过，天工给后来的那份起名「名字_0」—— 去掉 _数字 再按同类型对；
        //   · SolidWorks 的虚拟零件（「零件^装配」，只存在装配里、项目里没有单独的文件）—— 放在这个装配原来所在的文件夹
        static string Unstage(string assigned,Dictionary<string,string> staged,ConvertItem item){
            string original;
            if(staged.TryGetValue(StageKey(assigned),out original))return Path.Combine(Path.GetDirectoryName(original),Path.GetFileName(assigned));
            string dir=Path.GetDirectoryName(assigned)??"",stem=Path.GetFileNameWithoutExtension(assigned);
            string kind=StageKind(assigned);
            var m=System.Text.RegularExpressions.Regex.Match(stem,@"^(.*)_\d+$");
            if(m.Success&&staged.TryGetValue(kind+"|"+Path.Combine(dir,m.Groups[1].Value),out original))return Path.Combine(Path.GetDirectoryName(original),Path.GetFileName(assigned));
            bool inStage=false;
            foreach(string v in staged.Keys){
                int sep=v.IndexOf('|');
                string rawKey=sep>=0?v.Substring(sep+1):v;
                inStage=string.Equals(Path.GetDirectoryName(rawKey),dir,StringComparison.OrdinalIgnoreCase);
                break;
            }
            return inStage?Path.Combine(Path.GetDirectoryName(item.Source),Path.GetFileName(assigned)):assigned;
        }
        static string StageKind(string path){
            string ext=(Path.GetExtension(path)??"").ToLowerInvariant();
            if(ext==".sldasm"||ext==".asm")return "A";
            if(ext==".sldprt"||ext==".par"||ext==".psm")return "P";
            return "O";
        }
        static string StageKey(string path){ return StageKind(path)+"|"+Path.Combine(Path.GetDirectoryName(path)??"",Path.GetFileNameWithoutExtension(path)); }
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
                PaceOpen();
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
            if(depth>32)return null;
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
        void CloseAll(){ CloseAll(null); }
        // 先关顶层文档（SaveChanges=false），天工会在一次 C++ 内部清理中顺带关闭全部独占子文档，
        // 比逐个跨进程 COM 调 Documents.Item(1).Close(false) 更快；绝不调无参 Documents.Close()（实测会把未存文档自动写回源文件夹）。
        void CloseAll(F.SolidEdgeDocument top){
            if(top!=null){
                try{ top.Close(false); }catch{}
            }
            for(int guard=0;guard<4096;guard++){
                int count=0;
                try{ count=app.Documents.Count; }catch{ return; }
                if(count<=0){ try{ app.DoIdle(); }catch{} return; }
                try{ ((F.SolidEdgeDocument)app.Documents.Item(1)).Close(false); }
                catch(Exception e){ log("关闭文档失败："+e.Message); return; }
            }
            try{ app.DoIdle(); }catch{}
        }
        // Two workers can reach the same component (shared standard parts are common). Each output
        // path is claimed through a lock file so only one worker writes it; the others wait for the
        // file to appear and then skip it. Bounded wait, then take over, so a dead worker cannot
        // stall the batch.
        // 非阻塞认领：仅用于判断干净原名是否尚未被其他进程占用（0ms 返回，绝不等待）。
        public static bool TryClaimNonBlocking(string lockRoot,string targetPath,out string lockPath){
            lockPath=null;
            if(lockRoot==null||lockRoot.Length==0)return true;
            try{
                string directory=Path.Combine(lockRoot,"locks");
                Directory.CreateDirectory(directory);
                string path=Path.Combine(directory,Hash(targetPath)+".lock");
                using(var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None)){ stream.WriteByte(1); }
                lockPath=path;
                return true;
            }catch(IOException){
                return false;
            }catch{ return false; }
        }
        public static bool TryClaim(string lockRoot,string targetPath){
            string _; return TryClaim(lockRoot,targetPath,out _);
        }
        public static bool TryClaim(string lockRoot,string targetPath,out string lockPath){
            lockPath=null;
            if(lockRoot==null||lockRoot.Length==0)return true;
            string directory=Path.Combine(lockRoot,"locks");
            string path;
            try{
                Directory.CreateDirectory(directory);
                path=Path.Combine(directory,Hash(targetPath)+".lock");
            }catch{ return true; }
            // 等待持锁进程真正完成 SaveAs 并落盘 ConvertWrittenIndex 完成标记（禁止仅凭 File.Exists&&Length>0 提前放行半截大文件）；
            // 轮询上限与 180 秒过期阈值对齐，接管过期锁时刷新时间戳防第三进程同时抢写。
            var waitDeadline=DateTime.UtcNow.AddSeconds(185);
            while(DateTime.UtcNow<waitDeadline){
                try{
                    using(var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None)){ stream.WriteByte(1); }
                    lockPath=path;
                    return true;
                }catch(IOException){
                    if(ConvertWrittenIndex.Contains(lockRoot,targetPath))return false;
                    try{
                        if(DateTime.UtcNow-new FileInfo(path).LastWriteTimeUtc>TimeSpan.FromSeconds(180)){
                            try{ File.SetLastWriteTimeUtc(path,DateTime.UtcNow); }catch{}
                            lockPath=path;
                            return true;
                        }
                    }catch{}
                    Thread.Sleep(150);
                }catch{ return true; }
            }
            lockPath=path;
            return true;
        }
        static string Hash(string value){
            return ConvertWrittenIndex.HashPath(value);
        }
        sealed class MutexScope : IDisposable {
            readonly Mutex mutex; readonly bool owned;
            public MutexScope(Mutex mutex,bool owned){ this.mutex=mutex; this.owned=owned; }
            public void Dispose(){ if(owned)try{ mutex.ReleaseMutex(); }catch{} try{ mutex.Dispose(); }catch{} }
        }
        static IDisposable AcquireNamedMutex(string name,int timeoutSeconds){
            Mutex mutex=null;
            try{
                mutex=new Mutex(false,name);
                bool owned;
                try{ owned=mutex.WaitOne(TimeSpan.FromSeconds(timeoutSeconds)); }
                catch(AbandonedMutexException){ owned=true; }
                if(!owned){ mutex.Dispose(); return null; }
                return new MutexScope(mutex,true);
            }catch{ if(mutex!=null)try{ mutex.Dispose(); }catch{} return null; }
        }
        // 按目标输出路径细粒度加锁（取代全局单把互斥锁）：不同文件的 SaveAs 互不阻塞、真正并发写盘。
        static IDisposable AcquireTargetSaveLock(string fullTargetPath){
            return AcquireNamedMutex("Local\\TianGongFormatConvert.Save."+Hash(fullTargetPath),180);
        }
        // 跨进程 Documents.Open 节拍保护（最小间隔 1.0 秒）：
        // 实测天工 2025 在多个实例从空框架切入文档环境（JDLLPostOpen）时，若两个进程在 <400ms 内同时进入 Open，
        // 会并发读写 %APPDATA%\...\LayoutCustom.xml 导致 ToolkitPro 抛出 0xe06d7363/0xc000041d 崩溃。
        // 仅在两次 Open 间隔不足 1 秒时短暂错峰（正常异步流转 0ms 等待），彻底消除多进程中段崩溃与 25 秒重启惩罚。
        static void PaceOpen(){
            const int MinGapMs=1000;
            using(AcquireNamedMutex("Local\\TianGongFormatConvert.OpenPace",15)){
                try{
                    string dir=Path.Combine(Path.GetTempPath(),"TianGongConverter");
                    Directory.CreateDirectory(dir);
                    string stamp=Path.Combine(dir,"open-pace.utc");
                    if(File.Exists(stamp)){
                        long prev;
                        if(long.TryParse(File.ReadAllText(stamp).Trim(),System.Globalization.NumberStyles.Integer,System.Globalization.CultureInfo.InvariantCulture,out prev)){
                            long elapsedMs=(DateTime.UtcNow.Ticks-prev)/TimeSpan.TicksPerMillisecond;
                            if(elapsedMs>=0&&elapsedMs<MinGapMs)Thread.Sleep((int)(MinGapMs-elapsedMs));
                        }
                    }
                    File.WriteAllText(stamp,DateTime.UtcNow.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }catch{}
            }
        }
        public void Dispose(){
            if(app==null)return;
            try{ CloseAll(null); }catch{}
            try{ app.Quit(); }catch{}
            try{ Marshal.FinalReleaseComObject(app); }catch{}
            app=null;
            DropStages();
        }
    }
}
