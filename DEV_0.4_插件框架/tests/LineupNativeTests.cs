using System;
using System.IO;
using System.Linq;
using System.Drawing;
using System.Security.Cryptography;
using System.Windows.Forms;
using TianGongCadSuite;
using F=SolidEdgeFramework;
using A=SolidEdgeAssembly;
static class LineupNativeTests {
    static void Check(bool ok,string label){if(!ok)throw new Exception("FAIL LINEUP: "+label);Console.WriteLine("PASS LINEUP: "+label);}
    static void Reject(Action action,string label){try{action();}catch(InvalidOperationException){Check(true,label);return;}throw new Exception("Expected rejection: "+label);}
    static string Hash(string file){using(var s=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(s));}
    public static void Run(F.Application app,string output){
        if(app==null)throw new InvalidOperationException("Lineup native tests must run inside the registered CAD add-in.");
        string dir=Path.Combine(Path.GetFullPath(output),"lineup-"+DateTime.Now.ToString("yyyyMMdd-HHmmss"));Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(output,"latest-lineup.txt"),dir);
        string file=Path.Combine(dir,"LineupFixture.asm"),partFile=Path.Combine(dir,"LineupPart.par");
        A.AssemblyDocument doc=null;LineupForm form=null;
        try{
            doc=(A.AssemblyDocument)app.Documents.Add("SolidEdge.AssemblyDocument");
            Reject(()=>LineupCad.AssemblyFile(doc),"unsaved assembly rejected");
            doc.SaveAs(file);
            Check(LineupCad.AssemblyFile(doc)==file,"FullName includes filename; separate assembly sidecar");
            Console.WriteLine("CAD Path="+doc.Path+" FullName="+doc.FullName);
            var spec=new PanelSpec{Width=.2,Height=.1,Thickness=.006,Origin=new V3(),U=new V3(1,0,0),V=new V3(0,1,0),N=new V3(0,0,1)};
            var one=CadBuilder.Generate(app,doc,spec,partFile);var two=doc.Occurrences.AddByFilename(partFile);
            Array matrix=Transform.Frame(new V3(.5,0,0),new V3(1,0,0),new V3(0,1,0),new V3(0,0,1)).M;two.PutMatrix(ref matrix,true);doc.Save();
            string partHash=Hash(partFile),asmHash=Hash(file);File.WriteAllText(Path.Combine(dir,"model-sha256-before.txt"),file+" "+asmHash+Environment.NewLine+partFile+" "+partHash);
            form=new LineupForm(app,doc);form.Show();Application.DoEvents();
            Check(form.FieldWidth>250,"visible field area remains wide enough for editing");
            Reject(()=>form.CaptureSelection(),"empty selection rejected");
            doc.SelectSet.Add(one);doc.SelectSet.Add(two);Reject(()=>form.CaptureSelection(),"multiple selection rejected");
            doc.SelectSet.RemoveAll();doc.SelectSet.Add(one);
            form.SetField(0," Y1 ");form.SetField(4,"型号A");form.SetCategory("阀岛");form.CaptureSelection();
            Check(form.GetField(4)=="型号A","capture does not overwrite model field");
            var r1=form.SaveRecord();Check(r1.Number=="Y1"&&r1.ReferenceKey.Length>0&&r1.SourceFile==partFile,"trimmed number and native reference persisted");
            form.SetField(3,"中文,描述\"引用\"\r\n第二行");r1=form.SaveRecord();Check(form.RecordCount==1,"edit updates same record without duplication");
            form.NewRecord();form.SetField(0," y1 ");Reject(()=>form.SaveRecord(),"case and whitespace duplicate rejected");
            form.SetField(0," ");Reject(()=>form.SaveRecord(),"empty number rejected");
            form.SetField(0,"Y1V01");form.SetCategory("阀片");form.SetField(11,"missing");Reject(()=>form.SaveRecord(),"missing relationship rejected before save");
            form.SetField(11,"Y1");doc.SelectSet.RemoveAll();doc.SelectSet.Add(two);form.CaptureSelection();var r2=form.SaveRecord();
            Check(r2.IslandId==r1.Id&&r2.ReferenceKey!=r1.ReferenceKey,"same-part instances have distinct keys; number resolves to island ID");
            doc.SelectSet.RemoveAll();
            form.SelectIndex(0);Check(form.Locate(true).Name==one.Name,"first instance highlights and range zoom succeeds");
            app.DoIdle();((dynamic)app.ActiveWindow).View.SaveAsImage(Path.Combine(dir,"native-locate.png"),1200,800);
            using(var bmp=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bmp,new Rectangle(0,0,form.Width,form.Height));bmp.Save(Path.Combine(dir,"lineup-form.png"));}
            string csv=Path.Combine(dir,"Lineup.csv");form.ExportTo(csv);var rows=LineupCsv.Read(File.ReadAllText(csv));
            Check(rows.Count==3&&rows.All(x=>x.Length==13)&&rows[1][3]==r1.Description&&rows[2][11]=="Y1","actual form CSV exports 13 fields, Chinese, quotes, newline and relationship numbers");
            form.Close();form.Dispose();form=null;
            Check(Hash(file)==asmHash&&Hash(partFile)==partHash,"Lineup leaves assembly and part file hashes unchanged");
            doc.Close(false);doc=null;doc=(A.AssemblyDocument)app.Documents.Open(file);
            var loaded=LineupProject.Load(Path.ChangeExtension(file,".lineup.xml"),file);
            Check(LineupCad.Resolve(doc,loaded.Records[0]).Name==oneName(loaded.Records[0])&&LineupCad.Resolve(doc,loaded.Records[1]).Name==oneName(loaded.Records[1]),"both native keys rebind after assembly close and reopen");
            form=new LineupForm(app,doc);form.Show();Application.DoEvents();Check(form.RecordCount==2,"saved project auto-loads when Lineup opens");
            form.SelectIndex(1);Check(form.GetField(0)=="Y1V01"&&form.GetField(11)=="Y1","record fields and relation reload in form");form.Locate(true);
            form.Close();form.Dispose();form=null;
            string xml=Path.ChangeExtension(file,".lineup.xml");string xmlHash=Hash(xml);
            // Simulate a failed atomic save without changing the existing project.
            string blocked=Path.Combine(dir,"blocked.lineup.xml");Directory.CreateDirectory(blocked);
            try{loaded.Save(blocked);throw new Exception("Expected save failure");}catch(IOException){Check(Hash(xml)==xmlHash,"failed save preserves prior project");}
            Console.WriteLine("LINEUP OUTPUT "+dir);
        }finally{if(form!=null)form.Dispose();if(doc!=null)doc.Close(false);}
    }
    static string oneName(LineupRecord r){return r.InstancePath[0];}
    public static void Reopen(F.Application app,string dir){
        if(app==null)throw new InvalidOperationException("Requires native host.");
        string file=Path.Combine(dir,"LineupFixture.asm");string before=Hash(file);
        var doc=(A.AssemblyDocument)app.Documents.Open(file);
        var loaded=LineupProject.Load(Path.ChangeExtension(file,".lineup.xml"),file);
            Check(loaded.Records.Count==2,"fresh CAD process loads two persisted records");
        foreach(var r in loaded.Records)Check(LineupCad.Resolve(doc,r).Name==oneName(r),"fresh CAD process rebinds "+r.Number);
        using(var form=new LineupForm(app,doc)){
            form.Show();Application.DoEvents();form.SelectIndex(1);form.Locate(true);
            Check(form.RecordCount==2&&form.GetField(11)=="Y1","fresh CAD process form reload and highlight/zoom");
            using(var bmp=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bmp,new Rectangle(0,0,form.Width,form.Height));bmp.Save(Path.Combine(dir,"restarted-lineup-form.png"));}
        }
        Check(Hash(file)==before,"restart validation leaves assembly unchanged");
        Diagnostics diagnostic=null;
        foreach(F.AddIn addin in app.AddIns)if(string.Equals(addin.GUID,"{8C05165C-65A4-4EF2-A138-508589D82004}",StringComparison.OrdinalIgnoreCase))diagnostic=addin.Object as Diagnostics;
        Check(diagnostic!=null&&diagnostic.MenuStatus=="Registered 3 commands","actual registered native host exposes Lineup command");
        diagnostic.OpenLineupPanel();Application.DoEvents();
        var hosted=Application.OpenForms.Cast<Form>().OfType<LineupTableForm>().Single();
        Check(hosted.Visible&&hosted.RecordCount==2,"native host command opens owned Lineup window with saved records");
        hosted.SetCategory("全部");hosted.Grid.CurrentCell=hosted.Grid.Rows[1].Cells[1];hosted.Locate();
        using(var bmp=new Bitmap(hosted.Width,hosted.Height)){hosted.DrawToBitmap(bmp,new Rectangle(0,0,hosted.Width,hosted.Height));bmp.Save(Path.Combine(dir,"native-host-lineup-form.png"));}
        // Leave only this generated fixture open for visible native-menu acceptance.
    }
}
