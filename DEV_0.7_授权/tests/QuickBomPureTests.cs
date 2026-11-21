using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace TianGongCadSuite {
    // 快速出清单（0.12.0）纯逻辑测试：八大类自动判别、加工件筛选、表头推断、标准命名、校验、
    // 以及内嵌公司模板写出《加工件清单.xlsx》（含缩略图）与《编码申请-加工件.xlsx》（含 VLOOKUP 预填缓存值）。
    public static class QuickBomPureTests {
        static int passed;
        static void Check(bool ok, string label) {
            if (!ok) throw new Exception("QuickBomPureTests FAIL: " + label);
            passed++;
        }

        public static void Run() {
            passed = 0;
            TestCategoryClassification();
            TestCategoryParsing();
            TestCustomPartFilter();
            TestHeaderInferenceAndNaming();
            TestValidation();
            TestProcessingListXlsx();
            TestCodeRequestXlsxAndDictionaries();
            Console.WriteLine("QuickBomPureTests PASSED " + passed);
        }

        static void TestCategoryClassification() {
            string why;
            // 1类大焊接件：长度 > 1200mm
            var wBigLen = new BomItem { ModelPath = "SK26.01-SD-15-001.asm", PartName = "主框架", Material = "Q235-B", MaxLengthMm = 1500, MassKg = 45, MetricsLoaded = true };
            Check(BomRules.ClassifyCategory(wBigLen, out why) == 1, "weldment >1200mm -> cat 1 (" + why + ")");

            // 1类大焊接件：重量 > 100kg
            var wBigMass = new BomItem { ModelPath = "SK26.01-SD-15-002.asm", PartName = "底座", Material = "Q235-B", MaxLengthMm = 900, MassKg = 125, MetricsLoaded = true };
            Check(BomRules.ClassifyCategory(wBigMass, out why) == 1, "weldment >100kg -> cat 1 (" + why + ")");

            // 2类小焊接件：长度 <= 1200mm 且 重量 <= 100kg（按钮盒支架实测 1020mm / 33.8kg）
            var wSmall = new BomItem { ModelPath = "SK26.01-SD-15-009.asm", PartName = "缓存按钮盒支架", Material = "Q235-B", MaxLengthMm = 1020, MassKg = 33.8, MetricsLoaded = true };
            Check(BomRules.ClassifyCategory(wSmall, out why) == 2, "button box weldment -> cat 2 (" + why + ")");

            // 3类大板：长度 > 800mm 的机加件
            var bigPlate = new BomItem { ModelPath = "SK26.01-SD-15-020.par", PartName = "安装底板", Material = "45#", SurfaceTreatment = "镀化学镍", MaxLengthMm = 950, MidDimMm = 300, MinThickMm = 20, MetricsLoaded = true };
            Check(BomRules.ClassifyCategory(bigPlate, out why) == 3, "machined plate >800mm -> cat 3 (" + why + ")");

            // 4类小机加件：<= 800mm 且厚度 >= 6mm
            var smallMach = new BomItem { ModelPath = "SK26.01-SD-15-021.par", PartName = "固定块", Material = "Q235-B", SurfaceTreatment = "哑光喷塑RAL9003", MaxLengthMm = 300, MidDimMm = 100, MinThickMm = 10, MetricsLoaded = true };
            Check(BomRules.ClassifyCategory(smallMach, out why) == 4, "small machined part -> cat 4 (" + why + ")");

            // 5类车床件
            var lathe = new BomItem { ModelPath = "SK26.01-SD-15-022.par", PartName = "定位销轴", Material = "45#", MaxLengthMm = 80, MidDimMm = 20, MinThickMm = 20, MetricsLoaded = true };
            Check(BomRules.ClassifyCategory(lathe, out why) == 5, "turning pin shaft -> cat 5 (" + why + ")");

            // 6类钣金：.psm 扩展名
            var psm = new BomItem { ModelPath = "SK26.01-SD-15-023.psm", PartName = "侧盖板", Material = "SUS304", MaxLengthMm = 400, MidDimMm = 200, MinThickMm = 2, MetricsLoaded = true };
            Check(BomRules.ClassifyCategory(psm, out why) == 6, ".psm sheet metal -> cat 6 (" + why + ")");

            // 6类钣金：薄板无表面处理要求
            var thinSheet = new BomItem { ModelPath = "SK26.01-SD-15-024.par", PartName = "护罩", Material = "SPCC", MaxLengthMm = 400, MidDimMm = 200, MinThickMm = 2, MetricsLoaded = true };
            Check(BomRules.ClassifyCategory(thinSheet, out why) == 6, "thin sheet guard -> cat 6 (" + why + ")");

            // 7类非金属件
            var pom = new BomItem { ModelPath = "SK26.01-SD-15-025.par", PartName = "导向块", Material = "POM", MaxLengthMm = 120, MidDimMm = 40, MinThickMm = 15, MetricsLoaded = true };
            Check(BomRules.ClassifyCategory(pom, out why) == 7, "POM part -> cat 7 (" + why + ")");

            // 8类型材 / PC板
            var pc = new BomItem { ModelPath = "SK26.01-SD-15-026.par", PartName = "安全围栏PC板", Material = "PC", MaxLengthMm = 600, MidDimMm = 400, MinThickMm = 5, MetricsLoaded = true };
            Check(BomRules.ClassifyCategory(pc, out why) == 8, "PC plate -> cat 8 (" + why + ")");
            var alu = new BomItem { ModelPath = "SK26.01-SD-15-027.par", PartName = "40x40铝型材", Material = "6063-T5", MaxLengthMm = 1100, MidDimMm = 40, MinThickMm = 40, MetricsLoaded = true };
            Check(BomRules.ClassifyCategory(alu, out why) == 8, "6063 extrusion -> cat 8 (" + why + ")");
        }

        static void TestCategoryParsing() {
            Check(BomRules.ParseCategory("2") == 2, "parse 2");
            Check(BomRules.ParseCategory("2-焊接小件") == 2, "parse label 2-焊接小件");
            Check(BomRules.ParseCategory("大焊接件") == 1, "parse ERP name 大焊接件");
            Check(BomRules.ParseCategory("铝型材") == 8, "parse ERP name 铝型材");
            Check(BomRules.ErpCategoryName(2) == "焊接小件", "ERP name for 2");
            Check(BomRules.ErpCategoryName(8) == "铝型材", "ERP name for 8");
        }

        static void TestCustomPartFilter() {
            string why;
            Check(BomRules.IsCustomBomPart(@"C:\proj\SK26.01-SD-15-009.asm", "SK26.01", true, out why), "numbered + draft accepted");
            Check(!BomRules.IsCustomBomPart(@"C:\proj\MISUMI-SFJ10.par", "SK26.01", false, out why), "MISUMI rejected");
            Check(!BomRules.IsCustomBomPart(@"C:\proj\GB╱T 70.1 M8x20 螺钉.par", "SK26.01", false, out why), "GB screw rejected");
            Check(!BomRules.IsCustomBomPart(@"C:\Standard Parts\Pin.par", "SK26.01", false, out why), "Standard Parts dir rejected");
            Check(!BomRules.IsCustomBomPart(@"C:\proj\脚底板.par", "SK26.01", false, out why), "unnumbered helper par rejected");
            Check(BomRules.IsCustomBomPart(@"C:\proj\自定义板.par", "SK26.01", true, out why), "paired draft accepted even without SK code");
        }

        static void TestHeaderInferenceAndNaming() {
            var items = new List<BomItem> {
                new BomItem { Selected = true, DrawingNo = "SK26.01-SD-15-009", ProjectNameHint = "SK26.01丰田雷克萨斯金山PACK线", DesignerHint = "苏英治" },
                new BomItem { Selected = true, DrawingNo = "SK26.01-SD-15-010", ProjectNameHint = "SK26.01丰田雷克萨斯金山PACK线", DesignerHint = "苏英治" },
            };
            var h = new BomHeader {
                Customer = "丰田雷克萨斯",
                LineNo = "L2",
                StationNo = "C10",
                SequenceNo = "01",
                IssueDate = new DateTime(2026, 11, 18),
                DeliveryDate = new DateTime(2026, 11, 30)
            };
            BomRules.InferHeader(items, @"C:\work\按钮盒支架", h);
            Check(h.ProjectCode == "SK26.01", "inferred ProjectCode=" + h.ProjectCode);
            Check(h.MechanismNo == "SK26.01-SD-15", "inferred MechanismNo=" + h.MechanismNo);
            Check(h.ProjectName == "丰田雷克萨斯金山PACK线", "inferred ProjectName=" + h.ProjectName);
            Check(h.EngineerName == "苏英治", "inferred EngineerName=" + h.EngineerName);
            Check(h.ListTitle == "按钮盒支架", "inferred ListTitle=" + h.ListTitle);

            string folder = BomRules.SuggestFolderName(h);
            Check(folder == "SK26.01-C10-01-加工件清单（按钮盒支架）-苏英治-20261118", "folder name=" + folder);
            string xlsx = BomRules.SuggestListFileName(h);
            Check(xlsx == "SK26.01-C10-01-按钮盒支架加工件清单-苏英治-20261118.xlsx", "list name=" + xlsx);
            Check(BomXlsxWriter.ExcelSerialDate(new DateTime(2026, 11, 18)) == 46344, "serial date 2026-11-18 == 46344");
            Check(BomXlsxWriter.ExcelSerialDate(new DateTime(2026, 11, 30)) == 46356, "serial date 2026-11-30 == 46356");
        }

        static void TestValidation() {
            var opt = new BomOptions();
            var dup = new List<BomItem> {
                new BomItem { Selected = true, PartName = "A", DrawingNo = "SK26.01-SD-15-009", Material = "Q235-B", Quantity = 1, Category = 2 },
                new BomItem { Selected = true, PartName = "B", DrawingNo = "SK26.01-SD-15-009", Material = "Q235-B", Quantity = 2, Category = 2 }
            };
            var issues = BomRules.Validate(dup, opt);
            Check(issues.Any(i => i.Blocking && i.Message.Contains("图号重复")), "duplicate drawing no blocks export");

            var badQty = new List<BomItem> {
                new BomItem { Selected = true, PartName = "A", DrawingNo = "SK26.01-SD-15-010", Material = "Q235-B", Quantity = 0, Category = 2 }
            };
            Check(BomRules.Validate(badQty, opt).Any(i => i.Blocking && i.Message.Contains("数量")), "qty<=0 blocks export");
        }

        // 1x1 合法 PNG 字节（供缩略图写入测试）
        static readonly byte[] TinyPng = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

        static void TestProcessingListXlsx() {
            string tmpDir = Path.Combine(Path.GetTempPath(), "tg-bom-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmpDir);
            try {
                var h = new BomHeader {
                    Customer = "丰田雷克萨斯",
                    ProjectCode = "SK26.01",
                    ProjectName = "丰田雷克萨斯PACK线项目",
                    LineNo = "L2",
                    StationNo = "C10",
                    SequenceNo = "01",
                    MechanismNo = "SK26.01-SD-15",
                    ListTitle = "按钮盒支架",
                    EngineerRegion = "上海",
                    EngineerName = "苏英治",
                    IssueDate = new DateTime(2026, 11, 18),
                    DeliveryDate = new DateTime(2026, 11, 30)
                };
                var items = new List<BomItem> {
                    new BomItem { PartName = "缓存按钮盒支架", DrawingNo = "SK26.01-SD-15-009", Material = "Q235-B", Quantity = 10, Category = 2, SurfaceTreatment = "哑光喷塑RAL9003", Batch = "第一批", Priority = "A", ChangeReason = "正常设计", Code = "JF00404493", LookupDrawingNo = "SK26.01-SD-15-009", LookupPartName = "缓存按钮盒支架", ThumbnailPng = TinyPng },
                    new BomItem { PartName = "NG投排按钮盒支架", DrawingNo = "SK26.01-SD-15-010", Material = "Q235-B", Quantity = 10, Category = 2, SurfaceTreatment = "哑光喷塑RAL9003", Batch = "第一批", Priority = "A", ChangeReason = "正常设计", ThumbnailPng = TinyPng }
                };
                string outFile = Path.Combine(tmpDir, "out_list.xlsx");
                BomXlsxWriter.WriteProcessingList(outFile, h, items, new BomOptions { IncludeThumbnails = true });
                Check(File.Exists(outFile) && new FileInfo(outFile).Length > 80000, "list xlsx written");

                using (var z = ZipFile.OpenRead(outFile)) {
                    Check(z.GetEntry("xl/media/image1.png") != null, "company LOGO preserved");
                    Check(z.GetEntry("xl/media/image2.png") != null, "8-category rules image preserved");
                    Check(z.GetEntry("xl/media/thumb_1.png") != null, "thumb_1.png embedded");
                    Check(z.GetEntry("xl/media/thumb_2.png") != null, "thumb_2.png embedded");
                    string sheet = ReadEntry(z, "xl/worksheets/sheet1.xml");
                    string sst = ReadEntry(z, "xl/sharedStrings.xml");
                    string draw = ReadEntry(z, "xl/drawings/drawing1.xml");
                    string wb = ReadEntry(z, "xl/workbook.xml");
                    Check(sheet.Contains("COUNTIF(C:C,\"&lt;&gt;\")-3"), "COUNTIF formula preserved");
                    Check(sheet.Contains("<v>46356</v>") && sheet.Contains("<v>46344</v>"), "Excel serial dates written");
                    Check(sheet.Contains("J5:K5") && sheet.Contains("L5:M5") && sheet.Contains("J14:K14"), "row merges written");
                    Check(sheet.Contains("编制：苏英治"), "footer author updated");
                    Check(sst.Contains("缓存按钮盒支架") && sst.Contains("SK26.01-SD-15-009") && sst.Contains("JF00404493") && sst.Contains("丰田雷克萨斯"), "sharedStrings populated");
                    Check(draw.Contains("rIdThumb1") && draw.Contains("rIdThumb2"), "thumbnail anchors written");
                    Check(wb.Contains("加工件清单!$A$1:$T$14"), "Print_Area expanded to T with thumbs");
                }
            } finally {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        static void TestCodeRequestXlsxAndDictionaries() {
            List<string> mats, surfs;
            BomXlsxWriter.ReadErpDictionaries("", out mats, out surfs);
            Check(mats.Count > 100 && mats.Contains("Q235-B"), "ERP material dictionary loaded (" + mats.Count + ")");
            Check(surfs.Count > 100 && surfs.Contains("哑光喷塑RAL9003"), "ERP surface dictionary loaded (" + surfs.Count + ")");

            string tmpDir = Path.Combine(Path.GetTempPath(), "tg-bom-code-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmpDir);
            try {
                var items = new List<BomItem> {
                    new BomItem { PartName = "缓存按钮盒支架", DrawingNo = "SK26.01-SD-15-009", Material = "Q235-B", SurfaceTreatment = "哑光喷塑RAL9003", Category = 2 },
                    new BomItem { PartName = "导向块", DrawingNo = "SK26.01-SD-15-099", Material = "POM", SurfaceTreatment = "", Category = 7 }
                };
                string outFile = Path.Combine(tmpDir, "编码申请-加工件.xlsx");
                BomXlsxWriter.WriteCodeRequest(outFile, items, "");
                Check(File.Exists(outFile) && new FileInfo(outFile).Length > 30000, "code request xlsx written");

                using (var z = ZipFile.OpenRead(outFile)) {
                    Check(z.GetEntry("xl/worksheets/sheet3.xml") != null, "material lookup sheet preserved");
                    Check(z.GetEntry("xl/worksheets/sheet4.xml") != null, "surface lookup sheet preserved");
                    Check(z.GetEntry("xl/worksheets/sheet5.xml") != null, "8-category lookup sheet preserved");
                    string s1 = ReadEntry(z, "xl/worksheets/sheet1.xml");
                    string sst = ReadEntry(z, "xl/sharedStrings.xml");
                    Check(s1.Contains("VLOOKUP(TRIM(AG5),材料!$E:$J,6,FALSE)"), "AF5 material VLOOKUP formula");
                    Check(s1.Contains("<v>CL001</v>"), "AF5 cached material code CL001");
                    Check(s1.Contains("VLOOKUP(TRIM(AI5),处理方式!$E:$J,6,FALSE)"), "AH5 surface VLOOKUP formula");
                    Check(s1.Contains("<v>CLFS202</v>"), "AH5 cached surface code CLFS202");
                    Check(s1.Contains("VLOOKUP(AK5,八大类!$A:$B,2,FALSE)"), "AJ5 category VLOOKUP formula");
                    Check(sst.Contains("焊接小件") && sst.Contains("非金属件") && sst.Contains("SK26.01-SD-15-009"), "sharedStrings populated for code request");
                }
            } finally {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        static string ReadEntry(ZipArchive z, string path) {
            using (var s = z.GetEntry(path).Open())
            using (var r = new StreamReader(s, Encoding.UTF8)) return r.ReadToEnd();
        }
    }
}
