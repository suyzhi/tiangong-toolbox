using System;
using System.Collections.Generic;
using System.Linq;
using A=SolidEdgeAssembly;
using F=SolidEdgeFramework;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;
namespace TianGongCadSuite {
    public sealed class FrameFace {
        public PlaneInput Plane;
        public List<V3[]> Edges=new List<V3[]>();
    }
    public sealed class FrameMember {
        public string Name;
        public string Source="";   // 来自哪个顶层实例（子装配名），界面汇总用
        public V3 Axis;
        public List<V3> Points=new List<V3>();
        public List<FrameFace> Faces=new List<FrameFace>();
        public List<V3[]> Triangles=new List<V3[]>();
    }
    public sealed class FrameOpening {
        public PlaneInput[] Planes;
        public V3 MidPoint;
        public PanelSpec Solve(double thickness,double gap){return PanelGeometry.Solve(Planes,MidPoint,thickness,gap);}
    }
    // 一次读取的结果：型材、已有板子（不参与识别，只用来标"已有板"）、读不了跳过的零件
    public sealed class FrameSelection {
        public readonly List<FrameMember> Members=new List<FrameMember>();
        public readonly List<FrameMember> Plates=new List<FrameMember>();
        public readonly List<string> Skipped=new List<string>();   // 零件实例名（原因写在 panel.log 的 FrameReaderSkip）
        public string Summary(){
            var sources=Members.GroupBy(m=>m.Source??"").Select(g=>(g.Key.Length>0?g.Key:"顶层零件")+" "+g.Count()+" 根").ToList();
            string text="已读 "+Members.Count+" 根型材"+(sources.Count>1?"，来自 "+sources.Count+" 处："+string.Join("、",sources.ToArray()):"")+"。";
            if(Plates.Count>0)text+="另有 "+Plates.Count+" 块板子（不当型材，只用来标出已有板的框口）。";
            if(Skipped.Count>0)text+="跳过 "+Skipped.Count+" 个不是直型材 / 打不开的零件（"+string.Join("、",Skipped.Take(2).ToArray())+(Skipped.Count>2?" 等":"")+"）。";
            return text;
        }
    }
    public static class FrameReader {
        const int MaxMembers=400;
        // 只认实例身份去重，不按源文件名（同一根型材文件插很多次是常态）。
        public static List<FrameMember> Read(A.AssemblyDocument assembly,IEnumerable<object> selection){
            var result=ReadAll(assembly,selection);
            if(result.Members.Count<4)throw new ArgumentException("请至少选择围成框口的四根型材，或选择包含它们的框架子装配。"+(result.Members.Count+result.Plates.Count+result.Skipped.Count>0?"\n"+result.Summary():""));
            return result.Members;
        }
        // 2026-10-15：可以跨子装配选型材了。
        //   · 子装配内部的零件（顶层装配里在装配树/快速选取点的）由 OccurrencePick 解析成顶层装配里的 SubOccurrence，
        //     坐标是顶层装配的 —— 原来直接取 Reference.Object，拿到的是子装配自己文档里的实例，报「选择不属于当前装配」；
        //   · 整个子装配选上时，里面不是直型材的零件（多实体的标准件、铰链……）跳过并记下，不再整批失败；
        //   · 薄板（以前生成的填充板、玻璃板）放进 Plates，不当型材。
        public static FrameSelection ReadAll(A.AssemblyDocument assembly,IEnumerable<object> selection){return ReadAll(assembly,selection,null);}
        // progress(已读几个零件)：整个围栏几百根型材（每根读边、面、三角面）要读好一阵，窗口拿它刷状态栏、泵消息
        public static FrameSelection ReadAll(A.AssemblyDocument assembly,IEnumerable<object> selection,Action<int> progress){
            var result=new FrameSelection();var seen=new HashSet<string>();
            foreach(var item in selection)ReadOne(assembly,item,result,seen,null,progress);
            return result;
        }
        static void ReadOne(A.AssemblyDocument assembly,object selected,FrameSelection result,HashSet<string> seen,string source,Action<int> progress){
            object instance=OccurrencePick.Instance(selected);
            if(instance==null){var reference=selected as F.Reference;if(reference!=null)instance=reference.Object;}
            selected=instance??selected;
            var occurrence=selected as A.Occurrence;var sub=selected as A.SubOccurrence;
            if(occurrence==null&&sub==null)throw new ArgumentException("请在装配选择工具中选中零件实例或框架子装配，不要选择面、边或草图。");
            if(!ReferenceEquals(occurrence!=null?occurrence.TopLevelDocument:sub.TopLevelDocument,assembly))throw new ArgumentException("选择不属于当前装配。");
            Array key=new byte[0];object size=null;
            if(occurrence!=null)occurrence.GetReferenceKey(ref key,out size);else sub.GetReferenceKey(ref key,out size);
            string identity=Convert.ToBase64String(key.Cast<object>().Select(Convert.ToByte).ToArray());
            if(!seen.Add(identity))return;
            if(source==null)source=OccurrencePick.TopName(selected);
            bool isAssembly=occurrence!=null?occurrence.Subassembly:sub.Subassembly;
            if(isAssembly){foreach(A.SubOccurrence child in occurrence!=null?occurrence.SubOccurrences:sub.SubOccurrences)ReadOne(assembly,child,result,seen,source,progress);return;}
            if(result.Members.Count>=MaxMembers)throw new ArgumentException("单次最多读取 "+MaxMembers+" 根型材，请分批选取。");
            string name=occurrence!=null?occurrence.Name:sub.Name;
            if(progress!=null){try{progress(result.Members.Count+result.Plates.Count+result.Skipped.Count+1);}catch{}}
            FrameMember member;
            try{member=ReadPart(assembly,selected,occurrence,sub);}
            catch(Exception e){
                // 不是直型材（多实体标准件、曲面件……）：跳过、记下，别让整批失败
                if(!(e is ArgumentException||e is System.Runtime.InteropServices.COMException||e is InvalidCastException))throw;
                result.Skipped.Add(name+(e.Message.StartsWith("文件打不开")?"（文件打不开）":""));Log.Write("FrameReaderSkip",name+"："+e.Message);return;
            }
            member.Source=source??"";
            if(PanelPlates.IsPlate(member.Axis,member.Points))result.Plates.Add(member);else result.Members.Add(member);
        }
        static FrameMember ReadPart(A.AssemblyDocument assembly,object selected,A.Occurrence occurrence,A.SubOccurrence sub){
            // Native StructuralFrames use override bodies for their cut-to-length/mitered geometry.
            var definition=occurrence??sub.ThisAsOccurrence;
            bool subOverride=sub!=null&&sub.HasBodyOverride;
            bool bodyOverride=subOverride||definition.HasBodyOverride;
            var document=(occurrence!=null?occurrence.OccurrenceDocument:sub.SubOccurrenceDocument) as P.PartDocument;
            if(document==null&&!(occurrence!=null?occurrence.OccurrenceDocument!=null:sub.SubOccurrenceDocument!=null))throw new ArgumentException("文件打不开（不在了或加载失败）");
            if(document==null||(!bodyOverride&&document.Models.Count!=1))throw new ArgumentException("自动模式目前需要单实体 PAR 型材。");
            var body=bodyOverride?(G.Body)(subOverride?sub.Body:definition.Body):(G.Body)document.Models.Item(1).Body;
            var member=new FrameMember{Name=occurrence!=null?occurrence.Name:sub.Name};
            Transform overrideTransform=null;
            if(bodyOverride){
                Array inversion=new double[16],placement=new double[16];
                if(subOverride)sub.GetBodyInversionMatrix(ref inversion);else definition.GetBodyInversionMatrix(ref inversion);
                if(occurrence!=null)occurrence.GetMatrix(ref placement);else sub.GetMatrix(ref placement);
                var inv=new Transform(inversion);var place=new Transform(placement);
                // Override geometry is already in its owning assembly space. First return it
                // to part coordinates, then apply the full target occurrence placement once.
                overrideTransform=Transform.Frame(place.Point(inv.Point(new V3())),place.Vector(inv.Vector(new V3(1,0,0))),place.Vector(inv.Vector(new V3(0,1,0))),place.Vector(inv.Vector(new V3(0,0,1))));
            }
            Func<object,object> makeReference=entity=>{
                if(bodyOverride)return assembly.CreateReference2(selected,entity);
                if(occurrence!=null)return assembly.CreateReference(occurrence,entity);
                Array topologyKey=new byte[0];((dynamic)entity).GetReferenceKey(ref topologyKey);A.TopologyReference tr;sub.CreateTopologyReference(ref topologyKey,out tr);return tr;
            };
            // 同一个实例里所有边、面的"零件 → 装配"变换都一样：只拿第一个建引用取一次矩阵，后面直接复用。
            // 原来每条边、每张面各建一个装配引用（2～4 次 COM 调用），选整个围栏几百根型材时光这一步就要几分钟。
            Transform instance=null;
            Func<object,PickGeometry> pick=entity=>{
                if(bodyOverride)return new PickGeometry{Geometry=entity,Transform=overrideTransform};
                if(instance==null)instance=PickGeometry.Unwrap(makeReference(entity)).Transform;
                return new PickGeometry{Geometry=entity,Transform=instance};
            };
            double longest=0;
            foreach(G.Edge edge in (G.Edges)body.Edges[G.FeatureTopologyQueryTypeConstants.igQueryAll]){
                var pg=pick(edge);
                Array data=new double[0],parameters=new double[0];int count;edge.GetStrokeData(.00001,out count,ref data,ref parameters);
                var points=new List<V3>();for(int i=0;i+2<data.Length;i+=3)points.Add(pg.Transform.Point(new V3(Convert.ToDouble(data.GetValue(i)),Convert.ToDouble(data.GetValue(i+1)),Convert.ToDouble(data.GetValue(i+2)))));
                member.Points.AddRange(points);
                if(edge.Geometry is G.Line&&points.Count>1){var delta=points.Last()-points[0];if(delta.Length>longest){longest=delta.Length;member.Axis=delta.Canonical();}}
            }
            foreach(G.Face face in (G.Faces)body.Faces[G.FeatureTopologyQueryTypeConstants.igQueryPlane]){
                var pg=pick(face);member.Faces.Add(new FrameFace{Plane=pg.Plane(),Edges=pg.Outline()});
            }
            if(longest<.001||member.Points.Count<8)throw new ArgumentException("未读取到有效直型材："+member.Name);
            Array occurrenceMatrix=new double[16];if(occurrence!=null)occurrence.GetMatrix(ref occurrenceMatrix);else sub.GetMatrix(ref occurrenceMatrix);
            var placementTransform=new Transform(occurrenceMatrix);
            // Longitudinal edges are used as the member axis. We deliberately avoid
            // reading ExtrudedProtrusion.Profile here: frame members and some imported
            // aluminum profiles expose that COM property as E_FAIL in an assembly.
            Array facets=new double[0];object normals=null,texture=null,styles=null,faceIds=null;int facetCount;
            body.GetFacetData(.000001,out facetCount,ref facets,out normals,out texture,out styles,out faceIds,false);
            if(facets.Length!=facetCount*9)throw new ArgumentException("CAD 未返回完整的三角面数据："+member.Name);
            var bodyTransform=bodyOverride?overrideTransform:placementTransform;
            for(int i=0;i<facets.Length;i+=9){var triangle=new V3[3];for(int j=0;j<3;j++)triangle[j]=bodyTransform.Point(new V3(Convert.ToDouble(facets.GetValue(i+j*3)),Convert.ToDouble(facets.GetValue(i+j*3+1)),Convert.ToDouble(facets.GetValue(i+j*3+2))));member.Triangles.Add(triangle);}
            return member;
        }
    }
    public static class FrameDetection {
        const double Tol=1e-7;
        sealed class Bounds {public double X0,X1,Y0,Y1,Z0,Z1;public FrameMember Member;}
        static List<double> Unique(IEnumerable<double> values){var result=new List<double>();foreach(double v in values.OrderBy(x=>x))if(result.Count==0||v-result.Last()>Tol)result.Add(v);return result;}
        public static List<FrameOpening> Detect(IList<FrameMember> members,double thickness,double gap=0){
            if(members==null||members.Count<4)throw new ArgumentException("至少需要四根型材。");
            if(double.IsNaN(thickness)||double.IsInfinity(thickness)||thickness<=Tol)throw new ArgumentException("总厚度必须大于 0.0001 mm。");
            V3 u=members[0].Axis.Canonical();var other=members.FirstOrDefault(m=>m.Axis.Cross(u).Length>PanelGeometry.AngularTolerance);
            if(other==null)throw new ArgumentException("所选型材均平行，没有闭合矩形框口。");
            V3 v=other.Axis.Canonical();if(Math.Abs(u.Dot(v))>PanelGeometry.AngularTolerance)throw new ArgumentException("自动模式仅支持相互垂直的直型材。");
            V3 n=u.Cross(v).Unit();var bounds=new List<Bounds>();
            foreach(var m in members){
                if(m.Axis.Cross(u).Length>PanelGeometry.AngularTolerance&&m.Axis.Cross(v).Length>PanelGeometry.AngularTolerance)throw new ArgumentException("所选型材不在同一矩形方向组，请分组选取。");
                var b=new Bounds{Member=m,X0=m.Points.Min(p=>p.Dot(u)),X1=m.Points.Max(p=>p.Dot(u)),Y0=m.Points.Min(p=>p.Dot(v)),Y1=m.Points.Max(p=>p.Dot(v)),Z0=m.Points.Min(p=>p.Dot(n)),Z1=m.Points.Max(p=>p.Dot(n))};
                double length=m.Axis.Cross(u).Length<PanelGeometry.AngularTolerance?b.X1-b.X0:b.Y1-b.Y0;
                double width=m.Axis.Cross(u).Length<PanelGeometry.AngularTolerance?b.Y1-b.Y0:b.X1-b.X0;
                bounds.Add(b);
            }
            double depth=(bounds[0].Z0+bounds[0].Z1)/2;
            if(bounds.Any(b=>Math.Abs((b.Z0+b.Z1)/2-depth)>Tol))throw new ArgumentException("型材厚度中心不共面。请按平面分组选取，或使用手动定位点模式。");
            if(bounds.Any(b=>depth-thickness/2<b.Z0-Tol||depth+thickness/2>b.Z1+Tol))throw new ArgumentException("板厚超出型材共同深度范围。");
            var xs=Unique(bounds.SelectMany(b=>new[]{b.X0,b.X1}));var ys=Unique(bounds.SelectMany(b=>new[]{b.Y0,b.Y1}));
            xs.Insert(0,xs[0]-.01);xs.Add(xs.Last()+.01);ys.Insert(0,ys[0]-.01);ys.Add(ys.Last()+.01);
            int nx=xs.Count-1,ny=ys.Count-1;if((long)nx*ny>160000)throw new ArgumentException("框架过大，请分组选取。");
            var blocked=new bool[nx,ny];var visited=new bool[nx,ny];
            // Envelopes are used only for candidate topology. Every final boundary must also
            // be supported by actual planar material over the complete panel thickness below.
            for(int x=0;x<nx;x++)for(int y=0;y<ny;y++){double cx=(xs[x]+xs[x+1])/2,cy=(ys[y]+ys[y+1])/2;blocked[x,y]=bounds.Any(b=>cx>b.X0-Tol&&cx<b.X1+Tol&&cy>b.Y0-Tol&&cy<b.Y1+Tol);}
            var result=new List<FrameOpening>();int[] dx={1,-1,0,0},dy={0,0,1,-1};
            for(int sx=0;sx<nx;sx++)for(int sy=0;sy<ny;sy++){
                if(blocked[sx,sy]||visited[sx,sy])continue;
                var queue=new Queue<int[]>();queue.Enqueue(new[]{sx,sy});visited[sx,sy]=true;bool outside=false;int x0=sx,x1=sx,y0=sy,y1=sy,cells=0;
                while(queue.Count>0){var cell=queue.Dequeue();int x=cell[0],y=cell[1];cells++;x0=Math.Min(x0,x);x1=Math.Max(x1,x);y0=Math.Min(y0,y);y1=Math.Max(y1,y);outside|=x==0||y==0||x==nx-1||y==ny-1;
                    for(int d=0;d<4;d++){int xx=x+dx[d],yy=y+dy[d];if(xx<0||yy<0||xx>=nx||yy>=ny||blocked[xx,yy]||visited[xx,yy])continue;visited[xx,yy]=true;queue.Enqueue(new[]{xx,yy});}
                }
                if(outside)continue;
                if(cells!=(x1-x0+1)*(y1-y0+1))throw new ArgumentException("发现非矩形内框口，请分组选取或使用手动模式。");
                double left=xs[x0],right=xs[x1+1],bottom=ys[y0],top=ys[y1+1];
                V3 seed=u*((left+right)/2)+v*((bottom+top)/2)+n*depth;
                // The envelope locates a framework opening only. Its flanges are NOT the
                // filling boundary: rays in the middle section find the first actual material.
                left=seed.Dot(u)-SectionBoundary.Distance(members,seed,u*(-1));
                right=seed.Dot(u)+SectionBoundary.Distance(members,seed,u);
                bottom=seed.Dot(v)-SectionBoundary.Distance(members,seed,v*(-1));
                top=seed.Dot(v)+SectionBoundary.Distance(members,seed,v);
                var opening=new FrameOpening{MidPoint=seed,Planes=new[]{new PlaneInput(u*left,u,"中心剖面左边界"),new PlaneInput(u*right,u,"中心剖面右边界"),new PlaneInput(v*bottom,v,"中心剖面下边界"),new PlaneInput(v*top,v,"中心剖面上边界")}};
                if(members.Any(m=>m.Triangles.Count>0)){
                    if(SectionBoundary.Intersects(members,opening.Solve(thickness,gap)))throw new ArgumentException("中心剖面边界已找到，但此厚度的矩形板会进入型材材料（槽宽不足或角部干涉）。请减小板厚或增加间隙后重试。");
                }else{
                    Support(members,u,v,n,left,bottom,top,depth,thickness);Support(members,u,v,n,right,bottom,top,depth,thickness);
                    Support(members,v,u,n,bottom,left,right,depth,thickness);Support(members,v,u,n,top,left,right,depth,thickness);
                }
                result.Add(opening);
            }
            if(result.Count==0)throw new ArgumentException("没有发现闭合矩形框口。请检查是否漏选型材、角部未闭合或存在斜边。");
            return result;
        }
        static void Support(IList<FrameMember> members,V3 normal,V3 along,V3 depthAxis,double offset,double start,double end,double depth,double thickness){
            var faces=members.SelectMany(m=>m.Faces).Where(f=>f.Plane.Normal.Cross(normal).Length<PanelGeometry.AngularTolerance&&Math.Abs(f.Plane.Point.Dot(normal)-offset)<Tol).ToList();
            double lo=depth-thickness/2,hi=depth+thickness/2;
            var cuts=Unique(faces.SelectMany(f=>f.Edges).SelectMany(e=>e).Select(p=>p.Dot(depthAxis)).Where(z=>z>lo&&z<hi).Concat(new[]{lo,hi}));
            var samples=new List<double>{lo+1e-10,hi-1e-10};for(int i=0;i+1<cuts.Count;i++){samples.Add((cuts[i]+cuts[i+1])/2);samples.Add(cuts[i]+1e-10);samples.Add(cuts[i+1]-1e-10);}
            foreach(double z in samples){
                var intervals=new List<double[]>();
                foreach(var f in faces){var intersections=new List<double>();foreach(var edge in f.Edges)for(int i=0;i+1<edge.Length;i++){
                    var a=edge[i];var b=edge[i+1];double za=a.Dot(depthAxis),zb=b.Dot(depthAxis);
                    if((za<=z&&zb>z)||(zb<=z&&za>z))intersections.Add((a+(b-a)*((z-za)/(zb-za))).Dot(along));
                }
                    intersections.Sort();if(intersections.Count%2!=0)throw new ArgumentException("型材边界拓扑无法可靠解析，请使用手动模式。");
                    for(int i=0;i+1<intersections.Count;i+=2)intervals.Add(new[]{intersections[i],intersections[i+1]});
                }
                double covered=start;foreach(var interval in intervals.OrderBy(i=>i[0])){if(interval[1]<covered)continue;if(interval[0]>covered+Tol)break;covered=Math.Max(covered,interval[1]);}
                if(covered<end-Tol)throw new ArgumentException("候选框口的实际内侧平面未连续闭合，或板厚进入槽口/圆角。请缩小厚度、重新分组选取，或使用手动选四面。");
            }
        }
    }
}
