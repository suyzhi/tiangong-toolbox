using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace TianGongCadSuite {
    // 纯 OpenXML Lineup 工作簿读取器（支持明文 .xlsx / .xlsm，零 Office / WPS COM 依赖）。
    // 自动检测公司透明加密头（%TSD-Header-###%），兼容 10 列与 11 列 LineUP第二版_BOM，
    // 展开安装方式 / 备注列纵向合并单元格，并读取 LineUP第三版_CAD 抬头信息与阀岛配置。
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
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
            byte[] buf = new byte[16];
            int n;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) n = fs.Read(buf, 0, buf.Length);
            if (n < 4) return false;
            return buf[0] == 0x25 && buf[1] == 0x54 && buf[2] == 0x53 && buf[3] == 0x44; // "%TSD"
        }

        public static LineupImport Read(string path, bool allowDuplicates = false) {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new FileNotFoundException("找不到指定的 Lineup Excel 文件。", path);
            if (IsTsdEncrypted(path)) {
                throw new InvalidDataException(
                    "该文件受公司透明加密保护（%TSD-Header%），插件无法直接读取磁盘密文。\n"
                    + "请用以下任一方式导入：\n"
                    + "1. 在 WPS / Excel 中打开该文件，全选 LineUP第二版_BOM 复制，回到插件点「导入导出 → 粘贴整张五类清单」；\n"
                    + "2. 或先使用已解密的明文 .xlsx / .xlsm 副本导入。");
            }

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
            if (bytes.Length < 4 || bytes[0] != 0x50 || bytes[1] != 0x4B) {
                throw new InvalidDataException("文件不是有效的 .xlsx / .xlsm（OpenXML）格式。");
            }

            using (var ms = new MemoryStream(bytes))
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Read)) {
                var sst = ReadSharedStrings(zip);
                var sheets = ReadSheetTargets(zip);
                if (sheets.Count == 0) throw new InvalidDataException("工作簿中没有找到任何工作表。");

                // 优先找「LineUP第二版_BOM」（允许前后空格），其次包含「第二版」或「BOM」的页签
                KeyValuePair<string, string> bomSheet = sheets.FirstOrDefault(s => s.Key.Trim().IndexOf("第二版", StringComparison.OrdinalIgnoreCase) >= 0);
                if (string.IsNullOrEmpty(bomSheet.Value)) bomSheet = sheets.FirstOrDefault(s => s.Key.Trim().IndexOf("BOM", StringComparison.OrdinalIgnoreCase) >= 0);
                if (string.IsNullOrEmpty(bomSheet.Value)) bomSheet = sheets[0];

                var result = ParseBomSheet(zip, bomSheet.Value, sst, allowDuplicates);

                // 顺便从「LineUP第三版_CAD」读取项目编号 / 工位号 / 线体号
                var cadSheet = sheets.FirstOrDefault(s => s.Key.IndexOf("第三版_CAD", StringComparison.OrdinalIgnoreCase) >= 0 && !s.Key.Contains("(2)"));
                if (!string.IsNullOrEmpty(cadSheet.Value)) ReadCadMetadata(zip, cadSheet.Value, sst, result);

                return result;
            }
        }

        static LineupImport ParseBomSheet(ZipArchive zip, string entryPath, List<string> sst, bool allowDuplicates) {
            var doc = LoadXml(zip, entryPath);
            if (doc == null) throw new InvalidDataException("无法读取工作表 XML：" + entryPath);

            var nsm = MakeNsm(doc);
            // 1. 读全部单元格到稀疏表 (row -> col -> value)
            var grid = new SortedDictionary<int, Dictionary<int, string>>();
            foreach (XmlElement rowEl in doc.SelectNodes("//m:sheetData/m:row", nsm)) {
                int r;
                if (!int.TryParse(rowEl.GetAttribute("r"), out r)) continue;
                var rowMap = new Dictionary<int, string>();
                foreach (XmlElement cEl in rowEl.SelectNodes("m:c", nsm)) {
                    string cref = cEl.GetAttribute("r");
                    int col = ParseColIndex(cref);
                    if (col < 0 || col >= 15) continue;
                    string val = ReadCellValue(cEl, sst, nsm);
                    if (!string.IsNullOrEmpty(val)) rowMap[col] = val;
                }
                if (rowMap.Count > 0) grid[r] = rowMap;
            }

            // 2. 展开纵向合并单元格（安装方式列阀岛/汇流排、备注列模块名等）
            foreach (XmlElement mc in doc.SelectNodes("//m:mergeCells/m:mergeCell", nsm)) {
                string rref = mc.GetAttribute("ref");
                int r1, c1, r2, c2;
                if (!ParseRange(rref, out r1, out c1, out r2, out c2)) continue;
                // 只展开单列纵向合并（c1 == c2 且 r2 > r1），不展开横向分区标题合并
                if (c1 != c2 || r2 <= r1 || c1 >= 15) continue;
                Dictionary<int, string> headRow;
                string topVal;
                if (!grid.TryGetValue(r1, out headRow) || !headRow.TryGetValue(c1, out topVal) || string.IsNullOrWhiteSpace(topVal)) continue;
                for (int rr = r1 + 1; rr <= r2; rr++) {
                    Dictionary<int, string> targetRow;
                    if (!grid.TryGetValue(rr, out targetRow)) continue; // 空行不填，避免凭空造数据行
                    if (!targetRow.ContainsKey(c1) || string.IsNullOrWhiteSpace(targetRow[c1])) targetRow[c1] = topVal;
                }
            }

            // 3. 逐行解析五大类目与列映射
            var result = new LineupImport();
            int[] mapping = null; // index in LineupTableData.Headers (0..10) -> Excel col index (0-based, -1 = absent)
            string category = "";

            foreach (var kv in grid) {
                var rowMap = kv.Value;
                Func<int, string> getCol = c => {
                    string v; return (c >= 0 && rowMap.TryGetValue(c, out v)) ? v.Trim() : "";
                };

                // 检测表头行
                if (mapping == null) {
                    var values = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    foreach (var cell in rowMap) {
                        string t = cell.Value.Trim();
                        if (t.Length > 0 && !values.ContainsKey(t)) values[t] = cell.Key;
                    }
                    if (values.ContainsKey("编号") && values.ContainsKey("型号")) {
                        mapping = new int[LineupTableData.Headers.Length];
                        for (int i = 0; i < LineupTableData.Headers.Length; i++) {
                            int idx;
                            mapping[i] = values.TryGetValue(LineupTableData.Headers[i], out idx) ? idx : -1;
                        }
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

        static void ReadCadMetadata(ZipArchive zip, string entryPath, List<string> sst, LineupImport result) {
            try {
                var doc = LoadXml(zip, entryPath);
                if (doc == null) return;
                var nsm = MakeNsm(doc);
                for (int r = 1; r <= 5; r++) {
                    var rowEl = doc.SelectSingleNode("//m:sheetData/m:row[@r='" + r + "']", nsm) as XmlElement;
                    if (rowEl == null) continue;
                    var cells = new SortedDictionary<int, string>();
                    foreach (XmlElement cEl in rowEl.SelectNodes("m:c", nsm)) {
                        int col = ParseColIndex(cEl.GetAttribute("r"));
                        string v = ReadCellValue(cEl, sst, nsm).Trim();
                        if (col >= 0 && v.Length > 0) cells[col] = v;
                    }
                    foreach (var kv in cells) {
                        string nextVal;
                        if (!cells.TryGetValue(kv.Key + 1, out nextVal)) continue;
                        if (kv.Value.Contains("项目编号") && string.IsNullOrEmpty(result.ProjectCode)) result.ProjectCode = nextVal;
                        else if (kv.Value.Contains("工位号") && string.IsNullOrEmpty(result.StationCode)) result.StationCode = nextVal;
                        else if (kv.Value.Contains("线体号") && string.IsNullOrEmpty(result.LineCode)) result.LineCode = nextVal;
                    }
                }
            } catch { }
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
