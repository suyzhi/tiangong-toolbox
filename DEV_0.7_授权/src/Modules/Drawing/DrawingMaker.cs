using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using D=SolidEdgeDraft;
using F=SolidEdgeFramework;
using S=SolidEdgeFrameworkSupport;

namespace TianGongCadSuite {
    public static partial class DrawingMaker {
        static void Idle(F.Application app){ for (int k = 0; k < 6; k++) { try { app.DoIdle(); } catch { } } }

        public static DrawingOutcome Part(F.Application app, string modelPath, DrawingOptions opt){
            var o = new DrawingOutcome { Model = modelPath };
            D.DraftDocument dft = null;
            try {
                string a4 = string.IsNullOrEmpty(opt.TemplateA4) ? DrawingTemplates.Find(app, "A4") : opt.TemplateA4;
                string a3 = string.IsNullOrEmpty(opt.TemplateA3) ? DrawingTemplates.Find(app, "A3") : opt.TemplateA3;
                if (a3.Length == 0) throw new InvalidOperationException("找不到工程图模板（天工安装目录 Template\\SK先惠）。");
                // 1. 先用 A4 模板建一张，借它的模型链接读零件（不用在 CAD 里另开窗口）
                dft = (D.DraftDocument)app.Documents.Add("SolidEdge.DraftDocument", a4.Length > 0 ? a4 : a3);
                Idle(app);
                var link = dft.ModelLinks.Add(modelPath);
                object model = link.ModelDocument;
                double x0, y0, z0, x1, y1, z1; link.Range3d(out x0, out y0, out z0, out x1, out y1, out z1);
                var range = new[] { x0, y0, z0, x1, y1, z1 };
                var facts = DrawingModelRead.Facts(model, range);
                var holes = DrawingModelRead.Holes(model);
                DrawingLayout.Mouths(facts, holes);
                int oblique = holes.Count(h => Math.Abs(h.Axis.X) < 0.9998 && Math.Abs(h.Axis.Y) < 0.9998 && Math.Abs(h.Axis.Z) < 0.9998);
                if (oblique > 0) o.Notes.Add("有 " + oblique + " 个斜孔（轴线不沿 X/Y/Z），位置没标，请人工补");
                o.Notes.Add("视图 模型：孔 X/Y/Z=" + string.Join("/", facts.Holes) + " 台阶=" + string.Join("/", facts.Steps) + " 倒角=" + facts.Chamfers.Count + "(最大 C" + facts.ChamferMax + ") 三维孔=" + holes.Count);
                // 2. 选视图、排版：A4 放不下（或比例太小）换 A3
                var specs = DrawingLayout.Choose(facts);
                double iw, ih; DrawingLayout.IsoBox(facts.Size, out iw, out ih);
                var lay = DrawingLayout.Arrange(specs, iw, ih);
                if (lay.Zones.Name == "A3" || a4.Length == 0) {
                    try { dft.Close(false); } catch { }
                    dft = (D.DraftDocument)app.Documents.Add("SolidEdge.DraftDocument", a3);
                    Idle(app);
                    link = dft.ModelLinks.Add(modelPath);
                }
                o.Sheet = lay.Zones.Name; o.Scale = DrawingPlan.ScaleText(lay.Scale);
                // 切换为公司标准尺寸样式（A3-国标-水平 或 A3-国标）：模板保存时常停留在 A4-水平2（1.5mm 细字）或 A3-孔（单边箭头），
                // 必须同时锁定文档活动样式 + 标注集合 Style + 逐对象兜底核验，确保所有尺寸、孔标注、倒角、形位公差、粗糙度均不落回默认样式。
                var stylesAvail = DwgWriter.AvailableStyles(dft);
                string dimStyle = DrawingStyleRules.PrimaryDimensionStyle(stylesAvail, lay.Zones.Name, opt.DimensionStyle);
                string engraveStyle = DrawingStyleRules.EngravingStyle(stylesAvail, lay.Zones.Name);
                string centerStyle = DrawingStyleRules.CenterMarkStyle(stylesAvail, lay.Zones.Name);
                o.Style = dimStyle;
                if (dimStyle.Length == 0) o.Notes.Add("模板未找到 A3-国标 / A3-国标-水平 尺寸样式，已沿用模板原样式");
                D.Sheet sheet = dft.ActiveSheet;
                DwgWriter.ApplySheetStyle(dft, sheet, dimStyle);
                // 标题栏的「比例」读的是图纸比例
                try { if (lay.Scale >= 1) sheet.SheetSetup.SetScale(lay.Scale, 1); else sheet.SheetSetup.SetScale(1, Math.Round(1 / lay.Scale, 3)); } catch (Exception e) { o.Notes.Add("图纸比例没设上：" + e.Message); }
                // 3. 视图
                var views = new Dictionary<DwgViewRole, D.DrawingView>();
                var mainSpec = lay.Get(DwgViewRole.Main);
                var main = sheet.DrawingViews.AddPartView(link, D.ViewOrientationConstants.igTopView, lay.Scale, mainSpec.X, mainSpec.Y, D.PartDrawingViewTypeConstants.sePartDesignedView);
                var dMain = Unit(mainSpec.Axis, mainSpec.Sign); var xMain = Unit(mainSpec.Horizontal, 1);
                main.SetViewOrientation(dMain.X, dMain.Y, dMain.Z, xMain.X, xMain.Y, xMain.Z);
                SetViewScaleText(main, lay.Scale);
                // 改朝向后必须 Update：不然视图里的图元还是原来俯视的样子，折叠出的投影却按新朝向（探针 4，015 主视图"转了 90°"就是这个）
                try { main.Update(); } catch { }
                Idle(app);
                Center(main, mainSpec.X, mainSpec.Y, true, true);
                views[DwgViewRole.Main] = main;
                var below = lay.Get(DwgViewRole.Below);
                if (below != null && below.Section) {
                    var sec = Section(main, holes, app);
                    if (sec != null) { SetViewScaleText(sec, lay.Scale); Center(sec, below.X, below.Y, true, true); views[DwgViewRole.Below] = sec; o.Notes.Add("视图 加了剖视 A-A（台阶孔）"); }
                    else below.Section = false;
                }
                if (below != null && !below.Section) {
                    try { var v = sheet.DrawingViews.AddByFold(main, D.FoldTypeConstants.igFoldDown, below.X, below.Y); Idle(app); SetViewScaleText(v, lay.Scale); Center(v, below.X, below.Y, false, true); views[DwgViewRole.Below] = v; }
                    catch (Exception e) { o.Notes.Add("俯视图建不出来：" + e.Message); }
                }
                var right = lay.Get(DwgViewRole.Right);
                if (right != null) {
                    try { var v = sheet.DrawingViews.AddByFold(main, D.FoldTypeConstants.igFoldRight, right.X, right.Y); Idle(app); SetViewScaleText(v, lay.Scale); Center(v, right.X, right.Y, true, false); views[DwgViewRole.Right] = v; }
                    catch (Exception e) { o.Notes.Add("左视图建不出来：" + e.Message); }
                }
                // 左视图 / 主视图看不到的那面有盲孔口：补右视图 / 后视图（DrawingLayout.Facing；补了比例会缩小就不补，提示人工）；底面的只提示
                foreach (var dr in lay.Dropped) o.Notes.Add("视图 " + (dr == DwgViewRole.Left ? "右端面" : "背面") + "有盲孔，补视图图纸放不下（比例要缩小），没补，这些孔请人工补标");
                if (facts.BottomBlind > 0 && (below == null || !below.Section)) o.Notes.Add("视图 底面有 " + facts.BottomBlind + " 个盲孔，俯视图里看不到，请人工补仰视图或局部视图标注");
                var leftV = lay.Get(DwgViewRole.Left);
                if (leftV != null) {
                    try { var v = sheet.DrawingViews.AddByFold(main, D.FoldTypeConstants.igFoldLeft, leftV.X, leftV.Y); Idle(app); SetViewScaleText(v, lay.Scale); Center(v, leftV.X, leftV.Y, true, false); views[DwgViewRole.Left] = v; o.Notes.Add("视图 右端面有盲孔：加了右视图"); }
                    catch (Exception e) { o.Notes.Add("右视图建不出来：" + e.Message); }
                }
                var back = lay.Get(DwgViewRole.Back);
                if (back != null) {
                    try {
                        // 后视图：视线反过来、屏幕 x 也反过来（上方不变）
                        var v = sheet.DrawingViews.AddPartView(link, D.ViewOrientationConstants.igTopView, lay.Scale, back.X, back.Y, D.PartDrawingViewTypeConstants.sePartDesignedView);
                        v.SetViewOrientation(-dMain.X, -dMain.Y, -dMain.Z, -xMain.X, -xMain.Y, -xMain.Z);
                        SetViewScaleText(v, lay.Scale);
                        try { v.Update(); } catch { }
                        Idle(app); Center(v, back.X, back.Y, true, true); views[DwgViewRole.Back] = v; o.Notes.Add("视图 背面有盲孔：加了后视图");
                    } catch (Exception e) { o.Notes.Add("后视图建不出来：" + e.Message); }
                }
                var isoSpec = lay.Get(DwgViewRole.Iso);
                D.DrawingView iso = null;
                if (isoSpec != null) {
                    try {
                        // 轴测图一律 Z 朝上（和人画的一样，板子平躺），四个上方轴测里挑最能看到主视图那一面的
                        iso = sheet.DrawingViews.AddPartView(link, IsoOrientation(dMain), lay.IsoScale, isoSpec.X, isoSpec.Y, D.PartDrawingViewTypeConstants.sePartDesignedView);
                        SetViewScaleText(iso, lay.IsoScale);
                        try { iso.Defaults_ShowTangentEdges = false; } catch { }
                        Idle(app); Center(iso, isoSpec.X, isoSpec.Y, true, true);
                        views[DwgViewRole.Iso] = iso;
                    } catch (Exception e) { o.Notes.Add("轴测图建不出来：" + e.Message); }
                }
                o.Views = views.Count;
                // 4. 尺寸
                var w = new DwgWriter(sheet, dft, dimStyle);
                w.Space = new DwgSpace(lay.Zones);
                foreach (var kv in views) { var b = DwgWriter.SheetBox(kv.Value); w.Space.Add(b[0] - 0.001, b[1] - 0.001, b[2] + 0.001, b[3] + 0.001); }
                // 刻字引线的文字最后才挂，位置照模板那条：先把那块占上，孔标注别压在它上面（视图多了整组右移时 02-007 压上过）
                var eb = EngraveBox(sheet, Path.GetFileNameWithoutExtension(modelPath));
                if (eb != null) w.Space.Add(eb);
                Annotate(w, views, lay, holes, range, facts, o);
                foreach (var kv in views) o.Notes.Add("视图 " + kv.Key + " " + Describe(kv.Value));
                // 检索圆孔十字中心标记与圆柱中心线（以前只传一个 true，其余参数默认全 false 导致一张图一个中心标记都没出）
                DwgWriter.SetActiveStyle(dft, centerStyle);
                try {
                    RetrieveCenters(main, centerStyle);
                    foreach (var kv in views) if (kv.Key != DwgViewRole.Main && kv.Key != DwgViewRole.Iso) RetrieveCenters(kv.Value, centerStyle);
                } finally {
                    DwgWriter.ApplySheetStyle(dft, sheet, dimStyle);
                }
                // 5. 刻字：模板里那条引线挂到轴测图上（用模板同款 A3-国标，挂完恢复主尺寸样式为文档活动样式）
                if (iso != null) Engrave(dft, sheet, iso, engraveStyle, dimStyle, o);
                else DwgWriter.SetActiveStyle(dft, dimStyle);
                DwgWriter.ClearCollectionStyles(sheet);
                o.Dimensions = w.Created; o.Failed = w.Failed; o.Notes.AddRange(w.Notes);
                // 6. 存
                if (opt.Save) o.Output = SaveAs(app, dft, modelPath, opt, o);
                o.Ok = true;
            } catch (Exception e) {
                o.Notes.Add("出图失败：" + e.Message);
                Log.Write("DrawingMaker", e);
            }
            if (dft != null && (opt.Close || !o.Ok)) { try { dft.Close(false); } catch { } }
            return o;
        }

