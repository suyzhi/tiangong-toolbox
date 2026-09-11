using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
namespace TianGongCadSuite {
    public static class TrainingMetadata {
        static object Get(Dictionary<string,object> d,string k){return TrainingDataset.Get(d,k);}
        static string N(double d){return Math.Round(d,9).ToString("R",System.Globalization.CultureInfo.InvariantCulture);}
        static string Hash(IEnumerable<string> values){return TrainingDataset.Digest(string.Join(";",values.OrderBy(s=>s,StringComparer.Ordinal)));}
        static int Count(object value){return value is ICollection?((ICollection)value).Count:0;}
        public static void Apply(Dictionary<string,object> root){
            var models=TrainingDataset.Rows(Get(root,"models")).ToArray();
            foreach(var m in models){var bodies=TrainingDataset.Rows(Get(m,"bodies")).ToArray();var faces=bodies.SelectMany(b=>TrainingDataset.Rows(Get(b,"faces"))).ToArray();var edges=bodies.SelectMany(b=>TrainingDataset.Rows(Get(b,"edges"))).ToArray();var features=TrainingDataset.Rows(Get(Get(m,"feature_tree") as Dictionary<string,object>,"features")).Select(f=>Convert.ToString(Get(f,"type"))).OrderBy(x=>x).ToArray();
                double scale=edges.Where(e=>Get(e,"length_m")!=null).Select(e=>Convert.ToDouble(e["length_m"])).DefaultIfEmpty(0).Max();var normalized=new List<string>();var exact=new List<string>();var topo=new List<string>();
                foreach(var f in faces){string kind=Convert.ToString(Get(f,"surface_kind"));double area=Convert.ToDouble(Get(f,"area_m2"));exact.Add(kind+"|A:"+N(area));if(scale>0)normalized.Add(kind+"|A:"+N(area/(scale*scale)));topo.Add(kind+"|E:"+Count(Get(f,"edge_ids"))+"|F:"+Count(Get(f,"adjacent_face_ids")));var g=Get(f,"geometry") as Dictionary<string,object>;foreach(string key in new[]{"radius_m","major_radius_m","minor_radius_m","half_angle_rad"})if(Get(g,key)!=null){double value=Convert.ToDouble(g[key]);exact.Add(kind+"|"+key+":"+N(value));if(scale>0)normalized.Add(kind+"|"+key+":"+N(key.EndsWith("_m")?value/scale:value));}}
                foreach(var e in edges){string kind=Convert.ToString(Get(e,"curve_kind"));if(Get(e,"length_m")!=null){double length=Convert.ToDouble(e["length_m"]);exact.Add(kind+"|L:"+N(length));if(scale>0)normalized.Add(kind+"|L:"+N(length/scale));}}
                string documentType=Convert.ToString(Get(m,"kind"))=="part"?"PAR":Convert.ToString(Get(m,"kind"))=="sheet_metal"?"PSM":"ASM";
                m["dataset_metadata"]=TrainingExporter.Obj("document_type",documentType,"family_id",Get(m,"family_member")==null?null:Get(m,"source_sha256"),"family_source",Get(m,"family_member")==null?"unavailable":"native_family_member_in_physical_document","geometry_hash",exact.Count>0?Hash(exact):null,"normalized_geometry_hash",normalized.Count>0?Hash(normalized):null,"normalization_scale_m",scale>0?(object)scale:null,"topology_hash",topo.Count>0?Hash(topo):null,"feature_type_hash",features.Length>0?Hash(features):null,"near_duplicate_group",null,"hash_status",exact.Count==0?"unsupported_assembly_layout_hash":"native_descriptor_hash","hash_method","v2: sorted analytic surface types/areas/radii and curve types/lengths, 9 decimal SI; normalized copy divides by longest native edge; descriptors can collide and do not prove shape identity","topology_hash_method","sorted local native face adjacency degree descriptors; collisions possible");
            }
            root["dataset_metadata"]=TrainingExporter.Obj("document_type",Get(root,"document_type"),"paired_model_types",models.Select(m=>Get(Get(m,"dataset_metadata") as Dictionary<string,object>,"document_type")).Distinct().ToArray(),"family_id",null,"geometry_hash",null,"topology_hash",null,"near_duplicate_group",null,"status","use_per_model_descriptors_and_shared_model_hash_split_groups");
        }
    }
}
