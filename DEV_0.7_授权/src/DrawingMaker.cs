using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using D=SolidEdgeDraft;
using F=SolidEdgeFramework;
using S=SolidEdgeFrameworkSupport;

namespace TianGongCadSuite {
    public sealed class DrawingOptions {
        public string TemplateA3, TemplateA4;   // 空 = 自动找（DrawingTemplates）
        public string OutputDir;                // 空 = 放在模型旁边
        public bool Overwrite;                  // 已有同名工程图时：false = 另存为「名字(自动).dft」
        public bool Save = true;
        public string PdfDir;                   // 非空：顺便导出一份 PDF（测试 / 交付用）
        public bool Close;                      // 批量出图：存完就关（单张出图留着给用户看）
    }

    public sealed class DrawingOutcome {
        public string Model, Output, Sheet = "", Scale = "";
        public bool Ok;
        public int Views, Dimensions, Failed, Holes, HoleCallouts, Radii;
        public int Required, Covered;            // 完成度：要标的落点 / 标上的（见 DrawingCoverage）
        public readonly List<string> Notes = new List<string>();
        public double Completeness { get { return Required == 0 ? 1 : (double)Covered / Required; } }
    }

    // 找公司模板：天工安装目录 Template\SK先惠\A3-SK图纸.dft / A4-SK图纸.dft；没有就用天工自带 GB 模板
    public static class DrawingTemplates {
        public static string Root(){
            foreach (var p in new[] { @"C:\Program Files\NDS\TianGong 2025\Template", @"D:\Program Files\NDS\TianGong 2025\Template" }) if (Directory.Exists(p)) return p;
            return "";
        }
        public static string Find(string size){
            string root = Root();
            if (root.Length == 0) return "";
            string sk = Path.Combine(root, "SK先惠", size + "-SK图纸.dft");
            if (File.Exists(sk)) return sk;
            string gb = Path.Combine(root, "GB Metric", "GB 公制工程图.dft");
            return File.Exists(gb) ? gb : "";
        }
    }

    // 一键出图：零件 → 一张可加工的工程图。步骤见 Part() 里的注释。
    public static class DrawingMaker {
        static void Idle(F.Application app){ for (int k = 0; k < 6; k++) { try { app.DoIdle(); } catch { } } }

