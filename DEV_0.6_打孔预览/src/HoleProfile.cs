using System;
using System.Collections.Generic;
using System.Globalization;

namespace TianGongCadSuite {
    // 半剖面上的一个点：R = 到孔轴的距离（毫米），Z = 离孔口的深度（毫米，向材料内部为正）。
    public struct HolePt {
        public double R, Z;
        public HolePt(double r, double z){ R = r; Z = z; }
        public override string ToString(){ return "(" + R.ToString("0.###", CultureInfo.InvariantCulture) + "," + Z.ToString("0.###", CultureInfo.InvariantCulture) + ")"; }
    }

    // 一个孔的"形状定义"。
    //
    // 这是预览和实际打孔之间唯一的一份几何定义：2D 剖面、3D 参考、以及 CAD 里真正切出来的
    // 形状，必须对得上。所以这里只有纯计算，不碰 CAD 也不碰 WinForms，好让 tests 直接断言。
    //
    // 尺寸的来源分两类，都在注释里写明：
    //   [CAD 实测] 由真机上量切除体积反推出来的（见 tools/ChamferProbe.cs、tests/AutoHoleTests.cs）
    //   [表值]     取自 HoleMatcher.Table / HoleSpec 的输入
    public sealed class HoleShape {
        public HoleSpec Spec;                 // 原始输入（可能为 null：还没点参考孔时画默认形状）

        public HoleKind Kind;
        public bool Tapped;                   // 螺纹孔：实体孔按内小径，螺纹是装饰螺纹
        public string ThreadSize = "";
        public double ThreadMajorMm;          // 装饰螺纹公称直径（仅 Tapped 有意义）

        public bool Through;
        public double DepthMm;                // 盲孔总深（含钻尖）；贯通孔等于参考板厚
        public double ThicknessMm;            // 参考板厚（界面会写明是"示意"）

        public double HoleDiameterMm, HoleRadiusMm;   // 主孔（圆柱段）直径
        public double MouthDiameterMm, MouthRadiusMm; // 孔口最大直径（含沉孔/锥孔/倒角）

        public double CounterboreDiaMm, CounterboreDepMm;
        public double CountersinkDiaMm, CountersinkAngleDeg, CountersinkDepMm;

        public bool Chamfer;
        public double ChamferSetbackMm;       // 孔口径向增量
        public double ChamferAngleDeg;        // 与孔轴的夹角
        public double ChamferDepMm;           // 轴向深度 = Setback / tan(Angle)   [CAD 实测]
        public double ChamferTopDiaMm;        // 倒角后的孔口直径

        public HoleBottom Bottom;
        public double BottomAngleDeg;
        public double TipHeightMm;            // V 型钻尖高度 = r / tan(角度/2)     [CAD 实测]

        // 右半轮廓：从孔口轴线上的点开始，沿孔壁走到孔底轴线上的点。
        // 镜像一下就是完整剖面；孔口和孔底都落在轴线上，所以首尾自动闭合。
        public readonly List<HolePt> Wall = new List<HolePt>();

        public double VoidDepthMm { get { return Through ? ThicknessMm : DepthMm; } }

        // 这个规格本身就不成立（例如沉孔直径 ≤ 孔径）。CAD 那边同样会拒绝这种参数，
        // 但预览绝不能照着画一个看着挺像、其实打不出来的形状 —— 视图会改成显示这句话。
        public string Problem = "";

        // 这个孔的"实体孔"是不是比公称直径小（螺纹孔按内小径建模）—— 3D 参考里要画装饰螺纹
        public bool CosmeticThread { get { return Tapped && ThreadMajorMm > HoleDiameterMm + 0.01; } }

        public string KindName { get { return HoleSpec.KindName(Kind); } }

        // 一句话说清这个孔长什么样（2D 视图的标题行用）
        public string Summary {
            get {
                string s = KindName + " Φ" + N(HoleDiameterMm);
                if (Kind == HoleKind.Counterbore) s += "，沉孔 Φ" + N(CounterboreDiaMm) + " 深 " + N(CounterboreDepMm);
                if (Kind == HoleKind.Countersink) s += "，锥孔 Φ" + N(CountersinkDiaMm) + " " + N(CountersinkAngleDeg) + "°";
                if (Tapped && ThreadSize.Length > 0) s += "（" + ThreadSize + " 装饰螺纹）";
                s += Through ? "，贯通" : "，深 " + N(DepthMm);
                if (!Through && Bottom == HoleBottom.VBottom) s += "，V 型底 " + N(BottomAngleDeg) + "°";
                else if (!Through) s += "，平底";
                if (Chamfer) s += "，孔口倒角 " + N(ChamferSetbackMm) + "×" + N(ChamferAngleDeg) + "°";
                return s;
            }
        }
        static string N(double v){ return v.ToString("0.##", CultureInfo.InvariantCulture); }
    }

