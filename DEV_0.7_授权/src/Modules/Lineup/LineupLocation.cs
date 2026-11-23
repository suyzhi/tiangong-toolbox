using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Drawing;
using System.Xml.Serialization;
using System.Windows.Forms;
using F=SolidEdgeFramework;
using A=SolidEdgeAssembly;
using G=SolidEdgeGeometry;
using P=SolidEdgePart;
namespace TianGongCadSuite {
    public static class LineupLocation {
        const int CaptureWidth=1600;
        const int CaptureHeight=1000;

        public static void SetIsometric(F.Application app){
            F.View view=(F.View)((dynamic)app.ActiveWindow).View;
            double ex,ey,ez,tx,ty,tz,ux,uy,uz,scale;bool perspective;
            view.GetCamera(out ex,out ey,out ez,out tx,out ty,out tz,out ux,out uy,out uz,out perspective,out scale);
            double distance=Math.Max(.1,Math.Sqrt((ex-tx)*(ex-tx)+(ey-ty)*(ey-ty)+(ez-tz)*(ez-tz))/Math.Sqrt(3));
            view.SetCamera(tx+distance,ty-distance,tz+distance,tx,ty,tz,-1,1,2,false,Math.Max(scale,.001));
            view.Fit();view.Update();app.DoIdle();
            view.GetCamera(out ex,out ey,out ez,out tx,out ty,out tz,out ux,out uy,out uz,out perspective,out scale);
            double x=Math.Abs(ex-tx),y=Math.Abs(ey-ty),z=Math.Abs(ez-tz),tolerance=Math.Max(x,Math.Max(y,z))*1e-5+1e-8;
            if(perspective||Math.Abs(x-y)>tolerance||Math.Abs(y-z)>tolerance)throw new InvalidOperationException("CAD 未成功切换正交等轴测视图，已停止截图。");
        }
        static void Capture(F.Application app,dynamic view,string file){
            for(int attempt=0;attempt<5;attempt++){
                app.DoIdle();view.Update();System.Threading.Thread.Sleep(200);view.SaveAsImage(file,CaptureWidth,CaptureHeight);
                using(var bitmap=new Bitmap(file)){
                    var colors=new HashSet<int>();for(int y=0;y<bitmap.Height;y+=13)for(int x=0;x<bitmap.Width;x+=13)colors.Add(bitmap.GetPixel(x,y).ToArgb());
                    if(colors.Count>8)return;
                }
            }
            throw new InvalidOperationException("CAD 返回空白截图，请等待模型显示稳定后重试。");
        }
        static bool IsFinite(double v){return !double.IsNaN(v)&&!double.IsInfinity(v);}

        // 将天工 CAD OpenGL NDC 投影坐标 (gx, gy) 按 SaveAsImage 的 fit-contain 规则映射到输出图片归一化坐标 [0,1]×[0,1]
        public static bool MapGlProjectionToImage(double gx,double gy,double vz,bool perspective,double winAspect,double imgAspect,out double normX,out double normY){
            normX=0;normY=0;
            if(!IsFinite(gx)||!IsFinite(gy)||!IsFinite(vz)||!IsFinite(winAspect)||!IsFinite(imgAspect))return false;
            if(winAspect<=1e-6||imgAspect<=1e-6)return false;
            // OpenGL 右手视图坐标系中相机朝向 -Z，透视模式下 vz >= 0 表示位于相机背面
            if(perspective&&vz>=-1e-6)return false;
            if(winAspect>=imgAspect){
                normX=0.5+0.5*gx;
                normY=0.5-0.5*gy*(imgAspect/winAspect);
            }else{
                normX=0.5+0.5*gx*(winAspect/imgAspect);
                normY=0.5-0.5*gy;
            }
            return IsFinite(normX)&&IsFinite(normY);
        }

        // 从实体投影后的 2D 三角形顶点列表（每 3 个点为一个三角形）计算面积加权质心，并落回最近的真实实体三角形中心，
        // 既不受倒角/螺纹网格密度影响，又保证箭头始终落在实体表面（不落入空心框、L 型件或环件的空白处）。
        public static bool SelectProjectedTriangleAnchor(IList<PointF> triPts,out double normX,out double normY){
            normX=0;normY=0;
            if(triPts==null||triPts.Count<3)return false;
            var centers=new List<PointF>(triPts.Count/3);
            double sumX=0,sumY=0,sumW=0;
            for(int i=0;i+2<triPts.Count;i+=3){
                var p0=triPts[i];var p1=triPts[i+1];var p2=triPts[i+2];
                if(!IsFinite(p0.X)||!IsFinite(p0.Y)||!IsFinite(p1.X)||!IsFinite(p1.Y)||!IsFinite(p2.X)||!IsFinite(p2.Y))continue;
                double mx=(p0.X+p1.X+p2.X)/3.0;
                double my=(p0.Y+p1.Y+p2.Y)/3.0;
                if(mx<0.0||mx>1.0||my<0.0||my>1.0)continue;
                double area=0.5*Math.Abs((p1.X-p0.X)*(p2.Y-p0.Y)-(p2.X-p0.X)*(p1.Y-p0.Y));
                double w=area>1e-10?area:1e-10;
                sumX+=mx*w;sumY+=my*w;sumW+=w;
                centers.Add(new PointF((float)mx,(float)my));
            }
            if(centers.Count==0||sumW<=0)return false;
            double cx=sumX/sumW,cy=sumY/sumW;
            PointF best=centers[0];
            double bestD=double.MaxValue;
            for(int i=0;i<centers.Count;i++){
                double dx=centers[i].X-cx,dy=centers[i].Y-cy;
                double d=dx*dx+dy*dy;
                if(d<bestD){bestD=d;best=centers[i];}
            }
            normX=Math.Max(0.002,Math.Min(0.998,best.X));
            normY=Math.Max(0.002,Math.Min(0.998,best.Y));
            return true;
        }

