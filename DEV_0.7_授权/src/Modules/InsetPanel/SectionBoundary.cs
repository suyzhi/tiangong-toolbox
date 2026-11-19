using System;
using System.Linq;
using System.Collections.Generic;
namespace TianGongCadSuite {
    public static class SectionBoundary {
        const double Tolerance=1e-7;
        public static double Distance(IList<FrameMember> members,V3 origin,V3 direction){
            double nearest=double.PositiveInfinity;
            foreach(var member in members){
                // 射线够不着这根型材的包围盒就不用逐个三角形试（原来每个框口 4 个方向 × 全部型材的全部三角形，选整个围栏时上亿次）
                if(member.Triangles.Count>0&&!RayNearBox(BoxOf(member),origin,direction,nearest))continue;
                if(member.Triangles.Count>0){foreach(var t in member.Triangles){double d=Ray(origin,direction,t);if(d>1e-8)nearest=Math.Min(nearest,d);}}
                else foreach(var f in member.Faces){
                    double denominator=f.Plane.Normal.Dot(direction);if(Math.Abs(denominator)<1e-9)continue;
                    double distance=f.Plane.Normal.Dot(f.Plane.Point-origin)/denominator;if(distance<=1e-8||distance>=nearest)continue;
                    V3 hit=origin+direction*distance;
                    V3 x=(Math.Abs(f.Plane.Normal.X)<.8?new V3(1,0,0):new V3(0,1,0)).Cross(f.Plane.Normal).Unit();V3 y=f.Plane.Normal.Cross(x);
                    bool inside=false;foreach(var e in f.Edges)for(int i=0;i+1<e.Length;i++){V3 a=e[i]-hit,b=e[i+1]-hit;double ay=a.Dot(y),by=b.Dot(y);if((ay>0)!=(by>0)&&a.Dot(x)+(b.Dot(x)-a.Dot(x))*(-ay)/(by-ay)>0)inside=!inside;}
                    if(inside)nearest=distance;
                }
            }
            if(double.IsInfinity(nearest))throw new ArgumentException("框口中心剖面有一侧没有实际材料边界，请检查型材配合或中面位置。");
            return nearest;
        }
        static double Ray(V3 origin,V3 direction,V3[] triangle){
            V3 e1=triangle[1]-triangle[0],e2=triangle[2]-triangle[0],p=direction.Cross(e2);double det=e1.Dot(p);if(Math.Abs(det)<1e-16)return double.PositiveInfinity;
            V3 t=origin-triangle[0];double u=t.Dot(p)/det;if(u<-1e-8||u>1+1e-8)return double.PositiveInfinity;V3 q=t.Cross(e1);double v=direction.Dot(q)/det;if(v<-1e-8||u+v>1+1e-8)return double.PositiveInfinity;return e2.Dot(q)/det;
        }
        // Clip each selected-body triangle against the open interior of the panel slab.
        // Touching a boundary is allowed; positive penetration is not.
        public static bool Intersects(IList<FrameMember> members,PanelSpec spec){
            var slab=SlabBox(spec);
            foreach(var triangle in members.Where(m=>m.Triangles.Count>0&&Overlap(BoxOf(m),slab)).SelectMany(m=>m.Triangles)){
                var p=triangle.Select(a=>{var d=a-spec.Origin;return new V3(d.Dot(spec.U),d.Dot(spec.V),d.Dot(spec.N));}).ToList();
                if(p.Max(a=>a.X)<=Tolerance||p.Min(a=>a.X)>=spec.Width-Tolerance||p.Max(a=>a.Y)<=Tolerance||p.Min(a=>a.Y)>=spec.Height-Tolerance||p.Max(a=>a.Z)<=-spec.Thickness/2+Tolerance||p.Min(a=>a.Z)>=spec.Thickness/2-Tolerance)continue;
                for(int axis=0;axis<3&&p.Count>0;axis++){double lo=axis==2?-spec.Thickness/2+Tolerance:Tolerance,hi=axis==0?spec.Width-Tolerance:axis==1?spec.Height-Tolerance:spec.Thickness/2-Tolerance;p=Clip(p,axis,lo,true);p=Clip(p,axis,hi,false);}
                if(p.Count>0)return true;
            }return false;
        }
        // 型材三角形的包围盒（装配坐标，xmin,ymin,zmin,xmax,ymax,zmax），算一次记住
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<FrameMember,double[]> boxes=new System.Runtime.CompilerServices.ConditionalWeakTable<FrameMember,double[]>();
        internal static double[] BoxOf(FrameMember m){
            return boxes.GetValue(m,x=>{
                var b=new[]{double.MaxValue,double.MaxValue,double.MaxValue,double.MinValue,double.MinValue,double.MinValue};
                foreach(var t in x.Triangles)foreach(var a in t){b[0]=Math.Min(b[0],a.X);b[1]=Math.Min(b[1],a.Y);b[2]=Math.Min(b[2],a.Z);b[3]=Math.Max(b[3],a.X);b[4]=Math.Max(b[4],a.Y);b[5]=Math.Max(b[5],a.Z);}
                return b;
            });
        }
        const double BoxPad=1e-6;
        // 射线 origin + direction·t（t > 0，且 t 不超过已经找到的最近距离）碰不碰得到盒子（四周放宽一点）
        internal static bool RayNearBox(double[] b,V3 o,V3 d,double maxT){
            if(b[0]>b[3])return false;
            double t0=0,t1=double.IsInfinity(maxT)?double.MaxValue:maxT+BoxPad;
            double[] os={o.X,o.Y,o.Z},ds={d.X,d.Y,d.Z};
            for(int i=0;i<3;i++){
                double lo=b[i]-BoxPad,hi=b[i+3]+BoxPad;
                if(Math.Abs(ds[i])<1e-15){if(os[i]<lo||os[i]>hi)return false;continue;}
                double a=(lo-os[i])/ds[i],c=(hi-os[i])/ds[i];if(a>c){var tmp=a;a=c;c=tmp;}
                if(a>t0)t0=a;if(c<t1)t1=c;if(t0>t1)return false;
            }
            return true;
        }
        static double[] SlabBox(PanelSpec spec){
            var b=new[]{double.MaxValue,double.MaxValue,double.MaxValue,double.MinValue,double.MinValue,double.MinValue};
            foreach(double z in new[]{-spec.Thickness/2,spec.Thickness/2})for(int i=0;i<4;i++){var a=spec.Corner(i,z);b[0]=Math.Min(b[0],a.X);b[1]=Math.Min(b[1],a.Y);b[2]=Math.Min(b[2],a.Z);b[3]=Math.Max(b[3],a.X);b[4]=Math.Max(b[4],a.Y);b[5]=Math.Max(b[5],a.Z);}
            return b;
        }
        internal static bool Overlap(double[] a,double[] b){
            for(int i=0;i<3;i++)if(a[i]>b[i+3]+BoxPad||b[i]>a[i+3]+BoxPad)return false;
            return true;
        }
        static double Coordinate(V3 p,int axis){return axis==0?p.X:axis==1?p.Y:p.Z;}
        static List<V3> Clip(List<V3> input,int axis,double value,bool greater){
            var output=new List<V3>();if(input.Count==0)return output;
            for(int i=0;i<input.Count;i++){V3 a=input[i],b=input[(i+1)%input.Count];double da=Coordinate(a,axis)-value,db=Coordinate(b,axis)-value;bool ina=greater?da>=0:da<=0,inb=greater?db>=0:db<=0;
                if(ina)output.Add(a);if(ina!=inb)output.Add(a+(b-a)*(da/(da-db)));
            }return output;
        }
    }
}
