using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using A=SolidEdgeAssembly;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;
using F=SolidEdgeFramework;

namespace TianGongCadSuite {
    // ---------- 原生（真机）：点一个带孔的面自动认孔 / 点圆柱面认孔 / 沉孔取下面的孔径 ----------
    // 夹具是一块 100×100×20 的板，上面造出：
    //   2 个圆柱沉孔 Φ11/Φ6.6（深 6）、1 个锥形沉孔 Φ11 90°/Φ5、1 个孔口倒角 0.5×45° 的 Φ6.6 通孔、
    //   1 个螺纹孔 M6（内小径 Φ4.917），外加 1 个 Φ20 高 5 的圆凸台（必须被排除，不能被当成孔）。
    // 然后：面扫描 → 归类 → 往另一块板（B）上配做，用切除体积钉死"配的是 Φ6.6 而不是 Φ11"。
    public static partial class AutoHoleTests {
        static string N3(double v){ return v.ToString("0.####", CultureInfo.InvariantCulture); }

        static G.Face CylinderFaceNear(P.Model m, double x, double y, double radius){
            foreach (G.Face f in (G.Faces)((G.Body)m.Body).get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll)) {
                var c = f.Geometry as G.Cylinder; if (c == null) continue;
                if (Math.Abs(c.Radius - radius) > 1e-6) continue;
                Array bp = new double[3], ax = new double[3]; double r = 0;
                c.GetCylinderData(ref bp, ref ax, out r);
                if (Math.Sqrt(Math.Pow(D(bp,0)-x,2) + Math.Pow(D(bp,1)-y,2)) < 1e-6) return f;
            }
            return null;
        }
        static G.Face ConeFaceNear(P.Model m, double x, double y){
            foreach (G.Face f in (G.Faces)((G.Body)m.Body).get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll)) {
                var c = f.Geometry as G.Cone; if (c == null) continue;
                Array bp = new double[3], ax = new double[3]; double r = 0, half = 0; bool expanding = false;
                c.GetConeData(ref bp, ref ax, out r, out half, out expanding);
                if (Math.Sqrt(Math.Pow(D(bp,0)-x,2) + Math.Pow(D(bp,1)-y,2)) < 1e-6) return f;
            }
            return null;
        }
        static G.Edge CircleEdgeAt(P.Model m, double x, double y, double z, double diaMm){
            foreach (G.Edge e in (G.Edges)((G.Body)m.Body).get_Edges(G.FeatureTopologyQueryTypeConstants.igQueryAll)) {
                var c = e.Geometry as G.Circle; if (c == null) continue;
                Array cc = new double[3], ax = new double[3]; double r = 0;
                try { c.GetCircleData(ref cc, ref ax, out r); } catch { continue; }
                if (Math.Abs(r*2000.0 - diaMm) > 0.02) continue;
                if (Math.Abs(D(cc,0)-x) > 1e-6 || Math.Abs(D(cc,1)-y) > 1e-6 || Math.Abs(D(cc,2)-z) > 1e-6) continue;
                return e;
            }
            return null;
        }
        static void DrillAt(F.Application app, P.PartDocument part, P.Model model, double x, double y, HoleSpec spec, string what){
            var top = PlanarFaceAtZ(model, 0.010);
            if (top == null) throw new InvalidOperationException("找不到顶面");
            var target = AutoHoleReader.ReadTarget(top);
            var res = AutoHoleWriter.Drill(target, spec, new V3[]{ new V3(x, y, 0.010) });
            if (res.Created != 1) throw new InvalidOperationException(what + " 没打出来：" + string.Join("；", res.Failures.ToArray()));
            Pump(300);
        }

        // 圆柱面在某个点的法向约定（真机诊断）：材料在圆柱**里**=凸台，材料在圆柱**外**=孔。
        static G.Face BodyCylinder(P.Model m, double x, double y, double radius){
            return CylinderFaceNear(m, x, y, radius);
        }
        static void DumpWallNormal(G.Face wall, string what, double x, double y, double r, double z){
            if (wall == null) { Console.WriteLine("WALL " + what + "：找不到这张面"); return; }
            var cyl = wall.Geometry as G.Cylinder;
            if (cyl == null) { Console.WriteLine("WALL " + what + "：不是圆柱面"); return; }
            Array bp = new double[3], ax = new double[3]; double rr = 0;
            cyl.GetCylinderData(ref bp, ref ax, out rr);
            V3 axis = V3.From(ax).Unit();
            V3 p = new V3(x + r * axis.Y + r, y - r * axis.Y, z);   // 壁上一点（轴沿 Z 时取 +X 方向）
            V3 baseP = V3.From(bp);
            V3 closest = baseP + axis * ((p - baseP).Dot(axis));
            V3 radial = (p - closest).Unit();
            Array pt = new double[]{ p.X, p.Y, p.Z };
            Array nrm = new double[3];
            string err = "";
            try { wall.GetNormal(0, ref pt, ref nrm); } catch (Exception e) { err = e.GetType().Name + ": " + e.Message; }
            bool reversed = false;
            try { reversed = wall.IsParamReversed; } catch { }
            V3 n = V3.From(nrm);
            if (reversed) n = n * -1.0;
            double dot = err.Length == 0 ? n.Unit().Dot(radial) : double.NaN;
            Console.WriteLine("WALL " + what + " 半径Φ" + N3(rr*2000) + " IsParamReversed=" + reversed
                              + " 面法向(修正后)与径向外侧的点积=" + (double.IsNaN(dot) ? "读不到" : N3(dot))
                              + (err.Length > 0 ? "  GetNormal 失败：" + err : "")
                              + "  [孔期望 <0，凸台期望 >0]");
        }

        public static void ScanNative(F.Application app, string outputDir){
            checks = 0;
            bool createdInstance = false;
            if (app == null) {
                try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); Console.WriteLine("AUTOHOLE attached to running CAD"); }
                catch { app = (F.Application)Activator.CreateInstance(Type.GetTypeFromProgID("SolidEdge.Application")); createdInstance = true; Console.WriteLine("AUTOHOLE started a new CAD instance"); }
                app.Visible = true; app.ScreenUpdating = true;
            }
            string dir = Path.GetFullPath(Path.Combine(outputDir, "autohole-scan-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")));
            Directory.CreateDirectory(dir);
            A.AssemblyDocument asm = null; object original = null;
            try { original = app.ActiveDocument; } catch {}
            P.PartDocument a = null, b = null;
            try {
                // ---- 夹具 A：带五种孔口 + 一个圆凸台 ----
                P.Model ma;
                a = BuildPlate(app, "ScanA", 0.1, 0.1, 0.02, out ma);
                DrillAt(app, a, ma, 0.020, 0.020, new HoleSpec{ Kind=HoleKind.Counterbore, HoleDiameter=6.6, CounterboreDiameter=11.0, CounterboreDepth=6.0 }, "Φ11/Φ6.6 沉孔 #1");
                DrillAt(app, a, ma, 0.080, 0.020, new HoleSpec{ Kind=HoleKind.Counterbore, HoleDiameter=6.6, CounterboreDiameter=11.0, CounterboreDepth=6.0 }, "Φ11/Φ6.6 沉孔 #2");
                DrillAt(app, a, ma, 0.050, 0.020, new HoleSpec{ Kind=HoleKind.Countersink, HoleDiameter=5.0, CountersinkDiameter=11.0, CountersinkAngle=90 }, "Φ11 90°/Φ5 锥形沉孔");
                DrillAt(app, a, ma, 0.020, 0.050, new HoleSpec{ Kind=HoleKind.Through, HoleDiameter=6.6, Chamfer=true, ChamferSetback=0.5, ChamferAngle=45 }, "孔口倒角 Φ6.6");
                DrillAt(app, a, ma, 0.050, 0.050, new HoleSpec{ Kind=HoleKind.Tapped, ThreadSize="M6", HoleDiameter=4.917 }, "螺纹孔 M6");
                Assert(a.Models.Item(1).Holes.Count == 5, "A 板有 5 个孔特征");

                // 圆凸台 Φ20 高 5：它的底圆同样长在顶面的内环上，绝不能被当成孔
                var xy = FindXY(a);
                var rp = a.RefPlanes.AddParallelByDistance(xy, 0.010, P.ReferenceElementConstants.igNormalSide, M, M, M, M);
                var bossProfile = a.ProfileSets.Add().Profiles.Add(rp);
                double bx, by; bossProfile.Convert3DCoordinate(0.080, 0.080, 0.010, out bx, out by);
                bossProfile.Circles2d.AddByCenterRadius(bx, by, 0.010);
                if (bossProfile.End(P.ProfileValidationType.igProfileClosed) != 0) throw new InvalidOperationException("凸台轮廓未闭合。");
                Array bossArr = new object[]{bossProfile};
                ma = a.Models.AddFiniteExtrudedProtrusion(1, ref bossArr, P.FeaturePropertyConstants.igRight, 0.005);
                bossProfile.Visible = false;
                Pump(400);

                // ---- 1) 点带孔的面：自动认孔 ----
                var top = PlanarFaceAtZ(ma, 0.010);
                Assert(top != null, "读到 A 板顶面");
                var target = AutoHoleReader.ReadTarget(top);
                string how; int notHole;
                var holes = HoleScanCad.ScanFace(target, out how, out notHole);
                Console.WriteLine("SCAN 识别 " + holes.Count + " 个孔，跳过 " + notHole + " 个（凸台/非孔）" + (how.Length > 0 ? "；" + how : ""));
                foreach (var h in holes) Console.WriteLine("HOLE " + h + " 孔口Φ" + N3(h.MouthDiameterMm) + " 壁=" + (h.MouthKind.Length > 0 ? h.MouthKind : "平孔") + (h.Stepped ? "  <- " + h.Note : ""));
                Assert(holes.Count == 5, "顶面识别到 5 个孔（实 " + holes.Count + "）");
                Assert(notHole == 1, "Φ20 圆凸台的底圆被排除（跳过 " + notHole + " 个）");

                var groups = HoleScan.GroupByDiameter(holes);
                Assert(groups.Count == 3, "按孔径归成 3 类（Φ6.6 / Φ5 / Φ4.917）（实 " + groups.Count + "）");
                List<ScannedHole> g66 = null;
                foreach (var g in groups) if (Math.Abs(g[0].DiameterMm - 6.6) < 0.02) g66 = g;
                Assert(g66 != null && g66.Count == 3, "Φ6.6 那一类有 3 个（2 个沉孔 + 1 个倒角孔口）");
                int stepped = 0, cbores = 0, chamferHoles = 0;
                foreach (var h in g66) {
                    if (h.Stepped) stepped++;
                    if (h.MouthKind.IndexOf("沉孔") >= 0) cbores++;
                    else if (h.MouthKind.IndexOf("倒角") >= 0) chamferHoles++;
                }
                Assert(stepped == 3, "3 个孔口都比孔径大（2 个沉孔 + 1 个倒角孔口）（实 " + stepped + "）");
                Assert(cbores == 2, "其中 2 个认成沉孔（实 " + cbores + "）");
                Assert(chamferHoles == 1, "另 1 个认成倒角孔口（实 " + chamferHoles + "）");
                Assert(HoleScan.MouthSummary(g66).Contains("沉孔") && HoleScan.MouthSummary(g66).Contains("Φ11"),
                       "归类说明里写明「孔口 Φ11，按 Φ6.6 配做」：" + HoleScan.MouthSummary(g66));
                bool tooBig = false;
                foreach (var h in holes) if (h.DiameterMm > 7.0) tooBig = true;
                Assert(!tooBig, "没有任何孔按 Φ11 配做（否则 M6 会被配成 M10）");
                Console.WriteLine("SCAN 归类：" + HoleScan.StatusLine(holes));

                // ---- 2) 点沉孔的孔口圆边 Φ11：也要取下面的 Φ6.6 ----
                var mouthEdge = CircleEdgeAt(ma, 0.020, 0.020, 0.010, 11.0);
                Assert(mouthEdge != null, "找到沉孔孔口圆边 Φ11");
                var edgeHole = AutoHoleReader.ReadReference(mouthEdge);
                Assert(Math.Abs(edgeHole.DiameterMm - 6.6) < 0.02, "点沉孔孔口圆边取下面的 Φ6.6（实 " + N3(edgeHole.DiameterMm) + "）");
                Assert(Math.Abs(edgeHole.MouthDiameterMm - 11.0) < 0.02, "孔口直径仍是 Φ11");
                var plainEdge = CircleEdgeAt(ma, 0.050, 0.020, 0.010, 6.6 + 1.0);   // 倒角孔口 Φ7.6
                if (plainEdge != null) {
                    var chamferHole = AutoHoleReader.ReadReference(plainEdge);
                    Assert(Math.Abs(chamferHole.DiameterMm - 6.6) < 0.02, "点倒角孔口 Φ7.6 取 Φ6.6（实 " + N3(chamferHole.DiameterMm) + "）");
                }

                // ---- 3) 点圆柱面 / 圆锥面认孔 ----
                string label;
                var cboreWall = CylinderFaceNear(ma, 0.020, 0.020, 0.0055);
                Assert(cboreWall != null, "找到沉孔壁圆柱面 Φ11");
                var fromWall = HoleScanCad.ScanSurface(cboreWall, out label);
                Assert(Math.Abs(fromWall.DiameterMm - 6.6) < 0.02, "点沉孔壁（Φ11 圆柱面）认出下面那个 Φ6.6（实 " + N3(fromWall.DiameterMm) + "）");
                var smallWall = CylinderFaceNear(ma, 0.020, 0.020, 0.0033);
                Assert(smallWall != null, "找到 Φ6.6 孔壁圆柱面");
                var fromSmall = HoleScanCad.ScanSurface(smallWall, out label);
                Assert(Math.Abs(fromSmall.DiameterMm - 6.6) < 0.02, "点 Φ6.6 孔壁认出 Φ6.6");
                var tapWall = CylinderFaceNear(ma, 0.050, 0.050, 0.0024585);
                Assert(tapWall != null, "找到螺纹孔壁圆柱面");
                var fromTap = HoleScanCad.ScanSurface(tapWall, out label);
                Assert(Math.Abs(fromTap.DiameterMm - 4.917) < 0.02, "点螺纹孔壁认出 Φ4.917（实 " + N3(fromTap.DiameterMm) + "）");
                var coneFace = ConeFaceNear(ma, 0.050, 0.020);
                Assert(coneFace != null, "找到锥形沉孔的圆锥面");
                var fromCone = HoleScanCad.ScanSurface(coneFace, out label);
                Assert(Math.Abs(fromCone.DiameterMm - 5.0) < 0.02, "点锥形沉孔的锥面认出 Φ5（实 " + N3(fromCone.DiameterMm) + "）");

                // 凸台外壁（Φ20 圆柱）：诊断用，先把事实打出来（判据要按真机数据写）
                var bossWall = CylinderFaceNear(ma, 0.080, 0.080, 0.0100);
                Assert(bossWall != null, "找到 Φ20 凸台的外壁圆柱面");
                string bossErr = "";
                try { HoleScanCad.ScanSurface(bossWall, out label); } catch (Exception e) { bossErr = e.Message; }
                Assert(bossErr.IndexOf("凸台") >= 0, "点圆凸台的外壁会被拒绝（实：" + bossErr + "）");
                // 凸台的底圆同样长在板面内环上：点它也不能当成孔
                var bossBaseEdge = CircleEdgeAt(ma, 0.080, 0.080, 0.010, 20.0);
                Assert(bossBaseEdge != null, "找到凸台底圆 Φ20");
                string bossEdgeErr = "";
                try { AutoHoleReader.ReadReference(bossBaseEdge); } catch (Exception e) { bossEdgeErr = e.Message; }
                Assert(bossEdgeErr.IndexOf("凸台") >= 0, "点凸台底圆也会被拒绝（实：" + bossEdgeErr + "）");
                // 圆柱面的法向约定：判"这圈壁是凸台还是孔"要用（凸台：材料在圆柱里面；孔：材料在圆柱外面）
                DumpWallNormal(cboreWall, "沉孔壁(孔)", 0.020, 0.020, 0.0055, 0.010);
                DumpWallNormal(smallWall, "Φ6.6 孔壁(孔)", 0.020, 0.020, 0.0033, 0.006);
                DumpWallNormal(BodyCylinder(ma, 0.020, 0.050, 0.0038), "倒角孔壁(孔)", 0.020, 0.050, 0.0033, 0.005);
                DumpWallNormal(bossWall, "凸台外壁(凸台)", 0.080, 0.080, 0.0100, 0.012);

                a.SaveAs(Path.Combine(dir, "ScanA.par"));
                a.Close(false); a = null;

                // ---- 4) 装配：B 板在上方 30mm，用扫描结果往 B 板上配做 ----
                P.Model mb;
                b = BuildPlate(app, "ScanB", 0.1, 0.1, 0.02, out mb);
                b.SaveAs(Path.Combine(dir, "ScanB.par"));
                b.Close(false); b = null;

                asm = (A.AssemblyDocument)app.Documents.Add("SolidEdge.AssemblyDocument");
                asm.SaveAs(Path.Combine(dir, "ScanFixture.asm"));
                asm.Activate();
                var occA = asm.Occurrences.AddByFilename(Path.Combine(dir, "ScanA.par"));
                Array mA = Transform.Identity.M; occA.PutMatrix(ref mA, true);
                var occB = asm.Occurrences.AddByFilename(Path.Combine(dir, "ScanB.par"));
                Array mB = Transform.Frame(new V3(0,0,0.030), new V3(1,0,0), new V3(0,1,0), new V3(0,0,1)).M;
                occB.PutMatrix(ref mB, true);
                Assert(asm.Occurrences.Count == 2, "装配里两个实例");

                var docB = (P.PartDocument)occB.OccurrenceDocument;
                var faceB = PlanarFaceAtZ((P.Model)docB.Models.Item(1), -0.010);   // B 板下表面（局部）
                Assert(faceB != null, "读到 B 板下表面");
                var refB = asm.CreateReference(occB, faceB);
                var targetB = AutoHoleReader.ReadTarget(refB);
                Assert(Math.Abs(targetB.Plane.Point.Z - 0.020) < 1e-6, "B 板下表面在装配 z=20mm（实 " + N3(targetB.Plane.Point.Z*1000) + "mm）");

                var reqs = new List<AutoHoleWriter.HoleRequest>();
                foreach (var h in holes) {
                    V3 centre;
                    try { centre = AutoHoleReader.Intersect(AutoHoleReader.ToReference(h), targetB); } catch { continue; }
                    var match = HoleMatcher.Match(h.DiameterMm);
                    reqs.Add(new AutoHoleWriter.HoleRequest { Spec = match.Target, Centre = centre,
                                                              Source = "扫描 Φ" + N3(h.DiameterMm) + " -> " + match.Row.Size + " " + HoleSpec.KindName(match.Target.Kind) });
                }
                Assert(reqs.Count == 5, "5 个扫描孔都投到 B 板上（凸台没有被投过去，实 " + reqs.Count + "）");
                foreach (var r in reqs) Console.WriteLine("REQ " + r.Source + " @ " + r.Centre);

                double volB0 = Vol((P.Model)docB.Models.Item(1));
                var res = AutoHoleWriter.DrillRequests(targetB, reqs);
                Assert(res.Created == 5 && res.Failures.Count == 0, "B 板上打出 5 个孔（成功 " + res.Created + "，失败 " + res.Failures.Count + "：" + string.Join("；", res.Failures.ToArray()) + "）");
                double removed = volB0 - Vol((P.Model)docB.Models.Item(1));
                Assert(((P.Model)docB.Models.Item(1)).Holes.Count == 5, "B 板新增 5 个孔特征");

                // 三个 Φ6.6（M6 过孔）-> M6 螺纹孔（按内小径 Φ4.917 贯通 20mm）
                // 两个 Φ5 / Φ4.917（M6 底孔/内径）-> M6 沉孔（沉孔 Φ11 深 6.5 + 通孔 Φ6.6 走完剩下 13.5）
                double expect = 3.0 * Math.PI * Math.Pow(0.004917/2, 2) * 0.020
                              + 2.0 * (Math.PI * Math.Pow(0.0055, 2) * 0.0065 + Math.PI * Math.Pow(0.0033, 2) * 0.0135);
                Assert(Math.Abs(removed - expect) / expect < 0.03,
                    "B 板切除体积 = 3×M6 螺纹孔 + 2×M6 沉孔（实 " + (removed*1e9).ToString("0.#") + " mm³ / 期 " + (expect*1e9).ToString("0.#") + " mm³）");
                // 反证：孔口 Φ11 会被反推成 M10 螺纹孔（Φ8.376），切除体积是 M6 的 2.9 倍
                var m6 = HoleMatcher.Match(6.6);
                var m10 = HoleMatcher.Match(11.0);
                Assert(m6.HasRow && m6.Row.Size == "M6", "Φ6.6 反推的是 M6（实 " + (m6.HasRow ? m6.Row.Size : "未命中") + "）");
                Assert(m10.HasRow && m10.Row.Size == "M10", "对照：孔口 Φ11 会被反推成 M10 —— 这就是不能拿孔口直径去配的原因");
                double volM6 = 3.0 * Math.PI * Math.Pow(m6.Target.HoleDiameter/2000.0, 2) * 0.020;
                double volM10 = 3.0 * Math.PI * Math.Pow(m10.Target.HoleDiameter/2000.0, 2) * 0.020;
                Assert(volM6 < volM10 * 0.40, "按 Φ6.6 配的这三个孔切除体积只有按 Φ11 配的三分之一左右（"
                    + (volM6*1e9).ToString("0.#") + " vs " + (volM10*1e9).ToString("0.#") + " mm³）");

                asm.Save();
                try { ((dynamic)app.ActiveWindow).View.Fit(); ((dynamic)app.ActiveWindow).View.SaveAsImage(Path.Combine(dir, "autohole-scan.png"), 1200, 900); } catch {}
                Console.WriteLine("AUTO-HOLE SCAN ASSERTIONS " + checks);
                Console.WriteLine("AUTO-HOLE SCAN ARTIFACTS " + dir);
            } finally {
                if (a != null) try { a.Close(false); } catch {}
                if (b != null) try { b.Close(false); } catch {}
                if (asm != null) try { asm.Close(false); } catch {}
                try { ((dynamic)original).Activate(); } catch {}
                if (createdInstance) { try { app.Quit(); } catch {} }
            }
        }
    }
}
