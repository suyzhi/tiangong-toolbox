using System;
using System.IO;
using System.Linq;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using TianGongCadSuite;
using F=SolidEdgeFramework;
using A=SolidEdgeAssembly;
static class LineupHierarchyTests {
    static void Check(bool value,string name){if(!value)throw new Exception(name);Console.WriteLine("PASS HIERARCHY: "+name);}
    public static void Pure(string output){
        Directory.CreateDirectory(output);var p=new LineupProject{AssemblyFile=Path.GetFullPath(Path.Combine(output,"test.asm"))};
        var v=new LineupRecord{Category="阀片",Number="OK170-Y2V01"};var a=new LineupRecord{Category="气缸",Number="OLD1"};var b=new LineupRecord{Category="气缸",Number="OLD2"};p.Records.AddRange(new[]{v,a,b});
        LineupNumbering.SensorPairs(p,new[]{a.Id,b.Id});LineupHierarchy.AssignCylinders(p,new[]{a.Id,b.Id},v.Id);
        Check(a.Number=="OK170-Y2C01-1"&&b.Number=="OK170-Y2C01-2","shared valve suffixes");
        Check(p.Records.Any(r=>r.Number=="OK170-Y2C01-2W"),"sensor cascade");v.Number="OK170-Y3V04";LineupHierarchy.RenumberChildren(p,v);
        Check(a.Number=="OK170-Y3C04-1"&&p.Records.Any(r=>r.Number=="OK170-Y3C04-1H"),"island and valve cascade");
        string key=a.Id;p.Save(Path.Combine(output,"hierarchy.xml"));var copy=LineupProject.Load(Path.Combine(output,"hierarchy.xml"),p.AssemblyFile);Check(copy.Records.Single(r=>r.Id==key).ValveId==v.Id,"relations survive XML reload");
        LineupHierarchy.Move(p,new[]{a.Id,b.Id},new[]{b.Id},-1);Check(p.Records[0]==v&&p.Records[1]==b&&p.Records[2]==a,"filtered reorder preserves hidden rows");
        using(var yellow=new Bitmap(100,80))using(var magenta=new Bitmap(100,80)){
            using(var g=Graphics.FromImage(yellow)){g.Clear(Color.White);g.FillRectangle(Brushes.Yellow,60,20,10,10);}using(var g=Graphics.FromImage(magenta)){g.Clear(Color.White);g.FillRectangle(Brushes.Magenta,60,20,10,10);}
            var point=LineupLocation.Detect(yellow,magenta);Check(point.State=="ok"&&point.X>.60&&point.X<.70&&point.Y>.25&&point.Y<.375,"mask coordinates normalized and inside target");
            Check(LineupLocation.Detect(yellow,yellow).State=="unresolved","missing highlight does not fabricate point");
        }
    }
    public static void Native(string output){
        Pure(output);var app=(F.Application)Activator.CreateInstance(Type.GetTypeFromProgID("SolidEdge.Application"));
        if(app.Documents.Count!=0)throw new Exception("Independent CAD is not empty; not touching documents.");
        app.Visible=true;A.AssemblyDocument doc=null;LineupTableForm form=null;
        try{
            doc=(A.AssemblyDocument)app.Documents.Add("SolidEdge.AssemblyDocument");string file=Path.GetFullPath(Path.Combine(output,"LocationFixture.asm"));doc.SaveAs(file);
            var occurrence=CadBuilder.Generate(app,doc,new PanelSpec{Width=.2,Height=.1,Thickness=.006,Origin=new V3(),U=new V3(1,0,0),V=new V3(0,1,0),N=new V3(0,0,1)},Path.GetFullPath(Path.Combine(output,"LocationPart.par")));doc.Save();
            var p=new LineupProject{AssemblyFile=file,NumberPrefix="TEST"};var row=new LineupRecord{Number="TEST-Y1C01",Category="气缸",Model="TEST-PANEL"};LineupCad.CaptureOccurrence(occurrence,row);p.Records.Add(row);p.Save(Path.ChangeExtension(file,".lineup.xml"));
            dynamic view=((dynamic)app.ActiveWindow).View;view.Fit();view.Update();
            var pack=LineupLocation.Export(app,doc,p,new[]{row.Id},Path.GetFullPath(Path.Combine(output,"location-package")));Check(pack.Points[0].State=="ok","real CAD double highlight recognized");
            form=new LineupTableForm(app,doc,Path.Combine(output,"library.xml"));form.Show();Application.DoEvents();form.Grid.CurrentCell=form.Grid.Rows[0].Cells[1];form.InsertAt(true);Check(form.RecordCount==2,"native form inserts row");form.MoveRows(-1);Check(form.CurrentProject.Records[0].Number!="TEST-Y1C01","native form row reorder");form.Undo();Check(form.CurrentProject.Records[0].Number=="TEST-Y1C01","undo restores order");
            form.SetCategory("阀片");form.AddEmpty();string valveId=form.CurrentProject.Records.Single(r=>r.Category=="阀片").Id;
            var cylinders=form.CurrentProject.Records.Where(r=>r.Category=="气缸").Select(r=>r.Id).ToArray();form.AssignCylindersToValve(cylinders,valveId);form.GenerateSensorPairsFor(cylinders);
            form.ConfigureNumbering("TEST",3,"ISLAND-3");form.AssignValvesToIsland("TEST-Y3",new[]{valveId});
            Check(form.CurrentProject.Records.Where(r=>r.Category=="气缸").All(r=>r.Number.StartsWith("TEST-Y3C01-")&&r.ValveId==valveId),"native island reassignment cascades to cylinders");
            Check(form.CurrentProject.Records.Where(r=>r.Category=="气缸传感器").All(r=>r.Number.StartsWith("TEST-Y3C01-")&&!string.IsNullOrEmpty(r.IslandId)),"native island reassignment cascades to sensors");
            Check(form.CurrentProject.Validate().Count==0,"native hierarchy links valid");form.SetCategory("气缸");Application.DoEvents();
            using(var bmp=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bmp,new Rectangle(0,0,form.Width,form.Height));bmp.Save(Path.Combine(output,"hierarchy-form.png"));}
        }finally{if(form!=null)form.Dispose();if(doc!=null)doc.Close(false);app.Quit();}
    }
}
