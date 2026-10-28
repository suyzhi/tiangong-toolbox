using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using TianGongCadSuite;
using A=SolidEdgeAssembly;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;
using F=SolidEdgeFramework;

// 0.10.2：钣金件上的孔当参考孔，「保持关联」能不能挂上天工原生关联（零件间复制钣金件的面 + 找到对得上的圆边）。
// 原来钣金件的参考孔不记所属文档，一律"读不到参考孔所在零件在装配里的位置"。
// 只走关联准备（Session.Begin），不打孔；End 时没用上的零件间复制自动删掉；装配不保存。
// 用法: SheetLinkE2E.exe <输出目录> <装配副本>（钣金件和普通零件都在顶层）
class SheetLinkE2E {
    static F.Application app;
    static int fails;
    static void L(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    static void Check(bool ok, string what){ L((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; }
    static T Try<T>(Func<T> f, T d){ try { return f(); } catch { return d; } }
    static void Pump(int n){ for (int i = 0; i < n; i++) { try { app.DoIdle(); } catch { } Application.DoEvents(); Thread.Sleep(50); } }

    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        TianGongCadSuite.Licensing.LicenseTestHooks.GateOverride = 0;
        try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); } catch (Exception e) { L("连不上 CAD：" + e.Message); return 2; }
        try { app.DisplayAlerts = false; } catch { }
        A.AssemblyDocument asm = null;
        try {
            asm = (A.AssemblyDocument)app.Documents.Open(args[1]); Pump(20);
            // 参考：第一个带圆孔平面的钣金件
            ScannedHole refHole = null; string refName = "";
            foreach (A.Occurrence o in asm.Occurrences) {
                var sm = Try<object>(() => o.OccurrenceDocument, null) as P.SheetMetalDocument;
                if (sm == null) continue;
                var body = Try(() => (G.Body)((P.Model)sm.Models.Item(1)).Body, null);
                if (body == null) continue;
                foreach (G.Face f in (G.Faces)body.get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll)) {
                    if (!(Try<object>(() => f.Geometry, null) is G.Plane)) continue;
                    try {
                        bool sheet; var t = AutoHoleReader.ReadScanFace(asm.CreateReference(o, f), out sheet);
                        string how; int sk; var holes = HoleScanCad.ScanFace(t, out how, out sk);
                        if (holes.Count > 0) { refHole = holes[0]; refName = o.Name; break; }
                    } catch { }
                }
                if (refHole != null) break;
            }
            Check(refHole != null, "找到钣金件上的参考孔：" + refName + (refHole != null ? " Φ" + refHole.DiameterMm.ToString("0.##") : ""));
            if (refHole == null) return 1;
            Check(refHole.Owner is P.SheetMetalDocument, "参考孔记下了所属钣金件文档");
            // 打孔面：第一个普通零件上法向和参考孔轴线平行的平面
            TargetFace target = null;
            foreach (A.Occurrence o in asm.Occurrences) {
                var part = Try<object>(() => o.OccurrenceDocument, null) as P.PartDocument;
                if (part == null) continue;
                var body = Try(() => (G.Body)((P.Model)part.Models.Item(1)).Body, null);
                if (body == null) continue;
                foreach (G.Face f in (G.Faces)body.get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll)) {
                    if (!(Try<object>(() => f.Geometry, null) is G.Plane)) continue;
                    try {
                        var t = AutoHoleReader.ReadTarget(asm.CreateReference(o, f));
                        if (Math.Abs(Math.Abs(t.Plane.Normal.Unit().Dot(refHole.Axis.Unit())) - 1) < 1e-6) { target = t; break; }
                    } catch { }
                }
                if (target != null) break;
            }
            Check(target != null, "找到打孔面：" + (target != null ? target.PartName + " " + target.Label : ""));
            if (target == null) return 1;
            var n = target.Plane.Normal.Unit();
            var c = refHole.Center - n * (refHole.Center - target.Plane.Point).Dot(n);   // 参考孔轴线和打孔面的交点
            var req = new AutoHoleWriter.HoleRequest { Spec = HoleMatcher.Match(refHole.DiameterMm).Target, Centre = c, From = AutoHoleReader.ToReference(refHole), Source = "E2E" };
            var reqs = new List<AutoHoleWriter.HoleRequest> { req };
            var sess = new NativeHoleLink.Session(app, asm);
            var t0 = DateTime.Now;
            var ip = sess.Begin(target, reqs, null);
            L("   关联准备 " + (DateTime.Now - t0).TotalSeconds.ToString("0.0") + "s：Anchor=" + (req.Anchor != null) + " LinkWhy=" + req.LinkWhy + " 说明=" + string.Join("；", sess.Notes.ToArray()));
            Check(ip != null && req.Anchor != null, "钣金件的参考孔挂得上天工关联（零件间复制 + 圆边）");
            Check(!req.LinkWhy.Contains("读不到参考孔所在零件"), "不再报「读不到参考孔所在零件在装配里的位置」");
            sess.End(ip, reqs, null, null);
            Pump(10);
            object active = Try<object>(() => app.ActiveDocument, null);
            Check(ReferenceEquals(active, asm) || Try(() => ((dynamic)active).FullName == asm.FullName, false), "退回了顶层装配");
        } catch (Exception e) { L("异常：" + e); fails++; }
        finally { try { if (asm != null) asm.Close(false); } catch { } Pump(4); }
        L("RESULT " + (fails == 0 ? "ALL PASS" : fails + " FAIL"));
        return fails == 0 ? 0 : 1;
    }
}
