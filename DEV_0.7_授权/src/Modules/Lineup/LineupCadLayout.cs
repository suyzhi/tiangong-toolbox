using System;
using System.Collections.Generic;
using System.Linq;

namespace TianGongCadSuite {
    public struct LineupCellAnchor {
        public int Col;       // 0-based
        public long ColOff;   // EMU
        public int Row;       // 0-based
        public long RowOff;   // EMU
    }

    public sealed class LineupCadCallout {
        public LineupLocationPoint Point;
        public string Side = "left";      // "left" or "right"
        public int ExcelRow;              // 1-based Excel row
        public int BomRow;                // 1-based row in LineUP第二版_BOM (0 = literal)
        public string Number = "";
        public string Description = "";
        public string Address = "";
        public string Tag = "";
        public bool HasArrow;
        // 页面坐标系下（以页面左上角为 0,0）的箭头端点（单位 EMU，供预览和锚点共用）
        public long StartX, StartY, EndX, EndY;
        public LineupCellAnchor FromAnchor, ToAnchor;
        public bool FlipH, FlipV;
    }

    public sealed class LineupCadPagePlan {
        public LineupCadPage Page;
        public int StartRow;              // 1-based 起始行（插图区域行）
        public int RowCount;              // 本页总行数（默认 41 = 3 行标题 + 38 行图区）
        public int DataStartRow;          // 1-based 数据与图区首行（StartRow + 3）
        public int DataEndRow;            // 1-based 数据与图区末行
        // 页面坐标系下图片位置（单位 EMU）
        public long ImageLeftEmu, ImageTopEmu, ImageWidthEmu, ImageHeightEmu;
        public long TotalWidthEmu, TotalHeightEmu;
        public LineupCellAnchor ImageFrom, ImageTo;
        public List<LineupCadCallout> Callouts = new List<LineupCadCallout>();
    }

    // 「LineUP第三版_CAD」12 列公司内部模板 V3.0 标准版式与无交叉箭头排布计算器
    // （纯逻辑，Excel OpenXML 导出与 WinForms 实时预览共用同一套几何；箭头方向严格为从图指向单元格）
    public static class LineupCadLayout {
        public const long EmuPerPx = 9525L;
        public const long EmuPerPt = 12700L;

        // 12 列（A..L）字符宽度（严格对齐公司内部模板 V3.0「LineUP第三版_CAD」）：
        // A:名称/描述  B:IO点位  C:SK编号 | D..I: 插图区域（6 列） | J:SK编号  K:IO点位  L:名称/描述
        public static readonly double[] ColWidths = {
            48.18, 34.64, 25.91,                      // A:名称/描述  B:IO点位  C:SK编号
            20.83, 20.83, 20.83, 20.83, 20.83, 35.22, // D..I: 插图区域（6 列）
            26.82, 33.03, 49.99                       // J:SK编号  K:IO点位  L:名称/描述
        };

        public const int ImageFirstCol = 3;    // D (0-based)
        public const int ImageLastColExcl = 9; // J (0-based, exclusive)
        public const double HeaderRow0Pt = 20.0;
        public const double HeaderRow1Pt = 20.0;
        public const double HeaderRow2Pt = 20.0;
        public const double DataRowPt = 20.0;  // 宋体 14pt，与模板 LineUP第三版_CAD 一致
        public const int DefaultDataRows = 38; // 3 + 38 = 41 行/页，单页清晰饱满

        public static long ColWidthEmu(int colIndex) {
            double w = colIndex >= 0 && colIndex < ColWidths.Length ? ColWidths[colIndex] : 9.0;
            // 标准 OOXML 96dpi 近似：字符宽 * 7 + 5 px
            long px = (long)Math.Floor(w * 7.0 + 5.0);
            return px * EmuPerPx;
        }

        public static long ColLeftEmu(int colIndex) {
            long x = 0;
            for (int i = 0; i < colIndex; i++) x += ColWidthEmu(i);
            return x;
        }

        public static long RowHeightEmu(int offsetInPage) {
            double pt = offsetInPage == 0 ? HeaderRow0Pt : offsetInPage == 1 ? HeaderRow1Pt : offsetInPage == 2 ? HeaderRow2Pt : DataRowPt;
            return (long)Math.Round(pt * EmuPerPt);
        }

