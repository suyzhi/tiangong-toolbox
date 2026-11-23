using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace TianGongCadSuite {
    internal sealed class LineupCellMerge {
        public int R1, C1, R2, C2;
    }

    // Lineup Excel 工作簿读取器：
    // · 明文 .xlsx / .xlsm：走零依赖纯 OpenXML 快速通道；
    // · 公司透明加密（%TSD-Header%）与旧版 .xls（OLE2）：自动改走本机 Excel / WPS 官方 COM 接口只读读取，
    //   在内存中直接提取单元格，不落盘任何明文副本；
    // · 两条通道共享同一套 BOM 解析、纵向合并单元格展开与 LineUP第三版_CAD 抬头提取逻辑。
    public static class LineupXlsxReader {
        const string NsMain = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        const string NsRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

        static readonly string[] SectionPrefixes = {
            "CYLINDER LIST",
            "PNEUMATIC SOLENOID/VALVE LIST",
            "CYLINDER POSITION SENSOR LIST",
            "GENERAL CHECK SENSOR LIST",
            "OTHER ELECTRICAL PARTS LIST"
        };

        public static bool IsTsdEncrypted(string path) {
            byte[] buf = ReadHeaderBytes(path, 16);
            return buf.Length >= 4 && buf[0] == 0x25 && buf[1] == 0x54 && buf[2] == 0x53 && buf[3] == 0x44; // "%TSD"
        }

        public static bool IsOle2File(string path) {
            byte[] buf = ReadHeaderBytes(path, 8);
            return buf.Length >= 4 && buf[0] == 0xD0 && buf[1] == 0xCF && buf[2] == 0x11 && buf[3] == 0xE0; // OLE2 (.xls / 加密容器)
        }

        static bool IsZipHeader(byte[] buf) {
            return buf != null && buf.Length >= 4 && buf[0] == 0x50 && buf[1] == 0x4B; // "PK"
        }

        static byte[] ReadHeaderBytes(string path, int count) {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return new byte[0];
            try {
                byte[] buf = new byte[count];
                int n;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) n = fs.Read(buf, 0, buf.Length);
                if (n <= 0) return new byte[0];
                if (n == count) return buf;
                byte[] trimmed = new byte[n];
                Array.Copy(buf, trimmed, n);
                return trimmed;
            } catch {
                return new byte[0];
            }
        }

        // 默认开启 Excel / WPS 官方 COM 只读通道；纯单元测试可临时关闭以验证无 Office 时的兜底提示
        internal static bool EnableComFallback = true;

        public static LineupImport Read(string path, bool allowDuplicates = false) {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new FileNotFoundException("找不到指定的 Lineup Excel 文件。", path);

            byte[] head = ReadHeaderBytes(path, 16);
            bool isTsd = head.Length >= 4 && head[0] == 0x25 && head[1] == 0x54 && head[2] == 0x53 && head[3] == 0x44;
            bool isZip = IsZipHeader(head);
            string ext = (Path.GetExtension(path) ?? "").ToLowerInvariant();

            // 明文 OpenXML（.xlsx / .xlsm 且以 PK 开头）：走零依赖快速通道
            if (!isTsd && isZip && ext != ".xls") {
                try {
                    return ReadOpenXml(path, allowDuplicates);
                } catch (InvalidDataException) {
                    throw;
                } catch (InvalidOperationException) {
                    throw;
                } catch {
                    // 非标准压缩包或文件被独占锁定时，改走本机 Excel / WPS 只读通道兜底
                }
            }

            // 防止将截断/伪造的极短 %TSD 文件头交给本机表格或加解密驱动导致异常卡死
            if (isTsd && new FileInfo(path).Length < 256) {
                throw new InvalidDataException(
                    "该文件受公司透明加密保护（%TSD-Header%），但文件头不完整或已损坏，无法读取。\n"
                    + "请改在 WPS / Excel 中打开原始文件并复制 LineUP第二版_BOM，回到插件点「更多 ▾ → 导入 / 导出 → 从剪贴板粘贴整张清单」。");
            }
            if (!EnableComFallback) {
                if (isTsd) {
                    throw new InvalidDataException(
                        "该文件受公司透明加密保护（%TSD-Header%），当前未启用本机 Excel / WPS 读取通道。\n"
                        + "请在 WPS / Excel 中打开该文件，复制 LineUP第二版_BOM 后点「更多 ▾ → 导入 / 导出 → 从剪贴板粘贴整张清单」。");
                }
                throw new InvalidDataException("文件不是有效的明文 .xlsx / .xlsm（OpenXML）格式。");
            }

            // 公司透明加密（%TSD-Header%）、旧版 .xls 或其他加密/复合容器：走官方 Excel / WPS COM 只读读取
            return LineupExcelComReader.Read(path, allowDuplicates, isTsd);
        }

        static LineupImport ReadOpenXml(string path, bool allowDuplicates) {
            byte[] bytes;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) {
                bytes = new byte[fs.Length];
                int read = 0;
                while (read < bytes.Length) {
                    int n = fs.Read(bytes, read, bytes.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
            }
            if (!IsZipHeader(bytes)) {
                throw new InvalidDataException("文件不是有效的 .xlsx / .xlsm（OpenXML）格式。");
            }

            using (var ms = new MemoryStream(bytes))
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Read)) {
                var sst = ReadSharedStrings(zip);
                var strikeStyles = ReadStrikethroughStyleIndices(zip);
                var sheets = ReadSheetTargets(zip);
                if (sheets.Count == 0) throw new InvalidDataException("工作簿中没有找到任何工作表。");

                var names = sheets.Select(s => s.Key).ToList();
                int bomIdx = PickBomSheetIndex(names);
                var result = ParseBomSheet(zip, sheets[bomIdx].Value, sst, strikeStyles, allowDuplicates);

                int cadIdx = PickCadSheetIndex(names);
                if (cadIdx >= 0) ReadCadMetadata(zip, sheets[cadIdx].Value, sst, result);

                int logIdx = PickChangelogSheetIndex(names);
                if (logIdx >= 0) ReadChangelogSheet(zip, sheets[logIdx].Value, sst, result);

                return result;
            }
        }

        // 优先找「LineUP第二版_BOM」（允许前后空格），其次包含「第二版」或「BOM」的页签，兜底取第 1 张表
        internal static int PickBomSheetIndex(IList<string> sheetNames) {
            if (sheetNames == null || sheetNames.Count == 0) return -1;
            for (int i = 0; i < sheetNames.Count; i++) {
                string s = (sheetNames[i] ?? "").Trim();
                if (s.IndexOf("第二版", StringComparison.OrdinalIgnoreCase) >= 0) return i;
            }
            for (int i = 0; i < sheetNames.Count; i++) {
                string s = (sheetNames[i] ?? "").Trim();
                if (s.IndexOf("BOM", StringComparison.OrdinalIgnoreCase) >= 0) return i;
            }
            return 0;
        }

        // 找「LineUP第三版_CAD」（跳过副本页签）
        internal static int PickCadSheetIndex(IList<string> sheetNames) {
            if (sheetNames == null || sheetNames.Count == 0) return -1;
            for (int i = 0; i < sheetNames.Count; i++) {
                string s = sheetNames[i] ?? "";
                if (s.IndexOf("第三版_CAD", StringComparison.OrdinalIgnoreCase) >= 0 && !s.Contains("(2)")) return i;
            }
            return -1;
        }

        // 找「LineUP更新日志」（跳过副本页签）
        internal static int PickChangelogSheetIndex(IList<string> sheetNames) {
            if (sheetNames == null || sheetNames.Count == 0) return -1;
            for (int i = 0; i < sheetNames.Count; i++) {
                string s = sheetNames[i] ?? "";
                if (s.IndexOf("更新日志", StringComparison.OrdinalIgnoreCase) >= 0 && !s.Contains("(2)")) return i;
            }
            for (int i = 0; i < sheetNames.Count; i++) {
                string s = sheetNames[i] ?? "";
                if (s.IndexOf("日志", StringComparison.OrdinalIgnoreCase) >= 0 && !s.Contains("(2)")) return i;
            }
            return -1;
        }

        // 检测某表格引擎是否未解密、直接把 %TSD 密文头当纯文本读进了单元格
        internal static bool LooksLikeRawTsdGrid(SortedDictionary<int, Dictionary<int, string>> grid) {
            if (grid == null || grid.Count == 0) return false;
            foreach (var rowKv in grid.Take(3)) {
                foreach (var cellKv in rowKv.Value) {
                    string v = (cellKv.Value ?? "").TrimStart();
                    if (v.StartsWith("%TSD", StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            return false;
        }

        static LineupImport ParseBomSheet(ZipArchive zip, string entryPath, List<string> sst, HashSet<int> strikeStyles, bool allowDuplicates) {
            var doc = LoadXml(zip, entryPath);
            if (doc == null) throw new InvalidDataException("无法读取工作表 XML：" + entryPath);

            var nsm = MakeNsm(doc);
            // 1. 读全部单元格到稀疏表 (row -> col -> value)，并识别按公司规范带删除线的“取消元件”行
            var grid = new SortedDictionary<int, Dictionary<int, string>>();
            var struckRows = new HashSet<int>();
            foreach (XmlElement rowEl in doc.SelectNodes("//m:sheetData/m:row", nsm)) {
                int r;
                if (!int.TryParse(rowEl.GetAttribute("r"), out r)) continue;
                var rowMap = new Dictionary<int, string>();
                bool rowStruck = false;
                foreach (XmlElement cEl in rowEl.SelectNodes("m:c", nsm)) {
                    string cref = cEl.GetAttribute("r");
                    int col = ParseColIndex(cref);
                    if (col < 0 || col >= 15) continue;
                    string val = ReadCellValue(cEl, sst, nsm);
                    if (!string.IsNullOrEmpty(val)) {
                        rowMap[col] = val;
                        if ((col == 0 || col == 3) && strikeStyles != null && strikeStyles.Count > 0) {
                            int sIdx;
                            if (int.TryParse(cEl.GetAttribute("s"), out sIdx) && strikeStyles.Contains(sIdx)) rowStruck = true;
                        }
                    }
                }
                if (rowStruck) struckRows.Add(r);
                if (rowMap.Count > 0) grid[r] = rowMap;
            }

            // 2. 收集单列纵向合并单元格
            var merges = new List<LineupCellMerge>();
            foreach (XmlElement mc in doc.SelectNodes("//m:mergeCells/m:mergeCell", nsm)) {
                string rref = mc.GetAttribute("ref");
                int r1, c1, r2, c2;
                if (!ParseRange(rref, out r1, out c1, out r2, out c2)) continue;
                if (c1 == c2 && r2 > r1 && c1 < 15) {
                    merges.Add(new LineupCellMerge { R1 = r1, C1 = c1, R2 = r2, C2 = c2 });
                }
            }

            return ParseBomGrid(grid, merges, allowDuplicates, struckRows);
        }

        // OpenXML 与 Excel/WPS COM 两条通道共用的 BOM 稀疏网格解析器
        internal static LineupImport ParseBomGrid(
            SortedDictionary<int, Dictionary<int, string>> grid,
            IEnumerable<LineupCellMerge> merges,
            bool allowDuplicates,
            HashSet<int> struckRows = null) {
            if (grid == null) grid = new SortedDictionary<int, Dictionary<int, string>>();

            // 1. 展开纵向合并单元格（安装方式列阀岛/汇流排、备注列模块名等）
            if (merges != null) {
                foreach (var m in merges) {
                    if (m == null || m.C1 != m.C2 || m.R2 <= m.R1 || m.C1 < 0 || m.C1 >= 15) continue;
                    Dictionary<int, string> headRow;
                    string topVal;
                    if (!grid.TryGetValue(m.R1, out headRow) || !headRow.TryGetValue(m.C1, out topVal) || string.IsNullOrWhiteSpace(topVal)) continue;
                    for (int rr = m.R1 + 1; rr <= m.R2; rr++) {
                        Dictionary<int, string> targetRow;
                        if (!grid.TryGetValue(rr, out targetRow)) continue; // 空行不填，避免凭空造数据行
                        if (!targetRow.ContainsKey(m.C1) || string.IsNullOrWhiteSpace(targetRow[m.C1])) targetRow[m.C1] = topVal;
                    }
                }
            }

            // 2. 逐行解析五大类目与列映射
            var result = new LineupImport();
            int[] mapping = null; // index in LineupTableData.Headers (0..10) -> Excel col index (0-based, -1 = absent)
            string category = "";

            foreach (var kv in grid) {
                var rowMap = kv.Value;
                Func<int, string> getCol = c => {
                    string v; return (c >= 0 && rowMap.TryGetValue(c, out v)) ? v.Trim() : "";
                };

                // 检测表头行（同时兼容公司 V3.0 中英双语模板表头如「SK编号No.」「型号\nModel number」与旧版纯中文表头）
                if (mapping == null) {
                    var candidate = TryMapBomHeaders(rowMap);
                    if (candidate != null) {
                        mapping = candidate;
                        continue;
                    }
                }

                string col0 = getCol(0);
                int secIdx = Array.FindIndex(SectionPrefixes, p => col0.StartsWith(p, StringComparison.OrdinalIgnoreCase));
                if (secIdx >= 0) {
                    category = LineupTableData.Categories[secIdx];
                    var secCells = new string[11];
                    for (int i = 0; i < 11; i++) secCells[i] = mapping != null ? getCol(mapping[i]) : getCol(i);
                    result.Sections.Add(new LineupSection { Category = category, Cells = secCells });
                    continue;
                }

                if (mapping == null || string.IsNullOrEmpty(category)) continue;
                // 跳过按公司规范加了删除线的“取消元件”历史留档行，避免二次导入时复活已取消器件
                if (struckRows != null && struckRows.Contains(kv.Key)) continue;

                var vals = new string[11];
                for (int i = 0; i < 11; i++) vals[i] = getCol(mapping[i]);
                // 跳过完全空白或只有纵向合并传下来的安装方式/备注、却没有编号/描述/型号的纯空行
                if (string.IsNullOrWhiteSpace(vals[0]) && string.IsNullOrWhiteSpace(vals[3]) && string.IsNullOrWhiteSpace(vals[4])) continue;

                var rec = new LineupRecord { Category = category };
                LineupTableData.SetCells(rec, vals);
                result.Records.Add(rec);
            }

            if (result.Records.Count == 0) throw new InvalidDataException("未在工作簿中找到可导入的 LineUP第二版_BOM 数据行。");
            if (!allowDuplicates) LineupTableData.RejectDuplicates(result.Records);

            // 临时项目用于推断阀岛配置
            var temp = new LineupProject { Records = result.Records };
            LineupTableData.InferHierarchy(temp);
            result.Islands = temp.Islands;
            return result;
        }

        // 将单行单元格映射为 LineupTableData.Headers（0..10）对应的列下标；找不到「编号」与「型号」返回 null
        static int[] TryMapBomHeaders(Dictionary<int, string> rowMap) {
            int[] map = new int[LineupTableData.Headers.Length];
            for (int i = 0; i < map.Length; i++) map[i] = -1;

            foreach (var cell in rowMap.OrderBy(kv => kv.Key)) {
                int fieldIdx = MatchHeaderFieldIndex(cell.Value);
                if (fieldIdx >= 0 && map[fieldIdx] < 0) map[fieldIdx] = cell.Key;
            }
            if (map[0] < 0 || map[4] < 0) return null;
            return map;
        }

        static int MatchHeaderFieldIndex(string raw) {
            if (string.IsNullOrWhiteSpace(raw)) return -1;
            string s = raw.Replace("\r\n", "\n").Trim();
            int nl = s.IndexOf('\n');
            string first = (nl >= 0 ? s.Substring(0, nl) : s).Trim();
            string compact = Regex.Replace(first, @"\s+", "");

            if (compact == "编号" || compact.StartsWith("编号") || compact.Equals("SK编号", StringComparison.OrdinalIgnoreCase) || compact.StartsWith("SK编号", StringComparison.OrdinalIgnoreCase)) return 0;
            if (compact == "地址" || compact.StartsWith("地址")) return 1;
            if (compact == "功能参数" || compact == "规格参数" || compact.StartsWith("功能参数")) return 2;
            if (compact == "描述" || compact.StartsWith("描述")) return 3;
            if (compact == "型号" || compact.StartsWith("型号")) return 4;
            if (compact == "品牌" || compact.StartsWith("品牌")) return 5;
            if (compact == "原位" || compact.StartsWith("原位")) return 6;
            if (compact == "安装方式" || compact.StartsWith("安装方式")) return 7;
            if (compact == "机构名称" || compact == "机构") return 8;
            if (compact == "传感器接口类型" || compact == "接口类型" || compact.StartsWith("传感器接口")) return 9;
            if (compact == "备注" || compact.StartsWith("备注")) return 10;
            return -1;
        }

        static void ReadCadMetadata(ZipArchive zip, string entryPath, List<string> sst, LineupImport result) {
            try {
                var doc = LoadXml(zip, entryPath);
                if (doc == null) return;
                var nsm = MakeNsm(doc);
                var cadGrid = new SortedDictionary<int, Dictionary<int, string>>();
                for (int r = 1; r <= 5; r++) {
                    var rowEl = doc.SelectSingleNode("//m:sheetData/m:row[@r='" + r + "']", nsm) as XmlElement;
                    if (rowEl == null) continue;
                    var cells = new Dictionary<int, string>();
                    foreach (XmlElement cEl in rowEl.SelectNodes("m:c", nsm)) {
                        int col = ParseColIndex(cEl.GetAttribute("r"));
                        string v = ReadCellValue(cEl, sst, nsm).Trim();
                        if (col >= 0 && v.Length > 0) cells[col] = v;
                    }
                    if (cells.Count > 0) cadGrid[r] = cells;
                }
                ApplyCadMetadataRows(cadGrid, result);
            } catch { }
        }

        internal static void ApplyCadMetadataRows(SortedDictionary<int, Dictionary<int, string>> cadGrid, LineupImport result) {
            if (cadGrid == null || result == null) return;
            foreach (var rowKv in cadGrid) {
                var cells = rowKv.Value;
                foreach (var kv in cells) {
                    string label = (kv.Value ?? "").Trim();
                    string nextVal;
                    if (!cells.TryGetValue(kv.Key + 1, out nextVal)) continue;
                    nextVal = (nextVal ?? "").Trim();
                    if (nextVal.Length == 0) continue;
                    if (label.Contains("项目编号") && string.IsNullOrEmpty(result.ProjectCode)) result.ProjectCode = nextVal;
                    else if (label.Contains("工位号") && string.IsNullOrEmpty(result.StationCode)) result.StationCode = nextVal;
                    else if (label.Contains("线体号") && string.IsNullOrEmpty(result.LineCode)) result.LineCode = nextVal;
                }
            }
        }

        static void ReadChangelogSheet(ZipArchive zip, string entryPath, List<string> sst, LineupImport result) {
            try {
                var doc = LoadXml(zip, entryPath);
                if (doc == null) return;
                var nsm = MakeNsm(doc);
                var logGrid = new SortedDictionary<int, Dictionary<int, string>>();
                foreach (XmlElement rowEl in doc.SelectNodes("//m:sheetData/m:row", nsm)) {
                    int r;
                    if (!int.TryParse(rowEl.GetAttribute("r"), out r) || r > 200) continue;
                    var cells = new Dictionary<int, string>();
                    foreach (XmlElement cEl in rowEl.SelectNodes("m:c", nsm)) {
                        int col = ParseColIndex(cEl.GetAttribute("r"));
                        if (col < 0 || col >= 12) continue;
                        string v = ReadCellValue(cEl, sst, nsm).Trim();
                        if (v.Length > 0) cells[col] = v;
                    }
                    if (cells.Count > 0) logGrid[r] = cells;
                }
                ParseChangelogGrid(logGrid, result);
            } catch { }
        }

        // OpenXML 与 Excel/WPS COM 共用的「LineUP更新日志」解析器
        internal static void ParseChangelogGrid(SortedDictionary<int, Dictionary<int, string>> logGrid, LineupImport result) {
            if (logGrid == null || result == null || logGrid.Count == 0) return;

            int dateCol = -1, verCol = -1, modelDateCol = -1, contentCol = -1, notesCol = -1, designerCol = -1, editorCol = -1, approverCol = -1;
            int headerRow = -1;

            foreach (var rowKv in logGrid) {
                var cells = rowKv.Value;
                int dc = -1, vc = -1, mdc = -1, cc = -1, nc = -1, desc = -1, edc = -1, apc = -1;
                foreach (var kv in cells) {
                    string t = Regex.Replace((kv.Value ?? "").Trim(), @"\s+", "");
                    if (t.Length == 0) continue;
                    if (t.Contains("数模") && t.Contains("日期")) mdc = kv.Key;
                    else if (t == "日期" || (t.Contains("日期") && dc < 0)) dc = kv.Key;
                    else if (t.Contains("版本")) vc = kv.Key;
                    else if (t.Contains("更新内容") || t == "内容") cc = kv.Key;
                    else if (t.Contains("备注")) nc = kv.Key;
                    else if (t.Contains("设计")) desc = kv.Key;
                    else if (t.Contains("更新人") || t.Contains("编辑")) edc = kv.Key;
                    else if (t.Contains("确认")) apc = kv.Key;
                }
                if (vc >= 0 && (cc >= 0 || dc >= 0)) {
                    headerRow = rowKv.Key;
                    dateCol = dc; verCol = vc; modelDateCol = mdc; contentCol = cc;
                    notesCol = nc; designerCol = desc; editorCol = edc; approverCol = apc;
                    break;
                }
            }
            if (headerRow < 0) return;

            foreach (var rowKv in logGrid) {
                if (rowKv.Key <= headerRow) continue;
                var cells = rowKv.Value;
                Func<int, string> get = c => {
                    string v; return (c >= 0 && cells.TryGetValue(c, out v)) ? CleanLogCell(v) : "";
                };
                string c0 = get(0);
                if (c0.StartsWith("备注", StringComparison.Ordinal)) break;

                string date = LineupRevision.NormalizeDateString(get(dateCol));
                string ver = get(verCol);
                string mDate = LineupRevision.NormalizeDateString(get(modelDateCol));
                string content = get(contentCol);
                string notes = get(notesCol);
                string designer = get(designerCol);
                string editor = get(editorCol);
                string approver = get(approverCol);

                // 跳过只带预填序号的空白占位行
                if (date.Length == 0 && ver.Length == 0 && mDate.Length == 0 && content.Length == 0
                    && notes.Length == 0 && designer.Length == 0 && editor.Length == 0 && approver.Length == 0) continue;

                result.ChangeLogs.Add(new LineupChangeLog {
                    Date = date, Version = ver, ModelDate = mDate, Content = content,
                    Notes = notes, Designer = designer, Editor = editor, Approver = approver
                });
                if (ver.Length > 0) result.LatestRevision = ver;
                if (designer.Length > 0) result.LatestDesigner = designer;
                if (editor.Length > 0) result.LatestEditor = editor;
            }
        }

        static string CleanLogCell(string raw) {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            // 过滤 WPS 单元格嵌图公式残留（如 =DISPIMG("ID_...",1)）
            string s = Regex.Replace(raw, @"=?\[?=?DISPIMG\([^\)]*\)\]?", "", RegexOptions.IgnoreCase).Trim();
            return s;
        }

        static HashSet<int> ReadStrikethroughStyleIndices(ZipArchive zip) {
            var res = new HashSet<int>();
            try {
                var doc = LoadXml(zip, "xl/styles.xml");
                if (doc == null) return res;
                var nsm = MakeNsm(doc);
                var strikeFonts = new HashSet<int>();
                var fontNodes = doc.SelectNodes("//m:fonts/m:font", nsm);
                if (fontNodes != null) {
                    for (int i = 0; i < fontNodes.Count; i++) {
                        if (fontNodes[i].SelectSingleNode("m:strike", nsm) != null) strikeFonts.Add(i);
                    }
                }
                if (strikeFonts.Count == 0) return res;
                var xfNodes = doc.SelectNodes("//m:cellXfs/m:xf", nsm);
                if (xfNodes != null) {
                    for (int i = 0; i < xfNodes.Count; i++) {
                        var el = xfNodes[i] as XmlElement;
                        int fid;
                        if (el != null && int.TryParse(el.GetAttribute("fontId"), out fid) && strikeFonts.Contains(fid)) {
                            res.Add(i);
                        }
                    }
                }
            } catch { }
            return res;
        }

        static List<string> ReadSharedStrings(ZipArchive zip) {
            var list = new List<string>();
            var doc = LoadXml(zip, "xl/sharedStrings.xml");
            if (doc == null) return list;
            var nsm = MakeNsm(doc);
            foreach (XmlElement si in doc.SelectNodes("//m:si", nsm)) {
                var sb = new StringBuilder();
                foreach (XmlNode t in si.SelectNodes(".//m:t", nsm)) sb.Append(t.InnerText);
                list.Add(sb.ToString());
            }
            return list;
        }

        static List<KeyValuePair<string, string>> ReadSheetTargets(ZipArchive zip) {
            var res = new List<KeyValuePair<string, string>>();
            var wb = LoadXml(zip, "xl/workbook.xml");
            var rels = LoadXml(zip, "xl/_rels/workbook.xml.rels");
            if (wb == null || rels == null) return res;

            var relMap = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (XmlElement el in rels.DocumentElement.ChildNodes.OfType<XmlElement>()) {
                string id = el.GetAttribute("Id");
                string target = el.GetAttribute("Target");
                if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(target)) relMap[id] = target;
            }

            var nsm = MakeNsm(wb);
            foreach (XmlElement sh in wb.SelectNodes("//m:sheets/m:sheet", nsm)) {
                string name = sh.GetAttribute("name");
                string rid = sh.GetAttribute("id", NsRel);
                string target;
                if (string.IsNullOrEmpty(rid) || !relMap.TryGetValue(rid, out target)) continue;
                target = target.TrimStart('/');
                if (!target.StartsWith("xl/", StringComparison.OrdinalIgnoreCase)) target = "xl/" + target;
                res.Add(new KeyValuePair<string, string>(name, target));
            }
            return res;
        }

        static string ReadCellValue(XmlElement cEl, List<string> sst, XmlNamespaceManager nsm) {
            string t = cEl.GetAttribute("t");
            if (t == "inlineStr") {
                var sb = new StringBuilder();
                foreach (XmlNode tn in cEl.SelectNodes(".//m:t", nsm)) sb.Append(tn.InnerText);
                return sb.ToString();
            }
            var vEl = cEl.SelectSingleNode("m:v", nsm);
            if (vEl == null) return "";
            string raw = vEl.InnerText ?? "";
            if (t == "s") {
                int idx;
                if (int.TryParse(raw, out idx) && idx >= 0 && idx < sst.Count) return sst[idx];
                return "";
            }
            return raw;
        }

        static XmlDocument LoadXml(ZipArchive zip, string path) {
            var entry = zip.Entries.FirstOrDefault(e => string.Equals(e.FullName.Replace('\\', '/'), path, StringComparison.OrdinalIgnoreCase));
            if (entry == null) return null;
            var doc = new XmlDocument { XmlResolver = null };
            using (var s = entry.Open()) doc.Load(s);
            return doc;
        }

        static XmlNamespaceManager MakeNsm(XmlDocument doc) {
            var nsm = new XmlNamespaceManager(doc.NameTable);
            nsm.AddNamespace("m", NsMain);
            nsm.AddNamespace("r", NsRel);
            return nsm;
        }

        static int ParseColIndex(string cellRef) {
            if (string.IsNullOrEmpty(cellRef)) return -1;
            int col = 0;
            for (int i = 0; i < cellRef.Length; i++) {
                char c = cellRef[i];
                if (c >= 'A' && c <= 'Z') col = col * 26 + (c - 'A' + 1);
                else if (c >= 'a' && c <= 'z') col = col * 26 + (c - 'a' + 1);
                else break;
            }
            return col - 1;
        }

        static bool ParseRange(string rangeRef, out int r1, out int c1, out int r2, out int c2) {
            r1 = c1 = r2 = c2 = -1;
            if (string.IsNullOrEmpty(rangeRef)) return false;
            var m = Regex.Match(rangeRef, @"^([A-Z]+)([0-9]+):([A-Z]+)([0-9]+)$", RegexOptions.IgnoreCase);
            if (!m.Success) return false;
            c1 = ParseColIndex(m.Groups[1].Value);
            r1 = int.Parse(m.Groups[2].Value);
            c2 = ParseColIndex(m.Groups[3].Value);
            r2 = int.Parse(m.Groups[4].Value);
            return c1 >= 0 && c2 >= 0 && r1 > 0 && r2 > 0;
        }
    }
}
