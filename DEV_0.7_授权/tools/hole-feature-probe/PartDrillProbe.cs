using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using TianGongCadSuite;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;
using F=SolidEdgeFramework;

// 0.7.12 现场：YF25.07G01-L1-M30-02-07.par 上自动打孔每个孔都报「这个位置已经有一个孔了」——
// 日志里是 CAD 建孔两侧都 0x80010108、面片建基准面 E_FAIL，插件自己的「已有孔」预检并没有拦。
// 单独打开零件副本（不保存），量：模型/实体数、建模方式、特征树、构造体；每个实体最大的几张平面上走插件真实路径打一个 Φ6.6 通孔。
// 用法: PartDrillProbe.exe <输出目录> <零件副本路径>
class PartDrillProbe {
    static F.Application app;
    static void L(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    static void Pump(int n){ for (int i = 0; i < n; i++) { try { app.DoIdle(); } catch { } try { Application.DoEvents(); } catch { } Thread.Sleep(60); } }
    static string Hr(Exception e){ return "0x" + e.HResult.ToString("X8") + " " + e.Message; }
    static T Try<T>(Func<T> f, T dflt){ try { return f(); } catch { return dflt; } }
    static string Mm(V3 v){ return "(" + (v.X * 1000).ToString("0.##") + ", " + (v.Y * 1000).ToString("0.##") + ", " + (v.Z * 1000).ToString("0.##") + ")"; }

    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        TianGongCadSuite.Licensing.LicenseTestHooks.GateOverride = 0;
        ProbeFilter3.Register();
        string path = args[1];
        try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); }
        catch (Exception e) { L("连不上 CAD：" + e.Message); return 2; }
        try { app.DisplayAlerts = false; } catch { }
        L("已连上 CAD " + app.Version);
        if (args.Length > 2 && args[2] == "active") { ActiveRuns(path); L("PROBE DONE"); return 0; }
        if (args.Length > 2 && args[2] == "multi") { MultiRuns(path); L("PROBE DONE"); return 0; }
        if (args.Length > 2 && args[2] == "facts") { Facts(path); L("PROBE DONE"); return 0; }
        P.PartDocument part = null;
        try {
            part = (P.PartDocument)app.Documents.Open(path);
            Pump(20);
            L("打开 " + Path.GetFileName(path) + " ReadOnly=" + Try(() => part.ReadOnly.ToString(), "?"));
            Overview(part);
            L(""); L("② 每个实体最大的平面上打 Φ6.6 通孔（插件 AutoHoleWriter.Drill 真实路径）");
            int mc = Try(() => part.Models.Count, 0);
            for (int m = 1; m <= mc; m++) {
                var model = (P.Model)part.Models.Item(m);
                var body = Try(() => (G.Body)model.Body, null);
                if (body == null) { L("  模型 " + m + "：读不到实体"); continue; }
                int k = 0;
                foreach (var face in BigPlanes(body, 3)) {
                    k++;
                    DrillAt(part, "模型" + m + "·平面" + k, face);
                }
            }
        } catch (Exception e) {
            L("PROBE 异常：" + e.GetType().Name + " " + Hr(e) + "\r\n" + e.StackTrace);
        } finally {
            try { if (part != null) part.Close(false); } catch { }
            Pump(4);
        }
        L("PROBE DONE");
        return 0;
    }

    static void Overview(P.PartDocument part){
        L(""); L("① 概况");
        L("  ModelingMode=" + Try(() => part.ModelingMode.ToString(), "?") + " Models=" + Try(() => part.Models.Count.ToString(), "?")
          + " Constructions=" + Try(() => part.Constructions.Count.ToString(), "?") + " RefPlanes=" + Try(() => part.RefPlanes.Count.ToString(), "?"));
        bool has = false; try { part.HasInterpartLinks(ref has); } catch { }
        L("  HasInterpartLinks=" + has + " 零件间复制=" + Try(() => part.Constructions.InterpartConstructions.Count.ToString(), "?")
          + " 零件副本=" + Try(() => ((dynamic)part.Constructions).CopiedParts.Count.ToString(), "?"));
        int mc = Try(() => part.Models.Count, 0);
        for (int m = 1; m <= mc; m++) {
            var model = (P.Model)part.Models.Item(m);
            var body = Try(() => (G.Body)model.Body, null);
            int faces = body == null ? -1 : Try(() => ((G.Faces)body.get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll)).Count, -1);
            int planes = 0, cyl = 0;
            if (body != null) foreach (G.Face f in (G.Faces)body.get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll)) {
                object g = Try<object>(() => f.Geometry, null);
                if (g is G.Plane) planes++; else if (g is G.Cylinder) cyl++;
            }
            L("  模型 " + m + "：" + Try(() => model.Name, "?") + " 面=" + faces + "（平面 " + planes + "，圆柱 " + cyl + "）孔特征=" + Try(() => model.Holes.Count.ToString(), "?")
              + " 体积=" + Try(() => (body.Volume * 1e9).ToString("0") + "mm³", "?") + " Solid=" + Try(() => body.IsSolid.ToString(), "?")
              + " Shells=" + Try(() => ((G.Shells)body.Shells).Count.ToString(), "?"));
        }
        for (int i = 1; i <= Try(() => part.Constructions.Count, 0) && i <= 20; i++) {
            object c = Try<object>(() => part.Constructions.Item(i), null);
            L("  构造 " + i + "：" + Try(() => (string)((dynamic)c).Name, "?") + " 类型=" + Try(() => ((dynamic)c).Type.ToString(), "?"));
        }
        try {
            var tree = (P.EdgebarFeatures)((dynamic)part).DesignEdgebarFeatures;
            int bad = 0;
            for (int i = 1; i <= tree.Count; i++) {
                object f = tree.Item(i); string nm = Try(() => (string)((dynamic)f).Name, "?"), st = "";
                try { object d; st = ((dynamic)f).GetStatusEx(out d).ToString(); } catch { try { st = ((dynamic)f).Status.ToString(); } catch { st = "-"; } }
                bool ok = st == "-" || st.IndexOf("OK", StringComparison.OrdinalIgnoreCase) >= 0 || st == "1216476310";
                if (!ok) bad++;
                L("  特征 " + i + "/" + tree.Count + " " + nm + " 类型=" + Try(() => ((dynamic)f).Type.ToString(), "?") + " 状态=" + st);
            }
            L("  特征树 " + tree.Count + " 项，状态不正常 " + bad + " 项");
        } catch (Exception e) { L("  读特征树失败 " + Hr(e)); }
    }

    // 面积最大的几张平面（法向不同的优先：一块板的正反面算一张就够）
    static List<G.Face> BigPlanes(G.Body body, int n){
        var all = new List<Tuple<double, G.Face>>();
        foreach (G.Face f in (G.Faces)body.get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll)) {
            if (!(Try<object>(() => f.Geometry, null) is G.Plane)) continue;
            all.Add(Tuple.Create(Try(() => f.Area, 0.0), f));
        }
        return all.OrderByDescending(t => t.Item1).Take(n).Select(t => t.Item2).ToList();
    }

    static void DrillAt(P.PartDocument part, string label, G.Face face){
        L(""); L("  [" + label + "] 面积 " + (Try(() => face.Area, 0) * 1e6).ToString("0") + "mm²");
        try {
            var plane = (G.Plane)face.Geometry;
            Array p = new double[3], nv = new double[3]; plane.GetPlaneData(ref p, ref nv);
            var centre = Inside(face);
            var normal = V3.From(nv);
            L("    孔心 " + Mm(centre) + " 法向 " + normal.X.ToString("0.###") + "," + normal.Y.ToString("0.###") + "," + normal.Z.ToString("0.###")
              + " IsParamReversed=" + Try(() => face.IsParamReversed.ToString(), "?"));
            // ScanSpots / HoleSpots.At 是 internal：反射调
            var bf = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance;
            object spots = typeof(AutoHoleWriter).GetMethod("ScanSpots", bf).Invoke(null, new object[]{ part.Models.Item(1) });   // 0.7.13 起按实体扫
            var at = new object[]{ centre, normal, 0.0 };
            bool exists = (bool)spots.GetType().GetMethod("At", bf).Invoke(spots, at);
            L("    插件预检「已有孔」=" + exists + (exists ? " Φ" + ((double)at[2]).ToString("0.##") : ""));
            var target = new TargetFace {
                Plane = new PlaneInput(centre, normal, "打孔面"), Face = face, Part = part,
                Placement = Transform.Identity, Pick = face, PartName = Try(() => part.Name, "零件"), Label = label
            };
            int before = Try(() => ((P.Model)part.Models.Item(1)).Holes.Count, -1);
            var spec = new HoleSpec { Kind = HoleKind.Through, HoleDiameter = 6.6 };
            var r = AutoHoleWriter.Drill(target, spec, new[]{ centre });
            Pump(4);
            L("    建了 " + r.Created + "/" + r.Requested + "，模型1 孔特征 " + before + " → " + Try(() => ((P.Model)part.Models.Item(1)).Holes.Count, -1)
              + (r.Failures.Count > 0 ? "，失败：" + string.Join("；", r.Failures) : ""));
            L("    RESULT " + label + "：" + (r.Created > 0 ? "OK" : "FAIL"));
        } catch (Exception e) {
            L("    异常 " + e.GetType().Name + " " + Hr(e) + "\r\n" + e.StackTrace);
            L("    RESULT " + label + "：EXC");
        }
    }

    // 第二轮（参数 active）：多实体零件里孔特征加到哪个实体上？
    //   每轮重新打开副本（失败一次面片对象就失效），对实体 m 最大的平面：
    //   X 不激活，用模型 m 的 Holes 打；Y 先 MakeActive(m) 再用模型 m 打；Z 先 MakeActive(m)、仍用模型 1 打（= 插件现在的写法）
    static readonly System.Reflection.BindingFlags Bf = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static;
    static void ActiveRuns(string path){
        var bodies = new[]{ 1, 6, 39 };
        foreach (var m in bodies) foreach (var mode in new[]{ "X", "Y", "Z" }) {
            P.PartDocument part = null;
            try {
                part = (P.PartDocument)app.Documents.Open(path);
                Pump(15);
                if (m == 1 && mode == "X") {
                    var act = new List<string>();
                    for (int i = 1; i <= part.Models.Count; i++) { var mm = (P.Model)part.Models.Item(i); if (Try(() => mm.IsModelActive, false) || Try(() => mm.IsBodyActive, false)) act.Add(i + "(" + Try(() => mm.BodyName, "?") + ")"); }
                    L("打开时活动的实体：" + (act.Count == 0 ? "无" : string.Join("、", act)) + "；实体1 名=" + Try(() => ((P.Model)part.Models.Item(1)).BodyName, "?") + " BodyType=" + Try(() => ((P.Model)part.Models.Item(1)).BodyType.ToString(), "?"));
                }
                var model = (P.Model)part.Models.Item(m);
                if (mode != "X") { try { model.MakeActive(); Pump(4); L(""); L("  MakeActive(" + m + ") 后 IsModelActive=" + Try(() => model.IsModelActive.ToString(), "?") + " IsBodyActive=" + Try(() => model.IsBodyActive.ToString(), "?")); } catch (Exception e) { L("  MakeActive(" + m + ") 失败 " + Hr(e)); } }
                var face = BigPlanes((G.Body)model.Body, 1).FirstOrDefault();
                var plane = (G.Plane)face.Geometry;
                Array p = new double[3], nv = new double[3]; plane.GetPlaneData(ref p, ref nv);
                var centre = Inside(face);
                var target = new TargetFace { Plane = new PlaneInput(centre, V3.From(nv), "打孔面"), Face = face, Part = part, Placement = Transform.Identity, Pick = face };
                var rp = (P.RefPlane)typeof(AutoHoleWriter).GetMethod("FindOrCreatePlane", Bf).Invoke(null, new object[]{ part, target });
                var spec = new HoleSpec { Kind = HoleKind.Through, HoleDiameter = 6.6 };
                var data = (P.HoleData)typeof(AutoHoleWriter).GetMethod("BuildHoleData", Bf).Invoke(null, new object[]{ part, spec, new DrillResult() });
                var drillModel = mode == "Z" ? (P.Model)part.Models.Item(1) : model;
                var drill = typeof(AutoHoleWriter).GetMethods(Bf).First(x => x.Name == "DrillOne" && x.GetParameters().Length == 8);
                var a = new object[]{ part, drillModel, rp, centre, data, 0.0, null, null };
                bool ok = (bool)drill.Invoke(null, a);
                L("  RESULT 实体" + m + "·" + mode + "：" + (ok ? "OK" : "FAIL " + a[6]) + "  实体" + m + " 孔特征=" + Try(() => model.Holes.Count, -1));
            } catch (Exception e) {
                var ie = e is System.Reflection.TargetInvocationException && e.InnerException != null ? e.InnerException : e;
                L("  RESULT 实体" + m + "·" + mode + "：EXC " + Hr(ie));
            } finally {
                try { if (part != null) part.Close(false); } catch { }
                Pump(6);
            }
        }
    }

    // 第三轮（参数 multi）：Holes.AddMultiBody 显式给目标实体。BodyArray 放 Body 还是 Model、左侧还是右侧，各试一次（每次重开副本）
    static void MultiRuns(string path){
        foreach (var m in new[]{ 1, 6 }) foreach (var what in new[]{ "Body", "Model" }) foreach (var side in new[]{ P.FeaturePropertyConstants.igLeft, P.FeaturePropertyConstants.igRight }) {
            P.PartDocument part = null;
            string label = "实体" + m + "·" + what + "·" + side;
            try {
                part = (P.PartDocument)app.Documents.Open(path);
                Pump(15);
                var model = (P.Model)part.Models.Item(m);
                var face = BigPlanes((G.Body)model.Body, 1).FirstOrDefault();
                var plane = (G.Plane)face.Geometry;
                Array p = new double[3], nv = new double[3]; plane.GetPlaneData(ref p, ref nv);
                var centre = Inside(face);
                var target = new TargetFace { Plane = new PlaneInput(centre, V3.From(nv), "打孔面"), Face = face, Part = part, Placement = Transform.Identity, Pick = face };
                var rp = (P.RefPlane)typeof(AutoHoleWriter).GetMethod("FindOrCreatePlane", Bf).Invoke(null, new object[]{ part, target });
                var data = (P.HoleData)typeof(AutoHoleWriter).GetMethod("BuildHoleData", Bf).Invoke(null, new object[]{ part, new HoleSpec { Kind = HoleKind.Through, HoleDiameter = 6.6 }, new DrillResult() });
                var prof = part.ProfileSets.Add().Profiles.Add(rp);
                double x2, y2; prof.Convert3DCoordinate(centre.X, centre.Y, centre.Z, out x2, out y2);
                prof.Holes2d.Add(x2, y2);
                int end = prof.End(P.ProfileValidationType.igProfileClosed);
                Array bodies = what == "Body" ? (Array)new object[]{ model.Body } : new object[]{ model };
                var kpf = P.KeyPointExtentConstants.igTangentNormal;
                var h = model.Holes.AddMultiBody(prof, side, P.FeaturePropertyConstants.igThroughAll, 0.0, Type.Missing, ref kpf, Type.Missing, Type.Missing, data, 1, ref bodies);
                object d = null; var st = h == null ? (P.FeatureStatusConstants)0 : h.GetStatusEx(out d);
                L("  RESULT " + label + "：" + (h != null && (int)st == (int)P.FeatureStatusConstants.igFeatureOK ? "OK" : "FAIL 状态=" + st + (d == null ? "" : " " + d))
                  + " 闭合检查=" + end + " 实体" + m + " 孔特征=" + Try(() => model.Holes.Count, -1) + " 面数=" + Try(() => ((G.Faces)((G.Body)model.Body).get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll)).Count, -1));
            } catch (Exception e) {
                var ie = e is System.Reflection.TargetInvocationException && e.InnerException != null ? e.InnerException : e;
                L("  RESULT " + label + "：EXC " + Hr(ie));
            } finally {
                try { if (part != null) part.Close(false); } catch { }
                Pump(6);
            }
        }
    }

    // 第四轮（参数 facts）：多实体零件里其余写法的事实 ——
    //   F1 面片 → 实体：Face.Body.Tag 和 Model.Body.Tag 对不对得上；
    //   F2 实体 6 上 AddMultiBody 打的孔，出现在哪些 Model.Holes 里；
    //   F3 实体 6 上盲孔（igFinite）；F4 多孔合一（种子孔 AddMultiBody + UserDefinedPatterns.AddByProfiles）；
    //   F5 矩形阵列（种子孔 AddMultiBody + Patterns.AddByRectangular）；F6 特征组；F7 腰孔通切（AddThroughAll / AddThroughAllMultiBody）
    static P.Hole SeedHole(P.PartDocument part, P.Model model, P.Profile prof, P.HoleData data, double depthM){
        foreach (var side in new[]{ P.FeaturePropertyConstants.igLeft, P.FeaturePropertyConstants.igRight }) {
            try {
                Array bodies = new object[]{ model.Body };
                var kpf = P.KeyPointExtentConstants.igTangentNormal;
                var h = model.Holes.AddMultiBody(prof, side, depthM > 0 ? P.FeaturePropertyConstants.igFinite : P.FeaturePropertyConstants.igThroughAll, depthM, Type.Missing, ref kpf, Type.Missing, Type.Missing, data, 1, ref bodies);
                object d; if (h != null && (int)h.GetStatusEx(out d) == (int)P.FeatureStatusConstants.igFeatureOK) { L("    （" + side + " 成功）"); return h; }
                if (h != null) { L("    " + side + " 状态 " + h.GetStatusEx(out d)); try { h.Delete(); } catch { } }
                return null;   // 轮廓已被作废，调用方换新的
            } catch (Exception e) { L("    " + side + " 异常 " + Hr(e)); return null; }
        }
        return null;
    }
    static void Facts(string path){
        P.PartDocument part = (P.PartDocument)app.Documents.Open(path);
        Pump(15);
        try {
            int mc = part.Models.Count;
            var m6 = (P.Model)part.Models.Item(6);
            var face = BigPlanes((G.Body)m6.Body, 1).First();
            // F1
            int ft = Try(() => ((G.Body)face.Body).Tag, -1);
            var tags = new List<string>();
            for (int i = 1; i <= mc; i++) { int t = Try(() => ((G.Body)((P.Model)part.Models.Item(i)).Body).Tag, -1); if (t == ft) tags.Add(i.ToString()); }
            L("F1 面片 Body.Tag=" + ft + " 对上的模型：" + string.Join(",", tags) + "  Body.Parent 是 Model=" + Try(() => (((G.Body)face.Body).Parent is P.Model).ToString(), "?")
              + " Face.Parent 类型=" + Try(() => Microsoft.VisualBasic.Information.TypeName(face.Parent), "?"));
            var plane = (G.Plane)face.Geometry;
            Array p = new double[3], nv = new double[3]; plane.GetPlaneData(ref p, ref nv);
            var centre = Inside(face);
            var target = new TargetFace { Plane = new PlaneInput(centre, V3.From(nv), "打孔面"), Face = face, Part = part, Placement = Transform.Identity, Pick = face };
            var rp = (P.RefPlane)typeof(AutoHoleWriter).GetMethod("FindOrCreatePlane", Bf).Invoke(null, new object[]{ part, target });
            var data = (P.HoleData)typeof(AutoHoleWriter).GetMethod("BuildHoleData", Bf).Invoke(null, new object[]{ part, new HoleSpec { Kind = HoleKind.Through, HoleDiameter = 6.6 }, new DrillResult() });
            Func<V3, P.Profile> profAt = c => { var pr = part.ProfileSets.Add().Profiles.Add(rp); double x, y; pr.Convert3DCoordinate(c.X, c.Y, c.Z, out x, out y); pr.Holes2d.Add(x, y); pr.End(P.ProfileValidationType.igProfileClosed); return pr; };
            // 面内另取几个点：沿基准面参考方向挪 15mm、30mm（重心附近，三角形够大时还在面上）
            Array rx = new double[3]; V3 u = new V3(1, 0, 0);
            try { Array dummy = new double[3]; rp.GetRootPoint(ref dummy); var n = V3.From(nv).Unit(); u = Math.Abs(n.X) < 0.9 ? new V3(1, 0, 0) : new V3(0, 1, 0); u = (u - n * u.Dot(n)).Unit(); } catch { }
            // F2
            var h1 = SeedHole(part, m6, profAt(centre), data, 0);
            L("F2 实体6 打孔 " + (h1 != null ? "OK" : "FAIL"));
            var counts = new List<string>();
            for (int i = 1; i <= mc; i++) { int c = Try(() => ((P.Model)part.Models.Item(i)).Holes.Count, -1); if (c != 0) counts.Add(i + ":" + c); }
            L("   各模型 Holes.Count 非 0 的：" + string.Join(" ", counts) + "  （模型1=" + Try(() => ((P.Model)part.Models.Item(1)).Holes.Count, -1) + "）");
            // F3 盲孔
            var hb = SeedHole(part, m6, profAt(centre + u * 0.012), data, 0.002);
            L("F3 实体6 盲孔 2mm " + (hb != null ? "OK" : "FAIL"));
            // F4 多孔合一
            try {
                var set = part.ProfileSets.Add(); var pr = set.Profiles.Add(rp);
                var pts = new[]{ centre - u * 0.012, centre - u * 0.024 };
                foreach (var c in pts) { double x, y; pr.Convert3DCoordinate(c.X, c.Y, c.Z, out x, out y); pr.Holes2d.Add(x, y); }
                pr.End(P.ProfileValidationType.igProfileClosed);
                var profs = new List<P.Profile>(); for (int i = 1; i <= set.Profiles.Count; i++) profs.Add((P.Profile)set.Profiles.Item(i));
                L("F4 共享轮廓拆成 " + profs.Count + " 个");
                var seed = SeedHole(part, m6, profs[0], data, 0);
                if (seed == null) L("F4 种子孔 FAIL");
                else {
                    Array arr = profs.ToArray();
                    foreach (var who in new[]{ 6, 1 }) {
                        try {
                            var mm = (P.Model)part.Models.Item(who);
                            var udp = mm.UserDefinedPatterns.AddByProfiles(profs.Count, arr, seed);
                            object d; L("F4 模型" + who + ".UserDefinedPatterns.AddByProfiles → " + (udp == null ? "null" : udp.GetStatusEx(out d).ToString()) + " 实体6 面数=" + ((G.Faces)((G.Body)m6.Body).get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll)).Count);
                            if (udp != null) break;
                        } catch (Exception e) { L("F4 模型" + who + " AddByProfiles 异常 " + Hr(e)); }
                    }
                }
            } catch (Exception e) { L("F4 异常 " + Hr(e)); }
            // F5 矩形阵列
            try {
                var seed = SeedHole(part, m6, profAt(centre + u * 0.024), data, 0);
                if (seed == null) L("F5 种子孔 FAIL");
                else foreach (var who in new[]{ 6, 1 }) {
                    try {
                        var mm = (P.Model)part.Models.Item(who);
                        int before = ((G.Faces)((G.Body)m6.Body).get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll)).Count;
                        var pat = mm.Patterns.AddByRectangular(1, new object[]{ seed }, rp, 2, 1, 0.008, 0.01, 0.0, SolidEdgeFrameworkSupport.PatternOffsetTypeConstants.sePatternFixedOffset, 1);
                        object d; L("F5 模型" + who + ".Patterns.AddByRectangular → " + (pat == null ? "null" : pat.GetStatusEx(out d).ToString()) + " 实体6 面数 " + before + "→" + ((G.Faces)((G.Body)m6.Body).get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll)).Count);
                        if (pat != null) break;
                    } catch (Exception e) { L("F5 模型" + who + " AddByRectangular 异常 " + Hr(e)); }
                }
            } catch (Exception e) { L("F5 异常 " + Hr(e)); }
            // F6 特征组
            try {
                var hs = new List<object>();
                for (int i = 1; i <= mc && hs.Count < 2; i++) { var mm = (P.Model)part.Models.Item(i); for (int k = 1; k <= Try(() => mm.Holes.Count, 0) && hs.Count < 2; k++) hs.Add(mm.Holes.Item(k)); }
                if (hs.Count >= 2) {
                    Array arr = hs.ToArray();
                    foreach (var who in new[]{ 6, 1 }) {
                        try { var g = ((P.Model)part.Models.Item(who)).FeatureGroups.AddFeatureGroupsBySet(2, ref arr); L("F6 模型" + who + ".FeatureGroups.Add → " + (g == null ? "null" : "OK") + "  各模型 FeatureGroups.Count：模型1=" + ((P.Model)part.Models.Item(1)).FeatureGroups.Count + " 模型6=" + m6.FeatureGroups.Count); if (g != null) break; }
                        catch (Exception e) { L("F6 模型" + who + " 异常 " + Hr(e)); }
                    }
                } else L("F6 孔不够 2 个");
            } catch (Exception e) { L("F6 异常 " + Hr(e)); }
            // F7 腰孔通切
            foreach (var mode in new[]{ "AddThroughAll", "MultiBody" }) {
                try {
                    var pr = part.ProfileSets.Add().Profiles.Add(rp);
                    var c = centre + u * 0.036; double x, y; pr.Convert3DCoordinate(c.X, c.Y, c.Z, out x, out y);
                    pr.Circles2d.AddByCenterRadius(x, y, 0.003);
                    pr.End(P.ProfileValidationType.igProfileClosed);
                    P.ExtrudedCutout cut = null;
                    foreach (var side in new[]{ P.FeaturePropertyConstants.igLeft, P.FeaturePropertyConstants.igRight }) {
                        try {
                            if (mode == "AddThroughAll") cut = m6.ExtrudedCutouts.AddThroughAll(pr, P.FeaturePropertyConstants.igRight, side);
                            else { Array profs = new object[]{ pr }; Array bodies = new object[]{ m6.Body }; cut = m6.ExtrudedCutouts.AddThroughAllMultiBody(1, ref profs, P.FeaturePropertyConstants.igRight, side, 1, ref bodies); }
                            object d; L("F7 " + mode + " " + side + " → " + (cut == null ? "null" : cut.GetStatusEx(out d).ToString()));
                            if (cut != null && (int)cut.GetStatusEx(out d) == (int)P.FeatureStatusConstants.igFeatureOK) break;
                        } catch (Exception e) { L("F7 " + mode + " " + side + " 异常 " + Hr(e)); break; }
                    }
                } catch (Exception e) { L("F7 " + mode + " 异常 " + Hr(e)); }
            }
        } catch (Exception e) { L("FACTS 异常 " + Hr(e) + "\r\n" + e.StackTrace); }
        finally { try { part.Close(false); } catch { } Pump(4); }
    }

    // 面上面积最大那个三角形的重心（一定在面上，也不会落在已有孔里）
    static V3 Inside(G.Face face){
        int n = 0; Array pts = new double[0]; object nrm = null, tex = null;
        face.GetFacetData(0.0005, out n, ref pts, out nrm, out tex);
        double best = 0; V3 c = new V3();
        for (int t = 0; t + 8 < pts.Length; t += 9) {
            var p0 = P3(pts, t); var p1 = P3(pts, t + 3); var p2 = P3(pts, t + 6);
            double a = (p1 - p0).Cross(p2 - p0).Length;
            if (a > best) { best = a; c = (p0 + p1 + p2) * (1.0 / 3.0); }
        }
        return c;
    }
    static V3 P3(Array a, int i){ return new V3(Convert.ToDouble(a.GetValue(i)), Convert.ToDouble(a.GetValue(i + 1)), Convert.ToDouble(a.GetValue(i + 2))); }
}

[ComImport, Guid("00000016-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IProbeMessageFilter3 {
    [PreserveSig] int HandleInComingCall(int type, IntPtr task, int ticks, IntPtr info);
    [PreserveSig] int RetryRejectedCall(IntPtr task, int ticks, int reject);
    [PreserveSig] int MessagePending(IntPtr task, int ticks, int pending);
}
sealed class ProbeFilter3 : IProbeMessageFilter3 {
    [DllImport("ole32.dll")] static extern int CoRegisterMessageFilter(IProbeMessageFilter3 f, out IProbeMessageFilter3 old);
    static ProbeFilter3 keep;
    public static void Register(){ keep = new ProbeFilter3(); IProbeMessageFilter3 old; CoRegisterMessageFilter(keep, out old); }
    public int HandleInComingCall(int a, IntPtr b, int c, IntPtr d){ return 0; }
    public int RetryRejectedCall(IntPtr a, int ticks, int reject){ return ticks < 120000 ? 200 : -1; }
    public int MessagePending(IntPtr a, int b, int c){ return 2; }
}
