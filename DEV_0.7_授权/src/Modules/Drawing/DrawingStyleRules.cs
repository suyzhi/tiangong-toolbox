using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace TianGongCadSuite {
    // 一键出图（0.13.4）的样式、孔深与标注形态纯逻辑。不引用任何 CAD 类型，tests/pure 与 PanelTests 均可离线验证。
    //
    // 背景（2026-11-21 实测公司模板与 24 张人画工程图，共 620 个尺寸 + 162 个中心标记/中心线）：
    //   · A4-SK图纸.dft 保存时的当前活动尺寸样式是「A4-水平2」（字高 1.5mm、线宽 0.10mm，细小难以辨认）；
    //   · A3-SK图纸.dft 保存时的当前活动尺寸样式是「A3-孔」（单边尺寸线 + 单边箭头，LineConnect=False，只适合特殊孔）；
    //   · 人画图纸中上述两个样式使用次数均为 0：A3 图 95% 用「A3-国标-水平」（3mm/0.18mm 蓝字，引出文字水平折线），
    //     A4 常规图同样主用「A3-国标-水平」（在 A4 模板里字高 2.75mm/0.18mm 蓝字），刻字引线统一用「A3-国标」，
    //     中心标记/中心线用模板自带的红色点划线样式「中心线」（#FF0000）。
    public static class DrawingStyleRules {
        static readonly Regex MetricNom = new Regex(@"^M(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);

        public const string DefaultDimensionStyle = "A3-国标-水平";
        public static readonly string[] DimensionStyleChoices = { "A3-国标-水平", "A3-国标" };

        static readonly string[] PrimaryCandidates = { "A3-国标-水平", "A3-国标水平", "A3-国标", "A4-国标-水平", "A4-国标水平", "A4-国标" };
        static readonly string[] EngravingCandidates = { "A3-国标", "A3-国标-水平", "A3-国标水平", "A4-国标", "A4-国标-水平", "A4-国标水平" };
        static readonly string[] CenterMarkCandidates = { "中心线", "A3-国标-水平", "A3-国标水平", "A3-国标" };

        // 统一界面 / 配置里的尺寸样式选项：A3-国标水平 与 A3-国标-水平 视为同一种，默认 A3-国标-水平。
        public static string NormalizePreferredStyle(string preferred){
            if (string.IsNullOrEmpty(preferred)) return DefaultDimensionStyle;
            string trimmed = preferred.Trim();
            if (trimmed.Length == 0) return DefaultDimensionStyle;
            string compact = CompactKey(trimmed);
            if (compact == "A3国标水平") return "A3-国标-水平";
            if (compact == "A3国标") return "A3-国标";
            return trimmed;
        }

        // 主尺寸样式（坐标、线性、直径、半径、倒角、形位公差、粗糙度）。
        // 支持「A3-国标-水平」「A3-国标水平」「A3-国标」等命名变体；模板里找不到公司样式时返回空串，沿用模板自带样式。
        public static string PrimaryDimensionStyle(IEnumerable<string> available, string sheetSize){
            return PrimaryDimensionStyle(available, sheetSize, "");
        }

        public static string PrimaryDimensionStyle(IEnumerable<string> available, string sheetSize, string preferred){
            var list = NormalizeAvailable(available);
            if (list.Count == 0) return "";
            if (!string.IsNullOrEmpty(preferred) && preferred.Trim().Length > 0) {
                string prefHit = PickFirst(list, new[] { preferred.Trim() });
                if (prefHit.Length > 0) return prefHit;
            }
            string hit = PickFirst(list, PrimaryCandidates);
            if (hit.Length > 0) return hit;
            // 兜底：模板里把公司样式改名成带前后缀的 A3 国标样式（排除孔 / 水平2 / 标题栏）
            string fallback = list
                .Where(s => {
                    string c = CompactKey(s);
                    return c.Contains("A3国标") && !c.Contains("孔") && !c.Contains("水平2") && !c.Contains("标题栏");
                })
                .OrderByDescending(s => CompactKey(s).Contains("水平") ? 1 : 0)
                .FirstOrDefault();
            return fallback ?? "";
        }

        // 刻字引线样式：与公司模板原装「刻字：...」气泡保持一致（A3-国标，2.75mm/3mm）。
        public static string EngravingStyle(IEnumerable<string> available, string sheetSize){
            return PickFirst(NormalizeAvailable(available), EngravingCandidates);
        }

        // 中心标记 / 中心线样式：优先用模板里的红色点划线样式「中心线」。
        public static string CenterMarkStyle(IEnumerable<string> available, string sheetSize){
            return PickFirst(NormalizeAvailable(available), CenterMarkCandidates);
        }

        // 清洗天工全局参数 seApplicationGlobalTemplatePathFolder 返回的模板路径（如「C:\...\Template\SK先惠//LISTTYPE_3/0/」）。
        public static string ParseTemplateFolder(string raw){
            if (string.IsNullOrEmpty(raw)) return "";
            string s = raw.Trim();
            int marker = s.IndexOf("//", StringComparison.Ordinal);
            if (marker > 2) s = s.Substring(0, marker);
            return s.Trim().TrimEnd('\\', '/');
        }

        static List<string> NormalizeAvailable(IEnumerable<string> available){
            if (available == null) return new List<string>();
            return available.Where(s => !string.IsNullOrEmpty(s)).ToList();
        }

        static string StyleKey(string s){
            if (string.IsNullOrEmpty(s)) return "";
            return s.Trim()
                .Replace('－', '-')
                .Replace('—', '-')
                .Replace('–', '-')
                .Replace('_', '-')
                .Replace(" ", "")
                .Replace("\t", "")
                .Replace("\u3000", "")
                .ToUpperInvariant();
        }

        static string CompactKey(string s){
            return StyleKey(s).Replace("-", "");
        }

        static string PickFirst(IList<string> available, string[] candidates){
            if (available == null || available.Count == 0 || candidates == null) return "";
            var exact = new HashSet<string>(available, StringComparer.Ordinal);
            foreach (var c in candidates) {
                if (string.IsNullOrEmpty(c)) continue;
                if (exact.Contains(c)) return c;
                string ck = StyleKey(c);
                foreach (var a in available) if (StyleKey(a) == ck) return a;
                string cc = CompactKey(c);
                foreach (var a in available) if (CompactKey(a) == cc) return a;
            }
            return "";
        }

        // 校验几何圆柱孔径（毫米）是否真的可能是某个公制内螺纹孔（如 M5 的小径 4.134 / 攻丝底孔 4.2）。
        // 目的：防止过孔（Ø5.5 过 M5、Ø6.6 过 M6）因同轴串配或 HoleData 残留描述被误标成「M5 / M6 螺纹孔」。
        public static bool IsNominalTappedDiameter(double holeDiaMm, string threadSize){
            if (holeDiaMm <= 0 || string.IsNullOrEmpty(threadSize)) return false;
            var m = MetricNom.Match(threadSize.Trim());
            if (!m.Success) return false;
            double nom;
            if (!double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out nom) || nom <= 0) return false;
            // 内螺纹几何圆柱可能是内小径、攻丝底孔或公称直径（绝不会超过公称直径 +0.05；所有过孔均比公称大 ≥0.2mm）
            return holeDiaMm <= nom + 0.05 && holeDiaMm >= nom * 0.65;
        }

        // 盲孔名义深度（毫米）：优先取孔特征录入的深度 spec.Depth（例如 10mm）。
        // 原因：V 型底盲孔（118° 钻尖，VBottomDimType=igVBottomDimToV）的圆柱面长度比孔深短一个钻尖高，
        // 只量圆柱面会把 M5 深 10 量成 8.76、M6 深 10 量成 8.52（2026-11-21 实测 02-015 / 02-023）。
        public static double NominalHoleDepthMm(ModelHole h){
            if (h == null) return 0;
            if (h.Spec != null && h.Spec.Depth > 1e-6) return h.Spec.Depth;
            return h.Depth * 1000.0;
        }

        // 只有 2 个落点（原点 + 1 个目标，例如板厚 1 / 4 / 20 或侧视图总宽）的坐标串：
        // 用坐标尺寸会叠出「0」「1」糊成一团蓝块，人画工程图一律改用单个线性尺寸。
        public static bool ShouldUseLinearSpan(OrdinateRun run){
            return run != null && run.Origin != null && run.Targets.Count == 1;
        }

        // 找 45° 倒角斜线的相邻正交边（水平或竖直边，与倒角共享一个端点），供原生 AddChamfer 倒角尺寸使用。
        // 优先挑水平参考边：天工 AddChamfer 随参考边定向，选水平边能保证「2-4×45°」水平引出、不竖着歪写。
        public static DwgEdge ChamferReferenceEdge(DwgEdge chamfer, IList<DwgEdge> edges){
            if (chamfer == null || chamfer.Kind != DwgKind.Line || edges == null) return null;
            const double tol = 2e-5;
            DwgEdge best = null;
            foreach (var e in edges) {
                if (e == null || ReferenceEquals(e, chamfer) || e.Kind != DwgKind.Line) continue;
                if (!e.IsHorizontal && !e.IsVertical) continue;
                bool touch = NearPt(e.X1, e.Y1, chamfer.X1, chamfer.Y1, tol)
                    || NearPt(e.X1, e.Y1, chamfer.X2, chamfer.Y2, tol)
                    || NearPt(e.X2, e.Y2, chamfer.X1, chamfer.Y1, tol)
                    || NearPt(e.X2, e.Y2, chamfer.X2, chamfer.Y2, tol);
                if (!touch) continue;
                if (best == null) { best = e; continue; }
                if (e.IsHorizontal != best.IsHorizontal) { if (e.IsHorizontal) best = e; }
                else if (e.Length > best.Length + 1e-9) best = e;
            }
            return best;
        }

        static bool NearPt(double x1, double y1, double x2, double y2, double tol){
            return Math.Abs(x1 - x2) <= tol && Math.Abs(y1 - y2) <= tol;
        }

        // 判断坐标尺寸文字关键点 (tx, ty) 是否意外落进了视图外框 viewBox 内部（或跑到尺寸线反侧）。
        // 实测 02-015 上多个坐标尺寸的文字被天工放在零件轮廓里面（y=162 / 228，视图顶边 262），用此判定做防错拦截。
        public static bool IsTextInsideView(double tx, double ty, double[] viewBox, bool horizontal, bool lowSide, double clearance){
            if (viewBox == null || viewBox.Length < 4) return false;
            if (horizontal) return lowSide ? ty > viewBox[1] - clearance : ty < viewBox[3] + clearance;
            return lowSide ? tx > viewBox[0] - clearance : tx < viewBox[2] + clearance;
        }

        // ---- 端符翻转与窄跨度排版（0.13.6）----
        // 公司 A3-国标-水平 / A3-国标 箭头长约 3mm，单字符宽约 2.1mm。
        // 线性尺寸（板厚 4mm、小台阶、小比例细长件宽 20mm@1:6=3.3mm）纸面跨度不足以从容容纳两个内箭头 + 文字时，
        // 两个箭头会从内朝外挤成一团（缩在一块）并压住数字；翻转端符（TerminatorPosition=true）改为外箭头朝里指（-->| 4 |<--）。
        public static double EstimateValueWidth(string valueText){
            if (string.IsNullOrEmpty(valueText)) return 0.0024;
            return Math.Max(0.0022, valueText.Length * 0.0021);
        }

        public static bool ShouldFlipLinearTerminator(double valueM, double scale, string valueText){
            double paperSpan = Math.Abs(valueM) * Math.Max(scale, 1e-9);
            double textW = EstimateValueWidth(valueText);
            // 两个箭头 6mm + 文字宽 + 两侧留白 ≥ 4mm，且纸面跨度 < 14mm 一律翻转成外箭头朝里指
            return paperSpan < Math.Max(0.014, 0.0085 + textW);
        }

        // 纸面跨度连文字本身都塞不下（例如 1mm/2mm 板厚，或 20mm@1:6 = 3.3mm 塞不下 "20"）时，
        // 除翻转端符外，还将数字引出到单侧箭头外（-->| |<-- 20），避免尺寸界线压字。
        public static bool ShouldBreakNarrowLinearText(double valueM, double scale, string valueText){
            double paperSpan = Math.Abs(valueM) * Math.Max(scale, 1e-9);
            double textW = EstimateValueWidth(valueText);
            return paperSpan < textW + 0.0022;
        }

        public static double NarrowLinearBreakDistance(string valueText){
            return Math.Max(0.0055, 0.0035 + EstimateValueWidth(valueText) * 0.65);
        }

        // 半径标注箭头方向判定（AddRadius）：
        // kp0 = 弧上箭尖，kp1 = 圆弧中心，kp2 = 箭尾。
        // 当箭尾比箭尖更靠近圆心（|kp2-kp1| < |kp0-kp1|）时，箭头是从圆弧内侧朝外指的；
        // 翻转 TerminatorPosition 让箭头落到圆弧外侧朝圆心指（朝里指），避免箭头缩在圆角内部。
        public static bool ShouldFlipRadiusTerminator(double tipX, double tipY, double centerX, double centerY, double tailX, double tailY){
            double dTip2 = (tipX - centerX) * (tipX - centerX) + (tipY - centerY) * (tipY - centerY);
            double dTail2 = (tailX - centerX) * (tailX - centerX) + (tailY - centerY) * (tailY - centerY);
            return dTail2 < dTip2 - 1e-12;
        }

        // 引线标注水平横线（文字基准线）的 Y 坐标：
        // 实测天工 A3-国标-水平 下，主行和 SubfixString（沉孔 / 攻丝底孔第二行）全部画在水平横线【上方】，
        // 只有紧贴下方的形位公差框（⌖ Ø0.03）占 horizontal shelf 下方的 belowH 高度。
        public static double CalloutShelfY(double boxY0, double belowH){
            return boxY0 + 0.001 + Math.Max(0, belowH);
        }

        // 引线标注移动文字时对应的关键点索引：
        // AddRadialDiameter / AddRadius 的文字肩部关键点固定在 KP[4]（当半径引线越过圆弧角度范围时，天工会追加 KP[5]
        // 作为圆弧延长点，此时仍应移动 KP[4] 而非 KP[kc-1]）；AddChamfer(Perpendicular) 为 3 个关键点，文字点是 KP[2]。
        public static int CalloutTextKeyPointIndex(int keyPointCount){
            if (keyPointCount <= 0) return -1;
            return Math.Min(keyPointCount - 1, 4);
        }

        // 依据文字中心 (cx,cy) 与水平横线外端 (ex,ey) 计算真实文字架矩形（不含斜引线本身）：
        // 实测（A3/A4 单行与 2 行沉孔标注）：(cx,cy) 恰好是水平横线上方整个文字块的几何中心，
        // 故文字顶边约在 cy + |cy - ey| = 2*cy - ey（±0.7mm），完全不受向下斜引线导致的 Range.y1 虚高干扰。
        public static double[] CalloutShelfBox(double cx, double cy, double ex, double ey){
            double otherX = 2 * cx - ex;
            double x0 = Math.Min(otherX, ex) - 0.0006;
            double x1 = Math.Max(otherX, ex) + 0.0006;
            double halfH = Math.Max(0.0022, Math.Abs(cy - ey));
            double y0 = Math.Min(ey, cy) - 0.0006;
            double y1 = Math.Max(ey, cy) + halfH;
            return new[] { x0, y0, x1, y1 };
        }
    }
}
