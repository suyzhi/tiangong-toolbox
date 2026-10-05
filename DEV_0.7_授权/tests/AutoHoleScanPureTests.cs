using System;
using System.Collections.Generic;
using System.Globalization;

namespace TianGongCadSuite {
    // 面扫描 / 沉孔识别 / 归类的纯逻辑回归。几何用例全部照抄真机量出来的数（tools/facescan-probe，
    // 2026-10-05，天工CAD 225.03.00.165）：100×100×20 的板上打了
    //   H1 圆柱沉孔 Φ11/Φ6.6（沉孔深 6，孔口在 z=+10，沉孔底 z=+4，出口 z=-10）
    //   H2 平孔 Φ5
    //   H3 锥形沉孔 Φ11 90°/Φ5（锥面与圆柱的交界圆在 z=+7）
    //   H4 孔口倒角 0.5×45° 的 Φ6.6 通孔（孔口圆 Φ7.6 在 z=+10，倒角下沿 Φ6.6 在 z=+9.5）
    //   H5 螺纹孔 M6（按内小径 Φ4.917 建模）
    //   BOSS 圆凸台 Φ20 高 5（底圆在 z=+10，顶圆在 z=+15）
    public static partial class AutoHoleTests {
        // 轴沿 -Z（真机顶面上的圆孔边读出来的就是 -Z）、轴沿 +Z（底面上的圆边是 +Z）
        static CircleCandidate Down(double xMm, double yMm, double zMm, double diaMm, string wall){
            return new CircleCandidate { Center = new V3(xMm/1000.0, yMm/1000.0, zMm/1000.0), Axis = new V3(0,0,-1), RadiusMm = diaMm/2, Wall = wall, Closed = true, Source = "探针" };
        }
        static CircleCandidate Up(double xMm, double yMm, double zMm, double diaMm){
            return Up(xMm, yMm, zMm, diaMm, "");
        }
        static CircleCandidate Up(double xMm, double yMm, double zMm, double diaMm, string wall){
            return new CircleCandidate { Center = new V3(xMm/1000.0, yMm/1000.0, zMm/1000.0), Axis = new V3(0,0,1), RadiusMm = diaMm/2, Wall = wall, Closed = true };
        }
        static List<CircleCandidate> Fixture(){
            return new List<CircleCandidate>{
                Down(20,20,10, 11.0,"Cylinder"), Down(20,20,4, 6.6,""), Up(20,20,-10, 6.6,""),      // H1 沉孔
                Down(50,20,10, 5.0,"Cylinder"), Up(50,20,-10, 5.0,""),                               // H2 平孔
                Down(80,20,10, 11.0,"Cone"), Down(80,20,7, 5.0,""), Up(80,20,-10, 5.0,""),           // H3 锥沉
                Down(20,50,10, 7.6,"Cone"), Down(20,50,9.5, 6.6,""), Up(20,50,-10, 6.6,""),          // H4 倒角孔
                Down(50,50,10, 4.917,"Cylinder"), Up(50,50,-10, 4.917,""),                           // H5 螺纹孔
                Down(80,80,10, 20.0,"Cylinder"), Up(80,80,15, 20.0,""),                              // 圆凸台
            };
        }

