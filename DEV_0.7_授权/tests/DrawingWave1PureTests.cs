using System;
using System.Collections.Generic;
using System.Linq;

namespace TianGongCadSuite {
    // 「一键出图」Wave 1 升级纯逻辑回归测试：
    //   1. 全视图 3D XYZ 基准统一（含镜像视图不换边、对称居中、缺中心落点时统一回退）；
    //   2. 单根铝型材 T 型槽屏蔽 + 型材框架坐标落点精简；
    //   3. 圆周均布孔 PCD（3/6/8 孔法兰必认、矩形板 4 角孔不误杀）、腰孔、斜面/锥尖角度；
    //   4. 车床回转件自动识别、视图精简、外径/内径配对及阶梯式轴向线性规划（开放环不封闭链）。
    public static class DrawingWave1PureTests {
        static void Check(bool ok, string label){ if (!ok) throw new Exception("FAIL: " + label); Console.WriteLine("PASS: " + label); }
        static bool Near(double a, double b, double tol){ return Math.Abs(a - b) <= tol; }
        const double mm = 0.001;

        static List<DwgEdge> Rect(double x0, double y0, double x1, double y1){
            return new List<DwgEdge> {
                DwgEdge.Line(x0, y0, x1, y0), DwgEdge.Line(x1, y0, x1, y1),
                DwgEdge.Line(x1, y1, x0, y1), DwgEdge.Line(x0, y1, x0, y0)
            };
        }

        public static void Run(){
            TestUnifiedDatum3D();
            TestProfileAndFrame();
            TestPcdSlotsAndAngles();
            TestLathePipeline();
        }

        static void TestUnifiedDatum3D(){
            // 非对称块 100×60×20：range = [0.01, 0.02, 0.03, 0.11, 0.08, 0.05]
            var range = new[] { 0.01, 0.02, 0.03, 0.11, 0.08, 0.05 };
            var f = new ModelFacts { Size = new[] { 0.10, 0.06, 0.02 }, Holes = new[] { 0, 0, 2 }, Steps = new[] { 2, 2, 2 }, FaceArea = new[] { 1e-3, 2e-3, 6e-3 } };
            f.PlaneOffsets[0].AddRange(new[] { 0.01, 0.11 });
            f.PlaneOffsets[1].AddRange(new[] { 0.02, 0.08 });
            f.PlaneOffsets[2].AddRange(new[] { 0.03, 0.05 });
            var mainSpec = new DwgViewSpec { Role = DwgViewRole.Main, Axis = 2, Sign = 1, Horizontal = 0, Vertical = 1 };
            var d3 = ModelDatum3D.Compute(range, f, new List<ModelHole>(), mainSpec);
            Check(!d3.Symmetric[0] && !d3.Symmetric[1] && !d3.Symmetric[2], "非对称件：XYZ 均采用外轮廓面作 0");
            Check(Near(d3.Coord[0], 0.01, 1e-9) && Near(d3.Coord[1], 0.02, 1e-9) && Near(d3.Coord[2], 0.03, 1e-9), "3D 基准锁定在主视图左边/下边/背面");

            // 模拟后视图（镜像：vx = (-1, 0, 0)，视图坐标 x∈[0, 0.10]，则 pu=-0.11, ou=0.11）
            double backProjX = d3.ProjectToView(0, new V3(-1, 0, 0), 0.11);
            Check(Near(backProjX, 0.10, 1e-9), "后视图镜像投影：原 X_min(0.01) 精确落到后视图右侧 x=0.10");
            var backPlate = Rect(0, 0, 0.10, 0.06);
            backPlate.Add(DwgEdge.Circle(0.03, 0.03, 0.003));
            var backRun = DrawingPlan.Ordinate(backPlate, true, 1.0, DrawingPlan.Box(backPlate), false, 0.008, 0, null, false, backProjX, false);
            Check(backRun != null && Near(backRun.Origin.Coord, 0.10, 1e-6), "镜像后视图的 0 基准强制锁在右侧 0.10，绝不偷偷换回左侧 0");

            // 模拟俯视图（Z 朝下：vy = (0,0,-1)，视图 y∈[0, 0.02]，pv=-0.05, ov=0.05）与左视图（Z 朝右：vx=(0,0,1)，x∈[0,0.02]，pu=0.03, ou=-0.03）
            double belowZ = d3.ProjectToView(2, new V3(0, 0, -1), 0.05);
            double rightZ = d3.ProjectToView(2, new V3(0, 0, 1), -0.03);
            Check(Near(belowZ, 0.02, 1e-9) && Near(rightZ, 0.00, 1e-9), "俯视图与左视图在相反屏幕朝向下锁定同一物理 Z=0.03 背面");

            // 全局对称件（220×45×20，中心孔在 X=0，且两侧对称孔 ±77.5）
            var symRange = new[] { -0.11, 0, 0, 0.11, 0.045, 0.02 };
            var symF = new ModelFacts { Size = new[] { 0.22, 0.045, 0.02 } };
            symF.PlaneOffsets[0].AddRange(new[] { -0.11, 0.11 });
            symF.PlaneOffsets[1].AddRange(new[] { 0, 0.045 });
            symF.PlaneOffsets[2].AddRange(new[] { 0, 0.02 });
            var symHoles = new List<ModelHole> {
                new ModelHole { Point = new V3(-0.0775, 0.0225, 0), Axis = new V3(0, 0, 1), Radius = 0.003 },
                new ModelHole { Point = new V3(0, 0.0225, 0), Axis = new V3(0, 0, 1), Radius = 0.003 },
                new ModelHole { Point = new V3(0.0775, 0.0225, 0), Axis = new V3(0, 0, 1), Radius = 0.003 }
            };
            var dSym = ModelDatum3D.Compute(symRange, symF, symHoles, mainSpec);
            Check(dSym.Symmetric[0] && Near(dSym.Coord[0], 0.0, 1e-9), "全局对称件 X 轴自动锁定中心面 0");
            dSym.FallbackToEdge(0);
            Check(!dSym.Symmetric[0] && Near(dSym.Coord[0], -0.11, 1e-9), "对称基准一致性回退可切回外轮廓端面");
        }

