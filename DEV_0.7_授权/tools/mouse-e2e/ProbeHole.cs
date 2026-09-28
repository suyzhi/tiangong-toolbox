using System;
using System.Runtime.InteropServices;
using System.Threading;
using A=SolidEdgeAssembly;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;
using F=SolidEdgeFramework;

// 判别实验 8（重写）：绕过插件，直接用 CAD 原生 API 建孔。
//   用法: ProbeHole.exe asm <装配路径>    -> 对装配里最后一个实例打孔
//         ProbeHole.exe part <零件路径>   -> 对顶层零件打孔（对照）
class ProbeHole {
    static void L(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    static object M = Type.Missing;
    static F.Application app;
    static void Pump(int n){ for (int i = 0; i < n; i++){ try { app.DoIdle(); } catch {} Thread.Sleep(250); } }

    static bool RawDrill(P.PartDocument part, string label){
        var model = (P.Model)part.Models.Item(1);
        P.RefPlane plane = null;
        try { plane = part.RefPlanes.AddParallelByDistance(part.RefPlanes.Item(1), 0.010, P.ReferenceElementConstants.igNormalSide, M, M, M, M); L("  [" + label + "] 建基准面 OK"); }
        catch (Exception e) { L("  [" + label + "] 建基准面失败：" + e.Message); return false; }
        P.Profile prof = null;
        try {
            prof = part.ProfileSets.Add().Profiles.Add(plane);
            prof.Holes2d.Add(0.05, 0.05);
            int rc = prof.End(P.ProfileValidationType.igProfileClosed);
            L("  [" + label + "] 建轮廓 OK rc=" + rc);
        } catch (Exception e) { L("  [" + label + "] 建轮廓失败：" + e.Message); return false; }
        var none = P.FeaturePropertyConstants.igNone;
        var vd = P.FeaturePropertyConstants.igVBottomDimToFlat;
        P.HoleData data = null;
        try {
            data = part.HoleDataCollection.AddEx(P.FeaturePropertyConstants.igRegularHole, "ISO Metric", M, M, M, 0.006,
                0.0, 0.0, 0.0, 0.0, 0.0, none, M, M, M, M, M, vd, M, M, M, M, M, true, M, M, M, M, M, M, M, M, M, M, M, M);
            L("  [" + label + "] 建 HoleData OK");
        } catch (Exception e) { L("  [" + label + "] 建 HoleData 失败：" + e.Message); return false; }
        foreach (string sn in new string[]{ "igLeft", "igRight" }){
            var side = sn == "igLeft" ? P.FeaturePropertyConstants.igLeft : P.FeaturePropertyConstants.igRight;
            try {
                P.Hole h = model.Holes.AddThroughAll(prof, side, data);
                if (h == null) { L("  [" + label + "] " + sn + " 返回 null"); continue; }
                object desc = null;
                var st = h.GetStatusEx(out desc);
                if (Convert.ToInt32(st) == Convert.ToInt32(P.FeatureStatusConstants.igFeatureOK)){ L("  [" + label + "] " + sn + " 成功：原生 API 建出孔了"); return true; }
                L("  [" + label + "] " + sn + " 状态=" + st + (desc == null ? "" : " " + desc));
                try { h.Delete(); } catch {}
            } catch (Exception e) { L("  [" + label + "] " + sn + " 抛异常：" + e.GetType().Name + " -> " + e.Message); }
        }
        return false;
    }

    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); }
        catch (Exception e) { L("FATAL 连不上 CAD：" + e.Message); return 2; }
        try { app.Visible = true; app.ScreenUpdating = true; } catch { }
        bool okAsm = false, okPart = false;

        try {
            var asm = (A.AssemblyDocument)app.Documents.Open(args[1]);
            L("装配（打开）" + asm.FullName);
            Pump(8);
            A.Occurrence tgt = null;
            foreach (A.Occurrence occ in asm.Occurrences) tgt = occ;
            var part = (P.PartDocument)tgt.OccurrenceDocument;
            L("对实例零件打孔：" + tgt.Name);
            okAsm = RawDrill(part, "实例零件");
        } catch (Exception e) { L("装配用例异常：" + e.Message); }

        try {
            var pd = (P.PartDocument)app.Documents.Open(args[2]);
            L("顶层零件（打开）" + pd.Name);
            Pump(6);
            okPart = RawDrill(pd, "顶层零件");
        } catch (Exception e) { L("零件用例异常：" + e.Message); }

        L("PROBE-HOLE 实例零件=" + okAsm + "  顶层零件=" + okPart);
        return (okAsm && okPart) ? 0 : ((!okAsm && okPart) ? 7 : 1);
    }
}
