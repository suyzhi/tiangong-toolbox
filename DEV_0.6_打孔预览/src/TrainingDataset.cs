using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Web.Script.Serialization;
namespace TianGongCadSuite {
    public static class TrainingDataset {
        public static IEnumerable<Dictionary<string,object>> Rows(object value){var list=value as IEnumerable;if(list!=null)foreach(object v in list){var d=v as Dictionary<string,object>;if(d!=null)yield return d;}}
        public static object Get(Dictionary<string,object> d,string key){object v;return d!=null&&d.TryGetValue(key,out v)?v:null;}
        static Dictionary<string,object> O(params object[] p){return TrainingExporter.Obj(p);}
        public static string Digest(string text){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text??""))).Replace("-","").ToLowerInvariant();}
        static int Count(object x){return x is ICollection?((ICollection)x).Count:0;}
        static double? Coverage(int n,int total){return total==0?(double?)null:(double)n/total;}
        public static Dictionary<string,object> Capabilities(Dictionary<string,object> sample){var summary=Validate(sample);return O("semantics","observed in this sample; false can mean absent or unsupported; inspect module status","native_brep",Convert.ToInt32(summary["faces"])>0,"mesh_face_mapping",summary["triangle_native_face_coverage"]!=null&&Convert.ToDouble(summary["triangle_native_face_coverage"])==1,"feature_tree",Rows(Get(sample,"models")).Any(m=>Get(m,"feature_tree")!=null),"drawing_3d_reference",summary["dimension_native_reference_coverage"]!=null&&Convert.ToDouble(summary["dimension_native_reference_coverage"])>0,"assembly_reference",Convert.ToInt32(summary["mapped_occurrence_reference_count"])>0,"gdt",Convert.ToInt32(summary["gdt"])>0,"gdt_structured_tolerance",false,"datum",Convert.ToInt32(summary["datums"])>0,"roughness",Convert.ToInt32(summary["roughness"])>0);}
        public static Dictionary<string,object> Validate(Dictionary<string,object> sample){
            var models=Rows(Get(sample,"models")).ToArray();var bodies=models.SelectMany(m=>Rows(Get(m,"bodies"))).ToArray();var faces=bodies.SelectMany(b=>Rows(Get(b,"faces"))).ToArray();var edges=bodies.SelectMany(b=>Rows(Get(b,"edges"))).ToArray();
            var allFaces=new HashSet<string>(faces.Select(f=>(string)f["id"]));var allEdges=new HashSet<string>(edges.Select(f=>(string)f["id"]));var warnings=new List<string>();int triangles=0,mapped=0,topologyErrors=0;
            foreach(var b in bodies){var mesh=Get(b,"mesh") as Dictionary<string,object>;if(mesh!=null){int n=Convert.ToInt32(Get(mesh,"triangle_count"));triangles+=n;var ids=Get(mesh,"triangle_face_ids") as IEnumerable;if(ids!=null)foreach(object fid in ids)if(fid is string&&allFaces.Contains((string)fid))mapped++;if(Count(Get(mesh,"triangle_face_ids"))!=n)topologyErrors++;}
                var topology=Get(b,"topology") as Dictionary<string,object>;var vids=new HashSet<string>(Rows(Get(topology,"vertices")).Select(v=>(string)v["id"]));
                foreach(var e in Rows(Get(b,"edges"))){var vs=Get(e,"vertex_ids") as IEnumerable;if(vs!=null)foreach(object v in vs)if(v!=null&&!vids.Contains((string)v))topologyErrors++;}
                foreach(var f in Rows(Get(b,"faces"))){var eids=Get(f,"edge_ids") as IEnumerable;if(eids!=null)foreach(object eid in eids)if(!allEdges.Contains((string)eid))topologyErrors++;}
            }
            var drawing=Get(sample,"drawing") as Dictionary<string,object>;var sheets=Rows(Get(drawing,"sheets")).Where(s=>Convert.ToString(Get(s,"section_type"))=="igWorkingSection").ToArray();var views=sheets.SelectMany(s=>Rows(Get(s,"views"))).ToArray();var dims=sheets.SelectMany(s=>Rows(Get(s,"dimensions"))).ToArray();int bound=0,drawingBound=0;
            foreach(var dim in dims){var refs=Rows(Get(dim,"related_geometry")).Select(r=>Get(r,"native_reference") as Dictionary<string,object>).Where(r=>r!=null).ToArray();if(refs.Any(r=>Get(r,"drawing_geometry_id")!=null))drawingBound++;if(refs.Any(r=>Get(r,"native_geometry")!=null))bound++;}
            if(topologyErrors>0)warnings.Add("topology_consistency_failed");if(mapped!=triangles||triangles==0)warnings.Add("mesh_face_mapping_incomplete");
            var annotations=sheets.SelectMany(s=>Rows(Get(s,"annotations"))).ToArray();int datums=annotations.Count(a=>Convert.ToString(Get(a,"kind"))=="datum"),gdt=annotations.Count(a=>Convert.ToString(Get(a,"kind"))=="gdt"),rough=annotations.Count(a=>Convert.ToString(Get(a,"kind"))=="roughness");
            int features=models.Sum(m=>Rows(Get(Get(m,"feature_tree") as Dictionary<string,object>,"features")).Count());
            var summary=O("schema","tiangong.export-summary","schema_version","0.2.0","document_type",Get(sample,"document_type"),"bodies",bodies.Length,"faces",faces.Length,"edges",edges.Length,"features",features,"mesh_triangles",triangles,"mesh_native_face_coverage",Coverage(mapped,triangles),"triangle_native_face_coverage",Coverage(mapped,triangles),"topology_errors",topologyErrors,"drawing_views",views.Length,"valid_transforms",views.Count(v=>Get(v,"model_to_sheet_affine")!=null),"dimensions",dims.Length,"dimension_drawing_reference_coverage",Coverage(drawingBound,dims.Length),"dimension_native_reference_coverage",Coverage(bound,dims.Length),"datums",datums,"gdt",gdt,"roughness",rough,"datum_native_reference_coverage",(object)null,"occurrence_mapping_coverage",(object)null,"warnings",warnings,"training_ready",false);
            summary["status"]=warnings.Count==0?"success":"partial";TrainingValidation.Enrich(sample,summary);return summary;
        }
        public static void LinkGeometry(Dictionary<string,object> sample){
            var models=Rows(Get(sample,"models")).ToArray();var references=new Dictionary<string,Dictionary<string,object>>();
            var drawingKeys=new Dictionary<string,List<string>>();
            foreach(var sheet in Rows(Get(Get(sample,"drawing") as Dictionary<string,object>,"sheets")))foreach(var view in Rows(Get(sheet,"views")))foreach(string kind in new[]{"lines","arcs","circles"})foreach(var curve in Rows(Get(view,kind))){var reference=Get(curve,"native_reference") as Dictionary<string,object>;if(Get(reference,"drawing_reference_key")==null)continue;string key=(string)view["id"]+"|"+reference["drawing_reference_key"];List<string> candidates;if(!drawingKeys.TryGetValue(key,out candidates)){candidates=new List<string>();drawingKeys[key]=candidates;}candidates.Add((string)curve["id"]);}
            foreach(var model in models)foreach(var body in Rows(Get(model,"bodies")))foreach(var edge in Rows(Get(body,"edges")))if(Get(edge,"reference_key")!=null)references[(string)model["id"]+"|"+edge["reference_key"]]=edge;
            Action<object> visit=null;visit=value=>{var d=value as Dictionary<string,object>;if(d!=null){var bound=Get(d,"bound_edge") as Dictionary<string,object>;if(bound!=null){string key=Convert.ToString(Get(bound,"canonical_reference_key"));var matches=references.Where(p=>p.Key==Convert.ToString(Get(d,"target_model_id"))+"|"+key).Select(p=>p.Value).ToArray();if(matches.Length==1){d["native_geometry"]=O("type","edge","id",matches[0]["id"]);d["mapping_status"]="success";}else d["mapping_status"]=matches.Length==0?"unresolved":"ambiguous";}
                if(Get(d,"view_id")!=null&&Get(d,"drawing_reference_key")!=null){List<string> candidates;if(drawingKeys.TryGetValue(d["view_id"]+"|"+d["drawing_reference_key"],out candidates)){d["drawing_geometry_candidates"]=candidates.ToArray();if(candidates.Count==1){d["drawing_geometry_id"]=candidates[0];d["drawing_mapping_source"]="native_drawing_object_key_in_view";}else d["drawing_mapping_status"]="ambiguous_native_drawing_key";}}
                foreach(var child in d.Values.ToArray())visit(child);}else if(value is IEnumerable&&!(value is string))foreach(var child in (IEnumerable)value)visit(child);};visit(sample);
            foreach(var sheet in Rows(Get(Get(sample,"drawing") as Dictionary<string,object>,"sheets")))foreach(var dim in Rows(Get(sheet,"dimensions"))){var refs=Rows(Get(dim,"related_geometry")).Select(r=>Get(r,"native_reference") as Dictionary<string,object>).Where(r=>r!=null).ToArray();var views=refs.Select(r=>Get(r,"view_id") as string).Where(x=>x!=null).Distinct().ToArray();dim["view_id"]=views.Length==1?views[0]:null;dim["view_ids"]=views;dim["view_status"]=views.Length==1?"success":views.Length>1?"multiple_views":"unresolved";dim["references"]=refs.Select(r=>O("drawing_geometry",Get(r,"drawing_geometry_id"),"native_geometry",Get(r,"native_geometry"),"mapping_status",Get(r,"mapping_status"))).ToArray();}
        }
        public static void Metadata(Dictionary<string,object> sample){foreach(var model in Rows(Get(sample,"models"))){var bodies=Rows(Get(model,"bodies")).ToArray();var geom=new List<string>();var topology=new List<string>();foreach(var body in bodies){foreach(var f in Rows(Get(body,"faces"))){geom.Add(Convert.ToString(Get(f,"surface_kind"))+"|"+Rounded(Get(f,"area_m2")));topology.Add(Convert.ToString(Get(f,"surface_kind"))+"|"+Count(Get(f,"edge_ids"))+"|"+Count(Get(f,"adjacent_face_ids")));}foreach(var e in Rows(Get(body,"edges")))geom.Add(Convert.ToString(Get(e,"curve_kind"))+"|"+Rounded(Get(e,"length_m")));}geom.Sort(StringComparer.Ordinal);topology.Sort(StringComparer.Ordinal);model["dataset_metadata"]=O("document_type",Get(model,"kind"),"family_id",Get(model,"family_member")==null?null:Get(model,"source_sha256"),"geometry_hash",Digest(string.Join(";",geom)),"topology_hash",Digest(string.Join(";",topology)),"near_duplicate_group",null,"hash_status",geom.Count==0?"unsupported_empty_geometry":"descriptor_hash","hash_method","sorted native surface types/areas and curve types/lengths rounded to 9 decimals; translation and rotation independent; not a unique shape proof","topology_hash_method","unlabelled local face-degree descriptors; collisions possible");}}
        static string Rounded(object x){return x==null?"null":Math.Round(Convert.ToDouble(x),9).ToString("R",System.Globalization.CultureInfo.InvariantCulture);}
        public static void Sanitize(object value){
            var d=value as Dictionary<string,object>;if(d!=null){foreach(var key in d.Keys.ToArray()){
                if(key=="availability"||key=="geometry_status")continue;
                object child=d[key];string text=child as string;
                bool privateKey=Regex.IsMatch(key,"^(source_name|source_path|model_path|path_original|name|Name|DisplayName|SystemName|EdgebarName|VariableTableName|caption|text|display_text|background|family_member|Formula|formula|author|user_name|prefix_text|suffix_text|override|TextString|PrefixString|SuffixString|SubfixString|SuperfixString|OverrideString|.*DisplayedText)$");
                if(text!=null&&(privateKey||Regex.IsMatch(text,@"[A-Za-z]:[\\/]|\\\\[^\\]+\\"))){d[key+"_sha256"]=Digest(text);d[key]=null;d[key+"_status"]="redacted";}
                else if(key=="error"&&text!=null){d["error_code"]=d.ContainsKey("hresult")?d["hresult"]:"read_failed";d[key]="native_read_failed";}
                else Sanitize(child);
            }}else if(value is IEnumerable&&!(value is string))foreach(var child in (IEnumerable)value)Sanitize(child);
        }
    }
}
