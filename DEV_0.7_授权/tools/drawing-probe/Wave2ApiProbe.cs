using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using D = SolidEdgeDraft;
using F = SolidEdgeFramework;
using S = SolidEdgeFrameworkSupport;
using TianGongCadSuite;

// Wave 2 天工 225 COM 接口真机探针：
// 一次性实测 5 个新原生接口在 SK先惠 模板下的真实参数、坐标约定与可用性：
//   1. Sheets.AddSheet + 继承公司背景图框；
//   2. DrawingView.BreakLinePairs.Add + DisplayBreakLinesInBrokenState（断裂视图）；
//   3. HoleTables2.Add + AddNewXYoriginEx + AddHole + SetOrigin（原生孔表）及 Tables.Add 兜底；
//   4. DrawingViews.AddDetailView（局部放大视图 + 放第 2 页 / 空位）；
//   5. SaveCopyAs PDF（验证多页图导出全部页）。
class Wave2ApiProbe {
    static void Idle(F.Application app){ for (int k = 0; k < 6; k++) { try { app.DoIdle(); } catch { } } }

    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = Encoding.UTF8;
        string outDir = args[0];
        string slenderPart = args[1]; // 02-023.par (1325mm)
        string multiHolePart = args[2]; // 02-031.par (多孔大板)
        Directory.CreateDirectory(outDir);

        var app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application");
        try { app.DisplayAlerts = false; } catch { }
        string tplA3 = DrawingTemplates.FindCompany(app, "A3");
        Console.WriteLine("TEMPLATE_A3=" + tplA3);

