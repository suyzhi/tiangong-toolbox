using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using D=SolidEdgeDraft;
using S=SolidEdgeFrameworkSupport;
namespace TianGongCadSuite {
    public sealed class TrainingAnnotationReader {
        readonly Func<object,object> reference;
        readonly List<object> issues=new List<object>();
        public readonly List<object> Items=new List<object>();
        public TrainingAnnotationReader(Func<object,object> resolver){reference=resolver;}
        static Dictionary<string,object> O(params object[] p){return TrainingExporter.Obj(p);}
        object Read(string path,Func<object> get){return TrainingNative.Read(issues,path,get);}
        // Only these known read-only methods are called; no inferred IDispatch names.
        public static Dictionary<string,object> Outputs(object obj,Type type,string name,params object[] inputs){
            MethodInfo method=type.GetMethod(name);if(method==null)throw new NotSupportedException(name);
            var ps=method.GetParameters();object[] args=new object[ps.Length];int n=0;
            for(int i=0;i<ps.Length;i++){Type t=ps[i].ParameterType;if(t.IsByRef)t=t.GetElementType();args[i]=!ps[i].ParameterType.IsByRef?inputs[n++]:t==typeof(Array)?(name=="GetVertices"?(object)new double[0]:new object[0]):t==typeof(string)?"":t.IsValueType?Activator.CreateInstance(t):null;}
            method.Invoke(obj,args);var result=new Dictionary<string,object>();
            for(int i=0;i<ps.Length;i++)if(ps[i].ParameterType.IsByRef)result[ps[i].Name]=TrainingNative.Value(args[i]);return result;
        }
        public static Dictionary<string,object> Display(S.DisplayData data){
            var errors=new List<object>();var result=O("coordinate_system","sheet","length_unit","m","angle_unit","rad","source","native_DisplayData","issues",errors);
            string[] kinds={"Line","Arc","Terminator","Text","Ellipse"};
            foreach(string kind in kinds){string k=kind;var rows=new List<object>();result[k.ToLowerInvariant()+"s"]=rows;
                TrainingNative.Read(errors,"display/"+k,()=>{int count=Convert.ToInt32(typeof(S.DisplayData).GetMethod("Get"+k+"Count").Invoke(data,null));for(int i=0;i<count;i++){int index=i;var item=TrainingNative.Read(errors,"display/"+k+"-"+i,()=>Outputs(data,typeof(S.DisplayData),"Get"+k+"AtIndex",index));if(item!=null)rows.Add(item);}return true;});
            }
            result["status"]=errors.Count==0?"success":"partial";return result;
        }
        public static Dictionary<string,object> DimensionLayout(S.Dimension dim){
            var errors=new List<object>();var display=(Dictionary<string,object>)TrainingNative.Read(errors,"dimension/display",()=>Display(dim.GetDisplayData()));
            var r=O("source","native","display_geometry",display,"text_bbox_sheet_m",null,"text_bbox_status","unsupported_separate_text_range","dimension_line",null,"extension_lines",null,"line_roles_status","unsupported_native_DisplayData_role_labels","placement_side",null,"placement_side_status","unsupported","issues",errors);
            var text=TrainingDataset.Rows(TrainingDataset.Get(display,"texts")).FirstOrDefault();
            r["text_position_sheet_m"]=text==null?null:new[]{Convert.ToDouble(text["OriginX"]),Convert.ToDouble(text["OriginY"])};
            r["arrows"]=TrainingDataset.Get(display,"terminators");
            r["text_offsets_m"]=TrainingNative.Read(errors,"dimension/text_offsets",()=>{double x,y;dim.GetTextOffsets(out x,out y);return new[]{x,y};});
            r["track_distance_m"]=TrainingNative.Read(errors,"dimension/track",()=>dim.TrackDistance);
            r["track_angle_rad"]=TrainingNative.Read(errors,"dimension/angle",()=>dim.TrackAngle);
            if(r["track_angle_rad"]!=null&&Math.Abs(Convert.ToDouble(r["track_angle_rad"]))>Math.PI*2){r["track_angle_native"]=r["track_angle_rad"];r["track_angle_rad"]=null;r["track_angle_status"]="unavailable_native_value_outside_angle_range";}
            r["leader_distance_m"]=TrainingNative.Read(errors,"dimension/leader_distance",()=>dim.LeaderDistance);
            r["leader"]=dim.Leader;r["group_member_type"]=dim.GroupMemberType.ToString();r["chamfer_mode"]=dim.ChamferDimensionMode.ToString();
            r["status"]=errors.Count==0&&display!=null&&Convert.ToString(display["status"])=="success"?"success":"partial";return r;
        }
        public Dictionary<string,object> Export(D.Sheet sheet,string sid){
            var modules=new Dictionary<string,object>();
            string[] collections={"DatumFrames","FeatureControlFrames","SurfaceFinishSymbols","CenterLines","CenterMarks","WeldSymbols","Balloons","Leaders","RevisionSymbols"};
            string[] classes={"DatumFrame","FeatureControlFrame","SurfaceFinishSymbol","CenterLine","CenterMark","WeldSymbol","Balloon","Leader","RevisionSymbol"};
            string[] kinds={"datum","gdt","roughness","centerline","center_mark","weld","balloon","leader","revision"};
            for(int k=0;k<collections.Length;k++){int at=k;int before=issues.Count,count=0;bool enumerated=false;
                Read(sid+"/"+collections[k],()=>{dynamic items=typeof(D.Sheet).GetProperty(collections[at]).GetValue(sheet,null);count=(int)items.Count;enumerated=true;for(int i=1;i<=count;i++){object item=items.Item(i);string id=sid+"/"+kinds[at]+"-"+i;var row=O("id",id,"kind",kinds[at],"source","native");Items.Add(row);int prior=issues.Count;Read(id,()=>{Annotation(item,typeof(S.Dimension).Assembly.GetType("SolidEdgeFrameworkSupport."+classes[at]),row);return true;});row["status"]=issues.Count==prior?"success":"partial";}return true;});
                modules[kinds[k]]=O("count",count,"status",issues.Count==before?"success":enumerated?"partial":"failed");
            }
            return O("status",issues.Count==0?"success":"partial","modules",modules,"issues",issues);
        }
        void Annotation(object obj,Type type,Dictionary<string,object> row){
            string id=(string)row["id"],kind=(string)row["kind"];int before=issues.Count;
            row["parameters_native"]=TrainingNative.Properties(obj,type,issues,id);
            row["parameter_unit_policy"]="native SDK scalars; unconverted fields are not SI training targets";
            row["bbox_sheet_m"]=Read(id+"/range",()=>{var b=Outputs(obj,type,"Range");return new[]{b["XMinimum"],b["YMinimum"],b["XMaximum"],b["YMaximum"]};});
            if(type.GetMethod("GetDisplayData")!=null)row["layout"]=Read(id+"/display",()=>Display((S.DisplayData)type.GetMethod("GetDisplayData").Invoke(obj,null)));
            var refs=new List<object>();row["references"]=refs;
            // CenterLine.GetCenter on a straight native line crashes this CAD build.
            // Endpoints remain sufficient to preserve its actual native attachment.
            foreach(string name in new[]{"GetTerminator","GetStart","GetEnd","GetCenter"})if(type.GetMethod(name)!=null&&!(kind=="centerline"&&name=="GetCenter"))Read(id+"/"+name,()=>{var attached=type.GetProperty("IsTerminatorAttachedToEntity");if(name=="GetTerminator"&&attached!=null&&!(bool)attached.GetValue(obj,null))return null;var data=Outputs(obj,type,name);var entity=data.FirstOrDefault(p=>p.Value!=null&&System.Runtime.InteropServices.Marshal.IsComObject(p.Value));if(entity.Value!=null){data[entity.Key]=null;data["native_reference"]=reference(entity.Value);}data["method"]=name;refs.Add(data);return true;});
            if(type.GetMethod("GetReferencedGeometry")!=null)Read(id+"/referenced_geometry",()=>{object[] args={new object[0]};type.GetMethod("GetReferencedGeometry").Invoke(obj,args);foreach(object entity in (Array)args[0])if(entity!=null)refs.Add(O("method","GetReferencedGeometry","native_reference",reference(entity)));return true;});
            if(type.GetMethod("GetVertices")!=null)row["leader_vertices"]=Read(id+"/vertices",()=>Outputs(obj,type,"GetVertices"));
            if(kind=="datum"){string label=((S.DatumFrame)obj).Datum;row["label"]=Regex.IsMatch(label??"",@"^[A-Z][A-Z0-9]{0,2}$")?label:null;row["label_status"]=row["label"]==null?"redacted_nonstandard_label":"available";}
            if(kind=="gdt"){
                var f=(S.FeatureControlFrame)obj;row["frames_native"]=new[]{Frame(f.PrimaryFrame),Frame(f.SecondaryFrame),Frame(f.TertiaryFrame),Frame(f.QuaternaryFrame)};
                row["geometric_characteristic"]=null;row["value_m"]=null;row["diameter_zone"]=null;row["datum_references"]=null;row["semantic_status"]="unsupported_native_interface_exposes_formatted_frames_only";
            }
            if(kind=="roughness"){var f=(S.SurfaceFinishSymbol)obj;row["symbol_type"]=Read(id+"/symbol_type",()=>f.SurfaceFinishSymbol.ToString());row["lay_symbol"]=Read(id+"/lay_symbol",()=>f.SurfaceLaySymbol.ToString());row["roughness_value_native"]=Read(id+"/value",()=>NumericToken(f.RoughnessValue));row["minimum_value_native"]=Read(id+"/minimum",()=>NumericToken(f.MinimumRoughnessValue));row["maximum_value_native"]=Read(id+"/maximum",()=>NumericToken(f.MaximumRoughnessValue));row["roughness_value_m"]=null;row["unit_status"]="unsupported_explicit_native_roughness_unit";}
            row["status"]=issues.Count==before?"success":"partial";
        }
        static object NumericToken(string value){return Regex.IsMatch(value??"",@"^[+\-]?\d+(?:[.,]\d+)?$")?(object)value:null;}
        static object Frame(string value){return O("encoded_text",null,"encoded_text_sha256",TrainingDataset.Digest(value),"status",string.IsNullOrEmpty(value)?"empty":"redacted_formatted_text","structured_fields",null);}
    }
}
