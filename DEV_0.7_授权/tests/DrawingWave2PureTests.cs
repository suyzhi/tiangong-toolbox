using System;
using System.Collections.Generic;
using System.Linq;

namespace TianGongCadSuite {
    // Wave 2 纯逻辑测试（不依赖天工 CAD）：
    //   1. 细长件断裂视图规划（长宽比/比例门槛、均匀空段扫描、防切中孔/台阶、比例升级与短件豁免）；
    //   2. 多孔大板孔表规划（≥16 孔触发、框件/车床豁免、按规格分组 A1/A2/B1 编号、严格相对统一 3D 基准 0,0 换算毫米）；
    //   3. 局部放大详图聚类（大件/框架局部安装板孔群识别、定位销孔簇优先、大比例/松散孔豁免）；
    //   4. 斜孔向视图方向聚合与去重；
    //   5. 第 2 页多视图网格排版边界校验。
    public static class DrawingWave2PureTests {
        static void Check(bool cond, string msg){ if (!cond) throw new Exception("DrawingWave2PureTests FAIL: " + msg); }
        static bool Near(double a, double b, double tol){ return Math.Abs(a - b) <= tol; }

        public static void Run(){
            TestBreakViewSlenderPlate();
            TestBreakViewSkipsCenterFeatureOrShortPart();
            TestHoleTableTriggerAndUnifiedDatum();
            TestHoleTablePinCoaxialityAndChunks();
            TestDetailClusterOnFramePads();
            TestObliqueHoleViewsAndSheet2Layout();
            TestCalloutAvoidsOrdinateBandAndNarrowCrack();
            Console.WriteLine("DrawingWave2PureTests ALL PASSED");
        }

        // 1. 1325×40mm 细长件（两头各有孔，中间 900mm 均匀空段，当前 1:6）→ 必须切中间空段并升级比例
        static void TestBreakViewSlenderPlate(){
            var edges = new List<DwgEdge> {
                DwgEdge.Line(0, 0, 1.325, 0),
                DwgEdge.Line(0, 0.040, 1.325, 0.040),
                DwgEdge.Line(0, 0, 0, 0.040),
                DwgEdge.Line(1.325, 0, 1.325, 0.040),
                DwgEdge.Circle(0.030, 0.020, 0.005),
                DwgEdge.Circle(0.080, 0.020, 0.005),
                DwgEdge.Circle(1.250, 0.020, 0.005),
                DwgEdge.Circle(1.295, 0.020, 0.005)
            };
            var box = DrawingPlan.Box(edges);
            var plan = DrawingWave2Rules.PlanBreakView(edges, box, 1.0 / 6.0, 0.240, 0.140);
            Check(plan != null, "1325x40 slender bar at 1:6 should plan a break view");
            Check(plan.VerticalBreak, "horizontal slender bar should use vertical break lines");
            Check(plan.CutStartView > 0.095 && plan.CutEndView < 1.235, "break cut must stay clear of end holes");
            Check(plan.RemovedModelLen > 1.0, "should remove >1000mm empty middle span");
            Check(plan.UpgradedScale >= 0.5 - 1e-6, "upgraded scale should jump from 1:6 to at least 1:2, got " + plan.UpgradedScale);
        }

        // 2. 满布均匀孔的 1000mm 长板（每隔 100mm 都有孔，无 ≥25% 连续空段）或已 1:2 以上视图 → 不应误断裂
        static void TestBreakViewSkipsCenterFeatureOrShortPart(){
            var dense = new List<DwgEdge> {
                DwgEdge.Line(0, 0, 1.0, 0),
                DwgEdge.Line(0, 0.04, 1.0, 0.04),
                DwgEdge.Line(0, 0, 0, 0.04),
                DwgEdge.Line(1.0, 0, 1.0, 0.04)
            };
            for (int i = 1; i <= 9; i++) dense.Add(DwgEdge.Circle(i * 0.1, 0.02, 0.004));
            var box = DrawingPlan.Box(dense);
            Check(DrawingWave2Rules.PlanBreakView(dense, box, 0.2, 0.24, 0.14) == null, "uniformly populated holes every 100mm must not be broken");
            // 已经 1:2 的视图不再断裂
            var sparse = new List<DwgEdge> {
                DwgEdge.Line(0, 0, 0.5, 0), DwgEdge.Line(0, 0.04, 0.5, 0.04),
                DwgEdge.Line(0, 0, 0, 0.04), DwgEdge.Line(0.5, 0, 0.5, 0.04)
            };
            Check(DrawingWave2Rules.PlanBreakView(sparse, DrawingPlan.Box(sparse), 0.5, 0.24, 0.14) == null, "scale >= 1:2 should not break");
        }

