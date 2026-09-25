using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using A=SolidEdgeAssembly;
using F=SolidEdgeFramework;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;
using S=SolidEdgeFrameworkSupport;

namespace TianGongCadSuite {
    public static class AutoHoleTests {
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
            // 横向错开 0.5mm：超出 0.2mm 同轴容差，所以不在同一组
            var nearHole = new HoleRecord { Center = new V3(0.0005,0,0.02), Axis = new V3(0,0,1), DiameterMm = 11.0, PartName = "B" };
            var nearHoleRun = HoleCheck.Run(new List<HoleRecord>{ h1, nearHole }, null, true);
            Assert(nearHoleRun.Count == 2, "横向错开 0.5mm 超出同轴容差，实得 " + nearHoleRun.Count);
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
            Console.WriteLine("AUTO-HOLE PURE ASSERTIONS " + checks);
        }

        // ---------- 原生：装配里跨零件照孔打孔 ----------
        const string CadHome = @"C:\Program Files\NDS\TianGong 2025";
        static string Template { get { return Path.Combine(CadHome, "Template", "ISO Metric", "iso metric part.par"); } }
        static object M = Type.Missing;

        // 按几何找标准 XY 基准面。不要按 RefPlanes.Item(n) 的序号猜——序号不是 XY/YZ/XZ 的顺序。
        static P.RefPlane FindXY(P.PartDocument part){
            foreach (P.RefPlane c in part.RefPlanes){
                Array n = new double[3], p = new double[3], u = new double[3];
                c.GetNormal(ref n); c.GetRootPoint(ref p); c.GetReferenceDirection(ref u);
                if (Math.Abs(D(n,2)-1)<1e-8 && Math.Abs(D(p,0))<1e-8 && Math.Abs(D(p,1))<1e-8 && Math.Abs(D(u,0)-1)<1e-8) return c;
            }
            return null;
        }

        static P.PartDocument BuildPlate(F.Application app, string name, double w, double h, double t, out P.Model model){
            var part = (P.PartDocument)app.Documents.Add("SolidEdge.PartDocument", Template);
            part.ModelingMode = P.ModelingModeConstants.seModelingModeOrdered;
            P.RefPlane plane = FindXY(part);
            if (plane == null) throw new InvalidOperationException("模板缺少标准 XY 基准面。");
            var profile = part.ProfileSets.Add().Profiles.Add(plane);
            var L = new S.Line2d[4];
            L[0]=profile.Lines2d.AddBy2Points(0,0,w,0); L[1]=profile.Lines2d.AddBy2Points(w,0,w,h);
            L[2]=profile.Lines2d.AddBy2Points(w,h,0,h); L[3]=profile.Lines2d.AddBy2Points(0,h,0,0);
            var rel = (S.Relations2d)profile.Relations2d;
            for (int i=0;i<4;i++){ rel.AddKeypoint(L[i],(int)SolidEdgeConstants.KeypointIndexConstants.igLineEnd,L[(i+1)%4],(int)SolidEdgeConstants.KeypointIndexConstants.igLineStart); if(i%2==0) rel.AddHorizontal(L[i]); else rel.AddVertical(L[i]); }
            rel.AddKeypointFix(L[0],(int)SolidEdgeConstants.KeypointIndexConstants.igLineStart);
            var dims = (S.Dimensions)profile.Dimensions; dims.Constraint=true; dims.AddLength(L[0]); dims.AddLength(L[1]);
            if (profile.End(P.ProfileValidationType.igProfileClosed) != 0) throw new InvalidOperationException("矩形轮廓未闭合。");
            Array arr = new object[]{profile};
            model = part.Models.AddFiniteExtrudedProtrusion(1, ref arr, P.FeaturePropertyConstants.igSymmetric, t);
            return part;
        }
        static double D(Array a,int i){ return Convert.ToDouble(a.GetValue(i)); }
        static double Vol(P.Model m){ return ((G.Body)m.Body).Volume; }

