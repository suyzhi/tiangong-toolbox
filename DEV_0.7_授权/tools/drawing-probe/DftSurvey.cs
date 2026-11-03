using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using D=SolidEdgeDraft;
using F=SolidEdgeFramework;
using S=SolidEdgeFrameworkSupport;

// 只读普查：逐个打开工程图，记下图纸 / 视图 / 尺寸 / 标注的统计，一张图一行 JSON，给"一键出图"定规矩用。
// 用法：DftSurvey.exe <输出目录> <清单文件(每行一个 .dft)> [起始行] [PDF 张数上限]
// 不存盘：打开后 Close(false)。清单里的文件可以是加密的（天工自己能读）。
class DftSurvey {
    static StreamWriter outw;
    static void L(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    static T Try<T>(Func<T> f, T d){ try { var v = f(); return v == null ? d : v; } catch { return d; } }
    static Dictionary<string,object> O(params object[] p){ var r=new Dictionary<string,object>(); for(int i=0;i<p.Length;i+=2) r[(string)p[i]]=p[i+1]; return r; }
    static int Cnt(object sheet, string prop){ try { object c = sheet.GetType().InvokeMember(prop, System.Reflection.BindingFlags.GetProperty, null, sheet, null); return c==null?-1:(int)c.GetType().InvokeMember("Count", System.Reflection.BindingFlags.GetProperty, null, c, null); } catch { return -1; } }

    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = Encoding.UTF8;
        string outDir = args[0]; string list = args[1];
        int start = args.Length > 2 ? int.Parse(args[2]) : 0;
        int pdfMax = args.Length > 3 ? int.Parse(args[3]) : 0;
        F.Application app;
        try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); } catch (Exception e) { L("连不上 CAD：" + e.Message); return 2; }
        try { app.DisplayAlerts = false; } catch { }
        var files = File.ReadAllLines(list, Encoding.UTF8).Where(x => x.Trim().Length > 0).ToArray();
        var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        outw = new StreamWriter(Path.Combine(outDir, "survey.jsonl"), true, new UTF8Encoding(false));
        int pdfs = 0;
        for (int i = start; i < files.Length; i++) {
            string f = files[i].Trim();
            var row = O("i", i, "file", f);
            var t0 = DateTime.Now;
            D.DraftDocument dft = null;
            try {
                dft = (D.DraftDocument)app.Documents.Open(f);
                for (int k = 0; k < 10; k++) { try { app.DoIdle(); } catch { } Thread.Sleep(20); }
                Survey(dft, row);
                if (pdfs < pdfMax) {
                    string pdf = Path.Combine(outDir, "pdf", i.ToString("D4") + ".pdf");
                    Directory.CreateDirectory(Path.GetDirectoryName(pdf));
                    try { object old = null; app.GetGlobalParameter(F.ApplicationGlobalConstants.seApplicationGlobalDraftSaveAsPDFSheetOptions, ref old); app.SetGlobalParameter(F.ApplicationGlobalConstants.seApplicationGlobalDraftSaveAsPDFSheetOptions, 1); dft.SaveCopyAs(pdf); app.SetGlobalParameter(F.ApplicationGlobalConstants.seApplicationGlobalDraftSaveAsPDFSheetOptions, old); row["pdf"] = pdf; pdfs++; } catch (Exception e) { row["pdf_error"] = e.Message; }
                }
            } catch (Exception e) { row["error"] = e.Message; }
            finally { try { if (dft != null) dft.Close(false); } catch { } }
            row["sec"] = Math.Round((DateTime.Now - t0).TotalSeconds, 1);
            outw.WriteLine(js.Serialize(row)); outw.Flush();
            L("[" + i + "/" + files.Length + "] " + row["sec"] + "s " + (row.ContainsKey("error") ? "ERR " + row["error"] : "") + " " + Path.GetFileName(f));
        }
        outw.Close();
        L("RESULT done " + files.Length);
        return 0;
    }

    static void Survey(D.DraftDocument d, Dictionary<string,object> row){
        var sheets = new List<object>(); row["sheets"] = sheets;
        row["model_links"] = Try(() => d.ModelLinks.Count, -1);
        try { var ml = new List<object>(); foreach (D.ModelLink m in d.ModelLinks) ml.Add(Try(() => m.FileName, "?")); row["links"] = ml; } catch { }
        row["parts_lists"] = Try(() => d.PartsLists.Count, -1);
        try { var pls = new List<object>(); foreach (D.PartsList p in d.PartsLists) { pls.Add(O("rows", Try(() => p.Rows.Count, -1), "cols", Try(() => p.Columns.Count, -1), "heads", Try(() => { var h = new List<string>(); foreach (D.TableColumn c in p.Columns) h.Add(Try(() => c.HeaderRowValue, "?")); return string.Join("|", h); }, ""))); } row["pl"] = pls; } catch { }
        row["hole_tables"] = Try(() => d.HoleTables.Count, -1);
        foreach (D.Sheet sh in d.Sheets) {
            var st = Try(() => sh.SectionType.ToString(), "?");
            if (st != "igWorkingSection") continue;
            var sr = O("name", Try(() => sh.Name, "?"), "w", Try(() => sh.SheetSetup.SheetWidth, 0.0), "h", Try(() => sh.SheetSetup.SheetHeight, 0.0),
                "bg", Try(() => sh.Background.Name, ""), "size", Try(() => sh.SheetSetup.SheetSizeOption.ToString(), ""));
            sheets.Add(sr);
            var views = new List<object>(); sr["views"] = views;
            var viewKeys = new Dictionary<string,int>();
            int vi = 0;
            foreach (D.DrawingView v in sh.DrawingViews) {
                vi++;
                var vr = O("type", Try(() => v.DrawingViewType.ToString(), "?"), "scale", Try(() => v.ScaleFactor, 0.0), "name", Try(() => v.Name, ""), "caption", Try(() => v.CaptionDisplayedTextPrimary, ""), "showcap", Try(() => v.DisplayCaption, false));
                try { double dx,dy,dz,ux,uy,uz; D.ViewOrientationConstants k; v.ViewOrientation(out dx,out dy,out dz,out ux,out uy,out uz,out k); vr["orient"] = k.ToString(); vr["dir"] = new[]{ Math.Round(dx,3), Math.Round(dy,3), Math.Round(dz,3) }; } catch { }
                try { double a,b,c,e; v.Range(out a,out b,out c,out e); vr["range"] = new[]{ Math.Round(a,4), Math.Round(b,4), Math.Round(c,4), Math.Round(e,4) }; } catch { }
                try { var link = (D.ModelLink)v.ModelLink; vr["model"] = Path.GetFileName(Try(() => link.FileName, "")); } catch { }
                vr["lines"] = Try(() => v.DVLines2d.Count, -1); vr["circles"] = Try(() => v.DVCircles2d.Count, -1); vr["arcs"] = Try(() => v.DVArcs2d.Count, -1);
                vr["shading"] = Try(() => v.Shading, false);
                vr["src"] = Try(() => v.SourceDrawingView == null ? 0 : v.SourceDrawingView.Index, 0);
                try { viewKeys[v.Key] = vi; } catch { }
                vr["dims"] = 0;
                views.Add(vr);
            }
            // 尺寸：类型分布、前缀/后缀/公差、挂在哪个视图
            var types = new Dictionary<string,int>(); var prefixes = new List<string>(); int tol = 0, ovr = 0, toView = 0, n = 0;
            var samples = new List<object>();
            try {
                S.Dimensions ds = (S.Dimensions)sh.Dimensions;
                foreach (S.Dimension dm in ds) {
                    n++;
                    string ty = Try(() => dm.DimensionType.ToString(), "?"); types[ty] = (types.ContainsKey(ty) ? types[ty] : 0) + 1;
                    string pre = Try(() => dm.PrefixDisplayedText, "") + "¦" + Try(() => dm.SuffixDisplayedText, "") + "¦" + Try(() => dm.SuperfixDisplayedText, "") + "¦" + Try(() => dm.SubfixDisplayedText, "");
                    if (pre != "¦¦¦" && prefixes.Count < 40) prefixes.Add(pre);
                    { string tt = Try(() => dm.DisplayType.ToString(), ""); if (tt.Contains("Tol") || tt.Contains("Limit") || tt.Contains("Class")) tol++; }
                    if (Try(() => dm.OverrideString, "").Length > 0) ovr++;
                    if (samples.Count < 60) samples.Add(O("t", ty, "v", Math.Round(Try(() => dm.Value, 0.0) * (Try(() => dm.UnitsType, 1) == 1 ? 1000 : 57.29578), 2), "tt", Try(() => dm.DisplayType.ToString(), ""), "o", Try(() => dm.OverrideString, "")));
                    try {
                        int rc; dm.GetRelatedCount(out rc);
                        if (rc > 0) { object ent; double x,y,z; bool kp; dm.GetRelated(0, out ent, out x, out y, out z, out kp); var rf = ent as F.Reference; if (rf != null) ent = rf.Object;
                            D.DrawingView dv = null; var l = ent as D.DVLine2d; var c = ent as D.DVCircle2d; var a = ent as D.DVArc2d;
                            if (l != null) dv = l.DrawingView; else if (c != null) dv = c.DrawingView; else if (a != null) dv = a.DrawingView;
                            int idx; if (dv != null && viewKeys.TryGetValue(dv.Key, out idx)) { toView++; var vv = (Dictionary<string,object>)views[idx - 1]; vv["dims"] = (int)vv["dims"] + 1; } }
                    } catch { }
                }
            } catch (Exception e) { sr["dim_err"] = e.Message; }
            sr["dims"] = n; sr["dim_types"] = types; sr["dim_affix"] = prefixes; sr["dim_tol"] = tol; sr["dim_override"] = ovr; sr["dim_on_view"] = toView; sr["dim_samples"] = samples;
            foreach (var p in new[]{ "CenterLines","CenterMarks","Balloons","SurfaceFinishSymbols","WeldSymbols","FeatureControlFrames","DatumFrames","Leaders","TextBoxes","BoltHoleCircles","TechnicalRequirements","BlockOccurrences","Lines2d","CuttingPlanes","Symbols" })
                sr["n_" + p] = Cnt(sh, p);
            try { var tx = new List<string>(); dynamic tbs = sh.TextBoxes; for (int j = 1; j <= (int)tbs.Count && tx.Count < 20; j++) { string s = Try(() => (string)tbs.Item(j).Text, ""); if (s.Length > 0) tx.Add(s.Length > 300 ? s.Substring(0, 300) : s); } sr["texts"] = tx; } catch { }
            try { var tx = new List<string>(); dynamic ls = sh.Leaders; for (int j = 1; j <= (int)ls.Count && tx.Count < 20; j++) { string s = Try(() => (string)ls.Item(j).TextString, ""); if (s.Length > 0) tx.Add(s); } sr["leader_texts"] = tx; } catch { }
            try { var tx = new List<string>(); var raw = new List<string>(); S.Balloons bs = (S.Balloons)sh.Balloons; for (int j = 1; j <= bs.Count && tx.Count < 30; j++) { S.Balloon b = bs.Item(j); tx.Add(Try(() => b.BalloonDisplayedText, "")); raw.Add(Try(() => b.Callout, -9) + "|" + Try(() => b.BalloonText, "") + "|" + Try(() => b.BalloonType.ToString(), "")); } sr["balloon_texts"] = tx; sr["balloon_raw"] = raw; } catch (Exception e) { sr["balloon_err"] = e.Message; }
            try { var tx = new List<string>(); dynamic tr = sh.TechnicalRequirements; for (int j = 1; j <= (int)tr.Count && tx.Count < 5; j++) { string s = Try(() => (string)tr.Item(j).Text, ""); tx.Add(s.Length > 400 ? s.Substring(0, 400) : s); } sr["tech_req"] = tx; } catch { }
            try { sr["tol"] = Tolerances(sh, viewKeys); } catch (Exception e) { sr["tol_err"] = e.Message; }
        }
    }

    // ---- 公差普查（0.11.1）----
    // 每张图页的 "tol"：全部尺寸（类型 / 值 / 显示类型；带公差的再记上下偏差、公差带；非坐标尺寸记挂在哪些图元上）、
    // 每个形位公差框、基准、粗糙度符号连同挂的图元（视图坐标毫米 + 视图序号 + 那个视图全部图元的外框），给"一键出图加公差"定规矩用。
    static readonly Dictionary<string,double[]> boxCache = new Dictionary<string,double[]>();
    static double R(double v){ return Math.Round(v * 1000, 3); }   // 米 → 毫米

    static double[] ViewBox(D.DrawingView v){
        string k = Try(() => v.Key, ""); double[] b;
        if (k.Length > 0 && boxCache.TryGetValue(k, out b)) return b;
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        Action<double,double> add = (x, y) => { x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y); };
        try { foreach (D.DVLine2d l in v.DVLines2d) { double a, c; l.GetStartPoint(out a, out c); add(a, c); l.GetEndPoint(out a, out c); add(a, c); } } catch { }
        try { foreach (D.DVCircle2d c in v.DVCircles2d) { double a, e; c.GetCenterPoint(out a, out e); double r = c.Radius; add(a - r, e - r); add(a + r, e + r); } } catch { }
        try { foreach (D.DVArc2d c in v.DVArcs2d) { double a, e; c.GetStartPoint(out a, out e); add(a, e); c.GetEndPoint(out a, out e); add(a, e); } } catch { }
        b = x0 == double.MaxValue ? new double[0] : new[] { R(x0), R(y0), R(x1), R(y1) };
        if (k.Length > 0) boxCache[k] = b;
        return b;
    }

    static Dictionary<string,object> Elem(object ent, Dictionary<string,int> viewKeys){
        var rf = ent as F.Reference; if (rf != null) { try { ent = rf.Object; } catch { } }
        if (ent == null) return O("k", "null");
        D.DrawingView dv = null; Dictionary<string,object> r;
        var l = ent as D.DVLine2d; var c = ent as D.DVCircle2d; var a = ent as D.DVArc2d; var dm = ent as S.Dimension;
        if (l != null) { double p, q, u, w; l.GetStartPoint(out p, out q); l.GetEndPoint(out u, out w); r = O("k", "L", "g", new[] { R(p), R(q), R(u), R(w) }, "et", Try(() => l.EdgeType.ToString(), "")); dv = l.DrawingView; }
        else if (c != null) { double p, q; c.GetCenterPoint(out p, out q); r = O("k", "C", "g", new[] { R(p), R(q), R(c.Radius) }, "et", Try(() => c.EdgeType.ToString(), "")); dv = c.DrawingView; }
        else if (a != null) { double p, q, u, w, x, y; a.GetCenterPoint(out p, out q); a.GetStartPoint(out u, out w); a.GetEndPoint(out x, out y); r = O("k", "A", "g", new[] { R(p), R(q), R(a.Radius), R(u), R(w), R(x), R(y) }); dv = a.DrawingView; }
        else if (dm != null) return O("k", "D", "t", Try(() => dm.DimensionType.ToString(), ""), "v", Math.Round(Try(() => dm.Value, 0.0) * 1000, 3));
        else return O("k", Try(() => Microsoft.VisualBasic.Information.TypeName(ent), "?"));
        if (dv != null) { int idx; if (viewKeys.TryGetValue(Try(() => dv.Key, ""), out idx)) r["view"] = idx; r["vbox"] = ViewBox(dv); }
        return r;
    }

    // 人画的形位公差框 / 基准 / 粗糙度多数没真的挂在图元上（普查冒烟：ref 全空），只能按位置找：
    // 箭头点（或符号框）离哪条视图图元、哪个尺寸最近。图页上所有视图图元换成图纸坐标（毫米）缓存一份。
    sealed class SE { public int Kind; public double A, B, C, E; public Dictionary<string,object> Desc; }
    static List<SE> sheetElems; static List<KeyValuePair<double[], Dictionary<string,object>>> sheetDims;
    static void BuildSheet(D.Sheet sh, Dictionary<string,int> viewKeys){
        sheetElems = new List<SE>();
        foreach (D.DrawingView v in sh.DrawingViews) {
            Func<double,double,double[]> S2 = (x, y) => { double a, b; v.ViewToSheet(x, y, out a, out b); return new[] { R(a), R(b) }; };
            int vi = 0; viewKeys.TryGetValue(Try(() => v.Key, ""), out vi);
            try { foreach (D.DVLine2d l in v.DVLines2d) { double a, c, p, q; l.GetStartPoint(out a, out c); l.GetEndPoint(out p, out q); var s0 = S2(a, c); var s1 = S2(p, q); sheetElems.Add(new SE { Kind = 0, A = s0[0], B = s0[1], C = s1[0], E = s1[1], Desc = Elem(l, viewKeys) }); } } catch { }
            try { foreach (D.DVCircle2d c in v.DVCircles2d) { double a, e; c.GetCenterPoint(out a, out e); var s0 = S2(a, e); sheetElems.Add(new SE { Kind = 1, A = s0[0], B = s0[1], C = R(c.Radius) * v.ScaleFactor, Desc = Elem(c, viewKeys) }); } } catch { }
            try { foreach (D.DVArc2d c in v.DVArcs2d) { double a, e; c.GetCenterPoint(out a, out e); var s0 = S2(a, e); sheetElems.Add(new SE { Kind = 1, A = s0[0], B = s0[1], C = R(c.Radius) * v.ScaleFactor, Desc = Elem(c, viewKeys) }); } } catch { }
        }
    }
    static double SegDist(double px, double py, double ax, double ay, double bx, double by){
        double dx = bx - ax, dy = by - ay, l2 = dx * dx + dy * dy; double t = l2 < 1e-12 ? 0 : Math.Max(0, Math.Min(1, ((px - ax) * dx + (py - ay) * dy) / l2));
        double x = ax + t * dx - px, y = ay + t * dy - py; return Math.Sqrt(x * x + y * y);
    }
    static double BoxDist(double[] a, double[] b){ double dx = Math.Max(0, Math.Max(a[0] - b[2], b[0] - a[2])), dy = Math.Max(0, Math.Max(a[1] - b[3], b[1] - a[3])); return Math.Sqrt(dx * dx + dy * dy); }
    static object NearElem(double x, double y){
        if (sheetElems == null) return null;
        SE best = null; double bd = double.MaxValue;
        foreach (var e in sheetElems) { double d = e.Kind == 0 ? SegDist(x, y, e.A, e.B, e.C, e.E) : Math.Abs(Math.Sqrt((x - e.A) * (x - e.A) + (y - e.B) * (y - e.B)) - e.C); if (d < bd) { bd = d; best = e; } }
        if (best == null) return null;
        var r = new Dictionary<string,object>(best.Desc); r["dist"] = Math.Round(bd, 2); return r;
    }
    // 离符号框最近的视图图元（框和线段 / 圆的距离；没引线的基准三角、粗糙度尖角就贴在要标的那条线上）
    static object NearBox(double[] b){
        if (sheetElems == null || b.Length < 4) return null;
        SE best = null; double bd = double.MaxValue;
        double[][] cs = { new[] { b[0], b[1] }, new[] { b[2], b[1] }, new[] { b[0], b[3] }, new[] { b[2], b[3] }, new[] { (b[0] + b[2]) / 2, b[1] }, new[] { (b[0] + b[2]) / 2, b[3] }, new[] { b[0], (b[1] + b[3]) / 2 }, new[] { b[2], (b[1] + b[3]) / 2 } };
        foreach (var e in sheetElems) {
            double d;
            if (e.Kind == 0) {
                d = Math.Min(BoxDist(b, new[] { e.A, e.B, e.A, e.B }), BoxDist(b, new[] { e.C, e.E, e.C, e.E }));
                foreach (var c in cs) d = Math.Min(d, SegDist(c[0], c[1], e.A, e.B, e.C, e.E));
                // 横 / 竖线段穿过框
                if (d > 0 && Math.Min(e.A, e.C) <= b[2] && Math.Max(e.A, e.C) >= b[0] && Math.Min(e.B, e.E) <= b[3] && Math.Max(e.B, e.E) >= b[1] && (Math.Abs(e.A - e.C) < 1e-6 || Math.Abs(e.B - e.E) < 1e-6)) d = 0;
            } else { d = double.MaxValue; foreach (var c in cs) d = Math.Min(d, Math.Abs(Math.Sqrt((c[0] - e.A) * (c[0] - e.A) + (c[1] - e.B) * (c[1] - e.B)) - e.C)); }
            if (d < bd) { bd = d; best = e; }
        }
        if (best == null || bd > 15) return null;
        var r = new Dictionary<string,object>(best.Desc); r["dist"] = Math.Round(bd, 2); return r;
    }
    static List<object> NearDims(double[] box){
        var l = new List<object>();
        if (sheetDims == null || box.Length < 4) return l;
        foreach (var kv in sheetDims.Select(k => new { k, d = BoxDist(box, k.Key) }).Where(z => z.d < 12).OrderBy(z => z.d).Take(2)) { var r = new Dictionary<string,object>(kv.k.Value); r["dist"] = Math.Round(kv.d, 2); l.Add(r); }
        return l;
    }

    static List<object> Refs(dynamic o, Dictionary<string,int> viewKeys){
        var l = new List<object>();
        try { object t; double x, y, z; bool kp; o.GetTerminator(out t, out x, out y, out z, out kp); bool att = false; try { att = (bool)o.IsTerminatorAttachedToEntity; } catch { }
            var e = att && t != null ? Elem(t, viewKeys) : O("k", "free"); e["via"] = "term"; e["at"] = new[] { R(x), R(y) };
            if (!att) { var ne = NearElem(R(x), R(y)); if (ne != null) e["near"] = ne; e["neardims"] = NearDims(new[] { R(x), R(y), R(x), R(y) }); }
            l.Add(e); } catch { }
        try { int n = (int)o.VertexCount; if (n > 0) { var vs = new List<double[]>(); for (int i = 0; i < n; i++) { double x, y, z; o.GetVertex(i, out x, out y, out z); vs.Add(new[] { R(x), R(y) }); } l.Add(O("k", "vertices", "v", vs)); } } catch { }
        try { Array g = new object[0]; o.GetReferencedGeometry(ref g); foreach (object x in g) if (x != null) { var e = Elem(x, viewKeys); e["via"] = "geom"; l.Add(e); } } catch { }
        try { object s; double x, y; o.GetAlignmentShape(out s, out x, out y); if (s != null) { var e = Elem(s, viewKeys); e["via"] = "align"; l.Add(e); } } catch { }
        return l;
    }
    static double[] Box(dynamic o){ try { double a, b, c, d; o.Range(out a, out b, out c, out d); return new[] { R(a), R(b), R(c), R(d) }; } catch { return new double[0]; } }

    static Dictionary<string,object> Tolerances(D.Sheet sh, Dictionary<string,int> viewKeys){
        var t = new Dictionary<string,object>();
        var dims = new List<object>(); t["dims"] = dims;
        sheetElems = null; sheetDims = new List<KeyValuePair<double[], Dictionary<string,object>>>();
        foreach (S.Dimension dm in (S.Dimensions)sh.Dimensions) {
            string ty = Try(() => dm.DimensionType.ToString(), "?"), tt = Try(() => dm.DisplayType.ToString(), "");
            bool tol = tt.Contains("Tol") || tt.Contains("Limit") || tt.Contains("Class") || tt.Contains("Basic") || tt.Contains("Reference");
            var r = O("t", ty.Replace("igDimType", ""), "v", Math.Round(Try(() => dm.Value, 0.0) * (Try(() => dm.UnitsType, 1) == 1 ? 1000 : 57.29578), 3), "tt", tt.Replace("igDimDisplayType", ""));
            if (tol) { r["up"] = Try(() => dm.PrimaryUpperTolerance, ""); r["lo"] = Try(() => dm.PrimaryLowerTolerance, ""); r["hc"] = Try(() => dm.HoleClassString, ""); r["sc"] = Try(() => dm.ShaftClassString, ""); r["ts"] = Try(() => dm.PrimaryToleranceSymbol, ""); r["std"] = Try(() => dm.UseStandardLimitAndFitTolerance, false); }
            string aff = Try(() => dm.PrefixString, "") + "¦" + Try(() => dm.SuffixString, "") + "¦" + Try(() => dm.SuperfixString, "") + "¦" + Try(() => dm.SubfixString, "") + "¦" + Try(() => dm.OverrideString, "");
            if (aff != "¦¦¦¦") r["a"] = aff;
            if (tol || ty != "igDimTypeCoordinate") {
                var rel = new List<object>();
                try { int rc; dm.GetRelatedCount(out rc); for (int i = 0; i < rc && i < 3; i++) { object ent; double x, y, z; bool kp; dm.GetRelated(i, out ent, out x, out y, out z, out kp); rel.Add(Elem(ent, viewKeys)); } } catch { }
                r["rel"] = rel;
            }
            var bx = Box(dm); if (r.ContainsKey("rel")) r["box"] = bx;
            if (bx.Length == 4) { var brief = O("t", r["t"], "v", r["v"], "tt", r["tt"]); foreach (var k in new[] { "hc", "up", "lo", "a" }) if (r.ContainsKey(k)) brief[k] = r[k]; if (r.ContainsKey("rel") && ((List<object>)r["rel"]).Count > 0) brief["rel0"] = ((List<object>)r["rel"])[0]; sheetDims.Add(new KeyValuePair<double[], Dictionary<string,object>>(bx, brief)); }
            dims.Add(r);
        }
        if (Cnt(sh, "FeatureControlFrames") > 0 || Cnt(sh, "DatumFrames") > 0 || Cnt(sh, "SurfaceFinishSymbols") > 0) try { BuildSheet(sh, viewKeys); } catch (Exception e) { t["near_err"] = e.Message; }
        var fcf = new List<object>(); t["fcf"] = fcf;
        try { foreach (S.FeatureControlFrame f in (S.FeatureControlFrames)sh.FeatureControlFrames)
            fcf.Add(O("f", new[] { Try(() => f.PrimaryFrame, ""), Try(() => f.SecondaryFrame, ""), Try(() => f.TertiaryFrame, ""), Try(() => f.QuaternaryFrame, "") }, "sfx", Try(() => f.FCFSuffix1, "") + "¦" + Try(() => f.FCFSuperscript, "") + "¦" + Try(() => f.FCFSubscript, ""), "proj", Try(() => f.ProjectedToleranceFrame, ""), "ref", Refs(f, viewKeys), "box", Box(f), "nd", NearDims(Box(f)), "ne", NearBox(Box(f)))); } catch (Exception e) { t["fcf_err"] = e.Message; }
        var dat = new List<object>(); t["datum"] = dat;
        try { foreach (S.DatumFrame f in (S.DatumFrames)sh.DatumFrames) dat.Add(O("d", Try(() => f.Datum, ""), "ref", Refs(f, viewKeys), "box", Box(f), "nd", NearDims(Box(f)), "ne", NearBox(Box(f)))); } catch (Exception e) { t["datum_err"] = e.Message; }
        var sfs = new List<object>(); t["sfs"] = sfs;
        try { foreach (S.SurfaceFinishSymbol f in (S.SurfaceFinishSymbols)sh.SurfaceFinishSymbols) sfs.Add(Finish(f, viewKeys)); } catch (Exception e) { t["sfs_err"] = e.Message; }
        // 背景页（模板）上的粗糙度 / 文字：统一粗糙度常放在标题栏旁边
        try {
            D.Sheet bg = sh.Background;
            if (bg != null) {
                sheetElems = null; sheetDims = null;
                var b = new List<object>(); foreach (S.SurfaceFinishSymbol f in (S.SurfaceFinishSymbols)bg.SurfaceFinishSymbols) b.Add(Finish(f, viewKeys)); t["bg_sfs"] = b;
                var tx = new List<string>(); dynamic tbs = bg.TextBoxes; for (int j = 1; j <= (int)tbs.Count && tx.Count < 30; j++) { string s = Try(() => (string)tbs.Item(j).Text, ""); if (s.Length > 0) tx.Add(s.Length > 200 ? s.Substring(0, 200) : s); } t["bg_texts"] = tx;
            }
        } catch (Exception e) { t["bg_err"] = e.Message; }
        // 技术要求：Text 读出来是空的（第一次普查），换几个属性名试
        try { var tx = new List<string>(); dynamic tr = sh.TechnicalRequirements; for (int j = 1; j <= (int)tr.Count; j++) { object it = tr.Item(j); foreach (var nm in new[] { "Text", "TextString", "Content", "Value", "DisplayedText", "Contents" }) { string s = Try(() => Convert.ToString(it.GetType().InvokeMember(nm, System.Reflection.BindingFlags.GetProperty, null, it, null)), ""); if (s.Length > 0) { tx.Add(nm + ":" + (s.Length > 600 ? s.Substring(0, 600) : s)); break; } } } t["tech"] = tx; } catch (Exception e) { t["tech_err"] = e.Message; }
        return t;
    }
    static Dictionary<string,object> Finish(S.SurfaceFinishSymbol f, Dictionary<string,int> viewKeys){
        return O("ra", Try(() => f.RoughnessValue, ""), "min", Try(() => f.MinimumRoughnessValue, ""), "max", Try(() => f.MaximumRoughnessValue, ""), "sym", Try(() => f.SurfaceFinishSymbol.ToString(), ""), "lay", Try(() => f.SurfaceLaySymbol.ToString(), ""),
            "meth", Try(() => f.ProductionMethod, ""), "req", Try(() => f.Requirements, ""), "allow", Try(() => f.MachiningAllowance, ""), "all", Try(() => f.AllAround, false), "ref", Refs(f, viewKeys), "box", Box(f), "nd", NearDims(Box(f)), "ne", NearBox(Box(f)));
    }
}
