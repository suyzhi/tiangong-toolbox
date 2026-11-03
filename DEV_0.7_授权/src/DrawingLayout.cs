using System;
using System.Collections.Generic;
using System.Linq;

namespace TianGongCadSuite {
    // 一键出图：选哪几个视图、用 A4 还是 A3、什么比例、每个视图放在图纸哪里。纯逻辑。
    //
    // 公司图纸（普查 200+ 张）：一张图 3～5 个视图 —— 主视图 + 1～2 个投影 + 右下角一个轴测图，偶尔剖视 / 局部放大；
    // A4 横放和 A3 横放都用（约 4:6）；GB 第一角投影：俯视图在主视图**下面**、左视图在主视图**右边**。
    // 主视图挑"孔最多、面最大"的那一面朝外（板件看大面，人画的板件都是这样）。

    // 零件的特征统计（来自三维模型，见 DrawingModelRead）。轴 0/1/2 = 模型 X/Y/Z。
    public sealed class ModelFacts {
        public double[] Size = new double[3];          // 外形尺寸（米）
        public int[] Holes = new int[3];               // 轴线沿该轴的孔个数
        public int[] Steps = new int[3];               // 垂直于该轴的平面有几个不同位置（≥2：两个端面；更多 = 有台阶）
        public double[] FaceArea = new double[3];      // 法向沿该轴的平面面积和
        public bool SheetMetal;
        public int[] MouthPlus = new int[3], MouthMinus = new int[3];
        public bool[] Stepped = new bool[3];   // 沿该轴有台阶孔 / 槽（不只是孔口一段沉孔）：主视图下面换成剖视
        public static bool IsStepped(ModelHole h){ return h.Segments.Count >= 3 || (h.Segments.Count == 2 && h.CbRadius <= 0); }   // 孔口朝 +轴 / -轴 的个数（盲孔只有一头开口）
        public readonly List<double> Chamfers = new List<double>();   // 倒角边长（毫米），每个倒角面一个
        public readonly List<double> Rounds = new List<double>();     // 外棱倒圆半径（毫米）
        // 每个模型轴上"外形边里侧多近的线算倒角 / 倒圆分界线"（米）：倒角 / 倒圆只把线往它切掉的那两个轴的方向挪
        public double[] EdgeTol = new double[3];
        // 外形边里侧这么近的平行线都是倒角 / 倒圆的分界线
        public double ChamferMax { get { return Math.Max(Chamfers.Count == 0 ? 0 : Chamfers.Max(), Rounds.Count == 0 ? 0 : Rounds.Max()); } }
    }

    public enum DwgViewRole { Main, Below, Right, Iso }

    // 要建的一个视图：Axis = 视线方向沿哪个模型轴（Sign 正负表示从哪边看），Horizontal = 视图里水平方向对应的模型轴。
    public sealed class DwgViewSpec {
        public DwgViewRole Role;
        public int Axis, Horizontal, Vertical;
        public int Sign = 1;
        public double W, H;                 // 视图里的宽高（模型米，未乘比例）
        public double X, Y;                 // 视图中心放在图纸哪（纸面米）
        public double Scale;
        public bool Section;                // Below 换成过台阶孔中心的全剖
        public DwgViewSpec Clone(){ return (DwgViewSpec)MemberwiseClone(); }
    }

    // 图纸上能画图的区域（纸面米）：主区放正投影一组，Iso 区放轴测图。
    public sealed class SheetZones {
        public string Name;
        public double Width, Height;
        public double[] Main;   // x0,y0,x1,y1
        public double[] Iso;
        // 公司 SK先惠 模板（2026-11-03 量自 A3-SK图纸.dft / A4-SK图纸.dft 导出的 PDF）：
        //   A3 420×297：标题栏 x≥219、y≤43；右上未注公差表 x≥300、y≥247；左上图号框 y≥278；右边小方框 x 378～410、y 62～90；左下技术要求 x≤95、y≤43
        //   A4 297×210：标题栏 x≥97、y≤43；右上公差表 x≥217、y≥173；左上图号框 y≥194；小方框 x 272～292、y 43～61
        public static SheetZones A3(){ return new SheetZones { Name = "A3", Width = 0.420, Height = 0.297, Main = new[] { 0.010, 0.050, 0.290, 0.272 }, Iso = new[] { 0.295, 0.095, 0.412, 0.242 } }; }
        public static SheetZones A4(){ return new SheetZones { Name = "A4", Width = 0.297, Height = 0.210, Main = new[] { 0.008, 0.048, 0.205, 0.190 }, Iso = new[] { 0.208, 0.064, 0.290, 0.170 } }; }
        public double MainW { get { return Main[2] - Main[0]; } }
        public double MainH { get { return Main[3] - Main[1]; } }
    }

