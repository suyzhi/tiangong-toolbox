using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using TianGongCadSuite;
using A=SolidEdgeAssembly;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;
using F=SolidEdgeFramework;
using S=SolidEdgeFrameworkSupport;

// 造一份"真机鼠标实测"用的夹具装配（不写用户文件，只写 artifacts）：
//   ScanA.par：100×100×20 板，顶面有 5 个孔（2 个圆柱沉孔 Φ11/Φ6.6、1 个锥形沉孔 Φ11 90°/Φ5、
//              1 个孔口倒角 0.5×45° 的 Φ6.6 通孔、1 个螺纹孔 M6）+ 1 个 Φ20 高 5 的圆凸台；
//   ScanB.par：200×200×20 板，放在 A 下面（顶面 z=-20mm，四周比 A 大 50mm，方便点它的顶面）；
//   ScanFixture.asm：两者插进装配。
// 用法: MakeScanFixture.exe <输出目录>
class MakeScanFixture {
    static F.Application app;
    static object M = Type.Missing;
    const string CadHome = @"C:\Program Files\NDS\TianGong 2025";
    static string Template { get { return Path.Combine(CadHome, "Template", "ISO Metric", "iso metric part.par"); } }

    static void L(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    static string N(double v){ return v.ToString("0.####", CultureInfo.InvariantCulture); }
    static string Try(Func<string> f){ try { return f(); } catch (Exception e) { return "<" + e.GetType().Name + ": " + Safe(e.Message) + ">"; } }
    static string Safe(string s){ try { return s == null ? "" : s; } catch { return "<读不出>"; } }
    static void Pump(int n){ for (int i = 0; i < n; i++) { try { app.DoIdle(); } catch {} Thread.Sleep(60); } }
    static double D(Array a,int i){ return Convert.ToDouble(a.GetValue(i)); }

    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        // 开发构建的授权闸默认是关着的（本机没激活），造夹具要真写模型，这里放行。
        TianGongCadSuite.Licensing.LicenseTestHooks.GateOverride = 0;
        string dir = Path.GetFullPath(args.Length > 0 ? args[0] : "facescan-fixture");
        Directory.CreateDirectory(dir);
        try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); }
        catch (Exception e) { L("连不上 CAD：" + e.Message); return 2; }
        L("已连上 CAD " + Try(() => app.Version));
        Pump(6);
        L("CAD 活动文档 = " + Try(() => ((dynamic)app.ActiveDocument).FullName));

        P.PartDocument a = null, b = null; A.AssemblyDocument asm = null; object original = null;
        try { original = app.ActiveDocument; } catch {}
        try {
            // ---- A 板 ----
            P.Model ma;
            a = BuildPlate(app, 0.1, 0.1, 0.02, out ma);
            DrillAt(a, ma, 0.020, 0.020, new HoleSpec{ Kind=HoleKind.Counterbore, HoleDiameter=6.6, CounterboreDiameter=11.0, CounterboreDepth=6.0 }, "圆柱沉孔 #1");
            DrillAt(a, ma, 0.080, 0.020, new HoleSpec{ Kind=HoleKind.Counterbore, HoleDiameter=6.6, CounterboreDiameter=11.0, CounterboreDepth=6.0 }, "圆柱沉孔 #2");
            DrillAt(a, ma, 0.050, 0.020, new HoleSpec{ Kind=HoleKind.Countersink, HoleDiameter=5.0, CountersinkDiameter=11.0, CountersinkAngle=90 }, "锥形沉孔");
            DrillAt(a, ma, 0.020, 0.050, new HoleSpec{ Kind=HoleKind.Through, HoleDiameter=6.6, Chamfer=true, ChamferSetback=0.5, ChamferAngle=45 }, "孔口倒角 Φ6.6");
            DrillAt(a, ma, 0.050, 0.050, new HoleSpec{ Kind=HoleKind.Tapped, ThreadSize="M6", HoleDiameter=4.917 }, "螺纹孔 M6");
            // 圆凸台 Φ20 高 5（底圆长在顶面内环上 —— 不能被当成孔）
            var xy = FindXY(a);
            var rp = a.RefPlanes.AddParallelByDistance(xy, 0.010, P.ReferenceElementConstants.igNormalSide, M, M, M, M);
            var bossProfile = a.ProfileSets.Add().Profiles.Add(rp);
            double bx, by; bossProfile.Convert3DCoordinate(0.080, 0.080, 0.010, out bx, out by);
            bossProfile.Circles2d.AddByCenterRadius(bx, by, 0.010);
            if (bossProfile.End(P.ProfileValidationType.igProfileClosed) != 0) throw new InvalidOperationException("凸台轮廓未闭合。");
            Array bossArr = new object[]{bossProfile};
            ma = a.Models.AddFiniteExtrudedProtrusion(1, ref bossArr, P.FeaturePropertyConstants.igRight, 0.005);
            bossProfile.Visible = false;
            L("A 板：5 个孔 + Φ20 圆凸台，孔特征 " + a.Models.Item(1).Holes.Count + " 个");
            a.SaveAs(Path.Combine(dir, "ScanA.par"));
            a.Close(false); a = null;

            // ---- B 板（200×200，给打孔用）----
            P.Model mb;
            b = BuildPlate(app, 0.2, 0.2, 0.02, out mb);
            b.SaveAs(Path.Combine(dir, "ScanB.par"));
            b.Close(false); b = null;

            // ---- 装配：A 在原点，B 在 A 下面 30mm（顶面 z=-20mm），四周大 50mm 便于点它的顶面 ----
            asm = (A.AssemblyDocument)app.Documents.Add("SolidEdge.AssemblyDocument");
            asm.SaveAs(Path.Combine(dir, "ScanFixture.asm"));
            asm.Activate();
            var occA = asm.Occurrences.AddByFilename(Path.Combine(dir, "ScanA.par"));
            Array mA = Transform.Identity.M; occA.PutMatrix(ref mA, true);
            var occB = asm.Occurrences.AddByFilename(Path.Combine(dir, "ScanB.par"));
            Array mB = Transform.Frame(new V3(-0.05,-0.05,-0.030), new V3(1,0,0), new V3(0,1,0), new V3(0,0,1)).M;
            occB.PutMatrix(ref mB, true);
            L("装配：A(原点, 顶面 z=+10mm) + B(顶面 z=-20mm)，实例 " + asm.Occurrences.Count + " 个");
            asm.Save();
            L("夹具已保存到 " + dir);
        } catch (Exception ex) {
            L("造夹具失败：" + ex.GetType().Name + ": " + Safe(ex.Message) + "\n" + Safe(ex.StackTrace));
            return 3;
        } finally {
            if (a != null) try { a.Close(false); } catch {}
            if (b != null) try { b.Close(false); } catch {}
            if (asm != null) try { asm.Close(false); } catch {}
            try { ((dynamic)original).Activate(); } catch {}
        }
        return 0;
    }

    static P.RefPlane FindXY(P.PartDocument part){
        foreach (P.RefPlane c in part.RefPlanes) {
            Array n = new double[3], p = new double[3], u = new double[3];
            c.GetNormal(ref n); c.GetRootPoint(ref p); c.GetReferenceDirection(ref u);
            if (Math.Abs(D(n,2)-1)<1e-8 && Math.Abs(D(p,0))<1e-8 && Math.Abs(D(p,1))<1e-8 && Math.Abs(D(u,0)-1)<1e-8) return c;
        }
        return null;
    }
    static P.PartDocument BuildPlate(F.Application app2, double w, double h, double t, out P.Model model){
        var part = (P.PartDocument)app.Documents.Add("SolidEdge.PartDocument", Template);
        part.ModelingMode = P.ModelingModeConstants.seModelingModeOrdered;
        var plane = FindXY(part);
        var profile = part.ProfileSets.Add().Profiles.Add(plane);
        var Ln = new S.Line2d[4];
        Ln[0]=profile.Lines2d.AddBy2Points(0,0,w,0); Ln[1]=profile.Lines2d.AddBy2Points(w,0,w,h);
        Ln[2]=profile.Lines2d.AddBy2Points(w,h,0,h); Ln[3]=profile.Lines2d.AddBy2Points(0,h,0,0);
        var rel = (S.Relations2d)profile.Relations2d;
        for (int i=0;i<4;i++){ rel.AddKeypoint(Ln[i],(int)SolidEdgeConstants.KeypointIndexConstants.igLineEnd,Ln[(i+1)%4],(int)SolidEdgeConstants.KeypointIndexConstants.igLineStart); if(i%2==0) rel.AddHorizontal(Ln[i]); else rel.AddVertical(Ln[i]); }
        rel.AddKeypointFix(Ln[0],(int)SolidEdgeConstants.KeypointIndexConstants.igLineStart);
        var dims = (S.Dimensions)profile.Dimensions; dims.Constraint=true; dims.AddLength(Ln[0]); dims.AddLength(Ln[1]);
        if (profile.End(P.ProfileValidationType.igProfileClosed) != 0) throw new InvalidOperationException("矩形轮廓未闭合。");
        Array arr = new object[]{profile};
        model = part.Models.AddFiniteExtrudedProtrusion(1, ref arr, P.FeaturePropertyConstants.igSymmetric, t);
        return part;
    }
    static G.Face PlanarFaceAtZ(P.Model m, double z){
        foreach (G.Face f in (G.Faces)((G.Body)m.Body).get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll)) {
            var pl = f.Geometry as G.Plane; if (pl == null) continue;
            Array p=new double[3], n=new double[3]; pl.GetPlaneData(ref p, ref n);
            if (Math.Abs(Math.Abs(D(n,2)) - 1) > 1e-6) continue;
            if (Math.Abs(D(p,2)-z) < 1e-6) return f;
        }
        return null;
    }
    // CAD 刚建完文档时会短暂处于"拒绝呼叫"状态（RPC_E_CALL_REJECTED 0x80010001），
    // 一次就失败会误报。生产代码里打孔那一段有重试，这里同样重试。
    static void Retry(Action a, string what){
        Exception last = null;
        for (int i = 0; i < 8; i++) {
            try { a(); return; }
            catch (Exception e) {
                last = e;
                int hr = e.HResult;
                bool transient = hr == unchecked((int)0x80010001) || hr == unchecked((int)0x800401FD)
                              || hr == unchecked((int)0x8001010A) || hr == unchecked((int)0x80010108)
                              || hr == unchecked((int)0x800401FB);
                if (!transient) throw;
                L("  " + what + " 被 CAD 拒绝（0x" + hr.ToString("X8") + "），等一下重试 " + (i + 1));
                Pump(8);
            }
        }
        throw last;
    }
    static void DrillAt(P.PartDocument part, P.Model model, double x, double y, HoleSpec spec, string what){
        Retry(delegate {
            var top = PlanarFaceAtZ(model, 0.010);
            if (top == null) throw new InvalidOperationException("找不到顶面");
            var target = AutoHoleReader.ReadTarget(top);
            var res = AutoHoleWriter.Drill(target, spec, new V3[]{ new V3(x, y, 0.010) });
            if (res.Created != 1) throw new InvalidOperationException(what + " 没打出来：" + string.Join("；", res.Failures.ToArray()));
        }, what);
        L("  " + what + " ✓");
        Pump(3);
    }
}