        public static long RowTopEmu(int offsetInPage) {
            long y = 0;
            for (int i = 0; i < offsetInPage; i++) y += RowHeightEmu(i);
            return y;
        }

        public static LineupCellAnchor ToAnchor(long xEmu, long yEmuInPage, int pageStartRow1Based, int pageRowCount) {
            int col = 0;
            long remX = Math.Max(0, xEmu);
            while (col < ColWidths.Length - 1) {
                long cw = ColWidthEmu(col);
                if (remX < cw) break;
                remX -= cw;
                col++;
            }
            int rowOffIdx = 0;
            long remY = Math.Max(0, yEmuInPage);
            while (rowOffIdx < pageRowCount - 1) {
                long rh = RowHeightEmu(rowOffIdx);
                if (remY < rh) break;
                remY -= rh;
                rowOffIdx++;
            }
            return new LineupCellAnchor {
                Col = col,
                ColOff = remX,
                Row = (pageStartRow1Based - 1) + rowOffIdx,
                RowOff = remY
            };
        }

        public static LineupCadPagePlan Solve(
            LineupCadPage page,
            int pageStartRow1Based,
            IDictionary<string, LineupRecord> recordsById,
            IDictionary<string, LineupRecord> recordsByNum,
            IDictionary<string, int> bomRowByNum) {

            var pts = (page != null && page.Points != null) ? page.Points : new List<LineupLocationPoint>();
            var left = new List<LineupLocationPoint>();
            var right = new List<LineupLocationPoint>();

            // 1. 按左右侧划分：尊重手工指定 Side，否则 X < 0.5 放左栏，X >= 0.5 放右栏；未识别点均匀分配到较少一侧
            foreach (var pt in pts) {
                string side = (pt.Side ?? "").Trim().ToLowerInvariant();
                if (side == "left") left.Add(pt);
                else if (side == "right") right.Add(pt);
                else if (pt.State == "ok") {
                    if (pt.X < 0.5) left.Add(pt); else right.Add(pt);
                } else {
                    if (left.Count <= right.Count) left.Add(pt); else right.Add(pt);
                }
            }

            // 若单侧严重超出默认行数（38）而另一侧有富余，仅把未手工锁定的居中点适当均衡
            int maxSide = Math.Max(left.Count, right.Count);
            if (maxSide > DefaultDataRows && Math.Min(left.Count, right.Count) < DefaultDataRows) {
                if (left.Count > right.Count) Rebalance(left, right, true);
                else Rebalance(right, left, false);
            }

            int dataRows = Math.Max(DefaultDataRows, Math.Max(left.Count, right.Count) + 2);
            int totalRows = 3 + dataRows;

            // 2. 计算图片在 D..I × DataRows 区域内等比居中矩形
            long areaLeft = ColLeftEmu(ImageFirstCol);
            long areaRight = ColLeftEmu(ImageLastColExcl);
            long areaTop = RowTopEmu(3);
            long areaBot = RowTopEmu(totalRows);
            long padX = 18 * EmuPerPx;
            long padY = 14 * EmuPerPx;
            long availW = Math.Max(EmuPerPx * 40, (areaRight - areaLeft) - padX * 2);
            long availH = Math.Max(EmuPerPx * 40, (areaBot - areaTop) - padY * 2);

            double srcW = (page != null && page.Width > 0) ? page.Width : 1600.0;
            double srcH = (page != null && page.Height > 0) ? page.Height : 1000.0;
            double scale = Math.Min(availW / srcW, availH / srcH);
            long imgW = (long)Math.Round(srcW * scale);
            long imgH = (long)Math.Round(srcH * scale);
            long imgL = areaLeft + padX + (availW - imgW) / 2;
            long imgT = areaTop + padY + (availH - imgH) / 2;

            var plan = new LineupCadPagePlan {
                Page = page,
                StartRow = pageStartRow1Based,
                RowCount = totalRows,
                DataStartRow = pageStartRow1Based + 3,
                DataEndRow = pageStartRow1Based + totalRows - 1,
                ImageLeftEmu = imgL,
                ImageTopEmu = imgT,
                ImageWidthEmu = imgW,
                ImageHeightEmu = imgH,
                TotalWidthEmu = ColLeftEmu(ColWidths.Length),
                TotalHeightEmu = RowTopEmu(totalRows),
                ImageFrom = ToAnchor(imgL, imgT, pageStartRow1Based, totalRows),
                ImageTo = ToAnchor(imgL + imgW, imgT + imgH, pageStartRow1Based, totalRows)
            };

            // 3. 左右两侧各自按 Y 坐标升序排序，做无交叉单调行分配
            LayoutSide(plan, left, "left", recordsById, recordsByNum, bomRowByNum);
            LayoutSide(plan, right, "right", recordsById, recordsByNum, bomRowByNum);
            return plan;
        }