        public static void ScanPure(){
            var all = Fixture();

            // ---- 沉孔：孔口 Φ11，真正要配做的是下面那个 Φ6.6 ----
            var cbore = HoleScan.FromCircle(all[0], all);
            Assert(Math.Abs(cbore.DiameterMm - 6.6) < 1e-9, "圆柱沉孔 Φ11 取下面的孔径 Φ6.6（实 " + cbore.DiameterMm + "）");
            Assert(Math.Abs(cbore.MouthDiameterMm - 11.0) < 1e-9, "沉孔的孔口直径仍然是 Φ11");
            Assert(cbore.MouthKind == "圆柱沉孔", "沉孔措辞：圆柱沉孔");
            Assert(cbore.Stepped, "沉孔标记为「孔口比孔径大」");
            Assert(cbore.Note.Contains("Φ11") && cbore.Note.Contains("Φ6.6"), "沉孔说明写清孔口与下面的孔径：" + cbore.Note);

            // 沉孔底那个环形面上的两条同心圆，算出来都是 Φ6.6，去重后只剩一个
            var annulus = new List<CircleCandidate>{ all[0], Down(20,20,4, 11.0,"Cylinder"), all[1], all[2] };
            var ring = HoleScan.FromCircles(new[]{ annulus[1], annulus[2] }, all);
            Assert(ring.Count == 1, "沉孔底环形面上的外圆与内圆去重成一个孔（实 " + ring.Count + "）");
            Assert(Math.Abs(ring[0].DiameterMm - 6.6) < 1e-9, "去重后按 Φ6.6 配做");
            Assert(Math.Abs(ring[0].MouthDiameterMm - 11.0) < 1e-9, "去重后保留信息更全的孔口 Φ11");

            // ---- 锥形沉孔：交界圆 Φ5 才是孔径，锥面壁 ----
            var csink = HoleScan.FromCircle(all[5], all);
            Assert(Math.Abs(csink.DiameterMm - 5.0) < 1e-9, "锥形沉孔 Φ11/Φ5 取 Φ5");
            Assert(csink.MouthKind == "锥形沉孔", "锥沉措辞：锥形沉孔（半增量 ≥1mm）");

            // ---- 孔口倒角：孔口 Φ7.6，孔径 Φ6.6，措辞是"孔口倒角" ----
            var chamfer = HoleScan.FromCircle(all[8], all);
            Assert(Math.Abs(chamfer.DiameterMm - 6.6) < 1e-9, "倒角孔口 Φ7.6 取 Φ6.6");
            Assert(chamfer.MouthKind == "孔口倒角", "半增量只有 0.5mm，措辞是孔口倒角（不是沉孔）");

            // ---- 平孔 / 螺纹孔：没有更小的同轴圆，孔径不变 ----
            var plain = HoleScan.FromCircle(all[3], all);   // all[3] = H2 平孔 Φ5 的孔口
            Assert(all[3].RadiusMm > 2.4 && all[3].RadiusMm < 2.6, "夹具下标没串（H2 平孔 Φ5）");
            Assert(Math.Abs(plain.DiameterMm - 5.0) < 1e-9 && !plain.Stepped, "平孔 Φ5 不变");
            var tappedCircle = all[11];                     // all[11] = H5 螺纹孔 Φ4.917 的孔口
            Assert(Math.Abs(tappedCircle.DiameterMm - 4.917) < 1e-9, "夹具下标没串（H5 螺纹孔 Φ4.917）");
            var tapped = HoleScan.FromCircle(tappedCircle, all);
            Assert(Math.Abs(tapped.DiameterMm - 4.917) < 1e-9 && !tapped.Stepped, "螺纹孔按内小径 Φ4.917，不误判成沉孔");

            // ---- 圆凸台：只有上下两条同径圆，不该"变小"（真正的排除在 CAD 侧按壁面方位判） ----
            var bossCircle = all[13];                        // all[13] = 凸台底圆 Φ20
            Assert(Math.Abs(bossCircle.DiameterMm - 20.0) < 1e-9, "夹具下标没串（凸台底圆 Φ20）");
            var boss = HoleScan.FromCircle(bossCircle, all);
            Assert(Math.Abs(boss.DiameterMm - 20.0) < 1e-9 && !boss.Stepped, "凸台底圆不会被认成沉孔口");

            // ---- 同轴判据：轴线反号也要认得出同轴（顶面圆 -Z、底面圆 +Z） ----
            Assert(HoleScan.Parallel(new V3(0,0,1), new V3(0,0,-1)), "轴向反号仍算同轴");
            CircleCandidate inner;
            Assert(HoleScan.InnerBelow(all[0], all, out inner), "沉孔能找到下面的孔");
            Assert(Math.Abs(inner.RadiusMm - 3.3) < 1e-9, "找到的是 Φ6.6 那个圆");
            // 偏心的同轴候选不能算（径向 0.1mm > 0.05mm 容差）
            var offset = Fixture(); offset.Add(Down(20.1, 20, 4, 6.6,""));
            var withOffset = HoleScan.FromCircle(offset[0], offset);
            Assert(Math.Abs(withOffset.DiameterMm - 6.6) < 1e-9, "偏心 0.1mm 的圆不参与同轴匹配（仍然取到正下方那个）");
            var faraway = new List<CircleCandidate>{ Down(20,20,10, 11.0,"Cylinder"), Down(30,20,-10, 3.0,"") };
            Assert(Math.Abs(HoleScan.FromCircle(faraway[0], faraway).DiameterMm - 11.0) < 1e-9, "不同轴的更小圆不会被当成「下面的孔」");

            // ---- 归类：按配做孔径分组 ----
            var mouths = new List<CircleCandidate>{
                Down(20,20,10, 11.0,"Cylinder"),   // 沉孔 -> 6.6
                Down(60,20,10, 6.6,"Cylinder"),    // 平孔 6.6
                Down(70,20,10, 6.6,"Cylinder"),    // 平孔 6.6
                Down(80,20,10, 5.0,"Cylinder"),    // 平孔 5.0
            };
            var holes = HoleScan.FromCircles(mouths, all);
            Assert(holes.Count == 4, "四个孔口识别成四个孔（实 " + holes.Count + "）");
            var groups = HoleScan.GroupByDiameter(holes);
            Assert(groups.Count == 2, "归成 2 类（Φ6.6 / Φ5）（实 " + groups.Count + "）");
            Assert(groups[0].Count == 3 && groups[1].Count == 1, "Φ6.6 那类 3 个（含沉孔那个）、Φ5 那类 1 个");
            Assert(HoleScan.MouthSummary(groups[0]).Contains("圆柱沉孔") && HoleScan.MouthSummary(groups[0]).Contains("Φ11"),
                   "Φ6.6 那一类的说明里写明「其中 1 个是圆柱沉孔（孔口 Φ11）」：" + HoleScan.MouthSummary(groups[0]));
            Assert(HoleScan.MouthSummary(groups[1]) == "", "Φ5 那类没有沉孔，说明为空");
            Assert(HoleScan.StatusLine(holes).Contains("Φ6.6 ×3"), "扫描结论一行里带每类的个数：" + HoleScan.StatusLine(holes));

            // ---- 归类容差：M6 过孔 6.6 与 M8 螺纹内小径 6.647 只差 0.047mm，**不能并成一组** ----
            // 注意这两个孔要放在"没有别的同轴圆"的位置上：Φ6.647 若与沉孔的 Φ6.6 同轴，
            // 会被规则当成"孔口下面更小的孔"而取成 6.6（这是对的——同轴的两个直径本来就是同一个孔）。
            var near = new List<ScannedHole>{ HoleScan.FromCircle(Down(10,10,10, 6.6,"Cylinder"), all), HoleScan.FromCircle(Down(60,30,10, 6.647,"Cylinder"), all) };
            Assert(Math.Abs(HoleScan.FromCircle(Down(60,30,10, 6.647,"Cylinder"), all).DiameterMm - 6.647) < 1e-9, "孤立的 Φ6.647 保持原样（附近没有同轴小圆）");
            Assert(HoleScan.GroupByDiameter(near).Count == 2, "Φ6.6 与 Φ6.647 不能并成一组（否则 M6 过孔会被配成 M8）");
            // 但同一个孔径的重复识别要去重（例如同一个孔口从两张面上各扫到一次）
            var dup = new List<ScannedHole>{ HoleScan.FromCircle(Down(10,10,10, 6.6,"Cylinder"), all), HoleScan.FromCircle(Up(10,10,-10, 6.6,""), all) };
            Assert(HoleScan.Dedupe(dup).Count == 1, "同轴线同径的重复识别去重成一个");

            // ---- 圆弧不算孔口 ----
            var arc = Down(10,10,10, 6.6,"Cylinder"); arc.Closed = false;
            Assert(HoleScan.FromCircle(arc, all) == null, "圆弧（非整圆）不当孔口");

            // ---- 面片上的圆口一条都认不出来时，状态行给的是"没有识别到孔" ----
            Assert(HoleScan.StatusLine(new List<ScannedHole>()).Contains("没有识别到孔"), "空结果的状态行文案");
        }
    }
}
