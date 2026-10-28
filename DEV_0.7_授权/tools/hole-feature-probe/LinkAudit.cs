using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using A=SolidEdgeAssembly;
using P=SolidEdgePart;
using F=SolidEdgeFramework;

// 0.10.2：只读——列出装配里每个带零件间链接的零件：建链装配是谁、文件在不在、零件间复制各是什么状态。
// 批量改名前后各跑一次比一比：改名后链接还找得到源（建链装配改成新名字、状态还是 OK）。
// 用法: LinkAudit.exe <输出目录> <装配>
class LinkAudit {
    static F.Application app;
    static void L(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    static T Try<T>(Func<T> f, T d){ try { return f(); } catch { return d; } }
    static void Pump(int n){ for (int i = 0; i < n; i++) { try { app.DoIdle(); } catch { } Application.DoEvents(); Thread.Sleep(50); } }
    static readonly HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    static int parts, linked, copies, bad, ctxMissing;

    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); } catch (Exception e) { L("连不上 CAD：" + e.Message); return 2; }
        try { app.DisplayAlerts = false; } catch { }
        A.AssemblyDocument asm = null;
        try {
            asm = (A.AssemblyDocument)app.Documents.Open(args[1]); Pump(20);
            Walk(asm.Occurrences, 0);
            L("RESULT 零件 " + parts + "，带零件间链接 " + linked + "，零件间复制 " + copies + "，状态不正常 " + bad + "，建链装配文件不在 " + ctxMissing);
        } catch (Exception e) { L("异常：" + e); return 1; }
        finally { try { if (asm != null) asm.Close(false); } catch { } Pump(4); }
        return 0;
    }

    static void Walk(A.Occurrences occs, int depth){
        if (occs == null || depth > 12) return;
        int n = Try(() => occs.Count, 0);
        for (int i = 1; i <= n; i++) {
            var o = Try(() => (A.Occurrence)occs.Item(i), null);
            if (o == null) continue;
            object doc = Try<object>(() => o.OccurrenceDocument, null);
            var sub = doc as A.AssemblyDocument;
            if (sub != null) { Walk(Try(() => sub.Occurrences, null), depth + 1); continue; }
            string file = Try(() => ((dynamic)doc).FullName as string, "") ?? "";
            if (file.Length == 0 || !seen.Add(file)) continue;
            parts++;
            Audit(doc, file);
        }
    }

    static void Audit(object doc, string file){
        bool has = false; string ctx = "";
        var part = doc as P.PartDocument;
        var sheet = doc as P.SheetMetalDocument;
        try {
            if (part != null) { part.HasInterpartLinks(ref has); if (has) part.GetInContextAssemblyNameForInterpartLinks(ref ctx); }
            else if (sheet != null) { sheet.HasInterpartLinks(ref has); if (has) sheet.GetInContextAssemblyNameForInterpartLinks(ref ctx); }
        } catch (Exception e) { L("   " + Path.GetFileName(file) + " 读链接失败：" + e.Message); return; }
        if (!has) return;
        linked++;
        ctx = (ctx ?? "").Trim();
        bool ctxOk = ctx.Length > 0 && File.Exists(ctx);
        if (!ctxOk) ctxMissing++;
        P.InterpartConstructions all = null;
        try { all = part != null ? part.Constructions.InterpartConstructions : sheet.Constructions.InterpartConstructions; } catch { }
        int k = Try(() => all.Count, 0), notOk = 0;
        var states = new List<string>();
        for (int i = 1; i <= k; i++) {
            try {
                var c = (P.InterpartConstruction)all.Item(i);
                object d; var st = c.get_Status(out d);
                if (st != P.FeatureStatusConstants.igFeatureOK) { notOk++; states.Add(Try(() => c.Name, "?") + "=" + st); }
            } catch (Exception e) { notOk++; states.Add("读不出：" + e.Message); }
        }
        copies += k; bad += notOk;
        L("LINK " + Path.GetFileName(file) + "　建链装配=" + Path.GetFileName(ctx) + (ctxOk ? "" : "（文件不在！）") + "　零件间复制 " + k + "，不正常 " + notOk
          + (states.Count > 0 ? "：" + string.Join("，", states.Take(6).ToArray()) : ""));
    }
}
