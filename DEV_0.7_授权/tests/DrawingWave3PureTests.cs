using System;
using System.Collections.Generic;

namespace TianGongCadSuite {
    // Wave 3 纯逻辑测试（不依赖天工 CAD / WinForms）：
    //   1. 定位销孔上下文判定（成对 / 伴随安装孔判 H7，成对 Ø10 准入，孤立单个无伴随孔不误标 H7）；
    //   2. 标准轴承孔（如 02-003 Ø28 带挡肩/卡簧槽）识别为 H7，非标径或浅沉孔不误判；
    //   3. 车床轴颈（k6）与圆柱定位销外径（g6）配合自动判定 + ISO 286 数值兜底查表；
    //   4. 直线导轨安装孔排识别（标准孔距 20/25/30/40/60/80mm × ≥3 螺纹孔）；
    //   5. 综合公差人工确认摘要（SummaryNote）与老版 Note() 兼容。
    public static class DrawingWave3PureTests {
        static void Check(bool cond, string msg){ if (!cond) throw new Exception("DrawingWave3PureTests FAIL: " + msg); }

        public static void Run(){
            TestContextPinHoles();
            TestBearingBoreAndShaftFits();
            TestGuideRailPatternDetection();
            TestSummaryNote();
            Console.WriteLine("DrawingWave3PureTests ALL PASSED");
        }

        static void TestContextPinHoles(){
            Check(DrawingTolerance.IsContextPinHole(6.0, 2, true, false, false, false), "paired 2xØ6 plain hole is dowel pin");
            Check(DrawingTolerance.IsContextPinHole(8.0, 1, true, false, false, true), "single Ø8 alongside M6 mounting holes is dowel pin");
            Check(!DrawingTolerance.IsContextPinHole(6.0, 1, true, false, false, false), "isolated single Ø6 with no companion mounting holes should not get false H7");
            Check(DrawingTolerance.IsContextPinHole(10.0, 2, true, false, false, true), "paired 2xØ10 alongside mounting holes qualifies as dowel pin");
            Check(!DrawingTolerance.IsContextPinHole(10.0, 1, true, false, false, true), "single Ø10 should not qualify as dowel pin");
            Check(!DrawingTolerance.IsContextPinHole(6.6, 4, true, false, false, true), "clearance Ø6.6 never qualifies");
        }

        static void TestBearingBoreAndShaftFits(){
            // 02-003 衬套块：Ø28 带同轴 Ø22 挡肩、深 18mm → 标准轴承孔 H7
            Check(DrawingTolerance.IsBearingBore(28.0, true, false, true, 18.0), "Ø28 stepped bore is bearing bore H7");
            Check(DrawingTolerance.IsBearingBore(47.0, true, false, true, 14.0), "Ø47 stepped bore is bearing bore H7");
            Check(!DrawingTolerance.IsBearingBore(28.0, true, false, false, 18.0), "plain Ø28 without shoulder/groove should not auto-H7");
            Check(!DrawingTolerance.IsBearingBore(28.0, true, false, true, 2.5), "shallow 2.5mm step is not bearing seat");
            Check(!DrawingTolerance.IsBearingBore(29.5, true, false, true, 10.0), "circlip groove Ø29.5 is not standard bearing OD");

            // 多台阶传动轴：Ø20 轴承位（带 Ø30 挡肩，长 25mm）→ k6；最大外径 Ø30 无挡肩 → 空
            var pJournal = new LatheDiameterPair { DiameterM = 0.020, XMin = 0.0, XMax = 0.025, IsInnerBore = false };
            var pBody = new LatheDiameterPair { DiameterM = 0.030, XMin = 0.025, XMax = 0.200, IsInnerBore = false };
            var allShaft = new List<LatheDiameterPair> { pJournal, pBody };
            Check(DrawingTolerance.ClassifyShaftDiameterFit(pJournal, allShaft, 200.0) == "k6", "bearing journal Ø20 with Ø30 shoulder gets k6");
            Check(DrawingTolerance.ClassifyShaftDiameterFit(pBody, allShaft, 200.0) == "", "main shaft body Ø30 gets no bearing fit");

            // 定位销 02-029（Ø12×25 单段精销）→ g6
            var pPin = new LatheDiameterPair { DiameterM = 0.012, XMin = 0.0, XMax = 0.025, IsInnerBore = false };
            Check(DrawingTolerance.ClassifyShaftDiameterFit(pPin, new List<LatheDiameterPair> { pPin }, 25.0) == "g6", "Ø12x25 locating pin gets g6");

            double u, l;
            Check(DrawingTolerance.ShaftFitDeviation("k6", 20.0, out u, out l) && Math.Abs(u - 0.015) < 1e-6 && Math.Abs(l - 0.002) < 1e-6, "Ø20 k6 = +0.015/+0.002");
            Check(DrawingTolerance.ShaftFitDeviation("g6", 12.0, out u, out l) && Math.Abs(u + 0.006) < 1e-6 && Math.Abs(l + 0.017) < 1e-6, "Ø12 g6 = -0.006/-0.017");
        }

        static void TestGuideRailPatternDetection(){
            // 模拟 02-031：5×M5 沿 Y 排成一列，间距 60mm（30, 90, 150, 210, 270）
            var circles = new List<DwgEdge> {
                DwgEdge.Circle(0.163, 0.030, 0.0021),
                DwgEdge.Circle(0.163, 0.090, 0.0021),
                DwgEdge.Circle(0.163, 0.150, 0.0021),
                DwgEdge.Circle(0.163, 0.210, 0.0021),
                DwgEdge.Circle(0.163, 0.270, 0.0021),
                // 非导轨的杂散通孔
                DwgEdge.Circle(0.020, 0.020, 0.0045)
            };
            var m = DrawingTolerance.DetectGuideRailPattern(circles, c => c.R < 0.003, new[] { 0.0, 0.0, 1.3, 0.3 });
            Check(m != null, "5xM5 @ 60mm must be detected as linear guide rail pattern");
            Check(!m.HorizontalRow && m.HoleCount == 5 && Math.Abs(m.PitchMm - 60.0) < 0.1, "detected 5-hole vertical rail at 60mm pitch");

            // 非标随机间距 37mm / 53mm 不应误判为导轨
            var randomHoles = new List<DwgEdge> {
                DwgEdge.Circle(0.050, 0.020, 0.0021),
                DwgEdge.Circle(0.087, 0.020, 0.0021),
                DwgEdge.Circle(0.140, 0.020, 0.0021)
            };
            Check(DrawingTolerance.DetectGuideRailPattern(randomHoles, c => true, new[] { 0.0, 0.0, 0.2, 0.1 }) == null, "non-standard pitch must not trigger guide rail rule");
        }

        static void TestSummaryNote(){
            Check(DrawingTolerance.SummaryNote(0, 0, 0, 0, 0, false, false, false) == "", "no tolerances -> empty note");
            string s = DrawingTolerance.SummaryNote(1, 2, 1, 0, 0, true, true, true);
            Check(s.Contains("2 个定位销孔") && s.Contains("1 个轴承孔 H7") && s.Contains("导轨安装面") && s.Contains("基准 A"), "summary lists all applied smart rules");
        }
    }
}
