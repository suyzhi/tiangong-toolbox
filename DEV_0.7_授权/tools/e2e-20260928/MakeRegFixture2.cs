using System;
using System.IO;
using System.Runtime.InteropServices;
using F=SolidEdgeFramework;
using P=SolidEdgePart;
using A=SolidEdgeAssembly;
using S=SolidEdgeFrameworkSupport;

// tools/e2e-20260928/MakeRegFixture2.cs
// B5 regression fixture #2: a *correct* pair -> 100x50x10 plate with a cylindrical
// counterbore (Phi6.6 through + Phi11 x 6.5 deep, i.e. M6 head clearance) stacked on a
// plate with an M6 tapped through hole. The check must not report "wrong spec" and must
// not treat the two circular edges of the counterbore as two separate holes.
class MakeRegFixture2 {
    static object M = Type.Missing;
    const string CadHome = @"C:\Program Files\NDS\TianGong 2025";
    static string Template { get { return Path.Combine(CadHome, "Template", "ISO Metric", "iso metric part.par"); } }
    static F.Application app;
    static string Dir;

    [STAThread] static int Main(string[] args){
        Dir = args.Length > 0 ? Path.GetFullPath(args[0]) : Environment.CurrentDirectory;
        Directory.CreateDirectory(Dir);
        try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); }
        catch { app = (F.Application)Activator.CreateInstance(Type.GetTypeFromProgID("SolidEdge.Application")); }
        app.Visible = true; app.ScreenUpdating = true;

        string pd = Path.Combine(Dir, "HcD.par"), pe = Path.Combine(Dir, "HcE.par");
        foreach (var f in new[]{ pd, pe, Path.Combine(Dir, "Rg2.asm"), Path.Combine(Dir, "Rg2.asm.cfg") }) if (File.Exists(f)) File.Delete(f);

        var dd = NewPlate("HcD", 0.1, 0.05, 0.01);
        DrillCounterbore(dd, 0.05, 0.025);
        dd.SaveAs(pd); dd.Close(false);
        var de = NewPlate("HcE", 0.1, 0.05, 0.01);
        DrillTapped(de, 0.05, 0.025);
        de.SaveAs(pe); de.Close(false);
        Console.WriteLine("PARTS OK");

        var asm = (A.AssemblyDocument)app.Documents.Add("SolidEdge.AssemblyDocument");
        string asmf = Path.Combine(Dir, "Rg2.asm");
        asm.SaveAs(asmf);
        var o1 = asm.Occurrences.AddByFilename(pd);
        Array m1 = new double[]{ 1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1 };
        o1.PutMatrix(ref m1, true);
        var o2 = asm.Occurrences.AddByFilename(pe);
        Array m2 = new double[]{ 1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0.01,1 };
        o2.PutMatrix(ref m2, true);
        asm.Save();
        Console.WriteLine("ASM Rg2 occ=" + asm.Occurrences.Count);
        asm.Close(false);
        Console.WriteLine("REG2 FIXTURE OK");
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
        for (int i = 0; i < 4; i++) { rel.AddKeypoint(L[i], (int)SolidEdgeConstants.KeypointIndexConstants.igLineEnd, L[(i + 1) % 4], (int)SolidEdgeConstants.KeypointIndexConstants.igLineStart); if (i % 2 == 0) rel.AddHorizontal(L[i]); else rel.AddVertical(L[i]); }
        rel.AddKeypointFix(L[0], (int)SolidEdgeConstants.KeypointIndexConstants.igLineStart);
        if (prof.End(P.ProfileValidationType.igProfileClosed) != 0) throw new InvalidOperationException("plate profile not closed");
        Array arr = new object[]{ prof };
        part.Models.AddFiniteExtrudedProtrusion(1, ref arr, P.FeaturePropertyConstants.igSymmetric, t);
        return part;
    }

    static void HoleProfile(P.PartDocument part, double x, double y, out P.Profile prof){
        var plane = part.RefPlanes.AddParallelByDistance(FindXY(part), 0.005, P.ReferenceElementConstants.igNormalSide, M, M, M, M);
        prof = part.ProfileSets.Add().Profiles.Add(plane);
        double x2, y2; prof.Convert3DCoordinate(x, y, 0.005, out x2, out y2);
        prof.Holes2d.Add(x2, y2);
        if (prof.End(P.ProfileValidationType.igProfileClosed) != 0) throw new InvalidOperationException("hole profile not closed");
    }

    static void DrillCounterbore(P.PartDocument part, double x, double y){
        var model = (P.Model)part.Models.Item(1);
        P.Profile prof; HoleProfile(part, x, y, out prof);
        var hd = part.HoleDataCollection.Add(P.FeaturePropertyConstants.igCounterboreHole, 0.0066,
            0.011, 0.0065, M, M, M, M, M, M, M, M, M, M, M, M, M, M, M);
        Drill(part, model, prof, hd, "counterbore");
    }

    static void DrillTapped(P.PartDocument part, double x, double y){
        var model = (P.Model)part.Models.Item(1);
        P.Profile prof; HoleProfile(part, x, y, out prof);
        var hd = part.HoleDataCollection.AddEx(P.FeaturePropertyConstants.igTappedHole,
            "ISO Metric", M, "M6", M,
            M, M, M, M, M,
            M, M, M, M, M, M, M, M, M,
            M, M, M, M, M,
            true,
            M, M, M,
            M, M, M, M, M, M, M, M, M);
        Drill(part, model, prof, hd, "tapped M6");
    }

    static void Drill(P.PartDocument part, P.Model model, P.Profile prof, P.HoleData hd, string what){
        object desc = null;
        foreach (var side in new[]{ P.FeaturePropertyConstants.igLeft, P.FeaturePropertyConstants.igRight }) {
            P.Hole h = null;
            try { h = model.Holes.AddThroughAll(prof, side, hd); } catch { continue; }
            if (h == null) continue;
            if ((int)h.GetStatusEx(out desc) == (int)P.FeatureStatusConstants.igFeatureOK) return;
            try { h.Delete(); } catch { }
        }
        throw new InvalidOperationException(what + " failed");
    }

    static P.RefPlane FindXY(P.PartDocument part){
        foreach (P.RefPlane c in part.RefPlanes) {
            Array n = new double[3], p = new double[3], u = new double[3];
            c.GetNormal(ref n); c.GetRootPoint(ref p); c.GetReferenceDirection(ref u);
            if (Math.Abs(Convert.ToDouble(n.GetValue(2)) - 1) < 1e-8 && Math.Abs(Convert.ToDouble(p.GetValue(0))) < 1e-8
                && Math.Abs(Convert.ToDouble(p.GetValue(1))) < 1e-8 && Math.Abs(Convert.ToDouble(u.GetValue(0)) - 1) < 1e-8) return c;
        }
        return null;
    }
}
