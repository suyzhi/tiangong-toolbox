using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace TianGongCadSuite {
    // 纯 OpenXML 读写器（仅依赖 System.IO.Compression + System.Xml，不依赖 Office / WPS COM）。
    // 直接基于内嵌的公司标准模板（或用户指定的自定义 .xlsx 模板）写出：
    //   1. 《加工件清单.xlsx》（原样保留公司 LOGO、八大类规则图、样式、公式、打印设置，支持嵌入三维缩略图）
    //   2. 《编码申请-加工件.xlsx》（原样保留 6 个工作表、批注、VLOOKUP 公式并预填缓存值）
    public static class BomXlsxWriter {
        public const string ProcessingListResource = "TianGongCadSuite.QuickBom.ProcessingListTemplate.xlsx";
        public const string CodeRequestResource = "TianGongCadSuite.QuickBom.CodeRequestTemplate.xlsx";

        const string NsMain = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        const string NsRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        const string NsPkgRel = "http://schemas.openxmlformats.org/package/2006/relationships";
        const string NsXdr = "http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing";
        const string NsA = "http://schemas.openxmlformats.org/drawingml/2006/main";
        const string RelTypeImage = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/image";

        static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        public static int ExcelSerialDate(DateTime date) {
            return (int)(date.Date - new DateTime(1899, 12, 30)).TotalDays;
        }

        // ---- 写《加工件清单.xlsx》 ----
        public static void WriteProcessingList(string outputPath, BomHeader header, IList<BomItem> items, BomOptions options) {
            if (string.IsNullOrEmpty(outputPath)) throw new ArgumentException("输出路径为空。");
            header = header ?? new BomHeader();
            items = items ?? new List<BomItem>();
            options = options ?? new BomOptions();

            byte[] tpl = LoadTemplate(options.CustomListTemplate, ProcessingListResource);
            bool withThumbs = options.IncludeThumbnails && HasAnyThumbnail(items);

            byte[] result;
            using (var srcMs = new MemoryStream(tpl))
            using (var zin = new ZipArchive(srcMs, ZipArchiveMode.Read))
            using (var dstMs = new MemoryStream()) {
                // 1. 读 sharedStrings（保留模板原有的表头文字与下标）
                var sst = ReadSharedStrings(zin);
                // 2. 读 sheet1.xml、workbook.xml、drawing1.xml、drawing1.xml.rels，以及可能含 calcChain 引用的打包元数据
                string sheetXml = ReadEntryText(zin, "xl/worksheets/sheet1.xml");
                string wbXml = ReadEntryText(zin, "xl/workbook.xml");
                string drawXml = ReadEntryText(zin, "xl/drawings/drawing1.xml");
                string drawRelsXml = ReadEntryText(zin, "xl/drawings/_rels/drawing1.xml.rels");
                bool hadCalcChain = FindEntry(zin, "xl/calcChain.xml") != null;
                string contentTypesXml = ReadEntryText(zin, "[Content_Types].xml");
                string wbRelsXml = hadCalcChain ? ReadEntryText(zin, "xl/_rels/workbook.xml.rels") : "";

                var existingEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var en in zin.Entries) existingEntries.Add(en.FullName.Replace('\\', '/'));

                var thumbEntries = new List<KeyValuePair<string, byte[]>>();
                int lastRow;
                sheetXml = PopulateProcessingSheet(sheetXml, sst, header, items, withThumbs, out lastRow);
                wbXml = UpdateWorkbookPrintArea(wbXml, lastRow, withThumbs);

                bool thumbsAdded = false;
                if (withThumbs && !string.IsNullOrEmpty(drawXml) && !string.IsNullOrEmpty(drawRelsXml)) {
                    AppendThumbnails(ref drawXml, ref drawRelsXml, items, thumbEntries, existingEntries);
                    thumbsAdded = thumbEntries.Count > 0;
                }
                bool updateContentTypes = (hadCalcChain || thumbsAdded) && !string.IsNullOrEmpty(contentTypesXml);
                if (updateContentTypes) contentTypesXml = UpdateContentTypesXml(contentTypesXml, hadCalcChain, thumbsAdded);
                if (hadCalcChain && !string.IsNullOrEmpty(wbRelsXml)) wbRelsXml = StripCalcChainRel(wbRelsXml);

                // 3. 写出新 zip（保留全部原样部件如 styles / theme / image1 / image2，替换修改过的 XML）
                using (var zout = new ZipArchive(dstMs, ZipArchiveMode.Create, true)) {
                    foreach (var entry in zin.Entries) {
                        string name = entry.FullName.Replace('\\', '/');
                        if (name.EndsWith("/", StringComparison.Ordinal)) continue;
                        if (string.Equals(name, "xl/calcChain.xml", StringComparison.OrdinalIgnoreCase)) continue;

                        if (string.Equals(name, "xl/worksheets/sheet1.xml", StringComparison.OrdinalIgnoreCase)) {
                            WriteEntryText(zout, name, sheetXml);
                        } else if (string.Equals(name, "xl/sharedStrings.xml", StringComparison.OrdinalIgnoreCase)) {
                            WriteEntryText(zout, name, BuildSharedStringsXml(sst));
                        } else if (string.Equals(name, "xl/workbook.xml", StringComparison.OrdinalIgnoreCase)) {
                            WriteEntryText(zout, name, wbXml);
                        } else if (updateContentTypes && string.Equals(name, "[Content_Types].xml", StringComparison.OrdinalIgnoreCase)) {
                            WriteEntryText(zout, name, contentTypesXml);
                        } else if (hadCalcChain && !string.IsNullOrEmpty(wbRelsXml) && string.Equals(name, "xl/_rels/workbook.xml.rels", StringComparison.OrdinalIgnoreCase)) {
                            WriteEntryText(zout, name, wbRelsXml);
                        } else if (thumbsAdded && string.Equals(name, "xl/drawings/drawing1.xml", StringComparison.OrdinalIgnoreCase)) {
                            WriteEntryText(zout, name, drawXml);
                        } else if (thumbsAdded && string.Equals(name, "xl/drawings/_rels/drawing1.xml.rels", StringComparison.OrdinalIgnoreCase)) {
                            WriteEntryText(zout, name, drawRelsXml);
                        } else {
                            CopyEntry(entry, zout, name);
                        }
                    }
                    foreach (var kv in thumbEntries) {
                        WriteEntryBytes(zout, kv.Key, kv.Value);
                    }
                }
                result = dstMs.ToArray();
            }

            WriteFileAtomic(outputPath, result);
        }

        // ---- 写《编码申请-加工件.xlsx》 ----
        public static void WriteCodeRequest(string outputPath, IList<BomItem> items, string customTemplatePath) {
            if (string.IsNullOrEmpty(outputPath)) throw new ArgumentException("输出路径为空。");
            items = items ?? new List<BomItem>();
            byte[] tpl = LoadTemplate(customTemplatePath, CodeRequestResource);

            byte[] result;
            using (var srcMs = new MemoryStream(tpl))
            using (var zin = new ZipArchive(srcMs, ZipArchiveMode.Read))
            using (var dstMs = new MemoryStream()) {
                var sst = ReadSharedStrings(zin);
                var matMap = ReadLookupSheet(zin, "xl/worksheets/sheet3.xml", sst);
                var surfMap = ReadLookupSheet(zin, "xl/worksheets/sheet4.xml", sst);
                bool hadCalcChain = FindEntry(zin, "xl/calcChain.xml") != null;
                string contentTypesXml = hadCalcChain ? ReadEntryText(zin, "[Content_Types].xml") : "";
                string wbRelsXml = hadCalcChain ? ReadEntryText(zin, "xl/_rels/workbook.xml.rels") : "";
                if (hadCalcChain && !string.IsNullOrEmpty(contentTypesXml)) contentTypesXml = UpdateContentTypesXml(contentTypesXml, true, false);
                if (hadCalcChain && !string.IsNullOrEmpty(wbRelsXml)) wbRelsXml = StripCalcChainRel(wbRelsXml);

                string sheetXml = ReadEntryText(zin, "xl/worksheets/sheet1.xml");
                sheetXml = PopulateCodeRequestSheet(sheetXml, sst, items, matMap, surfMap);

                using (var zout = new ZipArchive(dstMs, ZipArchiveMode.Create, true)) {
                    foreach (var entry in zin.Entries) {
                        string name = entry.FullName.Replace('\\', '/');
                        if (name.EndsWith("/", StringComparison.Ordinal)) continue;
                        if (string.Equals(name, "xl/calcChain.xml", StringComparison.OrdinalIgnoreCase)) continue;

                        if (string.Equals(name, "xl/worksheets/sheet1.xml", StringComparison.OrdinalIgnoreCase)) {
                            WriteEntryText(zout, name, sheetXml);
                        } else if (string.Equals(name, "xl/sharedStrings.xml", StringComparison.OrdinalIgnoreCase)) {
                            WriteEntryText(zout, name, BuildSharedStringsXml(sst));
                        } else if (hadCalcChain && !string.IsNullOrEmpty(contentTypesXml) && string.Equals(name, "[Content_Types].xml", StringComparison.OrdinalIgnoreCase)) {
                            WriteEntryText(zout, name, contentTypesXml);
                        } else if (hadCalcChain && !string.IsNullOrEmpty(wbRelsXml) && string.Equals(name, "xl/_rels/workbook.xml.rels", StringComparison.OrdinalIgnoreCase)) {
                            WriteEntryText(zout, name, wbRelsXml);
                        } else {
                            CopyEntry(entry, zout, name);
                        }
                    }
                }
                result = dstMs.ToArray();
            }

            WriteFileAtomic(outputPath, result);
        }

        // 读取公司 ERP 标准材料与处理方式词典（供界面下拉/校验用）
        public static void ReadErpDictionaries(string customTemplatePath, out List<string> materials, out List<string> treatments) {
            materials = new List<string>();
            treatments = new List<string>();
            try {
                byte[] tpl = LoadTemplate(customTemplatePath, CodeRequestResource);
                using (var ms = new MemoryStream(tpl))
                using (var z = new ZipArchive(ms, ZipArchiveMode.Read)) {
                    var sst = ReadSharedStrings(z);
                    var m = ReadLookupSheet(z, "xl/worksheets/sheet3.xml", sst);
                    var t = ReadLookupSheet(z, "xl/worksheets/sheet4.xml", sst);
                    foreach (var k in m.Keys) materials.Add(k);
                    foreach (var k in t.Keys) treatments.Add(k);
                }
            } catch (Exception e) {
                Log.Write("BomErpDict", e.Message);
            }
        }

        // ---- 加工件清单 XML 填充 ----
        static string PopulateProcessingSheet(string xml, SharedStrings sst, BomHeader h, IList<BomItem> items, bool withThumbs, out int lastRow) {
            var doc = new XmlDocument { PreserveWhitespace = false };
            doc.LoadXml(xml);
            var nsm = new XmlNamespaceManager(doc.NameTable);
            nsm.AddNamespace("m", NsMain);

            var sheetData = doc.SelectSingleNode("//m:sheetData", nsm);
            if (sheetData == null) throw new InvalidDataException("模板缺少 sheetData。");

            // 更新表头格（1~3 行）
            SetFormulaCell(doc, sheetData, nsm, 1, "N1", "10", "COUNTIF(C:C,\"<>\")-3", items.Count.ToString(CultureInfo.InvariantCulture));
            SetStringCell(doc, sheetData, nsm, 1, "Q1", "2", h.EngineerRegion, sst);

            SetStringCell(doc, sheetData, nsm, 2, "C2", "14", h.Customer, sst);
            SetStringCell(doc, sheetData, nsm, 2, "E2", "16", h.ProjectCode, sst);
            SetStringCell(doc, sheetData, nsm, 2, "H2", "15", h.LineNo, sst);
            SetNumberCell(doc, sheetData, nsm, 2, "N2", "20", ExcelSerialDate(h.DeliveryDate).ToString(CultureInfo.InvariantCulture));
            SetStringCell(doc, sheetData, nsm, 2, "Q2", "3", h.EngineerName, sst);

            SetStringCell(doc, sheetData, nsm, 3, "C3", "14", h.ProjectName, sst);
            SetStringCell(doc, sheetData, nsm, 3, "E3", "24", string.IsNullOrWhiteSpace(h.ProjectAttr) ? "加工件" : h.ProjectAttr, sst);
            SetStringCell(doc, sheetData, nsm, 3, "H3", "16", h.StationNo, sst);
            SetStringCell(doc, sheetData, nsm, 3, "I3", "26", h.MechanismNo, sst);
            SetNumberCell(doc, sheetData, nsm, 3, "N3", "20", ExcelSerialDate(h.IssueDate).ToString(CultureInfo.InvariantCulture));

            // 清除原有第 5 行及以后
            var toRemove = new List<XmlNode>();
            foreach (XmlNode rn in sheetData.SelectNodes("m:row", nsm)) {
                int r;
                if (int.TryParse(Attr(rn, "r"), out r) && r >= 5) toRemove.Add(rn);
            }
            foreach (var rn in toRemove) sheetData.RemoveChild(rn);

            // 若嵌入缩略图，调宽 T 列（第 20 列）
            if (withThumbs) {
                var cols = doc.SelectSingleNode("//m:cols", nsm);
                if (cols != null) {
                    foreach (XmlNode cn in cols.SelectNodes("m:col", nsm)) {
                        int mn, mx;
                        if (int.TryParse(Attr(cn, "min"), out mn) && int.TryParse(Attr(cn, "max"), out mx) && mn <= 20 && mx >= 20) {
                            ((XmlElement)cn).SetAttribute("width", "18");
                            ((XmlElement)cn).SetAttribute("customWidth", "1");
                        }
                    }
                }
            }

            // 写数据行（至少保留 10 行带框线的标准表体，方便打印与手补）
            int minBlankRows = Math.Max(items.Count, 10);
            lastRow = 4 + minBlankRows;
            string rowHeight = withThumbs ? "62" : "35";

            for (int i = 0; i < minBlankRows; i++) {
                int r = 5 + i;
                var rowEl = doc.CreateElement("row", NsMain);
                rowEl.SetAttribute("r", r.ToString(CultureInfo.InvariantCulture));
                rowEl.SetAttribute("ht", i < items.Count ? rowHeight : "35");
                rowEl.SetAttribute("customHeight", "1");
                rowEl.SetAttribute("spans", "1:20");

                if (i < items.Count) {
                    var it = items[i];
                    AppendNumCell(doc, rowEl, "A" + r, "38", (i + 1).ToString(CultureInfo.InvariantCulture));
                    AppendStrCell(doc, rowEl, "B" + r, "39", it.PartName, sst);
                    AppendStrCell(doc, rowEl, "C" + r, "39", it.DrawingNo, sst);
                    AppendStrCell(doc, rowEl, "D" + r, "39", it.Material, sst);
                    AppendNumCell(doc, rowEl, "E" + r, "40", Math.Max(1, it.Quantity).ToString(CultureInfo.InvariantCulture));
                    AppendNumCell(doc, rowEl, "F" + r, "41", (it.Category >= 1 && it.Category <= 8 ? it.Category : 4).ToString(CultureInfo.InvariantCulture));
                    AppendStrCell(doc, rowEl, "G" + r, "39", it.SurfaceTreatment, sst);
                    AppendStrCell(doc, rowEl, "H" + r, "41", it.Batch, sst);
                    AppendStrCell(doc, rowEl, "I" + r, "41", it.Priority, sst);
                    AppendStrCell(doc, rowEl, "J" + r, "29", it.Remark, sst);
                    AppendEmptyCell(doc, rowEl, "K" + r, "29");
                    AppendStrCell(doc, rowEl, "L" + r, "36", it.ChangeReason, sst);
                    AppendEmptyCell(doc, rowEl, "M" + r, "36");
                    AppendEmptyCell(doc, rowEl, "N" + r, "42");
                    AppendEmptyCell(doc, rowEl, "O" + r, "42");
                    AppendStrCell(doc, rowEl, "P" + r, "43", it.Code, sst);
                    AppendStrCell(doc, rowEl, "Q" + r, "39", it.LookupDrawingNo, sst);
                    AppendStrCell(doc, rowEl, "R" + r, "39", it.LookupPartName, sst);
                    AppendStrCell(doc, rowEl, "S" + r, "36", it.TagCode, sst);
                    AppendEmptyCell(doc, rowEl, "T" + r, "12");
                } else {
                    AppendEmptyCell(doc, rowEl, "A" + r, "38");
                    AppendEmptyCell(doc, rowEl, "B" + r, "39");
                    AppendEmptyCell(doc, rowEl, "C" + r, "39");
                    AppendEmptyCell(doc, rowEl, "D" + r, "39");
                    AppendEmptyCell(doc, rowEl, "E" + r, "40");
                    AppendEmptyCell(doc, rowEl, "F" + r, "41");
                    AppendEmptyCell(doc, rowEl, "G" + r, "39");
                    AppendEmptyCell(doc, rowEl, "H" + r, "41");
                    AppendEmptyCell(doc, rowEl, "I" + r, "41");
                    AppendEmptyCell(doc, rowEl, "J" + r, "29");
                    AppendEmptyCell(doc, rowEl, "K" + r, "29");
                    AppendEmptyCell(doc, rowEl, "L" + r, "36");
                    AppendEmptyCell(doc, rowEl, "M" + r, "36");
                    AppendEmptyCell(doc, rowEl, "N" + r, "42");
                    AppendEmptyCell(doc, rowEl, "O" + r, "42");
                    AppendEmptyCell(doc, rowEl, "P" + r, "43");
                    AppendEmptyCell(doc, rowEl, "Q" + r, "39");
                    AppendEmptyCell(doc, rowEl, "R" + r, "39");
                    AppendEmptyCell(doc, rowEl, "S" + r, "36");
                    AppendEmptyCell(doc, rowEl, "T" + r, "12");
                }
                sheetData.AppendChild(rowEl);
            }

            // 更新合并单元格（保留 1~4 行合并，重建第 5..lastRow 行的 J:K 和 L:M）
            var mergeNode = doc.SelectSingleNode("//m:mergeCells", nsm) as XmlElement;
            if (mergeNode == null) {
                mergeNode = doc.CreateElement("mergeCells", NsMain);
                sheetData.ParentNode.InsertAfter(mergeNode, sheetData);
            }
            var oldMerges = new List<XmlNode>();
            foreach (XmlNode mn in mergeNode.SelectNodes("m:mergeCell", nsm)) {
                string mref = Attr(mn, "ref");
                if (MergeStartsAtOrAfterRow(mref, 5)) oldMerges.Add(mn);
            }
            foreach (var mn in oldMerges) mergeNode.RemoveChild(mn);
            for (int r = 5; r <= lastRow; r++) {
                AddMerge(doc, mergeNode, "J" + r + ":K" + r);
                AddMerge(doc, mergeNode, "L" + r + ":M" + r);
            }
            mergeNode.SetAttribute("count", mergeNode.SelectNodes("m:mergeCell", nsm).Count.ToString(CultureInfo.InvariantCulture));

            // 更新 dimension
            var dim = doc.SelectSingleNode("//m:dimension", nsm) as XmlElement;
            if (dim != null) dim.SetAttribute("ref", "A1:T" + lastRow.ToString(CultureInfo.InvariantCulture));

            // 更新页脚编制人
            var oddFooter = doc.SelectSingleNode("//m:headerFooter/m:oddFooter", nsm);
            if (oddFooter != null && !string.IsNullOrWhiteSpace(h.EngineerName)) {
                oddFooter.InnerText = Regex.Replace(oddFooter.InnerText, @"编制：[^&]*", "编制：" + h.EngineerName.Trim());
            }

            return SerializeXml(doc);
        }

        static string UpdateWorkbookPrintArea(string wbXml, int lastRow, bool withThumbs) {
            if (string.IsNullOrEmpty(wbXml)) return wbXml;
            string endCol = withThumbs ? "T" : "O";
            string target = "加工件清单!$A$1:$" + endCol + "$" + lastRow.ToString(CultureInfo.InvariantCulture);
            return Regex.Replace(wbXml, @"(<definedName\s+name=""_xlnm\.Print_Area""[^>]*>).*?(</definedName>)",
                m => m.Groups[1].Value + target + m.Groups[2].Value, RegexOptions.Singleline);
        }

        static void AppendThumbnails(ref string drawXml, ref string drawRelsXml, IList<BomItem> items,
            List<KeyValuePair<string, byte[]>> thumbEntries, HashSet<string> existingEntries) {
            var dDoc = new XmlDocument { PreserveWhitespace = false };
            dDoc.LoadXml(drawXml);
            var rDoc = new XmlDocument { PreserveWhitespace = false };
            rDoc.LoadXml(drawRelsXml);

            // 扫描自定义模板里已有的 cNvPr id 与 Relationship Id，避免重号导致 Excel 打开报修复弹窗
            int nextPicId = 2000;
            foreach (XmlNode n in dDoc.GetElementsByTagName("cNvPr", NsXdr)) {
                int id;
                if (int.TryParse(Attr(n, "id"), out id) && id >= nextPicId) nextPicId = id + 1;
            }
            var usedRids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (XmlNode n in rDoc.DocumentElement.ChildNodes) {
                string id = Attr(n, "Id");
                if (id.Length > 0) usedRids.Add(id);
            }

            for (int i = 0; i < items.Count; i++) {
                var png = items[i].ThumbnailPng;
                if (png == null || png.Length == 0) continue;
                int row0 = 4 + i; // 0-based row (Excel row 5 = 4)

                int ridSeq = i + 1;
                string rid = "rIdThumb" + ridSeq.ToString(CultureInfo.InvariantCulture);
                while (!usedRids.Add(rid)) {
                    ridSeq++;
                    rid = "rIdThumb" + ridSeq.ToString(CultureInfo.InvariantCulture);
                }

                int mediaSeq = i + 1;
                string mediaName = "thumb_" + mediaSeq.ToString(CultureInfo.InvariantCulture) + ".png";
                while (existingEntries != null && !existingEntries.Add("xl/media/" + mediaName)) {
                    mediaSeq++;
                    mediaName = "thumb_" + mediaSeq.ToString(CultureInfo.InvariantCulture) + ".png";
                }
                thumbEntries.Add(new KeyValuePair<string, byte[]>("xl/media/" + mediaName, png));

                // drawing1.xml.rels
                var relEl = rDoc.CreateElement("Relationship", NsPkgRel);
                relEl.SetAttribute("Id", rid);
                relEl.SetAttribute("Type", RelTypeImage);
                relEl.SetAttribute("Target", "../media/" + mediaName);
                rDoc.DocumentElement.AppendChild(relEl);

                // drawing1.xml: twoCellAnchor inside T column (col=19), row=row0..row0+1 with small inset margin
                var anchor = dDoc.CreateElement("xdr", "twoCellAnchor", NsXdr);
                anchor.SetAttribute("editAs", "oneCell");

                var from = dDoc.CreateElement("xdr", "from", NsXdr);
                AppendXdrText(dDoc, from, "col", "19");
                AppendXdrText(dDoc, from, "colOff", "38100");
                AppendXdrText(dDoc, from, "row", row0.ToString(CultureInfo.InvariantCulture));
                AppendXdrText(dDoc, from, "rowOff", "38100");
                anchor.AppendChild(from);

                var to = dDoc.CreateElement("xdr", "to", NsXdr);
                AppendXdrText(dDoc, to, "col", "20");
                AppendXdrText(dDoc, to, "colOff", "0");
                AppendXdrText(dDoc, to, "row", (row0 + 1).ToString(CultureInfo.InvariantCulture));
                AppendXdrText(dDoc, to, "rowOff", "0");
                anchor.AppendChild(to);

                var pic = dDoc.CreateElement("xdr", "pic", NsXdr);
                var nvPicPr = dDoc.CreateElement("xdr", "nvPicPr", NsXdr);
                var cNvPr = dDoc.CreateElement("xdr", "cNvPr", NsXdr);
                cNvPr.SetAttribute("id", (nextPicId++).ToString(CultureInfo.InvariantCulture));
                cNvPr.SetAttribute("name", "缩略图 " + (i + 1).ToString(CultureInfo.InvariantCulture));
                nvPicPr.AppendChild(cNvPr);
                var cNvPicPr = dDoc.CreateElement("xdr", "cNvPicPr", NsXdr);
                var locks = dDoc.CreateElement("a", "picLocks", NsA);
                locks.SetAttribute("noChangeAspect", "1");
                cNvPicPr.AppendChild(locks);
                nvPicPr.AppendChild(cNvPicPr);
                pic.AppendChild(nvPicPr);

                var blipFill = dDoc.CreateElement("xdr", "blipFill", NsXdr);
                var blip = dDoc.CreateElement("a", "blip", NsA);
                blip.SetAttribute("embed", NsRel, rid);
                blipFill.AppendChild(blip);
                var stretch = dDoc.CreateElement("a", "stretch", NsA);
                stretch.AppendChild(dDoc.CreateElement("a", "fillRect", NsA));
                blipFill.AppendChild(stretch);
                pic.AppendChild(blipFill);

                var spPr = dDoc.CreateElement("xdr", "spPr", NsXdr);
                var prst = dDoc.CreateElement("a", "prstGeom", NsA);
                prst.SetAttribute("prst", "rect");
                prst.AppendChild(dDoc.CreateElement("a", "avLst", NsA));
                spPr.AppendChild(prst);
                pic.AppendChild(spPr);

                anchor.AppendChild(pic);
                anchor.AppendChild(dDoc.CreateElement("xdr", "clientData", NsXdr));
                dDoc.DocumentElement.AppendChild(anchor);
            }

            drawXml = SerializeXml(dDoc);
            drawRelsXml = SerializeXml(rDoc);
        }

        static void AppendXdrText(XmlDocument doc, XmlElement parent, string localName, string text) {
            var el = doc.CreateElement("xdr", localName, NsXdr);
            el.InnerText = text;
            parent.AppendChild(el);
        }

        // 同步维护 [Content_Types].xml：删掉被移除的 /xl/calcChain.xml Override，并在嵌入 PNG 缩略图时确保存在 png Default 声明
        static string UpdateContentTypesXml(string xml, bool removeCalcChain, bool ensurePng) {
            var doc = new XmlDocument { PreserveWhitespace = false };
            doc.LoadXml(xml);
            string ns = doc.DocumentElement != null ? doc.DocumentElement.NamespaceURI : "";
            var toRemove = new List<XmlNode>();
            bool hasPng = false;
            foreach (XmlNode n in doc.DocumentElement.ChildNodes) {
                if (removeCalcChain && string.Equals(n.LocalName, "Override", StringComparison.Ordinal)
                    && string.Equals(Attr(n, "PartName"), "/xl/calcChain.xml", StringComparison.OrdinalIgnoreCase)) {
                    toRemove.Add(n);
                } else if (ensurePng && string.Equals(n.LocalName, "Default", StringComparison.Ordinal)
                    && string.Equals(Attr(n, "Extension"), "png", StringComparison.OrdinalIgnoreCase)) {
                    hasPng = true;
                }
            }
            for (int i = 0; i < toRemove.Count; i++) doc.DocumentElement.RemoveChild(toRemove[i]);
            if (ensurePng && !hasPng) {
                var def = doc.CreateElement("Default", ns);
                def.SetAttribute("Extension", "png");
                def.SetAttribute("ContentType", "image/png");
                doc.DocumentElement.PrependChild(def);
            }
            return SerializeXml(doc);
        }

        // 同步清理 xl/_rels/workbook.xml.rels 中指向 calcChain.xml 的悬空关系，避免自定义 Excel 模板打开时弹损坏修复框
        static string StripCalcChainRel(string xml) {
            var doc = new XmlDocument { PreserveWhitespace = false };
            doc.LoadXml(xml);
            var toRemove = new List<XmlNode>();
            foreach (XmlNode n in doc.DocumentElement.ChildNodes) {
                if (!string.Equals(n.LocalName, "Relationship", StringComparison.Ordinal)) continue;
                string target = Attr(n, "Target");
                string type = Attr(n, "Type");
                if (target.EndsWith("calcChain.xml", StringComparison.OrdinalIgnoreCase)
                    || type.EndsWith("/calcChain", StringComparison.OrdinalIgnoreCase)) {
                    toRemove.Add(n);
                }
            }
            for (int i = 0; i < toRemove.Count; i++) doc.DocumentElement.RemoveChild(toRemove[i]);
            return SerializeXml(doc);
        }

        // ---- 编码申请表 XML 填充 ----
        static string PopulateCodeRequestSheet(string xml, SharedStrings sst, IList<BomItem> items, Dictionary<string, string> matMap, Dictionary<string, string> surfMap) {
            var doc = new XmlDocument { PreserveWhitespace = false };
            doc.LoadXml(xml);
            var nsm = new XmlNamespaceManager(doc.NameTable);
            nsm.AddNamespace("m", NsMain);

            var sheetData = doc.SelectSingleNode("//m:sheetData", nsm);
            if (sheetData == null) throw new InvalidDataException("编码申请模板缺少 sheetData。");

            var toRemove = new List<XmlNode>();
            foreach (XmlNode rn in sheetData.SelectNodes("m:row", nsm)) {
                int r;
                if (int.TryParse(Attr(rn, "r"), out r) && r >= 5) toRemove.Add(rn);
            }
            foreach (var rn in toRemove) sheetData.RemoveChild(rn);

            for (int i = 0; i < items.Count; i++) {
                int r = 5 + i;
                var it = items[i];
                var rowEl = doc.CreateElement("row", NsMain);
                rowEl.SetAttribute("r", r.ToString(CultureInfo.InvariantCulture));
                rowEl.SetAttribute("ht", "15.6");
                rowEl.SetAttribute("spans", "1:39");

                string mat = (it.Material ?? "").Trim();
                string surf = (it.SurfaceTreatment ?? "").Trim();
                int cat = it.Category >= 1 && it.Category <= 8 ? it.Category : 4;
                string catName = BomRules.ErpCategoryName(cat);
                string matCode; matMap.TryGetValue(mat, out matCode);
                string surfCode; surfMap.TryGetValue(surf, out surfCode);

                AppendStrCell(doc, rowEl, "B" + r, "24", it.PartName, sst);
                AppendStrCell(doc, rowEl, "E" + r, "25", "自制", sst);
                AppendStrCell(doc, rowEl, "F" + r, "26", "M0301", sst);
                AppendStrCell(doc, rowEl, "H" + r, "26", "JF", sst);
                AppendStrCell(doc, rowEl, "J" + r, "11", "pcs", sst);
                AppendStrCell(doc, rowEl, "K" + r, "11", "个", sst);
                AppendStrCell(doc, rowEl, "L" + r, "26", "SK-0003", sst);
                AppendStrCell(doc, rowEl, "N" + r, "24", it.DrawingNo, sst);
                AppendStrCell(doc, rowEl, "Q" + r, null, "物资", sst);
                AppendStrCell(doc, rowEl, "R" + r, "3", "DataLib", sst);
                AppendEmptyCell(doc, rowEl, "S" + r, "3");
                AppendStrCell(doc, rowEl, "AB" + r, "11", "V13", sst);
                AppendNumCell(doc, rowEl, "AE" + r, "12", "100000");

                AppendFormulaStrCell(doc, rowEl, "AF" + r, "27", "VLOOKUP(TRIM(AG" + r + "),材料!$E:$J,6,FALSE)", matCode);
                AppendStrCell(doc, rowEl, "AG" + r, "24", mat, sst);
                AppendFormulaStrCell(doc, rowEl, "AH" + r, "27", "VLOOKUP(TRIM(AI" + r + "),处理方式!$E:$J,6,FALSE)", surfCode);
                AppendStrCell(doc, rowEl, "AI" + r, "24", surf, sst);
                AppendFormulaNumCell(doc, rowEl, "AJ" + r, "28", "VLOOKUP(AK" + r + ",八大类!$A:$B,2,FALSE)", cat.ToString(CultureInfo.InvariantCulture));
                AppendStrCell(doc, rowEl, "AK" + r, null, catName, sst);
                AppendStrCell(doc, rowEl, "AL" + r, null, "是", sst);
                AppendStrCell(doc, rowEl, "AM" + r, null, "是", sst);

                sheetData.AppendChild(rowEl);
            }

            int lastRow = Math.Max(4, 4 + items.Count);
            var dim = doc.SelectSingleNode("//m:dimension", nsm) as XmlElement;
            if (dim != null) dim.SetAttribute("ref", "A1:AM" + lastRow.ToString(CultureInfo.InvariantCulture));
            var af = doc.SelectSingleNode("//m:autoFilter", nsm) as XmlElement;
            if (af != null) af.SetAttribute("ref", "A4:AM" + lastRow.ToString(CultureInfo.InvariantCulture));

            return SerializeXml(doc);
        }

        // 读取 材料 / 处理方式 表里的 E 列（长名称）→ J 列（长编码）映射
        static Dictionary<string, string> ReadLookupSheet(ZipArchive zip, string entryName, SharedStrings sst) {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string xml = ReadEntryText(zip, entryName);
            if (string.IsNullOrEmpty(xml)) return map;
            try {
                var doc = new XmlDocument();
                doc.LoadXml(xml);
                var nsm = new XmlNamespaceManager(doc.NameTable);
                nsm.AddNamespace("m", NsMain);
                foreach (XmlNode rn in doc.SelectNodes("//m:sheetData/m:row", nsm)) {
                    int r;
                    if (!int.TryParse(Attr(rn, "r"), out r) || r <= 1) continue;
                    string name = "", code = "";
                    foreach (XmlNode cn in rn.SelectNodes("m:c", nsm)) {
                        string cref = Attr(cn, "r");
                        string col = ColLetters(cref);
                        if (col == "E" || col == "D") {
                            string val = ReadCellText(cn, nsm, sst);
                            if (col == "E" && val.Length > 0) name = val;
                            else if (name.Length == 0 && val.Length > 0) name = val;
                        } else if (col == "J" || col == "C") {
                            string val = ReadCellText(cn, nsm, sst);
                            if (col == "J" && val.Length > 0) code = val;
                            else if (code.Length == 0 && val.Length > 0) code = val;
                        }
                    }
                    name = name.Trim(); code = code.Trim();
                    if (name.Length > 0 && code.Length > 0 && !map.ContainsKey(name)) map[name] = code;
                }
            } catch { }
            return map;
        }

        static string ReadCellText(XmlNode cn, XmlNamespaceManager nsm, SharedStrings sst) {
            string t = Attr(cn, "t");
            var v = cn.SelectSingleNode("m:v", nsm);
            if (t == "s" && v != null) {
                int idx;
                if (int.TryParse(v.InnerText, out idx)) return sst.Get(idx);
                return "";
            }
            if (t == "inlineStr") {
                var tn = cn.SelectSingleNode("m:is/m:t", nsm);
                return tn == null ? "" : tn.InnerText;
            }
            return v == null ? "" : v.InnerText;
        }

        // ---- 单元格操作 ----
        static XmlElement EnsureCell(XmlDocument doc, XmlNode sheetData, XmlNamespaceManager nsm, int rowNum, string cellRef, string defaultStyle) {
            XmlElement rowEl = sheetData.SelectSingleNode("m:row[@r='" + rowNum + "']", nsm) as XmlElement;
            if (rowEl == null) {
                rowEl = doc.CreateElement("row", NsMain);
                rowEl.SetAttribute("r", rowNum.ToString(CultureInfo.InvariantCulture));
                sheetData.AppendChild(rowEl);
            }
            XmlElement cEl = rowEl.SelectSingleNode("m:c[@r='" + cellRef + "']", nsm) as XmlElement;
            if (cEl == null) {
                cEl = doc.CreateElement("c", NsMain);
                cEl.SetAttribute("r", cellRef);
                if (!string.IsNullOrEmpty(defaultStyle)) cEl.SetAttribute("s", defaultStyle);
                // 按列顺序插入，保证 Excel 严格有序
                int targetCol = ColIndex(cellRef);
                XmlNode before = null;
                foreach (XmlNode existing in rowEl.SelectNodes("m:c", nsm)) {
                    if (ColIndex(Attr(existing, "r")) > targetCol) { before = existing; break; }
                }
                if (before != null) rowEl.InsertBefore(cEl, before);
                else rowEl.AppendChild(cEl);
            }
            return cEl;
        }

        static void SetStringCell(XmlDocument doc, XmlNode sheetData, XmlNamespaceManager nsm, int rowNum, string cellRef, string style, string value, SharedStrings sst) {
            var c = EnsureCell(doc, sheetData, nsm, rowNum, cellRef, style);
            c.RemoveAll();
            c.SetAttribute("r", cellRef);
            if (!string.IsNullOrEmpty(style)) c.SetAttribute("s", style);
            string s = (value ?? "").Trim();
            if (s.Length == 0) return;
            c.SetAttribute("t", "s");
            var v = doc.CreateElement("v", NsMain);
            v.InnerText = sst.Add(s).ToString(CultureInfo.InvariantCulture);
            c.AppendChild(v);
        }

        static void SetNumberCell(XmlDocument doc, XmlNode sheetData, XmlNamespaceManager nsm, int rowNum, string cellRef, string style, string numText) {
            var c = EnsureCell(doc, sheetData, nsm, rowNum, cellRef, style);
            c.RemoveAll();
            c.SetAttribute("r", cellRef);
            if (!string.IsNullOrEmpty(style)) c.SetAttribute("s", style);
            if (string.IsNullOrEmpty(numText)) return;
            var v = doc.CreateElement("v", NsMain);
            v.InnerText = numText;
            c.AppendChild(v);
        }

        static void SetFormulaCell(XmlDocument doc, XmlNode sheetData, XmlNamespaceManager nsm, int rowNum, string cellRef, string style, string formula, string cachedValue) {
            var c = EnsureCell(doc, sheetData, nsm, rowNum, cellRef, style);
            c.RemoveAll();
            c.SetAttribute("r", cellRef);
            if (!string.IsNullOrEmpty(style)) c.SetAttribute("s", style);
            var f = doc.CreateElement("f", NsMain);
            f.InnerText = formula;
            c.AppendChild(f);
            if (!string.IsNullOrEmpty(cachedValue)) {
                var v = doc.CreateElement("v", NsMain);
                v.InnerText = cachedValue;
                c.AppendChild(v);
            }
        }

        static void AppendStrCell(XmlDocument doc, XmlElement rowEl, string cellRef, string style, string value, SharedStrings sst) {
            var c = doc.CreateElement("c", NsMain);
            c.SetAttribute("r", cellRef);
            if (!string.IsNullOrEmpty(style)) c.SetAttribute("s", style);
            string s = (value ?? "").Trim();
            if (s.Length > 0) {
                c.SetAttribute("t", "s");
                var v = doc.CreateElement("v", NsMain);
                v.InnerText = sst.Add(s).ToString(CultureInfo.InvariantCulture);
                c.AppendChild(v);
            }
            rowEl.AppendChild(c);
        }

        static void AppendNumCell(XmlDocument doc, XmlElement rowEl, string cellRef, string style, string numText) {
            var c = doc.CreateElement("c", NsMain);
            c.SetAttribute("r", cellRef);
            if (!string.IsNullOrEmpty(style)) c.SetAttribute("s", style);
            if (!string.IsNullOrEmpty(numText)) {
                var v = doc.CreateElement("v", NsMain);
                v.InnerText = numText;
                c.AppendChild(v);
            }
            rowEl.AppendChild(c);
        }

        static void AppendEmptyCell(XmlDocument doc, XmlElement rowEl, string cellRef, string style) {
            var c = doc.CreateElement("c", NsMain);
            c.SetAttribute("r", cellRef);
            if (!string.IsNullOrEmpty(style)) c.SetAttribute("s", style);
            rowEl.AppendChild(c);
        }

        static void AppendFormulaStrCell(XmlDocument doc, XmlElement rowEl, string cellRef, string style, string formula, string cachedValue) {
            var c = doc.CreateElement("c", NsMain);
            c.SetAttribute("r", cellRef);
            if (!string.IsNullOrEmpty(style)) c.SetAttribute("s", style);
            if (!string.IsNullOrEmpty(cachedValue)) c.SetAttribute("t", "str");
            var f = doc.CreateElement("f", NsMain);
            f.InnerText = formula;
            c.AppendChild(f);
            if (!string.IsNullOrEmpty(cachedValue)) {
                var v = doc.CreateElement("v", NsMain);
                v.InnerText = cachedValue;
                c.AppendChild(v);
            }
            rowEl.AppendChild(c);
        }

        static void AppendFormulaNumCell(XmlDocument doc, XmlElement rowEl, string cellRef, string style, string formula, string cachedValue) {
            var c = doc.CreateElement("c", NsMain);
            c.SetAttribute("r", cellRef);
            if (!string.IsNullOrEmpty(style)) c.SetAttribute("s", style);
            var f = doc.CreateElement("f", NsMain);
            f.InnerText = formula;
            c.AppendChild(f);
            if (!string.IsNullOrEmpty(cachedValue)) {
                var v = doc.CreateElement("v", NsMain);
                v.InnerText = cachedValue;
                c.AppendChild(v);
            }
            rowEl.AppendChild(c);
        }

        static void AddMerge(XmlDocument doc, XmlElement mergeNode, string rangeRef) {
            var m = doc.CreateElement("mergeCell", NsMain);
            m.SetAttribute("ref", rangeRef);
            mergeNode.AppendChild(m);
        }

        static bool MergeStartsAtOrAfterRow(string rangeRef, int minRow) {
            if (string.IsNullOrEmpty(rangeRef)) return false;
            int colon = rangeRef.IndexOf(':');
            string first = colon > 0 ? rangeRef.Substring(0, colon) : rangeRef;
            int idx = 0;
            while (idx < first.Length && char.IsLetter(first[idx])) idx++;
            int r;
            return int.TryParse(first.Substring(idx), out r) && r >= minRow;
        }

        static string ColLetters(string cellRef) {
            if (string.IsNullOrEmpty(cellRef)) return "";
            int i = 0;
            while (i < cellRef.Length && char.IsLetter(cellRef[i])) i++;
            return cellRef.Substring(0, i);
        }

        static int ColIndex(string cellRef) {
            string col = ColLetters(cellRef);
            int n = 0;
            for (int i = 0; i < col.Length; i++) n = n * 26 + (char.ToUpperInvariant(col[i]) - 'A' + 1);
            return n;
        }

        static string Attr(XmlNode node, string name) {
            if (node == null || node.Attributes == null) return "";
            var a = node.Attributes[name];
            return a == null ? "" : a.Value;
        }

        // ---- SharedStrings ----
        sealed class SharedStrings {
            public readonly List<string> Items = new List<string>();
            readonly Dictionary<string, int> index = new Dictionary<string, int>(StringComparer.Ordinal);
            public void AddRaw(string s) {
                s = s ?? "";
                int idx = Items.Count;
                Items.Add(s);
                if (!index.ContainsKey(s)) index[s] = idx;
            }
            public int Add(string s) {
                s = s ?? "";
                int idx;
                if (index.TryGetValue(s, out idx)) return idx;
                idx = Items.Count;
                Items.Add(s);
                index[s] = idx;
                return idx;
            }
            public string Get(int idx) {
                return idx >= 0 && idx < Items.Count ? Items[idx] : "";
            }
        }

        static SharedStrings ReadSharedStrings(ZipArchive zip) {
            var sst = new SharedStrings();
            string xml = ReadEntryText(zip, "xl/sharedStrings.xml");
            if (string.IsNullOrEmpty(xml)) return sst;
            var doc = new XmlDocument();
            doc.LoadXml(xml);
            var nsm = new XmlNamespaceManager(doc.NameTable);
            nsm.AddNamespace("m", NsMain);
            foreach (XmlNode si in doc.SelectNodes("//m:si", nsm)) {
                var sb = new StringBuilder();
                foreach (XmlNode tn in si.SelectNodes(".//m:t", nsm)) sb.Append(tn.InnerText);
                sst.AddRaw(sb.ToString());
            }
            return sst;
        }

        static string BuildSharedStringsXml(SharedStrings sst) {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n");
            sb.Append("<sst xmlns=\"").Append(NsMain).Append("\" count=\"").Append(sst.Items.Count)
              .Append("\" uniqueCount=\"").Append(sst.Items.Count).Append("\">");
            for (int i = 0; i < sst.Items.Count; i++) {
                string s = sst.Items[i] ?? "";
                bool needSpace = s.Length > 0 && (char.IsWhiteSpace(s[0]) || char.IsWhiteSpace(s[s.Length - 1]));
                sb.Append("<si><t");
                if (needSpace) sb.Append(" xml:space=\"preserve\"");
                sb.Append(">").Append(XmlEscape(s)).Append("</t></si>");
            }
            sb.Append("</sst>");
            return sb.ToString();
        }

        static string XmlEscape(string s) {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++) {
                char c = s[i];
                // 剔除 XML 1.0 非法控制字符
                if (c < 0x20 && c != '\t' && c != '\n' && c != '\r') continue;
                switch (c) {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        // ---- 模板加载与 Zip 工具 ----
        static byte[] LoadTemplate(string customPath, string resourceName) {
            if (!string.IsNullOrWhiteSpace(customPath)) {
                if (!File.Exists(customPath)) throw new FileNotFoundException("找不到自定义清单模板：" + customPath);
                byte[] raw = File.ReadAllBytes(customPath);
                if (raw.Length < 4 || raw[0] != (byte)'P' || raw[1] != (byte)'K') {
                    throw new InvalidDataException("自定义模板不是有效的明文 .xlsx（如为加密文件，请先解密或改用内置模板）：" + Path.GetFileName(customPath));
                }
                return raw;
            }
            var asm = Assembly.GetExecutingAssembly();
            using (var s = asm.GetManifestResourceStream(resourceName)) {
                if (s == null) throw new InvalidOperationException("插件缺少内置清单模板资源：" + resourceName);
                using (var ms = new MemoryStream()) {
                    s.CopyTo(ms);
                    return ms.ToArray();
                }
            }
        }

        static bool HasAnyThumbnail(IList<BomItem> items) {
            for (int i = 0; i < items.Count; i++) {
                if (items[i].ThumbnailPng != null && items[i].ThumbnailPng.Length > 0) return true;
            }
            return false;
        }

        static string ReadEntryText(ZipArchive zip, string path) {
            var e = FindEntry(zip, path);
            if (e == null) return "";
            using (var s = e.Open())
            using (var r = new StreamReader(s, Encoding.UTF8)) return r.ReadToEnd();
        }

        static ZipArchiveEntry FindEntry(ZipArchive zip, string path) {
            foreach (var e in zip.Entries) {
                if (string.Equals(e.FullName.Replace('\\', '/'), path, StringComparison.OrdinalIgnoreCase)) return e;
            }
            return null;
        }

        static void WriteEntryText(ZipArchive zip, string path, string text) {
            WriteEntryBytes(zip, path, Utf8NoBom.GetBytes(text));
        }

        static void WriteEntryBytes(ZipArchive zip, string path, byte[] bytes) {
            var e = zip.CreateEntry(path, CompressionLevel.Optimal);
            using (var s = e.Open()) s.Write(bytes, 0, bytes.Length);
        }

        static void CopyEntry(ZipArchiveEntry src, ZipArchive dst, string path) {
            var e = dst.CreateEntry(path, CompressionLevel.Optimal);
            using (var sIn = src.Open())
            using (var sOut = e.Open()) sIn.CopyTo(sOut);
        }

        static string SerializeXml(XmlDocument doc) {
            var settings = new XmlWriterSettings {
                Encoding = Utf8NoBom,
                OmitXmlDeclaration = false,
                Indent = false,
                NewLineChars = "\r\n"
            };
            using (var ms = new MemoryStream()) {
                using (var w = XmlWriter.Create(ms, settings)) doc.Save(w);
                return Utf8NoBom.GetString(ms.ToArray());
            }
        }

        static void WriteFileAtomic(string outputPath, byte[] bytes) {
            string dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = outputPath + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            try {
                File.WriteAllBytes(tmp, bytes);
                if (File.Exists(outputPath)) File.Delete(outputPath);
                File.Move(tmp, outputPath);
            } finally {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }
    }
}
