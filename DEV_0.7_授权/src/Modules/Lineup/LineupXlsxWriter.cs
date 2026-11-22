using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;

namespace TianGongCadSuite {
    public sealed class LineupXlsxOptions {
        public bool Export11Columns;       // false = 标准 10 列（与 OK200/OK270/C11 一致），true = 11 列（含机构名称）
        public bool IncludeCadPages = true;
        public bool HighlightAudit = false;
        public string CadAssetsDir = "";   // 可选覆盖图片查找目录（默认取 AssemblyFile 旁 .lineup.cad）
        public string VersionNote = "";
    }

    public sealed class LineupXlsxSummary {
        public string OutputPath = "";
        public int BomRows;
        public int CadPages;
        public int CadCallouts;
        public int CadArrows;
        public int AuditErrors;
        public int AuditWarnings;
    }

    // 纯 OpenXML 公司标准 Lineup 工作簿生成器（零 Office / WPS COM 依赖，仅用 System.IO.Compression + System.Xml）。
    // 一键输出完整 5 个标准页签：
    //   1. LineUP更新日志
    //   2. LineUP第一版_配置表（按类别与型号自动统计）
    //   3. LineUP第二版_BOM（5 大类目分区、Y 阀岛间隔空行、安装方式列同阀岛/汇流排纵向合并、冻结与自动筛选）
    //   4. LineUP第三版_CAD（多页 14 列标准版式、等比高清图、无交叉左右排布、VLOOKUP 实时联动 BOM、原生红色箭头）
    //   5. 签字页（嵌入公司自检表并带入自动检查结论）
    public static class LineupXlsxWriter {
        const string NsMain = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        const string NsRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        const string NsPkgRel = "http://schemas.openxmlformats.org/package/2006/relationships";
        const string NsXdr = "http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing";
        const string NsA = "http://schemas.openxmlformats.org/drawingml/2006/main";
        const string BomSheetName = "LineUP第二版_BOM";

        static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        sealed class SstBuilder {
            readonly List<string> items = new List<string>();
            readonly Dictionary<string, int> index = new Dictionary<string, int>(StringComparer.Ordinal);
            public int Add(string s) {
                s = SanitizeXml(s ?? "");
                int i;
                if (index.TryGetValue(s, out i)) return i;
                i = items.Count;
                items.Add(s);
                index[s] = i;
                return i;
            }
            public string ToXml() {
                var sb = new StringBuilder();
                sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
                sb.Append("<sst xmlns=\"").Append(NsMain).Append("\" count=\"").Append(items.Count).Append("\" uniqueCount=\"").Append(items.Count).Append("\">");
                foreach (var s in items) {
                    sb.Append("<si><t xml:space=\"preserve\">").Append(Esc(s)).Append("</t></si>");
                }
                sb.Append("</sst>");
                return sb.ToString();
            }
        }

        sealed class MediaItem {
            public string RelId = "";
            public string ZipPath = "";
            public string Ext = "png";
            public byte[] Bytes;
        }

        public static LineupXlsxSummary Write(string outputPath, LineupProject project, LineupXlsxOptions options = null) {
            if (string.IsNullOrWhiteSpace(outputPath)) throw new ArgumentException("输出路径为空。");
            if (project == null) throw new ArgumentNullException("project");
            options = options ?? new LineupXlsxOptions { Export11Columns = project.Export11Columns };

            var audit = LineupAudit.Run(project);
            var sst = new SstBuilder();
            var media = new List<MediaItem>();

            // 1. 先生成 BOM，拿到 编号 -> BOM 行号 的精确映射，供 CAD 页公式联动
            Dictionary<string, int> bomRowByNum;
            int bomDataCount;
            string bomXml = BuildBomSheet(project, options, audit, sst, out bomRowByNum, out bomDataCount);

            // 2. 更新日志 & 第一版配置表 & 签字页
            string logXml = BuildChangelogSheet(project, options, sst);
            string cfgXml = BuildConfigSheet(project, sst);
            string auditXml = BuildAuditSheet(project, audit, sst);

            // 3. 第三版 CAD 位置图页 + 原生 Drawing XML（图片 + 红色箭头连接符）
            string drawingXml, drawingRelsXml;
            int cadPageCount, cadCalloutCount, cadArrowCount;
            string cadXml = BuildCadSheet(project, options, bomRowByNum, sst, media, out drawingXml, out drawingRelsXml, out cadPageCount, out cadCalloutCount, out cadArrowCount);

            bool hasDrawing = !string.IsNullOrEmpty(drawingXml);

            // 4. 打包成标准 .xlsx（先写临时文件再原子替换）
            string fullOut = Path.GetFullPath(outputPath);
            string dir = Path.GetDirectoryName(fullOut);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = fullOut + "." + Guid.NewGuid().ToString("N") + ".tmp";

            try {
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, false, Utf8NoBom)) {
                    WriteTextEntry(zip, "[Content_Types].xml", BuildContentTypes(hasDrawing, media));
                    WriteTextEntry(zip, "_rels/.rels", BuildRootRels());
                    WriteTextEntry(zip, "xl/workbook.xml", BuildWorkbookXml());
                    WriteTextEntry(zip, "xl/_rels/workbook.xml.rels", BuildWorkbookRels());
                    WriteTextEntry(zip, "xl/styles.xml", BuildStylesXml());
                    WriteTextEntry(zip, "xl/sharedStrings.xml", sst.ToXml());
                    WriteTextEntry(zip, "xl/worksheets/sheet1.xml", logXml);
                    WriteTextEntry(zip, "xl/worksheets/sheet2.xml", cfgXml);
                    WriteTextEntry(zip, "xl/worksheets/sheet3.xml", bomXml);
                    WriteTextEntry(zip, "xl/worksheets/sheet4.xml", cadXml);
                    WriteTextEntry(zip, "xl/worksheets/sheet5.xml", auditXml);
                    if (hasDrawing) {
                        WriteTextEntry(zip, "xl/worksheets/_rels/sheet4.xml.rels", BuildSheetDrawingRels());
                        WriteTextEntry(zip, "xl/drawings/drawing1.xml", drawingXml);
                        WriteTextEntry(zip, "xl/drawings/_rels/drawing1.xml.rels", drawingRelsXml);
                        foreach (var m in media) WriteBytesEntry(zip, m.ZipPath, m.Bytes);
                    }
                }
                if (File.Exists(fullOut)) File.Delete(fullOut);
                File.Move(tmp, fullOut);
            } finally {
                if (File.Exists(tmp)) try { File.Delete(tmp); } catch { }
            }

