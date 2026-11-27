using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace TianGongCadSuite {
    // 一键出图（0.11.1）的公差 / 形位公差 / 粗糙度规矩。纯逻辑，tests/pure 能跑。
    //
    // 规矩全部来自 2026-11-03 的公差普查（tools/drawing-probe/DftSurvey.cs，734 张公司工程图、615 张零件图页；
    // 统计脚本 artifacts/drawing-bench/tolstats*.py，结论记在 DRAWING.md「公差和粗糙度」）：
    //   * 带公差的孔几乎都是定位销孔：对照三维只算光孔（没螺纹、没沉孔），带公差的 Ø4 59%、Ø5 55%、Ø6 71%、Ø8 68%
    //     （Ø10 45%、Ø3 22% 不到一半，不加）；写法几乎都是 H7 或 H7 的数值（Ø6 +0.012/0）。过孔 Ø4.5 / 5.5 / 6.6 / 9 / 11 几乎是 0。
    //   * 带公差的小孔 76% 在孔标注正下方紧贴一个位置度框「⌖0.03 / ⌖Ø0.03」（公司模板图框外就有「⌖ Ø0.03」样例）。
    //   * 基准：只有 27% 带公差孔的图页画了基准，所以插件不自动加基准，也就不加要基准的平行度 / 垂直度。
    //   * 粗糙度：80% 的零件图页不画粗糙度符号，靠模板右上角「其余 Ra6.3」。有定位销孔公差的图页 34% 画了粗糙度，几乎都是 Ra3.2、
    //     画在安装面上 —— 依据偏弱，插件只给销孔开口的那个面标一个 Ra3.2，并在「需要人工确认」里说清楚。
    // 单零件智能公差子规则开关集合（纯逻辑，镜像 DrawingOptions 公差子项）
    public sealed class ToleranceRuleSet {
        public bool PinHoles = true;
        public bool Bearings = true;
        public bool GuideRails = true;
        public bool DatumsAndGdt = true;
        public bool Roughness = true;
    }

    // 导轨安装孔排识别结果（如 5×M5 均布 60mm 间距的线性导轨安装行）
    public sealed class GuideRailMatch {
        public bool HorizontalRow;     // true = 孔排沿水平 X 方向（同 Y），false = 沿竖直 Y 方向（同 X）
        public double LineCoord;       // 孔排中心坐标（视图坐标，米）
        public double SpanStart;       // 孔排起始位置（米）
        public double SpanEnd;         // 孔排结束位置（米）
        public int HoleCount;          // 孔数（≥3）
        public double PitchMm;         // 标准导轨孔距（20/25/30/40/60/80 mm）
    }

    public static class DrawingTolerance {
        public const string PinFit = "H7";
        public const string BearingBoreFit = "H7";
        public const string BearingShaftFit = "k6";
        public const string GuidePinShaftFit = "g6";
        public const string PositionFrame = "%PO%VB%DI0.03";          // 天工形位公差框编码：%PO 位置度、%VB 分隔、%DI 直径符号
        public const string PositionFrameDatumA = "%PO%VB%DI0.03%VBA"; // 带基准 A 的定位销位置度
        public const string FlatnessFrame = "%FL%VB0.02";             // 平面度 0.02（导轨安装面）
        public const string ParallelismFrameA = "%PR%VB0.03%VBA";     // 平行度 0.03 A（轴承座端面/安装面对面）
        public const string PerpendicularityDiaFrameA = "%PP%VB%DI0.03%VBA"; // 垂直度 Ø0.03 A（轴承孔轴线对基准面 A）
        public const string PerpendicularityFrameA = "%PP%VB0.02%VBA"; // 垂直度 0.02 A（导轨靠山台阶面对安装面 A）
        public const string CoaxialityFrameA = "%CO%VB%DI0.02%VBA";   // 同轴度 Ø0.02 A（轴承轴颈对主轴基准 A）
        public const string HoleTableCoaxialityFrame = "%CO%VB%DI0.03"; // 孔表销孔同轴度 Ø0.03（无基准）
        public const string HoleTableCoaxialityFrameA = "%CO%VB%DI0.03%VBA"; // 孔表销孔同轴度 Ø0.03 A（带基准 A）
        public const string MatingRoughness = "3.2";
        public const string BearingRoughness = "1.6";

        // 定位销孔的直径（毫米）：普查里没螺纹、没沉孔的这些直径的孔，多数标了公差（见 DRAWING.md 的表）。
        // Ø5 光孔 55%（21/38）刚过一半、留出集里 5 张人没标的图因此被加了 H7；用户 2026-11-03 看过这个代价，定了保留
        public static readonly double[] PinDiameters = { 4, 5, 6, 8 };

        // 常见深沟球轴承 / 衬套标准外径（轴承孔直径，毫米）
        public static readonly double[] BearingBoreDiameters = { 16, 19, 22, 24, 26, 28, 30, 32, 35, 37, 40, 42, 47, 52, 55, 62, 68, 72, 80, 85, 90, 100 };

        // 常见轴承内径（轴颈直径，毫米）
        public static readonly double[] BearingJournalDiameters = { 8, 10, 12, 15, 17, 20, 25, 30, 35, 40, 45, 50 };

        // 常见圆柱定位销外径（毫米）
        public static readonly double[] GuidePinDiameters = { 6, 8, 10, 12, 16, 20 };

        // 常见直线导轨标准安装孔距（MGN/HGH/EGR 15~35，毫米）
        public static readonly double[] GuideRailPitches = { 20, 25, 30, 40, 60, 80 };

        // 一个孔要不要当定位销孔加 H7：真的孔（不是轴 / 凸台）、没螺纹、没沉孔、直径是销径
        public static bool IsPinHole(double diameterMm, bool isHole, bool threaded, bool counterbore){
            if (!isHole || threaded || counterbore) return false;
            foreach (var d in PinDiameters) if (Math.Abs(d - diameterMm) < 0.005) return true;
            return false;
        }

        // Wave 3 上下文增强版定位销孔判定（降低孤立过线孔误判，同时支持成对 Ø10 定位销孔）：
        //   · 成对出现（count >= 2）或同视图伴有螺纹/过孔安装孔（hasCompanionMounting）的 Ø4/5/6/8 光孔判为定位销孔；
        //   · Ø10 光孔仅当成对出现且伴有安装孔时才判为定位销孔；
        //   · 全零件孤立单个无伴随安装孔的光孔（如单孔过线支架）不误标 H7。
        public static bool IsContextPinHole(double diameterMm, int countInGroup, bool isHole, bool threaded, bool counterbore, bool hasCompanionMounting){
            if (!isHole || threaded || counterbore) return false;
            bool basePin = IsPinHole(diameterMm, true, false, false);
            if (basePin) {
                if (countInGroup >= 2 || hasCompanionMounting) return true;
                return false;
            }
            if (Math.Abs(diameterMm - 10.0) < 0.005 && countInGroup >= 2 && hasCompanionMounting) return true;
            return false;
        }

        // 同轴定位销沉孔判定（如 02-031 的 2×Ø6.6 贯通 + 反面同轴 2×Ø12 ↧5 H7 定位销座孔）：
        // 外圈为标准销径/定位套径（Ø6/8/10/12/14/16）、无螺纹、成对出现（2~8 个）、比内孔大 ≥1.5mm 且深度 ≥3.5mm。
        public static readonly double[] LocatingPinBoreDiameters = { 6, 8, 10, 12, 14, 16 };
        public static bool IsConcentricLocatingPinBore(double outerDiaMm, double innerDiaMm, double depthMm, int pairCount, bool outerThreaded){
            if (outerThreaded || pairCount < 2 || pairCount > 8) return false;
            if (outerDiaMm < innerDiaMm + 1.5 || depthMm < 3.5) return false;
            foreach (double d in LocatingPinBoreDiameters) if (Math.Abs(d - outerDiaMm) < 0.05) return true;
            return false;
        }

        // 标准轴承孔判定（如衬套块 02-003 的 Ø28 轴承位）：
        // 光孔、无螺纹、匹配标准轴承外径、带同轴挡肩或卡簧退刀槽、且轴承位深度 >= 4mm → 返回 "H7"
        public static bool IsBearingBore(double diameterMm, bool isHole, bool threaded, bool hasConcentricStepOrGroove, double depthMm){
            if (!isHole || threaded || !hasConcentricStepOrGroove || depthMm < 4.0) return false;
            foreach (double bd in BearingBoreDiameters) if (Math.Abs(bd - diameterMm) < 0.02) return true;
            return false;
        }

        // 车床回转件各段外径/内径智能配合判定：
        //   · 多台阶轴上带更大挡肩的标准轴承内径段（轴向长 6~65mm）→ "k6"（SK先惠模板右栏：轴与轴承内圈 H7/k6）；
        //   · 单段精细导向定位销（如 02-029 Ø12×25，长度≥1.4×直径）→ "g6"（SK先惠模板右栏：定位销外径 g6）；
        //   · 剖视台阶内孔匹配标准轴承外径 → "H7"。
        public static string ClassifyShaftDiameterFit(LatheDiameterPair p, IList<LatheDiameterPair> allOuter, double totalLengthMm){
            if (p == null || p.IsUndercut) return "";
            double dia = p.DiameterMm;
            double segLenMm = (p.XMax - p.XMin) * 1000.0;
            if (p.IsInnerBore) {
                bool hasStep = allOuter != null && allOuter.Count >= 1;
                return IsBearingBore(dia, true, false, hasStep, segLenMm) ? BearingBoreFit : "";
            }
            int outerCount = allOuter == null ? 0 : allOuter.Count(o => !o.IsInnerBore && !o.IsUndercut);
            if (outerCount >= 2) {
                bool hasLargerShoulder = allOuter.Any(o => !o.IsInnerBore && o.DiameterMm > dia + 1.5);
                if (hasLargerShoulder && segLenMm >= 6.0 && segLenMm <= 65.0) {
                    foreach (double jd in BearingJournalDiameters) if (Math.Abs(jd - dia) < 0.02) return BearingShaftFit;
                }
            } else if (outerCount == 1 && totalLengthMm >= dia * 1.4 && totalLengthMm <= 120.0) {
                foreach (double gd in GuidePinDiameters) if (Math.Abs(gd - dia) < 0.02) return GuidePinShaftFit;
            }
            return "";
        }

        // 直线导轨安装孔排识别（纯单零件特征）：
        // 在同一直线（同 X 或同 Y ±0.15mm）上发现 ≥3 个等距螺纹孔，且相邻孔距命中常见直线导轨节距（20/25/30/40/60/80mm ±0.25mm）。
        public static GuideRailMatch DetectGuideRailPattern(IList<DwgEdge> circles, Func<DwgEdge, bool> isTapped, double[] viewBox){
            if (circles == null || circles.Count < 3 || isTapped == null) return null;
            var tapped = new List<DwgEdge>();
            foreach (var c in circles) if (c != null && c.Kind == DwgKind.Circle && isTapped(c)) tapped.Add(c);
            if (tapped.Count < 3) return null;

            GuideRailMatch best = null;
            foreach (bool horiz in new[] { true, false }) {
                var groups = new List<List<DwgEdge>>();
                foreach (var c in tapped) {
                    double key = horiz ? c.Cy : c.Cx;
                    List<DwgEdge> hit = null;
                    foreach (var g in groups) {
                        double gk = horiz ? g[0].Cy : g[0].Cx;
                        if (Math.Abs(gk - key) <= 1.5e-4) { hit = g; break; }
                    }
                    if (hit == null) { hit = new List<DwgEdge>(); groups.Add(hit); }
                    hit.Add(c);
                }
                foreach (var g in groups) {
                    if (g.Count < 3) continue;
                    g.Sort((a, b) => (horiz ? a.Cx : a.Cy).CompareTo(horiz ? b.Cx : b.Cy));
                    var coords = new List<double>();
                    foreach (var c in g) {
                        double v = horiz ? c.Cx : c.Cy;
                        if (coords.Count == 0 || v - coords[coords.Count - 1] > 1e-4) coords.Add(v);
                    }
                    if (coords.Count < 3) continue;
                    // 查找命中标准导轨节距的最长等距连续子列
                    foreach (double stdPitch in GuideRailPitches) {
                        int maxRun = 1;
                        double runStart = coords[0], runEnd = coords[0];
                        for (int i = 0; i < coords.Count; i++) {
                            int len = 1;
                            double cur = coords[i];
                            for (int j = i + 1; j < coords.Count; j++) {
                                double gapMm = (coords[j] - cur) * 1000.0;
                                if (Math.Abs(gapMm - stdPitch) <= 0.25) {
                                    len++; cur = coords[j];
                                } else if (gapMm > stdPitch + 0.25) break;
                            }
                            if (len > maxRun) { maxRun = len; runStart = coords[i]; runEnd = cur; }
                        }
                        if (maxRun >= 3 && (best == null || maxRun > best.HoleCount)) {
                            best = new GuideRailMatch {
                                HorizontalRow = horiz,
                                LineCoord = horiz ? g[0].Cy : g[0].Cx,
                                SpanStart = runStart,
                                SpanEnd = runEnd,
                                HoleCount = maxRun,
                                PitchMm = stdPitch
                            };
                        }
                    }
                }
            }
            return best;
        }

        // ISO 286 k6 / g6 常用轴公差兜底查表（毫米，供天工标准表未命中时回退）
        static readonly double[] K6Lower = { 0.000, 0.001, 0.001, 0.001, 0.002, 0.002, 0.002, 0.003, 0.003, 0.004, 0.004, 0.004, 0.005 };
        static readonly double[] K6Upper = { 0.006, 0.009, 0.010, 0.012, 0.015, 0.018, 0.021, 0.025, 0.028, 0.033, 0.036, 0.040, 0.045 };
        static readonly double[] G6Upper = { -0.002, -0.004, -0.005, -0.006, -0.007, -0.009, -0.010, -0.012, -0.014, -0.015, -0.017, -0.018, -0.020 };
        static readonly double[] G6Lower = { -0.008, -0.012, -0.014, -0.017, -0.020, -0.025, -0.029, -0.034, -0.039, -0.044, -0.049, -0.054, -0.060 };

        public static bool ShaftFitDeviation(string fitClass, double diameterMm, out double upperMm, out double lowerMm){
            upperMm = 0; lowerMm = 0;
            int idx = -1;
            for (int i = 0; i < Band.Length; i++) if (diameterMm <= Band[i] + 1e-9) { idx = i; break; }
            if (idx < 0) return false;
            if (fitClass == "k6") { upperMm = K6Upper[idx]; lowerMm = K6Lower[idx]; return true; }
            if (fitClass == "g6") { upperMm = G6Upper[idx]; lowerMm = G6Lower[idx]; return true; }
            return false;
        }

        // ISO 286 / GB/T 1800.2 的 IT7 公差（毫米），按基本尺寸分段（段上限含在本段：Ø6 属于 3～6 段）。H7 = 上偏差 IT7、下偏差 0。
        static readonly double[] Band = { 3, 6, 10, 18, 30, 50, 80, 120, 180, 250, 315, 400, 500 };
        static readonly double[] IT7 = { 0.010, 0.012, 0.015, 0.018, 0.021, 0.025, 0.030, 0.035, 0.040, 0.046, 0.052, 0.057, 0.063 };
        public static double H7Upper(double diameterMm){
            for (int i = 0; i < Band.Length; i++) if (diameterMm <= Band[i] + 1e-9) return IT7[i];
            return double.NaN;
        }

        // 一个尺寸的公差写法统一成能比较的样子：公差带（"H7"）、对称公差（"±0.05"）、上下偏差（"+0.08/+0.03"）。
        // 直接写数值的 H7（人图里 Ø6 写成 +0.012/0）也认成 "H7"。全空 = ""。
        public static string Normalize(double valueMm, string holeClass, string shaftClass, string upper, string lower){
            string c = ((holeClass ?? "") + (shaftClass ?? "")).Trim();
            if (c.Length > 0) return c;
            string us = (upper ?? "").Trim(), ls = (lower ?? "").Trim();
            if (us.Length == 0 && ls.Length == 0) return "";
            if (us.StartsWith("±") || ls.StartsWith("±")) { double s; return Num(us.StartsWith("±") ? us.Substring(1) : ls.Substring(1), out s) ? "±" + F(Math.Abs(s)) : ""; }
            double u, l;
            if (!Num(us, out u) || !Num(ls, out l)) return "";
            if (Math.Abs(u + l) < 1e-6 && u > 0) return "±" + F(u);
            double h7 = H7Upper(valueMm);
            if (!double.IsNaN(h7) && Math.Abs(u - h7) < 0.0011 && Math.Abs(l) < 1e-6) return "H7";
            return S(u) + "/" + S(l);
        }

        // 形位公差框的一格（"%PO%VB%DI0.03"、"%PP%VB0.04%VBA"）→ 符号 + 公差值（"PO0.03"），比较用：不管有没有 Ø、基准字母
        public static string FrameKey(string frame){
            if (string.IsNullOrEmpty(frame)) return "";
            var m = Regex.Match(frame, @"^%([A-Z]{2})");
            if (!m.Success) return "";
            var n = Regex.Match(frame.Substring(3), @"\d*\.?\d+");
            double v;
            return m.Groups[1].Value + (n.Success && Num(n.Value, out v) ? F(v) : "");
        }

        // 销孔所在的面在侧面视图里是一条线：孔轴在这个视图里沿 x（alongX）或沿 y，孔口在视图坐标 (mu, mv)，孔半径 r（视图坐标，米）。
        // 找过孔口、垂直于孔轴的最长那条线，粗糙度符号挂在这条线上孔带外面、离孔远的那一段中间（at = 视图坐标）。找不到返回 null。
        public static DwgEdge MouthFace(IEnumerable<DwgEdge> edges, bool alongX, double mu, double mv, double r, out double[] at){
            at = null;
            const double tol = 2e-5;
            DwgEdge best = null;
            foreach (var e in edges) {
                if (e.Kind != DwgKind.Line) continue;
                // 孔轴沿 x → 面是竖线 x = mu；沿 y → 横线 y = mv
                bool ok = alongX ? e.IsVertical && Math.Abs(e.X1 - mu) < tol : e.IsHorizontal && Math.Abs(e.Y1 - mv) < tol;
                if (!ok) continue;
                double lo = alongX ? Math.Min(e.Y1, e.Y2) : Math.Min(e.X1, e.X2), hi = alongX ? Math.Max(e.Y1, e.Y2) : Math.Max(e.X1, e.X2), c = alongX ? mv : mu;
                // 整条线都在孔带里（孔壁轮廓那一小段）不算面
                if (lo >= c - r - tol && hi <= c + r + tol) continue;
                if (best == null || e.Length > best.Length + 1e-9) best = e;
            }
            if (best == null) return null;
            double l0 = alongX ? Math.Min(best.Y1, best.Y2) : Math.Min(best.X1, best.X2), h0 = alongX ? Math.Max(best.Y1, best.Y2) : Math.Max(best.X1, best.X2), cc = alongX ? mv : mu;
            // 孔带两边哪段长用哪段（孔不在这条线范围里就用整条线）
            double a0 = l0, a1 = h0;
            if (cc + r > l0 && cc - r < h0) { if (cc - r - l0 >= h0 - (cc + r)) a1 = cc - r; else a0 = cc + r; }
            double p = (a0 + a1) / 2;
            at = alongX ? new[] { mu, p } : new[] { p, mv };
            return best;
        }

        // 「需要人工确认」里的一句（保留老版签名 100% 兼容）
        public static string Note(int pinGroups, int pinHoles, bool roughness){
            if (pinGroups == 0) return "";
            return "公差 加了 " + pinHoles + " 个定位销孔的 H7 + 位置度 Ø0.03" + (roughness ? "、销孔所在面 Ra3.2" : "") + "（按公司图纸统计的习惯加的，请确认是不是销孔、要不要基准" + (roughness ? " / 粗糙度" : "") + "）";
        }

        // Wave 3 综合单零件智能公差确认提示
        public static string SummaryNote(int pinGroups, int pinHoles, int bearingBores, int bearingShafts, int guidePins, bool hasGuideRail, bool hasDatumA, bool hasRoughness){
            var parts = new List<string>();
            if (pinGroups > 0 && pinHoles > 0) parts.Add(pinHoles + " 个定位销孔 H7/位置度");
            if (bearingBores > 0) parts.Add(bearingBores + " 个轴承孔 H7");
            if (bearingShafts > 0) parts.Add(bearingShafts + " 段轴承轴颈 k6");
            if (guidePins > 0) parts.Add(guidePins + " 段定位销 g6");
            if (hasGuideRail) parts.Add("导轨安装面平面度");
            if (hasDatumA) parts.Add("基准 A");
            if (hasRoughness) parts.Add("配合面粗糙度");
            if (parts.Count == 0) return "";
            return "公差 已按单零件规则自动补充 " + string.Join("、", parts) + "（请人工复核配合与基准）";
        }

        static bool Num(string s, out double v){
            s = (s ?? "").Trim().Replace("+", "");
            if (s.StartsWith(".")) s = "0" + s; if (s.StartsWith("-.")) s = "-0" + s.Substring(1);
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }
        static string F(double v){ return Math.Round(v, 4).ToString("0.####", CultureInfo.InvariantCulture); }
        static string S(double v){ return Math.Abs(v) < 1e-9 ? "0" : (v > 0 ? "+" : "") + F(v); }
    }
}
