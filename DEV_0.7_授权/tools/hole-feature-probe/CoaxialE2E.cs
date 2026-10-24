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

// 0.8.5：现场截图 —— 顶面要打的孔正下方、底面已经有一个同轴盲孔，插件说"这个位置已经有孔、已跳过"。
// 新建 100×100×40 的板（只落在输出目录），走插件真实接口 AutoHoleWriter.DrillRequests：
//   ① 底面打两个盲孔（Φ5 深 8 @30,50；Φ6.6 深 8 @70,50）和一个通孔（Φ6.6 @50,20）
//   ② 顶面 @30,50 通孔、@70,50 盲孔 → 都要真的打上（老版本：本来就有孔、跳过）
//   ③ 顶面 @30,50 再打一次 → 本来就有孔；顶面 @50,20（底面打的通孔）→ 本来就有孔
//   ④ 顶面一批 3 个（其中一个和底面盲孔同轴）→ 合成一个多孔特征
// 用法: CoaxialE2E.exe <输出目录>
class CoaxialE2E {
    static F.Application app;
    static int fail = 0;
    static object M = Type.Missing;
    const string Tpl = @"C:\Program Files\NDS\TianGong 2025\Template\ISO Metric\";
    static void L(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    static void Check(bool ok, string label){ L((ok ? "PASS: " : "FAIL: ") + label); if (!ok) fail++; }
    static void Pump(int n){ for (int i = 0; i < n; i++) { try { app.DoIdle(); } catch { } try { Application.DoEvents(); } catch { } Thread.Sleep(60); } }
    static double D(Array a,int i){ return Convert.ToDouble(a.GetValue(i)); }
    static string Hr(Exception e){ return e.GetType().Name + " 0x" + e.HResult.ToString("X8") + " " + e.Message; }
    static T Try<T>(Func<T> f, T dflt){ try { return f(); } catch { return dflt; } }
    static string Res(DrillResult r){
        return "建了 " + r.Created + "/" + r.Requested + "，本来就有 " + r.AlreadyOk + "，多孔特征 " + r.Combined + "，方式 " + r.Method
             + (r.Failures.Count > 0 ? "，失败：" + string.Join("；", r.Failures) : "")
             + " ⏎ " + string.Join(" ⏎ ", r.Outcomes.Select(o => o.Text).ToArray());
    }
    static P.RefPlane FindXY(P.PartDocument part){
        foreach (P.RefPlane c in part.RefPlanes) {
            Array n = new double[3], p = new double[3];
            c.GetNormal(ref n); c.GetRootPoint(ref p);
            if (Math.Abs(D(n,2)-1) < 1e-8 && Math.Abs(D(p,2)) < 1e-8) return c;
        }
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
    static int Faces(P.Model m){ return Try(() => ((G.Faces)((G.Body)m.Body).get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll)).Count, -1); }
    // (x,y,z) 上有没有半径 r 的圆边
    static bool HasCircle(P.Model m, double x, double y, double z, double r){
        foreach (G.Edge e in (G.Edges)((G.Body)m.Body).get_Edges(G.FeatureTopologyQueryTypeConstants.igQueryAll)) {
            var c = e.Geometry as G.Circle; if (c == null) continue;
            Array cc = new double[3], ax = new double[3]; double rr; c.GetCircleData(ref cc, ref ax, out rr);
            if (Math.Abs(D(cc,0) - x) < 1e-6 && Math.Abs(D(cc,1) - y) < 1e-6 && Math.Abs(D(cc,2) - z) < 1e-6 && Math.Abs(rr - r) < 2e-5) return true;
        }
        return false;
    }
    static AutoHoleWriter.HoleRequest Req(double x, double y, double z, double dia, double depth, string src){
        return new AutoHoleWriter.HoleRequest { Spec = new HoleSpec { Kind = HoleKind.Through, HoleDiameter = dia, Depth = depth }, Centre = new V3(x, y, z), Source = src };
    }

    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        TianGongCadSuite.Licensing.LicenseTestHooks.GateOverride = 0;
        E2EFilterCx.Register();
        string outDir = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(outDir);
        try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); }
        catch (Exception e) { L("连不上 CAD：" + e.Message); return 2; }
        try { app.DisplayAlerts = false; } catch { }
        L("已连上 CAD " + app.Version + "　插件 DLL " + typeof(AutoHoleWriter).Assembly.GetName().Version);
        P.PartDocument part = null;
        try {
            part = (P.PartDocument)app.Documents.Add("SolidEdge.PartDocument", Tpl + "iso metric part.par");
            part.ModelingMode = P.ModelingModeConstants.seModelingModeOrdered;
            Pump(10);
            var m = Plate(part, 0.1, 0.1, 0.04);
            Pump(4);
            const double T = 0.04;

            // ① 底面
            var bottom = AutoHoleReader.ReadTarget(FaceAtZ(m, 0));
            var r1 = AutoHoleWriter.DrillRequests(bottom, new[]{ Req(0.03, 0.05, 0, 5, 8, "底盲孔A"), Req(0.07, 0.05, 0, 6.6, 8, "底盲孔B"), Req(0.05, 0.02, 0, 6.6, 0, "底通孔") }); Pump(4);
            L("① " + Res(r1));
            Check(r1.Created == 3 && r1.Failures.Count == 0, "① 底面两个盲孔 + 一个通孔打上");
            Check(HasCircle(m, 0.03, 0.05, 0, 0.0025) && !HasCircle(m, 0.03, 0.05, T, 0.0025), "① 盲孔A 孔口在底面、顶面没有");

            // ② 顶面同轴（用户截图那种）
            int f0 = Faces(m);
            var top = AutoHoleReader.ReadTarget(FaceAtZ(m, T));
            var r2 = AutoHoleWriter.DrillRequests(top, new[]{ Req(0.03, 0.05, T, 6.6, 0, "顶通孔@A"), Req(0.07, 0.05, T, 4.2, 12, "顶盲孔@B") }); Pump(4);
            L("② " + Res(r2) + "　面 " + f0 + " → " + Faces(m));
            Check(r2.AlreadyOk == 0, "② 反面的同轴盲孔不算「这个位置已经有孔」");
            Check(r2.Created == 2 && r2.Failures.Count == 0, "② 两个都打上（CAD 不拒绝同轴孔）");
            Check(HasCircle(m, 0.03, 0.05, T, 0.0033) && HasCircle(m, 0.07, 0.05, T, 0.0021), "② 顶面上有两个新孔口");

            // ③ 真的已经有孔
            var top3 = AutoHoleReader.ReadTarget(FaceAtZ(m, T));
            var r3 = AutoHoleWriter.DrillRequests(top3, new[]{ Req(0.03, 0.05, T, 6.6, 0, "再打@A"), Req(0.05, 0.02, T, 6.6, 0, "底通孔上") }); Pump(4);
            L("③ " + Res(r3));
            Check(r3.AlreadyOk == 2 && r3.Created == 0 && r3.Failures.Count == 0, "③ 顶面已有孔口的、从底面打穿的通孔：都算本来就有孔");

            // ④ 一批 3 个，其中一个对着底面盲孔
            var bottom4 = AutoHoleReader.ReadTarget(FaceAtZ(m, 0));
            var r4a = AutoHoleWriter.DrillRequests(bottom4, new[]{ Req(0.05, 0.08, 0, 5, 6, "底盲孔C") }); Pump(4);
            var top4 = AutoHoleReader.ReadTarget(FaceAtZ(m, T));
            var r4 = AutoHoleWriter.DrillRequests(top4, new[]{ Req(0.03, 0.08, T, 6.6, 0, "批1"), Req(0.05, 0.08, T, 6.6, 0, "批2@C"), Req(0.07, 0.08, T, 6.6, 0, "批3") }); Pump(4);
            L("④ " + Res(r4a) + " ⏎ " + Res(r4));
            Check(r4.Created == 3 && r4.Failures.Count == 0 && r4.Combined == 1, "④ 一批 3 个合成一个多孔特征（含一个和底面盲孔同轴）");
            try { part.SaveAs(Path.Combine(outDir, "coaxial_" + DateTime.Now.ToString("HHmmss") + ".par")); } catch (Exception e) { L("保存失败：" + Hr(e)); }
        } catch (Exception e) {
            L("E2E 异常：" + Hr(e) + "\r\n" + e.StackTrace); fail++;
        } finally {
            try { if (part != null) part.Close(false); } catch { }
            Pump(6);
        }
        L(fail == 0 ? "COAXIAL E2E OK" : ("COAXIAL E2E FAILED " + fail));
        return fail == 0 ? 0 : 1;
    }
}

[ComImport, Guid("00000016-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IE2EMessageFilterCx {
    [PreserveSig] int HandleInComingCall(int type, IntPtr task, int ticks, IntPtr info);
    [PreserveSig] int RetryRejectedCall(IntPtr task, int ticks, int reject);
    [PreserveSig] int MessagePending(IntPtr task, int ticks, int pending);
}
sealed class E2EFilterCx : IE2EMessageFilterCx {
    [DllImport("ole32.dll")] static extern int CoRegisterMessageFilter(IE2EMessageFilterCx f, out IE2EMessageFilterCx old);
    static E2EFilterCx keep;
    public static void Register(){ keep = new E2EFilterCx(); IE2EMessageFilterCx old; CoRegisterMessageFilter(keep, out old); }
    public int HandleInComingCall(int a, IntPtr b, int c, IntPtr d){ return 0; }
    public int RetryRejectedCall(IntPtr a, int ticks, int reject){ return ticks < 120000 ? 200 : -1; }
    public int MessagePending(IntPtr a, int b, int c){ return 2; }
}
