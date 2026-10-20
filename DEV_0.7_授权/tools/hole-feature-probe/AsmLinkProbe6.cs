using System;
using System.Collections.Generic;
using System.IO;
using A=SolidEdgeAssembly;
using F=SolidEdgeFramework;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;
using S=SolidEdgeFrameworkSupport;

// R：子装配里的零件能不能用"零件间复制"关联。
//   顶层：S.asm（里面是 A，带两个孔）+ T.asm（里面是 B，z+30）+ 顶层直接放的 C（z+60）。
//   R1 参考孔在子装配里（A），打孔零件在顶层（C）
//   R2 参考孔在子装配里（A），打孔零件也在子装配里（B）
//   装配引用两种造法：嵌套 CreateReference（子装配里先造一个，再在顶层包一层）/ CreateReference2(SubOccurrence, 面)
partial class AsmDrivenHoleProbe {
    static void R(){
        try { app.SetGlobalParameter(F.ApplicationGlobalConstants.seApplicationGlobalAllowInterPart, true); } catch { }
        var a = NewPlate(0.1, 0.1, 0.01);
        foreach (var c in new[]{ new[]{0.03,0.04}, new[]{0.07,0.04} }) {
            var pl = a.Part.RefPlanes.AddParallelByDistance(FaceAtZ(a.Part, 0.01), 0.0, P.ReferenceElementConstants.igNormalSide, M, M, M, M);
            var pr = a.Part.ProfileSets.Add().Profiles.Add(pl);
            double x2, y2; pr.Convert3DCoordinate(c[0], c[1], 0.01, out x2, out y2); pr.Holes2d.Add(x2, y2); pr.End(P.ProfileValidationType.igProfileClosed);
            Through(a, pr, Regular(a.Part.HoleDataCollection, 0.010));
        }
        string pa = Path.Combine(Out, "R-A.par"), pb = Path.Combine(Out, "R-B.par"), pc = Path.Combine(Out, "R-C.par");
        string ps = Path.Combine(Out, "R-S.asm"), pt = Path.Combine(Out, "R-T.asm"), ptop = Path.Combine(Out, "R.asm");
        a.Part.SaveAs(pa); a.Part.Close(false);
        var b = NewPlate(0.1, 0.1, 0.01); b.Part.SaveAs(pb); b.Part.Close(false);
        var c3 = NewPlate(0.1, 0.1, 0.01); c3.Part.SaveAs(pc); c3.Part.Close(false);
        var sAsm = (A.AssemblyDocument)app.Documents.Add("SolidEdge.AssemblyDocument", Tpl + "iso metric assembly.asm");
        sAsm.Occurrences.AddByFilename(pa); sAsm.SaveAs(ps); sAsm.Close(false);
        var tAsm = (A.AssemblyDocument)app.Documents.Add("SolidEdge.AssemblyDocument", Tpl + "iso metric assembly.asm");
        tAsm.Occurrences.AddByFilename(pb); tAsm.SaveAs(pt); tAsm.Close(false);
        var top = (A.AssemblyDocument)app.Documents.Add("SolidEdge.AssemblyDocument", Tpl + "iso metric assembly.asm");
        var occS = top.Occurrences.AddByFilename(ps);
        var occT = top.Occurrences.AddByFilename(pt);
        var occC = top.Occurrences.AddByFilename(pc);
        Array mT = new double[]{ 1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0.03,1 }; occT.PutMatrix(ref mT, true);
        Array mC = new double[]{ 1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0.06,1 }; occC.PutMatrix(ref mC, true);
        top.SaveAs(ptop);
        Wait(1000);
        var subA = (A.SubOccurrence)occS.SubOccurrences.Item(1);
        var subB = (A.SubOccurrence)occT.SubOccurrences.Item(1);
        var partA = (P.PartDocument)subA.SubOccurrenceDocument;
        var partB = (P.PartDocument)subB.SubOccurrenceDocument;
        var partC = (P.PartDocument)occC.OccurrenceDocument;
        var cyl1 = CylFace(partA, 0.03, 0.04);
        Say("   R 夹具 OK 孔壁 " + (cyl1 != null));

        // 两种装配引用
        object refNested = null, ref2 = null;
        try {
            var sDoc = (A.AssemblyDocument)occS.OccurrenceDocument;
            object inner = sDoc.CreateReference((A.Occurrence)sDoc.Occurrences.Item(1), cyl1);
            refNested = top.CreateReference(occS, inner);
            Say("   R 嵌套 CreateReference OK " + RefInfo(refNested));
        } catch (Exception e) { Say("   R 嵌套 CreateReference 异常 0x" + e.HResult.ToString("X8") + " " + e.Message); }
        try { ref2 = top.CreateReference2(subA, cyl1); Say("   R CreateReference2(SubOccurrence) OK " + RefInfo(ref2)); }
        catch (Exception e) { Say("   R CreateReference2 异常 0x" + e.HResult.ToString("X8") + " " + e.Message); }

        // R1：顶层 C 原位编辑
        LinkInto("R1", top, partC, () => { try { app.ActiveSelectSet.RemoveAll(); } catch { } occC.Select(true); }, new[]{ refNested, ref2 }, 0.0);
        // R2：子装配里的 B —— 先原位编辑子装配 T，再在 T 里原位编辑 B（两级）
        {
            try { app.ActiveSelectSet.RemoveAll(); } catch { }
            occT.Select(true);
            try { app.StartCommand((F.SolidEdgeCommandConstants)57872); } catch (Exception e) { Say("   R2 第一级 StartCommand 异常 " + e.Message); }
            Wait(4000);
            Say("   R2 第一级后 活动文档 " + ActiveName() + " top.InPlaceActivated=" + top.InPlaceActivated);
            var tDoc = app.ActiveDocument as A.AssemblyDocument;
            if (tDoc != null && !ReferenceEquals(tDoc, top)) {
                var occBinT = (A.Occurrence)tDoc.Occurrences.Item(1);
                // 第二级：在 T 里选 B
                Action sel = () => { try { app.ActiveSelectSet.RemoveAll(); } catch { } occBinT.Select(true); };
                // 两种引用：顶层造的（refNested），以及在 T 的上下文里看不到 S，只能用顶层的
                LinkInto("R2", top, partB, sel, new[]{ refNested }, 0.0);
                Say("   R2 退出第二级后 活动文档 " + ActiveName());
                if (!(app.ActiveDocument is A.AssemblyDocument) || !ReferenceEquals(app.ActiveDocument, top)) LeaveInPlace();
            }
        }
        // R4：先 Reorder 到拉伸特征前面行不行（R1 里挪到最前面 E_FAIL）
        try {
            var cs = partC.Constructions.InterpartConstructions;
            if (cs.Count > 0) {
                var cpy = (P.InterpartConstruction)cs.Item(1);
                var feats = partC.DesignEdgebarFeatures;
                for (int i = 1; i <= feats.Count; i++) {
                    string nm = ""; try { nm = (string)((dynamic)feats.Item(i)).Name; } catch { }
                    if (nm.StartsWith("ExtrudedProtrusion") || nm.Contains("拉伸")) {
                        try { cpy.Reorder(feats.Item(i), false); Say("   R4 Reorder(拉伸之后) OK"); } catch (Exception e) { Say("   R4 Reorder(拉伸之后) 异常 0x" + e.HResult.ToString("X8")); }
                        break;
                    }
                }
            }
        } catch (Exception e) { Say("   R4 异常 " + e.Message); }

        try { top.Save(); } catch (Exception e) { Say("   保存异常 " + e.Message); }
        // 挪 S（参考孔所在子装配）x+5mm
        Array mS = new double[]{ 1,0,0,0, 0,1,0,0, 0,0,1,0, 0.005,0,0,1 }; occS.PutMatrix(ref mS, true);
        Wait(1000);
        try { top.UpdateAll(); } catch (Exception e) { Say("   UpdateAll 异常 " + e.Message); }
        Wait(2000);
        Say("RESULT R 挪 S +5 并 UpdateAll 后：C 圆边 " + Circles(partC) + "  B 圆边 " + Circles(partB));
        try { top.Save(); top.Close(true); } catch { }
    }
    static string RefInfo(object r){
        try {
            var rr = r as F.Reference;
            if (rr == null) return "(不是 Reference：" + SafeType(r) + ")";
            object topOcc = null; int nSub = 0, nBound = 0; Array bound = new object[0];
            rr.GetOccurrencesInPath(out topOcc, out nSub, out nBound, ref bound);
            Array m = new double[16]; rr.GetMatrix(ref m);
            return "顶层实例=" + SafeName(topOcc) + " 子实例数=" + nSub + " 矩阵平移=(" + Mm(D(m,12)) + "," + Mm(D(m,13)) + "," + Mm(D(m,14)) + ")";
        } catch (Exception e) { return "读路径异常 " + e.Message; }
    }
    static string SafeName(object o){ try { return (string)((dynamic)o).Name; } catch { return "?"; } }

