using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace TianGongCadSuite {
    public sealed class FormatConvertForm : Form {
        readonly ListBox inputs=new ListBox();
        readonly TextBox output=new TextBox();
        readonly CheckBox mirror=new CheckBox();
        readonly CheckBox parts=new CheckBox();
        readonly CheckBox assemblies=new CheckBox();
        readonly CheckBox drawings=new CheckBox();
        readonly CheckBox step=new CheckBox();
        readonly CheckBox recursive=new CheckBox();
        readonly CheckBox force=new CheckBox();
        readonly CheckBox verify=new CheckBox();
        readonly CheckBox autoWorkers=new CheckBox();
        readonly NumericUpDown workers=new NumericUpDown();
        readonly Button start=new Button();
        readonly Button stop=new Button();
        readonly Button openOutput=new Button();
        readonly Button saveReport=new Button();
        readonly ProgressBar progress=new ProgressBar();
        readonly Label status=new Label();
        readonly TextBox logBox=new TextBox();
        readonly Timer timer=new Timer();
        readonly Label estimate=new Label();

        readonly List<ConvertItem> items=new List<ConvertItem>();
        readonly Dictionary<int,ConvertRow> rows=new Dictionary<int,ConvertRow>();
        readonly List<Process> processes=new List<Process>();
        readonly List<string> statusFiles=new List<string>();
        readonly Dictionary<string,long> offsets=new Dictionary<string,long>();
        string workRoot;
        string reportPath;
        DateTime startedAt;
        int totalPlanned;
        IntPtr jobHandle=IntPtr.Zero;

        public FormatConvertForm(){
            Text="天工CAD 批量格式转换 SolidWorks / STEP (DEV 0.5)";
            ClientSize=new Size(900,660);
            MinimumSize=new Size(760,560);
            StartPosition=FormStartPosition.CenterScreen;
            Font=new Font("Microsoft YaHei UI",9f);

            var inputLabel=MakeLabel("输入（SolidWorks 文件或文件夹，可多选）",12,10);
            Controls.Add(inputLabel);
            inputs.Location=new Point(12,32);inputs.Size=new Size(660,120);inputs.SelectionMode=SelectionMode.MultiExtended;
            inputs.Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right;
            Controls.Add(inputs);
            Controls.Add(MakeButton("添加文件…",684,32,96,delegate{ AddFiles(); }));
            Controls.Add(MakeButton("添加文件夹…",684,60,96,delegate{ AddFolder(); }));
            Controls.Add(MakeButton("移除选中",684,88,96,delegate{ RemoveSelected(); }));
            Controls.Add(MakeButton("清空",684,116,96,delegate{ items.Clear();RefreshList(); }));

            Controls.Add(MakeLabel("输出目录",12,160));
            output.Location=new Point(80,157);output.Size=new Size(556,25);output.Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right;
            Controls.Add(output);
            Controls.Add(MakeButton("浏览…",644,156,80,delegate{ BrowseOutput(); }));
            Controls.Add(MakeButton("打开",728,156,52,delegate{ OpenOutputFolder(); }));
            openOutput.Visible=false;

            mirror.Text="镜像源目录结构（推荐，避免同名覆盖）";mirror.Location=new Point(80,186);mirror.Size=new Size(300,22);mirror.Checked=true;Controls.Add(mirror);
            force.Text="覆盖已存在的转换结果";force.Location=new Point(400,186);force.Size=new Size(200,22);Controls.Add(force);
            verify.Text="转换后重新打开校验引用";verify.Location=new Point(610,186);verify.Size=new Size(220,22);Controls.Add(verify);

            parts.Text="零件 .SLDPRT";parts.Location=new Point(80,212);parts.Size=new Size(120,22);parts.Checked=true;Controls.Add(parts);
            assemblies.Text="装配 .SLDASM";assemblies.Location=new Point(210,212);assemblies.Size=new Size(130,22);assemblies.Checked=true;Controls.Add(assemblies);
            drawings.Text="工程图 .SLDDRW";drawings.Location=new Point(350,212);drawings.Size=new Size(140,22);Controls.Add(drawings);
            step.Text="STEP .stp/.step";step.Location=new Point(500,212);step.Size=new Size(150,22);step.Checked=true;Controls.Add(step);
            recursive.Text="包含子文件夹";recursive.Location=new Point(660,212);recursive.Size=new Size(130,22);recursive.Checked=true;Controls.Add(recursive);

            Controls.Add(MakeLabel("并行进程",12,244));
            workers.Location=new Point(80,241);workers.Size=new Size(56,25);workers.Minimum=1;workers.Maximum=8;workers.Value=2;Controls.Add(workers);
            autoWorkers.Text="自动（推荐：单进程）";autoWorkers.Location=new Point(146,243);autoWorkers.Size=new Size(180,22);autoWorkers.Checked=true;
            autoWorkers.CheckedChanged+=delegate{ workers.Enabled=!autoWorkers.Checked; };workers.Enabled=false;
            Controls.Add(autoWorkers);
            estimate.Location=new Point(330,244);estimate.Size=new Size(560,22);estimate.ForeColor=Color.DimGray;
            estimate.Text="并行>1 为实验特性：实测仅快约 1.2 倍，且个别零件可能保存失败";
            Controls.Add(estimate);

            Controls.Add(MakeLabel("输出格式：装配 .asm、零件 .par、钣金 .psm、工程图 .dft；转换由天工CAD自带的 SolidWorks / STEP 转换器完成。",12,272,true));

            start.Text="开始转换";start.Location=new Point(12,300);start.Size=new Size(110,30);start.Click+=delegate{ StartConversion(); };Controls.Add(start);
            stop.Text="停止";stop.Location=new Point(130,300);stop.Size=new Size(90,30);stop.Enabled=false;stop.Click+=delegate{ StopConversion(); };Controls.Add(stop);
            saveReport.Text="导出报告";saveReport.Location=new Point(228,300);saveReport.Size=new Size(100,30);saveReport.Enabled=false;saveReport.Click+=delegate{ SaveReportAs(); };Controls.Add(saveReport);
            Controls.Add(MakeButton("扫描文件",336,300,100,30,delegate{ ScanInputs(); }));
            Controls.Add(MakeButton("输出目录",444,300,100,30,delegate{ OpenOutputFolder(); }));

            progress.Location=new Point(12,340);progress.Size=new Size(876,18);progress.Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right;
            Controls.Add(progress);
            status.Location=new Point(12,362);status.Size=new Size(876,20);status.Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right;
            status.Text="就绪。";Controls.Add(status);

            logBox.Location=new Point(12,388);logBox.Size=new Size(876,258);logBox.Multiline=true;logBox.ScrollBars=ScrollBars.Vertical;
            logBox.ReadOnly=true;logBox.BackColor=Color.FromArgb(250,250,250);
            logBox.Anchor=AnchorStyles.Top|AnchorStyles.Bottom|AnchorStyles.Left|AnchorStyles.Right;
            Controls.Add(logBox);

            timer.Interval=500;timer.Tick+=delegate{ Poll(); };
            AcceptButton=null;
        }
        public void PrefillInput(string folder){
            if(folder==null||folder.Length==0||!Directory.Exists(folder))return;
            if(!inputs.Items.Contains(folder))inputs.Items.Add(folder);
            if(output.Text.Length==0)output.Text=Path.Combine(folder,"天工格式输出");
        }
        Label MakeLabel(string text,int x,int y){ return MakeLabel(text,x,y,false); }
        Label MakeLabel(string text,int x,int y,bool small){ var l=new Label();l.Text=text;l.Location=new Point(x,y);l.AutoSize=true;if(small)l.ForeColor=Color.DimGray;return l; }
        Button MakeButton(string text,int x,int y,int w,Action action){ return MakeButton(text,x,y,w,26,action); }
        Button MakeButton(string text,int x,int y,int w,int h,Action action){ var b=new Button();b.Text=text;b.Location=new Point(x,y);b.Size=new Size(w,h);b.Click+=delegate{ action(); };return b; }

        void AddFiles(){
            using(var dialog=new OpenFileDialog()){
                dialog.Multiselect=true;dialog.Filter="CAD 文件|*.sldasm;*.SLDASM;*.sldprt;*.SLDPRT;*.slddrw;*.SLDDRW;*.stp;*.STP;*.step;*.STEP|SolidWorks|*.sldasm;*.SLDASM;*.sldprt;*.SLDPRT;*.slddrw;*.SLDDRW|STEP|*.stp;*.STP;*.step;*.STEP|所有文件|*.*";
                if(dialog.ShowDialog(this)!=DialogResult.OK)return;
                foreach(string file in dialog.FileNames)if(!inputs.Items.Contains(file))inputs.Items.Add(file);
                if(output.Text.Length==0)output.Text=Path.GetDirectoryName(dialog.FileNames[0]);
            }
        }
        void AddFolder(){
            using(var dialog=new FolderBrowserDialog()){
                dialog.Description="选择包含 SolidWorks 文件的文件夹（可重复添加多个）";
                if(dialog.ShowDialog(this)!=DialogResult.OK)return;
                if(!inputs.Items.Contains(dialog.SelectedPath))inputs.Items.Add(dialog.SelectedPath);
                if(output.Text.Length==0)output.Text=Path.Combine(dialog.SelectedPath,"天工格式输出");
            }
        }
        void RemoveSelected(){
            var chosen=new List<object>();
            foreach(object item in inputs.SelectedItems)chosen.Add(item);
            foreach(object item in chosen)inputs.Items.Remove(item);
        }
        void BrowseOutput(){
            using(var dialog=new FolderBrowserDialog()){
                dialog.Description="选择转换结果的输出目录";
                if(dialog.ShowDialog(this)!=DialogResult.OK)return;
                output.Text=dialog.SelectedPath;
            }
        }
        void OpenOutputFolder(){
            string path=output.Text;
            try{
                if(path!=null&&path.Length>0){ Directory.CreateDirectory(path); Process.Start("explorer.exe","\""+path+"\""); }
            }catch(Exception e){ MessageBox.Show(this,"无法打开目录："+e.Message,"天工格式转换"); }
        }
        void RefreshList(){ inputs.Items.Clear(); foreach(ConvertItem item in items)inputs.Items.Add(item.Source); }

        ConvertOptions BuildOptions(){
            var options=new ConvertOptions();
            options.OutputRoot=output.Text.Trim();
            options.Recursive=recursive.Checked;
            options.FlatOutput=!mirror.Checked;
            options.Force=force.Checked;
            options.VerifyReopen=verify.Checked;
            options.IncludeParts=parts.Checked;
            options.IncludeAssemblies=assemblies.Checked;
            options.IncludeDrawings=drawings.Checked;
            options.IncludeStep=step.Checked;
            foreach(object entry in inputs.Items)options.Inputs.Add(Convert.ToString(entry));
            options.Workers=EffectiveWorkers(options);
            return options;
        }
        int EffectiveWorkers(ConvertOptions options){
            if(!autoWorkers.Checked)return (int)workers.Value;
            return ConvertPlanner.SuggestedWorkers(Math.Max(1,inputs.Items.Count),Environment.ProcessorCount,(long)AvailableMemoryMb());
        }
        int RequestedWorkers(){ return autoWorkers.Checked?1:(int)workers.Value; }
        static ulong AvailableMemoryMb(){
            try{
                var status=new MEMORYSTATUSEX();status.dwLength=(uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
                if(GlobalMemoryStatusEx(ref status))return status.ullAvailPhys/(1024*1024);
            }catch{}
            return 2048;
        }
        void ScanInputs(){
            var options=BuildOptions();
            string problem=options.Validate();
            if(problem!=null&&problem.IndexOf("输出目录",StringComparison.Ordinal)<0){ MessageBox.Show(this,problem,"天工格式转换"); return; }
            items.Clear();
            items.AddRange(ConvertPlanner.Scan(options));
            RefreshList();
            long bytes=0;foreach(ConvertItem item in items)bytes+=item.Size;
            status.Text="已发现 "+items.Count+" 个 SolidWorks 文件，合计 "+(bytes/1048576.0).ToString("F1")+" MB。";
            AppendLog(status.Text);
            if(items.Count==0)MessageBox.Show(this,"没有找到可转换的文件。请检查输入路径、扩展名勾选和“包含子文件夹”。","天工格式转换");
        }
        void StartConversion(){
            if(timer.Enabled)return;
            var options=BuildOptions();
            string problem=options.Validate();
            if(problem!=null){ MessageBox.Show(this,problem,"天工格式转换"); return; }
            try{ Directory.CreateDirectory(options.OutputRoot); }catch(Exception e){ MessageBox.Show(this,"无法创建输出目录："+e.Message,"天工格式转换"); return; }
            if(items.Count==0||force.Checked||verify.Checked)ScanInputs();
            if(items.Count==0)return;
            int count=EffectiveWorkers(options);
            options.Workers=count;
            var plan=ConvertPlanner.Plan(items,count);
            if(count>1)AppendLog("警告：并行进程 >1 属于实验特性，同一批里共用零件的装配可能出现保存失败（会自动重试一次）。");
            workRoot=Path.Combine(Path.GetTempPath(),"TianGongConverter",DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(workRoot);
            processes.Clear();statusFiles.Clear();offsets.Clear();rows.Clear();
            totalPlanned=items.Count;
            reportPath=Path.Combine(options.OutputRoot,"转换报告-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".csv");
            startedAt=DateTime.Now;
            string self=Application.ExecutablePath;
            jobHandle=NativeJob.Create();
            int started=0;
            for(int i=0;i<plan.Count;i++){
                if(plan[i].Count==0)continue;
                var builder=new StringBuilder();
                foreach(ConvertItem item in plan[i])builder.Append(item.Index).Append('\t').Append(item.Source).Append('\t').Append(item.Root).Append('\t').Append(i).AppendLine();
                string jobFile=Path.Combine(workRoot,"job-"+i+".txt");File.WriteAllText(jobFile,builder.ToString(),new UTF8Encoding(false));
                string statusFile=Path.Combine(workRoot,"status-"+i+".txt");File.WriteAllText(statusFile,"",new UTF8Encoding(false));
                var info=new ProcessStartInfo(self,"--worker \""+jobFile+"\" \""+statusFile+"\" \""+options.OutputRoot+"\" "+(options.FlatOutput?"1":"0")+" "+(options.VerifyReopen?"1":"0")+" "+(options.Force?"1":"0")+" \""+workRoot+"\"");
                info.UseShellExecute=false;info.CreateNoWindow=true;info.WindowStyle=ProcessWindowStyle.Hidden;
                var process=Process.Start(info);
                NativeJob.Add(jobHandle,process);
                processes.Add(process);statusFiles.Add(statusFile);offsets[statusFile]=0;started++;
            }
            AppendLog("开始转换："+items.Count+" 个文件，"+started+" 个并行进程。输出目录 "+options.OutputRoot);
            AppendLog("并行进程数按 CPU "+(Environment.ProcessorCount)+" 核、可用内存 "+AvailableMemoryMb()+" MB 选择。");
            start.Enabled=false;stop.Enabled=true;saveReport.Enabled=false;
            progress.Maximum=totalPlanned;progress.Value=0;
            timer.Start();
        }
        void StopConversion(){
            AppendLog("正在停止……");
            NativeJob.Terminate(jobHandle);
            foreach(Process process in processes){ try{ if(!process.HasExited)process.Kill(); }catch{} }
        }
        void Poll(){
            bool running=false;
            foreach(Process process in processes){ try{ if(!process.HasExited)running=true; }catch{} }
            foreach(string statusFile in statusFiles)ReadStatus(statusFile);
            int done=0,failed=0;double seconds=0;
            foreach(ConvertRow row in rows.Values){ if(row.Status=="ok"){done++;seconds+=row.TotalSeconds;} else if(row.Status=="fail")failed++; }
            progress.Value=Math.Min(progress.Maximum,rows.Count);
            status.Text="进度 "+rows.Count+" / "+totalPlanned+"　成功 "+done+"　失败 "+failed+"　累计单件 "+seconds.ToString("F1")+" 秒　已用 "+(DateTime.Now-startedAt).TotalSeconds.ToString("F1")+" 秒";
            if(!running){
                timer.Stop();
                Finish();
            }
        }
        void ReadStatus(string statusFile){
            try{
                using(var stream=new FileStream(statusFile,FileMode.Open,FileAccess.Read,FileShare.ReadWrite)){
                    long offset=offsets[statusFile];
                    if(offset>stream.Length)offset=0;
                    stream.Seek(offset,SeekOrigin.Begin);
                    using(var reader=new StreamReader(stream,Encoding.UTF8)){ string line; while((line=reader.ReadLine())!=null)HandleLine(line); }
                    offsets[statusFile]=stream.Position;
                }
            }catch{}
        }
        void HandleLine(string line){
            string[] parts=ConvertProtocol.Split(line);
            if(parts.Length==0)return;
            if(parts[0]==ConvertProtocol.End){
                ConvertRow row;
                if(ConvertProtocol.TryParseEnd(parts,out row)){
                    rows[row.Index]=row;
                    if(row.Status=="ok")AppendLog("完成 "+(row.TotalSeconds).ToString("F1")+" 秒（打开 "+row.OpenSeconds.ToString("F1")+" / 保存 "+row.SaveSeconds.ToString("F1")+"，"+row.Documents+" 个文档） "+Path.GetFileName(row.Source));
                    else if(row.Status=="skip")AppendLog("跳过（输出已存在） "+Path.GetFileName(row.Source));
                    else AppendLog("失败 "+Path.GetFileName(row.Source)+"："+row.Error);
                }
            }else if(parts[0]==ConvertProtocol.Ready){
                AppendLog("工作进程就绪（CAD 启动 "+(parts.Length>1?parts[1]:"?")+" 秒）。");
            }else if(parts[0]=="FATAL"){
                AppendLog("工作进程异常："+(parts.Length>1?parts[1]:""));
            }else if(parts[0]=="LOG"){
                AppendLog(parts.Length>1?parts[1]:"");
            }
        }
        void Finish(){
            var list=new List<ConvertRow>();
            foreach(ConvertItem item in items){
                ConvertRow row;
                if(rows.TryGetValue(item.Index,out row))list.Add(row);
                else{
                    var pending=new ConvertRow();
                    pending.Index=item.Index;pending.Source=item.Source;pending.Status="pending";pending.Target=item.Target;
                    list.Add(pending);
                }
            }
            double wall=(DateTime.Now-startedAt).TotalSeconds;
            var summary=ConvertSummary.From(list,wall);
            try{
                File.WriteAllText(reportPath,summary.ToCsv(list),new UTF8Encoding(true));
                AppendLog("报告已写入 "+reportPath);
            }catch(Exception e){ AppendLog("报告写入失败："+e.Message); }
            AppendLog(summary.Describe());
            status.Text=summary.Describe();
            start.Enabled=true;stop.Enabled=false;saveReport.Enabled=true;
            progress.Value=progress.Maximum;
            if(jobHandle!=IntPtr.Zero){ NativeJob.Close(jobHandle);jobHandle=IntPtr.Zero; }
        }
        void SaveReportAs(){
            using(var dialog=new SaveFileDialog()){
                dialog.Filter="CSV 报告|*.csv";dialog.FileName=Path.GetFileName(reportPath==null?"转换报告.csv":reportPath);
                if(dialog.ShowDialog(this)!=DialogResult.OK)return;
                try{ File.Copy(reportPath,dialog.FileName,true); MessageBox.Show(this,"报告已保存到\n"+dialog.FileName,"天工格式转换"); }
                catch(Exception e){ MessageBox.Show(this,"保存失败："+e.Message,"天工格式转换"); }
            }
        }
        void AppendLog(string message){
            if(InvokeRequired){ BeginInvoke((Action)delegate{ AppendLog(message); });return; }
            if(message==null)return;
            logBox.AppendText(DateTime.Now.ToString("HH:mm:ss")+"  "+message+Environment.NewLine);
        }
        protected override void OnFormClosing(FormClosingEventArgs e){
            if(timer.Enabled){
                if(MessageBox.Show(this,"转换仍在进行，确定要停止并退出吗？","天工格式转换",MessageBoxButtons.YesNo,MessageBoxIcon.Warning)!=DialogResult.Yes){ e.Cancel=true; return; }
                NativeJob.Terminate(jobHandle);
                foreach(Process process in processes){ try{ if(!process.HasExited)process.Kill(); }catch{} }
            }
            if(jobHandle!=IntPtr.Zero)NativeJob.Close(jobHandle);
            base.OnFormClosing(e);
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MEMORYSTATUSEX {
            public uint dwLength;public uint dwMemoryLoad;public ulong ullTotalPhys;public ulong ullAvailPhys;
            public ulong ullTotalPageFile;public ulong ullAvailPageFile;public ulong ullTotalVirtual;public ulong ullAvailVirtual;public ulong ullAvailExtendedVirtual;
        }
        [DllImport("kernel32.dll",SetLastError=true)]
        static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);
    }
    // Workers (and the CAD processes they start) are placed in a job object so "停止" can never leave
    // a hidden CAD process behind, even if the tool itself is killed.
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
}
