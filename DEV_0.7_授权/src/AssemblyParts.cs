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
    public sealed class AssemblyPart {
        public string Name = "";                 // 顶层：实例名；子装配里：路径
        public P.PartDocument Part;
        public G.Body Body;
        public bool Nested;
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
                foreach (G.Edge e in (G.Edges)Body.get_Edges(G.FeatureTopologyQueryTypeConstants.igQueryAll)) {
                    placement = PickGeometry.Unwrap(Reference(e)).Transform;
                    return placement;
                }
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
                    try { Walk(assembly, occ.SubOccurrences, name, 1, result, warnings); }
                    catch (Exception e) { Warn(warnings, name + " 子装配读取失败：" + e.Message); }
                    continue;
                }
                var part = Doc(occ.OccurrenceDocument);
                if (part == null) continue;
                var body = BodyOf(part, name, warnings);
                if (body == null) continue;
                result.Add(new AssemblyPart { Name = name, Part = part, Body = body, Top = occ, assembly = assembly });
            }
            return result;
        }

        static void Walk(A.AssemblyDocument assembly, A.SubOccurrences subs, string parent, int depth, List<AssemblyPart> result, List<string> warnings){
            if (subs == null || depth > MaxDepth) return;
            int n = 0; try { n = subs.Count; } catch { }
            for (int i = 1; i <= n; i++) {
                A.SubOccurrence s = null;
                try { s = subs.Item(i); } catch { }
                if (s == null) continue;
                string name;
                try { name = parent + "/" + s.Name; } catch { continue; }
                bool sub = false;
                try { sub = s.Subassembly; } catch { }
                if (sub) {
                    try { Walk(assembly, s.SubOccurrences, name, depth + 1, result, warnings); }
                    catch (Exception e) { Warn(warnings, name + " 子装配读取失败：" + e.Message); }
                    continue;
                }
                P.PartDocument part = null;
                try { part = Doc(s.SubOccurrenceDocument); } catch { }
                if (part == null) continue;
                var body = BodyOf(part, name, warnings);
                if (body == null) continue;
                result.Add(new AssemblyPart { Name = name, Part = part, Body = body, Nested = true, Sub = s, assembly = assembly });
            }
        }

        static P.PartDocument Doc(object doc){ return doc as P.PartDocument; }
        static G.Body BodyOf(P.PartDocument part, string name, List<string> warnings){
            try {
                if (part.Models.Count < 1) return null;
                return (G.Body)((P.Model)part.Models.Item(1)).Body;
            } catch (Exception e) { Warn(warnings, name + " 读取实体失败：" + e.Message); return null; }
        }
        static void Warn(List<string> warnings, string s){ if (warnings != null) warnings.Add(s); }
    }
}
