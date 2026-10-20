using System;
using System.Collections.Generic;
using System.IO;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;

// J1..J4：H3 走通以后（种子孔用第 1 个轮廓建、AddByProfiles 传全部轮廓 → 1 个 UserDefinedPattern，Holes=0），接进插件前要知道的事。
//   J1 每个孔各自一个草图集（插件 DrillOne 就是这么建轮廓的），种子孔用第 1 个轮廓 —— 能不能照样包成一个 UDP
//   J2 H3 + 盲孔 6mm + M6 螺纹（插件的"普通孔 + 螺纹数据"写法）—— 盲孔、螺纹能不能带过去
//   J3 H3 建完：GetInputFeatures 拿输入孔，换种子孔的 HoleData（Φ9）→ 整组跟不跟；再单换第 2 个输入孔
//   J4 H3 建完：改名、挂属性、同一面再包一个 UDP、两个 UDP 编进一个特征组、散组、删 UDP（孔、轮廓还剩什么）
partial class AsmDrivenHoleProbe {
    static readonly double[][] Js = { new[]{0.05,0.05}, new[]{0.08,0.05}, new[]{0.05,0.08} };

    static void J(string mode){
        if (mode == "JA" || mode == "JB" || mode == "JC") { JX(mode); return; }
        if (string.CompareOrdinal(mode, "J5") >= 0) { JF(mode); return; }
        var p = NewPlate(0.1, 0.1, 0.01);
        double v0 = Vol(p.Part);
        var plane = p.Part.RefPlanes.AddParallelByDistance(FaceAtZ(p.Part, 0.01), 0.0, P.ReferenceElementConstants.igNormalSide, M, M, M, M);
        var profs = mode == "J1" ? SeparateProfiles(p, plane, Js) : SharedProfiles(p, plane, Js);
        Say("   " + mode + " 轮廓 " + profs.Count + " 个，草图集 " + p.Part.ProfileSets.Count + " 个");
        P.Hole seed;
        if (mode == "J2") seed = Blind(p, profs[0], TappedM6(p.Part.HoleDataCollection), 0.006);
        else seed = Through(p, profs[0], Regular(p.Part.HoleDataCollection, 0.006));
        if (seed == null) { Say("RESULT " + mode + " 种子孔没建成"); p.Part.Close(false); return; }
        var u = Wrap(p, mode, profs, seed);
        if (u == null) { p.Part.Close(false); return; }
        ReadUdp(p, mode, u, v0);

        if (mode == "J2") {
            // 理论：3 × Φ5 × 6 深（盲孔 V 底按 118° 算一个锥尖）
            double r = 0.0025, cyl = Math.PI * r * r * 0.006, cone = Math.PI * r * r * (r / Math.Tan(59 * Math.PI / 180)) / 3;
            Say("RESULT J2 切除 " + SafeStr2(() => ((v0 - Vol(p.Part)) * 1e9).ToString("0.0")) + " mm³，理论 3×(柱+锥)=" + (3 * (cyl + cone) * 1e9).ToString("0.0") + " / 只算柱=" + (3 * cyl * 1e9).ToString("0.0"));
        }
        if (mode == "J3") {
            var inputs = Inputs(u);
            Say("   J3 换种子孔 Φ9=" + SafeStr2(() => { var d9 = Regular(p.Part.HoleDataCollection, 0.009); inputs[0].HoleData = d9; ((P.HoleData)inputs[0].HoleData).HoleDiameter = 0.009; p.Model.Recompute(); return "OK"; }));
            Say("RESULT J3 换种子后 状态=" + Status(u) + " 圆 " + SafeStr2(() => Circles(p.Part)));
            ReadInputs("J3 换种子后", u);
            if (inputs.Count > 1) {
                Say("   J3 单换第 2 个输入孔 Φ4=" + SafeStr2(() => { var d4 = Regular(p.Part.HoleDataCollection, 0.004); inputs[1].HoleData = d4; ((P.HoleData)inputs[1].HoleData).HoleDiameter = 0.004; p.Model.Recompute(); return "OK"; }));
                Say("RESULT J3 单换第 2 个后 状态=" + Status(u) + " 圆 " + SafeStr2(() => Circles(p.Part)));
                ReadInputs("J3 单换后", u);
            }
            Say("   J3 改种子孔深度/盲孔：" + SafeStr2(() => { inputs[0].ExtentType = P.FeaturePropertyConstants.igFinite; inputs[0].Depth = 0.004; p.Model.Recompute(); return "OK 状态=" + Status(u); }));
        }
        if (mode == "J4") {
            Say("   J4 改名=" + SafeStr2(() => { u.Name = "M6 螺纹孔 ×3"; return u.Name; }));
            Say("   J4 挂属性=" + SafeStr2(() => { var sets = (SolidEdgeFramework.AttributeSets)u.AttributeSets; var s = sets.Add("TGAutoHoleSpec"); var a = s.Add("Key", SolidEdgeFramework.AttributeTypeConstants.seStringUnicode); a.Value = "k1"; return u.get_IsAttributeSetPresent("TGAutoHoleSpec").ToString(); }));
            // 第二批：同一面再 2 个孔，另一个 UDP
            var more = SharedProfiles(p, plane, new[]{ new[]{0.02,0.02}, new[]{0.02,0.08} });
            var seed2 = Through(p, more[0], Regular(p.Part.HoleDataCollection, 0.006));
            var u2 = seed2 == null ? null : Wrap(p, "J4b", more, seed2);
            Say("   J4 第二个 UDP " + (u2 == null ? "没建成" : "OK " + Status(u2)) + " Holes=" + SafeStr2(() => p.Model.Holes.Count.ToString()) + " UDP=" + p.Model.UserDefinedPatterns.Count);
            P.FeatureGroup g = null;
            if (u2 != null) {
                Say("   J4 两个 UDP 编组=" + SafeStr2(() => { Array arr = new object[]{ u, u2 }; g = p.Model.FeatureGroups.AddFeatureGroupsBySet(2, ref arr); return g == null ? "null" : g.Count + " 个成员 名=" + g.Name; }));
                Say("   J4 组成员类型=" + SafeStr2(() => { var l = new List<string>(); for (int i = 1; i <= g.Count; i++) l.Add(Microsoft.VisualBasic.Information.TypeName(g.Item(i))); return string.Join(",", l.ToArray()); }));
                Say("   J4 组改名=" + SafeStr2(() => { g.Name = "M6 ×5"; return g.Name; }));
                Say("   J4 散组=" + SafeStr2(() => { g.Ungroup(); return "OK 组数=" + p.Model.FeatureGroups.Count; }));
                Say("   J4 UDP+UDP 按范围编组=" + SafeStr2(() => { var g2 = p.Model.FeatureGroups.AddFeatureGroupsByRange(u, u2); return g2.Count + " 个成员"; }));
            }
            Say("   J4 SystemName 改名后=" + SafeStr2(() => u.SystemName) + " 输入孔名=" + SafeStr2(() => { var l = new List<string>(); foreach (var h in Inputs(u)) l.Add(h.Name + "/" + h.SystemName); return string.Join(",", l.ToArray()); }));
            double before = v0 - Vol(p.Part);
            Say("   J4 删前 草图集=" + p.Part.ProfileSets.Count + " 切除≈" + (before / (Math.PI * 0.003 * 0.003 * 0.01)).ToString("0.00"));
            Say("   J4 删第一个 UDP=" + SafeStr2(() => { foreach (P.FeatureGroup fg in p.Model.FeatureGroups) { try { fg.Ungroup(); } catch { } } u.Delete(); return "OK"; }));
            Say("RESULT J4 删后 Holes=" + SafeStr2(() => p.Model.Holes.Count.ToString()) + " UDP=" + SafeStr2(() => p.Model.UserDefinedPatterns.Count.ToString()) + " 草图集=" + SafeStr2(() => p.Part.ProfileSets.Count.ToString())
                + " 切除≈" + SafeStr2(() => ((v0 - Vol(p.Part)) / (Math.PI * 0.003 * 0.003 * 0.01)).ToString("0.00")) + " 圆 " + SafeStr2(() => Circles(p.Part)));
            Say("   J4 删后特征树 " + Tree(p));
        }
        try { p.Part.SaveAs(Path.Combine(Out, mode + "-udp.par")); Say("   已存 " + mode + "-udp.par"); } catch { }
        try { p.Part.Close(false); } catch { }
    }

