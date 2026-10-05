using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using F=SolidEdgeFramework;

// 收尾：把本次排查在用户 CAD 里打开的**副本**文档全部关掉（不保存），只留用户自己的文档。
// 用法: Cleanup.exe <必须包含的路径子串> [--list]
class Cleanup {
    static void L(string s){ Console.WriteLine(s); Console.Out.Flush(); }
    [STAThread] static int Main(string[] args){
        Console.OutputEncoding=System.Text.Encoding.UTF8;
        string root = args.Length>0 ? args[0] : null;
        bool list = Array.IndexOf(args, "--list") >= 0;
        F.Application app;
        try{ app=(F.Application)Marshal.GetActiveObject("SolidEdge.Application"); }
        catch(Exception e){ L("连不上 CAD："+e.Message); return 2; }
        if(list){
            for(int i=1;i<=app.Documents.Count;i++){
                F.SolidEdgeDocument d=null; try{ d=app.Documents.Item(i) as F.SolidEdgeDocument; }catch{ continue; }
                string fn="?"; try{ fn=d==null?"<null>":d.FullName; }catch{}
                L("  文档["+i+"] "+fn);
            }
            return 0;
        }
        int closed=0;
        for(int round=0; round<6; round++){
            var victims=new List<F.SolidEdgeDocument>(); var names=new List<string>();
            for(int i=1;i<=app.Documents.Count;i++){
                F.SolidEdgeDocument d=null; try{ d=app.Documents.Item(i) as F.SolidEdgeDocument; }catch{ continue; }
                if(d==null) continue;
                string fn=null; try{ fn=d.FullName; }catch{}
                if(fn!=null && (root==null || fn.StartsWith(root, StringComparison.OrdinalIgnoreCase))){ victims.Add(d); names.Add(fn); }
            }
            if(victims.Count==0) break;
            foreach(var v in victims){ try{ v.Close(false); closed++; }catch(Exception e){ L("  关闭失败："+e.Message); } }
            L("  本轮关闭 "+victims.Count+" 个："+string.Join(" | ", names.ToArray()));
            try{ app.DoIdle(); }catch{}
            Thread.Sleep(400);
        }
        L("共关闭 "+closed+" 个副本文档");
        L("剩下的文档：");
        for(int i=1;i<=app.Documents.Count;i++){
            F.SolidEdgeDocument d=null; try{ d=app.Documents.Item(i) as F.SolidEdgeDocument; }catch{ continue; }
            string fn="?"; try{ fn=d.FullName; }catch{}
            L("  · "+fn);
        }
        return 0;
    }
}
