using System;
using System.Collections.Generic;
using System.IO;
using A=SolidEdgeAssembly;
using F=SolidEdgeFramework;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;
using S=SolidEdgeFrameworkSupport;

// AsmDrivenHoleProbe 的第二部分（2026-10-20）：和 AsmDrivenHoleProbe.cs 一起编译，第二个参数选实验（M / Y / X）。
//   M  一个轮廓里放多个 Hole2d：Profile.End 的各种写法下还剩几个、切出几个孔
//   Y  原位编辑目标零件（StartCommand 57872 = 激活嵌入对象，等于在装配里双击零件）后
//      IncludeEdge(参考零件孔口圆边的装配引用) —— 天工原生跨零件关联（特征前面带锁链）；挪参考零件看孔跟不跟
partial class AsmDrivenHoleProbe {
    // ---------------- M：多 Hole2d ----------------
    static void Multi(){
        var variants = new object[]{ "none", 0, 1, 8192, 1 | 8192, 524288, 4 };
        foreach (var v in variants) {
            var p = NewPlate(0.1, 0.1, 0.01);
            double v0 = Vol(p.Part);
            var plane = p.Part.RefPlanes.AddParallelByDistance(FaceAtZ(p.Part, 0.01), 0.0, P.ReferenceElementConstants.igNormalSide, M, M, M, M);
            var prof = p.Part.ProfileSets.Add().Profiles.Add(plane);
            foreach (var c in new[]{ new[]{0.02,0.05}, new[]{0.05,0.05}, new[]{0.08,0.05} }) { double x2, y2; prof.Convert3DCoordinate(c[0], c[1], 0.01, out x2, out y2); prof.Holes2d.Add(x2, y2); }
            string endr = "不调";
            if (!(v is string)) { try { endr = prof.End((P.ProfileValidationType)(int)v).ToString(); } catch (Exception e) { endr = "异常 " + e.Message; } }
            int after = prof.Holes2d.Count;
            var h = Through(p, prof, Regular(p.Part.HoleDataCollection, 0.006));
            double cut = v0 - Vol(p.Part), want = Math.PI * 0.003 * 0.003 * 0.01;
            int h2 = -1; if (h != null) { try { int np; Array pr = new object[0]; h.GetProfiles(out np, ref pr); h2 = ((P.Profile)pr.GetValue(0)).Holes2d.Count; } catch { } }
            Say("RESULT M End(" + v + ") 返回 " + endr + " End 后 Hole2d=" + after + " 孔=" + (h == null ? "失败" : "OK") + " 切出孔数≈" + (cut / want).ToString("0.00") + " 特征里 Hole2d=" + h2);
            p.Part.Close(false);
        }
        // 同一轮廓：End 之后逐个看原来的 Hole2d 对象还在不在
        {
            var p = NewPlate(0.1, 0.1, 0.01);
            var plane = p.Part.RefPlanes.AddParallelByDistance(FaceAtZ(p.Part, 0.01), 0.0, P.ReferenceElementConstants.igNormalSide, M, M, M, M);
            var prof = p.Part.ProfileSets.Add().Profiles.Add(plane);
            var list = new List<P.Hole2d>();
            foreach (var c in new[]{ new[]{0.02,0.05}, new[]{0.05,0.05}, new[]{0.08,0.05} }) { double x2, y2; prof.Convert3DCoordinate(c[0], c[1], 0.01, out x2, out y2); list.Add(prof.Holes2d.Add(x2, y2)); }
            prof.End(P.ProfileValidationType.igProfileClosed);
            for (int i = 0; i < list.Count; i++) {
                try { double x, y; list[i].GetCenterPoint(out x, out y); Say("   M 第 " + (i+1) + " 个 Hole2d 还活着 (" + Mm(x) + "," + Mm(y) + ")"); }
                catch (Exception e) { Say("   M 第 " + (i+1) + " 个 Hole2d 已失效 0x" + e.HResult.ToString("X8")); }
            }
            for (int i = 1; i <= prof.Holes2d.Count; i++) { double x, y; prof.Holes2d.Item(i).GetCenterPoint(out x, out y); Say("   M 轮廓里第 " + i + " 个 (" + Mm(x) + "," + Mm(y) + ")"); }
            p.Part.Close(false);
        }
    }

