using System;
using System.Collections.Generic;
using System.Globalization;
using A=SolidEdgeAssembly;
using F=SolidEdgeFramework;
using P=SolidEdgePart;

namespace TianGongCadSuite {
    // 装配里的一个零件实例（含嵌套子装配里的）。
    public sealed class OccurrenceNode {
        public string Path = "";        // "子装配.asm:1/板A.par:1"
        public string Name = "";
        public string FullName = "";    // 零件或钣金件文件全路径
        public string FileName = "";
        public P.PartDocument Part;     // 子装配与钣金件节点为 null（打孔目标仍仅支持 .par）
        public object Document;         // 零件（.par）或钣金件（.psm）文档，供参考孔关联与同步定位
        public bool HasDoc { get { return Part != null || Document != null; } }
        public Transform Raw;           // 实例自己 GetMatrix 读到的
        public Transform Composed;      // 从顶层逐级乘下来的
        public Transform Placement(string mode){ return mode == "C" ? Composed : Raw; }
    }

    // 装配实例索引：打孔时用它认出"参考孔/打孔面在哪个实例上"，同步时用它找回实例、读现在的位置。
    //
    // 认实例的办法是"零件文件相同 + 变换矩阵相同"：选择引用（Reference.GetMatrix）给的就是
    // 零件局部 → 顶层装配的变换，拿它去和每个实例的矩阵比。嵌套子装配里 SubOccurrence.GetMatrix
    // 到底是相对父装配还是相对顶层，手册没说清，所以两种都算（Raw / Composed），哪个对得上就记哪个，
    // 同步时按同一种取法读，不用赌。
    public sealed class OccurrenceIndex {
        public readonly List<OccurrenceNode> Nodes = new List<OccurrenceNode>();
        const int MaxDepth = 8;

        public static OccurrenceIndex Build(A.AssemblyDocument asm){ return Build(asm, null); }
        // progress(第几个顶层实例, 共几个)：大装配要把每个零件加载一遍、一两分钟，窗口拿它刷状态栏、泵消息
        public static OccurrenceIndex Build(A.AssemblyDocument asm, Action<int, int> progress){
            var idx = new OccurrenceIndex();
            if (asm == null) return idx;
            int n = 0;
            try { n = asm.Occurrences.Count; } catch (Exception e) { Log.Write("OccIndexCount", e); }
            for (int i = 1; i <= n; i++) {
                if (progress != null) { try { progress(i, n); } catch { } }
                A.Occurrence occ = null;
                try { occ = asm.Occurrences.Item(i); } catch { }
                if (occ == null) continue;
                try {
                    string name = occ.Name;
                    Array m = new double[16]; occ.GetMatrix(ref m);
                    var raw = new Transform(m);
                    var node = new OccurrenceNode { Path = name, Name = name, Raw = raw, Composed = raw };
                    object doc = null; try { doc = occ.OccurrenceDocument; } catch { }
                    Fill(node, doc);
                    idx.Nodes.Add(node);
                    bool sub = false; try { sub = occ.Subassembly; } catch { }
                    if (sub) { try { WalkSubs(idx, occ.SubOccurrences, node.Path, raw, 1); } catch (Exception e) { Log.Write("OccIndexSub " + name, e); } }
                } catch (Exception e) { Log.Write("OccIndexNode", e); }
            }
            return idx;
        }

        // 只建一条实例链上的节点（0.8.4）：打孔时给挂不上天工关联的孔写插件关联记录，只需要认出参考零件 / 打孔零件这两个实例。
        // 原来为此把整个装配建一遍索引（每个实例取 OccurrenceDocument = 把每个零件加载一遍，大装配现场 106 秒）；
        // 实例链从选择引用直接拿（NativeHoleLink.ChainOf），这里沿着链用和 Build 一样的取法（SubOccurrences）读矩阵，
        // 路径、R/C 两种矩阵取法都和 Build 完全一致，「同步孔位」时用整装配索引照样找得回来。
        public static OccurrenceIndex ForChain(OccChain chain){
            var idx = new OccurrenceIndex();
            if (chain == null || chain.Items.Count == 0) return idx;
            var occ = chain.Items[0];
            try {
                string name = occ.Name;
                Array m = new double[16]; occ.GetMatrix(ref m);
                var raw = new Transform(m);
                var node = new OccurrenceNode { Path = name, Name = name, Raw = raw, Composed = raw };
                object doc = null; try { doc = occ.OccurrenceDocument; } catch { }
                Fill(node, doc);
                idx.Nodes.Add(node);
                A.SubOccurrences subs = null;
                if (chain.Items.Count > 1) { try { subs = occ.SubOccurrences; } catch { } }
                for (int k = 1; k < chain.Items.Count && subs != null; k++) {
                    string want = ""; try { want = chain.Items[k].Name; } catch { }
                    A.SubOccurrence hit = null;
                    int n = 0; try { n = subs.Count; } catch { }
                    for (int i = 1; i <= n && hit == null; i++) { try { var s = subs.Item(i); if (s.Name == want) hit = s; } catch { } }
                    if (hit == null) break;
                    Array sm = new double[16]; hit.GetMatrix(ref sm);
                    var sraw = new Transform(sm);
                    var child = new OccurrenceNode { Path = node.Path + "/" + want, Name = want, Raw = sraw, Composed = Compose(node.Composed, sraw) };
                    object sdoc = null; try { sdoc = hit.SubOccurrenceDocument; } catch { }
                    Fill(child, sdoc);
                    idx.Nodes.Add(child);
                    node = child;
                    try { subs = hit.SubOccurrences; } catch { subs = null; }
                }
            } catch (Exception e) { Log.Write("OccIndexChain", e); }
            return idx;
        }

