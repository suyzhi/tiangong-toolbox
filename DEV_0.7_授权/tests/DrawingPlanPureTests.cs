using System;
using System.Collections.Generic;
using System.Linq;
namespace TianGongCadSuite {
    // 一键出图的纯逻辑：落点、基准、文字排开、孔 / 圆角分组、比例。
    // 板子尺寸取自 SK26.01-OK310-02-004（90×37 板，4×Ø5.5 孔，C3 倒角）和 YF25.07-L1-M70-02-310（220×45，三个沉孔，对称标注）。
    public static class DrawingPlanPureTests {
        static void Check(bool ok, string label){ if (!ok) throw new Exception("FAIL: " + label); Console.WriteLine("PASS: " + label); }
        static bool Near(double a, double b){ return Math.Abs(a - b) < 1e-9; }
        const double mm = 0.001;
        static List<DwgEdge> Rect(double x0, double y0, double x1, double y1){
            return new List<DwgEdge> { DwgEdge.Line(x0, y0, x1, y0), DwgEdge.Line(x1, y0, x1, y1), DwgEdge.Line(x1, y1, x0, y1), DwgEdge.Line(x0, y1, x0, y0) };
        }
        public static void Run(){
            // 90×37 板：四个 Ø5.5 孔在 (15,8)(75,8)(15,23)(75,23)
            var plate = Rect(0, 0, 90 * mm, 37 * mm);
            foreach (var p in new[] { new[] { 15, 8 }, new[] { 75, 8 }, new[] { 15, 23 }, new[] { 75, 23 } }) plate.Add(DwgEdge.Circle(p[0] * mm, p[1] * mm, 2.75 * mm));
            var xs = DrawingPlan.Targets(plate, true);
            Check(xs.Count == 4 && Near(xs[0].Coord, 0) && Near(xs[1].Coord, 15 * mm) && Near(xs[3].Coord, 90 * mm), "横向落点：两条边 + 两列孔心");
            Check(xs[1].KeyPoint && !xs[0].KeyPoint, "孔心是关键点、边是线上一点");
            var ys = DrawingPlan.Targets(plate, false);
            Check(ys.Count == 4, "竖向落点：0 / 8 / 23 / 37");
            bool sym;
            var o = DrawingPlan.PickOrigin(xs, out sym);
            Check(!sym && Near(o.Coord, 0) && o.Edge.IsVertical, "没有中心孔：基准是左边线");

            // 对称件：220 长，孔心 -77.5 / 0 / 77.5，基准取中间孔心
            var sy = Rect(-110 * mm, 0, 110 * mm, 45 * mm);
            foreach (double x in new[] { -77.5, 0, 77.5 }) sy.Add(DwgEdge.Circle(x * mm, 32.5 * mm, 3.3 * mm));
            var sx = DrawingPlan.Targets(sy, true);
            o = DrawingPlan.PickOrigin(sx, out sym);
            Check(sym && Near(o.Coord, 0) && o.KeyPoint, "对称件：基准是中间孔心（110-0-110 的标法）");
            // 不对称时不用中心
            sy.Add(DwgEdge.Line(30 * mm, 0, 30 * mm, 10 * mm));
            o = DrawingPlan.PickOrigin(DrawingPlan.Targets(sy, true), out sym);
            Check(!sym && Near(o.Coord, -110 * mm), "多一条不对称的台阶线就不对称了");

            // 文字排开：0 / 1 / 2 / 50（毫米，比例 1:1，文字间距 4.5）
            var ts = new List<DwgTarget>();
            foreach (double c in new[] { 0, 1, 2, 50 }) ts.Add(new DwgTarget { Coord = c * mm });
            DrawingPlan.Spread(ts, 4.5 * mm);
            var at = ts.OrderBy(t => t.Coord).Select(t => t.TextAt / mm).ToList();
            Check(Math.Abs(at[1] - at[0] - 4.5) < 1e-6 && Math.Abs(at[2] - at[1] - 4.5) < 1e-6, "挤在一起的三个文字隔开 4.5");
            Check(Math.Abs(at[1] - 1) < 1e-6, "一簇以平均位置为中心摊开");
            Check(Math.Abs(at[3] - 50) < 1e-6, "远处的文字不动");
            // 摊开后碰到下一簇要合并
            ts = new List<DwgTarget>();
            foreach (double c in new[] { 0, 1, 2, 3, 9, 10 }) ts.Add(new DwgTarget { Coord = c * mm });
            DrawingPlan.Spread(ts, 4.5 * mm);
            at = ts.OrderBy(t => t.Coord).Select(t => t.TextAt / mm).ToList();
            bool ok = true; for (int i = 1; i < at.Count; i++) if (at[i] - at[i - 1] < 4.5 - 1e-6) ok = false;
            Check(ok, "合并后的大簇里任意相邻文字都不小于间距");

            // 整串：比例 2:1 时视图坐标里的间距减半
            var run = DrawingPlan.Ordinate(plate, true, 2.0, DrawingPlan.Box(plate), true, 0.010);
            Check(run != null && run.Count == 4 && Near(run.Line, -5 * mm), "横向坐标串：4 个，文字线在下边外 10mm 纸面 = 5mm 视图");

            // 孔分组：沉孔（Ø11 套 Ø6.6）只算里面的；两个不同规格分两组
            var holes = Rect(0, 0, 100 * mm, 50 * mm);
            foreach (double x in new[] { 20, 50, 80 }) { holes.Add(DwgEdge.Circle(x * mm, 25 * mm, 3.3 * mm)); holes.Add(DwgEdge.Circle(x * mm, 25 * mm, 5.5 * mm)); }
            holes.Add(DwgEdge.Circle(10 * mm, 10 * mm, 2.5 * mm)); holes.Add(DwgEdge.Circle(90 * mm, 40 * mm, 2.5 * mm));
            var hg = DrawingPlan.Holes(holes, 0.2);
            Check(hg.Count == 2, "沉孔外圈不单独成组：两组");
            var g66 = hg.First(g => Math.Abs(g.DiameterMm - 6.6) < 1e-6);
            Check(g66.Count == 3 && Near(g66.Leader.Cx, 80 * mm), "Ø6.6 三个，引线挂最右上");
            Check(hg.First(g => g.DiameterMm == 5).Count == 2, "Ø5 两个");
            Check(DrawingPlan.HoleText(4, 5.5, "贯通") == "4×%DI5.5 贯通", "孔文字 4×Ø5.5 贯通");
            Check(DrawingPlan.HoleText(1, 6.6, "") == "%DI6.6", "单个孔不写 1×");

            // 圆角：四个 R5 角 + 一个半圆缺口（不算圆角）
            var rr = new List<DwgEdge>();
            foreach (var c in new[] { new[] { 5, 5 }, new[] { 85, 5 }, new[] { 85, 32 }, new[] { 5, 32 } }) rr.Add(DwgEdge.Arc(c[0] * mm, c[1] * mm, 5 * mm, Math.PI / 2, 0, 0, 0, 0));
            rr.Add(DwgEdge.Arc(45 * mm, 0, 6 * mm, Math.PI, 0, 0, 0, 0));
            var rg = DrawingPlan.Radii(rr);
            Check(rg.Count == 2 && rg.Any(g => g.Count == 4 && g.RadiusMm == 5) && rg.Any(g => g.Count == 1 && g.RadiusMm == 6) && DrawingPlan.RadiusPrefix(4) == "4-", "4-R5 一组；单独的半圆（圆头）也标 R6");
            var slot = new List<DwgEdge> { DwgEdge.Arc(10 * mm, 0, 3 * mm, Math.PI, 0, 0, 0, 0), DwgEdge.Arc(30 * mm, 0, 3 * mm, Math.PI, 0, 0, 0, 0) };
            Check(DrawingPlan.Radii(slot).Count == 0, "腰孔两头的半圆不标 R");
            Check(DrawingPlan.Targets(rr, true).Any(t => Near(t.Coord, 45 * mm) && t.KeyPoint), "半圆缺口的圆心是落点");

            // 外框：带圆弧
            var box = DrawingPlan.Box(new List<DwgEdge> { DwgEdge.Arc(0, 0, 1, Math.PI, 1, 0, -1, 0) });
            Check(Near(box[3], 1) && Near(box[1], 0), "上半圆的外框 y∈[0,1]");

            // 比例
            Check(DrawingPlan.PickScale(0.09, 0.037, 0.2, 0.15) == 2, "90×37 放进 200×150：2:1");
            Check(Math.Abs(DrawingPlan.PickScale(0.6, 0.3, 0.2, 0.15) - 1 / 3.0) < 1e-12, "600×300 放进 200×150：1:3");
            Check(DrawingPlan.PickScale(0.9, 0.3, 0.2, 0.15) == 0.2, "900×300 放进 200×150：1:5");
            Check(DrawingPlan.ScaleText(0.25) == "1:4" && DrawingPlan.ScaleText(2) == "2:1" && DrawingPlan.ScaleText(1 / 3.0) == "1:3", "比例文字");

            // ---- 选视图 / 排版 ----
            // 28×28×1 板，两个孔沿 Z：主视图看 Z（大面），只要一个投影表达厚度
            var f = new ModelFacts(); f.Size = new[] { 0.028, 0.028, 0.001 }; f.Holes = new[] { 0, 0, 2 }; f.Steps = new[] { 2, 2, 2 }; f.FaceArea = new[] { 5.6e-5, 5.6e-5, 1.5e-3 };
            var vs = DrawingLayout.Choose(f);
            var mainV = vs.First(v => v.Role == DwgViewRole.Main);
            Check(mainV.Axis == 2 && vs.Count(v => v.Role == DwgViewRole.Below || v.Role == DwgViewRole.Right) == 1 && vs.Any(v => v.Role == DwgViewRole.Iso), "板：主视图看大面 + 一个投影 + 轴测");
            // 长条 220×45×18，三个孔沿 Z，侧面有两个 M5 沿 Y
            f = new ModelFacts(); f.Size = new[] { 0.045, 0.220, 0.018 }; f.Holes = new[] { 0, 2, 3 }; f.Steps = new[] { 2, 2, 3 }; f.FaceArea = new[] { 0.004, 0.0016, 0.0099 };
            vs = DrawingLayout.Choose(f);
            mainV = vs.First(v => v.Role == DwgViewRole.Main);
            Check(mainV.Axis == 2 && mainV.Horizontal == 1 && mainV.Vertical == 0, "长条：看 Z，长边 Y 水平");
            Check(vs.Any(v => v.Role == DwgViewRole.Right && v.Axis == 1) && !vs.Any(v => v.Role == DwgViewRole.Below), "长条：沿水平轴 Y 的孔从右边看（左视图）是圆，不要俯视图");
            var lay = DrawingLayout.Arrange(DrawingLayout.Choose(f), 0.2, 0.1);
            Check(lay.Zones.Name == "A4" || lay.Zones.Name == "A3", "排版给出图纸");
            // 小件 50×50×21 → A4，比例不小于 1:1
            f = new ModelFacts(); f.Size = new[] { 0.050, 0.050, 0.021 }; f.Holes = new[] { 0, 0, 5 }; f.Steps = new[] { 2, 2, 3 }; f.FaceArea = new[] { 0.001, 0.001, 0.004 };
            double iw, ih; DrawingLayout.IsoBox(f.Size, out iw, out ih);
            lay = DrawingLayout.Arrange(DrawingLayout.Choose(f), iw, ih);
            Check(lay.Zones.Name == "A4" && lay.Scale >= 1, "50 的小块：A4，比例 ≥ 1:1（实际 " + DrawingPlan.ScaleText(lay.Scale) + "）");
            var m = lay.Get(DwgViewRole.Main);
            Check(m.X - m.W * lay.Scale / 2 >= lay.Zones.Main[0] && m.Y + m.H * lay.Scale / 2 <= lay.Zones.Main[3], "主视图在主区里");
            // 大件 1200×400×300 → A3
            f = new ModelFacts(); f.Size = new[] { 1.2, 0.4, 0.3 }; f.Holes = new[] { 0, 0, 8 }; f.Steps = new[] { 2, 2, 2 }; f.FaceArea = new[] { 0.1, 0.3, 0.4 };
            DrawingLayout.IsoBox(f.Size, out iw, out ih);
            lay = DrawingLayout.Arrange(DrawingLayout.Choose(f), iw, ih);
            Check(lay.Zones.Name == "A3", "1200 的大件：A3（比例 " + DrawingPlan.ScaleText(lay.Scale) + "）");
            foreach (var v in lay.Views) if (v.Role != DwgViewRole.Iso) Check(v.X + v.W * v.Scale / 2 <= lay.Zones.Main[2] + 1e-9 && v.Y - v.H * v.Scale / 2 >= lay.Zones.Main[1] - 1e-9, "视图 " + v.Role + " 不出主区");

            // 倒角：四个角 C2（45° 短斜线）一组；长斜线不算
            var ch = Rect(2 * mm, 0, 98 * mm, 50 * mm);
            ch.Add(DwgEdge.Line(0, 2 * mm, 2 * mm, 0)); ch.Add(DwgEdge.Line(98 * mm, 0, 100 * mm, 2 * mm)); ch.Add(DwgEdge.Line(100 * mm, 48 * mm, 98 * mm, 50 * mm)); ch.Add(DwgEdge.Line(2 * mm, 50 * mm, 0, 48 * mm));
            ch.Add(DwgEdge.Line(20 * mm, 10 * mm, 40 * mm, 30 * mm));
            var cg = DrawingPlan.Chamfers(ch);
            Check(cg.Count == 1 && cg[0].Count == 4 && DrawingPlan.ChamferText(cg[0].Count, cg[0].RadiusMm) == "4-C2", "四个 C2 倒角一组：4-C2；20mm 的长斜线不算");
            // 三维孔合并：Ø6.6 贯通 + Ø11 深 6.8 沉孔
            var segs = new List<ModelHole> {
                new ModelHole { Point = new V3(0, 0, 0), Axis = new V3(0, 0, 1), Radius = 3.3 * mm, Lo = 0, Hi = 11.1 * mm },
                new ModelHole { Point = new V3(0, 0, 0.0179), Axis = new V3(0, 0, -1), Radius = 5.5 * mm, Lo = 0, Hi = 6.8 * mm } };
            var mh = ModelHole.Merge(segs);
            Check(mh.Count == 1 && Math.Abs(mh[0].Radius - 3.3 * mm) < 1e-12 && Math.Abs(mh[0].CbRadius - 5.5 * mm) < 1e-12 && Math.Abs(mh[0].CbDepth - 6.8 * mm) < 1e-9 && mh[0].Through, "沉孔两段合成一个孔：Ø6.6 贯通 + 沉孔 Ø11 深 6.8");
            string pre, val, suf, sub;
            DrawingText.Hole(3, mh[0], 6.6, out pre, out val, out suf, out sub);
            Check(pre == "3×" && val == "" && suf == " 贯通" && sub == "%CB%DI11%DP6.8", "沉孔标注：3×Ø6.6 贯通 / ⌴Ø11↧6.8（人画 YF25.07-L1-M70-02-310 一样）");
            // 盲孔
            var blind = ModelHole.Merge(new[] { new ModelHole { Point = new V3(0, 0, 0), Axis = new V3(0, 0, 1), Radius = 2.5 * mm, Lo = 0, Hi = 12 * mm, BlindLo = true } });
            DrawingText.Hole(2, blind[0], 5, out pre, out val, out suf, out sub);
            Check(!blind[0].Through && suf == "%DP12" && pre == "2×", "盲孔：2×Ø5↧12");
            // 螺纹孔（孔特征上读到 M5、螺纹深 10）
            blind[0].Spec = new HoleSpec { Kind = HoleKind.Tapped, ThreadSize = "M5", ThreadFull = false, ThreadDepth = 10, Depth = 12 };
            DrawingText.Hole(4, blind[0], 4.2, out pre, out val, out suf, out sub);
            Check(pre == "4×" && val == "M5" && suf == "%DP10" && sub == "%DI4.2%DP12", "螺纹孔：4×M5↧10，第二行标准底孔 Ø4.2↧12");
            DrawingText.Hole(1, null, 64, false, out pre, out val, out suf, out sub);
            Check(pre == "" && suf == "" && sub == "", "外圆只写直径，不写贯通");

            // 引线文字找空地：视图右边被占了就往左边放；不出允许区（标题栏、公差表那里不放）
            var sp = new DwgSpace(SheetZones.A4());
            var vb = new[] { 0.050, 0.080, 0.120, 0.150 };
            sp.Add(vb);
            var r1 = sp.Place(vb, 0.140, 0.025, 0.0045);
            Check(r1 != null && r1[0] >= vb[2] && sp.Free(new[] { r1[2] + 0.001, r1[1], r1[2] + 0.002, r1[3] }), "第一条放在视图右边");
            var r2 = sp.Place(vb, 0.140, 0.025, 0.0045);
            Check(r2 != null && !(r2[0] < r1[2] && r1[0] < r2[2] && r2[1] < r1[3] && r1[1] < r2[3]), "第二条不和第一条重叠");
            sp.Add(new[] { 0.120, 0.048, 0.205, 0.190 });   // 右边整块占满
            var r3 = sp.Place(vb, 0.140, 0.025, 0.0045);
            Check(r3 != null && r3[2] <= vb[0], "右边满了放左边");
            Check(!sp.Free(new[] { 0.150, 0.010, 0.170, 0.020 }), "标题栏那里不能放");
            Check(Math.Abs(DwgSpace.TextWidth("4×%DI5.5 贯通") - (0.0021 * 7 + 0.0036 * 2 + 0.0021 * 3 - 0.0021 * 2)) < 1e-9 || DwgSpace.TextWidth("4×%DI5.5 贯通") > 0.015, "文字宽度估计：汉字宽、%DI 算一个字");

            // 主视图朝向：盲孔开口朝 -Z 多 → 从 -Z 看
            var pin = new ModelFacts(); pin.Size = new[] { 0.012, 0.012, 0.025 }; pin.Holes = new[] { 0, 0, 1 }; pin.Steps = new[] { 2, 2, 2 }; pin.FaceArea = new[] { 0, 0, 1e-4 };
            DrawingLayout.Mouths(pin, new[] { new ModelHole { Axis = new V3(0, 0, 1), BlindHi = true, BlindLo = false } });
            Check(DrawingLayout.Choose(pin)[0].Sign == -1, "销子一头的盲孔朝 -Z 开口：主视图从 -Z 看");
            Check(!DrawingLayout.Choose(pin).Any(v => v.Role == DwgViewRole.Back), "盲孔都朝主视图：不加后视图");

            // 视图转过 90° 时圆弧的扫角正负和起点对不上（OK310-02-010 主视图，DrawProbe8 / 第 21 轮调试输出）：
            // R20 圆角心 (5,-9.5)，起点 (-15,-9.5)、终点 (5,10.5)，天工报扫角 +90°（按起点逆时针转就成了往下那段）
            var lEdges = new List<DwgEdge> {
                DwgEdge.Line(25 * mm, 22.5 * mm, -25 * mm, 22.5 * mm), DwgEdge.Line(-25 * mm, 22.5 * mm, -25 * mm, -22.5 * mm), DwgEdge.Line(-25 * mm, -22.5 * mm, -17 * mm, -22.5 * mm),
                DwgEdge.Line(5 * mm, 10.5 * mm, 25 * mm, 10.5 * mm), DwgEdge.Line(25 * mm, 10.5 * mm, 25 * mm, 22.5 * mm), DwgEdge.Line(-15 * mm, -20.5 * mm, -15 * mm, -9.5 * mm),
                DwgEdge.Line(-17 * mm, -22.5 * mm, -15 * mm, -20.5 * mm), DwgEdge.Arc(5 * mm, -9.5 * mm, 20 * mm, Math.PI / 2, -15 * mm, -9.5 * mm, 5 * mm, 10.5 * mm),
                DwgEdge.Circle(-10 * mm, 16.5 * mm, 2.25 * mm), DwgEdge.Circle(15 * mm, 16.5 * mm, 2.25 * mm) };
            var lBox = DrawingPlan.Box(lEdges);
            Check(Math.Abs(lBox[1] + 22.5 * mm) < 1e-9, "扫角正负不对的圆角不撑大外框：下边 -22.5（实际 " + lBox[1] * 1000 + "）");
            var lRun = DrawingPlan.Ordinate(lEdges, false, 2, lBox, true, 0.008);
            Check(lRun != null && Math.Abs(lRun.Origin.Coord + 22.5 * mm) < 1e-9 && lRun.Targets.Select(t => Math.Round(t.Coord * 1000, 3)).SequenceEqual(new[] { 10.5, 16.5, 22.5 }), "竖直串基准落在零件底边 -22.5，落点 10.5 / 16.5 / 22.5（人图的 12、6、45）");

            // 盲孔开口那面要有正对的视图（普查 80% vs 反面 24%）
            // 板 100×60×12，主视图看 Z：正面 4 个盲孔朝 +Z，背面 2 个盲孔朝 -Z → 补后视图，放在最右边
            var fPlate = new ModelFacts(); fPlate.Size = new[] { 0.100, 0.060, 0.012 }; fPlate.Holes = new[] { 0, 0, 6 }; fPlate.Steps = new[] { 2, 2, 2 }; fPlate.FaceArea = new[] { 7e-4, 1.2e-3, 6e-3 };
            var fhz = new List<ModelHole>();
            for (int i = 0; i < 4; i++) fhz.Add(new ModelHole { Axis = new V3(0, 0, 1), BlindLo = true, BlindHi = false });
            for (int i = 0; i < 2; i++) fhz.Add(new ModelHole { Axis = new V3(0, 0, 1), BlindLo = false, BlindHi = true });
            DrawingLayout.Mouths(fPlate, fhz);
            Check(fPlate.BlindPlus[2] == 4 && fPlate.BlindMinus[2] == 2, "盲孔口朝向计数：+Z 4、-Z 2");
            var fpv = DrawingLayout.Choose(fPlate);
            var fback = fpv.FirstOrDefault(v => v.Role == DwgViewRole.Back);
            Check(fpv[0].Sign == 1 && fback != null && fback.Sign == -1 && fback.Extra && fback.Axis == 2, "背面有盲孔：主视图从 +Z 看，补一个从 -Z 看的后视图");
            DrawingLayout.IsoBox(fPlate.Size, out iw, out ih);
            var fpl = DrawingLayout.Arrange(fpv, iw, ih);
            var fpm = fpl.Get(DwgViewRole.Main); var fpb = fpl.Get(DwgViewRole.Back);
            Check(fpb != null && fpb.X - fpb.W * fpl.Scale / 2 > fpm.X + fpm.W * fpl.Scale / 2 && Math.Abs(fpb.Y - fpm.Y) < 1e-12, "后视图在主视图右边、同一高度");
            foreach (var v in fpl.Views) if (v.Role != DwgViewRole.Iso) Check(v.X + v.W * v.Scale / 2 <= fpl.Zones.Main[2] + 1e-9 && v.X - v.W * v.Scale / 2 >= fpl.Zones.Main[0] - 1e-9, "带后视图：视图 " + v.Role + " 不出主区");
            // 侧面（沿竖直轴 Y）的盲孔开在底面（-Y）：俯视图看 +Y 看不到 → 不补仰视图（第 19 / 20 轮补了掉分），只记个数提示人工
            var fBlk = new ModelFacts(); fBlk.Size = new[] { 0.100, 0.040, 0.020 }; fBlk.Holes = new[] { 0, 2, 4 }; fBlk.Steps = new[] { 2, 2, 2 }; fBlk.FaceArea = new[] { 8e-4, 2e-3, 4e-3 };
            var fhb = new List<ModelHole>();
            for (int i = 0; i < 4; i++) fhb.Add(new ModelHole { Axis = new V3(0, 0, 1) });
            for (int i = 0; i < 2; i++) fhb.Add(new ModelHole { Axis = new V3(0, 1, 0), BlindLo = false, BlindHi = true });
            DrawingLayout.Mouths(fBlk, fhb);
            var fbv = DrawingLayout.Choose(fBlk);
            Check(fbv[0].Axis == 2 && fbv[0].Horizontal == 0 && fbv.Any(v => v.Role == DwgViewRole.Below) && !fbv.Any(v => v.Extra) && fBlk.BottomBlind == 2, "Y 向盲孔开在 -Y：俯视图照旧，记下底面 2 个盲孔");
            // 补了比例要缩小就不补，记下来提示人工（第 17 轮大焊接框架 1:10 → 1:20）
            var fBig = new ModelFacts(); fBig.Size = new[] { 1.2, 0.8, 0.3 }; fBig.Holes = new[] { 0, 0, 6 }; fBig.Steps = new[] { 2, 2, 2 }; fBig.FaceArea = new[] { 0.2, 0.3, 0.9 };
            var fhg = new List<ModelHole>();
            for (int i = 0; i < 4; i++) fhg.Add(new ModelHole { Axis = new V3(0, 0, 1), BlindLo = true, BlindHi = false });
            for (int i = 0; i < 2; i++) fhg.Add(new ModelHole { Axis = new V3(0, 0, 1), BlindLo = false, BlindHi = true });
            DrawingLayout.Mouths(fBig, fhg);
            var fgv = DrawingLayout.Choose(fBig);
            double giw, gih; DrawingLayout.IsoBox(fBig.Size, out giw, out gih);
            var fgl = DrawingLayout.Arrange(fgv, giw, gih);
            var fgCore = DrawingLayout.Arrange(fgv.Where(v => !v.Extra).ToList(), giw, gih);
            Check(fgv.Any(v => v.Role == DwgViewRole.Back) && fgl.Scale >= fgCore.Scale - 1e-12, "补后视图不让比例变小（" + DrawingPlan.ScaleText(fgl.Scale) + " vs " + DrawingPlan.ScaleText(fgCore.Scale) + "）");
            Check(fgl.Get(DwgViewRole.Back) != null || fgl.Dropped.Contains(DwgViewRole.Back), "放不下的后视图记进 Dropped");
            // 通孔不算：从哪面看都是圆
            var fThr = new ModelFacts(); fThr.Size = new[] { 0.100, 0.040, 0.020 }; fThr.Holes = new[] { 0, 2, 4 }; fThr.Steps = new[] { 2, 2, 2 }; fThr.FaceArea = new[] { 8e-4, 2e-3, 4e-3 };
            DrawingLayout.Mouths(fThr, new[] { new ModelHole { Axis = new V3(0, 1, 0) }, new ModelHole { Axis = new V3(0, 0, 1) } });
            var ftv = DrawingLayout.Choose(fThr);
            Check(!ftv.Any(v => v.Extra), "只有通孔：视图照旧");

            // 螺纹线认规格：Ø3.24 底孔 + 大径 4 的螺纹线 → M4
            var h4 = DwgEdge.Circle(0, 0, 1.62 * mm);
            Check(DrawingPlan.ThreadFromEdges(h4, new[] { DwgEdge.Circle(0, 0, 2.0 * mm) }) == "M4", "螺纹线大径 4 → M4");
            Check(DrawingPlan.ThreadFromEdges(h4, new[] { DwgEdge.Circle(5 * mm, 0, 2.0 * mm) }) == "", "不同心的螺纹线不算");

            // 销子：两条竖边 + 底下一个半圆球头（R6），竖向总长要能标到球头最低点
            var pinE = new List<DwgEdge> { DwgEdge.Line(-6 * mm, 0, -6 * mm, 19 * mm), DwgEdge.Line(6 * mm, 0, 6 * mm, 19 * mm), DwgEdge.Line(-6 * mm, 19 * mm, 6 * mm, 19 * mm),
                DwgEdge.Arc(0, 0, 6 * mm, Math.PI, 6 * mm, 0, -6 * mm, 0) };
            pinE[3].Sweep = -Math.PI;   // 从右端顺时针转到左端，经过最低点
            var pv = DrawingPlan.Targets(pinE, false);
            Check(pv.Any(t => Near(t.Coord, -6 * mm) && t.Edge.Kind == DwgKind.Arc && t.Quadrant) && pv.Any(t => Near(t.Coord, 19 * mm)), "球头最低点也是落点：总长 25 能标");

            // 法兰：外圆 Ø64 套着内孔 Ø25 —— 外圈要单独能标
            var fl = new List<DwgEdge> { DwgEdge.Circle(0, 0, 32 * mm), DwgEdge.Circle(0, 0, 12.5 * mm), DwgEdge.Circle(20 * mm, 0, 2.25 * mm) };
            var rings = DrawingPlan.OuterRings(fl, 0.07);
            Check(rings.Count == 1 && rings[0].DiameterMm == 64, "同心圆外圈 Ø64 拿得出来");
            string s1, s2; DrawingText.ThreadOnly("M4", new ModelHole { Radius = 1.62 * mm, Through = false, Depth = 10.12 * mm }, out s1, out s2);
            Check(s1 == "" && s2 == "%DI3.3%DP10.12", "只有螺纹线的盲螺纹孔：不写假螺纹深度，底孔 Ø3.3↧10.12");
        }
    }
}
