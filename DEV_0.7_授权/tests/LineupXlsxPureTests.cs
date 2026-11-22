using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
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
            var lefts = plan.Callouts.Where(c => c.Side == "left").ToList();
            var rights = plan.Callouts.Where(c => c.Side == "right").ToList();
            Assert(lefts.Count >= 6 && rights.Count >= 6, "points split to left and right columns");
            Assert(lefts.Where(c => c.HasArrow).All(c => !c.FlipH), "left callout arrows point left-to-right (FlipH=false)");
            Assert(rights.Where(c => c.HasArrow).All(c => c.FlipH), "right callout arrows point right-to-left (FlipH=true)");
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

                var c1 = new LineupRecord { Category = "气缸", Number = "OK170-Y1C01", Description = "阻挡气缸", Model = "CDQ2B50-20TZ", Brand = "SMC", Home = "伸出", Mounting = "水平安装", Notes = "进料段" };
                var c2 = new LineupRecord { Category = "气缸", Number = "OK170-Y1C02", Description = "顶升气缸", Model = "MBB100-200Z", Brand = "SMC", Home = "缩回", Mounting = "垂直朝上", Notes = "顶升段" };
                var c3 = new LineupRecord { Category = "气缸", Number = "OK170-Y2C01", Description = "夹紧气缸", Model = "MGPM32-30Z", Brand = "SMC", Home = "松开", Mounting = "水平安装", Notes = "抓手" };
                p.Records.AddRange(new[] { c1, c2, c3 });
                LineupNumbering.GenerateValves(p, new[] { c1.Id, c2.Id, c3.Id });
                foreach (var v in p.Records.Where(r => r.Category == "阀片")) { v.Model = "SY5400-5UD1"; v.Brand = "SMC"; v.Address = "Y500SOLA/Y501SOLB"; }
                LineupNumbering.SensorPairs(p, new[] { c1.Id, c2.Id, c3.Id });
                foreach (var s in p.Records.Where(r => r.Category == "气缸传感器")) { s.Model = "D-M9PSAPC"; s.Brand = "SMC"; s.Connector = "M8/3针"; s.Address = "X200LS"; }
                p.Records.Add(new LineupRecord { Category = "普通传感器", Number = "SE01", Address = "X210PRS", Description = "托盘到位检测", Model = "WLG2-LD-DGJ03", Brand = "OMRON/欧姆龙", Connector = "M12/4针", Notes = "进料段" });
                p.Records.Add(new LineupRecord { Category = "其他电气件", Number = "E01", Address = "", Description = "RFID读写头", Model = "V680S_HMD63_EIP", Brand = "OMRON/欧姆龙", Connector = "M12/4针", Notes = "进料段" });

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

                // 2. 导出标准 10 列 .xlsx
                string xlsxPath = Path.Combine(dir, "SK26.01-OK170-line up.xlsx");
                var sum = LineupXlsxWriter.Write(xlsxPath, p, new LineupXlsxOptions { Export11Columns = false, IncludeCadPages = true });
                Assert(File.Exists(xlsxPath), "xlsx file written");
                Assert(sum.BomRows == p.Records.Count(r => LineupTableData.Categories.Contains(r.Category)), "all BOM rows written");
                Assert(sum.CadPages == 2 && sum.CadCallouts == 5 && sum.CadArrows == 4, "2 CAD pages, 5 callouts, 4 native red arrows written");
                Assert(sum.AuditErrors == 0 && sum.AuditWarnings == 1, "clean BOM + 1 intentionally unresolved CAD point => 0 errors, 1 warning");

                // 3. 校验 OpenXML 内部文件完整性（5 工作表 + 纵向合并 + VLOOKUP + 2 图 + 4 红色箭头 + 分页符）
                using (var zip = ZipFile.OpenRead(xlsxPath)) {
                    Assert(zip.GetEntry("xl/worksheets/sheet3.xml") != null, "BOM sheet present");
                    Assert(zip.GetEntry("xl/worksheets/sheet4.xml") != null, "CAD sheet present");
                    Assert(zip.GetEntry("xl/drawings/drawing1.xml") != null, "drawing1.xml present");
                    Assert(zip.GetEntry("xl/media/image1.png") != null && zip.GetEntry("xl/media/image2.png") != null, "both CAD PNG images embedded");

                    string bomXml = ReadZipText(zip, "xl/worksheets/sheet3.xml");
                    Assert(bomXml.Contains("autoFilter") && bomXml.Contains("state=\"frozen\""), "BOM sheet has autoFilter and frozen header");
                    Assert(System.Text.RegularExpressions.Regex.IsMatch(bomXml, @"mergeCell ref=""H\d+:H\d+"""), "valve island cells vertically merged on col H");

                    string cadXml = ReadZipText(zip, "xl/worksheets/sheet4.xml");
                    Assert(cadXml.Contains("VLOOKUP(") && cadXml.Contains("&amp;&quot;&quot;") && cadXml.Contains("t=\"str\""), "CAD sheet uses zero-safe VLOOKUP(...&\"\") formulas with t=str cached values");
                    Assert(cadXml.Contains("<rowBreaks"), "multi-page CAD sheet has print page breaks");

                    string drawXml = ReadZipText(zip, "xl/drawings/drawing1.xml");
                    int picCount = CountOccurrences(drawXml, "<xdr:pic>");
                    int arrowCount = CountOccurrences(drawXml, "<xdr:cxnSp>");
                    Assert(picCount == 2 && arrowCount == 4, "drawing1 has exact 2 pictures and 4 straightConnector1 red arrows");
                    Assert(drawXml.Contains("srgbClr val=\"FF0000\"") && drawXml.Contains("tailEnd type=\"arrow\""), "arrows are red with arrowheads");
                }

                // 4. 用 LineupXlsxReader 反向读回导出的 .xlsx，验证 BOM + 纵向合并阀岛 + 抬头元数据无损还原
                var imported = LineupXlsxReader.Read(xlsxPath);
                Assert(imported.Records.Count == sum.BomRows, "XLSX reader round-trips exact BOM row count (" + imported.Records.Count + ")");
                Assert(imported.ProjectCode == "SK26.01" && imported.StationCode == "OK170" && imported.LineCode == "L1", "XLSX reader recovers CAD header metadata");
                Assert(imported.Islands.Any(i => i.Key == "OK170-Y1" && i.Kind == "汇流排" && i.Model == "SS5Y7-20-05"), "XLSX reader recovers vertically merged manifold");
                Assert(imported.Islands.Any(i => i.Key == "OK170-Y2" && i.Kind == "阀岛" && i.Model == "SS5Y5-10SDAN-09BS-C6"), "XLSX reader recovers island config");
                var impV2 = imported.Records.Single(r => r.Number == "OK170-Y1V02");
                Assert(impV2.Mounting.Contains("SS5Y7-20-05"), "vertical merge propagated to second valve row in Y1");
                var impC1 = imported.Records.Single(r => r.Number == "OK170-Y1C01");
                var impS1 = imported.Records.Single(r => r.Number == "OK170-Y1C01H");
                Assert(impS1.CylinderId == impC1.Id && impS1.Position == "H", "XLSX reader auto-reconstructed cylinder->sensor hierarchy");

                // 5. 也验证 11 列模式导出 + 回读
                string xlsx11 = Path.Combine(dir, "lineup-11col.xlsx");
                var sum11 = LineupXlsxWriter.Write(xlsx11, p, new LineupXlsxOptions { Export11Columns = true, IncludeCadPages = false });
                var imp11 = LineupXlsxReader.Read(xlsx11);
                Assert(imp11.Records.Count == sum11.BomRows, "11-column XLSX export & re-import round-trips cleanly");
            } finally {
                try { Directory.Delete(dir, true); } catch { }
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
                Assert(caught, "TSD encrypted file throws friendly guidance error");
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
