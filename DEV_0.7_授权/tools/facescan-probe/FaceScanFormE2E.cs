using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using TianGongCadSuite;
using A=SolidEdgeAssembly;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;
using F=SolidEdgeFramework;

// 「面扫描 × 界面层」真机端到端（2026-10-06）。
// 在私有桌面的 CAD 里打开夹具装配，走插件「自动打孔」窗口的真实代码路径 ——
// 和用户鼠标点面走的是同一个 AcceptPick：
//   ① 点 A 的顶面（充满孔的面）→ 自动认孔 + 按孔径归类
//   ② 点 B 的顶面（实心板）     → 当打孔面
//   ③ 开始打孔 → 读结果文本 + 核对 B 板的切除体积与孔数
// 用法: FaceScanFormE2E.exe <夹具.asm> [输出目录]
class FaceScanFormE2E {
    static F.Application app;
    static int fail = 0;
    static void L(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    static void Check(bool ok, string label){ L((ok ? "PASS: " : "FAIL: ") + label); if (!ok) fail++; }
    static void Pump(int n){ for (int i = 0; i < n; i++) { try { app.DoIdle(); } catch { } try { Application.DoEvents(); } catch { } Thread.Sleep(60); } }
    static string N(double v){ return v.ToString("0.###", CultureInfo.InvariantCulture); }
    static double D(Array a,int i){ return Convert.ToDouble(a.GetValue(i)); }
    static double Vol(P.Model m){ return ((G.Body)m.Body).Volume; }

    static G.Face PlanarFaceAtZ(P.Model m, double z){
        foreach (G.Face f in (G.Faces)((G.Body)m.Body).get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll)) {
            var pl = f.Geometry as G.Plane; if (pl == null) continue;
            Array p=new double[3], n=new double[3]; pl.GetPlaneData(ref p, ref n);
            if (Math.Abs(Math.Abs(D(n,2)) - 1) > 1e-6) continue;
            if (Math.Abs(D(p,2)-z) < 1e-6) return f;
        }
        return null;
    }
    // CAD 刚打开文档/刚建完文档时会短暂"拒绝呼叫"（0x80010001），一次就失败会误报。
    static void Retry(Action a, string what){
        Exception last = null;
        for (int i = 0; i < 8; i++) {
            try { a(); return; }
            catch (Exception e) {
                last = e;
                int hr = e.HResult;
                L("   " + what + " 被 CAD 拒绝（0x" + hr.ToString("X8") + "），等一下重试 " + (i + 1));
                Pump(8);
            }
        }
        throw last;
    }

    // 窗口的 AcceptPick 会把异常吞进状态栏（用户看到的一句话），所以"CAD 忙被拒"从外面看不到 ——
    // 只能看状态栏文字重试。实测这个拒绝是瞬态的（CAD 刚打开装配时最容易撞上）。
    static void PickUntilAccepted(AutoHoleForm form, object sel, string what){
        for (int i = 0; i < 6; i++) {
            Retry(delegate { form.AcceptPick(sel); }, what);
            Pump(8);
            string ui = form.UiSummary();
            if (ui.IndexOf("0x80010001") < 0 && ui.IndexOf("拒绝接收呼叫") < 0) return;
            L("   " + what + " 撞上 CAD 忙，等一等重来 " + (i + 1));
            Pump(12);
        }
    }