    // ---------------- Y：原位编辑 + IncludeEdge ----------------
    static void Wait(int ms){ var t = DateTime.Now.AddMilliseconds(ms); while (DateTime.Now < t) { try { app.DoIdle(); } catch { } System.Threading.Thread.Sleep(100); } }
    static string ActiveName(){ try { dynamic d = app.ActiveDocument; return Microsoft.VisualBasic.Information.TypeName(d) + ":" + (string)d.Name; } catch (Exception e) { return "?" + e.Message; } }
    static string SafeIP(P.PartDocument p){ try { return p.InPlaceActivated.ToString(); } catch (Exception e) { return "?" + e.Message; } }

    static void Y(){
        object allow = null;
        try { app.GetGlobalParameter(F.ApplicationGlobalConstants.seApplicationGlobalAllowInterPart, ref allow); Say("   AllowInterPart=" + allow); } catch (Exception e) { Say("   读 AllowInterPart 异常 " + e.Message); }
        try { app.SetGlobalParameter(F.ApplicationGlobalConstants.seApplicationGlobalAllowInterPart, true); Say("   设 AllowInterPart=true OK"); } catch (Exception e) { Say("   设 AllowInterPart 异常 " + e.Message); }

        // A：两个 Φ10 孔（各自一个孔特征）；B 实心，z+30
        var a = NewPlate(0.1, 0.1, 0.01);
        foreach (var c in new[]{ new[]{0.03,0.04}, new[]{0.07,0.04} }) {
            var pl = a.Part.RefPlanes.AddParallelByDistance(FaceAtZ(a.Part, 0.01), 0.0, P.ReferenceElementConstants.igNormalSide, M, M, M, M);
            var pr = a.Part.ProfileSets.Add().Profiles.Add(pl);
            double x2, y2; pr.Convert3DCoordinate(c[0], c[1], 0.01, out x2, out y2); pr.Holes2d.Add(x2, y2); pr.End(P.ProfileValidationType.igProfileClosed);
            Through(a, pr, Regular(a.Part.HoleDataCollection, 0.010));
        }
        Say("   A 圆边 " + Circles(a.Part));
        string pa = Path.Combine(Out, "Y-A.par"), pb = Path.Combine(Out, "Y-B.par"), pasm = Path.Combine(Out, "Y.asm");
        a.Part.SaveAs(pa); a.Part.Close(false);
        var b = NewPlate(0.1, 0.1, 0.01); b.Part.SaveAs(pb); b.Part.Close(false);
        var asm = (A.AssemblyDocument)app.Documents.Add("SolidEdge.AssemblyDocument", Tpl + "iso metric assembly.asm");
        var occA = asm.Occurrences.AddByFilename(pa);
        var occB = asm.Occurrences.AddByFilename(pb);
        Array mB = new double[]{ 1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0.03,1 }; occB.PutMatrix(ref mB, true);
        asm.SaveAs(pasm);
        var partA = (P.PartDocument)occA.OccurrenceDocument;
        var partB = (P.PartDocument)occB.OccurrenceDocument;
        var e1 = TopCircle(partA, 0.03, 0.04, 0.01); var e2 = TopCircle(partA, 0.07, 0.04, 0.01);
        Say("   A 孔口圆边 " + (e1 != null) + " " + (e2 != null));
        object r1 = asm.CreateReference(occA, e1), r2 = asm.CreateReference(occA, e2);

        // Y1：不进原位编辑，开了 AllowInterPart 再试一次
        {
            var bp = partB.RefPlanes.AddParallelByDistance(FaceAtZ(partB, 0.0), 0.0, P.ReferenceElementConstants.igNormalSide, M, M, M, M);
            var prof = partB.ProfileSets.Add().Profiles.Add(bp);
            try { object inc; prof.IncludeEdge(r1, out inc); Say("RESULT Y1 不原位 IncludeEdge OK Circles2d=" + prof.Circles2d.Count); }
            catch (Exception e) { Say("RESULT Y1 不原位 IncludeEdge 异常 0x" + e.HResult.ToString("X8")); }
            try { prof.Delete(); } catch { }
        }

        // Y2：进原位编辑 B
        Say("   原位前 活动文档 " + ActiveName() + " asm.InPlaceActivated=" + asm.InPlaceActivated);
        try { app.ActiveSelectSet.RemoveAll(); } catch { }
        try { occB.Select(true); } catch (Exception e) { Say("   选 B 异常 " + e.Message); }
        try { app.StartCommand((F.SolidEdgeCommandConstants)57872); Say("   StartCommand(57872 激活嵌入对象) 已发"); } catch (Exception e) { Say("   StartCommand 57872 异常 " + e.Message); }
        Wait(4000);
        Say("   原位后 活动文档 " + ActiveName() + " asm.InPlaceActivated=" + asm.InPlaceActivated + " B.InPlaceActivated=" + SafeIP(partB));
        var editB = app.ActiveDocument as P.PartDocument;
        if (editB == null) { Say("RESULT Y2 没进原位编辑"); try { asm.Close(false); } catch { } return; }
        Say("   原位文档就是 occB 的文档：" + ReferenceEquals(editB, partB) + " " + editB.FullName);
        // B 底面打孔，两个孔放进同一个轮廓，各自 Keypoint 到包含进来的 A 圆边圆心
        var bp2 = editB.RefPlanes.AddParallelByDistance(FaceAtZ(editB, 0.0), 0.0, P.ReferenceElementConstants.igNormalSide, M, M, M, M);
        var prof2 = editB.ProfileSets.Add().Profiles.Add(bp2);
        foreach (var pair in new[]{ Tuple.Create(r1, 0.03), Tuple.Create(r2, 0.07) }) {
            object inc = null;
            try { prof2.IncludeEdge(pair.Item1, out inc); Say("RESULT Y2 原位 IncludeEdge OK 返回 " + (inc == null ? "null" : Microsoft.VisualBasic.Information.TypeName(inc)) + " Circles2d=" + prof2.Circles2d.Count); }
            catch (Exception e) { Say("RESULT Y2 原位 IncludeEdge 异常 0x" + e.HResult.ToString("X8") + " " + e.Message); continue; }
            double x2, y2; prof2.Convert3DCoordinate(pair.Item2, 0.04, 0.0, out x2, out y2);
            var h2 = prof2.Holes2d.Add(x2, y2);
            var circ = inc ?? (object)prof2.Circles2d.Item(prof2.Circles2d.Count);
            try { ((S.Relations2d)prof2.Relations2d).AddKeypoint(h2, 0, circ, (int)SolidEdgeConstants.KeypointIndexConstants.igCircleCenter, M); Say("   Y2 Keypoint OK"); } catch (Exception e) { Say("   Y2 Keypoint 异常 0x" + e.HResult.ToString("X8")); }
            try { prof2.ToggleConstruction(circ); } catch (Exception e) { Say("   Y2 ToggleConstruction 异常 " + e.Message); }
        }
        Say("   Y2 End 前 Hole2d=" + prof2.Holes2d.Count);
        try { Say("   Y2 End=" + prof2.End(P.ProfileValidationType.igProfileClosed)); } catch (Exception e) { Say("   Y2 End 异常 " + e.Message); }
        Say("   Y2 End 后 Hole2d=" + prof2.Holes2d.Count);
        var mdl = (P.Model)editB.Models.Item(1);
        double v0 = Vol(editB);
        P.Hole hole = null;
        foreach (var sd in new[]{ P.FeaturePropertyConstants.igLeft, P.FeaturePropertyConstants.igRight }) {
            try {
                hole = mdl.Holes.AddThroughAll(prof2, sd, Regular(editB.HoleDataCollection, 0.006));
                object desc; var st = hole.GetStatusEx(out desc);
                Say("RESULT Y2 打孔 side=" + sd + " 状态=" + st + " 切出孔数≈" + ((v0 - Vol(editB)) / (Math.PI * 0.003 * 0.003 * 0.01)).ToString("0.00"));
                if ((int)st == (int)P.FeatureStatusConstants.igFeatureOK) break;
                hole.Delete(); hole = null;
            } catch (Exception e) { Say("RESULT Y2 打孔 side=" + sd + " 异常 0x" + e.HResult.ToString("X8") + " " + e.Message); break; }
        }
        if (hole != null) { bool ext = false; try { hole.HasExternalLink(out ext); } catch { } Say("   Y2 孔 HasExternalLink=" + ext); }
        try { bool has = false; editB.HasInterpartLinks(ref has); Say("   Y2 B.HasInterpartLinks=" + has); } catch (Exception e) { Say("   HasInterpartLinks 异常 " + e.Message); }
        Say("   Y2 B 圆边 " + Circles(editB));
        // 退出原位
        try { app.StartCommand((F.SolidEdgeCommandConstants)40013); } catch (Exception e) { Say("   CloseAndReturn 异常 " + e.Message); }
        Wait(3000);
        Say("   退出后 活动文档 " + ActiveName() + " asm.InPlaceActivated=" + asm.InPlaceActivated);
        try { bool has = false; asm.HasInterpartLinks(ref has); Say("   asm.HasInterpartLinks=" + has); } catch { }
        try { int n = 0; Array occs = new object[0]; occB.GetInterpartDrivingOccurrences(out n, ref occs); Say("   B 的驱动实例数=" + n); } catch (Exception e) { Say("   GetInterpartDrivingOccurrences 异常 " + e.Message); }
        try { asm.Save(); } catch (Exception e) { Say("   保存异常 " + e.Message); }

        // Y3：挪 A +5mm，看 B 的孔跟不跟
        Array mA = new double[]{ 1,0,0,0, 0,1,0,0, 0,0,1,0, 0.005,0,0,1 }; occA.PutMatrix(ref mA, true);
        Wait(1000);
        Say("RESULT Y3 挪 A 后（不做别的）B 圆边 " + Circles(partB));
        try { asm.UpdateAll(); } catch (Exception e) { Say("   UpdateAll 异常 " + e.Message); }
        Wait(1000);
        Say("RESULT Y3 UpdateAll 后 B 圆边 " + Circles(partB));
        try { app.StartCommand((F.SolidEdgeCommandConstants)33068); } catch (Exception e) { Say("   UpdateAllLinks 异常 " + e.Message); }
        Wait(3000);
        Say("RESULT Y3 更新全部链接后 B 圆边 " + Circles(partB));
        try { asm.Save(); } catch { }
        try { asm.Close(false); } catch { }
    }

