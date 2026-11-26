using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using A=SolidEdgeAssembly;
namespace TianGongCadSuite {
    public sealed class LineupCadPartMeta {
        public string FileName="",Title="",Model="",Brand="",Connector="",Category="",Mechanism="";
        // 标记该条目是由型号库精确匹配或厂商标准型号编码命中的外购组件（而非纯中文机构名关键词猜测）
        internal bool MatchedStandardModel;
    }
    public sealed class LineupCadScanItem {
        public string ReferenceKey="",SourceFile="",Category="",Model="",Brand="",Connector="",Description="",Mechanism="";
        public string[] InstancePath=new string[0];
        public bool AlreadyBound;
        public string DisplayPath { get { return string.Join(" / ", InstancePath ?? new string[0]); } }
    }
    // Association is instance-specific. Never silently bind a stale key by display name.
    public static class LineupCad {
        public static string AssemblyFile(A.AssemblyDocument doc) {
            string file=doc.FullName;
            if(string.IsNullOrEmpty(doc.Path)||!Path.IsPathRooted(file)||!File.Exists(file))
                throw new InvalidOperationException("请先保存当前装配，再使用 Lineup。");
            return Path.GetFullPath(file);
        }
        public static void Capture(A.AssemblyDocument doc,LineupRecord record) {
            AssemblyFile(doc);
            if(doc.SelectSet.Count!=1)throw new InvalidOperationException("请在装配中选择一个零件或子装配实例。");
            CaptureOccurrence(doc.SelectSet.Item(1),record);
        }
        public static object Instance(object selected) {
            var instance=OccurrencePick.Instance(selected);
            if(instance!=null)return instance;
            throw new InvalidOperationException("无法识别此选择的零件实例。请在装配树中选择具体零件（支持子装配内部零件），再加入或绑定。");
        }
        public static void CaptureOccurrence(object selected,LineupRecord record) {
            object instance=Instance(selected);var occurrence=instance as A.Occurrence;var sub=instance as A.SubOccurrence;
            Array key=new byte[0];object context;
            if(occurrence!=null)occurrence.GetReferenceKey(ref key,out context);else sub.GetReferenceKey(ref key,out context);
            if(key==null||key.Length==0)throw new InvalidOperationException("CAD 未返回持久化关联键。");
            record.ReferenceKey=Convert.ToBase64String(key.Cast<object>().Select(Convert.ToByte).ToArray());
            record.InstancePath=occurrence!=null?new[]{occurrence.Name}:InstanceNames(sub);record.SourceFile=occurrence!=null?occurrence.OccurrenceFileName:sub.SubOccurrenceFileName;
        }
        static string[] InstanceNames(A.SubOccurrence sub){
            object top;int count,bound;Array path=new object[0];sub.Reference.GetOccurrencesInPath(out top,out count,out bound,ref path);
            var names=new List<string>();var root=top as A.Occurrence;if(root!=null)names.Add(root.Name);
            if(path!=null)foreach(object item in path){var child=item as A.SubOccurrence;if(child!=null)names.Add(child.Name);}
            if(names.Count<2)names.Add(sub.Name);return names.ToArray();
        }
        public static object HighlightTarget(object target){var sub=target as A.SubOccurrence;return sub==null?target:sub.Reference;}
        public static A.Occurrence Resolve(A.AssemblyDocument doc,LineupRecord record) {
            var occurrence=ResolveTarget(doc,record) as A.Occurrence;
            if(occurrence==null)throw new InvalidOperationException("此记录关联子装配内部零件，请使用五类清单窗口定位。");
            return occurrence;
        }
        public static object ResolveTarget(A.AssemblyDocument doc,LineupRecord record) {
            if(string.IsNullOrEmpty(record.ReferenceKey))throw new InvalidOperationException("此记录没有 CAD 持久化关联键，请重新读取当前选择并保存。");
            Array key=Convert.FromBase64String(record.ReferenceKey);object target=null;
            doc.BindKeyToObject(ref key,out target);
            if(!(target is A.Occurrence)&&!(target is A.SubOccurrence))throw new InvalidOperationException("关联模型已失效，请重新选择模型绑定。");
            return target;
        }