        // ---- 装配图 ----
        // 公司装配图（普查 SK26.01-OK010-05-07A 等）：两个正投影标外形尺寸 + 一个轴测图挂零件序号 + 明细表（公司配置 SK-BOM）。
        // A3：主视图（前视）左上、俯视图在它下面，轴测图右边，明细表压在标题栏上方往上长。
        public static DrawingOutcome Assembly(F.Application app, string asmPath, DrawingOptions opt){
            var o = new DrawingOutcome { Model = asmPath };
            D.DraftDocument dft = null;
            try {
                string a3 = string.IsNullOrEmpty(opt.TemplateA3) ? DrawingTemplates.Find(app, "A3") : opt.TemplateA3;
                if (a3.Length == 0) throw new InvalidOperationException("找不到工程图模板（天工安装目录 Template\\SK先惠）。");
                dft = (D.DraftDocument)app.Documents.Add("SolidEdge.DraftDocument", a3);
                Idle(app);
                // 装配图同样切到 A3-国标-水平 / A3-国标，避免外形线性尺寸继承 A3-孔 变成单边箭头
                string dimStyle = DrawingStyleRules.PrimaryDimensionStyle(DwgWriter.AvailableStyles(dft), "A3", opt.DimensionStyle);
                o.Style = dimStyle;
                if (dimStyle.Length == 0) o.Notes.Add("模板未找到 A3-国标 / A3-国标-水平 尺寸样式，已沿用模板原样式");
                D.Sheet sheet = dft.ActiveSheet;
                DwgWriter.ApplySheetStyle(dft, sheet, dimStyle);
                var link = dft.ModelLinks.Add(asmPath);
                // 装配的 ModelLink.Range3d 天工给的是一个假的 200×200×200（e2e asm-3 实测），外形尺寸要从视图量：
                // 先按 1:10 放主视图 + 俯视图，读它们在图纸上多大，换算出真实的长宽高，再定比例、挪位置
                var z = SheetZones.A3();
                const double s0 = 0.1;
                var main = sheet.DrawingViews.AddAssemblyView(link, D.ViewOrientationConstants.igFrontView, s0, 0.10, 0.20, D.AssemblyDrawingViewTypeConstants.seAssemblyDesignedView);
                Idle(app);
                D.DrawingView below = null;
                try { below = sheet.DrawingViews.AddByFold(main, D.FoldTypeConstants.igFoldDown, 0.10, 0.08); Idle(app); } catch (Exception e) { o.Notes.Add("俯视图建不出来：" + e.Message); }
                // 视图的 Range 四周带一圈固定留白（1:10 时把 188 宽量成 326），所以量视图里图元的外框（视图坐标 = 模型尺寸）
                var mb0 = DrawingPlan.Box(DrawingViewRead.Edges(main));
                double sx = mb0[2] - mb0[0], sz = mb0[3] - mb0[1], sy = sx * 0.5;
                if (below != null) { var bb0 = DrawingPlan.Box(DrawingViewRead.Edges(below)); sy = bb0[3] - bb0[1]; }
                if (sx < 1e-4 || sz < 1e-4) { var r0 = DwgWriter.SheetBox(main); sx = (r0[2] - r0[0]) / s0; sz = (r0[3] - r0[1]) / s0; }
                // 主视图 X×Z，俯视图 X×Y 在下面；左边 2/3 宽放两个正投影
                double availW = 0.250 - 2 * DrawingLayout.DimBand, availH = z.MainH - 3 * DrawingLayout.DimBand - DrawingLayout.Gap;
                double scale = DrawingPlan.PickScale(sx, sz + sy, availW, availH);
                o.Sheet = "A3"; o.Scale = DrawingPlan.ScaleText(scale);
                try { if (scale >= 1) sheet.SheetSetup.SetScale(scale, 1); else sheet.SheetSetup.SetScale(1, Math.Round(1 / scale, 3)); } catch { }
                try { main.ScaleFactor = scale; SetViewScaleText(main, scale); Idle(app); } catch (Exception e) { Log.Write("DrawingAsmScale", e); }
                if (below != null) { try { if (Math.Abs(below.ScaleFactor - scale) > 1e-9) below.ScaleFactor = scale; SetViewScaleText(below, scale); Idle(app); } catch { } }
                double left = z.Main[0] + DrawingLayout.DimBand, top = z.Main[3] - DrawingLayout.DimBand;
                double mx = left + sx * scale / 2, my = top - sz * scale / 2;
                Center(main, mx, my, true, true);
                if (below != null) { double by = my - sz * scale / 2 - DrawingLayout.DimBand - DrawingLayout.Gap - sy * scale / 2; Center(below, mx, by, true, true); }
                // 轴测图 + 明细表
                double isoX0 = 0.262, isoX1 = 0.412, isoY0 = 0.110, isoY1 = 0.242;
                double iw, ih; DrawingLayout.IsoBox(new[] { sx, sy, sz }, out iw, out ih);
                double isoScale = Math.Min(scale, DrawingPlan.PickScale(iw, ih, isoX1 - isoX0 - 0.02, isoY1 - isoY0 - 0.02));
                var iso = sheet.DrawingViews.AddAssemblyView(link, D.ViewOrientationConstants.igTopFrontRightView, isoScale, (isoX0 + isoX1) / 2, (isoY0 + isoY1) / 2, D.AssemblyDrawingViewTypeConstants.seAssemblyDesignedView);
                Idle(app); Center(iso, (isoX0 + isoX1) / 2, (isoY0 + isoY1) / 2, true, true);
                o.Views = below == null ? 2 : 3;
                var w = new DwgWriter(sheet, dft, dimStyle);
                // 外形尺寸：主视图 长 × 高，俯视图 宽
                Overall(w, main, true, o); Overall(w, main, false, o);
                if (below != null) Overall(w, below, false, o);
                // 明细表 + 序号：明细表压在标题栏上方往上长，长完量它多高，轴测图挪到它上面 / 左边剩下的地方
                try {
                    var pl = dft.PartsLists.Add(iso, "SK-BOM", 1, 1);
                    Idle(app);
                    try { pl.SetOrigin(z.Width - 0.005, 0.0445); } catch (Exception e) { Log.Write("DrawingBomOrigin", e); }
                    int rows = 0; try { rows = pl.Rows.Count; } catch { }
                    o.Notes.Add("明细表 " + rows + " 行");
                    o.Required++; o.Covered++;
                    double bx0 = 0.240, by1 = 0.0445 + (rows + 2) * 0.0075;
                    try { double a, b, c, d; ((dynamic)pl).Range(out a, out b, out c, out d); bx0 = Math.Min(a, c); by1 = Math.Max(b, d); } catch { }
                    // 轴测图的地方：明细表上方（到右上公差表下面）或者明细表左边（正投影右边到明细表左边），哪块大用哪块
                    double orthoRight = 0.262;
                    try { var mb = DwgWriter.SheetBox(main); orthoRight = mb[2] + DrawingLayout.DimBand; if (below != null) { var bb = DwgWriter.SheetBox(below); orthoRight = Math.Max(orthoRight, bb[2] + DrawingLayout.DimBand); } } catch { }
                    double[] up = { Math.Max(orthoRight, 0.200), by1 + 0.006, 0.412, 0.242 }, leftZ = { orthoRight, 0.050, bx0 - 0.006, 0.272 };
                    double au = Math.Max(0, up[2] - up[0]) * Math.Max(0, up[3] - up[1]), al = Math.Max(0, leftZ[2] - leftZ[0]) * Math.Max(0, leftZ[3] - leftZ[1]);
                    var zone = au >= al ? up : leftZ;
                    // 序号气泡围着轴测图一圈，四周各留 25mm
                    // 轴测图比例不写在图上：放满（两位有效数字），不超过主视图比例
                    double s2 = Math.Min((zone[2] - zone[0] - 0.05) / Math.Max(iw, 1e-6), (zone[3] - zone[1] - 0.05) / Math.Max(ih, 1e-6));
                    if (s2 > 0) { double mag = Math.Pow(10, Math.Floor(Math.Log10(s2)) - 1); s2 = Math.Floor(s2 / mag) * mag; }
                    s2 = Math.Min(scale, Math.Max(s2, scale * 0.1));
                    if (Math.Abs(s2 - isoScale) > 1e-9) { try { iso.ScaleFactor = s2; Idle(app); } catch { } }
                    Center(iso, (zone[0] + zone[2]) / 2, (zone[1] + zone[3]) / 2, true, true);
                } catch (Exception e) { o.Notes.Add("明细表没生成：" + e.Message); o.Required++; }
                o.Notes.Add("视图 范围 " + Math.Round(sx * 1000) + "×" + Math.Round(sy * 1000) + "×" + Math.Round(sz * 1000) + "；主视图 " + Describe(main) + (below != null ? "；俯视图 " + Describe(below) : "") + "；轴测 " + Describe(iso));
                // 装配不刻字：模板里那条刻字引线删掉
                try { var bs = (S.Balloons)sheet.Balloons; for (int i = bs.Count; i >= 1; i--) { string t = ""; try { t = bs.Item(i).BalloonText ?? ""; } catch { } if (t.Contains("刻字")) bs.Item(i).Delete(); } } catch { }
                DwgWriter.SetActiveStyle(dft, dimStyle);
                DwgWriter.ClearCollectionStyles(sheet);
                o.Dimensions = w.Created; o.Failed = w.Failed; o.Notes.AddRange(w.Notes);
                if (opt.Save) o.Output = SaveAs(app, dft, asmPath, opt, o);
                o.Ok = true;
            } catch (Exception e) { o.Notes.Add("出图失败：" + e.Message); Log.Write("DrawingAssembly", e); }
            if (dft != null && (opt.Close || !o.Ok)) { try { dft.Close(false); } catch { } }
            return o;
        }

