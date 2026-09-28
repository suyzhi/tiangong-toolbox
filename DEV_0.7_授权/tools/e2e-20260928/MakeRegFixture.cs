using System;
using System.IO;
using System.Runtime.InteropServices;
using F=SolidEdgeFramework;
using A=SolidEdgeAssembly;

// tools/e2e-20260928/MakeRegFixture.cs
// B5 regression fixture: a counterbored plate (Phi5.5 through + Phi11 c'bore) stacked
// coaxially on a tapped M6 plate. The check must NOT report "wrong spec" for it
// (the two circular edges of a cylindrical counterbore must not be read as two holes).
class MakeRegFixture {
    [STAThread] static int Main(string[] args){
        string dir = args.Length > 0 ? Path.GetFullPath(args[0]) : Environment.CurrentDirectory;
        string srcA = args.Length > 1 ? args[1] : null;
        string srcB = args.Length > 2 ? args[2] : null;
        Directory.CreateDirectory(dir);
        F.Application app;
        try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); }
        catch { app = (F.Application)Activator.CreateInstance(Type.GetTypeFromProgID("SolidEdge.Application")); }
        app.Visible = true; app.ScreenUpdating = true;

        string pa = Path.Combine(dir, "RgCbore.par");
        string pb = Path.Combine(dir, "RgTap.par");
        File.Copy(srcA, pa, true);
        File.Copy(srcB, pb, true);
        string asmf = Path.Combine(dir, "Rg1.asm");
        if (File.Exists(asmf)) File.Delete(asmf);
        if (File.Exists(asmf + ".cfg")) File.Delete(asmf + ".cfg");

        var asm = (A.AssemblyDocument)app.Documents.Add("SolidEdge.AssemblyDocument");
        asm.SaveAs(asmf);
        var occ1 = asm.Occurrences.AddByFilename(pa);
        Array m1 = new double[]{ 1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1 };
        occ1.PutMatrix(ref m1, true);
        var occ2 = asm.Occurrences.AddByFilename(pb);
        Array m2 = new double[]{ 1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0.03,1 };
        occ2.PutMatrix(ref m2, true);
        asm.Save();
        Console.WriteLine("REG FIXTURE OK occ=" + asm.Occurrences.Count);
        foreach (A.Occurrence o in asm.Occurrences) Console.WriteLine("  " + o.Name);
        asm.Close(false);
        return 0;
    }
}