        static void TestProfileAndFrame(){
            // 1. 单根 40×40×800 铝型材（16 张全长 T 型槽平面）
            var pf = new ModelFacts {
                Size = new[] { 0.800, 0.040, 0.040 },
                Steps = new[] { 2, 12, 12 },
                FullSpanParallelPlanes = new[] { 24, 0, 0 },
                BodyCount = 1,
                Holes = new[] { 0, 0, 2 }
            };
            int ext;
            var pk = DrawingProfileRules.ClassifyProfileOrFrame(pf, "ABB11-G408-4040", "6063-T5", out ext);
            Check(pk == DrawingPartKind.ProfileSingle && ext == 0, "40×40×800 铝型材识别为 ProfileSingle，拉伸轴 X");

            // 侧面视图：外轮廓 0..40 + 10 条全长 T 型槽平行线 + 2 个侧孔
            var side = Rect(0, 0, 800 * mm, 40 * mm);
            foreach (double y in new[] { 4, 8, 12, 16, 20, 24, 28, 32, 36 }) side.Add(DwgEdge.Line(0, y * mm, 800 * mm, y * mm));
            side.Add(DwgEdge.Circle(100 * mm, 20 * mm, 4.5 * mm));
            side.Add(DwgEdge.Circle(700 * mm, 20 * mm, 4.5 * mm));
            var filtSide = DrawingProfileRules.FilterSingleProfileView(side, false, true, DrawingPlan.Box(side));
            var yTs = DrawingPlan.Targets(filtSide, false);
            var xTs = DrawingPlan.Targets(filtSide, true);
            Check(yTs.Count == 3, "铝型材侧面视图屏蔽全部 9 条 T 槽线，竖向只剩 0 / 20(孔心) / 40（实际 " + yTs.Count + "）");
            Check(xTs.Count == 4, "铝型材侧面视图横向保留 0 / 100 / 700 / 800 四个关键加工坐标");

            // 端面视图：40×40 截面一堆 T 型槽线 → 过滤后只剩外形 4 边 + 中心圆孔
            var end = Rect(0, 0, 40 * mm, 40 * mm);
            foreach (double t in new[] { 6, 11, 16, 24, 29, 34 }) {
                end.Add(DwgEdge.Line(t * mm, 5 * mm, t * mm, 35 * mm));
                end.Add(DwgEdge.Line(5 * mm, t * mm, 35 * mm, t * mm));
            }
            end.Add(DwgEdge.Circle(20 * mm, 20 * mm, 3.4 * mm));
            var filtEnd = DrawingProfileRules.FilterSingleProfileView(end, true, false, DrawingPlan.Box(end));
            Check(DrawingPlan.Targets(filtEnd, true).Count == 3 && DrawingPlan.Targets(filtEnd, false).Count == 3, "铝型材端面图屏蔽内部槽线，只留 40×40 外形与中心孔");

            // 2. 空心焊接/型材框架（1940×885×530，填充率 0.08，20 个阶梯）
            var ff = new ModelFacts {
                Size = new[] { 1.940, 0.885, 0.530 },
                Steps = new[] { 22, 18, 10 },
                FillRatio = 0.08,
                BodyCount = 1
            };
            var fk = DrawingProfileRules.ClassifyProfileOrFrame(ff, "YF25.07G01-L1-M30-02-007", "Q235-B", out ext);
            Check(fk == DrawingPartKind.ProfileFrame, "空心大焊接框架识别为 ProfileFrame");
            var frameEdges = Rect(0, 0, 1940 * mm, 885 * mm);
            for (int i = 1; i <= 25; i++) frameEdges.Add(DwgEdge.Line((i * 70) * mm, 0, (i * 70) * mm, (i % 3 == 0 ? 885 : 30) * mm));
            frameEdges.Add(DwgEdge.Circle(100 * mm, 100 * mm, 3 * mm));
            var simp = DrawingProfileRules.SimplifyFrameTargets(DrawingPlan.Targets(frameEdges, true), frameEdges, true, DrawingPlan.Box(frameEdges), 0, 14);
            Check(simp.Count <= 14 && simp.Any(t => Near(t.Coord, 0, 1e-6)) && simp.Any(t => Near(t.Coord, 1.94, 1e-6)) && simp.Any(t => Near(t.Coord, 0.1, 1e-6)), "框架坐标落点精简到 ≤14 且必保总长与安装孔位");
        }

