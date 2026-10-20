using System;
using System.Collections.Generic;
using System.IO;
using A=SolidEdgeAssembly;
using F=SolidEdgeFramework;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;
using S=SolidEdgeFrameworkSupport;

// W：原位编辑 B，零件间复制（InterpartConstructions.Add）换各种来源，看哪种能建成、复制出来的有没有圆边；
// 成了就把孔挂在复制来的圆边上、挪 A、更新、重开。
partial class AsmDrivenHoleProbe {
    static G.Face CylFace(P.PartDocument part, double x, double y){
        foreach (G.Face f in (G.Faces)((G.Body)((P.Model)part.Models.Item(1)).Body).get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll)) {
            var c = f.Geometry as G.Cylinder; if (c == null) continue;
            Array bp = new double[3], ax = new double[3]; double r; c.GetCylinderData(ref bp, ref ax, out r);
            if (Math.Abs(D(bp,0) - x) < 1e-6 && Math.Abs(D(bp,1) - y) < 1e-6) return f;
        }
        return null;
    }
    static int Count(object coll){ if (coll == null) return -1; int n = 0; foreach (object o in (System.Collections.IEnumerable)coll) n++; return n; }

    static void W(){
        try { app.SetGlobalParameter(F.ApplicationGlobalConstants.seApplicationGlobalAllowInterPart, true); } catch { }
        var f = Build("W");
        var cyl1 = CylFace(f.PartA, 0.03, 0.04); var cyl2 = CylFace(f.PartA, 0.07, 0.04);
        Say("   A 孔壁 " + (cyl1 != null) + " " + (cyl2 != null));
        if (!EnterInPlace(f)) { Say("RESULT W 进不了原位"); try { f.Asm.Close(false); } catch { } return; }
        var ipc = f.PartB.Constructions.InterpartConstructions;
        var sources = new List<Tuple<string, Func<object>>> {
            Tuple.Create("引用(孔壁)", (Func<object>)(() => f.Asm.CreateReference(f.OccA, cyl1))),
            Tuple.Create("引用(A顶面)", (Func<object>)(() => f.Asm.CreateReference(f.OccA, FaceAtZ(f.PartA, 0.01)))),
            Tuple.Create("数组[引用(孔壁)]", (Func<object>)(() => new object[]{ f.Asm.CreateReference(f.OccA, cyl1) })),
            Tuple.Create("CreateReference2(孔壁)", (Func<object>)(() => f.Asm.CreateReference2(f.OccA, cyl1))),
            Tuple.Create("引用(A实体)", (Func<object>)(() => f.Asm.CreateReference(f.OccA, ((P.Model)f.PartA.Models.Item(1)).Body))),
            Tuple.Create("实例A", (Func<object>)(() => f.OccA)),
        };
        P.InterpartConstruction good = null; string goodName = "";
        foreach (var s in sources) {
            P.InterpartConstruction c = null;
            try { var src = s.Item2(); c = ipc.Add(src); }
            catch (Exception e) { Say("RESULT W Add(" + s.Item1 + ") 异常 0x" + e.HResult.ToString("X8") + " " + e.Message); continue; }
            object d0 = null, st = null; try { st = c.get_Status(out d0); } catch (Exception e) { st = e.Message; }
            object edges = null, faces = null;
            try { edges = c.get_Edges(G.FeatureTopologyQueryTypeConstants.igQueryAll); } catch { }
            try { faces = c.get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll); } catch { }
            var circles = CircleEdges(edges);
            Say("RESULT W Add(" + s.Item1 + ") 状态=" + st + " 边=" + Count(edges) + " 面=" + Count(faces) + " 圆边=" + circles.Count + (circles.Count > 0 ? " 例 " + EdgeInfo(circles[0]) : ""));
            if (good == null && circles.Count > 0 && st != null && st.ToString() == "igFeatureOK") { good = c; goodName = s.Item1; continue; }
            try { c.Delete(); } catch { }
        }
        if (good == null) { Say("RESULT W 没有一种来源能复制出圆边"); LeaveInPlace(); try { f.Asm.Close(false); } catch { } return; }
        Say("   W 用 " + goodName);
        P.InterpartConstruction good2 = null;
        try {
            object src2 = goodName.StartsWith("数组") ? (object)new object[]{ f.Asm.CreateReference(f.OccA, cyl2) } : f.Asm.CreateReference(f.OccA, cyl2);
            if (goodName.Contains("孔壁")) good2 = ipc.Add(src2);
        } catch (Exception e) { Say("   第二个孔壁复制异常 " + e.Message); }
        // 孔：B 底面；同一轮廓两个 Hole2d，各自约束到复制来的圆边圆心
        var bp = f.PartB.RefPlanes.AddParallelByDistance(FaceAtZ(f.PartB, 0.0), 0.0, P.ReferenceElementConstants.igNormalSide, M, M, M, M);
        var prof = f.PartB.ProfileSets.Add().Profiles.Add(bp);
        foreach (var pr in new[]{ Tuple.Create(good, 0.03), Tuple.Create(good2, 0.07) }) {
            if (pr.Item1 == null) continue;
            var circles = CircleEdges(pr.Item1.get_Edges(G.FeatureTopologyQueryTypeConstants.igQueryAll));
            if (circles.Count == 0) continue;
            object inc = null;
            try { prof.IncludeEdge(circles[0], out inc); Say("   W IncludeEdge(复制圆边 " + EdgeInfo(circles[0]) + ") OK"); }
            catch (Exception e) { Say("   W IncludeEdge 异常 0x" + e.HResult.ToString("X8")); continue; }
            double x2, y2; prof.Convert3DCoordinate(pr.Item2, 0.04, 0.0, out x2, out y2);
            var h2 = prof.Holes2d.Add(x2, y2);
            var circ = inc ?? (object)prof.Circles2d.Item(prof.Circles2d.Count);
            try { ((S.Relations2d)prof.Relations2d).AddKeypoint(h2, 0, circ, (int)SolidEdgeConstants.KeypointIndexConstants.igCircleCenter, M); Say("   W Keypoint OK"); } catch (Exception e) { Say("   W Keypoint 异常 0x" + e.HResult.ToString("X8")); }
            try { prof.ToggleConstruction(circ); } catch { }
        }
        Say("   W End 前 Hole2d=" + prof.Holes2d.Count + " End=" + prof.End(P.ProfileValidationType.igProfileClosed) + " End 后 Hole2d=" + prof.Holes2d.Count);
        var mdl = (P.Model)f.PartB.Models.Item(1);
        double v0 = Vol(f.PartB);
        P.Hole hole = null;
        foreach (var sd in new[]{ P.FeaturePropertyConstants.igLeft, P.FeaturePropertyConstants.igRight }) {
            try {
                hole = mdl.Holes.AddThroughAll(prof, sd, Regular(f.PartB.HoleDataCollection, 0.006));
                object desc; var hs = hole.GetStatusEx(out desc);
                Say("RESULT W 打孔 side=" + sd + " 状态=" + hs + " 切出孔数≈" + ((v0 - Vol(f.PartB)) / (Math.PI * 0.003 * 0.003 * 0.01)).ToString("0.00"));
                if ((int)hs == (int)P.FeatureStatusConstants.igFeatureOK) break;
                hole.Delete(); hole = null;
            } catch (Exception e) { Say("RESULT W 打孔 side=" + sd + " 异常 0x" + e.HResult.ToString("X8") + " " + e.Message); hole = null; break; }
        }
        Say("   W B 圆边 " + Circles(f.PartB));
        LeaveInPlace();
        try { f.Asm.Save(); } catch (Exception e) { Say("   保存异常 " + e.Message); }
        Array mA = new double[]{ 1,0,0,0, 0,1,0,0, 0,0,1,0, 0.005,0,0,1 }; f.OccA.PutMatrix(ref mA, true);
        Wait(1500);
        Say("RESULT W 挪 A +5 后 B 圆边 " + Circles(f.PartB));
        try { f.Asm.UpdateAll(); } catch (Exception e) { Say("   UpdateAll 异常 " + e.Message); }
        Wait(1500);
        Say("RESULT W UpdateAll 后 B 圆边 " + Circles(f.PartB));
        try { good.Update(); if (good2 != null) good2.Update(); ((P.Model)f.PartB.Models.Item(1)).Recompute(); } catch (Exception e) { Say("   Update/Recompute 异常 " + e.Message); }
        Wait(1500);
        Say("RESULT W Update+Recompute 后 B 圆边 " + Circles(f.PartB));
        try { f.Asm.Save(); f.Asm.Close(true); } catch { }
        try {
            var re = (A.AssemblyDocument)app.Documents.Open(f.AsmPath);
            Wait(2000);
            var reB = (P.PartDocument)((A.Occurrence)re.Occurrences.Item(2)).OccurrenceDocument;
            Say("RESULT W 重开 B 圆边 " + Circles(reB) + " 零件间复制数=" + reB.Constructions.InterpartConstructions.Count);
            re.Close(false);
        } catch (Exception e) { Say("RESULT W 重开异常 " + e.Message); }
    }
}
