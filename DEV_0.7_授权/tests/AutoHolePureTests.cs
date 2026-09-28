using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace TianGongCadSuite {
    // 自动打孔的纯逻辑测试（规格反推、排孔、配孔检查、孔形状）。不引用任何 CAD / WinForms 类型，
    // 可以脱离天工 CAD 单独编译运行（见 tests/pure）。原生 CAD 测试与界面状态测试在 AutoHoleTests.cs。
    public static partial class AutoHoleTests {
        static int checks;
        static void Assert(bool ok, string label){ if (!ok) throw new Exception("FAIL: " + label); checks++; Console.WriteLine("PASS: " + label); }

        // ---------- 纯逻辑：规格反推 ----------
        public static void Pure(){
            // ICAN 的两个真实样例：8.5 -> M10 沉孔；13.5 -> M12 螺纹孔
            var a = HoleMatcher.Match(8.5);
            Assert(a.HasRow && a.Row.Size == "M10", "Φ8.5 -> M10");
            Assert(a.ReferenceIsTapped, "Φ8.5 判定为螺纹底孔");
            Assert(a.Target.Kind == HoleKind.Counterbore, "螺纹底孔 -> 目标件配沉孔");
            Assert(Math.Abs(a.Target.HoleDiameter - 11.0) < 1e-9, "M10 沉孔通孔Φ11");
            Assert(Math.Abs(a.Target.CounterboreDiameter - 18.0) < 1e-9, "M10 沉孔Φ18");
            Assert(Math.Abs(a.Target.CounterboreDepth - 11.0) < 1e-9, "M10 沉孔深11");

            var b = HoleMatcher.Match(13.5);
            Assert(b.HasRow && b.Row.Size == "M12", "Φ13.5 -> M12");
            Assert(!b.ReferenceIsTapped, "Φ13.5 判定为过孔");
            Assert(b.Target.Kind == HoleKind.Tapped, "过孔 -> 目标件配螺纹孔");
            // 螺纹孔按「螺纹内小径」建模（与 CAD 自带 ISOHOLES.TXT 一致），不再是攻丝底孔
            Assert(Math.Abs(b.Target.HoleDiameter - 10.106) < 1e-9, "M12 螺纹孔按内小径Φ10.106 建模");
            Assert(Math.Abs(b.Target.HoleDiameter - b.Row.MinorDia) < 1e-9, "螺纹孔直径 = 该规格内小径");

            var c = HoleMatcher.Match(5.0);
            Assert(c.HasRow && c.Row.Size == "M6" && c.ReferenceIsTapped, "Φ5.0 -> M6 底孔");

            var d = HoleMatcher.Match(6.6);
            Assert(d.HasRow && d.Row.Size == "M6" && !d.ReferenceIsTapped, "Φ6.6 -> M6 过孔");

            // 边界：13.0 是 M12 精配过孔，12.7 距它只有 0.3，应当仍命中 M12
            var near = HoleMatcher.Match(12.7);
            Assert(near.HasRow && near.Row.Size == "M12", "Φ12.7 距 M12 精配过孔 0.3，仍命中 M12");
            // 未命中标准表 -> 同径通孔，且明确告知（7.5 距所有标准值都 > 0.35）
            var e = HoleMatcher.Match(7.5);
            Assert(!e.HasRow, "Φ7.5 未命中标准表（最近标准值 6.8/8.4/8.5 均超出容差）");
            Assert(e.Target.Kind == HoleKind.Through && Math.Abs(e.Target.HoleDiameter - 7.5) < 1e-9, "未命中按同径通孔");
            Assert(HoleMatcher.StatusLine(e).Contains("未匹配"), "未命中状态栏有提示");

            // 状态栏文案
            Assert(HoleMatcher.StatusLine(a).Contains("M10") && HoleMatcher.StatusLine(a).Contains("沉孔"), "状态栏显示 M10 沉孔");
            Assert(HoleMatcher.StatusLine(b).Contains("M12") && HoleMatcher.StatusLine(b).Contains("螺纹孔"), "状态栏显示 M12 螺纹孔");

            // 手工指定规格
            var row = HoleMatcher.Find("M8"); Assert(row.HasValue, "找到 M8");
            var tap = HoleMatcher.FromRow(row.Value, HoleKind.Tapped);
            Assert(tap.Kind == HoleKind.Tapped && Math.Abs(tap.HoleDiameter - 6.647) < 1e-9, "手工 M8 螺纹孔按内小径Φ6.647");
            Assert(tap.Bottom == HoleBottom.Flat && !tap.Chamfer, "新孔规格默认平底、不倒角");
            Assert(Math.Abs(tap.BottomAngle - 118) < 1e-9, "V 型孔底默认 118°");
            Assert(tap.Through && tap.Summary.Contains("贯通"), "手工规格默认贯通");
            var blind = tap.Clone(); blind.Depth = 8; blind.Bottom = HoleBottom.VBottom;
            Assert(blind.Summary.Contains("V 底 118°"), "盲孔摘要写明 V 型底角度");
            var ch = tap.Clone(); ch.Chamfer = true; ch.Depth = 6;
            Assert(ch.Summary.Contains("孔口倒角 0.5×45°"), "摘要写明孔口倒角");
            var cb = HoleMatcher.FromRow(row.Value, HoleKind.Counterbore);
            Assert(Math.Abs(cb.CounterboreDiameter - 14.5) < 1e-9 && Math.Abs(cb.CounterboreDepth - 8.6) < 1e-9, "手工 M8 沉孔Φ14.5深8.6");

            // 表本身单调、无重复
            Assert(HoleMatcher.Table.Length == 9, "螺纹表 9 个规格");
            for (int i = 1; i < HoleMatcher.Table.Length; i++)
                Assert(HoleMatcher.Table[i].TapDrill > HoleMatcher.Table[i-1].TapDrill, "螺纹表底孔单调递增 " + HoleMatcher.Table[i].Size);
            for (int i = 0; i < HoleMatcher.Table.Length; i++) {
                var r2 = HoleMatcher.Table[i];
                Assert(r2.MinorDia > 0 && r2.MinorDia < r2.TapDrill, "内小径小于攻丝底孔 " + r2.Size);
                Assert(HoleMatcher.Match(r2.MinorDia).Row.Size == r2.Size, "按内小径也能反推到同规格 " + r2.Size);
            }

            // 非法输入
            try { HoleMatcher.Match(0); throw new Exception("zero accepted"); } catch (ArgumentException) { Assert(true, "拒绝 0 直径"); }
            try { HoleMatcher.Match(double.NaN); throw new Exception("NaN accepted"); } catch (ArgumentException) { Assert(true, "拒绝 NaN 直径"); }

            // 逆变换往返（装配坐标 <-> 零件坐标）
            var t = Transform.Frame(new V3(0.3,-0.2,0.5), new V3(1,0,0), new V3(0,1,0), new V3(0,0,1));
            var p0 = new V3(0.11,0.22,0.33);
            var back = t.InversePoint(t.Point(p0));
            Assert((back-p0).Length < 1e-12, "Transform 逆变换往返一致");
            var rot = Transform.Frame(new V3(1,2,3), new V3(0,1,0), new V3(-1,0,0), new V3(0,0,1));
            var n0 = new V3(0,0,1);
            Assert((rot.InverseNormal(rot.Normal(n0))-n0).Length < 1e-12, "Transform 逆法向往返一致");
            Assert(rot.Rigid, "旋转帧判定为刚体变换");

            // ---- 排孔 ----
            var div = new HolePatternSpec { Kind = HolePatternKind.Divide, Count = 4, EdgeMm = 10 };
            var o = HolePatternSolver.Offsets(120, div);
            Assert(o.Length == 4, "等分 4 孔");
            Assert(Math.Abs(o[0] - 10) < 1e-9 && Math.Abs(o[3] - 110) < 1e-9, "等分首末孔落在边距上");
            Assert(Math.Abs(o[1] - 43.3333333333) < 1e-6, "等分间距均等");
            var one = new HolePatternSpec { Kind = HolePatternKind.Divide, Count = 1, EdgeMm = 10 };
            Assert(Math.Abs(HolePatternSolver.Offsets(120, one)[0] - 60) < 1e-9, "单孔居中");

            var pit = new HolePatternSpec { Kind = HolePatternKind.Pitch, PitchMm = 50, EdgeMm = 10 };
            var op = HolePatternSolver.Offsets(120, pit);
            Assert(op.Length == 3, "定距 50mm 在 120-2×10 内排 3 孔，实排 " + op.Length);
            Assert(Math.Abs(op[2] - 110) < 1e-9, "定距末孔");
            Assert(HolePatternSolver.PitchCount(120, pit) == 3, "定距预览孔数一致");

            var cir = new HolePatternSpec { Kind = HolePatternKind.Circular, Count = 6 };
            var ang = HolePatternSolver.Angles(cir);
            Assert(ang.Length == 6 && Math.Abs(ang[1] - 60) < 1e-9, "圆周 6 孔间隔 60°");
            var cir4 = new HolePatternSpec { Kind = HolePatternKind.Circular, Count = 4, StartAngleDeg = 45 };
            var a4 = HolePatternSolver.Angles(cir4);
            Assert(Math.Abs(a4[0] - 45) < 1e-9 && Math.Abs(a4[2] - 225) < 1e-9, "圆周起始角生效");

            try { HolePatternSolver.Offsets(20, new HolePatternSpec { Kind = HolePatternKind.Divide, Count = 2, EdgeMm = 10 }); throw new Exception("accepted"); }
            catch (ArgumentException) { Assert(true, "拒绝边距吃掉全部长度"); }
            try { HolePatternSolver.Offsets(0, div); throw new Exception("accepted"); }
            catch (ArgumentException) { Assert(true, "拒绝零长度"); }
            try { HolePatternSolver.Angles(new HolePatternSpec { Kind = HolePatternKind.Circular, Count = 1 }); throw new Exception("accepted"); }
            catch (ArgumentException) { Assert(true, "圆周少于 2 孔被拒"); }

            // ---- 配孔检查 ----
            var h1 = new HoleRecord { Center = new V3(0,0,0), Axis = new V3(0,0,1), DiameterMm = 8.5, PartName = "A" };
            var h2 = new HoleRecord { Center = new V3(0,0,0.02), Axis = new V3(0,0,1), DiameterMm = 11.0, PartName = "B" };
            Assert(HoleCheck.GroupByAxis(new List<HoleRecord>{ h1, h2 }).Count == 1, "同轴孔归为一组");
            var off = new HoleRecord { Center = new V3(0.005,0,0.02), Axis = new V3(0,0,1), DiameterMm = 11.0, PartName = "B" };
            Assert(HoleCheck.GroupByAxis(new List<HoleRecord>{ h1, off }).Count == 2, "偏心 5mm 不算同轴");
            var tilted = new HoleRecord { Center = new V3(0,0,0.02), Axis = new V3(0,0.2,1).Unit(), DiameterMm = 11.0, PartName = "B" };
            Assert(HoleCheck.GroupByAxis(new List<HoleRecord>{ h1, tilted }).Count == 2, "轴线倾斜超过 2° 不算同轴");
            // 同一零件、同一轴线上的两个不同直径（= 圆柱沉孔的过孔 + 沉孔）必须归成一组。
            // 采集顺序会让"沉孔那一条"先入组，实现里如果只拿 g[0] 做比较，
            // 另一条就会因为横向差 >0.2mm 被拆成第二条假轴线。
            var cb1 = new HoleRecord { Center = new V3(0,0,0.03), Axis = new V3(0,0,1), DiameterMm = 11.0, PartName = "B" };
            var cb2 = new HoleRecord { Center = new V3(0,0,0.01), Axis = new V3(0,0,1), DiameterMm = 5.5, PartName = "B" };
            Assert(HoleCheck.GroupByAxis(new List<HoleRecord>{ cb1, cb2 }).Count == 1, "沉孔的 Φ11/Φ5.5 两条边算同一条轴线");
            // 反平行的轴线也算同一条：圆柱边的轴线方向来自几何法向，同一个物理孔读出来
            // 可能是 (0,0,1) 也可能是 (0,0,-1)（实测同一块板的沉孔与过孔就是这样）。
            var flip1 = new HoleRecord { Center = new V3(0,0,0.03), Axis = new V3(0,0,1), DiameterMm = 11.0, PartName = "B" };
            var flip2 = new HoleRecord { Center = new V3(0,0,0.01), Axis = new V3(0,0,-1), DiameterMm = 5.5, PartName = "B" };
            Assert(HoleCheck.GroupByAxis(new List<HoleRecord>{ flip1, flip2 }).Count == 1, "轴线方向相反的同一个孔算同一条轴线");
            Assert(HoleCheck.SameHoleLine(flip1, flip2, 10.0), "SameHoleLine 也认反平行的同一条轴线");

            string whyPair;
            Assert(HoleCheck.IsValidPair(8.5, 11.0, out whyPair), "M10 底孔 + M10 过孔 是合法配做");
            Assert(HoleCheck.IsValidPair(13.5, 10.2, out whyPair), "M12 过孔 + M12 底孔 是合法配做");
            Assert(!HoleCheck.IsValidPair(8.5, 6.8, out whyPair), "M10 底孔 + M8 底孔 配不上（" + whyPair + "）");
            Assert(HoleCheck.IsValidPair(7.5, 8.0, out whyPair), "非标准表内、直径差 ≤1mm 放过");

            var aligned = HoleCheck.Run(new List<HoleRecord>{ h1, h2 }, null);
            Assert(aligned.Count == 0, "同轴且配做正确 -> 无问题，实得 " + aligned.Count);
            // 偏心 5mm 的两孔不在同一组，各自成孤孔；默认不报，要求报孤孔时才报 2 条
            Assert(HoleCheck.Run(new List<HoleRecord>{ h1, off }, null).Count == 0, "默认不报孤孔");
            var unpaired = HoleCheck.Run(new List<HoleRecord>{ h1, off }, null, true);
            Assert(unpaired.Count == 2 && unpaired[0].Kind == HoleIssueKind.Unpaired, "要求报孤孔时得 2 条，实得 " + unpaired.Count);
            // 横向错开 0.5mm：超出 0.2mm 同轴容差，但轴线仍在对方孔内 -> 同一组，报"孔偏了"
            // （原来分组容差也是 0.2mm，这两个孔会被拆成两个默认隐藏的孤孔，"孔偏了"永远报不出来）
            var nearHole = new HoleRecord { Center = new V3(0.0005,0,0.02), Axis = new V3(0,0,1), DiameterMm = 11.0, PartName = "B" };
            var nearHoleRun = HoleCheck.Run(new List<HoleRecord>{ h1, nearHole }, null, true);
            Assert(nearHoleRun.Count == 1 && nearHoleRun[0].Kind == HoleIssueKind.Misaligned
                   && nearHoleRun[0].Message.Contains("0.5mm"), "横向错开 0.5mm -> 孔偏了，实得 " + Describe(nearHoleRun));
            Assert(HoleCheck.Run(new List<HoleRecord>{ h1, nearHole }, null).Count == 1, "孔偏了不受“忽略孤孔”影响");
            MisalignmentAndRay();
            var wrongSpec = HoleCheck.Run(new List<HoleRecord>{
                new HoleRecord{ Center=new V3(0,0,0), Axis=new V3(0,0,1), DiameterMm=8.5, PartName="A" },
                new HoleRecord{ Center=new V3(0,0,0.02), Axis=new V3(0,0,1), DiameterMm=6.8, PartName="B" } }, null);
            Assert(wrongSpec.Count == 1 && wrongSpec[0].Kind == HoleIssueKind.WrongSpec, "M10 底孔配 M8 底孔 -> 配错孔");

            // 漏打孔：装配里有 A、B 两个零件，射线说 A 的孔穿过了 B，但 B 上没有孔
            var parts = new List<string>{ "A", "B" };
            var miss = HoleCheck.Run(new List<HoleRecord>{ h1 }, (h, part) => part == "B", false, parts);
            Assert(miss.Count == 1 && miss[0].Kind == HoleIssueKind.MissingHole, "射线判据 -> 漏打孔，实得 " + miss.Count);
            Assert(HoleCheck.Run(new List<HoleRecord>{ h1 }, (h, part) => false, false, parts).Count == 0, "射线未命中 -> 不报漏孔");
            // 只给"有孔的零件"当全集时，漏孔发现不了——这正是 allParts 存在的理由
            Assert(HoleCheck.Run(new List<HoleRecord>{ h1 }, (h, part) => part == "B").Count == 0,
                   "不给零件全集时无法判漏孔（说明 allParts 是必需的）");

            // 小孔（<3mm）不参与连接孔检查
            var tiny = HoleCheck.Run(new List<HoleRecord>{ new HoleRecord{ Center=new V3(), Axis=new V3(0,0,1), DiameterMm=2.0, PartName="A" } }, null);
            Assert(tiny.Count == 0, "Φ2 小孔不当作连接孔");

            // ---- 沉孔不能被当成"配错孔"（同一零件同轴线上两个不同直径）----
            // 圆柱沉孔在实体上有两条圆边：Φ11 过孔 + Φ18 沉孔外径，采集时都会记录下来。
            // 配做比较必须拿"与螺钉杆配合的那个直径"（较小者），拿 Φ18 去比对方零件的
            // 螺纹底孔 Φ8.5 必然对不上 —— 会对一个打得完全正确的组合误报配错孔。
            var withCounterbore = new List<HoleRecord>{
                new HoleRecord{ Center=new V3(0,0,0),    Axis=new V3(0,0,1), DiameterMm=8.5, PartName="A" },
                new HoleRecord{ Center=new V3(0,0,0.02), Axis=new V3(0,0,1), DiameterMm=11.0, PartName="B" },
                new HoleRecord{ Center=new V3(0,0,0.02), Axis=new V3(0,0,1), DiameterMm=18.0, PartName="B" } };
            var cbIssues = HoleCheck.Run(withCounterbore, null);
            Assert(cbIssues.Count == 0, "沉孔 Φ11/Φ18 + 螺纹底孔 Φ8.5 不算配错孔，实得 " + cbIssues.Count
                   + (cbIssues.Count > 0 ? "：" + cbIssues[0].Message : ""));
            // 反向确认：拿沉孔外径去比确实会误报（说明上面那条断言测的是真问题）
            Assert(!HoleCheck.IsValidPair(8.5, 18.0, out whyPair), "拿沉孔外径 Φ18 比 Φ8.5 会判配不上（这正是要避免的误报）");

            // ---- 匹配歧义：相邻规格的容差窗口重叠时，不能静默替用户选一个 ----
            // Φ3.45 离 M3 过孔 3.4 是 0.05、离 M4 底孔 3.3 是 0.15：两种解读都说得通，
            // 而它们要求目标件打的孔**完全不同**（一个攻丝、一个配沉孔）。必须提示。
            var amb = HoleMatcher.Match(3.45);
            Assert(amb.HasRow && amb.Row.Size == "M3" && !amb.ReferenceIsTapped, "Φ3.45 首选 M3 过孔");
            Assert(amb.Ambiguous, "Φ3.45 应被标为有歧义（M4 底孔 3.3 只差 0.15mm）");
            Assert(amb.AmbiguousWith.Contains("M4"), "歧义提示里写明另一种解读是 M4：" + amb.AmbiguousWith);
            Assert(HoleMatcher.StatusLine(amb).Contains(HoleMatcher.AmbiguousHint), "状态栏必须提示用户手动确认：" + HoleMatcher.StatusLine(amb));
            // 8.5 更典型：M10 攻丝底孔与 M8 过孔只差 0.1mm，物理含义相反
            var dual = HoleMatcher.Match(8.5);
            Assert(dual.Ambiguous, "Φ8.5（M10 底孔 / M8 过孔只差 0.1）必须提示歧义，实得 "
                   + (dual.HasRow ? dual.Row.Size : "-") + " gap=" + dual.AmbiguousDeviation.ToString("0.###"));
            Assert(HoleMatcher.Match(3.4).Ambiguous, "Φ3.4：M3 过孔 3.4 与 M4 底孔 3.3 差 0.1，也必须提示");
            Assert(HoleMatcher.Match(6.6).Ambiguous, "Φ6.6：M6 过孔 6.6 与 M8 内小径 6.647 差 0.047，也必须提示");
            // 两个候选差得够开 -> 自动推断可靠，不提示（否则每个标准孔都弹提示）
            Assert(!HoleMatcher.Match(5.0).Ambiguous, "Φ5.0 精确命中 M6 底孔（最近的其他标准值差 0.3），不该报歧义");
            Assert(!HoleMatcher.Match(13.5).Ambiguous, "Φ13.5 精确命中 M12 过孔，不该报歧义");
            // 离得远的不该报歧义（Φ12.7 首选 M12 过孔差 0.3，次选 M16 差 1.1）
            Assert(!HoleMatcher.Match(12.7).Ambiguous, "Φ12.7 不该报歧义");

            // ---- 锥形沉孔默认值：按沉头螺钉头径（2×头半径），不是"沉孔直径+1" ----
            var m8 = HoleMatcher.Find("M8").Value;
            Assert(Math.Abs(HoleMatcher.CountersinkDiameterFor(m8) - 13.0) < 1e-9,
                   "M8 锥形沉孔默认锥孔Φ13（= 2×头半径 6.5），实得 " + HoleMatcher.CountersinkDiameterFor(m8));
            var sink = HoleMatcher.FromRow(m8, HoleKind.Countersink);
            Assert(Math.Abs(sink.CountersinkDiameter - 13.0) < 1e-9 && Math.Abs(sink.CountersinkAngle - 90) < 1e-9,
                   "M8 锥形沉孔：Φ13 / 90°");
            Assert(sink.Note.Contains("估算"), "锥形沉孔的默认值是估算值，摘要里要说清楚：" + sink.Note);
            for (int i = 0; i < HoleMatcher.Table.Length; i++) {
                var r3 = HoleMatcher.Table[i];
                Assert(r3.HeadRadius > 0, r3.Size + " 表里有沉头头半径（锥形沉孔默认值要用）");
            }

            // 注意：源码里是"没有发现问题"，"没有问题"并不是它的连续子串
            string sum = HoleCheck.Summary(new List<HoleIssue>(), 10, 4);
            Assert(sum.Contains("没有发现"), "无问题时摘要文案，实得 [" + sum + "]");
            Assert(HoleCheck.Summary(new List<HoleIssue>{ new HoleIssue{ Kind = HoleIssueKind.MissingHole } }, 10, 4).Contains("漏打孔 1"), "有漏孔时摘要分类计数");
            ShapePure();
            Console.WriteLine("AUTO-HOLE PURE ASSERTIONS " + checks);
        }

        // ---------- 纯逻辑：孔形状（2D 剖面 / 3D 参考都吃这一份定义） ----------
        // 预览画出来的形状如果不等于 CAD 实际切出来的形状，那这个预览就是在骗人。
        // 所以这里逐项钉住几何：每一项都注明是"CAD 实测"还是"表值"。
        static void ShapePure(){
            // ① 孔口倒角的实测语义：Setback 是径向增量，Angle 是从孔轴量，轴向深 = Setback/tan(Angle)。
            //    依据 tools/ChamferProbe.cs 的切除体积（Φ6 通孔、10mm 板）：
            //      2/60° 实测环体积 26.602 mm³，本式 26.602；2/30° 实测 79.807，本式 79.807；
            //      3/60° 实测 65.297，本式 65.297。另外三种解释分别差 3 倍以上。
            Assert(Math.Abs(HoleShapeBuilder.ChamferDepthMm(0.5, 45) - 0.5) < 1e-9, "45° 倒角：轴向深 = 径向增量");
            Assert(Math.Abs(HoleShapeBuilder.ChamferDepthMm(2, 60) - 2 / Math.Tan(Math.PI / 3)) < 1e-9,
                   "60° 倒角：轴向深 = Setback/tan(60°) = " + (2 / Math.Tan(Math.PI / 3)).ToString("0.####"));
            Assert(Math.Abs(HoleShapeBuilder.ChamferDepthMm(2, 30) - 2 / Math.Tan(Math.PI / 6)) < 1e-9, "30° 倒角轴向深");
            Assert(HoleShapeBuilder.ChamferDepthMm(2, 60) < HoleShapeBuilder.ChamferDepthMm(2, 30),
                   "角度越大倒角越浅（和「角度越大越深」的直觉相反，是实测结论）");

            // ② V 型钻尖：角度是钻尖夹角，高度 = r/tan(角度/2)。CAD 实测 coneH = r/tan(59°)。
            Assert(Math.Abs(HoleShapeBuilder.TipHeightMm(3, 118) - 3 / Math.Tan(59 * Math.PI / 180)) < 1e-9,
                   "118° 钻尖高度 = r/tan(59°)");
            // 与原生测试里那一行逐字对齐（那边用米，这边用毫米，所以要 ×1000）：
            //   double coneH = 0.0024585 / Math.Tan(59.0 * Math.PI / 180.0);
            double nativeConeHmm = 0.0024585 / Math.Tan(59.0 * Math.PI / 180.0) * 1000.0;
            Assert(Math.Abs(HoleShapeBuilder.TipHeightMm(2.4585, 118) - nativeConeHmm) < 1e-9,
                   "与原生测试里的 coneH 公式一致：" + HoleShapeBuilder.TipHeightMm(2.4585, 118).ToString("0.####")
                   + " vs " + nativeConeHmm.ToString("0.####"));

            // ③ 锥形沉孔锥座：h = (R - r)/tan(角度/2)。CAD 实测 Φ11/90° 收口到 Φ5 时 h = 3mm。
            Assert(Math.Abs(HoleShapeBuilder.CountersinkDepthMm(5.5, 2.5, 90) - 3.0) < 1e-9, "Φ11/90° 锥座深 3mm");
            Assert(Math.Abs(HoleShapeBuilder.CountersinkDepthMm(6.5, 3.0, 90) - 3.5) < 1e-9, "Φ13/90° 锥座深 3.5mm");

            // ④ 通孔剖面：轴线 -> 孔口 -> 孔底 -> 轴线，深度就是板厚
            var th = new HoleSpec { Kind = HoleKind.Through, HoleDiameter = 6, Depth = 0 };
            var sh = HoleShapeBuilder.Build(th, 10);
            Assert(sh.Wall.Count == 4, "通孔轮廓 4 个点，实得 " + sh.Wall.Count);
            Assert(Math.Abs(sh.Wall[1].R - 3) < 1e-9 && Math.Abs(sh.Wall[1].Z) < 1e-9, "孔口在 (r=3, z=0)");
            Assert(Math.Abs(sh.Wall[2].R - 3) < 1e-9 && Math.Abs(sh.Wall[2].Z - 10) < 1e-9, "孔底在 (r=3, z=板厚)");
            Assert(Math.Abs(sh.Wall[3].R) < 1e-9 && Math.Abs(sh.Wall[3].Z - 10) < 1e-9, "轮廓收在轴线上");
            Assert(sh.VoidDepthMm == 10, "贯通孔的孔深 = 参考板厚");

            // ⑤ 盲孔 + V 型底：圆柱段到 (深-钻尖高)，再收到轴线
            var blind = new HoleSpec { Kind = HoleKind.Through, HoleDiameter = 6, Depth = 10, Bottom = HoleBottom.VBottom, BottomAngle = 118 };
            var sb = HoleShapeBuilder.Build(blind, 0);
            double tip = 3 / Math.Tan(59 * Math.PI / 180);
            Assert(Math.Abs(sb.TipHeightMm - tip) < 1e-9, "V 底钻尖高 " + tip.ToString("0.###"));
            Assert(Math.Abs(sb.Wall[sb.Wall.Count - 2].Z - (10 - tip)) < 1e-9, "圆柱段止于 深-钻尖高");
            Assert(Math.Abs(sb.Wall[sb.Wall.Count - 1].Z - 10) < 1e-9, "钻尖落在名义深度上");
            Assert(!sb.Through && sb.ThicknessMm > 10, "盲孔参考板厚必须大于孔深，实得 " + sb.ThicknessMm);

            // ⑥ 平底盲孔：轮廓直接走到底，没有钻尖
            var flat = new HoleSpec { Kind = HoleKind.Through, HoleDiameter = 6, Depth = 10, Bottom = HoleBottom.Flat };
            var sf = HoleShapeBuilder.Build(flat, 0);
            Assert(sf.TipHeightMm == 0, "平底没有钻尖高度");
            Assert(!sf.Through && sf.ThicknessMm >= 12, "平底盲孔板厚留了余料 " + sf.ThicknessMm);

            // ⑦ 圆柱沉孔：孔口是沉孔直径，沉孔底再收到主孔直径
            var cbSpec = new HoleSpec { Kind = HoleKind.Counterbore, ThreadSize = "M6", HoleDiameter = 6.6,
                                        CounterboreDiameter = 11, CounterboreDepth = 6.5, Depth = 0 };
            var sc = HoleShapeBuilder.Build(cbSpec, 12);
            Assert(Math.Abs(sc.MouthDiameterMm - 11) < 1e-9, "沉孔的孔口直径 = 沉孔直径");
            Assert(sc.Wall.Count == 6, "沉孔轮廓 6 个点，实得 " + sc.Wall.Count);
            Assert(Math.Abs(sc.Wall[1].R - 5.5) < 1e-9 && Math.Abs(sc.Wall[2].Z - 6.5) < 1e-9, "沉孔直壁 Φ11 深 6.5");
            Assert(Math.Abs(sc.Wall[3].R - 3.3) < 1e-9, "沉孔底收到通孔 Φ6.6");

            // ⑧ 锥形沉孔：孔口 Φ，锥座结束后是主孔
            var csSpec = new HoleSpec { Kind = HoleKind.Countersink, HoleDiameter = 5, CountersinkDiameter = 11, CountersinkAngle = 90, Depth = 0 };
            var ss = HoleShapeBuilder.Build(csSpec, 10);
            Assert(Math.Abs(ss.CountersinkDepMm - 3.0) < 1e-9, "Φ11/90° 锥座深 3");
            Assert(Math.Abs(ss.Wall[2].R - 2.5) < 1e-9 && Math.Abs(ss.Wall[2].Z - 3.0) < 1e-9, "锥座收口到 Φ5、深 3");

            // ⑨ 孔口倒角后孔口变大：径向增量 = Setback
            var chSpec = new HoleSpec { Kind = HoleKind.Through, HoleDiameter = 6, Depth = 0, Chamfer = true, ChamferSetback = 0.5, ChamferAngle = 45 };
            var sc2 = HoleShapeBuilder.Build(chSpec, 10);
            Assert(Math.Abs(sc2.MouthDiameterMm - 7.0) < 1e-9, "Φ6 加 0.5 倒角 -> 孔口 Φ7，实得 " + sc2.MouthDiameterMm);
            Assert(chSpec.Kind == HoleKind.Through, "（前置）孔型还是通孔");
            // 锥形沉孔本身就是一个锥座，CAD 那边明确跳过倒角，预览也不能给它叠一个
            var csCh = csSpec.Clone(); csCh.Chamfer = true;
            Assert(!HoleShapeBuilder.Build(csCh, 10).Chamfer, "锥形沉孔不叠加孔口倒角（与 CAD 侧一致）");

            // ⑩ 螺纹孔：实体孔是内小径，装饰螺纹用公称直径，所以装饰螺纹一定比孔大
            var tapSpec = HoleMatcher.FromRow(HoleMatcher.Find("M6").Value, HoleKind.Tapped);
            var st = HoleShapeBuilder.Build(tapSpec, 0);
            Assert(st.Tapped && Math.Abs(st.HoleDiameterMm - 4.917) < 1e-9, "M6 螺纹孔实体孔径 = 内小径 4.917");
            Assert(Math.Abs(st.ThreadMajorMm - 6) < 1e-9, "M6 装饰螺纹公称直径 6");
            Assert(st.CosmeticThread, "装饰螺纹要比实体孔大，才画得出来");

            // ⑪ 参考板厚：贯通孔按孔径给，盲孔保证孔底有余料，都取到 0.5
            Assert(HoleShapeBuilder.ReferenceThicknessMm(th) >= 3, "Φ6 通孔的示意板厚不至于薄到看不出");
            for (int i = 0; i < HoleMatcher.Table.Length; i++) {
                var r = HoleMatcher.Table[i];
                var mt = HoleShapeBuilder.Build(new HoleSpec { Kind = HoleKind.Tapped, ThreadSize = r.Size, HoleDiameter = r.MinorDia, Depth = 12, Bottom = HoleBottom.Flat }, 0);
                Assert(mt.ThicknessMm > mt.DepthMm, r.Size + " 盲孔示意板厚必须厚于孔深 " + mt.ThicknessMm);
                Assert(Math.Abs(mt.ThicknessMm * 2 - Math.Round(mt.ThicknessMm * 2)) < 1e-9, r.Size + " 示意板厚取到 0.5");
            }

            // ⑫ 不成立的规格：预览必须拒画，不能画一个看着挺像、其实打不出来的形状
            var badCb = new HoleSpec { Kind = HoleKind.Counterbore, HoleDiameter = 11, CounterboreDiameter = 6.6, CounterboreDepth = 6.5, Depth = 0 };
            Assert(HoleShapeBuilder.Build(badCb, 0).Problem.Length > 0,
                   "沉孔直径小于孔径时要报错，实得 [" + HoleShapeBuilder.Build(badCb, 0).Problem + "]");
            var badCs = new HoleSpec { Kind = HoleKind.Countersink, HoleDiameter = 12, CountersinkDiameter = 5, CountersinkAngle = 90, Depth = 0 };
            Assert(HoleShapeBuilder.Build(badCs, 0).Problem.Length > 0, "锥孔直径小于孔径时要报错");
            Assert(HoleShapeBuilder.Build(cbSpec, 12).Problem.Length == 0, "正常的沉孔不报错");
            Assert(HoleShapeBuilder.Build(csSpec, 10).Problem.Length == 0, "正常的锥形沉孔不报错");
            Assert(HoleShapeBuilder.Build(th, 10).Problem.Length == 0, "通孔不报错");

            // ⑬ 轮廓必须单调向下（孔是往下走的），画图才不会自交
            foreach (var spec in new[]{ th, blind, flat, cbSpec, csSpec, chSpec, tapSpec }) {
                var s = HoleShapeBuilder.Build(spec, 0);
                for (int i = 1; i < s.Wall.Count; i++)
                    Assert(s.Wall[i].Z >= s.Wall[i - 1].Z - 1e-9,
                           HoleSpec.KindName(spec.Kind) + " 轮廓深度单调不回头 @" + i);
                Assert(Math.Abs(s.Wall[0].R) < 1e-9 && Math.Abs(s.Wall[s.Wall.Count - 1].R) < 1e-9,
                       HoleSpec.KindName(spec.Kind) + " 轮廓首尾都落在轴线上");
            }
        }

        static string Describe(IList<HoleIssue> issues){
            return issues.Count + " 条 [" + string.Join(" | ", issues.Select(i => i.KindName + "：" + i.Message).ToArray()) + "]";
        }

        // ---- 孔偏了的分组口径 + 漏打孔射线的坐标换算 ----
        static void MisalignmentAndRay(){
            // M6：A 板螺纹底孔 Φ5，B 板过孔 Φ6.6
            var tap = new HoleRecord { Center = new V3(0,0,0), Axis = new V3(0,0,1), DiameterMm = 5.0, PartName = "A" };
            Func<double, HoleRecord> clearanceAt = dx => new HoleRecord { Center = new V3(dx,0,0.02), Axis = new V3(0,0,-1), DiameterMm = 6.6, PartName = "B" };

            var ok = HoleCheck.Run(new List<HoleRecord>{ tap, clearanceAt(0.0001) }, null, true);
            Assert(ok.Count == 0, "偏 0.1mm（容差内）且配做正确 -> 无问题，实得 " + Describe(ok));
            var off1 = HoleCheck.Run(new List<HoleRecord>{ tap, clearanceAt(0.001) }, null);
            Assert(off1.Count == 1 && off1[0].Kind == HoleIssueKind.Misaligned && off1[0].Message.Contains("1mm"),
                   "偏 1mm -> 孔偏了（反平行轴线也照样分到一组），实得 " + Describe(off1));
            // 偏移超过较小孔的半径（Φ5 -> 2.5mm）：一个孔的轴线已经不在另一个孔里，不再算同一个紧固点
            Assert(HoleCheck.GroupByAxis(new List<HoleRecord>{ tap, clearanceAt(0.003) }).Count == 2, "偏 3mm（> 较小孔半径）不再归为一组");
            // 同一零件上间距正常的两个孔不能被宽容差并成一组
            var p1 = new HoleRecord { Center = new V3(0,0,0), Axis = new V3(0,0,1), DiameterMm = 6.6, PartName = "B" };
            var p2 = new HoleRecord { Center = new V3(0.01,0,0), Axis = new V3(0,0,1), DiameterMm = 6.6, PartName = "B" };
            Assert(HoleCheck.GroupByAxis(new List<HoleRecord>{ p1, p2 }).Count == 2, "同一零件间距 10mm 的两个 Φ6.6 孔仍是两组");
            // 沉孔（同一零件 Φ6.6 + Φ11 同轴）配对方螺纹底孔偏 1mm：偏心只按不同零件之间算
            var cbHead = new HoleRecord { Center = new V3(0.001,0,0.03), Axis = new V3(0,0,1), DiameterMm = 11.0, PartName = "B" };
            var cbRun = HoleCheck.Run(new List<HoleRecord>{ tap, clearanceAt(0.001), cbHead }, null);
            Assert(cbRun.Count == 1 && cbRun[0].Kind == HoleIssueKind.Misaligned, "沉孔 + 螺纹孔偏 1mm -> 一条孔偏了，实得 " + Describe(cbRun));

            // 射线：零件实体在自己的坐标系里（这里用一个长方体代替），实例在装配里转了 90° 并平移。
            // 局部长方体 x∈[0,100] y∈[0,50] z∈[0,10] mm；绕 Z 转 90°、原点移到 (500,200,30) mm，
            // 装配里占 x∈[450,500] y∈[200,300] z∈[30,40] mm。
            var rotated = Transform.Frame(new V3(0.5,0.2,0.03), new V3(0,1,0), new V3(-1,0,0), new V3(0,0,1));
            Func<V3, V3, bool> box = (o, d) => RayHitsBox(o, d, new V3(0,0,0), new V3(0.1,0.05,0.01));
            var b = new PartRayTarget("B", rotated, box);
            Assert(b.Hits(new V3(0.48,0.25,0), new V3(0,0,1)), "装配坐标的轴线穿过转了 90° 的零件 -> 命中");
            Assert(b.Hits(new V3(0.48,0.25,0.1), new V3(0,0,1)), "孔在零件另一侧（射线反向）也命中");
            Assert(!box(new V3(0.48,0.25,0), new V3(0,0,1)) && !box(new V3(0.48,0.25,0), new V3(0,0,-1)),
                   "对照：不换算直接拿装配坐标打，会漏掉这次命中（原来的 bug）");
            Assert(!b.Hits(new V3(0.05,0.02,0), new V3(0,0,1)), "落在零件局部范围、但装配里并不穿过零件 -> 不命中");
            Assert(box(new V3(0.05,0.02,0), new V3(0,0,1)), "对照：不换算时这一条会被误判成命中（原来的 bug）");

            // 轴线方向也要换算：零件绕 X 转 90°（局部 z -> 装配 -y），孔轴沿装配 Y
            var tilted = Transform.Frame(new V3(0,0,0.1), new V3(1,0,0), new V3(0,0,1), new V3(0,-1,0));
            var t = new PartRayTarget("T", tilted, box);   // 装配里占 x∈[0,100] y∈[-10,0] z∈[100,150] mm
            Assert(t.Hits(new V3(0.05,0.5,0.12), new V3(0,1,0)), "零件转了方向：轴线方向换算后命中");
            Assert(!box(new V3(0.05,0.5,0.12), new V3(0,1,0)) && !box(new V3(0.05,0.5,0.12), new V3(0,-1,0)), "对照：方向不换算会漏判");
            Assert(!new PartRayTarget("N", rotated, null).Hits(new V3(0.48,0.25,0), new V3(0,0,1)), "没有实体（cast 为 null）-> 不命中");

            // 表 + 主检查：同一个零件文件在装配里用了两次，只有真正挡在轴线上的那个实例算命中
            var index = new PartRayIndex();
            Assert(index.Add(b), "登记实例 B");
            Assert(!index.Add(new PartRayTarget("B", Transform.Identity, box)), "同名实例只认第一个");
            Assert(index.Add(new PartRayTarget("B:2", Transform.Frame(new V3(2,0,0), new V3(1,0,0), new V3(0,1,0), new V3(0,0,1)), box)), "登记同一零件的第二个实例");
            var lone = new HoleRecord { Center = new V3(0.48,0.25,0), Axis = new V3(0,0,1), DiameterMm = 6.6, PartName = "A" };
            Assert(index.Hits(lone, "B") && !index.Hits(lone, "B:2") && !index.Hits(lone, "不存在"), "按实例名查，每个实例用自己的变换");
            var missing = HoleCheck.Run(new List<HoleRecord>{ lone }, null, false, new List<string>{ "A", "B", "B:2" }, index);
            Assert(missing.Count == 1 && missing[0].Kind == HoleIssueKind.MissingHole, "PartRayIndex 接入主检查 -> 漏打孔，实得 " + Describe(missing));
            var far = new HoleRecord { Center = new V3(0.05,0.02,0), Axis = new V3(0,0,1), DiameterMm = 6.6, PartName = "A" };
            Assert(HoleCheck.Run(new List<HoleRecord>{ far }, null, false, new List<string>{ "A", "B", "B:2" }, index).Count == 0, "轴线不穿过任何实例 -> 不报漏孔");
        }

        // 单向射线与轴对齐长方体求交（slab 法），t ≥ 0 才算命中。
        static bool RayHitsBox(V3 o, V3 d, V3 lo, V3 hi){
            double tmin = 0, tmax = double.MaxValue;
            double[] os = { o.X, o.Y, o.Z }, ds = { d.X, d.Y, d.Z }, los = { lo.X, lo.Y, lo.Z }, his = { hi.X, hi.Y, hi.Z };
            for (int i = 0; i < 3; i++) {
                if (Math.Abs(ds[i]) < 1e-12) { if (os[i] < los[i] || os[i] > his[i]) return false; continue; }
                double t1 = (los[i] - os[i]) / ds[i], t2 = (his[i] - os[i]) / ds[i];
                if (t1 > t2) { var s = t1; t1 = t2; t2 = s; }
                tmin = Math.Max(tmin, t1); tmax = Math.Min(tmax, t2);
                if (tmin > tmax) return false;
            }
            return true;
        }
    }
}
