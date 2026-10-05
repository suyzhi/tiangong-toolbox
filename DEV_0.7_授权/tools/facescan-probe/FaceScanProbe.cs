using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using TianGongCadSuite;
using A=SolidEdgeAssembly;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;
using F=SolidEdgeFramework;
using S=SolidEdgeFrameworkSupport;

// 面扫描 / 沉孔识别的事实探针（2026-10-06）。
//
// 目的：在真机上把"点一个带孔的面，自动认出上面的孔（含沉孔下面的孔径）"这一步需要的
// 底层事实逐条量出来，再照着写生产代码。要量的东西：
//   1) 平面面片的 Loops / IsOuterLoop / 内环上的整圆（IsClosed）读得到吗？
//   2) 圆边的相邻壁面（圆柱/圆锥/圆环）怎么读？圆凸台的底圆是不是也长在内环上（必须能排除）？
//   3) 沉孔/锥沉/倒角孔口下面的那个"更小的同轴圆"在实体里存在吗？能不能量出来？
//   4) Face.GetRange 返回的是 3D 包围盒还是参数范围？（判"壁在材料的哪一侧"要用）
//   5) Body.get_FacesByRay 的返回是不是"射线穿过的所有面"（内外判定要用奇偶性）？
//
// 自己新建一块 100×100×20 的板，用生产代码 AutoHoleWriter.Drill 造出四种孔口 + 一个圆凸台。
// 用法: FaceScanProbe.exe <输出目录>
class FaceScanProbe {
    static F.Application app;
    static object M = Type.Missing;
    const string CadHome = @"C:\Program Files\NDS\TianGong 2025";
    static string Template { get { return Path.Combine(CadHome, "Template", "ISO Metric", "iso metric part.par"); } }