    public sealed class DwgLayout {
        public SheetZones Zones;
        public double Scale, IsoScale;
        public readonly List<DwgViewSpec> Views = new List<DwgViewSpec>();
        public DwgViewSpec Get(DwgViewRole r){ return Views.FirstOrDefault(v => v.Role == r); }
    }

    public static class DrawingLayout {
        // 视图之间、视图和区域边上要留给尺寸的地方（纸面米）：坐标尺寸一串文字 ≈ 10mm，外加 6mm 空
        public const double DimBand = 0.016;
        public const double Gap = 0.010;

        // ---- 选视图 ----
        // 主视图视线轴：孔最多的轴；一样多时取平面面积最大的轴（= 看大面）；再一样取外形最薄的轴。
        // 主视图里长边水平。投影：
        //   右（左视图，显示 竖直轴 × 视线轴）：竖直方向上有孔（轴沿水平轴的孔在左视图里是圆）——不，左视图看到的是轴沿"水平轴"的孔；
        //   下（俯视图，显示 水平轴 × 视线轴）：看到轴沿"竖直轴"的孔。
        // 两个都没有孔时只要一个，用来表达厚度 / 台阶：主视图扁（宽 ≥ 高）放下面（俯视图矮），否则放右边。
        // 孔口朝向统计（三维孔 → ModelFacts）
        public static void Mouths(ModelFacts f, IEnumerable<ModelHole> holes){
            foreach (var h in holes) {
                double[] a = { h.Axis.X, h.Axis.Y, h.Axis.Z };
                int k = -1; for (int i = 0; i < 3; i++) if (Math.Abs(a[i]) > 0.9998) k = i;
                if (k < 0) continue;
                if (ModelFacts.IsStepped(h)) f.Stepped[k] = true;
                int sg = a[k] > 0 ? 1 : -1;
                // Hi 那头朝 +Axis 开口，Lo 那头朝 -Axis
                if (!h.BlindHi) { if (sg > 0) f.MouthPlus[k]++; else f.MouthMinus[k]++; }
                if (!h.BlindLo) { if (sg > 0) f.MouthMinus[k]++; else f.MouthPlus[k]++; }
            }
        }

        public static List<DwgViewSpec> Choose(ModelFacts f){
            int axis = 0;
            for (int a = 1; a < 3; a++) {
                if (f.Holes[a] > f.Holes[axis]) axis = a;
                else if (f.Holes[a] == f.Holes[axis]) {
                    if (f.FaceArea[a] > f.FaceArea[axis] * 1.05) axis = a;
                    else if (Math.Abs(f.FaceArea[a] - f.FaceArea[axis]) <= f.FaceArea[axis] * 0.05 && f.Size[a] < f.Size[axis]) axis = a;
                }
            }
            int u = (axis + 1) % 3, v = (axis + 2) % 3;
            int horiz = f.Size[u] >= f.Size[v] ? u : v, vert = horiz == u ? v : u;
            var r = new List<DwgViewSpec>();
            // 看 Y 的主视图从前面看（-Y，天工的「前视图」，人画的图默认这个）；看 X、Z 的从 +X（右视）、+Z（俯视）看
            // 盲孔开口多的那一面朝外：销子一头的 M5 孔从另一头看是看不见的（YF25.07G01-L1-M30-02-029）
            int sign = axis == 1 ? -1 : 1;
            if (f.MouthPlus[axis] > f.MouthMinus[axis]) sign = 1; else if (f.MouthMinus[axis] > f.MouthPlus[axis]) sign = -1;
            r.Add(new DwgViewSpec { Role = DwgViewRole.Main, Axis = axis, Sign = sign, Horizontal = horiz, Vertical = vert, W = f.Size[horiz], H = f.Size[vert] });
            bool right = f.Holes[horiz] > 0;     // 轴沿水平轴的孔 → 从右边看（左视图）是圆
            bool below = f.Holes[vert] > 0;      // 轴沿竖直轴的孔 → 从下面看（俯视图）是圆
            bool steps = f.Steps[axis] > 2;
            if (!right && !below) { if (f.Size[horiz] >= f.Size[vert]) below = true; else right = true; }
            else if (steps && !below && !right) below = true;
            // 沿主视图视线有台阶孔（衬套块的 Ø22 / Ø28 / Ø29.5 退刀槽）：下面那个视图换成全剖 A-A，台阶深度、各段直径在剖视里标（人画的 003 就是这样）
            bool section = f.Stepped[axis];
            if (section) below = true;
            if (below) r.Add(new DwgViewSpec { Role = DwgViewRole.Below, Axis = vert, Horizontal = horiz, Vertical = axis, W = f.Size[horiz], H = f.Size[axis], Section = section });
            if (right) r.Add(new DwgViewSpec { Role = DwgViewRole.Right, Axis = horiz, Horizontal = axis, Vertical = vert, W = f.Size[axis], H = f.Size[vert] });
            r.Add(new DwgViewSpec { Role = DwgViewRole.Iso, Axis = axis, Horizontal = horiz, Vertical = vert });
            return r;
        }

