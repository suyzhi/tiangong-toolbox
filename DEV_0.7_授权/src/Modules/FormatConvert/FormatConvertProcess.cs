using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

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
}
