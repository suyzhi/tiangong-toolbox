using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using TianGongCadSuite;
using F=SolidEdgeFramework;

// 公差普查的三维对照：工程图对应的零件（副本）逐个打开，用一键出图自己的 DrawingModelRead.Holes 读出全部孔，
// 一行 JSON：图号 + 每个孔（直径、贯通 / 盲孔深度、沉孔、孔特征类型、螺纹规格、轴向）。只读，Close(false)。
// 用法：ModelHoleSurvey.exe <输出目录> <清单.tsv：图号\t零件副本>
class ModelHoleSurvey {
    static T Try<T>(Func<T> f, T d){ try { var v = f(); return v == null ? d : v; } catch { return d; } }
    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = Encoding.UTF8;
        var app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application");
        try { app.DisplayAlerts = false; } catch { }
        var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        var w = new StreamWriter(Path.Combine(args[0], "holes.jsonl"), true, new UTF8Encoding(false));
        var lines = File.ReadAllLines(args[1], Encoding.UTF8).Where(x => x.Contains("\t")).ToArray();
        for (int i = 0; i < lines.Length; i++) {
            var p = lines[i].Split('\t'); var row = new Dictionary<string,object> { { "k", p[0].Trim('﻿') }, { "file", p[1] } };
            var t0 = DateTime.Now; object doc = null;
            try {
                doc = app.Documents.Open(p[1]);
                for (int k = 0; k < 6; k++) { try { app.DoIdle(); } catch { } Thread.Sleep(20); }
                var holes = DrawingModelRead.Holes(doc);
                row["holes"] = holes.Select(h => new Dictionary<string,object> {
                    { "d", Math.Round(h.Radius * 2000, 3) }, { "thr", h.Through }, { "dep", Math.Round(h.Depth * 1000, 2) }, { "cb", Math.Round(h.CbRadius * 2000, 2) },
                    { "kind", h.Spec == null ? "" : h.Spec.Kind.ToString() }, { "m", h.Spec == null ? "" : h.Spec.ThreadSize },
                    { "ax", new[] { Math.Round(h.Axis.X, 3), Math.Round(h.Axis.Y, 3), Math.Round(h.Axis.Z, 3) } }, { "seg", h.Segments.Count },
                    // 孔口朝向（+ = 沿 ax 那头开口，- = 反方向那头开口；通孔两个都有）和孔轴上一点（毫米）：对照人图里有没有正对孔口的视图
                    { "mo", (h.BlindHi ? "" : "+") + (h.BlindLo ? "" : "-") },
                    { "pt", new[] { Math.Round(h.Point.X * 1000, 2), Math.Round(h.Point.Y * 1000, 2), Math.Round(h.Point.Z * 1000, 2) } } }).ToList();
            } catch (Exception e) { row["error"] = e.Message; }
            finally { try { if (doc != null) ((dynamic)doc).Close(false); } catch { } }
            row["sec"] = Math.Round((DateTime.Now - t0).TotalSeconds, 1);
            w.WriteLine(js.Serialize(row)); w.Flush();
            Console.WriteLine("[" + i + "/" + lines.Length + "] " + row["sec"] + "s " + (row.ContainsKey("error") ? "ERR " + row["error"] : "") + " " + p[0]); Console.Out.Flush();
        }
        w.Close();
        Console.WriteLine("RESULT done " + lines.Length);
        return 0;
    }
}
