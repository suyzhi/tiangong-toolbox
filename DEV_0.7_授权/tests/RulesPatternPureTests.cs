using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TianGongCadSuite {
    // 2026-10-14 新增的纯逻辑：批量排孔两端边距/横向偏移、配做规则表、设置文件、排孔预设、日志滚动；
    // 0.7.6：种子孔 + 阵列的参数（PatternFit）、改孔型回读规格（HoleSpecRead）。
    // tests/pure（dotnet run）和 PanelTests.exe 都跑这一套。
    public static class RulesPatternPureTests {
        static int checks;
        static void Assert(bool ok, string what){
            checks++;
            if (!ok) throw new Exception("FAIL: " + what);
            Console.WriteLine("PASS: " + what);
        }

        public static void Run(){
            checks = 0;
            var saved = HoleRuleBook.Current;
            try {
                Pattern();
                Rules();
                Files();
                Edit();
            } finally { HoleRuleBook.Current = saved; }
            Console.WriteLine("RULES/PATTERN PURE ASSERTIONS " + checks);
        }

        static bool Near(double a, double b){ return Math.Abs(a - b) < 1e-9; }

        static void Pattern(){
            // 两端边距不同：一头 20、一头 35
            var div = new HolePatternSpec { Kind = HolePatternKind.Divide, Count = 3, EdgeMm = 20, EndEdgeMm = 35 };
            var o = HolePatternSolver.Offsets(255, div);
            Assert(o.Length == 3 && Near(o[0], 20) && Near(o[2], 220) && Near(o[1], 120), "等分：起端 20、末端 35（255 长）→ 20/120/220");
            var same = new HolePatternSpec { Kind = HolePatternKind.Divide, Count = 2, EdgeMm = 10 };
            Assert(Near(same.EndEdge, 10), "末端边距 < 0 时跟起端一样（老行为）");
            var pit = new HolePatternSpec { Kind = HolePatternKind.Pitch, PitchMm = 50, EdgeMm = 10, EndEdgeMm = 40 };
            var op = HolePatternSolver.Offsets(200, pit);
            Assert(op.Length == 4 && Near(op[3], 160), "定距：可用长度 200-10-40=150 → 4 孔，末孔 160（离终点正好 40）");
            Assert(HolePatternSolver.PitchCount(200, pit) == 4, "定距预览孔数按两端边距算");
            Assert(HolePatternSolver.Describe(200, pit).Contains("边距 10 / 40mm"), "描述写出两端边距：" + HolePatternSolver.Describe(200, pit));
            var pit2 = new HolePatternSpec { Kind = HolePatternKind.Pitch, PitchMm = 70, EdgeMm = 10, EndEdgeMm = 10 };
            Assert(HolePatternSolver.Describe(200, pit2).Contains("末端实际余 50mm"), "定距排不满：说明末端实际剩多少：" + HolePatternSolver.Describe(200, pit2));
            try { HolePatternSolver.Offsets(50, new HolePatternSpec { Kind = HolePatternKind.Divide, Count = 2, EdgeMm = 20, EndEdgeMm = 30 }); throw new Exception("accepted"); }
            catch (ArgumentException) { Assert(true, "两端边距加起来吃掉全长被拒"); }
            try { HolePatternSolver.Offsets(100, new HolePatternSpec { Kind = HolePatternKind.Divide, Count = 2, EdgeMm = 10, EndEdgeMm = -0.0001 + 0 }); Assert(true, "末端 <0 视为与起端相同"); }
            catch (ArgumentException) { throw new Exception("FAIL: 末端 <0 应视为与起端相同"); }

            // 横向偏移：孔不能伸出面
            Assert(HolePatternSolver.LateralProblem(40, 0, 3.3) == "", "居中不报");
            Assert(HolePatternSolver.LateralProblem(40, 16, 3.3) == "", "偏 16、孔半径 3.3、半宽 20：刚好放得下");
            Assert(HolePatternSolver.LateralProblem(40, 17, 3.3).Length > 0, "偏 17：孔伸出面外要报");
            Assert(HolePatternSolver.LateralProblem(40, -17, 3.3).Length > 0, "往另一侧偏一样要报");
            Assert(HolePatternSolver.LateralProblem(0, 100, 3.3) == "", "面宽读不出来不拦");
        }

        static void Rules(){
            var presets = HoleRuleBook.Presets;
            Assert(presets.Length == 4, "内置 4 套配做规则");
            foreach (var p in presets) Assert(p.Problem() == "", "预设「" + p.Name + "」本身没问题");

            HoleRuleBook.Current = presets[0];
            var m = HoleMatcher.Match(6.6);   // M6 过孔
            Assert(m.Target.Kind == HoleKind.Tapped && m.Target.ThreadSize == "M6", "默认规则：M6 过孔配螺纹孔");
            Assert(m.Target.Note.Contains("沉头螺钉（默认）"), "说明里写出用的是哪套规则：" + m.Target.Note);
            var t = HoleMatcher.Match(5.0);   // M6 底孔
            Assert(t.Target.Kind == HoleKind.Counterbore && Near(t.Target.HoleDiameter, 6.6), "默认规则：M6 底孔配沉孔 Φ6.6（普通）");

            HoleRuleBook.Current = presets[3];   // 螺栓 + 螺母
            m = HoleMatcher.Match(6.6);
            Assert(m.Target.Kind == HoleKind.Through && Near(m.Target.HoleDiameter, 6.6), "螺栓+螺母：M6 过孔配 Φ6.6 过孔");
            Assert(HoleStandards.Resolve(m.Target) != null && HoleStandards.Resolve(m.Target).SubType == HoleStandards.ClearanceSub, "配出来的过孔是天工标准孔（常规螺钉间隙）");

            HoleRuleBook.Current = presets[1];   // 螺钉头露在外面
            t = HoleMatcher.Match(5.0);
            Assert(t.Target.Kind == HoleKind.Through && Near(t.Target.HoleDiameter, 6.6), "螺钉头露在外面：M6 底孔配 Φ6.6 过孔");

            HoleRuleBook.Current = presets[2];   // 锥形沉头
            t = HoleMatcher.Match(5.0);
            Assert(t.Target.Kind == HoleKind.Countersink && Near(t.Target.CountersinkDiameter, 12.6), "锥形沉头：M6 底孔配锥孔 Φ12.6（天工表机用）");

            // 按规格单独改：默认之外，M8 以上用螺栓、过孔配合用紧密
            var custom = presets[0].Clone();
            custom.Rules.Add(new HoleRule { Size = "M8", Role = HoleRefRole.Clearance, Target = HoleKind.Through, Fit = HoleStandards.Close });
            Assert(custom.Problem() == "", "加一条 M8 专用规则没问题");
            Assert(HoleRuleBook.NameOf(custom) == "自定义", "改过的规则叫「自定义」");
            HoleRuleBook.Current = custom;
            m = HoleMatcher.Match(9.0);   // M8 过孔
            Assert(m.Target.Kind == HoleKind.Through && Near(m.Target.HoleDiameter, 8.4), "M8 专用规则生效：过孔配 Φ8.4（紧密 H12）");
            m = HoleMatcher.Match(6.6);
            Assert(m.Target.Kind == HoleKind.Tapped, "其它规格仍按「全部」那一行（M6 过孔配螺纹孔）");

            var dup = presets[0].Clone();
            dup.Rules.Add(new HoleRule { Size = "*", Role = HoleRefRole.TapDrill, Target = HoleKind.Through });
            Assert(dup.Problem().Length > 0, "同一规格同一角色写两行要报");
            var missing = new HoleRuleSet { Name = "x" };
            missing.Rules.Add(new HoleRule { Size = "*", Role = HoleRefRole.TapDrill, Target = HoleKind.Through });
            Assert(missing.Problem().Length > 0, "缺「全部规格 · 过孔」那一行要报");
            Assert(missing.For("M6", HoleRefRole.Clearance).Target == HoleKind.Tapped, "规则缺行时兜底按默认（过孔配螺纹孔）");

            // 新补的规格：M2.5 / M14 / M24 / M30 都能反推，三种孔型都是天工标准孔
            HoleRuleBook.Current = presets[0];
            Assert(HoleMatcher.Match(15.5).Row.Size == "M14", "Φ15.5 认成 M14 过孔（原来没有 M14，只能当自定义通孔）");
            Assert(HoleMatcher.Match(26.0).Row.Size == "M24", "Φ26 认成 M24 过孔");
            Assert(HoleMatcher.Match(2.9).Row.Size == "M2.5", "Φ2.9 认成 M2.5 过孔");
            foreach (var size in new[]{ "M2.5", "M14", "M24", "M30" }) {
                var row = HoleMatcher.Find(size).Value;
                foreach (var k in new[]{ HoleKind.Through, HoleKind.Tapped, HoleKind.Counterbore })
                    Assert(HoleStandards.Resolve(HoleMatcher.FromRow(row, k)) != null, size + " " + HoleSpec.KindName(k) + " 是天工标准孔");
            }
            Assert(Near(HoleMatcher.M6.MinorDia, 4.917), "HoleMatcher.M6 取的是 M6（不再靠下标）");
        }

        static void Files(){
            string dir = Path.Combine(Path.GetTempPath(), "tg-pure-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            try {
                // 设置文件：制表符、换行、反斜杠往返
                string tricky = "a\tb\\c\nd";
                Assert(SettingsFile.Unescape(SettingsFile.Escape(tricky)) == tricky, "设置文件转义往返");
                string f = Path.Combine(dir, "x.tsv");
                SettingsFile.Write(f, new List<string[]>{ new[]{ "名", tricky, "" } }, "注释");
                var back = SettingsFile.Read(f);
                Assert(back.Count == 1 && back[0][1] == tricky && back[0][2] == "", "设置文件读回一样（注释行跳过）");
                SettingsFile.Write(f, new List<string[]>{ new[]{ "第二次" } });
                Assert(SettingsFile.Read(f)[0][0] == "第二次", "覆盖写（先写临时文件再替换）");

                // 配做规则存盘往返
                var custom = HoleRuleBook.Presets[0].Clone();
                custom.Rules.Add(new HoleRule { Size = "M8", Role = HoleRefRole.Clearance, Target = HoleKind.Through, Fit = HoleStandards.Loose });
                string rf = Path.Combine(dir, HoleRuleBook.FileName);
                HoleRuleBook.Save(custom, rf);
                var loaded = HoleRuleBook.Load(rf);
                Assert(loaded != null && loaded.SameRules(custom), "配做规则存盘再读回来一样");
                Assert(loaded.Name == "自定义", "规则文件里记的名字");
                HoleRuleBook.Save(HoleRuleBook.Presets[3], rf);
                Assert(HoleRuleBook.NameOf(HoleRuleBook.Load(rf)) == "螺栓 + 螺母", "存的是预设，读回来还认得出是哪套");
                Assert(HoleRuleBook.Load(Path.Combine(dir, "nope.tsv")) == null, "没有规则文件 → null（用默认）");

                // 排孔预设
                string pf = Path.Combine(dir, PatternPresetStore.FileName);
                var store = new PatternPresetStore(pf);
                var p = new PatternPreset { Name = "型材 M6 ×4" };
                p.Values["mode"] = "0"; p.Values["count"] = "4"; p.Values["edge"] = "20";
                store.Put(p); store.Save();
                var s2 = new PatternPresetStore(pf).Load();
                Assert(s2.Items.Count == 1 && s2.Find("型材 M6 ×4").Get("count", "") == "4", "排孔预设存盘读回");
                var p2 = new PatternPreset { Name = "型材 M6 ×4" }; p2.Values["count"] = "6";
                s2.Put(p2);
                Assert(s2.Items.Count == 1 && s2.Find("型材 M6 ×4").Get("count", "") == "6", "同名预设覆盖");
                Assert(s2.Find("型材 M6 ×4").Get("edge", "缺") == "缺", "覆盖后旧键不残留");
                Assert(s2.Remove("型材 M6 ×4") && s2.Items.Count == 0, "删除预设");

                // 配孔检查判定口径：存盘往返、坏值退回默认、改了口径立刻生效
                string of = Path.Combine(dir, HoleCheckOptions.FileName);
                var opt = new HoleCheckOptions { AxisOffsetTolMm = 0.5, MaxDiameterMm = 30 };
                opt.Save(of);
                var opt2 = HoleCheckOptions.Load(of);
                Assert(Near(opt2.AxisOffsetTolMm, 0.5) && Near(opt2.MaxDiameterMm, 30) && Near(opt2.MinDiameterMm, 3), "判定口径存盘读回");
                SettingsFile.Write(of, new List<string[]>{ new[]{ "最小孔径mm", "50" }, new[]{ "最大孔径mm", "10" } });
                Assert(Near(HoleCheckOptions.Load(of).MaxDiameterMm, 25), "最小 > 最大这种坏口径：退回默认");
                var savedOpt = HoleCheck.Options;
                try {
                    var big = new HoleRecord { PartName = "A", DiameterMm = 28, Center = new V3(0, 0, 0), Axis = new V3(0, 0, 1), AxialMax = 0.01 };
                    var idx = new PartRayIndex();
                    idx.Add(PartRayTarget.WithBoxes("B", Transform.Identity, (o2, d2) => new List<Box3>{ new Box3(new V3(-0.05, -0.05, 0.01), new V3(0.05, 0.05, 0.02)) }));
                    HoleCheck.Options = new HoleCheckOptions();
                    Assert(HoleCheck.Run(new List<HoleRecord>{ big }, null, false, new List<string>{ "A", "B" }, idx, null).Count == 0, "默认口径：Φ28 超过 25mm 不检查");
                    HoleCheck.Options = opt;
                    var iss = HoleCheck.Run(new List<HoleRecord>{ big }, null, false, new List<string>{ "A", "B" }, idx, null);
                    Assert(iss.Count == 1 && iss[0].Kind == HoleIssueKind.MissingHole, "口径放宽到 30mm：Φ28 穿过 B 报漏打孔");
                    Assert(iss[0].MissingPart == "B", "漏打孔记下是哪个零件没孔（「去配孔」提示用户点它）");
                } finally { HoleCheck.Options = savedOpt; }

                // 第 4 轮「不查的零件」：存盘往返、拆关键字；外购件内部的不列，孔在外购件上、缺孔的是自制支架照样列
                var so = new HoleCheckOptions { SkipNames = "FESTO，bosch 、 LEUZE  festo" };
                Assert(string.Join("|", so.SkipWords()) == "FESTO|bosch|LEUZE", "关键字：中英文逗号 / 顿号 / 空格都能隔开，重复的（不分大小写）去掉");
                so.Save(of);
                Assert(string.Join("|", HoleCheckOptions.Load(of).SkipWords()) == "FESTO|bosch|LEUZE", "不查的零件存盘读回");
                Assert(new HoleCheckOptions().SkipWords().Length == 0, "默认不排除任何零件（不一刀切）");
                var words = so.SkipWords();
                Func<string, HoleRecord> hr = n => new HoleRecord { PartName = n, DiameterMm = 6.6 };
                var inside = new HoleIssue { Kind = HoleIssueKind.WrongSpec, Holes = new List<HoleRecord>{ hr("FESTO_DSBC.asm:1/活塞杆.par:1"), hr("FESTO_DSBC.asm:1/端盖.par:1") } };
                var mixed = new HoleIssue { Kind = HoleIssueKind.WrongSpec, Holes = new List<HoleRecord>{ hr("FESTO_DSBC.asm:1/端盖.par:1"), hr("支架.par:1") } };
                var bracketMissing = new HoleIssue { Kind = HoleIssueKind.MissingHole, MissingPart = "支架.par:1", Holes = new List<HoleRecord>{ hr("Bosch_导轨.par:2") } };
                var vendorMissing = new HoleIssue { Kind = HoleIssueKind.MissingHole, MissingPart = "LEUZE传感器.asm:1/壳体.par:1", Holes = new List<HoleRecord>{ hr("支架.par:1") } };
                Assert(HoleCheck.Skipped(inside, words), "外购件子装配内部的配错孔：不列");
                Assert(!HoleCheck.Skipped(mixed, words), "外购件和自制支架之间的配错孔：照样列");
                Assert(!HoleCheck.Skipped(bracketMissing, words), "孔在导轨上、支架没孔：支架真漏了，照样列");
                Assert(HoleCheck.Skipped(vendorMissing, words), "缺孔的是外购件（改不了）：不列");
                Assert(!HoleCheck.Skipped(inside, new string[0]), "没填关键字：什么都不排除");
                // Run 挑缺孔零件 / 配错的一对时先挑自制件：外购件挡在前面也不会把支架的真问题藏掉
                var saved2 = HoleCheck.Options;
                try {
                    var a1 = new HoleRecord { PartName = "FESTO_缸.asm:1/缸体.par:1", DiameterMm = 6.6, Center = new V3(0, 0, 0), Axis = new V3(0, 0, 1), AxialMax = 0.01 };
                    var ix = new PartRayIndex();
                    ix.Add(PartRayTarget.WithBoxes("FESTO_缸.asm:1/端盖.par:1", Transform.Identity, (o2, d2) => new List<Box3>{ new Box3(new V3(-0.05, -0.05, 0.01), new V3(0.05, 0.05, 0.012)) }));
                    ix.Add(PartRayTarget.WithBoxes("支架.par:1", Transform.Identity, (o2, d2) => new List<Box3>{ new Box3(new V3(-0.05, -0.05, 0.012), new V3(0.05, 0.05, 0.013)) }));
                    var univ = new List<string>{ "FESTO_缸.asm:1/缸体.par:1", "FESTO_缸.asm:1/端盖.par:1", "支架.par:1" };
                    HoleCheck.Options = new HoleCheckOptions();
                    var r0 = HoleCheck.Run(new List<HoleRecord>{ a1 }, null, false, univ, ix, null);
                    Assert(r0.Count == 1 && r0[0].MissingPart == "FESTO_缸.asm:1/端盖.par:1", "没填关键字：照旧报先碰到的零件");
                    HoleCheck.Options = new HoleCheckOptions { SkipNames = "festo" };
                    var r1 = HoleCheck.Run(new List<HoleRecord>{ a1 }, null, false, univ, ix, null);
                    Assert(r1.Count == 1 && r1[0].MissingPart == "支架.par:1" && !HoleCheck.Skipped(r1[0], HoleCheck.Options.SkipWords()), "填了 FESTO：先碰到的外购件端盖跳过，报支架漏孔");
                    // 配错孔：外购件内部一对（Φ6.6 与 Φ10.5）和 外购件—支架一对（Φ6.6 与 Φ12）都配不上：报有支架的那对
                    Func<string, double, double, HoleRecord> at = (n, d, z) => new HoleRecord { PartName = n, DiameterMm = d, Center = new V3(0, 0, z), Axis = new V3(0, 0, 1), AxialMax = 0.002 };
                    var chain = new List<HoleRecord>{ at("FESTO_缸.asm:1/缸体.par:1", 6.6, 0), at("FESTO_缸.asm:1/端盖.par:1", 10.5, 0.002), at("支架.par:1", 16, 0.004) };
                    var r2 = HoleCheck.Run(chain, null, false, new List<string>(), new PartRayIndex(), null).Where(x => x.Kind == HoleIssueKind.WrongSpec).ToList();
                    Assert(r2.Count == 1 && r2[0].Parts.Contains("支架.par:1") && !HoleCheck.Skipped(r2[0], HoleCheck.Options.SkipWords()), "配错孔先报有支架的那对：" + (r2.Count > 0 ? r2[0].Message : "无"));
                } finally { HoleCheck.Options = saved2; }

                // 日志滚动
                string lf = Path.Combine(dir, "panel.log");
                File.WriteAllText(lf, new string('x', 2000));
                Assert(!Log.Roll(lf, 4096), "没到上限不滚动");
                Assert(Log.Roll(lf, 1000) && File.Exists(lf + ".1") && !File.Exists(lf), "超过上限：当前文件改名成 .1");
                File.WriteAllText(lf, new string('y', 2000));
                Assert(Log.Roll(lf, 1000) && File.ReadAllText(lf + ".1").StartsWith("y"), "再滚动：覆盖更早的 .1，只留一份");
            } finally {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
        static V3 Mm(double x, double y, double z){ return new V3(x / 1000, y / 1000, z / 1000); }

        static void Edit(){
            // ---- PatternFit.Linear：角度从参考方向绕法向逆时针量（真机 X6：30° → (20,15)(37.321,25)(54.641,35)）----
            var n = new V3(0, 0, 1); var u = new V3(1, 0, 0);
            double sp, ang;
            var row = new List<V3>{ Mm(20, 15, 20), Mm(37.3205080757, 25, 20), Mm(54.6410161514, 35, 20) };
            Assert(PatternFit.Linear(row, n, u, out sp, out ang) && Math.Abs(sp - 0.02) < 1e-9 && Math.Abs(ang - Math.PI / 6) < 1e-9, "直线 30°：间距 20mm、角 π/6");
            var back = new List<V3>{ Mm(80, 30, 0), Mm(50, 30, 0), Mm(20, 30, 0) };
            Assert(PatternFit.Linear(back, n, u, out sp, out ang) && Math.Abs(Math.Abs(ang) - Math.PI) < 1e-9, "反向排的一排：角 ±π");
            var down = new List<V3>{ Mm(0, 0, 0), Mm(0, -10, 0) };
            Assert(PatternFit.Linear(down, n, u, out sp, out ang) && Math.Abs(ang + Math.PI / 2) < 1e-9, "沿 -Y：角 -π/2（顺时针）");
            Assert(PatternFit.Linear(row, new V3(0, 0, -1), u, out sp, out ang) && Math.Abs(ang + Math.PI / 6) < 1e-9, "法向反过来：同一排的角度变号");
            var uneven = new List<V3>{ Mm(0, 0, 0), Mm(10, 0, 0), Mm(21, 0, 0) };
            Assert(!PatternFit.Linear(uneven, n, u, out sp, out ang), "间距不等：不能做成阵列");
            var bent = new List<V3>{ Mm(0, 0, 0), Mm(10, 0, 0), Mm(20, 1, 0) };
            Assert(!PatternFit.Linear(bent, n, u, out sp, out ang), "不在一条直线上：不能做成阵列");
            var offPlane = new List<V3>{ Mm(0, 0, 0), Mm(10, 0, 5) };
            Assert(!PatternFit.Linear(offPlane, n, u, out sp, out ang), "方向不在基准面里：不能做成阵列");
            Assert(!PatternFit.Linear(new List<V3>{ Mm(0, 0, 0) }, n, u, out sp, out ang), "只有一个孔：不做阵列");
            // 参考方向不是 X：基准面参考方向 = +Y 时，沿 +X 是 -π/2
            Assert(PatternFit.Linear(new List<V3>{ Mm(0, 0, 0), Mm(10, 0, 0) }, n, new V3(0, 1, 0), out sp, out ang) && Math.Abs(ang + Math.PI / 2) < 1e-9, "参考方向 +Y、沿 +X：角 -π/2");

            // ---- PatternFit.Circular（真机 X7：绕 (50,30) 4 个，(65,30)→(50,45) 逆时针 +90°）----
            V3 ax; double step;
            var ring = new List<V3>{ Mm(65, 30, 20), Mm(50, 45, 20), Mm(35, 30, 20), Mm(50, 15, 20) };
            Assert(PatternFit.Circular(ring, Mm(50, 30, 20), n, out step, out ax) && Math.Abs(step - Math.PI / 2) < 1e-9, "圆周 4 个逆时针：每步 +π/2");
            var cw = new List<V3>{ Mm(65, 30, 20), Mm(50, 15, 20), Mm(35, 30, 20) };
            Assert(PatternFit.Circular(cw, Mm(50, 30, 20), n, out step, out ax) && Math.Abs(step + Math.PI / 2) < 1e-9, "顺时针排：每步 -π/2");
            Assert(PatternFit.Circular(ring, Mm(50, 30, 0), n, out step, out ax) && Math.Abs(ax.Z - 0.02) < 1e-12, "圆心不在孔心平面上：投影到孔心平面");
            var six = new List<V3>();
            for (int i = 0; i < 6; i++) { double a = Math.PI / 3 * i + 0.2; six.Add(Mm(10 + 25 * Math.Cos(a), -5 + 25 * Math.Sin(a), 3)); }
            Assert(PatternFit.Circular(six, Mm(10, -5, 3), n, out step, out ax) && Math.Abs(step - Math.PI / 3) < 1e-9, "6 个、起始角 0.2rad：每步 π/3");
            var badR = new List<V3>{ Mm(65, 30, 0), Mm(50, 46, 0) };
            Assert(!PatternFit.Circular(badR, Mm(50, 30, 0), n, out step, out ax), "半径不一样：不能做圆周阵列");
            var badStep = new List<V3>{ Mm(65, 30, 0), Mm(50, 45, 0), Mm(50, 15, 0) };
            Assert(!PatternFit.Circular(badStep, Mm(50, 30, 0), n, out step, out ax), "转角不均匀：不能做圆周阵列");

            // ---- HoleSpecRead：插件打的螺纹孔是"普通孔 + 螺纹数据" ----
            var tapped = HoleSpecRead.From(new HoleDataValues { HoleType = "igRegularHole", Treatment = "igTappedHole", ThreadDescription = "M6", Size = "M6",
                HoleDiameter = 4.9170000001, Through = true, ThreadDepthMethod = "igThroughAll" });
            Assert(tapped.Kind == HoleKind.Tapped && tapped.ThreadSize == "M6" && Math.Abs(tapped.HoleDiameter - 4.917) < 1e-9 && tapped.Through && tapped.ThreadFull, "回读螺纹孔：M6 全长 Φ4.917 贯通");
            var tapped2 = HoleSpecRead.From(new HoleDataValues { HoleType = "igRegularHole", Treatment = "igNone", ThreadDescription = "M8x1.25",
                HoleDiameter = 6.647, Through = false, Depth = 20, ThreadDepthMethod = "igFinite", ThreadDepth = 16 });
            Assert(tapped2.Kind == HoleKind.Tapped && tapped2.ThreadSize == "M8" && !tapped2.Through && Math.Abs(tapped2.Depth - 20) < 1e-9 && !tapped2.ThreadFull && Math.Abs(tapped2.ThreadDepth - 16) < 1e-9,
                   "回读：只有螺纹描述也算螺纹孔；盲孔深 20、螺纹深 16");
            var cb = HoleSpecRead.From(new HoleDataValues { HoleType = "igCounterboreHole", Size = "M6", HoleDiameter = 6.6, CounterboreDiameter = 11, CounterboreDepth = 6.8 });
            Assert(cb.Kind == HoleKind.Counterbore && cb.ThreadSize == "M6" && Math.Abs(cb.CounterboreDiameter - 11) < 1e-9 && cb.Through, "回读圆柱沉孔 M6 Φ6.6/Φ11 深6.8");
            Assert(HoleNaming.Short(cb) == "M6 沉孔 Φ6.6/Φ11 深6.8", "回读的沉孔命名和打孔时一样：" + HoleNaming.Short(cb));
            var cs = HoleSpecRead.From(new HoleDataValues { HoleType = "igCountersinkHole", HoleDiameter = 6.6, CountersinkDiameter = 12.6, CountersinkAngle = 90 });
            Assert(cs.Kind == HoleKind.Countersink && Math.Abs(cs.CountersinkAngle - 90) < 1e-9 && cs.ThreadSize == "", "回读锥形沉孔（没有大小）");
            var plain = HoleSpecRead.From(new HoleDataValues { HoleType = "igRegularHole", Treatment = "igNone", HoleDiameter = 9 });
            Assert(plain.Kind == HoleKind.Through && plain.ThreadSize == "" && HoleNaming.Short(plain) == "通孔 Φ9", "回读普通通孔 Φ9");
            var vb = HoleSpecRead.From(new HoleDataValues { HoleType = "igRegularHole", HoleDiameter = 5, Through = false, Depth = 12, BottomAngle = 118 });
            Assert(vb.Bottom == HoleBottom.VBottom && Math.Abs(vb.BottomAngle - 118) < 1e-9 && Math.Abs(vb.Depth - 12) < 1e-9, "回读盲孔 V 底 118°（撤销时按它重建）");
            var flat = HoleSpecRead.From(new HoleDataValues { HoleType = "igRegularHole", HoleDiameter = 5, Through = false, Depth = 12, BottomAngle = 0 });
            Assert(flat.Bottom == HoleBottom.Flat, "孔底角度 0 = 平底");
            Assert(HoleSpecRead.MetricSize("GB M10 普通") == "M10" && HoleSpecRead.MetricSize("m2.5") == "M2.5" && HoleSpecRead.MetricSize("Φ6") == "", "从文字里认出 M 规格");
            Assert(HoleNaming.PatternName(cb) == "M6 沉孔 Φ6.6/Φ11 深6.8 阵列", "阵列特征名 = 规格短名 + 阵列");
        }
    }
}