    static int L2 = 0;
    static void L(string s){ L2++; Console.WriteLine(s); Console.Out.Flush(); }
    static string N(double v){ return v.ToString("0.####", CultureInfo.InvariantCulture); }
    static double D(Array a,int i){ return Convert.ToDouble(a.GetValue(i)); }
    static string P3(Array a){ return "(" + N(D(a,0)) + ", " + N(D(a,1)) + ", " + N(D(a,2)) + ")"; }
    static string Try(Func<string> f){ try { return f(); } catch (Exception e) { return "<" + e.GetType().Name + ": " + e.Message + ">"; } }
    static void Pump(int n){ for (int i = 0; i < n; i++) { try { app.DoIdle(); } catch {} Thread.Sleep(60); } }

    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        // 每跑一次换一个子目录：SaveAs 到已存在的文件会让 CAD 弹"是否替换"的模态框，
        // 而那个框在私有桌面上没人点 —— 探针就永远卡在那一句上（已经踩过一次）。
        string dir = Path.GetFullPath(Path.Combine(args.Length > 0 ? args[0] : "facescan-probe", "run-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")));
        Directory.CreateDirectory(dir);
        try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); }
        catch (Exception e) { L("连不上 CAD：" + e.Message); return 2; }
        L("已连上 CAD " + Try(() => app.Version));
        Pump(4);

        P.PartDocument part = null; object original = null;
        try { original = app.ActiveDocument; } catch {}
        try {
            part = (P.PartDocument)app.Documents.Add("SolidEdge.PartDocument", Template);
            part.ModelingMode = P.ModelingModeConstants.seModelingModeOrdered;
            var model = BuildPlate(part, 0.1, 0.1, 0.02);
            part.SaveAs(Path.Combine(dir, "FaceScanFixture.par"));
            L("板 100x100x20 已建：体积 " + N(((G.Body)model.Body).Volume * 1e9) + " mm³");
            double v0 = ((G.Body)model.Body).Volume;

            // 四个孔口（全部走生产代码的打孔路径）。每个都单独兜住异常：一个造不出来不能拖垮其余测量。
            TryDrill(part, model, 0.020, 0.020, new HoleSpec{ Kind=HoleKind.Counterbore, HoleDiameter=6.6, CounterboreDiameter=11.0, CounterboreDepth=6.0 }, "H1 圆柱沉孔 Φ11/Φ6.6");
            TryDrill(part, model, 0.050, 0.020, new HoleSpec{ Kind=HoleKind.Through, HoleDiameter=5.0 }, "H2 平孔 Φ5");
            TryDrill(part, model, 0.080, 0.020, new HoleSpec{ Kind=HoleKind.Countersink, HoleDiameter=5.0, CountersinkDiameter=11.0, CountersinkAngle=90 }, "H3 锥形沉孔 Φ11 90°/Φ5");
            TryDrill(part, model, 0.020, 0.050, new HoleSpec{ Kind=HoleKind.Through, HoleDiameter=6.6, Chamfer=true, ChamferSetback=0.5, ChamferAngle=45 }, "H4 孔口倒角 0.5x45° 的 Φ6.6 通孔");
            TryDrill(part, model, 0.050, 0.050, new HoleSpec{ Kind=HoleKind.Tapped, ThreadSize="M6", HoleDiameter=4.917 }, "H5 螺纹孔 M6（内径 Φ4.917）");
            L("四个孔打完：体积 " + N(((G.Body)model.Body).Volume * 1e9) + " mm³（去掉 " + N((v0 - ((G.Body)model.Body).Volume) * 1e9) + "）");

            try { MakeBoss(part, model, 0.080, 0.080, 0.010, 0.005); L("圆凸台 Φ20 高 5 已建：体积 " + N(((G.Body)model.Body).Volume * 1e9) + " mm³"); }
            catch (Exception e) { L("圆凸台构造失败（不影响其余测量）：" + e.Message); }

            part.Save();
            Pump(4);

            var body = (G.Body)model.Body;
            L("");
            L("=== 1) 顶面（z=+10mm）的拓扑 ===");
            var top = PlanarFaceAtZ(model, 0.010);
            if (top == null) { L("找不到顶面"); return 5; }
            DumpFace(top, "顶面");

            L("");
            L("=== 2) 每个圆边：相邻壁面是什么 ===");
            foreach (G.Edge e in (G.Edges)top.Edges) {
                var circ = e.Geometry as G.Circle;
                if (circ == null) { L("  边 " + e.ID + " 非圆：" + e.Geometry.GetType().Name + " closed=" + Try(() => e.IsClosed.ToString())); continue; }
                Array c = new double[3], ax = new double[3]; double r = 0; circ.GetCircleData(ref c, ref ax, out r);
                L("  圆边 " + e.ID + " r=" + N(r*1000) + "mm 心=" + P3(c) + " 轴=" + P3(ax) + " closed=" + Try(() => e.IsClosed.ToString()));
                int cnt = 0; Array faces = null;
                try { e.GetFaces(out cnt, ref faces); } catch (Exception ex) { L("      GetFaces 失败：" + ex.Message); continue; }
                L("    相邻面 " + cnt + " 个：");
                for (int i = 0; i < cnt; i++) {
                    var f = faces.GetValue(i) as G.Face; if (f == null) continue;
                    L("      #" + i + " " + Describe(f) + " 同顶面? " + (f.ID == top.ID));
                }
            }

            L("");
            L("=== 3) 各孔口下面的同轴小圆（沉孔识别要靠它） ===");
            var all = Circles(body);
            L("实体上整圆共 " + all.Count + " 条");
            foreach (var rec in all) L("  " + rec);
            L("按 '同轴 + 更小' 找：");
            foreach (var mouth in all) {
                double best = double.MaxValue; string src = "";
                foreach (var other in all) {
                    if (ReferenceEquals(other, mouth)) continue;
                    if (Math.Abs(other.Axis.Dot(mouth.Axis)) < 0.9999) continue;
                    double axial = (other.Centre - mouth.Centre).Dot(mouth.Axis);
                    var radial = (other.Centre - mouth.Centre) - mouth.Axis * axial;
                    if (radial.Length * 1000 > 0.05) continue;
                    if (other.RadiusMm > mouth.RadiusMm - 0.02) continue;
                    if (Math.Abs(axial) * 1000 < 0.001) continue;
                    if (other.RadiusMm < best) { best = other.RadiusMm; src = "圆心 " + other.Centre + " 轴向偏移 " + N(axial*1000) + "mm"; }
                }
                L("  Φ" + N(mouth.RadiusMm*2) + " @" + mouth.Centre + "  ->  " + (best == double.MaxValue ? "没有更小的同轴圆（= 平孔）" : ("Φ" + N(best*2) + "（" + src + "）")));
            }

            L("");
            L("=== 4) Face.GetRange 是什么 ===");
            Array lo = new double[3], hi = new double[3];
            try { top.GetRange(ref lo, ref hi); L("  顶面 GetRange " + P3(lo) + " .. " + P3(hi)); } catch (Exception e) { L("  顶面 GetRange 失败：" + e.Message); }
            var wall = CylinderFaceNear(model, 0.020, 0.020, 0.0055);
            if (wall != null) { try { wall.GetRange(ref lo, ref hi); L("  沉孔壁(Φ11) GetRange " + P3(lo) + " .. " + P3(hi) + "  半径 " + Try(() => (((G.Cylinder)wall.Geometry).Radius*1000).ToString("0.###"))); } catch (Exception e) { L("  沉孔壁 GetRange 失败：" + e.Message); } }
            else L("  没找到沉孔壁");

            L("");
            L("=== 5) get_FacesByRay 的语义（内外判定奇偶性） ===");
            Ray(body, "空气中(板子上方 50mm)", 0.05, 0.05, 0.06, 0.5773, 0.5773, 0.5773);
            Ray(body, "材料内(板中心)", 0.05, 0.05, 0.0, 0.5773, 0.5773, 0.5773);
            Ray(body, "沉孔空腔里(z=8mm)", 0.020, 0.020, 0.008, 0.5773, 0.5773, 0.5773);
            Ray(body, "Φ6.6 孔内(z=0)", 0.020, 0.020, 0.0, 0.5773, 0.5773, 0.5773);
            Ray(body, "凸台内(z=12mm)", 0.080, 0.080, 0.012, 0.5773, 0.5773, 0.5773);
            Ray(body, "凸台上方(z=16mm)", 0.080, 0.080, 0.016, 0.5773, 0.5773, 0.5773);

            L("");
            L("=== 6) 生产读孔函数当前怎么读沉孔（旧行为对照） ===");
            foreach (G.Edge e in (G.Edges)top.Edges) {
                var circ = e.Geometry as G.Circle; if (circ == null) continue;
                var pg = PickGeometry.Unwrap(e);
                var hole = AutoHoleReader.ReadReference(e);
                L("  旧 ReadReference：Φ" + N(hole.DiameterMm) + " @ " + hole.Center + " 轴 " + hole.Axis + "（" + hole.Where + "）");
            }

            try { ((dynamic)app.ActiveWindow).View.Fit(); ((dynamic)app.ActiveWindow).View.SaveAsImage(Path.Combine(dir, "facescan.png"), 1200, 900); } catch {}
            L("");
            L("输出目录 " + dir);
        } catch (Exception ex) {
            L("测量中断（其余部分见上面的输出）：" + Safe(ex));
            int k = L2; // 只是为了保留栈的线索
            L("");
        } finally {
            if (part != null) { try { part.Close(false); } catch {} }
            try { ((dynamic)original).Activate(); } catch {}
            Pump(2);
        }
        return 0;
    }

    // ---- 夹具 ----
    static P.RefPlane FindXY(P.PartDocument part){
        foreach (P.RefPlane c in part.RefPlanes) {
            Array n = new double[3], p = new double[3], u = new double[3];
            c.GetNormal(ref n); c.GetRootPoint(ref p); c.GetReferenceDirection(ref u);
            if (Math.Abs(D(n,2)-1)<1e-8 && Math.Abs(D(p,0))<1e-8 && Math.Abs(D(p,1))<1e-8 && Math.Abs(D(u,0)-1)<1e-8) return c;
        }
        return null;
    }
    static P.Model BuildPlate(P.PartDocument part, double w, double h, double t){
        var plane = FindXY(part);
        if (plane == null) throw new InvalidOperationException("模板缺少标准 XY 基准面。");
        var profile = part.ProfileSets.Add().Profiles.Add(plane);
        var Ln = new S.Line2d[4];
        Ln[0]=profile.Lines2d.AddBy2Points(0,0,w,0); Ln[1]=profile.Lines2d.AddBy2Points(w,0,w,h);
        Ln[2]=profile.Lines2d.AddBy2Points(w,h,0,h); Ln[3]=profile.Lines2d.AddBy2Points(0,h,0,0);
        var rel = (S.Relations2d)profile.Relations2d;
        for (int i=0;i<4;i++){ rel.AddKeypoint(Ln[i],(int)SolidEdgeConstants.KeypointIndexConstants.igLineEnd,Ln[(i+1)%4],(int)SolidEdgeConstants.KeypointIndexConstants.igLineStart); if(i%2==0) rel.AddHorizontal(Ln[i]); else rel.AddVertical(Ln[i]); }
        rel.AddKeypointFix(Ln[0],(int)SolidEdgeConstants.KeypointIndexConstants.igLineStart);
        var dims = (S.Dimensions)profile.Dimensions; dims.Constraint=true; dims.AddLength(Ln[0]); dims.AddLength(Ln[1]);
        if (profile.End(P.ProfileValidationType.igProfileClosed) != 0) throw new InvalidOperationException("矩形轮廓未闭合。");
        Array arr = new object[]{profile};
        return part.Models.AddFiniteExtrudedProtrusion(1, ref arr, P.FeaturePropertyConstants.igSymmetric, t);
    }
    static void MakeBoss(P.PartDocument part, P.Model model, double x, double y, double z, double h){
        var xy = FindXY(part);
        var rp = part.RefPlanes.AddParallelByDistance(xy, z, P.ReferenceElementConstants.igNormalSide, M, M, M, M);
        var prof = part.ProfileSets.Add().Profiles.Add(rp);
        double x2, y2; prof.Convert3DCoordinate(x, y, z, out x2, out y2);
        prof.Circles2d.AddByCenterRadius(x2, y2, 0.010);
        if (prof.End(P.ProfileValidationType.igProfileClosed) != 0) throw new InvalidOperationException("圆轮廓未闭合。");
        Array arr = new object[]{prof};
        model = part.Models.AddFiniteExtrudedProtrusion(1, ref arr, P.FeaturePropertyConstants.igRight, h);
        prof.Visible = false;
        Array lo=new double[3], hi=new double[3]; ((G.Body)model.Body).GetRange(ref lo, ref hi);
        L("  凸台后包围盒 z " + N(D(lo,2)*1000) + " .. " + N(D(hi,2)*1000) + " mm");
    }

    static void TryDrill(P.PartDocument part, P.Model model, double x, double y, HoleSpec spec, string what){
        try { DrillAt(part, model, x, y, spec, what); }
        catch (Exception ex) { L("打孔 " + what + " 抛异常：" + Safe(ex)); Pump(2); }
    }
    static string Safe(Exception e){ try { return e.GetType().Name + ": " + e.Message; } catch { return "<异常信息读不出来>"; } }

    static void DrillAt(P.PartDocument part, P.Model model, double x, double y, HoleSpec spec, string what){
        var top = PlanarFaceAtZ(model, 0.010);
        if (top == null) throw new InvalidOperationException("找不到顶面");
        var target = AutoHoleReader.ReadTarget(top);
        var res = AutoHoleWriter.Drill(target, spec, new V3[]{ new V3(x, y, 0.010) });
        L("打孔 " + what + " -> 成功 " + res.Created + "、本来就有 " + res.AlreadyOk + "、失败 " + res.Failures.Count + (res.Failures.Count > 0 ? ("（" + string.Join("；", res.Failures.ToArray()) + "）") : ""));
        Pump(2);
    }

    // ---- 读取 ----
    static G.Face PlanarFaceAtZ(P.Model m, double z){
        foreach (G.Face f in (G.Faces)((G.Body)m.Body).get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll)) {
            var pl = f.Geometry as G.Plane; if (pl == null) continue;
            Array p=new double[3], n=new double[3]; pl.GetPlaneData(ref p, ref n);
            if (Math.Abs(Math.Abs(D(n,2)) - 1) > 1e-6) continue;
            if (Math.Abs(D(p,2)-z) < 1e-6) return f;
        }
        return null;
    }
    static G.Face CylinderFaceNear(P.Model m, double x, double y, double r){
        foreach (G.Face f in (G.Faces)((G.Body)m.Body).get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll)) {
            var c = f.Geometry as G.Cylinder; if (c == null) continue;
            if (Math.Abs(c.Radius - r) > 1e-6) continue;
            Array bp=new double[3], ax=new double[3]; double rr=0; c.GetCylinderData(ref bp, ref ax, out rr);
            var radial = new V3(D(bp,0)-x, D(bp,1)-y, 0).Length;
            if (radial < 1e-6) return f;
        }
        return null;
    }
    static string Describe(G.Face f){
        string t = f.Geometry == null ? "null" : f.Geometry.GetType().Name;
        string extra = "";
        try {
            var c = f.Geometry as G.Cylinder; if (c != null) extra = " r=" + N(c.Radius*1000) + "mm 轴=" + P3(Ax(c));
            var co = f.Geometry as G.Cone; if (co != null) extra = " r=" + N(co.Radius*1000) + "mm 半角=" + N(co.HalfAngle) + " expanding=" + co.Expanding;
            var to = f.Geometry as G.Torus; if (to != null) extra = " 主半径=" + N(to.MajorRadius*1000) + " 次半径=" + N(to.MinorRadius*1000);
            var pl = f.Geometry as G.Plane; if (pl != null) { Array p=new double[3],n=new double[3]; pl.GetPlaneData(ref p,ref n); extra = " 点=" + P3(p) + " 法向=" + P3(n); }
        } catch {}
        return "面#" + f.ID + " " + t + extra + " 参数反向=" + Try(() => f.IsParamReversed.ToString());
    }
    static Array Ax(G.Cylinder c){ Array a=new double[3]; c.GetAxisVector(ref a); return a; }

    class Circ { public V3 Centre; public V3 Axis; public double RadiusMm; public int EdgeId; public bool Closed;
        public override string ToString(){ return "边#" + EdgeId + " Φ" + N(RadiusMm*2) + " 心=" + Centre + " 轴=" + Axis + " closed=" + Closed; } }

    static List<Circ> Circles(G.Body body){
        var list = new List<Circ>();
        foreach (G.Edge e in (G.Edges)body.get_Edges(G.FeatureTopologyQueryTypeConstants.igQueryAll)) {
            var c = e.Geometry as G.Circle; if (c == null) continue;
            Array cc=new double[3], ax=new double[3]; double r=0;
            try { c.GetCircleData(ref cc, ref ax, out r); } catch { continue; }
            bool closed = false; try { closed = e.IsClosed; } catch {}
            list.Add(new Circ{ Centre = V3.From(cc), Axis = V3.From(ax).Unit(), RadiusMm = r*1000, EdgeId = e.ID, Closed = closed });
        }
        return list;
    }

    static void DumpFace(G.Face f, string what){
        L(what + "：" + Describe(f));
        int loops = 0; try { loops = ((G.Loops)f.Loops).Count; } catch (Exception e) { L("  Loops 读失败：" + e.Message); return; }
        L("  环数 " + loops);
        int li = 0;
        foreach (G.Loop loop in (G.Loops)f.Loops) {
            li++;
            int ec = 0; try { ec = ((G.Edges)loop.Edges).Count; } catch {}
            L("  环#" + li + " 外环=" + Try(() => loop.IsOuterLoop.ToString()) + " 边数=" + ec);
            foreach (G.Edge e in (G.Edges)loop.Edges) {
                var circ = e.Geometry as G.Circle;
                string info = circ == null ? ("非圆 " + e.Geometry.GetType().Name) : ("圆 r=" + N(circ.Radius*1000) + "mm");
                L("     " + info + " closed=" + Try(() => e.IsClosed.ToString()) + " 边#" + e.ID);
            }
        }
    }

    static void Ray(G.Body body, string what, double x, double y, double z, double dx, double dy, double dz){
        try {
            var faces = body.get_FacesByRay(x, y, z, dx, dy, dz);
            var coll = faces as System.Collections.IEnumerable;
            var names = new List<string>();
            if (coll != null) foreach (object o in coll) { var f = o as G.Face; names.Add(f == null ? "?" : (f.ID + ":" + (f.Geometry == null ? "?" : f.Geometry.GetType().Name))); }
            L("  从 " + what + " (" + N(x*1000) + "," + N(y*1000) + "," + N(z*1000) + ")mm 打射线 -> " + names.Count + " 个面 " + string.Join(", ", names.ToArray()));
        } catch (Exception e) { L("  从 " + what + " 打射线失败：" + e.Message); }
    }
}
