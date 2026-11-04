using System;
using System.Collections.Generic;
using A = SolidEdgeAssembly;
using F = SolidEdgeFramework;
using G = SolidEdgeGeometry;
using P = SolidEdgePart;

namespace TianGongCadSuite {
    // 零件预览的兜底：天工的预览控件有的装配画不出来（焊接台车里的「聚氧模组侧向支撑子装配」，它的零部件单独都画得出，
    // 组合起来一片空白 —— 控件内部的问题，见 HANDOVER-0.11.3.md），这时用天工里开着的这个装配自己画（MeshView）。
    //
    // 三角网格从天工取：每个可见的零件实例 Body.GetFacetData（同一个零件文件只取一次），
    // 摆位用实例矩阵 —— 顶层 Occurrence.GetMatrix，子装配里的 SubOccurrence.GetMatrix 直接就是在顶层装配里的位置
    // （型材自动填充 FrameReader 跨子装配读型材就是这么用的，真机验证过）。坐标单位：米。
    // 隐藏的、只作参考的实例不画（和天工里看到的一样）。
    public sealed class PreviewMesh {
        public float[] Tri = new float[0];   // 每个三角形 9 个数（三个顶点的 x y z）
        public int[] Tint = new int[0];      // 每个三角形的底色（同一个零件文件同一种颜色）
        public int Triangles { get { return Tint.Length; } }
        public float[] Lo = { 0, 0, 0 }, Hi = { 0, 0, 0 };
        public int Parts, Skipped;

        // 三角形太多时画得慢（拖动要能跟手）：超过这么多就把弦高放粗重取
        const int Budget = 1500000;

        // 在天工里找这个装配：本身开着的文档，或者开着的装配里用到的子装配
        public static A.AssemblyDocument Find(F.Application app, string path){
            if (app == null || string.IsNullOrEmpty(path)) return null;
            try {
                foreach (object d in app.Documents) {
                    var asm = d as A.AssemblyDocument;
                    if (asm == null) continue;
                    string full = ""; try { full = asm.FullName; } catch { }
                    if (string.Equals(full, path, StringComparison.OrdinalIgnoreCase)) return asm;
                }
                foreach (object d in app.Documents) {
                    var asm = d as A.AssemblyDocument;
                    if (asm == null) continue;
                    var hit = FindIn(asm.Occurrences, path, 0);
                    if (hit != null) return hit;
                }
            } catch (Exception e) { Log.Write("PreviewMeshFind", e.Message); }
            return null;
        }
        static A.AssemblyDocument FindIn(A.Occurrences occs, string path, int depth){
            if (occs == null || depth > 8) return null;
            foreach (A.Occurrence o in occs) {
                bool sub = false; try { sub = o.Subassembly; } catch { }
                if (!sub) continue;
                string f = ""; try { f = o.OccurrenceFileName; } catch { }
                A.AssemblyDocument doc = null; try { doc = o.OccurrenceDocument as A.AssemblyDocument; } catch { }
                if (doc == null) continue;
                if (string.Equals(f, path, StringComparison.OrdinalIgnoreCase)) return doc;
                var hit = FindIn(doc.Occurrences, path, depth + 1);
                if (hit != null) return hit;
            }
            return null;
        }

        sealed class Leaf { public string File; public object Doc; public double[] M; }

        public static PreviewMesh Build(A.AssemblyDocument asm, Action<string> pulse){
            var leaves = new List<Leaf>();
            int skipped = 0;
            foreach (A.Occurrence o in asm.Occurrences) {
                if (Hidden(o)) continue;
                bool sub = false; try { sub = o.Subassembly; } catch { }
                if (sub) { try { Walk(o.SubOccurrences, 1, leaves, ref skipped); } catch { skipped++; } continue; }
                object doc = null; try { doc = o.OccurrenceDocument; } catch { }
                var m = Matrix(o, null);
                if (doc == null || m == null) { skipped++; continue; }
                string f = ""; try { f = o.OccurrenceFileName; } catch { }
                leaves.Add(new Leaf { File = f, Doc = doc, M = m });
            }
            var mesh = Assemble(leaves, 0.0005, pulse);
            if (mesh.Triangles > Budget) mesh = Assemble(leaves, 0.003, pulse);
            mesh.Skipped += skipped;
            return mesh;
        }

