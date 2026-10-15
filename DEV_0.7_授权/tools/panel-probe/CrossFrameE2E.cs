using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using TianGongCadSuite;
using A=SolidEdgeAssembly;
using F=SolidEdgeFramework;

// 跨子装配型材填充的真机端到端（2026-10-15）。走「型材自动填充」窗口的真实代码路径，夹具是用户给的围栏的**副本**：
//   SK26.01-OK310-03-001：1～4 号子围栏环成一圈。2 号（法向 X、x≈2221）两端的角柱分别属于 1 号（y≈3476）和 3 号（y≈8886），
//   所以 2 号 y≈3966、y≈8396 两列框口以前生成不了。
//   ① 只选 2 号整个子装配：不报错（MISUMI 多实体件跳过、已有填充板不当型材），中间几列框口标「已有板」，两端两列没有；
//   ② 2 号 + 1 号、3 号的角柱（子装配内部零件，用 SubOccurrence.Reference 放进 SelectSet，同用户在装配树里点选）：两端两列出现，标「跨 2 个子装配」；
//   ③ 「追加选择」：先 2 号，再追加 1 号角柱 → y≈3966 那列出现；再追加 3 号角柱 → y≈8396 那列也出现；追加空选择不丢已读；
//   ④ 生成 y≈3966 最下面那个框口 → 顶层多 1 个实例，位置、尺寸和识别的一致；再读一遍它标「已有板」；
//   ⑤ 重复选同一根（整个 2 号 + 2 号里的一根）不重复计数；整个围栏 5 个顶层一起选也能读、能识别。
// 用法: CrossFrameE2E.exe <输出目录> <围栏副本装配路径>
class CrossFrameE2E {
    static F.Application app;
    static int fail = 0;
    static void L(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    static void Check(bool ok, string label){ L((ok ? "PASS: " : "FAIL: ") + label); if (!ok) fail++; }
    static void Pump(int n){ for (int i = 0; i < n; i++) { try { app.DoIdle(); } catch { } try { Application.DoEvents(); } catch { } Thread.Sleep(60); } }
    static string N(double v){ return v.ToString("0.###", CultureInfo.InvariantCulture); }
    static void Retry(Action a, string what){
        Exception last = null;
        for (int i = 0; i < 8; i++) {
            try { a(); return; }
            catch (Exception e) { last = e; L("   " + what + " 被 CAD 拒绝（0x" + e.HResult.ToString("X8") + "），重试 " + (i + 1)); Pump(8); }
        }
        throw last;
    }
    static T Get<T>(AutoPanelForm f, string prop){ return (T)typeof(AutoPanelForm).GetProperty(prop, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(f, null); }
    static void Call(AutoPanelForm f, string method, params object[] args){
        var m = typeof(AutoPanelForm).GetMethods(BindingFlags.NonPublic | BindingFlags.Instance).First(x => x.Name == method && x.GetParameters().Length == args.Length);
        try { m.Invoke(f, args); } catch (TargetInvocationException e) { throw e.InnerException; }
    }
    static PanelSpec Spec(AutoPanelForm f, int i){ return (PanelSpec)typeof(AutoPanelForm).GetMethod("Opening", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(f, new object[]{ i }); }
    static V3 Center(PanelSpec s){ return s.Origin + s.U * (s.Width / 2) + s.V * (s.Height / 2); }
    static void Select(A.AssemblyDocument asm, IEnumerable<object> items){
        Retry(delegate { asm.SelectSet.RemoveAll(); foreach (var o in items) asm.SelectSet.Add(o); }, "设置选择集");
        Pump(4);
    }
    // 框口列表里某列（x≈2.2215、y 在区间内）的序号
    static List<int> Column(AutoPanelForm f, double y0, double y1){
        var r = new List<int>();
        int n = Get<int>(f, "OpeningCount");
        for (int i = 0; i < n; i++) { var c = Center(Spec(f, i)); if (Math.Abs(c.X - 2.221523) < 0.002 && c.Y > y0 && c.Y < y1) r.Add(i); }
        return r;
    }
    static void Dump(AutoPanelForm f){
        L("   型材：" + Get<string>(f, "MemberText").Replace("\n", " "));
        L("   状态：" + Get<string>(f, "StatusText").Replace("\n", " | "));
        var texts = Get<string[]>(f, "OpeningTexts"); var on = Get<bool[]>(f, "OpeningChecked");
        for (int i = 0; i < texts.Length; i++) { var c = Center(Spec(f, i)); L("     [" + (on[i] ? "x" : " ") + "] " + texts[i] + "  中心 (" + N(c.X * 1000) + ", " + N(c.Y * 1000) + ", " + N(c.Z * 1000) + ")"); }
    }

    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        TianGongCadSuite.Licensing.LicenseTestHooks.GateOverride = 0;   // 开发构建才有；实测要真写模型
        string settings = SettingsFile.PathOf("autopanel.tsv");
        bool hadSettings = File.Exists(settings); string oldSettings = hadSettings ? File.ReadAllText(settings) : null;
        for (int i = 0; i < 60 && app == null; i++) { try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); } catch { Thread.Sleep(1000); } }
        if (app == null) { L("FATAL 连不上 CAD"); return 2; }
        try { app.Visible = true; app.DisplayAlerts = false; } catch { }
        A.AssemblyDocument asm = null; AutoPanelForm form = null;
        using (new OleFilter())
        try {
            Retry(delegate { asm = (A.AssemblyDocument)app.Documents.Open(Path.GetFullPath(args[1])); }, "打开围栏");
            Pump(30);
            var tops = asm.Occurrences.Cast<A.Occurrence>().ToList();
            Func<string, A.Occurrence> top = name => tops.First(o => o.Name.StartsWith(name));
            A.Occurrence s1 = top("1号"), s2 = top("2号"), s3 = top("3号");
            // 1 号、3 号里落在 2 号平面（x≈2201..2241）上的角柱：子装配内部零件，按用户在装配树里点选的方式放进选择集
            Func<A.Occurrence, double, F.Reference> post = (sub, y) => {
                foreach (A.SubOccurrence c in sub.SubOccurrences) {
                    if (c.Subassembly) continue;
                    var r = (F.Reference)c.Reference;
                    FrameSelection one;
                    try { one = FrameReader.ReadAll(asm, new object[]{ r }); } catch { continue; }
                    if (one.Members.Count != 1) continue;
                    var p = one.Members[0].Points;
                    if (Math.Abs(p.Min(q => q.X) - 2.201523) < 1e-4 && p.Min(q => q.Y) < y && p.Max(q => q.Y) > y && p.Max(q => q.Z) - p.Min(q => q.Z) > 3) {
                        L("   找到角柱 " + sub.Name + " / " + c.Name + "  来源 " + one.Members[0].Source);
                        return r;
                    }
                }
                throw new InvalidOperationException(sub.Name + " 里没找到 y≈" + y + " 的角柱");
            };
            var post1 = post(s1, 3.476); var post3 = post(s3, 8.886);
            Check(FrameReader.ReadAll(asm, new object[]{ post1 }).Members[0].Source == s1.Name, "子装配内部零件的来源记成顶层子装配名（" + s1.Name + "）");

            L(""); L("① 只选 2 号整个子装配");
            Select(asm, new object[]{ s2 });
            Retry(delegate { form = new AutoPanelForm(app, asm); }, "打开型材自动填充");
            form.Show(); Pump(6);
            Call(form, "SetParameters", "4", "0"); Pump(4);
            Dump(form);
            int alone = Get<int>(form, "OpeningCount");
            Check(alone > 0, "只选 2 号能识别出框口（以前：子装配里的填充板让整个平面识别失败）");
            Check(Get<string>(form, "MemberText").Contains("块板子"), "已有填充板单独算，不当型材");
            Check(Column(form, 3.496, 4.436).Count == 0 && Column(form, 7.926, 8.866).Count == 0, "两端两列（缺角柱）识别不到 —— 和用户描述一致");
            Check(Get<string[]>(form, "OpeningTexts").Any(t => t.Contains("已有板")), "已有填充板的框口标「已有板」");
            Check(Enumerable.Range(0, alone).All(i => !Get<string[]>(form, "OpeningTexts")[i].Contains("已有板") || !Get<bool[]>(form, "OpeningChecked")[i]), "「已有板」的框口默认不勾");

            L(""); L("② 2 号 + 1 号、3 号的角柱一起选");
            Select(asm, new object[]{ s2, post1, post3 });
            L("   选择集里的类型：" + string.Join("、", asm.SelectSet.Cast<object>().Select(o => o is F.Reference ? "Reference" : o is A.Occurrence ? "Occurrence" : o is A.SubOccurrence ? "SubOccurrence" : o.GetType().Name).ToArray()));
            Call(form, "ReadSelection"); Pump(4);
            Dump(form);
            var left = Column(form, 3.496, 4.436); var right = Column(form, 7.926, 8.866);
            Check(left.Count == 3, "y≈3966 那列出现 3 个框口（实 " + left.Count + "）");
            Check(right.Count == 3, "y≈8396 那列出现 3 个框口（实 " + right.Count + "）");
            var texts = Get<string[]>(form, "OpeningTexts"); var on = Get<bool[]>(form, "OpeningChecked");
            Check(left.Concat(right).All(i => texts[i].Contains("跨 2 个子装配") && on[i] && !texts[i].Contains("已有板")), "两端的框口标「跨 2 个子装配」、默认勾上");
            var w = left.Select(i => Spec(form, i).Width).Concat(right.Select(i => Spec(form, i).Width)).ToList();
            Check(w.All(x => Math.Abs(x - 0.962) < 1e-6), "两端框口宽 962（940 净空 + 两边各 11 槽深），实 " + string.Join("/", w.Select(x => N(x * 1000)).ToArray()));
            Check(Get<string>(form, "MemberText").Contains("来自 3 处"), "型材汇总写出来自 3 个子装配");

            L(""); L("③ 追加选择");
            Select(asm, new object[]{ s2 }); Call(form, "ReadSelection"); Pump(2);
            Select(asm, new object[]{ post1 }); Call(form, "ReadSelection", true); Pump(2);
            Check(Column(form, 3.496, 4.436).Count == 3 && Column(form, 7.926, 8.866).Count == 0, "追加 1 号角柱 → 只出现 y≈3966 那列");
            Select(asm, new object[]{ post3 }); Call(form, "ReadSelection", true); Pump(2);
            Check(Column(form, 3.496, 4.436).Count == 3 && Column(form, 7.926, 8.866).Count == 3, "再追加 3 号角柱 → 两列都有");
            int full = Get<int>(form, "OpeningCount");
            Select(asm, new object[0]); Call(form, "ReadSelection", true); Pump(2);
            Check(Get<int>(form, "OpeningCount") == full && Get<string>(form, "StatusText").Contains("没有选中"), "空选择点追加：提示、已读的不丢");
            Select(asm, new object[]{ post3 }); Call(form, "ReadSelection", true); Pump(2);
            Check(Get<int>(form, "OpeningCount") == full, "同一根角柱追加两次不重复计数");

            L(""); L("④ 生成 y≈3966 最下面那个框口");
            left = Column(form, 3.496, 4.436);
            int pick = left.OrderBy(i => Center(Spec(form, i)).Z).First();
            var spec = Spec(form, pick); var want = Center(spec);
            Call(form, "CheckOnly", pick);
            int before = asm.Occurrences.Count;
            Call(form, "Generate"); Pump(10);
            L("   状态：" + Get<string>(form, "StatusText"));
            Check(Get<string>(form, "StatusText").StartsWith("已生成 1 块板子"), "生成 1 块板子");
            Check(asm.Occurrences.Count == before + 1, "顶层装配多 1 个实例");
            var panel = asm.Occurrences.Item(asm.Occurrences.Count);
            double x0, y0, z0, x1, y1, z1; panel.Range(out x0, out y0, out z0, out x1, out y1, out z1);
            L("   " + panel.Name + " 包围盒 (" + N(x0 * 1000) + "," + N(y0 * 1000) + "," + N(z0 * 1000) + ")-(" + N(x1 * 1000) + "," + N(y1 * 1000) + "," + N(z1 * 1000) + ")");
            var got = new V3((x0 + x1) / 2, (y0 + y1) / 2, (z0 + z1) / 2);
            Check((got - want).Length < 1e-5, "板子中心 = 识别的框口中心（差 " + N((got - want).Length * 1000) + " mm）");
            var size = new[]{ x1 - x0, y1 - y0, z1 - z0 }.OrderBy(v => v).ToArray();
            var expect = new[]{ spec.Thickness, spec.Width, spec.Height }.OrderBy(v => v).ToArray();
            Check(Enumerable.Range(0, 3).All(k => Math.Abs(size[k] - expect[k]) < 1e-5), "板子尺寸 = " + N(spec.Width * 1000) + " × " + N(spec.Height * 1000) + " × " + N(spec.Thickness * 1000));
            Check(Get<string[]>(form, "OpeningTexts").Count(t => t.Contains("已生成")) == 1, "列表里这个框口标「已生成」");
            Select(asm, new object[]{ s2, post1, post3, panel }); Call(form, "ReadSelection"); Pump(2);
            texts = Get<string[]>(form, "OpeningTexts");
            int again = Enumerable.Range(0, texts.Length).First(i => (Center(Spec(form, i)) - want).Length < 1e-6);
            Check(texts[again].Contains("已有板") && !Get<bool[]>(form, "OpeningChecked")[again], "重新读取（连新板子一起选）：这个框口标「已有板」、不勾");

            L(""); L("⑤ 去重 / 整个围栏");
            var oneOf2 = s2.SubOccurrences.Cast<A.SubOccurrence>().First(c => !c.Subassembly && c.Name.StartsWith("40x40"));
            int n2 = FrameReader.ReadAll(asm, new object[]{ s2 }).Members.Count;
            Check(FrameReader.ReadAll(asm, new object[]{ s2, oneOf2.Reference, oneOf2 }).Members.Count == n2, "整个 2 号 + 2 号里的一根（Reference / SubOccurrence 两种）不重复计数（" + n2 + " 根）");
            Select(asm, tops.Cast<object>());
            Call(form, "ReadSelection"); Pump(2);
            L("   型材：" + Get<string>(form, "MemberText").Replace("\n", " "));
            L("   状态：" + Get<string>(form, "StatusText").Replace("\n", " | "));
            Check(Get<int>(form, "OpeningCount") >= 40, "整个围栏一起选：识别出 " + Get<int>(form, "OpeningCount") + " 个框口");
            Check(Get<string>(form, "MemberText").Contains("跳过"), "MISUMI 多实体件跳过并说明");
        } catch (Exception e) {
            L("FATAL " + e);
            fail++;
        } finally {
            if (form != null) try { form.Close(); } catch { }
            if (asm != null) try { asm.Close(false); } catch { }
            Pump(4);
            try { if (hadSettings) File.WriteAllText(settings, oldSettings); else if (File.Exists(settings)) File.Delete(settings); } catch { }
        }
        L(fail == 0 ? "ALL PASS" : fail + " FAIL");
        return fail == 0 ? 0 : 1;
    }
}
