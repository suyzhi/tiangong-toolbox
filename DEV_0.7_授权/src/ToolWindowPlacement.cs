using System;
using System.Drawing;
using System.Globalization;

namespace TianGongCadSuite {
    // 工具窗口摆放的纯计算：贴边停靠、收起成一条、再展开、记住的位置还能不能用。
    // 不碰 WinForms，tests/pure 在 Mac 上也能跑。所有矩形都是屏幕坐标（像素）。
    public static class ToolWindowPlacement {
        // 贴 CAD 窗口的左/右边摆：顶部让开功能区（topInset），宽度保持，高度不超出可用区域。
        // 原来一律 CenterParent —— 正好盖在模型中间，而这些命令偏偏都要在模型上点东西。
        public static Rectangle Dock(Rectangle cad, Rectangle work, Size size, Size min, bool right, int topInset, int margin){
            Rectangle area = Rectangle.Intersect(cad, work);
            if (area.Width < 200 || area.Height < 200) area = work;   // CAD 窗口读不到 / 最小化：退回整个工作区
            int w = Math.Max(min.Width, Math.Min(size.Width, area.Width - 2 * margin));
            int top = area.Top + Math.Max(0, topInset);
            int h = Math.Max(min.Height, Math.Min(size.Height, area.Bottom - margin - top));
            // 最小高度比"功能区以下"的空间还高：整体往上挪，底边不出界
            if (top + h > area.Bottom - margin) top = Math.Max(area.Top, area.Bottom - margin - h);
            int x = right ? area.Right - margin - w : area.Left + margin;
            return Fit(new Rectangle(x, top, w, h), work);
        }

        // 收起时贴哪条竖边：窗口中心在工作区右半边就贴右边（条留在原来的右上角），否则贴左边。
        public static bool AnchorRight(Rectangle window, Rectangle work){
            return window.Left + window.Width / 2 > work.Left + work.Width / 2;
        }

        // 收起：顶边和贴靠的那条竖边不动，大小换成条的大小（条不比原窗口宽）。
        public static Rectangle Collapse(Rectangle expanded, Size strip, bool anchorRight){
            int w = Math.Min(strip.Width, expanded.Width);
            int x = anchorRight ? expanded.Right - w : expanded.Left;
            return new Rectangle(x, expanded.Top, w, strip.Height);
        }

        // 展开：以条现在的位置为准（用户可能把条拖走了），恢复原来的大小，再整个塞回工作区。
        public static Rectangle Expand(Rectangle strip, Size size, bool anchorRight, Rectangle work){
            int x = anchorRight ? strip.Right - size.Width : strip.Left;
            return Fit(new Rectangle(x, strip.Top, size.Width, size.Height), work);
        }

        // 整个塞进工作区：先保证右/下不出界，再保证左/上不出界（比工作区还大时左上角优先可见，标题栏抓得到）。
        public static Rectangle Fit(Rectangle r, Rectangle work){
            int x = r.X, y = r.Y;
            if (x + r.Width > work.Right) x = work.Right - r.Width;
            if (y + r.Height > work.Bottom) y = work.Bottom - r.Height;
            if (x < work.Left) x = work.Left;
            if (y < work.Top) y = work.Top;
            return new Rectangle(x, y, r.Width, r.Height);
        }

        // 记住的位置还能不能用：标题栏那一条至少有 80×16 像素落在某块屏幕的工作区里。
        // 拔了副屏、换了分辨率之后，上次的位置可能整个在屏幕外 —— 那就当没记过，重新贴边。
        public static bool Reachable(Rectangle r, Rectangle[] workAreas){
            if (r.Width <= 0 || r.Height <= 0 || workAreas == null) return false;
            var caption = new Rectangle(r.X, r.Y, r.Width, Math.Min(24, r.Height));
            foreach (var w in workAreas) {
                var hit = Rectangle.Intersect(caption, w);
                if (hit.Width >= 80 && hit.Height >= 16) return true;
            }
            return false;
        }

        public static string Format(Rectangle r){
            var c = CultureInfo.InvariantCulture;
            return r.X.ToString(c) + "," + r.Y.ToString(c) + "," + r.Width.ToString(c) + "," + r.Height.ToString(c);
        }

        public static bool TryParse(string text, out Rectangle r){
            r = Rectangle.Empty;
            if (string.IsNullOrEmpty(text)) return false;
            var parts = text.Split(',');
            if (parts.Length != 4) return false;
            var v = new int[4];
            for (int i = 0; i < 4; i++)
                if (!int.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v[i])) return false;
            if (v[2] <= 0 || v[3] <= 0) return false;
            r = new Rectangle(v[0], v[1], v[2], v[3]);
            return true;
        }
    }
}
