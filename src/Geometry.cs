using System;
using System.Linq;
namespace TianGongPanel {
    public struct V3 {
        public readonly double X,Y,Z;
        public V3(double x,double y,double z){X=x;Y=y;Z=z;}
        public static V3 operator +(V3 a,V3 b){return new V3(a.X+b.X,a.Y+b.Y,a.Z+b.Z);}
        public static V3 operator -(V3 a,V3 b){return new V3(a.X-b.X,a.Y-b.Y,a.Z-b.Z);}
        public static V3 operator *(V3 a,double b){return new V3(a.X*b,a.Y*b,a.Z*b);}
        public double Dot(V3 b){return X*b.X+Y*b.Y+Z*b.Z;}
        public V3 Cross(V3 b){return new V3(Y*b.Z-Z*b.Y,Z*b.X-X*b.Z,X*b.Y-Y*b.X);}
        public double Length {get{return Math.Sqrt(Dot(this));}}
        public bool Finite {get{return !(double.IsNaN(X)||double.IsNaN(Y)||double.IsNaN(Z)||double.IsInfinity(X)||double.IsInfinity(Y)||double.IsInfinity(Z));}}
        public V3 Unit(){if(!Finite||Length<1e-12)throw new ArgumentException("无效的方向向量。");return this*(1/Length);}
        public V3 Canonical(){var v=Unit();var a=new[]{Math.Abs(v.X),Math.Abs(v.Y),Math.Abs(v.Z)};int i=System.Array.IndexOf(a,a.Max());return (i==0?v.X:i==1?v.Y:v.Z)<0?v*(-1):v;}
        public double[] Array(){return new[]{X,Y,Z};}
        public static V3 From(System.Array a){return new V3(Convert.ToDouble(a.GetValue(0)),Convert.ToDouble(a.GetValue(1)),Convert.ToDouble(a.GetValue(2)));}
        public override string ToString(){return string.Format(System.Globalization.CultureInfo.InvariantCulture,"({0:F6}, {1:F6}, {2:F6})",X,Y,Z);}
    }
    public sealed class Transform {
        public readonly double[] M;
        public Transform(System.Array a){M=a.Cast<object>().Select(Convert.ToDouble).ToArray();if(M.Length!=16||M.Any(x=>double.IsNaN(x)||double.IsInfinity(x)))throw new ArgumentException("无效的装配变换矩阵。");}
        public static Transform Identity {get{return Frame(new V3(0,0,0),new V3(1,0,0),new V3(0,1,0),new V3(0,0,1));}}
        public static Transform Frame(V3 o,V3 x,V3 y,V3 z){return new Transform(new[]{x.X,x.Y,x.Z,0,y.X,y.Y,y.Z,0,z.X,z.Y,z.Z,0,o.X,o.Y,o.Z,1});}
        public V3 Vector(V3 p){return new V3(M[0]*p.X+M[4]*p.Y+M[8]*p.Z,M[1]*p.X+M[5]*p.Y+M[9]*p.Z,M[2]*p.X+M[6]*p.Y+M[10]*p.Z);}
        public V3 Point(V3 p){return Vector(p)+new V3(M[12],M[13],M[14]);}
        public V3 Normal(V3 n){var x=new V3(M[0],M[1],M[2]);var y=new V3(M[4],M[5],M[6]);var z=new V3(M[8],M[9],M[10]);double d=x.Dot(y.Cross(z));if(Math.Abs(d)<1e-12)throw new ArgumentException("装配变换不可逆。");return (y.Cross(z)*n.X+z.Cross(x)*n.Y+x.Cross(y)*n.Z)*(1/d);}
    }
    public sealed class PlaneInput {
        public V3 Point,Normal;
        public string Label;
        public PlaneInput(V3 p,V3 n,string label){Point=p;Normal=n.Unit();Label=label;if(!p.Finite)throw new ArgumentException("平面坐标无效。");}
    }
    public sealed class PanelSpec {
        public V3 Origin,U,V,N;
        public double Width,Height,Thickness,Gap;
        public Transform Placement {get{return Transform.Frame(Origin,U,V,N);}}
        public V3 Corner(int i,double z){return Origin+U*((i==1||i==2)?Width:0)+V*(i>=2?Height:0)+N*z;}
    }
    public static class PanelGeometry {
        // Strict numerical tolerances; do not silently square a physically skew frame.
        public const double AngularTolerance=1e-7, DistanceTolerance=1e-7;
        public static PanelSpec Solve(PlaneInput[] faces,V3 point,double thickness,double gap){
            if(faces==null||faces.Length!=4)throw new ArgumentException("请恰好选择四个平面。");
            if(!point.Finite)throw new ArgumentException("定位点无效。");
            if(double.IsNaN(thickness)||double.IsInfinity(thickness)||thickness<=DistanceTolerance)throw new ArgumentException("总厚度必须大于 0.0001 mm。");
            if(double.IsNaN(gap)||double.IsInfinity(gap)||gap<0)throw new ArgumentException("统一间隙必须为非负数。");
            var n=faces.Select(f=>f.Normal.Canonical()).ToArray();
            int partner=-1;
            for(int i=1;i<4;i++)if(n[0].Cross(n[i]).Length<=AngularTolerance){if(partner!=-1)throw new ArgumentException("四面无法分成两组平行面。");partner=i;}
            if(partner<0)throw new ArgumentException("所选面没有组成两组平行面。");
            var other=Enumerable.Range(1,3).Where(i=>i!=partner).ToArray();
            if(n[other[0]].Cross(n[other[1]]).Length>AngularTolerance||Math.Abs(n[0].Dot(n[other[0]]))>AngularTolerance)throw new ArgumentException("相对面必须平行，相邻面必须垂直。");
            V3 u=n[0],v=n[other[0]]; int a=0,b=partner,c=other[0],d=other[1];
            // Stable orientation independent of picking order and flipped face normals.
            if(Compare(u,v)<0){var t=u;u=v;v=t;int ai=a,bi=b;a=c;b=d;c=ai;d=bi;}
            v=(v-u*u.Dot(v)).Unit(); V3 normal=u.Cross(v).Unit();
            double loU=Math.Min(u.Dot(faces[a].Point),u.Dot(faces[b].Point));
            double hiU=Math.Max(u.Dot(faces[a].Point),u.Dot(faces[b].Point));
            double loV=Math.Min(v.Dot(faces[c].Point),v.Dot(faces[d].Point));
            double hiV=Math.Max(v.Dot(faces[c].Point),v.Dot(faces[d].Point));
            if(hiU-loU<=DistanceTolerance||hiV-loV<=DistanceTolerance)throw new ArgumentException("重复面或重合平面不能围成板子。");
            double w=hiU-loU-2*gap,h=hiV-loV-2*gap;
            if(w<=DistanceTolerance||h<=DistanceTolerance)throw new ArgumentException("间隙过大，板子的长宽必须为正数。");
            return new PanelSpec{Origin=u*(loU+gap)+v*(loV+gap)+normal*normal.Dot(point),U=u,V=v,N=normal,Width=w,Height=h,Thickness=thickness,Gap=gap};
        }
        static int Compare(V3 a,V3 b){int c=Math.Round(a.X,9).CompareTo(Math.Round(b.X,9));if(c!=0)return c;c=Math.Round(a.Y,9).CompareTo(Math.Round(b.Y,9));return c!=0?c:Math.Round(a.Z,9).CompareTo(Math.Round(b.Z,9));}
    }
}
