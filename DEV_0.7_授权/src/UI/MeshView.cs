using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TianGongCadSuite {
    // 零件预览兜底用的三维视图：把 PreviewMesh 用软件 z 缓冲画成着色图（不依赖显卡 / OpenGL，截图也截得到）。
    // 操作和天工预览控件一致：左键拖动旋转、Shift+拖动平移、滚轮缩放、双击回到等轴测。
    // 拖动时用半分辨率画（几十万个三角形也能跟手），松开再按全分辨率画一遍；边缘按深度突变描深色线，看起来像天工的带边着色。
    public sealed class MeshView : Control {
        PreviewMesh mesh;
        double az, el, zoom = 1, panX, panY;   // 看的方向：方位角（XY 平面里，从 +X 量）、仰角
        Bitmap frame;
        bool dragging, coarse;
        Point last;
        float[] zbuf = new float[0];
        int[] pix = new int[0];

        public MeshView(){
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Color.FromArgb(0xE9, 0xED, 0xF3);
            Reset();
        }

        public PreviewMesh Mesh { get { return mesh; } set { mesh = value; Reset(); } }

        // 等轴测：人站在 (1, -1, 1) 方向看，Z 朝上（和天工一样）；前视站在 -Y、右视站在 +X、俯视站在 +Z
        public void Reset(){ az = -Math.PI / 4; el = Math.Atan(1 / Math.Sqrt(2)); zoom = 1; panX = panY = 0; Redraw(); }
        public void SetView(string type){
            switch (type) {
                case "Front": az = -Math.PI / 2; el = 0; break;
                case "Top": az = -Math.PI / 2; el = Math.PI / 2; break;
                case "Right": az = 0; el = 0; break;
                default: Reset(); return;
            }
            zoom = 1; panX = panY = 0; Redraw();
        }
        public void Fit(){ zoom = 1; panX = panY = 0; Redraw(); }

        void Redraw(){ if (frame != null) { frame.Dispose(); frame = null; } Invalidate(); }

        protected override void OnPaint(PaintEventArgs e){
            if (frame == null || frame.Width != Math.Max(1, ClientSize.Width) || frame.Height != Math.Max(1, ClientSize.Height)) {
                if (frame != null) frame.Dispose();
                frame = Render(Math.Max(1, ClientSize.Width), Math.Max(1, ClientSize.Height), coarse ? 2 : 1);
            }
            e.Graphics.DrawImageUnscaled(frame, 0, 0);
        }

        // ---------- 鼠标 ----------
        protected override void OnMouseDown(MouseEventArgs e){ Focus(); if (e.Button == MouseButtons.Left || e.Button == MouseButtons.Middle) { dragging = true; last = e.Location; Capture = true; } base.OnMouseDown(e); }
        protected override void OnMouseMove(MouseEventArgs e){
            if (dragging) {
                int dx = e.X - last.X, dy = e.Y - last.Y; last = e.Location;
                if ((ModifierKeys & Keys.Shift) != 0) { panX += dx; panY += dy; }
                else if ((ModifierKeys & Keys.Control) != 0) zoom = Math.Max(0.05, Math.Min(50, zoom * Math.Exp(-dy * 0.01)));
                else { az -= dx * 0.01; el = Math.Max(-Math.PI / 2, Math.Min(Math.PI / 2, el + dy * 0.01)); }
                coarse = true; Redraw(); Update();
            }
            base.OnMouseMove(e);
        }
        protected override void OnMouseUp(MouseEventArgs e){ if (dragging) { dragging = false; Capture = false; coarse = false; Redraw(); } base.OnMouseUp(e); }
        protected override void OnMouseDoubleClick(MouseEventArgs e){ Reset(); base.OnMouseDoubleClick(e); }
        protected override void OnMouseWheel(MouseEventArgs e){
            // 以鼠标位置为中心缩放
            double k = e.Delta > 0 ? 1.25 : 0.8;
            double cx = e.X - Width / 2.0 - panX, cy = e.Y - Height / 2.0 - panY;
            panX -= cx * (k - 1); panY -= cy * (k - 1);
            zoom = Math.Max(0.05, Math.Min(50, zoom * k));
            Redraw();
            base.OnMouseWheel(e);
        }

        // ---------- 渲染 ----------
        static int Lerp(int a, int b, double t){ return (int)(a + (b - a) * t); }

        Bitmap Render(int w, int h, int step){
            int rw = Math.Max(1, w / step), rh = Math.Max(1, h / step);
            int n = rw * rh;
            if (zbuf.Length < n) { zbuf = new float[n]; pix = new int[n]; }
            // 背景：上浅下深的渐变（和预览控件设的一样）
            for (int y = 0; y < rh; y++) {
                double t = rh > 1 ? y / (double)(rh - 1) : 0;
                int c = (0xFF << 24) | (Lerp(0xF2, 0xCC, t) << 16) | (Lerp(0xF5, 0xD6, t) << 8) | Lerp(0xF8, 0xE0, t);
                for (int x = 0; x < rw; x++) { pix[y * rw + x] = c; zbuf[y * rw + x] = float.MaxValue; }
            }
            if (mesh != null && mesh.Triangles > 0) Raster(rw, rh, step);
            var bmp = new Bitmap(rw, rh, PixelFormat.Format32bppArgb);
            var data = bmp.LockBits(new Rectangle(0, 0, rw, rh), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try { for (int y = 0; y < rh; y++) Marshal.Copy(pix, y * rw, data.Scan0 + y * data.Stride, rw); }
            finally { bmp.UnlockBits(data); }
            if (step == 1) return bmp;
            var full = new Bitmap(w, h);
            using (var g = Graphics.FromImage(full)) { g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear; g.DrawImage(bmp, 0, 0, w, h); }
            bmp.Dispose();
            return full;
        }

        void Raster(int rw, int rh, int step){
            // 视图坐标系：D = 从模型指向看的人，right = 屏幕向右，up = D × right（屏幕向上）
            double ca = Math.Cos(az), sa = Math.Sin(az), ce = Math.Cos(el), se = Math.Sin(el);
            double dx = ce * ca, dy = ce * sa, dz = se;
            double rx = -sa, ry = ca, rz = 0;
            double ux = dy * rz - dz * ry, uy = dz * rx - dx * rz, uz = dx * ry - dy * rx;
            float[] lo = mesh.Lo, hi = mesh.Hi;
            double ox = (lo[0] + hi[0]) / 2, oy = (lo[1] + hi[1]) / 2, oz = (lo[2] + hi[2]) / 2;
            double radius = Math.Sqrt((hi[0] - lo[0]) * (hi[0] - lo[0]) + (hi[1] - lo[1]) * (hi[1] - lo[1]) + (hi[2] - lo[2]) * (hi[2] - lo[2])) / 2;
            if (radius <= 0) radius = 1;
            // 包围球装进视野（转到哪个角度都不出框）、留一点边
            double scale = Math.Min(rw, rh) * 0.46 / radius * zoom;
            double cx0 = rw / 2.0 + panX / step, cy0 = rh / 2.0 + panY / step;
            var t = mesh.Tri; int count = mesh.Triangles;
            // 光从看的人左上方、略靠前来（世界坐标）
            double lx = -0.35 * rx + 0.55 * ux + 0.76 * dx, ly = -0.35 * ry + 0.55 * uy + 0.76 * dy, lz = -0.35 * rz + 0.55 * uz + 0.76 * dz;
            var sx = new double[3]; var syv = new double[3]; var sz = new double[3];
            for (int i = 0; i < count; i++) {
                int b = i * 9;
                for (int v = 0; v < 3; v++) {
                    double px = t[b + v * 3] - ox, py = t[b + v * 3 + 1] - oy, pz = t[b + v * 3 + 2] - oz;
                    sx[v] = cx0 + (px * rx + py * ry + pz * rz) * scale;
                    syv[v] = cy0 - (px * ux + py * uy + pz * uz) * scale;
                    sz[v] = -(px * dx + py * dy + pz * dz);           // 越小越近
                }
                // 法线（世界坐标）→ 明暗，两面都亮（网格的朝向不一定统一）
                double ax = t[b + 3] - t[b], ay = t[b + 4] - t[b + 1], aaz = t[b + 5] - t[b + 2];
                double bx = t[b + 6] - t[b], by = t[b + 7] - t[b + 1], bz = t[b + 8] - t[b + 2];
                double nx = ay * bz - aaz * by, ny = aaz * bx - ax * bz, nz = ax * by - ay * bx;
                double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (len < 1e-30) continue;
                double lit = Math.Abs((nx * lx + ny * ly + nz * lz) / len);
                double shade = 0.55 + 0.45 * lit;
                int tint = mesh.Tint[i];
                int color = (0xFF << 24) | (Clamp((tint >> 16 & 0xFF) * shade) << 16) | (Clamp((tint >> 8 & 0xFF) * shade) << 8) | Clamp((tint & 0xFF) * shade);
                Fill(rw, rh, sx, syv, sz, color);
            }
            Outline(rw, rh, (float)(radius * 0.015));
        }
        static int Clamp(double v){ return v < 0 ? 0 : v > 255 ? 255 : (int)v; }

        void Fill(int rw, int rh, double[] x, double[] y, double[] z, int color){
            int x0 = (int)Math.Max(0, Math.Floor(Math.Min(x[0], Math.Min(x[1], x[2])))), x1 = (int)Math.Min(rw - 1, Math.Ceiling(Math.Max(x[0], Math.Max(x[1], x[2]))));
            int y0 = (int)Math.Max(0, Math.Floor(Math.Min(y[0], Math.Min(y[1], y[2])))), y1 = (int)Math.Min(rh - 1, Math.Ceiling(Math.Max(y[0], Math.Max(y[1], y[2]))));
            if (x0 > x1 || y0 > y1) return;
            double area = (x[1] - x[0]) * (y[2] - y[0]) - (x[2] - x[0]) * (y[1] - y[0]);
            if (Math.Abs(area) < 1e-9) {
                // 退化成线 / 点的小三角形：只点一个像素，薄板的侧面不至于消失
                int px = (int)x[0], py = (int)y[0];
                if (px >= 0 && py >= 0 && px < rw && py < rh) { int k = py * rw + px; if (z[0] < zbuf[k]) { zbuf[k] = (float)z[0]; pix[k] = color; } }
                return;
            }
            double inv = 1 / area;
            for (int py = y0; py <= y1; py++) {
                double cyp = py + 0.5;
                for (int px = x0; px <= x1; px++) {
                    double cxp = px + 0.5;
                    double w0 = ((x[1] - cxp) * (y[2] - cyp) - (x[2] - cxp) * (y[1] - cyp)) * inv;
                    double w1 = ((x[2] - cxp) * (y[0] - cyp) - (x[0] - cxp) * (y[2] - cyp)) * inv;
                    double w2 = 1 - w0 - w1;
                    if (w0 < -1e-6 || w1 < -1e-6 || w2 < -1e-6) continue;
                    float d = (float)(w0 * z[0] + w1 * z[1] + w2 * z[2]);
                    int k = py * rw + px;
                    if (d < zbuf[k]) { zbuf[k] = d; pix[k] = color; }
                }
            }
        }

        // 深度突变处描线：模型轮廓、零件之间的台阶
        void Outline(int rw, int rh, float jump){
            var edge = new bool[rw * rh];
            for (int y = 1; y < rh - 1; y++)
                for (int x = 1; x < rw - 1; x++) {
                    int k = y * rw + x; float d = zbuf[k];
                    float r = zbuf[k + 1], dn = zbuf[k + rw];
                    bool bgHere = d == float.MaxValue;
                    if (bgHere && r == float.MaxValue && dn == float.MaxValue) continue;
                    if (bgHere != (r == float.MaxValue) || bgHere != (dn == float.MaxValue)) { edge[k] = true; continue; }
                    if (!bgHere && (Math.Abs(d - r) > jump || Math.Abs(d - dn) > jump)) edge[k] = true;
                }
            for (int k = 0; k < edge.Length; k++) if (edge[k]) pix[k] = unchecked((int)0xFF2A2E35);
        }

        protected override void Dispose(bool disposing){ if (disposing && frame != null) { frame.Dispose(); frame = null; } base.Dispose(disposing); }

#if TG_DEV_BUILD
        internal void TestSave(string png){ using (var b = Render(Math.Max(1, Width), Math.Max(1, Height), 1)) b.Save(png); }
        internal void TestRotate(double daz, double del){ az += daz; el += del; Redraw(); }
#endif
    }
}
