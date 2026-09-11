using System;
using System.Collections.Generic;
using D=SolidEdgeDraft;
namespace TianGongCadSuite {
    public sealed class TrainingSectionReader {
        readonly Func<D.DrawingView,string> viewId;readonly List<object> issues=new List<object>();
        public TrainingSectionReader(Func<D.DrawingView,string> resolver){viewId=resolver;}
        static Dictionary<string,object> O(params object[] p){return TrainingExporter.Obj(p);}
        object Read(string id,Func<object> get){return TrainingNative.Read(issues,id,get);}
        object View(D.DrawingView v){return v==null?null:viewId(v);}
        public object Export(D.DrawingView view,string id){
            var r=O("issues",issues,"source","native","parent_view",null,"section_line_coordinate_status","native_profile_frame_unverified","section_depth_m",null,"section_depth_status","not_applicable_or_unsupported");
            bool section=view.DrawingViewType==D.DrawingViewTypeConstants.igXSectionView||view.DrawingViewType==D.DrawingViewTypeConstants.igIsoXSectionView||view.DrawingViewType==D.DrawingViewTypeConstants.igRevolvedSectionView,detail=view.DrawingViewType==D.DrawingViewTypeConstants.igDetailView;
            r["type"]=section?"section":detail?"detail":"regular";r["is_broken"]=view.IsBroken;r["is_broken_out_section_target"]=view.IsBrokenOutSectionTarget;
            if(section||detail)r["parent_view"]=Read(id+"/source_view",()=>View(view.SourceDrawingView));
            if(section){r["section_only"]=view.SectionOnly;r["section_full_model"]=view.SectionFullModel;r["revolved_section"]=view.RevolvedSection;}
            var cuts=new List<object>();r["cutting_planes"]=cuts;
            Read(id+"/cutting_planes",()=>{var collection=view.CuttingPlanes;for(int i=1;i<=collection.Count;i++){var cut=collection.Item(i);string cid=id+"/cut-"+i;var item=O("id",cid);cuts.Add(item);
                item["parent_view"]=Read(cid+"/parent",()=>View(cut.SourceDrawingView));item["child_view"]=Read(cid+"/child",()=>View(cut.SectionView));item["view_side"]=cut.ViewSide.ToString();item["fold_segment"]=cut.FoldSegment;
                item["fold_line_native_m"]=Read(cid+"/fold_line",()=>TrainingAnnotationReader.Outputs(cut,typeof(D.CuttingPlane),"GetFoldLineWithViewDirection"));item["profile"]=Read(cid+"/profile",()=>Profile(cut.Profile));
                item["caption"]=cut.CaptionDisplayedText;item["arrow_style"]=cut.TerminatorType.ToString();item["section_arrows_sheet_m"]=null;item["arrow_positions_status"]="unsupported_separate_native_arrow_positions";
            }return true;});
            var details=new List<object>();r["detail_envelopes"]=details;
            Read(id+"/details",()=>{var collection=view.DetailEnvelopes;for(int i=1;i<=collection.Count;i++){var d=collection.Item(i);var item=O("id",id+"/detail-"+i,"parent_view",View(d.SourceDrawingView),"child_view",View(d.DetailView),"diameter_m",d.Diameter,"display_as_circle",d.DisplayAsCircle);details.Add(item);item["center_native_m"]=Read(id+"/detail_center",()=>{double x,y;d.GetCenterPoint(out x,out y);return new[]{x,y};});item["profile"]=Read(id+"/detail_profile",()=>Profile(d.Profile));}return true;});
            var broken=new List<object>();r["broken_out_profiles"]=broken;
            Read(id+"/broken_out",()=>{var collection=view.BrokenOutSectionProfiles;for(int i=1;i<=collection.Count;i++){var b=collection.Item(i);broken.Add(O("id",id+"/broken-out-"+i,"child_view",View(b.TargetView),"depth_m",b.Depth,"depth_plane_offset_m",b.DepthPlaneOffset,"profile",Profile(b.Profile)));}return true;});
            r["status"]=issues.Count==0?"success":"partial";return r;
        }
        static object Profile(D.DraftProfile p){if(p==null)return null;var lines=new List<object>();for(int i=1;i<=p.Lines2d.Count;i++){var l=p.Lines2d.Item(i);double x,y,u,v;l.GetStartPoint(out x,out y);l.GetEndPoint(out u,out v);lines.Add(O("start_m",new[]{x,y},"end_m",new[]{u,v}));}var circles=new List<object>();for(int i=1;i<=p.Circles2d.Count;i++){var c=p.Circles2d.Item(i);double x,y;c.GetCenterPoint(out x,out y);circles.Add(O("center_m",new[]{x,y},"radius_m",c.Radius));}return O("lines",lines,"circles",circles,"arc_count",p.Arcs2d.Count,"bspline_count",p.BsplineCurves2d.Count,"coordinate_system","native_profile_frame_unverified");}
    }
}