        // ---- 探针 A：多页图纸 + 断裂视图（02-023 细长件）----
        D.DraftDocument dft1 = null;
        try {
            dft1 = (D.DraftDocument)app.Documents.Add("SolidEdge.DraftDocument", tplA3);
            Idle(app);
            D.Sheet s1 = dft1.ActiveSheet;
            string bgName = "";
            try { bgName = s1.Background != null ? s1.Background.Name : ""; } catch { }
            Console.WriteLine("S1_NAME=" + s1.Name + " BG=" + bgName + " BGVIS=" + s1.BackgroundVisible);

            var link = dft1.ModelLinks.Add(slenderPart);
            var main = s1.DrawingViews.AddPartView(link, D.ViewOrientationConstants.igTopView, 0.2, 0.16, 0.18, D.PartDrawingViewTypeConstants.sePartDesignedView);
            main.SetViewOrientation(1, 0, 0, 0, 1, 0); // 1325mm 沿水平 X
            main.Update(); Idle(app);
            double x0, y0, x1, y1; main.Range(out x0, out y0, out x1, out y1);
            Console.WriteLine("BEFORE_BREAK range=(" + Math.Round(x0 * 1000, 1) + "," + Math.Round(y0 * 1000, 1) + ")-(" + Math.Round(x1 * 1000, 1) + "," + Math.Round(y1 * 1000, 1) + ") W=" + Math.Round((x1 - x0) * 1000, 1));

            // 尝试加竖向断裂线（沿 X 方向裁掉中间空段）
            try {
                double mx = (x0 + x1) / 2.0;
                double span = (x1 - x0);
                var bp = main.BreakLinePairs.Add(D.BreakLinePairDirConstants.igBreakLinePairDirConstants_Vertical, mx - span * 0.20, mx + span * 0.20, false);
                Console.WriteLine("BREAK_ADDED=" + (bp != null) + " count=" + main.BreakLinePairs.Count);
                main.DisplayBreakLinesInBrokenState = true;
                main.Update(); Idle(app);
                double bx0, by0, bx1, by1; main.Range(out bx0, out by0, out bx1, out by1);
                Console.WriteLine("AFTER_BREAK_VERT range=(" + Math.Round(bx0 * 1000, 1) + "," + Math.Round(by0 * 1000, 1) + ")-(" + Math.Round(bx1 * 1000, 1) + "," + Math.Round(by1 * 1000, 1) + ") W=" + Math.Round((bx1 - bx0) * 1000, 1));
                // 测试放大比例到 1:2 后再量
                main.ScaleFactor = 0.5;
                main.Update(); Idle(app);
                main.Range(out bx0, out by0, out bx1, out by1);
                Console.WriteLine("AFTER_BREAK_SCALE05 W=" + Math.Round((bx1 - bx0) * 1000, 1) + " H=" + Math.Round((by1 - by0) * 1000, 1));
            } catch (Exception ex) { Console.WriteLine("BREAK_ERR=" + ex.Message); }

            // 测试加第 2 页图纸并继承背景图框
            try {
                D.Sheet s2 = dft1.Sheets.AddSheet("Sheet2", D.SheetSectionTypeConstants.igWorkingSection, Type.Missing, s1);
                Idle(app);
                try { s2.Background = s1.Background; s2.BackgroundVisible = true; } catch (Exception bge) { Console.WriteLine("S2_BG_PROP_ERR=" + bge.Message); }
                try { s2.SheetSetup.SheetSizeOption = s1.SheetSetup.SheetSizeOption; } catch { }
                Console.WriteLine("S2_ADDED name=" + s2.Name + " bg=" + (s2.Background != null ? s2.Background.Name : "null") + " sheets=" + dft1.Sheets.Count);
                s2.Activate(); Idle(app);
                var v2 = s2.DrawingViews.AddPartView(link, D.ViewOrientationConstants.igBottomView, 0.2, 0.16, 0.16, D.PartDrawingViewTypeConstants.sePartDesignedView);
                Idle(app);
                Console.WriteLine("S2_VIEW_OK=" + (v2 != null));
            } catch (Exception ex) { Console.WriteLine("S2_ERR=" + ex.Message); }

            dft1.SaveAs(Path.Combine(outDir, "probe-break-s2.dft"));
            try {
                object old = null;
                app.GetGlobalParameter(F.ApplicationGlobalConstants.seApplicationGlobalDraftSaveAsPDFSheetOptions, ref old);
                // 查全部页导出选项值（1 = 当前页？0 = 全部页？顺手打印 old）
                Console.WriteLine("PDF_OPT_DEFAULT=" + old);
                app.SetGlobalParameter(F.ApplicationGlobalConstants.seApplicationGlobalDraftSaveAsPDFSheetOptions, 0);
                dft1.SaveCopyAs(Path.Combine(outDir, "probe-break-s2.pdf"));
                app.SetGlobalParameter(F.ApplicationGlobalConstants.seApplicationGlobalDraftSaveAsPDFSheetOptions, old);
            } catch (Exception ex) { Console.WriteLine("PDF_ERR=" + ex.Message); }
        } catch (Exception ex) { Console.WriteLine("PROBE_A_ERR=" + ex); }
        finally { if (dft1 != null) try { dft1.Close(false); } catch { } }

