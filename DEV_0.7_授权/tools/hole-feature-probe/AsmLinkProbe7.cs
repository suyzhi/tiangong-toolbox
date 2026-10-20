using System;
using System.Collections.Generic;
using P=SolidEdgePart;
using S=SolidEdgeFrameworkSupport;

// K：End 是不是按"连通性"把 Hole2d 拆成几个轮廓？把孔心连起来（构造线 + 关键点约束 / 对齐约束）再 End 看看。
partial class AsmDrivenHoleProbe {
    static void K(){
        var variants = new[]{ "构造线串起来", "构造线串起来(不改构造)", "水平对齐约束", "一条构造线过所有孔心(点在线上)" };
        foreach (var v in variants) {
            var p = NewPlate(0.1, 0.1, 0.01);
            double v0 = Vol(p.Part);
            var plane = p.Part.RefPlanes.AddParallelByDistance(FaceAtZ(p.Part, 0.01), 0.0, P.ReferenceElementConstants.igNormalSide, M, M, M, M);
            var ps = p.Part.ProfileSets.Add();
            var prof = ps.Profiles.Add(plane);
            var pts = new List<double[]>();
            var hs = new List<P.Hole2d>();
            foreach (var c in new[]{ new[]{0.02,0.05}, new[]{0.05,0.05}, new[]{0.08,0.05} }) { double x2, y2; prof.Convert3DCoordinate(c[0], c[1], 0.01, out x2, out y2); pts.Add(new[]{x2,y2}); hs.Add(prof.Holes2d.Add(x2, y2)); }
            var rel = (S.Relations2d)prof.Relations2d;
            string err = "";
            try {
                if (v.StartsWith("构造线串起来")) {
                    for (int i = 0; i + 1 < hs.Count; i++) {
                        var ln = prof.Lines2d.AddBy2Points(pts[i][0], pts[i][1], pts[i+1][0], pts[i+1][1]);
                        rel.AddKeypoint(ln, (int)SolidEdgeConstants.KeypointIndexConstants.igLineStart, hs[i], 0, M);
                        rel.AddKeypoint(ln, (int)SolidEdgeConstants.KeypointIndexConstants.igLineEnd, hs[i+1], 0, M);
                        if (v == "构造线串起来") prof.ToggleConstruction(ln);
                    }
                } else if (v == "水平对齐约束") {
                    for (int i = 0; i + 1 < hs.Count; i++) rel.AddHorizontalAlign(hs[i], 0, hs[i+1], 0, M);
                } else {
                    var ln = prof.Lines2d.AddBy2Points(pts[0][0] - 0.005, pts[0][1], pts[2][0] + 0.005, pts[2][1]);
                    prof.ToggleConstruction(ln);
                    foreach (var h in hs) rel.AddPointOn(h, 0, ln, M);
                }
            } catch (Exception e) { err = " 约束异常 0x" + e.HResult.ToString("X8") + " " + e.Message; }
            int er = prof.End(P.ProfileValidationType.igProfileClosed);
            int nprof = ps.Profiles.Count, h2 = prof.Holes2d.Count;
            var hole = Through(p, prof, Regular(p.Part.HoleDataCollection, 0.006));
            double holes = (v0 - Vol(p.Part)) / (Math.PI * 0.003 * 0.003 * 0.01);
            int np = -1; if (hole != null) { try { Array pr = new object[0]; hole.GetProfiles(out np, ref pr); } catch { } }
            Say("RESULT K " + v + err + " End=" + er + " 轮廓数=" + nprof + " 首轮廓Hole2d=" + h2 + " 孔=" + (hole == null ? "失败" : "OK") + " 切出≈" + holes.ToString("0.00") + " 特征轮廓数=" + np);
            p.Part.Close(false);
        }
    }
}
