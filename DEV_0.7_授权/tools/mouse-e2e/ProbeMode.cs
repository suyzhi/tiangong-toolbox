using System;
using System.Runtime.InteropServices;
using P=SolidEdgePart;
using F=SolidEdgeFramework;
class ProbeMode {
    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        F.Application app;
        try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); }
        catch (Exception e) { Console.WriteLine("连不上 CAD：" + e.Message); return 2; }
        for (int i = 1; i <= app.Documents.Count; i++){
            var d = app.Documents.Item(i);
            var pd = d as P.PartDocument;
            if (pd == null) continue;
            string name = ""; try { name = pd.Name; } catch { }
            string mode = "?"; string modeName = "?";
            try {
                object mm = pd.GetType().InvokeMember("ModelingMode", System.Reflection.BindingFlags.GetProperty, null, pd, null);
                mode = Convert.ToString(Convert.ToInt32(mm));
                modeName = Convert.ToString(mm);
            } catch (Exception e) { mode = "读不到：" + e.Message; }
            int models = -1; try { models = pd.Models.Count; } catch { }
            string bodyInfo = "";
            try {
                var mdl = (P.Model)pd.Models.Item(1);
                bodyInfo = "Volume=" + ((SolidEdgeGeometry.Body)mdl.Body).Volume.ToString("0.###");
            } catch (Exception e) { bodyInfo = "读体失败：" + e.Message; }
            Console.WriteLine("DOC " + name + "  ModelingMode=" + modeName + "(" + mode + ")  Models=" + models + "  " + bodyInfo);
        }
        Console.WriteLine("PROBE DONE");
        return 0;
    }
}
