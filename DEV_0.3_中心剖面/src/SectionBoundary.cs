using System;
using System.Linq;
using System.Collections.Generic;
namespace TianGongPanelAuto {
    public static class SectionBoundary {
        const double Tolerance=1e-7;
        public static double Distance(IList<FrameMember> members,V3 origin,V3 direction){
            double nearest=double.PositiveInfinity;
            foreach(var member in members){
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
            foreach(var triangle in members.SelectMany(m=>m.Triangles)){
                var p=triangle.Select(a=>{var d=a-spec.Origin;return new V3(d.Dot(spec.U),d.Dot(spec.V),d.Dot(spec.N));}).ToList();
                if(p.Max(a=>a.X)<=Tolerance||p.Min(a=>a.X)>=spec.Width-Tolerance||p.Max(a=>a.Y)<=Tolerance||p.Min(a=>a.Y)>=spec.Height-Tolerance||p.Max(a=>a.Z)<=-spec.Thickness/2+Tolerance||p.Min(a=>a.Z)>=spec.Thickness/2-Tolerance)continue;
                for(int axis=0;axis<3&&p.Count>0;axis++){double lo=axis==2?-spec.Thickness/2+Tolerance:Tolerance,hi=axis==0?spec.Width-Tolerance:axis==1?spec.Height-Tolerance:spec.Thickness/2-Tolerance;p=Clip(p,axis,lo,true);p=Clip(p,axis,hi,false);}
                if(p.Count>0)return true;
            }return false;
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
