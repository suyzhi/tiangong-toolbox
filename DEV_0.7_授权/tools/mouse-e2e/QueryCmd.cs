using System;
using System.Runtime.InteropServices;
using F=SolidEdgeFramework;
class QueryCmd {
    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        F.Application app;
        try { app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application"); }
        catch (Exception e) { Console.WriteLine("连不上 CAD：" + e.Message); return 2; }
        object act = null; try { act = app.ActiveDocument; } catch {}
        string actName = "?"; try { dynamic d = act; actName = Convert.ToString(d.Name); } catch {}
        Console.WriteLine("ActiveDocument = " + actName + "  (" + (act == null ? "null" : act.GetType().Name) + ")");
        F.AddIn addin = null;
        foreach (F.AddIn a in app.AddIns){ if (a.GUID == "{8C05165C-65A4-4EF2-A138-508589D82004}"){ addin = a; break; } }
        if (addin == null){ Console.WriteLine("没找到插件"); return 3; }
        object diag = addin.Object;
        if (diag == null){ Console.WriteLine("诊断对象为空"); return 4; }
        dynamic d2 = diag;
        for (int id = 1; id <= 10; id++){
            try {
                int flags = d2.QueryCommand(id);
                int native = 0; try { native = d2.NativeCommandId(id); } catch {}
                Console.WriteLine("命令 " + id + "：enabled=" + ((flags & 1) == 0) + " flags=" + flags + " nativeId=" + native);
            } catch (Exception e) { Console.WriteLine("命令 " + id + " 查询失败：" + e.Message); }
        }
        return 0;
    }
}
