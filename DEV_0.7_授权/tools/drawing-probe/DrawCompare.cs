using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using D=SolidEdgeDraft;
using F=SolidEdgeFramework;
using S=SolidEdgeFrameworkSupport;

// 一键出图的完成度基准：人画的工程图 vs 插件出的工程图（同一个零件）。只读。
// 人图里每个尺寸（长度 / 坐标 / 直径 / 半径 / 倒角）的数值，在插件图里能不能找到：
//   * 直接有同值的尺寸；或
//   * 插件图里同一串坐标尺寸（同一条文字线）里两个值之差等于它（人用线性尺寸、插件用坐标尺寸时）。
// 人图的坐标尺寸也换成串内差值比较（两边基准可能不同）：人图同一串里每个相邻差值都算一个"要表达的距离"。
// 0.11.1 另算「公差和粗糙度完成度」（每张图一行 TOL，最后 RESULT 公差完成度），不动原来的数值完成度：
//   人图里每个带公差的尺寸（值 + 公差写法，DrawingTolerance.Normalize 统一：+0.012/0 的 Ø6 也算 H7）、
//   每格形位公差（符号 + 公差值，不管 Ø / 基准字母）、每个基准、每个粗糙度符号（Ra 值），插件图里有没有一样的（按个数配对）。
// 用法：DrawCompare.exe <输出目录> <清单.txt：每行 人图.dft|插件图.dft>
class DrawCompare {
    static F.Application app;
    static void L(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    static T Try<T>(Func<T> f, T d){ try { var v = f(); return v == null ? d : v; } catch { return d; } }

    sealed class Dim { public string Type; public double Value; public double LineKey; public bool Horizontal; public string Text; public double Kp1; public double Pos; public string Run = ""; }
    // 公差项：Kind = 尺寸 / 形位 / 基准 / 粗糙度；Key = 比较用的写法（尺寸再带上值）
    sealed class Tol { public string Kind, Key; public double Value; public override string ToString(){ return Kind + ":" + (Kind == "尺寸" ? Value.ToString("0.##") + " " : "") + Key; } }

    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = Encoding.UTF8;
        app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application");
        try { app.DisplayAlerts = false; } catch { }
        int total = 0, covered = 0, tolTotal = 0, tolCovered = 0, tolDrawings = 0; double tolMean = 0;
        var kindNeed = new Dictionary<string,int>(); var kindHave = new Dictionary<string,int>();
        foreach (var line in File.ReadAllLines(args[1], Encoding.UTF8)) {
            var parts = line.Split('|'); if (parts.Length < 2) continue;
            List<Dim> human, ours; List<double> humanTexts, ourTexts; List<Tol> humanTols, ourTols;
            try { human = Read(parts[0], out humanTexts, out humanTols); ours = Read(parts[1], out ourTexts, out ourTols); } catch (Exception e) { L("ERR " + Path.GetFileName(parts[1]) + " " + e.Message); continue; }
            var need = Needs(human);
            var have = Pool(ours, ourTexts);
            int c = 0; var miss = new List<string>();
            foreach (var n in need) {
                bool ok = have.Any(h => Math.Abs(h - n.Value) < 0.011);
                if (ok) c++; else miss.Add(n.Type.Replace("igDimType", "") + ":" + n.Value.ToString("0.##"));
            }
            total += need.Count; covered += c;
            L((need.Count == 0 ? "N/A " : (c * 100 / Math.Max(1, need.Count)) + "% ") + Path.GetFileNameWithoutExtension(parts[1]) + " 人图要表达 " + need.Count + " 个、插件图能读出 " + c + "（人图尺寸 " + human.Count + "，插件图 " + ours.Count + "）缺：" + string.Join(" ", miss.Take(25)));
            // 公差：按个数配对（人图两个 H7 孔、插件图一个 → 1/2）
            var pool = new List<Tol>(ourTols); int tc = 0; var tmiss = new List<string>();
            foreach (var h in humanTols) {
                var m = pool.FirstOrDefault(o => o.Kind == h.Kind && o.Key == h.Key && (h.Kind != "尺寸" || Math.Abs(o.Value - h.Value) < 0.011));
                if (!kindNeed.ContainsKey(h.Kind)) { kindNeed[h.Kind] = 0; kindHave[h.Kind] = 0; }
                kindNeed[h.Kind]++;
                if (m != null) { pool.Remove(m); tc++; kindHave[h.Kind]++; } else tmiss.Add(h.ToString());
            }
            tolTotal += humanTols.Count; tolCovered += tc;
            if (humanTols.Count > 0) { tolDrawings++; tolMean += (double)tc / humanTols.Count; }
            L("TOL " + (humanTols.Count == 0 ? "N/A " : (tc * 100 / humanTols.Count) + "% ") + Path.GetFileNameWithoutExtension(parts[1]) + " 人图公差项 " + humanTols.Count + "、插件图对上 " + tc + "（插件图公差项 " + ourTols.Count + "：" + string.Join(" ", ourTols.Select(x => x.ToString()).Take(12)) + "）缺：" + string.Join(" ", tmiss.Take(20)));
        }
        L("RESULT 完成度 " + covered + "/" + total + " = " + (total == 0 ? 0 : covered * 1000 / total / 10.0) + "%");
        L("RESULT 公差完成度 " + tolCovered + "/" + tolTotal + " = " + (tolTotal == 0 ? 0 : tolCovered * 1000 / tolTotal / 10.0) + "%；有公差项的图 " + tolDrawings + " 张，每张平均 " + (tolDrawings == 0 ? 0 : Math.Round(tolMean * 100 / tolDrawings, 1)) + "%；分项 " + string.Join(" ", kindNeed.Keys.Select(k => k + " " + kindHave[k] + "/" + kindNeed[k])));
        return 0;
    }

