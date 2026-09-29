using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using TianGongCadSuite;
using A=SolidEdgeAssembly;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;
using F=SolidEdgeFramework;

// 型材内嵌板排查探针：只读，不改模型。
// 用法：
//   PanelProbe.exe tree  <asmPath>
//   PanelProbe.exe geom  <asmPath> <filter>
//   PanelProbe.exe read  <asmPath> <filter> [t_mm] [gap_mm]
static class PanelProbe {
    static F.Application app;
    static void L(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    static string F3(Array a){ return Convert.ToDouble(a.GetValue(0)).ToString("F3")+", "+Convert.ToDouble(a.GetValue(1)).ToString("F3")+", "+Convert.ToDouble(a.GetValue(2)).ToString("F3"); }
    static string M16(Array a){
        var v=new double[16]; for(int i=0;i<16;i++) v[i]=Convert.ToDouble(a.GetValue(i));
        return string.Format("t=({0:F2},{1:F2},{2:F2}) X=({3:F4},{4:F4},{5:F4}) Y=({6:F4},{7:F4},{8:F4}) Z=({9:F4},{10:F4},{11:F4})",
            v[12],v[13],v[14], v[0],v[1],v[2], v[4],v[5],v[6], v[8],v[9],v[10]);
    }
    static Array Mat(object o){ Array m=new double[16];
        if(o is A.Occurrence) ((A.Occurrence)o).GetMatrix(ref m); else ((A.SubOccurrence)o).GetMatrix(ref m);
        return m; }
    static string DocName(object o){
        try{ object d = o is A.Occurrence ? (object)((A.Occurrence)o).OccurrenceDocument : (object)((A.SubOccurrence)o).SubOccurrenceDocument;
             if(d==null) return "<null>";
             return System.IO.Path.GetFileName((string)((dynamic)d).FullName)+" ["+(string)((dynamic)d).Type+"]"; }
        catch(Exception e){ return "<err "+e.Message+">"; }
    }
    static void Walk(object o,int depth){
        string pad=new string(' ',depth*2);
        string name=""; bool isAsm=false, ovr=false; int models=-1;
        try{ if(o is A.Occurrence){ var x=(A.Occurrence)o; name=x.Name; isAsm=x.Subassembly; ovr=x.HasBodyOverride; }
             else { var x=(A.SubOccurrence)o; name=x.Name; isAsm=x.Subassembly; ovr=x.HasBodyOverride; } }
        catch(Exception e){ L(pad+"<读取失败 "+e.Message+">"); return; }
        try{ var d = o is A.Occurrence ? (object)((A.Occurrence)o).OccurrenceDocument : (object)((A.SubOccurrence)o).SubOccurrenceDocument;
             var pd=d as P.PartDocument; if(pd!=null) models=pd.Models.Count; }catch{}
        L(pad+"- "+name+"  asm="+isAsm+" bodyOverride="+ovr+" models="+models+" doc="+DocName(o));
        L(pad+"  M "+M16(Mat(o)));
        if(isAsm){
            try{ if(o is A.Occurrence) foreach(A.SubOccurrence c in ((A.Occurrence)o).SubOccurrences) Walk(c,depth+1);
                 else foreach(A.SubOccurrence c in ((A.SubOccurrence)o).SubOccurrences) Walk(c,depth+1); }
            catch(Exception e){ L(pad+"  <子项枚举失败 "+e.Message+">"); }
        }
    }
    static void Flatten(object o, List<object> leaves){
        bool isAsm=false;
        try{ isAsm = o is A.Occurrence ? ((A.Occurrence)o).Subassembly : ((A.SubOccurrence)o).Subassembly; }catch{}
        if(isAsm){
            try{ if(o is A.Occurrence) foreach(A.SubOccurrence c in ((A.Occurrence)o).SubOccurrences) Flatten(c,leaves);
                 else foreach(A.SubOccurrence c in ((A.SubOccurrence)o).SubOccurrences) Flatten(c,leaves); }
            catch(Exception e){ L("   展开失败 "+e.Message); }
        } else leaves.Add(o);
    }
    static string FileOf(object o){
        try{ object d = o is A.Occurrence ? (object)((A.Occurrence)o).OccurrenceDocument : (object)((A.SubOccurrence)o).SubOccurrenceDocument;
             return d==null?"":System.IO.Path.GetFileName((string)((dynamic)d).FullName); }catch{ return ""; }
    }
    static string NameOf(object o){ return o is A.Occurrence?((A.Occurrence)o).Name:((A.SubOccurrence)o).Name; }
    static bool OverrideOf(object o){ try{ return o is A.Occurrence?((A.Occurrence)o).HasBodyOverride:((A.SubOccurrence)o).HasBodyOverride; }catch{return false;} }
    static G.Body RawBody(object o){
        var doc = o is A.Occurrence ? (object)((A.Occurrence)o).OccurrenceDocument : (object)((A.SubOccurrence)o).SubOccurrenceDocument;
        var pd = doc as P.PartDocument; if(pd==null) return null;
        var mdl = pd.Models.Item(1) as P.Model; if(mdl==null) return null;
        return mdl.Body as G.Body;
    }
    static G.Body OverrideBody(object o){
        if(!OverrideOf(o)) return null;
        try{ return (o is A.Occurrence ? (G.Body)((A.Occurrence)o).Body : (G.Body)((A.SubOccurrence)o).Body); }catch{ return null; }
    }
    static G.Body EffectiveBody(object o){ var b=OverrideBody(o); return b!=null?b:RawBody(o); }
    static G.Face FirstPlane(G.Body body){
        if(body==null) return null;
        foreach(G.Face f in (G.Faces)body.Faces[G.FeatureTopologyQueryTypeConstants.igQueryPlane]) if(f.Geometry is G.Plane) return f;
        return null;
    }
    static string Range(G.Body b){ if(b==null) return "<无>"; Array lo=new double[3],hi=new double[3]; b.GetRange(ref lo,ref hi); return "("+F3(lo)+") .. ("+F3(hi)+")"; }
    static object MakeReference(A.AssemblyDocument asm,object o,G.Face face){
        if(o is A.Occurrence) return asm.CreateReference((A.Occurrence)o,face);
        Array key=new byte[0]; ((dynamic)face).GetReferenceKey(ref key); A.TopologyReference tr; ((A.SubOccurrence)o).CreateTopologyReference(ref key,out tr); return tr;
    }
    static List<V3> FacetRange(G.Body body,Transform tf){
        var all=new List<V3>();
        if(body==null) return all;
        Array facets=new double[0]; object normals=null,texture=null,styles=null,faceIds=null; int facetCount;
        body.GetFacetData(.000001,out facetCount,ref facets,out normals,out texture,out styles,out faceIds,false);
        for(int i=0;i+2<facets.Length;i+=3){ var p=new V3(Convert.ToDouble(facets.GetValue(i)),Convert.ToDouble(facets.GetValue(i+1)),Convert.ToDouble(facets.GetValue(i+2))); all.Add(tf==null?p:tf.Point(p)); }
        return all;
    }
    static string Rng(List<V3> pts){ if(pts==null||pts.Count==0) return "<空>";
        return string.Format("X {0:F3}..{1:F3}  Y {2:F3}..{3:F3}  Z {4:F3}..{5:F3}", pts.Min(p=>p.X),pts.Max(p=>p.X),pts.Min(p=>p.Y),pts.Max(p=>p.Y),pts.Min(p=>p.Z),pts.Max(p=>p.Z)); }

    static double RayDist(V3 origin,V3 direction,V3[] triangle){
        V3 e1=triangle[1]-triangle[0],e2=triangle[2]-triangle[0],pp=direction.Cross(e2);double det=e1.Dot(pp);if(Math.Abs(det)<1e-16)return double.PositiveInfinity;
        V3 tt=origin-triangle[0];double uu=tt.Dot(pp)/det;if(uu<-1e-8||uu>1+1e-8)return double.PositiveInfinity;V3 q=tt.Cross(e1);double vv=direction.Dot(q)/det;if(vv<-1e-8||uu+vv>1+1e-8)return double.PositiveInfinity;return e2.Dot(q)/det;
    }
    [STAThread] static int Main(string[] args){
        Console.OutputEncoding=System.Text.Encoding.UTF8;
        if(args.Length<2){ L("用法: PanelProbe.exe tree|geom|read <asmPath> [filter] [t_mm] [gap_mm]"); return 1; }
        try { app=(F.Application)Marshal.GetActiveObject("SolidEdge.Application"); }
        catch(Exception e){ L("FATAL 连不上 CAD："+e.Message); return 2; }
        try{ app.Visible=true; app.ScreenUpdating=true; }catch{}
        for(int i=0;i<40;i++){ try{ int n=app.Documents.Count; try{app.DoIdle();}catch{} break; }catch{ Thread.Sleep(400);} }
        string mode=args[0];
        if(mode=="file"){
            foreach(string f in args.Skip(2)){
                try{
                    object d=null;
                    foreach(object dd in (System.Collections.IEnumerable)app.Documents){ try{ if(string.Equals((string)((dynamic)dd).FullName,System.IO.Path.GetFullPath(f),StringComparison.OrdinalIgnoreCase)){ d=dd; break; } }catch{} }
                    bool opened=false;
                    if(d==null){ d=app.Documents.Open(System.IO.Path.GetFullPath(f)); opened=true; }
                    var pd=d as P.PartDocument;
                    Array lo=new double[3],hi=new double[3];
                    ((G.Body)((P.Model)pd.Models.Item(1)).Body).GetRange(ref lo,ref hi);
                    L(System.IO.Path.GetFileName(f)+"  实体 "+((Convert.ToDouble(hi.GetValue(0))-Convert.ToDouble(lo.GetValue(0)))*1000).ToString("F3")+" x "+((Convert.ToDouble(hi.GetValue(1))-Convert.ToDouble(lo.GetValue(1)))*1000).ToString("F3")+" x "+((Convert.ToDouble(hi.GetValue(2))-Convert.ToDouble(lo.GetValue(2)))*1000).ToString("F3")+" mm  路径 "+((dynamic)d).FullName);
                    if(opened) try{ ((dynamic)d).Close(false); }catch{}
                }catch(Exception e){ L(System.IO.Path.GetFileName(f)+"  读取失败："+e.Message); }
            }
            return 0;
        }
        A.AssemblyDocument asm=null;
        string want=System.IO.Path.GetFullPath(args[1]);
        foreach(object d in (System.Collections.IEnumerable)app.Documents){ try{ if(string.Equals((string)((dynamic)d).FullName,want,StringComparison.OrdinalIgnoreCase)){ asm=d as A.AssemblyDocument; break; } }catch{} }
        if(asm==null){ L("打开装配 "+want); asm=(A.AssemblyDocument)app.Documents.Open(want); }
        for(int i=0;i<20;i++){ try{ app.DoIdle(); }catch{} Thread.Sleep(300); }
        L("装配："+asm.FullName+"  occurrences="+asm.Occurrences.Count);
        if(mode=="tree"){ L("=== 顶层结构 ==="); foreach(A.Occurrence occ in asm.Occurrences) Walk(occ,0); return 0; }
        var leaves=new List<object>();
        foreach(A.Occurrence occ in asm.Occurrences) Flatten(occ,leaves);
        L("叶子实例总数="+leaves.Count+"  含体覆盖的="+leaves.Count(OverrideOf));
        foreach(var g in leaves.GroupBy(o=>FileOf(o)).OrderByDescending(g=>g.Count())) L("  "+g.Count()+" × "+g.Key+"   override="+g.Count(OverrideOf));
        string filter = args.Length>2? args[2] : "40x40";
        var picked = leaves.Where(o=>FileOf(o).IndexOf(filter,StringComparison.OrdinalIgnoreCase)>=0 || NameOf(o).IndexOf(filter,StringComparison.OrdinalIgnoreCase)>=0).ToList();
        L("=== 命中 '"+filter+"' 的实例 "+picked.Count+" 个 ===");
        if(mode=="geom"){
            foreach(var o in picked.Take(4)){
                L("-- "+NameOf(o)+"  file="+FileOf(o)+"  override="+OverrideOf(o));
                L("   occ.GetMatrix        : "+M16(Mat(o)));
                try{ Array inv=new double[16];
                     if(o is A.Occurrence)((A.Occurrence)o).GetBodyInversionMatrix(ref inv); else ((A.SubOccurrence)o).GetBodyInversionMatrix(ref inv);
                     L("   BodyInversionMatrix : "+M16(inv)); }
                catch(Exception e){ L("   BodyInversionMatrix 读不到："+e.Message); }
                try{ L("   原零件体 GetRange     : "+Range(RawBody(o))); L("   原零件体 facet       : "+Rng(FacetRange(RawBody(o),new Transform(Mat(o))))); }catch(Exception e){ L("   原零件体读取失败："+e.Message); }
                try{ L("   覆盖体 GetRange       : "+Range(OverrideBody(o))); L("   覆盖体 facet(原样)    : "+Rng(FacetRange(OverrideBody(o),null)));
                     L("   覆盖体 facet(occ矩阵) : "+Rng(FacetRange(OverrideBody(o),new Transform(Mat(o))))); }catch(Exception e){ L("   覆盖体读取失败："+e.Message); }
                try{ var body=EffectiveBody(o); var face=FirstPlane(body);
                     if(face==null){ L("   （没有平面面片）"); }
                     else{
                        Array p=new double[3],n=new double[3]; ((G.Plane)face.Geometry).GetPlaneData(ref p,ref n);
                        var rp=V3.From(p); var rn=V3.From(n).Unit();
                        L("   面 局部        p="+rp+" n="+rn);
                        var oM=new Transform(Mat(o));
                        L("   面 occ矩阵后   p="+oM.Point(rp)+" n="+oM.Normal(rn));
                        var r=MakeReference(asm,o,face); var pg=PickGeometry.Unwrap(r); var pi=pg.Plane();
                        L("   面 引用矩阵后  p="+pi.Point+" n="+pi.Normal);
                     } }
                catch(Exception e){ L("   面读取失败："+e.GetType().Name+" "+e.Message); }
            }
            return 0;
        }
        if(mode=="insert"){   // 用插件自己的 CadBuilder.Generate 生成并插入一块板，报告 CAD 实际绑定到的文档
            double w=double.Parse(args[2])/1000.0, h=double.Parse(args[3])/1000.0, t3=double.Parse(args[4])/1000.0;
            string outPath=args[5];
            var spec=new PanelSpec{Origin=new V3(-1.0,0.0,0.5),U=new V3(1,0,0),V=new V3(0,0,1),N=new V3(0,-1,0),Width=w,Height=h,Thickness=t3,Gap=0};
            try{
                var occ=CadBuilder.Generate(app,asm,spec,outPath);
                string resolved="<null>"; try{ object dd=occ.OccurrenceDocument; resolved=(string)((dynamic)dd).FullName; }catch(Exception e2){ resolved="<"+e2.Message+">"; }
                L("插入成功  实例="+occ.Name);
                L("  期望文档 "+System.IO.Path.GetFullPath(outPath));
                L("  实际绑定 "+resolved);
                Array lo=new double[3],hi=new double[3];
                ((G.Body)((P.Model)((P.PartDocument)occ.OccurrenceDocument).Models.Item(1)).Body).GetRange(ref lo,ref hi);
                L("  实际实体 尺寸 "+(Convert.ToDouble(hi.GetValue(0))*1000).ToString("F3")+" x "+(Convert.ToDouble(hi.GetValue(1))*1000).ToString("F3"));
            }catch(Exception e){ L("插入失败："+e.GetType().Name+" "+e.Message); }
            return 0;
        }
        if(mode=="select"){
            asm.SelectSet.RemoveAll();
            var list=new List<object>();
            foreach(A.Occurrence occ in asm.Occurrences){
                var f=FileOf(occ);
                if(filter=="*"||f.IndexOf(filter,StringComparison.OrdinalIgnoreCase)>=0||NameOf(occ).IndexOf(filter,StringComparison.OrdinalIgnoreCase)>=0) list.Add(occ);
            }
            foreach(var o in list) asm.SelectSet.Add(o);
            L("已选 "+list.Count+" 个顶层实例（过滤器 '"+filter+"'）");
            foreach(var o in list) L("   "+NameOf(o));
            return 0;
        }
        if(mode=="inspect"){   // 只读当前 SelectSet 内容
            L("SelectSet 共 "+asm.SelectSet.Count+" 项");
            foreach(object o in asm.SelectSet){ try{ L("   "+o.GetType().FullName+"  name="+NameOf(o)+"  file="+FileOf(o)+"  override="+OverrideOf(o)); }catch(Exception e){ L("   <"+e.Message+">"); } }
            return 0;
        }
        if(mode=="slice"){
            var src = picked.Count>0? picked[0] : leaves[0];
            L("零件 "+FileOf(src)+"  实例 "+NameOf(src));
            var rb=RawBody(src);
            var segs=new List<double[]>();
            Array facets=new double[0]; object normals=null,texture=null,styles=null,faceIds=null; int facetCount;
            rb.GetFacetData(.000001,out facetCount,ref facets,out normals,out texture,out styles,out faceIds,false);
            double y0=0.05;   // 零件局部坐标单位是米；取长度中点 y=50mm
            for(int i=0;i+8<facets.Length;i+=9){
                var a=new double[3]; var b=new double[3]; var c=new double[3];
                for(int k=0;k<3;k++){ a[k]=Convert.ToDouble(facets.GetValue(i+k)); b[k]=Convert.ToDouble(facets.GetValue(i+3+k)); c[k]=Convert.ToDouble(facets.GetValue(i+6+k)); }
                var pts=new List<double[]>();
                var tri=new double[][]{a,b,c};
                for(int k=0;k<3;k++){
                    double[] p1=tri[k], p2=tri[(k+1)%3];
                    if((p1[1]-y0)*(p2[1]-y0)<0){ double tt=(y0-p1[1])/(p2[1]-p1[1]); pts.Add(new double[]{p1[0]+tt*(p2[0]-p1[0]), p1[2]+tt*(p2[2]-p1[2])}); }
                }
                if(pts.Count==2) segs.Add(new double[]{pts[0][0],pts[0][1],pts[1][0],pts[1][1]});
            }
            L("横截面（沿零件局部 y=50 剖切）线段数 "+segs.Count);
            foreach(double zmm in new double[]{0,-1,1,-2,2,-3,3,-5,5,-8,8,-11,11,-13,13,-16,16,-19,19}){
                double zq=zmm/1000.0;
                var xs=new List<double>();
                foreach(var s in segs){
                    double z1=s[1],z2=s[3];
                    if((z1-zq)*(z2-zq)<0){ double tt=(zq-z1)/(z2-z1); xs.Add(s[0]+tt*(s[2]-s[0])); }
                }
                xs.Sort();
                var parts=new List<string>();
                for(int k=0;k+1<xs.Count;k+=2) parts.Add(string.Format("[{0:F2},{1:F2}]",xs[k]*1000,xs[k+1]*1000));
                L(string.Format("   z={0,4:F0} mm: 材料 x 区间 "+string.Join("  ",parts.ToArray()), zmm));
            }
            return 0;
        }
        if(mode=="profile"){
            var src = picked.Count>0? picked[0] : leaves[0];
            L("零件 "+FileOf(src)+"  实例 "+NameOf(src));
            var rb=RawBody(src);
            var seen=new Dictionary<string,string[]>();
            foreach(G.Face f in (G.Faces)rb.Faces[G.FeatureTopologyQueryTypeConstants.igQueryPlane]){
                var pl=f.Geometry as G.Plane; if(pl==null) continue;
                Array pp=new double[3],nn=new double[3]; pl.GetPlaneData(ref pp,ref nn);
                var P=V3.From(pp); var N=V3.From(nn).Unit();
                string key=string.Format("{0:F4}|{1:F4}|{2:F4}|{3:F4}",Math.Round(N.X,4),Math.Round(N.Y,4),Math.Round(N.Z,4),Math.Round(N.Dot(P)*1000,3));
                if(!seen.ContainsKey(key)) seen[key]=new string[]{"0",""};
                seen[key][0]=(int.Parse(seen[key][0])+1).ToString();
                seen[key][1]=string.Format("n=({0:F4},{1:F4},{2:F4}) 面法向偏移 {3,9:F3} mm  参考点 ({4:F1},{5:F1},{6:F1})",N.X,N.Y,N.Z,N.Dot(P)*1000,P.X*1000,P.Y*1000,P.Z*1000);
            }
            L("=== 去重后的平面（零件局部坐标，mm）===");
            foreach(var kv in seen) L("  x"+kv.Value[0]+" "+kv.Value[1]);
            return 0;
        }
        if(mode=="panels"){
            foreach(var o in leaves.Where(o=>FileOf(o).StartsWith("填充板")||NameOf(o).StartsWith("填充板"))){
                var rb=RawBody(o); if(rb==null){ L("-- "+NameOf(o)+" 没有实体"); continue; }
                Array lo=new double[3],hi=new double[3]; rb.GetRange(ref lo,ref hi);
                var om=new Transform(Mat(o));
                var c=new List<V3>(); double[] xs={0,1};
                foreach(double a in xs)foreach(double b in xs)foreach(double c2 in xs)
                    c.Add(om.Point(new V3(Convert.ToDouble(lo.GetValue(0))+a*(Convert.ToDouble(hi.GetValue(0))-Convert.ToDouble(lo.GetValue(0))),
                                          Convert.ToDouble(lo.GetValue(1))+b*(Convert.ToDouble(hi.GetValue(1))-Convert.ToDouble(lo.GetValue(1))),
                                          Convert.ToDouble(lo.GetValue(2))+c2*(Convert.ToDouble(hi.GetValue(2))-Convert.ToDouble(lo.GetValue(2))))));
                string full=""; try{ object dd = o is A.Occurrence ? (object)((A.Occurrence)o).OccurrenceDocument : (object)((A.SubOccurrence)o).SubOccurrenceDocument; full=(string)((dynamic)dd).FullName; }catch(Exception ex){ full="<"+ex.Message+">"; }
                L("-- "+NameOf(o)+"  "+FileOf(o));
                L("   文档路径 "+full);
                L(string.Format("   零件尺寸 {0:F3} x {1:F3} x {2:F3} mm", (Convert.ToDouble(hi.GetValue(0))-Convert.ToDouble(lo.GetValue(0)))*1000,(Convert.ToDouble(hi.GetValue(1))-Convert.ToDouble(lo.GetValue(1)))*1000,(Convert.ToDouble(hi.GetValue(2))-Convert.ToDouble(lo.GetValue(2)))*1000));
                L("   零件局部 "+F3(lo)+" .. "+F3(hi));
                L("   装配位置 "+Rng(c));
                L("   矩阵 "+M16(Mat(o)));
            }
            return 0;
        }
        if(mode=="diag"){
            double td = args.Length>3? double.Parse(args[3])/1000.0 : 0.006;
            double gd = args.Length>4? double.Parse(args[4])/1000.0 : 0.0;
            var mlist = FrameReader.Read(asm, picked.Cast<object>().ToList());
            var fl = FrameDetection.Detect(mlist,td,gd);
            L("框口 "+fl.Count);
            int ii=0;
            foreach(var op in fl){
                ii++;
                var s=op.Solve(td,gd);
                L(string.Format("#{0} 宽{1:F3} 高{2:F3} 厚{3:F3} mm", ii, s.Width*1000, s.Height*1000, s.Thickness*1000));
                V3 uu=op.Planes[0].Normal.Unit(), vv=op.Planes[2].Normal.Unit();
                L("  U "+uu+"  V "+vv+"  seed "+op.MidPoint);
                foreach(var dir in new[]{uu,uu*-1.0,vv,vv*-1.0}){
                    var hits=new List<double>();
                    for(int mi=0;mi<mlist.Count;mi++) foreach(var tr in mlist[mi].Triangles){ double dd=RayDist(op.MidPoint,dir,tr); if(dd>1e-8) hits.Add(dd); }
                    hits.Sort();
                    L("   方向 "+dir+" 命中 "+hits.Count+" 个  最近5个(mm): "+(hits.Count==0?"无":string.Join(", ", hits.Take(5).Select(x=>(x*1000).ToString("F3")).ToArray())));
                }
            }
            return 0;
        }
        if(mode=="readsel"){    // 完全照 AutoPanelForm 的做法：用当前 SelectSet
            double t2 = args.Length>2? double.Parse(args[2])/1000.0 : 0.004;
            double g2 = args.Length>3? double.Parse(args[3])/1000.0 : 0.0;
            var sel2=new List<object>(); foreach(object o in asm.SelectSet) sel2.Add(o);
            L("SelectSet 项数 "+sel2.Count);
            List<FrameMember> mm;
            try{ mm = FrameReader.Read(asm, sel2); }
            catch(Exception e){ L("FrameReader.Read 抛异常："+e.GetType().Name+" "+e.Message); return 3; }
            L("读回型材 "+mm.Count+" 根：");
            foreach(var m in mm) L("  * "+m.Name+"  axis="+m.Axis+"  pts="+m.Points.Count+"  faces="+m.Faces.Count+"  tri="+m.Triangles.Count+"  "+Rng(m.Points));
            List<FrameOpening> fo;
            try{ fo = FrameDetection.Detect(mm,t2,g2); }
            catch(Exception e){ L("FrameDetection.Detect 抛异常："+e.GetType().Name+" "+e.Message); return 4; }
            L("识别到框口 "+fo.Count+" 个：");
            int k2=0;
            foreach(var op in fo){ var s=op.Solve(t2,g2); k2++;
                L(string.Format("  #{0}  宽 {1:F3} x 高 {2:F3} x 厚 {3:F3} mm  原点 {4} U {5} V {6}", k2, s.Width*1000, s.Height*1000, s.Thickness*1000, s.Origin, s.U, s.V)); }
            return 0;
        }
        if(mode=="read"){
            double t = args.Length>3? double.Parse(args[3])/1000.0 : 0.006;
            double gap = args.Length>4? double.Parse(args[4])/1000.0 : 0.0;
            List<FrameMember> members;
            try{ members = FrameReader.Read(asm, picked.Cast<object>().ToList()); }
            catch(Exception e){ L("FrameReader.Read 抛异常："+e.GetType().Name+" "+e.Message); return 3; }
            L("读回型材 "+members.Count+" 根：");
            int k=0;
            foreach(var m in members){
                k++;
                L("  * ["+k+"] "+m.Name+"  axis="+m.Axis+"  pts="+m.Points.Count+"  faces="+m.Faces.Count+"  tri="+m.Triangles.Count);
                if(m.Points.Count>0) L("      点 "+Rng(m.Points));
                if(m.Triangles.Count>0){ var flat=m.Triangles.SelectMany(x=>x).ToList(); L("      面片 "+Rng(flat)); }
            }
            List<FrameOpening> found;
            try{ found = FrameDetection.Detect(members,t,gap); }
            catch(Exception e){ L("FrameDetection.Detect 抛异常："+e.GetType().Name+" "+e.Message); return 4; }
            L("识别到框口 "+found.Count+" 个：");
            int i=0;
            foreach(var o in found){
                var s=o.Solve(t,gap);
                L(string.Format("  #{0}  宽 {1:F3} x 高 {2:F3} x 厚 {3:F3} mm", ++i, s.Width*1000, s.Height*1000, s.Thickness*1000));
                L("      原点 "+s.Origin+"  U "+s.U+"  V "+s.V+"  N "+s.N);
                L(string.Format("      四角 Z=±厚/2: {0} {1} {2} {3}", s.Corner(0,s.Thickness/2), s.Corner(1,s.Thickness/2), s.Corner(2,s.Thickness/2), s.Corner(3,s.Thickness/2)));
            }
            return 0;
        }
        return 1;
    }
}