            return new LineupXlsxSummary {
                OutputPath = fullOut,
                BomRows = bomDataCount,
                CadPages = cadPageCount,
                CadCallouts = cadCalloutCount,
                CadArrows = cadArrowCount,
                AuditErrors = audit.ErrorCount,
                AuditWarnings = audit.WarningCount
            };
        }

        // ==================== Sheet 1: LineUP更新日志 ====================
        static string BuildChangelogSheet(LineupProject p, LineupXlsxOptions opt, SstBuilder sst) {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<worksheet xmlns=\"").Append(NsMain).Append("\">");
            sb.Append("<sheetPr><pageSetUpPr fitToPage=\"1\"/></sheetPr>");
            sb.Append("<sheetViews><sheetView workbookViewId=\"0\"/></sheetViews>");
            sb.Append("<sheetFormatPr defaultRowHeight=\"18\"/>");
            sb.Append("<cols>");
            double[] ws = { 7, 14, 14, 14, 48, 24, 12, 14, 24 };
            for (int i = 0; i < ws.Length; i++) sb.Append(ColDef(i + 1, ws[i]));
            sb.Append("</cols><sheetData>");

            sb.Append("<row r=\"1\" ht=\"28\" customHeight=\"1\">");
            sb.Append(CellStr("A1", "LineUP更新日志", 1, sst));
            for (int c = 2; c <= 9; c++) sb.Append(CellBlank(ColName(c) + "1", 1));
            sb.Append("</row>");

            string[] headers = { "序号", "日期", "Line-up版本号", "数模提供日期", "更新内容", "备注", "设计人", "Lineup编辑人", "确认人（机械/Lineup/电气）" };
            sb.Append("<row r=\"2\" ht=\"22\" customHeight=\"1\">");
            for (int c = 0; c < headers.Length; c++) sb.Append(CellStr(ColName(c + 1) + "2", headers[c], 2, sst));
            sb.Append("</row>");

            int today = ExcelSerialDate(DateTime.Today);
            int modelDate = today;
            try { if (!string.IsNullOrEmpty(p.AssemblyFile) && File.Exists(p.AssemblyFile)) modelDate = ExcelSerialDate(File.GetLastWriteTime(p.AssemblyFile)); } catch { }
            string content = !string.IsNullOrWhiteSpace(opt.VersionNote)
                ? opt.VersionNote
                : ((p.CadPages != null && p.CadPages.Count > 0) ? "LineUP第二版_BOM，LineUP第三版_CAD（共 " + p.CadPages.Count + " 页位置图）" : "LineUP第二版_BOM");

            sb.Append("<row r=\"3\" ht=\"22\" customHeight=\"1\">");
            sb.Append(CellNum("A3", 1, 7));
            sb.Append(CellNum("B3", today, 17));
            sb.Append(CellStr("C3", string.IsNullOrWhiteSpace(p.Revision) ? "V1.0" : p.Revision, 7, sst));
            sb.Append(CellNum("D3", modelDate, 17));
            sb.Append(CellStr("E3", content, 6, sst));
            sb.Append(CellStr("F3", "由天工工具箱自动导出", 6, sst));
            sb.Append(CellStr("G3", p.Designer ?? "", 7, sst));
            sb.Append(CellStr("H3", p.Editor ?? "", 7, sst));
            sb.Append(CellBlank("I3", 7));
            sb.Append("</row>");

            for (int r = 4; r <= 12; r++) {
                sb.Append("<row r=\"").Append(r).Append("\" ht=\"20\" customHeight=\"1\">");
                sb.Append(CellNum("A" + r, r - 2, 7));
                for (int c = 2; c <= 9; c++) sb.Append(CellBlank(ColName(c) + r, 6));
                sb.Append("</row>");
            }

            sb.Append("</sheetData><mergeCells count=\"1\"><mergeCell ref=\"A1:I1\"/></mergeCells>");
            sb.Append(LandscapePageSetup(1, 1));
            sb.Append("</worksheet>");
            return sb.ToString();
        }

        // ==================== Sheet 2: LineUP第一版_配置表 ====================
        static string BuildConfigSheet(LineupProject p, SstBuilder sst) {
            var records = (p.Records ?? new List<LineupRecord>()).Where(r => LineupTableData.Categories.Contains(LineupTableData.Category(r.Category))).ToList();
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<worksheet xmlns=\"").Append(NsMain).Append("\">");
            sb.Append("<sheetPr><pageSetUpPr fitToPage=\"1\"/></sheetPr>");
            sb.Append("<sheetViews><sheetView workbookViewId=\"0\"/></sheetViews>");
            sb.Append("<sheetFormatPr defaultRowHeight=\"18\"/>");
            sb.Append("<cols>");
            double[] ws = { 8, 30, 34, 16, 10, 20, 28 };
            for (int i = 0; i < ws.Length; i++) sb.Append(ColDef(i + 1, ws[i]));
            sb.Append("</cols><sheetData>");

            // 汇总区
            sb.Append("<row r=\"1\" ht=\"28\" customHeight=\"1\">");
            sb.Append(CellStr("A1", "LineUP第一版_配置汇总表（按第二版 BOM 自动统计）", 1, sst));
            for (int c = 2; c <= 7; c++) sb.Append(CellBlank(ColName(c) + "1", 1));
            sb.Append("</row>");

            string[] sumHdr = { "序号", "器件大类", "数量", "备注" };
            sb.Append("<row r=\"2\" ht=\"22\" customHeight=\"1\">");
            sb.Append(CellStr("A2", sumHdr[0], 2, sst));
            sb.Append(CellStr("B2", sumHdr[1], 2, sst));
            sb.Append(CellStr("C2", sumHdr[2], 2, sst));
            sb.Append(CellStr("D2", sumHdr[3], 2, sst));
            for (int c = 5; c <= 7; c++) sb.Append(CellBlank(ColName(c) + "2", 2));
            sb.Append("</row>");

            var summaryRows = new List<Tuple<string, int, string>>();
            foreach (string cat in LineupTableData.Categories) {
                int cnt = records.Count(r => LineupTableData.Category(r.Category) == cat);
                summaryRows.Add(Tuple.Create(cat, cnt, ""));
            }
            int islandCount = (p.Islands ?? new List<LineupIsland>()).Count(i => !string.IsNullOrWhiteSpace(i.Model) || i.Kind == "单片阀");
            summaryRows.Add(Tuple.Create("阀岛 / 汇流排配置数", islandCount, string.Join("；", (p.Islands ?? new List<LineupIsland>()).Where(i => !string.IsNullOrWhiteSpace(i.Key)).Select(i => i.Key + "(" + (string.IsNullOrWhiteSpace(i.Kind) ? "阀岛" : i.Kind) + ")").ToArray())));

            double mergedNoteWidth = ws[3] + ws[4] + ws[5] + ws[6];
            int row = 3;
            for (int i = 0; i < summaryRows.Count; i++) {
                double ht = EstimateRowHeight(new[] { summaryRows[i].Item1, summaryRows[i].Item3 }, new[] { ws[1], mergedNoteWidth }, 20.0);
                sb.Append("<row r=\"").Append(row).Append("\" ht=\"").Append(Fmt(ht)).Append("\" customHeight=\"1\">");
                sb.Append(CellNum("A" + row, i + 1, 7));
                sb.Append(CellStr("B" + row, summaryRows[i].Item1, 6, sst));
                sb.Append(CellNum("C" + row, summaryRows[i].Item2, 18));
                sb.Append(CellStr("D" + row, summaryRows[i].Item3, 6, sst));
                for (int c = 5; c <= 7; c++) sb.Append(CellBlank(ColName(c) + row, 6));
                sb.Append("</row>");
                row++;
            }
            int sumTotal = records.Count;
            sb.Append("<row r=\"").Append(row).Append("\" ht=\"24\" customHeight=\"1\">");
            sb.Append(CellStr("A" + row, "合计", 2, sst));
            sb.Append(CellStr("B" + row, "BOM 五大类目器件总数", 2, sst));
            sb.Append(CellFormulaNum("C" + row, "SUM(C3:C7)", sumTotal, 18));
            sb.Append(CellBlank("D" + row, 2));
            for (int c = 5; c <= 7; c++) sb.Append(CellBlank(ColName(c) + row, 2));
            sb.Append("</row>");
            row += 2;

            // 型号明细汇总区
            int detTitleRow = row;
            sb.Append("<row r=\"").Append(row).Append("\" ht=\"26\" customHeight=\"1\">");
            sb.Append(CellStr("A" + row, "按型号汇总清单", 1, sst));
            for (int c = 2; c <= 7; c++) sb.Append(CellBlank(ColName(c) + row, 1));
            sb.Append("</row>");
            row++;

            string[] detHdr = { "序号", "类别 / 名称", "型号", "品牌", "数量", "传感器接口", "所属机构 / 备注" };
            sb.Append("<row r=\"").Append(row).Append("\" ht=\"24\" customHeight=\"1\">");
            for (int c = 0; c < detHdr.Length; c++) sb.Append(CellStr(ColName(c + 1) + row, detHdr[c], 2, sst));
            sb.Append("</row>");
            row++;

            var grouped = records
                .GroupBy(r => new {
                    Cat = LineupTableData.Category(r.Category),
                    Model = (r.Model ?? "").Trim(),
                    Brand = (r.Brand ?? "").Trim(),
                    Conn = (r.Connector ?? "").Trim()
                })
                .OrderBy(g => Array.IndexOf(LineupTableData.Categories, g.Key.Cat))
                .ThenByDescending(g => g.Count())
                .ThenBy(g => g.Key.Model, StringComparer.OrdinalIgnoreCase)
                .ToList();

            int seq = 1;
            foreach (var g in grouped) {
                string names = string.Join("、", g.Select(x => (x.Description ?? "").Trim()).Where(s => s.Length > 0).Distinct().Take(3).ToArray());
                string label = g.Key.Cat + (names.Length > 0 ? " - " + names : "");
                string notes = string.Join("、", g.Select(x => !string.IsNullOrWhiteSpace(x.Notes) ? x.Notes.Trim() : (x.Mechanism ?? "").Trim()).Where(s => s.Length > 0).Distinct().Take(4).ToArray());
                double ht = EstimateRowHeight(new[] { label, g.Key.Model, g.Key.Conn, notes }, new[] { ws[1], ws[2], ws[5], ws[6] }, 20.0);
                sb.Append("<row r=\"").Append(row).Append("\" ht=\"").Append(Fmt(ht)).Append("\" customHeight=\"1\">");
                sb.Append(CellNum("A" + row, seq++, 7));
                sb.Append(CellStr("B" + row, label, 6, sst));
                sb.Append(CellStr("C" + row, g.Key.Model, 6, sst));
                sb.Append(CellStr("D" + row, g.Key.Brand, 7, sst));
                sb.Append(CellNum("E" + row, g.Count(), 18));
                sb.Append(CellStr("F" + row, g.Key.Conn, 7, sst));
                sb.Append(CellStr("G" + row, notes, 6, sst));
                sb.Append("</row>");
                row++;
            }

            sb.Append("</sheetData>");
            var merges = new List<string> { "A1:G1", "D2:G2", "A" + detTitleRow + ":G" + detTitleRow };
            for (int r = 3; r <= 3 + summaryRows.Count; r++) merges.Add("D" + r + ":G" + r);
            sb.Append("<mergeCells count=\"").Append(merges.Count).Append("\">");
            foreach (var m in merges) sb.Append("<mergeCell ref=\"").Append(m).Append("\"/>");
            sb.Append("</mergeCells>");
            sb.Append(LandscapePageSetup(1, 0));
            sb.Append("</worksheet>");
            return sb.ToString();
        }

        // ==================== Sheet 3: LineUP第二版_BOM ====================
        static string BuildBomSheet(
            LineupProject p, LineupXlsxOptions opt, LineupAuditResult audit, SstBuilder sst,
            out Dictionary<string, int> bomRowByNum, out int dataCount) {

            bomRowByNum = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            dataCount = 0;

            bool col11 = opt.Export11Columns;
            int colCount = col11 ? 11 : 10;
            string[] headers = col11 ? LineupTableData.Headers : LineupTableData.ReferenceHeaders;
            double[] widths = col11
                ? new double[] { 20, 26, 18, 30, 44, 15, 11, 24, 16, 22, 24 }
                : new double[] { 20, 26, 18, 30, 44, 15, 11, 24, 22, 24 };

            var warnIds = new HashSet<string>();
            if (opt.HighlightAudit && audit != null) {
                foreach (var iss in audit.Issues) {
                    if (iss.Severity != LineupAuditSeverity.Info && !string.IsNullOrEmpty(iss.RecordId)) warnIds.Add(iss.RecordId);
                }
            }

            var sb = new StringBuilder();
            var merges = new List<string>();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<worksheet xmlns=\"").Append(NsMain).Append("\">");
            sb.Append("<sheetPr><pageSetUpPr fitToPage=\"1\"/></sheetPr>");
            sb.Append("<sheetViews><sheetView tabSelected=\"1\" workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>");
            sb.Append("<sheetFormatPr defaultRowHeight=\"16.5\"/>");
            sb.Append("<cols>");
            for (int i = 0; i < widths.Length; i++) sb.Append(ColDef(i + 1, widths[i]));
            sb.Append("</cols><sheetData>");

            // Row 1: 冻结表头（留足自动筛选下拉箭头空间，避免「传感器接口类型」折行裁切）
            sb.Append("<row r=\"1\" ht=\"28\" customHeight=\"1\">");
            for (int c = 0; c < colCount; c++) sb.Append(CellStr(ColName(c + 1) + "1", headers[c], 2, sst));
            sb.Append("</row>");

            int row = 2;
            string lastCol = ColName(colCount);

            for (int ci = 0; ci < LineupTableData.Categories.Length; ci++) {
                string cat = LineupTableData.Categories[ci];
                var saved = (p.Sections ?? new List<LineupSection>()).LastOrDefault(s => s.Category == cat);
                string title = saved != null && saved.Cells != null && saved.Cells.Length > 0 && !string.IsNullOrWhiteSpace(saved.Cells[0])
                    ? saved.Cells[0]
                    : LineupTableData.Titles[ci];
                int bannerStyle = (ci == 0 || ci == 3) ? 3 : (ci == 1 || ci == 2) ? 4 : 5;

                // 分区标题行
                sb.Append("<row r=\"").Append(row).Append("\" ht=\"").Append(ci == 3 ? "22" : "18").Append("\" customHeight=\"1\">");
                sb.Append(CellStr("A" + row, title, bannerStyle, sst));
                if (cat == "普通传感器") {
                    for (int c = 2; c <= 6; c++) sb.Append(CellBlank(ColName(c) + row, bannerStyle));
                    string ncNote = saved != null && saved.Cells != null && saved.Cells.Length > 6 && !string.IsNullOrWhiteSpace(saved.Cells[6])
                        ? saved.Cells[6] : "传感器常开/常闭（只有常闭的需要填写）";
                    sb.Append(CellStr("G" + row, ncNote, bannerStyle, sst));
                    for (int c = 8; c <= colCount; c++) sb.Append(CellBlank(ColName(c) + row, bannerStyle));
                    merges.Add("A" + row + ":F" + row);
                    merges.Add("G" + row + ":" + lastCol + row);
                } else {
                    for (int c = 2; c <= colCount; c++) sb.Append(CellBlank(ColName(c) + row, bannerStyle));
                    merges.Add("A" + row + ":" + lastCol + row);
                }
                sb.Append("</row>");
                row++;

                var items = (p.Records ?? new List<LineupRecord>()).Where(r => LineupTableData.Category(r.Category) == cat).ToList();

                // 跟踪阀片同阀岛/汇流排纵向合并范围（H 列 = 第 8 列）
                string prevIslandKey = "";
                int mergeStartRow = -1;
                string mergeIslandLabel = "";

                for (int i = 0; i < items.Count; i++) {
                    var r = items[i];
                    string curIslandKey = (cat == "气缸" || cat == "阀片" || cat == "气缸传感器") ? LineupNumbering.IslandKey(r.Number) : "";

                    // 不同 Y 阀岛之间自动插一条视觉空行（与真实 OK170/OK200/OK270 习惯一致）
                    if (i > 0 && curIslandKey.Length > 0 && prevIslandKey.Length > 0 && !string.Equals(curIslandKey, prevIslandKey, StringComparison.OrdinalIgnoreCase)) {
                        if (cat == "阀片" && mergeStartRow > 0 && row - 1 > mergeStartRow) {
                            merges.Add("H" + mergeStartRow + ":H" + (row - 1));
                        }
                        mergeStartRow = -1; mergeIslandLabel = "";
                        row++; // 留空行
                    }

                    string[] full11 = LineupTableData.Cells(r);
                    // 阀片安装方式列处理：有阀岛/汇流排/阀组配置时在同组首行写入并纵向合并
                    bool isValveIslandMergeHead = false;
                    bool isValveIslandMergeBody = false;
                    if (cat == "阀片") {
                        string ilabel = LineupNumbering.IslandLabel(p, r);
                        if (!string.IsNullOrWhiteSpace(ilabel) && ilabel != "单片阀") {
                            if (mergeStartRow > 0 && string.Equals(ilabel, mergeIslandLabel, StringComparison.OrdinalIgnoreCase)) {
                                isValveIslandMergeBody = true;
                                full11[7] = "";
                            } else {
                                if (mergeStartRow > 0 && row - 1 > mergeStartRow) merges.Add("H" + mergeStartRow + ":H" + (row - 1));
                                mergeStartRow = row;
                                mergeIslandLabel = ilabel;
                                isValveIslandMergeHead = true;
                                full11[7] = ilabel;
                            }
                        } else {
                            if (mergeStartRow > 0 && row - 1 > mergeStartRow) merges.Add("H" + mergeStartRow + ":H" + (row - 1));
                            mergeStartRow = -1; mergeIslandLabel = "";
                            if (ilabel == "单片阀" && string.IsNullOrWhiteSpace(full11[7])) full11[7] = "单片阀";
                        }
                    }

                    string[] rowVals = col11 ? full11 : full11.Where((v, idx) => idx != 8).ToArray();
                    bool warn = warnIds.Contains(r.Id);

                    // 阀岛纵向合并格不计入单行高度，避免把整段型号撑高到单行
                    var heightVals = new string[colCount];
                    for (int c = 0; c < colCount; c++) heightVals[c] = (c == 7 && isValveIslandMergeHead) ? "" : rowVals[c];
                    double rowHt = EstimateRowHeight(heightVals, widths, 18.0);
                    sb.Append("<row r=\"").Append(row).Append("\" ht=\"").Append(Fmt(rowHt)).Append("\" customHeight=\"1\">");
                    for (int c = 0; c < colCount; c++) {
                        int st;
                        if (c == 0) st = warn ? 19 : 8;
                        else if (c == 7 && (isValveIslandMergeHead || isValveIslandMergeBody)) st = 9;
                        else if (c == 5 || c == 6 || c == 7 || (!col11 && c == 8) || (col11 && c == 9)) st = warn ? 19 : 7;
                        else st = warn ? 19 : 6;
                        string cellRef = ColName(c + 1) + row;
                        if (string.IsNullOrEmpty(rowVals[c])) sb.Append(CellBlank(cellRef, st));
                        else sb.Append(CellStr(cellRef, rowVals[c], st, sst));
                    }
                    sb.Append("</row>");

                    string numKey = (r.Number ?? "").Trim();
                    if (numKey.Length > 0 && !bomRowByNum.ContainsKey(numKey)) bomRowByNum[numKey] = row;
                    dataCount++;
                    if (curIslandKey.Length > 0) prevIslandKey = curIslandKey;
                    row++;
                }

                if (cat == "阀片" && mergeStartRow > 0 && row - 1 > mergeStartRow) {
                    merges.Add("H" + mergeStartRow + ":H" + (row - 1));
                }
                row++; // 类目间留 1 行空行
            }

            sb.Append("</sheetData>");
            int lastDataRow = Math.Max(2, row - 1);
            sb.Append("<autoFilter ref=\"A1:").Append(lastCol).Append(lastDataRow).Append("\"/>");
            if (merges.Count > 0) {
                sb.Append("<mergeCells count=\"").Append(merges.Count).Append("\">");
                foreach (var m in merges) sb.Append("<mergeCell ref=\"").Append(m).Append("\"/>");
                sb.Append("</mergeCells>");
            }
            sb.Append(LandscapePageSetup(1, 0));
            sb.Append("</worksheet>");
            return sb.ToString();
        }

        // ==================== Sheet 4: LineUP第三版_CAD ====================
        static string BuildCadSheet(
            LineupProject p, LineupXlsxOptions opt, Dictionary<string, int> bomRowByNum,
            SstBuilder sst, List<MediaItem> media,
            out string drawingXml, out string drawingRelsXml,
            out int pageCount, out int calloutCount, out int arrowCount) {

            drawingXml = ""; drawingRelsXml = "";
            pageCount = 0; calloutCount = 0; arrowCount = 0;

            var records = p.Records ?? new List<LineupRecord>();
            var byId = new Dictionary<string, LineupRecord>();
            var byNum = new Dictionary<string, LineupRecord>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in records) {
                if (!string.IsNullOrEmpty(r.Id) && !byId.ContainsKey(r.Id)) byId[r.Id] = r;
                string k = (r.Number ?? "").Trim();
                if (k.Length > 0 && !byNum.ContainsKey(k)) byNum[k] = r;
            }

            var pages = (opt.IncludeCadPages && p.CadPages != null && p.CadPages.Count > 0)
                ? p.CadPages
                : new List<LineupCadPage> { new LineupCadPage { Name = "总图", Width = 1600, Height = 1000 } };

            var sb = new StringBuilder();
            var merges = new List<string>();
            var rowBreaks = new List<int>();
            var drawAnchors = new StringBuilder();
            int shapeId = 2;

            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<worksheet xmlns=\"").Append(NsMain).Append("\" xmlns:r=\"").Append(NsRel).Append("\">");
            sb.Append("<sheetPr><pageSetUpPr fitToPage=\"1\"/></sheetPr>");
            sb.Append("<sheetViews><sheetView workbookViewId=\"0\"/></sheetViews>");
            sb.Append("<sheetFormatPr defaultRowHeight=\"").Append(Fmt(LineupCadLayout.DataRowPt)).Append("\" customHeight=\"1\"/>");
            sb.Append("<cols>");
            for (int c = 0; c < LineupCadLayout.ColWidths.Length; c++) sb.Append(ColDef(c + 1, LineupCadLayout.ColWidths[c]));
            sb.Append("</cols><sheetData>");

            int curRow = 1;
            string projCode = string.IsNullOrWhiteSpace(p.ProjectCode) ? "" : p.ProjectCode.Trim();
            string station = !string.IsNullOrWhiteSpace(p.StationCode) ? p.StationCode.Trim() : (p.NumberPrefix ?? "").Trim();
            string defLine = string.IsNullOrWhiteSpace(p.LineCode) ? "L1" : p.LineCode.Trim();
            string assetsDir = !string.IsNullOrWhiteSpace(opt.CadAssetsDir) ? opt.CadAssetsDir : LineupProject.CadAssetsDir(p.AssemblyFile);

            for (int pi = 0; pi < pages.Count; pi++) {
                var page = pages[pi];
                var plan = LineupCadLayout.Solve(page, curRow, byId, byNum, bomRowByNum);
                pageCount++;

                // 1. 写入每页顶部 3 行（插图区域 + 标题栏；每页都带左右列标题，打印翻页更清晰）
                int r0 = plan.StartRow;
                int r1 = plan.StartRow + 1;
                int r2 = plan.StartRow + 2;
                string lineCode = !string.IsNullOrWhiteSpace(page.LineCode) ? page.LineCode.Trim() : defLine;

                sb.Append("<row r=\"").Append(r0).Append("\" ht=\"").Append(Fmt(LineupCadLayout.HeaderRow0Pt)).Append("\" customHeight=\"1\">");
                sb.Append(CellStr("A" + r0, "IO点位", 2, sst));
                sb.Append(CellStr("B" + r0, "名称/描述", 2, sst));
                sb.Append(CellStr("C" + r0, "备忘", 2, sst));
                sb.Append(CellStr("D" + r0, "SK编号", 2, sst));
                sb.Append(CellStr("E" + r0, "插图区域", 12, sst));
                for (int c = 6; c <= 10; c++) sb.Append(CellBlank(ColName(c) + r0, 12));
                sb.Append(CellStr("K" + r0, "SK编号", 2, sst));
                sb.Append(CellStr("L" + r0, "备忘", 2, sst));
                sb.Append(CellStr("M" + r0, "名称/描述", 2, sst));
                sb.Append(CellStr("N" + r0, "IO点位", 2, sst));
                sb.Append("</row>");

                sb.Append("<row r=\"").Append(r1).Append("\" ht=\"").Append(Fmt(LineupCadLayout.HeaderRow1Pt)).Append("\" customHeight=\"1\">");
                for (int c = 1; c <= 4; c++) sb.Append(CellBlank(ColName(c) + r1, 2));
                sb.Append(CellStr("E" + r1, "项目编号", 10, sst));
                sb.Append(CellStr("F" + r1, projCode, 11, sst));
                sb.Append(CellBlank("G" + r1, 11));
                sb.Append(CellStr("H" + r1, "工位号", 10, sst));
                sb.Append(CellStr("I" + r1, station, 11, sst));
                sb.Append(CellBlank("J" + r1, 11));
                for (int c = 11; c <= 14; c++) sb.Append(CellBlank(ColName(c) + r1, 2));
                sb.Append("</row>");

                sb.Append("<row r=\"").Append(r2).Append("\" ht=\"").Append(Fmt(LineupCadLayout.HeaderRow2Pt)).Append("\" customHeight=\"1\">");
                for (int c = 1; c <= 4; c++) sb.Append(CellBlank(ColName(c) + r2, 2));
                sb.Append(CellStr("E" + r2, "线体号", 10, sst));
                sb.Append(CellStr("F" + r2, lineCode, 11, sst));
                sb.Append(CellBlank("G" + r2, 11));
                sb.Append(CellStr("H" + r2, "机构名称", 10, sst));
                sb.Append(CellStr("I" + r2, string.IsNullOrWhiteSpace(page.Name) ? "总图" : page.Name.Trim(), 11, sst));
                sb.Append(CellBlank("J" + r2, 11));
                for (int c = 11; c <= 14; c++) sb.Append(CellBlank(ColName(c) + r2, 2));
                sb.Append("</row>");

                merges.Add("A" + r0 + ":A" + r2);
                merges.Add("B" + r0 + ":B" + r2);
                merges.Add("C" + r0 + ":C" + r2);
                merges.Add("D" + r0 + ":D" + r2);
                merges.Add("E" + r0 + ":J" + r0);
                merges.Add("F" + r1 + ":G" + r1);
                merges.Add("I" + r1 + ":J" + r1);
                merges.Add("F" + r2 + ":G" + r2);
                merges.Add("I" + r2 + ":J" + r2);
                merges.Add("K" + r0 + ":K" + r2);
                merges.Add("L" + r0 + ":L" + r2);
                merges.Add("M" + r0 + ":M" + r2);
                merges.Add("N" + r0 + ":N" + r2);

                // 2. 索引本页左右标注行
                var leftByRow = new Dictionary<int, LineupCadCallout>();
                var rightByRow = new Dictionary<int, LineupCadCallout>();
                foreach (var co in plan.Callouts) {
                    calloutCount++;
                    if (co.Side == "left") leftByRow[co.ExcelRow] = co;
                    else rightByRow[co.ExcelRow] = co;
                }

                // 3. 写入本页全部数据行（显式固定行高，确保图片与箭头锚点精确）
                for (int r = plan.DataStartRow; r <= plan.DataEndRow; r++) {
                    LineupCadCallout lc, rc;
                    leftByRow.TryGetValue(r, out lc);
                    rightByRow.TryGetValue(r, out rc);

                    sb.Append("<row r=\"").Append(r).Append("\" ht=\"").Append(Fmt(LineupCadLayout.DataRowPt)).Append("\" customHeight=\"1\">");
                    if (lc != null) WriteLeftCalloutCells(sb, r, lc, sst);
                    else {
                        sb.Append(CellBlank("A" + r, 7)); sb.Append(CellBlank("B" + r, 6));
                        sb.Append(CellBlank("C" + r, 7)); sb.Append(CellBlank("D" + r, 7));
                    }
                    if (rc != null) WriteRightCalloutCells(sb, r, rc, sst);
                    else {
                        sb.Append(CellBlank("K" + r, 7)); sb.Append(CellBlank("L" + r, 7));
                        sb.Append(CellBlank("M" + r, 6)); sb.Append(CellBlank("N" + r, 7));
                    }
                    sb.Append("</row>");
                }

                // 4. 嵌入图片（优先解析相对路径到 assetsDir）
                string imgPath = "";
                if (!string.IsNullOrWhiteSpace(page.ImageFile)) {
                    imgPath = Path.IsPathRooted(page.ImageFile)
                        ? page.ImageFile
                        : (!string.IsNullOrEmpty(assetsDir) ? Path.Combine(assetsDir, page.ImageFile) : page.ImageFile);
                }
                if (!string.IsNullOrEmpty(imgPath) && File.Exists(imgPath)) {
                    byte[] bytes = File.ReadAllBytes(imgPath);
                    string ext = Path.GetExtension(imgPath).TrimStart('.').ToLowerInvariant();
                    if (ext != "png" && ext != "jpg" && ext != "jpeg") ext = "png";
                    string relId = "rId" + (media.Count + 1);
                    string zipPath = "xl/media/image" + (media.Count + 1) + "." + ext;
                    media.Add(new MediaItem { RelId = relId, ZipPath = zipPath, Ext = ext, Bytes = bytes });
                    drawAnchors.Append(BuildPicAnchorXml(shapeId++, "LineupView_" + (pi + 1), relId, plan.ImageFrom, plan.ImageTo));
                }

                // 5. 写入本页所有原生红色指引箭头（straightConnector1）
                foreach (var co in plan.Callouts) {
                    if (!co.HasArrow) continue;
                    arrowCount++;
                    drawAnchors.Append(BuildArrowAnchorXml(shapeId++, "LineupArrow_" + arrowCount, co));
                }

                if (pi < pages.Count - 1) rowBreaks.Add(plan.DataEndRow);
                curRow = plan.DataEndRow + 1;
            }

            sb.Append("</sheetData>");
            if (merges.Count > 0) {
                sb.Append("<mergeCells count=\"").Append(merges.Count).Append("\">");
                foreach (var m in merges) sb.Append("<mergeCell ref=\"").Append(m).Append("\"/>");
                sb.Append("</mergeCells>");
            }
            sb.Append(LandscapePageSetup(1, 0));
            if (rowBreaks.Count > 0) {
                sb.Append("<rowBreaks count=\"").Append(rowBreaks.Count).Append("\" manualBreakCount=\"").Append(rowBreaks.Count).Append("\">");
                foreach (int br in rowBreaks) sb.Append("<brk id=\"").Append(br).Append("\" max=\"16383\" man=\"1\"/>");
                sb.Append("</rowBreaks>");
            }
            if (drawAnchors.Length > 0) {
                sb.Append("<drawing r:id=\"rId1\"/>");
                drawingXml = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
                    + "<xdr:wsDr xmlns:xdr=\"" + NsXdr + "\" xmlns:a=\"" + NsA + "\" xmlns:r=\"" + NsRel + "\">"
                    + drawAnchors.ToString()
                    + "</xdr:wsDr>";
                drawingRelsXml = BuildDrawingRels(media);
            }
            sb.Append("</worksheet>");
            return sb.ToString();
        }

        static void WriteLeftCalloutCells(StringBuilder sb, int r, LineupCadCallout co, SstBuilder sst) {
            int numStyle = co.HasArrow ? 8 : 13;
            // 用 &"" 防止目标 BOM 单元格为空（如尚未填 IO 点位）时 Excel/WPS 把 VLOOKUP 空值显示成 0
            string ioFormula = "IF(D" + r + "=\"\",\"\",IFERROR(VLOOKUP(D" + r + ",'" + BomSheetName + "'!$A:$D,2,0)&\"\",\"\"))";
            string descFormula = "IF(D" + r + "=\"\",\"\",IFERROR(VLOOKUP(D" + r + ",'" + BomSheetName + "'!$A:$D,4,0)&\"\",\"\"))";
            sb.Append(CellFormulaStr("A" + r, ioFormula, co.Address, 7));
            sb.Append(CellFormulaStr("B" + r, descFormula, co.Description, 6));
            if (string.IsNullOrEmpty(co.Tag)) sb.Append(CellBlank("C" + r, 7));
            else sb.Append(CellStr("C" + r, co.Tag, 7, sst));
            if (co.BomRow > 0) sb.Append(CellFormulaStr("D" + r, "'" + BomSheetName + "'!A" + co.BomRow, co.Number, numStyle));
            else sb.Append(CellStr("D" + r, co.Number, numStyle, sst));
        }

        static void WriteRightCalloutCells(StringBuilder sb, int r, LineupCadCallout co, SstBuilder sst) {
            int numStyle = co.HasArrow ? 8 : 13;
            string descFormula = "IF(K" + r + "=\"\",\"\",IFERROR(VLOOKUP(K" + r + ",'" + BomSheetName + "'!$A:$D,4,0)&\"\",\"\"))";
            string ioFormula = "IF(K" + r + "=\"\",\"\",IFERROR(VLOOKUP(K" + r + ",'" + BomSheetName + "'!$A:$D,2,0)&\"\",\"\"))";
            if (co.BomRow > 0) sb.Append(CellFormulaStr("K" + r, "'" + BomSheetName + "'!A" + co.BomRow, co.Number, numStyle));
            else sb.Append(CellStr("K" + r, co.Number, numStyle, sst));
            if (string.IsNullOrEmpty(co.Tag)) sb.Append(CellBlank("L" + r, 7));
            else sb.Append(CellStr("L" + r, co.Tag, 7, sst));
            sb.Append(CellFormulaStr("M" + r, descFormula, co.Description, 6));
            sb.Append(CellFormulaStr("N" + r, ioFormula, co.Address, 7));
        }

        static string BuildPicAnchorXml(int id, string name, string relId, LineupCellAnchor from, LineupCellAnchor to) {
            return "<xdr:twoCellAnchor editAs=\"oneCell\">"
                + "<xdr:from><xdr:col>" + from.Col + "</xdr:col><xdr:colOff>" + from.ColOff + "</xdr:colOff><xdr:row>" + from.Row + "</xdr:row><xdr:rowOff>" + from.RowOff + "</xdr:rowOff></xdr:from>"
                + "<xdr:to><xdr:col>" + to.Col + "</xdr:col><xdr:colOff>" + to.ColOff + "</xdr:colOff><xdr:row>" + to.Row + "</xdr:row><xdr:rowOff>" + to.RowOff + "</xdr:rowOff></xdr:to>"
                + "<xdr:pic>"
                + "<xdr:nvPicPr><xdr:cNvPr id=\"" + id + "\" name=\"" + Esc(name) + "\"/><xdr:cNvPicPr><a:picLocks noChangeAspect=\"1\"/></xdr:cNvPicPr></xdr:nvPicPr>"
                + "<xdr:blipFill><a:blip r:embed=\"" + relId + "\"/><a:stretch><a:fillRect/></a:stretch></xdr:blipFill>"
                + "<xdr:spPr><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></xdr:spPr>"
                + "</xdr:pic><xdr:clientData/></xdr:twoCellAnchor>";
        }

        static string BuildArrowAnchorXml(int id, string name, LineupCadCallout co) {
            long minX = Math.Min(co.StartX, co.EndX), maxX = Math.Max(co.StartX, co.EndX);
            long minY = Math.Min(co.StartY, co.EndY), maxY = Math.Max(co.StartY, co.EndY);
            long cx = Math.Max(LineupCadLayout.EmuPerPx, maxX - minX);
            long cy = Math.Max(LineupCadLayout.EmuPerPx, maxY - minY);
            string flips = (co.FlipH ? " flipH=\"1\"" : "") + (co.FlipV ? " flipV=\"1\"" : "");
            return "<xdr:twoCellAnchor>"
                + "<xdr:from><xdr:col>" + co.FromAnchor.Col + "</xdr:col><xdr:colOff>" + co.FromAnchor.ColOff + "</xdr:colOff><xdr:row>" + co.FromAnchor.Row + "</xdr:row><xdr:rowOff>" + co.FromAnchor.RowOff + "</xdr:rowOff></xdr:from>"
                + "<xdr:to><xdr:col>" + co.ToAnchor.Col + "</xdr:col><xdr:colOff>" + co.ToAnchor.ColOff + "</xdr:colOff><xdr:row>" + co.ToAnchor.Row + "</xdr:row><xdr:rowOff>" + co.ToAnchor.RowOff + "</xdr:rowOff></xdr:to>"
                + "<xdr:cxnSp>"
                + "<xdr:nvCxnSpPr><xdr:cNvPr id=\"" + id + "\" name=\"" + Esc(name) + "\"/><xdr:cNvCxnSpPr/></xdr:nvCxnSpPr>"
                + "<xdr:spPr>"
                + "<a:xfrm" + flips + "><a:off x=\"" + minX + "\" y=\"" + minY + "\"/><a:ext cx=\"" + cx + "\" cy=\"" + cy + "\"/></a:xfrm>"
                + "<a:prstGeom prst=\"straightConnector1\"><a:avLst/></a:prstGeom>"
                + "<a:ln w=\"19050\" cap=\"flat\" cmpd=\"sng\" algn=\"ctr\">"
                + "<a:solidFill><a:srgbClr val=\"FF0000\"/></a:solidFill>"
                + "<a:prstDash val=\"solid\"/><a:round/>"
                + "<a:headEnd type=\"none\" w=\"med\" len=\"med\"/>"
                + "<a:tailEnd type=\"arrow\" w=\"med\" len=\"med\"/>"
                + "</a:ln>"
                + "</xdr:spPr>"
                + "</xdr:cxnSp><xdr:clientData/></xdr:twoCellAnchor>";
        }

        // ==================== Sheet 5: 签字页（含自动规范自检结论） ====================
        static string BuildAuditSheet(LineupProject p, LineupAuditResult audit, SstBuilder sst) {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<worksheet xmlns=\"").Append(NsMain).Append("\">");
            sb.Append("<sheetPr><pageSetUpPr fitToPage=\"1\"/></sheetPr>");
            sb.Append("<sheetViews><sheetView workbookViewId=\"0\"/></sheetViews>");
            sb.Append("<sheetFormatPr defaultRowHeight=\"20\"/>");
            sb.Append("<cols>");
            double[] ws = { 7, 22, 48, 14, 14, 18, 52 };
            for (int i = 0; i < ws.Length; i++) sb.Append(ColDef(i + 1, ws[i]));
            sb.Append("</cols><sheetData>");

            sb.Append("<row r=\"1\" ht=\"28\" customHeight=\"1\">");
            sb.Append(CellStr("A1", "LineUP 自检表（导出时由插件自动核验）", 1, sst));
            for (int c = 2; c <= 7; c++) sb.Append(CellBlank(ColName(c) + "1", 1));
            sb.Append("</row>");

            string[] hdr = { "序号", "审查范围", "自审内容", "电气确认", "机械确认", "插件自检", "明细 / 备注" };
            sb.Append("<row r=\"2\" ht=\"24\" customHeight=\"1\">");
            for (int c = 0; c < hdr.Length; c++) sb.Append(CellStr(ColName(c + 1) + "2", hdr[c], 2, sst));
            sb.Append("</row>");

            int row = 3;
            foreach (var item in audit.Checklist) {
                int st = item.AutoStatus.StartsWith("不通过") ? 16 : (item.AutoStatus.StartsWith("通过") || item.AutoStatus.StartsWith("已自动")) ? 14 : 15;
                double ht = EstimateRowHeight(new[] { item.Scope, item.Requirement, item.AutoStatus, item.Detail }, new[] { ws[1], ws[2], ws[5], ws[6] }, 24.0);
                sb.Append("<row r=\"").Append(row).Append("\" ht=\"").Append(Fmt(ht)).Append("\" customHeight=\"1\">");
                sb.Append(CellNum("A" + row, item.Seq, 7));
                sb.Append(CellStr("B" + row, item.Scope, 6, sst));
                sb.Append(CellStr("C" + row, item.Requirement, 6, sst));
                sb.Append(CellBlank("D" + row, 7));
                sb.Append(CellBlank("E" + row, 7));
                sb.Append(CellStr("F" + row, item.AutoStatus, st, sst));
                sb.Append(CellStr("G" + row, item.Detail, 6, sst));
                sb.Append("</row>");
                row++;
            }

            if (audit.Issues.Count > 0) {
                row++;
                int detTitle = row;
                sb.Append("<row r=\"").Append(row).Append("\" ht=\"26\" customHeight=\"1\">");
                sb.Append(CellStr("A" + row, "自检明细清单（错误 " + audit.ErrorCount + " 项，提醒 " + audit.WarningCount + " 项，提示 " + audit.InfoCount + " 项）", 1, sst));
                for (int c = 2; c <= 7; c++) sb.Append(CellBlank(ColName(c) + row, 1));
                sb.Append("</row>");
                row++;
                // 与上方自检表共用 7 列列宽：B(22)=编号，C(48)=检查说明，D(14)=级别，E(14)=类目，F(18)=规则，G(52)=处理状态/复核备注
                string[] dh = { "序号", "编号", "检查说明", "级别", "类目", "规则", "处理状态 / 复核备注" };
                sb.Append("<row r=\"").Append(row).Append("\" ht=\"22\" customHeight=\"1\">");
                for (int c = 0; c < dh.Length; c++) sb.Append(CellStr(ColName(c + 1) + row, dh[c], 2, sst));
                sb.Append("</row>");
                row++;
                int seq = 1;
                foreach (var iss in audit.Issues) {
                    int st = iss.Severity == LineupAuditSeverity.Error ? 16 : iss.Severity == LineupAuditSeverity.Warning ? 15 : 7;
                    string sev = iss.Severity == LineupAuditSeverity.Error ? "错误" : iss.Severity == LineupAuditSeverity.Warning ? "警告" : "提示";
                    double ht = EstimateRowHeight(new[] { iss.Number, iss.Message, iss.Category, iss.RuleCode }, new[] { ws[1], ws[2], ws[4], ws[5] }, 20.0);
                    sb.Append("<row r=\"").Append(row).Append("\" ht=\"").Append(Fmt(ht)).Append("\" customHeight=\"1\">");
                    sb.Append(CellNum("A" + row, seq++, 7));
                    sb.Append(CellStr("B" + row, iss.Number, 8, sst));
                    sb.Append(CellStr("C" + row, iss.Message, 6, sst));
                    sb.Append(CellStr("D" + row, sev, st, sst));
                    sb.Append(CellStr("E" + row, iss.Category, 7, sst));
                    sb.Append(CellStr("F" + row, iss.RuleCode, 7, sst));
                    sb.Append(CellBlank("G" + row, 6));
                    sb.Append("</row>");
                    row++;
                }
                sb.Append("</sheetData><mergeCells count=\"2\"><mergeCell ref=\"A1:G1\"/><mergeCell ref=\"A").Append(detTitle).Append(":G").Append(detTitle).Append("\"/></mergeCells>");
            } else {
                sb.Append("</sheetData><mergeCells count=\"1\"><mergeCell ref=\"A1:G1\"/></mergeCells>");
            }

            sb.Append(LandscapePageSetup(1, 0));
            sb.Append("</worksheet>");
            return sb.ToString();
        }

        // ==================== OOXML 样板与工具函数 ====================
        static string BuildContentTypes(bool hasDrawing, List<MediaItem> media) {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">");
            sb.Append("<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>");
            sb.Append("<Default Extension=\"xml\" ContentType=\"application/xml\"/>");
            sb.Append("<Default Extension=\"png\" ContentType=\"image/png\"/>");
            if (media.Any(m => m.Ext == "jpg" || m.Ext == "jpeg")) {
                sb.Append("<Default Extension=\"jpg\" ContentType=\"image/jpeg\"/>");
                sb.Append("<Default Extension=\"jpeg\" ContentType=\"image/jpeg\"/>");
            }
            sb.Append("<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>");
            sb.Append("<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");
            sb.Append("<Override PartName=\"/xl/sharedStrings.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml\"/>");
            for (int i = 1; i <= 5; i++)
                sb.Append("<Override PartName=\"/xl/worksheets/sheet").Append(i).Append(".xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
            if (hasDrawing)
                sb.Append("<Override PartName=\"/xl/drawings/drawing1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.drawing+xml\"/>");
            sb.Append("</Types>");
            return sb.ToString();
        }

        static string BuildRootRels() {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
                + "<Relationships xmlns=\"" + NsPkgRel + "\">"
                + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>"
                + "</Relationships>";
        }

        static string BuildWorkbookXml() {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
                + "<workbook xmlns=\"" + NsMain + "\" xmlns:r=\"" + NsRel + "\">"
                + "<bookViews><workbookView activeTab=\"2\"/></bookViews>"
                + "<sheets>"
                + "<sheet name=\"LineUP更新日志\" sheetId=\"1\" r:id=\"rId1\"/>"
                + "<sheet name=\"LineUP第一版_配置表\" sheetId=\"2\" r:id=\"rId2\"/>"
                + "<sheet name=\"" + BomSheetName + "\" sheetId=\"3\" r:id=\"rId3\"/>"
                + "<sheet name=\"LineUP第三版_CAD\" sheetId=\"4\" r:id=\"rId4\"/>"
                + "<sheet name=\"签字页\" sheetId=\"5\" r:id=\"rId5\"/>"
                + "</sheets>"
                + "<calcPr calcId=\"191029\" fullCalcOnLoad=\"1\"/>"
                + "</workbook>";
        }

        static string BuildWorkbookRels() {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<Relationships xmlns=\"").Append(NsPkgRel).Append("\">");
            for (int i = 1; i <= 5; i++)
                sb.Append("<Relationship Id=\"rId").Append(i).Append("\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet").Append(i).Append(".xml\"/>");
            sb.Append("<Relationship Id=\"rId6\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>");
            sb.Append("<Relationship Id=\"rId7\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings\" Target=\"sharedStrings.xml\"/>");
            sb.Append("</Relationships>");
            return sb.ToString();
        }

        static string BuildSheetDrawingRels() {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
                + "<Relationships xmlns=\"" + NsPkgRel + "\">"
                + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/drawing\" Target=\"../drawings/drawing1.xml\"/>"
                + "</Relationships>";
        }

        static string BuildDrawingRels(List<MediaItem> media) {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<Relationships xmlns=\"").Append(NsPkgRel).Append("\">");
            foreach (var m in media)
                sb.Append("<Relationship Id=\"").Append(m.RelId).Append("\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"../media/").Append(Path.GetFileName(m.ZipPath)).Append("\"/>");
            sb.Append("</Relationships>");
            return sb.ToString();
        }

        static string BuildStylesXml() {
            // Fonts: 0=宋体9, 1=宋体14B, 2=宋体11B, 3=宋体9.5B#002060, 4=宋体9.5B#3333CC, 5=宋体9.5B#1F4E78,
            //        6=宋体9.5B#000000, 7=宋体10B#002060, 8=宋体9B#274E13, 9=宋体9B#7F6000, 10=宋体9B#9C0006
            // Fills: 0=none, 1=gray125, 2=#D9E1F2, 3=#DDEBF7, 4=#FFF2CC, 5=#E7E6E6, 6=#F2F7FC, 7=#F2F2F2, 8=#FFFFFF, 9=#E2EFDA, 10=#FCE4D6
            // Borders: 0=none, 1=thin #BFBFBF
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
                + "<styleSheet xmlns=\"" + NsMain + "\">"
                + "<fonts count=\"11\">"
                + "<font><sz val=\"9\"/><name val=\"宋体\"/></font>"
                + "<font><b/><sz val=\"14\"/><name val=\"宋体\"/></font>"
                + "<font><b/><sz val=\"11\"/><name val=\"宋体\"/></font>"
                + "<font><b/><sz val=\"9\"/><color rgb=\"FF002060\"/><name val=\"宋体\"/></font>"
                + "<font><b/><sz val=\"9.5\"/><color rgb=\"FF3333CC\"/><name val=\"宋体\"/></font>"
                + "<font><b/><sz val=\"9.5\"/><color rgb=\"FF1F4E78\"/><name val=\"宋体\"/></font>"
                + "<font><b/><sz val=\"9.5\"/><name val=\"宋体\"/></font>"
                + "<font><b/><sz val=\"10\"/><color rgb=\"FF002060\"/><name val=\"宋体\"/></font>"
                + "<font><b/><sz val=\"9\"/><color rgb=\"FF274E13\"/><name val=\"宋体\"/></font>"
                + "<font><b/><sz val=\"9\"/><color rgb=\"FF7F6000\"/><name val=\"宋体\"/></font>"
                + "<font><b/><sz val=\"9\"/><color rgb=\"FF9C0006\"/><name val=\"宋体\"/></font>"
                + "</fonts>"
                + "<fills count=\"11\">"
                + "<fill><patternFill patternType=\"none\"/></fill>"
                + "<fill><patternFill patternType=\"gray125\"/></fill>"
                + "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFD9E1F2\"/></patternFill></fill>"
                + "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFDDEBF7\"/></patternFill></fill>"
                + "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFFFF2CC\"/></patternFill></fill>"
                + "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFE7E6E6\"/></patternFill></fill>"
                + "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFF2F7FC\"/></patternFill></fill>"
                + "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFF2F2F2\"/></patternFill></fill>"
                + "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFFFFFFF\"/></patternFill></fill>"
                + "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFE2EFDA\"/></patternFill></fill>"
                + "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFFCE4D6\"/></patternFill></fill>"
                + "</fills>"
                + "<borders count=\"2\">"
                + "<border><left/><right/><top/><bottom/><diagonal/></border>"
                + "<border><left style=\"thin\"><color rgb=\"FF808080\"/></left><right style=\"thin\"><color rgb=\"FF808080\"/></right><top style=\"thin\"><color rgb=\"FF808080\"/></top><bottom style=\"thin\"><color rgb=\"FF808080\"/></bottom><diagonal/></border>"
                + "</borders>"
                + "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>"
                + "<cellXfs count=\"20\">"
                + "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"><alignment vertical=\"center\"/></xf>"                                                         // 0 default
                + "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\"/></xf>" // 1 title
                + "<xf numFmtId=\"0\" fontId=\"2\" fillId=\"2\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\" wrapText=\"1\"/></xf>" // 2 header
                + "<xf numFmtId=\"0\" fontId=\"3\" fillId=\"3\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"left\" vertical=\"center\" wrapText=\"1\"/></xf>" // 3 sec navy
                + "<xf numFmtId=\"0\" fontId=\"4\" fillId=\"4\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"left\" vertical=\"center\" wrapText=\"1\"/></xf>" // 4 sec blue
                + "<xf numFmtId=\"0\" fontId=\"5\" fillId=\"5\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"left\" vertical=\"center\" wrapText=\"1\"/></xf>" // 5 sec gray
                + "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"left\" vertical=\"center\" wrapText=\"1\"/></xf>" // 6 data left
                + "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\" wrapText=\"1\"/></xf>" // 7 data center
                + "<xf numFmtId=\"0\" fontId=\"3\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\" wrapText=\"1\"/></xf>" // 8 SK number
                + "<xf numFmtId=\"0\" fontId=\"5\" fillId=\"6\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\" wrapText=\"1\"/></xf>" // 9 island merge
                + "<xf numFmtId=\"0\" fontId=\"6\" fillId=\"7\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\"/></xf>" // 10 CAD label
                + "<xf numFmtId=\"0\" fontId=\"7\" fillId=\"8\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\" wrapText=\"1\"/></xf>" // 11 CAD value
                + "<xf numFmtId=\"0\" fontId=\"2\" fillId=\"2\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\"/></xf>" // 12 CAD banner
                + "<xf numFmtId=\"0\" fontId=\"10\" fillId=\"4\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\" wrapText=\"1\"/></xf>" // 13 unresolved
                + "<xf numFmtId=\"0\" fontId=\"8\" fillId=\"9\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\"/></xf>" // 14 audit pass
                + "<xf numFmtId=\"0\" fontId=\"9\" fillId=\"4\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\"/></xf>" // 15 audit warn
                + "<xf numFmtId=\"0\" fontId=\"10\" fillId=\"10\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\"/></xf>" // 16 audit err
                + "<xf numFmtId=\"14\" fontId=\"0\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyNumberFormat=\"1\" applyFont=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\"/></xf>" // 17 date
                + "<xf numFmtId=\"1\" fontId=\"6\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyNumberFormat=\"1\" applyFont=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\"/></xf>" // 18 int count
                + "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"4\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"left\" vertical=\"center\" wrapText=\"1\"/></xf>" // 19 bom warn
                + "</cellXfs>"
                + "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>"
                + "</styleSheet>";
        }

        static string LandscapePageSetup(int fitW, int fitH) {
            return "<pageMargins left=\"0.3\" right=\"0.3\" top=\"0.4\" bottom=\"0.4\" header=\"0.2\" footer=\"0.2\"/>"
                + "<pageSetup paperSize=\"9\" orientation=\"landscape\" fitToWidth=\"" + fitW + "\" fitToHeight=\"" + fitH + "\"/>";
        }

        static string ColDef(int index1Based, double width) {
            return "<col min=\"" + index1Based + "\" max=\"" + index1Based + "\" width=\"" + Fmt(width) + "\" customWidth=\"1\"/>";
        }

        static string CellStr(string cellRef, string text, int style, SstBuilder sst) {
            int idx = sst.Add(text);
            return "<c r=\"" + cellRef + "\" s=\"" + style + "\" t=\"s\"><v>" + idx + "</v></c>";
        }

        static string CellNum(string cellRef, int value, int style) {
            return "<c r=\"" + cellRef + "\" s=\"" + style + "\"><v>" + value + "</v></c>";
        }

        static string CellBlank(string cellRef, int style) {
            return "<c r=\"" + cellRef + "\" s=\"" + style + "\"/>";
        }

        static string CellFormulaStr(string cellRef, string formula, string cachedValue, int style) {
            return "<c r=\"" + cellRef + "\" s=\"" + style + "\" t=\"str\"><f>" + Esc(formula) + "</f><v>" + Esc(SanitizeXml(cachedValue ?? "")) + "</v></c>";
        }

        static string CellFormulaNum(string cellRef, string formula, int cachedValue, int style) {
            return "<c r=\"" + cellRef + "\" s=\"" + style + "\"><f>" + Esc(formula) + "</f><v>" + cachedValue + "</v></c>";
        }

        public static string ColName(int col1Based) {
            string s = "";
            while (col1Based > 0) {
                col1Based--;
                s = (char)('A' + (col1Based % 26)) + s;
                col1Based /= 26;
            }
            return s;
        }

        static int ExcelSerialDate(DateTime date) {
            return (int)(date.Date - new DateTime(1899, 12, 30)).TotalDays;
        }

        // 按列宽估算自动换行所需行高（中文按 2 字符宽，ASCII 按 1.05 字符宽），避免导出后在 WPS/Excel 中被固定行高裁切
        static double EstimateRowHeight(string[] texts, double[] colWidths, double minPt) {
            int maxLines = 1;
            int n = Math.Min(texts.Length, colWidths.Length);
            for (int i = 0; i < n; i++) {
                string t = texts[i];
                if (string.IsNullOrWhiteSpace(t)) continue;
                double cap = Math.Max(6.0, colWidths[i] - 2.0);
                int lines = 0;
                foreach (var para in t.Replace("\r\n", "\n").Split('\n')) {
                    double units = 0;
                    for (int k = 0; k < para.Length; k++) units += (para[k] > 127 ? 2.1 : 1.08);
                    lines += Math.Max(1, (int)Math.Ceiling(units / cap));
                }
                if (lines > maxLines) maxLines = lines;
            }
            if (maxLines <= 1) return minPt;
            return Math.Min(160.0, Math.Max(minPt, maxLines * 14.5 + 5.5));
        }

        static string Fmt(double v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }

        static string Esc(string s) {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }

        static string SanitizeXml(string s) {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++) {
                char ch = s[i];
                if (ch == 0x9 || ch == 0xA || ch == 0xD || (ch >= 0x20 && ch <= 0xD7FF) || (ch >= 0xE000 && ch <= 0xFFFD)) sb.Append(ch);
            }
            return sb.ToString();
        }

        static void WriteTextEntry(ZipArchive zip, string path, string text) {
            var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
            using (var s = entry.Open()) {
                byte[] bytes = Utf8NoBom.GetBytes(text);
                s.Write(bytes, 0, bytes.Length);
            }
        }

        static void WriteBytesEntry(ZipArchive zip, string path, byte[] bytes) {
            var entry = zip.CreateEntry(path, CompressionLevel.NoCompression);
            using (var s = entry.Open()) s.Write(bytes, 0, bytes.Length);
        }
    }
}
