using System;
using System.Collections.Generic;
using A=SolidEdgeAssembly;
using G=SolidEdgeGeometry;
using P=SolidEdgePart;

namespace TianGongCadSuite {
    // 装配里的每一个零件实例（顶层 + 子装配里的，多层也算），配孔检查用。
    //
    // 原来配孔检查只遍历 assembly.Occurrences（顶层实例），子装配整个被跳过：
    // 子装配里的孔不检查，子装配里的零件也不会被判成"漏打孔的那个零件" —— 而型材框架大多就是子装配。
    //
    // 两种实例的取法：
    //   · 顶层实例：和原来完全一样（assembly.CreateReference(occ, 实体) 建引用、occ.GetMatrix 取变换），不改变已有结果；
    //   · 子装配里的实例：用 SubOccurrence.CreateTopologyReference(实体的引用键) 建引用 ——
    //     「型材自动填充」读子装配里的型材就是这么做的（FrameReader，真机用过）；引用自带"零件 → 顶层装配"的矩阵，
    //     变换直接从引用里取，不自己乘矩阵（嵌套矩阵的约定没有单独验证过，见 HoleLinkCad 里的说明）。
    // 名字用路径「子装配名/零件名」：不同子装配里同名的实例不会混在一起。
    // 多实体零件（0.7.15）：Bodies 是全部设计体，Body = 第 1 个。原来只读第 1 个实体，
    // 第 2 个以后的实体上的孔配孔检查看不到，轴线穿过它们也判不出漏打孔（见 PartBodies）。
    public sealed class AssemblyPart {
        public string Name = "";                 // 顶层：实例名；子装配里：路径
        public P.PartDocument Part;              // 钣金件（.psm）为 null：只有实体（Bodies）
        public G.Body Body;
        public List<G.Body> Bodies = new List<G.Body>();
        public bool Nested;
        // 子装配里一条边都没有的零件（轴承里的滚珠 —— 球面没有边）：取不到放置矩阵，但它本来也不可能有孔。
        // 配孔检查跳过它、不算"读取失败"（ST064 原来报"108 个零件读取失败"，日志里每次检查 108 段异常堆栈）
        public bool NoEdges;
        public string File = "";                 // 零件文件全路径（配孔检查按文件缓存圆边；读不出来为空 = 不缓存）
        // 隐藏的、只作参考的（「范围」「包络」这类不计入物理属性的零件）：配孔检查不拿它当"漏打孔的那个零件"（0.8.5，见 RefOnly）
        public bool Ignored;
        public A.Occurrence Top;                 // 顶层实例（Nested=false）
        public A.SubOccurrence Sub;              // 子装配里的实例（Nested=true）
        A.AssemblyDocument assembly;
        Transform placement;

        // 实体（边/面）→ 可高亮、可取装配坐标的引用
        public object Reference(object entity){
            if (!Nested) return assembly.CreateReference(Top, entity);
            Array key = new byte[0];
            ((dynamic)entity).GetReferenceKey(ref key);
            A.TopologyReference tr;
            Sub.CreateTopologyReference(ref key, out tr);
            return tr;
        }

        // 零件局部 → 顶层装配
        public Transform Placement {
            get {
                if (placement != null) return placement;
                if (!Nested) { Array m = new double[16]; Top.GetMatrix(ref m); placement = new Transform(m); return placement; }
                // 子装配里的实例：拿任意一条边建个引用，矩阵就是它的放置
                foreach (var b in Bodies)
                    foreach (G.Edge e in (G.Edges)b.get_Edges(G.FeatureTopologyQueryTypeConstants.igQueryAll)) {
                        placement = PickGeometry.Unwrap(Reference(e)).Transform;
                        return placement;
                    }
                NoEdges = true;
                throw new InvalidOperationException(Name + " 没有边，取不到放置矩阵。");
            }
        }

        const int MaxDepth = 8;

