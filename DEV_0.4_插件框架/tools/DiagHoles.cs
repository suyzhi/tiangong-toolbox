using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using TianGongCadSuite;
using A=SolidEdgeAssembly;
using F=SolidEdgeFramework;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;

// 诊断：两块板各有几个孔？命令 8 到底读到了什么？
class DiagHoles {
    static void Main(string[] args){
        var app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application");
        var asm = app.ActiveDocument as A.AssemblyDocument;
        Console.WriteLine("DOC " + (asm == null ? "?" : asm.Name));
        foreach (A.Occurrence o in asm.Occurrences) {
            var d = o.OccurrenceDocument as P.PartDocument;
            if (d == null) continue;
            var m = (P.Model)d.Models.Item(1);
            Console.WriteLine("  " + o.Name + "  孔特征 " + m.Holes.Count + "  体积 " + (((G.Body)m.Body).Volume*1e9).ToString("0.#") + " mm3");
            for (int i = 1; i <= m.Holes.Count; i++) {
                P.Hole h = (P.Hole)m.Holes.Item(i);
                Console.WriteLine("     孔#" + i + "  " + h.Name + "  dia=" + Describe(h));
            }
        }
        var warns = new List<string>();
        var holes = AutoHoleWriter.CollectAssemblyHoles(asm, out warns);
        Console.WriteLine("CollectAssemblyHoles 读到 " + holes.Count + " 个孔，警告 " + warns.Count);
        foreach (var h in holes) Console.WriteLine("   " + h.ToString());
        var groups = HoleCheck.GroupByAxis(holes);
        Console.WriteLine("同轴组 " + groups.Count);
        foreach (var g in groups) {
            Console.WriteLine("   组: " + g.Count + " 个孔");
            foreach (var h in g) Console.WriteLine("      " + h.ToString());
        }
    }
    static string Describe(P.Hole h){
        try { var d = h.HoleData as P.HoleData; return d == null ? "?" : (d.HoleDiameter*1000).ToString("0.###"); }
        catch (Exception e) { return "err " + e.Message; }
    }
}
