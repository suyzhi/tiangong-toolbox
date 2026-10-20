using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using A=SolidEdgeAssembly;
using F=SolidEdgeFramework;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;
using S=SolidEdgeFrameworkSupport;

// 装配驱动零件特征（AssemblyDrivenPartFeaturesHoles）探针（2026-10-20）。
// 想回答三件事：
//   X1 装配草图里放 N 个 Hole2d，建一个"装配驱动零件孔"：B 零件里是不是 **一个** 孔特征切出 N 个孔、带不带关联（锁链）
//   X2 装配草图里 IncludeEdge(A 零件孔口圆边的装配引用) + Hole2d 同心约束：挪 A 以后 B 的孔会不会自己跟着走
//   X3 盲孔 / 改 HoleData（改孔型）在装配驱动孔上能不能用
// 夹具都是新建文档，落在输出目录里。
class AsmDrivenHoleProbe {
    static object M = Type.Missing;
    static string Out = ".";
    static F.Application app;
    const string Tpl = @"C:\Program Files\NDS\TianGong 2025\Template\ISO Metric\";

    static void Say(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    static double D(Array a, int i){ return Convert.ToDouble(a.GetValue(i)); }
    static string Mm(double m){ return (m * 1000.0).ToString("0.###"); }

    static int Main(string[] args){
        Out = args.Length > 0 ? args[0] : ".";
        Directory.CreateDirectory(Out);
        app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application");
        Say("CAD " + app.Version);
        try { app.DisplayAlerts = false; } catch { }
        Run("X 装配驱动零件孔", X);
        Say("PROBE DONE");
        return 0;
    }
    static void Run(string name, Action a){
        Say(""); Say("==== " + name);
        try { a(); } catch (Exception e) { Say("FATAL " + name + " : " + e.GetType().Name + " 0x" + e.HResult.ToString("X8") + " " + e.Message + "\n" + e.StackTrace); }
    }

    sealed class Plate { public P.PartDocument Part; public P.Model Model; public double Top; }
    static Plate NewPlate(double w, double h, double t){
        var part = (P.PartDocument)app.Documents.Add("SolidEdge.PartDocument", Tpl + "iso metric part.par");
        part.ModelingMode = P.ModelingModeConstants.seModelingModeOrdered;
        var prof = part.ProfileSets.Add().Profiles.Add(FindXY(part));
        var L = new S.Line2d[4];
        L[0] = prof.Lines2d.AddBy2Points(0, 0, w, 0); L[1] = prof.Lines2d.AddBy2Points(w, 0, w, h);
        L[2] = prof.Lines2d.AddBy2Points(w, h, 0, h); L[3] = prof.Lines2d.AddBy2Points(0, h, 0, 0);
        var rel = (S.Relations2d)prof.Relations2d;
        for (int i = 0; i < 4; i++) rel.AddKeypoint(L[i], (int)SolidEdgeConstants.KeypointIndexConstants.igLineEnd, L[(i+1)%4], (int)SolidEdgeConstants.KeypointIndexConstants.igLineStart);
        if (prof.End(P.ProfileValidationType.igProfileClosed) != 0) throw new InvalidOperationException("底板不闭合");
        Array arr = new object[]{ prof };
        var model = part.Models.AddFiniteExtrudedProtrusion(1, ref arr, P.FeaturePropertyConstants.igRight, t);
        return new Plate { Part = part, Model = model, Top = t };
    }
    static P.RefPlane FindXY(P.PartDocument part){
        foreach (P.RefPlane c in part.RefPlanes) {
            Array n = new double[3], p = new double[3];
            c.GetNormal(ref n); c.GetRootPoint(ref p);
            if (Math.Abs(D(n,2)-1) < 1e-8 && Math.Abs(D(p,2)) < 1e-8) return c;
        }
        return null;
    }
    static P.HoleData Regular(P.HoleDataCollection coll, double diaM){
        return coll.AddEx(P.FeaturePropertyConstants.igRegularHole,
            "ISO Metric", M, M, M, diaM,
            0.0, 0.0, 0.0, 0.0, 0.0,
            P.FeaturePropertyConstants.igNone, M, M, M, M, M, P.FeaturePropertyConstants.igVBottomDimToFlat, M,
            M, M, M, M, M, true,
            M, M, M, M, M, M, M, M, M, M, M, M);
    }
    static P.HoleData Counterbore(P.HoleDataCollection coll, double diaM, double cbM, double cbDepM){
        return coll.AddEx(P.FeaturePropertyConstants.igCounterboreHole,
            "ISO Metric", M, M, M, diaM,
            cbM, cbDepM, 0.0, 0.0, 0.0,
            P.FeaturePropertyConstants.igNone, M, M, M, M, M, P.FeaturePropertyConstants.igVBottomDimToFlat, M,
            P.FeaturePropertyConstants.igCounterboreProfileIsAtTop, M, M, M, M, true,
            M, M, M, M, M, M, M, M, M, M, M, M);
    }
    static P.Hole Through(Plate p, P.Profile prof, P.HoleData data){
        foreach (var side in new[]{ P.FeaturePropertyConstants.igLeft, P.FeaturePropertyConstants.igRight }) {
            P.Hole h = null;
            try { h = p.Model.Holes.AddThroughAll(prof, side, data); } catch { continue; }
            object desc; var st = h.GetStatusEx(out desc);
            if ((int)st == (int)P.FeatureStatusConstants.igFeatureOK) return h;
            try { h.Delete(); } catch { }
        }
        return null;
    }
    static double Vol(P.PartDocument part){ return ((G.Body)((P.Model)part.Models.Item(1)).Body).Volume; }
    // 零件局部：所有圆边（去重后按 x 排）
    static string Circles(P.PartDocument part){
        var list = new List<string>();
        var seen = new HashSet<string>();
        foreach (G.Edge e in (G.Edges)((G.Body)((P.Model)part.Models.Item(1)).Body).get_Edges(G.FeatureTopologyQueryTypeConstants.igQueryAll)) {
            var c = e.Geometry as G.Circle; if (c == null) continue;
            Array cc = new double[3], ax = new double[3]; double r; c.GetCircleData(ref cc, ref ax, out r);
            string k = "(" + Mm(D(cc,0)) + "," + Mm(D(cc,1)) + " Φ" + Mm(r*2) + ")";
            if (seen.Add(k)) list.Add(k);
        }
        list.Sort();
        return string.Join(" ", list.ToArray());
    }
    static G.Edge TopCircle(P.PartDocument part, double x, double y, double z){
        foreach (G.Edge e in (G.Edges)((G.Body)((P.Model)part.Models.Item(1)).Body).get_Edges(G.FeatureTopologyQueryTypeConstants.igQueryAll)) {
            var c = e.Geometry as G.Circle; if (c == null) continue;
            Array cc = new double[3], ax = new double[3]; double r; c.GetCircleData(ref cc, ref ax, out r);
            if (Math.Abs(D(cc,0)-x) < 1e-6 && Math.Abs(D(cc,1)-y) < 1e-6 && Math.Abs(D(cc,2)-z) < 1e-6) return e;
        }
        return null;
    }
    static G.Face FaceAtZ(P.PartDocument part, double z){
        foreach (G.Face f in (G.Faces)((G.Body)((P.Model)part.Models.Item(1)).Body).get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll)) {
            var pl = f.Geometry as G.Plane; if (pl == null) continue;
            Array a = new double[3], n = new double[3]; pl.GetPlaneData(ref a, ref n);
            if (Math.Abs(Math.Abs(D(n,2)) - 1) < 1e-6 && Math.Abs(D(a,2) - z) < 1e-9) return f;
        }
        return null;
    }
    static void Links(string tag, P.PartDocument part){
        try { bool has = false; part.HasInterpartLinks(ref has); Say("   " + tag + " HasInterpartLinks=" + has); } catch (Exception e) { Say("   " + tag + " HasInterpartLinks 异常 " + e.Message); }
        try {
            var holes = ((P.Model)part.Models.Item(1)).Holes;
            Say("   " + tag + " Holes.Count=" + holes.Count);
            for (int i = 1; i <= holes.Count; i++) {
                var h = holes.Item(i);
                string nm = ""; try { nm = h.Name; } catch { }
                bool ext = false; try { h.HasExternalLink(out ext); } catch (Exception e) { nm += " HasExternalLink异常:" + e.Message; }
                object desc; var st = h.GetStatusEx(out desc);
                int np = 0; Array profs = new object[0]; int h2 = -1;
                try { h.GetProfiles(out np, ref profs); if (np > 0) h2 = ((P.Profile)profs.GetValue(0)).Holes2d.Count; } catch { }
                Say("     孔特征 " + i + " 名=" + nm + " 状态=" + st + " HasExternalLink=" + ext + " 轮廓数=" + np + " 轮廓里Hole2d=" + h2);
            }
        } catch (Exception e) { Say("   " + tag + " 列孔异常 " + e.Message); }
    }