        public static List<AssemblyPart> List(A.AssemblyDocument assembly, List<string> warnings){
            var result = new List<AssemblyPart>();
            if (assembly == null) return result;
            foreach (A.Occurrence occ in assembly.Occurrences) {
                string name;
                try { name = occ.Name; } catch { continue; }
                bool sub = false;
                try { sub = occ.Subassembly; } catch { }
                if (sub) {
                    // 整个子装配隐藏 / 只作参考：里面的零件一样不当"漏打孔的那个零件"（原来只看零件自己，隐藏的整套包络件照样被报）
                    try { Walk(assembly, occ.SubOccurrences, name, 1, result, warnings, RefOnly(occ)); }
                    catch (Exception e) { Warn(warnings, name + " 子装配读取失败：" + e.Message); }
                    continue;
                }
                object doc = null; try { doc = occ.OccurrenceDocument; } catch { }
                // 文件不在了（被移走 / 删了）、加载不了：原来一声不响地跳过，配孔检查漏查了它用户也不知道
                if (doc == null) { string miss = ""; try { miss = occ.OccurrenceFileName ?? ""; } catch { } Warn(warnings, name + " 的文件打不开（不在了或加载失败），这个零件没查" + (miss.Length > 0 ? "（找的是 " + miss + "）" : "")); continue; }
                var part = Doc(doc);
                if (part == null && !IsSheet(doc)) continue;
                var bodies = part != null ? BodiesOf(part, name, warnings) : SheetBodies(doc, name, warnings);
                if (bodies.Count == 0) continue;
                string file = ""; try { file = occ.OccurrenceFileName ?? ""; } catch { }
                result.Add(new AssemblyPart { Name = name, Part = part, Body = bodies[0], Bodies = bodies, Top = occ, assembly = assembly, Ignored = RefOnly(occ), File = file });
            }
            return result;
        }

        static void Walk(A.AssemblyDocument assembly, A.SubOccurrences subs, string parent, int depth, List<AssemblyPart> result, List<string> warnings, bool parentIgnored){
            if (subs == null || depth > MaxDepth) return;
            int n = 0; try { n = subs.Count; } catch { }
            for (int i = 1; i <= n; i++) {
                A.SubOccurrence s = null;
                try { s = subs.Item(i); } catch { }
                if (s == null) continue;
                string name, own = null;
                try { own = s.Name; } catch { }
                // 文件不在了的实例：天工给的名字是空的或者是整条路径（焊接台车副本实测「MC模组侧向支撑.asm:1/」「…asm:5/H:\桌面\…\-019.par」），
                // 「读取失败的零件」里看不出是哪个零件 —— 换成文件名
                string subFile = ""; try { subFile = s.SubOccurrenceFileName ?? ""; } catch { }
                if (string.IsNullOrEmpty(own)) own = subFile.Length > 0 ? System.IO.Path.GetFileName(subFile) : null;
                else if (own.IndexOf('\\') >= 0) { try { own = System.IO.Path.GetFileName(own); } catch { } }
                if (string.IsNullOrEmpty(own)) continue;
                name = parent + "/" + own;
                bool sub = false;
                try { sub = s.Subassembly; } catch { }
                if (sub) {
                    try { Walk(assembly, s.SubOccurrences, name, depth + 1, result, warnings, parentIgnored || RefOnly(s)); }
                    catch (Exception e) { Warn(warnings, name + " 子装配读取失败：" + e.Message); }
                    continue;
                }
                object doc = null; try { doc = s.SubOccurrenceDocument; } catch { }
                if (doc == null) { Warn(warnings, name + " 的文件打不开（不在了或加载失败），这个零件没查" + (subFile.Length > 0 ? "（找的是 " + subFile + "）" : "")); continue; }
                var part = Doc(doc);
                if (part == null && !IsSheet(doc)) continue;
                var bodies = part != null ? BodiesOf(part, name, warnings) : SheetBodies(doc, name, warnings);
                if (bodies.Count == 0) continue;
                string file = subFile;
                result.Add(new AssemblyPart { Name = name, Part = part, Body = bodies[0], Bodies = bodies, Nested = true, Sub = s, assembly = assembly, Ignored = parentIgnored || RefOnly(s), File = file });
            }
        }

