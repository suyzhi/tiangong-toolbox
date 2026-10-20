using System;
using System.Collections.Generic;
using A=SolidEdgeAssembly;
using F=SolidEdgeFramework;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;
using S=SolidEdgeFrameworkSupport;

// L1..L3：挂了天工原生关联的孔合成多孔特征。插件里（原位编辑中合成）重算后仍是 igFeatureWarned、种子孔位读不到孔；
// 不进装配、用画的构造圆模拟（JA）却没问题。这里在真的原位编辑 + 零件间复制里复现：
//   L1 原位里：两个孔都挂复制圆边打成单孔 → 删第 2 个 → 新轮廓挂圆边 → AddByProfiles（插件现在的做法）
//   L2 原位里打好单孔、**退出原位**，再在零件里做合成（新轮廓照样 IncludeEdge 复制圆边）
//   L3 同 L1，但第 2 个新轮廓不挂圆边（看警告是不是挂圆边带来的）
// 每种都挪 A +5mm → UpdateAll，看孔跟不跟。
partial class AsmDrivenHoleProbe {
    static void LZ(string mode){
        try { app.SetGlobalParameter(F.ApplicationGlobalConstants.seApplicationGlobalAllowInterPart, true); } catch { }
        var f = Build(mode);
        var pts = new[]{ new[]{0.03,0.04}, new[]{0.07,0.04} };
        if (!EnterInPlace(f)) { Say("RESULT " + mode + " 进不了原位"); try { f.Asm.Close(false); } catch { } return; }
        P.InterpartConstruction copy = null;
        try { copy = f.PartB.Constructions.InterpartConstructions.Add(f.Asm.CreateReference(f.OccA, FaceAtZ(f.PartA, 0.01))); }
        catch (Exception e) { Say("RESULT " + mode + " 零件间复制异常 0x" + e.HResult.ToString("X8")); LeaveInPlace(); try { f.Asm.Close(false); } catch { } return; }
        Say("   复制状态 " + SafeStr2(() => { object d; return copy.get_Status(out d).ToString(); }) + " 圆边 " + CircleEdges(SafeEdges(copy)).Count);
        var mdl = (P.Model)f.PartB.Models.Item(1);
        var plane = f.PartB.RefPlanes.AddParallelByDistance(FaceAtZ(f.PartB, 0.0), 0.0, P.ReferenceElementConstants.igNormalSide, M, M, M, M);
        var data = TappedM6(f.PartB.HoleDataCollection);
        double v0 = Vol(f.PartB);
        var singles = new List<P.Hole>();
        foreach (var c in pts) singles.Add(LinkedHole(f, mdl, plane, copy, c, true, data));
        Say("   单孔 " + singles.FindAll(h => h != null).Count + " 个，B 圆 " + SafeStr2(() => Circles(f.PartB)));
        if (singles.Contains(null)) { Say("RESULT " + mode + " 单孔没打全"); LeaveInPlace(); try { f.Asm.Close(false); } catch { } return; }

        if (mode == "L2") LeaveInPlace();
        var seed = singles[0];
        Say("   删第 2 个单孔 " + SafeStr2(() => { singles[1].Delete(); return "OK"; }));
        P.Profile p2 = null;
        Say("   第 2 个新轮廓 " + SafeStr2(() => { p2 = LinkedProfile(f, plane, copy, pts[1], mode != "L3"); return "OK"; }));
        P.UserDefinedPattern u = null;
        Say("   AddByProfiles " + SafeStr2(() => { Array arr = new object[]{ (P.Profile)seed.Profile, p2 }; u = mdl.UserDefinedPatterns.AddByProfiles(2, arr, seed); return u == null ? "null" : "UDP"; }));
        if (u != null) {
            Say("   " + mode + " 合成后 " + Status(u) + " B 圆 " + SafeStr2(() => Circles(f.PartB)) + " 切除≈" + SafeStr2(() => ((v0 - Vol(f.PartB)) / (Math.PI * 0.0025 * 0.0025 * 0.01)).ToString("0.00")));
            Say("   " + mode + " 重算后 " + SafeStr2(() => { mdl.Recompute(); return Status(u); }) + " B 圆 " + SafeStr2(() => Circles(f.PartB)));
            Say("   " + mode + " 输入孔 " + string.Join(",", Inputs(u).ConvertAll(h => SafeStr2(() => { object d; return h.Name + ":" + h.GetStatusEx(out d); })).ToArray()));
        }
        if (mode != "L2") LeaveInPlace();
        if (u != null) {
            Say("RESULT " + mode + " 退出原位后 " + Status(u) + " B 圆 " + SafeStr2(() => Circles(f.PartB)));
            Say("   " + mode + " 退出原位、重算后 " + SafeStr2(() => { ((P.Model)f.PartB.Models.Item(1)).Recompute(); return Status(u); }));
            Say("   " + mode + " B.HasInterpartLinks=" + SafeStr2(() => { bool has = false; f.PartB.HasInterpartLinks(ref has); return has.ToString(); }));
        }
        try { f.Asm.Save(); } catch { }
        Array mA = new double[]{ 1,0,0,0, 0,1,0,0, 0,0,1,0, 0.005,0,0,1 }; f.OccA.PutMatrix(ref mA, true);
        Wait(1000);
        try { f.Asm.UpdateAll(); } catch (Exception e) { Say("   UpdateAll 异常 " + e.Message); }
        Wait(1500);
        Say("RESULT " + mode + " 挪 A +5mm、UpdateAll 后 " + (u == null ? "" : Status(u)) + " B 圆 " + SafeStr2(() => Circles(f.PartB)) + "（应在 35,40 / 75,40）");
        try { f.Asm.Save(); } catch { }
        try { f.Asm.Close(true); } catch { }
    }
    // 孔轮廓：IncludeEdge(复制来的圆边) + Hole2d 约束到圆心 + 圆改构造线（插件 NativeHoleLink.Attach 的做法）
    static P.Profile LinkedProfile(Fixture f, P.RefPlane plane, P.InterpartConstruction copy, double[] c, bool anchor){
        var prof = f.PartB.ProfileSets.Add().Profiles.Add(plane);
        double x2, y2; prof.Convert3DCoordinate(c[0], c[1], 0.0, out x2, out y2);
        var h2 = prof.Holes2d.Add(x2, y2);
        if (anchor) {
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
        prof.End(P.ProfileValidationType.igProfileClosed);
        return prof;
    }
    static P.Hole LinkedHole(Fixture f, P.Model mdl, P.RefPlane plane, P.InterpartConstruction copy, double[] c, bool anchor, P.HoleData data){
        foreach (var sd in new[]{ P.FeaturePropertyConstants.igLeft, P.FeaturePropertyConstants.igRight }) {
            try {
                var prof = LinkedProfile(f, plane, copy, c, anchor);
                var h = mdl.Holes.AddThroughAll(prof, sd, data);
                object d; if ((int)h.GetStatusEx(out d) == (int)P.FeatureStatusConstants.igFeatureOK) return h;
                h.Delete();
            } catch (Exception e) { Say("   打孔 " + sd + " 异常 0x" + e.HResult.ToString("X8")); }
        }
        return null;
    }
}
