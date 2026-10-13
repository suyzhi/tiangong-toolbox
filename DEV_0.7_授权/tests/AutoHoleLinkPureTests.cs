using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace TianGongCadSuite {
    // 0.7.3 的三件事的纯逻辑回归：螺纹深度、特征命名/编组、参考孔关联（序列化 + 求新孔心）。
    // 真机事实来源：tools/hole-feature-probe（2026-10-13，天工CAD 225.03.00.165）。
    public static partial class AutoHoleTests {
        public static void LinkPure(){
            ThreadDepthPure();
            NamingPure();
            LinkFormatPure();
            LinkSolvePure();
            StandardPure();
            CheckChainPure();
        }

        // 配孔检查（2026-10-13 复查修的两处）：
        //   ① 同一条直线上隔得很远的孔不算一对（CoaxialSpanTolMm 原来定义了却没用上）；
        //   ② 漏打孔只看孔附近（get_FacesByRay 实测是整条直线的查询，原来一米外的零件也会触发）。
        static HoleRecord Rec(string part, double zLoMm, double zHiMm, double dia){
            return new HoleRecord { Center = new V3(0, 0, zLoMm / 1000.0), Axis = new V3(0, 0, 1), DiameterMm = dia, PartName = part,
                                    AxialMin = 0, AxialMax = (zHiMm - zLoMm) / 1000.0 };
        }
        // 零件 = z 区间 [lo,hi]mm 的一块板（x,y 各 ±100mm）；直线碰到就返回它的包围盒
        static PartRayTarget Slab(string name, double zLoMm, double zHiMm){
            var box = new Box3(new V3(-0.1, -0.1, zLoMm / 1000.0), new V3(0.1, 0.1, zHiMm / 1000.0));
            return PartRayTarget.WithBoxes(name, Transform.Identity, (o, d) => {
                double t0, t1;
                return box.Clip(o, d, out t0, out t1) ? new List<Box3>{ box } : new List<Box3>();
            });
        }

        static void CheckChainPure(){
            // 包围盒求交
            double t0, t1;
            var b = new Box3(new V3(0, 0, 0.01), new V3(0.1, 0.1, 0.02));
            Assert(b.Clip(new V3(0.05, 0.05, 0), new V3(0, 0, 1), out t0, out t1) && Math.Abs(t0 - 0.01) < 1e-12 && Math.Abs(t1 - 0.02) < 1e-12, "直线穿过盒子：参数区间 [10,20]mm");
            Assert(!b.Clip(new V3(0.2, 0.05, 0), new V3(0, 0, 1), out t0, out t1), "直线在盒子外：不相交");
            Assert(Box3.Infinite.Clip(new V3(5, 5, 5), new V3(0, 1, 0), out t0, out t1), "无穷大盒子总相交（读不到包围盒时的退路）");

            // 孔记录的轴向范围：同一个孔的上下两条圆边合并
            var r = new HoleRecord { Center = new V3(0, 0, 0.01), Axis = new V3(0, 0, -1), DiameterMm = 6.6, PartName = "A" };
            r.Extend(new V3(0, 0, 0));
            Assert(Math.Abs(r.AxialMin) < 1e-12 && Math.Abs(r.AxialMax - 0.01) < 1e-12, "轴线朝 -Z 的孔，下面那条边在 +0.01（沿自身轴线）");

            // ① 隔得很远的同轴孔不配对：A 板 Φ8.5（M10 底孔）在 z0~20，C 板 Φ6.8（M8 底孔）在 z300~320
            var farPair = new List<HoleRecord>{ Rec("A", 0, 20, 8.5), Rec("C", 300, 320, 6.8) };
            Assert(HoleCheck.GroupByAxis(farPair).Count == 1, "两孔同一条直线（分组仍是一组）");
            Assert(HoleCheck.SplitByGap(HoleCheck.GroupByAxis(farPair)[0], HoleCheck.CoaxialSpanTolMm).Count == 2, "隔 280mm 切成两段");
            Assert(HoleCheck.Run(farPair, null).Count == 0, "隔 280mm 的两个孔不再互相比规格（原来报配错孔）");
            Assert(HoleCheck.Run(farPair, null, true).Count(i => i.Kind == HoleIssueKind.Unpaired) == 2, "要求报孤孔时两个都是孤孔");
            Assert(HoleCheck.CountGroups(farPair) == 2, "摘要里按 2 组同轴孔计");
            // 挨着（间隙 2mm）照样配对、照样能报配错孔
            var near = new List<HoleRecord>{ Rec("A", 0, 20, 8.5), Rec("C", 22, 42, 6.8) };
            Assert(HoleCheck.Run(near, null).Count(i => i.Kind == HoleIssueKind.WrongSpec) == 1, "间隙 2mm 仍算一对：M10 底孔配 M8 底孔 -> 配错孔");
            Assert(HoleCheck.CountGroups(new List<HoleRecord>{ Rec("A", 0, 20, 8.5), Rec("A", 0, 20, 2.0) }) == 1, "组数只算 3~25mm 的孔（Φ2 不计）");

            // ② 漏打孔只看孔附近
            var idx = new PartRayIndex();
            idx.Add(Slab("B", 10, 20));         // 夹在 A、C 中间
            idx.Add(Slab("FAR", 800, 820));     // 同一条直线上 800mm 外
            var all = new List<string>{ "A", "B", "C", "FAR" };
            // A z0~10、C z20~30 都有 Φ6.6，中间的 B 没孔：原来 A、C 被当成"配好的一对"，B 的漏孔报不出来
            var sandwich = new List<HoleRecord>{ Rec("A", 0, 10, 6.6), Rec("C", 20, 30, 6.6) };
            var sw = HoleCheck.Run(sandwich, null, false, all, idx);
            Assert(sw.Count == 1 && sw[0].Kind == HoleIssueKind.MissingHole, "夹在中间的 B 漏打孔被报出来，实得 " + Describe(sw));
            Assert(sw.Count == 1 && sw[0].Message.Contains("穿过了 B") && sw[0].Message.Contains("A、C"), "一条漏孔说清是 B，且 A、C 合成一条：" + (sw.Count > 0 ? sw[0].Message : ""));
            // 只有远处有零件：不报
            var lone = new List<HoleRecord>{ Rec("A", 100, 110, 6.6) };
            var idxFar = new PartRayIndex(); idxFar.Add(Slab("FAR", 800, 820));
            Assert(HoleCheck.Run(lone, null, false, all, idxFar).Count == 0, "800mm 外的零件不再触发漏打孔（原来整条直线都算）");
            // 紧挨着（间隙 2mm）没孔的零件：报
            var idxNear = new PartRayIndex(); idxNear.Add(Slab("B", 112, 122));
            var ln = HoleCheck.Run(lone, null, false, all, idxNear);
            Assert(ln.Count == 1 && ln[0].Kind == HoleIssueKind.MissingHole, "间隙 2mm 的零件没孔 -> 漏打孔");
            // 读不到包围盒（Infinite）：退回整条直线都算，宁可多报
            var idxInf = new PartRayIndex();
            idxInf.Add(PartRayTarget.WithBoxes("B", Transform.Identity, (o, d) => new List<Box3>{ Box3.Infinite }));
            Assert(HoleCheck.Run(lone, null, false, all, idxInf).Count == 1, "读不到包围盒时宁可多报");
            // 零件在装配里平移了：包围盒在零件坐标里，换算后才判距离
            var moved = new PartRayIndex();
            var slabLocal = new Box3(new V3(-0.1, -0.1, 0), new V3(0.1, 0.1, 0.01));
            moved.Add(PartRayTarget.WithBoxes("B", Transform.Frame(new V3(0, 0, 0.111), new V3(1, 0, 0), new V3(0, 1, 0), new V3(0, 0, 1)),
                (o, d) => { double a0, a1; return slabLocal.Clip(o, d, out a0, out a1) ? new List<Box3>{ slabLocal } : new List<Box3>(); }));
            Assert(HoleCheck.Run(lone, null, false, all, moved).Count == 1, "零件平移到 z111 后按装配位置判：紧挨着 -> 漏打孔");
        }

        // 天工孔标准表（GB Metric）：插件自动生成的孔都要能在表里认出来，天工自带的孔命令打开才不显示 *
        static void StandardPure(){
            foreach (var r in HoleMatcher.Table) {
                foreach (var k in new[]{ HoleKind.Through, HoleKind.Tapped, HoleKind.Counterbore }) {
                    var s = HoleMatcher.FromRow(r, k);
                    Assert(HoleStandards.Resolve(s) != null, r.Size + " " + HoleSpec.KindName(k) + " 是天工标准孔：" + s.Summary);
                }
                var cs = HoleMatcher.FromRow(r, HoleKind.Countersink);
                double dummy;
                bool inTable = HoleStandards.CountersinkOf(r.Size, out dummy);
                Assert((HoleStandards.Resolve(cs) != null) == inTable, r.Size + " 锥形沉孔：天工表里有就认成标准孔，没有（M12 以上）就是非标");
            }
            var cb = HoleStandards.Resolve(HoleMatcher.FromRow(HoleMatcher.Find("M6").Value, HoleKind.Counterbore));
            Assert(cb.Standard == "GB Metric" && cb.SubType == "内六角螺钉" && cb.Size == "M6" && cb.Fit == "普通 (H13)", "M6 沉孔 = GB Metric / 内六角螺钉 / M6 / 普通 (H13)");
            var th = HoleStandards.Resolve(HoleMatcher.FromRow(HoleMatcher.Find("M6").Value, HoleKind.Through));
            Assert(th.SubType == "常规螺钉间隙" && th.Size == "M6" && th.Fit == "普通 (H13)", "M6 过孔 Φ6.6 = 常规螺钉间隙 / M6 / 普通 (H13)");
            var tp = HoleStandards.Resolve(HoleMatcher.FromRow(HoleMatcher.Find("M6").Value, HoleKind.Tapped));
            Assert(tp.SubType == "标准螺纹" && tp.Size == "M6" && tp.Fit == "", "M6 螺纹孔 = 标准螺纹 / M6（无配合）");
            var cs6 = HoleStandards.Resolve(HoleMatcher.FromRow(HoleMatcher.Find("M6").Value, HoleKind.Countersink));
            Assert(cs6.SubType == "机用" && cs6.Size == "M6" && Math.Abs(cs6.CsDia - 12.6) < 1e-9, "M6 锥形沉孔 = 机用 / M6 / Φ12.6");
            // 自动反推出来的（参考孔 Φ6.6 → 螺纹孔；Φ5 → 沉孔）也是标准孔
            Assert(HoleStandards.Resolve(HoleMatcher.Match(6.6).Target) != null, "Φ6.6 反推出的 M6 螺纹孔是标准孔");
            Assert(HoleStandards.Resolve(HoleMatcher.Match(5.0).Target) != null, "Φ5 反推出的 M6 沉孔是标准孔");
            // 紧密配合的过孔
            var close = new HoleSpec { Kind = HoleKind.Through, ThreadSize = "M6", HoleDiameter = 6.4 };
            Assert(HoleStandards.Resolve(close).Fit == "紧密 (H12)", "M6 Φ6.4 = 紧密 (H12)");
            // 没有螺纹规格的通孔按钻头大小认
            var drill = HoleStandards.Resolve(new HoleSpec { Kind = HoleKind.Through, HoleDiameter = 6 });
            Assert(drill != null && drill.SubType == "钻头大小" && drill.Size == "6" && drill.Fit == "精确", "Φ6 通孔 = 钻头大小 / 6 / 精确");
            Assert(HoleStandards.Resolve(new HoleSpec { Kind = HoleKind.Through, HoleDiameter = 6.33 }) == null, "Φ6.33 不在表里：非标");
            // 尺寸差一点也不能硬套（否则天工会按表把尺寸改掉）
            Assert(HoleStandards.Resolve(new HoleSpec { Kind = HoleKind.Counterbore, ThreadSize = "M6", HoleDiameter = 6.6, CounterboreDiameter = 11, CounterboreDepth = 6.5 }) == null,
                   "旧经验值 M6 沉孔深 6.5 对不上表（表里是 6.8）：非标 —— 这就是原来显示 * 的原因");
            Assert(HoleStandards.Resolve(new HoleSpec { Kind = HoleKind.Countersink, ThreadSize = "M6", HoleDiameter = 6.6, CountersinkDiameter = 12.6, CountersinkAngle = 82 }) == null,
                   "锥角不是 90° 的锥沉：非标");
            Assert(HoleStandards.Resolve(new HoleSpec { Kind = HoleKind.Tapped, ThreadSize = "", HoleDiameter = 4.917 }) == null, "没有螺纹规格的自定义螺纹孔：非标");
            Assert(HoleStandards.Describe(new HoleSpec { Kind = HoleKind.Through, HoleDiameter = 6.33 }).Contains("*"), "非标孔的说明里写明天工会显示 *");
            Assert(HoleStandards.Describe(HoleMatcher.FromRow(HoleMatcher.Find("M8").Value, HoleKind.Counterbore)).Contains("内六角螺钉 M8 普通 (H13)"), "标准孔的说明写出是表里哪一行");
        }

        static void ThreadDepthPure(){
            // 自动反推出来的螺纹孔：默认全长，指定深度的默认值 = 2×公称直径
            var m6 = HoleMatcher.Match(6.6).Target;
            Assert(m6.Kind == HoleKind.Tapped && m6.ThreadSize == "M6", "Φ6.6 → M6 螺纹孔");
            Assert(m6.ThreadFull, "自动反推的螺纹孔默认螺纹全长（CAD 不给就只攻 2mm）");
            Assert(Math.Abs(m6.ThreadDepth - 12) < 1e-9, "M6 默认指定螺纹深度 12（实 " + m6.ThreadDepth + "）");
            Assert(Math.Abs(HoleSpec.DefaultThreadDepth("M8") - 16) < 1e-9, "M8 默认指定螺纹深度 16");
            Assert(Math.Abs(HoleSpec.DefaultThreadDepth("") - 12) < 1e-9, "解析不出规格时默认 12");
            Assert(Math.Abs(HoleMatcher.FromRow(HoleMatcher.Find("M10").Value, HoleKind.Tapped).ThreadDepth - 20) < 1e-9, "手选 M10 螺纹孔默认指定深度 20");

            // 实际攻丝深度
            var s = new HoleSpec { Kind = HoleKind.Tapped, ThreadSize = "M6", HoleDiameter = 4.917, Depth = 0, ThreadFull = true, ThreadDepth = 12 };
            Assert(s.EffectiveThreadDepth == 0, "贯通 + 全长：攻丝深度 0（= 整孔）");
            s.ThreadFull = false;
            Assert(Math.Abs(s.EffectiveThreadDepth - 12) < 1e-9, "贯通 + 指定 12：攻 12");
            s.Depth = 16; s.ThreadFull = true;
            Assert(Math.Abs(s.EffectiveThreadDepth - 16) < 1e-9, "盲孔 16 + 全长：攻到孔底 16");
            s.ThreadFull = false; s.ThreadDepth = 10;
            Assert(Math.Abs(s.EffectiveThreadDepth - 10) < 1e-9, "盲孔 16 + 指定 10：攻 10");
            Assert(s.ThreadProblem.Length == 0, "盲孔 16 + 指定 10 成立");
            s.ThreadDepth = 20;
            Assert(s.ThreadProblem.Contains("超过"), "盲孔 16 + 指定 20：不成立（" + s.ThreadProblem + "）");
            s.ThreadDepth = 0;
            Assert(s.ThreadProblem.Length > 0, "指定深度 0 不成立");
            var plain = new HoleSpec { Kind = HoleKind.Through, HoleDiameter = 6.6, ThreadFull = false, ThreadDepth = 0 };
            Assert(plain.ThreadProblem.Length == 0 && plain.ThreadText.Length == 0 && plain.EffectiveThreadDepth == 0, "非螺纹孔不谈螺纹深度");

            // 摘要里写明螺纹深度（界面上一眼看出"螺纹只攻一段"）
            var t = new HoleSpec { Kind = HoleKind.Tapped, ThreadSize = "M6", HoleDiameter = 4.917, ThreadFull = false, ThreadDepth = 10 };
            Assert(t.Summary.Contains("螺纹深 10"), "摘要写明螺纹深 10：" + t.Summary);
            t.ThreadFull = true;
            Assert(t.Summary.Contains("螺纹全长"), "摘要写明螺纹全长：" + t.Summary);

            // 孔形状：装饰螺纹按深度画
            var shape = HoleShapeBuilder.Build(new HoleSpec { Kind = HoleKind.Tapped, ThreadSize = "M6", HoleDiameter = 4.917, ThreadFull = false, ThreadDepth = 10 }, 0);
            Assert(!shape.ThreadFull && Math.Abs(shape.ThreadDepthMm - 10) < 1e-9, "形状里装饰螺纹深 10");
            Assert(shape.ThicknessMm > 10, "指定螺纹深度的贯通孔，示意板厚比螺纹深（看得出只攻了一段），实 " + shape.ThicknessMm);
            var full = HoleShapeBuilder.Build(new HoleSpec { Kind = HoleKind.Tapped, ThreadSize = "M6", HoleDiameter = 4.917, Depth = 16, ThreadFull = true }, 0);
            Assert(full.ThreadFull && Math.Abs(full.ThreadDepthMm - 16) < 1e-9, "盲孔全长：装饰螺纹画到孔底 16");
            var bad = HoleShapeBuilder.Build(new HoleSpec { Kind = HoleKind.Tapped, ThreadSize = "M6", HoleDiameter = 4.917, Depth = 10, ThreadFull = false, ThreadDepth = 15 }, 0);
            Assert(bad.Problem.Contains("超过"), "螺纹深度超过孔深的规格，形状拒画：" + bad.Problem);
        }

        static void NamingPure(){
            var tap = new HoleSpec { Kind = HoleKind.Tapped, ThreadSize = "M6", HoleDiameter = 4.917, ThreadFull = true };
            Assert(HoleNaming.Short(tap) == "M6 螺纹孔（螺纹全长）", "螺纹孔短名：" + HoleNaming.Short(tap));
            tap.ThreadFull = false; tap.ThreadDepth = 12; tap.Depth = 16;
            Assert(HoleNaming.Short(tap) == "M6 螺纹孔 孔深16（螺纹深 12）", "盲螺纹孔短名：" + HoleNaming.Short(tap));
            var custom = new HoleSpec { Kind = HoleKind.Tapped, ThreadSize = "", HoleDiameter = 4.5, ThreadFull = true };
            Assert(HoleNaming.Short(custom) == "螺纹孔 Φ4.5（螺纹全长）", "自定义螺纹孔写孔径：" + HoleNaming.Short(custom));
            var cb = new HoleSpec { Kind = HoleKind.Counterbore, ThreadSize = "M6", HoleDiameter = 6.6, CounterboreDiameter = 11, CounterboreDepth = 6.5 };
            Assert(HoleNaming.Short(cb) == "M6 沉孔 Φ6.6/Φ11 深6.5", "沉孔短名：" + HoleNaming.Short(cb));
            var cs = new HoleSpec { Kind = HoleKind.Countersink, ThreadSize = "M6", HoleDiameter = 6.6, CountersinkDiameter = 10, CountersinkAngle = 90 };
            Assert(HoleNaming.Short(cs) == "M6 锥形沉孔 Φ6.6/Φ10 90°", "锥沉短名：" + HoleNaming.Short(cs));
            var thr = new HoleSpec { Kind = HoleKind.Through, ThreadSize = "M6", HoleDiameter = 6.6 };
            Assert(HoleNaming.Short(thr) == "通孔 Φ6.6", "通孔短名不带螺纹规格：" + HoleNaming.Short(thr));
            var blind = new HoleSpec { Kind = HoleKind.Through, HoleDiameter = 6, Depth = 10 };
            Assert(HoleNaming.Short(blind) == "盲孔 Φ6 深10", "盲孔短名：" + HoleNaming.Short(blind));
            blind.Chamfer = true; blind.ChamferSetback = 0.5;
            Assert(HoleNaming.Short(blind) == "盲孔 Φ6 深10 倒角0.5", "带倒角写倒角：" + HoleNaming.Short(blind));

            Assert(HoleNaming.Group(thr, 4) == "通孔 Φ6.6 ×4", "组名 = 短名 ×N");
            Assert(HoleNaming.Member(thr, 2) == "通孔 Φ6.6-2", "成员名 = 短名-序号");
            var taken = new HashSet<string> { "通孔 Φ6.6-1", "通孔 Φ6.6-2", "通孔 Φ6.6 ×2" };
            var names = HoleNaming.Members(thr, 3, taken);
            Assert(names.Count == 3 && names[0] == "通孔 Φ6.6-3" && names[2] == "通孔 Φ6.6-5", "第二批同规格孔接着往下编号：" + string.Join(",", names.ToArray()));
            Assert(HoleNaming.Unique("通孔 Φ6.6 ×2", taken) == "通孔 Φ6.6 ×2 (2)", "组名重名时加 (2)");
            Assert(HoleNaming.Unique("通孔 Φ6.6 ×3", taken) == "通孔 Φ6.6 ×3", "不重名原样返回");
            var longSpec = new HoleSpec { Kind = HoleKind.Countersink, ThreadSize = "M20", HoleDiameter = 222.22, CountersinkDiameter = 333.33, CountersinkAngle = 120, Depth = 444.44 };
            Assert(HoleNaming.Group(longSpec, 1234).Length <= HoleNaming.MaxLength, "名字长度封顶 " + HoleNaming.MaxLength);
            Assert(HoleNaming.SpecKey(thr) != HoleNaming.SpecKey(blind), "不同规格的键不同");
            var vb = new HoleSpec { Kind = HoleKind.Through, HoleDiameter = 6, Depth = 10, Bottom = HoleBottom.VBottom };
            var fb = new HoleSpec { Kind = HoleKind.Through, HoleDiameter = 6, Depth = 10, Bottom = HoleBottom.Flat };
            Assert(HoleNaming.SpecKey(vb) != HoleNaming.SpecKey(fb), "孔底不同（短名一样）也不能编进同一组");
        }

        static void LinkFormatPure(){
            var link = new HoleLink {
                RefOcc = "子装配;1.asm:1/板=A%.par:2", RefDoc = "板=A%.par", RefMode = "C",
                RefCenter = new V3(0.0123456789, -0.5, 1e-9), RefAxis = new V3(0, 0, 2),
                RefFeature = "6.6 简单孔 1", RefDiameterMm = 6.6,
                TgtOcc = "板B.par:1", TgtDoc = "板B.par", TgtMode = "R", TgtCenter = new V3(0.02, 0.03, 0.01)
            };
            string text = link.Format();
            var back = HoleLink.Parse(text);
            Assert(back != null, "关联记录能读回来：" + text);
            Assert(back.RefOcc == link.RefOcc && back.RefDoc == link.RefDoc && back.RefMode == "C", "名字里的 ; = % 转义后原样读回");
            Assert(back.RefFeature == "6.6 简单孔 1" && back.TgtOcc == "板B.par:1" && back.TgtMode == "R", "特征名/实例路径原样读回");
            Assert((back.RefCenter - link.RefCenter).Length < 1e-15 && (back.TgtCenter - link.TgtCenter).Length < 1e-15, "坐标按 R 格式无损往返");
            Assert(Math.Abs(back.RefAxis.Length - 1) < 1e-12 && Math.Abs(back.RefAxis.Z - 1) < 1e-12, "轴线读回后是单位向量");
            Assert(Math.Abs(back.RefDiameterMm - 6.6) < 1e-12, "孔径读回");
            Assert(HoleLink.Parse("") == null && HoleLink.Parse("garbage") == null && HoleLink.Parse("v=1;ro=x") == null, "残缺记录读成 null（不当真）");
            Assert(HoleLink.Parse(text.Replace("v=1", "v=0")) == null, "版本号非法读成 null");
        }

        static void LinkSolvePure(){
            // 参考板 A 在原点，孔心 (30,40,10)mm 轴线 -Z；打孔板 B 在 A 上方 z+30，打孔面是 B 的底面（局部 z=0，法向 -Z）
            var aPlace = Transform.Identity;
            var bPlace = Transform.Frame(new V3(0, 0, 0.03), new V3(1, 0, 0), new V3(0, 1, 0), new V3(0, 0, 1));
            V3 rc = new V3(0.03, 0.04, 0.01), ra = new V3(0, 0, -1);
            V3 pp = new V3(0.03, 0.04, 0), pn = new V3(0, 0, -1);
            V3 d; string why;
            Assert(HoleLink.Solve(aPlace, rc, ra, bPlace, pp, pn, out d, out why), "同轴时能求出孔心：" + why);
            Assert((d - new V3(0.03, 0.04, 0)).Length < 1e-12, "没挪：孔心就在原位");
            Assert(HoleLink.OffsetMm(pp, d) < HoleLink.SyncToleranceMm, "没挪：偏差在容差内");

            // A 在装配里平移 (+5, -3)mm：B 上的孔心应跟着到 (35,37)
            var aMoved = Transform.Frame(new V3(0.005, -0.003, 0), new V3(1, 0, 0), new V3(0, 1, 0), new V3(0, 0, 1));
            Assert(HoleLink.Solve(aMoved, rc, ra, bPlace, pp, pn, out d, out why), "A 平移后能求出孔心");
            Assert((d - new V3(0.035, 0.037, 0)).Length < 1e-12, "A 平移 (+5,-3) → B 孔心到 (35,37)，实 " + d);
            Assert(Math.Abs(HoleLink.OffsetMm(pp, d) - Math.Sqrt(34)) < 1e-9, "错位量 = √(5²+3²)mm");

            // A 绕 Z 转 90°（原点为轴）：孔心 (30,40) → (-40,30)
            var aRot = Transform.Frame(new V3(0, 0, 0), new V3(0, 1, 0), new V3(-1, 0, 0), new V3(0, 0, 1));
            Assert(HoleLink.Solve(aRot, rc, ra, bPlace, pp, pn, out d, out why), "A 旋转后能求出孔心");
            Assert((d - new V3(-0.04, 0.03, 0)).Length < 1e-12, "A 绕 Z 转 90° → 孔心 (-40,30)，实 " + d);

            // B 自己也挪了（平移 +10 x）：要换回 B 的局部坐标
            var bMoved = Transform.Frame(new V3(0.01, 0, 0.03), new V3(1, 0, 0), new V3(0, 1, 0), new V3(0, 0, 1));
            Assert(HoleLink.Solve(aPlace, rc, ra, bMoved, pp, pn, out d, out why), "B 平移后能求出孔心");
            Assert((d - new V3(0.02, 0.04, 0)).Length < 1e-12, "B 平移 +10 → B 局部孔心 (20,40)，实 " + d);

            // A 立起来（轴线与打孔面平行）：说清楚为什么同步不了
            var aTilt = Transform.Frame(new V3(0, 0, 0), new V3(1, 0, 0), new V3(0, 0, 1), new V3(0, -1, 0));
            Assert(!HoleLink.Solve(aTilt, rc, ra, bPlace, pp, pn, out d, out why) && why.Contains("平行"), "轴线与打孔面平行时不硬算：" + why);
            Assert(!HoleLink.Solve(null, rc, ra, bPlace, pp, pn, out d, out why), "读不到实例位置时不硬算");
        }
    }
}