        // 外形尺寸：视图里最左、最右两条竖线（horizontal=true）或最下、最上两条横线之间的距离，摆在视图上方 / 左边
        static void Overall(DwgWriter w, D.DrawingView v, bool horizontal, DrawingOutcome o){
            o.Required++;
            try {
                var e = DrawingViewRead.Edges(v).Where(x => horizontal ? x.IsVertical : x.IsHorizontal).ToList();
                if (e.Count < 2) return;
                var a = horizontal ? e.OrderBy(x => x.X1).ThenByDescending(x => x.Length).First() : e.OrderBy(x => x.Y1).ThenByDescending(x => x.Length).First();
                var b = horizontal ? e.OrderByDescending(x => x.X1).ThenByDescending(x => x.Length).First() : e.OrderByDescending(x => x.Y1).ThenByDescending(x => x.Length).First();
                if (w.Distance(v, a, b, horizontal, 0.010)) o.Covered++;
            } catch (Exception ex) { Log.Write("DrawingOverall", ex); }
        }

        static string SaveAs(F.Application app, D.DraftDocument dft, string modelPath, DrawingOptions opt, DrawingOutcome o){
            string dir = string.IsNullOrEmpty(opt.OutputDir) ? Path.GetDirectoryName(modelPath) : opt.OutputDir;
            string name = Path.GetFileNameWithoutExtension(modelPath);
            string target = Path.Combine(dir, name + ".dft");
            if (File.Exists(target) && !opt.Overwrite) target = Path.Combine(dir, name + "(自动).dft");
            if (File.Exists(target) && !opt.Overwrite) { int k = 2; while (File.Exists(Path.Combine(dir, name + "(自动" + k + ").dft"))) k++; target = Path.Combine(dir, name + "(自动" + k + ").dft"); }
            dft.SaveAs(target);
            if (!string.IsNullOrEmpty(opt.PdfDir)) {
                try {
                    object old = null; app.GetGlobalParameter(F.ApplicationGlobalConstants.seApplicationGlobalDraftSaveAsPDFSheetOptions, ref old);
                    app.SetGlobalParameter(F.ApplicationGlobalConstants.seApplicationGlobalDraftSaveAsPDFSheetOptions, 1);
                    dft.SaveCopyAs(Path.Combine(opt.PdfDir, name + ".pdf"));
                    app.SetGlobalParameter(F.ApplicationGlobalConstants.seApplicationGlobalDraftSaveAsPDFSheetOptions, old);
                } catch (Exception e) { o.Notes.Add("PDF 没导出：" + e.Message); }
            }
            return target;
        }

