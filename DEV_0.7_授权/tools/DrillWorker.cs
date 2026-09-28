using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using TianGongCadSuite;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;
using F=SolidEdgeFramework;

// 打孔工作器：由插件面板在**独立进程**里启动，用跨进程 COM 连上 CAD 完成打孔。
//
// 为什么需要它（2026-09-26 实测）：
//   在"原位编辑零件"这种唯一可写的上下文里，
//     * 外部进程（本程序）调用 Holes.AddThroughAll  → 成功
//     * 插件进程内（CAD 自己的 UI 线程）调用同一份代码 → 一直被拒（RPC_E_DISCONNECTED）
//   所以把"写模型"这一步挪到外部进程来做。
//
// 用法（所有坐标都是**零件局部坐标**，米；由插件先用 Placement 换算好）：
//   DrillWorker.exe --part <par路径> --plane px,py,pz --normal nx,ny,nz
//                   --hole x,y,z [--hole x,y,z ...]
//                   --kind through|tapped|counterbore|countersink
//                   --dia 6.0 [--thread M6] [--depth 0] [--cbore 11 --cbdepth 6.5]
//                   [--csdia 11 --csangle 90] [--chamfer] [--flat] [--vbottom] [--vangle 118]
//                   --out <结果文件>
// 结果文件内容：OK created=N method=... / ERR message
class DrillWorker {
    static string Get(string[] a, string key, string def){
        for (int i = 0; i < a.Length - 1; i++) if (string.Equals(a[i], "--" + key, StringComparison.OrdinalIgnoreCase)) return a[i + 1];
        return def;
    }
    static bool Has(string[] a, string key){
        foreach (var s in a) if (string.Equals(s, "--" + key, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
    static V3 Vec(string text){
        var parts = text.Split(',');
        return new V3(double.Parse(parts[0], CultureInfo.InvariantCulture), double.Parse(parts[1], CultureInfo.InvariantCulture), double.Parse(parts[2], CultureInfo.InvariantCulture));
    }
    static double Num(string text, double def){ double v; return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : def; }

    [STAThread] static int Main(string[] args){
        string outPath = Get(args, "out", null);
        try {
            string partPath = Get(args, "part", null);
            if (string.IsNullOrEmpty(partPath)) return Fail(outPath, "缺少 --part");
            string planeText = Get(args, "plane", null), normalText = Get(args, "normal", null);
            if (planeText == null || normalText == null) return Fail(outPath, "缺少 --plane/--normal");

            var holes = new List<V3>();
            for (int i = 0; i < args.Length - 1; i++) if (string.Equals(args[i], "--hole", StringComparison.OrdinalIgnoreCase)) holes.Add(Vec(args[i + 1]));
            if (holes.Count == 0) return Fail(outPath, "缺少 --hole");

            var spec = new HoleSpec();
            string kind = (Get(args, "kind", "through") ?? "through").ToLowerInvariant();
            spec.Kind = kind == "tapped" ? HoleKind.Tapped : kind == "counterbore" ? HoleKind.Counterbore : kind == "countersink" ? HoleKind.Countersink : HoleKind.Through;
            spec.HoleDiameter = Num(Get(args, "dia", "6"), 6);
            spec.ThreadSize = Get(args, "thread", "") ?? "";
            spec.Depth = Num(Get(args, "depth", "0"), 0);
            spec.CounterboreDiameter = Num(Get(args, "cbore", "0"), 0);
            spec.CounterboreDepth = Num(Get(args, "cbdepth", "0"), 0);
            spec.CountersinkDiameter = Num(Get(args, "csdia", "0"), 0);
            spec.CountersinkAngle = Num(Get(args, "csangle", "90"), 90);
            spec.Chamfer = Has(args, "chamfer");
            spec.Bottom = Has(args, "vbottom") ? HoleBottom.VBottom : HoleBottom.Flat;
            if (Has(args, "vbottom")) spec.BottomAngle = Num(Get(args, "vangle", "118"), 118);

            F.Application app;
            try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); }
            catch (Exception e) { return Fail(outPath, "连不上 CAD：" + e.Message); }
            for (int i = 0; i < 8; i++){ try { app.DoIdle(); } catch {} Thread.Sleep(150); }

            var part = app.ActiveDocument as P.PartDocument;
            if (part == null) return Fail(outPath, "活动文档不是零件（请先双击零件进入原位编辑）");
            string activeName = null; try { activeName = part.FullName; } catch {}
            if (!string.IsNullOrEmpty(activeName) && !string.Equals(Path.GetFullPath(activeName), Path.GetFullPath(partPath), StringComparison.OrdinalIgnoreCase))
                return Fail(outPath, "活动零件不是目标零件：" + activeName);

            var target = new TargetFace {
                Plane = new PlaneInput(Vec(planeText), Vec(normalText).Unit(), "打孔面"),
                Face = null, Part = part, Placement = Transform.Identity, Label = "打孔面", PartName = Path.GetFileName(partPath)
            };
            // 面对象留空：AutoHoleWriter 会用 Plane 数据（纯数据）建基准面，不依赖面片对象
            var result = AutoHoleWriter.Drill(target, spec, holes);
            if (result.Created == holes.Count && result.Failures.Count == 0)
                return Ok(outPath, "created=" + result.Created + " method=" + result.Method + (result.Audit.Length > 0 ? " audit=" + result.Audit : ""));
            return Fail(outPath, "created=" + result.Created + "/" + holes.Count + " failures=" + string.Join(" | ", result.Failures.ToArray()));
        } catch (Exception e) {
            return Fail(outPath, e.GetType().Name + ": " + e.Message);
        }
    }

    static int Ok(string path, string text){
        if (!string.IsNullOrEmpty(path)) File.WriteAllText(path, "OK " + text, new UTF8Encoding(false));
        Console.WriteLine("OK " + text);
        return 0;
    }
    static int Fail(string path, string text){
        if (!string.IsNullOrEmpty(path)) File.WriteAllText(path, "ERR " + text, new UTF8Encoding(false));
        Console.WriteLine("ERR " + text);
        return 1;
    }
}