    static void LinkInto(string tag, A.AssemblyDocument top, P.PartDocument target, Action select, object[] refs, double bottomZ){
        try { select(); } catch (Exception e) { Say("   " + tag + " 选择异常 " + e.Message); return; }
        try { app.StartCommand((F.SolidEdgeCommandConstants)57872); } catch (Exception e) { Say("   " + tag + " StartCommand 异常 " + e.Message); return; }
        Wait(4000);
        bool ok = ReferenceEquals(app.ActiveDocument, target);
        Say("   " + tag + " 原位编辑目标：" + ok + " 活动文档 " + ActiveName());
        if (!ok) { if (!(app.ActiveDocument is A.AssemblyDocument)) LeaveInPlace(); return; }
        var ipc = target.Constructions.InterpartConstructions;
        P.InterpartConstruction copy = null;
        for (int i = 0; i < refs.Length && copy == null; i++) {
            if (refs[i] == null) continue;
            try {
                var cpy = ipc.Add(refs[i]);
                object d0; var st = cpy.get_Status(out d0);
                var circles = CircleEdges(cpy.get_Edges(G.FeatureTopologyQueryTypeConstants.igQueryAll));
                Say("RESULT " + tag + " 复制(引用" + (i + 1) + ") 状态=" + st + " 圆边=" + circles.Count + (circles.Count > 0 ? " " + EdgeInfo(circles[0]) : ""));
                if (st.ToString() == "igFeatureOK" && circles.Count > 0) copy = cpy; else cpy.Delete();
            } catch (Exception e) { Say("RESULT " + tag + " 复制(引用" + (i + 1) + ") 异常 0x" + e.HResult.ToString("X8") + " " + e.Message); }
        }
        if (copy != null) {
            try { copy.Name = "孔位链接-测试"; Say("   " + tag + " 改名 OK → " + copy.Name + " / " + copy.DisplayName); } catch (Exception e) { Say("   " + tag + " 改名异常 0x" + e.HResult.ToString("X8")); }
            try { copy.Visible = false; Say("   " + tag + " 隐藏 OK"); } catch (Exception e) { Say("   " + tag + " 隐藏异常 0x" + e.HResult.ToString("X8")); }
            try {
                var feats = target.DesignEdgebarFeatures;
                string list = ""; for (int i = 1; i <= feats.Count && i <= 8; i++) { try { list += " [" + i + "]" + (string)((dynamic)feats.Item(i)).Name; } catch { list += " [" + i + "]?"; } }
                Say("   " + tag + " 特征树 " + list);
                object first = feats.Item(1);
                copy.Reorder(first, true);
                list = ""; for (int i = 1; i <= feats.Count && i <= 8; i++) { try { list += " [" + i + "]" + (string)((dynamic)feats.Item(i)).Name; } catch { list += " [" + i + "]?"; } }
                Say("   " + tag + " 挪到最前以后 " + list);
            } catch (Exception e) { Say("   " + tag + " Reorder 异常 0x" + e.HResult.ToString("X8") + " " + e.Message); }
            var circ3d =CircleEdges(copy.get_Edges(G.FeatureTopologyQueryTypeConstants.igQueryAll))[0];
            var bp = target.RefPlanes.AddParallelByDistance(FaceAtZ(target, bottomZ), 0.0, P.ReferenceElementConstants.igNormalSide, M, M, M, M);
            var prof = target.ProfileSets.Add().Profiles.Add(bp);
            object inc = null;
            try { prof.IncludeEdge(circ3d, out inc); } catch (Exception e) { Say("   " + tag + " IncludeEdge 异常 0x" + e.HResult.ToString("X8")); }
            double x2, y2; prof.Convert3DCoordinate(0.03, 0.04, bottomZ, out x2, out y2);
            var h2 = prof.Holes2d.Add(x2, y2);
            var circ = inc ?? (object)prof.Circles2d.Item(prof.Circles2d.Count);
            try { ((S.Relations2d)prof.Relations2d).AddKeypoint(h2, 0, circ, (int)SolidEdgeConstants.KeypointIndexConstants.igCircleCenter, M); } catch (Exception e) { Say("   " + tag + " Keypoint 异常 0x" + e.HResult.ToString("X8")); }
            try { prof.ToggleConstruction(circ); } catch { }
            prof.End(P.ProfileValidationType.igProfileClosed);
            double v0 = Vol(target);
            P.Hole hole = null;
            foreach (var sd in new[]{ P.FeaturePropertyConstants.igLeft, P.FeaturePropertyConstants.igRight }) {
                try {
                    hole = ((P.Model)target.Models.Item(1)).Holes.AddThroughAll(prof, sd, Regular(target.HoleDataCollection, 0.006));
                    object desc; var hs = hole.GetStatusEx(out desc);
                    if ((int)hs == (int)P.FeatureStatusConstants.igFeatureOK) break;
                    hole.Delete(); hole = null;
                } catch { hole = null; break; }
            }
            Say("RESULT " + tag + " 打孔 " + (hole != null ? "OK" : "失败") + " 切出≈" + ((v0 - Vol(target)) / (Math.PI * 0.003 * 0.003 * 0.01)).ToString("0.00") + " 圆边 " + Circles(target));
        }
        LeaveInPlace();
    }
}