        static D.ViewOrientationConstants IsoOrientation(V3 main){
            var c = new[] { new { O = D.ViewOrientationConstants.igTopFrontRightView, V = new V3(1, -1, 1) }, new { O = D.ViewOrientationConstants.igTopFrontLeftView, V = new V3(-1, -1, 1) },
                            new { O = D.ViewOrientationConstants.igTopBackRightView, V = new V3(1, 1, 1) }, new { O = D.ViewOrientationConstants.igTopBackLeftView, V = new V3(-1, 1, 1) } };
            var best = c[0]; double bd = double.MinValue;
            foreach (var k in c) { double d = k.V.Dot(main); if (d > bd + 1e-9) { bd = d; best = k; } }
            return best.O;
        }

        // 全剖：主视图上过最大的那个台阶孔圆心画一条横贯的剖切线 → CreateView → Update（不 Update 剖视里一条线都没有，探针 6）
        static D.DrawingView Section(D.DrawingView main, List<ModelHole> holes, F.Application app){
            try {
                var edges = DrawingViewRead.Edges(main);
                // 台阶孔在主视图里是一组同心圆：取圆最多 / 最大的那组的圆心
                var circles = edges.Where(e => e.Kind == DwgKind.Circle).ToList();
                DwgEdge best = null; int bestN = 0;
                foreach (var c in circles) { int n = circles.Count(d => Math.Abs(d.Cx - c.Cx) < 1e-5 && Math.Abs(d.Cy - c.Cy) < 1e-5); if (n > bestN || (n == bestN && best != null && c.R > best.R)) { bestN = n; best = c; } }
                if (best == null || bestN < 2) return null;
                var box = DrawingPlan.Box(edges);
                var a = DwgWriter.ToSheet(main, box[0], best.Cy); var b = DwgWriter.ToSheet(main, box[2], best.Cy);
                var cut = main.CuttingPlanes.Add();
                cut.Profile.Lines2d.AddBy2Points(a[0] - 0.006, a[1], b[0] + 0.006, b[1]);
                var sec = cut.CreateView();
                Idle(app); try { sec.Update(); } catch { } Idle(app);
                return sec;
            } catch (Exception e) { Log.Write("DrawingSection", e); return null; }
        }

