using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using F=SolidEdgeFramework;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;
using S=SolidEdgeFrameworkSupport;

// 第二轮探针：为每种孔型找一条"几何正确"的建孔配方。
// 重点回答：螺纹孔怎么建才不带那个 90 度沉头锥面。
class HoleRecipeSpike {
    static object X = Type.Missing;
    const string CadHome = @"C:\Program Files\NDS\TianGong 2025";
    static string Template { get { return Path.Combine(CadHome,"Template","ISO Metric","iso metric part.par"); } }
    static string OutDir;
    const double T = 0.020;   // 板厚 20mm

    [STAThread] static int Main(string[] args){
        OutDir = args.Length>0?Path.GetFullPath(args[0]):Path.Combine(Environment.CurrentDirectory,"artifacts","recipe-spike-"+DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(OutDir);
        F.Application app=null; bool own=false;
        try{
            try{ app=(F.Application)Marshal.GetActiveObject("SolidEdge.Application"); }
            catch{ app=(F.Application)Activator.CreateInstance(Type.GetTypeFromProgID("SolidEdge.Application")); own=true; }
            Console.WriteLine("INFO version "+app.Version);
            app.Visible=true; app.ScreenUpdating=true;

            Case(app,"1-reg-add-ignore",  false,0,false, R_RegIgnore,  Cyl(5.0,T));
            Case(app,"2-tap-ex-default", false,0,false, R_TapExDefault, 0);   // 现行插件做法
            Case(app,"3-tap-ex-ignore",  false,0,false, R_TapExIgnore, 0);
            Case(app,"4-tap-ex-subtype", false,0,false, R_TapExSubtype, 0);
            Case(app,"5-tap-add-explicit",false,0,false, R_TapAddExplicit, Cyl(4.917,T));
            Case(app,"6-tap-add-min",    false,0,false, R_TapAddMin, Cyl(4.917,T));
            Case(app,"7-tap-add-mutate", false,0,false, R_TapAddMutate, Cyl(4.917,T));
            Case(app,"8-cbore-add-ignore",false,0,false, R_CboreIgnore, Cbore(5.5,T,11.0,6.5));
            Case(app,"9-csink-add-ignore",false,0,false, R_CsinkIgnore, Csink(5.0,T,11.0,90));
            Case(app,"10-reg-blind-flat",true,10,false, R_RegFlatBlind, Cyl(5.0,10));
            Case(app,"11-reg-blind-v118",true,10,false, R_RegV118Blind, Cyl(5.0,10)+Cone(5.0,118));
            Case(app,"12-tap-blind-flat",true,10,false, R_TapAddExplicit, Cyl(4.917,10));
            Case(app,"13-tap-add-physical",false,0,true, R_TapAddExplicit, 0);
            Case(app,"14-reg-chamfer",   false,0,false, R_RegChamfer, 0);
        } catch(Exception e){ Console.WriteLine("FATAL "+e.GetType().Name+": "+e.Message); Console.WriteLine(e.StackTrace); }
        finally{ try{ if(app!=null&&own) app.Quit(); }catch{} }
        Console.WriteLine("RECIPE-SPIKE DONE");
        return 0;
    }

    static double Cyl(double d,double h){ return Math.PI*(d/2)*(d/2)*h; }
    static double Cone(double d,double angleDeg){ double r=d/2; double h=r/Math.Tan(angleDeg/2*Math.PI/180.0); return Math.PI*r*r*h/3.0; }
    static double Cbore(double d,double t,double cbd,double cbdep){ return Cyl(d,t)+Math.PI*((cbd/2)*(cbd/2)-(d/2)*(d/2))*cbdep; }
    static double Csink(double d,double t,double csd,double ang){ double r=d/2,R=csd/2; double h=(R-r)/Math.Tan(ang/2*Math.PI/180.0); return Cyl(d,t)+Math.PI*h/3.0*(R*R+R*r+r*r); }

    // ---------- 配方（参数位置由生成器保证） ----------
    static P.HoleData R_RegIgnore(P.PartDocument p){
        return p.HoleDataCollection.Add(P.FeaturePropertyConstants.igRegularHole, 0.005,
                    X/*1:CounterboreDiameter*/, X/*2:CounterboreDepth*/, X/*3:CountersinkDiameter*/, X/*4:CountersinkAngle*/, X/*5:BottomAngle*/,
                    X/*6:TreatmentType*/, X/*7:TaperMethod*/, X/*8:Taper*/, X/*9:ThreadMinorDiameter*/, X/*10:ThreadDepthMethod*/,
                    X/*11:ThreadDepth*/, X/*12:VBottomDimType*/, X/*13:TaperDimType*/, X/*14:CounterboreProfileLocationType*/, X/*15:TaperLValue*/,
                    X/*16:TaperRValue*/, X/*17:ThreadExternalDiameter*/, X/*18:ThreadDescription*/, true/*19:IgnoreSavedDefaultValues*/);
    }
    static P.HoleData R_TapExDefault(P.PartDocument p){
        return p.HoleDataCollection.AddEx(P.FeaturePropertyConstants.igTappedHole,
                    "ISO Metric"/*1:Standard*/, X/*2:SubType*/, "M6"/*3:Size*/, X/*4:Fit*/, X/*5:HoleDiameter*/,
                    X/*6:CounterboreDiameter*/, X/*7:CounterboreDepth*/, X/*8:CountersinkDiameter*/, X/*9:CountersinkAngle*/, X/*10:BottomAngle*/,
                    X/*11:TreatmentType*/, X/*12:TaperMethod*/, X/*13:Taper*/, X/*14:ThreadMinorDiameter*/, X/*15:ThreadDepthMethod*/,
                    X/*16:ThreadDepth*/, X/*17:VBottomDimType*/, X/*18:TaperDimType*/, X/*19:CounterboreProfileLocationType*/, X/*20:TaperLValue*/,
                    X/*21:TaperRValue*/, X/*22:ThreadExternalDiameter*/, X/*23:ThreadDescription*/, X/*24:IgnoreSavedDefaultValues*/, X/*25:ThreadDiameterOption*/,
                    X/*26:ThreadTapDrillDiameter*/, X/*27:HeadClearance*/, X/*28:StartChamferOn*/, X/*29:StartChamferSetback*/, X/*30:StartChamferAngle*/,
                    X/*31:NeckChamferOn*/, X/*32:NeckChamferSetback*/, X/*33:NeckChamferAngle*/, X/*34:EndChamferOn*/, X/*35:EndChamferSetback*/,
                    X/*36:EndChamferAngle*/);
    }
    static P.HoleData R_TapExIgnore(P.PartDocument p){
        return p.HoleDataCollection.AddEx(P.FeaturePropertyConstants.igTappedHole,
                    "ISO Metric"/*1:Standard*/, X/*2:SubType*/, "M6"/*3:Size*/, X/*4:Fit*/, X/*5:HoleDiameter*/,
                    X/*6:CounterboreDiameter*/, X/*7:CounterboreDepth*/, X/*8:CountersinkDiameter*/, X/*9:CountersinkAngle*/, X/*10:BottomAngle*/,
                    X/*11:TreatmentType*/, X/*12:TaperMethod*/, X/*13:Taper*/, X/*14:ThreadMinorDiameter*/, X/*15:ThreadDepthMethod*/,
                    X/*16:ThreadDepth*/, X/*17:VBottomDimType*/, X/*18:TaperDimType*/, X/*19:CounterboreProfileLocationType*/, X/*20:TaperLValue*/,
                    X/*21:TaperRValue*/, X/*22:ThreadExternalDiameter*/, X/*23:ThreadDescription*/, true/*24:IgnoreSavedDefaultValues*/, X/*25:ThreadDiameterOption*/,
                    X/*26:ThreadTapDrillDiameter*/, X/*27:HeadClearance*/, X/*28:StartChamferOn*/, X/*29:StartChamferSetback*/, X/*30:StartChamferAngle*/,
                    X/*31:NeckChamferOn*/, X/*32:NeckChamferSetback*/, X/*33:NeckChamferAngle*/, X/*34:EndChamferOn*/, X/*35:EndChamferSetback*/,
                    X/*36:EndChamferAngle*/);
    }
    static P.HoleData R_TapExSubtype(P.PartDocument p){
        return p.HoleDataCollection.AddEx(P.FeaturePropertyConstants.igTappedHole,
                    "ISO Metric"/*1:Standard*/, "标准螺纹"/*2:SubType*/, "M6"/*3:Size*/, "普通"/*4:Fit*/, X/*5:HoleDiameter*/,
                    X/*6:CounterboreDiameter*/, X/*7:CounterboreDepth*/, X/*8:CountersinkDiameter*/, X/*9:CountersinkAngle*/, 0.0/*10:BottomAngle*/,
                    P.FeaturePropertyConstants.igNone/*11:TreatmentType*/, X/*12:TaperMethod*/, X/*13:Taper*/, X/*14:ThreadMinorDiameter*/, X/*15:ThreadDepthMethod*/,
                    X/*16:ThreadDepth*/, P.FeaturePropertyConstants.igVBottomDimToFlat/*17:VBottomDimType*/, X/*18:TaperDimType*/, X/*19:CounterboreProfileLocationType*/, X/*20:TaperLValue*/,
                    X/*21:TaperRValue*/, X/*22:ThreadExternalDiameter*/, X/*23:ThreadDescription*/, true/*24:IgnoreSavedDefaultValues*/, X/*25:ThreadDiameterOption*/,
                    X/*26:ThreadTapDrillDiameter*/, X/*27:HeadClearance*/, X/*28:StartChamferOn*/, X/*29:StartChamferSetback*/, X/*30:StartChamferAngle*/,
                    X/*31:NeckChamferOn*/, X/*32:NeckChamferSetback*/, X/*33:NeckChamferAngle*/, X/*34:EndChamferOn*/, X/*35:EndChamferSetback*/,
                    X/*36:EndChamferAngle*/);
    }
    static P.HoleData R_TapAddExplicit(P.PartDocument p){
        return p.HoleDataCollection.Add(P.FeaturePropertyConstants.igTappedHole, 0.005,
                    X/*1:CounterboreDiameter*/, X/*2:CounterboreDepth*/, X/*3:CountersinkDiameter*/, X/*4:CountersinkAngle*/, 0.0/*5:BottomAngle*/,
                    P.FeaturePropertyConstants.igNone/*6:TreatmentType*/, X/*7:TaperMethod*/, X/*8:Taper*/, 0.004917/*9:ThreadMinorDiameter*/, X/*10:ThreadDepthMethod*/,
                    X/*11:ThreadDepth*/, P.FeaturePropertyConstants.igVBottomDimToFlat/*12:VBottomDimType*/, X/*13:TaperDimType*/, X/*14:CounterboreProfileLocationType*/, X/*15:TaperLValue*/,
                    X/*16:TaperRValue*/, 0.006/*17:ThreadExternalDiameter*/, "M6"/*18:ThreadDescription*/, true/*19:IgnoreSavedDefaultValues*/);
    }
    static P.HoleData R_TapAddMin(P.PartDocument p){
        return p.HoleDataCollection.Add(P.FeaturePropertyConstants.igTappedHole, 0.005,
                    X/*1:CounterboreDiameter*/, X/*2:CounterboreDepth*/, X/*3:CountersinkDiameter*/, X/*4:CountersinkAngle*/, X/*5:BottomAngle*/,
                    X/*6:TreatmentType*/, X/*7:TaperMethod*/, X/*8:Taper*/, X/*9:ThreadMinorDiameter*/, X/*10:ThreadDepthMethod*/,
                    X/*11:ThreadDepth*/, X/*12:VBottomDimType*/, X/*13:TaperDimType*/, X/*14:CounterboreProfileLocationType*/, X/*15:TaperLValue*/,
                    X/*16:TaperRValue*/, X/*17:ThreadExternalDiameter*/, "M6"/*18:ThreadDescription*/, true/*19:IgnoreSavedDefaultValues*/);
    }
    static P.HoleData R_TapAddMutate(P.PartDocument p){
        P.HoleData d = p.HoleDataCollection.Add(P.FeaturePropertyConstants.igTappedHole, 0.005,
                    X/*1:CounterboreDiameter*/, X/*2:CounterboreDepth*/, X/*3:CountersinkDiameter*/, X/*4:CountersinkAngle*/, X/*5:BottomAngle*/,
                    X/*6:TreatmentType*/, X/*7:TaperMethod*/, X/*8:Taper*/, X/*9:ThreadMinorDiameter*/, X/*10:ThreadDepthMethod*/,
                    X/*11:ThreadDepth*/, X/*12:VBottomDimType*/, X/*13:TaperDimType*/, X/*14:CounterboreProfileLocationType*/, X/*15:TaperLValue*/,
                    X/*16:TaperRValue*/, X/*17:ThreadExternalDiameter*/, X/*18:ThreadDescription*/, true/*19:IgnoreSavedDefaultValues*/);
        try{ d.ThreadDescription="M6"; }catch(Exception e){ Console.WriteLine("    mutate ThreadDescription FAILED "+e.Message); }
        try{ d.ThreadMinorDiameter=0.004917; }catch(Exception e){ Console.WriteLine("    mutate ThreadMinorDiameter FAILED "+e.Message); }
        try{ d.BottomAngle=0.0; }catch(Exception e){ Console.WriteLine("    mutate BottomAngle FAILED "+e.Message); }
        try{ d.CounterboreDiameter=0.0; }catch(Exception e){ Console.WriteLine("    mutate CounterboreDiameter FAILED "+e.Message); }
        try{ d.CountersinkDiameter=0.0; }catch(Exception e){ Console.WriteLine("    mutate CountersinkDiameter FAILED "+e.Message); }
        return d;
    }
    static P.HoleData R_CboreIgnore(P.PartDocument p){
        return p.HoleDataCollection.Add(P.FeaturePropertyConstants.igCounterboreHole, 0.005,
                    0.011/*1:CounterboreDiameter*/, 0.0065/*2:CounterboreDepth*/, X/*3:CountersinkDiameter*/, X/*4:CountersinkAngle*/, 0.0/*5:BottomAngle*/,
                    X/*6:TreatmentType*/, X/*7:TaperMethod*/, X/*8:Taper*/, X/*9:ThreadMinorDiameter*/, X/*10:ThreadDepthMethod*/,
                    X/*11:ThreadDepth*/, X/*12:VBottomDimType*/, X/*13:TaperDimType*/, X/*14:CounterboreProfileLocationType*/, X/*15:TaperLValue*/,
                    X/*16:TaperRValue*/, X/*17:ThreadExternalDiameter*/, X/*18:ThreadDescription*/, true/*19:IgnoreSavedDefaultValues*/);
    }
    static P.HoleData R_CsinkIgnore(P.PartDocument p){
        return p.HoleDataCollection.Add(P.FeaturePropertyConstants.igCountersinkHole, 0.005,
                    X/*1:CounterboreDiameter*/, X/*2:CounterboreDepth*/, 0.011/*3:CountersinkDiameter*/, 90.0/*4:CountersinkAngle*/, X/*5:BottomAngle*/,
                    X/*6:TreatmentType*/, X/*7:TaperMethod*/, X/*8:Taper*/, X/*9:ThreadMinorDiameter*/, X/*10:ThreadDepthMethod*/,
                    X/*11:ThreadDepth*/, X/*12:VBottomDimType*/, X/*13:TaperDimType*/, X/*14:CounterboreProfileLocationType*/, X/*15:TaperLValue*/,
                    X/*16:TaperRValue*/, X/*17:ThreadExternalDiameter*/, X/*18:ThreadDescription*/, true/*19:IgnoreSavedDefaultValues*/);
    }
    static P.HoleData R_RegFlatBlind(P.PartDocument p){
        return p.HoleDataCollection.Add(P.FeaturePropertyConstants.igRegularHole, 0.005,
                    X/*1:CounterboreDiameter*/, X/*2:CounterboreDepth*/, X/*3:CountersinkDiameter*/, X/*4:CountersinkAngle*/, 0.0/*5:BottomAngle*/,
                    X/*6:TreatmentType*/, X/*7:TaperMethod*/, X/*8:Taper*/, X/*9:ThreadMinorDiameter*/, X/*10:ThreadDepthMethod*/,
                    X/*11:ThreadDepth*/, P.FeaturePropertyConstants.igVBottomDimToFlat/*12:VBottomDimType*/, X/*13:TaperDimType*/, X/*14:CounterboreProfileLocationType*/, X/*15:TaperLValue*/,
                    X/*16:TaperRValue*/, X/*17:ThreadExternalDiameter*/, X/*18:ThreadDescription*/, true/*19:IgnoreSavedDefaultValues*/);
    }
    static P.HoleData R_RegV118Blind(P.PartDocument p){
        return p.HoleDataCollection.Add(P.FeaturePropertyConstants.igRegularHole, 0.005,
                    X/*1:CounterboreDiameter*/, X/*2:CounterboreDepth*/, X/*3:CountersinkDiameter*/, X/*4:CountersinkAngle*/, 118.0/*5:BottomAngle*/,
                    X/*6:TreatmentType*/, X/*7:TaperMethod*/, X/*8:Taper*/, X/*9:ThreadMinorDiameter*/, X/*10:ThreadDepthMethod*/,
                    X/*11:ThreadDepth*/, P.FeaturePropertyConstants.igVBottomDimToV/*12:VBottomDimType*/, X/*13:TaperDimType*/, X/*14:CounterboreProfileLocationType*/, X/*15:TaperLValue*/,
                    X/*16:TaperRValue*/, X/*17:ThreadExternalDiameter*/, X/*18:ThreadDescription*/, true/*19:IgnoreSavedDefaultValues*/);
    }
    static P.HoleData R_RegChamfer(P.PartDocument p){
        P.HoleData d = p.HoleDataCollection.Add(P.FeaturePropertyConstants.igRegularHole, 0.005,
                    X/*1:CounterboreDiameter*/, X/*2:CounterboreDepth*/, X/*3:CountersinkDiameter*/, X/*4:CountersinkAngle*/, X/*5:BottomAngle*/,
                    X/*6:TreatmentType*/, X/*7:TaperMethod*/, X/*8:Taper*/, X/*9:ThreadMinorDiameter*/, X/*10:ThreadDepthMethod*/,
                    X/*11:ThreadDepth*/, X/*12:VBottomDimType*/, X/*13:TaperDimType*/, X/*14:CounterboreProfileLocationType*/, X/*15:TaperLValue*/,
                    X/*16:TaperRValue*/, X/*17:ThreadExternalDiameter*/, X/*18:ThreadDescription*/, true/*19:IgnoreSavedDefaultValues*/);
        try{ d.SetStartChamfer(1,0.0005,45.0); }catch(Exception e){ Console.WriteLine("    SetStartChamfer FAILED "+e.Message); }
        return d;
    }

    // ---------- 用例执行 ----------
    delegate P.HoleData Maker(P.PartDocument part);
    static void Case(F.Application app, string tag, bool finite, double depthMm, bool physical, Maker make, double expectedMm3){
        Console.WriteLine("=== CASE "+tag+" ===");
        P.Model model; P.PartDocument part=null;
        try{
            part=NewPart(app,"R"+tag.Substring(0,2),0.1,0.1,T,out model);
            double v0=((G.Body)model.Body).Volume;
            P.HoleData data=make(part);
            Console.Write("  IN : "); Brief(data);
            var plane=part.RefPlanes.AddParallelByDistance(FindXY(part), T/2.0, P.ReferenceElementConstants.igNormalSide, X,X,X,X);
            var prof=part.ProfileSets.Add().Profiles.Add(plane);
            double x2,y2; prof.Convert3DCoordinate(0.03,0.03,T/2.0,out x2,out y2);
            prof.Holes2d.Add(x2,y2);
            if(prof.End(P.ProfileValidationType.igProfileClosed)!=0){ Console.WriteLine("  FAIL profile not closed"); return; }
            P.Hole hole=null; object desc=null;
            foreach(var side in new[]{P.FeaturePropertyConstants.igLeft,P.FeaturePropertyConstants.igRight}){
                try{
                    if(physical) hole=model.Holes.AddThroughAllEx(prof,side,data,true);
                    else hole=finite? model.Holes.AddFinite(prof,side,depthMm/1000.0,data) : model.Holes.AddThroughAll(prof,side,data);
                }catch(Exception e){ Console.WriteLine("  side "+side+" threw "+e.Message); continue; }
                if(hole==null){ Console.WriteLine("  side "+side+" null"); continue; }
                var fs=hole.GetStatusEx(out desc);
                if((int)fs==(int)P.FeatureStatusConstants.igFeatureOK){ Console.Write("  OUT: "); Feature(hole); Console.WriteLine("  side="+side+" OK"); break; }
                Console.WriteLine("  side="+side+" status="+fs+" desc="+desc);
                try{hole.Delete();}catch{} hole=null;
            }
            if(hole==null){ Console.WriteLine("  VERDICT: HOLE FAILED"); return; }
            double removed=(((G.Body)model.Body).Volume-v0)*-1e9;   // mm^3 正值
            int cones; string coneInfo;
            CountCones((G.Body)model.Body, out cones, out coneInfo);
            double ratio = expectedMm3>0? removed/expectedMm3 : 0;
            Console.WriteLine("  GEO removed="+F(removed)+" mm3  expected="+(expectedMm3>0?F(expectedMm3):"n/a")+(expectedMm3>0?("  ratio="+F(ratio)):"")+"  cones="+cones);
            if(coneInfo.Length>0) Console.WriteLine("      "+coneInfo);
            Console.WriteLine("  VERDICT: "+((expectedMm3<=0)?"INFO":(Math.Abs(ratio-1)<0.01?"OK":"MISMATCH"))+(cones>0?"  (有锥面)":"  (无锥面)"));
            part.SaveAs(Path.Combine(OutDir,tag+".par"));
        }catch(Exception e){ Console.WriteLine("  FATAL "+e.GetType().Name+": "+e.Message); }
        finally{ if(part!=null)try{part.Close(false);}catch{} }
    }

    static void CountCones(G.Body body,out int cones,out string info){
        cones=0; info="";
        var faces=(G.Faces)body.get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll);
        var list=new List<string>();
        foreach(G.Face f in faces){
            object geo=null; try{geo=f.Geometry;}catch{}
            var cone=geo as G.Cone; if(cone==null) continue;
            cones++;
            try{ Array o=new double[3],ax=new double[3]; double r=0,ang=0; bool ex=false;
                 cone.GetConeData(ref o,ref ax,out r,out ang,out ex);
                 list.Add("CONE origin_z="+F(D(o,2)*1000)+"mm r="+F(r*1000)+"mm angle="+F(ang*180/Math.PI)+"deg expanding="+ex);
            }catch{}
        }
        info=string.Join(" | ",list.ToArray());
    }

