using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Xml;

namespace TianGongCadSuite {
    // Lineup 新功能纯逻辑与 OpenXML 全链路测试（无 CAD / Office 依赖）
    public static class LineupXlsxPureTests {
        static int passed;
        static void Assert(bool ok, string label) {
            if (!ok) throw new Exception("LineupXlsxPureTests FAIL: " + label);
            passed++;
        }

        // 最小合法 1x1 红色 PNG 字节（用于纯逻辑测试嵌入 CAD 图片，无需 System.Drawing）
        static readonly byte[] TinyPng = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

        public static void Run() {
            passed = 0;
            TestSeedAndMatching();
            TestNumberingValvesAndSensors();
            TestAuditRules();
            TestCadLayoutNonCrossing();
            TestXlsxExportAndRoundTrip();
            TestRevisionChangelogAndBomDiff();
            TestSharedGridParserAndSheetPickers();
            TestTsdEncryptedDetection();
            Console.WriteLine("LineupXlsxPureTests PASSED " + passed);
        }

        static void TestSeedAndMatching() {
            var seed = LineupModelCatalog.LoadSeed(null);
            if (seed.Entries.Count > 0) {
                Assert(seed.Entries.Count >= 120, "embedded seed catalog has >= 120 real entries (" + seed.Entries.Count + ")");
                Assert(!seed.Entries.Any(e => e.Model.Contains("(0_0)") || e.Model == "吸钉气缸"), "seed catalog has no dirty CAD suffixes or pure Chinese names");
                var cyl = seed.MatchByFileName("CDQ2B50H-50TL-SL-DNV9002(0_0).par");
                Assert(cyl != null && cyl.Category == "气缸" && cyl.Brand == "SMC", "fuzzy match cylinder filename");
                var sen = seed.MatchByFileName("D_M9PSAPC_1.par");
                Assert(sen != null && sen.Brand == "SMC" && sen.Connector == "M8/3针", "fuzzy match sensor filename with underscores");
                var gen = seed.MatchByFileName("WLG2-LD-DGJ03.par");
                Assert(gen != null && gen.Category == "普通传感器" && gen.Connector == "M12/4针", "fuzzy match limit switch");
            }
            // 无论是否嵌入私有种子文件，匹配算法本身必须稳定可用
            var synthetic = new LineupModelCatalog();
            synthetic.Entries.Add(new LineupModelEntry { Category = "气缸", Model = "CDM2B40-100Z", Brand = "SMC" });
            synthetic.Entries.Add(new LineupModelEntry { Category = "气缸传感器", Model = "D-M9PSAPC", Brand = "SMC", Connector = "M8/3针" });
            Assert(synthetic.MatchByFileName("CDM2B40-100Z(0_0)_0.par") != null, "synthetic match strips (0_0)_0 suffix");
            Assert(synthetic.MatchByFileName("D_M9PSAPC.par") != null, "synthetic match normalizes underscores and hyphens");

            var p = new LineupProject();
            p.InferMetadataFromAssembly(@"C:\Work\SK26.01-OK170-总装配.asm");
            Assert(p.ProjectCode == "SK26.01" && p.StationCode == "OK170" && p.NumberPrefix == "OK170", "infer project & station from asm filename");

            var p2 = new LineupProject();
            p2.InferMetadataFromAssembly(@"C:\Work\SK26.01-C11-主线体.asm");
            Assert(p2.ProjectCode == "SK26.01" && p2.StationCode == "C11", "infer line station C11 from asm filename");
        }

        static void TestNumberingValvesAndSensors() {
            var p = new LineupProject();
            LineupNumbering.SetIsland(p, "OK270", 1, "SS5Y5-20-05", "汇流排");
            LineupNumbering.SetIsland(p, "OK270", 2, "SS5Y5-10SDAN-12BS-C6", "阀岛");
            LineupNumbering.SetIsland(p, "OK270", 3, "", "单片阀");

            Assert(LineupNumbering.ValveNumberForCylinder("OK270-Y1C01") == "OK270-Y1V01", "valve number for single cyl");
            Assert(LineupNumbering.ValveNumberForCylinder("OK270-Y1C02-1") == "OK270-Y1V02", "valve number for paired cyl -1");
            Assert(LineupNumbering.ValveNumberForCylinder("OK270-Y2C02a") == "OK270-Y2V02", "valve number for blow port a");

            var c1 = new LineupRecord { Category = "气缸", Number = "OK270-Y1C01", Description = "阻挡气缸", Model = "CDQ2B50-20TZ", Brand = "SMC", Home = "缩回", Mounting = "水平安装", Notes = "进料段" };
            var c2a = new LineupRecord { Category = "气缸", Number = "OK270-Y1C02-1", Description = "插销气缸", Model = "CDQ2A63-50DMZ", Brand = "SMC", Home = "缩回", Mounting = "竖直向下", Notes = "定位模块" };
            var c2b = new LineupRecord { Category = "气缸", Number = "OK270-Y1C02-2", Description = "插销气缸", Model = "CDQ2A63-50DMZ", Brand = "SMC", Home = "缩回", Mounting = "竖直向下", Notes = "定位模块" };
            var c3 = new LineupRecord { Category = "气缸", Number = "OK270-Y2C01", Description = "顶升气缸", Model = "MGPM40-75Z", Brand = "SMC", Home = "缩回", Mounting = "垂直朝上", Notes = "顶升机构" };
            p.Records.AddRange(new[] { c1, c2a, c2b, c3 });

            int valvesAdded = LineupNumbering.GenerateValves(p, new[] { c1.Id, c2a.Id, c2b.Id, c3.Id });
            Assert(valvesAdded == 3, "paired -1/-2 cylinders share 1 valve (3 valves generated)");
            var v1 = p.Records.Single(r => r.Number == "OK270-Y1V01");
            var v2 = p.Records.Single(r => r.Number == "OK270-Y1V02");
            var v3 = p.Records.Single(r => r.Number == "OK270-Y2V01");
            Assert(v1.Parameters == "阻挡气缸" && v2.Parameters == "插销气缸", "valve parameters populated from cylinder description");
            Assert(c2a.ValveId == v2.Id && c2b.ValveId == v2.Id, "both paired cylinders linked to shared valve");
            Assert(LineupNumbering.IslandLabel(p, v1) == "汇流排：SS5Y5-20-05", "manifold label formatted");
            Assert(LineupNumbering.IslandLabel(p, v3) == "阀岛：SS5Y5-10SDAN-12BS-C6", "island label formatted");

            var vSingle = new LineupRecord { Category = "阀片", Number = "OK270-Y3V01" };
            Assert(LineupNumbering.IslandLabel(p, vSingle) == "单片阀", "single valve label formatted");

            int senAdded = LineupNumbering.SensorPairs(p, new[] { c1.Id });
            Assert(senAdded == 2, "H/W sensor pair created");
            var s1h = p.Records.Single(r => r.Number == "OK270-Y1C01H");
            Assert(s1h.Notes == "进料段", "sensor copies cylinder notes");

            int multiAdded = LineupNumbering.SensorPairsMulti(p, new[] { c3.Id }, 2);
            Assert(multiAdded == 4 && p.Records.Any(r => r.Number == "OK270-Y2C01H2") && p.Records.Any(r => r.Number == "OK270-Y2C01W2"), "multi sensor pairs H1/W1/H2/W2 generated");
        }

