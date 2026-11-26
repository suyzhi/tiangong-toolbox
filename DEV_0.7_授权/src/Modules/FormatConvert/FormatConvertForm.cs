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
        readonly Segmented outputMode=new Segmented("各自源文件夹（原地）","各自建子文件夹","统一输出到指定目录");
        readonly TextBox output=new TextBox();
        readonly TextBox subfolderBox=new TextBox();
        readonly ListBox outputPreview=new ListBox();
        readonly List<string> previewDirs=new List<string>();
        readonly List<string> lastRunOutputDirs=new List<string>();
        Panel outRow;
        Panel subRow;
        FieldGrid optsGrid;
        FieldGrid.Row mirrorRow;
        VerticalStack outStack;
        bool initializingMode;
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
        readonly CheckBox keepPool=new CheckBox();
        readonly FlatButton start=(FlatButton)Ui.Primary("开始转换",220,44);
        readonly Button stop=Ui.Secondary("停止",72,38);
        readonly Button saveReport=Ui.Secondary("导出报告",96,38);
        readonly Button releasePool=Ui.Secondary("释放驻留",104,38);
        readonly SlimProgress progress=new SlimProgress();
        readonly Label status=new Label();
        readonly TextBox logBox=new TextBox();
        readonly Timer timer=new Timer();
        readonly Timer poolWatch=new Timer();
        readonly Label estimate=new Label();
        readonly StringBuilder pendingLog=new StringBuilder();
        bool deferLog;

        readonly List<ConvertItem> items=new List<ConvertItem>();
        // 用户加进来的文件 / 文件夹（列表扫描后显示的是扫到的文件，但扫描、转换永远从这里出发）。
        // 原来扫描后把列表换成文件清单、再拿它当输入：文件夹没了，重新扫描（勾了覆盖 / 校验时开始转换会自动重扫）
        // 每个文件的根目录变成它自己所在的文件夹 —— 镜像目录结构塌成一层，不同子文件夹里的同名文件互相覆盖
        readonly List<string> sources=new List<string>();
        bool scanned;   // 列表现在显示的是扫到的文件
        readonly Dictionary<int,ConvertRow> rows=new Dictionary<int,ConvertRow>();
        readonly List<Process> processes=new List<Process>();
        readonly List<string> statusFiles=new List<string>();
        readonly HashSet<string> finishedStatusFiles=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string,long> offsets=new Dictionary<string,long>();
        readonly WarmConvertPool warmPool=new WarmConvertPool();
        string workRoot;
        string reportPath;
        DateTime startedAt;
        int totalPlanned;
        int preSkippedCount;

        // 0.9.0「D · 侧栏向导」：① 输入（文件/文件夹 + 转哪几类）→ ② 输出设置 → ③ 转换（数字块 + 细进度条 + 日志）。
        // 「开始转换」在最后一步；转换中主按钮自己变成进度条（转换中 23 / 64）。
        readonly WizardShell shell=new WizardShell();
        readonly FlatButton next=(FlatButton)Ui.Primary("下一步",200,44);
        readonly FlatButton back=(FlatButton)Ui.Secondary("上一步",84,38);
        readonly StatTile statDone=new StatTile("完成"),statOk=new StatTile("成功"),statFail=new StatTile("失败"),statSkip=new StatTile("已存在跳过");
        readonly EmptyHint inputEmpty=new EmptyHint("还没有输入：点右上角「添加文件…」「添加文件夹…」，或直接把文件 / 文件夹拖进窗口");
        readonly CadKeyShield keyShield;

        public FormatConvertForm(){
            Ui.Shell(this,Product.Title("批量格式转换 SolidWorks / STEP"),1000,700,860,580);
            FormBorderStyle=FormBorderStyle.Sizable;
            StartPosition=FormStartPosition.CenterScreen;
            ShowInTaskbar=true;
            AllowDrop=true;
            DragEnter+=OnFormDragEnter;
            DragDrop+=OnFormDragDrop;
            keyShield=new CadKeyShield(this,()=>timer.Enabled);
            WindowStateStore.Bind(this,"convert");
            Controls.Add(shell);
            var pIn=shell.AddPage("输入","输入","SolidWorks / STEP 文件或文件夹");
            var pOut=shell.AddPage("输出设置","输出设置","");
            var pRun=shell.AddPage("转换","转换","");

            // ---- ① 输入 ----
            var addFiles=Ui.Small("添加文件…");addFiles.Click+=delegate{ AddFiles();RefreshRail(); };
            var addFolder=Ui.Small("添加文件夹…");addFolder.Click+=delegate{ AddFolder();RefreshRail(); };
            var remove=Ui.Small("移除选中");((FlatButton)remove).SetFore(Ui.Text2);remove.Click+=delegate{ RemoveSelected();RefreshRail(); };
            var clear=Ui.Small("清空");((FlatButton)clear).SetFore(Ui.Text2);clear.Click+=delegate{ if(timer.Enabled)return;sources.Clear();items.Clear();lastRunOutputDirs.Clear();scanned=false;AutoOutput();RefreshList();RefreshRail(); };
            shell.AddHeader(pIn,addFiles);shell.AddHeader(pIn,addFolder);shell.AddHeader(pIn,remove);shell.AddHeader(pIn,clear);
            var inStack=new VerticalStack{ Dock=DockStyle.Fill,Padding=new Padding(0),Gap=Ui.S(10),AutoScroll=false };
            pIn.Body.Controls.Add(inStack);
            var frame=Ui.Card("",0,"");frame.Padding=Ui.SPad(6,8,6,8);
            inputs.Dock=DockStyle.Fill;inputs.SelectionMode=SelectionMode.MultiExtended;inputs.BorderStyle=BorderStyle.None;inputs.IntegralHeight=false;inputs.Font=Ui.F9;inputs.ItemHeight=Ui.S(26);
            Ui.EnableDoubleBuffer(inputs);
            inputEmpty.Dock=DockStyle.Top;
            frame.Controls.Add(inputs);frame.Controls.Add(inputEmpty);inputEmpty.BringToFront();
            var kinds=new FlowLayoutPanel{ Height=Ui.S(92),BackColor=Ui.Bg,FlowDirection=FlowDirection.LeftToRight,WrapContents=true,Padding=new Padding(0) };
            int klw=Math.Max(Ui.S(76),TextRenderer.MeasureText("转哪几类",Ui.F9B).Width+Ui.S(10));
            var kindsLabel=new Label{ Text="转哪几类",AutoSize=false,Width=klw,Height=Ui.S(28),TextAlign=ContentAlignment.MiddleLeft,Font=Ui.F9B,ForeColor=Ui.Text2,BackColor=Ui.Bg };
            kinds.Controls.Add(kindsLabel);
            parts.Text="零件 .SLDPRT";parts.Checked=true;
            assemblies.Text="装配 .SLDASM";assemblies.Checked=true;
            drawings.Text="工程图 .SLDDRW（天工 2025 打不开，一般不勾）";   // 0.10.2 真机：宝马托盘里试了 2 张（一张零件图、一张装配图），一打开都是 E_FAIL
            step.Text="STEP .stp/.step";step.Checked=true;
            recursive.Text="包含子文件夹";recursive.Checked=true;
            foreach(var c in new[]{ parts,assemblies,drawings,step,recursive }){ c.AutoSize=true;c.BackColor=Ui.Bg;c.Margin=Ui.SPad(0,4,16,4);kinds.Controls.Add(c); c.CheckedChanged+=delegate{ Unscan(); }; }
            var scan=Ui.Secondary("扫描文件",100,34);scan.Click+=delegate{ ScanInputs();RefreshRail(); };scan.Margin=Ui.SPad(0,2,0,2);
            kinds.Controls.Add(scan);
            kinds.MinimumSize=new Size(0,Ui.S(78));
            kinds.Tag="autoheight";
            inStack.Add(frame,true);inStack.Add(kinds);

            // ---- ② 输出设置 ----
            outStack=new VerticalStack{ Dock=DockStyle.Fill,Padding=new Padding(0),Gap=Ui.S(12),AutoScroll=true };
            pOut.Body.Controls.Add(outStack);
            int labelW=Ui.S(84);
            var modeRow=new Panel{ Height=Ui.S(38),BackColor=Ui.Bg };
            var modeLabel=new Label{ Text="输出位置",AutoSize=false,Font=Ui.F9B,ForeColor=Ui.Text2,BackColor=Ui.Bg,TextAlign=ContentAlignment.MiddleLeft,Bounds=new Rectangle(0,Ui.S(2),labelW,Ui.S(34)) };
            outputMode.Height=Ui.S(34);
            outputMode.Width=outputMode.PreferredWidth;
            modeRow.Controls.AddRange(new Control[]{ modeLabel,outputMode });
            modeRow.Resize+=delegate{
                int avail=Math.Max(Ui.S(220),modeRow.Width-labelW);
                outputMode.SetBounds(labelW,Ui.S(2),Math.Min(outputMode.PreferredWidth,avail),Ui.S(34));
            };

            outRow=new Panel{ Height=Ui.S(36),BackColor=Ui.Bg };
            var outLabel=new Label{ Text="输出目录",AutoSize=false,Font=Ui.F9B,ForeColor=Ui.Text2,BackColor=Ui.Bg,TextAlign=ContentAlignment.MiddleLeft,Bounds=new Rectangle(0,Ui.S(3),labelW,Ui.S(30)) };
            output.BorderStyle=BorderStyle.FixedSingle;output.Font=Ui.F9;
            var browse=Ui.Secondary("浏览…",80,30);browse.Click+=delegate{ BrowseOutput();RefreshRail(); };
            var openDir=Ui.Ghost("打开");openDir.Click+=delegate{ OpenOutputFolder(); };
            outRow.Controls.AddRange(new Control[]{ outLabel,output,browse,openDir });
            outRow.Resize+=delegate{
                int w=outRow.Width, btns=browse.Width+openDir.Width+Ui.S(16);
                output.SetBounds(labelW,Ui.S(3),Math.Max(Ui.S(80),w-labelW-btns),Ui.S(30));
                browse.Location=new Point(output.Right+Ui.S(8),Ui.S(2));
                openDir.Location=new Point(browse.Right+Ui.S(6),Ui.S(2));
            };
            output.TextChanged+=delegate{ if(!settingOutput)outputAuto=output.Text.Trim().Length==0; UpdateEstimate(); RefreshRail(); };

            subRow=new Panel{ Height=Ui.S(36),BackColor=Ui.Bg };
            var subLabel=new Label{ Text="子文件夹",AutoSize=false,Font=Ui.F9B,ForeColor=Ui.Text2,BackColor=Ui.Bg,TextAlign=ContentAlignment.MiddleLeft,Bounds=new Rectangle(0,Ui.S(3),labelW,Ui.S(30)) };
            subfolderBox.BorderStyle=BorderStyle.FixedSingle;subfolderBox.Font=Ui.F9;subfolderBox.Text=ConvertOptions.DefaultOutputSubfolder;
            var subHint=new Label{ Text="建在每个输入文件夹内（源目录 0 残留）",AutoSize=true,Font=Ui.F8,ForeColor=Ui.Muted,BackColor=Ui.Bg };
            subRow.Controls.AddRange(new Control[]{ subLabel,subfolderBox,subHint });
            subRow.Resize+=delegate{
                int boxW=Math.Min(Ui.S(220),Math.Max(Ui.S(100),subRow.Width-labelW-Ui.S(12)));
                subfolderBox.SetBounds(labelW,Ui.S(3),boxW,Ui.S(30));
                subHint.Location=new Point(subfolderBox.Right+Ui.S(10),Math.Max(0,(subRow.Height-subHint.Height)/2));
            };
            subfolderBox.TextChanged+=delegate{
                if(initializingMode)return;
                string val=subfolderBox.Text.Trim();
                if(ConvertOptions.IsValidSubfolderName(val))Choices.Set("convert.outputSubfolder",val);
                Unscan();
            };

            optsGrid=new FieldGrid{ BackColor=Ui.Bg,LabelWidth=labelW,Tag="autoheight" };
            mirror.Text="镜像源目录结构（多文件夹自动按站位分目录，避免同名覆盖）";mirror.Checked=true;mirror.AutoSize=true;mirror.CheckedChanged+=delegate{ Unscan(); UpdateEstimate(); };
            force.Text="覆盖已存在的转换结果";force.AutoSize=true;
            verify.Text="转换后重新打开校验引用";verify.AutoSize=true;
            mirrorRow=optsGrid.Add("目录结构",mirror);
            optsGrid.Add("已存在",force);
            optsGrid.Add("校验",verify);
            workers.Minimum=1;workers.Maximum=8;workers.Value=Math.Max(1,Math.Min(8,ConvertPlanner.SuggestedWorkers(8,Environment.ProcessorCount,(long)AvailableMemoryMb())));workers.Width=Ui.S(72);workers.BorderStyle=BorderStyle.FixedSingle;workers.TextAlign=HorizontalAlignment.Right;
            autoWorkers.Text="自动（智能并发，推荐）";autoWorkers.Checked=true;autoWorkers.AutoSize=true;
            autoWorkers.CheckedChanged+=delegate{ workers.Enabled=!autoWorkers.Checked; UpdateEstimate(); };
            workers.ValueChanged+=delegate{ UpdateEstimate(); };
            workers.Enabled=false;
            optsGrid.Add("并行进程",autoWorkers,workers);
            keepPool.Text="热驻留进程池（连续转换秒启动，空闲 5 分钟自动释放）";keepPool.Checked=true;keepPool.AutoSize=true;
            keepPool.CheckedChanged+=delegate{ if(!keepPool.Checked&&!timer.Enabled&&warmPool.AliveCount>0){ ReleaseWarmPool(); } UpdateEstimate(); };
            optsGrid.Add("热池驻留",keepPool);
            optsGrid.Height=optsGrid.PreferredHeight;

            var previewCard=Ui.Card("输出位置预览（双击打开目录）",0,"");
            previewCard.Padding=Ui.SPad(10,36,8,8);
            previewCard.MinimumSize=new Size(0,Ui.S(86));
            outputPreview.Dock=DockStyle.Fill;outputPreview.BorderStyle=BorderStyle.None;outputPreview.IntegralHeight=false;
            outputPreview.Font=Ui.F8;outputPreview.ForeColor=Ui.Text2;outputPreview.BackColor=Color.White;outputPreview.ItemHeight=Ui.S(22);
            Ui.EnableDoubleBuffer(outputPreview);
            outputPreview.DoubleClick+=delegate{ OpenPreviewSelection(); };
            previewCard.Controls.Add(outputPreview);

            estimate.AutoSize=false;estimate.ForeColor=Ui.Muted;estimate.Font=Ui.F8;estimate.BackColor=Ui.Bg;estimate.Height=Ui.S(68);
            estimate.MinimumSize=new Size(0,Ui.S(54));estimate.Tag="autoheight";
            outStack.Add(modeRow);outStack.Add(outRow);outStack.Add(subRow);outStack.Add(optsGrid);outStack.Add(previewCard,true);outStack.Add(estimate);

            outputMode.SelectedIndexChanged+=delegate{
                if(initializingMode)return;
                Choices.Set("convert.outputMode",ConvertOptions.FormatOutputMode(CurrentOutputMode()));
                ApplyOutputModeUi();
                Unscan();
            };

            // ---- ③ 转换 ----
            var runStack=new VerticalStack{ Dock=DockStyle.Fill,Padding=new Padding(0),Gap=Ui.S(12),AutoScroll=false };
            pRun.Body.Controls.Add(runStack);
            progress.Height=Ui.S(10);
            var tiles=new Panel{ Height=statDone.Height,BackColor=Ui.Bg };
            tiles.Controls.AddRange(new Control[]{ statDone,statOk,statFail,statSkip });
            tiles.Resize+=delegate{ int g=Ui.S(12);int w=(tiles.Width-g*3)/4;int x=0;foreach(var t in new[]{ statDone,statOk,statFail,statSkip }){ t.SetBounds(x,0,w,tiles.Height);x+=w+g; } };
            var logFrame=Ui.Card("日志",0,"");logFrame.Padding=Ui.SPad(12,38,6,8);
            logBox.Dock=DockStyle.Fill;logBox.Multiline=true;logBox.ScrollBars=ScrollBars.Vertical;logBox.ReadOnly=true;
            logBox.BorderStyle=BorderStyle.None;logBox.BackColor=Color.White;logBox.Font=Ui.F8;
            logFrame.Controls.Add(logBox);
            runStack.Add(progress);runStack.Add(tiles);runStack.Add(logFrame,true);

            // ---- 底部 ----
            start.Text="开始转换";start.Click+=delegate{ StartConversion(); };
            stop.Text="停止";stop.Enabled=false;stop.Click+=delegate{ StopConversion(); };((FlatButton)stop).SetFore(Ui.Bad);
            saveReport.Text="导出报告";saveReport.Enabled=false;saveReport.Click+=delegate{ SaveReportAs(); };
            releasePool.Text="释放驻留";releasePool.Visible=false;releasePool.Click+=delegate{ ReleaseWarmPool(); };
            back.Click+=delegate{ shell.Go(shell.Current-1); };
            next.Click+=delegate{ shell.Go(shell.Current+1); };
            shell.Footer.AddLeft(back);shell.Footer.AddLeft(stop);shell.Footer.AddLeft(saveReport);shell.Footer.AddLeft(releasePool);
            shell.Footer.AddRight(start);shell.Footer.AddRight(next);
            shell.Footer.Status=status;status.ForeColor=Ui.Muted;
            status.Text="就绪。";
            shell.PageChanged+=delegate{ UpdatePrimary(); };

            timer.Interval=500;timer.Tick+=delegate{ Poll(); };
            poolWatch.Interval=5000;poolWatch.Tick+=delegate{ if(!timer.Enabled)RefreshPoolUi(); };
            poolWatch.Start();
            AcceptButton=null;
            // 记住上次的勾选（0.10.2）：原来每次打开都回到默认，转工程图、不镜像的人每次重新勾
            Choices.Bind(parts,"convert.parts");Choices.Bind(assemblies,"convert.assemblies");Choices.Bind(drawings,"convert.drawings");Choices.Bind(step,"convert.step");
            Choices.Bind(recursive,"convert.recursive");Choices.Bind(mirror,"convert.mirror");Choices.Bind(verify,"convert.verify");
            // 「覆盖已存在的转换结果」不记（0.10.3）：转出来的 .par / .asm 常常接着在天工里改（打孔、改属性）。
            // 记住的话，哪天往同一个输出文件夹补转几个新文件，前面改过的全被重新转换覆盖掉、一声不响。每次要覆盖就当场勾。
            Choices.Bind(autoWorkers,"convert.autoWorkers");
            Choices.Bind(keepPool,"convert.keepPool");
            // 0.13.10：默认「各自源文件夹（原地）」（方案 B），多站位自动转换到各自文件夹内；记住用户的模式切换。
            initializingMode=true;
            try{
                ConvertOutputMode savedMode=ConvertOptions.ParseOutputMode(Choices.Get("convert.outputMode","inplace"),ConvertOutputMode.InPlace);
                outputMode.SelectedIndex=ModeToIndex(savedMode);
                string savedSub=Choices.Get("convert.outputSubfolder",ConvertOptions.DefaultOutputSubfolder);
                subfolderBox.Text=ConvertOptions.IsValidSubfolderName(savedSub)?savedSub.Trim():ConvertOptions.DefaultOutputSubfolder;
            }finally{ initializingMode=false; }
            ApplyOutputModeUi();
            RefreshPoolUi();
        }
        static int ModeToIndex(ConvertOutputMode mode){
            if(mode==ConvertOutputMode.PerInputSubfolder)return 1;
            if(mode==ConvertOutputMode.SingleRoot)return 2;
            return 0;
        }
        ConvertOutputMode CurrentOutputMode(){
            if(outputMode.SelectedIndex==1)return ConvertOutputMode.PerInputSubfolder;
            if(outputMode.SelectedIndex==2)return ConvertOutputMode.SingleRoot;
            return ConvertOutputMode.InPlace;
        }
        void ApplyOutputModeUi(){
            ConvertOutputMode mode=CurrentOutputMode();
            if(outRow!=null)outRow.Visible=mode==ConvertOutputMode.SingleRoot;
            if(subRow!=null)subRow.Visible=mode==ConvertOutputMode.PerInputSubfolder;
            if(optsGrid!=null&&mirrorRow!=null){
                optsGrid.Show(mirrorRow,mode!=ConvertOutputMode.InPlace);
                optsGrid.Height=optsGrid.PreferredHeight;
            }
            if(outStack!=null)outStack.PerformLayout();
            UpdateEstimate();
            RefreshRail();
        }
        void RefreshOutputPreview(){
            if(outputPreview==null)return;
            var options=BuildOptions();
            var map=ConvertPlanner.ResolveOutputRoots(options);
            outputPreview.BeginUpdate();
            try{
                outputPreview.Items.Clear();
                previewDirs.Clear();
                if(sources.Count==0){
                    if(options.OutputMode==ConvertOutputMode.InPlace)
                        outputPreview.Items.Add("请先在第 1 步添加站位文件夹或文件；多个站位将各自直接输出到各自源文件夹内。");
                    else if(options.OutputMode==ConvertOutputMode.PerInputSubfolder)
                        outputPreview.Items.Add("请先在第 1 步添加站位文件夹或文件；多个站位将各自输出到「各自源文件夹\\"+options.EffectiveSubfolder()+"」。");
                    else
                        outputPreview.Items.Add("请先在第 1 步添加站位文件夹或文件并选择输出目录；多站位勾选镜像时自动按站位名分目录。");
                    return;
                }
                if(options.OutputMode==ConvertOutputMode.SingleRoot&&string.IsNullOrWhiteSpace(options.OutputRoot)){
                    outputPreview.Items.Add("请在上方选择统一输出目录。");
                    return;
                }
                if(options.OutputMode==ConvertOutputMode.PerInputSubfolder&&!ConvertOptions.IsValidSubfolderName(options.OutputSubfolder)){
                    outputPreview.Items.Add("子文件夹名称不合法，请修改（不能包含 \\ / : * ? \" < > |）。");
                    return;
                }
                foreach(var kv in map){
                    string srcRoot=kv.Key;
                    string dstRoot=kv.Value;
                    string line;
                    if(options.OutputMode==ConvertOutputMode.InPlace){
                        line=srcRoot+"  →  原地输出（直接保存在此文件夹）";
                    }else if(options.OutputMode==ConvertOutputMode.SingleRoot&&options.FlatOutput){
                        line=srcRoot+"  →  "+dstRoot+"（平铺）";
                    }else{
                        line=srcRoot+"  →  "+dstRoot;
                    }
                    outputPreview.Items.Add(line);
                    previewDirs.Add(dstRoot);
                }
            }finally{ outputPreview.EndUpdate(); }
        }
        void OpenPreviewSelection(){
            int idx=outputPreview.SelectedIndex;
            if(idx<0||idx>=previewDirs.Count)return;
            OpenFolderSafe(previewDirs[idx],false);
        }
        public void GoPageForTest(int i){ shell.Go(i,false); }
