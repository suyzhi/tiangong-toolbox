using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using TianGongCadSuite;

static class LineupPneumaticPureTests {
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    const int WM_KEYDOWN = 0x0100;
    const int WM_CHAR = 0x0102;
    const int WM_LBUTTONDOWN = 0x0201;
    const int WM_LBUTTONUP = 0x0202;
    const int WM_LBUTTONDBLCLK = 0x0203;

    static int passed;
    static void Check(bool ok, string label) {
        if (!ok) throw new Exception("FAIL PNEUMATIC: " + label);
        passed++;
        Console.WriteLine("PASS PNEUMATIC: " + label);
    }

    static void Reject(Action action, string label) {
        try { action(); }
        catch (InvalidOperationException) { Check(true, label); return; }
        throw new Exception("FAIL PNEUMATIC (expected rejection): " + label);
    }

    static LineupRecord MockCad(string key, string name) {
        return new LineupRecord { ReferenceKey = key, SourceFile = name + ".par", InstancePath = new[] { name } };
    }

    static void ClickCell(DataGridView g, int col, int row) {
        var rc = g.GetCellDisplayRectangle(col, row, true);
        int x = rc.Left + Math.Min(10, Math.Max(2, rc.Width / 2));
        int y = rc.Top + Math.Min(10, Math.Max(2, rc.Height / 2));
        IntPtr lp = new IntPtr(((y & 0xFFFF) << 16) | (x & 0xFFFF));
        SendMessage(g.Handle, WM_LBUTTONDOWN, (IntPtr)1, lp);
        SendMessage(g.Handle, WM_LBUTTONUP, IntPtr.Zero, lp);
        Application.DoEvents();
    }

