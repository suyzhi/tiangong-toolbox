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

// 隔离实验：每次只做一件事（每次跑都在**全新副本**上），用来说明"孔打不出来"到底取决于什么
//   SeqProbe.exe <asmPath> <partContains> <mode>
//     through         : 只打一个贯通孔（看写操作本身通不通）
//     blind-top       : 只在上表面打盲孔（基准面由生产逻辑建）
//     blind-bottom    : 只在下表面打盲孔（基准面=零件自带基面，位置对、法向朝外）
//     preplane-blind  : 先用"面片"建共面基准面（法向朝外），再在上表面打盲孔
class SeqProbe {
    static F.Application app; static object M=Type.Missing;
    static void L(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    static string N(double v){ return v.ToString("0.######", CultureInfo.InvariantCulture); }
    static double[] Arr(Array a){ return new[]{ Convert.ToDouble(a.GetValue(0)), Convert.ToDouble(a.GetValue(1)), Convert.ToDouble(a.GetValue(2)) }; }
    static string S(Array a){ var v=Arr(a); return "("+N(v[0])+", "+N(v[1])+", "+N(v[2])+")"; }
    static string SV(V3 v){ return "("+N(v.X)+", "+N(v.Y)+", "+N(v.Z)+")"; }
    static void Pump(int n){ for(int i=0;i<n;i++){ try{ app.DoIdle(); }catch{} Thread.Sleep(60); } }
    static IEnumerable<P.RefPlane> AllPlanes(P.PartDocument part){
        int n=0; try{ n=part.RefPlanes.Count; }catch{}
        for(int i=1;i<=n;i++){ P.RefPlane rp=null; try{ rp=part.RefPlanes.Item(i); }catch{} if(rp!=null) yield return rp; }
    }
    static string Safe(Func<string> f){ try{ return f(); }catch(Exception e){ return "<"+e.Message+">"; } }

    [STAThread] static int Main(string[] args){
        Console.OutputEncoding=System.Text.Encoding.UTF8;
        if(args.Length<3){ L("用法: SeqProbe.exe <asmPath> <partContains> <through|blind-top|blind-bottom|preplane-blind>"); return 1; }
        string asmPath=args[0], want=args[1], mode=args[2].ToLowerInvariant();
        try{ app=(F.Application)Marshal.GetActiveObject("SolidEdge.Application"); }catch(Exception e){ L("连不上 CAD："+e.Message); return 2; }
        if(mode=="fixture"){
            // 和 tests/AutoHoleTests.cs 的夹具一样：100×100×20 的板，XY 基准面对称拉伸
            string tpl = @"C:\Program Files\NDS\TianGong 2025\Template\ISO Metric\iso metric part.par";
            var pd=(P.PartDocument)app.Documents.Add("SolidEdge.PartDocument", tpl);
            pd.Activate();
            pd.ModelingMode=P.ModelingModeConstants.seModelingModeOrdered;
            P.RefPlane xy=null;
            foreach(P.RefPlane c in pd.RefPlanes){ Array n=new double[3], p=new double[3], u=new double[3];
                c.GetNormal(ref n); c.GetRootPoint(ref p); c.GetReferenceDirection(ref u);
                if(Math.Abs(Arr(n)[2]-1)<1e-8 && Math.Abs(Arr(p)[0])<1e-8 && Math.Abs(Arr(p)[1])<1e-8 && Math.Abs(Arr(u)[0]-1)<1e-8){ xy=c; break; } }
            if(xy==null){ L("模板缺少标准 XY 基准面"); return 9; }
            var prof=pd.ProfileSets.Add().Profiles.Add(xy);
            var seg=new SolidEdgeFrameworkSupport.Line2d[4];
            seg[0]=prof.Lines2d.AddBy2Points(0,0,0.1,0); seg[1]=prof.Lines2d.AddBy2Points(0.1,0,0.1,0.1);
            seg[2]=prof.Lines2d.AddBy2Points(0.1,0.1,0,0.1); seg[3]=prof.Lines2d.AddBy2Points(0,0.1,0,0);
            var rel=(SolidEdgeFrameworkSupport.Relations2d)prof.Relations2d;
            for(int i=0;i<4;i++){ rel.AddKeypoint(seg[i],(int)SolidEdgeConstants.KeypointIndexConstants.igLineEnd,seg[(i+1)%4],(int)SolidEdgeConstants.KeypointIndexConstants.igLineStart); if(i%2==0) rel.AddHorizontal(seg[i]); else rel.AddVertical(seg[i]); }
            rel.AddKeypointFix(seg[0],(int)SolidEdgeConstants.KeypointIndexConstants.igLineStart);
            var dims=(SolidEdgeFrameworkSupport.Dimensions)prof.Dimensions; dims.Constraint=true; dims.AddLength(seg[0]); dims.AddLength(seg[1]);
            if(prof.End(P.ProfileValidationType.igProfileClosed)!=0){ L("矩形轮廓未闭合"); return 9; }
            Array arr=new object[]{prof};
            var fm=pd.Models.AddFiniteExtrudedProtrusion(1, ref arr, P.FeaturePropertyConstants.igSymmetric, 0.02);
            Pump(4);
            G.Face top=null; double bestArea2=0;
            foreach(G.Face f in (G.Faces)((G.Body)fm.Body).get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll)){
                var pl=f.Geometry as G.Plane; if(pl==null) continue;
                Array p=new double[3], n=new double[3]; pl.GetPlaneData(ref p, ref n);
                if(Math.Abs(Arr(n)[2])<0.999) continue;
                if(Arr(p)[2] <= 0) continue;              // 只要上表面
                double area=0; try{ area=f.Area; }catch{}
                if(area>bestArea2){ bestArea2=area; top=f; }
            }
            if(top==null){ L("找不到夹具上表面"); return 9; }
            Array fp=new double[3], fnn=new double[3]; ((G.Plane)top.Geometry).GetPlaneData(ref fp, ref fnn);
            bool frev=false; try{ frev=top.IsParamReversed; }catch{}
            var ftarget=new TargetFace{ Plane=new PlaneInput(V3.From(fp), V3.From(fnn), "打孔面"), Face=top, Part=pd, Placement=Transform.Identity, Label="打孔面", PartName="FixturePlate" };
            L("夹具上表面 点="+S(fp)+" 几何法向="+S(fnn)+" IsParamReversed="+frev+" 面积="+N(bestArea2));
            var ff=FaceFrameReader.Read(ftarget);
            foreach(var fspec in new[]{ new HoleSpec{ Kind=HoleKind.Tapped, HoleDiameter=2.46, ThreadSize="M3", Depth=10, Bottom=HoleBottom.Flat },
                                       new HoleSpec{ Kind=HoleKind.Tapped, HoleDiameter=2.46, ThreadSize="M3", Depth=6,  Bottom=HoleBottom.Flat } }){
                var c1=ff.Origin + ff.Direction * ((fspec.Depth==10?0.4:0.6)*ff.LengthMm*0.001);
                var rq=new List<AutoHoleWriter.HoleRequest>();
                rq.Add(new AutoHoleWriter.HoleRequest{ Spec=fspec, Centre=c1, Source="夹具盲孔深"+N(fspec.Depth) });
                var rr=AutoHoleWriter.DrillRequests(ftarget, rq);
                L("  夹具盲孔深"+N(fspec.Depth)+" → Created="+rr.Created+" Failures="+rr.Failures.Count);
                foreach(var f in rr.Failures) L("    ❌ "+f);
            }
            try{ pd.Close(false); }catch(Exception e){ L("关闭夹具失败："+e.Message); }
            L("夹具回归结束");
            return 0;
        }

        A.AssemblyDocument asm=null;
        for(int i=1;i<=app.Documents.Count && asm==null;i++){ try{ var d=app.Documents.Item(i) as A.AssemblyDocument; if(d!=null && string.Equals(d.FullName, asmPath, StringComparison.OrdinalIgnoreCase)) asm=d; }catch{} }
        if(asm==null){ try{ asm=(A.AssemblyDocument)app.Documents.Open(asmPath); }catch(Exception e){ L("打开装配失败："+e.Message); return 3; } }
        try{ asm.Activate(); }catch{}
        Pump(8);
        L("当前活动文档="+Safe(()=>((F.SolidEdgeDocument)app.ActiveDocument).FullName));

        A.Occurrence occ=null;
        foreach(A.Occurrence o in asm.Occurrences){ string nm=null; try{ nm=o.Name; }catch{} if(nm!=null && nm.IndexOf(want, StringComparison.OrdinalIgnoreCase)>=0){ occ=o; break; } }
        if(occ==null){ L("找不到实例"); return 4; }
        var part=(P.PartDocument)occ.OccurrenceDocument;
        var model=(P.Model)part.Models.Item(1);
        var body=(G.Body)model.Body;
        var mm=new double[16]; { Array m=new double[16]; occ.GetMatrix(ref m); for(int k=0;k<16;k++) mm[k]=Convert.ToDouble(m.GetValue(k)); }
        var xf=new Transform(mm);

        bool bottom = mode=="blind-bottom";
        G.Face best=null; double bestArea=0, bestZ=bottom?double.MaxValue:double.MinValue;
        foreach(G.Face f in (G.Faces)body.get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll)){
            var pl=f.Geometry as G.Plane; if(pl==null) continue;
            Array p=new double[3], n=new double[3]; try{ pl.GetPlaneData(ref p, ref n); }catch{ continue; }
            if(Math.Abs(xf.Normal(V3.From(n)).Unit().Z)<0.99) continue;
            double area=0; try{ area=f.Area; }catch{}
            double z=xf.Point(V3.From(p)).Z;
            if(area>bestArea*0.5 && (bottom ? z<bestZ : z>bestZ)){ bestZ=z; bestArea=area; best=f; }
        }
        if(best==null){ L("找不到面"); return 5; }
        if(mode=="flip"){
            Array fp0=new double[3], fn0=new double[3]; ((G.Plane)best.Geometry).GetPlaneData(ref fp0, ref fn0);
            V3 lfpt=V3.From(fp0), lfnv=V3.From(fn0).Unit();
            bool rev=false; try{ rev=best.IsParamReversed; }catch{}
            var outN = rev ? lfnv * -1.0 : lfnv;
            L("面片 IsParamReversed=" + rev + "  几何法向=" + SV(lfnv) + "  外法向=" + SV(outN));
            P.RefPlane par=null; V3 pr=new V3(); V3 pn=new V3();
            foreach(P.RefPlane rp in AllPlanes(part)){
                try{ Array rn=new double[3], rr=new double[3]; rp.GetNormal(ref rn); rp.GetRootPoint(ref rr);
                     var n=V3.From(rn).Unit(); if(Math.Abs(n.Dot(lfnv))<0.999) continue; par=rp; pr=V3.From(rr); pn=n; break; }catch{}
            }
            if(par==null){ L("没有平行基准面"); return 0; }
            L("平行基准面 root="+SV(pr)+" normal="+SV(pn));
            double mag=Math.Abs((lfpt-pr).Dot(lfnv));
            foreach(var sd in new[]{ P.ReferenceElementConstants.igNormalSide, P.ReferenceElementConstants.igReverseNormalSide }){
              foreach(var fl in new object[]{ (object)false, (object)true }){
                try{
                    var np=part.RefPlanes.AddParallelByDistance(par, mag, sd, M, M, M, fl);
                    Array r=new double[3], n=new double[3]; np.GetRootPoint(ref r); np.GetNormal(ref n);
                    var nv=V3.From(n).Unit();
                    L("  side="+sd+" FlipNormal="+fl+" -> root="+S(r)+" normal="+S(n)+"  位置偏差="+N((V3.From(r)-lfpt).Dot(lfnv)*1000)+"mm 法向·外法向="+N(nv.Dot(outN)));
                }catch(Exception e){ L("  side="+sd+" FlipNormal="+fl+" 抛 "+e.GetType().Name+" 0x"+e.HResult.ToString("X8")+" "+e.Message); }
                Pump(2);
              }
            }
            return 0;
        }
        if(mode=="facesides"){
            Array fp0=new double[3], fn0=new double[3]; ((G.Plane)best.Geometry).GetPlaneData(ref fp0, ref fn0);
            V3 lfpt=V3.From(fp0), lfnv=V3.From(fn0).Unit();
            bool rev=false; try{ rev=best.IsParamReversed; }catch{}
            var outN = rev ? lfnv * -1.0 : lfnv;
            L("面片 IsParamReversed="+rev+" 几何法向="+SV(lfnv)+" 外法向="+SV(outN));
            foreach(var sd in new[]{ P.ReferenceElementConstants.igNormalSide, P.ReferenceElementConstants.igReverseNormalSide }){
              foreach(var fl in new object[]{ (object)false, (object)true }){
                try{
                    var np=part.RefPlanes.AddParallelByDistance(best, 0.0, sd, M, M, M, fl);
                    Array r=new double[3], n=new double[3]; np.GetRootPoint(ref r); np.GetNormal(ref n);
                    var nv=V3.From(n).Unit();
                    L("  面片建面 side="+sd+" Flip="+fl+" -> root="+S(r)+" normal="+S(n)+" 位置偏差="+N((V3.From(r)-lfpt).Dot(lfnv)*1000)+"mm 法向·外法向="+N(nv.Dot(outN)));
                }catch(Exception e){ L("  面片建面 side="+sd+" Flip="+fl+" 抛 "+e.GetType().Name+" 0x"+e.HResult.ToString("X8")+" "+e.Message); }
                Pump(2);
              }
            }
            return 0;
        }
        if(mode=="inspect"){
            L("== 所有平面面：几何法向 / IsParamReversed / 体包围盒 ==");
            Array blo=new double[3], bhi=new double[3]; body.GetRange(ref blo, ref bhi);
            L("  体包围盒 "+S(blo)+" .. "+S(bhi));
            foreach(G.Face f in (G.Faces)body.get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll)){
                var pl=f.Geometry as G.Plane; if(pl==null) continue;
                Array p=new double[3], n=new double[3]; try{ pl.GetPlaneData(ref p, ref n); }catch{ continue; }
                double area=0; try{ area=f.Area; }catch{}
                bool rev=false; try{ rev=f.IsParamReversed; }catch{}
                L("  点="+S(p)+" 几何法向="+S(n)+" 面积="+N(area)+" IsParamReversed="+rev);
            }
            return 0;
        }
        object sel=asm.CreateReference(occ, best);
        var target=AutoHoleReader.ReadTarget(sel);
        var fpt=target.Placement.InversePoint(target.Plane.Point);
        var fnv=target.Placement.InverseNormal(target.Plane.Normal).Unit();
        L("模式="+mode+"  目标面局部点="+SV(fpt)+" 局部法向="+SV(fnv)+" 面积="+N(bestArea));

        var mi=typeof(AutoHoleWriter).GetMethod("FindOrCreatePlane", BindingFlags.NonPublic|BindingFlags.Static);
        if(mode=="preplane-blind"){
            try{
                var np=part.RefPlanes.AddParallelByDistance(best, 0.0, P.ReferenceElementConstants.igNormalSide, M,M,M,M);
                Array r=new double[3], n=new double[3]; np.GetRootPoint(ref r); np.GetNormal(ref n);
                L("已用面片建共面基准面 root="+S(r)+" normal="+S(n));
            }catch(Exception e){ L("用面片建基准面失败："+e.Message); }
            Pump(2);
        }
        P.RefPlane plane=null;
        try{ plane=(P.RefPlane)mi.Invoke(null, new object[]{ target.Part, target }); }catch(Exception e){ L("FindOrCreatePlane 异常："+(e.InnerException??e).Message); return 7; }
        if(plane!=null){ Array r=new double[3], n=new double[3]; plane.GetRootPoint(ref r); plane.GetNormal(ref n);
            L("生产逻辑给的打孔基准面 root="+S(r)+" normal="+S(n)+"  离面="+N((V3.From(r)-fpt).Dot(fnv)*1000)+"mm"); }

        if(mode=="flip") return 0;
        var frame=FaceFrameReader.Read(target);
        var centre=frame.Origin + frame.Direction * (frame.LengthMm*0.5*0.001);
        var spec = mode=="through"
            ? new HoleSpec{ Kind=HoleKind.Tapped, HoleDiameter=2.46, ThreadSize="M3", Depth=0, Bottom=HoleBottom.Flat }
            : new HoleSpec{ Kind=HoleKind.Tapped, HoleDiameter=2.46, ThreadSize="M3", Depth=10, Bottom=HoleBottom.Flat };
        var reqs=new List<AutoHoleWriter.HoleRequest>();
        reqs.Add(new AutoHoleWriter.HoleRequest{ Spec=spec, Centre=centre, Source=mode });
        var r1=AutoHoleWriter.DrillRequests(target, reqs);
        L("结果 Created="+r1.Created+" Failures="+r1.Failures.Count+" Notes="+string.Join(" | ", r1.Notes.ToArray()));
        foreach(var f in r1.Failures) L("  ❌ "+f);
        return 0;
    }
}