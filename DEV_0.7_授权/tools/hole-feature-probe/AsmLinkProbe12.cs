using System;
using System.Collections.Generic;
using P=SolidEdgePart;

// J5..J9：插件里合成多孔特征（HoleCombine）真机出了 igFeatureWarned / 种子孔位没孔，探针都是"先建全部轮廓、再建种子孔"。
// 这里按插件的真实顺序复现，找出是哪一步：
//   J5 种子孔先建（自己的草图集），之后才建第 2、3 个轮廓 → AddByProfiles（只差"轮廓在种子孔后面"）
//   J6 插件流程：3 个单孔各自打好 → 删第 2、3 个 → 新建第 2、3 个轮廓 → AddByProfiles（普通通孔 Φ6）
//   J7 J6 + M6 螺纹孔（贯通、螺纹全长）
//   J8 J6 + M6 沉孔 Φ6.6/Φ11 深 6.8
//   J9 另一条路：3 个单孔打好 → 3 个全删 → 新建 3 个轮廓 → 用第 1 个轮廓重打种子孔 → AddByProfiles（螺纹孔）
partial class AsmDrivenHoleProbe {
    static void JF(string mode){
        var p = NewPlate(0.1, 0.1, 0.01);
        double v0 = Vol(p.Part);
        var plane = p.Part.RefPlanes.AddParallelByDistance(FaceAtZ(p.Part, 0.01), 0.0, P.ReferenceElementConstants.igNormalSide, M, M, M, M);
        P.HoleData data = mode == "J7" || mode == "J9" ? TappedM6(p.Part.HoleDataCollection)
                        : mode == "J8" ? Counterbore(p.Part.HoleDataCollection, 0.0066, 0.011, 0.0068)
                        : Regular(p.Part.HoleDataCollection, 0.006);
        var profs = new List<P.Profile>();
        P.Hole seed = null;
        if (mode == "J5") {
            profs.Add(SeparateProfiles(p, plane, new[]{ Js[0] })[0]);
            seed = Through(p, profs[0], data);
            profs.AddRange(SeparateProfiles(p, plane, new[]{ Js[1], Js[2] }));
        } else {
            var singles = new List<P.Hole>();
            foreach (var c in Js) { var pr = SeparateProfiles(p, plane, new[]{ c })[0]; singles.Add(Through(p, pr, data)); }
            Say("   " + mode + " 单孔 " + singles.FindAll(h => h != null).Count + " 个，切除≈" + Cut(p, v0) + " 圆 " + SafeStr2(() => Circles(p.Part)));
            if (mode == "J9") {
                foreach (var h in singles) Say("   删单孔 " + SafeStr2(() => { h.Delete(); return "OK"; }));
                profs.AddRange(SeparateProfiles(p, plane, Js));
                seed = Through(p, profs[0], data);
            } else {
                seed = singles[0];
                for (int k = 1; k < 3; k++) { var h = singles[k]; Say("   删单孔 " + SafeStr2(() => { h.Delete(); return "OK"; })); }
                var sp = SafeStr2(() => (seed.Profile as P.Profile) == null ? "null" : "Profile");
                Say("   种子孔.Profile=" + sp);
                profs.Add((P.Profile)seed.Profile);
                profs.AddRange(SeparateProfiles(p, plane, new[]{ Js[1], Js[2] }));
            }
        }
        if (seed == null) { Say("RESULT " + mode + " 种子孔没建成"); p.Part.Close(false); return; }
        var u = Wrap(p, mode, profs, seed);
        if (u != null) {
            Say("   " + mode + " 状态描述=" + SafeStr2(() => { object d; var st = u.GetStatusEx(out d); return st + " / " + (d == null ? "null" : d.ToString()); }));
            Say("   " + mode + " 重算后=" + SafeStr2(() => { p.Model.Recompute(); return Status(u); }));
            ReadUdp(p, mode, u, v0);
        }
        try { p.Part.SaveAs(System.IO.Path.Combine(Out, mode + "-udp.par")); } catch { }
        try { p.Part.Close(false); } catch { }
    }
    static string Cut(Plate p, double v0){ return SafeStr2(() => ((v0 - Vol(p.Part)) / (Math.PI * 0.003 * 0.003 * 0.01)).ToString("0.00")); }
}