        static void WalkSubs(OccurrenceIndex idx, A.SubOccurrences subs, string parentPath, Transform parent, int depth){
            if (subs == null || depth > MaxDepth) return;
            int n = 0; try { n = subs.Count; } catch { }
            for (int i = 1; i <= n; i++) {
                A.SubOccurrence s = null;
                try { s = subs.Item(i); } catch { }
                if (s == null) continue;
                try {
                    string name = s.Name;
                    Array m = new double[16]; s.GetMatrix(ref m);
                    var raw = new Transform(m);
                    var node = new OccurrenceNode { Path = parentPath + "/" + name, Name = name, Raw = raw, Composed = Compose(parent, raw) };
                    object doc = null; try { doc = s.SubOccurrenceDocument; } catch { }
                    Fill(node, doc);
                    idx.Nodes.Add(node);
                    bool sub = false; try { sub = s.Subassembly; } catch { }
                    if (sub) WalkSubs(idx, s.SubOccurrences, node.Path, node.Composed, depth + 1);
                } catch (Exception e) { Log.Write("OccIndexSubNode", e); }
            }
        }

        static void Fill(OccurrenceNode node, object doc){
            var part = doc as P.PartDocument;
            var sheet = doc as P.SheetMetalDocument;
            node.Part = part;
            node.Document = (object)part ?? sheet;
            if (node.Document == null) return;
            node.FullName = DocName(node.Document);
            node.FileName = System.IO.Path.GetFileName(node.FullName);
        }

        // outer ∘ inner：先 inner 再 outer
        static Transform Compose(Transform outer, Transform inner){
            var o = outer.Point(new V3(inner.M[12], inner.M[13], inner.M[14]));
            var x = outer.Vector(new V3(inner.M[0], inner.M[1], inner.M[2]));
            var y = outer.Vector(new V3(inner.M[4], inner.M[5], inner.M[6]));
            var z = outer.Vector(new V3(inner.M[8], inner.M[9], inner.M[10]));
            return Transform.Frame(o, x, y, z);
        }

        static bool Same(Transform a, Transform b){
            if (a == null || b == null) return false;
            for (int i = 0; i < 16; i++) if (Math.Abs(a.M[i] - b.M[i]) > 1e-7) return false;
            return true;
        }

        static string DocName(object doc){
            var part = doc as P.PartDocument;
            if (part != null) {
                string s = "";
                try { s = part.FullName ?? ""; } catch { }
                if (s.Length == 0) { try { s = part.Name ?? ""; } catch { } }
                return s;
            }
            var sheet = doc as P.SheetMetalDocument;
            if (sheet != null) {
                string s = "";
                try { s = sheet.FullName ?? ""; } catch { }
                if (s.Length == 0) { try { s = sheet.Name ?? ""; } catch { } }
                return s;
            }
            return "";
        }

        // 选择引用（零件或钣金件文档 + 局部→装配的变换）对应哪个实例。mode 返回矩阵取法。
        public OccurrenceNode Locate(object partDocument, Transform placement, out string mode){
            mode = "R";
            if (!(partDocument is P.PartDocument) && !(partDocument is P.SheetMetalDocument)) return null;
            string full = DocName(partDocument);
            if (full.Length == 0) return null;
            OccurrenceNode only = null; int same = 0;
            foreach (var n in Nodes) {
                if (!n.HasDoc || !string.Equals(n.FullName, full, StringComparison.OrdinalIgnoreCase)) continue;
                same++; only = n;
                if (Same(n.Raw, placement)) { mode = "R"; return n; }
                if (Same(n.Composed, placement)) { mode = "C"; return n; }
            }
            // 矩阵谁都对不上（例如原位编辑时引用矩阵的取法不同），但这个零件在装配里只有一个实例：就是它
            return same == 1 ? only : null;
        }

        // 同步时按记下的路径找回实例；路径变了（实例改名、删了重插）就按文件名兜底，但必须唯一。
        public OccurrenceNode Find(string path, string fileName){
            if (!string.IsNullOrEmpty(path))
                foreach (var n in Nodes) if (n.HasDoc && n.Path == path) return n;
            if (string.IsNullOrEmpty(fileName)) return null;
            OccurrenceNode only = null; int count = 0;
            foreach (var n in Nodes) {
                if (!n.HasDoc) continue;
                if (string.Equals(n.FileName, fileName, StringComparison.OrdinalIgnoreCase)) { only = n; count++; }
            }
            return count == 1 ? only : null;
        }

        // 装配里出现过的零件文档（去重）
        public List<OccurrenceNode> DistinctParts(){
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<OccurrenceNode>();
            foreach (var n in Nodes) {
                if (n.Part == null) continue;
                string key = n.FullName.Length > 0 ? n.FullName : n.Path;
                if (seen.Add(key)) list.Add(n);
            }
            return list;
        }
    }
}