        // 从零件路径、装配路径及属性卡自动提取型号/品牌/接口/机构名（不打开文档，加密件安全可读）
        public static LineupCadPartMeta ReadPartMeta(string sourceFile,string[] instancePath,LineupModelCatalog catalog,string preferredCategory=null){
            return ReadPartMetaCore(sourceFile,instancePath,catalog,preferredCategory,null,null);
        }

        static LineupCadPartMeta ReadPartMetaCore(string sourceFile,string[] instancePath,LineupModelCatalog catalog,string preferredCategory,object sharedFp,Type sharedFpType){
            var meta=new LineupCadPartMeta{FileName=string.IsNullOrEmpty(sourceFile)?"":Path.GetFileNameWithoutExtension(sourceFile)};
            if(instancePath!=null&&instancePath.Length>=2){
                meta.Mechanism=StripOccSuffix(instancePath[0]);
            }
            string propName="",propModel="",propBrand="";
            ReadFilePropsSafe(sourceFile,out propName,out propModel,out propBrand,sharedFp,sharedFpType);
            meta.Title=propName;
            meta.Model=propModel;
            meta.Brand=propBrand;

            if(catalog!=null){
                LineupModelEntry hit=null;
                if(!string.IsNullOrWhiteSpace(propModel))hit=catalog.MatchByFileName(propModel,preferredCategory);
                if(hit==null&&!string.IsNullOrWhiteSpace(meta.FileName))hit=catalog.MatchByFileName(meta.FileName,preferredCategory);
                if(hit==null&&!string.IsNullOrWhiteSpace(propName))hit=catalog.MatchByFileName(propName,preferredCategory);
                if(hit!=null){
                    meta.Category=hit.Category;
                    meta.Model=hit.Model;
                    meta.Brand=hit.Brand;
                    meta.Connector=hit.Connector;
                    meta.MatchedStandardModel=true;
                }
            }
            if(string.IsNullOrEmpty(meta.Category)){
                bool matchedCode;
                meta.Category=GuessCategory(meta.FileName+" "+propName+" "+meta.Model,out matchedCode);
                if(matchedCode)meta.MatchedStandardModel=true;
            }
            return meta;
        }

        static string StripOccSuffix(string name){
            if(string.IsNullOrEmpty(name))return "";
            string s=Regex.Replace(name,@":\d+$","").Trim();
            s=Regex.Replace(s,@"\.(asm|par|psm)$","",RegexOptions.IgnoreCase).Trim();
            return s;
        }

        static string GuessCategory(string text,out bool matchedModelCode){
            matchedModelCode=false;
            if(string.IsNullOrWhiteSpace(text))return "";
            string u=text.ToUpperInvariant();
            // 标准件型号前缀后常紧跟缸径/行程等字母数字（如 CDQ2B50-20TZ、SY5400-5UD1、D-M9B），
            // 绝不能加尾部 \b，只保留左侧前缀边界避免误伤普通英文单词
            if(Regex.IsMatch(u,@"(?<![A-Z0-9])(CDQ2|CQ2|MGPM|MGPK|MDB|CDM2|CDS2|MBB|MXS|MXQ|CXSM|MHQ|MHL|MHF|HMF\d|CJ2|CG1|CP96)")){matchedModelCode=true;return "气缸";}
            if(Regex.IsMatch(u,@"(?<![A-Z0-9])(SY5\d|SY7\d|SY3\d|VFS|VQ\d|VQZ|SS5Y|ZK2H|EX260|EX600)")){matchedModelCode=true;return "阀片";}
            if(Regex.IsMatch(u,@"(?<![A-Z0-9])(D-M9|D_M9|D-Y59|D-A9|CS1-)")){matchedModelCode=true;return "气缸传感器";}
            if(Regex.IsMatch(u,@"(?<![A-Z0-9])(E2B|E2E|E3Z|E3S|E3AS|WLG2|T4VH|GL-V|F3SG|EE-SX|TL-W)")){matchedModelCode=true;return "普通传感器";}
            if(Regex.IsMatch(u,@"(?<![A-Z0-9])(V680|RNYM|MV-CS|MV-CA|AC40|AC30|AC20|AR20|AF20|D4SL|D4NL|SICK|KEYENCE)")){matchedModelCode=true;return "其他电气件";}
            if(text.Contains("气缸")||text.Contains("夹爪")||text.Contains("手指"))return "气缸";
            if(text.Contains("电磁阀")||text.Contains("阀岛")||text.Contains("阀片")||text.Contains("汇流排"))return "阀片";
            if(text.Contains("磁开")||text.Contains("磁性开关"))return "气缸传感器";
            if(text.Contains("接近开关")||text.Contains("光电")||text.Contains("传感器")||text.Contains("行程开关")||text.Contains("限位"))return "普通传感器";
            if(text.Contains("伺服")||text.Contains("电机")||text.Contains("光栅")||text.Contains("门锁")||text.Contains("按钮盒")||text.Contains("RFID")||text.Contains("相机")||text.Contains("三联件")||text.Contains("扫码")||text.Contains("三色灯"))return "其他电气件";
            return "";
        }