    static A.Occurrence FindOcc(A.AssemblyDocument asm, string want){
        foreach (A.Occurrence o in asm.Occurrences) {
            string nm = null; try { nm = o.Name; } catch { }
            if (nm != null && nm.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0) return o;
        }
        return null;
    }

    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        TianGongCadSuite.Licensing.LicenseTestHooks.GateOverride = 0;   // 开发构建才有；本机没激活，实测要真写模型
        if (args.Length < 1) { L("用法: FaceScanFormE2E.exe <夹具.asm> [输出目录]"); return 1; }
        string asmPath = Path.GetFullPath(args[0]);
        string outDir = args.Length > 1 ? Path.GetFullPath(args[1]) : Path.GetDirectoryName(asmPath);
        Directory.CreateDirectory(outDir);
        try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); }
        catch (Exception e) { L("连不上 CAD：" + e.Message); return 2; }
        L("已连上 CAD " + app.Version);
        Pump(6);

        A.AssemblyDocument asm = null;
        try {
            asm = (A.AssemblyDocument)app.Documents.Open(asmPath);
            asm.Activate();
            Pump(30);      // 刚打开装配时 CAD 会忙一阵子，任何一次跨进程调用都可能被拒
            int occCount = -1;
            Retry(delegate { occCount = asm.Occurrences.Count; }, "读实例数");
            Check(occCount == 2, "夹具装配有两个实例（实 " + occCount + "）");
            A.Occurrence occA = null, occB = null;
            Retry(delegate { occA = FindOcc(asm, "ScanA"); occB = FindOcc(asm, "ScanB"); }, "找实例");
            Check(occA != null && occB != null, "找到 A / B 两个实例");
            P.PartDocument partA = null, partB = null;
            Retry(delegate { partA = (P.PartDocument)occA.OccurrenceDocument; partB = (P.PartDocument)occB.OccurrenceDocument; }, "取实例文档");
            P.Model modelB = null;
            Retry(delegate { modelB = (P.Model)partB.Models.Item(1); }, "取 B 板模型");
            double volB0 = Vol(modelB);
            G.Face faceA = null, faceB = null;
            Retry(delegate { faceA = PlanarFaceAtZ((P.Model)partA.Models.Item(1), 0.010); }, "读 A 顶面");
            Retry(delegate { faceB = PlanarFaceAtZ(modelB, 0.010); }, "读 B 顶面");
            Check(faceA != null && faceB != null, "读到 A 的顶面与 B 的顶面");
            object refA = null, refB = null;
            Retry(delegate { refA = asm.CreateReference(occA, faceA); }, "取 A 顶面的选择引用");
            Retry(delegate { refB = asm.CreateReference(occB, faceB); }, "取 B 顶面的选择引用");

            AutoHoleForm form = null;
            try {
                Retry(delegate { form = new AutoHoleForm(app, asm); }, "打开自动打孔窗口");
                form.SuppressDialogs = true;      // 结果写进 LastResult，不弹框（真机脚本里没人点确定）
                Retry(delegate { form.Show(); }, "显示窗口");
                Pump(6);
                // 这里**故意不调 StartPicking**：它要问 CAD 要一个命令对象，而命令对象只有 CAD 进程内的
                // 加载项才拿得到（外部进程会被 RPC_E_CALL_REJECTED 拒绝）；它的失败分支还会 Cleanup()、
                // 把窗口置成 closing，后面就什么都点不动了。用户真实使用时这一步由点功能区按钮触发、
                // 在 CAD 进程内跑；下面这一步一步的 AcceptPick 才是"点在模型上"要走的代码。
                Pump(6);

                L("");
                L("① 点 A 的顶面（一个充满孔的面）");
                PickUntilAccepted(form, refA, "点 A 的顶面");
                Pump(6);
                string ui1 = form.UiSummary();
                L("   UI: " + ui1);
                Check(ui1.IndexOf("参考孔 3 组") >= 0, "自动认孔并按孔径归成 3 类（Φ6.6 / Φ5 / Φ4.917）");
                Check(ui1.IndexOf("Φ6.6　×3") >= 0, "Φ6.6 那一类有 3 个孔");
                Check(ui1.IndexOf("圆柱沉孔") >= 0 && ui1.IndexOf("孔口 Φ11") >= 0, "沉孔说明写明孔口 Φ11、按 Φ6.6 配做");
                Check(ui1.IndexOf("打孔面 0 个") >= 0, "带孔的面没有被当成打孔面");

                L("");
                L("② 点 B 的顶面（实心板 → 打孔面）");
                PickUntilAccepted(form, refB, "点 B 的顶面");
                Pump(6);
                string ui2 = form.UiSummary();
                L("   UI: " + ui2);
                Check(ui2.IndexOf("打孔面 1 个") >= 0, "B 的顶面被当成打孔面");
                Check(ui2.IndexOf("将打 5 个孔") >= 0, "预览徽标：将打 5 个孔（凸台没有被算进去）");
                Check(ui2.IndexOf("打孔面分布：ScanB.par 5 个") >= 0, "预览：5 个孔心都落在 B 板上");
                Check(ui2.IndexOf("可打孔=True") >= 0, "「开始打孔」可用");

                L("");
                L("③ 开始打孔");
                Retry(delegate { form.DrillForTest(); }, "开始打孔");
                Pump(8);
                string res = form.LastResult ?? "";
                L("   RESULT: " + res.Replace("\r\n", " ⏎ "));
                Check(res.IndexOf("共打孔 5 个") >= 0, "结果：共打孔 5 个");
                Check(res.IndexOf("沉孔/锥沉/倒角孔口") >= 0, "结果里写明了沉孔按下面的孔径配做");
                Check(res.IndexOf("失败") < 0, "没有失败项");
            } finally { if (form != null) try { form.Close(); } catch {} }

            // 几何核验：3 个 M6 螺纹孔（Φ4.917 贯通 20mm）+ 2 个 M6 沉孔（Φ11 深 6.5 + Φ6.6 走完 13.5）
            double removed = volB0 - Vol(modelB);
            double expect = 3.0 * Math.PI * Math.Pow(0.004917/2, 2) * 0.020
                          + 2.0 * (Math.PI * Math.Pow(0.0055, 2) * 0.0065 + Math.PI * Math.Pow(0.0033, 2) * 0.0135);
            Check(Math.Abs(removed - expect) / expect < 0.03,
                  "B 板切除体积符合 3×M6 螺纹孔 + 2×M6 沉孔（实 " + N(removed*1e9) + " mm³ / 期 " + N(expect*1e9) + " mm³）");
            Check(modelB.Holes.Count == 5, "B 板新增 5 个孔特征（实 " + modelB.Holes.Count + "）");
            Check(Math.Abs(Vol((P.Model)((P.PartDocument)occA.OccurrenceDocument).Models.Item(1)) - 0) > 0, "A 板仍可读");

            // 不保存文档（保存会弹"是否保存零件"的模态框，私有桌面上没人点）；截图留证就行。
            try { ((dynamic)app.ActiveWindow).View.Fit(); ((dynamic)app.ActiveWindow).View.SaveAsImage(Path.Combine(outDir, "facescan-e2e.png"), 1200, 900); } catch {}
        } catch (Exception e) {
            L("E2E 异常：" + e.GetType().Name + ": " + e.Message);
            fail++;
        } finally {
            try { asm.Close(false); } catch {}
            Pump(2);
        }
        L("");
        L(fail == 0 ? "FACE-SCAN E2E OK" : ("FACE-SCAN E2E FAILED " + fail));
        return fail == 0 ? 0 : 1;
    }
}
