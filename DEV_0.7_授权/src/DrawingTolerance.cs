using System;
using System.Collections.Generic;
using System.Globalization;
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
    public static class DrawingTolerance {
        public const string PinFit = "H7";
        public const string PositionFrame = "%PO%VB%DI0.03";   // 天工形位公差框的编码：%PO 位置度、%VB 分隔、%DI 直径符号
        public const string MatingRoughness = "3.2";

        // 定位销孔的直径（毫米）：普查里没螺纹、没沉孔的这些直径的孔，多数标了公差（见 DRAWING.md 的表）。
        // Ø5 光孔 55%（21/38）刚过一半、留出集里 5 张人没标的图因此被加了 H7；用户 2026-11-03 看过这个代价，定了保留
        public static readonly double[] PinDiameters = { 4, 5, 6, 8 };

        // 一个孔要不要当定位销孔加 H7：真的孔（不是轴 / 凸台）、没螺纹、没沉孔、直径是销径
        public static bool IsPinHole(double diameterMm, bool isHole, bool threaded, bool counterbore){
            if (!isHole || threaded || counterbore) return false;
            foreach (var d in PinDiameters) if (Math.Abs(d - diameterMm) < 0.005) return true;
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

        // 「需要人工确认」里的一句
        public static string Note(int pinGroups, int pinHoles, bool roughness){
            if (pinGroups == 0) return "";
            return "公差 加了 " + pinHoles + " 个定位销孔的 H7 + 位置度 Ø0.03" + (roughness ? "、销孔所在面 Ra3.2" : "") + "（按公司图纸统计的习惯加的，请确认是不是销孔、要不要基准" + (roughness ? " / 粗糙度" : "") + "）";
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
