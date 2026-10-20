using System;
using System.Collections.Generic;
using P=SolidEdgePart;

// N：Profile.End 以后多出来的 Hole2d 去哪了？（是不是被拆到同一草图集的新轮廓里）
//    以及：拆出来的这几个轮廓一起交给孔特征（SetProfiles / 建孔时用第一个）能不能切出全部孔。
partial class AsmDrivenHoleProbe {
    static void N(){
        for (int variant = 0; variant < 3; variant++) {
            var p = NewPlate(0.1, 0.1, 0.01);
            double v0 = Vol(p.Part);
            var plane = p.Part.RefPlanes.AddParallelByDistance(FaceAtZ(p.Part, 0.01), 0.0, P.ReferenceElementConstants.igNormalSide, M, M, M, M);
            var ps = p.Part.ProfileSets.Add();
            var prof = ps.Profiles.Add(plane);
            foreach (var c in new[]{ new[]{0.02,0.05}, new[]{0.05,0.05}, new[]{0.08,0.05} }) { double x2, y2; prof.Convert3DCoordinate(c[0], c[1], 0.01, out x2, out y2); prof.Holes2d.Add(x2, y2); }
            Say("   N" + variant + " End 前 草图集轮廓数=" + ps.Profiles.Count + " 草图集总数=" + p.Part.ProfileSets.Count);
            prof.End(P.ProfileValidationType.igProfileClosed);
            Say("   N" + variant + " End 后 草图集轮廓数=" + ps.Profiles.Count + " 草图集总数=" + p.Part.ProfileSets.Count);
            var all = new List<P.Profile>();
            for (int i = 1; i <= p.Part.ProfileSets.Count; i++) {
                var s = p.Part.ProfileSets.Item(i);
                for (int j = 1; j <= s.Profiles.Count; j++) {
                    var pr = s.Profiles.Item(j);
                    int h = -1; try { h = pr.Holes2d.Count; } catch { }
                    string pts = "";
                    try { for (int k = 1; k <= pr.Holes2d.Count; k++) { double x, y; pr.Holes2d.Item(k).GetCenterPoint(out x, out y); pts += " (" + Mm(x) + "," + Mm(y) + ")"; } } catch { }
                    Say("     草图集 " + i + " 轮廓 " + j + " Hole2d=" + h + pts + (ReferenceEquals(s, ps) ? " ←本草图集" : ""));
                    if (ReferenceEquals(s, ps) && h > 0) all.Add(pr);
                }
            }
            var data = Regular(p.Part.HoleDataCollection, 0.006);
            P.Hole hole = null;
            if (variant == 0) {
                hole = Through(p, prof, data);
                Say("RESULT N0 用第一个轮廓建孔 切出≈" + ((v0 - Vol(p.Part)) / (Math.PI * 0.003 * 0.003 * 0.01)).ToString("0.00"));
                if (hole != null && all.Count > 1) {
                    try {
                        Array arr = all.ToArray();
                        hole.SetProfiles(all.Count, ref arr);
                        try { p.Model.Recompute(); } catch (Exception e) { Say("   Recompute 异常 " + e.Message); }
                        object d; var st = hole.GetStatusEx(out d);
                        Say("RESULT N0 SetProfiles(" + all.Count + ") 状态=" + st + " 切出≈" + ((v0 - Vol(p.Part)) / (Math.PI * 0.003 * 0.003 * 0.01)).ToString("0.00"));
                    } catch (Exception e) { Say("RESULT N0 SetProfiles 异常 0x" + e.HResult.ToString("X8") + " " + e.Message); }
                }
            } else if (variant == 1) {
                // 每个拆出来的轮廓单独建孔 → 这是插件现在的做法的等价物，作对照
                int ok = 0; foreach (var pr in all) if (Through(p, pr, data) != null) ok++;
                Say("RESULT N1 每个轮廓各建一个孔 成功 " + ok + " 切出≈" + ((v0 - Vol(p.Part)) / (Math.PI * 0.003 * 0.003 * 0.01)).ToString("0.00") + " 孔特征数=" + p.Model.Holes.Count);
            } else {
                // 草图集里只放一个轮廓，但 End 之前就用 Holes2d 的数组 + AddMultiBody？先试 AddFinite 传草图集
                try {
                    var h = p.Model.Holes.AddThroughAll((P.Profile)ps.Profiles.Item(ps.Profiles.Count), P.FeaturePropertyConstants.igLeft, data);
                    object d; var st = h.GetStatusEx(out d);
                    Say("RESULT N2 用最后一个轮廓建孔 状态=" + st + " 切出≈" + ((v0 - Vol(p.Part)) / (Math.PI * 0.003 * 0.003 * 0.01)).ToString("0.00"));
                } catch (Exception e) { Say("RESULT N2 异常 0x" + e.HResult.ToString("X8") + " " + e.Message); }
            }
            p.Part.Close(false);
        }
    }
}