    static void DoubleClickCell(DataGridView g, int col, int row) {
        var rc = g.GetCellDisplayRectangle(col, row, true);
        int x = rc.Left + Math.Min(10, Math.Max(2, rc.Width / 2));
        int y = rc.Top + Math.Min(10, Math.Max(2, rc.Height / 2));
        IntPtr lp = new IntPtr(((y & 0xFFFF) << 16) | (x & 0xFFFF));
        SendMessage(g.Handle, WM_LBUTTONDBLCLK, (IntPtr)1, lp);
        SendMessage(g.Handle, WM_LBUTTONUP, IntPtr.Zero, lp);
        Application.DoEvents();
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

                // 12. 队列规范化与多行顺序批量绑定纯逻辑
                var seqProj = new LineupProject();
                var rA = new LineupRecord { Category = "气缸传感器", Number = "OK170-Y1C01H", Description = "1原位" };
                var rB = new LineupRecord { Category = "气缸传感器", Number = "OK170-Y1C01W", Description = "1工作位" };
                var rC = new LineupRecord { Category = "气缸传感器", Number = "OK170-Y1C02H", Description = "2原位" };
                var rExt = new LineupRecord { Category = "气缸传感器", Number = "OK170-Y1C02W", ReferenceKey = "EXT_KEY" };
                seqProj.Records.AddRange(new[] { rA, rB, rC, rExt });
                var norm = LineupTableForm.NormalizeBindQueue(seqProj, new[] { rA.Id, "missing", rB.Id, rA.Id, rC.Id });
                Check(norm.Count == 3 && norm[0] == rA.Id && norm[1] == rB.Id && norm[2] == rC.Id, "NormalizeBindQueue preserves order, deduplicates, and filters invalid IDs");

                int boundN = LineupTableForm.ApplySequentialCadBindings(seqProj, norm, new[] { MockCad("K1", "S1"), MockCad("K2", "S2"), MockCad("K3", "S3") });
                Check(boundN == 3 && rA.ReferenceKey == "K1" && rB.ReferenceKey == "K2" && rC.ReferenceKey == "K3", "ApplySequentialCadBindings assigns CAD instances strictly in row order");

                // 批内互换（重绑）允许，批外冲突整体拒绝且不产生半成品
                int swapped = LineupTableForm.ApplySequentialCadBindings(seqProj, new[] { rA.Id, rB.Id }, new[] { MockCad("K2", "S2"), MockCad("K1", "S1") });
                Check(swapped == 2 && rA.ReferenceKey == "K2" && rB.ReferenceKey == "K1", "ApplySequentialCadBindings allows in-batch swap rebind");
                Reject(() => LineupTableForm.ApplySequentialCadBindings(seqProj, new[] { rA.Id, rB.Id }, new[] { MockCad("NEW1", "N1"), MockCad("EXT_KEY", "E1") }),
                    "ApplySequentialCadBindings atomically rejects external conflict");
                Check(rA.ReferenceKey == "K2" && rB.ReferenceKey == "K1", "failed sequential bind leaves records untouched");

                // 13. 类 Excel 编辑体验验证（单击不进编辑、直接打英文/中文自动进编辑且不丢字、Backspace 清空编辑）
                form.SetViewMode(0);
                form.PneumaticView.SelectSubTab(3); // 气缸传感器（4 行：Y1C01H/W + Y1C02H/W，再补一行以确保 >= 2 行）
                var pg = form.PneumaticView.Grid;
                Check(pg.Rows.Count >= 2, "sensor sub-tab has rows for spreadsheet interaction test");
                pg.Focus();
                Application.DoEvents();
                ClickCell(pg, 2, 0); // 单击「描述」单元格
                Check(!pg.IsCurrentCellInEditMode, "single left-click on pGrid cell selects without entering edit mode");
                ClickCell(pg, 2, 0); // 再次单击同一单元格
                Check(!pg.IsCurrentCellInEditMode, "second single left-click on already-selected cell still does not enter edit mode");

                // 英文键入：WM_KEYDOWN + WM_CHAR 不应重复双字
                SendMessage(pg.Handle, WM_KEYDOWN, (IntPtr)0x41, IntPtr.Zero);
                SendMessage(pg.Handle, WM_CHAR, (IntPtr)'a', IntPtr.Zero);
                Application.DoEvents();
                Check(pg.IsCurrentCellInEditMode && pg.EditingControl != null && pg.EditingControl.Text == "a", "ASCII typing enters edit mode with exact single char");
                pg.CancelEdit();
                pg.EndEdit();
                Application.DoEvents();

                // 中文输入法多字上屏（无前置 ASCII WM_KEYDOWN 的连续 WM_CHAR）
                ClickCell(pg, 2, 1);
                Check(!pg.IsCurrentCellInEditMode, "clicking row 1 cell stays non-editing");
                SendMessage(pg.Handle, WM_CHAR, (IntPtr)'气', IntPtr.Zero);
                SendMessage(pg.Handle, WM_CHAR, (IntPtr)'缸', IntPtr.Zero);
                Application.DoEvents();
                Check(pg.IsCurrentCellInEditMode && pg.EditingControl != null && pg.EditingControl.Text == "气缸", "multi-char Chinese typing enters edit mode without dropping chars");
                pg.CancelEdit();
                pg.EndEdit();
                Application.DoEvents();

                // Backspace 清空并进编辑
                SendMessage(pg.Handle, WM_KEYDOWN, (IntPtr)0x08, IntPtr.Zero);
                Application.DoEvents();
                Check(pg.IsCurrentCellInEditMode && pg.EditingControl != null && pg.EditingControl.Text == "", "Backspace clears selected cell and enters edit mode");
                pg.CancelEdit();
                pg.EndEdit();
                Application.DoEvents();

                // 双击普通文本单元格进入编辑
                DoubleClickCell(pg, 2, 1);
                Check(pg.IsCurrentCellInEditMode, "double-click on normal cell enters edit mode");
                pg.CancelEdit();
                pg.EndEdit();
                Application.DoEvents();

                // 14. 右键菜单：单行 vs 多行选项 + 现代白底圆角主题样式
                pg.ClearSelection();
                pg.CurrentCell = pg.Rows[0].Cells[0];
                pg.Rows[0].Cells[0].Selected = true;
                form.PneumaticView.BuildRowContextMenu(0, 0);
                var singleItems = form.PneumaticView.RowMenu.Items.OfType<ToolStripMenuItem>().Select(x => x.Text).ToList();
                Check(singleItems.Contains("进入 3D 点选模型绑定到此行") && singleItems.Contains("绑定 CAD 当前选中件到此行"),
                    "single-row pneumatic context menu offers both 3D pick bind and instant CAD bind");
                Check(form.PneumaticView.RowMenu.BackColor == Color.White && !form.PneumaticView.RowMenu.ShowImageMargin,
                    "pneumatic context menu uses unified white modern skin");

                pg.ClearSelection();
                pg.Rows[0].Cells[0].Selected = true;
                pg.Rows[1].Cells[0].Selected = true;
                form.PneumaticView.BuildRowContextMenu(1, 0);
                var multiItems = form.PneumaticView.RowMenu.Items.OfType<ToolStripMenuItem>().Select(x => x.Text).ToList();
                Check(multiItems.Contains("进入 3D 依次点选绑定选中 2 行") && multiItems.Contains("按顺序绑定 CAD 已选中件到选中 2 行"),
                    "multi-row pneumatic context menu offers sequential 3D pick bind and ordered batch CAD bind");

                // BOM 主表右键菜单与单击不进编辑同样生效
                form.SetViewMode(3);
                form.Grid.Focus();
                Application.DoEvents();
                ClickCell(form.Grid, 4, 0);
                Check(!form.Grid.IsCurrentCellInEditMode, "single left-click on main BOM Grid selects without entering edit mode");
                form.Grid.ClearSelection();
                form.Grid.Rows[0].Cells[1].Selected = true;
                form.Grid.Rows[1].Cells[1].Selected = true;
                form.BuildGridContextMenuForRow(0);
                var bomMenuItems = form.GridMenu.Items.OfType<ToolStripMenuItem>().Select(x => x.Text).ToList();
                Check(bomMenuItems.Contains("进入 3D 依次点选绑定选中 2 行") && bomMenuItems.Contains("按顺序绑定 CAD 已选中件到选中 2 行"),
                    "main BOM Grid context menu offers sequential 3D bind for multi-selected rows");

                // 15. 顶栏「型号库」显眼入口与按钮下拉菜单二次点击关闭（不重开）
                Check(form.ModelLibraryButton != null && form.ModelLibraryButton.Visible && form.ModelLibraryButton.Text == "型号库",
                    "top bar exposes prominent Model Library button");

                form.SetViewMode(0);
                Application.DoEvents();
                var btnValve = form.PneumaticView.AddValveButton;
                var menuValve = form.PneumaticView.AddValveMenu;
                Check(!menuValve.Visible, "addValveMenu starts closed");
                btnValve.PerformClick();
                Application.DoEvents();
                Check(menuValve.Visible, "first click opens addValveMenu");
                btnValve.PerformClick();
                Application.DoEvents();
                Check(!menuValve.Visible, "second click closes addValveMenu instead of reopening");
                btnValve.PerformClick();
                Application.DoEvents();
                Check(menuValve.Visible, "third click reopens addValveMenu cleanly");
                menuValve.Close();
                Application.DoEvents();

                var btnMore = form.MoreMenuButton;
                var menuMore = form.MoreMenu;
                btnMore.PerformClick();
                Application.DoEvents();
                Check(menuMore.Visible, "first click opens moreMenu");
                btnMore.PerformClick();
                Application.DoEvents();
                Check(!menuMore.Visible, "second click closes moreMenu instead of reopening");

                // 16. 删除普通传感器(SE)与电气件(E)中间编号后自动顺序重排 + 撤销恢复
                form.SetViewMode(1); // SE
                form.SetCategory("普通传感器");
                form.AddEmpty(); // SE01
                form.AddEmpty(); // SE02
                form.AddEmpty(); // SE03
                form.AddEmpty(); // SE04
                var seBefore = form.CurrentProject.Records.Where(r => r.Category == "普通传感器").Select(r => r.Number).ToList();
                Check(seBefore.SequenceEqual(new[] { "SE01", "SE02", "SE03", "SE04" }), "SE01..SE04 created sequentially");
                // 删除中间的 SE02
                form.Grid.ClearSelection();
                form.Grid.Rows[1].Cells[1].Selected = true;
                form.DeleteRows();
                var seAfter = form.CurrentProject.Records.Where(r => r.Category == "普通传感器").Select(r => r.Number).ToList();
                Check(seAfter.SequenceEqual(new[] { "SE01", "SE02", "SE03" }), "deleting middle SE02 compacts SE03/SE04 to SE02/SE03");
                form.Undo();
                var seUndo = form.CurrentProject.Records.Where(r => r.Category == "普通传感器").Select(r => r.Number).ToList();
                Check(seUndo.SequenceEqual(new[] { "SE01", "SE02", "SE03", "SE04" }), "Undo restores deleted SE02 and original numbers");

                form.SetCategory("其他电气件");
                form.AddEmpty(); // E03 (已有2条同步的 E01, E02)
                form.AddEmpty(); // E04
                form.Grid.ClearSelection();
                form.Grid.Rows[0].Cells[1].Selected = true; // 删 E01
                form.Grid.Rows[2].Cells[1].Selected = true; // 删 E03
                form.DeleteRows();
                var eAfter = form.CurrentProject.Records.Where(r => r.Category == "其他电气件").Select(r => r.Number).ToList();
                Check(eAfter.SequenceEqual(new[] { "E01", "E02" }), "deleting E01 and E03 compacts remaining E02/E04 to E01/E02");

                form.Close();
            }