        // 3. 多孔大板孔表触发门槛 + 相对统一 3D 基准原点精确换算（含中心对称 0,0 产生正负坐标）
        static void TestHoleTableTriggerAndUnifiedDatum(){
            Check(!DrawingWave2Rules.ShouldUseHoleTable(DrawingPartKind.Standard, 12, 16), "12 holes below 16 threshold");
            Check(DrawingWave2Rules.ShouldUseHoleTable(DrawingPartKind.Standard, 20, 16), "20 holes on Standard plate triggers Hole Table");
            Check(!DrawingWave2Rules.ShouldUseHoleTable(DrawingPartKind.ProfileFrame, 30, 16), "ProfileFrame must not trigger Hole Table");
            Check(!DrawingWave2Rules.ShouldUseHoleTable(DrawingPartKind.Lathe, 20, 16), "Lathe part must not trigger Hole Table");

            var g1 = new List<DwgEdge> { DwgEdge.Circle(0.12, 0.08, 0.0033), DwgEdge.Circle(0.08, 0.08, 0.0033), DwgEdge.Circle(0.10, 0.12, 0.0033) };
            var g2 = new List<DwgEdge> { DwgEdge.Circle(0.09, 0.10, 0.003), DwgEdge.Circle(0.11, 0.10, 0.003) };
            var groups = new List<KeyValuePair<string, List<DwgEdge>>> {
                new KeyValuePair<string, List<DwgEdge>>("Ø6.6 贯通", g1),
                new KeyValuePair<string, List<DwgEdge>>("Ø6 H7 贯通", g2)
            };
            // 设统一 3D 基准原点在 (0.10, 0.10)
            var rows = DrawingWave2Rules.BuildHoleTable(groups, 0.10, 0.10);
            Check(rows.Count == 5, "should emit 5 hole rows");
            Check(rows[0].Tag == "A1" && rows[1].Tag == "A2" && rows[2].Tag == "A3", "largest group gets A1..A3");
            Check(Near(rows[0].Xmm, -20.0, 0.01) && Near(rows[0].Ymm, -20.0, 0.01), "A1 sorted by X,Y relative to unified datum (-20,-20)");
            Check(rows[3].Tag == "B1" && rows[4].Tag == "B2", "second group gets B1..B2");
            Check(Near(rows[3].Xmm, -10.0, 0.01) && Near(rows[4].Xmm, 10.0, 0.01), "B1/B2 symmetric around datum X=0");
        }

        // 4. 焊接框架 1200×800（比例 1:6）上有 2 处 60×40 安装板密孔簇，含定位销孔簇应排第一
        static void TestDetailClusterOnFramePads(){
            var box = new[] { 0.0, 0.0, 1.2, 0.8 };
            var pin1 = DwgEdge.Circle(0.95, 0.65, 0.003);
            var circles = new List<DwgEdge> {
                // 簇 1（左下角 4 个普通螺栓孔，间距 25mm → 纸面 4.1mm）
                DwgEdge.Circle(0.10, 0.10, 0.0033), DwgEdge.Circle(0.125, 0.10, 0.0033),
                DwgEdge.Circle(0.10, 0.125, 0.0033), DwgEdge.Circle(0.125, 0.125, 0.0033),
                // 簇 2（右上角含定位销孔 + 2 个螺栓孔）
                pin1, DwgEdge.Circle(0.97, 0.65, 0.0033), DwgEdge.Circle(0.95, 0.67, 0.0033)
            };
            var clusters = DrawingWave2Rules.FindDetailClusters(circles, box, 1.0 / 6.0, c => ReferenceEquals(c, pin1), 2);
            Check(clusters.Count == 2, "should detect both dense mounting-pad clusters");
            Check(clusters[0].HasPinHole, "cluster containing dowel pin hole should rank first");
            Check(clusters[0].TargetScale >= 0.4, "detail view scale should magnify cluster cleanly, got " + clusters[0].TargetScale);
            // 若主视图比例已是 1:2，无需自动拉详图
            Check(DrawingWave2Rules.FindDetailClusters(circles, box, 0.5, null, 2).Count == 0, "large scale 1:2 should not create detail views");
        }