    // ---------------- Z：零件间复制（InterpartConstructions）+ 孔草图挂在复制来的圆边上 ----------------
    sealed class Fixture { public A.AssemblyDocument Asm; public A.Occurrence OccA, OccB; public P.PartDocument PartA, PartB; public G.Edge E1, E2; public string AsmPath; }
    static Fixture Build(string tag){
        var a = NewPlate(0.1, 0.1, 0.01);
        foreach (var c in new[]{ new[]{0.03,0.04}, new[]{0.07,0.04} }) {
            var pl = a.Part.RefPlanes.AddParallelByDistance(FaceAtZ(a.Part, 0.01), 0.0, P.ReferenceElementConstants.igNormalSide, M, M, M, M);
            var pr = a.Part.ProfileSets.Add().Profiles.Add(pl);
            double x2, y2; pr.Convert3DCoordinate(c[0], c[1], 0.01, out x2, out y2); pr.Holes2d.Add(x2, y2); pr.End(P.ProfileValidationType.igProfileClosed);
            Through(a, pr, Regular(a.Part.HoleDataCollection, 0.010));
        }
        string pa = Path.Combine(Out, tag + "-A.par"), pb = Path.Combine(Out, tag + "-B.par"), pasm = Path.Combine(Out, tag + ".asm");
        a.Part.SaveAs(pa); a.Part.Close(false);
        var b = NewPlate(0.1, 0.1, 0.01); b.Part.SaveAs(pb); b.Part.Close(false);
        var f = new Fixture { AsmPath = pasm };
        f.Asm = (A.AssemblyDocument)app.Documents.Add("SolidEdge.AssemblyDocument", Tpl + "iso metric assembly.asm");
        f.OccA = f.Asm.Occurrences.AddByFilename(pa);
        f.OccB = f.Asm.Occurrences.AddByFilename(pb);
        Array mB = new double[]{ 1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0.03,1 }; f.OccB.PutMatrix(ref mB, true);
        f.Asm.SaveAs(pasm);
        f.PartA = (P.PartDocument)f.OccA.OccurrenceDocument;
        f.PartB = (P.PartDocument)f.OccB.OccurrenceDocument;
        f.E1 = TopCircle(f.PartA, 0.03, 0.04, 0.01); f.E2 = TopCircle(f.PartA, 0.07, 0.04, 0.01);
        return f;
    }
    static bool EnterInPlace(Fixture f){
        try { app.ActiveSelectSet.RemoveAll(); } catch { }
        try { f.OccB.Select(true); } catch (Exception e) { Say("   选 B 异常 " + e.Message); }
        try { app.StartCommand((F.SolidEdgeCommandConstants)57872); } catch (Exception e) { Say("   StartCommand 57872 异常 " + e.Message); }
        Wait(4000);
        bool ok = ReferenceEquals(app.ActiveDocument, f.PartB);
        Say("   原位编辑 B：" + ok + " 活动文档 " + ActiveName());
        return ok;
    }
    static void LeaveInPlace(){
        try { app.StartCommand((F.SolidEdgeCommandConstants)40013); } catch (Exception e) { Say("   CloseAndReturn 异常 " + e.Message); }
        Wait(3000);
        Say("   退出原位 活动文档 " + ActiveName());
    }
    // 零件间复制出来的东西里找圆边（零件 B 局部坐标）
    static List<G.Edge> CircleEdges(object edgesObj){
        var list = new List<G.Edge>();
        if (edgesObj == null) return list;
        foreach (object o in (System.Collections.IEnumerable)edgesObj) { var e = o as G.Edge; if (e != null && e.Geometry is G.Circle) list.Add(e); }
        return list;
    }
    static string EdgeInfo(G.Edge e){
        var c = (G.Circle)e.Geometry; Array cc = new double[3], ax = new double[3]; double r; c.GetCircleData(ref cc, ref ax, out r);
        return "(" + Mm(D(cc,0)) + "," + Mm(D(cc,1)) + "," + Mm(D(cc,2)) + " Φ" + Mm(r*2) + ")";
    }