        // 找 Z=z 的水平面。必须同时按"法向沿 Z"筛选：
        //   - 平面的 root point 可以落在平面上的任意位置，只按 z 匹配会误中侧面
        //     （实测 x=0 的侧面 root point 的 z 正好是 +0.005，面积只有 1000mm²）
        //   - 拉伸件的下表面底层法向仍可能是 +Z（靠 IsParamReversed 表达朝向），
        //     所以法向只看绝对值，不看符号
        static G.Face PlanarFaceAtZ(P.Model m, double z){
            foreach (G.Face f in (G.Faces)((G.Body)m.Body).get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll)){
                var pl = f.Geometry as G.Plane; if (pl == null) continue;
                Array p=new double[3], n=new double[3]; pl.GetPlaneData(ref p, ref n);
                if (Math.Abs(Math.Abs(D(n,2)) - 1) > 1e-6) continue;
                if (Math.Abs(D(p,2)-z) < 1e-6) return f;
            }
            return null;
        }
        static G.Edge CircularEdge(P.Model m){
            foreach (G.Edge e in (G.Edges)((G.Body)m.Body).get_Edges(G.FeatureTopologyQueryTypeConstants.igQueryAll))
                if (e.Geometry is G.Circle) return e;
            return null;
        }

        // 用底层 API 在 A 板上打一个 Φ8.5 的底孔（模拟"别人已经打好的孔"）
        static void TapHole(P.PartDocument part, P.Model model, double x, double y, double dia){
            var xy = FindXY(part);
            if (xy == null) throw new InvalidOperationException("找不到标准 XY 基准面，无法定位底孔。");
            var rp = part.RefPlanes.AddParallelByDistance(xy, 0.005, P.ReferenceElementConstants.igNormalSide, M, M, M, M);
            var prof = part.ProfileSets.Add().Profiles.Add(rp);
            double x2, y2; prof.Convert3DCoordinate(x, y, 0.005, out x2, out y2);
            prof.Holes2d.Add(x2, y2);
            if (prof.End(P.ProfileValidationType.igProfileClosed) != 0) throw new InvalidOperationException("孔轮廓未闭合。");
            var hd = part.HoleDataCollection.Add(P.FeaturePropertyConstants.igRegularHole, dia, M,M,M,M,M,M,M,M,M,M,M,M,M,M,M,M,M,M,M);
            P.Hole h = null;
            foreach (var side in new[]{P.FeaturePropertyConstants.igLeft, P.FeaturePropertyConstants.igRight}){
                h = model.Holes.AddThroughAll(prof, side, hd);
                if (h == null) continue;
                object dsc = null; var st = h.GetStatusEx(out dsc);
                if ((int)st == (int)P.FeatureStatusConstants.igFeatureOK) return;
                try { h.Delete(); } catch {}
            }
            throw new InvalidOperationException("底孔构造失败。");
        }

        public static void Native(F.Application app, string outputDir){
            checks = 0;
            bool createdInstance = false;
            if (app == null) {
                try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); Console.WriteLine("AUTOHOLE attached to running CAD"); }
                catch { app = (F.Application)Activator.CreateInstance(Type.GetTypeFromProgID("SolidEdge.Application")); createdInstance = true; Console.WriteLine("AUTOHOLE started a new CAD instance"); }
                app.Visible = true; app.ScreenUpdating = true;
            }
            string dir = Path.GetFullPath(Path.Combine(outputDir, "autohole-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")));
            Directory.CreateDirectory(dir);
            A.AssemblyDocument asm = null; object original = null;
            try { original = app.ActiveDocument; } catch {}
            P.PartDocument a = null, b = null;
            try {
                asm = (A.AssemblyDocument)app.Documents.Add("SolidEdge.AssemblyDocument");
                asm.SaveAs(Path.Combine(dir, "HoleFixture.asm"));

                // A 板：带一个 Φ8.5 的 M10 螺纹底孔
                P.Model ma; a = BuildPlate(app, "A", 0.1, 0.1, 0.01, out ma);
                TapHole(a, ma, 0.05, 0.05, 0.0085);
                Assert(a.Models.Item(1).Holes.Count == 1, "A 板有一个底孔");
                double va0 = Vol(ma);
                a.SaveAs(Path.Combine(dir, "PlateA.par")); a.Close(false); a = null;

                // B 板：实心，20mm 厚（M10 沉孔深 11mm，10mm 板会被沉孔打穿，测不出两段）
                P.Model mb; b = BuildPlate(app, "B", 0.1, 0.1, 0.02, out mb);
                b.SaveAs(Path.Combine(dir, "PlateB.par")); b.Close(false); b = null;

                // 插入装配：A 在下，B 在上方 20mm
                asm.Activate();
                var occA = asm.Occurrences.AddByFilename(Path.Combine(dir, "PlateA.par"));
                Array mA = Transform.Identity.M; occA.PutMatrix(ref mA, true);
                var occB = asm.Occurrences.AddByFilename(Path.Combine(dir, "PlateB.par"));
                Array mB = Transform.Frame(new V3(0,0,0.02), new V3(1,0,0), new V3(0,1,0), new V3(0,0,1)).M;
                occB.PutMatrix(ref mB, true);
                Assert(asm.Occurrences.Count == 2, "装配内两个实例");

                // 取 A 的孔圆边做参考孔，取 B 的下表面做打孔面
                var docA = (P.PartDocument)occA.OccurrenceDocument;
                var docB = (P.PartDocument)occB.OccurrenceDocument;
                var edgeA = CircularEdge((P.Model)docA.Models.Item(1));
                Assert(edgeA != null, "读到 A 板的圆孔边线");
                var faceB = PlanarFaceAtZ((P.Model)docB.Models.Item(1), -0.010);   // 20mm 板的下表面
                Assert(faceB != null, "读到 B 板的下表面");

                var refA = asm.CreateReference(occA, edgeA);
                var refB = asm.CreateReference(occB, faceB);

                // 诊断：直接看底层圆几何返回了什么
                var circ = (G.Circle)edgeA.Geometry;
                Array cc = new double[3], ax = new double[3]; double rr = 0;
                circ.GetCircleData(ref cc, ref ax, out rr);
                Console.WriteLine("RAW circle centerLen=" + cc.Length + " [" + D(cc,0).ToString("0.#####") + "," + D(cc,1).ToString("0.#####") + "," + D(cc,2).ToString("0.#####") + "] axisLen=" + ax.Length + " [" + D(ax,0).ToString("0.#####") + "," + D(ax,1).ToString("0.#####") + "," + D(ax,2).ToString("0.#####") + "] r=" + rr.ToString("0.#####"));
                Array ax2 = new double[3]; circ.GetAxisVector(ref ax2);
                Console.WriteLine("RAW GetAxisVector [" + D(ax2,0).ToString("0.#####") + "," + D(ax2,1).ToString("0.#####") + "," + D(ax2,2).ToString("0.#####") + "]");

                var hole = AutoHoleReader.ReadReference(refA);
                Console.WriteLine("READ hole center=" + hole.Center + " axis=" + hole.Axis + " dia=" + hole.DiameterMm.ToString("0.###"));
                Assert(Math.Abs(hole.DiameterMm - 8.5) < 0.02, "参考孔直径读为 8.5mm，实读 " + hole.DiameterMm.ToString("0.###"));
                Assert(Math.Abs(Math.Abs(hole.Axis.Z) - 1) < 1e-6, "参考孔轴向为 Z，实读 " + hole.Axis);

                var target = AutoHoleReader.ReadTarget(refB);
                Assert(target.Part != null, "从面片反查到所属零件文档");
                Assert(target.PartName.Length > 0, "零件名非空：" + target.PartName);
                Assert(Math.Abs(Math.Abs(target.Plane.Normal.Z) - 1) < 1e-6, "打孔面法向为 Z");

                var match = HoleMatcher.Match(hole.DiameterMm);
                Assert(match.Row.Size == "M10" && match.Target.Kind == HoleKind.Counterbore, "Φ8.5 自动判为 M10 沉孔");

                var centre = AutoHoleReader.Intersect(hole, target);
                Assert(Math.Abs(centre.X - 0.05) < 1e-6 && Math.Abs(centre.Y - 0.05) < 1e-6, "孔心与参考孔同轴 " + centre);
                Assert(Math.Abs(centre.Z - 0.010) < 1e-6, "孔心落在 B 板下表面 z=10mm，实读 " + centre.Z.ToString("0.#####"));

                double vb0 = Vol((P.Model)docB.Models.Item(1));
                var res = AutoHoleWriter.Drill(target, match.Target, new V3[]{ centre });
                Assert(res.Created == 1, "在 B 板上打出 1 个孔（失败 " + res.Failures.Count + " 个：" + string.Join("；", res.Failures.ToArray()) + "）");
                Assert(res.Failures.Count == 0, "无失败项");

                double vb1 = Vol((P.Model)docB.Models.Item(1));
                double removed = vb0 - vb1;
                // 两段：沉孔Φ18 深11mm + 通孔Φ11 走完剩余 9mm
                double expect = Math.PI*0.009*0.009*0.011 + Math.PI*0.0055*0.0055*0.009;
                Assert(Math.Abs(removed - expect) / expect < 0.03,
                    "B 板切除体积符合「沉孔Φ18深11 + 通孔Φ11」两段（实 " + (removed*1e9).ToString("0.#") + " mm³ / 期 " + (expect*1e9).ToString("0.#") + " mm³）");
                Assert(((P.Model)docB.Models.Item(1)).Holes.Count == 1, "B 板新增 1 个孔特征");

                // A 板必须原封不动
                var docA2 = (P.PartDocument)occA.OccurrenceDocument;
                Assert(Math.Abs(Vol((P.Model)docA2.Models.Item(1)) - va0) < 1e-12, "A 板（参照件）未被改动");

                asm.Save();
                try { ((dynamic)app.ActiveWindow).View.Fit(); ((dynamic)app.ActiveWindow).View.SaveAsImage(Path.Combine(dir, "autohole.png"), 1200, 900); } catch {}
                Console.WriteLine("AUTO-HOLE NATIVE ASSERTIONS " + checks);
                Console.WriteLine("AUTO-HOLE ARTIFACTS " + dir);
            } finally {
                if (a != null) try { a.Close(false); } catch {}
                if (b != null) try { b.Close(false); } catch {}
                if (asm != null) try { asm.Close(false); } catch {}
                try { ((dynamic)original).Activate(); } catch {}
                if (createdInstance) { try { app.Quit(); } catch {} }
            }
        }

        // ---------- 原生：螺纹孔不能带锥面（2026-09-25 用户反馈的回归测试） ----------
        // 用户报的现象：打螺纹孔，出来的孔口有一个锥面，而且没设过任何锥度。
        // 根因：AddEx(igTappedHole,…) 会被 CAD 换成 igCounterdrillHole 并把保存下来的
        //       沉头尺寸（Φ13.71 / 90° / 深 50.8mm）灌进去。修法见 AutoHoleWriter.BuildHoleData。
        // 这个用例把"贯通螺纹孔 = 纯圆柱、0 个锥面"钉死成回归断言。
        public static void TappedNative(F.Application app, string outputDir){
            checks = 0;
            bool createdInstance = false;
            if (app == null) {
                try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); Console.WriteLine("AUTOHOLE attached to running CAD"); }
                catch { app = (F.Application)Activator.CreateInstance(Type.GetTypeFromProgID("SolidEdge.Application")); createdInstance = true; Console.WriteLine("AUTOHOLE started a new CAD instance"); }
                app.Visible = true; app.ScreenUpdating = true;
            }
            string dir = Path.GetFullPath(Path.Combine(outputDir, "autohole-tapped-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")));
            Directory.CreateDirectory(dir);
            A.AssemblyDocument asm = null; object original = null;
            try { original = app.ActiveDocument; } catch {}
            P.PartDocument a = null, b = null;
            try {
                asm = (A.AssemblyDocument)app.Documents.Add("SolidEdge.AssemblyDocument");
                asm.SaveAs(Path.Combine(dir, "TappedFixture.asm"));

                // A 板：一个 Φ6.6 的 M6 过孔（= 参考孔）
                P.Model ma; a = BuildPlate(app, "TA", 0.1, 0.1, 0.01, out ma);
                TapHole(a, ma, 0.05, 0.05, 0.0066);
                a.SaveAs(Path.Combine(dir, "TappedA.par")); a.Close(false); a = null;

                // B 板：20mm 实心
                P.Model mb; b = BuildPlate(app, "TB", 0.1, 0.1, 0.02, out mb);
                b.SaveAs(Path.Combine(dir, "TappedB.par")); b.Close(false); b = null;

                asm.Activate();
                var occA = asm.Occurrences.AddByFilename(Path.Combine(dir, "TappedA.par"));
                Array mA = Transform.Identity.M; occA.PutMatrix(ref mA, true);
                var occB = asm.Occurrences.AddByFilename(Path.Combine(dir, "TappedB.par"));
                Array mB = Transform.Frame(new V3(0,0,0.02), new V3(1,0,0), new V3(0,1,0), new V3(0,0,1)).M;
                occB.PutMatrix(ref mB, true);

                var docA = (P.PartDocument)occA.OccurrenceDocument;
                var docB = (P.PartDocument)occB.OccurrenceDocument;
                var mdlB = (P.Model)docB.Models.Item(1);
                var edgeA = CircularEdge((P.Model)docA.Models.Item(1));
                Assert(edgeA != null, "读到 A 板的圆孔边线");
                var hole = AutoHoleReader.ReadReference(asm.CreateReference(occA, edgeA));
                Assert(Math.Abs(hole.DiameterMm - 6.6) < 0.05, "参考孔 Φ6.6，实读 " + hole.DiameterMm.ToString("0.###"));

                var match = HoleMatcher.Match(hole.DiameterMm);
                Assert(match.Row.Size == "M6" && !match.ReferenceIsTapped, "Φ6.6 判为 M6 过孔");
                Assert(match.Target.Kind == HoleKind.Tapped, "过孔 -> 目标件配螺纹孔");
                Assert(Math.Abs(match.Target.HoleDiameter - 4.917) < 1e-9, "M6 螺纹孔按内小径 Φ4.917 建模");
                Assert(match.Target.Bottom == HoleBottom.Flat && !match.Target.Chamfer, "自动规格默认平底、不倒角");

                var faceB = PlanarFaceAtZ(mdlB, -0.010);
                var target = AutoHoleReader.ReadTarget(asm.CreateReference(occB, faceB));
                var centre = AutoHoleReader.Intersect(hole, target);

                // ① 贯通螺纹孔：必须是纯圆柱，锥面数 = 0
                double v0 = Vol(mdlB);
                var res = AutoHoleWriter.Drill(target, match.Target, new V3[]{ centre });
                Assert(res.Created == 1, "贯通螺纹孔打出 1 个（失败 " + res.Failures.Count + "：" + string.Join("；", res.Failures.ToArray()) + "）");
                Assert(res.Audit.Length == 0, "自检没发现孔型被改写：" + res.Audit);
                double removed = v0 - Vol(mdlB);
                double expect = Math.PI * 0.0024585 * 0.0024585 * 0.02;
                Assert(Math.Abs(removed - expect) / expect < 0.01,
                    "贯通螺纹孔切除体积 = Φ4.917 圆柱（实 " + (removed*1e9).ToString("0.#") + " / 期 " + (expect*1e9).ToString("0.#") + " mm³）");
                Assert(Cones(mdlB) == 0, "贯通螺纹孔实体上没有锥面 —— 就是用户报的那个 bug");
                Assert(res.Method.Contains("螺纹孔") && res.Method.Contains("M6"), "结果里写明是 M6 螺纹孔：" + res.Method);

                var hd = (P.HoleData)mdlB.Holes.Item(1).HoleData;
                Assert(hd.HoleType == P.FeaturePropertyConstants.igRegularHole, "孔型是普通孔（螺纹走装饰螺纹），实为 " + hd.HoleType);
                Assert(hd.ThreadDescription != null && hd.ThreadDescription.Contains("M6"), "回读到螺纹规格 M6，实读 " + hd.ThreadDescription);

                // ② 盲孔 + 平底：锥面数仍然 0
                var faceB2 = PlanarFaceAtZ(mdlB, -0.010);
                var t2 = AutoHoleReader.ReadTarget(asm.CreateReference(occB, faceB2));
                var spec2 = match.Target.Clone(); spec2.Depth = 10; spec2.Bottom = HoleBottom.Flat;
                double v1 = Vol(mdlB);
                var res2 = AutoHoleWriter.Drill(t2, spec2, new V3[]{ new V3(0.03, 0.05, centre.Z) });
                Assert(res2.Created == 1, "平底盲孔打出 1 个（失败 " + string.Join("；", res2.Failures.ToArray()) + "）");
                double rem2 = v1 - Vol(mdlB);
                double exp2 = Math.PI * 0.0024585 * 0.0024585 * 0.01;
                Assert(Math.Abs(rem2 - exp2) / exp2 < 0.03, "平底盲孔深 10mm 体积吻合（实 " + (rem2*1e9).ToString("0.#") + " / 期 " + (exp2*1e9).ToString("0.#") + " mm³）");
                Assert(Cones(mdlB) == 0, "平底盲孔没有锥面");

                // ②b 用同一个 TargetFace 接着打第二批 —— 此时面对象已经失效。
                //     修复前这里会 E_FAIL（"无法在所选面上建立打孔基准面"），
                //     因为共面判断依赖失效的面片；现在改用装配坐标的纯平面数据来找已有基准面。
                var res2b = AutoHoleWriter.Drill(t2, spec2, new V3[]{ new V3(0.06, 0.07, centre.Z) });
                Assert(res2b.Created == 1, "同一个面接着打第二批孔（面对象已失效）也成功（失败：" + string.Join("；", res2b.Failures.ToArray()) + "）");

                // ③ 盲孔 + V 型底：应当有且只有 1 个锥面（钻尖），这是"要的时候必须有"
                var faceB3 = PlanarFaceAtZ(mdlB, -0.010);
                var t3 = AutoHoleReader.ReadTarget(asm.CreateReference(occB, faceB3));
                var spec3 = match.Target.Clone(); spec3.Depth = 10; spec3.Bottom = HoleBottom.VBottom;
                double v2 = Vol(mdlB);
                var res3 = AutoHoleWriter.Drill(t3, spec3, new V3[]{ new V3(0.07, 0.05, centre.Z) });
                Assert(res3.Created == 1, "V 底盲孔打出 1 个（失败 " + string.Join("；", res3.Failures.ToArray()) + "）");
                Assert(Cones(mdlB) == 1, "V 型盲孔有且只有 1 个锥面（钻尖），实得 " + Cones(mdlB));
                double rem3 = v2 - Vol(mdlB);
                double coneH = 0.0024585 / Math.Tan(59.0 * Math.PI / 180.0);
                double exp3 = Math.PI * 0.0024585 * 0.0024585 * (0.010 - coneH) + Math.PI * 0.0024585 * 0.0024585 * coneH / 3.0;
                Assert(Math.Abs(rem3 - exp3) / exp3 < 0.03, "V 底盲孔体积 = 圆柱段 + 钻尖（实 " + (rem3*1e9).ToString("0.#") + " / 期 " + (exp3*1e9).ToString("0.#") + " mm³）");

                // ③b 缓存失效回归（用户实机报的 RPC_E_DISCONNECTED）
                //     对同一张面打过孔以后，插件会把它建的基准面缓存起来（为了支持"同一张面打第二批"）。
                //     但零件被重新加载后，缓存里那份基准面就成了死对象；拿它建轮廓会报
                //     0x80010108 RPC_E_DISCONNECTED，而且重试同一个死对象永远失败。
                //     修法：缓存命中时先验证对象还活着。这里通过"关掉文档再重新打开"来制造死对象。
                {
                    asm.Save();
                    var faceR = PlanarFaceAtZ(mdlB, -0.010);
                    var tR = AutoHoleReader.ReadTarget(asm.CreateReference(occB, faceR));
                    var specR = match.Target.Clone();
                    var r0 = AutoHoleWriter.Drill(tR, specR, new V3[]{ new V3(0.03, 0.03, centre.Z) });
                    Assert(r0.Created == 1, "先打一批，让插件把基准面缓存起来（失败 " + string.Join("；", r0.Failures.ToArray()) + "）");

                    string asmPath = asm.FullName;
                    asm.Close(false);
                    for (int i = 0; i < 12; i++) { try { app.DoIdle(); } catch { } System.Threading.Thread.Sleep(150); }
                    asm = (A.AssemblyDocument)app.Documents.Open(asmPath);
                    asm.Activate();
                    occB = null;
                    foreach (A.Occurrence oc in asm.Occurrences) { try { if (oc.Name.StartsWith("TappedB")) occB = oc; } catch { } }
                    Assert(occB != null, "重新打开装配后找到 B 板实例");
                    var docB2 = (P.PartDocument)occB.OccurrenceDocument;
                    var mdlB2 = (P.Model)docB2.Models.Item(1);
                    var faceR2 = PlanarFaceAtZ(mdlB2, -0.010);
                    var tR2 = AutoHoleReader.ReadTarget(asm.CreateReference(occB, faceR2));
                    var r1 = AutoHoleWriter.Drill(tR2, specR, new V3[]{ new V3(0.05, 0.03, centre.Z) });
                    Assert(r1.Created == 1, "零件重新加载（缓存的基准面已成死对象）后仍能打孔（失败 " + string.Join("；", r1.Failures.ToArray()) + "）");
                    Assert(CountFailures(r1, "断开") == 0, "失败信息里不应再出现【对象已断开】");
                    mdlB = mdlB2; docB = docB2;
                }

                // ④ 孔口倒角：锥面变成 2 个（钻尖 + 倒角），这是显式要求才有的
                var faceB4 = PlanarFaceAtZ(mdlB, -0.010);
                var t4 = AutoHoleReader.ReadTarget(asm.CreateReference(occB, faceB4));
                var spec4 = match.Target.Clone(); spec4.Chamfer = true; spec4.ChamferSetback = 0.5; spec4.ChamferAngle = 45;
                var res4 = AutoHoleWriter.Drill(t4, spec4, new V3[]{ new V3(0.05, 0.07, centre.Z) });
                Assert(res4.Created == 1, "带孔口倒角的贯通螺纹孔打出 1 个（失败 " + string.Join("；", res4.Failures.ToArray()) + "）");
                double rem4 = Vol(mdlB);
                Assert(Cones(mdlB) == 2, "勾了孔口倒角才会多出 1 个锥面，实得 " + Cones(mdlB));

                asm.Save();
                Console.WriteLine("AUTO-HOLE TAPPED ASSERTIONS " + checks);
                Console.WriteLine("AUTO-HOLE ARTIFACTS " + dir);
            } finally {
                if (a != null) try { a.Close(false); } catch {}
                if (b != null) try { b.Close(false); } catch {}
                if (asm != null) try { asm.Close(false); } catch {}
                try { ((dynamic)original).Activate(); } catch {}
                if (createdInstance) { try { app.Quit(); } catch {} }
            }
        }

        static int CountFailures(DrillResult r, string keyword){
            int n = 0;
            foreach (var f in r.Failures) if (f != null && f.Contains(keyword)) n++;
            return n;
        }

        // 实体上的锥面个数。板件本身只有平面和圆柱，锥面全部来自孔特征。
        static int Cones(P.Model model){
            int n = 0;
            var body = (G.Body)model.Body;
            foreach (G.Face f in (G.Faces)body.get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll)) {
                object geo = null;
                try { geo = f.Geometry; } catch { }
                if (geo is G.Cone) n++;
            }
            return n;
        }

        // ---------- 原生：本轮修复的回归（圆面 ContainsPoint / 沉孔+锥沉自检 / 配孔收集） ----------
        // 三件事必须真机验证，纯逻辑测不出来：
        // ① ContainsPoint 对"整圆边界的面"不再误判为面外（原来只采样直线端点，整圆的
        //    GetEndPoints 退化成一个点，包围盒比实际面小一圈）；
        // ② 全参数显式的沉孔/锥形沉孔配方真能建出正确几何（对角线级的回归：CAD 一旦
        //    把保存的旧参数灌进去，下面的直径/角度断言就会炸）；
        // ③ 通了孔的零件被 CollectAssemblyHoles 采出来是两条不同直径的圆边，
        //    HoleCheck.Run 不报配错孔。
        public static void FixesNative(F.Application app, string outputDir){
            checks = 0;
            bool createdInstance = false;
            if (app == null) {
                try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); Console.WriteLine("AUTOHOLE attached to running CAD"); }
                catch { app = (F.Application)Activator.CreateInstance(Type.GetTypeFromProgID("SolidEdge.Application")); createdInstance = true; Console.WriteLine("AUTOHOLE started a new CAD instance"); }
                app.Visible = true; app.ScreenUpdating = true;
            }
            string dir = Path.GetFullPath(Path.Combine(outputDir, "autohole-fixes-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")));
            Directory.CreateDirectory(dir);
            A.AssemblyDocument asm = null; object original = null;
            try { original = app.ActiveDocument; } catch { }
            P.PartDocument a = null, b = null;
            try {
                asm = (A.AssemblyDocument)app.Documents.Add("SolidEdge.AssemblyDocument");
                asm.SaveAs(Path.Combine(dir, "FixesFixture.asm"));

                // A 板：一个 Φ6.6 的通孔（= 参考孔，也是"圆边界"的测试对象）
                P.Model ma; a = BuildPlate(app, "FA", 0.1, 0.1, 0.01, out ma);
                TapHole(a, ma, 0.05, 0.05, 0.0066);
                a.SaveAs(Path.Combine(dir, "FixesA.par")); a.Close(false); a = null;

                // B 板：20mm，沉孔打不穿才有两段
                P.Model mb; b = BuildPlate(app, "FB", 0.1, 0.1, 0.02, out mb);
                b.SaveAs(Path.Combine(dir, "FixesB.par")); b.Close(false); b = null;

                asm.Activate();
                var occA = asm.Occurrences.AddByFilename(Path.Combine(dir, "FixesA.par"));
                Array mA = Transform.Identity.M; occA.PutMatrix(ref mA, true);
                var occB = asm.Occurrences.AddByFilename(Path.Combine(dir, "FixesB.par"));
                Array mB = Transform.Frame(new V3(0,0,0.02), new V3(1,0,0), new V3(0,1,0), new V3(0,0,1)).M;
                occB.PutMatrix(ref mB, true);

                var docA = (P.PartDocument)occA.OccurrenceDocument;
                var docB = (P.PartDocument)occB.OccurrenceDocument;
                var mdlA = (P.Model)docA.Models.Item(1);
                var mdlB = (P.Model)docB.Models.Item(1);
                var edgeA = CircularEdge(mdlA);
                Assert(edgeA != null, "读到 A 板的圆孔边线");
                var hole = AutoHoleReader.ReadReference(asm.CreateReference(occA, edgeA));
                Assert(Math.Abs(hole.DiameterMm - 6.6) < 0.05, "参考孔 Φ6.6，实读 " + hole.DiameterMm.ToString("0.###"));

                // ① 圆面（整圆边界）的 ContainsPoint：包围盒必须包住整圆
                var faceTopA = PlanarFaceAtZ(mdlA, 0.005);
                Assert(faceTopA != null, "读到 A 板顶面");
                var targetA = AutoHoleReader.ReadTarget(asm.CreateReference(occA, faceTopA));
                Assert(FaceFrameReader.ContainsPoint(targetA, new V3(0.095, 0.05, 0.005)),
                       "圆面上的孔心（靠近圆周、在包围盒圆弧鼓出的一侧）判定为在面内 —— 原来会误判成面外被静默跳过");
                Assert(FaceFrameReader.ContainsPoint(targetA, new V3(0.05, 0.05, 0.005)), "圆心在面内");
                Assert(!FaceFrameReader.ContainsPoint(targetA, new V3(0.20, 0.05, 0.005)), "面外的点仍然判为不在面内");
                // 缓存：同一个 TargetFace 第二次调用必须给同样的答案（缓存键没写错）
                Assert(FaceFrameReader.ContainsPoint(targetA, new V3(0.095, 0.05, 0.005)), "第二次调用（走缓存）结果一致");
                Assert(!FaceFrameReader.ContainsPoint(targetA, new V3(-0.05, 0.05, 0.005)), "左侧面外的点判为不在面内");

                // ②-a 圆柱沉孔：全参数显式配方 + 回读自检
                var faceB = PlanarFaceAtZ(mdlB, -0.010);
                var targetB = AutoHoleReader.ReadTarget(asm.CreateReference(occB, faceB));
                var cb = new HoleSpec { Kind = HoleKind.Counterbore, ThreadSize = "M6",
                                        HoleDiameter = 5.5, CounterboreDiameter = 11.0, CounterboreDepth = 6.5, Depth = 0 };
                double v0 = Vol(mdlB);
                var rc = AutoHoleWriter.Drill(targetB, cb, new V3[]{ new V3(0.03, 0.05, -0.010) });
                Assert(rc.Created == 1, "圆柱沉孔打出 1 个（失败 " + string.Join("；", rc.Failures.ToArray()) + "）");
                Assert(rc.Audit.Length == 0, "沉孔自检通过（孔型/沉孔直径/沉孔深度都对得上）：" + rc.Audit);
                // 沉孔Φ11 深6.5（Φ5.5 走完剩余 13.5mm）
                double expectCb = Math.PI*0.0055*0.0055*0.0065 + Math.PI*0.00275*0.00275*0.0135;
                double gotCb = v0 - Vol(mdlB);
                Assert(Math.Abs(gotCb - expectCb) / expectCb < 0.03,
                    "沉孔切除体积符合「沉孔Φ11深6.5 + 通孔Φ5.5」（实 " + (gotCb*1e9).ToString("0.#") + " / 期 " + (expectCb*1e9).ToString("0.#") + " mm³）");
                var hdCb = (P.HoleData)((P.Hole)mdlB.Holes.Item(1)).HoleData;
                Assert(Math.Abs(hdCb.CounterboreDiameter*1000 - 11.0) < 0.05, "回读沉孔直径 11，实读 " + (hdCb.CounterboreDiameter*1000).ToString("0.###"));
                Assert(Math.Abs(hdCb.CounterboreDepth*1000 - 6.5) < 0.05, "回读沉孔深度 6.5，实读 " + (hdCb.CounterboreDepth*1000).ToString("0.###"));

                // ②-b 锥形沉孔：锥孔直径/角度必须回读到我们给的值
                var faceB2 = PlanarFaceAtZ(mdlB, -0.010);
                var targetB2 = AutoHoleReader.ReadTarget(asm.CreateReference(occB, faceB2));
                var cs = new HoleSpec { Kind = HoleKind.Countersink, ThreadSize = "M6",
                                        HoleDiameter = 5.0, CountersinkDiameter = 11.0, CountersinkAngle = 90, Depth = 0 };
                double v1 = Vol(mdlB);
                var rcs = AutoHoleWriter.Drill(targetB2, cs, new V3[]{ new V3(0.07, 0.05, -0.010) });
                Assert(rcs.Created == 1, "锥形沉孔打出 1 个（失败 " + string.Join("；", rcs.Failures.ToArray()) + "）");
                Assert(rcs.Audit.Length == 0, "锥形沉孔自检通过（锥孔直径/锥角都对得上）：" + rcs.Audit);
                // 锥座是从 Φ11 收口到 Φ5 的**圆台**（水平切法）：
                //   圆台高 h = (0.0055-0.0025)/tan(45°) = 0.003
                //   体积 = Φ5 通孔走完剩下的深度 + 圆台
                //   即 π·r²·(t-h) + π·h/3·(R² + R·r + r²)
                double csH = (0.0055 - 0.0025) / Math.Tan(45.0 * Math.PI / 180.0);
                double expectCs = Math.PI*0.0025*0.0025*(0.02 - csH)
                                + Math.PI*csH/3.0*(0.0055*0.0055 + 0.0055*0.0025 + 0.0025*0.0025);
                double gotCs = v1 - Vol(mdlB);
                Assert(Math.Abs(gotCs - expectCs) / expectCs < 0.03,
                    "锥沉切除体积符合「Φ5 通孔 + Φ11/90° 锥座」（实 " + (gotCs*1e9).ToString("0.#") + " / 期 " + (expectCs*1e9).ToString("0.#") + " mm³）");
                var hdCs = (P.HoleData)((P.Hole)mdlB.Holes.Item(2)).HoleData;
                Assert(Math.Abs(hdCs.CountersinkDiameter*1000 - 11.0) < 0.05, "回读锥孔直径 11，实读 " + (hdCs.CountersinkDiameter*1000).ToString("0.###"));
                Assert(Math.Abs(hdCs.CountersinkAngle - 90) < 0.5, "回读锥角 90°，实读 " + hdCs.CountersinkAngle.ToString("0.###"));
                Assert(hdCs.HoleType == P.FeaturePropertyConstants.igCountersinkHole, "孔型是锥形沉孔，实为 " + hdCs.HoleType);

                // ③ 采集 + 配孔检查：Φ18/Φ11 同轴 + 对方 Φ8.5，不能报配错孔
                asm.Save();
                var warnings = new List<string>();
                var collected = AutoHoleWriter.CollectAssemblyHoles(asm, out warnings);
                // B 板上的沉孔/锥沉都在 (30,50)/(70,50) 那条线上，A 板的孔在 (50,50)，
                // 所以"同一轴线上被采到 ≥2 条不同直径的圆边"说的是 B 的沉孔（Φ11 过孔 + Φ18 沉孔外径
                // 这类"同轴双直径"）—— 正是配孔检查假阳性的来源。
                Console.WriteLine("INFO 采集到 " + collected.Count + " 条圆边记录：");
                foreach (var h in collected)
                    Console.WriteLine("INFO   " + h.PartName + " Φ" + h.DiameterMm.ToString("0.###")
                        + " @(" + (h.Center.X*1000).ToString("0.#") + "," + (h.Center.Y*1000).ToString("0.#") + "," + (h.Center.Z*1000).ToString("0.#") + ")"
                        + " axis=(" + h.Axis.X.ToString("0.##") + "," + h.Axis.Y.ToString("0.##") + "," + h.Axis.Z.ToString("0.##") + ")");
                var allGroups = HoleCheck.GroupByAxis(collected);
                Console.WriteLine("INFO 分成 " + allGroups.Count + " 组：");
                foreach (var grp in allGroups) {
                    var sb = new List<string>();
                    foreach (var h in grp) sb.Add(h.PartName + " Φ" + h.DiameterMm.ToString("0.###") + "@x" + (h.Center.X*1000).ToString("0.#"));
                    Console.WriteLine("INFO   组[" + string.Join(" | ", sb.ToArray()) + "]");
                }
                var bMulti = 0;
                foreach (var grp in allGroups) {
                    int nb = 0; var dias = new List<double>();
                    foreach (var h in grp) if (h.PartName.StartsWith("FixesB") || h.PartName.StartsWith("FB")) { nb++; dias.Add(Math.Round(h.DiameterMm, 1)); }
                    if (nb >= 2 && dias.Distinct().Count() >= 2) bMulti++;
                }
                // B 板那条沉孔轴线上应当采到 Φ11（沉孔）与 Φ5.5（过孔）两条边 —— 而且必须**同组**。
                Assert(bMulti >= 1, "沉孔零件的同一条轴线上采到两条不同直径的圆边并归成一组（实得 " + bMulti
                       + " 组 / 共 " + collected.Count + " 条记录）");
                var conflicts = HoleCheck.Run(collected, null, false, null);
                int wrongSpec = 0;
                foreach (var it in conflicts) if (it.Kind == HoleIssueKind.WrongSpec) wrongSpec++;
                Assert(wrongSpec == 0, "沉孔 + 配做孔不报「配错孔」，实得 " + wrongSpec
                       + (wrongSpec > 0 ? "：" + conflicts[0].Message : ""));

                Console.WriteLine("AUTO-HOLE FIXES ASSERTIONS " + checks);
                Console.WriteLine("AUTO-HOLE ARTIFACTS " + dir);
            } finally {
                if (a != null) try { a.Close(false); } catch { }
                if (b != null) try { b.Close(false); } catch { }
                if (asm != null) try { asm.Close(false); } catch { }
                try { ((dynamic)original).Activate(); } catch { }
                if (createdInstance) { try { app.Quit(); } catch { } }
            }
        }

        // ---------- 原生：把三个窗口真实渲染出来截图（界面验收用） ----------
        // 窗口移到屏幕外再 Show()，用户看不到；PrintWindow 照样能抓到真实渲染结果。
        public static void ShotsNative(F.Application app, string outputDir){
            checks = 0;
            bool createdInstance = false;
            if (app == null) {
                try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); Console.WriteLine("AUTOHOLE attached to running CAD"); }
                catch { app = (F.Application)Activator.CreateInstance(Type.GetTypeFromProgID("SolidEdge.Application")); createdInstance = true; Console.WriteLine("AUTOHOLE started a new CAD instance"); }
                app.Visible = true; app.ScreenUpdating = true;
            }
            string dir = Path.GetFullPath(Path.Combine(outputDir, "autohole-shots-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")));
            Directory.CreateDirectory(dir);
            A.AssemblyDocument asm = null; object original = null;
            try { original = app.ActiveDocument; } catch {}
            try {
                asm = (A.AssemblyDocument)app.Documents.Add("SolidEdge.AssemblyDocument");
                asm.Activate();
                // 让窗口里有内容可看：造一个假的"已选面 + 已选孔"状态不好造，
                // 所以这里只截"刚打开"的状态，重点看排版、配色和可用性。
                ShotForm(new AutoHoleForm(app, asm), Path.Combine(dir, "1-autohole.png"));
                ShotForm(new AutoHolePatternForm(app, asm), Path.Combine(dir, "2-pattern.png"));
                ShotForm(new AutoHoleCheckForm(app, asm), Path.Combine(dir, "3-check.png"));
                Console.WriteLine("AUTO-HOLE SHOT ASSERTIONS " + checks);
                Console.WriteLine("AUTO-HOLE ARTIFACTS " + dir);
            } finally {
                if (asm != null) try { asm.Close(false); } catch {}
                try { ((dynamic)original).Activate(); } catch {}
                if (createdInstance) { try { app.Quit(); } catch {} }
            }
        }

        static void ShotForm(System.Windows.Forms.Form f, string path){
            f.StartPosition = System.Windows.Forms.FormStartPosition.Manual;
            // 必须放在屏幕内：窗口在屏幕外时 DWM 不建立绘制表面，PrintWindow 只能抓到空窗框。
            // 每个窗口只显示约 1 秒就关掉，用户基本看不到。
            f.Location = new System.Drawing.Point(120, 60);
            f.Show();
            f.Refresh();
            Pump(700);
            System.Windows.Forms.Application.DoEvents();
            var r = f.Bounds;
            Assert(f.Visible && r.Width > 200 && r.Height > 200, Path.GetFileName(path) + " 窗口尺寸正常 " + r.Width + "x" + r.Height);
            System.Windows.Forms.Application.DoEvents();
            using (var bmp = new System.Drawing.Bitmap(r.Width, r.Height))
            using (var g = System.Drawing.Graphics.FromImage(bmp)) {
                IntPtr hdc = g.GetHdc();
                bool ok = PrintWindow(f.Handle, hdc, 2);
                g.ReleaseHdc(hdc);
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                Assert(ok, Path.GetFileName(path) + " PrintWindow 成功");
            }
            f.Close(); Pump(300);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

        // ---------- 原生：批量排孔 + 配孔检查 ----------
        public static void PatternNative(F.Application app, string outputDir){
            checks = 0;
            bool createdInstance = false;
            if (app == null) {
                try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); Console.WriteLine("AUTOHOLE attached to running CAD"); }
                catch { app = (F.Application)Activator.CreateInstance(Type.GetTypeFromProgID("SolidEdge.Application")); createdInstance = true; Console.WriteLine("AUTOHOLE started a new CAD instance"); }
                app.Visible = true; app.ScreenUpdating = true;
            }
            string dir = Path.GetFullPath(Path.Combine(outputDir, "autohole-pattern-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")));
            Directory.CreateDirectory(dir);
            A.AssemblyDocument asm = null; object original = null;
            try { original = app.ActiveDocument; } catch {}
            P.PartDocument p = null;
            try {
                asm = (A.AssemblyDocument)app.Documents.Add("SolidEdge.AssemblyDocument");
                asm.SaveAs(Path.Combine(dir, "PatternFixture.asm"));
                P.Model model; p = BuildPlate(app, "P", 0.20, 0.10, 0.01, out model);
                p.SaveAs(Path.Combine(dir, "PlateP.par")); p.Close(false); p = null;

                asm.Activate();
                var occ = asm.Occurrences.AddByFilename(Path.Combine(dir, "PlateP.par"));
                Array m = Transform.Identity.M; occ.PutMatrix(ref m, true);
                var doc = (P.PartDocument)occ.OccurrenceDocument;
                var mdl = (P.Model)doc.Models.Item(1);
                var face = PlanarFaceAtZ(mdl, 0.005);           // 上表面
                Assert(face != null, "读到打孔面");
                var faceRef = asm.CreateReference(occ, face);

                var target = AutoHoleReader.ReadTarget(faceRef);
                Assert(target.Part != null, "打孔面反查到零件");
                Info2("面面积 " + (face.Area*1e6).ToString("0.#") + " mm²");
                foreach (G.Edge de in (G.Edges)face.Edges) {
                    Array ds = new double[3], de2 = new double[3];
                    try { de.GetEndPoints(ref ds, ref de2); } catch { Info2("  edge 端点读取失败"); continue; }
                    var q1 = target.Placement.Point(V3.From(ds)); var q2 = target.Placement.Point(V3.From(de2));
                    Info2("  edge " + (de.Geometry is G.Line ? "Line" : "Other") + " len=" + ((q2-q1).Length*1000).ToString("0.##") + "mm  " + q1 + " -> " + q2);
                }
                var frame = FaceFrameReader.Read(target);
                Info2("排孔方向长度 " + frame.LengthMm.ToString("0.##") + "mm  dir=" + frame.Direction);
                Assert(Math.Abs(frame.LengthMm - 200) < 0.5, "自动取到 200mm 长边，实读 " + frame.LengthMm.ToString("0.##"));
                Assert(Math.Abs(Math.Abs(frame.Direction.X) - 1) < 1e-6, "方向沿 X");

                // 等分 4 孔，边距 20mm，通孔 Φ6
                var spec = new HolePatternSpec { Kind = HolePatternKind.Divide, Count = 4, EdgeMm = 20 };
                var offs = HolePatternSolver.Offsets(frame.LengthMm, spec);
                Assert(offs.Length == 4, "等分 4 孔");
                var centres = new List<V3>();
                foreach (var o in offs) centres.Add(frame.At(o));
                var holeSpec = new HoleSpec { Kind = HoleKind.Through, HoleDiameter = 6.0, Depth = 0 };

                double v0 = Vol(mdl);
                var res = AutoHoleWriter.Drill(target, holeSpec, centres);
                Assert(res.Created == 4, "打出 4 个孔（失败 " + res.Failures.Count + "：" + string.Join("；", res.Failures.ToArray()) + "）");
                double got = v0 - Vol(mdl);
                double expect = 4 * Math.PI * 0.003 * 0.003 * 0.01;
                Assert(Math.Abs(got - expect) / expect < 0.03,
                    "4 个 Φ6 通孔体积吻合（实 " + (got*1e9).ToString("0.#") + " / 期 " + (expect*1e9).ToString("0.#") + " mm³）");

                // 配孔检查：采集 + 分组
                var warnings = new List<string>();
                var holes = AutoHoleWriter.CollectAssemblyHoles(asm, out warnings);
                Assert(holes.Count == 4, "采集到 4 个孔（通孔上下两条圆边已去重），实得 " + holes.Count);
                Assert(holes.TrueForAll(h => Math.Abs(h.DiameterMm - 6.0) < 0.01), "采集到的直径都是 6mm");
                var groups = HoleCheck.GroupByAxis(holes);
                Assert(groups.Count == 4, "4 个孔分属 4 条独立轴线，实得 " + groups.Count);
                var issues = HoleCheck.Run(holes, null, false, new List<string>{ occ.Name });
                Assert(issues.Count == 0, "同零件上的 4 个独立孔不报问题，实得 " + issues.Count);
                var issuesWithUnpaired = HoleCheck.Run(holes, null, true, new List<string>{ occ.Name });
                Assert(issuesWithUnpaired.Count == 4, "要求报孤孔时得 4 条，实得 " + issuesWithUnpaired.Count);

                // 打了 4 个孔以后，原来的面对象已失效，必须重新取一次
                var face2 = PlanarFaceAtZ(mdl, 0.005);
                Assert(face2 != null, "重新取到打孔面");
                var target2 = AutoHoleReader.ReadTarget(asm.CreateReference(occ, face2));
                Assert(target2.Part != null, "重取的面能反查到零件");

                // 圆周均布：R25 上 6 孔
                var cir = new HolePatternSpec { Kind = HolePatternKind.Circular, Count = 6 };
                var angs = HolePatternSolver.Angles(cir);
                var c2 = new List<V3>();
                var u = frame.Direction; var v = frame.Perp;
                var centre0 = new V3(0.10, 0.05, 0.005);
                // 半径 15mm：R25 的话会和上面那 4 个孔重叠（实测体积只有 78%），
                // 重叠孔是几何事实不是 bug，所以夹具要避开。
                foreach (var deg in angs) {
                    double rad = deg * Math.PI / 180.0;
                    c2.Add(centre0 + u * (0.015*Math.Cos(rad)) + v * (0.015*Math.Sin(rad)));
                }
                double v1 = Vol(mdl);
                var res2 = AutoHoleWriter.Drill(target2, holeSpec, c2);
                Assert(res2.Created == 6, "圆周均布打出 6 个孔（失败 " + res2.Failures.Count + "）");
                double got2 = v1 - Vol(mdl);
                double expect2 = 6 * Math.PI * 0.003 * 0.003 * 0.01;
                Assert(Math.Abs(got2 - expect2) / expect2 < 0.03,
                    "圆周 6 孔体积吻合（实 " + (got2*1e9).ToString("0.#") + " / 期 " + (expect2*1e9).ToString("0.#") + " mm³）");

                // 腰孔：通切
                // 再取一次面（上一批又改了模型）
                var face3 = PlanarFaceAtZ(mdl, 0.005);
                var target3 = AutoHoleReader.ReadTarget(asm.CreateReference(occ, face3));
                var slotDirLocal = target3.Placement.InverseNormal(frame.Direction);
                var slotCentre = target3.Placement.InverseLocal(frame.At(160));
                double v2 = Vol(mdl);
                string whySlot;
                bool slotOk = AutoHoleWriter.DrillSlotOne(target3, slotCentre, slotDirLocal, 30.0, 8.0, out whySlot);
                Assert(slotOk, "腰孔通切成功" + (slotOk ? "" : "：" + whySlot));
                double got3 = v2 - Vol(mdl);
                double slotExpect = (2*0.004*(0.030-0.008) + Math.PI*0.004*0.004) * 0.01;
                Assert(Math.Abs(got3 - slotExpect) / slotExpect < 0.05,
                    "腰孔体积吻合（实 " + (got3*1e9).ToString("0.#") + " / 期 " + (slotExpect*1e9).ToString("0.#") + " mm³）");

                asm.Save();
                Console.WriteLine("AUTO-HOLE PATTERN ASSERTIONS " + checks);
                Console.WriteLine("AUTO-HOLE ARTIFACTS " + dir);
            } finally {
                if (p != null) try { p.Close(false); } catch {}
                if (asm != null) try { asm.Close(false); } catch {}
                try { ((dynamic)original).Activate(); } catch {}
                if (createdInstance) { try { app.Quit(); } catch {} }
            }
        }
        static void Info2(string s){ Console.WriteLine("INFO " + s); }

        // ---------- 原生：窗口生命周期 ----------
        // 复现"点命令后窗口闪一下就没了"：宿主 ToolContext.Show 会调一次 StartPicking，
        // 如果窗口自己在 Shown 里又调一次，第二条原生命令会把第一条顶掉 -> Terminate() -> 窗口自关。
        public static void FormNative(F.Application app, string outputDir){
            checks = 0;
            bool createdInstance = false;
            if (app == null) {
                try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); Console.WriteLine("AUTOHOLE attached to running CAD"); }
                catch { app = (F.Application)Activator.CreateInstance(Type.GetTypeFromProgID("SolidEdge.Application")); createdInstance = true; Console.WriteLine("AUTOHOLE started a new CAD instance"); }
                app.Visible = true; app.ScreenUpdating = true;
            }
            A.AssemblyDocument asm = null; object original = null;
            try { original = app.ActiveDocument; } catch {}
            try {
                asm = (A.AssemblyDocument)app.Documents.Add("SolidEdge.AssemblyDocument");
                asm.Activate();

                var f1 = new AutoHoleForm(app, asm);
                f1.Show(); Pump(700);
                Assert(f1.Visible && !f1.IsDisposed, "自动打孔窗口显示后仍然可见");
                string sc = f1.SelfCheck();
                Assert(sc.Length == 0, "自动打孔窗口自检（孔型 × 规格 全组合）：" + sc);
                f1.StartPicking(); Pump(700);
                Assert(f1.Visible && !f1.IsDisposed, "自动打孔 StartPicking 一次后窗口仍在");
                f1.StartPicking(); Pump(900);
                Assert(f1.Visible && !f1.IsDisposed, "自动打孔 StartPicking 重复调用后窗口仍在（幂等）");
                f1.Close(); Pump(300);
                Assert(f1.IsDisposed, "自动打孔窗口能正常关闭");

                var f2 = new AutoHolePatternForm(app, asm);
                f2.Show(); Pump(700);
                Assert(f2.Visible && !f2.IsDisposed, "批量排孔窗口显示后仍然可见");
                f2.StartPicking(); Pump(700);
                f2.StartPicking(); Pump(900);
                Assert(f2.Visible && !f2.IsDisposed, "批量排孔 StartPicking 重复调用后窗口仍在（幂等）");
                f2.Close(); Pump(300);

                var f3 = new AutoHoleCheckForm(app, asm);
                f3.Show(); Pump(700);
                Assert(f3.Visible && !f3.IsDisposed, "配孔检查窗口显示后仍然可见");
                f3.Close(); Pump(300);

                Console.WriteLine("AUTO-HOLE FORM ASSERTIONS " + checks);
            } finally {
                if (asm != null) try { asm.Close(false); } catch {}
                try { ((dynamic)original).Activate(); } catch {}
                if (createdInstance) { try { app.Quit(); } catch {} }
            }
        }


        // ---------- 原生：一次多选不同大小的参考孔 ----------
        public static void MultiNative(F.Application app, string outputDir){
            checks = 0;
            bool createdInstance = false;
            if (app == null) {
                try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); Console.WriteLine("AUTOHOLE attached to running CAD"); }
                catch { app = (F.Application)Activator.CreateInstance(Type.GetTypeFromProgID("SolidEdge.Application")); createdInstance = true; Console.WriteLine("AUTOHOLE started a new CAD instance"); }
                app.Visible = true; app.ScreenUpdating = true;
            }
            string dir = Path.GetFullPath(Path.Combine(outputDir, "autohole-multi-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")));
            Directory.CreateDirectory(dir);
            A.AssemblyDocument asm = null; object original = null;
            try { original = app.ActiveDocument; } catch {}
            P.PartDocument a = null, b = null;
            try {
                asm = (A.AssemblyDocument)app.Documents.Add("SolidEdge.AssemblyDocument");
                asm.SaveAs(Path.Combine(dir, "MultiFixture.asm"));

                // A 板：20mm 厚，两个不同大小的孔
                P.Model ma; a = BuildPlate(app, "MA", 0.20, 0.10, 0.02, out ma);
                DrillFixtureHoles(a, ma, 0.010, new double[][]{
                    new double[]{ 0.05, 0.05, 0.0085 },   // M10 底孔
                    new double[]{ 0.15, 0.05, 0.0135 },   // M12 过孔
                });
                Assert(((P.Model)a.Models.Item(1)).Holes.Count == 2, "A 板有两个不同大小的孔");
                a.SaveAs(Path.Combine(dir, "MultiA.par")); a.Close(false); a = null;

                P.Model mb; b = BuildPlate(app, "MB", 0.20, 0.10, 0.02, out mb);
                b.SaveAs(Path.Combine(dir, "MultiB.par")); b.Close(false); b = null;

                asm.Activate();
                var occA = asm.Occurrences.AddByFilename(Path.Combine(dir, "MultiA.par"));
                Array m1 = Transform.Identity.M; occA.PutMatrix(ref m1, true);
                var occB = asm.Occurrences.AddByFilename(Path.Combine(dir, "MultiB.par"));
                Array m2 = Transform.Frame(new V3(0,0,0.04), new V3(1,0,0), new V3(0,1,0), new V3(0,0,1)).M;
                occB.PutMatrix(ref m2, true);

                var docA = (P.PartDocument)occA.OccurrenceDocument;
                var docB = (P.PartDocument)occB.OccurrenceDocument;
                var mdlA = (P.Model)docA.Models.Item(1);
                var mdlB = (P.Model)docB.Models.Item(1);

                // 读出 A 板上两个参考孔（多选）
                var refs = new List<ReferenceHole>();
                foreach (G.Edge e in (G.Edges)((G.Body)mdlA.Body).get_Edges(G.FeatureTopologyQueryTypeConstants.igQueryAll)) {
                    if (!(e.Geometry is G.Circle)) continue;
                    var hole = AutoHoleReader.ReadReference(asm.CreateReference(occA, e));
                    bool dup = false;
                    foreach (var r in refs) if (Math.Abs(r.DiameterMm - hole.DiameterMm) < 0.01) dup = true;
                    if (!dup) refs.Add(hole);
                }
                Assert(refs.Count == 2, "多选读到 2 个不同大小的参考孔，实得 " + refs.Count);
                refs.Sort((x,y) => x.DiameterMm.CompareTo(y.DiameterMm));
                Assert(Math.Abs(refs[0].DiameterMm - 8.5) < 0.02, "第一个 Φ8.5，实读 " + refs[0].DiameterMm.ToString("0.###"));
                Assert(Math.Abs(refs[1].DiameterMm - 13.5) < 0.02, "第二个 Φ13.5，实读 " + refs[1].DiameterMm.ToString("0.###"));

                var faceB = PlanarFaceAtZ(mdlB, -0.010);
                Assert(faceB != null, "读到 B 板下表面");
                var target = AutoHoleReader.ReadTarget(asm.CreateReference(occB, faceB));

                // 面内判断
                // ContainsPoint 判的是"投影到面平面之后是否落在面内"——沿法向的偏移不影响判断，
                // 因为调用方给的孔心本来就是轴线与面的交点，一定在平面上。这是刻意的语义。
                Assert(FaceFrameReader.ContainsPoint(target, new V3(0.10, 0.05, 0.030)), "面中心点判定为在面内");
                Assert(FaceFrameReader.ContainsPoint(target, new V3(0.10, 0.05, 5.0)), "沿法向偏移的点投影后仍在面内（刻意语义）");
                Assert(!FaceFrameReader.ContainsPoint(target, new V3(0.25, 0.05, 0.030)), "超出面右边界判定为不在面内");
                Assert(!FaceFrameReader.ContainsPoint(target, new V3(0.10, 0.15, 0.030)), "超出面上边界判定为不在面内");
                Assert(!FaceFrameReader.ContainsPoint(target, new V3(-0.05, 0.05, 0.030)), "超出面左边界判定为不在面内");

                // 收集请求（强制通孔，便于精确核对体积）
                var requests = new List<AutoHoleWriter.HoleRequest>();
                foreach (var r in refs) {
                    var centre = AutoHoleReader.Intersect(r, target);
                    Assert(FaceFrameReader.ContainsPoint(target, centre), "Φ" + r.DiameterMm.ToString("0.##") + " 投影落在 B 板面内");
                    var spec = HoleMatcher.FromRow(HoleMatcher.Match(r.DiameterMm).Row, HoleKind.Through);
                    requests.Add(new AutoHoleWriter.HoleRequest { Spec = spec, Centre = centre, Source = "Φ" + r.DiameterMm.ToString("0.##") });
                }
                Assert(requests.Count == 2, "两个参考孔各生成一个打孔请求");

                double v0 = Vol(mdlB);
                var res = AutoHoleWriter.DrillRequests(target, requests);
                Assert(res.Created == 2, "一次打出 2 个孔（失败 " + res.Failures.Count + "：" + string.Join("；", res.Failures.ToArray()) + "）");
                Assert(res.Failures.Count == 0, "无失败项");
                double got = v0 - Vol(mdlB);
                // 强制通孔：Φ8.5->M10 过孔 Φ11；Φ13.5->M12 过孔 Φ13.5；都穿 20mm
                double expect = Math.PI * (0.0055*0.0055 + 0.00675*0.00675) * 0.02;
                Assert(Math.Abs(got - expect) / expect < 0.03,
                    "两个不同大小的孔体积吻合（实 " + (got*1e9).ToString("0.#") + " / 期 " + (expect*1e9).ToString("0.#") + " mm³）");
                Assert(mdlB.Holes.Count == 2, "B 板新增 2 个孔特征，实得 " + mdlB.Holes.Count);

                // 有限深度（盲孔）：从 B 板上表面往下 5mm
                var faceTop = PlanarFaceAtZ(mdlB, 0.010);
                Assert(faceTop != null, "读到 B 板上表面");
                var targetTop = AutoHoleReader.ReadTarget(asm.CreateReference(occB, faceTop));
                var blindReq = new List<AutoHoleWriter.HoleRequest>{
                    new AutoHoleWriter.HoleRequest {
                        Spec = new HoleSpec { Kind = HoleKind.Through, HoleDiameter = 10.0, Depth = 5.0 },
                        Centre = new V3(0.10, 0.05, 0.050), Source = "盲孔Φ10深5" } };
                double vBlind = Vol(mdlB);
                var resBlind = AutoHoleWriter.DrillRequests(targetTop, blindReq);
                Assert(resBlind.Created == 1, "盲孔打出（失败：" + string.Join("；", resBlind.Failures.ToArray()) + "）");
                double gotBlind = vBlind - Vol(mdlB);
                double expectBlind = Math.PI * 0.005 * 0.005 * 0.005;
                Assert(Math.Abs(gotBlind - expectBlind) / expectBlind < 0.05,
                    "盲孔 Φ10 深 5mm 体积吻合（实 " + (gotBlind*1e9).ToString("0.#") + " / 期 " + (expectBlind*1e9).ToString("0.#") + " mm³）");

                // 自动模式（不强制孔型）：Φ8.5 应配沉孔，Φ13.5 应配螺纹孔
                var autoSpecs = new List<string>();
                foreach (var r in refs) autoSpecs.Add(HoleMatcher.Match(r.DiameterMm).Target.Summary);
                Assert(autoSpecs[0].Contains("沉孔"), "Φ8.5 自动配沉孔：" + autoSpecs[0]);
                Assert(autoSpecs[1].Contains("螺纹孔"), "Φ13.5 自动配螺纹孔：" + autoSpecs[1]);

                asm.Save();
                Console.WriteLine("AUTO-HOLE MULTI ASSERTIONS " + checks);
                Console.WriteLine("AUTO-HOLE ARTIFACTS " + dir);
            } finally {
                if (a != null) try { a.Close(false); } catch {}
                if (b != null) try { b.Close(false); } catch {}
                if (asm != null) try { asm.Close(false); } catch {}
                try { ((dynamic)original).Activate(); } catch {}
                if (createdInstance) { try { app.Quit(); } catch {} }
            }
        }

        // 在板的一面上一批打好几个孔（基准面只建一次）
        static void DrillFixtureHoles(P.PartDocument part, P.Model model, double z, double[][] holes){
            var xy = FindXY(part);
            if (xy == null) throw new InvalidOperationException("找不到标准 XY 基准面。");
            var rp = part.RefPlanes.AddParallelByDistance(xy, z, P.ReferenceElementConstants.igNormalSide, M, M, M, M);
            foreach (var h in holes) {
                var prof = part.ProfileSets.Add().Profiles.Add(rp);
                double x2, y2; prof.Convert3DCoordinate(h[0], h[1], z, out x2, out y2);
                prof.Holes2d.Add(x2, y2);
                if (prof.End(P.ProfileValidationType.igProfileClosed) != 0) throw new InvalidOperationException("夹具孔轮廓未闭合。");
                var hd = part.HoleDataCollection.Add(P.FeaturePropertyConstants.igRegularHole, h[2], M,M,M,M,M,M,M,M,M,M,M,M,M,M,M,M,M,M,M);
                bool done = false;
                foreach (var side in new[]{P.FeaturePropertyConstants.igLeft, P.FeaturePropertyConstants.igRight}) {
                    var hole = model.Holes.AddThroughAll(prof, side, hd);
                    if (hole == null) continue;
                    object dsc = null;
                    if ((int)hole.GetStatusEx(out dsc) == (int)P.FeatureStatusConstants.igFeatureOK) { done = true; break; }
                    try { hole.Delete(); } catch {}
                }
                if (!done) throw new InvalidOperationException("夹具孔构造失败 Φ" + h[2]);
            }
        }

        static void Pump(int ms){
            var until = DateTime.Now.AddMilliseconds(ms);
            while (DateTime.Now < until) { System.Windows.Forms.Application.DoEvents(); System.Threading.Thread.Sleep(20); }
        }
    }
}