    // 人图里要表达的数值：线性 / 直径 / 半径 / 倒角直接要；
    // 坐标尺寸按串还原成带正负的位置（对称标注 0 在中间：±20 两边都有），再取「到这串最小位置的距离」—— 基准取哪都一样能比
    static List<Dim> Needs(List<Dim> h){
        var r = new List<Dim>();
        foreach (var d in h.Where(x => x.Type != "igDimTypeCoordinate" && x.Type != "igDimTypeAngular" && x.Value > 0.005)) r.Add(d);
        foreach (var g in h.Where(x => x.Type == "igDimTypeCoordinate").GroupBy(x => x.Run)) {
            var pos = Positions(g.ToList());
            // 基准本身（值 0）在串中间时只是个参照（对称标注的中心线），不是要加工的要素，不算要表达的位置
            var feats = g.Where(x => x.Value > 0.005).Select(x => (x.Kp1 >= g.OrderBy(y => y.Value).First().Kp1 - 1e-7 ? 1 : -1) * x.Value).ToList();
            if (pos.Count(x => Math.Abs(x) < 0.005) > 0 && feats.Count > 0 && feats.Min() < 0 && feats.Max() > 0) pos = feats;
            double min = pos.Min();
            foreach (var p in pos.Select(x => x - min).Where(x => x > 0.005).Distinct()) r.Add(new Dim { Type = "igDimTypeCoordinate", Value = Math.Round(p, 3) });
        }
        return r;
    }
    // 一串坐标尺寸的位置：值是到基准的距离（不带符号），符号看落点在基准哪一边（关键点 1 的图纸坐标）
    static List<double> Positions(List<Dim> run){
        var origin = run.OrderBy(x => x.Value).First();
        return run.Select(x => (x.Kp1 >= origin.Kp1 - 1e-7 ? 1 : -1) * x.Value).ToList();
    }
    // 插件图能读出的数值：所有尺寸值、标注文字里的数（孔标注的沉孔 / 深度、倒角 C2、半径），每串坐标尺寸任意两点之间的距离
    static List<double> Pool(List<Dim> o, List<double> texts){
        var r = new List<double>(texts);
        foreach (var d in o) r.Add(d.Value);
        foreach (var g in o.Where(x => x.Type == "igDimTypeCoordinate").GroupBy(x => x.Run)) {
            var vals = Positions(g.ToList()).Distinct().ToList();
            for (int i = 0; i < vals.Count; i++) for (int j = i + 1; j < vals.Count; j++) r.Add(Math.Abs(vals[i] - vals[j]));
        }
        return r;
    }

    static readonly System.Text.RegularExpressions.Regex Num = new System.Text.RegularExpressions.Regex(@"(\d+(\.\d+)?)");
    static void Numbers(string t, List<double> into){
        if (string.IsNullOrEmpty(t)) return;
        // 数量（「4×」「4-R5」「4 x」）不是尺寸，先去掉
        t = System.Text.RegularExpressions.Regex.Replace(t, @"\d+\s*(×|x|X)\s*", " ");
        t = System.Text.RegularExpressions.Regex.Replace(t, @"(^|\s)\d+-(?=[RCM%])", " ");
        foreach (System.Text.RegularExpressions.Match m in Num.Matches(t)) { double v; if (double.TryParse(m.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)) into.Add(v); }
    }

