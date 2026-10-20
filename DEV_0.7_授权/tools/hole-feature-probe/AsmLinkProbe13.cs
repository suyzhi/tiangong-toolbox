using System;
using System.Collections.Generic;
using P=SolidEdgePart;
using S=SolidEdgeFrameworkSupport;

// JA..JC：插件里挂了原生关联的孔合成多孔特征，重算后仍是 igFeatureWarned（NativeLinkE2E）。
// 原生关联在孔轮廓里多了一个"包含进来的圆（改构造线）+ Hole2d 圆心约束"。不进装配，用画的构造圆模拟：
//   JA 插件顺序（J6）+ M6 螺纹孔 + 每个轮廓里一个构造圆、Hole2d 约束到圆心
//   JB 同 JA，M6 沉孔
//   JC 同 JA，构造圆不加约束
partial class AsmDrivenHoleProbe {
    static void JX(string mode){
        var p = NewPlate(0.1, 0.1, 0.01);
        double v0 = Vol(p.Part);
        var plane = p.Part.RefPlanes.AddParallelByDistance(FaceAtZ(p.Part, 0.01), 0.0, P.ReferenceElementConstants.igNormalSide, M, M, M, M);
        var data = mode == "JB" ? Counterbore(p.Part.HoleDataCollection, 0.0066, 0.011, 0.0068) : TappedM6(p.Part.HoleDataCollection);
        bool relate = mode != "JC";
        var singles = new List<P.Hole>();
        foreach (var c in Js) singles.Add(Through(p, Anchored(p, plane, c, relate), data));
        Say("   " + mode + " 单孔 " + singles.FindAll(h => h != null).Count + " 个 状态 " + string.Join(",", singles.ConvertAll(h => h == null ? "null" : SafeStr2(() => { object d; return h.GetStatusEx(out d).ToString(); })).ToArray()));
        var seed = singles[0];
        for (int k = 1; k < 3; k++) { var h = singles[k]; Say("   删单孔 " + SafeStr2(() => { h.Delete(); return "OK"; })); }
        var profs = new List<P.Profile>{ (P.Profile)seed.Profile };
        profs.Add(Anchored(p, plane, Js[1], relate)); profs.Add(Anchored(p, plane, Js[2], relate));
        var u = Wrap(p, mode, profs, seed);
        if (u != null) {
            Say("   " + mode + " 重算后=" + SafeStr2(() => { p.Model.Recompute(); return Status(u); }));
            Say("   " + mode + " 输入孔状态=" + string.Join(",", Inputs(u).ConvertAll(h => SafeStr2(() => { object d; return h.GetStatusEx(out d).ToString(); })).ToArray()));
            ReadUdp(p, mode, u, v0);
        }
        try { p.Part.SaveAs(System.IO.Path.Combine(Out, mode + "-udp.par")); } catch { }
        try { p.Part.Close(false); } catch { }
    }
    static P.Profile Anchored(Plate p, P.RefPlane plane, double[] c, bool relate){
        var prof = p.Part.ProfileSets.Add().Profiles.Add(plane);
        double x2, y2; prof.Convert3DCoordinate(c[0], c[1], 0.01, out x2, out y2);
        var circ = prof.Circles2d.AddByCenterRadius(x2, y2, 0.003);
        var h2 = prof.Holes2d.Add(x2, y2);
        if (relate) ((S.Relations2d)prof.Relations2d).AddKeypoint(h2, 0, circ, (int)SolidEdgeConstants.KeypointIndexConstants.igCircleCenter, M);
        prof.ToggleConstruction(circ);
        prof.End(P.ProfileValidationType.igProfileClosed);
        return prof;
    }
}