        static void Rebalance(List<LineupLocationPoint> heavy, List<LineupLocationPoint> light, bool heavyIsLeft) {
            while (heavy.Count > DefaultDataRows && light.Count < DefaultDataRows) {
                LineupLocationPoint candidate = null;
                foreach (var p in heavy) {
                    if (!string.IsNullOrEmpty(p.Side)) continue;
                    if (candidate == null) candidate = p;
                    else if (heavyIsLeft ? p.X > candidate.X : p.X < candidate.X) candidate = p;
                }
                if (candidate == null) break;
                heavy.Remove(candidate);
                light.Add(candidate);
            }
        }

        static void LayoutSide(
            LineupCadPagePlan plan,
            List<LineupLocationPoint> points,
            string side,
            IDictionary<string, LineupRecord> recordsById,
            IDictionary<string, LineupRecord> recordsByNum,
            IDictionary<string, int> bomRowByNum) {

            if (points.Count == 0) return;

            // 稳定排序：已识别点按 Y 升序（同 Y 按靠近边缘优先），未识别点排在底部
            var ordered = points
                .OrderBy(p => p.State == "ok" ? 0 : 1)
                .ThenBy(p => p.State == "ok" ? p.Y : 2.0)
                .ThenBy(p => side == "left" ? p.X : -p.X)
                .ToList();

            int n = ordered.Count;
            int minRow = plan.DataStartRow;
            int maxRow = plan.DataEndRow;
            int[] ideal = new int[n];
            long rowH = RowHeightEmu(3);
            long pageDataTop = RowTopEmu(3);

            for (int i = 0; i < n; i++) {
                var p = ordered[i];
                if (p.State == "ok") {
                    long targetY = plan.ImageTopEmu + (long)Math.Round(Clamp01(p.Y) * plan.ImageHeightEmu);
                    int offset = (int)Math.Round((targetY - pageDataTop - rowH / 2.0) / (double)rowH);
                    ideal[i] = Math.Max(minRow, Math.Min(maxRow, minRow + offset));
                } else {
                    ideal[i] = maxRow;
                }
            }

            // 单调无重叠行求解：保证 assigned[0] < assigned[1] < ... < assigned[n-1]，且尽量贴近 ideal[i]
            int[] assigned = SolveMonotonicRows(ideal, minRow, maxRow);

            // 箭头方向：从三维图上的器件落点指向两侧编号单元格（左栏终点 = C 列右缘即 col 3；右栏终点 = J 列左缘即 col 9）
            long cellEdgeX = side == "left" ? ColLeftEmu(ImageFirstCol) : ColLeftEmu(ImageLastColExcl);

            for (int i = 0; i < n; i++) {
                var pt = ordered[i];
                int excelRow = assigned[i];
                int rowOffset = excelRow - plan.StartRow;

                LineupRecord rec = ResolveRecord(pt, recordsById, recordsByNum);
                string num = rec != null && !string.IsNullOrWhiteSpace(rec.Number) ? rec.Number.Trim() : (pt.Number ?? "").Trim();
                string desc = rec != null ? (rec.Description ?? "") : (pt.Description ?? "");
                string addr = rec != null ? (rec.Address ?? "") : "";
                int bomRow = 0;
                if (bomRowByNum != null && num.Length > 0) bomRowByNum.TryGetValue(num, out bomRow);

                var c = new LineupCadCallout {
                    Point = pt,
                    Side = side,
                    ExcelRow = excelRow,
                    BomRow = bomRow,
                    Number = num,
                    Description = desc,
                    Address = addr,
                    Tag = pt.Tag ?? "",
                    HasArrow = pt.State == "ok"
                };

                if (c.HasArrow) {
                    long sx = plan.ImageLeftEmu + (long)Math.Round(Clamp01(pt.X) * plan.ImageWidthEmu);
                    long sy = plan.ImageTopEmu + (long)Math.Round(Clamp01(pt.Y) * plan.ImageHeightEmu);
                    long ex = cellEdgeX;
                    long ey = RowTopEmu(rowOffset) + RowHeightEmu(rowOffset) / 2;
                    c.StartX = sx; c.StartY = sy; c.EndX = ex; c.EndY = ey;

                    long minX = Math.Min(sx, ex), maxX = Math.Max(sx, ex);
                    long minY = Math.Min(sy, ey), maxY = Math.Max(sy, ey);
                    // 防止完全水平/垂直退化导致 ext=0 某些查看器不渲染箭头
                    if (maxX == minX) maxX = minX + EmuPerPx;
                    if (maxY == minY) maxY = minY + EmuPerPx;

                    c.FromAnchor = ToAnchor(minX, minY, plan.StartRow, plan.RowCount);
                    c.ToAnchor = ToAnchor(maxX, maxY, plan.StartRow, plan.RowCount);
                    c.FlipH = ex < sx;
                    c.FlipV = ey < sy;
                }
                plan.Callouts.Add(c);
            }
        }

