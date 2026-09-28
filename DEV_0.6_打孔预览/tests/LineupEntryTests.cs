using System;
using System.IO;
using System.Linq;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using TianGongCadSuite;
using F=SolidEdgeFramework;
using A=SolidEdgeAssembly;
static class LineupEntryTests {
    static void Check(bool ok,string label){if(!ok)throw new Exception("FAIL ENTRY: "+label);Console.WriteLine("PASS ENTRY: "+label);}
    static void Reject(Action action,string label){try{action();}catch(InvalidOperationException){Check(true,label);return;}catch(InvalidDataException){Check(true,label);return;}throw new Exception("Expected rejection: "+label);}
    static LineupRecord Pick(string key){return new LineupRecord{ReferenceKey=key,SourceFile="fixture.par",InstancePath=new[]{key}};}
    static LineupEntryPreset Cylinder(){return new LineupEntryPreset{Category="气缸",Model="CYL-A",Brand="SMC",Description="夹紧",Mechanism="测试机构",Home="伸出",Mounting="水平",GenerateSensors=true};}
    public static void Pure(string output){
        Directory.CreateDirectory(output);var p=new LineupProject{AssemblyFile=Path.Combine(output,"EntryFixture.asm")};LineupNumbering.SetIsland(p,"ENTRY",1,"ISLAND-MODEL");
        var preset=Cylinder();var batch=LineupEntry.Create(p,preset,3,null);
        Check(p.Records.Count==0&&batch.Project.Records.Count==9&&batch.SensorCount==6,"batch creates cylinders and H/W without mutating source");
        Check(batch.Project.Records.Where(r=>r.Category=="气缸").All(r=>r.Model=="CYL-A"&&r.Home=="伸出"&&r.Mounting=="水平"&&r.Address==""&&r.ReferenceKey==""),"batch reuses common fields without IO or model bindings");
        Check(batch.Project.Records.Where(r=>r.Category=="气缸传感器").All(r=>r.Model==""&&r.CylinderId.Length>0),"sensor pairs retain separate specifications and stable parents");
        Check(batch.Project.EntryPresets.Count==1&&p.EntryPresets.Count==0,"preset memory is committed only with a successful batch");
        batch.Project.Save(Path.Combine(output,"presets.xml"));var loaded=LineupProject.Load(Path.Combine(output,"presets.xml"),p.AssemblyFile);
        Check(loaded.EntryPresets.Single().Mounting=="水平"&&loaded.EntryPresets.Single().GenerateSensors,"entry presets persist and reopen");
        var valves=LineupEntry.Create(p,new LineupEntryPreset{Category="阀片",Model="VALVE"},2,null);var owner=valves.Project.Records.Single(r=>r.Category=="阀岛");
        Check(valves.Project.Records.Count==3&&valves.Project.Records.Where(r=>r.Category=="阀片").All(r=>r.IslandId==owner.Id)&&owner.Model=="ISLAND-MODEL","valve batch creates exactly one explicit island parent");
        preset.ParentId=valves.AddedIds[0];var family=LineupEntry.Create(valves.Project,preset,2,null);
        Check(family.Project.Validate().Count==0&&family.Project.Records.Any(r=>r.Number=="ENTRY-Y1C01-2W"),"cylinder batch follows valve numbering with valid full hierarchy");
        Check(family.Project.Records.Where(r=>r.Category=="气缸传感器").All(r=>r.ValveId==preset.ParentId&&r.IslandId==owner.Id),"generated sensors inherit valve and island IDs");
        var cylinder=family.Project.Records.First(r=>r.Category=="气缸");cylinder.Address="MANUAL-IO";cylinder.Notes="manual note";cylinder.ReferenceKey="existing";
        var extended=LineupEntry.Create(family.Project,preset,1,new[]{Pick("new")});var kept=extended.Project.Records.Single(r=>r.Id==cylinder.Id);
        Check(kept.Address=="MANUAL-IO"&&kept.Notes=="manual note"&&kept.ReferenceKey=="existing","expanding a valve family preserves existing manual fields and binding");
        var duplicated=LineupEntry.Create(extended.Project,preset,100,new[]{Pick("new"),Pick("fresh"),Pick("fresh")});
        Check(duplicated.AddedIds.Count==1&&duplicated.Skipped==2,"selected models determine batch count and skip duplicate instance keys");
        var noOp=LineupEntry.Create(duplicated.Project,preset,1,new[]{Pick("new")});Check(noOp.AddedIds.Count==0&&noOp.Project.Records.Count==duplicated.Project.Records.Count,"duplicate-only selection is a no-op");
        var bad=preset.Copy();bad.ParentId="missing";Reject(()=>LineupEntry.Create(p,bad,1,null),"stale parent is rejected atomically");
        Reject(()=>LineupEntry.Create(new LineupProject(),Cylinder(),1,null),"missing prefix is rejected before adding records");
        Reject(()=>LineupEntry.Create(p,preset,0,null),"invalid quantity rejected");
        var sensor=new LineupEntryPreset{Category="气缸传感器",ParentId=cylinder.Id,Position="H"};Reject(()=>LineupEntry.Create(family.Project,sensor,1,null),"existing H position cannot be duplicated");
        var known=new LineupProject();known.Records.Add(new LineupRecord{Category="普通传感器",Number="SE01",SourceFile="fixture.par",Model="SENSOR",Brand="B",Connector="M8",Address="IO",Description="original",ReferenceKey="one"});
        var reused=LineupEntry.Create(known,new LineupEntryPreset{Category="普通传感器"},1,new[]{Pick("two")}).Project.Records.Last();
        Check(reused.Model=="SENSOR"&&reused.Brand=="B"&&reused.Connector=="M8"&&reused.Address==""&&reused.Description=="","same-file repeat uses unique known specification only");
        known.Records.Add(new LineupRecord{Category="普通传感器",Number="SE02",SourceFile="fixture.par",Model="OTHER",ReferenceKey="three"});
        Check(LineupEntry.Create(known,new LineupEntryPreset{Category="普通传感器"},1,new[]{Pick("four")}).Project.Records.Last().Model=="","conflicting known specifications are not guessed");
        var blank=new LineupRecord{Id="blank"};known.Records.Add(blank);
        Check(LineupEntry.NextUnbound(known,new[]{known.Records[0].Id,known.Records[1].Id,"blank"},known.Records[0].Id)=="blank","continuous queue skips bound records");
        Check(LineupEntry.NextUnbound(known,new[]{"blank"},"blank")==null,"queue completion has no wraparound rebind");
    }
    static void Pump(int milliseconds){var end=DateTime.UtcNow.AddMilliseconds(milliseconds);while(DateTime.UtcNow<end){Application.DoEvents();Thread.Sleep(20);}}
    static void Wait(Func<bool> condition,string label){var end=DateTime.UtcNow.AddSeconds(12);while(!condition()&&DateTime.UtcNow<end)Pump(60);Check(condition(),label);}
    static void Snapshot(LineupTableForm form,string path){using(var bmp=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bmp,new Rectangle(0,0,form.Width,form.Height));bmp.Save(path);}}
    public static void Reopen(F.Application app,string output){
        string file=Path.Combine(output,"EntryFixture.asm");A.AssemblyDocument doc=null;
        try{doc=(A.AssemblyDocument)app.Documents.Open(file);using(var form=new LineupTableForm(app,doc,Path.Combine(output,"entry-models.xml"))){form.Show();Pump(100);var p=form.CurrentProject;Check(p.Records.Count==11&&p.EntryPresets.Single(r=>r.Category=="气缸").Model=="CYL-A","fresh CAD process reloads complete entry project and preset");foreach(var r in p.Records.Where(r=>r.ReferenceKey.Length>0))Check(LineupCad.ResolveTarget(doc,r)!=null,"fresh process resolves entry binding "+r.Number);Check(!form.ContinuousActive,"reopened project never starts automatic recording without user action");Snapshot(form,Path.Combine(output,"entry-fresh-process.png"));form.Close();}}finally{if(doc!=null)doc.Close(false);}
    }
    public static void Native(F.Application app,string output){
        Pure(output);A.AssemblyDocument doc=null;LineupTableForm form=null;
        try{
            string file=Path.Combine(output,"EntryFixture.asm"),part=Path.Combine(output,"EntryPart.par"),library=Path.Combine(output,"entry-models.xml");
            doc=(A.AssemblyDocument)app.Documents.Add("SolidEdge.AssemblyDocument");doc.SaveAs(file);
            var spec=new PanelSpec{Width=.2,Height=.1,Thickness=.006,Origin=new V3(),U=new V3(1,0,0),V=new V3(0,1,0),N=new V3(0,0,1)};
            var one=CadBuilder.Generate(app,doc,spec,part);var two=doc.Occurrences.AddByFilename(part);var three=doc.Occurrences.AddByFilename(part);var four=doc.Occurrences.AddByFilename(part);var five=doc.Occurrences.AddByFilename(part);doc.Save();
            form=new LineupTableForm(app,doc,library);form.Show();Pump(100);form.ConfigureNumbering("ENTRY",1,"ISLAND");form.SetCategory("气缸");form.WriteEntry(Cylinder());
            Check(form.QuickEntry(false)==1&&form.RecordCount==3,"one entry action creates numbered cylinder and H/W");form.Undo();Check(form.RecordCount==0,"single undo removes the complete generated family");
            doc.SelectSet.Add(one);doc.SelectSet.Add(two);Check(form.QuickEntry(false)==2&&form.RecordCount==6&&doc.SelectSet.Count==0,"native multi-selection binds two instances and clears selection");
            Check(form.CurrentProject.Records.Where(r=>r.Category=="气缸").Select(r=>r.ReferenceKey).Distinct().Count()==2,"identical source parts retain distinct native instance keys");
            doc.SelectSet.Add(one);doc.SelectSet.Add(three);Check(form.QuickEntry(false)==1&&form.RecordCount==9,"mixed native selection skips existing instance and creates only new family");
            Snapshot(form,Path.Combine(output,"entry-form.png"));form.StartContinuous("add");
            doc.SelectSet.Add(one);Pump(1100);Check(form.RecordCount==9&&form.ContinuousActive,"continuous duplicate keeps current entry and creates nothing");
            doc.SelectSet.RemoveAll();doc.SelectSet.Add(four);Wait(()=>form.RecordCount==12,"timer automatically captures new native selection");Pump(1000);Check(form.RecordCount==12&&doc.SelectSet.Count==0,"stable selection is not recorded twice");
            Snapshot(form,Path.Combine(output,"entry-continuous.png"));form.Undo();Check(!form.ContinuousActive&&form.RecordCount==9,"undo stops continuous recording and reverts entire batch");form.SetPickingMode(false);
            var unbound=Cylinder();unbound.GenerateSensors=false;form.WriteEntry(unbound);form.QuickEntry(false);string first=form.CurrentProject.Records.Last().Id;form.QuickEntry(false);string last=form.CurrentProject.Records.Last().Id;
            form.Grid.CurrentCell=form.Grid.Rows[3].Cells[1];Check(form.BindingRecordId==first,"continuous bind starts from selected pending row");form.StartContinuous("bind");
            doc.SelectSet.Add(one);Pump(1100);Check(form.BindingRecordId==first&&form.CurrentProject.Records.Single(r=>r.Id==first).ReferenceKey=="","duplicate does not advance the pending binding row");
            doc.SelectSet.RemoveAll();doc.SelectSet.Add(four);Wait(()=>form.BindingRecordId==last,"timer binds and advances to next unbound row");
            doc.SelectSet.Add(five);Wait(()=>!form.ContinuousActive,"last pending row automatically stops continuous mode");
            Check(form.CurrentProject.Records.Single(r=>r.Id==last).ReferenceKey.Length>0&&doc.SelectSet.Count==0,"last row binding saved and selection cleared");
            form.SetPickingMode(false);Check(form.Grid.Columns["原位"].Visible&&form.Grid.Columns["安装方式"].Visible,"home and mounting remain visible in simplified table");
            form.Close();form.Dispose();form=null;doc.Close(false);doc=null;doc=(A.AssemblyDocument)app.Documents.Open(file);form=new LineupTableForm(app,doc,library);form.Show();Pump(100);
            Check(form.RecordCount==11&&form.CurrentProject.EntryPresets.Single(r=>r.Category=="气缸").Model=="CYL-A","close/reopen retains all batches and remembered specifications");
            foreach(var r in form.CurrentProject.Records.Where(r=>r.ReferenceKey.Length>0))Check(LineupCad.ResolveTarget(doc,r)!=null,"reopened entry resolves native model "+r.Number);
            Snapshot(form,Path.Combine(output,"entry-reopened.png"));
        }finally{if(form!=null)form.Dispose();if(doc!=null)doc.Close(false);}
    }
}
