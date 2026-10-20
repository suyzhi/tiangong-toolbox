using System;
using System.Collections.Generic;
using System.IO;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;

// U：建一块 100x100x10 的板留在 CAD 里（不关），给"用天工自己的打孔命令手工点 3 个孔"的鼠标实验用。
// I：读当前活动零件里所有孔特征的结构（轮廓数、每个轮廓几个 Hole2d、草图集里几个轮廓、切出几根轴线）。
partial class AsmDrivenHoleProbe {
    static void U(){
        var p = NewPlate(0.1, 0.1, 0.01);
        try { p.Part.SaveAs(Path.Combine(Out, "U-plate.par")); } catch (Exception e) { Say("   保存异常 " + e.Message); }
        Say("RESULT U 板已建好并留着：" + p.Part.FullName);
    }
    static void I(){
        var part = app.ActiveDocument as P.PartDocument;
        if (part == null) { Say("RESULT I 活动文档不是零件"); return; }
        Say("   I 零件 " + part.FullName + " 建模方式 " + part.ModelingMode + " 草图集 " + part.ProfileSets.Count);
        for (int i = 1; i <= part.ProfileSets.Count; i++) {
            var s = part.ProfileSets.Item(i);
            string line = "     草图集 " + i + " 轮廓 " + s.Profiles.Count + ":";
            for (int j = 1; j <= s.Profiles.Count; j++) { var pr = s.Profiles.Item(j); int h = -1; try { h = pr.Holes2d.Count; } catch { } line += " [" + j + "]Hole2d=" + h + " 圆=" + SafeCount(() => pr.Circles2d.Count); }
            Say(line);
        }
        var model = (P.Model)part.Models.Item(1);
        Say("   I Model.Holes.Count=" + SafeCount(() => model.Holes.Count));
        var feats = part.DesignEdgebarFeatures;
        for (int i = 1; i <= feats.Count; i++) {
            object f = feats.Item(i);
            string nm = "", tp = "", ty = "";
            try { nm = (string)((dynamic)f).Name; } catch { }
            try { tp = Microsoft.VisualBasic.Information.TypeName(f); } catch { }
            try { ty = ((dynamic)f).Type.ToString(); } catch { }
            string extra = "";
            try { int np = 0; Array pr = new object[0]; ((dynamic)f).GetProfiles(out np, ref pr); extra = " GetProfiles=" + np; } catch (Exception e) { extra = " GetProfiles?" + e.GetType().Name; }
            Say("   I 特征树[" + i + "] " + nm + " | " + tp + " | Type=" + ty + extra);
        }
        // 所有 Models 集合里带 Hole 的
        foreach (var coll in new[]{ "Holes", "HoleGeometries", "ExtrudedCutouts", "Patterns", "UserDefinedPatterns" }) {
            try { object c = model.GetType().InvokeMember(coll, System.Reflection.BindingFlags.GetProperty, null, model, null); Say("   I Model." + coll + ".Count=" + ((dynamic)c).Count); } catch (Exception e) { Say("   I Model." + coll + " 异常 " + e.Message); }
        }
        for (int i = 1; i <= model.UserDefinedPatterns.Count; i++) {
            var u = model.UserDefinedPatterns.Item(i);
            string s = "   I UDP[" + i + "] " + u.Name + " sys=" + SafeStr2(() => u.SystemName) + " 输入特征数=" + SafeStr2(() => u.NumberInputFeatures.ToString());
            try { Array inp = new object[0]; u.GetInputFeatures(ref inp); s += " 输入:"; foreach (object o in inp) s += " " + Microsoft.VisualBasic.Information.TypeName(o) + "(" + SafeStr2(() => (string)((dynamic)o).Name) + ")"; } catch (Exception e) { s += " GetInputFeatures 异常 " + e.Message; }
            try { int n, per; u.GetNumberOfOccurrences(out n, out per); s += " 实例数=" + n + "×" + per; } catch (Exception e) { s += " GetNumberOfOccurrences 异常 " + e.Message; }
            try { object pp = u.PatternPlane; s += " 阵列面=" + Microsoft.VisualBasic.Information.TypeName(pp); } catch (Exception e) { s += " PatternPlane 异常 " + e.Message; }
            try { object tr = u.Transforms; s += " Transforms=" + Microsoft.VisualBasic.Information.TypeName(tr); } catch (Exception e) { s += " Transforms 异常 " + e.Message; }
            object d0; try { s += " 状态=" + u.GetStatusEx(out d0); } catch { }
            Say(s);
        }
        for (int i = 1; i <= model.HoleGeometries.Count; i++) {
            var hg = model.HoleGeometries.Item(i);
            string s = "   I HoleGeometry[" + i + "] 父=" + SafeStr2(() => Microsoft.VisualBasic.Information.TypeName(hg.Parent));
            try { var hd = (P.HoleData)hg.HoleData; s += " 孔径=" + Mm(hd.HoleDiameter) + " 类型=" + hd.HoleType; } catch (Exception e) { s += " HoleData 异常 " + e.Message; }
            try { s += " 面=" + ((dynamic)hg.Faces).Count; } catch { }
            Say(s);
        }
        for (int i = 1; i <= model.Holes.Count; i++) {
            var h = model.Holes.Item(i);
            int np = 0; Array pr = new object[0];
            try { h.GetProfiles(out np, ref pr); } catch (Exception e) { Say("     GetProfiles 异常 " + e.Message); }
            var per = new List<string>();
            for (int k = 0; k < np; k++) { try { var x = (P.Profile)pr.GetValue(k); per.Add(x.Holes2d.Count.ToString() + "@" + SafeSetIndex(part, x)); } catch { per.Add("?"); } }
            int axes = 0;
            try { foreach (object o in (System.Collections.IEnumerable)h.get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll)) if (((G.Face)o).Geometry is G.Cylinder) axes++; } catch { }
            string nm = ""; try { nm = h.Name; } catch { }
            object d; var st = h.GetStatusEx(out d);
            Say("RESULT I 孔特征 " + nm + " 状态 " + st + " 轮廓数=" + np + " 各轮廓Hole2d@草图集=[" + string.Join(",", per.ToArray()) + "] 圆柱面=" + axes);
        }
    }
    static string SafeStr2(Func<string> f){ try { return f(); } catch (Exception e) { return "?" + e.GetType().Name; } }
    static string SafeCount(Func<int> f){ try { return f().ToString(); } catch { return "?"; } }
    static string SafeSetIndex(P.PartDocument part, P.Profile prof){
        try { var s = (P.ProfileSet)prof.Parent; for (int i = 1; i <= part.ProfileSets.Count; i++) if (ReferenceEquals(part.ProfileSets.Item(i), s)) return i.ToString(); } catch { }
        return "?";
    }
}
