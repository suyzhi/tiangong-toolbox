using System;
using System.Runtime.InteropServices;
using A=SolidEdgeAssembly;
using P=SolidEdgePart;
using F=SolidEdgeFramework;
class ProbeMode2 {
    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        F.Application app;
        try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); }
        catch (Exception e) { Console.WriteLine("连不上 CAD：" + e.Message); return 2; }
        for (int i = 1; i <= app.Documents.Count; i++){
            var asm = app.Documents.Item(i) as A.AssemblyDocument;
            if (asm == null) continue;
            string an = ""; try { an = asm.FullName; } catch { }
            Console.WriteLine("ASM " + an);
            foreach (A.Occurrence occ in asm.Occurrences){
                var pd = occ.OccurrenceDocument as P.PartDocument;
                if (pd == null) continue;
                string nm = ""; try { nm = occ.Name; } catch { }
                string full = ""; try { full = pd.FullName; } catch { }
                string modeText; try {
                    object mm = pd.GetType().InvokeMember("ModelingMode", System.Reflection.BindingFlags.GetProperty, null, pd, null);
                    modeText = Convert.ToInt32(mm) + " / " + mm;
                } catch (Exception e) { modeText = "读不到(" + e.Message + ")"; }
                string holes = ""; try {
                    var mdl = (P.Model)pd.Models.Item(1);
                    holes = "Holes=" + mdl.Holes.Count;
                } catch (Exception e) { holes = "读孔失败(" + e.Message + ")"; }
                string ro = ""; try { ro = "ReadOnly=" + pd.ReadOnly; } catch { }
                Console.WriteLine("  PART " + nm + "  " + full);
                Console.WriteLine("       ModelingMode=" + modeText + "   " + holes + "   " + ro);
            }
        }
        for (int i = 1; i <= app.Documents.Count; i++){
            var pd = app.Documents.Item(i) as P.PartDocument;
            if (pd == null) continue;
            string nm = ""; try { nm = pd.Name; } catch { }
            string modeText; try {
                object mm = pd.GetType().InvokeMember("ModelingMode", System.Reflection.BindingFlags.GetProperty, null, pd, null);
                modeText = Convert.ToInt32(mm) + " / " + mm;
            } catch (Exception e) { modeText = "读不到"; }
            Console.WriteLine("TOP-LEVEL PART " + nm + "  ModelingMode=" + modeText);
        }
        Console.WriteLine("PROBE DONE");
        return 0;
    }
}