        static void ReleaseComSafe(object obj){
            if(obj!=null&&Marshal.IsComObject(obj)){
                try{Marshal.ReleaseComObject(obj);}catch{}
            }
        }

        static void ReadFilePropsSafe(string path,out string name,out string model,out string brand,object sharedFp,Type sharedFpType){
            name="";model="";brand="";
            if(string.IsNullOrEmpty(path)||!File.Exists(path))return;
            object fp=sharedFp;
            Type tp=sharedFpType;
            bool ownsFp=false;
            try{
                if(fp==null){
                    tp=Type.GetTypeFromProgID("SolidEdge.FileProperties");
                    if(tp==null)return;
                    fp=Activator.CreateInstance(tp);
                    ownsFp=true;
                }else if(tp==null){
                    tp=fp.GetType();
                }
                tp.InvokeMember("Open",BindingFlags.InvokeMethod,null,fp,new object[]{path,true});
                try{
                    object sets=null;
                    try{
                        sets=tp.InvokeMember("Item",BindingFlags.GetProperty,null,fp,new object[]{"Custom"});
                        if(sets!=null){
                            name=ReadPropVal(sets,"零件名称");
                            if(name.Length==0)name=ReadPropVal(sets,"零件名");
                            if(name.Length==0)name=ReadPropVal(sets,"名称");
                            model=ReadPropVal(sets,"型号");
                            if(model.Length==0)model=ReadPropVal(sets,"规格");
                            if(model.Length==0)model=ReadPropVal(sets,"图号");
                            brand=ReadPropVal(sets,"品牌");
                            if(brand.Length==0)brand=ReadPropVal(sets,"厂家");
                            if(brand.Length==0)brand=ReadPropVal(sets,"供应商");
                        }
                    }finally{ReleaseComSafe(sets);}
                    if(name.Length==0){
                        object sum=null;
                        try{
                            sum=tp.InvokeMember("Item",BindingFlags.GetProperty,null,fp,new object[]{"SummaryInformation"});
                            if(sum!=null)name=ReadPropVal(sum,"Title");
                        }finally{ReleaseComSafe(sum);}
                    }
                }finally{try{tp.InvokeMember("Close",BindingFlags.InvokeMethod,null,fp,null);}catch{}}
            }catch{}
            finally{if(ownsFp)ReleaseComSafe(fp);}
        }

        static string ReadPropVal(object propSet,string propName){
            object p=null;
            try{
                p=propSet.GetType().InvokeMember("Item",BindingFlags.GetProperty,null,propSet,new object[]{propName});
                if(p==null)return "";
                object v=p.GetType().InvokeMember("Value",BindingFlags.GetProperty,null,p,null);
                return v==null?"":Convert.ToString(v).Trim();
            }catch{return "";}
            finally{ReleaseComSafe(p);}
        }

        static bool IsScanHiddenOrRef(A.Occurrence occ,A.SubOccurrence sub){
            if(occ!=null){
                try{if(!occ.Visible)return true;}catch{}
                try{if(occ.ReferenceOnly)return true;}catch{}
                try{if(occ.DisplayAsReference)return true;}catch{}
                try{if(!occ.IncludeInPhysicalProperties)return true;}catch{}
                return false;
            }
            if(sub!=null){
                try{if(!sub.Visible)return true;}catch{}
                try{if(sub.ExcludeFromPhysicalProperties)return true;}catch{}
                return false;
            }
            return false;
        }