        static void Walk(A.SubOccurrences subs, int depth, List<Leaf> leaves, ref int skipped){
            if (subs == null || depth > 8) return;
            int n = 0; try { n = subs.Count; } catch { }
            for (int i = 1; i <= n; i++) {
                A.SubOccurrence s = null; try { s = subs.Item(i); } catch { }
                if (s == null || Hidden(s)) continue;
                bool sub = false; try { sub = s.Subassembly; } catch { }
                if (sub) { try { Walk(s.SubOccurrences, depth + 1, leaves, ref skipped); } catch { skipped++; } continue; }
                object doc = null; try { doc = s.SubOccurrenceDocument; } catch { }
                var m = Matrix(null, s);
                if (doc == null || m == null) { skipped++; continue; }
                string f = ""; try { f = s.SubOccurrenceFileName; } catch { }
                leaves.Add(new Leaf { File = f, Doc = doc, M = m });
            }
        }
        static bool Hidden(A.Occurrence o){
            try { if (!o.Visible) return true; } catch { }
            try { if (o.DisplayAsReference) return true; } catch { }
            return false;
        }
        static bool Hidden(A.SubOccurrence s){ try { return !s.Visible; } catch { return false; } }
        static double[] Matrix(A.Occurrence o, A.SubOccurrence s){
            try {
                Array m = new double[16];
                if (o != null) o.GetMatrix(ref m); else s.GetMatrix(ref m);
                var d = new double[16];
                for (int i = 0; i < 16; i++) { d[i] = Convert.ToDouble(m.GetValue(i)); if (double.IsNaN(d[i]) || double.IsInfinity(d[i])) return null; }
                return d;
            } catch { return null; }
        }

        static PreviewMesh Assemble(List<Leaf> leaves, double chord, Action<string> pulse){
            var cache = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
            var tints = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int total = 0;
            for (int i = 0; i < leaves.Count; i++) {
                var lf = leaves[i];
                string key = lf.File.Length > 0 ? lf.File : "#" + i;
                float[] local;
                if (!cache.TryGetValue(key, out local)) {
                    if (pulse != null) pulse("读取零件网格 " + (cache.Count + 1) + " …");
                    local = Local(lf.Doc, chord);
                    cache[key] = local;
                    tints[key] = Palette[tints.Count % Palette.Length];
                }
                total += local.Length / 9;
            }
            var mesh = new PreviewMesh { Tri = new float[total * 9], Tint = new int[total], Parts = leaves.Count };
            float[] lo = { float.MaxValue, float.MaxValue, float.MaxValue }, hi = { float.MinValue, float.MinValue, float.MinValue };
            int at = 0;
            for (int i = 0; i < leaves.Count; i++) {
                var lf = leaves[i];
                string key = lf.File.Length > 0 ? lf.File : "#" + i;
                var local = cache[key]; var m = lf.M; int tint = tints[key];
                for (int k = 0; k < local.Length; k += 3) {
                    double x = local[k], y = local[k + 1], z = local[k + 2];
                    // 天工的矩阵按行存（平移在 12～14），和 Transform.Point 一样
                    float wx = (float)(m[0] * x + m[4] * y + m[8] * z + m[12]);
                    float wy = (float)(m[1] * x + m[5] * y + m[9] * z + m[13]);
                    float wz = (float)(m[2] * x + m[6] * y + m[10] * z + m[14]);
                    mesh.Tri[at * 9 + (k % 9)] = wx; mesh.Tri[at * 9 + (k % 9) + 1] = wy; mesh.Tri[at * 9 + (k % 9) + 2] = wz;
                    if (wx < lo[0]) lo[0] = wx; if (wy < lo[1]) lo[1] = wy; if (wz < lo[2]) lo[2] = wz;
                    if (wx > hi[0]) hi[0] = wx; if (wy > hi[1]) hi[1] = wy; if (wz > hi[2]) hi[2] = wz;
                    if (k % 9 == 6) { mesh.Tint[at] = tint; at++; }
                }
            }
            if (total > 0) { mesh.Lo = lo; mesh.Hi = hi; }
            return mesh;
        }

        // 一个零件（或钣金件）全部实体的三角形，零件坐标
        static float[] Local(object doc, double chord){
            var bodies = new List<G.Body>();
            var part = doc as P.PartDocument;
            if (part != null) bodies = AssemblyPart.BodiesOf(part, "", null);
            else if (doc is P.SheetMetalDocument) bodies = AssemblyPart.SheetBodies(doc, "", null);
            var list = new List<float>();
            foreach (var b in bodies) {
                try {
                    Array facets = new double[0]; object normals = null, texture = null, styles = null, faceIds = null; int n;
                    b.GetFacetData(chord, out n, ref facets, out normals, out texture, out styles, out faceIds, false);
                    if (n <= 0 || facets.Length < n * 9) continue;
                    var d = facets as double[];
                    for (int i = 0; i < n * 9; i++) list.Add((float)(d != null ? d[i] : Convert.ToDouble(facets.GetValue(i))));
                } catch (Exception e) { Log.Write("PreviewMeshFacets", e.Message); }
            }
            return list.ToArray();
        }

        // 柔和的几种灰 / 浅色：相邻零件分得清，又不花
        static readonly int[] Palette = {
            0xB8BEC6, 0xC9C2B6, 0xAFC0CF, 0xC4CBB8, 0xC8B9C2, 0xBCC8C8, 0xD0C8B0, 0xB4B8CC
        };
    }
}
