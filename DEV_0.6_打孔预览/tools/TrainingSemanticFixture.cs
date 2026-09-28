using System;
using System.IO;
using System.Diagnostics;
using TianGongCadSuite;
using F=SolidEdgeFramework;
using P=SolidEdgePart;
using D=SolidEdgeDraft;
using S=SolidEdgeFrameworkSupport;
class TrainingSemanticFixture {
    [STAThread]static int Main(string[] args){
        if(args.Length!=2||Process.GetProcessesByName("TianGong").Length>0)return 2;
        F.Application app=null;object opened=null;string source=Path.GetFullPath(args[1]),hash=TrainingExporter.Hash(source),dir=Path.GetFullPath(args[0]);Directory.CreateDirectory(dir);
        try{using(new OleFilter()){
            app=(F.Application)Activator.CreateInstance(Type.GetTypeFromProgID("SolidEdge.Application"));if(app.Documents.Count>0)throw new Exception("CAD has open documents");app.Visible=true;app.DisplayAlerts=false;
            var part=(P.PartDocument)app.Documents.Add("SolidEdge.PartDocument",CadBuilder.TemplatePath);opened=part;part.ModelingMode=P.ModelingModeConstants.seModelingModeOrdered;
            P.RefPlane xy=null;foreach(P.RefPlane plane in part.RefPlanes){Array n=new double[3];plane.GetNormal(ref n);if(Math.Abs(Convert.ToDouble(n.GetValue(2))-1)<1e-8){xy=plane;break;}}
            var profile=part.ProfileSets.Add().Profiles.Add(xy);profile.Circles2d.AddByCenterRadius(0,0,.05);profile.End(P.ProfileValidationType.igProfileClosed);Array profiles=new object[]{profile};var model=part.Models.AddFiniteExtrudedProtrusion(1,ref profiles,P.FeaturePropertyConstants.igRight,.02);
            var hp=part.ProfileSets.Add().Profiles.Add(xy);hp.Holes2d.Add(0,0);hp.End(P.ProfileValidationType.igProfileClosed);
            var data=part.HoleDataCollection.Add(P.FeaturePropertyConstants.igRegularHole,.008);model.Holes.AddThroughAll(hp,P.FeaturePropertyConstants.igRight,data);
            var cp=part.ProfileSets.Add().Profiles.Add(xy);cp.Holes2d.Add(.025,0);cp.End(P.ProfileValidationType.igProfileClosed);var countersink=part.HoleDataCollection.Add(P.FeaturePropertyConstants.igCountersinkHole,.006,CountersinkDiameter:.012,CountersinkAngle:90,BottomAngle:0);model.Holes.AddThroughAll(cp,P.FeaturePropertyConstants.igRight,countersink);
            var bp=part.ProfileSets.Add().Profiles.Add(xy);bp.Holes2d.Add(-.025,0);bp.End(P.ProfileValidationType.igProfileClosed);var counterbore=part.HoleDataCollection.Add(P.FeaturePropertyConstants.igCounterboreHole,.006,CounterboreDiameter:.012,CounterboreDepth:.004);model.Holes.AddThroughAll(bp,P.FeaturePropertyConstants.igRight,counterbore);
            string partFile=Path.Combine(dir,"native-hole-fixture.par");part.SaveAs(partFile);Console.WriteLine("FIXTURE part saved");Console.WriteLine("SAMPLE "+new TrainingExporter(app).Export(part,dir));part.Close(false);opened=null;
            string copy=Path.Combine(dir,"native-annotation-fixture.dft");File.Copy(source,copy,false);var draft=(D.DraftDocument)app.Documents.Open(copy);opened=draft;var sheet=draft.ActiveSheet;var view=sheet.DrawingViews.Item(1);var edge=view.DVLines2d.Item(1);double x,y,sx,sy;edge.GetStartPoint(out x,out y);view.ViewToSheet(x,y,out sx,out sy);var reference=view.GetReferenceToGraphicMember2[edge];
            var datum=((S.DatumFrames)sheet.DatumFrames).AddByTerminator(reference,sx,sy,0,false);datum.Datum="A";datum.AddVertex(sx-.015,sy+.02,0);
            var gdt=((S.FeatureControlFrames)sheet.FeatureControlFrames).AddByTerminator(reference,sx,sy,0,false);gdt.PrimaryFrame="FLVB0.001MCVBA";gdt.AddVertex(sx+.02,sy+.025,0);
            var rough=((S.SurfaceFinishSymbols)sheet.SurfaceFinishSymbols).AddByTerminator(reference,sx,sy,0,false);rough.RoughnessValue="3.2";rough.AddVertex(sx+.035,sy+.04,0);
            var detail=sheet.DrawingViews.AddDetailView(view,sx,sy,.015,view.ScaleFactor*2,.30,.20,false);Console.WriteLine("FIXTURE detail view created");
            var cut=view.CuttingPlanes.Add();cut.Profile.Lines2d.AddBy2Points(sx-.04,sy,sx+.04,sy);var section=cut.CreateView();Console.WriteLine("FIXTURE section view created");
            draft.Save();Console.WriteLine("FIXTURE annotations saved");Console.WriteLine("SAMPLE "+new TrainingExporter(app).Export(draft,dir));draft.Close(false);opened=null;
            return 0;
        }}catch(Exception e){Console.Error.WriteLine(e);return 1;}finally{if(opened!=null)try{((dynamic)opened).Close(false);}catch{}if(app!=null)try{app.Quit();}catch{}if(TrainingExporter.Hash(source)!=hash)throw new IOException("Original source changed");}
    }
}
