using System;
using System.IO;
using System.Linq;
using System.Drawing;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Windows.Forms;
using TianGongPanel;
using F=SolidEdgeFramework;
using A=SolidEdgeAssembly;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;
static class Advanced {
    static void Assert(bool value,string message){if(!value)throw new Exception("FAIL: "+message);Console.WriteLine("PASS: "+message);}
    static string Hash(string p){using(var f=File.OpenRead(p))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(f)).Replace("-","");}
    static PanelSpec Box(double x,double y,double z){return new PanelSpec{Origin=new V3(),U=new V3(1,0,0),V=new V3(0,1,0),N=new V3(0,0,1),Width=x,Height=y,Thickness=z};}
    static void MakeBox(F.Application app,string file,double x,double y,double z){var p=CadBuilder.CreatePart(app,Box(x,y,z));p.SaveAs(file);p.Close(false);}
    static void Place(A.Occurrence occ,Transform transform){Array m=transform.M;occ.PutMatrix(ref m,true);}
    static A.TopologyReference NestedReference(A.SubOccurrence occurrence,object entity){Array key=new byte[0];((dynamic)entity).GetReferenceKey(ref key);A.TopologyReference reference;occurrence.CreateTopologyReference(ref key,out reference);return reference;}
    static G.Face Face(A.Occurrence occ,V3 normal,double offset){var body=(G.Body)((P.PartDocument)occ.OccurrenceDocument).Models.Item(1).Body;foreach(G.Face f in (G.Faces)body.Faces[G.FeatureTopologyQueryTypeConstants.igQueryPlane]){var p=(G.Plane)f.Geometry;Array n=new double[3],o=new double[3];p.GetPlaneData(ref o,ref n);if(V3.From(n).Cross(normal).Length<1e-8&&Math.Abs(normal.Dot(V3.From(o))-offset)<1e-8)return f;}throw new Exception("Fixture face missing");}
    public static void Run(F.Application app,string dir){
        string vertical=Path.Combine(dir,"Vertical.par"),horizontal=Path.Combine(dir,"Horizontal.par"),frameFile=Path.Combine(dir,"Frame.asm"),nestedFile=Path.Combine(dir,"Nested.asm");
        MakeBox(app,vertical,.02,.3,.02);MakeBox(app,horizontal,.54,.02,.02);
        string hv=Hash(vertical),hh=Hash(horizontal);File.WriteAllText(Path.Combine(dir,"source-hashes.txt"),vertical+" "+hv+Environment.NewLine+horizontal+" "+hh);
        A.AssemblyDocument frame=null,top=null;
        try {
            frame=(A.AssemblyDocument)app.Documents.Add("SolidEdge.AssemblyDocument");frame.SaveAs(frameFile);
            var parts=new[]{frame.Occurrences.AddByFilename(vertical),frame.Occurrences.AddByFilename(vertical),frame.Occurrences.AddByFilename(horizontal),frame.Occurrences.AddByFilename(horizontal)};
            var origins=new[]{new V3(-.02,0,0),new V3(.5,0,0),new V3(-.02,-.02,0),new V3(-.02,.3,0)};
            for(int i=0;i<4;i++)Place(parts[i],Transform.Frame(origins[i],new V3(1,0,0),new V3(0,1,0),new V3(0,0,1)));
            var faces=new[]{Face(parts[0],new V3(1,0,0),.02),Face(parts[1],new V3(1,0,0),0),Face(parts[2],new V3(0,1,0),.02),Face(parts[3],new V3(0,1,0),0)};
            var refs=parts.Select((p,i)=>frame.CreateReference(p,faces[i])).ToArray();
            var body=(G.Body)((P.PartDocument)parts[0].OccurrenceDocument).Models.Item(1).Body;
            var vertex=(G.Vertex)((G.Vertices)body.Vertices).Item(1);var vertexRef=frame.CreateReference(parts[0],vertex);Array pv=new double[3];vertex.GetPointData(ref pv);
            Assert((PickGeometry.Unwrap(vertexRef).Point()-(V3.From(pv)+origins[0])).Length<1e-9,"vertex reference transformed");
            var edge=(G.Edge)((G.Edges)body.Edges[G.FeatureTopologyQueryTypeConstants.igQueryAll]).Item(1);Array pm=new double[3];edge.GetMiddlePoint(ref pm);var edgeRef=frame.CreateReference(parts[0],edge);
            Assert((PickGeometry.Unwrap(edgeRef).Point()-(V3.From(pm)+origins[0])).Length<1e-9,"line midpoint transformed");
            var point=PickGeometry.Unwrap(edgeRef).Point();var spec=PanelGeometry.Solve(refs.Select(x=>PickGeometry.Unwrap(x).Plane()).ToArray(),point,.006,.001);
            Assert(Math.Abs(spec.Width-.498)<1e-8&&Math.Abs(spec.Height-.298)<1e-8,"four real instance faces give 498 x 298");
            frame.Save();
            // Exercise the real command lifecycle and its event-handler selection path on a new fixture only.
            ((dynamic)app.ActiveWindow).View.Fit();bool dirtyBefore=frame.Dirty;int occurrenceCount=frame.Occurrences.Count;int highlightCount=frame.HighlightSets.Count;
            using(var form=new PanelForm(app,frame)){
                form.Show();Application.DoEvents();form.StartPicking();
                Assert(form.InterDocumentPicking,"assembly mouse enters referenced part documents");
                Assert(form.PickingMode==(int)SolidEdgeConstants.seLocateModes.seLocateSimple&&form.MoveFeedbackEnabled,"native mouse uses simple locate with move events enabled");
                Assert(!form.Modal&&form.ClientSize.Width<500,"selection panel is modeless and compact");
                refs=parts.Select((p,i)=>frame.CreateReference(p,faces[i])).ToArray();
                form.MouseMove(0,0,0,0,0,null,0,refs[0]);Assert(form.HoverMessage.Contains("可选平面")&&form.PickedFaceCount==0,"hover reports a valid face without accepting it");
                form.MouseMove(0,0,0,0,0,null,-1,null);Assert(form.HoverMessage.Contains("未捕捉")&&form.PickedFaceCount==0,"leaving a face clears hover without advancing selection");
                form.MouseClick(1,0,0,0,0,null,-1,null);Assert(form.PickedFaceCount==0&&!string.IsNullOrEmpty(form.Message),"empty model pick gives feedback without advancing");
                refs=parts.Select((p,i)=>frame.CreateReference(p,faces[i])).ToArray();edgeRef=frame.CreateReference(parts[0],edge);foreach(var r in refs)form.MouseClick(1,0,0,0,0,null,0,r);form.MouseClick(1,0,0,0,0,null,0,edgeRef);form.SetParameters("6","1");Application.DoEvents();app.DoIdle();
                Assert(form.PreviewReady,"command accepts face refs and point: "+form.Message);
                using(var bitmap=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,new Rectangle(0,0,form.Width,form.Height));bitmap.Save(Path.Combine(dir,"dialog.png"));}
                ((dynamic)app.ActiveWindow).View.SaveAsImage(Path.Combine(dir,"preview.png"),1200,900);
                Assert(form.PreviewDrawCount>0,"3D preview panel actually painted; "+form.Message);
                form.Close();Application.DoEvents();
            }
            Assert(frame.Occurrences.Count==occurrenceCount&&frame.HighlightSets.Count==highlightCount&&frame.Dirty==dirtyBefore,string.Format("cancel preview: occurrences {0}/{1}, highlights {2}/{3}, dirty {4}/{5}",frame.Occurrences.Count,occurrenceCount,frame.HighlightSets.Count,highlightCount,frame.Dirty,dirtyBefore));
            // Frame is inserted twice into a top assembly, with a non-axis-aligned rotation on instance two.
            top=(A.AssemblyDocument)app.Documents.Add("SolidEdge.AssemblyDocument");top.SaveAs(nestedFile);
            top.Occurrences.AddByFilename(frameFile);var parent=top.Occurrences.AddByFilename(frameFile);
            var u=new V3(1,2,3).Unit();var v=u.Cross(new V3(0,1,0)).Unit();var transform=Transform.Frame(new V3(.8,.4,.2),u,v,u.Cross(v));Place(parent,transform);
            var nestedRefs=new object[4];for(int i=0;i<4;i++)nestedRefs[i]=NestedReference(parent.SubOccurrences.Item(i+1),faces[i]);
            var nestedPoint=NestedReference(parent.SubOccurrences.Item(1),edge);
            var np=PickGeometry.Unwrap(nestedPoint).Point();Assert((np-transform.Point(point)).Length<1e-8,"nested midpoint includes all transforms once");
            var inputs=nestedRefs.Select(r=>PickGeometry.Unwrap(r).Plane()).ToArray();
            for(int i=0;i<4;i++){var expected=transform.Point(PickGeometry.Unwrap(frame.CreateReference(parts[i],faces[i])).Plane().Point);Assert(Math.Abs((inputs[i].Point-expected).Dot(inputs[i].Normal))<1e-8,"nested face plane "+i);}
            var ns=PanelGeometry.Solve(inputs,np,.006,.001);Assert(Math.Abs(ns.Width*ns.Height-.498*.298)<1e-8,"nested rotated frame dimensions");
            CadBuilder.Generate(app,top,ns,Path.Combine(dir,"NestedPanel.par"));Assert(top.Occurrences.Count==3,"panel added to top assembly alongside two frame instances");
            top.Save();((dynamic)app.ActiveWindow).View.Fit();((dynamic)app.ActiveWindow).View.SaveAsImage(Path.Combine(dir,"nested.png"),1200,900);
            int before=top.Occurrences.Count;try{CadBuilder.Generate(app,top,ns,Path.Combine(dir,"missing","fail.par"));throw new Exception("Missing folder was accepted");}catch(DirectoryNotFoundException){Assert(top.Occurrences.Count==before,"missing directory does not create occurrence");}
            var nestedVertex=NestedReference(parent.SubOccurrences.Item(1),vertex);var datum=top.AsmRefPoints.AddReferencePointByPoint(nestedVertex);
            Assert((PickGeometry.Unwrap(datum).Point()-PickGeometry.Unwrap(nestedVertex).Point()).Length<1e-8,"assembly datum point");
            CircleCase(app,top,dir);
            string racePath=Path.Combine(dir,"PublishRace.par"),marker="owned-by-concurrent-writer";int countBefore=top.Occurrences.Count;
            using(var watcher=new FileSystemWatcher(dir,".panel-*.par")){
                watcher.Created+=(sender,args)=>{try{File.WriteAllText(racePath,marker);}catch{}};watcher.EnableRaisingEvents=true;
                try{CadBuilder.Generate(app,top,ns,racePath);throw new Exception("Publish race did not occur");}catch(InvalidOperationException){Assert(File.Exists(racePath)&&File.ReadAllText(racePath)==marker&&top.Occurrences.Count==countBefore,"publish failure preserves concurrent file and leaves no occurrence");}
                watcher.EnableRaisingEvents=false;
            }
            Assert(!Directory.GetFiles(dir,".panel-*.par").Any(),"no staging file remains");
        }finally{if(top!=null)try{top.Close(false);}catch{}if(frame!=null)try{frame.Close(false);}catch{}}
        Assert(Hash(vertical)==hv&&Hash(horizontal)==hh,"source PAR SHA256 unchanged");
    }
    static void CircleCase(F.Application app,A.AssemblyDocument asm,string dir){
        P.PartDocument part=null;
        try{part=(P.PartDocument)app.Documents.Add("SolidEdge.PartDocument",CadBuilder.TemplatePath);part.ModelingMode=P.ModelingModeConstants.seModelingModeOrdered;
            P.RefPlane xy=null;foreach(P.RefPlane plane in part.RefPlanes){Array n=new double[3];plane.GetNormal(ref n);if(Math.Abs(V3.From(n).Z-1)<1e-8){xy=plane;break;}}
            var profile=part.ProfileSets.Add().Profiles.Add(xy);profile.Circles2d.AddByCenterRadius(.04,.06,.02);profile.End(P.ProfileValidationType.igProfileClosed);Array profiles=new object[]{profile};part.Models.AddFiniteExtrudedProtrusion(1,ref profiles,P.FeaturePropertyConstants.igSymmetric,.01);
            string path=Path.Combine(dir,"CircleFixture.par");part.SaveAs(path);part.Close(false);part=null;asm.Activate();var occ=asm.Occurrences.AddByFilename(path);var translation=new V3(.6,.7,.8);Place(occ,Transform.Frame(translation,new V3(1,0,0),new V3(0,1,0),new V3(0,0,1)));
            var body=(G.Body)((P.PartDocument)occ.OccurrenceDocument).Models.Item(1).Body;bool checkedCircle=false,checkedCurve=false;
            foreach(G.Edge e in (G.Edges)body.Edges[G.FeatureTopologyQueryTypeConstants.igQueryAll]){var c=e.Geometry as G.Circle;if(c==null)continue;Array center=new double[3];c.GetCenterPoint(ref center);Assert((PickGeometry.Unwrap(asm.CreateReference(occ,e)).Point()-(V3.From(center)+translation)).Length<1e-8,"circle center exact, not clicked point");checkedCircle=true;break;}
            foreach(G.Face f in (G.Faces)body.Faces[G.FeatureTopologyQueryTypeConstants.igQueryAll]){if(f.Geometry is G.Plane)continue;try{PickGeometry.Unwrap(asm.CreateReference(occ,f)).Plane();throw new Exception("curved face accepted");}catch(ArgumentException){checkedCurve=true;break;}}
            Assert(checkedCircle&&checkedCurve,"circle point and curved face rejection exercised");occ.Delete();
        }finally{if(part!=null)try{part.Close(false);}catch{}asm.Activate();}
    }
}
