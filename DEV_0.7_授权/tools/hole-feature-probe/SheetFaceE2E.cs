using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using TianGongCadSuite;
using A=SolidEdgeAssembly;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;
using F=SolidEdgeFramework;

// 0.10.2：钣金件上点面认参考孔 / 点孔内壁认孔（原来报"只支持普通零件" / "不属于零件文档"）。
// 在装配副本里找钣金件，每个钣金件取内环圆最多的平面，按用户点的方式（装配引用）走 ReadScanFace + ScanFace；再取一张孔内壁圆柱面走 ScanSurface。只读。
// 用法: SheetFaceE2E.exe <输出目录> <装配副本>
class SheetFaceE2E {
    static F.Application app;
    static int fails;
    static void L(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    static void Check(bool ok, string what){ L((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; }
    static T Try<T>(Func<T> f, T d){ try { return f(); } catch { return d; } }
    static void Pump(int n){ for (int i = 0; i < n; i++) { try { app.DoIdle(); } catch { } Application.DoEvents(); Thread.Sleep(50); } }

    static string ParentOfSheet(A.AssemblyDocument a, int depth){
        if (a == null || depth > 10) return null;
        var subs = new List<A.AssemblyDocument>();
        foreach (A.Occurrence o in a.Occurrences) {
            object d = Try<object>(() => o.OccurrenceDocument, null);
            if (d is P.SheetMetalDocument) return Try(() => a.FullName, null);
            var s = d as A.AssemblyDocument; if (s != null) subs.Add(s);
        }
        foreach (var s in subs) { var r = ParentOfSheet(s, depth + 1); if (r != null) return r; }
        return null;
    }

    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        TianGongCadSuite.Licensing.LicenseTestHooks.GateOverride = 0;
        try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); } catch (Exception e) { L("连不上 CAD：" + e.Message); return 2; }
        try { app.DisplayAlerts = false; } catch { }
        A.AssemblyDocument asm = null;
        try {
            asm = (A.AssemblyDocument)app.Documents.Open(args[1]); Pump(20);
            // 钣金件一般在下面几级的子装配里：找到直接装着钣金件的那个子装配，单独打开它来点（= 用户在那个子装配里点）
            string parent = ParentOfSheet(asm, 0);
            L("直接装着钣金件的装配：" + (parent ?? "（没找到）"));
            if (parent != null && !string.Equals(parent, Try(() => asm.FullName, ""), StringComparison.OrdinalIgnoreCase)) {
                var sub = (A.AssemblyDocument)app.Documents.Open(parent); Pump(20);
                try { asm.Close(false); } catch { }
                asm = sub;
            }
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int sheets = 0, faceOk = 0, wallOk = 0;
            foreach (A.Occurrence o in asm.Occurrences) {
                var sm = Try<object>(() => o.OccurrenceDocument, null) as P.SheetMetalDocument;
                if (sm == null || !seen.Add(Try(() => sm.FullName, ""))) continue;
                if (sheets >= 6) break;
                sheets++;
                G.Body body = Try(() => (G.Body)((P.Model)sm.Models.Item(1)).Body, null);
                if (body == null) { L("   " + sm.Name + "：读不到实体"); continue; }
                G.Face best = null, wall = null; int bestN = 0;
                foreach (G.Face f in (G.Faces)body.get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll)) {
                    object g = Try<object>(() => f.Geometry, null);
                    if (g is G.Cylinder && wall == null && Try(() => f.IsParamReversed, false)) wall = f;
                    if (!(g is G.Plane)) continue;
                    int n = 0;
                    foreach (G.Loop lp in (G.Loops)f.Loops) { if (Try(() => lp.IsOuterLoop, true)) continue; foreach (G.Edge e in (G.Edges)lp.Edges) if (Try<object>(() => e.Geometry, null) is G.Circle && Try(() => e.IsClosed, false)) n++; }
                    if (n > bestN) { bestN = n; best = f; }
                }
                if (best == null) { L("   " + sm.Name + "：没有带圆孔的平面"); continue; }
                try {
                    object pick = asm.CreateReference(o, best);
                    bool isSheet; var t = AutoHoleReader.ReadScanFace(pick, out isSheet);
                    string how; int skipped; var holes = HoleScanCad.ScanFace(t, out how, out skipped);
                    L("   " + sm.Name + " 平面 " + best.ID + "：内环整圆 " + bestN + "，认出孔 " + holes.Count + "（判为凸台 " + skipped + "）"
                      + (holes.Count > 0 ? "，如 " + string.Join(" ", holes.Take(4).Select(h => "Φ" + h.DiameterMm.ToString("0.##")).ToArray()) : ""));
                    Check(isSheet, sm.Name + "：按钣金件认（不当打孔面）");
                    if (holes.Count > 0 && holes.Count <= bestN) faceOk++;
                    try { AutoHoleReader.ReadTarget(pick); Check(false, sm.Name + "：当打孔面应该被拦"); }
                    catch (ArgumentException e) { Check(e.Message.Contains("钣金件"), sm.Name + "：当打孔面照样拦住：" + e.Message.Substring(0, Math.Min(30, e.Message.Length))); }
                } catch (Exception e) { Check(false, sm.Name + " 点面认孔异常：" + e.Message); }
                if (wall != null) {
                    try {
                        string label; var h = HoleScanCad.ScanSurface(asm.CreateReference(o, wall), out label);
                        L("   " + sm.Name + " 孔内壁：" + label + " Φ" + h.DiameterMm.ToString("0.##")); wallOk++;
                    } catch (Exception e) { L("   " + sm.Name + " 孔内壁认不出：" + e.Message); }
                }
            }
            L("RESULT 钣金件 " + sheets + "，点面认出孔的 " + faceOk + "，点内壁认出的 " + wallOk);
            Check(sheets > 0 && faceOk > 0, "钣金件上点面能认出参考孔");
            Check(wallOk > 0, "钣金件上点孔内壁能认出孔");
        } catch (Exception e) { L("异常：" + e); fails++; }
        finally { try { if (asm != null) asm.Close(false); } catch { } Pump(4); }
        L("RESULT " + (fails == 0 ? "ALL PASS" : fails + " FAIL"));
        return fails == 0 ? 0 : 1;
    }
}
