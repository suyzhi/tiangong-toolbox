using System;
using System.IO;
using System.Linq;
using System.Drawing;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Windows.Forms;
using TianGongCadSuite;
using F=SolidEdgeFramework;
using A=SolidEdgeAssembly;
using G=SolidEdgeGeometry;
static class AutoTests {
    static void Check(bool ok,string text){if(!ok)throw new Exception("FAIL AUTO: "+text);Console.WriteLine("PASS AUTO: "+text);}
    static void Reject(Action a,string text){try{a();}catch(ArgumentException){Check(true,text);return;}throw new Exception("Expected AUTO rejection: "+text);}
    static string Hash(string p){using(var f=File.OpenRead(p))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(f));}
    static FrameMember Box(double x,double y,double w,double h){
        var m=new FrameMember{Name="fixture",Axis=w>h?new V3(1,0,0):new V3(0,1,0)};
        var points=new[]{new V3(x,y,-.01),new V3(x+w,y,-.01),new V3(x+w,y+h,-.01),new V3(x,y+h,-.01),new V3(x,y,.01),new V3(x+w,y,.01),new V3(x+w,y+h,.01),new V3(x,y+h,.01)};m.Points.AddRange(points);
        foreach(var ids in new[]{new[]{0,1,5,4},new[]{1,2,6,5},new[]{2,3,7,6},new[]{3,0,4,7},new[]{0,3,2,1},new[]{4,5,6,7}}){var f=new FrameFace{Plane=new PlaneInput(points[ids[0]],(points[ids[1]]-points[ids[0]]).Cross(points[ids[2]]-points[ids[1]]),"test")};for(int i=0;i<4;i++)f.Edges.Add(new[]{points[ids[i]],points[ids[(i+1)%4]]});m.Faces.Add(f);}return m;
    }
    static List<FrameMember> Frame(){return new List<FrameMember>{Box(-.02,0,.02,.3),Box(.5,0,.02,.3),Box(-.02,-.02,.54,.02),Box(-.02,.3,.54,.02)};}
    public static void Pure(){
        var members=Frame();var found=FrameDetection.Detect(members,.006);var s=found.Single().Solve(.006,.001);Check(Math.Abs(s.Width*s.Height-.498*.298)<1e-10&&Math.Abs(s.Origin.Z)<1e-10,"one frame gives manual dimensions and shared midpoint");
        members.Add(Box(.24,0,.02,.3));found=FrameDetection.Detect(members,.006);Check(found.Count==2&&found.All(o=>Math.Abs(o.Solve(.006,.001).Width*o.Solve(.006,.001).Height-.238*.298)<1e-10),"divider creates exactly two smaller panels");
        var u=new V3(1,2,3).Unit();var v=u.Cross(new V3(0,1,0)).Unit();var t=Transform.Frame(new V3(10,-3,6),u,v,u.Cross(v));
        foreach(var m in members){m.Axis=t.Vector(m.Axis);m.Points=m.Points.Select(t.Point).ToList();foreach(var f in m.Faces){f.Plane=new PlaneInput(t.Point(f.Plane.Point),t.Normal(f.Plane.Normal),"rotated");f.Edges=f.Edges.Select(e=>e.Select(t.Point).ToArray()).ToList();}}
        found=FrameDetection.Detect(members,.006);Check(found.Count==2&&found.All(o=>Math.Abs((o.MidPoint-t.Point(new V3())).Dot(t.Vector(new V3(0,0,1))))<1e-8),"rotated and translated multi opening frame");
        members=Frame();members.RemoveAt(3);Reject(()=>FrameDetection.Detect(members,.006),"missing member");
        members=Frame();members[0]=Box(-.02,.002,.02,.298);Reject(()=>FrameDetection.Detect(members,.006),"2 mm corner gap is not closed");
        members=Frame();members[0].Faces.Clear();Reject(()=>FrameDetection.Detect(members,.006),"envelope alone cannot prove a boundary");
        members=Frame();members.Add(Box(.24,0,.02,.15));Reject(()=>FrameDetection.Detect(members,.006),"partial divider makes a nonrectangular hole");
        Reject(()=>FrameDetection.Detect(Frame(),.03),"thickness outside frame depth");
        var two=Frame();var shifted=Frame();foreach(var m in shifted){m.Points=m.Points.Select(p=>p+new V3(1,0,0)).ToList();foreach(var f in m.Faces){f.Plane.Point=f.Plane.Point+new V3(1,0,0);f.Edges=f.Edges.Select(e=>e.Select(p=>p+new V3(1,0,0)).ToArray()).ToList();}}two.AddRange(shifted);Check(FrameDetection.Detect(two,.006).Count==2,"two disjoint closed frames");
    }
    public static void Native(F.Application app,string dir){
        string output=Path.Combine(dir,"AutoFrame");Directory.CreateDirectory(output);
        string source=Path.Combine(CadBuilder.CadInstallRoot,"Frames","Frames","方管","20x1.5.par"),copy=Path.Combine(output,"Section20x1.5.par");string before=Hash(source);File.Copy(source,copy);string copyBefore=Hash(copy);
        File.WriteAllText(Path.Combine(output,"library-hashes.txt"),source+"\n"+before+"\n"+copy+"\n"+copyBefore);
        A.AssemblyDocument frame=null,top=null;
        try{
            frame=(A.AssemblyDocument)app.Documents.Add("SolidEdge.AssemblyDocument");string path=Path.Combine(output,"NativeStructuralFrame.asm");frame.SaveAs(path);
            var sketch=frame.Sketches3D.Add();var lines=sketch.Lines3D;Array paths=new object[]{lines.Add(0,0,0,.52,0,0),lines.Add(.52,0,0,.52,.32,0),lines.Add(.52,.32,0,0,.32,0),lines.Add(0,.32,0,0,0,0)};
            Console.WriteLine("AUTO native StructuralFrames.Add start");
            var structural=frame.StructuralFrames.Add(copy,4,ref paths,A.StructuralFrameEndConditionConstants.seMiter,0.0,true);
            frame.Save();Console.WriteLine("AUTO StructuralFrames="+frame.StructuralFrames.Count+" occurrences="+frame.Occurrences.Count);
            var selection=frame.Occurrences.Cast<object>().ToList();
            foreach(A.Occurrence occ in frame.Occurrences)Console.WriteLine("AUTO member "+occ.Name+" sub="+occ.Subassembly+" override="+occ.HasBodyOverride+" frame="+occ.IsStructuralFrameItem);
            var members=FrameReader.Read(frame,selection);var openings=FrameDetection.Detect(members,.006);Check(openings.Count==1,"actual TianGong StructuralFrames gives one closed opening");
            var spec=openings[0].Solve(.006,.001);Console.WriteLine("AUTO DIMENSIONS "+spec.Width*1000+" x "+spec.Height*1000+" x "+spec.Thickness*1000+" at "+spec.Origin);
            Check(Math.Abs(spec.Width*spec.Height-.498*.298)<1e-8,"native 20 mm section / 520 x 320 paths -> 498 x 298 panel at 1 mm gap");
            // Use actual selected occurrences, including repeated selection, through the new UI.
            frame.Activate();frame.SelectSet.RemoveAll();foreach(var item in selection)frame.SelectSet.Add(item);
            Check(FrameReader.Read(frame,selection.Concat(selection)).Count==members.Count,"duplicate occurrence selection is deduplicated");
            using(var form=new AutoPanelForm(app,frame)){form.Show();form.SetParameters("6","1");Application.DoEvents();Check(form.OpeningCount==1,"automatic dialog reads native preselection: "+form.StatusText);using(var bitmap=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,new Rectangle(0,0,form.Width,form.Height));bitmap.Save(Path.Combine(output,"automatic-dialog.png"));}form.Close();}
            frame.SelectSet.RemoveAll();sketch.Visible=false;
            string batch=BatchPanels.Generate(app,frame,new[]{spec},output);Check(Directory.GetFiles(batch,"*.par").Length==1,"batch produces one native PAR");frame.Save();
            ((dynamic)app.ActiveWindow).View.Fit();((dynamic)app.ActiveWindow).View.Update();Application.DoEvents();app.DoIdle();((dynamic)app.ActiveWindow).View.SaveAsImage(Path.Combine(output,"native-frame-panel.png"),1400,1000);
            frame.Close(false);frame=null;frame=(A.AssemblyDocument)app.Documents.Open(path);Check(frame.StructuralFrames.Count==1&&frame.Occurrences.Count==selection.Count+1,"saved frame and panel reopen together");
            var panel=frame.Occurrences.Item(frame.Occurrences.Count);var p=(SolidEdgePart.PartDocument)panel.OccurrenceDocument;CadBuilder.CheckBody((G.Body)p.Models.Item(1).Body,spec.Width,spec.Height,.006);Check(true,"reopened panel thickness and dimensions verified");
            // Validate failed second output removes only the first output of that batch.
            int count=frame.Occurrences.Count;var bad=new PanelSpec{Width=-1,Height=.1,Thickness=.006,U=new V3(1,0,0),V=new V3(0,1,0),N=new V3(0,0,1)};
            try{BatchPanels.Generate(app,frame,new[]{spec,bad},output);throw new Exception("Batch failure expected");}catch(InvalidOperationException){Check(frame.Occurrences.Count==count,"second-panel failure rolls back prior inserted panel");}
            // Save a clean frame-only copy for nested detection.
            panel.Delete();frame.SaveAs(Path.Combine(output,"FrameOnly.asm"));
            top=(A.AssemblyDocument)app.Documents.Add("SolidEdge.AssemblyDocument");top.SaveAs(Path.Combine(output,"NestedAuto.asm"));var parent=top.Occurrences.AddByFilename(Path.Combine(output,"FrameOnly.asm"));
            var u=new V3(1,2,3).Unit();var v=u.Cross(new V3(0,1,0)).Unit();var transform=Transform.Frame(new V3(.8,.4,.2),u,v,u.Cross(v));Array matrix=transform.M;parent.PutMatrix(ref matrix,true);
            var nm=FrameReader.Read(top,new object[]{parent});for(int mi=0;mi<nm.Count;mi++){Console.WriteLine("NEST "+mi+" axis="+nm[mi].Axis+" center="+(nm[mi].Points.Aggregate(new V3(),(sum,pt)=>sum+pt)*(1.0/nm[mi].Points.Count))+" expected="+transform.Point(members[mi].Points.Aggregate(new V3(),(sum,pt)=>sum+pt)*(1.0/members[mi].Points.Count)));}var nested=FrameDetection.Detect(nm,.006);Check(nested.Count==1&&Math.Abs(nested[0].Solve(.006,.001).Width*nested[0].Solve(.006,.001).Height-.498*.298)<1e-8,"selected rotated native frame subassembly resolves nested transforms");
            BatchPanels.Generate(app,top,nested.Select(o=>o.Solve(.006,.001)).ToList(),output);top.Save();
            ((dynamic)app.ActiveWindow).View.Fit();((dynamic)app.ActiveWindow).View.Update();Application.DoEvents();app.DoIdle();((dynamic)app.ActiveWindow).View.SaveAsImage(Path.Combine(output,"nested-auto.png"),1400,1000);
            MultiNative(app,output,copy);
        }finally{if(top!=null)try{top.Close(false);}catch{}if(frame!=null)try{frame.Close(false);}catch{}Check(Hash(source)==before,"installed frame library SHA256 preserved");Check(Hash(copy)==copyBefore,"copied frame section SHA256 preserved");}
    }
    static void MultiNative(F.Application app,string output,string section){
        A.AssemblyDocument doc=null;
        try{
            doc=(A.AssemblyDocument)app.Documents.Add("SolidEdge.AssemblyDocument");string path=Path.Combine(output,"NativeTwoOpenings.asm");doc.SaveAs(path);
            var sketch=doc.Sketches3D.Add();var l=sketch.Lines3D;Array perimeter=new object[]{l.Add(0,0,0,.52,0,0),l.Add(.52,0,0,.52,.32,0),l.Add(.52,.32,0,0,.32,0),l.Add(0,.32,0,0,0,0)};
            doc.StructuralFrames.Add(section,4,ref perimeter,A.StructuralFrameEndConditionConstants.seMiter,0.0,true);
            Array middle=new object[]{l.Add(.26,.01,0,.26,.31,0)};doc.StructuralFrames.Add(section,1,ref middle,A.StructuralFrameEndConditionConstants.seNone,0.0,true);sketch.Visible=false;doc.Save();
            var selection=doc.Occurrences.Cast<object>().ToList();var found=FrameDetection.Detect(FrameReader.Read(doc,selection),.006);var specs=found.Select(o=>o.Solve(.006,.001)).ToList();
            Check(selection.Count==5&&specs.Count==2&&specs.All(s=>Math.Abs(s.Width*s.Height-.238*.298)<1e-8),"five native frame members create two 238 x 298 x 6 mm panels");
            foreach(var item in selection)doc.SelectSet.Add(item);int countBefore=doc.Occurrences.Count;bool dirtyBefore=doc.Dirty;
            using(var form=new AutoPanelForm(app,doc)){form.Show();form.SetParameters("6","1");Application.DoEvents();Check(form.OpeningCount==2,"native multi-selection dialog lists both openings");using(var bitmap=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,new Rectangle(0,0,form.Width,form.Height));bitmap.Save(Path.Combine(output,"two-openings-dialog.png"));}form.Close();}
            Check(doc.Occurrences.Count==countBefore&&doc.Dirty==dirtyBefore,"automatic preview cancel leaves frame untouched");doc.SelectSet.RemoveAll();
            string batch=BatchPanels.Generate(app,doc,specs,output);Check(doc.Occurrences.Count==7&&Directory.GetFiles(batch,"*.par").Length==2,"native batch inserts two panels in one operation");doc.Save();
            ((dynamic)app.ActiveWindow).View.Fit();((dynamic)app.ActiveWindow).View.Update();Application.DoEvents();app.DoIdle();((dynamic)app.ActiveWindow).View.SaveAsImage(Path.Combine(output,"native-two-panels.png"),1400,1000);
            doc.Close(false);doc=null;doc=(A.AssemblyDocument)app.Documents.Open(path);Check(doc.Occurrences.Count==7&&doc.StructuralFrames.Count==2,"two panels and two native frame features survive reopen");
            for(int i=0;i<2;i++){var panel=doc.Occurrences.Item(6+i);var part=(SolidEdgePart.PartDocument)panel.OccurrenceDocument;CadBuilder.CheckBody((G.Body)part.Models.Item(1).Body,specs[i].Width,specs[i].Height,.006);Array m=new double[16];panel.GetMatrix(ref m);Check(m.Cast<object>().Select(Convert.ToDouble).Where((v,index)=>Math.Abs(v-specs[i].Placement.M[index])>1e-8).Count()==0,"reopened batch panel "+(i+1)+" size and placement");}
        }finally{if(doc!=null)try{doc.Close(false);}catch{}}
    }
}
