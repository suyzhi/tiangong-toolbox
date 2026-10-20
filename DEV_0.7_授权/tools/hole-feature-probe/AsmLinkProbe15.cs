using System;
using System.Collections.Generic;
using F=SolidEdgeFramework;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;
using S=SolidEdgeFrameworkSupport;

// L4/L5：L1 的结论是"各自草图集里挂复制圆边的轮廓，只有种子孔那个能用，其余输入孔 igFeatureWarned、不跟着挪"。
// 天工自己的多孔特征是**一个草图集、N 个轮廓**。试同一个草图集：
//   L4 一个轮廓里 IncludeEdge 两条复制圆边 + 两个 Hole2d 各约束到圆心，End 拆成 N 个轮廓 → 种子孔用第 1 个 → AddByProfiles(全部)
//   L5 同一个草图集里 Profiles.Add 两次，各挂各的复制圆边 → 种子孔用第 1 个 → AddByProfiles(全部)
// 都在原位编辑里做；挪 A +5mm → UpdateAll，看两个孔是不是都跟到 (35,40) (75,40)。
partial class AsmDrivenHoleProbe {
    static void LS(string mode){
        try { app.SetGlobalParameter(F.ApplicationGlobalConstants.seApplicationGlobalAllowInterPart, true); } catch { }
        var f = Build(mode);
        var pts = new[]{ new[]{0.03,0.04}, new[]{0.07,0.04} };
        if (!EnterInPlace(f)) { Say("RESULT " + mode + " 进不了原位"); try { f.Asm.Close(false); } catch { } return; }
        var copy = f.PartB.Constructions.InterpartConstructions.Add(f.Asm.CreateReference(f.OccA, FaceAtZ(f.PartA, 0.01)));
        var mdl = (P.Model)f.PartB.Models.Item(1);
        var plane = f.PartB.RefPlanes.AddParallelByDistance(FaceAtZ(f.PartB, 0.0), 0.0, P.ReferenceElementConstants.igNormalSide, M, M, M, M);
        var data = TappedM6(f.PartB.HoleDataCollection);
        double v0 = Vol(f.PartB);
        var ps = f.PartB.ProfileSets.Add();
        var profs = new List<P.Profile>();
        try {
            if (mode == "L4") {
                var prof = ps.Profiles.Add(plane);
                foreach (var c in pts) Anchor(prof, copy, c);
                Say("   L4 End=" + prof.End(P.ProfileValidationType.igProfileClosed));
            } else {
                foreach (var c in pts) { var prof = ps.Profiles.Add(plane); Anchor(prof, copy, c); Say("   L5 End=" + prof.End(P.ProfileValidationType.igProfileClosed)); }
            }
            for (int j = 1; j <= ps.Profiles.Count; j++) {
                var pj = ps.Profiles.Item(j);
                profs.Add(pj);
                Say("   轮廓 " + j + "：Hole2d=" + SafeStr2(() => pj.Holes2d.Count.ToString()) + " Circles2d=" + SafeStr2(() => pj.Circles2d.Count.ToString()));
            }
        } catch (Exception e) { Say("RESULT " + mode + " 建轮廓异常 0x" + e.HResult.ToString("X8") + " " + e.Message); LeaveInPlace(); try { f.Asm.Close(false); } catch { } return; }
        P.Hole seed = null;
        foreach (var sd in new[]{ P.FeaturePropertyConstants.igLeft, P.FeaturePropertyConstants.igRight }) {
            try { var h = mdl.Holes.AddThroughAll(profs[0], sd, data); object d; if ((int)h.GetStatusEx(out d) == (int)P.FeatureStatusConstants.igFeatureOK) { seed = h; break; } h.Delete(); }
            catch (Exception e) { Say("   种子孔 " + sd + " 异常 0x" + e.HResult.ToString("X8")); break; }   // 失败时轮廓被作废，不换侧再试
        }
        Say("   种子孔 " + (seed == null ? "没建成" : "OK") + " B 圆 " + SafeStr2(() => Circles(f.PartB)));
        P.UserDefinedPattern u = null;
        if (seed != null) Say("   AddByProfiles(" + profs.Count + ") " + SafeStr2(() => { Array arr = profs.ToArray(); u = mdl.UserDefinedPatterns.AddByProfiles(profs.Count, arr, seed); return u == null ? "null" : "UDP"; }));
        if (u != null) {
            Say("   " + mode + " 合成后 " + Status(u) + " 重算后 " + SafeStr2(() => { mdl.Recompute(); return Status(u); }) + " B 圆 " + SafeStr2(() => Circles(f.PartB)));
            Say("   " + mode + " 输入孔 " + string.Join(",", Inputs(u).ConvertAll(h => SafeStr2(() => { object d; return h.Name + ":" + h.GetStatusEx(out d); })).ToArray()));
        }
        LeaveInPlace();
        try { f.Asm.Save(); } catch { }
        Array mA = new double[]{ 1,0,0,0, 0,1,0,0, 0,0,1,0, 0.005,0,0,1 }; f.OccA.PutMatrix(ref mA, true);
        Wait(1000);
        try { f.Asm.UpdateAll(); } catch (Exception e) { Say("   UpdateAll 异常 " + e.Message); }
        Wait(1500);
        Say("RESULT " + mode + " 挪 A +5mm、UpdateAll 后 " + (u == null ? "无 UDP" : Status(u)) + " B 圆 " + SafeStr2(() => Circles(f.PartB)) + "（应在 35,40 / 75,40）");
        try { f.Asm.Save(); } catch { }
        try { f.Asm.Close(true); } catch { }
    }
    static void Anchor(P.Profile prof, P.InterpartConstruction copy, double[] c){
        double x2, y2; prof.Convert3DCoordinate(c[0], c[1], 0.0, out x2, out y2);
        var h2 = prof.Holes2d.Add(x2, y2);
        G.Edge edge = null;
        foreach (var e in CircleEdges(SafeEdges(copy))) {
            Array cc = new double[3], ax = new double[3]; double r; ((G.Circle)e.Geometry).GetCircleData(ref cc, ref ax, out r);
            if (Math.Abs(D(cc,0) - c[0]) < 1e-6 && Math.Abs(D(cc,1) - c[1]) < 1e-6) { edge = e; break; }
        }
        if (edge == null) throw new InvalidOperationException("复制里找不到 (" + c[0] + "," + c[1] + ") 的圆边");
        object inc; prof.IncludeEdge(edge, out inc);
        var circ = inc ?? (object)prof.Circles2d.Item(prof.Circles2d.Count);
        ((S.Relations2d)prof.Relations2d).AddKeypoint(h2, 0, circ, (int)SolidEdgeConstants.KeypointIndexConstants.igCircleCenter, M);
        prof.ToggleConstruction(circ);
    }
}
