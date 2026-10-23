using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using TianGongCadSuite;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;
using F=SolidEdgeFramework;
using S=SolidEdgeFrameworkSupport;

// 0.8.4 探针：「改孔型」改多孔孔特征（M6 螺纹孔 ×4 → M8 沉孔）再「撤销修改」，NativeLinkE2E ⑦ 实测有一个孔停在 Φ9（0.8.3 一样）。
// 新建零件、插件真实接口：打 4 个 M6 螺纹孔（合成一个多孔特征）→ 改 M8 沉孔 → 撤销，逐个输入孔回读 HoleData、量孔口直径；再试补救。
// 用法: EditUndoProbe.exe <输出目录>
class EditUndoProbe {
    static F.Application app;
    static object M = Type.Missing;
    const string Tpl = @"C:\Program Files\NDS\TianGong 2025\Template\ISO Metric\";
    static void L(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    static void Pump(int n){ for (int i = 0; i < n; i++) { try { app.DoIdle(); } catch { } try { Application.DoEvents(); } catch { } Thread.Sleep(60); } }
    static string N(double v){ return v.ToString("0.###", CultureInfo.InvariantCulture); }
    static double D(Array a,int i){ return Convert.ToDouble(a.GetValue(i)); }
    static P.RefPlane FindXY(P.PartDocument part){
        foreach (P.RefPlane c in part.RefPlanes) { Array n = new double[3], p = new double[3]; c.GetNormal(ref n); c.GetRootPoint(ref p); if (Math.Abs(D(n,2)-1) < 1e-8 && Math.Abs(D(p,2)) < 1e-8) return c; }
        return null;
    }
    static P.Model Plate(P.PartDocument part, double w, double h, double t){
        var prof = part.ProfileSets.Add().Profiles.Add(FindXY(part));
        var Ls = new S.Line2d[4];
        Ls[0] = prof.Lines2d.AddBy2Points(0, 0, w, 0); Ls[1] = prof.Lines2d.AddBy2Points(w, 0, w, h);
        Ls[2] = prof.Lines2d.AddBy2Points(w, h, 0, h); Ls[3] = prof.Lines2d.AddBy2Points(0, h, 0, 0);
        var rel = (S.Relations2d)prof.Relations2d;
        for (int i = 0; i < 4; i++) rel.AddKeypoint(Ls[i], (int)SolidEdgeConstants.KeypointIndexConstants.igLineEnd, Ls[(i+1)%4], (int)SolidEdgeConstants.KeypointIndexConstants.igLineStart);
        prof.End(P.ProfileValidationType.igProfileClosed);
        Array arr = new object[]{ prof };
        return part.Models.AddFiniteExtrudedProtrusion(1, ref arr, P.FeaturePropertyConstants.igRight, t);
    }
    static G.Face FaceAtZ(P.Model m, double z){
        foreach (G.Face f in (G.Faces)((G.Body)m.Body).get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll)) {
            var pl = f.Geometry as G.Plane; if (pl == null) continue;
            Array p = new double[3], n = new double[3]; pl.GetPlaneData(ref p, ref n);
            if (Math.Abs(Math.Abs(D(n,2)) - 1) < 1e-6 && Math.Abs(D(p,2) - z) < 1e-9) return f;
        }
        return null;
    }
    static double DiaAt(P.Model m, double z, double x, double y){
        double best = 0;
        foreach (G.Edge e in (G.Edges)((G.Body)m.Body).get_Edges(G.FeatureTopologyQueryTypeConstants.igQueryAll)) {
            var c = e.Geometry as G.Circle; if (c == null) continue;
            Array cc = new double[3], ax = new double[3]; double r; c.GetCircleData(ref cc, ref ax, out r);
            if (Math.Abs(D(cc,0) - x) < 1e-6 && Math.Abs(D(cc,1) - y) < 1e-6 && Math.Abs(D(cc,2) - z) < 1e-7) best = Math.Max(best, r * 2000);
        }
        return best;
    }
    static List<P.Hole> In(P.UserDefinedPattern u){ Array a = new object[0]; u.GetInputFeatures(ref a); return a.Cast<object>().OfType<P.Hole>().ToList(); }
    static void Swap(P.PartDocument part, P.Model m, P.Hole h, HoleSpec spec){
        typeof(HoleEditCad).GetMethod("SwapData", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public).Invoke(null, new object[]{ part, m, h, spec });
    }
    static readonly double[][] Pts = { new[]{0.02,0.02}, new[]{0.08,0.02}, new[]{0.08,0.08}, new[]{0.02,0.08} };
    static string Dias(P.Model m){ return string.Join(" ", Pts.Select(c => N(DiaAt(m, 0.01, c[0], c[1]))).ToArray()); }
    static string Inputs(P.UserDefinedPattern u){
        var l = new List<string>();
        foreach (var h in In(u)) {
            try {
                var d = (P.HoleData)h.HoleData; object st; var s = h.GetStatusEx(out st);
                double x2, y2, x, y, z; var prof = (P.Profile)h.Profile; prof.Holes2d.Item(1).GetCenterPoint(out x2, out y2); prof.Convert2DCoordinate(x2, y2, out x, out y, out z);
                l.Add("(" + N(x*1000) + "," + N(y*1000) + ") " + d.HoleType + " Φ" + N(d.HoleDiameter*1000) + " cb" + N(d.CounterboreDiameter*1000) + " " + s + " data#" + d.GetHashCode());
            } catch (Exception e) { l.Add("读不到:" + e.Message); }
        }
        return string.Join(" | ", l.ToArray());
    }

    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        TianGongCadSuite.Licensing.LicenseTestHooks.GateOverride = 0;
        try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); } catch (Exception e) { L("连不上 CAD：" + e.Message); return 2; }
        try { app.DisplayAlerts = false; } catch { }
        foreach (var kind in new[]{ "tapped", "through" }) {
            try { Run(kind); } catch (Exception e) { L(kind + " 异常 " + e.GetType().Name + " 0x" + e.HResult.ToString("X8") + " " + e.Message + "\r\n" + e.StackTrace); }
        }
        L("PROBE DONE");
        return 0;
    }

    static void Run(string kind){
        L(""); L("== 原规格 " + kind);
        var part = (P.PartDocument)app.Documents.Add("SolidEdge.PartDocument", Tpl + "iso metric part.par");
        part.ModelingMode = P.ModelingModeConstants.seModelingModeOrdered;
        var m = Plate(part, 0.1, 0.1, 0.01);
        Pump(4);
        var t = AutoHoleReader.ReadTarget(FaceAtZ(m, 0.01));
        var spec0 = kind == "tapped" ? new HoleSpec { Kind = HoleKind.Tapped, ThreadSize = "M6", HoleDiameter = 4.917, ThreadFull = true }
                                     : new HoleSpec { Kind = HoleKind.Through, HoleDiameter = 6.6 };
        var reqs = Pts.Select(c => new AutoHoleWriter.HoleRequest { Spec = spec0, Centre = new V3(c[0], c[1], 0.01), Source = "p" }).ToList();
        var r = AutoHoleWriter.DrillRequests(t, reqs);
        L("打孔：" + r.Created + " 个，多孔 " + r.Combined + "　孔口 " + Dias(m));
        var u = m.UserDefinedPatterns.Item(1);
        L("   输入孔：" + Inputs(u));
        var notes = new List<string>();
        var targets = HoleEditCad.FromCreated(new List<KeyValuePair<P.PartDocument, string>>{ new KeyValuePair<P.PartDocument, string>(part, u.SystemName) }, notes);
        var spec1 = new HoleSpec { Kind = HoleKind.Counterbore, ThreadSize = "M8", HoleDiameter = 9, CounterboreDiameter = 15, CounterboreDepth = 9 };
        var o = HoleEditCad.Apply(targets, spec1, true);
        L("改 M8 沉孔：改了 " + o.Changed + "　孔口 " + Dias(m));
        L("   输入孔：" + Inputs(u));
        var back = o.Undo.Run();
        L("撤销：" + back.Summary + "　孔口 " + Dias(m));
        L("   输入孔：" + Inputs(u));
        try { part.Close(false); } catch { }
    }
}
