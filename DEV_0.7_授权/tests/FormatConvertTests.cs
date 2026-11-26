using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using TianGongCadSuite;

static class FormatConvertTests {
    static void Assert(bool ok,string label){ if(!ok)throw new Exception("FAIL: "+label); Console.WriteLine("PASS: "+label); }
    public static void Pure(){
        SameNameTargets();PureRest();
    }
    public static void SameNameTargets(){
        // 0.10.2：平铺输出时不同文件夹里的同名零件不能并成一个
        var written=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);string clash;
        Assert(ConvertPlanner.ClaimTarget(written,@"D:\out\零件1.par",@"C:\src\A\零件1.par",out clash)==@"D:\out\零件1.par"&&clash==null,"first part claims its name");
        Assert(ConvertPlanner.ClaimTarget(written,@"D:\out\零件1.par",@"C:\src\A\零件1.par",out clash)==null,"same source again merges (duplicate instance)");
        Assert(ConvertPlanner.ClaimTarget(written,@"D:\out\零件1.par",@"C:\src\B\零件1.par",out clash)==@"D:\out\零件1 (2).par"&&clash==@"C:\src\A\零件1.par","different source with the same name gets (2)");
        Assert(ConvertPlanner.ClaimTarget(written,@"D:\out\零件1.par",@"C:\src\B\零件1.par",out clash)==null,"second folder's part again merges into its own (2)");
        Assert(ConvertPlanner.ClaimTarget(written,@"D:\out\零件1.par",@"C:\src\C\零件1.par",out clash)==@"D:\out\零件1 (3).par","third different source gets (3)");
        // 0.10.2：装配打开成空的时拷到一起再开 —— 零件、装配体分文件夹的项目
        string root=Path.Combine(Path.GetTempPath(),"tgstage-"+Guid.NewGuid().ToString("N").Substring(0,6));
        try{
            Directory.CreateDirectory(Path.Combine(root,"装配体"));Directory.CreateDirectory(Path.Combine(root,"零件","子"));
            string asm=Path.Combine(root,"装配体","总装.SLDASM");File.WriteAllText(asm,"x");
            File.WriteAllText(Path.Combine(root,"零件","板.sldprt"),"x");File.WriteAllText(Path.Combine(root,"零件","子","板.sldprt"),"x");
            File.WriteAllText(Path.Combine(root,"零件","~$板.sldprt"),"x");File.WriteAllText(Path.Combine(root,"零件","说明.pdf"),"x");
            var a=ConvertPlanner.StageSources(asm,root);
            Assert(a.Count==2&&a[0]==Path.GetFullPath(asm)&&Path.GetFileName(a[1])=="板.sldprt","stage: source first, same-name part once, no lock file / pdf ("+a.Count+")");
            var b=ConvertPlanner.StageSources(asm,Path.Combine(root,"装配体"));
            Assert(b.Count==2,"stage: input folder = assembly folder → looks one level up ("+b.Count+")");
        } finally { try{ Directory.Delete(root,true); }catch{} }
    }
    static void PureRest(){
        Assert(ConvertPlanner.NativeExtension(@"C:\a\b.SLDASM")==".asm","sldasm maps to asm");
        Assert(ConvertPlanner.NativeExtension(@"C:\a\b.sldprt")==".par","sldprt maps to par");
        Assert(ConvertPlanner.NativeExtension(@"C:\a\b.SLDDRW")==".dft","slddrw maps to dft");
        Assert(ConvertPlanner.IsLockFile("~$x.SLDASM"),"SolidWorks lock file detected");
        Assert(!ConvertPlanner.IsLockFile("x.SLDASM"),"normal file is not a lock file");
        Assert(ConvertPlanner.IsSolidWorks(@"C:\a\b.SLDPRT"),"sldprt recognised");
        Assert(!ConvertPlanner.IsSolidWorks(@"C:\a\b.step"),"step is not claimed as SolidWorks");
        Assert(ConvertPlanner.IsStep(@"C:\a\b.STP"),"stp recognised");
        Assert(ConvertPlanner.IsStep(@"C:\a\b.step"),"step recognised");
        Assert(ConvertPlanner.IsSupported(@"C:\a\b.step")&&ConvertPlanner.IsSupported(@"C:\a\b.sldasm"),"step and solidworks both supported");
        Assert(ConvertPlanner.NativeExtension(@"C:\a\b.stp")==".par","stp provisionally maps to par");

        string root=Path.Combine(Path.GetTempPath(),"fmtconvtest-"+Guid.NewGuid().ToString("N"));
        string sub=Path.Combine(root,"子目录");
        Directory.CreateDirectory(sub);
        Directory.CreateDirectory(Path.Combine(root,".hidden"));
        File.WriteAllText(Path.Combine(root,"a.SLDASM"),"x");
        File.WriteAllText(Path.Combine(root,"~$a.SLDASM"),"x");
        File.WriteAllText(Path.Combine(root,"b.sldprt"),"x");
        File.WriteAllText(Path.Combine(root,"c.STEP"),"x");
        File.WriteAllText(Path.Combine(sub,"d.SLDASM"),"x");
        File.WriteAllText(Path.Combine(root,".hidden","e.SLDASM"),"x");
        try{
            var options=new ConvertOptions();
            options.Inputs.Add(root);options.OutputRoot=Path.Combine(root,"out");
            var items=ConvertPlanner.Scan(options);
            Assert(items.Count==4,"scan keeps 4 supported files, drops the lock file and the hidden folder: got "+items.Count);
            options.IncludeStep=false;
            Assert(ConvertPlanner.Scan(options).Count==3,"step can be switched off");
            options.IncludeStep=true;
            foreach(ConvertItem item in items)Assert(!ConvertPlanner.IsLockFile(Path.GetFileName(item.Source)),"lock file excluded");
            bool found=false;foreach(ConvertItem item in items)if(Path.GetFileName(item.Source)=="d.SLDASM")found=true;
            Assert(found,"recursive scan found nested assembly");
            options.Recursive=false;
            Assert(ConvertPlanner.Scan(options).Count==3,"non-recursive scan keeps 3 files");
            options.Recursive=true;
            options.IncludeParts=false;
            Assert(ConvertPlanner.Scan(options).Count==3,"parts-only switch respected");
            options.IncludeParts=true;
            var stepItem=new ConvertItem();stepItem.Source=Path.Combine(root,"c.STEP");stepItem.Root=root;
            var stepTargets=ConvertPlanner.CandidateTargets(stepItem,options);
            Assert(stepTargets.Count==2,"a STEP input can end up as par or asm");
            Assert(stepTargets[0].EndsWith("c.par")&&stepTargets[1].EndsWith("c.asm"),"both STEP candidate names are checked");
            Assert(!ConvertPlanner.IsAlreadyConverted(stepItem,options),"STEP is not converted yet");
            Directory.CreateDirectory(options.OutputRoot);
            File.WriteAllText(stepTargets[1],"x");
            Assert(ConvertPlanner.IsAlreadyConverted(stepItem,options),"an existing asm marks the STEP file as converted");
            File.Delete(stepTargets[1]);

            var target=ConvertPlanner.TopTarget(items[0],options);
            Assert(target.StartsWith(Path.GetFullPath(options.OutputRoot),StringComparison.OrdinalIgnoreCase),"mirror output stays under the output root");
            var nestedItem=new ConvertItem();nestedItem.Source=Path.Combine(sub,"d.SLDASM");nestedItem.Root=root;
            string nestedTarget=ConvertPlanner.TopTarget(nestedItem,options);
            Assert(string.Equals(Path.GetDirectoryName(nestedTarget),Path.Combine(Path.GetFullPath(options.OutputRoot),"子目录"),StringComparison.OrdinalIgnoreCase),"nested top target mirrors only the folder, not the file name: "+nestedTarget);
            options.FlatOutput=true;
            var flat=ConvertPlanner.TopTarget(items[0],options);
            Assert(string.Equals(Path.GetDirectoryName(flat),Path.GetFullPath(options.OutputRoot),StringComparison.OrdinalIgnoreCase),"flat output has no sub-folder");
            options.FlatOutput=false;

            var sample=new ConvertItem();
            sample.Source=Path.Combine(root,"a.SLDASM");sample.Root=root;
            var inside=ConvertPlanner.ComponentTarget(Path.Combine(sub,"p.par"),"p.par",sample,options);
            Assert(inside.EndsWith(Path.Combine("子目录","p.par")),"component inside the root mirrors its folder: "+inside);
            // A rooted folder outside the input root and outside its parent (drive root, so it is rooted on every platform).
            var outside=ConvertPlanner.ComponentTarget(Path.Combine(Path.GetPathRoot(Path.GetTempPath()),"elsewhere-"+Guid.NewGuid().ToString("N"),"q.par"),"q.par",sample,options);
            Assert(outside.IndexOf("_外部",StringComparison.Ordinal)>=0,"component outside the root is bucketed: "+outside);
            // 零件在输入文件夹的兄弟文件夹里（零件\ 装配体\ 分开放）：按相对上一级的位置放，不进 _外部
            var sibling=ConvertPlanner.ComponentTarget(Path.Combine(Path.GetDirectoryName(root),"零件-"+Guid.NewGuid().ToString("N").Substring(0,6),"s.par"),"s.par",sample,options);
            Assert(sibling.IndexOf("_外部",StringComparison.Ordinal)<0&&Path.GetFileName(Path.GetDirectoryName(sibling)).StartsWith("零件-",StringComparison.Ordinal)&&string.Equals(Path.GetDirectoryName(Path.GetDirectoryName(sibling)),Path.GetFullPath(options.OutputRoot),StringComparison.OrdinalIgnoreCase),"component in a sibling folder mirrors as a sibling: "+sibling);

            var planItems=new List<ConvertItem>();
            long[] sizes={100,200,300,400,500,600,700,800,900,1000};
            for(int i=0;i<sizes.Length;i++){ var ci=new ConvertItem();ci.Index=i;ci.Size=sizes[i];ci.Source=@"C:\proj"+i+@"\"+i+".SLDASM";ci.Root=@"C:\proj"+i;planItems.Add(ci); }
            var plan=ConvertPlanner.Plan(planItems,3);
            Assert(plan.Count==3,"plan has one bucket per worker");
            int planned=0;long max=0,min=long.MaxValue;
            foreach(List<ConvertItem> bucket in plan){
                long load=0;foreach(ConvertItem ci in bucket){ load+=ci.Size;planned++; }
                if(load>max)max=load;if(load<min)min=load;
                Assert(bucket.Count>0,"largest-first plan spreads the 10 files over 3 workers");
            }
            Assert(planned==planItems.Count,"every file scheduled exactly once: "+planned);
            Assert(max-min<=1000,"longest-processing-time balance within one item: max-min="+(max-min));
            var sharedRoot=new List<ConvertItem>();
            for(int i=0;i<6;i++){ var ci=new ConvertItem();ci.Index=100+i;ci.Size=500;ci.Source=@"C:\one\"+i+".SLDASM";ci.Root=@"C:\one";sharedRoot.Add(ci); }
            var sharedPlan=ConvertPlanner.Plan(sharedRoot,4);
            int occupied=0;foreach(List<ConvertItem> bucket in sharedPlan)if(bucket.Count>0)occupied++;
            Assert(occupied==1,"same-folder assemblies stay grouped in static Plan: "+occupied);
            // 同一根目录下不同子文件夹的装配体、以及同目录的多个 STEP / 独立零件，静态分桶也应分散到多进程
            var sameRootSteps=new List<ConvertItem>();
            for(int i=0;i<8;i++){ var ci=new ConvertItem();ci.Index=300+i;ci.Size=1000*(i+1);ci.Source=@"C:\one\"+i+".step";ci.Root=@"C:\one";sameRootSteps.Add(ci); }
            var stepPlan=ConvertPlanner.Plan(sameRootSteps,4);
            int stepOccupied=0;foreach(List<ConvertItem> bucket in stepPlan)if(bucket.Count>0)stepOccupied++;
            Assert(stepOccupied==4,"STEP files in one root spread across all 4 workers: "+stepOccupied);
            // 装配先转（它会把零件一起存出来，轮到零件时直接跳过），哪怕零件文件更大
            var mixed=new List<ConvertItem>();
            string[] names={"big.SLDPRT","small.SLDASM","mid.sldprt","b.sldasm"};long[] msz={9000,100,5000,300};
            for(int i=0;i<names.Length;i++){ var ci=new ConvertItem();ci.Index=200+i;ci.Size=msz[i];ci.Source=@"C:\mix\"+names[i];ci.Root=@"C:\mix";mixed.Add(ci); }
            var mixedPlan=ConvertPlanner.Plan(mixed,1)[0];
            Assert(mixedPlan[0].Source.EndsWith("b.sldasm")&&mixedPlan[1].Source.EndsWith("small.SLDASM")&&mixedPlan[2].Source.EndsWith("big.SLDPRT"),"assemblies are converted before (larger) parts: "+string.Join(",",mixedPlan.ConvertAll(x=>System.IO.Path.GetFileName(x.Source)).ToArray()));
            Assert(ConvertPlanner.SuggestedWorkers(0,16,8192)==1,"no files means one worker");
            Assert(ConvertPlanner.SuggestedWorkers(1,16,8192)==1,"single file means one worker");
            Assert(ConvertPlanner.SuggestedWorkers(2,16,16384,200*1024)==1,"2 tiny files (<1MB) stay on 1 worker");
            Assert(ConvertPlanner.SuggestedWorkers(20,16,16384)==3,"20 files on 16-thread/16GB recommends 3 workers (empirical sweet spot)");
            Assert(ConvertPlanner.SuggestedWorkers(20,32,24576)==4,"20 files on 32-thread/24GB workstation recommends 4 workers");
            Assert(ConvertPlanner.SuggestedWorkers(20,16,4000)==1,"low free RAM (<5GB) caps workers to 1");
            Assert(ConvertPlanner.SuggestedWorkers(20,8,16384)==2,"8-core caps auto workers to 2");
            // 单装配+同目录零件批次：自动并发保持 1，避免多起 CAD 却只剩零件可跳过
            var singleAsmBatch=new List<ConvertItem>();
            singleAsmBatch.Add(new ConvertItem{Index=0,Source=@"C:\p\top.sldasm",Root=@"C:\p",Size=2000000});
            for(int i=1;i<=10;i++)singleAsmBatch.Add(new ConvertItem{Index=i,Source=@"C:\p\part"+i+".sldprt",Root=@"C:\p",Size=500000});
            Assert(ConvertPlanner.SuggestedWorkers(singleAsmBatch,16,16384)==1,"1 assembly + 10 co-located parts stays 1 worker");
            singleAsmBatch.Add(new ConvertItem{Index=11,Source=@"C:\p\top2.sldasm",Root=@"C:\p",Size=2000000});
            Assert(ConvertPlanner.SuggestedWorkers(singleAsmBatch,16,16384)==2,"2 assemblies + parts scales to 2 workers");

            string line=ConvertProtocol.Line(ConvertProtocol.End,"7","ok","C:\\a\\b.SLDASM","1.50","2.25","3.75","12","C:\\o\\b.asm","1","");
            ConvertRow row;
            Assert(ConvertProtocol.TryParseEnd(ConvertProtocol.Split(line),out row),"protocol round trip");
            Assert(row.Index==7&&row.Status=="ok"&&row.Documents==12,"protocol fields parsed: "+row.Index+"/"+row.Status+"/"+row.Documents);
            Assert(Math.Abs(row.TotalSeconds-3.75)<1e-9,"protocol seconds parsed");
            Assert(!ConvertProtocol.TryParseEnd(ConvertProtocol.Split("BEGIN\t1\tx"),out row),"non-END lines rejected");
            string sanitized=ConvertProtocol.Line("a\tb","c\nd");
            Assert(sanitized.IndexOf('\t')==sanitized.LastIndexOf('\t'),"tabs inside fields are escaped");

            var rows=new List<ConvertRow>();
            var ok=new ConvertRow();ok.Status="ok";ok.TotalSeconds=10;ok.Source="a,b";rows.Add(ok);
            var skip=new ConvertRow();skip.Status="skip";rows.Add(skip);
            var fail=new ConvertRow();fail.Status="fail";fail.Error="boom";rows.Add(fail);
            var pending=new ConvertRow();pending.Status="pending";rows.Add(pending);
            var summary=ConvertSummary.From(rows,5);
            Assert(summary.Total==4&&summary.Converted==1&&summary.Skipped==1&&summary.Failed==1,"summary buckets statuses");
            Assert(Math.Abs(summary.SpeedupFactor-2)<1e-9,"speedup factor computed");
            string csv=summary.ToCsv(rows);
            // 回归：worker 必须解析到转换器自身。插件模式下当前进程是 TianGong.exe，
        // 用 Application.ExecutablePath 当 worker 会再拉起一个天工CAD（弹"找不到文件 --worker"）
        // 或直接拉起插件 DLL 自己，两种情况都转换不了、而且界面完全没有报错。
        string worker=ConverterHost.Resolve();
        Assert(worker!=null,"worker host resolves: "+(worker??"(null)"));
        Assert(Path.GetFileName(worker)=="TianGongConverter.exe","worker host points at the converter exe, got "+Path.GetFileName(worker));
        Assert(string.Equals(Path.GetDirectoryName(worker),Path.GetDirectoryName(typeof(ConverterHost).Assembly.Location),StringComparison.OrdinalIgnoreCase),
            "worker host is beside the plug-in assembly (not the hosting CAD exe)");
        Assert(File.Exists(worker),"resolved worker host actually exists on disk");
        Assert(csv.IndexOf("\"a,b\"",StringComparison.Ordinal)>=0,"CSV quotes embedded commas");
            Assert(csv.Split('\n').Length>=5,"CSV has a header and one line per row");
        } finally { try{ Directory.Delete(root,true); }catch{} }
        ResumeMarker();
        CommandLineQuoting();
        DynamicQueueAndFilter();
        MultiStationOutputModes();
    }

    // 0.13.10 多站位输出模式测试：原地输出（方案 B 默认）、各自建子文件夹、统一总目录多站位自动分目录与同名消歧、
    // 原地模式同目录串行 / 站间并行、STEP 子件不落入 Temp、防递归扫入输出子目录与队列第 5 列透传。
    public static void MultiStationOutputModes(){
        string root=Path.Combine(Path.GetTempPath(),"fmtmulti-"+Guid.NewGuid().ToString("N").Substring(0,8));
        string st1=Path.Combine(root,"ST01");
        string st1Sub=Path.Combine(st1,"子部件");
        string st1Parts=Path.Combine(root,"ST01_零件");
        string st2=Path.Combine(root,"ST02");
        string lineA3d=Path.Combine(root,"线体A","3D");
        string lineB3d=Path.Combine(root,"线体B","3D");
        string sharedOut=Path.Combine(root,"统一输出");
        Directory.CreateDirectory(st1Sub);
        Directory.CreateDirectory(st1Parts);
        Directory.CreateDirectory(st2);
        Directory.CreateDirectory(lineA3d);
        Directory.CreateDirectory(lineB3d);
        Directory.CreateDirectory(sharedOut);
        try{
            string s1Asm1=Path.Combine(st1,"ST01_Top.SLDASM");
            string s1Asm2=Path.Combine(st1,"ST01_Sub.SLDASM");
            string s1Prt=Path.Combine(st1Sub,"Bracket.SLDPRT");
            string s1Step=Path.Combine(st1,"Cylinder.step");
            string s2Asm=Path.Combine(st2,"ST02_Top.SLDASM");
            string s2Prt=Path.Combine(st2,"Bracket.SLDPRT");
            File.WriteAllText(s1Asm1,"x");
            File.WriteAllText(s1Asm2,"x");
            File.WriteAllText(s1Prt,"x");
            File.WriteAllText(s1Step,"x");
            File.WriteAllText(s2Asm,"x");
            File.WriteAllText(s2Prt,"x");
            File.WriteAllText(Path.Combine(lineA3d,"A.SLDASM"),"x");
            File.WriteAllText(Path.Combine(lineB3d,"B.SLDASM"),"x");

            // 1. 原地模式（InPlace）：两个站位各自转换在各自源文件夹内，无需填全局 OutputRoot
            var inPlace=new ConvertOptions{ OutputMode=ConvertOutputMode.InPlace };
            inPlace.Inputs.Add(st1);
            inPlace.Inputs.Add(st2);
            Assert(inPlace.Validate()==null,"InPlace validates with empty global OutputRoot");
            var ipItems=ConvertPlanner.Scan(inPlace);
            Assert(ipItems.Count==6,"InPlace scans all 6 files across ST01 and ST02: "+ipItems.Count);
            ConvertItem itS1Asm=ipItems.Find(x=>string.Equals(x.Source,s1Asm1,StringComparison.OrdinalIgnoreCase));
            ConvertItem itS1Prt=ipItems.Find(x=>string.Equals(x.Source,s1Prt,StringComparison.OrdinalIgnoreCase));
            ConvertItem itS1Step=ipItems.Find(x=>string.Equals(x.Source,s1Step,StringComparison.OrdinalIgnoreCase));
            ConvertItem itS2Asm=ipItems.Find(x=>string.Equals(x.Source,s2Asm,StringComparison.OrdinalIgnoreCase));
            ConvertItem itS2Prt=ipItems.Find(x=>string.Equals(x.Source,s2Prt,StringComparison.OrdinalIgnoreCase));
            Assert(string.Equals(itS1Asm.Target,Path.Combine(st1,"ST01_Top.asm"),StringComparison.OrdinalIgnoreCase),"InPlace ST01 top asm lands in ST01: "+itS1Asm.Target);
            Assert(string.Equals(itS1Prt.Target,Path.Combine(st1Sub,"Bracket.par"),StringComparison.OrdinalIgnoreCase),"InPlace ST01 subfolder part lands in its own subfolder: "+itS1Prt.Target);
            Assert(string.Equals(itS2Asm.Target,Path.Combine(st2,"ST02_Top.asm"),StringComparison.OrdinalIgnoreCase),"InPlace ST02 top asm lands in ST02: "+itS2Asm.Target);
            Assert(string.Equals(itS2Prt.Target,Path.Combine(st2,"Bracket.par"),StringComparison.OrdinalIgnoreCase),"InPlace ST02 same-name part stays isolated in ST02: "+itS2Prt.Target);
            string swComp=ConvertPlanner.ComponentTarget(Path.Combine(st1Parts,"Plate.par"),"Plate.par",itS1Asm,inPlace);
            Assert(string.Equals(swComp,Path.Combine(st1Parts,"Plate.par"),StringComparison.OrdinalIgnoreCase),"InPlace SW component keeps its own source dir: "+swComp);
            string stepCompTemp=ConvertPlanner.ComponentTarget(Path.Combine(Path.GetTempPath(),"tg_step_tmp","Body1.par"),"Body1.par",itS1Step,inPlace);
            Assert(string.Equals(stepCompTemp,Path.Combine(st1,"Body1.par"),StringComparison.OrdinalIgnoreCase),"InPlace STEP component never leaks into system Temp: "+stepCompTemp);

            // 原地模式下：同一站位里的 2 个小装配强制串行（sameOut=true 写入 .d<hash>），但两个站位之间仍可并行！
            var singleStationInPlace=new List<ConvertItem>{
                new ConvertItem{Index=0,Source=s1Asm1,Root=st1,OutputRoot=st1,Size=1024*1024},
                new ConvertItem{Index=1,Source=s1Asm2,Root=st1,OutputRoot=st1,Size=1024*1024},
            };
            Assert(ConvertPlanner.SuggestedWorkers(singleStationInPlace,16,16384,inPlace)==1,"InPlace serializes 2 small assemblies in same station dir");
            var twoStationsInPlace=new List<ConvertItem>{
                new ConvertItem{Index=0,Source=s1Asm1,Root=st1,OutputRoot=st1,Size=1024*1024},
                new ConvertItem{Index=1,Source=s1Asm2,Root=st1,OutputRoot=st1,Size=1024*1024},
                new ConvertItem{Index=2,Source=s2Asm,Root=st2,OutputRoot=st2,Size=1024*1024},
            };
            Assert(ConvertPlanner.SuggestedWorkers(twoStationsInPlace,16,16384,inPlace)==2,"InPlace scales to 2 workers across 2 stations");
            string qIp=Path.Combine(root,"qip");
            ConvertQueue.Write(qIp,twoStationsInPlace,inPlace);
            ConvertItem ipC0,ipC1;string ipP0,ipP1;
            Assert(ConvertQueue.TryClaimNext(qIp,0,out ipC0,out ipP0)&&ipC0.Index==0&&ConvertPlanner.SameDir(ipC0.OutputRoot,st1),"InPlace queue round-trips item.OutputRoot for ST01");
            // ST01 的 #1 因同目录 .d<hash> 忙碌跳过，Worker 1 立刻并行领取 ST02 的 #2！
            Assert(ConvertQueue.TryClaimNext(qIp,1,out ipC1,out ipP1)&&ipC1.Index==2&&ConvertPlanner.SameDir(ipC1.OutputRoot,st2),"InPlace queue serializes ST01 internally while letting worker 1 run ST02 in parallel");
            ConvertQueue.Complete(ipP0);ConvertQueue.Complete(ipP1);
            // 原地模式不采集源目录清理基线
            string workIp=Path.Combine(root,"work-ip");
            ConvertSourceBaseline.CaptureForBatch(workIp,ipItems,inPlace);
            Assert(!Directory.Exists(Path.Combine(workIp,"src-baseline")),"InPlace skips source-stub baseline capture");

            // 2. 各自建子文件夹模式（PerInputSubfolder）：分别落盘到 ST01\天工格式输出、ST02\天工格式输出，且防二次扫描误扫输出目录
            string st1OutSub=Path.Combine(st1,"天工格式输出");
            Directory.CreateDirectory(st1OutSub);
            File.WriteAllText(Path.Combine(st1OutSub,"OldLeftover.step"),"x"); // 模拟上次输出目录里的干扰文件
            var subOpts=new ConvertOptions{ OutputMode=ConvertOutputMode.PerInputSubfolder, OutputSubfolder="天工格式输出" };
            subOpts.Inputs.Add(st1);
            subOpts.Inputs.Add(st2);
            Assert(subOpts.Validate()==null,"PerInputSubfolder validates cleanly");
            Assert(!ConvertOptions.IsValidSubfolderName("a\\b")&&!ConvertOptions.IsValidSubfolderName(".."),"invalid subfolder names rejected");
            var subItems=ConvertPlanner.Scan(subOpts);
            Assert(subItems.Count==6,"PerInputSubfolder skips output subfolder during recursive scan: got "+subItems.Count);
            ConvertItem subS1Prt=subItems.Find(x=>string.Equals(x.Source,s1Prt,StringComparison.OrdinalIgnoreCase));
            ConvertItem subS2Asm=subItems.Find(x=>string.Equals(x.Source,s2Asm,StringComparison.OrdinalIgnoreCase));
            Assert(string.Equals(subS1Prt.Target,Path.Combine(st1,"天工格式输出","子部件","Bracket.par"),StringComparison.OrdinalIgnoreCase),"PerInputSubfolder mirrors ST01 subfolder under ST01\\天工格式输出: "+subS1Prt.Target);
            Assert(string.Equals(subS2Asm.Target,Path.Combine(st2,"天工格式输出","ST02_Top.asm"),StringComparison.OrdinalIgnoreCase),"PerInputSubfolder puts ST02 under ST02\\天工格式输出: "+subS2Asm.Target);
            string workSub=Path.Combine(root,"work-sub");
            ConvertSourceBaseline.CaptureForBatch(workSub,subItems,subOpts);
            Assert(ConvertSourceBaseline.TryLoadExisting(workSub,st1)!=null,"PerInputSubfolder captures source dir baseline for ST01");
            Assert(ConvertSourceBaseline.TryLoadExisting(workSub,st1OutSub)==null,"PerInputSubfolder excludes ST01 output subfolder from baseline");

            // 3. 统一输出到指定目录（SingleRoot）：多输入勾选镜像时按站位名分目录；重名目录自动带上级目录名消歧
            var singleMulti=new ConvertOptions{ OutputMode=ConvertOutputMode.SingleRoot, OutputRoot=sharedOut, FlatOutput=false };
            singleMulti.Inputs.Add(st1);
            singleMulti.Inputs.Add(st2);
            var smItems=ConvertPlanner.Scan(singleMulti);
            ConvertItem smS1=smItems.Find(x=>string.Equals(x.Source,s1Asm1,StringComparison.OrdinalIgnoreCase));
            ConvertItem smS2=smItems.Find(x=>string.Equals(x.Source,s2Asm,StringComparison.OrdinalIgnoreCase));
            Assert(string.Equals(smS1.Target,Path.Combine(sharedOut,"ST01","ST01_Top.asm"),StringComparison.OrdinalIgnoreCase),"SingleRoot multi-input isolates ST01 into sharedOut\\ST01: "+smS1.Target);
            Assert(string.Equals(smS2.Target,Path.Combine(sharedOut,"ST02","ST02_Top.asm"),StringComparison.OrdinalIgnoreCase),"SingleRoot multi-input isolates ST02 into sharedOut\\ST02: "+smS2.Target);

            var sameLeaf=new ConvertOptions{ OutputMode=ConvertOutputMode.SingleRoot, OutputRoot=sharedOut, FlatOutput=false };
            sameLeaf.Inputs.Add(lineA3d);
            sameLeaf.Inputs.Add(lineB3d);
            var slItems=ConvertPlanner.Scan(sameLeaf);
            ConvertItem slA=slItems.Find(x=>x.Source.EndsWith("A.SLDASM",StringComparison.OrdinalIgnoreCase));
            ConvertItem slB=slItems.Find(x=>x.Source.EndsWith("B.SLDASM",StringComparison.OrdinalIgnoreCase));
            Assert(string.Equals(slA.Target,Path.Combine(sharedOut,"线体A_3D","A.asm"),StringComparison.OrdinalIgnoreCase),"SingleRoot disambiguates duplicate leaf dir with parent name (A): "+slA.Target);
            Assert(string.Equals(slB.Target,Path.Combine(sharedOut,"线体B_3D","B.asm"),StringComparison.OrdinalIgnoreCase),"SingleRoot disambiguates duplicate leaf dir with parent name (B): "+slB.Target);

            // 4. CommonParentDirectory 与 DefaultReportDirectory
            Assert(ConvertPlanner.SameDir(ConvertPlanner.CommonParentDirectory(new[]{st1,st2}),root),"CommonParentDirectory finds shared parent for ST01 & ST02");
            Assert(ConvertPlanner.SameDir(ConvertPlanner.DefaultReportDirectory(new[]{st1,st2},inPlace),root),"DefaultReportDirectory places multi-station report in shared parent");
            Assert(ConvertPlanner.CommonParentDirectory(new[]{@"C:\ST01",@"D:\ST02"})=="","CommonParentDirectory returns empty across different drives");
            Assert(ConvertPlanner.SameDir(ConvertPlanner.DefaultReportDirectory(new[]{@"C:\ST01",@"D:\ST02"},inPlace),@"C:\ST01"),"DefaultReportDirectory falls back to first dir across different drives");
        } finally { try{ Directory.Delete(root,true); }catch{} }
    }

    // 动态抢占队列（优先级 + LPT + 多线程无重复抢占 + 装配体相位屏障）、预跳过过滤与跨进程已写索引测试
    public static void DynamicQueueAndFilter(){
        string root=Path.Combine(Path.GetTempPath(),"fmtqueue-"+Guid.NewGuid().ToString("N"));
        string input=Path.Combine(root,"in"),output=Path.Combine(root,"out"),qDir=Path.Combine(root,"queue"),work=Path.Combine(root,"work");
        Directory.CreateDirectory(input);Directory.CreateDirectory(output);
        try{
            var opts=new ConvertOptions();opts.Inputs.Add(input);opts.OutputRoot=output;
            var items=new List<ConvertItem>{
                new ConvertItem{Index=0,Source=Path.Combine(input,"p_big.sldprt"),Root=input,Size=9000},
                new ConvertItem{Index=1,Source=Path.Combine(input,"s_mid.step"),Root=input,Size=4000},
                new ConvertItem{Index=2,Source=Path.Combine(input,"a_small.sldasm"),Root=input,Size=100},
                new ConvertItem{Index=3,Source=Path.Combine(input,"a_big.sldasm"),Root=input,Size=500},
                new ConvertItem{Index=4,Source=Path.Combine(input,"s_big.stp"),Root=input,Size=8000},
            };
            for(int i=0;i<items.Count;i++)items[i].Target=ConvertPlanner.TopTarget(items[i],opts);
            // 1. FilterPending：预跳过已存在输出（不含 pending marker 的），Force=true 时不过滤
            File.WriteAllText(items[1].Target,"ok");
            File.WriteAllText(items[4].Target,"half");
            ConvertPlanner.MarkPending(items[4],opts);
            List<ConvertRow> skipped;
            var pending=ConvertPlanner.FilterPending(items,opts,out skipped);
            Assert(skipped.Count==1&&skipped[0].Index==1&&skipped[0].Status=="skip","FilterPending pre-skips completed item 1");
            Assert(pending.Count==4,"FilterPending keeps pending-marker item 4 and unconverted items");
            opts.Force=true;
            List<ConvertRow> forceSkipped;
            var forcePending=ConvertPlanner.FilterPending(items,opts,out forceSkipped);
            Assert(forceSkipped.Count==0&&forcePending.Count==5,"FilterPending skips nothing when Force=true");
            opts.Force=false;

            // 2. ConvertQueue 优先级顺序：a_big.sldasm -> a_small.sldasm -> s_big.stp -> s_mid.step -> p_big.sldprt
            int written=ConvertQueue.Write(qDir,items);
            Assert(written==5&&ConvertQueue.HasPending(qDir),"queue wrote 5 items");
            ConvertItem c0;string p0;
            Assert(ConvertQueue.TryClaimNext(qDir,0,out c0,out p0)&&c0.Index==3,"queue claims largest assembly first: got "+c0.Index);
            ConvertItem c1;string p1;
            Assert(ConvertQueue.TryClaimNext(qDir,1,out c1,out p1)&&c1.Index==2,"queue claims second assembly next: got "+c1.Index);
            // 装配体仍在进行中（p0/p1 未 Complete），STEP 任务仍可立即并行领取！
            ConvertItem c2;string p2;
            Assert(ConvertQueue.TryClaimNext(qDir,2,out c2,out p2)&&c2.Index==4,"STEP claims in parallel while assemblies active: got "+c2.Index);
            ConvertItem c3;string p3;
            Assert(ConvertQueue.TryClaimNext(qDir,3,out c3,out p3)&&c3.Index==1,"second STEP claims in parallel: got "+c3.Index);
            // 验证死掉的 PID 不会阻塞相位屏障：释放 p0/p1 后写一个伪造的已退出 PID 锁，p_big.sldprt 应立即放行
            ConvertQueue.Complete(p0);ConvertQueue.Complete(p1);ConvertQueue.Complete(p2);ConvertQueue.Complete(p3);
            File.WriteAllText(Path.Combine(qDir,"0_999999.task.w9.p999999"),"stale");
            ConvertItem c4;string p4;
            Assert(ConvertQueue.TryClaimNext(qDir,0,out c4,out p4)&&c4.Index==0,"part claims immediately once active assemblies finish (stale dead-PID ignored)");
            ConvertQueue.Complete(p4);
            Assert(!ConvertQueue.HasPending(qDir),"queue is empty after all claimed");

            // 3. 多线程并发原子抢占：24 个任务 4 线程争抢，确保每项恰好领到 1 次
            string qMulti=Path.Combine(root,"qmulti");
            var batch=new List<ConvertItem>();
            for(int i=0;i<24;i++)batch.Add(new ConvertItem{Index=i,Source=Path.Combine(input,"m"+i+".step"),Root=input,Size=100+i});
            ConvertQueue.Write(qMulti,batch);
            var claimedIds=new List<int>();
            object gate=new object();
            var threads=new System.Threading.Thread[4];
            for(int t=0;t<4;t++){
                int wid=t;
                threads[t]=new System.Threading.Thread(delegate(){
                    ConvertItem ci;string cp;
                    while(ConvertQueue.TryClaimNext(qMulti,wid,out ci,out cp)){
                        lock(gate){ claimedIds.Add(ci.Index); }
                        ConvertQueue.Complete(cp);
                    }
                });
                threads[t].Start();
            }
            foreach(var th in threads)th.Join();
            Assert(claimedIds.Count==24&&new HashSet<int>(claimedIds).Count==24,"concurrent 4-thread claim yields 24 distinct items");

            // 4. 跨进程共享已落盘索引 ConvertWrittenIndex（含源路径索引与目标路径隔离）
            string partOut=Path.Combine(output,"SharedPart.par");
            string partSrc=Path.Combine(input,"SharedPart.sldprt");
            Assert(!ConvertWrittenIndex.Contains(work,partOut),"written index initially empty");
            ConvertWrittenIndex.Mark(work,partOut);
            Assert(ConvertWrittenIndex.Contains(work,partOut.ToUpperInvariant()),"written index matches case-insensitively");
            Assert(ConvertWrittenIndex.ContainsAny(work,new[]{Path.Combine(output,"nope.par"),partOut}),"ContainsAny hits marked target");
            Assert(!ConvertWrittenIndex.ContainsSource(work,partSrc),"source index initially empty");
            ConvertWrittenIndex.MarkSource(work,partSrc);
            Assert(ConvertWrittenIndex.ContainsSource(work,partSrc.ToUpperInvariant()),"source index matches case-insensitively");
            Assert(!ConvertWrittenIndex.Contains(work,partSrc),"source index and target index namespaces stay isolated");

            // 5. SolidWorks 源主干解析：区分天工追加的 _5 后缀与真实存在的 _2 文件名
            string bolt=Path.Combine(input,"Bolt.sldprt");
            string bolt2=Path.Combine(input,"Bolt_2.sldprt");
            string subAsm=Path.Combine(input,"Sub.sldasm");
            File.WriteAllText(bolt,"x");File.WriteAllText(bolt2,"x");File.WriteAllText(subAsm,"x");
            bool suf;
            string rExact=ConvertPlanner.ResolveSolidWorksSource(Path.Combine(input,"Bolt.par"),out suf);
            Assert(string.Equals(rExact,bolt,StringComparison.OrdinalIgnoreCase)&&!suf,"exact SW source resolved without suffix");
            string rSuff=ConvertPlanner.ResolveSolidWorksSource(Path.Combine(input,"Bolt_5.par"),out suf);
            Assert(string.Equals(rSuff,bolt,StringComparison.OrdinalIgnoreCase)&&suf,"MRU-suffixed Bolt_5.par maps back to Bolt.sldprt");
            string rReal2=ConvertPlanner.ResolveSolidWorksSource(Path.Combine(input,"Bolt_2.par"),out suf);
            Assert(string.Equals(rReal2,bolt2,StringComparison.OrdinalIgnoreCase)&&!suf,"genuine Bolt_2.sldprt is never stripped to Bolt.sldprt");
            string rAsmSuff=ConvertPlanner.ResolveSolidWorksSource(Path.Combine(input,"Sub_12.asm"),out suf);
            Assert(string.Equals(rAsmSuff,subAsm,StringComparison.OrdinalIgnoreCase)&&suf,"suffixed sub-assembly Sub_12.asm maps back to Sub.sldasm");
            string rNonNum=ConvertPlanner.ResolveSolidWorksSource(Path.Combine(input,"Bolt_A.par"),out suf);
            Assert(rNonNum==null&&!suf,"non-numeric suffix is not stripped");
            string rVirt=ConvertPlanner.ResolveSolidWorksSource(Path.Combine(input,"Virt^Asm.par"),out suf);
            Assert(rVirt==null&&!suf,"virtual component with no disk file returns null");

            // 6. 同名 .sldasm 与 .sldprt 共存同一目录时，ResolveSolidWorksSource 必须按扩展名精确匹配（不把 .asm 错归给 .sldprt）
            string dualAsm=Path.Combine(input,"Relay.sldasm");
            string dualPrt=Path.Combine(input,"Relay.sldprt");
            File.WriteAllText(dualAsm,"x");File.WriteAllText(dualPrt,"x");
            string rDualAsm=ConvertPlanner.ResolveSolidWorksSource(Path.Combine(input,"Relay.asm"),out suf);
            Assert(string.Equals(rDualAsm,dualAsm,StringComparison.OrdinalIgnoreCase)&&!suf,"same-stem .asm resolves to .sldasm");
            string rDualAsmSuf=ConvertPlanner.ResolveSolidWorksSource(Path.Combine(input,"Relay_0.asm"),out suf);
            Assert(string.Equals(rDualAsmSuf,dualAsm,StringComparison.OrdinalIgnoreCase)&&suf,"suffixed same-stem _0.asm resolves to .sldasm");
            string rDualPrt=ConvertPlanner.ResolveSolidWorksSource(Path.Combine(input,"Relay.par"),out suf);
            Assert(string.Equals(rDualPrt,dualPrt,StringComparison.OrdinalIgnoreCase)&&!suf,"same-stem .par resolves to .sldprt");

            // 7. 大型站位目录智能并发 + 同目录大装配串行化 + 重型装配实时内存门控
            var ok160Items=new List<ConvertItem>();
            ok160Items.Add(new ConvertItem{Index=0,Source=Path.Combine(input,"OK160-00.SLDASM"),Root=input,Size=120L*1024*1024});
            for(int i=1;i<=135;i++)ok160Items.Add(new ConvertItem{Index=i,Source=Path.Combine(input,"sub"+i+".SLDASM"),Root=input,Size=1024*1024});
            for(int i=136;i<200;i++)ok160Items.Add(new ConvertItem{Index=i,Source=Path.Combine(input,"p"+i+".SLDPRT"),Root=input,Size=512*1024});
            Assert(ConvertPlanner.SuggestedWorkers(ok160Items,22,8500)==1,"single giant station folder (136 sldasm, 0 step) stays 1 worker");
            ok160Items.Add(new ConvertItem{Index=300,Source=Path.Combine(input,"bom","a.step"),Root=input,Size=500*1024});
            ok160Items.Add(new ConvertItem{Index=301,Source=Path.Combine(input,"bom","b.step"),Root=input,Size=500*1024});
            Assert(ConvertPlanner.SuggestedWorkers(ok160Items,22,8500)==2,"giant station + independent STEP scales to 2 workers on 8.5GB free RAM");
            var multiStation=new List<ConvertItem>{
                new ConvertItem{Index=0,Source=Path.Combine(root,"st1","top1.SLDASM"),Root=root,Size=100L*1024*1024},
                new ConvertItem{Index=1,Source=Path.Combine(root,"st2","top2.SLDASM"),Root=root,Size=100L*1024*1024},
                new ConvertItem{Index=2,Source=Path.Combine(root,"st3","top3.SLDASM"),Root=root,Size=100L*1024*1024},
            };
            Assert(ConvertPlanner.SuggestedWorkers(multiStation,22,8500)==1,"3 giant stations on 8.5GB free RAM cap to 1 worker to avoid swap thrashing");
            Assert(ConvertPlanner.SuggestedWorkers(multiStation,22,16000)==3,"3 giant stations on 16GB free RAM allow 3 workers");

            // 8. 队列同目录大装配串行 + 跳过忙碌目录直接领 STEP / 另一目录 + 重型内存门控
            string qSer=Path.Combine(root,"qser");
            string dirA=Path.Combine(root,"dirA"),dirB=Path.Combine(root,"dirB");
            Directory.CreateDirectory(dirA);Directory.CreateDirectory(dirB);
            var serItems=new List<ConvertItem>{
                new ConvertItem{Index=0,Source=Path.Combine(dirA,"topA.SLDASM"),Root=root,Size=80L*1024*1024},
                new ConvertItem{Index=1,Source=Path.Combine(dirA,"subA.SLDASM"),Root=root,Size=10L*1024*1024},
                new ConvertItem{Index=2,Source=Path.Combine(dirB,"topB.SLDASM"),Root=root,Size=60L*1024*1024},
                new ConvertItem{Index=3,Source=Path.Combine(dirA,"part.STEP"),Root=root,Size=1024*1024},
            };
            ConvertQueue.Write(qSer,serItems,opts);
            ConvertItem s0,s1,s2,s3;string sp0,sp1,sp2,sp3;
            Assert(ConvertQueue.TryClaimNext(qSer,0,out s0,out sp0)&&s0.Index==0,"claims largest heavy assembly topA first");
            // 内存不足 5500 MB 时：dirA 的 subA 因同目录串行跳过，dirB 的重型 topB 因内存门控跳过，立即领取独立 STEP（#3）！
            ConvertQueue.FreeMemoryProvider=delegate(){ return 3000L; };
            try{
                Assert(ConvertQueue.TryClaimNext(qSer,1,out s1,out sp1)&&s1.Index==3,"skips busy dirA & heavy topB under low RAM, immediately claims STEP");
                ConvertQueue.Complete(sp1);
                // 内存充足时：dirA 仍忙（跳过 subA），但 dirB 的重型 topB 立即放行！
                ConvertQueue.FreeMemoryProvider=delegate(){ return 12000L; };
                Assert(ConvertQueue.TryClaimNext(qSer,1,out s2,out sp2)&&s2.Index==2,"allows heavy topB in another dir when free RAM >= 5500MB (still skipping busy dirA subA)");
                ConvertQueue.Complete(sp0);
                ConvertQueue.Complete(sp2);
                Assert(ConvertQueue.TryClaimNext(qSer,0,out s3,out sp3)&&s3.Index==1,"claims dirA subA once topA finishes");
                ConvertQueue.Complete(sp3);
            } finally { ConvertQueue.FreeMemoryProvider=null; }

            // 9. StageFolder 不越界爬出含零件的站位目录 + ParseMissingFromTranslatorLog + StageCanSupplyMissing
            string stRoot=Path.Combine(root,"lineup");
            string stSelf=Path.Combine(stRoot,"OK160"),stOther=Path.Combine(stRoot,"OK070");
            Directory.CreateDirectory(stSelf);Directory.CreateDirectory(stOther);
            string selfAsm=Path.Combine(stSelf,"OK160-00.SLDASM");
            File.WriteAllText(selfAsm,"x");
            File.WriteAllText(Path.Combine(stSelf,"p1.sldprt"),"x");
            File.WriteAllText(Path.Combine(stOther,"other.sldprt"),"x");
            Assert(ConvertPlanner.StageFolder(selfAsm,stSelf)==null,"self-contained station folder with .sldprt does not climb to parent");
            string logPath=Path.Combine(stSelf,"OK160-00.log");
            File.WriteAllText(logPath,"Warn: The body C:\\x\\missing_a.SLDPRT miss file\r\nWarn: The body C:\\x\\other.sldprt miss file\r\n",Encoding.UTF8);
            var parsedMiss=ConvertPlanner.ParseMissingFromTranslatorLog(selfAsm);
            Assert(parsedMiss.Count==2&&parsedMiss[0]=="missing_a.SLDPRT"&&parsedMiss[1]=="other.sldprt","translator log missing files parsed");
            Assert(!ConvertPlanner.StageCanSupplyMissing(selfAsm,stRoot,new[]{"missing_a.SLDPRT"}),"StageCanSupplyMissing=false when missing file does not exist anywhere in root");
            Assert(ConvertPlanner.StageCanSupplyMissing(selfAsm,stRoot,parsedMiss),"StageCanSupplyMissing=true when a missing file exists in another subfolder of root");

            // 10. 无会话跨进程跳过校验（ContainsAnyOrSource）+ 等待重型装配回调触发（用于释放空闲 CAD 内存）
            string idxRoot=Path.Combine(root,"idxtest");
            string idxOut=Path.Combine(idxRoot,"out.par"),idxSrc=Path.Combine(idxRoot,"src.sldprt");
            Assert(!ConvertWrittenIndex.ContainsAnyOrSource(idxRoot,new[]{idxOut},idxSrc),"ContainsAnyOrSource initially false");
            ConvertWrittenIndex.MarkSource(idxRoot,idxSrc);
            Assert(ConvertWrittenIndex.ContainsAnyOrSource(idxRoot,new[]{idxOut},idxSrc),"ContainsAnyOrSource hits source index without live CAD session");
            string qHeavy=Path.Combine(root,"qheavy");
            var hItems=new List<ConvertItem>{
                new ConvertItem{Index=0,Source=Path.Combine(dirA,"giant.SLDASM"),Root=root,Size=90L*1024*1024},
                new ConvertItem{Index=1,Source=Path.Combine(dirA,"child.SLDPRT"),Root=root,Size=512*1024},
            };
            ConvertQueue.Write(qHeavy,hItems,opts);
            ConvertItem h0;string hp0;
            Assert(ConvertQueue.TryClaimNext(qHeavy,0,out h0,out hp0)&&h0.Index==0,"heavy assembly claimed first");
            int heavyCallbacks=0;
            ConvertItem h1;string hp1;
            var releaser=new System.Threading.Thread(delegate(){ System.Threading.Thread.Sleep(450); ConvertQueue.Complete(hp0); });
            releaser.Start();
            Assert(ConvertQueue.TryClaimNext(qHeavy,1,out h1,out hp1,delegate(int sec){ heavyCallbacks++; })&&h1.Index==1,"part claimed after heavy assembly finishes");
            releaser.Join();
            ConvertQueue.Complete(hp1);
            Assert(heavyCallbacks>0,"onDeferredBehindHeavy callback fires while waiting behind active heavy assembly");
        } finally { try{ Directory.Delete(root,true); }catch{} }
    }

    // A pending marker left by a failed or interrupted run must force a re-conversion even though
    // the top-level file already exists (it may be incomplete).
    public static void ResumeMarker(){
        string root=Path.Combine(Path.GetTempPath(),"fmtresume-"+Guid.NewGuid().ToString("N"));
        string input=Path.Combine(root,"in"),output=Path.Combine(root,"out");
        Directory.CreateDirectory(Path.Combine(input,"sub"));Directory.CreateDirectory(output);
        try{
            var options=new ConvertOptions();options.Inputs.Add(input);options.OutputRoot=output;
            var asm=new ConvertItem();asm.Source=Path.Combine(input,"Foo.SLDASM");asm.Root=input;
            var prt=new ConvertItem();prt.Source=Path.Combine(input,"Foo.SLDPRT");prt.Root=input;
            string top=ConvertPlanner.TopTarget(asm,options);
            File.WriteAllText(top,"half written");
            Assert(ConvertPlanner.IsAlreadyConverted(asm,options),"existing output without a marker still counts as converted (old runs keep resuming)");
            ConvertPlanner.MarkPending(asm,options);
            string marker=ConvertPlanner.PendingMarker(asm,options);
            Assert(File.Exists(marker)&&Path.GetFileName(marker)=="Foo.SLDASM"+ConvertPlanner.PendingSuffix,"marker sits next to the output: "+marker);
            Assert(!ConvertPlanner.IsAlreadyConverted(asm,options),"a pending marker forces re-conversion although the top file exists");
            Assert(ConvertPlanner.PendingMarker(prt,options)!=marker,"Foo.SLDASM and Foo.SLDPRT in one folder get separate markers");
            ConvertPlanner.ClearPending(asm,options);
            Assert(!File.Exists(marker)&&ConvertPlanner.IsAlreadyConverted(asm,options),"clearing the marker after success restores the skip");
            ConvertPlanner.ClearPending(asm,options);
            Assert(true,"clearing a missing marker is harmless");

            var step=new ConvertItem();step.Source=Path.Combine(input,"sub","Bar.step");step.Root=input;
            ConvertPlanner.MarkPending(step,options);
            Assert(File.Exists(Path.Combine(output,"sub","Bar.step"+ConvertPlanner.PendingSuffix)),"marker creates the mirrored sub-folder; name does not depend on the STEP result type");
            File.WriteAllText(ConvertPlanner.TopTargetFor(step,options,".asm"),"x");
            Assert(!ConvertPlanner.IsAlreadyConverted(step,options),"pending STEP item is re-converted whatever it imported as");
            options.FlatOutput=true;
            Assert(Path.GetDirectoryName(ConvertPlanner.PendingMarker(step,options))==Path.GetFullPath(output),"flat output keeps the marker in the output root");
        } finally { try{ Directory.Delete(root,true); }catch{} }
    }

    // Worker arguments must survive CommandLineToArgvW. The old "\""+path+"\"" broke on D:\ .
    public static void CommandLineQuoting(){
        string naive="--worker \"C:\\job.txt\" \"C:\\status.txt\" \"D:\\\" 1 0 0 \"C:\\work\"";
        Assert(ParseArgv(naive).Count!=8,"old quoting of a drive root shifts the arguments (the original bug): "+string.Join(" | ",ParseArgv(naive).ToArray()));
        var cases=new List<string[]>{
            new[]{"--worker",@"C:\job dir\job-0.txt",@"C:\status.txt",@"D:\","1","0","0",@"C:\work"},
            new[]{"--input",@"C:\My Files\"},
            new[]{@"C:\a b\\",@"C:\x",""},
            new[]{"a\"b",@"back\\""slash","tab\tin",@"\\server\share\dir\"},
            new[]{@"D:\项目 图纸\装配\","plain"},
        };
        foreach(string[] args in cases){
            string line=CommandLine.Join(args);
            var parsed=ParseArgv(line);
            bool same=parsed.Count==args.Length;
            for(int i=0;same&&i<args.Length;i++)same=parsed[i]==args[i];
            Assert(same,"round trip ["+string.Join(" | ",args)+"] via "+line+" -> ["+string.Join(" | ",parsed.ToArray())+"]");
        }
        Assert(CommandLine.Quote(@"D:\")==@"D:\","a path without spaces is left as is");
        Assert(CommandLine.Quote(@"C:\My Files\")=="\"C:\\My Files\\\\\"","trailing backslash is doubled before the closing quote");
    }

    // Reference splitter following the documented CommandLineToArgvW / MSVCRT rules:
    // 2n backslashes + quote -> n backslashes and a quote delimiter; 2n+1 -> n backslashes and a literal
    // quote; backslashes not followed by a quote are literal; "" inside quotes is a literal quote.
    static List<string> ParseArgv(string line){
        var args=new List<string>();int i=0;
        while(true){
            while(i<line.Length&&(line[i]==' '||line[i]=='\t'))i++;
            if(i>=line.Length)break;
            var sb=new StringBuilder();bool quoted=false;
            while(i<line.Length){
                char c=line[i];
                if(c=='\\'){
                    int n=0;while(i<line.Length&&line[i]=='\\'){ n++;i++; }
                    if(i<line.Length&&line[i]=='"'){ sb.Append('\\',n/2);if(n%2==1){ sb.Append('"');i++; } }
                    else sb.Append('\\',n);
                    continue;
                }
                if(c=='"'){
                    if(quoted&&i+1<line.Length&&line[i+1]=='"'){ sb.Append('"');i+=2;continue; }
                    quoted=!quoted;i++;continue;
                }
                if(!quoted&&(c==' '||c=='\t'))break;
                sb.Append(c);i++;
            }
            args.Add(sb.ToString());
        }
        return args;
    }
}
