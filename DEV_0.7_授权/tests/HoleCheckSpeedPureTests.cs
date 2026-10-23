using System;
using System.Collections.Generic;

namespace TianGongCadSuite {
    // 0.8.3 配孔检查提速：包围盒预筛（只挡肯定碰不到的）、孔记录的高亮引用要用时才建。
    public static class HoleCheckSpeedPureTests {
        static void Check(bool ok, string label){ if (!ok) throw new Exception("FAIL: " + label); Console.WriteLine("PASS: " + label); }
        public static void Run(){
            var box = new Box3(new V3(0, 0, 0), new V3(0.1, 0.1, 0.01));
            var z = new V3(0, 0, 1);
            // 轴线竖着穿过板中间，孔段就在板上方 2mm：容差 3mm 内算够得着
            Check(PartRayTarget.NearBox(box, new V3(0.05, 0.05, 0.012), z, 0, 0, 0.003), "孔段离板 2mm、容差 3mm：够得着");
            Check(!PartRayTarget.NearBox(box, new V3(0.05, 0.05, 0.02), z, 0, 0, 0.003), "孔段离板 10mm：挡掉");
            Check(!PartRayTarget.NearBox(box, new V3(0.2, 0.05, 0.005), z, -1, 1, 0.003), "轴线在板外 100mm：挡掉");
            Check(PartRayTarget.NearBox(box, new V3(0.1015, 0.05, 0.005), z, -1, 1, 0.003), "轴线贴着板边 1.5mm（容差内）：够得着");
            Check(PartRayTarget.NearBox(box, new V3(0.05, 0.05, 0.005), new V3(0, 0, 0), 0, 0, 0), "方向读不出：不预筛");
            // 盒子挡掉时不再去问 CAD
            int asked = 0;
            var t = PartRayTarget.WithBoxes("板", Transform.Identity, (o, d) => { asked++; return new List<Box3> { box }; });
            t.World = box;
            Check(!t.Hits(new V3(0.5, 0.5, 0), z, -1, 1, 0.003) && asked == 0, "包围盒外：不调射线");
            Check(t.Hits(new V3(0.05, 0.05, 0.012), z, -0.001, 0.001, 0.003) && asked == 1, "包围盒内：照常调射线");
            // 高亮引用延迟建，只建一次
            int made = 0;
            var h = new HoleRecord { MakeSelection = () => { made++; return "ref"; } };
            Check(made == 0, "采集时不建引用");
            Check((string)h.Selection == "ref" && (string)h.Selection == "ref" && made == 1, "第一次用时建、只建一次");
            var bad = new HoleRecord { MakeSelection = () => { throw new InvalidOperationException(); } };
            Check(bad.Selection == null, "建引用失败返回 null 不抛");
            JunctionLoop();
            LockedSettings();
            // 规格本身打不出来：打之前就挡住
            Check(new HoleSpec { Kind = HoleKind.Counterbore, HoleDiameter = 12, CounterboreDiameter = 11, CounterboreDepth = 6.5 }.SpecProblem.Contains("沉孔直径"), "沉孔直径不大于孔径：挡住");
            Check(new HoleSpec { Kind = HoleKind.Counterbore, HoleDiameter = 6.6, CounterboreDiameter = 11, CounterboreDepth = 0 }.SpecProblem.Contains("沉孔深度"), "沉孔深度 0：挡住");
            Check(new HoleSpec { Kind = HoleKind.Countersink, HoleDiameter = 13, CountersinkDiameter = 12, CountersinkAngle = 90 }.SpecProblem.Contains("锥孔直径"), "锥孔直径不大于孔径：挡住");
            Check(new HoleSpec { Kind = HoleKind.Counterbore, HoleDiameter = 6.6, CounterboreDiameter = 11, CounterboreDepth = 6.8 }.SpecProblem.Length == 0, "正常的 M6 沉孔：放行");
            Check(new HoleSpec { Kind = HoleKind.Tapped, HoleDiameter = 5, ThreadFull = false, ThreadDepth = 20, Depth = 10 }.SpecProblem.Contains("螺纹深度"), "螺纹深度超过孔深：照旧挡住");
        }
        // 设置文件被别的程序独占打开：读不出来 → 当没有；这时不许整份重写（会冲掉原有内容）；放开后读得出来就能写
        static void LockedSettings(){
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tgsettings-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".tsv");
            SettingsFile.Write(path, new[]{ new[]{ "预设A", "孔数", "4" } }, "测试");
            bool threw = false;
            using (var hold = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.ReadWrite, System.IO.FileShare.None)) {
                Check(SettingsFile.Read(path).Count == 0, "被独占打开时读不出来，当没有（不抛）");
                try { SettingsFile.Write(path, new[]{ new[]{ "预设B", "孔数", "2" } }, "测试"); } catch (System.IO.IOException) { threw = true; }
            }
            Check(threw, "读不出来的文件不许整份重写");
            var rows = SettingsFile.Read(path);
            Check(rows.Count == 1 && rows[0][0] == "预设A", "原有内容还在");
            SettingsFile.Write(path, new[]{ new[]{ "预设A", "孔数", "4" }, new[]{ "预设B", "孔数", "2" } }, "测试");
            Check(SettingsFile.Read(path).Count == 2, "重新读成功以后可以写");
            try { System.IO.File.Delete(path); } catch { }
        }
        // 格式转换扫描目录：指回上级的目录联接不能让扫描转圈（只在 Windows 上测，需要 mklink）
        static void JunctionLoop(){
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return;
            string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tgjunction-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root, "a"));
            System.IO.File.WriteAllText(System.IO.Path.Combine(root, "a", "x.sldprt"), "x");
            string loop = System.IO.Path.Combine(root, "a", "back");
            try {
                var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c mklink /J \"" + loop + "\" \"" + root + "\"") { CreateNoWindow = true, UseShellExecute = false });
                p.WaitForExit(10000);
                if (!System.IO.Directory.Exists(loop)) { Console.WriteLine("SKIP: 建不了目录联接"); return; }
                var o = new ConvertOptions(); o.Inputs.Add(root); o.OutputRoot = root; o.Recursive = true;
                var task = System.Threading.Tasks.Task.Run(() => ConvertPlanner.Scan(o));
                Check(task.Wait(20000) && task.Result.Count == 1, "扫描遇到指回上级的目录联接不转圈，只找到 1 个文件");
            } finally {
                try { System.IO.Directory.Delete(loop); } catch { }
                try { System.IO.Directory.Delete(root, true); } catch { }
            }
        }
    }
}
