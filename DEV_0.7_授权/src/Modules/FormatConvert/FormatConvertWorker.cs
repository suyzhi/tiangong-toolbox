using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace TianGongCadSuite {
    // Head-less worker: one process = one hidden CAD session.
    // 支持两种输入模式：
    //   1. 动态任务队列目录（--worker <queueDir> ...）：多进程通过 ConvertQueue 原子抢占任务，零长尾空等；
    //   2. 传统静态清单文件（--worker <jobFile> ...）：向后兼容 ConvertE2E 等命令行脚本。
    // Status is appended to a tab-separated file after every event so the UI can tail it live and a
    // crash still leaves a complete record.
    public static class ConvertWorker {
        public static int Run(string[] args){
            // --worker <jobFileOrQueueDir> <statusFile> <outputRoot> <flat> <verify> <force> [lockRoot] [--wid N] [--rot N] [--before p1,p2]
            if(args.Length<7)return 2;
            // 先查授权再启 CAD：未激活时启动一次 CAD 要约 12 秒，白等且报错含糊。
            if(!TianGongCadSuite.Licensing.LicenseGate.Pass()){
                try{
                    ConvertLog.Append(args[2],ConvertProtocol.Line("FATAL",
                        "未激活或授权已失效，转换器拒绝工作。请先在天工CAD里打开插件任意命令完成激活，"
                        +"再回来点「开始转换」（激活工具不随安装包提供，只能在天工里激活）。"));
                    ConvertLog.Append(args[2],ConvertProtocol.Line(ConvertProtocol.Exit,"1"));
                }catch{}
                return 1;
            }
            string jobSource=args[1];string statusFile=args[2];string outputRoot=args[3];
            var options=new ConvertOptions();
            options.OutputRoot=outputRoot;
            options.FlatOutput=args[4]=="1";
            options.VerifyReopen=args[5]=="1";
            options.Force=args[6]=="1";
            string lockRoot=args.Length>7&&!args[7].StartsWith("--",StringComparison.Ordinal)?args[7]:null;
            int workerId=0,rotTarget=0;
            HashSet<int> forbiddenPids=null;
            for(int i=7;i<args.Length;i++){
                if(args[i]=="--wid"&&i+1<args.Length){ int.TryParse(args[++i],NumberStyles.Integer,CultureInfo.InvariantCulture,out workerId); }
                else if(args[i]=="--rot"&&i+1<args.Length){ int.TryParse(args[++i],NumberStyles.Integer,CultureInfo.InvariantCulture,out rotTarget); }
                else if(args[i]=="--before"&&i+1<args.Length){ forbiddenPids=ParsePids(args[++i]); }
            }
            bool isQueue=Directory.Exists(jobSource);
            int exit=0;
            CadConvertSession session=null;
            Action<string> emitLog=delegate(string message){ ConvertLog.Append(statusFile,ConvertProtocol.Line("LOG",message)); };
            try{
                using(new OleFilter()){
                    if(isQueue&&!ConvertQueue.HasPending(jobSource)){
                        ConvertLog.Append(statusFile,ConvertProtocol.Line(ConvertProtocol.Exit,"0"));
                        return 0;
                    }
                    session=new CadConvertSession(emitLog,lockRoot,rotTarget,forbiddenPids,isQueue?jobSource:null);
                    if(session.SkippedBecauseEmpty){
                        ConvertLog.Append(statusFile,ConvertProtocol.Line(ConvertProtocol.Exit,"0"));
                        return 0;
                    }
                    ConvertLog.Append(statusFile,ConvertProtocol.Line(ConvertProtocol.Ready,ConvertProtocol.Number(session.BootSeconds),session.ProcessId.ToString(CultureInfo.InvariantCulture)));

                    int restarts=0;
                    Action<int> onDeferredHeavy=delegate(int waitSec){
                        if(session!=null&&waitSec>=8){
                            long procMb=session.PrivateMemoryMb();
                            long freeMb=ConvertQueue.CurrentFreeMemoryMb();
                            if(procMb>=600L||freeMb<6000L){
                                emitLog("其他进程正在转换大型装配，先释放当前空闲 CAD 进程（回收 "+procMb+" MB 内存）以避免换页卡顿…");
                                try{ session.Dispose(); }catch{}
                                session=null;
                            }
                        }
                    };
                    if(isQueue){
                        ConvertItem item;string claimed;
                        while(ConvertQueue.TryClaimNext(jobSource,workerId,out item,out claimed,onDeferredHeavy)){
                            try{
                                ProcessOne(ref session,item,options,statusFile,lockRoot,workerId,forbiddenPids,emitLog,ref restarts);
                            }finally{
                                ConvertQueue.Complete(claimed);
                            }
                        }
                    } else {
                        foreach(string raw in File.ReadAllLines(jobSource)){
                            if(raw.Trim().Length==0)continue;
                            string[] parts=raw.Split('\t');
                            if(parts.Length<3)continue;
                            var item=new ConvertItem();
                            item.Index=int.Parse(parts[0],CultureInfo.InvariantCulture);
                            item.Source=parts[1];
                            item.Root=parts[2];
                            item.Worker=parts.Length>3?int.Parse(parts[3],CultureInfo.InvariantCulture):workerId;
                            try{ item.Size=new FileInfo(item.Source).Length; }catch{ item.Size=0; }
                            ProcessOne(ref session,item,options,statusFile,lockRoot,item.Worker,forbiddenPids,emitLog,ref restarts);
                        }
                    }
                }
            }catch(Exception e){
                exit=1;
                // 授权栅栏在深层调用点抛的是"模型操作未能完成…"，对用户毫无指向性（看起来像模型坏了）。
                // 转换器不像插件那样会弹激活窗口，所以这里必须在启动阶段就把话说明白。
                string detail=e.GetType().Name+": "+e.Message;
                try{
                    if(!TianGongCadSuite.Licensing.LicenseGate.Pass())
                        detail="未激活或授权已失效，转换器拒绝工作。请先在天工CAD里打开插件的任意命令完成激活，"
                            +"再回来点「开始转换」。转换器的并行进程不会弹出激活窗口。"
                            +"（原始信息："+detail+"）";
                }catch{}
                try{ if(statusFile!=null)ConvertLog.Append(statusFile,ConvertProtocol.Line("FATAL",detail)); }catch{}
            }finally{
                if(session!=null)try{ session.Dispose(); }catch{}
            }
            try{ ConvertLog.Append(statusFile,ConvertProtocol.Line(ConvertProtocol.Exit,exit.ToString(CultureInfo.InvariantCulture))); }catch{}
            return exit;
        }

        static void ProcessOne(ref CadConvertSession session,ConvertItem item,ConvertOptions options,string statusFile,
            string lockRoot,int workerId,HashSet<int> forbiddenPids,Action<string> emitLog,ref int restarts){
            // 路径太长（0.10.3）：原来这里抛 PathTooLongException 冒到外层，这个 worker 后面的文件一个都不转了。只让这一个失败、说清楚
            string tooLong=null;
            if(item.Source.Length>259)tooLong="源文件路径太长（"+item.Source.Length+" 个字符，Windows 最多 259），天工打不开。把项目文件夹挪到浅一点的地方（比如 D:\\项目\\）再转。";
            else{
                try{ item.Target=ConvertPlanner.TopTarget(item,options); if(item.Target.Length>259)tooLong="输出路径太长（"+item.Target.Length+" 个字符，Windows 最多 259），存不上。把输出目录选短一点（比如 D:\\转换输出），或者不勾「镜像源目录结构」。"; }
                catch(PathTooLongException){ tooLong="输出路径太长（超过 Windows 的 259 个字符），存不上。把输出目录选短一点（比如 D:\\转换输出），或者不勾「镜像源目录结构」。"; }
            }
            if(tooLong!=null){
                ConvertLog.Append(statusFile,ConvertProtocol.Line(ConvertProtocol.End,item.Index.ToString(CultureInfo.InvariantCulture),"fail",item.Source,"0","0","0","0",item.Target??"",workerId.ToString(CultureInfo.InvariantCulture),tooLong));
                return;
            }
            // 这次前面任意进程转装配时已经把它一起转好存出来了（含按源主干去重，覆盖/续跑模式均跳过，杜绝重复转换；即使当前已释放空闲 CAD 也能 0 秒跳过）
            bool wroteAlready=session!=null
                ?session.WroteThisRun(ConvertPlanner.CandidateTargets(item,options),item.Source)
                :ConvertWrittenIndex.ContainsAnyOrSource(lockRoot,ConvertPlanner.CandidateTargets(item,options),item.Source);
            if(wroteAlready){
                ConvertLog.Append(statusFile,ConvertProtocol.Line(ConvertProtocol.End,item.Index.ToString(CultureInfo.InvariantCulture),"skip",item.Source,"0","0","0","0",item.Target,workerId.ToString(CultureInfo.InvariantCulture),"这次已随装配一起转好"));
                return;
            }
            if(!options.Force&&ConvertPlanner.IsAlreadyConverted(item,options)){
                ConvertLog.Append(statusFile,ConvertProtocol.Line(ConvertProtocol.End,item.Index.ToString(CultureInfo.InvariantCulture),"skip",item.Source,"0","0","0","0",item.Target,workerId.ToString(CultureInfo.InvariantCulture),"输出已存在"));
                return;
            }
            // 天工批量连续打开几十/上百个文档后会累计泄漏数 GB 内存；空闲且超过 4 GB 时在文件间隙主动重启以防崩溃换页
            if(session!=null){
                long idleMb=session.PrivateMemoryMb();
                if(idleMb>=4000L){
                    ConvertLog.Append(statusFile,ConvertProtocol.Line("LOG","隐藏 CAD 空闲内存已达 "+idleMb+" MB，主动重启回收内存…"));
                    try{ session.Dispose(); }catch{}
                    session=null;
                }
            }
            if(session==null||!session.IsAlive()){
                try{ if(session!=null)session.Dispose(); }catch{}
                session=new CadConvertSession(emitLog,lockRoot,0,forbiddenPids,null);
                ConvertLog.Append(statusFile,ConvertProtocol.Line(ConvertProtocol.Ready,ConvertProtocol.Number(session.BootSeconds),session.ProcessId.ToString(CultureInfo.InvariantCulture)));
            }
            ConvertLog.Append(statusFile,ConvertProtocol.Line(ConvertProtocol.Begin,item.Index.ToString(CultureInfo.InvariantCulture),item.Source));
            // Marker first: if this item fails or the worker dies, the next resume must not
            // mistake a partly written output for a finished one.
            try{ ConvertPlanner.MarkPending(item,options); }
            catch(Exception e){ ConvertLog.Append(statusFile,ConvertProtocol.Line("LOG","无法写入续做标记："+e.Message)); }

            ConvertRow row=session.Convert(item,options);
            if(row.Status=="fail"&&!row.NoRetry){
                // 会话自愈：若隐藏天工进程在单个异常模型上崩溃断开，自动拉起新会话再重试一次，避免后续所有任务连坐失败。
                if(!session.IsAlive()&&restarts<3){
                    restarts++;
                    ConvertLog.Append(statusFile,ConvertProtocol.Line("LOG","检测到隐藏 CAD 会话异常退出，正在自动重建会话（第 "+restarts+" 次）…"));
                    try{ session.Dispose(); }catch{}
                    session=new CadConvertSession(emitLog,lockRoot,0,forbiddenPids,null);
                    ConvertLog.Append(statusFile,ConvertProtocol.Line(ConvertProtocol.Ready,ConvertProtocol.Number(session.BootSeconds),session.ProcessId.ToString(CultureInfo.InvariantCulture)));
                }
                ConvertLog.Append(statusFile,ConvertProtocol.Line("LOG","重试 "+item.Source));
                row=session.Convert(item,options);
            }
            if(row.Status=="ok"||row.NoRetry){   // NoRetry：根本没写出东西（打不开的工程图），续做标记留着只会在输出文件夹里多一堆 .tgconvert-pending
                try{ ConvertPlanner.ClearPending(item,options); }
                catch(Exception e){ ConvertLog.Append(statusFile,ConvertProtocol.Line("LOG","无法清除续做标记："+e.Message)); }
            }
            if(row.Error==null)row.Error="";
            ConvertLog.Append(statusFile,ConvertProtocol.Line(ConvertProtocol.End,row.Index.ToString(CultureInfo.InvariantCulture),row.Status,row.Source,
                ConvertProtocol.Number(row.OpenSeconds),ConvertProtocol.Number(row.SaveSeconds),ConvertProtocol.Number(row.TotalSeconds),
                row.Documents.ToString(CultureInfo.InvariantCulture),row.Target==null?"":row.Target,workerId.ToString(CultureInfo.InvariantCulture),row.Error));
        }

        static HashSet<int> ParsePids(string csv){
            var set=new HashSet<int>();
            if(string.IsNullOrEmpty(csv)||csv=="-")return set;
            foreach(string tok in csv.Split(',')){
                int pid;
                if(int.TryParse(tok.Trim(),NumberStyles.Integer,CultureInfo.InvariantCulture,out pid)&&pid>0)set.Add(pid);
            }
            return set;
        }

        // 热驻留常驻工作进程：--pool-worker <poolDir> <slotId> <rotTarget> <beforeCsv> <idleSec> <parentPid>
        // 启动一次隐藏天工 CAD 后常驻内存，连续多个批次通过 slot-<id>.go 触发秒级接入动态队列，
        // 省掉每次 20~25 秒 CAD 冷启动与 3~5 秒首件冷初始化；空闲超时、父进程退出或收到 shutdown.flag 自动安全释放。
        public static int RunPool(string[] args){
            if(args.Length<7)return 2;
            string poolDir=args[1];
            int slotId=0,rotTarget=0,idleSec=300,parentPid=0;
            int.TryParse(args[2],NumberStyles.Integer,CultureInfo.InvariantCulture,out slotId);
            int.TryParse(args[3],NumberStyles.Integer,CultureInfo.InvariantCulture,out rotTarget);
            HashSet<int> forbiddenPids=ParsePids(args[4]);
            int.TryParse(args[5],NumberStyles.Integer,CultureInfo.InvariantCulture,out idleSec);
            int.TryParse(args[6],NumberStyles.Integer,CultureInfo.InvariantCulture,out parentPid);
            if(idleSec<30)idleSec=30;

            string slotPrefix=Path.Combine(poolDir,"slot-"+slotId.ToString(CultureInfo.InvariantCulture));
            string readyFile=slotPrefix+".ready";
            string goFile=slotPrefix+".go";
            string busyFile=slotPrefix+".busy";
            string stopFile=slotPrefix+".stop";
            string fatalFile=slotPrefix+".fatal";
            string shutdownFlag=Path.Combine(poolDir,"shutdown.flag");

            if(!TianGongCadSuite.Licensing.LicenseGate.Pass()){
                string msg="未激活或授权已失效，转换器拒绝工作。请先在天工CAD里打开插件任意命令完成激活，再回来点「开始转换」。";
                WriteSmallFile(fatalFile,msg);
                NotifyPendingGoFatal(goFile,msg);
                return 1;
            }

            CadConvertSession session=null;
            try{
                using(new OleFilter()){
                    Action<string> noopLog=delegate(string m){};
                    session=new CadConvertSession(noopLog,null,rotTarget,forbiddenPids,null);
                    WriteSmallFile(readyFile,session.ProcessId.ToString(CultureInfo.InvariantCulture)+"\t"+ConvertProtocol.Number(session.BootSeconds));

                    bool isWarm=false;
                    DateTime lastActive=DateTime.UtcNow;
                    while(true){
                        if(File.Exists(shutdownFlag)||File.Exists(stopFile))break;
                        if(parentPid>0&&!IsProcessAlive(parentPid))break;
                        if((DateTime.UtcNow-lastActive).TotalSeconds>=idleSec)break;

                        if(!File.Exists(goFile)){
                            Thread.Sleep(80);
                            continue;
                        }
                        try{
                            if(File.Exists(busyFile))File.Delete(busyFile);
                            File.Move(goFile,busyFile);
                        }catch{
                            Thread.Sleep(40);
                            continue;
                        }

                        string statusFile=null;
                        try{
                            string spec=File.ReadAllText(busyFile,new UTF8Encoding(false)).Trim();
                            string[] fields=spec.Split('\t');
                            if(fields.Length>=7){
                                string queueDir=fields[0];
                                statusFile=fields[1];
                                var options=new ConvertOptions();
                                options.OutputRoot=fields[2];
                                options.FlatOutput=fields[3]=="1";
                                options.VerifyReopen=fields[4]=="1";
                                options.Force=fields[5]=="1";
                                string workRoot=fields[6];
                                Action<string> emitLog=delegate(string m){ ConvertLog.Append(statusFile,ConvertProtocol.Line("LOG",m)); };

                                if(session==null||!session.IsAlive()){
                                    try{ if(session!=null)session.Dispose(); }catch{}
                                    session=new CadConvertSession(emitLog,workRoot,0,forbiddenPids,null);
                                    WriteSmallFile(readyFile,session.ProcessId.ToString(CultureInfo.InvariantCulture)+"\t"+ConvertProtocol.Number(session.BootSeconds));
                                    isWarm=false;
                                }
                                session.BeginBatch(workRoot,emitLog);
                                double reportBoot=isWarm?0.0:session.BootSeconds;
                                ConvertLog.Append(statusFile,ConvertProtocol.Line(ConvertProtocol.Ready,
                                    ConvertProtocol.Number(reportBoot),
                                    session.ProcessId.ToString(CultureInfo.InvariantCulture),
                                    isWarm?"warm":"cold"));

                                int restarts=0;
                                Action<int> onDeferredHeavy=delegate(int waitSec){
                                    if(session!=null&&waitSec>=8){
                                        long procMb=session.PrivateMemoryMb();
                                        long freeMb=ConvertQueue.CurrentFreeMemoryMb();
                                        if(procMb>=600L||freeMb<6000L){
                                            emitLog("其他进程正在转换大型装配，先释放当前空闲 CAD 进程（回收 "+procMb+" MB 内存）以避免换页卡顿…");
                                            try{ if(File.Exists(readyFile))File.Delete(readyFile); }catch{}
                                            try{ session.Dispose(); }catch{}
                                            session=null;
                                            isWarm=false;
                                        }
                                    }
                                };
                                ConvertItem item;string claimed;
                                while(ConvertQueue.TryClaimNext(queueDir,slotId,out item,out claimed,onDeferredHeavy)){
                                    try{
                                        ProcessOne(ref session,item,options,statusFile,workRoot,slotId,forbiddenPids,emitLog,ref restarts);
                                    }finally{
                                        ConvertQueue.Complete(claimed);
                                    }
                                    if(File.Exists(shutdownFlag)||File.Exists(stopFile))break;
                                }
                                if(session!=null){
                                    session.EndBatch();
                                    WriteSmallFile(readyFile,session.ProcessId.ToString(CultureInfo.InvariantCulture)+"\t"+ConvertProtocol.Number(session.BootSeconds));
                                    isWarm=true;
                                    ConvertLog.Append(statusFile,ConvertProtocol.Line(ConvertProtocol.Exit,"0"));
                                } else {
                                    // 本槽位在等待重型装配期间已释放 CAD，且后续剩余条目已全部 0 秒跳过（无需再起 CAD）：干净退出让热池回收槽位
                                    ConvertLog.Append(statusFile,ConvertProtocol.Line(ConvertProtocol.Exit,"0"));
                                    break;
                                }
                            }
                        }catch(Exception ex){
                            string detail=ex.GetType().Name+": "+ex.Message;
                            if(statusFile!=null){
                                ConvertLog.Append(statusFile,ConvertProtocol.Line("FATAL",detail));
                                ConvertLog.Append(statusFile,ConvertProtocol.Line(ConvertProtocol.Exit,"1"));
                            }
                        }finally{
                            try{ if(File.Exists(busyFile))File.Delete(busyFile); }catch{}
                            lastActive=DateTime.UtcNow;
                        }
                    }
                }
                return 0;
            }catch(Exception e){
                string detail=e.GetType().Name+": "+e.Message;
                try{
                    if(!TianGongCadSuite.Licensing.LicenseGate.Pass())
                        detail="未激活或授权已失效，转换器拒绝工作。请先在天工CAD里打开插件的任意命令完成激活，再回来点「开始转换」。（原始信息："+detail+"）";
                }catch{}
                WriteSmallFile(fatalFile,detail);
                NotifyPendingGoFatal(goFile,detail);
                return 1;
            }finally{
                try{ if(File.Exists(readyFile))File.Delete(readyFile); }catch{}
                if(session!=null)try{ session.Dispose(); }catch{}
            }
        }
        static void NotifyPendingGoFatal(string goFile,string detail){
            try{
                if(!File.Exists(goFile))return;
                string spec=File.ReadAllText(goFile,new UTF8Encoding(false)).Trim();
                string[] fields=spec.Split('\t');
                if(fields.Length>=2){
                    ConvertLog.Append(fields[1],ConvertProtocol.Line("FATAL",detail));
                    ConvertLog.Append(fields[1],ConvertProtocol.Line(ConvertProtocol.Exit,"1"));
                }
                File.Delete(goFile);
            }catch{}
        }
        static void WriteSmallFile(string path,string content){
            try{
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path,content,new UTF8Encoding(false));
            }catch{}
        }
        static bool IsProcessAlive(int pid){
            try{ using(var p=Process.GetProcessById(pid)){ return !p.HasExited; } }catch{ return false; }
        }
    }
    // Small append-only writer shared by the session and the worker.
    public static class ConvertLog {
        public static void Append(string path,string line){
            if(path==null)return;
            try{ ConvertProtocol.Append(path,line); }catch{}
        }
    }
}
