using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using F=SolidEdgeFramework;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;
using S=SolidEdgeFrameworkSupport;

// 锥面根因探针：在真实天工CAD 上创建各种孔，逐项回读 HoleData 属性 + 实体面构成 + 切除体积。
class ConeSpike {
    static object X = Type.Missing;
    const string CadHome = @"C:\Program Files\NDS\TianGong 2025";
    static string Template { get { return Path.Combine(CadHome,"Template","ISO Metric","iso metric part.par"); } }
    static string OutDir;

    [STAThread] static int Main(string[] args){
        OutDir = args.Length>0?Path.GetFullPath(args[0]):Path.Combine(Environment.CurrentDirectory,"artifacts","cone-spike-"+DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(OutDir);
        Console.WriteLine("INFO out "+OutDir);
        F.Application app=null; bool own=false;
        try{
            try{ app=(F.Application)Marshal.GetActiveObject("SolidEdge.Application"); Console.WriteLine("INFO attached"); }
            catch{ app=(F.Application)Activator.CreateInstance(Type.GetTypeFromProgID("SolidEdge.Application")); own=true; Console.WriteLine("INFO new instance"); }
            Console.WriteLine("INFO version "+app.Version);
            app.Visible=true; app.ScreenUpdating=true;

            // 0) 默认值探针：全部参数留空，看 CAD 自己填了什么
            ProbeDefaults(app,"tap",   P.FeaturePropertyConstants.igTappedHole,   "ISO Metric","M6");
            ProbeDefaults(app,"reg",   P.FeaturePropertyConstants.igRegularHole,  null,null);
            ProbeDefaults(app,"csink", P.FeaturePropertyConstants.igCountersinkHole, null,null);
            ProbeDefaults(app,"cbore", P.FeaturePropertyConstants.igCounterboreHole, null,null);

            // 1) 各孔型 × 贯通/有限
            Run(app,"reg-thru-30",      0.030, D_Reg_Default,     false, 0);
            Run(app,"reg-fin10-30",     0.030, D_Reg_Default,     true, 10);
            Run(app,"reg-fin10-flat-30",0.030, D_Reg_FlatBottom,  true, 10);
            Run(app,"tap-thru-30",      0.030, D_Tap_Default,     false, 0);
            Run(app,"tap-thru-10",      0.010, D_Tap_Default,     false, 0);
            Run(app,"tap-fin10-30",     0.030, D_Tap_Default,     true, 10);
            Run(app,"tap-fin10-clean-30",0.030,D_Tap_Clean,       true, 10);
            Run(app,"tap-thru-clean-30", 0.030,D_Tap_Clean,       false, 0);
            Run(app,"csink-thru-30",    0.030, D_Csink_Default,   false, 0);
            Run(app,"cbore-thru-30",    0.030, D_Cbore_Default,   false, 0);

            // 2) 真螺纹（物理螺纹）与装饰螺纹对比
            Run(app,"tap-thru-physical-30", 0.030, D_Tap_Default, false, 0, true);
        } catch(Exception e){ Console.WriteLine("FATAL "+e.GetType().Name+": "+e.Message); Console.WriteLine(e.StackTrace); }
        finally{ try{ if(app!=null&&own) app.Quit(); }catch{} }
        Console.WriteLine("CONE-SPIKE DONE");
        return 0;
    }

    // ---------- 孔数据工厂 ----------
    static P.HoleData D_Reg_Default(P.PartDocument p){
        return p.HoleDataCollection.Add(P.FeaturePropertyConstants.igRegularHole, 0.005,
            X,X,X,X,X,X,X,X,X,X,X,X,X,X,X,X,X,X,X);
    }
    static P.HoleData D_Reg_FlatBottom(P.PartDocument p){
        return p.HoleDataCollection.Add(P.FeaturePropertyConstants.igRegularHole, 0.005,
            X, X, X, X,          // 1-4  CounterboreDia, CounterboreDepth, CountersinkDia, CountersinkAngle
            0.0,                 // 5    BottomAngle = 0 -> 平底，无钻尖锥
            X, X, X, X, X, X, X, X,   // 6-13 Treatment..TaperDimType
            X, X, X, X, X,            // 14-18 CounterboreProfileLocationType..ThreadDescription
            true);               // 19   IgnoreSavedDefaultValues
    }
    static P.HoleData D_Csink_Default(P.PartDocument p){
        return p.HoleDataCollection.Add(P.FeaturePropertyConstants.igCountersinkHole, 0.005,
            X,X, 0.011, 90.0, X,X,X,X,X,X,X,X,X,X,X,X,X,X,X);
    }
    static P.HoleData D_Cbore_Default(P.PartDocument p){
        return p.HoleDataCollection.Add(P.FeaturePropertyConstants.igCounterboreHole, 0.0055,
            0.011, 0.0065, X,X,X,X,X,X,X,X,X,X,X,X,X,X,X);
    }
    static P.HoleData D_Tap_Default(P.PartDocument p){
        return p.HoleDataCollection.AddEx(P.FeaturePropertyConstants.igTappedHole,
            "ISO Metric", X, "M6", X,        // 1-4  Standard, SubType, Size, Fit
            X,                                // 5    HoleDiameter
            X, X, X, X,                       // 6-9  CounterboreDia/Depth, CountersinkDia/Angle
            X,                                // 10   BottomAngle
            X, X, X, X, X, X, X, X,           // 11-18 Treatment..TaperDimType
            X, X, X, X, X,                    // 19-23 CounterboreProfileLocationType..ThreadDescription
            X,                                // 24   IgnoreSavedDefaultValues
            X, X, X,                          // 25-27 ThreadDiameterOption, TapDrill, HeadClearance
            X, X, X, X, X, X, X, X, X);       // 28-36 Start/Neck/End chamfer
    }
    // 全部显式：无 V 形底、无起始倒角、忽略已保存默认值
    static P.HoleData D_Tap_Clean(P.PartDocument p){
        return p.HoleDataCollection.AddEx(P.FeaturePropertyConstants.igTappedHole,
            "ISO Metric", X, "M6", X,        // 1-4  Standard, SubType, Size, Fit
            X,                                // 5    HoleDiameter
            X, X, X, X,                       // 6-9  CounterboreDia/Depth, CountersinkDia/Angle
            0.0,                              // 10   BottomAngle = 0
            X, X, X, X, X, X, X, X,           // 11-18 Treatment..TaperDimType
            X, X, X, X, X,                    // 19-23 CounterboreProfileLocationType..ThreadDescription
            true,                             // 24   IgnoreSavedDefaultValues
            X, X, X,                          // 25-27 ThreadDiameterOption, TapDrill, HeadClearance
            0, X, X,                          // 28-30 StartChamferOn = 0
            0, X, X,                          // 31-33 NeckChamferOn = 0
            0, X, X);                         // 34-36 EndChamferOn = 0
    }
    // ---------- 默认值探针 ----------
    static void ProbeDefaults(F.Application app, string tag, P.FeaturePropertyConstants type, string std, string size){
        P.Model model; P.PartDocument part=null;
        try{
            part=NewPart(app,"PD-"+tag,0.05,0.05,0.01,out model);
            P.HoleData d;
            if(std==null) d=part.HoleDataCollection.Add(type, 0.005, X,X,X,X,X,X,X,X,X,X,X,X,X,X,X,X,X,X,X);
            else d=part.HoleDataCollection.AddEx(type, std, X, size, X,   // 1-4 Standard,SubType,Size,Fit
                 X, X, X, X, X,                                            // 5-9 HoleDia,CB,CS
                 X,                                                        // 10 BottomAngle
                 X, X, X, X, X, X, X, X,                                   // 11-18 Treatment..TaperDimType
                 X, X, X, X, X,                                            // 19-23 CBProfileLoc..ThreadDescription
                 X,                                                        // 24 IgnoreSavedDefaultValues
                 X, X, X,                                                  // 25-27
                 X, X, X, X, X, X, X, X, X);                               // 28-36 chamfers
        }catch(Exception e){ Console.WriteLine("=== DEFAULT PROBE "+tag+" FAILED: "+e.GetType().Name+" "+e.Message); }
        finally{ if(part!=null)try{part.Close(false);}catch{} }
    }

    // ---------- 用例 ----------
    delegate P.HoleData Maker(P.PartDocument part);
    static void Run(F.Application app, string tag, double thickness, Maker make, bool finite, double depthMm){ Run(app,tag,thickness,make,finite,depthMm,false); }
    static void Run(F.Application app, string tag, double thickness, Maker make, bool finite, double depthMm, bool physical){
        P.Model model; P.PartDocument part=null;
        Console.WriteLine("=== CASE "+tag+"  plate="+(thickness*1000)+"mm "+(finite?("finite "+(depthMm)+"mm"):"through")+(physical?" physical-thread":"")+" ===");
        try{
            part=NewPart(app,"C-"+tag,0.1,0.1,thickness,out model);
            double v0=((G.Body)model.Body).Volume;
            P.HoleData data=make(part);
            Console.WriteLine("-- HoleData as built --"); DumpData(data);
            var plane=part.RefPlanes.AddParallelByDistance(FindXY(part), thickness/2.0, P.ReferenceElementConstants.igNormalSide, X,X,X,X);
            var prof=part.ProfileSets.Add().Profiles.Add(plane);
            double x2,y2; prof.Convert3DCoordinate(0.03,0.03,thickness/2.0,out x2,out y2);
            prof.Holes2d.Add(x2,y2);
            int st=prof.End(P.ProfileValidationType.igProfileClosed);
            if(st!=0){ Console.WriteLine("FAIL profile not closed "+st); return; }
            P.Hole hole=null; object desc=null;
            foreach(var side in new[]{P.FeaturePropertyConstants.igLeft,P.FeaturePropertyConstants.igRight}){
                try{
                    if(physical) hole = finite? model.Holes.AddFiniteEx(prof,side,depthMm/1000.0,data,true) : model.Holes.AddThroughAllEx(prof,side,data,true);
                    else hole = finite? model.Holes.AddFinite(prof,side,depthMm/1000.0,data) : model.Holes.AddThroughAll(prof,side,data);
                }catch(Exception e){ Console.WriteLine("  side "+side+" threw "+e.Message); continue; }
                if(hole==null){ Console.WriteLine("  side "+side+" null"); continue; }
                var fs=hole.GetStatusEx(out desc);
                if((int)fs==(int)P.FeatureStatusConstants.igFeatureOK){ Console.WriteLine("  side="+side+" OK"); break; }
                Console.WriteLine("  side="+side+" status="+fs+" desc="+desc);
                try{hole.Delete();}catch{} hole=null;
            }
            if(hole==null){ Console.WriteLine("RESULT "+tag+" : HOLE FAILED"); return; }
            double v1=((G.Body)model.Body).Volume;
            Console.WriteLine("RESULT "+tag+" removed="+Mm3(v1-v0)+" mm^3  (through-cylinder would be "+Mm3(Math.PI*2.5*2.5*thickness*1000.0)+" mm^3 for D5)");
            DumpTopology(tag,(G.Body)model.Body);
            part.SaveAs(Path.Combine(OutDir,tag+".par"));
        }catch(Exception e){ Console.WriteLine("FATAL "+tag+" "+e.GetType().Name+": "+e.Message); Console.WriteLine(e.StackTrace); }
        finally{ if(part!=null)try{part.Close(false);}catch{} }
    }

    // ---------- 回读 ----------
    static void DumpData(P.HoleData d){
        S("HoleType",()=>d.HoleType.ToString());
        S("Standard",()=>d.Standard); S("SubType",()=>d.SubType); S("Size",()=>d.Size); S("Name",()=>d.Name);
        S("TGHoleExtent",()=>d.TGHoleExtent.ToString()); S("TGHoleDepth_mm",()=>Mm(d.TGHoleDepth));
        S("HoleDiameter_mm",()=>Mm(d.HoleDiameter));
        S("BottomAngle_deg",()=>Num(d.BottomAngle));
        S("VBottomDimType",()=>d.VBottomDimType.ToString());
        S("CounterboreDiameter_mm",()=>Mm(d.CounterboreDiameter));
        S("CounterboreDepth_mm",()=>Mm(d.CounterboreDepth));
        S("CounterboreProfileLocationType",()=>d.CounterboreProfileLocationType.ToString());
        S("CountersinkDiameter_mm",()=>Mm(d.CountersinkDiameter));
        S("CountersinkAngle_deg",()=>Num(d.CountersinkAngle));
        S("TreatmentType",()=>d.TreatmentType.ToString());
        S("TaperMethod",()=>d.TaperMethod.ToString()); S("Taper",()=>Num(d.Taper));
        S("TaperDimType",()=>d.TaperDimType.ToString()); S("TaperLValue",()=>Num(d.TaperLValue)); S("TaperRValue",()=>Num(d.TaperRValue));
        S("ThreadDescription",()=>d.ThreadDescription); S("InternalThreadDescription",()=>d.InternalThreadDescription);
        S("ThreadDepth_mm",()=>Mm(d.ThreadDepth)); S("ThreadDepthMethod",()=>d.ThreadDepthMethod.ToString());
        S("ThreadMinorDiameter_mm",()=>Mm(d.ThreadMinorDiameter));
        S("ThreadTapDrillDiameter_mm",()=>Mm(d.ThreadTapDrillDiameter));
        S("ThreadNominalDiameter_mm",()=>Mm(d.ThreadNominalDiameter));
        S("ThreadExternalDiameter_mm",()=>Mm(d.ThreadExternalDiameter));
        S("ThreadHeight_mm",()=>Mm(d.ThreadHeight)); S("ThreadOffset_mm",()=>Mm(d.ThreadOffset));
        S("TGThreadPitch_mm",()=>Mm(d.TGThreadPitch));
        S("InsideEffectiveThreadLength_mm",()=>Mm(d.InsideEffectiveThreadLength));
        S("OutsideEffectiveThreadLength_mm",()=>Mm(d.OutsideEffectiveThreadLength));
        S("ThreadLocation",()=>d.ThreadLocation.ToString()); S("ThreadSetting",()=>d.ThreadSetting.ToString());
        S("HeadClearance_mm",()=>Mm(d.HeadClearance));
        S("Units",()=>d.Units.ToString());
        Chamfer("Start",d,true); Chamfer("Neck",d,false);
    }
    static void Chamfer(string which,P.HoleData d,bool start){
        try{ int on=0; double a=0,b=0; if(start)d.GetStartChamfer(out on,out a,out b); else d.GetNeckChamfer(out on,out a,out b);
             Console.WriteLine("    "+which+"Chamfer on="+on+" a="+Num(a)+" b="+Num(b)); }
        catch(Exception e){ Console.WriteLine("    "+which+"Chamfer ERR "+e.Message); }
    }
    static void S(string k,Func<string> f){ try{ Console.WriteLine("    "+k+" = "+f()); }catch(Exception e){ Console.WriteLine("    "+k+" ERR "+e.Message); } }
    static string Num(double v){ return v.ToString("G8",CultureInfo.InvariantCulture); }
    static string Mm(double v){ return (v*1000.0).ToString("F4",CultureInfo.InvariantCulture); }
    static string Mm3(double v){ return (v*1e9).ToString("F3",CultureInfo.InvariantCulture); }

    static void DumpTopology(string tag,G.Body body){
        var faces=(G.Faces)body.get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll);
        var hist=new Dictionary<string,int>(); int cones=0;
        foreach(G.Face f in faces){
            object geo=null; try{ geo=f.Geometry; }catch{}
            string n = geo==null?"null":geo.GetType().Name;
            if(!hist.ContainsKey(n)) hist[n]=0; hist[n]++;
            var cone=geo as G.Cone;
            if(cone!=null){
                cones++;
                try{
                    Array o=new double[3], ax=new double[3]; double r=0,ang=0; bool exp=false;
                    cone.GetConeData(ref o, ref ax, out r, out ang, out exp);
                    Console.WriteLine("    CONE#"+cones+" origin_z_mm="+Mm(D(o,2))+" r_mm="+Mm(r)+" halfAngle_deg="+Num(ang*180.0/Math.PI)+" expanding="+exp+" axis=("+Num(D(ax,0))+","+Num(D(ax,1))+","+Num(D(ax,2))+")");
                }catch(Exception e){ Console.WriteLine("    CONE#"+cones+" read ERR "+e.Message); }
            }
        }
        var parts=new List<string>();
        foreach(var kv in hist) parts.Add(kv.Key+"="+kv.Value);
        Console.WriteLine("    FACES total="+faces.Count+" { "+string.Join(", ",parts.ToArray())+" }");
    }
    static double D(Array a,int i){ return Convert.ToDouble(a.GetValue(i)); }

    // ---------- 建板 ----------
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
