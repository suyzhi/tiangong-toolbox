using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace TianGongCadSuite {
    // Wave 2 纯逻辑引擎（无 CAD / WinForms 依赖，全部纳入 tests/pure）：
    //   1. 细长件断裂区间规划（Broken View）：扫描长轴上的无特征均匀空段，算出安全断裂起止位置与放大后的新比例；
    //   2. 多孔大板孔表规划（Hole Table）：将视图孔按规格分组编号（A1/A2/B1…），按统一 3D XYZ 基准输出精确 X/Y 坐标行；
    //   3. 局部密集区聚类（Detail View）：在框架安装板或大板密孔区识别紧凑特征簇，计算放大圈中心、半径与目标放大比例；
    //   4. 溢出补视图（底面盲孔 / 背面·右端面盲孔 / 斜孔向视图）第 1 页空位择优与第 2 页网格排版。

    public sealed class BreakPlan {
        public bool VerticalBreak;        // true = 沿水平 X 方向截短（画两条竖向断裂线）；false = 沿竖直 Y 截短
        public double CutStartView;       // 断裂起点（视图坐标，米）
        public double CutEndView;         // 断裂终点（视图坐标，米）
        public double RemovedModelLen;    // 截掉的模型长度（米）
        public double EffectiveLongSpan;  // 截短后的等效长轴跨度（模型米，含断口间隙）
        public double UpgradedScale;      // 截短后可提升到的目标比例
    }

    public sealed class HoleTableRow {
        public string Tag = "";           // 孔编号（A1, A2, B1...）
        public double Xmm;                // 相对统一 3D 基准的 X 坐标（毫米）
        public double Ymm;                // 相对统一 3D 基准的 Y 坐标（毫米）
        public string SpecText = "";      // 孔规格文字（如 Ø6.6 贯通 / M5↧12 / Ø6 H7 贯通）
        public bool IsPinHole;            // 是否定位销孔 / 同轴定位销座孔（孔表右侧追加同轴度形位公差列）
        public string GdtFrame = "";      // 销孔对应的天工形位公差编码（如 %CO%VB%DI0.03%VBA）
        public DwgEdge Circle;            // 对应视图圆（用于挂编号气泡）
    }

    public sealed class HoleTableGroupInput {
        public string SpecText = "";
        public bool IsPinHole;
        public string GdtFrame = "";
        public readonly List<DwgEdge> Circles = new List<DwgEdge>();
    }

    public sealed class HoleTableChunkPlan {
        public int Offset;
        public int Count;
        public bool HasPinHole;
        public double HeaderH;
        public double RowH;
        public double[] ColWidths;
        public double Width { get { double s = 0; if (ColWidths != null) foreach (double w in ColWidths) s += w; return s; } }
        public double Height { get { return HeaderH + Count * RowH; } }
    }

    public sealed class DetailCluster {
        public double Cx, Cy;             // 聚类中心（视图坐标，米）
        public double RadiusView;         // 包住整簇特征的安全圆半径（视图坐标，米）
        public int HoleCount;             // 包含的孔数
        public bool HasPinHole;           // 是否含定位销孔（高优先级）
        public double TargetScale;        // 建议放大比例
        public readonly List<DwgEdge> Circles = new List<DwgEdge>();
    }

    public sealed class OverflowViewPlan {
        public string Caption = "";       // 视图标题（如「底面视图」「背面视图」「右视图」「向视图 A」）
        public V3 SightDir;               // 视线方向（指向看图人）
        public V3 HorzDir;                // 屏幕水平 X 轴方向
        public double ModelW, ModelH;     // 视图模型宽高（米）
        public bool IsOblique;            // 是否斜孔向视图
        public bool BlindOnly;            // 仅标盲孔/斜孔
        public DwgViewRole RoleHint;      // Left / Back / Below(Bottom)
    }

    public sealed class Sheet2Slot {
        public double X, Y;               // 视图中心（图纸米）
        public double Scale;              // 推荐比例
        public double BoxW, BoxH;         // 可用格宽高（米）
    }

    public static class DrawingWave2Rules {
        // ---- 1. 细长件断裂视图规划 ----
        // 触发条件：
        //   · 零件长宽比 >= 5.0 且当前主视图比例 < 0.5（低于 1:2）；
        //   · 沿长轴扫描全部孔（含半径）、内部台阶竖线、圆弧后，存在一段占总长 >= 25% 的无特征均匀空段；
        //   · 截掉该空段（两端各留 ≥15mm 安全余量）后，新比例能严格大于当前比例（至少升一档）。
        public static BreakPlan PlanBreakView(IList<DwgEdge> edges, double[] viewBox, double currentScale, double availW, double availH){
            if (edges == null || viewBox == null || viewBox.Length < 4 || currentScale <= 0 || currentScale >= 0.5 - 1e-9) return null;
            double w = viewBox[2] - viewBox[0], h = viewBox[3] - viewBox[1];
            if (w < 0.12 && h < 0.12) return null;
            bool horizLong = w >= h;
            double longSpan = horizLong ? w : h;
            double shortSpan = horizLong ? h : w;
            if (shortSpan < 1e-4 || longSpan / shortSpan < 5.0) return null;

            double lo = horizLong ? viewBox[0] : viewBox[1];
            double hi = horizLong ? viewBox[2] : viewBox[3];
            // 收集所有沿长轴方向占据特征的区间 [a, b]（端面 12mm 内保留完整端头，不切在端头上）
            double margin = Math.Max(0.012, Math.Min(0.025, longSpan * 0.03));
            var busy = new List<double[]>();
            busy.Add(new[] { lo, lo + margin });
            busy.Add(new[] { hi - margin, hi });

            const double endTol = 5e-4;
            foreach (var e in edges) {
                if (e.Kind == DwgKind.Circle) {
                    double c = horizLong ? e.Cx : e.Cy;
                    busy.Add(new[] { c - e.R - margin, c + e.R + margin });
                } else if (e.Kind == DwgKind.Arc) {
                    var ab = DrawingPlan.Box(new[] { e });
                    double a0 = horizLong ? ab[0] : ab[1], a1 = horizLong ? ab[2] : ab[3];
                    busy.Add(new[] { a0 - margin, a1 + margin });
                } else if (e.Kind == DwgKind.Line) {
                    bool crossLine = horizLong ? e.IsVertical : e.IsHorizontal;
                    if (crossLine) {
                        double c = horizLong ? e.X1 : e.Y1;
                        if (c > lo + endTol && c < hi - endTol) {
                            busy.Add(new[] { c - margin, c + margin });
                        }
                    } else if (!e.IsHorizontal && !e.IsVertical) {
                        double a0 = horizLong ? Math.Min(e.X1, e.X2) : Math.Min(e.Y1, e.Y2);
                        double a1 = horizLong ? Math.Max(e.X1, e.X2) : Math.Max(e.Y1, e.Y2);
                        busy.Add(new[] { a0 - margin, a1 + margin });
                    }
                }
            }

            // 合并占据区间，找最宽的空隙
            busy.Sort((a, b) => a[0].CompareTo(b[0]));
            var merged = new List<double[]>();
            foreach (var iv in busy) {
                if (merged.Count == 0 || iv[0] > merged[merged.Count - 1][1]) merged.Add(new[] { iv[0], iv[1] });
                else if (iv[1] > merged[merged.Count - 1][1]) merged[merged.Count - 1][1] = iv[1];
            }
            double bestGap = 0, bestA = 0, bestB = 0;
            for (int i = 0; i + 1 < merged.Count; i++) {
                double g0 = merged[i][1], g1 = merged[i + 1][0];
                if (g1 - g0 > bestGap) { bestGap = g1 - g0; bestA = g0; bestB = g1; }
            }
            if (bestGap < longSpan * 0.25 || bestGap < 0.050) return null;

            double effLong = longSpan - bestGap + 0.006 / Math.Max(currentScale, 0.05);
            double newW = horizLong ? effLong : shortSpan;
            double newH = horizLong ? shortSpan : effLong;
            double upgraded = DrawingPlan.PickScale(newW, newH, availW, availH);
            // 至少能比原比例放大 25% 才值得插断裂视图，避免无谓断开
            if (upgraded <= currentScale * 1.24) return null;
            // 车床轴/长板最多升到 1:1，不盲目放大到 2:1
            if (upgraded > 1.0) upgraded = 1.0;
            if (upgraded <= currentScale * 1.15) return null;

            return new BreakPlan {
                VerticalBreak = horizLong,
                CutStartView = bestA,
                CutEndView = bestB,
                RemovedModelLen = bestGap,
                EffectiveLongSpan = effLong,
                UpgradedScale = upgraded
            };
        }

        // ---- 2. 多孔大板孔表规划 ----
        // 触发条件：非框架件的主视图圆孔总数 >= minHoles（默认 16）且孔组至少 2 组或单组 ≥ 16 孔（分布较散，非纯 PCD）。
        public static bool ShouldUseHoleTable(DrawingPartKind kind, int nonPcdHoleCount, int minHoles){
            if (kind == DrawingPartKind.ProfileFrame || kind == DrawingPartKind.ProfileSingle || kind == DrawingPartKind.Lathe) return false;
            return nonPcdHoleCount >= minHoles;
        }

        // 按规格分组编号（A1, A2... / B1, B2...），坐标严格相对统一 3D 基准投影原点 (originXView, originYView) 换算为毫米。
        public static List<HoleTableRow> BuildHoleTable(IList<KeyValuePair<string, List<DwgEdge>>> specGroups, double originXView, double originYView){
            var inputs = new List<HoleTableGroupInput>();
            if (specGroups != null) {
                foreach (var g in specGroups) {
                    var item = new HoleTableGroupInput { SpecText = g.Key ?? "" };
                    if (g.Value != null) item.Circles.AddRange(g.Value);
                    inputs.Add(item);
                }
            }
            return BuildHoleTable(inputs, originXView, originYView);
        }

        public static List<HoleTableRow> BuildHoleTable(IList<HoleTableGroupInput> specGroups, double originXView, double originYView){
            var rows = new List<HoleTableRow>();
            if (specGroups == null || specGroups.Count == 0) return rows;
            int gIdx = 0;
            foreach (var grp in specGroups.OrderByDescending(g => g.Circles.Count).ThenBy(g => g.SpecText, StringComparer.Ordinal)) {
                if (grp.Circles.Count == 0) continue;
                string prefix = LetterPrefix(gIdx++);
                var ordered = grp.Circles
                    .OrderBy(c => Math.Round((c.Cx - originXView) * 1000.0, 1))
                    .ThenBy(c => Math.Round((c.Cy - originYView) * 1000.0, 1))
                    .ToList();
                for (int i = 0; i < ordered.Count; i++) {
                    var c = ordered[i];
                    rows.Add(new HoleTableRow {
                        Tag = prefix + (i + 1),
                        Xmm = Math.Round((c.Cx - originXView) * 1000.0, 2),
                        Ymm = Math.Round((c.Cy - originYView) * 1000.0, 2),
                        SpecText = grp.SpecText,
                        IsPinHole = grp.IsPinHole,
                        GdtFrame = grp.IsPinHole ? (grp.GdtFrame ?? "") : "",
                        Circle = c
                    });
                }
            }
            return rows;
        }

        // 统一规划规范孔表的分栏列宽与行高（纯逻辑，排版区预留与 DwgWriter.HoleTable 共用）：
        //   · 只要全表含销孔（IsPinHole），若按 6.8mm 销孔行高能在单栏内放完（如 02-031 的 26 行），优先用统一 5 列单表；
        //   · 否则按栏规划：含销孔的分栏用 5 列（规格旁追加「形位公差」列，行高 6.8mm 容下 6.0mm 同轴度框），不含销孔的栏用紧凑 4 列（行高 4.8mm）。
        public static List<HoleTableChunkPlan> PlanHoleTableChunks(IList<HoleTableRow> rows, double maxHeightM, out double totalWidthM, out double maxHeightUsedM){
            var chunks = new List<HoleTableChunkPlan>();
            totalWidthM = 0; maxHeightUsedM = 0;
            if (rows == null || rows.Count == 0) return chunks;
            double usableH = Math.Max(0.065, maxHeightM);
            const double headerH = 0.0056;
            const double normalRowH = 0.0048;
            const double pinRowH = 0.0068;
            const double chunkGap = 0.005;

            bool anyPin = rows.Any(r => r != null && r.IsPinHole && !string.IsNullOrEmpty(r.GdtFrame));
            int capPin = Math.Max(8, (int)Math.Floor((usableH - headerH - 0.002) / pinRowH));
            int capNormal = Math.Max(10, (int)Math.Floor((usableH - headerH - 0.002) / normalRowH));

            // 单栏即可完整容纳全表（含销孔行高）时，直接输出一张统一 5 列表，美观且总宽仅 ~114mm
            if (anyPin && rows.Count <= capPin) {
                var p = MakeHoleChunk(rows, 0, rows.Count, headerH, pinRowH);
                chunks.Add(p);
            } else {
                int offset = 0;
                while (offset < rows.Count) {
                    int peek = Math.Min(capNormal, rows.Count - offset);
                    bool hasPinInPeek = false;
                    for (int i = 0; i < peek; i++) {
                        var hr = rows[offset + i];
                        if (hr != null && hr.IsPinHole && !string.IsNullOrEmpty(hr.GdtFrame)) { hasPinInPeek = true; break; }
                    }
                    int take = hasPinInPeek ? Math.Min(capPin, rows.Count - offset) : peek;
                    // 重新核查实际截取的 take 行里是否真含销孔
                    bool chunkHasPin = false;
                    for (int i = 0; i < take; i++) {
                        var hr = rows[offset + i];
                        if (hr != null && hr.IsPinHole && !string.IsNullOrEmpty(hr.GdtFrame)) { chunkHasPin = true; break; }
                    }
                    var p = MakeHoleChunk(rows, offset, take, headerH, chunkHasPin ? pinRowH : normalRowH);
                    chunks.Add(p);
                    offset += take;
                }
            }

            for (int i = 0; i < chunks.Count; i++) {
                totalWidthM += chunks[i].Width + (i > 0 ? chunkGap : 0);
                if (chunks[i].Height > maxHeightUsedM) maxHeightUsedM = chunks[i].Height;
            }
            return chunks;
        }

        static HoleTableChunkPlan MakeHoleChunk(IList<HoleTableRow> rows, int offset, int count, double headerH, double rowH){
            bool hasPin = false;
            bool hasDatum = false;
            int maxSpecLen = 6;
            for (int i = 0; i < count; i++) {
                var hr = rows[offset + i];
                if (hr == null) continue;
                int len = (hr.SpecText ?? "").Length;
                if (len > maxSpecLen) maxSpecLen = len;
                if (hr.IsPinHole && !string.IsNullOrEmpty(hr.GdtFrame)) {
                    hasPin = true;
                    if (hr.GdtFrame.IndexOf("%VBA", StringComparison.Ordinal) >= 0) hasDatum = true;
                }
            }
            double specW = maxSpecLen >= 13 ? 0.044 : 0.038;
            double[] widths = hasPin
                ? new[] { 0.011, 0.016, 0.016, specW, hasDatum ? 0.031 : 0.025 }
                : new[] { 0.011, 0.016, 0.016, specW };
            return new HoleTableChunkPlan {
                Offset = offset,
                Count = count,
                HasPinHole = hasPin,
                HeaderH = headerH,
                RowH = rowH,
                ColWidths = widths
            };
        }

        static string LetterPrefix(int idx){
            // A..Z, AA..AZ
            if (idx < 26) return ((char)('A' + idx)).ToString();
            return ((char)('A' + (idx / 26) - 1)).ToString() + ((char)('A' + (idx % 26))).ToString();
        }

        // ---- 3. 局部放大视图聚类（框架安装板 / 大板密孔扎堆区） ----
        // 触发场景：主视图比例 <= 1:4（大件或框架）且视图里存在多处紧凑小孔簇（单簇包络直径 ≤ 零件最大边 18%、含 ≥2 个孔、且纸面孔距偏密 < 10mm）。
        public static List<DetailCluster> FindDetailClusters(IList<DwgEdge> circles, double[] viewBox, double mainScale, Func<DwgEdge, bool> isPin, int maxClusters){
            var result = new List<DetailCluster>();
            if (circles == null || circles.Count < 2 || viewBox == null || viewBox.Length < 4 || mainScale > 0.26) return result;
            double partMax = Math.Max(viewBox[2] - viewBox[0], viewBox[3] - viewBox[1]);
            if (partMax < 0.25) return result;

            // 邻域聚类半径：模型上最多 120mm 或零件长 9%（对应框架 80×40 ~ 120×120 安装板）
            double linkDist = Math.Min(0.120, Math.Max(0.045, partMax * 0.09));
            double maxClusterDia = Math.Min(0.220, partMax * 0.18);

            var visited = new bool[circles.Count];
            for (int i = 0; i < circles.Count; i++) {
                if (visited[i]) continue;
                var comp = new List<DwgEdge>();
                var q = new Queue<int>();
                q.Enqueue(i); visited[i] = true;
                while (q.Count > 0) {
                    int cur = q.Dequeue();
                    comp.Add(circles[cur]);
                    for (int j = 0; j < circles.Count; j++) {
                        if (visited[j]) continue;
                        double dx = circles[cur].Cx - circles[j].Cx, dy = circles[cur].Cy - circles[j].Cy;
                        if (Math.Sqrt(dx * dx + dy * dy) <= linkDist) {
                            visited[j] = true;
                            q.Enqueue(j);
                        }
                    }
                }
                if (comp.Count < 2) continue;
                double x0 = comp.Min(c => c.Cx - c.R), x1 = comp.Max(c => c.Cx + c.R);
                double y0 = comp.Min(c => c.Cy - c.R), y1 = comp.Max(c => c.Cy + c.R);
                double cw = x1 - x0, ch = y1 - y0;
                double dia = Math.Sqrt(cw * cw + ch * ch);
                if (dia > maxClusterDia) continue;

                // 检查簇内最小孔距在当前主比例下确实偏挤（纸面间距 < 10mm）
                double minPaperSep = double.MaxValue;
                for (int a = 0; a < comp.Count; a++) {
                    for (int b = a + 1; b < comp.Count; b++) {
                        double d = Math.Sqrt((comp[a].Cx - comp[b].Cx) * (comp[a].Cx - comp[b].Cx) + (comp[a].Cy - comp[b].Cy) * (comp[a].Cy - comp[b].Cy)) * mainScale;
                        if (d < minPaperSep) minPaperSep = d;
                    }
                }
                if (minPaperSep > 0.010) continue;

                double cx = (x0 + x1) / 2.0, cy = (y0 + y1) / 2.0;
                double rad = Math.Max(0.018, dia * 0.5 + 0.015);
                // 目标放大比例：至少比主视图放大 2 倍，框在图纸上约 45~75mm 直径
                double target = DrawingPlan.PickScale(rad * 2, rad * 2, 0.068, 0.068);
                if (target < mainScale * 1.9) target = Math.Min(1.0, mainScale * 2.5);
                if (target > 1.0) target = 1.0;

                var cl = new DetailCluster {
                    Cx = cx, Cy = cy, RadiusView = rad,
                    HoleCount = comp.Count,
                    HasPinHole = isPin != null && comp.Any(isPin),
                    TargetScale = target
                };
                cl.Circles.AddRange(comp);
                result.Add(cl);
            }

            return result
                .OrderByDescending(c => c.HasPinHole ? 1 : 0)
                .ThenByDescending(c => c.HoleCount)
                .Take(Math.Max(1, maxClusters))
                .ToList();
        }

        // ---- 4. 斜孔向视图方向聚合 ----
        // 识别所有轴线不沿 X/Y/Z 的斜孔，按去重后的孔口朝向归组（最多返回 2 个不同斜向视图）。
        public static List<OverflowViewPlan> PlanObliqueViews(IList<ModelHole> holes, double[] range){
            var plans = new List<OverflowViewPlan>();
            if (holes == null || holes.Count == 0 || range == null || range.Length < 6) return plans;
            double sx = Math.Abs(range[3] - range[0]), sy = Math.Abs(range[4] - range[1]), sz = Math.Abs(range[5] - range[2]);
            double diag = Math.Sqrt(sx * sx + sy * sy + sz * sz);

            var dirs = new List<V3>();
            foreach (var h in holes) {
                if (h == null) continue;
                var ax = h.Axis.Unit();
                if (Math.Abs(ax.X) >= 0.9998 || Math.Abs(ax.Y) >= 0.9998 || Math.Abs(ax.Z) >= 0.9998) continue;
                // 盲孔取开口朝向（!BlindHi → +Axis，否则 -Axis）；通孔规范化主分量为正
                V3 sight = ax;
                if (!h.Through) sight = !h.BlindHi ? ax : (ax * -1.0);
                else if (sight.Z < -1e-4 || (Math.Abs(sight.Z) <= 1e-4 && sight.Y < -1e-4)) sight = sight * -1.0;
                if (dirs.Any(d => Math.Abs(d.Dot(sight)) > 0.999)) continue;
                dirs.Add(sight);
                // 构造与 sight 正交的屏幕水平轴 HorzDir
                V3 up = Math.Abs(sight.Z) < 0.9 ? new V3(0, 0, 1) : new V3(1, 0, 0);
                V3 hx = up.Cross(sight).Unit();
                V3 hy = sight.Cross(hx).Unit();
                // 投影包围盒求视图模型宽高
                double u0 = double.MaxValue, u1 = double.MinValue, v0 = double.MaxValue, v1 = double.MinValue;
                for (int i = 0; i < 8; i++) {
                    var c = new V3(range[(i & 1) == 0 ? 0 : 3], range[(i & 2) == 0 ? 1 : 4], range[(i & 4) == 0 ? 2 : 5]);
                    double pu = c.Dot(hx), pv = c.Dot(hy);
                    if (pu < u0) u0 = pu; if (pu > u1) u1 = pu;
                    if (pv < v0) v0 = pv; if (pv > v1) v1 = pv;
                }
                plans.Add(new OverflowViewPlan {
                    Caption = "向视图 " + ((char)('A' + plans.Count)),
                    SightDir = sight,
                    HorzDir = hx,
                    ModelW = Math.Max(0.01, Math.Min(diag, u1 - u0)),
                    ModelH = Math.Max(0.01, Math.Min(diag, v1 - v0)),
                    IsOblique = true,
                    BlindOnly = false
                });
                if (plans.Count >= 2) break;
            }
            return plans;
        }

        // ---- 5. 第 2 页图纸统一网格排版 ----
        // 在第 2 页有效绘图区（严格避开底栏标题栏和右上未注公差表）把 N 个溢出视图 / 详图均匀排成 1×1、1×2 或 2×2 网格，
        // 四周预留充足孔引线与坐标串走廊，杜绝视图冲进右上公差表或引线越出右侧图框。
        public static List<Sheet2Slot> LayoutSheet2(SheetZones z, IList<double[]> modelSizes, double preferredScale){
            var slots = new List<Sheet2Slot>();
            if (z == null || modelSizes == null || modelSizes.Count == 0) return slots;
            int n = modelSizes.Count;
            double tolZoneX = z.Name == "A4" ? 0.210 : 0.292;
            bool spansRightTolZone = z.Iso[2] > tolZoneX;
            double x0 = z.Main[0] + 0.018;
            double x1 = z.Iso[2] - (spansRightTolZone ? 0.032 : 0.016);
            if (x1 <= x0 + 0.04) { x0 = z.Main[0] + 0.008; x1 = z.Iso[2] - 0.008; }
            double y0 = z.Main[1] + 0.016;
            // 若排版区跨入右侧未注公差表 X 区间（A3 x≥292 / A4 x≥210），顶界严格压到 Iso[3] - 10mm（A3≤232mm），彻底避开公差表（A3 y≥247mm）
            double y1 = spansRightTolZone
                ? Math.Min(z.Main[3] - 0.014, z.Iso[3] - 0.010)
                : (z.Main[3] - 0.014);
            if (y1 <= y0 + 0.04) y1 = z.Main[3] - 0.008;

            int cols = n <= 1 ? 1 : (n <= 4 ? 2 : 3);
            int rows = (int)Math.Ceiling((double)n / cols);
            double cellW = (x1 - x0) / cols;
            double cellH = (y1 - y0) / rows;
            double padW = Math.Min(0.056, cellW * 0.28);
            double padH = Math.Min(0.044, cellH * 0.28);
            for (int i = 0; i < n; i++) {
                int r = i / cols, c = i % cols;
                double cx = x0 + (c + 0.5) * cellW;
                double cy = y1 - (r + 0.5) * cellH;
                double mw = Math.Max(1e-4, modelSizes[i][0]);
                double mh = Math.Max(1e-4, modelSizes[i][1]);
                double availW = Math.Max(0.02, cellW - padW);
                double availH = Math.Max(0.02, cellH - padH);
                double sc = DrawingPlan.PickScale(mw, mh, availW, availH);
                if (preferredScale > 0 && sc > preferredScale) sc = preferredScale;
                slots.Add(new Sheet2Slot { X = cx, Y = cy, Scale = sc, BoxW = cellW, BoxH = cellH });
            }
            return slots;
        }
    }
}