        // ---- 排版 ----
        // 主视图左上，俯视图在它下面、左视图在它右边；整组按比例放进主区，轴测图单独放进 Iso 区（比例可以比主视图小一档）。
        // 先试 A4，A4 放得下且比例不比 1:1 小（或者和 A3 一样大）就用 A4，否则 A3 —— 普查里小件 A4、大件 A3。
        public static DwgLayout Arrange(List<DwgViewSpec> views, double isoW, double isoH){
            var a4 = Fit(views, SheetZones.A4(), isoW, isoH);
            var a3 = Fit(views, SheetZones.A3(), isoW, isoH);
            return (a4.Scale >= a3.Scale - 1e-12 || a4.Scale >= 1 - 1e-12) ? a4 : a3;
        }

        public static DwgLayout Fit(List<DwgViewSpec> specs, SheetZones z, double isoW, double isoH){
            // A4 / A3 各排一遍，视图对象各用各的（共用会被后排的那张覆盖坐标）
            var views = specs.Select(v => v.Clone()).ToList();
            var main = views.First(v => v.Role == DwgViewRole.Main);
            var below = views.FirstOrDefault(v => v.Role == DwgViewRole.Below);
            var right = views.FirstOrDefault(v => v.Role == DwgViewRole.Right);
            // 需要的宽高 = 模型尺寸 × 比例 + 固定留白（尺寸带）
            double modelW = main.W + (right != null ? right.W : 0), modelH = main.H + (below != null ? below.H : 0);
            int bandsW = 2 + (right != null ? 1 : 0), bandsH = 2 + (below != null ? 1 : 0);
            double fixedW = bandsW * DimBand + (right != null ? Gap : 0), fixedH = bandsH * DimBand + (below != null ? Gap : 0);
            double scale = DrawingPlan.PickScale(modelW, modelH, Math.Max(1e-6, z.MainW - fixedW), Math.Max(1e-6, z.MainH - fixedH));
            var lay = new DwgLayout { Zones = z, Scale = scale };
            // 整组在主区里居中偏左上
            double groupW = modelW * scale + fixedW, groupH = modelH * scale + fixedH;
            double left = z.Main[0] + Math.Max(0, (z.MainW - groupW) / 2), top = z.Main[3] - Math.Max(0, (z.MainH - groupH) / 2);
            double mx = left + DimBand + main.W * scale / 2, my = top - DimBand - main.H * scale / 2;
            Put(lay, main, mx, my, scale);
            if (below != null) Put(lay, below, mx, my - main.H * scale / 2 - DimBand - Gap - below.H * scale / 2, scale);
            if (right != null) Put(lay, right, mx + main.W * scale / 2 + DimBand + Gap + right.W * scale / 2, my, scale);
            var iso = views.FirstOrDefault(v => v.Role == DwgViewRole.Iso);
            if (iso != null) {
                double isoAvailW = z.Iso[2] - z.Iso[0] - 0.006, isoAvailH = z.Iso[3] - z.Iso[1] - 0.006;
                // 轴测图只是看形状，比例不写在图上：按区域放满（两位有效数字），不超过主视图比例
                double s = Math.Min(isoAvailW / Math.Max(isoW, 1e-6), isoAvailH / Math.Max(isoH, 1e-6)) * 0.92;
                double mag = Math.Pow(10, Math.Floor(Math.Log10(s)) - 1); s = Math.Floor(s / mag) * mag;
                if (s > scale) s = scale;
                lay.IsoScale = s;
                iso.W = isoW; iso.H = isoH;
                Put(lay, iso, (z.Iso[0] + z.Iso[2]) / 2, (z.Iso[1] + z.Iso[3]) / 2, s);
            }
            return lay;
        }
        static void Put(DwgLayout lay, DwgViewSpec v, double x, double y, double s){ v.X = x; v.Y = y; v.Scale = s; if (!lay.Views.Contains(v)) lay.Views.Add(v); }

