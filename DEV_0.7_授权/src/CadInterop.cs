using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Linq;
using F=SolidEdgeFramework;
using A=SolidEdgeAssembly;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;
using S=SolidEdgeFrameworkSupport;
namespace TianGongCadSuite {
    public sealed class Connection : IDisposable {
        IConnectionPoint point; int cookie;object source;
        public Connection(object source,Type eventInterface,object sink){this.source=source;Guid id=eventInterface.GUID;((IConnectionPointContainer)source).FindConnectionPoint(ref id,out point);point.Advise(sink,out cookie);}
        public void Dispose(){if(point!=null){try{point.Unadvise(cookie);}catch(COMException){}point=null;}GC.KeepAlive(source);source=null;}
    }
    [ComImport,Guid("00000016-0000-0000-C000-000000000046"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IOleMessageFilter {
        [PreserveSig]int HandleInComingCall(int type,IntPtr task,int ticks,IntPtr info);
        [PreserveSig]int RetryRejectedCall(IntPtr task,int ticks,int reject);
        [PreserveSig]int MessagePending(IntPtr task,int ticks,int pending);
    }
    public sealed class OleFilter : IOleMessageFilter,IDisposable {
        IOleMessageFilter old;
        [DllImport("ole32.dll")]static extern int CoRegisterMessageFilter(IOleMessageFilter filter,out IOleMessageFilter old);
        public OleFilter(){CoRegisterMessageFilter(this,out old);}
        public int HandleInComingCall(int a,IntPtr b,int c,IntPtr d){return 0;}
        public int RetryRejectedCall(IntPtr a,int ticks,int reject){return reject==2&&ticks<10000?100:-1;}
        public int MessagePending(IntPtr a,int b,int c){return 2;}
        public void Dispose(){IOleMessageFilter ignored;CoRegisterMessageFilter(old,out ignored);}
    }
    public sealed class PickGeometry {
        public object Selection,Geometry;
        public Transform Transform;
        internal static string Describe(object value){
            if(value==null)return "null";
            string result=value.GetType().FullName;
            foreach(var type in new[]{typeof(F.Reference),typeof(A.TopologyReference),typeof(A.Occurrence),typeof(A.SubOccurrence),typeof(G.Face),typeof(G.Plane),typeof(G.Edge),typeof(G.Vertex),typeof(G.Body),typeof(P.RefPlane)})if(type.IsInstanceOfType(value))result+=" | "+type.FullName;
            try{result+=" Type="+((dynamic)value).Type;}catch{}
            try{object inner=null;if(value is F.Reference)inner=((F.Reference)value).Object;else if(value is A.TopologyReference)inner=((A.TopologyReference)value).Object;if(inner!=null&&!ReferenceEquals(inner,value))result+=" -> "+DescribeLeaf(inner);}catch(Exception e){result+=" unwrap: "+e.Message;}
            return result;
        }
        static string DescribeLeaf(object value){string s=value.GetType().FullName;foreach(var t in typeof(G.Face).Assembly.GetTypes().Where(t=>t.IsInterface&&t.IsImport&&(t.Namespace=="SolidEdgeGeometry"||t.Namespace=="SolidEdgeFramework"||t.Namespace=="SolidEdgeAssembly")))try{if(t.IsInstanceOfType(value))s+=" | "+t.FullName;}catch{}try{s+=" Type="+((dynamic)value).Type;}catch{}return s;}
        public static PickGeometry Unwrap(object selection){
            var r=selection as F.Reference; var t=selection as A.TopologyReference;
            Array matrix=new double[16];object geometry;
            if(r!=null){r.GetMatrix(ref matrix);geometry=r.Object;}
            else if(t!=null){t.GetMatrix(ref matrix);geometry=t.Object;}
            else {geometry=selection;matrix=Transform.Identity.M;}
            return new PickGeometry{Selection=selection,Geometry=geometry,Transform=new Transform(matrix)};
        }
        public PlaneInput Plane(){
            var face=Geometry as G.Face;if(face==null)throw new ArgumentException("请选择型材上的平面面片。");
            var plane=face.Geometry as G.Plane;if(plane==null)throw new ArgumentException("选择的是曲面，请改选平面。");
            Array p=new double[3],n=new double[3];plane.GetPlaneData(ref p,ref n);
            return new PlaneInput(Transform.Point(V3.From(p)),Transform.Normal(V3.From(n)),"平面 "+face.ID);
        }
        public V3 Point(){
            Array p=new double[3];var vertex=Geometry as G.Vertex;var edge=Geometry as G.Edge;var rp=Geometry as P.RefPoint;var arp=Geometry as A.AsmRefPoint;
            if(vertex!=null)vertex.GetPointData(ref p);
            else if(rp!=null)rp.GetPoint(ref p);
            else if(arp!=null)arp.GetPoint(ref p);
            else if(edge!=null){
                var circle=edge.Geometry as G.Circle;
                if(circle!=null)circle.GetCenterPoint(ref p);
                else if(edge.Geometry is G.Line)edge.GetMiddlePoint(ref p);
                else throw new ArgumentException("定位边仅支持直线中点或圆/圆弧中心。");
            } else throw new ArgumentException("请选择顶点、基准点、直线边或圆形边。");
            return Transform.Point(V3.From(p));
        }
        public System.Collections.Generic.List<V3[]> Outline(){
            var result=new System.Collections.Generic.List<V3[]>();var face=Geometry as G.Face;if(face==null)return result;
            foreach(G.Edge edge in (G.Edges)face.Edges){Array data=new double[0],parameters=new double[0];int count;edge.GetStrokeData(.0001,out count,ref data,ref parameters);var pts=new System.Collections.Generic.List<V3>();for(int i=0;i+2<data.Length;i+=3)pts.Add(Transform.Point(new V3(Convert.ToDouble(data.GetValue(i)),Convert.ToDouble(data.GetValue(i+1)),Convert.ToDouble(data.GetValue(i+2)))));if(pts.Count>=2)result.Add(pts.ToArray());}
            return result;
        }
    }
    public static class CadBuilder {
        public static string TemplatePath {get{return Path.Combine(CadInstallRoot,"Template","ISO Metric","iso metric part.par");}}
        // 天工装在哪：原来写死 C:\Program Files\NDS\TianGong 2025 —— 同事把天工装在 D 盘或别的目录时，型材自动填充找不到零件模板、一块板都生成不了。
        // 现在：环境变量 TIANGONG_HOME → 插件所在的 CAD 进程自己的位置（…\Program\TianGong.exe 的上一级）→ 默认目录
        public static string CadInstallRoot {get{
            string env=Environment.GetEnvironmentVariable("TIANGONG_HOME");
            if(!string.IsNullOrEmpty(env))return env;
            try{
                string exe=System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
                if(string.Equals(Path.GetFileName(exe),"TianGong.exe",StringComparison.OrdinalIgnoreCase)){
                    var root=Path.GetDirectoryName(Path.GetDirectoryName(exe));
                    if(!string.IsNullOrEmpty(root)&&Directory.Exists(Path.Combine(root,"Template")))return root;
                }
            }catch{}
            return @"C:\Program Files\NDS\TianGong 2025";
        }}
        public static P.PartDocument CreatePart(F.Application app,PanelSpec s){return CreatePart(app,s,null);}
        // props 不为空：把尺寸、材质写进零件文件（BOM、工程图明细栏直接能引用），写不上的记进 props.Problems，不影响建模。
        public static P.PartDocument CreatePart(F.Application app,PanelSpec s,PanelProps props){
            TianGongCadSuite.Licensing.LicenseGate.Require();
            P.PartDocument part=null;
            try {
                // 模板不在（天工装的是别的语言 / 模板目录被整理过）：用 CAD 自己的默认零件模板，别让整批板子都生成失败（接口一律按米，模板单位不影响尺寸）
                part=(P.PartDocument)(File.Exists(TemplatePath)?app.Documents.Add("SolidEdge.PartDocument",TemplatePath):app.Documents.Add("SolidEdge.PartDocument"));
                part.ModelingMode=P.ModelingModeConstants.seModelingModeOrdered;
                // Build the profile in the native XY plane; occurrence placement maps it to the picked mid-plane.
                P.RefPlane plane=null;
                foreach(P.RefPlane candidate in part.RefPlanes){Array n=new double[3],p=new double[3],u=new double[3];candidate.GetNormal(ref n);candidate.GetRootPoint(ref p);candidate.GetReferenceDirection(ref u);if(Math.Abs(V3.From(n).Z-1)<1e-8&&V3.From(p).Length<1e-8&&Math.Abs(V3.From(u).X-1)<1e-8){plane=candidate;break;}}
                if(plane==null)throw new InvalidOperationException("零件模板缺少标准 XY 基准面。");
                var profile=part.ProfileSets.Add().Profiles.Add(plane);
                profile.Name="矩形板轮廓";
                var lines=new S.Line2d[4];
                lines[0]=profile.Lines2d.AddBy2Points(0,0,s.Width,0);lines[1]=profile.Lines2d.AddBy2Points(s.Width,0,s.Width,s.Height);
                lines[2]=profile.Lines2d.AddBy2Points(s.Width,s.Height,0,s.Height);lines[3]=profile.Lines2d.AddBy2Points(0,s.Height,0,0);
                var rel=(S.Relations2d)profile.Relations2d;
                for(int i=0;i<4;i++){rel.AddKeypoint(lines[i],(int)SolidEdgeConstants.KeypointIndexConstants.igLineEnd,lines[(i+1)%4],(int)SolidEdgeConstants.KeypointIndexConstants.igLineStart);if(i%2==0)rel.AddHorizontal(lines[i]);else rel.AddVertical(lines[i]);}
                rel.AddKeypointFix(lines[0],(int)SolidEdgeConstants.KeypointIndexConstants.igLineStart);
                var dims=(S.Dimensions)profile.Dimensions;dims.Constraint=true;dims.AddLength(lines[0]);dims.AddLength(lines[1]);
                if(profile.End(P.ProfileValidationType.igProfileClosed)!=0)throw new InvalidOperationException("矩形轮廓未通过闭合检查。");
                Array profiles=new object[]{profile};
                var model=part.Models.AddFiniteExtrudedProtrusion(1,ref profiles,P.FeaturePropertyConstants.igSymmetric,s.Thickness);
                model.ExtrudedProtrusions.Item(1).Name="板厚（对称）";
                profile.Visible=false;
                CheckBody((G.Body)model.Body,s.Width,s.Height,s.Thickness);
                if(props!=null)WriteProps(app,part,s,props);
                return part;
            } catch {if(part!=null)try{part.Close(false);}catch{}throw;}
        }
        // 尺寸/材质写进文件属性。自定义属性用 Properties.Item("Custom").Add —— Solid Edge 的标准接口；
        // 材质用材料表 ApplyMaterial（材料库里要有这个名字）。两样都没在天工上单独验证过，所以失败只记一笔。
        static void WriteProps(F.Application app,P.PartDocument part,PanelSpec s,PanelProps props){
            var ci=System.Globalization.CultureInfo.InvariantCulture;
            // 属性集合按接口类型写（PropCardCad.DocCustom / PutDoc）：原来用 dynamic —— 文档属性集合用 dynamic 调 Add 报
            // DISP_E_NOTACOLLECTION（0.8.0 真机），而且 dynamic 按接口类型缓存调用方式，会连累同一进程里的属性卡填写（0.8.2）。
            try{
                var w=new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string,string>>();
                System.Action<string,string> put=(k,v)=>w.Add(new System.Collections.Generic.KeyValuePair<string,string>(k,v));
                put("宽度mm",(s.Width*1000).ToString("0.###",ci));
                put("高度mm",(s.Height*1000).ToString("0.###",ci));
                put("厚度mm",(s.Thickness*1000).ToString("0.###",ci));
                put("每边间隙mm",(s.Gap*1000).ToString("0.###",ci));
                put("规格",(s.Width*1000).ToString("0.#",ci)+"×"+(s.Height*1000).ToString("0.#",ci)+"×"+(s.Thickness*1000).ToString("0.#",ci));
                if(!string.IsNullOrEmpty(props.Material))put("材质",props.Material);
                put("来源","天工工具箱 型材自动填充");
                PropCardCad.PutDoc(PropCardCad.DocCustom(part),w,null);
            }catch(Exception e){Log.Write("PanelProps",e);props.Problem("尺寸没能写进零件的自定义属性："+e.Message);}
            if(string.IsNullOrEmpty(props.Material))return;
            try{dynamic table=((dynamic)app).GetMaterialTable();table.ApplyMaterial(part,props.Material);}
            catch(Exception e){Log.Write("PanelMaterial",e);props.Problem("材质「"+props.Material+"」没能套用材料库（材料库里可能没有这个名字），已写进自定义属性「材质」。");}
        }
        public static void CheckBody(G.Body body,double w,double h,double t){
            Array min=new double[3],max=new double[3];body.GetRange(ref min,ref max);var lo=V3.From(min);var hi=V3.From(max);
            const double tol=1e-7;
            if(Math.Abs(lo.X)>tol||Math.Abs(lo.Y)>tol||Math.Abs(lo.Z+t/2)>tol||Math.Abs(hi.X-w)>tol||Math.Abs(hi.Y-h)>tol||Math.Abs(hi.Z-t/2)>tol)throw new InvalidOperationException("生成实体的尺寸或对称位置未通过检查："+lo+" → "+hi);
        }
        public static A.Occurrence Generate(F.Application app,A.AssemblyDocument assembly,PanelSpec spec,string filename){return Generate(app,assembly,spec,filename,null);}
        public static A.Occurrence Generate(F.Application app,A.AssemblyDocument assembly,PanelSpec spec,string filename,PanelProps props){
            if(assembly==null||assembly.ReadOnly||assembly.InPlaceActivated)throw new InvalidOperationException("请在可编辑的装配顶层运行命令，先退出原位编辑。");
            string path=Path.GetFullPath(filename);
            if(!string.Equals(Path.GetExtension(path),".par",StringComparison.OrdinalIgnoreCase))throw new ArgumentException("输出文件必须是 .par。");
            if(File.Exists(path)||Directory.Exists(path))throw new IOException("目标已存在，请更换文件名。");
            if(!Directory.Exists(Path.GetDirectoryName(path)))throw new DirectoryNotFoundException("目标文件夹不存在。");
            string staging=Path.Combine(Path.GetDirectoryName(path),".panel-"+Guid.NewGuid().ToString("N")+".par");
            P.PartDocument part=null;A.Occurrence occurrence=null;bool moved=false;
            try {
                part=CreatePart(app,spec,props);part.SaveAs(staging);part.Close(false);part=null;
                // Atomic, no-overwrite publication. A competing file is never replaced.
                File.Move(staging,path);moved=true;
                assembly.Activate();occurrence=assembly.Occurrences.AddByFilename(path);
                Array matrix=spec.Placement.M;occurrence.PutMatrix(ref matrix,true);
                if(((dynamic)occurrence.Relations3d).Count==0)assembly.Relations3d.AddGround(occurrence);
                Array actual=new double[16];occurrence.GetMatrix(ref actual);
                if(actual.Cast<object>().Select(Convert.ToDouble).Where((v,i)=>Math.Abs(v-spec.Placement.M[i])>1e-8).Any())throw new InvalidOperationException("板子的装配位置未通过检查。");
                // 位置对不代表零件对：CAD 插实例时按“文件名”查找文件，装配目录里若有同名零件，
                // 插进来的会是那个旧零件（位置是新算的，几何是旧的）。必须核对实例真正绑定的文档路径。
                // 这条也必须留作护栏，用户手动命名（生成矩形板）时会踩到同一件事。
                string bound=null;
                try{var document=occurrence.OccurrenceDocument as F.SolidEdgeDocument;if(document!=null)bound=document.FullName;}catch{}
                if(bound==null||!string.Equals(Path.GetFullPath(bound),path,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("天工 CAD 没有使用刚生成的板子，而是按文件名找到了另一个同名零件：\n"+(bound==null?"（读不到该实例的文档路径）":bound)+"\n装配所在目录里如果有同名零件，CAD 会优先用它。请更换输出目录，或给板子换一个不重名的文件名。\n（本批已插入的板子与刚生成的 PAR 会被撤回。）");
                return occurrence;
            } catch(Exception original) {
                string cleanup="";bool detached=occurrence==null;
                if(occurrence!=null)try{occurrence.Delete();detached=true;}catch(Exception e){cleanup+="；装配引用清理失败："+e.Message;}
                if(part!=null)try{part.Close(false);}catch(Exception e){cleanup+="；临时文档关闭失败："+e.Message;}
                try{if(File.Exists(staging))File.Delete(staging);if(moved&&detached&&File.Exists(path))File.Delete(path);}catch(Exception e){cleanup+="；临时文件清理失败："+e.Message;}
                throw new InvalidOperationException(original.Message+cleanup,original);
            } finally {try{assembly.Activate();}catch{}}
        }
    }
}