        static V3 Unit(int axis, int sign){ return new V3(axis == 0 ? sign : 0, axis == 1 ? sign : 0, axis == 2 ? sign : 0); }

        static void SetViewScaleText(D.DrawingView v, double scale){
            if (v == null || scale <= 0) return;
            try { v.ScaleDefinitionText = DrawingPlan.ScaleText(scale); } catch { }
        }

        static void RetrieveCenters(D.DrawingView v, string centerStyle){
            if (v == null) return;
            try {
                object st = string.IsNullOrEmpty(centerStyle) ? (object)Type.Missing : centerStyle;
                // 显式开启圆孔中心标记（不含小圆角弧）与完整圆柱孔中心线，使用模板红色「中心线」样式
                ((dynamic)v).RetrieveCenterLinesCenterMarks(
                    true, st,
                    true, true, true, false,
                    true, false,
                    true, false, false, false,
                    false, false, 0.0, 0.0, 0.0, 0.0);
            } catch (Exception e) { Log.Write("DrawingCenterMarks", e); }
        }

        // 把视图挪到中心 = (x,y)。折叠出来的视图只沿折叠方向挪（另一方向要和主视图对齐）
        static void Center(D.DrawingView v, double x, double y, bool moveX, bool moveY){
            try {
                double a, b, c, d; v.Range(out a, out b, out c, out d);
                double ox, oy; v.GetOrigin(out ox, out oy);
                double dx = moveX ? x - (a + c) / 2 : 0, dy = moveY ? y - (b + d) / 2 : 0;
                if (Math.Abs(dx) > 1e-6 || Math.Abs(dy) > 1e-6) v.SetOrigin(ox + dx, oy + dy);
            } catch (Exception e) { Log.Write("DrawingCenter", e); }
        }
    }
}