        // 从 3D 包围盒投影后的 2D 点（8 角点 + 中心点）判断是否在画面内并计算落点
        public static bool SelectProjectedBoxAnchor(IList<PointF> cornersAndCenter,out double normX,out double normY){
            normX=0;normY=0;
            if(cornersAndCenter==null||cornersAndCenter.Count==0)return false;
            double minX=double.PositiveInfinity,minY=double.PositiveInfinity;
            double maxX=double.NegativeInfinity,maxY=double.NegativeInfinity;
            int valid=0;
            for(int i=0;i<cornersAndCenter.Count;i++){
                var p=cornersAndCenter[i];
                if(!IsFinite(p.X)||!IsFinite(p.Y))continue;
                valid++;
                if(p.X<minX)minX=p.X;if(p.X>maxX)maxX=p.X;
                if(p.Y<minY)minY=p.Y;if(p.Y>maxY)maxY=p.Y;
            }
            if(valid==0||maxX<0.0||minX>1.0||maxY<0.0||minY>1.0)return false;
            var center=cornersAndCenter[cornersAndCenter.Count-1];
            if(IsFinite(center.X)&&IsFinite(center.Y)&&center.X>=0.0&&center.X<=1.0&&center.Y>=0.0&&center.Y<=1.0){
                normX=Math.Max(0.002,Math.Min(0.998,center.X));
                normY=Math.Max(0.002,Math.Min(0.998,center.Y));
                return true;
            }
            double cx=(Math.Max(0.0,minX)+Math.Min(1.0,maxX))*0.5;
            double cy=(Math.Max(0.0,minY)+Math.Min(1.0,maxY))*0.5;
            normX=Math.Max(0.002,Math.Min(0.998,cx));
            normY=Math.Max(0.002,Math.Min(0.998,cy));
            return true;
        }

