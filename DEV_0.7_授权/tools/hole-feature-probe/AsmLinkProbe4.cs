using System;
using System.Collections.Generic;
using P=SolidEdgePart;

// Q：草图集 Profiles 的 FeatureCommand / FeatureCommandType / ValidationCriteria —— 交互的打孔命令是不是靠它们
//    让 End 不把多个 Hole2d 拆开？逐个组合试：End 后剩几个轮廓、建孔切出几个孔。
partial class AsmDrivenHoleProbe {
    static string Props(P.Profiles ps){
        string s = "";
        try { s += "FeatureCommand=" + ps.FeatureCommand; } catch (Exception e) { s += "FeatureCommand?" + e.HResult.ToString("X8"); }
        try { s += " FeatureCommandType=" + ps.FeatureCommandType; } catch (Exception e) { s += " FeatureCommandType?" + e.HResult.ToString("X8"); }
        try { s += " ValidationCriteria=" + ps.ValidationCriteria; } catch (Exception e) { s += " ValidationCriteria?" + e.HResult.ToString("X8"); }
        return s;
    }
    static void Q(){
        // 默认值
        {
            var p = NewPlate(0.1, 0.1, 0.01);
            var ps = p.Part.ProfileSets.Add();
            Say("   Q 新草图集默认 " + Props(ps.Profiles));
            // 底板拉伸用的那个草图集
            var first = p.Part.ProfileSets.Item(1);
            Say("   Q 拉伸草图集 " + Props(first.Profiles));
            p.Part.Close(false);
        }
        var trials = new List<Tuple<string, Action<P.Profiles>>> {
            Tuple.Create("FeatureCommand=45003", (Action<P.Profiles>)(ps => ps.FeatureCommand = 45003)),
            Tuple.Create("FeatureCommand=45003 Type=1", (Action<P.Profiles>)(ps => { ps.FeatureCommand = 45003; ps.FeatureCommandType = 1; })),
            Tuple.Create("FeatureCommand=45003 Type=2", (Action<P.Profiles>)(ps => { ps.FeatureCommand = 45003; ps.FeatureCommandType = 2; })),
            Tuple.Create("ValidationCriteria=0", (Action<P.Profiles>)(ps => ps.ValidationCriteria = 0)),
            Tuple.Create("ValidationCriteria=8192", (Action<P.Profiles>)(ps => ps.ValidationCriteria = 8192)),
            Tuple.Create("ValidationCriteria=524288", (Action<P.Profiles>)(ps => ps.ValidationCriteria = 524288)),
            Tuple.Create("FeatureCommand=45003 VC=0", (Action<P.Profiles>)(ps => { ps.FeatureCommand = 45003; ps.ValidationCriteria = 0; })),
            Tuple.Create("FeatureCommandType=1", (Action<P.Profiles>)(ps => ps.FeatureCommandType = 1)),
            Tuple.Create("FeatureCommandType=2", (Action<P.Profiles>)(ps => ps.FeatureCommandType = 2)),
            Tuple.Create("FeatureCommandType=3", (Action<P.Profiles>)(ps => ps.FeatureCommandType = 3)),
        };
        foreach (var t in trials) {
            foreach (var endMode in new[]{ "End(1)", "End(0)" }) {
                var p = NewPlate(0.1, 0.1, 0.01);
                double v0 = Vol(p.Part);
                var plane = p.Part.RefPlanes.AddParallelByDistance(FaceAtZ(p.Part, 0.01), 0.0, P.ReferenceElementConstants.igNormalSide, M, M, M, M);
                var ps = p.Part.ProfileSets.Add();
                string setErr = "";
                try { t.Item2(ps.Profiles); } catch (Exception e) { setErr = " 设置异常 0x" + e.HResult.ToString("X8"); }
                var prof = ps.Profiles.Add(plane);
                foreach (var c in new[]{ new[]{0.02,0.05}, new[]{0.05,0.05}, new[]{0.08,0.05} }) { double x2, y2; prof.Convert3DCoordinate(c[0], c[1], 0.01, out x2, out y2); prof.Holes2d.Add(x2, y2); }
                int er = -999; try { er = prof.End(endMode == "End(1)" ? P.ProfileValidationType.igProfileClosed : (P.ProfileValidationType)0); } catch { }
                int nprof = ps.Profiles.Count, h2 = prof.Holes2d.Count;
                var hole = Through(p, prof, Regular(p.Part.HoleDataCollection, 0.006));
                double holes = (v0 - Vol(p.Part)) / (Math.PI * 0.003 * 0.003 * 0.01);
                Say("RESULT Q " + t.Item1 + setErr + " " + endMode + "=" + er + " → 轮廓数=" + nprof + " 首轮廓 Hole2d=" + h2 + " 孔=" + (hole == null ? "失败" : "OK") + " 切出≈" + holes.ToString("0.00") + " | 之后 " + Props(ps.Profiles));
                p.Part.Close(false);
            }
        }
    }
}
