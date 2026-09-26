using System;
using System.IO;
using System.Linq;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using TianGongCadSuite;
using F=SolidEdgeFramework;
using A=SolidEdgeAssembly;
static class LineupTableTests {
    static void Check(bool ok,string text){if(!ok)throw new Exception("FAIL TABLE: "+text);Console.WriteLine("PASS TABLE: "+text);}
    static string Sample(){return File.ReadAllText(Path.Combine(Path.GetDirectoryName(typeof(LineupTableTests).Assembly.Location),"lineup-sample.tsv"));}
    static void Reject(Action action,string label){try{action();}catch(InvalidDataException){Check(true,label);return;}throw new Exception("Expected rejection: "+label);}
    public static void Pure(string output){
        Directory.CreateDirectory(output);var imported=LineupTableData.Parse(Sample(),"气缸",'\t');
        Check(imported.Records.Count==273,"all 273 source records imported");
        int[] counts={56,33,72,77,35};
        for(int i=0;i<5;i++)Check(imported.Records.Count(r=>r.Category==LineupTableData.Categories[i])==counts[i],"source category "+LineupTableData.Categories[i]+" = "+counts[i]);
        Check(imported.Records.Count(r=>r.Number=="")==2,"both unnumbered regulator records preserved");
        Check(imported.Sections.Count==5&&imported.Sections[3].Cells[6].Contains("常闭"),"all section headings and normally-closed note preserved");
        var p=new LineupProject{AssemblyFile=Path.Combine(output,"table-fixture.asm"),Records=imported.Records,Sections=imported.Sections};
        string csv=LineupCsv.Write(LineupTableData.Grouped(p));
        var round=LineupTableData.Parse(csv,"气缸",',');
        Check(round.Records.Count==273&&round.Records.Zip(imported.Records,(a,b)=>a.Category==b.Category&&LineupTableData.Cells(a).SequenceEqual(LineupTableData.Cells(b))).All(x=>x),"all 3003 business cells round-trip exactly");
        Check(LineupTableData.Parse(LineupTableData.Tsv(LineupTableData.Grouped(p)),"气缸",'\t').Records.Count==273,"grouped clipboard TSV reimports");
        var special=new LineupProject();special.Records.Add(new LineupRecord{Category="气缸",Number="A01",Description="中文,引号\"\r\n换行",Model="=literal"});
        var parsed=LineupTableData.Parse(LineupTableData.Tsv(LineupTableData.Grouped(special)),"气缸",'\t');
        Check(parsed.Records[0].Description==special.Records[0].Description,"quoted multiline clipboard values preserved");
        special.Records[0].Connector="M12/4针";special.Records[0].Notes="保留备注";special.Records[0].Mechanism="仅供插件内部组织视图";special.Records[0].Address="X207LS";
        var reference=LineupTableData.ReferenceGrouped(special);var ten=LineupTableData.Parse(LineupTableData.Tsv(reference),"气缸",'\t').Records[0];
        Check(reference.All(r=>r.Length==10)&&reference[0][8]=="传感器接口类型","reference BOM has exact ten-column layout");
        Check(ten.Connector=="M12/4针"&&ten.Notes=="保留备注"&&ten.Address=="X207LS"&&ten.Mechanism=="","ten-column import does not shift interface notes or IO");
        Check(special.Records[0].Mechanism=="仅供插件内部组织视图","reference export retains internal mechanism metadata");
        Reject(()=>LineupTableData.Parse("A01\t\r\na01\t","气缸",'\t'),"duplicate number paste rejected");
        Reject(()=>LineupTableData.Parse("\"未结束","气缸",'\t'),"malformed quotation rejected");
        Reject(()=>LineupTableData.Parse(string.Join("\t",Enumerable.Repeat("x",12)),"气缸",'\t'),"too many columns rejected");
        File.WriteAllText(Path.Combine(output,"Lineup-五类清单.csv"),csv,new System.Text.UTF8Encoding(true));
        File.WriteAllText(Path.Combine(output,"Lineup-五类清单.tsv"),LineupTableData.Tsv(LineupTableData.Grouped(p)),new System.Text.UTF8Encoding(true));
        p.Save(Path.Combine(output,"draft.xml"));Check(LineupProject.Load(Path.Combine(output,"draft.xml"),p.AssemblyFile).Sections.Count==5,"section notes persist with draft");
    }
    static void Edit(LineupTableForm form,int row,int column,string text){
        form.Grid.CurrentCell=form.Grid.Rows[row].Cells[column];Check(form.Grid.BeginEdit(true),"grid enters edit mode");
        ((TextBox)form.Grid.EditingControl).Text=text;form.Grid.EndEdit();
        var end=DateTime.Now.AddMilliseconds(900);while(DateTime.Now<end){Application.DoEvents();Thread.Sleep(20);}
    }
    public static void Native(F.Application app,string output){
        Pure(output);A.AssemblyDocument doc=null;LineupTableForm form=null;
        try {
            string file=Path.Combine(output,"TableFixture.asm");doc=(A.AssemblyDocument)app.Documents.Add("SolidEdge.AssemblyDocument");doc.SaveAs(file);
            var spec=new PanelSpec{Width=.2,Height=.1,Thickness=.006,Origin=new V3(),U=new V3(1,0,0),V=new V3(0,1,0),N=new V3(0,0,1)};
            string part=Path.Combine(output,"TablePart.par");var one=CadBuilder.Generate(app,doc,spec,part);var two=doc.Occurrences.AddByFilename(part);doc.Save();
            form=new LineupTableForm(app,doc,Path.Combine(output,"test-models.xml"));form.Show();Application.DoEvents();form.ConfigureNumbering("OK170",1,"");form.ImportText(Sample(),'\t');
            Check(form.RecordCount==273,"native table imports source in one operation");
            Reject(()=>form.ImportText(Sample(),'\t'),"repeated full-table import rejected atomically");Check(form.RecordCount==273,"failed import leaves all rows unchanged");
            Edit(form,0,6,"批量测试品牌");
            var saved=LineupProject.Load(Path.ChangeExtension(file,".lineup.xml"),file);
            Check(saved.Records[0].Brand=="批量测试品牌","editing cell auto-saves without Add/Save button");
            form.Grid.CurrentCell=form.Grid.Rows[0].Cells[4];form.PasteCells("同组描述\t同组型号\r\n第二行描述\t第二行型号");
            Check(form.CurrentProject.Records[1].Model=="第二行型号","rectangular multi-cell paste works");
            form.Grid.CurrentCell=form.Grid.Rows[1].Cells[1];
            Reject(()=>form.PasteCells("OK170-Y1C01"),"duplicate cell paste rejected");
            Check(form.CurrentProject.Records[1].Number=="OK170-Y1C02","duplicate paste rolls back without data loss");
            form.Grid.ClearSelection();form.Grid.Rows[0].Cells[6].Selected=true;form.Grid.Rows[1].Cells[6].Selected=true;form.FillDown();
            Check(form.CurrentProject.Records[1].Brand=="批量测试品牌","selected column fill-down updates both rows");
            form.Grid.CurrentCell=form.Grid.Rows[0].Cells[1];doc.SelectSet.RemoveAll();doc.SelectSet.Add(one);form.BindAndNext();
            Check(form.CurrentProject.Records[0].ReferenceKey.Length>0&&form.Grid.CurrentCell.RowIndex==1,"bind current row advances to next row");
            doc.SelectSet.Add(two);Check(form.AddSelectedModels()==1&&form.RecordCount==274,"multi-pick adds unbound instance and skips existing binding");
            Check(form.AddSelectedModels()==0&&form.RecordCount==274,"repeated multi-pick does not duplicate model rows");
            Check(form.CurrentProject.Records[273].Number=="OK170-Y1C06","multi-pick automatically numbers the next cylinder");

            Edit(form,273,4,"新增模型待完善");
            form.Grid.ClearSelection();form.Grid.Rows[273].Cells[1].Selected=true;form.DeleteRows();Check(form.RecordCount==273,"selected row deletion works");
            form.Undo();Check(form.RecordCount==274&&form.CurrentProject.Records[273].ReferenceKey.Length>0,"undo restores deleted row and association");
            form.Export(Path.Combine(output,"native-table-export.csv"));
            Check(LineupTableData.Parse(File.ReadAllText(Path.Combine(output,"native-table-export.csv")),"气缸",',').Records.Count==274,"native table grouped export preserves every row");
            form.SetCategory("气缸传感器");Check(form.Grid.Rows.Count==72,"category filter shows 72 cylinder sensors");
            form.SetCategory("全部");
            using(var bmp=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bmp,new Rectangle(0,0,form.Width,form.Height));bmp.Save(Path.Combine(output,"table-form.png"));}
            form.Close();form.Dispose();form=null;doc.Close(false);doc=null;doc=(A.AssemblyDocument)app.Documents.Open(file);
            form=new LineupTableForm(app,doc,Path.Combine(output,"test-models.xml"));form.Show();Application.DoEvents();
            Check(form.RecordCount==274&&form.CurrentProject.Records[0].ReferenceKey.Length>0,"close/reopen keeps draft rows and CAD associations");
        }finally{if(form!=null)form.Dispose();if(doc!=null)doc.Close(false);}
    }
}
