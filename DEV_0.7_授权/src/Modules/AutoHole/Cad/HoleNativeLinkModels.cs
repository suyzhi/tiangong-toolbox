using System;
using System.Collections.Generic;
using A=SolidEdgeAssembly;
using F=SolidEdgeFramework;
using G=SolidEdgeGeometry;
using P=SolidEdgePart;
using S=SolidEdgeFrameworkSupport;

namespace TianGongCadSuite {
    // 天工原生跨零件关联（0.7.8）：配做孔挂在参考孔上，参考零件一挪，天工自己把孔带过去 —— 特征树里有锁链，不靠插件同步。
    //
    // 真机走通的唯一一条路（tools/hole-feature-probe/AsmLinkProbe*.cs，2026-10-20，记录见 AUTO-HOLE.md 0.7.8 一节）：
    //   1) 原位编辑打孔零件：选中实例 + StartCommand(57872 激活嵌入对象) = 在装配里双击零件；
    //      零件在子装配里就一级一级进（先进子装配、再在里面进零件），直接选深层实例进不去。
    //   2) 在打孔零件里建"零件间复制"（Constructions.InterpartConstructions.Add），来源 = 参考孔所在的**面**的装配引用
    //      （平面或孔壁圆柱面都行；引用一条边会建失败）。子装配里的参考零件：先在子装配里 CreateReference，再到上一级包一层。
    //      复制出来的圆边就在打孔零件的局部坐标里，零件 HasInterpartLinks=True。
    //   3) 孔草图里 IncludeEdge(复制来的圆边) + Hole2d 关键点约束到圆心 + 圆改构造线，再建孔。
    //   4) 参考零件挪了以后 UpdateAll（或天工里「更新所有链接」），孔自己跟过去；保存重开还在。
    // 直接 IncludeEdge 另一个零件的边（不管在不在原位）一律 0x80040224 —— 所以必须经过零件间复制这一步。
    //
    // 走不通的情况（链接属于别的装配、实例认不出、进不了原位、斜孔……）孔照样打（照样一个多孔特征），只是不挂关联，结果里说明原因。
    // （0.7.11 以前退回属性集关联 +「同步孔位」，只能单孔 + 编组，0.7.12 起不再用。）
    public sealed class NativeAnchor {
        public P.InterpartConstruction Copy;
        public CopyCircles Circles; // 同一个复制的锚点共用的圆边表（null = 每次现读）
        public V3 Centre;   // 要打的孔心（打孔零件局部）
        public V3 Normal;   // 打孔面法向（打孔零件局部）
        public string Label = "";
        public string Why = ""; // 打孔时孔心挂不上这条圆边的原因（Attach 返回的），结果里告诉用户

        // 复制来的圆边里"就是这个孔"的那条
        public G.Edge Edge(){
            if (Copy == null) return null;
            return (Circles ?? new CopyCircles(Copy)).Match(Centre, Normal);
        }
    }

    // 一个零件间复制上的全部圆边（0.7.10）：读一遍，这个复制上的所有孔共用。
    // 原来每个孔（Begin 里一次、Attach 里一次）都把复制的全部边从 CAD 读一遍 —— 20 个孔 × 几十条边 × 每条 3 次 COM 调用。
    // 对不上时重读一次再比：刚建的复制偶尔还没算完、圆边缺一条（NativeLinkE2E 实测）；打完孔后旧的边对象也可能失效（Attach 包含失败时 Reset）。
    public sealed class CopyCircles {
        public readonly P.InterpartConstruction Copy;
        List<G.Edge> edges; List<NativeLinkMath.Circle> circles;
        public CopyCircles(P.InterpartConstruction copy){ Copy = copy; }
        public void Reset(){ edges = null; circles = null; }

        public G.Edge Match(V3 centre, V3 normal){
            bool justRead = edges == null;
            if (justRead) Read();
            double off;
            int i = NativeLinkMath.Match(circles, centre, normal, out off);
            if (i >= 0 || justRead) return i < 0 ? null : edges[i];
            Reset(); Read();
            i = NativeLinkMath.Match(circles, centre, normal, out off);
            return i < 0 ? null : edges[i];
        }

        void Read(){
            edges = new List<G.Edge>(); circles = new List<NativeLinkMath.Circle>();
            object all = null;
            try { all = Copy.get_Edges(G.FeatureTopologyQueryTypeConstants.igQueryAll); } catch (Exception e) { Log.Write("NativeAnchorEdges", e); return; }
            if (all == null) return;
            foreach (object o in (System.Collections.IEnumerable)all) {
                var e = o as G.Edge; if (e == null) continue;
                G.Circle c = null; try { c = e.Geometry as G.Circle; } catch { }
                if (c == null) continue;
                Array cc = new double[3], ax = new double[3]; double r = 0;
                try { c.GetCircleData(ref cc, ref ax, out r); } catch { continue; }
                edges.Add(e); circles.Add(new NativeLinkMath.Circle(V3.From(cc), V3.From(ax), r));
            }
        }
    }

    // 实例链：顶层装配里的实例 → 子装配文档里的实例 → … → 零件实例（每一项都是所在装配文档里的 Occurrence）
    public sealed class OccChain {
        public readonly List<A.Occurrence> Items = new List<A.Occurrence>();
        public string Key = "";
        public A.Occurrence Leaf { get { return Items.Count == 0 ? null : Items[Items.Count - 1]; } }
    }
}
