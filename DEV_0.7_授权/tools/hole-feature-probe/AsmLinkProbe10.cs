using System;
using System.Collections.Generic;
using System.IO;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;

// H1..H6：照天工自己的"多孔孔特征"结构（UserDefinedPattern，输入 = N 个单孔 Hole）用 AddByProfiles 造。
// 0.7.5 那次 AddByProfiles 把 CAD 搞崩（传的是种子孔自己用掉的那个轮廓）；这次逐种组合试，每种一个 CAD 会话（崩了不连累别的）。
//   H1 轮廓 = End 拆出来的 3 个（都没被用过），种子 = 另一个轮廓建的孔（孔心在 (20,50)）
//   H2 种子孔用第 1 个轮廓建，轮廓 = 第 2、3 个
//   H3 种子孔用第 1 个轮廓建，轮廓 = 全部 3 个（最像天工自己的结构；最可能崩）
//   H4 种子 = HoleData 对象，轮廓 = 3 个
//   H5 H2 的做法，孔是盲孔 + 种子孔是螺纹孔 —— 看换孔型、盲孔能不能带过去
//   H6 H2 的做法，建完再用「改孔型」的路子换种子孔的 HoleData，看整组跟不跟
partial class AsmDrivenHoleProbe {
    static void H(string mode){
        var p = NewPlate(0.1, 0.1, 0.01);
        double v0 = Vol(p.Part);
        var plane = p.Part.RefPlanes.AddParallelByDistance(FaceAtZ(p.Part, 0.01), 0.0, P.ReferenceElementConstants.igNormalSide, M, M, M, M);
        var ps = p.Part.ProfileSets.Add();
        var prof = ps.Profiles.Add(plane);
        foreach (var c in new[]{ new[]{0.05,0.05}, new[]{0.08,0.05}, new[]{0.05,0.08} }) { double x2, y2; prof.Convert3DCoordinate(c[0], c[1], 0.01, out x2, out y2); prof.Holes2d.Add(x2, y2); }
        prof.End(P.ProfileValidationType.igProfileClosed);
        var all = new List<P.Profile>();
        for (int j = 1; j <= ps.Profiles.Count; j++) all.Add(ps.Profiles.Item(j));
        Say("   " + mode + " 草图集里 " + all.Count + " 个轮廓");
        var data = Regular(p.Part.HoleDataCollection, 0.006);
        object seed = null; Array profs = null; int n = 0;
        P.Hole seedHole = null;
        try {
            if (mode == "H1") {
                var ps2 = p.Part.ProfileSets.Add(); var sp = ps2.Profiles.Add(plane);
                double x2, y2; sp.Convert3DCoordinate(0.02, 0.05, 0.01, out x2, out y2); sp.Holes2d.Add(x2, y2); sp.End(P.ProfileValidationType.igProfileClosed);
                seedHole = Through(p, sp, data); seed = seedHole;
                profs = all.ToArray(); n = all.Count;
            } else if (mode == "H2" || mode == "H5" || mode == "H6") {
                var sd = mode == "H5" ? TappedData(p.Part.HoleDataCollection) : data;
                if (mode == "H5") seedHole = Blind(p, all[0], sd, 0.006); else seedHole = Through(p, all[0], sd);
                seed = seedHole;
                profs = new object[]{ all[1], all[2] }; n = 2;
            } else if (mode == "H3") {
                seedHole = Through(p, all[0], data); seed = seedHole;
                profs = all.ToArray(); n = all.Count;
            } else if (mode == "H4") {
                seed = data; profs = all.ToArray(); n = all.Count;
            }
        } catch (Exception e) { Say("RESULT " + mode + " 准备种子孔异常 0x" + e.HResult.ToString("X8") + " " + e.Message); p.Part.Close(false); return; }
        Say("   " + mode + " 种子 " + (seed == null ? "null" : Microsoft.VisualBasic.Information.TypeName(seed)) + " 切除≈" + ((v0 - Vol(p.Part)) / (Math.PI * 0.003 * 0.003 * 0.01)).ToString("0.00"));
        P.UserDefinedPattern u = null;
        try {
            Say("   " + mode + " 调 AddByProfiles(" + n + ", …)");
            u = p.Model.UserDefinedPatterns.AddByProfiles(n, profs, seed);
            Say("   " + mode + " AddByProfiles 返回 " + (u == null ? "null" : "UDP"));
        } catch (Exception e) { Say("RESULT " + mode + " AddByProfiles 异常 0x" + e.HResult.ToString("X8") + " " + e.Message); }
        // 回读：每一项单独 try（H2 第一次跑时整段被一个 E_FAIL 打断，什么都没看到）
        Say("   " + mode + " UDP 状态=" + SafeStr2(() => { object d; return u == null ? "null" : u.GetStatusEx(out d).ToString() + " " + d; }));
        Say("   " + mode + " UDP Status 属性=" + SafeStr2(() => { object d; return u == null ? "null" : u.get_Status(out d).ToString(); }));
        Say("   " + mode + " UDP 名=" + SafeStr2(() => u.Name) + " 系统名=" + SafeStr2(() => u.SystemName));
        Say("   " + mode + " 输入特征数=" + SafeStr2(() => u.NumberInputFeatures.ToString()));
        Say("   " + mode + " 实例=" + SafeStr2(() => { int a, b; u.GetNumberOfOccurrences(out a, out b); return a + "×" + b; }));
        Say("   " + mode + " Holes=" + SafeStr2(() => p.Model.Holes.Count.ToString()) + " UDP=" + SafeStr2(() => p.Model.UserDefinedPatterns.Count.ToString()) + " HoleGeometries=" + SafeStr2(() => p.Model.HoleGeometries.Count.ToString()));
        Say("   " + mode + " 重算=" + SafeStr2(() => { p.Model.Recompute(); return "OK"; }));
        Say("   " + mode + " 重算后 UDP 状态=" + SafeStr2(() => { object d; return u == null ? "null" : u.GetStatusEx(out d).ToString(); }));
        Say("RESULT " + mode + " 切出≈" + SafeStr2(() => ((v0 - Vol(p.Part)) / (Math.PI * 0.003 * 0.003 * 0.01)).ToString("0.00")) + "（按 Φ6×10 折算）顶面圆 " + SafeStr2(() => Circles(p.Part)));
        Say("   " + mode + " 特征树 " + SafeStr2(() => { var f = p.Part.DesignEdgebarFeatures; var l = new List<string>(); for (int i = 1; i <= f.Count; i++) l.Add(SafeStr2(() => (string)((dynamic)f.Item(i)).Name) + ":" + SafeStr2(() => Microsoft.VisualBasic.Information.TypeName(f.Item(i)))); return string.Join(" → ", l.ToArray()); }));
        if (mode == "H6" && u != null && seedHole != null) {
            Say("   H6 换孔型=" + SafeStr2(() => { var d9 = Regular(p.Part.HoleDataCollection, 0.009); seedHole.HoleData = d9; ((P.HoleData)seedHole.HoleData).HoleDiameter = 0.009; p.Model.Recompute(); return "OK"; }));
            Say("RESULT H6 换成 Φ9 后 UDP 状态=" + SafeStr2(() => { object d; return u.GetStatusEx(out d).ToString(); }) + " 顶面圆 " + SafeStr2(() => Circles(p.Part)) + " 切出≈" + SafeStr2(() => ((v0 - Vol(p.Part)) / (Math.PI * 0.003 * 0.003 * 0.01)).ToString("0.00")));
        }
        if (u != null) { try { p.Part.SaveAs(Path.Combine(Out, mode + "-udp.par")); Say("   已存 " + mode + "-udp.par"); } catch { } }
        try { p.Part.Close(false); } catch { }
    }
    static P.Hole Blind(Plate p, P.Profile prof, P.HoleData data, double depth){
        foreach (var side in new[]{ P.FeaturePropertyConstants.igLeft, P.FeaturePropertyConstants.igRight }) {
            P.Hole h = null;
            try { h = p.Model.Holes.AddFinite(prof, side, depth, data); } catch { continue; }
            object desc; var st = h.GetStatusEx(out desc);
            if ((int)st == (int)P.FeatureStatusConstants.igFeatureOK) return h;
            try { h.Delete(); } catch { }
        }
        return null;
    }
    static P.HoleData TappedData(P.HoleDataCollection coll){
        // 普通孔 + 螺纹数据（AutoHoleWriter 的写法）：M6 底孔 Φ5 + 装饰螺纹
        return coll.AddEx(P.FeaturePropertyConstants.igRegularHole,
            "ISO Metric", M, M, M, 0.005,
            0.0, 0.0, 0.0, 0.0, 0.0,
            P.FeaturePropertyConstants.igTappedHole, M, M, M, M, M, P.FeaturePropertyConstants.igVBottomDimToFlat, M,
            M, M, M, M, M, true,
            M, M, M, "M6", M, M, M, M, M, M, M, M);
    }
}
