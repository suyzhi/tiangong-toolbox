using System;
using System.IO;
using System.Runtime.InteropServices;
using F=SolidEdgeFramework;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;
using S=SolidEdgeFrameworkSupport;

// Spike 10: 腰孔用 AddByStartAlongEnd（显式给出弧上一点），彻底消除绕行方向的歧义。
class HoleSpike10 {
    static int pass, fail;
    static void Step(string s){ Console.WriteLine("STEP "+s); Console.Out.Flush(); }
    static void Ok(string s){ pass++; Console.WriteLine("PASS "+s); Console.Out.Flush(); }
    static void Bad(string s){ fail++; Console.WriteLine("FAIL "+s); Console.Out.Flush(); }
    static void Info(string s){ Console.WriteLine("INFO "+s); Console.Out.Flush(); }
    static object M = Type.Missing;
    const string CadHome = @"C:\Program Files\NDS\TianGong 2025";
    static string Template { get { return Path.Combine(CadHome,"Template","ISO Metric","iso metric part.par"); } }
    [STAThread] static int Main(string[] args){
        string outDir = args.Length>0?Path.GetFullPath(args[0]):Path.Combine(Environment.CurrentDirectory,"artifacts","hole-spike10-"+DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(outDir); Info("output "+outDir);
        F.Application app=null; bool weCreated=false;
        try{
            try{ app=(F.Application)Marshal.GetActiveObject("SolidEdge.Application"); Info("attached"); }
            catch{ app=(F.Application)Activator.CreateInstance(Type.GetTypeFromProgID("SolidEdge.Application")); weCreated=true; }
            Info("version "+app.Version); app.Visible=true; app.ScreenUpdating=true;
            Run(app,outDir,"A",false); Run(app,outDir,"B",true);
        } catch(Exception e){ Bad("fatal: "+e.GetType().Name+": "+e.Message); Console.WriteLine(e.StackTrace); }
        finally{ try{ if(app!=null&&weCreated) app.Quit(); }catch{} }
        Console.WriteLine("SPIKE10 RESULT pass="+pass+" fail="+fail);
        Console.WriteLine("ARTIFACTS "+outDir);
        return fail==0?0:1;
    }
    static bool IsOK(P.FeatureStatusConstants s){ return (int)s==(int)P.FeatureStatusConstants.igFeatureOK; }
    static double D(Array a,int i){ return Convert.ToDouble(a.GetValue(i)); }
    static double Vol(P.Model m){ return ((G.Body)m.Body).Volume; }
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
    static void Run(F.Application app,string outDir,string tag,bool cwLoop){
        Step("SLOT "+tag+" cwLoop="+cwLoop+" AddByStartAlongEnd");
        P.PartDocument part=null;
        try{
            P.Model model; part=NewPart(app,"L"+tag,0.12,0.08,0.01,out model);
            double v0=Vol(model);
            var rp=part.RefPlanes.AddParallelByDistance(FindXY(part),0.005,P.ReferenceElementConstants.igNormalSide,M,M,M,M);
            var prof=part.ProfileSets.Add().Profiles.Add(rp);
            double x1,y1,x2,y2,r=0.004;
            prof.Convert3DCoordinate(0.03,0.04,0.005,out x1,out y1);
            prof.Convert3DCoordinate(0.07,0.04,0.005,out x2,out y2);
            double cy=y1;
            S.Line2d l1,l2; S.Arc2d a1,a2;
            if(!cwLoop){
                l1=prof.Lines2d.AddBy2Points(x1, cy+r, x2, cy+r);
                a1=prof.Arcs2d.AddByStartAlongEnd(x2, cy+r, x2+r, cy, x2, cy-r);
                l2=prof.Lines2d.AddBy2Points(x2, cy-r, x1, cy-r);
                a2=prof.Arcs2d.AddByStartAlongEnd(x1, cy-r, x1-r, cy, x1, cy+r);
            } else {
                l1=prof.Lines2d.AddBy2Points(x2, cy+r, x1, cy+r);
                a1=prof.Arcs2d.AddByStartAlongEnd(x1, cy+r, x1-r, cy, x1, cy-r);
                l2=prof.Lines2d.AddBy2Points(x1, cy-r, x2, cy-r);
                a2=prof.Arcs2d.AddByStartAlongEnd(x2, cy-r, x2+r, cy, x2, cy+r);
            }
            var rel=(S.Relations2d)prof.Relations2d;
            int ks=(int)SolidEdgeConstants.KeypointIndexConstants.igLineStart, ke=(int)SolidEdgeConstants.KeypointIndexConstants.igLineEnd;
            int as_=(int)SolidEdgeConstants.KeypointIndexConstants.igArcStart, ae=(int)SolidEdgeConstants.KeypointIndexConstants.igArcEnd;
            rel.AddKeypoint(l1,ke,a1,as_); rel.AddKeypoint(a1,ae,l2,ks); rel.AddKeypoint(l2,ke,a2,as_); rel.AddKeypoint(a2,ae,l1,ks);
            int st=prof.End(P.ProfileValidationType.igProfileClosed);
            Info(tag+" Profile.End="+st);
            if(st!=0){ Bad(tag+" not closed"); return; }
            Ok(tag+" 腰孔轮廓闭合");
            object dsc=null;
            foreach(var side in new[]{P.FeaturePropertyConstants.igLeft,P.FeaturePropertyConstants.igRight}){
                P.ExtrudedCutout cut=null;
                try{ cut=model.ExtrudedCutouts.AddThroughAll(prof, P.FeaturePropertyConstants.igRight, side); }
                catch(Exception e){ Info("  side "+side+" threw "+e.Message.Substring(0,Math.Min(80,e.Message.Length))); continue; }
                if(cut==null){ Info("  side "+side+" null"); continue; }
                var fs=cut.GetStatusEx(out dsc);
                if(IsOK(fs)){
                    double got=v0-Vol(model), len=Math.Abs(x2-x1);
                    double expect=(2*r*len+Math.PI*r*r)*0.01;
                    Info("  side="+side+" removed="+got+" expected="+expect+" ratio="+(got/expect));
                    if(Math.Abs(got-expect)/expect<0.03){ Ok(tag+" 腰孔通切成功，体积吻合"); part.SaveAs(Path.Combine(outDir,"SLOT-"+tag+".par")); }
                    else Bad(tag+" 体积比 "+(got/expect));
                    return;
                }
                try{ cut.Delete(); }catch{}
            }
            Bad(tag+" 两方向都失败");
        } catch(Exception e){ Bad(tag+" exception: "+e.GetType().Name+": "+e.Message); }
        finally{ if(part!=null) try{part.Close(false);}catch{} }
    }
}
