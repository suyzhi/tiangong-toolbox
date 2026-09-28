using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace TianGongCadSuite {
    // Head-less worker: one process = one hidden CAD session = one slice of the work list.
    // Status is appended to a tab-separated file after every event so the UI can tail it live and a
    // crash still leaves a complete record.
    public static class ConvertWorker {
        public static int Run(string[] args){
            // --worker <jobFile> <statusFile> <outputRoot> <flat> <verify> <force> [lockRoot]
            if(args.Length<7)return 2;
            string jobFile=args[1];string statusFile=args[2];string outputRoot=args[3];
            var options=new ConvertOptions();
            options.OutputRoot=outputRoot;
            options.FlatOutput=args[4]=="1";
            options.VerifyReopen=args[5]=="1";
            options.Force=args[6]=="1";
            string lockRoot=args.Length>7?args[7]:null;
            int exit=0;
            CadConvertSession session=null;
            try{
                using(new OleFilter()){
                    session=new CadConvertSession(delegate(string message){ ConvertLog.Append(statusFile,ConvertProtocol.Line("LOG",message)); },lockRoot);
                    ConvertLog.Append(statusFile,ConvertProtocol.Line(ConvertProtocol.Ready,ConvertProtocol.Number(session.BootSeconds),session.ProcessId.ToString()));
                    foreach(string raw in File.ReadAllLines(jobFile)){
                        if(raw.Trim().Length==0)continue;
                        string[] parts=raw.Split('\t');
                        if(parts.Length<3)continue;
                        var item=new ConvertItem();
                        item.Index=int.Parse(parts[0]);
                        item.Source=parts[1];
                        item.Root=parts[2];
                        item.Worker=parts.Length>3?int.Parse(parts[3]):0;
                        try{ item.Size=new FileInfo(item.Source).Length; }catch{ item.Size=0; }
                        item.Target=ConvertPlanner.TopTarget(item,options);
                        if(!options.Force&&ConvertPlanner.IsAlreadyConverted(item,options)){
                            ConvertLog.Append(statusFile,ConvertProtocol.Line(ConvertProtocol.End,item.Index.ToString(),"skip",item.Source,"0","0","0","0",item.Target,item.Worker.ToString(),"输出已存在"));
                            continue;
                        }
                        ConvertLog.Append(statusFile,ConvertProtocol.Line(ConvertProtocol.Begin,item.Index.ToString(),item.Source));
                        ConvertRow row=session.Convert(item,options);
                        if(row.Status=="fail"){
                            // One retry: transient translator/save conflicts are recoverable, and a failed
                            // document would otherwise leave a half-converted assembly behind.
                            ConvertLog.Append(statusFile,ConvertProtocol.Line("LOG","重试 "+item.Source));
                            row=session.Convert(item,options);
                        }
                        if(row.Error==null)row.Error="";
                        ConvertLog.Append(statusFile,ConvertProtocol.Line(ConvertProtocol.End,row.Index.ToString(),row.Status,row.Source,
                            ConvertProtocol.Number(row.OpenSeconds),ConvertProtocol.Number(row.SaveSeconds),ConvertProtocol.Number(row.TotalSeconds),
                            row.Documents.ToString(),row.Target==null?"":row.Target,row.Worker.ToString(),row.Error));
                    }
                }
            }catch(Exception e){
                exit=1;
                try{ if(statusFile!=null)ConvertLog.Append(statusFile,ConvertProtocol.Line("FATAL",e.GetType().Name+": "+e.Message)); }catch{}
            }finally{
                if(session!=null)try{ session.Dispose(); }catch{}
            }
            try{ ConvertLog.Append(statusFile,ConvertProtocol.Line(ConvertProtocol.Exit,exit.ToString())); }catch{}
            return exit;
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