        // 轴测图外形（正等测）：三个轴的投影长度。用来给轴测图挑比例
        public static void IsoBox(double[] size, out double w, out double h){
            double c = Math.Cos(Math.PI / 6), s = 0.5;
            w = (size[0] + size[1]) * c;
            h = size[2] + (size[0] + size[1]) * s;
        }
    }

    // 图纸上已经占用的地方（视图、尺寸、标注文字），给引线文字找空地。全部图纸坐标（米），矩形 {x0,y0,x1,y1}。
    public sealed class DwgSpace {
        readonly List<double[]> used = new List<double[]>();
        readonly List<double[]> allowed = new List<double[]>();
        public DwgSpace(SheetZones z){
            allowed.Add(z.Main); allowed.Add(z.Iso);
            // 主区和轴测区之间那条竖带也能放字（主区右边到轴测区左边）
            allowed.Add(new[] { z.Main[2], z.Iso[1], z.Iso[0], z.Main[3] });
        }
        public void Add(double[] r){ if (r != null && r[2] > r[0] && r[3] > r[1]) used.Add(r); }
        public void Add(double x0, double y0, double x1, double y1){ Add(new[] { Math.Min(x0, x1), Math.Min(y0, y1), Math.Max(x0, x1), Math.Max(y0, y1) }); }
        static bool Overlap(double[] a, double[] b){ return a[0] < b[2] && b[0] < a[2] && a[1] < b[3] && b[1] < a[3]; }
        bool Inside(double[] r){ foreach (var z in allowed) if (r[0] >= z[0] - 1e-9 && r[2] <= z[2] + 1e-9 && r[1] >= z[1] - 1e-9 && r[3] <= z[3] + 1e-9) return true; return false; }
        public bool Free(double[] r){ if (!Inside(r)) return false; foreach (var u in used) if (Overlap(u, r)) return false; return true; }
        // 文字大小估计（字高 3.5 的公司尺寸样式）：汉字 3.6mm、其它 2.1mm 宽，一行 4.5mm 高
        public static double TextWidth(string t){
            double w = 0; foreach (char c in t ?? "") w += c > 0x2E80 ? 0.0036 : 0.0021;
            // %DI %DP %CB 这类符号码只占一个字
            int codes = 0; for (int i = 0; i + 2 < (t ?? "").Length; i++) if (t[i] == '%' && char.IsLetter(t[i + 1]) && char.IsLetter(t[i + 2])) codes++;
            return Math.Max(0.006, w - codes * 0.0021 * 2);
        }
        // 从 (x, y) 附近找一块 w×h 的空地：先在右边从 y 开始上下错开，再试左边、上面、下面。找不到给 null
        public double[] Place(double[] box, double y, double w, double h){
            var tries = new List<double[]>();
            for (int i = 0; i < 16; i++) {
                double dy = (i % 2 == 0 ? 1 : -1) * (i / 2) * 0.0055;
                tries.Add(new[] { box[2] + 0.004, y + dy });
            }
            for (int i = 0; i < 16; i++) { double dy = (i % 2 == 0 ? 1 : -1) * (i / 2) * 0.0055; tries.Add(new[] { box[0] - 0.004 - w, y + dy }); }
            for (int i = 0; i < 10; i++) { tries.Add(new[] { box[2] - w - i * 0.012, box[3] + 0.004 }); tries.Add(new[] { box[2] - w - i * 0.012, box[1] - 0.004 - h }); }
            foreach (var t in tries) { var r = new[] { t[0], t[1], t[0] + w, t[1] + h }; if (Free(r)) { Add(r); return r; } }
            return null;
        }
    }
}
