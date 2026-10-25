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
        readonly FlatButton start=(FlatButton)Ui.Primary("开始转换",220,44);
        readonly Button stop=Ui.Secondary("停止",72,38);
        readonly Button openOutput=new Button();
        readonly Button saveReport=Ui.Secondary("导出报告",96,38);
        readonly SlimProgress progress=new SlimProgress();
        readonly Label status=new Label();
        readonly TextBox logBox=new TextBox();
        readonly Timer timer=new Timer();
        readonly Label estimate=new Label();

        readonly List<ConvertItem> items=new List<ConvertItem>();
        // 用户加进来的文件 / 文件夹（列表扫描后显示的是扫到的文件，但扫描、转换永远从这里出发）。
        // 原来扫描后把列表换成文件清单、再拿它当输入：文件夹没了，重新扫描（勾了覆盖 / 校验时开始转换会自动重扫）
        // 每个文件的根目录变成它自己所在的文件夹 —— 镜像目录结构塌成一层，不同子文件夹里的同名文件互相覆盖
        readonly List<string> sources=new List<string>();
        bool scanned;   // 列表现在显示的是扫到的文件
        readonly Dictionary<int,ConvertRow> rows=new Dictionary<int,ConvertRow>();
        readonly List<Process> processes=new List<Process>();
        readonly List<string> statusFiles=new List<string>();
        readonly Dictionary<string,long> offsets=new Dictionary<string,long>();
        string workRoot;
        string reportPath;
        DateTime startedAt;
        // 本次转换的 worker 报上来的隐藏 CAD 进程号（READY 行第 3 列），和开始前就在跑的天工进程（用户的，绝不能杀）
        readonly HashSet<int> workerCads=new HashSet<int>();
        HashSet<int> cadsBefore=new HashSet<int>();
        int totalPlanned;
        IntPtr jobHandle=IntPtr.Zero;

        // 0.9.0「D · 侧栏向导」：① 输入（文件/文件夹 + 转哪几类）→ ② 输出设置 → ③ 转换（数字块 + 细进度条 + 日志）。
        // 「开始转换」在最后一步；转换中主按钮自己变成进度条（转换中 23 / 64）。
        readonly WizardShell shell=new WizardShell();
        readonly FlatButton next=(FlatButton)Ui.Primary("下一步",200,44);
        readonly FlatButton back=(FlatButton)Ui.Secondary("上一步",84,38);
        readonly StatTile statDone=new StatTile("完成"),statOk=new StatTile("成功"),statFail=new StatTile("失败"),statSkip=new StatTile("已存在跳过");
        readonly EmptyHint inputEmpty=new EmptyHint("还没有输入：点右上角「添加文件…」或「添加文件夹…」");

        public FormatConvertForm(){
            Ui.Shell(this,Product.Title("批量格式转换 SolidWorks / STEP"),1000,700,860,580);
            FormBorderStyle=FormBorderStyle.Sizable;
            StartPosition=FormStartPosition.CenterScreen;
            ShowInTaskbar=true;
            Controls.Add(shell);
            var pIn=shell.AddPage("输入","输入","SolidWorks / STEP 文件或文件夹");
            var pOut=shell.AddPage("输出设置","输出设置","");
            var pRun=shell.AddPage("转换","转换","");

            // ---- ① 输入 ----
            var addFiles=Ui.Small("添加文件…");addFiles.Click+=delegate{ AddFiles();RefreshRail(); };
            var addFolder=Ui.Small("添加文件夹…");addFolder.Click+=delegate{ AddFolder();RefreshRail(); };
            var remove=Ui.Small("移除选中");((FlatButton)remove).SetFore(Ui.Text2);remove.Click+=delegate{ RemoveSelected();RefreshRail(); };
            var clear=Ui.Small("清空");((FlatButton)clear).SetFore(Ui.Text2);clear.Click+=delegate{ if(timer.Enabled)return;sources.Clear();items.Clear();scanned=false;RefreshList();RefreshRail(); };
            shell.AddHeader(pIn,addFiles);shell.AddHeader(pIn,addFolder);shell.AddHeader(pIn,remove);shell.AddHeader(pIn,clear);
            var inStack=new VerticalStack{ Dock=DockStyle.Fill,Padding=new Padding(0),Gap=10,AutoScroll=false };
            pIn.Body.Controls.Add(inStack);
            var frame=Ui.Card("",0,"");frame.Padding=new Padding(6,8,6,8);
            inputs.Dock=DockStyle.Fill;inputs.SelectionMode=SelectionMode.MultiExtended;inputs.BorderStyle=BorderStyle.None;inputs.IntegralHeight=false;inputs.Font=Ui.F9;inputs.ItemHeight=24;
            inputEmpty.Dock=DockStyle.Top;
            frame.Controls.Add(inputs);frame.Controls.Add(inputEmpty);inputEmpty.BringToFront();
            var kinds=new FlowLayoutPanel{ Height=76,BackColor=Ui.Bg,FlowDirection=FlowDirection.LeftToRight,WrapContents=true,Padding=new Padding(0) };
            var kindsLabel=new Label{ Text="转哪几类",AutoSize=false,Width=76,Height=26,TextAlign=ContentAlignment.MiddleLeft,Font=Ui.F9B,ForeColor=Ui.Text2,BackColor=Ui.Bg };
            kinds.Controls.Add(kindsLabel);
            parts.Text="零件 .SLDPRT";parts.Checked=true;
            assemblies.Text="装配 .SLDASM";assemblies.Checked=true;
            drawings.Text="工程图 .SLDDRW";
            step.Text="STEP .stp/.step";step.Checked=true;
            recursive.Text="包含子文件夹";recursive.Checked=true;
            foreach(var c in new[]{ parts,assemblies,drawings,step,recursive }){ c.AutoSize=true;c.BackColor=Ui.Bg;c.Margin=new Padding(0,4,18,4);kinds.Controls.Add(c); c.CheckedChanged+=delegate{ Unscan(); }; }
            var scan=Ui.Secondary("扫描文件",100,34);scan.Click+=delegate{ ScanInputs();RefreshRail(); };scan.Margin=new Padding(0,0,0,0);
            kinds.Controls.Add(scan);
            inStack.Add(frame,true);inStack.Add(kinds);

            // ---- ② 输出设置 ----
            var outStack=new VerticalStack{ Dock=DockStyle.Fill,Padding=new Padding(0),Gap=14,AutoScroll=false };
            pOut.Body.Controls.Add(outStack);
            var outRow=new Panel{ Height=62,BackColor=Ui.Bg };
            var outLabel=new Label{ Text="输出目录",AutoSize=false,Font=Ui.F9B,ForeColor=Ui.Text2,BackColor=Ui.Bg,Bounds=new Rectangle(0,0,200,22) };
            output.BorderStyle=BorderStyle.FixedSingle;output.Font=Ui.F9;
            var browse=Ui.Secondary("浏览…",80,30);browse.Click+=delegate{ BrowseOutput();RefreshRail(); };
            var openDir=Ui.Ghost("打开");openDir.Click+=delegate{ OpenOutputFolder(); };
            outRow.Controls.AddRange(new Control[]{ outLabel,output,browse,openDir });
            outRow.Resize+=delegate{ int w=outRow.Width;output.SetBounds(0,28,Math.Max(80,w-180),28);browse.Location=new Point(output.Right+8,27);openDir.Location=new Point(browse.Right+6,27); };
            output.TextChanged+=delegate{ RefreshRail(); };
            openOutput.Visible=false;
            var opts=new FieldGrid{ BackColor=Ui.Bg,LabelWidth=84 };
            mirror.Text="镜像源目录结构（推荐，避免同名覆盖）";mirror.Checked=true;mirror.AutoSize=true;mirror.CheckedChanged+=delegate{ Unscan(); };
            force.Text="覆盖已存在的转换结果";force.AutoSize=true;
            verify.Text="转换后重新打开校验引用";verify.AutoSize=true;
            opts.Add("目录结构",mirror);
            opts.Add("已存在",force);
            opts.Add("校验",verify);
            workers.Minimum=1;workers.Maximum=8;workers.Value=2;workers.Width=64;workers.BorderStyle=BorderStyle.FixedSingle;workers.TextAlign=HorizontalAlignment.Right;
            autoWorkers.Text="自动（推荐：单进程）";autoWorkers.Checked=true;autoWorkers.AutoSize=true;
            autoWorkers.CheckedChanged+=delegate{ workers.Enabled=!autoWorkers.Checked; };workers.Enabled=false;
            opts.Add("并行进程",autoWorkers,workers);
            opts.Height=opts.PreferredHeight;
            estimate.AutoSize=false;estimate.ForeColor=Ui.Muted;estimate.Font=Ui.F8;estimate.BackColor=Ui.Bg;estimate.Height=40;
            estimate.Text="并行>1 为实验特性：实测仅快约 1.2 倍，且个别零件可能保存失败。\r\n输出格式：装配 .asm、零件 .par、钣金 .psm、工程图 .dft；转换由天工CAD自带的 SolidWorks / STEP 转换器完成。";
            outStack.Add(outRow);outStack.Add(opts);outStack.Add(estimate);

            // ---- ③ 转换 ----
            var runStack=new VerticalStack{ Dock=DockStyle.Fill,Padding=new Padding(0),Gap=12,AutoScroll=false };
            pRun.Body.Controls.Add(runStack);
            progress.Height=10;
            var tiles=new Panel{ Height=statDone.Height,BackColor=Ui.Bg };
            tiles.Controls.AddRange(new Control[]{ statDone,statOk,statFail,statSkip });
            tiles.Resize+=delegate{ int w=(tiles.Width-36)/4;int x=0;foreach(var t in new[]{ statDone,statOk,statFail,statSkip }){ t.SetBounds(x,0,w,tiles.Height);x+=w+12; } };
            var logFrame=Ui.Card("日志",0,"");logFrame.Padding=new Padding(12,36,6,8);
            logBox.Dock=DockStyle.Fill;logBox.Multiline=true;logBox.ScrollBars=ScrollBars.Vertical;logBox.ReadOnly=true;
            logBox.BorderStyle=BorderStyle.None;logBox.BackColor=Color.White;logBox.Font=Ui.F8;
            logFrame.Controls.Add(logBox);
            runStack.Add(progress);runStack.Add(tiles);runStack.Add(logFrame,true);

            // ---- 底部 ----
            start.Text="开始转换";start.Click+=delegate{ StartConversion(); };
            stop.Text="停止";stop.Enabled=false;stop.Click+=delegate{ StopConversion(); };((FlatButton)stop).SetFore(Ui.Bad);
            saveReport.Text="导出报告";saveReport.Enabled=false;saveReport.Click+=delegate{ SaveReportAs(); };
            back.Click+=delegate{ shell.Go(shell.Current-1); };
            next.Click+=delegate{ shell.Go(shell.Current+1); };
            shell.Footer.AddLeft(back);shell.Footer.AddLeft(stop);shell.Footer.AddLeft(saveReport);
            shell.Footer.AddRight(start);shell.Footer.AddRight(next);
            shell.Footer.Status=status;status.ForeColor=Ui.Muted;
            status.Text="就绪。";
            shell.PageChanged+=delegate{ UpdatePrimary(); };

            timer.Interval=500;timer.Tick+=delegate{ Poll(); };
            AcceptButton=null;
            RefreshRail();
        }
        public void GoPageForTest(int i){ shell.Go(i,false); }
        void UpdatePrimary(){
            int cur=shell.Current;
            bool running=timer.Enabled;
            back.Enabled=cur>0&&!running;
            bool showStart=running||cur==2;
            start.Visible=showStart;next.Visible=!showStart;
            if(!showStart)next.Text="下一步："+shell.Pages[cur+1].Title;
            shell.Footer.PerformLayout();
        }
        void RefreshRail(){
            int n=inputs.Items.Count;
            shell.Rail.Set(0,n==0?"文件或文件夹，可多选":n+" 项"+(items.Count>0?" · 扫到 "+items.Count+" 个文件":""),n>0,false);
            string o=output.Text.Trim();
            shell.Rail.Set(1,o.Length==0?"选输出目录":(mirror.Checked?"镜像目录 · ":"")+Path.GetFileName(o.TrimEnd('\\','/')),o.Length>0,false);
            inputEmpty.Visible=n==0;
            UpdatePrimary();
        }
        public void PrefillInput(string folder){
            if(folder==null||folder.Length==0||!Directory.Exists(folder))return;
            AddSource(folder);
            if(output.Text.Length==0)output.Text=Path.Combine(folder,"天工格式输出");
            RefreshRail();
        }

        void AddFiles(){
            using(var dialog=new OpenFileDialog()){
                dialog.Multiselect=true;dialog.Filter="CAD 文件|*.sldasm;*.SLDASM;*.sldprt;*.SLDPRT;*.slddrw;*.SLDDRW;*.stp;*.STP;*.step;*.STEP|SolidWorks|*.sldasm;*.SLDASM;*.sldprt;*.SLDPRT;*.slddrw;*.SLDDRW|STEP|*.stp;*.STP;*.step;*.STEP|所有文件|*.*";
                if(dialog.ShowDialog(this)!=DialogResult.OK)return;
                foreach(string file in dialog.FileNames)AddSource(file);
                if(output.Text.Length==0)output.Text=Path.GetDirectoryName(dialog.FileNames[0]);
            }
        }
        void AddFolder(){
            using(var dialog=new FolderBrowserDialog()){
                dialog.Description="选择包含 SolidWorks 文件的文件夹（可重复添加多个）";
                if(dialog.ShowDialog(this)!=DialogResult.OK)return;
                AddSource(dialog.SelectedPath);
                if(output.Text.Length==0)output.Text=Path.Combine(dialog.SelectedPath,"天工格式输出");
            }
        }
        void AddSource(string path){
            if(timer.Enabled)return;
            if(!sources.Contains(path,StringComparer.OrdinalIgnoreCase))sources.Add(path);
            Unscan();
        }
        // 输入、类型勾选、目录结构变了：上次扫到的文件清单作废，列表回到用户加的文件 / 文件夹
        void Unscan(){
            if(timer.Enabled)return;
            if(scanned){ items.Clear();scanned=false; }
            RefreshList();RefreshRail();
        }
        void RemoveSelected(){
            if(timer.Enabled)return;
            var chosen=new List<string>();
            foreach(object item in inputs.SelectedItems)chosen.Add(Convert.ToString(item));
            if(scanned){
                // 扫描后的列表：移掉的文件这次不转（原来只从列表里拿掉，开始转换照样转它）
                var drop=new HashSet<string>(chosen,StringComparer.OrdinalIgnoreCase);
                items.RemoveAll(i=>drop.Contains(i.Source));
                for(int i=0;i<items.Count;i++)items[i].Index=i;
                foreach(string c in chosen)inputs.Items.Remove(c);
            }else{
                foreach(string c in chosen){ sources.RemoveAll(x=>string.Equals(x,c,StringComparison.OrdinalIgnoreCase));inputs.Items.Remove(c); }
            }
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
        void RefreshList(){
            inputs.BeginUpdate();
            try{
                inputs.Items.Clear();
                if(scanned)foreach(ConvertItem item in items)inputs.Items.Add(item.Source);
                else foreach(string s in sources)inputs.Items.Add(s);
            }finally{ inputs.EndUpdate(); }
        }

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
            options.Inputs.AddRange(sources);
            options.Workers=EffectiveWorkers(options);
            return options;
        }
        int EffectiveWorkers(ConvertOptions options){
            if(!autoWorkers.Checked)return (int)workers.Value;
            return ConvertPlanner.SuggestedWorkers(Math.Max(1,items.Count>0?items.Count:sources.Count),Environment.ProcessorCount,(long)AvailableMemoryMb());
        }
        int RequestedWorkers(){ return autoWorkers.Checked?1:(int)workers.Value; }
        static ulong AvailableMemoryMb(){
            try{
                var status=new MEMORYSTATUSEX();status.dwLength=(uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
                if(GlobalMemoryStatusEx(ref status))return status.ullAvailPhys/(1024*1024);
            }catch{}
            return 2048;
        }
        void ScanInputs(){ ScanInputs(false); }
        void ScanInputs(bool keepRemovals){
            var options=BuildOptions();
            string problem=options.Validate();
            if(problem!=null&&problem.IndexOf("输出目录",StringComparison.Ordinal)<0){ MessageBox.Show(this,problem,"天工格式转换"); return; }
            // 扫描后列表里手动移掉的文件，重扫（勾了覆盖 / 校验时开始转换会自动重扫）也别再加回来
            HashSet<string> keep=null;
            if(scanned&&keepRemovals){ keep=new HashSet<string>(StringComparer.OrdinalIgnoreCase);foreach(var i in items)keep.Add(i.Source); }
            var fresh=ConvertPlanner.Scan(options);
            items.Clear();
            foreach(var i in fresh)if(keep==null||keep.Contains(i.Source))items.Add(i);
            for(int i=0;i<items.Count;i++)items[i].Index=i;
            scanned=true;
            RefreshList();
            long bytes=0;foreach(ConvertItem item in items)bytes+=item.Size;
            status.Text="已发现 "+items.Count+" 个要转换的文件（SolidWorks / STEP），合计 "+(bytes/1048576.0).ToString("F1")+" MB。";
            AppendLog(status.Text);
            if(items.Count==0)MessageBox.Show(this,"没有找到可转换的文件。请检查输入路径、扩展名勾选和“包含子文件夹”。","天工格式转换");
        }
        void StartConversion(){
            if(timer.Enabled)return;
            var options=BuildOptions();
            // 先建输出目录再校验：默认的输出目录（加文件夹时自动填的「…\天工格式输出」）本来就还不存在，
            // 原来先校验，点「开始转换」就报"输出目录不存在"，用户得自己去建一个
            if(options.OutputRoot.Length>0&&options.Inputs.Count>0){ try{ Directory.CreateDirectory(options.OutputRoot); }catch(Exception e){ MessageBox.Show(this,"无法创建输出目录："+e.Message,"天工格式转换"); return; } }
            string problem=options.Validate();
            if(problem!=null){ MessageBox.Show(this,problem,"天工格式转换"); return; }
            if(items.Count==0||force.Checked||verify.Checked)ScanInputs(true);
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
            workerCads.Clear();cadsBefore=WorkerChildren.Running();
            // 插件模式下当前进程是 TianGong.exe，Application.ExecutablePath 指向 CAD 本体，
            // 用它当 worker 会再拉起一个天工CAD（"找不到文件 --worker"）。必须解析转换器自身。
            string self=ConverterHost.Resolve();
            if(self==null)self=Application.ExecutablePath;
            jobHandle=NativeJob.Create();
            int started=0;
            for(int i=0;i<plan.Count;i++){
                if(plan[i].Count==0)continue;
                var builder=new StringBuilder();
                foreach(ConvertItem item in plan[i])builder.Append(item.Index).Append('\t').Append(item.Source).Append('\t').Append(item.Root).Append('\t').Append(i).AppendLine();
                string jobFile=Path.Combine(workRoot,"job-"+i+".txt");File.WriteAllText(jobFile,builder.ToString(),new UTF8Encoding(false));
                string statusFile=Path.Combine(workRoot,"status-"+i+".txt");File.WriteAllText(statusFile,"",new UTF8Encoding(false));
                var info=new ProcessStartInfo(self,CommandLine.Join("--worker",jobFile,statusFile,options.OutputRoot,options.FlatOutput?"1":"0",options.VerifyReopen?"1":"0",options.Force?"1":"0",workRoot));
                info.UseShellExecute=false;info.CreateNoWindow=true;info.WindowStyle=ProcessWindowStyle.Hidden;
                Process process;
                try{
                    process=Process.Start(info);
                }catch(Exception e){
                    // 以前这里异常会直接冒泡成"未处理的异常"，界面什么都不显示、看起来像点了没反应。
                    Log.Write("Convert","启动 worker 失败 self="+self+"："+e);
                    ConvertLog.Append(statusFile,ConvertProtocol.Line("FATAL","启动 worker 失败："+e.Message));
                    AppendLog("启动转换进程失败："+e.Message+"（转换器路径："+self+"）。");
                    continue;
                }
                NativeJob.Add(jobHandle,process);
                processes.Add(process);statusFiles.Add(statusFile);offsets[statusFile]=0;started++;
            }
            AppendLog("开始转换："+items.Count+" 个文件，"+started+" 个并行进程。输出目录 "+options.OutputRoot);
            AppendLog("并行进程数按 CPU "+(Environment.ProcessorCount)+" 核、可用内存 "+AvailableMemoryMb()+" MB 选择。");
            start.Enabled=false;stop.Enabled=true;saveReport.Enabled=false;
            progress.Maximum=totalPlanned;progress.Value=0;
            timer.Start();
            start.Text="转换中 0 / "+totalPlanned;start.Progress=0;
            shell.Go(2);UpdatePrimary();
        }
        void StopConversion(){
            AppendLog("正在停止……");
            foreach(string statusFile in statusFiles)ReadStatus(statusFile);   // 先把 worker 报上来的 CAD 进程号读全
            NativeJob.Terminate(jobHandle);
            foreach(Process process in processes){ try{ if(!process.HasExited)process.Kill(); }catch{} }
            WorkerChildren.Kill(startedAt,workerCads,cadsBefore,AppendLog);
        }
        void Poll(){
            bool running=false;
            foreach(Process process in processes){ try{ if(!process.HasExited)running=true; }catch{} }
            foreach(string statusFile in statusFiles)ReadStatus(statusFile);
            int done=0,failed=0;double seconds=0;
            foreach(ConvertRow row in rows.Values){ if(row.Status=="ok"){done++;seconds+=row.TotalSeconds;} else if(row.Status=="fail")failed++; }
            progress.Value=Math.Min(progress.Maximum,rows.Count);
            int skipped=0;foreach(ConvertRow row in rows.Values)if(row.Status=="skip")skipped++;
            statDone.Set(rows.Count+" / "+totalPlanned,Ui.Text);statOk.Set(done.ToString(),Ui.Ok);statFail.Set(failed.ToString(),failed>0?Ui.Bad:Ui.Text);statSkip.Set(skipped.ToString(),Ui.Text);
            if(timer.Enabled){ start.Text="转换中 "+rows.Count+" / "+totalPlanned;start.Progress=totalPlanned>0?(float)rows.Count/totalPlanned:0; }
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
                int pid;if(parts.Length>2&&int.TryParse(parts[2],out pid)&&pid>0)workerCads.Add(pid);
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
            start.Text="开始转换";start.Progress=-1;UpdatePrimary();
            shell.ShowToast(summary.Describe(),summary.Failed>0?Ui.Warn:Ui.Ok,"打开输出目录",delegate{ OpenOutputFolder(); });
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
                foreach(string statusFile in statusFiles)ReadStatus(statusFile);
                NativeJob.Terminate(jobHandle);
                foreach(Process process in processes){ try{ if(!process.HasExited)process.Kill(); }catch{} }
                WorkerChildren.Kill(startedAt,workerCads,cadsBefore,AppendLog);
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