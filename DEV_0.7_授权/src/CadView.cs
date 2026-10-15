using System;
using F=SolidEdgeFramework;

namespace TianGongCadSuite {
    // 把当前视图镜头拉到某个位置（装配坐标，米）。打孔结果、配孔检查的「跳转到孔」都用它：
    // 光高亮不够 —— 大装配里孔只有几毫米，高亮了也得自己满屏找。
    // 视图对象用迟绑定取（ActiveWindow.View），和 Lineup「定位模型」同一条已经在用的路。
    public static class CadView {
        // halfSizeM：镜头框住的半边长（米）。孔一般给孔径的几倍 + 一点余量，能看清孔又能看见周围是哪个零件。
        public static bool ZoomTo(F.Application app, V3 centre, double halfSizeM){
            if (app == null || !centre.Finite) return false;
            double h = Math.Max(0.003, halfSizeM);
            try {
                dynamic view = ((dynamic)app.ActiveWindow).View;
                view.RangeZoomCamera(centre.X - h, centre.Y - h, centre.Z - h, centre.X + h, centre.Y + h, centre.Z + h);
                view.Update();
                return true;
            } catch (Exception e) { Log.Write("CadViewZoom", e); return false; }
        }

        // 孔的合适镜头：孔径 ×4，至少 15mm 见方
        public static bool ZoomToHole(F.Application app, V3 centre, double diameterMm){
            double half = Math.Max(0.015, diameterMm * 4 / 1000.0);
            return ZoomTo(app, centre, half);
        }

        // 一组点的包围盒（配孔检查一条结果涉及好几个孔时用）
        public static bool ZoomToPoints(F.Application app, System.Collections.Generic.IList<V3> points, double padM){
            if (points == null || points.Count == 0) return false;
            double x0 = double.MaxValue, y0 = double.MaxValue, z0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue, z1 = double.MinValue;
            foreach (var p in points) {
                if (!p.Finite) continue;
                x0 = Math.Min(x0, p.X); y0 = Math.Min(y0, p.Y); z0 = Math.Min(z0, p.Z);
                x1 = Math.Max(x1, p.X); y1 = Math.Max(y1, p.Y); z1 = Math.Max(z1, p.Z);
            }
            if (x0 > x1) return false;
            double pad = Math.Max(padM, Math.Max(x1 - x0, Math.Max(y1 - y0, z1 - z0)) * 0.15);
            try {
                dynamic view = ((dynamic)app.ActiveWindow).View;
                view.RangeZoomCamera(x0 - pad, y0 - pad, z0 - pad, x1 + pad, y1 + pad, z1 + pad);
                view.Update();
                return true;
            } catch (Exception e) { Log.Write("CadViewZoom", e); return false; }
        }
    }
}
