using System;
using System.IO;
using System.Runtime.InteropServices;
using F=SolidEdgeFramework;
using P=SolidEdgePart;
using A=SolidEdgeAssembly;
using S=SolidEdgeFrameworkSupport;

// 造一个干净的手动验收夹具：A 板（10mm，带 1 个 Φ6.6 孔）+ B 板（20mm 实心）+ 装配
class MakeCleanFixture {
    static object M = Type.Missing;
    const string CadHome = @"C:\Program Files\NDS\TianGong 2025";
    static string Template { get { return Path.Combine(CadHome,"Template","ISO Metric","iso metric part.par"); } }
    static F.Application app;

    [STAThread] static int Main(string[] args){
        string dir = args.Length > 0 ? Path.GetFullPath(args[0]) : Environment.CurrentDirectory;
        Directory.CreateDirectory(dir);
        try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); }
        catch { app = (F.Application)Activator.CreateInstance(Type.GetTypeFromProgID("SolidEdge.Application")); }
        app.Visible = true; app.ScreenUpdating = true;
        string pa = Path.Combine(dir, "LiveA.par"), pb = Path.Combine(dir, "LiveB.par"), asmf = Path.Combine(dir, "LiveFixture.asm");
        foreach (var f in new[]{ pa, pb, asmf, asmf + ".cfg" }) if (File.Exists(f)) File.Delete(f);

        var da = NewPlate("LiveA", 0.1, 0.1, 0.01);
        DrillHole(da, 0.05, 0.05, 0.0066);
        da.SaveAs(pa); da.Close(false);
        var db = NewPlate("LiveB", 0.1, 0.1, 0.02);
        db.SaveAs(pb); db.Close(false);

        var asm = (A.AssemblyDocument)app.Documents.Add("SolidEdge.AssemblyDocument");
        asm.SaveAs(asmf);
        var occA = asm.Occurrences.AddByFilename(pa);
        Array m1 = new double[]{1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1};
        occA.PutMatrix(ref m1, true);
        // B 板悬在 A 板正上方 60mm：几何上与 A 板的孔同轴（打孔能命中），
        // 同时从等轴测看两块板都不被遮挡，用户能点到 A 板的孔边和 B 板的下表面。
        var occB = asm.Occurrences.AddByFilename(pb);
        Array m2 = new double[]{1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0.06,1};
        occB.PutMatrix(ref m2, true);
        asm.Save();
        try { ((dynamic)app.ActiveWindow).View.Fit(); } catch { }
        Console.WriteLine("FIXTURE OK");
        Console.WriteLine(asmf);
        Console.WriteLine("OCC " + asm.Occurrences.Count);
        return 0;
    }

    static P.PartDocument NewPlate(string name, double w, double h, double t){
        var part = (P.PartDocument)app.Documents.Add("SolidEdge.PartDocument", Template);
        part.ModelingMode = P.ModelingModeConstants.seModelingModeOrdered;
        var prof = part.ProfileSets.Add().Profiles.Add(FindXY(part));
        var L = new S.Line2d[4];
        L[0] = prof.Lines2d.AddBy2Points(0, 0, w, 0); L[1] = prof.Lines2d.AddBy2Points(w, 0, w, h);
        L[2] = prof.Lines2d.AddBy2Points(w, h, 0, h); L[3] = prof.Lines2d.AddBy2Points(0, h, 0, 0);
        var rel = (S.Relations2d)prof.Relations2d;
        for (int i = 0; i < 4; i++) { rel.AddKeypoint(L[i], (int)SolidEdgeConstants.KeypointIndexConstants.igLineEnd, L[(i+1)%4], (int)SolidEdgeConstants.KeypointIndexConstants.igLineStart); if (i%2==0) rel.AddHorizontal(L[i]); else rel.AddVertical(L[i]); }
        rel.AddKeypointFix(L[0], (int)SolidEdgeConstants.KeypointIndexConstants.igLineStart);
        if (prof.End(P.ProfileValidationType.igProfileClosed) != 0) throw new InvalidOperationException("底板不闭合");
        Array arr = new object[]{ prof };
        part.Models.AddFiniteExtrudedProtrusion(1, ref arr, P.FeaturePropertyConstants.igSymmetric, t);
        return part;
    }

    static void DrillHole(P.PartDocument part, double x, double y, double dia){
        var model = (P.Model)part.Models.Item(1);
        var plane = part.RefPlanes.AddParallelByDistance(FindXY(part), 0.005, P.ReferenceElementConstants.igNormalSide, M, M, M, M);
        var prof = part.ProfileSets.Add().Profiles.Add(plane);
        double x2, y2; prof.Convert3DCoordinate(x, y, 0.005, out x2, out y2);
        prof.Holes2d.Add(x2, y2);
        if (prof.End(P.ProfileValidationType.igProfileClosed) != 0) throw new InvalidOperationException("孔轮廓不闭合");
        var hd = part.HoleDataCollection.Add(P.FeaturePropertyConstants.igRegularHole, dia,
            0.0, 0.0, 0.0, 0.0, 0.0, P.FeaturePropertyConstants.igNone, M, M, M, M, M,
            P.FeaturePropertyConstants.igVBottomDimToFlat, M, M, M, M, M, M, true);
        object desc = null;
        foreach (var side in new[]{ P.FeaturePropertyConstants.igLeft, P.FeaturePropertyConstants.igRight }) {
            P.Hole h = null;
            try { h = model.Holes.AddThroughAll(prof, side, hd); } catch { continue; }
            if (h == null) continue;
            if ((int)h.GetStatusEx(out desc) == (int)P.FeatureStatusConstants.igFeatureOK) return;
            try { h.Delete(); } catch { }
        }
        throw new InvalidOperationException("夹具孔打不出来");
    }

    static P.RefPlane FindXY(P.PartDocument part){
        foreach (P.RefPlane c in part.RefPlanes) {
            Array n = new double[3], p = new double[3], u = new double[3];
            c.GetNormal(ref n); c.GetRootPoint(ref p); c.GetReferenceDirection(ref u);
            if (Math.Abs(Convert.ToDouble(n.GetValue(2))-1) < 1e-8 && Math.Abs(Convert.ToDouble(p.GetValue(0))) < 1e-8
                && Math.Abs(Convert.ToDouble(p.GetValue(1))) < 1e-8 && Math.Abs(Convert.ToDouble(u.GetValue(0))-1) < 1e-8) return c;
        }
        return null;
    }
}