    public static class HoleShapeBuilder {
        // 孔口倒角的实测结论（tools/ChamferProbe.cs，2026-09-26 真机，天工CAD 225.03.00.165）：
        //   HoleData.SetStartChamfer(1, Setback, Angle) 里
        //     Setback = 孔口的**径向**增量（不是轴向深度）
        //     Angle   = 与孔轴的夹角（不是与端面的夹角）
        //   轴向深度 = Setback / tan(Angle)。
        //   证据：Φ6 通孔、板厚 10mm，量切除体积再减掉圆柱体积，得倒角环体积
        //     2/60°  实测 26.603 mm³，本式 26.602（另外三种解释分别给 90.4 / 24.6 / 79.8）
        //     2/30°  实测 79.808 mm³，本式 79.807
        //     3/60°  实测 65.297 mm³，本式 65.297
        //     45° 时四种解释同值，所以只有非 45° 才分得出来 —— 这也是必须真机量的原因。
        public static double ChamferDepthMm(double setbackMm, double angleDeg){
            if (setbackMm <= 0) return 0;
            double a = angleDeg;
            if (a < 1) a = 1; if (a > 89.9) a = 89.9;
            return setbackMm / Math.Tan(a * Math.PI / 180.0);
        }

        // V 型钻尖高度：角度是**钻尖夹角**（118° 标准麻花钻），所以半角是 angle/2。
        // [CAD 实测] tests/AutoHoleTests.cs：coneH = r / tan(59°)，切除体积精确吻合。
        public static double TipHeightMm(double radiusMm, double angleDeg){
            if (radiusMm <= 0) return 0;
            double a = angleDeg;
            if (a < 1) a = 1; if (a > 179) a = 179;
            return radiusMm / Math.Tan(a * Math.PI / 360.0);
        }

        // 锥形沉孔的锥座深度：锥角是**夹角**，从孔口直径收口到主孔直径。
        // [CAD 实测] tests/AutoHoleTests.cs：h = (R - r) / tan(45°)，切除体积吻合。
        public static double CountersinkDepthMm(double csRadiusMm, double holeRadiusMm, double angleDeg){
            if (csRadiusMm <= holeRadiusMm) return 0;
            double a = angleDeg;
            if (a < 1) a = 1; if (a > 179) a = 179;
            return (csRadiusMm - holeRadiusMm) / Math.Tan(a * Math.PI / 360.0);
        }

        // 参考板厚：界面里拿不到目标零件的真实厚度（那要额外走一遍 COM 去量），
        // 但预览必须有个材料块才看得出"孔是深是浅"。所以给一个按规格推出来的示意厚度，
        // 界面上会明确写"示意"。贯通孔的厚度不影响孔的形状，盲孔则保证孔底还有余料。
        public static double ReferenceThicknessMm(HoleSpec spec){
            if (spec == null) return 8.0;
            double t;
            if (!spec.Through) {
                t = spec.Depth + Math.Max(2.0, spec.Depth * 0.25);
            } else {
                t = Math.Max(3.0, spec.HoleDiameter * 1.6);
                if (spec.Kind == HoleKind.Counterbore) t = Math.Max(t, spec.CounterboreDepth * 1.6);
                if (spec.Kind == HoleKind.Countersink) t = Math.Max(t, spec.CountersinkDiameter * 0.5);
            }
            if (t > 60) t = 60;
            return Math.Round(t * 2.0, MidpointRounding.AwayFromZero) / 2.0;   // 取到 0.5
        }

