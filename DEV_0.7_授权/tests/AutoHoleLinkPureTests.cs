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
            NativeLinkPure();
            HoleSpotsPure();
            FaceShapePure();
            MergeArcsPure();
        }

        // 0.8.5 孔心落没落在面的材料上：L 形面（缺一角）、面上的孔口（算在面上，交给"已经有孔"去说）、读不到面片时不拦
        static void MergeArcsPure(){
            var z = new V3(0, 0, 1);
            var half1 = new CircleCandidate { Center = new V3(0.01, 0.01, 0), Axis = z, RadiusMm = 3.3, Closed = false };
            var half2 = new CircleCandidate { Center = new V3(0.01, 0.01, 0), Axis = z, RadiusMm = 3.3, Closed = false };
            var slotEnd = new CircleCandidate { Center = new V3(0.05, 0.01, 0), Axis = z, RadiusMm = 4.5, Closed = false };
            var full = new CircleCandidate { Center = new V3(0.08, 0.01, 0), Axis = z, RadiusMm = 3.3 };
            var m = HoleScan.MergeArcs(new List<CircleCandidate>{ half1, half2, slotEnd, full });
            Assert(m.Count == 3 && m.Count(c => c.Closed) == 2, "两段半圆拼成一个整圆孔口，腰孔一头的单段圆弧不算（实 " + m.Count + "）");
            var holes = HoleScan.FromCircles(new List<CircleCandidate>{ half1, half2 }, new List<CircleCandidate>{ half1, half2 });
            Assert(holes.Count == 1 && Math.Abs(holes[0].DiameterMm - 6.6) < 1e-9, "导入件拆成两段的孔口照样认出 Φ6.6");
            // 0.9.1：两段半圆的圆心差 3µm、半径差 0.002mm（导入件常见的误差）也要拼回来；相距 0.1mm 的两段不拼
            var near = new CircleCandidate { Center = new V3(0.010003, 0.01, 0), Axis = z, RadiusMm = 3.302, Closed = false };
            Assert(HoleScan.MergeArcs(new List<CircleCandidate>{ half1, near }).Count(c => c.Closed) == 1, "圆心差 3µm 的两段半圆拼成一个孔口");
            var far = new CircleCandidate { Center = new V3(0.0101, 0.01, 0), Axis = z, RadiusMm = 3.3, Closed = false };
            Assert(HoleScan.MergeArcs(new List<CircleCandidate>{ half1, far }).Count(c => c.Closed) == 0, "圆心差 0.1mm 的两段圆弧不拼");
        }

        static void FaceShapePure(){
            // L 形：0..0.1 × 0..0.05 加 0..0.05 × 0.05..0.1（右上角 0.05..0.1 × 0.05..0.1 空着）
            var t = new double[]{ 0,0, 0.1,0, 0.1,0.05,   0,0, 0.1,0.05, 0,0.05,   0,0.05, 0.05,0.05, 0.05,0.1,   0,0.05, 0.05,0.1, 0,0.1 };
            var c = new List<double[]>{ new[]{ 0.02, 0.02, 0.0033 }, new[]{ 0.09, 0.09, 0.03 } };
            Assert(FaceShape.Inside(t, c, 0.08, 0.02, 0.001), "L 形面：下半截里的孔心在面上");
            Assert(FaceShape.Inside(t, c, 0.02, 0.08, 0.001), "L 形面：左上那一块里的孔心在面上");
            Assert(!FaceShape.Inside(t, c, 0.08, 0.08, 0.001), "L 形面：缺掉的右上角不在面上（原来按包围盒算在面上，CAD 报含糊错误）");
            Assert(FaceShape.Inside(t, c, 0.1005, 0.02, 0.001), "离边 0.5mm（容差 1mm 以内）算在面上");
            Assert(FaceShape.Inside(t, c, 0.02, 0.02, 0.001), "落在面上已有的孔口圆心上：算在面上（后面报「本来就有孔」）");
            Assert(!FaceShape.Inside(t, c, 0.07, 0.07, 0.001), "大圆窗口里不在圆心的位置：不在面上");
            Assert(FaceShape.Inside(null, null, 5, 5, 0.001), "读不到面片：不拦");
        }

        // 0.7.10 打孔提速：实体圆边读一遍、孔位在内存里比（HoleSpots），判定口径必须和原来逐个读边的 ExistingHoleAt 一样。
        static void HoleSpotsPure(){
            var n = new V3(0, 0, 1);
            var s = new HoleSpots();
            s.Add(new V3(0.03, 0.04, 0.01), new V3(0, 0, -1), 0.0033);      // 竖孔孔口（轴线反向也算平行）
            s.Add(new V3(0.08, 0.04, 0.005), new V3(1, 0, 0), 0.0025);      // 横孔：圆心恰好在 (80,40) 的轴线上，但不是竖孔
            s.Add(new V3(0.05, 0.05, 0), new V3(0, 0, 1), 0);                // 半径 0 的不收
            double d;
            Assert(s.Count == 2, "半径 0 的圆不收（实 " + s.Count + "）");
            Assert(s.At(new V3(0.03, 0.04, 0.01), n, out d) && Math.Abs(d - 6.6) < 1e-9, "孔口在打孔面上的竖孔认出来，给出孔口直径 6.6");
            Assert(s.At(new V3(0.0304, 0.04, 0.0101), n, out d), "径向差 0.4mm、轴向差 0.1mm 仍算同一个孔位（容差 0.5 / 0.2mm）");
            Assert(!s.At(new V3(0.0306, 0.04, 0.01), n, out d), "径向差 0.6mm 不算");
            Assert(!s.At(new V3(0.03, 0.04, 0), n, out d), "0.8.5：同轴但孔口不在打孔面上（反面打的盲孔）不算已经有孔（现场截图）");
            Assert(!s.At(new V3(0.03, 0.04, 0.0103), n, out d), "轴向差 0.3mm 不算");
            Assert(!s.At(new V3(0.08, 0.04, 0), n, out d), "横孔不算这里已经有孔（围栏 FenceHoleE2E 第 4 条）");
            Assert(s.At(new V3(0.08, 0.04, 0.005), new V3(1, 0, 0), out d), "沿 x 打孔、孔心就在横孔孔口上时就算");
            s.Plan(new V3(0.1, 0.1, 0), n, 5);
            Assert(s.At(new V3(0.1, 0.1, 0), n, out d) && Math.Abs(d - 5) < 1e-9, "这批准备打的孔：同一位置后面的请求算已经有孔");
            Assert(!new HoleSpots().At(new V3(0, 0, 0), n, out d) && d == 0, "空表：没有孔");
            // 0.8.5 盲孔打穿：孔口 z=0.01、向下 15 深；10 厚的板在 z=0 处有出口圆 → 10mm 处穿出；材料够厚（孔底圆在 15mm）→ 0
            var b = new HoleSpots();
            b.Add(new V3(0.05, 0.03, 0.01), new V3(0, 0, 1), 0.0033); b.Add(new V3(0.05, 0.03, 0.0), new V3(0, 0, 1), 0.0033);
            Assert(Math.Abs(b.ExitDepth(new V3(0.05, 0.03, 0.01), n, 0.0033, 0.015) - 0.01) < 1e-9, "15 深的盲孔在 10mm 处穿出");
            var c2 = new HoleSpots();
            c2.Add(new V3(0.05, 0.03, 0.03), new V3(0, 0, 1), 0.0033); c2.Add(new V3(0.05, 0.03, 0.015), new V3(0, 0, 1), 0.0033);
            Assert(c2.ExitDepth(new V3(0.05, 0.03, 0.03), n, 0.0033, 0.015) == 0, "孔底圆正好在 15mm（平底盲孔）不算穿出");
            Assert(b.ExitDepth(new V3(0.05, 0.03, 0.01), n, 0.0025, 0.015) == 0, "大小不一样的同轴圆不算");

            // 0.7.12 这批里互相重叠的孔（腰孔两头当成两个参考孔）
            var o = new HoleSpots();
            o.Plan(new V3(-0.692, 0, 0), n, 20);
            Assert(o.Overlaps(new V3(-0.688, 0, 0), n, 20, out d) && Math.Abs(d - 20) < 1e-9, "两个 Φ20 心距 4mm：重叠（副本 -4.par）");
            Assert(!o.Overlaps(new V3(-0.671, 0, 0), n, 20, out d), "两个 Φ20 心距 21mm：不重叠");
            Assert(!o.Overlaps(new V3(-0.692, 0, 0.003), n, 20, out d), "同一个孔位不算重叠（那是「已经有孔」）");
            Assert(!o.Overlaps(new V3(-0.688, 0, 0), new V3(1, 0, 0), 20, out d), "方向不平行的不比");
            s.Add(new V3(0.2, 0.2, 0), n, 0.01);
            Assert(!s.Overlaps(new V3(0.205, 0.2, 0), n, 10, out d), "零件上已有的孔不算重叠（只比这批准备打的）");

            // 配对：输入孔 / 拆出来的轮廓按孔心一一配到孔位上
            var found = new List<V3?> { new V3(0.02, 0, 0), null, new V3(0.01, 0, 0), new V3(0.0101, 0, 0) };
            var want = new List<V3> { new V3(0.01, 0, 0), new V3(0.02, 0, 0), new V3(0.03, 0, 0) };
            var pick = HoleSpots.Assign(found, want, HoleSpots.RadialTolM);
            Assert(pick[0] == 2 && pick[1] == 0 && pick[2] == -1, "按最近配、读不出的跳过、超出容差的不配（实 " + string.Join(",", pick.Select(x => x.ToString()).ToArray()) + "）");
            pick = HoleSpots.Assign(found, new List<V3> { new V3(0.01, 0, 0), new V3(0.01, 0, 0) }, HoleSpots.RadialTolM);
            Assert(pick[0] == 2 && pick[1] == 3, "一个孔心只配一次：第二个孔位拿次近的");
        }

        // 0.7.8 天工原生关联的纯逻辑：子装配矩阵串接、认实例、在零件间复制来的圆边里挑"就是这个孔"的那条。
        static void NativeLinkPure(){
            // 串接：子装配平移 (10,0,0)mm，里面的零件再平移 (0,20,0)mm 并绕 z 转 90°
            var outer = Transform.Frame(new V3(0.01, 0, 0), new V3(1, 0, 0), new V3(0, 1, 0), new V3(0, 0, 1));
            var inner = Transform.Frame(new V3(0, 0.02, 0), new V3(0, 1, 0), new V3(-1, 0, 0), new V3(0, 0, 1));
            var both = NativeLinkMath.Compose(outer, inner);
            var p = both.Point(new V3(0.001, 0, 0));      // 零件局部 x=1mm → 转 90° 成 y=1mm → +(0,20)+(10,0)
            Assert((p - new V3(0.01, 0.021, 0)).Length < 1e-12, "子装配矩阵串接：先内后外");
            Assert(NativeLinkMath.Same(both, NativeLinkMath.Compose(outer, inner)), "同一个变换 Same=true");
            Assert(!NativeLinkMath.Same(both, outer), "不同变换 Same=false");
            Assert(NativeLinkMath.Same(NativeLinkMath.Compose(null, inner), inner), "没有父级时就是自己");

            // 挑圆边：打孔面法向 z，要打的孔心 (30,40,0)mm
            var want = new V3(0.03, 0.04, 0); var n = new V3(0, 0, 1);
            var circles = new List<NativeLinkMath.Circle> {
                new NativeLinkMath.Circle(new V3(0.07, 0.04, -0.02), new V3(0, 0, 1), 0.005),    // 另一个孔
                new NativeLinkMath.Circle(new V3(0.03, 0.04, -0.02), new V3(0, 0, -1), 0.005),   // 就是它（轴线反向也算平行，在别的高度也行：约束的是投影圆心）
                new NativeLinkMath.Circle(new V3(0.03, 0.04, -0.01), new V3(1, 0, 0), 0.005),    // 圆心对得上但轴线不平行：不是孔口圆
            };
            double off;
            Assert(NativeLinkMath.Match(circles, want, n, out off) == 1 && off < 1e-12, "挑中同轴的那条圆边");
            Assert(NativeLinkMath.Match(circles, new V3(0.03, 0.0401, 0), n, out off) == -1, "差 0.1mm 不认（容差 0.05mm）");
            Assert(NativeLinkMath.Match(circles, new V3(0.03, 0.04002, 0), n, out off) == 1, "差 0.02mm 认（浮点 / 建模误差）");
            circles.Add(new NativeLinkMath.Circle(new V3(0.03, 0.04001, 0.0), new V3(0, 0, 1), 0.0055));
            Assert(NativeLinkMath.Match(circles, want, n, out off) == 1, "几条都对得上取投影误差最小的");
            Assert(NativeLinkMath.Match(null, want, n, out off) == -1, "没有圆边 → -1");
            // 斜孔不走原生关联
            Assert(NativeLinkMath.Perpendicular(new V3(0, 0, -1), n), "孔轴线 ∥ 面法向（反向）：可以");
            Assert(!NativeLinkMath.Perpendicular(new V3(0, Math.Sin(0.05), Math.Cos(0.05)), n), "斜 3° 的孔：不走原生关联");
            // 0.7.11 零件的链接属于哪个装配：路径比较
            Assert(NativeLinkMath.SamePath(@"H:\桌面\YF25.07\焊接台车\YF25.07G01-L1-M30-02.asm", @"h:/桌面/YF25.07/焊接台车/yf25.07g01-l1-m30-02.ASM "), "同一个装配：大小写、斜杠、空白不算");
            Assert(!NativeLinkMath.SamePath(@"H:\桌面\YF25.07\焊接台车\YF25.07G01-L1-M30-02.asm", @"H:\桌面\YF25.07\焊接台车\YF25.07底部水冷兼容短刀模组堆叠小车.asm"), "别的装配：不同");
            Assert(!NativeLinkMath.SamePath("", @"H:\a.asm") && !NativeLinkMath.SamePath(null, null), "空路径不算同一个");
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
            var near = new List<HoleRecord>{ Rec("A", 0, 20, 8.5), Rec("C", 22, 42, 3.3) };
            Assert(HoleCheck.Run(near, null).Count(i => i.Kind == HoleIssueKind.WrongSpec) == 1, "间隙 2mm 仍算一对：M10 底孔配 M4 底孔 -> 配错孔");
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

            // ②b 完整的螺纹连接（0.10.1 焊接台车实测）：A 板 M6 过孔 z0~10、C 板 M6 螺纹孔 z10~20，
            // 螺钉到 C 就到头了，C 下面贴着的底板 D（z20~30）不需要孔 —— 原来一个螺钉报一条「D 漏打孔」
            var joint = new List<HoleRecord>{ Rec("A", 0, 10, 6.6), Rec("C", 10, 20, 4.92) };
            var idxBase = new PartRayIndex(); idxBase.Add(Slab("D", 20, 30));
            var allJ = new List<string>{ "A", "C", "D" };
            Assert(HoleCheck.CompleteJoint(joint), "M6 过孔 + M6 螺纹孔 = 完整连接");
            Assert(HoleCheck.Run(joint, null, false, allJ, idxBase).Count(i => i.Kind == HoleIssueKind.MissingHole) == 0, "螺纹孔下面贴着的底板不报漏打孔");
            var idxMid = new PartRayIndex(); idxMid.Add(Slab("D", 10, 20));
            var jointGap = new List<HoleRecord>{ Rec("A", 0, 10, 6.6), Rec("C", 20, 30, 4.92) };
            Assert(HoleCheck.Run(jointGap, null, false, allJ, idxMid).Count(i => i.Kind == HoleIssueKind.MissingHole) == 1, "夹在过孔和螺纹孔中间的板没孔：照样报");
            var twoClear = new List<HoleRecord>{ Rec("A", 0, 10, 6.6), Rec("C", 10, 20, 6.6) };
            Assert(!HoleCheck.CompleteJoint(twoClear), "两个过孔不是完整连接（还缺螺纹孔或螺母）");
            Assert(HoleCheck.Run(twoClear, null, false, allJ, idxBase).Count(i => i.Kind == HoleIssueKind.MissingHole) == 1, "两个过孔、下面贴着的 D 没孔：照样报");

            // ③ 孔里装了螺栓/销：轴线穿过螺栓杆是正常的，不报"螺栓上漏打孔"
            var idxBolt = new PartRayIndex(); idxBolt.Add(Slab("BOLT", 90, 130));   // 螺栓杆整段压在孔附近
            var shaftOk = new List<HoleRecord>{ new HoleRecord { PartName = "BOLT", DiameterMm = 6, Center = new V3(0, 0, 0.09), Axis = new V3(0, 0, -1) } };
            var allB = new List<string>{ "A", "BOLT" };
            Assert(HoleCheck.Run(lone, null, false, allB, idxBolt).Count == 1, "对照：不告诉它螺栓杆在哪时，会误报螺栓上漏打孔");
            Assert(HoleCheck.Run(lone, null, false, allB, idxBolt, shaftOk).Count == 0, "螺栓杆和孔同轴：不报漏打孔");
            var shaftOff = new List<HoleRecord>{ new HoleRecord { PartName = "BOLT", DiameterMm = 6, Center = new V3(0.02, 0, 0.09), Axis = new V3(0, 0, 1) } };
            Assert(HoleCheck.Run(lone, null, false, allB, idxBolt, shaftOff).Count == 1, "螺栓杆偏开 20mm（不是装在这个孔里）：照样报");
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

            // 0.8.4 孔位链接组：一种规格写规格 ×N，几种规格写总数 + 种数，带上跟随的参考零件
            var tap6 = new HoleSpec { Kind = HoleKind.Tapped, ThreadSize = "M6", HoleDiameter = 4.917, ThreadFull = true };
            var one = new List<KeyValuePair<HoleSpec, int>> { new KeyValuePair<HoleSpec, int>(tap6, 4) };
            Assert(HoleNaming.LinkGroup(one, new[]{ "支撑条" }) == "关联孔 M6 螺纹孔（螺纹全长） ×4（跟随 支撑条）", "链接组名：一种规格 + 参考零件：" + HoleNaming.LinkGroup(one, new[]{ "支撑条" }));
            Assert(HoleNaming.LinkGroup(one, new[]{ "支撑条", "支撑条", "" }) == "关联孔 M6 螺纹孔（螺纹全长） ×4（跟随 支撑条）", "参考零件重复 / 空的只算一次");
            var two = new List<KeyValuePair<HoleSpec, int>> { new KeyValuePair<HoleSpec, int>(tap6, 4), new KeyValuePair<HoleSpec, int>(cb, 2) };
            Assert(HoleNaming.LinkGroup(two, new[]{ "A", "B" }) == "关联孔 6 个（2 种规格，跟随 A 等 2 个零件）", "两种规格两个参考零件：" + HoleNaming.LinkGroup(two, new[]{ "A", "B" }));
            Assert(HoleNaming.LinkGroup(two, new string[0]) == "关联孔 6 个（2 种规格）", "没有参考零件名：" + HoleNaming.LinkGroup(two, new string[0]));
            Assert(HoleNaming.LinkGroup(one, null) == "关联孔 M6 螺纹孔（螺纹全长） ×4", "参考零件为 null 也不出错");
            Assert(HoleNaming.LinkGroup(one, new[]{ "支撑条" }, "顶面") == "关联孔 M6 螺纹孔（螺纹全长） ×4 · 顶面（跟随 支撑条）", "0.8.5 组名带面的方位：" + HoleNaming.LinkGroup(one, new[]{ "支撑条" }, "顶面"));
            Assert(HoleNaming.LinkGroup(two, new[]{ "A" }, "左侧面") == "关联孔 6 个 · 左侧面（2 种规格，跟随 A）", "几种规格也带方位：" + HoleNaming.LinkGroup(two, new[]{ "A" }, "左侧面"));
            Assert(HoleNaming.SideIn("关联孔 M6 螺纹孔 ×4 · 左侧面（跟随 A） (2)") == "左侧面" && HoleNaming.SideIn("关联孔 M6 ×4（跟随 A）") == "", "改孔型重起名时认出组名里的方位");
            Assert(HoleNaming.SideOf(new V3(0, 0, -1)) == "底面" && HoleNaming.SideOf(new V3(1, 0, 0.01)) == "右侧面" && HoleNaming.SideOf(new V3(0, -1, 0)) == "前面" && HoleNaming.SideOf(new V3(0.7, 0.7, 0)) == "斜面", "方位：底面 / 右侧面 / 前面 / 斜面");
            var lng = new List<KeyValuePair<HoleSpec, int>> { new KeyValuePair<HoleSpec, int>(longSpec, 9) };
            Assert(HoleNaming.LinkGroup(lng, new[]{ "YF25.07G01-L1-M30-02-23-很长很长的零件名" }).Length <= HoleNaming.MaxLength, "链接组名长度封顶");
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