#if TG_DEV_BUILD
        internal ConvertOutputMode TestOutputMode { get{ return CurrentOutputMode(); } }
        internal void TestSetOutputMode(ConvertOutputMode mode){ outputMode.SelectedIndex=ModeToIndex(mode); }
        internal void TestSetOutputRoot(string dir){ output.Text=dir??""; }
        internal void TestSetSubfolder(string name){ subfolderBox.Text=name??""; }
        internal void TestAddSources(IEnumerable<string> paths){ AddSources(paths); }
        internal List<string> TestPreviewDirs(){ return new List<string>(previewDirs); }
        internal List<string> TestPreviewLines(){
            var list=new List<string>();
            foreach(object item in outputPreview.Items)list.Add(Convert.ToString(item));
            return list;
        }
        internal ConvertOptions TestBuildOptions(){ return BuildOptions(); }
#endif
        void UpdatePrimary(){
            int cur=shell.Current;
            bool running=timer.Enabled;
            back.Enabled=cur>0&&!running;
            bool showStart=running||cur==2;
            start.Visible=showStart;next.Visible=!showStart;
            if(!showStart){ next.Text="下一步："+shell.Pages[cur+1].Title; Ui.FitButton(next); }
            shell.Footer.PerformLayout();
        }
        void RefreshRail(){
            int n=inputs.Items.Count;
            shell.Rail.Set(0,n==0?"文件或文件夹，可多选":n+" 项"+(items.Count>0?" · 扫到 "+items.Count+" 个文件":""),n>0,false);
            var options=BuildOptions();
            var dirs=ConvertPlanner.ResolveDistinctOutputDirs(options);
            if(options.OutputMode==ConvertOutputMode.InPlace){
                shell.Rail.Set(1,dirs.Count>1?("各自源文件夹 · "+dirs.Count+" 处"):"各自源文件夹（原地）",true,false);
            }else if(options.OutputMode==ConvertOutputMode.PerInputSubfolder){
                bool ok=options.HasOutputTarget();
                shell.Rail.Set(1,ok?("各自子文件夹 · "+options.EffectiveSubfolder()):"请填合法子文件夹名",ok,false);
            }else{
                string o=output.Text.Trim();
                string prefix=mirror.Checked?(dirs.Count>1?"按站位分目录 · ":"镜像目录 · "):"";
                shell.Rail.Set(1,o.Length==0?"选输出目录":(prefix+ConvertPlanner.FolderDisplayName(o)),o.Length>0,false);
            }
            RefreshOutputPreview();
            inputEmpty.Visible=n==0;
            UpdatePrimary();
        }
        // 从天工里点「批量格式转换」时带进来的是当前文档所在的文件夹。0.10.3：只有那里真有 SolidWorks / STEP 文件才填。
        // 原来天工里开着自己的装配（一个天工项目文件夹）点进来，这个文件夹照样成了输入、输出目录也建在它里面；
        // 用户再「添加文件夹」加 SolidWorks 项目时输出目录不跟着变，转出来的东西全写进了自己正在做的天工项目里
        public void PrefillInput(string folder){
            if(folder==null||folder.Length==0||!Directory.Exists(folder))return;
            if(!HasConvertible(folder))return;
            AddSource(folder);
            RefreshRail();
        }
        static bool HasConvertible(string folder){
            int seen=0;
            try{ foreach(string f in Directory.EnumerateFiles(folder)){ if(ConvertPlanner.IsSupported(f))return true; if(++seen>5000)return false; } }catch{}
            // 往下看一层：SolidWorks 项目常把零件、装配体分两个子文件夹放
            try{ foreach(string d in Directory.EnumerateDirectories(folder))foreach(string f in Directory.EnumerateFiles(d)){ if(ConvertPlanner.IsSupported(f))return true; if(++seen>5000)return false; } }catch{}
            return false;
        }
        // 输出目录没被用户改过时跟着第一个输入走（「第一个输入\天工格式输出」）：原来只在输出框为空时填一次，
        // 移掉 / 清空输入再加别的文件夹，输出还指着上一个文件夹
        bool outputAuto=true,settingOutput;
        void AutoOutput(){
            if(!outputAuto)return;
            string first=sources.Count==0?null:sources[0];
            string dir=first==null?"":(File.Exists(first)?Path.GetDirectoryName(first):first);
            settingOutput=true;
            try{ output.Text=dir.Length==0?"":Path.Combine(dir,"天工格式输出"); }finally{ settingOutput=false; }
        }

        void AddFiles(){
            using(var dialog=new OpenFileDialog()){
                dialog.Multiselect=true;dialog.Filter="CAD 文件|*.sldasm;*.SLDASM;*.sldprt;*.SLDPRT;*.slddrw;*.SLDDRW;*.stp;*.STP;*.step;*.STEP|SolidWorks|*.sldasm;*.SLDASM;*.sldprt;*.SLDPRT;*.slddrw;*.SLDDRW|STEP|*.stp;*.STP;*.step;*.STEP|所有文件|*.*";
                if(dialog.ShowDialog(this)!=DialogResult.OK)return;
                AddSources(dialog.FileNames);   // 批量加入只刷新一次列表和侧栏，避免多选文件 O(N²) 卡顿
            }
        }
        void AddFolder(){
            string init=sources.Count>0?sources[sources.Count-1]:"";
            string[] dirs=FolderPicker.PickMultiple(this,"选择包含 SolidWorks / STEP 文件的文件夹（可按住 Ctrl 多选多个站位）",init,"convert.input");
            if(dirs!=null&&dirs.Length>0)AddSources(dirs);
        }
        void OnFormDragEnter(object sender,DragEventArgs e){
            if(!timer.Enabled&&e.Data!=null&&e.Data.GetDataPresent(DataFormats.FileDrop))e.Effect=DragDropEffects.Copy;
            else e.Effect=DragDropEffects.None;
        }
        void OnFormDragDrop(object sender,DragEventArgs e){
            if(timer.Enabled||e.Data==null||!e.Data.GetDataPresent(DataFormats.FileDrop))return;
            var dropped=e.Data.GetData(DataFormats.FileDrop) as string[];
            if(dropped==null||dropped.Length==0)return;
            var accepted=new List<string>(dropped.Length);
            int skipped=0;
            foreach(string raw in dropped){
                if(string.IsNullOrEmpty(raw))continue;
                if(Directory.Exists(raw))accepted.Add(raw);
                else if(File.Exists(raw)&&ConvertPlanner.IsSupported(raw))accepted.Add(raw);
                else skipped++;
            }
            if(accepted.Count>0){
                AddSources(accepted);
                shell.Go(0,false);
                RefreshRail();
                status.Text="已拖入 "+accepted.Count+" 项"+(skipped>0?"（跳过 "+skipped+" 个非 SolidWorks / STEP 文件）":"")+"。";
            }else if(skipped>0){
                status.Text="拖入的文件里没有 SolidWorks（.sldprt/.sldasm/.slddrw）或 STEP（.stp/.step）文件。";
            }
        }
        void AddSource(string path){
            AddSources(new[]{ path });
        }
        void AddSources(IEnumerable<string> paths){
            if(timer.Enabled||paths==null)return;
            var existing=new HashSet<string>(sources,StringComparer.OrdinalIgnoreCase);
            bool changed=false;
            foreach(string path in paths){
                if(!string.IsNullOrEmpty(path)&&existing.Add(path)){ sources.Add(path); changed=true; }
            }
            if(!changed)return;
            lastRunOutputDirs.Clear();
            AutoOutput();
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
                lastRunOutputDirs.Clear();
                AutoOutput();
            }
        }
        void BrowseOutput(){
            string dir=FolderPicker.Pick(this,"选择转换结果的输出目录",output.Text,"convert.output");
            if(!string.IsNullOrEmpty(dir))output.Text=dir;
        }
        void OpenOutputFolder(){
            var options=BuildOptions();
            string path=null;
            bool createIfMissing=false;
            if(options.OutputMode==ConvertOutputMode.SingleRoot&&!string.IsNullOrWhiteSpace(options.OutputRoot)){
                path=options.OutputRoot;
                createIfMissing=true;
            }else{
                var dirs=lastRunOutputDirs.Count>0?lastRunOutputDirs:ConvertPlanner.ResolveDistinctOutputDirs(options);
                if(dirs.Count==1){
                    path=dirs[0];
                    createIfMissing=lastRunOutputDirs.Count>0;
                }else if(dirs.Count>1){
                    string common=ConvertPlanner.CommonParentDirectory(dirs);
                    path=(!string.IsNullOrEmpty(common)&&Directory.Exists(common))?common:dirs[0];
                }
            }
            if(string.IsNullOrEmpty(path)){
                MessageBox.Show(this,"还没有可打开的输出目录，请先添加输入文件夹。","天工格式转换");
                return;
            }
            OpenFolderSafe(path,createIfMissing);
        }
        void OpenFolderSafe(string path,bool createIfMissing){
            try{
                if(string.IsNullOrEmpty(path))return;
                string target=path;
                if(createIfMissing){
                    Directory.CreateDirectory(target);
                }else if(!Directory.Exists(target)){
                    target=FolderPicker.ResolveExistingDir(target);
                }
                // 输出目录选的是盘符根（D:\）时原来拼成 "D:\""，\" 被当成引号，打开的是「文档」
                if(!string.IsNullOrEmpty(target)&&Directory.Exists(target)){
                    Process.Start("explorer.exe",CommandLine.Quote(target));
                }
            }catch(Exception e){ MessageBox.Show(this,"无法打开目录："+e.Message,"天工格式转换"); }
        }
        void RefreshList(){
            inputs.BeginUpdate();
            try{
                inputs.Items.Clear();
                if(scanned){
                    if(items.Count>0){
                        var arr=new object[items.Count];
                        for(int i=0;i<items.Count;i++)arr[i]=items[i].Source;
                        inputs.Items.AddRange(arr);
                    }
                }else if(sources.Count>0){
                    var arr=new object[sources.Count];
                    for(int i=0;i<sources.Count;i++)arr[i]=sources[i];
                    inputs.Items.AddRange(arr);
                }
            }finally{ inputs.EndUpdate(); }
        }

        ConvertOptions BuildOptions(){
            var options=new ConvertOptions();
            options.OutputMode=CurrentOutputMode();
            options.OutputRoot=options.OutputMode==ConvertOutputMode.SingleRoot?output.Text.Trim():"";
            options.OutputSubfolder=subfolderBox.Text.Trim();
            options.Recursive=recursive.Checked;
            options.FlatOutput=options.OutputMode!=ConvertOutputMode.InPlace&&!mirror.Checked;
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
            if(items.Count>0)return ConvertPlanner.SuggestedWorkers(items,Environment.ProcessorCount,(long)AvailableMemoryMb(),options);
            return ConvertPlanner.SuggestedWorkers(Math.Max(1,sources.Count),Environment.ProcessorCount,(long)AvailableMemoryMb());
        }
        void UpdateEstimate(){
            int cores=Environment.ProcessorCount;
            long mem=(long)AvailableMemoryMb();
            var previewOpts=BuildOptions();
            int suggested=items.Count>0
                ?ConvertPlanner.SuggestedWorkers(items,cores,mem,previewOpts)
                :ConvertPlanner.SuggestedWorkers(8,cores,mem);
            string mode=autoWorkers.Checked
                ?("当前模式：自动并发（按本机 "+cores+" 核 / 可用内存 "+mem+" MB，多模型自动建议 "+suggested+" 进程；单文件或小批次自动降为 1 进程）。")
                :("当前模式：手动指定 "+(int)workers.Value+" 个并行进程（并行预拉起 + 动态任务队列均衡，自动不超过实际待转文件数）。");
            string poolNote=keepPool.Checked?"热驻留已开（连续批次 0 秒启动，空闲 5 分钟自动释放）":"热驻留已关（每批结束立即释放隐藏 CAD）";
            string outNote=previewOpts.OutputMode==ConvertOutputMode.InPlace
                ?"原地输出：多站位各自在源目录内转换（同目录装配自动串行防锁冲突，不同站位可并行）"
                :(previewOpts.OutputMode==ConvertOutputMode.PerInputSubfolder
                    ?"独立子目录输出：各站位分别写入各自「"+previewOpts.EffectiveSubfolder()+"」并保持源目录 0 残留"
                    :"统一目录输出：源目录 0 残留自动清理，多站位勾选镜像时自动按站位分目录");
            estimate.Text=mode+"\r\n"+outNote+"；"+poolNote+"。\r\n输出格式：装配 .asm、零件 .par、钣金 .psm、工程图 .dft；由天工CAD自带转换器完成。";
        }
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
            if(problem!=null&&problem.IndexOf("输出目录",StringComparison.Ordinal)<0&&problem.IndexOf("子文件夹",StringComparison.Ordinal)<0){ MessageBox.Show(this,problem,"天工格式转换"); return; }
            // 扫描后列表里手动移掉的文件，重扫（勾了覆盖 / 校验时开始转换会自动重扫）也别再加回来
            HashSet<string> keep=null;
            if(scanned&&keepRemovals){ keep=new HashSet<string>(StringComparer.OrdinalIgnoreCase);foreach(var i in items)keep.Add(i.Source); }
            // 后台扫（0.10.3）：加了一个大文件夹（宝马托盘资料整理：5 万个 SolidWorks 文件）时，原来在界面线程上扫，
            // 天工（插件里打开时）和转换窗口一起"未响应"半分钟以上。扫的时候窗口禁用、照常刷新
            List<ConvertItem> fresh=null;Exception scanError=null;
            var worker=new System.Threading.Thread(()=>{ try{ fresh=ConvertPlanner.Scan(options); }catch(Exception e){ scanError=e; } });
            worker.IsBackground=true;worker.Start();
            if(!worker.Join(300)){
                bool was=Enabled;Enabled=false;Cursor=Cursors.WaitCursor;
                status.Text="正在扫描文件夹…";status.Refresh();
                try{ while(!worker.Join(16)){ try{ Application.DoEvents(); }catch{} } }
                finally{ Enabled=was;Cursor=Cursors.Default; }
            }
            if(scanError!=null){ Log.Write("ConvertScan",scanError); MessageBox.Show(this,"扫描输入失败："+scanError.Message,"天工格式转换"); return; }
            items.Clear();
            foreach(var i in fresh)if(keep==null||keep.Contains(i.Source))items.Add(i);
            for(int i=0;i<items.Count;i++)items[i].Index=i;
            scanned=true;
            RefreshList();
            UpdateEstimate();
            long bytes=0;foreach(ConvertItem item in items)bytes+=item.Size;
            status.Text="已发现 "+items.Count+" 个要转换的文件（SolidWorks / STEP），合计 "+(bytes/1048576.0).ToString("F1")+" MB。";
            AppendLog(status.Text);
            // 路径太长（0.10.3）：H 盘的 SolidWorks 项目里几百个文件超过 220 个字符（最长 266），默认输出目录又在输入文件夹里面、再套一层，
            // 转出来的路径超过 Windows 的 259 个字符就存不上，原来每个只报一句看不懂的保存失败。扫描完就先说
            string warn=LongPathWarning(options);
            if(warn!=null){ AppendLog(warn); status.Text+="　"+warn; }
            if(items.Count==0)MessageBox.Show(this,"没有找到可转换的文件。请检查输入路径、扩展名勾选和“包含子文件夹”。","天工格式转换");
        }
        // 源文件 / 输出路径超过 259 个字符的有几个（输出只算得出顶层文件，零件按同一层估）
        string LongPathWarning(ConvertOptions options){
            int src=0,dst=0;string example=null;
            bool hasOut=options.HasOutputTarget();   // 还没选输出目录时只查源文件
            foreach(ConvertItem item in items){
                if(item.Source.Length>MaxPath){ src++; if(example==null)example=item.Source; }
                else if(hasOut){
                    bool tooLong;
                    try{ tooLong=ConvertPlanner.TopTarget(item,options).Length>MaxPath; }catch(PathTooLongException){ tooLong=true; }catch{ tooLong=false; }
                    if(tooLong){ dst++; if(example==null)example=item.Source; }
                }
            }
            if(src==0&&dst==0)return null;
            var sb=new StringBuilder("注意：");
            if(src>0)sb.Append(src+" 个文件本身的路径超过 "+MaxPath+" 个字符，天工打不开（把项目文件夹挪到浅一点的地方，比如 D:\\项目\\）；");
            if(dst>0){
                if(options.OutputMode==ConvertOutputMode.InPlace)
                    sb.Append(dst+" 个文件转出来的路径会超过 "+MaxPath+" 个字符、存不上（可把项目文件夹挪浅，或改用「统一输出到指定目录」选短路径如 D:\\转换输出）；");
                else
                    sb.Append(dst+" 个文件转出来的路径会超过 "+MaxPath+" 个字符、存不上（可改用「各自源文件夹（原地）」，或把输出目录选短一点如 D:\\转换输出）；");
            }
            sb.Append("例如 "+Path.GetFileName(example)+"。");
            return sb.ToString();
        }
        const int MaxPath=259;
        void StartConversion(){
            if(timer.Enabled)return;
            var options=BuildOptions();
            // 统一总目录模式下先建输出目录再校验：默认输出目录（…\天工格式输出）本来就还不存在。
            if(options.OutputMode==ConvertOutputMode.SingleRoot&&options.OutputRoot.Length>0&&options.Inputs.Count>0){
                try{ Directory.CreateDirectory(options.OutputRoot); }
                catch(Exception e){ MessageBox.Show(this,"无法创建输出目录："+e.Message,"天工格式转换"); return; }
            }
            string problem=options.Validate();
            if(problem!=null){ MessageBox.Show(this,problem,"天工格式转换"); return; }
            if(items.Count==0||force.Checked||verify.Checked)ScanInputs(true);
            if(items.Count==0)return;
            // 用户先扫描再切页修改输出模式/目录时，必须按当前 options 重算所有条目的 OutputRoot 与 Target 并复查超长路径，
            // 否则预跳过项与 pending 项在 CSV 报告里会写入旧路径或空路径。
            ConvertPlanner.ApplyOutputRoots(items,options);
            var outDirs=ConvertPlanner.ResolveDistinctOutputDirs(items,options);
            foreach(string dir in outDirs){
                try{ Directory.CreateDirectory(dir); }
                catch(Exception e){ MessageBox.Show(this,"无法创建输出目录 "+dir+"："+e.Message,"天工格式转换"); return; }
            }
            lastRunOutputDirs.Clear();
            lastRunOutputDirs.AddRange(outDirs);
            string pathWarn=LongPathWarning(options);
            if(pathWarn!=null)AppendLog(pathWarn);

            processes.Clear();statusFiles.Clear();finishedStatusFiles.Clear();offsets.Clear();rows.Clear();
            totalPlanned=items.Count;
            string reportDir=ConvertPlanner.DefaultReportDirectory(outDirs,options);
            if(!string.IsNullOrEmpty(reportDir)){
                try{ Directory.CreateDirectory(reportDir); }catch{}
            }
            reportPath=Path.Combine(string.IsNullOrEmpty(reportDir)?Path.GetTempPath():reportDir,"转换报告-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".csv");
            startedAt=DateTime.Now;

            // 启动任何隐藏 CAD 前先过滤已存在有效输出的文件：全部已转过时毫秒级结束，不白等十几秒启 CAD。
            List<ConvertRow> preSkipped;
            var pending=ConvertPlanner.FilterPending(items,options,out preSkipped);
            preSkippedCount=preSkipped.Count;
            foreach(ConvertRow sr in preSkipped)rows[sr.Index]=sr;
            if(pending.Count==0){
                AppendLog("全部 "+items.Count+" 个文件已存在有效转换结果，已直接跳过（无需启动 CAD）。");
                progress.Maximum=Math.Max(1,totalPlanned);progress.Value=progress.Maximum;
                statDone.Set(rows.Count+" / "+totalPlanned,Ui.Text);statOk.Set("0",Ui.Ok);statFail.Set("0",Ui.Text);statSkip.Set(preSkipped.Count.ToString(),Ui.Text);
                shell.Go(2);
                Finish();
                return;
            }

            // 插件模式下当前进程是 TianGong.exe，绝不能回退 Application.ExecutablePath 拉起报错的天工 GUI 本体。
            string self=ConverterHost.Resolve();
            if(self==null){
                string hostExe=Path.GetFileName(Application.ExecutablePath??"");
                if(string.Equals(hostExe,ConverterHost.ExeName,StringComparison.OrdinalIgnoreCase)){
                    self=Application.ExecutablePath;
                } else {
                    MessageBox.Show(this,"找不到独立转换器 "+ConverterHost.ExeName+"，无法启动后台转换进程。\n请确认插件安装目录完整或未被安全软件隔离后重试。","天工格式转换",MessageBoxButtons.OK,MessageBoxIcon.Error);
                    return;
                }
            }

            int cores=Environment.ProcessorCount;
            long availMb=(long)AvailableMemoryMb();
            int count=autoWorkers.Checked
                ?ConvertPlanner.SuggestedWorkers(pending,cores,availMb,options)
                :Math.Max(1,Math.Min((int)workers.Value,pending.Count));
            count=Math.Max(1,Math.Min(count,pending.Count));
            options.Workers=count;

            workRoot=Path.Combine(Path.GetTempPath(),"TianGongConverter",DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N").Substring(0,6));
            Directory.CreateDirectory(workRoot);
            // 启动任何 worker 之前一次性采集所有源目录（含子/兄弟目录）基线快照，杜绝并发小装配清理竞态与子目录残留。
            ConvertSourceBaseline.CaptureForBatch(workRoot,items,options);
            string queueDir=Path.Combine(workRoot,"queue");
            ConvertQueue.Write(queueDir,pending,options);

            List<string> activeStatus;
            List<Process> activeProcs;
            int reused,newlyBooted;
            warmPool.Dispatch(count,self,queueDir,options,workRoot,AppendLog,out activeStatus,out activeProcs,out reused,out newlyBooted);
            for(int i=0;i<activeStatus.Count;i++){
                statusFiles.Add(activeStatus[i]);
                offsets[activeStatus[i]]=0;
                processes.Add(activeProcs[i]);
            }
            string poolSummary=reused>0
                ?(newlyBooted>0?("复用热池 "+reused+" 个 + 同步预拉起 "+newlyBooted+" 个"):("热池秒复用 "+reused+" 个隐藏 CAD"))
                :("同步预拉起 "+newlyBooted+" 个隐藏 CAD");
            string outputSummary=outDirs.Count==1
                ?("输出目录 "+outDirs[0])
                :("共 "+outDirs.Count+" 个独立输出目录（"+(options.OutputMode==ConvertOutputMode.InPlace?"各自源文件夹原地输出":(options.OutputMode==ConvertOutputMode.PerInputSubfolder?("各自建「"+options.EffectiveSubfolder()+"」"):"统一目录按站位分目录"))+"）");
            AppendLog("开始转换：共 "+items.Count+" 个文件"+(preSkippedCount>0?"（预跳过已存在 "+preSkippedCount+" 个，实际待转 "+pending.Count+" 个）":"")
                +"，启用 "+processes.Count+" 个并发进程（"+poolSummary+"）。"+outputSummary);
            if(outDirs.Count>1){
                int show=Math.Min(10,outDirs.Count);
                for(int oi=0;oi<show;oi++)AppendLog("  输出目录 #"+(oi+1)+"："+outDirs[oi]);
                if(outDirs.Count>show)AppendLog("  … 等共 "+outDirs.Count+" 个输出目录");
            }
            AppendLog("硬件评估：CPU "+cores+" 逻辑核、可用内存 "+availMb+" MB，采用动态抢占队列（装配优先 + 源主干跨进程去重）。");
            start.Enabled=false;stop.Enabled=true;saveReport.Enabled=false;releasePool.Enabled=false;
            progress.Maximum=Math.Max(1,totalPlanned);progress.Value=Math.Min(progress.Maximum,rows.Count);
            statDone.Set(rows.Count+" / "+totalPlanned,Ui.Text);statOk.Set("0",Ui.Ok);statFail.Set("0",Ui.Text);statSkip.Set(preSkippedCount.ToString(),Ui.Text);
            timer.Start();
            start.Text="转换中 "+rows.Count+" / "+totalPlanned;start.Progress=totalPlanned>0?(float)rows.Count/totalPlanned:0;
            shell.Go(2);UpdatePrimary();
        }
        void StopConversion(){
            AppendLog("正在停止……");
            foreach(string statusFile in statusFiles)ReadStatus(statusFile);   // 先把 worker 报上来的 CAD 进程号读全
            timer.Stop();
            warmPool.Shutdown(true,AppendLog);
            Finish();
        }
        void ReleaseWarmPool(){
            if(timer.Enabled)return;
            int n=warmPool.AliveCount;
            if(n<=0){ RefreshPoolUi(); return; }
            warmPool.Shutdown(false,AppendLog);
            AppendLog("已释放 "+n+" 个热驻留隐藏 CAD 进程。");
            RefreshPoolUi();
        }
        void RefreshPoolUi(){
            int alive=warmPool.AliveCount;
            releasePool.Visible=alive>0&&!timer.Enabled;
            releasePool.Enabled=alive>0&&!timer.Enabled;
            if(alive>0)releasePool.Text="释放驻留 ("+alive+")";
            shell.Footer.PerformLayout();
        }
        void Poll(){
            deferLog=true;
            try{
                foreach(string statusFile in statusFiles)ReadStatus(statusFile);
            }finally{
                deferLog=false;
                FlushPendingLog();
            }
            bool running=false;
            for(int i=0;i<processes.Count;i++){
                if(i<statusFiles.Count&&finishedStatusFiles.Contains(statusFiles[i]))continue;
                try{ if(!processes[i].HasExited)running=true; }catch{}
            }
            int done=0,failed=0,skipped=0;double seconds=0;
            foreach(ConvertRow row in rows.Values){
                if(row.Status=="ok"){done++;seconds+=row.TotalSeconds;}
                else if(row.Status=="fail")failed++;
                else if(row.Status=="skip")skipped++;
            }
            progress.Value=Math.Min(progress.Maximum,rows.Count);
            statDone.Set(rows.Count+" / "+totalPlanned,Ui.Text);statOk.Set(done.ToString(),Ui.Ok);statFail.Set(failed.ToString(),failed>0?Ui.Bad:Ui.Text);statSkip.Set(skipped.ToString(),Ui.Text);
            if(timer.Enabled){ start.Text="转换中 "+rows.Count+" / "+totalPlanned;start.Progress=totalPlanned>0?(float)rows.Count/totalPlanned:0; }
            double elapsed=(DateTime.Now-startedAt).TotalSeconds;
            int activeDone=Math.Max(0,rows.Count-preSkippedCount);
            int remaining=Math.Max(0,totalPlanned-rows.Count);
            string rateText="";
            if(activeDone>0&&elapsed>1.0){
                double perMin=activeDone*60.0/elapsed;
                double eta=remaining>0?(elapsed*remaining/activeDone):0;
                rateText="　速率 "+perMin.ToString("F1")+" 个/分"+(remaining>0?"　预计剩余 "+FormatEta(eta):"");
            }
            status.Text="进度 "+rows.Count+" / "+totalPlanned+"　成功 "+done+"　失败 "+failed+"　累计单件 "+seconds.ToString("F1")+" 秒　已用 "+elapsed.ToString("F1")+" 秒"+rateText;
            if(!running){
                // 进程退出前最后写出 END/FATAL/EXIT 可能落在本轮 ReadStatus 与 HasExited 之间，进入 Finish 前必须补读一遍。
                deferLog=true;
                try{
                    foreach(string statusFile in statusFiles)ReadStatus(statusFile);
                }finally{
                    deferLog=false;
                    FlushPendingLog();
                }
                done=0;failed=0;skipped=0;seconds=0;
                foreach(ConvertRow row in rows.Values){
                    if(row.Status=="ok"){done++;seconds+=row.TotalSeconds;}
                    else if(row.Status=="fail")failed++;
                    else if(row.Status=="skip")skipped++;
                }
                progress.Value=Math.Min(progress.Maximum,rows.Count);
                statDone.Set(rows.Count+" / "+totalPlanned,Ui.Text);statOk.Set(done.ToString(),Ui.Ok);statFail.Set(failed.ToString(),failed>0?Ui.Bad:Ui.Text);statSkip.Set(skipped.ToString(),Ui.Text);
                timer.Stop();
                Finish();
            }
        }
        static string FormatEta(double seconds){
            if(seconds<60)return Math.Ceiling(seconds).ToString("F0")+" 秒";
            int mins=(int)(seconds/60);int secs=(int)(seconds%60);
            if(mins<60)return mins+" 分 "+secs+" 秒";
            return (mins/60)+" 小时 "+(mins%60)+" 分";
        }
        void ReadStatus(string statusFile){
            try{
                using(var stream=new FileStream(statusFile,FileMode.Open,FileAccess.Read,FileShare.ReadWrite)){
                    long offset=offsets[statusFile];
                    if(offset>stream.Length)offset=0;
                    stream.Seek(offset,SeekOrigin.Begin);
                    // 只读到最后一个换行（0.10.2）：worker 正写到一半的那行留到下次。原来 ReadLine 把半行当一整行吃掉、偏移挪到末尾，
                    // 剩下半行下次又当一行 —— 正好切开的是 END 行时，这个文件在报告里成了"没转"（其实转好了）
                    var buffer=new byte[stream.Length-offset];
                    int got=0,n;
                    while(got<buffer.Length&&(n=stream.Read(buffer,got,buffer.Length-got))>0)got+=n;
                    int end=Array.LastIndexOf(buffer,(byte)'\n',Math.Max(0,got-1));
                    if(got==0||end<0)return;
                    string text=new UTF8Encoding(false).GetString(buffer,0,end+1);
                    foreach(string line in text.Split('\n')){ string l=line.TrimEnd('\r'); if(l.Length>0)HandleLine(statusFile,l); }
                    offsets[statusFile]=offset+end+1;
                }
            }catch{}
        }
        void HandleLine(string statusFile,string line){
            string[] parts=ConvertProtocol.Split(line);
            if(parts.Length==0)return;
            if(parts[0]==ConvertProtocol.End){
                ConvertRow row;
                if(ConvertProtocol.TryParseEnd(parts,out row)){
                    rows[row.Index]=row;
                    if(row.Status=="ok")AppendLog("完成 "+(row.TotalSeconds).ToString("F1")+" 秒（打开 "+row.OpenSeconds.ToString("F1")+" / 保存 "+row.SaveSeconds.ToString("F1")+"，"+row.Documents+" 个文档）[P"+row.Worker+"] "+Path.GetFileName(row.Source));
                    else if(row.Status=="skip")AppendLog("跳过（"+(string.IsNullOrEmpty(row.Error)?"输出已存在":row.Error)+"） "+Path.GetFileName(row.Source));
                    else AppendLog("失败 "+Path.GetFileName(row.Source)+"："+row.Error);
                }
            }else if(parts[0]==ConvertProtocol.Ready){
                int pid;
                if(parts.Length>2&&int.TryParse(parts[2],out pid)&&pid>0){
                    long startTicks=0L;
                    if(parts.Length>4)long.TryParse(parts[4],NumberStyles.Integer,CultureInfo.InvariantCulture,out startTicks);
                    warmPool.RecordCadPid(pid,startTicks);
                }
                bool warm=parts.Length>3&&parts[3]=="warm";
                AppendLog(warm?"工作进程就绪（热池复用，0.00 秒）。":"工作进程就绪（CAD 启动 "+(parts.Length>1?parts[1]:"?")+" 秒）。");
            }else if(parts[0]==ConvertProtocol.Exit){
                finishedStatusFiles.Add(statusFile);
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
            var summary=ConvertSummary.From(list,wall);summary.Workers=Math.Max(1,processes.Count);
            try{
                File.WriteAllText(reportPath,summary.ToCsv(list),new UTF8Encoding(true));
                AppendLog("报告已写入 "+reportPath);
            }catch(Exception e){ AppendLog("报告写入失败："+e.Message); }
            AppendLog(summary.Describe());
            status.Text=summary.Describe();
            start.Enabled=true;stop.Enabled=false;saveReport.Enabled=true;
            progress.Value=progress.Maximum;
            start.Text="开始转换";start.Progress=-1;UpdatePrimary();
            if(!keepPool.Checked){
                warmPool.Shutdown(false,AppendLog);
            } else {
                int alive=warmPool.AliveCount;
                if(alive>0)AppendLog("热驻留进程池保持 "+alive+" 个已预热 CAD（连续下一批秒启动，空闲 5 分钟或关窗自动释放）。");
            }
            RefreshPoolUi();
            shell.ShowToast(summary.Describe(),summary.Failed>0?Ui.Warn:Ui.Ok,"打开输出目录",delegate{ OpenOutputFolder(); });
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
            string line=DateTime.Now.ToString("HH:mm:ss")+"  "+message+Environment.NewLine;
            if(deferLog){ pendingLog.Append(line); return; }
            AppendLogText(line);
        }
        void FlushPendingLog(){
            if(pendingLog.Length==0)return;
            string chunk=pendingLog.ToString();
            pendingLog.Length=0;
            AppendLogText(chunk);
        }
        void AppendLogText(string chunk){
            const int MaxLogChars=65536;
            if(logBox.TextLength+chunk.Length>MaxLogChars){
                string full=logBox.Text+chunk;
                int cut=full.IndexOf('\n',Math.Max(0,full.Length-MaxLogChars/2));
                logBox.Text=cut>=0?full.Substring(cut+1):full.Substring(Math.Max(0,full.Length-MaxLogChars/2));
                logBox.SelectionStart=logBox.TextLength;
                logBox.ScrollToCaret();
            }else{
                logBox.AppendText(chunk);
            }
        }
        // 宿主加载项卸载/退出时静默回收热池并关闭窗口（不弹模态确认框阻塞 CAD 退出）
        internal void ForceCloseForHostDispose(){
            try{ timer.Stop(); }catch{}
            try{ poolWatch.Stop(); }catch{}
            try{ warmPool.Shutdown(true,null); }catch{}
            try{ if(!IsDisposed)Close(); }catch{}
            try{ if(!IsDisposed)Dispose(); }catch{}
        }
        protected override void OnFormClosing(FormClosingEventArgs e){
            if(timer.Enabled){
                if(MessageBox.Show(this,"转换仍在进行，确定要停止并退出吗？","天工格式转换",MessageBoxButtons.YesNo,MessageBoxIcon.Warning)!=DialogResult.Yes){ e.Cancel=true; return; }
                foreach(string statusFile in statusFiles)ReadStatus(statusFile);
                timer.Stop();
                warmPool.Shutdown(true,AppendLog);
            } else if(warmPool.AliveCount>0){
                warmPool.Shutdown(false,null);
            }
            base.OnFormClosing(e);
        }
        protected override void OnFormClosed(FormClosedEventArgs e){
            timer.Stop();
            timer.Dispose();
            poolWatch.Stop();
            poolWatch.Dispose();
            processes.Clear();
            warmPool.Dispose();
            base.OnFormClosed(e);
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MEMORYSTATUSEX {
            public uint dwLength;public uint dwMemoryLoad;public ulong ullTotalPhys;public ulong ullAvailPhys;
            public ulong ullTotalPageFile;public ulong ullAvailPageFile;public ulong ullTotalVirtual;public ulong ullAvailVirtual;public ulong ullAvailExtendedVirtual;
        }
        [DllImport("kernel32.dll",SetLastError=true)]
        static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);
    }
}
