using System;
using System.IO;
using System.Text;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Web.Script.Serialization;
using F=SolidEdgeFramework;
using D=SolidEdgeDraft;
using G=SolidEdgeGeometry;
using S=SolidEdgeFrameworkSupport;

namespace TianGongCadSuite {
    // Export IDs are local to a source hash. Native reference keys must never be
    // interpreted as STEP face indices or compared across model revisions.
    public sealed class TrainingExporter {
        readonly F.Application app;
        readonly List<object> issues=new List<object>();
        readonly Dictionary<string,Dictionary<string,object>> models=new Dictionary<string,Dictionary<string,object>>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string,string> sourceHashes=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<long,string> drawingIds=new Dictionary<long,string>();
        readonly Dictionary<string,string> viewKeys=new Dictionary<string,string>();
        readonly Dictionary<long,string> occurrenceIds=new Dictionary<long,string>();
        string output;
        Dictionary<string,object> pendingDrawing;
        public string LastStatus {get;private set;}
        public TrainingExporter(F.Application application){app=application;}
        public static Dictionary<string,object> Obj(params object[] pairs){var r=new Dictionary<string,object>();for(int i=0;i<pairs.Length;i+=2)r.Add((string)pairs[i],pairs[i+1]);return r;}
        public static string Hash(string file){using(var h=SHA256.Create())using(var s=File.Open(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite))return BitConverter.ToString(h.ComputeHash(s)).Replace("-","").ToLowerInvariant();}
        public static void Json(string file,object value){var j=new JavaScriptSerializer{MaxJsonLength=int.MaxValue,RecursionLimit=200};File.WriteAllText(file,j.Serialize(value),new UTF8Encoding(false));}
        object Read(string where,Func<object> read){try{return read();}catch(Exception e){issues.Add(Obj("location",where,"error",e.Message,"hresult",e.HResult));return null;}}
        void Props(Dictionary<string,object> row,string where,params object[] fields){for(int i=0;i<fields.Length;i+=2)row[(string)fields[i]]=Read(where+"/"+fields[i],(Func<object>)fields[i+1]);}
        static Func<object> Fn(Func<object> f){return f;}
        string Track(object document){dynamic d=document;string raw=(string)d.FullName;int member=raw.LastIndexOf('!');string p=Path.GetFullPath(member>=0?raw.Substring(0,member):raw);if(!File.Exists(p))throw new IOException("请先保存文件："+p);if((bool)d.Dirty)throw new InvalidOperationException("存在未保存修改，请先自行保存再导出："+p);if(!sourceHashes.ContainsKey(p))sourceHashes.Add(p,Hash(p));return p;}
        string NativeCopy(object document,string relative){string source=Track(document);string path=Path.Combine(output,relative);Directory.CreateDirectory(Path.GetDirectoryName(path));File.Copy(source,path,false);if(Hash(path)!=sourceHashes[source])throw new IOException("复制期间源文件发生变化："+source);return relative.Replace('\\','/');}
        public string Export(object document,string parent){
            if(output!=null)throw new InvalidOperationException("Each exporter is single use.");
            string source=Track(document);
            output=Path.Combine(Path.GetFullPath(parent),"sample-"+sourceHashes[source].Substring(0,16)+"-"+Guid.NewGuid().ToString("N").Substring(0,8));Directory.CreateDirectory(output);
            var root=Obj("schema","tiangong.drawing-training","schema_version","0.2.0","legacy_schema_version","tiangong.drawing-training/0.1","exporter_version","0.2.0","created_utc",DateTime.UtcNow.ToString("o"),"source_sha256",sourceHashes[source],"document_type",Path.GetExtension(source).TrimStart('.').ToUpperInvariant(),"source_name",Path.GetFileName(source),"length_unit","m","angle_unit","rad","coordinate_policy","Native CAD coordinates; no centering, rotation or scale normalization", "training_ready",false,"review_status","requires_review","issues",issues);
            try{
                var draft=document as D.DraftDocument;
                if(draft!=null){root["task"]="model_to_drawing";root["drawing"]=Drawing(draft);}
                else {root["task"]="model_only";root["model_id"]=Model(document);}
                // Translate only after extracting associations. SaveAs runs on a unique
                // copied document and never on the caller's source document.
                foreach(var m in models.Values){var model=m;m["step_file"]=Read((string)m["id"]+"/step",()=>{if(model["family_member"]!=null)throw new NotSupportedException("Family member STEP conversion is not yet verified; master assembly substitution refused");return Translate((string)model["native_file"],"geometry.stp",false);});}
                if(draft!=null){var drawing=(Dictionary<string,object>)root["drawing"];drawing["pdf_file"]=Read("drawing/pdf",()=>Translate((string)drawing["native_file"],"drawing.pdf",true));}
                root["models"]=models.Values.ToArray();
                root["split_group_model_hashes"]=models.Values.Select(m=>(string)m["source_sha256"]).Distinct().OrderBy(s=>s).ToArray();
                root["unimplemented"]=new[]{"STEP face-to-native-face remapping","complete GD&T / surface finish / weld / BOM semantics","drawing action sequence reconstruction","assembly overrides and exploded geometry mapping","point cloud derivation","all drawing primitive classes"};
                root["extraction_status"]=issues.Count==0?"extracted_requires_review":"partial";
            }catch(Exception e){issues.Add(Obj("location","export","error",e.Message));root["extraction_status"]="failed";}
            finally{
                root["models"]=models.Values.ToArray();if(pendingDrawing!=null)root["drawing"]=pendingDrawing;
                bool unchanged=true;foreach(var p in sourceHashes)try{if(Hash(p.Key)!=p.Value){unchanged=false;issues.Add(Obj("location","source_integrity","file",p.Key,"error","source changed during export"));}}catch(Exception e){unchanged=false;issues.Add(Obj("location","source_integrity","error",e.Message));}
                root["source_integrity_verified"]=unchanged;if(!unchanged)root["extraction_status"]="failed";
                root["source_audit"]=sourceHashes.Select(p=>Obj("path",p.Key,"sha256_before",p.Value)).ToArray();
                root["files"]=Directory.GetFiles(output,"*",SearchOption.AllDirectories).Select(p=>Obj("path",p.Substring(output.Length+1).Replace('\\','/'),"sha256",Hash(p),"bytes",new FileInfo(p).Length)).ToArray();
                TrainingDataset.LinkGeometry(root);TrainingProjection.Apply(root);TrainingMetadata.Apply(root);root["capabilities"]=TrainingDataset.Capabilities(root);root["privacy"]=Obj("mode","training","free_text","redacted","media","native images and files retain source content");TrainingDataset.Sanitize(root);LastStatus=(string)root["extraction_status"];Json(Path.Combine(output,"sample.json"),root);Json(Path.Combine(output,"export_summary.json"),TrainingDataset.Validate(root));Json(Path.Combine(output,"training-labels.json"),Obj("schema","tiangong.training-labels","schema_version","0.2.0","models",root["models"],"sheets",pendingDrawing==null?(object)new object[0]:pendingDrawing["sheets"],"length_unit","m","angle_unit","rad","training_ready",false));
            }
            return output;
        }
        string Model(object document){
            string identity=(string)((dynamic)document).FullName;Dictionary<string,object> existing;if(models.TryGetValue(identity,out existing))return (string)existing["id"];string source=Track(document);
            string member=identity.StartsWith(source+"!",StringComparison.OrdinalIgnoreCase)?identity.Substring(source.Length+1):null;
            string id="model-"+sourceHashes[source];if(member!=null){using(var sha=SHA256.Create())id+="-member-"+BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(member))).Replace("-","").Substring(0,16).ToLowerInvariant();}
            var row=Obj("id",id,"source_sha256",sourceHashes[source],"source_name",Path.GetFileName(source),"family_member",member);models.Add(identity,row);
            // Short physical paths are needed by CAD / .NET Framework MAX_PATH.
            // Full SHA-256 identities remain in JSON, not in directory names.
            string dir="models/m"+models.Count.ToString("D4");row["native_file"]=NativeCopy(document,dir+"/source"+Path.GetExtension(source));
            var asm=document as SolidEdgeAssembly.AssemblyDocument;
            if(asm!=null){
                row["kind"]="assembly";var items=new List<object>();row["occurrences"]=items;
                for(int i=1;i<=asm.Occurrences.Count;i++){var o=asm.Occurrences.Item(i);var r=Obj("id",id+"/occ-"+i);items.Add(r);occurrenceIds[TrainingNative.Identity(o)]=(string)r["id"];Props(r,(string)r["id"],"name",Fn(()=>o.Name),"visible",Fn(()=>o.Visible),"source_name",Fn(()=>Path.GetFileName(o.OccurrenceFileName)),"reference_key",Fn(()=>Reference(o)),"has_body_override",Fn(()=>o.HasBodyOverride),"matrix_native",Fn(()=>{Array matrix=new double[16];o.GetMatrix(ref matrix);return matrix;}),"model_id",Fn(()=>Model(o.OccurrenceDocument)));}
                row["matrix_convention"]="SolidEdge Occurrence.GetMatrix: 16 values, translation at indices 12,13,14; child local to parent; length m";
            }else{
                row["kind"]=document is SolidEdgePart.SheetMetalDocument?"sheet_metal":"part";var bodies=new List<object>();row["bodies"]=bodies;
                Read(id+"/bodies",()=>{dynamic ms=((dynamic)document).Models;for(int i=1;i<=(int)ms.Count;i++)bodies.Add(Body((G.Body)ms.Item(i).Body,id+"/body-"+i));return true;});
                row["feature_tree"]=Read(id+"/feature_tree",()=>new TrainingFeatureReader(document,id,row).Export());
            }
            return id;
        }
        string Translate(string native,string filename,bool pdf){
            string source=Path.GetFullPath(Path.Combine(output,native)),target=Path.Combine(Path.GetDirectoryName(source),filename);object clone=null,previous=null,oldPdf=null;
            bool restorePdf=false;
            try{
                try{previous=app.ActiveDocument;}catch{}
                clone=app.Documents.Open(source);
                if(!string.Equals(Path.GetFullPath((string)((dynamic)clone).FullName),source,StringComparison.OrdinalIgnoreCase))throw new IOException("CAD returned a different document; translation refused");
                if(pdf){app.GetGlobalParameter(F.ApplicationGlobalConstants.seApplicationGlobalDraftSaveAsPDFSheetOptions,ref oldPdf);restorePdf=true;app.SetGlobalParameter(F.ApplicationGlobalConstants.seApplicationGlobalDraftSaveAsPDFSheetOptions,1);}
                ((dynamic)clone).SaveAs(target);
                if(pdf){using(var s=File.OpenRead(target)){byte[] b=new byte[5];if(s.Read(b,0,5)!=5||Encoding.ASCII.GetString(b)!="%PDF-")throw new IOException("Native container is not a standard PDF");}}
                else ValidateStep(target);
                return target.Substring(output.Length+1).Replace('\\','/');
            }catch{if(File.Exists(target))File.Move(target,target+".invalid");throw;}
            finally{if(restorePdf)app.SetGlobalParameter(F.ApplicationGlobalConstants.seApplicationGlobalDraftSaveAsPDFSheetOptions,oldPdf);if(clone!=null)try{if(string.Equals(Path.GetFullPath((string)((dynamic)clone).FullName),source,StringComparison.OrdinalIgnoreCase)||string.Equals(Path.GetFullPath((string)((dynamic)clone).FullName),target,StringComparison.OrdinalIgnoreCase))((dynamic)clone).Close(false);}catch{}if(previous!=null)try{((dynamic)previous).Activate();}catch{}}
        }
        public static void ValidateStep(string path){if(!File.Exists(path))throw new IOException("STEP exporter did not create a file");using(var s=new StreamReader(path)){char[] buffer=new char[256];int n=s.Read(buffer,0,buffer.Length);if(!new string(buffer,0,n).TrimStart('\uFEFF',' ','\r','\n').StartsWith("ISO-10303-21;",StringComparison.Ordinal))throw new IOException("Export is not ISO-10303-21 STEP; native container rejected");}}
        static string Reference(dynamic entity){Array key=new byte[0];object size=0;entity.GetReferenceKey(ref key,ref size);var bytes=new byte[key.Length];for(int i=0;i<bytes.Length;i++)bytes[i]=Convert.ToByte(key.GetValue(i));return Convert.ToBase64String(bytes);}
        Dictionary<string,object> Body(G.Body body,string id){
            string modelId=id.Substring(0,id.IndexOf('/'));var model=models.Values.First(m=>(string)m["id"]==modelId);
            string relative=Path.GetDirectoryName((string)model["native_file"]).Replace('\\','/')+"/"+id.Substring(id.IndexOf('/')+1)+".obj";
            var row=new TrainingGeometryReader(output,relative,id).Export(body);
            foreach(var issue in (List<object>)row["issues"])issues.Add(issue);return row;
        }
        Dictionary<string,object> Drawing(D.DraftDocument d){
            var result=Obj("native_file",NativeCopy(d,"drawing/source.dft"));pendingDrawing=result;var sheets=new List<object>();result["sheets"]=sheets;
            for(int si=1;si<=d.Sheets.Count;si++){var sheetForIds=d.Sheets.Item(si);for(int vi=1;vi<=sheetForIds.DrawingViews.Count;vi++){var v=sheetForIds.DrawingViews.Item(vi);viewKeys[v.Key]="sheet-"+si+"/view-"+vi;}}
            for(int i=1;i<=d.Sheets.Count;i++){
                D.Sheet sheet=d.Sheets.Item(i);string sid="sheet-"+i;var sr=Obj("id",sid,"name",sheet.Name);sheets.Add(sr);
                Props(sr,sid,"width_m",Fn(()=>sheet.SheetSetup.SheetWidth),"height_m",Fn(()=>sheet.SheetSetup.SheetHeight),"section_type",Fn(()=>sheet.SectionType.ToString()),"visible",Fn(()=>sheet.Visible),"background",Fn(()=>{if(!sheet.BackgroundVisible)return null;var bg=sheet.Background;return bg==null?null:bg.Name;}));
                var views=new List<object>();sr["views"]=views;
                for(int v=1;v<=sheet.DrawingViews.Count;v++)drawingIds[TrainingNative.Identity(sheet.DrawingViews.Item(v))]=sid+"/view-"+v;
                for(int v=1;v<=sheet.DrawingViews.Count;v++){D.DrawingView view=sheet.DrawingViews.Item(v);string vid=sid+"/view-"+v;var vr=Obj("id",vid);views.Add(vr);
                    Props(vr,vid,"name",Fn(()=>view.Name),"view_type",Fn(()=>view.DrawingViewType.ToString()),"scale",Fn(()=>view.ScaleFactor),"crop",Fn(()=>view.Crop),"caption",Fn(()=>view.CaptionDisplayedTextPrimary),"bbox_sheet_m",Fn(()=>{double a,b,c,e;view.Range(out a,out b,out c,out e);return new[]{a,b,c,e};}),"model_id",Fn(()=>{var link=(D.ModelLink)view.ModelLink;if(link.ModelOutOfDate)issues.Add(Obj("location",vid,"error","model link is out of date"));return Model(link.ModelDocument);}),"model_to_view_affine",Fn(()=>Projection(view,false)),"model_to_sheet_affine",Fn(()=>Projection(view,true)));
                    vr["projection_note"]="2x4 row-major affine from native API probes; broken/cropped/detail/section views require additional clipping and visibility semantics";
                    vr["view_to_sheet_affine"]=Read(vid+"/view_to_sheet",()=>{double ox,oy,xx,xy,yx,yy;view.ViewToSheet(0,0,out ox,out oy);view.ViewToSheet(1,0,out xx,out xy);view.ViewToSheet(0,1,out yx,out yy);return new[]{new[]{xx-ox,yx-ox,ox},new[]{xy-oy,yy-oy,oy}};});
                    vr["orientation_native"]=Read(vid+"/orientation",()=>{double dx,dy,dz,ux,uy,uz;D.ViewOrientationConstants kind;view.ViewOrientation(out dx,out dy,out dz,out ux,out uy,out uz,out kind);double angle,ox,oy;view.GetRotationAngle(out angle);view.GetOrigin(out ox,out oy);return Obj("view_direction",new[]{dx,dy,dz},"local_x_direction",new[]{ux,uy,uz},"kind",kind.ToString(),"rotation_rad",angle,"origin_sheet_m",new[]{ox,oy});});
                    vr["lines"]=Read(vid+"/lines",()=>Lines(view,vid));
                    vr["arcs"]=Read(vid+"/arcs",()=>Curves(view.DVArcs2d,vid,"arc"));
                    vr["circles"]=Read(vid+"/circles",()=>Curves(view.DVCircles2d,vid,"circle"));
                    vr["other_primitive_counts"]=Read(vid+"/other_primitives",()=>Obj("bsplines",view.DVBSplineCurves2d.Count,"ellipses",view.DVEllipses2d.Count,"elliptical_arcs",view.DVEllipticalArcs2d.Count,"line_strings",view.DVLineStrings2d.Count,"points",view.DVPoints2d.Count));
                    vr["section_data"]=Read(vid+"/section_data",()=>new TrainingSectionReader(ViewId).Export(view,vid));
                }
                var dims=new List<object>();sr["dimensions"]=dims;
                Read(sid+"/dimensions",()=>{var ds=(S.Dimensions)sheet.Dimensions;for(int j=1;j<=ds.Count;j++)dims.Add(Dimension(ds.Item(j),sid+"/dimension-"+j));return true;});
                var annotationReader=new TrainingAnnotationReader(GraphicReference);sr["annotation_export"]=annotationReader.Export(sheet,sid);sr["annotations"]=annotationReader.Items;
                var texts=new List<object>();sr["texts"]=texts;Read(sid+"/texts",()=>{dynamic ts=sheet.TextBoxes;for(int j=1;j<=(int)ts.Count;j++){S.TextBox t=(S.TextBox)ts.Item(j);double a,b,c,e;t.Range(out a,out b,out c,out e);texts.Add(Obj("id",sid+"/text-"+j,"text",t.Text,"bbox_sheet_m",new[]{a,b,c,e}));}return true;});
            }
            return result;
        }
        public static double[][] Projection(D.DrawingView v,bool sheet){var ps=new[]{new[]{0.0,0.0,0.0},new[]{1.0,0.0,0.0},new[]{0.0,1.0,0.0},new[]{0.0,0.0,1.0}};var uv=new double[4][];for(int i=0;i<4;i++){double x,y;v.ModelToView(ps[i][0],ps[i][1],ps[i][2],out x,out y);if(sheet){double sx,sy;v.ViewToSheet(x,y,out sx,out sy);x=sx;y=sy;}uv[i]=new[]{x,y};}return new[]{new[]{uv[1][0]-uv[0][0],uv[2][0]-uv[0][0],uv[3][0]-uv[0][0],uv[0][0]},new[]{uv[1][1]-uv[0][1],uv[2][1]-uv[0][1],uv[3][1]-uv[0][1],uv[0][1]}};}
        List<object> Lines(D.DrawingView v,string id){var lines=new List<object>();dynamic collection=v.DVLines2d;for(int i=1;i<=(int)collection.Count;i++){D.DVLine2d l=(D.DVLine2d)collection.Item(i);double x,y,u,w;l.GetStartPoint(out x,out y);l.GetEndPoint(out u,out w);var r=Obj("id",id+"/line-"+i,"start_native_m",new[]{x,y},"end_native_m",new[]{u,w});drawingIds[TrainingNative.Identity(l)]=(string)r["id"];r["edge_type"]=l.EdgeType.ToString();r["native_reference"]=Read((string)r["id"],()=>GraphicReference(l));lines.Add(r);}return lines;}
        object GraphicReference(object entity){
            if(entity==null)return null;
            var wrapped=entity as F.Reference;if(wrapped!=null)entity=wrapped.Object;
            var line=entity as D.DVLine2d;var arc=entity as D.DVArc2d;var circle=entity as D.DVCircle2d;
            if(line==null&&arc==null&&circle==null)return Obj("resolution","unsupported_entity_type");
            D.DrawingView view=line!=null?line.DrawingView:arc!=null?arc.DrawingView:circle.DrawingView;
            string drawingKey=line!=null?line.Key:arc!=null?arc.Key:circle.Key;
            var type=line!=null?line.EdgeType:arc!=null?arc.EdgeType:circle.EdgeType;
            if(type!=D.GraphicMemberEdgeTypeConstants.seModelEdgeType)return Obj("resolution","not_a_model_edge","edge_type",type.ToString(),"view_id",ViewId(view),"drawing_reference_key",drawingKey);
            string file="";Array key=new byte[0];int count=0;
            // Typed COM calls are required here: TianGong's IDispatch marshaler
            // rejects late-bound BSTR / SAFEARRAY output parameters.
            if(line!=null)line.GetReferenceKey(out file,out key,out count);
            else if(arc!=null)arc.GetReferenceKey(out file,out key,out count);
            else if(circle!=null)circle.GetReferenceKey(out file,out key,out count);
            else return Obj("resolution","unsupported_entity_type");
            var bytes=new byte[key.Length];for(int i=0;i<bytes.Length;i++)bytes[i]=Convert.ToByte(key.GetValue(i));
            var result=Obj("model_path",file,"reference_key",Convert.ToBase64String(bytes),"key_bytes",count);
            result["drawing_geometry_id"]=null;result["drawing_reference_key"]=drawingKey;result["view_id"]=ViewId(view);
            result["bound_edge"]=Read("graphic_reference/bind",()=>{
                object doc=((D.ModelLink)view.ModelLink).ModelDocument,bound=null;Dictionary<string,object> targetModel;if(models.TryGetValue((string)((dynamic)doc).FullName,out targetModel))result["target_model_id"]=targetModel["id"];Array bindKey=bytes;
                var part=doc as SolidEdgePart.PartDocument;var metal=doc as SolidEdgePart.SheetMetalDocument;
                if(part!=null)part.BindKeyToObject(ref bindKey,out bound);else if(metal!=null)metal.BindKeyToObject(ref bindKey,out bound);else if(doc is SolidEdgeAssembly.AssemblyDocument){var native=line!=null?line.Reference:arc!=null?arc.Reference:circle.Reference;var member=line!=null?line.ModelMember:arc!=null?arc.ModelMember:circle.ModelMember;return AssemblyReference((SolidEdgeAssembly.AssemblyDocument)doc,native,member,bytes,result);}else return null;
                var edge=bound as G.Edge;if(edge==null)return null;
                return Obj("native_id",edge.ID,"canonical_reference_key",Reference(edge));
            });
            return result;
        }
        string ViewId(D.DrawingView view){if(view==null)return null;string id;return viewKeys.TryGetValue(view.Key,out id)?id:null;}
        object AssemblyReference(SolidEdgeAssembly.AssemblyDocument asm,F.Reference native,D.ModelMember member,byte[] edgeKey,Dictionary<string,object> result){
            result["mapping_status"]="unsupported";var path=new List<object>();result["occurrence_records"]=path;object top=null;int total=0,boundCount=0;Array subs=new object[0];var localIssues=new List<object>();result["assembly_mapping_issues"]=localIssues;
            if(native!=null)TrainingNative.Read(localIssues,"assembly/reference_path",()=>{native.GetOccurrencesInPath(out top,out total,out boundCount,ref subs);return true;});
            if(top==null&&member!=null)TrainingNative.Read(localIssues,"assembly/model_node_key",()=>{Array key=new byte[0];object size=0;member.ModelNode.GetAssemblyReferenceKey(ref key,out size);object bound;asm.BindKeyToObject(ref key,out bound);var reference=bound as F.Reference;if(reference!=null)reference.GetOccurrencesInPath(out top,out total,out boundCount,ref subs);else top=bound;return true;});
            if(top==null){result["occurrence_path"]=null;return null;}
            var chain=new List<object>{top};foreach(object sub in subs)if(sub!=null)chain.Add(sub);object leafDoc=null;bool hasOverride=false;
            foreach(object item in chain){var occ=item as SolidEdgeAssembly.Occurrence;var sub=item as SolidEdgeAssembly.SubOccurrence;if(occ==null&&sub==null)continue;string key=Reference(item),oid;var assemblyRow=models.Values.First(m=>(string)m["id"]==Model(asm));var nativeMatches=TrainingDataset.Rows(TrainingDataset.Get(assemblyRow,"occurrences")).Where(o=>Convert.ToString(TrainingDataset.Get(o,"reference_key"))==key).ToArray();if(nativeMatches.Length==1)oid=(string)nativeMatches[0]["id"];else if(!occurrenceIds.TryGetValue(TrainingNative.Identity(item),out oid))oid="occurrence-"+TrainingDataset.Digest(key).Substring(0,24);
                leafDoc=occ!=null?occ.OccurrenceDocument:sub.SubOccurrenceDocument;bool bodyOverride=occ!=null?occ.HasBodyOverride:sub.HasBodyOverride;hasOverride|=bodyOverride;path.Add(Obj("id",oid,"reference_key",key,"model_id",Model(leafDoc),"has_body_override",bodyOverride));
            }
            result["occurrence_path"]=TrainingDataset.Rows(path).Select(p=>(string)p["id"]).ToArray();result["native_suboccurrence_count"]=total;result["bound_suboccurrence_count"]=boundCount;
            if(path.Count==0||leafDoc==null||hasOverride||total!=boundCount){result["mapping_status"]=hasOverride?"unsupported_body_override":"unresolved_occurrence_path";return null;}
            result["target_model_id"]=Model(leafDoc);object boundEdge=null;Array bind=edgeKey;
            var part=leafDoc as SolidEdgePart.PartDocument;var metal=leafDoc as SolidEdgePart.SheetMetalDocument;if(part!=null)part.BindKeyToObject(ref bind,out boundEdge);else if(metal!=null)metal.BindKeyToObject(ref bind,out boundEdge);else return null;
            var edge=boundEdge as G.Edge;if(edge==null)return null;result["mapping_source"]="native_occurrence_path_and_part_BindKeyToObject";return Obj("native_id",edge.ID,"canonical_reference_key",Reference(edge));
        }
        List<object> Curves(dynamic collection,string vid,string kind){var curves=new List<object>();for(int i=1;i<=(int)collection.Count;i++){dynamic c=collection.Item(i);double x,y;c.GetCenterPoint(out x,out y);var r=Obj("id",vid+"/"+kind+"-"+i,"center_native_m",new[]{x,y},"radius_m",(double)c.Radius,"edge_type",c.EdgeType.ToString());drawingIds[TrainingNative.Identity((object)c)]=(string)r["id"];curves.Add(r);r["native_reference"]=Read((string)r["id"],()=>GraphicReference(c));if(kind=="arc"){r["start_angle_rad"]=(double)c.StartAngle;r["sweep_angle_rad"]=(double)c.SweepAngle;}}return curves;}
        Dictionary<string,object> Dimension(S.Dimension d,string id){
            var r=Obj("id",id);Props(r,id,"dimension_type",Fn(()=>d.DimensionType.ToString()),"value_native",Fn(()=>d.Value),"units_type",Fn(()=>d.UnitsType),"status",Fn(()=>d.StatusOfDimension.ToString()),"override",Fn(()=>d.OverrideString),"upper_tolerance_text",Fn(()=>d.PrimaryUpperTolerance),"lower_tolerance_text",Fn(()=>d.PrimaryLowerTolerance),"prefix_text",Fn(()=>d.PrefixDisplayedText),"suffix_text",Fn(()=>d.SuffixDisplayedText),"display_type",Fn(()=>d.DisplayType.ToString()),"bbox_sheet_m",Fn(()=>{double a,b,c,e;d.Range(out a,out b,out c,out e);return new[]{a,b,c,e};}));
            r["layout"]=Read(id+"/layout",()=>TrainingAnnotationReader.DimensionLayout(d));
            r["value_m"]=d.UnitsType==(int)F.UnitTypeConstants.igUnitDistance?(object)d.Value:null;
            r["value_rad"]=d.UnitsType==(int)F.UnitTypeConstants.igUnitAngle?(object)d.Value:null;
            var refs=new List<object>();r["related_geometry"]=refs;Read(id+"/related",()=>{int n;d.GetRelatedCount(out n);for(int j=0;j<n;j++){object entity;double x,y,z;bool kp;d.GetRelated(j,out entity,out x,out y,out z,out kp);var rr=Obj("index",j,"point_native",new[]{x,y,z},"keypoint",kp);refs.Add(rr);var reference=entity as F.Reference;if(reference!=null)entity=reference.Object;rr["native_type"]=Read(id+"/related/type",()=>((dynamic)entity).Type.ToString());rr["native_reference"]=Read(id+"/related/reference",()=>GraphicReference(entity));}return true;});return r;
        }
    }
}
