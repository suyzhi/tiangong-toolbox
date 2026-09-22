using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using TianGongCadSuite;
using F=SolidEdgeFramework;
using A=SolidEdgeAssembly;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;
class Program {
    public static F.Application Host=null;
    static int checks;
    static void Assert(bool ok,string label){if(!ok)throw new Exception("FAIL: "+label);checks++;Console.WriteLine("PASS: "+label);}
    static PlaneInput[] Frame(){return new[]{new PlaneInput(new V3(0,0,0),new V3(1,0,0),"L"),new PlaneInput(new V3(.5,0,0),new V3(-1,0,0),"R"),new PlaneInput(new V3(0,0,0),new V3(0,1,0),"B"),new PlaneInput(new V3(0,.3,0),new V3(0,-1,0),"T")};}
    static void Reject(Action f,string label){try{f();}catch(ArgumentException){Assert(true,label);return;}throw new Exception("Expected rejection: "+label);}
    static void Pure(){
        CommandRoutingTests.Pure();LineupDataTests.Pure();FormatConvertTests.Pure();
        var s=PanelGeometry.Solve(Frame(),new V3(20,30,.05),.006,.001);
        Assert(Math.Abs(s.Width-.498)<1e-10&&Math.Abs(s.Height-.298)<1e-10,"498 x 298 mm");Assert(Math.Abs(s.Origin.Z-.05)<1e-10,"point fixes only depth");
        int permutations=0;for(int a=0;a<4;a++)for(int b=0;b<4;b++)for(int c=0;c<4;c++)for(int d=0;d<4;d++){int[] ids={a,b,c,d};if(ids.Distinct().Count()!=4)continue;for(int mask=0;mask<16;mask++){var f=Frame();for(int i=0;i<4;i++)if((mask&(1<<i))!=0)f[i].Normal=f[i].Normal*(-1);var p=PanelGeometry.Solve(ids.Select(i=>f[i]).ToArray(),new V3(20,30,.05),.006,.001);if((p.Origin-s.Origin).Length>1e-9||Math.Abs(p.Width-s.Width)>1e-9||(p.N-s.N).Length>1e-9)throw new Exception("selection invariant failed");permutations++;}}
        Assert(permutations==384,"24 pick orders x 16 normal flips");
        var u=new V3(1,2,3).Unit();var v=u.Cross(new V3(1,0,0)).Unit();var t=Transform.Frame(new V3(1,2,3),u,v,u.Cross(v));
        var transformed=Frame().Select(p=>new PlaneInput(t.Point(p.Point),t.Normal(p.Normal),p.Label)).ToArray();var r=PanelGeometry.Solve(transformed,t.Point(new V3(.1,.2,.05)),.006,.001);
        Assert(Math.Abs(r.Width*r.Height-.498*.298)<1e-10,"arbitrary rotation area");
        for(int i=0;i<4;i++)Assert(Math.Abs((r.Corner(i,0)-t.Point(new V3(0,0,.05))).Dot(t.Vector(new V3(0,0,1))))<1e-9,"rotated corner on picked plane "+i);
        Reject(()=>PanelGeometry.Solve(Frame(),new V3(),0,0),"zero thickness");Reject(()=>PanelGeometry.Solve(Frame(),new V3(),double.NaN,0),"NaN thickness");Reject(()=>PanelGeometry.Solve(Frame(),new V3(),.006,-.001),"negative gap");Reject(()=>PanelGeometry.Solve(Frame(),new V3(),.006,.15),"collapsed height");
        var dup=Frame();dup[1]=dup[0];Reject(()=>PanelGeometry.Solve(dup,new V3(),.006,0),"duplicate planes");var skew=Frame();skew[2].Normal=new V3(.1,1,0).Unit();Reject(()=>PanelGeometry.Solve(skew,new V3(),.006,0),"skew frame");
    }
    [STAThread]static int Main(string[] args){try{if(args.Length>0&&args[0]=="--commands"){var registry=ModuleCatalog.Create(new ToolContext(null));foreach(var command in registry.Commands)Console.WriteLine("COMMAND "+command.Id+" "+command.Title+" | "+command.Description);Console.WriteLine("COMMAND COUNT "+registry.Commands.Count);return 0;}if(args.Length>1&&args[0]=="--entry-reopen"){using(new OleFilter())LineupEntryTests.Reopen(Host,args[1]);return 0;}if(args.Length>1&&args[0]=="--entry"){LineupEntryTests.Pure(args[1]);return 0;}if(args.Length>1&&args[0]=="--entry-cad"){using(new OleFilter())LineupEntryTests.Native(Host,args[1]);return 0;}if(args.Length>1&&args[0]=="--hierarchy"){LineupHierarchyTests.Pure(args[1]);return 0;}if(args.Length>1&&args[0]=="--location-cad"){using(new OleFilter())LineupHierarchyTests.Native(args[1]);return 0;}if(args.Length>1&&args[0]=="--smart"){LineupSmartTests.Pure(args[1]);return 0;}if(args.Length>1&&args[0]=="--smart-cad"){using(new OleFilter())LineupSmartTests.Native(Host,args[1]);return 0;}if(args.Length>1&&args[0]=="--table"){LineupTableTests.Pure(args[1]);return 0;}if(args.Length>1&&args[0]=="--table-cad"){using(new OleFilter())LineupTableTests.Native(Host,args[1]);return 0;}if(args.Length>0&&args[0]=="--lineup-reopen"){using(new OleFilter())LineupNativeTests.Reopen(Host,args[1]);return 0;}if(args.Length>0&&args[0]=="--lineup"){CommandRoutingTests.Pure();LineupDataTests.Pure();using(new OleFilter())LineupNativeTests.Run(Host,args[1]);return 0;}Pure();AutoTests.Pure();if(args.Length>0&&args[0]=="--auto"){using(new OleFilter()){string dir=Path.GetFullPath(Path.Combine(args[1],"auto-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")));Directory.CreateDirectory(dir);SectionTests.Native(Host,dir);}}if(args.Length>0&&args[0]=="--cad"){using(new OleFilter())Cad(args.Length>1?args[1]:"artifacts");}Console.WriteLine("CORE ASSERTIONS PASSED "+checks+"; advanced results are listed above.");return 0;}catch(Exception e){Console.Error.WriteLine(e);return 1;}}
    static void Cad(string output){
        string dir=Path.GetFullPath(Path.Combine(output,"cad-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")));Directory.CreateDirectory(dir);
        var app=Host??(F.Application)Marshal.GetActiveObject("SolidEdge.Application");object original=null;try{original=app.ActiveDocument;}catch{}
        A.AssemblyDocument asm=null;P.PartDocument opened=null;
        try {
            asm=(A.AssemblyDocument)app.Documents.Add("SolidEdge.AssemblyDocument");
            asm.SaveAs(Path.Combine(dir,"PanelFixture.asm"));
            var spec=PanelGeometry.Solve(Frame(),new V3(0,0,.05),.006,.001);var occ=CadBuilder.Generate(app,asm,spec,Path.Combine(dir,"Panel.par"));
            Assert(asm.Occurrences.Count==1,"insert one panel");
            var part=(P.PartDocument)occ.OccurrenceDocument;CadBuilder.CheckBody((G.Body)part.Models.Item(1).Body,.498,.298,.006);Assert(true,"CAD body range symmetric total 6 mm");
            Assert(part.ProfileSets.Count==1&&part.Models.Item(1).ExtrudedProtrusions.Count==1,"editable profile + extrusion");
            asm.Save();asm.Close(false);asm=null;
            opened=(P.PartDocument)app.Documents.Open(Path.Combine(dir,"Panel.par"));
            var ext=opened.Models.Item(1).ExtrudedProtrusions.Item(1);Console.WriteLine("DEPTH "+ext.Depth+" SIDE "+ext.ExtentSide);ext.Depth=.008;
            CadBuilder.CheckBody((G.Body)opened.Models.Item(1).Body,.498,.298,.008);Assert(true,"edit thickness to symmetric 8 mm");
            var profile=opened.ProfileSets.Item(1).Profiles.Item(1);var dimensions=(SolidEdgeFrameworkSupport.Dimensions)profile.Dimensions;dimensions.Item(1).Value=.52;CadBuilder.CheckBody((G.Body)opened.Models.Item(1).Body,.52,.298,.008);Assert(true,"edit constrained sketch width to 520 mm");
            opened.Save();opened.Close(false);opened=null;
            asm=(A.AssemblyDocument)app.Documents.Open(Path.Combine(dir,"PanelFixture.asm"));Assert(asm.Occurrences.Count==1,"reopen saved assembly");
            Array matrix=new double[16];asm.Occurrences.Item(1).GetMatrix(ref matrix);Assert(Math.Abs(Convert.ToDouble(matrix.GetValue(14))-.05)<1e-9,"saved placement retained");
            try{CadBuilder.Generate(app,asm,spec,Path.Combine(dir,"Panel.par"));throw new Exception("collision not rejected");}catch(IOException){Assert(asm.Occurrences.Count==1,"collision rejects without adding occurrence");}
            ((dynamic)app.ActiveWindow).View.Fit();((dynamic)app.ActiveWindow).View.SaveAsImage(Path.Combine(dir,"panel.png"),1200,900);
            Advanced.Run(app,dir);AutoTests.Native(app,dir);
            Console.WriteLine("ARTIFACTS "+dir);
        } finally {if(opened!=null)try{opened.Close(false);}catch{}if(asm!=null)try{asm.Close(false);}catch{}try{((dynamic)original).Activate();}catch{}}
    }
}
