using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using TianGongCadSuite;

static class LineupPneumaticPureTests {
    static int passed;
    static void Check(bool ok, string label) {
        if (!ok) throw new Exception("FAIL PNEUMATIC: " + label);
        passed++;
        Console.WriteLine("PASS PNEUMATIC: " + label);
    }

    public static void Run() {
        passed = 0;
        var p = new LineupProject { AssemblyFile = Path.Combine(Path.GetTempPath(), "pneu-fixture.asm"), ProjectCode = "SK26.01" };

        // 1. 新建 Y1 阀岛 & Y2 汇流排
        string y1 = LineupPneumaticOps.CreateIsland(p, "OK170", 1, "阀岛", "SS5Y5-20-08");
        string y2 = LineupPneumaticOps.CreateIsland(p, "OK170", 2, "汇流排", "SY5000-4");
        Check(y1 == "OK170-Y1" && y2 == "OK170-Y2", "create Y1 island and Y2 manifold");
        Check(LineupPneumaticOps.NextIslandNumber(p, "OK170") == 3, "next island number suggests Y3");

        // 2. 先放 1 个预留盖板 V01，再添加气缸套件 → 自动避让到 V02 / C02 + H/W
        var spares = LineupPneumaticOps.AddValveSlices(p, y1, true, 1);
        Check(spares.Count == 1 && p.Records.Single(r => r.Id == spares[0]).Number == "OK170-Y1V01", "add spare cover slice V01");
        var b1 = LineupPneumaticOps.AddCylinderBundle(p, y1, true, 1, false);
        var cyl1 = p.Records.Single(r => r.Id == b1.CylinderId);
        var val2 = p.Records.Single(r => r.Id == b1.ValveId);
        Check(val2.Number == "OK170-Y1V02" && cyl1.Number == "OK170-Y1C02" && b1.SensorIds.Count == 2, "bundle skips spare slot V01 and creates V02 + C02 + H/W");
        Check(p.Validate().Count == 0, "project links valid after bundle creation");

        // 3. 修改气缸描述自动联动阀片功能参数与 H/W 传感器描述
        LineupPneumaticOps.SyncCylinderDescription(p, cyl1, "", "上料夹紧气缸");
        Check(val2.Parameters == "上料夹紧气缸", "cylinder description syncs to valve parameters");
        Check(p.Records.Any(r => r.Number == "OK170-Y1C02H" && r.Description == "上料夹紧气缸原位"), "cylinder description syncs to H/W sensor descriptions");

        // 4. 一阀控多缸：给 V02 追加第 2 支气缸 → 自动重编为 C02-1 / C02-2 及 4 个 H/W
        var b2 = LineupPneumaticOps.AddCylinderToValve(p, val2.Id, 1);
        var cyl2 = p.Records.Single(r => r.Id == b2.CylinderId);
        Check(cyl1.Number == "OK170-Y1C02-1" && cyl2.Number == "OK170-Y1C02-2", "1-valve-2-cylinders auto renumbers to -1/-2");
        Check(p.Records.Any(r => r.Number == "OK170-Y1C02-1H") && p.Records.Any(r => r.Number == "OK170-Y1C02-2W"), "sensors cascade on 1-valve-2-cylinders");
        var chRows = LineupPneumaticOps.BuildChannelRows(p, y1);
        Check(chRows.Count == 3 && chRows[0].IsSpare && !chRows[1].IsContinuation && chRows[2].IsContinuation, "channel rows represent spare + primary + continuation accurately");

        // 5. 磁开模式切换：升级双组 H1/W1/H2/W2 & 清零（吹气）
        LineupPneumaticOps.SetCylinderSensorMode(p, cyl2.Id, 2);
        Check(p.Records.Count(r => r.CylinderId == cyl2.Id) == 4 && p.Records.Any(r => r.Number == "OK170-Y1C02-2W2"), "upgrade cylinder to 2-pair H1/W1/H2/W2");
        LineupPneumaticOps.SetCylinderSensorMode(p, cyl2.Id, 0);
        Check(p.Records.Count(r => r.CylinderId == cyl2.Id) == 0, "clear sensors for blow/no-switch cylinder");

        // 6. 统一套用磁开规格 + 同步阀岛到其他电气件 E**（幂等）
        int appliedSen = LineupPneumaticOps.ApplySensorSpecToIsland(p, y1, "D-M9B", "SMC", "2 m 导线引出型", false);
        Check(appliedSen == 2 && p.Records.Where(r => r.Category == "气缸传感器").All(r => r.Model == "D-M9B" && r.Brand == "SMC"), "bulk apply sensor model to island");
        int synced1 = LineupPneumaticOps.SyncIslandsToElectrical(p);
        int synced2 = LineupPneumaticOps.SyncIslandsToElectrical(p);
        Check(synced1 == 2 && synced2 == 0 && p.Records.Count(r => r.Category == "其他电气件") == 2, "sync islands to E** is idempotent");

        // 7. 阀路换岛：把 V02（带 C02-1, C02-2 与磁开）整体移到 Y2
        LineupPneumaticOps.MoveValvesToIsland(p, new[] { val2.Id }, y2);
        Check(val2.Number == "OK170-Y2V01" && cyl1.Number == "OK170-Y2C01-1" && cyl2.Number == "OK170-Y2C01-2", "move valve to Y2 cascades valve + multi-cylinders");
        Check(p.Records.Any(r => r.Number == "OK170-Y2C01-1H"), "move valve to Y2 cascades sensors");

        // 8. 删掉 1阀多缸中的第 2 支气缸 → 剩余第 1 支自动恢复为 C01（去掉 -1 后缀）
        LineupPneumaticOps.DeleteChannelItem(p, val2.Id, cyl2.Id, false);
        Check(cyl1.Number == "OK170-Y2C01" && p.Records.Any(r => r.Number == "OK170-Y2C01W"), "deleting 2nd shared cylinder restores clean C01 + C01W");

        // 9. 孤立气缸一键归属到 Y1（自动配阀片 + H/W）
        var orphan = new LineupRecord { Category = "气缸", Number = "SCAN-01", Description = "扫描气缸" };
        p.Records.Add(orphan);
        Check(LineupPneumaticOps.FindOrphanPneumaticRecords(p).Count == 1, "orphan cylinder detected");
        LineupPneumaticOps.AssignOrphansToIsland(p, new[] { orphan.Id }, y1, true, true);
        Check(orphan.Number == "OK170-Y1C02" && LineupPneumaticOps.FindOrphanPneumaticRecords(p).Count == 0, "assign orphan generates Y1C02 + valve + H/W");
        Check(p.Records.Any(r => r.Category == "阀片" && r.Number == "OK170-Y1V02"), "assign orphan creates matching valve V02");

        // 10. 吹气口 & 预留盖板自检通过（无缺失阀片/无缺失磁开误报）
        LineupPneumaticOps.AddCylinderBundle(p, y2, true, 0, true);
        var audit = LineupAudit.Run(p);
        Check(!audit.Issues.Any(i => i.RuleCode == "VALVE_NO_CYL" && i.Number == "OK170-Y1V01"), "spare cover slice V01 produces zero VALVE_NO_CYL warning");
        Check(!audit.Issues.Any(i => i.RuleCode == "CYL_NO_SENSOR" && i.Number == "OK170-Y2C02"), "blow/vacuum port Y2C02 produces zero CYL_NO_SENSOR warning");

        // 11. 离线窗体回归验证：确认方案 A 四模式切换 + 既有 form.Grid 编程编辑/可见性契约完全兼容
        string tmpDir = Path.Combine(Path.GetTempPath(), "tg-pneu-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        try {
            string asm = Path.Combine(tmpDir, "SK26.01-OK170-Test.asm");
            File.WriteAllText(asm, "fixture");
            p.AssemblyFile = asm;
            p.Save(Path.ChangeExtension(asm, ".lineup.xml"));

            using (var form = new LineupTableForm(asm, Path.Combine(tmpDir, "models.xml"))) {
                form.Show();
                Application.DoEvents();
                Check(form.ActiveViewMode == 0 && form.PneumaticView.Visible, "opens in Mode 0 (Pneumatic Island workspace) by default");
                Check(form.Grid.Columns["原位"].Visible && form.Grid.Columns["安装方式"].Visible, "underlying Grid retains home & mounting visibility in Mode 0");
                // 验证在 Mode 0 下，自动化测试直接操作 form.Grid.BeginEdit(true) 仍能成功
                form.Grid.CurrentCell = form.Grid.Rows[0].Cells[6];
                Check(form.Grid.BeginEdit(true), "programmatic Grid.BeginEdit succeeds even while Mode 0 is active");
                form.Grid.EndEdit();

                form.SetViewMode(1);
                Check(form.Grid.Columns[7].HeaderText == "常开/常闭" && !form.Grid.Columns["安装方式"].Visible, "Mode 1 (SE) shows NC/NO header and hides mounting");
                form.SetViewMode(2);
                Check(!form.Grid.Columns["传感器接口类型"].Visible && !form.Grid.Columns["原位"].Visible, "Mode 2 (E) shows concise electrical columns");
                form.SetCategory("气缸");
                Check(form.ActiveViewMode == 3 && form.Grid.Columns["原位"].Visible && form.Grid.Columns["安装方式"].Visible, "SetCategory switches cleanly to BOM grid with home & mounting visible");
                form.Close();
            }
        } finally {
            try { Directory.Delete(tmpDir, true); } catch { }
        }

        Console.WriteLine("PNEUMATIC PURE TESTS PASSED: " + passed);
    }
}