        public static DrawingOutcome Part(F.Application app, string modelPath, DrawingOptions opt){
            var o = new DrawingOutcome { Model = modelPath };
            D.DraftDocument dft = null;
            try {
                string a4 = string.IsNullOrEmpty(opt.TemplateA4) ? DrawingTemplates.Find("A4") : opt.TemplateA4;
                string a3 = string.IsNullOrEmpty(opt.TemplateA3) ? DrawingTemplates.Find("A3") : opt.TemplateA3;
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
                D.Sheet sheet = dft.ActiveSheet;
                // 标题栏的「比例」读的是图纸比例
                try { if (lay.Scale >= 1) sheet.SheetSetup.SetScale(lay.Scale, 1); else sheet.SheetSetup.SetScale(1, Math.Round(1 / lay.Scale, 3)); } catch (Exception e) { o.Notes.Add("图纸比例没设上：" + e.Message); }
                // 3. 视图
                var views = new Dictionary<DwgViewRole, D.DrawingView>();
                var mainSpec = lay.Get(DwgViewRole.Main);
                var main = sheet.DrawingViews.AddPartView(link, D.ViewOrientationConstants.igTopView, lay.Scale, mainSpec.X, mainSpec.Y, D.PartDrawingViewTypeConstants.sePartDesignedView);
                var dMain = Unit(mainSpec.Axis, mainSpec.Sign); var xMain = Unit(mainSpec.Horizontal, 1);
                main.SetViewOrientation(dMain.X, dMain.Y, dMain.Z, xMain.X, xMain.Y, xMain.Z);
                // 改朝向后必须 Update：不然视图里的图元还是原来俯视的样子，折叠出的投影却按新朝向（探针 4，015 主视图"转了 90°"就是这个）
                try { main.Update(); } catch { }
                Idle(app);
                Center(main, mainSpec.X, mainSpec.Y, true, true);
                views[DwgViewRole.Main] = main;
                var below = lay.Get(DwgViewRole.Below);
                if (below != null && below.Section) {
                    var sec = Section(main, holes, app);
                    if (sec != null) { Center(sec, below.X, below.Y, true, true); views[DwgViewRole.Below] = sec; o.Notes.Add("视图 加了剖视 A-A（台阶孔）"); }
                    else below.Section = false;
                }
                if (below != null && !below.Section) {
                    try { var v = sheet.DrawingViews.AddByFold(main, D.FoldTypeConstants.igFoldDown, below.X, below.Y); Idle(app); Center(v, below.X, below.Y, false, true); views[DwgViewRole.Below] = v; }
                    catch (Exception e) { o.Notes.Add("俯视图建不出来：" + e.Message); }
                }
                var right = lay.Get(DwgViewRole.Right);
                if (right != null) {
                    try { var v = sheet.DrawingViews.AddByFold(main, D.FoldTypeConstants.igFoldRight, right.X, right.Y); Idle(app); Center(v, right.X, right.Y, true, false); views[DwgViewRole.Right] = v; }
                    catch (Exception e) { o.Notes.Add("左视图建不出来：" + e.Message); }
                }
                // 左视图 / 主视图看不到的那面有盲孔口：补右视图 / 后视图（DrawingLayout.Facing；补了比例会缩小就不补，提示人工）；底面的只提示
                foreach (var dr in lay.Dropped) o.Notes.Add("视图 " + (dr == DwgViewRole.Left ? "右端面" : "背面") + "有盲孔，补视图图纸放不下（比例要缩小），没补，这些孔请人工补标");
                if (facts.BottomBlind > 0 && (below == null || !below.Section)) o.Notes.Add("视图 底面有 " + facts.BottomBlind + " 个盲孔，俯视图里看不到，请人工补仰视图或局部视图标注");
                var leftV = lay.Get(DwgViewRole.Left);
                if (leftV != null) {
                    try { var v = sheet.DrawingViews.AddByFold(main, D.FoldTypeConstants.igFoldLeft, leftV.X, leftV.Y); Idle(app); Center(v, leftV.X, leftV.Y, true, false); views[DwgViewRole.Left] = v; o.Notes.Add("视图 右端面有盲孔：加了右视图"); }
                    catch (Exception e) { o.Notes.Add("右视图建不出来：" + e.Message); }
                }
                var back = lay.Get(DwgViewRole.Back);
                if (back != null) {
                    try {
                        // 后视图：视线反过来、屏幕 x 也反过来（上方不变）
                        var v = sheet.DrawingViews.AddPartView(link, D.ViewOrientationConstants.igTopView, lay.Scale, back.X, back.Y, D.PartDrawingViewTypeConstants.sePartDesignedView);
                        v.SetViewOrientation(-dMain.X, -dMain.Y, -dMain.Z, -xMain.X, -xMain.Y, -xMain.Z);
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
                        Idle(app); Center(iso, isoSpec.X, isoSpec.Y, true, true);
                        views[DwgViewRole.Iso] = iso;
                    } catch (Exception e) { o.Notes.Add("轴测图建不出来：" + e.Message); }
                }
                o.Views = views.Count;
                // 4. 尺寸
                var w = new DwgWriter(sheet);
                w.Space = new DwgSpace(lay.Zones);
                foreach (var kv in views) { var b = DwgWriter.SheetBox(kv.Value); w.Space.Add(b[0] - 0.001, b[1] - 0.001, b[2] + 0.001, b[3] + 0.001); }
                // 刻字引线的文字最后才挂，位置照模板那条：先把那块占上，孔标注别压在它上面（视图多了整组右移时 02-007 压上过）
                var eb = EngraveBox(sheet, Path.GetFileNameWithoutExtension(modelPath));
                if (eb != null) w.Space.Add(eb);
                Annotate(w, views, lay, holes, range, facts, o);
                foreach (var kv in views) o.Notes.Add("视图 " + kv.Key + " " + Describe(kv.Value));
                try { ((dynamic)main).RetrieveCenterLinesCenterMarks(true); } catch (Exception e) { Log.Write("DrawingCenterMarks", e); }
                foreach (var kv in views) if (kv.Key != DwgViewRole.Main && kv.Key != DwgViewRole.Iso) try { ((dynamic)kv.Value).RetrieveCenterLinesCenterMarks(true); } catch { }
                // 5. 刻字：模板里那条引线挂到轴测图上
                if (iso != null) Engrave(sheet, iso, o);
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
                string a3 = string.IsNullOrEmpty(opt.TemplateA3) ? DrawingTemplates.Find("A3") : opt.TemplateA3;
                if (a3.Length == 0) throw new InvalidOperationException("找不到工程图模板（天工安装目录 Template\\SK先惠）。");
                dft = (D.DraftDocument)app.Documents.Add("SolidEdge.DraftDocument", a3);
                Idle(app);
                D.Sheet sheet = dft.ActiveSheet;
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
                try { main.ScaleFactor = scale; Idle(app); } catch (Exception e) { Log.Write("DrawingAsmScale", e); }
                if (below != null) { try { if (Math.Abs(below.ScaleFactor - scale) > 1e-9) below.ScaleFactor = scale; Idle(app); } catch { } }
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
                var w = new DwgWriter(sheet);
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

        // 把视图挪到中心 = (x,y)。折叠出来的视图只沿折叠方向挪（另一方向要和主视图对齐）
        static void Center(D.DrawingView v, double x, double y, bool moveX, bool moveY){
            try {
                double a, b, c, d; v.Range(out a, out b, out c, out d);
                double ox, oy; v.GetOrigin(out ox, out oy);
                double dx = moveX ? x - (a + c) / 2 : 0, dy = moveY ? y - (b + d) / 2 : 0;
                if (Math.Abs(dx) > 1e-6 || Math.Abs(dy) > 1e-6) v.SetOrigin(ox + dx, oy + dy);
            } catch (Exception e) { Log.Write("DrawingCenter", e); }
        }

        // ---- 尺寸 ----
        // 主视图：上方一串水平坐标、左边一串竖直坐标；孔 / 圆角标注放右边。
        // 俯视图（主视图下面）：左边一串竖直坐标（= 深度方向）；主视图里没有的水平坐标在它下方再标一串。
        // 左视图（主视图右边）：深度方向没在俯视图标过就在它下方标一串水平坐标；主视图里没有的竖直坐标在它右边标。
        static void Annotate(DwgWriter w, Dictionary<DwgViewRole, D.DrawingView> views, DwgLayout lay, List<ModelHole> holes, double[] range, ModelFacts facts, DrawingOutcome o){
            // 每个视图的水平 / 竖直方向对应哪个模型轴 → 用那个轴的倒角容差
            Func<D.DrawingView, bool, double> ctOf = (vv, hz) => {
                try {
                    double dx, dy, dz, ux, uy, uz; D.ViewOrientationConstants kk; vv.ViewOrientation(out dx, out dy, out dz, out ux, out uy, out uz, out kk);
                    var xv = new V3(ux, uy, uz); var yv = new V3(dx, dy, dz).Cross(xv); var a = hz ? xv : yv;
                    double[] c = { Math.Abs(a.X), Math.Abs(a.Y), Math.Abs(a.Z) };
                    int i = c[0] >= c[1] && c[0] >= c[2] ? 0 : c[1] >= c[2] ? 1 : 2;
                    return facts.EdgeTol[i];
                } catch { return 0; }
            };
            const double off = 0.008;   // 坐标串文字线离视图外框（纸面）
            var edges = new Dictionary<DwgViewRole, List<DwgEdge>>();
            foreach (var kv in views) if (kv.Key != DwgViewRole.Iso) {
                bool isSection = false; try { isSection = kv.Value.DrawingViewType == D.DrawingViewTypeConstants.igXSectionView; } catch { }
                // 剖视里台阶孔的孔壁就是要标的台阶，不当"孔带"去掉；顺带剖开的普通孔（法兰上的螺栓孔，OK310-02-002）照样去掉，
                // 不然一圈螺栓孔的孔壁会把坐标尺寸挤成一团 —— 它们的位置在孔是圆的那个视图里标
                edges[kv.Key] = HoleInternal(kv.Value, DrawingViewRead.Edges(kv.Value), isSection ? holes.Where(h => !ModelFacts.IsStepped(h)).ToList() : holes, range);
            }
            var main = views[DwgViewRole.Main];
            var em = edges[DwgViewRole.Main];
            var boxM = DrawingPlan.Box(em);
            // 主视图两串
            var runH = DrawingPlan.Ordinate(em, true, lay.Scale, boxM, false, off, ctOf(main, true));
            var runV = DrawingPlan.Ordinate(em, false, lay.Scale, boxM, true, off, ctOf(main, false));
            Count(o, runH); Count(o, runV);
            o.Covered += Covered(w.Ordinate(main, runH, lay.Scale)) + Covered(w.Ordinate(main, runV, lay.Scale));
            var sheetXs = Sheet(main, runH, true); var sheetYs = Sheet(main, runV, false);
            bool depthDone = false;
            D.DrawingView vb;
            if (views.TryGetValue(DwgViewRole.Below, out vb)) {
                var eb = edges[DwgViewRole.Below]; var boxB = DrawingPlan.Box(eb);
                var rv = DrawingPlan.Ordinate(eb, false, lay.Scale, boxB, true, off, ctOf(vb, false));
                Count(o, rv); o.Covered += Covered(w.Ordinate(vb, rv, lay.Scale)); depthDone = rv != null;
                var rh = Only(DrawingPlan.Ordinate(eb, true, lay.Scale, boxB, true, off, ctOf(vb, true)), vb, sheetXs, true, main, boxM, boxB);
                Count(o, rh); o.Covered += Covered(w.Ordinate(vb, rh, lay.Scale));
            }
            D.DrawingView vr;
            if (views.TryGetValue(DwgViewRole.Right, out vr)) {
                var er = edges[DwgViewRole.Right]; var boxR = DrawingPlan.Box(er);
                if (!depthDone) { var rh = DrawingPlan.Ordinate(er, true, lay.Scale, boxR, true, off, ctOf(vr, true)); Count(o, rh); o.Covered += Covered(w.Ordinate(vr, rh, lay.Scale)); }
                var rv = Only(DrawingPlan.Ordinate(er, false, lay.Scale, boxR, false, off, ctOf(vr, false)), vr, sheetYs, false, main, boxM, boxR);
                Count(o, rv); o.Covered += Covered(w.Ordinate(vr, rv, lay.Scale));
            }
            // 补的反方向视图（右视图 / 后视图）：只标主视图里没标过的坐标（背面盲孔的位置）
            foreach (var role in new[] { DwgViewRole.Left, DwgViewRole.Back }) {
                D.DrawingView ve; if (!views.TryGetValue(role, out ve)) continue;
                var ee = edges[role]; var boxE = DrawingPlan.Box(ee);
                if (role != DwgViewRole.Left) { var rh = Only(DrawingPlan.Ordinate(ee, true, lay.Scale, boxE, true, off, ctOf(ve, true)), ve, sheetXs, true, main, boxM, boxE); Count(o, rh); o.Covered += Covered(w.Ordinate(ve, rh, lay.Scale)); }
                { var rv = Only(DrawingPlan.Ordinate(ee, false, lay.Scale, boxE, role == DwgViewRole.Left, off, ctOf(ve, false)), ve, sheetYs, false, main, boxM, boxE); Count(o, rv); o.Covered += Covered(w.Ordinate(ve, rv, lay.Scale)); }
            }
            // 孔、圆角
            int pinGroups = 0, pinCount = 0; var pins = new List<ModelHole>();
            foreach (var kv in views) {
                if (kv.Key == DwgViewRole.Iso) continue;
                var v = kv.Value; var e = edges[kv.Key];
                var box = DrawingPlan.Box(e);
                var sbox = DwgWriter.SheetBox(v);
                var map = HoleMap(v, e, holes, range);
                var threads = DrawingViewRead.Threads(v);
                // 圆形零件的外圆（法兰 Ø64）也要标直径：上限放到视图外框
                double maxD = Math.Min(box[2] - box[0], box[3] - box[1]) * 1.01;
                int k = 0;
                var hgs = DrawingPlan.Holes(e, maxD).OrderByDescending(g => g.Count).ToList();
                if (hgs.Count > 12) { o.Notes.Add("孔的规格太多（" + hgs.Count + " 种），只标了 12 种，请人工补"); hgs = hgs.Take(12).ToList(); }
                bool extra = kv.Key == DwgViewRole.Left || kv.Key == DwgViewRole.Back;
                foreach (var g in hgs) {
                    ModelHole mh; map.TryGetValue(g.Leader, out mh);
                    // 补的反方向视图只标盲孔（通孔在对面那个视图里已经是圆、已经标过）
                    if (extra && (mh == null || mh.Through)) continue;
                    // 不是孔（凸台外圆、轴）就不当孔标
                    if (mh == null && holes.Count > 0 && !map.Keys.Any(c => Math.Abs(c.R - g.Leader.R) < 1e-6)) { }
                    o.Required++;
                    string pre, val, suf, sub;
                    // 这个视图里孔能对上三维孔（map 非空）而这组对不上 → 凸台 / 外圆；零件根本没有孔 → 也都是外圆
                    bool isHole = mh != null || (holes.Count > 0 && map.Count == 0);
                    DrawingText.Hole(g.Count, mh, g.DiameterMm, isHole, out pre, out val, out suf, out sub);
                    if (isHole) o.Holes += g.Count;
                    // 没有孔特征的螺纹孔：看视图里的螺纹线
                    if (val.Length == 0 && isHole) {
                        string m = DrawingPlan.ThreadFromEdges(g.Leader, threads);
                        if (m.Length > 0) { val = m; DrawingText.ThreadOnly(m, mh, out suf, out sub); }
                    }
                    // 定位销孔（没螺纹、没沉孔的 Ø4/5/6/8 光孔，DrawingTolerance）：加 H7 + 位置度框，文字下面多留一格放框
                    // 视图里的圆没对上三维孔（HoleMap 偶尔整个视图对不上，02-010）时按直径在三维孔里找：同直径的孔全是光孔才算
                    var pinHole = mh;
                    if (pinHole == null && isHole) {
                        var same = holes.Where(h => Math.Abs(h.Radius * 2000 - g.DiameterMm) < 0.02).ToList();
                        if (same.Count > 0 && same.All(h => h.CbRadius <= 0 && (h.Spec == null || (h.Spec.Kind != HoleKind.Tapped && h.Spec.Kind != HoleKind.Counterbore && h.Spec.ThreadSize.Length == 0)))) pinHole = same[0];
                    }
                    bool cbore = pinHole != null && (pinHole.CbRadius > 0 || (pinHole.Spec != null && pinHole.Spec.Kind == HoleKind.Counterbore));
                    bool pin = DrawingTolerance.IsPinHole(g.DiameterMm, isHole && pinHole != null, val.Length > 0, cbore || sub.Length > 0);
                    var ls = DwgWriter.ToSheet(v, g.Leader.Cx, g.Leader.Cy);
                    string shown = pre + (val.Length > 0 ? val : "Ø" + g.DiameterMm.ToString("0.##")) + suf + (pin ? " H7(+0.012)" : "");
                    var text = Spot(w, sbox, ls[1], shown, sub.Length > 0 ? 2 : 1, k, pin ? 0.006 : 0);
                    if (w.HoleCallout(v, g, pre, val, suf, sub, text)) {
                        o.Covered++; o.HoleCallouts++;
                        if (pin && w.Fit(w.LastCallout, g.DiameterMm)) {
                            pinGroups++; pinCount += g.Count; pins.Add(pinHole);
                            w.Position(w.LastCallout);
                        }
                    }
                    k++;
                }
                bool sectionView = false; try { sectionView = v.DrawingViewType == D.DrawingViewTypeConstants.igXSectionView; } catch { }
                if (extra) continue;   // 外圆、圆角、倒角在主视图那一侧已经标了
                // 同心圆的外圈：不是沉孔那圈（已写在孔标注的第二行）的，单独标直径（台阶孔大径、法兰外圆）
                foreach (var g in DrawingPlan.OuterRings(e, maxD)) {
                    bool cb = map.Any(mp => Math.Abs(mp.Value.CbRadius * 2000 - g.DiameterMm) < 0.02 && Math.Abs(mp.Key.Cx - g.Leader.Cx) < 1e-5 && Math.Abs(mp.Key.Cy - g.Leader.Cy) < 1e-5)
                        || holes.Any(h => Math.Abs(h.CbRadius * 2000 - g.DiameterMm) < 0.02);
                    if (cb || DrawingPlan.ThreadFromEdges(new DwgEdge { Kind = DwgKind.Circle, Cx = g.Leader.Cx, Cy = g.Leader.Cy, R = 0 }, threads) == "M" + g.DiameterMm) continue;
                    o.Required++;
                    var ls2 = DwgWriter.ToSheet(v, g.Leader.Cx, g.Leader.Cy);
                    var t2 = Spot(w, sbox, ls2[1], (g.Count > 1 ? g.Count + "×" : "") + "Ø" + g.DiameterMm.ToString("0.##"), 1, k++);
                    if (w.HoleCallout(v, g, g.Count > 1 ? g.Count + "×" : "", "", "", "", t2)) o.Covered++;
                }
                // 刻字、花纹会带来几百个小圆弧：只标前 10 组（个数多的、半径大的在前），其余提示人工
                var radii = sectionView ? new List<RadiusGroup>() : DrawingPlan.Radii(e).Where(g => g.RadiusMm >= 0.3).OrderByDescending(g => g.Count).ThenByDescending(g => g.RadiusMm).ToList();
                if (radii.Count > 10) { o.Notes.Add("圆弧太多（" + radii.Count + " 种半径，多半是刻字 / 花纹），只标了 10 种，请人工看一眼"); radii = radii.Take(10).ToList(); }
                foreach (var g in radii) {
                    o.Required++; o.Radii++;
                    var ls = DwgWriter.ToSheet(v, g.Leader.Cx, g.Leader.Cy);
                    var text = Spot(w, sbox, ls[1], DrawingPlan.RadiusPrefix(g.Count) + "R" + g.RadiusMm.ToString("0.##"), 1, k);
                    if (w.Radius(v, g, text)) o.Covered++;
                    k++;
                }
                // 倒角：等于模板「未注倒角」那个值的不标（A3 模板写 C1，A4 写 C0.5）
                double dflt = lay.Zones.Name == "A4" ? 0.5 : 1.0;
                foreach (var g in sectionView ? new List<RadiusGroup>() : DrawingPlan.Chamfers(e)) {
                    if (Math.Abs(g.RadiusMm - dflt) < 0.01) continue;
                    o.Required++;
                    var l = g.Leader;
                    double mx = (l.X1 + l.X2) / 2, my = (l.Y1 + l.Y2) / 2;
                    // 文字往这个角的外侧斜上方引出 8mm
                    var sp = DwgWriter.ToSheet(v, mx, my);
                    double cx = (sbox[0] + sbox[2]) / 2, cy = (sbox[1] + sbox[3]) / 2;
                    var text = Spot(w, sbox, sp[1], DrawingPlan.ChamferText(g.Count, g.RadiusMm), 1, k++);
                    if (w.Note(v, l, mx, my, DrawingPlan.ChamferText(g.Count, g.RadiusMm), text) != null) o.Covered++;
                }
            }
            // 销孔所在的面（装配时贴着别的零件、靠销定位的安装面）标 Ra3.2：在孔是侧面的那个视图里找过孔口的那条面线
            bool rough = pins.Count > 0 && MatingFaces(w, views, edges, pins, range) > 0;
            string tn = DrawingTolerance.Note(pinGroups, pinCount, rough);
            if (tn.Length > 0) o.Notes.Insert(0, tn);
        }

        static int MatingFaces(DwgWriter w, Dictionary<DwgViewRole, D.DrawingView> views, Dictionary<DwgViewRole, List<DwgEdge>> edges, List<ModelHole> pins, double[] range){
            var done = new HashSet<string>(); int n = 0;
            foreach (var h in pins) {
                if (h == null || h.Segments.Count == 0) continue;
                // 开口那头（盲孔只有一头；通孔取 +轴 那头）
                double t = !h.BlindHi ? h.Segments.Max(sg => sg.Hi) : h.Segments.Min(sg => sg.Lo);
                var mouth = h.Point + h.Axis * t;
                int ax = Math.Abs(h.Axis.X) > 0.9998 ? 0 : Math.Abs(h.Axis.Y) > 0.9998 ? 1 : Math.Abs(h.Axis.Z) > 0.9998 ? 2 : -1;
                if (ax < 0) continue;
                string key = ax + ":" + Math.Round(ax == 0 ? mouth.X : ax == 1 ? mouth.Y : mouth.Z, 5);
                if (done.Contains(key)) continue;
                foreach (var kv in views) {
                    if (kv.Key == DwgViewRole.Iso || !edges.ContainsKey(kv.Key)) continue;
                    try {
                        V3 x, y, d; double ou, ov;
                        if (!Frame(kv.Value, edges[kv.Key], range, out x, out y, out d, out ou, out ov)) continue;
                        if (Math.Abs(h.Axis.Dot(d)) > 1e-3) continue;
                        bool alongX = Math.Abs(h.Axis.Dot(x)) > 0.999, alongY = Math.Abs(h.Axis.Dot(y)) > 0.999;
                        if (!alongX && !alongY) continue;
                        double[] at;
                        var face = DrawingTolerance.MouthFace(edges[kv.Key], alongX, mouth.Dot(x) + ou, mouth.Dot(y) + ov, h.Radius, out at);
                        if (face == null) continue;
                        if (w.Roughness(kv.Value, face, at, DrawingTolerance.MatingRoughness)) { done.Add(key); n++; }
                        break;
                    } catch (Exception e) { Log.Write("DrawingMatingFace", e); }
                }
            }
            return n;
        }

        // 视图的屏幕轴（x 右、y 上、d 视线）和"模型坐标投影 → 视图坐标"的平移（零件投影外框对齐视图图元外框，同 HoleMap）
        static bool Frame(D.DrawingView v, List<DwgEdge> edges, double[] range, out V3 x, out V3 y, out V3 d, out double ou, out double ov){
            double dx, dy, dz, ux, uy, uz; D.ViewOrientationConstants k; v.ViewOrientation(out dx, out dy, out dz, out ux, out uy, out uz, out k);
            d = new V3(dx, dy, dz).Unit(); x = new V3(ux, uy, uz).Unit(); y = d.Cross(x);
            double pu = double.MaxValue, pv = double.MaxValue;
            for (int i = 0; i < 8; i++) { var c = new V3(range[(i & 1) == 0 ? 0 : 3], range[(i & 2) == 0 ? 1 : 4], range[(i & 4) == 0 ? 2 : 5]); pu = Math.Min(pu, c.Dot(x)); pv = Math.Min(pv, c.Dot(y)); }
            var box = DrawingPlan.Box(edges);
            ou = box[0] - pu; ov = box[1] - pv;
            return edges.Count > 0;
        }
        // 引线文字放哪：在视图旁边找一块空地（DwgSpace）；实在没有就按老办法往视图右上角叠
        // below：文字下面再留多高（放位置度框）
        static double[] Spot(DwgWriter w, double[] sbox, double y, string text, int lines, int k, double below = 0){
            double tw = DwgSpace.TextWidth(text) + 0.002, th = 0.0045 * lines + below;
            var r = w.Space == null ? null : w.Space.Place(sbox, y, tw, th);
            if (r != null) return new[] { r[0], r[1] + 0.001 + below };
            return new[] { sbox[2] + 0.006, sbox[3] - 0.004 - k * 0.008 };
        }

        static void Count(DrawingOutcome o, OrdinateRun r){
            if (r == null) return;
            if (r.Count >= DrawingPlan.MaxRun && !o.Notes.Any(n => n.StartsWith("细节太多"))) o.Notes.Add("细节太多（刻线 / 刻字 / 花纹），坐标尺寸只标了外形、孔和长边，请人工补");
            o.Required += r.Count - 1;
            if (r.Count > 28 && !o.Notes.Any(n => n.StartsWith("尺寸很密"))) o.Notes.Add("尺寸很密（一串 " + r.Count + " 个），建议人工整理或加局部视图");
        }
        static int Covered(int created){ return Math.Max(0, created - 1); }
        static List<double> Sheet(D.DrawingView v, OrdinateRun r, bool horizontal){
            var l = new List<double>();
            if (r == null) return l;
            foreach (var t in new[] { r.Origin }.Concat(r.Targets)) { var p = DwgWriter.ToSheet(v, t.X, t.Y); l.Add(horizontal ? p[0] : p[1]); }
            return l;
        }
        // 只留主视图里没标过的坐标（比较图纸坐标：投影对齐的视图同一个模型坐标落在同一条图纸线上）
        // 只留主视图里没标过的坐标。按"到外形最小边的距离"比（剖视不和主视图投影对齐、还可能左右镜像），
        // 镜像（两个视图同一方向的屏幕轴反向）时从另一头量
        static OrdinateRun Only(OrdinateRun r, D.DrawingView v, List<double> done, bool horizontal, D.DrawingView main, double[] mainBox, double[] vBox){
            if (r == null) return null;
            var mlo = DwgWriter.ToSheet(main, mainBox[0], mainBox[1]);
            var vlo = DwgWriter.ToSheet(v, vBox[0], vBox[1]); var vhi = DwgWriter.ToSheet(v, vBox[2], vBox[3]);
            bool mirror = Axis(v, horizontal).Dot(Axis(main, horizontal)) < 0;
            var rel = done.Select(d => d - (horizontal ? mlo[0] : mlo[1])).ToList();
            var keep = r.Targets.Where(t => {
                var p = DwgWriter.ToSheet(v, t.X, t.Y); double c = horizontal ? p[0] : p[1];
                double cr = mirror ? (horizontal ? Math.Max(vlo[0], vhi[0]) - c : Math.Max(vlo[1], vhi[1]) - c) : c - (horizontal ? Math.Min(vlo[0], vhi[0]) : Math.Min(vlo[1], vhi[1]));
                return !rel.Any(d => Math.Abs(d - cr) < 2e-5);
            }).ToList();
            if (keep.Count == 0) return null;
            r.Targets.Clear(); r.Targets.AddRange(keep);
            DrawingPlan.Spread(new List<DwgTarget>(keep) { r.Origin }, DrawingPlan.TextPitch / Math.Max(v.ScaleFactor, 1e-9));
            return r;
        }
        static V3 Axis(D.DrawingView v, bool horizontal){
            try { double dx, dy, dz, ux, uy, uz; D.ViewOrientationConstants k; v.ViewOrientation(out dx, out dy, out dz, out ux, out uy, out uz, out k); var x = new V3(ux, uy, uz); return horizontal ? x : new V3(dx, dy, dz).Cross(x); } catch { return new V3(1, 0, 0); }
        }

        static string Describe(D.DrawingView v){
            try {
                double dx, dy, dz, ux, uy, uz; D.ViewOrientationConstants k; v.ViewOrientation(out dx, out dy, out dz, out ux, out uy, out uz, out k);
                var b = DwgWriter.SheetBox(v);
                Func<double, string> f = x => Math.Round(x, 3).ToString(System.Globalization.CultureInfo.InvariantCulture);
                return k + " 视线(" + f(dx) + "," + f(dy) + "," + f(dz) + ") x(" + f(ux) + "," + f(uy) + "," + f(uz) + ") 图纸框 " + f(b[0] * 1000) + "," + f(b[1] * 1000) + " - " + f(b[2] * 1000) + "," + f(b[3] * 1000);
            } catch (Exception e) { return e.Message; }
        }

        // 视线和孔轴垂直时，孔在视图里是一条带（孔壁、沉孔壁的轮廓线 / 隐藏线，沉孔底那条横线）。
        // 这些线不是零件外形，坐标尺寸不该标它们 —— 孔的位置在孔是圆的那个视图里标，深度写在孔标注里。
        static List<DwgEdge> HoleInternal(D.DrawingView v, List<DwgEdge> edges, List<ModelHole> holes, double[] range){
            if (holes.Count == 0) return edges;
            try {
                double dx, dy, dz, ux, uy, uz; D.ViewOrientationConstants k; v.ViewOrientation(out dx, out dy, out dz, out ux, out uy, out uz, out k);
                var d = new V3(dx, dy, dz).Unit(); var x = new V3(ux, uy, uz).Unit(); var y = d.Cross(x);
                double pu = double.MaxValue, pv = double.MaxValue;
                for (int i = 0; i < 8; i++) { var c = new V3(range[(i & 1) == 0 ? 0 : 3], range[(i & 2) == 0 ? 1 : 4], range[(i & 4) == 0 ? 2 : 5]); pu = Math.Min(pu, c.Dot(x)); pv = Math.Min(pv, c.Dot(y)); }
                var box = DrawingPlan.Box(edges);
                double ou = box[0] - pu, ov = box[1] - pv;
                var bands = new List<double[]>();   // {沿 x 走(1)/沿 y 走(0), 中心, 半宽, 起, 止}
                foreach (var h in holes) {
                    if (Math.Abs(h.Axis.Dot(d)) > 1e-3) continue;
                    double hw = Math.Max(h.Radius, h.CbRadius);
                    double ax = Math.Abs(h.Axis.Dot(x)), ay = Math.Abs(h.Axis.Dot(y));
                    var p0 = h.Point + h.Axis * h.Segments.Min(sg => sg.Lo); var p1 = h.Point + h.Axis * h.Segments.Max(sg => sg.Hi);
                    if (ax > 0.999) bands.Add(new[] { 1.0, h.Point.Dot(y) + ov, hw, Math.Min(p0.Dot(x), p1.Dot(x)) + ou, Math.Max(p0.Dot(x), p1.Dot(x)) + ou });
                    else if (ay > 0.999) bands.Add(new[] { 0.0, h.Point.Dot(x) + ou, hw, Math.Min(p0.Dot(y), p1.Dot(y)) + ov, Math.Max(p0.Dot(y), p1.Dot(y)) + ov });
                }
                if (bands.Count == 0) return edges;
                const double tol = 2e-5;
                return edges.Where(e => {
                    if (e.Kind != DwgKind.Line) return true;
                    foreach (var b in bands) {
                        bool alongX = b[0] > 0.5; double c = b[1], hw = b[2], s0 = b[3] - tol, s1 = b[4] + tol;
                        double pmin = alongX ? Math.Min(e.Y1, e.Y2) : Math.Min(e.X1, e.X2), pmax = alongX ? Math.Max(e.Y1, e.Y2) : Math.Max(e.X1, e.X2);
                        double qmin = alongX ? Math.Min(e.X1, e.X2) : Math.Min(e.Y1, e.Y2), qmax = alongX ? Math.Max(e.X1, e.X2) : Math.Max(e.Y1, e.Y2);
                        // 整条线落在孔带里（横向在 c±hw 内、沿孔方向在孔的长度内）
                        if (pmin >= c - hw - tol && pmax <= c + hw + tol && qmin >= s0 && qmax <= s1) return false;
                    }
                    return true;
                }).ToList();
            } catch (Exception ex) { Log.Write("DrawingHoleInternal", ex); return edges; }
        }

        // 视图里的圆 ↔ 模型里的孔：按视图朝向把孔轴线投影到视图平面，再用整个零件投影外框和视图图元外框对齐平移
        static Dictionary<DwgEdge, ModelHole> HoleMap(D.DrawingView v, List<DwgEdge> edges, List<ModelHole> holes, double[] range){
            var map = new Dictionary<DwgEdge, ModelHole>();
            if (holes.Count == 0) return map;
            try {
                double dx, dy, dz, ux, uy, uz; D.ViewOrientationConstants k; v.ViewOrientation(out dx, out dy, out dz, out ux, out uy, out uz, out k);
                var d = new V3(dx, dy, dz).Unit(); var x = new V3(ux, uy, uz).Unit(); var y = d.Cross(x);
                double pu = double.MaxValue, pv = double.MaxValue;
                for (int i = 0; i < 8; i++) { var c = new V3(range[(i & 1) == 0 ? 0 : 3], range[(i & 2) == 0 ? 1 : 4], range[(i & 4) == 0 ? 2 : 5]); pu = Math.Min(pu, c.Dot(x)); pv = Math.Min(pv, c.Dot(y)); }
                var box = DrawingPlan.Box(edges);
                double ou = box[0] - pu, ov = box[1] - pv;
                foreach (var e in edges) {
                    if (e.Kind != DwgKind.Circle) continue;
                    foreach (var h in holes) {
                        if (Math.Abs(Math.Abs(h.Axis.Dot(d)) - 1) > 1e-4) continue;
                        double cu = h.Point.Dot(x) + ou, cv = h.Point.Dot(y) + ov;
                        if (Math.Abs(cu - e.Cx) < 1e-4 && Math.Abs(cv - e.Cy) < 1e-4 && (Math.Abs(h.Radius - e.R) < 1e-5 || Math.Abs(h.CbRadius - e.R) < 1e-5)) { map[e] = h; break; }
                    }
                }
            } catch (Exception e) { Log.Write("DrawingHoleMap", e); }
            return map;
        }

        // 模板里自带一条「刻字：%{文件名（无扩展名）|R1}」引线，没挂在任何东西上（文字在标题栏左上方）。
        // 天工不让把它直接改挂（SetTerminator 0x80040223，探针 3），所以照它的文字和位置新建一条挂到轴测图最长的边上，再删掉模板那条。
        // 模板刻字引线文字占的地方（图纸坐标）：按模板那条的位置，宽度按展开后的「刻字：文件名」估
        static double[] EngraveBox(D.Sheet sheet, string name){
            try {
                var bs = (S.Balloons)sheet.Balloons;
                for (int i = 1; i <= bs.Count; i++) {
                    var b = bs.Item(i); string t = ""; try { t = b.BalloonText ?? ""; } catch { }
                    if (!t.Contains("刻字")) continue;
                    double a, c, d, e; b.Range(out a, out c, out d, out e);
                    double wd = Math.Max(d - a, DwgSpace.TextWidth("刻字：" + name) + 0.004);
                    return new[] { a - 0.002, Math.Min(c, e) - 0.002, a + wd, Math.Max(c, e) + 0.002 };
                }
            } catch (Exception ex) { Log.Write("DrawingEngraveBox", ex); }
            return null;
        }

        static void Engrave(D.Sheet sheet, D.DrawingView iso, DrawingOutcome o){
            try {
                var bs = (S.Balloons)sheet.Balloons;
                S.Balloon tpl = null;
                for (int i = 1; i <= bs.Count; i++) { var b = bs.Item(i); string t = ""; try { t = b.BalloonText ?? ""; } catch { } if (t.Contains("刻字")) { tpl = b; break; } }
                var edges = DrawingViewRead.Edges(iso).Where(x => x.Kind == DwgKind.Line).OrderByDescending(x => x.Length).ToList();
                if (edges.Count == 0) return;
                var best = edges[0];
                double[] at = null;
                string text = "刻字：%{文件名（无扩展名）|R1}";
                if (tpl != null) {
                    try { text = tpl.BalloonText; } catch { }
                    try { double a, b, c, d; tpl.Range(out a, out b, out c, out d); at = new[] { a, (b + d) / 2 }; } catch { }
                }
                if (at == null) { var ib = DwgWriter.SheetBox(iso); at = new[] { ib[0] - 0.06, 0.050 }; }
                var w = new DwgWriter(sheet);
                var nb = w.Note(iso, best, (best.X1 + best.X2) / 2, (best.Y1 + best.Y2) / 2, text, at);
                if (nb != null && tpl != null) { try { tpl.Delete(); } catch { } }
                if (nb == null) o.Notes.Add("刻字引线没挂上（模板那条还在，请手动挂到零件上）");
            } catch (Exception e) { o.Notes.Add("刻字引线没挂上：" + e.Message); }
        }
    }
}