            // 17. 分类化型号库纯逻辑与管理窗口验证
            TestModelLibraryClassificationAndForm(tmpDir);

            // 18. 气动槽位与阀岛中间删除自动重排纯逻辑（含 1阀多缸、空阀片保留、CAD位置图同步、阀岛Y*顺排）
            TestDeleteMiddleAutoRenumbering();
        } finally {
            try { Directory.Delete(tmpDir, true); } catch { }
        }

        Console.WriteLine("PNEUMATIC PURE TESTS PASSED: " + passed);
    }

    static void TestModelLibraryClassificationAndForm(string tmpDir) {
        var cat = LineupModelCatalog.LoadSeed(null);
        cat.EnsureSubCategories();
        Check(cat.Entries.Count >= 120, "model catalog seed loads with >= 120 entries");
        Check(cat.Entries.All(e => !string.IsNullOrWhiteSpace(e.EffectiveSubCategory)), "every catalog entry gets a non-empty sub-category");
        Check(cat.Entries.Any(e => e.Category == "气缸" && e.EffectiveSubCategory == "薄型气缸"), "cylinders include 薄型气缸 sub-category");
        Check(cat.Entries.Any(e => e.Category == "气缸" && e.EffectiveSubCategory == "导杆气缸"), "cylinders include 导杆气缸 sub-category");
        Check(cat.Entries.Any(e => e.Category == "普通传感器" && e.EffectiveSubCategory == "光电/激光传感器"), "sensors include 光电/激光传感器 sub-category");
        Check(cat.Entries.Any(e => e.Category == "气缸传感器" && e.EffectiveSubCategory == "磁性开关"), "cylinder sensors include 磁性开关 sub-category");
        Check(!cat.Entries.Any(e => e.Category == "气缸" && e.Model == "AR20-02BG-D"), "pressure regulator AR20-02BG-D moved out of 气缸");

        // CRUD：新增、更新、去重、删除、分类导出与导入回环
        var customEntry = new LineupModelEntry { Category = "气缸", SubCategory = "薄型气缸", Model = "CDQ2B32-25DZ-TEST", Brand = "SMC", Notes = "测试薄型" };
        cat.AddOrUpdate(null, customEntry);
        var added = cat.Entries.Last();
        Check(added.Model == "CDQ2B32-25DZ-TEST" && added.SubCategory == "薄型气缸" && added.Notes == "测试薄型", "AddOrUpdate adds new categorized entry");
        Reject(() => cat.AddOrUpdate(null, new LineupModelEntry { Category = "气缸", Model = "CDQ2B32-25DZ-TEST", Brand = "SMC" }), "AddOrUpdate rejects duplicate model");
        Reject(() => cat.AddOrUpdate(null, new LineupModelEntry { Category = "普通传感器", Model = "E3Z-TEST", Brand = "OMRON" }), "AddOrUpdate requires connector for sensors");
        cat.AddOrUpdate(added, new LineupModelEntry { Category = "气缸", SubCategory = "导杆气缸", Model = "CDQ2B32-25DZ-TEST", Brand = "SMC", Notes = "已改分类" });
        Check(added.SubCategory == "导杆气缸" && added.Notes == "已改分类", "AddOrUpdate edits existing entry in place");

        string csv = LineupCsv.Write(LineupModelCatalog.ExportRows(cat.Entries));
        var reimported = LineupModelCatalog.Import(csv, ',', "气缸");
        var hit = reimported.Entries.FirstOrDefault(e => e.Model == "CDQ2B32-25DZ-TEST");
        Check(hit != null && hit.Category == "气缸" && hit.SubCategory == "导杆气缸" && hit.Notes == "已改分类", "categorized CSV export/import round-trips sub-category and notes");

        int removed = cat.RemoveEntries(new[] { added });
        Check(removed == 1 && !cat.Entries.Any(e => e.Model == "CDQ2B32-25DZ-TEST"), "RemoveEntries removes entry cleanly");

        // 验证管理模式与选型模式下的分类筛选、下拉菜单二次点击收起
        string libPath = Path.Combine(tmpDir, "test-lib.xml");
        cat.SeedInitialized = true;
        cat.Save(libPath);
        bool persisted = false;
        using (var libForm = new LineupModelLibraryForm(cat, libPath, false, "", 0, null, c => { c.Save(libPath); persisted = true; }, null, () => LineupModelCatalog.LoadSeed(null))) {
            libForm.Show();
            Application.DoEvents();
            libForm.SelectCategoryForTest("气缸");
            Check(libForm.VisibleEntriesForTest.Count > 0 && libForm.VisibleEntriesForTest.All(e => e.Category == "气缸"), "Manage mode filters cleanly to 气缸");
            libForm.SelectCategoryForTest("气缸", "薄型气缸");
            Check(libForm.VisibleEntriesForTest.Count > 0 && libForm.VisibleEntriesForTest.All(e => e.Category == "气缸" && e.EffectiveSubCategory == "薄型气缸"), "Manage mode filters cleanly to 气缸 · 薄型气缸");
            libForm.SelectCategoryForTest("普通传感器", "光电/激光传感器");
            Check(libForm.VisibleEntriesForTest.Count > 0 && libForm.VisibleEntriesForTest.All(e => e.Category == "普通传感器" && e.EffectiveSubCategory == "光电/激光传感器"), "Manage mode filters cleanly to 普通传感器 · 光电/激光传感器");

            libForm.ImportButton.PerformClick();
            Application.DoEvents();
            Check(libForm.ImportMenu.Visible, "Library Import dropdown opens on first click");
            libForm.ImportButton.PerformClick();
            Application.DoEvents();
            Check(!libForm.ImportMenu.Visible, "Library Import dropdown closes on second click");

            libForm.ExportButton.PerformClick();
            Application.DoEvents();
            Check(libForm.ExportMenu.Visible, "Library Export dropdown opens on first click");
            libForm.ExportButton.PerformClick();
            Application.DoEvents();
            Check(!libForm.ExportMenu.Visible, "Library Export dropdown closes on second click");
            libForm.Close();
        }
        Check(!persisted, "browsing library does not write disk unnecessarily");
    }

    static void TestDeleteMiddleAutoRenumbering() {
        var p = new LineupProject { ProjectCode = "SK26.01" };
        string y1 = LineupPneumaticOps.CreateIsland(p, "OK170", 1, "阀岛", "SS5Y5-20-08");
        string y2 = LineupPneumaticOps.CreateIsland(p, "OK170", 2, "阀岛", "SS5Y5-20-04");
        string y3 = LineupPneumaticOps.CreateIsland(p, "OK170", 3, "汇流排", "SS5Y7-20-05");

        var b1 = LineupPneumaticOps.AddCylinderBundle(p, y1, true, 1, false); // V01 / C01
        var b2 = LineupPneumaticOps.AddCylinderBundle(p, y1, true, 1, false); // V02 / C02
        var b3 = LineupPneumaticOps.AddCylinderBundle(p, y1, true, 1, false); // V03 / C03
        var b3Extra = LineupPneumaticOps.AddCylinderToValve(p, b3.ValveId, 1); // V03 / C03-1, C03-2
        var b4Spare = LineupPneumaticOps.AddValveSlices(p, y1, true, 1);      // V04 预留盖板

        // CAD 位置图挂载 C01, C02, C03-1, C03-2
        var pg = new LineupCadPage { Name = "总图" };
        pg.Points.Add(new LineupLocationPoint { Id = b1.CylinderId, Number = "OK170-Y1C01", State = "ok" });
        pg.Points.Add(new LineupLocationPoint { Id = b2.CylinderId, Number = "OK170-Y1C02", State = "ok" });
        pg.Points.Add(new LineupLocationPoint { Id = b3.CylinderId, Number = "OK170-Y1C03-1", State = "ok" });
        pg.Points.Add(new LineupLocationPoint { Id = b3Extra.CylinderId, Number = "OK170-Y1C03-2", State = "ok" });
        p.CadPages.Add(pg);

        // 删除中间槽位 02（整条阀路 V02 + C02 + H/W）
        LineupPneumaticOps.DeleteChannelItem(p, b2.ValveId, b2.CylinderId, true);
        Check(p.Records.Single(r => r.Id == b1.ValveId).Number == "OK170-Y1V01"
            && p.Records.Single(r => r.Id == b1.CylinderId).Number == "OK170-Y1C01", "slot 01 stays unchanged");
        Check(p.Records.Single(r => r.Id == b3.ValveId).Number == "OK170-Y1V02"
            && p.Records.Single(r => r.Id == b3.CylinderId).Number == "OK170-Y1C02-1"
            && p.Records.Single(r => r.Id == b3Extra.CylinderId).Number == "OK170-Y1C02-2",
            "deleting middle slot 02 compacts slot 03 (1-valve-2-cylinders) to V02 / C02-1 / C02-2");
        Check(p.Records.Any(r => r.Number == "OK170-Y1C02-1H") && p.Records.Any(r => r.Number == "OK170-Y1C02-2W"),
            "compacting slot 03 to 02 cascades all H/W cylinder sensors");
        Check(p.Records.Single(r => r.Id == b4Spare[0]).Number == "OK170-Y1V03",
            "spare valve slice V04 compacts to V03");
        Check(pg.Points.Count == 3
            && !pg.Points.Any(pt => pt.Id == b2.CylinderId)
            && pg.Points.Single(pt => pt.Id == b3.CylinderId).Number == "OK170-Y1C02-1"
            && pg.Points.Single(pt => pt.Id == b3Extra.CylinderId).Number == "OK170-Y1C02-2",
            "CAD location page removes deleted point and syncs renumbered C02-1 / C02-2");
        Check(p.Validate().Count == 0, "all hierarchy links remain valid after middle slot compaction");

        // 仅删气缸保留空阀片：空阀片仍占槽位 01，因此 V02/V03 不应移位
        LineupPneumaticOps.DeleteChannelItem(p, b1.ValveId, b1.CylinderId, false);
        Check(p.Records.Single(r => r.Id == b1.ValveId).Number == "OK170-Y1V01"
            && p.Records.Single(r => r.Id == b3.ValveId).Number == "OK170-Y1V02",
            "deleting only cylinder while keeping empty valve V01 preserves slot positions");

        // 再把空阀片 V01 也删掉：V02 → V01, V03 → V02
        LineupPneumaticOps.DeleteChannelItem(p, b1.ValveId, "", true);
        Check(p.Records.Single(r => r.Id == b3.ValveId).Number == "OK170-Y1V01"
            && p.Records.Single(r => r.Id == b3.CylinderId).Number == "OK170-Y1C01-1"
            && p.Records.Single(r => r.Id == b4Spare[0]).Number == "OK170-Y1V02",
            "deleting empty valve V01 compacts remaining slots to V01 / V02");

        // 删除中间阀岛 Y2：Y3 自动顺排为 Y2，并同步其阀片/气缸/磁开/已同步 E**
        var y3Bundle = LineupPneumaticOps.AddCylinderBundle(p, y3, true, 1, false);
        LineupPneumaticOps.SyncIslandsToElectrical(p);
        LineupPneumaticOps.DeleteIsland(p, y2, true);
        Check(p.Islands.Any(i => i.Key == "OK170-Y2") && !p.Islands.Any(i => i.Key == "OK170-Y3"),
            "deleting middle island Y2 compacts Y3 island key to Y2");
        Check(p.Records.Single(r => r.Id == y3Bundle.ValveId).Number == "OK170-Y2V01"
            && p.Records.Single(r => r.Id == y3Bundle.CylinderId).Number == "OK170-Y2C01"
            && p.Records.Any(r => r.Number == "OK170-Y2C01H"),
            "deleting middle island Y2 cascades Y3 valves, cylinders, and H/W sensors to Y2");
        Check(p.Records.Any(r => r.Category == "其他电气件" && r.Notes == "OK170-Y2"),
            "deleting middle island Y2 updates synced E** island note to Y2");
        Check(p.Validate().Count == 0, "all links remain valid after island compaction");
    }
}
