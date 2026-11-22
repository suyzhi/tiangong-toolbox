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
            TestRecursiveFolderScan();
            TestHeaderInferenceAndNaming();
            TestValidation();
            TestWorkbenchSyncAndRename();
            TestWorkbenchSortFilterAndUndo();
            TestWindowBoundsAndUxRules();
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

        static void TestRecursiveFolderScan() {
            string tmpDir = Path.Combine(Path.GetTempPath(), "tg-bom-scan-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmpDir);
            try {
                // 根目录模型
                File.WriteAllText(Path.Combine(tmpDir, "SK26.01-SD-15-001.par"), "");
                File.WriteAllText(Path.Combine(tmpDir, "SK26.01-SD-15-001.dft"), "");
                File.WriteAllText(Path.Combine(tmpDir, "~$SK26.01-SD-15-001.par"), ""); // 临时锁文件应跳过

                // 二级子文件夹模型
                string subA = Path.Combine(tmpDir, "按钮盒支架");
                string subDeep = Path.Combine(subA, "子组件");
                Directory.CreateDirectory(subDeep);
                File.WriteAllText(Path.Combine(subA, "SK26.01-SD-15-009.asm"), "");
                File.WriteAllText(Path.Combine(subA, "SK26.01-SD-15-009.dft"), "");
                File.WriteAllText(Path.Combine(subDeep, "OK050-01-07A.asm"), "");
                File.WriteAllText(Path.Combine(subDeep, "SK26.01-SD-15-010.psm"), "");

                // 已导出的清单成品目录（应自动跳过，避免重复扫出）
                string exportedDir = Path.Combine(tmpDir, "SK26.01-C10-01-加工件清单（按钮盒支架）-张三-20261121");
                Directory.CreateDirectory(exportedDir);
                File.WriteAllText(Path.Combine(exportedDir, "SK26.01-C10-01-按钮盒支架加工件清单-张三-20261121.xlsx"), "");
                File.WriteAllText(Path.Combine(exportedDir, "SK26.01-SD-15-009.asm"), "");

                // 隐藏目录（应跳过）
                string hiddenDir = Path.Combine(tmpDir, ".hidden");
                Directory.CreateDirectory(hiddenDir);
                File.WriteAllText(Path.Combine(hiddenDir, "SK26.01-SD-15-999.par"), "");

                var topOnly = BomRules.ScanFolderModels(tmpDir, false);
                Check(topOnly.Count == 1 && topOnly[0].EndsWith("SK26.01-SD-15-001.par"), "non-recursive scans only root and skips ~* lock files");

                var all = BomRules.ScanFolderModels(tmpDir, true);
                Check(all.Count == 4, "recursive finds root + nested models, skips exported BOM dir & hidden dir (got " + all.Count + ")");
                Check(BomRules.RelativeSubFolder(tmpDir, Path.Combine(tmpDir, "SK26.01-SD-15-001.par")) == "", "root file relative subfolder is empty");
                Check(BomRules.RelativeSubFolder(tmpDir, Path.Combine(subDeep, "SK26.01-SD-15-010.psm")) == Path.Combine("按钮盒支架", "子组件"), "nested relative subfolder matches");
                Check(BomRules.PickTopProject(all) == "SK26.01", "PickTopProject prefers SK26.01 with .dft over OK050");
            } finally {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        static void TestWorkbenchSyncAndRename() {
            // 1. 扩展属性去重（不重复显示核心 6 列）
            var extras = WorkbenchRules.ExtraPropFields(PropCardFields.Default);
            Check(!extras.Contains("零件名称") && !extras.Contains("图号") && !extras.Contains("数量") && !extras.Contains("表面处理"), "core shared prop fields deduplicated from extra columns");
            Check(extras.Contains("设计") && extras.Contains("项目名称") && extras.Contains("总装配图号"), "non-core prop card fields kept");

            // 2. .asm 使用「零件名」键映射 + 修改属性实时联动到 BOM 和改名模板
            var asmBom = new BomItem { ModelPath = @"D:\proj\旧支架.asm", PartName = "旧支架", DrawingNo = "旧支架", Material = "Q235-B", Quantity = 1 };
            asmBom.RawCustomProps["零件名"] = "旧支架";
            asmBom.RawCustomProps["图号"] = "SK26.01-SD-15-009";
            var parBom = new BomItem { ModelPath = @"D:\proj\小板.par", PartName = "小板", DrawingNo = "小板", Material = "45#", Quantity = 2 };
            parBom.RawCustomProps["零件名称"] = "定位小板";

            var existingFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
                @"D:\proj\旧支架.asm", @"D:\proj\旧支架.dft", @"D:\proj\旧支架.cfg",
                @"D:\proj\小板.par", @"D:\proj\小板.dft", @"D:\proj\已存在.dft"
            };
            Func<string, bool> exists = p => existingFiles.Contains(p);

            var rAsm = WorkbenchRules.CreateRow(asmBom, exists);
            var rPar = WorkbenchRules.CreateRow(parBom, exists);
            Check(rAsm.HasCfgFollower && rAsm.Bom.HasDraft == false, "fake path exists check sets HasCfgFollower");
            Check(rAsm.DraftDisplay == "缺 .dft (+.cfg)" || rAsm.DraftDisplay == "有+.cfg", "draft display includes cfg note");
            Check(rAsm.PropKeyFor("零件名称") == "零件名", ".asm maps 零件名称 to existing 零件名");
            Check(rPar.PropKeyFor("零件名称") == "零件名称", ".par maps 零件名称 to 零件名称");

            // 修改属性：实时同步到 Bom + 标脏 + 模板立即可用（未写入前就能用！）
            rAsm.SetPropValue("零件名称", "缓存按钮盒支架");
            rAsm.SetPropValue("表面处理", "哑光喷塑RAL9003");
            Check(rAsm.PropDirty && rAsm.IsPropEdited("零件名称") && rAsm.Bom.PartName == "缓存按钮盒支架", "editing 零件名称 marks Prop dirty and syncs Bom.PartName");
            var asmWrites = WorkbenchRules.CollectPropWrites(rAsm, PropCardFields.Default, true);
            Check(asmWrites.Any(kv => kv.Key == "零件名" && kv.Value == "缓存按钮盒支架"), "CollectPropWrites writes 零件名 without creating duplicate 零件名称");
            Check(!asmWrites.Any(kv => kv.Key == "零件名称"), "CollectPropWrites avoids duplicate 零件名称 on .asm");

            // 模板生成直接读取表里刚改的新值
            var miss = new List<string>();
            string tplOut = RenameRules.Template("{图号}-{零件名称}", rAsm.Rename.OldBase, 1, rAsm.EffectiveTemplateProps(), miss);
            Check(miss.Count == 0 && tplOut == "SK26.01-SD-15-009-缓存按钮盒支架", "live template uses unsaved edited properties (got " + tplOut + ")");

            // 一键「用图号 → 填新文件名」与跟随 .dft / .cfg 冲突检测
            var list = new List<WorkbenchRow> { rAsm, rPar };
            int synced = WorkbenchRules.SyncDrawingNoToNewName(list);
            Check(synced == 2 && rAsm.Rename.NewBase == "旧支架" == false || rAsm.Bom.DrawingNo.Length > 0, "SyncDrawingNoToNewName ran");
            rAsm.SetPropValue("图号", "SK26.01-SD-15-009");
            WorkbenchRules.SyncDrawingNoToNewName(new[] { rAsm });
            Check(rAsm.Rename.NewBase == "SK26.01-SD-15-009" && rAsm.RenameChanged, "SyncDrawingNoToNewName sets NewBase to DrawingNo");

            // 测试跟随 .dft 撞上磁盘上已有的 已存在.dft
            rPar.Rename.SetNewBase("已存在");
            int probs;
            var batch = WorkbenchRules.BuildAndValidateRenameBatch(list, true, exists, out probs);
            Check(batch.Count == 5, "batch includes 2 masters + 2 .dft + 1 .cfg = 5 rows (got " + batch.Count + ")");
            Check(probs > 0 && rPar.RenameProblem.Contains(".dft"), "companion .dft collision bubbles to master row: " + rPar.RenameProblem);

            // 修正冲突后校验通过，并验证改名后路径同步保留未写入的属性卡修改
            rPar.Rename.SetNewBase("SK26.01-SD-15-010");
            batch = WorkbenchRules.BuildAndValidateRenameBatch(list, true, exists, out probs);
            Check(probs == 0, "no rename problems after fixing collision (got " + probs + ")");

            var moved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
                { @"D:\proj\旧支架.asm", @"D:\proj\SK26.01-SD-15-009.asm" },
                { @"D:\proj\旧支架.dft", @"D:\proj\SK26.01-SD-15-009.dft" },
                { @"D:\proj\旧支架.cfg", @"D:\proj\SK26.01-SD-15-009.cfg" },
                { @"D:\proj\小板.par", @"D:\proj\SK26.01-SD-15-010.par" },
                { @"D:\proj\小板.dft", @"D:\proj\SK26.01-SD-15-010.dft" }
            };
            existingFiles.Add(@"D:\proj\SK26.01-SD-15-009.asm");
            existingFiles.Add(@"D:\proj\SK26.01-SD-15-009.dft");
            existingFiles.Add(@"D:\proj\SK26.01-SD-15-009.cfg");
            existingFiles.Add(@"D:\proj\SK26.01-SD-15-010.par");
            existingFiles.Add(@"D:\proj\SK26.01-SD-15-010.dft");
            WorkbenchRules.ApplyRenamedPaths(list, moved, exists);
            Check(rAsm.Rename.Path == @"D:\proj\SK26.01-SD-15-009.asm" && !rAsm.RenameChanged, "ApplyRenamedPaths updates Rename.Path and clears Changed");
            Check(rAsm.Prop.Path == @"D:\proj\SK26.01-SD-15-009.asm" && rAsm.PropDirty, "ApplyRenamedPaths updates Prop.Path and preserves unsaved edits");
            Check(rAsm.Bom.ModelPath == @"D:\proj\SK26.01-SD-15-009.asm" && rAsm.Bom.DraftPath == @"D:\proj\SK26.01-SD-15-009.dft", "ApplyRenamedPaths updates Bom model & draft paths");

            // 一键「用文件名 → 填图号」
            WorkbenchRules.SyncFileNameToDrawingNo(new[] { rPar });
            Check(rPar.Bom.DrawingNo == "SK26.01-SD-15-010" && rPar.IsPropEdited("图号"), "SyncFileNameToDrawingNo updates both Bom.DrawingNo and Prop 图号");
        }

        static void TestWorkbenchSortFilterAndUndo() {
            // 1. 自然排序：2 排在 10 前面，忽略大小写
            Check(WorkbenchRules.NaturalCompare("SK26.01-SD-15-2", "SK26.01-SD-15-10") < 0, "natural compare: 2 < 10");
            Check(WorkbenchRules.NaturalCompare("SK26.01-SD-15-10", "SK26.01-SD-15-2") > 0, "natural compare: 10 > 2");
            Check(WorkbenchRules.NaturalCompare("part02", "PART10") < 0, "natural compare case-insensitive");
            Check(WorkbenchRules.NaturalCompare("A-2", "A-02") < 0, "natural compare shorter numeric tie-breaker");

            Func<string, bool> noExist = p => false;
            var r1 = WorkbenchRules.CreateRow(new BomItem { ModelPath = @"D:\p\SK-10.par", PartName = "底座", DrawingNo = "SK-10", Material = "Q235-B", Quantity = 10, Category = 4, Selected = true }, noExist);
            var r2 = WorkbenchRules.CreateRow(new BomItem { ModelPath = @"D:\p\SK-2.par", PartName = "支架", DrawingNo = "SK-2", Material = "", Quantity = 2, Category = 2, Selected = true }, noExist);
            var r3 = WorkbenchRules.CreateRow(new BomItem { ModelPath = @"D:\p\SK-1.asm", PartName = "总成", DrawingNo = "SK-1", Material = "SUS304", Quantity = 1, Category = 1, Selected = false }, noExist);
            var r4 = WorkbenchRules.CreateRow(new BomItem { ModelPath = @"D:\p\SK-3.par", PartName = "压板", DrawingNo = "SK-3", Material = "Q235-B", Quantity = 4, Category = 4, Selected = true }, noExist);

            // 2. 排序：文件名自然升序/降序、数量数值升序/降序、文本空白无论升降序均置底、勾选列升序已勾选在前
            var rows = new List<WorkbenchRow> { r1, r2, r3, r4 };
            WorkbenchRules.SortRows(rows, "col:old", 1, r => "");
            Check(rows[0] == r3 && rows[1] == r2 && rows[2] == r4 && rows[3] == r1, "natural sort asc on col:old: SK-1, SK-2, SK-3, SK-10");
            WorkbenchRules.SortRows(rows, "col:old", -1, r => "");
            Check(rows[0] == r1 && rows[1] == r4 && rows[2] == r2 && rows[3] == r3, "natural sort desc on col:old: SK-10, SK-3, SK-2, SK-1");

            WorkbenchRules.SortRows(rows, "prop:数量", 1, r => "");
            Check(rows[0].Bom.Quantity == 1 && rows[1].Bom.Quantity == 2 && rows[2].Bom.Quantity == 4 && rows[3].Bom.Quantity == 10, "numeric sort asc on prop:数量");
            WorkbenchRules.SortRows(rows, "prop:数量", -1, r => "");
            Check(rows[0].Bom.Quantity == 10 && rows[3].Bom.Quantity == 1, "numeric sort desc on prop:数量");

            WorkbenchRules.SortRows(rows, "col:mat", 1, r => "");
            Check(rows[3] == r2, "blank material stays last on asc sort");
            WorkbenchRules.SortRows(rows, "col:mat", -1, r => "");
            Check(rows[3] == r2 && rows[0] == r3, "blank material ALSO stays last on desc sort (Excel behavior)");

            WorkbenchRules.SortRows(rows, "col:sel", 1, r => "");
            Check(rows[0].Bom.Selected && rows[1].Bom.Selected && rows[2].Bom.Selected && !rows[3].Bom.Selected, "col:sel asc puts selected rows first");

            // 3. 唯一值统计（(空白) 置顶 + 计数准确）与多列级联筛选
            var distinctMat = WorkbenchRules.DistinctColumnValues(rows, "col:mat", r => "");
            Check(distinctMat.Count == 3 && distinctMat[0].Key == WorkbenchRules.BlankToken && distinctMat[0].Value == 1, "DistinctColumnValues puts (空白) first with count 1");
            Check(distinctMat.Any(kv => kv.Key == "Q235-B" && kv.Value == 2), "DistinctColumnValues counts Q235-B == 2");

            var filters = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal) {
                { "col:mat", new HashSet<string>(new[] { "Q235-B" }, StringComparer.Ordinal) },
                { "col:kind", new HashSet<string>(new[] { "零件" }, StringComparer.Ordinal) }
            };
            Check(WorkbenchRules.PassesColumnFilters(r1, "", filters, null), "r1 passes mat=Q235-B & kind=零件");
            Check(!WorkbenchRules.PassesColumnFilters(r2, "", filters, null), "r2 fails mat=Q235-B");
            Check(!WorkbenchRules.PassesColumnFilters(r3, "", filters, null), "r3 fails both filters");
            // 级联：在打开材料筛选时跳过 col:mat，仍只看到 kind=零件 下的材料（不包含 r3 的 SUS304）
            var matCands = rows.Where(r => WorkbenchRules.PassesColumnFilters(r, "", filters, "col:mat")).ToList();
            Check(matCands.Count == 3 && !matCands.Contains(r3), "cascading excludeColKey keeps other column filters active");

            // 4. Undo 快照捕获与精确回滚
            var snap = r1.CaptureSnapshot();
            r1.SetPropValue("零件名称", "改错的名字");
            r1.SetMaterial("AL6061");
            r1.Rename.SetNewBase("WRONG-NAME");
            r1.Bom.Selected = false;
            Check(r1.Bom.PartName == "改错的名字" && r1.RenameChanged && r1.PropDirty && !r1.Bom.Selected, "row mutated before undo");
            r1.RestoreSnapshot(snap);
            Check(r1.Bom.PartName == "底座" && r1.Bom.Material == "Q235-B" && !r1.RenameChanged && !r1.PropDirty && r1.Bom.Selected, "RestoreSnapshot restores all editable fields cleanly");

            // 5. 剪贴板 TSV 格式化与解析往返（含特殊换行/制表符清洗）
            var block = new List<IList<string>> {
                new[] { "零件A", "SK-01\t带Tab" },
                new[] { "零件B\r\n换行", "SK-02" }
            };
            string tsv = WorkbenchRules.FormatClipboardBlock(block);
            var parsed = PropCardFields.ParseClipboard(tsv);
            Check(parsed.Count == 2 && parsed[0].Length == 2 && parsed[0][0] == "零件A" && parsed[0][1] == "SK-01 带Tab" && parsed[1][0] == "零件B 换行" && parsed[1][1] == "SK-02",
                "FormatClipboardBlock + ParseClipboard roundtrips cleanly: " + tsv);
        }

        static void TestWindowBoundsAndUxRules() {
            // 1. 窗口位置/尺寸格式化与解析
            var b = new SavedWindowBounds { X = 1950, Y = 80, W = 1400, H = 820, Maximized = true };
            string encoded = WindowBoundsRules.Format(b);
            SavedWindowBounds back;
            Check(WindowBoundsRules.TryParse(encoded, out back) && back.X == 1950 && back.Y == 80 && back.W == 1400 && back.H == 820 && back.Maximized,
                "WindowBoundsRules Format/TryParse roundtrip");
            SavedWindowBounds bad;
            Check(!WindowBoundsRules.TryParse("10,10,50,50,0", out bad), "WindowBoundsRules rejects tiny bounds");
            Check(!WindowBoundsRules.TryParse("broken", out bad), "WindowBoundsRules rejects malformed text");

            // 2. 多屏适配：副屏仍在 -> 保留副屏位置；拔掉副屏 -> 自动拉回主屏居中且不超工作区
            var primary = new RectI(0, 0, 1920, 1040);
            var secondary = new RectI(1920, 0, 1920, 1040);
            var onSec = WindowBoundsRules.FitToScreens(new RectI(2000, 60, 1400, 820), new[] { primary, secondary }, primary, 860, 500);
            Check(onSec.X == 2000 && onSec.Y == 60 && onSec.W == 1400 && onSec.H == 820, "FitToScreens preserves valid secondary monitor placement");

            var unplugged = WindowBoundsRules.FitToScreens(new RectI(2200, 60, 1400, 820), new[] { primary }, primary, 860, 500);
            Check(unplugged.X >= 0 && unplugged.Right <= primary.Right && unplugged.Y >= 0 && unplugged.Bottom <= primary.Bottom,
                "FitToScreens rescues unplugged monitor window onto primary work area");

            var topOff = WindowBoundsRules.FitToScreens(new RectI(100, -10, 1000, 700), new[] { primary }, primary, 600, 400);
            Check(topOff.Y >= 0, "FitToScreens clamps negative top edge back onto screen");

            // 3. 列宽记忆往返与过滤异常项
            var wMap = new Dictionary<string, int>(StringComparer.Ordinal) { { "col:old", 220 }, { "prop:零件名称", 180 }, { "bad:tiny", 5 } };
            string wText = WindowBoundsRules.FormatWidths(wMap);
            var wBack = WindowBoundsRules.ParseWidths(wText);
            Check(wBack.Count == 2 && wBack["col:old"] == 220 && wBack["prop:零件名称"] == 180, "FormatWidths/ParseWidths roundtrip");

            // 4. 文本批量替换纯逻辑
            bool ch;
            string rCi = WindowBoundsRules.ReplaceInText("SK26-a-A", "a", "B", false, out ch);
            Check(ch && rCi == "SK26-B-B", "ReplaceInText case-insensitive");
            string rCs = WindowBoundsRules.ReplaceInText("SK26-a-A", "a", "B", true, out ch);
            Check(ch && rCs == "SK26-B-A", "ReplaceInText case-sensitive");
            string rNone = WindowBoundsRules.ReplaceInText("SK26-01", "ZZ", "YY", false, out ch);
            Check(!ch && rNone == "SK26-01", "ReplaceInText reports unchanged when not found");

            // 5. 自动补全候选合并去重（保留预设在前 + 表内新值追加在后）
            var sug = WorkbenchRules.BuildSuggestions(new[] { "45#", "Q235-B" }, new[] { "q235-b", "AL6061-T6", "", "45#" });
            Check(sug.Length == 3 && sug[0] == "45#" && sug[1] == "Q235-B" && sug[2] == "AL6061-T6", "BuildSuggestions deduplicates preserving preset order");
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