    static void Brief(P.HoleData d){
        Console.WriteLine("type="+Safe(()=>d.HoleType.ToString())+" holeDia="+Safe(()=>F(d.HoleDiameter*1000))+" bottom="+Safe(()=>F(d.BottomAngle))
            +" cbd="+Safe(()=>F(d.CounterboreDiameter*1000))+" cbdep="+Safe(()=>F(d.CounterboreDepth*1000))
            +" csd="+Safe(()=>F(d.CountersinkDiameter*1000))+" csa="+Safe(()=>F(d.CountersinkAngle))
            +" treat="+Safe(()=>d.TreatmentType.ToString())+" thread="+Safe(()=>d.ThreadDescription)+" minor="+Safe(()=>F(d.ThreadMinorDiameter*1000))
            +" std="+Safe(()=>d.Standard)+" size="+Safe(()=>d.Size)+" sub="+Safe(()=>d.SubType));
    }
    static void Feature(P.Hole h){
        object hd=null; try{hd=h.HoleData;}catch{}
        var d=hd as P.HoleData;
        Console.WriteLine("extent="+Safe(()=>h.ExtentType.ToString())+" depth="+Safe(()=>F(h.Depth*1000))+" physical="+Safe(()=>h.CreatePhysicalThread.ToString())
            +(d==null?" (no HoleData)":"  data: "+Safe(()=>d.HoleType.ToString())+" holeDia="+Safe(()=>F(d.HoleDiameter*1000))+" thread="+Safe(()=>d.ThreadDescription)+" bottom="+Safe(()=>F(d.BottomAngle))+" cbd="+Safe(()=>F(d.CounterboreDiameter*1000))+" csd="+Safe(()=>F(d.CountersinkDiameter*1000))));
    }
    static string Safe(Func<string> f){ try{ return f(); }catch(Exception e){ return "<err:"+e.Message+">"; } }
    static string F(double v){ return v.ToString("0.###",CultureInfo.InvariantCulture); }
    static double D(Array a,int i){ return Convert.ToDouble(a.GetValue(i)); }