        // 5. 斜孔聚合去重 + 第 2 页多格排版边界检查
        static void TestObliqueHoleViewsAndSheet2Layout(){
            var range = new[] { 0.0, 0.0, 0.0, 0.2, 0.1, 0.05 };
            var ax45 = new V3(1, 0, 1).Unit();
            var holes = new List<ModelHole> {
                new ModelHole { Point = new V3(0.05, 0.05, 0.025), Axis = ax45, Radius = 0.003, BlindLo = true, BlindHi = false },
                new ModelHole { Point = new V3(0.15, 0.05, 0.025), Axis = ax45, Radius = 0.003, BlindLo = true, BlindHi = false },
                // 常规正交孔应被忽略
                new ModelHole { Point = new V3(0.10, 0.05, 0.0), Axis = new V3(0, 0, 1), Radius = 0.004 }
            };
            var ob = DrawingWave2Rules.PlanObliqueViews(holes, range);
            Check(ob.Count == 1, "two parallel 45° oblique holes should merge into 1 auxiliary view");
            Check(ob[0].IsOblique && ob[0].Caption == "向视图 A", "oblique caption should be 向视图 A");
            Check(Math.Abs(ob[0].SightDir.Dot(ob[0].HorzDir)) < 1e-6, "sight and horz axes must be orthogonal");

            var z = SheetZones.A3();
            var slots = DrawingWave2Rules.LayoutSheet2(z, new List<double[]> { new[] { 0.2, 0.1 }, new[] { 0.1, 0.1 }, new[] { 0.08, 0.08 } }, 1.0);
            Check(slots.Count == 3, "should allocate 3 slots on Sheet 2");
            foreach (var s in slots) {
                Check(s.X > z.Main[0] && s.X < z.Iso[2], "slot X within A3 usable width");
                Check(s.Y > z.Main[1] && s.Y < z.Main[3], "slot Y within A3 usable height");
                Check(s.Scale > 0 && s.Scale <= 1.0, "slot scale valid");
                // 严守右上未注公差表（A3 y>=247mm）下方与右图框留白
                Check(s.Y + s.BoxH * 0.5 <= z.Iso[3] - 0.008, "Sheet 2 slot top must stay cleanly below tolerance table (<=234mm), got " + (s.Y + s.BoxH * 0.5));
                Check(s.X + s.BoxW * 0.5 <= z.Iso[2] - 0.025, "Sheet 2 slot right must leave >=25mm callout margin, got " + (s.X + s.BoxW * 0.5));
            }
        }