        // ---- 探针 B：局部放大视图 DetailView + 原生孔表 HoleTables2 + 通用表 Tables（02-031 多孔板）----
        D.DraftDocument dft2 = null;
        try {
            dft2 = (D.DraftDocument)app.Documents.Add("SolidEdge.DraftDocument", tplA3);
            Idle(app);
            D.Sheet s1 = dft2.ActiveSheet;
            var link = dft2.ModelLinks.Add(multiHolePart);
            var main = s1.DrawingViews.AddPartView(link, D.ViewOrientationConstants.igTopView, 0.125, 0.15, 0.19, D.PartDrawingViewTypeConstants.sePartDesignedView);
            main.Update(); Idle(app);
            var edges = DrawingViewRead.Edges(main);
            var circles = edges.Where(e => e.Kind == DwgKind.Circle).ToList();
            Console.WriteLine("MULTI_HOLES_CIRCLES=" + circles.Count);

            // 1. 测试 AddDetailView
            try {
                if (circles.Count > 0) {
                    var c0 = circles[0];
                    var cp = DwgWriter.ToSheet(main, c0.Cx, c0.Cy);
                    var dv = s1.DrawingViews.AddDetailView(main, cp[0], cp[1], 0.012, 0.5, 0.34, 0.18, true);
                    Idle(app);
                    double dx0, dy0, dx1, dy1; dv.Range(out dx0, out dy0, out dx1, out dy1);
                    Console.WriteLine("DETAIL_VIEW_OK caption=" + dv.Caption + " range=(" + Math.Round(dx0 * 1000, 1) + "," + Math.Round(dy0 * 1000, 1) + ")-(" + Math.Round(dx1 * 1000, 1) + "," + Math.Round(dy1 * 1000, 1) + ")");
                }
            } catch (Exception ex) { Console.WriteLine("DETAIL_ERR=" + ex.Message); }

            // 2. 测试原生 HoleTables2（打印模板保存的 SavedSettings 列表 + Add + Origin + AddHole）
            try {
                var hts = dft2.HoleTables2;
                Console.WriteLine("HT2_COUNT_INIT=" + hts.Count);
                // 尝试空设置名或常见名称
                D.HoleTable2 ht = null;
                foreach (var cfg in new[] { "", "ISO", "GB", "Normal", "默认" }) {
                    try {
                        ht = hts.Add(cfg, D.HoleTableType.igByUserSelection);
                        if (ht != null) { Console.WriteLine("HT2_ADD_OK cfg='" + cfg + "'"); break; }
                    } catch (Exception e1) { Console.WriteLine("HT2_TRY cfg='" + cfg + "' err=" + e1.Message); }
                }
                if (ht != null) {
                    int nSaved = 0; Array saved = null;
                    try { ht.GetListOfSavedSettings(out nSaved, ref saved); Console.WriteLine("HT2_SAVED_SETTINGS=" + nSaved); } catch { }
                    var line = edges.FirstOrDefault(e => e.Kind == DwgKind.Line);
                    if (line != null) {
                        try { ht.AddNewXYoriginEx(DwgWriter.Ref(main, line.Tag), D.KeypointIndexConstants.igLineStart); Console.WriteLine("HT2_ORIGIN_OK"); }
                        catch (Exception oe) { Console.WriteLine("HT2_ORIGIN_ERR=" + oe.Message); }
                    }
                    int added = 0;
                    foreach (var c in circles.Take(8)) {
                        try { ht.AddHole(DwgWriter.Ref(main, c.Tag), 1); added++; } catch { }
                    }
                    try { ht.SetOrigin(0.02, 0.12); ht.Update(); } catch (Exception ue) { Console.WriteLine("HT2_UPD_ERR=" + ue.Message); }
                    Console.WriteLine("HT2_HOLES_ADDED=" + added);
                }
            } catch (Exception ex) { Console.WriteLine("HT2_ERR=" + ex.Message); }

            // 3. 测试通用 Tables.Add（自定义可控精简孔表备选方案）
            try {
                var tbl = dft2.Tables.Add();
                tbl.SetOrigin(0.22, 0.14);
                var cols = tbl.Columns;
                while (cols.Count < 4) cols.Add(cols.Count, true);
                var rows = tbl.Rows;
                while (rows.Count < 3) rows.Add(rows.Count, true);
                tbl.Cell[1, 1].value = "编号";
                tbl.Cell[1, 2].value = "X";
                tbl.Cell[1, 3].value = "Y";
                tbl.Cell[1, 4].value = "规格";
                tbl.Cell[2, 1].value = "A1";
                tbl.Cell[2, 2].value = "20.0";
                tbl.Cell[2, 3].value = "30.0";
                tbl.Cell[2, 4].value = "Ø6.6 贯通";
                tbl.Update();
                Console.WriteLine("TABLE_ADD_OK rows=" + tbl.Rows.Count + " cols=" + tbl.Columns.Count);
            } catch (Exception ex) { Console.WriteLine("TABLE_ERR=" + ex.Message); }

            dft2.SaveAs(Path.Combine(outDir, "probe-detail-ht.dft"));
        } catch (Exception ex) { Console.WriteLine("PROBE_B_ERR=" + ex); }
        finally { if (dft2 != null) try { dft2.Close(false); } catch { } }

        Console.WriteLine("WAVE2_PROBE_DONE");
        return 0;
    }
}
