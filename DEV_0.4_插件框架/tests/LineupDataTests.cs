using System;
using System.IO;
using System.Linq;
using TianGongCadSuite;
static class LineupDataTests {
    static void Assert(bool ok,string message){if(!ok)throw new Exception("FAIL: "+message);Console.WriteLine("PASS: "+message);}
    public static void Pure(){
        var p=new LineupProject{AssemblyFile=Path.Combine(Path.GetTempPath(),"fixture.asm")};
        p.Records.Add(new LineupRecord{Number="Y1V01",Category="阀片",IslandId="island"});
        p.Records.Add(new LineupRecord{Number="Y1C01",Category="气缸",ValveId=p.Records[0].Id});
        Assert(p.Validate().Count==1,"invalid island link detected");
        p.Records[0].Category="阀片";var island=new LineupRecord{Number="Y1",Category="阀岛"};p.Records.Add(island);p.Records[0].IslandId=island.Id;Assert(p.Validate().Count==0,"valid valve relationship");
        var csv=LineupCsv.Write(new[]{new[]{"编号","描述"},new[]{"A,1","含\"引号\""}});var rows=LineupCsv.Read(csv);Assert(rows.Count==2&&rows[1][0]=="A,1"&&rows[1][1]=="含\"引号\"","quoted CSV round trip");
        var file=Path.Combine(Path.GetTempPath(),"lineup-"+Guid.NewGuid().ToString("N")+".xml");p.Save(file);var loaded=LineupProject.Load(file,p.AssemblyFile);Assert(loaded.Records.Count==3,"project persistence");File.Delete(file);File.Delete(file+".bak");
    }
}

