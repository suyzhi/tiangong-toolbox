using System;
using System.Collections.Generic;
using P=SolidEdgePart;
using S=SolidEdgeFrameworkSupport;

// E：先用 1 个 Hole2d 建好孔特征，再往这个孔特征**自己的轮廓**里补 Hole2d，重算 —— 一个特征能不能变成多个孔。
partial class AsmDrivenHoleProbe {
    static void E(){
        foreach (var v in new[]{ "补完直接重算", "补完 End 再重算", "补完 End(0) 再重算", "补完 Hole.Recompute" }) {
            var p = NewPlate(0.1, 0.1, 0.01);
            double v0 = Vol(p.Part);
            var plane = p.Part.RefPlanes.AddParallelByDistance(FaceAtZ(p.Part, 0.01), 0.0, P.ReferenceElementConstants.igNormalSide, M, M, M, M);
            var ps = p.Part.ProfileSets.Add();
            var prof = ps.Profiles.Add(plane);
            double x2, y2; prof.Convert3DCoordinate(0.02, 0.05, 0.01, out x2, out y2); prof.Holes2d.Add(x2, y2);
            prof.End(P.ProfileValidationType.igProfileClosed);
            var hole = Through(p, prof, Regular(p.Part.HoleDataCollection, 0.006));
            if (hole == null) { Say("RESULT E " + v + " 第一个孔就失败"); p.Part.Close(false); continue; }
            int np = 0; Array pr = new object[0]; hole.GetProfiles(out np, ref pr);
            var hp = (P.Profile)pr.GetValue(0);
            string err = "";
            try {
                foreach (var c in new[]{ new[]{0.05,0.05}, new[]{0.08,0.05} }) { hp.Convert3DCoordinate(c[0], c[1], 0.01, out x2, out y2); hp.Holes2d.Add(x2, y2); }
            } catch (Exception e) { err += " 补点异常 0x" + e.HResult.ToString("X8") + " " + e.Message; }
            int before = -1; try { before = hp.Holes2d.Count; } catch { }
            try {
                if (v == "补完 End 再重算") hp.End(P.ProfileValidationType.igProfileClosed);
                if (v == "补完 End(0) 再重算") hp.End((P.ProfileValidationType)0);
            } catch (Exception e) { err += " End 异常 0x" + e.HResult.ToString("X8"); }
            try {
                if (v == "补完 Hole.Recompute") ((dynamic)hole).Recompute();
                else p.Model.Recompute();
            } catch (Exception e) { err += " 重算异常 0x" + e.HResult.ToString("X8") + " " + e.Message; }
            object d; var st = hole.GetStatusEx(out d);
            int after = -1; try { after = hp.Holes2d.Count; } catch { }
            int np2 = 0; try { Array pr2 = new object[0]; hole.GetProfiles(out np2, ref pr2); } catch { }
            double holes = (v0 - Vol(p.Part)) / (Math.PI * 0.003 * 0.003 * 0.01);
            Say("RESULT E " + v + err + " 补后Hole2d=" + before + "→" + after + " 草图集轮廓数=" + ps.Profiles.Count + " 特征轮廓数=" + np2 + " 状态=" + st + " 切出≈" + holes.ToString("0.00") + " 孔特征数=" + p.Model.Holes.Count);
            if (holes > 1.5) { try { p.Part.SaveAs(System.IO.Path.Combine(Out, "E-multi.par")); } catch { } }
            p.Part.Close(false);
        }
    }
}
