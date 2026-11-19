using System;
using A=SolidEdgeAssembly;
using F=SolidEdgeFramework;

namespace TianGongCadSuite {
    // 装配选择集（SelectSet）里的一项 → 具体实例：顶层 Occurrence，或子装配内部零件的 SubOccurrence。
    //
    // 在顶层装配里点选子装配内部的零件（装配树展开后点、快速选取），CAD 放进 SelectSet 的是一个 Reference，
    // 不是 Occurrence。Reference.Object 拿到的是**子装配文档自己的** Occurrence —— 它的 TopLevelDocument 是子装配、
    // 矩阵也是相对子装配的，直接用会报「选择不属于当前装配」或者位置错。正确做法是 GetOccurrencesInPath
    // 沿路径取到顶层装配里的 SubOccurrence（Lineup 多选模型加入就是这么做的，真机验证过，见 LINEUP-SMART-VALIDATION）。
    public static class OccurrencePick {
        // 认不出返回 null（调用方自己决定报什么错）
        public static object Instance(object selected){
            if (selected is A.Occurrence || selected is A.SubOccurrence) return selected;
            var reference = selected as F.Reference;
            if (reference == null) return null;
            object top; int count, bound; Array path = new object[0];
            reference.GetOccurrencesInPath(out top, out count, out bound, ref path);
            if (count == 0 && top is A.Occurrence) return top;
            if (count == bound && path != null && path.Length > 0) {
                var leaf = path.GetValue(path.GetUpperBound(0));
                if (leaf is A.SubOccurrence) return leaf;
            }
            return null;
        }

        // 实例属于哪个顶层子装配（界面上报"型材来自哪几个子装配"用）。顶层零件本身不属于子装配、取不到时都返回空串。
        public static string TopName(object instance){
            try {
                var occurrence = instance as A.Occurrence;
                if (occurrence != null) return occurrence.Subassembly ? occurrence.Name : "";
                var sub = instance as A.SubOccurrence;
                if (sub == null) return "";
                object top; int count, bound; Array path = new object[0];
                sub.Reference.GetOccurrencesInPath(out top, out count, out bound, ref path);
                var root = top as A.Occurrence;
                return root != null ? root.Name : "";
            } catch { return ""; }
        }
    }
}