        // "M6" -> 6；"M6x0.75" -> 6。解析不出来返回 0。
        public static double NominalMm(string size){
            if (string.IsNullOrEmpty(size)) return 0;
            var sb = new System.Text.StringBuilder();
            foreach (char c in size) {
                if (char.IsDigit(c) || c == '.') sb.Append(c);
                else if (sb.Length > 0) break;
            }
            double v;
            return double.TryParse(sb.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : 0;
        }

        static string F(double v){ return v.ToString("0.##", CultureInfo.InvariantCulture); }

        // 把规格变成可画的形状。thicknessMm <= 0 时自动取参考板厚。
        public static HoleShape Build(HoleSpec spec, double thicknessMm){
            if (spec == null) spec = new HoleSpec { Kind = HoleKind.Through, HoleDiameter = 6, Depth = 0 };
            var s = new HoleShape { Spec = spec, Kind = spec.Kind, Tapped = spec.Kind == HoleKind.Tapped };
            s.ThreadSize = spec.ThreadSize == null ? "" : spec.ThreadSize;
            s.Through = spec.Through;
            s.HoleDiameterMm = Math.Max(0.1, spec.HoleDiameter);
            s.HoleRadiusMm = s.HoleDiameterMm / 2.0;
            s.Bottom = spec.Bottom;
            s.BottomAngleDeg = spec.BottomAngle;
            s.ThicknessMm = thicknessMm > 0 ? thicknessMm : ReferenceThicknessMm(spec);
            s.DepthMm = s.Through ? s.ThicknessMm : Math.Max(0.1, spec.Depth);

            if (s.Tapped) {
                double nominal = NominalMm(s.ThreadSize);
                s.ThreadMajorMm = nominal > 0 ? nominal : s.HoleDiameterMm;
            }

            double r = s.HoleRadiusMm;
            // 孔口基准直径：沉孔用沉孔直径，锥沉用锥孔直径，其余就是主孔直径。
            double mouthR = r;
            if (s.Kind == HoleKind.Counterbore) {
                s.CounterboreDiaMm = spec.CounterboreDiameter;
                s.CounterboreDepMm = Math.Max(0, spec.CounterboreDepth);
                mouthR = Math.Max(r, spec.CounterboreDiameter / 2.0);
            } else if (s.Kind == HoleKind.Countersink) {
                s.CountersinkDiaMm = spec.CountersinkDiameter;
                s.CountersinkAngleDeg = spec.CountersinkAngle;
                mouthR = Math.Max(r, spec.CountersinkDiameter / 2.0);
                s.CountersinkDepMm = CountersinkDepthMm(mouthR, r, s.CountersinkAngleDeg);
            }

            // 孔口倒角：锥形沉孔本身就是一个锥座，CAD 那边也明确跳过倒角（BuildHoleData 里判了孔型），
            // 所以这里同样不叠加，免得预览画出一个实际不存在的倒角。
            if (spec.Chamfer && s.Kind != HoleKind.Countersink) {
                s.Chamfer = true;
                s.ChamferSetbackMm = Math.Max(0, spec.ChamferSetback);
                s.ChamferAngleDeg = spec.ChamferAngle;
                s.ChamferDepMm = ChamferDepthMm(s.ChamferSetbackMm, s.ChamferAngleDeg);
                s.ChamferTopDiaMm = 2.0 * (mouthR + s.ChamferSetbackMm);
            }
            s.MouthRadiusMm = mouthR + (s.Chamfer ? s.ChamferSetbackMm : 0);
            s.MouthDiameterMm = s.MouthRadiusMm * 2.0;

            // 沉孔/锥孔的直径必须大于主孔，否则两段套不起来（CAD 侧的 BuildHoleData 也会拒绝）。
            if (s.Kind == HoleKind.Counterbore && s.CounterboreDiaMm <= s.HoleDiameterMm + 1e-9)
                s.Problem = "沉孔直径 Φ" + F(s.CounterboreDiaMm) + " 必须大于孔径 Φ" + F(s.HoleDiameterMm);
            if (s.Kind == HoleKind.Countersink && s.CountersinkDiaMm <= s.HoleDiameterMm + 1e-9)
                s.Problem = "锥孔直径 Φ" + F(s.CountersinkDiaMm) + " 必须大于孔径 Φ" + F(s.HoleDiameterMm);

            if (!s.Through && s.Bottom == HoleBottom.VBottom)
                s.TipHeightMm = Math.Min(TipHeightMm(r, s.BottomAngleDeg), s.DepthMm);

            BuildWall(s, mouthR, r);
            return s;
        }

        // 右半轮廓，从孔口轴线走到孔底轴线。顺序就是画图顺序。
        static void BuildWall(HoleShape s, double mouthR, double r){
            var w = s.Wall;
            double zBottom = s.VoidDepthMm;
            w.Add(new HolePt(0, 0));
            if (s.Chamfer) {
                // 倒角：孔口径向长 Setback，轴向深 Setback/tan(Angle)
                w.Add(new HolePt(mouthR + s.ChamferSetbackMm, 0));
                w.Add(new HolePt(mouthR, Math.Min(s.ChamferDepMm, zBottom)));
            } else {
                w.Add(new HolePt(mouthR, 0));
            }
            if (s.Kind == HoleKind.Counterbore) {
                double d = Math.Min(s.CounterboreDepMm, zBottom);
                w.Add(new HolePt(s.CounterboreDiaMm / 2.0, d));
                w.Add(new HolePt(r, d));
            } else if (s.Kind == HoleKind.Countersink) {
                w.Add(new HolePt(r, Math.Min(s.CountersinkDepMm, zBottom)));
            }
            if (s.Through) {
                w.Add(new HolePt(r, zBottom));
                w.Add(new HolePt(0, zBottom));
            } else if (s.Bottom == HoleBottom.VBottom && s.TipHeightMm > 0) {
                w.Add(new HolePt(r, Math.Max(0, zBottom - s.TipHeightMm)));
                w.Add(new HolePt(0, zBottom));
            } else {
                w.Add(new HolePt(r, zBottom));
                w.Add(new HolePt(0, zBottom));
            }
        }
    }
}
