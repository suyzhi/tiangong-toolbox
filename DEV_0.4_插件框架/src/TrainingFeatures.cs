using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using P=SolidEdgePart;
using F=SolidEdgeFramework;
using S=SolidEdgeFrameworkSupport;
using G=SolidEdgeGeometry;
namespace TianGongCadSuite {
    public sealed class TrainingFeatureReader {
        readonly object document;readonly string modelId;readonly Dictionary<string,object> model;
        readonly List<object> issues=new List<object>(),features=new List<object>(),profiles=new List<object>();
        readonly Dictionary<long,string> ids=new Dictionary<long,string>();readonly Dictionary<long,object> retained=new Dictionary<long,object>();
        readonly List<Tuple<object,Type,Dictionary<string,object>>> nodes=new List<Tuple<object,Type,Dictionary<string,object>>>();
        public TrainingFeatureReader(object doc,string id,Dictionary<string,object> m){document=doc;modelId=id;model=m;}
        static Dictionary<string,object> O(params object[] p){return TrainingExporter.Obj(p);}
        object Read(string path,Func<object> get){return TrainingNative.Read(issues,path,get);}
        void Register(object obj,string id){long key=TrainingNative.Identity(obj);ids[key]=id;retained[key]=obj;}
        object Link(object obj){if(obj==null)return null;string id;if(ids.TryGetValue(TrainingNative.Identity(obj),out id))return O("id",id,"status","success");return O("id",null,"status","unsupported_external_node");}
        Type FeatureInterface(object obj){return TrainingNative.Interface(obj,"SolidEdgePart",t=>t.GetProperty("Type")!=null&&t.GetProperty("Type").PropertyType==typeof(P.FeatureTypeConstants));}
        public Dictionary<string,object> Export(){
            var result=O("schema","tiangong.feature-tree","schema_version","0.2.0","feature_source","native","features",features,"profiles",profiles,"issues",issues,"order_semantics","SDK Features collection order; not historical replay for synchronous/imported geometry");
            result["modeling_mode"]=Read("features/modeling_mode",()=>((P.ModelingModeConstants)Convert.ToInt32(((dynamic)document).ModelingMode)).ToString());
            Read("features/enumeration",()=>{var models=(P.Models)((dynamic)document).Models;for(int b=1;b<=models.Count;b++){var fs=models.Item(b).Features;for(int i=1;i<=fs.Count;i++){object obj=fs.Item(i);string id=modelId+"/body-"+b+"/feature-"+i;int kind=Convert.ToInt32(((dynamic)obj).Type);Type type=FeatureInterface(obj);var row=O("id",id,"body_id",modelId+"/body-"+b,"order",i,"feature_source","native","native_type",kind,"type",((P.FeatureTypeConstants)kind).ToString(),"api_interface",type==null?null:type.FullName);features.Add(row);Register(obj,id);nodes.Add(Tuple.Create(obj,type,row));}}return true;});
            foreach(var node in nodes){int before=issues.Count;Read((string)node.Item3["id"],()=>{Feature(node.Item1,node.Item2,node.Item3);return true;});node.Item3["status"]=issues.Count==before?"success":"partial";}
            Read("features/all_profiles",()=>{var sets=(P.ProfileSets)((dynamic)document).ProfileSets;for(int i=1;i<=sets.Count;i++){var ps=sets.Item(i).Profiles;for(int j=1;j<=ps.Count;j++)Profile(ps.Item(j));}return true;});
            result["design_tree"]=Read("features/design_tree",()=>{P.EdgebarFeatures tree=(P.EdgebarFeatures)((dynamic)document).DesignEdgebarFeatures;var rows=new List<object>();for(int i=1;i<=tree.Count;i++)rows.Add(O("order",i,"object",Link(tree.Item(i))));return rows;});
            result["variables"]=Read("features/variables",()=>{var vars=(F.Variables)((dynamic)document).Variables;var rows=new List<object>();for(int i=1;i<=vars.Count;i++){object v=vars.Item(i);rows.Add(TrainingNative.Properties(v,v is S.Dimension?typeof(S.Dimension):typeof(F.variable),issues,"variable-"+i));}return rows;});
            // Repeated real runs showed GetParentsAndChildren terminating this
            // CAD build for both copied and extruded features. An HRESULT catch
            // cannot protect against host termination: do not call it.
            foreach(var node in nodes)node.Item3["dependencies"]=O("parents",null,"children",null,"status","unsupported_unstable_native_dependency_api");
            result["dependency_status"]="unsupported_unstable_native_dependency_api";
            result["feature_count"]=features.Count;result["profile_count"]=profiles.Count;result["status"]=issues.Count==0?"success":"partial";return result;
        }
        void Feature(object obj,Type type,Dictionary<string,object> row){string id=(string)row["id"];var native=TrainingNative.Properties(obj,type,issues,id+"/parameters");row["parameters_native"]=native;row["parameters"]=Normalized(native);row["feature_kind"]=type==null?null:type.Name.ToLowerInvariant();row["native_parameter_unit_policy"]="SDK property names and values retained; use normalized SI parameters as training targets";if(type==null){issues.Add(O("location",id,"error","Unsupported feature interface"));row["status"]="unsupported";return;}
            var profilesOut=new List<string>();row["profile_ids"]=profilesOut;var prop=type.GetProperty("Profile");if(prop!=null)Read(id+"/profile",()=>{var p=prop.GetValue(obj,null) as P.Profile;if(p!=null)profilesOut.Add(Profile(p));return true;});
            var multi=type.GetMethod("GetProfiles");if(multi!=null)Read(id+"/profiles",()=>{object[] args={0,new object[0]};multi.Invoke(obj,args);foreach(var p in ((Array)args[1]).Cast<object>().OfType<P.Profile>()){string pid=Profile(p);if(!profilesOut.Contains(pid))profilesOut.Add(pid);}return true;});
            row["hole_sketch_locations"]=TrainingDataset.Rows(profiles).Where(p=>profilesOut.Contains((string)p["id"])).SelectMany(p=>TrainingDataset.Rows(TrainingDataset.Get(p,"holes"))).ToArray();
            var hole=type.GetProperty("HoleData");if(hole!=null){row["hole_data"]=Read(id+"/hole",()=>TrainingNative.Properties(hole.GetValue(obj,null),typeof(P.HoleData),issues,id+"/hole_data"));row["hole_parameters"]=Normalized(row["hole_data"] as Dictionary<string,object>);row["thread_specification"]=Read(id+"/thread",()=>{string s=((P.HoleData)hole.GetValue(obj,null)).ThreadDescription;return System.Text.RegularExpressions.Regex.IsMatch(s??"",@"^M\s*\d+(?:[.,]\d+)?(?:\s*[xX×]\s*\d+(?:[.,]\d+)?)*(?:\s*[-–]\s*\d+[A-Za-z]+)?$")?(object)s:null;});}
            foreach(string name in new[]{"GetConstantRadii","GetVariableRadii"}){var method=type.GetMethod(name);if(method!=null)row[name=="GetConstantRadii"?"constant_radii_m":"variable_radii_native"]=Read(id+"/"+name,()=>{object[] args={0,new double[0]};method.Invoke(obj,args);return O("count",args[0],"values",args[1]);});}
            foreach(string collection in new[]{"Faces","Edges"}){var member=type.GetProperty(collection);string key=collection=="Faces"?"face_ids":"edge_ids";if(member==null){row[key]=null;row[key+"_status"]="unsupported";continue;}row[key]=Read(id+"/"+collection,()=>{dynamic items=member.GetValue(obj,new object[]{G.FeatureTopologyQueryTypeConstants.igQueryAll});var ids=new List<string>();var targets=TrainingDataset.Rows(model["bodies"]).Where(b=>(string)b["id"]==(string)row["body_id"]).SelectMany(b=>TrainingDataset.Rows(b[collection.ToLowerInvariant()])).ToDictionary(x=>Convert.ToInt32(x["native_id"]),x=>(string)x["id"]);int missed=0;for(int i=1;i<=(int)items.Count;i++){int nid=Convert.ToInt32(items.Item(i).ID);string mapped;if(targets.TryGetValue(nid,out mapped))ids.Add(mapped);else missed++;}row[key+"_unresolved_count"]=missed;row[key+"_status"]=missed==0?"success":"partial_history_geometry_absent_from_final_body";return ids;});}
            row["status"]="success";
        }
        static Dictionary<string,object> Normalized(Dictionary<string,object> raw){
            var r=O("axis",null,"center_m",null,"axis_status","unsupported_feature_axis","center_status","unsupported_feature_center");var availability=new Dictionary<string,object>();r["availability"]=availability;
            string[] native={"Depth","HoleDiameter","CounterboreDiameter","CounterboreDepth","CountersinkDiameter","CountersinkAngle","BottomAngle","ThreadDepth","ThreadMinorDiameter","Angle","Radius","Thickness"};string[] fields={"depth_m","diameter_m","counterbore_diameter_m","counterbore_depth_m","countersink_diameter_m","countersink_angle_rad","bottom_angle_rad","thread_depth_m","thread_minor_diameter_m","angle_rad","radius_m","thickness_m"};
            string kind=Convert.ToString(TrainingDataset.Get(TrainingDataset.Get(raw,"HoleType") as Dictionary<string,object>,"enum_name")),treatment=Convert.ToString(TrainingDataset.Get(TrainingDataset.Get(raw,"TreatmentType") as Dictionary<string,object>,"enum_name"));bool hole=kind.Length>0;
            for(int i=0;i<native.Length;i++){if(raw==null||!raw.ContainsKey(native[i]))continue;object v=TrainingDataset.Get(raw,native[i]);bool active=true;
                if(hole){if(native[i].StartsWith("Counterbore"))active=kind=="igCounterboreHole"||kind=="igCounterdrillHole";if(native[i].StartsWith("Countersink"))active=kind=="igCountersinkHole"||kind=="igCounterdrillHole";if(native[i].StartsWith("Thread"))active=treatment=="igTappedHole";if(native[i]=="BottomAngle")active=Convert.ToString(TrainingDataset.Get(TrainingDataset.Get(raw,"TGHoleExtent") as Dictionary<string,object>,"enum_name"))=="igFinite";
                    if(active&&v!=null&&(native[i]=="CountersinkAngle"||native[i]=="BottomAngle"))v=Convert.ToDouble(v)*Math.PI/180.0;
                }
                r[fields[i]]=active?v:null;availability[fields[i]]=!active?"not_applicable":v==null?"unavailable":"available";
            }
            if(hole){r["hole_type"]=kind;r["treatment_type"]=treatment;r["extent_type"]=TrainingDataset.Get(raw,"TGHoleExtent");r["angle_conversion"]="HoleData CountersinkAngle and BottomAngle: degree to radian; inactive defaults omitted";}
            return r;
        }
        string Profile(P.Profile p){string id;if(ids.TryGetValue(TrainingNative.Identity(p),out id))return id;id=modelId+"/profile-"+(profiles.Count+1);Register(p,id);var row=O("id",id,"parameters",TrainingNative.Properties(p,typeof(P.Profile),issues,id+"/properties"));profiles.Add(row);
            row["profile_to_model_affine"]=Read(id+"/plane",()=>{double x,y,z,u,v,w,a,b,c;p.Convert2DCoordinate(0,0,out x,out y,out z);p.Convert2DCoordinate(1,0,out u,out v,out w);p.Convert2DCoordinate(0,1,out a,out b,out c);return new[]{new[]{u-x,a-x,x},new[]{v-y,b-y,y},new[]{w-z,c-z,z}};});
            row["lines"]=Read(id+"/lines",()=>{var rows=new List<object>();for(int i=1;i<=p.Lines2d.Count;i++){var line=p.Lines2d.Item(i);double x,y,u,v;line.GetStartPoint(out x,out y);line.GetEndPoint(out u,out v);string lid=id+"/line-"+i;Register(line,lid);rows.Add(O("id",lid,"start_m",new[]{x,y},"end_m",new[]{u,v}));}return rows;});
            row["circles"]=Read(id+"/circles",()=>{var rows=new List<object>();for(int i=1;i<=p.Circles2d.Count;i++){var c=p.Circles2d.Item(i);double x,y;c.GetCenterPoint(out x,out y);rows.Add(O("center_m",new[]{x,y},"radius_m",c.Radius));}return rows;});
            row["arcs"]=Read(id+"/arcs",()=>{var rows=new List<object>();for(int i=1;i<=p.Arcs2d.Count;i++){var c=p.Arcs2d.Item(i);double x,y;c.GetCenterPoint(out x,out y);rows.Add(O("center_m",new[]{x,y},"radius_m",c.Radius,"start_angle_rad",c.StartAngle,"sweep_angle_rad",c.SweepAngle));}return rows;});
            row["holes"]=Read(id+"/holes",()=>{var rows=new List<object>();for(int i=1;i<=p.Holes2d.Count;i++){var h=p.Holes2d.Item(i);double x,y,mx,my,mz;h.GetCenterPoint(out x,out y);p.Convert2DCoordinate(x,y,out mx,out my,out mz);rows.Add(O("profile_id",id,"index",i,"center_profile_m",new[]{x,y},"center_model_m",new[]{mx,my,mz},"source","native_Hole2d_and_Profile_Convert2DCoordinate"));}return rows;});
            row["dimensions"]=Read(id+"/dimensions",()=>{var dims=(S.Dimensions)p.Dimensions;var rows=new List<object>();for(int i=1;i<=dims.Count;i++)rows.Add(TrainingNative.Properties(dims.Item(i),typeof(S.Dimension),issues,id+"/dimension-"+i));return rows;});
            row["constraint_count"]=Read(id+"/constraints",()=>((dynamic)p.Relations2d).Count);row["constraints"]=null;row["constraints_status"]="unsupported_structure";
            row["other_geometry_counts"]=Read(id+"/others",()=>O("bsplines",p.BSplineCurves2d.Count,"ellipses",p.Ellipses2d.Count,"points",p.Points2d.Count,"holes",p.Holes2d.Count));return id;
        }
    }
}
