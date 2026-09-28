using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using TianGongCadSuite;

static class FormatConvertTests {
    static void Assert(bool ok,string label){ if(!ok)throw new Exception("FAIL: "+label); Console.WriteLine("PASS: "+label); }
    public static void Pure(){
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
            var outside=ConvertPlanner.ComponentTarget(@"D:\elsewhere\q.par","q.par",sample,options);
            Assert(outside.IndexOf("_外部",StringComparison.Ordinal)>=0,"component outside the root is bucketed: "+outside);

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
            Assert(occupied==1,"files from one input folder never run in two workers at once: "+occupied);
            Assert(ConvertPlanner.SuggestedWorkers(0,16,8192)==1,"no files means one worker");
            Assert(ConvertPlanner.SuggestedWorkers(1000,64,65536)==1,"sequential is the measured default");

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
            Assert(csv.IndexOf("\"a,b\"",StringComparison.Ordinal)>=0,"CSV quotes embedded commas");
            Assert(csv.Split('\n').Length>=5,"CSV has a header and one line per row");
        } finally { try{ Directory.Delete(root,true); }catch{} }
    }
}