        // 扫描当前装配树（顶层 + 最多 3 层子装配），自动识别气缸/阀/传感器/电气件候选并带出 ReferenceKey
        public static List<LineupCadScanItem> ScanAssembly(A.AssemblyDocument doc,LineupProject project,LineupModelCatalog catalog){
            AssemblyFile(doc);
            var existingKeys=new HashSet<string>((project==null||project.Records==null?Enumerable.Empty<LineupRecord>():project.Records).Where(r=>!string.IsNullOrEmpty(r.ReferenceKey)).Select(r=>r.ReferenceKey));
            var metaCache=new Dictionary<string,LineupCadPartMeta>(StringComparer.OrdinalIgnoreCase);
            var results=new List<LineupCadScanItem>();
            // 整次扫描复用同一个 SolidEdge.FileProperties COM 实例，避免每文件反复建/拆 COM 服务器并及时释放子属性 RCW
            Type sharedFpType=null;
            object sharedFp=null;
            try{
                sharedFpType=Type.GetTypeFromProgID("SolidEdge.FileProperties");
                if(sharedFpType!=null)sharedFp=Activator.CreateInstance(sharedFpType);
            }catch{sharedFp=null;}
            try{
                var occs=doc.Occurrences;
                int count=0;try{count=occs.Count;}catch{}
                for(int i=1;i<=count;i++){
                    A.Occurrence occ=null;try{occ=occs.Item(i);}catch{}
                    if(occ==null)continue;
                    ScanNode(occ,null,0,existingKeys,metaCache,catalog,results,sharedFp,sharedFpType);
                }
                return results;
            }finally{
                ReleaseComSafe(sharedFp);
            }
        }

        static void ScanNode(A.Occurrence occ,A.SubOccurrence sub,int depth,HashSet<string> existingKeys,Dictionary<string,LineupCadPartMeta> metaCache,LineupModelCatalog catalog,List<LineupCadScanItem> results,object sharedFp,Type sharedFpType){
            if(depth>3||results.Count>=400)return;
            // 跳过隐藏或仅参考（如行程包络/视野范围）实例，避免误入 BOM 清单
            if(IsScanHiddenOrRef(occ,sub))return;
            string sf="";bool isSubAsm=false;
            try{
                if(occ!=null){sf=occ.OccurrenceFileName??"";isSubAsm=occ.Subassembly;}
                else{sf=sub.SubOccurrenceFileName??"";isSubAsm=sub.Subassembly;}
            }catch{return;}

            LineupCadPartMeta meta=null;
            if(!string.IsNullOrEmpty(sf)){
                if(!metaCache.TryGetValue(sf,out meta)){
                    meta=ReadPartMetaCore(sf,null,catalog,null,sharedFp,sharedFpType);
                    metaCache[sf]=meta;
                }
            }
            // 对子装配仅在命中型号库或标准型号编码（如外购气缸/阀岛总成）时作为单件收录并停止下钻；
            // 纯中文机构名（如「上料气缸机构.asm」「传感器支架组件.asm」）必须继续下钻以防漏扫内部真实器件
            bool treatAsDevice=meta!=null&&!string.IsNullOrEmpty(meta.Category)&&(!isSubAsm||meta.MatchedStandardModel);
            if(treatAsDevice){
                try{
                    var tmp=new LineupRecord();
                    CaptureOccurrence(occ!=null?(object)occ:(object)sub,tmp);
                    string mech=tmp.InstancePath.Length>=2?StripOccSuffix(tmp.InstancePath[0]):"";
                    string desc=!string.IsNullOrWhiteSpace(meta.Title)?meta.Title:meta.Category;
                    results.Add(new LineupCadScanItem{
                        ReferenceKey=tmp.ReferenceKey,SourceFile=tmp.SourceFile,InstancePath=tmp.InstancePath,
                        Category=meta.Category,Model=string.IsNullOrWhiteSpace(meta.Model)?meta.FileName:meta.Model,
                        Brand=meta.Brand,Connector=meta.Connector,Description=desc,Mechanism=mech,
                        AlreadyBound=existingKeys.Contains(tmp.ReferenceKey)
                    });
                    if(isSubAsm)return;
                }catch{}
            }

            if(!isSubAsm||depth>=3)return;
            try{
                var subs=occ!=null?occ.SubOccurrences:sub.SubOccurrences;
                int sc=subs.Count;
                for(int i=1;i<=sc;i++){
                    A.SubOccurrence child=null;try{child=subs.Item(i);}catch{}
                    if(child!=null)ScanNode(null,child,depth+1,existingKeys,metaCache,catalog,results,sharedFp,sharedFpType);
                }
            }catch{}
        }
    }
}