        // 这个零件文件在装配里一共用了几次（顶层 + 各级子装配，0.8.5）。只读文件名，不取 OccurrenceDocument（不加载零件）。
        // 打孔是改零件文件：用了 4 次的支架，4 处都会长出这些孔 —— 自动打孔点面时要先说清楚。读不出来返回 0
        public static int CountInstances(A.AssemblyDocument assembly, string fullName){
            if (assembly == null || string.IsNullOrEmpty(fullName)) return 0;
            int n = 0;
            try {
                foreach (A.Occurrence occ in assembly.Occurrences) {
                    bool sub = false; try { sub = occ.Subassembly; } catch { }
                    if (sub) { try { n += CountIn(occ.SubOccurrences, fullName, 1); } catch { } continue; }
                    string f = ""; try { f = occ.OccurrenceFileName; } catch { }
                    if (string.Equals(f, fullName, StringComparison.OrdinalIgnoreCase)) n++;
                }
            } catch (Exception e) { Log.Write("CountInstances", e.Message); return 0; }
            return n;
        }
        static int CountIn(A.SubOccurrences subs, string fullName, int depth){
            if (subs == null || depth > MaxDepth) return 0;
            int n = 0, count = 0; try { count = subs.Count; } catch { }
            for (int i = 1; i <= count; i++) {
                A.SubOccurrence s = null; try { s = subs.Item(i); } catch { }
                if (s == null) continue;
                bool sub = false; try { sub = s.Subassembly; } catch { }
                if (sub) { try { n += CountIn(s.SubOccurrences, fullName, depth + 1); } catch { } continue; }
                string f = ""; try { f = s.SubOccurrenceFileName; } catch { }
                if (string.Equals(f, fullName, StringComparison.OrdinalIgnoreCase)) n++;
            }
            return n;
        }

        // 隐藏 / 只作参考 / 不计入物理属性：现场「万向轮范围--兼容小车.par」这种运动范围包络件，轴线一穿过它就被报"漏打孔"，
        // 用户还真照着去给包络件打孔（10-22 日志）。读不出来的当正常零件
        static bool RefOnly(A.Occurrence o){
            try { if (!o.Visible) return true; } catch { }
            try { if (o.ReferenceOnly) return true; } catch { }
            try { if (o.DisplayAsReference) return true; } catch { }
            try { if (!o.IncludeInPhysicalProperties) return true; } catch { }
            return false;
        }
        static bool RefOnly(A.SubOccurrence s){
            try { if (!s.Visible) return true; } catch { }
            try { if (s.ExcludeFromPhysicalProperties) return true; } catch { }
            return false;
        }

        static P.PartDocument Doc(object doc){ return doc as P.PartDocument; }
        // 钣金件（.psm）：原来 as PartDocument 是 null，整个跳过 —— 钣金上的孔不查，钣金也永远不会被报成"漏打孔的那个零件"
        // （用户的 OK310 围栏里就有十几个钣金件）。配孔检查只用它的实体、位置和引用，Part 留空
        static bool IsSheet(object doc){ return doc is P.SheetMetalDocument; }
        static List<G.Body> SheetBodies(object doc, string name, List<string> warnings){
            var list = new List<G.Body>();
            try {
                var models = ((P.SheetMetalDocument)doc).Models;
                for (int i = 1; i <= models.Count; i++) { var b = PartBodies.Body((P.Model)models.Item(i)); if (b != null) list.Add(b); }
            } catch (Exception e) { Warn(warnings, name + " 读取钣金实体失败：" + e.Message); }
            return list;
        }
        static List<G.Body> BodiesOf(P.PartDocument part, string name, List<string> warnings){
            var list = new List<G.Body>();
            try {
                foreach (var m in PartBodies.All(part)) { var b = PartBodies.Body(m); if (b != null) list.Add(b); }
            } catch (Exception e) { Warn(warnings, name + " 读取实体失败：" + e.Message); }
            return list;
        }
        static void Warn(List<string> warnings, string s){ if (warnings != null) warnings.Add(s); }
    }
}
