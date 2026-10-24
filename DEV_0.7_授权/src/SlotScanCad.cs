using System;
using System.Collections.Generic;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;

namespace TianGongCadSuite {
    // 参考件上的腰孔（0.8.5）：点腰孔一头的圆弧边，认出整条腰孔（两段同半径圆弧 + 两条直边围成的环），
    // 在目标件上配一条同样的腰孔（通切，和「批量排孔」的腰孔同一个 AutoHoleWriter.DrillSlots）。
    // 原来点圆弧要么当成圆孔、在腰孔一头打个圆孔，要么直接拒绝。
    public sealed class SlotReference {
        public V3 Center;          // 装配坐标：两段圆弧圆心的中点（腰孔中心）
        public V3 Axis;            // 装配坐标：腰孔贯穿方向（= 圆弧轴线）
        public V3 Direction;       // 装配坐标：腰孔长度方向（两圆心连线，单位向量）
        public double LengthMm;    // 总长（两圆心距 + 宽）
        public double WidthMm;     // 槽宽（= 圆弧直径）
        public object Owner;       // 参考零件文档
        public string Source = "";
        public string Label { get { return "腰孔 " + LengthMm.ToString("0.##") + "×" + WidthMm.ToString("0.##"); } }
        public bool Same(SlotReference o){
            return o != null && (o.Center - Center).Length < 0.00005 && Math.Abs(Math.Abs(o.Direction.Dot(Direction)) - 1) < 1e-4
                && Math.Abs(o.LengthMm - LengthMm) < 0.01 && Math.Abs(o.WidthMm - WidthMm) < 0.01;
        }
    }

    public static class SlotScanCad {
        // 点到的圆弧边是腰孔的一头就返回整条腰孔，不是返回 null
        public static SlotReference FromArc(object selection){
            var pg = PickGeometry.Unwrap(selection);
            var edge = pg.Geometry as G.Edge;
            if (edge == null) return null;
            var arc = edge.Geometry as G.Circle;
            if (arc == null) return null;
            Array c = new double[3], ax = new double[3]; double r = 0;
            arc.GetCircleData(ref c, ref ax, out r);
            var c1 = V3.From(c); var axis = V3.From(ax).Unit();
            // 圆弧所在的平面（孔口那张面）里，含这条边的环
            int count = 0; Array faces = new object[0];
            try { edge.GetFaces(out count, ref faces); } catch { return null; }
            for (int i = 0; i < count; i++) {
                var f = faces.GetValue(i) as G.Face;
                if (f == null || !(SafeGeometry(f) is G.Plane)) continue;
                G.Loops loops = null; try { loops = f.Loops as G.Loops; } catch { }
                if (loops == null) continue;
                foreach (G.Loop loop in loops) {
                    var arcs = new List<V3>(); int lines = 0, others = 0; bool hasIt = false;
                    double rr = 0;
                    foreach (G.Edge e in (G.Edges)loop.Edges) {
                        if (SameEdge(e, edge)) hasIt = true;
                        object g = null; try { g = e.Geometry; } catch { }
                        var ci = g as G.Circle;
                        if (ci != null) {
                            Array cc = new double[3], aa = new double[3]; double rad = 0;
                            ci.GetCircleData(ref cc, ref aa, out rad);
                            arcs.Add(V3.From(cc)); rr = rad;
                            if (Math.Abs(rad - r) > 1e-6) others++;
                        } else if (g is G.Line) lines++; else others++;
                    }
                    if (!hasIt) continue;
                    if (arcs.Count != 2 || lines != 2 || others != 0) return null;
                    var other = (arcs[0] - c1).Length < 1e-7 ? arcs[1] : arcs[0];
                    double dist = (other - c1).Length;
                    if (dist < 1e-6) return null;
                    var mid = (c1 + other) * 0.5;
                    var s = new SlotReference {
                        Center = pg.Transform.Point(mid),
                        Axis = pg.Transform.Vector(axis).Unit(),
                        Direction = pg.Transform.Vector((other - c1) * (1.0 / dist)).Unit(),
                        LengthMm = (dist + 2 * r) * 1000.0, WidthMm = 2 * r * 1000.0,
                        Source = "腰孔圆弧"
                    };
                    try { s.Owner = edge.Document; } catch { }
                    return s;
                }
            }
            return null;
        }
        static object SafeGeometry(G.Face f){ try { return f.Geometry; } catch { return null; } }
        static bool SameEdge(G.Edge a, G.Edge b){
            try { return ReferenceEquals(a, b) || a.ID == b.ID; } catch { return false; }   // 同一个实体里边的 ID 唯一（环就在这个实体上）
        }
    }
}