        // 6. 孔表销孔同轴度规划 + 同轴定位销沉孔判定（02-031 2×Ø6.6贯通/Ø12 H7深5）
        static void TestHoleTablePinCoaxialityAndChunks(){
            Check(DrawingTolerance.IsConcentricLocatingPinBore(12.0, 6.6, 5.0, 2, false), "2x Ø12 depth 5 concentric with Ø6.6 is a locating pin bore");
            Check(!DrawingTolerance.IsConcentricLocatingPinBore(12.0, 6.6, 5.0, 2, true), "threaded bore is not a pin bore");
            Check(!DrawingTolerance.IsConcentricLocatingPinBore(12.0, 6.6, 2.0, 2, false), "too shallow bore (<3.5mm) is not a pin bore");
            Check(!DrawingTolerance.IsConcentricLocatingPinBore(11.0, 6.6, 5.0, 2, false), "non-standard Ø11 is not a pin bore");

            var inputs = new List<HoleTableGroupInput>();
            var gA = new HoleTableGroupInput { SpecText = "M5 深12", IsPinHole = false };
            for (int i = 0; i < 24; i++) gA.Circles.Add(DwgEdge.Circle(0.02 + i * 0.01, 0.05, 0.0021));
            var gPin = new HoleTableGroupInput { SpecText = "Ø6.6贯通/Ø12 H7深5", IsPinHole = true, GdtFrame = DrawingTolerance.HoleTableCoaxialityFrameA };
            gPin.Circles.Add(DwgEdge.Circle(0.04, 0.08, 0.0033));
            gPin.Circles.Add(DwgEdge.Circle(0.20, 0.08, 0.0033));
            inputs.Add(gA);
            inputs.Add(gPin);

            var rows = DrawingWave2Rules.BuildHoleTable(inputs, 0.0, 0.0);
            Check(rows.Count == 26, "26 rows built");
            Check(!rows[0].IsPinHole && rows[0].GdtFrame.Length == 0, "tapped row has no coaxiality frame");
            Check(rows[24].IsPinHole && rows[24].GdtFrame == DrawingTolerance.HoleTableCoaxialityFrameA, "pin row 1 carries coaxiality frame");
            Check(rows[25].IsPinHole && rows[25].GdtFrame == DrawingTolerance.HoleTableCoaxialityFrameA, "pin row 2 carries coaxiality frame");

            double totW, maxH;
            var chunks = DrawingWave2Rules.PlanHoleTableChunks(rows, 0.188, out totW, out maxH);
            Check(chunks.Count == 1, "26 rows with pin holes should fit into 1 clean unified 5-col table on A3, got " + chunks.Count);
            Check(chunks[0].HasPinHole && chunks[0].ColWidths.Length == 5, "chunk with pin holes must have 5 columns including GD&T");
            Check(chunks[0].RowH >= 0.0066, "pin chunk row height must accommodate 6mm FCF frame");
            Check(totW <= 0.125, "single 5-col table width should be compact <=125mm, got " + (totW * 1000));
            Check(maxH <= 0.188, "single 5-col table height must fit inside 188mm, got " + (maxH * 1000));
        }

        // 7. 引线落位避开整条顶部坐标文字带及两视图间狭窄缝隙（镜像 型材02-0516 场景）
        static void TestCalloutAvoidsOrdinateBandAndNarrowCrack(){
            var z = SheetZones.A3();
            var sp = new DwgSpace(z);
            var mainBox = new[] { 0.020, 0.154, 0.183, 0.175 };
            var mainObs = new[] { 0.018, 0.152, 0.185, 0.177 };
            var rightObs = new[] { 0.197, 0.152, 0.279, 0.177 };
            sp.AddView(mainObs);
            sp.AddView(rightObs);
            // 登记主视图上方整条连续坐标带
            var ordBand = new[] { 0.018, 0.176, 0.186, 0.199 };
            sp.Add(ordBand);

            double[] placed, elbow;
            bool ok = sp.PlaceCompact(0.178, 0.165, mainBox, mainObs, 0.024, 0.007, 0.0, true, false, null, out placed, out elbow);
            Check(ok && placed != null, "PlaceCompact should find clean free slot");
            bool hitsOrd = placed[0] < ordBand[2] && ordBand[0] < placed[2] && placed[1] < ordBand[3] && ordBand[1] < placed[3];
            Check(!hitsOrd, "callout must not overlap solid top ordinate band");
            bool hitsRight = placed[0] < rightObs[2] && rightObs[0] < placed[2] && placed[1] < rightObs[3] && rightObs[1] < placed[3];
            Check(!hitsRight, "callout must not overlap adjacent Right view");

            // 验证兜底网格搜索同样不会压出右边界
            double[] fbPlaced, fbElbow;
            bool fbOk = sp.FindFallbackSlot(0.178, 0.165, mainBox, 0.030, 0.008, 0.0, out fbPlaced, out fbElbow);
            Check(fbOk && fbPlaced != null, "FindFallbackSlot should locate free slot");
            Check(fbPlaced[2] <= z.Width - 0.008, "fallback slot must stay inside right border");
        }
    }
}
