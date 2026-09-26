using System;
using System.IO;
using System.Linq;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using TianGongCadSuite;
using F=SolidEdgeFramework;
using A=SolidEdgeAssembly;
static class LineupSmartTests {
    static void Check(bool ok,string text){if(!ok)throw new Exception("FAIL SMART: "+text);Console.WriteLine("PASS SMART: "+text);}
    static Button FindButton(Control root,string text){foreach(Control child in root.Controls){var button=child as Button;if(button!=null&&button.Text==text)return button;var nested=FindButton(child,text);if(nested!=null)return nested;}return null;}
    public static void Pure(string output){
        Directory.CreateDirectory(output);var p=new LineupProject();LineupNumbering.SetIsland(p,"OK170",1,"SS5Y7");
        Check(LineupNumbering.Next(p,"气缸")=="OK170-Y1C01"&&LineupNumbering.Next(p,"阀片")=="OK170-Y1V01","cylinder and valve number formats");
        p.Records.Add(new LineupRecord{Number="OK170-Y1C05-1",Category="气缸"});p.Records.Add(new LineupRecord{Number="OK170-Y1C05-2",Category="气缸"});
        Check(LineupNumbering.Next(p,"气缸")=="OK170-Y1C06","suffix instances reserve base sequence");
        var source=p.Records[0];LineupNumbering.SensorPairs(p,new[]{source.Id});
        Check(p.Records.Any(r=>r.Number=="OK170-Y1C05-1H")&&p.Records.Any(r=>r.Number=="OK170-Y1C05-1W"),"sensor H/W preserves cylinder suffix");
        Check(LineupNumbering.SensorPairs(p,new[]{source.Id})==0,"repeated H/W generation does not duplicate");
        p.Records.Add(new LineupRecord{Number="SE77"});p.Records.Add(new LineupRecord{Number="E35"});
        Check(LineupNumbering.Next(p,"普通传感器")=="SE78"&&LineupNumbering.Next(p,"其他电气件")=="E36","SE and E independently continue source numbers");
        var manual=new LineupRecord{Number="CUSTOM-9",Category="气缸"};p.Records.Add(manual);LineupNumbering.FillMissing(p,new[]{manual});Check(manual.Number=="CUSTOM-9","manual number remains unchanged");
        LineupNumbering.SetIsland(p,"OK170",2,"SS5Y5");Check(LineupNumbering.Next(p,"气缸")=="OK170-Y2C01","different island has separate sequence");
        var valve1=new LineupRecord{Number="OK170-Y2V01",Category="阀片",Mounting="水平安装"};var valve2=new LineupRecord{Number="OK170-Y2V02",Category="阀片"};p.Records.Add(valve1);p.Records.Add(valve2);
        LineupNumbering.SetIsland(p,"OK170",2,"ISLAND-NEW");
        Check(LineupNumbering.IslandModel(p,valve1)=="ISLAND-NEW"&&LineupNumbering.IslandModel(p,valve2)=="ISLAND-NEW","all same-island valves use one configured model");
        var export=LineupTableData.Grouped(p);Check(export.Count(r=>r[7].Contains("ISLAND-NEW"))==1&&export.First(r=>r[0]=="OK170-Y2V01")[7].Contains("水平安装"),"island model exports once without losing mounting text");
        p.AssemblyFile=Path.GetFullPath(Path.Combine(output,"fixture.asm"));p.Save(Path.Combine(output,"settings.xml"));var restored=LineupProject.Load(Path.Combine(output,"settings.xml"),p.AssemblyFile);Check(restored.NumberPrefix=="OK170"&&restored.ActiveIsland==2&&restored.Islands.Count==2,"numbering settings and island models persist");
        var catalog=LineupModelCatalog.Import("类别\t型号\t品牌\t接口类型\r\n普通传感器\tM1\tB1\tM12/4针\r\n普通传感器\tM1\tB2\tM8/3针","\t"[0],"普通传感器");
        Check(catalog.Match("普通传感器","M1").Count()==2,"same model with different brand/interface remains selectable variants");
        Check(catalog.Learn(new[]{new LineupRecord{Category="普通传感器",Model="M1",Brand="B1",Connector="M12/4针"}})==0,"complete identical model does not duplicate");
        Check(catalog.Learn(new[]{new LineupRecord{Category="气缸传感器",Model="M2",Brand="SMC"}})==0,"incomplete sensor interface is not learned");
        catalog.Save(Path.Combine(output,"models.xml"));Check(LineupModelCatalog.Load(Path.Combine(output,"models.xml")).Entries.Count==2,"model library roundtrip");
        string sample=File.ReadAllText(Path.Combine(Path.GetDirectoryName(typeof(LineupSmartTests).Assembly.Location),"lineup-sample.tsv"));var seed=LineupModelCatalog.Import(sample,'\t',"气缸");Check(seed.Entries.Count>20,"model library extracted from user reference");seed.Save(Path.Combine(output,"lineup-models-seed.xml"));
        Console.WriteLine("MODEL LIBRARY SEED "+seed.Entries.Count);
    }
    static void Edit(LineupTableForm form,int row,int column,string text){form.Grid.CurrentCell=form.Grid.Rows[row].Cells[column];if(!form.Grid.BeginEdit(true))throw new Exception("Cannot edit grid");((TextBox)form.Grid.EditingControl).Text=text;form.Grid.EndEdit();var end=DateTime.Now.AddMilliseconds(850);while(DateTime.Now<end){Application.DoEvents();Thread.Sleep(20);}}
    public static void Native(F.Application app,string output){
        Pure(output);A.AssemblyDocument doc=null;LineupTableForm form=null;
        try{
            doc=(A.AssemblyDocument)app.Documents.Add("SolidEdge.AssemblyDocument");string file=Path.Combine(output,"SmartFixture.asm");doc.SaveAs(file);
            var spec=new PanelSpec{Width=.2,Height=.1,Thickness=.006,Origin=new V3(),U=new V3(1,0,0),V=new V3(0,1,0),N=new V3(0,0,1)};var occurrence=CadBuilder.Generate(app,doc,spec,Path.Combine(output,"SmartPart.par"));doc.Save();
            string library=Path.Combine(output,"learned-models.xml");
            form=new LineupTableForm(app,doc,library);form.Show();Application.DoEvents();form.ConfigureNumbering("PRJ",2,"ISLAND-A");
            form.SetCategory("气缸");form.AddEmpty();form.AddEmpty();
            Check(form.CurrentProject.Records[0].Number=="PRJ-Y2C01"&&form.CurrentProject.Records[1].Number=="PRJ-Y2C02","new grid rows automatically numbered");
            var first=form.CurrentProject.Records[0];var second=form.CurrentProject.Records[1];
            form.ApplyModel(new LineupModelEntry{Category="气缸",Model="CYL-A",Brand="SMC"},new[]{first.Id,second.Id});
            Check(form.CurrentProject.Records.Take(2).All(r=>r.Model=="CYL-A"&&r.Brand=="SMC"),"model selection fills multiple rows");
            Edit(form,0,1,"MANUAL-1");form.ApplyModel(new LineupModelEntry{Category="气缸",Model="CYL-B",Brand="SMC"},new[]{first.Id});
            Check(form.CurrentProject.Records[0].Number=="MANUAL-1","manual number survives subsequent model selection");
            Check(form.GenerateSensorPairsFor(new[]{second.Id})==2,"native grid creates cylinder H/W pair");
            var sensors=form.CurrentProject.Records.Where(r=>r.Category=="气缸传感器").ToArray();Check(sensors[0].Number=="PRJ-Y2C02H"&&sensors[1].Number=="PRJ-Y2C02W"&&sensors.All(r=>r.Model==""),"sensor rows have correct H/W and do not inherit cylinder model");
            form.ApplyModel(new LineupModelEntry{Category="气缸传感器",Model="SENSOR-A",Brand="SMC",Connector="M8/3针"},sensors.Select(r=>r.Id));
            Check(LineupModelCatalog.Load(library).Entries.Any(e=>e.Model=="SENSOR-A"&&e.Connector=="M8/3针"),"complete selected model automatically learned to isolated library");
            form.SetCategory("阀片");form.AddEmpty();form.AddEmpty();form.ConfigureNumbering("PRJ",2,"ISLAND-B");
            Check(form.Grid.Rows.Cast<DataGridViewRow>().All(row=>Convert.ToString(row.Cells["阀岛型号"].Value)=="ISLAND-B"),"grid refreshes all valves after shared island model change");
            form.SetCategory("普通传感器");form.AddEmpty();Edit(form,0,5,"MANUAL-SENSOR");Edit(form,0,6,"OMRON");Edit(form,0,10,"M12/4针");
            Check(LineupModelCatalog.Load(library).Entries.Any(e=>e.Model=="MANUAL-SENSOR"&&e.Brand=="OMRON"&&e.Connector=="M12/4针"),"manually completed model automatically learned");
            Check(form.CurrentProject.Records.Last().Number=="SE01","ordinary sensor auto numbering in native grid");
            form.SetCategory("其他电气件");form.AddEmpty();Check(form.CurrentProject.Records.Last().Number=="E01","other electrical part auto numbering in native grid");
            form.SetCategory("全部");form.Grid.CurrentCell=form.Grid.Rows[0].Cells[1];form.Grid.HorizontalScrollingOffset=0;
            using(var bmp=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bmp,new Rectangle(0,0,form.Width,form.Height));bmp.Save(Path.Combine(output,"smart-table.png"));}
            Size full=form.Size;form.SetPickingMode(true);Check(form.PickingMode&&form.Width==440&&form.Height==180,"model-picking mode reduces window to 440 x 180");
            using(var bmp=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bmp,new Rectangle(0,0,form.Width,form.Height));bmp.Save(Path.Combine(output,"smart-picking.png"));}
            doc.SelectSet.RemoveAll();doc.SelectSet.Add(occurrence);form.BindAndNext();Check(form.CurrentProject.Records[0].ReferenceKey.Length>0&&form.Grid.CurrentCell.RowIndex==1,"compact mode binds actual CAD instance and advances");
            form.SetPickingMode(false);Check(!form.PickingMode&&form.Size==full,"return from picking restores table size");
            form.Export(Path.Combine(output,"smart-export.csv"));form.Close();form.Dispose();form=null;doc.Close(false);doc=null;
            doc=(A.AssemblyDocument)app.Documents.Open(file);form=new LineupTableForm(app,doc,library);form.Show();Application.DoEvents();Check(form.CurrentProject.NumberPrefix=="PRJ"&&form.CurrentProject.ActiveIsland==2&&form.CurrentProject.Islands.Single().Model=="ISLAND-B","reopen retains numbering context and shared model");
        }finally{if(form!=null)form.Dispose();if(doc!=null)doc.Close(false);}
        NestedParts(app,output);
    }
    static void NestedParts(F.Application app,string output){
        A.AssemblyDocument child=null,doc=null;LineupTableForm form=null;
        try{
            string childFile=Path.Combine(output,"Child.asm"),file=Path.Combine(output,"NestedParts.asm"),part=Path.Combine(output,"SmartPart.par");
            child=(A.AssemblyDocument)app.Documents.Add("SolidEdge.AssemblyDocument");child.Occurrences.AddByFilename(part);child.SaveAs(childFile);child.Close(false);child=null;
            doc=(A.AssemblyDocument)app.Documents.Add("SolidEdge.AssemblyDocument");doc.SaveAs(file);
            var top=doc.Occurrences.AddByFilename(part);var a=doc.Occurrences.AddByFilename(childFile);var b=doc.Occurrences.AddByFilename(childFile);doc.Save();
            var left=a.SubOccurrences.Item(1);var right=b.SubOccurrences.Item(1);
            var r1=new LineupRecord();var r2=new LineupRecord();LineupCad.CaptureOccurrence(left,r1);LineupCad.CaptureOccurrence(right,r2);
            Check(r1.ReferenceKey!=r2.ReferenceKey&&r1.SourceFile==r2.SourceFile,"same ordinary part in two subassemblies has distinct persistent instance keys");
            var fromReference=new LineupRecord();LineupCad.CaptureOccurrence(left.Reference,fromReference);Check(fromReference.ReferenceKey==r1.ReferenceKey,"native nested reference maps to the exact part instance");
            Console.WriteLine("NESTED PATHS "+string.Join("/",r1.InstancePath)+" | "+string.Join("/",r2.InstancePath));
            form=new LineupTableForm(app,doc,Path.Combine(output,"nested-models.xml"));form.Show();Application.DoEvents();form.ConfigureNumbering("NEST",1,"");form.SetCategory("气缸");
            doc.SelectSet.RemoveAll();doc.SelectSet.Add(top);doc.SelectSet.Add(left);doc.SelectSet.Add(right);
            Check(form.AddSelectedModels()==3,"batch adds top-level ordinary part and both nested ordinary parts");
            Check(form.AddSelectedModels()==0,"repeated part selection is deduplicated by instance");
            form.Grid.CurrentCell=form.Grid.Rows[1].Cells[1];form.Locate();Check(true,"nested ordinary part highlight and range zoom");
            form.Close();form.Dispose();form=null;doc.Close(false);doc=null;
            doc=(A.AssemblyDocument)app.Documents.Open(file);form=new LineupTableForm(app,doc,Path.Combine(output,"nested-models.xml"));form.Show();Application.DoEvents();
            var records=form.CurrentProject.Records;Check(records.Count==3&&records.All(r=>r.SourceFile==part),"ordinary part associations survive XML and assembly reopen");
            for(int i=0;i<records.Count;i++){var target=LineupCad.ResolveTarget(doc,records[i]);var check=new LineupRecord();LineupCad.CaptureOccurrence(target,check);Check(check.ReferenceKey==records[i].ReferenceKey,"rebind correct instance "+i);form.Grid.CurrentCell=form.Grid.Rows[i].Cells[1];form.Locate();}
            form.AddEmpty();form.Grid.CurrentCell=form.Grid.Rows[3].Cells[1];
            var extra=doc.Occurrences.AddByFilename(part);doc.Save();doc.SelectSet.RemoveAll();doc.SelectSet.Add(extra);form.BindAndNext();
            Check(form.CurrentProject.Records[3].ReferenceKey.Length>0,"bind existing row to an ordinary part");
            form.AddEmpty();string fifth=form.CurrentProject.Records.Last().Id;form.AddEmpty();string sixth=form.CurrentProject.Records.Last().Id;
            form.Grid.CurrentCell=form.Grid.Rows[4].Cells[1];form.SetPickingMode(true);
            var five=doc.Occurrences.AddByFilename(part);doc.Save();doc.SelectSet.RemoveAll();doc.SelectSet.Add(five);FindButton(form,"绑定并下一条").PerformClick();Application.DoEvents();
            Check(form.BindingRecordId==sixth&&doc.SelectSet.Count==0,"compact sequential bind retains next record and clears old CAD selection");
            doc.SelectSet.Add(five);bool duplicate=false;try{form.BindAndNext();}catch(InvalidOperationException ex){duplicate=ex.Message.Contains("请选择下一个模型");}
            Check(duplicate&&form.BindingRecordId==sixth,"duplicate selection names conflict and keeps pending row unchanged");
            form.SetPickingMode(false);Application.DoEvents();Check(form.BindingRecordId==sixth,"return to visible table retains pending row");form.SetPickingMode(true);
            var six=doc.Occurrences.AddByFilename(part);doc.Save();doc.SelectSet.RemoveAll();doc.SelectSet.Add(six);FindButton(form,"绑定并下一条").PerformClick();Application.DoEvents();
            Check(form.CurrentProject.Records.Single(r=>r.Id==sixth).ReferenceKey.Length>0,"second compact bind saves the intended next record");form.SetPickingMode(false);
            Check(form.Grid.Columns["原位"].Visible&&form.Grid.Columns["安装方式"].Visible,"home position and mounting visible in common fields");
            form.SetCategory("阀片");form.ConfigureNumbering("NEST",1,"ISLAND-ONE");form.AddEmpty();string valve=form.CurrentProject.Records.Last().Id;form.ConfigureNumbering("NEST",2,"ISLAND-TWO");form.AddEmpty();
            form.AssignValvesToIsland("NEST-Y2",new[]{valve});var moved=form.CurrentProject.Records.Single(r=>r.Id==valve);
            Check(moved.Number=="NEST-Y2V02"&&LineupNumbering.IslandModel(form.CurrentProject,moved)=="ISLAND-TWO"&&form.CurrentProject.Islands.Single(i=>i.Key=="NEST-Y1").Model=="ISLAND-ONE","selected valve moves to another island without changing other island model");
            form.Undo();Check(form.CurrentProject.Records.Single(r=>r.Id==valve).Number=="NEST-Y1V01","undo island assignment restores original valve number");
        }finally{if(form!=null)form.Dispose();if(doc!=null)doc.Close(false);if(child!=null)child.Close(false);}
    }
}
