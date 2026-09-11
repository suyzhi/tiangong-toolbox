using System;
using System.IO;
using System.Linq;
using A=SolidEdgeAssembly;
namespace TianGongCadSuite {
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
            if(selected is A.Occurrence||selected is A.SubOccurrence)return selected;
            var reference=selected as SolidEdgeFramework.Reference;
            if(reference!=null){
                object top;int count,bound;Array path=new object[0];
                reference.GetOccurrencesInPath(out top,out count,out bound,ref path);
                if(count==0&&top is A.Occurrence)return top;
                if(count==bound&&path!=null&&path.Length>0){var leaf=path.GetValue(path.GetUpperBound(0));if(leaf is A.SubOccurrence)return leaf;}
            }
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
            var names=new System.Collections.Generic.List<string>();var root=top as A.Occurrence;if(root!=null)names.Add(root.Name);
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
    }
}