    static List<P.Profile> SharedProfiles(Plate p, P.RefPlane plane, double[][] pts){
        var ps = p.Part.ProfileSets.Add();
        var prof = ps.Profiles.Add(plane);
        foreach (var c in pts) { double x2, y2; prof.Convert3DCoordinate(c[0], c[1], 0.01, out x2, out y2); prof.Holes2d.Add(x2, y2); }
        prof.End(P.ProfileValidationType.igProfileClosed);
        var all = new List<P.Profile>();
        for (int j = 1; j <= ps.Profiles.Count; j++) all.Add(ps.Profiles.Item(j));
        return all;
    }
    static List<P.Profile> SeparateProfiles(Plate p, P.RefPlane plane, double[][] pts){
        var all = new List<P.Profile>();
        foreach (var c in pts) {
            var prof = p.Part.ProfileSets.Add().Profiles.Add(plane);
            double x2, y2; prof.Convert3DCoordinate(c[0], c[1], 0.01, out x2, out y2); prof.Holes2d.Add(x2, y2);
            prof.End(P.ProfileValidationType.igProfileClosed);
            all.Add(prof);
        }
        return all;
    }
    static P.UserDefinedPattern Wrap(Plate p, string mode, List<P.Profile> profs, P.Hole seed){
        try {
            Array arr = profs.ToArray();
            var u = p.Model.UserDefinedPatterns.AddByProfiles(profs.Count, arr, seed);
            Say("   " + mode + " AddByProfiles(" + profs.Count + ") → " + (u == null ? "null" : "UDP " + Status(u)));
            return u;
        } catch (Exception e) { Say("RESULT " + mode + " AddByProfiles 异常 0x" + e.HResult.ToString("X8") + " " + e.Message); return null; }
    }
    static string Status(P.UserDefinedPattern u){ return SafeStr2(() => { object d; return u.GetStatusEx(out d).ToString(); }); }
    static List<P.Hole> Inputs(P.UserDefinedPattern u){
        var l = new List<P.Hole>();
        try { Array a = new object[0]; u.GetInputFeatures(ref a); foreach (var o in a) { var h = o as P.Hole; if (h != null) l.Add(h); } } catch (Exception e) { Say("   GetInputFeatures 异常 0x" + e.HResult.ToString("X8")); }
        return l;
    }
    static void ReadInputs(string tag, P.UserDefinedPattern u){
        var l = new List<string>();
        foreach (var h in Inputs(u)) l.Add(SafeStr2(() => {
            var d = (P.HoleData)h.HoleData;
            string thr = ""; try { thr = " 螺纹=" + d.ThreadDescription + " 处理=" + d.TreatmentType; } catch { }
            string ext = ""; try { ext = " 范围=" + h.ExtentType + (h.ExtentType == P.FeaturePropertyConstants.igFinite ? " 深" + Mm(h.Depth) : ""); } catch { }
            object desc; return h.Name + "[" + h.GetStatusEx(out desc) + "] Φ" + Mm(d.HoleDiameter) + thr + ext + " 轮廓=" + SafeStr2(() => ((P.Profile)h.Profile).Holes2d.Count + "孔心");
        }));
        Say("   " + tag + " 输入孔 " + l.Count + "：" + string.Join(" | ", l.ToArray()));
    }
    static void ReadUdp(Plate p, string mode, P.UserDefinedPattern u, double v0){
        Say("   " + mode + " UDP 名=" + SafeStr2(() => u.Name) + " 输入=" + SafeStr2(() => u.NumberInputFeatures.ToString()) + " 实例=" + SafeStr2(() => { int a, b; u.GetNumberOfOccurrences(out a, out b); return a + "×" + b; }));
        Say("   " + mode + " Holes=" + SafeStr2(() => p.Model.Holes.Count.ToString()) + " UDP=" + SafeStr2(() => p.Model.UserDefinedPatterns.Count.ToString()) + " HoleGeometries=" + SafeStr2(() => p.Model.HoleGeometries.Count.ToString()));
        ReadInputs(mode, u);
        Say("RESULT " + mode + " 状态=" + Status(u) + " 切除≈" + SafeStr2(() => ((v0 - Vol(p.Part)) / (Math.PI * 0.003 * 0.003 * 0.01)).ToString("0.00")) + "（按 Φ6×10 折算）圆 " + SafeStr2(() => Circles(p.Part)));
        Say("   " + mode + " 特征树 " + Tree(p));
    }
    static string Tree(Plate p){
        return SafeStr2(() => { var f = p.Part.DesignEdgebarFeatures; var l = new List<string>(); for (int i = 1; i <= f.Count; i++) l.Add(SafeStr2(() => (string)((dynamic)f.Item(i)).Name) + ":" + SafeStr2(() => Microsoft.VisualBasic.Information.TypeName(f.Item(i)))); return string.Join(" → ", l.ToArray()); });
    }
    // 插件 AutoHoleWriter 的螺纹孔写法：普通孔 Φ5 + 攻丝 + 螺纹全长，再补公称直径和描述
    static P.HoleData TappedM6(P.HoleDataCollection coll){
        var d = coll.AddEx(P.FeaturePropertyConstants.igRegularHole,
            "ISO Metric", M, M, M, 0.005,
            0.0, 0.0, 0.0, 0.0, 0.0,
            P.FeaturePropertyConstants.igTappedHole, M, M, 0.005, P.FeaturePropertyConstants.igThroughAll, M, P.FeaturePropertyConstants.igVBottomDimToFlat, M,
            M, M, M, M, M, true,
            M, M, M, M, M, M, M, M, M, M, M, M);
        try { d.ThreadExternalDiameter = 0.006; } catch { }
        try { d.ThreadDescription = "M6"; } catch { }
        return d;
    }
}
