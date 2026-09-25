using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using F=SolidEdgeFramework;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;
using S=SolidEdgeFrameworkSupport;

// 第三轮：验证修好的建孔配方（全部显式，不依赖 CAD 保存的默认值）。
class HoleFixSpike {
    static object X = Type.Missing;
    const string CadHome = @"C:\Program Files\NDS\TianGong 2025";
    static string Template { get { return Path.Combine(CadHome,"Template","ISO Metric","iso metric part.par"); } }
    static string OutDir;
    const double Tmm = 20.0;
    const double TM = 0.020;

    [STAThread] static int Main(string[] args){
        OutDir = args.Length>0?Path.GetFullPath(args[0]):Path.Combine(Environment.CurrentDirectory,"artifacts","fix-spike-"+DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(OutDir);
        F.Application app=null; bool own=false;
        try{
            try{ app=(F.Application)Marshal.GetActiveObject("SolidEdge.Application"); }
            catch{ app=(F.Application)Activator.CreateInstance(Type.GetTypeFromProgID("SolidEdge.Application")); own=true; }
            Console.WriteLine("INFO version "+app.Version);
            app.Visible=true; app.ScreenUpdating=true;
            Case(app,"t1-tap-thru-clean",false,0,false,t1_tap_thru_clean,"TAP");
            Case(app,"t2-tap-blind-clean",true,10,false,t2_tap_blind_clean,"TAP");
            Case(app,"t4-tap-thru-physical",false,0,true,t4_tap_thru_physical,"TAP");
            Case(app,"r1-reg-thru-zeros",false,0,false,r1_reg_thru_zeros,"REG");
            Case(app,"cb1-cbore-clean",false,0,false,cb1_cbore_clean,"CB");
            Case(app,"cs1-csink-clean",false,0,false,cs1_csink_clean,"CS");
            Case(app,"t5-tap-ex-zeros",false,0,false,t5_tap_ex_zeros,"TAP");
        } catch(Exception e){ Console.WriteLine("FATAL "+e.GetType().Name+": "+e.Message); Console.WriteLine(e.StackTrace); }
        finally{ try{ if(app!=null&&own) app.Quit(); }catch{} }
        Console.WriteLine("FIX-SPIKE DONE");
        return 0;
    }

    static double Cyl(double d,double h){ return Math.PI*(d/2)*(d/2)*h; }

    delegate P.HoleData Maker(P.PartDocument part);
    static void Case(F.Application app,string tag,bool finite,double depthMm,bool physical,Maker make,string kind){
        Console.WriteLine("=== CASE "+tag+" ===");
        P.Model model; P.PartDocument part=null;
        try{
            part=NewPart(app,"F"+tag.Substring(0,2),0.1,0.1,TM,out model);
            double v0=((G.Body)model.Body).Volume;
            P.HoleData data=make(part);
            Console.WriteLine("  IN : "+Brief(data));
            var plane=part.RefPlanes.AddParallelByDistance(FindXY(part), TM/2.0, P.ReferenceElementConstants.igNormalSide, X,X,X,X);
            var prof=part.ProfileSets.Add().Profiles.Add(plane);
            double x2,y2; prof.Convert3DCoordinate(0.03,0.03,TM/2.0,out x2,out y2);
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
                if((int)fs==(int)P.FeatureStatusConstants.igFeatureOK){ Console.WriteLine("  OUT: "+Feature(hole)); Console.WriteLine("  side="+side+" OK"); break; }
                Console.WriteLine("  side="+side+" status="+fs+" desc="+desc);
                try{hole.Delete();}catch{} hole=null;
            }
            if(hole==null){ Console.WriteLine("  VERDICT: HOLE FAILED"); return; }
            double removed=(((G.Body)model.Body).Volume-v0)*-1e9;
            int cones; string info; CountCones((G.Body)model.Body,out cones,out info);
            Console.WriteLine("  GEO removed="+F(removed)+" mm3  cones="+cones+(info.Length>0?("  "+info):""));
            Console.WriteLine("  VERDICT: "+(cones==0?"无锥面":"有锥面"));
            foreach(var f in (G.Faces)((G.Body)model.Body).get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll)){}
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
    static string Brief(P.HoleData d){
        return "type="+Safe(()=>d.HoleType.ToString())+" holeDia="+Safe(()=>F(d.HoleDiameter*1000))+" bottom="+Safe(()=>F(d.BottomAngle))
            +" cbd="+Safe(()=>F(d.CounterboreDiameter*1000))+" cbdep="+Safe(()=>F(d.CounterboreDepth*1000))
            +" csd="+Safe(()=>F(d.CountersinkDiameter*1000))+" csa="+Safe(()=>F(d.CountersinkAngle))
            +" treat="+Safe(()=>d.TreatmentType.ToString())+" thread="+Safe(()=>d.ThreadDescription)+" minor="+Safe(()=>F(d.ThreadMinorDiameter*1000))
            +" std="+Safe(()=>d.Standard)+" size="+Safe(()=>d.Size);
    }
    static string Feature(P.Hole h){
        object hd=null; try{hd=h.HoleData;}catch{}
        var d=hd as P.HoleData;
        return "extent="+Safe(()=>h.ExtentType.ToString())+" physical="+Safe(()=>h.CreatePhysicalThread.ToString())
            +(d==null?" (no HoleData)":"  data: "+Brief(d));
    }
    static string Safe(Func<string> f){ try{ return f(); }catch(Exception e){ return "<err>"; } }
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
    static P.HoleData t1_tap_thru_clean(P.PartDocument p){
        return p.HoleDataCollection.Add(P.FeaturePropertyConstants.igTappedHole, 0.004917,
                    0.0/*1:CounterboreDiameter*/, 0.0/*2:CounterboreDepth*/, 0.0/*3:CountersinkDiameter*/, 0.0/*4:CountersinkAngle*/, 0.0/*5:BottomAngle*/,
                    P.FeaturePropertyConstants.igNone/*6:TreatmentType*/, X/*7:TaperMethod*/, X/*8:Taper*/, 0.004917/*9:ThreadMinorDiameter*/, X/*10:ThreadDepthMethod*/,
                    X/*11:ThreadDepth*/, P.FeaturePropertyConstants.igVBottomDimToFlat/*12:VBottomDimType*/, X/*13:TaperDimType*/, X/*14:CounterboreProfileLocationType*/, X/*15:TaperLValue*/,
                    X/*16:TaperRValue*/, 0.006/*17:ThreadExternalDiameter*/, "M6"/*18:ThreadDescription*/, true/*19:IgnoreSavedDefaultValues*/);
    }
    static P.HoleData t2_tap_blind_clean(P.PartDocument p){
        return p.HoleDataCollection.Add(P.FeaturePropertyConstants.igTappedHole, 0.004917,
                    0.0/*1:CounterboreDiameter*/, 0.0/*2:CounterboreDepth*/, 0.0/*3:CountersinkDiameter*/, 0.0/*4:CountersinkAngle*/, 0.0/*5:BottomAngle*/,
                    P.FeaturePropertyConstants.igNone/*6:TreatmentType*/, X/*7:TaperMethod*/, X/*8:Taper*/, 0.004917/*9:ThreadMinorDiameter*/, X/*10:ThreadDepthMethod*/,
                    X/*11:ThreadDepth*/, P.FeaturePropertyConstants.igVBottomDimToFlat/*12:VBottomDimType*/, X/*13:TaperDimType*/, X/*14:CounterboreProfileLocationType*/, X/*15:TaperLValue*/,
                    X/*16:TaperRValue*/, 0.006/*17:ThreadExternalDiameter*/, "M6"/*18:ThreadDescription*/, true/*19:IgnoreSavedDefaultValues*/);
    }
    static P.HoleData t4_tap_thru_physical(P.PartDocument p){
        return p.HoleDataCollection.Add(P.FeaturePropertyConstants.igTappedHole, 0.004917,
                    0.0/*1:CounterboreDiameter*/, 0.0/*2:CounterboreDepth*/, 0.0/*3:CountersinkDiameter*/, 0.0/*4:CountersinkAngle*/, 0.0/*5:BottomAngle*/,
                    P.FeaturePropertyConstants.igNone/*6:TreatmentType*/, X/*7:TaperMethod*/, X/*8:Taper*/, 0.004917/*9:ThreadMinorDiameter*/, X/*10:ThreadDepthMethod*/,
                    X/*11:ThreadDepth*/, P.FeaturePropertyConstants.igVBottomDimToFlat/*12:VBottomDimType*/, X/*13:TaperDimType*/, X/*14:CounterboreProfileLocationType*/, X/*15:TaperLValue*/,
                    X/*16:TaperRValue*/, 0.006/*17:ThreadExternalDiameter*/, "M6"/*18:ThreadDescription*/, true/*19:IgnoreSavedDefaultValues*/);
    }
    static P.HoleData r1_reg_thru_zeros(P.PartDocument p){
        return p.HoleDataCollection.Add(P.FeaturePropertyConstants.igRegularHole, 0.005,
                    0.0/*1:CounterboreDiameter*/, 0.0/*2:CounterboreDepth*/, 0.0/*3:CountersinkDiameter*/, 0.0/*4:CountersinkAngle*/, 0.0/*5:BottomAngle*/,
                    X/*6:TreatmentType*/, X/*7:TaperMethod*/, X/*8:Taper*/, X/*9:ThreadMinorDiameter*/, X/*10:ThreadDepthMethod*/,
                    X/*11:ThreadDepth*/, X/*12:VBottomDimType*/, X/*13:TaperDimType*/, X/*14:CounterboreProfileLocationType*/, X/*15:TaperLValue*/,
                    X/*16:TaperRValue*/, X/*17:ThreadExternalDiameter*/, X/*18:ThreadDescription*/, true/*19:IgnoreSavedDefaultValues*/);
    }
    static P.HoleData cb1_cbore_clean(P.PartDocument p){
        return p.HoleDataCollection.Add(P.FeaturePropertyConstants.igCounterboreHole, 0.0055,
                    0.011/*1:CounterboreDiameter*/, 0.0065/*2:CounterboreDepth*/, 0.0/*3:CountersinkDiameter*/, 0.0/*4:CountersinkAngle*/, 0.0/*5:BottomAngle*/,
                    X/*6:TreatmentType*/, X/*7:TaperMethod*/, X/*8:Taper*/, X/*9:ThreadMinorDiameter*/, X/*10:ThreadDepthMethod*/,
                    X/*11:ThreadDepth*/, X/*12:VBottomDimType*/, X/*13:TaperDimType*/, X/*14:CounterboreProfileLocationType*/, X/*15:TaperLValue*/,
                    X/*16:TaperRValue*/, X/*17:ThreadExternalDiameter*/, X/*18:ThreadDescription*/, true/*19:IgnoreSavedDefaultValues*/);
    }
    static P.HoleData cs1_csink_clean(P.PartDocument p){
        return p.HoleDataCollection.Add(P.FeaturePropertyConstants.igCountersinkHole, 0.005,
                    0.0/*1:CounterboreDiameter*/, 0.0/*2:CounterboreDepth*/, 0.011/*3:CountersinkDiameter*/, 90.0/*4:CountersinkAngle*/, 0.0/*5:BottomAngle*/,
                    X/*6:TreatmentType*/, X/*7:TaperMethod*/, X/*8:Taper*/, X/*9:ThreadMinorDiameter*/, X/*10:ThreadDepthMethod*/,
                    X/*11:ThreadDepth*/, X/*12:VBottomDimType*/, X/*13:TaperDimType*/, X/*14:CounterboreProfileLocationType*/, X/*15:TaperLValue*/,
                    X/*16:TaperRValue*/, X/*17:ThreadExternalDiameter*/, X/*18:ThreadDescription*/, true/*19:IgnoreSavedDefaultValues*/);
    }
    static P.HoleData t5_tap_ex_zeros(P.PartDocument p){
        return p.HoleDataCollection.AddEx(P.FeaturePropertyConstants.igTappedHole,
                    "ISO Metric"/*1:Standard*/, X/*2:SubType*/, "M6"/*3:Size*/, X/*4:Fit*/, 0.004917/*5:HoleDiameter*/,
                    0.0/*6:CounterboreDiameter*/, 0.0/*7:CounterboreDepth*/, 0.0/*8:CountersinkDiameter*/, 0.0/*9:CountersinkAngle*/, 0.0/*10:BottomAngle*/,
                    P.FeaturePropertyConstants.igNone/*11:TreatmentType*/, X/*12:TaperMethod*/, X/*13:Taper*/, 0.004917/*14:ThreadMinorDiameter*/, X/*15:ThreadDepthMethod*/,
                    X/*16:ThreadDepth*/, P.FeaturePropertyConstants.igVBottomDimToFlat/*17:VBottomDimType*/, X/*18:TaperDimType*/, X/*19:CounterboreProfileLocationType*/, X/*20:TaperLValue*/,
                    X/*21:TaperRValue*/, 0.006/*22:ThreadExternalDiameter*/, "M6"/*23:ThreadDescription*/, true/*24:IgnoreSavedDefaultValues*/, X/*25:ThreadDiameterOption*/,
                    X/*26:ThreadTapDrillDiameter*/, X/*27:HeadClearance*/, X/*28:StartChamferOn*/, X/*29:StartChamferSetback*/, X/*30:StartChamferAngle*/,
                    X/*31:NeckChamferOn*/, X/*32:NeckChamferSetback*/, X/*33:NeckChamferAngle*/, X/*34:EndChamferOn*/, X/*35:EndChamferSetback*/,
                    X/*36:EndChamferAngle*/);
    }
}
