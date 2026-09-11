using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Xml.Serialization;
using TianGongCadSuite;
using F=SolidEdgeFramework;
using A=SolidEdgeAssembly;
using P=SolidEdgePart;
class LineupDemo {
 static F.Application app;static A.AssemblyDocument doc;static LineupProject project;
 static string output;static Dictionary<string,string> files=new Dictionary<string,string>();
 static List<A.Occurrence> all=new List<A.Occurrence>();static Dictionary<string,A.Occurrence> targets=new Dictionary<string,A.Occurrence>();
 static void Box(string name,double w,double h,double t){
  var part=CadBuilder.CreatePart(app,new PanelSpec{Width=w,Height=h,Thickness=t});string file=Path.Combine(output,name+".par");
  foreach(P.RefPlane plane in part.RefPlanes)plane.Visible=false;part.SaveAs(file);part.Close(false);files[name]=file;doc.Activate();
 }
 static A.Occurrence Place(string shape,double x,double y,double z){
  var o=doc.Occurrences.AddByFilename(files[shape]);Array matrix=Transform.Frame(new V3(x,y,z),new V3(1,0,0),new V3(0,1,0),new V3(0,0,1)).M;o.PutMatrix(ref matrix,true);try{o.ReferencePlanesVisible=false;o.CoordinateSystemsVisible=false;}catch{}all.Add(o);return o;
 }
 static LineupRecord Add(string kind,string number,string model,string description,string shape,double x,double y,double z,string island="",string valve="",string cylinder="",string position=""){
  var o=Place(shape,x,y,z);var r=new LineupRecord{Category=kind,Number=number,Model=model,Brand="DEMO",Description=description,Mechanism=number.Contains("Y2")?"B组装配机构":"A组装配机构",IslandId=island,ValveId=valve,CylinderId=cylinder,Position=position,Notes="演示数据 / 简化CAD模型",Home=kind=="气缸"?"缩回":"",Mounting="水平安装"};
  if(kind=="气缸传感器"){r.Address="DI"+project.Records.Count(q=>q.Category==kind).ToString("D3");r.Connector="M8 / 3针";}
  if(kind=="其他电气件")r.Address="DO"+project.Records.Count(q=>q.Category==kind).ToString("D3");
  LineupCad.CaptureOccurrence(o,r);project.Records.Add(r);targets[r.Id]=o;return r;
 }
 [STAThread]static int Main(string[] args){using(new OleFilter())try{
  output=Path.GetFullPath(args[0]);Directory.CreateDirectory(output);
  app=(F.Application)Activator.CreateInstance(Type.GetTypeFromProgID("SolidEdge.Application"));if(app.Documents.Count!=0)throw new Exception("CAD instance is not empty");app.Visible=true;
  if(File.Exists(Path.Combine(output,"LineupDemo.lineup.xml"))){doc=(A.AssemblyDocument)app.Documents.Open(Path.Combine(output,"LineupDemo.asm"));project=LineupProject.Load(Path.Combine(output,"LineupDemo.lineup.xml"),Path.Combine(output,"LineupDemo.asm"));foreach(A.Occurrence o in doc.Occurrences)all.Add(o);foreach(var r in project.Records)targets[r.Id]=(A.Occurrence)LineupCad.ResolveTarget(doc,r);goto capture;}
  doc=(A.AssemblyDocument)app.Documents.Add("SolidEdge.AssemblyDocument");string assembly=Path.Combine(output,"LineupDemo.asm");doc.SaveAs(assembly);project=new LineupProject{AssemblyFile=assembly,NumberPrefix="DEMO"};
  Box("Cylinder",.12,.06,.055);Box("Rod",.08,.018,.018);Box("Sensor",.025,.02,.025);Box("Electrical",.065,.06,.08);Box("Island",.05,.30,.04);Box("Valve",.075,.035,.055);Box("Base",1.15,.95,.015);
  for(int g=1;g<=2;g++){Console.WriteLine("BUILD GROUP "+g);
   double ox=(g-1)*1.35;string key="DEMO-Y"+g;LineupNumbering.SetIsland(project,"DEMO",g,"DEMO-VI-05");
   Place("Base",ox-.08,-.08,-.025);var island=Add("阀岛",key,"DEMO-VI-05","五联阀岛 "+g,"Island",ox+.85,.24,.02);
   for(int i=1;i<=5;i++){
    var valve=Add("阀片",key+"V"+i.ToString("D2"),"DEMO-5/2","第"+i+"路控制阀","Valve",ox+.89,.25+(i-1)*.05,.065,island.Id);
    double x=ox+(i%2==0?.4:0),y=(i-1)*.155;
    string number=key+"C"+i.ToString("D2");var c=Add("气缸",number,"DEMO-CYL-40x80",new[]{"阻挡","止回","顶升","定位","夹紧"}[i-1]+"气缸","Cylinder",x,y,.035,island.Id,valve.Id);
    Place("Rod",x+.12,y+.021,.035);
    Add("气缸传感器",number+"H","DEMO-MAG-H",c.Description+"原位","Sensor",x+.015,y-.035,.025,island.Id,valve.Id,c.Id,"H");
    Add("气缸传感器",number+"W","DEMO-MAG-W",c.Description+"工作位","Sensor",x+.083,y-.035,.025,island.Id,valve.Id,c.Id,"W");
    var e=Add("其他电气件","E"+((g-1)*5+i).ToString("D2"),new[]{"DEMO-RFID","DEMO-CAMERA","DEMO-LIGHT","DEMO-IO","DEMO-BUZZER"}[i-1],new[]{"RFID读写器","检测相机","照明光源","IO模块","蜂鸣器"}[i-1],"Electrical",x+.245,y,.045);e.Mechanism=g==1?"A组装配机构":"B组装配机构";
   }
  }
  var errors=project.Validate();if(errors.Count>0)throw new Exception(string.Join(";",errors));project.Save(Path.ChangeExtension(assembly,".lineup.xml"));doc.Save();
  capture:
  // Start deliberately from a top view, then verify export actually moves to isometric.
  F.View view=(F.View)((dynamic)app.ActiveWindow).View;view.SetCamera(1,.4,5,1,.4,0,0,1,0,false,1);view.Fit();view.Update();view.SaveAsImage(Path.Combine(output,"before-top.png"),1600,1000);
  var overview=LineupLocation.Export(app,doc,project,new string[0],Path.Combine(output,"overview-fit"));Console.WriteLine("PASS camera changed from top to verified orthographic isometric");
  // Four close views keep two sensors per cylinder clearly distinguishable.
  int group=0;
  foreach(string mechanism in new[]{"A组装配机构","B组装配机构"}){
   var records=project.Records.Where(r=>r.Mechanism==mechanism).ToList();
   foreach(string category in new[]{"动作组件","阀岛组件"}){
    group++;var rows=records.Where(r=>category=="阀岛组件"?(r.Category=="阀岛"||r.Category=="阀片"):(r.Category!="阀岛"&&r.Category!="阀片")).ToList();
    foreach(var o in all)o.Visible=false;foreach(var r in rows)targets[r.Id].Visible=true;foreach(var r in rows.Where(r=>r.Category=="气缸")){Array cm=new double[16];targets[r.Id].GetMatrix(ref cm);foreach(var o in all.Where(o=>o.OccurrenceFileName.EndsWith("Rod.par",StringComparison.OrdinalIgnoreCase))){Array rm=new double[16];o.GetMatrix(ref rm);if(Math.Abs(Convert.ToDouble(rm.GetValue(12))-Convert.ToDouble(cm.GetValue(12))-.12)<.001&&Math.Abs(Convert.ToDouble(rm.GetValue(13))-Convert.ToDouble(cm.GetValue(13))-.021)<.001)o.Visible=true;}}
    Console.WriteLine("CAPTURE "+group+" records="+rows.Count);
    var package=LineupLocation.Export(app,doc,project,rows.Select(r=>r.Id),Path.Combine(output,"view-fit"+group));
    Console.WriteLine("RECOGNIZED "+package.Points.Count(p=>p.State=="ok")+"/"+rows.Count);
    if(package.Points.Any(p=>p.State!="ok"))throw new Exception("Unresolved locations in view "+group);
   }
  }
  foreach(var o in all)o.Visible=true;LineupLocation.SetIsometric(app);doc.Save();
  Console.WriteLine("PASS records="+project.Records.Count+" islands=2 valves=10 cylinders=10 sensors=20 electrical=10");
  doc.Close(false);doc=null;app.Quit();return 0;
 }catch(Exception e){Console.Error.WriteLine(e);if(doc!=null)try{doc.Close(false);}catch{}if(app!=null)try{app.Quit();}catch{}return 1;}}
}