    static void Z(){
        try { app.SetGlobalParameter(F.ApplicationGlobalConstants.seApplicationGlobalAllowInterPart, true); } catch { }
        var f = Build("Z");
        object r1 = f.Asm.CreateReference(f.OccA, f.E1), r2 = f.Asm.CreateReference(f.OccA, f.E2);
        var ipc = f.PartB.Constructions.InterpartConstructions;
        P.InterpartConstruction c1 = null, c2 = null;
        // Z1：不进原位，Add2(目标零件, 装配引用)
        try { c1 = ipc.Add2(f.PartB, r1); Say("RESULT Z1 不原位 Add2(B, 引用) OK"); } catch (Exception e) { Say("RESULT Z1 不原位 Add2 异常 0x" + e.HResult.ToString("X8") + " " + e.Message); }
        if (c1 == null) { try { c1 = ipc.Add(r1); Say("RESULT Z1 不原位 Add(引用) OK"); } catch (Exception e) { Say("RESULT Z1 不原位 Add 异常 0x" + e.HResult.ToString("X8") + " " + e.Message); } }
        bool inPlace = false;
        if (c1 == null) {
            inPlace = EnterInPlace(f);
            if (inPlace) {
                ipc = f.PartB.Constructions.InterpartConstructions;
                try { c1 = ipc.Add(r1); Say("RESULT Z1 原位 Add(引用) OK"); } catch (Exception e) { Say("RESULT Z1 原位 Add 异常 0x" + e.HResult.ToString("X8") + " " + e.Message); }
                if (c1 == null) { try { c1 = ipc.Add2(f.PartB, r1); Say("RESULT Z1 原位 Add2 OK"); } catch (Exception e) { Say("RESULT Z1 原位 Add2 异常 0x" + e.HResult.ToString("X8") + " " + e.Message); } }
                // 原位里也再试一遍 IncludeEdge 的几种传法
                var bpT = f.PartB.RefPlanes.AddParallelByDistance(FaceAtZ(f.PartB, 0.0), 0.0, P.ReferenceElementConstants.igNormalSide, M, M, M, M);
                var pT = f.PartB.ProfileSets.Add().Profiles.Add(bpT);
                object r2b = null; try { r2b = f.Asm.CreateReference2(f.OccA, f.E1); } catch (Exception e) { Say("   CreateReference2 异常 " + e.Message); }
                foreach (var t in new[]{ Tuple.Create("零件边", (object)f.E1), Tuple.Create("CreateReference2", r2b) }) {
                    if (t.Item2 == null) continue;
                    try { object inc; pT.IncludeEdge(t.Item2, out inc); Say("RESULT Y4 原位 IncludeEdge(" + t.Item1 + ") OK"); }
                    catch (Exception e) { Say("RESULT Y4 原位 IncludeEdge(" + t.Item1 + ") 异常 0x" + e.HResult.ToString("X8")); }
                }
                try { pT.Delete(); } catch { }
            }
        }
        if (c1 == null) { Say("RESULT Z 零件间复制建不出来"); if (inPlace) LeaveInPlace(); try { f.Asm.Close(false); } catch { } return; }
        try { c2 = ipc.Add2(f.PartB, r2); } catch { try { c2 = ipc.Add(r2); } catch (Exception e) { Say("   第二个复制异常 " + e.Message); } }
        object st; try { object d0; st = c1.get_Status(out d0); } catch (Exception e) { st = e.Message; }
        string nm = ""; try { nm = c1.Name + " / " + c1.DisplayName; } catch { }
        Say("   Z1 复制特征 " + nm + " 状态 " + st + " 类型 " + SafeType(c1));
        var ce1 = CircleEdges(SafeEdges(c1)); var ce2 = c2 == null ? new List<G.Edge>() : CircleEdges(SafeEdges(c2));
        Say("   Z1 复制来的圆边 " + ce1.Count + " 个 " + (ce1.Count > 0 ? EdgeInfo(ce1[0]) : "") + "；第二个 " + ce2.Count);
        try { bool has = false; f.PartB.HasInterpartLinks(ref has); Say("   Z1 B.HasInterpartLinks=" + has); } catch (Exception e) { Say("   HasInterpartLinks 异常 " + e.Message); }

        // Z2：B 底面上一个轮廓：IncludeEdge(复制来的圆边) + Hole2d 关键点约束，两个孔放一个轮廓
        if (ce1.Count > 0) {
            var bp = f.PartB.RefPlanes.AddParallelByDistance(FaceAtZ(f.PartB, 0.0), 0.0, P.ReferenceElementConstants.igNormalSide, M, M, M, M);
            var prof = f.PartB.ProfileSets.Add().Profiles.Add(bp);
            var pairs = new List<Tuple<G.Edge, double>>{ Tuple.Create(ce1[0], 0.03) };
            if (ce2.Count > 0) pairs.Add(Tuple.Create(ce2[0], 0.07));
            foreach (var pr in pairs) {
                object inc = null;
                try { prof.IncludeEdge(pr.Item1, out inc); Say("   Z2 IncludeEdge(复制圆边) OK Circles2d=" + prof.Circles2d.Count); }
                catch (Exception e) { Say("   Z2 IncludeEdge(复制圆边) 异常 0x" + e.HResult.ToString("X8") + " " + e.Message); continue; }
                double x2, y2; prof.Convert3DCoordinate(pr.Item2, 0.04, 0.0, out x2, out y2);
                var h2 = prof.Holes2d.Add(x2, y2);
                var circ = inc ?? (object)prof.Circles2d.Item(prof.Circles2d.Count);
                try { ((S.Relations2d)prof.Relations2d).AddKeypoint(h2, 0, circ, (int)SolidEdgeConstants.KeypointIndexConstants.igCircleCenter, M); } catch (Exception e) { Say("   Z2 Keypoint 异常 0x" + e.HResult.ToString("X8")); }
                try { prof.ToggleConstruction(circ); } catch { }
            }
            Say("   Z2 End 前 Hole2d=" + prof.Holes2d.Count + " End=" + prof.End(P.ProfileValidationType.igProfileClosed) + " End 后 Hole2d=" + prof.Holes2d.Count);
            var mdl = (P.Model)f.PartB.Models.Item(1);
            double v0 = Vol(f.PartB);
            P.Hole hole = null;
            foreach (var sd in new[]{ P.FeaturePropertyConstants.igLeft, P.FeaturePropertyConstants.igRight }) {
                try {
                    hole = mdl.Holes.AddThroughAll(prof, sd, Regular(f.PartB.HoleDataCollection, 0.006));
                    object desc; var hs = hole.GetStatusEx(out desc);
                    Say("RESULT Z2 打孔 side=" + sd + " 状态=" + hs + " 切出孔数≈" + ((v0 - Vol(f.PartB)) / (Math.PI * 0.003 * 0.003 * 0.01)).ToString("0.00"));
                    if ((int)hs == (int)P.FeatureStatusConstants.igFeatureOK) break;
                    hole.Delete(); hole = null;
                } catch (Exception e) { Say("RESULT Z2 打孔 side=" + sd + " 异常 0x" + e.HResult.ToString("X8") + " " + e.Message); hole = null; break; }
            }
            if (hole != null) { bool ext = false; try { hole.HasExternalLink(out ext); } catch { } Say("   Z2 孔 HasExternalLink=" + ext); }
            Say("   Z2 B 圆边 " + Circles(f.PartB));
        }
        if (inPlace) LeaveInPlace();
        try { f.Asm.Save(); } catch (Exception e) { Say("   保存异常 " + e.Message); }
        try { int n = 0; Array occs = new object[0]; f.OccB.GetInterpartDrivingOccurrences(out n, ref occs); Say("   B 的驱动实例数=" + n); } catch (Exception e) { Say("   GetInterpartDrivingOccurrences 异常 " + e.Message); }

        // Z3：挪 A +5mm
        Array mA = new double[]{ 1,0,0,0, 0,1,0,0, 0,0,1,0, 0.005,0,0,1 }; f.OccA.PutMatrix(ref mA, true);
        Wait(1000);
        Say("RESULT Z3 挪 A 后 B 圆边 " + Circles(f.PartB));
        try { f.Asm.UpdateAll(); } catch (Exception e) { Say("   UpdateAll 异常 " + e.Message); }
        Wait(1500);
        Say("RESULT Z3 UpdateAll 后 B 圆边 " + Circles(f.PartB));
        try { c1.Update(); if (c2 != null) c2.Update(); Say("   InterpartConstruction.Update OK"); } catch (Exception e) { Say("   Update 异常 " + e.Message); }
        try { ((P.Model)f.PartB.Models.Item(1)).Recompute(); } catch (Exception e) { Say("   Recompute 异常 " + e.Message); }
        Wait(1500);
        Say("RESULT Z3 Update+Recompute 后 B 圆边 " + Circles(f.PartB));
        try { f.Asm.Save(); } catch { }
        try { f.Asm.Close(true); } catch { }
        // Z4：重开
        try {
            var re = (A.AssemblyDocument)app.Documents.Open(f.AsmPath);
            Wait(2000);
            var reB = (P.PartDocument)((A.Occurrence)re.Occurrences.Item(2)).OccurrenceDocument;
            Say("RESULT Z4 重开 B 圆边 " + Circles(reB) + " 零件间复制数=" + reB.Constructions.InterpartConstructions.Count);
            try { bool has = false; reB.HasInterpartLinks(ref has); Say("   Z4 B.HasInterpartLinks=" + has); } catch { }
            re.Close(false);
        } catch (Exception e) { Say("RESULT Z4 重开异常 " + e.Message); }
    }
    static object SafeEdges(P.InterpartConstruction c){ try { return c.get_Edges(G.FeatureTopologyQueryTypeConstants.igQueryAll); } catch (Exception e) { Say("   读 Edges 异常 " + e.Message); return null; } }
    static string SafeType(object o){ try { return Microsoft.VisualBasic.Information.TypeName(o); } catch { return "?"; } }
}