        static void TestPcdSlotsAndAngles(){
            // 1. 6 孔均布法兰（Ø120 外圆，Ø80 分度圆 6×Ø6.6）
            var flange = new List<DwgEdge> { DwgEdge.Circle(0, 0, 60 * mm), DwgEdge.Circle(0, 0, 20 * mm) };
            var hg6 = new HoleGroup { DiameterMm = 6.6 };
            for (int i = 0; i < 6; i++) {
                double rad = (i * 60.0 + 30.0) * Math.PI / 180.0;
                var c = DwgEdge.Circle(40 * mm * Math.Cos(rad), 40 * mm * Math.Sin(rad), 3.3 * mm);
                flange.Add(c); hg6.Circles.Add(c);
            }
            var pcd6 = DrawingFeatureRules.TryDetectPcd(hg6, flange);
            Check(pcd6 != null && Near(pcd6.DiameterMm, 80.0, 0.1) && pcd6.FullEqualSpacing && !pcd6.NeedsStartAngle, "6 孔均布法兰识别为 PCD Ø80（30° 对称半步角不重复报偏角）");

            // 矩形板 4 角孔（无同心圆外轮廓）不应误判为 PCD
            var rect4 = Rect(0, 0, 80 * mm, 80 * mm);
            var hg4 = new HoleGroup { DiameterMm = 5.5 };
            foreach (var p in new[] { new[] { 15, 15 }, new[] { 65, 15 }, new[] { 65, 65 }, new[] { 15, 65 } }) {
                var c = DwgEdge.Circle(p[0] * mm, p[1] * mm, 2.75 * mm);
                rect4.Add(c); hg4.Circles.Add(c);
            }
            Check(DrawingFeatureRules.TryDetectPcd(hg4, rect4) == null, "矩形板 4 角安装孔绝不误判为 PCD 分度圆");

            // 圆形法兰 4 孔应识别为 PCD
            var circ4 = new List<DwgEdge> { DwgEdge.Circle(40 * mm, 40 * mm, 50 * mm) };
            circ4.AddRange(hg4.Circles);
            Check(DrawingFeatureRules.TryDetectPcd(hg4, circ4) != null, "圆形法兰上的 4 孔均布正确识别为 PCD");

            // 2. 腰孔（槽宽 6，圆心距 10，总长 16）
            var slotEdges = Rect(0, 0, 80 * mm, 40 * mm);
            slotEdges.Add(DwgEdge.Arc(25 * mm, 20 * mm, 3 * mm, Math.PI, 25 * mm, 23 * mm, 25 * mm, 17 * mm));
            slotEdges.Add(DwgEdge.Arc(35 * mm, 20 * mm, 3 * mm, Math.PI, 35 * mm, 17 * mm, 35 * mm, 23 * mm));
            slotEdges.Add(DwgEdge.Line(25 * mm, 23 * mm, 35 * mm, 23 * mm));
            slotEdges.Add(DwgEdge.Line(25 * mm, 17 * mm, 35 * mm, 17 * mm));
            var sgs = DrawingFeatureRules.FindSlots(slotEdges);
            Check(sgs.Count == 1 && Near(sgs[0].WidthMm, 6.0, 0.05) && Near(sgs[0].TotalLengthMm, 16.0, 0.05) && sgs[0].CalloutText(true, 0) == "腰孔 6×16 贯通", "腰孔识别与标准文字输出正确");

            // 3. 对称锥尖角度（仿 02-010 定位销约 67.4° 锥尖）
            double halfRad = (67.4 / 2.0) * Math.PI / 180.0;
            double dy = 15 * mm * Math.Tan(halfRad);
            var cone = new List<DwgEdge> {
                DwgEdge.Line(0, dy, 15 * mm, 0),
                DwgEdge.Line(0, -dy, 15 * mm, 0),
                DwgEdge.Line(0, -dy, 0, dy)
            };
            var angs = DrawingFeatureRules.FindAngles(cone);
            Check(angs.Count == 1 && angs[0].IsIncludedCone && Near(angs[0].AngleDeg, 67.4, 0.2), "对称锥尖识别出夹角 67.4°");
        }

