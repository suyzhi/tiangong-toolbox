using System;
using System.IO;
using System.Text;
using System.Linq;
using System.Collections.Generic;
using G=SolidEdgeGeometry;
namespace TianGongCadSuite {
    public sealed class TrainingGeometryReader {
        readonly string output,relative,id;
        readonly List<object> issues=new List<object>();
        static Dictionary<string,object> O(params object[] p){return TrainingExporter.Obj(p);}
        public TrainingGeometryReader(string root,string file,string bodyId){output=root;relative=file;id=bodyId;}
        object Read(Dictionary<string,object> row,string key,Func<object> get){
            var status=(Dictionary<string,object>)row["availability"];
            try{object value=get();row[key]=value;status[key]=value==null?"unsupported":"available";return value;}
            catch(Exception e){row[key]=null;status[key]="failed";issues.Add(O("location",(row.ContainsKey("id")?row["id"]:id)+"/"+key,"error",e.Message,"hresult",e.HResult));return null;}
        }
        static Dictionary<string,object> Row(params object[] p){var r=O(p);r["availability"]=new Dictionary<string,object>();return r;}
        static double[] V(Array a){return a.Cast<object>().Select(Convert.ToDouble).ToArray();}
        static string Key(dynamic e){Array k=new byte[0];object n=0;e.GetReferenceKey(ref k,ref n);return Convert.ToBase64String(k.Cast<object>().Select(Convert.ToByte).ToArray());}
        static string Kind(object geom){string n=((G.GNTTypePropertyConstants)Convert.ToInt32(((dynamic)geom).Type)).ToString();return n.StartsWith("ig")?n.Substring(2).ToLowerInvariant():n;}
        static double Norm(double[] p){return Math.Sqrt(p.Sum(x=>x*x));}
        public Dictionary<string,object> Export(G.Body b){
            var r=Row("id",id,"issues",issues);Read(r,"reference_key",()=>Key(b));Read(r,"aabb_m",()=>{Array a=new double[3],c=new double[3];b.GetExactRange(ref a,ref c);return new[]{V(a),V(c)};});
            var fs=new List<G.Face>();var es=new List<G.Edge>();
            Read(r,"face_enumeration",()=>{dynamic faces=b.Faces[G.FeatureTopologyQueryTypeConstants.igQueryAll];for(int i=1;i<=(int)faces.Count;i++)fs.Add((G.Face)faces.Item(i));return fs.Count;});
            Read(r,"edge_enumeration",()=>{dynamic nativeEdges=b.Edges[G.FeatureTopologyQueryTypeConstants.igQueryAll];for(int i=1;i<=(int)nativeEdges.Count;i++)es.Add((G.Edge)nativeEdges.Item(i));return es.Count;});
            var faceIds=fs.Select((f,i)=>new{f.ID,Id=id+"/face-"+(i+1)}).ToDictionary(f=>f.ID,f=>f.Id);
            var edgeIds=es.Select((e,i)=>new{e.ID,Id=id+"/edge-"+(i+1)}).ToDictionary(e=>e.ID,e=>e.Id);
            var vertices=new List<object>();var vertexIds=new Dictionary<int,string>();
            Func<G.Vertex,string> vertex=v=>{if(v==null)return null;string name;if(vertexIds.TryGetValue(v.ID,out name))return name;name=id+"/vertex-"+(vertices.Count+1);vertexIds.Add(v.ID,name);var vr=Row("id",name,"native_id",v.ID);vertices.Add(vr);Read(vr,"reference_key",()=>Key(v));Read(vr,"point_m",()=>{Array p=new double[3];v.GetPointData(ref p);return V(p);});return name;};
            var edges=new List<object>();var relations=new List<object>();var adjacency=new List<object>();var facesOut=new List<object>();var loops=new List<object>();
            var edgeFaces=es.ToDictionary(e=>e.ID,e=>new List<string>());
            foreach(var f in fs){var fr=Face(f,faceIds[f.ID]);facesOut.Add(fr);
                Read(fr,"edge_ids",()=>{dynamic fe=f.Edges;var ids=new List<string>();for(int j=1;j<=(int)fe.Count;j++){int eid=((G.Edge)fe.Item(j)).ID;if(!edgeIds.ContainsKey(eid))throw new InvalidOperationException("Face references an unenumerated edge");ids.Add(edgeIds[eid]);if(!edgeFaces[eid].Contains(faceIds[f.ID]))edgeFaces[eid].Add(faceIds[f.ID]);}return ids;});
                Read(fr,"edge_reference_keys",()=>{dynamic fe=f.Edges;var keys=new List<string>();for(int j=1;j<=(int)fe.Count;j++)keys.Add(Key(fe.Item(j)));return keys;});
                Read(fr,"loop_ids",()=>{dynamic ls=f.Loops;var ids=new List<string>();for(int j=1;j<=(int)ls.Count;j++){G.Loop l=(G.Loop)ls.Item(j);string lid=faceIds[f.ID]+"/loop-"+j;var lr=Row("id",lid,"face_id",faceIds[f.ID],"is_outer",l.IsOuterLoop,"order_semantics","SDK loop edge collection; orientation not inferred");loops.Add(lr);ids.Add(lid);Read(lr,"edge_ids",()=>{dynamic le=l.Edges;var list=new List<string>();for(int k=1;k<=(int)le.Count;k++)list.Add(edgeIds[((G.Edge)le.Item(k)).ID]);return list;});}return ids;});
            }
            foreach(var e in es){var er=Edge(e,edgeIds[e.ID]);edges.Add(er);er["adjacent_face_ids"]=edgeFaces[e.ID];Read(er,"vertex_ids",()=>new[]{vertex((G.Vertex)e.StartVertex),vertex((G.Vertex)e.EndVertex)});
                relations.Add(O("edge_id",edgeIds[e.ID],"face_ids",edgeFaces[e.ID]));var af=edgeFaces[e.ID];for(int i=0;i<af.Count;i++)for(int j=i+1;j<af.Count;j++)adjacency.Add(O("face_a",af[i],"face_b",af[j],"shared_edge",edgeIds[e.ID]));
            }
            foreach(Dictionary<string,object> fr in facesOut){string fid=(string)fr["id"];fr["adjacent_face_ids"]=edgeFaces.Values.Where(list=>list.Contains(fid)).SelectMany(list=>list).Where(x=>x!=fid).Distinct().ToArray();}
            r["faces"]=facesOut;r["edges"]=edges;r["topology"]=O("vertices",vertices,"face_adjacency",adjacency,"edge_face_relations",relations,"loops",loops,"geometric_relations",null,"geometric_relations_status","unsupported");
            Read(r,"mesh",()=>Mesh(fs,faceIds));r["status"]=issues.Count==0?"success":"partial";return r;
        }
        Dictionary<string,object> Face(G.Face face,string fid){
            var r=Row("id",fid,"native_id",face.ID,"centroid_m",null,"centroid_status","unsupported_exact_area_centroid");
            Read(r,"reference_key",()=>Key(face));Read(r,"area_m2",()=>face.Area);Read(r,"aabb_m",()=>{Array a=new double[3],b=new double[3];face.GetExactRange(ref a,ref b);return new[]{V(a),V(b)};});
            Read(r,"native_center_m",()=>{Array a=new double[3];face.GetCenter(ref a);return V(a);});
            Read(r,"parameter_range",()=>{Array a=new double[2],b=new double[2];face.GetParamRange(ref a,ref b);return new[]{V(a),V(b)};});
            Read(r,"normal_sample",()=>{Array low=new double[2],high=new double[2];face.GetParamRange(ref low,ref high);Array uv=new[]{(Convert.ToDouble(low.GetValue(0))+Convert.ToDouble(high.GetValue(0)))/2,(Convert.ToDouble(low.GetValue(1))+Convert.ToDouble(high.GetValue(1)))/2},on=new bool[1];face.GetParamOnFace(1,ref uv,ref on);if(!Convert.ToBoolean(on.GetValue(0)))return null;Array n=new double[3],p=new double[3];face.GetNormal(1,ref uv,ref n);face.GetPointAtParam(1,ref uv,ref p);return O("normal",V(n),"point_m",V(p),"uv",V(uv),"source","Face.GetNormal");});
            Read(r,"geometry",()=>Surface(face.Geometry));r["surface_type"]=Read(r,"surface_type_native",()=>((G.GNTTypePropertyConstants)Convert.ToInt32(((dynamic)face.Geometry).Type)).ToString());r["surface_kind"]=Kind(face.Geometry);r["normal"]=null;
            var g=r["geometry"] as Dictionary<string,object>;if(g!=null&&g.ContainsKey("normal"))r["normal"]=g["normal"];
            r["geometry_status"]=g==null?O("status","failed"):g["availability"];return r;
        }
        Dictionary<string,object> Surface(object shape){
            var r=Row("type",Kind(shape));
            if(shape is G.Plane)Read(r,"plane",()=>{Array p=new double[3],n=new double[3];((G.Plane)shape).GetPlaneData(ref p,ref n);r["origin_m"]=V(p);r["normal"]=V(n);return true;});
            else if(shape is G.Cylinder)Read(r,"cylinder",()=>{Array p=new double[3],a=new double[3];double radius;((G.Cylinder)shape).GetCylinderData(ref p,ref a,out radius);r["origin_m"]=V(p);r["axis"]=V(a);r["radius_m"]=radius;Read(r,"height_m",()=>null);return true;});
            else if(shape is G.Cone)Read(r,"cone",()=>{Array p=new double[3],a=new double[3];double radius,angle;bool expanding;((G.Cone)shape).GetConeData(ref p,ref a,out radius,out angle,out expanding);r["origin_m"]=V(p);r["axis"]=V(a);r["radius_m"]=radius;r["half_angle_rad"]=angle;r["expanding"]=expanding;Read(r,"apex_m",()=>null);return true;});
            else if(shape is G.Sphere)Read(r,"sphere",()=>{Array p=new double[3];double radius;((G.Sphere)shape).GetSphereData(ref p,out radius);r["center_m"]=V(p);r["radius_m"]=radius;return true;});
            else if(shape is G.Torus)Read(r,"torus",()=>{Array p=new double[3],a=new double[3];double major,minor;((G.Torus)shape).GetTorusData(ref p,ref a,out major,out minor);r["center_m"]=V(p);r["axis"]=V(a);r["major_radius_m"]=major;r["minor_radius_m"]=minor;return true;});
            else if(shape is G.BSplineSurface)Read(r,"bspline",()=>{var s=(G.BSplineSurface)shape;Array order=new int[2],poles=new int[2],knots=new int[2],closed=new bool[2],periodic=new bool[2];bool rational,planar;s.GetBSplineInfo(ref order,ref poles,ref knots,out rational,ref closed,ref periodic,out planar);int n=Convert.ToInt32(poles.GetValue(0))*Convert.ToInt32(poles.GetValue(1)),nu=Convert.ToInt32(knots.GetValue(0)),nv=Convert.ToInt32(knots.GetValue(1));Array cp=new double[n*3],ku=new double[nu],kv=new double[nv],w=new double[rational?n:0];s.GetBSplineData(n,nu,nv,rational?n:0,ref cp,ref ku,ref kv,ref w);r["degree_u"]=Convert.ToInt32(order.GetValue(0))-1;r["degree_v"]=Convert.ToInt32(order.GetValue(1))-1;r["control_point_shape"]=poles;r["control_points_m"]=cp;r["knots_u"]=ku;r["knots_v"]=kv;r["weights"]=rational?w:null;r["rational"]=rational;r["periodic"]=periodic;r["closed"]=closed;return true;});
            else Read(r,"parameters",()=>null);
            foreach(var k in r.Keys.ToArray())if(k!="availability"&&!((Dictionary<string,object>)r["availability"]).ContainsKey(k))((Dictionary<string,object>)r["availability"])[k]=r[k]==null?"unsupported":"available";
            return r;
        }
        Dictionary<string,object> Edge(G.Edge edge,string eid){
            var r=Row("id",eid,"native_id",edge.ID);Read(r,"reference_key",()=>Key(edge));Read(r,"closed",()=>edge.IsClosed);Read(r,"endpoints_m",()=>{Array a=new double[3],b=new double[3];edge.GetEndPoints(ref a,ref b);r["start_m"]=V(a);r["end_m"]=V(b);return new[]{V(a),V(b)};});
            Read(r,"parameter_range",()=>{double a,b;edge.GetParamExtents(out a,out b);return new[]{a,b};});Read(r,"length_m",()=>{double a,b,length;edge.GetParamExtents(out a,out b);edge.GetLengthAtParam(a,b,out length);return length;});
            r["curve_type"]=Read(r,"curve_type_native",()=>((G.GNTTypePropertyConstants)Convert.ToInt32(((dynamic)edge.Geometry).Type)).ToString());r["curve_kind"]=Kind(edge.Geometry);Read(r,"geometry",()=>Curve(edge.Geometry,edge.IsClosed));var g=r["geometry"] as Dictionary<string,object>;r["geometry_status"]=g==null?O("status","failed"):g["availability"];return r;
        }
        Dictionary<string,object> Curve(object shape,bool closed){
            var r=Row("type",Kind(shape));
            if(shape is G.Line)Read(r,"line",()=>{Array p=new double[3],d=new double[3];((G.Line)shape).GetLineData(ref p,ref d);r["origin_m"]=V(p);var v=V(d);double length=Norm(v);r["direction"]=length>0?v.Select(x=>x/length).ToArray():null;return true;});
            else if(shape is G.Circle)Read(r,"circle",()=>{Array p=new double[3],a=new double[3];double radius;((G.Circle)shape).GetCircleData(ref p,ref a,out radius);r["center_m"]=V(p);r["normal"]=V(a);r["radius_m"]=radius;r["full_circle"]=closed;Read(r,"start_angle_rad",()=>null);Read(r,"end_angle_rad",()=>null);return true;});
            else if(shape is G.Ellipse)Read(r,"ellipse",()=>{Array p=new double[3],a=new double[3],major=new double[3];double ratio;((G.Ellipse)shape).GetEllipseData(ref p,ref a,ref major,out ratio);r["center_m"]=V(p);r["normal"]=V(a);r["major_axis_vector_m"]=V(major);r["major_radius_m"]=Norm(V(major));r["minor_radius_m"]=Norm(V(major))*ratio;return true;});
            else if(shape is G.BSplineCurve)Read(r,"bspline",()=>{var s=(G.BSplineCurve)shape;int order,n,nk;bool rational,c,periodic,planar;Array plane=new double[3];s.GetBSplineInfo(out order,out n,out nk,out rational,out c,out periodic,out planar,ref plane);Array poles=new double[n*3],knots=new double[nk],w=new double[rational?n:0];s.GetBSplineData(n,nk,rational?n:0,ref poles,ref knots,ref w);r["degree"]=order-1;r["control_points_m"]=poles;r["knots"]=knots;r["weights"]=rational?w:null;r["rational"]=rational;r["periodic"]=periodic;return true;});else Read(r,"parameters",()=>null);
            foreach(var k in r.Keys.ToArray())if(k!="availability"&&!((Dictionary<string,object>)r["availability"]).ContainsKey(k))((Dictionary<string,object>)r["availability"])[k]=r[k]==null?"unsupported":"available";return r;
        }
        object Mesh(List<G.Face> faces,Dictionary<int,string> faceIds){
            var mapping=new List<string>();var failed=new List<string>();int count=0;string path=Path.Combine(output,relative);Directory.CreateDirectory(Path.GetDirectoryName(path));
            using(var w=new StreamWriter(path,false,new UTF8Encoding(false))){w.WriteLine("# unit=m; source=Face.GetFacetData; chord_tolerance_m=0.00005");foreach(var f in faces){int n;Array points=new double[0];object normals,uv;
                try{f.GetFacetData(0.00005,out n,ref points,out normals,out uv);if(n<0||points.Length!=n*9)throw new InvalidOperationException("Unexpected per-face facet layout");var values=V(points);if(values.Any(x=>double.IsNaN(x)||double.IsInfinity(x)))throw new InvalidOperationException("Non-finite facet coordinate");w.WriteLine("g "+faceIds[f.ID].Replace('/','_'));for(int i=0;i<values.Length;i+=3)w.WriteLine("v "+N(values[i])+" "+N(values[i+1])+" "+N(values[i+2]));for(int i=0;i<n;i++){int v=(count+i)*3+1;w.WriteLine("f "+v+" "+(v+1)+" "+(v+2));mapping.Add(faceIds[f.ID]);}count+=n;
                }catch(Exception e){failed.Add(faceIds[f.ID]);issues.Add(O("location",faceIds[f.ID]+"/tessellation","error",e.Message,"hresult",e.HResult));}
            }}
            return O("file",relative,"triangle_count",count,"triangle_face_ids",mapping,"native_face_ids",mapping,"mapping_source","native_per_face_tessellation","face_id_mapping_status",failed.Count==0?"success":"partial","failed_face_ids",failed,"chord_tolerance_m",0.00005);
        }
        static string N(double d){return d.ToString("R",System.Globalization.CultureInfo.InvariantCulture);}
    }
}