    static void X(){
        // A：100x100x10，两个 Φ10 通孔 (30,40) (70,40)；B：100x100x10 实心，放在 z+30
        var a = NewPlate(0.1, 0.1, 0.01);
        var pa0 = a.Part.ProfileSets.Add().Profiles.Add(a.Part.RefPlanes.AddParallelByDistance(FaceAtZ(a.Part, 0.01), 0.0, P.ReferenceElementConstants.igNormalSide, M, M, M, M));
        foreach (var c in new[]{ new[]{0.03,0.04}, new[]{0.07,0.04} }) { double x2, y2; pa0.Convert3DCoordinate(c[0], c[1], 0.01, out x2, out y2); pa0.Holes2d.Add(x2, y2); }
        pa0.End(P.ProfileValidationType.igProfileClosed);
        Say("   A 孔 " + (Through(a, pa0, Regular(a.Part.HoleDataCollection, 0.010)) == null ? "失败" : "OK") + " 圆边 " + Circles(a.Part));
        string pa = Path.Combine(Out, "X-A.par"), pb = Path.Combine(Out, "X-B.par"), pc = Path.Combine(Out, "X-C.par"), pasm = Path.Combine(Out, "X.asm");
        a.Part.SaveAs(pa); a.Part.Close(false);
        var b = NewPlate(0.1, 0.1, 0.01); b.Part.SaveAs(pb); b.Part.Close(false);
        var cpl = NewPlate(0.1, 0.1, 0.01); cpl.Part.SaveAs(pc); cpl.Part.Close(false);

        var asm = (A.AssemblyDocument)app.Documents.Add("SolidEdge.AssemblyDocument", Tpl + "iso metric assembly.asm");
        var occA = asm.Occurrences.AddByFilename(pa);
        var occB = asm.Occurrences.AddByFilename(pb);
        var occC = asm.Occurrences.AddByFilename(pc);
        Array mB = new double[]{ 1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0.03,1 }; occB.PutMatrix(ref mB, true);
        Array mC = new double[]{ 1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0.06,1 }; occC.PutMatrix(ref mC, true);
        asm.SaveAs(pasm);
        var partA = (P.PartDocument)occA.OccurrenceDocument;
        var partB = (P.PartDocument)occB.OccurrenceDocument;
        var partC = (P.PartDocument)occC.OccurrenceDocument;
        double vB0 = Vol(partB), vC0 = Vol(partC);

        // ---- 装配基准面：B 顶面（装配 z=0.04）
        var faceB = FaceAtZ(partB, 0.01);
        object refFaceB = asm.CreateReference(occB, faceB);
        A.AsmRefPlane plane = null;
        try { plane = (A.AsmRefPlane)asm.AsmRefPlanes.AddParallelByDistance(refFaceB, 0.0, P.ReferenceElementConstants.igNormalSide, M, M, M); Say("   装配基准面（B 顶面引用）OK"); }
        catch (Exception e) { Say("   装配基准面（B 顶面引用）异常 0x" + e.HResult.ToString("X8") + " " + e.Message); }
        if (plane == null) {
            // 退路：装配默认 XY 面 + 偏移 40mm
            for (int i = 1; i <= asm.AsmRefPlanes.Count && plane == null; i++) {
                var rp = (A.AsmRefPlane)asm.AsmRefPlanes.Item(i);
                Array n = new double[3]; rp.GetNormal(ref n);
                if (Math.Abs(Math.Abs(D(n,2)) - 1) < 1e-6) {
                    try { plane = (A.AsmRefPlane)asm.AsmRefPlanes.AddParallelByDistance(rp, 0.04, P.ReferenceElementConstants.igNormalSide, M, M, M); Say("   装配基准面（XY+40）OK"); }
                    catch (Exception e) { Say("   装配基准面（XY+40）异常 " + e.Message); }
                }
            }
        }
        if (plane == null) { Say("RESULT X 没有装配基准面"); return; }
        { Array n = new double[3], r = new double[3]; plane.GetNormal(ref n); plane.GetRootPoint(ref r); Say("   基准面 法向(" + D(n,0) + "," + D(n,1) + "," + D(n,2) + ") 根点 z=" + Mm(D(r,2))); }

        // ---- X1：普通 Hole2d ×3，范围 = B，贯通
        var drv = asm.AssemblyDrivenPartFeatures.AssemblyDrivenPartFeaturesHoles;
        var prof1 = asm.ProfileSets.Add().Profiles.Add(plane);
        foreach (var c in new[]{ new[]{0.02,0.08}, new[]{0.05,0.08}, new[]{0.08,0.08} }) { double x2, y2; prof1.Convert3DCoordinate(c[0], c[1], 0.04, out x2, out y2); prof1.Holes2d.Add(x2, y2); Say("   X1 加一个 Hole2d 后 Count=" + prof1.Holes2d.Count + " 2d=(" + x2 + "," + y2 + ")"); }
        Say("   X1 轮廓 End=" + prof1.End(P.ProfileValidationType.igProfileClosed) + " Hole2d=" + prof1.Holes2d.Count);
        var data1 = Regular(asm.HoleDataCollection, 0.006);
        A.AssemblyDrivenPartFeaturesHole f1 = null;
        foreach (var sd in new[]{ P.FeaturePropertyConstants.igLeft, P.FeaturePropertyConstants.igRight }) {
            try {
                Array scope = new object[]{ occB }; Array profs = new object[]{ prof1 };
                var side = sd; double depth = 0; var kp = P.KeyPointExtentConstants.igTangentNormal;
                f1 = drv.Add(1, ref scope, 1, ref profs, ref side, data1, P.FeaturePropertyConstants.igThroughAll, ref depth, M, M, M, ref kp);
                object desc = null; object st = null; try { st = f1.get_Status(out desc); } catch (Exception e) { st = "读状态异常 " + e.Message; }
                try { asm.UpdateAll(); } catch (Exception e) { Say("   UpdateAll 异常 " + e.Message); }
                try { asm.AssemblyFeatures.Recompute(0); } catch (Exception e) { Say("   Recompute 异常 " + e.Message); }
                try { app.DoIdle(); } catch { }
                try { object d2; Say("   重算后状态=" + f1.get_Status(out d2) + " " + d2); } catch (Exception e) { Say("   重算后状态异常 " + e.Message); }
                double cut = vB0 - Vol(partB);
                Say("RESULT X1 side=" + sd + " Add 成功 状态=" + st + " 切除比=" + (cut / (3 * Math.PI * 0.003 * 0.003 * 0.01)).ToString("0.000"));
                if (cut > 1e-9) break;
                try { f1.Delete(); } catch { } f1 = null;
            } catch (Exception e) { Say("RESULT X1 side=" + sd + " 异常 0x" + e.HResult.ToString("X8") + " " + e.Message); }
        }
        if (f1 != null) {
            try { int n = 0; Array pf = new object[0]; f1.GetPartFeatures(out n, out pf); Say("   X1 GetPartFeatures n=" + n + (n > 0 ? " 类型=" + Microsoft.VisualBasic.Information.TypeName(pf.GetValue(0)) : "")); } catch (Exception e) { Say("   GetPartFeatures 异常 " + e.Message); }
            try { Say("   X1 名=" + f1.Name); } catch { }
            Links("X1 B", partB);
            Say("   X1 B 圆边 " + Circles(partB));
        }

        // ---- X1b：盲孔 5mm（装配驱动）、X1c：纯装配特征孔（AssemblyFeaturesHoles）
        foreach (var variant in new[]{ "drv-finite", "asm-through", "asm-finite" }) {
            var pr = asm.ProfileSets.Add().Profiles.Add(plane);
            { double x2, y2; pr.Convert3DCoordinate(0.05, 0.02, 0.04, out x2, out y2); pr.Holes2d.Add(x2, y2); }
            pr.End(P.ProfileValidationType.igProfileClosed);
            double vb = Vol(partB);
            foreach (var sd in new[]{ P.FeaturePropertyConstants.igLeft, P.FeaturePropertyConstants.igRight }) {
                try {
                    Array scope = new object[]{ occB }; Array profs = new object[]{ pr };
                    var side = sd; double depth = 0.005; var kp = P.KeyPointExtentConstants.igTangentNormal;
                    var ext = variant.EndsWith("finite") ? P.FeaturePropertyConstants.igFinite : P.FeaturePropertyConstants.igThroughAll;
                    object st = null, desc = null;
                    if (variant.StartsWith("drv")) { var f = drv.Add(1, ref scope, 1, ref profs, ref side, Regular(asm.HoleDataCollection, 0.006), ext, ref depth, M, M, M, ref kp); try { asm.UpdateAll(); } catch { } try { st = f.get_Status(out desc); } catch (Exception e) { st = e.Message; } }
                    else { var f = asm.AssemblyFeatures.AssemblyFeaturesHoles.Add(1, ref scope, 1, ref profs, ref side, Regular(asm.HoleDataCollection, 0.006), ext, ref depth, M, M, M, ref kp); try { asm.UpdateAll(); } catch { } st = "(asm feature)"; }
                    Say("RESULT X1b " + variant + " side=" + sd + " 状态=" + st + " B 切除=" + ((vb - Vol(partB)) * 1e9).ToString("0.#") + "mm³");
                    if (vb - Vol(partB) > 1e-10) break;
                } catch (Exception e) { Say("RESULT X1b " + variant + " side=" + sd + " 异常 0x" + e.HResult.ToString("X8") + " " + e.Message); }
            }
        }

        // ---- X2：IncludeEdge(A 孔口圆边) + Hole2d 同心，范围 = C（看挪 A 跟不跟）
        var prof2 = asm.ProfileSets.Add().Profiles.Add(plane);
        var hole2ds = new List<P.Hole2d>();
        int includeOk = 0;
        foreach (var c in new[]{ new[]{0.03,0.04}, new[]{0.07,0.04} }) {
            var edge = TopCircle(partA, c[0], c[1], 0.01);
            if (edge == null) { Say("   X2 找不到 A 圆边"); continue; }
            object refE = asm.CreateReference(occA, edge);
            object inc = null;
            try { prof2.IncludeEdge(refE, out inc); includeOk++; Say("   X2 IncludeEdge OK 返回 " + (inc == null ? "null" : Microsoft.VisualBasic.Information.TypeName(inc))); }
            catch (Exception e) { Say("   X2 IncludeEdge 异常 0x" + e.HResult.ToString("X8") + " " + e.Message); }
            double x2, y2; prof2.Convert3DCoordinate(c[0], c[1], 0.04, out x2, out y2);
            var h2 = prof2.Holes2d.Add(x2, y2); hole2ds.Add(h2);
            var circ = inc ?? (prof2.Circles2d.Count > 0 ? (object)prof2.Circles2d.Item(prof2.Circles2d.Count) : null);
            if (circ != null) {
                var rel = (S.Relations2d)prof2.Relations2d;
                try { rel.AddKeypoint(h2, 0, circ, (int)SolidEdgeConstants.KeypointIndexConstants.igCircleCenter, M); Say("   X2 Keypoint(hole,0,circle,center) OK"); }
                catch (Exception e) {
                    Say("   X2 Keypoint 异常 0x" + e.HResult.ToString("X8") + " " + e.Message);
                    try { rel.AddConcentric(h2, circ, M); Say("   X2 Concentric OK"); } catch (Exception e2) { Say("   X2 Concentric 异常 0x" + e2.HResult.ToString("X8") + " " + e2.Message); }
                }
                try { prof2.ToggleConstruction(circ); Say("   X2 圆改构造线 OK"); } catch (Exception e) { Say("   X2 ToggleConstruction 异常 " + e.Message); }
            }
        }
        Say("   X2 轮廓 End=" + prof2.End(P.ProfileValidationType.igProfileClosed) + " Hole2d=" + prof2.Holes2d.Count + " Circles2d=" + prof2.Circles2d.Count);
        A.AssemblyDrivenPartFeaturesHole f2 = null;
        var data2 = Regular(asm.HoleDataCollection, 0.010);
        foreach (var sd in new[]{ P.FeaturePropertyConstants.igLeft, P.FeaturePropertyConstants.igRight }) {
            try {
                Array scope = new object[]{ occB, occC }; Array profs = new object[]{ prof2 };
                var side = sd; double depth = 0; var kp = P.KeyPointExtentConstants.igTangentNormal;
                f2 = drv.Add(2, ref scope, 1, ref profs, ref side, data2, P.FeaturePropertyConstants.igThroughAll, ref depth, M, M, M, ref kp);
                double cutB = vB0 - Vol(partB), cutC = vC0 - Vol(partC);
                Say("RESULT X2 side=" + sd + " Add 成功 C 切除=" + (cutC * 1e9).ToString("0.#") + "mm³ B 总切除=" + (cutB * 1e9).ToString("0.#"));
                if (cutC > 1e-9) break;
                try { f2.Delete(); } catch { } f2 = null;
            } catch (Exception e) { Say("RESULT X2 side=" + sd + " 异常 0x" + e.HResult.ToString("X8") + " " + e.Message); }
        }
        if (f2 != null) {
            Links("X2 C", partC);
            Say("   X2 C 圆边（挪前）" + Circles(partC));
            try { asm.Save(); } catch (Exception e) { Say("   保存异常 " + e.Message); }
            // 挪 A：x +5mm
            Array mA = new double[]{ 1,0,0,0, 0,1,0,0, 0,0,1,0, 0.005,0,0,1 };
            occA.PutMatrix(ref mA, true);
            try { asm.UpdateAll(); Say("   UpdateAll OK"); } catch (Exception e) { Say("   UpdateAll 异常 " + e.Message); }
            try { app.DoIdle(); } catch { }
            Say("RESULT X2 挪 A +5mm 后 C 圆边 " + Circles(partC));
            object desc = null; try { Say("   X2 状态 " + f2.get_Status(out desc) + " " + desc); } catch (Exception e) { Say("   X2 状态异常 " + e.Message); }
            // 再试 AssemblyFeatures.Recompute
            try { asm.AssemblyFeatures.Recompute(0); Say("   AssemblyFeatures.Recompute OK"); } catch (Exception e) { Say("   AssemblyFeatures.Recompute 异常 " + e.Message); }
            Say("RESULT X2 Recompute 后 C 圆边 " + Circles(partC));
        }

        // ---- X3：换孔型（SetHoleData）
        if (f1 != null) {
            try {
                f1.SetHoleData(Counterbore(asm.HoleDataCollection, 0.0066, 0.011, 0.004));
                try { asm.UpdateAll(); } catch { }
                Say("RESULT X3 SetHoleData 沉孔 OK，B 圆边 " + Circles(partB));
            } catch (Exception e) { Say("RESULT X3 SetHoleData 异常 0x" + e.HResult.ToString("X8") + " " + e.Message); }
            // 盲孔
            try {
                f1.SetExtentType(P.FeaturePropertyConstants.igFinite); f1.SetDepth(0.005);
                try { asm.UpdateAll(); } catch { }
                Say("RESULT X3 改盲孔 5mm 后 B 体积减少 " + ((vB0 - Vol(partB)) * 1e9).ToString("0.#"));
            } catch (Exception e) { Say("RESULT X3 改盲孔 异常 0x" + e.HResult.ToString("X8") + " " + e.Message); }
        }
        try { asm.Save(); } catch { }
        // 保存后重开看关联还在不在
        try {
            asm.Close(true);
            var re = (A.AssemblyDocument)app.Documents.Open(pasm);
            int n = 0; try { n = re.AssemblyDrivenPartFeatures.AssemblyDrivenPartFeaturesHoles.Count; } catch { }
            Say("RESULT X4 重开 装配驱动孔数=" + n);
            var reB = (P.PartDocument)((A.Occurrence)re.Occurrences.Item(2)).OccurrenceDocument;
            Links("X4 B", reB);
            re.Close(false);
        } catch (Exception e) { Say("RESULT X4 重开异常 " + e.Message); }
    }
}