        static void TestAuditRules() {
            var p = new LineupProject();
            p.Records.Add(new LineupRecord { Category = "气缸", Number = "OK170-Y1C01", Description = "阻挡气缸" }); // 缺型号/原位/安装/阀片/传感器
            p.Records.Add(new LineupRecord { Category = "气缸", Number = "OK170-Y1C01", Description = "重复" }); // 重复编号
            p.Records.Add(new LineupRecord { Category = "气缸", Number = "OK170-Y3C01", Description = "真空吸盘", Model = "ZP3C2-T32J2FSJB10", Brand = "SMC", Home = "非真空", Mounting = "垂直朝下" });
            p.Records.Add(new LineupRecord { Category = "阀片", Number = "OK170-Y3V01", Description = "真空发生器", Model = "ZK2H15", Brand = "SMC", Mounting = "阀组：ZK2H15" });
            p.Records.Add(new LineupRecord { Category = "阀片", Number = "OK170-Y4V01", Description = "主气管" });
            p.Records.Add(new LineupRecord { Category = "其他电气件", Number = "E01", Description = "按钮盒", Model = "电气提供" }); // 缺孔位提示
            var r = LineupAudit.Run(p);
            Assert(r.ErrorCount >= 2, "duplicate number flagged as Error");
            Assert(r.Issues.Any(i => i.RuleCode == "CYL_NO_VALVE"), "missing valve detected");
            Assert(r.Issues.Any(i => i.RuleCode == "CYL_NO_SENSOR" && i.Number == "OK170-Y1C01"), "missing H/W sensor detected on regular cylinder");
            Assert(!r.Issues.Any(i => i.RuleCode == "CYL_NO_SENSOR" && i.Number == "OK170-Y3C01"), "vacuum cup does not falsely require H/W magnetic sensors");
            Assert(!r.Issues.Any(i => i.RuleCode == "VALVE_FIELD" && i.Number == "OK170-Y4V01"), "main air pipe row does not falsely require valve model");
            Assert(r.Issues.Any(i => i.RuleCode == "BTN_BOX_DESC"), "button box missing hole count hint");
            Assert(r.Checklist.Count == 8, "8-item company audit checklist generated");
        }

        static void TestCadLayoutNonCrossing() {
            // 10 个密集聚集在同一 Y 附近的点 + 1 个未识别点，验证单调无重叠且左右分栏无交叉
            var ideal = new[] { 10, 10, 10, 10, 10, 11, 11, 39, 40, 41 };
            var solved = LineupCadLayout.SolveMonotonicRows(ideal, 4, 41);
            Assert(solved.Length == ideal.Length, "monotonic solver length matches");
            for (int i = 0; i < solved.Length; i++) {
                Assert(solved[i] >= 4 && solved[i] <= 41, "row within bounds");
                if (i > 0) Assert(solved[i] > solved[i - 1], "strictly increasing non-overlapping rows");
            }

            var page = new LineupCadPage { Name = "测试页", Width = 1600, Height = 1000 };
            for (int i = 0; i < 6; i++) {
                page.Points.Add(new LineupLocationPoint { Id = "L" + i, Number = "OK170-Y1C0" + (i + 1), State = "ok", X = 0.22, Y = 0.20 + i * 0.02 });
                page.Points.Add(new LineupLocationPoint { Id = "R" + i, Number = "SE0" + (i + 1), State = "ok", X = 0.78, Y = 0.15 + i * 0.12 });
            }
            page.Points.Add(new LineupLocationPoint { Id = "U1", Number = "E01", State = "unresolved", Reason = "遮挡" });

            var plan = LineupCadLayout.Solve(page, 1, null, null, new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { { "OK170-Y1C01", 3 } });
            Assert(LineupCadLayout.ColWidths.Length == 12 && LineupCadLayout.ImageFirstCol == 3 && LineupCadLayout.ImageLastColExcl == 9, "CAD sheet uses template 12-column A..L layout with D..I image area");
            var lefts = plan.Callouts.Where(c => c.Side == "left").ToList();
            var rights = plan.Callouts.Where(c => c.Side == "right").ToList();
            Assert(lefts.Count >= 6 && rights.Count >= 6, "points split to left and right columns");
            // 箭头方向严格为“从图指向单元格”：左栏箭头由图指向 C 列右缘（StartX > EndX, FlipH=true），右栏箭头由图指向 J 列左缘（StartX < EndX, FlipH=false）
            long leftEdgeX = LineupCadLayout.ColLeftEmu(LineupCadLayout.ImageFirstCol);
            long rightEdgeX = LineupCadLayout.ColLeftEmu(LineupCadLayout.ImageLastColExcl);
            Assert(lefts.Where(c => c.HasArrow).All(c => c.FlipH && c.StartX > c.EndX && c.EndX == leftEdgeX), "left arrows point from diagram to left cell C (FlipH=true, EndX=col D left)");
            Assert(rights.Where(c => c.HasArrow).All(c => !c.FlipH && c.StartX < c.EndX && c.EndX == rightEdgeX), "right arrows point from diagram to right cell J (FlipH=false, EndX=col J left)");
            Assert(lefts.First(c => c.Number == "OK170-Y1C01").BomRow == 3, "BOM row lookup linked into callout");
            // 验证左右侧箭头 Y 顺序单调不交叉
            for (int i = 1; i < lefts.Count; i++) Assert(lefts[i].ExcelRow > lefts[i - 1].ExcelRow, "left callouts strictly monotonic");
            for (int i = 1; i < rights.Count; i++) Assert(rights[i].ExcelRow > rights[i - 1].ExcelRow, "right callouts strictly monotonic");
        }