    static P.RefPlane FindXY(P.PartDocument part){
        foreach(P.RefPlane c in part.RefPlanes){ Array n=new double[3],p=new double[3],u=new double[3];
            c.GetNormal(ref n); c.GetRootPoint(ref p); c.GetReferenceDirection(ref u);
            if(Math.Abs(D(n,2)-1)<1e-8&&Math.Abs(D(p,0))<1e-8&&Math.Abs(D(p,1))<1e-8&&Math.Abs(D(u,0)-1)<1e-8) return c; }
        return null;
    }
    static P.PartDocument NewPart(F.Application app,string n,double w,double h,double t,out P.Model model){
        P.PartDocument part=(P.PartDocument)app.Documents.Add("SolidEdge.PartDocument",Template);
        part.ModelingMode=P.ModelingModeConstants.seModelingModeOrdered;
        var profile=part.ProfileSets.Add().Profiles.Add(FindXY(part));
        var L=new S.Line2d[4];
        L[0]=profile.Lines2d.AddBy2Points(0,0,w,0); L[1]=profile.Lines2d.AddBy2Points(w,0,w,h);
        L[2]=profile.Lines2d.AddBy2Points(w,h,0,h); L[3]=profile.Lines2d.AddBy2Points(0,h,0,0);
        var rel=(S.Relations2d)profile.Relations2d;
        for(int i=0;i<4;i++){ rel.AddKeypoint(L[i],(int)SolidEdgeConstants.KeypointIndexConstants.igLineEnd,L[(i+1)%4],(int)SolidEdgeConstants.KeypointIndexConstants.igLineStart); if(i%2==0) rel.AddHorizontal(L[i]); else rel.AddVertical(L[i]); }
        rel.AddKeypointFix(L[0],(int)SolidEdgeConstants.KeypointIndexConstants.igLineStart);
        var dims=(S.Dimensions)profile.Dimensions; dims.Constraint=true; dims.AddLength(L[0]); dims.AddLength(L[1]);
        if(profile.End(P.ProfileValidationType.igProfileClosed)!=0) throw new InvalidOperationException("base not closed");
        Array arr=new object[]{profile};
        model=part.Models.AddFiniteExtrudedProtrusion(1,ref arr,P.FeaturePropertyConstants.igSymmetric,t);
        return part;
    }
}