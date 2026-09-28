using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using TianGongCadSuite;
using A=SolidEdgeAssembly;
using F=SolidEdgeFramework;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;
using S=SolidEdgeFrameworkSupport;

// 可视化验收（跑在私有桌面里，用户看不到）：
// 自己造一个干净夹具 → 用产品代码把命令 6/7/8 各跑一遍 → 逐步截图。
class VisualAcceptance {
    static object M = Type.Missing;
    const string CadHome = @"C:\Program Files\NDS\TianGong 2025";
    static string Template { get { return Path.Combine(CadHome,"Template","ISO Metric","iso metric part.par"); } }
    static string OutDir;
    static F.Application app;
    static int pass, fail;
    static void Ok(string s){ pass++; Console.WriteLine("PASS: " + s); Console.Out.Flush(); }
    static void Bad(string s){ fail++; Console.WriteLine("FAIL: " + s); Console.Out.Flush(); }
    static void Info(string s){ Console.WriteLine("INFO " + s); Console.Out.Flush(); }

    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool MoveWindow(IntPtr h, int x, int y, int w, int ht, bool repaint);
    [DllImport("user32.dll")] static extern IntPtr SendMessageW(IntPtr h, uint msg, IntPtr wp, IntPtr lp);
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }

    [STAThread] static int Main(string[] args){
        OutDir = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(Environment.CurrentDirectory, "artifacts", "visual");
        Directory.CreateDirectory(OutDir);
        try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); Ok("连上私有桌面上的 CAD（版本 " + app.Version + "）"); }
        catch (Exception e) { Bad("连接 CAD 失败：" + e.Message); return 2; }
        try { app.Visible = true; app.ScreenUpdating = true; } catch { }
        try { Run(); }
        catch (Exception e) { Bad("异常 " + e.GetType().Name + ": " + e.Message); Console.WriteLine(e.StackTrace); }
        Console.WriteLine("VISUAL ASSERTIONS pass=" + pass + " fail=" + fail);
        Console.WriteLine("VISUAL ARTIFACTS " + OutDir);
        return fail == 0 ? 0 : 1;
    }

    // COM 在 CAD 忙的时候会抛"调用方拒绝接收呼叫"，这类是暂时性的，重试即可
    static T Retry<T>(string what, Func<T> f){
        for (int i = 0; i < 12; i++) {
            try { return f(); }
            catch (Exception e) {
                string m = e.Message;
                bool transient = m.Contains("0x800401FD") || m.Contains("0x8001010A") || m.Contains("拒绝") || m.Contains("忙")
                                 || m.Contains("CO_E_OBJNOTCONNECTED") || m.Contains("RPC_E_CALL_REJECTED") || m.Contains("0x80010001");
                if (!transient || i == 11) throw;
                Info("  " + what + " 遇到 CAD 忙，重试 " + (i + 1));
                try { app.DoIdle(); } catch { }
                Thread.Sleep(700);
            }
        }
        throw new InvalidOperationException(what);
    }
    static void Retry(string what, Action a){ Retry<object>(what, () => { a(); return null; }); }

    static void Run(){
        var cad = FindWindow("EngineFrame", null);
        if (cad != IntPtr.Zero) { MoveWindow(cad, 30, 30, 1500, 880, true); SetForegroundWindow(cad); Thread.Sleep(1200); }

        // ---------- 造一个干净夹具：A 板带 Φ6.6 孔，B 板实心 20mm，B 在上方 20mm ----------
        string fx = Path.Combine(OutDir, "VisualFixture.asm");
        string pa = Path.Combine(OutDir, "VisA.par");
        string pb = Path.Combine(OutDir, "VisB.par");
        foreach (var p in new[]{ fx, fx + ".cfg", pa, pb }) if (File.Exists(p)) File.Delete(p);

        A.AssemblyDocument asm = null;
        P.Model ma = null, mb = null;
        Retry("建装配", () => { asm = (A.AssemblyDocument)app.Documents.Add("SolidEdge.AssemblyDocument"); asm.SaveAs(fx); });
        Retry("建 A 板", () => {
            var d = NewPlate("VisA", 0.1, 0.1, 0.01, out ma);
            DrillSimple(d, ma, 0.05, 0.05, 0.0066);
            d.SaveAs(pa); d.Close(false);
        });
        Retry("建 B 板", () => {
            var d = NewPlate("VisB", 0.1, 0.1, 0.02, out mb);
            d.SaveAs(pb); d.Close(false);
        });
        Ok("夹具就绪：A 板带 1 个 Φ6.6 孔，B 板实心 20mm");

        A.Occurrence occA = null, occB = null;
        Retry("插入实例", () => {
            asm.Activate();
            occA = asm.Occurrences.AddByFilename(pa);
            Array m1 = Transform.Identity.M; occA.PutMatrix(ref m1, true);
            occB = asm.Occurrences.AddByFilename(pb);
            Array m2 = Transform.Frame(new V3(0, 0, 0.02), new V3(1, 0, 0), new V3(0, 1, 0), new V3(0, 0, 1)).M;
            occB.PutMatrix(ref m2, true);
        });
        var docA = (P.PartDocument)occA.OccurrenceDocument;
        var docB = (P.PartDocument)occB.OccurrenceDocument;
        var mdlA = (P.Model)docA.Models.Item(1);
        var mdlB = (P.Model)docB.Models.Item(1);
        asm.Save();
        Ok("装配插入 2 个实例：" + occA.Name + "、" + occB.Name + "（B 板在 A 上方 20mm）");
        // 刚插入的实例文档需要一点时间让 COM 就绪：不等的话第一次写操作会被 CO_E_OBJNOTREG 顶回来
        for (int i = 0; i < 6; i++) { try { app.DoIdle(); } catch { } Thread.Sleep(400); }

        // ---------- 命令 6：点孔 → 点面 → 打孔 ----------
        G.Edge edgeA = null;
        foreach (G.Edge e in (G.Edges)((G.Body)mdlA.Body).get_Edges(G.FeatureTopologyQueryTypeConstants.igQueryAll))
            if (e.Geometry is G.Circle) { edgeA = e; break; }
        if (edgeA == null) { Bad("A 板上没有圆孔"); return; }
        var refHole = Retry("读参考孔", () => AutoHoleReader.ReadReference(asm.CreateReference(occA, edgeA)));
        Ok("① 点孔：读到参考孔 Φ" + F(refHole.DiameterMm));

        G.Face faceB = null;
        Retry("取打孔面", () => { faceB = PlanarFaceAtZ(mdlB, -0.010); });
        var target = Retry("读打孔面", () => AutoHoleReader.ReadTarget(asm.CreateReference(occB, faceB)));
        Ok("② 点面：读到打孔面 " + target.PartName + " " + target.Label);

        var match = HoleMatcher.Match(refHole.DiameterMm);
        Ok("③ 自动反推：Φ" + F(refHole.DiameterMm) + " = " + match.Row.Size + " 过孔 → 目标件配螺纹孔（按内径 Φ"
            + match.Target.HoleDiameter.ToString("0.###") + " 建模）");
        Info("孔规格：" + match.Target.Summary);

        ShotCad(cad, "01-打孔前.png");
        int conesBefore = Retry("数锥面", () => Cones(mdlB));
        double v0 = Retry("读体积", () => ((G.Body)mdlB.Body).Volume);
        var centre = AutoHoleReader.Intersect(refHole, target);
        var res = Retry("打螺纹孔", () => AutoHoleWriter.Drill(target, match.Target, new V3[]{ centre }));
        double removed = (v0 - ((G.Body)mdlB.Body).Volume) * 1e9;
        if (res.Created == 1 && res.Failures.Count == 0) Ok("④ 打孔成功：" + res.Method + "，切除 " + removed.ToString("0.#") + " mm³");
        else Bad("打孔失败：" + string.Join("；", res.Failures.ToArray()));
        if (res.Created > 0) {   // 没打上孔就别谈自检（免得假阳性）
            if (res.Audit.Length > 0) Bad("自检报警：" + res.Audit); else Ok("⑤ 自检通过：CAD 生成的孔型与要求一致");
        }
        int conesAfter = Retry("数锥面", () => Cones(mdlB));
        if (conesAfter == conesBefore) Ok("⑥ 贯通螺纹孔没有引入任何锥面（锥面数 " + conesBefore + " → " + conesAfter + "）");
        else Bad("引入了 " + (conesAfter - conesBefore) + " 个锥面");
        ShotCad(cad, "02-打孔后.png");

        // ---------- 命令 7：在 B 板顶面排 4 个孔 ----------
        G.Face faceTop = null; Retry("取顶面", () => { faceTop = PlanarFaceAtZ(mdlB, 0.010); });
        var t2 = Retry("读顶面", () => AutoHoleReader.ReadTarget(asm.CreateReference(occB, faceTop)));
        var frame = FaceFrameReader.Read(t2);
        var pattern = new HolePatternSpec { Kind = HolePatternKind.Divide, Count = 4, EdgeMm = 15 };
        var offs = HolePatternSolver.Offsets(frame.LengthMm, pattern);
        var pts = offs.Select(o => frame.At(o)).ToList();
        var spec2 = new HoleSpec { Kind = HoleKind.Through, HoleDiameter = 6, Depth = 0 };
        double w0 = Retry("读体积", () => ((G.Body)mdlB.Body).Volume);
        var res2 = Retry("批量排孔", () => AutoHoleWriter.Drill(t2, spec2, pts));
        double rem2 = (w0 - ((G.Body)mdlB.Body).Volume) * 1e9;
        if (res2.Created == offs.Length) Ok("命令 7：在 " + frame.Label + " 上排 " + offs.Length + " 个 Φ6 通孔，切除 " + rem2.ToString("0.#") + " mm³");
        else Bad("批量排孔只成功 " + res2.Created + "/" + offs.Length + "：" + string.Join("；", res2.Failures.ToArray()));
        ShotCad(cad, "03-排孔后.png");

        // ---------- 命令 7b：V 型底盲孔 + 孔口倒角（看得见的效果） ----------
        G.Face faceTop2 = null; Retry("取顶面", () => { faceTop2 = PlanarFaceAtZ(mdlB, 0.010); });
        var t3 = Retry("读顶面2", () => AutoHoleReader.ReadTarget(asm.CreateReference(occB, faceTop2)));
        var blind = new HoleSpec { Kind = HoleKind.Tapped, ThreadSize = "M8", HoleDiameter = 6.647, Depth = 8, Bottom = HoleBottom.VBottom };
        var cham = new HoleSpec { Kind = HoleKind.Tapped, ThreadSize = "M8", HoleDiameter = 6.647, Depth = 0, Chamfer = true };
        var r3 = Retry("V 底盲孔", () => AutoHoleWriter.Drill(t3, blind, new V3[]{ new V3(0.025, 0.075, 0.030) }));
        var r4 = Retry("孔口倒角孔", () => AutoHoleWriter.Drill(t3, cham, new V3[]{ new V3(0.075, 0.075, 0.030) }));
        if (r3.Created == 1 && r4.Created == 1) Ok("命令 6：另打 1 个 M8 V 型底盲孔（深 8）+ 1 个 M8 带孔口倒角孔");
        else Bad("V 底/倒角孔失败：" + string.Join("；", r3.Failures.Concat(r4.Failures).ToArray()));

        // ---------- 剖开 B 板，让孔的内部露出来 ----------
        Retry("剖切 B 板", () => CutHalf(docB, mdlB));
        Ok("把 B 板沿 Y 剖开一半，孔的内部（孔底/锥面/倒角）直接可见");
        LookFromPlusY(0.05, 0.014, 0.03);   // 装配坐标：B 板顶面在 z=30mm
        ShotCad(cad, "04-剖面.png");

        // ---------- 孔型剖面演示件：五种孔并排 + 剖开，一张图看懂每种孔长什么样 ----------
        try {
            P.Model mdemo;
            var demo = NewPlate("HoleDemo", 0.06, 0.03, 0.012, out mdemo);   // 小一点，孔才看得清
            var specs = new object[][]{
                new object[]{ "贯通螺纹孔 M6",   new HoleSpec{ Kind=HoleKind.Tapped, ThreadSize="M6", HoleDiameter=4.917, Depth=0 } },
                new object[]{ "平底盲孔 Φ6 深8", new HoleSpec{ Kind=HoleKind.Through, HoleDiameter=6, Depth=8, Bottom=HoleBottom.Flat } },
                new object[]{ "V 底盲孔 Φ6 深8", new HoleSpec{ Kind=HoleKind.Through, HoleDiameter=6, Depth=8, Bottom=HoleBottom.VBottom } },
                new object[]{ "孔口倒角孔 Φ6",   new HoleSpec{ Kind=HoleKind.Through, HoleDiameter=6, Depth=0, Chamfer=true } },
                new object[]{ "锥形沉孔 Φ6/Φ11", new HoleSpec{ Kind=HoleKind.Countersink, HoleDiameter=6, CountersinkDiameter=11, CountersinkAngle=90, Depth=0 } },
            };
            double dx = 0.010;
            for (int i = 0; i < specs.Length; i++) {
                var faceD = PlanarFaceAtZ(mdemo, 0.006);
                var td = AutoHoleReaderNoRef(demo, mdemo, faceD);
                var sp = (HoleSpec)specs[i][1];
                var rr = Retry("演示孔 " + specs[i][0], () => AutoHoleWriter.Drill(td, sp, new V3[]{ new V3(0.010 + dx * i, 0.014, 0.006) }));
                if (rr.Created == 1) Ok("演示孔 " + specs[i][0] + " 打好（" + rr.Method + "）");
                else Bad("演示孔 " + specs[i][0] + " 失败：" + string.Join("；", rr.Failures.ToArray()));
            }
            // 沿 Y 把后半块切掉，保留 y<30mm 的前半 —— 剖面正对默认等轴测视角
            Retry("演示件剖切", () => CutHalfKeepFront(demo, mdemo));
            string demoPath = Path.Combine(OutDir, "HoleDemo.par");
            if (File.Exists(demoPath)) File.Delete(demoPath);   // 已存在会弹"是否覆盖"对话框，永久阻塞
            demo.SaveAs(demoPath);
            demo.Activate();
            TidyView(demo, mdemo);
            Thread.Sleep(600);
            // 从 +Y 方向正对剖面看（默认等轴测看不到剖面内部）
            LookFromPlusY(0.03, 0.014, 0.0);
            ShotCad(cad, "05-五种孔剖面.png");
            CropZoom(Path.Combine(OutDir, "05-五种孔剖面.png"), Path.Combine(OutDir, "06-五种孔剖面-放大.png"), 400, 330, 950, 400, 1.7f);
            Ok("剖面演示件已保存并截图（05 整窗 / 06 放大）");
            asm.Activate(); Thread.Sleep(400);
        } catch (Exception e) { Bad("剖面演示件失败：" + e.Message); }

        // ---------- 命令 8：配孔检查 ----------
        var warns = new List<string>();
        var holes = Retry("采集孔", () => AutoHoleWriter.CollectAssemblyHoles(asm, out warns));
        var names = new List<string>();
        foreach (A.Occurrence oc in asm.Occurrences) { try { names.Add(oc.Name); } catch { } }
        var groups = HoleCheck.GroupByAxis(holes);
        var issues = HoleCheck.Run(holes, (h, p) => AutoHoleWriter.RayPassesThrough(asm, h, p), false, names);
        Ok("命令 8：读到 " + holes.Count + " 个孔 / " + groups.Count + " 组同轴孔，报出 " + issues.Count + " 条问题");
        foreach (var i in issues.Take(4)) Info("  【" + i.KindName + "】" + i.Message);
        Info(HoleCheck.Summary(issues, holes.Count, groups.Count));

        // ---------- 三个窗口（走 CAD 的命令机制真实显示） ----------
        int[] ids = { 6, 7, 8 };
        string[] names2 = { "10-窗口-自动打孔.png", "12-窗口-批量排孔.png", "13-窗口-配孔检查.png" };
        for (int i = 0; i < ids.Length; i++) {
            var win = ShowCommandWindow(ids[i]);
            if (win != IntPtr.Zero) {
                Ok("命令 " + ids[i] + " 的窗口在真实 CAD 里显示并截图（" + names2[i] + "）");
                ShotWindow(win, Path.Combine(OutDir, names2[i]));
                SendMessageW(win, 0x0010, IntPtr.Zero, IntPtr.Zero);
                Thread.Sleep(900);
            } else Bad("命令 " + ids[i] + " 的窗口没有出现");
        }

        try { asm.Save(); } catch { }
        ShotCad(cad, "20-最终模型.png");
        Compose(Path.Combine(OutDir, "04-剖面.png"), Path.Combine(OutDir, "10-窗口-自动打孔.png"), Path.Combine(OutDir, "30-剖面与窗口.png"));
    }

    static string F(double v){ return v.ToString("0.##"); }

    // ---------- 夹具 ----------
    static P.PartDocument NewPlate(string name, double w, double h, double t, out P.Model model){
        P.PartDocument part = (P.PartDocument)app.Documents.Add("SolidEdge.PartDocument", Template);
        part.ModelingMode = P.ModelingModeConstants.seModelingModeOrdered;
        var xy = FindXY(part);
        var prof = part.ProfileSets.Add().Profiles.Add(xy);
        var L = new S.Line2d[4];
        L[0] = prof.Lines2d.AddBy2Points(0, 0, w, 0); L[1] = prof.Lines2d.AddBy2Points(w, 0, w, h);
        L[2] = prof.Lines2d.AddBy2Points(w, h, 0, h); L[3] = prof.Lines2d.AddBy2Points(0, h, 0, 0);
        var rel = (S.Relations2d)prof.Relations2d;
        for (int i = 0; i < 4; i++) { rel.AddKeypoint(L[i], (int)SolidEdgeConstants.KeypointIndexConstants.igLineEnd, L[(i+1)%4], (int)SolidEdgeConstants.KeypointIndexConstants.igLineStart); if (i%2==0) rel.AddHorizontal(L[i]); else rel.AddVertical(L[i]); }
        rel.AddKeypointFix(L[0], (int)SolidEdgeConstants.KeypointIndexConstants.igLineStart);
        if (prof.End(P.ProfileValidationType.igProfileClosed) != 0) throw new InvalidOperationException("底板轮廓不闭合");
        Array arr = new object[]{ prof };
        model = part.Models.AddFiniteExtrudedProtrusion(1, ref arr, P.FeaturePropertyConstants.igSymmetric, t);
        return part;
    }

    static void DrillSimple(P.PartDocument part, P.Model model, double x, double y, double dia){
        var plane = part.RefPlanes.AddParallelByDistance(FindXY(part), 0.005, P.ReferenceElementConstants.igNormalSide, M, M, M, M);
        var prof = part.ProfileSets.Add().Profiles.Add(plane);
        double x2, y2; prof.Convert3DCoordinate(x, y, 0.005, out x2, out y2);
        prof.Holes2d.Add(x2, y2);
        if (prof.End(P.ProfileValidationType.igProfileClosed) != 0) throw new InvalidOperationException("孔轮廓不闭合");
        var hd = part.HoleDataCollection.Add(P.FeaturePropertyConstants.igRegularHole, dia,
            0.0, 0.0, 0.0, 0.0, 0.0, P.FeaturePropertyConstants.igNone, M, M, M, M, M,
            P.FeaturePropertyConstants.igVBottomDimToFlat, M, M, M, M, M, M, true);
        object desc = null;
        foreach (var side in new[]{ P.FeaturePropertyConstants.igLeft, P.FeaturePropertyConstants.igRight }) {
            P.Hole h = null;
            try { h = model.Holes.AddThroughAll(prof, side, hd); } catch { continue; }
            if (h == null) continue;
            if ((int)h.GetStatusEx(out desc) == (int)P.FeatureStatusConstants.igFeatureOK) return;
            try { h.Delete(); } catch { }
        }
        throw new InvalidOperationException("夹具孔打不出来");
    }

    // 剖切演示件：切掉 y>30mm 的后半，保留前半（剖面朝 +Y，正对等轴测视角）
    static void CutHalfKeepFront(P.PartDocument part, P.Model model){
        var plane = part.RefPlanes.AddParallelByDistance(FindXY(part), 0.006, P.ReferenceElementConstants.igNormalSide, M, M, M, M);
        var prof = part.ProfileSets.Add().Profiles.Add(plane);
        // 注意：基准面的 2D 坐标系不一定与零件 XY 轴对齐，所以矩形四角必须用
        // Convert3DCoordinate 从零件坐标换算过去（和打孔代码同款做法）。
        // 否则剖切位置会整体跑偏，孔就切不到。
        double[] px = new double[4], py = new double[4];
        double[][] corners = new double[][]{
            new double[]{ -0.02, 0.014 }, new double[]{ 0.12, 0.014 },
            new double[]{ 0.12, 0.09 },  new double[]{ -0.02, 0.09 },
        };
        for (int i = 0; i < 4; i++) {
            double u = 0, v = 0;
            prof.Convert3DCoordinate(corners[i][0], corners[i][1], 0.006, out u, out v);
            px[i] = u; py[i] = v;
        }
        var l = new S.Line2d[4];
        for (int i = 0; i < 4; i++) l[i] = prof.Lines2d.AddBy2Points(px[i], py[i], px[(i + 1) % 4], py[(i + 1) % 4]);
        var rel = (S.Relations2d)prof.Relations2d;
        for (int i = 0; i < 4; i++) rel.AddKeypoint(l[i], (int)SolidEdgeConstants.KeypointIndexConstants.igLineEnd, l[(i+1)%4], (int)SolidEdgeConstants.KeypointIndexConstants.igLineStart);
        if (prof.End(P.ProfileValidationType.igProfileClosed) != 0) throw new InvalidOperationException("剖切轮廓不闭合");
        object desc = null;
        foreach (var side in new[]{ P.FeaturePropertyConstants.igLeft, P.FeaturePropertyConstants.igRight }) {
            P.ExtrudedCutout cut = null;
            try { cut = model.ExtrudedCutouts.AddThroughAll(prof, P.FeaturePropertyConstants.igRight, side); } catch { continue; }
            if (cut == null) continue;
            if ((int)cut.GetStatusEx(out desc) == (int)P.FeatureStatusConstants.igFeatureOK) { try { prof.Visible = false; } catch { } return; }
            try { cut.Delete(); } catch { }
        }
        throw new InvalidOperationException("剖切失败");
    }

    // 把相机放到 +Y 方向，正对切开的剖面
    static void LookFromPlusY(double cx, double cy, double cz){
        try {
            dynamic win = app.ActiveWindow;
            dynamic v = win.View;
            v.SetCamera(cx, cy + 0.35, cz + 0.02, cx, cy, cz, 0, 0, 1, false, 0.2);
            Thread.Sleep(400);
            v.Fit();
            Thread.Sleep(400);
            app.DoIdle();
        } catch (Exception e) { Info("设置视角失败：" + e.Message); }
    }

    // 截图前把零件"收拾干净"：隐藏所有轮廓和基准面，免得挡住剖面
    static void TidyView(P.PartDocument part, P.Model model){
        try { for (int i = 1; i <= part.ProfileSets.Count; i++) { var ps = part.ProfileSets.Item(i); for (int j = 1; j <= ps.Profiles.Count; j++) { try { ps.Profiles.Item(j).Visible = false; } catch { } } } } catch { }
        try { for (int i = 1; i <= part.RefPlanes.Count; i++) { try { part.RefPlanes.Item(i).Visible = false; } catch { } } } catch { }
        try { ((dynamic)part).RefPlanes.Item(1).Visible = false; } catch { }
    }

    // 演示件不在装配里，TargetFace 直接按零件文档构造
    static TargetFace AutoHoleReaderNoRef(P.PartDocument part, P.Model model, G.Face face){
        var pl = face.Geometry as G.Plane;
        Array p = new double[3], n = new double[3];
        pl.GetPlaneData(ref p, ref n);
        return new TargetFace {
            Plane = new PlaneInput(V3.From(p), V3.From(n).Unit(), "顶面"),
            Face = face, Part = part, Placement = Transform.Identity, Label = "顶面", PartName = "HoleDemo"
        };
    }

    static void CutHalf(P.PartDocument part, P.Model model){
        var plane = part.RefPlanes.AddParallelByDistance(FindXY(part), 0.010, P.ReferenceElementConstants.igNormalSide, M, M, M, M);
        var prof = part.ProfileSets.Add().Profiles.Add(plane);
        double[] px = new double[4], py = new double[4];
        double[][] corners = new double[][]{
            new double[]{ -0.02, -0.02 }, new double[]{ 0.12, -0.02 },
            new double[]{ 0.12, 0.03 },   new double[]{ -0.02, 0.03 },
        };
        for (int i = 0; i < 4; i++) {
            double u = 0, v = 0;
            prof.Convert3DCoordinate(corners[i][0], corners[i][1], 0.010, out u, out v);
            px[i] = u; py[i] = v;
        }
        var l = new S.Line2d[4];
        for (int i = 0; i < 4; i++) l[i] = prof.Lines2d.AddBy2Points(px[i], py[i], px[(i + 1) % 4], py[(i + 1) % 4]);
        var rel = (S.Relations2d)prof.Relations2d;
        for (int i = 0; i < 4; i++) rel.AddKeypoint(l[i], (int)SolidEdgeConstants.KeypointIndexConstants.igLineEnd, l[(i+1)%4], (int)SolidEdgeConstants.KeypointIndexConstants.igLineStart);
        if (prof.End(P.ProfileValidationType.igProfileClosed) != 0) throw new InvalidOperationException("剖切轮廓不闭合");
        object desc = null;
        foreach (var side in new[]{ P.FeaturePropertyConstants.igLeft, P.FeaturePropertyConstants.igRight }) {
            P.ExtrudedCutout cut = null;
            try { cut = model.ExtrudedCutouts.AddThroughAll(prof, P.FeaturePropertyConstants.igRight, side); } catch { continue; }
            if (cut == null) continue;
            if ((int)cut.GetStatusEx(out desc) == (int)P.FeatureStatusConstants.igFeatureOK) return;
            try { cut.Delete(); } catch { }
        }
        throw new InvalidOperationException("剖切失败");
    }

    static P.RefPlane FindXY(P.PartDocument part){
        foreach (P.RefPlane c in part.RefPlanes) {
            Array n = new double[3], p = new double[3], u = new double[3];
            c.GetNormal(ref n); c.GetRootPoint(ref p); c.GetReferenceDirection(ref u);
            if (Math.Abs(D(n,2)-1) < 1e-8 && Math.Abs(D(p,0)) < 1e-8 && Math.Abs(D(p,1)) < 1e-8 && Math.Abs(D(u,0)-1) < 1e-8) return c;
        }
        return null;
    }
    static double D(Array a, int i){ return Convert.ToDouble(a.GetValue(i)); }

    static int Cones(P.Model model){
        int n = 0;
        foreach (G.Face f in (G.Faces)((G.Body)model.Body).get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll)) {
            object g = null; try { g = f.Geometry; } catch { }
            if (g is G.Cone) n++;
        }
        return n;
    }
    static G.Face PlanarFaceAtZ(P.Model model, double z){
        foreach (G.Face f in (G.Faces)((G.Body)model.Body).get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll)) {
            var pl = f.Geometry as G.Plane; if (pl == null) continue;
            Array p = new double[3], n = new double[3];
            pl.GetPlaneData(ref p, ref n);
            if (Math.Abs(D(n,2)) < 1e-6) continue;
            if (Math.Abs(D(p,2) - z) < 1e-6) return f;
        }
        return null;
    }

    static IntPtr ShowCommandWindow(int localId){
        try {
            F.AddIn addin = null;
            foreach (F.AddIn a in app.AddIns) { try { if (a.GUID == "{8C05165C-65A4-4EF2-A138-508589D82004}") { addin = a; break; } } catch { } }
            if (addin == null) { Info("没找到 AddIn"); return IntPtr.Zero; }
            addin.Connect = true;
            dynamic diag = addin.Object;
            if (diag == null) { Info("AddIn 没有诊断对象"); return IntPtr.Zero; }
            int nativeId = (int)diag.NativeCommandId(localId);
            Retry("启动命令 " + localId, () => app.StartCommand((F.SolidEdgeCommandConstants)nativeId));
            string want = localId == 6 ? "自动打孔" : (localId == 7 ? "批量排孔" : "配孔检查");
            for (int i = 0; i < 40; i++) {
                Thread.Sleep(250);
                try { app.DoIdle(); } catch { }
                var w = FindWindow(null, want);
                if (w != IntPtr.Zero) { Thread.Sleep(1000); return w; }
            }
        } catch (Exception e) { Info("叫窗口失败 " + e.Message); }
        return IntPtr.Zero;
    }

    static void ShotCad(IntPtr cad, string name){
        if (cad == IntPtr.Zero) return;
        try { ((dynamic)app.ActiveWindow).View.Fit(); } catch { }
        try { app.DoIdle(); } catch { }
        Thread.Sleep(900);
        ShotWindow(cad, Path.Combine(OutDir, name));
    }
    static void ShotWindow(IntPtr h, string path){
        RECT r; if (!GetWindowRect(h, out r)) return;
        int w = r.R - r.L, ht = r.B - r.T;
        if (w <= 0 || ht <= 0) return;
        using (var bmp = new Bitmap(w, ht))
        using (var g = Graphics.FromImage(bmp)) {
            IntPtr hdc = g.GetHdc();
            bool ok = PrintWindow(h, hdc, 2);
            g.ReleaseHdc(hdc);
            bmp.Save(path, ImageFormat.Png);
            Console.WriteLine("SHOT " + (ok ? "ok" : "false") + " " + path + " (" + w + "x" + ht + ")");
        }
    }
    // 把窗口截图里的视口区域裁出来放大，方便肉眼看孔的内部
    static void CropZoom(string src, string dst, int x, int y, int w, int h, float zoom){
        try {
            using (var img = Image.FromFile(src)) {
                var rect = new Rectangle(x, y, Math.Min(w, img.Width - x), Math.Min(h, img.Height - y));
                int ow = (int)(rect.Width * zoom), oh = (int)(rect.Height * zoom);
                using (var bmp = new Bitmap(ow, oh))
                using (var g = Graphics.FromImage(bmp)) {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.DrawImage(img, new Rectangle(0, 0, ow, oh), rect, GraphicsUnit.Pixel);
                    bmp.Save(dst, ImageFormat.Png);
                    Console.WriteLine("SHOT crop " + dst + " (" + ow + "x" + oh + ")");
                }
            }
        } catch (Exception e) { Info("裁剪失败 " + e.Message); }
    }

    static void Compose(string a, string b, string path){
        try {
            using (var ia = Image.FromFile(a)) using (var ib = Image.FromFile(b)) {
                int w = Math.Max(ia.Width, ib.Width); int h = ia.Height + ib.Height + 12;
                using (var bmp = new Bitmap(w, h)) using (var g = Graphics.FromImage(bmp)) {
                    g.Clear(Color.FromArgb(0xF0, 0xF2, 0xF6));
                    g.DrawImage(ia, 0, 0); g.DrawImage(ib, (w - ib.Width) / 2, ia.Height + 12);
                    bmp.Save(path, ImageFormat.Png);
                    Console.WriteLine("SHOT compose " + path);
                }
            }
        } catch (Exception e) { Info("拼图失败 " + e.Message); }
    }
    static IntPtr FindWindow(string cls, string title){
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, l) => {
            if (!IsWindowVisible(h)) return true;
            if (cls != null) { var c = new StringBuilder(256); GetClassNameW(h, c, 256); if (!c.ToString().StartsWith(cls)) return true; }
            if (title != null) { var t = new StringBuilder(512); GetWindowTextW(h, t, 512); if (t.ToString() != title) return true; }
            found = h; return false;
        }, IntPtr.Zero);
        return found;
    }
}