    static List<Dim> Read(string file, out List<double> texts, out List<Tol> tols){
        var list = new List<Dim>(); texts = new List<double>(); tols = new List<Tol>();
        D.DraftDocument d = (D.DraftDocument)app.Documents.Open(file);
        try {
            for (int k = 0; k < 6; k++) { try { app.DoIdle(); } catch { } Thread.Sleep(20); }
            foreach (D.Sheet sh in d.Sheets) {
                if (Try(() => sh.SectionType.ToString(), "") != "igWorkingSection") continue;
                foreach (S.Dimension dm in (S.Dimensions)sh.Dimensions) {
                    var x = new Dim { Type = Try(() => dm.DimensionType.ToString(), "?") };
                    x.Value = Math.Round(Try(() => dm.Value, 0.0) * (Try(() => dm.UnitsType, 1) == 1 ? 1000 : 57.29578), 3);
                    x.Text = Try(() => dm.OverrideString, "");
                    Numbers(Try(() => dm.PrefixString, "") + " " + Try(() => dm.SuffixString, "") + " " + Try(() => dm.SubfixString, "") + " " + x.Text, texts);
                    if (x.Type == "igDimTypeCoordinate") {
                        // 关键点 2 = 文字线上的点：横向串 y 相同，竖向串 x 相同（探针 2/3）；关键点 1 = 落点
                        try { double a, b, c; F.KeyPointType kt; int hh; dm.GetKeyPoint(2, out a, out b, out c, out kt, out hh); double p, q, r2; dm.GetKeyPoint(1, out p, out q, out r2, out kt, out hh);
                            x.Horizontal = Math.Abs(a - p) < Math.Abs(b - q); x.LineKey = (x.Horizontal ? b : a) * 1000; x.Kp1 = x.Horizontal ? p : q;
                            x.Run = (x.Horizontal ? "H" : "V") + Math.Round(x.LineKey, 1); } catch { }
                    }
                    list.Add(x);
                    string tt = Try(() => dm.DisplayType.ToString(), "");
                    if (tt != "" && tt != "igDimDisplayTypeNominal" && tt != "igDimDisplayTypeBlank" && tt != "igDimDisplayTypeHoleCallout" && tt != "igDimDisplayTypeReference" && tt != "igDimDisplayTypeBasic") {
                        string key = TianGongCadSuite.DrawingTolerance.Normalize(x.Value, Try(() => dm.HoleClassString, ""), Try(() => dm.ShaftClassString, ""), Try(() => dm.PrimaryUpperTolerance, ""), Try(() => dm.PrimaryLowerTolerance, ""));
                        if (key.Length > 0) tols.Add(new Tol { Kind = "尺寸", Key = key, Value = x.Value });
                    }
                }
                try { foreach (S.FeatureControlFrame f in (S.FeatureControlFrames)sh.FeatureControlFrames) foreach (var fr in new[] { Try(() => f.PrimaryFrame, ""), Try(() => f.SecondaryFrame, ""), Try(() => f.TertiaryFrame, ""), Try(() => f.QuaternaryFrame, "") }) { string k = TianGongCadSuite.DrawingTolerance.FrameKey(fr); if (k.Length > 0) tols.Add(new Tol { Kind = "形位", Key = k }); } } catch { }
                try { foreach (S.DatumFrame f in (S.DatumFrames)sh.DatumFrames) if (Try(() => f.Datum, "").Trim().Length > 0) tols.Add(new Tol { Kind = "基准", Key = "基准" }); } catch { }
                try { foreach (S.SurfaceFinishSymbol f in (S.SurfaceFinishSymbols)sh.SurfaceFinishSymbols) { string ra = Try(() => f.RoughnessValue, "").Trim(); if (ra.Length == 0) ra = Try(() => f.MinimumRoughnessValue, "").Trim(); if (ra.Length > 0) tols.Add(new Tol { Kind = "粗糙度", Key = "Ra" + ra }); } } catch { }
                try { var bs = (S.Balloons)sh.Balloons; for (int i = 1; i <= bs.Count; i++) { string t = Try(() => bs.Item(i).BalloonDisplayedText, ""); if (!t.Contains("刻字")) Numbers(t, texts); } } catch { }
            }
        } finally { try { d.Close(false); } catch { } }
        return list;
    }
}
