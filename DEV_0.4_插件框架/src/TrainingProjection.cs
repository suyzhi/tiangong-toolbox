using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
namespace TianGongCadSuite {
    public static class TrainingProjection {
        static object Get(Dictionary<string,object> d,string k){return TrainingDataset.Get(d,k);}
        static double[] V(object x){return ((IEnumerable)x).Cast<object>().Select(Convert.ToDouble).ToArray();}
        static double[][] Matrix(object x){return ((IEnumerable)x).Cast<object>().Select(V).ToArray();}
        static double Dot(double[] a,double[] b){return a.Zip(b,(x,y)=>x*y).Sum();}
        static double Dist(double[] a,double[] b){return Math.Sqrt(a.Zip(b,(x,y)=>(x-y)*(x-y)).Sum());}
        static double Median(IEnumerable<double> values){var a=values.OrderBy(v=>v).ToArray();return (a[(a.Length-1)/2]+a[a.Length/2])/2;}
        public static void Apply(Dictionary<string,object> root){
            var models=TrainingDataset.Rows(Get(root,"models")).ToDictionary(m=>(string)m["id"]);
            foreach(var sheet in TrainingDataset.Rows(Get(Get(root,"drawing") as Dictionary<string,object>,"sheets")))foreach(var view in TrainingDataset.Rows(Get(sheet,"views"))){
                var errors=new List<object>();view["projection_validation"]=TrainingNative.Read(errors,(string)view["id"]+"/projection_validation",()=>Derive(view,models));view["projection_issues"]=errors;
            }
        }
        static object Derive(Dictionary<string,object> view,Dictionary<string,Dictionary<string,object>> models){
            var result=TrainingExporter.Obj("matrix_status","unavailable","derivation","native orientation plus translation validated against every bound line endpoint","model_to_sheet_affine",null);
            Dictionary<string,object> model;var ori=Get(view,"orientation_native") as Dictionary<string,object>;
            if(!models.TryGetValue(Convert.ToString(Get(view,"model_id")),out model)||ori==null||Convert.ToString(Get(model,"kind"))=="assembly")return result;
            double[] x=V(ori["local_x_direction"]),n=V(ori["view_direction"]),y={n[1]*x[2]-n[2]*x[1],n[2]*x[0]-n[0]*x[2],n[0]*x[1]-n[1]*x[0]};
            if(Math.Abs(Dot(x,x)-1)>1e-7||Math.Abs(Dot(n,n)-1)>1e-7||Math.Abs(Dot(x,n))>1e-7)return result;
            var edges=TrainingDataset.Rows(Get(model,"bodies")).SelectMany(b=>TrainingDataset.Rows(Get(b,"edges"))).ToDictionary(e=>(string)e["id"]);
            var pairs=new List<Tuple<double[][],double[][]>>();
            foreach(var line in TrainingDataset.Rows(Get(view,"lines"))){var reference=Get(line,"native_reference") as Dictionary<string,object>;var target=Get(reference,"native_geometry") as Dictionary<string,object>;Dictionary<string,object> edge;
                if(target==null||!edges.TryGetValue(Convert.ToString(Get(target,"id")),out edge)||Convert.ToString(Get(edge,"curve_kind"))!="line"||Get(edge,"endpoints_m")==null)continue;
                var p=Matrix(edge["endpoints_m"]);if(Dist(p[0],p[1])<1e-9)continue;pairs.Add(Tuple.Create(p,new[]{V(line["start_native_m"]),V(line["end_native_m"])}));
            }
            result["verified_line_count"]=pairs.Count;if(pairs.Count<3)return result;
            double[] offset={Median(pairs.Select(p=>(p.Item2[0][0]+p.Item2[1][0]-Dot(x,p.Item1[0])-Dot(x,p.Item1[1]))/2)),Median(pairs.Select(p=>(p.Item2[0][1]+p.Item2[1][1]-Dot(y,p.Item1[0])-Dot(y,p.Item1[1]))/2))};
            double max=0;var spread=new List<double[]>();foreach(var pair in pairs){var p=pair.Item1.Select(point=>new[]{Dot(x,point)+offset[0],Dot(y,point)+offset[1]}).ToArray();var q=pair.Item2;max=Math.Max(max,Math.Min(Math.Max(Dist(p[0],q[0]),Dist(p[1],q[1])),Math.Max(Dist(p[0],q[1]),Dist(p[1],q[0]))));spread.AddRange(q);}
            double mx=spread.Average(p=>p[0]),my=spread.Average(p=>p[1]),xx=spread.Sum(p=>(p[0]-mx)*(p[0]-mx)),yy=spread.Sum(p=>(p[1]-my)*(p[1]-my)),xy=spread.Sum(p=>(p[0]-mx)*(p[1]-my));
            result["max_endpoint_error_m"]=max;if(max>1e-6||xx*yy-xy*xy<=1e-20){result["matrix_status"]="rejected_geometry_mismatch_or_degenerate_spread";return result;}
            var affine=new[]{new[]{x[0],x[1],x[2],offset[0]},new[]{y[0],y[1],y[2],offset[1]}};result["model_to_view_affine"]=affine;result["matrix_status"]="verified_against_model_edges";
            if(Get(view,"view_to_sheet_affine")!=null){var paper=Matrix(view["view_to_sheet_affine"]);var combined=new[]{new double[4],new double[4]};for(int i=0;i<2;i++)for(int j=0;j<4;j++)combined[i][j]=paper[i][0]*affine[0][j]+paper[i][1]*affine[1][j]+(j==3?paper[i][2]:0);result["model_to_sheet_affine"]=combined;
                if(Get(view,"model_to_sheet_affine")==null){view["model_to_sheet_affine"]=combined;view["model_to_view_affine"]=affine;view["projection_source"]="verified_native_edge_derivation";}
            }return result;
        }
    }
}
