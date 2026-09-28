using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using F=SolidEdgeFramework;
using P=SolidEdgePart;
using A=SolidEdgeAssembly;
using S=SolidEdgeFrameworkSupport;

// tools/e2e-20260928/MakeHoleCheckFixture.cs
// Builds the fixtures the 2026-09-28 check guide asks for:
//   HcA.par  100x50x10 plate with one Phi5 through hole (M6 tap drill) at its centre
//   HcB.par  100x50x10 plate with one Phi6.6 through hole (M6 clearance)
//   HcC.par  100x50x10 solid plate (no holes)
//   B1..B9    assemblies covering alignment / offset / missing hole / rotated part / same part twice
// ASCII only output on purpose: the log goes through cmd.exe redirection.
class MakeHoleCheckFixture {
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

        string pa = Path.Combine(Dir, "HcA.par"), pb = Path.Combine(Dir, "HcB.par"), pc = Path.Combine(Dir, "HcC.par");
        Clean(pa); Clean(pb); Clean(pc);
        for (int i = 1; i <= 9; i++) { Clean(Path.Combine(Dir, "B" + i + ".asm")); Clean(Path.Combine(Dir, "B" + i + ".asm.cfg")); }

        // plates are symmetric about the sketch plane: z in [-t/2, +t/2]
        var da = NewPlate("HcA", 0.1, 0.05, 0.01); DrillHole(da, 0.05, 0.025, 0.005); da.SaveAs(pa); da.Close(false);
        var db = NewPlate("HcB", 0.1, 0.05, 0.01); DrillHole(db, 0.05, 0.025, 0.0066); db.SaveAs(pb); db.Close(false);
        var dc = NewPlate("HcC", 0.1, 0.05, 0.01); dc.SaveAs(pc); dc.Close(false);
        Console.WriteLine("PARTS OK");

        double[] I = Id();
        // B1: A + B stacked, holes coaxial
        Asm("B1", new string[]{ pa, pb }, new double[][]{ I, Tr(0, 0, 0.01) });
        // B2: B shifted +0.5mm in X
        Asm("B2", new string[]{ pa, pb }, new double[][]{ I, Tr(0.0005, 0, 0.01) });
        // B3: B shifted +1mm
        Asm("B3", new string[]{ pa, pb }, new double[][]{ I, Tr(0.001, 0, 0.01) });
        // B4: B shifted +3mm  (beyond the Phi5 radius: becomes a missing hole on B's hole)
        Asm("B4", new string[]{ pa, pb }, new double[][]{ I, Tr(0.003, 0, 0.01) });
        // B5: A + solid C rotated 90deg about Z and moved so it still covers A's hole
        Asm("B5", new string[]{ pa, pc }, new double[][]{ I, RotZ90(0.075, -0.025, 0.01) });
        // B6: same but C moved far away
        Asm("B6", new string[]{ pa, pc }, new double[][]{ I, RotZ90(0.3, 0.3, 0.01) });
        // B7: A + solid C rotated 90deg about X, standing up across A's hole axis
        Asm("B7", new string[]{ pa, pc }, new double[][]{ I, RotX90(0, 0.025, 0.01) });
        // B8: A + two instances of the SAME file: first covers, second far away
        Asm("B8", new string[]{ pa, pc, pc }, new double[][]{ I, RotZ90(0.075, -0.025, 0.01), Tr(0.4, 0.4, 0.01) });
        // B9: same, but the SECOND instance covers and the first is far away
        Asm("B9", new string[]{ pa, pc, pc }, new double[][]{ I, Tr(0.4, 0.4, 0.01), RotZ90(0.075, -0.025, 0.01) });
        Console.WriteLine("FIXTURE OK");
        Console.WriteLine(Dir);
        return 0;
    }

    static void Clean(string f){ if (File.Exists(f)) File.Delete(f); }
    static double[] Id(){ return new double[]{ 1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1 }; }
    static double[] Tr(double x, double y, double z){ return new double[]{ 1,0,0,0, 0,1,0,0, 0,0,1,0, x,y,z,1 }; }
    // rows: (0,1,0) (-1,0,0) (0,0,1) -> (x,y,z) => (-y, x, z)
    static double[] RotZ90(double x, double y, double z){ return new double[]{ 0,1,0,0, -1,0,0,0, 0,0,1,0, x,y,z,1 }; }
    // rows: (1,0,0) (0,0,1) (0,-1,0) -> (x,y,z) => (x, -z, y)
    static double[] RotX90(double x, double y, double z){ return new double[]{ 1,0,0,0, 0,0,1,0, 0,-1,0,0, x,y,z,1 }; }

    static void Asm(string name, string[] files, double[][] mats){
        var doc = (A.AssemblyDocument)app.Documents.Add("SolidEdge.AssemblyDocument");
        string path = Path.Combine(Dir, name + ".asm");
        doc.SaveAs(path);
        for (int i = 0; i < files.Length; i++) {
            var occ = doc.Occurrences.AddByFilename(files[i]);
            Array m = mats[i];
            occ.PutMatrix(ref m, true);
        }
        // relationship-free placement: keep the raw matrices, do not let CAD auto-ground them
        doc.Save();
        Console.WriteLine("ASM " + name + " occ=" + doc.Occurrences.Count);
        doc.Close(false);
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

    static void DrillHole(P.PartDocument part, double x, double y, double dia){
        var model = (P.Model)part.Models.Item(1);
        var plane = part.RefPlanes.AddParallelByDistance(FindXY(part), 0.005, P.ReferenceElementConstants.igNormalSide, M, M, M, M);
        var prof = part.ProfileSets.Add().Profiles.Add(plane);
        double x2, y2; prof.Convert3DCoordinate(x, y, 0.005, out x2, out y2);
        prof.Holes2d.Add(x2, y2);
        if (prof.End(P.ProfileValidationType.igProfileClosed) != 0) throw new InvalidOperationException("hole profile not closed");
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
        throw new InvalidOperationException("hole failed");
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