        static void TestXlsxExportAndRoundTrip() {
            string dir = Path.Combine(Path.GetTempPath(), "tg-lineup-xlsx-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try {
                string asmPath = Path.Combine(dir, "SK26.01-OK170-总装配.asm");
                string assets = LineupProject.CadAssetsDir(asmPath);
                Directory.CreateDirectory(assets);
                File.WriteAllBytes(Path.Combine(assets, "page-1.png"), TinyPng);
                File.WriteAllBytes(Path.Combine(assets, "page-2.png"), TinyPng);

                var p = new LineupProject {
                    AssemblyFile = asmPath,
                    ProjectCode = "SK26.01", StationCode = "OK170", LineCode = "L1",
                    Designer = "杨超", Editor = "苏英治", Revision = "V1.2"
                };
                LineupNumbering.SetIsland(p, "OK170", 1, "SS5Y7-20-05", "汇流排");
                LineupNumbering.SetIsland(p, "OK170", 2, "SS5Y5-10SDAN-09BS-C6", "阀岛");

                var c1 = new LineupRecord { Category = "气缸", Number = "OK170-Y1C01", Description = "阻挡气缸", Model = "CDQ2B50-20TZ", Brand = "SMC", Home = "伸出", Mounting = "水平安装", Mechanism = "上料抽屉", Connector = "M5-φ4", Notes = "进料段" };
                var c2 = new LineupRecord { Category = "气缸", Number = "OK170-Y1C02", Description = "顶升气缸", Model = "MBB100-200Z", Brand = "SMC", Home = "缩回", Mounting = "垂直朝上", Mechanism = "上料抽屉", Connector = "G1/8-φ6", Notes = "顶升段" };
                var c3 = new LineupRecord { Category = "气缸", Number = "OK170-Y2C01", Description = "夹紧气缸", Model = "MGPM32-30Z", Brand = "SMC", Home = "松开", Mounting = "水平安装", Mechanism = "隔热垫夹爪", Connector = "M5-φ4", Notes = "抓手" };
                p.Records.AddRange(new[] { c1, c2, c3 });
                LineupNumbering.GenerateValves(p, new[] { c1.Id, c2.Id, c3.Id });
                foreach (var v in p.Records.Where(r => r.Category == "阀片")) { v.Model = "SY5400-5UD1"; v.Brand = "SMC"; v.Address = "Y500SOLA/Y501SOLB"; v.Mechanism = v.Number.Contains("Y1") ? "上料抽屉" : "隔热垫夹爪"; }
                LineupNumbering.SensorPairs(p, new[] { c1.Id, c2.Id, c3.Id });
                foreach (var s in p.Records.Where(r => r.Category == "气缸传感器")) { s.Model = "D-M9PSAPC"; s.Brand = "SMC"; s.Connector = "M8/3针"; s.Address = "X200LS"; s.Mechanism = s.Number.Contains("Y1") ? "上料抽屉" : "隔热垫夹爪"; }
                p.Records.Add(new LineupRecord { Category = "普通传感器", Number = "SE01", Address = "X210PRS", Description = "托盘到位检测", Model = "WLG2-LD-DGJ03", Brand = "OMRON/欧姆龙", Home = "常闭", Connector = "M12/4针", Mechanism = "上料抽屉", Notes = "进料段" });
                p.Records.Add(new LineupRecord { Category = "其他电气件", Number = "E01", Address = "", Description = "RFID读写头", Model = "V680S_HMD63_EIP", Brand = "OMRON/欧姆龙", Connector = "M12/4针", Mechanism = "上料抽屉", Notes = "进料段" });

                p.CadPages.Add(new LineupCadPage {
                    Name = "总图", LineCode = "L1", ImageFile = "page-1.png", Width = 1600, Height = 1000,
                    Points = new List<LineupLocationPoint> {
                        new LineupLocationPoint { Id = c1.Id, Number = c1.Number, State = "ok", X = 0.25, Y = 0.30 },
                        new LineupLocationPoint { Id = c2.Id, Number = c2.Number, State = "ok", X = 0.72, Y = 0.55 },
                        new LineupLocationPoint { Id = "manual-1", Number = "SE01", State = "unresolved", Reason = "待点选" }
                    }
                });
                p.CadPages.Add(new LineupCadPage {
                    Name = "抓手", LineCode = "L1", ImageFile = "page-2.png", Width = 1600, Height = 1000,
                    Points = new List<LineupLocationPoint> {
                        new LineupLocationPoint { Id = c3.Id, Number = c3.Number, State = "ok", X = 0.40, Y = 0.60 },
                        new LineupLocationPoint { Id = p.Records.Single(x => x.Number == "E01").Id, Number = "E01", State = "ok", X = 0.80, Y = 0.25 }
                    }
                });

                // 1. 持久化 .lineup.xml 验证 CadPages + Island.Kind 不丢失
                string xmlFile = Path.ChangeExtension(asmPath, ".lineup.xml");
                p.Save(xmlFile);
                var reloadedProj = LineupProject.Load(xmlFile, asmPath);
                Assert(reloadedProj.CadPages.Count == 2 && reloadedProj.CadPages[0].Points.Count == 3, "CadPages persist through XML");
                Assert(reloadedProj.Islands.Single(i => i.Key == "OK170-Y1").Kind == "汇流排", "Island Kind persists through XML");

                // 2. 导出公司模板标准 11 列 .xlsx（默认）
                string xlsxPath = Path.Combine(dir, "SK26.01-OK170-line up.xlsx");
                var sum = LineupXlsxWriter.Write(xlsxPath, p, new LineupXlsxOptions { Export11Columns = true, IncludeCadPages = true });
                Assert(File.Exists(xlsxPath), "xlsx file written");
                Assert(sum.BomRows == p.Records.Count(r => LineupTableData.Categories.Contains(r.Category)), "all BOM rows written");
                Assert(sum.CadPages == 2 && sum.CadCallouts == 5 && sum.CadArrows == 4, "2 CAD pages, 5 callouts, 4 native red arrows written");
                Assert(sum.AuditErrors == 0 && sum.AuditWarnings == 1, "clean BOM + 1 intentionally unresolved CAD point => 0 errors, 1 warning");

                // 3. 校验 OpenXML 内部文件与公司模板 V3.0 样式完整性
                using (var zip = ZipFile.OpenRead(xlsxPath)) {
                    Assert(zip.GetEntry("xl/worksheets/sheet3.xml") != null, "BOM sheet present");
                    Assert(zip.GetEntry("xl/worksheets/sheet4.xml") != null, "CAD sheet present");
                    Assert(zip.GetEntry("xl/drawings/drawing1.xml") != null, "drawing1.xml present");
                    Assert(zip.GetEntry("xl/media/image1.png") != null && zip.GetEntry("xl/media/image2.png") != null, "both CAD PNG images embedded");

                    string stylesXml = ReadZipText(zip, "xl/styles.xml");
                    Assert(stylesXml.Contains("FFFFFF00") && stylesXml.Contains("FFF4B183") && stylesXml.Contains("FFBFBFBF") && stylesXml.Contains("FFE3E3E3"), "styles match company template palette (yellow/peach/gray)");

                    string logXml = ReadZipText(zip, "xl/worksheets/sheet1.xml");
                    Assert(logXml.Contains("A21:I23"), "changelog sheet has yellow remark block A21:I23");

                    string cfgXml = ReadZipText(zip, "xl/worksheets/sheet2.xml");
                    Assert(System.Text.RegularExpressions.Regex.IsMatch(cfgXml, @"mergeCell ref=""H\d+:H\d+"""), "config sheet vertically merges 机构 column H");

                    string bomXml = ReadZipText(zip, "xl/worksheets/sheet3.xml");
                    Assert(bomXml.Contains("autoFilter") && bomXml.Contains("xSplit=\"1\" ySplit=\"1\"") && bomXml.Contains("state=\"frozen\""), "BOM sheet has autoFilter and frozen row+col header");
                    Assert(System.Text.RegularExpressions.Regex.IsMatch(bomXml, @"mergeCell ref=""H\d+:H\d+"""), "valve island cells vertically merged on col H");
                    Assert(System.Text.RegularExpressions.Regex.IsMatch(bomXml, @"mergeCell ref=""J\d+:J\d+"""), "mechanism cells vertically merged on col J");

                    string cadXml = ReadZipText(zip, "xl/worksheets/sheet4.xml");
                    Assert(cadXml.Contains("showGridLines=\"0\""), "CAD sheet hides gridlines like template");
                    Assert(cadXml.Contains("D1:I1") && cadXml.Contains("E2:F2") && cadXml.Contains("H2:I2"), "CAD sheet uses template 12-col D..I yellow title merges");
                    Assert(cadXml.Contains("VLOOKUP(C") && cadXml.Contains("VLOOKUP(J") && cadXml.Contains("&amp;&quot;&quot;") && cadXml.Contains("t=\"str\""), "CAD sheet uses zero-safe VLOOKUP on C/J with t=str cached values");
                    Assert(cadXml.Contains("<rowBreaks"), "multi-page CAD sheet has print page breaks");

                    string signXml = ReadZipText(zip, "xl/worksheets/sheet5.xml");
                    Assert(signXml.Contains("B5:B11") && signXml.Contains("B60:B66") && signXml.Contains("r=\"66\""), "signoff sheet embeds full 64-item company checklist with B-col merges");

                    string drawXml = ReadZipText(zip, "xl/drawings/drawing1.xml");
                    int picCount = CountOccurrences(drawXml, "<xdr:pic>");
                    int arrowCount = CountOccurrences(drawXml, "<xdr:cxnSp>");
                    Assert(picCount == 2 && arrowCount == 4, "drawing1 has exact 2 pictures and 4 straightConnector1 red arrows");
                    Assert(drawXml.Contains("w=\"31750\"") && drawXml.Contains("srgbClr val=\"FF0000\"") && drawXml.Contains("headEnd type=\"none\"") && drawXml.Contains("tailEnd type=\"arrow\""), "arrows are 2.5pt red pointing from diagram (head=none) to cell (tail=arrow)");
                    Assert(CountOccurrences(drawXml, "flipH=\"1\"") == 2, "exact 2 left-side arrows have flipH=1 (pointing right-to-left into col C)");
                }

                // 4. 用 LineupXlsxReader 反向读回导出的 11 列 .xlsx，验证 BOM + 纵向合并阀岛 + 机构名称 + 抬头元数据无损还原
                var imported = LineupXlsxReader.Read(xlsxPath);
                Assert(imported.Records.Count == sum.BomRows, "XLSX reader round-trips exact BOM row count (" + imported.Records.Count + ")");
                Assert(imported.ProjectCode == "SK26.01" && imported.StationCode == "OK170" && imported.LineCode == "L1", "XLSX reader recovers CAD header metadata");
                Assert(imported.Islands.Any(i => i.Key == "OK170-Y1" && i.Kind == "汇流排" && i.Model == "SS5Y7-20-05"), "XLSX reader recovers vertically merged manifold");
                Assert(imported.Islands.Any(i => i.Key == "OK170-Y2" && i.Kind == "阀岛" && i.Model == "SS5Y5-10SDAN-09BS-C6"), "XLSX reader recovers island config");
                var impV2 = imported.Records.Single(r => r.Number == "OK170-Y1V02");
                Assert(impV2.Mounting.Contains("SS5Y7-20-05") && impV2.Mechanism == "上料抽屉", "vertical merge propagated to second valve row (Mounting + Mechanism)");
                var impC1 = imported.Records.Single(r => r.Number == "OK170-Y1C01");
                var impC2 = imported.Records.Single(r => r.Number == "OK170-Y1C02");
                Assert(impC1.Mechanism == "上料抽屉" && impC2.Mechanism == "上料抽屉" && impC1.Connector == "M5-φ4", "11-column template column order (I=Connector, J=Mechanism) round-trips losslessly");
                var impS1 = imported.Records.Single(r => r.Number == "OK170-Y1C01H");
                Assert(impS1.CylinderId == impC1.Id && impS1.Position == "H", "XLSX reader auto-reconstructed cylinder->sensor hierarchy");

                // 5. 也验证精简 10 列模式导出 + 回读
                string xlsx10 = Path.Combine(dir, "lineup-10col.xlsx");
                var sum10 = LineupXlsxWriter.Write(xlsx10, p, new LineupXlsxOptions { Export11Columns = false, IncludeCadPages = false });
                var imp10 = LineupXlsxReader.Read(xlsx10);
                Assert(imp10.Records.Count == sum10.BomRows, "10-column XLSX export & re-import round-trips cleanly");

                // 6. 验证可直接解析双语模板表头（含 SK编号No. / 型号\nModel number / 阀岛在 I 列）
                var tplGrid = new SortedDictionary<int, Dictionary<int, string>> {
                    { 1, new Dictionary<int, string> {
                        { 0, "SK编号No." }, { 1, "地址\nAddress" }, { 2, "功能参数\nFunctional parameters" },
                        { 3, "描述\nDescription" }, { 4, "型号\nModel number" }, { 5, "品牌\nBrands" },
                        { 6, "原位\nQriginal position" }, { 7, "安装方式\nMounting method" },
                        { 8, "传感器接口类型\nDetect interface type" }, { 9, "机构名称" }, { 10, "备注\nRemarks" }
                    } },
                    { 2, new Dictionary<int, string> { { 0, "CYLINDER LIST (Y*C**)  气缸列表" } } },
                    { 3, new Dictionary<int, string> { { 0, "110-Y1C01-1" }, { 3, "抽屉横移气缸1" }, { 4, "DGC-K-25-600" }, { 5, "FESTO" }, { 6, "缩回" }, { 7, "水平" }, { 8, "G1/8-φ6" }, { 9, "上料抽屉" } } },
                    { 4, new Dictionary<int, string> { { 0, "PNEUMATIC SOLENOID/VALVE LIST (Y*V**)  气动线圈/阀片列表" } } },
                    { 5, new Dictionary<int, string> { { 0, "110-Y1V01" }, { 3, "二位五通双电控" }, { 4, "VSVA-B-B52" }, { 5, "FESTO" }, { 8, "阀岛1\n51E-NDGS" }, { 9, "上料抽屉" } } }
                };
                var tplImp = LineupXlsxReader.ParseBomGrid(tplGrid, null, false);
                Assert(tplImp.Records.Count == 2 && tplImp.Records[0].Mechanism == "上料抽屉" && tplImp.Records[0].Connector == "G1/8-φ6", "parses bilingual company V3.0 template headers cleanly");
                Assert(tplImp.Islands.Any(i => i.Key == "110-Y1" && i.Kind == "阀岛" && i.Model == "51E-NDGS"), "recovers valve island even when template stores it in col I with newline");
            } finally {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        static void TestRevisionChangelogAndBomDiff() {
            // 1. 版本号智能递增规则
            Assert(LineupRevision.NextVersion("") == "V1.0", "empty version -> V1.0");
            Assert(LineupRevision.NextVersion("V1") == "V1.1", "V1 -> V1.1");
            Assert(LineupRevision.NextVersion("V3.3") == "V3.4", "V3.3 -> V3.4");
            Assert(LineupRevision.NextVersion("v2.9") == "V2.10", "v2.9 -> V2.10");
            Assert(LineupRevision.NextVersion("Rev2") == "Rev3", "Rev2 -> Rev3");
            Assert(LineupRevision.NormalizeDateString("46167") == "2026/5/25", "Excel serial date converts to yyyy/M/d");

            // 2. 构造 V3.0 基准 -> 编辑为 V3.1（新增 1、修改 1、取消 1）
            string dir = Path.Combine(Path.GetTempPath(), "tg-lineup-rev-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try {
                string asmPath = Path.Combine(dir, "SK24.18-ST110-demo.asm");
                var p = new LineupProject {
                    AssemblyFile = asmPath,
                    ProjectCode = "SK24.18", StationCode = "ST110", LineCode = "L1",
                    Designer = "贾书博", Editor = "沈敬丞", Revision = "V3.0",
                    Export11Columns = true, MarkBomChanges = true
                };
                var cKeep = new LineupRecord { Category = "气缸", Number = "110-Y1C01", Description = "横移气缸1", Model = "DGC-25", Brand = "FESTO", Home = "缩回", Mounting = "水平", Mechanism = "上料抽屉" };
                var cDel = new LineupRecord { Category = "气缸", Number = "110-Y1C02", Description = "旧阻挡气缸", Model = "ADN-32", Brand = "FESTO", Home = "伸出", Mounting = "竖直", Mechanism = "上料抽屉" };
                p.Records.AddRange(new[] { cKeep, cDel });
                p.ChangeLogs.Add(new LineupChangeLog { Date = "2026/5/25", Version = "V3.0", ModelDate = "2026/5/25", Content = "接收初版数模", Designer = "贾书博", Editor = "沈敬丞" });
                p.Baseline = LineupRevision.CaptureSnapshot(p, "V3.0");
                p.LatestExported = p.Baseline.Copy();

                // 修改 cKeep 原位、删除 cDel、新增 cAdd 与 seAdd
                cKeep.Home = "伸出";
                p.Records.Remove(cDel);
                var cAdd = new LineupRecord { Category = "气缸", Number = "110-Y1C03", Description = "新增顶升气缸", Model = "DFM-20", Brand = "FESTO", Home = "缩回", Mounting = "竖直", Mechanism = "上料抽屉" };
                var seAdd = new LineupRecord { Category = "普通传感器", Number = "SE01", Description = "有料检测", Model = "GTB6", Brand = "SICK", Home = "常闭", Connector = "M12, 4针", Mechanism = "上料抽屉" };
                p.Records.AddRange(new[] { cAdd, seAdd });

                var diff = LineupRevision.CompareToBaseline(p);
                Assert(diff.HasBaseline && diff.BaselineVersion == "V3.0", "diff captures baseline V3.0");
                Assert(diff.Added.Count == 2 && diff.Modified.Count == 1 && diff.Removed.Count == 1, "diff detects 2 added, 1 modified, 1 removed");
                Assert(diff.Removed[0].Number == "110-Y1C02", "removed record identified cleanly");

                string draft = LineupRevision.DraftChangeSummary(diff);
                Assert(draft.Contains("110-Y1C03") && draft.Contains("SE01") && draft.Contains("110-Y1C01") && draft.Contains("缩回→伸出") && draft.Contains("110-Y1C02"), "auto-drafted summary lists all added, modified, and removed details");

                p.ChangeLogs.Add(new LineupChangeLog { Date = "2026/6/5", Version = "V3.1", Content = draft, Notes = "校核升版", Designer = "贾书博", Editor = "柳玲玉" });
                p.Revision = "V3.1";

                // 3. 验证 .lineup.xml 持久化 ChangeLogs + Baseline + LatestExported
                string xmlPath = Path.ChangeExtension(asmPath, ".lineup.xml");
                p.Save(xmlPath);
                var loaded = LineupProject.Load(xmlPath, asmPath);
                Assert(loaded.ChangeLogs.Count == 2 && loaded.ChangeLogs[1].Version == "V3.1", "ChangeLogs persist through XML");
                Assert(loaded.Baseline != null && loaded.Baseline.Version == "V3.0" && loaded.Baseline.Records.Count == 2, "Baseline persists through XML");

                // 4. 导出 V3.1 .xlsx，验证多版本更新日志 + BOM 新增红底 & 取消红底删除线
                string outXlsx = Path.Combine(dir, "SK24.18-ST110-V3.1.xlsx");
                var sum = LineupXlsxWriter.Write(outXlsx, loaded, new LineupXlsxOptions { Export11Columns = true, IncludeCadPages = false, MarkBomChanges = true });
                Assert(sum.ChangelogRows == 2 && sum.BomAddedRows == 2 && sum.BomRemovedRows == 1, "summary reports 2 changelog rows, 2 added, 1 removed");

                using (var zip = ZipFile.OpenRead(outXlsx)) {
                    string stylesXml = ReadZipText(zip, "xl/styles.xml");
                    Assert(stylesXml.Contains("FFFF0000") && stylesXml.Contains("<strike/>"), "styles define bright red fill and strikethrough font for BOM changes");

                    string bomXml = ReadZipText(zip, "xl/worksheets/sheet3.xml");
                    Assert(bomXml.Contains("s=\"72\"") && bomXml.Contains("s=\"75\""), "BOM sheet applies red-added (s72) and red-strikethrough-cancelled (s75) styles");
                }

                // 5. 反向导入刚导出的 V3.1 .xlsx：验证历史 2 条更新日志完整读回，且带删除线的取消项 110-Y1C02 不会复活成在用器件！
                var reImp = LineupXlsxReader.Read(outXlsx);
                Assert(reImp.ChangeLogs.Count == 2 && reImp.ChangeLogs[0].Version == "V3.0" && reImp.ChangeLogs[1].Version == "V3.1", "re-import recovers all 2 changelog rows");
                Assert(reImp.LatestRevision == "V3.1" && reImp.LatestEditor == "柳玲玉", "re-import recovers latest version and editor from changelog");
                Assert(reImp.Records.Count == 3 && !reImp.Records.Any(r => r.Number == "110-Y1C02"), "re-import skips struck-through cancelled row 110-Y1C02 so it does not resurrect");

                // 6. 验证超长版本历史（22 条版本）自动顺延备注合并区而不重叠
                for (int k = 2; k <= 21; k++) {
                    loaded.ChangeLogs.Add(new LineupChangeLog { Date = "2026/6/" + Math.Min(28, k), Version = "V3." + k, Content = "例行更新 #" + k });
                }
                string manyXlsx = Path.Combine(dir, "many-logs.xlsx");
                var sumMany = LineupXlsxWriter.Write(manyXlsx, loaded, new LineupXlsxOptions { Export11Columns = true, IncludeCadPages = false, MarkBomChanges = false });
                Assert(sumMany.ChangelogRows == 22, "exports all 22 changelog entries");
                using (var zip = ZipFile.OpenRead(manyXlsx)) {
                    string logXml = ReadZipText(zip, "xl/worksheets/sheet1.xml");
                    Assert(logXml.Contains("A27:I29"), "remark merge shifts dynamically below 22 log rows + 2 blank rows (A27:I29)");
                }
            } finally {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        static void TestSharedGridParserAndSheetPickers() {
            Assert(LineupXlsxReader.PickBomSheetIndex(new[] { "LineUP更新日志", "LineUP第一版_配置表", " LineUP第二版_BOM ", "LineUP第三版_CAD" }) == 2, "picks BOM sheet by 第二版");
            Assert(LineupXlsxReader.PickBomSheetIndex(new[] { "说明", "Sheet_BOM", "其他" }) == 1, "falls back to BOM keyword");
            Assert(LineupXlsxReader.PickBomSheetIndex(new[] { "Sheet1", "Sheet2" }) == 0, "falls back to first sheet");
            Assert(LineupXlsxReader.PickCadSheetIndex(new[] { "LineUP第二版_BOM", "LineUP第三版_CAD (2)", "LineUP第三版_CAD" }) == 2, "picks main CAD sheet skipping (2) copy");

            var rawTsd = new SortedDictionary<int, Dictionary<int, string>> {
                { 1, new Dictionary<int, string> { { 0, "%TSD-Header-###%encrypted" } } }
            };
            Assert(LineupXlsxReader.LooksLikeRawTsdGrid(rawTsd), "detects un-decrypted %TSD header text in grid");

            // 验证 OpenXML 与 COM 共用的 ParseBomGrid 纵向合并展开与空行跳过
            var grid = new SortedDictionary<int, Dictionary<int, string>> {
                { 1, new Dictionary<int, string> { { 0, "编号" }, { 1, "地址" }, { 2, "品牌" }, { 3, "描述" }, { 4, "型号" }, { 5, "规格参数" }, { 6, "原位" }, { 7, "安装方式" }, { 8, "接口类型" }, { 9, "备注" } } },
                { 2, new Dictionary<int, string> { { 0, "CYLINDER LIST 气缸明细" } } },
                { 3, new Dictionary<int, string> { { 0, "OK170-Y1C01" }, { 2, "SMC" }, { 3, "阻挡气缸" }, { 4, "CDQ2B50-20TZ" }, { 6, "缩回" }, { 7, "水平安装" }, { 9, "进料段" } } },
                { 4, new Dictionary<int, string> { { 0, "PNEUMATIC SOLENOID/VALVE LIST 阀明细" } } },
                { 5, new Dictionary<int, string> { { 0, "OK170-Y1V01" }, { 2, "SMC" }, { 3, "两位五通" }, { 4, "SY5400-5UD1" }, { 7, "阀岛：SS5Y5-20-08" }, { 9, "进料段" } } },
                { 6, new Dictionary<int, string> { { 0, "OK170-Y1V02" }, { 2, "SMC" }, { 3, "两位五通" }, { 4, "SY5400-5UD1" } } }
            };
            var merges = new List<LineupCellMerge> {
                new LineupCellMerge { R1 = 5, C1 = 7, R2 = 7, C2 = 7 }, // 故意覆盖到不存在的第 7 行，验证不凭空造行
                new LineupCellMerge { R1 = 5, C1 = 9, R2 = 6, C2 = 9 }
            };
            var parsed = LineupXlsxReader.ParseBomGrid(grid, merges, false);
            Assert(!LineupXlsxReader.LooksLikeRawTsdGrid(grid), "normal BOM grid is not flagged as raw TSD");
            Assert(parsed.Records.Count == 3, "shared ParseBomGrid imports 3 rows and ignores empty merged row 7");
            var v2 = parsed.Records.Single(r => r.Number == "OK170-Y1V02");
            Assert(v2.Mounting == "阀岛：SS5Y5-20-08" && v2.Notes == "进料段", "shared ParseBomGrid propagates vertical merges on Mounting and Notes");
            Assert(parsed.Islands.Any(i => i.Key == "OK170-Y1" && i.Kind == "阀岛" && i.Model == "SS5Y5-20-08"), "shared ParseBomGrid infers island from merged cells");

            var cadGrid = new SortedDictionary<int, Dictionary<int, string>> {
                { 1, new Dictionary<int, string> { { 4, "项目编号" }, { 5, "SK26.08" }, { 7, "工位号" }, { 8, "OK280" } } },
                { 2, new Dictionary<int, string> { { 4, "线体号" }, { 5, "L2" } } }
            };
            LineupXlsxReader.ApplyCadMetadataRows(cadGrid, parsed);
            Assert(parsed.ProjectCode == "SK26.08" && parsed.StationCode == "OK280" && parsed.LineCode == "L2", "shared ApplyCadMetadataRows extracts header fields");

            string oleFile = Path.Combine(Path.GetTempPath(), "tg-ole2-test-" + Guid.NewGuid().ToString("N") + ".xls");
            try {
                File.WriteAllBytes(oleFile, new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 });
                Assert(LineupXlsxReader.IsOle2File(oleFile), "detects OLE2 compound document header (.xls)");
            } finally {
                try { File.Delete(oleFile); } catch { }
            }
        }

        // 真机 COM 联调测试：验证 Excel.Application、WPS KET.Application、旧版 .xls 读回及零僵尸进程泄漏
        public static void RunExcelComLive() {
            passed = 0;
            string dir = Path.Combine(Path.GetTempPath(), "tg-lineup-com-live-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try {
                var p = new LineupProject {
                    ProjectCode = "SK26.01", StationCode = "OK170", LineCode = "L1",
                    Designer = "杨超", Editor = "苏英治", Revision = "V1.2"
                };
                LineupNumbering.SetIsland(p, "OK170", 1, "SS5Y7-20-05", "汇流排");
                LineupNumbering.SetIsland(p, "OK170", 2, "SS5Y5-10SDAN-09BS-C6", "阀岛");
                var c1 = new LineupRecord { Category = "气缸", Number = "OK170-Y1C01", Description = "阻挡气缸", Model = "CDQ2B50-20TZ", Brand = "SMC", Home = "伸出", Mounting = "水平安装", Notes = "进料段" };
                var c2 = new LineupRecord { Category = "气缸", Number = "OK170-Y1C02", Description = "顶升气缸", Model = "MBB100-200Z", Brand = "SMC", Home = "缩回", Mounting = "垂直朝上", Notes = "顶升段" };
                var c3 = new LineupRecord { Category = "气缸", Number = "OK170-Y2C01", Description = "夹紧气缸", Model = "MGPM32-30Z", Brand = "SMC", Home = "松开", Mounting = "水平安装", Notes = "抓手" };
                p.Records.AddRange(new[] { c1, c2, c3 });
                LineupNumbering.GenerateValves(p, new[] { c1.Id, c2.Id, c3.Id });
                foreach (var v in p.Records.Where(r => r.Category == "阀片")) { v.Model = "SY5400-5UD1"; v.Brand = "SMC"; }
                LineupNumbering.SensorPairs(p, new[] { c1.Id, c2.Id, c3.Id });
                p.Records.Add(new LineupRecord { Category = "普通传感器", Number = "SE01", Description = "到位检测", Model = "WLG2-LD-DGJ03", Brand = "OMRON/欧姆龙", Connector = "M12/4针", Notes = "进料段" });
                p.Records.Add(new LineupRecord { Category = "其他电气件", Number = "E01", Description = "RFID读写头", Model = "V680S_HMD63_EIP", Brand = "OMRON/欧姆龙", Connector = "M12/4针", Notes = "进料段" });

                string xlsxPath = Path.Combine(dir, "SK26.01-OK170-com-test.xlsx");
                var sum = LineupXlsxWriter.Write(xlsxPath, p, new LineupXlsxOptions { Export11Columns = false, IncludeCadPages = true });

                int excelBefore = Process.GetProcessesByName("EXCEL").Length;
                int wpsBefore = Process.GetProcessesByName("et").Length;

                int enginesTested = 0;
                foreach (string progId in new[] { "Excel.Application", "KET.Application" }) {
                    if (Type.GetTypeFromProgID(progId) == null) continue;
                    enginesTested++;
                    var sw = Stopwatch.StartNew();
                    var imp = LineupExcelComReader.ReadWithProgId(xlsxPath, progId, false, false);
                    sw.Stop();
                    Console.WriteLine("COM [" + progId + "] read " + imp.Records.Count + " rows in " + sw.ElapsedMilliseconds + " ms");
                    Assert(imp.Records.Count == sum.BomRows, progId + " read exact row count (" + imp.Records.Count + ")");
                    Assert(imp.ProjectCode == "SK26.01" && imp.StationCode == "OK170" && imp.LineCode == "L1", progId + " recovered CAD header metadata");
                    Assert(imp.Islands.Any(i => i.Key == "OK170-Y1" && i.Kind == "汇流排" && i.Model == "SS5Y7-20-05"), progId + " recovered merged manifold Y1");
                    Assert(imp.Islands.Any(i => i.Key == "OK170-Y2" && i.Kind == "阀岛" && i.Model == "SS5Y5-10SDAN-09BS-C6"), progId + " recovered island Y2");
                    var v2 = imp.Records.Single(r => r.Number == "OK170-Y1V02");
                    Assert(v2.Mounting.Contains("SS5Y7-20-05"), progId + " propagated vertical merge to Y1V02");
                }
                Assert(enginesTested > 0, "at least one spreadsheet COM engine (Excel or WPS) tested live");

                // 另存一份真旧版 .xls (BIFF8, FileFormat=56) 验证 OLE2 路由自动导入
                string xlsPath = Path.Combine(dir, "SK26.01-OK170-legacy.xls");
                if (SaveAsLegacyXls(xlsxPath, xlsPath)) {
                    Assert(LineupXlsxReader.IsOle2File(xlsPath), "generated legacy .xls has OLE2 header");
                    var impXls = LineupXlsxReader.Read(xlsPath);
                    Assert(impXls.Records.Count == sum.BomRows, "legacy .xls auto-routes through COM and reads exact rows");
                    Assert(impXls.Records.Single(r => r.Number == "OK170-Y1V02").Mounting.Contains("SS5Y7-20-05"), "legacy .xls preserves vertically merged manifold");
                    Console.WriteLine("COM legacy .xls read PASSED (" + impXls.Records.Count + " rows)");
                }

                // 稍等进程退出并验证无后台残留 Excel/WPS 进程（WPS 守护进程换新预热实例时最多需 1~2 秒）
                int excelAfter = Process.GetProcessesByName("EXCEL").Length;
                int wpsAfter = Process.GetProcessesByName("et").Length;
                for (int w = 0; w < 12 && (excelAfter > excelBefore || wpsAfter > wpsBefore); w++) {
                    Thread.Sleep(250);
                    excelAfter = Process.GetProcessesByName("EXCEL").Length;
                    wpsAfter = Process.GetProcessesByName("et").Length;
                }
                Console.WriteLine("Process check: EXCEL " + excelBefore + " -> " + excelAfter + ", et " + wpsBefore + " -> " + wpsAfter);
                Assert(excelAfter <= excelBefore, "no leaked EXCEL.EXE process");
                Assert(wpsAfter <= wpsBefore, "no leaked WPS et.exe process");
                Console.WriteLine("LineupExcelComLive PASSED " + passed);
            } finally {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        static bool SaveAsLegacyXls(string xlsxPath, string xlsPath) {
            Type t = Type.GetTypeFromProgID("Excel.Application");
            if (t == null) return false;
            object app = null, wbs = null, wb = null;
            try {
                app = Activator.CreateInstance(t);
                t.InvokeMember("Visible", BindingFlags.SetProperty, null, app, new object[] { false });
                t.InvokeMember("DisplayAlerts", BindingFlags.SetProperty, null, app, new object[] { false });
                wbs = t.InvokeMember("Workbooks", BindingFlags.GetProperty, null, app, null);
                wb = wbs.GetType().InvokeMember("Open", BindingFlags.InvokeMethod, null, wbs, new object[] { xlsxPath, 0, true });
                wb.GetType().InvokeMember("SaveAs", BindingFlags.InvokeMethod, null, wb, new object[] { xlsPath, 56 }); // xlExcel8
                wb.GetType().InvokeMember("Close", BindingFlags.InvokeMethod, null, wb, new object[] { false });
                t.InvokeMember("Quit", BindingFlags.InvokeMethod, null, app, null);
                return File.Exists(xlsPath);
            } catch {
                return false;
            } finally {
                try { if (wb != null && Marshal.IsComObject(wb)) Marshal.FinalReleaseComObject(wb); } catch { }
                try { if (wbs != null && Marshal.IsComObject(wbs)) Marshal.FinalReleaseComObject(wbs); } catch { }
                try { if (app != null && Marshal.IsComObject(app)) Marshal.FinalReleaseComObject(app); } catch { }
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }

        static void TestTsdEncryptedDetection() {
            string fake = Path.Combine(Path.GetTempPath(), "tg-tsd-fake-" + Guid.NewGuid().ToString("N") + ".xlsx");
            try {
                File.WriteAllBytes(fake, Encoding.ASCII.GetBytes("%TSD-Header-###%encrypted-payload"));
                Assert(LineupXlsxReader.IsTsdEncrypted(fake), "detects %TSD-Header% magic bytes");
                bool caught = false;
                try { LineupXlsxReader.Read(fake); }
                catch (InvalidDataException ex) { caught = ex.Message.Contains("透明加密"); }
                Assert(caught, "truncated TSD encrypted file throws friendly guidance error immediately");

                // 再验证关闭 COM 通道时，常规长度的 %TSD 文件也给出友好透明加密引导提示
                byte[] padded = new byte[512];
                byte[] hdr = Encoding.ASCII.GetBytes("%TSD-Header-###%");
                Array.Copy(hdr, padded, hdr.Length);
                File.WriteAllBytes(fake, padded);
                bool prevCom = LineupXlsxReader.EnableComFallback;
                LineupXlsxReader.EnableComFallback = false;
                try {
                    bool caughtNoCom = false;
                    try { LineupXlsxReader.Read(fake); }
                    catch (InvalidDataException ex) { caughtNoCom = ex.Message.Contains("透明加密"); }
                    Assert(caughtNoCom, "TSD file without COM fallback throws friendly guidance error");
                } finally {
                    LineupXlsxReader.EnableComFallback = prevCom;
                }
            } finally {
                try { File.Delete(fake); } catch { }
            }
        }

        static string ReadZipText(ZipArchive zip, string entryName) {
            using (var sr = new StreamReader(zip.GetEntry(entryName).Open(), Encoding.UTF8)) return sr.ReadToEnd();
        }

        static int CountOccurrences(string text, string sub) {
            int n = 0, pos = 0;
            while ((pos = text.IndexOf(sub, pos, StringComparison.Ordinal)) >= 0) { n++; pos += sub.Length; }
            return n;
        }
    }
}
