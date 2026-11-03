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
        }
    }
}