// 进程外调 CAD：CAD 忙（RPC_E_CALL_REJECTED / RETRYLATER）时由 COM 自动等，最多 120 秒
[System.Runtime.InteropServices.ComImport, System.Runtime.InteropServices.Guid("00000016-0000-0000-C000-000000000046"), System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
interface IProbeMessageFilter {
    [System.Runtime.InteropServices.PreserveSig] int HandleInComingCall(int type, IntPtr task, int ticks, IntPtr info);
    [System.Runtime.InteropServices.PreserveSig] int RetryRejectedCall(IntPtr task, int ticks, int reject);
    [System.Runtime.InteropServices.PreserveSig] int MessagePending(IntPtr task, int ticks, int pending);
}
sealed class ProbeFilter : IProbeMessageFilter {
    [System.Runtime.InteropServices.DllImport("ole32.dll")] static extern int CoRegisterMessageFilter(IProbeMessageFilter f, out IProbeMessageFilter old);
    static ProbeFilter keep;
    public static void Register(){ keep = new ProbeFilter(); IProbeMessageFilter old; CoRegisterMessageFilter(keep, out old); }
    public int HandleInComingCall(int a, IntPtr b, int c, IntPtr d){ return 0; }
    public int RetryRejectedCall(IntPtr a, int ticks, int reject){ return ticks < 120000 ? 200 : -1; }
    public int MessagePending(IntPtr a, int b, int c){ return 2; }
}
