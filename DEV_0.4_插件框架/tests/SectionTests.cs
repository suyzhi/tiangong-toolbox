using System;
using System.IO;
using System.Linq;
using System.Drawing;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Windows.Forms;
using TianGongCadSuite;
using A=SolidEdgeAssembly;
using F=SolidEdgeFramework;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;
static class SectionTests {
    static void Check(bool ok,string label){if(!ok)throw new Exception("FAIL SECTION: "+label);Console.WriteLine("PASS SECTION: "+label);}
    static string Hash(string path){using(var s=File.OpenRead(path))using(var h=SHA256.Create())return BitConverter.ToString(h.ComputeHash(s));}
    public static void Native(F.Application app,string output){
        Directory.CreateDirectory(output);
        Case(app,output,"铝型材","40X40AXR0.5.par",.14,.10,true);
    }
    static void Case(F.Application app,string output,string category,string file,double w,double h,bool userCase){
        string dir=Path.Combine(output,Path.GetFileNameWithoutExtension(file)+"_"+w+"x"+h);Directory.CreateDirectory(dir);
        string source=Path.Combine(CadBuilder.CadInstallRoot,"Frames","Frames",category,file),copy=Path.Combine(dir,file);string before=Hash(source);File.Copy(source,copy);A.AssemblyDocument asm=null;
        try{
            asm=(A.AssemblyDocument)app.Documents.Add("SolidEdge.AssemblyDocument");string path=Path.Combine(dir,"SectionFrame.asm");asm.SaveAs(path);
            var sketch=asm.Sketches3D.Add();var l=sketch.Lines3D;Array paths=new object[]{l.Add(0,0,0,w,0,0),l.Add(w,0,0,w,h,0),l.Add(w,h,0,0,h,0),l.Add(0,h,0,0,0,0)};
            asm.StructuralFrames.Add(copy,4,ref paths,A.StructuralFrameEndConditionConstants.seButt1,0.0,true);sketch.Visible=false;asm.Save();
            var selected=asm.Occurrences.Cast<object>().ToList();var members=FrameReader.Read(asm,selected);
            Console.WriteLine("SECTION "+file+" members="+members.Count+" triangles="+members.Sum(m=>m.Triangles.Count));
            foreach(var m in members)Console.WriteLine("AXIS "+m.Name+" "+m.Axis);
            var openings=FrameDetection.Detect(members,.006,.001);Check(openings.Count==1,file+" centre section identifies one opening");var spec=openings[0].Solve(.006,.001);
            Console.WriteLine("SECTION DIMENSIONS "+spec.Width*1000+" x "+spec.Height*1000+" x "+spec.Thickness*1000+" origin="+spec.Origin);
            if(userCase)Check(spec.Width*spec.Height>(w-.04-.002)*(h-.04-.002)+1e-8,"40X40AXR0.5 boundary reaches beyond outside flanges into the slots");
            foreach(var plane in openings[0].Planes){Check(members.SelectMany(m=>m.Faces).Any(f=>f.Plane.Normal.Cross(plane.Normal).Length<1e-7&&Math.Abs(plane.Normal.Dot(f.Plane.Point-plane.Point))<2e-6),"selected boundary is an actual CAD plane");}
            foreach(var item in selected)asm.SelectSet.Add(item);using(var form=new AutoPanelForm(app,asm)){form.SetParameters("6","1");form.Show();Application.DoEvents();Check(form.OpeningCount==1,"automatic dialog accepts "+file+": "+form.StatusText);using(var image=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(image,new Rectangle(0,0,form.Width,form.Height));image.Save(Path.Combine(dir,"preview.png"));}form.Close();}asm.SelectSet.RemoveAll();
            string batch=BatchPanels.Generate(app,asm,new[]{spec},dir);asm.Save();asm.Close(false);asm=null;asm=(A.AssemblyDocument)app.Documents.Open(path);
            Check(asm.Occurrences.Count==5,"reopened section frame contains four members and one panel");var panel=asm.Occurrences.Item(5);var part=(P.PartDocument)panel.OccurrenceDocument;CadBuilder.CheckBody((G.Body)part.Models.Item(1).Body,spec.Width,spec.Height,.006);Check(true,"reopened grooved panel native body dimensions");
            File.WriteAllText(Path.Combine(dir,"result.txt"),file+"\n"+spec.Width*1000+" x "+spec.Height*1000+" x 6 mm\n"+source+"\nSHA256 "+before);
        }finally{if(asm!=null)try{asm.Close(false);}catch{}Check(Hash(source)==before&&Hash(copy)==before,"section library and independent copy unchanged");}
    }
}
