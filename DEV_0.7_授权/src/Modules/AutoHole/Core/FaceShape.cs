using System;
using System.Collections.Generic;

namespace TianGongCadSuite {
    // 打孔面的实际形状（0.8.5）：孔心落没落在面的材料上。三角形来自面片（FaceFrameReader.Facets），纯内存判断。
    public static class FaceShape {
        // 面内一点 (u,v) 是不是落在面的材料上（离某个三角形 tol 以内），或者就在面上某个圆边的圆心上（孔口：那里本来就有孔，
        // 让后面的"已经有孔"去说，别说成"不在面上"；只认圆心附近 —— 大圆窗口里别的位置是真的空）。纯内存，单测见 AutoHoleLinkPureTests.FaceShapePure
        public static bool Inside(double[] tris, IList<double[]> circles, double u, double v, double tol){
            if (circles != null)
                foreach (var c in circles) { double du = u - c[0], dv = v - c[1]; if (du * du + dv * dv <= (tol + HoleSpots.RadialTolM) * (tol + HoleSpots.RadialTolM)) return true; }
            if (tris == null) return true;
            // 先只判"在不在三角形里"（几次乘法），绝大多数孔心第一遍就找到；都不在才逐个算到边的距离（面边上 tol 以内也算）。
            // 大板几百个孔时三角形上万个，预览每改一个数字都要把每个孔心判一遍
            for (int i = 0; i + 5 < tris.Length; i += 6)
                if (InTri(u, v, tris[i], tris[i + 1], tris[i + 2], tris[i + 3], tris[i + 4], tris[i + 5])) return true;
            for (int i = 0; i + 5 < tris.Length; i += 6)
                if (DistToTri(u, v, tris[i], tris[i + 1], tris[i + 2], tris[i + 3], tris[i + 4], tris[i + 5]) <= tol) return true;
            return false;
        }
        static bool InTri(double px, double py, double ax, double ay, double bx, double by, double cx, double cy){
            double d1 = Cross(ax, ay, bx, by, px, py), d2 = Cross(bx, by, cx, cy, px, py), d3 = Cross(cx, cy, ax, ay, px, py);
            return !((d1 < 0 || d2 < 0 || d3 < 0) && (d1 > 0 || d2 > 0 || d3 > 0));
        }
        static double DistToTri(double px, double py, double ax, double ay, double bx, double by, double cx, double cy){
            double d1 = Cross(ax, ay, bx, by, px, py), d2 = Cross(bx, by, cx, cy, px, py), d3 = Cross(cx, cy, ax, ay, px, py);
            bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
            if (!(neg && pos)) return 0;
            return Math.Min(Seg(px, py, ax, ay, bx, by), Math.Min(Seg(px, py, bx, by, cx, cy), Seg(px, py, cx, cy, ax, ay)));
        }
        static double Cross(double ax, double ay, double bx, double by, double px, double py){ return (bx - ax) * (py - ay) - (by - ay) * (px - ax); }
        static double Seg(double px, double py, double ax, double ay, double bx, double by){
            double dx = bx - ax, dy = by - ay, l2 = dx * dx + dy * dy;
            double t = l2 <= 0 ? 0 : Math.Max(0, Math.Min(1, ((px - ax) * dx + (py - ay) * dy) / l2));
            double qx = ax + t * dx - px, qy = ay + t * dy - py;
            return Math.Sqrt(qx * qx + qy * qy);
        }
    }
}
