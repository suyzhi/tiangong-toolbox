using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using TianGongCadSuite;
using A=SolidEdgeAssembly;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;
using F=SolidEdgeFramework;

// 方向约定 + 强制正确基准面 的对照实验（都在装配副本上跑）
//   用法: SideProbe.exe <asmPath> <partContains> <mode>
//     mode=sides    : 从"与目标面平行的已有基准面"用 igNormalSide / igReverseNormalSide 各建一张，打印根点法向
//     mode=preplane : 先用"面片"建一张与目标面共面的基准面，再走生产逻辑 FindOrCreatePlane + 打盲孔
class SideProbe {
    static F.Application app;
    static object M=Type.Missing;
    static void L(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    static string N(double v){ return v.ToString("0.######", CultureInfo.InvariantCulture); }
    static double[] Arr(Array a){ return new[]{ Convert.ToDouble(a.GetValue(0)), Convert.ToDouble(a.GetValue(1)), Convert.ToDouble(a.GetValue(2)) }; }
    static string S(Array a){ var v=Arr(a); return "("+N(v[0])+", "+N(v[1])+", "+N(v[2])+")"; }
    static string SV(V3 v){ return "("+N(v.X)+", "+N(v.Y)+", "+N(v.Z)+")"; }
    static void Pump(int n){ for(int i=0;i<n;i++){ try{ app.DoIdle(); }catch{} Thread.Sleep(60); } }
    static string Safe(Func<string> f){ try{ return f(); }catch(Exception e){ return "<"+e.GetType().Name+": "+e.Message+">"; } }

    [STAThread] static int Main(string[] args){
        Console.OutputEncoding=System.Text.Encoding.UTF8;
        if(args.Length<3){ L("用法: SideProbe.exe <asmPath> <partContains> <sides|preplane>"); return 1; }
        string asmPath=args[0], want=args[1], mode=args[2].ToLowerInvariant();
        try{ app=(F.Application)Marshal.GetActiveObject("SolidEdge.Application"); }catch(Exception e){ L("连不上 CAD："+e.Message); return 2; }
        A.AssemblyDocument asm=null;
        for(int i=1;i<=app.Documents.Count && asm==null;i++){ try{ var d=app.Documents.Item(i) as A.AssemblyDocument; if(d!=null && string.Equals(d.FullName, asmPath, StringComparison.OrdinalIgnoreCase)) asm=d; }catch{} }
        if(asm==null){ try{ asm=(A.AssemblyDocument)app.Documents.Open(asmPath); }catch(Exception e){ L("打开装配失败："+e.Message); return 3; } }
        try{ asm.Activate(); }catch{}
        Pump(6);

        A.Occurrence occ=null;
        foreach(A.Occurrence o in asm.Occurrences){ string nm=null; try{ nm=o.Name; }catch{} if(nm!=null && nm.IndexOf(want, StringComparison.OrdinalIgnoreCase)>=0){ occ=o; break; } }
        if(occ==null){ L("找不到实例"); return 4; }
        var part=(P.PartDocument)occ.OccurrenceDocument;
        var model=(P.Model)part.Models.Item(1);
        var body=(G.Body)model.Body;
        var mm=new double[16]; { Array m=new double[16]; occ.GetMatrix(ref m); for(int k=0;k<16;k++) mm[k]=Convert.ToDouble(m.GetValue(k)); }
        var xf=new Transform(mm);

        G.Face best=null; double bestArea=0; double bestZ=double.MinValue;
        foreach(G.Face f in (G.Faces)body.get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll)){
            var pl=f.Geometry as G.Plane; if(pl==null) continue;
            Array p=new double[3], n=new double[3]; try{ pl.GetPlaneData(ref p, ref n); }catch{ continue; }
            if(Math.Abs(xf.Normal(V3.From(n)).Unit().Z)<0.99) continue;
            double area=0; try{ area=f.Area; }catch{}
            double z=xf.Point(V3.From(p)).Z;
            if(area>bestArea*0.5 && z>bestZ){ bestZ=z; bestArea=area; best=f; }
        }
        if(best==null){ L("找不到面"); return 5; }
        object sel=asm.CreateReference(occ, best);
        var target=AutoHoleReader.ReadTarget(sel);
        var fpt=target.Placement.InversePoint(target.Plane.Point);
        var fnv=target.Placement.InverseNormal(target.Plane.Normal).Unit();
        L("目标面（零件局部）点="+SV(fpt)+" 法向="+SV(fnv)+" 面积="+N(bestArea));

        if(mode=="sides"){
            // 找与 fnv 平行的第一张已有基准面
            P.RefPlane par=null; V3 pr=new V3();
            foreach(P.RefPlane rp in AllPlanes(part)){
              try{
                Array rn=new double[3], rr=new double[3]; rp.GetNormal(ref rn); rp.GetRootPoint(ref rr);
                if(Math.Abs(V3.From(rn).Unit().Dot(fnv))<0.999) continue;
                par=rp; pr=V3.From(rr); break;
              }catch{}
            }
            if(par==null){ L("找不到平行基准面"); return 6; }
            L("平行基准面 root="+SV(pr));
            double mag=Math.Abs((fpt-pr).Dot(fnv));
            L("mag="+N(mag)+" m");
            foreach(var side in new[]{ P.ReferenceElementConstants.igNormalSide, P.ReferenceElementConstants.igReverseNormalSide }){
                try{
                    var np=part.RefPlanes.AddParallelByDistance(par, mag, side, M,M,M,M);
                    Array r=new double[3], n=new double[3]; np.GetRootPoint(ref r); np.GetNormal(ref n);
                    double off=(V3.From(r)-fpt).Dot(fnv);
                    L("  side="+side+"  ->  root="+S(r)+" normal="+S(n)+" 与目标面轴向偏差="+N(off*1000)+" mm");
                }catch(Exception e){ L("  side="+side+"  抛异常 "+e.GetType().Name+" HR=0x"+e.HResult.ToString("X8")+" "+e.Message); }
                Pump(2);
            }
            return 0;
        }

        // preplane: 先用面片建共面基准面，再走生产逻辑
        try{
            var np=part.RefPlanes.AddParallelByDistance(best, 0.0, P.ReferenceElementConstants.igNormalSide, M,M,M,M);
            Array r=new double[3], n=new double[3]; np.GetRootPoint(ref r); np.GetNormal(ref n);
            L("已用面片建共面基准面 root="+S(r)+" normal="+S(n)+" 与目标面偏差="+N((V3.From(r)-fpt).Dot(fnv)*1000)+" mm");
        }catch(Exception e){ L("用面片建基准面失败："+e.GetType().Name+" HR=0x"+e.HResult.ToString("X8")+" "+e.Message); }
        Pump(2);
        var mi=typeof(AutoHoleWriter).GetMethod("FindOrCreatePlane", BindingFlags.NonPublic|BindingFlags.Static);
        P.RefPlane plane=null;
        try{ plane=(P.RefPlane)mi.Invoke(null, new object[]{ target.Part, target }); }catch(Exception e){ L("FindOrCreatePlane 异常："+(e.InnerException??e).Message); return 7; }
        Array pr2=new double[3], pn2=new double[3]; plane.GetRootPoint(ref pr2); plane.GetNormal(ref pn2);
        L("FindOrCreatePlane 返回 plane root="+S(pr2)+" normal="+S(pn2)+" 与目标面偏差="+N((V3.From(pr2)-fpt).Dot(fnv)*1000)+" mm");

        var frame=FaceFrameReader.Read(target);
        var centre=frame.Origin + frame.Direction * (frame.LengthMm*0.5*0.001);
        var reqs=new List<AutoHoleWriter.HoleRequest>();
        reqs.Add(new AutoHoleWriter.HoleRequest{ Spec=new HoleSpec{ Kind=HoleKind.Tapped, HoleDiameter=2.46, ThreadSize="M3", Depth=10, Bottom=HoleBottom.Flat }, Centre=centre, Source="盲孔深10" });
        L("=== 用这张基准面打盲孔 ===");
        var r1=AutoHoleWriter.DrillRequests(target, reqs);
        L("  Created="+r1.Created+" Failures="+r1.Failures.Count);
        foreach(var f in r1.Failures) L("  ❌ "+f);
        return 0;
    }
    static IEnumerable<P.RefPlane> AllPlanes(P.PartDocument part){
        int n=0; try{ n=part.RefPlanes.Count; }catch{}
        for(int i=1;i<=n;i++){ P.RefPlane rp=null; try{ rp=part.RefPlanes.Item(i); }catch{} if(rp!=null) yield return rp; }
    }
}
