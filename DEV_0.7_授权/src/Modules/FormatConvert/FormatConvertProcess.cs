using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TianGongCadSuite {
    // Workers are placed in a job object so "停止" can kill them reliably.
    // 注意：worker 通过 COM 拉起的隐藏天工CAD **不在**这个作业里（COM 服务进程由 SCM 启动），
    // 所以停止时还要靠 WorkerChildren 单独回收，否则会留下孤儿 CAD（实测踩过）。
    internal static class NativeJob {
        [StructLayout(LayoutKind.Sequential)]
        struct BASIC_LIMIT {
            public long PerProcessUserTimeLimit;public long PerJobUserTimeLimit;public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;public UIntPtr MaximumWorkingSetSize;public uint ActiveProcessLimit;
            public UIntPtr Affinity;public uint PriorityClass;public uint SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct IO_COUNTERS { public ulong A,B,C,D,E,F; }
        [StructLayout(LayoutKind.Sequential)]
        struct EXTENDED_LIMIT {
            public BASIC_LIMIT Basic;public IO_COUNTERS Io;public UIntPtr ProcessMemoryLimit;public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;public UIntPtr PeakJobMemoryUsed;
        }
        const int ExtendedLimitInformation=9;
        const uint KillOnJobClose=0x2000;
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]
        static extern IntPtr CreateJobObject(IntPtr attributes,string name);
        [DllImport("kernel32.dll")]
        static extern bool SetInformationJobObject(IntPtr job,int infoClass,IntPtr info,uint length);
        [DllImport("kernel32.dll",SetLastError=true)]
        static extern bool AssignProcessToJobObject(IntPtr job,IntPtr process);
        [DllImport("kernel32.dll")]
        static extern bool TerminateJobObject(IntPtr job,uint exitCode);
        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr handle);
        public static IntPtr Create(){
            IntPtr job=CreateJobObject(IntPtr.Zero,null);
            if(job==IntPtr.Zero)return IntPtr.Zero;
            var limit=new EXTENDED_LIMIT();
            limit.Basic.LimitFlags=KillOnJobClose;
            int size=Marshal.SizeOf(typeof(EXTENDED_LIMIT));
            IntPtr memory=Marshal.AllocHGlobal(size);
            try{ Marshal.StructureToPtr(limit,memory,false); SetInformationJobObject(job,ExtendedLimitInformation,memory,(uint)size); }
            finally{ Marshal.FreeHGlobal(memory); }
            return job;
        }
        public static void Add(IntPtr job,Process process){
            if(job==IntPtr.Zero||process==null)return;
            try{ AssignProcessToJobObject(job,process.Handle); }catch{}
        }
        public static void Terminate(IntPtr job){ if(job!=IntPtr.Zero)try{ TerminateJobObject(job,1); }catch{} }
        public static void Close(IntPtr job){ if(job!=IntPtr.Zero)try{ CloseHandle(job); }catch{} }
    }
    // 收尾清理：worker 通过 COM 拉起的隐藏天工CAD 不进作业对象（DCOM 启动的服务进程），
    // 点「停止」只终止作业里的 worker，那个 CAD 会变成孤儿进程（实测：每停一次多一个
    // "TianGong.exe /automation -Embedding"）。这里在终止作业之后把它单独回收掉。
    internal static class WorkerChildren {
        // worker 通过 COM 拉起的隐藏天工CAD 是 **DCOM 启动**的服务进程：父进程恒为 svchost(DcomLaunch)，
        // 既不在作业对象里，也不能靠父进程认出来（实测父进程一直是 1432）。
        // 认它靠两条：命令行里有 /automation，且创建时间不早于本次转换开始前 60 秒。
        // 用户自己开的天工CAD 命令行里没有 /automation，不会被误杀。
        // 现在在跑的天工进程号
        public static HashSet<int> Running(){
            var set=new HashSet<int>();
            try{ foreach(var p in Process.GetProcessesByName("TianGong")){ set.Add(p.Id); p.Dispose(); } }catch{}
            return set;
        }
        // 先杀 worker 自己报上来的那几个；再按 /automation + 创建时间兜底（worker 还没来得及报就被停掉的）。
        // 兜底原来只看"转换开始前 60 秒以后创建"：用户在这 60 秒里、或转换期间从别的程序（PDM、脚本……）以自动化方式开的天工也会被一起杀掉，
        // 没存的东西全没。现在开始前就在跑的一律不碰，兜底的时间窗也收到开始之后。
        public static int Kill(DateTime startedAt,ICollection<int> known,ICollection<int> before,Action<string> log){
            int killed=0;
            var done=new HashSet<int>();
            if(known!=null)foreach(int pid in known){
                if(before!=null&&before.Contains(pid))continue;
                try{ using(var child=Process.GetProcessById(pid)){ child.Kill(); child.WaitForExit(5000); } done.Add(pid); killed++; if(log!=null)log("已结束转换器拉起的隐藏 CAD（pid "+pid+"）。"); }catch{}
            }
            DateTime floor=startedAt.AddSeconds(-2);
            try{
                var query=new ManagementObjectSearcher("SELECT ProcessId,Name,CommandLine,CreationDate FROM Win32_Process WHERE Name='TianGong.exe'");
                foreach(ManagementBaseObject mo in query.Get()){
                    int childPid=Convert.ToInt32(mo["ProcessId"]);
                    if(done.Contains(childPid)||(before!=null&&before.Contains(childPid)))continue;
                    string command=Convert.ToString(mo["CommandLine"]);
                    if(command==null||command.IndexOf("/automation",StringComparison.OrdinalIgnoreCase)<0)continue;
                    if(!CreatedAfter(mo["CreationDate"],floor))continue;
                    try{
                        using(var child=Process.GetProcessById(childPid)){ child.Kill(); child.WaitForExit(5000); }
                        if(log!=null)log("已结束转换器拉起的隐藏 CAD（pid "+childPid+"）。");
                        killed++;
                    }catch{}
                }
            }catch(Exception e){ if(log!=null)log("清理隐藏 CAD 失败："+e.Message); }
            return killed;
        }
        // WMI 的 CreationDate 是 DMTF（20260928231301.123456+480）。读不出来时按"满足"处理：
        // 宁可多清一个刚起来的 /automation 实例，也别把该清的漏掉。
        static bool CreatedAfter(object dmtf,DateTime floor){
            try{
                string text=Convert.ToString(dmtf);
                if(string.IsNullOrEmpty(text)||text.Length<14)return true;
                DateTime created=DateTime.ParseExact(text.Substring(0,14),"yyyyMMddHHmmss",CultureInfo.InvariantCulture);
                return created>=floor;
            }catch{ return true; }
        }
    }

    // 并行预拉起隐藏天工 CAD + ROT 就绪探测：
    // 根因：SolidEdge.Application 注册为 REGCLS_SINGLEUSE LocalServer，多个 worker 同时调用 CoCreateInstance 时，
    // Windows DCOM SCM 会严格串行启动天工（每起 1 个等 ~22~25 秒，4 个要近 100 秒），且 DCOM 拉起的进程不在作业对象里。
    // 改为由主控进程一次性并发启动 N 个「TianGong.exe /automation -Embedding」并立即加入 NativeJob 作业对象，
    // worker 仅轮询 ROT（绝不触发多余 DCOM 启动）等到预拉起实例全部注册完毕后再逐个瞬间绑定（~0.01 秒/个）：
    // 实测 4 实例总启动时间从 ~99 秒降至 ~25~32 秒，且 100% 隔离用户已打开的天工窗口，强杀/停止 0 残留。
    internal static class CadPrelauncher {
        const string SolidEdgeClsidFallback="{DED89DB0-45B6-11CE-B307-0800363A1E02}";
        const string SolidEdgeClsidToken="DED89DB0-45B6-11CE-B307-0800363A1E02";

        [DllImport("ole32.dll")]
        static extern int GetRunningObjectTable(int reserved,out IRunningObjectTable rot);
        [DllImport("ole32.dll")]
        static extern int CreateBindCtx(int reserved,out IBindCtx bc);

        // 只读探测当前 Windows ROT 中已注册的 SolidEdge.Application 数量，不会触发任何进程启动。
        public static int CountRotSolidEdge(){
            int count=0;
            IRunningObjectTable rot=null;
            IBindCtx bc=null;
            IEnumMoniker em=null;
            try{
                if(GetRunningObjectTable(0,out rot)!=0||rot==null)return 0;
                if(CreateBindCtx(0,out bc)!=0||bc==null)return 0;
                rot.EnumRunning(out em);
                if(em==null)return 0;
                var mon=new IMoniker[1];
                while(em.Next(1,mon,IntPtr.Zero)==0){
                    try{
                        string name;
                        mon[0].GetDisplayName(bc,null,out name);
                        if(name!=null&&(name.IndexOf(SolidEdgeClsidToken,StringComparison.OrdinalIgnoreCase)>=0
                            ||name.IndexOf("SolidEdge.Application",StringComparison.OrdinalIgnoreCase)>=0))
                            count++;
                    }catch{}
                    finally{ try{ Marshal.ReleaseComObject(mon[0]); }catch{} }
                }
            }catch{}
            finally{
                if(em!=null)try{ Marshal.ReleaseComObject(em); }catch{}
                if(bc!=null)try{ Marshal.ReleaseComObject(bc); }catch{}
                if(rot!=null)try{ Marshal.ReleaseComObject(rot); }catch{}
            }
            return count;
        }

        // 从 COM 注册表动态解析 TianGong.exe 路径（不写死版本号或安装目录），失败时回退常见路径。
        public static string ResolveCadExecutable(){
            try{
                string clsid=SolidEdgeClsidFallback;
                using(var k=Registry.ClassesRoot.OpenSubKey(@"SolidEdge.Application\CLSID")){
                    string v=k==null?null:Convert.ToString(k.GetValue(null));
                    if(!string.IsNullOrEmpty(v))clsid=v.Trim();
                }
                using(var ls=Registry.ClassesRoot.OpenSubKey(@"CLSID\"+clsid+@"\LocalServer32")){
                    string raw=ls==null?null:Convert.ToString(ls.GetValue(null));
                    string exe=ExtractExePath(raw);
                    if(exe!=null&&File.Exists(exe))return Path.GetFullPath(exe);
                }
            }catch{}
            try{
                string self=Process.GetCurrentProcess().MainModule.FileName;
                string name=Path.GetFileName(self);
                if(string.Equals(name,"TianGong.exe",StringComparison.OrdinalIgnoreCase)||string.Equals(name,"Edge.exe",StringComparison.OrdinalIgnoreCase))
                    return self;
            }catch{}
            try{
                string nds=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"NDS");
                if(Directory.Exists(nds)){
                    foreach(string dir in Directory.GetDirectories(nds,"TianGong*")){
                        string cand=Path.Combine(dir,"Program","TianGong.exe");
                        if(File.Exists(cand))return cand;
                    }
                }
            }catch{}
            return null;
        }

        static string ExtractExePath(string commandLine){
            if(string.IsNullOrWhiteSpace(commandLine))return null;
            string s=commandLine.Trim();
            if(s.StartsWith("\"",StringComparison.Ordinal)){
                int end=s.IndexOf('"',1);
                if(end>1)return s.Substring(1,end-1);
            }
            int exeIdx=s.IndexOf(".exe",StringComparison.OrdinalIgnoreCase);
            if(exeIdx>0)return s.Substring(0,exeIdx+4).Trim('"');
            int sp=s.IndexOf(' ');
            return sp>0?s.Substring(0,sp):s;
        }

        // 并发启动 count 个隐藏天工 CAD 并全部纳入 jobHandle 作业对象。返回成功拉起的进程列表；失败返回空列表（调用方自动退回标准 COM 启动）。
        public static List<Process> Prelaunch(int count,IntPtr jobHandle,Action<string> log,out int rotBefore){
            rotBefore=CountRotSolidEdge();
            var launched=new List<Process>();
            if(count<=0)return launched;
            string exe=ResolveCadExecutable();
            if(string.IsNullOrEmpty(exe)){
                if(log!=null)log("未解析到天工 CAD 路径，回退标准 COM 启动。");
                return launched;
            }
            for(int i=0;i<count;i++){
                try{
                    var psi=new ProcessStartInfo(exe,"/automation -Embedding");
                    psi.UseShellExecute=false;
                    psi.WindowStyle=ProcessWindowStyle.Hidden;
                    var p=Process.Start(psi);
                    if(p!=null){
                        NativeJob.Add(jobHandle,p);
                        launched.Add(p);
                    }
                }catch(Exception e){
                    if(log!=null)log("预拉起隐藏 CAD #"+(i+1)+" 失败："+e.Message);
                }
            }
            return launched;
        }

        public static void CleanupUnused(List<Process> prelaunched,Action<string> log){
            if(prelaunched==null)return;
            foreach(Process p in prelaunched){
                try{
                    if(p!=null&&!p.HasExited){
                        p.Kill();
                        p.WaitForExit(3000);
                        if(log!=null)log("已清理未使用的预拉起 CAD（pid "+p.Id+"）。");
                    }
                }catch{}
                try{ if(p!=null)p.Dispose(); }catch{}
            }
            prelaunched.Clear();
        }
    }

    // 热驻留隐藏 CAD 工作池：
    // 在转换窗口打开期间复用意经启动并预热好转换器 DLL 的隐藏天工 CAD 实例，
    // 第二批起省去 20~25 秒冷启动与 3~5 秒首件初始化，几乎零等待秒速开工；
    // 空闲 5 分钟自动退出释放内存，点「停止」/ 关窗 / 宿主异常退出时由作业对象与兜底清理保证 0 残留。
    internal sealed class WarmConvertPool : IDisposable {
        sealed class Slot {
            public int Id;
            public Process Worker;
            public int CadPid;
        }
        readonly List<Slot> slots=new List<Slot>();
        readonly List<Process> prelaunchedCads=new List<Process>();
        readonly HashSet<int> knownCadPids=new HashSet<int>();
        HashSet<int> cadsBefore=new HashSet<int>();
        IntPtr jobHandle=IntPtr.Zero;
        string poolDir;
        DateTime createdAt;
        int nextSlotId;
        public int IdleTimeoutSeconds=300;

        public int AliveCount {
            get{
                PruneDead();
                return slots.Count;
            }
        }
        public void RecordCadPid(int pid){
            if(pid>0)knownCadPids.Add(pid);
        }
        void PruneDead(){
            for(int i=slots.Count-1;i>=0;i--){
                bool alive=false;
                try{ alive=slots[i].Worker!=null&&!slots[i].Worker.HasExited; }catch{}
                if(!alive){
                    try{ if(slots[i].Worker!=null)slots[i].Worker.Dispose(); }catch{}
                    slots.RemoveAt(i);
                }
            }
            if(slots.Count==0&&jobHandle!=IntPtr.Zero){
                // 所有常驻 worker 已因空闲超时自行退出：清理空壳作业对象与临时目录，下次从干净状态重新创建
                CadPrelauncher.CleanupUnused(prelaunchedCads,null);
                NativeJob.Close(jobHandle);
                jobHandle=IntPtr.Zero;
                TryDeleteDir(poolDir);
                poolDir=null;
                knownCadPids.Clear();
            }
        }
        void RefreshSlotCadPids(){
            if(string.IsNullOrEmpty(poolDir))return;
            foreach(Slot s in slots){
                try{
                    string rf=Path.Combine(poolDir,"slot-"+s.Id.ToString(CultureInfo.InvariantCulture)+".ready");
                    if(!File.Exists(rf))continue;
                    string[] parts=File.ReadAllText(rf,new UTF8Encoding(false)).Trim().Split('\t');
                    int pid;
                    if(parts.Length>0&&int.TryParse(parts[0],NumberStyles.Integer,CultureInfo.InvariantCulture,out pid)&&pid>0){
                        s.CadPid=pid;
                        knownCadPids.Add(pid);
                    }
                }catch{}
            }
        }
        public void Dispatch(int desiredWorkers,string converterExe,string queueDir,ConvertOptions options,string workRoot,
            Action<string> log,out List<string> statusFiles,out List<Process> activeProcs,out int reusedCount,out int newlyBootedCount){
            statusFiles=new List<string>();
            activeProcs=new List<Process>();
            reusedCount=0;
            newlyBootedCount=0;
            if(desiredWorkers<1)desiredWorkers=1;

            PruneDead();
            if(slots.Count==0){
                jobHandle=NativeJob.Create();
                poolDir=Path.Combine(Path.GetTempPath(),"TianGongConverter","pool-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N").Substring(0,6));
                Directory.CreateDirectory(poolDir);
                createdAt=DateTime.Now;
                cadsBefore=WorkerChildren.Running();
                knownCadPids.Clear();
                nextSlotId=0;
            }
            string shutdownFlag=Path.Combine(poolDir,"shutdown.flag");
            try{ if(File.Exists(shutdownFlag))File.Delete(shutdownFlag); }catch{}
            RefreshSlotCadPids();

            // 1. 优先瞬间唤醒已在热池中驻留的 worker（0.08 秒内立即开转，不等扩容进程启动）
            int useExisting=Math.Min(slots.Count,desiredWorkers);
            for(int i=0;i<useExisting;i++){
                Slot slot=slots[i];
                string sf=Path.Combine(workRoot,"status-slot-"+slot.Id.ToString(CultureInfo.InvariantCulture)+".txt");
                File.WriteAllText(sf,"",new UTF8Encoding(false));
                WriteGoTrigger(slot.Id,queueDir,sf,options,workRoot);
                statusFiles.Add(sf);
                activeProcs.Add(slot.Worker);
                reusedCount++;
            }

            // 2. 若本次并发需求大于当前热池数量，仅对差额部分并行预拉起新隐藏 CAD 并扩充热池
            int needNew=desiredWorkers-useExisting;
            if(needNew>0){
                var forbidden=new HashSet<int>(cadsBefore);
                foreach(int pid in knownCadPids)forbidden.Add(pid);
                foreach(Slot s in slots)if(s.CadPid>0)forbidden.Add(s.CadPid);
                string beforeCsv=forbidden.Count==0?"-":string.Join(",",forbidden.Select(p=>p.ToString(CultureInfo.InvariantCulture)).ToArray());

                int rotBefore;
                var launched=CadPrelauncher.Prelaunch(needNew,jobHandle,log,out rotBefore);
                foreach(Process cp in launched){ prelaunchedCads.Add(cp); knownCadPids.Add(cp.Id); }
                int rotTarget=launched.Count>0?(rotBefore+launched.Count):0;
                int parentPid=0;
                try{ parentPid=Process.GetCurrentProcess().Id; }catch{}

                for(int k=0;k<needNew;k++){
                    int sid=nextSlotId++;
                    string sf=Path.Combine(workRoot,"status-slot-"+sid.ToString(CultureInfo.InvariantCulture)+".txt");
                    File.WriteAllText(sf,"",new UTF8Encoding(false));
                    WriteGoTrigger(sid,queueDir,sf,options,workRoot);
                    var info=new ProcessStartInfo(converterExe,CommandLine.Join(
                        "--pool-worker",poolDir,sid.ToString(CultureInfo.InvariantCulture),
                        rotTarget.ToString(CultureInfo.InvariantCulture),beforeCsv,
                        IdleTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
                        parentPid.ToString(CultureInfo.InvariantCulture)));
                    info.UseShellExecute=false;info.CreateNoWindow=true;info.WindowStyle=ProcessWindowStyle.Hidden;
                    try{
                        Process proc=Process.Start(info);
                        NativeJob.Add(jobHandle,proc);
                        slots.Add(new Slot{ Id=sid,Worker=proc,CadPid=0 });
                        statusFiles.Add(sf);
                        activeProcs.Add(proc);
                        newlyBootedCount++;
                    }catch(Exception e){
                        Log.Write("Convert","启动热池 worker 失败 self="+converterExe+"："+e);
                        ConvertLog.Append(sf,ConvertProtocol.Line("FATAL","启动热池进程失败："+e.Message));
                        ConvertLog.Append(sf,ConvertProtocol.Line(ConvertProtocol.Exit,"1"));
                        if(log!=null)log("启动热池转换进程失败："+e.Message);
                    }
                }
            }
        }
        void WriteGoTrigger(int slotId,string queueDir,string statusFile,ConvertOptions options,string workRoot){
            string goPath=Path.Combine(poolDir,"slot-"+slotId.ToString(CultureInfo.InvariantCulture)+".go");
            string tmpPath=goPath+".tmp";
            string line=string.Join("\t",new[]{
                queueDir,statusFile,options.OutputRoot,
                options.FlatOutput?"1":"0",options.VerifyReopen?"1":"0",options.Force?"1":"0",
                workRoot
            });
            File.WriteAllText(tmpPath,line,new UTF8Encoding(false));
            if(File.Exists(goPath))File.Delete(goPath);
            File.Move(tmpPath,goPath);
        }
        public void Shutdown(bool forceKill,Action<string> log){
            RefreshSlotCadPids();
            if(!string.IsNullOrEmpty(poolDir)&&Directory.Exists(poolDir)){
                try{ File.WriteAllText(Path.Combine(poolDir,"shutdown.flag"),"1",new UTF8Encoding(false)); }catch{}
            }
            if(!forceKill&&slots.Count>0){
                var deadline=DateTime.UtcNow.AddMilliseconds(2500);
                while(DateTime.UtcNow<deadline){
                    bool anyAlive=false;
                    foreach(Slot s in slots){ try{ if(s.Worker!=null&&!s.Worker.HasExited){ anyAlive=true; break; } }catch{} }
                    if(!anyAlive)break;
                    System.Threading.Thread.Sleep(100);
                }
            }
            if(jobHandle!=IntPtr.Zero){
                NativeJob.Terminate(jobHandle);
                NativeJob.Close(jobHandle);
                jobHandle=IntPtr.Zero;
            }
            foreach(Slot s in slots){
                try{ if(s.Worker!=null&&!s.Worker.HasExited)s.Worker.Kill(); }catch{}
                try{ if(s.Worker!=null)s.Worker.Dispose(); }catch{}
            }
            slots.Clear();
            CadPrelauncher.CleanupUnused(prelaunchedCads,log);
            if(knownCadPids.Count>0){
                WorkerChildren.Kill(createdAt,knownCadPids,cadsBefore,log);
                knownCadPids.Clear();
            }
            TryDeleteDir(poolDir);
            poolDir=null;
        }
        static void TryDeleteDir(string dir){
            if(string.IsNullOrEmpty(dir)||!Directory.Exists(dir))return;
            try{ Directory.Delete(dir,true); }catch{}
        }
        public void Dispose(){
            Shutdown(false,null);
        }
    }
}
