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
        // 装配连同文件夹拷走以后：旁边同名的 .lineup.xml 照样能打开，路径改记成现在的；别的文件里记着别的装配照样拒绝
        var moved=Path.Combine(Path.GetTempPath(),"lineup-moved-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(moved);
        var copyAsm=Path.Combine(moved,"fixture.asm");var copyXml=Path.ChangeExtension(copyAsm,".lineup.xml");p.Save(copyXml);
        var rebased=LineupProject.Load(copyXml,copyAsm);Assert(rebased.Records.Count==3&&rebased.AssemblyFile==Path.GetFullPath(copyAsm),"copied project folder: lineup data opens and follows the assembly");
        bool refused=false;try{LineupProject.Load(copyXml,Path.Combine(moved,"other.asm"));}catch(InvalidDataException){refused=true;}Assert(refused,"lineup data of another assembly is still refused");
        Directory.Delete(moved,true);
    }
}

