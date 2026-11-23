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

            // 19. Lineup 3D→2D 投影映射、实体三角面积质心与 ROI 抗悬停差分纯逻辑
            TestLocation3DProjectionMath();

            // 20. 气动系统（阀岛树）与常规表格视图上移/下移选中行（单选/多选/一阀多缸/边界/撤销/右键菜单）
            TestPneumaticAndGridRowMove(tmpDir);
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

    static void TestLocation3DProjectionMath() {
        double nx, ny;
        // 宽屏窗口（winAspect=1284/715≈1.7958 > imgAspect=1.6）：X 直接映射，Y 按 imgAspect/winAspect 缩放
        Check(LineupLocation.MapGlProjectionToImage(0.4, -0.5, -5.0, false, 1284.0 / 715.0, 1.6, out nx, out ny), "wide ortho projection succeeds");
        Check(Math.Abs(nx - 0.7) < 1e-6 && Math.Abs(ny - (0.5 - 0.5 * (-0.5) * (1.6 / (1284.0 / 715.0)))) < 1e-6, "wide ortho fit-contain math exact");

        // 窄屏窗口（winAspect=682/820≈0.8317 < imgAspect=1.6）：X 按 winAspect/imgAspect 缩放，Y 直接映射
        Check(LineupLocation.MapGlProjectionToImage(-0.6, 0.2, -3.0, true, 682.0 / 820.0, 1.6, out nx, out ny), "narrow persp projection succeeds");
        Check(Math.Abs(nx - (0.5 + 0.5 * (-0.6) * ((682.0 / 820.0) / 1.6))) < 1e-6 && Math.Abs(ny - 0.4) < 1e-6, "narrow persp fit-contain math exact");

        // 透视模式下相机背面点（vz >= 0）必须剔除；正交模式下允许任意 vz
        Check(!LineupLocation.MapGlProjectionToImage(0.1, 0.1, 0.2, true, 1.6, 1.6, out nx, out ny), "persp rejects point behind camera");
        Check(LineupLocation.MapGlProjectionToImage(0.1, 0.1, 0.2, false, 1.6, 1.6, out nx, out ny), "ortho allows positive vz");
        Check(!LineupLocation.MapGlProjectionToImage(double.NaN, 0, -1, false, 1.6, 1.6, out nx, out ny), "rejects NaN coordinates");

        // 空心矩形框（4 条边组成中空环，几何质心在 (0.5,0.5) 空白处）：SelectProjectedTriangleAnchor 必须落回真实实体三角面上
        var hollowTris = new List<PointF> {
            // 顶边实体条 (y=0.20..0.24)
            new PointF(0.20f, 0.20f), new PointF(0.80f, 0.20f), new PointF(0.80f, 0.24f),
            new PointF(0.20f, 0.20f), new PointF(0.80f, 0.24f), new PointF(0.20f, 0.24f),
            // 底边实体条 (y=0.76..0.80)
            new PointF(0.20f, 0.76f), new PointF(0.80f, 0.76f), new PointF(0.80f, 0.80f),
            new PointF(0.20f, 0.76f), new PointF(0.80f, 0.80f), new PointF(0.20f, 0.80f),
            // 左边实体条 (x=0.20..0.24)
            new PointF(0.20f, 0.24f), new PointF(0.24f, 0.24f), new PointF(0.24f, 0.76f),
            new PointF(0.20f, 0.24f), new PointF(0.24f, 0.76f), new PointF(0.20f, 0.76f),
            // 右边实体条 (x=0.76..0.80)
            new PointF(0.76f, 0.24f), new PointF(0.80f, 0.24f), new PointF(0.80f, 0.76f),
            new PointF(0.76f, 0.24f), new PointF(0.80f, 0.76f), new PointF(0.76f, 0.76f)
        };
        Check(LineupLocation.SelectProjectedTriangleAnchor(hollowTris, out nx, out ny), "hollow frame selects valid solid anchor");
        bool insideHollowHole = nx > 0.30 && nx < 0.70 && ny > 0.30 && ny < 0.70;
        Check(!insideHollowHole, "hollow frame anchor snaps to solid rim instead of empty center");

        // 面积加权抗网格密度偏移：一端 40 个微小倒角三角 vs 另一端 1 个大缸体三角 → 质心必须留在缸体大面上
        var weightedTris = new List<PointF> {
            new PointF(0.30f, 0.40f), new PointF(0.50f, 0.40f), new PointF(0.40f, 0.60f) // 大面中心 (0.40, 0.467)，面积 0.02
        };
        for (int i = 0; i < 40; i++) {
            weightedTris.Add(new PointF(0.80f, 0.50f));
            weightedTris.Add(new PointF(0.802f, 0.50f));
            weightedTris.Add(new PointF(0.801f, 0.502f)); // 极小面积 2e-6
        }
        Check(LineupLocation.SelectProjectedTriangleAnchor(weightedTris, out nx, out ny), "area-weighted anchor succeeds");
        Check(nx < 0.45, "area weighting prevents tiny dense triangles from pulling centroid off main body");

        // 画面外包围盒拒识，部分入画裁切保留
        Check(!LineupLocation.SelectProjectedBoxAnchor(new[] { new PointF(1.2f, 0.4f), new PointF(1.5f, 0.6f) }, out nx, out ny), "off-screen box rejected");
        Check(LineupLocation.SelectProjectedBoxAnchor(new[] { new PointF(-0.4f, 0.2f), new PointF(0.3f, 0.8f), new PointF(-0.05f, 0.5f) }, out nx, out ny)
            && nx > 0.05 && nx < 0.25 && Math.Abs(ny - 0.5) < 0.05, "partially clipped box anchors inside visible portion");

        // ROI 差分免疫鼠标悬停预高亮（背景外有一大片假悬停像素，ROI 内只有目标气缸像素）
        using (var bg = new Bitmap(200, 120))
        using (var hl = new Bitmap(200, 120)) {
            using (var g = Graphics.FromImage(bg)) g.Clear(Color.White);
            using (var g = Graphics.FromImage(hl)) {
                g.Clear(Color.White);
                g.FillRectangle(Brushes.Orange, 10, 10, 40, 100); // 4000 px 模拟鼠标悬停八角立柱
                g.FillRectangle(Brushes.Cyan, 140, 45, 16, 16);   // 256 px 真实目标气缸
            }
            var roiPt = LineupLocation.DetectDifferenceInRoi(bg, hl, hl, new Rectangle(130, 35, 36, 36), new PointF(0.74f, 0.44f));
            Check(roiPt.State == "ok" && roiPt.X > 0.68 && roiPt.X < 0.80 && roiPt.Y > 0.35 && roiPt.Y < 0.55,
                "ROI highlight diff ignores massive hover highlight outside ROI");
        }
    }

    static void TestPneumaticAndGridRowMove(string tmpDir) {
        // A. 纯逻辑：气动阀路移动（单选、多选、一阀多缸、预留位、吹气后缀、磁开明细、边界）
        var p = new LineupProject { ProjectCode = "SK26.01" };
        string y1 = LineupPneumaticOps.CreateIsland(p, "OK170", 1, "阀岛", "SS5Y5-20-08");
        var b1 = LineupPneumaticOps.AddCylinderBundle(p, y1, true, 1, false); // V01 / C01
        var b2 = LineupPneumaticOps.AddCylinderBundle(p, y1, true, 1, false); // V02 / C02
        var b2Extra = LineupPneumaticOps.AddCylinderToValve(p, b2.ValveId, 1); // V02 / C02-1, C02-2
        var b3Spare = LineupPneumaticOps.AddValveSlices(p, y1, true, 1);      // V03 预留位
        var b4Blow = LineupPneumaticOps.AddCylinderBundle(p, y1, true, 0, true); // V04 / C04 吹气
        p.Records.Single(r => r.Id == b4Blow.CylinderId).Number = "OK170-Y1C04a"; // 验证字母后缀保留

        LineupPneumaticOps.SyncCylinderDescription(p, p.Records.Single(r => r.Id == b1.CylinderId), "", "定位缸");
        LineupPneumaticOps.SyncCylinderDescription(p, p.Records.Single(r => r.Id == b2.CylinderId), "", "左夹紧");
        LineupPneumaticOps.SyncCylinderDescription(p, p.Records.Single(r => r.Id == b2Extra.CylinderId), "", "右夹紧");
        LineupPneumaticOps.SyncCylinderDescription(p, p.Records.Single(r => r.Id == b4Blow.CylinderId), "", "吹气嘴");

        var pg = new LineupCadPage { Name = "总图" };
        pg.Points.Add(new LineupLocationPoint { Id = b1.CylinderId, Number = "OK170-Y1C01", State = "ok", X = 0.2, Y = 0.2 });
        pg.Points.Add(new LineupLocationPoint { Id = b2.CylinderId, Number = "OK170-Y1C02-1", State = "ok", X = 0.4, Y = 0.4 });
        pg.Points.Add(new LineupLocationPoint { Id = b2Extra.CylinderId, Number = "OK170-Y1C02-2", State = "ok", X = 0.6, Y = 0.6 });
        p.CadPages.Add(pg);

        string focusId, msg;
        // 1. 顶部上移应返回 false（不触发无意义改动）
        Check(!LineupPneumaticOps.MoveIslandRows(p, y1, new[] { b1.CylinderId }, 0, -1, out focusId, out msg),
            "moving top pneumatic channel up is a clean no-op");

        // 2. 一阀多缸内上移 C02-2 → 与 C02-1 交换 -1/-2 并级联磁开与 CAD 页
        Check(LineupPneumaticOps.MoveIslandRows(p, y1, new[] { b2Extra.CylinderId }, 0, -1, out focusId, out msg),
            "moving second cylinder of shared valve up succeeds");
        Check(p.Records.Single(r => r.Id == b2Extra.CylinderId).Number == "OK170-Y1C02-1"
            && p.Records.Single(r => r.Id == b2.CylinderId).Number == "OK170-Y1C02-2"
            && pg.Points.Single(pt => pt.Id == b2Extra.CylinderId).Number == "OK170-Y1C02-1"
            && p.Records.Any(r => r.CylinderId == b2Extra.CylinderId && r.Number == "OK170-Y1C02-1H"),
            "shared valve intra-move swaps -1/-2 numbers, H/W sensors, and CAD page numbers");

        // 3. 再次上移现位于第一条的 C02-1 → 触碰同阀上边界，自动将整个 V02 阀位整体上移到 V01
        Check(LineupPneumaticOps.MoveIslandRows(p, y1, new[] { b2Extra.CylinderId }, 0, -1, out focusId, out msg),
            "moving first cylinder of shared valve up moves entire valve slot V02 above V01");
        Check(p.Records.Single(r => r.Id == b2.ValveId).Number == "OK170-Y1V01"
            && p.Records.Single(r => r.Id == b2Extra.CylinderId).Number == "OK170-Y1C01-1"
            && p.Records.Single(r => r.Id == b2.CylinderId).Number == "OK170-Y1C01-2"
            && p.Records.Single(r => r.Id == b1.ValveId).Number == "OK170-Y1V02"
            && p.Records.Single(r => r.Id == b1.CylinderId).Number == "OK170-Y1C02"
            && p.Records.Any(r => r.CylinderId == b1.CylinderId && r.Number == "OK170-Y1C02W")
            && pg.Points.Single(pt => pt.Id == b1.CylinderId).Number == "OK170-Y1C02",
            "swapping valve slot 02 above 01 cascades V**/C**/H/W and CAD points cleanly");

        // 4. 上移吹气阀路 V04(C04a) 越过预留盖板 V03 → 保留 'a' 后缀，预留位变 V04
        Check(LineupPneumaticOps.MoveIslandRows(p, y1, new[] { b4Blow.CylinderId }, 0, -1, out focusId, out msg),
            "moving blow cylinder slot up past spare valve succeeds");
        Check(p.Records.Single(r => r.Id == b4Blow.ValveId).Number == "OK170-Y1V03"
            && p.Records.Single(r => r.Id == b4Blow.CylinderId).Number == "OK170-Y1C03a"
            && p.Records.Single(r => r.Id == b3Spare[0]).Number == "OK170-Y1V04",
            "blow suffix 'a' and spare valve slot swap cleanly");

        // 5. 多选两行（V03 吹气 + V04 预留位）同时上移
        Check(LineupPneumaticOps.MoveIslandRows(p, y1, new[] { b4Blow.ValveId, b3Spare[0] }, 2, -1, out focusId, out msg),
            "multi-selecting two valve rows and moving up succeeds");
        var vOrder = LineupPneumaticOps.BuildChannelRows(p, y1).Select(ch => ch.Valve.Id).Distinct().ToList();
        Check(vOrder.SequenceEqual(new[] { b2.ValveId, b4Blow.ValveId, b3Spare[0], b1.ValveId }),
            "multi-select move up shifts both slots past V02 preserving relative order");
        Check(p.Validate().Count == 0, "all hierarchy links remain valid after all pneumatic moves");

        // 6. 磁开明细子页：同气缸内把 W 上移到 H 前面
        var b1SensBefore = LineupPneumaticOps.BuildChannelRows(p, y1).Single(ch => ch.Cylinder != null && ch.Cylinder.Id == b1.CylinderId).Sensors;
        Check(b1SensBefore.Count == 2, "b1 has 2 H/W sensors");
        string secondSensId = b1SensBefore[1].Id;
        Check(LineupPneumaticOps.MoveIslandRows(p, y1, new[] { secondSensId }, 3, -1, out focusId, out msg),
            "subTab 3 moves second sensor above first sensor within cylinder");
        var b1SensAfter = LineupPneumaticOps.BuildChannelRows(p, y1).Single(ch => ch.Cylinder != null && ch.Cylinder.Id == b1.CylinderId).Sensors;
        Check(b1SensAfter[0].Id == secondSensId, "sensor order updated in subTab 3");

        // B. 真实 WinForms 窗口验证：气动系统默认视图 + 普通传感器视图 + 右键菜单 + 撤销
        string asm = Path.Combine(tmpDir, "move-ui.asm");
        File.WriteAllText(asm, "dummy");
        var uiProj = new LineupProject { AssemblyFile = asm, ProjectCode = "SK26.01" };
        string iy = LineupPneumaticOps.CreateIsland(uiProj, "OK170", 1, "阀岛", "SS5Y5-20-08");
        var c1 = LineupPneumaticOps.AddCylinderBundle(uiProj, iy, true, 1, false);
        var c2 = LineupPneumaticOps.AddCylinderBundle(uiProj, iy, true, 1, false);
        LineupPneumaticOps.SyncCylinderDescription(uiProj, uiProj.Records.Single(r => r.Id == c1.CylinderId), "", "一缸");
        LineupPneumaticOps.SyncCylinderDescription(uiProj, uiProj.Records.Single(r => r.Id == c2.CylinderId), "", "二缸");
        uiProj.Records.Add(new LineupRecord { Category = "普通传感器", Number = "SE01", Description = "检测1" });
        uiProj.Records.Add(new LineupRecord { Category = "普通传感器", Number = "SE02", Description = "检测2" });
        uiProj.Records.Add(new LineupRecord { Category = "普通传感器", Number = "SE03", Description = "检测3" });
        uiProj.Save(Path.ChangeExtension(asm, ".lineup.xml"));

        using (var form = new LineupTableForm(asm, Path.Combine(tmpDir, "move-lib.xml"))) {
            form.Show();
            Application.DoEvents();
            Check(form.ActiveViewMode == 0, "form opens in pneumatic mode (0)");
            var pgGrid = form.PneumaticView.Grid;
            Check(pgGrid.Rows.Count == 2, "pneumatic grid shows 2 channels");
            Check(Convert.ToString(pgGrid.Rows[0].Cells["气缸描述"].Value) == "一缸"
                && Convert.ToString(pgGrid.Rows[1].Cells["气缸描述"].Value) == "二缸",
                "initial pneumatic order is 一缸 then 二缸");

            // 选中第 2 行（二缸）并上移
            pgGrid.ClearSelection();
            pgGrid.CurrentCell = pgGrid.Rows[1].Cells["气缸描述"];
            pgGrid.Rows[1].Cells["气缸描述"].Selected = true;
            Application.DoEvents();

            form.PneumaticView.BuildRowContextMenu(1, 4);
            var pMenuTexts = form.PneumaticView.RowMenu.Items.OfType<ToolStripMenuItem>().Select(x => x.Text).ToList();
            Check(pMenuTexts.Any(t => t.Contains("上移选中行")) && pMenuTexts.Any(t => t.Contains("下移选中行")),
                "pneumatic context menu exposes Move Up / Move Down");

            form.MoveRows(-1);
            Application.DoEvents();
            Check(Convert.ToString(pgGrid.Rows[0].Cells["气缸描述"].Value) == "二缸"
                && Convert.ToString(pgGrid.Rows[0].Cells["气缸号"].Value) == "OK170-Y1C01"
                && Convert.ToString(pgGrid.Rows[1].Cells["气缸描述"].Value) == "一缸"
                && Convert.ToString(pgGrid.Rows[1].Cells["气缸号"].Value) == "OK170-Y1C02",
                "MoveRows(-1) in pneumatic view visibly swaps rows and updates slot numbers");
            Check(pgGrid.CurrentCell != null && pgGrid.CurrentCell.RowIndex == 0,
                "selection follows moved row to index 0 for repeated moving");

            // 再下移一次恢复
            form.MoveRows(1);
            Application.DoEvents();
            Check(Convert.ToString(pgGrid.Rows[0].Cells["气缸描述"].Value) == "一缸"
                && Convert.ToString(pgGrid.Rows[1].Cells["气缸描述"].Value) == "二缸",
                "MoveRows(1) in pneumatic view moves selected row back down");

            // 撤销一次应回到 二缸在前
            form.Undo();
            Application.DoEvents();
            Check(Convert.ToString(pgGrid.Rows[0].Cells["气缸描述"].Value) == "二缸",
                "Undo restores pneumatic row order");

            // 切换到普通传感器视图（Mode 1）测试上移/下移与右键菜单
            form.SetViewMode(1);
            Application.DoEvents();
            Check(form.Grid.Rows.Count == 3, "SE view shows 3 rows");
            form.Grid.ClearSelection();
            form.Grid.CurrentCell = form.Grid.Rows[2].Cells["描述"];
            form.Grid.Rows[2].Cells["描述"].Selected = true;
            Application.DoEvents();

            form.BuildGridContextMenuForRow(2);
            var gMenuTexts = form.GridMenu.Items.OfType<ToolStripMenuItem>().Select(x => x.Text).ToList();
            Check(gMenuTexts.Any(t => t.Contains("上移选中行")) && gMenuTexts.Any(t => t.Contains("下移选中行")),
                "main grid context menu exposes Move Up / Move Down");

            form.MoveRows(-1);
            Application.DoEvents();
            Check(Convert.ToString(form.Grid.Rows[1].Cells["编号"].Value) == "SE03"
                && Convert.ToString(form.Grid.Rows[2].Cells["编号"].Value) == "SE02"
                && form.Grid.CurrentCell != null && form.Grid.CurrentCell.RowIndex == 1,
                "MoveRows(-1) in SE view moves SE03 above SE02 and keeps selection on moved row");

            form.MoveRows(-1);
            Application.DoEvents();
            Check(Convert.ToString(form.Grid.Rows[0].Cells["编号"].Value) == "SE03",
                "repeated MoveRows(-1) moves SE03 to top cleanly");
            form.Close();
        }
    }
}