        static void TestLathePipeline(){
            // 台阶轴 Ø30 / Ø20，总长 120，无端面孔无台阶内孔 → 只要主视图 + 轴测图
            var shaft = new ModelFacts {
                Size = new[] { 0.120, 0.030, 0.030 },
                RevAxis = 0,
                RevMaxOuterDiameter = 0.030,
                RevSurfaceArea = 0.011,
                TotalSurfaceArea = 0.013,
                Holes = new[] { 0, 0, 0 },
                Steps = new[] { 3, 0, 0 }
            };
            Check(DrawingLatheRules.IsLathePart(shaft), "台阶光轴识别为车床回转件");
            var svs = DrawingLatheRules.ChooseViews(shaft);
            Check(svs.Any(v => v.Role == DwgViewRole.Main && v.Horizontal == 0) && !svs.Any(v => v.Role == DwgViewRole.Right) && !svs.Any(v => v.Role == DwgViewRole.Below), "纯台阶光轴：主视图水平放置，自动省略多余端面图和俯视图");

            // 矩形衬套块（50×46×21 中间 Ø28 孔）绝不误判为车床回转件
            var block = new ModelFacts {
                Size = new[] { 0.050, 0.046, 0.021 },
                RevAxis = 2,
                RevMaxOuterDiameter = 0.028,
                RevSurfaceArea = 0.002,
                TotalSurfaceArea = 0.008
            };
            Check(!DrawingLatheRules.IsLathePart(block), "矩形衬套块不误判为车床件，保持板件+剖视模式");

            // 车床主视图外径提取 + 内部键槽边过滤 + 阶梯式轴向尺寸规划（4 个台阶坐标 → 2 段 + 1 总长，主动留最长段作开放环）
            var shaft2d = new List<DwgEdge> {
                DwgEdge.Line(0, 15 * mm, 40 * mm, 15 * mm), DwgEdge.Line(0, -15 * mm, 40 * mm, -15 * mm),
                DwgEdge.Line(40 * mm, 10 * mm, 120 * mm, 10 * mm), DwgEdge.Line(40 * mm, -10 * mm, 120 * mm, -10 * mm),
                // 落在 Ø30 内部的 12mm 键槽边，非剖视图绝不能误识别为 Ø12 外径
                DwgEdge.Line(5 * mm, 6 * mm, 35 * mm, 6 * mm), DwgEdge.Line(5 * mm, -6 * mm, 35 * mm, -6 * mm),
                DwgEdge.Line(0, -15 * mm, 0, 15 * mm), DwgEdge.Line(40 * mm, -15 * mm, 40 * mm, 15 * mm), DwgEdge.Line(120 * mm, -10 * mm, 120 * mm, 10 * mm)
            };
            var dias = DrawingLatheRules.ExtractDiameters(shaft2d, 0, false, 0.030);
            Check(dias.Count == 2 && Near(dias[0].DiameterMm, 30.0, 0.05) && Near(dias[1].DiameterMm, 20.0, 0.05), "水平车床主视图提取出 Ø30 与 Ø20 两段外径（自动过滤内部 12mm 键槽边）");

            // 同径多道卡簧槽（如 2 道 1.95mm 宽 Ø38 槽）：识别为 IsUndercut 且自动合并 Count=2
            var groovedShaft = new List<DwgEdge> {
                DwgEdge.Line(0, 20 * mm, 100 * mm, 20 * mm), DwgEdge.Line(0, -20 * mm, 100 * mm, -20 * mm),
                DwgEdge.Line(100 * mm, 19 * mm, 101.95 * mm, 19 * mm), DwgEdge.Line(100 * mm, -19 * mm, 101.95 * mm, -19 * mm),
                DwgEdge.Line(101.95 * mm, 20 * mm, 150 * mm, 20 * mm), DwgEdge.Line(101.95 * mm, -20 * mm, 150 * mm, -20 * mm),
                DwgEdge.Line(150 * mm, 19 * mm, 151.95 * mm, 19 * mm), DwgEdge.Line(150 * mm, -19 * mm, 151.95 * mm, -19 * mm),
                DwgEdge.Line(151.95 * mm, 20 * mm, 200 * mm, 20 * mm), DwgEdge.Line(151.95 * mm, -20 * mm, 200 * mm, -20 * mm)
            };
            var gdias = DrawingLatheRules.ExtractDiameters(groovedShaft, 0, false, 0.040);
            var groovePair = gdias.FirstOrDefault(p => Near(p.DiameterMm, 38.0, 0.05));
            Check(groovePair != null && groovePair.IsUndercut && groovePair.Count == 2, "同规格双卡簧槽识别为 IsUndercut 并自动合并数量 2×Ø38");

            // 单头 +Z 盲孔车床轴：HorzSign 自动取 -1 使盲孔口朝左正对 GB 第一角左视图
            var blindShaft = new ModelFacts {
                Size = new[] { 0.04, 0.04, 0.47 }, Holes = new[] { 0, 0, 1 }, BlindPlus = new[] { 0, 0, 1 },
                RevAxis = 2, RevMaxOuterDiameter = 0.04, RevSurfaceArea = 0.06, TotalSurfaceArea = 0.07, PartKind = DrawingPartKind.Lathe
            };
            Check(DrawingLayout.Choose(blindShaft).First(v => v.Role == DwgViewRole.Main).HorzSign == -1, "车床轴 +Z 盲孔自动翻转主视图水平轴使端面孔可见");

            var ts = DrawingPlan.Targets(shaft2d, true);
            var spans = DrawingLatheRules.PlanSteppedAxial(ts);
            Check(spans.Count == 2 && spans.Any(s => s.IsOverall && Near(s.SpanM, 0.120, 1e-6)) && spans.Any(s => !s.IsOverall && Near(s.SpanM, 0.040, 1e-6)), "阶梯式轴向尺寸：标总长 120 + 短台阶 40，自动留 80mm 开放环不封闭链");
        }
    }
}