        // Pool-Adjacent-Violators (PAV) 最小二乘单调行求解：在满足 r[i] >= r[i-1] + 1 的约束下，最小化 Σ(r[i] - ideal[i])²
        public static int[] SolveMonotonicRows(int[] ideal, int minRow, int maxRow) {
            int n = ideal.Length;
            if (n == 0) return new int[0];
            // 变换 z[i] = ideal[i] - i，问题转为单调不减 z'[0] <= z'[1] <= ... <= z'[n-1]
            var blocks = new List<double[]>(); // [startIdx, count, sumZ]
            for (int i = 0; i < n; i++) {
                double z = ideal[i] - i;
                blocks.Add(new[] { (double)i, 1.0, z });
                while (blocks.Count >= 2) {
                    var last = blocks[blocks.Count - 1];
                    var prev = blocks[blocks.Count - 2];
                    if (prev[2] / prev[1] > last[2] / last[1]) {
                        prev[1] += last[1];
                        prev[2] += last[2];
                        blocks.RemoveAt(blocks.Count - 1);
                    } else break;
                }
            }
            var res = new int[n];
            foreach (var b in blocks) {
                int s = (int)b[0];
                int cnt = (int)b[1];
                int avg = (int)Math.Round(b[2] / b[1]);
                for (int k = 0; k < cnt; k++) res[s + k] = avg + s + k;
            }
            // 边界夹紧并保持严格递增
            if (res[0] < minRow) {
                int shift = minRow - res[0];
                for (int i = 0; i < n; i++) res[i] += shift;
            }
            if (res[n - 1] > maxRow) {
                res[n - 1] = maxRow;
                for (int i = n - 2; i >= 0; i--) if (res[i] >= res[i + 1]) res[i] = res[i + 1] - 1;
            }
            for (int i = 0; i < n; i++) {
                int lo = minRow + i;
                int hi = maxRow - (n - 1 - i);
                if (res[i] < lo) res[i] = lo;
                if (res[i] > hi) res[i] = hi;
            }
            return res;
        }

        static LineupRecord ResolveRecord(LineupLocationPoint pt, IDictionary<string, LineupRecord> byId, IDictionary<string, LineupRecord> byNum) {
            LineupRecord rec = null;
            if (byId != null && !string.IsNullOrEmpty(pt.Id) && byId.TryGetValue(pt.Id, out rec)) return rec;
            if (byNum != null && !string.IsNullOrWhiteSpace(pt.Number) && byNum.TryGetValue(pt.Number.Trim(), out rec)) return rec;
            return null;
        }

        static double Clamp01(double v) { return v < 0 ? 0 : (v > 1 ? 1 : v); }
    }
}
