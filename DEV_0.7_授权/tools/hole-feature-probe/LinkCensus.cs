using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using TianGongCadSuite;
using A=SolidEdgeAssembly;
using P=SolidEdgePart;
using F=SolidEdgeFramework;

// 0.7.11：装配里每个零件的链接情况一览（哪个零件有链接、链接属于哪个装配、零件间复制断开没有、孔特征里有没有失败的）。只读，不保存。
// 用法: LinkCensus.exe <输出目录> <装配路径>
class LinkCensus {
    static F.Application app;
    static void L(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    static T Try<T>(Func<T> f, T d){ try { return f(); } catch { return d; } }
    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        CensusFilter.Register();
        try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); } catch (Exception e) { L("连不上 CAD：" + e.Message); return 2; }
        try { app.DisplayAlerts = false; } catch { }
        A.AssemblyDocument asm = null;
        try {
            asm = (A.AssemblyDocument)app.Documents.Open(args[1]);
            for (int i = 0; i < 20; i++) { try { app.DoIdle(); } catch { } Application.DoEvents(); Thread.Sleep(60); }
            L("装配 " + asm.FullName);
            var parts = AssemblyPart.List(asm, new List<string>());
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var ap in parts) {
                string full = Try(() => ap.Part.FullName, "?");
                if (!seen.Add(full)) continue;
                bool has = false; try { ap.Part.HasInterpartLinks(ref has); } catch { }
                int n = Try(() => ap.Part.Constructions.InterpartConstructions.Count, 0);
                if (!has && n == 0) continue;
                string ctx = ""; try { ap.Part.GetInContextAssemblyNameForInterpartLinks(ref ctx); } catch { }
                int broken = 0, bad = 0; var names = new List<string>();
                for (int i = 1; i <= n; i++) {
                    var c = (P.InterpartConstruction)ap.Part.Constructions.InterpartConstructions.Item(i);
                    bool b = Try(() => c.IsBroken, false); if (b) broken++;
                    string st = Try(() => { object d; return c.GetStatusEx(out d).ToString().Replace("igFeature", ""); }, "?");
                    if (st != "OK") bad++;
                    names.Add(Try(() => c.Name, "?") + (b ? "[断开]" : "") + (st != "OK" ? "[" + st + "]" : ""));
                }
                int holes = Try(() => ((P.Model)ap.Part.Models.Item(1)).Holes.Count, -1);
                int holesBad = 0;
                try { var hs = ((P.Model)ap.Part.Models.Item(1)).Holes; for (int i = 1; i <= hs.Count; i++) { object d; var st = hs.Item(i).GetStatusEx(out d); if ((int)st != (int)P.FeatureStatusConstants.igFeatureOK) holesBad++; } } catch { }
                string mt = Try(() => File.GetLastWriteTime(full).ToString("MM-dd HH:mm"), "?");
                L((has ? "[链接] " : "[无链]  ") + ap.Name + "　文件 " + Path.GetFileName(full) + "（改于 " + mt + "）　零件间复制 " + n + "（断开 " + broken + "、不正常 " + bad + "）　孔 " + holes + "（不正常 " + holesBad + "）"
                  + (ctx.Length > 0 ? "　建链装配 " + Path.GetFileName(ctx) : ""));
                L("        " + string.Join("，", names.ToArray()));
            }
        } catch (Exception e) { L("异常 " + e.Message + "\r\n" + e.StackTrace); }
        finally { try { if (asm != null) asm.Close(false); } catch { } }
        L("CENSUS DONE");
        return 0;
    }
}
[ComImport, Guid("00000016-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface ICensusMessageFilter {
    [PreserveSig] int HandleInComingCall(int type, IntPtr task, int ticks, IntPtr info);
    [PreserveSig] int RetryRejectedCall(IntPtr task, int ticks, int reject);
    [PreserveSig] int MessagePending(IntPtr task, int ticks, int pending);
}
sealed class CensusFilter : ICensusMessageFilter {
    [DllImport("ole32.dll")] static extern int CoRegisterMessageFilter(ICensusMessageFilter f, out ICensusMessageFilter old);
    static CensusFilter keep;
    public static void Register(){ keep = new CensusFilter(); ICensusMessageFilter old; CoRegisterMessageFilter(keep, out old); }
    public int HandleInComingCall(int a, IntPtr b, int c, IntPtr d){ return 0; }
    public int RetryRejectedCall(IntPtr a, int ticks, int reject){ return ticks < 120000 ? 200 : -1; }
    public int MessagePending(IntPtr a, int b, int c){ return 2; }
}