        // 整张图一次读成 ARGB 数组（LockBits）。
        static int[] Pixels(Bitmap bitmap){
            var rect=new Rectangle(0,0,bitmap.Width,bitmap.Height);
            var data=bitmap.LockBits(rect,System.Drawing.Imaging.ImageLockMode.ReadOnly,System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try{
                var all=new int[bitmap.Width*bitmap.Height];
                for(int y=0;y<bitmap.Height;y++)System.Runtime.InteropServices.Marshal.Copy(data.Scan0+y*data.Stride,all,y*bitmap.Width,bitmap.Width);
                return all;
            }finally{bitmap.UnlockBits(data);}
        }
        static int R(int c){return (c>>16)&255;} static int G(int c){return (c>>8)&255;} static int B(int c){return c&255;}

        public static LineupLocationPoint DetectDifference(Bitmap background,Bitmap first,Bitmap second){
            return DetectDifferenceInRoi(background,first,second,null,null);
        }

        // 支持 ROI 裁剪与连通域过滤的高亮差分识别（3D 投影不可用时的兜底路径；防止鼠标悬停预高亮等视野外杂散像素拉偏质心）
        public static LineupLocationPoint DetectDifferenceInRoi(Bitmap background,Bitmap first,Bitmap second,Rectangle? roi,PointF? expectedNorm){
            if(background.Size!=first.Size||first.Size!=second.Size)throw new InvalidDataException("截图尺寸发生变化。");
            int w=first.Width,h=first.Height;
            int x0=0,y0=0,x1=w-1,y1=h-1;
            if(roi.HasValue){
                var rc=roi.Value;
                x0=Math.Max(0,rc.Left);y0=Math.Max(0,rc.Top);
                x1=Math.Min(w-1,rc.Right);y1=Math.Min(h-1,rc.Bottom);
                if(x0>x1||y0>y1)return new LineupLocationPoint{State="unresolved",Reason="投影区域在画面之外",Pixels=0};
            }
            var points=new List<Point>();double sx=0,sy=0;
            int[] pa=Pixels(background),pb=Pixels(first),pc=Pixels(second);
            for(int y=y0;y<=y1;y++)for(int x=x0;x<=x1;x++){
                int a=pa[y*w+x],b=pb[y*w+x],c=pc[y*w+x];
                int delta=Math.Abs(R(a)-R(b))+Math.Abs(G(a)-G(b))+Math.Abs(B(a)-B(b)),stable=Math.Abs(R(c)-R(b))+Math.Abs(G(c)-G(b))+Math.Abs(B(c)-B(b));
                if(delta>100&&stable<15){points.Add(new Point(x,y));sx+=x;sy+=y;}
            }
            if(points.Count<6||points.Count>w*h/3)return new LineupLocationPoint{State="unresolved",Reason="高亮差分缺失或影响范围过大",Pixels=points.Count};
            double cx=sx/points.Count,cy=sy/points.Count;
            if(expectedNorm.HasValue){
                double ex=expectedNorm.Value.X*w,ey=expectedNorm.Value.Y*h;
                // 若给了预期落点，优先在距预期点较近的像素群中取质心，避开 ROI 内相邻悬停件
                double maxR=Math.Max(w,h)*0.12;
                var near=points.Where(p=>(p.X-ex)*(p.X-ex)+(p.Y-ey)*(p.Y-ey)<=maxR*maxR).ToList();
                if(near.Count>=6){points=near;cx=near.Average(p=>p.X);cy=near.Average(p=>p.Y);}
            }
            var hit=points.OrderBy(p=>(p.X-cx)*(p.X-cx)+(p.Y-cy)*(p.Y-cy)).First();
            return new LineupLocationPoint{State="ok",Method="stable-highlight-difference",X=(hit.X+.5)/w,Y=(hit.Y+.5)/h,Pixels=points.Count};
        }

        // Use an actual highlighted pixel nearest the mask centroid, never an empty bounding-box center.
        public static LineupLocationPoint Detect(Bitmap first,Bitmap second){
            if(first.Size!=second.Size)throw new InvalidDataException("截图尺寸发生变化。");
            var points=new List<Point>();double sx=0,sy=0;
            int[] pa=Pixels(first),pb=Pixels(second);int w=first.Width;
            for(int y=0;y<first.Height;y++)for(int x=0;x<w;x++){
                int a=pa[y*w+x],b=pb[y*w+x];
                bool yellow=R(a)>150&&G(a)>150&&B(a)<140,magenta=R(b)>150&&B(b)>150&&G(b)<140;
                if(yellow&&magenta){points.Add(new Point(x,y));sx+=x;sy+=y;}
            }
            if(points.Count<6)return new LineupLocationPoint{State="unresolved",Reason="未识别到足够的双色高亮像素；请调整视角或检查遮挡",Pixels=points.Count};
            double cx=sx/points.Count,cy=sy/points.Count;var hit=points.OrderBy(p=>(p.X-cx)*(p.X-cx)+(p.Y-cy)*(p.Y-cy)).First();
            return new LineupLocationPoint{State="ok",X=(hit.X+.5)/first.Width,Y=(hit.Y+.5)/first.Height,Pixels=points.Count};
        }

        // 快速且经自校验的 3D→2D 相机投影器：先通过极少数 COM 采样推导出当前视角的精确变换，并用测试点做 1e-5 误差验证，
        // 校验通过则所有实体三角面片在纯 C# 中微秒级投影；未通过则自动回退到逐点调用天工 COM 变换接口。
        sealed class CameraProjector {
            public F.View View;
            public bool Perspective;
            public double WinAspect;
            public double ImgAspect;
            // Model -> View 仿射基
            bool fastMv;
            double ox,oy,oz,xx,xy,xz,yx,yy,yz,zx,zy,zz;
            // View -> GL 投影系数
            bool fastGl;
            double g0x,g0y,gxx,gxy,gyx,gyy,gzx,gzy; // ortho: gx = g0x + vx*gxx + vy*gyx + vz*gzx
            double pAx,pCx,pBy,pCy;                 // persp: gx = pAx*(vx/-vz) + pCx, gy = pBy*(vy/-vz) + pCy

            public static CameraProjector TryCreate(dynamic win,dynamic view,int imgW,int imgH){
                var fview=view as F.View;
                if(fview==null||imgW<=0||imgH<=0)return null;
                try{
                    double ex,ey,ez,tx,ty,tz,ux,uy,uz,scale;bool persp;
                    view.GetCamera(out ex,out ey,out ez,out tx,out ty,out tz,out ux,out uy,out uz,out persp,out scale);
                    double winAspect=0;
                    try{
                        int fl;double ex2,ey2,ez2,tx2,ty2,tz2,ux2,uy2,uz2,nc,fc,fw,fh,fex,fey,fsc;
                        view.GetCameraEx(out fl,out ex2,out ey2,out ez2,out tx2,out ty2,out tz2,out ux2,out uy2,out uz2,out nc,out fc,out fw,out fh,out fex,out fey,out fsc);
                        if(IsFinite(fw)&&IsFinite(fh)&&fw>1&&fh>1)winAspect=fw/fh;
                    }catch{}
                    if(winAspect<=1e-6){
                        double uw=Convert.ToDouble(win.UsableWidth),uh=Convert.ToDouble(win.UsableHeight);
                        if(IsFinite(uw)&&IsFinite(uh)&&uw>1&&uh>1)winAspect=uw/uh;
                    }
                    if(winAspect<=1e-6)return null;
                    var cp=new CameraProjector{
                        View=fview,
                        Perspective=persp,
                        WinAspect=winAspect,
                        ImgAspect=(double)imgW/imgH
                    };
                    cp.CalibrateFastPath();
                    return cp;
                }catch{return null;}
            }

            void CalibrateFastPath(){
                try{
                    double x1,y1,z1,x2,y2,z2,x3,y3,z3;
                    View.TransformModelToView(0,0,0,out ox,out oy,out oz);
                    View.TransformModelToView(1,0,0,out x1,out y1,out z1);
                    View.TransformModelToView(0,1,0,out x2,out y2,out z2);
                    View.TransformModelToView(0,0,1,out x3,out y3,out z3);
                    xx=x1-ox;xy=y1-oy;xz=z1-oz;
                    yx=x2-ox;yy=y2-oy;yz=z2-oz;
                    zx=x3-ox;zy=y3-oy;zz=z3-oz;
                    double tvx,tvy,tvz;
                    View.TransformModelToView(1.3,-0.7,2.1,out tvx,out tvy,out tvz);
                    double evx=ox+1.3*xx-0.7*yx+2.1*zx;
                    double evy=oy+1.3*xy-0.7*yy+2.1*zy;
                    double evz=oz+1.3*xz-0.7*yz+2.1*zz;
                    fastMv=Math.Abs(tvx-evx)<1e-5&&Math.Abs(tvy-evy)<1e-5&&Math.Abs(tvz-evz)<1e-5;
                }catch{fastMv=false;}

                try{
                    if(!Perspective){
                        double bx,by,bz,ax,ay,az,cx,cy,cz,dx,dy,dz;
                        View.TransformViewToGLProjection(0,0,-5,out bx,out by,out bz);
                        View.TransformViewToGLProjection(1,0,-5,out ax,out ay,out az);
                        View.TransformViewToGLProjection(0,1,-5,out cx,out cy,out cz);
                        View.TransformViewToGLProjection(0,0,-6,out dx,out dy,out dz);
                        gxx=ax-bx;gxy=ay-by;
                        gyx=cx-bx;gyy=cy-by;
                        gzx=bx-dx;gzy=by-dy; // (-5) - (-6) = +1
                        g0x=bx+5.0*gzx;g0y=by+5.0*gzy;
                        double tgx,tgy,tgz;
                        View.TransformViewToGLProjection(0.7,-0.4,-7.3,out tgx,out tgy,out tgz);
                        double egx=g0x+0.7*gxx-0.4*gyx-7.3*gzx;
                        double egy=g0y+0.7*gxy-0.4*gyy-7.3*gzy;
                        fastGl=Math.Abs(tgx-egx)<1e-5&&Math.Abs(tgy-egy)<1e-5;
                    }else{
                        double x0,y0,z0,x1,y1,z1,x2,y2,z2;
                        View.TransformViewToGLProjection(0,0,-1,out x0,out y0,out z0);
                        View.TransformViewToGLProjection(1,0,-1,out x1,out y1,out z1);
                        View.TransformViewToGLProjection(0,1,-1,out x2,out y2,out z2);
                        pCx=x0;pCy=y0;pAx=x1-x0;pBy=y2-y0;
                        double t1x,t1y,t1z,t2x,t2y,t2z;
                        View.TransformViewToGLProjection(0.5,-0.3,-2.5,out t1x,out t1y,out t1z);
                        View.TransformViewToGLProjection(-0.4,0.8,-7.0,out t2x,out t2y,out t2z);
                        double e1x=pAx*(0.5/2.5)+pCx,e1y=pBy*(-0.3/2.5)+pCy;
                        double e2x=pAx*(-0.4/7.0)+pCx,e2y=pBy*(0.8/7.0)+pCy;
                        fastGl=Math.Abs(t1x-e1x)<1e-5&&Math.Abs(t1y-e1y)<1e-5&&Math.Abs(t2x-e2x)<1e-5&&Math.Abs(t2y-e2y)<1e-5;
                    }
                }catch{fastGl=false;}
            }

            public bool IsFast{get{return fastMv&&fastGl;}}

            public bool Project(double x,double y,double z,out double nx,out double ny){
                nx=0;ny=0;
                try{
                    double vx,vy,vz,gx,gy,gz;
                    if(fastMv){
                        vx=ox+x*xx+y*yx+z*zx;
                        vy=oy+x*xy+y*yy+z*zy;
                        vz=oz+x*xz+y*yz+z*zz;
                    }else{
                        View.TransformModelToView(x,y,z,out vx,out vy,out vz);
                    }
                    if(Perspective&&vz>=-1e-6)return false;
                    if(fastGl){
                        if(!Perspective){
                            gx=g0x+vx*gxx+vy*gyx+vz*gzx;
                            gy=g0y+vx*gxy+vy*gyy+vz*gzy;
                        }else{
                            double inv=-1.0/vz;
                            gx=pAx*(vx*inv)+pCx;
                            gy=pBy*(vy*inv)+pCy;
                        }
                    }else{
                        View.TransformViewToGLProjection(vx,vy,vz,out gx,out gy,out gz);
                    }
                    return MapGlProjectionToImage(gx,gy,vz,Perspective,WinAspect,ImgAspect,out nx,out ny);
                }catch{return false;}
            }
        }

        sealed class TargetLeaf { public string File; public object Doc; public double[] Matrix; }

        static bool IsTargetVisible(object target){
            var occ=target as A.Occurrence;
            if(occ!=null){
                try{return occ.Visible;}catch{return true;}
            }
            var sub=target as A.SubOccurrence;
            if(sub!=null){
                try{if(!sub.Visible)return false;}catch{}
                try{
                    object top;int count,bound;Array path=new object[0];
                    sub.Reference.GetOccurrencesInPath(out top,out count,out bound,ref path);
                    var topOcc=top as A.Occurrence;
                    if(topOcc!=null){try{if(!topOcc.Visible)return false;}catch{}}
                    if(path!=null)foreach(object item in path){
                        var ps=item as A.SubOccurrence;
                        if(ps!=null){try{if(!ps.Visible)return false;}catch{}}
                    }
                }catch{}
                return true;
            }
            return true;
        }

        static double[] ReadMatrix(A.Occurrence o,A.SubOccurrence s){
            try{
                Array m=new double[16];
                if(o!=null)o.GetMatrix(ref m);else s.GetMatrix(ref m);
                var d=new double[16];
                for(int i=0;i<16;i++){d[i]=Convert.ToDouble(m.GetValue(i));if(!IsFinite(d[i]))return null;}
                return d;
            }catch{return null;}
        }

        static void CollectTargetLeaves(object target,List<TargetLeaf> leaves,int maxLeaves){
            var occ=target as A.Occurrence;
            if(occ!=null){
                bool sub=false;try{sub=occ.Subassembly;}catch{}
                if(sub){try{WalkSubLeaves(occ.SubOccurrences,1,leaves,maxLeaves);}catch{}return;}
                object doc=null;try{doc=occ.OccurrenceDocument;}catch{}
                var m=ReadMatrix(occ,null);
                if(doc!=null&&m!=null){string f="";try{f=occ.OccurrenceFileName??"";}catch{}leaves.Add(new TargetLeaf{File=f,Doc=doc,Matrix=m});}
                return;
            }
            var s=target as A.SubOccurrence;
            if(s!=null){
                bool sub=false;try{sub=s.Subassembly;}catch{}
                if(sub){try{WalkSubLeaves(s.SubOccurrences,1,leaves,maxLeaves);}catch{}return;}
                object doc=null;try{doc=s.SubOccurrenceDocument;}catch{}
                var m=ReadMatrix(null,s);
                if(doc!=null&&m!=null){string f="";try{f=s.SubOccurrenceFileName??"";}catch{}leaves.Add(new TargetLeaf{File=f,Doc=doc,Matrix=m});}
            }
        }

        static void WalkSubLeaves(A.SubOccurrences subs,int depth,List<TargetLeaf> leaves,int maxLeaves){
            if(subs==null||depth>6||leaves.Count>=maxLeaves)return;
            int n=0;try{n=subs.Count;}catch{}
            for(int i=1;i<=n&&leaves.Count<maxLeaves;i++){
                A.SubOccurrence s=null;try{s=subs.Item(i);}catch{}
                if(s==null)continue;
                bool vis=true;try{vis=s.Visible;}catch{}
                if(!vis)continue;
                bool sub=false;try{sub=s.Subassembly;}catch{}
                if(sub){try{WalkSubLeaves(s.SubOccurrences,depth+1,leaves,maxLeaves);}catch{}continue;}
                object doc=null;try{doc=s.SubOccurrenceDocument;}catch{}
                var m=ReadMatrix(null,s);
                if(doc!=null&&m!=null){string f="";try{f=s.SubOccurrenceFileName??"";}catch{}leaves.Add(new TargetLeaf{File=f,Doc=doc,Matrix=m});}
            }
        }

        static float[] ReadLocalFacets(object doc,Dictionary<string,float[]> cache,string fileKey){
            float[] cached;
            if(!string.IsNullOrEmpty(fileKey)&&cache.TryGetValue(fileKey,out cached))return cached;
            var bodies=new List<G.Body>();
            var part=doc as P.PartDocument;
            if(part!=null)bodies=AssemblyPart.BodiesOf(part,"",null);
            else if(doc is P.SheetMetalDocument)bodies=AssemblyPart.SheetBodies(doc,"",null);
            var list=new List<float>();
            foreach(var b in bodies){
                try{
                    Array facets=new double[0];object normals=null,texture=null,styles=null,faceIds=null;int n=0;
                    b.GetFacetData(0.002,out n,ref facets,out normals,out texture,out styles,out faceIds,false);
                    if(n<=0||facets.Length<n*9){
                        facets=new double[0];n=0;
                        b.GetFacetData(0.0005,out n,ref facets,out normals,out texture,out styles,out faceIds,false);
                    }
                    if(n<=0||facets.Length<n*9)continue;
                    var d=facets as double[];
                    for(int i=0;i<n*9;i++)list.Add((float)(d!=null?d[i]:Convert.ToDouble(facets.GetValue(i))));
                }catch{}
            }
            var arr=list.ToArray();
            if(!string.IsNullOrEmpty(fileKey))cache[fileKey]=arr;
            return arr;
        }

        static bool TryReadRange(object target,out double x0,out double y0,out double z0,out double x1,out double y1,out double z1){
            x0=y0=z0=x1=y1=z1=0;
            try{
                dynamic td=target;
                td.Range(out x0,out y0,out z0,out x1,out y1,out z1);
                return IsFinite(x0)&&IsFinite(y0)&&IsFinite(z0)&&IsFinite(x1)&&IsFinite(y1)&&IsFinite(z1)&&x1>=x0&&y1>=y0&&z1>=z0;
            }catch{return false;}
        }

        static LineupLocationPoint TryProjectTarget(CameraProjector cam,object target,Dictionary<string,float[]> facetCache){
            if(cam==null||target==null)return null;
            if(!IsTargetVisible(target))
                return new LineupLocationPoint{State="unresolved",Reason="该模型在装配中已隐藏，请显示后重拍或在图上手动点选"};

            double rx0,ry0,rz0,rx1,ry1,rz1;
            bool hasRange=TryReadRange(target,out rx0,out ry0,out rz0,out rx1,out ry1,out rz1);
            double rcx=(rx0+rx1)*0.5,rcy=(ry0+ry1)*0.5,rcz=(rz0+rz1)*0.5;
            double rDiag=hasRange?Math.Sqrt((rx1-rx0)*(rx1-rx0)+(ry1-ry0)*(ry1-ry0)+(rz1-rz0)*(rz1-rz0)):0;

            // 1. 优先投影真实实体三角面片（解决空心件、L型件与杂散参考特征导致包围盒中心偏空的问题）
            var leaves=new List<TargetLeaf>();
            CollectTargetLeaves(target,leaves,64);
            if(leaves.Count>0&&leaves.Count<64){
                int totalTris=0;
                var leafFacets=new List<KeyValuePair<TargetLeaf,float[]>>(leaves.Count);
                foreach(var lf in leaves){
                    var f=ReadLocalFacets(lf.Doc,facetCache,lf.File);
                    if(f.Length>=9){leafFacets.Add(new KeyValuePair<TargetLeaf,float[]>(lf,f));totalTris+=f.Length/9;}
                }
                if(totalTris>0){
                    int maxSampleTris=cam.IsFast?2400:160;
                    int stride=Math.Max(1,totalTris/maxSampleTris);
                    var projTris=new List<PointF>(Math.Min(totalTris,maxSampleTris)*3+12);
                    double mx0=double.PositiveInfinity,my0=mx0,mz0=mx0;
                    double mx1=double.NegativeInfinity,my1=mx1,mz1=mx1;
                    int triIndex=0;
                    foreach(var kv in leafFacets){
                        var m=kv.Key.Matrix;var f=kv.Value;
                        for(int k=0;k+8<f.Length;k+=9){
                            if((triIndex++)%stride!=0)continue;
                            double ax=f[k],ay=f[k+1],az=f[k+2];
                            double bx=f[k+3],by=f[k+4],bz=f[k+5];
                            double cx=f[k+6],cy=f[k+7],cz=f[k+8];
                            double wax=m[0]*ax+m[4]*ay+m[8]*az+m[12],way=m[1]*ax+m[5]*ay+m[9]*az+m[13],waz=m[2]*ax+m[6]*ay+m[10]*az+m[14];
                            double wbx=m[0]*bx+m[4]*by+m[8]*bz+m[12],wby=m[1]*bx+m[5]*by+m[9]*bz+m[13],wbz=m[2]*bx+m[6]*by+m[10]*bz+m[14];
                            double wcx=m[0]*cx+m[4]*cy+m[8]*cz+m[12],wcy=m[1]*cx+m[5]*cy+m[9]*cz+m[13],wcz=m[2]*cx+m[6]*cy+m[10]*cz+m[14];
                            if(wax<mx0)mx0=wax;if(wax>mx1)mx1=wax;if(way<my0)my0=way;if(way>my1)my1=way;if(waz<mz0)mz0=waz;if(waz>mz1)mz1=waz;
                            if(wbx<mx0)mx0=wbx;if(wbx>mx1)mx1=wbx;if(wby<my0)my0=wby;if(wby>my1)my1=wby;if(wbz<mz0)mz0=wbz;if(wbz>mz1)mz1=wbz;
                            if(wcx<mx0)mx0=wcx;if(wcx>mx1)mx1=wcx;if(wcy<my0)my0=wcy;if(wcy>my1)my1=wcy;if(wcz<mz0)mz0=wcz;if(wcz>mz1)mz1=wcz;
                            double p0x,p0y,p1x,p1y,p2x,p2y;
                            if(cam.Project(wax,way,waz,out p0x,out p0y)&&cam.Project(wbx,wby,wbz,out p1x,out p1y)&&cam.Project(wcx,wcy,wcz,out p2x,out p2y)){
                                projTris.Add(new PointF((float)p0x,(float)p0y));
                                projTris.Add(new PointF((float)p1x,(float)p1y));
                                projTris.Add(new PointF((float)p2x,(float)p2y));
                            }
                        }
                    }
                    // 校验变换后实体网格的 3D 包围盒是否与 target.Range 一致（防止柔性/可调子装配发生矩阵偏差）
                    bool rangeConsistent=true;
                    if(hasRange&&!double.IsInfinity(mx0)){
                        double mcx=(mx0+mx1)*0.5,mcy=(my0+my1)*0.5,mcz=(mz0+mz1)*0.5;
                        double dist=Math.Sqrt((mcx-rcx)*(mcx-rcx)+(mcy-rcy)*(mcy-rcy)+(mcz-rcz)*(mcz-rcz));
                        double tol=Math.Max(0.08,rDiag*0.65);
                        if(dist>tol)rangeConsistent=false;
                    }
                    if(rangeConsistent&&projTris.Count>=3){
                        double ax,ay;
                        if(SelectProjectedTriangleAnchor(projTris,out ax,out ay))
                            return new LineupLocationPoint{State="ok",Method="3d-mesh-projection",X=ax,Y=ay,Pixels=projTris.Count/3};
                        // 若所有采样三角中心都在画面外但大面跨越画框，继续交给下方包围盒投影做裁切判定
                    }
                }
            }

            // 2. 回退到 3D 包围盒 8 角点 + 中心投影（柔性子装配、大型子装配或无实体面片件）
            if(hasRange){
                var pts=new List<PointF>(9);
                double[] xs={rx0,rx1},ys={ry0,ry1},zs={rz0,rz1};
                foreach(double x in xs)foreach(double y in ys)foreach(double z in zs){
                    double px,py;if(cam.Project(x,y,z,out px,out py))pts.Add(new PointF((float)px,(float)py));
                }
                double cx2,cy2;
                if(cam.Project(rcx,rcy,rcz,out cx2,out cy2))pts.Add(new PointF((float)cx2,(float)cy2));
                double ax,ay;
                if(SelectProjectedBoxAnchor(pts,out ax,out ay))
                    return new LineupLocationPoint{State="ok",Method="3d-range-projection",X=ax,Y=ay,Pixels=pts.Count};
                return new LineupLocationPoint{State="unresolved",Reason="该模型在当前视角画面之外，请调整视角重拍或在图上手动点选"};
            }
            return null;
        }

        public static LineupLocationPackage Export(F.Application app,A.AssemblyDocument doc,LineupProject project,IEnumerable<string> ids,string directory){
            if(Directory.Exists(directory))throw new IOException("输出目录已存在，请使用新的目录。");Directory.CreateDirectory(directory);
            return CaptureCore(app,doc,project,ids,directory,"view.png",true,true,false,null);
        }

        // 新版多页 CAD 位置图抓图：支持「等轴测」或「保持当前 CAD 视角」，自动清理临时差分图，结果直接存入 .lineup.cad
        public static LineupCadPage CapturePage(F.Application app,A.AssemblyDocument doc,LineupProject project,IEnumerable<string> ids,string pageName,bool useIsometric,bool zoomToTargets,bool autoDetect,Action<string> progress){
            string assets=LineupProject.CadAssetsDir(project.AssemblyFile);
            Directory.CreateDirectory(assets);
            string fileName="page-"+DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")+".png";
            var pack=CaptureCore(app,doc,project,ids,assets,fileName,useIsometric,zoomToTargets,true,progress,autoDetect);
            return new LineupCadPage{
                Name=string.IsNullOrWhiteSpace(pageName)?"总图":pageName.Trim(),
                LineCode=project.LineCode??"L1",
                ImageFile=fileName,
                ViewMode=useIsometric?"isometric":"current",
                Width=pack.Width,
                Height=pack.Height,
                Points=pack.Points
            };
        }

        static LineupLocationPackage CaptureCore(F.Application app,A.AssemblyDocument doc,LineupProject project,IEnumerable<string> ids,string directory,string imageName,bool useIsometric,bool zoomToTargets,bool cleanupTemp,Action<string> progress,bool autoDetect=true){
            var idSet=new HashSet<string>(ids??Enumerable.Empty<string>());
            var targetRecords=project.Records.Where(r=>idSet.Contains(r.Id)).ToList();
            var package=new LineupLocationPackage{AssemblyFile=project.AssemblyFile,Image=imageName};
            dynamic win=app.ActiveWindow;
            dynamic view=win.View;
            var selected=new List<object>();for(int i=1;i<=doc.SelectSet.Count;i++)selected.Add(doc.SelectSet.Item(i));
            string imgPath=Path.Combine(directory,imageName);
            string tempDir=cleanupTemp?Path.Combine(Path.GetTempPath(),"tg-lineup-cap-"+Guid.NewGuid().ToString("N")):directory;
            if(cleanupTemp)Directory.CreateDirectory(tempDir);
            try{
                doc.SelectSet.RemoveAll();
                if(useIsometric)SetIsometric(app);
                if(zoomToTargets){
                    double minX=double.PositiveInfinity,minY=minX,minZ=minX,maxX=double.NegativeInfinity,maxY=maxX,maxZ=maxX;
                    foreach(var record in targetRecords)try{
                        if(string.IsNullOrEmpty(record.ReferenceKey))continue;
                        object target=LineupCad.ResolveTarget(doc,record);double x,y,z,u,v,w;
                        if(TryReadRange(target,out x,out y,out z,out u,out v,out w)){
                            minX=Math.Min(minX,x);minY=Math.Min(minY,y);minZ=Math.Min(minZ,z);maxX=Math.Max(maxX,u);maxY=Math.Max(maxY,v);maxZ=Math.Max(maxZ,w);
                        }
                    }catch{}
                    if(!double.IsInfinity(minX)){double pad=Math.Max(.003,Math.Max(maxX-minX,Math.Max(maxY-minY,maxZ-minZ))*.12);view.RangeZoomCamera(minX-pad,minY-pad,minZ-pad,maxX+pad,maxY+pad,maxZ+pad);view.Update();app.DoIdle();}
                }
                if(progress!=null)progress("正在截取 CAD 基准画面…");
                Capture(app,view,imgPath);
                using(var baseImage=new Bitmap(imgPath)){package.Width=baseImage.Width;package.Height=baseImage.Height;}

                // 紧接基准截图后立刻快照相机投影参数（零多余截图、免疫鼠标悬停预高亮、毫秒级精准映射）
                var cam=autoDetect?CameraProjector.TryCreate(win,view,package.Width,package.Height):null;
                var facetCache=new Dictionary<string,float[]>(StringComparer.OrdinalIgnoreCase);
                bool usedPixelFallback=false;

                int idx=0;
                foreach(var r in targetRecords){
                    idx++;
                    var point=new LineupLocationPoint{State="unresolved"};
                    if(!autoDetect){
                        point.Reason="未自动定位，请在预览图上点选箭头位置";
                    }else if(string.IsNullOrEmpty(r.ReferenceKey)){
                        point.Reason="该记录未绑定三维模型，请在预览图上点选箭头位置";
                    }else{
                        if(progress!=null)progress("正在定位 ("+idx+"/"+targetRecords.Count+")："+r.Number);
                        try{
                            var target=LineupCad.ResolveTarget(doc,r);
                            var projected=cam!=null?TryProjectTarget(cam,target,facetCache):null;
                            if(projected!=null){
                                point=projected;
                            }else{
                                // 极端兜底：仅在 3D 投影接口不可用时才退回到高亮差分
                                usedPixelFallback=true;
                                point=FallbackDetectByHighlight(app,doc,view,target,r.Id,imgPath,tempDir);
                            }
                        }catch(Exception ex){point.Reason=ex.Message;}
                    }
                    point.Id=r.Id;point.Number=r.Number;point.Model=r.Model;point.Category=r.Category;point.Description=r.Description;
                    package.Points.Add(point);
                }

                if(usedPixelFallback&&autoDetect&&targetRecords.Any(r=>!string.IsNullOrEmpty(r.ReferenceKey))){
                    string checkFile=Path.Combine(tempDir,"view-check.png");Capture(app,view,checkFile);
                    using(var a=new Bitmap(imgPath))using(var b=new Bitmap(checkFile)){
                        int changed=0,total=0;for(int y=0;y<a.Height;y+=5)for(int x=0;x<a.Width;x+=5){total++;var c=a.GetPixel(x,y);var d=b.GetPixel(x,y);if(Math.Abs(c.R-d.R)+Math.Abs(c.G-d.G)+Math.Abs(c.B-d.B)>60)changed++;}
                        if(changed>total*.002)foreach(var point in package.Points){point.State="unresolved";point.Reason="截图前后视图发生变化，请保持视角不动重新导出";point.X=point.Y=0;}
                    }
                }
                if(!cleanupTemp){
                    using(var stream=File.Create(Path.Combine(directory,"locations.xml")))new XmlSerializer(typeof(LineupLocationPackage)).Serialize(stream,package);
                    var rows=new List<string[]>{new[]{"编号","型号","状态","原因","X比例","Y比例"}};rows.AddRange(package.Points.Select(p=>new[]{p.Number,p.Model,p.State,p.Reason,p.X.ToString(System.Globalization.CultureInfo.InvariantCulture),p.Y.ToString(System.Globalization.CultureInfo.InvariantCulture)}));File.WriteAllText(Path.Combine(directory,"locations.csv"),LineupCsv.Write(rows),new System.Text.UTF8Encoding(true));
                }
                return package;
            }finally{
                if(cleanupTemp)try{Directory.Delete(tempDir,true);}catch{}
                foreach(var item in selected)try{doc.SelectSet.Add(item);}catch{}
                view.Update();
            }
        }

        static LineupLocationPoint FallbackDetectByHighlight(F.Application app,A.AssemblyDocument doc,dynamic view,object target,string recId,string imgPath,string tempDir){
            var point=new LineupLocationPoint{State="unresolved"};
            dynamic highlight=null;
            try{
                highlight=doc.HighlightSets.Add();highlight.Color=0x00FFFF;highlight.AddItem(LineupCad.HighlightTarget(target));highlight.Draw();view.Update();
                string yellow=Path.Combine(tempDir,recId+"-yellow.png"),magenta=Path.Combine(tempDir,recId+"-magenta.png");Capture(app,view,yellow);
                highlight.Color=0xFF00FF;highlight.Draw();Capture(app,view,magenta);
                using(var a=new Bitmap(yellow))using(var b=new Bitmap(magenta))using(var bg=new Bitmap(imgPath)){
                    point=Detect(a,b);
                    if(point.State!="ok")point=DetectDifference(bg,a,b);else point.Method="dual-color";
                }
                if(point.State!="ok"){
                    highlight.Delete();highlight=null;dynamic occurrence=target;bool visible=occurrence.Visible;
                    if(visible)try{
                        occurrence.Visible=false;
                        string hide1=Path.Combine(tempDir,recId+"-hide1.png"),hide2=Path.Combine(tempDir,recId+"-hide2.png");Capture(app,view,hide1);Capture(app,view,hide2);
                        using(var a=new Bitmap(hide1))using(var b=new Bitmap(hide2))using(var bg=new Bitmap(imgPath))point=DetectDifference(bg,a,b);
                        if(point.State=="ok")point.Method="stable-visibility-difference";
                    }finally{occurrence.Visible=visible;view.Update();}
                }
            }catch(Exception ex){point.Reason=ex.Message;}
            finally{if(highlight!=null)try{highlight.Delete();}catch{}view.Update();}
            return point;
        }
    }
    public sealed partial class LineupTableForm {
        void ExportLocationDialog(){
            SaveDraft();var ids=SelectedIds();if(ids.Count==0)throw new InvalidOperationException("请选择本张位置图需要标注的清单行；可跨类别多选。");
            string picked=FolderPicker.Pick(this,"选择位置图包保存目录（会新建带时间的子目录）",Path.GetDirectoryName(file),"lineup.location");
            if(!string.IsNullOrEmpty(picked)){
                string folder=Path.Combine(picked,"Lineup位置图-"+DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));ClearHighlight();Hide();
                try{var package=LineupLocation.Export(app,assembly,project,ids,folder);status.Text="已导出："+folder+"；识别 "+package.Points.Count(p=>p.State=="ok")+" / "+package.Points.Count+"。请核对位置后导入 WPS。";}
                finally{Show();}
            }
        }
    }
}
