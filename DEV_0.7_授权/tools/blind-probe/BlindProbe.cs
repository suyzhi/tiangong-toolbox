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

// 盲孔诊断探针（在**装配副本**上跑，不碰用户原文件）：
//  1) 打开 <asmPath>
//  2) 找到目标零件实例，选它的"朝上的最大平面"
//  3) 反射调用生产代码 AutoHoleWriter.FindOrCreatePlane，把**实际用的打孔基准面**打在哪儿打出来
//  4) 同一个孔心上先打盲孔（深 N），再打贯通孔 —— 复现"盲孔失败、贯通成功"
// 用法: BlindProbe.exe <asmPath> <partNameContains> [depthMm] [--no-drill]
class BlindProbe {
    static F.Application app;
    static void L(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    static string N(double v){ return v.ToString("0.######", CultureInfo.InvariantCulture); }
    static double[] Arr(Array a){ return new[]{ Convert.ToDouble(a.GetValue(0)), Convert.ToDouble(a.GetValue(1)), Convert.ToDouble(a.GetValue(2)) }; }
    static string S(Array a){ var v=Arr(a); return "("+N(v[0])+", "+N(v[1])+", "+N(v[2])+")"; }
    static string SV(V3 v){ return "("+N(v.X)+", "+N(v.Y)+", "+N(v.Z)+")"; }
    static double[] A3(V3 v){ return new[]{ v.X, v.Y, v.Z }; }
    static void Pump(int n){ for(int i=0;i<n;i++){ try{ app.DoIdle(); }catch{} Thread.Sleep(80); } }

    static string Try(Func<string> f){ try{ return f(); }catch(Exception e){ return "<"+e.GetType().Name+": "+e.Message+">"; } }

    static A.Occurrence FindOcc(A.AssemblyDocument asm, string want){
        foreach(A.Occurrence o in asm.Occurrences){
            string nm=null; try{ nm=o.Name; }catch{}
            if(nm!=null && nm.IndexOf(want, StringComparison.OrdinalIgnoreCase)>=0) return o;
        }
        return null;
    }

    static P.PartDocument PartOf(A.Occurrence o){
        try{ return o.OccurrenceDocument as P.PartDocument; }catch{ return null; }
    }

    [STAThread] static int Main(string[] args){
        Console.OutputEncoding=System.Text.Encoding.UTF8;
        if(args.Length<2){ L("用法: BlindProbe.exe <asmPath> <partNameContains> [depthMm] [--no-drill]"); return 1; }
        string asmPath=args[0], want=args[1];
        double depthMm = args.Length>2 && !args[2].StartsWith("--") ? double.Parse(args[2], CultureInfo.InvariantCulture) : 10.0;
        bool drill = Array.IndexOf(args, "--no-drill") < 0;

        try{ app=(F.Application)Marshal.GetActiveObject("SolidEdge.Application"); }
        catch(Exception e){ L("连不上 CAD："+e.Message); return 2; }
        L("已连上 CAD "+Try(()=>app.Version));
        Pump(4);

        A.AssemblyDocument asm=null;
        for(int i=1;i<=app.Documents.Count && asm==null;i++){
            try{ var d=app.Documents.Item(i) as A.AssemblyDocument; if(d!=null && string.Equals(d.FullName, asmPath, StringComparison.OrdinalIgnoreCase)) asm=d; }catch{}
        }
        if(asm==null){
            try{ asm=(A.AssemblyDocument)app.Documents.Open(asmPath); }
            catch(Exception e){ L("打开装配失败："+e.Message); return 3; }
        }
        try{ asm.Activate(); }catch{}
        L("装配 = "+Try(()=>asm.FullName));
        Pump(8);

        A.Occurrence occ=FindOcc(asm, want);
        if(occ==null){ L("找不到实例："+want); return 4; }
        var part=PartOf(occ);
        L("实例 "+Try(()=>occ.Name)+" -> 零件 "+Try(()=>part.FullName));
        var model=(P.Model)part.Models.Item(1);
        var body=(G.Body)model.Body;
        Array lo=new double[3], hi=new double[3]; body.GetRange(ref lo, ref hi);
        L("零件体包围盒 "+S(lo)+" .. "+S(hi));

        // 选"朝上的最大平面"：面的平面法向经实例矩阵变换后 ≈ +Z，面积最大
        var mm=new double[16]; { Array m=new double[16]; occ.GetMatrix(ref m); for(int k=0;k<16;k++) mm[k]=Convert.ToDouble(m.GetValue(k)); }
        var xf=new Transform(mm);
        G.Face best=null; double bestArea=0; Array bestN=null; Array bestP=null; double bestZ=double.MinValue;
        foreach(G.Face f in (G.Faces)body.get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll)){
            var pl=f.Geometry as G.Plane; if(pl==null) continue;
            Array p=new double[3], n=new double[3];
            try{ pl.GetPlaneData(ref p, ref n); }catch{ continue; }
            var nAsm=xf.Normal(V3.From(n)).Unit();
            if(Math.Abs(nAsm.Z) < 0.99) continue;                 // 只看法向沿装配 Z 的面（板的面片法向可能朝内，故取绝对值）
            double area=0; try{ area=f.Area; }catch{}
            double zAsm=xf.Point(V3.From(p)).Z;                    // 面积最大 + 装配 Z 最高 = 上表面
            L("  候选平面面：面积="+N(area)+"  装配Z="+N(zAsm)+"  局部点="+S(p)+" 局部法向="+S(n)+" 装配法向="+SV(nAsm));
            if(area>bestArea*0.5 && zAsm>bestZ){ bestZ=zAsm; bestArea=area; best=f; bestN=n; bestP=p; }
            else if(area>bestArea){ bestArea=area; best=f; bestN=n; bestP=p; }
        }
        if(best==null){ L("找不到朝上的平面"); return 5; }
        L("选中面：面积="+N(bestArea)+" m²  零件局部点="+S(bestP)+" 局部法向="+S(bestN)+"  面ID="+Try(()=>best.ID.ToString()));

        object sel=null;
        TargetFace target=null;
        try{
            sel=asm.CreateReference(occ, best);
            target=AutoHoleReader.ReadTarget(sel);
        }catch(Exception e){ L("构造 TargetFace 失败："+e.GetType().Name+" "+e.Message); return 6; }
        L("TargetFace: 装配坐标点="+SV(target.Plane.Point)+" 法向="+SV(target.Plane.Normal)+" 零件="+target.PartName);
        var fpt=target.Placement.InversePoint(target.Plane.Point);
        var fnv=target.Placement.InverseNormal(target.Plane.Normal).Unit();
        L("零件局部：面点="+SV(fpt)+"  面法向="+SV(fnv));

        // 反射调用生产代码的打孔基准面逻辑
        var mi=typeof(AutoHoleWriter).GetMethod("FindOrCreatePlane", BindingFlags.NonPublic|BindingFlags.Static);
        if(mi==null){ L("反射找不到 FindOrCreatePlane"); return 7; }
        P.RefPlane plane=null;
        try{ plane=(P.RefPlane)mi.Invoke(null, new object[]{ target.Part, target }); }
        catch(Exception e){ L("FindOrCreatePlane 抛异常："+(e.InnerException??e).GetType().Name+" "+(e.InnerException??e).Message); return 8; }
        if(plane==null){ L("FindOrCreatePlane 返回 null"); return 9; }
        Array pr=new double[3], pn=new double[3];
        plane.GetRootPoint(ref pr); plane.GetNormal(ref pn);
        double offset=(V3.From(pr)-fpt).Dot(fnv);
        L("打孔基准面：根点="+S(pr)+"  法向="+S(pn));
        L("  → 与所选面的轴向偏差 = "+N(offset*1000.0)+" mm（0 表示正确落在面上；正=沿面法向偏出，负=扎进材料）");

        if(!drill){ L("（--no-drill：只诊断，不打孔）"); return 0; }

        // 找一个"面上且附近没有已有圆孔"的孔心
        var frame=FaceFrameReader.Read(target);
        L("面框：Origin="+SV(frame.Origin)+" Dir="+SV(frame.Direction)+" 长="+N(frame.LengthMm)+"mm 宽="+N(frame.WidthMm)+"mm");
        var outers=new List<double[]>();
        foreach(G.Edge e in (G.Edges)body.get_Edges(G.FeatureTopologyQueryTypeConstants.igQueryAll)){
            var c=e.Geometry as G.Circle; if(c==null) continue;
            Array cc=new double[3], ax=new double[3]; double r=0;
            try{ c.GetCircleData(ref cc, ref ax, out r); }catch{ continue; }
            var pa=target.Placement.Point(V3.From(cc));
            outers.Add(new[]{ pa.X, pa.Y, pa.Z, r });
        }
        V3 centre=new V3(0,0,0); bool found=false;
        foreach(var frac in new[]{ 0.5, 0.35, 0.65, 0.2, 0.8 }){
            var cand=frame.Origin + frame.Direction * (frame.LengthMm*frac*0.001);
            bool clash=false;
            foreach(var c in outers){
                var dv=cand - new V3(c[0],c[1],c[2]);
                var dz=dv.Dot(target.Plane.Normal);
                double rad=Math.Sqrt(Math.Max(0, dv.Dot(dv)-dz*dz));
                if(rad < Math.Max(0.005, c[3]+0.002)) { clash=true; break; }
            }
            if(!clash && FaceFrameReader.ContainsPoint(target, cand, 5.0)){ centre=cand; found=true; L("取孔心 frac="+N(frac)+" -> "+SV(centre)); break; }
        }
        if(!found){ L("找不到没有干涉的孔心"); return 10; }

        var specBlind=new HoleSpec{ Kind=HoleKind.Tapped, HoleDiameter=2.46, ThreadSize="M3", Depth=depthMm, Bottom=HoleBottom.Flat };
        var specThru=new HoleSpec{ Kind=HoleKind.Tapped, HoleDiameter=2.46, ThreadSize="M3", Depth=0, Bottom=HoleBottom.Flat };

        var reqs=new List<AutoHoleWriter.HoleRequest>();
        reqs.Add(new AutoHoleWriter.HoleRequest{ Spec=specBlind, Centre=centre, Source="盲孔Φ2.46深"+N(depthMm) });
        L("=== ① 盲孔（AddFinite，深 "+N(depthMm)+"mm）===");
        var r1=AutoHoleWriter.DrillRequests(target, reqs);
        L("  Created="+r1.Created+" Failures="+r1.Failures.Count+" Notes="+string.Join(" | ", r1.Notes.ToArray()));
        foreach(var f in r1.Failures) L("  ❌ "+f);

        var reqs2=new List<AutoHoleWriter.HoleRequest>();
        reqs2.Add(new AutoHoleWriter.HoleRequest{ Spec=specThru, Centre=centre, Source="贯通孔Φ2.46" });
        L("=== ② 同一孔心的贯通孔（AddThroughAll）===");
        var r2=AutoHoleWriter.DrillRequests(target, reqs2);
        L("  Created="+r2.Created+" Failures="+r2.Failures.Count+" Notes="+string.Join(" | ", r2.Notes.ToArray()));
        foreach(var f in r2.Failures) L("  ❌ "+f);

        L("=== ③ 再打一次盲孔（换个孔心、同样的面）===");
        var centre2=frame.Origin + frame.Direction * (frame.LengthMm*0.25*0.001);
        var reqs3=new List<AutoHoleWriter.HoleRequest>();
        reqs3.Add(new AutoHoleWriter.HoleRequest{ Spec=specBlind, Centre=centre2, Source="盲孔#2" });
        var r3=AutoHoleWriter.DrillRequests(target, reqs3);
        L("  Created="+r3.Created+" Failures="+r3.Failures.Count);
        foreach(var f in r3.Failures) L("  ❌ "+f);

        L("诊断结束（装配副本未保存；需要时可人工另存另看）");
        return 0;
    }
}
